using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 经验与升级在 Play 层的接线(证据 E-111):主角的等级进度、生命与攻击面板随等级重算、击杀给经验、升级时的声音与光效、
/// 几个房间共用一份进度、基础经验表的来源文件、经验条的宽度。数据全是合成的,期望值是手算:
/// 经验表是 100 × L 的等差区间(升到 L 级的累计经验 100 × (L − 1) × L / 2,所以 2 级门槛 100、3 级 300、4 级 600、5 级 1000),
/// 升到 L 级给 L 点技能点;成长表:1 级生命 1000、魔法 500、物攻 50、物防 20、魔攻 40、魔防 10、受击恢复 5、魔法恢复 30,
/// 每升一级加 100、50、10、4、8、2、1、3。基础经验 400(1 级怪物)→ 总经验 400 / 2 = 200,等级差 0 倍率 1。
/// </summary>
public class ExpTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptLine Line(string tag, params ScriptValue[] values) => new ScriptLine(tag, values);

    internal static ExpTable Table()
    {
        var thresholds = new List<int>();
        for (int level = 2; level <= 91; level++)
        {
            thresholds.Add(100 * (level - 1) * level / 2);
        }
        return new ExpTable(thresholds);
    }

    internal static SpTable Sp()
    {
        var values = new List<int>();
        for (int level = 0; level <= 95; level++)
        {
            values.Add(level);
        }
        return new SpTable(values);
    }

    internal static ScriptLine[] GrowthLines()
    {
        return new[]
        {
            Line("[initial value]"),
            Line("[HP MAX]", I(1000)), Line("[MP MAX]", I(500)), Line("[physical attack]", I(50)), Line("[physical defense]", I(20)),
            Line("[magical attack]", I(40)), Line("[magical defense]", I(10)), Line("[hit recovery]", I(5)), Line("[MP regen speed]", I(30)),
            Line("[growtype 1]"),
            Line("[HP MAX]", I(100)), Line("[MP MAX]", I(50)), Line("[physical attack]", I(10)), Line("[physical defense]", I(4)),
            Line("[magical attack]", I(8)), Line("[magical defense]", I(2)), Line("[hit recovery]", I(1)), Line("[MP regen speed]", I(3)),
        };
    }

    internal static CharacterGrowth Growth() => CharacterGrowth.Parse(GrowthLines());

    internal static MobRewardTable Rewards(int level, long exp) => MobRewardTable.FromRows(new[] { new KeyValuePair<int, long>(level, exp) });

    /// <summary>合成的等级进度;<paramref name="mobExp"/> 是 <paramref name="mobLevel"/> 级怪物的基础经验。</summary>
    internal static HeroProgress Progress(int level = 1, int exp = -1, long mobExp = 400, int mobLevel = 1, ExpRates rates = null)
    {
        return new HeroProgress(Table(), Sp(), Growth(), Rewards(mobLevel, mobExp), rates ?? ExpRates.Neutral, level, exp);
    }

    private static HeroVitals VitalsOf(HeroProgress progress) => HeroVitals.FromStats(progress.Level, progress.Stats);

    /// <summary>按下攻击键、每 10 毫秒一步推到击杀(最多 1 秒)。</summary>
    private static void SwingUntilKill(RoomCombatSession s)
    {
        s.Update(10, 0, 0, true);
        for (int i = 0; i < 100 && s.Kills == 0; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(1, s.Kills);
    }

    // ------------------------------------------------------------------ HeroProgress

    [Fact]
    public void ANewProgressStartsAtTheThresholdOfItsLevelOrAtTheGivenExp()
    {
        HeroProgress first = Progress();
        Assert.Equal((1, 0, 0, 100), (first.Level, first.Exp, first.ExpInLevel, first.LevelSpan));
        HeroProgress third = Progress(level: 3);
        Assert.Equal((3, 300, 0, 300), (third.Level, third.Exp, third.ExpInLevel, third.LevelSpan));
        HeroProgress middle = Progress(level: 3, exp: 450);
        Assert.Equal((450, 150), (middle.Exp, middle.ExpInLevel));
        Assert.Equal(1, Progress(level: 0).Level);                       // 等级最低是 1
    }

    [Fact]
    public void GainAddsExpAndEveryCallThatChangesSomethingBumpsTheVersionOnce()
    {
        HeroProgress p = Progress();
        Assert.Equal(0, p.Version);
        ExpGainResult r = p.Gain(99);
        Assert.Equal((1, 99, 0, false), (r.Level, r.Exp, r.LevelsGained, r.LeveledUp));
        Assert.Equal((1, 99, 99, 1), (p.Level, p.Exp, p.ExpInLevel, p.Version));
        r = p.Gain(1);                                                   // 正好到 2 级的门槛:升级
        Assert.Equal((2, 100, 1, true), (r.Level, r.Exp, r.LevelsGained, r.LeveledUp));
        Assert.Equal((2, 100, 0, 1), (p.Level, p.Exp, p.ExpInLevel, p.LevelUps));
        Assert.Equal((2, 2, 100L), (p.SkillPoints, p.Version, p.TotalGained));   // 升到 2 级给 2 点技能点
    }

    [Fact]
    public void GainOfZeroOrLessChangesNothing()
    {
        HeroProgress p = Progress();
        ExpGainResult zero = p.Gain(0);
        ExpGainResult negative = p.Gain(-5);
        Assert.Equal((0, 0, 0L), (p.Version, p.Exp, p.TotalGained));
        Assert.Equal((1, 0, 0), (zero.Level, zero.Exp, zero.LevelsGained));
        Assert.Equal((1, 0, 0), (negative.Level, negative.Exp, negative.LevelsGained));
    }

    [Fact]
    public void OneBigGainCanCrossSeveralLevelsAndAddsUpThePointsInOneVersionStep()
    {
        HeroProgress p = Progress();
        ExpGainResult r = p.Gain(600);                                   // 600 正好是 4 级的门槛
        Assert.Equal((4, 600, 3), (r.Level, r.Exp, r.LevelsGained));
        Assert.Equal((3, 2 + 3 + 4, 1), (p.LevelUps, p.SkillPoints, p.Version));
        Assert.Equal(0, p.AwakeningPoints);                              // 觉醒点从 50 级起才给
    }

    [Fact]
    public void StatsFollowTheLevelThroughTheGrowth()
    {
        HeroProgress p = Progress();
        Assert.Equal((10000L, 5000L, 500L), (p.Stats.HpMax, p.Stats.MpMax, p.Stats.PhysicalAttack));
        p.Gain(600);                                                     // 4 级:加三次增量
        Assert.Equal((10000L + 3 * 1000, 5000L + 3 * 500, 500L + 3 * 100), (p.Stats.HpMax, p.Stats.MpMax, p.Stats.PhysicalAttack));
    }

    [Fact]
    public void SetLevelJumpsToTheThresholdAndOnlyCountsAsAChangeWhenSomethingChanged()
    {
        HeroProgress p = Progress();
        p.SetLevel(5);
        Assert.Equal((5, 1000, 1), (p.Level, p.Exp, p.Version));         // 5 级的门槛 1000
        p.SetLevel(5);                                                   // 没变:不算一次变化
        Assert.Equal(1, p.Version);
        p.Gain(10);
        Assert.Equal((1010, 2), (p.Exp, p.Version));
        p.SetLevel(5);                                                   // 经验退回门槛:算一次变化
        Assert.Equal((1000, 3), (p.Exp, p.Version));
        p.SetLevel(-3);                                                  // 等级最低是 1
        Assert.Equal((1, 0), (p.Level, p.Exp));
    }

    [Fact]
    public void TheCapLevelIsCappedAndGainingThereDoesNotLevelUp()
    {
        HeroProgress cap = Progress(level: Leveling.MaxLevel);
        Assert.True(cap.IsCapped);
        Assert.False(Progress(level: Leveling.MaxLevel - 1).IsCapped);
        cap.Gain(100000);
        Assert.Equal(Leveling.MaxLevel, cap.Level);
    }

    [Fact]
    public void MissingPartsFallBackToEmptyTablesAndAPlaceholderRewardTable()
    {
        Assert.Throws<ArgumentNullException>(() => new HeroProgress(null, Sp(), Growth(), Rewards(1, 1), null));
        var bare = new HeroProgress(Table(), null, null, null, null);
        Assert.Equal(0, bare.Sp.Count);
        Assert.False(bare.Growth.HasInitial);
        Assert.True(bare.Rewards.IsPlaceholder);
        Assert.Equal(0L, bare.Stats.HpMax);                              // 没有成长数据:属性全 0
        Assert.Throws<ArgumentNullException>(() => HeroProgress.CreateForLocalHero(null));
    }

    // ------------------------------------------------------------------ HeroVitals 与 HeroPanel 随等级重算

    [Fact]
    public void VitalsFromStatsRoundTenthsAwayFromZeroAndKeepDefensesAsDecimals()
    {
        var stats = new StatBlock(hpMax: 10005, mpMax: 5004, physicalAttack: 0, physicalDefense: 205, magicalAttack: 0, magicalDefense: 99,
            inventoryLimit: 0, mpRegenSpeed: 300, hitRecovery: 55);
        HeroVitals v = HeroVitals.FromStats(7, stats);
        Assert.Equal((7, 1001, 500, 6), (v.Level, v.HpMax, v.MpMax, v.HitRecovery));   // 1000.5 → 1001、500.4 → 500、5.5 → 6
        Assert.Equal((20.5, 9.9, 30.0), (v.PhysicalDefense, v.MagicalDefense, v.MpRegenPerMinute));
        Assert.Equal((v.HpMax, v.MpMax), (v.Hp, v.Mp));                  // 新造的满血满蓝
    }

    [Fact]
    public void VitalsFromStatsAtLevelOneMatchFromChrOfTheSameInitialBlock()
    {
        var chr = new CharacterDef(0, "character/test/mini.chr", GrowthLines());
        HeroVitals old = HeroVitals.FromChr(chr);
        HeroVitals fresh = HeroVitals.FromStats(1, Growth().Initial);
        Assert.Equal((old.Level, old.HpMax, old.MpMax, old.HitRecovery), (fresh.Level, fresh.HpMax, fresh.MpMax, fresh.HitRecovery));
        Assert.Equal((old.PhysicalDefense, old.MagicalDefense, old.MpRegenPerMinute), (fresh.PhysicalDefense, fresh.MagicalDefense, fresh.MpRegenPerMinute));
        HeroPanel oldPanel = HeroPanel.FromChr(chr, 1, default);
        HeroPanel freshPanel = HeroPanel.FromStats(1, Growth().Initial, default);
        Assert.Equal((oldPanel.PhysicalAttack, oldPanel.MagicalAttack), (freshPanel.PhysicalAttack, freshPanel.MagicalAttack));
    }

    [Fact]
    public void ApplyStatsRaisesTheLimitsKeepsTheCurrentValuesAndClampsWhenTheLimitDrops()
    {
        var v = new HeroVitals(1, 100, 50, 1, 2, 3);
        v.Damage(40);
        v.TrySpendMp(10);
        v.ApplyStats(new HeroVitals(2, 200, 80, 11, 12, 13, 5.0));
        Assert.Equal((2, 200, 80, 60, 40), (v.Level, v.HpMax, v.MpMax, v.Hp, v.Mp));   // 当前生命与魔法不变(升级不回复)
        Assert.Equal((11.0, 12.0, 13, 5.0), (v.PhysicalDefense, v.MagicalDefense, v.HitRecovery, v.MpRegenPerMinute));
        v.ApplyStats(new HeroVitals(1, 30, 20, 0, 0, 0));
        Assert.Equal((30, 20), (v.Hp, v.Mp));                            // 上限掉下来:夹在新上限里
        Assert.Throws<ArgumentNullException>(() => v.ApplyStats(null));
    }

    [Fact]
    public void PanelFromStatsTruncatesTenthsKeepsTheEquipmentAndClampsHugeValues()
    {
        var stats = new StatBlock(0, 0, physicalAttack: 125, physicalDefense: 0, magicalAttack: 99, magicalDefense: 0, inventoryLimit: 0, mpRegenSpeed: 0, hitRecovery: 0);
        HeroPanel p = HeroPanel.FromStats(9, stats, new EquipmentStats(30, 20, 1.5f, 2.5f));
        Assert.Equal((9, 12, 9), (p.Level, p.PhysicalAttack, p.MagicalAttack));          // 12.5 → 12、9.9 → 9
        Assert.Equal((30, 20, 1.5f, 2.5f), (p.Equipment.PhysicalAttack, p.Equipment.MagicalAttack, p.Equipment.PhysicalCritical, p.Equipment.MagicalCritical));
        var huge = new StatBlock(0, 0, physicalAttack: long.MaxValue, physicalDefense: 0, magicalAttack: long.MinValue, magicalDefense: 0, inventoryLimit: 0, mpRegenSpeed: 0, hitRecovery: 0);
        HeroPanel clamped = HeroPanel.FromStats(1, huge, default);
        Assert.Equal((int.MaxValue, int.MinValue), (clamped.PhysicalAttack, clamped.MagicalAttack));
    }

    // ------------------------------------------------------------------ 基础经验表的来源

    [Fact]
    public void RewardFilesAreReadFromTheExportedTextAndBadOrMissingFilesCountAsAbsent()
    {
        Assert.Null(MobRewards.TryRead(null));
        Assert.Null(MobRewards.TryRead(string.Empty));
        string dir = Path.Combine(Path.GetTempPath(), "dnf-exp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(MobRewards.TryRead(Path.Combine(dir, "nothing.csv")));
            string empty = Path.Combine(dir, "empty.csv");
            File.WriteAllText(empty, "level,exp\n# 只有标题与注释\n");
            Assert.Null(MobRewards.TryRead(empty));                       // 没有一个有效行:按没有处理
            string good = Path.Combine(dir, MobRewards.FileName);
            File.WriteAllText(good, "level,exp\r\n1,100\r\n2;250\r\n# 注释\r\n3\t400\r\n");
            MobRewardTable table = MobRewards.TryRead(good);
            Assert.NotNull(table);
            Assert.Equal((3, 100u, 250u, 400u, 0u, false), (table.Count, table.BaseExp(1), table.BaseExp(2), table.BaseExp(3), table.BaseExp(4), table.IsPlaceholder));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WithoutAnyExportTheRewardTableIsTheLabeledPlaceholder()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(MobRewards.EnvVariable)))
        {
            return;                                                      // 这台机器设了环境变量:不测缺省
        }
        MobRewardTable table = MobRewards.Load(null, Table());
        Assert.True(table.IsPlaceholder);
        Assert.Equal("placeholder", table.Source);
        Assert.Equal(4u, table.BaseExp(1));                              // 1 级区间 100 / 25
    }

    // ------------------------------------------------------------------ 战斗会话里的经验

    [Fact]
    public void AKillGivesTheFormulaExpAndAddsItToTheProgress()
    {
        HeroProgress progress = Progress(level: 10, mobExp: 160, mobLevel: 10);
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        Assert.Same(progress, s.Progress);
        SwingUntilKill(s);
        // 基础经验 160 → 总经验 160 / 2 = 80;主角与怪物同为 10 级,等级差倍率 1
        Assert.Equal((80, 80L, 0), (s.LastKillExp, s.ExpEarned, s.LevelUps));
        Assert.Equal((10, 4500 + 80, 80), (progress.Level, progress.Exp, progress.ExpInLevel));   // 10 级门槛 4500
    }

    [Fact]
    public void TheDungeonExpWeightAndTheMonsterKindScaleTheKillExp()
    {
        var rates = new ExpRates(null, null, new[] { 1f, 1.5f, 2f, 3f });
        using (var weighted = new Arena(new[] { (340, 100) }, hp: 1, progress: Progress(level: 10, mobExp: 160, mobLevel: 10), expWeight: 1.5f, monsterLevel: 10))
        {
            SwingUntilKill(weighted.Session);
            Assert.Equal(120, weighted.Session.LastKillExp);             // 160 / 2 × 1.5
            Assert.Equal(1.5f, weighted.Session.ExpWeight);
        }
        using (var boss = new Arena(new[] { (340, 100) }, hp: 1, progress: Progress(level: 10, mobExp: 160, mobLevel: 10, rates: rates), monsterLevel: 10, monsterBoss: true))
        {
            SwingUntilKill(boss.Session);
            Assert.Equal(240, boss.Session.LastKillExp);                 // 首领 = 种类 3:160 / 2 × 3
        }
    }

    [Fact]
    public void ALevelGapScalesTheKillExp()
    {
        // 怪物比主角高 2 级:等级差倍率 1.12(单精度);80 × 1.12 在 x87 里是 89.6000003…,截断成 89
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: Progress(level: 10, mobExp: 160, mobLevel: 12), monsterLevel: 12);
        SwingUntilKill(arena.Session);
        Assert.Equal(89, arena.Session.LastKillExp);
    }

    [Fact]
    public void WithoutAProgressAKillGivesNothingAndGainExpDoesNothing()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1);
        RoomCombatSession s = arena.Session;
        Assert.Null(s.Progress);
        SwingUntilKill(s);
        Assert.Equal((0, 0L, 0), (s.LastKillExp, s.ExpEarned, s.LevelUps));
        ExpGainResult r = s.GainExp(1000);
        Assert.Equal((0, 0, 0, false), (r.Level, r.Exp, r.LevelsGained, r.LeveledUp));
        s.SyncProgress();                                                // 没有进度:什么也不做,也不会出错
    }

    [Fact]
    public void ALevelUpSwapsInTheNewLevelsLimitsAndPanelKeepsTheCurrentLifeAndPlaysTheSoundAndTheEffect()
    {
        HeroProgress progress = Progress();
        HeroVitals vitals = VitalsOf(progress);
        HeroPanel panel = HeroPanel.FromStats(progress.Level, progress.Stats, new EquipmentStats(7, 8));
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, vitals: vitals, heroPanel: panel);
        RoomCombatSession s = arena.Session;
        vitals.Damage(300);
        SwingUntilKill(s);
        // 基础经验 400 → 200:够 2 级的门槛(100)、不够 3 级的(300)
        Assert.Equal((200, 2, 1, 1), (s.LastKillExp, progress.Level, s.LevelUps, progress.LevelUps));
        Assert.Equal((2, 1100, 550), (s.Vitals.Level, s.Vitals.HpMax, s.Vitals.MpMax));   // 1000 + 100、500 + 50
        Assert.Equal(700, s.Vitals.Hp);                                  // 升级不回复:受伤后的 700 留着
        Assert.Equal((24.0, 12.0, 6, 33.0), (s.Vitals.PhysicalDefense, s.Vitals.MagicalDefense, s.Vitals.HitRecovery, s.Vitals.MpRegenPerMinute));
        Assert.Equal((2, 60, 48), (s.Panel.Level, s.Panel.PhysicalAttack, s.Panel.MagicalAttack));   // 物攻 50 + 10、魔攻 40 + 8
        Assert.Equal((7, 8), (s.Panel.Equipment.PhysicalAttack, s.Panel.Equipment.MagicalAttack));   // 装备原样带过来
        var cues = new List<SoundCue>();
        s.DrainSounds(cues);
        Assert.Contains(cues, c => c.Kind == SoundCueKind.Ui && c.Key == RoomCombatSession.LevelUpSound);
        Assert.Contains(s.Effects, e => e.Path == RoomCombatSession.LevelUpEffectPath);
    }

    [Fact]
    public void TheLevelUpEffectFollowsTheHeroUntilItFinishes()
    {
        HeroProgress progress = Progress();
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress);
        RoomCombatSession s = arena.Session;
        SwingUntilKill(s);
        AnimatedEffect effect = s.Effects.First(e => e.Path == RoomCombatSession.LevelUpEffectPath);
        double startX = s.Hero.X;
        s.Hero.Place(250, 120, false, HeroMotion.Rest, 0);               // 主角还在挥剑硬直里走不动:直接把他放到别处
        s.Update(10, 0, 0, false);
        Assert.NotEqual(startX, s.Hero.X);
        Assert.Equal((s.Hero.X, s.Hero.Y), (effect.X, effect.Y));        // 光效每一步都挪到主角脚下
        for (int i = 0; i < 100; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.DoesNotContain(s.Effects, e => e.Path == RoomCombatSession.LevelUpEffectPath);   // 播完就没了
    }

    [Fact]
    public void AnExpOnlyGainDoesNotChangeTheLimitsAndALaterRoomPicksUpALevelUpFromTheSharedProgress()
    {
        HeroProgress progress = Progress();
        using var a = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, vitals: VitalsOf(progress));
        using var b = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, vitals: VitalsOf(progress));
        a.Session.GainExp(50);                                           // 只涨经验:不升级
        Assert.Equal((1, 50), (a.Session.Vitals.Level, progress.Exp));
        ExpGainResult r = a.Session.GainExp(100);                        // 150:升到 2 级
        Assert.Equal((2, 1, 1), (r.Level, r.LevelsGained, a.Session.LevelUps));
        Assert.Equal((2, 1100), (a.Session.Vitals.Level, a.Session.Vitals.HpMax));
        Assert.Equal((1, 1000), (b.Session.Vitals.Level, b.Session.Vitals.HpMax));   // 另一个房间的会话还没同步
        b.Session.SyncProgress();
        Assert.Equal((2, 1100), (b.Session.Vitals.Level, b.Session.Vitals.HpMax));
        Assert.Equal(0, b.Session.LevelUps);                             // 升级是在 a 里发生的,b 不记
        Assert.Equal(150L, a.Session.ExpEarned);
    }

    [Fact]
    public void AnUpdateOfALateRoomSyncsTheSharedProgressByItself()
    {
        HeroProgress progress = Progress();
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, vitals: VitalsOf(progress));
        progress.Gain(150);                                              // 别处升了级
        Assert.Equal(1, arena.Session.Vitals.Level);
        arena.Session.Update(10, 0, 0, false);
        Assert.Equal((2, 1100), (arena.Session.Vitals.Level, arena.Session.Vitals.HpMax));
    }

    // ------------------------------------------------------------------ 经验条宽度

    [Fact]
    public void TheExpBarWidthIsTheFloatRatioOfTheSixHundredTwentyFivePixelBarTruncated()
    {
        Assert.Equal(0, HudArt.ExpBarWidth(0, 1000));
        Assert.Equal(0, HudArt.ExpBarWidth(-5, 1000));                   // 值不大于 0:空
        Assert.Equal(625, HudArt.ExpBarWidth(1000, 1000));               // 值不小于分母:满
        Assert.Equal(625, HudArt.ExpBarWidth(5000, 1000));
        Assert.Equal(312, HudArt.ExpBarWidth(500, 1000));                // 625 × 0.5 = 312.5 截断
        Assert.Equal(0, HudArt.ExpBarWidth(1, 1000));                    // 0.625 截断成 0
        Assert.Equal(1, HudArt.ExpBarWidth(2, 1000));                    // 1.25
        Assert.Equal(624, HudArt.ExpBarWidth(999, 1000));                // 624.375
        Assert.Equal(208, HudArt.ExpBarWidth(1, 3));                     // 625 / 3 = 208.33
        Assert.Equal(625, HudArt.ExpBarWidth(10, 0));                    // 分母不大于 0(表走到头):值不小于分母就是满条
        Assert.Equal(625, HudArt.ExpBarWidth(0, 0));
        Assert.Equal(0, HudArt.ExpBarWidth(-5, 0));                      // 值为负:先于除法判成空,不会除以 0
    }
}
