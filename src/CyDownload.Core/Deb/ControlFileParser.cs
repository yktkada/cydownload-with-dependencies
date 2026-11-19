namespace CyDownload.Core.Deb;

/// <summary>
/// 一个 control stanza / 索引段落：`Field: value`，续行以空格或 tab 开头并属于上一字段。
/// <para>
/// <b>字段名大小写不敏感</b>（Debian Policy 5.1：field names are not case-sensitive），
/// 字典用 <see cref="StringComparer.OrdinalIgnoreCase"/>；值保留原样（续行用 <c>\n</c> 拼回）。
/// </para>
/// </summary>
public sealed class ControlStanza
{
    private readonly Dictionary<string, string> _fields;

    internal ControlStanza(Dictionary<string, string> fields) => _fields = fields;

    public IReadOnlyDictionary<string, string> Fields => _fields;

    public int Count => _fields.Count;

    /// <summary>取字段值（不存在返回 null）。</summary>
    public string? Get(string key) => _fields.GetValueOrDefault(key);

    public bool TryGet(string key, out string value) => _fields.TryGetValue(key, out value!);

    /// <summary>`Package` 字段（索引里是包名；`Release` 里没有）。</summary>
    public string? Package => Get("Package");

    public override string ToString() => Package is { } name ? $"{name} {Get("Version")}" : $"{_fields.Count} fields";
}

/// <summary>
/// `Packages` / `Release` 这类 control 文件的解析器（Debian 822 格式）。
/// <para>
/// 必须处理的三件事（参考项目全栽在这里，见 ）：
/// ① **续行**（以空格/tab 开头的行属于上一字段）；
/// ② **末段没有空行结尾**（原项目用「行首是 \n」判新段，会丢最后一个包）；
/// ③ **`Key:value` 冒号后没有空格**（原项目用正则 `^(.*): (.*)`，会整条丢弃）。
/// </para>
/// </summary>
public static class ControlFileParser
{
    public static IReadOnlyList<ControlStanza> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        using var reader = new StringReader(text);
        return Parse(reader);
    }

    public static IReadOnlyList<ControlStanza> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var stanzas = new List<ControlStanza>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastKey = null;

        while (reader.ReadLine() is { } rawLine)
        {
            // 统一去掉 CR（索引可能是 CRLF）
            var line = rawLine.TrimEnd('\r');

            // 只有**全空行**才分段；「只有空格的行」按 822 是上一字段的续行（等价于追加一个空行）。
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                if (lastKey is null) continue;          // 字段前的缩进：忽略
                if (fields.TryGetValue(lastKey, out var current))
                {
                    fields[lastKey] = current + "\n" + line.Trim();
                }

                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;                   // 不是合法字段行：忽略

            var key = line[..colon].Trim();
            if (key.Length == 0) continue;

            lastKey = key;

            // 重复字段：822 里算错。这里保留**第一次**出现的值（后面的丢弃），
            // 至少不会让后面的垃圾值覆盖掉有效值。
            if (!fields.ContainsKey(key))
            {
                fields[key] = line[(colon + 1)..].TrimStart();
            }
        }

        Flush();
        return stanzas;

        void Flush()
        {
            if (fields.Count > 0) stanzas.Add(new ControlStanza(fields));
            fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lastKey = null;
        }
    }
}

