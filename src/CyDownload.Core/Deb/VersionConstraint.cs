namespace CyDownload.Core.Deb;

/// <summary>版本约束运算符。`&lt;` / `&gt;` 是 Debian Policy 里的废弃写法，等价于 `&lt;=` / `&gt;=`。</summary>
public enum ConstraintOp
{
    Lt,
    Le,
    Eq,
    Ge,
    Gt,
}

/// <summary>一个版本约束，例如 `(>= 6.0-7)`。没有约束时用 <c>null</c> 表示（不是空约束）。</summary>
public readonly record struct VersionConstraint(ConstraintOp Op, DebianVersion Version)
{
    public bool IsSatisfiedBy(DebianVersion actual) => Op switch
    {
        ConstraintOp.Lt => actual.CompareTo(Version) < 0,
        ConstraintOp.Le => actual.CompareTo(Version) <= 0,
        ConstraintOp.Eq => actual.CompareTo(Version) == 0,
        ConstraintOp.Ge => actual.CompareTo(Version) >= 0,
        ConstraintOp.Gt => actual.CompareTo(Version) > 0,
        _ => throw new ArgumentOutOfRangeException(nameof(Op), Op, "Unknown constraint operator."),
    };

    /// <summary>解析运算符 + 版本；运算符不认识或版本非法返回 false。**调用方不要吞掉失败**（应报「未解析」）。</summary>
    public static bool TryParse(string? op, string? version, out VersionConstraint constraint)
    {
        constraint = default;
        if (string.IsNullOrWhiteSpace(op) || string.IsNullOrWhiteSpace(version)) return false;

        ConstraintOp parsed;
        switch (op.Trim())
        {
            case "<<": parsed = ConstraintOp.Lt; break;
            case "<=":
            case "<": parsed = ConstraintOp.Le; break;     // 废弃写法
            case "=": parsed = ConstraintOp.Eq; break;
            case ">=":
            case ">": parsed = ConstraintOp.Ge; break;     // 废弃写法
            case ">>": parsed = ConstraintOp.Gt; break;
            default: return false;
        }

        if (!DebianVersion.TryParse(version, out var wanted)) return false;

        constraint = new VersionConstraint(parsed, wanted);
        return true;
    }

    public override string ToString() => Op switch
    {
        ConstraintOp.Lt => "<< ",
        ConstraintOp.Le => "<= ",
        ConstraintOp.Eq => "= ",
        ConstraintOp.Ge => ">= ",
        ConstraintOp.Gt => ">> ",
        _ => "? ",
    } + Version;
}
