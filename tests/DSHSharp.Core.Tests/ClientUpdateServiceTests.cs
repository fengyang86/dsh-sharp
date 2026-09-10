using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

public sealed class ClientUpdateServiceTests
{
    [Fact]
    public void IsNewerThanCurrent_DetectsNewerVersions()
    {
        // 从当前 ProductVersion 推导“更新一版”，避免随发版硬编码。
        var current = Version.Parse(DSHSharp.Core.Compatibility.DshSharpCompatibility.ProductVersion);
        var next = new Version(current.Major, current.Minor, current.Build + 1);
        var majorAhead = new Version(current.Major + 1, 0, 0);

        Assert.True(ClientUpdateService.IsNewerThanCurrent(next.ToString()));
        Assert.True(ClientUpdateService.IsNewerThanCurrent($"v{next}"));
        Assert.True(ClientUpdateService.IsNewerThanCurrent(majorAhead.ToString()));
    }

    [Fact]
    public void IsNewerThanCurrent_RejectsSameOlderOrInvalid()
    {
        var current = Version.Parse(DSHSharp.Core.Compatibility.DshSharpCompatibility.ProductVersion);
        var older = new Version(current.Major, current.Minor, Math.Max(current.Build - 1, 0));

        Assert.False(ClientUpdateService.IsNewerThanCurrent(current.ToString()));
        Assert.False(ClientUpdateService.IsNewerThanCurrent($"v{current}"));
        Assert.False(ClientUpdateService.IsNewerThanCurrent(older.ToString()));
        Assert.False(ClientUpdateService.IsNewerThanCurrent("not-a-version"));
        Assert.False(ClientUpdateService.IsNewerThanCurrent(null));
        Assert.False(ClientUpdateService.IsNewerThanCurrent(""));
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        var service = new ClientUpdateService(Path.Combine(Path.GetTempPath(), $"dsh-upd-{Guid.NewGuid():N}"));

        Assert.Equal("Idle", service.State.Phase);
        Assert.Null(service.State.LatestVersion);
        Assert.False(service.IsReady);
    }

    [Fact]
    public void CleanupStaging_RemovesDirectory_WhenExists()
    {
        var staging = Path.Combine(Path.GetTempPath(), $"dsh-upd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(staging, "app"));
        File.WriteAllText(Path.Combine(staging, "app", "marker.txt"), "x");

        var service = new ClientUpdateService(staging);
        service.CleanupStaging();

        Assert.False(Directory.Exists(staging));
        // 对不存在目录重复清理应安全无异常。
        service.CleanupStaging();
    }

    [Fact]
    public async Task CleanupStaging_ConcurrentWithOperation_DoesNotThrow()
    {
        // staging 清理与状态操作互不阻塞：空目录上的并发清理安全返回。
        var service = new ClientUpdateService(Path.Combine(Path.GetTempPath(), $"dsh-upd-{Guid.NewGuid():N}"));
        service.Reset();

        await Task.WhenAll(
            Task.Run(() => service.CleanupStaging()),
            Task.Run(() => service.CleanupStaging()));

        Assert.Equal("Idle", service.State.Phase);
    }
}
