namespace Dnf.Play.Tests;

/// <summary>宠物用的时间源:手动钟(测试推进)与毫秒读数折成整数秒的钟(宿主接墙钟或单调计时器)。</summary>
public class PetClockTests
{
    [Fact]
    public void TheManualClockStartsWhereToldAndMovesOnlyWhenAsked()
    {
        var clock = new ManualPetClock();
        Assert.Equal(0, clock.NowSeconds);
        Assert.Equal(0, clock.NowSeconds);          // 读不会让它走

        clock.Advance(90);
        Assert.Equal(90, clock.NowSeconds);
        clock.Advance(0);
        Assert.Equal(90, clock.NowSeconds);

        clock.Set(5);                               // 可以拨回去(试时钟倒退)
        Assert.Equal(5, clock.NowSeconds);
        clock.Advance(-2);
        Assert.Equal(3, clock.NowSeconds);

        Assert.Equal(1234, new ManualPetClock(1234).NowSeconds);
    }

    [Fact]
    public void TheMillisecondsClockFloorsToSecondsAndClampsBothEnds()
    {
        long ms = 0;
        var clock = new MillisecondsPetClock(() => ms);
        Assert.Equal(0, clock.NowSeconds);

        ms = 999;
        Assert.Equal(0, clock.NowSeconds);
        ms = 1000;
        Assert.Equal(1, clock.NowSeconds);
        ms = 1999;
        Assert.Equal(1, clock.NowSeconds);
        ms = 61_500;
        Assert.Equal(61, clock.NowSeconds);

        ms = -1;                                    // 负数按 0
        Assert.Equal(0, clock.NowSeconds);
        ms = long.MinValue;
        Assert.Equal(0, clock.NowSeconds);

        ms = (long)int.MaxValue * 1000 + 999;       // 刚好装得下
        Assert.Equal(int.MaxValue, clock.NowSeconds);
        ms = ((long)int.MaxValue + 1) * 1000;       // 多一秒也按上限
        Assert.Equal(int.MaxValue, clock.NowSeconds);
        ms = long.MaxValue;
        Assert.Equal(int.MaxValue, clock.NowSeconds);
    }

    [Fact]
    public void TheMillisecondsClockReadsTheSourceEveryTimeAndNeedsOne()
    {
        int reads = 0;
        var clock = new MillisecondsPetClock(() => ++reads * 1000L);
        Assert.Equal(1, clock.NowSeconds);
        Assert.Equal(2, clock.NowSeconds);
        Assert.Equal(2, reads);

        Assert.Throws<ArgumentNullException>(() => new MillisecondsPetClock(null));
    }
}
