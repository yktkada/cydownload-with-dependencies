using CyDownload.Core.Deb;
using Xunit;

namespace CyDownload.Tests;

/// <summary>依赖表达式与版本约束（数据取自 saurik 真实索引字段原文）。</summary>
public sealed class DependencyExprTests
{
    private static VersionConstraint RequireConstraint(Dependency dependency)
        => dependency.Constraint ?? throw new Xunit.Sdk.XunitException($"{dependency.Name} 应当有版本约束");

    // ── 解析 ────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyAndNull_YieldEmpty()
    {
        Assert.True(DependencyExpr.Parse("").IsEmpty);
        Assert.True(DependencyExpr.Parse(null).IsEmpty);
        Assert.True(DependencyExpr.Parse("   ").IsEmpty);
    }

    [Fact]
    public void SingleDependency_NoConstraint()
    {
        var expr = DependencyExpr.Parse("com.saurik.substrate.safemode");

        var clause = Assert.Single(expr.Clauses);
        var dependency = Assert.Single(clause.Alternatives);
        Assert.Equal("com.saurik.substrate.safemode", dependency.Name);
        Assert.Null(dependency.Constraint);
    }

    [Fact]
    public void RealBashDepends_ParsesFourItems()
    {
        // saurik: bash Depends: grep, ncurses, readline (>= 6.0-7), sed
        var expr = DependencyExpr.Parse("grep, ncurses, readline (>= 6.0-7), sed");

        Assert.Equal(4, expr.Clauses.Count);
        Assert.All(expr.Clauses, c => Assert.Single(c.Alternatives));

        var readline = expr.Clauses[2].First;
        Assert.Equal("readline", readline.Name);
        var readlineConstraint = RequireConstraint(readline);
        Assert.Equal(ConstraintOp.Ge, readlineConstraint.Op);
        Assert.Equal("6.0-7", readlineConstraint.Version.ToString());
    }

    [Fact]
    public void RealUikittoolsDepends_HasAlternativeWithVersionConstraint()
    {
        // saurik: uikittools Depends: coreutils-bin, firmware (<< 8.3) | com.saurik.patcyh
        var expr = DependencyExpr.Parse("coreutils-bin, firmware (<< 8.3) | com.saurik.patcyh");

        Assert.Equal(2, expr.Clauses.Count);
        Assert.Single(expr.Clauses[0].Alternatives);

        var alternatives = expr.Clauses[1].Alternatives;
        Assert.Equal(2, alternatives.Count);
        Assert.Equal("firmware", alternatives[0].Name);
        var firmwareConstraint = RequireConstraint(alternatives[0]);
        Assert.Equal(ConstraintOp.Lt, firmwareConstraint.Op);
        Assert.Equal("8.3", firmwareConstraint.Version.ToString());
        Assert.Equal("com.saurik.patcyh", alternatives[1].Name);
        Assert.Null(alternatives[1].Constraint);
    }

    [Fact]
    public void WhitespaceVariants_AreTolerated()
    {
        var expr = DependencyExpr.Parse("  a(>=1.0) ,b  |  c ( = 2 )  ");

        Assert.Equal(2, expr.Clauses.Count);
        Assert.Equal("a", expr.Clauses[0].First.Name);
        Assert.Equal(ConstraintOp.Ge, RequireConstraint(expr.Clauses[0].First).Op);
        Assert.Equal("c", expr.Clauses[1].Alternatives[1].Name);
        Assert.Equal(ConstraintOp.Eq, RequireConstraint(expr.Clauses[1].Alternatives[1]).Op);
    }

    [Fact]
    public void ArchQualifier_IsStrippedFromName()
    {
        var expr = DependencyExpr.Parse("mobilesubstrate (>= 0.9.5000), libfoo:any");

        Assert.Equal("mobilesubstrate", expr.Clauses[0].First.Name);
        Assert.Equal("libfoo", expr.Clauses[1].First.Name);
    }

    [Fact]
    public void InvalidItems_AreReportedAsUnparsed_NotSilentlyDropped()
    {
        // ：解析不出的依赖必须能被报出来——少一个依赖不会报错，只会装不上。
        var expr = DependencyExpr.Parse("good, ((broken)), also-good");

        Assert.Equal(2, expr.Clauses.Count);
        Assert.Equal("good", expr.Clauses[0].First.Name);
        Assert.Equal("also-good", expr.Clauses[1].First.Name);
        Assert.Equal("((broken))", Assert.Single(expr.Unparsed));
    }

    [Fact]
    public void ValidField_HasNoUnparsedItems()
    {
        var expr = DependencyExpr.Parse("grep, ncurses, readline (>= 6.0-7), sed");
        Assert.Empty(expr.Unparsed);
    }

    [Fact]
    public void ToString_RendersBackReadably()
    {
        Assert.Equal("a (>= 1.0) | b", DependencyExpr.Parse("a (>= 1.0) | b").ToString());
    }

    // ── 版本约束判定 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<<", "8.3", "6.1.3", true)]
    [InlineData("<<", "8.3", "8.3", false)]
    [InlineData("<=", "8.3", "8.3", true)]
    [InlineData(">=", "0.9.5000", "0.9.6301", true)]
    [InlineData(">=", "0.9.5000", "0.9.4999", false)]
    [InlineData(">>", "1.0", "1.0", false)]
    [InlineData("=", "1.0-0", "1.0", true)]        // 语义相等
    [InlineData(">>", "1:1.0", "2.0", false)]      // epoch 更大：1:1.0 > 2.0
    [InlineData("<", "8.3", "6.1.3", true)]        // 废弃写法 = <=
    [InlineData(">", "8.3", "6.1.3", false)]
    public void Constraint_IsSatisfiedBy(string op, string wanted, string actual, bool expected)
    {
        Assert.True(VersionConstraint.TryParse(op, wanted, out var constraint));
        Assert.Equal(expected, constraint.IsSatisfiedBy(DebianVersion.Parse(actual)));
    }

    [Theory]
    [InlineData("<>", "1.0")]
    [InlineData(">=", "not-a-version!")]
    [InlineData("", "1.0")]
    [InlineData(">=", "")]
    public void TryParse_RejectsBadOperatorOrVersion(string op, string version)
        => Assert.False(VersionConstraint.TryParse(op, version, out _));

    // ── Provides ────────────────────────────────────────────────────────────

    [Fact]
    public void Provides_ParsesNamesAndOptionalVersions()
    {
        var provides = ProvidedName.ParseField("dirname, kill, mktemp, su");

        Assert.Equal(4, provides.Count);
        Assert.All(provides, p => Assert.Null(p.Version));
        Assert.Equal("su", provides[^1].Name);
    }

    [Fact]
    public void Provides_WithVersion()
    {
        var provides = ProvidedName.ParseField("virtual-pkg (= 1.2.3), plain");

        Assert.Equal(2, provides.Count);
        Assert.Equal("virtual-pkg", provides[0].Name);
        Assert.Equal("1.2.3", provides[0].Version?.ToString());
        Assert.Null(provides[1].Version);
    }

    [Fact]
    public void Provides_EmptyField()
    {
        Assert.Empty(ProvidedName.ParseField(""));
        Assert.Empty(ProvidedName.ParseField(null));
    }

    /// <summary>真实字段：`apt7-lib` 在 31 包基线里提供 `apt7-ssl`。</summary>
    [Fact]
    public void RealProvidesValue_IsUsableForVirtualPackages()
    {
        var provides = ProvidedName.ParseField("apt7-ssl");
        Assert.Equal("apt7-ssl", Assert.Single(provides).Name);
    }
}

