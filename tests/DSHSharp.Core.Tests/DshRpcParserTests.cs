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
}
