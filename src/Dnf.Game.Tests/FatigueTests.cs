using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 疲劳的规则与状态(r15-growth,证据见 <see cref="FatigueRules"/> 文件头:服务端 CUser::CheckFatigue、FatigueUp、RecoverFatigue、CheckDailyScheduleTime 的静态读码)。
/// 时间全是合成的固定时刻(不取系统时钟),上限与整点用小数字,只检验规则的形状。
/// </summary>
public class FatigueTests
{
    private static DateTime T(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new DateTime(y, mo, d, h, mi, s);

    // ------------------------------------------------------------------ 规则参数

    [Fact]
    public void TheDefaultRulesHaveTheServersGlobalCapAndAnInferredSixOClockReset()
    {
        Assert.Equal((FatigueRules.DefaultMax, FatigueRules.DefaultResetHour), (FatigueRules.Default.Max, FatigueRules.Default.ResetHour));
        Assert.Equal(156, FatigueRules.DefaultMax);          // DEFAULT_MAX_FATIGUE(0x09370f5c)的取值,本机读 ELF 数据节
        Assert.Equal(6, FatigueRules.DefaultResetHour);      // 推断,不是读码结论
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(-5, 6)]
    [InlineData(10, -1)]
    [InlineData(10, 24)]
    public void ARuleSetWithoutACapOrWithAnImpossibleHourIsRejected(int max, int hour)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FatigueRules(max, hour));
    }

    [Fact]
    public void TheCapDoesNotDependOnAnythingButTheRules()
    {
        // 服务端 RecoverFatigue 里没有等级参与:状态对象的构造与恢复都只看规则
        var a = new FatigueState(new FatigueRules(10, 6));
        a.Use(3);
        a.Recover();
        Assert.Equal((0, 10, 10), (a.Consumed, a.Max, a.Remaining));
    }

    // ------------------------------------------------------------------ 恢复边界(CheckDailyScheduleTime 0x0846c0a8)

    [Theory]
    [InlineData(2026, 3, 10, 5, 59, 59, 2026, 3, 9, 6, 0, 0)]       // 当前小时还没到 6 点:退到昨天 6 点
    [InlineData(2026, 3, 10, 6, 0, 0, 2026, 3, 10, 6, 0, 0)]        // 刚好 6 点整:今天 6 点
    [InlineData(2026, 3, 10, 23, 59, 59, 2026, 3, 10, 6, 0, 0)]
    [InlineData(2026, 3, 1, 0, 0, 0, 2026, 2, 28, 6, 0, 0)]         // 跨月(平年 2 月)
    [InlineData(2027, 1, 1, 3, 0, 0, 2026, 12, 31, 6, 0, 0)]        // 跨年
    public void TheLatestBoundaryIsTodaysResetHourOrYesterdaysWhenTheHourHasNotComeYet(int y, int mo, int d, int h, int mi, int s, int by, int bmo, int bd, int bh, int bmi, int bs)
    {
        Assert.Equal(T(by, bmo, bd, bh, bmi, bs), new FatigueRules(10, 6).LatestBoundary(T(y, mo, d, h, mi, s)));
    }

    [Fact]
    public void AHourOfZeroMakesTheBoundaryMidnightAndNeverLooksBackAWholeDay()
    {
        var rules = new FatigueRules(10, 0);
        Assert.Equal(T(2026, 3, 10, 0, 0, 0), rules.LatestBoundary(T(2026, 3, 10, 0, 0, 0)));
        Assert.Equal(T(2026, 3, 10, 0, 0, 0), rules.LatestBoundary(T(2026, 3, 10, 23, 59, 59)));
    }

    [Fact]
    public void ThePlayerIsDueWhenTheLastPlayWasNotAfterTheBoundaryEvenWhenItEqualsIt()
    {
        var rules = new FatigueRules(10, 6);
        DateTime now = T(2026, 3, 10, 9, 0, 0);                   // 边界 = 3 月 10 日 6:00:00
        Assert.True(rules.DueForReset(null, now));                // 从没记过
        Assert.True(rules.DueForReset(T(2026, 3, 10, 5, 59, 59), now));
        Assert.True(rules.DueForReset(T(2026, 3, 10, 6, 0, 0), now));     // 相等也算(last <= boundary)
        Assert.False(rules.DueForReset(T(2026, 3, 10, 6, 0, 1), now));
        Assert.False(rules.DueForReset(T(2026, 3, 10, 8, 59, 0), now));
        Assert.True(rules.DueForReset(T(2026, 1, 1, 12, 0, 0), now));     // 隔了很多天
        Assert.False(rules.DueForReset(T(2026, 3, 11, 0, 0, 0), now));    // 时钟被拨回过:上次比现在还晚,不恢复
    }

    // ------------------------------------------------------------------ 扣与门槛

    [Fact]
    public void ANewStateIsFullAndCanEnter()
    {
        var s = new FatigueState(new FatigueRules(3, 6));
        Assert.Equal((0, 3, 3, true, null, 0), (s.Consumed, s.Max, s.Remaining, s.CanEnter, s.LastPlay, s.Version));
    }

    [Fact]
    public void WithOnePointLeftTheDungeonCanStillBeEnteredAndWithNoneItCannot()
    {
        var s = new FatigueState(new FatigueRules(3, 6));
        s.Use(2);
        Assert.Equal((2, 1, true), (s.Consumed, s.Remaining, s.CanEnter));     // 剩 1 点:恰好够,进
        s.Use(1);
        Assert.Equal((3, 0, false), (s.Consumed, s.Remaining, s.CanEnter));    // 剩 0 点:差一点,不进
    }

    [Fact]
    public void UseAddsToTheConsumedAndStopsAtTheCapReportingWhatWasReallyAdded()
    {
        var s = new FatigueState(new FatigueRules(3, 6));
        Assert.Equal(1, s.Use(1));
        Assert.Equal(2, s.Use(5));                      // 只剩 2 点空间:多出来的不算
        Assert.Equal((3, 0), (s.Consumed, s.Remaining));
        Assert.Equal(0, s.Use(1));                      // 已经满了
        Assert.Equal(3, s.Consumed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void UseWithoutAPositiveAmountChangesNothingAndDoesNotCountAsAChange(int amount)
    {
        var s = new FatigueState(new FatigueRules(3, 6));
        s.Use(1);
        int version = s.Version;
        Assert.Equal(0, s.Use(amount));
        Assert.Equal((1, version), (s.Consumed, s.Version));
    }

    [Fact]
    public void AHugeAmountDoesNotOverflowTheCounter()
    {
        var s = new FatigueState(new FatigueRules(156, 6));
        s.Use(100);
        Assert.Equal(56, s.Use(int.MaxValue));
        Assert.Equal((156, 0, false), (s.Consumed, s.Remaining, s.CanEnter));
    }

    [Fact]
    public void TheVersionOnlyMovesWhenThePointsReallyChange()
    {
        var s = new FatigueState(new FatigueRules(2, 6));
        Assert.Equal(0, s.Version);
        s.Use(1);
        Assert.Equal(1, s.Version);
        s.Use(5);
        Assert.Equal(2, s.Version);
        s.Use(1);                                       // 满了,没变
        Assert.Equal(2, s.Version);
        s.Recover();
        Assert.Equal(3, s.Version);
        s.Recover();                                    // 已经是满的,没变
        Assert.Equal(3, s.Version);
    }

    // ------------------------------------------------------------------ 每日恢复(登录与计时器)

    [Fact]
    public void TheFirstRefreshRecoversBecauseNothingWasRecordedAndStampsThePlayTime()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        s.Use(4);
        DateTime now = T(2026, 3, 10, 12, 0, 0);
        Assert.True(s.Refresh(now));
        Assert.Equal((0, now), (s.Consumed, s.LastPlay));
    }

    [Fact]
    public void RefreshingAgainWithinTheSameDayDoesNotRecoverAgain()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        s.Refresh(T(2026, 3, 10, 12, 0, 0));
        s.Use(3);
        Assert.False(s.Refresh(T(2026, 3, 10, 12, 0, 1)));
        Assert.False(s.Refresh(T(2026, 3, 10, 23, 59, 59)));
        Assert.False(s.Refresh(T(2026, 3, 11, 5, 59, 59)));         // 第二天 6 点之前还是同一个恢复周期
        Assert.Equal(3, s.Consumed);
    }

    [Fact]
    public void CrossingTheResetHourWhileOnlineRecoversOnTheNextRefresh()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        s.Refresh(T(2026, 3, 10, 20, 0, 0));
        s.Use(5);
        Assert.False(s.CanEnter);
        Assert.False(s.Refresh(T(2026, 3, 11, 5, 59, 59)));
        Assert.False(s.CanEnter);                                   // 差一秒
        Assert.True(s.Refresh(T(2026, 3, 11, 6, 0, 0)));            // 刚好到点:上次 5:59:59 ≤ 边界 6:00:00
        Assert.Equal((0, 5, true), (s.Consumed, s.Remaining, s.CanEnter));
    }

    [Fact]
    public void ALastPlayExactlyOnTheBoundaryIsStillDueSoTheInclusiveRuleIsKept()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        DateTime boundary = T(2026, 3, 10, 6, 0, 0);
        s.Restore(2, boundary);
        Assert.True(s.Refresh(boundary.AddHours(3)));               // last == boundary → 到点(服务端的 <=)
        Assert.Equal(0, s.Consumed);
        s.Restore(2, boundary.AddSeconds(1));
        Assert.False(s.Refresh(boundary.AddHours(3)));              // 晚一秒就不到点
        Assert.Equal(2, s.Consumed);
    }

    [Fact]
    public void AGapOfSeveralDaysRecoversOnceAndNotMoreThanTheCap()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        s.Restore(4, T(2026, 3, 1, 9, 0, 0));
        Assert.True(s.Refresh(T(2026, 3, 20, 9, 0, 0)));
        Assert.Equal((0, 5), (s.Consumed, s.Max));
    }

    [Fact]
    public void AClockWoundBackDoesNotRecover()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        s.Restore(4, T(2026, 3, 10, 12, 0, 0));
        Assert.False(s.Refresh(T(2026, 3, 10, 11, 0, 0)));
        Assert.Equal(4, s.Consumed);
        Assert.Equal(T(2026, 3, 10, 11, 0, 0), s.LastPlay);         // 记的是现在,不是较晚的那个
    }

    [Fact]
    public void ADifferentResetHourMovesTheBoundary()
    {
        var s = new FatigueState(new FatigueRules(5, 0));           // 零点恢复
        s.Restore(5, T(2026, 3, 10, 23, 0, 0));
        Assert.False(s.Refresh(T(2026, 3, 10, 23, 59, 59)));
        Assert.True(s.Refresh(T(2026, 3, 11, 0, 0, 0)));
    }

    // ------------------------------------------------------------------ 读档

    [Fact]
    public void RestoreClampsTheConsumedPointsToTheCapAndKeepsTheStampAsIs()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        DateTime stamp = T(2026, 3, 10, 7, 0, 0);
        s.Restore(99, stamp);
        Assert.Equal((5, 0, stamp), (s.Consumed, s.Remaining, s.LastPlay));
        s.Restore(-3, null);
        Assert.Equal((0, 5, null), (s.Consumed, s.Remaining, s.LastPlay));
        s.Restore(2, stamp);
        Assert.Equal((2, 3), (s.Consumed, s.Remaining));
    }

    [Fact]
    public void RestoreBringsTheCapBackToTheRulesEvenIfItWasChanged()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        s.Use(2);
        s.Restore(1, null);
        Assert.Equal((1, 5), (s.Consumed, s.Max));
    }

    [Fact]
    public void RestoreAlwaysMovesTheVersionEvenWhenTheValuesComeBackTheSameSoABarRedrawsAfterALoad()
    {
        var s = new FatigueState(new FatigueRules(5, 6));
        int version = s.Version;
        s.Restore(0, null);                                         // 读回来的和现有的一样,也算一次读档
        Assert.Equal(version + 1, s.Version);
        DateTime stamp = T(2026, 3, 10, 7, 0, 0);
        s.Restore(2, stamp);
        s.Restore(2, stamp);
        Assert.Equal(version + 3, s.Version);
    }
}
