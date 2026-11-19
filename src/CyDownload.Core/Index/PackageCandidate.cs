using CyDownload.Core.Deb;

namespace CyDownload.Core.Index;

/// <summary>
/// 一个候选（`packages` 一行 + 它所在源的优先级）。
/// 求解时**先比版本再比源优先级**：同名同版本可能在多个源各有一份（实测 `mobilesubstrate 0.9.6301`
/// 在 saurik 与 aptgs 各一行，文件甚至不同），必须靠 priority 决出取哪个。
/// </summary>
public sealed record PackageCandidate(PackageRow Package, int RepoPriority)
{
    public string PackageId => Package.PackageId;

    public string Version => Package.Version;

    public DebianVersion ParsedVersion => DebianVersion.Parse(Package.Version);

    public override string ToString() => $"{Package.PackageId} {Package.Version} [{Package.Repo}/{RepoPriority}]";
}
