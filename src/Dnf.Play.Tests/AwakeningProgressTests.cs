using Dnf.Data;
using Dnf.Game;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 觉醒在主角进度里的接线(r15-awaken;服务端 <c>set_grow_type</c> 的第二成长类型):门槛(50 级)与次序(一觉才能二觉)、没转职不能觉醒、
/// 觉醒后面板换成觉醒块、白送觉醒技能(不清空、点数不动)、学习上限按觉醒后的表算、重置只留当前觉醒的白送、觉醒点(特殊技能)不为负、
/// 转职保持觉醒、存档快照与还原(含旧存档、坏存档、觉醒后上限高于有效最高级的技能不退步)。技能、成长表与点数全是合成的,期望值是手算。
///
/// 成长表(.chr 的数乘 10 就是面板的十分之一单位):初始 生命 1000、物攻 50;[growtype 1](下标 0,15 级以前用它)每级 生命 +100、物攻 +10;
/// [growtype 2](下标 1,主角成长类型 1):每级 +110 / +11,觉醒 1 +150 / +15,觉醒 2 +190 / +19;
/// [growtype 3](下标 2,主角成长类型 2):每级 +120 / +12,觉醒 1 +160 / +16,觉醒 2 +200 / +20。
/// 50 级的生命(十分之一单位):成长类型 1 为 10000 + 14 × 1000 + 35 × 1100 = 62500,成长类型 2 为 10000 + 14000 + 35 × 1200 = 66000;
/// 50 级以后每级再加所选块的值(×10),所以 52 级比 50 级多两份。
///
/// 技能(目录第 0 页):901 开局白送,最高 5,觉醒 1 (成长类型 1)后上限降到 3;902 成长类型 1 白送;903 觉醒才开(一觉最高 3、二觉最高 4)、一觉白送 1 级;
/// 904 觉醒才开(一觉最高 2)、一觉白送;905 二觉才开(最高 2)、二觉白送;906 只在觉醒块里写成 [skill](不是白送);907 成长类型 2 白送;
/// 908、909 成长类型 2 的一觉与二觉白送;910 最高 3、觉醒 1 后最高 6(上限高于有效最高级);911 特殊技能,每级 2 个觉醒点。
/// 点数表是"第 L 级给 L 点",觉醒点从 50 级起每级 1 点(52 级 3 点、60 级 11 点)。
/// </summary>
public class AwakeningProgressTests
{
    private static ScriptLine Ints(string tag, params int[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, list);
    }

    private static ScriptLine Str(string tag, string text)
    {
        return new ScriptLine(tag, new[] { new ScriptValue(ScriptValueKind.String, 0, text) });
    }

    private static readonly int[] AllFive = { 5, 5, 5, 5, 5, 5 };
    private static readonly int[] AllThree = { 3, 3, 3, 3, 3, 3 };
    private static readonly int[] Nothing = new int[6];

    private static SkillDef Skill(int id, int[] max, int[] second = null, int[] cost = null, int[] special = null)
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", "[active]"),
            Ints("[required level]", 1),
            Ints("[required level range]", 1),
            Ints("[growtype maximum level]", max),
            Ints("[purchase cost]", cost ?? new[] { 1 }),
        };
        if (second != null)
        {
            lines.Add(Ints("[second growtype maximum level]", second));
        }
        if (special != null)
        {
            lines.Add(Ints("[feature skill type]", 1));
            lines.Add(Ints("[special purchase cost]", special));
        }
        return new SkillDef(id, "skill/test/s" + id + ".skl", lines.ToArray());
    }

    /// <summary>12 个第二成长类型最高级:每个 (成长类型, 觉醒, 值) 写在 成长类型 × 2 + 觉醒 − 1 这一格,别处 0。</summary>
    private static int[] Second(params (int Growtype, int Awakening, int Value)[] cells)
    {
        var second = new int[12];
        foreach ((int growtype, int awakening, int value) in cells)
        {
            second[growtype * 2 + awakening - 1] = value;
        }
        return second;
    }

    private static readonly int[] Ones6 = { 1, 1, 1, 1, 1, 1 };

    private static SkillCatalog Catalog()
    {
        return new SkillCatalog(0, new[]
        {
            Skill(901, AllFive, Second((1, 1, 3))),
            Skill(902, AllFive),
            Skill(903, Nothing, Second((1, 1, 3), (1, 2, 4)), cost: Ones6),
            Skill(904, Nothing, Second((1, 1, 2)), cost: Ones6),
            Skill(905, Nothing, Second((1, 2, 2)), cost: Ones6),
            Skill(906, AllFive),
            Skill(907, AllFive),
            Skill(908, Nothing, Second((2, 1, 3)), cost: Ones6),
            Skill(909, Nothing, Second((2, 2, 3)), cost: Ones6),
            Skill(910, AllThree, Second((1, 1, 6)), cost: Ones6),
            Skill(911, AllFive, special: new[] { 2 }),
        });
    }

    private static ScriptLine[] ChrLines()
    {
        return new[]
        {
            Ints("[initial value]"), Ints("[HP MAX]", 1000), Ints("[physical attack]", 50), Ints("[skill]", 901, 1),
            Ints("[growtype 1]"), Ints("[HP MAX]", 100), Ints("[physical attack]", 10),
            Ints("[growtype 2]"), Ints("[HP MAX]", 110), Ints("[physical attack]", 11), Ints("[skill]", 902, 1),
            Str("[awakening name]", "A"),
            Ints("[awakening 1]"), Ints("[HP MAX]", 150), Ints("[physical attack]", 15), Ints("[skill]", 906, 1),
            Ints("[awakening skill]", 903, 1, 904, 1), Ints("[/awakening skill]"),
            Ints("[awakening 2]"), Ints("[HP MAX]", 190), Ints("[physical attack]", 19),
            Ints("[awakening skill]", 905, 1), Ints("[/awakening skill]"),
            Ints("[growtype 3]"), Ints("[HP MAX]", 120), Ints("[physical attack]", 12), Ints("[skill]", 907, 1),
            Ints("[awakening 1]"), Ints("[HP MAX]", 160), Ints("[physical attack]", 16),
            Ints("[awakening skill]", 908, 1), Ints("[/awakening skill]"),
            Ints("[awakening 2]"), Ints("[HP MAX]", 200), Ints("[physical attack]", 20),
            Ints("[awakening skill]", 909, 1), Ints("[/awakening skill]"),
        };
    }

    internal static HeroProgress Hero(int level, int growtype = 1, bool grant = true)
    {
        ScriptLine[] chr = ChrLines();
        var p = new HeroProgress(Table(), Sp(), CharacterGrowth.Parse(chr), Rewards(1, 400), ExpRates.Neutral, level, -1, growtype);
        p.AttachSkills(Catalog(), chr, grant);
        return p;
    }

    private static int[] Learned(HeroProgress p) => p.Skills.Learned.Select(k => k.Key).OrderBy(i => i).ToArray();

    // ------------------------------------------------------------------ 门槛与次序

    [Fact]
    public void ANewHeroIsNotAwakenedAndHasTheAwakeningPointsOfItsLevel()
    {
        HeroProgress p = Hero(52);
        Assert.Equal(0, p.Awakening);
        Assert.Equal(1, p.Growtype);
        Assert.Equal(3, p.AwakeningPoints);                    // 52 − 49
        Assert.Equal(new[] { 901, 902 }, Learned(p));
        Assert.Equal(0, Hero(49).AwakeningPoints);
        Assert.Equal(1, Hero(50).AwakeningPoints);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(48)]
    [InlineData(49)]
    public void BelowFiftyAwakeningIsRefusedAndNothingChanges(int level)
    {
        HeroProgress p = Hero(level);
        int version = p.Version;
        int skillsVersion = p.SkillsVersion;
        long hp = p.BaseStats.HpMax;
        Assert.Equal(AwakeningCheck.LevelTooLow, p.CheckAwaken(1));
        Assert.Equal(AwakeningCheck.LevelTooLow, p.Awaken(1));
        Assert.Equal(0, p.Awakening);
        Assert.Equal(version, p.Version);
        Assert.Equal(skillsVersion, p.SkillsVersion);
        Assert.Equal(hp, p.BaseStats.HpMax);
        Assert.Equal(0, p.Skills.LevelOf(903));
    }

    [Fact]
    public void AtExactlyFiftyTheFirstAwakeningWorksAndBumpsTheVersions()
    {
        HeroProgress p = Hero(50);
        Assert.Equal(AwakeningCheck.Ok, p.CheckAwaken(1));
        Assert.Equal(0, p.Awakening);                           // 检查不动手
        int version = p.Version;
        int skillsVersion = p.SkillsVersion;
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(1));
        Assert.Equal(1, p.Awakening);
        Assert.Equal(version + 1, p.Version);
        Assert.True(p.SkillsVersion > skillsVersion);
        Assert.Equal(1, p.Skills.LevelOf(903));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void AHeroThatNeverTransferredCannotAwakenAtAnyLevel(int growtype)
    {
        HeroProgress p = Hero(60, growtype);
        Assert.Equal(AwakeningCheck.NotTransferred, p.Awaken(1));
        Assert.Equal(AwakeningCheck.NotTransferred, p.Awaken(2));
        Assert.Equal(AwakeningCheck.BadTarget, p.Awaken(0));    // 目标不对先于转职检查
        Assert.Equal(0, p.Awakening);
    }

    [Fact]
    public void OnlyTheNextAwakeningInOrderIsAllowedAndTheStateNeverMovesBack()
    {
        HeroProgress p = Hero(60);
        Assert.Equal(AwakeningCheck.OutOfOrder, p.Awaken(2));
        Assert.Equal(0, p.Awakening);
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(1));
        Assert.Equal(AwakeningCheck.OutOfOrder, p.Awaken(1));
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(2));
        Assert.Equal(AwakeningCheck.OutOfOrder, p.Awaken(2));
        Assert.Equal(AwakeningCheck.OutOfOrder, p.Awaken(1));
        Assert.Equal(AwakeningCheck.BadTarget, p.Awaken(3));
        Assert.Equal(AwakeningCheck.BadTarget, p.Awaken(0));
        Assert.Equal(AwakeningCheck.BadTarget, p.Awaken(-1));
        Assert.Equal(2, p.Awakening);
    }

    [Fact]
    public void ARefusedAwakeningLeavesSkillsAndPointsAlone()
    {
        HeroProgress p = Hero(60);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(901, 2, out _));
        int sp = p.SkillPoints;
        int skillsVersion = p.SkillsVersion;
        int version = p.Version;
        Assert.Equal(AwakeningCheck.OutOfOrder, p.Awaken(2));
        Assert.Equal((sp, skillsVersion, version), (p.SkillPoints, p.SkillsVersion, p.Version));
        Assert.Equal(3, p.Skills.LevelOf(901));
        Assert.Equal(new[] { 901, 902 }, Learned(p));
    }

    [Fact]
    public void AHeroWithoutSkillDataStillAwakensAndSwitchesItsStats()
    {
        var p = new HeroProgress(Table(), Sp(), Growth(), Rewards(1, 400), ExpRates.Neutral, 60, -1, 1);
        Assert.False(p.HasSkillData);
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(1));
        Assert.Equal(1, p.Awakening);
        Assert.Equal(0, p.SkillsLearned);
    }

    // ------------------------------------------------------------------ 成长块

    [Theory]
    [InlineData(1, 0, 64700, 5970)]
    [InlineData(1, 1, 65500, 6050)]
    [InlineData(1, 2, 66300, 6130)]
    [InlineData(2, 0, 68400, 6340)]
    [InlineData(2, 1, 69200, 6420)]
    [InlineData(2, 2, 70000, 6500)]
    public void TheBaseStatsFollowTheAwakeningBlockOfTheGrowtype(int growtype, int awakening, long hp, long physicalAttack)
    {
        HeroProgress p = Hero(52, growtype);
        for (int a = 1; a <= awakening; a++)
        {
            Assert.Equal(AwakeningCheck.Ok, p.Awaken(a));
        }
        Assert.Equal(awakening, p.Awakening);
        Assert.Equal(hp, p.BaseStats.HpMax);
        Assert.Equal(physicalAttack, p.BaseStats.PhysicalAttack);
        Assert.Equal(hp, p.Stats.HpMax);                        // 没穿装备:面板就是成长值
    }

    [Fact]
    public void AtLevelFiftyTheAwakeningBlockHasNotAddedAnythingYetAndFromFiftyOneItHas()
    {
        HeroProgress p = Hero(50);
        Assert.Equal(62500, p.BaseStats.HpMax);
        p.Awaken(1);
        Assert.Equal(62500, p.BaseStats.HpMax);                 // 觉醒块从 50 → 51 这一步起才加
        p.SetLevel(51);
        Assert.Equal(64000, p.BaseStats.HpMax);                 // 62500 + 1500
        p.SetLevel(53);
        Assert.Equal(67000, p.BaseStats.HpMax);                 // 62500 + 3 × 1500
        HeroProgress q = Hero(51);
        Assert.Equal(63600, q.BaseStats.HpMax);                 // 没觉醒:62500 + 1100
    }

    [Fact]
    public void AwakeningNeverTouchesTheStatsBelowFifty()
    {
        HeroProgress p = Hero(49);
        long hp = p.BaseStats.HpMax;
        p.SetLevel(50);
        p.Awaken(1);
        p.SetLevel(49);
        Assert.Equal(hp, p.BaseStats.HpMax);
        Assert.Equal(62500 - 1100, hp);
    }

    // ------------------------------------------------------------------ 技能

    [Fact]
    public void AwakeningAddsItsGiftsAndKeepsEverythingLearnedAndThePoints()
    {
        HeroProgress p = Hero(60);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(902, 2, out _));
        int sp = p.SkillPoints;
        int sfp = p.AwakeningPoints;
        int skillsVersion = p.SkillsVersion;
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(1));

        Assert.Equal(3, p.Skills.LevelOf(902));                 // 学过的不动
        Assert.Equal(1, p.Skills.LevelOf(903));
        Assert.Equal(1, p.Skills.LevelOf(904));
        Assert.Equal(1, p.Skills.GivenLevelOf(903));
        Assert.Equal(new[] { 901, 902, 903, 904 }, Learned(p));
        Assert.Equal((sp, sfp), (p.SkillPoints, p.AwakeningPoints));   // 觉醒不给也不收技能点
        Assert.True(p.SkillsVersion > skillsVersion);
        Assert.Equal(new[] { 901, 902, 903, 904 }, Enumerable.Range(0, 4).Select(p.Skills.QuickAt).ToArray());   // 新技能补进快捷栏的空格
    }

    [Fact]
    public void OnlyTheGiftsOfTheMatchingGrowtypeAndAwakeningAreGiven()
    {
        HeroProgress p = Hero(60);
        p.Awaken(1);
        Assert.Equal(0, p.Skills.LevelOf(905));                 // 二觉的
        Assert.Equal(0, p.Skills.LevelOf(906));                 // 觉醒块里写的 [skill] 不是白送
        Assert.Equal(0, p.Skills.LevelOf(907));                 // 另一个成长类型的
        Assert.Equal(0, p.Skills.LevelOf(908));
        Assert.Equal(0, p.Skills.LevelOf(909));
        Assert.Equal(0, p.Skills.LevelOf(910));
        HeroProgress q = Hero(60, 2);
        q.Awaken(1);
        Assert.Equal(new[] { 901, 907, 908 }, Learned(q));
    }

    [Fact]
    public void TheSecondAwakeningAddsItsOwnGiftsAndKeepsTheFirstOnes()
    {
        HeroProgress p = Hero(60);
        p.Awaken(1);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(903, 2, out _));
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(2));
        Assert.Equal(new[] { 901, 902, 903, 904, 905 }, Learned(p));
        Assert.Equal(3, p.Skills.LevelOf(903));
        Assert.Equal(1, p.Skills.GivenLevelOf(904));
        Assert.Equal(1, p.Skills.GivenLevelOf(905));
        Assert.Equal(new[] { 901, 902, 903, 904, 905 }, Enumerable.Range(0, 5).Select(p.Skills.QuickAt).ToArray());
    }

    [Fact]
    public void AnAwakeningThatLowersTheLimitKeepsWhatWasLearnedAndBlocksFurtherLevels()
    {
        HeroProgress p = Hero(60);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(901, 3, out _));      // 1 → 4:觉醒前最高 5
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(1));
        Assert.Equal(4, p.Skills.LevelOf(901));                           // 觉醒后上限 3,已学的不削
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(901, 1, out int spent));
        Assert.Equal(0, spent);
        Assert.Equal(SkillOpResult.Ok, p.RefundSkill(901, 2, out int refunded));
        Assert.Equal(2, refunded);
        Assert.Equal(2, p.Skills.LevelOf(901));
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(901, 1, out _));      // 低于上限了又能学,学到 3 为止
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(901, 1, out _));
        Assert.Equal(3, p.Skills.LevelOf(901));
    }

    [Fact]
    public void AwakeningOpensTheSkillsOnlyItOpensAndEachAwakeningHasItsOwnCap()
    {
        HeroProgress p = Hero(60);
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(903, 1, out _));
        p.Awaken(1);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(903, 2, out int spent));
        Assert.Equal(2, spent);
        Assert.Equal(3, p.Skills.LevelOf(903));
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(903, 1, out _));    // 一觉上限 3
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(905, 1, out _));    // 二觉才开
        p.Awaken(2);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(903, 1, out _));            // 二觉上限 4
        Assert.Equal(4, p.Skills.LevelOf(903));
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(903, 1, out _));
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(905, 1, out _));
        Assert.Equal(2, p.Skills.LevelOf(905));
    }

    [Fact]
    public void AnAwakeningCanRaiseTheLimitAboveTheOrdinaryOneAndTheSaveKeepsIt()
    {
        HeroProgress p = Hero(60);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(910, 3, out _));
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(910, 1, out _));    // 觉醒前上限 3
        p.Awaken(1);
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(910, 3, out _));            // 觉醒后上限 6
        Assert.Equal(6, p.Skills.LevelOf(910));
        HeroSave save = p.Snapshot();
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave parsed, out string error), error);
        HeroProgress q = Hero(1, 0, false);
        q.Restore(parsed, _ => null);
        Assert.Equal(6, q.Skills.LevelOf(910));                                 // 读档不把高于有效最高级的等级削回去
        Assert.Equal(1, q.Awakening);
    }

    [Fact]
    public void ResettingKeepsOnlyTheGiftsOfTheCurrentAwakeningAndRefundsEverything()
    {
        HeroProgress p = Hero(60);
        int totalSp = p.SkillPoints;
        p.Awaken(1);
        p.LearnSkill(903, 2, out _);
        p.LearnSkill(902, 2, out _);
        p.LearnSkill(911, 1, out _);
        Assert.True(p.SkillPoints < totalSp);
        p.ResetSkills();
        Assert.Equal(1, p.Awakening);                                           // 重置不动觉醒
        Assert.Equal(new[] { 901, 902, 903, 904 }, Learned(p));
        Assert.Equal(1, p.Skills.LevelOf(903));
        Assert.Equal(totalSp, p.SkillPoints);
        Assert.Equal(11, p.AwakeningPoints);                                    // 60 − 49
        p.Awaken(2);
        p.ResetSkills();
        Assert.Equal(new[] { 901, 902, 905 }, Learned(p));                      // 一觉的 903、904 不再白送(服务端只读当前觉醒的那份)
        Assert.Equal(2, p.Awakening);
    }

    [Fact]
    public void SpecialSkillsSpendAwakeningPointsAndTheyNeverGoBelowZero()
    {
        HeroProgress p = Hero(52);                                              // 3 个觉醒点
        p.Awaken(1);
        Assert.Equal(3, p.AwakeningPoints);                                     // 觉醒本身不收觉醒点
        int sp = p.SkillPoints;
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(911, 1, out int spent));
        Assert.Equal(2, spent);
        Assert.Equal(1, p.AwakeningPoints);
        Assert.Equal(sp, p.SkillPoints);                                        // 特殊技能不动技能点
        Assert.Equal(SkillOpResult.NotEnoughPoints, p.LearnSkill(911, 1, out spent));
        Assert.Equal(0, spent);
        Assert.Equal(1, p.AwakeningPoints);                                     // 不够就一分不扣
        Assert.Equal(SkillOpResult.Ok, p.RefundSkill(911, 1, out int back));
        Assert.Equal(2, back);
        Assert.Equal(3, p.AwakeningPoints);
        p.Skills.AddPoints(0, -50);
        Assert.Equal(0, p.AwakeningPoints);
        p.Skills.SetPoints(-4, -9);
        Assert.Equal((0, 0), (p.SkillPoints, p.AwakeningPoints));
        Assert.Equal(SkillOpResult.NotEnoughPoints, p.LearnSkill(911, 1, out _));
    }

    [Fact]
    public void ChangingGrowtypeKeepsTheAwakeningAndRereadsTheGiftsUnlessGoingBackToZero()
    {
        HeroProgress p = Hero(52);
        p.Awaken(1);
        int version = p.Version;
        Assert.True(p.ChangeGrowtype(2));
        Assert.Equal(1, p.Awakening);
        Assert.True(p.Version > version);
        Assert.Equal(new[] { 901, 907, 908 }, Learned(p));
        Assert.Equal(69200, p.BaseStats.HpMax);                                 // 成长类型 2 的觉醒 1 块
        Assert.True(p.ChangeGrowtype(0));
        Assert.Equal(0, p.Awakening);
        Assert.Equal(new[] { 901 }, Learned(p));
        Assert.Equal(61000, p.BaseStats.HpMax);                                 // 没转职:全程第一个成长类型的块
        Assert.Equal(AwakeningCheck.NotTransferred, p.Awaken(1));
    }

    // ------------------------------------------------------------------ 技能窗口、学习服务与自动加点

    [Fact]
    public void TheSkillWindowListsAndLimitsFollowTheAwakening()
    {
        HeroProgress p = Hero(60);
        var w = new SkillWindow();
        w.Use(p);
        Assert.True(w.Show(GameStage.Town));
        w.SetPage(1);
        Assert.Equal(new[] { 901, 902, 906, 907, 910 }, w.Rows.Select(r => r.SkillId).ToArray());
        Assert.Equal(5, w.Rows.Single(r => r.SkillId == 901).Cap);
        Assert.Equal(3, w.Rows.Single(r => r.SkillId == 910).Cap);

        p.Awaken(1);
        Assert.Equal(new[] { 901, 902, 903, 904, 906, 907, 910 }, w.Rows.Select(r => r.SkillId).ToArray());
        Assert.Equal(3, w.Rows.Single(r => r.SkillId == 901).Cap);
        Assert.Equal(6, w.Rows.Single(r => r.SkillId == 910).Cap);
        Assert.Equal(6, w.Rows.Single(r => r.SkillId == 910).MaxNow);
        Assert.Equal(3, w.Rows.Single(r => r.SkillId == 903).Cap);
        Assert.Equal(1, w.Rows.Single(r => r.SkillId == 903).Level);

        w.SetPage(3);
        Assert.Equal(new[] { 911 }, w.Rows.Select(r => r.SkillId).ToArray());
    }

    [Fact]
    public void AnAwakeningOnlySkillNobodyGaveAppearsInTheWindowOnlyAfterTheAwakeningThatOpensIt()
    {
        // 912 只有一觉才学得了(最高 2),不在任何白送列表里:不靠"已学的技能"这条路进窗口,只靠按觉醒列出的目录
        ScriptLine[] chr = ChrLines();
        var p = new HeroProgress(Table(), Sp(), CharacterGrowth.Parse(chr), Rewards(1, 400), ExpRates.Neutral, 60, -1, 1);
        p.AttachSkills(new SkillCatalog(0, new[] { Skill(901, AllFive), Skill(912, Nothing, Second((1, 1, 2)), cost: Ones6) }), chr, true);
        var w = new SkillWindow();
        w.Use(p);
        Assert.True(w.Show(GameStage.Town));
        w.SetPage(1);
        Assert.Equal(new[] { 901 }, w.Rows.Select(r => r.SkillId).ToArray());            // 觉醒前 912 学不了,不列

        Assert.Equal(AwakeningCheck.Ok, p.Awaken(1));
        Assert.Equal(new[] { 901, 912 }, w.Rows.Select(r => r.SkillId).ToArray());       // 觉醒后列出来,虽然一级都没学
        SkillRow row = w.Rows.Single(r => r.SkillId == 912);
        Assert.Equal(0, row.Level);
        Assert.Equal(2, row.Cap);
        Assert.Equal(2, row.MaxNow);
        Assert.Equal(1, row.NextCost);
        Assert.Equal(SkillRowState.Learnable, row.State);                                // 学得动的原因也按觉醒后的上限算
        Assert.Equal(SkillOpResult.Ok, row.Reason);
    }

    [Fact]
    public void TheWindowRowOfAnAwakeningSkillIsLearnableOnlyAfterTheAwakeningThatOpensIt()
    {
        HeroProgress p = Hero(60);
        var w = new SkillWindow();
        w.Use(p);
        Assert.True(w.Show(GameStage.Town));
        w.SetPage(1);
        Assert.Null(w.Rows.SingleOrDefault(r => r.SkillId == 903));
        Assert.Equal(AwakeningCheck.Ok, p.Awaken(1));
        SkillRow row = w.Rows.Single(r => r.SkillId == 903);                              // 一觉白送 1 级,上限 3
        Assert.Equal((1, 3), (row.Level, row.Cap));
        Assert.Equal(SkillRowState.Learnable, row.State);
        Assert.Equal(SkillOpResult.Ok, row.Reason);
    }

    [Fact]
    public void TheLearningServiceUsesTheAwakeningForLimitsAndBatches()
    {
        HeroProgress p = Hero(60);
        var svc = new SkillLearning(p);
        Assert.Equal(0, svc.LearnableLevels(903));
        Assert.Equal(SkillOpResult.NotAllowed, svc.CheckLearn(903, 1, out _));
        p.Awaken(1);
        Assert.Equal(2, svc.LearnableLevels(903));
        Assert.Equal(SkillOpResult.Ok, svc.CheckLearn(903, 2, out int cost));
        Assert.Equal(2, cost);

        SkillBatchResult tooMany = svc.LearnAll(new[] { new SkillStep(903, 2), new SkillStep(904, 2) });     // 904 一觉最高 2、已有 1 级:只能再学 1 级
        Assert.False(tooMany.Ok);
        Assert.Equal(1, tooMany.FailedIndex);
        Assert.Equal(1, p.Skills.LevelOf(903));                                                              // 整批不成,一步没动

        SkillBatchResult batch = svc.LearnAll(new[] { new SkillStep(903, 2), new SkillStep(904, 1) });
        Assert.True(batch.Ok);
        Assert.Equal(3, batch.Sp);
        Assert.Equal((3, 2), (p.Skills.LevelOf(903), p.Skills.LevelOf(904)));
        Assert.Equal(6, svc.LearnMax(910, out int spent));                                                   // 觉醒后上限 6,从 0 学到 6
        Assert.Equal(6, spent);
        Assert.Equal(0, svc.LearnMax(910, out _));
    }

    [Fact]
    public void TheAutoPlannerLearnsTheAwakeningSkillsAndHonoursTheAwakenedLimits()
    {
        HeroProgress plain = Hero(60);
        SkillPlanner.SpendAll(plain);
        Assert.Equal(0, plain.Skills.LevelOf(903));
        Assert.Equal(5, plain.Skills.LevelOf(901));
        Assert.Equal(3, plain.Skills.LevelOf(910));

        HeroProgress awakened = Hero(60);
        awakened.Awaken(1);
        SkillPlanner.SpendAll(awakened);
        Assert.Equal(3, awakened.Skills.LevelOf(903));
        Assert.Equal(2, awakened.Skills.LevelOf(904));
        Assert.Equal(3, awakened.Skills.LevelOf(901));
        Assert.Equal(6, awakened.Skills.LevelOf(910));
        Assert.Equal(0, SkillPlanner.SpendAll(awakened));                                                    // 再跑一遍没有可加的了

        Assert.DoesNotContain(903, SkillPlanner.Order(Catalog(), 1, 0).Select(d => d.Id));
        Assert.Contains(903, SkillPlanner.Order(Catalog(), 1, 1).Select(d => d.Id));
        Assert.DoesNotContain(903, SkillPlanner.Order(Catalog(), 1).Select(d => d.Id));                      // 老写法是没觉醒
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void AnAwakenedHeroSurvivesASaveRoundTripWithoutRegressing()
    {
        HeroProgress p = Hero(52);
        p.Awaken(1);
        p.LearnSkill(903, 2, out _);
        p.LearnSkill(901, 1, out _);
        HeroSave save = p.Snapshot();
        Assert.Equal(1, save.Awakening);
        string text = save.ToText();
        Assert.Contains("job 0\ngrowtype 1\nawakening 1\nlevel 52\n", text);
        Assert.True(HeroSave.TryParse(text, out HeroSave parsed, out string error), error);
        Assert.Equal(1, parsed.Awakening);

        HeroProgress q = Hero(1, 0, false);
        q.Restore(parsed, _ => null);
        Assert.Equal((52, 1, 1), (q.Level, q.Growtype, q.Awakening));
        Assert.Equal(65500, q.BaseStats.HpMax);
        Assert.Equal(6050, q.BaseStats.PhysicalAttack);
        Assert.Equal((3, 2), (q.Skills.LevelOf(903), q.Skills.LevelOf(901)));
        Assert.Equal(new[] { 901, 902, 903, 904 }, Learned(q));
        Assert.Equal((p.SkillPoints, p.AwakeningPoints), (q.SkillPoints, q.AwakeningPoints));
        Assert.Equal(Enumerable.Range(0, 4).Select(p.Skills.QuickAt).ToArray(), Enumerable.Range(0, 4).Select(q.Skills.QuickAt).ToArray());
        Assert.Equal(text, q.Snapshot().ToText());                                                           // 再拍一张与原来逐字节相同

        Assert.Equal(1, q.Skills.GivenLevelOf(903));                                                         // 觉醒白送的下限还原了:退到底停在白送等级
        Assert.Equal(SkillOpResult.Ok, q.RefundSkill(903, 2, out _));
        Assert.NotEqual(SkillOpResult.Ok, q.RefundSkill(903, 1, out _));
        Assert.Equal(1, q.Skills.LevelOf(903));
    }

    [Fact]
    public void ASecondAwakeningIsSavedAndRestoredToo()
    {
        HeroProgress p = Hero(60, 2);
        p.Awaken(1);
        p.Awaken(2);
        string text = p.Snapshot().ToText();
        Assert.Contains("growtype 2\nawakening 2\n", text);
        Assert.True(HeroSave.TryParse(text, out HeroSave parsed, out string error), error);
        HeroProgress q = Hero(1, 0, false);
        q.Restore(parsed, _ => null);
        Assert.Equal((2, 2), (q.Growtype, q.Awakening));
        Assert.Equal(p.BaseStats.HpMax, q.BaseStats.HpMax);
        Assert.Equal(new[] { 901, 907, 908, 909 }, Learned(q));
    }

    [Fact]
    public void ASaveWithoutTheAwakeningKeyLoadsAsNotAwakenedAndReplacesAnAwakenedHero()
    {
        string oldText = Hero(52).Snapshot().ToText();
        Assert.DoesNotContain("awakening", oldText);                            // 没觉醒的存档文本里没有这个键:旧格式不变
        HeroProgress p = Hero(52);
        p.Awaken(1);
        Assert.True(HeroSave.TryParse(oldText, out HeroSave parsed, out string error), error);
        Assert.Equal(0, parsed.Awakening);
        p.Restore(parsed, _ => null);
        Assert.Equal(0, p.Awakening);
        Assert.Equal(0, p.Skills.LevelOf(903));
        Assert.Equal(64700, p.BaseStats.HpMax);                                 // 回到觉醒前的面板
    }

    [Fact]
    public void RestoreDropsAnAwakeningWithoutATransferAndClampsOutOfRangeNumbers()
    {
        HeroSave save = Hero(52).Snapshot();
        save.Growtype = 0;
        save.Awakening = 1;
        HeroProgress p = Hero(52, 0);
        p.Restore(save, _ => null);
        Assert.Equal((0, 0), (p.Growtype, p.Awakening));                        // 没转职不会有觉醒

        save.Growtype = 1;
        save.Awakening = 7;
        p.Restore(save, _ => null);
        Assert.Equal(2, p.Awakening);                                           // 越界夹到 2

        save.Awakening = -3;
        p.Restore(save, _ => null);
        Assert.Equal(0, p.Awakening);
    }

    [Fact]
    public void ANegativeAwakeningPointBalanceInASaveLoadsAsZero()
    {
        HeroSave save = Hero(52).Snapshot();
        save.AwakeningPoints = -5;
        HeroProgress p = Hero(52);
        p.Restore(save, _ => null);
        Assert.Equal(0, p.AwakeningPoints);
    }

    [Theory]
    [InlineData("awakening 3")]
    [InlineData("awakening -1")]
    [InlineData("awakening x")]
    [InlineData("awakening")]
    [InlineData("awakening 1 2")]
    public void TheSaveTextRejectsABrokenAwakeningLine(string line)
    {
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\n" + line + "\nlevel 5\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("第 2 行", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheSaveTextAcceptsAwakeningZeroToTwo(int value)
    {
        Assert.True(HeroSave.TryParse("DNFUNITY-SAVE 1\ngrowtype 1\nawakening " + value + "\nlevel 60\n", out HeroSave save, out string error), error);
        Assert.Equal(value, save.Awakening);
    }

    [Fact]
    public void AFreshSaveWritesNoAwakeningLineAndTheLastOfARepeatedKeyWins()
    {
        Assert.DoesNotContain("awakening", new HeroSave().ToText());
        Assert.True(HeroSave.TryParse("DNFUNITY-SAVE 1\nawakening 1\nawakening 2\n", out HeroSave save, out _));
        Assert.Equal(2, save.Awakening);
        Assert.Equal(0, new HeroSave().Awakening);
    }

    // ------------------------------------------------------------------ 文字

    [Theory]
    [InlineData("AWAKENING", "觉醒")]
    [InlineData("EXP 100  GOLD 20  AWAKENING", "经验 100  金币 20  觉醒")]
    public void TheAwakeningSummaryIsTranslatedAndNotSwallowedByTheShorterPattern(string english, string chinese)
    {
        Assert.Equal(chinese, UiText.Translate(english));
    }
}
