using System.IO;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Export;
using CyDownload.Core.Index;

namespace CyDownload.App.Infrastructure;

/// <summary>
/// 资料库「导出这一组」的文件规划：组内成员 → pool 文件 + **拓扑序编号**（与主导出一致）。
/// <para>
/// 抽出来是为了可单测： 这里曾因「同包同版本来自两个源」用二元键建字典，
/// 抛 `ArgumentException: An item with the same key has already been added` 导致导出直接失败。
/// </para>
/// </summary>
public static class LibraryExportPlanner
{
    public static List<LibrarySourceFile> Build(
        IReadOnlyList<LibraryGroupItem> items,
        IReadOnlyList<LibraryPoolEntry> pool,
        PackageLookup lookup,
        string poolDirectory,
        ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentException.ThrowIfNullOrWhiteSpace(poolDirectory);

        var sink = log ?? NullLogSink.Instance;

        var poolByKey = new Dictionary<string, LibraryPoolEntry>(StringComparer.Ordinal);
        foreach (var entry in pool)
        {
            poolByKey.TryAdd(Key(entry.PackageId, entry.Version, entry.Architecture, entry.Repo), entry);
        }

        var members = items
            .OrderBy(item => item.Position)
            .ThenBy(item => item.PackageId, StringComparer.Ordinal)
            .ToList();

        // 拓扑序（依赖在前）：与主导出同口径
        var resolvable = members
            .Select(item => lookup.Find(item.PackageId, item.Version, item.Architecture, item.Repo))
            .Where(row => row is not null)
            .Cast<PackageRow>()
            .ToList();
        var topo = TopologicalSorter.Sort(resolvable);
        var topoIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < topo.Ordered.Count; i++) topoIndex[topo.Ordered[i].PackageId] = i;

        var files = new List<LibrarySourceFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;

        foreach (var item in members
                     .OrderBy(item => topoIndex.TryGetValue(item.PackageId, out var index) ? index : int.MaxValue)
                     .ThenBy(item => item.PackageId, StringComparer.Ordinal))
        {
            var key = Key(item.PackageId, item.Version, item.Architecture, item.Repo);
            if (!seen.Add(key)) continue;

            if (!poolByKey.TryGetValue(key, out var entry))
            {
                // 兜底：同源不同 repo 的台账行（改名前后）也可能命中
                entry = pool.FirstOrDefault(item2 =>
                    string.Equals(item2.PackageId, item.PackageId, StringComparison.Ordinal) &&
                    string.Equals(item2.Version, item.Version, StringComparison.Ordinal));
            }

            if (entry is null)
            {
                sink.Write($"[library] missing pool row: {item.PackageId} {item.Version}");
                continue;
            }

            files.Add(new LibrarySourceFile(
                Path.Combine(poolDirectory, entry.RelativePath),
                item.PackageId, item.Version, item.Architecture, order++));
        }

        return files;
    }

    private static string Key(string packageId, string version, string architecture, string repo)
        => packageId + "\u0000" + version + "\u0000" + architecture + "\u0000" + repo;
}
