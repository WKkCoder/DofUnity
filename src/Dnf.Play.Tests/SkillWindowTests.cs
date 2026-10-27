using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 技能窗口(M35,证据 E-127;<see cref="SkillWindow"/>、<see cref="SkillWindowArt"/>、流程里的 K 键):分页与排序、选择与滚动、学与退与重置的结果与提示、
/// 快捷栏摆放、鼠标点击判定、与别的窗口的互斥,以及画法(标签、不出屏、占位词与中文词表)。技能与点数全是合成的:点数表是"第 L 级给 L 点"。
/// </summary>
public class SkillWindowTests
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

    private static ScriptLine Keyed(string tag, int kind, string key)
    {
        return new ScriptLine(tag, new[] { new ScriptValue(ScriptValueKind.Keyed, kind, key) });
    }

    private static SkillDef Skill(int id, int req, int range, int[] max, int cost, bool active = true, int[] pre = null, int cls = 0, int[] fit = null,
        bool special = false, bool icon = false)
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", active ? "[active]" : "[passive]"),
            Keyed("[name]", 6, "n" + id),
            Keyed("[explain]", 6, "e" + id),
            Ints("[required level]", req),
            Ints("[required level range]", range),
            Ints("[growtype maximum level]", max),
            Ints("[skill class]", cls),
            Ints("[skill fitness growtype]", fit ?? new[] { 0, 1, 2, 3 }),
        };
        lines.Add(special ? Ints("[special purchase cost]", cost) : Ints("[purchase cost]", cost));
        if (special)
        {
            lines.Add(Ints("[feature skill type]", 1));
        }
        if (pre != null)
        {
            lines.Add(Ints("[pre required skill]", pre));
        }
        if (icon)
        {
            lines.Add(new ScriptLine("[icon]", new[]
            {
                new ScriptValue(ScriptValueKind.String, 0, "Character/Test/Skill.img"), new ScriptValue(ScriptValueKind.Int, id, null),
                new ScriptValue(ScriptValueKind.String, 0, "Character/Test/Skill.img"), new ScriptValue(ScriptValueKind.Int, id + 100, null),
            }));
        }
        return new SkillDef(id, "skill/test/s" + id + ".skl", lines.ToArray());
    }

    private static readonly int[] Five = { 5, 5, 5, 5, 5, 5 };

    /// <summary>
    /// 第 0 页(共用):1 号主动(1 级起、间隔 2、最高 5、每级 3 点、有图标)、2 号被动(3 级起、间隔 3、最高 3、每级 4 点);
    /// 第 1 页(职业):3 号要求 1 号 2 级加 2 号 1 级、5 级起;第 2 页:4 号只有成长类型 1 学得了;第 3 页(觉醒):5 号 10 级起、间隔 5、最高 3、每级 1 个觉醒点。
    /// 6 到 14 号是第 0 页里要求 1 到 9 级的九个主动技能(滚动用)。
    /// </summary>
    internal static SkillCatalog Catalog(bool extra = true)
    {
        var list = new List<SkillDef>
        {
            Skill(1, 1, 2, Five, 3, icon: true),
            Skill(2, 3, 3, new[] { 3, 3, 3, 3, 3, 3 }, 4, active: false),
            Skill(3, 5, 1, Five, 5, pre: new[] { 1, 2, 2, 1 }, cls: 1, fit: new[] { 0 }),
            Skill(4, 1, 1, new[] { 0, 3, 0, 0, 0, 0 }, 2, cls: 4),
            Skill(5, 10, 5, new[] { 3, 3, 3, 3, 3, 3 }, 1, special: true),
        };
        if (extra)
        {
            for (int i = 0; i < 9; i++)
            {
                list.Add(Skill(6 + i, 1 + i, 1, Five, 1));
            }
        }
        return new SkillCatalog(0, list);
    }

    /// <summary>白送:开局 1 号 1 级。</summary>
    internal static ScriptLine[] ChrLines()
    {
        return new[] { Ints("[initial value]"), Ints("[skill]", 1, 1) };
    }

    internal static HeroProgress Hero(int level = 20, bool extra = true)
    {
        HeroProgress p = Progress(level: level);
        p.AttachSkills(Catalog(extra), ChrLines(), true);
        return p;
    }

    private static SkillWindow Opened(HeroProgress hero = null, int page = 0)
    {
        var w = new SkillWindow { TextSource = Names };
        w.Use(hero ?? Hero());
        Assert.True(w.Show(GameStage.Town));
        w.SetPage(page);
        return w;
    }

    private static string Names(int kind, string key)
    {
        return kind == 6 ? "Name " + key : null;
    }

    private static string[] Tags(List<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToArray();

    // ------------------------------------------------------------------ 打开与分页

    [Fact]
    public void ItOpensOnlyInTownWithSkillDataAndCountsOpens()
    {
        var w = new SkillWindow();
        Assert.False(w.Show(GameStage.Town));                                       // 没接进度
        Assert.Throws<ArgumentNullException>(() => w.Use(null));
        w.Use(Progress(level: 5));                                                  // 没接技能数据
        Assert.False(w.Show(GameStage.Town));
        w.Use(Hero());
        Assert.False(w.Show(GameStage.Dungeon));                                    // 副本里不能开
        Assert.False(w.Show(GameStage.DungeonSelect));
        Assert.True(w.Show(GameStage.Town));
        Assert.False(w.Show(GameStage.Town));                                       // 已经开着
        Assert.Equal(1, w.Opens);
        w.Close();
        Assert.False(w.Open);
        Assert.True(w.Show(GameStage.Town));
        Assert.Equal(2, w.Opens);
        Assert.Equal(0, w.Page);
    }

    [Fact]
    public void RowsFollowThePageAndSortByRequiredLevelAndHideWhatTheGrowtypeCannotLearn()
    {
        SkillWindow w = Opened();
        // 第 0 页按要求等级再按编号:1 号与 6 号(1 级)、7 号(2 级)、2 号与 8 号(3 级)、9 到 14 号(4 到 9 级)
        Assert.Equal(new[] { 1, 6, 7, 2, 8, 9, 10, 11, 12, 13, 14 }, w.Rows.Select(r => r.SkillId).ToArray());
        Assert.Equal(0, w.Selected);
        w.SetPage(1);
        Assert.Equal(new[] { 3 }, w.Rows.Select(r => r.SkillId).ToArray());
        w.SetPage(2);
        Assert.Empty(w.Rows);                                                       // 4 号只有成长类型 1 学得了
        Assert.Equal(-1, w.Selected);
        Assert.Null(w.SelectedRow);
        Assert.Null(w.SelectedExplain);
        w.SetPage(3);
        Assert.Equal(new[] { 5 }, w.Rows.Select(r => r.SkillId).ToArray());
        Assert.True(w.Rows[0].Special);
    }

    [Fact]
    public void PagesMoveWithLeftAndRightAndClampAtTheEnds()
    {
        SkillWindow w = Opened();
        w.Move(-1, 0);
        Assert.Equal(0, w.Page);
        w.Move(1, 0);
        Assert.Equal(1, w.Page);
        w.Move(1, 0);
        w.Move(1, 0);
        w.Move(1, 0);
        Assert.Equal(SkillWindow.PageCount - 1, w.Page);
        w.SetPage(-5);
        Assert.Equal(0, w.Page);
        w.SetPage(99);
        Assert.Equal(SkillWindow.PageCount - 1, w.Page);
        w.Close();
        w.Move(-1, 0);                                                              // 没开时不动
        Assert.Equal(SkillWindow.PageCount - 1, w.Page);
    }

    [Fact]
    public void UpAndDownMoveTheSelectionAndTheListScrollsToKeepItInView()
    {
        SkillWindow w = Opened();
        int count = w.Rows.Count;
        Assert.Equal(11, count);
        w.Move(0, -1);
        Assert.Equal((0, 0), (w.Selected, w.ScrollRow));
        for (int i = 0; i < SkillWindow.VisibleRows; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal(SkillWindow.VisibleRows, w.Selected);
        Assert.Equal(1, w.ScrollRow);                                               // 选到第 9 行,一屏 8 行,滚动 1 行
        for (int i = 0; i < 20; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal((count - 1, count - SkillWindow.VisibleRows), (w.Selected, w.ScrollRow));
        for (int i = 0; i < 20; i++)
        {
            w.Move(0, -1);
        }
        Assert.Equal((0, 0), (w.Selected, w.ScrollRow));
        w.Select(99);
        Assert.Equal(count - 1, w.Selected);
    }

    // ------------------------------------------------------------------ 行的状态

    [Fact]
    public void RowStatesComeFromTheLearningRules()
    {
        SkillWindow w = Opened();
        SkillRow one = w.Rows.Single(r => r.SkillId == 1);
        Assert.Equal((1, 5, 3, SkillRowState.Learnable), (one.Level, one.Cap, one.NextCost, one.State));
        Assert.Equal(SkillOpResult.Ok, one.Reason);
        Assert.Equal(3, one.NextHeroLevel);                                         // 第 2 级要求 1 + 2 = 3 级
        Assert.True(one.Active);
        Assert.Equal(0, one.Quick);                                                 // 白送的 1 号在第一个快捷格
        Assert.Equal(("Character/Test/Skill.img", 1, 101), (one.IconImage, one.IconFrame, one.IconDisabledFrame));
        Assert.Equal(5, one.MaxNow);                                                // 20 级:min(5, 20 - 1 + 1, (20 - 1) / 2 + 1)
        SkillRow two = w.Rows.Single(r => r.SkillId == 2);
        Assert.Equal((0, 3, 4, SkillRowState.Learnable, false), (two.Level, two.Cap, two.NextCost, two.State, two.Active));
        Assert.Equal(-1, two.Quick);

        w.SetPage(1);
        SkillRow three = w.Rows[0];
        Assert.Equal((SkillRowState.Locked, SkillOpResult.NotAllowed), (three.State, three.Reason));   // 前置技能没学够
        Assert.Equal(2, three.Prereqs.Count);
        Assert.Equal((1, 2, false, "Name n1"), (three.Prereqs[0].SkillId, three.Prereqs[0].Level, three.Prereqs[0].Met, three.Prereqs[0].Name));
        Assert.Equal((2, 1, false), (three.Prereqs[1].SkillId, three.Prereqs[1].Level, three.Prereqs[1].Met));
        Assert.Equal("Name n3", three.Name);

        w.SetPage(3);
        SkillRow five = w.Rows[0];
        Assert.Equal((SkillRowState.Locked, SkillOpResult.NotEnoughPoints), (five.State, five.Reason));  // 20 级没有觉醒点
        Assert.Equal(1, five.NextCost);
    }

    [Fact]
    public void AwakeningPointsAppearFromLevelFiftyAndPayForSpecialSkills()
    {
        SkillWindow w = Opened(Hero(level: 52, extra: false), page: 3);
        Assert.Equal(3, w.AwakeningPoints);
        Assert.Equal(SkillRowState.Learnable, w.Rows[0].State);
        int spBefore = w.SkillPoints;
        Assert.True(w.Learn());
        Assert.Equal((2, spBefore), (w.AwakeningPoints, w.SkillPoints));            // 花觉醒点,技能点不动
        Assert.Equal("SKILL LEARNED", w.Message);
    }

    // ------------------------------------------------------------------ 学

    [Fact]
    public void LearningSpendsPointsRaisesTheLevelAndAutoSlotsNewActiveSkills()
    {
        HeroProgress hero = Hero();
        SkillWindow w = Opened(hero);
        int sp = w.SkillPoints;
        Assert.Equal(210, sp);                                                      // 1 + 2 + ... + 20
        Assert.True(w.Learn());                                                     // 选中 1 号:1 → 2 级,花 3 点
        Assert.Equal((sp - 3, 2, "SKILL LEARNED", SkillOpResult.Ok, 1), (w.SkillPoints, hero.Skills.LevelOf(1), w.Message, w.LastResult, w.Learns));
        Assert.Equal(2, w.Rows.Single(r => r.SkillId == 1).Level);                  // 行跟着刷新

        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 6));                   // 6 号是新技能:学会后自动放进第一个空的快捷格
        Assert.True(w.Learn());
        Assert.Equal(1, hero.Skills.QuickSlotOf(6));
        Assert.Equal(1, w.Rows.Single(r => r.SkillId == 6).Quick);
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 2));                   // 被动技能不上栏
        Assert.True(w.Learn());
        Assert.Equal(-1, hero.Skills.QuickSlotOf(2));
        Assert.Equal(3, w.Learns);
    }

    [Fact]
    public void LearningFailuresKeepTheStateAndExplainWhy()
    {
        HeroProgress hero = Hero(level: 4, extra: false);                           // 总共 1 + 2 + 3 + 4 = 10 点
        SkillWindow w = Opened(hero);
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 1));
        Assert.True(w.Learn());                                                     // 1 → 2 级(要求 3 级),花 3 点
        Assert.False(w.Learn());                                                    // 第 3 级要求 5 级
        Assert.Equal((SkillOpResult.NotAllowed, "CANNOT LEARN YET"), (w.LastResult, w.Message));
        Assert.Equal(2, hero.Skills.LevelOf(1));
        Assert.Equal(7, hero.SkillPoints);

        HeroProgress poor = Hero(level: 4, extra: false);
        poor.Skills.SetPoints(2, 0);                                                // 2 号要 4 点
        SkillWindow p = Opened(poor);
        p.Select(p.Rows.ToList().FindIndex(r => r.SkillId == 2));
        Assert.False(p.Learn());
        Assert.Equal((SkillOpResult.NotEnoughPoints, "NOT ENOUGH POINTS"), (p.LastResult, p.Message));
        Assert.Equal(2, poor.SkillPoints);
        Assert.Equal(0, p.Learns);

        var empty = Opened(page: 2);                                                // 空页没有可学的
        Assert.False(empty.Learn());
        Assert.False(empty.Refund());
        Assert.False(empty.Assign(0));
        Assert.False(empty.ClearSlot());
        var closed = new SkillWindow();
        closed.Use(Hero());
        Assert.False(closed.Learn());
        Assert.False(closed.Reset());
    }

    // ------------------------------------------------------------------ 退与重置

    [Fact]
    public void RefundingReturnsPointsAndRespectsTheStartingLevelAndDependents()
    {
        HeroProgress hero = Hero(level: 20, extra: false);
        SkillWindow w = Opened(hero);
        int total = hero.SkillPoints;
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 1));
        Assert.False(w.Refund());                                                   // 1 号在白送的 1 级,不能再退
        Assert.Equal((SkillOpResult.BelowGiven, "BELOW STARTING LEVEL"), (w.LastResult, w.Message));
        Assert.True(w.Learn());
        Assert.True(w.Learn());                                                     // 3 级
        Assert.Equal(total - 6, hero.SkillPoints);
        Assert.True(w.Refund());
        Assert.Equal((total - 3, 2, "SKILL REFUNDED", 1), (hero.SkillPoints, hero.Skills.LevelOf(1), w.Message, w.Refunds));

        // 3 号要求 1 号 2 级加 2 号 1 级:学了 3 号之后 1 号不能退到 1 级以下的要求之下
        w.SetPage(0);
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 2));
        Assert.True(w.Learn());
        w.SetPage(1);
        Assert.True(w.Learn());                                                     // 3 号
        w.SetPage(0);
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 1));
        Assert.False(w.Refund());                                                   // 1 号要保持 2 级
        Assert.Equal((SkillOpResult.NeededByOther, "ANOTHER SKILL NEEDS IT"), (w.LastResult, w.Message));
        Assert.Equal(2, hero.Skills.LevelOf(1));
        Assert.Equal(1, w.Refunds);                                                 // 失败的不计
    }

    [Fact]
    public void ResetNeedsTwoPressesAndRestoresTheGivenSkillsAndTheFullPoints()
    {
        HeroProgress hero = Hero(level: 20, extra: false);
        SkillWindow w = Opened(hero);
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 1));
        Assert.True(w.Learn());
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 2));
        Assert.True(w.Learn());
        Assert.NotEqual(210, hero.SkillPoints);
        Assert.False(w.Reset());                                                    // 第一下只是预备
        Assert.Equal(("PRESS RESET AGAIN", true, 0), (w.Message, w.ResetArmed, w.Resets));
        Assert.Equal(2, hero.Skills.LevelOf(1));
        w.Move(0, 1);                                                               // 动了光标就取消预备
        Assert.False(w.ResetArmed);
        Assert.False(w.Reset());
        Assert.True(w.Reset());
        Assert.Equal(("SKILLS RESET", false, 1), (w.Message, w.ResetArmed, w.Resets));
        Assert.Equal((210, 1, 0), (hero.SkillPoints, hero.Skills.LevelOf(1), hero.Skills.LevelOf(2)));
        Assert.Equal(1, w.Rows.Single(r => r.SkillId == 1).Level);
    }

    // ------------------------------------------------------------------ 快捷栏

    [Fact]
    public void AssignPutsLearnedActiveSkillsInSlotsAndSwapsAndClears()
    {
        HeroProgress hero = Hero(level: 20, extra: true);
        SkillWindow w = Opened(hero);
        int i6 = w.Rows.ToList().FindIndex(r => r.SkillId == 6);
        w.Select(i6);
        Assert.False(w.Assign(3));                                                  // 6 号还没学
        Assert.Equal("CANNOT SLOT THIS", w.Message);
        Assert.True(w.Learn());                                                     // 学会后自动进第 1 格(0 号格是 1 号)
        Assert.Equal(1, hero.Skills.QuickSlotOf(6));
        Assert.True(w.Assign(4));                                                   // 挪到第 4 格
        Assert.Equal((4, 1, "SKILL SLOTTED"), (hero.Skills.QuickSlotOf(6), w.Assigns, w.Message));
        Assert.Equal(6, w.QuickAt(4));
        Assert.True(w.Assign(0));                                                   // 与 1 号互换
        Assert.Equal((0, 4), (hero.Skills.QuickSlotOf(6), hero.Skills.QuickSlotOf(1)));
        Assert.False(w.Assign(9));                                                  // 越界

        int i2 = w.Rows.ToList().FindIndex(r => r.SkillId == 2);
        w.Select(i2);
        Assert.True(w.Learn());                                                     // 被动技能
        Assert.False(w.Assign(2));
        Assert.Equal(0, w.QuickAt(2));

        w.Select(i6);
        Assert.True(w.ClearSlot());
        Assert.Equal(("SLOT CLEARED", -1), (w.Message, hero.Skills.QuickSlotOf(6)));
        Assert.False(w.ClearSlot());
        Assert.Equal("NOT IN A SLOT", w.Message);
        Assert.NotNull(w.IconOf(1));
        Assert.Equal(("Character/Test/Skill.img", 1), (w.IconOf(1).Image, w.IconOf(1).Frame));
        Assert.Null(w.IconOf(99));
    }

    // ------------------------------------------------------------------ 鼠标

    [Fact]
    public void ClicksHitTabsRowsButtonsAndQuickSlots()
    {
        HeroProgress hero = Hero();
        SkillWindow w = Opened(hero);
        Assert.False(w.Click(0, 0));
        // 分页
        (int tx, int ty, int tw, int th) = SkillWindow.TabRect(1);
        Assert.True(w.Click(tx + tw / 2, ty + th / 2));
        Assert.Equal(1, w.Page);
        (tx, ty, tw, th) = SkillWindow.TabRect(0);
        Assert.True(w.Click(tx + 1, ty + 1));
        Assert.Equal(0, w.Page);
        // 技能行:第一下选中,再点一下学一级
        (int rx, int ry, int rw, int rh) = SkillWindow.RowRect(2);
        Assert.True(w.Click(rx + rw / 2, ry + rh / 2));
        Assert.Equal(2, w.Selected);
        Assert.Equal(0, w.Learns);
        Assert.True(w.Click(rx + 3, ry + 3));
        Assert.Equal(1, w.Learns);
        // 按钮:学习、退还、重置
        (int bx, int by, int bw, int bh) = SkillWindow.ButtonRect((int)SkillButton.Learn);
        Assert.True(w.Click(bx + bw / 2, by + bh / 2));
        Assert.Equal(2, w.Learns);
        (bx, by, bw, bh) = SkillWindow.ButtonRect((int)SkillButton.Refund);
        Assert.True(w.Click(bx + 5, by + 5));
        Assert.Equal(1, w.Refunds);
        (bx, by, bw, bh) = SkillWindow.ButtonRect((int)SkillButton.Reset);
        Assert.True(w.Click(bx + 5, by + 5));
        Assert.True(w.ResetArmed);
        Assert.True(w.Click(bx + 5, by + 5));
        Assert.Equal(1, w.Resets);
        // 快捷栏格:把选中的(现在没学)放进去 → 不行;选 1 号再点格子 → 放进去
        w.Select(w.Rows.ToList().FindIndex(r => r.SkillId == 1));
        (int qx, int qy, int qw, int qh) = SkillWindow.QuickRect(3);
        Assert.True(w.Click(qx + qw / 2, qy + qh / 2));
        Assert.Equal(3, hero.Skills.QuickSlotOf(1));
    }

    [Fact]
    public void ClicksOnEmptyRowSpaceDoNothingWhenThePageHasFewRows()
    {
        SkillWindow w = Opened(page: 1);
        (int rx, int ry, int rw, int rh) = SkillWindow.RowRect(1);
        Assert.False(w.Click(rx + 3, ry + 3));                                      // 这一页只有一行
    }

    // ------------------------------------------------------------------ 流程

    private sealed class FakeRun : GameFlowTests.FakeHero, IRunSession
    {
        public FakeRun(string path, int run) : base("run " + path + " #" + run)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }
    }

    private static readonly DungeonEntry Entry = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private static GameFlow NewFlow(bool withSkills = true)
    {
        var flow = new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new FakeRun(path, run), new[] { Entry });
        if (withSkills)
        {
            flow.Skills.Use(Hero());
        }
        flow.EnterTown();
        return flow;
    }

    [Fact]
    public void KTogglesTheSkillWindowAndItExcludesTheOtherWindows()
    {
        GameFlow flow = NewFlow();
        Assert.True(flow.ToggleSkills());
        Assert.True(flow.Skills.Open);
        Assert.False(flow.Poll());                                                  // 开着时游戏暂停,不处理各阶段的结果
        Assert.False(flow.ToggleBag());                                             // 一次只开一个窗口
        Assert.False(flow.ToggleQuests());
        Assert.False(flow.OpenShop(1));
        Assert.True(flow.ToggleSkills());                                           // K 再按一次关上
        Assert.False(flow.Skills.Open);

        Assert.True(flow.ToggleBag());
        Assert.False(flow.ToggleSkills());                                          // 背包开着不开技能窗口
        Assert.False(flow.Skills.Open);
        flow.Bag.Close();
        Assert.True(flow.ToggleMenu());
        Assert.False(flow.ToggleSkills());                                          // 系统菜单开着也不开
        flow.Menu.Close();
    }

    [Fact]
    public void EscClosesTheSkillWindowFirstAndAStageChangeClosesItToo()
    {
        GameFlow flow = NewFlow();
        Assert.True(flow.ToggleSkills());
        Assert.True(flow.ToggleMenu());                                             // Esc:先关技能窗口,不开系统菜单
        Assert.False(flow.Skills.Open);
        Assert.False(flow.Menu.Open);
        Assert.True(flow.ToggleSkills());
        flow.StartDungeon(Entry.Path);
        Assert.False(flow.Skills.Open);                                             // 换阶段时关上
        Assert.False(flow.ToggleSkills());                                          // 副本里打不开
        Assert.False(flow.Skills.Open);
        flow.EnterTown();
        Assert.True(flow.ToggleSkills());
        flow.EnterSelect();
        Assert.False(flow.Skills.Open);
    }

    [Fact]
    public void WithoutAHeroTheFlowIgnoresTheKey()
    {
        GameFlow flow = NewFlow(withSkills: false);
        Assert.False(flow.ToggleSkills());
        Assert.False(flow.Skills.Open);
        var fresh = new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new FakeRun(path, run), new[] { Entry });
        fresh.Skills.Use(Hero());
        Assert.False(fresh.ToggleSkills());                                         // 流程还没开始
    }

    // ------------------------------------------------------------------ 画法

    private static OverlayArt FontArt() => new OverlayArt { Fonts = new BlockFonts() };

    private sealed class StubSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public List<(string, int)> Asked { get; } = new List<(string, int)>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            Asked.Add((imageName, frame));
            return Frames.TryGetValue((imageName, frame), out sprite);
        }
    }

    private static SpriteFrame Opaque(int w, int h)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = 255;
        }
        return new SpriteFrame(w, h, 0, 0, w, h, rgba);
    }

    private static void AssertInsideScreen(List<SceneSprite> sprites)
    {
        foreach (SceneSprite s in sprites)
        {
            Assert.InRange(s.ScreenX, 0, 800);
            Assert.InRange(s.ScreenY, 0, 600);
            Assert.True(s.ScreenX + s.Frame.Width <= 800, s.AnimationPath + " 超出右边");
            Assert.True(s.ScreenY + s.Frame.Height <= 600, s.AnimationPath + " 超出下边");
        }
    }

    [Fact]
    public void AClosedWindowDrawsNothing()
    {
        var w = new SkillWindow();
        Assert.Empty(SkillWindowArt.Build(w, null, new OverlayArt()));
        Assert.Empty(SkillWindowArt.Build(null, null, new OverlayArt()));
    }

    [Fact]
    public void TheOpenWindowDrawsEveryPartInsideTheScreen()
    {
        SkillWindow w = Opened();
        List<SceneSprite> sprites = SkillWindowArt.Build(w, null, new OverlayArt());
        string[] tags = Tags(sprites);
        foreach (string tag in new[]
        {
            "skill-dim", "skill-frame", "skill-panel", "skill-title-bar", "skill-title", "skill-points", "skill-tab-selected", "skill-tab", "skill-tab-label",
            "skill-row-selected", "skill-row", "skill-row-block", "skill-row-name", "skill-row-level", "skill-row-key", "skill-track", "skill-thumb", "skill-detail",
            "skill-icon-block", "skill-name", "skill-info", "skill-quick", "skill-quick-selected", "skill-quick-key", "skill-button", "skill-button-primary", "skill-button-label", "skill-hint",
        })
        {
            Assert.Contains(tag, tags);
        }
        Assert.Equal(SkillWindow.PageCount, tags.Count(t => t == "skill-tab" || t == "skill-tab-selected"));
        Assert.Equal(SkillWindow.VisibleRows, tags.Count(t => t == "skill-row"));
        Assert.Equal(SkillBook.QuickSlots, tags.Count(t => t == "skill-quick"));
        Assert.Equal(1, tags.Count(t => t == "skill-quick-selected"));                  // 选中的 1 号在第一格
        Assert.DoesNotContain("skill-message", tags);
        AssertInsideScreen(sprites);
        for (int page = 0; page < SkillWindow.PageCount; page++)
        {
            w.SetPage(page);
            AssertInsideScreen(SkillWindowArt.Build(w, null, FontArt()));
        }
        // 空页只有占位提示
        w.SetPage(2);
        Assert.Contains("skill-empty", Tags(SkillWindowArt.Build(w, null, new OverlayArt())));
    }

    [Fact]
    public void WithoutFontsNamesFallBackToPlaceholdersAndThereIsNoExplanation()
    {
        SkillWindow w = Opened();
        List<SceneSprite> plain = SkillWindowArt.Build(w, null, new OverlayArt());
        Assert.DoesNotContain("skill-explain", Tags(plain));
        Assert.Equal(OverlayArt.TextWidth("SKILL 1", 1), SkillWindowArt.NameFrame(new OverlayArt(), w.Rows[0], 1, OverlayArt.White, 200, 2).Width);
        Assert.NotNull(SkillWindowArt.NameFrame(FontArt(), w.Rows[0], 1, OverlayArt.White, 200, 2));
        List<SceneSprite> withFonts = SkillWindowArt.Build(w, null, FontArt());
        Assert.Contains("skill-explain", Tags(withFonts));
        Assert.Equal("Name n1", w.Rows[0].Name);
        // 前置技能页:有前置行、名字用数据文字
        w.SetPage(1);
        Assert.Contains("skill-prereq-head", Tags(SkillWindowArt.Build(w, null, FontArt())));
        Assert.Equal(2, Tags(SkillWindowArt.Build(w, null, FontArt())).Count(t => t == "skill-prereq"));
        Assert.Equal("Name n1 LV 2", SkillWindowArt.PrereqText(FontArt(), w.Rows[0].Prereqs[0]));
        Assert.Equal("SKILL 1 LV 2", SkillWindowArt.PrereqText(new OverlayArt(), w.Rows[0].Prereqs[0]));
    }

    [Fact]
    public void IconsComeFromTheSkillIconImageAndFallBackToBlocks()
    {
        SkillWindow w = Opened();
        var sprites = new StubSprites();
        sprites.Frames[("sprite/character/test/skill.img", 1)] = Opaque(28, 28);          // 1 号学会了:亮图标
        sprites.Frames[("sprite/character/test/skill.img", 101)] = Opaque(28, 28);        // 灰图标
        List<SceneSprite> drawn = SkillWindowArt.Build(w, sprites, new OverlayArt());
        Assert.Contains("skill-row-icon", Tags(drawn));
        Assert.Contains("skill-icon", Tags(drawn));
        Assert.Contains("skill-quick-icon", Tags(drawn));                                // 1 号在第一个快捷格
        Assert.Contains(("sprite/character/test/skill.img", 1), sprites.Asked);
        Assert.DoesNotContain("skill-icon-block", Tags(drawn));                          // 选中的 1 号有图标

        // 没学过的画灰图标
        var fresh = new StubSprites();
        fresh.Frames[("sprite/character/test/skill.img", 1)] = Opaque(28, 28);
        fresh.Frames[("sprite/character/test/skill.img", 101)] = Opaque(28, 28);
        HeroProgress hero = Hero();
        hero.Skills.Reset(new List<SkillGrant>(), hero.SkillPoints, 0);                  // 连白送的也清掉
        SkillWindow cold = Opened(hero);
        SkillWindowArt.Build(cold, fresh, new OverlayArt());
        Assert.Contains(("sprite/character/test/skill.img", 101), fresh.Asked);
    }

    [Fact]
    public void TheMessageLineAppearsAfterAnAction()
    {
        SkillWindow w = Opened();
        Assert.True(w.Learn());
        Assert.Contains("skill-message", Tags(SkillWindowArt.Build(w, null, new OverlayArt())));
    }

    [Fact]
    public void EveryPlaceholderTheSkillWindowShowsHasAChineseVersion()
    {
        var words = new List<string> { "SKILLS", "NO SKILLS", "AWAKENING SKILL", "ACTIVE SKILL", "PASSIVE SKILL", "REQUIRES", SkillWindowArt.HintText,
            SkillWindowArt.PointsText(3, 4), "NEED LEVEL 5" };
        words.AddRange(SkillWindowArt.PageNames);
        words.Add(SkillWindowArt.NextCostText(new SkillRow { NextCost = 3 }));
        words.Add(SkillWindowArt.NextCostText(new SkillRow { NextCost = 3, Special = true }));
        for (int i = 0; i < 3; i++)
        {
            words.Add(SkillWindowArt.ButtonText((SkillButton)i));
        }
        foreach (SkillOpResult result in Enum.GetValues(typeof(SkillOpResult)))
        {
            if (result != SkillOpResult.Ok)
            {
                words.Add(SkillWindow.MessageOf(result));
            }
        }
        words.AddRange(new[] { "SKILL LEARNED", "SKILL REFUNDED", "SKILLS RESET", "PRESS RESET AGAIN", "SKILL SLOTTED", "CANNOT SLOT THIS", "SLOT CLEARED", "NOT IN A SLOT" });
        foreach (string word in words)
        {
            Assert.True(UiText.HasTranslation(word), word);
        }
        Assert.Equal("技能点 3  觉醒点 4", UiText.Translate(SkillWindowArt.PointsText(3, 4)));
        Assert.Equal("下一级花费 觉醒点 3", UiText.Translate("NEXT COST AP 3"));
        Assert.Equal(string.Empty, SkillWindow.MessageOf(SkillOpResult.Ok));
    }

    [Fact]
    public void NameColorsMarkTheStates()
    {
        Assert.Equal(OverlayArt.Yellow, SkillWindowArt.NameColor(new SkillRow { State = SkillRowState.Maxed }));
        Assert.Equal(SkillWindowArt.LearnableColor, SkillWindowArt.NameColor(new SkillRow { State = SkillRowState.Learnable }));
        Assert.Equal(SkillWindowArt.LockedColor, SkillWindowArt.NameColor(new SkillRow { State = SkillRowState.Locked }));
        Assert.Equal(OverlayArt.White, SkillWindowArt.NameColor(new SkillRow { State = SkillRowState.Locked, Level = 2 }));
        Assert.Equal("2/5", SkillWindowArt.LevelText(new SkillRow { Level = 2, Cap = 5 }));
    }
}
