using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

/// <summary>与 DshServiceManagerNodeRuntimeTests 同集合串行：ResolveOnPath 会临时替换进程 PATH，
/// 并行执行会污染对方的 PATH 断言。</summary>
[Collection("PathEnvironment")]
public sealed class NodeEnvironmentTests
{
    [Theory]
    [InlineData("v22.13.0", true)]
    [InlineData("22.13.0", true)]
    [InlineData("22.14.1", true)]
    [InlineData("24.0.0", true)]
    [InlineData("22.12.9", false)]
    [InlineData("20.13.1", false)]  // 截图故障机的版本：pnpm 11.25 静态依赖 node:sqlite
    [InlineData("22.13.0-rc.1", false)] // prerelease 不算达标（与 pnpm engines 语义一致）
    [InlineData("", false)]
    [InlineData("not-a-version", false)]
    public void IsNodeVersionSupported_ChecksGateAgainstMinimum(string version, bool expected)
        => Assert.Equal(expected, NodeEnvironment.IsNodeVersionSupported(version, "22.13.0"));

    [Fact]
    public void IsNodeVersionSupported_NullVersion_ReturnsFalse()
        => Assert.False(NodeEnvironment.IsNodeVersionSupported(null, "22.13"));

    [Fact]
    public void ResolveOnPath_FindsExecutableOnPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dshsharp-preflight-" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var toolName = OperatingSystem.IsWindows() ? "dshsharp-test-tool.exe" : "dshsharp-test-tool";
            var toolPath = Path.Combine(directory, toolName);
            File.WriteAllText(toolPath, "stub");

            var original = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", directory);
            try
            {
                var resolved = NodeEnvironment.ResolveOnPath("dshsharp-test-tool");
                // Windows 上 PATHEXT 命中 .EXE 大写形态，文件系统大小写不敏感：只比较路径本身。
                Assert.True(
                    resolved is not null && string.Equals(resolved, toolPath, StringComparison.OrdinalIgnoreCase),
                    $"expected {toolPath}, got {resolved}");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", original);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveOnPath_MissingExecutable_ReturnsNull()
        => Assert.Null(NodeEnvironment.ResolveOnPath("dshsharp-definitely-missing-tool-42"));

    [Fact]
    public async Task RunNodeVersionAsync_MissingExecutable_ReturnsNull()
        => Assert.Null(await NodeEnvironment.RunNodeVersionAsync(
            Path.Combine(Path.GetTempPath(), "dshsharp-missing-node-42.exe")));
}
