using System.Net;
using System.Text;
using System.Text.Json;

namespace DSHSharp.Core.Dsh;

/// <summary>会话列表项摘要。</summary>
/// <param name="AsOfSeq">会话投影的最新事件序号，作为 session/page 的 throughSeq 上界。</param>
public sealed record DshSessionSummary(string SessionId, string? Title, bool Running, long UpdatedAt, long AsOfSeq = 0, bool Archived = false);

/// <summary>
/// DSH HTTP RPC 客户端：按官方协议调用 <c>POST /api/&lt;method&gt;</c>，
/// 请求体为 <c>{ type:'client-request', rpcId, method, payload }</c>。
/// DSH 0.1.2+ 方法名用斜杠（session/list）且 payload 需 <c>{ args: { _request | request } }</c> 包装，
/// 与旧版（session.list、平铺 payload）在运行时自动回退兼容。
/// 认证依赖 <see cref="DshAuthSession"/>：token 交换 cookie，401 时自动重新交换一次。
/// 本地服务直连（不走系统代理）。
/// </summary>
public sealed class DshApiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly DshAuthSession _auth;

    public DshApiClient(string baseUrl)
        : this(new DshAuthSession(baseUrl))
    {
    }

    public DshApiClient(DshAuthSession auth)
    {
        _auth = auth;
    }

    /// <summary>获取会话列表（session/list），按最近更新排序。</summary>
    public async Task<IReadOnlyList<DshSessionSummary>> ListSessionsAsync(CancellationToken ct = default)
    {
        var json = await PostRpcCompatAsync(
            (DshMethods.SessionListNew, DshPayloads.SessionListNew),
            (DshMethods.SessionListOld, DshPayloads.SessionListOld),
            ct);
        return DshRpcParser.ParseSessionList(json);
    }

    /// <summary>
    /// 获取会话最后一条助手回复的文本（用于通知内容预览）。
    /// 新版走 session/page（throughSeq 必须不越过会话游标，取自会话摘要的 asOfSeq），
    /// 旧版走 session.history。
    /// </summary>
    public async Task<string?> GetLastAssistantTextAsync(string sessionId, long throughSeq, CancellationToken ct = default)
    {
        var json = await PostRpcCompatAsync(
            (DshMethods.SessionPageNew, DshPayloads.SessionPageNew(sessionId, throughSeq)),
            (DshMethods.SessionHistoryOld, DshPayloads.SessionHistoryOld(sessionId)),
            ct);
        return DshRpcParser.ParsePageRecords(json) ?? DshRpcParser.ParseLastAssistantText(json);
    }

    /// <summary>
    /// 查询 npm 上 @deepseek-ai/dsh 的最新稳定版本。受限网络环境依次尝试
    /// 官方 registry 与 npmmirror 镜像（各经 HttpFallback 回退链）。
    /// </summary>
    public async Task<string?> GetNpmLatestVersionAsync(CancellationToken ct = default)
    {
        foreach (var registry in new[]
                 {
                     "https://registry.npmjs.org/@deepseek-ai/dsh/latest",
                     "https://registry.npmmirror.com/@deepseek-ai/dsh/latest",
                 })
        {
            try
            {
                using var response = await Services.HttpFallback.GetAsync(registry, RequestTimeout, ct: ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                var version = DshRpcParser.ParseNpmLatestVersion(json);
                if (version is not null)
                {
                    return version;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // 官方源不可达时继续尝试镜像。
            }
        }

        return null;
    }

    /// <summary>方法名常量：DSH 0.1.2+ 为斜杠命名，旧版为点号命名。</summary>
    internal static class DshMethods
    {
        public const string SessionListNew = "session/list";
        public const string SessionListOld = "session.list";
        public const string SessionPageNew = "session/page";
        public const string SessionHistoryOld = "session.history";
    }

    /// <summary>各 RPC 的请求载荷（新版需 args 包装；无实参方法用 _request，有实参方法用 request）。</summary>
    internal static class DshPayloads
    {
        public static readonly object SessionListNew = new { args = new { _request = new { } } };
        public static readonly object SessionListOld = new { };

        public static object SessionPageNew(string sessionId, long throughSeq) => new
        {
            args = new
            {
                request = new
                {
                    address = new { kind = "session", sessionId },
                    throughSeq,
                    maxMessages = 3,
                },
            },
        };

        public static object SessionHistoryOld(string sessionId) => new { sessionId, maxMessages = 3 };
    }

    /// <summary>
    /// 先按新版协议调用，未成功（404 或 ok:false）时回退旧版；
    /// 两者都失败时返回新版响应，保持原始错误信息上抛。
    /// </summary>
    private async Task<string> PostRpcCompatAsync(
        (string Method, object Payload) primary,
        (string Method, object Payload) fallback,
        CancellationToken ct)
    {
        string? primaryJson = null;
        try
        {
            primaryJson = await PostRpcAsync(primary.Method, primary.Payload, ct);
        }
        catch (HttpRequestException)
        {
            // 旧版 runtime 对斜杠方法名返回 404；留给 fallback 判定。
        }

        if (primaryJson is not null && DshRpcParser.IsOk(primaryJson))
        {
            return primaryJson;
        }

        var fallbackJson = await PostRpcAsync(fallback.Method, fallback.Payload, ct);
        return DshRpcParser.IsOk(fallbackJson) ? fallbackJson : primaryJson ?? fallbackJson;
    }

    private async Task<string> PostRpcAsync(string method, object payload, CancellationToken ct)
    {
        var response = await SendAsync(method, payload, allowReauth: true, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string method, object payload, bool allowReauth, CancellationToken ct)
    {
        if (!await _auth.EnsureAuthenticatedAsync(ct))
        {
            throw new HttpRequestException($"DSH 认证失败：token 未兑换到 cookie（{_auth.Origin}）");
        }

        var request = new
        {
            type = "client-request",
            rpcId = Guid.NewGuid().ToString("N"),
            method,
            payload,
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var http = new HttpClient(_auth.CreateHandler())
        {
            Timeout = RequestTimeout,
        };
        // Origin 无 query，相对拼接不会丢失认证信息。
        var response = await http.PostAsync(new Uri(_auth.Origin, $"api/{method}"), content, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && allowReauth)
        {
            response.Dispose();
            // runtime 重启或端口漂移会使旧 cookie 失效：重新交换一次再试。
            _auth.Reset();
            return await SendAsync(method, payload, allowReauth: false, ct);
        }

        return response;
    }
}

/// <summary>RPC 响应解析（纯函数，便于单元测试）。</summary>
public static class DshRpcParser
{
    /// <summary>响应信封是否为 <c>result.ok == true</c>；解析失败返回 false。</summary>
    public static bool IsOk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("result", out var result) &&
                   result.TryGetProperty("ok", out var ok) &&
                   ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 <c>session/list</c> 的 server-response：
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
                JsonElement values = default;
                var hasProjections = item.TryGetProperty("projections", out var projections) &&
                                     projections.TryGetProperty("values", out values);
                var title = hasProjections && values.TryGetProperty("title", out var titleEl)
                    ? titleEl.GetString()
                    : null;
                var asOfSeq = hasProjections && projections.TryGetProperty("asOfSeq", out var seqEl) &&
                              seqEl.ValueKind == JsonValueKind.Number
                    ? seqEl.GetInt64()
                    : 0L;
                var running = item.TryGetProperty("running", out var runningEl) && runningEl.GetBoolean();
                var updatedAt = item.TryGetProperty("updatedAt", out var updatedEl)
                    ? updatedEl.GetInt64()
                    : 0L;
                var archived = item.TryGetProperty("archived", out var archivedEl) && archivedEl.ValueKind == JsonValueKind.True;
                if (!archived && item.TryGetProperty("status", out var statusEl) &&
                    string.Equals(statusEl.GetString(), "archived", StringComparison.OrdinalIgnoreCase))
                {
                    archived = true;
                }
                if (!archived && hasProjections &&
                    values.TryGetProperty("sessionListMetadata", out var metadata) &&
                    metadata.TryGetProperty("archived", out var metadataArchived) &&
                    metadataArchived.ValueKind == JsonValueKind.True)
                {
                    archived = true;
                }
                result.Add(new DshSessionSummary(sessionId, title, running, updatedAt, asOfSeq, archived));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 响应异常：返回空列表。
        }

        return result
            .Where(s => !s.Archived)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    /// <summary>从 npm registry <c>/latest</c> 响应中提取版本号。</summary>
    public static string? ParseNpmLatestVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var versionEl))
            {
                return versionEl.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 解析失败：返回 null。
        }

        return null;
    }

    /// <summary>
    /// 从 <c>session/page</c> 响应（DSH 0.1.2+）中提取最后一条助手回复的文本。
    /// 结构：<c>value.records[]</c>，事件项 <c>{ type:'event', event:{ type:'assistant/message', data:{ message:{ content:[{type:'text',text}] } } } }</c>。
    /// 无回复或解析失败返回 null。
    /// </summary>
    public static string? ParsePageRecords(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var resultEl) is false ||
                resultEl.TryGetProperty("ok", out var okEl) is false ||
                okEl.GetBoolean() is false ||
                resultEl.TryGetProperty("value", out var valueEl) is false ||
                valueEl.TryGetProperty("records", out var records) is false)
            {
                return null;
            }

            foreach (var record in records.EnumerateArray().Reverse())
            {
                if (record.TryGetProperty("type", out var recordType) is false ||
                    recordType.GetString() != "event" ||
                    record.TryGetProperty("event", out var evt) is false ||
                    evt.TryGetProperty("type", out var typeEl) is false ||
                    typeEl.GetString() != "assistant/message" ||
                    evt.TryGetProperty("data", out var data) is false ||
                    data.TryGetProperty("message", out var message) is false ||
                    message.TryGetProperty("content", out var content) is false)
                {
                    continue;
                }

                var text = ExtractText(content);
                if (text is not null)
                {
                    return text;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 解析失败：返回 null。
        }

        return null;
    }

    /// <summary>
    /// 从 <c>session.history</c> 响应（旧版协议）中提取最后一条助手回复的文本。
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

                var text = ExtractText(content);
                if (text is not null)
                {
                    return text;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 解析失败：返回 null。
        }

        return null;
    }

    private static string? ExtractText(JsonElement content)
    {
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
