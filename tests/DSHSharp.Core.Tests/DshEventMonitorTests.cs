using System.Reflection;
using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

/// <summary>运行状态边沿与事件语义：直接向 HandleFrame 喂 remote.mux 帧（反射调用）。</summary>
public sealed class DshEventMonitorTests
{
    private static void Feed(DshEventMonitor monitor, string json)
    {
        var handle = typeof(DshEventMonitor).GetMethod(
            "HandleFrame",
            BindingFlags.Instance | BindingFlags.NonPublic);
        handle!.Invoke(monitor, [json, true]);
    }

    private static string StatusFrame(string session, bool running) =>
        $"{{\"type\":\"item\",\"streamId\":\"t\",\"value\":{{\"type\":\"emit\",\"event\":\"api-session/status\",\"args\":[\"{session}\",{(running ? "true" : "false")}]}}}}";

    private DshEventMonitor CreateMonitor(
        List<SessionCompletedEventArgs> completions,
        List<RunningSessionsChangedEventArgs> runningChanges)
    {
        var monitor = new DshEventMonitor("http://127.0.0.1:1/");
        monitor.SessionCompleted += (_, e) => completions.Add(e);
        monitor.RunningSessionsChanged += (_, e) => runningChanges.Add(e);
        return monitor;
    }

    // monitor 未 Start()，不持有任何后台任务/连接，测试结束无需 DisposeAsync。

    [Fact]
    public void RunningTrueThenFalse_RaisesCompletionAndCountBackToZero()
    {
        var completions = new List<SessionCompletedEventArgs>();
        var runningChanges = new List<RunningSessionsChangedEventArgs>();
        var monitor = CreateMonitor(completions, runningChanges);

        Feed(monitor, StatusFrame("sess-1", true));
        Feed(monitor, StatusFrame("sess-1", false));

        var completion = Assert.Single(completions);
        Assert.Equal("sess-1", completion.SessionId);
        Assert.Equal(1, runningChanges[0].Count);
        Assert.Equal(0, runningChanges[^1].Count);
    }

    [Fact]
    public void FirstObservationOfIdle_DoesNotRaiseCompletion()
    {
        var completions = new List<SessionCompletedEventArgs>();
        var runningChanges = new List<RunningSessionsChangedEventArgs>();
        var monitor = CreateMonitor(completions, runningChanges);

        Feed(monitor, StatusFrame("sess-1", false));

        Assert.Empty(completions);
        // unknown → false 仍是状态变化（计次事件），但不会误报完成。
        Assert.Single(runningChanges);
        Assert.Equal(0, runningChanges[0].Count);
    }

    [Fact]
    public void RepeatedSameState_DoesNotSpamEvents()
    {
        var completions = new List<SessionCompletedEventArgs>();
        var runningChanges = new List<RunningSessionsChangedEventArgs>();
        var monitor = CreateMonitor(completions, runningChanges);

        Feed(monitor, StatusFrame("sess-1", true));
        Feed(monitor, StatusFrame("sess-1", true));
        Feed(monitor, StatusFrame("sess-1", true));

        Assert.Empty(completions);
        Assert.Single(runningChanges);
    }

    [Fact]
    public void SnapshotListsRunningSessionsWithKnownTitles()
    {
        var completions = new List<SessionCompletedEventArgs>();
        var runningChanges = new List<RunningSessionsChangedEventArgs>();
        var monitor = CreateMonitor(completions, runningChanges);

        var added = """
            {"type":"item","streamId":"t","value":{"type":"emit","event":"api-session/added","args":[
              {"sessionId":"sess-1","updatedAt":1788703489962,"running":true,"blank":false,"cwd":"D:\\\\work",
               "projections":{"asOfSeq":12,"values":{"title":"重构运行时","goal":null}}}]}}
            """;
        Feed(monitor, added);
        Feed(monitor, StatusFrame("sess-2", true));

        var snapshot = monitor.RunningSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, s => s.SessionId == "sess-1" && s.Title == "重构运行时");
        Assert.Contains(snapshot, s => s.SessionId == "sess-2" && s.Title is null);
    }

    [Fact]
    public void RemovedRunningSession_DecreasesCount()
    {
        var completions = new List<SessionCompletedEventArgs>();
        var runningChanges = new List<RunningSessionsChangedEventArgs>();
        var monitor = CreateMonitor(completions, runningChanges);

        Feed(monitor, StatusFrame("sess-1", true));
        Feed(monitor, """{"type":"item","streamId":"t","value":{"type":"emit","event":"api-session/removed","args":["sess-1"]}}""");

        Assert.Empty(completions);
        Assert.Equal(1, runningChanges[0].Count);
        Assert.Equal(0, runningChanges[^1].Count);
        Assert.Empty(monitor.RunningSnapshot());
    }
}
