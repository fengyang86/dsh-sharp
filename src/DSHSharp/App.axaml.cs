using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using DSHSharp.Core.Configuration;
using DSHSharp.Core.Dsh;
using DSHSharp.Core.Services;
using DSHSharp.ViewModels;
using DSHSharp.Views;

namespace DSHSharp;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHSharp", "app.log");

    /// <summary>当前 App 实例（供入口/托盘/单实例回调访问）。</summary>
    public static App? Instance { get; private set; }

    /// <summary>本次启动是否来自自启动（--autostart）。</summary>
    public static bool AutoStartLaunch { get; set; }

    private readonly AppSettingsService _settingsService = new();
    private readonly IAutoStartService _autoStart = AutoStartServiceFactory.Create();
    private DshEventMonitor? _monitor;
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private bool _isExiting;

    /// <summary>当前应用设置。</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>是否正在退出（供窗口关闭逻辑区分"关闭到托盘"与"真正退出"）。</summary>
    public bool IsExiting => _isExiting;

    public override void Initialize()
    {
        Instance = this;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings = _settingsService.Load();
            // 自启动开关以系统实际状态为准（防止设置与注册表脱节）。
            Settings.AutoStartEnabled = _autoStart.IsEnabled();
            ApplyTheme(Settings.Theme);

            _mainWindow = new MainWindow(Settings);
            desktop.MainWindow = _mainWindow;

            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += OnExit;

            SetupTrayIcon();
            SetupDshMonitor();

            if (AutoStartLaunch || Settings.StartMinimized)
            {
                // 自启动/静默启动：直接驻留托盘，不打扰用户。
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>唤起主窗口（单实例第二实例通知、托盘点击、通知点击）。线程安全，可在任意线程调用。</summary>
    public void ActivateMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
        });
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _isExiting = true;
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Log("app OnExit triggered");
        // 只取消不等待：UI 线程同步等待后台任务会与窗口/WebView2 销毁形成死锁。
        _monitor?.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Log("app OnExit finished");
    }

    private void SetupTrayIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://DSHSharp/Assets/avalonia-logo.ico"));
        var icon = new WindowIcon(iconStream);

        var menu = new NativeMenu();
        var showItem = new NativeMenuItem("显示主窗口");
        showItem.Click += (_, _) => SafePost("tray:show", ActivateMainWindow);
        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => SafePost("tray:exit", RequestShutdown);
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "DSH-Sharp",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => SafePost("tray:clicked", ActivateMainWindow);
    }

    /// <summary>在 UI 线程安全执行托盘回调（托盘事件可能在非 UI 线程触发），并记录异常。</summary>
    private static void SafePost(string action, Action handler)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    Log($"{action} handler failed: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"{action} post failed: {ex}");
        }
    }

    /// <summary>追加一行应用日志（%APPDATA%/DSHSharp/app.log），用于排查桌面壳问题。</summary>
    public static void Log(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响应用。
        }
    }

    /// <summary>真正退出应用（托盘菜单"退出"）。</summary>
    private void RequestShutdown()
    {
        Log("tray:exit handler invoked");
        _isExiting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log("tray:exit -> calling desktop.Shutdown()");
            desktop.Shutdown();
            Log("tray:exit -> desktop.Shutdown() returned");
        }
        else
        {
            Log("tray:exit -> ApplicationLifetime is NOT IClassicDesktopStyleApplicationLifetime");
        }
    }

    private void SetupDshMonitor()
    {
        DshEventMonitor.Log = Log;
        _monitor = new DshEventMonitor(Settings.WebUrl);
        _monitor.SessionCompleted += OnSessionCompleted;
        _monitor.ServiceAvailabilityChanged += OnServiceAvailabilityChanged;
        _monitor.Start();
    }

    private void OnSessionCompleted(object? sender, SessionCompletedEventArgs e)
    {
        if (!Settings.SessionCompleteNotifications)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            var title = string.IsNullOrEmpty(e.Title)
                ? $"会话 {ShortId(e.SessionId)}"
                : e.Title;
            _mainWindow.ShowNotification("会话已完成", title);

            // 窗口驻留托盘时自动唤起，确保用户看到通知。
            if (!_mainWindow.IsVisible)
            {
                ActivateMainWindow();
            }
        });
    }

    private void OnServiceAvailabilityChanged(object? sender, ServiceAvailabilityEventArgs e)
    {
        Log($"service availability changed: online={e.IsOnline}");
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_mainWindow is { DataContext: MainWindowViewModel vm })
                {
                    vm.SetServiceOnline(e.IsOnline);
                }
            }
            catch (Exception ex)
            {
                Log($"service status update failed: {ex}");
            }
        });
    }

    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];

    private static void ApplyTheme(string theme)
    {
        var current = Application.Current;
        if (current is null)
        {
            return;
        }

        current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
