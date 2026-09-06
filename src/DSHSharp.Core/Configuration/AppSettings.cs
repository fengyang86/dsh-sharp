namespace DSHSharp.Core.Configuration;

/// <summary>
/// 旧版服务连接配置。仅用于读取和迁移历史 settings.json，新的设置文件不再写出。
/// </summary>
public sealed class ServiceProfile
{
    public string Name { get; set; } = "默认配置";

    public string WebUrl { get; set; } = AppSettings.DefaultWebUrl;

    /// <summary>Npx / Source / None。</summary>
    public string ManagedMode { get; set; } = "Npx";

    public string? SourcePath { get; set; }
}

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

    /// <summary>会话完成通知附带提示音。</summary>
    public bool NotificationSoundEnabled { get; set; } = true;

    /// <summary>
    /// 旧版托管模式字段。仅用于读取和迁移历史 settings.json。
    /// </summary>
    public string ManagedMode { get; set; } = "Npx";

    /// <summary>旧版源码路径字段。仅用于读取和迁移历史 settings.json。</summary>
    public string? SourcePath { get; set; }

    /// <summary>上次窗口位置/大小（null 表示未记录，使用默认布局）。</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    /// <summary>上次窗口是否最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>旧版连接配置列表。仅用于读取和迁移历史 settings.json。</summary>
    public List<ServiceProfile> Profiles { get; set; } = [];

    /// <summary>旧版激活配置名。仅用于读取和迁移历史 settings.json。</summary>
    public string ActiveProfileName { get; set; } = "默认配置";
}
