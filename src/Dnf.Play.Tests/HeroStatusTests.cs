using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角身上的异常状态(M43,证据 E-136):怪物攻击信息里的 [active status] 槽随命中落到主角身上——概率与等级差修正(地下城里不用"人类比率")、
/// 失去控制(眩晕等:输入作废、留在受击姿势里、状态结束才恢复)、冰冻的摇脱、持续伤害(按间隔掉血、能把主角打死)、睡眠被打醒、减速、死亡清空。
/// 数据全是合成的(<see cref="AvatarFixture"/> 的迷你怪物每一击 25 点伤害;数值表的默认秒数 3 + 种类号,感电与石化的间隔 400 毫秒)。
/// </summary>
public class HeroStatusTests
{
    private const int Damage = 25;

    private static ActiveStatusTable Table()
    {
        var seconds = new int[ActiveStatusTable.KindCount];
        for (int k = 0; k < seconds.Length; k++)
        {
            seconds[k] = 3 + k;
        }
        return ActiveStatusTable.Synthetic(11, seconds, new float[ActiveStatusTable.KindCount * 2], new float[ActiveStatusTable.KindCount * 2], null, 1f, 1f, 400);
    }

    private static ActiveStatusEntry E(ActiveStatusKind kind, int duration, params int[] extra)
    {
        return ActiveStatusEntry.Create(kind, 100f, 0, duration, extra);
    }

    private static HeroStrikeInfo StatusStrike(float humanRate, params ActiveStatusEntry[] slots)
    {
        return new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, 0, false, 0, 0, -1, false, -1f, 0, false, 0, false, 300, false, false,
            reactionDamage: true, hitWav: null, reactionNone: false, source: null, statuses: slots, humanActiveStatusRate: humanRate);
    }

    // 带火属性、没有受击反应或倒地标志的一击
    private static HeroStrikeInfo OtherStrike(bool fire = false, bool none = false, bool down = false)
    {
        return new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, 0, down, 0, 0, -1, false, -1f, 0, false, 0, false, 300, false, false,
            reactionDamage: !down && !none, hitWav: null, reactionNone: none, source: null, statuses: null, humanActiveStatusRate: 1f, fireElement: fire);
    }

    private static HeroKnockdownTests.Arena NewArena(float humanRate, int hpMax, int attackDelayMs, params ActiveStatusEntry[] slots)
    {
        var arena = new HeroKnockdownTests.Arena(StatusStrike(humanRate, slots), hpMax, null, attackDelayMs);
        arena.Session.UseStatuses(Table());
        return arena;
    }

    // 带等级差比率的表:概率比率 250(每级 2.5%)、威力比率 50(每级 0.5%)
    private static HeroKnockdownTests.Arena NewLeveledArena(int hpMax, params ActiveStatusEntry[] slots)
    {
        var seconds = new int[ActiveStatusTable.KindCount];
        var table = ActiveStatusTable.Synthetic(11, seconds, new float[ActiveStatusTable.KindCount * 2], new float[ActiveStatusTable.KindCount * 2],
            null, 1f, 1f, 400, levelRateForProb: 250f, levelRateForPower: 50f);
        var arena = new HeroKnockdownTests.Arena(StatusStrike(1f, slots), hpMax, null, 100000);
        arena.Session.UseStatuses(table);
        return arena;
    }

    // ---- 攻击信息 → 一击 ----

    [Fact]
    public void TheStatusSlotsAndTheHumanRateOfAnAttackInfoReachTheStrike()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroStrikeInfo info = HeroStrikeInfo.Read(data, AvatarFixture.StatusAtk);
        Assert.Equal(new[] { ActiveStatusKind.Stun, ActiveStatusKind.Bleeding, ActiveStatusKind.Slow }, info.Statuses.Select(s => s.Kind));   // 第四个槽超出三个,被忽略
        Assert.Equal(new[] { 80f, 100f, 30f }, info.Statuses.Select(s => s.Probability));
        Assert.Equal(new[] { 1500, 2000, 3000 }, info.Statuses.Select(s => s.Duration));
        Assert.Equal(new[] { 300 }, info.Statuses[1].Extra);
        Assert.Equal(new[] { 40, 20 }, info.Statuses[2].Extra);
        Assert.Equal(0.5f, info.HumanActiveStatusRate);
        Assert.True(info.FireElement);

        HeroStrikeInfo withSkill = info.WithSkillDamage(10, 20);                                  // 换技能伤害不丢状态
        Assert.Equal(3, withSkill.Statuses.Count);
        Assert.Equal(0.5f, withSkill.HumanActiveStatusRate);
        Assert.True(withSkill.FireElement);

        // 没写状态的攻击信息、没有攻击信息、缺省的一击:没有槽,比率 1
        foreach (HeroStrikeInfo none in new[] { HeroStrikeInfo.Read(data, AvatarFixture.BlobStrikerAtk), HeroStrikeInfo.Read(data, null), default })
        {
            Assert.Empty(none.Statuses);
            Assert.Equal(1f, none.HumanActiveStatusRate);
            Assert.False(none.FireElement);
        }
    }

    // ---- 失去控制 ----

    [Fact]
    public void AStunningHitKeepsTheHeroHelplessUntilTheStunEnds()
    {
        using var arena = NewArena(1f, 1000, 100000, E(ActiveStatusKind.Stun, 2000));
        RoomCombatSession s = arena.Session;
        TownHero hero = arena.Hero;
        Assert.NotNull(s.HeroStatus);
        Assert.True(arena.RunToFirstHit() >= 0);
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Stun));
        Assert.Equal((1000 - Damage, 1, HeroMotion.Hurt), (s.Vitals.Hp, s.HeroHits, hero.Motion));

        arena.Steps(50);                                                // 500 毫秒:击退走完
        double x = hero.X, y = hero.Y;
        for (int i = 0; i < 100; i++)                                   // 又 1000 毫秒:方向键、攻击键、跳跃键都没有用
        {
            s.Update(10, 1, -1, i % 3 == 0, i % 5 == 0);
            Assert.Equal(HeroMotion.Hurt, hero.Motion);
            Assert.True(s.HeroStatus.ControlLocked);
            Assert.False(hero.IsAirborne);
            Assert.False(hero.IsAttacking);
        }
        Assert.Equal((x, y), (hero.X, hero.Y));

        Assert.True(arena.RunUntil(() => !s.HeroStatus.ControlLocked, 200));    // 剩下的 500 毫秒
        arena.Steps(2);
        Assert.Equal((HeroMotion.Rest, 0), (hero.Motion, s.HeroStatus.Count));
        s.Update(10, 1, 0, false, false);                                       // 恢复操作
        Assert.Equal(HeroMotion.Move, hero.Motion);
    }

    [Fact]
    public void AFrozenHeroShakesItOffByMashingTheDirectionKeysButNotByStandingStill()
    {
        using var arena = NewArena(1f, 1000, 100000, E(ActiveStatusKind.Freeze, 3000));
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Freeze));

        int steps = 0;
        while (s.HeroStatus.Has(ActiveStatusKind.Freeze) && steps < 400)
        {
            s.Update(10, steps % 2 == 0 ? 1 : -1, 0, false, false);       // 左右左右乱按:每次换方向缩短 HeroStatusTuning.ShakeStepMs
            steps++;
        }
        Assert.InRange(steps, 20, 30);                                  // 3000 / (120 + 10) 约 23 步,不摇要 300 步
        arena.Steps(40);
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);               // 摇脱之后放开受击姿势

        using var still = NewArena(1f, 1000, 100000, E(ActiveStatusKind.Freeze, 3000));
        still.RunToFirstHit();
        still.Steps(250);                                               // 不按键:2.5 秒后还冻着
        Assert.True(still.Session.HeroStatus.Has(ActiveStatusKind.Freeze));
        Assert.Equal(HeroMotion.Hurt, still.Hero.Motion);
        Assert.True(still.RunUntil(() => !still.Session.HeroStatus.Has(ActiveStatusKind.Freeze), 100));
    }

    [Fact]
    public void ALaterPlainHitDoesNotReleaseTheHold()
    {
        using var arena = NewArena(1f, 1000, 500, E(ActiveStatusKind.Stun, 5000));
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        arena.Monster.Strike = HeroKnockdownTests.Strike();             // 之后每 500 毫秒一击不带状态的普通受击
        Assert.True(arena.RunUntil(() => s.HeroHits >= 3, 400));
        arena.Steps(20);
        Assert.Equal(HeroMotion.Hurt, arena.Hero.Motion);               // 每一击的受击都放开了留住,随后又接上:没有站起来的空档
        Assert.True(s.HeroStatus.ControlLocked);
    }

    [Fact]
    public void ASleepingHeroWakesWhenHitAgainAndGetsUpAfterTheStun()
    {
        using var arena = NewArena(1f, 1000, 1000, E(ActiveStatusKind.Sleep, 10000));
        RoomCombatSession s = arena.Session;
        TownHero hero = arena.Hero;
        arena.RunToFirstHit();
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Sleep));
        arena.Steps(30);
        Assert.Equal(HeroMotion.Hurt, hero.Motion);                     // 睡着:不是硬直也留在姿势里
        arena.Monster.Strike = HeroKnockdownTests.Strike();             // 第二击是不带状态的普通一击
        Assert.True(arena.RunUntil(() => s.HeroHits >= 2, 400));
        Assert.False(s.HeroStatus.Has(ActiveStatusKind.Sleep));
        Assert.Equal(1000 - 2 * Damage, s.Vitals.Hp);
        Assert.True(arena.RunUntil(() => hero.Motion == HeroMotion.Rest, 200));
    }

    // ---- 受击方规则:冰冻与石化不产生反应、火打碎冰冻、睡眠只被有反应的一击叫醒 ----

    [Fact]
    public void AFrozenOrStonedHeroIsNotFlinchedOrKnockedDownByLaterHits()
    {
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.Freeze, ActiveStatusKind.Stone })
        {
            using var arena = NewArena(1f, 1000, 400, E(kind, 6000));
            RoomCombatSession s = arena.Session;
            arena.RunToFirstHit();
            Assert.True(s.HeroStatus.Has(kind));
            arena.Steps(50);                                                // 击退走完
            double x = arena.Hero.X, y = arena.Hero.Y;

            arena.Monster.Strike = OtherStrike(down: true);                 // 之后每 400 毫秒一击倒地攻击
            Assert.True(arena.RunUntil(() => s.HeroHits >= 3, 400));
            Assert.Equal(1000 - 3 * Damage, s.Vitals.Hp);                   // 照样扣血
            Assert.False(arena.Down.Active);                                // 推不倒
            Assert.Equal(HeroMotion.Hurt, arena.Hero.Motion);               // 还在原来的受击姿势里
            Assert.Equal((x, y), (arena.Hero.X, arena.Hero.Y));             // 没有再被击退
            Assert.True(s.HeroStatus.Has(kind));
        }
    }

    [Fact]
    public void AStunnedHeroIsStillKnockedDownByADownAttack()
    {
        using var arena = NewArena(1f, 1000, 400, E(ActiveStatusKind.Stun, 6000));
        arena.RunToFirstHit();
        arena.Monster.Strike = OtherStrike(down: true);
        Assert.True(arena.RunUntil(() => arena.Down.Active, 400));         // 眩晕不在第 5 步的两种里:倒地攻击照样推倒
    }

    [Fact]
    public void AFireHitBreaksTheIceButAPlainHitDoesNot()
    {
        using var arena = NewArena(1f, 1000, 400, E(ActiveStatusKind.Freeze, 6000));
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        arena.Monster.Strike = OtherStrike();                               // 普通一击:冰冻还在
        Assert.True(arena.RunUntil(() => s.HeroHits >= 2, 400));
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Freeze));

        arena.Monster.Strike = OtherStrike(fire: true);                     // 火属性的一击:打碎
        Assert.True(arena.RunUntil(() => s.HeroHits >= 3, 400));
        Assert.False(s.HeroStatus.Has(ActiveStatusKind.Freeze));
        arena.Monster.Strike = OtherStrike(none: true);
        Assert.True(arena.RunUntil(() => arena.Hero.Motion == HeroMotion.Rest, 400));    // 没有留住了:放开受击姿势回到站立
    }

    [Fact]
    public void ASleeperIsWokenOnlyByAHitWithAReaction()
    {
        using var arena = NewArena(1f, 1000, 400, E(ActiveStatusKind.Sleep, 8000));
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Sleep));
        arena.Monster.Strike = OtherStrike(fire: true, none: true);         // 火属性但没有受击反应:睡着的不醒(解除冰冻才看火)
        Assert.True(arena.RunUntil(() => s.HeroHits >= 2, 400));
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Sleep));
        arena.Monster.Strike = OtherStrike();                               // 有反应的一击:叫醒
        Assert.True(arena.RunUntil(() => s.HeroHits >= 3, 400));
        Assert.False(s.HeroStatus.Has(ActiveStatusKind.Sleep));
    }

    // ---- 概率 ----

    [Fact]
    public void AZeroChanceOrNoTableLeavesTheHeroAloneAndTheHumanRateIsNotUsedInADungeon()
    {
        // 攻击信息的人类比率只在决斗类模式里生效:地下城里即使写 0 也照样挂上
        using (var zeroRate = NewArena(0f, 1000, 100000, E(ActiveStatusKind.Stun, 2000)))
        {
            zeroRate.RunToFirstHit();
            Assert.Equal((1, 0), (zeroRate.Session.HeroStatus.Count, zeroRate.Session.HeroStatus.MissedCount));
        }
        using (var zeroChance = NewArena(1f, 1000, 100000, ActiveStatusEntry.Create(ActiveStatusKind.Stun, 0f, 0, 2000)))
        {
            zeroChance.RunToFirstHit();
            Assert.Equal((0, 1), (zeroChance.Session.HeroStatus.Count, zeroChance.Session.HeroStatus.MissedCount));
            Assert.True(zeroChance.RunUntil(() => zeroChance.Hero.Motion == HeroMotion.Rest, 200));
        }
        using (var noTable = new HeroKnockdownTests.Arena(StatusStrike(1f, E(ActiveStatusKind.Stun, 2000))))
        {
            Assert.Null(noTable.Session.HeroStatus);
            noTable.RunToFirstHit();
            Assert.True(noTable.RunUntil(() => noTable.Hero.Motion == HeroMotion.Rest, 200));   // 没有数值表:状态不起作用,只有普通受击
            noTable.Session.UseStatuses(null);
            Assert.Null(noTable.Session.HeroStatus);
        }
    }

    // ---- 等级差 ----

    [Fact]
    public void TheHeroLevelAndTheStatusLevelOfTheSlotChangeTheLandedStatus()
    {
        // 迷你局里主角 1 级。状态等级 51:主角低 50 级,眩晕时长乘 1 + 50 × 0.5% = 1.25
        using (var low = NewLeveledArena(1000, ActiveStatusEntry.Create(ActiveStatusKind.Stun, 100f, 51, 2000)))
        {
            low.RunToFirstHit();
            Assert.Equal(2500, low.Session.HeroStatus.Find(ActiveStatusKind.Stun).DurationMs);
        }
        // 状态等级 1:同级,原样
        using (var same = NewLeveledArena(1000, ActiveStatusEntry.Create(ActiveStatusKind.Stun, 100f, 1, 2000)))
        {
            same.RunToFirstHit();
            Assert.Equal(2000, same.Session.HeroStatus.Find(ActiveStatusKind.Stun).DurationMs);
        }
        // 状态等级 0:不修正
        using (var zero = NewLeveledArena(1000, ActiveStatusEntry.Create(ActiveStatusKind.Stun, 100f, 0, 2000)))
        {
            zero.RunToFirstHit();
            Assert.Equal(2000, zero.Session.HeroStatus.Find(ActiveStatusKind.Stun).DurationMs);
        }
        // 主角比状态等级高很多:状态等级取合成的 -99(相当于主角高 100 级),概率乘 1 − 100 × 2.5% 小于 0,取 0,一次也挂不上
        using (var high = NewLeveledArena(1000, ActiveStatusEntry.Create(ActiveStatusKind.Stun, 100f, -99, 2000)))
        {
            high.RunToFirstHit();
            Assert.Equal((0, 1), (high.Session.HeroStatus.Count, high.Session.HeroStatus.MissedCount));
        }
    }

    // ---- 持续伤害 ----

    [Fact]
    public void ABleedingHeroLosesTheTotalInEvenTicksAndStillWalks()
    {
        using var arena = NewArena(1f, 1000, 100000, E(ActiveStatusKind.Bleeding, 2000, 200));
        RoomCombatSession s = arena.Session;
        TownHero hero = arena.Hero;
        arena.RunToFirstHit();
        Assert.Equal(1000 - Damage, s.Vitals.Hp);
        long taken = s.HeroDamageTaken;
        Assert.False(s.HeroStatus.ControlLocked);                       // 出血不锁住

        var drops = new List<int>();
        for (int i = 0; i < 205; i++)
        {
            int before = s.Vitals.Hp;
            s.Update(10, 0, 0, false, false);
            if (s.Vitals.Hp < before)
            {
                drops.Add(before - s.Vitals.Hp);
                Assert.Contains(s.Popups, p => p.OnHero && p.Value == before - s.Vitals.Hp);     // 飘一个红字
            }
        }
        Assert.Equal(new[] { 50, 50, 50, 50 }, drops);                  // 总量 200,每 500 毫秒一跳
        Assert.Equal(1000 - Damage - 200, s.Vitals.Hp);
        Assert.Equal((1, taken + 200, 0), (s.HeroHits, s.HeroDamageTaken, s.HeroStatus.Count));   // 持续伤害不算"被打中"

        double x = hero.X;
        arena.Steps(10, dirX: 1);
        Assert.True(hero.X > x);
    }

    [Fact]
    public void ADamageOverTimeTickCanKillTheHeroAndLeavesNoStatusBehind()
    {
        using var arena = NewArena(1f, 100, 100000, E(ActiveStatusKind.Bleeding, 2000, 500));    // 每跳 125,比剩下的血多
        RoomCombatSession s = arena.Session;
        TownHero hero = arena.Hero;
        arena.RunToFirstHit();
        Assert.Equal((100 - Damage, false), (s.Vitals.Hp, s.HeroDead));
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Bleeding));

        Assert.True(arena.RunUntil(() => s.HeroDead, 100));
        Assert.Equal((0, 0, 1), (s.Vitals.Hp, s.HeroStatus.Count, s.HeroHits));
        Assert.Equal((HeroMotion.Down, 1), (hero.Motion, arena.Down.Begun));     // 倒下
        var cues = new List<SoundCue>();
        s.DrainSounds(cues);
        Assert.Contains(cues, c => c.Key == SwordmanVoice.Die);
        long taken = s.HeroDamageTaken;
        arena.Steps(300);                                               // 死后不再掉血
        Assert.Equal((0, taken, 0), (s.Vitals.Hp, s.HeroDamageTaken, s.HeroStatus.Count));
    }

    [Fact]
    public void AKillingBlowLeavesNoStatusOnTheCorpse()
    {
        using var arena = NewArena(1f, 20, 100000, E(ActiveStatusKind.Stun, 2000), E(ActiveStatusKind.Poison, 3000, 90));
        arena.RunToFirstHit();
        Assert.True(arena.Session.HeroDead);
        Assert.Equal(0, arena.Session.HeroStatus.Count);
    }

    // ---- 减速 ----

    [Fact]
    public void ASlowedHeroMovesSlowerUntilTheStatusEnds()
    {
        using var arena = NewArena(1f, 1000, 100000, E(ActiveStatusKind.Slow, 2000, 50, 0));
        RoomCombatSession s = arena.Session;
        TownHero hero = arena.Hero;
        double fx = hero.SpeedFactorX, fy = hero.SpeedFactorY;
        arena.RunToFirstHit();
        arena.Steps(1);
        Assert.Equal(50, s.HeroStatus.MoveSlowPercent);
        Assert.Equal((fx * 0.5, fy * 0.5), (hero.SpeedFactorX, hero.SpeedFactorY));
        Assert.False(s.HeroStatus.ControlLocked);

        Assert.True(arena.RunUntil(() => !s.HeroStatus.Has(ActiveStatusKind.Slow), 400));
        arena.Steps(1);
        Assert.Equal(fx, hero.SpeedFactorX, 9);                         // 状态一结束就回到原来的速度
        Assert.Equal(fy, hero.SpeedFactorY, 9);
    }

    [Fact]
    public void DeathClearsASlowAndTheSpeedFactorsComeBack()
    {
        using var arena = NewArena(1f, 100, 100000, E(ActiveStatusKind.Slow, 9000, 40, 0), E(ActiveStatusKind.Bleeding, 3000, 600));
        RoomCombatSession s = arena.Session;
        TownHero hero = arena.Hero;
        double fx = hero.SpeedFactorX;
        arena.RunToFirstHit();
        arena.Steps(1);
        Assert.Equal(fx * 0.6, hero.SpeedFactorX, 9);
        Assert.True(arena.RunUntil(() => s.HeroDead, 200));
        arena.Steps(2);
        Assert.Equal(fx, hero.SpeedFactorX, 9);
        Assert.Equal(0, s.HeroStatus.Count);
    }
}
