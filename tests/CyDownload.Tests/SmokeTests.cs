using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 骨架自检：确认测试宿主能起来。真实用例随各批次增加。
/// </summary>
public sealed class SmokeTests
{
    [Fact]
    public void HostRuns() => Assert.True(true);
}
