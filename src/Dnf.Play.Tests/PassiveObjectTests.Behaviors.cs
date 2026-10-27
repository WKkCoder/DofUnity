using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物 .act 里的受击盒开关、浮空高度、瞬移与震屏(M40,证据 E-133)。出招 .act 是合成的(见 <see cref="AvatarFixture.ActMobPath"/>:dmgoff、dmgkeep、hover、land、tele、shake),
/// 出招动画四帧各 80 毫秒;主角站在 (300, 100),怪物站在 (340, 100) 朝左。重力取与被动对象同值的 1500 像素/秒²。
/// </summary>
public partial class PassiveObjectTests
{
    private static readonly IWalkArea FreeArea = new RectUnionArea(new List<WorldRect>());

    private static void RunFor(MonsterActor m, double ms, IWalkArea walk = null, double step = 10)
    {
        double left = ms;
        while (left > 0)
        {
            double d = left < step ? left : step;
            m.Update(d, 0, 0, false, walk ?? FreeArea);
            left -= d;
        }
    }

    private static MonsterActor Plain(DnfAssets assets, double x = 400, double y = 100, string mob = AvatarFixture.BlobMobPath)
    {
        MonsterModel model = MonsterModel.Read(assets, mob);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, 1000, 222.5, 1500, 0, 0, 10000);
    }

    // ------------------------------------------------------------------ 受击盒

    [Fact]
    public void AScriptCanSwitchTheDamageBoxOffAndOnAgainWhileTheMonsterIsAttacking()
    {
        using var arena = new ActArena("dmgoff", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.False(m.DamageBoxOff);
        Assert.NotEmpty(m.DamageBoxes());
        Assert.True(m.HasDamageBox);

        Assert.True(arena.Until(() => m.DamageBoxOff) > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Empty(m.DamageBoxes());                    // 关着:没有受击框,主角打不到
        Assert.False(m.HasDamageBox);

        Assert.True(arena.Until(() => !m.DamageBoxOff) > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);       // 帧 3 重新打开,还在出招里
        Assert.NotEmpty(m.DamageBoxes());
        Assert.True(m.HasDamageBox);
    }

    [Fact]
    public void ADamageBoxLeftOffIsRestoredWhenTheAttackPhaseEnds()
    {
        using var arena = new ActArena("dmgkeep", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.DamageBoxOff) > 0);
        Assert.Empty(m.DamageBoxes());
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack) > 0);
        Assert.False(m.DamageBoxOff);                     // 脚本忘了开:离开出招阶段就恢复,房间不会因此永远清不掉
        Assert.NotEmpty(m.DamageBoxes());
    }

    // ------------------------------------------------------------------ 浮空

    [Fact]
    public void AHoverScriptLiftsTheMonsterToTheHeightWithTheLaunchSpeedAndKeepsItThere()
    {
        using var arena = new ActArena("hover", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(0.0, m.Z);
        Assert.False(m.IsHovering);
        Assert.True(arena.Until(() => m.Z > 0) > 0);
        Assert.True(m.IsHovering);
        Assert.Equal(60.0, m.HoverHeight);
        double previous = m.Z;
        int steps = arena.Until(() =>
        {
            Assert.True(m.Z >= previous);                 // 上升过程不回头
            previous = m.Z;
            return m.Z >= 60.0;
        });
        Assert.InRange(steps, 18, 32);                    // 初速 √(2 × 1500 × 60) ≈ 424,约 0.25 到 0.28 秒到顶(显式欧拉积分略早)
        Assert.Equal(60.0, m.Z);
        arena.Steps(5);
        Assert.Equal(60.0, m.Z);                          // 出招期间一直悬着
        Assert.True(m.IsHovering);
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack) > 0);
        Assert.False(m.IsHovering);                       // 离开出招阶段:回到基准(没有基准就是地面),开始落下
        Assert.True(arena.Until(() => m.Z == 0.0) > 0);
        arena.Steps(20);
        Assert.Equal(0.0, m.Z);
    }

    [Fact]
    public void ALeftOverScriptHoverGoesBackToTheBaseHeightWhenTheAttackEnds()
    {
        using var arena = new ActArena("hover", useVitals: false);
        MonsterActor m = arena.Monster;
        m.SetBaseFloatingHeight(20);                      // 基准高度 20:出生就悬在 20
        Assert.Equal(20.0, m.Z);
        Assert.True(arena.Until(() => m.Z >= 60.0) > 0);  // 脚本把它升到 60
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack) > 0);
        Assert.True(m.IsHovering);
        Assert.True(arena.Until(() => m.Z == 20.0) > 0);  // 回到基准 20,而不是地面
        arena.Steps(30);
        Assert.Equal(20.0, m.Z);
        Assert.Equal(20.0, m.HoverHeight);
    }

    [Fact]
    public void ZeroOrBelowEndsTheHoverAndTheMonsterFallsUnderGravity()
    {
        using var arena = new ActArena("land", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Z >= 30.0) > 0);
        Assert.True(arena.Until(() => !m.IsHovering) > 0); // 帧 3 取消
        Assert.Equal(30.0, m.Z, 6);
        double previous = m.Z;
        int steps = arena.Until(() =>
        {
            Assert.True(m.Z <= previous);                 // 下落不回头
            previous = m.Z;
            return m.Z == 0.0;
        });
        Assert.InRange(steps, 16, 26);                    // 自由落体 30 像素约 0.2 秒
        Assert.Equal(0.0, m.Z);
        arena.Steps(20);
        Assert.Equal(0.0, m.Z);
        Assert.False(m.IsHovering);
    }

    [Fact]
    public void AMonsterWithABaseFloatingHeightStartsOnItAndAnOrdinaryHitDoesNotDropIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Plain(assets);
        m.SetBaseFloatingHeight(0);                       // 没有高度:什么也不做
        Assert.False(m.IsHovering);
        Assert.Equal(0.0, m.Z);
        m.SetBaseFloatingHeight(50);
        Assert.True(m.IsHovering);
        Assert.Equal(50.0, m.Z);
        RunFor(m, 200);
        Assert.Equal(50.0, m.Z);                          // 悬着不动

        Assert.False(m.ApplyHit(1, false, 0, false));     // 普通受击(不倒地)
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Assert.Equal(50.0, m.Z);                          // 挨一下不落地
        RunFor(m, 500);
        Assert.Equal(50.0, m.Z);
        Assert.True(m.IsHovering);
    }

    [Fact]
    public void AHoveringMonsterKnockedDownFliesFromItsHeightAndStopsHovering()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Plain(assets, mob: AvatarFixture.BlobBoxedDownMobPath);
        m.SetBaseFloatingHeight(50);
        Assert.False(m.ApplyHit(1, true, 212, false, default, new KnockdownInput(215, false, false, false, 0, 0)));
        Assert.True(m.IsKnockedDown);
        Assert.False(m.IsHovering);                       // 被打得倒地:悬停结束
        Assert.True(m.Z >= 50.0);                         // 从悬停高度起飞(起飞的抬升再叠上去)
        bool landed = false;
        for (int i = 0; i < 500 && m.IsKnockedDown; i++)
        {
            RunFor(m, 10);
            landed |= m.Z == 0.0;
        }
        Assert.False(m.IsKnockedDown);
        Assert.True(landed);                              // 躺在地上
        RunFor(m, 2000);
        Assert.True(m.IsHovering);                        // 起身之后回到基准悬停高度
        Assert.Equal(50.0, m.Z);
    }

    [Fact]
    public void EndingTheHoverDirectlyDropsTheMonsterAndHoveringWinsOverAScriptJump()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Plain(assets);
        m.ActSelf.SetFloatingHeight(40);
        RunFor(m, 1000);
        Assert.Equal(40.0, m.Z);
        m.ActSelf.SetFloatingHeight(-5);                  // 负数同样结束悬停
        Assert.False(m.IsHovering);
        Assert.Equal(40.0, m.Z);
        RunFor(m, 1000);
        Assert.Equal(0.0, m.Z);

        // 升到一个更低的高度:从高处落下来停住
        m.ActSelf.SetFloatingHeight(80);
        RunFor(m, 1000);
        Assert.Equal(80.0, m.Z);
        m.ActSelf.SetFloatingHeight(30);
        RunFor(m, 1000);
        Assert.Equal(30.0, m.Z);
        Assert.True(m.IsHovering);
    }

    // ------------------------------------------------------------------ 瞬移

    [Fact]
    public void ATeleportScriptPutsTheMonsterInFrontOfItselfAndAMidairLandingFalls()
    {
        using var arena = new ActArena("tele", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(340.0, m.X);
        Assert.True(arena.Until(() => m.Z > 0) > 0);
        Assert.Equal(290.0, m.X);                         // 朝左:X 偏移 50 取反
        Assert.Equal(100.0, m.Y);
        Assert.True(m.Z <= 30.0 && m.Z > 25.0);           // 落在偏移 30 的半空,已经掉了一步
        double previous = m.Z;
        int steps = arena.Until(() =>
        {
            Assert.True(m.Z <= previous);
            previous = m.Z;
            return m.Z == 0.0;
        });
        Assert.InRange(steps, 14, 26);                    // 自由落体 30 像素约 0.2 秒
        Assert.False(m.IsHovering);
    }

    [Fact]
    public void ATeleportOntoABlockedPointFindsTheNearestStandableOne()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Plain(assets, 50, 50);
        var area = new RectUnionArea(new List<WorldRect> { new WorldRect(0, 0, 100, 100) });
        m.Update(10, 0, 0, false, area);                  // 记下这一步见到的可走区
        Assert.True(m.ActSelf.Teleport(130, 50, 0));
        Assert.Equal(100.0, m.X);                         // 区里离目标最近的格点
        Assert.Equal(50.0, m.Y);

        Assert.True(m.ActSelf.Teleport(20, 30, 0));       // 能站:原样
        Assert.Equal((20.0, 30.0), (m.X, m.Y));

        Assert.False(m.ActSelf.Teleport(1000, 1000, 0));  // 200 像素内没有能站的点:不动
        Assert.Equal((20.0, 30.0), (m.X, m.Y));
    }

    [Fact]
    public void ADyingOrKnockedDownMonsterIsNotTeleported()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor down = Plain(assets, 100, 100, AvatarFixture.BlobBoxedDownMobPath);
        down.ApplyHit(1, true, 212, false, default, new KnockdownInput(215, false, false, false, 0, 0));
        Assert.True(down.IsKnockedDown);
        Assert.False(down.ActSelf.Teleport(150, 100, 0));

        MonsterActor dead = Plain(assets, 100, 100);
        dead.ApplyHit(100000, false, 0, false);
        Assert.False(dead.IsAlive);
        Assert.False(dead.ActSelf.Teleport(150, 100, 0));
    }

    [Fact]
    public void ATeleportedHoveringMonsterHeadsBackToItsHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Plain(assets);
        m.SetBaseFloatingHeight(60);
        RunFor(m, 10);
        Assert.True(m.ActSelf.Teleport(420, 100, 0));     // 瞬移到地面
        Assert.Equal(0.0, m.Z);
        RunFor(m, 1000);
        Assert.Equal(60.0, m.Z);                          // 重新升回悬停高度
        Assert.True(m.ActSelf.Teleport(420, 100, 200));   // 瞬移到比悬停高度更高的地方:落回悬停高度
        RunFor(m, 2000);
        Assert.Equal(60.0, m.Z);
    }

    [Fact]
    public void TheHeroIsTeleportedOnlyOntoStandablePointsAndKeepsItsAction()
    {
        using var arena = new ActArena("plain", useVitals: true, frozenHero: true);
        var list = new List<IActEntity>();
        arena.Session.ActHost.Resolve(arena.Monster.ActSelf, ActSelector.AllEnemy, 0, list);
        IActEntity hero = Assert.Single(list);
        TownHero body = arena.Session.Room.Hero;
        Assert.True(hero.Teleport(400, 100, 0));          // 可走区只有脚下一小块:找 200 像素内最近的格点
        Assert.Equal((300.0, 100.0), (body.X, body.Y));
        Assert.False(hero.Teleport(900, 900, 0));         // 够不着:不动
        Assert.Equal((300.0, 100.0), (body.X, body.Y));
    }

    [Fact]
    public void APassiveObjectIsTeleportedWithoutAnyWalkCheck()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActForeverId);
        Assert.True(((IActEntity)o).Teleport(10, 20, 30));
        Assert.Equal((10.0, 20.0, 30.0), (o.X, o.Y, o.Z));
        ((IActEntity)o).SetDamageBox(ActDamageBoxMode.Off);       // 被动对象没有受击盒、没有悬停:都是空操作
        ((IActEntity)o).SetFloatingHeight(100);
        Assert.Equal((10.0, 20.0, 30.0), (o.X, o.Y, o.Z));
    }

    // ------------------------------------------------------------------ 震屏

    [Fact]
    public void AShakeScriptShakesTheRoomForItsDurationAndThenSettlesToZero()
    {
        using var arena = new ActArena("shake", useVitals: false);
        RoomSession room = arena.Session.Room;
        Assert.False(arena.Session.Shake.Active);
        Assert.Equal((0, 0), (room.ShakeX, room.ShakeY));
        Assert.True(arena.Until(() => arena.Session.Shake.Active) > 0);
        Assert.Equal(6, arena.Session.Shake.Strength);

        bool moved = false;
        int steps = arena.Until(() =>
        {
            Assert.InRange(room.ShakeX, -6, 6);
            Assert.InRange(room.ShakeY, -6, 6);
            moved |= room.ShakeX != 0 || room.ShakeY != 0;
            return !arena.Session.Shake.Active;
        });
        Assert.InRange(steps, 17, 22);                    // 200 毫秒
        Assert.True(moved);
        Assert.Equal((0, 0), (room.ShakeX, room.ShakeY));
        arena.Steps(10);
        Assert.Equal((0, 0), (room.ShakeX, room.ShakeY));
    }

    [Fact]
    public void ShakingMovesTheWorldProjectionButNotTheNoShakeOne()
    {
        using var arena = new ActArena("plain", useVitals: false);
        RoomSession room = arena.Session.Room;
        TownProjection still = room.Projection;
        room.ShakeX = 5;
        room.ShakeY = -3;
        TownProjection shaken = room.Projection;
        Assert.Equal(still.CameraX - 5, shaken.CameraX);
        Assert.Equal(still.GroundY - 3, shaken.GroundY);
        room.ShakeX = 0;
        room.ShakeY = 0;
        Assert.Equal((still.CameraX, still.GroundY), (room.Projection.CameraX, room.Projection.GroundY));
    }
}
