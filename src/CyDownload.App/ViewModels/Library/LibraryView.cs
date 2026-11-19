// carried from IdeviceGui ce8419424ead298532195cdfca2c7e0435890323 ViewModels/Library/LibraryView.cs
// 只搬「分组 + 展开/收起 + 聚合 + 搜索过滤 + 排序」这套行为；IPA / 兼容性 / 图标 / 设备族那部分不适用，未搬。

using System.Globalization;
using CyDownload.App.Localization;
using CyDownload.Core.Index;

namespace CyDownload.App.ViewModels.Library;

/// <summary>资料库排序字段（组按聚合值排，成员按包名排）。</summary>
internal enum LibrarySortField
{
    Size,
    Name,
    Time,
}

internal sealed record LibrarySort(LibrarySortField Field, bool Descending);

/// <summary>
/// 资料库一行：**组头**（一个插件）或**成员**（闭包里的一个 deb）。
/// 与 `IdeviceInstaller` 的 `LibraryRow` 同名同角色，但字段只保留本程序有的。
/// </summary>
internal sealed class LibraryRowItem
{
    /// <summary>组行的 id（负数是组，与 pool 行区分；分组落库用正数 id）。</summary>
    public long Id { get; init; }

    public bool IsGroup { get; init; }

    /// <summary>合并显示时这一行代表的**全部**底层 group id（导出/删除按并集走，）。</summary>
    public IReadOnlyList<long> GroupIds { get; init; } = [];

    public long GroupId { get; init; }

    public bool IsCollapsed { get; set; }

    public int GroupCount { get; set; }

    /// <summary>这一行是「展开后的组内成员」。</summary>
    public bool IsGroupMember { get; set; }

    /// <summary>组内最后一个成员（用来画分隔线 / 收尾背景）。</summary>
    public bool IsGroupEnd { get; set; }

    public string PackageId { get; init; } = "";

    public string NameText { get; init; } = "";

    public string VersionText { get; init; } = "";

    public string RepoText { get; init; } = "";

    public string SizeText { get; init; } = "";

    public long Bytes { get; init; }

    public string RelativePath { get; init; } = "";

    /// <summary>组行的「N 个」；成员行空。</summary>
    public string GroupText => IsGroup ? Loc.Format("Library.GroupCount", GroupCount) : "";

    /// <summary>展开箭头（仅组行显示）。</summary>
    public string Glyph => !IsGroup || GroupCount <= 1 ? "" : IsCollapsed ? "▶" : "▼";

    /// <summary>成员行缩进（组行不缩进）。</summary>
    public System.Windows.Thickness IndentMargin => IsGroupMember
        ? new System.Windows.Thickness(18, 0, 0, 0)
        : new System.Windows.Thickness(0);

    public System.Windows.FontWeight FontWeight => IsGroup
        ? System.Windows.FontWeights.SemiBold
        : System.Windows.FontWeights.Normal;

    /// <summary>组的根插件包名（成员行为空）。</summary>
    public string RootPackage { get; init; } = "";

    public string CreatedAtText { get; init; } = "";

    public long CreatedAtTicks { get; init; }

    public string SearchText { get; init; } = "";

    public override string ToString() => $"{NameText} {VersionText}";
}

/// <summary>
/// 资料库视图逻辑（纯逻辑，无 WPF 依赖；分组行为对齐 `iDeviceInstaller` 的 `LibraryView`）：
/// <list type="number">
/// <item>**一次导出 = 一个 run；每个根插件一组**，组里是它的整个依赖闭包（同一 deb 可出现在多个组里）；</item>
/// <item>组**默认收起**，点组头展开；组头显示成员数与体积合计；</item>
/// <item>搜索时组与成员都参与匹配，命中成员的组只列出命中的成员；</item>
/// <item>组按聚合值排序（体积 / 时间）或根包名排序，成员按「根在前、包名升序」；</item>
/// <item>不属于任何组的 deb（单独下过、或组已删）作为独立行列出——**下载过的东西都算资料库**。</item>
/// </list>
/// </summary>
internal sealed class LibraryView
{
    private readonly List<LibraryPoolEntry> _pool = [];
    private readonly List<LibraryGroup> _groups = [];
    private readonly HashSet<long> _collapsed = [];

    private string _searchText = "";
    private LibrarySort _sort = new(LibrarySortField.Time, Descending: true);

    public IReadOnlyList<LibraryRowItem> Rows { get; private set; } = [];

    /// <summary>
    /// 只显示这些根插件（用户分组）；null / 空 = 全部。由 MainViewModel 在切换分组时下发。
    /// </summary>
    public IReadOnlySet<string>? AllowedRoots { get; set; }

    /// <summary>
    /// 是否把「同一插件」的多次下载/导出合并成一行（对齐参考项目 iDeviceGui 的 merge same bundle 开关）。
    /// 关掉后每次 run 各占一行，便于查看历史差异。
    /// </summary>
    public bool MergeSameGroups { get; set; } = true;

    public string SearchText
    {
        get => _searchText;
        set => _searchText = value ?? "";
    }

    public LibrarySort Sort
    {
        get => _sort;
        set => _sort = value;
    }

    /// <summary>最近一次 Build 的统计（状态栏用）。</summary>
    public (int Files, int Groups) LastCounts { get; private set; }

    /// <summary>整体替换数据（导出后 / 启动时）。新组的收起状态默认为收起。</summary>
    public void ReplaceAll(IReadOnlyList<LibraryPoolEntry> pool, IReadOnlyList<LibraryGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(groups);

        _pool.Clear();
        _pool.AddRange(pool);

        _groups.Clear();
        _groups.AddRange(groups);

        var known = _groups.Select(group => group.Id).ToHashSet();
        _collapsed.RemoveWhere(id => !known.Contains(id));

        // 新组默认收起（与 IdeviceInstaller 的资料库一致）
        foreach (var group in _groups)
        {
            if (!_collapsed.Contains(group.Id) && !_knownGroups.Contains(group.Id))
            {
                _knownGroups.Add(group.Id);
                _collapsed.Add(group.Id);
            }
        }
    }

    private readonly HashSet<long> _knownGroups = [];

    public bool ToggleGroup(long groupId)
    {
        if (!_collapsed.Remove(groupId)) _collapsed.Add(groupId);
        return _collapsed.Contains(groupId);
    }

    public void ExpandAll()
    {
        _collapsed.Clear();
        foreach (var group in _groups) _knownGroups.Add(group.Id);
    }

    public void CollapseAll()
    {
        foreach (var group in _groups)
        {
            _knownGroups.Add(group.Id);
            _collapsed.Add(group.Id);
        }
    }

    public bool IsCollapsed(long groupId) => _collapsed.Contains(groupId);

    /// <summary>重新算出行序列（组头 + 成员 + 独立 deb），供界面整表替换。</summary>
    public IReadOnlyList<LibraryRowItem> Build()
    {
        var keyword = _searchText.Trim();
        var byKey = new Dictionary<string, LibraryPoolEntry>(StringComparer.Ordinal);
        foreach (var entry in _pool) byKey[Key(entry.PackageId, entry.Version, entry.Architecture, entry.Repo)] = entry;

        var grouped = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<LibraryRowItem>(_pool.Count + _groups.Count + 8);
        var groups = 0;

        // 同一插件可能被下载/导出过多次（多个 run）→ 显示时合并成一行（设计：资料库去重显示）
        var mergedGroups = MergeSameGroups ? MergeGroups(_groups) : [.. _groups];
        // 分组过滤：null = 全部；**空集合 = 这个分组还没有成员，就什么都不显示**
        var allowedRoots = AllowedRoots;
        if (allowedRoots is not null)
        {
            mergedGroups = mergedGroups.Where(group => allowedRoots.Contains(group.RootPackage)).ToList();
        }


        var orderedGroups = mergedGroups
            .Select(group => new
            {
                Group = group,
                Items = group.Items
                    .Select(item => byKey.TryGetValue(Key(item.PackageId, item.Version, item.Architecture, item.Repo), out var entry)
                        ? entry
                        : null)
                    .ToList(),
            })
            .ToList();

        // 组排序：体积 / 时间用聚合值，名字用显示名（没有则包名）
        orderedGroups.Sort((left, right) =>
        {
            var order = _sort.Field switch
            {
                LibrarySortField.Size => TotalBytes(left.Items).CompareTo(TotalBytes(right.Items)),
                LibrarySortField.Name => string.CompareOrdinal(
                    left.Group.DisplayName ?? left.Group.RootPackage,
                    right.Group.DisplayName ?? right.Group.RootPackage),
                _ => string.CompareOrdinal(left.Group.CreatedAt, right.Group.CreatedAt),
            };

            if (order == 0) order = string.CompareOrdinal(left.Group.RootPackage, right.Group.RootPackage);
            return _sort.Descending ? -order : order;
        });

        foreach (var entry in orderedGroups)
        {
            var group = entry.Group;
            foreach (var item in group.Items) grouped.Add(Key(item.PackageId, item.Version, item.Architecture, item.Repo));

            var all = entry.Items
                .Where(item => item is not null)
                .Cast<LibraryPoolEntry>()
                .OrderBy(item => item.PackageId == group.RootPackage ? 0 : 1)
                .ThenBy(item => item.PackageId, StringComparer.Ordinal)
                .ToList();

            var matching = all.Where(item => Matches(item, keyword)).ToList();
            var groupMatches = keyword.Length == 0 || MatchesGroup(group, keyword);
            if (!groupMatches && matching.Count == 0) continue;

            groups++;

            // 命中组名 → 列出全部成员（按组看）；只命中成员 → 只列命中的
            var items = groupMatches ? all : matching;
            var collapsed = _collapsed.Contains(group.Id) && items.Count > 1;
            var totalBytes = items.Sum(item => item.Bytes);

            rows.Add(new LibraryRowItem
            {
                Id = -group.Id,
                GroupId = group.Id,
                GroupIds = group.GroupIds ?? [group.Id],
                IsGroup = true,
                IsCollapsed = collapsed,
                GroupCount = items.Count,
                PackageId = group.RootPackage,
                RootPackage = group.RootPackage,
                NameText = string.IsNullOrWhiteSpace(group.DisplayName) ? group.RootPackage : group.DisplayName,
                VersionText = group.RootVersion,
                RepoText = items.FirstOrDefault()?.Repo ?? "",
                SizeText = FormatBytes(totalBytes),
                Bytes = totalBytes,
                CreatedAtText = FormatTime(group.CreatedAt),
                CreatedAtTicks = ParseTicks(group.CreatedAt),
                SearchText = BuildSearchText(group.DisplayName, group.RootPackage, group.RootVersion),
            });

            if (collapsed) rows[^1].IsGroupEnd = true;

            // 单成员组：组头一行就代表了这个包，不再重复列成员行（设计：单个包也可以合并）
            if (collapsed || items.Count <= 1) continue;

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                rows.Add(new LibraryRowItem
                {
                    Id = index,
                    IsGroupMember = true,
                    IsGroupEnd = index == items.Count - 1,
                    PackageId = item.PackageId,
                    NameText = string.IsNullOrWhiteSpace(item.DisplayName) ? item.PackageId : item.DisplayName,
                    VersionText = item.Version,
                    RepoText = item.Repo,
                    SizeText = FormatBytes(item.Bytes),
                    Bytes = item.Bytes,
                    RelativePath = item.RelativePath,
                    CreatedAtText = FormatTime(item.AcquiredAt),
                    CreatedAtTicks = ParseTicks(item.AcquiredAt),
                    SearchText = BuildSearchText(item.DisplayName, item.PackageId, item.Version),
                });
            }
        }

        // 不属于任何组的 deb → 独立行
        foreach (var entry in _pool
                     .Where(entry => allowedRoots is null || allowedRoots.Contains(entry.PackageId))
                     .Where(entry => !grouped.Contains(Key(entry.PackageId, entry.Version, entry.Architecture, entry.Repo)))
                     .Where(entry => Matches(entry, keyword))
                     .OrderByDescending(entry => ParseTicks(entry.AcquiredAt))
                     .ThenBy(entry => entry.PackageId, StringComparer.Ordinal))
        {
            rows.Add(new LibraryRowItem
            {
                Id = 0,
                PackageId = entry.PackageId,
                NameText = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.PackageId : entry.DisplayName,
                VersionText = entry.Version,
                RepoText = entry.Repo,
                SizeText = FormatBytes(entry.Bytes),
                Bytes = entry.Bytes,
                RelativePath = entry.RelativePath,
                CreatedAtText = FormatTime(entry.AcquiredAt),
                CreatedAtTicks = ParseTicks(entry.AcquiredAt),
                SearchText = BuildSearchText(entry.DisplayName, entry.PackageId, entry.Version),
            });
        }

        Rows = rows;
        // 内容寻址：多行可能指向同一对象文件，状态栏按实体文件去重
        LastCounts = (_pool.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(), groups);
        return rows;
    }

    // ── 匹配与格式化 ────────────────────────────────────────────────────────

    private static bool Matches(LibraryPoolEntry entry, string keyword)
        => keyword.Length == 0
           || entry.PackageId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || (entry.DisplayName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
           || entry.Version.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesGroup(LibraryGroup group, string keyword)
        => group.RootPackage.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || (group.DisplayName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
           || group.RootVersion.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string BuildSearchText(string? displayName, string packageId, string version)
        => displayName is null ? packageId + " " + version : displayName + " " + packageId + " " + version;

    /// <summary>显示名 + 包名；两者相同或没有显示名时只显示包名。</summary>
    private static string FormatPluginTitle(string? displayName, string packageId)
        => string.IsNullOrWhiteSpace(displayName) ||
           string.Equals(displayName, packageId, StringComparison.Ordinal)
            ? packageId
            : displayName + " · " + packageId;

    /// <summary>
    /// 把同一插件（同一 `RootPackage`）的多次记录合并成一组：成员取并集、版本取最高、时间取最新。
    /// 「资料库去重显示」——同一个插件下载/导出几次都只占一行。
    /// </summary>
    private static List<LibraryGroup> MergeGroups(IReadOnlyList<LibraryGroup> groups)
    {
        var byRoot = new Dictionary<string, List<LibraryGroup>>(StringComparer.Ordinal);   // key = 包名\0版本
        var order = new List<string>();

        foreach (var group in groups)
        {
            // 精确身份 = 包名 + 版本（1.5 与 1.6 是**两个**插件版本，不能合并成一行）
            var key = group.RootPackage + "\u0000" + group.RootVersion;
            if (!byRoot.TryGetValue(key, out var list))
            {
                list = [];
                byRoot[key] = list;
                order.Add(key);
            }

            list.Add(group);
        }

        var merged = new List<LibraryGroup>(order.Count);

        foreach (var root in order)
        {
            var members = byRoot[root];

            var items = new Dictionary<string, LibraryGroupItem>(StringComparer.Ordinal);
            foreach (var group in members)
            {
                foreach (var item in group.Items)
                {
                    var key = Key(item.PackageId, item.Version, item.Architecture, item.Repo);
                    if (items.TryGetValue(key, out var existing))
                    {
                        if (item.IsRoot && !existing.IsRoot) items[key] = existing with { IsRoot = true };
                        continue;
                    }

                    items[key] = item;
                }
            }

            var latest = members
                .OrderByDescending(group => ParseTicks(group.CreatedAt))
                .First();

            var highestVersion = members
                .Select(group => group.RootVersion)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(version => CyDownload.Core.Deb.DebianVersion.TryParse(version, out var parsed) ? parsed : default,
                    CyDownload.Core.Deb.DebianVersion.Comparer)
                .FirstOrDefault() ?? latest.RootVersion;

            merged.Add(latest with
            {
                Id = members.Min(group => group.Id),
                GroupIds = members.Select(group => group.Id).Distinct().OrderBy(id => id).ToList(),
                RootVersion = highestVersion,
                DisplayName = members.Select(group => group.DisplayName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                Items = items.Values.OrderBy(item => item.Position).ToList(),
            });
        }

        return merged;
    }

    private static long TotalBytes(IReadOnlyList<LibraryPoolEntry?> items)
        => items.Where(item => item is not null).Sum(item => item!.Bytes);

    private static string Key(string packageId, string version, string architecture, string repo)
        => packageId + "\u0000" + version + "\u0000" + architecture + "\u0000" + repo;

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024;
        if (bytes <= 0) return "";
        if (bytes < kb) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < kb * 1024) return (bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < kb * 1024 * 1024) return (bytes / (kb * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (kb * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    private static string FormatTime(string? raw)
        => DateTimeOffset.TryParse(raw, out var parsed) ? parsed.ToLocalTime().ToString("MM-dd HH:mm") : "";

    private static long ParseTicks(string? raw)
        => DateTimeOffset.TryParse(raw, out var parsed) ? parsed.UtcTicks : 0;
}




