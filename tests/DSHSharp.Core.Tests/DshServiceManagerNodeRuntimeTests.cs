using System.Diagnostics;
using System.Reflection;
using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

/// <summary>内置 Node 激活后的子进程环境注入：全部 spawn 点必须统一走 ConfigureRuntimePath，
/// 保证主进程与孙进程（npm.cmd、DSH 派生进程）解析到同一个 Node，避免版本混用。
/// 与 NodeEnvironmentTests 同集合串行：后者会临时替换进程 PATH。</summary>
[Collection("PathEnvironment")]
public sealed class DshServiceManagerNodeRuntimeTests
{
    private static MethodInfo StaticConfigureRuntimePath() =>
        typeof(DshServiceManager).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "ConfigureRuntimePath" && m.GetParameters().Length == 4);

    [Fact]
    public void ConfigureRuntimePath_ManagedNodeFirstThenPrivateBins()
    {
        var startInfo = new ProcessStartInfo("node") { UseShellExecute = false };
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), "rt");
        var toolsDirectory = Path.Combine(runtimeDirectory, ".tools");
        var managedNodeDirectory = Path.Combine(Path.GetTempPath(), "managed-node");

        StaticConfigureRuntimePath().Invoke(null, [startInfo, runtimeDirectory, toolsDirectory, managedNodeDirectory]);

        var segments = startInfo.Environment["PATH"]!.Split(Path.PathSeparator);
        Assert.Equal(managedNodeDirectory, segments[0]);
        Assert.Equal(Path.Combine(toolsDirectory, "node_modules", ".bin"), segments[1]);
        Assert.Equal(Path.Combine(runtimeDirectory, "node_modules", ".bin"), segments[2]);
        // 宿主 PATH 完整保留在前三段之后（再按分隔符拼接应与原值一致）。
        Assert.Equal(
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            string.Join(Path.PathSeparator, segments[3..]));
    }

    [Fact]
    public void ConfigureRuntimePath_WithoutManagedNode_KeepsPrivateBinsFirst()
    {
        var startInfo = new ProcessStartInfo("node") { UseShellExecute = false };
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), "rt");
        var toolsDirectory = Path.Combine(runtimeDirectory, ".tools");

        StaticConfigureRuntimePath().Invoke(null, [startInfo, runtimeDirectory, toolsDirectory, null]);

        var segments = startInfo.Environment["PATH"]!.Split(Path.PathSeparator);
        Assert.Equal(Path.Combine(toolsDirectory, "node_modules", ".bin"), segments[0]);
        Assert.Equal(Path.Combine(runtimeDirectory, "node_modules", ".bin"), segments[1]);
    }

    [Fact]
    public void BuildStartInfo_WithManagedNode_UsesFullPathAndInjectsEnvironment()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "dshsharp-mgr-" + Path.GetRandomFileName());
        try
        {
            using var manager = new DshServiceManager("http://127.0.0.1:3080/", ManagedMode.None, null, dataDirectory);
            var managedNodeExe = Path.Combine(
                dataDirectory, "node-runtime", $"node-v{NodeRuntimeService.NodeVersion}-win-x64", "node.exe");
            typeof(DshServiceManager)
                .GetField("_managedNodeExe", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(manager, managedNodeExe);

            var buildStartInfo = typeof(DshServiceManager)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(m => m.Name == "BuildStartInfo" && m.GetParameters().Length == 1);
            var startInfo = (ProcessStartInfo)buildStartInfo.Invoke(manager, [3080])!;

            // FileName 必须是完整路径：Windows 上 CreateProcess 按父进程 PATH 解析 FileName，
            // 改子进程环境对 FileName 本身的解析无效。
            Assert.Equal(managedNodeExe, startInfo.FileName);
            var pathSegments = startInfo.Environment["PATH"]!.Split(Path.PathSeparator);
            Assert.Equal(Path.GetDirectoryName(managedNodeExe), pathSegments[0]);
            Assert.Equal(Path.Combine(dataDirectory, "dsh-home"), startInfo.Environment["DSH_HOME"]);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
