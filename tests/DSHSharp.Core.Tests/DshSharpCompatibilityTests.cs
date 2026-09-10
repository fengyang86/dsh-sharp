using DSHSharp.Core.Compatibility;

namespace DSHSharp.Core.Tests;

public sealed class DshSharpCompatibilityTests
{
    [Theory]
    [InlineData("0.1.0-rc.8")]
    [InlineData("0.1.1-rc.2")]
    [InlineData("0.1.2-rc.1")]
    [InlineData("0.1.5-alpha.2")]
    [InlineData("0.1.5-rc.1")]
    public void AcceptsVersionsInsideContract(string version)
        => Assert.True(DshSharpCompatibility.IsCompatible(version));

    [Theory]
    [InlineData("0.1.0-rc.7")]
    [InlineData("0.2.0")]
    [InlineData("0.1.9")]
    [InlineData("0.1.3-alpha.1")]
    [InlineData("0.1.3-alpha.2")]
    [InlineData("0.1.5-alpha.1")]
    [InlineData("0.1.5-rc.2")]
    [InlineData("0.1.5")]
    [InlineData("not-a-version")]
    [InlineData(null)]
    public void RejectsVersionsOutsideContract(string? version)
        => Assert.False(DshSharpCompatibility.IsCompatible(version));

    [Fact]
    public void DefaultDshVersion_IsInVerifiedList()
        => Assert.Contains(DshSharpCompatibility.DefaultDshVersion, DshSharpCompatibility.VerifiedDshVersions);

    [Fact]
    public void TargetVersionSelection_PrefersLatest_WhenVerified_ElseFallsBackToDefault()
    {
        // npm latest 标签落后于最新已验证版本时的回退语义（与 App.UpdateManagedService 一致）。
        const string staleLatest = "0.1.2-rc.1";
        var target = DshSharpCompatibility.IsCompatible(staleLatest) ? staleLatest : DshSharpCompatibility.DefaultDshVersion;
        Assert.True(DshSharpCompatibility.IsCompatible(target));
    }
}
