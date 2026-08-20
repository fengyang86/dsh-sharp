using Avalonia;
using Avalonia.Threading;
using DSHSharp.Core.Services;

namespace DSHSharp;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        using var singleInstance = new SingleInstanceService();
        if (!singleInstance.IsFirstInstance)
        {
            // 已有实例在运行：通知其唤起主窗口，本进程直接退出。
            singleInstance.NotifyFirstInstance();
            return 0;
        }

        App.AutoStartLaunch = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);

        singleInstance.StartListener(() =>
            Dispatcher.UIThread.Post(() => App.Instance?.ActivateMainWindow()));

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
