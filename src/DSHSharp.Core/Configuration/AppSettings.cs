namespace DSHSharp.Core.Configuration;

/// <summary>
/// DSH-Sharp 应用设置。字段与 settings.json 一一对应，
/// 由 <c>AppSettingsService</c> 负责持久化。
/// </summary>
public sealed class AppSettings
{
    /// <summary>DSH WebUI 默认地址（本机 Harness 服务）。</summary>
    public const string DefaultWebUrl = "http://127.0.0.1:3080";

    /// <summary>要嵌入的 DSH WebUI 地址。</summary>
    public string WebUrl { get; set; } = DefaultWebUrl;

    /// <summary>登录时自启动（以系统注册表/启动项实际状态为准）。</summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>关闭窗口时最小化到托盘而非退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>启动后直接最小化到托盘（静默启动）。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>主题：System / Light / Dark。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>会话完成时弹出桌面通知。</summary>
    public bool SessionCompleteNotifications { get; set; } = true;
}
