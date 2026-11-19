namespace CyDownload.Core.Resolve;

/// <summary>
/// 目标设备 + iOS 版本。**全是配置，不是常量** —— 换机器只改这里。
/// <para>
/// 伪包按 <c>firmware.sh</c> 的规则从这些字段算出来，所以字段的语义就是脚本里的语义。
/// </para>
/// </summary>
public sealed record TargetProfile
{
    /// <summary>显示名，如 `iPhone 4S / 6.1.3`。</summary>
    public required string Name { get; init; }

    /// <summary>`hw.machine`，如 `iPhone4,1`；用于生成 `cy+model.*` 与伪包的机型名。</summary>
    public required string ProductType { get; init; }

    /// <summary>`sw_vers -productVersion`，也用于 `firmware` 伪包版本与版本约束判定。</summary>
    public required string IosVersion { get; init; }

    /// <summary>`sw_vers -buildVersion`（如 `10B329`），仅显示用。</summary>
    public string? BuildVersion { get; init; }

    /// <summary>`/usr/libexec/cydia/cfversion` 的输出（如 `793.00`），生成 `cy+lib.corefoundation`。</summary>
    public string? CfVersion { get; init; }

    /// <summary>`kern.osrelease`（如 `13.0.0`），生成 `cy+kernel.darwin`。</summary>
    public string? KernelVersion { get; init; }

    /// <summary>索引架构目录名。arm64 设备要加 `iphoneos-arm64`。</summary>
    public IReadOnlyList<string> Arches { get; init; } = ["iphoneos-arm"];

    /// <summary>`uname -p` 的等价物：有 arm64 架构就是 arm64，否则 arm。</summary>
    public string CpuName =>
        Arches.Any(arch => arch.Contains("arm64", StringComparison.OrdinalIgnoreCase)) ? "arm64" : "arm";

    /// <summary>机型名（`iPhone4,1` → `iPhone`）。</summary>
    public string ModelName => ParseProductType(ProductType).Name;

    /// <summary>机型版本（`iPhone4,1` → `4.1`；脚本里就是 `,` 换成 `.`）。</summary>
    public string ModelVersion => ParseProductType(ProductType).Version;

    /// <summary>实测目标：iPhone 4S / iOS 6.1.3。</summary>
    public static TargetProfile IPhone4S613 { get; } = new()
    {
        Name = "iPhone 4S / 6.1.3",
        ProductType = "iPhone4,1",
        IosVersion = "6.1.3",
        BuildVersion = "10B329",
        CfVersion = "793.00",
        KernelVersion = "13.0.0",
        Arches = ["iphoneos-arm"],
    };

    /// <summary>`iPhone4,1` → (`iPhone`, `4.1`)；`iPad2,1` → (`iPad`, `2.1`)。</summary>
    public static (string Name, string Version) ParseProductType(string productType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productType);

        var firstDigit = productType.IndexOfAny("0123456789".ToCharArray());
        if (firstDigit <= 0)
        {
            return (productType, "0");
        }

        var name = productType[..firstDigit];
        var version = productType[firstDigit..].Replace(',', '.');
        return (name, version);
    }

    /// <summary>用于日志/界面的一行摘要。</summary>
    public string Describe()
    {
        var build = string.IsNullOrEmpty(BuildVersion) ? "" : " / " + BuildVersion;
        return $"{Name}（{ProductType} / iOS {IosVersion}{build} / {string.Join('+', Arches)}）";
    }

    public override string ToString() => Name + " (" + ProductType + " / " + IosVersion + ")";
}
