using System.Globalization;
using CyDownload.Core.Deb;

namespace CyDownload.Core.Index;

/// <summary>
/// `packages` 表的一行（20 个数据列，）。
/// <para>
/// 字段名与索引原文一一对应：`Package` → <see cref="PackageId"/>、`Name` → <see cref="Name"/>（显示名，可空）。
/// </para>
/// </summary>
public sealed record PackageRow(
    long Id,
    string PackageId,
    string? Name,
    string Version,
    string Architecture,
    string? Section,
    string? Maintainer,
    string? Author,
    string? Website,
    string? Depiction,
    string? Description,
    string? Depends,
    string? PreDepends,
    string? Provides,
    string? Tags,
    string? Filename,
    long? Size,
    long? InstalledSize,
    string? Md5,
    string? Sha256,
    string Repo,
    string? MinIos = null)
{
    /// <summary>
    /// 从一个 control stanza 造行。缺 `Package` / `Version` / `Architecture` 视为坏行（返回 false，
    /// 由调用方计入 skipped）——索引里这种行极少，但**不能猜**。
    /// </summary>
    public static bool TryFromStanza(ControlStanza stanza, string repo, out PackageRow row)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        row = null!;
        var packageId = stanza.Get("Package")?.Trim();
        var version = stanza.Get("Version")?.Trim();
        var architecture = stanza.Get("Architecture")?.Trim();

        if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(version) || string.IsNullOrEmpty(architecture))
        {
            return false;
        }

        row = new PackageRow(
            Id: 0,
            PackageId: packageId,
            Name: NullIfEmpty(stanza.Get("Name")),
            Version: version,
            Architecture: architecture,
            Section: NullIfEmpty(stanza.Get("Section")),
            Maintainer: NullIfEmpty(stanza.Get("Maintainer")),
            Author: NullIfEmpty(stanza.Get("Author")),
            Website: NullIfEmpty(stanza.Get("Website")),
            Depiction: NullIfEmpty(stanza.Get("Depiction")),
            Description: NullIfEmpty(stanza.Get("Description")),
            Depends: NullIfEmpty(stanza.Get("Depends")),
            PreDepends: NullIfEmpty(stanza.Get("Pre-Depends")),
            Provides: NullIfEmpty(stanza.Get("Provides")),
            Tags: NullIfEmpty(stanza.Get("Tag") ?? stanza.Get("Tags")),
            Filename: NullIfEmpty(stanza.Get("Filename")),
            Size: ParseLong(stanza.Get("Size")),
            InstalledSize: ParseLong(stanza.Get("Installed-Size")),
            Md5: NullIfEmpty(stanza.Get("MD5sum")),
            Sha256: NullIfEmpty(stanza.Get("SHA256")),
            Repo: repo,
            MinIos: Deb.MinIosParser.Parse(stanza.Get("Depends")) ?? "");


        return true;
    }

    /// <summary>`Provides:` 拆成名字列表（写 `provides` 表用）。</summary>
    public IReadOnlyList<ProvidedName> ProvidedNames() => ProvidedName.ParseField(Provides);

    /// <summary>这个名字是否被本行提供（不看版本）。</summary>
    public bool ProvidesName(string name)
        => ProvidedNames().Any(item => string.Equals(item.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// 本行是否满足「依赖 <paramref name="name"/>（可带版本约束）」：
    /// 名字相同看自身 `Version`；由 `Provides` 命中则看提供时的版本
    /// （**无版本的 Provides 不能满足版本化依赖**，与 dpkg 一致）。
    /// <para>
    /// **自身版本串非法时一律返回 false（不抛）**——真实库里确实存在
    /// `1.0_biuld1008` 这种第三方源自造的版本串（实测 5 行），
    /// 让它抛异常会把整次求解带走。
    /// </para>
    /// </summary>
    public bool Satisfies(string name, VersionConstraint? constraint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (string.Equals(PackageId, name, StringComparison.Ordinal))
        {
            if (constraint is not { } direct) return true;
            return DebianVersion.TryParse(Version, out var own) && direct.IsSatisfiedBy(own);
        }

        foreach (var item in ProvidedNames())
        {
            if (!string.Equals(item.Name, name, StringComparison.Ordinal)) continue;
            if (constraint is not { } required) return true;
            return item.Version is { } provided && required.IsSatisfiedBy(provided);
        }

        return false;
    }

    /// <summary>`Depends` + `Pre-Depends` 的并集（求解时两者都要解）。</summary>
    public DependencyExpr Dependencies()
    {
        var depends = DependencyExpr.Parse(Depends);
        var preDepends = DependencyExpr.Parse(PreDepends);

        if (preDepends.IsEmpty) return depends;
        if (depends.IsEmpty) return preDepends;

        return new DependencyExpr([.. preDepends.Clauses, .. depends.Clauses])
        {
            Unparsed = [.. preDepends.Unparsed, .. depends.Unparsed],
        };
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? ParseLong(string? value)
        => long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    public override string ToString() => $"{PackageId} {Version} [{Repo}]";
}
