using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
using System.Text.Json;
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
    public async Task IsOnlineAsync_WhenExternalServerResponds_ReturnsFalse()
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

        Assert.False(ok, "私有 Runtime 管理器不得把外部服务视为自己的在线服务");
    }

    [Fact]
    public async Task IsOnlineAsync_WhenPortClosed_ReturnsFalse()
    {
        using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.None, null, Path.GetTempPath());

        Assert.False(await manager.IsOnlineAsync());
    }

    [Fact]
    public async Task StartAsync_WhenExternalServiceIsOnline_DoesNotReuseIt()
    {
        // 使用 None 验证外部服务不会改变单一私有 Runtime 的所有权策略。
        using var privateOnlyManager = new DshServiceManager(FakeServerUrl, ManagedMode.None, null, Path.GetTempPath());
        var ok = await privateOnlyManager.StartAsync();

        Assert.False(ok);
        Assert.Contains("仅支持私有", privateOnlyManager.LastError);
    }

    [Fact]
    public async Task StartAsync_WhenOfflineAndModeNone_ReturnsFalseWithError()
    {
        using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.None, null, Path.GetTempPath());

        var ok = await manager.StartAsync();

        Assert.False(ok);
        Assert.Contains("仅支持私有", manager.LastError);
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
        Assert.Contains("仅支持私有", manager.LastError);
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
            Assert.Contains("仅支持私有", manager.LastError);
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
            Assert.Contains("仅支持私有", manager.LastError);
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
    public void DshHomeDirectory_IsPrivateToManagerDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dshsharp-home-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.Npx, null, directory);

            Assert.Equal(Path.Combine(directory, "dsh-home"), manager.DshHomeDirectory);
            var legacyHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            Assert.False(string.Equals(legacyHome, manager.DshHomeDirectory, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IncompleteRuntimeDirectory_IsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json"), "{\"version\":\"1.0.0\"}");
        try
        {
            Assert.False(DshServiceManager.IsRuntimeDirectoryComplete(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RuntimeLayoutMigration_PreservesInstalledDshVersion()
    {
        var resolve = typeof(DshServiceManager).GetMethod(
            "ResolveRuntimeInstallVersion",
            BindingFlags.Static | BindingFlags.NonPublic);

        var version = (string)resolve!.Invoke(null, [null, "0.1.0-rc.8"])!;

        Assert.Equal("0.1.0-rc.8", version);
    }

    [Fact]
    public void ExplicitRuntimeUpdate_UsesRequestedDshVersion()
    {
        var resolve = typeof(DshServiceManager).GetMethod(
            "ResolveRuntimeInstallVersion",
            BindingFlags.Static | BindingFlags.NonPublic);

        var version = (string)resolve!.Invoke(null, ["0.1.1-rc.2", "0.1.0-rc.8"])!;

        Assert.Equal("0.1.1-rc.2", version);
    }

    [Fact]
    public void InterruptedDirectorySwap_IsRecoveredOnStartup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-recovery-test-" + Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(dir, "dsh-runtime-previous");
        Directory.CreateDirectory(Path.Combine(previous, "node_modules"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dsh-runtime-transaction.json"),
            JsonSerializer.Serialize(new { Phase = "ActiveMoved", TargetVersion = "1.0.0", StartedUtc = DateTimeOffset.UtcNow }));
        try
        {
            using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.Npx, null, dir);
            var recover = typeof(DshServiceManager).GetMethod("RecoverRuntimeUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            recover!.Invoke(manager, null);
            Assert.True(Directory.Exists(Path.Combine(dir, "dsh-runtime")));
            Assert.False(File.Exists(Path.Combine(dir, "dsh-runtime-transaction.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PreparedTransaction_WhenActiveWasMovedBeforePhasePersisted_RestoresPreviousRuntime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-prepared-recovery-test-" + Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(dir, "dsh-runtime-previous");
        var staging = Path.Combine(dir, "dsh-runtime-staging");
        Directory.CreateDirectory(previous);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(dir, "dsh-runtime-transaction.json"),
            JsonSerializer.Serialize(new { Phase = "Prepared", TargetVersion = "0.1.1-rc.2", StartedUtc = DateTimeOffset.UtcNow }));
        try
        {
            using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.Npx, null, dir);

            InvokeRuntimeRecovery(manager);

            Assert.True(Directory.Exists(Path.Combine(dir, "dsh-runtime")));
            Assert.False(Directory.Exists(previous));
            Assert.False(Directory.Exists(staging));
            Assert.False(File.Exists(Path.Combine(dir, "dsh-runtime-transaction.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ActiveMovedTransaction_WhenNewRuntimeWasAlreadyPromoted_PreservesRollbackRuntime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-active-moved-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "dsh-runtime"));
        Directory.CreateDirectory(Path.Combine(dir, "dsh-runtime-previous"));
        File.WriteAllText(Path.Combine(dir, "dsh-runtime-transaction.json"),
            JsonSerializer.Serialize(new { Phase = "ActiveMoved", TargetVersion = "0.1.1-rc.2", StartedUtc = DateTimeOffset.UtcNow }));
        try
        {
            using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.Npx, null, dir);

            InvokeRuntimeRecovery(manager);

            Assert.True(manager.CanRollbackRuntime);
            Assert.True(File.Exists(Path.Combine(dir, "dsh-runtime-transaction.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PromotedRuntimeTransaction_LeavesPreviousAvailableForRollback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshsharp-promoted-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "dsh-runtime"));
        Directory.CreateDirectory(Path.Combine(dir, "dsh-runtime-previous"));
        File.WriteAllText(Path.Combine(dir, "dsh-runtime-transaction.json"),
            JsonSerializer.Serialize(new { Phase = "Promoted", TargetVersion = "1.0.0", StartedUtc = DateTimeOffset.UtcNow }));
        try
        {
            using var manager = new DshServiceManager("http://127.0.0.1:1/", ManagedMode.Npx, null, dir);
            var recover = typeof(DshServiceManager).GetMethod("RecoverRuntimeUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            recover!.Invoke(manager, null);
            Assert.True(manager.CanRollbackRuntime);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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

    private static void InvokeRuntimeRecovery(DshServiceManager manager)
    {
        var recover = typeof(DshServiceManager).GetMethod("RecoverRuntimeUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        recover!.Invoke(manager, null);
    }

    public void Dispose()
    {
        _fakeServer.Stop();
        _fakeServer.Dispose();
    }
}
