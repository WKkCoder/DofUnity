namespace Dnf.Game.Tests;

/// <summary>
/// 精确的"整数 × 单精度 ÷ 单精度"向零取整。依据:服务端 get_disjoint_result_ 0x085d234a 在 32 位 x87 上的这几步(FILD 整数、FMUL/FDIV 单精度、
/// 取整前把舍入方式改成向零)。期望值由另一份独立实现(Python 的 fractions.Fraction,<c>python3 tools/py/disjoint_ref.py --exact</c>)算出,不是照着被测代码抄的。
/// </summary>
public class ExactFloatTests
{
    [Theory]
    [InlineData(1000L, 1.1f, 1100)]               // 1.1f 比 1.1 略大:1000 × 它 = 1100.0000238…
    [InlineData(10L, 0.7f, 6)]                    // 0.7f 比 0.7 略小:6.9999998… → 6
    [InlineData(-10L, 0.7f, -6)]                  // 向零取整:负数往 0 靠
    [InlineData(30L, 0.1f, 3)]
    [InlineData(20L, 0.1f, 2)]
    [InlineData(100L, 0.3f, 30)]
    [InlineData(7L, 1.5f, 10)]
    [InlineData(3L, -0.5f, -1)]
    [InlineData(1000000L, 1.1f, 1100000)]
    [InlineData(99999999L, 1.1f, 110000001)]
    [InlineData(20000000L, 1.1f, 22000000)]
    [InlineData(2147483647L, 2.0f, int.MinValue)]   // 超出 32 位:整数不定值
    [InlineData(-2147483648L, 1.0f, int.MinValue)]  // 恰好是 int.MinValue 本身
    [InlineData(2147483647L, 1.0f, 2147483647)]     // 恰好是 int.MaxValue:还装得下
    [InlineData(2147483648L, 1.0f, int.MinValue)]   // 比 int.MaxValue 大 1:装不下
    [InlineData(-2147483647L, 1.0f, -2147483647)]   // 比 int.MinValue 大 1:装得下
    [InlineData(-2147483649L, 1.0f, int.MinValue)]  // 比 int.MinValue 小 1:装不下,给不定值
    public void MultiplyingTruncatesTowardZeroExactly(long a, float f, int expected)
    {
        Assert.Equal(expected, ExactFloat.TruncMul(a, f));
    }

    [Theory]
    [InlineData(11000L, 1.5f, 1000f, 16)]
    [InlineData(55000L, 2.0f, 1000f, 110)]
    [InlineData(7700L, 1.5f, 1000f, 11)]
    [InlineData(10L, 1.5f, 1000f, 0)]
    [InlineData(2147483647L, 3.5f, 1000f, 7516192)]
    [InlineData(1000L, 1.0f, 3.0f, 333)]
    [InlineData(99999L, 0.1f, 1000f, 9)]
    [InlineData(1234567L, 2.5f, 0.3f, 10288057)]
    [InlineData(1L, 1.0f, 0.0f, int.MinValue)]      // 除以零:整数不定值
    public void MultiplyingThenDividingTruncatesTowardZeroExactly(long a, float f, float d, int expected)
    {
        Assert.Equal(expected, ExactFloat.TruncMulDiv(a, f, d));
    }

    [Theory]
    [InlineData(3500L, 1250.0f, 4750)]
    [InlineData(100L, -0.5f, 99)]
    [InlineData(-100L, 0.5f, -99)]
    [InlineData(1000L, 0.99999994f, 1000)]
    [InlineData(1500L, 9990.0f, 11490)]
    [InlineData(0L, 3330.0f, 3330)]
    [InlineData(2147483647L, 1.0f, int.MinValue)]
    [InlineData(2147483646L, 1.0f, 2147483647)]     // 恰好到 int.MaxValue:装得下
    [InlineData(-2147483648L, -1.0f, int.MinValue)] // 比 int.MinValue 小 1:装不下,给不定值
    [InlineData(5L, 16777216.0f, 16777221)]         // 单精度的指数为正(2^24):整数部分的加法仍是精确的
    [InlineData(-5L, -16777216.0f, -16777221)]
    [InlineData(1L, 1.0e9f, 1000000001)]            // 1.0e9f 正好是 10 亿(尾数 1953125 × 2^9)
    [InlineData(-1L, -1.0e9f, -1000000001)]
    [InlineData(1147483647L, 1.0e9f, 2147483647)]   // 恰好到 int.MaxValue
    [InlineData(1147483648L, 1.0e9f, int.MinValue)] // 比 int.MaxValue 大 1:装不下
    public void AddingTruncatesTowardZeroExactly(long a, float f, int expected)
    {
        Assert.Equal(expected, ExactFloat.TruncAdd(a, f));
    }

    [Fact]
    public void NotANumberAndInfinityGiveTheIndefiniteInteger()
    {
        Assert.Equal(int.MinValue, ExactFloat.TruncMul(5, float.NaN));
        Assert.Equal(int.MinValue, ExactFloat.TruncMul(5, float.PositiveInfinity));
        Assert.Equal(int.MinValue, ExactFloat.TruncMulDiv(5, 1.0f, float.NaN));
        Assert.Equal(int.MinValue, ExactFloat.TruncMulDiv(5, float.NegativeInfinity, 2.0f));
        Assert.Equal(int.MinValue, ExactFloat.TruncAdd(5, float.NaN));
        Assert.Equal(int.MinValue, ExactFloat.TruncAdd(5, float.NegativeInfinity));
    }

    [Fact]
    public void ADenormalAndAVeryLargeSingleAreHandled()
    {
        Assert.Equal(0, ExactFloat.TruncMul(1_000_000_000, BitConverter.Int32BitsToSingle(1)));     // 最小的非规格数
        Assert.Equal(int.MinValue, ExactFloat.TruncMul(2, 3.0e38f));
        Assert.Equal(0, ExactFloat.TruncMulDiv(5, 1.0f, 3.0e38f));
        Assert.Equal(int.MinValue, ExactFloat.TruncMulDiv(5, 3.0e38f, 1.0f));
        Assert.Equal(-7, ExactFloat.TruncMulDiv(7, 1.0f, -1.0f));
        Assert.Equal(7, ExactFloat.TruncMulDiv(-7, 1.0f, -1.0f));
    }

    [Fact]
    public void ADenormalHasTheSameScaleAsTheSmallestNormalNumberBelowIt()
    {
        // 最大的非规格数是 (2^23 − 1) × 2^−149,最小的规格数是 2^23 × 2^−149(= 2^−126):两者之比 8388607 / 8388608。
        // 指数差一位就会把比值算成约 1/2,所以 1000000 × 比值 = 999999.88… 向零取整是 999999,而不是 499999。
        float largestDenormal = BitConverter.Int32BitsToSingle(0x007FFFFF);
        float smallestNormal = BitConverter.Int32BitsToSingle(0x00800000);
        Assert.Equal(999999, ExactFloat.TruncMulDiv(1_000_000, largestDenormal, smallestNormal));
        Assert.Equal(-999999, ExactFloat.TruncMulDiv(-1_000_000, largestDenormal, smallestNormal));
        Assert.Equal(1_000_000, ExactFloat.TruncMulDiv(1_000_000, smallestNormal, smallestNormal));
    }
}
