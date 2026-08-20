namespace DSHSharp.Core.Services;

/// <summary>登录自启动开关的抽象（Windows 注册表 / 其他平台空实现）。</summary>
public interface IAutoStartService
{
    /// <summary>当前系统层面是否已启用自启动。</summary>
    bool IsEnabled();

    /// <summary>启用或停用登录自启动。</summary>
    void SetEnabled(bool enabled);
}
