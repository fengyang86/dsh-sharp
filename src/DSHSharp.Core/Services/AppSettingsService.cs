using System.Text.Json;
using DSHSharp.Core.Configuration;

namespace DSHSharp.Core.Services;

/// <summary>
/// 应用设置持久化：JSON 文件，默认存放于
/// %APPDATA%/DSHSharp/settings.json（可通过构造参数覆盖目录，便于测试）。
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _settingsPath;

    public AppSettingsService(string? settingsDirectory = null)
    {
        var directory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHSharp");
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    /// <summary>加载设置；文件缺失或损坏时返回默认值。</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置文件损坏/不可读：回退默认值，不影响启动。
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _settingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(PersistedSettings.From(settings), JsonOptions));
        try
        {
            if (File.Exists(_settingsPath))
                File.Replace(tempPath, _settingsPath, null);
            else
                File.Move(tempPath, _settingsPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// 写出模型刻意不包含旧版多连接字段。加载时仍接受它们，保证历史 settings.json
    /// 可直接启动；下一次保存会迁移到“单一私有 Runtime”的设置格式。
    /// </summary>
    private sealed class PersistedSettings
    {
        public bool AutoStartEnabled { get; init; }
        public bool CloseToTray { get; init; }
        public bool StartMinimized { get; init; }
        public string Theme { get; init; } = "System";
        public bool SessionCompleteNotifications { get; init; }
        public bool NotificationSoundEnabled { get; init; }
        public double? WindowLeft { get; init; }
        public double? WindowTop { get; init; }
        public double? WindowWidth { get; init; }
        public double? WindowHeight { get; init; }
        public bool WindowMaximized { get; init; }

        public static PersistedSettings From(AppSettings settings) => new()
        {
            AutoStartEnabled = settings.AutoStartEnabled,
            CloseToTray = settings.CloseToTray,
            StartMinimized = settings.StartMinimized,
            Theme = settings.Theme,
            SessionCompleteNotifications = settings.SessionCompleteNotifications,
            NotificationSoundEnabled = settings.NotificationSoundEnabled,
            WindowLeft = settings.WindowLeft,
            WindowTop = settings.WindowTop,
            WindowWidth = settings.WindowWidth,
            WindowHeight = settings.WindowHeight,
            WindowMaximized = settings.WindowMaximized,
        };
    }
}
