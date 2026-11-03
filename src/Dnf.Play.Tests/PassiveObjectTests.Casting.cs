using Dnf.Game.Act;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 脚本读条([CASTING],M69b,证据 E-168):怪物与被动对象的行为里排一条读条,到期在更新的末尾执行指定的行为组;读条随出招(动作)结束而作废。
/// 合成怪物 xcast 一族的出招动画第 0 帧是 5000 毫秒的占位(见 <see cref="AvatarFixture.CastAni"/>),读条到期的行为组 [SET FRAME] 把它跳走——
/// 没有读条这个行为,怪物就要在占位帧上白等 5 秒。主角站在 (300, 100),怪物站在 (340, 100) 朝左;每步 10 毫秒。
/// </summary>
public partial class PassiveObjectTests
{
    [Fact]
    public void AMonsterHoldingThePlaceholderFrameMovesOnWhenItsCastRunsOut()
    {
        using var arena = new ActArena("xcast");
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => arena.Session.CastsArmed > 0, 600) > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(0, m.CurrentFrame);                                  // 占位帧
        Assert.Equal(0, arena.Session.CastsFired);

        int wait = arena.Until(() => m.CurrentFrame == 1, 200);
        Assert.InRange(wait, 39, 41);                                     // 读条 400 毫秒,不是占位帧的 5000 毫秒
        Assert.Equal(1, arena.Session.CastsFired);
        Assert.Equal(MonsterPhase.Attack, m.Phase);

        Assert.True(arena.Until(() => arena.Session.HeroHits > 0, 100) > 0);        // 帧 2 的攻击框:到期后不到半秒
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack, 100) > 0);    // 余下的帧放完,出招结束
        Assert.Equal(1, arena.Session.CastsArmed);
    }

    [Fact]
    public void WithoutACastTheSameAnimationHoldsThePlaceholderFrame()
    {
        using var arena = new ActArena("xcastnone");
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack, 600) > 0);
        arena.Steps(300);                                                 // 3 秒后还停在占位帧上
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(0, m.CurrentFrame);
        Assert.Equal(0, arena.Session.HeroHits);
        Assert.Equal(0, arena.Session.CastsArmed);
    }

    [Fact]
    public void ACastGroupSpawnsItsObjectAtTheDueTimeAndOnlyOnce()
    {
        using var arena = new ActArena("xcastspawn", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => arena.Session.CastsArmed > 0, 600) > 0);
        Assert.Equal(0, arena.Session.PassivesSpawned);

        int wait = arena.Until(() => arena.Session.PassivesSpawned > 0, 200);
        Assert.InRange(wait, 29, 31);                                     // 读条 300 毫秒
        Assert.Equal(1, m.CurrentFrame);                                  // 同一组里的 [SET FRAME] 一起跑

        arena.Steps(60);                                                  // 之后(冷却里)不再放
        Assert.Equal(1, arena.Session.PassivesSpawned);
        Assert.Equal(1, arena.Session.CastsArmed);
        Assert.Equal(1, arena.Session.CastsFired);
    }

    [Fact]
    public void ACastStillPendingWhenTheAttackEndsIsDroppedWithIt()
    {
        using var arena = new ActArena("xcastlate", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => arena.Session.CastsArmed > 0, 600) > 0);      // 帧 1 排上 2000 毫秒
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack, 100) > 0);    // 出招共 320 毫秒,先放完了

        arena.Steps(500);                                                 // 读条原定的时刻早已过去,中间还出了几次招
        Assert.True(arena.Session.CastsArmed >= 2);                       // 每次出招都排一条新的……
        Assert.Equal(0, arena.Session.CastsFired);                        // ……但没有一条等到过期
        Assert.Equal(0, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void ACastThatArmsAnotherCastSpawnsOnlyAfterBothRunOut()
    {
        using var arena = new ActArena("xcastchain", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.CastsArmed > 0, 600) > 0);
        Assert.True(arena.Until(() => arena.Session.CastsFired > 0, 100) > 0);      // 第一条到期(200 毫秒)
        Assert.Equal(2, arena.Session.CastsArmed);                        // 它的行为组里排上了第二条
        Assert.Equal(0, arena.Session.PassivesSpawned);

        int wait = arena.Until(() => arena.Session.PassivesSpawned > 0, 100);
        Assert.InRange(wait, 19, 21);                                     // 第二条也是 200 毫秒
        Assert.Equal(2, arena.Session.CastsFired);
    }

    [Fact]
    public void APassiveObjectCastsToo()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActCastId);
        Advance(o, 190);
        Assert.Empty(rig.Host.Spawns);                                    // 出生排的 200 毫秒读条还没到
        Advance(o, 20);
        Assert.Equal(AvatarFixture.ActForeverId, Assert.Single(rig.Host.Spawns).Index);
        Advance(o, 1000);
        Assert.Single(rig.Host.Spawns);                                   // 只放一次
    }
}
