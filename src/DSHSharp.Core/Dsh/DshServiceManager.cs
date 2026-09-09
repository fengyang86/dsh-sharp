using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DSHSharp.Core.Compatibility;

namespace DSHSharp.Core.Dsh;

/// <summary>服务托管模式。</summary>
public enum ManagedMode
{
    /// <summary>纯探测，不托管任何服务进程。</summary>
    None,

    /// <summary>离线时托管私有目录中安装的官方 npm 包（普通用户默认）。</summary>
    Npx,

    /// <summary>离线时在源码路径下托管 <c>pnpm dsh web</c>（源码部署）。</summary>
    Source,
}

/// <summary>
/// DSH 服务进程托管：
/// <list type="bullet">
/// <item>探测服务在线状态（与 DshEventMonitor 的心跳互补，供启动前/就绪轮询使用）；</item>
/// <item>按托管模式拉起服务进程（私有 npm 包或源码 pnpm），等待端口就绪；</item>
/// <item>所有权语义：只有本管理器拉起的进程才会被 Stop/Dispose 终止；</item>
/// <item>进程输出重定向到日志文件，意外退出触发事件。</item>
/// </list>
/// 线程安全：StartAsync 内部串行化；事件在后台线程触发，调用方负责调度。
/// </summary>
public sealed class DshServiceManager : IDisposable
{
    public sealed record ProfilePlugin(string Name, string? Version, bool IsBundled, bool IsActive, string? Description);
    private sealed record RuntimeUpdateTransaction(string Phase, string? TargetVersion, DateTimeOffset StartedUtc);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NpxReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SourceReadyTimeout = TimeSpan.FromSeconds(180); // tsx 冷启动实测 90-150s
    private const string ManagedPackageName = "@deepseek-ai/dsh";
    // pnpm 11.25 修复了安装时错误改写 allowBuilds 的问题。
    private const string ManagedPnpmSpec = "pnpm@11.25.0";

    private readonly Uri _baseUri;
    private Uri _activeBaseUri;
    private readonly ManagedMode _mode;
    private readonly string? _sourcePath;
    private readonly string _logPath;
    private readonly string _packageDirectory;
    private readonly string _runtimeStagingDirectory;
    private readonly string _runtimePreviousDirectory;
    private readonly string _runtimeTransactionPath;
    private readonly string _packageToolsDirectory;
    private readonly string _dshHomeDirectory;
    private readonly string _runtimePidPath;
    private string? _runtimeBackupDirectory;
    private readonly string _bundledShortcutPluginDirectory;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _profileEditLock = new(1, 1);
    private Process? _process;
    private bool _intentionalStop;
    private bool _disposed;
    private TaskCompletionSource<Uri>? _readyUrlSource;

    public DshServiceManager(string baseUrl, ManagedMode mode, string? sourcePath, string? logDirectory = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _activeBaseUri = _baseUri;
        _mode = mode;
        _sourcePath = sourcePath;
        var dir = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHSharp");
        _logPath = Path.Combine(dir, "dsh-service.log");
        _packageDirectory = Path.Combine(dir, "dsh-runtime");
        _runtimeStagingDirectory = Path.Combine(dir, "dsh-runtime-staging");
        _runtimePreviousDirectory = Path.Combine(dir, "dsh-runtime-previous");
        _runtimeTransactionPath = Path.Combine(dir, "dsh-runtime-transaction.json");
        _packageToolsDirectory = Path.Combine(_packageDirectory, ".tools");
        _dshHomeDirectory = Path.Combine(dir, "dsh-home");
        _runtimePidPath = Path.Combine(dir, "dsh-runtime.pid");
        _bundledShortcutPluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins", "dsh-sharp-session");
    }

    /// <summary>可选的日志回调（由宿主注入）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>托管模式。</summary>
    public ManagedMode Mode => _mode;

    /// <summary>本次运行实际连接的地址。端口由托管进程成功绑定后确定。</summary>
    public string ActiveBaseUrl => _activeBaseUri.AbsoluteUri.TrimEnd('/');

    /// <summary>私有 DSH_HOME。会话、profile、插件和凭据不会与外部 DSH 共用。</summary>
    public string DshHomeDirectory => _dshHomeDirectory;

    /// <summary>最近一次 Runtime 更新留下的可恢复快照目录。</summary>
    public bool CanRollbackRuntime => !string.IsNullOrEmpty(_runtimeBackupDirectory) && Directory.Exists(_runtimeBackupDirectory);

    /// <summary>提交已通过健康检查的 Runtime 更新并清理旧版本。</summary>
    public bool CommitRuntimeUpdate()
    {
        if (!CanRollbackRuntime)
        {
            return false;
        }

        if (!TryDeleteDirectory(_runtimeBackupDirectory!) || Directory.Exists(_runtimeBackupDirectory))
        {
            LastError = WithLogTail("Runtime 更新已验证，但无法清理上一版本目录；将保留回滚状态");
            return false;
        }
        _runtimeBackupDirectory = null;
        ClearRuntimeTransaction();
        Log?.Invoke("private DSH Runtime update committed");
        return true;
    }

    /// <summary>是否持有托管进程（仅本客户端拉起的服务）。</summary>
    public bool IsOwned => _process is { HasExited: false };

    /// <summary>最近一次启动失败原因（成功启动后清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>私有 npm 包当前安装的版本；未安装或元数据损坏时返回 null。</summary>
    public string? InstalledPackageVersion => ReadInstalledPackageVersion();
    public bool NeedsRuntimeUpdate => !string.Equals(InstalledPackageVersion, DshSharpCompatibility.DefaultDshVersion, StringComparison.Ordinal);

    /// <summary>读取当前 web profile 中已安装的插件及激活状态。</summary>
    public IReadOnlyList<ProfilePlugin> ListProfilePlugins()
    {
        var path = GetWebProfileManifestPath();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var deps = root.TryGetProperty("dependencies", out var dependencies)
                ? dependencies.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "")
                : new Dictionary<string, string>();
            var active = root.TryGetProperty("dsh", out var dsh) && dsh.TryGetProperty("profile", out var profile) &&
                         profile.TryGetProperty("bundles", out var bundles)
                ? bundles.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            return deps.Select(x => new ProfilePlugin(
                x.Key,
                ReadPluginVersion(path, x.Key),
                string.Equals(x.Key, "@yangfeng/dsh-sharp-session", StringComparison.Ordinal),
                active.Contains(x.Key),
                null)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log?.Invoke($"plugin inventory failed: {ex.Message}");
            return [];
        }
    }

    public Task<bool> SetPluginActiveAsync(string name, bool active, CancellationToken ct = default)
        => RunSerializedProfilePackageEditAsync(name, active ? "activate" : "deactivate", ct);

    public Task<bool> RemovePluginAsync(string name, CancellationToken ct = default)
        => RunSerializedProfilePackageEditAsync(name, "remove", ct);

    /// <summary>托管进程意外退出时触发（服务崩溃）。</summary>
    public event EventHandler? ProcessExitedUnexpectedly;

    /// <summary>常见 DSH/开发服务端口（用于配置端口错误时的纠错扫描）。</summary>
    private static readonly int[] CommonPorts = [3080, 3000, 8080, 8000];

    /// <summary>
    /// 探测服务是否在线（收到任意 HTTP 响应即视为在线）。本地服务不走系统代理。
    /// </summary>
    public async Task<bool> IsOnlineAsync(CancellationToken ct = default)
        => IsOwned && await ProbeAsync(_activeBaseUri, ct);

    /// <summary>
    /// 扫描常见端口，返回第一个响应 DSH/HTTP 服务的端口；
    /// 未发现返回 null。用于配置地址端口错误时的纠错提示。
    /// </summary>
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
            CleanupOrphanedRuntimeProcess();
            if (_disposed)
            {
                return false;
            }

            if (_process is { HasExited: false })
            {
                return true;
            }

            if (_mode != ManagedMode.Npx)
            {
                LastError = "DSH-Sharp 仅支持私有 DSH Runtime，不连接外部或源码服务";
                return false;
            }

            RecoverRuntimeUpdate();
            Directory.CreateDirectory(_dshHomeDirectory);
            if (!await EnsurePrivatePackageAsync(forceUpdate: false, ct) ||
                !await EnsureBundledPluginsAsync(ct))
            {
                return false;
            }

            LastError = null;
            foreach (var port in CandidatePorts())
            {
                if (!IsPortAvailable(port))
                {
                    Log?.Invoke($"managed port unavailable, trying next: {port}");
                    continue;
                }

                _activeBaseUri = new UriBuilder(_baseUri) { Host = "127.0.0.1", Port = port }.Uri;
                _readyUrlSource = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
                var psi = BuildStartInfo(port);
                Log?.Invoke($"starting private runtime: {psi.FileName} {psi.Arguments} (port={port})");
                AppendLog($"--- DSH private runtime start (port={port}) ---");
                var process = Process.Start(psi);
                if (process is null)
                {
                    LastError = "无法启动私有 DSH Runtime 进程";
                    return false;
                }

                _process = process;
                File.WriteAllText(_runtimePidPath, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _intentionalStop = false;
                process.EnableRaisingEvents = true;
                process.Exited += OnProcessExited;
                PumpOutput(process);
                var deadline = DateTime.UtcNow + NpxReadyTimeout;
                while (DateTime.UtcNow < deadline && !process.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    if (await ProbeAsync(_activeBaseUri, ct))
                    {
                        // DSH 先监听端口，再异步输出带认证令牌的完整地址；等待输出泵完成，避免 WebView 抢先加载无令牌地址。
                        var readyTask = _readyUrlSource.Task;
                        try
                        {
                            await readyTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
                        }
                        catch (TimeoutException)
                        {
                            // 旧版 runtime 无令牌输出；新版未捕获时保持启动成功，由认证层在 RPC/事件流上显式报错。
                            Log?.Invoke("private runtime ready, but authenticated URL not captured within 10s");
                        }

                        Log?.Invoke($"private runtime ready: {ActiveBaseUrl}");
                        return true;
                    }
                }

                if (process.HasExited)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                    LastError = WithLogTail($"私有 DSH Runtime 启动失败（退出码 {process.ExitCode}），已停止继续尝试端口");
                    KillOwnedProcess();
                    return false;
                }

                KillOwnedProcess();
            }

            LastError = WithLogTail("私有 DSH Runtime 未能启动：3080 及备用端口均不可用或服务未就绪");
            Log?.Invoke($"managed start failed: {LastError}");
            return false;
        }
        catch (OperationCanceledException)
        {
            // 取消发生在已拉起进程后时，不能把孤儿 Runtime 留在后台。
            KillOwnedProcess();
            throw;
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
        _intentionalStop = true;
        KillOwnedProcess();
    }

    /// <summary>将私有目录中的 DSH 官方包显式升级到 npm latest。不会自动启动服务。</summary>
    public async Task<bool> UpdatePrivatePackageAsync(string? targetVersion = null, CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            if (_disposed || _mode != ManagedMode.Npx)
            {
                LastError = "当前不是官方包托管模式";
                return false;
            }

            if (!DshSharpCompatibility.IsCompatible(targetVersion))
            {
                LastError = $"DSH Runtime 更新已阻止：目标版本 {targetVersion ?? "未知"} 不在支持范围 {DshSharpCompatibility.SupportedRange} 内";
                return false;
            }

            var backup = _runtimePreviousDirectory;
            if (!TryDeleteDirectory(_runtimeStagingDirectory) || Directory.Exists(_runtimeStagingDirectory) ||
                !TryDeleteDirectory(backup) || Directory.Exists(backup))
            {
                LastError = WithLogTail("无法清理上一次 Runtime 更新残留目录，请关闭占用文件后重试");
                return false;
            }
            var hadActiveRuntime = Directory.Exists(_packageDirectory);
            if (!await InstallRuntimeIntoAsync(_runtimeStagingDirectory, targetVersion, ct))
            {
                TryDeleteDirectory(_runtimeStagingDirectory);
                return false;
            }

            WriteRuntimeTransaction("Prepared", targetVersion);
            KillOwnedProcess();
            try
            {
                if (Directory.Exists(_packageDirectory))
                {
                    Directory.Move(_packageDirectory, backup);
                    _runtimeBackupDirectory = backup;
                    WriteRuntimeTransaction("ActiveMoved", targetVersion);
                }

                Directory.Move(_runtimeStagingDirectory, _packageDirectory);
                WriteRuntimeTransaction("Promoted", targetVersion);
                if (!hadActiveRuntime)
                    ClearRuntimeTransaction();
                LastError = null;
                Log?.Invoke($"private DSH Runtime staged and promoted, version={InstalledPackageVersion ?? "unknown"}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = WithLogTail($"Runtime 目录切换失败：{ex.Message}");
                RestoreRuntimeDirectories(forceRollback: Directory.Exists(_runtimePreviousDirectory));
                return false;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>恢复最近一次 Runtime 更新前的私有安装。</summary>
    public bool RollbackRuntime()
    {
        if (!CanRollbackRuntime)
        {
            return false;
        }

        KillOwnedProcess();
        var failed = _packageDirectory + ".failed";
        try
        {
            if (!TryDeleteDirectory(failed) || Directory.Exists(failed))
            {
                LastError = WithLogTail("Runtime 回滚失败：无法清理失败版本目录");
                return false;
            }
            if (Directory.Exists(_packageDirectory)) Directory.Move(_packageDirectory, failed);
            Directory.Move(_runtimeBackupDirectory!, _packageDirectory);
            if (!TryDeleteDirectory(failed) || Directory.Exists(failed))
            {
                LastError = WithLogTail("Runtime 已恢复，但无法清理失败版本目录；请关闭占用文件后重试");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = WithLogTail($"Runtime 回滚失败：{ex.Message}");
            return false;
        }
        _runtimeBackupDirectory = null;
        ClearRuntimeTransaction();
        LastError = null;
        Log?.Invoke("private DSH Runtime rolled back");
        return true;
    }

    /// <summary>释放：仅终止本客户端拉起的服务进程。</summary>
    public void Dispose()
    {
        _disposed = true;
        _intentionalStop = true;
        KillOwnedProcess();
        _startLock.Dispose();
        _profileEditLock.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        // 保留最后已知地址（含令牌 query）供 UI 展示与重启前比对；下次启动重新探测端口。

        Log?.Invoke($"managed process exited, code={process.ExitCode}");
        AppendLog($"--- DSH service process exited, code={process.ExitCode} ---");
        if (!_intentionalStop && !_disposed)
        {
            ProcessExitedUnexpectedly?.Invoke(this, EventArgs.Empty);
        }
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
            try { if (File.Exists(_runtimePidPath)) File.Delete(_runtimePidPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 清理遗留的私有 Runtime 进程：pid 文件记录的直接杀掉；
    /// 再按命令行兜底扫描（仅限命令行包含私有运行目录的 node 进程，外部 DSH 不受影响）。
    /// 覆盖 pid 文件丢失（异常退出未写、手误删除、旧版本部署）遗留的孤儿。
    /// </summary>
    private void CleanupOrphanedRuntimeProcess()
    {
        try
        {
            if (File.Exists(_runtimePidPath) && int.TryParse(File.ReadAllText(_runtimePidPath), out var pid))
            {
                KillRuntimePid(pid);
            }

            var ownedId = _process?.Id;
            foreach (var orphanPid in FindPrivateRuntimeProcessIds())
            {
                if (orphanPid == ownedId)
                {
                    continue;
                }

                Log?.Invoke($"orphaned private runtime detected by command line: pid={orphanPid}");
                KillRuntimePid(orphanPid);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Log?.Invoke($"orphaned runtime cleanup skipped: {ex.Message}");
        }
        finally { try { if (File.Exists(_runtimePidPath)) File.Delete(_runtimePidPath); } catch (IOException) { } }
    }

    private void KillRuntimePid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
                Log?.Invoke($"orphaned private runtime killed: pid={pid}");
            }
        }
        catch (ArgumentException)
        {
            // 进程已不存在。
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log?.Invoke($"orphaned runtime kill failed (pid={pid}): {ex.Message}");
        }
    }

    /// <summary>
    /// 扫描命令行指向私有 Runtime 安装目录的 node 进程。
    /// 只有同时包含私有目录路径与官方 bin.js 的进程才会被识别为“自己人”，外部/源码 DSH 不会匹配。
    /// </summary>
    internal IEnumerable<int> FindPrivateRuntimeProcessIds()
    {
        var marker = NormalizePath(Path.Combine(_packageDirectory, "node_modules"));
        foreach (var (pid, commandLine) in ListNodeProcessCommandLines())
        {
            if (commandLine is not null &&
                NormalizePath(commandLine).Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                (commandLine.Contains("dsh-entry.mjs", StringComparison.OrdinalIgnoreCase) ||
                 commandLine.Contains("bin.js", StringComparison.OrdinalIgnoreCase)))
            {
                yield return pid;
            }
        }
    }

    private static string NormalizePath(string value) => value.Replace('/', '\\');

    private static IEnumerable<(int Pid, string? CommandLine)> ListNodeProcessCommandLines()
    {
        return OperatingSystem.IsWindows()
            ? ListNodeProcessCommandLinesWindows()
            : ListNodeProcessCommandLinesUnix();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<(int Pid, string? CommandLine)> ListNodeProcessCommandLinesWindows()
    {
        var results = new List<(int Pid, string? CommandLine)>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'");
            foreach (var item in searcher.Get().Cast<System.Management.ManagementBaseObject>())
            {
                using (item)
                {
                    var pid = Convert.ToInt32(item["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                    results.Add((pid, item["CommandLine"] as string));
                }
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // WMI 不可用时跳过兜底扫描，pid 文件路径仍然有效。
            Log?.Invoke($"runtime process scan unavailable: {ex.Message}");
        }

        return results;
    }

    private static List<(int Pid, string? CommandLine)> ListNodeProcessCommandLinesUnix()
    {
        var results = new List<(int Pid, string? CommandLine)>();
        try
        {
            var psi = new ProcessStartInfo("ps", "-eo pid=,args=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var ps = Process.Start(psi);
            if (ps is null)
            {
                return results;
            }

            var lines = ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            ps.WaitForExit(TimeSpan.FromSeconds(5));
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                var space = trimmed.IndexOf(' ');
                if (space <= 0 || !int.TryParse(trimmed[..space], out var pid))
                {
                    continue;
                }

                var args = trimmed[(space + 1)..];
                if (args.StartsWith("node", StringComparison.Ordinal))
                {
                    results.Add((pid, args));
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // ps 不可用时跳过兜底扫描。
            Log?.Invoke($"runtime process scan unavailable: {ex.Message}");
        }

        return results;
    }

    private IEnumerable<int> CandidatePorts()
    {
        yield return 3080;
        for (var port = 3081; port <= 3090; port++) yield return port;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void WriteRuntimeTransaction(string phase, string? targetVersion)
    {
        var transaction = new RuntimeUpdateTransaction(phase, targetVersion, DateTimeOffset.UtcNow);
        var tempPath = _runtimeTransactionPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_runtimeTransactionPath)!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(transaction, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        try
        {
            if (File.Exists(_runtimeTransactionPath))
                File.Replace(tempPath, _runtimeTransactionPath, null);
            else
                File.Move(tempPath, _runtimeTransactionPath);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private void RecoverRuntimeUpdate()
    {
        if (!File.Exists(_runtimeTransactionPath))
        {
            return;
        }

        RuntimeUpdateTransaction? transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<RuntimeUpdateTransaction>(File.ReadAllText(_runtimeTransactionPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log?.Invoke($"Runtime 事务记录损坏，将保留目录并尝试恢复：{ex.Message}");
            transaction = null;
        }

        try
        {
            switch (transaction?.Phase)
            {
                case "Prepared":
                    RecoverPreparedTransaction();
                    break;
                case "ActiveMoved":
                    RecoverActiveMovedTransaction(transaction!);
                    break;
                case "Promoted":
                    RecoverPromotedTransaction();
                    break;
                default:
                    RestoreRuntimeDirectories();
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = WithLogTail($"Runtime 更新恢复失败：{ex.Message}");
            Log?.Invoke(LastError);
        }
    }

    private void RecoverPreparedTransaction()
    {
        if (!Directory.Exists(_packageDirectory) && Directory.Exists(_runtimePreviousDirectory))
        {
            Directory.Move(_runtimePreviousDirectory, _packageDirectory);
        }
        else if (!Directory.Exists(_packageDirectory) && !Directory.Exists(_runtimePreviousDirectory) &&
                 Directory.Exists(_runtimeStagingDirectory))
        {
            Directory.Move(_runtimeStagingDirectory, _packageDirectory);
        }

        if (!Directory.Exists(_packageDirectory) ||
            !TryDeleteDirectory(_runtimeStagingDirectory) || Directory.Exists(_runtimeStagingDirectory))
        {
            LastError = "Runtime 更新中断，且无法恢复到一致的目录状态";
            return;
        }

        ClearRuntimeTransaction();
    }

    private void RecoverActiveMovedTransaction(RuntimeUpdateTransaction transaction)
    {
        if (Directory.Exists(_packageDirectory))
        {
            // 新目录已提升但进程在写入 Promoted 前退出；保留 previous 供健康检查回滚。
            if (Directory.Exists(_runtimePreviousDirectory))
            {
                _runtimeBackupDirectory = _runtimePreviousDirectory;
                WriteRuntimeTransaction("Promoted", transaction.TargetVersion);
            }
            else
            {
                ClearRuntimeTransaction();
            }
            return;
        }

        RestoreRuntimeDirectories();
    }

    private void RecoverPromotedTransaction()
    {
        if (!Directory.Exists(_packageDirectory) && Directory.Exists(_runtimePreviousDirectory))
        {
            RestoreRuntimeDirectories();
            return;
        }

        if (Directory.Exists(_packageDirectory) && Directory.Exists(_runtimePreviousDirectory))
        {
            _runtimeBackupDirectory = _runtimePreviousDirectory;
            return;
        }

        if (!Directory.Exists(_packageDirectory) && Directory.Exists(_runtimeStagingDirectory))
        {
            Directory.Move(_runtimeStagingDirectory, _packageDirectory);
        }

        if (Directory.Exists(_packageDirectory))
        {
            ClearRuntimeTransaction();
        }
        else
        {
            LastError = "Runtime 更新中断，找不到可启动的版本目录";
        }
    }

    private void RestoreRuntimeDirectories(bool forceRollback = false)
    {
        try
        {
            var failed = _packageDirectory + ".failed";
            if (!TryDeleteDirectory(failed) || Directory.Exists(failed))
            {
                LastError = WithLogTail("Runtime 目录恢复失败：无法清理失败版本目录");
                return;
            }
            if (forceRollback && Directory.Exists(_packageDirectory))
            {
                Directory.Move(_packageDirectory, failed);
            }
            if (!Directory.Exists(_packageDirectory) && Directory.Exists(_runtimePreviousDirectory))
                Directory.Move(_runtimePreviousDirectory, _packageDirectory);
            if ((!TryDeleteDirectory(failed) && Directory.Exists(failed)) ||
                (!TryDeleteDirectory(_runtimeStagingDirectory) && Directory.Exists(_runtimeStagingDirectory)))
            {
                LastError = WithLogTail("Runtime 目录已恢复，但无法清理更新残留；将保留事务记录以便下次继续恢复");
                return;
            }
            _runtimeBackupDirectory = null;
            ClearRuntimeTransaction();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = WithLogTail($"Runtime 目录恢复失败：{ex.Message}");
            Log?.Invoke(LastError);
        }
    }

    private void ClearRuntimeTransaction() => TryDeleteFile(_runtimeTransactionPath);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ProcessStartInfo BuildStartInfo(int port)
    {
        var privateStartInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = _packageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        privateStartInfo.ArgumentList.Add(EnsureCliEntryWrapper());
        privateStartInfo.ArgumentList.Add("web");
        privateStartInfo.ArgumentList.Add("--no-open");
        privateStartInfo.ArgumentList.Add("--host");
        privateStartInfo.ArgumentList.Add("127.0.0.1");
        privateStartInfo.ArgumentList.Add("--port");
        privateStartInfo.ArgumentList.Add(port.ToString());
        ApplyPrivateEnvironment(privateStartInfo);

        return privateStartInfo;
    }

    private void ApplyPrivateEnvironment(ProcessStartInfo startInfo) =>
        startInfo.Environment["DSH_HOME"] = _dshHomeDirectory;

    private void ConfigurePrivatePnpmPath(ProcessStartInfo startInfo)
    {
        var privateToolsBin = Path.Combine(_packageToolsDirectory, "node_modules", ".bin");
        var privatePackageBin = Path.Combine(_packageDirectory, "node_modules", ".bin");
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            privateToolsBin,
            privatePackageBin,
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
    }

    /// <summary>首次运行通过私有 pnpm 安装 latest 并固定版本；后续启动复用该版本。</summary>
    private async Task<bool> EnsurePrivatePackageAsync(bool forceUpdate, CancellationToken ct, string? targetVersion = null)
    {
        if (!forceUpdate && HasCurrentRuntimeLayout(_packageDirectory) &&
            File.Exists(PrivateDshEntryPath) && File.Exists(PrivatePnpmEntryPath) &&
            ReadInstalledPackageVersion() is not null)
        {
            return true;
        }

        // 普通启动和布局迁移必须保留用户当前的 DSH 版本；只有显式更新操作
        // 通过 targetVersion 改变它。首次安装才使用客户端验证过的默认版本。
        var versionToInstall = ResolveRuntimeInstallVersion(targetVersion, ReadInstalledPackageVersion());
        if (!DshSharpCompatibility.IsCompatible(versionToInstall))
        {
            LastError = $"DSH Runtime 安装已阻止：目标版本 {versionToInstall} 不在支持范围 {DshSharpCompatibility.SupportedRange} 内";
            return false;
        }

        Directory.CreateDirectory(_packageDirectory);
        Directory.CreateDirectory(_packageToolsDirectory);
        WritePrivatePnpmManifest();
        WritePrivatePnpmConfig(_packageDirectory);
        AppendLog(forceUpdate
            ? "--- updating private DSH package ---"
            : "--- installing private DSH package ---");

        try
        {
            if (!forceUpdate && !HasCurrentRuntimeLayout(_packageDirectory) &&
                Directory.Exists(Path.Combine(_packageDirectory, "node_modules")))
            {
                TryDeleteDirectory(Path.Combine(_packageDirectory, "node_modules"));
            }

            if (!HasExpectedPrivatePnpm(_packageToolsDirectory))
            {
                if (!TryDeleteDirectory(Path.Combine(_packageToolsDirectory, "node_modules")))
                {
                    LastError = WithLogTail("无法替换私有 pnpm；请关闭占用文件后重试");
                    return false;
                }
                AppendLog("bootstrapping private pnpm");
                var bootstrapExitCode = await RunInstallProcessAsync(BuildPnpmBootstrapStartInfo(), ct);
                if (bootstrapExitCode != 0 || !File.Exists(PrivatePnpmEntryPath))
                {
                    LastError = WithLogTail($"私有 pnpm 安装失败（npm 退出码 {bootstrapExitCode}）");
                    return false;
                }
            }

            WritePrivatePnpmWorkspaceConfig();
            if (!File.Exists(PrivateDshEntryPath) && Directory.Exists(Path.Combine(_packageDirectory, "node_modules")))
            {
                // 旧版本可能留下指向已移动目录的 .modules.yaml，先清理依赖布局再重装。
                TryDeleteDirectory(Path.Combine(_packageDirectory, "node_modules"));
            }
            AppendLog(forceUpdate ? "updating DSH with private pnpm" : "installing DSH with private pnpm");
            var installExitCode = await RunInstallProcessAsync(BuildDshInstallStartInfo(versionToInstall), ct);
            if (installExitCode != 0 || !File.Exists(PrivateDshEntryPath))
            {
                LastError = WithLogTail($"DSH 官方包安装失败（pnpm 退出码 {installExitCode}）");
                return false;
            }

            var version = ReadInstalledPackageVersion();
            File.WriteAllText(RuntimeLayoutMarkerPath(_packageDirectory), "3\n", new UTF8Encoding(false));
            LastError = null;
            Log?.Invoke($"private DSH package ready, version={version ?? "unknown"}");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            LastError = WithLogTail($"DSH 官方包安装失败：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> InstallRuntimeIntoAsync(string runtimeDirectory, string? targetVersion, CancellationToken ct)
    {
        if (!DshSharpCompatibility.IsCompatible(targetVersion))
        {
            LastError = $"DSH Runtime 安装已阻止：目标版本 {targetVersion ?? "未知"} 不在支持范围 {DshSharpCompatibility.SupportedRange} 内";
            return false;
        }

        var toolsDirectory = Path.Combine(runtimeDirectory, ".tools");
        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(toolsDirectory);
        WritePrivatePnpmManifest(toolsDirectory);
        WritePrivatePnpmConfig(runtimeDirectory);
        try
        {
            var pnpmEntry = Path.Combine(toolsDirectory, "node_modules", "pnpm", "bin", "pnpm.cjs");
            if (!HasExpectedPrivatePnpm(toolsDirectory))
            {
                if (!TryDeleteDirectory(Path.Combine(toolsDirectory, "node_modules")))
                {
                    LastError = WithLogTail("无法替换暂存 Runtime 的私有 pnpm");
                    return false;
                }
                if (await RunInstallProcessAsync(BuildPnpmBootstrapStartInfo(toolsDirectory), ct) != 0 || !File.Exists(pnpmEntry))
                {
                    LastError = WithLogTail("私有 pnpm 安装失败");
                    return false;
                }
            }

            WritePrivatePnpmWorkspaceConfig(runtimeDirectory);
            if (await RunInstallProcessAsync(BuildDshInstallStartInfo(runtimeDirectory, toolsDirectory, targetVersion), ct) != 0 ||
                !IsRuntimeDirectoryComplete(runtimeDirectory))
            {
                LastError = WithLogTail("DSH 官方包安装失败或 Runtime 不完整");
                return false;
            }

            File.WriteAllText(RuntimeLayoutMarkerPath(runtimeDirectory), "3\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            LastError = WithLogTail($"DSH 官方包安装失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>确保随客户端发布的 DSH 插件已链接到 web 配置。</summary>
    private async Task<bool> EnsureBundledPluginsAsync(CancellationToken ct)
    {
        var manifestPath = Path.Combine(_bundledShortcutPluginDirectory, "package.json");
        if (!File.Exists(manifestPath))
        {
            LastError = $"内置快捷键插件缺失：{manifestPath}";
            return false;
        }

        var expectedSpec = $"link:{Path.GetFullPath(_bundledShortcutPluginDirectory).Replace('\\', '/')}";
        var profileManifest = GetWebProfileManifestPath();
        if (IsProfileDependencyCurrent(profileManifest, "@yangfeng/dsh-sharp-session", expectedSpec))
        {
            return await RemoveLegacyShortcutPluginAsync(profileManifest, ct);
        }

        AppendLog("--- installing bundled dsh-sharp-session plugin ---");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = _packageDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            ApplyPrivateEnvironment(startInfo);
            ConfigurePrivatePnpmPath(startInfo);
            startInfo.ArgumentList.Add(EnsureCliEntryWrapper());
            startInfo.ArgumentList.Add("plugin");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add("web");
            startInfo.ArgumentList.Add("add");
            startInfo.ArgumentList.Add(expectedSpec);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                LastError = "无法启动 DSH 插件安装命令";
                return false;
            }

            PumpOutput(process);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 ||
                !IsProfileDependencyCurrent(profileManifest, "@yangfeng/dsh-sharp-session", expectedSpec))
            {
                LastError = WithLogTail($"内置快捷键插件安装失败（DSH 退出码 {process.ExitCode}）");
                return false;
            }

            Log?.Invoke("bundled dsh-sharp-session plugin ready");
            return await RemoveLegacyShortcutPluginAsync(profileManifest, ct);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            LastError = WithLogTail($"内置快捷键插件安装失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>迁移旧快捷键包，避免新旧插件同时注册 Esc 监听器。</summary>
    private async Task<bool> RemoveLegacyShortcutPluginAsync(string profileManifest, CancellationToken ct)
    {
        const string legacyPackage = "@yangfeng/dsh-shortcuts";
        if (!HasProfileDependency(profileManifest, legacyPackage))
        {
            return true;
        }

        AppendLog("--- removing legacy dsh-shortcuts plugin ---");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = _packageDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            ApplyPrivateEnvironment(startInfo);
            ConfigurePrivatePnpmPath(startInfo);
            startInfo.ArgumentList.Add(EnsureCliEntryWrapper());
            startInfo.ArgumentList.Add("plugin");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add("web");
            startInfo.ArgumentList.Add("remove");
            startInfo.ArgumentList.Add(legacyPackage);

            var exitCode = await RunInstallProcessAsync(startInfo, ct);
            if (exitCode != 0 || HasProfileDependency(profileManifest, legacyPackage))
            {
                LastError = WithLogTail($"旧快捷键插件迁移失败（DSH 退出码 {exitCode}）");
                return false;
            }

            Log?.Invoke("legacy dsh-shortcuts plugin removed");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            LastError = WithLogTail($"旧快捷键插件迁移失败：{ex.Message}");
            return false;
        }
    }

    private static bool HasProfileDependency(string profileManifest, string packageName)
    {
        try
        {
            if (!File.Exists(profileManifest))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(profileManifest));
            return document.RootElement.TryGetProperty("dependencies", out var dependencies) &&
                   dependencies.TryGetProperty(packageName, out _);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string GetWebProfileManifestPath() =>
        Path.Combine(_dshHomeDirectory, "profiles", "web", "package.json");

    private static string? ReadPluginVersion(string profileManifest, string name)
    {
        var packagePath = Path.Combine(Path.GetDirectoryName(profileManifest) ?? string.Empty, "node_modules", name, "package.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packagePath));
            return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<bool> RunSerializedProfilePackageEditAsync(string name, string action, CancellationToken ct)
    {
        await _profileEditLock.WaitAsync(ct);
        try
        {
            return await RunProfilePackageEditAsync(name, action, ct);
        }
        finally
        {
            _profileEditLock.Release();
        }
    }

    private async Task<bool> RunProfilePackageEditAsync(string name, string action, CancellationToken ct)
    {
        var profileManifest = GetWebProfileManifestPath();
        if (!File.Exists(profileManifest) || string.Equals(name, "@yangfeng/dsh-sharp-session", StringComparison.Ordinal))
        {
            LastError = "内置插件由客户端管理，不能停用或卸载";
            return false;
        }

        if (action is "activate" or "deactivate")
        {
            if (action == "activate" && TryGetKnownPluginIncompatibility(name, out var incompatibility))
            {
                LastError = incompatibility;
                Log?.Invoke($"plugin activation blocked: {name}; {incompatibility}");
                return false;
            }

            try
            {
                var originalManifest = await File.ReadAllTextAsync(profileManifest, ct);
                using var document = JsonDocument.Parse(originalManifest);
                var root = JsonNode.Parse(document.RootElement.GetRawText())?.AsObject() ?? new JsonObject();
                var dsh = root["dsh"]?.AsObject() ?? new JsonObject();
                var profile = dsh["profile"]?.AsObject() ?? new JsonObject();
                var bundles = profile["bundles"]?.AsArray() ?? [];
                var exists = bundles.Any(x => string.Equals(x?.GetValue<string>(), name, StringComparison.Ordinal));
                if (action == "activate" && !exists) bundles.Add(name);
                if (action == "deactivate")
                {
                    for (var i = bundles.Count - 1; i >= 0; i--)
                        if (string.Equals(bundles[i]?.GetValue<string>(), name, StringComparison.Ordinal)) bundles.RemoveAt(i);
                }
                profile["bundles"] = bundles; dsh["profile"] = profile; root["dsh"] = dsh;
                await WriteTextAtomicallyAsync(
                    profileManifest,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    ct);

                if (!await VerifyProfileCompositionAsync(ct))
                {
                    await WriteTextAtomicallyAsync(profileManifest, originalManifest, ct);
                    LastError = WithLogTail("插件状态未保存：DSH 无法组合此插件配置，已自动恢复原设置");
                    Log?.Invoke($"plugin configuration rolled back: {name}");
                    return false;
                }

                LastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                LastError = $"插件状态保存失败：{ex.Message}";
                return false;
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "node", WorkingDirectory = Path.GetDirectoryName(profileManifest), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        ApplyPrivateEnvironment(startInfo);
        ConfigurePrivatePnpmPath(startInfo);
        startInfo.ArgumentList.Add(EnsureCliEntryWrapper()); startInfo.ArgumentList.Add("plugin");
        startInfo.ArgumentList.Add("--profile"); startInfo.ArgumentList.Add("web"); startInfo.ArgumentList.Add("remove"); startInfo.ArgumentList.Add(name);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            PumpOutput(process); await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            LastError = $"插件卸载失败：{ex.Message}"; return false;
        }
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), ct);
        try
        {
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    /// 在重启服务前用 DSH 官方配置组合命令验证 profile，失败时调用方可回滚清单。
    /// 这不能替代第三方插件的完整兼容性测试，但能阻止无效 patch 直接写入激活列表。
    /// </summary>
    private async Task<bool> VerifyProfileCompositionAsync(CancellationToken ct)
    {
        if (!File.Exists(PrivateDshEntryPath))
        {
            LastError = "无法验证插件配置：DSH 私有安装不存在";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = _packageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        ApplyPrivateEnvironment(startInfo);
        startInfo.ArgumentList.Add(EnsureCliEntryWrapper());
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--dump-config");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                LastError = "无法启动 DSH 插件配置验证";
                return false;
            }

            // --dump-config 的正常输出很大，不应写入运行日志；仅在失败时保留诊断。
            var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
            var standardError = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var error = await standardError;
            _ = await standardOutput;
            if (process.ExitCode == 0)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                AppendLog($"plugin configuration validation failed: {error.Trim()}");
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            LastError = $"插件配置验证失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>记录已在当前 DSH 版本实测会阻断 Web 启动的第三方插件。</summary>
    private bool TryGetKnownPluginIncompatibility(string name, out string message)
    {
        if (string.Equals(name, "dsh-routing-suite", StringComparison.Ordinal) &&
            string.Equals(ReadPluginVersion(GetWebProfileManifestPath(), name), "0.1.2", StringComparison.Ordinal) &&
            string.Equals(InstalledPackageVersion, "0.1.1-rc.2", StringComparison.Ordinal))
        {
            message = "dsh-routing-suite 0.1.2 与当前 DSH 0.1.1-rc.2 不兼容：实测会阻断 Web 客户端启动。请升级该插件或 DSH 后再试。";
            return true;
        }

        message = string.Empty;
        return false;
    }

    /// <summary>判断 web 配置中的插件依赖是否已指向当前内置目录。</summary>
    public static bool IsProfileDependencyCurrent(
        string profileManifest,
        string packageName,
        string expectedSpec)
    {
        try
        {
            if (!File.Exists(profileManifest))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(profileManifest));
            return document.RootElement.TryGetProperty("dependencies", out var dependencies) &&
                   dependencies.TryGetProperty(packageName, out var spec) &&
                   string.Equals(spec.GetString()?.Replace('\\', '/'), expectedSpec, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>检查 Runtime 暂存目录是否具备可启动的最小文件集。</summary>
    public static bool IsRuntimeDirectoryComplete(string runtimeDirectory)
    {
        var entry = Path.Combine(runtimeDirectory, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        return File.Exists(entry) && ReadInstalledPackageVersion(runtimeDirectory) is not null;
    }

    private string PrivateDshEntryPath => Path.Combine(
        _packageDirectory,
        "node_modules",
        "@deepseek-ai",
        "dsh",
        "lib",
        "bin.js");

    /// <summary>
    /// CLI 入口包装：Node &lt; 23 的 <c>import.meta.main</c> 恒为 undefined，
    /// 官方 bin.js（0.1.5+）的 <c>if (import.meta.main) await runCli()</c> 不会触发，
    /// 进程会静默退出 0。此 wrapper 显式调用其导出的 <c>runCli()</c>，
    /// 对已有 main 判定的版本行为一致。
    /// </summary>
    private string CliWrapperPath => Path.Combine(_packageToolsDirectory, "dsh-entry.mjs");

    /// <summary>确保 CLI wrapper 存在并返回其路径；bin.js 缺失时返回原路径（由调用方报缺失错误）。</summary>
    private string EnsureCliEntryWrapper()
    {
        if (!File.Exists(PrivateDshEntryPath))
        {
            return PrivateDshEntryPath;
        }

        Directory.CreateDirectory(_packageToolsDirectory);
        var fileUrl = new Uri(PrivateDshEntryPath).AbsoluteUri;
        var content = $$"""
            // Generated by DSH-Sharp: Node < 23 has no import.meta.main, so the official
            // bin.js self-dispatch never runs; invoke its exported runCli() explicitly.
            import { runCli } from "{{fileUrl}}";
            await runCli();
            """;
        try
        {
            if (!File.Exists(CliWrapperPath) || File.ReadAllText(CliWrapperPath) != content)
            {
                File.WriteAllText(CliWrapperPath, content, new System.Text.UTF8Encoding(false));
            }
        }
        catch (IOException ex)
        {
            Log?.Invoke($"cli wrapper write failed: {ex.Message}");
        }

        return CliWrapperPath;
    }

    private string PrivatePnpmEntryPath => Path.Combine(
        _packageToolsDirectory,
        "node_modules",
        "pnpm",
        "bin",
        "pnpm.cjs");

    private static string RuntimeLayoutMarkerPath(string runtimeDirectory) =>
        Path.Combine(runtimeDirectory, ".dshsharp-runtime-layout-v2");

    private static bool HasCurrentRuntimeLayout(string runtimeDirectory)
    {
        try
        {
            return string.Equals(
                File.ReadAllText(RuntimeLayoutMarkerPath(runtimeDirectory)).Trim(),
                "3",
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasExpectedPrivatePnpm(string toolsDirectory)
    {
        var packagePath = Path.Combine(toolsDirectory, "node_modules", "pnpm", "package.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            return document.RootElement.TryGetProperty("version", out var version) &&
                string.Equals(version.GetString(), ManagedPnpmSpec["pnpm@".Length..], StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveRuntimeInstallVersion(string? requestedVersion, string? installedVersion)
        => requestedVersion ?? installedVersion ?? DshSharpCompatibility.DefaultDshVersion;

    private static string CommandName(string name) => OperatingSystem.IsWindows() ? $"{name}.cmd" : name;

    private ProcessStartInfo BuildPnpmBootstrapStartInfo()
        => BuildPnpmBootstrapStartInfo(_packageToolsDirectory);

    private ProcessStartInfo BuildPnpmBootstrapStartInfo(string toolsDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : "npm",
            WorkingDirectory = toolsDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(
                $"npm install --no-audit --no-fund --save-exact {ManagedPnpmSpec}");
            return startInfo;
        }

        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add("--no-fund");
        startInfo.ArgumentList.Add("--save-exact");
        startInfo.ArgumentList.Add(ManagedPnpmSpec);
        return startInfo;
    }

    private ProcessStartInfo BuildDshInstallStartInfo(string? targetVersion = null)
        => BuildDshInstallStartInfo(_packageDirectory, _packageToolsDirectory, targetVersion);

    private ProcessStartInfo BuildDshInstallStartInfo(string runtimeDirectory, string toolsDirectory, string? targetVersion = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(toolsDirectory, "node_modules", "pnpm", "bin", "pnpm.cjs"));
        startInfo.ArgumentList.Add("add");
        startInfo.ArgumentList.Add("--save-exact");
        startInfo.ArgumentList.Add($"{ManagedPackageName}@{targetVersion ?? DshSharpCompatibility.DefaultDshVersion}");
        return startInfo;
    }

    private async Task<int> RunInstallProcessAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return -1;
        }

        PumpOutput(process);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 安装工具也会派生 node/npm 子进程；取消不能留下它们继续改写 Runtime 目录。
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log?.Invoke($"kill install process failed: {ex.Message}");
            }
            throw;
        }
        return process.ExitCode;
    }

    private void WritePrivatePnpmWorkspaceConfig()
        => WritePrivatePnpmWorkspaceConfig(_packageDirectory);

    private static void WritePrivatePnpmWorkspaceConfig(string runtimeDirectory)
    {
        const string config = """
            # pnpm 11 仅从 workspace 配置读取 nodeLinker；.npmrc 中同名设置会被忽略。
            nodeLinker: hoisted
            allowBuilds:
              esbuild: true
              node-pty: true
              koffi: true
              '@google/genai': false
              protobufjs: false
              node-addon-require-builtin: false
              '@deepseek-ai/dsh-subprocess-local': true
            """;
        File.WriteAllText(
            Path.Combine(runtimeDirectory, "pnpm-workspace.yaml"),
            config + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WritePrivatePnpmConfig(string runtimeDirectory)
    {
        // pnpm 11 的非认证配置必须在 pnpm-workspace.yaml 中；保留空 .npmrc，
        // 避免旧 Runtime 中遗留的无效 node-linker 设置造成误解。
        const string config = "# DSH-Sharp Runtime npm configuration\n";
        File.WriteAllText(
            Path.Combine(runtimeDirectory, ".npmrc"),
            config,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WritePrivatePnpmManifest()
        => WritePrivatePnpmManifest(_packageToolsDirectory);

    private static void WritePrivatePnpmManifest(string toolsDirectory)
    {
        const string manifest = """
            {
              "name": "dsh-sharp-private-tools",
              "private": true
            }
            """;
        File.WriteAllText(
            Path.Combine(toolsDirectory, "package.json"),
            manifest + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string? ReadInstalledPackageVersion()
        => ReadInstalledPackageVersion(_packageDirectory);

    private static string? ReadInstalledPackageVersion(string runtimeDirectory)
    {
        var packageJson = Path.Combine(
            runtimeDirectory, "node_modules", "@deepseek-ai", "dsh", "package.json");
        try
        {
            if (!File.Exists(packageJson))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
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
                CaptureReadyUrl(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 进程退出后管道关闭：正常。
        }
    }

    private void CaptureReadyUrl(string line)
    {
        const string marker = "dsh web: ";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;
        var value = line[(index + marker.Length)..].Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            _activeBaseUri = uri;
            _readyUrlSource?.TrySetResult(uri);
            Log?.Invoke($"captured private runtime URL: {uri}");
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
