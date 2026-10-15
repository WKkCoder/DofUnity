using Dnf.Game;
using Xunit;

namespace Dnf.Game.Tests;

/// <summary>
/// 数学类原生接口的纯函数部分。期望值由另一份独立的 Python 实现算出(单精度每一步取整,与客户端汇编的 SSE 序列一一对应),
/// 不是拿被测函数自己的输出当期望值。
/// </summary>
public class NativeMathTests
{
    [Theory]
    [InlineData(100, 200, 50, 100, 150)]
    [InlineData(100, 200, 0, 100, 100)]     // 时间不到 1:起点
    [InlineData(100, 200, -5, 100, 100)]
    [InlineData(100, 200, 100, 100, 200)]   // 够了总时间:终点
    [InlineData(100, 200, 500, 100, 200)]
    [InlineData(200, 100, 25, 100, 175)]
    [InlineData(0, 10, 1, 3, 3)]            // 3.33 向零截断
    [InlineData(0, -10, 1, 3, -3)]          // -3.33 向零截断,不是向下取整
    [InlineData(0, 100, 33, 100, 33)]
    [InlineData(0, 255, 7, 100, 17)]        // 17.85 截断成 17,不是四舍五入成 18
    public void UniformVelocityTruncatesTowardZero(int from, int to, int t, int total, int expected)
    {
        Assert.Equal(expected, NativeMath.Uniform(from, to, t, total));
    }

    [Theory]
    [InlineData(0, 10, 0, 50, 0)]
    [InlineData(0, 10, 25, 50, 5)]          // 第 4 个参数是单程时间
    [InlineData(0, 10, 50, 50, 10)]         // 单程走完:在终点
    [InlineData(0, 10, 75, 50, 5)]          // 回程的一半
    [InlineData(0, 10, 100, 50, 0)]         // 来回走完:回到起点
    [InlineData(0, 10, 12, 50, 2)]
    [InlineData(0, 10, -5, 50, 0)]          // 负的时间:起点
    [InlineData(55, 65, 300, 1200, 57)]
    [InlineData(0, 6, 70, 100, 4)]
    public void ShuttleTakesTheOneWayDurationNotThePeriod(int from, int to, int t, int total, int expected)
    {
        Assert.Equal(expected, NativeMath.Shuttle(from, to, t, total));
    }

    [Theory]
    [InlineData(0, 100, 50, 100, true, 75)]
    [InlineData(0, 100, 50, 100, false, 24)]   // 理想值 25;单精度的 0.02 偏小一点点,结果 24.99999 向零截断成 24,客户端也是如此
    [InlineData(0, 100, 0, 100, true, 0)]
    [InlineData(0, 100, 100, 100, false, 100)]
    [InlineData(0, 100, 25, 100, true, 43)]    // 先快后慢:100 × (2×0.25 - 0.0625) = 43.75
    [InlineData(0, 100, 25, 100, false, 6)]    // 先慢后快:100 × 0.0625 = 6.25
    [InlineData(0, 100, 75, 100, true, 93)]
    [InlineData(0, 100, 75, 100, false, 56)]
    [InlineData(100, 0, 25, 100, true, 56)]    // 从大到小也成立
    [InlineData(100, 0, 25, 100, false, 93)]
    [InlineData(0, 35, 150, 300, true, 26)]
    [InlineData(0, 35, 150, 300, false, 8)]
    [InlineData(0, 1000, 333, 1000, true, 555)]
    [InlineData(0, 1000, 333, 1000, false, 110)]
    public void AccelTrueIsEaseOutAndFalseIsEaseIn(int from, int to, int t, int total, bool easeOut, int expected)
    {
        Assert.Equal(expected, NativeMath.Accel(from, to, t, total, easeOut));
    }

    [Fact]
    public void AccelClampsToTheEndpointsOutsideTheDuration()
    {
        Assert.Equal(40, NativeMath.Accel(40, 90, 0, 100, true));
        Assert.Equal(40, NativeMath.Accel(40, 90, -3, 100, false));
        Assert.Equal(90, NativeMath.Accel(40, 90, 100, 100, true));
        Assert.Equal(90, NativeMath.Accel(40, 90, 250, 100, false));
    }

    [Fact]
    public void DistanceFlatAndDepthScaled()
    {
        Assert.Equal(5, NativeMath.Distance(0, 0, 3, 4, true));
        Assert.Equal(5, NativeMath.Distance(3, 4, 0, 0, true));
        Assert.Equal(0, NativeMath.Distance(7, 7, 7, 7, true));
        // 开方向零截断:边长 1 和 1 的距离是 1.41,取 1
        Assert.Equal(1, NativeMath.Distance(0, 0, 1, 1, true));
        // 不缩放时纵向 4 保持 4;缩放时纵向差 4 除以 0.29 = 13.79,截断成 13,横向 3:开方 13.34,取 13
        Assert.Equal(13, NativeMath.Distance(0, 0, 3, 4, false));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(359, 359)]
    [InlineData(360, 0)]
    [InlineData(361, 1)]
    [InlineData(720, 0)]
    [InlineData(1000, 280)]
    [InlineData(-1, 359)]
    [InlineData(-360, 0)]
    [InlineData(-361, 359)]
    [InlineData(-720, 0)]
    [InlineData(-1000, 80)]
    public void DegreeTableIndexWrapsToANonNegativeRemainder(int degrees, int expected)
    {
        Assert.Equal(expected, NativeMath.DegreeIndex(degrees));
    }

    [Fact]
    public void AbsIsTheIntegerBitTrickIncludingTheMostNegativeNumber()
    {
        Assert.Equal(7, NativeMath.Abs(-7));
        Assert.Equal(7, NativeMath.Abs(7));
        Assert.Equal(0, NativeMath.Abs(0));
        Assert.Equal(int.MinValue, NativeMath.Abs(int.MinValue)); // 客户端的位运算写法没有这个数的正数,结果还是它自己
    }

    [Fact]
    public void ColorsArePackedLikeTheClient()
    {
        Assert.Equal(unchecked((int)0xFF030201), NativeMath.Rgb(1, 2, 3)); // 0xFFBBGGRR:红在最低位
        Assert.Equal(unchecked((int)0xFFFFFFFF), NativeMath.Rgb(255, 255, 255));
        Assert.Equal(unchecked((int)0xFF0000FF), NativeMath.Rgb(255, 0, 0));
        Assert.Equal(unchecked((int)0x80FFFFFF), NativeMath.Alpha(128));    // 0xAAFFFFFF
        Assert.Equal(unchecked((int)0x00FFFFFF), NativeMath.Alpha(0));
        Assert.Equal(unchecked((int)0xFFFFFFFF), NativeMath.Alpha(255));
        Assert.Equal(unchecked((int)0xFFFFFFFF), NativeMath.Alpha(255 + 256)); // 只取低 8 位
    }
}
