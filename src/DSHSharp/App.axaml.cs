using System.Diagnostics;
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
using DSHSharp.Core.Compatibility;
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
    private DshServiceManager? _serviceManager;
    private DshApiClient? _apiClient;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private NativeMenu? _trayMenu;
    private NativeMenuItem? _trayServiceItem;
    private NativeMenuItem? _traySessionsItem;
    private Timer? _sessionRefreshTimer;
    private bool _isExiting;
    private bool _serviceOnline;
    private bool _isStarting;
    private bool _portScanDone;
    private DateTime _lastAutoStartAttempt = DateTime.MinValue;

    /// <summary>当前应用设置。</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>是否正在退出（供窗口关闭逻辑区分"关闭到托盘"与"真正退出"）。</summary>
    public bool IsExiting => _isExiting;

    /// <summary>是否托管着本地 DSH 服务进程（客户端拉起的服务）。</summary>
    public bool IsServiceOwned => _serviceManager?.IsOwned ?? false;

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
            // 多配置：旧版配置迁移出默认 Profile，并同步顶层字段；迁移结果落盘。
            ProfileHelper.EnsureDefaultProfile(Settings);
            ProfileHelper.ApplyActiveProfile(Settings);
            _settingsService.Save(Settings);
            // 自启动开关以系统实际状态为准（防止设置与注册表脱节）。
            Settings.AutoStartEnabled = _autoStart.IsEnabled();
            ApplyTheme(Settings.Theme);

            _mainWindow = new MainWindow(Settings);
            desktop.MainWindow = _mainWindow;

            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += OnExit;

            SetupTrayIcon();
            SetupServiceManager();
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
        // 仅终止本客户端拉起的服务进程（外部服务不受影响）。
        _serviceManager?.Dispose();
        _sessionRefreshTimer?.Dispose();
        SaveWindowState();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Log("app OnExit finished");
    }

    /// <summary>把主窗口位置/大小/最大化状态写入设置并持久化。</summary>
    private void SaveWindowState()
    {
        if (_mainWindow is null || !_mainWindow.IsVisible)
        {
            return;
        }

        Settings.WindowMaximized = _mainWindow.WindowState == WindowState.Maximized;
        if (_mainWindow.WindowState == WindowState.Normal)
        {
            Settings.WindowLeft = _mainWindow.Position.X;
            Settings.WindowTop = _mainWindow.Position.Y;
            Settings.WindowWidth = _mainWindow.Width;
            Settings.WindowHeight = _mainWindow.Height;
        }

        try
        {
            _settingsService.Save(Settings);
        }
        catch (Exception ex)
        {
            Log($"window state save failed: {ex.Message}");
        }
    }

    private void SetupServiceManager()
    {
        var mode = Enum.TryParse<ManagedMode>(Settings.ManagedMode, ignoreCase: true, out var parsed)
            ? parsed
            : ManagedMode.Npx;
        DshServiceManager.Log = Log;
        _serviceManager = new DshServiceManager(Settings.WebUrl, mode, Settings.SourcePath);
        _serviceManager.ProcessExitedUnexpectedly += (_, _) =>
            SafePost("service:crashed", () =>
            {
                Log("managed service exited unexpectedly");
                UpdateServiceUi();
            });
    }

    /// <summary>手动/自动启动托管的本地服务（在线则直接返回）。</summary>
    public async Task StartManagedServiceAsync()
    {
        if (_serviceManager is null || _isStarting)
        {
            return;
        }

        _isStarting = true;
        UpdateServiceUi();
        try
        {
            var ok = await _serviceManager.StartAsync();
            Log($"managed start: ok={ok}, error={_serviceManager.LastError ?? "none"}");
        }
        catch (Exception ex)
        {
            Log($"managed start threw: {ex}");
        }
        finally
        {
            _isStarting = false;
        }

        UpdateServiceUi();
    }

    /// <summary>停止本客户端托管的本地服务（外部服务不受影响）。</summary>
    public void StopManagedService()
    {
        _serviceManager?.Stop();
        UpdateServiceUi();
    }

    /// <summary>打开设置窗口（已打开则激活）。</summary>
    public void OpenSettingsWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(new SettingsViewModel(
                Settings,
                BuildServiceStatusText(),
                SaveSettings,
                () => _ = StartManagedServiceAsync(),
                StopManagedService,
                SwitchProfile,
                CheckDshVersionAsync,
                UpdateManagedService));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    /// <summary>
    /// 切换服务配置（Profile）：同步激活与顶层字段、持久化并即时重建连接。
    /// </summary>
    public void SwitchProfile(string name)
    {
        ProfileHelper.ActivateProfile(Settings, name);
        ProfileHelper.ApplyActiveProfile(Settings);
        Log($"switched profile: {name} -> {Settings.WebUrl}");
        _settingsService.Save(Settings);
        _mainWindow?.ShowNotification("服务配置已切换", $"{Settings.ActiveProfileName} · {Settings.WebUrl}");
        SwitchWebUrl(Settings.WebUrl);
    }

    /// <summary>
    /// 切换服务地址并即时重建连接（无需重启）：更新设置、重建事件监控与服务托管、
    /// 重载 WebView。
    /// </summary>
    public void SwitchWebUrl(string url)
    {
        Log($"switching web url: {Settings.WebUrl} -> {url}");
        Settings.WebUrl = url;
        _settingsService.Save(Settings);

        _monitor?.Dispose();
        _monitor = null;
        _serviceManager?.Dispose();
        _serviceManager = null;
        _portScanDone = false;

        SetupServiceManager();
        SetupDshMonitor();
        _mainWindow?.ReloadWeb(url);
        UpdateServiceUi();
    }

    /// <summary>保存设置：即时应用可生效项，持久化，提示重启生效项。</summary>
    public void SaveSettings(AppSettings updated)
    {
        if (updated.AutoStartEnabled != Settings.AutoStartEnabled)
        {
            try
            {
                _autoStart.SetEnabled(updated.AutoStartEnabled);
            }
            catch (Exception ex)
            {
                Log($"autostart set failed: {ex.Message}");
            }
        }

        if (updated.Theme != Settings.Theme)
        {
            ApplyTheme(updated.Theme);
        }

        var connectionChanged = updated.WebUrl != Settings.WebUrl
            || !string.Equals(updated.ManagedMode, Settings.ManagedMode, StringComparison.OrdinalIgnoreCase)
            || updated.SourcePath != Settings.SourcePath;

        Settings = updated;
        _settingsService.Save(Settings);
        Log($"settings saved (connectionChanged={connectionChanged})");

        _mainWindow?.ShowNotification(
            "设置已保存",
            connectionChanged ? "服务地址/托管模式将在下次启动时生效。" : "更改已即时生效。");
    }

    private void SetupTrayIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://DSHSharp/Assets/avalonia-logo.ico"));
        var icon = new WindowIcon(iconStream);

        var menu = new NativeMenu();
        var showItem = new NativeMenuItem("显示主窗口");
        showItem.Click += (_, _) => SafePost("tray:show", ActivateMainWindow);
        menu.Items.Add(showItem);
        var settingsItem = new NativeMenuItem("设置…");
        settingsItem.Click += (_, _) => SafePost("tray:settings", OpenSettingsWindow);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        _traySessionsItem = new NativeMenuItem("最近会话");
        _traySessionsItem.Menu = new NativeMenu();
        menu.Items.Add(_traySessionsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        _trayServiceItem = new NativeMenuItem("本地服务");
        _trayServiceItem.Click += (_, _) => SafePost("tray:service", OnTrayServiceClick);
        menu.Items.Add(_trayServiceItem);
        var aboutItem = new NativeMenuItem("关于 DSH-Sharp");
        aboutItem.Click += (_, _) => SafePost("tray:about", ShowAbout);
        menu.Items.Add(aboutItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => SafePost("tray:exit", RequestShutdown);
        menu.Items.Add(exitItem);
        _trayMenu = menu;

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "DSH-Sharp",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => SafePost("tray:clicked", ActivateMainWindow);
        RefreshTrayMenu();
        StartSessionRefresh();
    }

    /// <summary>启动会话列表定时刷新（60s 间隔，首次 5s 后）。</summary>
    private void StartSessionRefresh()
    {
        _apiClient = new DshApiClient(Settings.WebUrl);
        _sessionRefreshTimer?.Dispose();
        _sessionRefreshTimer = new Timer(
            _ => _ = RefreshSessionsAsync(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(60));
    }

    /// <summary>拉取会话列表并更新托盘"最近会话"子菜单。</summary>
    private async Task RefreshSessionsAsync()
    {
        var client = _apiClient;
        if (client is null || !_serviceOnline)
        {
            return;
        }

        IReadOnlyList<DshSessionSummary> sessions;
        try
        {
            sessions = await client.ListSessionsAsync();
        }
        catch (Exception ex)
        {
            Log($"session list refresh failed: {ex.Message}");
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var menu = _traySessionsItem?.Menu;
            if (menu is null)
            {
                return;
            }

            menu.Items.Clear();
            if (sessions.Count == 0)
            {
                menu.Items.Add(new NativeMenuItem("（暂无会话）") { IsEnabled = false });
                return;
            }

            foreach (var session in sessions.Take(8))
            {
                var title = string.IsNullOrEmpty(session.Title)
                    ? ShortId(session.SessionId)
                    : session.Title;
                if (title.Length > 24)
                {
                    title = title[..24] + "…";
                }

                var item = new NativeMenuItem((session.Running ? "● " : "") + title);
                item.Click += (_, _) => SafePost("tray:session", ActivateMainWindow);
                menu.Items.Add(item);
            }
        });
    }

    /// <summary>托盘"关于"：弹出版本信息 Toast。</summary>
    private void ShowAbout()
    {
        _mainWindow?.ShowNotification(
            "DSH-Sharp",
            $"版本 {new MainWindowViewModel(Settings).Version}\nDeepSeek Harness 桌面客户端（.NET 10 + Avalonia）");
    }

    private void OnTrayServiceClick()
    {
        if (_serviceManager is null)
        {
            return;
        }

        if (_serviceManager.IsOwned)
        {
            StopManagedService();
        }
        else if (!_serviceOnline)
        {
            _ = StartManagedServiceAsync();
        }
    }

    /// <summary>按当前服务状态刷新托盘"本地服务"菜单项（UI 线程调用）。</summary>
    private void RefreshTrayMenu()
    {
        if (_trayServiceItem is null || _serviceManager is null)
        {
            return;
        }

        if (_serviceManager.IsOwned)
        {
            _trayServiceItem.Header = "停止本地服务";
            _trayServiceItem.IsEnabled = true;
        }
        else if (!_serviceOnline && _serviceManager.Mode != ManagedMode.None)
        {
            _trayServiceItem.Header = "启动本地服务";
            _trayServiceItem.IsEnabled = true;
        }
        else
        {
            _trayServiceItem.Header = _serviceOnline ? "本地服务：外部运行" : "本地服务：未托管";
            _trayServiceItem.IsEnabled = false;
        }
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
        Log($"session completed event: session={e.SessionId}, title={e.Title}, notificationsEnabled={Settings.SessionCompleteNotifications}");
        if (!Settings.SessionCompleteNotifications)
        {
            return;
        }

        // 后台线程异步获取会话标题与最后回复开头，再回 UI 线程弹 Toast。
        _ = Task.Run(async () =>
        {
            string title;
            string? preview = null;
            try
            {
                var client = new DshApiClient(Settings.WebUrl);
                title = string.IsNullOrEmpty(e.Title) ? ShortId(e.SessionId) : e.Title;
                preview = await client.GetLastAssistantTextAsync(e.SessionId);
            }
            catch (Exception ex)
            {
                Log($"session completion preview failed: {ex.Message}");
                title = string.IsNullOrEmpty(e.Title) ? ShortId(e.SessionId) : e.Title;
            }

            var finalTitle = title;
            var finalPreview = preview;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_mainWindow is null)
                    {
                        Log("session completed: main window is null, skip toast");
                        return;
                    }

                    Log($"session completed: showing toast '{finalTitle}', preview='{finalPreview}'");

                    // 通知音效（可配置开关）。
                    if (Settings.NotificationSoundEnabled)
                    {
                        Services.NotificationSound.Play();
                    }

                    _mainWindow.ShowNotification("会话已完成", finalTitle, finalPreview);

                    // 窗口驻留托盘时自动唤起，确保用户看到通知。
                    if (!_mainWindow.IsVisible)
                    {
                        ActivateMainWindow();
                    }
                }
                catch (Exception ex)
                {
                    Log($"session completed toast failed: {ex}");
                }
            });
        });
    }

    private void OnServiceAvailabilityChanged(object? sender, ServiceAvailabilityEventArgs e)
    {
        _serviceOnline = e.IsOnline;
        Log($"service availability changed: online={e.IsOnline}");
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UpdateServiceUi();
                // 离线处理：先扫描常见端口纠错，未发现服务才按托管模式自动启动。
                if (!e.IsOnline && !_portScanDone)
                {
                    _portScanDone = true;
                    _ = HandleOfflineAsync();
                }
            }
            catch (Exception ex)
            {
                Log($"service status update failed: {ex}");
            }
        });
    }

    /// <summary>把服务状态推送到 UI（状态栏、引导页、托盘菜单）。UI 线程调用。</summary>
    private void UpdateServiceUi()
    {
        if (_mainWindow is not { DataContext: MainWindowViewModel vm })
        {
            return;
        }

        var owned = _serviceManager?.IsOwned ?? false;
        vm.SetServiceState(_serviceOnline, owned, _isStarting, Settings.WebUrl, Settings.ManagedMode, Settings.ActiveProfileName);

        // 离线时隐藏 WebView（原生表面会遮挡引导页），在线时恢复。
        _mainWindow.SetWebViewVisible(_serviceOnline);

        if (!_serviceOnline)
        {
            var detail = _serviceManager?.LastError ?? BuildOnboardingDetail();
            _mainWindow.ShowOnboarding(true, detail, _isStarting || owned);
        }
        else
        {
            _mainWindow.ShowOnboarding(false, null, false);
        }

        RefreshTrayMenu();
    }

    /// <summary>
    /// 检查 DSH-Sharp 与 DSH 版本，并报告当前托管模式下的兼容状态。
    /// 返回可直接展示的文本。
    /// </summary>
    public async Task<string> CheckDshVersionAsync()
    {
        var api = _apiClient ?? new DshApiClient(Settings.WebUrl);
        string? current;
        try
        {
            current = await api.GetVersionAsync();
        }
        catch (Exception ex)
        {
            return $"版本检查失败：{ex.Message}";
        }

        current ??= "未知";
        try
        {
            var installed = _serviceManager?.InstalledPackageVersion;
            if (string.Equals(Settings.ManagedMode, "Source", StringComparison.OrdinalIgnoreCase))
            {
                return $"DSH-Sharp：{DshSharpCompatibility.ProductVersion}\nDSH 运行版本：{current}\n支持范围：{DshSharpCompatibility.SupportedRange}\n源码模式：版本由开发者管理";
            }

            var latest = await api.GetNpmLatestVersionAsync();
            if (latest is null)
            {
                return $"DSH-Sharp：{DshSharpCompatibility.ProductVersion}\nDSH 运行版本：{current}\n私有安装版本：{installed ?? "未安装"}\n支持范围：{DshSharpCompatibility.SupportedRange}\nnpm 最新版本：查询失败";
            }
            var status = !DshSharpCompatibility.IsCompatible(current) ? "当前运行版本不兼容，请先升级客户端" :
                !DshSharpCompatibility.IsCompatible(latest) ? "npm 最新版本超出当前客户端支持范围" :
                current == latest ? "已是最新" : "有兼容更新";
            return $"DSH-Sharp：{DshSharpCompatibility.ProductVersion}\nDSH 运行版本：{current}\n私有安装版本：{installed ?? "未安装"}\nnpm 最新版本：{latest}\n支持范围：{DshSharpCompatibility.SupportedRange}\n兼容状态：{status}";
        }
        catch (Exception ex)
        {
            return $"运行版本：{current}（更新检查失败：{ex.Message}）";
        }
    }

    /// <summary>更新服务：官方包模式只升级支持范围内的私有包；源码模式由开发者管理。</summary>
    public async void UpdateManagedService()
    {
        if (string.Equals(Settings.ManagedMode, "Source", StringComparison.OrdinalIgnoreCase))
        {
            _mainWindow?.ShowNotification(
                "源码模式更新",
                "DSH-Sharp 不管理源码、依赖和构建；请由开发者完成更新后重启服务。");
            return;
        }

        var manager = _serviceManager;
        if (manager is null)
        {
            return;
        }

        Log("updating privately managed DSH package");
        _isStarting = true;
        UpdateServiceUi();
        string? targetVersion = null;
        try { targetVersion = await (_apiClient ?? new DshApiClient(Settings.WebUrl)).GetNpmLatestVersionAsync(); }
        catch { }
        if (!DshSharpCompatibility.IsCompatible(targetVersion))
        {
            _isStarting = false;
            _mainWindow?.ShowNotification("DSH 更新已阻止", $"npm 版本 {targetVersion ?? "未知"} 不在支持范围 {DshSharpCompatibility.SupportedRange} 内");
            UpdateServiceUi();
            return;
        }
        var updated = await manager.UpdatePrivatePackageAsync(targetVersion);
        _isStarting = false;
        if (!updated)
        {
            _mainWindow?.ShowNotification("DSH 更新失败", manager.LastError ?? "未知错误");
            UpdateServiceUi();
            return;
        }

        _portScanDone = false;
        await StartManagedServiceAsync();
    }

    /// <summary>设置页显示的当前服务状态卡片文本（地址/模式/状态/错误）。</summary>
    private string BuildServiceStatusText()
    {
        var modeText = Settings.ManagedMode switch
        {
            "None" => "不托管（仅探测）",
            "Source" => "源码托管（pnpm/node）",
            _ => "私有目录托管（官方包）",
        };

        var status = _serviceManager?.IsOwned ?? false
            ? "托管中（客户端已启动服务）"
            : _serviceOnline
                ? "在线（外部运行）"
                : _isStarting
                    ? "正在启动…"
                    : "离线";

        var error = _serviceManager?.LastError;
        var errorLine = string.IsNullOrEmpty(error) ? "" : $"\n最近错误：{error}";
        return $"● {Settings.WebUrl}\n托管模式：{modeText}\n状态：{status}{errorLine}";
    }

    private string BuildOnboardingDetail()
    {
        if (_serviceManager is null)
        {
            return "正在检测本地 DSH 服务…";
        }

        return _serviceManager.Mode switch
        {
            ManagedMode.None => "当前为纯探测模式（未托管）。请手动启动 DSH 服务，或在设置中开启托管。",
            ManagedMode.Source => "将使用配置的源码路径启动 DSH 服务（pnpm dsh web --no-open）。请确认已执行 pnpm install && pnpm run build。",
            _ => "将从 DSH-Sharp 私有目录启动官方 DSH 包。首次启动需要联网安装，后续直接复用固定版本。",
        };
    }

    /// <summary>
    /// 离线统一处理：先扫描常见端口——
    /// 发现其他端口有 DSH 服务则提示切换（此时不启动托管，避免双服务并行）；
    /// 未发现任何服务才按托管模式自动启动（防抖：失败后 60s 冷却）。
    /// </summary>
    private async Task HandleOfflineAsync()
    {
        var manager = _serviceManager;
        if (manager is null || _serviceOnline)
        {
            return;
        }

        // 1. 端口纠错扫描。
        Log("offline: scanning common ports");
        int? found;
        try
        {
            found = await manager.ScanCommonPortsAsync();
        }
        catch (Exception ex)
        {
            Log($"port scan failed: {ex.Message}");
            found = null;
        }

        Log($"port scan result: {found?.ToString() ?? "none"}");
        if (found is not null && !_serviceOnline)
        {
            // 2. 发现其他端口的服务：提示切换，跳过托管启动。
            var port = found.Value;
            Dispatcher.UIThread.Post(() =>
            {
                _mainWindow?.ShowPortHint(port, p => SwitchWebUrl($"http://127.0.0.1:{p}"));
            });
            return;
        }

        // 3. 未发现：按托管模式自动启动。
        if (manager.Mode != ManagedMode.None
            && !manager.IsOwned
            && !_isStarting
            && DateTime.UtcNow - _lastAutoStartAttempt > TimeSpan.FromSeconds(60))
        {
            _lastAutoStartAttempt = DateTime.UtcNow;
            _ = StartManagedServiceAsync();
        }
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

        var variant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        current.RequestedThemeVariant = variant;
        Log($"theme applied: {theme} -> {variant}");
    }
}
