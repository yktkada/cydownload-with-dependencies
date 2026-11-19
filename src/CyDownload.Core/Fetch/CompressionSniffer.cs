using System.IO.Compression;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;

namespace CyDownload.Core.Fetch;

/// <summary>
/// 索引 / deb 内容的压缩格式（按魔数判定，**不看扩展名、也不看 HTTP 状态码**）。
/// <para>
/// 依据  实测：
/// 三个源唯一共有的是 <see cref="Bzip2"/>；<c>.xz</c> / <c>.zst</c> 三源全 404；
/// bigboss 的明文 <c>Packages</c> 会返回 200 但内容是营销网页 —— 所以必须认魔数。
/// </para>
/// </summary>
public enum CompressionKind
{
    /// <summary>无压缩的明文索引（<c>Packages</c> / <c>Release</c>）。</summary>
    None,

    Bzip2,
    Gzip,
    Xz,
    Zstd,

    /// <summary>
    /// 文本但是 HTML（多半是服务器把请求重写成营销页 / 登录页 / 404 页面）。
    /// 实测：bigboss 的明文 <c>Packages</c> 302→301 之后返回 200 HTML —— 只看状态码会把它当索引。
    /// </summary>
    Markup,

    /// <summary>既不是已知压缩格式，也不是可打印文本（二进制垃圾 / 被截断的响应）。</summary>
    Unknown,
}

/// <summary>压缩格式嗅探 + 解压流构造。</summary>
internal static class CompressionSniffer
{
    /// <summary>判定魔数所需的最少字节数（xz 的魔数有 6 字节）。</summary>
    public const int SniffBytes = 8;

    private static readonly byte[] XzMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static CompressionKind Detect(ReadOnlySpan<byte> head)
    {
        // 明文索引可能带 UTF-8 BOM（只影响开头几个字节，但足以让纯 ASCII 判定失败）
        if (head.StartsWith(Utf8Bom)) head = head[Utf8Bom.Length..];

        if (head.StartsWith(XzMagic)) return CompressionKind.Xz;
        if (head.StartsWith(ZstdMagic)) return CompressionKind.Zstd;

        // bzip2: 'B' 'Z' 'h' 后跟块大小级别 '1'..'9'
        if (head.Length >= 4 && head[0] == (byte)'B' && head[1] == (byte)'Z' && head[2] == (byte)'h'
            && head[3] is >= (byte)'1' and <= (byte)'9')
        {
            return CompressionKind.Bzip2;
        }

        // gzip: 1F 8B
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B) return CompressionKind.Gzip;

        if (!LooksLikeText(head)) return CompressionKind.Unknown;
        return LooksLikeMarkup(head) ? CompressionKind.Markup : CompressionKind.None;
    }

    /// <summary>
    /// 首个非空白字节是 <c>&lt;</c>（HTML/XML）、<c>{</c> 或 <c>[</c>（JSON 错误页）→ 都不是合法索引。
    /// </summary>
    private static bool LooksLikeMarkup(ReadOnlySpan<byte> head)
    {
        foreach (var b in head)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b is (byte)'<' or (byte)'{' or (byte)'[';
        }

        return false;
    }

    /// <summary>
    /// 文本判定：明文 <c>Packages</c> / <c>Release</c> 都以可打印 ASCII 开头。
    /// 用来把「无压缩的合法内容」和「网页 / 二进制垃圾」区分开。
    /// </summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty) return false;

        foreach (var b in head)
        {
            if (b == 0) return false;
            if (b < 0x09) return false;                 // 控制字符
            if (b > 0x0D && b < 0x20) return false;     // 其余控制字符
            if (b > 0x7E) return false;                 // 非 ASCII（Release 里可能有 UTF-8，但不做猜测）
        }

        return true;
    }

    /// <summary>按魔数把原始流包成解压流。调用方负责释放返回的流（它会连带释放 <paramref name="raw"/>）。</summary>
    public static Stream OpenDecompressor(Stream raw, CompressionKind kind) => kind switch
    {
        CompressionKind.None => raw,
        CompressionKind.Gzip => new GZipStream(raw, System.IO.Compression.CompressionMode.Decompress),
        CompressionKind.Bzip2 => BZip2Stream.Create(
            raw,
            SharpCompress.Compressors.CompressionMode.Decompress,
            decompressConcatenated: true,
            leaveOpen: false),
        CompressionKind.Xz => new XZStream(raw),
        CompressionKind.Zstd => new SharpCompress.Compressors.ZStandard.DecompressionStream(
            raw,
            bufferSize: 81920,
            checkEndOfStream: true,
            leaveOpen: false),
        CompressionKind.Markup => throw new InvalidDataException(
            "The server returned an HTML/XML page instead of an index (check the URL and redirects)."),
        CompressionKind.Unknown => throw new InvalidDataException(
            "The downloaded payload is neither a known compression format nor plain text."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown compression kind."),
    };
}

