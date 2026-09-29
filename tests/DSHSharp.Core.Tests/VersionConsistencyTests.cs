namespace DSHSharp.Core.Tests;

/// <summary>版本单源守卫：Directory.Build.props 的 &lt;Version&gt; 是全仓唯一版本号，
/// 必须与运行时经 AssemblyInformationalVersion 读出的 ProductVersion 一致
/// （csproj/常量双处手工同步曾漏改，v0.2.5 发布时 csproj 停在 0.2.4）。</summary>
public sealed class VersionConsistencyTests
{
    [Fact]
    public void ProductVersion_MatchesSingleSourceInDirectoryBuildProps()
    {
        var repoRoot = FindRepoRoot();
        var props = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(props), $"找不到 {props}");

        var content = File.ReadAllText(props);
        var version = Match(content, "<Version>([^<]+)</Version>");
        Assert.True(version is not null, "Directory.Build.props 缺少 <Version>");

        Assert.Equal(version, DSHSharp.Core.Compatibility.DshSharpCompatibility.ProductVersion);
    }

    [Fact]
    public void AssemblyVersion_DerivesFromProductVersion()
    {
        var expected = DSHSharp.Core.Compatibility.DshSharpCompatibility.ProductVersion + ".0";
        Assert.Equal(
            expected,
            typeof(DSHSharp.Core.Compatibility.DshSharpCompatibility).Assembly.GetName().Version?.ToString());
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

    /// <summary>内置 Node 版本必须满足私有 pnpm 的 engines 门槛（pnpm@11.25 要求 node ≥ 22.13，
    /// 低于该版本会在启动时抛 ERR_UNKNOWN_BUILTIN_MODULE）。调整任一版本而漏改另一个时，此测试显式失败。</summary>
    [Fact]
    public void ManagedNodeVersion_SatisfiesMinimumRequirement()
        => Assert.True(
            DSHSharp.Core.Dsh.NodeEnvironment.IsNodeVersionSupported(
                DSHSharp.Core.Dsh.NodeRuntimeService.NodeVersion,
                DSHSharp.Core.Dsh.DshServiceManager.MinimumNodeVersion),
            $"NodeRuntimeService.NodeVersion={DSHSharp.Core.Dsh.NodeRuntimeService.NodeVersion} " +
            $"低于 DshServiceManager.MinimumNodeVersion={DSHSharp.Core.Dsh.DshServiceManager.MinimumNodeVersion}");

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
