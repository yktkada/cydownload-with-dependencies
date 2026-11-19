namespace CyDownload.Core.Deb;

/// <summary>
/// Debian 版本号（`[epoch:]upstream[-revision]`）与比较。
/// <para>
/// **必须与 dpkg 逐位一致**：黄金向量由真实 `dpkg --compare-versions` 生成
/// （`tests/CyDownload.Tests/Fixtures/dpkg-version-vectors.tsv`）。
/// 算法照抄 dpkg 的 <c>verrevcmp</c>（Debian Policy 5.6.12）：
/// 先用 epoch 数字比，再比 upstream，最后比 revision；每段按「数字段 ↔ 非数字段」交替比较。
/// </para>
/// <para>
/// 非数字字符的排序是 <c>~</c> &lt; 空串 &lt; 字母 &lt; 其他字符；
/// <c>~</c> 比所有东西都小，连「什么都没有」都比它大 —— 所以 <c>1.0~rc1 &lt; 1.0</c>。
/// </para>
/// <para>
/// <b>判断「是不是同一个版本」必须用 <see cref="CompareTo"/>，不要用 <c>==</c></b>：
/// 结构不同但语义相等的写法是合法的（实测 <c>1.0</c> 与 <c>1.0-0</c> 的 dpkg 比较结果为相等），
/// 而 <c>==</c> 是逐字段相等。
/// </para>
/// <para>
/// <b>不要把它放进 <c>HashSet</c> / <c>Distinct</c> / <c>Dictionary</c> 键</b>：
/// 那些容器用的是结构相等，与 <see cref="CompareTo"/> 不等价。
/// 需要「按语义去重」时，先按 <see cref="ToString"/> 的规范形式分组，或在求解层用比较器去重。
/// </para>
/// </summary>
public readonly record struct DebianVersion : IComparable<DebianVersion>
{
    private DebianVersion(int epoch, string upstream, string revision)
    {
        Epoch = epoch;
        Upstream = upstream;
        Revision = revision;
    }

    public int Epoch { get; }

    public string Upstream { get; }

    /// <summary>没有 `-` 时为空串（不是 null）。</summary>
    public string Revision { get; }

    /// <summary>
    /// 给 <c>OrderBy</c> / <c>Sort</c> 用的比较器 —— `record struct` 的**结构相等**与
    /// <see cref="CompareTo(DebianVersion)"/> 不等价（见类注释），所以排序必须显式传它。
    /// </summary>
    public static IComparer<DebianVersion> Comparer { get; } =
        System.Collections.Generic.Comparer<DebianVersion>.Create((left, right) => left.CompareTo(right));

    /// <summary>解析失败抛 <see cref="FormatException"/>；调用方可先用 <see cref="TryParse"/>。</summary>
    public static DebianVersion Parse(string text)
    {
        if (!TryParse(text, out var version))
        {
            throw new FormatException($"Invalid Debian version: '{text}'.");
        }

        return version;
    }

    public static bool TryParse(string? text, out DebianVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '+' or '-' or '~' or ':'))
            {
                return false;
            }
        }

        var epoch = 0;
        var rest = value;
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            if (colon == 0) return false;
            if (!int.TryParse(value.AsSpan(0, colon), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out epoch))
            {
                return false;
            }

            rest = value[(colon + 1)..];
        }

        if (rest.Length == 0) return false;

        // revision 取**最后一个** '-' 之后的部分（upstream 里允许出现 '-'？policy 说 upstream 不含 '-'，
        // 但历史包里有；按 dpkg 的做法取最后一个）
        var dash = rest.LastIndexOf('-');
        var upstream = dash < 0 ? rest : rest[..dash];
        var revision = dash < 0 ? "" : rest[(dash + 1)..];
        if (upstream.Length == 0) return false;

        version = new DebianVersion(epoch, upstream, revision);
        return true;
    }

    public int CompareTo(DebianVersion other)
    {
        var result = Epoch.CompareTo(other.Epoch);
        if (result != 0) return result;

        result = CompareFragment(Upstream, other.Upstream);
        return result != 0 ? result : CompareFragment(Revision, other.Revision);
    }

    /// <summary>dpkg 的 <c>order</c>：数字无视（比较时另走数字分支）、`~` 最小、字母按码位、其余 +256。
    /// <b>`\0`（越界）必须返回 0</b>——否则 `1.0a` 与 `1.0` 会被判反（黄金向量抓到过）。</summary>
    private static int Order(char c) => c switch
    {
        '\0' => 0,
        >= '0' and <= '9' => 0,
        >= 'a' and <= 'z' => c,
        >= 'A' and <= 'Z' => c,
        '~' => -1,
        _ => c + 256,
    };

    private static int CompareFragment(string left, string right)
    {
        var i = 0;
        var j = 0;
        while (i < left.Length || j < right.Length)
        {
            var firstDifference = 0;

            // 非数字段：逐字符比，越界按 '\0'（order = 0）
            while ((i < left.Length && !char.IsAsciiDigit(left[i])) ||
                   (j < right.Length && !char.IsAsciiDigit(right[j])))
            {
                var a = Order(i < left.Length ? left[i] : '\0');
                var b = Order(j < right.Length ? right[j] : '\0');
                if (a != b) return a < b ? -1 : 1;
                if (i < left.Length) i++;
                if (j < right.Length) j++;
            }

            // 数字段：跳前导零，先比长度再比数值（长度相同则逐位差即可定序）
            while (i < left.Length && left[i] == '0') i++;
            while (j < right.Length && right[j] == '0') j++;

            while (i < left.Length && char.IsAsciiDigit(left[i]) &&
                   j < right.Length && char.IsAsciiDigit(right[j]))
            {
                if (firstDifference == 0) firstDifference = left[i] - right[j];
                i++;
                j++;
            }

            if (i < left.Length && char.IsAsciiDigit(left[i])) return 1;
            if (j < right.Length && char.IsAsciiDigit(right[j])) return -1;
            if (firstDifference != 0) return firstDifference < 0 ? -1 : 1;
        }

        return 0;
    }

    /// <summary>dpkg 的规范形式：epoch 为 0 时不写；revision 为空时不写 `-`。</summary>
    public override string ToString()
    {
        var epoch = Epoch > 0 ? Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" : "";
        return Revision.Length == 0 ? epoch + Upstream : epoch + Upstream + "-" + Revision;
    }

    public static bool operator <(DebianVersion left, DebianVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(DebianVersion left, DebianVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(DebianVersion left, DebianVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(DebianVersion left, DebianVersion right) => left.CompareTo(right) >= 0;
}

