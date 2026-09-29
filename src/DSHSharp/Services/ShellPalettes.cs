using Avalonia;
using Avalonia.Media;

namespace DSHSharp.Services;

/// <summary>
/// 壳层品牌色板（Light/Dark 两套）。由 <see cref="App.ApplyTheme"/> 直接写入 Application.Resources：
/// DynamicResource 对资源替换的通知在启动与运行时切换两个阶段都可靠
/// （ThemeDictionaries 的运行时切换在部分场景不重解析，故弃用）。
/// </summary>
internal static class ShellPalettes
{
    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private static LinearGradientBrush SplashBackgroundDark() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#0B101E"), 0),
            new GradientStop(Color.Parse("#121A30"), 0.55),
            new GradientStop(Color.Parse("#0D1322"), 1),
        },
    };

    private static LinearGradientBrush SplashBackgroundLight() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#FDFEFF"), 0),
            new GradientStop(Color.Parse("#F2F5FC"), 0.55),
            new GradientStop(Color.Parse("#E9EEF9"), 1),
        },
    };

    private static RadialGradientBrush SplashHalo(string inner, string outer) => new()
    {
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(inner), 0),
            new GradientStop(Color.Parse(outer), 1),
        },
    };

    private static LinearGradientBrush SplashShimmer(string edge, string mid, string tail) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(edge), 0),
            new GradientStop(Color.Parse(mid), 0.5),
            new GradientStop(Color.Parse(tail), 1),
        },
    };

    /// <summary>暗黑色板：与启动过场同源的深海军蓝家族。</summary>
    public static IReadOnlyDictionary<string, object> Dark { get; } = new Dictionary<string, object>
    {
        ["ChromeBarBrush"] = Brush("#0D1322"),
        ["ChromeBorderBrush"] = Brush("#1C2438"),
        ["ChromeForegroundBrush"] = Brush("#E8EDF8"),
        ["ChromeForegroundDimBrush"] = Brush("#6C7A99"),
        ["ChromeStatusTextBrush"] = Brush("#8FA0C4"),
        ["ChromeHoverBrush"] = Brush("#1A2340"),
        ["OnboardingBackgroundBrush"] = Brush("#0F1526"),
        ["OnboardingDetailBrush"] = Brush("#AAB6D0"),
        ["SplashTitleBrush"] = Brush("#EAEFFB"),
        ["SplashVersionBrush"] = Brush("#6C7A99"),
        ["SplashTrackBrush"] = Brush("#14FFFFFF"),
        ["SplashRingStrokeBrush"] = Brush("#8C5B8DEF"),
        ["SplashHaloBrush"] = SplashHalo("#595B8DEF", "#005B8DEF"),
        ["SplashShimmerBrush"] = SplashShimmer("#005B8DEF", "#FF5B8DEF", "#009DB8FF"),
        ["SplashBackgroundBrush"] = SplashBackgroundDark(),
    };

    /// <summary>明亮色板：同品牌的浅色转译（白/雾蓝底，深蓝文字与点缀）。</summary>
    public static IReadOnlyDictionary<string, object> Light { get; } = new Dictionary<string, object>
    {
        ["ChromeBarBrush"] = Brush("#F4F6FB"),
        ["ChromeBorderBrush"] = Brush("#DDE3F0"),
        ["ChromeForegroundBrush"] = Brush("#1B2437"),
        ["ChromeForegroundDimBrush"] = Brush("#7A86A3"),
        ["ChromeStatusTextBrush"] = Brush("#5A6685"),
        ["ChromeHoverBrush"] = Brush("#E3E9F6"),
        ["OnboardingBackgroundBrush"] = Brush("#F7F9FD"),
        ["OnboardingDetailBrush"] = Brush("#4A5570"),
        ["SplashTitleBrush"] = Brush("#1B2437"),
        ["SplashVersionBrush"] = Brush("#7A86A3"),
        ["SplashTrackBrush"] = Brush("#12000000"),
        ["SplashRingStrokeBrush"] = Brush("#8C3D6DD8"),
        ["SplashHaloBrush"] = SplashHalo("#403D6DD8", "#003D6DD8"),
        ["SplashShimmerBrush"] = SplashShimmer("#003D6DD8", "#FF3D6DD8", "#005F8FD8"),
        ["SplashBackgroundBrush"] = SplashBackgroundLight(),
    };
}
