using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Play.Tests.QuestBookTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 任务窗口(<see cref="QuestWindow"/>、<see cref="QuestWindowArt"/>,M31,本项目自己的占位设计,证据 E-123 的规则)、它在流程里的开关(<see cref="GameFlow.Quests"/>、<see cref="GameFlow.TryTalk"/>)、
/// 提示条与追踪(<see cref="QuestHud"/>、<see cref="QuestHudArt"/>)与 NPC 头顶的标记。任务、物品、NPC、字体全是合成的(方块字体没有任何真实字形)。
/// 任务数据见 <see cref="QuestBookTests"/>:NPC 4 发布 1、2、3、4、6、7、8、9、10 号(10 级的主角可接,共 9 个,多于一屏的 8 行),1 与 7 与 8 在 NPC 4 交付,
/// 1 号有多选一奖励(800 或 801),2 号去 NPC 8 交,3 号去 NPC 9 交(拜访),7 号不能放弃。
/// </summary>
public class QuestWindowTests
{
    private static (int X, int Y) Center((int X, int Y, int W, int H) r) => (r.X + r.W / 2, r.Y + r.H / 2);

    private static (QuestWindow Window, QuestBook Book, HeroProgress Hero) Opened(int npc = 4, int shop = -1, int level = 10)
    {
        (QuestBook book, HeroProgress hero) = Make(level);
        var window = new QuestWindow();
        window.Use(book);
        Assert.True(window.ShowNpc(GameStage.Town, npc, "Alice", shop));
        return (window, book, hero);
    }

    private static bool Click(QuestWindow w, (int X, int Y, int W, int H) rect, out bool shop)
    {
        (int x, int y) = Center(rect);
        return w.Click(x, y, out shop);
    }

    private static void Kill(QuestBook book, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            book.MonsterKilled(7, 0, 100, false);
        }
    }

    // ------------------------------------------------------------------ 打开与列表

    [Fact]
    public void TheWindowOpensOnlyInTownAndOnlyWhenThereIsSomethingToShow()
    {
        (QuestBook book, _) = Make();
        var window = new QuestWindow();
        Assert.False(window.ShowNpc(GameStage.Town, 4, null));                    // 没接任务系统
        Assert.False(window.ShowJournal(GameStage.Town));
        window.Use(book);
        Assert.False(window.ShowNpc(GameStage.Dungeon, 4, null));                 // 副本里没有对话
        Assert.False(window.ShowNpc(GameStage.Town, 99, null));                   // 这个 NPC 没有任务
        Assert.False(window.Open);
        Assert.True(window.ShowNpc(GameStage.Town, 4, "Alice", 3));
        Assert.True(window.Open);
        Assert.Equal((QuestWindowMode.Npc, 4, "Alice", 3), (window.Mode, window.NpcId, window.NpcName, window.ShopIndex));
        Assert.Equal(9, window.Rows.Count);
        Assert.Equal((0, 0), (window.Selected, window.ScrollRow));
        Assert.Equal(1, window.Opens);
        window.Close();
        Assert.False(window.Open);
        Assert.Throws<ArgumentNullException>(() => window.Use(null));
    }

    [Fact]
    public void TheJournalOpensInTownAndDungeonEvenWhenEmpty()
    {
        (QuestBook book, _) = Make();
        var window = new QuestWindow();
        window.Use(book);
        Assert.False(window.ShowJournal(GameStage.DungeonSelect));
        Assert.False(window.ShowJournal(GameStage.Loading));
        Assert.True(window.ShowJournal(GameStage.Dungeon));
        Assert.Equal((QuestWindowMode.Journal, -1, -1), (window.Mode, window.NpcId, window.ShopIndex));
        Assert.Empty(window.Rows);
        Assert.Equal(-1, window.Selected);
        Assert.Null(window.SelectedRow);
        Assert.Equal(new[] { QuestButton.Close }, window.Buttons.ToArray());
        Assert.False(window.Activate());
        Assert.False(window.Abandon());
        window.Move(0, 1);                                                          // 空列表上移动不出错
        Assert.True(window.ShowJournal(GameStage.Town));
        Assert.Equal(2, window.Opens);
    }

    [Fact]
    public void MovingSelectsRowsAndScrollsAPageOfEight()
    {
        (QuestWindow w, _, _) = Opened();
        for (int i = 0; i < 20; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal((8, 1), (w.Selected, w.ScrollRow));                            // 9 行,一屏 8 行
        Assert.Equal(10, w.SelectedRow.Id);
        w.Move(0, -1);
        Assert.Equal((7, 1), (w.Selected, w.ScrollRow));
        for (int i = 0; i < 20; i++)
        {
            w.Move(0, -1);
        }
        Assert.Equal((0, 0), (w.Selected, w.ScrollRow));
        Assert.Equal(1, w.SelectedRow.Id);
    }

    [Fact]
    public void ButtonsFollowTheSelectedRowAndAreRightAligned()
    {
        (QuestWindow w, QuestBook book, _) = Opened(shop: 3);
        Assert.Equal(new[] { QuestButton.Accept, QuestButton.Shop, QuestButton.Close }, w.Buttons.ToArray());
        (int x0, int y0, int w0, int h0) = QuestWindow.ButtonRect(0, 3);
        (int x2, _, int w2, _) = QuestWindow.ButtonRect(2, 3);
        Assert.Equal(QuestWindow.DetailX + QuestWindow.DetailWidth, x2 + w2);       // 关闭贴着详情栏右边缘
        Assert.Equal(QuestWindow.ButtonY, y0);
        Assert.Equal((QuestWindow.ButtonWidth, QuestWindow.ButtonHeight), (w0, h0));
        Assert.True(x0 + w0 < x2);
        Assert.Equal(0, w.HitButton(x0 + 1, y0 + 1));
        Assert.Equal(2, w.HitButton(x2 + 1, y0 + 1));
        Assert.Equal(-1, w.HitButton(x0 - 3, y0 + 1));

        // 选到一个进行中的任务:没有接取按钮;可以交的:换成完成按钮
        book.Accept(7);
        w.Refresh();
        int inProgress = w.Rows.ToList().FindIndex(r => r.Id == 7);
        for (int i = 0; i < inProgress; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal(7, w.SelectedRow.Id);
        Assert.Equal(new[] { QuestButton.Shop, QuestButton.Close }, w.Buttons.ToArray());
        Kill(book);
        w.Refresh();
        Assert.Equal(QuestRowState.Ready, w.Rows.Single(r => r.Id == 7).State);
        Assert.Equal(7, w.SelectedRow.Id);
        Assert.Equal(new[] { QuestButton.Complete, QuestButton.Shop, QuestButton.Close }, w.Buttons.ToArray());
    }

    // ------------------------------------------------------------------ 接取与完成

    [Fact]
    public void AcceptingKeepsTheSelectionOnTheSameQuestAndMovesItToTheInProgressEnd()
    {
        (QuestWindow w, QuestBook book, _) = Opened();
        Assert.Equal(1, w.SelectedRow.Id);
        Assert.True(w.Activate());
        Assert.Equal(QuestActionResult.Ok, w.LastResult);
        Assert.Equal("QUEST ACCEPTED", w.Message);
        Assert.Equal((1, 1, 1), (book.Accepted, w.Accepts, book.Log.Active.Count));
        Assert.Equal(1, w.SelectedRow.Id);
        Assert.Equal(QuestRowState.InProgress, w.SelectedRow.State);
        Assert.Equal((8, 1), (w.Selected, w.ScrollRow));                            // 进行中的排到最后,一屏跟着滚
        Assert.Equal(new[] { QuestButton.Close }, w.Buttons.ToArray());
        Assert.False(w.Activate());                                                 // 进行中的:提示还没做完
        Assert.Equal("NOT DONE YET", w.Message);
        Assert.Equal(1, w.Accepts);
    }

    [Fact]
    public void CompletingTakesTheChosenRewardAndRemovesTheRow()
    {
        (QuestWindow w, QuestBook book, HeroProgress hero) = Opened();
        w.Activate();                                                               // 接 1 号
        Kill(book, 2);
        w.Refresh();
        QuestRow ready = w.Rows[0];
        Assert.Equal((1, QuestRowState.Ready), (ready.Id, ready.State));            // 可以交的排最前
        w.Move(0, -10);
        Assert.Equal(1, w.SelectedRow.Id);
        Assert.Equal(2, ready.Choices.Count);
        w.Move(1, 0);                                                               // 选第二件(801)
        Assert.Equal(1, w.ChoiceIndex);
        w.Move(1, 0);                                                               // 到头了不再动
        Assert.Equal(1, w.ChoiceIndex);
        long exp = hero.Exp;
        Assert.True(w.Activate());
        Assert.Equal("QUEST COMPLETE", w.Message);
        Assert.Equal((1, 1), (w.TurnIns, book.Completed));
        Assert.Equal((0, 1), (hero.Bag.CountOf(800), hero.Bag.CountOf(801)));       // 领的是选中的那一件
        Assert.Equal(2, hero.Bag.CountOf(500));
        Assert.True(hero.Exp > exp || hero.Level > 10);
        Assert.DoesNotContain(w.Rows, r => r.Id == 1);
        Assert.True(book.Log.IsCleared(1));
        Assert.Equal(0, w.ChoiceIndex);
    }

    [Fact]
    public void ChangingTheSelectionResetsTheRewardChoiceAndOnlyReadyRowsPick()
    {
        (QuestWindow w, QuestBook book, _) = Opened();
        w.Move(1, 0);
        Assert.Equal(0, w.ChoiceIndex);                                             // 可接的行不选奖励
        w.Activate();
        Kill(book, 2);
        w.Refresh();
        w.Move(0, -10);
        w.Move(1, 0);
        Assert.Equal(1, w.ChoiceIndex);
        w.Move(0, 1);
        Assert.Equal(0, w.ChoiceIndex);
        w.Move(0, -1);
        Assert.Equal(0, w.ChoiceIndex);
    }

    [Fact]
    public void ANoRoomCompletionKeepsTheQuestAndExplainsWhy()
    {
        (QuestWindow w, QuestBook book, HeroProgress hero) = Opened();
        w.Activate();
        Kill(book, 2);
        FillConsumables(hero);                                                      // 奖励 500 号是消耗品:这一页满了
        w.Refresh();
        w.Move(0, -10);
        Assert.False(w.Activate());
        Assert.Equal((QuestActionResult.NoRoom, "BAG IS FULL"), (w.LastResult, w.Message));
        Assert.Equal(QuestRowState.Ready, w.SelectedRow.State);
        Assert.Equal(0, book.Completed);
        Assert.Equal(0, w.TurnIns);
    }

    [Fact]
    public void AnAvailableQuestThatBecameUnavailableIsRefused()
    {
        (QuestWindow w, QuestBook book, _) = Opened();
        book.Accept(1);                                                             // 窗口外面被接走了
        Assert.False(w.Activate());
        Assert.Equal((QuestActionResult.NotAvailable, "NOT AVAILABLE"), (w.LastResult, w.Message));
        Assert.DoesNotContain(w.Rows, r => r.State == QuestRowState.Available && r.Id == 1);   // 活动列表已经刷新
    }

    [Fact]
    public void TheLastRowGoingAwayLeavesAnEmptyWindowWithTheResult()
    {
        (QuestBook book, _) = Make();
        book.Accept(2);
        book.Accept(3);
        var w = new QuestWindow();
        w.Use(book);
        // NPC 8:2 号收集任务在这里交
        Assert.True(w.ShowNpc(GameStage.Town, 8, null));
        Assert.Equal(2, Assert.Single(w.Rows).Id);
        book.MonsterKilled(7, 0, 100, false);                                       // 怪物 100 掉 900 号物品(掉率 100)
        book.MonsterKilled(7, 0, 100, false);
        w.Refresh();
        Assert.Equal(QuestRowState.Ready, w.SelectedRow.State);
        Assert.True(w.Activate());
        Assert.Empty(w.Rows);
        Assert.Equal(-1, w.Selected);
        Assert.Null(w.SelectedRow);
        Assert.Equal("QUEST COMPLETE", w.Message);
        Assert.Equal(new[] { QuestButton.Close }, w.Buttons.ToArray());
    }

    // ------------------------------------------------------------------ 日志与放弃

    [Fact]
    public void TheJournalListsActiveQuestsAndAbandonNeedsASecondPress()
    {
        (QuestBook book, _) = Make();
        book.Accept(8);
        book.Accept(2);
        var w = new QuestWindow();
        w.Use(book);
        Assert.True(w.ShowJournal(GameStage.Town));
        Assert.Equal(new[] { 8, 2 }, w.Rows.Select(r => r.Id).ToArray());          // 接取次序
        Assert.Equal(new[] { QuestButton.Abandon, QuestButton.Close }, w.Buttons.ToArray());
        Assert.False(w.Activate());                                                 // 日志里回车什么也不做
        Assert.False(w.Abandon());
        Assert.True(w.AbandonArmed);
        Assert.Equal("PRESS ABANDON AGAIN", w.Message);
        w.Move(0, 1);                                                               // 动一下就解除
        Assert.False(w.AbandonArmed);
        w.Move(0, -1);
        Assert.False(w.Abandon());
        Assert.True(w.Abandon());
        Assert.Equal((1, 1), (w.Abandons, book.Abandoned));
        Assert.Equal("QUEST ABANDONED", w.Message);
        Assert.Equal(2, Assert.Single(w.Rows).Id);
        Assert.False(w.AbandonArmed);
    }

    [Fact]
    public void AQuestThatCannotBeGivenUpSaysSo()
    {
        (QuestBook book, _) = Make();
        book.Accept(7);
        var w = new QuestWindow();
        w.Use(book);
        w.ShowJournal(GameStage.Dungeon);
        Assert.False(w.SelectedRow.CanAbandon);
        Assert.False(w.Abandon());
        Assert.False(w.AbandonArmed);
        Assert.Equal((QuestActionResult.CannotGiveUp, "CANNOT ABANDON"), (w.LastResult, w.Message));
        Assert.False(w.Abandon());
        Assert.Equal(0, w.Abandons);
        Assert.Single(w.Rows);
    }

    [Fact]
    public void AbandonAndActivateDoNothingInTheOtherMode()
    {
        (QuestWindow npc, _, _) = Opened();
        Assert.False(npc.Abandon());
        Assert.False(npc.AbandonArmed);
        (QuestBook book, _) = Make();
        book.Accept(1);
        var journal = new QuestWindow();
        journal.Use(book);
        journal.ShowJournal(GameStage.Town);
        Assert.False(journal.Activate());
        Assert.Single(book.Log.Active);
    }

    // ------------------------------------------------------------------ 鼠标

    [Fact]
    public void ClickingARowSelectsItAndClickingTheSelectedRowAgainActs()
    {
        (QuestWindow w, QuestBook book, _) = Opened();
        Assert.True(Click(w, QuestWindow.RowRect(2), out bool shop));
        Assert.False(shop);
        Assert.Equal((2, 3), (w.Selected, w.SelectedRow.Id));
        Assert.Equal(0, book.Accepted);
        Assert.True(Click(w, QuestWindow.RowRect(2), out _));                       // 再点一下 = 回车
        Assert.Equal(1, book.Accepted);
        Assert.True(book.Log.IsActive(3));
        Assert.DoesNotContain(w.Rows, r => r.Id == 3);                              // 3 号去 NPC 9 那里交,这里不再列
    }

    [Fact]
    public void ClickedRowsAccountForTheScrollOffset()
    {
        (QuestWindow w, _, _) = Opened();
        for (int i = 0; i < 20; i++)
        {
            w.Move(0, 1);
        }
        Assert.Equal(1, w.ScrollRow);                                               // 9 行,滚到底
        Assert.True(Click(w, QuestWindow.RowRect(0), out _));                       // 屏上第一行 = 第二行任务
        Assert.Equal((1, 2), (w.Selected, w.SelectedRow.Id));
        Assert.Equal(-1, w.HitRow(QuestWindow.ListX + 1, QuestWindow.ListY + QuestWindow.VisibleRows * (QuestWindow.RowHeight + QuestWindow.RowGap) + 2));
        Assert.Equal(-1, w.HitRow(QuestWindow.ListX - 3, QuestWindow.ListY + 1));
    }

    [Fact]
    public void ClickingTheButtonsAcceptsClosesAndAsksForTheShop()
    {
        (QuestWindow w, QuestBook book, _) = Opened(shop: 3);
        Assert.True(Click(w, QuestWindow.ButtonRect(0, 3), out bool shop));        // 接受
        Assert.False(shop);
        Assert.Equal(1, book.Accepted);
        Assert.Equal(new[] { QuestButton.Shop, QuestButton.Close }, w.Buttons.ToArray());
        Assert.True(Click(w, QuestWindow.ButtonRect(0, 2), out shop));              // 商店:窗口自己不关,交给流程
        Assert.True(shop);
        Assert.True(w.Open);
        Assert.True(Click(w, QuestWindow.ButtonRect(1, 2), out shop));              // 关闭
        Assert.False(shop);
        Assert.False(w.Open);
        Assert.False(w.Click(1, 1, out _));                                         // 关着时点击不处理
    }

    [Fact]
    public void ClickingTheRewardChoicesPicksOneAndClickingElsewhereDoesNothing()
    {
        (QuestWindow w, QuestBook book, _) = Opened();
        w.Activate();
        Kill(book, 2);
        w.Refresh();
        w.Move(0, -10);
        QuestRow row = w.SelectedRow;
        int top = QuestWindowArt.ChoiceTop(row);
        Assert.Equal(QuestWindowArt.DetailBottom - 2 * QuestWindow.ChoiceHeight, top);
        Assert.True(Click(w, QuestWindow.ChoiceRect(top, 1), out _));
        Assert.Equal(1, w.ChoiceIndex);
        Assert.True(Click(w, QuestWindow.ChoiceRect(top, 0), out _));
        Assert.Equal(0, w.ChoiceIndex);
        Assert.False(w.Click(QuestWindow.WindowX + 2, QuestWindow.WindowY + QuestWindow.WindowHeight - 2, out bool shop));
        Assert.False(shop);
        Assert.True(w.Open);
    }

    // ------------------------------------------------------------------ 画法

    private static OverlayArt FontArt() => new OverlayArt { Fonts = new BlockFonts() };

    private static string[] Tags(List<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToArray();

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
        var w = new QuestWindow();
        Assert.Empty(QuestWindowArt.Build(w, new OverlayArt()));
        Assert.Empty(QuestWindowArt.Build(null, new OverlayArt()));
    }

    [Fact]
    public void WithoutFontsTheWindowUsesPlaceholderNamesAndNoDialogue()
    {
        (QuestWindow w, QuestBook book, _) = Opened(shop: 3);
        foreach (QuestRow row in w.Rows)
        {
            row.Name = "数据文字";                                                   // 有名字也画不出来:没有字体
            row.Message = "对白";
        }
        List<SceneSprite> sprites = QuestWindowArt.Build(w, new OverlayArt());
        string[] tags = Tags(sprites);
        Assert.Contains("quest-dim", tags);
        Assert.Contains("quest-panel", tags);
        Assert.Equal(8, tags.Count(t => t == "quest-row"));                          // 一屏 8 行
        Assert.Equal(1, tags.Count(t => t == "quest-row-selected"));
        Assert.Equal(8, tags.Count(t => t == "quest-row-name"));
        Assert.DoesNotContain("quest-message", tags);
        Assert.Contains("quest-name", tags);
        Assert.Contains("quest-info", tags);
        Assert.Equal(3, tags.Count(t => t == "quest-button-label"));
        Assert.Contains("quest-thumb", tags);                                        // 9 行超过一屏:有滚动条
        Assert.Contains("quest-hint", tags);
        Assert.Equal(1, tags.Count(t => t == "quest-button-primary"));
        AssertInsideScreen(sprites);
        Assert.NotNull(book);
    }

    [Fact]
    public void WithFontsTheWindowDrawsNamesDialogueAndRewards()
    {
        (QuestWindow w, _, _) = Opened(shop: 3);
        foreach (QuestRow row in w.Rows)
        {
            row.Name = "任务名" + row.Id;
            row.Message = "对白对白对白对白对白对白对白对白对白对白";
        }
        QuestRow first = w.SelectedRow;
        first.Rewards.Add(new QuestRewardLine(500, 2, "奖励物品"));
        first.Rewards.Add(new QuestRewardLine(501, 1, null));
        List<SceneSprite> sprites = QuestWindowArt.Build(w, FontArt());
        string[] tags = Tags(sprites);
        Assert.Contains("quest-message", tags);
        Assert.Contains("quest-reward-head", tags);
        Assert.Contains("quest-reward-gold", tags);
        Assert.Equal(3, tags.Count(t => t == "quest-reward-item"));                  // 数据里的一件加上这里补的两件
        Assert.Equal(2, tags.Count(t => t == "quest-choice"));
        Assert.Contains("quest-choice-head", tags);
        Assert.DoesNotContain("quest-choice-selected", tags);                        // 可接的任务:多选一只是预览,不画光标
        SceneSprite message = sprites.Single(s => s.AnimationPath == "quest-message");
        SceneSprite rewardHead = sprites.Single(s => s.AnimationPath == "quest-reward-head");
        Assert.True(message.ScreenY + message.Frame.Height <= rewardHead.ScreenY, "对白不压奖励区");
        AssertInsideScreen(sprites);
    }

    [Fact]
    public void AJobChangeRewardGetsItsOwnLineInTheRewardArea()
    {
        (QuestWindow w, _, _) = Opened(shop: 3);
        QuestRow row = w.SelectedRow;
        Assert.Equal(-1, row.Growtype);
        Assert.DoesNotContain("quest-reward-job", Tags(QuestWindowArt.Build(w, FontArt())));
        row.Growtype = 2;
        List<SceneSprite> sprites = QuestWindowArt.Build(w, FontArt());
        string[] tags = Tags(sprites);
        Assert.Equal(1, tags.Count(t => t == "quest-reward-job"));
        SceneSprite job = sprites.Single(s => s.AnimationPath == "quest-reward-job");
        SceneSprite gold = sprites.Single(s => s.AnimationPath == "quest-reward-gold");
        SceneSprite head = sprites.Single(s => s.AnimationPath == "quest-reward-head");
        Assert.True(job.ScreenY + job.Frame.Height <= gold.ScreenY, "转职一行在金币一行上面");
        Assert.True(head.ScreenY + head.Frame.Height <= job.ScreenY, "转职一行在奖励标题下面");
        AssertInsideScreen(sprites);
    }

    [Fact]
    public void AJobChangeRewardAloneStillOpensTheRewardArea()
    {
        (QuestWindow w, _, _) = Opened(shop: 3);
        QuestRow row = w.SelectedRow;
        row.Exp = 0;
        row.Gold = 0;
        row.Rewards.Clear();
        row.Choices.Clear();
        Assert.DoesNotContain("quest-reward-head", Tags(QuestWindowArt.Build(w, FontArt())));
        row.Growtype = 1;
        string[] tags = Tags(QuestWindowArt.Build(w, FontArt()));
        Assert.Contains("quest-reward-head", tags);
        Assert.Contains("quest-reward-job", tags);
        Assert.DoesNotContain("quest-reward-gold", tags);
    }

    [Fact]
    public void TheReadyRowDrawsTheRewardCursorOnTheChosenLine()
    {
        (QuestWindow w, QuestBook book, _) = Opened();
        w.Activate();
        Kill(book, 2);
        w.Refresh();
        w.Move(0, -10);
        w.Move(1, 0);
        List<SceneSprite> sprites = QuestWindowArt.Build(w, FontArt());
        SceneSprite cursor = sprites.Single(s => s.AnimationPath == "quest-choice-selected");
        (int cx, int cy, _, _) = QuestWindow.ChoiceRect(QuestWindowArt.ChoiceTop(w.SelectedRow), 1);
        Assert.Equal((cx - 2, cy - 2), (cursor.ScreenX, cursor.ScreenY));
        Assert.Contains("quest-button-primary", Tags(sprites));
    }

    [Fact]
    public void LongRewardListsAndTheTitleStayInsideTheScreen()
    {
        (QuestWindow w, _, _) = Opened();
        QuestRow row = w.SelectedRow;
        for (int i = 0; i < 12; i++)
        {
            row.Rewards.Add(new QuestRewardLine(600 + i, i + 1, "奖励" + i));
            row.Choices.Add(new QuestRewardLine(700 + i, 1, "选择" + i));
        }
        List<SceneSprite> sprites = QuestWindowArt.Build(w, FontArt());
        string[] tags = Tags(sprites);
        Assert.Equal(QuestWindowArt.MaxRewardLines, tags.Count(t => t == "quest-reward-item"));
        Assert.Equal(QuestWindowArt.MaxChoiceLines, tags.Count(t => t == "quest-choice"));
        AssertInsideScreen(sprites);
    }

    [Fact]
    public void TheTitleIsTheNpcNameOnlyWhenFontsAreInstalled()
    {
        (QuestWindow w, _, _) = Opened();
        Assert.Equal("Alice", QuestWindowArt.TitleText(w, FontArt()));
        Assert.Equal("QUESTS", QuestWindowArt.TitleText(w, new OverlayArt()));
        (QuestBook book, _) = Make();
        var journal = new QuestWindow();
        journal.Use(book);
        journal.ShowJournal(GameStage.Town);
        Assert.Equal("QUESTS", QuestWindowArt.TitleText(journal, FontArt()));
        Assert.Contains("quest-empty", Tags(QuestWindowArt.Build(journal, FontArt())));
    }

    [Fact]
    public void TheNearHintIsABarWithText()
    {
        List<SceneSprite> hint = QuestWindowArt.BuildNearHint(new OverlayArt());
        Assert.Equal(new[] { "quest-near-bg", "quest-near-hint" }, Tags(hint));
        AssertInsideScreen(hint);
        Assert.Equal(QuestWindowArt.NearHintAlpha, hint[0].TintAlpha);
    }

    // ------------------------------------------------------------------ 流程

    private sealed class FakeTown : GameFlowTests.FakeHero, ITownNpcSource
    {
        public FakeTown(params TownNpc[] npcs) : base("town")
        {
            Npcs = npcs;
        }

        public IReadOnlyList<TownNpc> Npcs { get; }

        public double HeroX { get; set; }

        public double HeroY { get; set; }
    }

    private sealed class FakeRun : GameFlowTests.FakeHero, IRunSession
    {
        public FakeRun(string path, int run) : base("run " + path + " #" + run)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }
    }

    private static readonly DungeonEntry Entry = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private static (GameFlow Flow, QuestBook Book, HeroProgress Hero, FakeTown Town) NewFlow(FakeTown town = null, bool withQuests = true, bool withShops = true)
    {
        town ??= new FakeTown(new TownNpc(4, 500, 100, 0)) { HeroX = 500, HeroY = 100 };
        var flow = new GameFlow(() => town, (path, run) => new FakeRun(path, run), new[] { Entry });
        (QuestBook book, HeroProgress hero) = Make();
        if (withQuests)
        {
            flow.UseQuests(book);
            flow.UseNpcNames(id => id == 4 ? "Alice" : null);
        }
        if (withShops)
        {
            flow.Shop.Use(ShopWindowTests.Book());
            flow.UseShopNpcs(new TownShopNpcs(id => id == 4 ? 3 : id == 6 ? 4 : -1));
        }
        flow.EnterTown();
        return (flow, book, hero, town);
    }

    [Fact]
    public void SpaceTalksToTheNearestQuestNpcAndTheWindowHasTheShopButton()
    {
        (GameFlow flow, QuestBook book, _, _) = NewFlow();
        Assert.Equal(QuestMark.Available, flow.QuestMarkOf(4));
        Assert.Equal(GameFlow.TalkKind.Quest, flow.FindNearbyTalk(out TownNpc npc));
        Assert.Equal(4, npc.Id);
        Assert.True(flow.TryTalk());
        Assert.True(flow.Quests.Open);
        Assert.False(flow.Shop.Open);
        Assert.Equal((4, "Alice", 3), (flow.Quests.NpcId, flow.Quests.NpcName, flow.Quests.ShopIndex));
        Assert.False(flow.TryTalk());                                               // 已经开着
        Assert.False(flow.Poll());                                                  // 开着时不处理各阶段的结果
        Assert.False(flow.ToggleBag());
        Assert.False(flow.OpenShop(3));
        Assert.True(flow.ToggleQuests());                                           // L 键:开着就关上
        Assert.False(flow.Quests.Open);
        Assert.Equal(0, book.Accepted);
    }

    [Fact]
    public void TheShopButtonClosesTheQuestWindowAndOpensTheShop()
    {
        (GameFlow flow, _, _, _) = NewFlow();
        Assert.True(flow.TryTalk());
        Assert.True(flow.QuestsToShop());
        Assert.False(flow.Quests.Open);
        Assert.True(flow.Shop.Open);
        Assert.Equal(3, flow.Shop.Shop.Index);
        Assert.False(flow.QuestsToShop());                                          // 窗口已经关了
    }

    [Fact]
    public void AnNpcWithoutRowsFallsBackToItsShopAndAShopOnlyNpcOpensTheShop()
    {
        var town = new FakeTown(new TownNpc(4, 500, 100, 0), new TownNpc(6, 900, 100, 0)) { HeroX = 500, HeroY = 100 };
        (GameFlow flow, QuestBook book, _, _) = NewFlow(town);
        // 4 号 NPC 的任务全接走:没有标记、没有任务行,剩下它的商店
        foreach (int id in new[] { 1, 2, 3, 4, 6, 7, 8, 9, 10 })
        {
            book.Accept(id);
        }
        Assert.Equal(QuestMark.InProgress, flow.QuestMarkOf(4));                    // 进行中的任务要在 4 号这里交
        Assert.Equal(GameFlow.TalkKind.Quest, flow.FindNearbyTalk(out _));
        town.HeroX = 900;
        Assert.Equal(GameFlow.TalkKind.Shop, flow.FindNearbyTalk(out TownNpc npc));
        Assert.Equal(6, npc.Id);
        Assert.True(flow.TryTalk());
        Assert.True(flow.Shop.Open);
        Assert.Equal(4, flow.Shop.Shop.Index);
        Assert.False(flow.Quests.Open);
    }

    [Fact]
    public void OutOfRangeAndWindowlessFlowsDoNotTalk()
    {
        var town = new FakeTown(new TownNpc(4, 500, 100, 0)) { HeroX = 5000, HeroY = 100 };
        (GameFlow flow, _, _, _) = NewFlow(town);
        Assert.Equal(GameFlow.TalkKind.None, flow.FindNearbyTalk(out _));
        Assert.False(flow.TryTalk());
        town.HeroX = 500;
        Assert.True(flow.TryTalk());

        (GameFlow bare, _, _, _) = NewFlow(withQuests: false, withShops: false);
        Assert.Equal(QuestMark.None, bare.QuestMarkOf(4));
        Assert.Equal(GameFlow.TalkKind.None, bare.FindNearbyTalk(out _));
        Assert.False(bare.TryTalk());
        Assert.False(bare.ToggleQuests());
        Assert.False(bare.QuestsToShop());
        Assert.Throws<ArgumentNullException>(() => bare.UseQuests(null));
        Assert.Throws<ArgumentNullException>(() => bare.UseNpcNames(null));
    }

    [Fact]
    public void TheJournalTogglesWithLAndEscClosesTheQuestWindowFirst()
    {
        (GameFlow flow, _, _, _) = NewFlow();
        Assert.True(flow.ToggleQuests());
        Assert.True(flow.Quests.Open);
        Assert.Equal(QuestWindowMode.Journal, flow.Quests.Mode);
        Assert.False(flow.ToggleBag());                                             // 一次只开一个窗口
        Assert.True(flow.ToggleMenu());                                             // Esc:先关任务窗口,不开系统菜单
        Assert.False(flow.Quests.Open);
        Assert.False(flow.Menu.Open);
        Assert.True(flow.ToggleMenu());                                             // 再按才开系统菜单
        Assert.True(flow.Menu.Open);
        Assert.False(flow.ToggleQuests());                                          // 菜单开着不开日志
        Assert.False(flow.Quests.Open);
    }

    [Fact]
    public void ChangingStageClosesTheQuestWindowAndTheJournalWorksInTheDungeonToo()
    {
        (GameFlow flow, _, _, _) = NewFlow();
        Assert.True(flow.TryTalk());
        flow.StartDungeon(Entry.Path);
        Assert.False(flow.Quests.Open);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.False(flow.TryTalk());                                               // 副本里没有对话
        Assert.True(flow.ToggleQuests());
        Assert.Equal(QuestWindowMode.Journal, flow.Quests.Mode);
        Assert.Equal(ItemUse.UnavailableName, flow.UseQuickSlot(0));                // 窗口开着不能用快捷栏
        flow.EnterTown();
        Assert.False(flow.Quests.Open);
    }

    [Fact]
    public void ATalkToTheTargetNpcCompletesAVisitQuestAndOffersTheTurnIn()
    {
        var town = new FakeTown(new TownNpc(9, 500, 100, 0)) { HeroX = 500, HeroY = 100 };
        (GameFlow flow, QuestBook book, HeroProgress hero, _) = NewFlow(town, withShops: false);
        Assert.Equal(QuestActionResult.Ok, book.Accept(3));                         // 去 NPC 9 那里
        Assert.Equal(QuestMark.Ready, flow.QuestMarkOf(9));
        Assert.True(flow.TryTalk());                                                // 说话 = 完成拜访目标
        QuestWindow w = flow.Quests;
        Assert.Equal(3, w.SelectedRow.Id);
        Assert.Equal(QuestRowState.Ready, w.SelectedRow.State);
        Assert.True(w.Activate());
        Assert.Equal(1, hero.Bag.CountOf(500));
        Assert.Equal(1, book.Completed);
    }

    [Fact]
    public void MarkCountsAndTheProbeHelpersLookAtEveryNpcInTheTown()
    {
        var town = new FakeTown(new TownNpc(4, 500, 100, 0), new TownNpc(9, 800, 100, 0), new TownNpc(6, 1100, 100, 0)) { HeroX = 5000, HeroY = 100 };
        (GameFlow flow, QuestBook book, _, _) = NewFlow(town);
        Assert.Equal(1, flow.CountQuestMarks(out int available, out int ready, out int inProgress));   // 只有 4 号有任务可接
        Assert.Equal((1, 0, 0), (available, ready, inProgress));
        Assert.Equal(4, flow.FirstNpcWithMark(QuestMark.Available));
        Assert.Equal(-1, flow.FirstNpcWithMark(QuestMark.Ready));

        book.Accept(3);                                                             // 去 9 号那里:9 号头顶是"可以交"
        Assert.Equal(2, flow.CountQuestMarks(out available, out ready, out inProgress));
        Assert.Equal((1, 1, 0), (available, ready, inProgress));
        Assert.Equal(9, flow.FirstNpcWithMark(QuestMark.Ready));
        book.Accept(1);                                                             // 1 号在 4 号这里交:4 号还有别的可接,可接盖过进行中
        Assert.Equal(QuestMark.Available, flow.QuestMarkOf(4));

        Assert.Equal(0, NewFlow(withQuests: false).Flow.CountQuestMarks(out available, out ready, out inProgress));
        Assert.Equal((0, 0, 0), (available, ready, inProgress));
        Assert.Equal(-1, NewFlow(withQuests: false).Flow.FirstNpcWithMark(QuestMark.Available));
    }

    [Fact]
    public void OpenQuestNpcIgnoresDistanceButNotTheOtherGuards()
    {
        var town = new FakeTown(new TownNpc(4, 500, 100, 0)) { HeroX = 5000, HeroY = 100 };
        (GameFlow flow, _, _, _) = NewFlow(town);
        Assert.False(flow.TryTalk());                                               // 太远
        Assert.True(flow.OpenQuestNpc(4));                                          // 探针不看距离
        Assert.Equal(4, flow.Quests.NpcId);
        Assert.False(flow.OpenQuestNpc(4));                                         // 已经开着
        flow.Quests.Close();
        Assert.True(flow.ToggleBag());
        Assert.False(flow.OpenQuestNpc(4));                                         // 别的窗口开着
        flow.Bag.Close();
        flow.StartDungeon(Entry.Path);
        Assert.False(flow.OpenQuestNpc(4));                                         // 副本里没有对话
        Assert.False(NewFlow(withQuests: false).Flow.OpenQuestNpc(4));              // 没接任务系统
    }

    // ------------------------------------------------------------------ 提示条与追踪

    private static QuestNotice Notice(QuestNoticeKind kind, int id = 1, string detail = "name", string extra = null)
    {
        return new QuestNotice(kind, id, "QUEST", detail, extra);
    }

    [Fact]
    public void ToastsFadeOutExpireAndMergeProgress()
    {
        var hud = new QuestHud();
        hud.Add(Notice(QuestNoticeKind.Accepted));
        hud.Add(Notice(QuestNoticeKind.Progress, 1, "a 1/3"));
        Assert.Equal(2, hud.Toasts.Count);
        hud.Advance(1000, null);
        hud.Add(Notice(QuestNoticeKind.Progress, 1, "a 2/3"));                      // 同一个任务的进度合并、重新计时
        Assert.Equal(2, hud.Toasts.Count);
        Assert.Equal(2, hud.ToastsShown);
        Assert.Equal("a 2/3", hud.Toasts[1].Notice.Detail);
        Assert.Equal(0, hud.Toasts[1].AgeMs);
        Assert.Equal(255, hud.Toasts[0].Alpha);
        hud.Advance(QuestHud.ToastLifeMs - 1000 - QuestHud.ToastFadeMs / 2, null);   // 第一条还剩半个淡出期
        Assert.InRange(hud.Toasts[0].Alpha, 120, 135);
        hud.Advance(QuestHud.ToastFadeMs, null);
        Assert.Single(hud.Toasts);                                                  // 第一条过期了,第二条还在
        hud.Advance(QuestHud.ToastLifeMs, null);
        Assert.Empty(hud.Toasts);
    }

    [Fact]
    public void OnlyTheNewestFourToastsStayAndNullNoticesAreIgnored()
    {
        var hud = new QuestHud();
        for (int i = 1; i <= 6; i++)
        {
            hud.Add(Notice(QuestNoticeKind.Accepted, i, "q" + i));
        }
        hud.Add(null);
        Assert.Equal(QuestHud.MaxToasts, hud.Toasts.Count);
        Assert.Equal(new[] { "q3", "q4", "q5", "q6" }, hud.Toasts.Select(t => t.Notice.Detail).ToArray());
        Assert.Equal(6, hud.ToastsShown);
        hud.Clear();
        Assert.Empty(hud.Toasts);
        hud.Advance(-5, null);                                                      // 负的时间当 0
        Assert.Empty(hud.Toasts);
    }

    [Fact]
    public void TheHudPullsNoticesFromTheBookAndTracksReadyQuestsFirst()
    {
        (QuestBook book, _) = Make();
        var hud = new QuestHud();
        hud.Advance(16, book);
        Assert.Empty(hud.Toasts);
        Assert.Empty(hud.Tracked);
        book.Accept(8);
        book.Accept(7);
        book.Accept(2);
        book.Accept(4);
        hud.Advance(16, book);
        Assert.Equal(QuestHud.MaxToasts, hud.Toasts.Count);                          // 四个接取提示
        Assert.Equal(QuestHud.TrackedMax, hud.Tracked.Count);
        Assert.Equal(new[] { 8, 7, 2 }, hud.Tracked.Select(r => r.Id).ToArray());
        Kill(book);                                                                  // 7 号只要一只:可以交了,排到追踪最前
        hud.Advance(16, book);
        Assert.Equal(7, hud.Tracked[0].Id);
        Assert.Equal(QuestRowState.Ready, hud.Tracked[0].State);
        Assert.Equal(QuestHud.TrackedMax, hud.Tracked.Count);
        book.Abandon(8);
        hud.RefreshTracker(book);
        Assert.DoesNotContain(hud.Tracked, r => r.Id == 8);
        hud.RefreshTracker(null);
        Assert.Empty(hud.Tracked);
    }

    [Fact]
    public void TheTrackerOnlyRefreshesWhenDueOrWhenNoticesArrive()
    {
        (QuestBook book, _) = Make();
        var hud = new QuestHud();
        hud.Advance(16, book);
        book.Accept(8);
        book.TakeNotices();                                                          // 没有新提示:追踪要等刷新间隔
        hud.Advance(16, book);
        Assert.Empty(hud.Tracked);
        hud.Advance(QuestHud.TrackerRefreshMs, book);
        Assert.Single(hud.Tracked);
    }

    [Fact]
    public void ToastAndTrackerArtStayInsideTheScreen()
    {
        (QuestBook book, _) = Make();
        book.Accept(8);
        book.Accept(7);
        var hud = new QuestHud();
        hud.Advance(16, book);
        hud.Add(Notice(QuestNoticeKind.Completed, 5, "完成的任务", "EXP 100  GOLD 20"));
        hud.Add(Notice(QuestNoticeKind.BagFull, 0, null, null));
        List<SceneSprite> sprites = QuestHudArt.Build(hud, FontArt(), true);
        string[] tags = Tags(sprites);
        Assert.Contains("quest-toast-bg", tags);
        Assert.Contains("quest-toast-extra", tags);
        Assert.Contains("quest-tracker-bg", tags);
        Assert.Equal(2, tags.Count(t => t == "quest-tracker-name"));
        AssertInsideScreen(sprites);
        // 没有字体:提示条里的数据文字不画,标题照画
        List<SceneSprite> bare = QuestHudArt.Build(hud, new OverlayArt(), false);
        Assert.DoesNotContain("quest-toast-detail", Tags(bare));
        Assert.Contains("quest-toast-title", Tags(bare));
        Assert.DoesNotContain("quest-tracker-bg", Tags(bare));
        Assert.Empty(QuestHudArt.Build(new QuestHud(), FontArt(), true));
    }

    [Fact]
    public void ToastsFadeThroughTheTintAlpha()
    {
        var hud = new QuestHud();
        hud.Add(Notice(QuestNoticeKind.Ready));
        hud.Advance(QuestHud.ToastLifeMs - QuestHud.ToastFadeMs / 2, null);
        List<SceneSprite> sprites = QuestHudArt.Build(hud, FontArt(), false);
        SceneSprite title = sprites.Single(s => s.AnimationPath == "quest-toast-title");
        SceneSprite bg = sprites.Single(s => s.AnimationPath == "quest-toast-bg");
        Assert.InRange(title.TintAlpha, 120, 135);
        Assert.True(bg.TintAlpha <= QuestHudArt.ToastAlpha);
    }

    // ------------------------------------------------------------------ NPC 头顶标记

    private static SpriteFrame Block(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        Array.Fill(rgba, (byte)255);
        return new SpriteFrame(w, h, 0, 0, w, h, rgba);
    }

    [Fact]
    public void MarksSitAboveTheNpcAndTheNameplateAndBob()
    {
        var npcs = new[] { new TownNpc(4, 500, 300, 0), new TownNpc(5, 700, 300, 0), new TownNpc(6, 900, 300, 0), new TownNpc(7, 100, 300, 0) };
        var scene = new List<SceneSprite>
        {
            new SceneSprite(Block(40, 60), 460, 200, 1, 4, "npc/a.ani", 0),
            new SceneSprite(Block(40, 60), 660, 200, 1, 5, "npc/b.ani", 0),
            new SceneSprite(Block(40, 60), 860, 200, 1, 6, "npc/c.ani", 0),
        };
        QuestMark MarkOf(int id) => id == 4 ? QuestMark.Available : id == 5 ? QuestMark.Ready : id == 7 ? QuestMark.InProgress : QuestMark.None;
        var into = new List<SceneSprite>();
        int order = 0;
        int added = QuestHudArt.AddNpcMarks(into, ref order, new OverlayArt(), MarkOf, scene, npcs, 0, 0.0);
        Assert.Equal(2, added);                                                      // 6 号没有标记;7 号画面里没有精灵
        Assert.All(into, s => Assert.Equal("quest-mark", s.AnimationPath));
        SceneSprite m = into[0];
        int shift = NamePlates.NpcCenterShift;
        Assert.Equal(500 + shift - m.Frame.Width / 2, m.ScreenX);
        Assert.Equal(200 - (NamePlates.NpcInkAboveSprite + QuestHudArt.MarkGap) - m.Frame.Height, m.ScreenY);   // 没有字体:没有名牌,紧贴精灵上方
        // 有字体时名牌占一截:标记再往上
        var withPlates = new List<SceneSprite>();
        order = 0;
        QuestHudArt.AddNpcMarks(withPlates, ref order, FontArt(), MarkOf, scene, npcs, 0, 0.0);
        Assert.Equal(m.ScreenY - QuestHudArt.PlateHeight, withPlates[0].ScreenY);
        // 上下浮动:周期的四分之一处最高偏移
        var bobbed = new List<SceneSprite>();
        order = 0;
        QuestHudArt.AddNpcMarks(bobbed, ref order, new OverlayArt(), MarkOf, scene, npcs, 0, QuestHudArt.BobPeriodMs / 4.0);
        Assert.Equal(m.ScreenY + QuestHudArt.BobAmplitude, bobbed[0].ScreenY);
        // 镜头移动:标记跟着 NPC 的屏幕 x
        var moved = new List<SceneSprite>();
        order = 0;
        QuestHudArt.AddNpcMarks(moved, ref order, new OverlayArt(), MarkOf, scene, npcs, 20, 0.0);
        Assert.Equal(m.ScreenX - 20, moved[0].ScreenX);
        // 没接任务系统或没有场景:什么也不加
        Assert.Equal(0, QuestHudArt.AddNpcMarks(new List<SceneSprite>(), ref order, new OverlayArt(), null, scene, npcs, 0, 0.0));
        Assert.Equal(0, QuestHudArt.AddNpcMarks(new List<SceneSprite>(), ref order, new OverlayArt(), MarkOf, null, npcs, 0, 0.0));
    }

    [Fact]
    public void MarkGlyphsAndColorsFollowTheState()
    {
        Assert.Equal("!", QuestHudArt.MarkGlyph(QuestMark.Available));
        Assert.Equal("?", QuestHudArt.MarkGlyph(QuestMark.Ready));
        Assert.Equal("?", QuestHudArt.MarkGlyph(QuestMark.InProgress));
        Assert.Equal(QuestHudArt.MarkAvailableColor, QuestHudArt.MarkColor(QuestMark.Available));
        Assert.Equal(QuestHudArt.MarkReadyColor, QuestHudArt.MarkColor(QuestMark.Ready));
        Assert.Equal(QuestHudArt.MarkInProgressColor, QuestHudArt.MarkColor(QuestMark.InProgress));
        Assert.NotEqual(QuestHudArt.MarkColor(QuestMark.Ready), QuestHudArt.MarkColor(QuestMark.InProgress));
        foreach (QuestNoticeKind kind in Enum.GetValues(typeof(QuestNoticeKind)))
        {
            Assert.False(string.IsNullOrEmpty(QuestHudArt.ToastTitle(kind)));
        }
        Assert.Equal(OverlayArt.Orange, QuestHudArt.ToastColor(QuestNoticeKind.BagFull));
    }
}
