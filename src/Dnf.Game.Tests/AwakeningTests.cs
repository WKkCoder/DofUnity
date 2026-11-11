using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 觉醒(第二成长类型,r15-awaken)的规则与技能侧:<see cref="AwakeningRules"/> 的门槛与次序、.skl 的 <c>[second growtype maximum level]</c>、
/// 觉醒后的最高级(<see cref="SkillRules.TypeMaxLevel(SkillDef, int, int)"/> 一族)、.chr 里 <c>[awakening 1]</c>、<c>[awakening 2]</c> 的 <c>[awakening skill]</c> 白送列表、
/// 技能簿并入白送(<see cref="SkillBook.GrantGiven"/>)与读档上界(<see cref="SkillDef.SaveCeilingLevel"/>)。
/// 数字与编号全是合成的(技能 901 起),只检验规则的形状,与原版数据无关。
/// </summary>
public class AwakeningTests
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
    private static readonly int[] Nothing = { 0, 0, 0, 0, 0, 0 };

    /// <summary>
    /// 合成技能:要求等级 1、间隔 1(第 n 级要求主角 n 级);<paramref name="second"/> 为 null 就不写 [second growtype maximum level];
    /// <paramref name="special"/> 非 null 时是特殊技能(花觉醒点)。
    /// </summary>
    private static SkillDef Skill(int id, int[] max, int[] second = null, int[] cost = null, int[] special = null, string type = "[active]")
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", type),
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

    /// <summary>12 个第二成长类型最高级:只在 (成长类型, 觉醒) 这一格写 <paramref name="value"/>,别处 0。</summary>
    private static int[] SecondAt(int growtype, int awakening, int value)
    {
        var second = new int[12];
        second[growtype * 2 + awakening - 1] = value;
        return second;
    }

    private static (int Id, int Level)[] Pairs(List<SkillGrant> grants)
    {
        return grants.Select(g => (g.SkillId, g.Level)).ToArray();
    }

    // ------------------------------------------------------------------ 门槛与次序

    [Fact]
    public void TheThresholdIsFiftyWhereTheAwakeningBlocksStartToApplyAndThePointsStartToAccrue()
    {
        Assert.Equal(50, AwakeningRules.MinLevel);
        Assert.Equal(CharacterGrowth.AwakeningFromLevel, AwakeningRules.MinLevel);
        Assert.Equal(Leveling.AwakeningFirstLevel, AwakeningRules.MinLevel);
        Assert.Equal(2, AwakeningRules.Count);
        Assert.Equal(CharacterGrowth.AwakeningCount, AwakeningRules.Count);
        Assert.Equal(AwakeningRules.Count, SkillRules.AwakeningCount);
        Assert.Equal(2, AwakeningRules.QuestFlag);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(49)]
    public void BelowFiftyNoOneCanAwaken(int level)
    {
        Assert.Equal(AwakeningCheck.LevelTooLow, AwakeningRules.Check(level, 1, 0, 1));
        Assert.Equal(AwakeningCheck.LevelTooLow, AwakeningRules.Check(level, 3, 1, 2));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(85)]
    [InlineData(99)]
    [InlineData(1000)]
    public void FromFiftyTheNextAwakeningIsAllowed(int level)
    {
        Assert.Equal(AwakeningCheck.Ok, AwakeningRules.Check(level, 1, 0, 1));
        Assert.Equal(AwakeningCheck.Ok, AwakeningRules.Check(level, 1, 1, 2));
    }

    [Theory]
    [InlineData(0, 1, AwakeningCheck.Ok)]
    [InlineData(1, 2, AwakeningCheck.Ok)]
    [InlineData(0, 2, AwakeningCheck.OutOfOrder)]          // 跳级:没有一觉就二觉
    [InlineData(1, 1, AwakeningCheck.OutOfOrder)]          // 重复
    [InlineData(2, 2, AwakeningCheck.OutOfOrder)]
    [InlineData(2, 1, AwakeningCheck.OutOfOrder)]          // 退回
    [InlineData(1, 0, AwakeningCheck.BadTarget)]           // 0 是"没觉醒",不是觉醒的目标
    [InlineData(0, 3, AwakeningCheck.BadTarget)]           // 没有三觉
    [InlineData(2, 3, AwakeningCheck.BadTarget)]
    [InlineData(0, -1, AwakeningCheck.BadTarget)]
    [InlineData(5, 1, AwakeningCheck.OutOfOrder)]          // 现在的觉醒号坏了(越界)也不放行
    public void OnlyTheNextAwakeningInOrderIsAllowed(int current, int target, AwakeningCheck expected)
    {
        Assert.Equal(expected, AwakeningRules.Check(60, 2, current, target));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(100)]
    public void AHeroThatHasNotTransferredOrHasABadGrowtypeCannotAwaken(int growtype)
    {
        Assert.Equal(AwakeningCheck.NotTransferred, AwakeningRules.Check(60, growtype, 0, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryTransferredGrowtypeCanAwaken(int growtype)
    {
        Assert.Equal(AwakeningCheck.Ok, AwakeningRules.Check(50, growtype, 0, 1));
    }

    [Fact]
    public void WhenSeveralThingsAreWrongTheFirstInTheDocumentedOrderIsReported()
    {
        Assert.Equal(AwakeningCheck.BadTarget, AwakeningRules.Check(1, 0, 0, 9));              // 目标先于转职
        Assert.Equal(AwakeningCheck.NotTransferred, AwakeningRules.Check(1, 0, 0, 1));         // 转职先于等级
        Assert.Equal(AwakeningCheck.LevelTooLow, AwakeningRules.Check(49, 1, 0, 2));           // 等级先于次序
        Assert.Equal(AwakeningCheck.OutOfOrder, AwakeningRules.Check(50, 1, 0, 2));
    }

    // ------------------------------------------------------------------ .skl:觉醒后的最高级

    [Fact]
    public void SkillDefReadsTheTwelveSecondGrowtypeLimitsAndTheLastOccurrenceWins()
    {
        var def = new SkillDef(901, "skill/test/s901.skl", new[]
        {
            Ints("[growtype maximum level]", 3, 3, 3, 3, 3, 3),
            Ints("[second growtype maximum level]", 1, 2, 3),
            Ints("[second growtype maximum level]", 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112),
        });
        Assert.Equal(12, def.SecondGrowtypeMaxLevels.Length);
        Assert.Equal(101, def.SecondGrowtypeMaxLevels[0]);
        Assert.Equal(112, def.SecondGrowtypeMaxLevels[11]);
        Assert.Empty(new SkillDef(902, "skill/test/s902.skl", new[] { Ints("[growtype maximum level]", 3, 3, 3, 3, 3, 3) }).SecondGrowtypeMaxLevels);
    }

    [Fact]
    public void TheSecondLimitsDoNotRaiseTheEffectiveMaxLevelButTheyRaiseTheSaveCeiling()
    {
        SkillDef raised = Skill(901, new[] { 3, 3, 3, 3, 3, 3 }, SecondAt(1, 1, 6));
        Assert.Equal(3, raised.EffectiveMaxLevel);             // 服务端只拿成长类型的最高级抬 [maximum level]
        Assert.Equal(6, raised.SaveCeilingLevel);
        Assert.Equal(3, Skill(902, new[] { 3, 3, 3, 3, 3, 3 }).SaveCeilingLevel);                       // 没有第二张表:等于有效最高级
        Assert.Equal(5, Skill(903, new[] { 3, 3, 3, 3, 3, 3 }, SecondAt(1, 1, 5)).SaveCeilingLevel);
        Assert.Equal(3, Skill(904, new[] { 3, 3, 3, 3, 3, 3 }, SecondAt(1, 1, 2)).SaveCeilingLevel);    // 觉醒后更低的不拉低上界
    }

    [Fact]
    public void SecondMaxLevelIsIndexedByGrowtypeTimesTwoPlusAwakeningMinusOne()
    {
        var second = new int[12];
        for (int i = 0; i < 12; i++)
        {
            second[i] = 101 + i;                                // 每格不同:下标错一位就对不上
        }
        SkillDef def = Skill(901, AllFive, second);
        for (int growtype = 0; growtype < SkillRules.GrowtypeCount; growtype++)
        {
            for (int awakening = 1; awakening <= SkillRules.AwakeningCount; awakening++)
            {
                Assert.Equal(101 + growtype * 2 + awakening - 1, SkillRules.SecondMaxLevel(def, growtype, awakening));
            }
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(1, -1)]
    [InlineData(-1, 1)]
    [InlineData(6, 1)]
    [InlineData(99, 2)]
    public void SecondMaxLevelOutsideTheTableIsZero(int growtype, int awakening)
    {
        SkillDef def = Skill(901, AllFive, Enumerable.Repeat(9, 12).ToArray());
        Assert.Equal(0, SkillRules.SecondMaxLevel(def, growtype, awakening));
    }

    [Fact]
    public void SecondMaxLevelWithoutTheTagOrWithAShortTagOrWithoutASkillIsZero()
    {
        Assert.Equal(0, SkillRules.SecondMaxLevel(Skill(901, AllFive), 1, 1));
        SkillDef shortTable = Skill(902, AllFive, new[] { 7, 8, 9 });
        Assert.Equal(9, SkillRules.SecondMaxLevel(shortTable, 1, 1));           // 下标 2
        Assert.Equal(0, SkillRules.SecondMaxLevel(shortTable, 1, 2));           // 下标 3:表没写到
        Assert.Equal(0, SkillRules.SecondMaxLevel(null, 1, 1));
    }

    [Fact]
    public void TheAwakenedMaxLevelFallsBackToTheGrowtypeLimitWhenTheSecondLimitIsZero()
    {
        // 成长类型的最高级都是 5;第二成长类型:(1,1) 抬到 7,(1,2) 没写(0)、(2,1) 降到 3
        var second = new int[12];
        second[2] = 7;
        second[4] = 3;
        SkillDef def = Skill(901, AllFive, second);
        Assert.Equal(5, SkillRules.TypeMaxLevel(def, 1, 0));
        Assert.Equal(7, SkillRules.TypeMaxLevel(def, 1, 1));
        Assert.Equal(5, SkillRules.TypeMaxLevel(def, 1, 2));
        Assert.Equal(3, SkillRules.TypeMaxLevel(def, 2, 1));
        Assert.Equal(5, SkillRules.TypeMaxLevel(def, 2, 2));
        Assert.Equal(5, SkillRules.TypeMaxLevel(def, 3, 1));
        // 觉醒号越界按没觉醒;不带觉醒的老写法与觉醒 0 一致
        Assert.Equal(5, SkillRules.TypeMaxLevel(def, 1, 3));
        Assert.Equal(5, SkillRules.TypeMaxLevel(def, 1, -1));
        Assert.Equal(SkillRules.TypeMaxLevel(def, 1), SkillRules.TypeMaxLevel(def, 1, 0));
    }

    [Fact]
    public void AwakeningCanOpenASkillTheGrowtypeItselfCannotLearn()
    {
        SkillDef only = Skill(901, Nothing, SecondAt(1, 1, 3));
        Assert.Equal(0, SkillRules.TypeMaxLevel(only, 1, 0));
        Assert.Equal(3, SkillRules.TypeMaxLevel(only, 1, 1));
        Assert.Equal(0, SkillRules.TypeMaxLevel(only, 1, 2));        // 二觉没写:回到成长类型本身的 0
        Assert.Equal(0, SkillRules.TypeMaxLevel(only, 2, 1));        // 别的成长类型没写
    }

    [Fact]
    public void CanLearnUsesTheAwakenedLimit()
    {
        SkillDef def = Skill(901, AllFive, new[] { 0, 0, 3, 7, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.True(SkillRules.CanLearn(def, 60, 1, 5));
        Assert.True(SkillRules.CanLearn(def, 60, 1, 0, 5));
        Assert.False(SkillRules.CanLearn(def, 60, 1, 1, 5));         // 一觉后最高级降到 3
        Assert.True(SkillRules.CanLearn(def, 60, 1, 1, 3));
        Assert.False(SkillRules.CanLearn(def, 60, 1, 1, 4));
        Assert.True(SkillRules.CanLearn(def, 60, 1, 2, 7));          // 二觉后抬到 7
        Assert.False(SkillRules.CanLearn(def, 60, 1, 2, 8));
        Assert.False(SkillRules.CanLearn(def, 60, 1, 2, 0));         // 目标不到 1 级
        Assert.False(SkillRules.CanLearn(def, 4, 1, 2, 5));          // 主角等级不够:5 级要主角 5 级
        Assert.True(SkillRules.CanLearn(def, 5, 1, 2, 5));
    }

    [Fact]
    public void MaxLearnableUsesTheAwakenedLimitAndMinusOneWhenNothingIsLearnable()
    {
        SkillDef only = Skill(901, Nothing, SecondAt(1, 1, 3));
        Assert.Equal(-1, SkillRules.MaxLearnable(only, 60, 1));
        Assert.Equal(-1, SkillRules.MaxLearnable(only, 60, 1, 0));
        Assert.Equal(3, SkillRules.MaxLearnable(only, 60, 1, 1));
        Assert.Equal(2, SkillRules.MaxLearnable(only, 2, 1, 1));     // 主角 2 级:最多 2 级(2 − 1 + 1)
        Assert.Equal(-1, SkillRules.MaxLearnable(only, 60, 1, 2));
        SkillDef lowered = Skill(902, AllFive, SecondAt(1, 1, 2));
        Assert.Equal(5, SkillRules.MaxLearnable(lowered, 60, 1, 0));
        Assert.Equal(2, SkillRules.MaxLearnable(lowered, 60, 1, 1));
    }

    [Fact]
    public void TheCatalogListsTheSkillsAnAwakenedGrowtypeCanLearn()
    {
        var catalog = new SkillCatalog(0, new[]
        {
            Skill(901, AllFive),
            Skill(902, Nothing, new[] { 0, 0, 3, 0, 2, 0, 0, 0, 0, 0, 0, 0 }),         // 只有觉醒才学得了:(1,1) 与 (2,1)
            Skill(903, new[] { 0, 4, 0, 0, 0, 0 }),                                      // 只有成长类型 1 学得了
        });
        Assert.Equal(new[] { 901, 903 }, catalog.ForGrowtype(1).Select(d => d.Id).ToArray());
        Assert.Equal(new[] { 901, 903 }, catalog.ForGrowtype(1, 0).Select(d => d.Id).ToArray());
        Assert.Equal(new[] { 901, 902, 903 }, catalog.ForGrowtype(1, 1).Select(d => d.Id).ToArray());
        Assert.Equal(new[] { 901, 903 }, catalog.ForGrowtype(1, 2).Select(d => d.Id).ToArray());      // (1,2) 没写 902
        Assert.Equal(new[] { 901, 902 }, catalog.ForGrowtype(2, 1).Select(d => d.Id).ToArray());
        Assert.Equal(new[] { 901 }, catalog.ForGrowtype(0, 1).Select(d => d.Id).ToArray());
    }

    // ------------------------------------------------------------------ .chr:觉醒白送

    /// <summary>
    /// 合成的 .chr 技能部分:开局 901;成长类型 1(下标 0)没有觉醒块;成长类型 2(下标 1)白送 902,[awakening name] 不是块头,觉醒 1 给 903 与 904(2 级)
    /// (块里的 [skill] 906 不算白送),觉醒 2 给 905;成长类型 3(下标 2)白送 907,觉醒 1 给 908,觉醒 2 给 909 与 903(3 级,与成长类型 2 觉醒 1 重名)。
    /// </summary>
    private static ScriptLine[] Chr()
    {
        return new[]
        {
            Ints("[initial value]"),
            Ints("[skill]", 901, 1),
            Ints("[growtype 1]"),
            Ints("[growtype 2]"),
            Ints("[skill]", 902, 1),
            Str("[awakening name]", "A"),
            Ints("[awakening skill]", 999, 1),                   // 还没有觉醒块:不读
            Ints("[awakening 1]"),
            Ints("[skill]", 906, 1),
            Ints("[awakening skill]", 903, 1, 904, 2),
            Ints("[/awakening skill]"),
            Ints("[awakening 2]"),
            Ints("[awakening skill]", 905, 1),
            Ints("[/awakening skill]"),
            Ints("[growtype 3]"),
            Ints("[skill]", 907, 1),
            Ints("[awakening 1]"),
            Ints("[awakening skill]", 908, 1),
            Ints("[awakening 2]"),
            Ints("[awakening skill]", 909, 1, 903, 3),
        };
    }

    [Fact]
    public void WithoutAwakeningTheGiftsAreTheOldOnes()
    {
        Assert.Equal(new[] { (901, 1), (902, 1) }, Pairs(SkillGrants.Read(Chr(), 1)));
        Assert.Equal(new[] { (901, 1), (902, 1) }, Pairs(SkillGrants.Read(Chr(), 1, 0)));
        Assert.Equal(new[] { (901, 1), (907, 1) }, Pairs(SkillGrants.Read(Chr(), 2)));
        Assert.Equal(new[] { (901, 1) }, Pairs(SkillGrants.Read(Chr(), 0)));
    }

    [Fact]
    public void TheAwakeningGiftsFollowTheGrowtypeOnesOfTheMatchingGrowtypeAndAwakening()
    {
        Assert.Equal(new[] { (901, 1), (902, 1), (903, 1), (904, 2) }, Pairs(SkillGrants.Read(Chr(), 1, 1)));
        Assert.Equal(new[] { (901, 1), (907, 1), (908, 1) }, Pairs(SkillGrants.Read(Chr(), 2, 1)));
    }

    [Fact]
    public void TheSecondAwakeningGivesOnlyItsOwnListNotTheFirstOnesToo()
    {
        Assert.Equal(new[] { (901, 1), (902, 1), (905, 1) }, Pairs(SkillGrants.Read(Chr(), 1, 2)));
        Assert.Equal(new[] { (901, 1), (907, 1), (909, 1), (903, 3) }, Pairs(SkillGrants.Read(Chr(), 2, 2)));
    }

    [Fact]
    public void AwakeningListsDoNotLeakAcrossGrowtypesAndAGrowtypeWithoutBlocksGivesNone()
    {
        Assert.Equal(new[] { (901, 1) }, Pairs(SkillGrants.Read(Chr(), 0, 1)));          // 成长类型 1(下标 0)没有觉醒块
        Assert.Equal(new[] { (901, 1) }, Pairs(SkillGrants.Read(Chr(), 3, 1)));          // 文件里没有下标 3 的块
        Assert.DoesNotContain(SkillGrants.Read(Chr(), 2, 1), g => g.SkillId == 903 || g.SkillId == 904 || g.SkillId == 905);
        Assert.DoesNotContain(SkillGrants.Read(Chr(), 1, 1), g => g.SkillId == 908 || g.SkillId == 909);
    }

    [Fact]
    public void SkillLinesInsideAnAwakeningBlockAndListsBeforeAnyBlockAreNotGifts()
    {
        foreach (int awakening in new[] { 0, 1, 2 })
        {
            List<SkillGrant> grants = SkillGrants.Read(Chr(), 1, awakening);
            Assert.DoesNotContain(grants, g => g.SkillId == 906);            // 觉醒块里的 [skill]
            Assert.DoesNotContain(grants, g => g.SkillId == 999);            // [awakening name] 之后、块头之前的列表
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(99)]
    public void AnAwakeningOutsideOneAndTwoGivesNothingExtra(int awakening)
    {
        Assert.Equal(new[] { (901, 1), (902, 1) }, Pairs(SkillGrants.Read(Chr(), 1, awakening)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-1)]
    public void AListUnderABlockNumberedOutsideOneAndTwoIsNeverGivenToAnyAwakening(int awakening)
    {
        var lines = new[]
        {
            Ints("[growtype 2]"),
            Ints("[skill]", 902, 1),
            Ints("[awakening 3]"),                               // 没有三觉:不是块头,它后面的列表不属于任何觉醒
            Ints("[awakening skill]", 903, 1),
            Ints("[awakening 0]"),                               // 0 是"没觉醒",也不是块头
            Ints("[awakening skill]", 904, 1),
            Ints("[awakening -1]"),
            Ints("[awakening skill]", 905, 1),
        };
        Assert.Equal(new[] { (902, 1) }, Pairs(SkillGrants.Read(lines, 1, awakening)));
    }

    [Fact]
    public void ADuplicateAcrossTheGrowtypeAndAwakeningListsKeepsTheHigherLevelInPlace()
    {
        var lines = new[]
        {
            Ints("[growtype 2]"),
            Ints("[skill]", 902, 1, 903, 4),
            Ints("[awakening 1]"),
            Ints("[awakening skill]", 902, 3, 903, 2, 904, 0, 905, 1, 906),    // 902 抬到 3;903 低于已有的不动;904 是 0 级不算;末尾落单的 906 忽略
        };
        Assert.Equal(new[] { (902, 3), (903, 4), (905, 1) }, Pairs(SkillGrants.Read(lines, 1, 1)));
    }

    [Fact]
    public void WithoutChrLinesThereAreNoGifts()
    {
        Assert.Empty(SkillGrants.Read(null, 1, 1));
    }

    // ------------------------------------------------------------------ 成长表:觉醒段(与 CharacterGrowth 既有读法对读)

    /// <summary>
    /// 合成成长表(.chr 的数乘 10 是十分之一单位):初始生命 1000;[growtype 1](下标 0)每级 +20;[growtype 2](下标 1)每级 +30,觉醒 1 +40、觉醒 2 +50;
    /// [growtype 3](下标 2)每级 +60,觉醒 1 +70、觉醒 2 +80。每格不同,下标错一位就对不上。
    /// </summary>
    private static ScriptLine[] GrowthLines()
    {
        return new[]
        {
            Ints("[initial value]"), Ints("[HP MAX]", 1000),
            Ints("[growtype 1]"), Ints("[HP MAX]", 20),
            Ints("[growtype 2]"), Ints("[HP MAX]", 30),
            Ints("[awakening 1]"), Ints("[HP MAX]", 40),
            Ints("[awakening 2]"), Ints("[HP MAX]", 50),
            Ints("[growtype 3]"), Ints("[HP MAX]", 60),
            Ints("[awakening 1]"), Ints("[HP MAX]", 70),
            Ints("[awakening 2]"), Ints("[HP MAX]", 80),
        };
    }

    [Fact]
    public void EachAwakeningBlockIsReadForItsOwnGrowtypeAndAwakeningNumber()
    {
        CharacterGrowth growth = CharacterGrowth.Parse(GrowthLines());
        Assert.Equal(300, growth.Increment(60, 1, 0).HpMax);
        Assert.Equal(400, growth.Increment(60, 1, 1).HpMax);
        Assert.Equal(500, growth.Increment(60, 1, 2).HpMax);
        Assert.Equal(600, growth.Increment(60, 2, 0).HpMax);
        Assert.Equal(700, growth.Increment(60, 2, 1).HpMax);
        Assert.Equal(800, growth.Increment(60, 2, 2).HpMax);
        Assert.Equal(200, growth.Increment(60, 0, 0).HpMax);
        Assert.Equal(0, growth.Increment(60, 0, 1).HpMax);              // 成长类型 1(下标 0)没有觉醒块:增量是 0
        Assert.Equal(0, growth.Increment(60, 3, 1).HpMax);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(99)]
    public void AnAwakeningNumberOutsideOneAndTwoReadsThePlainGrowtypeBlock(int awakening)
    {
        CharacterGrowth growth = CharacterGrowth.Parse(GrowthLines());
        for (int growtype = 0; growtype < CharacterGrowth.GrowtypeCount; growtype++)
        {
            Assert.Equal(growth.Increment(60, growtype, 0).HpMax, growth.Increment(60, growtype, awakening).HpMax);
            Assert.Equal(growth.StatsAt(70, growtype, 0).HpMax, growth.StatsAt(70, growtype, awakening).HpMax);
        }
        Assert.Equal(300, growth.Increment(60, 1, awakening).HpMax);
    }

    [Fact]
    public void AnAwakeningTagBeforeAnyGrowtypeBlockIsNotABlockAndBreaksNothing()
    {
        CharacterGrowth growth = CharacterGrowth.Parse(new[]
        {
            Ints("[awakening 1]"), Ints("[HP MAX]", 9),              // 任何块之前:忽略
            Ints("[initial value]"), Ints("[HP MAX]", 1000),
            Ints("[awakening 2]"), Ints("[HP MAX]", 8),              // 还没有成长类型块:觉醒标签不开块
            Ints("[growtype 1]"), Ints("[HP MAX]", 20),
            Ints("[awakening 1]"), Ints("[HP MAX]", 40),             // 这一块才是真正的觉醒 1
        });
        Assert.True(growth.HasInitial);
        Assert.Equal(1, growth.GrowtypeBlocks);
        Assert.Equal(200, growth.Growtype(0).HpMax);
        Assert.Equal(400, growth.Increment(60, 0, 1).HpMax);
        Assert.Equal(0, growth.Increment(60, 0, 2).HpMax);               // 觉醒 2 那个标签没开块
        Assert.Equal(0, growth.Increment(60, 1, 1).HpMax);
    }

    // ------------------------------------------------------------------ 技能簿

    private static SkillBook Book(params SkillDef[] defs)
    {
        return new SkillBook(new SkillCatalog(0, defs));
    }

    [Fact]
    public void GrantGivenMergesNewGiftsWithoutTouchingWhatWasLearnedOrThePoints()
    {
        SkillBook book = Book(Skill(901, AllFive), Skill(902, AllFive), Skill(903, AllFive, type: "[passive]"));
        book.SetGiven(new[] { new SkillGrant(901, 1) });
        book.SetPoints(10, 4);
        Assert.Equal(SkillOpResult.Ok, book.Learn(901, 60, 1, 1, out _));            // 901 到 2 级,花 1 点
        int version = book.Version;
        book.GrantGiven(new[] { new SkillGrant(902, 1), new SkillGrant(903, 2), new SkillGrant(901, 1), new SkillGrant(999, 1) });
        Assert.Equal(2, book.LevelOf(901));                  // 学过的不动
        Assert.Equal(1, book.GivenLevelOf(901));
        Assert.Equal(1, book.LevelOf(902));
        Assert.Equal(1, book.GivenLevelOf(902));
        Assert.Equal(2, book.LevelOf(903));
        Assert.Equal(0, book.LevelOf(999));                  // 目录里没有的忽略
        Assert.Equal(0, book.GivenLevelOf(999));
        Assert.Equal(9, book.Sp);                            // 点数一分不动
        Assert.Equal(4, book.Sfp);
        Assert.True(book.Version > version);
        Assert.Equal(901, book.QuickAt(0));                  // 新得到的主动技能放进空格;被动不上栏
        Assert.Equal(902, book.QuickAt(1));
        Assert.Equal(0, book.QuickAt(2));
        Assert.Equal(SkillOpResult.BelowGiven, book.Refund(902, 1, out _));          // 觉醒送的也是退技能的下限
        Assert.Equal(SkillOpResult.BelowGiven, book.Refund(903, 1, out _));
    }

    [Fact]
    public void GrantGivenRaisesALowerLearnedLevelToTheGiftAndNeverLowersAHigherFloor()
    {
        SkillBook book = Book(Skill(901, AllFive));
        book.SetGiven(new[] { new SkillGrant(901, 3) });
        book.SetPoints(10, 0);
        book.GrantGiven(new[] { new SkillGrant(901, 1) });
        Assert.Equal(3, book.GivenLevelOf(901));             // 下限取较高的
        Assert.Equal(3, book.LevelOf(901));
        book.GrantGiven(new[] { new SkillGrant(901, 4) });
        Assert.Equal(4, book.GivenLevelOf(901));
        Assert.Equal(4, book.LevelOf(901));                  // 补到给出的等级,不花点数
        Assert.Equal(10, book.Sp);
    }

    [Fact]
    public void GrantGivenWithNothingStillCountsAsAChangeAndNullIsHarmless()
    {
        SkillBook book = Book(Skill(901, AllFive));
        int version = book.Version;
        book.GrantGiven(null);
        Assert.True(book.Version > version);
        Assert.Equal(0, book.LearnedCount);
    }

    [Fact]
    public void SetGivenStillReplacesTheWholeGiftTable()
    {
        SkillBook book = Book(Skill(901, AllFive), Skill(902, AllFive));
        book.SetGiven(new[] { new SkillGrant(901, 2) });
        book.GrantGiven(new[] { new SkillGrant(902, 1) });
        book.SetGiven(new[] { new SkillGrant(901, 1) });
        Assert.Equal(1, book.GivenLevelOf(901));
        Assert.Equal(0, book.GivenLevelOf(902));             // 换了一张表:旧的下限不留
    }

    [Fact]
    public void LearnHonorsTheAwakenedLimitAndTheOldOverloadMeansNotAwakened()
    {
        // 902 号是只有觉醒才开的技能:花费表要自己写够长(载入时只补到有效最高级,而有效最高级不含觉醒后的上限,与服务端一致)
        SkillBook book = Book(Skill(901, AllFive, SecondAt(1, 1, 3)), Skill(902, Nothing, SecondAt(1, 1, 3), cost: new[] { 1, 1, 1 }));
        book.SetPoints(30, 0);
        Assert.Equal(SkillOpResult.Ok, book.CheckLearn(901, 60, 1, 4, out _));               // 老写法:不觉醒,最高 5
        Assert.Equal(SkillOpResult.Ok, book.CheckLearn(901, 60, 1, 0, 4, out _));
        Assert.Equal(SkillOpResult.NotAllowed, book.CheckLearn(901, 60, 1, 1, 4, out _));    // 一觉后最高 3
        Assert.Equal(SkillOpResult.Ok, book.CheckLearn(901, 60, 1, 1, 3, out int cost));
        Assert.Equal(3, cost);
        Assert.Equal(SkillOpResult.NotAllowed, book.Learn(902, 60, 1, 1, out _));            // 觉醒前学不了
        Assert.Equal(SkillOpResult.NotAllowed, book.Learn(902, 60, 1, 0, 1, out _));
        Assert.Equal(SkillOpResult.Ok, book.Learn(902, 60, 1, 1, 2, out int spent));         // 觉醒后能学
        Assert.Equal(2, spent);
        Assert.Equal(2, book.LevelOf(902));
        Assert.Equal(28, book.Sp);
        Assert.Equal(902, book.QuickAt(0));
    }

    [Fact]
    public void SpecialSkillsSpendAwakeningPointsAndNeverTakeThemBelowZero()
    {
        SkillBook book = Book(Skill(901, AllFive, special: new[] { 2 }));
        book.SetPoints(99, 3);
        Assert.Equal(SkillOpResult.Ok, book.Learn(901, 60, 1, 1, 1, out int spent));
        Assert.Equal(2, spent);
        Assert.Equal(1, book.Sfp);
        Assert.Equal(99, book.Sp);                                      // 特殊技能不动技能点
        Assert.Equal(SkillOpResult.NotEnoughPoints, book.Learn(901, 60, 1, 1, 1, out spent));
        Assert.Equal(0, spent);
        Assert.Equal(1, book.Sfp);                                      // 不够就一分不扣
        Assert.Equal(1, book.LevelOf(901));
        Assert.Equal(SkillOpResult.Ok, book.Refund(901, 1, out int back));
        Assert.Equal(2, back);
        Assert.Equal(3, book.Sfp);
        book.AddPoints(0, -50);
        Assert.Equal(0, book.Sfp);                                      // 减到底只到 0
        book.SetPoints(-4, -9);
        Assert.Equal((0, 0), (book.Sp, book.Sfp));
    }

    [Fact]
    public void RestoreKeepsLevelsUpToTheAwakenedCeilingAndClampsAboveIt()
    {
        SkillDef raised = Skill(901, new[] { 3, 3, 3, 3, 3, 3 }, SecondAt(1, 1, 6), cost: new[] { 1, 1, 1, 1, 1, 1 });
        SkillDef plain = Skill(902, new[] { 3, 3, 3, 3, 3, 3 });
        SkillBook book = Book(raised, plain);
        book.Restore(new[] { new KeyValuePair<int, int>(901, 6), new KeyValuePair<int, int>(902, 6) }, null, 0, 0);
        Assert.Equal(6, book.LevelOf(901));                            // 觉醒后学到的 6 级读档不能削回 3
        Assert.Equal(3, book.LevelOf(902));                            // 没有第二张表的照旧夹在有效最高级
        book.Restore(new[] { new KeyValuePair<int, int>(901, 9) }, null, 0, 0);
        Assert.Equal(6, book.LevelOf(901));                            // 超过上界的夹回上界
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(30, 0)]
    [InlineData(48, 0)]
    [InlineData(49, 0)]
    [InlineData(50, 1)]
    [InlineData(51, 2)]
    [InlineData(60, 11)]
    [InlineData(85, 36)]
    public void TheAwakeningPointsAreOnePerLevelFromFiftyAndNeverNegativeBelowIt(int level, int expected)
    {
        Assert.Equal(expected, SkillRules.TotalSfp(level));
    }

    [Fact]
    public void AnExactBalanceOfAwakeningPointsPaysForASpecialSkillAndOnePointLessDoesNot()
    {
        SkillBook book = Book(Skill(901, AllFive, special: new[] { 2 }));
        book.SetPoints(0, 1);
        Assert.Equal(SkillOpResult.NotEnoughPoints, book.Learn(901, 60, 1, 1, 1, out int spent));
        Assert.Equal(0, spent);
        Assert.Equal(1, book.Sfp);
        book.SetPoints(0, 2);
        Assert.Equal(SkillOpResult.Ok, book.Learn(901, 60, 1, 1, 1, out spent));        // 刚好够:花光
        Assert.Equal(2, spent);
        Assert.Equal(0, book.Sfp);
        Assert.Equal(1, book.LevelOf(901));
    }

    [Fact]
    public void AnExactBalanceOfSkillPointsPaysForAnOrdinarySkillToo()
    {
        SkillBook book = Book(Skill(901, AllFive, cost: new[] { 3 }));
        book.SetPoints(2, 99);
        Assert.Equal(SkillOpResult.NotEnoughPoints, book.Learn(901, 60, 1, 1, 1, out _));
        book.SetPoints(3, 0);
        Assert.Equal(SkillOpResult.Ok, book.Learn(901, 60, 1, 1, 1, out int spent));
        Assert.Equal(3, spent);
        Assert.Equal((0, 0), (book.Sp, book.Sfp));                                   // 普通技能花的是技能点,觉醒点分文不动
    }

    [Fact]
    public void ResetAndRestoreNeverLeaveNegativePoints()
    {
        SkillBook book = Book(Skill(901, AllFive));
        book.Reset(new[] { new SkillGrant(901, 1) }, -3, -7);
        Assert.Equal((0, 0), (book.Sp, book.Sfp));
        Assert.Equal(1, book.LevelOf(901));
        book.SetPoints(5, 5);
        book.Restore(new[] { new KeyValuePair<int, int>(901, 2) }, null, -3, -7);
        Assert.Equal((0, 0), (book.Sp, book.Sfp));
        Assert.Equal(2, book.LevelOf(901));
        book.Restore(new[] { new KeyValuePair<int, int>(901, 2) }, null, 4, 6);
        Assert.Equal((4, 6), (book.Sp, book.Sfp));                                   // 正数原样
    }
}
