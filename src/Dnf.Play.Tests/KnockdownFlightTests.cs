using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物与主角共用的倒地运动与动画时钟(<see cref="KnockdownFlight"/>、<see cref="DownAnimation"/>,证据 E-082、E-083、E-085、E-105):
/// 动画形状(长停留帧与躺着的那一帧)、三个阶段靠"落地后再请求"串起来、提前结束、已死转入死亡、不再倒地的两种停法。
/// 怪物那一侧的行为(受击框、阶段计时、死亡淡出……)由 <c>MonsterKnockdownTests</c> 等覆盖,这里只看共用部分本身。
/// 动画是 <see cref="AvatarFixture.DownAni"/>:80、10000、10000、400 毫秒四帧(第 2、3 帧是长停留帧,第 4 帧躺着)。
/// </summary>
public class KnockdownFlightTests
{
    private const int Weight = 60000;

    private static KnockdownRequest Request(int stage = 0, int lift = 300)
    {
        return new KnockdownRequest(stage, Knockdown.TypeOf(false, false, stage), 0, lift);
    }

    private static void Run(KnockdownFlight flight, Func<bool> until, int maxSteps = 1000)
    {
        for (int i = 0; i < maxSteps && !until(); i++)
        {
            flight.Place(flight.X, flight.Y, flight.Z, flight.FacingLeft);
            flight.Update(10, null);
        }
        Assert.True(until());
    }

    [Fact]
    public void TheAnimationShapeIsReadFromTheHoldFrames()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var anim = new DownAnimation(assets.LoadAni(AvatarFixture.DownAni));
        Assert.True(anim.HasAnimation);
        Assert.Equal(new[] { 1, 2 }, anim.HoldFrames.ToArray());
        Assert.Equal(3, anim.LyingFrame);                                 // 最后一个长停留帧的下一帧
        Assert.True(anim.IsHold(1));
        Assert.False(anim.IsHold(0));
        Assert.False(anim.IsHold(3));
        Assert.Equal(20480L, anim.TotalMs);
        Assert.Equal(20080.0, anim.StartMs(3));
        Assert.Equal((0, 1, 3), (anim.FrameAt(5), anim.FrameAt(80), anim.FrameAt(20085)));

        var none = new DownAnimation(null);
        Assert.False(none.HasAnimation);
        Assert.Equal((-1, 0L, -1), (none.LyingFrame, none.TotalMs, none.FrameAt(10)));
        Assert.Empty(none.HoldFrames);
    }

    [Fact]
    public void TheThreeStagesAreChainedByLandingsAndTheEndIsReportedOnce()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var flight = new KnockdownFlight(Weight, new DownAnimation(assets.LoadAni(AvatarFixture.DownAni)));
        flight.Place(300, 100, 0, false);
        flight.Begin(Request(), 500, 500, true);
        Assert.Equal((true, 0, 1, true), (flight.Active, flight.Stage, flight.BeginCount, flight.VerticalSpeed > 0));
        Assert.True(flight.FacingLeft == false);                          // pushToLeft:朝向攻击方(右边),速度背离

        var stages = new List<int>();
        Run(flight, () =>
        {
            if (stages.Count == 0 || stages[stages.Count - 1] != flight.Stage)
            {
                stages.Add(flight.Stage);
            }
            return !flight.Active;
        });
        Assert.Equal(new[] { 0, 1, 2, -1 }, stages);
        Assert.Equal(3, flight.BeginCount);                               // 首次 + 阶段 0 落地后的再请求 + 阶段 1 落地后的再请求
        Assert.True(flight.TakeEnded());
        Assert.False(flight.TakeEnded());                                 // 通知只取一次
        Assert.False(flight.TakeDied());
        Assert.Equal(0.0, flight.Z);
        Assert.Equal(0.0, flight.HorizontalSpeed);
    }

    [Fact]
    public void AnEarlyEndStandsUpAtTheFirstLandingWithoutTheBounceOrTheLyingDown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var flight = new KnockdownFlight(Weight, new DownAnimation(assets.LoadAni(AvatarFixture.DownAni)));
        flight.Place(300, 100, 0, false);
        flight.Begin(Request(), 500, 500, true);
        flight.EarlyEnd = true;
        Run(flight, () => !flight.Active);
        Assert.Equal((1, true, 0.0), (flight.BeginCount, flight.TakeEnded(), flight.Z));    // 没有再请求:没颠一下、没躺下
        Assert.True(flight.AniMs < 20080);
    }

    [Fact]
    public void AnEarlyEndWhileLyingStandsUpOnTheNextStep()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var flight = new KnockdownFlight(Weight, new DownAnimation(assets.LoadAni(AvatarFixture.DownAni)));
        flight.Place(300, 100, 0, false);
        flight.Begin(Request(), 500, 500, true);
        Run(flight, () => flight.Stage == 2);
        flight.Update(10, null);
        Assert.True(flight.Active);                                       // 躺着,动画还没放完
        flight.EarlyEnd = true;
        flight.Update(10, null);
        Assert.Equal((false, true), (flight.Active, flight.TakeEnded()));
    }

    [Fact]
    public void ADeadBodyLandsThenDiesInsteadOfLyingDownAndNeverGetsUp()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var anim = new DownAnimation(assets.LoadAni(AvatarFixture.DownAni));
        var flight = new KnockdownFlight(Weight, anim);
        flight.Place(300, 100, 0, false);
        flight.DieOnLying = true;
        flight.EarlyEnd = true;                                           // 已死时提前结束不起作用
        flight.Begin(Request(), 500, 500, true);
        Run(flight, () => !flight.Active);
        Assert.Equal((true, false), (flight.TakeDied(), flight.TakeEnded()));
        Assert.Equal((0.0, 0.0, anim.StartMs(3)), (flight.Z, flight.HorizontalSpeed, flight.AniMs));    // 停在躺着的那一帧
        Assert.False(flight.TakeDied());
    }

    [Fact]
    public void StoppingEitherKeepsOrClearsTheVelocities()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var flight = new KnockdownFlight(Weight, new DownAnimation(assets.LoadAni(AvatarFixture.DownAni)));
        flight.Place(300, 100, 0, false);
        flight.Begin(new KnockdownRequest(0, 1, 200, 300), 500, 500, true);
        flight.Update(50, null);
        Assert.True(flight.Z > 0 && flight.VerticalSpeed > 0 && flight.HorizontalSpeed != 0);

        flight.Stop(keepVelocity: true);                                  // 已死怪物带着速度落地:速度与高度留给 FallWhileDying
        Assert.Equal((false, -1), (flight.Active, flight.Stage));
        Assert.True(flight.Z > 0 && flight.VerticalSpeed != 0);
        flight.FallWhileDying(2000, null);
        Assert.Equal((0.0, 0.0, 0.0), (flight.Z, flight.VerticalSpeed, flight.HorizontalSpeed));

        flight.Place(300, 100, 0, false);
        flight.Begin(Request(), 500, 500, true);
        flight.Update(50, null);
        flight.Stop(keepVelocity: false);                                 // 占位路径:全部清零
        Assert.Equal((0.0, 0.0, 0.0), (flight.Z, flight.VerticalSpeed, flight.HorizontalSpeed));
    }

    [Fact]
    public void WithoutAnAnimationTheFlightStillFliesAndEnds()
    {
        var flight = new KnockdownFlight(Weight, new DownAnimation(null));
        flight.Place(300, 100, 0, false);
        flight.Begin(Request(), 500, 500, false);
        Run(flight, () => !flight.Active);
        Assert.Equal((true, 3), (flight.TakeEnded(), flight.BeginCount));
        Assert.Equal(0.0, flight.AniMs);
    }

    // ------------------------------------------------------------------ 倒地起身(M47,证据 E-140)

    private static KnockdownFlight LyingFlight(DnfAssets assets)
    {
        var flight = new KnockdownFlight(Weight, new DownAnimation(assets.LoadAni(AvatarFixture.DownAni)));
        flight.Place(300, 100, 0, false);
        flight.Begin(Request(), 500, 500, true);
        Run(flight, () => flight.Stage == 2);
        return flight;
    }

    [Fact]
    public void QuickStandIsOnlyPossibleWhileLyingAndNotWhenDeadOrAlreadyStandingUp()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var flight = new KnockdownFlight(Weight, new DownAnimation(assets.LoadAni(AvatarFixture.DownAni)));
        Assert.False(flight.CanQuickStand);                               // 没在倒地里
        Assert.False(flight.BeginQuickStand(100));
        Assert.Equal((false, -1.0), (flight.QuickStanding, flight.QuickStandLeftMs));

        flight.Place(300, 100, 0, false);
        flight.Begin(Request(), 500, 500, true);
        Assert.Equal(0, flight.Stage);
        Assert.False(flight.CanQuickStand);                               // 腾空阶段不行
        Assert.False(flight.BeginQuickStand(100));
        Run(flight, () => flight.Stage == 1);
        Assert.False(flight.CanQuickStand);                               // 颠一下的阶段也不行
        Run(flight, () => flight.Stage == 2);
        Assert.True(flight.CanQuickStand);                                // 躺着才行

        flight.DieOnLying = true;                                         // 已死的不能起身
        Assert.False(flight.CanQuickStand);
        Assert.False(flight.BeginQuickStand(100));
        flight.DieOnLying = false;

        Assert.True(flight.BeginQuickStand(100));
        Assert.Equal((true, true, 100.0), (flight.QuickStanding, flight.Active, flight.QuickStandLeftMs));
        Assert.False(flight.CanQuickStand);                               // 已经在起身里,不能再进一次
        Assert.False(flight.BeginQuickStand(500));
        Assert.Equal(100.0, flight.QuickStandLeftMs);
    }

    [Fact]
    public void QuickStandEndsAfterItsOwnCountdownInsteadOfTheLyingAnimation()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 对照:一直躺着,躺着的那一帧放完(400 毫秒)才起身
        KnockdownFlight normal = LyingFlight(assets);
        normal.Update(150, null);
        Assert.True(normal.Active);

        KnockdownFlight quick = LyingFlight(assets);
        Assert.True(quick.BeginQuickStand(100));
        quick.Update(90, null);
        Assert.Equal((true, true), (quick.Active, quick.QuickStanding));
        Assert.Equal(10.0, quick.QuickStandLeftMs, 6);
        Assert.Equal(2, quick.Stage);                                     // 仍是倒地的躺着阶段:位置不动、不是站立
        Assert.Equal(0.0, quick.Z);
        quick.Update(20, null);
        Assert.Equal((false, false, -1.0), (quick.Active, quick.QuickStanding, quick.QuickStandLeftMs));
        Assert.True(quick.TakeEnded());                                   // 起身通知照常发一次
        Assert.False(quick.TakeEnded());
        Assert.Equal(-1, quick.Stage);
    }

    [Fact]
    public void AZeroDurationQuickStandEndsOnTheNextStepAndEarlyEndDoesNotCutItShort()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        KnockdownFlight zero = LyingFlight(assets);
        Assert.True(zero.BeginQuickStand(0));
        Assert.True(zero.QuickStanding);
        zero.Update(1, null);
        Assert.Equal((false, true), (zero.Active, zero.TakeEnded()));

        // 起身途中提前结束的标志没有用:只看自己的倒计时
        KnockdownFlight flagged = LyingFlight(assets);
        Assert.True(flagged.BeginQuickStand(100));
        flagged.EarlyEnd = true;
        flagged.Update(50, null);
        Assert.True(flagged.Active);
        flagged.Update(60, null);
        Assert.False(flagged.Active);

        // 负时长当 0
        KnockdownFlight negative = LyingFlight(assets);
        Assert.True(negative.BeginQuickStand(-30));
        Assert.Equal(0.0, negative.QuickStandLeftMs);
    }

    [Fact]
    public void ANewRequestAStopOrAResurrectionCancelsTheQuickStand()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        KnockdownFlight again = LyingFlight(assets);
        Assert.True(again.BeginQuickStand(200));
        again.Begin(Request(), 500, 500, true);                           // 起身时又被击倒:重新倒地,起身作废
        Assert.Equal((true, false), (again.Active, again.QuickStanding));

        KnockdownFlight stopped = LyingFlight(assets);
        Assert.True(stopped.BeginQuickStand(200));
        stopped.Stop(keepVelocity: false);
        Assert.Equal((false, false, -1.0), (stopped.Active, stopped.QuickStanding, stopped.QuickStandLeftMs));

        KnockdownFlight revived = LyingFlight(assets);
        Assert.True(revived.BeginQuickStand(200));
        revived.Resurrect();                                              // 活着时复活是空操作(还在倒地里),不动起身倒计时
        Assert.True(revived.QuickStanding);
    }
}
