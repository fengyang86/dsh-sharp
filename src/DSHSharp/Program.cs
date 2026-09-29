using System.Diagnostics;
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
        // 客户端自更新：新版本 EXE 以 --apply-update 启动时接管安装——
        // 等旧实例退出、把自己复制进安装目录、清理 staging、重启新版本。
        // 该模式不进入单实例检查和 Avalonia 主循环。
        if (ParseApplyUpdate(args) is { } applyArgs)
        {
            return RunApplyUpdate(applyArgs);
        }

        using var singleInstance = new SingleInstanceService();
        if (!singleInstance.IsFirstInstance)
        {
            // 已有实例在运行：通知其唤起主窗口，本进程直接退出。
            singleInstance.NotifyFirstInstance();
            return 0;
        }

        App.AutoStartLaunch = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        App.PendingDeepLinkSession = ParseDeepLinkSession(args);
        App.StartupStopwatch = Stopwatch.StartNew();

        singleInstance.StartListener(() =>
            Dispatcher.UIThread.Post(() => App.Instance?.ActivateMainWindow()));

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>解析 dshsharp://session/&lt;id&gt; 深链参数（安装器注册的 URL 协议传入）。</summary>
    internal static string? ParseDeepLinkSession(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith("dshsharp://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, "session", StringComparison.OrdinalIgnoreCase))
            {
                var id = uri.AbsolutePath.Trim('/');
                return id.Length > 0 ? id : null;
            }
        }

        return null;
    }

    private static (string InstallDir, int WaitForPid)? ParseApplyUpdate(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length)
        {
            return null;
        }

        return (args[index + 1], int.TryParse(args[index + 2], out var pid) ? pid : 0);
    }

    private static int RunApplyUpdate((string InstallDir, int WaitForPid) applyArgs)
    {
        var (installDir, waitForPid) = applyArgs;
        var log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSHSharp", "app.log");
        void WriteLog(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss.fff}] apply-update: {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响安装。
            }
        }

        // 旁路暂存 + 目录改名交换：中途失败保留旧版本可回滚，不再直接逐文件覆盖安装目录。
        return DSHSharp.Core.Services.UpdateInstaller.Run(AppContext.BaseDirectory, installDir, waitForPid, WriteLog);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
