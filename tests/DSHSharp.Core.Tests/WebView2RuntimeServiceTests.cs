using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

public sealed class WebView2RuntimeServiceTests
{
    [Fact]
    public void BootstrapperUrl_IsMicrosoftHttpsFwlink()
    {
        Assert.StartsWith("https://go.microsoft.com/", WebView2RuntimeService.BootstrapperUrl);
    }

    [Fact]
    public void RuntimeProductGuid_MatchesOfficialValue()
    {
        Assert.Equal("{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", WebView2RuntimeService.RuntimeProductGuid);
    }

    [Fact]
    public void DetectInstalled_DoesNotThrow()
    {
        // 结果取决于目标机器是否安装 WebView2，只验证探测本身可安全执行。
        _ = WebView2RuntimeService.DetectInstalled();
    }

    [Fact]
    public void BuildSignatureCheckCommand_QuotesAndEscapesPath()
    {
        var command = WebView2RuntimeService.BuildSignatureCheckCommand(@"C:\temp\it's.exe");
        Assert.StartsWith("(Get-AuthenticodeSignature -LiteralPath '", command);
        Assert.Contains(@"C:\temp\it''s.exe", command);
        Assert.EndsWith("').Status -eq 'Valid'", command);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("true\r\n", true)]
    [InlineData(" False", false)]
    [InlineData("NotSigned", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseSignatureCheckResult_OnlyAcceptsExplicitTrue(string? output, bool expected)
        => Assert.Equal(expected, WebView2RuntimeService.ParseSignatureCheckResult(output));
}
