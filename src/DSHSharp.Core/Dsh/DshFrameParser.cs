using System.Text.Json;

namespace DSHSharp.Core.Dsh;

/// <summary>
/// DSH WebSocket 事件帧解析（纯函数，便于单元测试）。
/// DSH 0.1.2+ 的 <c>/api/remote.mux</c> 逻辑流帧为
/// <c>{ type:'item', streamId, value:{ type:'emit', event, args } }</c>；
/// 旧版 <c>/api/events.mux</c> 帧为 <c>{ type:'server-request', rpcId, method, payload }</c>。
/// </summary>
public static class DshFrameParser
{
    /// <summary>
    /// 解析 <c>api-session/status</c> emit 帧：args 为 <c>[sessionId, isRunning]</c>。
    /// 会话运行状态翻转（true→false 即一轮完成）。其他事件或格式错误返回 null。
    /// </summary>
    public static SessionStatusInfo? TryParseSessionStatus(string json)
    {
        return WithEmitArgs<SessionStatusInfo>(json, "api-session/status", static args =>
        {
            if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2)
            {
                return null;
            }

            var sessionId = args[0].GetString();
            if (sessionId is null || args[1].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            return new SessionStatusInfo(sessionId, args[1].GetBoolean());
        });
    }

    /// <summary>
    /// 解析 <c>api-session/added</c> emit 帧：args 为 <c>[会话摘要]</c>，
    /// 与 session/list 项同构（含 projections.values.title、projections.asOfSeq、running）。
    /// </summary>
    public static SessionAddedInfo? TryParseSessionAdded(string json)
    {
        return WithEmitArgs<SessionAddedInfo>(json, "api-session/added", static args =>
        {
            if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 1 || args[0].ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var summary = args[0];
            if (summary.TryGetProperty("sessionId", out var idEl) is false || idEl.GetString() is not { } sessionId)
            {
                return null;
            }

            JsonElement values = default;
            var hasProjections = summary.TryGetProperty("projections", out var projections) &&
                                 projections.TryGetProperty("values", out values);
            var title = hasProjections && values.TryGetProperty("title", out var titleEl)
                ? titleEl.GetString()
                : null;
            var asOfSeq = hasProjections && projections.TryGetProperty("asOfSeq", out var seqEl) &&
                          seqEl.ValueKind == JsonValueKind.Number
                ? seqEl.GetInt64()
                : 0L;
            var running = summary.TryGetProperty("running", out var runningEl) && runningEl.ValueKind == JsonValueKind.True;
            return new SessionAddedInfo(sessionId, title, running, asOfSeq);
        });
    }

    /// <summary>解析 <c>api-session/removed</c> emit 帧：args 为 <c>[sessionId]</c>。</summary>
    public static string? TryParseSessionRemoved(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeEl) is false || typeEl.GetString() != "item" ||
                root.TryGetProperty("value", out var value) is false ||
                value.TryGetProperty("type", out var valueType) is false || valueType.GetString() != "emit" ||
                value.TryGetProperty("event", out var evt) is false || evt.GetString() != "api-session/removed" ||
                value.TryGetProperty("args", out var args) is false ||
                args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 1)
            {
                return null;
            }

            return args[0].GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>在 JsonDocument 生命周期内提取目标 emit 帧的 args 并转换。</summary>
    private static T? WithEmitArgs<T>(string json, string eventName, Func<JsonElement, T?> extract)
        where T : struct
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeEl) is false || typeEl.GetString() != "item" ||
                root.TryGetProperty("value", out var value) is false ||
                value.TryGetProperty("type", out var valueType) is false || valueType.GetString() != "emit" ||
                value.TryGetProperty("event", out var evt) is false || evt.GetString() != eventName ||
                value.TryGetProperty("args", out var args) is false)
            {
                return null;
            }

            return extract(args);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }


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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
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

/// <summary><c>api-session/status</c> 事件的解析结果：会话运行状态。</summary>
public readonly record struct SessionStatusInfo(string SessionId, bool Running);

/// <summary><c>api-session/added</c> 事件的解析结果：会话摘要。</summary>
public readonly record struct SessionAddedInfo(string SessionId, string? Title, bool Running, long AsOfSeq);
