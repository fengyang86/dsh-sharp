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

    /// <summary>应用数据目录（%APPDATA%\DSHSharp）。</summary>
    public static string AppDataDirectory => Path.GetDirectoryName(LogPath)!;

    /// <summary>当前 App 实例（供入口/托盘/单实例回调访问）。</summary>
    public static App? Instance { get; private set; }

    /// <summary>启动参数携带的深链目标会话（dshsharp://session/&lt;id&gt;；服务就绪后导航一次）。</summary>
    public static string? PendingDeepLinkSession { get; set; }

    /// <summary>本次启动是否来自自启动（--autostart）。</summary>
    public static bool AutoStartLaunch { get; set; }

    /// <summary>冷启动计时（Program.Main 起表）：框架初始化/服务就绪耗时写入 app.log，
    /// 为 R2R/NativeAOT 的启动收益提供实测基线。</summary>
    public static System.Diagnostics.Stopwatch? StartupStopwatch { get; set; }

    private readonly AppSettingsService _settingsService = new();
    private readonly IAutoStartService _autoStart = AutoStartServiceFactory.Create();
    private readonly DSHSharp.Core.Services.WebView2RuntimeService _webView2Service = new();
    private DshEventMonitor? _monitor;
    private DshServiceManager? _serviceManager;
    private DshApiClient? _apiClient;
    private DshAuthSession? _authSession;
    private ClientUpdateService? _clientUpdate;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private Services.NotifyIconService? _notifyIcon;
    private Services.GlobalHotkeyService? _hotkeys;

    /// <summary>主窗口基础标题（与 MainWindow.axaml 的 Title 一致），运行计数作为后缀追加。</summary>
    private const string BaseWindowTitle = "DSH-Sharp";
    private Timer? _sessionRefreshTimer;
    private int _sessionRefreshFailures;
    // 会话刷新在途标记（Interlocked）：并发触发只放行一次 RPC。
    private int _refreshInFlight;
    // 客户端更新轮询：单次触发定时器，每次完成后按结果重排（失败退避）。
    private Timer? _clientUpdateTimer;
    private TimeSpan _clientUpdateBackoff = TimeSpan.Zero;
    // 冷启动基线：服务首次就绪的耗时只记一次（后续重启不重复统计）。
    private bool _serviceOnlineLogged;
    // 托盘快捷面板数据：最近一次会话列表刷新的快照（UI 线程更新）。
    private IReadOnlyList<DshSessionSummary> _lastSessions = [];
    // 托盘左键快捷面板实例（单例复用；失焦自关）。
    private Views.TrayFlyoutWindow? _trayFlyout;
    private bool _isExiting;
    private bool _serviceOnline;
    private bool _isStarting;
    // WebView2 运行时是否可用；预检确认缺失/安装中为 false（乐观初值，启动后立即校正）。
    private volatile bool _webView2Ready = true;

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
            StartSystemThemeWatcher();

            _mainWindow = new MainWindow(Settings);
            desktop.MainWindow = _mainWindow;

            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += OnExit;

            SetupTrayIcon();
            SetupServiceManager();
            SetupGlobalHotkeys();
            // WebView2 安装进度 → 引导页（与 Node 下载进度共用同一面板）。
            _webView2Service.StateChanged += (_, state) =>
                SafePost("webview2:state", () => OnWebView2StateChanged(state));

            if (AutoStartLaunch || Settings.StartMinimized)
            {
                // 自启动/静默启动：直接驻留托盘，不打扰用户。
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.Show();
            }

            // WebView 预热：运行时存在时提前创建隐藏控件，WebView2 初始化与 DSH Runtime 启动并行，
            // 服务就绪后的首次导航不再串行等待控件创建。
            _mainWindow.TryPrepareWebView();

            // DSH-Sharp 是私有 Runtime 的唯一宿主。服务就绪后才创建 WebView 连接。
            _ = StartManagedServiceAsync();

            // WebView2 运行时预检：缺失时后台静默安装（与 Node 预检同为自动修复，不阻塞服务启动）。
            _ = EnsureWebView2RuntimeAsync();

            // 客户端更新：启动即检查并后台预下载（失败静默，可到设置页手动重试）。
            _clientUpdate = new ClientUpdateService();
            ClientUpdateService.Log = Log;
            _clientUpdate.CleanupStaging();
            if (Settings.ClientUpdateCheckEnabled)
            {
                _ = Task.Run(() => _clientUpdate.CheckAsync());
            }

            // 周期轮询（借鉴官方桌面端）：10 分钟基础间隔 ±20% 抖动，失败指数退避至 1 小时。
            StartClientUpdatePolling();

            Log($"startup: framework initialized in {StartupStopwatch?.ElapsedMilliseconds ?? -1}ms");
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>下载就绪的客户端更新：拉起 staging 新 EXE 自安装并退出当前实例。
    /// 更新会重启客户端及其托管的 Runtime：运行中会话将被打断，需用户确认。</summary>
    public async void InstallClientUpdate()
    {
        var staging = _clientUpdate?.State.StagingDirectory;
        if (staging is null || !File.Exists(Path.Combine(staging, "DSHSharp.exe")))
        {
            Log("client update install skipped: staging not ready");
            return;
        }

        if (!await ConfirmInterruptionAsync("升级并重启客户端"))
        {
            return;
        }

        var installDir = AppContext.BaseDirectory;
        Log($"client update: launching installer from {staging}");
        try
        {
            using var installer = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(staging, "DSHSharp.exe"),
                Arguments = $"--apply-update \"{installDir}\" {Environment.ProcessId}",
                UseShellExecute = true,
            });
            if (installer is null)
            {
                Log("client update installer failed to start");
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"client update installer start failed: {ex.Message}");
            return;
        }

        RequestShutdown();
    }

    /// <summary>
    /// 更新/重启前的排水确认（借鉴官方桌面端）：有运行中 agent 会话时弹确认框，
    /// 用户批准才继续中断；没有运行中会话直接放行。
    /// </summary>
    private async Task<bool> ConfirmInterruptionAsync(string actionText)
    {
        var running = await CountRunningSessionsAsync();
        if (running == 0)
        {
            return true;
        }

        Avalonia.Controls.Window? owner = _settingsWindow is { IsVisible: true } ? _settingsWindow : _mainWindow;
        if (owner is null)
        {
            return true; // 无可交互窗口（理论不可达）：不阻塞。
        }

        var confirmed = await Views.ConfirmWindow.AskAsync(
            owner,
            actionText,
            $"有 {running} 个会话正在运行，{actionText}会中断这些会话。\n确定要继续吗？",
            actionText);
        Log($"interruption confirm ({actionText}): running={running}, confirmed={confirmed}");
        return confirmed;
    }

    /// <summary>统计运行中会话数：优先事件流实时状态，不可用时回退一次 session/list 查询。</summary>
    private async Task<int> CountRunningSessionsAsync()
    {
        if (_monitor is { } monitor)
        {
            return monitor.RunningSnapshot().Count;
        }

        if (_apiClient is { } client && _serviceOnline)
        {
            try
            {
                var sessions = await client.ListSessionsAsync();
                return sessions.Count(s => s.Running);
            }
            catch (Exception ex)
            {
                Log($"running session count query failed: {ex.Message}");
            }
        }

        return 0;
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
        _hotkeys?.Dispose();
        _hotkeys = null;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
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
        NodeRuntimeService.Log = Log;
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
        // 内置 Node 下载/校验/解压进度：下载发生在 StartAsync 内部（引导页已处于 busy 态），直接覆盖更新详情。
        _serviceManager.NodeRuntime.StateChanged += (_, state) =>
            SafePost("node:state", () =>
            {
                if (_serviceOnline)
                {
                    return;
                }

                var detail = state.Phase switch
                {
                    "Downloading" => $"正在下载内置 Node（约 30MB）… {state.DownloadPercent}%",
                    "Verifying" => "正在校验内置 Node 安装包…",
                    "Extracting" => "正在解压内置 Node…",
                    _ => null,
                };
                if (detail is not null)
                {
                    _mainWindow?.ShowOnboarding(true, detail, true);
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
            // WebView2 修复失败后的重试入口：重试服务的同时重试运行时安装。
            if (!_webView2Ready)
            {
                _ = EnsureWebView2RuntimeAsync();
            }

            var ok = await _serviceManager.StartAsync();
            Log($"managed start: ok={ok}, error={_serviceManager.LastError ?? "none"}");
            if (ok)
            {
                if (!_serviceOnlineLogged)
                {
                    _serviceOnlineLogged = true;
                    Log($"startup: service online in {StartupStopwatch?.ElapsedMilliseconds ?? -1}ms");
                }

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

    /// <summary>安全模式重启（故障页按钮触发）：停服并停用第三方插件激活后重启 Runtime。</summary>
    public async Task RestartInSafeModeAsync()
    {
        var manager = _serviceManager;
        if (manager is null || _isStarting)
        {
            return;
        }

        _isStarting = true;
        UpdateServiceUi();
        try
        {
            var ok = await manager.RestartInSafeModeAsync();
            Log($"safe mode restart: ok={ok}, error={manager.LastError ?? "none"}");
            if (ok)
            {
                _serviceOnline = true;
                ConnectRuntime();
            }
        }
        catch (Exception ex)
        {
            Log($"safe mode restart threw: {ex}");
        }
        finally
        {
            _isStarting = false;
        }

        UpdateServiceUi();
    }

    /// <summary>WebView2 运行时预检与自动安装（后台执行，不阻塞 DSH Runtime 启动）。</summary>
    private async Task EnsureWebView2RuntimeAsync()
    {
        DSHSharp.Core.Services.WebView2RuntimeService.Log = Log;
        if (DSHSharp.Core.Services.WebView2RuntimeService.IsInstalled)
        {
            _webView2Ready = true;
            return;
        }

        _webView2Ready = false;
        Log("webview2 runtime missing; auto-installing");
        try
        {
            _webView2Ready = await _webView2Service.EnsureAsync();
        }
        catch (Exception ex)
        {
            Log($"webview2 ensure failed: {ex.Message}");
            _webView2Ready = false;
        }

        SafePost("webview2:done", () =>
        {
            // 安装晚于服务就绪时（典型时序），这里补上本该由 ConnectRuntime 做的首导航。
            if (_webView2Ready && _serviceOnline)
            {
                _mainWindow?.ReloadWeb(BuildPluginFeatureUrl(RuntimeBaseUrl));
            }

            UpdateServiceUi();
        });
    }

    /// <summary>WebView2 安装进度 → 引导页详情（Node 下载进行中时让位，避免两条进度互相覆盖）。</summary>
    private void OnWebView2StateChanged(DSHSharp.Core.Services.WebView2RuntimeState state)
    {
        if (_webView2Ready)
        {
            return;
        }

        if (_serviceManager?.NodeRuntime.State.Phase is "Downloading" or "Verifying" or "Extracting")
        {
            return;
        }

        var detail = state.Phase switch
        {
            "Downloading" => $"正在下载 WebView2 安装器… {state.DownloadPercent}%",
            "Verifying" => "正在校验 WebView2 安装器签名…",
            "Installing" => "正在安装 WebView2 运行时（微软官方源，可能需要几分钟）…",
            _ => null,
        };
        if (detail is not null)
        {
            _mainWindow?.ShowOnboarding(true, detail, busy: true);
        }
    }

    /// <summary>引导页上展示的 WebView2 状态详情（安装中/失败两种形态）。</summary>
    private string BuildWebView2Detail()
    {
        var state = _webView2Service.State;
        return state.Phase == "Failed"
            ? $"WebView2 运行时缺失，自动安装失败：{state.Error}\n可点击\"重试启动\"再次尝试；多次失败请从微软官网安装 Evergreen 版后重启客户端。"
            : "WebView2 运行时缺失，正在自动安装（微软官方源，约 100MB）…\n安装完成后会自动进入 DSH WebUI。";
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
        // HTTP RPC、事件流与 WebView 共享同一认证会话：token 换 cookie 只做一次。
        var auth = new DshAuthSession(RuntimeBaseUrl);
        _authSession = auth;
        _ = Task.Run(async () =>
        {
            if (!await auth.EnsureAuthenticatedAsync())
            {
                Log($"runtime auth cookie exchange failed: {auth.Origin}");
            }
        });
        SetupDshMonitor(auth);
        _apiClient = new DshApiClient(auth);
        StartSessionRefresh();
        _mainWindow?.ReloadWeb(BuildPluginFeatureUrl(RuntimeBaseUrl));
    }

    private string BuildPluginFeatureUrl(string baseUrl)
    {
        // token 由 query 携带且只用于根路径交换；功能开关放 fragment（303 重定向后仍保留），
        // 由 dsh-sharp-session 插件从 location.hash 读取。
        if (baseUrl.Contains('#'))
        {
            return baseUrl;
        }

        var hash = BuildPluginFeatureHash(Settings);
        // 显式主题随首导航下发给插件（theme.setTheme 落 WebUI 自己的设置）；
        // System 档不下发——该档位以 WebUI 为权威，方向反过来（主题桥镜像回壳）。
        if (Settings.Theme is "Light" or "Dark")
        {
            hash += $"&dshsharp-theme={Settings.Theme.ToLowerInvariant()}";
        }

        return $"{baseUrl}#{hash}";
    }

    /// <summary>把壳端主题选择推送给 WebUI（插件 hash 通道 → 官方 theme.setTheme → WebUI 持久化）。
    /// 供设置页切换时即时联动；WebUI 变化后主题桥会回报，双向闭环。</summary>
    private void PushThemeToWebUi(string theme)
    {
        if (theme is not ("Light" or "Dark"))
        {
            return; // System：WebUI 自己说了算。
        }

        _mainWindow?.NavigateToPluginHash($"dshsharp-theme={theme.ToLowerInvariant()}");
        Log($"theme pushed to webui: {theme}");
    }

    /// <summary>构造内置插件功能开关的 fragment 参数（不含 #）。</summary>
    internal static string BuildPluginFeatureHash(AppSettings settings)
    {
        return $"dshsharp-esc-stop={(settings.SessionPluginEscStopEnabled ? 1 : 0)}" +
               $"&dshsharp-copy-id={(settings.SessionPluginCopyIdEnabled ? 1 : 0)}" +
               $"&dshsharp-open-workspace={(settings.SessionPluginOpenWorkspaceEnabled ? 1 : 0)}" +
               $"&dshsharp-tray-navigation={(settings.SessionPluginTrayNavigationEnabled ? 1 : 0)}";
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
                async name => _serviceManager is not null && await _serviceManager.RemovePluginAsync(name),
                _clientUpdate,
                InstallClientUpdate));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    /// <summary>保存设置：即时应用可生效项，持久化，提示重启生效项。</summary>
    public void SaveSettings(AppSettings updated)
    {
        try
        {
            if (_autoStart.IsEnabled() != updated.AutoStartEnabled)
            {
                _autoStart.SetEnabled(updated.AutoStartEnabled);
            }
        }
        catch (Exception ex)
        {
            Log($"autostart set failed: {ex.Message}");
        }

        if (updated.Theme != _lastAppliedTheme)
        {
            ApplyTheme(updated.Theme);
            PushThemeToWebUi(updated.Theme);
        }
        Settings.SessionPluginEscStopEnabled = updated.SessionPluginEscStopEnabled;
        Settings.SessionPluginCopyIdEnabled = updated.SessionPluginCopyIdEnabled;
        Settings.SessionPluginOpenWorkspaceEnabled = updated.SessionPluginOpenWorkspaceEnabled;
        Settings.SessionPluginTrayNavigationEnabled = updated.SessionPluginTrayNavigationEnabled;
        Settings.ClientUpdateCheckEnabled = updated.ClientUpdateCheckEnabled;

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
            "设置已应用",
            "更改已保存并即时生效。");
    }

    /// <summary>OS 级全局热键：Ctrl+Alt+D 唤起主窗口；Ctrl+Alt+K 唤起并呼出会话切换器。</summary>
    private void SetupGlobalHotkeys()
    {
        _hotkeys = new Services.GlobalHotkeyService(id =>
            SafePost($"hotkey:{id}", () => HandleGlobalHotkey(id)));
        if (!_hotkeys.TryInstall(out var error) && error is not null)
        {
            Log($"global hotkeys unavailable: {error}");
        }
    }

    private void HandleGlobalHotkey(int id)
    {
        if (id == Services.GlobalHotkeyService.HotkeyOpenSwitcher)
        {
            ActivateMainWindow();
            _mainWindow?.NavigateToPluginHash("dshsharp-switcher=1");
            return;
        }

        ActivateMainWindow();
    }

    private void SetupTrayIcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            StartSessionRefresh();
            return;
        }

        using var iconStream = AssetLoader.Open(new Uri("avares://DSHSharp/Assets/avalonia-logo.ico"));
        var iconHandle = GetIconHandle(iconStream);

        _notifyIcon = new Services.NotifyIconService("DSH-Sharp");
        // Windows 托盘惯例：左键单击 = 主窗口；双击 = 主窗口；右键 = 菜单/面板（唯一富交互入口）。
        _notifyIcon.Clicked += args =>
        {
            Log($"tray left-click at {args.CursorX},{args.CursorY} -> main window");
            if (_trayFlyout is { IsVisible: true })
            {
                _trayFlyout.Close();
            }

            ActivateMainWindow();
        };
        _notifyIcon.DoubleClicked += args =>
        {
            Log($"tray double-click at {args.CursorX},{args.CursorY}");
            if (_trayFlyout is { IsVisible: true })
            {
                _trayFlyout.Close();
            }

            ActivateMainWindow();
        };
        _notifyIcon.RightClicked += args =>
        {
            Log($"tray right-click (icon anchor {args.CursorX},{args.CursorY})");
            ShowTrayFlyoutAt(args.CursorX, args.CursorY);
        };
        if (!_notifyIcon.TryInstall(iconHandle))
        {
            Log($"notify icon install failed (hwnd created={_notifyIcon.HasMessageWindow}, iconHandle={iconHandle != IntPtr.Zero})");
        }
        else
        {
            Log("notify icon installed");
            UpdateTrayTip();
        }

        StartSessionRefresh();
    }

    /// <summary>从 ico 资源流提取 Win32 HICON：落临时 .ico 文件 → PrivateExtractIconsW → 删临时文件。</summary>
    private static IntPtr GetIconHandle(Stream iconStream)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"dshsharp-{Guid.NewGuid():N}.ico");
            try
            {
                using (var file = File.Create(tempPath))
                {
                    iconStream.CopyTo(file);
                }

                var handles = new IntPtr[1];
                var extracted = PrivateExtractIconsW(tempPath, 0, 48, 48, handles, null, 1, 0);
                return extracted > 0 ? handles[0] : IntPtr.Zero;
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(
        string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[]? phicon, int[]? piconid, int nIcons, uint flags);

    /// <summary>托盘悬停提示（自研图标直接设置，127 字符上限内部截断）。</summary>
    private void UpdateTrayTip()
    {
        var running = _lastSessions.Count(session => session.Running);
        var runtime = _serviceOnline ? $" · {RuntimeBaseUrl}" : " · Runtime 未运行";
        _notifyIcon?.UpdateTip($"DSH-Sharp v{DshSharpCompatibility.ProductVersion}" +
            (running > 0 ? $" · {running} 个会话运行中" : "") + runtime);
    }

    /// <summary>启动会话列表自调度刷新（成功 60s；连续失败指数退避至 10 分钟，避免 401 刷屏日志）。</summary>
    private void StartSessionRefresh()
    {
        _sessionRefreshTimer?.Dispose();
        _sessionRefreshFailures = 0;
        _sessionRefreshTimer = new Timer(
            _ => _ = RefreshSessionsAsync(),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);
    }

    private void ScheduleSessionRefresh()
    {
        var delay = _sessionRefreshFailures == 0
            ? TimeSpan.FromSeconds(60)
            : TimeSpan.FromSeconds(Math.Min(60 * Math.Pow(2, _sessionRefreshFailures), 600));
        try
        {
            _sessionRefreshTimer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 退出路径：忽略。
        }
    }

    /// <summary>事件驱动的即时刷新：滑动 2 秒合并窗口——多会话密集启停时收敛为一次 RPC。</summary>
    private void ScheduleSessionRefreshSoon()
    {
        try
        {
            _sessionRefreshTimer?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 退出路径：忽略。
        }
    }

    /// <summary>启动客户端更新周期轮询（单次触发定时器，自调度重排）。</summary>
    private void StartClientUpdatePolling()
    {
        _clientUpdateTimer = new Timer(
            _ => _ = PollClientUpdateAsync(),
            null,
            TimeSpan.FromMinutes(10), // 启动时已检查过一次，首个轮询从一个完整间隔开始。
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>一次轮询：更新就绪（等安装）或开关关闭时跳过检查只重排；失败进入指数退避。</summary>
    private async Task PollClientUpdateAsync()
    {
        try
        {
            var skipCheck = _clientUpdate?.IsReady == true || !Settings.ClientUpdateCheckEnabled;
            if (!skipCheck && _clientUpdate is not null)
            {
                await _clientUpdate.CheckAsync();
            }

            _clientUpdateBackoff = _clientUpdate?.State.Phase == "CheckFailed"
                ? TimeSpan.FromTicks(Math.Min(
                    Math.Max(_clientUpdateBackoff.Ticks, TimeSpan.FromMinutes(10).Ticks) * 2,
                    TimeSpan.FromHours(1).Ticks))
                : TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            Log($"client update poll failed: {ex.Message}");
            _clientUpdateBackoff = TimeSpan.FromMinutes(10); // 意外异常按一次退避处理。
        }
        finally
        {
            ScheduleClientUpdatePoll();
        }
    }

    /// <summary>重排下一次轮询：基础间隔（或退避间隔）乘 ±20% 抖动，避免集中请求。</summary>
    private void ScheduleClientUpdatePoll()
    {
        var baseDelay = _clientUpdateBackoff == TimeSpan.Zero ? TimeSpan.FromMinutes(10) : _clientUpdateBackoff;
        var jitter = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2);
        var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitter);
        try
        {
            _clientUpdateTimer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 退出路径：忽略。
        }
    }

    /// <summary>拉取会话列表并更新托盘"最近会话"子菜单（在途去重：并发触发只放行一次）。</summary>
    private async Task RefreshSessionsAsync()
    {
        var client = _apiClient;
        if (client is null || !_serviceOnline)
        {
            ScheduleSessionRefresh();
            return;
        }

        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return; // 已有在途请求：其结果覆盖最新状态，完成后的常规重排兜底。
        }

        try
        {
            IReadOnlyList<DshSessionSummary> sessions;
            try
            {
                sessions = await client.ListSessionsAsync();
                _sessionRefreshFailures = 0;
            }
            catch (Exception ex)
            {
                _sessionRefreshFailures++;
                Log($"session list refresh failed ({_sessionRefreshFailures}): {ex.Message}");
                ScheduleSessionRefresh();
                return;
            }

        Dispatcher.UIThread.Post(() =>
        {
            _lastSessions = sessions;
            UpdateTrayTip();
        });
            ScheduleSessionRefresh();
        }
        finally
        {
            Volatile.Write(ref _refreshInFlight, 0);
        }
    }



    private void NavigateToSession(string sessionId)
    {
        ActivateMainWindow();
        _mainWindow?.NavigateToSession(sessionId);
    }

    /// <summary>在系统文件管理器中打开目录（托盘"打开所在目录"）。</summary>
    private static void OpenDirectoryInFileManager(string directory)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            Log($"opened workspace directory: {directory}");
        }
        catch (Exception ex)
        {
            Log($"open workspace directory failed ({directory}): {ex.Message}");
        }
    }


    /// <summary>在指定锚点（托盘图标）弹出品牌化面板：面板右下角 = 锚点在任务栏上沿的投影。</summary>
    private void ShowTrayFlyoutAt(int cursorX, int cursorY)
    {
        if (_trayFlyout is { IsVisible: true })
        {
            _trayFlyout.Close();
        }

        _trayFlyout = new Views.TrayFlyoutWindow
        {
            OpenMain = ActivateMainWindow,
            NewSession = () =>
            {
                ActivateMainWindow();
                _mainWindow?.NavigateHome();
            },
            OpenSession = sessionId => NavigateToSession(sessionId),
            ToggleRuntime = OnTrayServiceClick,
            OpenSettings = OpenSettingsWindow,
            Exit = RequestShutdown,
        };
        _trayFlyout.Closed += (_, _) => _trayFlyout = null;
        var running = _lastSessions.Count(session => session.Running);
        _trayFlyout.Present(
            serviceOnline: _serviceOnline,
            runningCount: running,
            runtimeStatus: _serviceManager?.IsOwned == true ? $"运行中 · {RuntimeBaseUrl}" : _serviceOnline ? "外部服务" : "未运行",
            runtimeOwned: _serviceManager?.IsOwned == true,
            sessions: _lastSessions
                .Take(6)
                .Select(x => new Views.TraySessionItem(x.SessionId, string.IsNullOrEmpty(x.Title) ? ShortId(x.SessionId) : (x.Title.Length > 26 ? x.Title[..26] + "…" : x.Title), x.Running))
                .ToArray(),
            anchor: new Avalonia.PixelPoint(cursorX, cursorY));
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

    /// <summary>托盘菜单已最小化为引导条目（完整交互在左键面板），保留调用点便于将来扩展。</summary>
    private void RefreshTrayMenu()
    {
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

    /// <summary>追加一行应用日志（%APPDATA%/DSHSharp/app.log），用于排查桌面壳问题。
    /// 与服务日志共用 2MB 轮转，常驻运行不无限增长。</summary>
    public static void Log(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            DshServiceManager.RotateLogFile(LogPath, DshServiceManager.MaxServiceLogBytes);
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
        Services.SessionNotifications.Uninitialize();
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

    /// <summary>一键备份会话数据（dsh-home 的会话/配置，排除凭据与派生物）到文档目录，返回 zip 路径。</summary>
    public string BackupUserData()
    {
        var zipPath = UserDataMaintenance.CreateBackupZip(AppDataDirectory);
        Log($"user data backup created: {zipPath}");
        return zipPath;
    }

    /// <summary>一键导出诊断包（日志尾部 token 脱敏 + 版本/运行时/更新状态清单）到文档目录，返回 zip 路径。</summary>
    public string ExportDiagnostics()
    {
        var update = _clientUpdate?.State;
        var profileManifest = Path.Combine(AppDataDirectory, "dsh-home", "profiles", "web", "package.json");
        var extras = new Dictionary<string, string>
        {
            ["versions.txt"] = string.Join(Environment.NewLine, new[]
            {
                $"client: DSH-Sharp {DshSharpCompatibility.ProductVersion}",
                $"runtime: {_serviceManager?.InstalledPackageVersion ?? "未安装"}",
                $"os: {Environment.OSVersion.VersionString} ({Environment.OSVersion.Platform})",
                $"install: {Environment.ProcessPath}",
                $"update-state: {update?.Phase ?? "未启用"} {update?.LatestVersion ?? ""}".TrimEnd(),
            }),
            ["profile-web-package.json"] = File.Exists(profileManifest)
                ? File.ReadAllText(profileManifest)
                : "（不存在）",
        };
        var zipPath = UserDataMaintenance.CreateDiagnosticsZip(AppDataDirectory, extras);
        Log($"diagnostics exported: {zipPath}");
        return zipPath;
    }

    private void SetupDshMonitor(DshAuthSession auth)    {
        DshEventMonitor.Log = Log;
        _monitor = new DshEventMonitor(auth);
        _monitor.SessionCompleted += OnSessionCompleted;
        _monitor.ServiceAvailabilityChanged += OnServiceAvailabilityChanged;
        _monitor.RunningSessionsChanged += OnRunningSessionsChanged;
        // 原生 Toast 点击激活回调来自 COM 线程，统一转 UI 线程导航。
        Services.SessionNotifications.EnsureInitialized(
            sessionId => Dispatcher.UIThread.Post(() => NavigateToSession(sessionId)));
        _monitor.Start();
    }

    private void OnSessionCompleted(object? sender, SessionCompletedEventArgs e)
    {
        Log($"session completed event: session={e.SessionId}, title={e.Title}, notificationsEnabled={Settings.SessionCompleteNotifications}");
        if (!Settings.SessionCompleteNotifications)
        {
            return;
        }

        // 后台线程异步获取回合结局（完成/失败/中止）与最后回复开头，再决定是否弹 Toast。
        _ = Task.Run(async () =>
        {
            string title;
            string? preview = null;
            string outcomeKind = "completed";
            string? errorMessage = null;
            try
            {
                var client = _apiClient ?? new DshApiClient(_authSession ?? new DshAuthSession(RuntimeBaseUrl));
                title = string.IsNullOrEmpty(e.Title) ? ShortId(e.SessionId) : e.Title;
                // session/page 的 throughSeq 不得越过会话游标：优先用事件流缓存的 asOfSeq，缺失时从会话列表补查。
                var throughSeq = e.AsOfSeq;
                if (throughSeq <= 0)
                {
                    var sessions = await client.ListSessionsAsync();
                    throughSeq = sessions.FirstOrDefault(s => s.SessionId == e.SessionId)?.AsOfSeq ?? 0;
                }

                if (throughSeq > 0)
                {
                    var outcome = await client.GetTurnOutcomeAsync(e.SessionId, throughSeq);
                    outcomeKind = outcome.Kind ?? "completed";
                    errorMessage = outcome.ErrorMessage;
                    preview = outcome.LastAssistantText;
                }
            }
            catch (Exception ex)
            {
                Log($"session completion preview failed: {ex.Message}");
                title = string.IsNullOrEmpty(e.Title) ? ShortId(e.SessionId) : e.Title;
            }

            // aborted 是用户主动停止（含 Esc）：不再打扰。
            if (outcomeKind == "aborted")
            {
                Log($"session turn ended as aborted, skip toast: session={e.SessionId}");
                return;
            }

            var failed = outcomeKind == "error";
            var heading = failed ? "会话回合失败" : "会话已完成";
            if (failed && string.IsNullOrEmpty(preview))
            {
                preview = errorMessage ?? "（无错误详情）";
            }

            var finalTitle = title;
            var finalPreview = preview;

            // 原生 Toast 优先：进操作中心、系统默认音、点击直达会话，无需强制唤起窗口。
            if (Services.SessionNotifications.TryShowSessionCompleted(e.SessionId, heading, finalTitle, finalPreview))
            {
                Log($"session turn outcome: kind={outcomeKind}, native toast shown '{finalTitle}'");
                return;
            }

            var finalHeading = heading;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_mainWindow is null)
                    {
                        Log("session completed: main window is null, skip toast");
                        return;
                    }

                    Log($"session turn outcome: kind={outcomeKind}, showing toast '{finalTitle}', preview='{finalPreview}'");

                    // 通知音效（可配置开关）。
                    if (Settings.NotificationSoundEnabled)
                    {
                        Services.NotificationSound.Play();
                    }

                    _mainWindow.ShowNotification(finalHeading, finalTitle, finalPreview);

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

    /// <summary>运行中会话数量变化：标题栏计数 + 任务栏脉冲 + 托盘最近会话即时刷新。</summary>
    private void OnRunningSessionsChanged(object? sender, RunningSessionsChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => UpdateRunningIndicator(e.Count));
    }

    /// <summary>UI 线程：根据运行中会话数刷新窗口标题、任务栏状态与托盘 tooltip。</summary>
    private void UpdateRunningIndicator(int count)
    {
        try
        {
            if (_mainWindow is { } window)
            {
                window.Title = count > 0
                    ? $"{BaseWindowTitle} — {count} 个会话运行中"
                    : BaseWindowTitle;
                var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
                Services.WindowsTaskbarProgress.SetRunningIndicator(handle, count > 0);
            }

            UpdateTrayTip();

            // 事件驱动的即时刷新：滑动 2 秒合并窗口，托盘 "●" 运行标记与列表次序保持新鲜。
            ScheduleSessionRefreshSoon();
        }
        catch (Exception ex)
        {
            Log($"running indicator update failed: {ex.Message}");
        }
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
                if (!e.IsOnline)
                {
                    // 服务下线后运行状态已无意义：清空标题/任务栏指示。
                    UpdateRunningIndicator(0);
                }
            }
            catch (Exception ex)
            {
                Log($"service status update failed: {ex.Message}");
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
            if (_isStarting)
            {
                // 正常启动：只显示品牌过场动画（不出现"正在准备 Runtime"的工程文案）。
                // Node/WebView2 修复进度与故障页由各自处理器调 ShowOnboarding 覆盖（Z 序在过场之上）。
                _mainWindow.ShowOnboarding(false, null, false);
                _mainWindow.ShowSplash(true);
            }
            else
            {
                _mainWindow.ShowSplash(false);
                var detail = _serviceManager?.LastError ?? BuildOnboardingDetail();
                _mainWindow.ShowOnboarding(true, detail, owned);
            }

            // 安全模式入口：只在"启动失败（非忙）+ 存在已激活第三方插件"时出现。
            _mainWindow.SetSafeModeOffer(
                !_isStarting &&
                !owned &&
                _serviceManager?.LastError is not null &&
                _serviceManager.HasThirdPartyPluginsActive);
        }
        else
        {
            _mainWindow.ShowSplash(false);
            _mainWindow.SetSafeModeOffer(false);
            if (_mainWindow.TryPrepareWebView())
            {
                _mainWindow.ShowOnboarding(false, null, false);
                // 深链（dshsharp://session/<id>）冷启动：服务就绪后导航一次；
                // 插件侧的 store 就绪重试保证早于列表 baseline 的导航也能生效。
                if (PendingDeepLinkSession is { } session)
                {
                    PendingDeepLinkSession = null;
                    Log($"deep link session navigation: {session}");
                    NavigateToSession(session);
                }
            }
            else
            {
                // WebView2 缺失/安装中：Web 面板保持隐藏，引导页转为运行时修复视图。
                var repairing = _webView2Service.State.Phase is not ("Failed" or "Ready" or "Idle");
                _mainWindow.ShowOnboarding(true, BuildWebView2Detail(), repairing);
            }
        }

        RefreshTrayMenu();
    }

    /// <summary>
    /// 检查 DSH-Sharp 与 DSH 版本，并报告当前托管模式下的兼容状态。
    /// 返回可直接展示的文本。
    /// </summary>
    public async Task<string> CheckDshVersionAsync()
    {
        var api = _apiClient ?? new DshApiClient(_authSession ?? new DshAuthSession(RuntimeBaseUrl));
        try
        {
            var installed = _serviceManager?.InstalledPackageVersion;
            var latest = await api.GetNpmLatestVersionAsync();
            // 目标版本与升级按钮同语义：npm latest 在支持范围内取 latest，否则取客户端最新已验证版本。
            var target = DshSharpCompatibility.IsCompatible(latest) ? latest : DshSharpCompatibility.DefaultDshVersion;
            if (latest is null)
            {
                return $"DSH-Sharp：{DshSharpCompatibility.ProductVersion}\nDSH 私有运行版本：{installed ?? "未安装"}\n支持范围：{DshSharpCompatibility.SupportedRange}\nnpm 最新版本：查询失败";
            }
            var status = !DshSharpCompatibility.IsCompatible(installed) ? "私有安装版本不兼容或无法识别，请先升级客户端" :
                installed == target ? "已是最新" : $"可更新到 {target}";
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
        if (!await ConfirmInterruptionAsync("更新 DSH Runtime"))
        {
            Log("managed Runtime update cancelled by user (running sessions)");
            return;
        }

        _isStarting = true;
        UpdateServiceUi();
        try
        {
            string? targetVersion;
            try
            {
                // npm latest 标签可能仍指向旧的稳定线（如 0.1.2-rc.1），而官方最新发布挂在
                // next/alpha 标签上；目标版本优先取 latest，不在支持范围时回退到客户端
                // 最新已验证版本（DefaultDshVersion 恒在白名单内）。
                var latest = await (_apiClient ?? new DshApiClient(_authSession ?? new DshAuthSession(RuntimeBaseUrl))).GetNpmLatestVersionAsync();
                targetVersion = DshSharpCompatibility.IsCompatible(latest) ? latest : DshSharpCompatibility.DefaultDshVersion;
            }
            catch (Exception ex)
            {
                _mainWindow?.ShowNotification("DSH 更新失败", $"无法查询 npm 版本：{ex.Message}");
                return;
            }

            if (!DshSharpCompatibility.IsCompatible(targetVersion))
            {
                _mainWindow?.ShowNotification("DSH 更新已阻止", $"目标版本 {targetVersion ?? "未知"} 不在支持范围 {DshSharpCompatibility.SupportedRange} 内");
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
        var nodeLine = $"\nNode：{_serviceManager?.ActiveNodeDescription ?? "未检查"}";
        return $"● {RuntimeBaseUrl}\n运行时：{modeText}{nodeLine}\n数据目录：{_serviceManager?.DshHomeDirectory ?? "未初始化"}\n状态：{status}{updateLine}{errorLine}";
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

    /// <summary>
    /// 处理 WebUI 主题桥消息：web 端的 data-ds-theme-source 是为宿主壳镜像设计的官方信号。
    /// 仅当壳主题设置为 System 时镜像 web 的浅/深选择；用户显式固定 Light/Dark 时壳设置优先。
    /// </summary>
    public void ApplyWebThemeMessage(string body)
    {
        try
        {
            if (!body.Contains("dshsharp-theme", StringComparison.Ordinal))
            {
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("source", out var sourceEl) ||
                sourceEl.GetString() is not { } source)
            {
                return;
            }

            // 主题桥仅记录 WebUI 外观供诊断/将来参考，不再驱动壳——
            // System 档的壳跟随 Windows 深浅（实时），WebUI 主题由 web 自己管理。
            Dispatcher.UIThread.Post(() =>
            {
                var theme = source switch
                {
                    "dark" => "Dark",
                    "light" => "Light",
                    _ => "System",
                };
                if (Settings.LastWebTheme != theme)
                {
                    Settings.LastWebTheme = theme;
                    Log($"web theme observed: {theme} (recorded, not applied to shell)");
                }
            });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException)
        {
            // 非 JSON 消息：忽略。
        }
        catch (Exception ex)
        {
            Log($"web theme message failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 应用外观：显式 Light/Dark 直接生效；System 解析为具体值（上次 WebUI 外观优先——
    /// 窗口主体是 WebUI，与它一致比与 OS 一致更重要；无历史读系统注册表）。
    /// 品牌色板随解析结果直接写入 Resources，DynamicResource 即时重取——
    /// 启动首帧与运行中切换（设置页、主题桥）走同一条路径。
    /// </summary>
    private static void ApplyTheme(string theme)
    {
        var current = Application.Current;
        if (current is null)
        {
            return;
        }

        var resolved = theme switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => ResolveSystemTheme(),
        };
        current.RequestedThemeVariant = resolved == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var palette = resolved == "Dark" ? Services.ShellPalettes.Dark : Services.ShellPalettes.Light;
        foreach (var (key, value) in palette)
        {
            current.Resources[key] = value;
        }

        _lastAppliedTheme = theme;
        Log($"theme applied: {theme} -> {resolved}");
    }

    /// <summary>最近一次已应用的主题设置。设置页持有活设置实例（保存回调传入同一引用），
    /// 对象 diff 永远为假——主题变化必须对照实际生效值判断。</summary>
    private static string? _lastAppliedTheme;

    /// <summary>System 主题实时跟随：Avalonia PlatformSettings 的 ColorValuesChanged
    /// 在 Windows 深浅切换时触发（内部即 WM_SETTINGCHANGE），System 档下即时重新解析。</summary>
    private static void StartSystemThemeWatcher()
    {
        var platform = Application.Current?.PlatformSettings;
        if (platform is null)
        {
            return;
        }

        platform.ColorValuesChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Instance?.Settings.Theme == "System")
                {
                    Log("system color values changed -> re-applying System theme");
                    ApplyTheme("System");
                }
            });
        };
    }

    /// <summary>System 档的具体化：读取 Windows 系统浅色/深色注册表值。</summary>
    private static string ResolveSystemTheme()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int light)
                {
                    return light == 1 ? "Light" : "Dark";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 读不了注册表：按浅色兜底。
        }

        return "Light";
    }
}
