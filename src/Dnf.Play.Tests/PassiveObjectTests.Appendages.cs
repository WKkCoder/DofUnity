using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 属性改变附加物落到怪物与主角身上(M63,证据 E-161):怪物的生命上限、走路速度、出招速率、防御面板(含绝对防御)与攻击力每次附加物集合变化时
/// 从基础数值块重算,到期与删除后原样回到出生值;主角只有移动速度与攻击速度两处能落,按相对倍率乘在速度系数与攻击动画倍率上。
/// 怪物与 .act 全是合成的(<see cref="Dnf.Render.Tests.AvatarFixture.ActMobPath"/>:appendme、appendhero、appenddel)。数是测试用的。
/// </summary>
public partial class PassiveObjectTests
{
    private static StatChange Pct(int type, int value) => new StatChange(type, true, value);

    private static StatChange Pts(int type, int value) => new StatChange(type, false, value);

    // ------------------------------------------------------------------ 脚本 → 怪物自己

    [Fact]
    public void AnAttackScriptAppendsStatChangesToItselfAndTheMonsterRecomputesItsNumbers()
    {
        using var arena = new ActArena("appendme", useVitals: false);
        MonsterActor m = arena.Monster;
        double speed = m.SpeedX;
        double speedY = m.SpeedY;
        Assert.Equal((100, 100), (m.HpMax, m.Hp));

        Assert.True(arena.Until(() => m.StatAppendagesAdded > 0) > 0);
        Assert.Equal(2, m.StatAppendagesAdded);                       // 一项一个附加物
        Assert.Equal(2, m.StatAppendages.Count);
        Assert.Equal(speed * 0.5, m.SpeedX, 6);                       // 移动速度 -50%
        Assert.Equal(speedY * 0.5, m.SpeedY, 6);
        Assert.Equal(200, m.HpMax);                                   // 生命上限 +100%
        Assert.Equal(100, m.Hp);                                      // 当前生命不动
        Assert.Equal((0, 0, 0), (m.StatAppendagesReplaced, m.StatAppendagesRemoved, m.StatAppendagesExpired));
        Assert.Equal(1, m.StatAppendageTypeCounts[StatChangeTypes.MoveSpeed]);
        Assert.Equal(1, m.StatAppendageTypeCounts[StatChangeTypes.HpMax]);

        arena.Steps(100);                                             // 没有时限:一直在
        Assert.Equal(2, m.StatAppendages.Count);
        Assert.Equal(200, m.HpMax);
    }

    [Fact]
    public void TheSameScriptHangingTheSameChangesAgainReplacesInsteadOfStacking()
    {
        using var arena = new ActArena("appendme", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.StatAppendagesAdded > 0) > 0);
        double slowed = m.SpeedX;
        Assert.True(arena.Until(() => m.StatAppendagesAdded >= 4, 900) > 0);      // 冷却后第二次出招又挂同样两项
        Assert.Equal(2, m.StatAppendages.Count);                                   // 顶替,不叠加
        Assert.Equal(2, m.StatAppendagesReplaced);
        Assert.Equal(slowed, m.SpeedX);
        Assert.Equal(200, m.HpMax);
    }

    // ------------------------------------------------------------------ 脚本 → 主角

    [Fact]
    public void AnAttackScriptsAppendageSlowsTheHeroAndHisAttackAnimationThenExpires()
    {
        using var arena = new ActArena("appendhero", frozenHero: true);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        double fx = hero.SpeedFactorX;
        double fy = hero.SpeedFactorY;
        double ar = hero.AttackAnimationRate;

        Assert.True(arena.Until(() => s.HeroStatAppendagesAdded > 0) > 0);
        Assert.Equal(2, s.HeroStatAppendagesAdded);
        Assert.Equal(2, s.HeroStatAppendages.Count);
        Assert.Equal(fx * 0.5, hero.SpeedFactorX, 9);
        Assert.Equal(fy * 0.5, hero.SpeedFactorY, 9);
        Assert.Equal(ar * 0.5, hero.AttackAnimationRate, 9);
        Assert.Equal(1, s.HeroStatTypeCounts[StatChangeTypes.MoveSpeed]);
        Assert.Equal(1, s.HeroStatTypeCounts[StatChangeTypes.AttackSpeed]);
        Assert.Equal(0, s.MonsterStatAppendagesAdded);               // 怪物自己没挂

        Assert.True(arena.Until(() => s.HeroStatAppendagesExpired == 2) > 0);       // 500 毫秒后到期
        Assert.Equal(0, s.HeroStatAppendages.Count);
        Assert.Equal(fx, hero.SpeedFactorX, 9);
        Assert.Equal(fy, hero.SpeedFactorY, 9);
        Assert.Equal(ar, hero.AttackAnimationRate, 9);
    }

    [Fact]
    public void DeleteAppendageTakesTheOwnersChangeOffTheHero()
    {
        using var arena = new ActArena("appenddel", frozenHero: true);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        double fx = hero.SpeedFactorX;

        Assert.True(arena.Until(() => s.HeroStatAppendagesAdded > 0) > 0);
        Assert.Equal(fx * 0.5, hero.SpeedFactorX, 9);
        Assert.Equal(1, s.HeroStatAppendages.Count);

        int steps = arena.Until(() => s.HeroStatAppendagesRemoved > 0);
        Assert.InRange(steps, 10, 20);                                // 帧 3 从 240 毫秒起,帧 1 在 80 毫秒
        Assert.Equal(0, s.HeroStatAppendages.Count);
        Assert.Equal(fx, hero.SpeedFactorX, 9);
    }

    [Fact]
    public void HeroAppendagesAreIgnoredWithoutALifeBarAndClearedWhenTheHeroDies()
    {
        using (var noVitals = new ActArena("plain", monsterX: 900, useVitals: false))
        {
            RoomCombatSession s = noVitals.Session;
            double fx = s.Hero.SpeedFactorX;
            s.AddHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -50), 0, noVitals.Monster.ActSelf);
            Assert.Equal((0, 0), (s.HeroStatAppendagesAdded, s.HeroStatAppendages.Count));
            Assert.Equal(fx, s.Hero.SpeedFactorX);
        }

        using var arena = new ActArena("plain", monsterX: 900);
        RoomCombatSession session = arena.Session;
        TownHero hero = session.Hero;
        double before = hero.SpeedFactorX;
        session.AddHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -50), 0, arena.Monster.ActSelf);
        Assert.Equal(before * 0.5, hero.SpeedFactorX, 9);

        session.Vitals.Damage(1_000_000);
        Assert.True(session.HeroDead);
        arena.Steps(1);
        Assert.Equal(0, session.HeroStatAppendages.Count);
        Assert.Equal(before, hero.SpeedFactorX, 9);
        session.AddHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -50), 0, arena.Monster.ActSelf);      // 死了不挂
        Assert.Equal(1, session.HeroStatAppendagesAdded);
    }

    [Fact]
    public void TheSameChangeHungOnTheHeroTwiceReplacesAndIsCounted()
    {
        using var arena = new ActArena("plain", monsterX: 900);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        double before = hero.SpeedFactorX;
        StatChange slow = Pct(StatChangeTypes.MoveSpeed, -50);

        s.AddHeroStatAppendage(slow, 0, arena.Monster.ActSelf);
        s.AddHeroStatAppendage(slow, 0, arena.Monster.ActSelf);
        Assert.Equal((2, 1, 1), (s.HeroStatAppendagesAdded, s.HeroStatAppendagesReplaced, s.HeroStatAppendages.Count));
        Assert.Equal(before * 0.5, hero.SpeedFactorX, 9);                  // 顶替,不叠加
    }

    [Fact]
    public void TheHerosAttackSpeedIsOnlyChangedWhileHeIsNotAttackingButMovementIsImmediate()
    {
        using var arena = new ActArena("plain", monsterX: 900, frozenHero: true);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        double fx = hero.SpeedFactorX;
        double ar = hero.AttackAnimationRate;

        s.Update(10, 0, 0, true, false);                              // 按下攻击键
        Assert.True(hero.IsAttacking);
        s.AddHeroStatAppendage(Pct(StatChangeTypes.AttackSpeed, 100), 0, arena.Monster.ActSelf);
        s.AddHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -50), 0, arena.Monster.ActSelf);
        Assert.Equal(ar, hero.AttackAnimationRate);                   // 出招中不换播放倍率(免得帧号跳变)
        Assert.Equal(fx * 0.5, hero.SpeedFactorX, 9);                 // 速度系数立即换

        Assert.True(arena.Until(() => !hero.IsAttacking, 300) > 0);
        arena.Steps(1);
        Assert.Equal(ar * 2.0, hero.AttackAnimationRate, 9);          // 出招结束才换上

        s.RemoveHeroStatAppendage(Pct(StatChangeTypes.AttackSpeed, 100), arena.Monster.ActSelf);
        Assert.Equal(ar, hero.AttackAnimationRate, 9);
        s.RemoveHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -50), new Victim());               // 来源不对:不删
        Assert.Equal(fx * 0.5, hero.SpeedFactorX, 9);
        s.RemoveHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -50), arena.Monster.ActSelf);
        Assert.Equal(fx, hero.SpeedFactorX, 9);
    }

    [Fact]
    public void TheHeroSpeedFloorKeepsTheRelativeScalingReversible()
    {
        using var arena = new ActArena("plain", monsterX: 900);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        double fx = hero.SpeedFactorX;
        s.AddHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -100), 0, arena.Monster.ActSelf);          // 1 − 1 = 0:压到下限
        Assert.Equal(fx * RoomCombatSession.HeroStatMinRate, hero.SpeedFactorX, 9);
        s.RemoveHeroStatAppendage(Pct(StatChangeTypes.MoveSpeed, -100), arena.Monster.ActSelf);
        Assert.Equal(fx, hero.SpeedFactorX, 9);
    }

    // ------------------------------------------------------------------ 怪物身上的落实

    [Fact]
    public void AMonstersWalkSpeedFollowsTheMoveRateAndExactlyReturnsWhenTheAppendageExpires()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        double sx = m.SpeedX;
        double sy = m.SpeedY;
        m.AddStatAppendage(Pct(StatChangeTypes.MoveSpeed, -30), 200, m.ActSelf);
        Assert.Equal(sx * 0.7, m.SpeedX, 4);
        Assert.Equal(sy * 0.7, m.SpeedY, 4);
        m.AddStatAppendage(Pts(StatChangeTypes.MoveSpeed, 500), 0, m.ActSelf);                           // 点数的单位是千分之一:+0.5
        Assert.Equal(sx * 1.2, m.SpeedX, 4);

        arena.Steps(25);                                              // 250 毫秒:有时限的那项到期
        Assert.Equal(1, m.StatAppendagesExpired);
        Assert.Equal(sx * 1.5, m.SpeedX, 4);

        m.RemoveStatAppendage(Pts(StatChangeTypes.MoveSpeed, 500), m.ActSelf);
        Assert.Equal(sx, m.SpeedX);                                   // 全没了:原样回到出生值
        Assert.Equal(sy, m.SpeedY);
        Assert.Equal(1, m.StatAppendagesRemoved);
    }

    [Fact]
    public void AMonstersAttackRateScalesTheBirthRateAndIsFloored()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false, attackSpeed: 2000);
        MonsterActor m = arena.Monster;
        Assert.Equal(2.0, m.AttackRate);
        m.AddStatAppendage(Pct(StatChangeTypes.AttackSpeed, 50), 0, m.ActSelf);
        Assert.Equal(3.0, m.AttackRate, 6);                           // 2.0 × (1.5 ÷ 1)
        m.AddStatAppendage(Pct(StatChangeTypes.AttackSpeed, -150), 0, m.ActSelf);                        // 1 + 0.5 − 1.5 = 0:压到下限
        Assert.Equal(MonsterActor.MinAttackRate, m.AttackRate);
        m.RemoveStatAppendage(Pct(StatChangeTypes.AttackSpeed, -150), m.ActSelf);
        m.RemoveStatAppendage(Pct(StatChangeTypes.AttackSpeed, 50), m.ActSelf);
        Assert.Equal(2.0, m.AttackRate);
    }

    [Fact]
    public void TheAttackRateFloorNeverRaisesAMonsterThatWasSlowerToBeginWith()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false, attackSpeed: 50);
        MonsterActor m = arena.Monster;
        Assert.Equal(0.05, m.AttackRate, 9);
        m.AddStatAppendage(Pts(StatChangeTypes.AttackSpeed, 0), 0, m.ActSelf);                           // 什么都没改:出生值原样
        Assert.Equal(0.05, m.AttackRate, 9);
        m.AddStatAppendage(Pct(StatChangeTypes.AttackSpeed, -50), 0, m.ActSelf);                         // 压低:下限取出生值与 0.1 里小的那个
        Assert.Equal(0.05, m.AttackRate, 9);
    }

    [Fact]
    public void TheMonsterBodyHandsTheDurationAndTheSourceToTheMonster()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        IActEntity body = m.ActSelf;
        StatChange slow = Pct(StatChangeTypes.MoveSpeed, -30);
        body.AddStatAppendage(slow, 120, body);
        Assert.Equal(1, m.StatAppendages.Count);
        Assert.Equal(120, m.StatAppendages.Items[0].DurationMs);
        Assert.Same(body, m.StatAppendages.Items[0].Source);
        body.RemoveStatAppendage(slow, new Victim());                 // 来源不对:不删
        Assert.Equal(1, m.StatAppendages.Count);
        arena.Steps(15);                                              // 120 毫秒到期
        Assert.Equal(0, m.StatAppendages.Count);
        Assert.Equal(1, m.StatAppendagesExpired);
        body.AddStatAppendage(slow, 0, body);
        body.RemoveStatAppendage(slow, body);
        Assert.Equal(0, m.StatAppendages.Count);
    }

    [Fact]
    public void TheFallbackBasisKeepsTheCurrentPanelsDefenseElementsAndAbsoluteDefense()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        var stats = new MonsterStats();
        stats.Raw[MonsterStats.IxEquipPhysicalDefense] = 200;
        stats.Raw[MonsterStats.IxEquipMagicalDefense] = 100;
        for (int i = 0; i < 4; i++)
        {
            stats.Raw[MonsterStats.IxElementResistance + i] = 10 * (i + 1);
        }
        stats.SetF(MonsterStats.IxPhysicalAbsoluteDefense, 12f);
        stats.SetF(MonsterStats.IxMagicalAbsoluteDefense, 7f);
        m.Defense = DefenderPanel.FromMonster(stats, m.Level);        // 没给基础数值块:挂第一个附加物时按现在的面板临时造一块
        Assert.Null(m.BaseStats);
        m.AddStatAppendage(Pts(StatChangeTypes.EquipmentPhysicalDefense, 50), 0, m.ActSelf);
        Assert.NotNull(m.BaseStats);
        Assert.Equal((250, 100), (m.Defense.PhysicalDefense, m.Defense.MagicalDefense));
        Assert.Equal(new[] { 10, 20, 30, 40 }, new[] { m.Defense.ElementResist(0), m.Defense.ElementResist(1), m.Defense.ElementResist(2), m.Defense.ElementResist(3) });
        Assert.Equal((12f, 7f), (m.Defense.PhysicalPenetration, m.Defense.MagicalPenetration));
    }

    [Fact]
    public void ChangingTheAttackRateMidSwingKeepsTheAnimationOnTheSameFrame()
    {
        using var arena = new ActArena("plain", useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        arena.Steps(8);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        double clock = m.PhaseMs * m.AttackRate;
        int frame = m.CurrentFrame;
        m.AddStatAppendage(Pct(StatChangeTypes.AttackSpeed, 100), 0, m.ActSelf);
        Assert.Equal(2.0, m.AttackRate, 6);
        Assert.InRange(m.PhaseMs * m.AttackRate, clock - 2.0, clock + 2.0);       // 动画时钟不跳
        Assert.Equal(frame, m.CurrentFrame);
    }

    [Fact]
    public void AMonstersMaxHpFollowsTheHpMaxChangeAndTheCurrentHpIsOnlyClampedDownward()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        m.AddStatAppendage(Pct(StatChangeTypes.HpMax, 100), 0, m.ActSelf);
        Assert.Equal((200, 100), (m.HpMax, m.Hp));
        m.ActSelf.SetHp(200);
        Assert.Equal(200, m.Hp);
        m.RemoveStatAppendage(Pct(StatChangeTypes.HpMax, 100), m.ActSelf);
        Assert.Equal((100, 100), (m.HpMax, m.Hp));                    // 上限降了,当前生命夹下来

        m.AddStatAppendage(Pts(StatChangeTypes.HpMax, -500), 0, m.ActSelf);
        Assert.Equal(1, m.HpMax);                                     // 至少 1
        Assert.Equal(1, m.Hp);
    }

    [Fact]
    public void AMonstersDefensePanelIsRebuiltFromTheBaseStatsAndTheOriginalPanelComesBack()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        var stats = new MonsterStats();
        stats.Raw[MonsterStats.IxEquipPhysicalDefense] = 200;
        stats.Raw[MonsterStats.IxEquipMagicalDefense] = 100;
        stats.Raw[MonsterStats.IxElementResistance + 2] = 30;
        m.Defense = DefenderPanel.FromMonster(stats, m.Level);
        m.SetBaseStats(stats);
        DefenderPanel original = m.Defense;

        m.AddStatAppendage(Pts(StatChangeTypes.EquipmentPhysicalDefense, 50), 0, m.ActSelf);
        m.AddStatAppendage(Pct(StatChangeTypes.EquipmentMagicalDefense, 50), 0, m.ActSelf);
        m.AddStatAppendage(Pts(StatChangeTypes.ElementToleranceDark, 20), 0, m.ActSelf);
        m.AddStatAppendage(Pts(StatChangeTypes.PhysicalAbsoluteDefense, 30), 0, m.ActSelf);
        Assert.Equal((250, 150, 50), (m.Defense.PhysicalDefense, m.Defense.MagicalDefense, m.Defense.ElementResist(2)));
        Assert.Equal((30f, 0f), (m.Defense.PhysicalPenetration, m.Defense.MagicalPenetration));
        Assert.Equal(m.Level, m.Defense.Level);
        Assert.NotSame(original, m.Defense);
        Assert.Equal((200, 100, 30), (original.PhysicalDefense, original.MagicalDefense, original.ElementResist(2)));      // 原来的面板对象没被改

        m.RemoveStatAppendage(Pts(StatChangeTypes.EquipmentPhysicalDefense, 50), m.ActSelf);
        m.RemoveStatAppendage(Pct(StatChangeTypes.EquipmentMagicalDefense, 50), m.ActSelf);
        m.RemoveStatAppendage(Pts(StatChangeTypes.ElementToleranceDark, 20), m.ActSelf);
        Assert.Equal((200, 100, 30f), (m.Defense.PhysicalDefense, m.Defense.MagicalDefense, m.Defense.PhysicalPenetration));
        m.RemoveStatAppendage(Pts(StatChangeTypes.PhysicalAbsoluteDefense, 30), m.ActSelf);
        Assert.Same(original, m.Defense);                             // 全没了:放回出生时那个对象
    }

    [Fact]
    public void AMonsterWithoutADefensePanelKeepsNone()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        Assert.Null(m.Defense);
        m.AddStatAppendage(Pts(StatChangeTypes.EquipmentPhysicalDefense, 50), 0, m.ActSelf);
        Assert.Null(m.Defense);
    }

    [Fact]
    public void AMonstersAttackMovesByTheChangeInTheTwoAttackPairsProducts()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        m.MagicalAttack = 25;
        Assert.Equal((40, 25), (m.PhysicalAttack, m.MagicalAttack));

        m.AddStatAppendage(Pts(StatChangeTypes.PhysicalAttack, 10), 0, m.ActSelf);                      // 普通一对:整数 + 10
        Assert.Equal(50, m.PhysicalAttack);
        m.AddStatAppendage(Pct(StatChangeTypes.PhysicalAttack, 50), 0, m.ActSelf);                      // 50 × 1.5 = 75
        Assert.Equal(75, m.PhysicalAttack);
        m.AddStatAppendage(Pts(StatChangeTypes.EquipmentPhysicalAttack, 30), 0, m.ActSelf);             // 装备一对也算:+30
        Assert.Equal(105, m.PhysicalAttack);
        m.AddStatAppendage(Pct(StatChangeTypes.EquipmentMagicalAttack, 100), 0, m.ActSelf);             // 装备魔攻基数 0:乘出来还是 0
        Assert.Equal(25, m.MagicalAttack);
        m.AddStatAppendage(Pts(StatChangeTypes.MagicalAttack, -100), 0, m.ActSelf);                     // 不会降到 0 以下
        Assert.Equal(0, m.MagicalAttack);

        foreach (StatAppendage a in new System.Collections.Generic.List<StatAppendage>(m.StatAppendages.Items))
        {
            m.RemoveStatAppendage(a.Change, m.ActSelf);
        }
        Assert.Equal((40, 25), (m.PhysicalAttack, m.MagicalAttack));
    }

    [Fact]
    public void TheBirthAttackComesBackExactlyWhenTheLastAppendageGoes()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        m.MagicalAttack = 25;
        StatChange phys = Pts(StatChangeTypes.PhysicalAttack, 10);
        StatChange mag = Pts(StatChangeTypes.MagicalAttack, 5);

        m.AddStatAppendage(phys, 0, m.ActSelf);
        m.AddStatAppendage(mag, 0, m.ActSelf);
        Assert.Equal((50, 30), (m.PhysicalAttack, m.MagicalAttack));
        m.RemoveStatAppendage(mag, m.ActSelf);                        // 还剩一个:物攻的改动还在
        Assert.Equal((50, 25), (m.PhysicalAttack, m.MagicalAttack));
        m.RemoveStatAppendage(phys, m.ActSelf);                       // 最后一个走了:出生值放回
        Assert.Equal((40, 25), (m.PhysicalAttack, m.MagicalAttack));

        m.AddStatAppendage(phys, 0, m.ActSelf);
        m.AddStatAppendage(mag, 0, m.ActSelf);
        m.RemoveStatAppendage(phys, m.ActSelf);
        Assert.Equal((40, 30), (m.PhysicalAttack, m.MagicalAttack));
        m.RemoveStatAppendage(mag, m.ActSelf);
        Assert.Equal((40, 25), (m.PhysicalAttack, m.MagicalAttack));
    }

    [Fact]
    public void ChangesNobodyReadsAreCountedByTypeAndLeaveEveryNumberAlone()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        (int HpMax, double Speed, double Rate, int Attack) before = (m.HpMax, m.SpeedX, m.AttackRate, m.PhysicalAttack);
        m.AddStatAppendage(Pct(StatChangeTypes.PhysicalCriticalHitRate, 10), 0, m.ActSelf);
        m.AddStatAppendage(Pct(StatChangeTypes.GoodHit, 10), 0, m.ActSelf);
        m.AddStatAppendage(new StatChange(StatChangeTypes.Unknown, true, 10), 0, m.ActSelf);
        Assert.Equal(3, m.StatAppendagesAdded);
        Assert.Equal(3, m.StatAppendages.Count);
        Assert.Equal((m.HpMax, m.SpeedX, m.AttackRate, m.PhysicalAttack), before);
        Assert.Equal(1, m.StatAppendageTypeCounts[StatChangeTypes.PhysicalCriticalHitRate]);
        Assert.Equal(1, m.StatAppendageTypeCounts[StatChangeTypes.Unknown]);
    }

    [Fact]
    public void TheSameChangeFromAnotherSourceReplacesAndOnlyTheNewSourceCanDeleteIt()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        var other = new Victim();
        var slow = Pct(StatChangeTypes.MoveSpeed, -30);
        double sx = m.SpeedX;
        m.AddStatAppendage(slow, 0, m.ActSelf);
        m.AddStatAppendage(slow, 0, other);
        Assert.Equal((2, 1, 1), (m.StatAppendagesAdded, m.StatAppendagesReplaced, m.StatAppendages.Count));
        Assert.Equal(sx * 0.7, m.SpeedX, 4);                          // 顶替,不叠加
        m.RemoveStatAppendage(slow, m.ActSelf);                       // 旧来源已经不持有它
        Assert.Equal(1, m.StatAppendages.Count);
        m.RemoveStatAppendage(slow, other);
        Assert.Equal(0, m.StatAppendages.Count);
        Assert.Equal(sx, m.SpeedX);
    }

    [Fact]
    public void AnEmptySetKeepsRemoveAndAdvanceCheap()
    {
        using var arena = new ActArena("plain", monsterX: 900, useVitals: false, attach: false);
        MonsterActor m = arena.Monster;
        m.RemoveStatAppendage(Pct(StatChangeTypes.MoveSpeed, -30), m.ActSelf);
        arena.Steps(5);
        Assert.Equal((0, 0, 0, 0), (m.StatAppendagesAdded, m.StatAppendagesRemoved, m.StatAppendagesExpired, m.StatAppendages.Version));
        Assert.Null(m.BaseStats);                                     // 没挂过附加物就没造替身
    }

    [Fact]
    public void SessionTotalsAddUpTheMonstersAndTheirTypes()
    {
        using var arena = new ActArena("appendme", useVitals: false);
        Assert.True(arena.Until(() => arena.Monster.StatAppendagesAdded > 0) > 0);
        RoomCombatSession s = arena.Session;
        Assert.Equal((2, 0, 0, 0), (s.MonsterStatAppendagesAdded, s.MonsterStatAppendagesReplaced, s.MonsterStatAppendagesRemoved, s.MonsterStatAppendagesExpired));
        Assert.Equal(1, s.MonsterStatTypeCounts[StatChangeTypes.MoveSpeed]);
        Assert.Equal(1, s.MonsterStatTypeCounts[StatChangeTypes.HpMax]);
        Assert.Equal(0, s.HeroStatAppendagesAdded);
    }
}
