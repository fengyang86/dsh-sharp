using System.Reflection;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DSHSharp.Core.Configuration;

namespace DSHSharp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WebUrl = settings.WebUrl;
    }

    public string AppName { get; } = "DSH-Sharp";

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>嵌入的 DSH WebUI 地址。</summary>
    public string WebUrl { get; }

    /// <summary>状态栏文本（服务连接状态：地址 + 模式 + 状态）。</summary>
    [ObservableProperty]
    private string _serviceStatusText = "正在连接 DSH 服务…";

    /// <summary>状态圆点颜色：在线绿 / 启动橙 / 离线灰。</summary>
    [ObservableProperty]
    private IBrush _serviceStatusBrush = StatusGray;

    private static readonly SolidColorBrush StatusGreen = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush StatusOrange = new(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly SolidColorBrush StatusGray = new(Color.FromRgb(0x9E, 0x9E, 0x9E));

    /// <summary>
    /// 更新服务状态栏，展示当前连接的服务身份：
    /// 配置名、地址、托管模式、在线/离线、是否客户端托管；圆点同步着色。
    /// </summary>
    public void SetServiceState(bool online, bool owned, bool starting, string webUrl, string managedMode, string profileName)
    {
        var modeText = managedMode switch
        {
            "None" => "不托管",
            "Source" => "源码托管",
            _ => "官方包托管",
        };

        ServiceStatusBrush = starting ? StatusOrange : online ? StatusGreen : StatusGray;
        ServiceStatusText = starting
            ? $"正在启动 {profileName} · {webUrl}（{modeText}）…"
            : online
                ? owned
                    ? $"{profileName} · {webUrl} · 在线（{modeText}·客户端启动）"
                    : $"{profileName} · {webUrl} · 在线（外部运行）"
                : $"{profileName} · {webUrl} · 离线";
    }
}
