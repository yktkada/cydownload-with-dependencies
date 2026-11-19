using CyDownload.Core.Deb;
using CyDownload.Core.Resolve;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// base 与伪包。
/// 伪包规则是从 `firmware.sh` 全文抄的，这里逐条钉住——写错会「误判已装 → 漏依赖 → 装不上」。
/// </summary>
public sealed class BasePackageProviderTests
{
    private static VersionConstraint Constraint(string op, string version)
    {
        Assert.True(VersionConstraint.TryParse(op, version, out var constraint));
        return constraint;
    }

    [Fact]
    public void Pseudo_IPhone4S613_MatchesFirmwareScriptRules()
    {
        var provider = new BasePackageProvider(TargetProfile.IPhone4S613);

        Assert.Equal("6.1.3", provider.PseudoPackages["firmware"]);
        Assert.Equal("6.1.3", provider.PseudoPackages["cy+os.ios"]);
        Assert.Equal("0", provider.PseudoPackages["cy+cpu.arm"]);          // 版本恒 0
        Assert.Equal("4.1", provider.PseudoPackages["cy+model.iphone"]);   // iPhone4,1 → 4,1 → 4.1
        Assert.Equal("13.0.0", provider.PseudoPackages["cy+kernel.darwin"]);
        Assert.Equal("793.00", provider.PseudoPackages["cy+lib.corefoundation"]);
    }

    [Theory]
    [InlineData("iPad2,1", "cy+model.ipad", "2.1")]
    [InlineData("iPod5,1", "cy+model.ipod", "5.1")]
    [InlineData("iPhone5,2", "cy+model.iphone", "5.2")]
    public void Pseudo_ModelNameAndVersion_DerivedFromProductType(string productType, string expectedKey, string expectedVersion)
    {
        var target = TargetProfile.IPhone4S613 with { ProductType = productType, Arches = ["iphoneos-arm"] };
        var provider = new BasePackageProvider(target);

        Assert.Equal(expectedVersion, provider.PseudoPackages[expectedKey]);
    }

    [Fact]
    public void Pseudo_Arm64Device_UsesCyCpuArm64()
    {
        var target = TargetProfile.IPhone4S613 with { ProductType = "iPhone6,1", Arches = ["iphoneos-arm64"] };
        var provider = new BasePackageProvider(target);

        Assert.True(provider.PseudoPackages.ContainsKey("cy+cpu.arm64"));
        Assert.False(provider.PseudoPackages.ContainsKey("cy+cpu.arm"));
    }

    [Fact]
    public void FirmwareConstraint_IsJudgedAgainstTargetIosVersion()
    {
        var provider = new BasePackageProvider(TargetProfile.IPhone4S613);

        // 实测：uikittools 依赖 firmware (<< 8.3) | com.saurik.patcyh —— 在 6.1.3 上成立
        Assert.True(provider.IsSatisfied("firmware", Constraint("<<", "8.3"), out var source));
        Assert.Equal("pseudo", source);

        // firmware (>= 7.0) 在 6.1.3 上不成立 → 该候选不可用
        Assert.False(provider.IsSatisfied("firmware", Constraint(">=", "7.0"), out _));
    }

    [Fact]
    public void GscCapabilities_AreAlwaysSatisfied_WithoutWhitelist()
    {
        var provider = new BasePackageProvider(TargetProfile.IPhone4S613);

        // 83 个 gsc.* 随设备变，不可能穷举 → 一律放行
        Assert.True(provider.IsSatisfied("gsc.retina", null, out var source));
        Assert.Equal("capability", source);
        Assert.True(provider.IsSatisfied("gsc.opengles-2", Constraint(">=", "1.0"), out _));
    }

    [Fact]
    public void DefaultProvider_DoesNotAssumeAnythingInstalled()
    {
        var provider = new BasePackageProvider(TargetProfile.IPhone4S613);

        Assert.False(provider.IsSatisfied("dpkg", null, out _));
        Assert.False(provider.IsSatisfied("bash", Constraint(">=", "4.0"), out _));
    }

    [Fact]
    public void Preset31_ProvidesBasePackages()
    {
        var provider = new BasePackageProvider(
            TargetProfile.IPhone4S613, installed: null, usePreset: true);

        Assert.True(provider.IsSatisfied("dpkg", Constraint(">=", "1.14.25-8"), out var source));
        Assert.Equal("base", source);

        // 预设里 dpkg 是 1.18.10-12，不满足 >= 2.0
        Assert.False(provider.IsSatisfied("dpkg", Constraint(">=", "2.0"), out _));
    }

    [Fact]
    public void InstalledStatus_OverridesPresetAndIsReportedAsBase()
    {
        var installed = new Dictionary<string, string> { ["apt7-lib"] = "0.7.25.3-16" };
        var provider = new BasePackageProvider(TargetProfile.IPhone4S613, installed, usePreset: true);

        Assert.True(provider.IsSatisfied("apt7-lib", Constraint(">=", "0.7.25.3-16"), out var source));
        Assert.Equal("base", source);

        // 设备实际版本比预设新：预设的 -14 不会覆盖它
        Assert.True(provider.TryGetBaseVersion("apt7-lib", out var version));
        Assert.Equal("0.7.25.3-16", version);
    }

    [Fact]
    public void TryGetBaseVersion_WorksForPseudoAndInstalled()
    {
        var provider = new BasePackageProvider(
            TargetProfile.IPhone4S613,
            new Dictionary<string, string> { ["bash"] = "4.0.44-16" });

        Assert.True(provider.TryGetBaseVersion("firmware", out var firmware));
        Assert.Equal("6.1.3", firmware);

        Assert.True(provider.TryGetBaseVersion("bash", out var bash));
        Assert.Equal("4.0.44-16", bash);

        Assert.False(provider.TryGetBaseVersion("nope", out _));
    }

    [Fact]
    public void Matches_HandlesMissingAndInvalidVersions()
    {
        Assert.True(BasePackageProvider.Matches("1.0", null));
        Assert.True(BasePackageProvider.Matches("1.0", Constraint(">=", "0.9")));
        Assert.False(BasePackageProvider.Matches("1.0", Constraint(">>", "1.0")));

        // 非法版本串按「不满足」处理，不抛
        Assert.False(BasePackageProvider.Matches("not-a-version!", Constraint(">=", "0.9")));
    }

    [Fact]
    public void Pseudo_OmitsOptionalFieldsWhenTargetLacksThem()
    {
        var target = TargetProfile.IPhone4S613 with { CfVersion = null, KernelVersion = null };
        var provider = new BasePackageProvider(target);

        Assert.False(provider.PseudoPackages.ContainsKey("cy+lib.corefoundation"));
        Assert.False(provider.PseudoPackages.ContainsKey("cy+kernel.darwin"));
        Assert.True(provider.PseudoPackages.ContainsKey("firmware"));
    }
}
