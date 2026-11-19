using System.Text.Json;

namespace CyDownload.Core.Index;

/// <summary>`repos` 表的一行（界面上每个源页签的数据来源）。</summary>
public sealed record RepoRow(
    string Name,
    string BaseUrl,
    string Layout,
    string? Suite,
    string? Component,
    int Priority,
    string? HeadersJson,
    string? LastFetch,
    int? PackageCount,
    bool Enabled = true)
{
    public Repos.RepoLayout LayoutKind =>
        string.Equals(Layout, "flat", StringComparison.OrdinalIgnoreCase)
            ? Repos.RepoLayout.Flat
            : Repos.RepoLayout.Dists;

    /// <summary>还原成抓取用的源定义（含每源伪造头）。</summary>
    public Repos.RepoDescriptor ToDescriptor()
    {
        IReadOnlyDictionary<string, string>? headers = null;
        if (!string.IsNullOrWhiteSpace(HeadersJson))
        {
            try
            {
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(HeadersJson);
            }
            catch (JsonException)
            {
                headers = null;      // 坏 JSON 当没配，不要让界面打不开
            }
        }

        return new Repos.RepoDescriptor
        {
            Name = Name,
            BaseUrl = BaseUrl,
            Layout = LayoutKind,
            Suite = Suite,
            Component = Component,
            Priority = Priority,
            HeaderOverrides = headers,
            Enabled = Enabled,
        };
    }

    public override string ToString() => $"{Name} ({BaseUrl})";
}

