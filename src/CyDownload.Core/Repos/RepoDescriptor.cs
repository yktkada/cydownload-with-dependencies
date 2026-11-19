namespace CyDownload.Core.Repos;

/// <summary>源布局。dists（saurik / bigboss）与 flat（apt.gs）两种。</summary>
public enum RepoLayout
{
    /// <summary>`dists/&lt;suite&gt;/&lt;component&gt;/binary-&lt;arch&gt;/Packages.*`</summary>
    Dists,

    /// <summary>`&lt;base&gt;/Packages.*`（apt.gs）</summary>
    Flat,
}

/// <summary>一个候选索引地址 + 它在 Release 校验表里的相对路径。</summary>
public sealed record IndexCandidate(string Url, string RelativePath, string Suffix);

/// <summary>
/// 源定义。**不写死任何 suite**：saurik 用 `ios/&lt;cf_version&gt;`，bigboss 用 `stable`。
/// </summary>
public sealed record RepoDescriptor
{
    /// <summary>显示名 / 主键，如 `saurik`。</summary>
    public required string Name { get; init; }

    /// <summary>源根地址，可带可不带结尾斜杠。</summary>
    public required string BaseUrl { get; init; }

    public RepoLayout Layout { get; init; } = RepoLayout.Dists;

    /// <summary>dists 布局必填；flat 布局忽略。</summary>
    public string? Suite { get; init; }

    public string? Component { get; init; }

    /// <summary>索引架构目录名，如 `iphoneos-arm`。</summary>
    public string Architecture { get; init; } = "iphoneos-arm";

    /// <summary>同版本时选谁（数值小优先），saurik=1 / bigboss=2 / aptgs=3。</summary>
    public int Priority { get; init; } = 100;

    /// <summary>是否参与「全部」页签的批量抓取（禁用 = 保留配置但跳过；单源页签仍可手动抓）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>该源覆盖的伪造头（null = 用全局）。</summary>
    public IReadOnlyDictionary<string, string>? HeaderOverrides { get; init; }

    /// <summary>
    /// 索引候选，按**实测的最优顺序**排：`.bz2` → `.gz` → 明文。
    /// <para>
    /// 为什么不试 `.xz` / `.zst`： 实测三源全部 404（见 ）。
    /// 真出现了也不怕——判断靠魔数，不靠扩展名。
    /// </para>
    /// </summary>
    public IReadOnlyList<IndexCandidate> BuildIndexCandidates()
    {
        var root = BaseUrl.TrimEnd('/');

        // 目录前缀（URL 用）与**相对路径**（Release 校验表用）不是一回事：
        // dists 布局的 Release 在 dists/<suite>/Release，表里的路径形如
        // "main/binary-iphoneos-arm/Packages.bz2"（实测 bigboss），不含 dists/<suite> 前缀。
        var urlPrefix = Layout switch
        {
            RepoLayout.Dists => $"dists/{Require(Suite, nameof(Suite))}/{Require(Component, nameof(Component))}/binary-{Architecture}",
            RepoLayout.Flat => "",
            _ => throw new ArgumentOutOfRangeException(nameof(Layout), Layout, "Unknown repo layout."),
        };

        var relativePrefix = Layout switch
        {
            RepoLayout.Dists => $"{Require(Component, nameof(Component))}/binary-{Architecture}",
            RepoLayout.Flat => "",
            _ => throw new ArgumentOutOfRangeException(nameof(Layout), Layout, "Unknown repo layout."),
        };

        var urlStem = urlPrefix.Length == 0 ? root : $"{root}/{urlPrefix}";

        var candidates = new List<IndexCandidate>(3);
        foreach (var suffix in (string[])[".bz2", ".gz", ""])
        {
            candidates.Add(new IndexCandidate(
                urlStem + "/Packages" + suffix,
                (relativePrefix.Length == 0 ? "" : relativePrefix + "/") + "Packages" + suffix,
                suffix.Length == 0 ? "plain" : suffix[1..]));
        }

        return candidates;
    }

    /// <summary>Release 地址（bigboss / apt.gs 都有，实测 1007 B / 325 B）。</summary>
    public string BuildReleaseUrl()
    {
        var root = BaseUrl.TrimEnd('/');
        return Layout switch
        {
            RepoLayout.Dists => $"{root}/dists/{Require(Suite, nameof(Suite))}/Release",
            RepoLayout.Flat => $"{root}/Release",
            _ => throw new ArgumentOutOfRangeException(nameof(Layout), Layout, "Unknown repo layout."),
        };
    }

    private static string Require(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{propertyName} is required for dists layout.");
        }

        return value;
    }

    public override string ToString() => $"{Name} ({BaseUrl})";
}

