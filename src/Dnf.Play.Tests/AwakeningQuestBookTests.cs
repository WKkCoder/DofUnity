using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Tests;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 觉醒任务奖励的接线(r15-awaken;服务端 <c>quest_reward</c> 的奖励类型 2 <c>[awakening type]</c> → <c>set_grow_type</c> 的第二成长类型):
/// 交任务(或条件满足自动完成)时主角觉醒到奖励给的号(白送觉醒技能、面板换觉醒块、觉醒点与技能点不动、属性版本加一);
/// 等级不到 50、次序不对、没转职的,只完成任务、不觉醒。任务、技能与数值全是合成的(编号 911 起),与原版数据无关。
///
/// 合成的任务(全是成就类、<c>[job change quest]</c> 2、50 到 99 级,NPC 7 发布):
/// 911 通关副本 7(自动完成)奖励一觉;912 要先完成 911、通关副本 9 后回 NPC 6 交,奖励二觉;913 没有前置、通关副本 8 自动完成,奖励二觉(次序不对的坏数据);
/// 914 奖励整数 7(越界,不支持);915 过房间 30,没有奖励(链上的普通一环)。主角、技能与成长表见 <see cref="AwakeningProgressTests.Hero"/>。
/// </summary>
public class AwakeningQuestBookTests
{
    private static GameData Data()
    {
        var fx = new Fixture();
        fx.List("n_quest/quest.lst", (911, "a.qst"), (912, "b.qst"), (913, "c.qst"), (914, "d.qst"), (915, "e.qst"));
        fx.Tokens("n_quest/a.qst", w => w.Tag("[grade]").Str("[achievement]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6).Tag("[job change quest]").Int(2)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(50).Int(99).Tag("[difficulty]").Str("S")
            .Tag("[int data]").Ints(7, -1).Tag("[/int data]")
            .Tag("[reward type]").Str("[awakening type]")
            .Tag("[reward int data]").Int(1).Tag("[/reward int data]"));
        fx.Tokens("n_quest/b.qst", w => w.Tag("[grade]").Str("[achievement]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6).Tag("[job change quest]").Int(2)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(6)
            .Tag("[level]").Int(50).Int(99).Tag("[difficulty]").Str("S")
            .Tag("[pre required quest]").Int(911).Tag("[/pre required quest]")
            .Tag("[int data]").Ints(9, -1).Tag("[/int data]")
            .Tag("[reward type]").Str("[awakening type]")
            .Tag("[reward int data]").Int(2).Tag("[/reward int data]"));
        fx.Tokens("n_quest/c.qst", w => w.Tag("[grade]").Str("[achievement]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6).Tag("[job change quest]").Int(2)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(50).Int(99).Tag("[difficulty]").Str("S")
            .Tag("[int data]").Ints(8, -1).Tag("[/int data]")
            .Tag("[reward type]").Str("[awakening type]")
            .Tag("[reward int data]").Int(2).Tag("[/reward int data]"));
        fx.Tokens("n_quest/d.qst", w => w.Tag("[grade]").Str("[achievement]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6).Tag("[job change quest]").Int(2)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(50).Int(99)
            .Tag("[int data]").Ints(7, -1).Tag("[/int data]")
            .Tag("[reward type]").Str("[awakening type]")
            .Tag("[reward int data]").Int(7).Tag("[/reward int data]"));
        fx.Tokens("n_quest/e.qst", w => w.Tag("[grade]").Str("[achievement]").Tag("[type]").Str("[clear map]")
            .Tag("[job change quest]").Int(2)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(50).Int(99)
            .Tag("[int data]").Int(30).Tag("[/int data]"));
        fx.Tokens("n_quest/questparameter.etc", w =>
        {
            w.Tag("[difficulty]").Str("S").Int(110).Tag("[/difficulty]");
            w.Tag("[green level penalty]").Int(80);
            w.Tag("[grey level penalty]").Int(30);
            w.Tag("[exp reward table]");
            for (int i = 0; i < 100; i++)
            {
                w.Int(i * 10);
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

    private static (QuestBook Book, HeroProgress Hero) Make(int level = 50, int growtype = 1)
    {
        HeroProgress hero = AwakeningProgressTests.Hero(level, growtype);
        var book = new QuestBook(new QuestCatalog(Data()), hero, _ => null, monsterExists: _ => true, seed: 5);
        return (book, hero);
    }

    // ------------------------------------------------------------------ 列出与行

    [Fact]
    public void TheChainIsListedInOrderAndTheBadShapeIsNot()
    {
        (QuestBook book, _) = Make();
        // 912 要先完成 911;914 奖励越界不支持
        Assert.Equal(new[] { 911, 913, 915 }, book.RowsFor(7).Select(r => r.Id).ToArray());
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(912));
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(914));
        Assert.Equal(QuestMark.Available, book.MarkOf(7));
    }

    [Fact]
    public void TheRowShowsTheAwakeningAndOtherRowsShowNone()
    {
        (QuestBook book, _) = Make();
        Assert.Equal(1, book.RowOf(911).Awakening);
        Assert.Equal(-1, book.RowOf(911).Growtype);                // 觉醒不是转职
        Assert.Equal(0, book.RowOf(915).Awakening);
        Assert.Equal(0, new QuestRow().Awakening);
        Assert.True(book.RowOf(911).Exp > 0);
    }

    [Fact]
    public void BelowFiftyTheChainIsNotOfferedAndCannotBeAccepted()
    {
        (QuestBook book, _) = Make(level: 49);
        Assert.Empty(book.RowsFor(7));
        Assert.Equal(QuestMark.None, book.MarkOf(7));
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(911));
        (QuestBook atFifty, _) = Make(level: 50);
        Assert.Equal(QuestActionResult.Ok, atFifty.Accept(911));
    }

    // ------------------------------------------------------------------ 自动完成

    [Fact]
    public void ClearingTheDungeonCompletesTheQuestAndAwakensTheHero()
    {
        (QuestBook book, HeroProgress hero) = Make();
        int version = hero.Version;
        int skillsVersion = hero.SkillsVersion;
        Assert.Equal(QuestActionResult.Ok, book.Accept(911));
        book.TakeNotices();
        Assert.Equal(0, hero.Awakening);
        book.DungeonCleared(7, 0, 90000);

        Assert.Equal(1, hero.Awakening);
        Assert.Equal(1, hero.Growtype);                            // 成长类型不动
        Assert.True(hero.Version > version);
        Assert.True(hero.SkillsVersion > skillsVersion);
        Assert.Equal(1, book.Awakenings);
        Assert.Equal(0, book.JobChanges);
        Assert.Equal(1, book.Completed);
        Assert.True(book.Log.IsCleared(911));
        Assert.Equal(1, hero.Skills.LevelOf(903));                 // 觉醒技能进了技能簿
        Assert.Equal(1, hero.Skills.LevelOf(904));
        QuestNotice done = Assert.Single(book.TakeNotices(), n => n.Kind == QuestNoticeKind.Completed);
        Assert.Contains("AWAKENING", done.Extra);
        Assert.Contains("EXP", done.Extra);
        Assert.DoesNotContain("JOB CHANGE", done.Extra);
    }

    [Fact]
    public void TheAwakeningKeepsLearnedSkillsAndPointsAndTheExpIsStillGiven()
    {
        (QuestBook book, HeroProgress hero) = Make(level: 60);
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(902, 2, out _));
        int sp = hero.SkillPoints;
        int sfp = hero.AwakeningPoints;
        int exp = hero.Exp;
        int expected = book.RowOf(911).Exp;
        Assert.True(expected > 0);
        book.Accept(911);
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(1, hero.Awakening);
        Assert.Equal(3, hero.Skills.LevelOf(902));                 // 学过的技能还在
        Assert.Equal(sp, hero.SkillPoints);                        // 点数不回收也不多给(这里经验不够升级)
        Assert.Equal(sfp, hero.AwakeningPoints);
        Assert.Equal(exp + expected, hero.Exp);
    }

    // ------------------------------------------------------------------ 链与手动交

    [Fact]
    public void TheSecondLinkAppearsAfterTheFirstAndAwakensTheHeroASecondTimeWhenTurnedIn()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(911);
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(1, hero.Awakening);
        Assert.Equal(new[] { 912, 913, 915 }, book.RowsFor(7).Select(r => r.Id).ToArray());

        Assert.Equal(QuestActionResult.Ok, book.Accept(912));
        book.DungeonCleared(9, 0, 1000);
        Assert.Equal(QuestRowState.Ready, book.RowOf(912).State);
        Assert.Equal(1, hero.Awakening);                           // 条件满足了,但要回 NPC 6 那里交
        Assert.Equal(1, book.Awakenings);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(912));
        Assert.Equal(2, hero.Awakening);
        Assert.Equal(2, book.Awakenings);
        Assert.Equal(1, hero.Skills.LevelOf(905));                 // 二觉的白送
        Assert.Equal(1, hero.Skills.LevelOf(903));                 // 一觉的没有收回
    }

    [Fact]
    public void TurningInTwiceAwakensOnlyOnce()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(911);
        book.DungeonCleared(7, 0, 1000);
        book.Accept(912);
        book.DungeonCleared(9, 0, 1000);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(912));
        int version = hero.Version;
        Assert.Equal(QuestActionResult.NotFound, book.TurnIn(912));
        Assert.Equal(version, hero.Version);
        Assert.Equal(2, book.Awakenings);
    }

    // ------------------------------------------------------------------ 觉醒不了的

    [Fact]
    public void ASecondAwakeningRewardBeforeTheFirstOnlyCompletesTheQuest()
    {
        (QuestBook book, HeroProgress hero) = Make();
        Assert.Equal(QuestActionResult.Ok, book.Accept(913));
        book.TakeNotices();
        int version = hero.Version;
        book.DungeonCleared(8, 0, 1000);
        Assert.True(book.Log.IsCleared(913));
        Assert.Equal(1, book.Completed);
        Assert.Equal(0, hero.Awakening);                           // 一觉都没有就二觉:次序不对
        Assert.Equal(0, book.Awakenings);
        Assert.Equal(0, hero.Skills.LevelOf(905));
        QuestNotice done = Assert.Single(book.TakeNotices(), n => n.Kind == QuestNoticeKind.Completed);
        Assert.DoesNotContain("AWAKENING", done.Extra);
        Assert.True(hero.Version >= version);
    }

    [Fact]
    public void AHeroThatDroppedBelowFiftyAfterAcceptingCompletesWithoutAwakening()
    {
        (QuestBook book, HeroProgress hero) = Make(level: 50);
        Assert.Equal(QuestActionResult.Ok, book.Accept(911));
        hero.SetLevel(49);                                         // 接了之后等级回落(调试开关、读旧档)
        book.DungeonCleared(7, 0, 1000);
        Assert.True(book.Log.IsCleared(911));
        Assert.Equal(0, hero.Awakening);
        Assert.Equal(0, book.Awakenings);
        Assert.Equal(1, book.Completed);
    }

    [Fact]
    public void AHeroThatNeverTransferredCompletesWithoutAwakening()
    {
        (QuestBook book, HeroProgress hero) = Make(level: 50, growtype: 0);
        Assert.Equal(QuestActionResult.Ok, book.Accept(911));
        book.DungeonCleared(7, 0, 1000);
        Assert.True(book.Log.IsCleared(911));
        Assert.Equal(0, hero.Awakening);
        Assert.Equal(0, book.Awakenings);
    }

    [Fact]
    public void OrdinaryChainLinksNeverTouchTheAwakening()
    {
        (QuestBook book, HeroProgress hero) = Make();
        Assert.Equal(QuestActionResult.Ok, book.Accept(915));
        book.MapCleared(30);
        Assert.True(book.Log.IsCleared(915));
        Assert.Equal(0, hero.Awakening);
        Assert.Equal(0, book.Awakenings);
        Assert.Equal(1, book.Completed);
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void TheAwakeningAndTheClearedChainSurviveASaveRoundTrip()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(911);
        book.DungeonCleared(7, 0, 1000);
        HeroSave save = hero.Snapshot();
        Assert.Equal(1, save.Awakening);
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave parsed, out string error), error);

        HeroProgress other = AwakeningProgressTests.Hero(1, 0, false);
        other.Restore(parsed, _ => null);
        Assert.Equal(1, other.Awakening);
        Assert.Equal(1, other.Skills.LevelOf(903));
        var again = new QuestBook(new QuestCatalog(Data()), other, _ => null, monsterExists: _ => true, seed: 5);
        Assert.True(again.Log.IsCleared(911));
        Assert.Equal(new[] { 912, 913, 915 }, again.RowsFor(7).Select(r => r.Id).ToArray());   // 链接着往下走
    }

    // ------------------------------------------------------------------ 窗口

    [Fact]
    public void AnAwakeningRewardGetsItsOwnLineInTheRewardArea()
    {
        (QuestBook book, _) = Make();
        var w = new QuestWindow();
        w.Use(book);
        Assert.True(w.ShowNpc(GameStage.Town, 7, "Alice", -1));
        QuestRow row = w.SelectedRow;
        Assert.Equal(911, row.Id);
        Assert.Equal(1, row.Awakening);
        var art = new OverlayArt { Fonts = new BlockFonts() };
        string[] withReward = QuestWindowArt.Build(w, art).Select(s => s.AnimationPath).ToArray();
        Assert.Equal(1, withReward.Count(t => t == "quest-reward-job"));
        Assert.Contains("quest-reward-head", withReward);
        row.Awakening = 0;
        string[] without = QuestWindowArt.Build(w, art).Select(s => s.AnimationPath).ToArray();
        Assert.DoesNotContain("quest-reward-job", without);
        Assert.Equal("AWAKENING", QuestWindowArt.AwakeningText);
    }

    [Fact]
    public void TheAwakeningLineSaysAwakeningAndTheJobChangeLineStillSaysJobChange()
    {
        (QuestBook book, _) = Make();
        var w = new QuestWindow();
        w.Use(book);
        Assert.True(w.ShowNpc(GameStage.Town, 7, "Alice", -1));
        QuestRow row = w.SelectedRow;
        Assert.Equal(1, row.Awakening);
        var art = new OverlayArt { Fonts = new BlockFonts(), Localize = false };                      // 不翻译:占位英文走同一张文字缓存,同样的字得到同一个画面
        SceneSprite job = QuestWindowArt.Build(w, art).Single(s => s.AnimationPath == "quest-reward-job");
        Assert.Same(art.Text(QuestWindowArt.AwakeningText, QuestWindowArt.LineScale, OverlayArt.Orange), job.Frame);
        Assert.NotSame(art.Text(QuestWindowArt.JobChangeText, QuestWindowArt.LineScale, OverlayArt.Orange), job.Frame);

        row.Awakening = 0;
        row.Growtype = 2;                                                                             // 转职奖励那一行不变
        SceneSprite changed = QuestWindowArt.Build(w, art).Single(s => s.AnimationPath == "quest-reward-job");
        Assert.Same(art.Text(QuestWindowArt.JobChangeText, QuestWindowArt.LineScale, OverlayArt.Orange), changed.Frame);
    }
}
