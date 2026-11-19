using System.IO;
using System.Xml.Linq;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 资源字典守门：中英两份的 <c>x:Key</c> 集合必须完全相等，
/// 且键名符合 <c>区域.位置.用途</c> 规范、值不为空。
/// <para>
/// 直接读源文件（不是 BAML）：需要从测试输出目录回溯到仓库根。
/// </para>
/// </summary>
public sealed class LocalizationResourceTests
{
    private const string XNamespaceKey = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string LocalizationDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && directory is not null; i++)
            {
                var candidate = Path.Combine(
                    directory.FullName, "src", "CyDownload.App", "Localization");
                if (Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "找不到 src/CyDownload.App/Localization：从 " + AppContext.BaseDirectory + " 向上回溯失败。");
        }
    }

    private static Dictionary<string, string> LoadKeys(string fileName)
    {
        var path = Path.Combine(LocalizationDirectory, fileName);
        Assert.True(File.Exists(path), "资源文件不存在：" + path);

        var document = XDocument.Load(path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in document.Root!.Elements())
        {
            var key = element.Attribute(XName.Get("Key", XNamespaceKey))?.Value;
            if (key is null) continue;
            result[key] = element.Value;
        }

        return result;
    }

    [Fact]
    public void ChineseAndEnglish_HaveIdenticalKeySets()
    {
        var chinese = LoadKeys("Strings.zh-CN.xaml");
        var english = LoadKeys("Strings.en.xaml");

        Assert.NotEmpty(chinese);
        Assert.Equal(
            chinese.Keys.OrderBy(key => key, StringComparer.Ordinal),
            english.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryKey_FollowsNamingConvention()
    {
        foreach (var key in LoadKeys("Strings.zh-CN.xaml").Keys)
        {
            Assert.Matches(@"^[A-Z][A-Za-z]*(\.[A-Za-z0-9]+)+$", key);
        }
    }

    [Fact]
    public void EveryValue_IsNotEmpty()
    {
        foreach (var (key, value) in LoadKeys("Strings.zh-CN.xaml"))
        {
            Assert.False(string.IsNullOrWhiteSpace(value), "值为空：" + key);
        }

        foreach (var (key, value) in LoadKeys("Strings.en.xaml"))
        {
            Assert.False(string.IsNullOrWhiteSpace(value), "value is empty: " + key);
        }
    }
}
