using CyDownload.Core.Deb;
using CyDownload.Core.Repos;
using Xunit;

namespace CyDownload.Tests;

/// <summary>Release 解析（正文取自 bigboss / apt.gs 实测）。</summary>
public sealed class ReleaseInfoTests
{
    private const string BigBossRelease = """
        Origin: BigBoss
        Label: BigBoss
        Suite: stable
        Version: 1.0
        Codename: BigBoss
        Support: https://cydia.saurik.com/api/support/*
        Architectures: iphoneos-arm iphoneos-arm64
        Components: main
        Description: For hosting your apps, see our website.
        MD5Sum:
         9aec85230f287825542fd5c5d9f37ea3 8247106 main/binary-iphoneos-arm/Packages
         7ab64976e117f75f3be5ba499b0d7b78 1315706 main/binary-iphoneos-arm/Packages.bz2
         c21cef11d93a156a6dc24fca805c6389 221169 main/binary-iphoneos-arm64/Packages
         a692b11367c14e47373daaecc34088cd 39334 main/binary-iphoneos-arm64/Packages.bz2
        SHA256:
         f9a6084258d473b359697055bcc637a6651211b172f8638b4cdc7b3f9ddd6edc 8247106 main/binary-iphoneos-arm/Packages
         168f9ad571e2199e2506a4a5779100eba1d93822cc02a6225a76a8c913df8aac 1315706 main/binary-iphoneos-arm/Packages.bz2
         8cd3e16bc8d6204c1d25df397769950f4f93cdcd011837ed5a5b85ae83d5749e 221169 main/binary-iphoneos-arm64/Packages
         9132c0ffb263f604ea1e98c1e869c775b88bdd547d09a518b3286610997a4a41 39334 main/binary-iphoneos-arm64/Packages.bz2
        """;

    private static ReleaseInfo Parse(string text) => ReleaseInfo.Parse(ControlFileParser.Parse(text).Single());

    [Fact]
    public void BigBoss_HasMetadataAndBothHashTables()
    {
        var release = Parse(BigBossRelease);

        Assert.Equal("BigBoss", release.Origin);
        Assert.Equal("stable", release.Suite);
        Assert.Equal("iphoneos-arm iphoneos-arm64", release.Architectures);
        Assert.Equal(4, release.FileCount);
    }

    [Fact]
    public void HashLookup_MergesMd5AndSha256()
    {
        var release = Parse(BigBossRelease);

        Assert.True(release.TryGetFileHash("main/binary-iphoneos-arm/Packages.bz2", out var hash));
        Assert.Equal(1_315_706, hash.Size);
        Assert.Equal("7ab64976e117f75f3be5ba499b0d7b78", hash.Md5);
        Assert.Equal("168f9ad571e2199e2506a4a5779100eba1d93822cc02a6225a76a8c913df8aac", hash.Sha256);
    }

    [Fact]
    public void HashLookup_IsCaseInsensitiveAndNormalizesSlashes()
    {
        var release = Parse(BigBossRelease);

        Assert.True(release.TryGetFileHash(@"MAIN\BINARY-IPHONEOS-ARM\PACKAGES.BZ2", out _));
    }

    [Fact]
    public void HashLookup_MissingPathReturnsFalse()
    {
        var release = Parse(BigBossRelease);
        Assert.False(release.TryGetFileHash("main/binary-iphoneos-arm/Packages.gz", out _));
    }

    [Fact]
    public void AptsRelease_WithoutHashTable_IsAvailableButEmpty()
    {
        // apt.gs 实测只有 325 B 元数据，没有校验表
        const string aptgs = """
            Origin: 狗屎源(iOSIPA软件网)
            Label: 狗屎源(iOSIPA软件网)
            Suite: stable
            Version: 1.2
            Architectures: iphoneos-arm
            Components: main
            """;

        var release = Parse(aptgs);

        Assert.Equal(0, release.FileCount);
        Assert.Equal("stable", release.Suite);
        Assert.False(release.TryGetFileHash("Packages.bz2", out _));
    }
}
