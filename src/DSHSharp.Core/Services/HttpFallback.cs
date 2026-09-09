using System.Diagnostics;
using System.Net;

namespace DSHSharp.Core.Services;

/// <summary>
/// 受限网络环境下的外网 HTTP 访问回退链：
/// 直连 → 系统代理 → git 配置代理 → 环境变量代理 → 常见本地代理端口。
/// 任一链路成功后缓存复用；全部失败抛出最后一次异常。仅用于 GitHub/npm 等外网端点，
/// DSH 本地服务直连不走此通道。
/// </summary>
public static class HttpFallback
{
    private static readonly string[] CommonLocalProxies =
    [
        "http://127.0.0.1:7890",  // Clash
        "http://127.0.0.1:7897",  // Clash Verge
        "http://127.0.0.1:10809", // V2RayN HTTP
        "http://127.0.0.1:7993",  // 常见自定义端口
    ];

    private static int _cachedIndex = -1;
    private static string? _gitProxy;

    private static List<string?> BuildProxyCandidates()
    {
        var candidates = new List<string?> { null /*直连*/, "#system" };
        if (_gitProxy is null)
        {
            _gitProxy = ReadGitProxy();
        }

        if (_gitProxy is not null)
        {
            candidates.Add(_gitProxy);
        }

        foreach (var env in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.Add(value);
            }
        }

        candidates.AddRange(CommonLocalProxies);
        return candidates.Distinct().ToList();
    }

    private static string? ReadGitProxy()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "config --global --get http.proxy")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (git is null)
            {
                return null;
            }

            var output = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit(3000);
            return output.Length > 0 && output.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static HttpClient Create(TimeSpan timeout, string? proxySpec)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = timeout,
        };
        handler.UseProxy = proxySpec switch
        {
            null => false,
            "#system" => true,
            _ => true,
        };
        if (proxySpec is not null && proxySpec != "#system")
        {
            handler.Proxy = new WebProxy(proxySpec);
        }

        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>按回退链 GET；返回响应（含已缓存的可用链路）。全部失败抛最后一次异常。</summary>
    public static async Task<HttpResponseMessage> GetAsync(string url, TimeSpan timeout, string? userAgent = null, CancellationToken ct = default)
    {
        var candidates = BuildProxyCandidates();
        var start = _cachedIndex >= 0 && _cachedIndex < candidates.Count ? _cachedIndex : 0;
        Exception? lastError = null;
        for (var offset = 0; offset < candidates.Count; offset++)
        {
            var index = (start + offset) % candidates.Count;
            var proxySpec = candidates[index];
            try
            {
                var http = Create(timeout, proxySpec);
                HttpResponseMessage response;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (userAgent is not null)
                    {
                        request.Headers.UserAgent.ParseAdd(userAgent);
                    }

                    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch
                {
                    http.Dispose();
                    throw;
                }

                Volatile.Write(ref _cachedIndex, index);
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new HttpRequestException("所有网络链路均不可用");
    }
}
