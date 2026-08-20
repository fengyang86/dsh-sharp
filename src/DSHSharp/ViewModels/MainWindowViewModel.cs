using System.Reflection;

namespace DSHSharp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public string AppName { get; } = "DSH-Sharp";

    public string Subtitle { get; } = "DeepSeek Harness 桌面客户端";

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public string StatusText { get; } = "就绪";
}
