using System.Text;
using Dnf.Data;
using Dnf.Game;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 技能学习服务的合成夹具(<see cref="SkillLearning"/>,证据 E-127;技能编号全是合成的 901 到 914,不含任何原版数据)。
/// 点数表是"第 L 级给 L 点"(<see cref="ExpTests.Sp"/>),所以 L 级主角一开始有 1 + 2 + … + L 点(10 级 55 点、55 级 1540 点),特殊点从 50 级起 级数 − 49(55 级 6 点)。
/// </summary>
internal static class LearningFixture
{
    internal static readonly int[] Five = { 5, 5, 5, 5, 5, 5 };

    internal static ScriptLine Ints(string tag, params int[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, list);
    }

    internal static ScriptLine Str(string tag, string text)
    {
        return new ScriptLine(tag, new[] { new ScriptValue(ScriptValueKind.String, 0, text) });
    }

    /// <summary>[command] 一行:按顺序的括号词(如 "DOWN"、"SKILL")。</summary>
    internal static ScriptLine Command(params string[] keys)
    {
        return new ScriptLine("[command]", keys.Select(k => new ScriptValue(ScriptValueKind.Paren, 0, k)).ToArray());
    }

    /// <summary>合成技能:要求等级 <paramref name="req"/>、间隔 <paramref name="range"/>、各成长类型最高级 <paramref name="max"/>、每级 <paramref name="cost"/> 点。</summary>
    internal static SkillDef Skill(int id, int req, int range, int[] max, int cost, bool active = true, bool special = false, int[] pre = null, string[] command = null)
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", active ? "[active]" : "[passive]"),
            Ints("[required level]", req),
            Ints("[required level range]", range),
            Ints("[growtype maximum level]", max),
            special ? Ints("[special purchase cost]", cost) : Ints("[purchase cost]", cost),
        };
        if (special)
        {
            lines.Add(Ints("[feature skill type]", 1));
        }
        if (pre != null)
        {
            lines.Add(Ints("[pre required skill]", pre));
        }
        if (command != null)
        {
            lines.Add(Command(command));
        }
        return new SkillDef(id, "skill/synthetic/s" + id + ".skl", lines.ToArray());
    }

    /// <summary>
    /// "学习树":901 主动、1 级起、间隔 2、最高 5、每级 3 点(第 n 级要求主角 1 + 2(n−1) 级);902 被动、3 级起、间隔 3、最高 3、每级 4 点(要求 3、6、9 级);
    /// 903 主动、5 级起、间隔 1、最高 5、每级 5 点,前置 901 两级与 902 一级;904 主动、只有成长类型 1 学得了、最高 3、每级 2 点;
    /// 905 特殊主动(花觉醒点)、10 级起、间隔 5、最高 3、每级 1 点(要求 10、15、20 级)。
    /// </summary>
    internal static SkillCatalog Tree()
    {
        return new SkillCatalog(0, new[]
        {
            Skill(901, 1, 2, Five, 3),
            Skill(902, 3, 3, new[] { 3, 3, 3, 3, 3, 3 }, 4, active: false),
            Skill(903, 5, 1, Five, 5, pre: new[] { 901, 2, 902, 1 }),
            Skill(904, 1, 1, new[] { 0, 3, 0, 0, 0, 0 }, 2),
            Skill(905, 10, 5, new[] { 3, 3, 3, 3, 3, 3 }, 1, special: true),
        });
    }

    /// <summary>学习树的白送:开局 901 一级;成长类型 1(下标 1)再给 904 一级。</summary>
    internal static ScriptLine[] TreeChr()
    {
        return new[]
        {
            Ints("[initial value]"),
            Ints("[skill]", 901, 1),
            Ints("[growtype 1]"),
            Ints("[growtype 2]"),
            Ints("[skill]", 904, 1),
        };
    }

    /// <summary>
    /// "快捷栏夹具":13 个主动技能与 1 个被动,都是 1 级起、间隔 1、最高 5、每级 1 点。
    /// 靠指令放得出来的:901 [技能]、902 [下 技能]、903 [增益]、904 [下 下 攻击]、905 [左 跳跃]、906 [上 增益];
    /// 只能靠快捷键的:907、910、913 没有指令,908、911 只有 [跳跃],909、912 只有 [攻击](单键的跳跃与攻击留给普通跳跃与普攻,指令层不放);914 是被动技能。
    /// </summary>
    internal static SkillCatalog BarSkills()
    {
        string[][] commands =
        {
            new[] { "SKILL" }, new[] { "DOWN", "SKILL" }, new[] { "BUFF" }, new[] { "DOWN", "DOWN", "ATTACK" }, new[] { "LEFT", "JUMP" }, new[] { "UP", "BUFF" },
            null, new[] { "JUMP" }, new[] { "ATTACK" }, null, new[] { "JUMP" }, new[] { "ATTACK" }, null,
        };
        var list = new List<SkillDef>();
        for (int i = 0; i < commands.Length; i++)
        {
            list.Add(Skill(901 + i, 1, 1, Five, 1, command: commands[i]));
        }
        list.Add(Skill(914, 1, 1, Five, 1, active: false));
        return new SkillCatalog(0, list);
    }

    /// <summary>接上技能数据的主角(<paramref name="grantTotalPoints"/> 真:点数是该级的总额)。</summary>
    internal static HeroProgress Hero(SkillCatalog catalog, int level, ScriptLine[] chr = null, int growtype = 0)
    {
        var hero = new HeroProgress(Table(), Sp(), Growth(), Rewards(1, 400), ExpRates.Neutral, level, -1, growtype);
        hero.AttachSkills(catalog, chr, true);
        return hero;
    }

    /// <summary>没接技能数据的主角(<see cref="HeroProgress.HasSkillData"/> 为假)。</summary>
    internal static HeroProgress Bare(int level)
    {
        return new HeroProgress(Table(), Sp(), Growth(), Rewards(1, 400), ExpRates.Neutral, level, -1, 0);
    }

    /// <summary>主角技能簿的全貌(点数、各技能等级、快捷栏六格),拿来比两个主角是不是同一个状态。</summary>
    internal static string State(HeroProgress hero)
    {
        var sb = new StringBuilder();
        sb.Append("sp=").Append(hero.Skills.Sp).Append(" sfp=").Append(hero.Skills.Sfp).Append(" levels=");
        foreach (KeyValuePair<int, int> learned in hero.Skills.Learned)
        {
            sb.Append(learned.Key).Append(':').Append(learned.Value).Append(',');
        }
        sb.Append(" bar=");
        for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
        {
            sb.Append(hero.Skills.QuickAt(slot)).Append(',');
        }
        return sb.ToString();
    }

    /// <summary>快捷栏六格的编号,逗号隔开(空格是 0)。</summary>
    internal static string Bar(HeroProgress hero)
    {
        var sb = new StringBuilder();
        for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
        {
            sb.Append(slot > 0 ? "," : "").Append(hero.Skills.QuickAt(slot));
        }
        return sb.ToString();
    }
}

/// <summary>
/// 技能学习服务的单个与批量规则(<see cref="SkillLearning"/>):委托、批量全成全不成、一次能学几级。
/// 期望值都是照规则手算的(注释里有算式),不是从实现里抄的。
/// </summary>
public class SkillLearningTests
{
    private static HeroProgress Hero10() => LearningFixture.Hero(LearningFixture.Tree(), 10, LearningFixture.TreeChr());

    private static SkillStep[] Steps(params (int Id, int Levels)[] steps) => steps.Select(s => new SkillStep(s.Id, s.Levels)).ToArray();

    /// <summary>10 级主角已学:901 到 3 级(6 点)、902 一级(4 点)、903 一级(5 点)——共 15 点,剩 40;快捷栏 901 在 0 格、903 在 1 格。</summary>
    private static HeroProgress Learned10()
    {
        HeroProgress hero = Hero10();
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(901, 2, out _));
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(902, 1, out _));
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(903, 1, out _));
        Assert.Equal(40, hero.Skills.Sp);
        return hero;
    }

    // ------------------------------------------------------------------ 单个:委托,不重写规则

    [Fact]
    public void SingleOperationsKeepTheSameBooksAsCallingTheHeroDirectly()
    {
        // 孪生主角:一个走服务、一个直接调 HeroProgress,每一步的结果、点数与终态都一样
        HeroProgress viaService = Hero10();
        HeroProgress direct = Hero10();
        var service = new SkillLearning(viaService);
        Assert.Equal(55, viaService.SkillPoints);                                          // 1 + … + 10
        Assert.Equal(SkillOpResult.Ok, service.Learn(901, 1, out int spent));              // 第 2 级要求 3 级,3 点
        Assert.Equal(SkillOpResult.Ok, direct.LearnSkill(901, 1, out int spentDirect));
        Assert.Equal(3, spent);
        Assert.Equal(spentDirect, spent);
        Assert.Equal(52, viaService.SkillPoints);
        Assert.Equal(SkillOpResult.NotAllowed, service.Learn(903, 1, out spent));          // 前置 902 没学
        Assert.Equal(0, spent);
        Assert.Equal(SkillOpResult.Ok, service.Forget(901, 1, out int back));
        Assert.Equal(3, back);
        Assert.Equal(SkillOpResult.BelowGiven, service.Forget(901, 1, out back));          // 退到 0 低于白送的 1 级
        Assert.Equal(0, back);
        Assert.Equal(SkillOpResult.Ok, direct.RefundSkill(901, 1, out _));
        Assert.Equal(LearningFixture.State(direct), LearningFixture.State(viaService));
        Assert.Equal(SkillOpResult.Ok, service.Learn(901, 2, out _));                      // 6 点
        service.Reset();
        direct.ResetSkills();
        Assert.Equal(LearningFixture.State(direct), LearningFixture.State(viaService));
        Assert.Equal(55, viaService.SkillPoints);                                          // 重置回到该级总额
        Assert.Equal(1, viaService.Skills.LevelOf(901));
    }

    [Fact]
    public void ChecksChangeNothingAndAnIncrementBelowOneIsUnknown()
    {
        HeroProgress hero = Hero10();
        var service = new SkillLearning(hero);
        int version = hero.SkillsVersion;
        string before = LearningFixture.State(hero);
        Assert.Equal(SkillOpResult.Ok, service.CheckLearn(901, 1, out int cost));
        Assert.Equal(3, cost);
        Assert.Equal(SkillOpResult.Ok, service.CheckLearn(901, 2, out cost));              // 第 3 级要求 5 级,10 级够
        Assert.Equal(6, cost);
        Assert.Equal(SkillOpResult.BelowGiven, service.CheckForget(901, 1, out int refund));
        Assert.Equal(0, refund);
        Assert.Equal(version, hero.SkillsVersion);
        Assert.Equal(before, LearningFixture.State(hero));
        // 增量小于 1:什么都学不了、退不了(与技能簿的增量检查一致)
        Assert.Equal(SkillOpResult.Unknown, service.Learn(901, 0, out int spent));
        Assert.Equal(0, spent);
        Assert.Equal(SkillOpResult.Unknown, service.Forget(901, -1, out refund));
        Assert.Equal(before, LearningFixture.State(hero));
    }

    [Fact]
    public void AHeroWithoutSkillDataLearnsNothingAndNullsAreRejected()
    {
        var service = new SkillLearning(Progress(level: 5));                               // 没接技能数据
        Assert.Equal(SkillOpResult.Unknown, service.Learn(901, 1, out _));
        Assert.Equal(SkillOpResult.Unknown, service.Forget(901, 1, out _));
        SkillBatchResult batch = service.LearnAll(Steps((901, 1)));
        Assert.Equal(SkillOpResult.Unknown, batch.Result);
        Assert.Equal(0, batch.FailedIndex);
        Assert.Equal(0, service.LearnableLevels(901));
        Assert.Throws<ArgumentNullException>(() => new SkillLearning(null));
        Assert.Throws<ArgumentNullException>(() => service.LearnAll(null));
        Assert.Throws<ArgumentNullException>(() => service.TryForgetAll(null));
    }

    // ------------------------------------------------------------------ 批量学:全成全不成

    [Fact]
    public void ABatchSucceedsWhenALaterStepNeedsAnEarlierOne()
    {
        HeroProgress hero = Hero10();
        var service = new SkillLearning(hero);
        int version = hero.SkillsVersion;
        // 901 由 1 级到 2 级:3 点;902 一级:4 点;903 一级:5 点(前置 901 两级与 902 一级,都被前面的两步满足)——共 12 点
        SkillBatchResult result = service.LearnAll(Steps((901, 1), (902, 1), (903, 1)));
        Assert.True(result.Ok);
        Assert.Equal(-1, result.FailedIndex);
        Assert.Equal(12, result.Sp);
        Assert.Equal(0, result.Sfp);
        Assert.Equal("Ok", result.ResultName);
        Assert.Equal(43, hero.SkillPoints);                                                // 55 − 12
        Assert.Equal("sp=43 sfp=0 levels=901:2,902:1,903:1, bar=901,903,0,0,0,0,", LearningFixture.State(hero));   // 903 是新学的主动技能,放进第一个空格
        Assert.Equal(version + 1, hero.SkillsVersion);                                     // 整批一次换进去,只记一次变化
    }

    [Fact]
    public void TheOrderOfTheStepsMatters()
    {
        HeroProgress hero = Hero10();
        var service = new SkillLearning(hero);
        string before = LearningFixture.State(hero);
        int version = hero.SkillsVersion;
        // 次序反了:第 0 步 903 的前置还没学,不成
        SkillBatchResult result = service.LearnAll(Steps((903, 1), (902, 1), (901, 1)));
        Assert.False(result.Ok);
        Assert.Equal(SkillOpResult.NotAllowed, result.Result);
        Assert.Equal(0, result.FailedIndex);
        Assert.Equal(0, result.Sp);
        Assert.Equal(before, LearningFixture.State(hero));
        Assert.Equal(version, hero.SkillsVersion);
    }

    [Fact]
    public void AFailureAnywhereLeavesNoTraceInTheBook()
    {
        HeroProgress hero = Hero10();
        var service = new SkillLearning(hero);
        string before = LearningFixture.State(hero);
        int version = hero.SkillsVersion;
        // 前两步都成(901 升 2 级 3 点、902 一级 4 点),第三步 905 要觉醒点,10 级主角没有(特殊点 0)
        SkillBatchResult result = service.LearnAll(Steps((901, 1), (902, 1), (905, 1)));
        Assert.Equal(SkillOpResult.NotEnoughPoints, result.Result);
        Assert.Equal(2, result.FailedIndex);
        Assert.Equal(0, result.Sp);                                                        // 失败时不报点数
        Assert.Equal(0, result.Sfp);
        Assert.Equal(before, LearningFixture.State(hero));                                 // 前两步没留下:等级、点数、快捷栏都原样
        Assert.Equal(version, hero.SkillsVersion);
        Assert.Equal(0, hero.Skills.LevelOf(902));
    }

    [Fact]
    public void ThePointsRunOutAtTheStepThatCannotBePaid()
    {
        // 901 第 2 级 3 点、902 第 1 级 4 点、902 第 2 级 4 点(要求 6 级,10 级够)——合计 11 点
        SkillStep[] steps = Steps((901, 1), (902, 1), (902, 1));
        HeroProgress poor = Hero10();
        poor.Skills.SetPoints(10, 0);                                                      // 差 1 点:前两步花 7 点剩 3,第三步要 4 点
        SkillBatchResult failed = new SkillLearning(poor).LearnAll(steps);
        Assert.Equal(SkillOpResult.NotEnoughPoints, failed.Result);
        Assert.Equal(2, failed.FailedIndex);
        Assert.Equal(10, poor.SkillPoints);
        HeroProgress exact = Hero10();
        exact.Skills.SetPoints(11, 0);                                                     // 刚好 11 点:成,余额 0
        SkillBatchResult ok = new SkillLearning(exact).LearnAll(steps);
        Assert.True(ok.Ok);
        Assert.Equal(11, ok.Sp);
        Assert.Equal(0, exact.SkillPoints);
        Assert.Equal(2, exact.Skills.LevelOf(902));
    }

    [Fact]
    public void TheSameSkillTwiceInOneBatchAddsUp()
    {
        HeroProgress twice = Hero10();
        HeroProgress once = Hero10();
        // 901 由 1 级到 2 级(要求 3 级)再到 3 级(要求 5 级):各 3 点,共 6 点;与一步学两级同样
        SkillBatchResult a = new SkillLearning(twice).LearnAll(Steps((901, 1), (901, 1)));
        SkillBatchResult b = new SkillLearning(once).LearnAll(Steps((901, 2)));
        Assert.Equal(6, a.Sp);
        Assert.Equal(6, b.Sp);
        Assert.Equal(3, twice.Skills.LevelOf(901));
        Assert.Equal(49, twice.SkillPoints);
        Assert.Equal(LearningFixture.State(once), LearningFixture.State(twice));
    }

    [Fact]
    public void TheTwoPointPoolsAreAccountedSeparately()
    {
        HeroProgress hero = LearningFixture.Hero(LearningFixture.Tree(), 55, LearningFixture.TreeChr());
        Assert.Equal(1540, hero.SkillPoints);                                              // 55 · 56 / 2
        Assert.Equal(6, hero.AwakeningPoints);                                             // 55 − 49
        // 901 学两级:6 点;905 学两级(要求 10、15 级)每级 1 个觉醒点:2 点
        SkillBatchResult result = new SkillLearning(hero).LearnAll(Steps((901, 2), (905, 2)));
        Assert.True(result.Ok);
        Assert.Equal(6, result.Sp);
        Assert.Equal(2, result.Sfp);
        Assert.Equal(1534, hero.SkillPoints);
        Assert.Equal(4, hero.AwakeningPoints);
    }

    [Fact]
    public void EmptyAndMalformedBatches()
    {
        HeroProgress hero = Hero10();
        var service = new SkillLearning(hero);
        string before = LearningFixture.State(hero);
        int version = hero.SkillsVersion;
        SkillBatchResult empty = service.LearnAll(Steps());
        Assert.True(empty.Ok);
        Assert.Equal(-1, empty.FailedIndex);
        Assert.Equal(0, empty.Sp);
        Assert.Equal(version, hero.SkillsVersion);                                         // 空批什么也不做,不记变化
        // 级数不大于 0 的步不成(位置是它自己),前面成了的 902 也不留
        SkillBatchResult zero = service.LearnAll(Steps((902, 1), (901, 0)));
        Assert.Equal(SkillOpResult.Unknown, zero.Result);
        Assert.Equal(1, zero.FailedIndex);
        SkillBatchResult unknown = service.LearnAll(Steps((999, 1)));
        Assert.Equal(SkillOpResult.Unknown, unknown.Result);
        Assert.Equal(0, unknown.FailedIndex);
        Assert.Equal(before, LearningFixture.State(hero));
        Assert.Equal(version, hero.SkillsVersion);
    }

    [Fact]
    public void TryOnlyRehearsesAndAgreesWithTheRealBatch()
    {
        SkillStep[] good = Steps((901, 1), (902, 1), (903, 1));
        SkillStep[] bad = Steps((901, 1), (905, 1));
        foreach (SkillStep[] steps in new[] { good, bad })
        {
            HeroProgress rehearsed = Hero10();
            HeroProgress real = Hero10();
            string before = LearningFixture.State(rehearsed);
            int version = rehearsed.SkillsVersion;
            SkillBatchResult a = new SkillLearning(rehearsed).TryLearnAll(steps);
            SkillBatchResult b = new SkillLearning(real).LearnAll(steps);
            Assert.Equal(before, LearningFixture.State(rehearsed));                        // 成功的演算也不动手
            Assert.Equal(version, rehearsed.SkillsVersion);
            Assert.Equal(b.Result, a.Result);
            Assert.Equal(b.FailedIndex, a.FailedIndex);
            Assert.Equal(b.Sp, a.Sp);
            Assert.Equal(b.Sfp, a.Sfp);
        }
    }

    [Fact]
    public void ABatchEndsInTheSameStateAsLearningOneByOne()
    {
        HeroProgress batch = Hero10();
        HeroProgress oneByOne = Hero10();
        new SkillLearning(batch).LearnAll(Steps((901, 2), (902, 2), (903, 1), (902, 1)));
        Assert.Equal(SkillOpResult.Ok, oneByOne.LearnSkill(901, 2, out _));
        Assert.Equal(SkillOpResult.Ok, oneByOne.LearnSkill(902, 2, out _));
        Assert.Equal(SkillOpResult.Ok, oneByOne.LearnSkill(903, 1, out _));
        Assert.Equal(SkillOpResult.Ok, oneByOne.LearnSkill(902, 1, out _));                // 902 第 3 级要求 9 级,10 级够
        Assert.Equal(LearningFixture.State(oneByOne), LearningFixture.State(batch));
        // 901 两级 3 + 3、902 两级 4 + 4、903 一级 5、902 再一级 4,共 23 点:55 − 23 = 32
        Assert.Equal("sp=32 sfp=0 levels=901:3,902:3,903:1, bar=901,903,0,0,0,0,", LearningFixture.State(batch));
    }

    // ------------------------------------------------------------------ 批量退:全成全不成

    [Fact]
    public void ForgettingTheDependentFirstWorksButTheOtherWayRoundFails()
    {
        // 先退依赖者 903(还 5 点)再退被依赖的 902(还 4 点):成
        HeroProgress hero = Learned10();
        var service = new SkillLearning(hero);
        SkillBatchResult ok = service.ForgetAll(Steps((903, 1), (902, 1)));
        Assert.True(ok.Ok);
        Assert.Equal(9, ok.Sp);
        Assert.Equal("sp=49 sfp=0 levels=901:3, bar=901,0,0,0,0,0,", LearningFixture.State(hero));   // 903 退到 0 级,清掉它在快捷栏的格
        // 次序反了:902 被已学的 903 需要
        HeroProgress other = Learned10();
        string before = LearningFixture.State(other);
        SkillBatchResult failed = new SkillLearning(other).ForgetAll(Steps((902, 1), (903, 1)));
        Assert.Equal(SkillOpResult.NeededByOther, failed.Result);
        Assert.Equal(0, failed.FailedIndex);
        Assert.Equal(before, LearningFixture.State(other));
    }

    [Fact]
    public void RefundsStopAtTheGivenLevelAndAtWhatOthersNeed()
    {
        // 已学 901 三级、903 一级(要求 901 至少两级)
        HeroProgress hero = Learned10();
        var service = new SkillLearning(hero);
        Assert.Equal(SkillOpResult.BelowGiven, service.ForgetAll(Steps((901, 3))).Result);        // 退到 0 级低于白送的 1 级
        Assert.Equal(SkillOpResult.NeededByOther, service.ForgetAll(Steps((901, 2))).Result);     // 退到 1 级低于 903 要求的 2 级
        SkillBatchResult edge = service.ForgetAll(Steps((901, 1)));                                // 退到 2 级:刚好够 903 的要求,3 点
        Assert.True(edge.Ok);
        Assert.Equal(3, edge.Sp);
        Assert.Equal(2, hero.Skills.LevelOf(901));
        Assert.Equal(43, hero.SkillPoints);
        Assert.Equal(SkillOpResult.NotLearned, service.ForgetAll(Steps((904, 1))).Result);        // 没学过的退不了
    }

    [Fact]
    public void AFailedForgetBatchKeepsTheQuickBarSlotItWouldHaveCleared()
    {
        HeroProgress hero = Learned10();
        var service = new SkillLearning(hero);
        Assert.Equal(903, hero.Skills.QuickAt(1));
        string before = LearningFixture.State(hero);
        int version = hero.SkillsVersion;
        // 第 0 步把 903 退到 0 级(会清掉第 1 格),第 1 步 901 退 3 级低于白送等级:整批不成,903 与它的格子都得还在
        SkillBatchResult result = service.ForgetAll(Steps((903, 1), (901, 3)));
        Assert.Equal(SkillOpResult.BelowGiven, result.Result);
        Assert.Equal(1, result.FailedIndex);
        Assert.Equal(903, hero.Skills.QuickAt(1));
        Assert.Equal(1, hero.Skills.LevelOf(903));
        Assert.Equal(before, LearningFixture.State(hero));
        Assert.Equal(version, hero.SkillsVersion);
    }

    [Fact]
    public void RefundedPointsGoBackToTheirOwnPool()
    {
        HeroProgress hero = LearningFixture.Hero(LearningFixture.Tree(), 55, LearningFixture.TreeChr());
        var service = new SkillLearning(hero);
        Assert.True(service.LearnAll(Steps((905, 2))).Ok);                                 // 觉醒点 6 → 4
        SkillBatchResult result = service.ForgetAll(Steps((905, 1)));                      // 退第 2 级:还 1 个觉醒点
        Assert.True(result.Ok);
        Assert.Equal(0, result.Sp);
        Assert.Equal(1, result.Sfp);
        Assert.Equal(5, hero.AwakeningPoints);
        Assert.Equal(1540, hero.SkillPoints);
    }

    [Fact]
    public void TryForgetOnlyRehearsAndABatchEndsLikeRefundingOneByOne()
    {
        HeroProgress rehearsed = Learned10();
        string before = LearningFixture.State(rehearsed);
        int version = rehearsed.SkillsVersion;
        SkillBatchResult tried = new SkillLearning(rehearsed).TryForgetAll(Steps((903, 1), (902, 1)));
        Assert.True(tried.Ok);
        Assert.Equal(9, tried.Sp);
        Assert.Equal(before, LearningFixture.State(rehearsed));
        Assert.Equal(version, rehearsed.SkillsVersion);
        HeroProgress batch = Learned10();
        HeroProgress oneByOne = Learned10();
        new SkillLearning(batch).ForgetAll(Steps((903, 1), (902, 1), (901, 1)));
        Assert.Equal(SkillOpResult.Ok, oneByOne.RefundSkill(903, 1, out _));
        Assert.Equal(SkillOpResult.Ok, oneByOne.RefundSkill(902, 1, out _));
        Assert.Equal(SkillOpResult.Ok, oneByOne.RefundSkill(901, 1, out _));
        Assert.Equal(LearningFixture.State(oneByOne), LearningFixture.State(batch));
    }

    // ------------------------------------------------------------------ 一次能学几级

    [Fact]
    public void TheLevelIntervalLimitsHowManyLevelsFitAtOnce()
    {
        // 901 现在 1 级(白送),每级要求主角 1 + 2(n − 1) 级、最高 5 级;点数管够:能学到的级数 = 能到的最高级 − 1
        // 主角等级 → 能到的最高级:1、2 级 → 1(第 2 级要 3 级);3、4 → 2;5、6 → 3;7、8 → 4;9 以上 → 5(最高级封顶)
        (int Hero, int Expected)[] table = { (1, 0), (2, 0), (3, 1), (4, 1), (5, 2), (6, 2), (7, 3), (8, 3), (9, 4), (10, 4), (20, 4) };
        foreach ((int level, int expected) in table)
        {
            HeroProgress hero = LearningFixture.Hero(LearningFixture.Tree(), level, LearningFixture.TreeChr());
            hero.Skills.SetPoints(1000, 0);
            Assert.Equal(expected, new SkillLearning(hero).LearnableLevels(901));
        }
    }

    [Fact]
    public void TheLevelsAreCappedByThePointsToo()
    {
        // 9 级主角:等级允许再学 4 级;每级 3 点
        (int Points, int Expected)[] table = { (0, 0), (2, 0), (3, 1), (5, 1), (6, 2), (8, 2), (9, 3), (11, 3), (12, 4), (1000, 4) };
        foreach ((int points, int expected) in table)
        {
            HeroProgress hero = LearningFixture.Hero(LearningFixture.Tree(), 9, LearningFixture.TreeChr());
            hero.Skills.SetPoints(points, 0);
            Assert.Equal(expected, new SkillLearning(hero).LearnableLevels(901));
        }
    }

    [Fact]
    public void ThePrerequisitesAndTheGrowtypeAndSpecialPointsDecideToo()
    {
        // 903 要求 901 两级与 902 一级:没满足时一级也学不了,满足之后 5 级起、间隔 1、最高 5,10 级主角能学到 5 级(要求 5、6、7、8、9 级)
        HeroProgress hero = LearningFixture.Hero(LearningFixture.Tree(), 10, LearningFixture.TreeChr());
        hero.Skills.SetPoints(1000, 0);
        var service = new SkillLearning(hero);
        Assert.Equal(0, service.LearnableLevels(903));
        Assert.True(service.LearnAll(Steps((901, 1), (902, 1))).Ok);
        Assert.Equal(5, service.LearnableLevels(903));
        // 904 只有成长类型 1 学得了:成长类型 0 的主角一级也学不了;成长类型 1(白送 1 级、最高 3 级)2 级主角能到第 2 级(要求 2 级),所以还能学 1 级
        Assert.Equal(0, service.LearnableLevels(904));
        HeroProgress second = LearningFixture.Hero(LearningFixture.Tree(), 2, LearningFixture.TreeChr(), growtype: 1);
        second.Skills.SetPoints(1000, 0);
        Assert.Equal(1, second.Skills.LevelOf(904));
        Assert.Equal(1, new SkillLearning(second).LearnableLevels(904));
        // 905 是特殊技能:看觉醒点,不看技能点;55 级主角觉醒点 6 点,要求 10、15、20 级都够,最高 3 级,每级 1 点
        HeroProgress awake = LearningFixture.Hero(LearningFixture.Tree(), 55, LearningFixture.TreeChr());
        Assert.Equal(3, new SkillLearning(awake).LearnableLevels(905));
        awake.Skills.SetPoints(1000, 2);
        Assert.Equal(2, new SkillLearning(awake).LearnableLevels(905));
        awake.Skills.SetPoints(0, 6);
        Assert.Equal(3, new SkillLearning(awake).LearnableLevels(905));                    // 技能点 0 也不碍事
        // 不认识的技能是 0
        Assert.Equal(0, service.LearnableLevels(999));
    }

    [Fact]
    public void LearningTheMaxSpendsExactlyWhatTheLevelsCostAndThenThereIsNothingLeftToLearn()
    {
        HeroProgress hero = LearningFixture.Hero(LearningFixture.Tree(), 9, LearningFixture.TreeChr());
        hero.Skills.SetPoints(7, 0);                                                       // 点数只够 2 级(6 点)
        var service = new SkillLearning(hero);
        Assert.Equal(2, service.LearnMax(901, out int spent));
        Assert.Equal(6, spent);
        Assert.Equal(3, hero.Skills.LevelOf(901));
        Assert.Equal(1, hero.SkillPoints);
        Assert.Equal(0, service.LearnMax(901, out spent));                                 // 余额 1 点,第 4 级要 3 点
        Assert.Equal(0, spent);
        hero.Skills.SetPoints(1000, 0);
        Assert.Equal(2, service.LearnMax(901, out spent));                                 // 9 级主角到 5 级封顶:再学 2 级
        Assert.Equal(6, spent);
        Assert.Equal(5, hero.Skills.LevelOf(901));
        Assert.Equal(0, service.LearnableLevels(901));                                     // 已经满级
        Assert.Equal(0, service.LearnMax(999, out spent));
    }
}
