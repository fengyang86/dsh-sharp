using System.Text.Json;

namespace DSHSharp.Core.Dsh;

/// <summary>
/// DSH WebSocket 事件帧解析（纯函数，便于单元测试）。
/// 帧为 <c>{ type: 'server-request', rpcId, method, payload }</c> JSON 结构。
/// </summary>
public static class DshFrameParser
{

    /// <summary>
    /// 解析 mux 流帧中的 <c>session/title</c> 事件。
    /// SessionEvent 为严格信封格式 <c>{ type, seq, time, data }</c>，
    /// 标题位于 <c>data.title</c>。非标题事件或格式错误返回 null。
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
                evt.TryGetProperty("data", out var data) is false ||
                data.TryGetProperty("title", out var title) is false)
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

    /// <summary>
    /// 解析 mux 流帧中的 <c>turn/end</c> 事件（回合结束，一次任务完成）。
    /// 完成原因位于 <c>data.reason.kind</c>。非回合结束事件或格式错误返回 null。
    /// </summary>
    public static TurnEndInfo? TryParseTurnEnd(string json)
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
                evtType.GetString() != "turn/end")
            {
                return null;
            }

            var id = sessionId.GetString();
            if (id is null)
            {
                return null;
            }

            // reason.kind 位于 data.reason.kind（信封 data 字段内）。
            var reasonKind = evt.TryGetProperty("data", out var data) &&
                             data.TryGetProperty("reason", out var reason) &&
                             reason.TryGetProperty("kind", out var kind)
                ? kind.GetString()
                : null;
            return new TurnEndInfo(id, reasonKind);
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

/// <summary>mux 流 <c>turn/end</c> 事件的解析结果。</summary>
public readonly record struct TurnEndInfo(string SessionId, string? ReasonKind);
