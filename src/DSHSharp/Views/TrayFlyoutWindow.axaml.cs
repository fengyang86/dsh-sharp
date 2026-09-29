using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DSHSharp.Views;

/// <summary>托盘快捷面板的一个会话条目。</summary>
public sealed record TraySessionItem(string SessionId, string Title, bool Running);

/// <summary>
/// 托盘左键弹出的品牌化快捷面板（右键菜单仍为系统原生绘制，无法注入样式；
/// 本面板承载样式化体验）：状态头部 + 快捷动作 + 最近会话 + Runtime 控制。
/// 失焦自动关闭；出现于主屏工作区右下角（托盘上方）。
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    /// <summary>点击"主窗口"。</summary>
    public required Action OpenMain { get; init; }

    /// <summary>点击"新建会话"。</summary>
    public required Action NewSession { get; init; }

    /// <summary>点击"设置"。</summary>
    public required Action OpenSettings { get; init; }

    /// <summary>点击某个最近会话。</summary>
    public required Action<string> OpenSession { get; init; }

    /// <summary>点击 Runtime 启动/停止。</summary>
    public required Action ToggleRuntime { get; init; }

    /// <summary>点击退出。</summary>
    public required Action Exit { get; init; }

    private bool _wired;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
    }

    /// <summary>动作接线（required 回调在对象初始化完成后才可安全使用）。</summary>
    private void WireActions()
    {
        if (_wired) return;
        _wired = true;
        OpenMainAction!.PointerPressed += (_, _) => { Close(); OpenMain(); };
        SettingsAction!.PointerPressed += (_, _) => { Close(); OpenSettings(); };
        NewSessionButton!.Click += (_, _) => { Close(); NewSession(); };
        RuntimeButton!.Click += (_, _) => { Close(); ToggleRuntime(); };
        ExitButton!.Click += (_, _) => { Close(); Exit(); };
    }

    /// <summary>填充内容并显示（右下角定位）。UI 线程调用。</summary>
    public void Present(
        bool serviceOnline,
        int runningCount,
        string runtimeStatus,
        bool runtimeOwned,
        IReadOnlyList<TraySessionItem> sessions,
        PixelPoint? anchor = null)
    {
        _explicitAnchor = anchor;
        _trayAnchor = null; // 每次弹出重新取锚。
        WireActions();
        StatusText!.Text = serviceOnline
            ? runningCount > 0 ? $"在线 · {runningCount} 个会话运行中" : "在线"
            : "Runtime 未运行";
        StatusDot!.Fill = new SolidColorBrush(Color.Parse(serviceOnline ? "#3DD66E" : "#8A8F98"));

        SessionList!.Children.Clear();
        if (sessions.Count == 0)
        {
            SessionList!.Children.Add(new TextBlock
            {
                Text = "（暂无会话）",
                Classes = { "tf-sub" },
                Margin = new Thickness(6, 4),
            });
        }
        else
        {
            foreach (var session in sessions.Take(6))
            {
                var id = session.SessionId;
                var title = string.IsNullOrEmpty(session.Title) ? ShortId(id) : session.Title;
                var row = new Border { Classes = { "tf-session" } };
                row.Child = new TextBlock
                {
                    Text = session.Running ? "● " + title : title,
                    Classes = { "tf-session-title" },
                };
                row.PointerPressed += (_, _) =>
                {
                    Close();
                    OpenSession(id);
                };
                SessionList.Children.Add(row);
            }
        }

        RuntimeText!.Text = runtimeStatus;
        RuntimeButton!.Content = runtimeOwned ? "停止" : "启动";

        Topmost = true; // 托盘飞出层（Shell 弹出层）会压住非置顶窗口。
        Show();
        Activate();
        // SizeToContent=Height 下首次布局前高度为 NaN/0（定位会算出屏幕外坐标）：
        // Show 后等首次布局完成（SizeChanged）再定位。一次性挂接。
        SizeChanged += OnFirstLayoutPosition;
    }

    private void OnFirstLayoutPosition(object? sender, Avalonia.Controls.SizeChangedEventArgs e)
    {
        // SizeToContent 分段布局会触发多次（头部先→内容展开到最终高度）。
        // 每次无脑重定位（纯计算微秒级），不再做"稳定检测"——防抖回调偶发不触发
        // 导致面板按过时高度定位、底部越出屏幕（用户实测 09:08 案例）。
        PositionAtTray();
        App.Log($"flyout repositioned: pos={Position} size={Bounds.Width}x{Bounds.Height}");
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 面板语义：点击别处即收起。延迟挂接：托盘点击瞬间宿主窗口可能仍持激活态，
        // 立即挂接会在 Show/Activate 之前被一次 Deactivated 误关（表现为"左键没反应"）。
        _ = DispatcherTimer.RunOnce(
            () => Deactivated += OnDeactivatedClose,
            TimeSpan.FromMilliseconds(250));
    }

    private void OnDeactivatedClose(object? sender, EventArgs e) => Close();

    /// <summary>定位到托盘图标旁：优先用点击瞬间的光标位置定位所在屏幕（多显示器下托盘
    /// 不一定在主屏），失败回退主屏；任务栏边（底部/顶部/左/右）由工作区与全屏的差值推断，
    /// 面板锚到对应角落。Position 是设备像素，Width/Height 是 DIP——先乘 scaling 换算。</summary>
    // Present 时捕获的托盘锚点（设备像素）：面板生命周期内固定，二次校准不重读光标。
    private PixelPoint? _trayAnchor;
    // 外部显式锚点（右键传入光标位置）；非空时优先于自动读取。
    private PixelPoint? _explicitAnchor;

    private void PositionAtTray()
    {
        _trayAnchor ??= _explicitAnchor ?? TryGetCursorPos();
        var cursor = _trayAnchor;
        var screen = cursor is { } point
            ? System.Linq.Enumerable.FirstOrDefault(Screens.All, sc => sc.Bounds.Contains(point))
              ?? Screens.Primary
            : Screens.Primary ?? System.Linq.Enumerable.FirstOrDefault(Screens.All);
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        // SizeToContent=Height 下布局前 Height 可能为 NaN，用 Bounds.Height 兜底后再定位。
        var heightDip = double.IsNaN(Height) ? Bounds.Height : Height;
        Position = ComputeFlyoutPosition(
            screen.Bounds,
            screen.WorkingArea,
            (int)(Width * scale),
            (int)(heightDip * scale),
            _trayAnchor?.X ?? screen.Bounds.X + screen.Bounds.Width,
            _trayAnchor?.Y ?? screen.Bounds.Y + screen.Bounds.Height);
    }

    /// <summary>按任务栏方位计算面板锚点（设备像素）。任务栏边 = 全屏与工作区之差所在的边：
    /// 底部栏（默认）→ 面板右下角锚定光标 X（托盘图标）上方，向左上展开；
    /// 顶部栏 → 光标 X 下方右上；左/右栏 → 面板贴工作区边、垂直锚光标。</summary>
    internal static PixelPoint ComputeFlyoutPosition(
        PixelRect bounds, PixelRect workingArea, int flyoutWidth, int flyoutHeight,
        int anchorX, int anchorY)
    {
        // 所有锚定以"可见卡片"为准：卡片边缘 = 窗口外框边缘 ∓ 14（根 Border 的阴影占位区）。
        const int cardInset = 14;
        // PixelRect 只有 X/Y/Width/Height（无 Top/Left 边界属性）。
        var taskbarOnTop = workingArea.Y > bounds.Y;
        var taskbarOnLeft = workingArea.X > bounds.X;
        // 水平：卡片左缘 = 图标 X（面板从图标向右上方展开，图标在面板左下角）。
        var x = taskbarOnLeft
            ? workingArea.X - cardInset
            : Math.Clamp(anchorX - cardInset, workingArea.X - cardInset,
                workingArea.X + workingArea.Width - flyoutWidth + cardInset);
        // 垂直：卡片底缘 = 图标 Y（锚点）——图标可能在任务栏上（y>工作区底）或系统托盘
        // 弹出层中（y<工作区底），两种情况都直接贴住图标位置。
        // 底部栏时面板从图标向上展开（cardBottom=anchorY）；顶部栏时从图标向下展开。
        var y = taskbarOnTop
            ? anchorY - cardInset
            : anchorY - flyoutHeight + cardInset;
        return new PixelPoint(x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private static PixelPoint? TryGetCursorPos() =>
        OperatingSystem.IsWindows() && GetCursorPos(out var point)
            ? new PixelPoint(point.X, point.Y)
            : null;

    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];
}
