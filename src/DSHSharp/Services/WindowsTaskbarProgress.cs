using System.Runtime.InteropServices;

namespace DSHSharp.Services;

/// <summary>
/// 任务栏进度指示（ITaskbarList3 COM 互操作）：有会话运行时以"不定进度"脉冲条提示，
/// 全部结束后清除。仅 Windows 运行时生效，其他平台为空操作。
/// </summary>
internal static class WindowsTaskbarProgress
{
    private const int TbpfNoProgress = 0;
    private const int TbpfIndeterminate = 1;

    private static readonly object Gate = new();
    private static ITaskbarList3? _instance;

    internal static void SetRunningIndicator(nint windowHandle, bool running)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                _instance ??= CreateTaskbarList();
                _instance?.SetProgressState(windowHandle, running ? TbpfIndeterminate : TbpfNoProgress);
            }
        }
        catch (Exception ex)
        {
            App.Log($"taskbar progress state failed: {ex.Message}");
        }
    }

    private static ITaskbarList3? CreateTaskbarList()
    {
        var list = (ITaskbarList3)new CoTaskbarList();
        list.HrInit();
        return list;
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] // CLSID_TaskbarList（注意 FD6D 是 coclass；FD6E 是 IID 家族）
    private class CoTaskbarList
    {
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3（后续成员未声明：互操作只需到 SetProgressState 为止的正确 vtable 顺序）
        void SetProgressValue(nint hwnd, ulong completed, ulong total);

        void SetProgressState(nint hwnd, int flags);
    }
}
