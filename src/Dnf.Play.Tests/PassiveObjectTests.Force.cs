using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物、主角与被动对象怎么落实 .act 的 [SET POS FORCE](M58,证据 E-152):每帧摆位置,落点能站才整个摆、不能就只改高度;
/// 运行器怎么算落点见 Game 层的测试。出招 .act 是合成的(见 <see cref="AvatarFixture.ActMobPath"/>:force、forceme),
/// 出招动画四帧各 80 毫秒;主角站在 (300, 100) 朝右,怪物站在 (340, 100) 朝左。
/// </summary>
public partial class PassiveObjectTests
{
    [Fact]
    public void AForcedPositionOnAMonsterNeedsAStandableSpotButAlwaysSetsTheHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Plain(assets, 50, 50);
        var area = new RectUnionArea(new List<WorldRect> { new WorldRect(0, 0, 100, 100) });
        m.Update(10, 0, 0, false, area);                       // 记下这一步见到的可走区
        int version = m.ActSelf.MotionVersion;

        m.ActSelf.ForcePosition(80, 60, 25);
        Assert.Equal((80.0, 60.0, 25.0), (m.X, m.Y, m.Z));
        m.ActSelf.ForcePosition(500, 500, 40);                 // 落点不能站:X、Y 留在原处,高度照改(与 [TELEPORT] 不同,不在附近找点)
        Assert.Equal((80.0, 60.0, 40.0), (m.X, m.Y, m.Z));
        m.ActSelf.ForcePosition(10, 10, -5);                   // 高度小于 0 当 0
        Assert.Equal((10.0, 10.0, 0.0), (m.X, m.Y, m.Z));
        Assert.Equal(version, m.ActSelf.MotionVersion);        // 每帧都调:不能算"动画帧变了"
    }

    [Fact]
    public void AMonsterForcedIntoTheAirFallsOrHeadsBackToItsHoverHeightOnceTheForceStops()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor ground = Plain(assets);
        ground.ActSelf.ForcePosition(420, 100, 90);
        Assert.Equal(90.0, ground.Z);
        RunFor(ground, 2000);
        Assert.Equal(0.0, ground.Z);

        MonsterActor hover = Plain(assets);
        hover.SetBaseFloatingHeight(60);
        RunFor(hover, 10);
        hover.ActSelf.ForcePosition(420, 100, 0);
        Assert.Equal(0.0, hover.Z);
        RunFor(hover, 1000);
        Assert.Equal(60.0, hover.Z);
    }

    [Fact]
    public void ADyingOrKnockedDownMonsterIsNotForced()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor down = Plain(assets, 100, 100, AvatarFixture.BlobBoxedDownMobPath);
        down.ApplyHit(1, true, 212, false, default, new KnockdownInput(215, false, false, false, 0, 0));
        Assert.True(down.IsKnockedDown);
        double z = down.Z;
        down.ActSelf.ForcePosition(150, 100, 70);
        Assert.Equal((100.0, z), (down.X, down.Z));

        MonsterActor dead = Plain(assets, 100, 100);
        dead.ApplyHit(100000, false, 0, false);
        Assert.False(dead.IsAlive);
        dead.ActSelf.ForcePosition(150, 100, 70);
        Assert.Equal((100.0, 0.0), (dead.X, dead.Z));
    }

    [Fact]
    public void TheHeroIsForcedOnlyOntoStandablePointsAndItsHeightIsLeftToItsOwnStateMachine()
    {
        using var arena = new ActArena("plain", useVitals: true, frozenHero: true);
        var list = new List<IActEntity>();
        arena.Session.ActHost.Resolve(arena.Monster.ActSelf, ActSelector.AllEnemy, 0, list);
        IActEntity hero = Assert.Single(list);
        TownHero body = arena.Session.Room.Hero;
        hero.ForcePosition(302, 98, 50);                       // 可走区是脚下 10 × 10 的一块:能站就整个摆
        Assert.Equal((302.0, 98.0, 0.0), (body.X, body.Y, body.Z));
        hero.ForcePosition(400, 100, 50);                      // 不能站:不动(高度归主角自己的腾空状态机管,这一版不改)
        Assert.Equal((302.0, 98.0, 0.0), (body.X, body.Y, body.Z));
    }

    [Fact]
    public void APassiveObjectIsForcedWithoutAnyWalkCheckAndReportsItsActionLength()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActForeverId);
        IActEntity entity = o;
        int version = entity.MotionVersion;
        entity.ForcePosition(10, 20, 30);
        Assert.Equal((10.0, 20.0, 30.0), (o.X, o.Y, o.Z));
        entity.ForcePosition(11, 21, -5);                      // 高度小于 0 当 0
        Assert.Equal((11.0, 21.0, 0.0), (o.X, o.Y, o.Z));
        Assert.Equal(version, entity.MotionVersion);
        Assert.True(entity.MotionDurationMs > 0);

        entity.Destroy(true);
        Assert.True(o.Finished);
        entity.ForcePosition(500, 500, 50);                    // 已经结束的对象不再被摆
        Assert.Equal((11.0, 21.0, 0.0), (o.X, o.Y, o.Z));
    }

    [Fact]
    public void AMonsterReportsItsAttackAnimationLengthScaledByItsAttackSpeed()
    {
        using var arena = new ActArena("plain", useVitals: false, attackSpeed: 2000);
        MonsterActor m = arena.Monster;
        Assert.Equal(2.0, m.AttackRate);
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(160, m.ActSelf.MotionDurationMs);         // 出招动画四帧各 80 毫秒共 320;放两倍速,真实时间是 160(与脚本的真实计时对得上)

        using var normal = new ActArena("plain", useVitals: false);
        Assert.True(normal.Until(() => normal.Monster.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(320, normal.Monster.ActSelf.MotionDurationMs);
    }

    [Fact]
    public void AForceScriptDragsTheHeroInFrontOfTheMonsterWhileItAttacks()
    {
        using var arena = new ActArena("force", useVitals: false);
        MonsterActor m = arena.Monster;
        TownHero body = arena.Session.Room.Hero;
        Assert.Equal((340.0, 300.0), (m.X, body.X));
        Assert.True(arena.Until(() => body.X != 300.0) > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(310.0, body.X);                           // 基准取怪物自己、朝向取怪物的(朝左):340 - 30
        Assert.Equal(100.0, body.Y);
        arena.Steps(10);
        Assert.Equal(310.0, body.X);                           // 基准是怪物,不随主角自己的位置累加
        Assert.Equal(340.0, m.X);
    }

    [Fact]
    public void AForceMeScriptPutsTheMonsterBesideTheHeroAndLeavesTheHeroAlone()
    {
        using var arena = new ActArena("forceme", useVitals: false);
        MonsterActor m = arena.Monster;
        TownHero body = arena.Session.Room.Hero;
        Assert.True(arena.Until(() => m.X != 340.0) > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(350.0, m.X);                              // 基准缺省取目标(主角,朝右):300 + 50
        Assert.Equal(100.0, m.Y);
        Assert.Equal((300.0, 100.0), (body.X, body.Y));
    }
}
