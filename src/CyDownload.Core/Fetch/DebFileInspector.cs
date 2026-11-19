using System.Globalization;
using System.Text;

namespace CyDownload.Core.Fetch;

/// <summary>deb 里的一个 ar 成员。</summary>
public sealed record DebMember(string Name, long Size, long Offset);

/// <summary>deb 结构检查结果。</summary>
public sealed record DebInspection(
    bool IsValid,
    string? Error,
    string? FormatVersion,
    IReadOnlyList<DebMember> Members,
    string? ControlMember,
    string? DataMember)
{
    /// <summary>控制段的压缩方式（`gz` / `xz` / `zst` / `plain`）；没有控制段时为 null。</summary>
    public string? ControlCompression => CompressionOf(ControlMember);

    /// <summary>
    /// iOS 6/7 的 `dpkg 1.18.10` 不认 `control.tar.zst`（ 真机实测）——
    /// 导出到这些系统时要给用户一个明确警告，否则只会看到「装不上」。
    /// </summary>
    public bool ControlIsZstd =>
        string.Equals(ControlCompression, "zst", StringComparison.Ordinal);

    private static string? CompressionOf(string? member)
    {
        if (member is null) return null;
        if (member.EndsWith(".gz", StringComparison.Ordinal)) return "gz";
        if (member.EndsWith(".xz", StringComparison.Ordinal)) return "xz";
        if (member.EndsWith(".zst", StringComparison.Ordinal)) return "zst";
        if (member.EndsWith(".bz2", StringComparison.Ordinal)) return "bz2";
        return "plain";
    }
}

/// <summary>
/// deb 结构校验（只读头部，不解压）：`!&lt;arch&gt;\n` 魔数 + ar 成员表 + 必需的
/// `debian-binary` / `control.tar.*` / `data.tar.*`。
/// <para>
/// 为什么需要：源站经常把 HTML 错误页、被截断的文件当 `Filename` 返回；光看「HTTP 200 且长度对」
/// 不够（bigboss 的明文索引就实测过 200 但内容是网页）。下载完先验结构，失败就别写进 pool。
/// </para>
/// </summary>
public static class DebFileInspector
{
    /// <summary>ar 全局头。</summary>
    public static ReadOnlySpan<byte> Magic => "!<arch>\n"u8;

    private const int MemberHeaderSize = 60;

    public static DebInspection InspectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Inspect(stream);
    }

    public static DebInspection Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return Inspect(stream);
    }

    public static DebInspection Inspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var magic = new byte[Magic.Length];
        if (stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length ||
            !magic.AsSpan().SequenceEqual(Magic))
        {
            return Invalid("not an ar archive (missing !<arch> magic)");
        }

        var members = new List<DebMember>();
        var header = new byte[MemberHeaderSize];

        while (true)
        {
            var read = stream.ReadAtLeast(header, MemberHeaderSize, throwOnEndOfStream: false);
            if (read == 0) break;
            if (read < MemberHeaderSize) return Invalid("truncated ar member header");

            // 60 字节头：name[16] mtime[12] uid[6] gid[6] mode[8] size[10] magic[2]
            if (header[58] != (byte)'`' || header[59] != (byte)'\n')
            {
                return Invalid("bad ar member trailer");
            }

            var name = Encoding.ASCII.GetString(header, 0, 16).Trim();
            var sizeText = Encoding.ASCII.GetString(header, 48, 10).Trim();

            var offset = stream.Position;
            if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                return Invalid($"bad ar member size '{sizeText}'");
            }

            // BSD/GNU 长名：`#1/<len>` 后面紧跟名字本体
            if (name.StartsWith("#1/", StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var nameLength) &&
                nameLength is > 0 and <= 255)
            {
                var nameBytes = new byte[nameLength];
                if (stream.ReadAtLeast(nameBytes, nameLength, throwOnEndOfStream: false) < nameLength)
                {
                    return Invalid("truncated long ar member name");
                }

                name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                offset = stream.Position;
                size -= nameLength;
            }
            else if (name.EndsWith('/'))
            {
                name = name[..^1];
            }

            // 成员体不能超出文件：三个头都在、但最后一个成员被截断的 deb 必须判无效
            if (stream.CanSeek && offset + size > stream.Length)
            {
                return Invalid($"truncated ar member '{name}' (needs {size} bytes, only {stream.Length - offset} left)");
            }

            members.Add(new DebMember(name, size, offset));

            // 成员按偶数对齐；跳过内容
            stream.Seek(size + (size % 2), SeekOrigin.Current);
        }

        if (members.Count == 0) return Invalid("no ar members");

        var format = members.FirstOrDefault(member => member.Name == "debian-binary");
        var control = members.FirstOrDefault(member =>
            member.Name.StartsWith("control.tar", StringComparison.Ordinal));
        var data = members.FirstOrDefault(member =>
            member.Name.StartsWith("data.tar", StringComparison.Ordinal));

        if (format is null) return Invalid("missing debian-binary");
        if (control is null) return Invalid("missing control.tar.*");
        if (data is null) return Invalid("missing data.tar.*");

        string? version = null;
        try
        {
            stream.Position = format.Offset;
            var versionBytes = new byte[Math.Min(format.Size, 8)];
            if (stream.ReadAtLeast(versionBytes, versionBytes.Length, throwOnEndOfStream: false) == versionBytes.Length)
            {
                version = Encoding.ASCII.GetString(versionBytes).Trim();
            }
        }
        catch (IOException)
        {
            // 版本号读不到不算结构错；上面三个成员齐全已经足够
        }

        return new DebInspection(true, null, version, members, control.Name, data.Name);
    }

    private static DebInspection Invalid(string error)
        => new(false, error, null, [], null, null);
}

