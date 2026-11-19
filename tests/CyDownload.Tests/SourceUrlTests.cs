using CyDownload.Core.Repos;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 添加源时的地址规范化与名称推导：用户只粘地址也要能加源。
/// </summary>
public sealed class SourceUrlTests
{
    [Theory]
    [InlineData("apt.saurik.com", "http://apt.saurik.com")]
    [InlineData("  apt.saurik.com/  ", "http://apt.saurik.com")]
    [InlineData("//apt.saurik.com/", "http://apt.saurik.com")]
    [InlineData("http://apt.saurik.com/", "http://apt.saurik.com")]
    [InlineData("https://repo.example.com/cydia/", "https://repo.example.com/cydia")]
    [InlineData("HTTP://Repo.Example.COM", "http://repo.example.com")]
    [InlineData("http://apt.thebigboss.org/repofiles/cydia", "http://apt.thebigboss.org/repofiles/cydia")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    public void TryNormalize_AddsPrefixAndTrims(string input, string expected)
    {
        Assert.True(SourceUrl.TryNormalize(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("", "address is empty")]
    [InlineData("   ", "address is empty")]
    [InlineData("ftp://example.com/", "unsupported scheme")]
    [InlineData("http://", "not a valid absolute URL")]
    [InlineData("http://example.com/?a=1", "query or fragment")]
    public void TryNormalize_RejectsBadInput(string input, string errorPart)
    {
        Assert.False(SourceUrl.TryNormalize(input, out _, out var error));
        Assert.Contains(errorPart, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://apt.saurik.com", "apt.saurik.com")]
    [InlineData("http://www.example.com/cydia", "example.com")]
    [InlineData("https://repo.example.com", "repo.example.com")]
    [InlineData("http://127.0.0.1:8080", "127.0.0.1")]
    public void DeriveName_UsesHostWithoutWww(string url, string expected)
    {
        Assert.Equal(expected, SourceUrl.DeriveName(url));
    }

    [Fact]
    public void SanitizeName_ReplacesWhitespace()
    {
        Assert.Equal("my-source", SourceUrl.SanitizeName(" my source "));
        Assert.Equal("", SourceUrl.SanitizeName("   "));
    }

    [Fact]
    public void MakeUniqueName_AppendsSuffixWhenTaken()
    {
        var existing = new[] { "apt.saurik.com", "apt.saurik.com-2" };

        Assert.Equal("apt.saurik.com-3", SourceUrl.MakeUniqueName("apt.saurik.com", existing));
        Assert.Equal("new.example.com", SourceUrl.MakeUniqueName("new.example.com", existing));
    }
}
