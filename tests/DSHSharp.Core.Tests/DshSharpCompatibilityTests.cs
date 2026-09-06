using DSHSharp.Core.Compatibility;

namespace DSHSharp.Core.Tests;

public sealed class DshSharpCompatibilityTests
{
    [Theory]
    [InlineData("0.1.0-rc.8")]
    [InlineData("0.1.1-rc.2")]
    [InlineData("0.1.2-rc.1")]
    public void AcceptsVersionsInsideContract(string version)
        => Assert.True(DshSharpCompatibility.IsCompatible(version));

    [Theory]
    [InlineData("0.1.0-rc.7")]
    [InlineData("0.2.0")]
    [InlineData("0.1.9")]
    [InlineData("0.1.3-alpha.1")]
    [InlineData("not-a-version")]
    public void RejectsVersionsOutsideContract(string version)
        => Assert.False(DshSharpCompatibility.IsCompatible(version));
}
