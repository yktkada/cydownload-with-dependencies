using System.IO;
using CyDownload.Core.Deb;
using CyDownload.Core.Index;
using CyDownload.Core.Repos;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 源管理：外键、改名保留包行、删源连包删、种子只做一次的前提、last_fetch。
/// </summary>
public sealed class RepoManagementTests
{
    private static RepoDescriptor Source(string name, string url = "http://example.invalid", int priority = 10)
        => new()
        {
            Name = name,
            BaseUrl = url,
            Layout = RepoLayout.Flat,
            Priority = priority,
        };


    private static void ImportOne(ArchiveDatabase database, PackageRepository repository, string repo, string packageId)
    {
        var stanzas = ControlFileParser.Parse($"""
            Package: {packageId}
            Version: 1.0
            Architecture: iphoneos-arm
            Provides: {packageId}-virtual
            """);

        repository.Import(Source(repo), stanzas, "iphoneos-arm", DateTimeOffset.Now);
    }

    [Fact]
    public void DeleteRepo_WithPackages_RemovesPackagesAndProvides()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);

        ImportOne(database, repository, "saurik", "wget");
        Assert.Equal(1, repository.CountPackages("saurik"));
        Assert.NotEmpty(repository.FindByProvidedName("wget-virtual"));

        // ：以前会抛 FOREIGN KEY constraint failed
        var removed = repository.DeleteRepo("saurik");

        Assert.Equal(1, removed);
        Assert.Empty(repository.LoadRepos());
        Assert.Equal(0, repository.CountPackages("saurik"));
        Assert.Empty(repository.FindByProvidedName("wget-virtual"));
    }

    [Fact]
    public void RenameRepo_KeepsPackages_AndMovesThemToNewName()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);

        ImportOne(database, repository, "saurik", "wget");

        repository.SaveRepo(Source("saurik-mirror"), originalName: "saurik");

        var repos = repository.LoadRepos();
        var repo = Assert.Single(repos);
        Assert.Equal("saurik-mirror", repo.Name);
        Assert.Equal(1, repository.CountPackages("saurik-mirror"));
        Assert.Equal(0, repository.CountPackages("saurik"));
        Assert.NotNull(repository.Find("wget", "1.0", "iphoneos-arm", "saurik-mirror"));
    }

    [Fact]
    public void SaveRepo_DoesNotTouchLastFetch()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);

        repository.SaveRepo(Source("fresh"));
        Assert.Null(repository.LoadRepos().Single().LastFetch);      // 只加源 ≠ 抓过

        ImportOne(database, repository, "fresh", "wget");
        var afterFetch = repository.LoadRepos().Single().LastFetch;
        Assert.NotNull(afterFetch);

        // 编辑源（改地址）不该改写「上次抓取」
        repository.SaveRepo(Source("fresh", url: "http://mirror.invalid"));
        Assert.Equal(afterFetch, repository.LoadRepos().Single().LastFetch);
    }

    [Fact]
    public void HasAnyRepo_ReflectsState()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);

        Assert.False(repository.HasAnyRepo());

        repository.SaveRepo(Source("a"));
        Assert.True(repository.HasAnyRepo());

        repository.DeleteRepo("a");
        Assert.False(repository.HasAnyRepo());
    }

    [Fact]
    public void DefaultSources_IncludeLiveAndLegacyRepos()
    {
        var sources = DefaultSources.All();

        //  实测：可用的 5 个 + 已关站的经典 6 个（默认禁用）
        Assert.Equal(11, sources.Count);
        Assert.Equal(5, sources.Count(source => source.Enabled));
        Assert.Contains("saurik", sources.Where(source => source.Enabled).Select(source => source.Name));
        Assert.Contains("bigboss", sources.Where(source => source.Enabled).Select(source => source.Name));
        Assert.Contains("aptgs", sources.Where(source => source.Enabled).Select(source => source.Name));
        Assert.Contains("julioverne", sources.Where(source => source.Enabled).Select(source => source.Name));
        Assert.Contains("cokepokes", sources.Where(source => source.Enabled).Select(source => source.Name));

        Assert.Contains("modmyi", sources.Where(source => !source.Enabled).Select(source => source.Name));
        Assert.Contains("zodttd", sources.Where(source => !source.Enabled).Select(source => source.Name));
        Assert.All(sources, source => Assert.False(string.IsNullOrWhiteSpace(source.BaseUrl)));
        Assert.Equal(sources.Count, sources.Select(source => source.Priority).Distinct().Count());
    }

    [Fact]
    public void SchemaV4_AddsEnabledColumn_AndKeepsExistingReposEnabled()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);

        repository.SaveRepo(Source("a"));
        var disabled = Source("b") with { Enabled = false };
        repository.SaveRepo(disabled);

        var rows = repository.LoadRepos();
        Assert.True(rows.Single(row => row.Name == "a").Enabled);
        Assert.False(rows.Single(row => row.Name == "b").Enabled);

        // 只改优先级不应把禁用改回启用
        repository.SaveRepo(disabled with { Priority = 5 });
        Assert.False(repository.LoadRepos().Single(row => row.Name == "b").Enabled);
    }

    [Fact]
    public void SchemaV3_Migration_KeepsPoolRowsAndAddsRepoToKey()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Full("archive.db");

        // 手工造一个 v2 库：v1 表 + library 表 + archive_pool 的 repo 列（主键还是 3 列）
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            SchemaSql.CreateV1Schema(connection);
            SchemaSql.MigrateToV2(connection);

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO archive_pool(package_id, version, architecture, repo, rel_path, bytes)
                VALUES('wget','1.16-4','iphoneos-arm','saurik','wget/1.16-4_iphoneos-arm_saurik.deb',210290),
                      ('mobilesubstrate','0.9.6301','iphoneos-arm','aptgs','mobilesubstrate/x.deb',122128);
                """;
            insert.ExecuteNonQuery();
        }

        using var database = ArchiveDatabase.Open(path);

        Assert.Equal(SchemaSql.CurrentVersion, database.UserVersion);

        var store = new LibraryStore(database);
        var pool = store.LoadPool();
        Assert.Equal(2, pool.Count);

        // 主键现在含 repo：同包同版本不同源可以共存
        store.UpsertPool(
        [
            new LibraryPoolEntry("wget", "1.16-4", "iphoneos-arm", "saurik", "wget/a.deb", 1, null, null),
            new LibraryPoolEntry("wget", "1.16-4", "iphoneos-arm", "aptgs", "wget/b.deb", 2, null, null),
        ]);

        Assert.Equal(3, store.LoadPool().Count);
    }
}



