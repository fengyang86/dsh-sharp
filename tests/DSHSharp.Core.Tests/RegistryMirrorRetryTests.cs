using System.Diagnostics;
using System.Reflection;
using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

/// <summary>npmjs.org 直连失败时的镜像重试：只对网络类失败触发，且通过子进程环境变量注入。</summary>
public sealed class RegistryMirrorRetryTests
{
    [Theory]
    [InlineData("npm ERR! network request failed https://registry.npmjs.org/@deepseek-ai", true)]
    [InlineData("ERR_PNPM_META_FETCH_FAIL GET https://registry.npmjs.org/dsh response timeout", true)]
    [InlineData("Error: getaddrinfo ENOTFOUND registry.npmjs.org", true)]
    [InlineData("ERR_PNPM_... ETIMEDOUT", true)]
    [InlineData("RequestError: send request failed: fetch failed", true)]
    [InlineData("SELF_SIGNED_CERT_IN_CHAIN while reaching registry", true)]
    [InlineData("ERR_PNPM_PEER_DEP_RESOLUTION_FAILED", false)]
    [InlineData("node-gyp rebuild failed with exit code 1", false)]
    [InlineData("Invalid character found in package.json", false)]
    [InlineData("", false)]
    public void LooksLikeNetworkFailure_MatchesOnlyNetworkTokens(string text, bool expected)
        => Assert.Equal(expected, DshServiceManager.LooksLikeNetworkFailure(text));

    private static ProcessStartInfo InvokeInstallBuilder(bool useRegistryMirror)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "dshsharp-mirror-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dataDirectory);
        try
        {
            using var manager = new DshServiceManager("http://127.0.0.1:3080/", ManagedMode.None, null, dataDirectory);
            var builder = typeof(DshServiceManager)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(m => m.Name == "BuildDshInstallStartInfo" && m.GetParameters().Length == 4);
            return (ProcessStartInfo)builder.Invoke(manager,
                [Path.Combine(dataDirectory, "rt"), Path.Combine(dataDirectory, "rt", ".tools"), "0.1.6-alpha.2", useRegistryMirror])!;
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void BuildDshInstallStartInfo_WithMirror_InjectsRegistryEnv()
    {
        var startInfo = InvokeInstallBuilder(useRegistryMirror: true);
        Assert.Equal(DshServiceManager.RegistryMirrorUrl, startInfo.Environment["npm_config_registry"]);
    }

    [Fact]
    public void BuildDshInstallStartInfo_WithoutMirror_LeavesRegistryUnset()
    {
        var startInfo = InvokeInstallBuilder(useRegistryMirror: false);
        Assert.False(startInfo.Environment.ContainsKey("npm_config_registry"));
    }

    [Theory]
    [InlineData("ERR_PNPM_ERESOLVE unable to resolve dependency tree fetch failed", true)]
    [InlineData("ERESOLVE unable to resolve dependency tree", false)]
    [InlineData("fetch failed getaddrinfo ENOTFOUND", false)]
    [InlineData("ERR_PNPM_PEER_DEP_RESOLUTION", false)]
    public void LooksLikeRegistrySyncLag_RequiresEresolvePlusFailureSignal(string text, bool expected)
        => Assert.Equal(expected, DshServiceManager.LooksLikeRegistrySyncLag(text));

    [Fact]
    public void RegistryMirrorPreference_MarkerLifecycle()
    {
        var path = Path.Combine(Path.GetTempPath(), "dshsharp-mirror-" + Path.GetRandomFileName());
        try
        {
            Assert.False(DshServiceManager.IsRegistryMirrorPreferred(path)); // 默认无偏好。
            DshServiceManager.SetRegistryMirrorPreference(path);             // 镜像成功后记住。
            Assert.True(DshServiceManager.IsRegistryMirrorPreferred(path));
            DshServiceManager.ClearRegistryMirrorPreference(path);           // 环境变化后自愈。
            Assert.False(DshServiceManager.IsRegistryMirrorPreferred(path));
            DshServiceManager.ClearRegistryMirrorPreference(path);           // 清除幂等。
            Assert.False(DshServiceManager.IsRegistryMirrorPreferred(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
