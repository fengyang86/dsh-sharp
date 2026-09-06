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

    /// <summary>当前应用设置。</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>是否正在退出（供窗口关闭逻辑区分"关闭到托盘"与"真正退出"）。</summary>
    public bool IsExiting => _isExiting;

    /// <summary>是否托管着本地 DSH 服务进程（客户端拉起的服务）。</summary>
    public bool IsServiceOwned => _serviceManager?.IsOwned ?? false;

    /// <summary>私有 Runtime 在本次运行中实际绑定的本机地址。</summary>
    public string RuntimeUrl => RuntimeBaseUrl;

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
            try
            {
                _settingsService.Save(Settings);
            }
            catch (Exception ex)
            {
                Log($"settings initial save failed: {ex.Message}");
            }
            // 自启动开关以系统实际状态为准（防止设置与注册表脱节）。
            Settings.AutoStartEnabled = _autoStart.IsEnabled();
            ApplyTheme(Settings.Theme);

            _mainWindow = new MainWindow(Settings);
            desktop.MainWindow = _mainWindow;

            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += OnExit;

            SetupTrayIcon();
            SetupServiceManager();

            if (AutoStartLaunch || Settings.StartMinimized)
            {
                // 自启动/静默启动：直接驻留托盘，不打扰用户。
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.Show();
            }

            // DSH-Sharp 是私有 Runtime 的唯一宿主。服务就绪后才创建 WebView 连接。
            _ = StartManagedServiceAsync();
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
        DshServiceManager.Log = Log;
        _serviceManager = new DshServiceManager(AppSettings.DefaultWebUrl, ManagedMode.Npx, null);
        _serviceManager.ProcessExitedUnexpectedly += (_, _) =>
            SafePost("service:crashed", () =>
            {
                Log("managed service exited unexpectedly");
                _serviceOnline = false;
                UpdateServiceUi();
                if (!_isExiting && !_isStarting)
                {
                    _mainWindow?.ShowNotification("DSH Runtime 已退出", "正在尝试自动恢复…");
                    _ = RecoverRuntimeAsync();
                }
            });
    }

    /// <summary>手动/自动启动 DSH-Sharp 私有 Runtime。</summary>
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
            if (ok)
            {
                _serviceOnline = true;
                ConnectRuntime();
            }
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

    private async Task RecoverRuntimeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (_isExiting || _isStarting || _serviceManager?.IsOwned == true)
        {
            return;
        }

        await StartManagedServiceAsync();
    }

    /// <summary>停止本客户端拥有的私有 Runtime。</summary>
    public void StopManagedService()
    {
        _serviceManager?.Stop();
        _serviceOnline = false;
        _monitor?.Dispose();
        _monitor = null;
        UpdateServiceUi();
    }

    private string RuntimeBaseUrl => _serviceManager?.ActiveBaseUrl ?? AppSettings.DefaultWebUrl;

    /// <summary>Runtime 成功启动后才建立监控和 WebView 导航，避免首屏连接错误。</summary>
    private void ConnectRuntime()
    {
        _monitor?.Dispose();
        SetupDshMonitor();
        _apiClient = new DshApiClient(RuntimeBaseUrl);
        StartSessionRefresh();
        _mainWindow?.ReloadWeb(BuildPluginFeatureUrl(RuntimeBaseUrl));
    }

    private string BuildPluginFeatureUrl(string baseUrl)
    {
        var separator = baseUrl.Contains('?') ? '&' : '?';
        return $"{baseUrl}{separator}dshsharp-esc-stop={(Settings.SessionPluginEscStopEnabled ? 1 : 0)}&dshsharp-copy-id={(Settings.SessionPluginCopyIdEnabled ? 1 : 0)}&dshsharp-open-workspace={(Settings.SessionPluginOpenWorkspaceEnabled ? 1 : 0)}&dshsharp-tray-navigation={(Settings.SessionPluginTrayNavigationEnabled ? 1 : 0)}";
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
                CheckDshVersionAsync,
                UpdateManagedService,
                () => _serviceManager?.ListProfilePlugins() ?? [],
                async (name, active) => _serviceManager is not null && await _serviceManager.SetPluginActiveAsync(name, active),
                async name => _serviceManager is not null && await _serviceManager.RemovePluginAsync(name)));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
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
        Settings.SessionPluginEscStopEnabled = updated.SessionPluginEscStopEnabled;
        Settings.SessionPluginCopyIdEnabled = updated.SessionPluginCopyIdEnabled;
        Settings.SessionPluginOpenWorkspaceEnabled = updated.SessionPluginOpenWorkspaceEnabled;
        Settings.SessionPluginTrayNavigationEnabled = updated.SessionPluginTrayNavigationEnabled;

        Settings = updated;
        try
        {
            _settingsService.Save(Settings);
        }
        catch (Exception ex)
        {
            Log($"settings save failed: {ex.Message}");
            _mainWindow?.ShowNotification("设置保存失败", ex.Message);
            return;
        }
        Log("settings saved");

        _mainWindow?.ShowNotification(
            "设置已保存",
            "更改已即时生效。");
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
        _traySessionsItem = new NativeMenuItem("最近会话（私有 Runtime）")
        {
            IsEnabled = false,
        };
        _traySessionsItem.Menu = new NativeMenu();
        _traySessionsItem.Menu.Items.Add(new NativeMenuItem("（正在准备私有 Runtime）") { IsEnabled = false });
        menu.Items.Add(_traySessionsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        _trayServiceItem = new NativeMenuItem("DSH Runtime：正在准备");
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
                var sessionId = session.SessionId;
                item.Click += (_, _) => SafePost("tray:session", () => NavigateToSession(sessionId));
                menu.Items.Add(item);
            }
        });
    }

    private void NavigateToSession(string sessionId)
    {
        ActivateMainWindow();
        _mainWindow?.NavigateToSession(sessionId);
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

    /// <summary>按当前 Runtime 状态刷新托盘菜单项（UI 线程调用）。</summary>
    private void RefreshTrayMenu()
    {
        if (_trayServiceItem is null || _serviceManager is null)
        {
            return;
        }

        if (_isStarting)
        {
            _trayServiceItem.Header = "DSH Runtime：正在启动…";
            _trayServiceItem.IsEnabled = false;
        }
        else if (_serviceManager.IsOwned && _serviceOnline)
        {
            _trayServiceItem.Header = $"停止 DSH Runtime（{RuntimeBaseUrl}）";
            _trayServiceItem.IsEnabled = true;
        }
        else if (_serviceManager.IsOwned)
        {
            _trayServiceItem.Header = "DSH Runtime：正在连接…";
            _trayServiceItem.IsEnabled = false;
        }
        else if (!_serviceOnline)
        {
            _trayServiceItem.Header = "启动 DSH Runtime";
            _trayServiceItem.IsEnabled = true;
        }
        else
        {
            _trayServiceItem.Header = "DSH Runtime：状态同步中";
            _trayServiceItem.IsEnabled = false;
        }

        RefreshTraySessionAvailability();
    }

    /// <summary>最近会话只属于私有 Runtime；离线后清除过期会话入口。</summary>
    private void RefreshTraySessionAvailability()
    {
        if (_traySessionsItem?.Menu is not { } menu)
        {
            return;
        }

        _traySessionsItem.IsEnabled = _serviceOnline;
        if (_serviceOnline)
        {
            return;
        }

        menu.Items.Clear();
        menu.Items.Add(new NativeMenuItem("（私有 Runtime 未运行）") { IsEnabled = false });
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
        _monitor = new DshEventMonitor(RuntimeBaseUrl);
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
                var client = new DshApiClient(RuntimeBaseUrl);
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
        vm.SetServiceState(_serviceOnline, owned, _isStarting, RuntimeBaseUrl, "Npx", "私有 Runtime");
        _settingsWindow?.UpdateServiceStatus(BuildServiceStatusText());

        // 离线时隐藏 WebView（原生表面会遮挡引导页），在线时恢复。
        _mainWindow.SetWebViewVisible(_serviceOnline);

        if (!_serviceOnline)
        {
            var detail = _isStarting ? "正在准备私有 DSH Runtime…" : _serviceManager?.LastError ?? BuildOnboardingDetail();
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
        var api = _apiClient ?? new DshApiClient(RuntimeBaseUrl);
        try
        {
            var installed = _serviceManager?.InstalledPackageVersion;
            var latest = await api.GetNpmLatestVersionAsync();
            if (latest is null)
            {
                return $"DSH-Sharp：{DshSharpCompatibility.ProductVersion}\nDSH 私有运行版本：{installed ?? "未安装"}\n支持范围：{DshSharpCompatibility.SupportedRange}\nnpm 最新版本：查询失败";
            }
            var status = !DshSharpCompatibility.IsCompatible(installed) ? "私有安装版本不兼容或无法识别，请先升级客户端" :
                !DshSharpCompatibility.IsCompatible(latest) ? "npm 最新版本超出当前客户端支持范围" :
                installed == latest ? "已是最新" : "有兼容更新";
            return $"DSH-Sharp：{DshSharpCompatibility.ProductVersion}\nDSH 私有运行版本：{installed ?? "未安装"}\nnpm 最新版本：{latest}\n支持范围：{DshSharpCompatibility.SupportedRange}\n兼容状态：{status}";
        }
        catch (Exception ex)
        {
            return $"DSH 私有运行版本：{_serviceManager?.InstalledPackageVersion ?? "未安装"}\n更新检查失败：{ex.Message}";
        }
    }

    /// <summary>更新服务：官方包模式只升级支持范围内的私有包；源码模式由开发者管理。</summary>
    public async void UpdateManagedService()
    {
        var manager = _serviceManager;
        if (manager is null)
        {
            return;
        }

        Log("updating privately managed DSH package");
        _isStarting = true;
        UpdateServiceUi();
        try
        {
            string? targetVersion;
            try
            {
                targetVersion = await (_apiClient ?? new DshApiClient(RuntimeBaseUrl)).GetNpmLatestVersionAsync();
            }
            catch (Exception ex)
            {
                _mainWindow?.ShowNotification("DSH 更新失败", $"无法查询 npm 版本：{ex.Message}");
                return;
            }

            if (!DshSharpCompatibility.IsCompatible(targetVersion))
            {
                _mainWindow?.ShowNotification("DSH 更新已阻止", $"npm 版本 {targetVersion ?? "未知"} 不在支持范围 {DshSharpCompatibility.SupportedRange} 内");
                return;
            }

            var updated = await manager.UpdatePrivatePackageAsync(targetVersion);
            if (!updated)
            {
                _mainWindow?.ShowNotification("DSH 更新失败", manager.LastError ?? "未知错误");
                return;
            }

            // 更新阶段结束后交给正常启动流程完成健康检查。
            _isStarting = false;
            await StartManagedServiceAsync();
            if (!_serviceOnline && manager.CanRollbackRuntime)
            {
                Log("updated private Runtime failed health check; rolling back");
                if (manager.RollbackRuntime())
                {
                    _mainWindow?.ShowNotification("DSH Runtime 已回滚", "新版本未通过启动检查，已恢复上一版本。");
                    await StartManagedServiceAsync();
                }
            }
            else if (_serviceOnline && manager.CanRollbackRuntime && !manager.CommitRuntimeUpdate())
            {
                Log($"Runtime update commit deferred: {manager.LastError}");
            }
        }
        catch (Exception ex)
        {
            Log($"managed Runtime update failed: {ex}");
            _mainWindow?.ShowNotification("DSH 更新失败", ex.Message);
        }
        finally
        {
            _isStarting = false;
            UpdateServiceUi();
        }
    }

    /// <summary>设置页显示的当前服务状态卡片文本（地址/模式/状态/错误）。</summary>
    private string BuildServiceStatusText()
    {
        const string modeText = "私有 Runtime（官方包）";

        var status = _serviceManager?.IsOwned ?? false
            ? "托管中（客户端已启动服务）"
            : _serviceOnline
                ? "在线（私有 Runtime）"
                : _isStarting
                    ? "正在启动…"
                    : "离线";

        var error = _serviceManager?.LastError;
        var errorLine = string.IsNullOrEmpty(error) ? "" : $"\n最近错误：{error}";
        var updateLine = _serviceManager?.NeedsRuntimeUpdate == true
            ? $"\n版本提示：当前 {_serviceManager.InstalledPackageVersion}，建议更新到 {DshSharpCompatibility.DefaultDshVersion}"
            : "";
        return $"● {RuntimeBaseUrl}\n运行时：{modeText}\n数据目录：{_serviceManager?.DshHomeDirectory ?? "未初始化"}\n状态：{status}{updateLine}{errorLine}";
    }

    private string BuildOnboardingDetail()
    {
        if (_serviceManager is null)
        {
            return "正在检测本地 DSH 服务…";
        }

        return "DSH-Sharp 将从私有目录启动官方 DSH Runtime。首次启动需要联网安装，后续直接复用固定版本。";
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
