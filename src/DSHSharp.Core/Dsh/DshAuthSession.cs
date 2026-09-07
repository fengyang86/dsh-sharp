using System.Net;

namespace DSHSharp.Core.Dsh;

/// <summary>
/// DSH 0.1.2+ 浏览器认证会话。
/// Runtime 启动 URL 中的 token 只能在根路径交换一次：GET /?token=… 返回 303 与
/// 绑定 host:port 的签名 cookie；此后所有 HTTP RPC 与 WebSocket 只认 cookie，
/// query token 与 Authorization 头均被拒绝（见 dsh-client-connection 官方文档）。
/// 旧版 runtime（启动 URL 无 token）直接直连，无需兑换。
/// </summary>
public sealed class DshAuthSession
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _token;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private CookieContainer? _cookies;
    private volatile bool _authenticated;

    public DshAuthSession(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        Origin = new UriBuilder(uri) { Query = null, Fragment = null, Path = "/" }.Uri;
        var query = uri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == "token")
                {
                    _token = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    break;
                }
            }
        }
    }

    /// <summary>不含 token 的服务根地址（如 http://127.0.0.1:3081/）。</summary>
    public Uri Origin { get; }

    /// <summary>启动 URL 携带的认证令牌；旧版 runtime 无令牌时为 null。</summary>
    public string? Token => _token;

    /// <summary>是否已获得认证 cookie（无 token 的旧版 runtime 视为直连可用）。</summary>
    public bool IsAuthenticated => _authenticated;

    /// <summary>认证 cookie 容器；仅在 <see cref="EnsureAuthenticatedAsync"/> 成功后非空。</summary>
    public CookieContainer? Cookies => _cookies;

    /// <summary>
    /// 确保 cookie 就绪：GET /?token=… 并接收 Set-Cookie。
    /// 并发调用只会产生一次实际交换；失败时下一次调用重试。
    /// </summary>
    public async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        if (_token is null)
        {
            // 旧版 runtime 无认证层。
            _authenticated = true;
            return true;
        }

        if (_authenticated && _cookies is not null)
        {
            return true;
        }

        await _exchangeLock.WaitAsync(ct);
        try
        {
            if (_authenticated && _cookies is not null)
            {
                return true;
            }

            var container = new CookieContainer();
            using var http = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                CookieContainer = container,
                AllowAutoRedirect = false,
            })
            {
                Timeout = ExchangeTimeout,
            };

            var exchangeUri = new Uri(Origin, $"/?token={Uri.EscapeDataString(_token)}");
            using var response = await http.GetAsync(exchangeUri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.Content.Dispose();

            // 期望 303 + Set-Cookie；宽容接受任意带认证 cookie 的响应。
            if (container.GetCookies(Origin).Count == 0)
            {
                return false;
            }

            _cookies = container;
            _authenticated = true;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    /// <summary>创建携带认证 cookie、不走系统代理的 HTTP handler。</summary>
    /// <remarks>必须先通过 <see cref="EnsureAuthenticatedAsync"/>，否则返回的 handler 不带 cookie。</remarks>
    public HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseProxy = false,
        CookieContainer = _cookies ?? new CookieContainer(),
        AllowAutoRedirect = false,
    };

    /// <summary>丢弃当前认证状态（runtime 重启、端口漂移或收到 401 后重新交换）。</summary>
    public void Reset()
    {
        _cookies = null;
        _authenticated = false;
    }
}
