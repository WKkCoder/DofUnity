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
        Assert.Equal(1, (int)MonsterMoveMode.Hesitant);
        Assert.Equal(4, (int)MonsterMoveMode.Chase);
        Assert.Equal(11, (int)MonsterMoveMode.Flank);
        Assert.Equal(12, (int)MonsterMoveMode.Approach);
        Assert.Equal(15, (int)MonsterMoveMode.Idle);
        Assert.Equal(21, (int)MonsterMoveMode.Reverse);
    }

    // ------------------------------------------------------------------ 其余模式的节拍与停下概率(E-148)

    [Fact]
    public void ChaseAndHesitantHaveTheirOwnTempoAndStopRolls()
    {
        MonsterTempo chase = MonsterMoveRules.TempoOf(MonsterMoveMode.Chase);
        Assert.Equal(1000, chase.Interval(false, 0));
        Assert.Equal(1000 + 999, chase.Interval(false, 999));
        Assert.Equal(300, chase.Interval(true, 0));
        Assert.Equal(300 + 2999, chase.Interval(true, 2999));

        MonsterTempo hesitant = MonsterMoveRules.TempoOf(MonsterMoveMode.Hesitant);
        Assert.Equal(300, hesitant.Interval(false, 0));
        Assert.Equal(300 + 2999, hesitant.Interval(false, 2999));
        Assert.Equal(300, hesitant.Interval(true, 0));

        // 绕背、靠近用敌对的节拍;站桩、反向用游荡的
        foreach (MonsterMoveMode m in new[] { MonsterMoveMode.Flank, MonsterMoveMode.Approach })
        {
            Assert.Equal(1000 + 299, MonsterMoveRules.TempoOf(m).Interval(false, 299));
            Assert.Equal(800 + 199, MonsterMoveRules.TempoOf(m).Interval(true, 199));
        }
        foreach (MonsterMoveMode m in new[] { MonsterMoveMode.Idle, MonsterMoveMode.Reverse })
        {
            Assert.Equal(500 + 999, MonsterMoveRules.TempoOf(m).Interval(false, 999));
            Assert.Equal(300 + 2999, MonsterMoveRules.TempoOf(m).Interval(true, 2999));
        }

        Assert.Equal(30, MonsterMoveRules.StopPercentY(MonsterMoveMode.Chase));
        Assert.Equal(30, MonsterMoveRules.StopPercentY(MonsterMoveMode.Hesitant));
        foreach (MonsterMoveMode m in new[] { MonsterMoveMode.Flank, MonsterMoveMode.Approach, MonsterMoveMode.Idle, MonsterMoveMode.Reverse })
        {
            Assert.Equal(0, MonsterMoveRules.StopPercentY(m));
            Assert.Equal(0, MonsterMoveRules.StopPercentX(m));
        }
    }

    [Fact]
    public void TheWarlikeBonusIsOnlyGivenInTheHostileModes()
    {
        Assert.Equal(50, MonsterMoveRules.WarlikeBonusOf(MonsterMoveMode.HostileMelee));
        Assert.Equal(50, MonsterMoveRules.WarlikeBonusOf(MonsterMoveMode.HostileRanged));
        foreach (MonsterMoveMode m in new[] { MonsterMoveMode.Chase, MonsterMoveMode.Hesitant, MonsterMoveMode.Flank, MonsterMoveMode.Approach, MonsterMoveMode.Wander })
        {
            Assert.Equal(0, MonsterMoveRules.WarlikeBonusOf(m));
        }
        // 加成 0、好战度 50:点数 50 起取反
        Assert.False(MonsterMoveRules.ShouldReverse(50, 0, 49));
        Assert.True(MonsterMoveRules.ShouldReverse(50, 0, 50));
        Assert.False(MonsterMoveRules.ShouldReverse(50, 50, 99));
    }

    [Fact]
    public void TheMoveRollIsTakenModuloOneHundredAndBelowThePercent()
    {
        Assert.True(MonsterMoveRules.RollMove(80, 79));
        Assert.False(MonsterMoveRules.RollMove(80, 80));
        Assert.True(MonsterMoveRules.RollMove(80, 100));                      // 先对 100 取余
        Assert.True(MonsterMoveRules.RollMove(30, 29));
        Assert.False(MonsterMoveRules.RollMove(30, 30));
    }

    // ------------------------------------------------------------------ 绕背冲刺

    [Fact]
    public void TheFlankDestinationOvershootsTheTargetByHalfTheSightAndTruncates()
    {
        Assert.Equal(300 + 100, MonsterMoveRules.FlankDestinationX(100, 300, 200));     // 自己在左:终点在目标右边
        Assert.Equal(300 - 100, MonsterMoveRules.FlankDestinationX(500, 300, 200));     // 自己在右:终点在目标左边
        Assert.Equal(300 + 12, MonsterMoveRules.FlankDestinationX(100, 300, 25));       // 12.5 截断
        Assert.Equal(287, MonsterMoveRules.FlankDestinationX(500, 300, 25));            // 287.5 截断成 287
        Assert.Equal(300 - 100, MonsterMoveRules.FlankDestinationX(300, 300, 200));     // 同一列:算"自己不在左边"
    }

    [Fact]
    public void TheFlankRunHeadsForTheTargetAndEndsWhenItPassesTheDestination()
    {
        Assert.Equal(MonsterMoveRules.Right, MonsterMoveRules.FlankDirectionX(100, 300));
        Assert.Equal(MonsterMoveRules.Left, MonsterMoveRules.FlankDirectionX(500, 300));
        Assert.Equal(MonsterMoveRules.Left, MonsterMoveRules.FlankDirectionX(300, 300));

        Assert.False(MonsterMoveRules.FlankArrived(MonsterMoveRules.Right, 399, 400));
        Assert.True(MonsterMoveRules.FlankArrived(MonsterMoveRules.Right, 400, 400));
        Assert.True(MonsterMoveRules.FlankArrived(MonsterMoveRules.Right, 450, 400));
        Assert.False(MonsterMoveRules.FlankArrived(MonsterMoveRules.Left, 201, 200));
        Assert.True(MonsterMoveRules.FlankArrived(MonsterMoveRules.Left, 200, 200));
        Assert.False(MonsterMoveRules.FlankArrived(MonsterMoveRules.None, 200, 200));    // 没在走:不算到
    }

    // ------------------------------------------------------------------ 被远处打中之后靠近

    [Fact]
    public void ToughAndPlainApproachGoStraightForTheTarget()
    {
        foreach (MonsterMoveRules.ApproachStyle style in new[] { MonsterMoveRules.ApproachStyle.Tough, MonsterMoveRules.ApproachStyle.Plain })
        {
            MonsterMoveRules.ApproachDestination(style, 100, 300, 180, 1, 1, 7, out int x, out int y);
            Assert.Equal((300, 180), (x, y));
        }
    }

    [Fact]
    public void ManaceApproachStopsAFifthOfTheGapShortOnTheNearSideAndOffsetsVerticallyAwayFromTheFarEdge()
    {
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Manace, 100, 300, 180, 0, 0, 0, out int x, out int y);
        Assert.Equal((300 - 40, 180 + 50), (x, y));                           // 自己在左:停在目标左边 40;纵向目标在 230 以内 → 往下偏 50
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Manace, 500, 300, 231, 0, 0, 0, out x, out y);
        Assert.Equal((300 + 40, 231 - 50), (x, y));                           // 自己在右:停在目标右边;目标在 230 以外 → 往上偏
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Manace, 100, 300, 230, 0, 0, 0, out _, out y);
        Assert.Equal(230 + 50, y);                                            // 正好 230 不算"大于"
    }

    [Fact]
    public void CarefulApproachLandsHalfTheGapBesideTheTargetOnARandomSideWithAVerticalJitter()
    {
        // 横向差 (300 − 100 + 10) / 2 = 105;纵向幅度上限 105 / 2 = 52
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Careful, 100, 300, 180, 0, 0, 13, out int x, out int y);
        Assert.Equal((300 - 105, 180 - 13), (x, y));
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Careful, 100, 300, 180, 1, 1, 13, out x, out y);
        Assert.Equal((300 + 105, 180 + 13), (x, y));
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Careful, 100, 300, 180, 3, 2, 52 + 5, out x, out y);
        Assert.Equal((300 + 105, 180 - 5), (x, y));                           // 随机数先对幅度取余
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Careful, 300, 300, 180, 1, 1, 9, out x, out y);
        Assert.Equal((300 + 5, 180 + 1), (x, y));                             // 同一列:|10| / 2 = 5,幅度 5 / 2 = 2,9 % 2 = 1
        MonsterMoveRules.ApproachDestination(MonsterMoveRules.ApproachStyle.Careful, 308, 300, 180, 1, 1, 9, out x, out y);
        Assert.Equal((300 + 1, 180), (x, y));                                 // 横向差 1:幅度下限 1,随机数 % 1 = 0
    }

    [Fact]
    public void TheApproachStepWalksEachAxisTowardTheDestinationAndStopsInsideTheMargin()
    {
        bool arrived = MonsterMoveRules.ApproachStep(100, 100, 300, 50, 20, out int dx, out int dy);
        Assert.False(arrived);
        Assert.Equal((MonsterMoveRules.Right, MonsterMoveRules.Up), (dx, dy));

        arrived = MonsterMoveRules.ApproachStep(100, 100, 119, 105, 20, out dx, out dy);
        Assert.True(arrived);                                                 // 两轴都在边距内
        Assert.Equal((MonsterMoveRules.None, MonsterMoveRules.None), (dx, dy));

        arrived = MonsterMoveRules.ApproachStep(100, 100, 120, 100, 20, out dx, out dy);
        Assert.False(arrived);                                                // 正好等于边距还要走
        Assert.Equal((MonsterMoveRules.Right, MonsterMoveRules.None), (dx, dy));

        MonsterMoveRules.ApproachStep(300, 100, 100, 300, 20, out dx, out dy);
        Assert.Equal((MonsterMoveRules.Left, MonsterMoveRules.Down), (dx, dy));
    }
}
