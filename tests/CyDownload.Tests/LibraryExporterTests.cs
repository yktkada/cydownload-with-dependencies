using System.IO;
using CyDownload.Core.Export;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 资料库右键「导出」的落地逻辑：只复制、不删除不覆盖、补零命名、缺文件单独计数。
/// </summary>
public sealed class LibraryExporterTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "debs", name);

    private static LibrarySourceFile File(string fixture, string packageId, string version, int order)
        => new(FixturePath(fixture), packageId, version, "iphoneos-arm", order);

    [Fact]
    public void CopiesFiles_WithPaddedOrderNames()
    {
        using var workspace = new TestWorkspace();
        var files = new[]
        {
            File("ios6-gz.deb", "gettext", "0.17-7", 0),
            File("ios6-xz.deb", "openssl", "0.9.8zg-13", 1),
            File("ios6.gz.deb".Replace("ios6.gz.deb", "ios6-gz.deb"), "wget", "1.16-4", 2),
        };

        var result = LibraryExporter.Copy(files, workspace.Root);

        Assert.Equal(3, result.Copied.Count);
        Assert.Equal("1-gettext_0.17-7_iphoneos-arm.deb", result.Copied[0]);
        Assert.Equal("3-wget_1.16-4_iphoneos-arm.deb", result.Copied[2]);
        Assert.Empty(result.Missing);
        Assert.Equal(0, result.SkippedIdentical);
        Assert.Equal(0, result.SkippedDifferent);
        Assert.All(result.Copied, name => Assert.True(System.IO.File.Exists(Path.Combine(workspace.Root, name))));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void TwelveFiles_ArePaddedToTwoDigits()
    {
        using var workspace = new TestWorkspace();
        var files = Enumerable.Range(0, 12)
            .Select(index => File("ios6-gz.deb", "pkg-" + index, "1.0", index))
            .ToList();

        var result = LibraryExporter.Copy(files, workspace.Root);

        Assert.Equal("01-pkg-0_1.0_iphoneos-arm.deb", result.Copied[0]);
        Assert.Equal("12-pkg-11_1.0_iphoneos-arm.deb", result.Copied[11]);
    }

    [Fact]
    public void ExistingIdenticalFiles_AreSkipped_NotDeleted()
    {
        using var workspace = new TestWorkspace();
        var files = new[] { File("ios6-gz.deb", "only", "1.0", 0) };

        var first = LibraryExporter.Copy(files, workspace.Root);
        var second = LibraryExporter.Copy(files, workspace.Root);

        Assert.Single(first.Copied);
        Assert.Equal(1, second.SkippedIdentical);
        Assert.Equal(0, second.SkippedDifferent);
        Assert.True(System.IO.File.Exists(Path.Combine(workspace.Root, first.Copied[0])));
    }

    [Fact]
    public void ExistingDifferentFile_IsNotOverwritten()
    {
        using var workspace = new TestWorkspace();
        var files = new[] { File("ios6-gz.deb", "only", "1.0", 0) };

        var first = LibraryExporter.Copy(files, workspace.Root);
        var target = Path.Combine(workspace.Root, first.Copied[0]);
        System.IO.File.WriteAllBytes(target, [1, 2, 3, 4]);

        var second = LibraryExporter.Copy(files, workspace.Root);

        Assert.Equal(1, second.SkippedDifferent);
        Assert.Equal(4, new FileInfo(target).Length);
    }

    [Fact]
    public void MissingPoolFile_IsReportedSeparately()
    {
        using var workspace = new TestWorkspace();
        var files = new[]
        {
            File("ios6-gz.deb", "present", "1.0", 0),
            new LibrarySourceFile(Path.Combine(workspace.Root, "nope.deb"), "gone", "1.0", "iphoneos-arm", 1),
        };

        var result = LibraryExporter.Copy(files, workspace.Root);

        Assert.Single(result.Copied);
        var missing = Assert.Single(result.Missing);
        Assert.Contains("gone", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void UserFilesInTargetDirectory_AreUntouched()
    {
        using var workspace = new TestWorkspace();
        var userFile = Path.Combine(workspace.Root, "mine.txt");
        System.IO.File.WriteAllText(userFile, "keep");

        LibraryExporter.Copy([File("ios6-gz.deb", "only", "1.0", 0)], workspace.Root);

        Assert.True(System.IO.File.Exists(userFile));
        Assert.Equal("keep", System.IO.File.ReadAllText(userFile));
    }
}
