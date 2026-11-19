using System.Globalization;
using CyDownload.Core.Deb;

namespace CyDownload.Core.Repos;

/// <summary>Release 校验表里的一行：`&lt;hash&gt; &lt;size&gt; &lt;path&gt;`。</summary>
public sealed record ReleaseFileHash(string RelativePath, long Size, string? Md5, string? Sha256);

/// <summary>
/// `Release` 文件（实测：bigboss 1007 B 且**同时给 MD5 与 SHA256**，apt.gs 325 B 只有元数据）。
/// <para>
/// 用途：索引下载完可以按它校验归档完整性——比「解压成功就算成功」强得多。
/// </para>
/// </summary>
public sealed class ReleaseInfo
{
    private readonly Dictionary<string, ReleaseFileHash> _hashes;

    private ReleaseInfo(Dictionary<string, ReleaseFileHash> hashes) => _hashes = hashes;

    public string? Origin { get; private init; }

    public string? Label { get; private init; }

    public string? Suite { get; private init; }

    public string? Codename { get; private init; }

    public string? Version { get; private init; }

    public string? Architectures { get; private init; }

    public string? Components { get; private init; }

    /// <summary>校验表里的文件数（0 表示这个源没给校验）。</summary>
    public int FileCount => _hashes.Count;

    public static ReleaseInfo Parse(ControlStanza stanza)
    {
        ArgumentNullException.ThrowIfNull(stanza);

        var hashes = new Dictionary<string, ReleaseFileHash>(StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        void Merge(string fieldName, bool isSha256)
        {
            if (!stanza.TryGet(fieldName, out var block)) return;

            foreach (var line in block.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 3) continue;

                var hash = parts[0];

                // 前两段是 hash 与 size，**其余整段拼回路径**：
                // Debian 校验表允许路径含空格，取 parts[^1] 会截错。
                var path = string.Join(' ', parts.Skip(2));
                if (path.Length == 0) continue;
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;

                sizes[path] = size;
                if (hashes.TryGetValue(path, out var existing))
                {
                    hashes[path] = isSha256
                        ? existing with { Sha256 = hash }
                        : existing with { Md5 = hash };
                }
                else
                {
                    hashes[path] = new ReleaseFileHash(
                        path, size, isSha256 ? null : hash, isSha256 ? hash : null);
                }
            }
        }

        Merge("MD5Sum", isSha256: false);
        Merge("SHA256", isSha256: true);

        // 补齐大小（只在一个表里出现过的路径）
        foreach (var (path, hash) in hashes.ToList())
        {
            if (sizes.TryGetValue(path, out var size) && hash.Size != size)
            {
                hashes[path] = hash with { Size = size };
            }
        }

        return new ReleaseInfo(hashes)
        {
            Origin = stanza.Get("Origin"),
            Label = stanza.Get("Label"),
            Suite = stanza.Get("Suite"),
            Codename = stanza.Get("Codename"),
            Version = stanza.Get("Version"),
            Architectures = stanza.Get("Architectures"),
            Components = stanza.Get("Components"),
        };
    }

    /// <summary>按相对路径查校验值（大小写不敏感）。</summary>
    public bool TryGetFileHash(string relativePath, out ReleaseFileHash hash)
        => _hashes.TryGetValue(relativePath.Replace('\\', '/'), out hash!);
}

