using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

public sealed class DshRpcParserTests
{
    [Fact]
    public void Parses_SessionList_WithTitlesFromProjections()
    {
        const string json = """
            {"type":"server-response","rpcId":"r1","result":{"ok":true,"value":{"items":[
              {"sessionId":"s1","updatedAt":2000,"running":true,"blank":false,
               "projections":{"asOfSeq":5,"values":{"title":"分析日志","sessionStats":{"turns":1}}}},
              {"sessionId":"s2","updatedAt":1000,"running":false,"blank":false,
               "projections":{"asOfSeq":6,"values":{"title":"写测试"}}},
              {"sessionId":"s3","updatedAt":3000,"running":false,"blank":false,
               "projections":{"asOfSeq":7,"values":{}}}
            ]}}}
            """;

        var sessions = DshRpcParser.ParseSessionList(json);

        Assert.Equal(3, sessions.Count);
        // 按更新时间倒序。
        Assert.Equal("s3", sessions[0].SessionId);
        Assert.Equal("s1", sessions[1].SessionId);
        Assert.Equal("s2", sessions[2].SessionId);
        Assert.True(sessions[1].Running);
        Assert.Equal("分析日志", sessions[1].Title);
        Assert.Null(sessions[0].Title);
    }

    [Fact]
    public void Filters_ArchivedSessions()
    {
        const string json = """
            {"result":{"ok":true,"value":{"items":[
              {"sessionId":"active","updatedAt":2,"archived":false},
              {"sessionId":"old","updatedAt":3,"archived":true},
              {"sessionId":"status-old","updatedAt":1,"status":"archived"}
            ]}}}
            """;

        var sessions = DshRpcParser.ParseSessionList(json);

        var item = Assert.Single(sessions);
        Assert.Equal("active", item.SessionId);
    }

    [Fact]
    public void Parses_SessionList_Empty()
    {
        const string json = """
            {"type":"server-response","rpcId":"r2","result":{"ok":true,"value":{"items":[]}}}
            """;

        var sessions = DshRpcParser.ParseSessionList(json);

        Assert.Empty(sessions);
    }

    [Fact]
    public void Parses_ErrorResult_AsEmpty()
    {
        const string json = """
            {"type":"server-response","rpcId":"r3","result":{"ok":false,
             "error":{"code":"internal","message":"boom","details":{}}}}
            """;

        var sessions = DshRpcParser.ParseSessionList(json);

        Assert.Empty(sessions);
    }

    [Fact]
    public void Parses_MalformedJson_AsEmpty()
    {
        Assert.Empty(DshRpcParser.ParseSessionList("{not json"));
    }

    [Fact]
    public void Skips_ItemsWithoutSessionId()
    {
        const string json = """
            {"type":"server-response","rpcId":"r4","result":{"ok":true,"value":{"items":[
              {"updatedAt":1000,"running":false},
              {"sessionId":"s5","updatedAt":2000,"running":true,
               "projections":{"values":{"title":"有效"}}}
            ]}}}
            """;

        var sessions = DshRpcParser.ParseSessionList(json);

        Assert.Single(sessions);
        Assert.Equal("s5", sessions[0].SessionId);
    }

    [Fact]
    public void Extracts_LastAssistantText()
    {
        const string json = """
            {"type":"server-response","rpcId":"h1","result":{"ok":true,"value":{"hasMore":false,"events":[
              {"event":{"type":"user/message","seq":1,"time":1,"data":{"message":{"role":"user","content":[{"type":"text","text":"你好"}]}}}},
              {"event":{"type":"assistant/message","seq":2,"time":2,"data":{"message":{"role":"assistant","content":[{"type":"text","text":"你好，"},{"type":"text","text":"我是 DSH"}]}}}},
              {"event":{"type":"turn/end","seq":3,"time":3,"data":{"turn":1,"reason":{"kind":"completed"}}}}
            ]}}}
            """;

        var text = DshRpcParser.ParseLastAssistantText(json);

        Assert.Equal("你好，我是 DSH", text);
    }

    [Fact]
    public void Extracts_LastAssistantText_SkipsNonTextBlocks()
    {
        const string json = """
            {"type":"server-response","rpcId":"h2","result":{"ok":true,"value":{"events":[
              {"event":{"type":"assistant/message","seq":1,"time":1,"data":{"message":{"role":"assistant","content":[
                {"type":"tool_use","name":"bash","input":{}},
                {"type":"text","text":"完成"}
              ]}}}}
            ]}}}
            """;

        var text = DshRpcParser.ParseLastAssistantText(json);

        Assert.Equal("完成", text);
    }

    [Fact]
    public void ReturnsNull_WhenNoAssistantMessage()
    {
        const string json = """
            {"type":"server-response","rpcId":"h3","result":{"ok":true,"value":{"events":[
              {"event":{"type":"user/message","seq":1,"time":1,"data":{"message":{"role":"user","content":[{"type":"text","text":"hi"}]}}}}
            ]}}}
            """;

        Assert.Null(DshRpcParser.ParseLastAssistantText(json));
    }

    [Fact]
    public void ReturnsNull_OnErrorOrMalformed()
    {
        Assert.Null(DshRpcParser.ParseLastAssistantText("{not json"));
        Assert.Null(DshRpcParser.ParseLastAssistantText(
            """{"type":"server-response","rpcId":"h4","result":{"ok":false,"error":{"code":"internal","message":"x","details":{}}}}"""));
    }

    [Fact]
    public void IsOk_DetectsResultEnvelope()
    {
        Assert.True(DshRpcParser.IsOk("""{"type":"server-response","rpcId":"r","result":{"ok":true,"value":{}}}"""));
        Assert.False(DshRpcParser.IsOk("""{"type":"server-response","rpcId":"r","result":{"ok":false,"error":{}}}"""));
        Assert.False(DshRpcParser.IsOk("""{"type":"server-response","rpcId":"r"}"""));
        Assert.False(DshRpcParser.IsOk("{not json"));
    }

    [Fact]
    public void Parses_SessionList_AsOfSeq()
    {
        const string json = """
            {"result":{"ok":true,"value":{"items":[
              {"sessionId":"s1","updatedAt":2000,"running":false,
               "projections":{"asOfSeq":2149,"values":{"title":"分析"}}}
            ]}}}
            """;

        var sessions = DshRpcParser.ParseSessionList(json);

        var item = Assert.Single(sessions);
        Assert.Equal(2149, item.AsOfSeq);
        Assert.Equal("分析", item.Title);
    }

    [Fact]
    public void Extracts_LastAssistantText_FromPageRecords()
    {
        // DSH 0.1.2+ session/page 响应：value.records[]，事件项在 event 字段内。
        const string json = """
            {"type":"server-response","rpcId":"p1","result":{"ok":true,"value":{"records":[
              {"type":"event","event":{"type":"user/message","seq":10,"time":1,"data":{"message":{"role":"user","content":[{"type":"text","text":"继续"}]}}}},
              {"type":"event","event":{"type":"assistant/message","seq":11,"time":2,"data":{"message":{"role":"assistant","content":[{"type":"text","text":"已修复"},{"type":"text","text":"，验证通过"}]}}}},
              {"type":"event","event":{"type":"turn/end","seq":12,"time":3,"data":{"turn":2,"reason":{"kind":"completed"}}}}
            ]}}}
            """;

        var text = DshRpcParser.ParsePageRecords(json);

        Assert.Equal("已修复，验证通过", text);
    }

    [Fact]
    public void ReturnsNull_FromPageRecords_WithoutAssistantMessage()
    {
        const string json = """
            {"result":{"ok":true,"value":{"records":[
              {"type":"chunks","event":{"type":"chunkrow/text-chunks","seq":5,"time":1,"data":{"texts":["流式"]}}}
            ]}}}
            """;

        Assert.Null(DshRpcParser.ParsePageRecords(json));
        Assert.Null(DshRpcParser.ParsePageRecords("{not json"));
    }

    [Fact]
    public void PagePayload_UsesRequestWrapper_WithAddressAndThroughSeq()
    {
        var payload = DshApiClient.DshPayloads.SessionPageNew("session-x", 1234);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        Assert.Contains("\"args\"", json);
        Assert.Contains("\"request\"", json);
        Assert.Contains("\"address\"", json);
        Assert.Contains("\"kind\":\"session\"", json);
        Assert.Contains("\"sessionId\":\"session-x\"", json);
        Assert.Contains("\"throughSeq\":1234", json);
    }

    [Fact]
    public void ListPayload_UsesEmptyRequestWrapper()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(DshApiClient.DshPayloads.SessionListNew);

        Assert.Contains("\"args\"", json);
        Assert.Contains("\"_request\"", json);
    }
}
