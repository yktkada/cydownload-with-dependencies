using CyDownload.Core.Export;
using CyDownload.Core.Index;
using Xunit;

namespace CyDownload.Tests;

/// <summary>拓扑排序：依赖在前、同层有序、环不丢。</summary>
public sealed class TopologicalSorterTests
{
    private static PackageRow Pkg(
        string id,
        string version = "1.0",
        string? depends = null,
        string? provides = null,
        string repo = "test")
        => new(
            0, id, null, version, "iphoneos-arm", null, null, null, null, null, null,
            depends, null, provides, null, $"debs/{id}.deb", 100, null, null, null, repo);

    [Fact]
    public void Chain_DependenciesComeFirst()
    {
        var packages = new[]
        {
            Pkg("a", depends: "b"),
            Pkg("b", depends: "c"),
            Pkg("c"),
        };

        var result = TopologicalSorter.Sort(packages);

        Assert.Equal(["c", "b", "a"], result.Ordered.Select(item => item.PackageId));
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void SameLayer_IsSortedByPackageId()
    {
        var packages = new[]
        {
            Pkg("zeta", depends: "base-lib"),
            Pkg("alpha", depends: "base-lib"),
            Pkg("base-lib"),
        };

        var result = TopologicalSorter.Sort(packages);

        Assert.Equal("base-lib", result.Ordered[0].PackageId);
        Assert.Equal(["alpha", "zeta"], result.Ordered.Skip(1).Select(item => item.PackageId));
    }

    [Fact]
    public void Diamond_DependencySharedOnce()
    {
        var packages = new[]
        {
            Pkg("app", depends: "left, right"),
            Pkg("left", depends: "common"),
            Pkg("right", depends: "common"),
            Pkg("common"),
        };

        var result = TopologicalSorter.Sort(packages);

        Assert.Equal(4, result.Ordered.Count);
        Assert.Equal("common", result.Ordered[0].PackageId);
        Assert.Equal("app", result.Ordered[^1].PackageId);
    }

    [Fact]
    public void DuplicateRows_AreCollapsed()
    {
        var packages = new[] { Pkg("a"), Pkg("a"), Pkg("b", depends: "a") };

        var result = TopologicalSorter.Sort(packages);

        Assert.Equal(2, result.Ordered.Count);
    }

    [Fact]
    public void RealCycle_BothKept_AndReported()
    {
        var packages = new[]
        {
            Pkg("mobilesubstrate", depends: "com.saurik.substrate.safemode"),
            Pkg("com.saurik.substrate.safemode", depends: "mobilesubstrate (>= 0.9.3367+38)"),
        };

        var result = TopologicalSorter.Sort(packages);

        Assert.Equal(2, result.Ordered.Count);
        Assert.NotEmpty(result.Cycles);
        Assert.Contains(result.Cycles, label => label.Contains("mobilesubstrate", StringComparison.Ordinal));
    }

    [Fact]
    public void Provides_AlsoCreatesEdges()
    {
        var packages = new[]
        {
            Pkg("apt7-lib", provides: "apt7-ssl"),
            Pkg("consumer", depends: "apt7-ssl"),
        };

        var result = TopologicalSorter.Sort(packages);

        Assert.Equal("apt7-lib", result.Ordered[0].PackageId);
    }

    [Fact]
    public void DependenciesOutsideTheSet_AreIgnored()
    {
        // 依赖 base 里的包（不在集合内）不该产生边，也不该报错
        var packages = new[] { Pkg("tweak", depends: "dpkg (>= 1.14.25-8), mobilesubstrate") };

        var result = TopologicalSorter.Sort(packages);

        Assert.Single(result.Ordered);
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void EmptyInput_IsEmpty()
    {
        var result = TopologicalSorter.Sort([]);
        Assert.Empty(result.Ordered);
        Assert.Empty(result.Cycles);
    }
}
