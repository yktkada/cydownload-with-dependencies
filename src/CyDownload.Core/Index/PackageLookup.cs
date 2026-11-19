using CyDownload.Core.Index;

namespace CyDownload.Core.Index;

/// <summary>
/// 包行的查找索引：**身份 = (包名, 版本, 架构, 源) 四元全等**。
/// <para>
/// 不能按「包名+版本」建 `ToDictionary`：同一个包同版本可能同时来自 saurik 与 aptgs，
/// 二元键会撞 `ArgumentException: An item with the same key has already been added`
/// （ 用户实测：资料库「导出这一组」直接失败）。
/// </para>
/// </summary>
public sealed class PackageLookup
{
    private readonly Dictionary<string, PackageRow> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackageRow> _relaxed = new(StringComparer.Ordinal);

    private PackageLookup()
    {
    }

    public static PackageLookup Build(IEnumerable<PackageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var lookup = new PackageLookup();
        foreach (var row in rows)
        {
            lookup._exact.TryAdd(Key(row.PackageId, row.Version, row.Architecture, row.Repo), row);
            // 宽松键只用于「按名+版本」找依赖行（同版本多源时取第一个）
            lookup._relaxed.TryAdd(row.PackageId + "\u0000" + row.Version, row);
        }

        return lookup;
    }

    /// <summary>精确四元查找；找不到时退回「包名+版本」（取任一源）。</summary>
    public PackageRow? Find(string packageId, string version, string architecture, string repo)
        => _exact.TryGetValue(Key(packageId, version, architecture, repo), out var row)
            ? row
            : _relaxed.TryGetValue(packageId + "\u0000" + version, out var relaxed)
                ? relaxed
                : null;

    public int Count => _exact.Count;

    private static string Key(string packageId, string version, string architecture, string repo)
        => packageId + "\u0000" + version + "\u0000" + architecture + "\u0000" + repo;
}
