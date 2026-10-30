using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 条件读取侧在宿主里的落点(M61,证据 E-156、E-157):
/// [TRIGGER CHECK] 逐对象过滤时问"对象"各族动作的触发器开关,怪物从自己的每招开关与各族宿主的开关数组里答;
/// [IS GRABABLE] 问主角能不能被抓(异常状态里没有冰冻与石化);
/// [SAVE TARGET OBJECT] 的槽表挂在怪物与被动对象身上,跨出招、跨运行器保留。怪物与 .act 全是合成的(见 <see cref="AvatarFixture"/>)。
/// </summary>
public partial class PassiveObjectTests
{
    // ------------------------------------------------------------------ [TRIGGER CHECK]:怪物答开关

    [Fact]
    public void AMonsterAnswersAnAttackTriggerCheckFromTheSwitchesTheScriptsFlip()
    {
        using var arena = new ActArena("xoff", useVitals: false);
        MonsterActor m = arena.Monster;
        IActEntity e = m.ActSelf;

        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Attack, 1, 0));    // 第 1 招的触发器 0 出生时开着
        Assert.True(arena.Until(() => m.ActTriggerState(1, 0) == false) > 0);
        Assert.Equal(false, e.GetActionTrigger(ActTriggerPool.Attack, 1, 0));   // 与 ActTriggerState 是同一份开关
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Attack, 0, 0));
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Attack, 5, 0));           // 没有这一招
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Attack, 1, 9));           // 触发器序号越界
    }

    [Theory]
    [InlineData("f_stay", ActTriggerPool.Stay)]
    [InlineData("f_move", ActTriggerPool.Move)]
    [InlineData("f_sit", ActTriggerPool.Sit)]
    [InlineData("f_down", ActTriggerPool.Down)]
    [InlineData("f_procboot", ActTriggerPool.Proc)]
    public void AMonsterAnswersAFamilyTriggerCheckOnlyForTheFamilyThatHasAProgram(string mob, ActTriggerPool owned)
    {
        using var arena = new ActArena(mob, useVitals: false);
        IActEntity e = arena.Monster.ActSelf;
        ActTriggerPool[] pools = { ActTriggerPool.Stay, ActTriggerPool.Move, ActTriggerPool.Sit, ActTriggerPool.Down, ActTriggerPool.Proc };

        foreach (ActTriggerPool pool in pools)
        {
            bool? answer = e.GetActionTrigger(pool, 0, 0);
            if (pool == owned)
            {
                Assert.NotNull(answer);                                         // 这一族装了程序:触发器 0 有开关
            }
            else
            {
                Assert.Null(answer);                                            // 没装程序的族:没有开关数组,对象被剔除
            }
            Assert.Null(e.GetActionTrigger(pool, 0, 7));                        // 序号越界一律没有
        }
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Default, 0, 0));          // [DEFAULT]、[CUSTOM] 还没有宿主
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Own, 0, 0));              // 自己这一族不是逐对象的写法
    }

    [Fact]
    public void AMonsterAnswersAFamilyTriggerCheckWithTheCurrentSwitchValue()
    {
        // 过程动作的触发器 0 出生时关着,站立的 [ON SET ACTION] 把它打开
        using var arena = new ActArena("f_procsw", useVitals: false);
        IActEntity e = arena.Monster.ActSelf;
        Assert.Equal(false, e.GetActionTrigger(ActTriggerPool.Proc, 0, 0));

        Assert.True(arena.Until(() => arena.Monster.FamilyTriggerState(MonsterActFamily.Proc, 0) == true) > 0);
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Proc, 0, 0));
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Stay, 0, 0));      // 站立这一族自己的触发器没动,开着
    }

    // ------------------------------------------------------------------ [IS GRABABLE]:主角

    [Fact]
    public void TheHeroCanBeGrabbedUnlessFrozenOrPetrifiedOrDead()
    {
        using var arena = new ActArena("f_plain", frozenHero: true);
        RoomCombatSession s = arena.Session;
        IActEntity hero = s.HeroEntity;
        IActEntity source = arena.Monster.ActSelf;

        Assert.True(hero.IsGrabbable);                                          // 还没有状态系统:能抓
        s.UseStatuses(ActStatusTable());
        Assert.True(hero.IsGrabbable);

        hero.ApplyStatus(ActiveStatusEntry.Create(ActiveStatusKind.Stun, 1000f, 0, 2000), source);
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Stun));
        Assert.True(hero.IsGrabbable);                                          // 别的异常状态不挡

        hero.ApplyStatus(ActiveStatusEntry.Create(ActiveStatusKind.Freeze, 1000f, 0, 2000), source);
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Freeze));
        Assert.False(hero.IsGrabbable);                                         // 冰冻:抓不了

        using var stone = new ActArena("f_plain", frozenHero: true);
        stone.Session.UseStatuses(ActStatusTable());
        stone.Session.HeroEntity.ApplyStatus(ActiveStatusEntry.Create(ActiveStatusKind.Stone, 1000f, 0, 2000), stone.Monster.ActSelf);
        Assert.True(stone.Session.HeroStatus.Has(ActiveStatusKind.Stone));
        Assert.False(stone.Session.HeroEntity.IsGrabbable);                     // 石化:抓不了

        using var dead = new ActArena("f_plain", frozenHero: true);
        dead.Session.Vitals.Damage(dead.Session.Vitals.Hp);
        Assert.False(dead.Session.HeroEntity.IsAlive);
        Assert.False(dead.Session.HeroEntity.IsGrabbable);                      // 死了:抓不了
    }

    [Fact]
    public void AMonsterIsGrabbableWhileItIsAliveByDefault()
    {
        using var arena = new ActArena("f_plain", useVitals: false);
        IActEntity e = arena.Monster.ActSelf;

        Assert.True(e.IsGrabbable);                                             // 怪物这一版没有"不可抓取"的属性,按活着算
        arena.Monster.ApplyHit(100000, false, 0, false);
        arena.Steps(2);
        Assert.Equal(e.IsAlive, e.IsGrabbable);
    }

    // ------------------------------------------------------------------ [SAVE TARGET OBJECT]:槽表挂在拥有者身上

    [Fact]
    public void AMonsterHoldsItsSavedTargetsForAllItsAttacks()
    {
        using var arena = new ActArena("f_plain", useVitals: false);
        IActEntity e = arena.Monster.ActSelf;
        IActEntity hero = arena.Session.HeroEntity;

        Assert.Null(e.GetSavedTarget(3));
        e.SetSavedTarget(3, hero);
        Assert.Same(hero, e.GetSavedTarget(3));
        Assert.Null(e.GetSavedTarget(4));                                       // 槽之间互不相干
        e.SetSavedTarget(3, e);
        Assert.Same(e, e.GetSavedTarget(3));                                    // 同一格重写覆盖
    }

    [Fact]
    public void ASecondAttackLoadsTheTargetTheFirstAttackSaved()
    {
        using var arena = new ActArena("xsave", useVitals: false);
        MonsterActor m = arena.Monster;

        Assert.Null(m.ActSelf.GetSavedTarget(3));
        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);                 // 第 0 招帧 1:存主角、切到第 1 招
        Assert.Same(arena.Session.HeroEntity, m.ActSelf.GetSavedTarget(3));
        Assert.Equal(0, arena.Session.PassivesSpawned);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);  // 第 1 招帧 1:取槽 3 得到主角,以它为目标放出被动对象
    }

    [Fact]
    public void WithoutTheSaveTheLoadFindsNothingAndTheSecondAttackReleasesNothing()
    {
        using var arena = new ActArena("xsavectl", useVitals: false);
        MonsterActor m = arena.Monster;

        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);
        arena.Steps(150);
        Assert.Null(m.ActSelf.GetSavedTarget(3));
        Assert.Equal(0, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void ADeadHeroIsNotLoadedBackFromTheSlot()
    {
        using var arena = new ActArena("xsave", useVitals: true);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);
        Assert.Same(arena.Session.HeroEntity, m.ActSelf.GetSavedTarget(3));

        arena.Session.Vitals.Damage(arena.Session.Vitals.Hp);                   // 存下之后主角死了:取出时当没有
        int steps = arena.Until(() => m.Phase != MonsterPhase.Attack);
        Assert.InRange(steps, 10, 400);                                         // 第 1 招照常放完(帧 1 在它里面),不是因为没放才没有对象
        Assert.Equal(0, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void APassiveObjectHoldsItsOwnSavedTargets()
    {
        using var arena = new ActArena("xsave", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        IActEntity passive = arena.Session.Passives[0];
        IActEntity hero = arena.Session.HeroEntity;

        Assert.Null(passive.GetSavedTarget(3));                                 // 槽表是各自的:怪物那一份不会串过来
        passive.SetSavedTarget(3, hero);
        Assert.Same(hero, passive.GetSavedTarget(3));
        Assert.Null(passive.GetSavedTarget(0));
        Assert.Same(hero, arena.Monster.ActSelf.GetSavedTarget(3));             // 怪物自己的还在
    }

    [Fact]
    public void TheHeroEntityHasNoSlotTable()
    {
        using var arena = new ActArena("f_plain", useVitals: false);
        IActEntity hero = arena.Session.HeroEntity;

        hero.SetSavedTarget(1, arena.Monster.ActSelf);                          // 主角不是脚本的拥有者:什么都不存
        Assert.Null(hero.GetSavedTarget(1));
    }
}
