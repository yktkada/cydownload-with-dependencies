using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CyDownload.App.Localization;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// Loc 的启动期行为。
/// **这些用例对应**：启动序列里的对话框必须在 <c>Loc.Initialize</c> 之后才弹，
/// 否则用户看到的是 <c>[App.NotWritable.Title]</c> 且目录/原因全丢。
/// </summary>
[Collection("Localization")]
public sealed class LocTests : IDisposable
{
    public void Dispose() => Loc.ResetForTests();

    [Fact]
    public void BeforeInitialize_KeysDegradeToBracketedKey()
    {
        Loc.ResetForTests();

        Assert.False(Loc.IsInitialized);
        Assert.Equal("[App.NotWritable.Title]", Loc.Get("App.NotWritable.Title"));
    }

    [Fact]
    public void BeforeInitialize_FormatKeepsArgumentsForDiagnosis()
    {
        Loc.ResetForTests();

        var text = Loc.Format("App.NotWritable.Message", @"D:\app", "UnauthorizedAccessException: denied");

        // 未初始化时模板取不到，但参数不能丢（否则排查时不知道目录和原因是什么）
        Assert.Contains("[App.NotWritable.Message]", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\app", text, StringComparison.Ordinal);
        Assert.Contains("UnauthorizedAccessException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_LoadsChineseDictionary()
    {
        Loc.ResetForTests();

        Loc.Initialize(LanguageRules.Chinese, out var problem);

        Assert.Null(problem);
        Assert.True(Loc.IsInitialized);
        Assert.Equal(LanguageRules.Chinese, Loc.Language.Name);

        // 取到的必须是真实文案，而不是 [键名]
        var title = Loc.Get("App.NotWritable.Title");
        Assert.DoesNotContain("[", title, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    [Fact]
    public void Initialize_EnglishDictionary_AlsoLoads()
    {
        Loc.ResetForTests();

        Loc.Initialize(LanguageRules.English, out var problem);

        Assert.Null(problem);
        Assert.True(Loc.IsInitialized);
        Assert.True(Loc.IsEnglish);
    }

    [Fact]
    public void Format_RendersTemplateArguments()
    {
        Loc.ResetForTests();
        Loc.Initialize(LanguageRules.Chinese, out _);

        var text = Loc.Format("App.NotWritable.Message", @"D:\app", "denied");

        Assert.Contains(@"D:\app", text, StringComparison.Ordinal);
        Assert.Contains("denied", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_InitializedTemplateWithoutPlaceholder_DoesNotAppendArguments()
    {
        // ：已初始化时模板没占位符，就不该偷偷把参数拼到文案后面
        Loc.ResetForTests();
        Loc.Initialize(LanguageRules.Chinese, out _);

        var plain = Loc.Get("App.StartupFailed.Title");
        var formatted = Loc.Format("App.StartupFailed.Title", "多余参数");

        Assert.Equal(plain, formatted);
    }

    [Fact]
    public void SwitchLanguage_ChangesRenderedText_AndRaisesEvent()
    {
        Loc.ResetForTests();
        Loc.Initialize(LanguageRules.Chinese, out _);
        var chinese = Loc.Get("App.NotWritable.Title");

        var raised = 0;
        EventHandler handler = (_, _) => raised++;
        Loc.LanguageChanged += handler;
        try
        {
            Loc.SetLanguage(LanguageRules.English);
            var english = Loc.Get("App.NotWritable.Title");

            Assert.NotEqual(chinese, english);
            Assert.Equal(1, raised);
        }
        finally
        {
            Loc.LanguageChanged -= handler;
        }
    }

    [Fact]
    public void SetLanguage_SameLanguage_IsNoOp()
    {
        Loc.ResetForTests();
        Loc.Initialize(LanguageRules.Chinese, out _);

        var raised = 0;
        EventHandler handler = (_, _) => raised++;
        Loc.LanguageChanged += handler;
        try
        {
            Loc.SetLanguage(LanguageRules.Chinese);
            Assert.Equal(0, raised);
        }
        finally
        {
            Loc.LanguageChanged -= handler;
        }
    }

    [Fact]
    public void AllResourceKeys_RenderWithoutBrackets_WhenInitialized()
    {
        Loc.ResetForTests();
        Loc.Initialize(LanguageRules.Chinese, out _);

        foreach (var key in ReadKeys("Strings.zh-CN.xaml"))
        {
            Assert.DoesNotContain("[", Loc.Get(key), StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> ReadKeys(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var path = Path.Combine(directory.FullName, "src", "CyDownload.App", "Localization", fileName);
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                foreach (Match match in Regex.Matches(text, @"x:Key=""([^""]+)"""))
                {
                    yield return match.Groups[1].Value;
                }

                yield break;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 " + fileName);
    }
}
