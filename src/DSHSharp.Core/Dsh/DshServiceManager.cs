using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// <item>启动前环境预检：Node 缺失或版本不足时自动下载内置 Node 兜底（不改动系统 Node）；</item>
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
    // pnpm@11.25 的 engines 要求 node >= 22.13（其 bundle 静态 require node:sqlite，
    // 该模块 22.13 起才免 flag、Node 20 完全没有，旧版会在启动时抛 ERR_UNKNOWN_BUILTIN_MODULE）。
    // 系统 Node 不达标时自动下载 NodeRuntimeService.NodeVersion 钉住的内置 Node 兜底；
    // 调整任一版本必须同步验证另一个（见 VersionConsistencyTests）。
    internal const string MinimumNodeVersion = "22.13.0";
    // 首装网络兜底：registry.npmjs.org 直连失败且日志呈网络特征时，注入 npmmirror 镜像重试一次。
    // 只做第二条链路不改变默认行为（镜像同步可能滞后，避免常态使用拿旧版本）。
    internal const string RegistryMirrorUrl = "https://registry.npmmirror.com";

    /// <summary>pnpm/npm 输出中的网络类失败特征（大小写不敏感匹配日志尾部）。</summary>
    private static readonly string[] NetworkFailureTokens =
    [
        "ETIMEDOUT", "ETIMEOUT", "ECONNRESET", "ECONNREFUSED", "EPIPE", "ENOTFOUND", "EAI_AGAIN",
        "EADDRNOTAVAIL", "EPROTO", "ECONNABORTED", "UNABLE_TO_VERIFY_LEAF_SIGNATURE",
        "SELF_SIGNED_CERT_IN_CHAIN", "CERT_HAS_EXPIRED", "DEPTH_ZERO_SELF_SIGNED_CERT",
        "ERR_PNPM_META_FETCH_FAIL", "ERR_PNPM_FETCH_401", "ERR_PNPM_NO_VERSIONS",
        "fetch failed", "getaddrinfo", "request timeout", "network timeout",
        "network request failed", "ERR! network",
    ];

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
    private readonly string _registryMirrorMarkerPath;
    private readonly NodeRuntimeService _nodeRuntime;
    private string? _runtimeBackupDirectory;
    private readonly string _bundledShortcutPluginDirectory;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _profileEditLock = new(1, 1);
    private Process? _process;
    private bool _intentionalStop;
    private bool _disposed;
    private TaskCompletionSource<Uri>? _readyUrlSource;
    // 非空 = 已激活内置 Node：本管理器拉起的全部子进程（含孙进程）一律用它。
    private string? _managedNodeExe;

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
        _registryMirrorMarkerPath = Path.Combine(dir, ".registry-mirror-preferred");
        _nodeRuntime = new NodeRuntimeService(Path.Combine(dir, "node-runtime"));
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

    /// <summary>内置 Node 运行时（按需下载），宿主可订阅其 StateChanged 显示下载进度。</summary>
    public NodeRuntimeService NodeRuntime => _nodeRuntime;

    /// <summary>当前生效的 Node 环境描述（如 "v22.23.2（内置 …）" / "v20.13.1（系统 …）"）；未检查过为 null。</summary>
    public string? ActiveNodeDescription { get; private set; }

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

    /// <summary>是否存在已激活的第三方（非随客户端发布）插件：故障页"安全模式重启"按钮的显示条件。</summary>
    public bool HasThirdPartyPluginsActive => ListProfilePlugins().Any(p => p.IsActive && !p.IsBundled);

    /// <summary>
    /// 安全模式重启：停止托管进程 → 停用全部第三方插件激活（保留安装与内置插件，稍后可在设置页
    /// 逐个重新启用）→ 正常走 StartAsync。故障场景下 dsh CLI 依赖同一 Runtime，因此直接原子改写
    /// profile 清单而不是调用插件命令（官方桌面端同等场景同样绕过 CLI）。
    /// </summary>
    public async Task<bool> RestartInSafeModeAsync(CancellationToken ct = default)
    {
        await _profileEditLock.WaitAsync(ct);
        bool rewritten;
        try
        {
            Stop();
            rewritten = DeactivateThirdPartyPlugins(GetWebProfileManifestPath());
        }
        finally
        {
            _profileEditLock.Release();
        }

        if (!rewritten)
        {
            LastError = WithLogTail("安全模式失败：无法改写插件配置（详见日志）");
            return false;
        }

        AppendLog("--- safe mode restart: third-party bundles deactivated ---");
        return await StartAsync(ct);
    }

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
    /// <summary>探测复用客户端：就绪轮询 1 次/秒，逐次 new 会反复建拆连接（本地直连，不走代理）。</summary>
    private static readonly HttpClient ProbeHttpClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = ProbeTimeout,
    };

    private static async Task<bool> ProbeAsync(Uri uri, CancellationToken ct = default)
    {
        try
        {
            using var response = await ProbeHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
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
            if (!await EnsureNodeEnvironmentAsync(ct))
            {
                return false;
            }

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
            FileName = NodeExecutable,
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
        ConfigureRuntimePath(privateStartInfo);

        return privateStartInfo;
    }

    private void ApplyPrivateEnvironment(ProcessStartInfo startInfo) =>
        startInfo.Environment["DSH_HOME"] = _dshHomeDirectory;

    /// <summary>启动 node 用的可执行文件：内置 Node 激活时给完整路径（Windows 上 CreateProcess
    /// 按父进程 PATH 解析 FileName，改子进程环境对 FileName 解析无效），否则交给 PATH 解析。</summary>
    private string NodeExecutable => _managedNodeExe ?? "node";

    private string? ManagedNodeDirectory => _managedNodeExe is null ? null : Path.GetDirectoryName(_managedNodeExe);

    /// <summary>为安装/运行的全部子进程统一注入 PATH：内置 Node 目录最前（npm.cmd 与 DSH 派生的
    /// 孙进程都解析到同一个 Node，避免主/子进程 Node 版本混用），其次私有 pnpm 与包 bin，最后继承宿主 PATH。</summary>
    private static void ConfigureRuntimePath(
        ProcessStartInfo startInfo, string runtimeDirectory, string toolsDirectory, string? managedNodeDirectory)
    {
        var segments = new List<string>();
        if (managedNodeDirectory is not null)
        {
            segments.Add(managedNodeDirectory);
        }

        segments.Add(Path.Combine(toolsDirectory, "node_modules", ".bin"));
        segments.Add(Path.Combine(runtimeDirectory, "node_modules", ".bin"));
        segments.Add(Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, segments);
    }

    private void ConfigureRuntimePath(ProcessStartInfo startInfo) =>
        ConfigureRuntimePath(startInfo, _packageDirectory, _packageToolsDirectory, ManagedNodeDirectory);

    /// <summary>
    /// 启动前环境预检：内置 Node 已安装且可执行 → 始终优先（不随系统 Node 变化漂移）；
    /// 否则要求系统 Node 满足 <see cref="MinimumNodeVersion"/> 且 npm 可解析；仍不达标时
    /// 自动下载内置 Node 兜底（只作为子进程叠加层，不改动用户系统）。失败时置 LastError 并返回 false。
    /// </summary>
    private async Task<bool> EnsureNodeEnvironmentAsync(CancellationToken ct)
    {
        if (_nodeRuntime.IsInstalled &&
            await NodeEnvironment.RunNodeVersionAsync(_nodeRuntime.NodeExePath, ct) is { } managedVersion)
        {
            _managedNodeExe = _nodeRuntime.NodeExePath;
            ActiveNodeDescription = $"{managedVersion}（内置 {_nodeRuntime.NodeDirectory}）";
            return true;
        }

        if (_nodeRuntime.IsInstalled)
        {
            // 标记有效但 node.exe 无法执行（杀软/损坏）：作废标记，让下面的 EnsureAsync 重新下载。
            _nodeRuntime.Invalidate();
            Log?.Invoke("managed node marked invalid (probe failed); will re-download");
        }

        var nodePath = NodeEnvironment.ResolveOnPath("node");
        var nodeVersion = nodePath is null ? null : await NodeEnvironment.RunNodeVersionAsync(nodePath, ct);
        if (nodeVersion is not null &&
            NodeEnvironment.IsNodeVersionSupported(nodeVersion, MinimumNodeVersion) &&
            NodeEnvironment.ResolveOnPath("npm") is not null)
        {
            _managedNodeExe = null;
            ActiveNodeDescription = $"{nodeVersion}（系统 {nodePath}）";
            return true;
        }

        AppendLog("--- system node unusable, preparing managed node runtime ---");
        if (await _nodeRuntime.EnsureAsync(ct) &&
            await NodeEnvironment.RunNodeVersionAsync(_nodeRuntime.NodeExePath, ct) is { } repairedVersion)
        {
            _managedNodeExe = _nodeRuntime.NodeExePath;
            ActiveNodeDescription = $"{repairedVersion}（内置 {_nodeRuntime.NodeDirectory}）";
            return true;
        }

        var reason = nodePath is null
            ? "未在 PATH 上检测到 Node.js"
            : nodeVersion is null
                ? $"Node 无法执行（{nodePath}）"
                : !NodeEnvironment.IsNodeVersionSupported(nodeVersion, MinimumNodeVersion)
                    ? $"检测到 Node {nodeVersion}（{nodePath}），DSH 运行时需要 Node ≥ {MinimumNodeVersion}"
                    : "未检测到 npm（Node 安装不完整）";
        ActiveNodeDescription = nodeVersion is null ? reason : $"{nodeVersion}（系统 {nodePath}，不达标）";
        LastError = WithLogTail(
            $"环境检查未通过：{reason}；自动下载内置 Node 失败：{_nodeRuntime.State.Error ?? "未知原因"}，请检查网络后重试");
        return false;
    }

    /// <summary>首次运行通过私有 pnpm 安装 latest 并固定版本；后续启动复用该版本。</summary>
    private async Task<bool> EnsurePrivatePackageAsync(bool forceUpdate, CancellationToken ct, string? targetVersion = null)
    {
        if (!forceUpdate && HasCurrentRuntimeLayout(_packageDirectory) &&
            File.Exists(PrivateDshEntryPath) && File.Exists(PrivatePnpmEntryPath) &&
            ReadInstalledPackageVersion() is not null)
        {
            if (VerifyRuntimeIntegrity(_packageDirectory))
            {
                return true;
            }

            // 完整性自检失败（典型：杀软改写/截断关键文件，接上内置 Node 的 node -v 探测同一条防线）：
            // 清掉依赖布局与标记，走完整重装自愈。
            AppendLog("--- runtime integrity check failed; reinstalling ---");
            TryDeleteDirectory(Path.Combine(_packageDirectory, "node_modules"));
            TryDeleteFile(RuntimeLayoutMarkerPath(_packageDirectory));
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
                var bootstrapExitCode = await RunInstallWithAdaptiveRegistryAsync(
                    useMirror => BuildPnpmBootstrapStartInfo(useRegistryMirror: useMirror), ct);

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
            var installExitCode = await RunInstallWithAdaptiveRegistryAsync(
                useMirror => BuildDshInstallStartInfo(versionToInstall, useRegistryMirror: useMirror), ct);

            if (installExitCode != 0 || !File.Exists(PrivateDshEntryPath))
            {
                LastError = WithLogTail($"DSH 官方包安装失败（pnpm 退出码 {installExitCode}）");
                return false;
            }

            var version = ReadInstalledPackageVersion();
            File.WriteAllText(RuntimeLayoutMarkerPath(_packageDirectory), "3\n", new UTF8Encoding(false));
            WriteRuntimeIntegrityManifest(_packageDirectory);
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
                var bootstrapExitCode = await RunInstallWithAdaptiveRegistryAsync(
                    useMirror => BuildPnpmBootstrapStartInfo(runtimeDirectory, toolsDirectory, useRegistryMirror: useMirror), ct);

                if (bootstrapExitCode != 0 || !File.Exists(pnpmEntry))
                {
                    LastError = WithLogTail("私有 pnpm 安装失败");
                    return false;
                }
            }

            WritePrivatePnpmWorkspaceConfig(runtimeDirectory);
            var installExitCode = await RunInstallWithAdaptiveRegistryAsync(
                useMirror => BuildDshInstallStartInfo(runtimeDirectory, toolsDirectory, targetVersion, useRegistryMirror: useMirror), ct);

            if (installExitCode != 0 ||
                !IsRuntimeDirectoryComplete(runtimeDirectory))
            {
                LastError = WithLogTail("DSH 官方包安装失败或 Runtime 不完整");
                return false;
            }

            File.WriteAllText(RuntimeLayoutMarkerPath(runtimeDirectory), "3\n", new UTF8Encoding(false));
            WriteRuntimeIntegrityManifest(runtimeDirectory);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            LastError = WithLogTail($"DSH 官方包安装失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>随客户端发布的会话插件包名（profile 依赖中的键名）。</summary>
    private const string BundledSessionPackage = "@yangfeng/dsh-sharp-session";

    /// <summary>构造一条 dsh plugin CLI 调用（node + wrapper + 私有 pnpm PATH；输出按 UTF-8 解码避免路径乱码）。</summary>
    private ProcessStartInfo CreatePluginCliStartInfo(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = NodeExecutable,
            WorkingDirectory = _packageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ApplyPrivateEnvironment(startInfo);
        ConfigureRuntimePath(startInfo);
        startInfo.ArgumentList.Add(EnsureCliEntryWrapper());
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
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
        if (IsProfileDependencyCurrent(profileManifest, BundledSessionPackage, expectedSpec))
        {
            return await RemoveLegacyShortcutPluginAsync(profileManifest, ct);
        }

        // 链接规格变化（客户端迁移目录/重装）时，profile 里旧的 node_modules 链接可能已断：
        // 悬挂的 junction 会让 pnpm 对依赖的评估直接失败（UNKNOWN: open package.json），
        // 先卸载旧链接再装新链接，保证任意迁移后可自愈。
        if (HasProfileDependency(profileManifest, BundledSessionPackage))
        {
            AppendLog("--- removing stale bundled plugin link ---");
            try
            {
                var removeExit = await RunPluginStepWithRetryAsync(
                    CreatePluginCliStartInfo("plugin", "--profile", "web", "remove", BundledSessionPackage), ct);
                if (removeExit != 0)
                {
                    LastError = WithLogTail($"内置插件旧链接清理失败（DSH 退出码 {removeExit}）");
                    return false;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                LastError = WithLogTail($"内置插件旧链接清理失败：{ex.Message}");
                return false;
            }
        }

        AppendLog("--- installing bundled dsh-sharp-session plugin ---");
        try
        {
            var exitCode = await RunPluginStepWithRetryAsync(
                CreatePluginCliStartInfo("plugin", "--profile", "web", "add", expectedSpec), ct);
            if (exitCode != 0 ||
                !IsProfileDependencyCurrent(profileManifest, BundledSessionPackage, expectedSpec))
            {
                LastError = WithLogTail($"内置快捷键插件安装失败（DSH 退出码 {exitCode}）");
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

    /// <summary>插件链接步骤带一次重试：新装/杀软实时扫描窗口期，pnpm 穿 junction 的
    /// 打开可能瞬时失败（UNKNOWN: open、负退出码），隔 15 秒重试一次即可通过。</summary>
    private async Task<int> RunPluginStepWithRetryAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        var exitCode = await RunInstallProcessAsync(startInfo, ct);
        if (exitCode == 0)
        {
            return 0;
        }

        Log?.Invoke($"plugin step failed (exit {exitCode}), retrying once in 15s");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        catch (OperationCanceledException)
        {
            return exitCode;
        }

        return await RunInstallProcessAsync(startInfo, ct);
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
            var exitCode = await RunInstallProcessAsync(
                CreatePluginCliStartInfo("plugin", "--profile", "web", "remove", legacyPackage), ct);
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

    /// <summary>改写 profile 清单：从 <c>dsh.profile.bundles</c> 移除全部第三方条目（保留内置插件，
    /// 依赖安装不动，只取消激活）。改写前留 <c>.dshsharp-safe-mode.bak</c> 备份。
    /// 清单缺失或没有第三方激活项时返回 true（幂等）。</summary>
    internal static bool DeactivateThirdPartyPlugins(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return true;
            }

            var root = JsonNode.Parse(File.ReadAllText(manifestPath));
            var bundles = root?["dsh"]?["profile"]?["bundles"];
            if (bundles is not JsonArray array || array.Count == 0)
            {
                return true;
            }

            var kept = array
                .Select(node => node?.GetValue<string>())
                .Where(name => string.Equals(name, BundledSessionPackage, StringComparison.Ordinal))
                .OfType<string>()
                .ToList();
            if (kept.Count == array.Count)
            {
                return true; // 没有第三方激活项：无需改写。
            }

            File.Copy(manifestPath, manifestPath + ".dshsharp-safe-mode.bak", overwrite: true);
            root!["dsh"]!["profile"]!["bundles"] = new JsonArray(
                kept.Select(name => JsonValue.Create(name)).ToArray());
            File.WriteAllText(
                manifestPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log?.Invoke($"safe mode manifest rewrite failed: {ex.Message}");
            return false;
        }
    }

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
            FileName = NodeExecutable, WorkingDirectory = Path.GetDirectoryName(profileManifest), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        ApplyPrivateEnvironment(startInfo);
        ConfigureRuntimePath(startInfo);
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
            FileName = NodeExecutable,
            WorkingDirectory = _packageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        ApplyPrivateEnvironment(startInfo);
        ConfigureRuntimePath(startInfo);
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

    /// <summary>完整性清单文件名（位于 Runtime 根目录，随目录交换一起迁移）。</summary>
    internal const string IntegrityManifestFileName = ".dshsharp-integrity.json";

    /// <summary>启动关键文件（损坏即无法启动/安装）：DSH 入口、包元数据、私有 pnpm 入口。</summary>
    private static readonly string[] IntegrityTrackedFiles =
    [
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "package.json"),
        Path.Combine(".tools", "node_modules", "pnpm", "bin", "pnpm.cjs"),
    ];

    /// <summary>安装成功后写入关键文件 SHA256 清单（失败仅记日志，不阻塞安装）。</summary>
    private static void WriteRuntimeIntegrityManifest(string runtimeDirectory)
    {
        try
        {
            var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in IntegrityTrackedFiles)
            {
                var path = Path.Combine(runtimeDirectory, relative);
                if (File.Exists(path))
                {
                    manifest[relative.Replace('\\', '/')] = HashFile(path);
                }
            }

            File.WriteAllText(
                Path.Combine(runtimeDirectory, IntegrityManifestFileName),
                JsonSerializer.Serialize(manifest),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"integrity manifest write failed: {ex.Message}");
        }
    }

    /// <summary>启动时校验关键文件哈希。清单缺失（旧安装/暂存迁移中）视为通过；
    /// 任一登记文件缺失或哈希不符（杀软改写/截断）即失败，调用方走重装自愈。</summary>
    internal static bool VerifyRuntimeIntegrity(string runtimeDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(runtimeDirectory, IntegrityManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return true;
            }

            var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath));
            if (manifest is null || manifest.Count == 0)
            {
                return true;
            }

            foreach (var (relative, expected) in manifest)
            {
                var path = Path.Combine(runtimeDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) || HashFile(path) != expected)
                {
                    Log?.Invoke($"runtime integrity mismatch: {relative}");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 清单读不了：不凭空判定损坏，放行（安装流程的常规校验仍会兜底）。
            return true;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

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

    private ProcessStartInfo BuildPnpmBootstrapStartInfo(bool useRegistryMirror = false)
        => BuildPnpmBootstrapStartInfo(_packageDirectory, _packageToolsDirectory, useRegistryMirror);

    private ProcessStartInfo BuildPnpmBootstrapStartInfo(string runtimeDirectory, string toolsDirectory, bool useRegistryMirror = false)
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
        // cmd 从子进程自身 PATH 解析 npm：内置 Node 激活时其目录排在最前，
        // npm.cmd 与其内部 node 均来自内置 Node（系统 npm 的 shim 不会被选中）。
        ConfigureRuntimePath(startInfo, runtimeDirectory, toolsDirectory, ManagedNodeDirectory);
        if (useRegistryMirror)
        {
            startInfo.Environment["npm_config_registry"] = RegistryMirrorUrl;
        }

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

    private ProcessStartInfo BuildDshInstallStartInfo(string? targetVersion = null, bool useRegistryMirror = false)
        => BuildDshInstallStartInfo(_packageDirectory, _packageToolsDirectory, targetVersion, useRegistryMirror);

    private ProcessStartInfo BuildDshInstallStartInfo(string runtimeDirectory, string toolsDirectory, string? targetVersion = null, bool useRegistryMirror = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = NodeExecutable,
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        ConfigureRuntimePath(startInfo, runtimeDirectory, toolsDirectory, ManagedNodeDirectory);
        if (useRegistryMirror)
        {
            startInfo.Environment["npm_config_registry"] = RegistryMirrorUrl;
        }

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

    /// <summary>判定文本是否呈网络类失败特征（镜像重试的触发条件）。</summary>
    internal static bool LooksLikeNetworkFailure(string text) =>
        NetworkFailureTokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>判定是否呈 registry 同步滞后特征：ERESOLVE 叠加网络失败（如 fetch failed）。
    /// 上游发版后镜像元数据同步窗口内，官方源不可达 + 镜像解析失败同时出现——
    /// 隔一段时间重试即可通过（实测 0.1.7-rc.1 发版后 ~1.5h 出现、次日自愈）。</summary>
    internal static bool LooksLikeRegistrySyncLag(string text) =>
        text.Contains("ERESOLVE", StringComparison.Ordinal) && LooksLikeNetworkFailure(text);

    /// <summary>镜像偏好标记：镜像重试成功后持久化，之后所有安装直走镜像（跳过注定失败的首试超时）；
    /// 镜像失败而官方源成功时清除——网络环境变化（装了代理/换了网络）自动回到默认顺序。</summary>
    internal static bool IsRegistryMirrorPreferred(string markerPath) => File.Exists(markerPath);

    internal static void SetRegistryMirrorPreference(string markerPath)
    {
        try
        {
            File.WriteAllText(markerPath, "npmmirror preferred after official registry failure\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"registry mirror marker write failed: {ex.Message}");
        }
    }

    internal static void ClearRegistryMirrorPreference(string markerPath)
    {
        try
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"registry mirror marker delete failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 带镜像自适应的安装执行：按上次成功经验选择首发源（标记存在 → 镜像优先）；
    /// 首试失败且呈网络特征时换另一个源重试，并按结果更新偏好标记。
    /// 返回最终退出码；"安装是否成功"仍由调用方按退出码 + 期望文件综合判定。
    /// </summary>
    private async Task<int> RunInstallWithAdaptiveRegistryAsync(
        Func<bool, ProcessStartInfo> buildStartInfo, CancellationToken ct)
    {
        var preferMirror = IsRegistryMirrorPreferred(_registryMirrorMarkerPath);
        var first = await RunInstallProcessAsync(buildStartInfo(preferMirror), ct);
        if (first == 0)
        {
            return first;
        }

        if (!RecentLogLooksLikeNetworkFailure())
        {
            return first; // 非网络失败：换源无意义，原样上报。
        }

        AppendLog(preferMirror
            ? "--- registry mirror failed with network symptoms; retrying via official registry ---"
            : "--- retrying via registry mirror ---");
        var second = await RunInstallProcessAsync(buildStartInfo(!preferMirror), ct);
        if (second == 0)
        {
            if (preferMirror)
            {
                ClearRegistryMirrorPreference(_registryMirrorMarkerPath);
            }
            else
            {
                SetRegistryMirrorPreference(_registryMirrorMarkerPath);
            }

            return second;
        }

        // 双源皆败且呈同步滞后特征：多半撞上上游发版窗口（官方源不可达 + 镜像元数据未齐），
        // 等 60 秒让镜像追平后再试一次，通常即可通过。
        if (LooksLikeRegistrySyncLag(ReadRecentLogTail()))
        {
            AppendLog("--- registry sync lag detected (ERESOLVE + fetch failure); retrying in 60s ---");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException)
            {
                return second;
            }

            return await RunInstallProcessAsync(buildStartInfo(!preferMirror), ct);
        }

        return second;
    }

    /// <summary>读取服务日志尾部文本（同步滞后判定窗口，同失败分类口径）。</summary>
    private string ReadRecentLogTail()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return string.Empty;
            }

            return string.Join(Environment.NewLine, File.ReadLines(_logPath).TakeLast(40));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>服务日志尾部是否呈网络类失败特征（重试窗口覆盖最近 60 行）。</summary>
    private bool RecentLogLooksLikeNetworkFailure()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return false;
            }

            return File.ReadLines(_logPath).TakeLast(60).Any(LooksLikeNetworkFailure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>失败诊断：附带服务日志尾部，帮助用户定位（依赖/构建/配置等问题）；
    /// 能识别的失败信号额外附加人话提示（见 <see cref="ClassifyFailureHint"/>）。</summary>
    private string WithLogTail(string message)
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return message;
            }

            var lines = File.ReadLines(_logPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            var tail = lines.TakeLast(6)
                .Select(l => l.Length > 180 ? l[..180] + "…" : l)
                .ToArray();
            // 分类窗口比展示窗口宽：网络类报错常在更多输出之前。
            var hint = ClassifyFailureHint(string.Join("\n", lines.TakeLast(40)));
            var hintSuffix = hint is null ? string.Empty : $"\n（{hint}）";
            return tail.Length > 0
                ? $"{message}{hintSuffix}\n最近日志：\n{string.Join("\n", tail)}"
                : message + hintSuffix;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志读取失败不影响诊断主体。
            return message;
        }
    }

    /// <summary>把常见失败信号翻译成可操作的提示（借鉴官方桌面端的失败分类：端口占用给
    /// "退出其他实例"而不是诊断天书）。按特异性排序取第一个命中，未命中返回 null。</summary>
    internal static string? ClassifyFailureHint(string logText)
    {
        if (logText.Contains("EADDRINUSE", StringComparison.Ordinal))
        {
            return "端口被占用：可能有另一个 DSH 实例正在运行。请退出其他 DSH/DSH-Sharp 程序后重试启动";
        }

        if (logText.Contains("ENOSPC", StringComparison.Ordinal) ||
            logText.Contains("insufficient disk space", StringComparison.OrdinalIgnoreCase))
        {
            return "磁盘空间不足：请清理 %APPDATA%\\DSHSharp 所在磁盘后重试";
        }

        if (logText.Contains("ERR_UNKNOWN_BUILTIN_MODULE", StringComparison.Ordinal) ||
            logText.Contains("EBADENGINE", StringComparison.Ordinal))
        {
            return $"Node 版本低于私有 pnpm 的要求（≥ {MinimumNodeVersion}）：升级系统 Node，或重启客户端让环境预检自动下载内置 Node";
        }

        if (LooksLikeNetworkFailure(logText))
        {
            return "日志呈网络失败特征：请检查网络/代理后重试（客户端已自动尝试镜像链路）";
        }

        return null;
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
        if (!TryParseReadyUrl(line, out var uri)) return;
        _activeBaseUri = uri;
        _readyUrlSource?.TrySetResult(uri);
        Log?.Invoke($"captured private runtime URL: {uri}");
    }

    /// <summary>从输出行提取运行时 URL。DSH 0.1.5-rc.2 起同一行可能追加 " (LAN: …)" 后缀，
    /// 因此 URL 只取标记后的第一个空白分隔段，整行直接 Uri.TryCreate 会因空格失败。</summary>
    internal static bool TryParseReadyUrl(string line, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        const string marker = "dsh web: ";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var value = line[(index + marker.Length)..].Trim();
        var end = value.IndexOf(' ');
        if (end >= 0) value = value[..end];
        return Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https";
    }

    /// <summary>服务日志大小上限（2MB）：超过后轮转为 .old 保留一代，常驻运行不会无限增长
    /// （官方桌面端对 Host 诊断同样只保留尾部 64Ki 的纪律）。app.log 复用同一上限。</summary>
    public const long MaxServiceLogBytes = 2 * 1024 * 1024;

    /// <summary>超过上限时把日志轮转为 <c>&lt;logPath&gt;.old</c>（覆盖上一代）。轮转失败静默：
    /// 日志写入不受影响，下次追加再试。</summary>
    public static void RotateLogFile(string logPath, long maxBytes)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length < maxBytes)
            {
                return;
            }

            var rotated = logPath + ".old";
            if (File.Exists(rotated))
            {
                File.Delete(rotated);
            }

            info.MoveTo(rotated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 轮转失败不影响写日志。
        }
    }

    // stdout/stderr 两个 pump 任务与启动流程并发写日志：不加锁会在 FileShare.None 上撞 IOException 丢行。
    private readonly object _logWriteLock = new();

    private void AppendLog(string line)
    {
        try
        {
            lock (_logWriteLock)
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateLogFile(_logPath, MaxServiceLogBytes);
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响服务。
        }
    }
}
