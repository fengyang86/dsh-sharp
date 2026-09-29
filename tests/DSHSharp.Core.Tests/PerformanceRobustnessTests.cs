using DSHSharp.Core.Dsh;
using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

/// <summary>性能与健壮性修复的回归测试：自更新目录交换、事件流单次解析分发。</summary>
public sealed class PerformanceRobustnessTests
{
    // ---- UpdateInstaller：目录级原子交换 ----

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "dshsharp-upd-" + Path.GetRandomFileName());

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void TrySwapDirectories_FreshInstall_PlacesNewDir()
    {
        var root = TempRoot();
        try
        {
            var install = Path.Combine(root, "app");
            var newDir = install + ".new";
            WriteFile(newDir, "DSHSharp.exe", "new");
            Assert.True(UpdateInstaller.TrySwapDirectories(install, newDir, install + ".old", _ => { }));
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "DSHSharp.exe")));
            Assert.False(Directory.Exists(install + ".old"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrySwapDirectories_ExistingInstall_PreservesAndCarriesWebView2Data()
    {
        var root = TempRoot();
        try
        {
            var install = Path.Combine(root, "app");
            var newDir = install + ".new";
            var oldDir = install + ".old";
            WriteFile(install, "DSHSharp.exe", "old");
            WriteFile(Path.Combine(install, "DSHSharp.exe.WebView2"), "Cache/profile.bin", "userData");
            WriteFile(newDir, "DSHSharp.exe", "new");
            WriteFile(newDir, "DSHSharp.exe.WebView2/placeholder", string.Empty); // staging 空骨架。

            Assert.True(UpdateInstaller.TrySwapDirectories(install, newDir, oldDir, _ => { }));

            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "DSHSharp.exe")));
            // WebView2 数据以旧安装为准，携带到新目录。
            Assert.Equal(
                "userData",
                File.ReadAllText(Path.Combine(install, "DSHSharp.exe.WebView2", "Cache", "profile.bin")));
            Assert.False(Directory.Exists(newDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrySwapDirectories_SwapFailure_RollsBackOldInstall()
    {
        var root = TempRoot();
        try
        {
            var install = Path.Combine(root, "app");
            var newDir = install + ".new";
            var oldDir = install + ".old";
            WriteFile(install, "DSHSharp.exe", "old");
            // newDir 不存在：第二步改名必然失败 → 应回滚旧目录。
            Assert.False(UpdateInstaller.TrySwapDirectories(install, newDir, oldDir, _ => { }));
            Assert.Equal("old", File.ReadAllText(Path.Combine(install, "DSHSharp.exe")));
            Assert.False(Directory.Exists(oldDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- DshFrameParser：单次解析分发 ----

    [Fact]
    public void DispatchEmitFrame_RoutesByEventName()
    {
        var seen = new List<string>();
        DshFrameParser.DispatchEmitFrame(
            """{"type":"item","streamId":"s","value":{"type":"emit","event":"api-session/status","args":["sess-1",false]}}""",
            (eventName, _) => seen.Add(eventName));
        Assert.Equal(["api-session/status"], seen);

        seen.Clear();
        DshFrameParser.DispatchEmitFrame(
            """{"type":"item","value":{"type":"emit","event":"api-session/removed","args":["sess-1"]}}""",
            (eventName, args) =>
            {
                seen.Add(eventName);
                Assert.Equal("sess-1", DshFrameParser.ExtractRemovedArgs(args));
            });
        Assert.Equal(["api-session/removed"], seen);
    }

    [Fact]
    public void DispatchEmitFrame_NonEmitOrMalformed_DoesNotCall()
    {
        var called = false;
        DshFrameParser.DispatchEmitFrame("""{"type":"end"}""", (_, _) => called = true);
        DshFrameParser.DispatchEmitFrame("not json", (_, _) => called = true);
        Assert.False(called);
    }

    [Fact]
    public void DispatchEmitFrame_UnknownEventName_StillDispatches()
    {
        // 任意 emit 事件都回调（分发职责），未知事件名由 Monitor 的 switch 忽略。
        var events = new List<string>();
        DshFrameParser.DispatchEmitFrame(
            """{"type":"item","value":{"type":"emit","event":"other/event","args":[]}}""",
            (eventName, _) => events.Add(eventName));
        Assert.Equal(["other/event"], events);
    }

    [Fact]
    public void DispatchLegacyFrame_ExtractsTitleAndTurnEnd()
    {
        (string sessionId, string title)? gotTitle = null;
        DshFrameParser.DispatchLegacyFrame(
            """{"type":"server-request","payload":{"type":"session/event","sessionId":"s1","event":{"type":"session/title","data":{"title":"标题"}}}}""",
            (sessionId, eventType, evt) =>
            {
                if (eventType == "session/title")
                {
                    gotTitle = (sessionId, DshFrameParser.ExtractTitle(evt)!);
                }
            });
        Assert.Equal(("s1", "标题"), gotTitle);

        string? reason = null;
        DshFrameParser.DispatchLegacyFrame(
            """{"payload":{"type":"session/event","sessionId":"s1","event":{"type":"turn/end","data":{"reason":{"kind":"completed"}}}}}""",
            (_, eventType, evt) =>
            {
                if (eventType == "turn/end")
                {
                    reason = DshFrameParser.ExtractTurnEndReason(evt);
                }
            });
        Assert.Equal("completed", reason);
    }

    [Fact]
    public void DispatchEmitFrame_StatusAndAdded_ExtractorsMatchLegacyParsers()
    {
        // 分发路径的提取器与原 TryParse* 语义一致（回归保护）。
        const string statusJson = """{"type":"item","value":{"type":"emit","event":"api-session/status","args":["s1",true]}}""";
        SessionStatusInfo? viaDispatch = null;
        DshFrameParser.DispatchEmitFrame(statusJson, (_, args) => viaDispatch = DshFrameParser.ExtractStatusArgs(args));
        Assert.Equal(DshFrameParser.TryParseSessionStatus(statusJson), viaDispatch);

        const string addedJson = """{"type":"item","value":{"type":"emit","event":"api-session/added","args":[{"sessionId":"s1","running":true,"projections":{"asOfSeq":7,"values":{"title":"T"}}}]}}""";
        SessionAddedInfo? addedViaDispatch = null;
        DshFrameParser.DispatchEmitFrame(addedJson, (_, args) => addedViaDispatch = DshFrameParser.ExtractAddedArgs(args));
        Assert.Equal(DshFrameParser.TryParseSessionAdded(addedJson), addedViaDispatch);
    }
}
