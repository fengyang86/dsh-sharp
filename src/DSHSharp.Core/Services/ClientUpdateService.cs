using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DSHSharp.Core.Compatibility;

namespace DSHSharp.Core.Services;

/// <summary>客户端更新状态快照（UI 绑定用）。</summary>
public sealed record ClientUpdateState
{
    /// <summary>状态机阶段：Idle / Checking / UpToDate / Available / Downloading / Ready / CheckFailed / DownloadFailed。</summary>
    public string Phase { get; init; } = "Idle";

    /// <summary>检测到的最新版本（不含 v 前缀；如 0.2.3）。</summary>
    public string? LatestVersion { get; init; }

    /// <summary>更新说明（release notes 正文，可能为空）。</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>下载进度 0-100；仅 Downloading 阶段有效。</summary>
    public int DownloadPercent { get; init; }

    /// <summary>失败信息（CheckFailed / DownloadFailed 阶段）。</summary>
    public string? Error { get; init; }

    /// <summary>就绪的更新包（解压后的新版本目录），安装时使用。</summary>
    public string? StagingDirectory { get; init; }
}

/// <summary>
/// DSH-Sharp 客户端自更新服务：
/// <list type="bullet">
/// <item>检查：GitHub Releases latest（不含 pre-release），与 ProductVersion 比较；</item>
/// <item>预下载：后台下载 win-x64 zip 到 %APPDATA%/DSHSharp/update-staging，报告百分比进度，
/// SHA256 校验（GitHub asset digest）后解压到 staging/app；</item>
/// <item>安装：由 <c>DSHSharp.exe --apply-update</c> 自安装模式完成（等旧进程退出 → 覆盖安装目录 → 重启）。</item>
/// </list>
/// 网络不可达时静默降级为 CheckFailed，不影响正常使用；安装动作始终由用户显式触发。
/// </summary>
public sealed class ClientUpdateService
{
    /// <summary>更新包所在 GitHub 仓库（owner/name）。</summary>
    public const string UpdateRepository = "fengyang86/dsh-sharp";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>可选日志回调。</summary>
    public static Action<string>? Log { get; set; }

    private readonly string _stagingRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ClientUpdateState _state = new();
    private volatile bool _downloadRequested;

    /// <summary>当前状态快照。</summary>
    public ClientUpdateState State => _state;

    /// <summary>状态变化事件（后台线程触发，调用方负责调度）。</summary>
    public event EventHandler<ClientUpdateState>? StateChanged;

    public ClientUpdateService(string? stagingRoot = null)
    {
        _stagingRoot = stagingRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSHSharp", "update-staging");
    }

    private void SetState(ClientUpdateState state)
    {
        _state = state;
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"client update state handler failed: {ex.Message}");
        }
    }

    /// <summary>恢复到初始状态（安装完成后调用；保留已就绪 staging 不受影响）。</summary>
    public void Reset() => SetState(new ClientUpdateState());

    /// <summary>是否已有就绪待安装的更新包。</summary>
    public bool IsReady => _state.Phase == "Ready" && _state.StagingDirectory is not null;

    /// <summary>
    /// 检查 GitHub 最新 release。当前版本已是最新时进入 UpToDate；
    /// 有新版本时进入 Available 并自动开始后台预下载（同一版本只自动下载一次）。
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            SetState(_state with { Phase = "Checking", Error = null });
            var release = await FetchLatestReleaseAsync(ct);
            if (release is null)
            {
                SetState(_state with { Phase = "CheckFailed", Error = "无法访问 GitHub Releases（网络不可达）" });
                return;
            }

            var latest = release.Value.Tag.TrimStart('v');
            if (!IsNewerThanCurrent(latest))
            {
                SetState(_state with { Phase = "UpToDate", LatestVersion = latest });
                return;
            }

            SetState(_state with { Phase = "Available", LatestVersion = latest, ReleaseNotes = release.Value.Notes });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            SetState(_state with { Phase = "CheckFailed", Error = $"检查更新失败：{ex.Message}" });
        }
        finally
        {
            _gate.Release();
        }

        // 检测到新版本且用户未手动取消过：自动后台预下载（网络不可用会停在 DownloadFailed，可手动重试）。
        if (_state.Phase == "Available" && !_downloadRequested)
        {
            _downloadRequested = true;
            _ = Task.Run(() => DownloadAsync(CancellationToken.None));
        }
    }

    /// <summary>手动开始/重试下载（不依赖自动下载标记）。</summary>
    public Task DownloadAsync(CancellationToken ct = default)
    {
        _downloadRequested = true;
        return DownloadCoreAsync(ct);
    }

    private async Task DownloadCoreAsync(CancellationToken ct)
    {
        if (_state.LatestVersion is null)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var release = await FetchLatestReleaseAsync(ct);
            if (release is null)
            {
                SetState(_state with { Phase = "DownloadFailed", Error = "无法访问 GitHub Releases（网络不可达）" });
                return;
            }

            if (release.Value.Asset is not { } asset)
            {
                SetState(_state with { Phase = "DownloadFailed", Error = "Release 中没有 win-x64 更新包" });
                return;
            }

            Directory.CreateDirectory(_stagingRoot);
            var zipPath = Path.Combine(_stagingRoot, $"DSHSharp-v{release.Value.Tag}-win-x64.zip");
            var downloaded = await DownloadFileAsync(asset.DownloadUrl, zipPath, asset.SizeBytes, ct);
            if (downloaded is null)
            {
                return; // DownloadFileAsync 已设置失败状态。
            }

            if (asset.Sha256 is { } expected &&
                !VerifySha256(downloaded, expected, out var actual))
            {
                TryDelete(zipPath);
                SetState(_state with { Phase = "DownloadFailed", Error = $"更新包校验失败（SHA256 不匹配：{actual}）" });
                return;
            }

            // 校验通过后解压：zip 根目录即发布物内容。
            var appDir = Path.Combine(_stagingRoot, "app");
            if (Directory.Exists(appDir))
            {
                Directory.Delete(appDir, recursive: true);
            }

            ZipFile.ExtractToDirectory(downloaded, appDir, overwriteFiles: true);
            TryDelete(downloaded);
            SetState(_state with { Phase = "Ready", DownloadPercent = 100, StagingDirectory = appDir });
            Log?.Invoke($"client update package ready: v{release.Value.Tag}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or InvalidDataException)
        {
            SetState(_state with { Phase = "DownloadFailed", Error = $"下载更新失败：{ex.Message}" });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>比较 release 标签与当前 ProductVersion；无法解析或相同/更旧时返回 false。容忍 v 前缀。</summary>
    internal static bool IsNewerThanCurrent(string? latest)
    {
        var normalized = latest?.Trim().TrimStart('v');
        return Version.TryParse(normalized, out var latestVer)
            && Version.TryParse(DshSharpCompatibility.ProductVersion, out var currentVer)
            && latestVer > currentVer;
    }

    // ---- GitHub Releases ----

    private static async Task<(
        string Tag,
        string Notes,
        (string DownloadUrl, long SizeBytes, string? Sha256)? Asset)?> FetchLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            var requestUri = $"https://api.github.com/repos/{UpdateRepository}/releases/latest";
            var userAgent = $"DSHSharp/{DshSharpCompatibility.ProductVersion}";
            // GitHub API 必须携带 User-Agent；回退链：直连 → 系统代理 → git 配置代理 → 常见本地代理端口。
            using var response = await HttpFallback.GetAsync(requestUri, CheckTimeout, userAgent, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log?.Invoke($"release check failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagEl))
            {
                return null;
            }

            (string, long, string?)? asset = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in assets.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameEl) &&
                        nameEl.GetString()?.Contains("win-x64", StringComparison.OrdinalIgnoreCase) == true &&
                        item.TryGetProperty("browser_download_url", out var urlEl))
                    {
                        var size = item.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                            ? sizeEl.GetInt64()
                            : 0L;
                        string? sha = null;
                        if (item.TryGetProperty("digest", out var digestEl) &&
                            digestEl.GetString() is { } digest &&
                            digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            sha = digest["sha256:".Length..];
                        }

                        asset = (urlEl.GetString()!, size, sha);
                        break;
                    }
                }
            }

            return (
                tagEl.GetString() ?? string.Empty,
                root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty,
                asset);
        }
        catch (HttpRequestException ex)
        {
            Log?.Invoke($"release check failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> DownloadFileAsync(string url, string targetPath, long totalBytes, CancellationToken ct)
    {
        using var response = await HttpFallback.GetAsync(url, DownloadTimeout, userAgent: $"DSHSharp/{DshSharpCompatibility.ProductVersion}", ct: ct);
        if (!response.IsSuccessStatusCode)
        {
            SetState(_state with { Phase = "DownloadFailed", Error = $"下载失败：HTTP {(int)response.StatusCode}" });
            return null;
        }

        if (totalBytes <= 0)
        {
            totalBytes = response.Content.Headers.ContentLength ?? 0;
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
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

        return written > 0 ? targetPath : null;
    }

    /// <summary>创建 GitHub 访问客户端（保留供诊断/测试使用；运行时走 <see cref="HttpFallback"/> 回退链）。</summary>
    internal static HttpClient CreateHttpClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        UseProxy = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = timeout,
    })
    {
        Timeout = timeout,
    };

    private static bool VerifySha256(string filePath, string expectedHex, out string actualHex)
    {
        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        actualHex = hash;
        return string.Equals(hash, expectedHex.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>清理 staging（安装成功后由新实例或下次启动调用）。</summary>
    public void CleanupStaging()
    {
        try
        {
            if (Directory.Exists(_stagingRoot))
            {
                Directory.Delete(_stagingRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"update staging cleanup skipped: {ex.Message}");
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
            // 下载残留不影响下次覆盖写入。
        }
    }
}
