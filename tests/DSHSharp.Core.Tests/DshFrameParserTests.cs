using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

public sealed class DshFrameParserTests
{
    [Fact]
    public void Parses_HostSessionStatus_Running()
    {
        const string json = """
            {"type":"server-request","rpcId":"r1","method":"events.host",
             "payload":{"type":"host/session-status","sessionId":"sess-1","running":true}}
            """;

        var frame = DshFrameParser.TryParseHostSessionStatus(json);

        Assert.True(frame.HasValue);
        Assert.Equal("sess-1", frame.Value.SessionId);
        Assert.True(frame.Value.Running);
    }

    [Fact]
    public void Parses_HostSessionStatus_Idle()
    {
        const string json = """
            {"type":"server-request","rpcId":"r2","method":"events.host",
             "payload":{"type":"host/session-status","sessionId":"sess-2","running":false}}
            """;

        var frame = DshFrameParser.TryParseHostSessionStatus(json);

        Assert.True(frame.HasValue);
        Assert.Equal("sess-2", frame.Value.SessionId);
        Assert.False(frame.Value.Running);
    }

    [Fact]
    public void Ignores_NonStatus_HostFrames()
    {
        const string json = """
            {"type":"server-request","rpcId":"r3","method":"events.host",
             "payload":{"type":"host/session-added","sessionId":"sess-3","blank":true}}
            """;

        Assert.Null(DshFrameParser.TryParseHostSessionStatus(json));
    }

    [Fact]
    public void Ignores_MalformedJson()
    {
        Assert.Null(DshFrameParser.TryParseHostSessionStatus("{not json"));
        Assert.Null(DshFrameParser.TryParseSessionTitle("{not json"));
    }

    [Fact]
    public void Parses_SessionTitle_FromMux()
    {
        const string json = """
            {"type":"server-request","rpcId":"r4","method":"events.mux",
             "payload":{"type":"session/event","sessionId":"sess-4",
                        "event":{"type":"session/title","title":"分析日志文件"}}}
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
                        "event":{"type":"turn/end","turn":1,"reason":{"kind":"completed"}}}}
            """;

        Assert.Null(DshFrameParser.TryParseSessionTitle(json));
    }
}

public sealed class SessionCompletionTrackerTests
{
    [Fact]
    public void RunningToIdle_TriggersCompletionOnce()
    {
        var tracker = new SessionCompletionTracker();

        Assert.False(tracker.OnSessionStatus("s1", running: true));
        Assert.True(tracker.OnSessionStatus("s1", running: false));

        // 重复 idle 不重复触发。
        Assert.False(tracker.OnSessionStatus("s1", running: false));
    }

    [Fact]
    public void IdleWithoutRunning_NeverTriggers()
    {
        var tracker = new SessionCompletionTracker();

        Assert.False(tracker.OnSessionStatus("s2", running: false));
    }

    [Fact]
    public void Complete_ThenRunAgain_CompletesAgain()
    {
        var tracker = new SessionCompletionTracker();

        tracker.OnSessionStatus("s3", running: true);
        Assert.True(tracker.OnSessionStatus("s3", running: false));

        tracker.OnSessionStatus("s3", running: true);
        Assert.True(tracker.OnSessionStatus("s3", running: false));
    }

    [Fact]
    public void RunningSessions_ReflectsLatestState()
    {
        var tracker = new SessionCompletionTracker();
        tracker.OnSessionStatus("a", running: true);
        tracker.OnSessionStatus("b", running: true);
        tracker.OnSessionStatus("a", running: false);

        Assert.Equal(["b"], tracker.RunningSessions);
    }
}
