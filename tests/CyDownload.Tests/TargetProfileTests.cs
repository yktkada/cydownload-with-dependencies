using CyDownload.Core.Resolve;
using Xunit;

namespace CyDownload.Tests;

/// <summary>目标设备参数与机型名推导（伪包靠它）。</summary>
public sealed class TargetProfileTests
{
    [Theory]
    [InlineData("iPhone4,1", "iPhone", "4.1")]
    [InlineData("iPhone5,2", "iPhone", "5.2")]
    [InlineData("iPad2,1", "iPad", "2.1")]
    [InlineData("iPod5,1", "iPod", "5.1")]
    [InlineData("iPhone3,1", "iPhone", "3.1")]
    public void ParseProductType_SplitsNameAndVersion(string productType, string name, string version)
    {
        var (parsedName, parsedVersion) = TargetProfile.ParseProductType(productType);

        Assert.Equal(name, parsedName);
        Assert.Equal(version, parsedVersion);
    }

    [Fact]
    public void CpuName_IsArmForOldDevices_AndArm64WhenArchitectureSaysSo()
    {
        Assert.Equal("arm", TargetProfile.IPhone4S613.CpuName);

        var modern = TargetProfile.IPhone4S613 with { Arches = ["iphoneos-arm64"] };
        Assert.Equal("arm64", modern.CpuName);
    }

    [Fact]
    public void IPhone4S613_HasMeasuredValues()
    {
        var target = TargetProfile.IPhone4S613;

        Assert.Equal("iPhone4,1", target.ProductType);
        Assert.Equal("6.1.3", target.IosVersion);
        Assert.Equal("10B329", target.BuildVersion);
        Assert.Equal("793.00", target.CfVersion);
        Assert.Equal("13.0.0", target.KernelVersion);
        Assert.Equal("iPhone", target.ModelName);
        Assert.Equal("4.1", target.ModelVersion);
    }
}
