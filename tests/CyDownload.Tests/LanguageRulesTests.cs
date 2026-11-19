using CyDownload.App.Localization;
using Xunit;

namespace CyDownload.Tests;

/// <summary>语言解析是纯函数，逐条钉死。</summary>
public sealed class LanguageRulesTests
{
    [Theory]
    [InlineData(null, "zh-CN", LanguageRules.Chinese)]
    [InlineData("", "zh-CN", LanguageRules.Chinese)]
    [InlineData("system", "zh-CN", LanguageRules.Chinese)]
    [InlineData("system", "zh-TW", LanguageRules.Chinese)]
    [InlineData("system", "zh-Hans", LanguageRules.Chinese)]
    [InlineData("system", "en-US", LanguageRules.English)]
    [InlineData("system", "en-GB", LanguageRules.English)]
    [InlineData("system", "fr-FR", LanguageRules.English)]
    [InlineData("system", "ja-JP", LanguageRules.English)]
    [InlineData("system", "", LanguageRules.English)]
    [InlineData("system", null, LanguageRules.English)]
    public void Resolve_System_FollowsUiCulture(string? setting, string? uiCulture, string expected)
        => Assert.Equal(expected, LanguageRules.Resolve(setting, uiCulture));

    [Theory]
    [InlineData("zh-CN", "en-US", LanguageRules.Chinese)]
    [InlineData("zh", "en-US", LanguageRules.Chinese)]
    [InlineData("en", "zh-CN", LanguageRules.English)]
    [InlineData("en-US", "zh-CN", LanguageRules.English)]
    [InlineData("  en  ", "zh-CN", LanguageRules.English)]
    [InlineData("SYSTEM", "zh-CN", LanguageRules.Chinese)]
    public void Resolve_ExplicitSetting_Wins(string setting, string uiCulture, string expected)
        => Assert.Equal(expected, LanguageRules.Resolve(setting, uiCulture));

    [Theory]
    [InlineData("de-DE", "zh-CN", LanguageRules.Chinese)]
    [InlineData("klingon", "en-US", LanguageRules.English)]
    public void Resolve_UnknownSetting_FallsBackToSystem(string setting, string uiCulture, string expected)
        => Assert.Equal(expected, LanguageRules.Resolve(setting, uiCulture));

    [Fact]
    public void DisplayName_IsEachLanguageSelfName()
    {
        Assert.Equal("简体中文", LanguageRules.DisplayName(LanguageRules.Chinese));
        Assert.Equal("English", LanguageRules.DisplayName(LanguageRules.English));
    }
}
