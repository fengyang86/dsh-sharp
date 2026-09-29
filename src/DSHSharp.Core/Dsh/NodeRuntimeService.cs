using System.IO.Compression;
using System.Security.Cryptography;
using DSHSharp.Core.Compatibility;
using DSHSharp.Core.Services;

namespace DSHSharp.Core.Dsh;

/// <summary>内置 Node 下载/安装状态快照（UI 绑定用）。</summary>
public sealed record NodeRuntimeState
{
    /// <summary>状态机阶段：Idle / Downloading / Verifying / Extracting / Ready / Failed。</summary>
    public string Phase { get; init; } = "Idle";

    /// <summary>下载进度 0-100；仅 Downloading 阶段有效。</summary>
    public int DownloadPercent { get; init; }

    /// <summary>失败信息（Failed 阶段）。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 内置（便携版）Node 运行时的按需安装：
/// <list type="bullet">
/// <item>下载 nodejs.org 官方 win-x64 zip（npmmirror 镜像兜底，复用 HttpFallback 代理链，
/// .part 断点续传），用官方 SHASUMS256.txt 校验；</item>
/// <item>解压到 <c>&lt;root&gt;/node-v&lt;版本&gt;-win-x64/</c>，以 <c>.node-version</c> 标记当前版本，
/// 安装后执行 <c>node --version</c> 验证可执行；</item>
/// <item>只作为客户端子进程的叠加层（PATH 注入 + 显式路径），不改动用户系统的 Node。</item>
/// </list>
/// </summary>
public sealed class NodeRuntimeService
{
    /// <summary>
    /// 内置 Node 版本。选型约束（调整时逐条核对，见 VersionConsistencyTests）：
    /// <list type="bullet">
    /// <item>≥ 22.19：DSH 源码仓 engines 已要求 <c>^22.19.0 || &gt;=24.0.0</c>（发布包暂未带
    /// engines，提前满足避免踩坑）；同时覆盖私有 pnpm 11.25 的 <c>&gt;= 22.13</c>；</item>
    /// <item>保持 22.x 大版本：与 DSH 依赖树的原生模块（node-pty 预编译等）ABI 一致，
    /// 不经真机验证不跨大版本；</item>
    /// <item>取 22.x 最新维护版，跟随安全补丁。</item>
    /// </list>
    /// </summary>
    public const string NodeVersion = "22.23.2";

    /// <summary>发行版候选源：官方 dist → 国内镜像。</summary>
    internal static readonly string[] DownloadBaseUrls =
    [
        "https://nodejs.org/dist",
        "https://npmmirror.com/mirrors/node",
    ];

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>可选日志回调（由宿主注入）。</summary>
    public static Action<string>? Log { get; set; }

    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile NodeRuntimeState _state = new();

    /// <summary>当前状态快照。</summary>
    public NodeRuntimeState State => _state;

    /// <summary>状态变化事件（后台线程触发，调用方负责调度）。</summary>
    public event EventHandler<NodeRuntimeState>? StateChanged;

    public NodeRuntimeService(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
    }

    /// <summary>内置 Node 安装目录（zip 顶层目录名一致）。</summary>
    public string NodeDirectory => Path.Combine(_rootDirectory, $"node-v{NodeVersion}-win-x64");

    /// <summary>内置 node.exe 完整路径。</summary>
    public string NodeExePath => Path.Combine(NodeDirectory, "node.exe");

    private string MarkerPath => Path.Combine(_rootDirectory, ".node-version");

    private string ZipPath => Path.Combine(_rootDirectory, ZipFileName);

    private string StagingDirectory => Path.Combine(_rootDirectory, ".staging");

    internal static string ZipFileName => $"node-v{NodeVersion}-win-x64.zip";

    /// <summary>构造全部下载候选 URL（官方 + 镜像，按序尝试）。</summary>
    internal static IReadOnlyList<string> BuildDownloadCandidates() =>
        DownloadBaseUrls.Select(baseUrl => $"{baseUrl}/v{NodeVersion}/{ZipFileName}").ToList();

    /// <summary>从 SHASUMS256.txt 内容中解析目标文件的 sha256（格式：<c>&lt;hex&gt;␣␣&lt;文件名&gt;</c>）；找不到返回 null。</summary>
    internal static string? ParseShasumsEntry(string shasumsContent, string fileName)
    {
        foreach (var rawLine in shasumsContent.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var hash = line[..separator].Trim();
            var name = line[separator..].Trim();
            if (string.Equals(name, fileName, StringComparison.Ordinal) &&
                hash.Length == 64 && hash.All(char.IsAsciiHexDigit))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>标记版本一致且 node.exe 存在即视为已安装（只做文件检查，不执行）。</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                return File.Exists(NodeExePath) &&
                    string.Equals(File.ReadAllText(MarkerPath).Trim(), NodeVersion, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>作废当前安装标记（如 node.exe 存在但无法执行），下次 <see cref="EnsureAsync"/> 将重新下载。</summary>
    public void Invalidate()
    {
        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"node runtime marker invalidation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 确保内置 Node 已安装：未安装时执行 下载（断点续传）→ SHASUMS256 校验 → 解压 →
    /// 执行验证 → 写标记。已安装直接返回 true。任何一步失败置 Failed 并返回 false。
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
            SetState(_state with { Phase = "Failed", Error = "当前平台不支持自动下载内置 Node，请手动安装 Node ≥ 22.13" });
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            TryDelete(ZipPath); // 上次成功下载但安装未完成的残留：重新走完整流程。

            SetState(_state with { Phase = "Downloading", DownloadPercent = 0, Error = null });
            var expectedSha = await FetchExpectedSha256Async(ct);
            if (expectedSha is null)
            {
                SetState(_state with
                {
                    Phase = "Failed",
                    Error = "无法获取 Node 发行版校验和（nodejs.org 与镜像均不可达），已中止下载",
                });
                return false;
            }

            var downloaded = await DownloadFileAsync(ct);
            if (downloaded is null)
            {
                return false; // DownloadFileAsync 已设置失败状态。
            }

            SetState(_state with { Phase = "Verifying" });
            if (!VerifySha256(downloaded, expectedSha))
            {
                TryDelete(downloaded);
                TryDelete(downloaded + ".part");
                SetState(_state with { Phase = "Failed", Error = "内置 Node 安装包校验失败（SHA256 不匹配）" });
                return false;
            }

            SetState(_state with { Phase = "Extracting" });
            if (!TryDeleteDirectory(StagingDirectory) || !TryDeleteDirectory(NodeDirectory))
            {
                SetState(_state with { Phase = "Failed", Error = "无法清理内置 Node 旧目录；请关闭占用文件的程序后重试" });
                return false;
            }

            // zip 顶层目录即 node-v<版本>-win-x64：解压到 staging 后整体移动到位。
            ZipFile.ExtractToDirectory(downloaded, StagingDirectory, overwriteFiles: true);
            var extracted = Path.Combine(StagingDirectory, $"node-v{NodeVersion}-win-x64");
            if (!File.Exists(Path.Combine(extracted, "node.exe")))
            {
                SetState(_state with { Phase = "Failed", Error = "内置 Node 安装包内容不完整（缺少 node.exe）" });
                return false;
            }

            Directory.Move(extracted, NodeDirectory);

            var actualVersion = await NodeEnvironment.RunNodeVersionAsync(NodeExePath, ct);
            if (!string.Equals(actualVersion?.TrimStart('v'), NodeVersion, StringComparison.Ordinal))
            {
                // 杀软拦截/磁盘损坏会让 node.exe 无法执行：清掉安装，下次重试重新下载。
                TryDeleteDirectory(NodeDirectory);
                SetState(_state with { Phase = "Failed", Error = $"内置 Node 无法执行或版本不符（实际 {actualVersion ?? "无输出"}）" });
                return false;
            }

            File.WriteAllText(MarkerPath, NodeVersion, System.Text.Encoding.ASCII);
            CleanupSuperfluous();
            SetState(_state with { Phase = "Ready", DownloadPercent = 100, Error = null });
            Log?.Invoke($"managed node runtime ready: v{NodeVersion} ({NodeDirectory})");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetState(_state with { Phase = "Failed", Error = $"内置 Node 安装失败：{ex.Message}" });
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetState(NodeRuntimeState state)
    {
        _state = state;
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"node runtime state handler failed: {ex.Message}");
        }
    }

    /// <summary>逐源拉取 SHASUMS256.txt 并解析目标 zip 的 sha256；全部失败返回 null（无校验和不下载）。</summary>
    private static async Task<string?> FetchExpectedSha256Async(CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var baseUrl in DownloadBaseUrls)
        {
            try
            {
                using var fetched = await HttpFallback.GetAsync(
                    $"{baseUrl}/v{NodeVersion}/SHASUMS256.txt",
                    CheckTimeout,
                    userAgent: UserAgent,
                    ct: ct);
                using var response = fetched.Response;
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                if (ParseShasumsEntry(content, ZipFileName) is { } sha)
                {
                    return sha;
                }

                throw new InvalidDataException("SHASUMS256.txt 中没有对应条目");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                lastError = ex;
                Log?.Invoke($"shasums fetch failed ({baseUrl}): {ex.Message}");
            }
        }

        Log?.Invoke($"shasums fetch failed on all sources: {lastError?.Message}");
        return null;
    }

    /// <summary>断点续传下载：候选链逐一尝试，失败从已下载字节数续传（.part 文件）。
    /// 全链失败返回 null 并置 Failed（.part 保留供下次续用）。</summary>
    private async Task<string?> DownloadFileAsync(CancellationToken ct)
    {
        var partPath = ZipPath + ".part";
        Exception? lastError = null;

        foreach (var candidate in BuildDownloadCandidates())
        {
            try
            {
                var downloaded = await DownloadCandidateAsync(candidate, partPath, ct);
                if (downloaded is null)
                {
                    continue;
                }

                File.Move(partPath, ZipPath, overwrite: true);
                return ZipPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                lastError = ex;
                Log?.Invoke($"node download candidate failed ({candidate}): {ex.Message}，保留断点续传");
            }
        }

        TryDelete(partPath);
        SetState(_state with
        {
            Phase = "Failed",
            Error = $"内置 Node 下载失败（已尝试官方与镜像链路）：{lastError?.Message ?? "无可用链路"}",
        });
        return null;
    }

    private static string UserAgent => $"DSHSharp/{DshSharpCompatibility.ProductVersion}";

    /// <summary>单个候选 URL 的续传下载；失败抛出（.part 保留给下一候选续用）。总大小未知（SHASUMS 不含字节数），以 Content-Length 推算进度。</summary>
    private async Task<long?> DownloadCandidateAsync(string url, string partPath, CancellationToken ct)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var fetched = await HttpFallback.GetAsync(
            url, DownloadTimeout,
            userAgent: UserAgent,
            rangeFrom: existing > 0 ? existing : null,
            ct: ct);
        using var response = fetched.Response;
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        // 服务器忽略 Range（200 全量）或续传起点不符时，从头写。
        var resumed = (int)response.StatusCode == 206 &&
                      response.Content.Headers.ContentRange?.From == existing;
        var totalBytes = (response.Content.Headers.ContentLength ?? 0) + (resumed ? existing : 0);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            partPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long written = existing;
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

        if (totalBytes > 0 && written != totalBytes)
        {
            throw new IOException($"下载数据不完整（{written}/{totalBytes}）");
        }

        return written;
    }

    private static bool VerifySha256(string filePath, string expectedHex)
    {
        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(hash, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>清理旧版本目录、下载残留和 staging（失败不阻塞：下次安装会再清一次）。</summary>
    private void CleanupSuperfluous()
    {
        TryDelete(ZipPath);
        TryDeleteDirectory(StagingDirectory);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_rootDirectory, "node-v*"))
            {
                if (!string.Equals(directory, NodeDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"node runtime cleanup skipped: {ex.Message}");
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

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"node runtime directory cleanup failed: {path}: {ex.Message}");
            return false;
        }
    }
}
