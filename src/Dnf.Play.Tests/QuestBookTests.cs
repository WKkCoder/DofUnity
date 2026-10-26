using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Tests;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 任务系统的接线(<see cref="QuestBook"/>、<see cref="QuestTexts"/>、存档里的任务行,M31,证据 E-123 的规则 + 本项目自己的接线设计):
/// NPC 头顶的标记与任务行、接任务、击杀/清房/通关/拜访事件推进计数、任务物品掉落、交任务(经验金币物品、多选一、背包放不下)、
/// 没有交任务 NPC 的自动完成、放弃、读档核对、存档往返。任务、物品、数值全是合成的,与原版数据无关。
/// </summary>
public class QuestBookTests
{
    // ------------------------------------------------------------------ 合成数据

    internal static ItemInfo Stack(int code, int type = StackableTypes.Waste, int limit = 99)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { StackableType = type, StackLimit = limit };
    }

    private static readonly Dictionary<int, ItemInfo> Items = new()
    {
        [500] = Stack(500),
        [700] = Stack(700, StackableTypes.Quest),
        [800] = Stack(800),
        [801] = Stack(801),
        [900] = Stack(900, StackableTypes.Quest),
        [901] = Stack(901, StackableTypes.Quest),
    };

    private static ItemInfo Lookup(int code) => Items.TryGetValue(code, out ItemInfo item) ? item : null;

    /// <summary>
    /// 合成的任务:
    /// 1 剿灭(副本 7 里的怪物 100 杀 2 只,NPC 4 发布与交付,等级 10 起,难度字母 A,奖励金币与 500 号物品 2 件,多选一 800 或 801);
    /// 2 收集(900 号物品 2 件,怪物 100 掉它,NPC 4 发布、NPC 8 交付,奖励金币);3 拜访(NPC 9,NPC 4 发布,奖励 500 号物品 1 件);
    /// 4 过房间(地图 30,没有交付 NPC,自动完成,奖励金币);5 前置是任务 1(NPC 4 发布与交付);
    /// 6 过房间(地图 31,自动完成,奖励 500 号物品 1 件);7 剿灭 1 只(不能放弃);8 剿灭 3 只;
    /// 9 通关副本 7(NPC 4 交付,通关掉 901 号物品);10 接取时给 700 号物品。
    /// </summary>
    internal static GameData Data()
    {
        var fx = new Fixture();
        fx.List("n_quest/quest.lst", (1, "a.qst"), (2, "b.qst"), (3, "c.qst"), (4, "d.qst"), (5, "e.qst"), (6, "f.qst"), (7, "g.qst"), (8, "h.qst"), (9, "i.qst"), (10, "j.qst"));
        fx.Tokens("n_quest/a.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[hunt monster]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(4)
            .Tag("[level]").Int(10).Int(99).Tag("[difficulty]").Str("A")
            .Tag("[int data]").Ints(7, -1, 100, 2).Tag("[/int data]")
            .Tag("[reward type]").Str("[item]")
            .Tag("[reward int data]").Ints(0, 1, 500, 2).Tag("[/reward int data]")
            .Tag("[reward selection int data]").Ints(800, 1, 801, 1).Tag("[/reward selection int data]"));
        fx.Tokens("n_quest/b.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[seeking]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(8)
            .Tag("[level]").Int(10).Int(99)
            .Tag("[int data]").Ints(900, 2).Tag("[/int data]")
            .Tag("[monster reward item]").Ints(100, -1, -1, 900, 1, 100, 0).Tag("[/monster reward item]")
            .Tag("[reward type]").Str("[item]")
            .Tag("[reward int data]").Ints(0, 1).Tag("[/reward int data]"));
        fx.Tokens("n_quest/c.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[meet npc]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(9)
            .Tag("[int data]").Int(9).Tag("[/int data]")
            .Tag("[reward type]").Str("[item]")
            .Tag("[reward int data]").Ints(500, 1).Tag("[/reward int data]"));
        fx.Tokens("n_quest/d.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[clear map]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(10).Int(99)
            .Tag("[int data]").Int(30).Tag("[/int data]")
            .Tag("[reward type]").Str("[item]")
            .Tag("[reward int data]").Ints(0, 1).Tag("[/reward int data]"));
        fx.Tokens("n_quest/e.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[meet npc]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(9)
            .Tag("[pre required quest]").Int(1).Tag("[/pre required quest]")
            .Tag("[int data]").Int(9).Tag("[/int data]"));
        fx.Tokens("n_quest/f.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[clear map]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(-1)
            .Tag("[int data]").Int(31).Tag("[/int data]")
            .Tag("[reward type]").Str("[item]")
            .Tag("[reward int data]").Ints(500, 1).Tag("[/reward int data]"));
        fx.Tokens("n_quest/g.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[hunt monster]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(4)
            .Tag("[cant giveup]").Int(1)
            .Tag("[int data]").Ints(7, -1, 100, 1).Tag("[/int data]"));
        fx.Tokens("n_quest/h.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[hunt monster]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(4)
            .Tag("[int data]").Ints(7, -1, 100, 3).Tag("[/int data]"));
        fx.Tokens("n_quest/i.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6)
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(4)
            .Tag("[int data]").Ints(7, -1).Tag("[/int data]")
            .Tag("[clear reward item]").Ints(7, -1, 901, 1, 100, 0).Tag("[/clear reward item]"));
        fx.Tokens("n_quest/j.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[meet npc]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(9)
            .Tag("[int data]").Int(9).Tag("[/int data]")
            .Tag("[depend give item]").Ints(700, 3).Tag("[/depend give item]"));
        fx.Tokens("n_quest/questparameter.etc", w =>
        {
            w.Tag("[difficulty]").Str("S").Int(110).Str("A").Int(90).Tag("[/difficulty]");
            w.Tag("[green level penalty]").Int(80);
            w.Tag("[grey level penalty]").Int(30);
            w.Tag("[exp reward table]");
            for (int i = 0; i < 100; i++)
            {
                w.Int(i * 100);
            }
            w.Tag("[gold reward table]");
            for (int i = 0; i < 100; i++)
            {
                w.Int(i * 10);
            }
            w.Tag("[quest parameter end]");
        });
        return new GameData(fx.Build());
    }

    internal static (QuestBook Book, HeroProgress Hero) Make(int level = 10)
    {
        HeroProgress hero = Progress(level: level);
        var book = new QuestBook(new QuestCatalog(Data()), hero, Lookup, monsterExists: id => id == 100, seed: 7);
        return (book, hero);
    }

    /// <summary>把消耗品页塞满(每格一种、一格一件)。</summary>
    internal static void FillConsumables(HeroProgress hero)
    {
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.Equal(1, hero.Bag.Add(Stack(2000 + i, StackableTypes.Waste, 1), 1));
        }
    }

    private static void FillQuestTab(HeroProgress hero)
    {
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.Equal(1, hero.Bag.Add(Stack(3000 + i, StackableTypes.Quest, 1), 1));
        }
    }

    // ------------------------------------------------------------------ 标记与任务行

    [Fact]
    public void RowsAtAnNpcListOnlyWhatTheHeroCanTakeInIdOrder()
    {
        (QuestBook book, _) = Make();
        List<QuestRow> rows = book.RowsFor(4);
        // 5 号要先完成 1 号,不出现;其余在 NPC 4 这里发布的都可以接
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 7, 8, 9, 10 }, rows.Select(r => r.Id).ToArray());
        Assert.All(rows, r => Assert.Equal(QuestRowState.Available, r.State));
        Assert.Equal(QuestMark.Available, book.MarkOf(4));
        Assert.Equal(QuestMark.None, book.MarkOf(99));
    }

    [Fact]
    public void LowLevelHeroesDoNotSeeQuestsAboveTheirLevel()
    {
        (QuestBook book, _) = Make(level: 9);
        // 1、2、4 要 10 级;没写等级的从 1 级起
        Assert.DoesNotContain(book.RowsFor(4), r => r.Id == 1 || r.Id == 2 || r.Id == 4);
        Assert.Contains(book.RowsFor(4), r => r.Id == 3);
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(1));
    }

    [Fact]
    public void RowsCarryTheRewardPreviewForTheHeroLevel()
    {
        (QuestBook book, _) = Make();
        QuestRow row = book.RowsFor(4).Single(r => r.Id == 1);
        Assert.Equal(900, row.Exp);                        // 经验表[10] × 权重 A 90% × 惩罚 100%
        Assert.Equal(100, row.Gold);                       // 金币表[10] × 惩罚 100%
        QuestRewardLine item = Assert.Single(row.Rewards);
        Assert.Equal(500, item.Code);
        Assert.Equal(2, item.Count);
        Assert.Equal(new[] { 800, 801 }, row.Choices.Select(c => c.Code).ToArray());
        Assert.Equal(10, row.MinLevel);
        Assert.Equal(4, row.HandInNpc);
        Assert.True(row.CanAbandon);
        Assert.Equal(string.Empty, row.Progress);
    }

    [Fact]
    public void HandInRowsAreOnlyAtTheHandInNpcAndShowProgress()
    {
        (QuestBook book, _) = Make();
        Assert.Equal(QuestActionResult.Ok, book.Accept(2));        // 收集类:NPC 4 发布,NPC 8 交付
        Assert.Equal(QuestMark.InProgress, book.MarkOf(8));
        QuestRow row = Assert.Single(book.RowsFor(8));
        Assert.Equal(QuestRowState.InProgress, row.State);
        Assert.Equal("0/2", row.Progress);
        Assert.DoesNotContain(book.RowsFor(4), r => r.Id == 2);   // 发布的 NPC 这里不再列出已接的任务
    }

    [Fact]
    public void ReadyRowsComeFirstThenAvailableThenInProgress()
    {
        (QuestBook book, _) = Make();
        book.Accept(7);
        book.Accept(8);
        book.MonsterKilled(7, 0, 100, false);          // 7 号只要 1 只:可以交了
        List<QuestRow> rows = book.RowsFor(4);
        Assert.Equal(QuestRowState.Ready, rows[0].State);
        Assert.Equal(7, rows[0].Id);
        int firstProgress = rows.FindIndex(r => r.State == QuestRowState.InProgress);
        int lastAvailable = rows.FindLastIndex(r => r.State == QuestRowState.Available);
        Assert.True(lastAvailable < firstProgress);
        Assert.Equal(8, rows[firstProgress].Id);
        Assert.Equal(QuestMark.Ready, book.MarkOf(4));
    }

    // ------------------------------------------------------------------ 接任务

    [Fact]
    public void AcceptingAddsToTheLogCountsAndNotifies()
    {
        (QuestBook book, HeroProgress hero) = Make();
        Assert.Equal(QuestActionResult.Ok, book.Accept(1));
        Assert.True(hero.Quests.IsActive(1));
        Assert.Equal(1, book.Accepted);
        Assert.Equal(1, hero.QuestsActive);
        QuestNotice notice = Assert.Single(book.TakeNotices());
        Assert.Equal(QuestNoticeKind.Accepted, notice.Kind);
        Assert.Equal(1, notice.QuestId);
        Assert.Equal("QUEST 1", notice.Detail);                // 没装文字表时的占位
        Assert.Equal(0, book.PendingNotices);
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(1));   // 已经接了
        Assert.Equal(QuestActionResult.NotFound, book.Accept(999));
    }

    [Fact]
    public void ThePreRequiredQuestUnlocksTheNextOne()
    {
        (QuestBook book, _) = Make();
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(5));
        book.Accept(1);
        book.MonsterKilled(7, 0, 100, false);
        book.MonsterKilled(7, 0, 100, false);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(1));
        Assert.Equal(QuestActionResult.Ok, book.Accept(5));
    }

    [Fact]
    public void AcceptItemsAreGivenAndNeedRoom()
    {
        (QuestBook book, HeroProgress hero) = Make();
        Assert.Equal(QuestActionResult.Ok, book.Accept(10));
        Assert.Equal(3, hero.Bag.CountOf(700));

        (QuestBook full, HeroProgress fullHero) = Make();
        FillQuestTab(fullHero);
        Assert.Equal(QuestActionResult.NoRoom, full.Accept(10));
        Assert.False(fullHero.Quests.IsActive(10));
        Assert.Equal(0, full.Accepted);
        Assert.Equal(QuestNoticeKind.BagFull, Assert.Single(full.TakeNotices()).Kind);
    }

    [Fact]
    public void AtMostTwentyQuestsCanBeActive()
    {
        (QuestBook book, HeroProgress hero) = Make();
        for (int i = 0; i < QuestLog.MaxActive; i++)
        {
            hero.Quests.Add(1000 + i, Array.Empty<int>());
        }
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(1));
    }

    // ------------------------------------------------------------------ 事件与计数

    [Fact]
    public void KillsInTheRightDungeonAdvanceAHuntQuestAndMakeItReady()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(1);
        book.TakeNotices();
        book.MonsterKilled(8, 0, 100, false);          // 别的副本:不算
        book.MonsterKilled(7, 0, 101, false);          // 别的怪物:不算
        Assert.Equal(2, hero.Quests.Find(1).Remaining[0]);
        Assert.Equal(0, book.KillsCounted);

        book.MonsterKilled(7, 0, 100, false);
        Assert.Equal(1, hero.Quests.Find(1).Remaining[0]);
        QuestNotice progress = Assert.Single(book.TakeNotices());
        Assert.Equal(QuestNoticeKind.Progress, progress.Kind);
        Assert.Equal("QUEST 1  1/2", progress.Detail);

        book.MonsterKilled(7, 0, 100, false);
        QuestNotice ready = Assert.Single(book.TakeNotices());
        Assert.Equal(QuestNoticeKind.Ready, ready.Kind);
        Assert.Equal(2, book.KillsCounted);
        Assert.Equal(QuestRowState.Ready, book.RowOf(1).State);
    }

    [Fact]
    public void ConsecutiveProgressNoticesOfOneQuestMergeIntoTheLatest()
    {
        (QuestBook book, _) = Make();
        book.Accept(8);
        book.TakeNotices();
        book.MonsterKilled(7, 0, 100, false);
        book.MonsterKilled(7, 0, 100, false);
        QuestNotice notice = Assert.Single(book.TakeNotices());
        Assert.Equal("QUEST 8  2/3", notice.Detail);
    }

    [Fact]
    public void EventsWithoutActiveQuestsDoNothing()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.MonsterKilled(7, 0, 100, false);
        book.MapCleared(30);
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(0, book.KillsCounted);
        Assert.Equal(0, book.PendingNotices);
        Assert.Equal(0, hero.Bag.TotalCount);
    }

    [Fact]
    public void TalkingToTheTargetNpcFinishesAMeetQuest()
    {
        (QuestBook book, _) = Make();
        book.Accept(3);
        Assert.Equal(QuestMark.Ready, book.MarkOf(9));          // 拜访类:被拜访的 NPC 头顶就是"可以交"
        Assert.False(book.Talk(5));
        Assert.True(book.Talk(9));
        QuestRow row = Assert.Single(book.RowsFor(9));
        Assert.Equal(QuestRowState.Ready, row.State);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(3));
    }

    [Fact]
    public void MeetQuestsCannotBeTurnedInBeforeTalking()
    {
        (QuestBook book, _) = Make();
        book.Accept(3);
        Assert.Equal(QuestActionResult.NotReady, book.TurnIn(3));
    }

    [Fact]
    public void DungeonClearsCountAndRollClearDrops()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(9);
        book.TakeNotices();
        book.DungeonCleared(7, 0, 90000);
        Assert.Equal(1, hero.Bag.CountOf(901));                // 通关掉落(概率 100)
        Assert.Equal(1, book.QuestItemsFound);
        List<QuestNotice> notices = book.TakeNotices();
        Assert.Contains(notices, n => n.Kind == QuestNoticeKind.ItemFound);
        Assert.Contains(notices, n => n.Kind == QuestNoticeKind.Ready);
        Assert.Equal(QuestRowState.Ready, book.RowOf(9).State);
    }

    // ------------------------------------------------------------------ 收集类与掉落

    [Fact]
    public void KillDropsGoStraightIntoTheBagAndFinishASeekingQuest()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(2);
        book.TakeNotices();
        book.MonsterKilled(1, 0, 100, false);
        Assert.Equal(1, hero.Bag.CountOf(900));
        Assert.Equal("1/2", book.RowOf(2).Progress);
        Assert.Equal(QuestRowState.InProgress, book.RowOf(2).State);
        book.MonsterKilled(1, 0, 100, false);
        Assert.Equal(2, hero.Bag.CountOf(900));
        Assert.Equal(QuestRowState.Ready, book.RowOf(2).State);
        Assert.Contains(book.TakeNotices(), n => n.Kind == QuestNoticeKind.Ready);
        book.MonsterKilled(1, 0, 100, false);                  // 已经够了:不再掉
        Assert.Equal(2, hero.Bag.CountOf(900));
        Assert.Equal(2, book.QuestItemsFound);
    }

    [Fact]
    public void ASeekingQuestIsReadyAsSoonAsTheBagHasTheItems()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(2);
        Assert.Equal(QuestRowState.InProgress, book.RowOf(2).State);
        hero.AddItem(Items[900], 2);
        book.Update();
        Assert.Equal(QuestRowState.Ready, book.RowOf(2).State);
        Assert.Contains(book.TakeNotices(), n => n.Kind == QuestNoticeKind.Ready);
        book.Update();                                          // 只提示一次
        Assert.Equal(0, book.PendingNotices);
    }

    [Fact]
    public void TurningInASeekingQuestConsumesTheItems()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(2);
        hero.AddItem(Items[900], 3);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(2));
        Assert.Equal(1, hero.Bag.CountOf(900));                // 要 2 件,多的留着
        Assert.True(hero.Quests.IsCleared(2));
        Assert.Equal(100, hero.Gold);
    }

    // ------------------------------------------------------------------ 交任务

    [Fact]
    public void TurnInPaysExpGoldAndItemsAndClearsTheQuest()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(1);
        book.MonsterKilled(7, 0, 100, false);
        book.MonsterKilled(7, 0, 100, false);
        book.TakeNotices();
        long expBefore = hero.TotalGained;

        Assert.Equal(QuestActionResult.Ok, book.TurnIn(1, 1));  // 选 801
        Assert.Equal(900, hero.TotalGained - expBefore);
        Assert.Equal(100, hero.Gold);
        Assert.Equal(2, hero.Bag.CountOf(500));
        Assert.Equal(1, hero.Bag.CountOf(801));
        Assert.Equal(0, hero.Bag.CountOf(800));
        Assert.False(hero.Quests.IsActive(1));
        Assert.True(hero.Quests.IsCleared(1));
        Assert.Equal(1, book.Completed);
        QuestNotice notice = Assert.Single(book.TakeNotices());
        Assert.Equal(QuestNoticeKind.Completed, notice.Kind);
        Assert.Equal("EXP 900  GOLD 100", notice.Extra);
        Assert.Equal(QuestActionResult.NotFound, book.TurnIn(1));
    }

    [Fact]
    public void AnUnchosenSelectionDefaultsToTheFirstChoice()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(1);
        book.MonsterKilled(7, 0, 100, false);
        book.MonsterKilled(7, 0, 100, false);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(1));
        Assert.Equal(1, hero.Bag.CountOf(800));
        Assert.Equal(0, hero.Bag.CountOf(801));
    }

    [Fact]
    public void TurnInKeepsTheQuestWhenTheRewardsDoNotFit()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(1);
        book.MonsterKilled(7, 0, 100, false);
        book.MonsterKilled(7, 0, 100, false);
        FillConsumables(hero);
        book.TakeNotices();

        Assert.Equal(QuestActionResult.NoRoom, book.TurnIn(1));
        Assert.True(hero.Quests.IsActive(1));
        Assert.Equal(0, hero.Gold);
        Assert.Equal(QuestNoticeKind.BagFull, Assert.Single(book.TakeNotices()).Kind);

        hero.Bag.Remove(2000);                                 // 腾出两格:500 号一格、选的 800 号一格
        hero.Bag.Remove(2001);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(1));
        Assert.Equal(100, hero.Gold);
    }

    [Fact]
    public void TurnInBeforeTheGoalIsMetFails()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(1);
        book.MonsterKilled(7, 0, 100, false);
        Assert.Equal(QuestActionResult.NotReady, book.TurnIn(1));
        Assert.True(hero.Quests.IsActive(1));
        Assert.Equal(QuestActionResult.NotFound, book.TurnIn(2));      // 没接
    }

    // ------------------------------------------------------------------ 自动完成

    [Fact]
    public void QuestsWithoutAHandInNpcCompleteByThemselves()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(4);
        book.TakeNotices();
        book.MapCleared(29);                                   // 别的地图
        Assert.True(hero.Quests.IsActive(4));
        book.MapCleared(30);
        Assert.False(hero.Quests.IsActive(4));
        Assert.True(hero.Quests.IsCleared(4));
        Assert.Equal(100, hero.Gold);
        Assert.Equal(1, book.Completed);
        QuestNotice notice = Assert.Single(book.TakeNotices());
        Assert.Equal(QuestNoticeKind.Completed, notice.Kind);
    }

    [Fact]
    public void AutoCompletionWaitsForBagRoomAndFinishesOnTheNextUpdate()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(6);
        FillConsumables(hero);
        book.TakeNotices();
        book.MapCleared(31);
        Assert.True(hero.Quests.IsActive(6));                  // 奖励放不下:留着
        Assert.Equal(QuestNoticeKind.BagFull, Assert.Single(book.TakeNotices()).Kind);
        book.MapCleared(31);                                   // 再触发也只提示一次
        book.Update();
        Assert.Equal(0, book.PendingNotices);

        hero.Bag.Remove(2000);
        book.Update();
        Assert.False(hero.Quests.IsActive(6));
        Assert.Equal(1, hero.Bag.CountOf(500));
    }

    // ------------------------------------------------------------------ 放弃与读档

    [Fact]
    public void AbandoningRemovesTheQuestUnlessItCannotBeGivenUp()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(1);
        book.Accept(7);
        Assert.Equal(QuestActionResult.CannotGiveUp, book.Abandon(7));
        Assert.True(hero.Quests.IsActive(7));
        Assert.False(book.RowOf(7).CanAbandon);
        Assert.Equal(QuestActionResult.Ok, book.Abandon(1));
        Assert.False(hero.Quests.IsActive(1));
        Assert.False(hero.Quests.IsCleared(1));                // 放弃不算完成,还能再接
        Assert.Equal(1, book.Abandoned);
        Assert.Equal(QuestActionResult.NotFound, book.Abandon(1));
        Assert.Equal(QuestActionResult.Ok, book.Accept(1));
    }

    [Fact]
    public void ReconcileDropsUnknownQuestsAndFixesCounterLists()
    {
        (QuestBook book, HeroProgress hero) = Make();
        hero.Quests.Add(1, new[] { 5 });                       // 超过初始值 2:夹回 2
        hero.Quests.Add(99, new[] { 1 });                      // 数据里没有:丢掉
        hero.Quests.Add(2, new[] { 1, 1 });                    // 收集类没有计数:长度对不上,重置
        hero.Quests.Add(8, new[] { 1 });                       // 8 号有一组计数,长度对得上、值合规:不动
        Assert.Equal(3, book.Reconcile());
        Assert.Equal(2, hero.Quests.Find(1).Remaining[0]);
        Assert.False(hero.Quests.IsActive(99));
        Assert.Empty(hero.Quests.Find(2).Remaining);
        Assert.Equal(1, hero.Quests.Find(8).Remaining[0]);
        Assert.Equal(0, book.Reconcile());
    }

    [Fact]
    public void QuestStateSurvivesASaveRoundTrip()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(4);
        book.MapCleared(30);                                   // 4 号完成
        book.Accept(1);
        book.MonsterKilled(7, 0, 100, false);
        book.Accept(2);

        HeroSave save = hero.Snapshot();
        string text = save.ToText();
        Assert.Contains("quest 1 1\n", text);
        Assert.Contains("quest 2\n", text);
        Assert.Contains("cleared 4\n", text);
        Assert.True(HeroSave.TryParse(text, out HeroSave read, out string error), error);
        Assert.Equal(text, read.ToText());

        HeroProgress fresh = Progress(level: 10);
        fresh.Restore(read, Lookup);
        Assert.Equal(new[] { 1, 2 }, fresh.Quests.Active.Select(a => a.Id).ToArray());
        Assert.Equal(1, fresh.Quests.Find(1).Remaining[0]);
        Assert.Empty(fresh.Quests.Find(2).Remaining);
        Assert.Equal(new[] { 4 }, fresh.Quests.Cleared.ToArray());
        Assert.Equal(2, fresh.QuestsActive);
        Assert.Equal(1, fresh.QuestsCleared);

        // 读档是整个替换:先前有的进度被换掉
        fresh.Quests.Add(8, new[] { 3 });
        fresh.Restore(read, Lookup);
        Assert.False(fresh.Quests.IsActive(8));
    }

    // ------------------------------------------------------------------ 存档文本

    [Fact]
    public void SaveTextParsesQuestLinesAndOldSavesHaveNone()
    {
        string old = "DNFUNITY-SAVE 1\nlevel 5\n";
        Assert.True(HeroSave.TryParse(old, out HeroSave none, out _));
        Assert.Empty(none.Quests);
        Assert.Empty(none.ClearedQuests);

        string text = "DNFUNITY-SAVE 1\nquest 12 3 0 5\nquest 13\ncleared 7\ncleared 9\nfuture-key 1 2 3\n";
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        Assert.Equal(2, save.Quests.Count);
        Assert.Equal(12, save.Quests[0].Id);
        Assert.Equal(new[] { 3, 0, 5 }, save.Quests[0].Remaining);
        Assert.Empty(save.Quests[1].Remaining);
        Assert.Equal(new[] { 7, 9 }, save.ClearedQuests.ToArray());
    }

    [Theory]
    [InlineData("quest\n")]
    [InlineData("quest x\n")]
    [InlineData("quest 0 1\n")]
    [InlineData("quest 5 -1\n")]
    [InlineData("quest 5 1 y\n")]
    [InlineData("cleared\n")]
    [InlineData("cleared 0\n")]
    [InlineData("cleared 3 4\n")]
    [InlineData("cleared z\n")]
    public void BadQuestLinesMakeTheWholeSaveUnreadable(string line)
    {
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\n" + line, out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("第 2 行", error);
    }

    [Fact]
    public void QuestLinesAreLimitedInCounterCount()
    {
        string ok = "DNFUNITY-SAVE 1\nquest 5 " + string.Join(" ", Enumerable.Repeat("1", HeroSave.MaxQuestCounters)) + "\n";
        Assert.True(HeroSave.TryParse(ok, out HeroSave save, out string error), error);
        Assert.Equal(HeroSave.MaxQuestCounters, save.Quests[0].Remaining.Length);
        string tooMany = "DNFUNITY-SAVE 1\nquest 5 " + string.Join(" ", Enumerable.Repeat("1", HeroSave.MaxQuestCounters + 1)) + "\n";
        Assert.False(HeroSave.TryParse(tooMany, out _, out _));
    }

    // ------------------------------------------------------------------ 文字

    [Fact]
    public void ReferenceTagsInDialogueAreReplacedByNames()
    {
        var texts = new QuestTexts(new StrLibrary(_ => null),
            id => id == 5 ? "AAA" : null, id => id == 7 ? "BBB" : null, id => id == 9 ? "CCC" : null);
        Assert.Equal("x AAA y BBB z CCC w", texts.Expand("x <npc::5> y <monster::7> z <apc::9> w"));
        Assert.Equal("a  b", texts.Expand("a <npc::6> b"));                   // 查不到名字:整个标记去掉
        Assert.Equal("a <foo::1> b", texts.Expand("a <foo::1> b"));            // 不认得的种类原样留着
        Assert.Equal("a <npc::x> b", texts.Expand("a <npc::x> b"));
        Assert.Equal("1 < 2 and 3 > 2", texts.Expand("1 < 2 and 3 > 2"));
        Assert.Equal("<npc::5", texts.Expand("<npc::5"));
        Assert.Equal("<npc::123456789012345678901234567890>", texts.Expand("<npc::123456789012345678901234567890>"));   // 太长不当标记
        Assert.Null(texts.Expand(null));
        Assert.Equal(string.Empty, texts.Expand(string.Empty));
    }

    [Fact]
    public void MissingTextTablesGiveNullTexts()
    {
        var texts = new QuestTexts(new StrLibrary(_ => null));
        var quest = new QuestDef(1, "n_quest/x.qst");
        Assert.Null(texts.NameOf(quest));
        Assert.Null(texts.DependOf(quest));
        Assert.Null(texts.ConditionOf(quest));
        Assert.Null(texts.SolveOf(quest));
        Assert.Null(texts.NameOf(null));
        quest.Name = new QuestText(StrLibrary.KindQuest, "key");
        Assert.Null(texts.NameOf(quest));                      // 文字表读不到
        Assert.Throws<ArgumentNullException>(() => new QuestTexts(null));
    }

    [Fact]
    public void ConstructorsRejectMissingParts()
    {
        HeroProgress hero = Progress();
        var catalog = new QuestCatalog(Data());
        Assert.Throws<ArgumentNullException>(() => new QuestBook(null, hero, Lookup));
        Assert.Throws<ArgumentNullException>(() => new QuestBook(catalog, null, Lookup));
        Assert.Throws<ArgumentNullException>(() => new QuestBook(catalog, hero, null));
    }
}
