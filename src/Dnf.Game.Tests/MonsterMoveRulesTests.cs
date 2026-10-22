using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物默认 AI 走位的规则(证据 E-114):距离与视野、防撞边、格子比较、远程取距、倒着走、模式的节拍与停下概率。
/// 数全是合成的,检验的是我们照客户端函数写下的比较方向、取整方式与边界。
/// </summary>
public class MonsterMoveRulesTests
{
    // ------------------------------------------------------------------ 距离与视野

    [Fact]
    public void TheVerticalDifferenceIsScaledByTheDepthRatioBeforeTheSquareRoot()
    {
        Assert.Equal(30, MonsterMoveRules.Distance(30, 0, 0, 0));
        Assert.Equal(100, MonsterMoveRules.Distance(0, 29, 0, 0));          // 29 像素的纵差折成 100 像素的横差
        Assert.Equal(100, MonsterMoveRules.Distance(0, 0, 0, 29));          // 方向无关
        Assert.Equal(104, MonsterMoveRules.Distance(30, 29, 0, 0));         // sqrt(100² + 30²) = 104.4 取整
        Assert.Equal(0, MonsterMoveRules.Distance(5, 5, 5, 5));
    }

    [Fact]
    public void ATargetIsValidWithinTheSightTimesOnePointFourAndSightZeroMeansUnlimited()
    {
        Assert.True(MonsterMoveRules.IsValidTarget(100, 0, 0, 140, 0));     // 视野 100 → 上限 140,等于上限还算有效
        Assert.False(MonsterMoveRules.IsValidTarget(100, 0, 0, 141, 0));
        Assert.True(MonsterMoveRules.IsValidTarget(100, 0, 0, -140, 0));
        Assert.False(MonsterMoveRules.IsValidTarget(100, 0, 0, 0, 41));     // 41 像素纵差折成 141
        Assert.True(MonsterMoveRules.IsValidTarget(0, 0, 0, 100000, 100000));
    }

    // ------------------------------------------------------------------ 游荡:防撞边

    [Fact]
    public void WanderingTurnsInwardWithinTheEdgeMarginOnBothSides()
    {
        Assert.True(MonsterMoveRules.NearEdge(79, 1000, out int dir));
        Assert.Equal(MonsterMoveRules.Right, dir);
        Assert.False(MonsterMoveRules.NearEdge(80, 1000, out _));           // 正好等于边距不算
        Assert.False(MonsterMoveRules.NearEdge(920, 1000, out _));
        Assert.True(MonsterMoveRules.NearEdge(921, 1000, out dir));
        Assert.Equal(MonsterMoveRules.Left, dir);
    }

    // ------------------------------------------------------------------ 近战敌对:比较所在的格子

    [Fact]
    public void MeleeApproachComparesTheCellsAndStandsStillInTheSameCell()
    {
        MonsterMoveRules.ApproachDirections(100, 100, 100 + 16, 100 + 12, out int dx, out int dy);
        Assert.Equal((MonsterMoveRules.Right, MonsterMoveRules.Down), (dx, dy));

        MonsterMoveRules.ApproachDirections(100, 100, 100 - 16, 100 - 12, out dx, out dy);
        Assert.Equal((MonsterMoveRules.Left, MonsterMoveRules.Up), (dx, dy));

        // 同一格(横向每 16 像素一格、纵向每 12 像素一格):不动
        MonsterMoveRules.ApproachDirections(96, 96, 96 + 15, 96 + 11, out dx, out dy);
        Assert.Equal((MonsterMoveRules.None, MonsterMoveRules.None), (dx, dy));

        // 只差一个方向
        MonsterMoveRules.ApproachDirections(96, 96, 96 + 40, 96 + 5, out dx, out dy);
        Assert.Equal((MonsterMoveRules.Right, MonsterMoveRules.None), (dx, dy));
    }

    // ------------------------------------------------------------------ 远程敌对:取距

    [Fact]
    public void ARangedMonsterApproachesBeyondNinetyPercentOfTheSightAndBacksOffWithinHalf()
    {
        // 视野 200:远界 180、近界 100;怪物在 x = 500
        Assert.Equal(MonsterMoveRules.Left, MonsterMoveRules.KeepDistanceX(200, 500, 320));   // 距离 180 ≥ 远界,目标在左 → 朝左靠近
        Assert.Equal(MonsterMoveRules.None, MonsterMoveRules.KeepDistanceX(200, 500, 350));                              // 距离 150 在两界之间 → 不动
        Assert.Equal(MonsterMoveRules.Right, MonsterMoveRules.KeepDistanceX(200, 500, 410));                             // 距离 90 ≤ 近界,目标在左 → 朝右后退
        Assert.Equal(MonsterMoveRules.Right, MonsterMoveRules.KeepDistanceX(200, 500, 700));                             // 目标在右,距离 200 → 朝右靠近
        Assert.Equal(MonsterMoveRules.None, MonsterMoveRules.KeepDistanceX(200, 500, 650));
        Assert.Equal(MonsterMoveRules.Left, MonsterMoveRules.KeepDistanceX(200, 500, 590));                              // 目标在右,距离 90 → 朝左后退
    }

    [Fact]
    public void ARangedMonsterAlwaysMovesVerticallyTowardTheTargetRow()
    {
        Assert.Equal(MonsterMoveRules.Up, MonsterMoveRules.KeepDistanceY(100, 90));
        Assert.Equal(MonsterMoveRules.Down, MonsterMoveRules.KeepDistanceY(100, 110));
        Assert.Equal(MonsterMoveRules.Down, MonsterMoveRules.KeepDistanceY(100, 100));    // 同一行也选"下"
    }

    // ------------------------------------------------------------------ 倒着走

    [Fact]
    public void ReversingSwapsLeftRightAndUpDownAndTurnsStillIntoStill()
    {
        Assert.Equal(MonsterMoveRules.Right, MonsterMoveRules.Reverse(MonsterMoveRules.Left));
        Assert.Equal(MonsterMoveRules.Left, MonsterMoveRules.Reverse(MonsterMoveRules.Right));
        Assert.Equal(MonsterMoveRules.Down, MonsterMoveRules.Reverse(MonsterMoveRules.Up));
        Assert.Equal(MonsterMoveRules.Up, MonsterMoveRules.Reverse(MonsterMoveRules.Down));
        Assert.Equal(MonsterMoveRules.None, MonsterMoveRules.Reverse(MonsterMoveRules.None));
    }

    [Fact]
    public void WarlikeAtLeastFiftyNeverReversesAndWarlikeZeroReversesHalfOfTheRolls()
    {
        for (int roll = 0; roll < 100; roll++)
        {
            Assert.False(MonsterMoveRules.ShouldReverse(50, roll));
            Assert.False(MonsterMoveRules.ShouldReverse(80, roll));
        }
        Assert.False(MonsterMoveRules.ShouldReverse(0, 49));
        Assert.True(MonsterMoveRules.ShouldReverse(0, 50));                  // 敌对加成 50 + [warlike] 0 ≤ 掷骰点数
        Assert.True(MonsterMoveRules.ShouldReverse(0, 99));
        Assert.False(MonsterMoveRules.ShouldReverse(30, 79));
        Assert.True(MonsterMoveRules.ShouldReverse(30, 80));
        Assert.False(MonsterMoveRules.ShouldReverse(0, 149));                // 点数先对 100 取余
        Assert.True(MonsterMoveRules.ShouldReverse(0, 150));
    }

    // ------------------------------------------------------------------ 模式的节拍与停下概率

    [Fact]
    public void WanderAndHostileModesHaveTheirOwnTempoForMovingAndStanding()
    {
        MonsterTempo wander = MonsterMoveRules.TempoOf(MonsterMoveMode.Wander);
        Assert.Equal(500, wander.Interval(false, 0));
        Assert.Equal(500 + 999, wander.Interval(false, 999));
        Assert.Equal(500, wander.Interval(false, 1000));                     // 随机数先对范围取余
        Assert.Equal(300, wander.Interval(true, 0));
        Assert.Equal(300 + 2999, wander.Interval(true, 2999));

        foreach (MonsterMoveMode hostile in new[] { MonsterMoveMode.HostileMelee, MonsterMoveMode.HostileRanged })
        {
            MonsterTempo t = MonsterMoveRules.TempoOf(hostile);
            Assert.Equal(1000, t.Interval(false, 0));
            Assert.Equal(1000 + 299, t.Interval(false, 299));
            Assert.Equal(800, t.Interval(true, 0));
            Assert.Equal(800 + 199, t.Interval(true, 199));
        }
    }

    [Fact]
    public void OnlyWanderingCancelsHalfOfTheVerticalMoves()
    {
        Assert.Equal(50, MonsterMoveRules.StopPercentY(MonsterMoveMode.Wander));
        Assert.Equal(0, MonsterMoveRules.StopPercentY(MonsterMoveMode.HostileMelee));
        Assert.Equal(0, MonsterMoveRules.StopPercentY(MonsterMoveMode.HostileRanged));
        foreach (MonsterMoveMode m in new[] { MonsterMoveMode.Wander, MonsterMoveMode.HostileMelee, MonsterMoveMode.HostileRanged })
        {
            Assert.Equal(0, MonsterMoveRules.StopPercentX(m));
        }
    }

    [Fact]
    public void TheModeValuesAreTheClientModeNumbers()
    {
        Assert.Equal(13, (int)MonsterMoveMode.Wander);
        Assert.Equal(9, (int)MonsterMoveMode.HostileMelee);
        Assert.Equal(10, (int)MonsterMoveMode.HostileRanged);
    }
}
