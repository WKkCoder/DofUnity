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
}
