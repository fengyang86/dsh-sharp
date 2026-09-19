using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

public sealed class UserDataMaintenanceTests
{
    [Fact]
    public void SelectBackupEntries_IncludesSessionsAndExcludesSecretsAndDerived()
    {
        var root = Path.Combine(Path.GetTempPath(), "dshsharp-maint-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dsh-home", "sessions", "abc"));
            Directory.CreateDirectory(Path.Combine(root, "dsh-home", "storages"));
            Directory.CreateDirectory(Path.Combine(root, "dsh-home", "skills", "mine"));
            Directory.CreateDirectory(Path.Combine(root, "dsh-home", "profiles", "web", "node_modules", "@yangfeng"));
            File.WriteAllText(Path.Combine(root, "dsh-home", "sessions", "abc", "session.json"), "{}");
            File.WriteAllText(Path.Combine(root, "dsh-home", "storages", "workspace.json"), "{}");
            File.WriteAllText(Path.Combine(root, "dsh-home", "skills", "mine", "SKILL.md"), "x");
            File.WriteAllText(Path.Combine(root, "dsh-home", "settings.yaml"), "a: 1");
            File.WriteAllText(Path.Combine(root, "dsh-home", ".credentials.yaml"), "SECRET");
            File.WriteAllText(Path.Combine(root, "dsh-home", "debug.log"), "log");
            File.WriteAllText(Path.Combine(root, "dsh-home", "profiles", "web", "package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "dsh-home", "profiles", "web", "cordis.patch.yml"), "x");
            File.WriteAllText(Path.Combine(root, "dsh-home", "profiles", "web", "node_modules", "@yangfeng", "pkg.js"), "x");
            File.WriteAllText(Path.Combine(root, "settings.json"), "{}");

            var entries = UserDataMaintenance.SelectBackupEntries(root);

            Assert.Contains("dsh-home/sessions/abc/session.json", entries);
            Assert.Contains("dsh-home/storages/workspace.json", entries);
            Assert.Contains("dsh-home/skills/mine/SKILL.md", entries);
            Assert.Contains("dsh-home/settings.yaml", entries);
            Assert.Contains("dsh-home/profiles/web/package.json", entries);
            Assert.Contains("dsh-home/profiles/web/cordis.patch.yml", entries);
            Assert.Contains("settings.json", entries);
            Assert.False(entries.Any(e => e.EndsWith(".credentials.yaml", StringComparison.OrdinalIgnoreCase)), "凭据不应入备份");
            Assert.False(entries.Any(e => e.EndsWith(".log", StringComparison.OrdinalIgnoreCase)), "日志不应入备份");
            Assert.False(entries.Any(e => e.Contains("node_modules", StringComparison.OrdinalIgnoreCase)), "node_modules 不应入备份");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RedactTokens_MasksUrlTokensAndCookieValues()
    {
        var raw = "url http://127.0.0.1:3080/?token=abc_DEF-123 x; cookie dsh-auth-AbCd12=zzz9; other token=keep_no";
        var redacted = UserDataMaintenance.RedactTokens(raw);

        Assert.DoesNotContain("abc_DEF-123", redacted);
        Assert.DoesNotContain("zzz9", redacted);
        Assert.Contains("token=<redacted>", redacted);
        Assert.Contains("dsh-auth-AbCd12=<redacted>", redacted);
    }

    [Fact]
    public void CreateBackupZip_ProducesZipWithSelectedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "dshsharp-maint-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(Path.GetTempPath(), "dshsharp-maint-dest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dsh-home", "sessions"));
            File.WriteAllText(Path.Combine(root, "dsh-home", "sessions", "s.json"), "{}");
            File.WriteAllText(Path.Combine(root, "dsh-home", ".credentials.yaml"), "SECRET");

            var zipPath = UserDataMaintenance.CreateBackupZip(root, dest);

            Assert.True(File.Exists(zipPath));
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("dsh-home/sessions/s.json", names);
            Assert.DoesNotContain("dsh-home/.credentials.yaml", names);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void CreateDiagnosticsZip_WritesRedactedLogsAndExtras()
    {
        var root = Path.Combine(Path.GetTempPath(), "dshsharp-diag-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(Path.GetTempPath(), "dshsharp-diag-dest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "app.log"), "line1 token=SUPERSECRET9\nline2");
            var zipPath = UserDataMaintenance.CreateDiagnosticsZip(
                root, new Dictionary<string, string> { ["versions.txt"] = "client: 0.2.7" }, dest);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            Assert.Contains("app.log", archive.Entries.Select(e => e.FullName));
            Assert.Contains("versions.txt", archive.Entries.Select(e => e.FullName));
            var appEntry = archive.GetEntry("app.log")!;
            using var reader = new StreamReader(appEntry.Open());
            var content = reader.ReadToEnd();
            Assert.DoesNotContain("SUPERSECRET9", content);
            Assert.Contains("token=<redacted>", content);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }
}
