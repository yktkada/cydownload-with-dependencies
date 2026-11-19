using System.Globalization;

namespace CyDownload.Core.Repos;

/// <summary>
/// 源地址的规范化与名称推导（添加源时用）。
/// <para>
/// 用户只想粘一个地址（如 `apt.saurik.com`）就能加源：**协议前缀自动补**（默认 http），
/// **名称留空时自动用主机名**。规范化是纯函数，方便单测；界面只负责显示与错误提示。
/// </para>
/// </summary>
public static class SourceUrl
{
    private const string DefaultScheme = "http";

    /// <summary>
    /// 规范化用户输入的地址。
    /// <list type="bullet">
    /// <item>不带协议 → 补 `http://`；`//host` → 补 `http:`；</item>
    /// <item>只接受 http / https（老源基本都是 http，https 保留）；</item>
    /// <item>主机名小写；路径去尾部 `/`（`RepoDescriptor` 拼接时自己会 TrimEnd）；</item>
    /// <item>带查询/锚点的输入不合法（源地址不是网页链接）。</item>
    /// </list>
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = "";
        error = null;

        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "address is empty";
            return false;
        }

        if (text.StartsWith("//", StringComparison.Ordinal)) text = DefaultScheme + ":" + text;
        if (!text.Contains("://", StringComparison.Ordinal)) text = DefaultScheme + "://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "not a valid absolute URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            error = $"unsupported scheme '{uri.Scheme}' (only http/https)";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "missing host";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "source address must not contain query or fragment";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(
            uri.Scheme.ToLowerInvariant(),
            uri.Host.ToLowerInvariant(),
            uri.IsDefaultPort ? -1 : uri.Port)
        {
            Path = path,
            Query = "",
            Fragment = "",
        };

        normalized = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>
    /// 名称留空时从地址推导：主机名去掉开头的 `www.`。
    /// 主机名不可用（极端情况）时返回 null，让调用方报错。
    /// </summary>
    public static string? DeriveName(string normalizedUrl)
    {
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.Trim();
        if (host.Length == 0) return null;

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];

        return host.Length == 0 ? null : host;
    }

    /// <summary>名称里不允许空白（页签名/DB 主键都要稳）；把空白换成 `-`。</summary>
    public static string SanitizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "";

        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsWhiteSpace(ch) ? '-' : ch);
        }

        return builder.ToString();
    }

    /// <summary>名称重复时追加 `-2`、`-3`…（添加源避免撞主键）。</summary>
    public static string MakeUniqueName(string baseName, IReadOnlyCollection<string> existing)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(existing);

        if (!existing.Contains(baseName, StringComparer.Ordinal)) return baseName;

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = baseName + "-" + index.ToString(CultureInfo.InvariantCulture);
            if (!existing.Contains(candidate, StringComparer.Ordinal)) return candidate;
        }

        return baseName + "-" + Guid.NewGuid().ToString("N")[..8];
    }
}

