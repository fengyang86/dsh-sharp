namespace DSHSharp.Core.Compatibility;

/// <summary>DSH-Sharp 与 DSH 的版本契约。</summary>
public static class DshSharpCompatibility
{
    public const string ProductVersion = "0.2.0";
    public const string MinimumDshVersion = "0.1.0-rc.8";
    public const string MaximumDshVersionExclusive = "0.2.0";
    public const string SupportedRange = ">=0.1.0-rc.8 <0.2.0";
    public static readonly IReadOnlyList<string> VerifiedDshVersions = ["0.1.0-rc.8", "0.1.1-rc.2"];

    public static bool IsCompatible(string? version)
        => SemVersion.TryParse(version, out var parsed)
            && parsed.CompareTo(SemVersion.Parse(MinimumDshVersion)) >= 0
            && parsed.CompareTo(SemVersion.Parse(MaximumDshVersionExclusive)) < 0;

    private readonly record struct SemVersion(int Major, int Minor, int Patch, string? Pre)
        : IComparable<SemVersion>
    {
        public static SemVersion Parse(string value) => TryParse(value, out var result)
            ? result : throw new FormatException($"无效版本：{value}");

        public static bool TryParse(string? value, out SemVersion result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var parts = value.Trim().TrimStart('v').Split('-', 2);
            var numbers = parts[0].Split('.');
            if (numbers.Length < 3 || !int.TryParse(numbers[0], out var major) ||
                !int.TryParse(numbers[1], out var minor) || !int.TryParse(numbers[2], out var patch)) return false;
            result = new SemVersion(major, minor, patch, parts.Length == 2 ? parts[1] : null);
            return true;
        }

        public int CompareTo(SemVersion other)
        {
            var value = Major.CompareTo(other.Major);
            if (value != 0) return value;
            value = Minor.CompareTo(other.Minor);
            if (value != 0) return value;
            value = Patch.CompareTo(other.Patch);
            if (value != 0) return value;
            if (Pre is null) return other.Pre is null ? 0 : 1;
            if (other.Pre is null) return -1;
            return ComparePrerelease(Pre, other.Pre);
        }

        private static int ComparePrerelease(string left, string right)
        {
            var a = left.Split('.'); var b = right.Split('.');
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                if (i >= a.Length) return -1;
                if (i >= b.Length) return 1;
                var an = int.TryParse(a[i], out var ai); var bn = int.TryParse(b[i], out var bi);
                if (an && bn) { var c = ai.CompareTo(bi); if (c != 0) return c; }
                else if (an != bn) return an ? -1 : 1;
                else { var c = string.CompareOrdinal(a[i], b[i]); if (c != 0) return c; }
            }
            return 0;
        }
    }
}
