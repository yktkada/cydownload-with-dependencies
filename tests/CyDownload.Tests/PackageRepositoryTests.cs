using System.IO;
using System.IO.Compression;
using System.Text;
using CyDownload.Core.Deb;
using CyDownload.Core.Fetch;
using CyDownload.Core.Index;
using CyDownload.Core.Repos;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 落库：真实 saurik 索引（364 包）→ `packages` / `provides` / `repos`。
/// 重点钉住：幂等、20 列映射、多行描述、provides 反查、架构过滤、坏行跳过、单事务。
/// </summary>
public sealed class PackageRepositoryTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 11, 12, 21, 0, 0, TimeSpan.Zero);

    private static RepoDescriptor TestRepo(string name) => new()
    {
        Name = name,
        BaseUrl = "http://" + name + ".invalid",
        Layout = RepoLayout.Flat,
    };

    private static RepoDescriptor Saurik() => new()
    {
        Name = "saurik",
        BaseUrl = "http://apt.saurik.com/",
        Layout = RepoLayout.Dists,
        Suite = "ios/793.00",
        Component = "main",
        Priority = 1,
    };

    private static IReadOnlyList<ControlStanza> SaurikStanzas()
    {
        using var raw = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "saurik-Packages.bz2"));
        using var decompressed = CompressionSniffer.OpenDecompressor(raw, CompressionKind.Bzip2);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);
        return ControlFileParser.Parse(reader);
    }

    private static (ArchiveDatabase Db, PackageRepository Repo) Open(TestWorkspace workspace)
    {
        var db = ArchiveDatabase.Open(workspace.Full("archive.db"));
        return (db, new PackageRepository(db));
    }

    [Fact]
    public void Import_RealSaurikIndex_StoresAllPackages()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        var stanzas = SaurikStanzas();
        Assert.Equal(364, stanzas.Count);

        var result = repository.Import(Saurik(), stanzas, "iphoneos-arm", FetchedAt);

        Assert.Equal(364, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(364, result.TotalAfterImport);
        Assert.True(result.Provides > 20, $"provides 条数异常：{result.Provides}");
        Assert.Equal(364, repository.CountPackages("saurik"));
    }

    [Fact]
    public void Import_IsIdempotent()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        var stanzas = SaurikStanzas();
        var first = repository.Import(Saurik(), stanzas, "iphoneos-arm", FetchedAt);
        var second = repository.Import(Saurik(), stanzas, "iphoneos-arm", FetchedAt.AddMinutes(5));

        Assert.Equal(0, second.Inserted);
        Assert.Equal(364, second.Updated);
        Assert.Equal(364, second.TotalAfterImport);
        Assert.Equal(first.Provides, second.Provides);        // 不会越插越多
        Assert.Equal(364, repository.CountPackages("saurik"));
    }

    [Fact]
    public void Import_MapsAllTwentyColumns()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        repository.Import(Saurik(), SaurikStanzas(), "iphoneos-arm", FetchedAt);

        var row = repository.Find("mobilesubstrate", "0.9.6301", "iphoneos-arm", "saurik");

        Assert.NotNull(row);
        Assert.Equal("Cydia Substrate", row!.Name);
        Assert.Equal("System", row.Section);
        Assert.Equal("com.saurik.substrate.safemode", row.Depends);
        Assert.Equal("dpkg (>= 1.14.25-8)", row.PreDepends);
        Assert.Equal("debs/mobilesubstrate_0.9.6301_iphoneos-arm.deb", row.Filename);
        Assert.Equal(122_342, row.Size);
        Assert.Equal("f2ca15c08aab522d8b7e842238aa83c6", row.Md5);
        Assert.Equal("saurik", row.Repo);
        Assert.True(row.Id > 0);
    }

    [Fact]
    public void Import_PreservesMultiLineDescription()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        repository.Import(Saurik(), SaurikStanzas(), "iphoneos-arm", FetchedAt);

        var gdb = repository.LoadAll("saurik").Single(r => r.PackageId == "gdb");

        Assert.NotNull(gdb.Description);
        Assert.Contains('\n', gdb.Description!);
        Assert.Contains("A debugger for native software from GNU.", gdb.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_KeepsNullNameWhenIndexHasNoNameField()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        repository.Import(Saurik(), SaurikStanzas(), "iphoneos-arm", FetchedAt);

        var adv = repository.LoadAll("saurik").Single(r => r.PackageId == "adv-cmds");
        Assert.Null(adv.Name);            // 索引里确实没有 Name 字段
    }

    [Fact]
    public void Provides_AreQueryableByName()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        repository.Import(Saurik(), SaurikStanzas(), "iphoneos-arm", FetchedAt);

        // coreutils-bin 0.8.12-9 提供 dirname / kill / mktemp / su
        var providers = repository.FindByProvidedName("dirname");
        Assert.Contains(providers, row => row.PackageId == "coreutils-bin");

        // apt7-lib 提供 apt7-ssl（虚拟包，无版本）
        var ssl = repository.FindByProvidedName("apt7-ssl");
        Assert.Contains(ssl, row => row.PackageId == "apt7-lib");

        // 未提供的名字应为空
        Assert.Empty(repository.FindByProvidedName("no-such-provided-name"));
    }

    [Fact]
    public void Import_FiltersOtherArchitectures()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        // saurik 全库都是 iphoneos-arm；用 arm64 导入 → 全部跳过
        var result = repository.Import(Saurik(), SaurikStanzas(), "iphoneos-arm64", FetchedAt);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(364, result.Skipped);
        Assert.Equal(0, repository.CountPackages("saurik"));
    }

    [Fact]
    public void Import_SkipsBadStanzasWithoutAborting()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        var stanzas = ControlFileParser.Parse("""
            Package: good
            Version: 1.0
            Architecture: iphoneos-arm

            Package: missing-version
            Architecture: iphoneos-arm

            Package: no-version-or-arch
            """);

        var result = repository.Import(Saurik(), stanzas, "iphoneos-arm", FetchedAt);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(2, result.Skipped);
        Assert.NotNull(repository.Find("good", "1.0", "iphoneos-arm", "saurik"));
    }

    [Fact]
    public void Import_UpdatesRepoRowWithStats()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        repository.Import(Saurik(), SaurikStanzas(), "iphoneos-arm", FetchedAt);

        var (count, suite, layout, lastFetch) = db.Read(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT pkg_count, suite, layout, last_fetch FROM repos WHERE name = 'saurik';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        });

        Assert.Equal(364, count);
        Assert.Equal("ios/793.00", suite);
        Assert.Equal("dists", layout);
        Assert.StartsWith("", lastFetch, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_TwoRepos_CoexistAndAreFiltered()
    {
        using var workspace = new TestWorkspace();
        var (db, repository) = Open(workspace);
        using var _ = db;

        var stanzas = ControlFileParser.Parse("""
            Package: shared
            Version: 1.0
            Architecture: iphoneos-arm
            """);

        var aptgs = new RepoDescriptor { Name = "aptgs", BaseUrl = "http://apt.gs", Layout = RepoLayout.Flat };

        repository.Import(Saurik(), stanzas, "iphoneos-arm", FetchedAt);
        repository.Import(aptgs, stanzas, "iphoneos-arm", FetchedAt);

        Assert.Equal(1, repository.CountPackages("saurik"));
        Assert.Equal(1, repository.CountPackages("aptgs"));
        Assert.Equal(2, repository.CountPackages());

        // 同一个包名在两个源各有 1 行（唯一键含 repo）
        Assert.NotNull(repository.Find("shared", "1.0", "iphoneos-arm", "saurik"));
        Assert.NotNull(repository.Find("shared", "1.0", "iphoneos-arm", "aptgs"));
    }


    [Fact]
    public void LoadAll_WithoutRepo_ReturnsPackagesFromEverySource()
    {
        // 「全部」合并页签用：不传 repo = 所有源并集
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);

        repository.Import(TestRepo("a"), ControlFileParser.Parse("""
            Package: pkg-a
            Version: 1.0
            Architecture: iphoneos-arm
            """), "iphoneos-arm", DateTimeOffset.Now);

        repository.Import(TestRepo("b"), ControlFileParser.Parse("""
            Package: pkg-b
            Version: 2.0
            Architecture: iphoneos-arm
            """), "iphoneos-arm", DateTimeOffset.Now);

        var all = repository.LoadAll();

        Assert.Equal(2, all.Count);
        Assert.Equal(["pkg-a", "pkg-b"], all.Select(row => row.PackageId));
        Assert.Equal(["a", "b"], all.Select(row => row.Repo));
    }

}


