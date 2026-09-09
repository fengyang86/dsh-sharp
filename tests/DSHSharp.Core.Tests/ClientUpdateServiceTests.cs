using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

public sealed class ClientUpdateServiceTests
{
    [Theory]
    [InlineData("0.2.4", true)]
    [InlineData("v0.2.4", true)]
    [InlineData("0.10.0", true)]
    [InlineData("0.2.3", false)]
    [InlineData("v0.2.3", false)]
    [InlineData("0.2.2", false)]
    [InlineData("0.2.1", false)]
    [InlineData("not-a-version", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsNewerThanCurrent_ComparesAgainstProductVersion(string? latest, bool expected)
    {
        Assert.Equal(expected, ClientUpdateService.IsNewerThanCurrent(latest));
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
