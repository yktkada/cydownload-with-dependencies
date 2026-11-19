using System.IO;
using System.IO.Compression;
using System.Text;
using CyDownload.Core.Deb;
using CyDownload.Core.Fetch;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// control 文件解析。夹具 `saurik-Packages.bz2` 是  从 apt.saurik.com 抓的**真实索引**（364 包）。
/// </summary>
public sealed class ControlFileParserTests
{
    private static string FixturePath(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    private static string LoadSaurikIndex()
    {
        using var raw = File.OpenRead(FixturePath("saurik-Packages.bz2"));
        using var decompressed = CompressionSniffer.OpenDecompressor(raw, CompressionKind.Bzip2);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ── 真实索引 ────────────────────────────────────────────────────────────

    [Fact]
    public void RealSaurikIndex_ParsesAll364Stanzas()
    {
        var stanzas = ControlFileParser.Parse(LoadSaurikIndex());

        Assert.Equal(364, stanzas.Count);
        Assert.All(stanzas, s => Assert.False(string.IsNullOrEmpty(s.Package)));
        Assert.All(stanzas, s => Assert.False(string.IsNullOrEmpty(s.Get("Version"))));
    }

    [Fact]
    public void RealSaurikIndex_MobileSubstrateFields()
    {
        var stanzas = ControlFileParser.Parse(LoadSaurikIndex());
        var stanza = stanzas.Single(s =>
            s.Package == "mobilesubstrate" && s.Get("Version") == "0.9.6301");

        Assert.Equal("iphoneos-arm", stanza.Get("Architecture"));
        Assert.Equal("com.saurik.substrate.safemode", stanza.Get("Depends"));
        Assert.Equal("dpkg (>= 1.14.25-8)", stanza.Get("Pre-Depends"));
        Assert.Equal("debs/mobilesubstrate_0.9.6301_iphoneos-arm.deb", stanza.Get("Filename"));
        Assert.Equal("122342", stanza.Get("Size"));
        Assert.Equal("f2ca15c08aab522d8b7e842238aa83c6", stanza.Get("MD5sum"));
        Assert.Equal("Cydia Substrate", stanza.Get("Name"));
    }

    [Fact]
    public void RealSaurikIndex_MultiLineDescriptionIsJoined()
    {
        var stanzas = ControlFileParser.Parse(LoadSaurikIndex());
        var gdb = stanzas.Single(s => s.Package == "gdb");

        var description = gdb.Get("Description")!;
        Assert.Contains('\n', description);
        Assert.Contains("A debugger for native software from GNU.", description, StringComparison.Ordinal);
    }

    [Fact]
    public void RealSaurikIndex_EpochVersionKeptVerbatim()
    {
        var stanzas = ControlFileParser.Parse(LoadSaurikIndex());
        var ldid = stanzas.Single(s => s.Package == "ldid");

        Assert.Equal("1:1.2.1", ldid.Get("Version"));
    }

    // ── 边界（参考项目全栽在这里）────────────────────────────────────────────

    [Fact]
    public void LastStanzaWithoutTrailingBlankLine_IsKept()
    {
        const string text = """
            Package: first
            Version: 1.0

            Package: last
            Version: 2.0
            """;

        var stanzas = ControlFileParser.Parse(text);

        Assert.Equal(2, stanzas.Count);
        Assert.Equal("last", stanzas[^1].Package);
    }

    [Fact]
    public void FieldWithoutSpaceAfterColon_IsParsed()
    {
        // 原项目用正则 `^(.*): (.*)`，这种写法会被整条丢掉
        var stanzas = ControlFileParser.Parse("Package:bash\nVersion:4.0.44-16\n");

        Assert.Single(stanzas);
        Assert.Equal("bash", stanzas[0].Package);
        Assert.Equal("4.0.44-16", stanzas[0].Get("Version"));
    }

    [Fact]
    public void ContinuationLines_AreJoinedWithNewline_AndKeepIndentationContent()
    {
        const string text = """
            Package: demo
            Description: first line
             second line
            	third line with tab
            Homepage: http://example.invalid
            """;

        var stanza = ControlFileParser.Parse(text).Single();

        Assert.Equal("first line\nsecond line\nthird line with tab", stanza.Get("Description"));
        Assert.Equal("http://example.invalid", stanza.Get("Homepage"));
    }

    [Fact]
    public void CrLfInput_IsHandled()
    {
        const string text = "Package: aaa\r\nVersion: 1.0\r\n\r\nPackage: bbb\r\nVersion: 2.0\r\n";

        var stanzas = ControlFileParser.Parse(text);

        Assert.Equal(2, stanzas.Count);
        Assert.Equal("aaa", stanzas[0].Package);
        Assert.Equal("1.0", stanzas[0].Get("Version"));
        Assert.Equal("bbb", stanzas[1].Package);
    }

    [Fact]
    public void WhitespaceOnlyLine_IsContinuationNotSeparator()
    {
        // 822：以空格/制表符开头的行是上一字段的续行；只有**全空行**才分段。
        // （ 指出旧实现把「只有空格的行」当分段 —— 已按规范改掉。）
        var stanzas = ControlFileParser.Parse("Package: aaa\nVersion: 1\n   \n");

        var stanza = Assert.Single(stanzas);
        Assert.Equal("aaa", stanza.Package);
        Assert.Equal("1\n", stanza.Get("Version"));      // 续行是一个空内容行
    }

    [Fact]
    public void FieldNames_AreCaseInsensitive()
    {
        // Debian Policy 5.1：字段名不区分大小写。旧实现用 Ordinal，全小写字段会被当坏行丢掉。
        var stanza = ControlFileParser.Parse("package: bash\nVERSION: 4.0.44-16\n").Single();

        Assert.Equal("bash", stanza.Package);
        Assert.Equal("4.0.44-16", stanza.Get("Version"));
        Assert.Equal("4.0.44-16", stanza.Get("version"));
    }

    [Fact]
    public void DuplicateField_KeepsFirstValue()
    {
        var stanza = ControlFileParser.Parse("Package: aaa\nVersion: 1\nVersion: 2\n").Single();

        Assert.Equal("aaa", stanza.Package);
        Assert.Equal("1", stanza.Get("Version"));
    }

    [Fact]
    public void EmptyOrNullInput_ReturnsNothing()
    {
        Assert.Empty(ControlFileParser.Parse(""));
        Assert.Empty(ControlFileParser.Parse((string?)null));
    }

    [Fact]
    public void MalformedLines_AreIgnoredWithoutBreakingStanza()
    {
        const string text = """
            Package: demo
            this line has no colon
            Version: 1.0
            """;

        var stanza = ControlFileParser.Parse(text).Single();

        Assert.Equal("demo", stanza.Package);
        Assert.Equal("1.0", stanza.Get("Version"));
        Assert.Equal(2, stanza.Count);
    }

    [Fact]
    public void ReleaseFile_ParsesSameWay()
    {
        const string release = """
            Origin: BigBoss
            Label: BigBoss
            Suite: stable
            Architectures: iphoneos-arm iphoneos-arm64
            Components: main
            MD5Sum:
             9aec85230f287825542fd5c5d9f37ea3 8247106 main/binary-iphoneos-arm/Packages
             7ab64976e117f75f3be5ba499b0d7b78 1315706 main/binary-iphoneos-arm/Packages.bz2
            """;

        var stanza = ControlFileParser.Parse(release).Single();

        Assert.Equal("BigBoss", stanza.Get("Origin"));
        Assert.Contains("iphoneos-arm64", stanza.Get("Architectures")!, StringComparison.Ordinal);
        Assert.Contains("1315706", stanza.Get("MD5Sum")!, StringComparison.Ordinal);
    }
}

