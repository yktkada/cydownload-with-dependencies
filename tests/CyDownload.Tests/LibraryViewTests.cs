using CyDownload.App.ViewModels.Library;
using CyDownload.Core.Index;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 资料库视图逻辑：分组、展开/收起、聚合、搜索、独立 deb。
/// 行为参照 `iDeviceInstaller` 的 `LibraryView`（同仓 `ViewModels/Library/LibraryView.cs`）。
/// </summary>
public sealed class LibraryViewTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2024-01-01T10:00:00+08:00");

    private static LibraryPoolEntry Pool(string packageId, string version, long bytes, string repo = "saurik", int minute = 0)
        => new(
            packageId, version, "iphoneos-arm", repo, $"{packageId}/{version}_{repo}.deb", bytes, null,
            Base.AddMinutes(minute).ToString("O"));

    private static LibraryGroup Group(long id, string root, string rootVersion, params (string Package, string Version)[] items)
        => new(
            id, RunId: 1, RootPackage: root, RootVersion: rootVersion, Target: "iPhone 4S / 6.1.3",
            CreatedAt: Base.AddHours(id).ToString("O"), ExportDir: null,
            Items: items.Select((item, index) => new LibraryGroupItem(
                item.Package, item.Version, "iphoneos-arm", "saurik", item.Package == root, index)).ToList());

    private static LibraryView Build(
        IReadOnlyList<LibraryPoolEntry> pool,
        IReadOnlyList<LibraryGroup> groups,
        string search = "",
        LibrarySort? sort = null)
    {
        var view = new LibraryView
        {
            SearchText = search,
            Sort = sort ?? new LibrarySort(LibrarySortField.Time, Descending: true),
        };

        view.ReplaceAll(pool, groups);
        return view;
    }

    [Fact]
    public void GroupsStartCollapsed_AndShowAggregates()
    {
        var view = Build(
            [Pool("wget", "1.16-4", 200), Pool("gettext", "0.17-7", 768)],
            [Group(1, "wget", "1.16-4", ("wget", "1.16-4"), ("gettext", "0.17-7"))]);

        var rows = view.Build();

        var group = Assert.Single(rows);
        Assert.True(group.IsGroup);
        Assert.True(group.IsCollapsed);
        Assert.Equal(2, group.GroupCount);
        Assert.Equal(968, group.Bytes);
        Assert.Equal("wget", group.NameText);
        Assert.Equal("1.16-4", group.VersionText);
        Assert.True(group.IsGroupEnd);
    }

    [Fact]
    public void ExpandingGroup_ListsMembersWithRootFirst()
    {
        var view = Build(
            [Pool("wget", "1.16-4", 200), Pool("gettext", "0.17-7", 768), Pool("openssl", "0.9.8zg-13", 1000)],
            [Group(1, "wget", "1.16-4",
                ("wget", "1.16-4"), ("gettext", "0.17-7"), ("openssl", "0.9.8zg-13"))]);

        view.ToggleGroup(1);
        var rows = view.Build();

        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].IsGroup);
        Assert.False(rows[0].IsCollapsed);
        Assert.Equal(["wget", "gettext", "openssl"], rows.Skip(1).Select(row => row.PackageId));
        Assert.All(rows.Skip(1), row => Assert.True(row.IsGroupMember));
        Assert.True(rows[^1].IsGroupEnd);
        Assert.Equal(new System.Windows.Thickness(18, 0, 0, 0), rows[1].IndentMargin);
    }

    [Fact]
    public void SamePluginDifferentVersions_AreNotMerged()
    {
        // 用户 ：1.5 与 1.6 是两个包，不能被合并显示成一行（更不能"覆盖"）
        var pool = new[]
        {
            Pool("com.coolbooter.coolbooter", "1.5-release", 27_300_000),
            Pool("com.coolbooter.coolbooter", "1.6-release", 32_200_000),
        };
        var groups = new[]
        {
            Group(1, "com.coolbooter.coolbooter", "1.5-release", ("com.coolbooter.coolbooter", "1.5-release")),
            Group(2, "com.coolbooter.coolbooter", "1.6-release", ("com.coolbooter.coolbooter", "1.6-release")),
        };

        var rows = Build(pool, groups).Build();

        Assert.Equal(2, rows.Count(row => row.IsGroup));
        Assert.Contains(rows, row => row.VersionText == "1.5-release");
        Assert.Contains(rows, row => row.VersionText == "1.6-release");
    }

    [Fact]
    public void MergeSameGroups_CanBeTurnedOff()
    {
        // 对齐参考项目 iDeviceGui 的 merge same bundle 开关：关掉后每次 run 各占一行
        var pool = new[]
        {
            Pool("com.jjr.nmlfix", "1.0.0", 9_800),
            Pool("mobilesubstrate", "0.9.6301", 122_342),
        };

        var groups = new[]
        {
            Group(1, "com.jjr.nmlfix", "1.0.0", ("com.jjr.nmlfix", "1.0.0"), ("mobilesubstrate", "0.9.6301")),
            Group(2, "com.jjr.nmlfix", "1.0.0", ("com.jjr.nmlfix", "1.0.0"), ("mobilesubstrate", "0.9.6301")),
        };

        var view = Build(pool, groups);
        view.MergeSameGroups = false;
        view.ExpandAll();

        var rows = view.Build();

        Assert.Equal(2, rows.Count(row => row.IsGroup));
    }

    [Fact]
    public void SamePluginDownloadedTwice_IsMergedIntoOneRow()
    {
        // 设计：资料库去重显示 —— 同一插件多次下载/导出只占一行
        var pool = new[]
        {
            Pool("com.jjr.nmlfix", "1.0.0", 9_800),
            Pool("mobilesubstrate", "0.9.6301", 122_342),
            Pool("com.saurik.substrate.safemode", "0.9.6005", 22_710),
        };

        var groups = new[]
        {
            Group(1, "com.jjr.nmlfix", "1.0.0", ("com.jjr.nmlfix", "1.0.0"), ("mobilesubstrate", "0.9.6301")),
            Group(2, "com.jjr.nmlfix", "1.0.0", ("com.jjr.nmlfix", "1.0.0"), ("com.saurik.substrate.safemode", "0.9.6005")),
        };

        var view = Build(pool, groups);
        view.ExpandAll();
        var rows = view.Build();

        Assert.Single(rows, row => row.IsGroup);
        var group = rows.Single(row => row.IsGroup);
        Assert.Equal(3, group.GroupCount);                       // 两次的成员取并集
        Assert.Equal(3, rows.Count(row => row.IsGroupMember));
    }

    [Fact]
    public void SingleMemberGroup_ShowsOnlyHeader_NoDuplicateChildRow()
    {
        var view = Build([Pool("adv-cmds", "119-6", 62_804)],
            [Group(1, "adv-cmds", "119-6", ("adv-cmds", "119-6"))]);
        view.ExpandAll();

        var rows = view.Build();

        var group = Assert.Single(rows);
        Assert.True(group.IsGroup);
        Assert.Equal(1, group.GroupCount);
    }

    [Fact]
    public void DisplayName_IsShownBeforePackageId()
    {
        var pool = new[] { Pool("com.jjr.nmlfix", "1.0.0", 9_800) with { DisplayName = "NMLFix" } };
        var group = Group(1, "com.jjr.nmlfix", "1.0.0", ("com.jjr.nmlfix", "1.0.0")) with { DisplayName = "NMLFix" };

        var rows = Build(pool, [group]).Build();

        Assert.Equal("NMLFix", rows[0].NameText);
        Assert.Equal("com.jjr.nmlfix", rows[0].PackageId);
        Assert.Contains("nmlfix", rows[0].SearchText, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void SameDebInTwoGroups_AppearsInBoth()
    {
        var pool = new[]
        {
            Pool("wget", "1.16-4", 200),
            Pool("mobilesubstrate", "0.9.6301", 300),
            Pool("dpkg", "1.18.10-12", 500),
        };

        var view = Build(pool,
        [
            Group(1, "wget", "1.16-4", ("wget", "1.16-4"), ("dpkg", "1.18.10-12")),
            Group(2, "mobilesubstrate", "0.9.6301", ("mobilesubstrate", "0.9.6301"), ("dpkg", "1.18.10-12")),
        ]);

        view.ExpandAll();
        var rows = view.Build();

        Assert.Equal(2, rows.Count(row => row.IsGroup));
        Assert.Equal(2, rows.Count(row => row.PackageId == "dpkg"));
    }

    [Fact]
    public void OrphanDebs_BecomeStandaloneRows()
    {
        var view = Build(
            [Pool("wget", "1.16-4", 200), Pool("orphan-pkg", "2.0", 50)],
            [Group(1, "wget", "1.16-4", ("wget", "1.16-4"))]);

        var rows = view.Build();

        Assert.Equal(2, rows.Count);                  // 单成员组只留组头 + 独立 deb
        Assert.True(rows[0].IsGroup);
        Assert.False(rows[0].IsCollapsed);
        Assert.Contains(rows, row => row.PackageId == "orphan-pkg" && !row.IsGroup);
    }

    [Fact]
    public void Search_MatchesGroupNameAndMemberName()
    {
        var pool = new[]
        {
            Pool("wget", "1.16-4", 200), Pool("gettext", "0.17-7", 768), Pool("orphan-pkg", "2.0", 50),
        };

        var groups = new[] { Group(1, "wget", "1.16-4", ("wget", "1.16-4"), ("gettext", "0.17-7")) };

        // 命中组名：组保留（默认收起），展开后能看到全部成员
        var byGroupView = Build(pool, groups, search: "wge");
        byGroupView.ExpandAll();
        var byGroup = byGroupView.Build();
        Assert.Single(byGroup, row => row.IsGroup);
        Assert.Contains(byGroup, row => row.PackageId == "gettext");

        // 命中成员（"text" 只出现在 gettext 里）：组保留，只剩命中的成员
        var byMemberView = Build(pool, groups, search: "text");
        byMemberView.ExpandAll();
        var byMember = byMemberView.Build();
        var group = byMember.First(row => row.IsGroup);
        Assert.Equal(1, group.GroupCount);            // 只剩命中的成员
        Assert.Single(byMember);                       // 单成员组只显示组头，不再重复列成员行

        // 全都不命中：只剩独立 deb 为空
        Assert.Empty(Build(pool, groups, search: "zzz").Build());
    }

    [Fact]
    public void Sort_BySizeDescending_PutsLargestGroupFirst()
    {
        var pool = new[]
        {
            Pool("small", "1.0", 10), Pool("big", "1.0", 1000), Pool("mid", "1.0", 100),
        };

        var view = Build(pool,
        [
            Group(1, "small", "1.0", ("small", "1.0")),
            Group(2, "big", "1.0", ("big", "1.0")),
            Group(3, "mid", "1.0", ("mid", "1.0")),
        ], sort: new LibrarySort(LibrarySortField.Size, Descending: true));

        var rows = view.Build().Where(row => row.IsGroup).Select(row => row.NameText).ToList();
        Assert.Equal(["big", "mid", "small"], rows);
    }

    [Fact]
    public void CollapseAll_And_ExpandAll()
    {
        var pool = new[]
        {
            Pool("a", "1.0", 10), Pool("a-dep", "1.0", 10),
            Pool("b", "1.0", 10), Pool("b-dep", "1.0", 10),
        };

        var view = Build(pool,
        [
            Group(1, "a", "1.0", ("a", "1.0"), ("a-dep", "1.0")),
            Group(2, "b", "1.0", ("b", "1.0"), ("b-dep", "1.0")),
        ]);

        view.ExpandAll();
        Assert.Equal(6, view.Build().Count);

        view.CollapseAll();
        Assert.Equal(2, view.Build().Count);      // 两个组头
    }

    [Fact]
    public void LastCounts_ReportsGroupsAndFiles()
    {
        var view = Build(
            [Pool("wget", "1.16-4", 200), Pool("gettext", "0.17-7", 768)],
            [Group(1, "wget", "1.16-4", ("wget", "1.16-4"), ("gettext", "0.17-7"))]);

        view.Build();
        Assert.Equal((2, 1), view.LastCounts);
    }

    [Fact]
    public void EmptyLibrary_BuildsEmptyRows()
    {
        var view = Build([], []);
        Assert.Empty(view.Build());
        Assert.Equal((0, 0), view.LastCounts);
    }
}



