using System.Runtime.InteropServices;

namespace DSHSharp.Services;

/// <summary>托盘图标的鼠标交互（由消息窗口线程分发到调用方线程）。</summary>
public sealed class NotifyIconClickedEventArgs
{
    /// <summary>点击类型：左键单击 / 左键双击 / 右键。</summary>
    public required string Kind { get; init; }

    /// <summary>点击瞬间的光标位置（设备像素）。</summary>
    public required int CursorX { get; init; }

    public required int CursorY { get; init; }
}

/// <summary>
/// 自研系统托盘图标（Shell_NotifyIcon）：完全接管左键/右键/双击消息，
/// 不使用 Avalonia TrayIcon（其右键弹出位置/样式写死、动态菜单不可靠）。
/// 消息窗口在 UI 线程创建（Avalonia Dispatcher 输入队列同源），回调天然在 UI 线程。
/// 仅 Windows；其他平台静默不安装。
/// </summary>
public sealed class NotifyIconService : IDisposable
{
    private const uint WmAppCallback = 0x8FFF; // WM_APP 区段，避开系统/常见框架占用
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04;
    private const uint NimAdd = 0x00, NimModify = 0x01, NimDelete = 0x02;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmLButtonDblClk = 0x0203;
    private const int WmContextMenu = 0x007B; // Vista+：Shell 以此消息转发图标右键，lParam = 图标屏幕坐标

    /// <summary>左键单击（防抖后的最终判定由调用方完成）。</summary>
    public event Action<NotifyIconClickedEventArgs>? Clicked;

    /// <summary>右键释放。</summary>
    public event Action<NotifyIconClickedEventArgs>? RightClicked;

    /// <summary>左键双击。</summary>
    public event Action<NotifyIconClickedEventArgs>? DoubleClicked;

    private readonly string _tip;
    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;
    private bool _iconAdded;
    private bool _disposed;
    private GCHandle _selfHandle;

    public NotifyIconService(string tip) => _tip = tip;

    /// <summary>在 UI 线程调用：创建消息窗口并添加托盘图标。</summary>
    public bool TryInstall(IntPtr iconHandle)
    {
        if (!OperatingSystem.IsWindows() || _iconAdded)
        {
            return false;
        }

        try
        {
            _selfHandle = GCHandle.Alloc(this);
            _wndProc = WndProc;
            var wndClass = new WndClassW
            {
                CbSize = (uint)Marshal.SizeOf<WndClassW>(),
                Style = 0,
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                CbClsExtra = 0,
                CbWndExtra = 0,
                HInstance = Marshal.GetHINSTANCE(typeof(NotifyIconService).Module),
                HIcon = IntPtr.Zero,
                HCursor = IntPtr.Zero,
                HbrBackground = IntPtr.Zero,
                LpszMenuName = null,
                LpszClassName = "DSHSharpNotifyIconWnd",
                HIconSm = IntPtr.Zero,
            };
            RegisterClassExW(ref wndClass);
            _hwnd = CreateWindowExW(
                0, "DSHSharpNotifyIconWnd", "DSHSharpNotifyIcon", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero,
                Marshal.GetHINSTANCE(typeof(NotifyIconService).Module), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            _iconAdded = ShellAddModify(NimAdd, iconHandle, _tip);
            return _iconAdded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>更新悬停提示文本（超长自动截断，NOTIFYICONDATA 上限 127 字符）。</summary>
    public void UpdateTip(string tip)
    {
        if (!_iconAdded || _disposed)
        {
            return;
        }

        if (tip.Length > 127)
        {
            tip = tip[..127];
        }

        ShellAddModify(NimModify, IntPtr.Zero, tip);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmAppCallback && !_disposed)
        {
            if (msg == (uint)WmContextMenu)
            {
                // 新版 Shell 行为：lParam 低字/高字 = 图标屏幕坐标（比光标可靠——不随鼠标移动）。
                GetCursorPos(out var pt);
                var args = new NotifyIconClickedEventArgs
                {
                    Kind = "right",
                    CursorX = pt.X,
                    CursorY = pt.Y,
                };
                RightClicked?.Invoke(args);
                return IntPtr.Zero;
            }

            var mouse = lParam.ToInt64() & 0xFFFF;
            if (mouse is WmLButtonUp or WmRButtonUp or WmLButtonDblClk)
            {
                GetCursorPos(out var pt);
                var args = new NotifyIconClickedEventArgs
                {
                    Kind = mouse == WmRButtonUp ? "right" : mouse == WmLButtonDblClk ? "double" : "left",
                    CursorX = pt.X,
                    CursorY = pt.Y,
                };
                switch (mouse)
                {
                    case WmLButtonUp:
                        Clicked?.Invoke(args);
                        break;
                    case WmRButtonUp:
                        // 旧版 Shell 行为（未设 uVersion）：右键在此处。
                        RightClicked?.Invoke(args);
                        break;
                    case WmLButtonDblClk:
                        DoubleClicked?.Invoke(args);
                        break;
                }
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>消息窗口是否已创建（区分"窗口失败"与"图标添加失败"）。</summary>
    public bool HasMessageWindow => _hwnd != IntPtr.Zero;

    private bool ShellAddModify(uint action, IntPtr iconHandle, string tip)
    {
        var data = new NotifyIconDataW
        {
            CbSize = (uint)Marshal.SizeOf<NotifyIconDataW>(),
            HWnd = _hwnd,
            UId = 1,
            UFlags = NifMessage | NifTip | (iconHandle != IntPtr.Zero ? NifIcon : 0),
            UCallbackMessage = WmAppCallback,
            HIcon = iconHandle,
            SzTip = tip,
        };
        return Shell_NotifyIconW(action, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_iconAdded)
        {
            var data = new NotifyIconDataW
            {
                CbSize = (uint)Marshal.SizeOf<NotifyIconDataW>(),
                HWnd = _hwnd,
                UId = 1,
            };
            Shell_NotifyIconW(NimDelete, ref data);
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconDataW
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszClassName;
        public IntPtr HIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconDataW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
