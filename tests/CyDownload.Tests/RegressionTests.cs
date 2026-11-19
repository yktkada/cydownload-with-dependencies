using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CyDownload.Core.Deb;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Index;
using CyDownload.Core.Net;
using CyDownload.Core.Repos;
using CyDownload.Core.Resolve;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
///  增量（F3/F3-b/F4）的回归用例：每条对应报告里的一个 P2 / P3 编号。
/// 报告：`。
/// </summary>
public sealed class RegressionTests
{
    // ── 脏版本串 + 版本约束 → 不许抛，只跳过 ──────────────────────────

    private static PackageRow Pkg(
        string id,
        string version,
        string? depends = null,
        string? preDepends = null,
        string? provides = null,
        long size = 1000,
        string repo = "test",
        string arch = "iphoneos-arm")
        => new(
            0, id, null, version, arch, null, null, null, null, null, null,
            depends, preDepends, provides, null, $"debs/{id}_{version}.deb", size, null, null, null, repo);

    [Fact]
    public void DirtyVersionWithVersionConstraint_IsSkippedNotThrown()
    {
        // 真实库里就有 5 行这种版本串（1.0_biuld1008 等，来自 apt.gs）
        var source = new InMemoryPackageSource()
            .Add(Pkg("com.tencent.qqinput", "1.0_biuld1008"))
            .Add(Pkg("com.tencent.qqinput", "2.0"))
            .Add(Pkg("target", "1.0", depends: "com.tencent.qqinput (>= 1.5)"));

        var result = new DependencyResolver(source, new BasePackageProvider(TargetProfile.IPhone4S613))
            .Resolve(["target"]);

        Assert.True(result.Success);
        Assert.Equal("2.0", result.ToDownload.Single(item => item.PackageId == "com.tencent.qqinput").Version);
    }

    // ── firmware 永远不从源里取 ──────────────────────────────────────

    [Fact]
    public void FirmwarePackageInSource_IsNeverSelected()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("firmware", "7.0"))                      // 万一某个源真的列出了它
            .Add(Pkg("needs-7", "1.0", depends: "firmware (>= 7.0)"));

        var result = new DependencyResolver(source, new BasePackageProvider(TargetProfile.IPhone4S613))
            .Resolve(["needs-7"]);

        Assert.False(result.Success);                          // 6.1.3 不满足 >= 7.0，且不许下载 firmware
        Assert.DoesNotContain(result.ToDownload, item => item.PackageId == "firmware");
    }

    [Fact]
    public void FirmwareWithoutConstraint_IsSatisfiedByBase()
    {
        var source = new InMemoryPackageSource().Add(Pkg("dummy", "1.0", depends: "firmware"));

        var result = new DependencyResolver(source, new BasePackageProvider(TargetProfile.IPhone4S613))
            .Resolve(["dummy"]);

        Assert.True(result.Success);
        Assert.Contains(result.Satisfied, item => item.Name == "firmware" && item.Source == "pseudo");
    }

    // ── 目标架构过滤 ─────────────────────────────────────────────────

    [Fact]
    public void InMemorySource_FiltersByArchitecture()
    {
        var source = new InMemoryPackageSource(["iphoneos-arm"])
            .Add(Pkg("dep", "2.0", arch: "iphoneos-arm64"))    // 版本更高但架构不符
            .Add(Pkg("dep", "1.0", arch: "iphoneos-arm"))
            .Add(Pkg("target", "1.0", depends: "dep"));

        var result = new DependencyResolver(source, new BasePackageProvider(TargetProfile.IPhone4S613))
            .Resolve(["target"]);

        Assert.True(result.Success);
        var dep = result.ToDownload.Single(item => item.PackageId == "dep");
        Assert.Equal("1.0", dep.Version);
        Assert.Equal("iphoneos-arm", dep.Package.Architecture);
    }

    [Fact]
    public void RepositoryCandidates_FilterByArchitecture()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);
        var repo = new RepoDescriptor
        {
            Name = "multi", BaseUrl = "http://example.invalid", Layout = RepoLayout.Flat,
        };

        var stanzas = ControlFileParser.Parse("""
            Package: dep
            Version: 1.0
            Architecture: iphoneos-arm

            Package: dep
            Version: 2.0
            Architecture: iphoneos-arm64
            """);

        repository.Import(repo, stanzas, "iphoneos-arm", DateTimeOffset.Now);
        repository.Import(repo, stanzas, "iphoneos-arm64", DateTimeOffset.Now);

        var arm = repository.CandidatesByName("dep", ["iphoneos-arm"]);
        Assert.Single(arm);
        Assert.Equal("1.0", arm[0].Version);

        var both = repository.CandidatesByName("dep");
        Assert.Equal(2, both.Count);
    }

    // ── 重导清理下架行 ───────────────────────────────────────────────

    [Fact]
    public void Reimport_RemovesRowsNoLongerInIndex()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);
        var repo = new RepoDescriptor { Name = "r", BaseUrl = "http://example.invalid", Layout = RepoLayout.Flat };

        var first = ControlFileParser.Parse("""
            Package: a
            Version: 1.0
            Architecture: iphoneos-arm

            Package: b
            Version: 1.0
            Architecture: iphoneos-arm

            Package: c
            Version: 1.0
            Architecture: iphoneos-arm
            """);
        repository.Import(repo, first, "iphoneos-arm", DateTimeOffset.Now);
        Assert.Equal(3, repository.CountPackages("r"));

        // 上游下架 b、c
        var second = ControlFileParser.Parse("""
            Package: a
            Version: 1.0
            Architecture: iphoneos-arm
            """);
        var result = repository.Import(repo, second, "iphoneos-arm", DateTimeOffset.Now);

        Assert.Equal(1, result.TotalAfterImport);
        Assert.Equal(2, result.Removed);
        Assert.Equal(1, repository.CountPackages("r"));
        Assert.NotNull(repository.Find("a", "1.0", "iphoneos-arm", "r"));
        Assert.Null(repository.Find("b", "1.0", "iphoneos-arm", "r"));
    }

    [Fact]
    public void RemovingPackages_AlsoRemovesTheirProvides()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);
        var repo = new RepoDescriptor { Name = "r", BaseUrl = "http://example.invalid", Layout = RepoLayout.Flat };

        repository.Import(repo, ControlFileParser.Parse("""
            Package: coreutils-bin
            Version: 8.12-9
            Architecture: iphoneos-arm
            Provides: dirname
            """), "iphoneos-arm", DateTimeOffset.Now);

        Assert.NotEmpty(repository.FindByProvidedName("dirname"));

        repository.Import(repo, ControlFileParser.Parse("""
            Package: other
            Version: 1.0
            Architecture: iphoneos-arm
            """), "iphoneos-arm", DateTimeOffset.Now);

        Assert.Empty(repository.FindByProvidedName("dirname"));
    }

    // ── 每源伪造头落库 ───────────────────────────────────────────────

    [Fact]
    public void HeaderOverrides_ArePersisted()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);

        var repo = new RepoDescriptor
        {
            Name = "r",
            BaseUrl = "http://example.invalid",
            Layout = RepoLayout.Flat,
            HeaderOverrides = new Dictionary<string, string> { ["X-Machine"] = "iPhone4,1" },
        };

        repository.Import(repo, ControlFileParser.Parse("""
            Package: a
            Version: 1.0
            Architecture: iphoneos-arm
            """), "iphoneos-arm", DateTimeOffset.Now);

        var json = database.Read(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT headers_json FROM repos WHERE name = 'r';";
            return command.ExecuteScalar() as string;
        });

        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json!);
        Assert.Equal("iPhone4,1", document.RootElement.GetProperty("X-Machine").GetString());
    }

    // ── 生产 HttpClient 必须关闭自动重定向 ───────────────────────────

    [Fact]
    public void DefaultHandler_DisablesAutoRedirect()
    {
        using var handler = HttpDownloader.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void DefaultClient_HasInfiniteTimeout()
    {
        using var client = HttpDownloader.CreateClient();
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    // ── 抓取失败不抛（超时 / 配置错误）───────────────────────────────

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task TotalTimeout_ReturnsFailureNotThrow()
    {
        var repo = new RepoDescriptor
        {
            Name = "slow", BaseUrl = "http://example.invalid", Layout = RepoLayout.Flat,
        };

        var fetcher = new IndexFetcher(
            new HttpDownloader(new HttpClient(new SlowHandler())),
            NullLogSink.Instance);

        var options = new HttpDownloadOptions
        {
            TotalTimeout = TimeSpan.FromMilliseconds(100),
            NoProgressTimeout = TimeSpan.FromSeconds(30),
        };

        var outcome = await fetcher.FetchAsync(repo, options, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("超时", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSuite_ReturnsFailureNotThrow()
    {
        var broken = new RepoDescriptor
        {
            Name = "broken", BaseUrl = "http://example.invalid", Layout = RepoLayout.Dists /* 缺 suite */,
        };

        var fetcher = new IndexFetcher(
            new HttpDownloader(new HttpClient(new SlowHandler())),
            NullLogSink.Instance);

        var outcome = await fetcher.FetchAsync(broken, new HttpDownloadOptions(), null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("配置", outcome.Error!, StringComparison.Ordinal);
    }

    // ── 两个 IPackageSource 语义一致（真实包命中也要看 provides）──────

    [Fact]
    public void InMemorySource_MergesProvidesEvenWhenRealPackageExists()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("virtual", "1.0"))                          // 真实包，版本低
            .Add(Pkg("provider", "5.0", provides: "virtual (= 2.0)"))
            .Add(Pkg("target", "1.0", depends: "virtual (>= 2.0)"));

        var result = new DependencyResolver(source, new BasePackageProvider(TargetProfile.IPhone4S613))
            .Resolve(["target"]);

        Assert.True(result.Success);
        Assert.Contains(result.ToDownload, item => item.PackageId == "provider");
    }

    // ── 追加发现：同一行被两个虚拟包名各选一次 → 下载清单会重复 ──

    [Fact]
    public void SameRow_SelectedUnderTwoVirtualNames_AppearsOnceInDownloads()
    {
        var source = new InMemoryPackageSource()
            .Add(Pkg("provider", "1.0", provides: "v1, v2"))
            .Add(Pkg("target", "1.0", depends: "v1, v2"));

        var result = new DependencyResolver(source, new BasePackageProvider(TargetProfile.IPhone4S613))
            .Resolve(["target"]);

        Assert.True(result.Success);
        Assert.Single(result.ToDownload, item => item.PackageId == "provider");
    }
    // ── Release 校验表路径含空格 ─────────────────────────────────────
    [Fact]
    public void ReleaseHashPath_WithSpaces_IsParsed()
    {
        var release = ReleaseInfo.Parse(ControlFileParser.Parse("""
            Origin: Test
            SHA256:
             aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1234 main/binary-iphoneos-arm/Packages (copy).bz2
            """).Single());

        Assert.True(release.TryGetFileHash("main/binary-iphoneos-arm/Packages (copy).bz2", out var hash));
        Assert.Equal(1234, hash.Size);
    }
}

