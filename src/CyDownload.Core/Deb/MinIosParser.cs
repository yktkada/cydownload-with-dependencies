using System.Globalization;
using System.Text.RegularExpressions;

namespace CyDownload.Core.Deb;

/// <summary>
/// 从依赖串里抽出「最低 iOS 版本」（`firmware (>= 7.0)` / `cy+os.ios (>= 5.1)`）。
/// 用于按 iOS 版本筛选插件：没有该依赖 = 不限制（返回 null）。
/// </summary>
public static partial class MinIosParser
{
    // firmware (>= 7.0) / firmware(>=7.0) / cy+os.ios (>= 5.1)
    [GeneratedRegex(@"(?:firmware|cy\+os\.ios)\s*\(\s*>=\s*([0-9]+(?:\.[0-9]+){0,2})\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirmwareConstraint();

    /// <summary>抽最低 iOS 版本；多个约束取最高。没有则返回 null。</summary>
    public static string? Parse(string? dependencyText)
    {
        if (string.IsNullOrWhiteSpace(dependencyText)) return null;

        string? highest = null;
        Version? highestVersion = null;

        foreach (Match match in FirmwareConstraint().Matches(dependencyText))
        {
            var text = match.Groups[1].Value;
            if (!Version.TryParse(text, out var parsed)) continue;

            if (highestVersion is null || parsed > highestVersion)
            {
                highestVersion = parsed;
                highest = text;
            }
        }

        return highest;
    }

    /// <summary>目标 iOS 是否满足包的最低版本要求（包没要求 → 满足）。</summary>
    public static bool IsCompatible(string? minIos, string? targetIos)
    {
        if (string.IsNullOrWhiteSpace(minIos)) return true;
        if (string.IsNullOrWhiteSpace(targetIos)) return true;

        if (!TryParseVersion(minIos, out var minimum)) return true;
        if (!TryParseVersion(targetIos, out var target)) return true;

        return target >= minimum;
    }

    /// <summary>按版本号比较（逐段补 0）；用于筛选项排序。</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0);

        var parts = text.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;

        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMinor)
            ? parsedMinor
            : 0;
        var build = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBuild)
            ? parsedBuild
            : 0;
        var revision = parts.Length > 3 && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRevision)
            ? parsedRevision
            : 0;

        version = new Version(major, minor, build, revision);
        return true;
    }

    /// <summary>显示用：把 `7.0` 之类的归一化文本原样返回（没有则空串）。</summary>
    public static string Format(string? minIos)
        => string.IsNullOrWhiteSpace(minIos) ? "" : minIos.Trim();

    internal static string ToInvariant(Version version)
        => string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");
}
