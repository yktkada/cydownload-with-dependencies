using System.IO;
using CyDownload.Core.Index;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 资料库去重（内容寻址）：真实相同的文件只存一份；旧布局迁移；文件名解析。
/// </summary>
public sealed class PoolStoreTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "debs", name);

    [Fact]
    public void SaveObject_WritesOnceAndReusesSameContent()
    {
        using var workspace = new TestWorkspace();
        var bytes = File.ReadAllBytes(FixturePath("ios6-gz.deb"));
        var sha = PoolStore.Sha256OfBytes(bytes);

        var first = PoolStore.SaveObject(workspace.Root, sha, bytes, TestContext.Current.CancellationToken);
        var second = PoolStore.SaveObject(workspace.Root, sha, bytes, TestContext.Current.CancellationToken);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.FullPath, second.FullPath);
        Assert.Equal(Path.Combine("objects", sha + ".deb"), first.RelativePath);
        Assert.Single(Directory.EnumerateFiles(workspace.Root, "*.deb", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void Migrate_MovesLegacyLayout_AndUpdatesRowPath()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));

        var pool = Path.Combine(workspace.Root, "pool");
        var legacyDir = Path.Combine(pool, "wget");
        Directory.CreateDirectory(legacyDir);
        var legacyFile = Path.Combine(legacyDir, "1.16-4_iphoneos-arm_saurik.deb");
        File.Copy(FixturePath("ios6-gz.deb"), legacyFile);

        var sha = PoolStore.Sha256OfFile(legacyFile);

        // 旧台账：rel_path 指向旧布局
        var store = new LibraryStore(database);
        store.UpsertPool(
        [
            new LibraryPoolEntry("wget", "1.16-4", "iphoneos-arm", "saurik",
                "wget/1.16-4_iphoneos-arm_saurik.deb", new FileInfo(legacyFile).Length, null, null),
        ]);

        var result = PoolStore.Migrate(database, pool);

        Assert.True(result.Moved == 1,
            $"files=[{string.Join("|", Directory.GetFiles(pool, "*", SearchOption.AllDirectories))}] moved={result.Moved} dedup={result.Deduped} ignored={result.Ignored}");
        Assert.Equal(1, result.RowsUpdated);
        Assert.False(Directory.Exists(legacyDir));                                   // 空目录已清
        var objectFile = Path.Combine(pool, "objects", sha + ".deb");
        Assert.True(File.Exists(objectFile));
        Assert.Single(Directory.EnumerateFiles(pool, "*.deb", SearchOption.AllDirectories));

        var entry = Assert.Single(store.LoadPool());
        Assert.Equal(Path.Combine("objects", sha + ".deb"), entry.RelativePath);
        Assert.Equal(sha, entry.Sha256);
    }

    [Fact]
    public void Migrate_IdenticalLegacyFiles_KeepsOnlyOneObject()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));

        var pool = Path.Combine(workspace.Root, "pool");
        var a = Path.Combine(pool, "wget");
        var b = Path.Combine(pool, "wget-mirror");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        // 同一份内容，放在两个「源」的旧布局里
        File.Copy(FixturePath("ios6-gz.deb"), Path.Combine(a, "1.16-4_iphoneos-arm_saurik.deb"));
        File.Copy(FixturePath("ios6-gz.deb"), Path.Combine(b, "1.16-4_iphoneos-arm_aptgs.deb"));

        var result = PoolStore.Migrate(database, pool);

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Deduped);
        Assert.Single(Directory.EnumerateFiles(pool, "*.deb", SearchOption.AllDirectories));

        var rows = new LibraryStore(database).LoadPool();
        Assert.Equal(2, rows.Count);                                  // 两行台账
        Assert.Single(rows.Select(row => row.RelativePath).Distinct()); // 同一个对象
    }

    [Fact]
    public void Migrate_UnparseableFileWithoutRow_IsKeptAndReported()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));

        var pool = Path.Combine(workspace.Root, "pool");
        Directory.CreateDirectory(pool);
        File.Copy(FixturePath("ios6-gz.deb"), Path.Combine(pool, "random-name.deb"));

        var result = PoolStore.Migrate(database, pool);

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Ignored);
        Assert.Single(Directory.EnumerateFiles(pool, "*.deb", SearchOption.AllDirectories));
        Assert.Empty(new LibraryStore(database).LoadPool());
    }

    [Theory]
    [InlineData("wget/1.16-4_iphoneos-arm_saurik.deb", "wget", "1.16-4", "iphoneos-arm", "saurik")]
    [InlineData("gettext/0.17-7_iphoneos-arm_aptgs.deb", "gettext", "0.17-7", "iphoneos-arm", "aptgs")]
    [InlineData("wget/1.16-4_saurik.deb", "wget", "1.16-4", "iphoneos-arm", "saurik")]
    public void TryParseLegacyName_ParsesBothLayouts(string path, string pkg, string ver, string arch, string repo)
    {
        Assert.True(PoolStore.TryParseLegacyName(path, out var packageId, out var version, out var architecture, out var parsedRepo));

        Assert.Equal(pkg, packageId);
        Assert.Equal(ver, version);
        Assert.Equal(arch, architecture);
        Assert.Equal(repo, parsedRepo);
    }

    [Theory]
    [InlineData("random-name.deb")]
    [InlineData("wget/README.deb")]
    public void TryParseLegacyName_RejectsUnknownShapes(string path)
    {
        Assert.False(PoolStore.TryParseLegacyName(path, out _, out _, out _, out _));
    }
}


