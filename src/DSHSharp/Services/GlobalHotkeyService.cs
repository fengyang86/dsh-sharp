using System.Runtime.InteropServices;

namespace DSHSharp.Services;

/// <summary>
/// OS 级全局热键：在 UI 线程创建消息窗口接收 WM_HOTKEY（Avalonia 的 Win32 消息循环负责分发）。
/// 热键被其他程序占用时记日志降级，不影响启动。仅 Windows；其他平台 TryInstall 直接返回 false。
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    public const int HotkeyShowWindow = 1;
    public const int HotkeyOpenSwitcher = 2;

    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const nint HwndMessage = -3;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    private readonly Action<int> _onHotkey;
    private WndProc? _wndProc;
    private nint _hwnd;
    private ushort _atom;

    public GlobalHotkeyService(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
    }

    /// <summary>注册 Ctrl+Alt+D（唤起窗口）与 Ctrl+Alt+K（唤起并呼出切换器）。失败返回 false 并带原因。</summary>
    public bool TryInstall(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "全局热键仅支持 Windows";
            return false;
        }

        try
        {
            _wndProc = WndProcImpl;
            var windowClass = new WindowClass
            {
                WndProc = _wndProc,
                ClassName = "DSHSharpHotkeyWindow",
            };
            _atom = RegisterClass(ref windowClass);
            if (_atom == 0)
            {
                error = $"RegisterClass 失败（{Marshal.GetLastWin32Error()}）";
                return false;
            }

            _hwnd = CreateWindowEx(0, "DSHSharpHotkeyWindow", string.Empty, 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
            if (_hwnd == 0)
            {
                error = $"CreateWindow 失败（{Marshal.GetLastWin32Error()}）";
                return false;
            }

            if (!RegisterHotKey(_hwnd, HotkeyShowWindow, ModControl | ModAlt, 'D'))
            {
                App.Log("global hotkey Ctrl+Alt+D 注册失败（可能被占用）");
            }

            if (!RegisterHotKey(_hwnd, HotkeyOpenSwitcher, ModControl | ModAlt, 'K'))
            {
                App.Log("global hotkey Ctrl+Alt+K 注册失败（可能被占用）");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private nint WndProcImpl(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmHotkey)
        {
            _onHotkey((int)wParam);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() || _hwnd == 0)
        {
            return;
        }

        try
        {
            _ = UnregisterHotKey(_hwnd, HotkeyShowWindow);
            _ = UnregisterHotKey(_hwnd, HotkeyOpenSwitcher);
            _ = DestroyWindow(_hwnd);
            if (_atom != 0)
            {
                _ = UnregisterClass("DSHSharpHotkeyWindow", 0);
            }
        }
        catch (Exception ex)
        {
            App.Log($"global hotkey dispose failed: {ex.Message}");
        }
        finally
        {
            _hwnd = 0;
            _atom = 0;
            _wndProc = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WndProc WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern short UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
}
