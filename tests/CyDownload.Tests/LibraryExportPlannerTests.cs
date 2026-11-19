using System.IO;
using CyDownload.App.Infrastructure;
using CyDownload.Core.Index;
using Xunit;

namespace CyDownload.Tests;

/// <summary>资料库导出规划（：同包同版本多源曾导致导出抛重复键；编号要按拓扑序）。</summary>
public sealed class LibraryExportPlannerTests
{
    [Fact]
    public void Build_HandlesSamePackageVersionFromTwoRepos()
    {
        var items = new[]
        {
            Item("app.iflytek.iflyinput", "4.1.1315", "saurik", 0),
            Item("app.iflytek.iflyinput", "4.1.1315", "aptgs", 1),
        };
        var pool = new[]
        {
            Pool("app.iflytek.iflyinput", "4.1.1315", "saurik"),
            Pool("app.iflytek.iflyinput", "4.1.1315", "aptgs"),
        };

        var files = LibraryExportPlanner.Build(items, pool, Lookup(pool), @"D:\pool");

        Assert.Equal(2, files.Count);            // 两个源各一份（身份四元不同）
    }

    [Fact]
    public void Build_NumbersFilesInTopologicalOrder()
    {
        // b 依赖 a → 导出编号 a 在前
        var items = new[]
        {
            Item("b", "1.0", "saurik", 0),
            Item("a", "1.0", "saurik", 1),
        };
        var pool = new[] { Pool("b", "1.0", "saurik"), Pool("a", "1.0", "saurik") };
        var lookup = PackageLookup.Build(new[]
        {
            Row("a", "1.0", "saurik", depends: null),
            Row("b", "1.0", "saurik", depends: "a"),
        });

        var files = LibraryExportPlanner.Build(items, pool, lookup, @"D:\pool");

        Assert.Equal(["a", "b"], files.Select(f => f.PackageId));
    }

    [Fact]
    public void Build_SkipsMembersWithoutPoolRow()
    {
        var items = new[] { Item("a", "1.0", "saurik", 0), Item("b", "1.0", "saurik", 1) };
        var pool = new[] { Pool("a", "1.0", "saurik") };

        var files = LibraryExportPlanner.Build(items, pool, Lookup(pool), @"D:\pool");

        Assert.Single(files);
        Assert.Equal("a", files[0].PackageId);
    }

    private static LibraryGroupItem Item(string id, string version, string repo, int pos)
        => new(PackageId: id, Version: version, Architecture: "iphoneos-arm", Repo: repo, IsRoot: pos == 0, Position: pos);

    private static LibraryPoolEntry Pool(string id, string version, string repo)
        => new(id, version, "iphoneos-arm", repo, Path.Combine("objects", id + "-" + repo + ".deb"), 10, null, null);

    private static PackageLookup Lookup(IEnumerable<LibraryPoolEntry> pool)
        => PackageLookup.Build(pool.Select(entry => Row(entry.PackageId, entry.Version, entry.Repo, depends: null)));

    private static PackageRow Row(string id, string version, string repo, string? depends)
        => new(Id: 0, PackageId: id, Name: id, Version: version, Architecture: "iphoneos-arm",
            Section: null, Maintainer: null, Author: null, Website: null, Depiction: null,
            Description: null, Depends: depends, PreDepends: null, Provides: null, Tags: null,
            Filename: null, Size: 10, InstalledSize: null, Md5: null, Sha256: null, Repo: repo);
}
