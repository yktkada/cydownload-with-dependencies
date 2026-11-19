using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 本地化的两道构建期守门——**/ 之外的另一处「文档声称有、实际没有」**，
/// 现在真的存在了：
/// <list type="bullet">
/// <item>L2：代码里 <c>Loc.Get("X")</c> / <c>Loc.Format("X", …)</c> 用到的键，两份字典都必须有。</item>
/// <item>L4：界面层（Views / ViewModels / Localization 之外的 App 代码）不许出现中文字面量（注释除外）。</item>
/// </list>
/// </summary>
public sealed class LocalizationGuardTests
{
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && directory is not null; i++)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src", "CyDownload.App")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("找不到仓库根（从 " + AppContext.BaseDirectory + " 回溯失败）");
        }
    }

    private static string AppDirectory => Path.Combine(RepositoryRoot, "src", "CyDownload.App");

    private static HashSet<string> KeysOf(string fileName)
    {
        var path = Path.Combine(AppDirectory, "Localization", fileName);
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Regex.Matches(text, @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> AppSourceFiles()
        => Directory.EnumerateFiles(AppDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // ── L2：代码里用到的键必须存在 ───────────────────────────────────────────

    [Fact]
    public void AllLocKeysUsedInCode_ExistInBothDictionaries()
    {
        var chinese = KeysOf("Strings.zh-CN.xaml");
        var english = KeysOf("Strings.en.xaml");

        var used = new SortedSet<string>(StringComparer.Ordinal);
        var pattern = new Regex(@"Loc\.(?:Get|Format)\(\s*""([^""]+)""", RegexOptions.Compiled);

        foreach (var file in AppSourceFiles())
        {
            var code = StripComments(File.ReadAllText(file, Encoding.UTF8));
            foreach (Match match in pattern.Matches(code))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(used);
        Assert.DoesNotContain(used, key => !chinese.Contains(key));
        Assert.DoesNotContain(used, key => !english.Contains(key));
    }

    // ── L4：界面代码里不许出现中文字面量 ─────────────────────────────────────

    [Fact]
    public void NoChineseLiterals_InViewsAndViewModels()
    {
        var offenders = new List<string>();

        foreach (var file in AppSourceFiles())
        {
            var relative = Path.GetRelativePath(AppDirectory, file);
            if (!relative.StartsWith("Views", StringComparison.OrdinalIgnoreCase) &&
                !relative.StartsWith("ViewModels", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = StripComments(File.ReadAllText(file, Encoding.UTF8));
            if (ContainsCjk(code)) offenders.Add(relative);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoChineseLiterals_InXaml()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            // 资源字典本身就是文案，跳过
            var relative = Path.GetRelativePath(AppDirectory, file);
            if (relative.StartsWith("Localization", StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(file, Encoding.UTF8);
            text = Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);   // XAML 注释允许中文
            if (ContainsCjk(text)) offenders.Add(relative);
        }

        Assert.Empty(offenders);
    }

    /// <summary>守卫测试自身要能抓到东西：故意喂一段带中文的代码，确认判定不是恒为 true。</summary>
    [Fact]
    public void GuardDetectsChinese()
    {
        Assert.True(ContainsCjk("var x = \"中文\";"));
        Assert.False(ContainsCjk("var x = \"ascii\";"));
        Assert.False(ContainsCjk(StripComments("// 中文注释\nvar x = 1;")));
    }

    private static bool ContainsCjk(string text)
        => text.Any(c => c is >= '\u4e00' and <= '\u9fff');

    /// <summary>去掉 // 行注释、/* */ 块注释与 XML 文档注释（只用于扫描，不追求完整词法）。</summary>
    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", "");
    }
}
