using System.Text;
using System.Text.Json;

namespace DSHSharp.Core.Dsh;

/// <summary>会话列表项摘要。</summary>
public sealed record DshSessionSummary(string SessionId, string? Title, bool Running, long UpdatedAt);

/// <summary>
/// DSH HTTP RPC 客户端：按官方协议调用 <c>POST /api/&lt;method&gt;</c>，
/// 请求体为 <c>{ type:'client-request', rpcId, method, payload }</c>。
/// 本地服务直连（不走系统代理）。
/// </summary>
public sealed class DshApiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly Uri _baseUri;

    public DshApiClient(string baseUrl)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    /// <summary>获取会话列表（session.list），按最近更新排序。</summary>
    public async Task<IReadOnlyList<DshSessionSummary>> ListSessionsAsync(CancellationToken ct = default)
    {
        var json = await PostRpcAsync("session.list", new { }, ct);
        return DshRpcParser.ParseSessionList(json);
    }

    /// <summary>获取会话最后一条助手回复的文本（用于通知内容预览）。</summary>
    public async Task<string?> GetLastAssistantTextAsync(string sessionId, CancellationToken ct = default)
    {
        var json = await PostRpcAsync("session.history", new { sessionId, maxMessages = 3 }, ct);
        return DshRpcParser.ParseLastAssistantText(json);
    }

    private async Task<string> PostRpcAsync(string method, object payload, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = RequestTimeout,
        };

        var request = new
        {
            type = "client-request",
            rpcId = Guid.NewGuid().ToString("N"),
            method,
            payload,
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri(_baseUri, $"api/{method}"), content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}

/// <summary>RPC 响应解析（纯函数，便于单元测试）。</summary>
public static class DshRpcParser
{
    /// <summary>
    /// 解析 <c>session.list</c> 的 server-response：
    /// <c>{ type, rpcId, result: { ok, value: { items: [...] } } }</c>。
    /// 解析失败时返回空列表。
    /// </summary>
    public static IReadOnlyList<DshSessionSummary> ParseSessionList(string json)
    {
        var result = new List<DshSessionSummary>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var resultEl) is false ||
                resultEl.TryGetProperty("ok", out var okEl) is false ||
                okEl.GetBoolean() is false ||
                resultEl.TryGetProperty("value", out var valueEl) is false ||
                valueEl.TryGetProperty("items", out var items) is false)
            {
                return result;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("sessionId", out var idEl) is false)
                {
                    continue;
                }

                var sessionId = idEl.GetString();
                if (sessionId is null)
                {
                    continue;
                }

                // 标题位于 projections.values.title（由会话标题投影提供）。
                var title = item.TryGetProperty("projections", out var projections) &&
                            projections.TryGetProperty("values", out var values) &&
                            values.TryGetProperty("title", out var titleEl)
                    ? titleEl.GetString()
                    : null;
                var running = item.TryGetProperty("running", out var runningEl) && runningEl.GetBoolean();
                var updatedAt = item.TryGetProperty("updatedAt", out var updatedEl)
                    ? updatedEl.GetInt64()
                    : 0L;
                result.Add(new DshSessionSummary(sessionId, title, running, updatedAt));
            }
        }
        catch (JsonException)
        {
            // 响应异常：返回空列表。
        }

        return result
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    /// <summary>
    /// 从 <c>session.history</c> 响应中提取最后一条助手回复的文本。
    /// 结构：<c>value.events[]</c>，每条 <c>{ event: { type:'assistant/message', data:{ message:{ content:[{type:'text',text}] } } } }</c>。
    /// 无回复或解析失败返回 null。
    /// </summary>
    public static string? ParseLastAssistantText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var resultEl) is false ||
                resultEl.TryGetProperty("ok", out var okEl) is false ||
                okEl.GetBoolean() is false ||
                resultEl.TryGetProperty("value", out var valueEl) is false ||
                valueEl.TryGetProperty("events", out var events) is false)
            {
                return null;
            }

            foreach (var entry in events.EnumerateArray().Reverse())
            {
                if (entry.TryGetProperty("event", out var evt) is false ||
                    evt.TryGetProperty("type", out var typeEl) is false ||
                    typeEl.GetString() != "assistant/message" ||
                    evt.TryGetProperty("data", out var data) is false ||
                    data.TryGetProperty("message", out var message) is false ||
                    message.TryGetProperty("content", out var content) is false)
                {
                    continue;
                }

                var parts = new List<string>();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType) &&
                        blockType.GetString() == "text" &&
                        block.TryGetProperty("text", out var text))
                    {
                        parts.Add(text.GetString() ?? string.Empty);
                    }
                }

                var combined = string.Join("", parts).Trim();
                return combined.Length > 0 ? combined : null;
            }
        }
        catch (JsonException)
        {
            // 响应异常：返回 null。
        }

        return null;
    }
}
