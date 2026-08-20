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

    /// <summary>状态栏文本（服务连接状态）。</summary>
    [ObservableProperty]
    private string _serviceStatusText = "正在连接 DSH 服务…";

    public void SetServiceOnline(bool online) =>
        ServiceStatusText = online ? "● DSH 服务在线" : "○ DSH 服务离线";
}
