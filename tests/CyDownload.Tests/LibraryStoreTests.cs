using System.IO;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 资料库分组（schema v2）：落库 / 读回 / 迁移 / 幂等。
/// 行为对齐 `iDeviceInstaller`：一次导出 = 一个 run，每个根插件一组，组内是整个依赖闭包。
/// </summary>
public sealed class LibraryStoreTests
{
    private static LibraryGroupItem Item(string packageId, string version, bool isRoot, int position, string repo = "saurik")
        => new(packageId, version, "iphoneos-arm", repo, isRoot, position);

    private static LibraryPoolEntry Pool(string packageId, string version, string repo = "saurik", long bytes = 100)
        => new(packageId, version, "iphoneos-arm", repo, $"{packageId}/{version}_{repo}.deb", bytes, null,
            "2024-01-01T00:00:00.0000000+08:00");

    [Fact]
    public void SchemaUpgrade_FromV1_CreatesGroupTables()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Full("archive.db");

        // 手工造一个 v1 库（只有 v1 表 + user_version=1）
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            SchemaSql.CreateV1Schema(connection);
        }

        using var database = ArchiveDatabase.Open(path);

        Assert.Equal(SchemaSql.CurrentVersion, database.UserVersion);
        Assert.Equal(SchemaSql.CurrentVersion, database.UserVersion);
        Assert.Equal(0, new LibraryStore(database).CountGroups());
    }

    [Fact]
    public void SaveRun_ThenLoadGroups_RoundTrips()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        var items = new[]
        {
            Item("wget", "1.16-4", isRoot: true, position: 0),
            Item("gettext", "0.17-7", isRoot: false, position: 1),
            Item("openssl", "0.9.8zg-13", isRoot: false, position: 2),
        };

        var runId = store.SaveRun(
            "iPhone 4S / 6.1.3",
            @"D:\export\wget",
            [new LibraryGroupDraft("wget", "1.16-4", items)],
            [Pool("wget", "1.16-4", bytes: 210_290), Pool("gettext", "0.17-7"), Pool("openssl", "0.9.8zg-13")],
            DateTimeOffset.Parse("2024-01-01T22:00:00+08:00"));

        Assert.Equal(1, runId);
        Assert.Equal(1, store.CountGroups());

        var group = Assert.Single(store.LoadGroups());
        Assert.Equal("wget", group.RootPackage);
        Assert.Equal("1.16-4", group.RootVersion);
        Assert.Equal("iPhone 4S / 6.1.3", group.Target);
        Assert.Equal(@"D:\export\wget", group.ExportDir);
        Assert.Equal(3, group.Items.Count);
        Assert.Single(group.Items, item => item.IsRoot);
        Assert.Equal(["wget", "gettext", "openssl"], group.Items.Select(item => item.PackageId));

        var pool = store.LoadPool();
        Assert.Equal(3, pool.Count);
        Assert.Equal(210_290, pool.Single(entry => entry.PackageId == "wget").Bytes);
    }

    [Fact]
    public void SaveRun_TwoPlugins_CreatesTwoGroupsSharingDependencies()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        var items = new[]
        {
            Item("wget", "1.16-4", isRoot: true, position: 0),
            Item("mobilesubstrate", "0.9.6301", isRoot: true, position: 1),
            Item("dpkg", "1.18.10-12", isRoot: false, position: 2),
        };

        store.SaveRun("target", null,
        [
            new LibraryGroupDraft("wget", "1.16-4", items),
            new LibraryGroupDraft("mobilesubstrate", "0.9.6301", items),
        ],
        [Pool("wget", "1.16-4"), Pool("mobilesubstrate", "0.9.6301"), Pool("dpkg", "1.18.10-12")],
        DateTimeOffset.Now);

        var groups = store.LoadGroups();
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Equal(3, group.Items.Count));
        // 同一个 run：两个组共享 run_id（将来可以整批删）
        Assert.Single(groups.Select(group => group.RunId).Distinct());

        // 同一份 deb 出现在两个组里 = 「按插件的闭包」，不是按文件去重
        Assert.Equal(2, groups.Count(group => group.Items.Any(item => item.PackageId == "dpkg")));
    }

    [Fact]
    public void UpsertPool_IsIdempotent_AndKeepsBytes()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        store.UpsertPool([Pool("wget", "1.16-4", bytes: 100)]);
        store.UpsertPool([Pool("wget", "1.16-4", bytes: 200)]);

        var pool = store.LoadPool();
        var entry = Assert.Single(pool);
        Assert.Equal(200, entry.Bytes);
    }

    [Fact]
    public void Pool_KeepsSameVersionFromTwoRepos_Separately()
    {
        // ：v3 之前 archive_pool 主键不含源，跨源会互相覆盖
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        store.UpsertPool(
        [
            Pool("mobilesubstrate", "0.9.6301", repo: "saurik", bytes: 122_342),
            Pool("mobilesubstrate", "0.9.6301", repo: "aptgs", bytes: 122_128),
        ]);

        var pool = store.LoadPool();
        Assert.Equal(2, pool.Count);
        Assert.Equal(122_342, pool.Single(entry => entry.Repo == "saurik").Bytes);
        Assert.Equal(122_128, pool.Single(entry => entry.Repo == "aptgs").Bytes);
    }
    [Fact]
    public void DeleteUnreferencedPool_RemovesOrphanRowsAndFiles_KeepsReferenced()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);
        var poolDir = Path.Combine(workspace.Root, "pool");
        Directory.CreateDirectory(Path.Combine(poolDir, "objects"));

        // 两个文件：一个被分组引用，一个已从资料库移除
        var keptFile = Path.Combine(poolDir, "objects", "aa.deb");
        var orphanFile = Path.Combine(poolDir, "objects", "bb.deb");
        File.WriteAllBytes(keptFile, [1, 2, 3]);
        File.WriteAllBytes(orphanFile, [4, 5, 6]);

        store.SaveRun("target", null,
            [new LibraryGroupDraft("wget", "1.16-4", [Item("wget", "1.16-4", true, 0)], "Wget")],
            [Pool("wget", "1.16-4") with { RelativePath = "objects\\aa.deb" }], DateTimeOffset.Now);

        store.UpsertPool([Pool("gettext", "0.17-7") with { RelativePath = "objects\\bb.deb" }]);

        var stats = store.CountUnreferencedPool();
        Assert.Equal(1, stats.Entries);
        Assert.Equal(1, stats.Files);

        var result = store.DeleteUnreferencedPool(poolDir);

        Assert.Equal(1, result.DeletedRows);
        Assert.Equal(1, result.DeletedFiles);
        Assert.True(File.Exists(keptFile));
        Assert.False(File.Exists(orphanFile));
        Assert.Single(store.LoadPool());
        Assert.Single(store.LoadGroups());                 // 分组不受影响
    }

    [Fact]
    public void DeleteUnreferencedPool_KeepsFileSharedWithReferencedRow()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);
        var poolDir = Path.Combine(workspace.Root, "pool");
        Directory.CreateDirectory(Path.Combine(poolDir, "objects"));

        // 内容寻址：两行指向同一个对象文件，只有一行还被分组引用
        var shared = Path.Combine(poolDir, "objects", "cc.deb");
        File.WriteAllBytes(shared, [9, 9, 9]);

        store.SaveRun("target", null,
            [new LibraryGroupDraft("wget", "1.16-4", [Item("wget", "1.16-4", true, 0)])],
            [Pool("wget", "1.16-4") with { RelativePath = "objects\\cc.deb" }], DateTimeOffset.Now);

        store.UpsertPool([Pool("gettext", "0.17-7") with { RelativePath = "objects\\cc.deb" }]);

        var result = store.DeleteUnreferencedPool(poolDir);

        Assert.Equal(1, result.DeletedRows);            // 没被引用的那一行删掉
        Assert.Equal(0, result.DeletedFiles);           // 但共享对象文件保留
        Assert.True(File.Exists(shared));
        Assert.Single(store.LoadPool());
    }

    [Fact]
    public void SaveRun_WithDisplayNames_RoundTrips()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        var items = new[] { Item("com.jjr.nmlfix", "1.0.0", isRoot: true, position: 0) };
        store.SaveRun("target", null,
            [new LibraryGroupDraft("com.jjr.nmlfix", "1.0.0", items, "NMLFix")],
            [Pool("com.jjr.nmlfix", "1.0.0") with { DisplayName = "NMLFix" }], DateTimeOffset.Now);

        var group = Assert.Single(store.LoadGroups());
        Assert.Equal("NMLFix", group.DisplayName);

        var entry = Assert.Single(store.LoadPool());
        Assert.Equal("NMLFix", entry.DisplayName);
    }

    [Fact]
    public void SchemaV5_Migration_BackfillsDisplayNameFromPackages()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);
        var store = new LibraryStore(database);

        // 先有包（带 Name），再有资料库分组（模拟历史库：v5 之前没有 display_name）
        repository.Import(
            new CyDownload.Core.Repos.RepoDescriptor
            {
                Name = "saurik", BaseUrl = "http://example.invalid", Layout = CyDownload.Core.Repos.RepoLayout.Flat,
            },
            CyDownload.Core.Deb.ControlFileParser.Parse("""
                Package: com.jjr.nmlfix
                Name: NMLFix
                Version: 1.0.0
                Architecture: iphoneos-arm
                """),
            "iphoneos-arm", DateTimeOffset.Now);

        store.SaveRun("target", null,
            [new LibraryGroupDraft("com.jjr.nmlfix", "1.0.0", [Item("com.jjr.nmlfix", "1.0.0", true, 0)])],
            [Pool("com.jjr.nmlfix", "1.0.0")], DateTimeOffset.Now);

        // 清空 display_name，模拟迁移前的旧数据
        database.Write(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "UPDATE library_groups SET display_name = NULL; UPDATE archive_pool SET display_name = NULL;";
            command.ExecuteNonQuery();
            return 0;
        });

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.Full("archive.db")}"))
        {
            connection.Open();
            SchemaSql.MigrateToV5(connection);
        }

        Assert.Equal("NMLFix", Assert.Single(store.LoadGroups()).DisplayName);
        Assert.Equal("NMLFix", Assert.Single(store.LoadPool()).DisplayName);
    }
    [Fact]
    public void SaveRun_WithoutExportDirectory_StillRecordsGroup()
    {
        // 「下载到资料库」：不导出，只下进 pool + 记分组（export_dir 为 null）
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        var items = new[] { Item("adv-cmds", "119-6", isRoot: true, position: 0) };
        store.SaveRun("target", exportDirectory: null,
            [new LibraryGroupDraft("adv-cmds", "119-6", items)],
            [Pool("adv-cmds", "119-6", bytes: 62_813)], DateTimeOffset.Now);

        var group = Assert.Single(store.LoadGroups());
        Assert.Null(group.ExportDir);
        Assert.Single(group.Items);
        Assert.Single(store.LoadPool());
    }
    [Fact]
    public void DeleteGroup_RemovesItsItems()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        store.SaveRun("target", null,
            [new LibraryGroupDraft("wget", "1.16-4", [Item("wget", "1.16-4", true, 0)])],
            [Pool("wget", "1.16-4")],
            DateTimeOffset.Now);

        var group = Assert.Single(store.LoadGroups());
        Assert.True(store.DeleteGroup(group.Id));
        Assert.Empty(store.LoadGroups());
        Assert.Single(store.LoadPool());        // pool 台账保留（文件还在）
    }

    [Fact]
    public void SecondRun_GetsNewRunId()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var store = new LibraryStore(database);

        var items = new[] { Item("wget", "1.16-4", true, 0) };
        var pool = new[] { Pool("wget", "1.16-4") };

        var first = store.SaveRun("target", null, [new LibraryGroupDraft("wget", "1.16-4", items)], pool, DateTimeOffset.Now);
        var second = store.SaveRun("target", null, [new LibraryGroupDraft("wget", "1.16-4", items)], pool, DateTimeOffset.Now);

        Assert.Equal(first + 1, second);
        Assert.Equal(2, store.CountGroups());   // 两次导出 = 两条记录（历史保留）
    }
}




