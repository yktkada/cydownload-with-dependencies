using System.IO;
using CyDownload.Core.Fetch;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// deb 容器结构校验。夹具是 `dpkg-deb` 真实产物（gz / xz / zst 三种控制段）。
/// </summary>
public sealed class DebFileInspectorTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "debs", name);

    [Theory]
    [InlineData("ios6-gz.deb", "gz")]
    [InlineData("ios6-xz.deb", "xz")]
    [InlineData("ios6-incompatible-zst.deb", "zst")]
    public void ValidDeb_IsRecognized_WithExpectedControlCompression(string file, string expected)
    {
        var inspection = DebFileInspector.InspectFile(FixturePath(file));

        Assert.True(inspection.IsValid, inspection.Error);
        Assert.Equal("2.0", inspection.FormatVersion);
        Assert.Equal(expected, inspection.ControlCompression);
        Assert.StartsWith("control.tar", inspection.ControlMember!, StringComparison.Ordinal);
        Assert.StartsWith("data.tar", inspection.DataMember!, StringComparison.Ordinal);
        Assert.Equal(3, inspection.Members.Count);      // debian-binary / control.tar.* / data.tar.*
        Assert.Equal(("zst" == expected), inspection.ControlIsZstd);
    }

    [Fact]
    public void MembersHaveNonNegativeSizes()
    {
        var inspection = DebFileInspector.InspectFile(FixturePath("ios6-gz.deb"));

        Assert.All(inspection.Members, member => Assert.True(member.Size >= 0));
        Assert.All(inspection.Members, member => Assert.False(string.IsNullOrWhiteSpace(member.Name)));
    }

    [Fact]
    public void HtmlPage_IsRejected()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html><html>hosting page</html>");

        var inspection = DebFileInspector.Inspect(bytes);

        Assert.False(inspection.IsValid);
        Assert.Contains("ar archive", inspection.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TruncatedFile_IsRejected()
    {
        var bytes = File.ReadAllBytes(FixturePath("ios6-gz.deb"))[..40];

        var inspection = DebFileInspector.Inspect(bytes);

        Assert.False(inspection.IsValid);
    }

    [Fact]
    public void ArWithoutRequiredMembers_IsRejected()
    {
        // 只有一个 debian-binary 成员：缺 control.tar.* / data.tar.*
        var bytes = BuildAr(("debian-binary", "2.0\n"u8.ToArray()));

        var inspection = DebFileInspector.Inspect(bytes);

        Assert.False(inspection.IsValid);
        Assert.Contains("control.tar", inspection.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ArWithoutDebianBinary_IsRejected()
    {
        var bytes = BuildAr(("control.tar.gz", [1, 2, 3]), ("data.tar.gz", [4, 5, 6]));

        var inspection = DebFileInspector.Inspect(bytes);

        Assert.False(inspection.IsValid);
        Assert.Contains("debian-binary", inspection.Error!, StringComparison.Ordinal);
    }

    /// <summary>造一个最小合法 ar 归档（只用于结构校验用例）。</summary>
    private static byte[] BuildAr(params (string Name, byte[] Data)[] members)
    {
        using var output = new MemoryStream();
        output.Write("!<arch>\n"u8);

        foreach (var (name, data) in members)
        {
            // ar 头用空格填充（不是 NUL）——真实 deb 也是这么写的
            var header = Enumerable.Repeat((byte)' ', 60).ToArray();
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
            System.Text.Encoding.ASCII.GetBytes("0".PadRight(12)).CopyTo(header, 16);          // mtime
            System.Text.Encoding.ASCII.GetBytes("0".PadRight(6)).CopyTo(header, 28);           // uid
            System.Text.Encoding.ASCII.GetBytes("0".PadRight(6)).CopyTo(header, 34);           // gid
            System.Text.Encoding.ASCII.GetBytes("100644".PadRight(8)).CopyTo(header, 40);      // mode
            System.Text.Encoding.ASCII.GetBytes(data.Length.ToString().PadRight(10)).CopyTo(header, 48);
            header[58] = (byte)'`';
            header[59] = (byte)'\n';

            output.Write(header);
            output.Write(data);
            if (data.Length % 2 == 1) output.WriteByte(0);                          // 偶数对齐
        }

        return output.ToArray();
    }
}
