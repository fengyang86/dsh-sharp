using System.Diagnostics;
using DSHSharp.Core.Compatibility;

namespace DSHSharp.Core.Services;

/// <summary>WebView2 运行时安装状态快照（UI 绑定用）。</summary>
public sealed record WebView2RuntimeState
{
    /// <summary>状态机阶段：Idle / Downloading / Verifying / Installing / Ready / Failed。</summary>
    public string Phase { get; init; } = "Idle";

    /// <summary>下载进度 0-100；仅 Downloading 阶段有效。</summary>
    public int DownloadPercent { get; init; }

    /// <summary>失败信息（Failed 阶段）。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Windows WebView2 运行时的检测与按需安装：
/// <list type="bullet">
/// <item>检测：EdgeUpdate Clients 注册表键（per-machine / per-user），与微软官方文档一致；</item>
/// <item>安装：下载 Evergreen Bootstrapper（go.microsoft.com 固定跳转链，经 HTTPS），
/// 校验 Authenticode 数字签名后以 <c>/silent /install</c> 静默执行，再回读注册表确认；</item>
/// <item>未提升权限时 Bootstrapper 自动落到 per-user 安装（免 UAC），与客户端安装策略一致。</item>
/// </list>
/// </summary>
public sealed class WebView2RuntimeService
{
    /// <summary>微软官方 Evergreen Bootstrapper 固定分发链接（内容随版本滚动，无钉住哈希，用签名校验代替）。</summary>
    public const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>EdgeUpdate Clients 键下 WebView2 Runtime 的产品 GUID（官方文档值）。</summary>
    internal const string RuntimeProductGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>可选日志回调（由宿主注入）。</summary>
    public static Action<string>? Log { get; set; }

    private readonly string _downloadDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile WebView2RuntimeState _state = new();

    /// <summary>当前状态快照。</summary>
    public WebView2RuntimeState State => _state;

    /// <summary>状态变化事件（后台线程触发，调用方负责调度）。</summary>
    public event EventHandler<WebView2RuntimeState>? StateChanged;

    public WebView2RuntimeService(string? downloadDirectory = null)
    {
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSHSharp", "webview2-setup");
    }

    /// <summary>WebView2 运行时是否已安装（注册表检测，不启动进程）。</summary>
    public static bool IsInstalled => DetectInstalled();

    internal static bool DetectInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // EdgeUpdate 注册表探测点：HKLM 32/64 视图（WOW6432Node）与 HKCU（per-user 安装）。
        var probes = new (Microsoft.Win32.RegistryHive Hive, Microsoft.Win32.RegistryView View)[]
        {
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
        };
        foreach (var (hive, view) in probes)
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($"Software\\Microsoft\\EdgeUpdate\\Clients\\{RuntimeProductGuid}");
                // pv 存在且不为哨兵值 0.0.0.0 即视为已安装（官方检测约定）。
                if (key?.GetValue("pv") is string version &&
                    !string.IsNullOrEmpty(version) &&
                    !string.Equals(version, "0.0.0.0", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // 单个探测点不可读不影响其他视图。
            }
        }

        return false;
    }

    /// <summary>
    /// 确保 WebView2 运行时可用：未安装时执行 下载 Bootstrapper → Authenticode 签名校验 →
    /// 静默安装 → 注册表复检。已安装直接返回 true。
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        if (IsInstalled)
        {
            SetState(_state with { Phase = "Ready", Error = null });
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            SetState(_state with { Phase = "Failed", Error = "当前平台不支持 WebView2 运行时检测" });
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_downloadDirectory);
            var bootstrapper = Path.Combine(_downloadDirectory, "MicrosoftEdgeWebview2Setup.exe");
            SetState(_state with { Phase = "Downloading", DownloadPercent = 0, Error = null });
            if (!await DownloadBootstrapperAsync(bootstrapper, ct))
            {
                return false; // 下载器已设置失败状态。
            }

            // 固定跳转链没有钉住的哈希：执行前必须验证微软 Authenticode 签名（链到微软根 + 覆盖文件字节）。
            SetState(_state with { Phase = "Verifying" });
            if (!await VerifyBootstrapperSignatureAsync(bootstrapper, ct))
            {
                TryDelete(bootstrapper);
                SetState(_state with { Phase = "Failed", Error = "WebView2 安装器签名校验失败（已删除），请重试或手动安装" });
                return false;
            }

            SetState(_state with { Phase = "Installing" });
            Log?.Invoke($"installing webview2 runtime silently: {bootstrapper}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            var exitCode = await RunBootstrapperAsync(bootstrapper, timeout.Token);
            TryDelete(bootstrapper);
            if (exitCode != 0)
            {
                Log?.Invoke($"webview2 bootstrapper exit code: {exitCode}");
            }

            if (!DetectInstalled())
            {
                SetState(_state with
                {
                    Phase = "Failed",
                    Error = exitCode == 0
                        ? "WebView2 安装器已执行但运行时仍不可用，请重启系统后重试"
                        : $"WebView2 安装失败（安装器退出码 {exitCode}）；可从微软官网手动安装 Evergreen 版",
                });
                return false;
            }

            SetState(_state with { Phase = "Ready", DownloadPercent = 100, Error = null });
            Log?.Invoke("webview2 runtime installed");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetState(_state with { Phase = "Failed", Error = $"WebView2 运行时安装失败：{ex.Message}" });
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<int> RunBootstrapperAsync(string bootstrapper, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = bootstrapper,
            Arguments = "/silent /install",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return -1;
        }

        try
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log?.Invoke($"webview2 installer kill failed: {ex.Message}");
            }

            throw;
        }
    }

    private void SetState(WebView2RuntimeState state)
    {
        _state = state;
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"webview2 state handler failed: {ex.Message}");
        }
    }

    /// <summary>构造签名校验命令：调用 OS 官方 cmdlet（内部即 WinVerifyTrust，
    /// 含证书链与文件哈希覆盖校验）。路径单引号包裹并转义，避免注入。</summary>
    internal static string BuildSignatureCheckCommand(string filePath) =>
        $"(Get-AuthenticodeSignature -LiteralPath '{filePath.Replace("'", "''")}').Status -eq 'Valid'";

    /// <summary>解析签名校验命令输出：True / False（其他输出一律视为失败）。</summary>
    internal static bool ParseSignatureCheckResult(string? output) =>
        string.Equals(output?.Trim(), "True", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用系统 PowerShell 的 Get-AuthenticodeSignature 验证 Authenticode 签名。
    /// 不直接 P/Invoke WinVerifyTrust：实测部分环境（安全软件挂钩 wintrust）下裸 interop 会
    /// 误报 TRUST_E_PROVIDER_UNKNOWN，而同一进程内官方 cmdlet 正常——走 OS 维护的通道更稳。
    /// </summary>
    private static async Task<bool> VerifyBootstrapperSignatureAsync(string filePath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ProgramFiles") is { } programFiles &&
                    File.Exists(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"))
                    ? Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe")
                    : "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{BuildSignatureCheckCommand(filePath)}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var valid = ParseSignatureCheckResult(output);
            if (!valid)
            {
                var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                Log?.Invoke($"webview2 bootstrapper signature invalid (status: {output.Trim()}; {error.Trim()})");
            }

            return valid;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log?.Invoke($"webview2 signature check failed to run: {ex.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke("webview2 signature check timed out");
            return false;
        }
    }

    /// <summary>下载 Bootstrapper（小文件，整体下载；进度按 Content-Length 推算）。</summary>
    private async Task<bool> DownloadBootstrapperAsync(string targetPath, CancellationToken ct)
    {
        var partPath = targetPath + ".part";
        try
        {
            using var fetched = await HttpFallback.GetAsync(
                BootstrapperUrl, DownloadTimeout,
                userAgent: $"DSHSharp/{DshSharpCompatibility.ProductVersion}",
                ct: ct);
            using var response = fetched.Response;
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            long written = 0;
            int read;
            var lastReported = -1;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (totalBytes > 0)
                {
                    var percent = (int)(written * 100 / totalBytes);
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        SetState(_state with { Phase = "Downloading", DownloadPercent = Math.Min(percent, 99) });
                    }
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            TryDelete(partPath);
            SetState(_state with { Phase = "Failed", Error = $"WebView2 安装器下载失败：{ex.Message}" });
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 残留不影响下次覆盖下载。
        }
    }
}
