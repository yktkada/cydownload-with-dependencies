using CyDownload.Core.Index;
using Xunit;

namespace CyDownload.Tests;

/// <summary>包身份四元查找（：同包同版本多源导致「导出这一组」抛重复键）。</summary>
public sealed class PackageLookupTests
{
    [Fact]
    public void Build_HandlesSamePackageVersionFromTwoRepos()
    {
        var rows = new[]
        {
            Row("app.iflytek.iflyinput", "4.1.1315", "iphoneos-arm", "saurik"),
            Row("app.iflytek.iflyinput", "4.1.1315", "iphoneos-arm", "aptgs"),
        };

        var lookup = PackageLookup.Build(rows);

        Assert.Equal(2, lookup.Count);
        Assert.Equal("saurik", lookup.Find("app.iflytek.iflyinput", "4.1.1315", "iphoneos-arm", "saurik")?.Repo);
        Assert.Equal("aptgs", lookup.Find("app.iflytek.iflyinput", "4.1.1315", "iphoneos-arm", "aptgs")?.Repo);
        Assert.NotNull(lookup.Find("app.iflytek.iflyinput", "4.1.1315", "iphoneos-arm", "unknown"));   // 名+版本回退
    }

    private static PackageRow Row(string id, string version, string arch, string repo)
        => new(Id: 0, PackageId: id, Name: id, Version: version, Architecture: arch,
            Section: null, Maintainer: null, Author: null, Website: null, Depiction: null,
            Description: null, Depends: null, PreDepends: null, Provides: null, Tags: null,
            Filename: null, Size: 1, InstalledSize: null, Md5: null, Sha256: null, Repo: repo);
}
