using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 转职任务奖励的接线(M59,证据 E-153:服务端 <c>quest_reward</c> 0x0866ea92 的奖励类型 1 → <c>set_grow_type</c>):
/// 交任务(或条件满足自动完成)时主角换成新的成长类型,技能清空重给、点数回到总额、属性版本加一;换不成的(越界、已经是这个成长类型)只完成任务。
/// 任务、技能与数值全是合成的,与原版数据无关。
///
/// 合成的任务:21 通关副本 7(自动完成,NPC 7 发布,奖励换成 1,与 22、23 互斥);22 通关副本 7(NPC 6 交,NPC 7 发布,奖励换成 2,与 21、23 互斥);
/// 23 奖励换成 9(越界,不支持);24 过房间 30(普通任务,没有转职奖励)。技能与点数见 <see cref="SkillProgressTests.Hero"/>:
/// 开局白送 1 号技能,成长类型 1 再白送 4 号,第 L 级给 L 点。
/// </summary>
public class QuestJobChangeBookTests
{
    private static GameData Data()
    {
        var fx = new Fixture();
        fx.List("n_quest/quest.lst", (21, "a.qst"), (22, "b.qst"), (23, "c.qst"), (24, "d.qst"));
        fx.Tokens("n_quest/a.qst", w => w.Tag("[grade]").Str("[common unique]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6).Tag("[job change quest]").Int(1)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(15).Int(99).Tag("[difficulty]").Str("S")
            .Tag("[collision quest]").Ints(22, 23).Tag("[/collision quest]")
            .Tag("[int data]").Ints(7, -1).Tag("[/int data]")
            .Tag("[reward type]").Str("[grow type]")
            .Tag("[reward int data]").Int(1).Tag("[/reward int data]"));
        fx.Tokens("n_quest/b.qst", w => w.Tag("[grade]").Str("[common unique]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6).Tag("[job change quest]").Int(1)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(6)
            .Tag("[level]").Int(15).Int(99).Tag("[difficulty]").Str("S")
            .Tag("[collision quest]").Ints(21, 23).Tag("[/collision quest]")
            .Tag("[int data]").Ints(7, -1).Tag("[/int data]")
            .Tag("[reward type]").Str("[grow type]")
            .Tag("[reward int data]").Int(2).Tag("[/reward int data]"));
        fx.Tokens("n_quest/c.qst", w => w.Tag("[grade]").Str("[common unique]").Tag("[type]").Str("[condition under clear]")
            .Tag("[sub type]").Int(6).Tag("[job change quest]").Int(1)
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(15).Int(99)
            .Tag("[int data]").Ints(7, -1).Tag("[/int data]")
            .Tag("[reward type]").Str("[grow type]")
            .Tag("[reward int data]").Int(9).Tag("[/reward int data]"));
        fx.Tokens("n_quest/d.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[clear map]")
            .Tag("[npc index]").Int(7).Tag("[complete npc index]").Int(-1)
            .Tag("[level]").Int(1).Int(99)
            .Tag("[int data]").Int(30).Tag("[/int data]")
            .Tag("[reward type]").Str("[item]")
            .Tag("[reward int data]").Ints(0, 1).Tag("[/reward int data]"));
        fx.Tokens("n_quest/questparameter.etc", w =>
        {
            w.Tag("[difficulty]").Str("S").Int(110).Tag("[/difficulty]");
            w.Tag("[green level penalty]").Int(80);
            w.Tag("[grey level penalty]").Int(30);
            w.Tag("[exp reward table]");
            for (int i = 0; i < 100; i++)
            {
                w.Int(i * 10);                           // 15 级的任务给 150 × 权重 110% = 165 点,远不够升级
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

    private static (QuestBook Book, HeroProgress Hero) Make(int level = 15, int growtype = 0)
    {
        HeroProgress hero = SkillProgressTests.Hero(level);
        if (growtype != 0)
        {
            Assert.True(hero.ChangeGrowtype(growtype));
        }
        var book = new QuestBook(new QuestCatalog(Data()), hero, _ => null, monsterExists: _ => true, seed: 5);
        return (book, hero);
    }

    // ------------------------------------------------------------------ 列出与行

    [Fact]
    public void TheSupportedJobChangeQuestsAreListedAndTheOutOfRangeOneIsNot()
    {
        (QuestBook book, _) = Make();
        List<QuestRow> rows = book.RowsFor(7);
        Assert.Equal(new[] { 21, 22, 24 }, rows.Select(r => r.Id).ToArray());
        Assert.Equal(QuestMark.Available, book.MarkOf(7));
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(23));
    }

    [Fact]
    public void TheRowShowsTheNewGrowtypeAndOtherQuestsShowNone()
    {
        (QuestBook book, _) = Make();
        Assert.Equal(1, book.RowOf(21).Growtype);
        Assert.Equal(2, book.RowOf(22).Growtype);
        Assert.Equal(-1, book.RowOf(24).Growtype);
        Assert.Empty(book.RowOf(21).Rewards);            // 单个整数不是物品
        Assert.Equal(0, book.RowOf(21).Gold);
        Assert.True(book.RowOf(21).Exp > 0);
    }

    [Fact]
    public void ARowBuiltByHandStartsWithoutAGrowtypeReward()
    {
        Assert.Equal(-1, new QuestRow().Growtype);
    }

    [Fact]
    public void BelowTheRequiredLevelTheQuestIsNotListed()
    {
        (QuestBook book, _) = Make(level: 14);
        Assert.Equal(new[] { 24 }, book.RowsFor(7).Select(r => r.Id).ToArray());
    }

    // ------------------------------------------------------------------ 自动完成

    [Fact]
    public void ClearingTheDungeonCompletesTheQuestAndChangesTheGrowtype()
    {
        (QuestBook book, HeroProgress hero) = Make();
        int version = hero.Version;
        Assert.Equal(QuestActionResult.Ok, book.Accept(21));
        book.TakeNotices();
        Assert.Equal(0, hero.Growtype);
        book.DungeonCleared(7, 0, 90000);

        Assert.Equal(1, hero.Growtype);
        Assert.True(hero.Version > version);
        Assert.Equal(1, book.JobChanges);
        Assert.Equal(1, book.Completed);
        Assert.True(book.Log.IsCleared(21));
        QuestNotice done = Assert.Single(book.TakeNotices(), n => n.Kind == QuestNoticeKind.Completed);
        Assert.Contains("JOB CHANGE", done.Extra);
        Assert.Contains("EXP", done.Extra);
    }

    [Fact]
    public void TheNewGrowtypeBringsItsSkillsAndGivesBackSpentPoints()
    {
        (QuestBook book, HeroProgress hero) = Make();
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(1, 1, out _));
        Assert.Equal(2, hero.Skills.LevelOf(1));
        Assert.Equal(0, hero.Skills.LevelOf(4));
        int total = hero.SkillPoints + 3;                // 学过一级花了 3 点
        book.Accept(21);
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(1, hero.Growtype);
        Assert.Equal(total, hero.SkillPoints);           // 清空重来:总额回来
        Assert.Equal(1, hero.Skills.LevelOf(1));          // 回到白送的级数
        Assert.Equal(1, hero.Skills.LevelOf(4));          // 成长类型 1 白送的
        Assert.Equal(2, hero.Skills.LearnedCount);
    }

    [Fact]
    public void TheExpOfTheQuestIsStillGiven()
    {
        (QuestBook book, HeroProgress hero) = Make();
        int exp = hero.Exp;
        int expected = book.RowOf(21).Exp;
        Assert.True(expected > 0);
        book.Accept(21);
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(exp + expected, hero.Exp);
    }

    [Fact]
    public void TheRivalQuestsDisappearOnceOneIsDone()
    {
        (QuestBook book, _) = Make();
        book.Accept(21);
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(new[] { 24 }, book.RowsFor(7).Select(r => r.Id).ToArray());
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(22));
        Assert.Equal(QuestActionResult.NotAvailable, book.Accept(21));      // 完成过的不再接
    }

    // ------------------------------------------------------------------ 手动交

    [Fact]
    public void AQuestHandedInAtAnNpcChangesTheGrowtypeOnlyWhenTurnedIn()
    {
        (QuestBook book, HeroProgress hero) = Make();
        Assert.Equal(QuestActionResult.Ok, book.Accept(22));
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(QuestRowState.Ready, book.RowOf(22).State);
        Assert.Equal(0, hero.Growtype);                  // 条件满足了,但要回 NPC 6 那里交
        Assert.Equal(0, book.JobChanges);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(22));
        Assert.Equal(2, hero.Growtype);
        Assert.Equal(1, book.JobChanges);
        Assert.Equal(0, hero.Skills.LevelOf(4));          // 成长类型 2 没有白送 4 号
        Assert.Equal(1, hero.Skills.LearnedCount);
    }

    [Fact]
    public void TurningInTwiceChangesNothingTheSecondTime()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(22);
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(QuestActionResult.Ok, book.TurnIn(22));
        int version = hero.Version;
        Assert.Equal(QuestActionResult.NotFound, book.TurnIn(22));
        Assert.Equal(version, hero.Version);
        Assert.Equal(1, book.JobChanges);
    }

    // ------------------------------------------------------------------ 换不成

    [Fact]
    public void AHeroAlreadyOfThatGrowtypeCompletesTheQuestWithoutAChange()
    {
        (QuestBook book, HeroProgress hero) = Make(growtype: 1);
        Assert.Equal(1, hero.Skills.LevelOf(4));
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(1, 1, out _));
        book.Accept(21);
        book.TakeNotices();
        book.DungeonCleared(7, 0, 1000);
        Assert.Equal(1, hero.Growtype);
        Assert.Equal(2, hero.Skills.LevelOf(1));         // 没有换:学过的技能不清空
        Assert.Equal(0, book.JobChanges);
        Assert.Equal(1, book.Completed);
        QuestNotice done = Assert.Single(book.TakeNotices(), n => n.Kind == QuestNoticeKind.Completed);
        Assert.DoesNotContain("JOB CHANGE", done.Extra);
    }

    [Fact]
    public void OrdinaryQuestsNeverTouchTheGrowtype()
    {
        (QuestBook book, HeroProgress hero) = Make();
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(1, 1, out _));
        Assert.Equal(QuestActionResult.Ok, book.Accept(24));
        book.MapCleared(30);                             // 没有交任务 NPC:自动完成
        Assert.True(book.Log.IsCleared(24));
        Assert.Equal(1, book.Completed);
        Assert.Equal(0, hero.Growtype);
        Assert.Equal(2, hero.Skills.LevelOf(1));         // 学过的技能还在
        Assert.Equal(0, book.JobChanges);
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void TheNewGrowtypeSurvivesASaveRoundTrip()
    {
        (QuestBook book, HeroProgress hero) = Make();
        book.Accept(21);
        book.DungeonCleared(7, 0, 1000);
        HeroSave save = hero.Snapshot();
        Assert.Equal(1, save.Growtype);
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave parsed, out string error), error);
        HeroProgress other = SkillProgressTests.Hero(1, grant: false);
        other.Restore(parsed, code => null);
        Assert.Equal(1, other.Growtype);
        Assert.Equal(1, other.Skills.LevelOf(4));
    }
}
