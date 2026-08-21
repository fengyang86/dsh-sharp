using DSHSharp.Core.Configuration;
using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dshsharp-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_WhenNoFile_ReturnsDefaults()
    {
        var settings = new AppSettingsService(_dir).Load();

        Assert.Equal(AppSettings.DefaultWebUrl, settings.WebUrl);
        Assert.True(settings.CloseToTray);
        Assert.True(settings.SessionCompleteNotifications);
        Assert.True(settings.NotificationSoundEnabled);
        Assert.Equal("System", settings.Theme);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllProperties()
    {
        var service = new AppSettingsService(_dir);
        var settings = new AppSettings
        {
            WebUrl = "http://localhost:9999",
            AutoStartEnabled = true,
            CloseToTray = false,
            StartMinimized = true,
            Theme = "Dark",
            SessionCompleteNotifications = false,
            NotificationSoundEnabled = false,
        };

        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal("http://localhost:9999", loaded.WebUrl);
        Assert.True(loaded.AutoStartEnabled);
        Assert.False(loaded.CloseToTray);
        Assert.True(loaded.StartMinimized);
        Assert.Equal("Dark", loaded.Theme);
        Assert.False(loaded.SessionCompleteNotifications);
        Assert.False(loaded.NotificationSoundEnabled);
    }

    [Fact]
    public void Load_WhenCorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not valid json !!");

        var settings = new AppSettingsService(_dir).Load();

        Assert.Equal(AppSettings.DefaultWebUrl, settings.WebUrl);
    }

    [Fact]
    public void Save_CreatesDirectoryAndFile()
    {
        var service = new AppSettingsService(_dir);
        service.Save(new AppSettings());

        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响结果。
        }
    }
}
