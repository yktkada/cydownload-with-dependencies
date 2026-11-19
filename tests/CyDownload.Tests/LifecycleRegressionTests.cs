using System.IO;
using CyDownload.App.ViewModels.Library;
using CyDownload.Core.Deb;
using CyDownload.Core.Index;
using CyDownload.Core.Repos;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 回归用例：菜单绑定、清理保护用户分组、合并组并集、
/// 改名同步台账、单段版本解析。
/// </summary>
public sealed class LifecycleRegressionTests
{
    // ── P1-A：资料库右键「删除这一组记录」必须绑到删**记录**的 handler ──────────────

    [Fact]
    public void LibraryDeleteGroupMenu_BindsToDeleteRecordHandler_NotUserGroupHandler()
    {
        var xamlPath = FindRepoFile(Path.Combine("src", "CyDownload.App", "Views", "MainWindow.xaml"));
        var codePath = FindRepoFile(Path.Combine("src", "CyDownload.App", "Views", "MainWindow.xaml.cs"));

        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);

        var menuLine = xaml.Split('\n')
            .First(line => line.Contains("LibMenuDeleteGroup", StringComparison.Ordinal));

        Assert.Contains("OnDeleteLibraryGroupClick", menuLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDeleteUserGroupClick", menuLine, StringComparison.Ordinal);
        Assert.Contains("private void OnDeleteLibraryGroupClick", code, StringComparison.Ordinal);
        Assert.Contains("private void OnDeleteUserGroupClick", code, StringComparison.Ordinal);
    }

    // ── 清理不删用户分组里的插件 ────────────────────────────────────────

    [Fact]
    public void DeleteUnreferencedPool_ProtectsPackagesUsedByUserGroups()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);
        var poolDir = Path.Combine(workspace.Root, "pool");
        Directory.CreateDirectory(Path.Combine(poolDir, "objects"));

        var groupFile = Path.Combine(poolDir, "objects", "aa.deb");
        var orphanFile = Path.Combine(poolDir, "objects", "bb.deb");
        File.WriteAllBytes(groupFile, [1, 2, 3]);
        File.WriteAllBytes(orphanFile, [4, 5, 6]);

        store.UpsertPool(
        [
            Pool("com.jjr.nmlfix", "1.0.0") with { RelativePath = "objects\\aa.deb" },
            Pool("gettext", "0.17-7") with { RelativePath = "objects\\bb.deb" },
        ]);

        // 没有任何自动分组，只有用户自定义分组（groups.json）里有 com.jjr.nmlfix
        var protectedIds = new[] { "com.jjr.nmlfix" };

        var stats = store.CountUnreferencedPool(protectedIds);
        Assert.Equal(1, stats.Entries);                       // 只有 gettext 是孤儿

        var result = store.DeleteUnreferencedPool(poolDir, protectedPackageIds: protectedIds);

        Assert.Equal(1, result.DeletedRows);
        Assert.True(File.Exists(groupFile));                  // 用户分组的包保住
        Assert.False(File.Exists(orphanFile));
        Assert.Single(store.LoadPool());
    }

    // ── 合并显示的行带全部底层 group id，导出/删除按并集 ─────────────────

    [Fact]
    public void MergedGroupRow_CarriesAllUnderlyingGroupIds()
    {
        var pool = new[]
        {
            Pool("com.jjr.nmlfix", "1.0.0"),
            Pool("mobilesubstrate", "0.9.6301"),
        };
        var groups = new[]
        {
            Group(1, "com.jjr.nmlfix", ("com.jjr.nmlfix", "1.0.0"), ("mobilesubstrate", "0.9.6301")),
            Group(2, "com.jjr.nmlfix", ("com.jjr.nmlfix", "1.0.0")),
        };

        var view = new LibraryView();
        view.ReplaceAll(pool, groups);

        var row = Assert.Single(view.Build());
        Assert.Equal([1, 2], row.GroupIds);
    }

    // ── 源改名要同步 archive_pool / library_group_items ─────────────────

    [Fact]
    public void SaveRepo_Rename_SyncsPoolAndGroupItemRepo()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);
        var store = new LibraryStore(database);

        repository.SaveRepo(new RepoDescriptor { Name = "old", BaseUrl = "http://example.invalid", Layout = RepoLayout.Flat });
        store.SaveRun("target", null,
            [new LibraryGroupDraft("wget", "1.16-4", [Item("wget", "1.16-4") with { Repo = "old" }])],
            [Pool("wget", "1.16-4") with { Repo = "old" }], DateTimeOffset.Now);

        repository.SaveRepo(
            new RepoDescriptor { Name = "new", BaseUrl = "http://example.invalid", Layout = RepoLayout.Flat },
            originalName: "old");

        Assert.Equal("new", Assert.Single(store.LoadPool()).Repo);
        var items = Assert.Single(store.LoadGroups()).Items;
        Assert.Equal("new", Assert.Single(items).Repo);
    }

    // ── P3（报告 §补注-3）：单段版本号 ─────────────────────────────────────────

    [Theory]
    [InlineData("7", "6.1.3", false)]
    [InlineData("7", "8.1.2", true)]
    [InlineData("8.1.2", "8.1.2", true)]
    public void IsCompatible_HandlesSingleSegmentVersion(string minIos, string target, bool expected)
        => Assert.Equal(expected, MinIosParser.IsCompatible(minIos, target));

    // ── 工具 ────────────────────────────────────────────────────────────────

    private static LibraryPoolEntry Pool(string id, string version)
        => new(id, version, "iphoneos-arm", "saurik", Path.Combine("objects", id + ".deb"), 100, null, "2024-01-01T00:00:00Z");

    private static LibraryGroupItem Item(string id, string version, bool isRoot = false, int position = 0)
        => new(id, version, "iphoneos-arm", "saurik", isRoot, position);

    private static LibraryGroup Group(long id, string root, params (string Id, string Version)[] members)
        => new(id, 1, root, members[0].Version, "target", "2024-01-01T00:00:00Z", null,
            members.Select((member, index) => Item(member.Id, member.Version, index == 0, index)).ToList());

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"repo file not found: {relativePath}");
    }
}
