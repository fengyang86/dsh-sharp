using System.Reflection;

namespace DSHSharp.Core.Compatibility;

/// <summary>DSH-Sharp 与 DSH 的版本契约。</summary>
public static class DshSharpCompatibility
{
    /// <summary>客户端产品版本。单源于 Directory.Build.props 的 &lt;Version&gt;，
    /// 经 AssemblyInformationalVersion 读取（剥离 SourceRevisionId 后缀）；
    /// 发版只改 Directory.Build.props 一处。</summary>
    public static string ProductVersion { get; } = ReadProductVersion();

    private static string ReadProductVersion()
    {
        var informational = typeof(DshSharpCompatibility).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        // InformationalVersion 形如 "0.2.7+<commit>"：取 + 前的产品版本部分。
        return informational is { Length: > 0 } value ? value.Split('+')[0] : "0.0.0";
    }

    /// <summary>默认安装的 DSH 版本（始终为白名单内最新已验证版本；跟随维护节奏更新）。</summary>
    public const string DefaultDshVersion = "0.1.7-rc.1";
    public const string SupportedRange = "verified: 0.1.0-rc.8, 0.1.1-rc.2, 0.1.2-rc.1, 0.1.5-alpha.2, 0.1.5-rc.1, 0.1.5-rc.2, 0.1.6-alpha.2, 0.1.7-alpha.2, 0.1.7-rc.1";
    public static readonly IReadOnlyList<string> VerifiedDshVersions = ["0.1.0-rc.8", "0.1.1-rc.2", "0.1.2-rc.1", "0.1.5-alpha.2", "0.1.5-rc.1", "0.1.5-rc.2", "0.1.6-alpha.2", "0.1.7-alpha.2", "0.1.7-rc.1"];

    public static bool IsCompatible(string? version)
        => SemVersion.TryParse(version, out var parsed)
            && VerifiedDshVersions.Any(item => SemVersion.Parse(item).CompareTo(parsed) == 0);
}
