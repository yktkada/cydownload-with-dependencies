using CyDownload.Core.Deb;
using CyDownload.Core.Index;
using CyDownload.Core.Resolve;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 依赖闭包求解。用内存候选源，把「候选排序 / 备选 / 虚拟包 / 回溯 / 环 / 上限」逐条钉住。
/// </summary>
public sealed class DependencyResolverTests
{
    private static PackageRow Pkg(
        string id,
        string version,
        string? depends = null,
        string? preDepends = null,
        string? provides = null,
        long size = 1000,
        string repo = "test")
        => new(
            Id: 0,
            PackageId: id,
            Name: null,
            Version: version,
            Architecture: "iphoneos-arm",
            Section: null,
            Maintainer: null,
            Author: null,
            Website: null,
            Depiction: null,
            Description: null,
            Depends: depends,
            PreDepends: preDepends,
            Provides: provides,
            Tags: null,
            Filename: $"debs/{id}_{version}.deb",
            Size: size,
            InstalledSize: null,
            Md5: null,
            Sha256: null,
            Repo: repo);

    private static DependencyResolver Resolver(
        InMemoryPackageSource source,
        bool usePreset = false,
        TargetProfile? target = null)
        => new(source, new BasePackageProvider(target ?? TargetProfile.IPhone4S613, installed: null, usePreset: usePreset));

    [Fact]
    public void PinnedTarget_SelectsRequestedVersion_NotHighest()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("coolbooter", "1.5-release"))
            .Add(Pkg("coolbooter", "1.6-release"));

        var unpinned = Resolver(source).Resolve(["coolbooter"]);
        Assert.True(unpinned.Success);
        Assert.Equal("1.6-release", unpinned.ToDownload.Single(item => item.IsTarget).Version);   // 默认挑最高

        var pinned = Resolver(source).Resolve(["coolbooter"], new ResolveOptions
        {
            PinnedTargets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["coolbooter"] = "1.5-release",
            },
        });

        Assert.True(pinned.Success);
        Assert.Equal("1.5-release", pinned.ToDownload.Single(item => item.IsTarget).Version);
    }

    [Fact]
    public void PinnedTarget_MissingVersion_Fails()
    {
        var source = new InMemoryPackageSource().Add(Pkg("coolbooter", "1.6-release"));

        var result = Resolver(source).Resolve(["coolbooter"], new ResolveOptions
        {
            PinnedTargets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["coolbooter"] = "1.5-release",
            },
        });

        Assert.False(result.Success);
    }

    [Fact]
    public void SimpleChain_AllDownloaded()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("a", "1.0", depends: "b"))
            .Add(Pkg("b", "1.0", depends: "c"))
            .Add(Pkg("c", "1.0"));

        var result = Resolver(source).Resolve(["a"]);

        Assert.True(result.Success);
        Assert.Equal(["a", "b", "c"], result.ToDownload.Select(item => item.PackageId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Empty(result.Unresolved);
        Assert.Single(result.ToDownload, item => item.IsTarget);
    }

    [Fact]
    public void BaseSatisfiedDependency_IsNotDownloaded()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("tweak", "1.0", depends: "dpkg (>= 1.14.25-8), bash"));

        var result = Resolver(source, usePreset: true).Resolve(["tweak"]);

        Assert.True(result.Success);
        Assert.Single(result.ToDownload);                       // 只下 tweak 自己
        Assert.Contains(result.Satisfied, item => item.Name == "dpkg" && item.Source == "base");
        Assert.Contains(result.Satisfied, item => item.Name == "bash" && item.Source == "base");
    }

    [Fact]
    public void HighestVersion_IsPreferred_AndCappedByOption()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("dep", "1.0"))
            .Add(Pkg("dep", "2.0"))
            .Add(Pkg("dep", "3.0"))
            .Add(Pkg("target", "1.0", depends: "dep"));

        var result = Resolver(source).Resolve(["target"]);

        Assert.Equal("3.0", result.ToDownload.Single(item => item.PackageId == "dep").Version);
    }

    [Fact]
    public void SameVersionInTwoRepos_PrefersLowerPriorityNumber()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("dep", "1.0", size: 111, repo: "aptgs"), repoPriority: 3)
            .Add(Pkg("dep", "1.0", size: 222, repo: "saurik"), repoPriority: 1)
            .Add(Pkg("target", "1.0", depends: "dep"));

        var result = Resolver(source).Resolve(["target"]);

        var dep = result.ToDownload.Single(item => item.PackageId == "dep");
        Assert.Equal("saurik", dep.Package.Repo);
        Assert.Equal(222, dep.Package.Size);
    }

    [Fact]
    public void Alternatives_PickTheOneThatExists()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("target", "1.0", depends: "missing | real"))
            .Add(Pkg("real", "1.0"));

        var result = Resolver(source).Resolve(["target"]);

        Assert.True(result.Success);
        Assert.Contains(result.ToDownload, item => item.PackageId == "real");
    }

    [Fact]
    public void Alternatives_PreferBaseSatisfiedBranch()
    {
        // 实测字段：uikittools 依赖 "firmware (<< 8.3) | com.saurik.patcyh"
        var source = new InMemoryPackageSource()
            .Add(Pkg("uikittools", "1.1.12", depends: "firmware (<< 8.3) | com.saurik.patcyh"));

        var result = Resolver(source).Resolve(["uikittools"]);

        Assert.True(result.Success);
        Assert.Single(result.ToDownload);      // firmware 由伪包满足，不需要 patcyh
        Assert.Contains(result.Satisfied, item => item.Name == "firmware" && item.Source == "pseudo");
    }

    [Fact]
    public void Provides_VirtualPackageIsResolved()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("target", "1.0", depends: "apt7-ssl"))
            .Add(Pkg("apt7-lib", "0.7.25.3-16", provides: "apt7-ssl"));

        var result = Resolver(source).Resolve(["target"]);

        Assert.True(result.Success);
        Assert.Contains(result.ToDownload, item => item.PackageId == "apt7-lib");
    }

    [Fact]
    public void VersionedProvides_SatisfiesConstraint_UnversionedDoesNot()
    {
        var versioned = new InMemoryPackageSource()
            .Add(Pkg("target", "1.0", depends: "virtual (>= 1.0)"))
            .Add(Pkg("provider", "9.9", provides: "virtual (= 1.5)"));
        Assert.True(Resolver(versioned).Resolve(["target"]).Success);

        var unversioned = new InMemoryPackageSource()
            .Add(Pkg("target", "1.0", depends: "virtual (>= 1.0)"))
            .Add(Pkg("provider", "9.9", provides: "virtual"));
        Assert.False(Resolver(unversioned).Resolve(["target"]).Success);
    }

    [Fact]
    public void Backtracking_FallsBackToOlderVersionWhenHighestIsUnsatisfiable()
    {
        // a 2.0 需要 b (>= 2.0)（不存在）；a 1.0 只需要 b (>= 1.0)（b 1.5 存在）
        var source = new InMemoryPackageSource()
            .Add(Pkg("a", "2.0", depends: "b (>= 2.0)"))
            .Add(Pkg("a", "1.0", depends: "b (>= 1.0)"))
            .Add(Pkg("b", "1.5"));

        var result = Resolver(source).Resolve(["a"]);

        Assert.True(result.Success);
        Assert.Equal("1.0", result.ToDownload.Single(item => item.PackageId == "a").Version);
        Assert.Equal("1.5", result.ToDownload.Single(item => item.PackageId == "b").Version);
    }

    [Fact]
    public void RealCycle_DoesNotHang_AndBothAreSelected()
    {
        // 真实环：mobilesubstrate ↔ com.saurik.substrate.safemode
        var source = new InMemoryPackageSource()
            .Add(Pkg("mobilesubstrate", "0.9.6301", preDepends: "dpkg (>= 1.14.25-8)", depends: "com.saurik.substrate.safemode"))
            .Add(Pkg("com.saurik.substrate.safemode", "0.9.6005", depends: "mobilesubstrate (>= 0.9.3367+38)"));

        var result = Resolver(source, usePreset: true).Resolve(["mobilesubstrate"]);

        Assert.True(result.Success);
        Assert.Equal(2, result.ToDownload.Count);
        Assert.Contains(result.Cycles, cycle => cycle.Contains("mobilesubstrate", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDependency_FailsWithDiagnosis()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("target", "1.0", depends: "nope"));

        var result = Resolver(source).Resolve(["target"]);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains(result.Unresolved, item => item.Text.Contains("nope", StringComparison.Ordinal));
        Assert.Empty(result.ToDownload);
    }

    [Fact]
    public void UnknownTarget_Fails()
    {
        var result = Resolver(new InMemoryPackageSource()).Resolve(["does-not-exist"]);

        Assert.False(result.Success);
        Assert.Contains(result.Unresolved, item => item.Text.Contains("does-not-exist", StringComparison.Ordinal));
    }

    [Fact]
    public void NodeLimit_StopsInsteadOfHanging()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("a", "1.0", depends: "b"))
            .Add(Pkg("b", "1.0", depends: "c"))
            .Add(Pkg("c", "1.0", depends: "d"))
            .Add(Pkg("d", "1.0"));

        var result = Resolver(source).Resolve(["a"], new ResolveOptions { MaxNodes = 2 });

        Assert.False(result.Success);
        Assert.Contains("上限", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleTargets_ShareDependencies()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("x", "1.0", depends: "common"))
            .Add(Pkg("y", "1.0", depends: "common"))
            .Add(Pkg("common", "1.0"));

        var result = Resolver(source).Resolve(["x", "y"]);

        Assert.True(result.Success);
        Assert.Equal(3, result.ToDownload.Count);
        Assert.Equal(2, result.ToDownload.Count(item => item.IsTarget));
        Assert.Single(result.ToDownload, item => item.PackageId == "common");
    }

    [Fact]
    public void UnparsableDependency_IsReportedNotSilentlyDropped()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("target", "1.0", depends: "((broken))"));

        var result = Resolver(source).Resolve(["target"]);

        Assert.False(result.Success);
        // 目标自身的依赖写坏了 → 不可选；诊断要说明原因，而不是只说「找不到包」
        Assert.Contains(result.Unresolved, item =>
            item.Text.Contains("target", StringComparison.Ordinal) &&
            item.Reason.Contains("无法解析", StringComparison.Ordinal));
    }

    [Fact]
    public void TotalBytes_SumsPackageSizes()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("a", "1.0", depends: "b", size: 100))
            .Add(Pkg("b", "1.0", size: 250));

        var result = Resolver(source).Resolve(["a"]);

        Assert.Equal(350, result.TotalBytes);
    }

    [Fact]
    public void InvalidVersionCandidate_IsSkipped()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("dep", "not-a-version!"))
            .Add(Pkg("dep", "1.0"))
            .Add(Pkg("target", "1.0", depends: "dep"));

        var result = Resolver(source).Resolve(["target"]);

        Assert.True(result.Success);
        Assert.Equal("1.0", result.ToDownload.Single(item => item.PackageId == "dep").Version);
    }
}

