using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

public sealed class SingleInstanceServiceTests
{
    // 隔离前缀：避免与正在运行的应用实例（Local\DSHSharp.* 互斥体）冲突。
    private static readonly string Prefix = "DSHSharp.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void FirstInstance_IsFirst()
    {
        using var service = new SingleInstanceService(Prefix);

        Assert.True(service.IsFirstInstance);
    }

    [Fact]
    public void SecondInstance_IsNotFirst_And_NotifiesFirstInstance()
    {
        using var first = new SingleInstanceService(Prefix);
        Assert.True(first.IsFirstInstance);

        using var second = new SingleInstanceService(Prefix);
        Assert.False(second.IsFirstInstance);

        using var activated = new ManualResetEventSlim();
        first.StartListener(activated.Set);

        second.NotifyFirstInstance();

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)), "首实例未收到激活通知");
    }

    [Fact]
    public void Notifications_Arrive_AfterPriorConsumption()
    {
        using var first = new SingleInstanceService(Prefix);
        using var second = new SingleInstanceService(Prefix);

        var count = 0;
        using var gate = new ManualResetEventSlim();
        first.StartListener(() =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                gate.Set();
            }
        });

        second.NotifyFirstInstance();
        Assert.True(gate.Wait(TimeSpan.FromSeconds(5)), "首次通知未送达");

        // AutoReset 信号不累积：等监听线程回到 WaitOne 后再次通知。
        Thread.Sleep(100);
        second.NotifyFirstInstance();
        Assert.True(
            SpinWait.SpinUntil(() => Volatile.Read(ref count) >= 2, TimeSpan.FromSeconds(5)),
            "消费后的再次通知未送达");
    }
}
