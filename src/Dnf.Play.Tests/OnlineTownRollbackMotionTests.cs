using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网城镇切区被拒、被禁、超时、映射不上时,<see cref="OnlineTownWorld"/> 把主角放回安全位置:
/// 除了位置、朝向与"站立"动作,<b>动作进度(毫秒)也要归零</b>(<see cref="TownHero.MotionMs"/>)。
/// 既有的回退用例只看位置、朝向与动作种类;这里先把主角摆成"行走、进度 700 毫秒"的状态,再触发回退,断言进度回到 0。
/// 合成图册、假传输层与真主角都用既有的测试夹具(<c>TownWorldRig</c>);时间全部由 <c>Poll(nowMs)</c> 喂入,没有任何真实等待。
/// </summary>
public sealed class OnlineTownRollbackMotionTests
{
    // 合成图册(自己的一份,与别的测试类互不相干):城镇 1 的区 0 是网关区,区 1、2、3 是普通区;区 1 → 区 2 的出口矩形是 (610,215,70,36)。
    private static TownAtlas WorldAtlas() => TownKit.Atlas(
        (1, new[]
        {
            new KitArea(0, "[gate]", new[] { 233, 177 }, new[] { 0, 100, 800, 300 }, new[] { new KitExit(402, 118, 38, 74, 1, 2) }),
            new KitArea(1, "[normal]", null, new[] { 0, 100, 800, 300 }, new[] { new KitExit(610, 215, 70, 36, 1, 2) }),
            new KitArea(2, "[normal]", null, new[] { 100, 120, 600, 200 },
                new[] { new KitExit(110, 150, 40, 40, 1, 1), new KitExit(640, 150, 40, 40, 1, 0) }),
            new KitArea(3, "[normal]", null, new[] { 50, 150, 500, 100 }, new[] { new KitExit(60, 160, 30, 30, 1, 2) }),
        }));

    private static TownExit Exit12 => TownKit.Exit(610, 215, 70, 36, 1, 2);

    /// <summary>主角在区域 1 里的场景:真的 <see cref="TownHero"/>,会话是假的出口来源。</summary>
    private sealed class Scene : IDisposable
    {
        public Scene()
        {
            Rig = new TownWorldRig(WorldAtlas(), new TownPlace(1, 1), heroes: true);
            Session = (TownKitSession)Rig.World.Create(new TownPlace(1, 1));
            Hero = Session.Hero;
        }

        public TownWorldRig Rig { get; }

        public TownKitSession Session { get; }

        public TownHero Hero { get; }

        /// <summary>
        /// 主角先在 (300,260) 朝左站定、宿主记下这个安全位置;然后主角带着"行走、进度 700 毫秒"走进出口矩形 (640,230) 并锁存了待触发出口
        /// (此后宿主再记录也不会更新安全位置)。
        /// </summary>
        public void WalkIntoTheExitFromTheSafeSpot()
        {
            Hero.Place(300, 260, true, HeroMotion.Rest, 0);
            Rig.World.RecordSafePosition();
            Hero.Place(640, 230, false, HeroMotion.Move, 700);
            Session.PendingExit = Exit12;
            Rig.World.RecordSafePosition();
        }

        public void Dispose() => Rig.Dispose();
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("forbidden")]
    [InlineData("timeout")]
    [InlineData("unmapped")]
    public void Putting_the_hero_back_zeroes_the_motion_progress_and_rests_at_the_recorded_safe_spot(string ending)
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.WalkIntoTheExitFromTheSafeSpot();
        // 前提:主角是"行走、进度 700 毫秒",站在出口里
        Assert.Equal(HeroMotion.Move, s.Hero.Motion);
        Assert.Equal(700L, s.Hero.MotionMs);
        Assert.Equal((640.0, 230.0), (s.Hero.X, s.Hero.Y));

        switch (ending)
        {
            case "rejected":
                Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
                s.Rig.Transport.Push(KitEvents.Error(0x11));
                Assert.Equal(AreaChangeStatus.RolledBack, s.Rig.World.Poll(1016).Status);
                break;
            case "forbidden":
                Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
                s.Rig.Transport.Push(KitEvents.Error(0x44, 9));
                Assert.Equal(AreaChangeStatus.Forbidden, s.Rig.World.Poll(1016).Status);
                break;
            case "timeout":
                Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
                Assert.Equal(AreaChangeStatus.None, s.Rig.World.Poll(5999).Status);
                Assert.Equal(AreaChangeStatus.Timeout, s.Rig.World.Poll(6000).Status);
                break;
            default:
                s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);
                Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
                break;
        }

        Assert.Equal(0L, s.Hero.MotionMs);
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
        Assert.Equal((300.0, 260.0), (s.Hero.X, s.Hero.Y));
        Assert.True(s.Hero.FacingLeft);
        Assert.Null(s.Session.PendingExit);
    }
}
