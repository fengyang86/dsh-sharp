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

    private readonly Uri _baseUri;
    private readonly string _wsScheme;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, string> _sessionTitles = new(StringComparer.Ordinal);
    private readonly SessionCompletionTracker _tracker = new();
    private Task? _hostTask;
    private Task? _muxTask;
    private Task? _heartbeatTask;

    public DshEventMonitor(string baseUrl)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _wsScheme = _baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
    }

    /// <summary>会话完成（running true→false 翻转，去重）。</summary>
    public event EventHandler<SessionCompletedEventArgs>? SessionCompleted;

    /// <summary>DSH 服务在线状态变化（心跳探测）。</summary>
    public event EventHandler<ServiceAvailabilityEventArgs>? ServiceAvailabilityChanged;

    public void Start()
    {
        _hostTask = Task.Run(() => RunStreamLoopAsync(host: true, _cts.Token));
        _muxTask = Task.Run(() => RunStreamLoopAsync(host: false, _cts.Token));
        _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            var tasks = new[] { _hostTask, _muxTask, _heartbeatTask }
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

        _cts.Dispose();
    }

    private async Task RunStreamLoopAsync(bool host, CancellationToken ct)
    {
        var backoff = BackoffMin;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(StreamUri(host), ct);
                backoff = BackoffMin;
                await PumpStreamAsync(ws, host, ct);
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

    private async Task PumpStreamAsync(ClientWebSocket ws, bool host, CancellationToken ct)
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
            HandleFrame(host, json);
        }
    }

    private void HandleFrame(bool host, string json)
    {
        if (host)
        {
            var status = DshFrameParser.TryParseHostSessionStatus(json);
            if (status is null)
            {
                return;
            }

            if (_tracker.OnSessionStatus(status.Value.SessionId, status.Value.Running))
            {
                _sessionTitles.TryGetValue(status.Value.SessionId, out var title);
                SessionCompleted?.Invoke(this, new SessionCompletedEventArgs(status.Value.SessionId, title));
            }
        }
        else
        {
            var title = DshFrameParser.TryParseSessionTitle(json);
            if (title is not null)
            {
                _sessionTitles[title.Value.SessionId] = title.Value.Title;
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var lastOnline = true; // 初始不触发，避免启动即"离线"闪烁。
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

    private Uri StreamUri(bool host)
    {
        var path = host ? "api/events.host" : "api/events.mux";
        var builder = new UriBuilder(_baseUri) { Scheme = _wsScheme, Path = path };
        return builder.Uri;
    }
}
