using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 主角的奔跑、跳跃与障碍(证据 E-072):双击方向键进入奔跑、奔跑攻击向前滑、跳跃的弹道与动画相位(.ani 里的停留帧)、空中攻击、
/// 落地帧,以及出口碰撞体这类障碍的阻挡与"顶着哪个障碍"的记录。动画与角色脚本是合成的(<see cref="AvatarFixture.ActionChrPath"/>),
/// 速度、重力与双击时间窗的取值见 <see cref="TownHero"/> 的常量(速度 242.5 是原版连拍里量到的,其余是假设)。
/// </summary>
public class HeroActionTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, string chr = AvatarFixture.ActionChrPath, IReadOnlyList<WorldRect> area = null,
        double x = 100, double y = 50, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr, outfit, area ?? Unrestricted, x, y, facingLeft, TownHero.DungeonRestTag);
    }

    /// <summary>推进 <paramref name="steps"/> 个 10 毫秒;"刚按下"的攻击与跳跃只在第一步里给。</summary>
    private static void Steps(TownHero hero, int steps, int dirX = 0, int dirY = 0, bool attack = false, bool jump = false)
    {
        for (int i = 0; i < steps; i++)
        {
            hero.Update(10, dirX, dirY, attack && i == 0, jump && i == 0);
        }
    }

    /// <summary>双击右:按下、松开、再按下(间隔在时间窗内),之后主角处在奔跑里。</summary>
    private static void DoubleTapRight(TownHero hero)
    {
        hero.Update(50, 1, 0);
        hero.Update(50, 0, 0);
        hero.Update(50, 1, 0);
    }

    [Fact]
    public void TheActionScriptLoadsTheDashAndJumpMotionsAndTheJumpStats()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.Equal((true, true, true, true), (hero.CanDash, hero.CanDashAttack, hero.CanJump, hero.CanJumpAttack));
        Assert.Equal(((double)AvatarFixture.JumpPowerStat, AvatarFixture.WeightStat, AvatarFixture.JumpSpeedStat), (hero.JumpPower, hero.WeightStat, hero.JumpSpeedStat));
        Assert.Equal(300.0, hero.DashAttackDurationMs);                  // 三帧 × 100 毫秒
        Assert.Equal(200.0, hero.JumpAttackDurationMs);                  // 100 + 50 + 50 毫秒

        // 跳跃动画的相位:停留帧在 3 与 6,所以下落帧是 4、落地帧是 7;蹲姿 = 第 0 帧(100 毫秒),落地段 = 最后一帧(50 毫秒)
        Assert.Equal((1, 4, 7), (hero.JumpLiftoffFrame, hero.JumpFallFrame, hero.JumpLandFrame));
        Assert.Equal((100.0, 50.0), (hero.JumpPrepareMs, hero.JumpLandingMs));

        // 没写这些标签的角色脚本:全部不可用,跳跃的三个属性取默认值
        TownHero plain = NewHero(assets, AvatarFixture.ChrPath);
        Assert.Equal((false, false, false, false), (plain.CanDash, plain.CanDashAttack, plain.CanJump, plain.CanJumpAttack));
        Assert.Equal((TownHero.DefaultJumpPower, TownHero.DefaultWeight, TownHero.DefaultJumpSpeedStat), (plain.JumpPower, plain.WeightStat, plain.JumpSpeedStat));
        Assert.Equal((0.0, 0.0), (plain.DashAttackDurationMs, plain.JumpAttackDurationMs));
        Assert.Equal((-1, -1, 0.0, TownHero.FallbackLandingMs), (plain.JumpFallFrame, plain.JumpLandFrame, plain.JumpPrepareMs, plain.JumpLandingMs));
    }

    [Fact]
    public void TheTakeoffSpeedAndTheGravityFollowTheClientFormulas()
    {
        // 起跳初速度 = 跳跃力 × 4000 / (((重量 × 100) / 跳跃速度 − 10000) × 0.0121 + 2100),除法取整(这里的数是合成的)
        // 400 × 4000 / ((6000000 / 90 = 66666 − 10000) × 0.0121 + 2100)
        double expected = 400 * 4000.0 / ((66666 - 10000) * 0.0121 + 2100.0);
        Assert.Equal(expected, TownHero.TakeoffSpeed(400, 60000, 90), 9);
        Assert.InRange(expected, 574.0, 575.0);
        // 跳跃速度属性是 100 时整数除法正好整除
        Assert.Equal(400 * 4000.0 / ((60000 - 10000) * 0.0121 + 2100.0), TownHero.TakeoffSpeed(400, 60000, 100), 9);
        // 跳跃力成正比;越重越低;跳跃速度属性越大越高
        Assert.Equal(2 * TownHero.TakeoffSpeed(300, 60000, 90), TownHero.TakeoffSpeed(600, 60000, 90), 9);
        Assert.True(TownHero.TakeoffSpeed(400, 90000, 90) < TownHero.TakeoffSpeed(400, 60000, 90));
        Assert.True(TownHero.TakeoffSpeed(400, 60000, 120) > TownHero.TakeoffSpeed(400, 60000, 90));
        Assert.Equal(0.0, TownHero.TakeoffSpeed(0, 60000, 90), 9);
        Assert.True(TownHero.TakeoffSpeed(400, 60000, 0) > 0);          // 跳跃速度 0 按 1 处理,不除零

        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.Equal(expected, hero.JumpTakeoffSpeed, 9);
        Assert.Equal(90 * 1500 / 100.0, hero.JumpGravity, 9);            // 重力 = 跳跃速度 × 重力基数 / 100
        Assert.Equal(2 * expected / hero.JumpGravity * 1000, hero.JumpAirtimeMs, 6);
        Assert.Equal(expected * expected / (2 * hero.JumpGravity), hero.JumpApexHeight, 6);
        hero.MapGravityAdjust = 150;                                    // 修正项加在(负的)重力基数 −1500 上:+150 让重力变小
        Assert.Equal(90 * 1350 / 100.0, hero.JumpGravity, 9);
    }

    [Fact]
    public void DoubleTappingASideKeyRunsAtTheDashSpeedAndKeepsRunningWhileHeld()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        hero.Update(50, 1, 0);                                           // 第一次按下:走
        Assert.Equal(HeroMotion.Move, hero.Motion);
        Assert.False(hero.RunArmed);
        Assert.Equal(100 + TownHero.DefaultWalkSpeedX * 0.05, hero.X, 6);
        hero.Update(50, 0, 0);                                           // 松开:站
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        double x0 = hero.X;
        hero.Update(50, 1, 0);                                           // 时间窗内再按:奔跑
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        Assert.True(hero.RunArmed);
        Assert.Equal(AvatarFixture.DashAni, hero.CurrentAnimationPath);
        Assert.Equal(x0 + TownHero.DefaultDashSpeedX * 0.05, hero.X, 6);
        Assert.False(hero.FacingLeft);

        hero.Update(1000, 1, 0);                                         // 一直按着:保持奔跑
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        Assert.Equal(x0 + TownHero.DefaultDashSpeedX * 1.05, hero.X, 4);
        Assert.True(TownHero.DefaultDashSpeedX > TownHero.DefaultWalkSpeedX);
    }

    [Fact]
    public void TheDoubleTapWindowIsMeasuredFromTheFirstPressToTheSecondPressAndRunsInEitherDirection()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 松开 200 毫秒 + 再按的那一步 100 毫秒 = 300 毫秒:刚好在窗内
        TownHero atEdge = NewHero(assets, x: 500);
        atEdge.Update(10, -1, 0);
        atEdge.Update(200, 0, 0);
        atEdge.Update(100, -1, 0);
        Assert.Equal(HeroMotion.Dash, atEdge.Motion);                    // 往左也一样
        Assert.True(atEdge.FacingLeft);

        // 多 1 毫秒就算两次单击
        TownHero late = NewHero(assets, x: 500);
        late.Update(10, -1, 0);
        late.Update(200, 0, 0);
        late.Update(101, -1, 0);
        Assert.Equal(HeroMotion.Move, late.Motion);
        Assert.False(late.RunArmed);

        // 第二次单击又是新的一次"第一次按下":紧接着在窗内再按一次才算双击
        late.Update(50, 0, 0);
        late.Update(50, -1, 0);
        Assert.Equal(HeroMotion.Dash, late.Motion);
    }

    [Fact]
    public void ATapInTheOtherDirectionOrWithoutADashMotionDoesNotRunAndTheStaleStateStartsFresh()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 先右后左:不是双击
        TownHero flip = NewHero(assets);
        flip.Update(50, 1, 0);
        flip.Update(50, 0, 0);
        flip.Update(50, -1, 0);
        Assert.Equal(HeroMotion.Move, flip.Motion);
        Assert.False(flip.RunArmed);

        // 直接从右换成左(中间没有松开)也不是双击
        TownHero direct = NewHero(assets);
        direct.Update(50, 1, 0);
        direct.Update(50, -1, 0);
        Assert.False(direct.RunArmed);

        // 没有 [dash motion] 的角色:双击也只是走,速度是行走速度
        TownHero plain = NewHero(assets, AvatarFixture.ChrPath);
        plain.Update(50, 1, 0);
        plain.Update(50, 0, 0);
        double x0 = plain.X;
        plain.Update(50, 1, 0);
        Assert.Equal(HeroMotion.Move, plain.Motion);
        Assert.Equal(x0 + TownHero.DefaultWalkSpeedX * 0.05, plain.X, 6);
    }

    [Fact]
    public void ReleasingEverythingOrReversingEndsTheRunButHoldingOnlyAVerticalKeyKeepsItRunning()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        DoubleTapRight(hero);
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        hero.Update(10, 0, 0);                                           // 方向全松开:站住
        Assert.Equal((HeroMotion.Rest, false), (hero.Motion, hero.RunArmed));

        DoubleTapRight(hero);
        hero.Update(10, -1, 0);                                          // 反向:走(朝左)
        Assert.Equal((HeroMotion.Move, false, true), (hero.Motion, hero.RunArmed, hero.FacingLeft));

        // 松开左右、只留着下:奔跑继续(客户端的退出条件是"方向全空"或"水平方向与朝向相反"),水平不动,竖直按奔跑的竖直速度
        DoubleTapRight(hero);
        double x0 = hero.X, y0 = hero.Y;
        hero.Update(100, 0, 1);
        Assert.Equal((HeroMotion.Dash, true, false), (hero.Motion, hero.RunArmed, hero.FacingLeft));
        Assert.Equal(x0, hero.X, 9);
        Assert.Equal(y0 + TownHero.EngineDashY * hero.SpeedFactorY * 0.1, hero.Y, 6);
        hero.Update(100, 1, 1);                                          // 再按回朝向的那一侧:斜着跑
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        hero.Update(100, 0, 0);                                          // 全松开才停
        Assert.Equal((HeroMotion.Rest, false), (hero.Motion, hero.RunArmed));
    }

    [Fact]
    public void UpAndDownAloneNeverStartARun()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(50, 0, 1);
        hero.Update(50, 0, 0);
        hero.Update(50, 0, 1);                                           // 对上下方向双击没有奔跑
        Assert.Equal((HeroMotion.Move, false), (hero.Motion, hero.RunArmed));
    }

    [Fact]
    public void RunningUsesTheDashRowOfTheSpeedTableAndItsDiagonalColumnWhenBothAxesAreHeld()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        DoubleTapRight(hero);
        double x0 = hero.X, y0 = hero.Y;
        hero.Update(100, 1, 0);                                          // 直跑:水平按奔跑速度,竖直不动
        Assert.Equal(x0 + TownHero.DefaultDashSpeedX * 0.1, hero.X, 6);
        Assert.Equal(y0, hero.Y, 9);
        x0 = hero.X;
        hero.Update(100, 1, 1);                                          // 斜着跑:两个轴都改用斜跑的数值
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        Assert.Equal(x0 + TownHero.EngineDashDiagonalX * hero.SpeedFactorX * 0.1, hero.X, 6);
        Assert.Equal(y0 + TownHero.EngineDashDiagonalY * hero.SpeedFactorY * 0.1, hero.Y, 6);
        Assert.True(TownHero.EngineDashDiagonalX < TownHero.EngineDashX);
    }

    [Fact]
    public void AttackingWhileRunningSlidesForwardWithADecayingSpeedThenStandsAndEndsTheRun()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        DoubleTapRight(hero);
        double x0 = hero.X;
        long swings = hero.SwingNumber;

        hero.Update(10, 1, 0, true);
        Assert.Equal((HeroMotion.DashAttack, true, HeroAttackKind.Dash), (hero.Motion, hero.IsAttacking, hero.AttackKind));
        Assert.Equal("dashattack.ani", hero.AttackMotionFile);
        Assert.Equal(AvatarFixture.DashAttackAni, hero.CurrentAnimationPath);
        Assert.Equal(swings + 1, hero.SwingNumber);
        Assert.Equal(3, hero.AttackCount);                               // 连招本身还是三段,与奔跑攻击互不相干

        // 挥击中不能转身、不能改走向;滑行速度从起手时的水平速度(直跑)线性减到 0(300 毫秒):走完 速度 × 0.3 / 2 像素
        for (int i = 0; i < 40 && hero.IsAttacking; i++)
        {
            hero.Update(10, -1, 1, false);
        }
        Assert.False(hero.IsAttacking);
        Assert.Equal((HeroMotion.Rest, false, false, 0), (hero.Motion, hero.RunArmed, hero.FacingLeft, hero.AttackStage));
        Assert.Equal(x0 + TownHero.DefaultDashSpeedX * 0.3 / 2.0, hero.X, 6);
        Assert.Equal(HeroAttackKind.Normal, hero.AttackKind);
        Assert.Null(hero.AttackMotionFile);

        // 再按住右不算奔跑(要重新双击)
        hero.Update(10, 1, 0);
        Assert.Equal(HeroMotion.Move, hero.Motion);
    }

    [Fact]
    public void ADashAttackSlidesTheSameDistanceWhateverTheFrameLengthAndStopsAtTheWalkAreaEdge()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        double slide = TownHero.DefaultDashSpeedX * 0.3 / 2.0;           // 滑行总距离:速度从 v 线性减到 0,300 毫秒

        TownHero one = NewHero(assets);
        DoubleTapRight(one);
        double x0 = one.X;
        one.Update(1000, 1, 0, true);                                    // 一帧就把整个奔跑攻击走完
        Assert.Equal(HeroMotion.Rest, one.Motion);
        Assert.Equal(x0 + slide, one.X, 9);

        TownHero many = NewHero(assets);
        DoubleTapRight(many);
        many.Update(1, 1, 0, true);
        for (int i = 0; i < 299; i++)
        {
            many.Update(1, 0, 0);
        }
        Assert.Equal(HeroMotion.Rest, many.Motion);                      // 300 毫秒整,与一帧走完的结果一样
        Assert.Equal(x0 + slide, many.X, 6);

        // 区域右边界 x = 140:双击的三步走到 133.375,滑行本来能走到 200,被边界挡在 140
        var area = new List<WorldRect> { new WorldRect(0, 0, 140, 100) };
        TownHero edge = NewHero(assets, area: area);
        DoubleTapRight(edge);
        Assert.Equal(100 + TownHero.DefaultWalkSpeedX * 0.05 + TownHero.DefaultDashSpeedX * 0.05, edge.X, 6);
        Assert.InRange(edge.X, 133.0, 134.0);
        edge.Update(10, 1, 0, true);
        for (int i = 0; i < 40 && edge.IsAttacking; i++)
        {
            edge.Update(10, 0, 0);
        }
        Assert.False(edge.IsAttacking);
        Assert.Equal(140.0, edge.X, 5);
    }

    [Fact]
    public void AJumpCrouchesFirstThenFollowsTheBallisticArcAndLandsThenStandsAfterTheLandingFrame()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        double v0 = hero.JumpTakeoffSpeed, g = hero.JumpGravity;
        int prepare = (int)hero.JumpPrepareMs;
        Assert.Equal(100, prepare);

        // 按下起跳(同时按着右):先蹲姿——还在地面上,不管按着什么方向都不动
        hero.Update(1, 1, 0, false, true);
        Assert.Equal((HeroMotion.Jump, true, true), (hero.Motion, hero.IsAirborne, !hero.FacingLeft));
        Assert.Equal(AvatarFixture.JumpAni, hero.CurrentAnimationPath);
        double x0 = hero.X;
        for (int ms = 2; ms <= prepare; ms++)
        {
            hero.Update(1, 1, 0);
        }
        Assert.Equal((0.0, x0, 1), (hero.Z, hero.X, hero.CurrentFrame));   // 蹲满 100 毫秒:刚离地,动画换到起跳帧

        // 离地后的第一步:初速度 v0、重力 g
        hero.Update(10, 0, 0);
        Assert.Equal(v0 * 0.01 - 0.5 * g * 0.01 * 0.01, hero.Z, 9);

        // 一毫秒一毫秒走到落地:腾空时间 = 2 v0 / g,最高 = v0² / 2g
        double maxZ = hero.Z;
        int now = prepare + 10;
        while (hero.Z > 0 && now < 3000)
        {
            hero.Update(1, 0, 0);
            now++;
            maxZ = Math.Max(maxZ, hero.Z);
        }
        Assert.InRange(now, prepare + hero.JumpAirtimeMs, prepare + hero.JumpAirtimeMs + 1.0001);
        Assert.InRange(maxZ, hero.JumpApexHeight - 0.01, hero.JumpApexHeight);
        Assert.Equal((HeroMotion.Jump, false, hero.JumpLandFrame), (hero.Motion, hero.IsAttacking, hero.CurrentFrame));   // 落地帧:还是跳跃动作

        // 落地段 50 毫秒(从精确的触地时刻算起):这段时间不接受操作
        double x = hero.X;
        hero.Update(40, 1, 0, true, true);
        Assert.Equal((HeroMotion.Jump, x, 0L), (hero.Motion, hero.X, hero.SwingNumber));
        hero.Update(10, 0, 0);
        Assert.Equal((HeroMotion.Rest, 0.0, false), (hero.Motion, hero.Z, hero.IsAirborne));
        hero.Update(10, 0, 0, false, true);                              // 站稳之后又能起跳
        Assert.Equal(HeroMotion.Jump, hero.Motion);
    }

    [Fact]
    public void TheLandingAndTheApexAreTimedExactlyWhateverTheFrameLength()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 同一段跳跃:一个大步长一次走到落地段的中间,与一毫秒一毫秒走,落地后站起来的时刻相同(落地段 50 毫秒从精确触地时刻算起)
        TownHero fine = NewHero(assets);
        fine.Update(1, 0, 0, false, true);
        double landAt = fine.JumpPrepareMs + fine.JumpAirtimeMs;          // 触地的精确时刻(自按下起跳起算)
        int t = 1;
        while (fine.Motion == HeroMotion.Jump && t < 5000)
        {
            fine.Update(1, 0, 0);
            t++;
        }
        Assert.Equal(HeroMotion.Rest, fine.Motion);
        Assert.InRange(t, landAt + fine.JumpLandingMs, landAt + fine.JumpLandingMs + 1.0001);

        TownHero coarse = NewHero(assets);
        coarse.Update(40, 0, 0, false, true);
        coarse.Update(landAt + 20 - 40, 0, 0);                            // 触地之后 20 毫秒
        Assert.Equal((HeroMotion.Jump, 0.0), (coarse.Motion, coarse.Z));
        coarse.Update(29, 0, 0);                                          // 49 毫秒:还在落地段
        Assert.Equal(HeroMotion.Jump, coarse.Motion);
        coarse.Update(2, 0, 0);                                           // 51 毫秒:站起来
        Assert.Equal(HeroMotion.Rest, coarse.Motion);
    }

    [Fact]
    public void TheJumpAnimationCrouchesRisesHoldsAtTheTopThenLoopsTheFallPhaseAndPlaysTheLandingFrame()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.Equal((1, 4, 7), (hero.JumpLiftoffFrame, hero.JumpFallFrame, hero.JumpLandFrame));

        // 动画帧(起始毫秒):0(0)、1(100)、2(200)、3 停留(280)、4(10280)、5(10380)、6 停留(10460)、7 落地(20460)
        // 时间轴(自按下起跳起算):蹲姿 0..100,离地后的上升段 100..(100 + v0/g),之后的下降段到落地
        double apex = hero.JumpPrepareMs + hero.JumpTakeoffSpeed / hero.JumpGravity * 1000.0;     // ≈ 525.4 毫秒
        double landing = hero.JumpPrepareMs + hero.JumpAirtimeMs;                                // ≈ 950.9 毫秒
        var frames = new Dictionary<int, int>();
        hero.Update(1, 0, 0, false, true);
        frames[1] = hero.CurrentFrame;
        int last = (int)landing + 1;
        for (int ms = 2; ms <= last; ms++)
        {
            hero.Update(1, 0, 0);
            frames[ms] = hero.CurrentFrame;
        }
        // 蹲姿:只播第 0 帧(没有离地)
        Assert.Equal(new[] { 0, 0, 0 }, new[] { frames[1], frames[50], frames[99] });
        // 离地后:起跳帧 100..199 毫秒,第 2 帧 200..279,然后停在第一个停留帧(第 3 帧)直到最高点
        Assert.Equal(new[] { 1, 1, 2, 2, 3, 3 }, new[] { frames[100], frames[199], frames[200], frames[279], frames[280], frames[(int)apex - 5] });
        // 最高点之后:下降段在第 4、5 帧之间循环(各 100、80 毫秒),不进第二个停留帧(第 6 帧)
        int d = (int)Math.Ceiling(apex);                                  // 第一步已经过了最高点
        Assert.Equal(new[] { 4, 4, 5, 5, 4, 4, 5 }, new[] { frames[d + 10], frames[d + 90], frames[d + 110], frames[d + 170], frames[d + 190], frames[d + 270], frames[d + 290] });
        for (int ms = d + 1; ms < (int)landing; ms++)
        {
            Assert.InRange(frames[ms], 4, 5);
        }
        Assert.Equal(7, frames[last]);                                    // 落地:落地帧
    }

    [Fact]
    public void WhileAirborneTheHeldDirectionsMoveTheHeroAtTheWalkingSpeedAndHalfTheDepthSpeedAndTurnIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, x: 500, y: 100);
        double factorX = hero.SpeedFactorX, factorY = hero.SpeedFactorY;

        hero.Update(10, -1, 1, false, true);                             // 一边按左下一边起跳:朝左,先蹲 100 毫秒
        Assert.True(hero.FacingLeft);
        Steps(hero, 9, -1, 1);
        Assert.Equal((500.0, 100.0), (hero.X, hero.Y));                  // 蹲姿期间不动

        // 离地后:水平速度与地面行走相同(斜向用斜行的数值),深度速度是行走的一半
        Steps(hero, 20, -1, 1);
        double xDiag = TownHero.EngineWalkDiagonalX * factorX * 0.01;
        double yDiag = TownHero.EngineAirDiagonalY * factorY * 0.01;
        Assert.Equal(500 - 20 * xDiag, hero.X, 6);
        Assert.Equal(100 + 20 * yDiag, hero.Y, 6);
        Assert.Equal(TownHero.EngineAirY, Math.Floor(TownHero.EngineWalkY / 2));     // 深度速度减半(客户端用整数除法:114/2 = 57、95/2 = 47)
        Assert.Equal(TownHero.EngineAirDiagonalY, Math.Floor(TownHero.EngineWalkDiagonalY / 2));

        Steps(hero, 20, 1, -1);                                          // 腾空中可以反向、可以转身
        Assert.False(hero.FacingLeft);
        Assert.Equal(500.0, hero.X, 6);
        Assert.Equal(100.0, hero.Y, 6);

        double x1 = hero.X;
        Steps(hero, 10, 1, 0);                                           // 直着飘:用直行的水平速度,深度不动
        Assert.Equal(x1 + 10 * TownHero.EngineWalkX * factorX * 0.01, hero.X, 6);
        Assert.Equal(100.0, hero.Y, 6);
        double y1 = hero.Y;
        Steps(hero, 10, 0, 1);                                           // 只按下:水平不动,深度用空中的竖直速度
        Assert.Equal(y1 + 10 * TownHero.EngineAirY * factorY * 0.01, hero.Y, 6);
        Assert.True(hero.IsAirborne);

        // 一路按着右下直到落地,再等落地段结束
        for (int i = 0; i < 200 && hero.Motion != HeroMotion.Rest; i++)
        {
            hero.Update(10, 1, 1);
        }
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void ARunningJumpUsesTheWalkingAirSpeedsBecauseBothJumpSourcesSendTheSameCommand()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        DoubleTapRight(hero);
        double x0 = hero.X;
        hero.Update(10, 1, 0, false, true);
        Steps(hero, 9, 1, 0);                                            // 蹲姿期间不动(奔跑速度也不带进来)
        Assert.Equal(x0, hero.X, 9);
        Steps(hero, 40, 1, 0);                                           // 离地后 400 毫秒,还在上升
        Assert.True(hero.IsAirborne);
        Assert.Equal(x0 + 40 * TownHero.EngineWalkX * hero.SpeedFactorX * 0.01, hero.X, 6);

        // 同一段起跳从站立出发:一样
        TownHero walker = NewHero(assets);
        walker.Update(10, 1, 0, false, true);
        Steps(walker, 49, 1, 0);
        Assert.Equal(100 + 40 * TownHero.EngineWalkX * walker.SpeedFactorX * 0.01, walker.X, 6);
    }

    [Fact]
    public void OneAirAttackPerJumpWhichEndsOnItsOwnOrWithTheLanding()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        hero.Update(10, 0, 0, false, true);
        Steps(hero, 19, 0, 0);                                           // 蹲 100 毫秒 + 空中 100 毫秒
        long swings = hero.SwingNumber;
        hero.Update(10, 1, 0, true);                                     // 空中出招,同时按着右:水平照常飘,朝向在出招时锁住
        Assert.Equal((HeroMotion.JumpAttack, HeroAttackKind.Jump, true), (hero.Motion, hero.AttackKind, hero.IsAttacking));
        Assert.Equal("jumpattack.ani", hero.AttackMotionFile);
        Assert.Equal(swings + 1, hero.SwingNumber);
        Assert.True(hero.IsAirborne);
        Steps(hero, 5, -1, 0);                                           // 按左:位置照常动,脸不转
        Assert.False(hero.FacingLeft);
        Steps(hero, 14, 0, 0);                                           // 一共 20 步 = 200 毫秒:挥完,回到跳跃动作
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Assert.False(hero.IsAttacking);

        hero.Update(10, 0, 0, true);                                     // 每次跳跃只能出一次
        Assert.Equal((HeroMotion.Jump, swings + 1), (hero.Motion, hero.SwingNumber));

        // 落地时还在挥:被打断,进入落地帧
        TownHero late = NewHero(assets);
        late.Update(10, 0, 0, false, true);
        Steps(late, 76, 0, 0);                                           // 第 77 步:蹲 100 毫秒 + 空中 670 毫秒
        late.Update(10, 0, 0, true);                                     // 第 78 步出招,200 毫秒挥不完就落地(在第 96 步,950.9 毫秒)
        Assert.Equal(HeroMotion.JumpAttack, late.Motion);
        Steps(late, 17, 0, 0);                                           // 第 95 步:还在空中挥
        Assert.Equal(HeroMotion.JumpAttack, late.Motion);
        late.Update(10, 0, 0);                                           // 第 96 步:落地
        Assert.Equal((HeroMotion.Jump, false, 0.0), (late.Motion, late.IsAttacking, late.Z));
        late.Update(50, 0, 0);
        Assert.Equal(HeroMotion.Rest, late.Motion);
    }

    [Fact]
    public void JumpAndAirAttackAreIgnoredWithoutTheirMotionsAndASecondJumpInTheAirIsIgnored()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        TownHero plain = NewHero(assets, AvatarFixture.ChrPath);
        plain.Update(10, 0, 0, false, true);
        Assert.Equal((HeroMotion.Rest, 0.0), (plain.Motion, plain.Z));

        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, false, true);
        Steps(hero, 20, 0, 0);                                           // 第 21 步:蹲 100 毫秒 + 空中 110 毫秒
        hero.Update(10, 0, 0, false, true);                              // 第 22 步:空中再按跳跃,没有二段跳
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        double v0 = hero.JumpTakeoffSpeed, g = hero.JumpGravity;
        Assert.Equal(v0 * 0.12 - 0.5 * g * 0.12 * 0.12, hero.Z, 6);      // 还是同一条弹道(初速度没有被重置):空中 120 毫秒
    }

    [Fact]
    public void TheJumpLiftsTheSpritesByTheHeightButKeepsTheDepthOrderOnTheGround()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, y: 120);
        var projection = new TownProjection(0, 200);

        hero.Update(10, 0, 0, false, true);
        int z1 = (int)Math.Round(hero.Z);                                // 蹲姿:还在地上(动画帧 0)
        Assert.Equal(0, z1);
        List<SceneSprite> first = hero.Sprites(assets, projection);
        Steps(hero, 52, 0, 0);                                           // 第 53 步(530 毫秒):刚过最高点(动画帧 4,与帧 0 用同一张 IMG 帧,图层数相同)
        int z2 = (int)Math.Round(hero.Z);
        List<SceneSprite> top = hero.Sprites(assets, projection);

        Assert.Equal(first.Count, top.Count);
        Assert.True(z2 > z1 + 80);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(z2 - z1, first[i].ScreenY - top[i].ScreenY);    // 画面往上抬(y 变小)
            Assert.Equal(first[i].ScreenX, top[i].ScreenX);
        }
        Assert.All(top, s => Assert.Equal(120, s.SortKey));              // 深度排序仍按脚下的 y
    }

    [Fact]
    public void PlacingTheHeroNeverLeavesItMidAirAndClearsTheAirState()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, false, true);
        Steps(hero, 10, 0, 0);
        Assert.True(hero.IsAirborne);

        hero.Place(300, 60, true, HeroMotion.Jump, 123);                 // 腾空状态有速度与相位,摆不出来:摆成站立
        Assert.Equal((HeroMotion.Rest, 0.0, false, 300.0, 60.0), (hero.Motion, hero.Z, hero.IsAirborne, hero.X, hero.Y));
        hero.Place(300, 60, true, HeroMotion.JumpAttack, 0);
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        // 摆成奔跑:接着按住同一方向仍在奔跑,不会被当成新的一次按下
        hero.Place(300, 60, false, HeroMotion.Dash, 0);
        Assert.Equal((HeroMotion.Dash, true), (hero.Motion, hero.RunArmed));
        hero.Update(10, 1, 0);
        Assert.Equal(HeroMotion.Dash, hero.Motion);

        // 没有奔跑动作的角色摆不出奔跑
        TownHero plain = NewHero(assets, AvatarFixture.ChrPath);
        plain.Place(1, 1, false, HeroMotion.Dash, 0);
        Assert.Equal(HeroMotion.Rest, plain.Motion);
        plain.Place(1, 1, false, HeroMotion.DashAttack, 0);
        Assert.Equal(HeroMotion.Rest, plain.Motion);
    }

    /// <summary>x ≥ <see cref="From"/> 的地方是一堵墙;BlockedBy 返回的对象用来辨认顶着的是哪一个障碍。</summary>
    private sealed class Wall : IHeroBlocker
    {
        public readonly object Id = new object();
        public double From;

        public Wall(double from)
        {
            From = from;
        }

        public object BlockedBy(double x, double y)
        {
            return x >= From ? Id : null;
        }
    }

    private static readonly IReadOnlyList<WorldRect> Field = new List<WorldRect> { new WorldRect(0, 0, 1000, 200) };

    [Fact]
    public void ABlockerStopsTheHeroAtItsEdgeAndRecordsWhichObstacleWasPushedUntilTheNextUpdate()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, area: Field);
        var wall = new Wall(200);
        hero.Blocker = wall;

        Assert.Null(hero.PushedInto);
        hero.Update(1000, 1, 0);
        Assert.Equal(200.0, hero.X, 5);                                  // 停在墙沿(在墙外一侧)
        Assert.Same(wall.Id, hero.PushedInto);
        Assert.False(hero.PushedUp || hero.PushedDown);
        hero.Update(10, 0, 0);
        Assert.Null(hero.PushedInto);                                    // 每次更新重新记
        hero.Update(10, -1, 0);                                          // 往回走:没顶到
        Assert.Null(hero.PushedInto);
        Assert.True(hero.X < 200);
    }

    [Fact]
    public void PushedUpAndPushedDownAreSetWhenTheWalkAreaEdgeStopsTheVerticalMove()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, area: Field, y: 150);

        hero.Update(1000, 0, 1);
        Assert.Equal(200.0, hero.Y, 5);
        Assert.Equal((false, true), (hero.PushedUp, hero.PushedDown));
        hero.Update(10, 0, 0);
        Assert.Equal((false, false), (hero.PushedUp, hero.PushedDown));
        hero.Update(5000, 0, -1);
        Assert.Equal(0.0, hero.Y, 5);
        Assert.Equal((true, false), (hero.PushedUp, hero.PushedDown));
        Assert.Null(hero.PushedInto);                                    // 竖直方向挡住的不记成障碍
    }

    [Fact]
    public void AHeroStartingInsideAnObstacleWalksOutAndIsThenBlockedByItFromOutside()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, area: Field, x: 250);
        var wall = new Wall(200);
        hero.Blocker = wall;

        hero.Update(1000, -1, 0);                                        // 出生时压在障碍里:这一步忽略障碍,能走出来
        Assert.Equal(250 - TownHero.DefaultWalkSpeedX, hero.X, 6);
        Assert.Null(hero.PushedInto);
        hero.Update(1000, 1, 0);                                         // 走出来之后就被挡了
        Assert.Equal(200.0, hero.X, 5);
        Assert.Same(wall.Id, hero.PushedInto);
    }

    [Fact]
    public void AnAirborneHeroIsBlockedByTheObstacleToo()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, area: Field, x: 150);
        var wall = new Wall(200);
        hero.Blocker = wall;

        hero.Update(10, 1, 0, false, true);
        Steps(hero, 40, 1, 0);                                           // 0.41 秒 × 211 像素/秒 = 86 像素,够顶到墙
        Assert.True(hero.IsAirborne);
        Assert.Equal(200.0, hero.X, 5);
        Assert.Same(wall.Id, hero.PushedInto);
        Assert.True(hero.Z > 90);
    }

    [Fact]
    public void TheTimelineGivesTheStartOfEachFrameAndClampsOutOfRangeFrames()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AniFile jump = assets.LoadAni(AvatarFixture.JumpAni)!;
        var timeline = new AniTimeline(jump);
        Assert.Equal(new long[] { 0, 100, 200, 280, 10280, 10380, 10460, 20460 }, Enumerable.Range(0, 8).Select(timeline.StartMs).ToArray());
        Assert.Equal(0, timeline.StartMs(-3));
        Assert.Equal(20460, timeline.StartMs(99));
        Assert.Equal(3, timeline.FrameAt(280));
        Assert.Equal(3, timeline.FrameAt(10279));
        Assert.Equal(4, timeline.FrameAt(10280));
    }
}
