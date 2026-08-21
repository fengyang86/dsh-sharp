using System.Diagnostics;

namespace DSHSharp.Core.Dsh;

/// <summary>服务托管模式。</summary>
public enum ManagedMode
{
    /// <summary>纯探测，不托管任何服务进程。</summary>
    None,

    /// <summary>离线时托管 <c>npx @deepseek-ai/dsh</c>（普通用户默认）。</summary>
    Npx,

    /// <summary>离线时在源码路径下托管 <c>pnpm dsh web</c>（源码部署）。</summary>
    Source,
}

/// <summary>
/// DSH 服务进程托管：
/// <list type="bullet">
/// <item>探测服务在线状态（与 DshEventMonitor 的心跳互补，供启动前/就绪轮询使用）；</item>
/// <item>按托管模式拉起服务进程（npx 或源码 pnpm），等待端口就绪；</item>
/// <item>所有权语义：只有本管理器拉起的进程才会被 Stop/Dispose 终止；</item>
/// <item>进程输出重定向到日志文件，意外退出触发事件。</item>
/// </list>
/// 线程安全：StartAsync 内部串行化；事件在后台线程触发，调用方负责调度。
/// </summary>
public sealed class DshServiceManager : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NpxReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SourceReadyTimeout = TimeSpan.FromSeconds(180); // tsx 冷启动实测 90-150s

    private readonly Uri _baseUri;
    private readonly ManagedMode _mode;
    private readonly string? _sourcePath;
    private readonly string _logPath;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private bool _useDirectNode; // 源码模式：pnpm 不可用时用 node + tsx 直接启动
    private bool _disposed;

    public DshServiceManager(string baseUrl, ManagedMode mode, string? sourcePath, string? logDirectory = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _mode = mode;
        _sourcePath = sourcePath;
        var dir = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHSharp");
        _logPath = Path.Combine(dir, "dsh-service.log");
    }

    /// <summary>可选的日志回调（由宿主注入）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>托管模式。</summary>
    public ManagedMode Mode => _mode;

    /// <summary>是否持有托管进程（仅本客户端拉起的服务）。</summary>
    public bool IsOwned => _process is { HasExited: false };

    /// <summary>最近一次启动失败原因（成功启动后清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>托管进程意外退出时触发（服务崩溃）。</summary>
    public event EventHandler? ProcessExitedUnexpectedly;

    /// <summary>常见 DSH/开发服务端口（用于配置端口错误时的纠错扫描）。</summary>
    private static readonly int[] CommonPorts = [3080, 3000, 8080, 8000];

    /// <summary>
    /// 探测服务是否在线（收到任意 HTTP 响应即视为在线）。本地服务不走系统代理。
    /// </summary>
    public async Task<bool> IsOnlineAsync(CancellationToken ct = default)
        => await ProbeAsync(_baseUri, ct);

    /// <summary>
    /// 扫描常见端口，返回第一个响应 DSH/HTTP 服务的端口；
    /// 未发现返回 null。用于配置地址端口错误时的纠错提示。
    /// </summary>
    public async Task<int?> ScanCommonPortsAsync(CancellationToken ct = default)
    {
        foreach (var port in CommonPorts)
        {
            if (port == _baseUri.Port)
            {
                continue;
            }

            var builder = new UriBuilder(_baseUri) { Port = port };
            if (await ProbeAsync(builder.Uri, ct))
            {
                return port;
            }
        }

        return null;
    }

    private static async Task<bool> ProbeAsync(Uri uri, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = ProbeTimeout,
            };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Log?.Invoke($"probe failed: {ex.GetType().Name}: {ex.Message}; inner={ex.InnerException}");
            return false;
        }
    }

    /// <summary>
    /// 确保服务可用：已在运行直接返回 true；服务在线返回 true（不重复托管）；
    /// 否则按托管模式启动并等待就绪。失败时返回 false 并记录 LastError。
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            if (_disposed)
            {
                return false;
            }

            if (_process is { HasExited: false })
            {
                return true;
            }

            if (await IsOnlineAsync(ct))
            {
                Log?.Invoke("service already online, skip managed start");
                LastError = null;
                return true;
            }

            if (_mode == ManagedMode.None)
            {
                LastError = "服务未托管（托管模式为 None）";
                return false;
            }

            if (_mode == ManagedMode.Source)
            {
                var issue = await ValidateSourceEnvironmentAsync(ct);
                if (issue is not null)
                {
                    LastError = issue;
                    return false;
                }
            }            LastError = null;
            var psi = BuildStartInfo();
            Log?.Invoke($"starting managed service: {psi.FileName} {psi.Arguments} (workdir={psi.WorkingDirectory})");
            AppendLog($"--- DSH service managed start ({_mode}) ---");

            var process = Process.Start(psi);
            if (process is null)
            {
                LastError = "进程启动失败";
                return false;
            }

            _process = process;
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
            PumpOutput(process);

            var timeout = _mode == ManagedMode.Npx ? NpxReadyTimeout : SourceReadyTimeout;
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                if (process.HasExited)
                {
                    break;
                }

                if (await IsOnlineAsync(ct))
                {
                    Log?.Invoke("managed service ready");
                    return true;
                }
            }

            if (process.HasExited)
            {
                // 进程刚退出时日志可能仍在写入：短暂等待后再读取尾部。
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                LastError = WithLogTail($"进程提前退出（退出码 {process.ExitCode}），请检查仓库依赖/构建是否就绪");
            }
            else
            {
                LastError = WithLogTail($"服务在 {(int)timeout.TotalSeconds}s 内未就绪（首次启动较慢，或端口/配置有问题）");
            }

            Log?.Invoke($"managed start failed: {LastError}");
            KillOwnedProcess();
            return false;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>停止本客户端拉起的服务进程（外部服务不受影响）。</summary>
    public void Stop()
    {
        Log?.Invoke("stopping managed service (owned)");
        KillOwnedProcess();
    }

    /// <summary>释放：仅终止本客户端拉起的服务进程。</summary>
    public void Dispose()
    {
        _disposed = true;
        KillOwnedProcess();
        _startLock.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        Log?.Invoke($"managed process exited, code={process.ExitCode}");
        AppendLog($"--- DSH service process exited, code={process.ExitCode} ---");
        ProcessExitedUnexpectedly?.Invoke(this, EventArgs.Empty);
    }

    private void KillOwnedProcess()
    {
        var process = _process;
        _process = null;
        if (process is null || process.HasExited)
        {
            process?.Dispose();
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log?.Invoke($"kill managed process failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        // 托管启动的服务端口与客户端探测端口保持一致，确保就绪检测有效。
        var portArg = _baseUri.IsDefaultPort ? "" : $" --port {_baseUri.Port}";

        if (_mode == ManagedMode.Source)
        {
            // pnpm 可用时用标准方式；否则 node + tsx 直接运行源码 cli。
            if (_useDirectNode)
            {
                return new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"--import tsx/esm apps/cli/src/bin.ts web --no-open{portArg}",
                    WorkingDirectory = _sourcePath!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
            }

            return new ProcessStartInfo
            {
                FileName = "pnpm.cmd",
                Arguments = $"dsh web --no-open{portArg}",
                WorkingDirectory = _sourcePath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c npx --yes @deepseek-ai/dsh@latest web --no-open{portArg}",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }

    /// <summary>
    /// 源码模式启动前环境预检：路径、package.json、依赖（node_modules）、pnpm 可用性。
    /// 返回问题描述；全部通过返回 null。
    /// </summary>
    private async Task<string?> ValidateSourceEnvironmentAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath))
        {
            return "未配置源码路径（设置 SourcePath）";
        }

        if (!Directory.Exists(_sourcePath))
        {
            return $"源码路径不存在：{_sourcePath}";
        }

        if (!File.Exists(Path.Combine(_sourcePath, "package.json")))
        {
            return $"路径下未找到 package.json（不是 DSH 仓库？）：{_sourcePath}";
        }

        if (!Directory.Exists(Path.Combine(_sourcePath, "node_modules")))
        {
            return $"仓库依赖未安装：请在 {_sourcePath} 下执行 pnpm install 后再试";
        }

        // 启动方式：优先 pnpm；不可用时降级为 node + tsx 直接运行（与部分源码部署环境一致）。
        _useDirectNode = false;
        if (await IsCommandAvailableAsync("pnpm.cmd", "--version", ct))
        {
            Log?.Invoke("source mode: pnpm available");
            return null;
        }

        if (await IsCommandAvailableAsync("node", "--version", ct) &&
            File.Exists(Path.Combine(_sourcePath, "apps", "cli", "src", "bin.ts")))
        {
            _useDirectNode = true;
            Log?.Invoke("source mode: pnpm missing, falling back to node + tsx");
            return null;
        }

        return "未找到 pnpm 或 node：请安装 pnpm（npm install -g pnpm）后重试";
    }

    /// <summary>检测命令是否可用（执行 &lt;cmd&gt; --version，1.5s 超时）。</summary>
    private static async Task<bool> IsCommandAvailableAsync(string fileName, string args, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>失败诊断：附带服务日志尾部，帮助用户定位（依赖/构建/配置等问题）。</summary>
    private string WithLogTail(string message)
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return message;
            }

            var tail = File.ReadLines(_logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .TakeLast(6)
                .Select(l => l.Length > 180 ? l[..180] + "…" : l)
                .ToArray();
            return tail.Length > 0
                ? $"{message}\n最近日志：\n{string.Join("\n", tail)}"
                : message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志读取失败不影响诊断主体。
            return message;
        }
    }

    private void PumpOutput(Process process)
    {
        if (process.StandardOutput.BaseStream.CanRead)
        {
            _ = Task.Run(() => PumpAsync(process.StandardOutput));
        }

        if (process.StandardError.BaseStream.CanRead)
        {
            _ = Task.Run(() => PumpAsync(process.StandardError));
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                AppendLog(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 进程退出后管道关闭：正常。
        }
    }

    private void AppendLog(string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响服务。
        }
    }
}
