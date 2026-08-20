using System.Text.Json;

namespace DSHSharp.Core.Dsh;

/// <summary>host 流中 <c>host/session-status</c> 帧的解析结果。</summary>
public readonly record struct HostSessionStatus(string SessionId, bool Running);

/// <summary>
/// DSH WebSocket 事件帧解析（纯函数，便于单元测试）。
/// 帧为 <c>{ type: 'server-request', rpcId, method, payload }</c> JSON 结构。
/// </summary>
public static class DshFrameParser
{
    /// <summary>
    /// 解析 host 流帧；仅识别 <c>host/session-status</c>，
    /// 其余帧返回 null。
    /// </summary>
    public static HostSessionStatus? TryParseHostSessionStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var payload = GetPayload(doc.RootElement);
            if (payload is null ||
                payload.Value.TryGetProperty("type", out var type) is false ||
                type.GetString() != "host/session-status" ||
                payload.Value.TryGetProperty("sessionId", out var sessionId) is false ||
                payload.Value.TryGetProperty("running", out var running) is false)
            {
                return null;
            }

            var id = sessionId.GetString();
            return id is null ? null : new HostSessionStatus(id, running.GetBoolean());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 mux 流帧中的 <c>session/title</c> 事件；
    /// 非标题事件或格式错误返回 null。
    /// </summary>
    public static SessionTitleInfo? TryParseSessionTitle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var payload = GetPayload(doc.RootElement);
            if (payload is null ||
                payload.Value.TryGetProperty("type", out var type) is false ||
                type.GetString() != "session/event" ||
                payload.Value.TryGetProperty("sessionId", out var sessionId) is false ||
                payload.Value.TryGetProperty("event", out var evt) is false ||
                evt.TryGetProperty("type", out var evtType) is false ||
                evtType.GetString() != "session/title" ||
                evt.TryGetProperty("title", out var title) is false)
            {
                return null;
            }

            var id = sessionId.GetString();
            var text = title.GetString();
            return id is null || text is null ? null : new SessionTitleInfo(id, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? GetPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            root.TryGetProperty("payload", out var payload) is false ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return payload;
    }
}

/// <summary>mux 流 <c>session/title</c> 事件的解析结果。</summary>
public readonly record struct SessionTitleInfo(string SessionId, string Title);
