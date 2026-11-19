using CyDownload.Core.Resolve;

namespace CyDownload.Core.Repos;

/// <summary>
/// 内置机型。参数都是**实测**的，不是猜的：
/// <list type="bullet">
/// <item>iPhone 4S / 6.1.3 ——  真机实验（`）；</item>
/// <item>iPhone 5 / 8.1.2 ——  从连接设备直接读（`ideviceinfo` + 设备上 `cfversion`/`uname`）；</item>
/// <item>iPhone 4 / 4.1 ——  连接设备实测（iPhone3,1 / 8B117 / cf 550.38 / Darwin 10.3.1）；</item>
/// <item>iPhone 5 / 9.3.2 ——  连接设备实测（iPhone5,1 / 13F69 / cf 1290.11 / Darwin 15.5.0）。</item>
/// </list>
/// <para>
/// 机型只是**预设**：设置窗口里选中后仍可手改（自定义设备的值优先）。
/// `cfversion` 用于伪包 `cy+lib.corefoundation`，`kernel` 用于 `cy+kernel.darwin`，都按 firmware.sh 的规则生成。
/// </para>
/// </summary>
public static class BuiltInTargets
{
    /// <summary>iPhone 4S / iOS 6.1.3（实测：cf 793.00、Darwin 13.0.0）。</summary>
    public static TargetProfile IPhone4S613 => TargetProfile.IPhone4S613;

    /// <summary>iPhone 5 / iOS 8.1.2（实测：build 12B440、cf 1141.16、Darwin 14.0.0、armv7s）。</summary>
    public static TargetProfile IPhone5_812 => new()
    {
        Name = "iPhone 5 / 8.1.2",
        ProductType = "iPhone5,2",
        IosVersion = "8.1.2",
        BuildVersion = "12B440",
        CfVersion = "1141.16",
        KernelVersion = "14.0.0",
        Arches = ["iphoneos-arm"],
    };

    /// <summary>iPhone 4 / iOS 4.1（实测：build 8B117、cf 550.38、Darwin 10.3.1、armv7）。</summary>
    public static TargetProfile IPhone4_41 => new()
    {
        Name = "iPhone 4 / 4.1",
        ProductType = "iPhone3,1",
        IosVersion = "4.1",
        BuildVersion = "8B117",
        CfVersion = "550.38",
        KernelVersion = "10.3.1",
        Arches = ["iphoneos-arm"],
    };

    /// <summary>iPhone 5 / iOS 9.3.2（实测：iPhone5,1 / build 13F69、cf 1290.11、Darwin 15.5.0、armv7s）。</summary>
    public static TargetProfile IPhone5_932 => new()
    {
        Name = "iPhone 5 / 9.3.2",
        ProductType = "iPhone5,1",
        IosVersion = "9.3.2",
        BuildVersion = "13F69",
        CfVersion = "1290.11",
        KernelVersion = "15.5.0",
        Arches = ["iphoneos-arm"],
    };

    public static IReadOnlyList<TargetProfile> All() => [IPhone4S613, IPhone5_812, IPhone4_41, IPhone5_932];

    /// <summary>按 `ProductType + IosVersion` 找内置机型（设置窗口回显用）；找不到返回 null。</summary>
    public static TargetProfile? Find(string? productType, string? iosVersion)
    {
        if (string.IsNullOrWhiteSpace(productType) || string.IsNullOrWhiteSpace(iosVersion)) return null;

        return All().FirstOrDefault(target =>
            string.Equals(target.ProductType, productType.Trim(), StringComparison.Ordinal) &&
            string.Equals(target.IosVersion, iosVersion.Trim(), StringComparison.Ordinal));
    }

    /// <summary>把设置里的目标字段与内置机型比对：完全一致才认为是「内置预设」。</summary>
    public static TargetProfile? Match(TargetProfile profile)
        => Find(profile.ProductType, profile.IosVersion) is { } preset &&
           string.Equals(preset.CfVersion ?? "", profile.CfVersion ?? "", StringComparison.Ordinal) &&
           string.Equals(preset.KernelVersion ?? "", profile.KernelVersion ?? "", StringComparison.Ordinal)
            ? preset
            : null;
}
