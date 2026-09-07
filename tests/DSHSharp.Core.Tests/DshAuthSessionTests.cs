using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

public sealed class DshAuthSessionTests
{
    [Fact]
    public void Extracts_Token_AndCleanOrigin_FromAuthenticatedUrl()
    {
        var session = new DshAuthSession("http://127.0.0.1:3081/?token=e5ob3bXU2eiu");

        Assert.Equal("http://127.0.0.1:3081/", session.Origin.AbsoluteUri);
        Assert.Equal("e5ob3bXU2eiu", session.Token);
    }

    [Fact]
    public async Task Handles_UrlWithoutToken_AsLegacyRuntime()
    {
        var session = new DshAuthSession("http://127.0.0.1:3080/");
        Assert.Null(session.Token);
        // 旧版 runtime 无认证层：直连视为可用。
        Assert.True(await session.EnsureAuthenticatedAsync());
        Assert.True(session.IsAuthenticated);
    }

    [Fact]
    public async Task Reset_Clears_AuthenticatedState()
    {
        var session = new DshAuthSession("http://127.0.0.1:3081/?token=x");
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Cookies);

        session.Reset();

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Cookies);
    }

    [Fact]
    public async Task ExchangeFailure_ReturnsFalse_WithoutThrowing()
    {
        // 保留端口（127.0.0.1:9）无服务监听：交换失败必须返回 false 而非抛出。
        var session = new DshAuthSession("http://127.0.0.1:9/?token=x");

        Assert.False(await session.EnsureAuthenticatedAsync());
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public void Extracts_Token_FromUrl_WithExtraQuery()
    {
        var session = new DshAuthSession("http://127.0.0.1:3081/?other=1&token=abc-def&x=2");

        Assert.Equal("abc-def", session.Token);
        Assert.Equal("http://127.0.0.1:3081/", session.Origin.AbsoluteUri);
    }

    [Fact]
    public void Origin_DropsFragment()
    {
        var session = new DshAuthSession("http://127.0.0.1:3081/?token=t#dshsharp-esc-stop=1");

        Assert.Equal("http://127.0.0.1:3081/", session.Origin.AbsoluteUri);
        Assert.Equal("t", session.Token);
    }
}
