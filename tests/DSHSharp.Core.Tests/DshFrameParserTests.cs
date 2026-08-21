using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

public sealed class DshFrameParserTests
{
    // SessionEvent 信封格式：{ type, seq, time, data }，内容位于 data。

    [Fact]
    public void Parses_SessionTitle_FromMux()
    {
        const string json = """
            {"type":"server-request","rpcId":"r4","method":"events.mux",
             "payload":{"type":"session/event","sessionId":"sess-4",
                        "event":{"type":"session/title","seq":5,"time":123,
                                 "data":{"title":"分析日志文件"}}}}
            """;

        var title = DshFrameParser.TryParseSessionTitle(json);

        Assert.True(title.HasValue);
        Assert.Equal("sess-4", title.Value.SessionId);
        Assert.Equal("分析日志文件", title.Value.Title);
    }

    [Fact]
    public void Ignores_NonTitle_MuxEvents()
    {
        const string json = """
            {"type":"server-request","rpcId":"r5","method":"events.mux",
             "payload":{"type":"session/event","sessionId":"sess-5",
                        "event":{"type":"turn/end","seq":6,"time":124,
                                 "data":{"turn":1,"reason":{"kind":"completed"}}}}}
            """;

        Assert.Null(DshFrameParser.TryParseSessionTitle(json));
    }

    [Fact]
    public void Parses_TurnEnd_Completed()
    {
        const string json = """
            {"type":"server-request","rpcId":"r6","method":"events.mux",
             "payload":{"type":"session/event","sessionId":"sess-6",
                        "event":{"type":"turn/end","seq":7,"time":125,
                                 "data":{"turn":2,"reason":{"kind":"completed"}}}}}
            """;

        var turnEnd = DshFrameParser.TryParseTurnEnd(json);

        Assert.True(turnEnd.HasValue);
        Assert.Equal("sess-6", turnEnd.Value.SessionId);
        Assert.Equal("completed", turnEnd.Value.ReasonKind);
    }

    [Fact]
    public void Parses_TurnEnd_Cancelled()
    {
        const string json = """
            {"type":"server-request","rpcId":"r7","method":"events.mux",
             "payload":{"type":"session/event","sessionId":"sess-7",
                        "event":{"type":"turn/end","seq":8,"time":126,
                                 "data":{"turn":1,"reason":{"kind":"cancelled"}}}}}
            """;

        var turnEnd = DshFrameParser.TryParseTurnEnd(json);

        Assert.True(turnEnd.HasValue);
        Assert.Equal("cancelled", turnEnd.Value.ReasonKind);
    }

    [Fact]
    public void Ignores_TurnStart_AsTurnEnd()
    {
        const string json = """
            {"type":"server-request","rpcId":"r8","method":"events.mux",
             "payload":{"type":"session/event","sessionId":"sess-8",
                        "event":{"type":"turn/start","seq":9,"time":127,"data":{"turn":1}}}}
            """;

        Assert.Null(DshFrameParser.TryParseTurnEnd(json));
    }

    [Fact]
    public void Ignores_MalformedJson()
    {
        Assert.Null(DshFrameParser.TryParseSessionTitle("{not json"));
        Assert.Null(DshFrameParser.TryParseTurnEnd("{not json"));
    }
}
