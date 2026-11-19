using CyDownload.Core.Repos;
using Xunit;

namespace CyDownload.Tests;

/// <summary>源定义与 URL 组装（纯逻辑，无网络）。</summary>
public sealed class RepoDescriptorTests
{
    private static RepoDescriptor Saurik() => new()
    {
        Name = "saurik",
        BaseUrl = "http://apt.saurik.com/",
        Layout = RepoLayout.Dists,
        Suite = "ios/793.00",
        Component = "main",
        Architecture = "iphoneos-arm",
        Priority = 1,
    };

    private static RepoDescriptor AptGs() => new()
    {
        Name = "aptgs",
        BaseUrl = "http://apt.gs",
        Layout = RepoLayout.Flat,
        Priority = 3,
    };

    [Fact]
    public void Dists_CandidatesAreBz2ThenGzThenPlain()
    {
        var candidates = Saurik().BuildIndexCandidates();

        Assert.Equal(3, candidates.Count);
        Assert.Equal("http://apt.saurik.com/dists/ios/793.00/main/binary-iphoneos-arm/Packages.bz2", candidates[0].Url);
        Assert.Equal("http://apt.saurik.com/dists/ios/793.00/main/binary-iphoneos-arm/Packages.gz", candidates[1].Url);
        Assert.Equal("http://apt.saurik.com/dists/ios/793.00/main/binary-iphoneos-arm/Packages", candidates[2].Url);
    }

    [Fact]
    public void Dists_RelativePathsMatchReleaseLayout()
    {
        var candidates = Saurik().BuildIndexCandidates();

        // Release 的校验表用的是 dists/<suite>/ 下的相对路径
        Assert.Equal("main/binary-iphoneos-arm/Packages.bz2", candidates[0].RelativePath);
        Assert.Equal("main/binary-iphoneos-arm/Packages", candidates[2].RelativePath);
    }

    [Fact]
    public void Flat_CandidatesAreAtRoot()
    {
        var candidates = AptGs().BuildIndexCandidates();

        Assert.Equal("http://apt.gs/Packages.bz2", candidates[0].Url);
        Assert.Equal("http://apt.gs/Packages", candidates[2].Url);
        Assert.Equal("Packages.bz2", candidates[0].RelativePath);
    }

    [Fact]
    public void BaseUrl_WithOrWithoutTrailingSlash_ProducesSameUrls()
    {
        var withSlash = Saurik();
        var withoutSlash = withSlash with { BaseUrl = "http://apt.saurik.com" };

        Assert.Equal(
            withSlash.BuildIndexCandidates().Select(c => c.Url),
            withoutSlash.BuildIndexCandidates().Select(c => c.Url));
    }

    [Fact]
    public void ReleaseUrl_DependsOnLayout()
    {
        Assert.Equal("http://apt.saurik.com/dists/ios/793.00/Release", Saurik().BuildReleaseUrl());
        // apt.gs 是扁平布局但**根目录仍有 Release**（实测 325 B）
        Assert.Equal("http://apt.gs/Release", AptGs().BuildReleaseUrl());
    }

    [Fact]
    public void Dists_WithoutSuite_Throws()
    {
        var broken = Saurik() with { Suite = null };
        Assert.Throws<InvalidOperationException>(() => broken.BuildIndexCandidates());
        Assert.Throws<InvalidOperationException>(() => broken.BuildReleaseUrl());
    }

    [Fact]
    public void SaurikSuite_IsNotHardcodedStable()
    {
        // 原项目硬编码 dists/stable/... → 对 saurik 失效（实测 404）。
        // 这里钉住：suite 必须来自配置。
        var candidates = Saurik().BuildIndexCandidates();
        Assert.DoesNotContain(candidates, c => c.Url.Contains("/dists/stable/", StringComparison.Ordinal));
    }
}
