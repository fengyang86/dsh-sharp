#if WINDOWS
using CommunityToolkit.WinUI.Notifications;

namespace DSHSharp.Services;

/// <summary>
/// Windows 原生 Toast 通道：进入操作中心、全屏/锁屏可见，点击激活回传会话参数。
/// 非打包应用的 AUMID 快捷方式与 COM 激活注册由 Toolkit 兼容层在首次 Show 时处理；
/// 进程退出后 COM 服务器随之注销，操作中心残留条目点击无效（已知可接受）。
/// </summary>
internal static class NativeToast
{
    private static int _wired;

    /// <summary>注册激活回调（幂等）：toast 点击时解析 session 参数并导航到该会话。</summary>
    internal static void EnsureInitialized(Action<string> onSessionNavigate)
    {
        if (Interlocked.Exchange(ref _wired, 1) == 1)
        {
            return;
        }

        ToastNotificationManagerCompat.OnActivated += args =>
        {
            try
            {
                var session = TryGetArgument(args.Argument, "session");
                if (session is not null)
                {
                    onSessionNavigate(session);
                }
            }
            catch (Exception ex)
            {
                App.Log($"toast activation failed: {ex.Message}");
            }
        };
    }

    /// <summary>展示会话回合结局通知（系统默认提示音）：toastTitle 为"会话已完成/会话回合失败"。
    /// 同一 Header 分组：操作中心里聚合在"DSH-Sharp 会话动态"下，不逐条堆叠。</summary>
    internal static void ShowSessionCompleted(string sessionId, string toastTitle, string sessionName, string? preview)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("session", sessionId)
            .AddHeader("dshsharp-sessions", "DSH-Sharp 会话动态", string.Empty)
            .AddText(toastTitle)
            .AddText(sessionName);
        if (!string.IsNullOrEmpty(preview))
        {
            builder.AddText(preview);
        }

        builder.Show();
    }

    /// <summary>退出时注销 COM 激活服务器（7.1.2 WinUI 包的 API 名为 Uninstall）。</summary>
    internal static void Uninitialize()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception ex)
        {
            App.Log($"toast uninitialize failed: {ex.Message}");
        }
    }

    /// <summary>Toolkit 的激活参数格式为 "k=v;k2=v2"，值经 URL 转义。</summary>
    private static string? TryGetArgument(string arguments, string key)
    {
        foreach (var pair in arguments.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
    }
}
#endif
