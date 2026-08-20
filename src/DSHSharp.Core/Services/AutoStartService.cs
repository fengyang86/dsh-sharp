using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DSHSharp.Core.Services;

/// <summary>
/// Windows 实现：写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，
/// 注册命令附带 <c>--autostart</c> 参数以便启动后静默到托盘。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DSHSharp";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定当前可执行文件路径。");
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(ValueName, $"\"{exe}\" --autostart");
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>非 Windows 平台的空实现（后续可按平台补充 LaunchAgent / autostart .desktop）。</summary>
public sealed class UnsupportedAutoStartService : IAutoStartService
{
    public bool IsEnabled() => false;

    public void SetEnabled(bool enabled)
    {
        // 无操作
    }
}

public static class AutoStartServiceFactory
{
    public static IAutoStartService Create() =>
        OperatingSystem.IsWindows()
            ? new WindowsAutoStartService()
            : new UnsupportedAutoStartService();
}
