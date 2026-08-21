using DSHSharp.Core.Configuration;

namespace DSHSharp.Core.Tests;

public sealed class ProfileHelperTests
{
    [Fact]
    public void EnsureDefaultProfile_MigratesLegacyTopLevelFields()
    {
        var settings = new AppSettings
        {
            WebUrl = "http://127.0.0.1:39999",
            ManagedMode = "Source",
            SourcePath = @"D:\repo",
        };

        ProfileHelper.EnsureDefaultProfile(settings);

        Assert.Single(settings.Profiles);
        Assert.Equal("默认配置", settings.Profiles[0].Name);
        Assert.Equal("http://127.0.0.1:39999", settings.Profiles[0].WebUrl);
        Assert.Equal("Source", settings.Profiles[0].ManagedMode);
        Assert.Equal(@"D:\repo", settings.Profiles[0].SourcePath);
        Assert.Equal("默认配置", settings.ActiveProfileName);
    }

    [Fact]
    public void EnsureDefaultProfile_KeepsExistingProfiles()
    {
        var settings = new AppSettings { Profiles = [new ServiceProfile { Name = "A" }] };

        ProfileHelper.EnsureDefaultProfile(settings);

        Assert.Single(settings.Profiles);
        Assert.Equal("A", settings.Profiles[0].Name);
    }

    [Fact]
    public void GetActiveProfile_FallsBackToFirst_WhenNameMissing()
    {
        var settings = new AppSettings
        {
            Profiles =
            [
                new ServiceProfile { Name = "A" },
                new ServiceProfile { Name = "B" },
            ],
            ActiveProfileName = "不存在",
        };

        var active = ProfileHelper.GetActiveProfile(settings);

        Assert.Equal("A", active.Name);
        Assert.Equal("A", settings.ActiveProfileName);
    }

    [Fact]
    public void ApplyActiveProfile_SyncsTopLevelFields()
    {
        var settings = new AppSettings
        {
            WebUrl = "http://old:1",
            ManagedMode = "Npx",
            Profiles = [new ServiceProfile { Name = "B", WebUrl = "http://b:2", ManagedMode = "Source", SourcePath = @"D:\b" }],
            ActiveProfileName = "B",
        };

        ProfileHelper.ApplyActiveProfile(settings);

        Assert.Equal("http://b:2", settings.WebUrl);
        Assert.Equal("Source", settings.ManagedMode);
        Assert.Equal(@"D:\b", settings.SourcePath);
    }

    [Fact]
    public void ActivateProfile_SwitchesAndSyncs()
    {
        var settings = new AppSettings
        {
            Profiles =
            [
                new ServiceProfile { Name = "A", WebUrl = "http://a:1" },
                new ServiceProfile { Name = "B", WebUrl = "http://b:2" },
            ],
            ActiveProfileName = "A",
        };

        Assert.True(ProfileHelper.ActivateProfile(settings, "B"));
        Assert.Equal("B", settings.ActiveProfileName);
        Assert.Equal("http://b:2", settings.WebUrl);

        // 同名或不存在：不切换。
        Assert.False(ProfileHelper.ActivateProfile(settings, "B"));
        Assert.False(ProfileHelper.ActivateProfile(settings, "不存在"));
    }
}
