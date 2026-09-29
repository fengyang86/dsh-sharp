using DSHSharp.Core.Dsh;

namespace DSHSharp.Core.Tests;

/// <summary>Runtime 完整性自检：安装时哈希清单 → 启动校验 → 失败触发重装自愈。</summary>
public sealed class RuntimeIntegrityTests
{
    private static string TempRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "dshsharp-integrity-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>反射调用私有 WriteRuntimeIntegrityManifest（安装路径内部细节）。</summary>
    private static void WriteManifest(string runtimeDirectory)
    {
        typeof(DshServiceManager)
            .GetMethod("WriteRuntimeIntegrityManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [runtimeDirectory]);
    }

    private static void WriteTrackedFile(string runtimeDirectory, string content = "ok")
    {
        var path = Path.Combine(
            runtimeDirectory, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void ManifestRoundtrip_IntactFiles_Pass()
    {
        var root = TempRuntime();
        try
        {
            WriteTrackedFile(root);
            WriteManifest(root);
            Assert.True(DshServiceManager.VerifyRuntimeIntegrity(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TamperedFile_FailsVerification()
    {
        var root = TempRuntime();
        try
        {
            WriteTrackedFile(root);
            WriteManifest(root);
            WriteTrackedFile(root, "tampered by antivirus");
            Assert.False(DshServiceManager.VerifyRuntimeIntegrity(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingTrackedFile_FailsVerification()
    {
        var root = TempRuntime();
        try
        {
            WriteTrackedFile(root);
            WriteManifest(root);
            File.Delete(Path.Combine(root, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"));
            Assert.False(DshServiceManager.VerifyRuntimeIntegrity(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingManifest_LegacyInstall_PassesWithoutReinstall()
    {
        var root = TempRuntime();
        try
        {
            WriteTrackedFile(root); // 旧版本安装：无清单文件 → 放行，不强迫存量用户重装。
            Assert.True(DshServiceManager.VerifyRuntimeIntegrity(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptManifest_PassesWithoutReinstall()
    {
        var root = TempRuntime();
        try
        {
            WriteTrackedFile(root);
            File.WriteAllText(Path.Combine(root, DshServiceManager.IntegrityManifestFileName), "not json");
            Assert.True(DshServiceManager.VerifyRuntimeIntegrity(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
