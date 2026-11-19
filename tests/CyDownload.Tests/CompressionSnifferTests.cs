using System.IO;
using System.IO.Compression;
using System.Text;
using CyDownload.Core.Fetch;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 压缩嗅探与解压。
/// 夹具：<c>Fixtures/sample-utf8.bz2</c>（libbz2 产物，含中文与续行）、
/// <c>Fixtures/saurik-Packages.bz2</c>（ 从 apt.saurik.com 抓的真实索引，364 包）。
/// </summary>
public sealed class CompressionSnifferTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ── 魔数判定 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Detect_Xz()
    {
        byte[] head = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x04];
        Assert.Equal(CompressionKind.Xz, CompressionSniffer.Detect(head));
    }

    [Fact]
    public void Detect_Zstd()
    {
        byte[] head = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(CompressionKind.Zstd, CompressionSniffer.Detect(head));
    }

    [Theory]
    [InlineData("BZh9")]
    [InlineData("BZh1")]
    public void Detect_Bzip2(string text)
        => Assert.Equal(CompressionKind.Bzip2, CompressionSniffer.Detect(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Detect_Gzip()
    {
        byte[] head = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(CompressionKind.Gzip, CompressionSniffer.Detect(head));
    }

    [Fact]
    public void Detect_PlainPackagesText()
        => Assert.Equal(
            CompressionKind.None,
            CompressionSniffer.Detect(Encoding.ASCII.GetBytes("Package: bash\nVersion: 4.0\n")));

    [Fact]
    public void Detect_HtmlPage_IsMarkup_NotPlainText()
    {
        // bigboss 实测：明文 Packages 会返回 200 的营销页 —— 必须与明文索引区分开
        Assert.Equal(
            CompressionKind.Markup,
            CompressionSniffer.Detect(Encoding.ASCII.GetBytes("<!DOCTYPE html>\n<html>")));
        Assert.Equal(
            CompressionKind.Markup,
            CompressionSniffer.Detect(Encoding.ASCII.GetBytes("  \r\n <html>")));
    }

    [Fact]
    public void Detect_Utf8BomIsStillPlainText()
    {
        // ：带 BOM 的明文索引（常见形态）不能被判成 Unknown
        byte[] head = [0xEF, 0xBB, 0xBF, (byte)'P', (byte)'a', (byte)'c', (byte)'k', (byte)'a'];
        Assert.Equal(CompressionKind.None, CompressionSniffer.Detect(head));
    }

    [Fact]
    public void Detect_JsonErrorPage_IsMarkup()
    {
        Assert.Equal(
            CompressionKind.Markup,
            CompressionSniffer.Detect(Encoding.ASCII.GetBytes("{\"error\":\"blocked\"}")));
        Assert.Equal(
            CompressionKind.Markup,
            CompressionSniffer.Detect(Encoding.ASCII.GetBytes("[{\"error\":1}]")));
    }

    [Fact]
    public void Detect_Bzip2RequiresBlockLevelDigit()
    {
        // 只有 "BZh" 但没有 '1'..'9' 的块级别 → 不是 bz2
        Assert.NotEqual(CompressionKind.Bzip2, CompressionSniffer.Detect(Encoding.ASCII.GetBytes("BZhabcdef")));
    }

    [Fact]
    public void Detect_BinaryGarbage_IsUnknown()
        => Assert.Equal(CompressionKind.Unknown, CompressionSniffer.Detect([0x00, 0x01, 0x02, 0x03]));

    [Fact]
    public void Detect_Empty_IsUnknown()
        => Assert.Equal(CompressionKind.Unknown, CompressionSniffer.Detect([]));

    // ── 解压 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Bzip2_DecompressesLibbz2Fixture()
    {
        var expected = File.ReadAllText(FixturePath("sample-utf8.txt"), Encoding.UTF8);

        using var raw = File.OpenRead(FixturePath("sample-utf8.bz2"));
        using var decompressed = CompressionSniffer.OpenDecompressor(raw, CompressionKind.Bzip2);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);

        var actual = reader.ReadToEnd();

        Assert.Equal(expected, actual);
        Assert.Contains("中文描述测试", actual, StringComparison.Ordinal);
        Assert.Contains("续行第二行：含制表符", actual, StringComparison.Ordinal);
        Assert.Contains("firmware (<< 8.3) | com.saurik.patcyh", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Bzip2_DecompressesRealSaurikIndex()
    {
        using var raw = File.OpenRead(FixturePath("saurik-Packages.bz2"));

        var head = new byte[CompressionSniffer.SniffBytes];
        var read = raw.Read(head, 0, head.Length);
        Assert.Equal(CompressionKind.Bzip2, CompressionSniffer.Detect(head.AsSpan(0, read)));
        raw.Position = 0;

        using var decompressed = CompressionSniffer.OpenDecompressor(raw, CompressionKind.Bzip2);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);
        var text = reader.ReadToEnd();

        Assert.Equal(174_237, Encoding.UTF8.GetByteCount(text));
        Assert.Equal(364, text.Split("\nPackage: ").Length);
        Assert.Contains("Package: mobilesubstrate\n", text, StringComparison.Ordinal);
        Assert.Contains("Package: com.saurik.substrate.safemode\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', text);   // 没有解码失败
    }

    [Fact]
    public void Gzip_DecompressesRoundTrip()
    {
        const string payload = "Package: gzip-test\nVersion: 1\n";
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            gzip.Write(bytes);
        }

        buffer.Position = 0;
        using var decompressed = CompressionSniffer.OpenDecompressor(buffer, CompressionKind.Gzip);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);

        Assert.Equal(payload, reader.ReadToEnd());
    }

    [Fact]
    public void PlainText_IsPassedThrough()
    {
        const string payload = "Package: plain\nVersion: 2\n";
        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        using var decompressed = CompressionSniffer.OpenDecompressor(buffer, CompressionKind.None);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);

        Assert.Equal(payload, reader.ReadToEnd());
    }

    [Fact]
    public void Markup_ThrowsInvalidData()
    {
        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes("<html></html>"));
        Assert.Throws<InvalidDataException>(() => CompressionSniffer.OpenDecompressor(buffer, CompressionKind.Markup));
    }

    [Fact]
    public void Zstd_DecompressesRoundTrip()
    {
        // SharpCompress 自带 ZStandard；真实场景来源是 dpkg-deb 新版本打出的 control.tar.zst
        const string payload = "Package: zstd-test\nVersion: 1\n";
        using var buffer = new MemoryStream();
        using (var zstd = new SharpCompress.Compressors.ZStandard.CompressionStream(
            buffer, level: 3, bufferSize: 81920, leaveOpen: true))
        {
            zstd.Write(Encoding.UTF8.GetBytes(payload));
        }

        buffer.Position = 0;
        var head = new byte[CompressionSniffer.SniffBytes];
        var read = buffer.Read(head, 0, head.Length);
        Assert.Equal(CompressionKind.Zstd, CompressionSniffer.Detect(head.AsSpan(0, read)));
        buffer.Position = 0;

        using var decompressed = CompressionSniffer.OpenDecompressor(buffer, CompressionKind.Zstd);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);

        Assert.Equal(payload, reader.ReadToEnd());
    }

    [Fact]
    public void ZstdDebFixture_IsAnArContainer_WithZstdMembers()
    {
        // 夹具：dpkg-deb -Zzstd 打的 deb（成员是 control.tar.zst / data.tar.zst）。
        // iOS 6 的 dpkg 1.18.10 实测不认 zst（见  实验记录）；我们这边必须能处理，才好做结构校验。
        // 注意：deb 不是压缩流，是 ar 容器 —— 所以 CompressionSniffer 不该拿来判 deb（ar 成员名是 ASCII，会判成 None）。
        var path = FixturePath(Path.Combine("debs", "ios6-incompatible-zst.deb"));
        Assert.True(File.Exists(path), "缺少夹具：" + path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("!<arch>\n"u8.ToArray(), bytes.AsSpan(0, 8).ToArray());

        // 成员内部才是 zstd
        var zstdMagic = new byte[] { 0x28, 0xB5, 0x2F, 0xFD };
        Assert.True(bytes.AsSpan().IndexOf(zstdMagic) > 0, "deb 里找不到 zstd 成员数据");
    }
}
