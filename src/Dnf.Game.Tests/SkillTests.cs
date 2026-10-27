using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 学技能的规则与状态(M35,证据 E-127):.skl 里学技能相关的标签、<see cref="SkillRules"/> 的公式、<see cref="SkillCatalog"/>、
/// <see cref="SkillGrants"/> 与 <see cref="SkillBook"/> 的学、退、重置与快捷栏。数字与技能全是合成的,不含原版内容。
/// </summary>
public sealed class SkillTests
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

    /// <summary>造一个合成技能:要求等级、等级间隔、六个成长类型的最高级、花费表,其余按需要加。</summary>
    private static SkillDef Skill(int id, int req, int range, int[] max, int[] cost, string type = "[active]",
        int cls = 1, int[] fit = null, int[] pre = null, int[] special = null)
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", type),
            Ints("[required level]", req),
            Ints("[required level range]", range),
            Ints("[growtype maximum level]", max),
            Ints("[skill class]", cls),
            Ints("[skill fitness growtype]", fit ?? new[] { 1 }),
        };
        if (cost != null)
        {
            lines.Add(Ints("[purchase cost]", cost));
        }
        if (special != null)
        {
            lines.Add(Ints("[feature skill type]", 1));
            lines.Add(Ints("[special purchase cost]", special));
        }
        if (pre != null)
        {
            lines.Add(Ints("[pre required skill]", pre));
        }
        return new SkillDef(id, "skill/test/s" + id + ".skl", lines.ToArray());
    }

    private static readonly int[] AllFive = { 5, 5, 5, 5, 5, 5 };

    /// <summary>
    /// 一棵小技能树:1 基础主动(要求 1 级、间隔 2 级、最高 5 级、每级 10 点);2 进阶主动(要求 10 级、间隔 5、最高 3、花费 20/30/40,前置 1 号 2 级);
    /// 3 被动(要求 5 级、间隔 5、最高 3、每级 15 点);4 特殊主动(50 级起、特殊点每级 1);5 只有第一个成长类型学得了的主动。
    /// </summary>
    private static SkillCatalog Tree()
    {
        return new SkillCatalog(0, new[]
        {
            Skill(1, 1, 2, AllFive, new[] { 10 }),
            Skill(2, 10, 5, new[] { 3, 3, 3, 3, 3, 3 }, new[] { 20, 30, 40 }, pre: new[] { 1, 2 }),
            Skill(3, 5, 5, new[] { 3, 3, 3, 3, 3, 3 }, new[] { 15 }, type: "[passive]", cls: 4),
            Skill(4, 50, 1, new[] { 3, 3, 3, 3, 3, 3 }, null, special: new[] { 1 }),
            Skill(5, 1, 1, new[] { 4, 0, 0, 0, 0, 0 }, new[] { 5 }),
        });
    }

    // ---------------- .skl 里学技能相关的标签 ----------------

    [Fact]
    public void SkillDefReadsTheLearningTags()
    {
        var def = new SkillDef(7, "skill/t/a.skl", new[]
        {
            Str("[type]", "[active]"),
            Ints("[maximum level]", 4),
            Ints("[required level]", 12),
            Ints("[required level range]", 3),
            Ints("[skill class]", 2),
            Ints("[skill fitness growtype]", 1, 2),
            Ints("[growtype maximum level]", 0, 6, 6, 3, 3, 3),
            Ints("[purchase cost]", 11),
            Ints("[pre required skill]", 4, 2, 9, 1),
        });
        Assert.True(def.IsActive);
        Assert.Equal(12, def.RequiredLevel);
        Assert.Equal(3, def.RequiredLevelRange);
        Assert.Equal(2, def.SkillClass);
        Assert.Equal(new[] { 1, 2 }, def.FitGrowtypes);
        Assert.Equal(new[] { 0, 6, 6, 3, 3, 3 }, def.GrowtypeMaxLevels);
        Assert.Equal(new[] { 11 }, def.PurchaseCost);
        Assert.False(def.IsSpecial);
        Assert.Equal(2, def.PreRequired.Count);
        Assert.Equal(4, def.PreRequired[0].SkillId);
        Assert.Equal(2, def.PreRequired[0].Level);
        Assert.Equal(9, def.PreRequired[1].SkillId);
        // 有效最高级:[maximum level] 低于某个成长类型的最高级时被抬高(服务端载入时的修正)
        Assert.Equal(6, def.EffectiveMaxLevel);
    }

    [Fact]
    public void SkillDefTagsThatTheServerOverwritesTakeTheLastOccurrence()
    {
        var def = new SkillDef(8, "skill/t/b.skl", new[]
        {
            Ints("[purchase cost]", 5),
            Ints("[purchase cost]", 8, 9),
            Ints("[pre required skill]", 1, 1, 2, 2),
            Ints("[pre required skill]", 3, 3),
            Ints("[feature skill type]", 1),
            Ints("[special purchase cost]", 2),
            Ints("[skill class]", 1),
            Ints("[skill class]", 4),
        });
        Assert.Equal(new[] { 8, 9 }, def.PurchaseCost);
        Assert.Single(def.PreRequired);
        Assert.Equal(3, def.PreRequired[0].SkillId);
        Assert.True(def.IsSpecial);
        Assert.Equal(new[] { 2 }, def.SpecialPurchaseCost);
        Assert.Equal(4, def.SkillClass);
    }

    [Fact]
    public void SpecialSkillFlagIsTheLowByteOfTheFeatureType()
    {
        Assert.False(new SkillDef(1, "skill/t/c.skl", new[] { Ints("[feature skill type]", 0) }).IsSpecial);
        Assert.False(new SkillDef(1, "skill/t/c.skl", new[] { Ints("[feature skill type]", 256) }).IsSpecial);
        Assert.True(new SkillDef(1, "skill/t/c.skl", new[] { Ints("[feature skill type]", 2) }).IsSpecial);
        Assert.False(new SkillDef(1, "skill/t/c.skl", new ScriptLine[0]).IsSpecial);
    }

    // ---------------- 公式 ----------------

    [Fact]
    public void TypeMaxLevelReadsTheGrowtypeAndIgnoresOutOfRangeOnes()
    {
        SkillDef def = Skill(1, 1, 1, new[] { 0, 7, 6, 5, 4, 3 }, new[] { 1 });
        Assert.Equal(0, SkillRules.TypeMaxLevel(def, 0));
        Assert.Equal(7, SkillRules.TypeMaxLevel(def, 1));
        Assert.Equal(3, SkillRules.TypeMaxLevel(def, 5));
        Assert.Equal(0, SkillRules.TypeMaxLevel(def, 6));
        Assert.Equal(0, SkillRules.TypeMaxLevel(def, -1));
        Assert.Equal(0, SkillRules.TypeMaxLevel(null, 1));
    }

    [Theory]
    [InlineData(10, 1, 1, true)]    // 刚好够第一级
    [InlineData(9, 1, 1, false)]    // 不到要求等级
    [InlineData(14, 1, 2, false)]   // 第二级要求 10 + 5
    [InlineData(15, 1, 2, true)]
    [InlineData(30, 1, 5, true)]    // 第五级要求 10 + 5 × 4
    [InlineData(29, 1, 5, false)]
    [InlineData(99, 1, 6, false)]   // 超出这个成长类型的最高级
    [InlineData(99, 1, 0, false)]
    [InlineData(99, 1, -2, false)]
    [InlineData(99, 0, 1, false)]   // 下标 0 的成长类型这个技能最高级为 0
    [InlineData(99, -1, 1, false)]
    [InlineData(99, 6, 1, false)]   // 成长类型越界
    public void CanLearnFollowsTheServerFormula(int heroLevel, int growtype, int target, bool expected)
    {
        SkillDef def = Skill(1, 10, 5, new[] { 0, 5, 5, 5, 5, 5 }, new[] { 1 });
        Assert.Equal(expected, SkillRules.CanLearn(def, heroLevel, growtype, target));
    }

    [Fact]
    public void CanLearnNeedsBothTheAllowedCapAndTheLevelStep()
    {
        // 间隔 1:每多一级多要求一个等级,12 级可以学到第 3 级(要求 10 + 2)
        SkillDef dense = Skill(1, 10, 1, AllFive, new[] { 1 });
        Assert.True(SkillRules.CanLearn(dense, 12, 1, 3));
        Assert.False(SkillRules.CanLearn(dense, 12, 1, 4));
        // 间隔 5:12 级时"主角等级 − 要求等级 + 1"也是 3,但第 3 级要求 20 级,所以不行
        SkillDef sparse = Skill(2, 10, 5, AllFive, new[] { 1 });
        Assert.False(SkillRules.CanLearn(sparse, 12, 1, 3));
        Assert.True(SkillRules.CanLearn(sparse, 12, 1, 1));
        // 没有间隔标签(只有一级的技能):只看要求等级
        SkillDef single = Skill(3, 20, 0, new[] { 1, 1, 1, 1, 1, 1 }, new[] { 1 });
        Assert.True(SkillRules.CanLearn(single, 20, 1, 1));
        Assert.False(SkillRules.CanLearn(single, 19, 1, 1));
        Assert.False(SkillRules.CanLearn(null, 99, 1, 1));
    }

    [Fact]
    public void MaxLearnableIsTheSmallestOfCapLevelAndStep()
    {
        SkillDef def = Skill(1, 10, 5, new[] { 0, 5, 5, 5, 5, 5 }, new[] { 1 });
        Assert.Equal(0, SkillRules.MaxLearnable(def, 5, 1));       // 等级太低:0 级
        Assert.Equal(0, SkillRules.MaxLearnable(def, 9, 1));
        Assert.Equal(1, SkillRules.MaxLearnable(def, 10, 1));
        Assert.Equal(1, SkillRules.MaxLearnable(def, 14, 1));      // 间隔 5:还不到第二级
        Assert.Equal(2, SkillRules.MaxLearnable(def, 15, 1));
        Assert.Equal(5, SkillRules.MaxLearnable(def, 30, 1));
        Assert.Equal(5, SkillRules.MaxLearnable(def, 99, 1));      // 封在成长类型的最高级
        Assert.Equal(-1, SkillRules.MaxLearnable(def, 99, 0));     // 这个成长类型学不了
        SkillDef single = Skill(2, 20, 0, new[] { 1, 1, 1, 1, 1, 1 }, new[] { 1 });
        Assert.Equal(1, SkillRules.MaxLearnable(single, 20, 1));   // 没有间隔标签也不会除零
        Assert.Equal(0, SkillRules.MaxLearnable(single, 19, 1));
        Assert.Equal(20, SkillRules.RequiredHeroLevel(def, 3));      // 要求等级 10 + 间隔 5 × (3 − 1)
        Assert.Equal(10, SkillRules.RequiredHeroLevel(def, 1));
    }

    [Fact]
    public void CostTableIsPaddedWithItsLastValueUpToTheEffectiveMaxLevel()
    {
        // 只写一个数:每级同价,补到最高级(5)那么长
        SkillDef flat = Skill(1, 1, 1, AllFive, new[] { 7 });
        Assert.Equal(7, SkillRules.CostOfLevel(flat, 1));
        Assert.Equal(7, SkillRules.CostOfLevel(flat, 5));
        Assert.Equal(-1, SkillRules.CostOfLevel(flat, 6));          // 补齐后的长度之外
        Assert.Equal(-1, SkillRules.CostOfLevel(flat, 0));
        // 写了几个数:前几级用自己的,后面用最后一个补
        SkillDef steps = Skill(2, 1, 1, AllFive, new[] { 3, 4, 5 });
        Assert.Equal(new[] { 3, 4, 5, 5, 5 }, new[] { 1, 2, 3, 4, 5 }.Select(l => SkillRules.CostOfLevel(steps, l)).ToArray());
        // 花费表比最高级还长:按表的长度
        SkillDef longer = Skill(3, 1, 1, new[] { 2, 2, 2, 2, 2, 2 }, new[] { 1, 2, 3, 4 });
        Assert.Equal(4, SkillRules.CostOfLevel(longer, 4));
        // 没有花费表
        SkillDef none = Skill(4, 1, 1, AllFive, null);
        Assert.Equal(-1, SkillRules.CostOfLevel(none, 1));
        Assert.Equal(-1, SkillRules.CostOfLevel(null, 1));
    }

    [Fact]
    public void NeedPointsSumsTheCostsOfTheLevelsBought()
    {
        SkillDef flat = Skill(1, 1, 1, AllFive, new[] { 7 });
        Assert.Equal(21, SkillRules.NeedPoints(flat, 0, 3));
        Assert.Equal(21, SkillRules.NeedPoints(flat, 2, 5));
        Assert.Equal(-1, SkillRules.NeedPoints(flat, 0, 6));        // 有一级没有花费
        Assert.Equal(0, SkillRules.NeedPoints(flat, 3, 3));
        Assert.Equal(0, SkillRules.NeedPoints(flat, 4, 2));
        Assert.Equal(-1, SkillRules.NeedPoints(flat, -1, 2));
        Assert.Equal(-1, SkillRules.NeedPoints(flat, 0, 0));
        SkillDef steps = Skill(2, 1, 1, AllFive, new[] { 3, 4, 5 });
        Assert.Equal(22, SkillRules.NeedPoints(steps, 0, 5));
        Assert.Equal(9, SkillRules.NeedPoints(steps, 1, 3));
        Assert.Equal(-1, SkillRules.NeedPoints(Skill(3, 1, 1, AllFive, null), 0, 1));
        // 特殊技能花的是特殊点,用特殊花费表
        SkillDef special = Skill(4, 50, 1, AllFive, new[] { 100 }, special: new[] { 2, 3 });
        Assert.Equal(2 + 3 + 3, SkillRules.NeedPoints(special, 0, 3));
    }

    [Fact]
    public void UiGroupFollowsTheServerReform()
    {
        Assert.Equal(1, SkillRules.UiGroup(Skill(1, 1, 1, AllFive, new[] { 1 }, cls: 0, fit: new[] { 1, 2 })));
        Assert.Equal(1, SkillRules.UiGroup(Skill(1, 1, 1, AllFive, new[] { 1 }, cls: 3, fit: new[] { 1 })));
        Assert.Equal(0, SkillRules.UiGroup(Skill(1, 1, 1, AllFive, new[] { 1 }, cls: 2, fit: new[] { 1, 2, 3 })));
        Assert.Equal(2, SkillRules.UiGroup(Skill(1, 1, 1, AllFive, new[] { 1 }, cls: 4, fit: new[] { 1, 2, 3, 4, 5 })));
        Assert.Equal(3, SkillRules.UiGroup(Skill(1, 1, 1, AllFive, new[] { 1 }, cls: 1, special: new[] { 1 })));
        Assert.Equal(7, SkillRules.UiGroup(Skill(1, 1, 1, AllFive, new[] { 1 }, cls: 7)));
        Assert.Equal(0, SkillRules.UiGroup(null));
    }

    [Fact]
    public void TotalPointsAddUpTheLevelUpTableAndTheAwakeningPoints()
    {
        var table = new SpTable(new[] { 0, 0, 10, 10, 20 });       // 第 1 级不给,2、3 级各 10,4 级 20
        Assert.Equal(0, SkillRules.TotalSp(table, 1));
        Assert.Equal(20, SkillRules.TotalSp(table, 3));
        Assert.Equal(40, SkillRules.TotalSp(table, 4));
        Assert.Equal(40, SkillRules.TotalSp(table, 9));              // 表走完以后不再给
        Assert.Equal(0, SkillRules.TotalSp(null, 4));
        Assert.Equal(0, SkillRules.TotalSfp(49));
        Assert.Equal(1, SkillRules.TotalSfp(50));
        Assert.Equal(11, SkillRules.TotalSfp(60));
    }

    // ---------------- 技能目录 ----------------

    [Fact]
    public void CatalogSortsDropsDuplicatesAndIndexesDependents()
    {
        SkillDef a = Skill(1, 1, 1, AllFive, new[] { 1 });
        SkillDef b = Skill(2, 1, 1, AllFive, new[] { 1 }, pre: new[] { 1, 3 });
        SkillDef c = Skill(3, 1, 1, AllFive, new[] { 1 }, pre: new[] { 1, 1, 2, 2 });
        SkillDef dup = Skill(1, 99, 1, AllFive, new[] { 1 });
        var catalog = new SkillCatalog(8, new[] { c, a, dup, b, null });      // 同编号的以先出现的为准
        Assert.Equal(8, catalog.Job);
        Assert.Equal(3, catalog.Count);
        Assert.Equal(new[] { 1, 2, 3 }, catalog.Skills.Select(s => s.Id).ToArray());
        Assert.Same(a, catalog.Find(1));
        Assert.Null(catalog.Find(9));
        IReadOnlyList<SkillDependent> onOne = catalog.DependentsOf(1);
        Assert.Equal(2, onOne.Count);
        Assert.Equal(2, onOne[0].SkillId);
        Assert.Equal(3, onOne[0].RequiresLevel);
        Assert.Equal(3, onOne[1].SkillId);
        Assert.Equal(1, onOne[1].RequiresLevel);
        Assert.Single(catalog.DependentsOf(2));
        Assert.Empty(catalog.DependentsOf(3));
        Assert.Empty(catalog.DependentsOf(99));
    }

    [Fact]
    public void CatalogListsTheSkillsAGrowtypeCanLearn()
    {
        SkillCatalog catalog = Tree();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, catalog.ForGrowtype(0).Select(s => s.Id).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, catalog.ForGrowtype(1).Select(s => s.Id).ToArray());
        Assert.Empty(catalog.ForGrowtype(9));
    }

    [Fact]
    public void CatalogLoadsTheJobsSkillListFromGameData()
    {
        var fx = new Fixture();
        fx.List("skill/skilllist.lst", (8, "testskill.lst"));
        fx.List("skill/testskill.lst", (40, "test/b.skl"), (15, "test/foo.skl"), (3, "test/a.skl"));
        fx.Tokens("skill/test/a.skl", w => w.Tag("[type]").Str("[active]").Tag("[required level]").Ints(3));
        fx.Tokens("skill/test/foo.skl", w => w.Tag("[type]").Str("[active]"));
        fx.Tokens("skill/test/b.skl", w => w.Tag("[type]").Str("[passive]"));
        var data = new GameData(fx.Build());
        Assert.Equal(new[] { 3, 15, 40 }, data.GetSkillIds(8));
        Assert.Empty(data.GetSkillIds(99));
        SkillCatalog catalog = SkillCatalog.Load(data, 8);
        Assert.Equal(3, catalog.Count);
        Assert.Equal(3, catalog.Find(3).RequiredLevel);
        Assert.False(catalog.Find(40).IsActive);
        Assert.Equal(0, SkillCatalog.Load(data, 99).Count);
        Assert.Equal(0, SkillCatalog.Load(null, 1).Count);
    }

    // ---------------- 白送的技能 ----------------

    [Fact]
    public void GrantsAreTheInitialListPlusTheChosenGrowtypeBlock()
    {
        ScriptLine[] chr =
        {
            Ints("[skill]", 99, 9),                 // 在任何块之前:忽略
            Ints("[initial value]"),
            Ints("[skill]", 1, 1, 2, 1),
            Ints("[growtype 1]"),
            Ints("[growtype 2]"),
            Ints("[skill]", 2, 3, 7, 1),
            Ints("[awakening 1]"),
            Ints("[skill]", 8, 8),                  // 觉醒块不读
            Ints("[growtype 3]"),
            Ints("[skill]", 9, 9),
        };
        List<SkillGrant> none = SkillGrants.Read(chr, 0);
        Assert.Equal(new[] { 1, 2 }, none.Select(g => g.SkillId).ToArray());
        List<SkillGrant> second = SkillGrants.Read(chr, 1);
        Assert.Equal(new[] { 1, 2, 7 }, second.Select(g => g.SkillId).ToArray());
        Assert.Equal(new[] { 1, 3, 1 }, second.Select(g => g.Level).ToArray());   // 同一个技能取较高的等级,位置不变
        Assert.Equal(new[] { 1, 2, 9 }, SkillGrants.Read(chr, 2).Select(g => g.SkillId).ToArray());
        Assert.Equal(new[] { 1, 2 }, SkillGrants.Read(chr, 5).Select(g => g.SkillId).ToArray());
        Assert.Empty(SkillGrants.Read(null, 0));
    }

    [Fact]
    public void GrantsSkipZeroLevels()
    {
        ScriptLine[] chr = { Ints("[initial value]"), Ints("[skill]", 1, 0, 2, 4) };
        Assert.Equal(new[] { 2 }, SkillGrants.Read(chr, 0).Select(g => g.SkillId).ToArray());
    }

    // ---------------- 学 ----------------

    [Fact]
    public void LearningSpendsPointsRaisesTheLevelAndPlacesActiveSkillsOnTheBar()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(100, 0);
        int version = book.Version;
        Assert.Equal(SkillOpResult.Ok, book.Learn(1, 1, 1, 1, out int spent));
        Assert.Equal(10, spent);
        Assert.Equal(90, book.Sp);
        Assert.Equal(1, book.LevelOf(1));
        Assert.Equal(1, book.QuickAt(0));
        Assert.True(book.Version > version);
        // 第二级要求 1 + 2 = 3 级
        Assert.Equal(SkillOpResult.NotAllowed, book.Learn(1, 2, 1, 1, out spent));
        Assert.Equal(0, spent);
        Assert.Equal(90, book.Sp);
        Assert.Equal(SkillOpResult.Ok, book.Learn(1, 3, 1, 1, out _));
        Assert.Equal(2, book.LevelOf(1));
        Assert.Equal(80, book.Sp);
        Assert.Equal(0, book.QuickAt(1));      // 已经在栏里,不会再放第二格
        // 被动技能学了不上栏
        Assert.Equal(SkillOpResult.Ok, book.Learn(3, 5, 1, 1, out _));
        Assert.Equal(-1, book.QuickSlotOf(3));
        Assert.Equal(2, book.LearnedCount);
    }

    [Fact]
    public void LearningChecksInTheServerOrder()
    {
        var book = new SkillBook(Tree());
        Assert.Equal(SkillOpResult.Unknown, book.CheckLearn(99, 99, 1, 1, out _));
        Assert.Equal(SkillOpResult.Unknown, book.CheckLearn(1, 99, 1, 0, out _));
        // 等级不够:不管有没有点数
        Assert.Equal(SkillOpResult.NotAllowed, book.CheckLearn(2, 5, 1, 1, out _));
        // 等级够了、点数不够:先报点数不够(前置技能的检查在点数之后)
        Assert.Equal(SkillOpResult.NotEnoughPoints, book.CheckLearn(2, 10, 1, 1, out _));
        // 点数够了、前置没学够:不允许
        book.AddPoints(500, 0);
        Assert.Equal(SkillOpResult.NotAllowed, book.CheckLearn(2, 10, 1, 1, out _));
        // 前置学到 2 级以后可以
        Assert.Equal(SkillOpResult.Ok, book.Learn(1, 10, 1, 2, out int spent));
        Assert.Equal(20, spent);
        Assert.Equal(SkillOpResult.Ok, book.CheckLearn(2, 10, 1, 1, out int cost));
        Assert.Equal(20, cost);
        // 这个成长类型学不了的技能(5 号只有下标 0 的成长类型学得了)
        Assert.Equal(SkillOpResult.NotAllowed, book.CheckLearn(5, 99, 1, 1, out _));
        Assert.Equal(SkillOpResult.Ok, book.CheckLearn(5, 99, 0, 1, out _));
    }

    [Fact]
    public void CheckingDoesNotChangeAnything()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(100, 0);
        int version = book.Version;
        Assert.Equal(SkillOpResult.Ok, book.CheckLearn(1, 1, 1, 1, out int cost));
        Assert.Equal(10, cost);
        Assert.Equal(version, book.Version);
        Assert.Equal(100, book.Sp);
        Assert.Equal(0, book.LevelOf(1));
    }

    [Fact]
    public void SpecialSkillsSpendSpecialPoints()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(100, 0);
        Assert.Equal(SkillOpResult.NotEnoughPoints, book.Learn(4, 50, 1, 1, out _));
        book.AddPoints(0, 2);
        Assert.Equal(SkillOpResult.Ok, book.Learn(4, 50, 1, 1, out int spent));
        Assert.Equal(1, spent);
        Assert.Equal(1, book.Sfp);
        Assert.Equal(100, book.Sp);
        Assert.Equal(SkillOpResult.Ok, book.Learn(4, 51, 1, 1, out _));
        Assert.Equal(0, book.Sfp);
        Assert.Equal(2, book.LevelOf(4));
    }

    [Fact]
    public void BatchLearningIsAllOrNothing()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(100, 0);
        // 3 级时最多学到第二级,一次学三级不成,什么都不动
        Assert.Equal(SkillOpResult.NotAllowed, book.Learn(1, 3, 1, 3, out _));
        Assert.Equal(0, book.LevelOf(1));
        Assert.Equal(100, book.Sp);
        // 5 级够到第三级
        Assert.Equal(SkillOpResult.Ok, book.Learn(1, 5, 1, 3, out int spent));
        Assert.Equal(30, spent);
        Assert.Equal(3, book.LevelOf(1));
        Assert.Equal(70, book.Sp);
        // 点数不够时整批不成
        book.SetPoints(15, 0);
        Assert.Equal(SkillOpResult.NotEnoughPoints, book.Learn(1, 9, 1, 2, out _));
        Assert.Equal(3, book.LevelOf(1));
        Assert.Equal(15, book.Sp);
    }

    [Fact]
    public void ABarThatIsFullKeepsNewSkillsOffTheBar()
    {
        var skills = new List<SkillDef>();
        for (int i = 1; i <= 8; i++)
        {
            skills.Add(Skill(i, 1, 1, AllFive, new[] { 1 }));
        }
        var book = new SkillBook(new SkillCatalog(0, skills));
        book.AddPoints(100, 0);
        for (int i = 1; i <= 8; i++)
        {
            Assert.Equal(SkillOpResult.Ok, book.Learn(i, 5, 1, 1, out _));
        }
        for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
        {
            Assert.Equal(slot + 1, book.QuickAt(slot));
        }
        Assert.Equal(-1, book.QuickSlotOf(7));
        Assert.Equal(-1, book.QuickSlotOf(8));
    }

    // ---------------- 退 ----------------

    [Fact]
    public void RefundingReturnsThePointsOfTheLevelsGivenBack()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(100, 0);
        book.Learn(1, 5, 1, 3, out _);
        Assert.Equal(70, book.Sp);
        Assert.Equal(SkillOpResult.Ok, book.Refund(1, 1, out int back));
        Assert.Equal(10, back);
        Assert.Equal(80, book.Sp);
        Assert.Equal(2, book.LevelOf(1));
        // 退到 0 级:从技能表与快捷栏拿掉
        Assert.Equal(SkillOpResult.Ok, book.Refund(1, 2, out back));
        Assert.Equal(20, back);
        Assert.Equal(100, book.Sp);
        Assert.Equal(0, book.LevelOf(1));
        Assert.Equal(0, book.LearnedCount);
        Assert.Equal(-1, book.QuickSlotOf(1));
        // 没学过、退得比学的多、没这个技能
        Assert.Equal(SkillOpResult.NotLearned, book.Refund(1, 1, out _));
        book.Learn(1, 5, 1, 1, out _);
        Assert.Equal(SkillOpResult.BelowGiven, book.Refund(1, 2, out _));
        Assert.Equal(SkillOpResult.Unknown, book.Refund(99, 1, out _));
        Assert.Equal(SkillOpResult.Unknown, book.Refund(1, 0, out _));
    }

    [Fact]
    public void RefundingNeverGoesBelowTheGivenLevel()
    {
        var book = new SkillBook(Tree());
        book.SetGiven(new[] { new SkillGrant(1, 2) });
        Assert.Equal(2, book.LevelOf(1));
        Assert.Equal(2, book.GivenLevelOf(1));
        Assert.Equal(SkillOpResult.BelowGiven, book.Refund(1, 1, out int back));
        Assert.Equal(0, back);
        book.AddPoints(100, 0);
        book.Learn(1, 9, 1, 2, out _);                      // 学到 4 级
        Assert.Equal(SkillOpResult.Ok, book.Refund(1, 2, out back));   // 退回白送的 2 级
        Assert.Equal(20, back);
        Assert.Equal(SkillOpResult.BelowGiven, book.Refund(1, 1, out _));
    }

    [Fact]
    public void RefundingRespectsTheLevelsOtherLearnedSkillsRequire()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(500, 0);
        book.Learn(1, 10, 1, 3, out _);
        Assert.Equal(SkillOpResult.Ok, book.Learn(2, 10, 1, 1, out _));      // 2 号要求 1 号至少 2 级
        Assert.Equal(SkillOpResult.Ok, book.CheckRefund(1, 1, out _));      // 3 → 2 还够
        Assert.Equal(SkillOpResult.NeededByOther, book.CheckRefund(1, 2, out _));
        Assert.Equal(SkillOpResult.NeededByOther, book.Refund(1, 3, out _));
        Assert.Equal(3, book.LevelOf(1));
        // 把依赖它的技能退光以后就可以退
        Assert.Equal(SkillOpResult.Ok, book.Refund(2, 1, out _));
        Assert.Equal(SkillOpResult.Ok, book.Refund(1, 3, out _));
        Assert.Equal(0, book.LevelOf(1));
    }

    [Fact]
    public void SpecialRefundsGoBackToTheSpecialPool()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(10, 5);
        book.Learn(4, 52, 1, 3, out _);
        Assert.Equal(2, book.Sfp);
        Assert.Equal(SkillOpResult.Ok, book.Refund(4, 2, out int back));
        Assert.Equal(2, back);
        Assert.Equal(4, book.Sfp);
        Assert.Equal(10, book.Sp);
    }

    // ---------------- 白送、重置 ----------------

    [Fact]
    public void GivenSkillsFillTheBarInTheirOrderAndPassivesStayOff()
    {
        var book = new SkillBook(Tree());
        book.SetGiven(new[] { new SkillGrant(3, 1), new SkillGrant(2, 1), new SkillGrant(1, 1) });
        Assert.Equal(3, book.LearnedCount);
        Assert.Equal(2, book.QuickAt(0));
        Assert.Equal(1, book.QuickAt(1));
        Assert.Equal(0, book.QuickAt(2));
        // 目录里没有的技能与 0 级的不收
        book.SetGiven(new[] { new SkillGrant(77, 1), new SkillGrant(1, 0) });
        Assert.Equal(0, book.GivenLevelOf(77));
        Assert.Equal(0, book.GivenLevelOf(1));
    }

    [Fact]
    public void ResettingKeepsOnlyTheGivenSkillsAndRestoresThePoints()
    {
        var book = new SkillBook(Tree());
        var given = new[] { new SkillGrant(1, 1) };
        book.SetGiven(given);
        book.AddPoints(100, 0);
        book.Learn(1, 10, 1, 4, out _);
        book.Learn(3, 10, 1, 2, out _);
        book.Learn(2, 10, 1, 1, out _);
        Assert.Equal(3, book.LearnedCount);
        book.Reset(given, 100, 0);
        Assert.Equal(1, book.LearnedCount);
        Assert.Equal(1, book.LevelOf(1));
        Assert.Equal(1, book.QuickAt(0));
        Assert.Equal(0, book.QuickAt(1));
        Assert.Equal(100, book.Sp);
        Assert.Equal(0, book.Sfp);
        // 转职:换一份白送的
        book.Reset(new[] { new SkillGrant(5, 2), new SkillGrant(2, 1) }, 80, 3);
        Assert.Equal(2, book.LearnedCount);
        Assert.Equal(0, book.LevelOf(1));
        Assert.Equal(2, book.LevelOf(5));
        Assert.Equal(5, book.QuickAt(0));
        Assert.Equal(2, book.QuickAt(1));
        Assert.Equal(80, book.Sp);
        Assert.Equal(3, book.Sfp);
    }

    // ---------------- 快捷栏 ----------------

    [Fact]
    public void AssigningMovesSwapsAndRejects()
    {
        var skills = new List<SkillDef>
        {
            Skill(1, 1, 1, AllFive, new[] { 1 }),
            Skill(2, 1, 1, AllFive, new[] { 1 }),
            Skill(3, 1, 1, AllFive, new[] { 1 }),
            Skill(4, 1, 1, AllFive, new[] { 1 }, type: "[passive]"),
        };
        var book = new SkillBook(new SkillCatalog(0, skills));
        book.AddPoints(100, 0);
        book.Learn(1, 5, 1, 1, out _);
        book.Learn(2, 5, 1, 1, out _);
        book.Learn(4, 5, 1, 1, out _);
        Assert.Equal(1, book.QuickAt(0));
        Assert.Equal(2, book.QuickAt(1));
        // 已在栏里的技能挪到别的格:两格互换
        Assert.True(book.AssignQuick(1, 1));
        Assert.Equal(2, book.QuickAt(0));
        Assert.Equal(1, book.QuickAt(1));
        // 挪到空格:原来的格腾空
        Assert.True(book.AssignQuick(4, 1));
        Assert.Equal(0, book.QuickAt(1));
        Assert.Equal(1, book.QuickAt(4));
        // 没学会的、被动的、越界的不收
        Assert.False(book.AssignQuick(2, 3));
        Assert.False(book.AssignQuick(2, 4));
        Assert.False(book.AssignQuick(6, 1));
        Assert.False(book.AssignQuick(-1, 1));
        Assert.False(book.AssignQuick(2, 99));
        // 放在同一格不算变化也算成功
        int version = book.Version;
        Assert.True(book.AssignQuick(4, 1));
        Assert.Equal(version, book.Version);
        // 新学会的技能自动放进第一个空格(第 1 格),再与第 0 格互换
        book.Learn(3, 5, 1, 1, out _);
        Assert.Equal(3, book.QuickAt(1));
        Assert.True(book.AssignQuick(0, 3));
        Assert.Equal(3, book.QuickAt(0));
        Assert.Equal(2, book.QuickAt(1));
        // 下了栏的技能再上栏:顶掉那一格原来的技能(被顶掉的不再在栏里)
        Assert.True(book.ClearQuick(1));
        Assert.Equal(-1, book.QuickSlotOf(2));
        Assert.True(book.AssignQuick(0, 2));
        Assert.Equal(2, book.QuickAt(0));
        Assert.Equal(-1, book.QuickSlotOf(3));
        Assert.False(book.ClearQuick(1));      // 空格
        Assert.False(book.ClearQuick(9));      // 越界
    }

    // ---------------- 存档还原 ----------------

    [Fact]
    public void RestoreValidatesTheSavedRows()
    {
        var book = new SkillBook(Tree());
        book.SetGiven(new[] { new SkillGrant(1, 2) });
        var levels = new[]
        {
            new KeyValuePair<int, int>(1, 1),        // 低于白送的 2 级:补回 2
            new KeyValuePair<int, int>(2, 2),
            new KeyValuePair<int, int>(3, 99),       // 超过有效最高级:夹住
            new KeyValuePair<int, int>(77, 1),       // 目录里没有:丢掉
            new KeyValuePair<int, int>(4, 0),        // 0 级:丢掉
        };
        var quick = new[]
        {
            new KeyValuePair<int, int>(0, 2),
            new KeyValuePair<int, int>(1, 1),
            new KeyValuePair<int, int>(2, 1),        // 同一个技能只留第一格
            new KeyValuePair<int, int>(3, 3),        // 被动:丢掉
            new KeyValuePair<int, int>(4, 4),        // 没学:丢掉
            new KeyValuePair<int, int>(9, 1),        // 越界:丢掉
        };
        int dropped = book.Restore(levels, quick, 33, 4);
        Assert.Equal(2, book.LevelOf(1));
        Assert.Equal(2, book.LevelOf(2));
        Assert.Equal(3, book.LevelOf(3));
        Assert.Equal(0, book.LevelOf(77));
        Assert.Equal(2, book.QuickAt(0));
        Assert.Equal(1, book.QuickAt(1));
        Assert.Equal(0, book.QuickAt(2));
        Assert.Equal(33, book.Sp);
        Assert.Equal(4, book.Sfp);
        Assert.Equal(2 + 4, dropped);
        // 空的存档:只剩白送的,栏是空的(玩家清空过栏的话就保持清空)
        Assert.Equal(0, book.Restore(null, null, 0, 0));
        Assert.Equal(1, book.LearnedCount);
        Assert.Equal(0, book.QuickAt(0));
    }

    [Fact]
    public void PointsNeverGoNegative()
    {
        var book = new SkillBook(Tree());
        book.AddPoints(5, 1);
        book.AddPoints(-50, -50);
        Assert.Equal(0, book.Sp);
        Assert.Equal(0, book.Sfp);
        book.SetPoints(-3, -4);
        Assert.Equal(0, book.Sp);
        Assert.Equal(0, book.Sfp);
    }
}
