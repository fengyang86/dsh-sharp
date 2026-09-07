using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DSHSharp.Core.Dsh;

/// <summary>会话完成事件参数。</summary>
/// <param name="AsOfSeq">会话投影最新事件序号（session/page 的 throughSeq 上界；未知为 0）。</param>
public sealed class SessionCompletedEventArgs(string sessionId, string? title, long asOfSeq) : EventArgs
{
    public string SessionId { get; } = sessionId;

    /// <summary>会话标题（来自事件流缓存，可能为空）。</summary>
    public string? Title { get; } = title;

    /// <summary>会话最新事件序号；0 表示未知，调用方需另行查询。</summary>
    public long AsOfSeq { get; } = asOfSeq;
}

/// <summary>DSH 服务在线状态变化事件参数。</summary>
public sealed class ServiceAvailabilityEventArgs(bool isOnline) : EventArgs
{
    public bool IsOnline { get; } = isOnline;
}

/// <summary>
/// DSH 服务事件监控：
/// <list type="bullet">
/// <item>DSH 0.1.2+：<c>/api/remote.mux</c> 逻辑流（open $events → ready → emit 项），
/// 会话完成由 <c>api-session/status</c> 与 <c>api-session/added</c> 的 running true→false 边沿判定；</item>
/// <item>旧版：<c>/api/events.host</c>/<c>events.mux</c> WebSocket 帧解析（自动回退）；</item>
/// <item>HTTP 心跳：探测服务在线状态。</item>
/// </list>
/// 流与心跳相互独立，各自断线重连（指数退避）。
/// 不依赖 UI 线程：事件在后台线程触发，调用方负责调度。
/// </summary>
public sealed class DshEventMonitor : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BackoffMin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MuxExistsProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>可选的日志回调（由宿主注入，便于排查）。</summary>
    public static Action<string>? Log { get; set; }

    private readonly DshAuthSession _auth;
    private readonly string _wsScheme;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, bool> _runningSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionTitles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sessionSeqs = new(StringComparer.Ordinal);
    private Task? _muxTask;
    private Task? _heartbeatTask;
    private int _started;
    private int _disposed;
    private bool? _remoteMuxAvailable;

    public DshEventMonitor(string baseUrl)
        : this(new DshAuthSession(baseUrl))
    {
    }

    public DshEventMonitor(DshAuthSession auth)
    {
        _auth = auth;
        _wsScheme = auth.Origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
    }

    /// <summary>会话回合完成（新协议 running true→false 边沿；旧协议 turn/end + reason.kind=completed）。</summary>
    public event EventHandler<SessionCompletedEventArgs>? SessionCompleted;

    /// <summary>DSH 服务在线状态变化（心跳探测）。</summary>
    public event EventHandler<ServiceAvailabilityEventArgs>? ServiceAvailabilityChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        _muxTask = Task.Run(() => RunStreamLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 同步取消并释放（进程退出路径使用）：只发出取消信号，不等待后台任务，
    /// 避免在 UI 线程上同步等待导致死锁；残留任务随进程退出自然终止。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Log?.Invoke("DshEventMonitor.Dispose: cancelling");
        _cts.Cancel();
        Log?.Invoke("DshEventMonitor.Dispose: done");
    }

    public async ValueTask DisposeAsync()
    {
        var disposeNow = Interlocked.Exchange(ref _disposed, 1) == 0;
        Log?.Invoke("DshEventMonitor.DisposeAsync: cancelling");
        if (disposeNow)
        {
            _cts.Cancel();
        }
        try
        {
            var tasks = new[] { _muxTask, _heartbeatTask }
                .Where(t => t is not null)
                .Cast<Task>()
                .ToArray();
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // 退出路径：忽略。
        }

        Log?.Invoke("DshEventMonitor.DisposeAsync: disposing cts");
        _cts.Dispose();
        Log?.Invoke("DshEventMonitor.DisposeAsync: done");
    }

    private async Task RunStreamLoopAsync(CancellationToken ct)
    {
        var backoff = BackoffMin;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ConnectStreamAsync(ws, ct);
                backoff = BackoffMin;
                await PumpStreamAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                // 服务未就绪、认证失效或连接被重置：退避后重连；认证会话在下次连接前重新交换。
                if (_auth.Token is not null)
                {
                    _auth.Reset();
                }

                Log?.Invoke($"event stream disconnected: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"event stream failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, BackoffMax.Ticks));
        }
    }

    /// <summary>建立事件流连接：优先 DSH 0.1.2+ 的 remote.mux 逻辑流，旧版回退 events.mux。</summary>
    private async Task ConnectStreamAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var useRemoteMux = _remoteMuxAvailable ?? await ProbeRemoteMuxAsync(ct);
        _remoteMuxAvailable = useRemoteMux;
        if (!useRemoteMux)
        {
            await ws.ConnectAsync(StreamUri("api/events.mux"), ct);
            return;
        }

        if (_auth.Token is not null && !await _auth.EnsureAuthenticatedAsync(ct))
        {
            // 事件流 WebSocket 只认 cookie；旧 cookie 可能随 runtime 重启失效，重置后仍失败则抛出走退避重连。
            throw new WebSocketException("authentication cookie exchange failed");
        }

        if (_auth.Cookies is not null)
        {
            ws.Options.Cookies = _auth.Cookies;
        }

        await ws.ConnectAsync(StreamUri("api/remote.mux"), ct);
        _remoteMuxAvailable = true;

        // 打开 $events 逻辑流；服务端先回 ready 项再推送事件。
        var open = JsonSerializer.Serialize(new
        {
            type = "open",
            streamId = Guid.NewGuid().ToString("N"),
            endpoint = "$events",
            payload = new { args = new { } },
        });
        var bytes = Encoding.UTF8.GetBytes(open);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    /// <summary>探测 remote.mux 端点是否存在：HTTP 404 视为旧版 runtime（无该端点）。</summary>
    private async Task<bool> ProbeRemoteMuxAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(_auth.CreateHandler())
            {
                Timeout = MuxExistsProbeTimeout,
            };
            using var response = await http.GetAsync(new Uri(_auth.Origin, "api/remote.mux"), ct);
            // 新版对普通 GET 返回非 404（如升级要求错误）；旧版路由未认领返回 404。
            return response.StatusCode != System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // 服务未就绪：默认按新版协议尝试连接，失败由退避重连兜底。
            return true;
        }
    }

    private async Task PumpStreamAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var received = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            received.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                received.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
            HandleFrame(json, remoteMux: _remoteMuxAvailable == true);
        }
    }

    private void HandleFrame(string json, bool remoteMux)
    {
        if (remoteMux)
        {
            HandleMuxFrame(json);
            return;
        }

        // 旧版 events.mux：session/title 缓存标题，turn/end + completed 触发完成。
        var title = DshFrameParser.TryParseSessionTitle(json);
        if (title is not null)
        {
            _sessionTitles[title.Value.SessionId] = title.Value.Title;
        }

        var turnEnd = DshFrameParser.TryParseTurnEnd(json);
        if (turnEnd is not null &&
            string.Equals(turnEnd.Value.ReasonKind, "completed", StringComparison.Ordinal))
        {
            RaiseSessionCompleted(turnEnd.Value.SessionId);
        }
    }

    /// <summary>处理 remote.mux 逻辑流的 item/end/error 帧。</summary>
    private void HandleMuxFrame(string json)
    {
        var status = DshFrameParser.TryParseSessionStatus(json);
        if (status is not null)
        {
            ApplyRunningState(status.Value.SessionId, status.Value.Running);
            return;
        }

        var added = DshFrameParser.TryParseSessionAdded(json);
        if (added is not null)
        {
            if (added.Value.Title is { } title)
            {
                _sessionTitles[added.Value.SessionId] = title;
            }

            if (added.Value.AsOfSeq > 0)
            {
                _sessionSeqs[added.Value.SessionId] = added.Value.AsOfSeq;
            }

            // added 摘要也带 running：与 status 事件共用边沿检测，避免错过任一来源的完成信号。
            ApplyRunningState(added.Value.SessionId, added.Value.Running);
            return;
        }

        var removed = DshFrameParser.TryParseSessionRemoved(json);
        if (removed is not null)
        {
            _runningSessions.TryRemove(removed, out _);
            _sessionTitles.TryRemove(removed, out _);
            _sessionSeqs.TryRemove(removed, out _);
        }
    }

    /// <summary>
    /// 维护会话 running 状态并检测完成边沿：已知 running=true 且翻转为 false 视为一轮完成。
    /// 首次观测（unknown）只记录状态，避免把空闲会话误报为完成。
    /// </summary>
    private void ApplyRunningState(string sessionId, bool running)
    {
        var completed = false;
        var updated = _runningSessions.AddOrUpdate(
            sessionId,
            _ => running,
            (_, previous) =>
            {
                completed = previous && !running;
                return running;
            });

        if (completed)
        {
            RaiseSessionCompleted(sessionId);
        }
    }

    private void RaiseSessionCompleted(string sessionId)
    {
        _sessionTitles.TryGetValue(sessionId, out var sessionTitle);
        _sessionSeqs.TryGetValue(sessionId, out var asOfSeq);
        Log?.Invoke($"session turn completed: session={sessionId}, seq={asOfSeq}");
        try
        {
            SessionCompleted?.Invoke(this, new SessionCompletedEventArgs(sessionId, sessionTitle, asOfSeq));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"session completion handler failed: {ex.Message}");
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        // 本地服务直连，不走系统代理。
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        bool? lastOnline = null; // null 确保首次探测必然上报一次在线状态。
        while (!ct.IsCancellationRequested)
        {
            var online = await ProbeAsync(http, ct);
            if (online != lastOnline)
            {
                lastOnline = online;
                try
                {
                    ServiceAvailabilityChanged?.Invoke(this, new ServiceAvailabilityEventArgs(online));
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"service availability handler failed: {ex.Message}");
                }
            }

            try
            {
                await Task.Delay(HeartbeatInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> ProbeAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(_auth.Origin, HttpCompletionOption.ResponseHeadersRead, ct);
            // WebUI 首页可能返回任意状态码（含未认证 401）；能收到响应即视为在线。
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private Uri StreamUri(string path)
    {
        var builder = new UriBuilder(_auth.Origin) { Scheme = _wsScheme, Path = path };
        return builder.Uri;
    }
}
