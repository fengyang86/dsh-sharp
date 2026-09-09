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

        singleInstance.StartListener(() =>
            Dispatcher.UIThread.Post(() => App.Instance?.ActivateMainWindow()));

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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

        try
        {
            WriteLog($"installer started (installDir={installDir}, waitForPid={waitForPid})");
            if (waitForPid > 0)
            {
                try
                {
                    using var old = Process.GetProcessById(waitForPid);
                    if (!old.WaitForExit(20_000))
                    {
                        WriteLog("old instance still running after 20s, aborting");
                        return 2;
                    }
                }
                catch (ArgumentException)
                {
                    // 旧实例已退出。
                }
            }

            var sourceDir = AppContext.BaseDirectory;
            var stagedExe = Path.Combine(sourceDir, "DSHSharp.exe");
            if (!File.Exists(stagedExe))
            {
                WriteLog($"staged DSHSharp.exe missing at {sourceDir}");
                return 3;
            }

            Directory.CreateDirectory(installDir);
            foreach (var source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, source);
                var target = Path.Combine(installDir, relative);
                var targetDir = Path.GetDirectoryName(target);
                if (targetDir is not null)
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(source, target, overwrite: true);
            }

            // WebView2 用户数据目录由旧安装产生、不应被 staging 里的空目录顶掉。
            var userData = Path.Combine(installDir, "DSHSharp.exe.WebView2");
            WriteLog($"copied update payload into {installDir}");

            // 清理安装目录内的下载 staging（历史版本曾放在安装目录中）。
            try
            {
                var legacyStaging = Path.Combine(installDir, "update-staging");
                if (Directory.Exists(legacyStaging))
                {
                    Directory.Delete(legacyStaging, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响安装结果。
            }

            using (Process.Start(new ProcessStartInfo(Path.Combine(installDir, "DSHSharp.exe")) { UseShellExecute = true }))
            {
                WriteLog("new instance launched");
            }

            return 0;
        }
        catch (Exception ex)
        {
            WriteLog($"failed: {ex}");
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
