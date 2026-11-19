using CyDownload.Core.Repos;
using CyDownload.Core.Resolve;
using Xunit;

namespace CyDownload.Tests;

/// <summary>内置机型：参数是否与实测一致、能否按机型回显。</summary>
public sealed class BuiltInTargetsTests
{
    [Fact]
    public void IPhone4S613_MatchesMeasuredDevice()
    {
        var target = BuiltInTargets.IPhone4S613;

        Assert.Equal("iPhone4,1", target.ProductType);
        Assert.Equal("6.1.3", target.IosVersion);
        Assert.Equal("10B329", target.BuildVersion);
        Assert.Equal("793.00", target.CfVersion);
        Assert.Equal("13.0.0", target.KernelVersion);
    }

    [Fact]
    public void IPhone5_812_MatchesMeasuredDevice()
    {
        //  从连接的 iPhone 5 直接读：ideviceinfo + 设备上 cfversion / uname
        var target = BuiltInTargets.IPhone5_812;

        Assert.Equal("iPhone5,2", target.ProductType);
        Assert.Equal("8.1.2", target.IosVersion);
        Assert.Equal("12B440", target.BuildVersion);
        Assert.Equal("1141.16", target.CfVersion);
        Assert.Equal("14.0.0", target.KernelVersion);
        Assert.Equal(["iphoneos-arm"], target.Arches);
        Assert.Equal("arm", target.CpuName);
        Assert.Equal("iPhone", target.ModelName);
        Assert.Equal("5.2", target.ModelVersion);
    }

    [Fact]
    public void All_ContainsAllMeasuredPresets()
    {
        Assert.Equal(4, BuiltInTargets.All().Count);
        Assert.Contains(BuiltInTargets.All(), target => target.ProductType == "iPhone4,1");
        Assert.Contains(BuiltInTargets.All(), target => target.ProductType == "iPhone5,2");
        Assert.Contains(BuiltInTargets.All(), target => target.ProductType == "iPhone3,1");
        Assert.Contains(BuiltInTargets.All(), target => target.ProductType == "iPhone5,1");
    }

    [Fact]
    public void IPhone4_41_MatchesMeasuredDevice()
    {
        //  连接设备实测：ideviceinfo + 设备上 cfversion / uname -r
        var target = BuiltInTargets.IPhone4_41;

        Assert.Equal("iPhone3,1", target.ProductType);
        Assert.Equal("4.1", target.IosVersion);
        Assert.Equal("8B117", target.BuildVersion);
        Assert.Equal("550.38", target.CfVersion);
        Assert.Equal("10.3.1", target.KernelVersion);
        Assert.Equal(["iphoneos-arm"], target.Arches);
    }

    [Fact]
    public void IPhone5_932_MatchesMeasuredDevice()
    {
        //  连接设备实测（iPhone5,1 / GSM）：cf 1290.11、Darwin 15.5.0、build 13F69
        var target = BuiltInTargets.IPhone5_932;

        Assert.Equal("iPhone5,1", target.ProductType);
        Assert.Equal("9.3.2", target.IosVersion);
        Assert.Equal("13F69", target.BuildVersion);
        Assert.Equal("1290.11", target.CfVersion);
        Assert.Equal("15.5.0", target.KernelVersion);
        Assert.Equal(["iphoneos-arm"], target.Arches);
    }

    [Fact]
    public void Find_ByProductTypeAndVersion()
    {
        Assert.NotNull(BuiltInTargets.Find("iPhone5,2", "8.1.2"));
        Assert.NotNull(BuiltInTargets.Find(" iPhone4,1 ", " 6.1.3 "));
        Assert.Null(BuiltInTargets.Find("iPhone5,2", "8.1.1"));
        Assert.Null(BuiltInTargets.Find(null, "8.1.2"));
    }

    [Fact]
    public void Match_RequiresSameCfAndKernel()
    {
        var profile = BuiltInTargets.IPhone5_812;

        Assert.NotNull(BuiltInTargets.Match(profile));

        var edited = profile with { CfVersion = "999.0" };
        Assert.Null(BuiltInTargets.Match(edited));
    }
}
