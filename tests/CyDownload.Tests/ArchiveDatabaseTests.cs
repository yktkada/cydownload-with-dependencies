using System.IO;
using CyDownload.Core.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CyDownload.Tests;

/// <summary>存档库：建库 / schema 形状 / 写锁语义。</summary>
public sealed class ArchiveDatabaseTests
{
    [Fact]
    public void Open_CreatesSchemaV1_WithWalAndBusyTimeout()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Full("archive.db");

        using var db = ArchiveDatabase.Open(path);

        Assert.True(File.Exists(path));
        Assert.Equal(SchemaSql.CurrentVersion, db.UserVersion);
        Assert.Equal("wal", db.JournalMode);
        Assert.Equal(15000, db.BusyTimeout);
    }

    [Fact]
    public void Open_IsIdempotent_SecondCallKeepsVersion()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Full("archive.db");

        using (var first = ArchiveDatabase.Open(path))
        {
            Assert.Equal(SchemaSql.CurrentVersion, first.UserVersion);
        }

        using var second = ArchiveDatabase.Open(path);
        Assert.Equal(SchemaSql.CurrentVersion, second.UserVersion);
    }

    [Fact]
    public void Schema_HasExpectedTables()
    {
        using var workspace = new TestWorkspace();
        using var db = ArchiveDatabase.Open(workspace.Full("archive.db"));

        var tables = db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        });

        string[] expected =
        [
            "archive_pool", "base_packages", "closure_runs", "library_group_items", "library_groups", "packages", "provides", "repos", "targets",
        ];
        Assert.Equal(expected, tables);
    }

    [Fact]
    public void Packages_Has20DataColumns()
    {
        using var workspace = new TestWorkspace();
        using var db = ArchiveDatabase.Open(workspace.Full("archive.db"));

        var columns = db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(packages);";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(1));
            return names;
        });

        // 1 个主键 id + 21 个数据列（v6 加 min_ios）
        Assert.Equal(22, columns.Count);
        Assert.Contains("min_ios", columns);
        Assert.Contains("predepends", columns);
        Assert.Contains("provides", columns);
        Assert.Contains("repo", columns);
        Assert.Contains("sha256", columns);
        Assert.Contains("tags", columns);
        Assert.Contains("installed_size", columns);
    }

    [Fact]
    public void RedundantIndexes_AreNotCreated()
    {
        // ：IX_packages_pkgid 被 UNIQUE(package_id,…) 最左前缀覆盖、
        // IX_provides_name 与 PRIMARY KEY(name,…) 重复 —— 都不建。
        using var workspace = new TestWorkspace();
        using var db = ArchiveDatabase.Open(workspace.Full("archive.db"));

        var indexes = db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND sql IS NOT NULL;";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        });

        Assert.Contains("IX_packages_name", indexes);
        Assert.DoesNotContain("IX_packages_pkgid", indexes);
        Assert.DoesNotContain("IX_provides_name", indexes);
    }

    [Fact]
    public void WriteLock_IsNotReentrant()
    {
        using var workspace = new TestWorkspace();
        using var db = ArchiveDatabase.Open(workspace.Full("archive.db"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            db.Write(_ => db.Write(_ => 0)));

        Assert.Contains("not reentrant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_InsertsRepoAndReadsBack()
    {
        using var workspace = new TestWorkspace();
        using var db = ArchiveDatabase.Open(workspace.Full("archive.db"));

        db.Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO repos(name, base_url, layout, suite, component, priority) " +
                "VALUES('saurik', 'http://apt.saurik.com/', 'dists', 'ios/793.00', 'main', 1);";
            return cmd.ExecuteNonQuery();
        });

        var count = db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM repos WHERE name='saurik';";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public void Checkpoint_Succeeds()
    {
        using var workspace = new TestWorkspace();
        using var db = ArchiveDatabase.Open(workspace.Full("archive.db"));

        db.Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO repos(name, base_url, layout, priority) VALUES('flat', 'http://apt.gs/', 'flat', 3);";
            return cmd.ExecuteNonQuery();
        });

        db.Checkpoint();
        Assert.True(File.Exists(workspace.Full("archive.db")));
    }
}

