using System.Diagnostics;

namespace DSHSharp.Core.Services;

/// <summary>
/// 客户端自更新的安装阶段（由 <c>DSHSharp.exe --apply-update</c> 模式调用）：
/// 等旧实例退出 → 把 staging 载荷完整复制到安装目录旁的 <c>&lt;installDir&gt;.new</c> →
/// 目录改名交换（旧目录 → <c>.old</c>，新目录就位）→ 启动新实例 → 清理 <c>.old</c>。
/// <list type="bullet">
/// <item>交换失败时回滚：旧目录改名回原位，安装保持旧版本可用（借鉴官方桌面端的目录替换策略）；</item>
/// <item>WebView2 用户数据目录（<c>DSHSharp.exe.WebView2</c>）从旧目录携带到新目录，不随旧版被清掉；</item>
/// <item>上次中断留下的 <c>.new</c>/<c>.old</c> 残留在本次开始时清理（交换成功后也尽力清理）。</item>
/// </list>
/// </summary>
public static class UpdateInstaller
{
    private const string WebView2DataDirName = "DSHSharp.exe.WebView2";

    /// <summary>执行安装。返回进程退出码：0 成功，1 失败，2 旧实例未退出，3 载荷缺失。</summary>
    public static int Run(string sourceDir, string installDir, int waitForPid, Action<string> log)
    {
        log($"installer started (installDir={installDir}, waitForPid={waitForPid})");
        if (!WaitForOldInstance(waitForPid, log))
        {
            return 2;
        }

        var stagedExe = Path.Combine(sourceDir, "DSHSharp.exe");
        if (!File.Exists(stagedExe))
        {
            log($"staged DSHSharp.exe missing at {sourceDir}");
            return 3;
        }

        var installParent = Path.GetDirectoryName(Path.GetFullPath(installDir))!;
        var newDir = installDir + ".new";
        var oldDir = installDir + ".old";
        CleanupLeftover(new[] { newDir, oldDir }, log);
        try
        {
            CopyPayload(sourceDir, newDir, log);
            if (!TrySwapDirectories(installDir, newDir, oldDir, log))
            {
                return 1;
            }

            TryDeleteDirectory(Path.Combine(installDir, "update-staging")); // 历史版本的下载残留。

            using (Process.Start(new ProcessStartInfo(Path.Combine(installDir, "DSHSharp.exe")) { UseShellExecute = true }))
            {
                log("new instance launched");
            }

            TryDeleteDirectory(oldDir); // 失败（杀软仍占用）留给下次安装开始时清理。
            return 0;
        }
        catch (Exception ex)
        {
            log($"failed: {ex}");
            return 1;
        }
    }

    private static bool WaitForOldInstance(int waitForPid, Action<string> log)
    {
        if (waitForPid <= 0)
        {
            return true;
        }

        try
        {
            using var old = Process.GetProcessById(waitForPid);
            if (!old.WaitForExit(20_000))
            {
                log("old instance still running after 20s, aborting");
                return false;
            }
        }
        catch (ArgumentException)
        {
            // 旧实例已退出。
        }

        return true;
    }

    /// <summary>复制全部载荷到新目录（逐文件；空目录不复制，运行时自建）。</summary>
    internal static void CopyPayload(string sourceDir, string targetDir, Action<string> log)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, source));
            var targetFileDir = Path.GetDirectoryName(target);
            if (targetFileDir is not null)
            {
                Directory.CreateDirectory(targetFileDir);
            }

            File.Copy(source, target, overwrite: true);
        }

        log($"update payload staged into {targetDir}");
    }

    /// <summary>
    /// 目录级交换：旧安装 → <c>.old</c>，新目录 → 安装目录，再把 WebView2 用户数据从旧目录搬入新目录。
    /// 交换中途失败时尝试把 <c>.old</c> 改回原位。改名失败通常由杀软短时占用引起，隔 2 秒重试一次。
    /// </summary>
    internal static bool TrySwapDirectories(string installDir, string newDir, string oldDir, Action<string> log)
    {
        var hadOldInstall = Directory.Exists(installDir);
        if (hadOldInstall && !TryMoveWithRetry(installDir, oldDir, log))
        {
            return false; // 旧目录未动，安装保持旧版本。
        }

        if (!TryMoveWithRetry(newDir, installDir, log))
        {
            if (hadOldInstall)
            {
                log("new directory swap failed; rolling back old install");
                if (!TryMoveWithRetry(oldDir, installDir, log))
                {
                    log($"rollback failed: {installDir} 缺失，请重新安装；新版本留在 {newDir}");
                }
            }

            return false;
        }

        CarryWebView2UserData(oldDir, installDir, hadOldInstall, log);
        return true;
    }

    /// <summary>WebView2 用户数据由旧安装产生，staging 里只有空骨架：以旧目录数据为准整体搬迁。</summary>
    private static void CarryWebView2UserData(string oldDir, string installDir, bool hadOldInstall, Action<string> log)
    {
        if (!hadOldInstall)
        {
            return;
        }

        var oldData = Path.Combine(oldDir, WebView2DataDirName);
        if (!Directory.Exists(oldData))
        {
            return;
        }

        try
        {
            var newData = Path.Combine(installDir, WebView2DataDirName);
            if (Directory.Exists(newData))
            {
                Directory.Delete(newData, recursive: true); // staging 空骨架。
            }

            Directory.Move(oldData, newData);
            log("webview2 user data carried over");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"webview2 user data carry-over failed (will rebuild): {ex.Message}");
        }
    }

    private static bool TryMoveWithRetry(string from, string to, Action<string> log)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(to))
                {
                    TryDeleteDirectory(to);
                }

                Directory.Move(from, to);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 2)
                {
                    log($"directory move failed: {from} -> {to}: {ex.Message}");
                    return false;
                }

                Thread.Sleep(TimeSpan.FromSeconds(2)); // 杀软扫描窗口期重试一次。
            }
        }
    }

    private static void CleanupLeftover(string[] directories, Action<string> log)
    {
        foreach (var directory in directories)
        {
            if (Directory.Exists(directory))
            {
                log($"cleaning leftover from previous update: {directory}");
                TryDeleteDirectory(directory);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不阻塞安装主流程。
        }
    }
}
