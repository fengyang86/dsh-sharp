using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DSHSharp.Core.Configuration;
using DSHSharp.Core.Services;
using DSHSharp.ViewModels;

namespace DSHSharp.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private ToastWindow? _toast;
    // WebView 惰性创建：缺 WebView2 运行时的机器上控件初始化会失败，必须保证窗口与引导页先活下来。
    private NativeWebView? _webView;

    /// <summary>供 XAML 运行时加载器/设计器使用。</summary>
    public MainWindow()
        : this(new AppSettings())
    {
    }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        SplashVersionText.Text = $"v{DSHSharp.Core.Compatibility.DshSharpCompatibility.ProductVersion}";

        // 主题化诊断（排查用）：窗口 variant 与过场背景在构造期的实际解析值。
        Opened += (_, _) =>
        {
            var bg = SplashPanel.Background as Avalonia.Media.LinearGradientBrush;
            var stop = bg?.GradientStops is { Count: > 0 } stops ? stops[0].Color.ToString() : "null";
            App.Log($"splash theme probe: window-actual={ActualThemeVariant}, splash-stop0={stop}");
        };

        var viewModel = new MainWindowViewModel(settings);
        DataContext = viewModel;

        WindowStateProperty.Changed.AddClassHandler<Window>(OnWindowStateChanged);
        Opened += (_, _) => RestoreWindowState();
        Closing += OnClosing;
    }

    /// <summary>
    /// 按需创建 WebView（幂等）。创建失败（典型：WebView2 运行时缺失）返回 false，
    /// 引导页保持可见并显示安装/修复进度；运行时装好后下一次调用会重建成功。
    /// </summary>
    public bool TryPrepareWebView()
    {
        if (_webView is not null)
        {
            return true;
        }

        try
        {
            var web = new NativeWebView { IsVisible = false };
            // 外链分流：非 WebUI 自身的目标交给系统，内嵌视图只承载 DSH。
            web.NavigationStarted += OnWebViewNavigationStarted;
            web.NewWindowRequested += OnWebViewNewWindowRequested;
            // 主题桥：WebUI 经 chrome.webview.postMessage 上报主题选择，壳在 System 模式下镜像。
            web.WebMessageReceived += OnWebMessageReceived;
            WebHost.Child = web;
            _webView = web;
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"webview initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>当前 Runtime 的源（scheme+host+port）；未知时为 null，分流策略届时全部放行内嵌。</summary>
    private static Uri? CurrentRuntimeOrigin()
    {
        var url = App.Instance?.RuntimeUrl;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private void OnWebViewNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (!ExternalLinkPolicy.ShouldOpenExternally(e.Request, CurrentRuntimeOrigin()))
        {
            return;
        }

        e.Cancel = true;
        OpenInSystemBrowser(e.Request!);
    }

    /// <summary>WebUI 主题桥消息（插件经 chrome.webview.postMessage 发送）。</summary>
    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Body))
        {
            return;
        }

        App.Instance?.ApplyWebThemeMessage(e.Body);
    }

    private void OnWebViewNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)    {
        e.Handled = true;
        if (e.Request is { } request && !ExternalLinkPolicy.ShouldOpenExternally(request, CurrentRuntimeOrigin()))
        {
            // 指向 WebUI 自身的新窗口没有独立认证上下文，就地导航主视图。
            if (_webView is not null)
            {
                _webView.Source = request;
            }
            return;
        }

        if (e.Request is not null)
        {
            OpenInSystemBrowser(e.Request);
        }
    }

    private static void OpenInSystemBrowser(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.OriginalString) { UseShellExecute = true });
            App.Log($"external link opened in system browser: {url.Scheme}://{url.Authority}");
        }
        catch (Exception ex)
        {
            App.Log($"open external link failed ({url}): {ex.Message}");
        }
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
    public void SetWebViewVisible(bool visible)
    {
        if (_webView is not null)
        {
            _webView.IsVisible = visible;
        }
    }

    /// <summary>重载 WebView 到新地址（服务地址切换）。WebView 尚不可用时返回 false，由调用方保持引导页。</summary>
    public bool ReloadWeb(string url)
    {
        if (!TryPrepareWebView())
        {
            return false;
        }

        // WebView2 可能仍停留在上一次无令牌导航；先清空再设置，确保认证地址真正触发新导航。
        _webView!.Source = new Uri("about:blank");
        _webView.Source = new Uri(url);
        _webView.IsVisible = true;
        App.Log($"webview navigation: {url}");
        return true;
    }

    /// <summary>通过会话插件接收托盘传入的会话 ID。</summary>
    public void NavigateToSession(string sessionId)
    {
        var app = App.Instance;
        var baseUrl = app?.RuntimeUrl ?? AppSettings.DefaultWebUrl;
        // dsh-session 与功能开关都放 fragment：query 里的 token 交换后服务端 303 到干净的 /，
        // query 会被清空，而 fragment 在重定向后由浏览器保留，插件从 location.hash 读取。
        var hash = $"dsh-session={Uri.EscapeDataString(sessionId)}";
        if (app is not null)
        {
            hash += $"&{App.BuildPluginFeatureHash(app.Settings)}";
        }

        if (_webView is null)
        {
            App.Log($"webview unavailable; session navigation skipped: {sessionId}");
            return;
        }

        _webView.Source = new Uri($"{baseUrl.TrimEnd('/')}#{hash}");
        App.Log($"webview navigate to session: {sessionId}");
    }

    /// <summary>导航到无会话参数的干净首页（托盘"新建会话"）。</summary>
    public void NavigateHome()
    {
        var app = App.Instance;
        var baseUrl = app?.RuntimeUrl ?? AppSettings.DefaultWebUrl;
        var hash = app is not null ? $"#{App.BuildPluginFeatureHash(app.Settings)}" : string.Empty;
        if (_webView is null)
        {
            App.Log("webview unavailable; home navigation skipped");
            return;
        }

        _webView.Source = new Uri($"{baseUrl.TrimEnd('/')}/{hash}");
        App.Log("webview navigate home");
    }

    /// <summary>通过 fragment 通道触发插件能力（如 dshsharp-switcher=1 呼出切换器）。</summary>
    public void NavigateToPluginHash(string pluginKey)
    {
        var app = App.Instance;
        var baseUrl = app?.RuntimeUrl ?? AppSettings.DefaultWebUrl;
        var hash = pluginKey;
        if (app is not null)
        {
            hash += $"&{App.BuildPluginFeatureHash(app.Settings)}";
        }

        if (_webView is null)
        {
            App.Log($"webview unavailable; plugin hash navigation skipped: {pluginKey}");
            return;
        }

        _webView.Source = new Uri($"{baseUrl.TrimEnd('/')}#{hash}");
        App.Log($"webview plugin hash navigation: {pluginKey}");
    }

    /// <summary>显示/隐藏 Runtime 启动与故障页。</summary>
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
                ? "停止 DSH Runtime"
                : "重试启动";
    }

    /// <summary>显示/隐藏"安全模式重启"按钮（仅故障态且有第三方插件激活时由 App 控制）。</summary>
    public void SetSafeModeOffer(bool offer) => SafeModeButton.IsVisible = offer;

    // 过场渐隐中标记：避免重复调度或覆盖随后而来的重新显示。
    private bool _splashFading;

    /// <summary>
    /// 启动过场控制：显示立即生效；隐藏先渐隐 0.4s（Opacity Transition）再收起，
    /// 避免 WebUI 就绪瞬间的硬切。仅 UI 线程调用。
    /// </summary>
    public void ShowSplash(bool show)
    {
        if (show)
        {
            _splashFading = false;
            SplashPanel.Opacity = 1;
            SplashPanel.IsVisible = true;
            return;
        }

        if (!SplashPanel.IsVisible || _splashFading)
        {
            return;
        }

        _splashFading = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(450));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_splashFading)
                {
                    SplashPanel.IsVisible = false;
                    _splashFading = false;
                }
            });
        });
    }

    private void SafeModeButton_OnClick(object? sender, RoutedEventArgs e) =>
        _ = App.Instance?.RestartInSafeModeAsync();

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
