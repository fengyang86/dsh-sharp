using System.Diagnostics;
using DSHSharp.Core.Compatibility;

namespace DSHSharp.Core.Dsh;

/// <summary>
/// 启动前的 Node 环境预检原语：PATH 定位、版本探测、版本门槛判断。
/// 由 DshServiceManager 组合成完整预检（内置 Node 优先 → 系统 Node 达标 → 自动下载修复）。
/// </summary>
public static class NodeEnvironment
{
    /// <summary>node --version 探测超时：WindowsApps 的假 node.exe 会超时/报错，按不可用处理。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>版本门槛：≥ minimum（prerelease 不算达标，与 pnpm engines 语义一致）。</summary>
    public static bool IsNodeVersionSupported(string? version, string minimum) =>
        SemVersion.TryParse(version, out var parsed) &&
        SemVersion.TryParse(minimum, out var min) &&
        parsed.CompareTo(min) >= 0;

    /// <summary>
    /// 模拟 shell 的 PATH 搜索：Windows 上依次尝试 PATHEXT 扩展名（node → node.EXE/node.CMD/…），
    /// 其他平台按原名匹配。返回可执行文件完整路径；找不到返回 null。仅做文件存在性检查，不执行。
    /// </summary>
    public static string? ResolveOnPath(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName) ? fileName : null;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, fileName + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // PATH 中存在非法路径段（含非法字符等）：跳过。
                }
            }
        }

        return null;
    }

    /// <summary>执行 <c>node --version</c>；超时、启动失败或输出无法解析为版本时返回 null。</summary>
    public static async Task<string?> RunNodeVersionAsync(string executablePath, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var version = output.Split('\n', 2)[0].Trim();
            return SemVersion.TryParse(version, out _) ? version : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
