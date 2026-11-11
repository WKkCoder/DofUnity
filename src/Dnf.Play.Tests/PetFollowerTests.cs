using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 宠物位置跟随的纯逻辑(<see cref="PetFollower"/>):直接读状态机每一步的输出(阶段、位置、目标点、朝向、动作),不经过画面。
/// 规则是本项目自己的占位设计;期望值是手算(整数化的步长)或用另一份独立的 Python 实现对拍出来的,不是照着被测代码抄的。
/// 全部用合成数:停靠距离 −60、横向最大速度 400、纵向 100,追赶率 5、每步 100 毫秒(追赶率乘时间 = 0.5)。
/// </summary>
public class PetFollowerTests
{
    private static readonly PetMaster Origin = new PetMaster(500, 300, false);

    private static PetFollower Follower(PetMaster master, double span = -60, double speedX = 400, double speedY = 100, PetFollowRules rules = null)
    {
        return new PetFollower(rules ?? PetTownKit.SimpleRules, span, speedX, speedY, master);
    }

    private static PetMaster At(double x, double y = 300, bool left = false) => new PetMaster(x, y, left);

    // ------------------------------------------------------------ 出现与停靠点

    [Fact]
    public void AFreshFollowerStandsAtTheHomePointBehindTheMasterAndFacesTheSameWay()
    {
        PetFollower right = Follower(new PetMaster(500, 300, false));
        Assert.Equal((440.0, 300.0, false, PetFollowPhase.Idle, 0), (right.X, right.Y, right.FacingLeft, right.Phase, right.Teleports));

        // 主角朝左时"背后"是右边:停靠点跟着朝向镜像
        PetFollower left = Follower(new PetMaster(500, 300, true));
        Assert.Equal((560.0, 300.0, true, PetFollowPhase.Idle, 0), (left.X, left.Y, left.FacingLeft, left.Phase, left.Teleports));
    }

    [Fact]
    public void APositiveSpanPutsThePetInFrontOfTheMasterAndTheHomeDepthShiftsItInDepth()
    {
        PetFollower front = Follower(new PetMaster(500, 300, false), span: 30);
        Assert.Equal((530.0, 300.0), (front.X, front.Y));
        PetFollower frontLeft = Follower(new PetMaster(500, 300, true), span: 30);
        Assert.Equal((470.0, 300.0), (frontLeft.X, frontLeft.Y));

        var deep = new PetFollowRules(homeDepth: 12);
        PetFollower shifted = Follower(new PetMaster(500, 300, false), rules: deep);
        Assert.Equal((440.0, 312.0), (shifted.X, shifted.Y));
        shifted.HomeOf(new PetMaster(100, 50, true), out double hx, out double hy);
        Assert.Equal((160.0, 62.0), (hx, hy));
    }

    [Fact]
    public void StandingStillAtTheHomePointStaysIdleWithoutMovingOrTurning()
    {
        PetFollower f = Follower(Origin);
        for (int i = 0; i < 5; i++)
        {
            PetFollowStep s = f.Step(100, Origin);
            Assert.Equal((PetFollowPhase.Idle, 440.0, 300.0, 440.0, 300.0, false, false, PetMotion.Stand),
                (s.Phase, s.X, s.Y, s.TargetX, s.TargetY, s.FacingLeft, s.Teleported, s.Motion));
        }
        Assert.Equal(0, f.Teleports);
    }

    // ------------------------------------------------------------ 追赶

    [Fact]
    public void TheMasterJumpingAwayIsFollowedOnTheHandComputedSlideAndThenSnapsToTheHomePoint()
    {
        // 主角跳到 (600,300),停靠点 540,宠物在 440。每步 100 毫秒、追赶率 5,走剩余距离的一半;第一步一半是 50,但速度封顶 400 → 每步最多 40,所以先走 40
        PetFollower f = Follower(Origin);
        PetMaster master = At(600);
        double[] x = { 480.0, 510.0, 525.0, 532.5, 536.25, 538.125 };
        for (int i = 0; i < x.Length; i++)
        {
            PetFollowStep s = f.Step(100, master);
            Assert.Equal((PetFollowPhase.Chasing, x[i], 300.0, 540.0, PetMotion.Walk, false), (s.Phase, s.X, s.Y, s.TargetX, s.Motion, s.Teleported));
            Assert.False(s.FacingLeft); // 向右追,朝右
        }

        // 第七步:剩余 1.875 已在停靠半径(3)之内,落在停靠点上
        PetFollowStep arrived = f.Step(100, master);
        Assert.Equal((PetFollowPhase.Idle, 540.0, 300.0, PetMotion.Stand), (arrived.Phase, arrived.X, arrived.Y, arrived.Motion));
        Assert.Equal(PetFollowPhase.Idle, f.Phase);
        Assert.Equal(0, f.Teleports);
    }

    [Fact]
    public void TheTargetSequenceWhileTheMasterWalksOnAndThenStopsMatchesTheIndependentOracle()
    {
        // 主角每步(100 毫秒)向右走 22.2 像素共 10 步,再停 8 步。期望值来自另一份独立的 Python 实现(/tmp 里的对拍脚本),不是从被测代码里抄的
        PetFollower f = Follower(Origin);
        double masterX = 500;
        double[] walkingX = { 451.1, 467.75, 487.175, 507.9875, 529.49375, 551.346875, 573.373438, 595.486719, 617.643359, 639.82168 };
        for (int i = 0; i < walkingX.Length; i++)
        {
            masterX += 22.2;
            PetFollowStep s = f.Step(100, At(masterX));
            Assert.Equal(masterX - 60, s.TargetX, 6);                    // 目标永远是主角往背后 60
            Assert.Equal(walkingX[i], s.X, 4);
            Assert.Equal((PetFollowPhase.Chasing, PetMotion.Walk, false), (s.Phase, s.Motion, s.FacingLeft));
        }

        double[] stoppedX = { 650.91084, 656.45542, 659.22771, 662.0, 662.0, 662.0, 662.0, 662.0 };
        PetFollowPhase[] phases = { PetFollowPhase.Chasing, PetFollowPhase.Chasing, PetFollowPhase.Chasing, PetFollowPhase.Idle, PetFollowPhase.Idle,
            PetFollowPhase.Idle, PetFollowPhase.Idle, PetFollowPhase.Idle };
        for (int i = 0; i < stoppedX.Length; i++)
        {
            PetFollowStep s = f.Step(100, At(masterX));
            Assert.Equal(662.0, s.TargetX, 6);
            Assert.Equal(stoppedX[i], s.X, 4);
            Assert.Equal(phases[i], s.Phase);
            Assert.Equal(phases[i] == PetFollowPhase.Idle ? PetMotion.Stand : PetMotion.Walk, s.Motion);
        }
        Assert.Equal(0, f.Teleports);
    }

    [Fact]
    public void BothAxesAreChasedWithTheirOwnSpeedCapsAndLeaveTheChaseTogether()
    {
        // 主角到 (560,380),宠物在 (440,300):横向剩 120 → 目标 500 剩 60,纵向剩 80;纵向速度封顶 100×0.1 = 10 每步
        PetFollower f = Follower(Origin);
        double[] x = { 470.0, 485.0, 492.5, 496.25, 498.125, 499.0625, 499.53125, 499.765625, 499.882812 };
        double[] y = { 310.0, 320.0, 330.0, 340.0, 350.0, 360.0, 370.0, 375.0, 377.5 };
        for (int i = 0; i < x.Length; i++)
        {
            PetFollowStep s = f.Step(100, At(560, 380));
            Assert.Equal(x[i], s.X, 4);
            Assert.Equal(y[i], s.Y, 4);
            Assert.Equal(PetFollowPhase.Chasing, s.Phase);
        }
        // 第十步:横向 0.117、纵向 2.5,都在停靠半径之内
        PetFollowStep done = f.Step(100, At(560, 380));
        Assert.Equal((PetFollowPhase.Idle, 500.0, 380.0), (done.Phase, done.X, done.Y));
    }

    [Fact]
    public void ASlowPetIsCappedAtItsOwnSpeedAndAFastOneNeverOvershootsTheHomePoint()
    {
        // 横向最大速度 50:每步 5 像素,不论离得多远(剩余 100 → 速度 500 → 封顶 50)
        PetFollower slow = Follower(Origin, speedX: 50);
        double expect = 440;
        for (int i = 0; i < 6; i++)
        {
            expect += 5;
            Assert.Equal(expect, slow.Step(100, At(600)).X, 6);
        }

        // 给一大段时间(10 秒):走不过剩余距离,正好落在停靠点,这一步仍是"在追",下一步才是"到了"
        PetFollower fast = Follower(Origin);
        PetFollowStep jump = fast.Step(10000, At(600));
        Assert.Equal((PetFollowPhase.Chasing, 540.0), (jump.Phase, jump.X));
        Assert.Equal(PetFollowPhase.Idle, fast.Step(100, At(600)).Phase);

        // 往左也一样(负方向封顶与不过冲)
        PetFollower west = Follower(At(500));
        PetFollowStep back = west.Step(10000, At(400));
        Assert.Equal(340.0, back.X, 6);
        Assert.True(back.FacingLeft);

        // 负方向的封顶:停靠点在 340、宠物在 440,剩 −100 → 速度 −500 → 封顶 −400 → 这一步 −40(不是 −50,更不是往回走 +40)
        PetFollowStep capped = Follower(Origin).Step(100, At(400));
        Assert.Equal(400.0, capped.X, 6);
        Assert.True(capped.FacingLeft);
        // 纵向同理:宠物在 300、停靠点在 220,剩 −80 → 速度 −400 → 封顶 −100 → 这一步 −10(不是 −40)
        PetFollowStep lifted = Follower(Origin).Step(100, At(500, 220));
        Assert.Equal((440.0, 290.0), (lifted.X, lifted.Y));
    }

    // ------------------------------------------------------------ 到达与传送的边界

    [Fact]
    public void ArrivingMeansBothAxesAreWithinTheRadiusInclusiveAndSnapsToTheHomePoint()
    {
        // 停靠半径 3(含):停靠点在 443 → 剩 3 直接落点;443.25 → 剩 3.25 还在追
        PetFollower atEdge = Follower(Origin);
        PetFollowStep snap = atEdge.Step(100, At(503));
        Assert.Equal((PetFollowPhase.Idle, 443.0, PetMotion.Stand), (snap.Phase, snap.X, snap.Motion));

        PetFollower justOut = Follower(Origin);
        PetFollowStep chase = justOut.Step(100, At(503.25));
        Assert.Equal(PetFollowPhase.Chasing, chase.Phase);
        Assert.Equal(440 + 3.25 * 5 * 0.1, chase.X, 6);

        // 纵向同理:纵向剩 3 落点;横向剩 2、纵向剩 3.5 还在追(两个轴都要在半径内才算到)
        Assert.Equal(PetFollowPhase.Idle, Follower(Origin).Step(100, At(502, 303)).Phase);
        Assert.Equal(PetFollowPhase.Idle, Follower(Origin).Step(100, At(503, 303)).Phase);
        Assert.Equal(PetFollowPhase.Chasing, Follower(Origin).Step(100, At(502, 303.5)).Phase);
        Assert.Equal(PetFollowPhase.Idle, Follower(Origin).Step(100, At(497, 297)).Phase);   // 负方向也一样
        Assert.Equal(PetFollowPhase.Chasing, Follower(Origin).Step(100, At(496.5, 300)).Phase);
    }

    [Fact]
    public void TeleportingNeedsStrictlyMoreThanTheDistanceOnEitherAxisAndLandsOnTheHomePoint()
    {
        // 横向:剩余正好 400 不传送(照常追),400.5 才传送
        PetFollower atLimit = Follower(Origin);
        PetFollowStep walk = atLimit.Step(100, At(900));
        Assert.Equal((PetFollowPhase.Chasing, false, 480.0, 0), (walk.Phase, walk.Teleported, walk.X, atLimit.Teleports));

        PetFollower over = Follower(Origin);
        PetFollowStep tele = over.Step(100, At(900.5, 300, true));
        Assert.Equal((PetFollowPhase.Idle, true, PetMotion.Stand, 1), (tele.Phase, tele.Teleported, tele.Motion, over.Teleports));
        Assert.Equal((960.5, 300.0), (tele.X, tele.Y));   // 主角朝左:停靠点在主角右边 60
        Assert.True(tele.FacingLeft);                      // 传送过去的朝向同主角

        // 负方向的边界:停靠点在 40(剩 −400)照常追,停靠点在 39.5(剩 −400.5)传送
        Assert.Equal(PetFollowPhase.Chasing, Follower(Origin).Step(100, At(100)).Phase);
        Assert.True(Follower(Origin).Step(100, At(99.5)).Teleported);

        // 纵向:剩 ±200 照常追,±200.5 传送(纵向的传送距离比横向小)
        Assert.False(Follower(Origin).Step(100, At(500, 500)).Teleported);
        Assert.True(Follower(Origin).Step(100, At(500, 500.5)).Teleported);
        Assert.False(Follower(Origin).Step(100, At(500, 100)).Teleported);
        Assert.True(Follower(Origin).Step(100, At(500, 99.5)).Teleported);

        // 只因纵向过远而传送时,横向也一并落到停靠点、纵向落在主角的纵向位置上,朝向同主角、站立
        PetFollowStep below = Follower(Origin).Step(100, At(520, 500.5));
        Assert.Equal((true, 460.0, 500.5, PetFollowPhase.Idle, PetMotion.Stand, false), (below.Teleported, below.X, below.Y, below.Phase, below.Motion, below.FacingLeft));
        PetFollowStep above = Follower(Origin).Step(100, At(480, 99.5, true));
        Assert.Equal((true, 540.0, 99.5, true), (above.Teleported, above.X, above.Y, above.FacingLeft));

        // 传送算数,接着就在停靠点上:再走一步站着不动,传送次数不变
        PetFollowStep next = over.Step(100, At(900.5, 300, true));
        Assert.Equal((PetFollowPhase.Idle, false, 1), (next.Phase, next.Teleported, over.Teleports));
    }

    [Fact]
    public void EachTeleportIsCountedAndPlaceNeverCountsOne()
    {
        PetFollower f = Follower(Origin);
        f.Step(100, At(2000));
        f.Step(100, At(-2000));
        Assert.Equal(2, f.Teleports);

        f.Place(At(10, 20, true));
        Assert.Equal((70.0, 20.0, true, PetFollowPhase.Idle, 2), (f.X, f.Y, f.FacingLeft, f.Phase, f.Teleports));
    }

    // ------------------------------------------------------------ 朝向与动作

    [Fact]
    public void WhileChasingThePetFacesItsMovementAndOnceThereItFacesLikeTheMaster()
    {
        // 主角站着转身朝左:停靠点从 440 换到 560,宠物向右追(朝右),到了以后朝向跟主角(朝左)
        PetFollower f = Follower(Origin);
        PetMaster turned = At(500, 300, true);
        double[] x = { 480.0, 520.0, 540.0, 550.0, 555.0, 557.5 };
        for (int i = 0; i < x.Length; i++)
        {
            PetFollowStep s = f.Step(100, turned);
            Assert.Equal((PetFollowPhase.Chasing, x[i], 560.0, false), (s.Phase, s.X, s.TargetX, s.FacingLeft));
        }
        PetFollowStep arrived = f.Step(100, turned);
        Assert.Equal((PetFollowPhase.Idle, 560.0, true), (arrived.Phase, arrived.X, arrived.FacingLeft));

        // 只有纵向在追(横向没动)时朝向保持不变
        PetFollower vertical = Follower(new PetMaster(500, 300, true));
        PetFollowStep up = vertical.Step(100, new PetMaster(500, 330, true));
        Assert.Equal((PetFollowPhase.Chasing, true), (up.Phase, up.FacingLeft));
        Assert.Equal(560.0, up.X, 6);

        // 向左追(主角往左走)就朝左,哪怕主角自己朝右
        PetFollower west = Follower(Origin);
        Assert.True(west.Step(100, At(450, 300, false)).FacingLeft);
    }

    [Fact]
    public void TheMotionIsRunWhenMoreThanTheRunDistanceAwayAndWalkWithinItInclusive()
    {
        // 横向剩 120 正好:走;120.5:跑(严格大于才跑)
        Assert.Equal(PetMotion.Walk, Follower(Origin).Step(100, At(620)).Motion);
        Assert.Equal(PetMotion.Run, Follower(Origin).Step(100, At(620.5)).Motion);
        Assert.Equal(PetMotion.Walk, Follower(Origin).Step(100, At(380)).Motion);
        Assert.Equal(PetMotion.Run, Follower(Origin).Step(100, At(379.5)).Motion);

        // 一路追过去:剩 200 → 跑,160 → 跑,120 → 走(每步封顶走 40)
        PetFollower f = Follower(Origin);
        PetMotion[] want = { PetMotion.Run, PetMotion.Run, PetMotion.Walk, PetMotion.Walk };
        for (int i = 0; i < want.Length; i++)
        {
            Assert.Equal(want[i], f.Step(100, At(700)).Motion);
        }

        // 只有纵向偏得远时也是走:跑只看横向
        Assert.Equal(PetMotion.Walk, Follower(Origin).Step(100, At(500, 495)).Motion);
    }

    // ------------------------------------------------------------ 退化输入

    [Fact]
    public void ZeroNegativeOrNotANumberTimeMovesNothingButStillCountsAsChasing()
    {
        foreach (double ms in new[] { 0.0, -50.0, double.NaN, double.NegativeInfinity })
        {
            PetFollower f = Follower(Origin);
            PetFollowStep s = f.Step(ms, At(600));
            Assert.Equal((PetFollowPhase.Chasing, 440.0, 300.0, 540.0, PetMotion.Walk), (s.Phase, s.X, s.Y, s.TargetX, s.Motion));
        }
    }

    [Fact]
    public void ANonFiniteMasterIsIgnoredEverywhere()
    {
        PetFollower f = Follower(Origin);
        f.Step(100, At(600));
        double x = f.X;
        foreach (PetMaster bad in new[] { At(double.NaN), At(500, double.NaN), At(double.PositiveInfinity), At(500, double.NegativeInfinity) })
        {
            PetFollowStep s = f.Step(100, bad);
            Assert.Equal((x, 300.0, false), (s.X, s.Y, s.Teleported));
            Assert.Equal((PetFollowPhase.Chasing, PetMotion.Walk), (s.Phase, s.Motion));   // 追着时遇到坏坐标:仍在追、动作仍是走
            Assert.Equal((x, 300.0, 0, PetFollowPhase.Chasing), (f.X, f.Y, f.Teleports, f.Phase));
            f.Place(bad);                                   // 落点也不理它
            Assert.Equal((x, PetFollowPhase.Chasing), (f.X, f.Phase));
        }

        // 站着时遇到坏坐标:仍是站立、不动
        PetFollower idle = Follower(Origin);
        PetFollowStep still = idle.Step(100, At(double.NaN));
        Assert.Equal((PetFollowPhase.Idle, PetMotion.Stand, 440.0, 300.0), (still.Phase, still.Motion, still.X, still.Y));

        // 一开始就是坏坐标:原地(0,0),之后一个正常的主角把它带过去
        PetFollower fresh = Follower(At(double.NaN));
        Assert.Equal((0.0, 0.0, PetFollowPhase.Idle), (fresh.X, fresh.Y, fresh.Phase));
    }

    [Fact]
    public void ANullRulesObjectFallsBackToTheDefaultRules()
    {
        // 缺省规则的横向传送距离是 400
        var f = new PetFollower(null, -60, 400, 100, Origin);
        Assert.False(f.Step(100, At(900)).Teleported);
        Assert.True(f.Step(100, At(1500)).Teleported);
        Assert.Equal(-64.0, PetFollowRules.Default.SpanOf(null));   // 缺省停靠距离:背后 64
    }

    [Fact]
    public void TheDefaultRulesAreTheDocumentedPlaceholdersAndAreWhatAFollowerWithoutRulesUses()
    {
        // 文档里写的占位数(NOTES §3):追赶率 5、停靠半径 3、传送 400/200、奔跑 120、停靠深度 0、缺省停靠距离 −64
        foreach (PetFollowRules rules in new[] { PetFollowRules.Default, new PetFollowRules() })
        {
            Assert.Equal((5.0, 3.0, 400.0, 200.0, 120.0, 0.0, -64.0),
                (rules.ChaseRate, rules.ArriveRadius, rules.TeleportDistanceX, rules.TeleportDistanceY, rules.RunDistance, rules.HomeDepth, rules.DefaultSpan));
        }

        // 没给规则、没给外观:停靠点在背后 64(主角 500 → 436);各个边界都按缺省规则走
        PetFollower Fresh() => PetFollower.For(null, null, Origin);
        Assert.Equal(436.0, Fresh().X, 9);
        Assert.Equal(PetFollowPhase.Idle, Fresh().Step(100, At(503)).Phase);             // 剩 3:到了
        Assert.Equal(PetFollowPhase.Chasing, Fresh().Step(100, At(503.25)).Phase);       // 剩 3.25:还在追
        Assert.False(Fresh().Step(100, At(900)).Teleported);                            // 横向剩 400:不传送
        Assert.True(Fresh().Step(100, At(900.5)).Teleported);                           // 横向剩 400.5:传送
        Assert.False(Fresh().Step(100, At(500, 500)).Teleported);                       // 纵向剩 200:不传送
        Assert.True(Fresh().Step(100, At(500, 500.5)).Teleported);                      // 纵向剩 200.5:传送
        Assert.Equal(PetMotion.Walk, Fresh().Step(100, At(620)).Motion);                // 横向剩 120:走
        Assert.Equal(PetMotion.Run, Fresh().Step(100, At(620.5)).Motion);               // 横向剩 120.5:跑

        // 追赶率 5:剩 20 时这一步走 20 × 5 × 0.1 = 10(缺省速度远大于这个,不封顶)
        Assert.Equal(436.0 + 10.0, Fresh().Step(100, At(520)).X, 9);
    }

    // ------------------------------------------------------------ 规则从外观里取数

    private static PetLook Look(int? span, params int[] speed)
    {
        return new PetLook(901, "creature/p901/p901.cre", "creature/p901/motion/stay.ani", null, null, 0, span, speed);
    }

    [Fact]
    public void SpanComesFromTheLookWhenWrittenAndFromTheDefaultOtherwise()
    {
        var rules = new PetFollowRules(defaultSpan: -64);
        Assert.Equal(-41.0, rules.SpanOf(Look(-41)));
        Assert.Equal(25.0, rules.SpanOf(Look(25)));
        Assert.Equal(0.0, rules.SpanOf(Look(0)));          // 写了 0 就是 0,不当成没写
        Assert.Equal(-64.0, rules.SpanOf(Look(null)));
        Assert.Equal(-64.0, rules.SpanOf(null));
    }

    [Fact]
    public void SpeedsAreTheFirstAndThirdNumbersTimesTheHeroFactorsAndFallBackWhenMissingOrNotPositive()
    {
        var rules = new PetFollowRules(defaultSpeedX: 111, defaultSpeedY: 22);
        PetLook ok = Look(null, 330, 999, 130, 999, 999);
        Assert.Equal(330 * TownHero.DefaultSpeedFactorX, rules.SpeedXOf(ok), 9);
        Assert.Equal(130 * TownHero.DefaultSpeedFactorY, rules.SpeedYOf(ok), 9);

        // 不是恰好五个数 → 没写,用缺省;零与负数 → 用缺省(只看各自取用的那一个数)
        Assert.Equal((111.0, 22.0), (rules.SpeedXOf(Look(null, 330, 270, 130, 105)), rules.SpeedYOf(Look(null, 330, 270, 130, 105))));
        Assert.Equal((111.0, 22.0), (rules.SpeedXOf(Look(null)), rules.SpeedYOf(Look(null))));
        Assert.Equal((111.0, 22.0), (rules.SpeedXOf(null), rules.SpeedYOf(null)));
        Assert.Equal(111.0, rules.SpeedXOf(Look(null, 0, 5, 5, 5, 5)));
        Assert.Equal(111.0, rules.SpeedXOf(Look(null, -3, 5, 5, 5, 5)));
        Assert.Equal(22.0, rules.SpeedYOf(Look(null, 5, 5, 0, 5, 5)));
        Assert.Equal(22.0, rules.SpeedYOf(Look(null, 5, 5, -1, 5, 5)));
        // 第一个坏、第三个好(反过来也一样):各取各的
        Assert.Equal(130 * TownHero.DefaultSpeedFactorY, rules.SpeedYOf(Look(null, 0, 5, 130, 5, 5)), 9);
        Assert.Equal(330 * TownHero.DefaultSpeedFactorX, rules.SpeedXOf(Look(null, 330, 5, 0, 5, 5)), 9);
    }

    [Fact]
    public void TheDefaultSpeedsAreTheHeroDashSpeeds()
    {
        Assert.Equal(TownHero.DefaultDashSpeedX, PetFollowRules.Default.DefaultSpeedX);
        Assert.Equal(TownHero.DefaultDashSpeedY, PetFollowRules.Default.DefaultSpeedY);
    }

    [Fact]
    public void AFollowerBuiltFromALookUsesItsSpanAndItsOwnSpeedCaps()
    {
        PetLook look = Look(-41, 3, 5, 2, 5, 5);   // 横向 3 × 系数(很慢),纵向 2 × 系数
        PetFollower f = PetFollower.For(PetTownKit.SimpleRules, look, new PetMaster(500, 300, false));
        Assert.Equal(459.0, f.X, 9);                // 500 − 41

        PetFollowStep s = f.Step(100, At(700, 400));
        double capX = 3 * TownHero.DefaultSpeedFactorX * 0.1;
        double capY = 2 * TownHero.DefaultSpeedFactorY * 0.1;
        Assert.Equal(459.0 + capX, s.X, 9);
        Assert.Equal(300.0 + capY, s.Y, 9);
        Assert.Equal(659.0, s.TargetX, 9);

        // 没写外观(look 为空)时用规则里的缺省停靠距离
        PetFollower plain = PetFollower.For(PetTownKit.SimpleRules, null, new PetMaster(500, 300, false));
        Assert.Equal(436.0, plain.X, 9);            // 500 − 64
    }
}
