namespace DSHSharp.Services;

/// <summary>
/// 会话完成通知门面：Windows 构建优先走原生 Toast（操作中心、点击直达会话），
/// 原生通道不可用时返回 false，由调用方回退到应用内 ToastWindow。
/// </summary>
internal static class SessionNotifications
{
    internal static void EnsureInitialized(Action<string> onSessionNavigate)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            NativeToast.EnsureInitialized(onSessionNavigate);
        }
#endif
    }

    /// <summary>尝试以原生 Toast 展示；成功 true（含系统默认音），不可用或失败 false。</summary>
    internal static bool TryShowSessionCompleted(string sessionId, string title, string? preview)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                NativeToast.ShowSessionCompleted(sessionId, title, preview);
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"native toast failed, falling back: {ex.Message}");
            }
        }
#endif
        return false;
    }

    internal static void Uninitialize()
    {
#if WINDOWS
        NativeToast.Uninitialize();
#endif
    }
}
