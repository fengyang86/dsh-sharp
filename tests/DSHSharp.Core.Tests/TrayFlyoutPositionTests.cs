using Avalonia;
using DSHSharp.Views;

namespace DSHSharp.Tests;

/// <summary>托盘快捷面板的锚点数学：任务栏四种方位 + 无任务栏，全部设备像素口径。</summary>
public sealed class TrayFlyoutPositionTests
{
    // 1920×1080 全屏、缩放 1.0 的基准场景。
    private static readonly PixelRect Bounds = new(0, 0, 1920, 1080);
    private const int FlyoutWidth = 280;
    private const int FlyoutHeight = 448;

    [Fact]
    public void BottomTaskbar_AnchorsWorkingAreaBottomRight()
    {
        // 底部任务栏高 48：工作区 = (0,0,1920,1032)。
        var working = new PixelRect(0, 0, 1920, 1032);
        var pos = TrayFlyoutWindow.ComputeFlyoutPosition(Bounds, working, FlyoutWidth, FlyoutHeight, 1800, 1056);
        Assert.Equal(1654, pos.X);
        Assert.Equal(1056 - FlyoutHeight + 14, pos.Y);
    }

    [Fact]
    public void TopTaskbar_AnchorsWorkingAreaTopRight()
    {
        // 顶部任务栏高 48：工作区 = (0,48,1920,1032)。
        var working = new PixelRect(0, 48, 1920, 1032);
        var pos = TrayFlyoutWindow.ComputeFlyoutPosition(Bounds, working, FlyoutWidth, FlyoutHeight, 1800, 24);
        Assert.Equal(1654, pos.X);
        Assert.Equal(24 - 14, pos.Y);
    }

    [Fact]
    public void LeftTaskbar_AnchorsWorkingAreaBottomLeft()
    {
        // 左侧任务栏宽 48：工作区 = (48,0,1872,1080)。
        var working = new PixelRect(48, 0, 1872, 1080);
        var pos = TrayFlyoutWindow.ComputeFlyoutPosition(Bounds, working, FlyoutWidth, FlyoutHeight, 1800, 1056);
        Assert.Equal(48 - 14, pos.X);
        Assert.Equal(1056 - FlyoutHeight + 14, pos.Y);
    }

    [Fact]
    public void RightTaskbar_AnchorsWorkingAreaBottomRight()
    {
        // 右侧任务栏宽 48：工作区 = (0,0,1872,1080)。
        var working = new PixelRect(0, 0, 1872, 1080);
        var pos = TrayFlyoutWindow.ComputeFlyoutPosition(Bounds, working, FlyoutWidth, FlyoutHeight, 1800, 1056);
        Assert.Equal(1606, pos.X);
        Assert.Equal(1056 - FlyoutHeight + 14, pos.Y);
    }

    [Fact]
    public void NoTaskbar_WorkAreaEqualsBounds()
    {
        var pos = TrayFlyoutWindow.ComputeFlyoutPosition(Bounds, Bounds, FlyoutWidth, FlyoutHeight, 1800, 1032);
        Assert.Equal(1654, pos.X);
        Assert.Equal(1032 - FlyoutHeight + 14, pos.Y);
    }

    [Fact]
    public void HighDpiCoordinates_DevicePixelsOnly()
    {
        // 4K 150% 缩放：全屏 3840×2160（设备像素），工作区底部任务栏 72px。
        var bounds = new PixelRect(0, 0, 3840, 2160);
        var working = new PixelRect(0, 0, 3840, 2088);
        // 面板 DIP 280×448 × 1.5 = 420×672 设备像素。
        var pos = TrayFlyoutWindow.ComputeFlyoutPosition(bounds, working, 420, 672, 3600, 2112);
        Assert.Equal(3434, pos.X);
        Assert.Equal(2112 - 672 + 14, pos.Y);
    }
}
