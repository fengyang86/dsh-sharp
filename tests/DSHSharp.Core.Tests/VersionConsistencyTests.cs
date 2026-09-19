namespace DSHSharp.Core.Tests;

/// <summary>版本单源守卫：csproj 的版本三元组必须与 DshSharpCompatibility.ProductVersion 一致。
/// 双处手工同步曾漏改（v0.2.5 发布时 csproj 停在 0.2.4），此测试把不一致变成显式失败。</summary>
public sealed class VersionConsistencyTests
{
    [Fact]
    public void CsprojVersion_MatchesProductVersion()
    {
        var repoRoot = FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "DSHSharp", "DSHSharp.csproj");
        Assert.True(File.Exists(csproj), $"找不到 {csproj}");

        var content = File.ReadAllText(csproj);
        var version = Match(content, "<Version>([^<]+)</Version>");
        var assemblyVersion = Match(content, "<AssemblyVersion>([^<]+)</AssemblyVersion>");
        var fileVersion = Match(content, "<FileVersion>([^<]+)</FileVersion>");

        var expected = DSHSharp.Core.Compatibility.DshSharpCompatibility.ProductVersion;
        Assert.True(version is not null, "csproj 缺少 <Version>");
        Assert.Equal(expected, version);
        Assert.Equal($"{expected}.0", assemblyVersion);
        Assert.Equal($"{expected}.0", fileVersion);
    }

    [Fact]
    public void DownloadCandidates_PrimaryFirstThenMirrors()
    {
        var candidates = DSHSharp.Core.Services.ClientUpdateService.BuildDownloadCandidates(
            "https://github.com/fengyang86/dsh-sharp/releases/download/v0.3.0/x.zip");
        Assert.Equal(3, candidates.Count);
        Assert.Equal("https://github.com/fengyang86/dsh-sharp/releases/download/v0.3.0/x.zip", candidates[0]);
        Assert.StartsWith("https://ghproxy.net/https://github.com/", candidates[1]);
        Assert.StartsWith("https://gh-proxy.com/https://github.com/", candidates[2]);
    }

    private static string? Match(string content, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(content, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DSHSharp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
