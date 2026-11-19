namespace CyDownload.Core.Repos;

/// <summary>
/// 内置源。新库第一次启动时写入 `repos` 表；老库按 <see cref="Version"/> 补缺失项。
/// <para>
/// ** 实测**（HEAD 探测 `Packages.bz2`）：
/// 只有 saurik / bigboss / apt.gs / julioverne / cokepokes 仍然可用；
/// ModMyi、ZodTTD、angelxwind、hbang、rpetrich、tihmstar、coolbooter 等老默认源**已关站或超时**。
/// </para>
/// <para>
/// 关站的源仍然**内置但默认禁用**（`Enabled=false`）：`repos` 里能看到、能启用，
/// 但「全部」页签的批量抓取会跳过它们，不会每次都卡在失败上。
/// </para>
/// </summary>
public static class DefaultSources
{
    /// <summary>内置源清单版本：加/删源时 +1，老库据此补缺失项。</summary>
    public const int Version = 2;

    public static IReadOnlyList<RepoDescriptor> All() =>
    [
        new()
        {
            Name = "saurik",
            BaseUrl = "http://apt.saurik.com",
            Layout = RepoLayout.Dists,
            Suite = "ios/793.00",
            Component = "main",
            Priority = 1,
            Architecture = "iphoneos-arm",
        },
        new()
        {
            Name = "bigboss",
            BaseUrl = "http://apt.thebigboss.org/repofiles/cydia",
            Layout = RepoLayout.Dists,
            Suite = "stable",
            Component = "main",
            Priority = 2,
            Architecture = "iphoneos-arm",
        },
        new()
        {
            Name = "aptgs",
            BaseUrl = "http://apt.gs",
            Layout = RepoLayout.Flat,
            Priority = 3,
            Architecture = "iphoneos-arm",
        },
        new()
        {
            Name = "julioverne",
            BaseUrl = "https://julioverne.github.io",
            Layout = RepoLayout.Flat,
            Priority = 4,
            Architecture = "iphoneos-arm",
        },
        new()
        {
            Name = "cokepokes",
            BaseUrl = "https://cokepokes.github.io",
            Layout = RepoLayout.Flat,
            Priority = 5,
            Architecture = "iphoneos-arm",
        },

        // —— 以下为经典默认源， 实测已关站/超时：内置但默认禁用 ——
        new()
        {
            Name = "modmyi",
            BaseUrl = "http://apt.modmyi.com",
            Layout = RepoLayout.Dists,
            Suite = "stable",
            Component = "main",
            Priority = 20,
            Architecture = "iphoneos-arm",
            Enabled = false,
        },
        new()
        {
            Name = "zodttd",
            BaseUrl = "http://cydia.zodttd.com/repo/cydia",
            Layout = RepoLayout.Dists,
            Suite = "stable",
            Component = "main",
            Priority = 21,
            Architecture = "iphoneos-arm",
            Enabled = false,
        },
        new()
        {
            Name = "angelxwind",
            BaseUrl = "http://cydia.angelxwind.net",
            Layout = RepoLayout.Dists,
            Suite = "stable",
            Component = "main",
            Priority = 22,
            Architecture = "iphoneos-arm",
            Enabled = false,
        },
        new()
        {
            Name = "hbang",
            BaseUrl = "http://cydia.hbang.ws",
            Layout = RepoLayout.Dists,
            Suite = "stable",
            Component = "main",
            Priority = 23,
            Architecture = "iphoneos-arm",
            Enabled = false,
        },
        new()
        {
            Name = "rpetrich",
            BaseUrl = "http://repo.rpetrich.com",
            Layout = RepoLayout.Dists,
            Suite = "stable",
            Component = "main",
            Priority = 24,
            Architecture = "iphoneos-arm",
            Enabled = false,
        },
        new()
        {
            Name = "coolbooter",
            BaseUrl = "http://repo.coolbooter.com",
            Layout = RepoLayout.Dists,
            Suite = "stable",
            Component = "main",
            Priority = 25,
            Architecture = "iphoneos-arm",
            Enabled = false,
        },
    ];
}
