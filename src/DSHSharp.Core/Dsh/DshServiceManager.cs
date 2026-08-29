using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NpxReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SourceReadyTimeout = TimeSpan.FromSeconds(180); // tsx 冷启动实测 90-150s
    private const string ManagedPackageName = "@deepseek-ai/dsh";
    private const string ManagedPnpmSpec = "pnpm@11.19.0";

    private readonly Uri _baseUri;
    private Uri _activeBaseUri;
    private readonly ManagedMode _mode;
    private readonly string? _sourcePath;
    private readonly string _logPath;
    private readonly string _packageDirectory;
    private readonly string _packageToolsDirectory;
    private readonly string _dshHomeDirectory;
    private readonly string _bundledShortcutPluginDirectory;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private bool _disposed;

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
        _packageToolsDirectory = Path.Combine(_packageDirectory, ".tools");
        _dshHomeDirectory = Path.Combine(dir, "dsh-home");
        _bundledShortcutPluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins", "dsh-sharp-session");
    }

    /// <summary>可选的日志回调（由宿主注入）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>托管模式。</summary>
    public ManagedMode Mode => _mode;

    /// <summary>本次运行实际连接的地址。端口由托管进程成功绑定后确定。</summary>
    public string ActiveBaseUrl => _activeBaseUri.GetLeftPart(UriPartial.Authority);

    /// <summary>私有 DSH_HOME。会话、profile、插件和凭据不会与外部 DSH 共用。</summary>
    public string DshHomeDirectory => _dshHomeDirectory;

    /// <summary>是否持有托管进程（仅本客户端拉起的服务）。</summary>
    public bool IsOwned => _process is { HasExited: false };

    /// <summary>最近一次启动失败原因（成功启动后清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>私有 npm 包当前安装的版本；未安装或元数据损坏时返回 null。</summary>
    public string? InstalledPackageVersion => ReadInstalledPackageVersion();

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
        => RunProfilePackageEditAsync(name, active ? "activate" : "deactivate", ct);

    public Task<bool> RemovePluginAsync(string name, CancellationToken ct = default)
        => RunProfilePackageEditAsync(name, "remove", ct);

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
                var psi = BuildStartInfo(port);
                Log?.Invoke($"starting private runtime: {psi.FileName} {psi.Arguments} (port={port})");
                AppendLog($"--- DSH private runtime start (port={port}) ---");
                var process = Process.Start(psi);
                if (process is null)
                {
                    continue;
                }

                _process = process;
                process.EnableRaisingEvents = true;
                process.Exited += OnProcessExited;
                PumpOutput(process);
                var deadline = DateTime.UtcNow + NpxReadyTimeout;
                while (DateTime.UtcNow < deadline && !process.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    if (await ProbeAsync(_activeBaseUri, ct))
                    {
                        Log?.Invoke($"private runtime ready: {ActiveBaseUrl}");
                        return true;
                    }
                }

                KillOwnedProcess();
            }

            LastError = WithLogTail("私有 DSH Runtime 未能启动：3080 及备用端口均不可用或服务未就绪");
            Log?.Invoke($"managed start failed: {LastError}");
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
        privateStartInfo.ArgumentList.Add(PrivateDshEntryPath);
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
            ApplyPrivateEnvironment(startInfo);
            ConfigurePrivatePnpmPath(startInfo);
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
            ApplyPrivateEnvironment(startInfo);
            ConfigurePrivatePnpmPath(startInfo);
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
                await File.WriteAllTextAsync(profileManifest, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

                if (!await VerifyProfileCompositionAsync(ct))
                {
                    await File.WriteAllTextAsync(profileManifest, originalManifest, ct);
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
        startInfo.ArgumentList.Add(PrivateDshEntryPath); startInfo.ArgumentList.Add("plugin");
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
        startInfo.ArgumentList.Add(PrivateDshEntryPath);
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
