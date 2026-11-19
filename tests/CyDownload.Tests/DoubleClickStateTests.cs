using CyDownload.App.Mvvm;
using Xunit;

namespace CyDownload.Tests;

/// <summary>双击判定（固定 400 ms，不跟系统设置）。</summary>
public sealed class DoubleClickStateTests
{
    [Fact]
    public void FirstClick_IsNotDoubleClick() => Assert.False(DoubleClickState.IsSecondClick(null, 1000));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(399)]
    [InlineData(DoubleClickState.WindowMs)]
    public void WithinWindow_IsDoubleClick(long delta)
        => Assert.True(DoubleClickState.IsSecondClick(1000, 1000 + delta));

    [Theory]
    [InlineData(DoubleClickState.WindowMs + 1L)]
    [InlineData(1000)]
    public void BeyondWindow_IsNotDoubleClick(long delta)
        => Assert.False(DoubleClickState.IsSecondClick(1000, 1000 + delta));

    [Fact]
    public void ClockGoingBackwards_IsNotDoubleClick()
        => Assert.False(DoubleClickState.IsSecondClick(1000, 999));
}
