using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DSHSharp.Core.Services;

/// <summary>
/// 用户数据维护：一键备份（会话与配置，排除凭据与派生物）与诊断包导出（日志尾部，token 脱敏）。
/// </summary>
public static partial class UserDataMaintenance
{
    /// <summary>备份与诊断包的默认输出目录（文档库下）。</summary>
    public static string DefaultDestinationDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    private static readonly string[] ProfileTopLevelExtensions = [".json", ".yml", ".yaml"];

    [GeneratedRegex(@"token=[A-Za-z0-9_\-]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"dsh-auth-[A-Za-z0-9_\-]+=[^;\s']+")]

    private static partial Regex CookiePattern();

    /// <summary>把输出中的访问令牌与认证 Cookie 值替换为占位符（诊断包可安全外发）。</summary>
    public static string RedactTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = TokenPattern().Replace(text, "token=<redacted>");
        return CookiePattern().Replace(text, m => m.Value.Split('=')[0] + "=<redacted>");
    }

    /// <summary>
    /// 计算备份应包含的文件（相对 appDataDir）：
    /// dsh-home 的 sessions/storages/skills 全量、顶层配置文件、profiles 顶层配置；
    /// 排除凭据文件（.credentials.yaml 含签名密钥）、profiles 内 node_modules 派生物与日志。
    /// </summary>
    internal static IReadOnlyList<string> SelectBackupEntries(string appDataDir)
    {
        var entries = new List<string>();
        var dshHome = Path.Combine(appDataDir, "dsh-home");
        if (!Directory.Exists(dshHome))
        {
            return entries;
        }

        foreach (var dir in new[] { "sessions", "storages", "skills" })
        {
            var full = Path.Combine(dshHome, dir);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                entries.Add(Path.GetRelativePath(appDataDir, file).Replace('\\', '/'));
            }
        }

        foreach (var file in Directory.EnumerateFiles(dshHome, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".credentials.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(Path.GetRelativePath(appDataDir, file).Replace('\\', '/'));
        }

        var profiles = Path.Combine(dshHome, "profiles");
        if (Directory.Exists(profiles))
        {
            foreach (var profile in Directory.EnumerateDirectories(profiles))
            {
                foreach (var file in Directory.EnumerateFiles(profile, "*", SearchOption.TopDirectoryOnly))
                {
                    if (ProfileTopLevelExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        entries.Add(Path.GetRelativePath(appDataDir, file).Replace('\\', '/'));
                    }
                }
            }
        }

        // 客户端自身设置（无敏感内容）。
        var settings = Path.Combine(appDataDir, "settings.json");
        if (File.Exists(settings))
        {
            entries.Add("settings.json");
        }

        return entries;
    }

    /// <summary>创建会话数据备份 zip（Documents\DSHSharp-data-backup-时间戳.zip），返回 zip 路径。</summary>
    public static string CreateBackupZip(string appDataDir, string? destinationDir = null)
    {
        var dir = destinationDir ?? DefaultDestinationDir;
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"DSHSharp-data-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var entries = SelectBackupEntries(appDataDir);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("没有可备份的数据（dsh-home 不存在或为空）");
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var relative in entries)
            {
                archive.CreateEntryFromFile(
                    Path.Combine(appDataDir, relative),
                    relative.Replace('\\', '/'),
                    CompressionLevel.Optimal);
            }
        }

        return zipPath;
    }

    /// <summary>
    /// 创建诊断包 zip（Documents\DSHSharp-diagnostics-时间戳.zip）：
    /// app.log / dsh-service.log 尾部（脱敏）+ 调用方附加的文本件（版本清单、更新状态等）。
    /// </summary>
    public static string CreateDiagnosticsZip(
        string appDataDir,
        IReadOnlyDictionary<string, string> extraTextFiles,
        string? destinationDir = null,
        int logTailLines = 1500)
    {
        var dir = destinationDir ?? DefaultDestinationDir;
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"DSHSharp-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, lines) in new[]
                 {
                     ("app.log", ReadLogTail(Path.Combine(appDataDir, "app.log"), logTailLines)),
                     ("dsh-service.log", ReadLogTail(Path.Combine(appDataDir, "dsh-service.log"), logTailLines)),
                 })
        {
            if (lines is not null)
            {
                WriteEntry(archive, name, lines);
            }
        }

        foreach (var (name, content) in extraTextFiles)
        {
            WriteEntry(archive, name, content);
        }

        return zipPath;
    }

    private static string? ReadLogTail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var queue = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                if (queue.Count > maxLines)
                {
                    queue.Dequeue();
                }
            }

            return string.Join(Environment.NewLine, queue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（读取失败：{ex.Message}）";
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(RedactTokens(content));
    }
}
