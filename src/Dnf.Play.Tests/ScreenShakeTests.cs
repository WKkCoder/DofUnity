namespace Dnf.Play.Tests;

/// <summary>
/// 屏幕震动与瞬移落点(M40,证据 E-133,客户端静态读码):震动每帧在 [-强度, +强度] 里各轴取一个随机偏移、没有衰减、到时间归零,再次开始覆盖;
/// 瞬移的落点能站就用它,不能站就在 200 像素内找最近的能站的点。数全是合成的。
/// </summary>
public class ScreenShakeTests
{
    [Fact]
    public void EachFrameDrawsAnOffsetWithinTheAmplitudeOnBothAxes()
    {
        // 随机数取最小:偏移 = -强度;取最大:偏移 = +强度(范围 = 2 × 强度 + 1 个取值)
        var low = new ScreenShake(n => 0);
        low.Start(4, 100);
        low.Step(10);
        Assert.Equal((-4, -4), (low.OffsetX, low.OffsetY));

        var high = new ScreenShake(n => n - 1);
        high.Start(4, 100);
        high.Step(10);
        Assert.Equal((4, 4), (high.OffsetX, high.OffsetY));

        var seen = new HashSet<int>();
        var rng = new Random(7);
        var mid = new ScreenShake(n => rng.Next(n));
        mid.Start(3, 10000);
        for (int i = 0; i < 400; i++)
        {
            mid.Step(10);
            Assert.InRange(mid.OffsetX, -3, 3);
            Assert.InRange(mid.OffsetY, -3, 3);
            seen.Add(mid.OffsetX);
        }
        Assert.Equal(7, seen.Count);       // 整个范围都会取到
    }

    [Fact]
    public void TheShakeEndsAtItsDurationAndZeroesTheOffsets()
    {
        var shake = new ScreenShake(n => n - 1);
        shake.Start(4, 100);
        Assert.True(shake.Active);
        Assert.Equal(4, shake.Strength);
        for (int i = 0; i < 9; i++)
        {
            shake.Step(10);
            Assert.True(shake.Active);
            Assert.Equal(4, shake.OffsetX);
        }
        shake.Step(10);                       // 累计 100 毫秒:到点,偏移归零
        Assert.False(shake.Active);
        Assert.Equal((0, 0), (shake.OffsetX, shake.OffsetY));
        shake.Step(10);
        Assert.Equal((0, 0), (shake.OffsetX, shake.OffsetY));
    }

    [Fact]
    public void ANonPositiveStrengthNeverShakes()
    {
        var shake = new ScreenShake(n => n - 1);
        shake.Start(0, 100);
        Assert.False(shake.Active);
        shake.Step(10);
        Assert.Equal((0, 0), (shake.OffsetX, shake.OffsetY));
        shake.Start(-3, 100);
        Assert.False(shake.Active);
        Assert.Equal(0, shake.Strength);
    }

    [Fact]
    public void StartingAgainRestartsTheClockAndTheAmplitude()
    {
        var shake = new ScreenShake(n => n - 1);
        shake.Start(2, 1000);
        shake.Step(500);
        Assert.Equal(2, shake.OffsetX);
        shake.Start(6, 100);                  // 覆盖:新的强度、重新计时
        Assert.Equal(6, shake.Strength);
        shake.Step(50);
        Assert.True(shake.Active);
        Assert.Equal(6, shake.OffsetX);
        shake.Step(50);
        Assert.False(shake.Active);
    }

    [Fact]
    public void StopClearsTheOffsetsAndStepDoesNothingWhileIdle()
    {
        var shake = new ScreenShake(n => n - 1);
        shake.Step(10);
        Assert.Equal((0, 0), (shake.OffsetX, shake.OffsetY));
        shake.Start(5, 500);
        shake.Step(10);
        Assert.Equal(5, shake.OffsetX);
        shake.Stop();
        Assert.False(shake.Active);
        Assert.Equal((0, 0), (shake.OffsetX, shake.OffsetY));
    }

    [Fact]
    public void TheDefaultRandomSourceIsDeterministic()
    {
        var a = new ScreenShake();
        var b = new ScreenShake();
        a.Start(5, 1000);
        b.Start(5, 1000);
        for (int i = 0; i < 30; i++)
        {
            a.Step(10);
            b.Step(10);
            Assert.Equal((a.OffsetX, a.OffsetY), (b.OffsetX, b.OffsetY));
        }
    }

    // ------------------------------------------------------------------ 落点

    private static Func<double, double, bool> Half(double maxX) => (x, y) => x <= maxX;

    [Fact]
    public void AStandablePointIsUsedAsIs()
    {
        Assert.True(Placement.TryResolve(Half(100), 40, 17, out double x, out double y));
        Assert.Equal((40.0, 17.0), (x, y));
    }

    [Fact]
    public void ABlockedPointMovesToTheNearestStandableOneOnTheGrid()
    {
        Assert.True(Placement.TryResolve(Half(100), 130, 50, out double x, out double y));
        Assert.Equal((100.0, 50.0), (x, y));       // 左边 30 像素是格点上最近的合法点
        Assert.True(Placement.TryResolve(Half(100), 133, 50, out x, out y));
        Assert.Equal((93.0, 50.0), (x, y));        // 格点是目标加上 10 的倍数的偏移:往左 30 → 103 不合法,往左 40 → 93
    }

    [Fact]
    public void NothingStandableWithinTheRadiusFails()
    {
        Assert.False(Placement.TryResolve(Half(0), 250, 0, out double x, out double y));
        Assert.Equal((250.0, 0.0), (x, y));
        Assert.True(Placement.TryResolve(Half(0), 200, 0, out x, out y));       // 恰好 200 像素:在半径里
        Assert.Equal((0.0, 0.0), (x, y));
        Assert.False(Placement.TryResolve(Half(0), 205, 0, out _, out _));      // 格点上最近的是 5(不合法):够不着
    }

    [Fact]
    public void TheSearchIsDeterministicAndPrefersTheNearestPoint()
    {
        // 只有两个格点合法:(−10, 0) 离 10、(0, 20) 离 20 → 取近的
        Func<double, double, bool> only = (x, y) => (x == -10 && y == 0) || (x == 0 && y == 20);
        Assert.True(Placement.TryResolve(only, 0, 0, out double rx, out double ry));
        Assert.Equal((-10.0, 0.0), (rx, ry));
        // 等距的两个点:先找到的赢(自上而下、自左而右),重复调用结果一样
        Func<double, double, bool> tie = (x, y) => (x == 30 && y == 10) || (x == 10 && y == 30);
        Assert.True(Placement.TryResolve(tie, 10, 10, out rx, out ry));
        Assert.Equal((30.0, 10.0), (rx, ry));
        Assert.True(Placement.TryResolve(tie, 10, 10, out double sx, out double sy));
        Assert.Equal((rx, ry), (sx, sy));
    }

    [Fact]
    public void ANullPredicateIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Placement.TryResolve(null, 0, 0, out _, out _));
    }
}
