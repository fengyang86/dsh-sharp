using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NpxReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SourceReadyTimeout = TimeSpan.FromSeconds(180); // tsx 冷启动实测 90-150s
    private const string ManagedPackageName = "@deepseek-ai/dsh";
    private const string ManagedPnpmSpec = "pnpm@11.19.0";

    private readonly Uri _baseUri;
    private readonly ManagedMode _mode;
    private readonly string? _sourcePath;
    private readonly string _logPath;
    private readonly string _packageDirectory;
    private readonly string _packageToolsDirectory;
    private readonly string _bundledShortcutPluginDirectory;
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
        _packageDirectory = Path.Combine(dir, "dsh-runtime");
        _packageToolsDirectory = Path.Combine(_packageDirectory, ".tools");
        _bundledShortcutPluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins", "dsh-sharp-session");
    }

    /// <summary>可选的日志回调（由宿主注入）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>托管模式。</summary>
    public ManagedMode Mode => _mode;

    /// <summary>是否持有托管进程（仅本客户端拉起的服务）。</summary>
    public bool IsOwned => _process is { HasExited: false };

    /// <summary>最近一次启动失败原因（成功启动后清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>私有 npm 包当前安装的版本；未安装或元数据损坏时返回 null。</summary>
    public string? InstalledPackageVersion => ReadInstalledPackageVersion();

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
            }
            else if (!await EnsurePrivatePackageAsync(forceUpdate: false, ct) ||
                     !await EnsureBundledPluginsAsync(ct))
            {
                return false;
            }

            LastError = null;
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

            KillOwnedProcess();
            return await EnsurePrivatePackageAsync(forceUpdate: true, ct: ct, targetVersion: targetVersion);
        }
        finally
        {
            _startLock.Release();
        }
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
                FileName = CommandName("pnpm"),
                Arguments = $"dsh web --no-open{portArg}",
                WorkingDirectory = _sourcePath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        var privateStartInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = _packageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        privateStartInfo.ArgumentList.Add(PrivateDshEntryPath);
        privateStartInfo.ArgumentList.Add("web");
        privateStartInfo.ArgumentList.Add("--no-open");
        if (!_baseUri.IsDefaultPort)
        {
            privateStartInfo.ArgumentList.Add("--port");
            privateStartInfo.ArgumentList.Add(_baseUri.Port.ToString());
        }

        return privateStartInfo;
    }

    /// <summary>首次运行通过私有 pnpm 安装 latest 并固定版本；后续启动复用该版本。</summary>
    private async Task<bool> EnsurePrivatePackageAsync(bool forceUpdate, CancellationToken ct, string? targetVersion = null)
    {
        if (!forceUpdate && File.Exists(PrivateDshEntryPath) && File.Exists(PrivatePnpmEntryPath) &&
            ReadInstalledPackageVersion() is not null)
        {
            return true;
        }

        Directory.CreateDirectory(_packageDirectory);
        Directory.CreateDirectory(_packageToolsDirectory);
        WritePrivatePnpmManifest();
        AppendLog(forceUpdate
            ? "--- updating private DSH package ---"
            : "--- installing private DSH package ---");

        try
        {
            if (!File.Exists(PrivatePnpmEntryPath))
            {
                AppendLog("bootstrapping private pnpm");
                var bootstrapExitCode = await RunInstallProcessAsync(BuildPnpmBootstrapStartInfo(), ct);
                if (bootstrapExitCode != 0 || !File.Exists(PrivatePnpmEntryPath))
                {
                    LastError = WithLogTail($"私有 pnpm 安装失败（npm 退出码 {bootstrapExitCode}）");
                    return false;
                }
            }

            WritePrivatePnpmWorkspaceConfig();
            AppendLog(forceUpdate ? "updating DSH with private pnpm" : "installing DSH with private pnpm");
            var installExitCode = await RunInstallProcessAsync(BuildDshInstallStartInfo(targetVersion), ct);
            if (installExitCode != 0 || !File.Exists(PrivateDshEntryPath))
            {
                LastError = WithLogTail($"DSH 官方包安装失败（pnpm 退出码 {installExitCode}）");
                return false;
            }

            var version = ReadInstalledPackageVersion();
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
            var privateToolsBin = Path.Combine(_packageToolsDirectory, "node_modules", ".bin");
            var privatePackageBin = Path.Combine(_packageDirectory, "node_modules", ".bin");
            startInfo.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                privateToolsBin,
                privatePackageBin,
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.ArgumentList.Add(PrivateDshEntryPath);
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
            var privateToolsBin = Path.Combine(_packageToolsDirectory, "node_modules", ".bin");
            var privatePackageBin = Path.Combine(_packageDirectory, "node_modules", ".bin");
            startInfo.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                privateToolsBin,
                privatePackageBin,
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.ArgumentList.Add(PrivateDshEntryPath);
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

    private static string GetWebProfileManifestPath()
    {
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(dshHome))
        {
            dshHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh");
        }

        return Path.Combine(dshHome, "profiles", "web", "package.json");
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

    private string PrivateDshEntryPath => Path.Combine(
        _packageDirectory,
        "node_modules",
        "@deepseek-ai",
        "dsh",
        "lib",
        "bin.js");

    private string PrivatePnpmEntryPath => Path.Combine(
        _packageToolsDirectory,
        "node_modules",
        "pnpm",
        "bin",
        "pnpm.cjs");

    private static string CommandName(string name) => OperatingSystem.IsWindows() ? $"{name}.cmd" : name;

    private ProcessStartInfo BuildPnpmBootstrapStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : "npm",
            WorkingDirectory = _packageToolsDirectory,
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
        startInfo.ArgumentList.Add(PrivatePnpmEntryPath);
        startInfo.ArgumentList.Add("add");
        startInfo.ArgumentList.Add("--save-exact");
        // latest 标签可能被 pnpm 元数据缓存滞留；首次安装和显式更新应优先向 registry 复核。
        startInfo.ArgumentList.Add("--prefer-online");
        startInfo.ArgumentList.Add($"{ManagedPackageName}@{targetVersion ?? "latest"}");
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
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    private void WritePrivatePnpmWorkspaceConfig()
    {
        const string config = """
            allowBuilds:
              esbuild: true
              node-pty: true
              koffi: true
              '@google/genai': false
              protobufjs: false
              node-addon-require-builtin: false
            """;
        File.WriteAllText(
            Path.Combine(_packageDirectory, "pnpm-workspace.yaml"),
            config + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WritePrivatePnpmManifest()
    {
        const string manifest = """
            {
              "name": "dsh-sharp-private-tools",
              "private": true
            }
            """;
        File.WriteAllText(
            Path.Combine(_packageToolsDirectory, "package.json"),
            manifest + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string? ReadInstalledPackageVersion()
    {
        var packageJson = Path.Combine(
            _packageDirectory, "node_modules", "@deepseek-ai", "dsh", "package.json");
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
