namespace DSHSharp.Core.Services;

/// <summary>
/// WebView 导航分流策略：DSH WebUI 自身（以及 WebView 内部机制地址）留在内嵌视图，
/// 其余目标（外站 http/https、mailto 等非 web 协议）交给系统默认程序处理，
/// 避免内嵌窗口被外部页面劫持或出现无法回退的跳转。
/// </summary>
public static class ExternalLinkPolicy
{
    /// <summary>WebView 内部机制使用的伪协议，导航必须留在视图内。</summary>
    private static readonly HashSet<string> InternalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "data",
        "blob",
        "chrome",
        "edge",
        "file",
    };

    /// <summary>
    /// 判断一次导航是否应转交系统处理。
    /// 服务地址未知（runtime 未就绪）时一律放行内嵌，避免误伤启动流程。
    /// </summary>
    public static bool ShouldOpenExternally(Uri? request, Uri? runtimeOrigin)
    {
        if (request is null)
        {
            return false;
        }

        if (InternalSchemes.Contains(request.Scheme))
        {
            return false;
        }

        if (request.Scheme is not ("http" or "https"))
        {
            // mailto:/tel:/自定义协议：交给系统。
            return true;
        }

        if (runtimeOrigin is null)
        {
            return false;
        }

        // WebUI 自身可能以 127.0.0.1 或 localhost 互写；回环 + 同端口视为同一服务。
        return !(request.IsLoopback && request.Port == runtimeOrigin.Port);
    }
}
