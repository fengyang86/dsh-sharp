using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace DSHSharp.Core.Dsh;

/// <summary>会话完成事件参数。</summary>
public sealed class SessionCompletedEventArgs(string sessionId, string? title) : EventArgs
{
    public string SessionId { get; } = sessionId;

    /// <summary>会话标题（来自 mux 流缓存，可能为空）。</summary>
    public string? Title { get; } = title;
}

/// <summary>DSH 服务在线状态变化事件参数。</summary>
public sealed class ServiceAvailabilityEventArgs(bool isOnline) : EventArgs
{
    public bool IsOnline { get; } = isOnline;
}

/// <summary>
/// DSH 服务事件监控：
/// <list type="bullet">
/// <item><c>/api/events.host</c> WebSocket：跟踪会话 running 状态翻转，触发完成事件；</item>
/// <item><c>/api/events.mux</c> WebSocket：缓存会话标题（session/title 事件）；</item>
/// <item>HTTP 心跳：探测服务在线状态。</item>
/// </list>
/// 两条流与心跳相互独立，各自断线重连（指数退避）。
/// 不依赖 UI 线程：事件在后台线程触发，调用方负责调度。
/// </summary>
public sealed class DshEventMonitor : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BackoffMin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(30);

    /// <summary>可选的日志回调（由宿主注入，便于排查）。</summary>
    public static Action<string>? Log { get; set; }

    private readonly Uri _baseUri;
    private readonly string _wsScheme;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, string> _sessionTitles = new(StringComparer.Ordinal);
    private Task? _muxTask;
    private Task? _heartbeatTask;

    public DshEventMonitor(string baseUrl)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _wsScheme = _baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
    }

    /// <summary>会话回合完成（mux 流 turn/end + reason.kind=completed）。</summary>
    public event EventHandler<SessionCompletedEventArgs>? SessionCompleted;

    /// <summary>DSH 服务在线状态变化（心跳探测）。</summary>
    public event EventHandler<ServiceAvailabilityEventArgs>? ServiceAvailabilityChanged;

    public void Start()
    {
        _muxTask = Task.Run(() => RunStreamLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 同步取消并释放（进程退出路径使用）：只发出取消信号，不等待后台任务，
    /// 避免在 UI 线程上同步等待导致死锁；残留任务随进程退出自然终止。
    /// </summary>
    public void Dispose()
    {
        Log?.Invoke("DshEventMonitor.Dispose: cancelling");
        _cts.Cancel();
        _cts.Dispose();
        Log?.Invoke("DshEventMonitor.Dispose: done");
    }

    public async ValueTask DisposeAsync()
    {
        Log?.Invoke("DshEventMonitor.DisposeAsync: cancelling");
        _cts.Cancel();
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
                await ws.ConnectAsync(StreamUri(), ct);
                backoff = BackoffMin;
                await PumpStreamAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                // 服务未就绪或连接被重置：退避后重连。
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
            HandleFrame(json);
        }
    }

    private void HandleFrame(string json)
    {
        // session/title：缓存标题（通知内容用）。
        var title = DshFrameParser.TryParseSessionTitle(json);
        if (title is not null)
        {
            _sessionTitles[title.Value.SessionId] = title.Value.Title;
        }

        // turn/end + completed：一次任务完成 → 完成通知。
        var turnEnd = DshFrameParser.TryParseTurnEnd(json);
        if (turnEnd is not null &&
            string.Equals(turnEnd.Value.ReasonKind, "completed", StringComparison.Ordinal))
        {
            _sessionTitles.TryGetValue(turnEnd.Value.SessionId, out var sessionTitle);
            Log?.Invoke($"turn/end completed: session={turnEnd.Value.SessionId}");
            SessionCompleted?.Invoke(this, new SessionCompletedEventArgs(turnEnd.Value.SessionId, sessionTitle));
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
                ServiceAvailabilityChanged?.Invoke(this, new ServiceAvailabilityEventArgs(online));
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
            using var response = await http.GetAsync(_baseUri, HttpCompletionOption.ResponseHeadersRead, ct);
            // WebUI 首页可能返回任意状态码；能收到响应即视为在线。
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private Uri StreamUri()
    {
        var builder = new UriBuilder(_baseUri) { Scheme = _wsScheme, Path = "api/events.mux" };
        return builder.Uri;
    }
}
