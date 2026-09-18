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

/// <summary>运行中会话数量变化事件参数（由 status/added/removed 事件驱动，非轮询）。</summary>
public sealed class RunningSessionsChangedEventArgs(int count) : EventArgs
{
    /// <summary>当前处于 running 状态的会话数。</summary>
    public int Count { get; } = count;
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

    /// <summary>运行中会话数量变化（事件在后台线程触发，调用方负责调度）。</summary>
    public event EventHandler<RunningSessionsChangedEventArgs>? RunningSessionsChanged;

    /// <summary>当前 running 会话快照（ID + 已知标题），供 UI 展示。</summary>
    public IReadOnlyList<(string SessionId, string? Title)> RunningSnapshot()
    {
        var result = new List<(string, string?)>();
        foreach (var (sessionId, running) in _runningSessions)
        {
            if (running)
            {
                _sessionTitles.TryGetValue(sessionId, out var title);
                result.Add((sessionId, title));
            }
        }

        return result;
    }

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
                using var ws = await ConnectStreamAsync(ct);
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
    /// <remarks>DSH 0.1.6 起对 <c>/api/remote.mux</c> 的普通 GET 也返回 404，端点存在性无法再用 GET 探测，
    /// 改为直接尝试 WebSocket 连接，仅当握手返回 404 时判定为旧版 runtime 并回退 events.mux。</remarks>
    private async Task<ClientWebSocket> ConnectStreamAsync(CancellationToken ct)
    {
        if (_remoteMuxAvailable != false)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ConnectRemoteMuxAsync(ws, ct);
                _remoteMuxAvailable = true;
                return ws;
            }
            catch (WebSocketException ex) when (IsHandshakeNotFound(ex))
            {
                ws.Dispose();
            }
            catch
            {
                ws.Dispose();
                throw;
            }
        }

        var legacy = new ClientWebSocket();
        await legacy.ConnectAsync(StreamUri("api/events.mux"), ct);
        _remoteMuxAvailable = false;
        return legacy;
    }

    /// <summary>判断 WebSocket 握手失败是否为 HTTP 404。
    /// WebSocketException 不暴露状态码；.NET 握手失败消息格式稳定为
    /// "The server returned status code '404' when status code '101' was expected"，NativeErrorCode 部分场景携带状态码。</summary>
    private static bool IsHandshakeNotFound(WebSocketException ex)
        => ex.NativeErrorCode == 404
           || ex.Message.Contains("'404'", StringComparison.Ordinal);

    private async Task ConnectRemoteMuxAsync(ClientWebSocket ws, CancellationToken ct)
    {
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
            var wasRunning = _runningSessions.TryGetValue(removed, out var running) && running;
            _runningSessions.TryRemove(removed, out _);
            _sessionTitles.TryRemove(removed, out _);
            _sessionSeqs.TryRemove(removed, out _);
            if (wasRunning)
            {
                RaiseRunningSessionsChanged();
            }
        }
    }

    /// <summary>
    /// 维护会话 running 状态并检测完成边沿：已知 running=true 且翻转为 false 视为一轮完成。
    /// 首次观测（unknown）只记录状态，避免把空闲会话误报为完成。
    /// </summary>
    private void ApplyRunningState(string sessionId, bool running)
    {
        var completed = false;
        var changed = false;
        _runningSessions.AddOrUpdate(
            sessionId,
            _ =>
            {
                changed = true;
                return running;
            },
            (_, previous) =>
            {
                completed = previous && !running;
                changed = previous != running;
                return running;
            });

        if (completed)
        {
            RaiseSessionCompleted(sessionId);
        }

        if (changed)
        {
            RaiseRunningSessionsChanged();
        }
    }

    private void RaiseRunningSessionsChanged()
    {
        var count = 0;
        foreach (var running in _runningSessions.Values)
        {
            if (running)
            {
                count++;
            }
        }

        Log?.Invoke($"running sessions changed: count={count}");
        try
        {
            RunningSessionsChanged?.Invoke(this, new RunningSessionsChangedEventArgs(count));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"running sessions handler failed: {ex.Message}");
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
