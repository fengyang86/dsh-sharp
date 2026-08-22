using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DSHSharp.Core.Configuration;
using DSHSharp.ViewModels;

namespace DSHSharp.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private ToastWindow? _toast;

    /// <summary>供 XAML 运行时加载器/设计器使用。</summary>
    public MainWindow()
        : this(new AppSettings())
    {
    }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        var viewModel = new MainWindowViewModel(settings);
        DataContext = viewModel;

        Web.Source = new Uri(_settings.WebUrl);

        WindowStateProperty.Changed.AddClassHandler<Window>(OnWindowStateChanged);
        Opened += (_, _) => RestoreWindowState();
        Closing += OnClosing;
    }

    /// <summary>启动时恢复上次的窗口位置/大小/最大化状态。</summary>
    private void RestoreWindowState()
    {
        var settings = App.Instance?.Settings;
        if (settings is null)
        {
            return;
        }

        if (settings.WindowWidth is > 0 && settings.WindowHeight is > 0)
        {
            Width = settings.WindowWidth.Value;
            Height = settings.WindowHeight.Value;
        }

        if (settings.WindowLeft is not null && settings.WindowTop is not null)
        {
            Position = new PixelPoint((int)settings.WindowLeft.Value, (int)settings.WindowTop.Value);
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnWindowStateChanged(Window window, AvaloniaPropertyChangedEventArgs e) =>
        UpdateMaximizeButton();

    /// <summary>最大化按钮图标随窗口状态切换：正常 ↔ 最大化。</summary>
    private void UpdateMaximizeButton()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !isMaximized;
        RestoreIcon.IsVisible = isMaximized;
        ToolTip.SetTip(MaximizeButton, isMaximized ? "还原" : "最大化");
    }

    /// <summary>
    /// 显示"会话完成"等通知：置顶 Toast 小窗口。
    /// 标题为会话名，内容为回复开头预览（可空）。
    /// </summary>
    public void ShowNotification(string title, string message, string? preview = null)
    {
        try
        {
            _toast?.Close();
            _toast = new ToastWindow(title, message, preview);
            _toast.Show();
            App.Log($"toast shown: '{title}'");
        }
        catch (Exception ex)
        {
            App.Log($"toast show failed: {ex}");
        }
    }

    /// <summary>服务离线时隐藏 WebView（其原生表面会遮挡引导页），在线时恢复。</summary>
    public void SetWebViewVisible(bool visible) => Web.IsVisible = visible;

    /// <summary>重载 WebView 到新地址（服务地址切换）。</summary>
    public void ReloadWeb(string url) => Web.Source = new Uri(url);

    /// <summary>显示端口纠错提示：发现其他端口有服务时建议切换。</summary>
    public void ShowPortHint(int port, Action<int> onSwitch)
    {
        _portHintPort = port;
        _portSwitchAction = onSwitch;
        PortHintText.Text = $"检测到 127.0.0.1:{port} 上有 DSH 服务，但当前配置指向 {App.Instance?.Settings.WebUrl ?? ""}。是否切换到 {port} 端口？";
        PortHintPanel.IsVisible = true;
        PortSwitchButton.IsEnabled = true;
    }

    private Action<int>? _portSwitchAction;
    private int _portHintPort;

    private void PortSwitchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PortSwitchButton.IsEnabled = false;
        var action = _portSwitchAction;
        _portSwitchAction = null;
        action?.Invoke(_portHintPort);
    }

    /// <summary>显示/隐藏"未检测到服务"引导页。</summary>
    public void ShowOnboarding(bool show, string? detail, bool busy)
    {
        OnboardingPanel.IsVisible = show;
        if (detail is not null)
        {
            OnboardingDetailText.Text = detail;
        }

        var app = App.Instance;
        var owned = app?.IsServiceOwned ?? false;
        StartServiceButton.IsEnabled = !busy;
        StartServiceButton.Content = busy
            ? "正在启动…"
            : owned
                ? "停止本地服务"
                : "启动本地服务";
    }

    private void StartServiceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var app = App.Instance;
        if (app is null)
        {
            return;
        }

        if (app.IsServiceOwned)
        {
            app.StopManagedService();
        }
        else
        {
            _ = app.StartManagedServiceAsync();
        }
    }

    private void RetryProbeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var app = App.Instance;
        if (app is null)
        {
            return;
        }

        if (app.IsServiceOwned)
        {
            app.StopManagedService();
        }
        else
        {
            _ = app.StartManagedServiceAsync();
        }
    }

    // ---- 关闭到托盘 ----

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var isExiting = App.Instance is { IsExiting: true };
        App.Log($"window closing: closeToTray={_settings.CloseToTray}, isExiting={isExiting}");
        if (_settings.CloseToTray && !isExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    // ---- 自定义标题栏 ----

    private void TitleBar_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        App.Instance?.OpenSettingsWindow();

    private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();
}
