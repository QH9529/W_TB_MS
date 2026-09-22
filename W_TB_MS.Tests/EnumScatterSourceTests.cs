using Xunit;
using W_TB_MS;

namespace W_TB_MS.Tests;

/// <summary>
/// EnumScatterSource（运行模式枚举阶梯线）边界行为测试：
/// FindNearestIndex 二分查找在鼠标 X 越界/渲染窗口为空时不得抛异常，且返回合法索引。
/// </summary>
public class EnumScatterSourceTests
{
    private static W_TB_MS.MainForm.EnumScatterSource CreateSource(
        List<double> times, List<ushort> values) =>
        new(times, values, W_TB_MS.MainForm.RuntimeModeMeanings);

    [Fact]
    public void FindNearestIndex_DoesNotThrow_WhenMouseBeforeFirstPoint()
    {
        // 旧实现 Math.Clamp(0, 0, -1) 在此处抛 ArgumentException
        var source = CreateSource(new List<double> { 100, 200, 300 }, new List<ushort> { 1, 2, 3 });

        var ex = Record.Exception(() => source.FindNearestIndex(50));

        Assert.Null(ex);
        Assert.Equal(0, source.FindNearestIndex(50)); // 夹回首点
    }

    [Fact]
    public void FindNearestIndex_DoesNotThrow_WhenMouseAfterLastPoint()
    {
        var source = CreateSource(new List<double> { 100, 200, 300 }, new List<ushort> { 1, 2, 3 });

        var ex = Record.Exception(() => source.FindNearestIndex(999));

        Assert.Null(ex);
        Assert.Equal(2, source.FindNearestIndex(999)); // 夹回末点
    }

    [Fact]
    public void FindNearestIndex_ReturnsMinusOne_WhenRenderWindowEmpty()
    {
        var source = CreateSource(new List<double> { 100, 200, 300 }, new List<ushort> { 1, 2, 3 });
        source.MinRenderIndex = 2;
        source.MaxRenderIndex = 1; // 窗口为空：min > max

        Assert.Equal(-1, source.FindNearestIndex(200));
    }

    [Fact]
    public void FindNearestIndex_RespectsRenderWindow()
    {
        var source = CreateSource(new List<double> { 100, 200, 300 }, new List<ushort> { 1, 2, 3 });
        source.MinRenderIndex = 1;

        // x=100 在窗口起点之前：不应夹回 0，而是窗口首点 1
        Assert.Equal(1, source.FindNearestIndex(100));
        Assert.Equal(2, source.FindNearestIndex(300));
    }

    [Fact]
    public void FindNearestIndex_ReturnsMinusOne_WhenListsEmpty()
    {
        var source = CreateSource(new List<double>(), new List<ushort>());

        Assert.Equal(-1, source.FindNearestIndex(100));
    }

    [Fact]
    public void FindNearestIndex_ReturnsExactMatch()
    {
        var source = CreateSource(new List<double> { 100, 200, 300 }, new List<ushort> { 1, 2, 3 });

        Assert.Equal(1, source.FindNearestIndex(200));
    }

    [Fact]
    public void GetMeaning_FallsBackToRawValue()
    {
        var source = CreateSource(new List<double> { 100 }, new List<ushort> { 5 });

        Assert.Equal("热水运行", source.GetMeaning(5));
        Assert.Equal("99", source.GetMeaning(99));
    }
}
