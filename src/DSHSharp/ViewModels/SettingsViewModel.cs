using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSHSharp.Core.Configuration;

namespace DSHSharp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly Action<AppSettings> _save;

    public SettingsViewModel(AppSettings current, string serviceStatusText, Action<AppSettings> save)
    {
        ArgumentNullException.ThrowIfNull(current);
        _save = save;

        ServiceStatusText = serviceStatusText;
        WebUrl = current.WebUrl;
        ManagedMode = current.ManagedMode;
        SourcePath = current.SourcePath ?? string.Empty;
        AutoStartEnabled = current.AutoStartEnabled;
        CloseToTray = current.CloseToTray;
        StartMinimized = current.StartMinimized;
        SessionCompleteNotifications = current.SessionCompleteNotifications;
        NotificationSoundEnabled = current.NotificationSoundEnabled;
        Theme = current.Theme;
    }

    /// <summary>当前 DSH 服务的实际状态（在线来源/离线），由宿主注入。</summary>
    public string ServiceStatusText { get; }

    public string[] ManagedModes { get; } = ["Npx", "Source", "None"];

    public string[] Themes { get; } = ["System", "Light", "Dark"];

    [ObservableProperty]
    private string _webUrl;

    [ObservableProperty]
    private string _managedMode;

    [ObservableProperty]
    private string _sourcePath;

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

    /// <summary>保存设置并关闭窗口。</summary>
    [RelayCommand]
    private void Save()
    {
        App.Log($"settings window: Save() invoked, theme={Theme}, webUrl={WebUrl}");
        var updated = new AppSettings
        {
            WebUrl = NormalizeUrl(WebUrl),
            ManagedMode = ManagedMode,
            SourcePath = string.IsNullOrWhiteSpace(SourcePath) ? null : SourcePath.Trim(),
            AutoStartEnabled = AutoStartEnabled,
            CloseToTray = CloseToTray,
            StartMinimized = StartMinimized,
            SessionCompleteNotifications = SessionCompleteNotifications,
            NotificationSoundEnabled = NotificationSoundEnabled,
            Theme = Theme,
        };
        _save(updated);
        CloseRequested?.Invoke();
    }

    /// <summary>不保存，直接关闭窗口。</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    /// <summary>关闭窗口请求（由窗口订阅）。</summary>
    public event Action? CloseRequested;

    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return AppSettings.DefaultWebUrl;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed;
    }
}
