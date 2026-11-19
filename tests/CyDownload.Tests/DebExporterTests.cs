using System.IO;
using System.Text.Json;
using CyDownload.Core.Export;
using CyDownload.Core.Fetch;
using CyDownload.Core.Index;
using CyDownload.Core.Net;
using CyDownload.Core.Resolve;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 导出：命名补零、依赖顺序、manifest、环标注、兼容性警告、旧文件清理。
/// deb 用真实夹具（`dpkg-deb` 产物），下载器用假实现。
/// </summary>
public sealed class DebExporterTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "debs", name);

    private static PackageRow Pkg(
        string id,
        string version = "1.0",
        string? depends = null,
        long size = 100,
        string repo = "saurik",
        string? md5 = null,
        string? sha256 = null)
        => new(
            0, id, null, version, "iphoneos-arm", null, null, null, null, null, null,
            depends, null, null, null, $"debs/{id}_{version}.deb", size, null, md5, sha256, repo);

    private sealed class FixtureSource(string fixtureName) : IDebSource
    {
        public List<string> Requested { get; } = [];

        public Task<DebFetchResult> FetchAsync(
            ResolvedPackage package,
            IProgress<DownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            Requested.Add(package.PackageId);
            var path = FixturePath(fixtureName);
            var bytes = File.ReadAllBytes(path);
            return Task.FromResult(new DebFetchResult(
                package, path, bytes.LongLength, "http://example.invalid/" + fixtureName,
                FromCache: false, DebFileInspector.Inspect(bytes)));
        }
    }

    private static ResolutionResult Resolution(params (PackageRow Package, int Priority)[] packages)
        => new(
            Success: true,
            ToDownload: packages.Select(item => new ResolvedPackage(item.Package, item.Priority, IsTarget: true)).ToList(),
            Satisfied: [],
            Unresolved: [],
            Cycles: [],
            FailureReason: null,
            NodesVisited: packages.Length);

    [Fact]
    public async Task TwoPackages_DependencyFirst_AndNonPaddedNames()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-gz.deb");
        var exporter = new DebExporter(source);
        var resolution = Resolution((Pkg("wget", depends: "gettext", size: 660), 1), (Pkg("gettext", size: 660), 1));

        var result = await exporter.ExportAsync(
            resolution,
            new ExportOptions { Directory = workspace.Root, Target = TargetProfile.IPhone4S613 },
            progress: null,
            CancellationToken.None);

        // 2 个包 → 位宽 1（不补零）
        Assert.Equal(["1-gettext_1.0_iphoneos-arm.deb", "2-wget_1.0_iphoneos-arm.deb"],
            result.Files.Select(file => file.FileName));
        Assert.Equal(1_320, result.TotalBytes);
        Assert.Equal(["gettext", "wget"], result.Files.Select(file => file.Package.PackageId));

        foreach (var file in result.Files)
        {
            Assert.True(File.Exists(Path.Combine(workspace.Root, file.FileName)), file.FileName);
        }
    }

    [Fact]
    public async Task TwelvePackages_ArePaddedToTwoDigits()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-gz.deb");
        var exporter = new DebExporter(source);

        // 12 个包：第 1 个被其余 11 个依赖（顺序稳定）
        var packages = new List<(PackageRow, int)> { (Pkg("base-pkg", size: 660), 1) };
        for (var index = 2; index <= 12; index++)
        {
            packages.Add((Pkg($"pkg-{index:D2}", depends: "base-pkg", size: 660), 1));
        }

        var result = await exporter.ExportAsync(
            Resolution([.. packages]),
            new ExportOptions { Directory = workspace.Root },
            progress: null,
            CancellationToken.None);

        Assert.Equal(12, result.Files.Count);
        Assert.Equal("01-base-pkg_1.0_iphoneos-arm.deb", result.Files[0].FileName);
        Assert.Equal("12-pkg-12_1.0_iphoneos-arm.deb", result.Files[^1].FileName);
        // 字符串排序必须与数字顺序一致（补零的意义）
        var sorted = result.Files.Select(file => file.FileName).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(result.Files.Select(file => file.FileName), sorted);
    }

    [Fact]
    public async Task Manifest_ContainsOrderHashesAndSatisfied()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-xz.deb");
        var exporter = new DebExporter(source);

        var resolution = new ResolutionResult(
            true,
            [new ResolvedPackage(Pkg("tweak", md5: "0123456789abcdef0123456789abcdef", size: 100), 1, true)],
            [new SatisfiedDependency("tweak", "dpkg", "base", "1.18.10-12")],
            [], [], null, 1);

        var result = await exporter.ExportAsync(
            resolution,
            new ExportOptions { Directory = workspace.Root, Target = TargetProfile.IPhone4S613, BaseMode = "preset" },
            progress: null,
            CancellationToken.None);

        Assert.True(File.Exists(result.ManifestPath));
        using var document = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        var root = document.RootElement;

        Assert.Equal("preset", root.GetProperty("baseMode").GetString());
        Assert.Equal("iPhone4,1", root.GetProperty("target").GetProperty("productType").GetString());
        Assert.Equal(1, root.GetProperty("count").GetInt32());

        var file = root.GetProperty("files")[0];
        Assert.Equal(1, file.GetProperty("order").GetInt32());
        Assert.Equal("tweak", file.GetProperty("package").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef", file.GetProperty("md5").GetString());
        Assert.Equal("http://example.invalid/ios6-xz.deb", file.GetProperty("sourceUrl").GetString());

        Assert.Equal("dpkg", root.GetProperty("satisfied")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Cycle_IsRecordedInManifest()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-gz.deb");

        var resolution = Resolution(
            (Pkg("mobilesubstrate", depends: "com.saurik.substrate.safemode"), 1),
            (Pkg("com.saurik.substrate.safemode", depends: "mobilesubstrate"), 1));

        var result = await new DebExporter(source).ExportAsync(
            resolution,
            new ExportOptions { Directory = workspace.Root },
            progress: null,
            CancellationToken.None);

        Assert.NotEmpty(result.Cycles);
        using var document = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        Assert.NotEmpty(document.RootElement.GetProperty("cycles").EnumerateArray());
    }

    [Fact]
    public async Task ZstdControlSegment_OnOldIos_ProducesWarning()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-incompatible-zst.deb");

        var result = await new DebExporter(source).ExportAsync(
            Resolution((Pkg("modern-pkg"), 1)),
            new ExportOptions { Directory = workspace.Root, Target = TargetProfile.IPhone4S613 },
            progress: null,
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("control.tar.zst", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ZstdControlSegment_OnNewIos_DoesNotWarn()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-incompatible-zst.deb");
        var newTarget = TargetProfile.IPhone4S613 with { IosVersion = "9.3.5" };

        var result = await new DebExporter(source).ExportAsync(
            Resolution((Pkg("modern-pkg"), 1)),
            new ExportOptions { Directory = workspace.Root, Target = newTarget },
            progress: null,
            CancellationToken.None);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ExistingFiles_InTargetDirectory_AreNeverDeleted()
    {
        // ：导出只复制，不删除、不移动。目录里用户自己的文件必须原样保留。
        using var workspace = new TestWorkspace();
        var userDeb = Path.Combine(workspace.Root, "user-own-package.deb");
        var userNote = Path.Combine(workspace.Root, "我的说明.txt");
        await File.WriteAllBytesAsync(userDeb, [9, 9, 9], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(userNote, "keep me", TestContext.Current.CancellationToken);

        var source = new FixtureSource("ios6-gz.deb");
        var result = await new DebExporter(source).ExportAsync(
            Resolution((Pkg("only"), 1)),
            new ExportOptions { Directory = workspace.Root },
            progress: null,
            CancellationToken.None);

        Assert.True(File.Exists(userDeb));
        Assert.True(File.Exists(userNote));
        Assert.Single(result.Files);
        Assert.Equal(1, result.Files[0].Order);
    }

    [Fact]
    public async Task DuplicateExport_SkipsIdenticalFiles_InsteadOfRecopying()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-gz.deb");
        var exporter = new DebExporter(source);
        var resolution = Resolution((Pkg("only"), 1));

        var first = await exporter.ExportAsync(
            resolution, new ExportOptions { Directory = workspace.Root }, null, CancellationToken.None);
        var second = await exporter.ExportAsync(
            resolution, new ExportOptions { Directory = workspace.Root }, null, CancellationToken.None);

        Assert.Equal(0, first.SkippedIdentical);
        Assert.Equal(1, second.SkippedIdentical);
        Assert.Equal(0, second.SkippedDifferent);
        Assert.Single(second.Files);                       // 清单仍列出该包（文件已在）
        Assert.Equal(first.Files[0].FileName, second.Files[0].FileName);
    }

    [Fact]
    public async Task SameNameDifferentContent_IsLeftUntouched_AndReported()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-gz.deb");
        var exporter = new DebExporter(source);
        var resolution = Resolution((Pkg("only"), 1));

        var first = await exporter.ExportAsync(
            resolution, new ExportOptions { Directory = workspace.Root }, null, CancellationToken.None);

        // 把导出目录里的文件改成不同内容（模拟用户自己换过文件）
        var target = Path.Combine(workspace.Root, first.Files[0].FileName);
        await File.WriteAllBytesAsync(target, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var second = await exporter.ExportAsync(
            resolution, new ExportOptions { Directory = workspace.Root }, null, CancellationToken.None);

        Assert.Equal(1, second.SkippedDifferent);
        Assert.Empty(second.Files);
        Assert.Contains(second.Warnings, warning => warning.Contains("未覆盖", StringComparison.Ordinal));
        Assert.Equal(4, new FileInfo(target).Length);      // 原文件没被覆盖
    }

    [Fact]
    public async Task Export_CopiesInsteadOfMoving_SoPoolFileRemains()
    {
        // 「只复制，不移动」：导出后 pool 里的原件必须还在（FixtureSource 用夹具当 pool 文件）
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-gz.deb");
        var result = await new DebExporter(source).ExportAsync(
            Resolution((Pkg("only"), 1)),
            new ExportOptions { Directory = workspace.Root },
            null,
            CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", "debs", "ios6-gz.deb")));
        Assert.True(File.Exists(Path.Combine(workspace.Root, result.Files[0].FileName)));
    }

    private sealed class FailingSource(string fixtureName, int succeedCount) : IDebSource
    {
        private int _calls;

        public Task<DebFetchResult> FetchAsync(
            ResolvedPackage package,
            IProgress<DownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls > succeedCount)
            {
                throw new DebFetchException("simulated download failure", "http://example.invalid/x.deb");
            }

            var path = FixturePath(fixtureName);
            var bytes = File.ReadAllBytes(path);
            return Task.FromResult(new DebFetchResult(
                package, path, bytes.LongLength, "http://example.invalid/" + fixtureName,
                FromCache: false, DebFileInspector.Inspect(bytes)));
        }
    }

    [Fact]
    public async Task FailureMidway_WritesPartialManifest_AndLeavesNoTempFiles()
    {
        using var workspace = new TestWorkspace();
        var source = new FailingSource("ios6-gz.deb", succeedCount: 1);
        var resolution = Resolution((Pkg("a", depends: "b", size: 660), 1), (Pkg("b", size: 660), 1));

        await Assert.ThrowsAsync<DebFetchException>(() => new DebExporter(source).ExportAsync(
            resolution, new ExportOptions { Directory = workspace.Root }, null, CancellationToken.None));

        var partial = Path.Combine(workspace.Root, "manifest.partial.json");
        Assert.True(File.Exists(partial));
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(partial));
        Assert.True(json.RootElement.GetProperty("partial").GetBoolean());
        Assert.Contains("simulated", json.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);

        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp"));
        Assert.Single(Directory.EnumerateFiles(workspace.Root, "*.deb"));     // 只有成功的那一个
    }

    [Fact]
    public async Task CopyIsAtomic_NoTempLeftOnSuccess()
    {
        using var workspace = new TestWorkspace();
        var result = await new DebExporter(new FixtureSource("ios6-gz.deb")).ExportAsync(
            Resolution((Pkg("only"), 1)), new ExportOptions { Directory = workspace.Root }, null,
            CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(workspace.Root, result.Files[0].FileName)));
    }
    [Fact]
    public async Task FailedResolution_ThrowsInsteadOfExportingEmptyDirectory()
    {
        using var workspace = new TestWorkspace();
        var exporter = new DebExporter(new FixtureSource("ios6-gz.deb"));
        var failed = ResolutionResult.Failed("有未解析依赖");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.ExportAsync(failed, new ExportOptions { Directory = workspace.Root }, null, CancellationToken.None));

        Assert.Contains("unsuccessful", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Progress_IsReportedPerFile()
    {
        using var workspace = new TestWorkspace();
        var source = new FixtureSource("ios6-gz.deb");
        var reports = new List<ExportProgress>();

        await new DebExporter(source).ExportAsync(
            Resolution((Pkg("a", depends: "b"), 1), (Pkg("b"), 1)),
            new ExportOptions { Directory = workspace.Root },
            new SynchronousProgress(reports),
            CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(2, reports[^1].Completed);
    }

    [Fact]
    public void WidthFor_MatchesPackageCountDigits()
    {
        Assert.Equal(1, ExportResult.WidthFor(2));
        Assert.Equal(2, ExportResult.WidthFor(12));
        Assert.Equal(2, ExportResult.WidthFor(99));
        Assert.Equal(3, ExportResult.WidthFor(100));
        Assert.Equal(1, ExportResult.WidthFor(0));
    }

    private sealed class SynchronousProgress(List<ExportProgress> sink) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => sink.Add(value);
    }
}



