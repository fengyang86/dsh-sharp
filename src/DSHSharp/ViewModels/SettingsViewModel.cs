using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSHSharp.Core.Configuration;

namespace DSHSharp.ViewModels;

/// <summary>服务配置列表项（包装 ServiceProfile，供列表展示与编辑）。</summary>
public partial class ServiceProfileItem : ViewModelBase
{
    private readonly ServiceProfile _profile;

    public ServiceProfileItem(ServiceProfile profile, bool isActive)
    {
        _profile = profile;
        IsActive = isActive;
    }

    public ServiceProfile Profile => _profile;

    [ObservableProperty]
    private bool _isActive;

    public string Name
    {
        get => _profile.Name;
        set
        {
            if (_profile.Name != value)
            {
                _profile.Name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string WebUrl
    {
        get => _profile.WebUrl;
        set
        {
            if (_profile.WebUrl != value)
            {
                _profile.WebUrl = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string ManagedMode
    {
        get => _profile.ManagedMode;
        set
        {
            if (_profile.ManagedMode != value)
            {
                _profile.ManagedMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string? SourcePath
    {
        get => _profile.SourcePath;
        set
        {
            if (_profile.SourcePath != value)
            {
                _profile.SourcePath = value;
                OnPropertyChanged();
            }
        }
    }

    public string[] ManagedModes { get; } = ["Npx", "Source", "None"];

    /// <summary>列表摘要：模式 + 地址。</summary>
    public string Summary
    {
        get
        {
            var modeText = ManagedMode switch
            {
                "None" => "不托管",
                "Source" => "源码",
                _ => "官方包",
            };
            return $"{modeText} · {WebUrl}";
        }
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    public sealed partial class PluginItem : ObservableObject
    {
        public PluginItem(string name, string? version, bool isBundled, bool isActive, string? description)
        { Name = name; Version = version ?? "未知版本"; IsBundled = isBundled; IsActive = isActive; Description = description ?? ""; }
        public string Name { get; }
        public string Version { get; }
        public bool IsBundled { get; }
        public string Description { get; }
        [ObservableProperty] private bool _isActive;
    }

    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action _startService;
    private readonly Action _stopService;
    private readonly Func<Task<string>> _checkVersion;
    private readonly Action _updateService;
    private readonly Func<IReadOnlyList<DSHSharp.Core.Dsh.DshServiceManager.ProfilePlugin>> _listPlugins;
    private readonly Func<string, bool, Task<bool>> _setPluginActive;
    private readonly Func<string, Task<bool>> _removePlugin;

    public SettingsViewModel(
        AppSettings settings,
        string serviceStatusText,
        Action<AppSettings> save,
        Action startService,
        Action stopService,
        Func<Task<string>>? checkVersion = null,
        Action? updateService = null,
        Func<IReadOnlyList<DSHSharp.Core.Dsh.DshServiceManager.ProfilePlugin>>? listPlugins = null,
        Func<string, bool, Task<bool>>? setPluginActive = null,
        Func<string, Task<bool>>? removePlugin = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _save = save;
        _startService = startService;
        _stopService = stopService;
        _checkVersion = checkVersion ?? (() => Task.FromResult("版本检查不可用"));
        _updateService = updateService ?? (() => { });
        _listPlugins = listPlugins ?? (() => []);
        _setPluginActive = setPluginActive ?? ((_, _) => Task.FromResult(false));
        _removePlugin = removePlugin ?? (_ => Task.FromResult(false));
        RefreshPlugins();

        _serviceStatusText = serviceStatusText;
        AutoStartEnabled = settings.AutoStartEnabled;
        CloseToTray = settings.CloseToTray;
        StartMinimized = settings.StartMinimized;
        SessionCompleteNotifications = settings.SessionCompleteNotifications;
        NotificationSoundEnabled = settings.NotificationSoundEnabled;
        Theme = settings.Theme;

        ProfileHelper.EnsureDefaultProfile(settings);
        foreach (var profile in settings.Profiles)
        {
            Profiles.Add(new ServiceProfileItem(profile, profile.Name == settings.ActiveProfileName));
        }

        if (Profiles.Count > 0)
        {
            SelectedProfile = Profiles[0];
        }
    }

    // ---- 导航 ----

    public string[] Sections { get; } = ["运行时", "插件", "偏好设置", "版本与更新"];

    [ObservableProperty]
    private string _selectedSection = "运行时";

    partial void OnSelectedSectionChanged(string value)
    {
        ShowProfiles = value == "运行时";
        ShowPlugins = value == "插件";
        ShowGeneral = value == "偏好设置";
        ShowAbout = value == "版本与更新";
        if (ShowAbout && !IsCheckingUpdate)
        {
            _ = CheckUpdate();
        }
    }

    [ObservableProperty]
    private bool _showProfiles = true;

    [ObservableProperty]
    private bool _showGeneral;

    [ObservableProperty]
    private bool _showPlugins;

    [ObservableProperty]
    private bool _showVersion;

    [ObservableProperty]
    private bool _showAbout;

    // ---- 服务状态卡片 ----

    [ObservableProperty]
    private string _serviceStatusText;

    // ---- 服务配置 ----

    public ObservableCollection<ServiceProfileItem> Profiles { get; } = [];

    [ObservableProperty]
    private ServiceProfileItem? _selectedProfile;

    [RelayCommand]
    private void AddProfile()
    {
        var index = Profiles.Count + 1;
        var profile = new ServiceProfile
        {
            Name = $"新配置 {index}",
            WebUrl = _settings.WebUrl,
            ManagedMode = _settings.ManagedMode,
            SourcePath = _settings.SourcePath,
        };
        var item = new ServiceProfileItem(profile, isActive: false);
        Profiles.Add(item);
        SelectedProfile = item;
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var source = SelectedProfile.Profile;
        var copy = new ServiceProfile
        {
            Name = source.Name + "（副本）",
            WebUrl = source.WebUrl,
            ManagedMode = source.ManagedMode,
            SourcePath = source.SourcePath,
        };
        var item = new ServiceProfileItem(copy, isActive: false);
        Profiles.Add(item);
        SelectedProfile = item;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1)
        {
            return;
        }

        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles[Math.Min(index, Profiles.Count - 1)];
    }

    /// <summary>保存全部配置与通用设置，并重建连接（激活配置内容变化即时生效）。</summary>
    [RelayCommand]
    private void Save()
    {
        _settings.AutoStartEnabled = AutoStartEnabled;
        _settings.CloseToTray = CloseToTray;
        _settings.StartMinimized = StartMinimized;
        _settings.SessionCompleteNotifications = SessionCompleteNotifications;
        _settings.NotificationSoundEnabled = NotificationSoundEnabled;
        _settings.Theme = Theme;

        _save(_settings);

        CloseRequested?.Invoke();
    }

    /// <summary>把选中的配置设为激活并立即切换（无需整体保存）。</summary>
    [RelayCommand]
    private void SetActive()
    {
        if (SelectedProfile is null || Profiles.Count == 0)
        {
            return;
        }

        // 历史多连接配置仅供旧 settings.json 兼容读取，不再参与 Runtime 选择。
    }

    /// <summary>不保存，直接关闭窗口。</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    /// <summary>重置为默认值：通用开关/主题 + 重建单个"默认配置"（需再点"保存"生效）。</summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        AutoStartEnabled = defaults.AutoStartEnabled;
        CloseToTray = defaults.CloseToTray;
        StartMinimized = defaults.StartMinimized;
        SessionCompleteNotifications = defaults.SessionCompleteNotifications;
        NotificationSoundEnabled = defaults.NotificationSoundEnabled;
        Theme = defaults.Theme;

        Profiles.Clear();
        var profile = new ServiceProfile { Name = "默认配置" };
        Profiles.Add(new ServiceProfileItem(profile, isActive: true));
        SelectedProfile = Profiles[0];
    }

    /// <summary>关闭窗口请求（由窗口订阅）。</summary>
    public event Action? CloseRequested;

    // ---- 通用 ----

    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _sessionCompleteNotifications;

    [ObservableProperty]
    private bool _notificationSoundEnabled;

    [ObservableProperty]
    private string _theme;

    public string[] Themes { get; } = ["System", "Light", "Dark"];

    // ---- 版本更新 ----

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private string _versionInfoText = "正在读取私有 DSH Runtime 信息…";

    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckUpdate()
    {
        IsCheckingUpdate = true;
        VersionInfoText = "正在检查版本…";
        try
        {
            VersionInfoText = await _checkVersion();
        }
        catch (Exception ex)
        {
            VersionInfoText = $"版本检查失败：{ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private bool CanCheckUpdate() => !IsCheckingUpdate;

    public bool CanUpdateOfficialPackage => true;

    [RelayCommand]
    private void UpdateService() => _updateService();

    public ObservableCollection<PluginItem> Plugins { get; } = [];

    public void UpdateServiceStatus(string text) => ServiceStatusText = text;

    private void RefreshPlugins()
    {
        Plugins.Clear();
        foreach (var plugin in _listPlugins())
        {
            Plugins.Add(new PluginItem(plugin.Name, plugin.Version ?? "未知版本", plugin.IsBundled,
                plugin.IsActive, plugin.IsBundled ? "DSH-Sharp 内置插件，由客户端维护" : "当前 DSH profile 中的第三方插件"));
        }
    }

    [RelayCommand]
    private async Task TogglePlugin(PluginItem? plugin)
    {
        if (plugin is null || plugin.IsBundled) return;
        // ToggleSwitch 先更新绑定值，再执行命令；当前值就是用户要求的目标状态。
        var target = plugin.IsActive;
        if (await _setPluginActive(plugin.Name, target))
        {
            plugin.IsActive = target;
            return;
        }

        // 写入或验证失败时恢复视觉状态，确保设置页不展示未生效的激活结果。
        plugin.IsActive = !target;
    }

    [RelayCommand]
    private async Task RemovePlugin(PluginItem? plugin)
    {
        if (plugin is null || plugin.IsBundled) return;
        if (await _removePlugin(plugin.Name)) Plugins.Remove(plugin);
    }

    // ---- 服务操作（状态卡片） ----

    [RelayCommand]
    private void StartService() => _startService();

    [RelayCommand]
    private void StopService() => _stopService();

    // ---- 关于 ----

    public string AboutText =>
        "DSH-Sharp · DeepSeek Harness 桌面客户端\n" +
        $".NET 10 + Avalonia\n" +
        $"客户端版本 {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0"}\n" +
        "MIT License";
}
