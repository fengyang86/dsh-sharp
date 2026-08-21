using System.Runtime.InteropServices;

namespace DSHSharp.Services;

/// <summary>
/// 通知音效：播放 Windows 系统提示音（SystemAsterisk）。
/// 非 Windows 平台或播放失败时静默降级，不影响通知功能。
/// </summary>
internal static partial class NotificationSound
{
    private const uint SndAlias = 0x0001_0000; // pszSound 为系统别名
    private const uint SndAsync = 0x0000_0001; // 异步播放，不阻塞

    public static void Play()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                PlaySoundW("SystemAsterisk", 0, SndAlias | SndAsync);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 极老系统缺少 winmm：静默降级。
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(string pszSound, nint hmod, uint fdwSound);
}
