using DSHSharp.Core.Services;

namespace DSHSharp.Core.Tests;

public sealed class ExternalLinkPolicyTests
{
    private static readonly Uri RuntimeOrigin = new("http://127.0.0.1:3080/");

    [Theory]
    [InlineData("http://127.0.0.1:3080/?token=x", false)]
    [InlineData("http://127.0.0.1:3080/#dsh-session=abc", false)]
    [InlineData("http://localhost:3080/some/path", false)]
    [InlineData("http://[::1]:3080/", false)]
    [InlineData("about:blank", false)]
    [InlineData("data:text/html,<b>hi</b>", false)]
    [InlineData("blob:http://127.0.0.1:3080/uuid", false)]
    public void WebUiAndInternalTargets_StayInWebView(string url, bool expected)
        => Assert.Equal(expected, ExternalLinkPolicy.ShouldOpenExternally(new Uri(url), RuntimeOrigin));

    [Theory]
    [InlineData("https://github.com/deepseek-ai/deepseek-harness")]
    [InlineData("http://example.com/")]
    [InlineData("https://127.0.0.1:9999/other-local-service")]
    [InlineData("http://192.168.1.5:3080/same-port-lan")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("vscode://file/d:/repo")]
    public void ExternalTargets_GoToSystem(string url)
        => Assert.True(ExternalLinkPolicy.ShouldOpenExternally(new Uri(url), RuntimeOrigin));

    [Fact]
    public void UnknownRuntimeOrigin_LetsEverythingPassInternally()
    {
        Assert.False(ExternalLinkPolicy.ShouldOpenExternally(new Uri("https://github.com/"), null));
        Assert.False(ExternalLinkPolicy.ShouldOpenExternally(null, RuntimeOrigin));
    }
}
