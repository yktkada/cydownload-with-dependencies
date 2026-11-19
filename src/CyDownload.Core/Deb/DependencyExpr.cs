namespace CyDownload.Core.Deb;

/// <summary>
/// 依赖表达式：`a, b (>= 1.0) | c` —— 逗号分隔成多个 <see cref="DependencyClause"/>（都要满足），
/// 竖线分隔成多个 <see cref="Dependency"/> 备选（满足**任一**即可）。
/// <para>
/// 只解析 `Depends` / `Pre-Depends`；`Recommends` / `Suggests` / `Conflicts` 一律不碰。
/// 解析失败不抛异常：无法识别的项会被跳过并由调用方按「未解析」上报（`Parse` 返回的集合可能为空）。
/// </para>
/// </summary>
public sealed record Dependency(string Name, VersionConstraint? Constraint)
{
    /// <summary>`name` 或 `name (>= 1.0)`。</summary>
    public override string ToString() => Constraint is { } c ? $"{Name} ({c})" : Name;
}

/// <summary>一组互斥备选（`a | b`）。</summary>
public sealed record DependencyClause(IReadOnlyList<Dependency> Alternatives)
{
    public Dependency First => Alternatives[0];

    public override string ToString() => string.Join(" | ", Alternatives);
}

/// <summary>一个依赖字段的完整解析结果。</summary>
public sealed record DependencyExpr(IReadOnlyList<DependencyClause> Clauses)
{
    public static DependencyExpr Empty { get; } = new([]);

    public bool IsEmpty => Clauses.Count == 0;

    /// <summary>
    /// 解析不出、被跳过的原始片段（非空表示源数据有问题）。
    /// **调用方必须把非空当成「未解析」报给用户**——依赖少一个不会报错，只会装不上。
    /// </summary>
    public IReadOnlyList<string> Unparsed { get; init; } = [];

    /// <summary>解析 `Depends` / `Pre-Depends` / `Provides` 这类字段。空/空白返回 <see cref="Empty"/>。</summary>
    public static DependencyExpr Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;

        var clauses = new List<DependencyClause>();
        var unparsed = new List<string>();

        foreach (var clauseText in text.Split(','))
        {
            var alternatives = new List<Dependency>();
            foreach (var alternativeText in clauseText.Split('|'))
            {
                var dependency = ParseSingle(alternativeText);
                if (dependency is not null) alternatives.Add(dependency);
            }

            if (alternatives.Count > 0)
            {
                clauses.Add(new DependencyClause(alternatives));
            }
            else if (clauseText.Trim().Length > 0)
            {
                unparsed.Add(clauseText.Trim());
            }
        }

        if (clauses.Count == 0) return unparsed.Count == 0 ? Empty : new DependencyExpr([]) { Unparsed = unparsed };
        return new DependencyExpr(clauses) { Unparsed = unparsed };
    }

    /// <summary>`name` / `name:any` / `name (>= 1.0)`，容忍任意空白。解析不出返回 null。</summary>
    internal static Dependency? ParseSingle(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;

        string? op = null;
        string? version = null;

        var open = value.IndexOf('(');
        if (open >= 0)
        {
            var close = value.LastIndexOf(')');
            if (close <= open) return null;

            var inner = value[(open + 1)..close].Trim();
            value = value[..open].Trim();

            // 运算符可以没有空格（`(>=1.0)` 也是合法的）：先匹配最长的运算符前缀
            foreach (var candidate in (string[])["<<", "<=", ">>", ">=", "=", "<", ">"])
            {
                if (!inner.StartsWith(candidate, StringComparison.Ordinal)) continue;
                op = candidate;
                version = inner[candidate.Length..].Trim();
                break;
            }

            if (op is null || version!.Length == 0) return null;
        }

        // 去掉架构限定 "pkg:any"（非 :any 的限定在 iOS 源里没见过，同样只记录名字）
        var colon = value.IndexOf(':');
        if (colon > 0) value = value[..colon];

        value = value.Trim();
        if (value.Length == 0) return null;

        VersionConstraint? constraint = null;
        if (op is not null)
        {
            if (!VersionConstraint.TryParse(op, version, out var parsed)) return null;
            constraint = parsed;
        }

        return new Dependency(value, constraint);
    }

    public override string ToString() => string.Join(", ", Clauses);
}

/// <summary>`Provides:` 里的一项（虚拟包，可带版本）。</summary>
public sealed record ProvidedName(string Name, DebianVersion? Version)
{
    public override string ToString() => Version is { } v ? $"{Name} (= {v})" : Name;

    /// <summary>解析 `Provides:` 字段（逗号分隔，无 `|`）。</summary>
    public static IReadOnlyList<ProvidedName> ParseField(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var result = new List<ProvidedName>();
        foreach (var item in text.Split(','))
        {
            var dependency = DependencyExpr.ParseSingle(item);
            if (dependency is null) continue;

            // Provides 里只有 "=" 有意义；其他运算符视为无版本
            var version = dependency.Constraint is { Op: ConstraintOp.Eq } c ? c.Version : (DebianVersion?)null;
            result.Add(new ProvidedName(dependency.Name, version));
        }

        return result;
    }
}
