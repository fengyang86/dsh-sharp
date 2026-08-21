using System.Reflection;
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

    /// <summary>
    /// 更新服务状态栏，展示当前连接的服务身份：
    /// 地址、托管模式、在线/离线、是否客户端托管。
    /// </summary>
    public void SetServiceState(bool online, bool owned, bool starting, string webUrl, string managedMode)
    {
        var modeText = managedMode switch
        {
            "None" => "不托管",
            "Source" => "源码托管",
            _ => "npx 托管",
        };

        ServiceStatusText = starting
            ? $"正在启动 {webUrl}（{modeText}）…"
            : online
                ? owned
                    ? $"● {webUrl} · 在线（{modeText}·客户端启动）"
                    : $"● {webUrl} · 在线（外部运行）"
                : $"○ {webUrl} · 离线";
    }
}
