using System.IO;
using CyDownload.App.Infrastructure;
using CyDownload.App.ViewModels.Library;
using CyDownload.Core.Deb;
using CyDownload.Core.Index;
using CyDownload.Core.Repos;
using Xunit;

namespace CyDownload.Tests;

/// <summary>按 iOS 版本筛选：最低 iOS 解析与兼容判定。</summary>
public sealed class MinIosParserTests
{
    [Theory]
    [InlineData("mobilesubstrate, firmware (>= 7.0)", "7.0")]
    [InlineData("firmware(>=7.1.2)", "7.1.2")]
    [InlineData("cy+os.ios (>= 5.0), zip", "5.0")]
    [InlineData("firmware (>= 6.0), firmware (>= 7.0)", "7.0")]
    [InlineData("zip, unzip", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parse_ExtractsLowestIos(string? depends, string? expected)
        => Assert.Equal(expected, MinIosParser.Parse(depends));

    [Theory]
    [InlineData(null, "6.1.3", true)]
    [InlineData("", "6.1.3", true)]
    [InlineData("6.0", "6.1.3", true)]
    [InlineData("7.0", "6.1.3", false)]
    [InlineData("7.0", "8.1.2", true)]
    [InlineData("8.1.2", "8.1.2", true)]
    public void IsCompatible_UsesNumericVersionCompare(string? minIos, string? target, bool expected)
        => Assert.Equal(expected, MinIosParser.IsCompatible(minIos, target));
}

/// <summary>用户分组文件（外挂 groups.json，抄 IdeviceGui GroupStore）。</summary>
public sealed class UserGroupsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        using var workspace = new TestWorkspace();
        var file = new UserGroupsFile();
        var group = UserGroupsStore.GetOrAdd(file, "修复工具");
        group.Members.Add("com.jjr.nmlfix");
        group.Members.Add("com.jjr.nmlfix");           // 去重
        group.Members.Add("com.jjr.nmfix");

        Assert.True(UserGroupsStore.Save(file, workspace.Root, out var error), error);

        var loaded = UserGroupsStore.Load(workspace.Root, out var warning);

        Assert.Null(warning);
        var restored = Assert.Single(loaded.Groups);
        Assert.Equal("修复工具", restored.Name);
        Assert.Equal(["com.jjr.nmlfix", "com.jjr.nmfix"], restored.Members);
    }

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsEmpty()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Root, UserGroupsStore.FileName), "{ not json");

        var loaded = UserGroupsStore.Load(workspace.Root, out var warning);

        Assert.Empty(loaded.Groups);
        Assert.NotNull(warning);
        Assert.NotEmpty(Directory.GetFiles(workspace.Root, "groups.bad-*.json"));
    }

    [Theory]
    [InlineData("", "name-empty")]
    [InlineData("   ", "name-empty")]
    [InlineData("已有", "name-exists")]
    public void ValidateName_ReportsProblems(string name, string expected)
    {
        var file = new UserGroupsFile();
        UserGroupsStore.GetOrAdd(file, "已有");

        Assert.Equal(expected, UserGroupsStore.ValidateName(file, name));
    }

    [Fact]
    public void Rename_AllowsKeepingOwnName()
    {
        var file = new UserGroupsFile();
        UserGroupsStore.GetOrAdd(file, "A");

        Assert.Null(UserGroupsStore.ValidateName(file, "A", exceptName: "A"));
        Assert.Equal("name-exists", UserGroupsStore.ValidateName(file, "A"));
    }
}

/// <summary>资料库按用户分组过滤 + 列头排序映射。</summary>
public sealed class LibraryGroupFilterTests
{
    private static LibraryPoolEntry Pool(string id, string version, long bytes)
        => new(id, version, "iphoneos-arm", "saurik", Path.Combine("objects", id + ".deb"), bytes, null, "2024-01-01T00:00:00Z");

    private static LibraryGroup Group(long id, string root, params (string Id, string Version)[] members)
        => new(id, 1, root, members[0].Version, "target", "2024-01-01T00:00:00Z", null,
            members.Select((member, index) => new LibraryGroupItem(member.Id, member.Version, "iphoneos-arm", "saurik",
                index == 0, index)).ToList());

    private static LibraryView Build(IEnumerable<LibraryPoolEntry> pool, params LibraryGroup[] groups)
    {
        var view = new LibraryView();
        view.ReplaceAll(pool.ToList(), groups);
        return view;
    }

    [Fact]
    public void AllowedRoots_ShowsOnlyGroupMembers()
    {
        var pool = new[]
        {
            Pool("com.jjr.nmlfix", "1.0", 100),
            Pool("com.jjr.nmfix", "1.0", 100),
        };
        var groups = new[]
        {
            Group(1, "com.jjr.nmlfix", ("com.jjr.nmlfix", "1.0")),
            Group(2, "com.jjr.nmfix", ("com.jjr.nmfix", "1.0")),
        };

        var view = Build(pool, groups);
        view.AllowedRoots = new HashSet<string>(StringComparer.Ordinal) { "com.jjr.nmlfix" };

        var rows = view.Build();

        var single = Assert.Single(rows);
        Assert.Equal("com.jjr.nmlfix", single.RootPackage);
    }

    [Fact]
    public void AllowedRoots_Null_ShowsEverything()
    {
        var groups = new[]
        {
            Group(1, "a", ("a", "1.0")),
            Group(2, "b", ("b", "1.0")),
        };
        var pool = new[] { Pool("a", "1.0", 1), Pool("b", "1.0", 1) };

        var rows = Build(pool, groups).Build();

        Assert.Equal(2, rows.Count);
    }
}

