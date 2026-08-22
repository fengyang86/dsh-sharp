using System.Net;
using System.Net.Sockets;
using System.Text;
using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

public sealed class DshServiceManagerTests : IDisposable
{
    private readonly TcpListener _fakeServer = new(IPAddress.Loopback, 0);

    public DshServiceManagerTests()
    {
        _fakeServer.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _fakeServer.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // 连接被瞬时重置等：继续接受，保证后续连接被处理。
                    continue;
                }
                catch (Exception)
                {
                    // 其他瞬时异常同样不退出监听循环。
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    using var c = client;
                    using var stream = c.GetStream();
                    var buffer = new byte[4096];
                    try
                    {
                        // 收到请求头首字节即可响应（仅需模拟"在线"）。
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, 1));
                        var response = Encoding.UTF8.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/html\r\n\r\nOK");
                        await stream.WriteAsync(response);
                    }
                    catch
                    {
                        // 客户端断开：忽略。
                    }
                });
            }
        });
    }

    private string FakeServerUrl =>
        $"http://127.0.0.1:{((IPEndPoint)_fakeServer.LocalEndpoint).Port}/";

    [Fact]
    public async Task IsOnlineAsync_WhenServerResponds_ReturnsTrue()
    {
        using var manager = new DshServiceManager(FakeServerUrl, ManagedMode.None, null, Path.GetTempPath());

        // 本地 fake server 偶发 socket 竞态（WSAECONNRESET 等）：轮询重试消除 flaky。
        var ok = false;
        for (var i = 0; i < 5 && !ok; i++)
        {
            ok = await manager.IsOnlineAsync();
            if (!ok)
            {
                await Task.Delay(200);
            }
        }

        Assert.True(ok, $"探测应成功（fake server 在线，端口 {((IPEndPoint)_fakeServer.LocalEndpoint).Port}）");
    }

    [Fact]
    public async Task IsOnlineAsync_WhenPortClosed_ReturnsFalse()
    {
        using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.None, null, Path.GetTempPath());

        Assert.False(await manager.IsOnlineAsync());
    }

    [Fact]
    public async Task StartAsync_WhenServiceOnline_ReturnsTrueWithoutSpawning()
    {
        using var manager = new DshServiceManager(FakeServerUrl, ManagedMode.Npx, null, Path.GetTempPath());

        var ok = await manager.StartAsync();

        Assert.True(ok);
        Assert.False(manager.IsOwned, "服务在线时不应拉起托管进程");
    }

    [Fact]
    public async Task StartAsync_WhenOfflineAndModeNone_ReturnsFalseWithError()
    {
        using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.None, null, Path.GetTempPath());

        var ok = await manager.StartAsync();

        Assert.False(ok);
        Assert.Contains("未托管", manager.LastError);
    }

    [Fact]
    public async Task StartAsync_WhenOfflineAndSourcePathInvalid_ReturnsFalseWithError()
    {
        using var manager = new DshServiceManager(
            "http://127.0.0.1:1/",
            ManagedMode.Source,
            @"Z:\definitely-not-a-dsh-repo",
            Path.GetTempPath());

        var ok = await manager.StartAsync();

        Assert.False(ok);
        Assert.NotNull(manager.LastError);
        Assert.Contains("不存在", manager.LastError);
    }

    [Fact]
    public async Task StartAsync_WhenOfflineAndSourcePathNotRepo_ReturnsFalseWithError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-src-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var manager = new DshServiceManager(
                "http://127.0.0.1:1/",
                ManagedMode.Source,
                dir,
                Path.GetTempPath());

            var ok = await manager.StartAsync();

            Assert.False(ok);
            Assert.Contains("package.json", manager.LastError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_WhenSourceDepsMissing_ReturnsInstallHint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-src-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), "{}");
        try
        {
            using var manager = new DshServiceManager(
                "http://127.0.0.1:1/",
                ManagedMode.Source,
                dir,
                Path.GetTempPath());

            var ok = await manager.StartAsync();

            Assert.False(ok);
            Assert.Contains("pnpm install", manager.LastError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InstalledPackageVersion_WhenPrivatePackageExists_ReturnsVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-package-test-" + Guid.NewGuid().ToString("N"));
        var packageDir = Path.Combine(dir, "dsh-runtime", "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"1.2.3\"}");
        try
        {
            using var manager = new DshServiceManager(
                "http://127.0.0.1:1/", ManagedMode.Npx, null, dir);

            Assert.Equal("1.2.3", manager.InstalledPackageVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsProfileDependencyCurrent_WhenLinkMatches_ReturnsTrue()
    {
        var manifest = WriteProfileManifest(
            "{\"dependencies\":{\"@yangfeng/dsh-sharp-session\":\"link:D:/apps/dsh-sharp-session\"}}");
        try
        {
            Assert.True(DshServiceManager.IsProfileDependencyCurrent(
                manifest,
                "@yangfeng/dsh-sharp-session",
                "link:D:/apps/dsh-sharp-session"));
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    [Theory]
    [InlineData("{\"dependencies\":{\"@yangfeng/dsh-sharp-session\":\"link:D:/old\"}}")]
    [InlineData("{\"dependencies\":{}}")]
    [InlineData("{not json")]
    public void IsProfileDependencyCurrent_WhenMissingStaleOrInvalid_ReturnsFalse(string json)
    {
        var manifest = WriteProfileManifest(json);
        try
        {
            Assert.False(DshServiceManager.IsProfileDependencyCurrent(
                manifest,
                "@yangfeng/dsh-sharp-session",
                "link:D:/apps/dsh-sharp-session"));
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    private static string WriteProfileManifest(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dshsharp-profile-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        _fakeServer.Stop();
        _fakeServer.Dispose();
    }
}
