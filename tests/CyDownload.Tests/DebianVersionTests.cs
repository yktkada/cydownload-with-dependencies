using System.IO;
using System.Globalization;
using CyDownload.Core.Deb;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 版本比较：黄金向量由真实 <c>dpkg --compare-versions</c> 生成
/// （`Fixtures/dpkg-version-vectors.tsv`，43 条，0 ERR）。
/// 这一块写错会「下错版本且不报错」，所以用 dpkg 当判据，不用自己推。
/// </summary>
public sealed class DebianVersionTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static TheoryData<string, string, string> GoldenVectors()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var line in File.ReadAllLines(FixturePath("dpkg-version-vectors.tsv")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            if (parts.Length != 3) continue;
            data.Add(parts[0], parts[1], parts[2]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Compare_MatchesDpkg(string left, string right, string relation)
    {
        var a = DebianVersion.Parse(left);
        var b = DebianVersion.Parse(right);
        var actual = Math.Sign(a.CompareTo(b));

        var expected = relation switch
        {
            "lt" => -1,
            "eq" => 0,
            "gt" => 1,
            _ => throw new InvalidOperationException("未知关系：" + relation),
        };

        Assert.Equal(expected, actual);
        Assert.Equal(-expected, Math.Sign(b.CompareTo(a)));   // 反对称
    }

    [Fact]
    public void GoldenVectors_AreNotEmpty()
        => Assert.True(GoldenVectors().Count >= 40, "黄金向量太少，检查夹具是否被复制到输出");

    // ── 解析与规范形式 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.0", 0, "1.0", "")]
    [InlineData("1:1.2.1", 1, "1.2.1", "")]
    [InlineData("1.0-1", 0, "1.0", "1")]
    [InlineData("1:3.0.10-5", 1, "3.0.10", "5")]
    [InlineData("1.0-0", 0, "1.0", "0")]
    public void Parse_SplitsEpochUpstreamRevision(string text, int epoch, string upstream, string revision)
    {
        var version = DebianVersion.Parse(text);

        Assert.Equal(epoch, version.Epoch);
        Assert.Equal(upstream, version.Upstream);
        Assert.Equal(revision, version.Revision);
    }

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("1:1.2.1", "1:1.2.1")]
    [InlineData("0:1.0", "1.0")]          // epoch 0 不写出来
    [InlineData("1.0-", "1.0")]           // 空 revision 不写 '-'
    [InlineData("1:1.0-1", "1:1.0-1")]
    public void ToString_UsesDpkgCanonicalForm(string text, string expected)
        => Assert.Equal(expected, DebianVersion.Parse(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(":1.0")]                   // epoch 缺失
    [InlineData("1.0:")]                   // 冒号后为空
    [InlineData("-1")]                     // upstream 为空
    [InlineData("1 .0")]                   // 空格
    [InlineData("1.0/2")]                  // 非法字符
    public void TryParse_RejectsInvalid(string? text)
        => Assert.False(DebianVersion.TryParse(text, out _));

    [Fact]
    public void Parse_ThrowsOnInvalid()
        => Assert.Throws<FormatException>(() => DebianVersion.Parse("1 .0"));

    [Fact]
    public void StructuralEqualityDiffersFromSemanticEquality()
    {
        // 语义相等（dpkg 判定）但结构不同 —— 所以比较必须用 CompareTo
        Assert.Equal(0, DebianVersion.Parse("1.0").CompareTo(DebianVersion.Parse("1.0-0")));
        Assert.NotEqual(DebianVersion.Parse("1.0"), DebianVersion.Parse("1.0-0"));
    }

    [Fact]
    public void Operators_FollowCompareTo()
    {
        var older = DebianVersion.Parse("0.9.6300");
        var newer = DebianVersion.Parse("0.9.6301");
        var sameAsOlder = DebianVersion.Parse("0.9.6300");

        Assert.True(older < newer);
        Assert.True(newer > older);
        Assert.True(older <= sameAsOlder);
        Assert.True(older >= sameAsOlder);
    }

    [Fact]
    public void RealIndexVersions_SortAsExpected()
    {
        // 来自 saurik 索引的真实版本（mobilesubstrate / safemode / ldid 的 epoch 写法）
        string[] ascending = ["0.9.5101", "0.9.6000", "0.9.6010", "0.9.6300", "0.9.6301"];

        var parsed = ascending.Select(DebianVersion.Parse).ToArray();
        for (var i = 1; i < parsed.Length; i++)
        {
            Assert.True(parsed[i - 1].CompareTo(parsed[i]) < 0,
                $"{ascending[i - 1]} 应小于 {ascending[i]}");
        }

        // epoch 会被优先比较：1:1.2.1 > 1.2.1
        Assert.True(DebianVersion.Parse("1:1.2.1") > DebianVersion.Parse("1.2.1"));
    }

    [Fact]
    public void Version_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // 换到用逗号当小数点的区域，规范形式不能变
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1:1.0-1", DebianVersion.Parse("1:1.0-1").ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

