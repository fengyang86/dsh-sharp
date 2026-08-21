namespace DSHSharp.Core.Configuration;

/// <summary>
/// 服务配置（Profile）的迁移与激活逻辑（纯函数，便于单元测试）。
/// </summary>
public static class ProfileHelper
{
    /// <summary>
    /// 确保 Profiles 非空：旧版配置（无 Profiles 字段）从顶层字段迁移出默认配置。
    /// 调用时机：设置加载后、首次使用前。
    /// </summary>
    public static void EnsureDefaultProfile(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Profiles.Count > 0)
        {
            return;
        }

        settings.Profiles.Add(new ServiceProfile
        {
            Name = "默认配置",
            WebUrl = settings.WebUrl,
            ManagedMode = settings.ManagedMode,
            SourcePath = settings.SourcePath,
        });
        settings.ActiveProfileName = "默认配置";
    }

    /// <summary>获取激活配置；名称不匹配时回退第一个并修正 ActiveProfileName。</summary>
    public static ServiceProfile GetActiveProfile(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Profiles.Count == 0)
        {
            EnsureDefaultProfile(settings);
        }

        var profile = settings.Profiles.FirstOrDefault(p => p.Name == settings.ActiveProfileName);
        if (profile is null)
        {
            profile = settings.Profiles[0];
            settings.ActiveProfileName = profile.Name;
        }

        return profile;
    }

    /// <summary>把激活配置同步到顶层字段（监控/状态栏等旧路径直接读 WebUrl 等）。</summary>
    public static void ApplyActiveProfile(AppSettings settings)
    {
        var profile = GetActiveProfile(settings);
        settings.WebUrl = profile.WebUrl;
        settings.ManagedMode = profile.ManagedMode;
        settings.SourcePath = profile.SourcePath;
    }

    /// <summary>
    /// 切换激活配置并同步顶层字段。
    /// 返回是否真的发生了切换（目标不存在或已是当前时返回 false）。
    /// </summary>
    public static bool ActivateProfile(AppSettings settings, string name)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Profiles.FirstOrDefault(p => p.Name == name) is null ||
            settings.ActiveProfileName == name)
        {
            return false;
        }

        settings.ActiveProfileName = name;
        ApplyActiveProfile(settings);
        return true;
    }
}
