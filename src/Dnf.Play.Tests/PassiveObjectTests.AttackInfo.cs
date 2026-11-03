using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 攻击信息的换用(M69,证据 E-167):出招脚本与被动对象脚本的 [CHANGE ATTACKINFO](客户端行为码 0x67),以及程序一级的 [DEFAULT ATTACKINFO]。
/// 怪物:换上的优先,其次是这一招 .act 自带的默认,再其次是 .mob 表里的;下一次出招开始清掉换上的。
/// 被动对象:换上的 > 当前动作 .act 的默认 > .obj 的 [attack info];进入自带默认的动作时默认盖掉换上的。
/// 伤害数用合成常量:怪物攻击力 40、主角防御率 0.5,所以加成 b% 的一击打 40 × (100 + b) / 100 × 0.5。脚本与 .atk 全是合成的。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>.mob 表里的攻击信息(合成):加成 25。</summary>
    private const int MobListBonus = 25;

    private static int Damage(int bonusPercent) => 40 * (100 + bonusPercent) / 100 / 2;

    // ------------------------------------------------------------------ 怪物

    [Fact]
    public void AMonstersAttackScriptSwapsItsAttackInfoAtTheScriptedFramesAndTheHitUsesTheCurrentOne()
    {
        using var arena = new ActArena("xatkchg");
        MonsterActor m = arena.Monster;
        m.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false) });
        Assert.Equal(MobListBonus, m.Strike.DamageBonus);                         // 出招前:.mob 表里的

        Assert.True(arena.Until(() => m.Strike.DamageBonus != MobListBonus, 600) > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(AvatarFixture.ChgBonusA, m.Strike.DamageBonus);              // 出招开始([ON SET ACTION])换成 A
        Assert.Equal(0, m.CurrentFrame);

        Assert.True(arena.Until(() => m.Strike.DamageBonus != AvatarFixture.ChgBonusA) > 0);
        Assert.Equal(AvatarFixture.ChgBonusB, m.Strike.DamageBonus);              // 帧 1 换成 B
        Assert.Equal(1, m.CurrentFrame);
        Assert.Equal(0, arena.Session.HeroHits);                                  // 攻击框在帧 2

        Assert.True(arena.Until(() => arena.Session.HeroHits > 0) > 0);
        Assert.Equal(2, m.CurrentFrame);
        Assert.Equal(Damage(AvatarFixture.ChgBonusB), arena.Session.HeroDamageTaken);   // 打在主角身上的是当时的 B

        Assert.True(arena.Until(() => m.Strike.DamageBonus != AvatarFixture.ChgBonusB) > 0);
        Assert.Equal(MobListBonus, m.Strike.DamageBonus);                         // 帧 3 [DEFAULT]:这一招没有程序默认,回到 .mob 表里的
        Assert.Equal(3, m.CurrentFrame);
        Assert.Equal(3, arena.Session.AttackInfosChanged);
        Assert.Equal(0, arena.Session.AttackInfosMissing);
    }

    [Fact]
    public void WithoutAnyChangeTheMonstersHitUsesTheMobListsInfo()
    {
        using var arena = new ActArena("plain");
        arena.Monster.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false) });
        Assert.True(arena.Until(() => arena.Session.HeroHits > 0, 900) > 0);
        Assert.Equal(Damage(MobListBonus), arena.Session.HeroDamageTaken);
        Assert.Equal(0, arena.Session.AttackInfosChanged);
    }

    [Fact]
    public void AnAttackInfoFileThatCannotBeReadChangesNothingAndIsCounted()
    {
        using var arena = new ActArena("xatkmissing");
        MonsterActor m = arena.Monster;
        m.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false) });
        Assert.True(arena.Until(() => arena.Session.AttackInfosMissing > 0, 600) > 0);
        Assert.Equal(MobListBonus, m.Strike.DamageBonus);                         // 客户端:找不到那一项就什么都不做
        Assert.Equal(1, arena.Session.AttackInfosMissing);
        Assert.Equal(0, arena.Session.AttackInfosChanged);
        Assert.True(arena.Until(() => arena.Session.HeroHits > 0) > 0);
        Assert.Equal(Damage(MobListBonus), arena.Session.HeroDamageTaken);
    }

    [Fact]
    public void TheProgramsDefaultAttackInfoOverridesTheMobListAndDefaultRestoresItNotTheMobLists()
    {
        using var arena = new ActArena("xatkdef");
        MonsterActor m = arena.Monster;
        m.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false) });
        Assert.Equal(AvatarFixture.ChgBonusA, m.Strike.DamageBonus);              // 出招 .act 自带的默认 A 盖过 .mob 表里的(推断:默认在动作开始时套上)

        Assert.True(arena.Until(() => m.Strike.DamageBonus != AvatarFixture.ChgBonusA, 600) > 0);
        Assert.Equal(AvatarFixture.ChgBonusB, m.Strike.DamageBonus);              // 帧 1
        Assert.Equal(1, m.CurrentFrame);
        Assert.True(arena.Until(() => m.Strike.DamageBonus != AvatarFixture.ChgBonusB) > 0);
        Assert.Equal(AvatarFixture.ChgBonusA, m.Strike.DamageBonus);              // 帧 3 [DEFAULT]:回到这一招的默认
        Assert.Equal(3, m.CurrentFrame);
        Assert.Equal(Damage(AvatarFixture.ChgBonusB), arena.Session.HeroDamageTaken);    // 帧 2 的一击用的是 B
    }

    [Fact]
    public void AProgramWithOnlyADefaultAttackInfoAndNoTriggersStillAppliesItToTheHit()
    {
        using var arena = new ActArena("xatkdefonly");
        MonsterActor m = arena.Monster;
        m.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false) });
        Assert.False(m.HasActs);                                                  // 没有触发器:不装运行器
        Assert.Equal(AvatarFixture.ChgBonusA, m.Strike.DamageBonus);
        Assert.True(arena.Until(() => arena.Session.HeroHits > 0, 900) > 0);
        Assert.Equal(Damage(AvatarFixture.ChgBonusA), arena.Session.HeroDamageTaken);
    }

    [Fact]
    public void DetachingTheActLibraryAlsoDropsTheProgramDefaults()
    {
        using var arena = new ActArena("xatkdefonly");
        MonsterActor m = arena.Monster;
        m.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false) });
        Assert.Equal(AvatarFixture.ChgBonusA, m.Strike.DamageBonus);
        arena.Session.AttachActs(null);
        Assert.Equal(MobListBonus, m.Strike.DamageBonus);                         // 撤掉程序库:只剩 .mob 表里的
        arena.Session.AttachActs(new ActLibrary(arena.Assets, DataOf(arena.Assets)));
        Assert.Equal(AvatarFixture.ChgBonusA, m.Strike.DamageBonus);              // 再接上又回来
    }

    [Fact]
    public void ANewSwingStartsFromItsOwnAttackInfoNotFromTheLastSwingsChange()
    {
        using var arena = new ActArena("xatkreset");
        MonsterActor m = arena.Monster;
        m.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false), new HeroStrikeInfo(40, 0, false) });
        Assert.True(arena.Until(() => m.Strike.DamageBonus == AvatarFixture.ChgBonusB, 600) > 0);   // 第 0 招出招开始换成 B
        Assert.Equal(0, m.AttackIndex);
        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);                  // 帧 1 用 [END] 切第 1 招,播完接上
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(40, m.Strike.DamageBonus);                                   // 第 1 招自己的:上一招换上的 B 不带过来
        Assert.Equal(1, arena.Session.AttackInfosChanged);
    }

    [Fact]
    public void TheProgramDefaultBelongsToItsOwnAttackIndex()
    {
        using var arena = new ActArena("xatkdefsecond");
        MonsterActor m = arena.Monster;
        m.SetStrikes(new[] { new HeroStrikeInfo(MobListBonus, 0, false), new HeroStrikeInfo(40, 0, false) });
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack, 600) > 0);
        Assert.Equal(0, m.AttackIndex);
        Assert.Equal(MobListBonus, m.Strike.DamageBonus);                         // 第 0 招没有默认:.mob 表里的
        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);                  // 帧 1 用 [END] 切第 1 招
        Assert.Equal(AvatarFixture.ChgBonusA, m.Strike.DamageBonus);              // 第 1 招自己的默认,盖过表里的 40
    }

    // ------------------------------------------------------------------ 被动对象

    [Fact]
    public void AnObjectThatChangesItsAttackInfoAtBirthHasTheNewOneAtOnce()
    {
        using var arena = new ActArena("spawn9225", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        PassiveObject o = Assert.Single(arena.Session.Passives);
        Assert.Equal(AvatarFixture.ShotDamageBonus, o.Template.Strike.DamageBonus);     // .obj 的 [attack info] 是 shot
        Assert.Equal(AvatarFixture.ChgBonusB, o.Strike.DamageBonus);              // 出生([ON SET ACTION])换成 B
        Assert.Equal(1, arena.Session.AttackInfosChanged);
        Assert.Equal(MonsterPhase.Attack, arena.Monster.Phase);
        Assert.NotEqual(AvatarFixture.ChgBonusB, arena.Monster.Strike.DamageBonus);   // 换的是对象自己的,不是放出它的怪物的
    }

    [Fact]
    public void AnObjectThatChangesItsAttackInfoAtBirthHitsWithTheNewOne()
    {
        using var arena = new ActArena("spawn9225", frozenHero: true);
        Assert.True(arena.Until(() => arena.Session.PassiveHits > 0) > 0);
        Assert.Equal(Damage(AvatarFixture.ChgBonusB), arena.Session.HeroDamageTaken);   // 不是 .obj 的 shot(25)
        Assert.Equal(1, arena.Session.AttackInfosChanged);
    }

    [Fact]
    public void AnObjectWhoseOnlyAttackInfoIsTheProgramDefaultHasItFromBirth()
    {
        using var arena = new ActArena("spawn9226", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        PassiveObject o = Assert.Single(arena.Session.Passives);
        Assert.Equal(0, o.Template.Strike.DamageBonus);                           // .obj 没有 [attack info]:全默认值
        Assert.Equal(AvatarFixture.ChgBonusA, o.Strike.DamageBonus);
        Assert.Equal(0, arena.Session.AttackInfosChanged);                        // 默认不是"换"
    }

    [Fact]
    public void AnObjectWhoseOnlyAttackInfoIsTheProgramDefaultHitsWithIt()
    {
        using var arena = new ActArena("spawn9226", frozenHero: true);
        Assert.True(arena.Until(() => arena.Session.PassiveHits > 0) > 0);
        Assert.Equal(Damage(AvatarFixture.ChgBonusA), arena.Session.HeroDamageTaken);
    }

    [Fact]
    public void TheProgramDefaultOverridesTheObjFilesInfoAndChangesComeAndGoOverTime()
    {
        using var arena = new ActArena("spawn9227", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        PassiveObject o = Assert.Single(arena.Session.Passives);
        Assert.Equal(AvatarFixture.ShotDamageBonus, o.Template.Strike.DamageBonus);
        Assert.Equal(0, o.CurrentFrame);
        Assert.Equal(AvatarFixture.ChgBonusA, o.Strike.DamageBonus);              // 程序默认 A 盖过 .obj 的 shot

        Assert.True(arena.Until(() => o.CurrentFrame >= 1) > 0);
        Assert.Equal(AvatarFixture.ChgBonusB, o.Strike.DamageBonus);              // 帧 1 换成 B
        Assert.True(arena.Until(() => o.CurrentFrame >= 2) > 0);
        Assert.Equal(AvatarFixture.ChgBonusA, o.Strike.DamageBonus);              // 帧 2 [DEFAULT]:回到这个动作的默认(不是 .obj 的)
        Assert.Equal(2, arena.Session.AttackInfosChanged);
    }

    [Fact]
    public void AChangedAttackInfoFollowsTheObjectIntoAnActionWithoutADefaultButAnActionDefaultReplacesIt()
    {
        using var arena = new ActArena("spawn9228", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        PassiveObject o = Assert.Single(arena.Session.Passives);
        Assert.Equal(AvatarFixture.ChgBonusB, o.Strike.DamageBonus);              // 出生换成 B
        Assert.Same(o.Template.Basic, o.Action);

        Assert.True(arena.Until(() => ReferenceEquals(o.Action, o.Template.Etc(0))) > 0);
        Assert.Equal(AvatarFixture.ChgBonusB, o.Strike.DamageBonus);              // 自定义动作 0 没有程序默认:换上的跟着走
        Assert.True(arena.Until(() => ReferenceEquals(o.Action, o.Template.Etc(1))) > 0);
        Assert.Equal(AvatarFixture.ChgBonusA, o.Strike.DamageBonus);              // 自定义动作 1 自带默认 A:进入时盖掉换上的
        Assert.Equal(1, arena.Session.AttackInfosChanged);
    }

    // ------------------------------------------------------------------ 程序库

    [Fact]
    public void TheLibraryReadsAndCachesAttackInfosAndReportsUnreadableOnesAsNull()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var library = new ActLibrary(assets, DataOf(assets));

        HeroStrikeInfo? a = library.Strike(AvatarFixture.ChgAtkA);
        Assert.Equal(AvatarFixture.ChgBonusA, a.Value.DamageBonus);
        Assert.Equal(AvatarFixture.ChgBonusB, library.Strike(AvatarFixture.ChgAtkB).Value.DamageBonus);
        Assert.Equal(a.Value.DamageBonus, library.Strike(AvatarFixture.ChgAtkA.ToUpperInvariant()).Value.DamageBonus);   // 路径大小写不敏感
        Assert.Null(library.Strike("monster/blob/attackinfo/none.atk"));
        Assert.Null(library.Strike("monster/blob/attackinfo/none.atk"));          // 读不到也缓存
        Assert.Null(library.Strike(null));
        Assert.Null(library.Strike(""));
    }

    [Fact]
    public void TheLibraryResolvesAProgramsDefaultRelativeToTheProgram()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var library = new ActLibrary(assets, DataOf(assets));

        ActProgram withDefault = library.Program(AvatarFixture.BlobActionDir + "/actatkdef.act");
        Assert.Equal(AvatarFixture.ChgBonusA, library.DefaultStrike(withDefault).Value.DamageBonus);
        Assert.Null(library.DefaultStrike(library.Program(AvatarFixture.BlobActionDir + "/actatkchg.act")));     // 没写默认
        Assert.Null(library.DefaultStrike(null));
        Assert.Null(library.DefaultStrike(ActProgram.Empty));

        // 对象的动作带着它的默认;换文件的动作也一样
        PassiveAction action = library.Action(AvatarFixture.ActObjectDir + "/action/actatkdef.act", null);
        Assert.Equal(AvatarFixture.ChgBonusA, action.DefaultStrike.Value.DamageBonus);
        Assert.Null(library.Action(AvatarFixture.ActObjectDir + "/action/actatkchg.act", null).DefaultStrike);
        Assert.Equal(AvatarFixture.ChgBonusA, library.SwapAction(AvatarFixture.BlobActionDir + "/actatkdef.act").DefaultStrike.Value.DamageBonus);
    }
}
