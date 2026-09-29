using System.Text.Json.Nodes;
using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

/// <summary>失败路径三件套：日志轮转、失败信号分类、安全模式清单改写。</summary>
public sealed class FailureRecoveryTests
{
    // ---- 日志轮转 ----

    [Fact]
    public void RotateLogFile_OversizedLog_MovesToOld()
    {
        var path = Path.Combine(Path.GetTempPath(), "dshsharp-rot-" + Path.GetRandomFileName() + ".log");
        File.WriteAllText(path, new string('x', 100));
        try
        {
            DshServiceManager.RotateLogFile(path, 50);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".old"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".old");
        }
    }

    [Fact]
    public void RotateLogFile_UnderLimitOrMissing_NoChange()
    {
        var path = Path.Combine(Path.GetTempPath(), "dshsharp-rot-" + Path.GetRandomFileName() + ".log");
        File.WriteAllText(path, "small");
        try
        {
            DshServiceManager.RotateLogFile(path, 1024);
            Assert.True(File.Exists(path));

            var missing = path + ".missing";
            DshServiceManager.RotateLogFile(missing, 1); // 不抛异常。
            Assert.False(File.Exists(missing));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RotateLogFile_ReplacesPreviousGeneration()
    {
        var path = Path.Combine(Path.GetTempPath(), "dshsharp-rot-" + Path.GetRandomFileName() + ".log");
        File.WriteAllText(path, new string('x', 100));
        File.WriteAllText(path + ".old", "old generation");
        try
        {
            DshServiceManager.RotateLogFile(path, 50);
            Assert.False(File.Exists(path));
            var rotated = File.ReadAllText(path + ".old");
            Assert.NotEqual("old generation", rotated); // 旧一代被覆盖。
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".old");
        }
    }

    // ---- 失败信号分类 ----

    [Theory]
    [InlineData("Error: listen EADDRINUSE: address already in use 127.0.0.1:3080", "端口被占用")]
    [InlineData("write ENOSPC: no space left on device", "磁盘空间不足")]
    [InlineData("Error [ERR_UNKNOWN_BUILTIN_MODULE]: Cannot find module 'node:sqlite'", "Node 版本")]
    [InlineData("npm WARN ... unsupported engine", false)] // EBADENGINE 才是信号，普通 engine 警告不算
    [InlineData("ERR_PNPM_META_FETCH_FAIL GET https://registry.npmjs.org timeout", "网络")]
    public void ClassifyFailureHint_MapsSignalsToHints(string logText, object expectedFragmentOrFalse)
    {
        var hint = DshServiceManager.ClassifyFailureHint(logText);
        if (expectedFragmentOrFalse is false)
        {
            Assert.Null(hint);
        }
        else
        {
            Assert.NotNull(hint);
            Assert.Contains((string)expectedFragmentOrFalse, hint);
        }
    }

    [Fact]
    public void ClassifyFailureHint_UnknownFailure_ReturnsNull()
        => Assert.Null(DshServiceManager.ClassifyFailureHint("node-gyp rebuild failed with exit code 1"));

    // ---- 安全模式清单改写 ----

    private static string WriteManifest(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "dshsharp-safe-" + Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void DeactivateThirdPartyPlugins_RemovesOnlyThirdPartyBundles()
    {
        var path = WriteManifest("""
            {
              "dependencies": { "@yangfeng/dsh-sharp-session": "link:C:/x", "someone/else": "1.0.0" },
              "dsh": { "profile": { "bundles": ["someone/else", "@yangfeng/dsh-sharp-session"] } }
            }
            """);
        try
        {
            Assert.True(DshServiceManager.DeactivateThirdPartyPlugins(path));
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            var bundles = root["dsh"]!["profile"]!["bundles"]!;
            Assert.Single(bundles.AsArray());
            Assert.Equal("@yangfeng/dsh-sharp-session", bundles[0]!.GetValue<string>());
            // 依赖安装保留，只是取消激活。
            Assert.NotNull(root["dependencies"]!["someone/else"]);
            Assert.True(File.Exists(path + ".dshsharp-safe-mode.bak"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".dshsharp-safe-mode.bak");
        }
    }

    [Fact]
    public void DeactivateThirdPartyPlugins_NoThirdPartyActive_NoRewrite()
    {
        var json = """
            { "dsh": { "profile": { "bundles": ["@yangfeng/dsh-sharp-session"] } } }
            """;
        var path = WriteManifest(json);
        try
        {
            Assert.True(DshServiceManager.DeactivateThirdPartyPlugins(path));
            Assert.Equal(json, File.ReadAllText(path)); // 原样未动。
            Assert.False(File.Exists(path + ".dshsharp-safe-mode.bak"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("not json at all")]
    public void DeactivateThirdPartyPlugins_MissingOrMalformed_IsIdempotentOrSafe(string? content)
    {
        var path = Path.Combine(Path.GetTempPath(), "dshsharp-safe-" + Path.GetRandomFileName() + ".json");
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        try
        {
            // 缺失/无 bundles = 无事可做（true）；损坏 JSON = 明确失败（false），不半写。
            if (content is null || content == "{}")
            {
                Assert.True(DshServiceManager.DeactivateThirdPartyPlugins(path));
            }
            else
            {
                Assert.False(DshServiceManager.DeactivateThirdPartyPlugins(path));
                Assert.Equal(content, File.ReadAllText(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
