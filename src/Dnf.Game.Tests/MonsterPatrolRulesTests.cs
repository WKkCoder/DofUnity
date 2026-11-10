using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物巡逻(模式 27 点巡逻、28 路线巡逻)的纯规则。数全是合成的,检验的是照客户端函数写下的比较方向、边界与模式切换:
/// 进入(0x0088d645)、追击(0x0088d65d)、到达(0x008813b0)、选方向(0x0089220f)、逐步判停(0x00897c66)。
/// </summary>
public class MonsterPatrolRulesTests
{
    private const int Wander = (int)MonsterMoveMode.Wander;
    private const int Idle = (int)MonsterMoveMode.Idle;
    private const int Point = (int)MonsterMoveMode.PatrolPoint;
    private const int Route = (int)MonsterMoveMode.PatrolRoute;

    // ------------------------------------------------------------------ 模式号

    [Fact]
    public void PatrolModesCarryTheClientsModeNumbers()
    {
        Assert.Equal(27, Point);
        Assert.Equal(28, Route);
        Assert.Equal(1, MonsterPatrolRules.TypePoint);
        Assert.Equal(2, MonsterPatrolRules.TypeRoute);
    }

    // ------------------------------------------------------------------ 进入:按类型字段转模式

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(17)]
    public void TypeOneSwitchesWanderIdleAndModeSeventeenToThePointPatrol(int current)
    {
        Assert.Equal(Point, MonsterPatrolRules.ModeFor(current, 1));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(17)]
    public void TypeTwoSwitchesTheSameThreeModesToTheRoutePatrol(int current)
    {
        Assert.Equal(Route, MonsterPatrolRules.ModeFor(current, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void AnyOtherTypeValueNeverStartsAPatrol(int type)
    {
        Assert.Equal(0, MonsterPatrolRules.ModeFor(Wander, type));
        Assert.Equal(0, MonsterPatrolRules.ModeFor(Idle, type));
        Assert.Equal(0, MonsterPatrolRules.ModeFor(17, type));
    }

    [Theory]
    [InlineData(1)]     // 迟疑
    [InlineData(4)]     // 追击
    [InlineData(9)]     // 敌对·近战
    [InlineData(10)]    // 敌对·远程
    [InlineData(11)]    // 绕背
    [InlineData(12)]    // 靠近
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(21)]    // 反向
    [InlineData(27)]    // 已经在巡逻
    [InlineData(28)]
    public void OnlyTheThreeIdleLikeModesAreEligibleToStartAPatrol(int current)
    {
        Assert.Equal(0, MonsterPatrolRules.ModeFor(current, 1));
        Assert.Equal(0, MonsterPatrolRules.ModeFor(current, 2));
    }

    // ------------------------------------------------------------------ 追击:巡逻中有目标

    [Fact]
    public void BothPatrolModesTurnToChaseAsSoonAsThereIsATarget()
    {
        Assert.True(MonsterPatrolRules.TurnsToChase(Point, true));
        Assert.True(MonsterPatrolRules.TurnsToChase(Route, true));
    }

    [Fact]
    public void WithoutATargetThePatrolKeepsGoing()
    {
        Assert.False(MonsterPatrolRules.TurnsToChase(Point, false));
        Assert.False(MonsterPatrolRules.TurnsToChase(Route, false));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(26)]
    [InlineData(29)]
    public void TheChaseTurnBelongsToThePatrolModesOnly(int mode)
    {
        Assert.False(MonsterPatrolRules.TurnsToChase(mode, true));
    }

    // ------------------------------------------------------------------ 到达:整数完全相等

    [Fact]
    public void ArrivalNeedsBothCoordinatesExactlyEqualWithNoTolerance()
    {
        Assert.True(MonsterPatrolRules.Arrived(1, 300, 200, 300, 200));
        Assert.False(MonsterPatrolRules.Arrived(1, 301, 200, 300, 200));
        Assert.False(MonsterPatrolRules.Arrived(1, 299, 200, 300, 200));
        Assert.False(MonsterPatrolRules.Arrived(1, 300, 201, 300, 200));
        Assert.False(MonsterPatrolRules.Arrived(1, 300, 199, 300, 200));
    }

    [Fact]
    public void OnlyTypeOneCanArriveSoTheRoutePatrolNeverStopsHere()
    {
        Assert.False(MonsterPatrolRules.Arrived(2, 300, 200, 300, 200));
        Assert.False(MonsterPatrolRules.Arrived(0, 300, 200, 300, 200));
        Assert.False(MonsterPatrolRules.Arrived(3, 300, 200, 300, 200));
    }

    // ------------------------------------------------------------------ 选方向

    [Fact]
    public void PointPatrolHeadsRightAndDownWhenThePointIsFartherOnBothAxes()
    {
        MonsterPatrolRules.PointDirections(100, 100, 300, 200, out int dirX, out int dirY);
        Assert.Equal(MonsterMoveRules.Right, dirX);
        Assert.Equal(MonsterMoveRules.Down, dirY);
    }

    [Fact]
    public void PointPatrolHeadsLeftAndUpWhenThePointIsNearerOnBothAxes()
    {
        MonsterPatrolRules.PointDirections(500, 300, 300, 200, out int dirX, out int dirY);
        Assert.Equal(MonsterMoveRules.Left, dirX);
        Assert.Equal(MonsterMoveRules.Up, dirY);
    }

    [Fact]
    public void AnAxisThatAlreadyMatchesStillGetsTheRightAndDownDirectionBecauseItIsLessOrEqual()
    {
        // 差为 0 时 "≤ 0" 成立:横向给右(1)、纵向给下(3);由逐步判停把这条轴停住
        MonsterPatrolRules.PointDirections(300, 100, 300, 200, out int dirX, out int dirY);
        Assert.Equal(MonsterMoveRules.Right, dirX);
        Assert.Equal(MonsterMoveRules.Down, dirY);

        MonsterPatrolRules.PointDirections(100, 200, 300, 200, out dirX, out dirY);
        Assert.Equal(MonsterMoveRules.Right, dirX);
        Assert.Equal(MonsterMoveRules.Down, dirY);
    }

    [Fact]
    public void OneOffAboveTheBoundaryFlipsEachAxisIndependently()
    {
        MonsterPatrolRules.PointDirections(301, 201, 300, 200, out int dirX, out int dirY);
        Assert.Equal(MonsterMoveRules.Left, dirX);
        Assert.Equal(MonsterMoveRules.Up, dirY);

        MonsterPatrolRules.PointDirections(301, 199, 300, 200, out dirX, out dirY);
        Assert.Equal(MonsterMoveRules.Left, dirX);
        Assert.Equal(MonsterMoveRules.Down, dirY);

        MonsterPatrolRules.PointDirections(299, 201, 300, 200, out dirX, out dirY);
        Assert.Equal(MonsterMoveRules.Right, dirX);
        Assert.Equal(MonsterMoveRules.Up, dirY);
    }

    [Fact]
    public void PointPatrolNeverChoosesTheNoMovementDirection()
    {
        for (int x = 295; x <= 305; x++)
        {
            for (int y = 195; y <= 205; y++)
            {
                MonsterPatrolRules.PointDirections(x, y, 300, 200, out int dirX, out int dirY);
                Assert.True(dirX == MonsterMoveRules.Left || dirX == MonsterMoveRules.Right);
                Assert.True(dirY == MonsterMoveRules.Up || dirY == MonsterMoveRules.Down);
            }
        }
    }

    // ------------------------------------------------------------------ 逐步判停

    [Fact]
    public void AnAxisStopsOnlyOnExactEquality()
    {
        Assert.True(MonsterPatrolRules.StopsOnAxis(300, 300));
        Assert.False(MonsterPatrolRules.StopsOnAxis(299, 300));
        Assert.False(MonsterPatrolRules.StopsOnAxis(301, 300));
    }

    // ------------------------------------------------------------------ 节拍与停下概率:与游荡一行相同

    [Fact]
    public void BothPatrolModesUseTheWanderTempoRowAndNeverCancelAnAxis()
    {
        foreach (MonsterMoveMode mode in new[] { MonsterMoveMode.PatrolPoint, MonsterMoveMode.PatrolRoute })
        {
            MonsterTempo t = MonsterMoveRules.TempoOf(mode);
            Assert.Equal(500, t.MoveBaseMs);
            Assert.Equal(1000, t.MoveRangeMs);
            Assert.Equal(300, t.IdleBaseMs);
            Assert.Equal(3000, t.IdleRangeMs);
            Assert.Equal(0, MonsterMoveRules.StopPercentX(mode));
            Assert.Equal(0, MonsterMoveRules.StopPercentY(mode));
        }
    }
}
