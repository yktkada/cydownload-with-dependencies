using CyDownload.App.Mvvm;
using Xunit;

namespace CyDownload.Tests;

/// <summary>拖动范围选择（搬自 iDeviceGui，唯一实现）。</summary>
public sealed class MultiSelectRangeTests
{
    private sealed class Row : IMultiSelectRow
    {
        public bool IsMultiSelected { get; set; }
        public bool Locked { get; init; }
        public override string ToString() => $"{IsMultiSelected}";
    }

    private static List<Row> FiveRows() => [new Row(), new Row(), new Row(), new Row(), new Row()];

    [Fact]
    public void ForwardRange_SelectsInclusive()
    {
        var rows = FiveRows();
        var applied = MultiSelectRange.Apply(rows, rows[1], rows[3], new HashSet<Row>(), _ => false);

        Assert.True(applied);
        Assert.Equal([false, true, true, true, false], rows.Select(row => row.IsMultiSelected));
    }

    [Fact]
    public void ReverseRange_SelectsInclusive()
    {
        var rows = FiveRows();
        MultiSelectRange.Apply(rows, rows[3], rows[1], new HashSet<Row>(), _ => false);

        Assert.Equal([false, true, true, true, false], rows.Select(row => row.IsMultiSelected));
    }

    [Fact]
    public void Range_TogglesAgainstBaseSelection()
    {
        var rows = FiveRows();
        rows[2].IsMultiSelected = true;

        // 基准集里已有 0/2/4 → 区间 1..3 内的选中状态取反（Explorer 行为）
        MultiSelectRange.Apply(rows, rows[1], rows[3], new HashSet<Row> { rows[0], rows[2], rows[4] }, _ => false);

        Assert.Equal([true, true, false, true, true], rows.Select(row => row.IsMultiSelected));
    }

    [Fact]
    public void Skip_LeavesRowUntouched()
    {
        var rows = new List<Row> { new(), new() { Locked = true }, new(), new() };
        rows[0].IsMultiSelected = true;

        MultiSelectRange.Apply(rows, rows[0], rows[2], new HashSet<Row>(), row => row.Locked);

        // 第 1 行被 skip：保持原状态（false），不被区间点亮；其余按区间切换
        Assert.Equal([true, false, true, false], rows.Select(row => row.IsMultiSelected));
    }

    [Fact]
    public void AnchorMissing_ReturnsFalse_AndChangesNothing()
    {
        var rows = FiveRows();
        var applied = MultiSelectRange.Apply(rows, new Row(), rows[2], new HashSet<Row>(), _ => false);

        Assert.False(applied);
        Assert.All(rows, row => Assert.False(row.IsMultiSelected));
    }
}
