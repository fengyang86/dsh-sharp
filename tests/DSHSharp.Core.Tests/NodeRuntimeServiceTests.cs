using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

public sealed class NodeRuntimeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshsharp-node-runtime-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private NodeRuntimeService CreateService() => new(_root);

    [Fact]
    public void BuildDownloadCandidates_OfficialFirstThenMirror()
    {
        var candidates = NodeRuntimeService.BuildDownloadCandidates();
        Assert.Equal(2, candidates.Count);
        Assert.Equal($"https://nodejs.org/dist/v{NodeRuntimeService.NodeVersion}/{NodeRuntimeService.ZipFileName}", candidates[0]);
        Assert.Equal($"https://npmmirror.com/mirrors/node/v{NodeRuntimeService.NodeVersion}/{NodeRuntimeService.ZipFileName}", candidates[1]);
    }

    [Fact]
    public void ParseShasumsEntry_FindsMatchingLine()
    {
        var content = $"""
            aaaabbbbccccdddd0000111122223333444455556666777788889999aaaabbbb  node-v22.17.0-win-x64.zip
            1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff  {NodeRuntimeService.ZipFileName}
            99990000111122223333444455556666777788889999aaaabbbbccccddddeeee  node-v22.17.0-linux-x64.tar.xz
            """;
        Assert.Equal(
            "1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff",
            NodeRuntimeService.ParseShasumsEntry(content, NodeRuntimeService.ZipFileName));
    }

    [Fact]
    public void ParseShasumsEntry_ToleratesWindowsLineEndings()
    {
        var content = $"1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff  {NodeRuntimeService.ZipFileName}\r\n";
        Assert.NotNull(NodeRuntimeService.ParseShasumsEntry(content, NodeRuntimeService.ZipFileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short  node-v22.17.1-win-x64.zip")]
    [InlineData("1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff  other-file.zip")]
    [InlineData("1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff  NODE-V22.17.1-WIN-X64.ZIP")]
    public void ParseShasumsEntry_NoValidEntry_ReturnsNull(string content)
        => Assert.Null(NodeRuntimeService.ParseShasumsEntry(content, NodeRuntimeService.ZipFileName));

    [Fact]
    public void IsInstalled_RequiresMarkerVersionAndNodeExe()
    {
        var service = CreateService();
        Assert.False(service.IsInstalled);

        Directory.CreateDirectory(service.NodeDirectory);
        File.WriteAllText(Path.Combine(service.NodeDirectory, "node.exe"), "stub");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".node-version"), "22.17.0"); // 版本不匹配
        Assert.False(service.IsInstalled);

        File.WriteAllText(Path.Combine(_root, ".node-version"), NodeRuntimeService.NodeVersion);
        Assert.True(service.IsInstalled);
    }

    [Fact]
    public void Invalidate_RemovesMarkerSoNextEnsureReinstalls()
    {
        var service = CreateService();
        Directory.CreateDirectory(service.NodeDirectory);
        File.WriteAllText(Path.Combine(service.NodeDirectory, "node.exe"), "stub");
        File.WriteAllText(Path.Combine(_root, ".node-version"), NodeRuntimeService.NodeVersion);
        Assert.True(service.IsInstalled);

        service.Invalidate();
        Assert.False(service.IsInstalled);
    }
}
