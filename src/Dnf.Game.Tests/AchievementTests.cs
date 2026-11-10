using Dnf.Data;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 成就框架(分类 [title] 的任务):定义的读入与目录、进度簿的触发规则(三个 16 位计数各自饱和累减、回复码的次序、"差一个/刚好够"的边界、完成后不能重复领取)、读档的范围。
/// 规则取自服务端静态读码(setTrigger/_verifyTrigger/_setTrigger/isComplete,地址在笔记里);任务编号 8001 起、成就编号 9001 起,全是合成的。
/// </summary>
public class AchievementTests
{
    private static AchievementTracker NewTracker() => new(TitleFixtures.Achievements());

    private static ScriptLine[] Lines(TokenStreamBuilder b) => TitleFixtures.Lines(b);

    // ------------------------------------------------------------------ 定义的读入

    [Fact]
    public void CheckCountsAreTheLastTagsNumbersUpToTheFirstNonNumber()
    {
        QuestDef quest = QuestScript.Parse(null, 9001, "n_quest/a.qst");
        var b = new TokenStreamBuilder();
        b.Tag("[check count]").Int(3);
        b.Tag("[check count]").Int(5).Int(2).Str("stop").Int(9);                                        // 第二次覆盖第一次;读到字符串就停
        AchievementDef def = AchievementScript.Parse(quest, Lines(b));
        Assert.Equal(new[] { 5, 2 }, def.CheckCounts);
        Assert.Equal((5, 2, 0), (def.InitialCount(0), def.InitialCount(1), def.InitialCount(2)));
    }

    [Fact]
    public void WithoutCheckCountsTheInitialCountsAreOneZeroZero()
    {
        QuestDef quest = QuestScript.Parse(null, 9001, "n_quest/a.qst");
        AchievementDef def = AchievementScript.Parse(quest, Lines(new TokenStreamBuilder().Tag("[level]").Int(5)));
        Assert.Empty(def.CheckCounts);
        Assert.Equal((1, 0, 0), (def.InitialCount(0), def.InitialCount(1), def.InitialCount(2)));
        Assert.Equal(0, def.InitialCount(3));
        Assert.Equal(0, def.InitialCount(-1));
        Assert.Equal((1, 0, 0), (AchievementScript.Parse(quest, null).InitialCount(0), AchievementScript.Parse(quest, null).InitialCount(1), AchievementScript.Parse(quest, null).InitialCount(2)));
    }

    [Fact]
    public void OnlyTheFirstThreeCheckCountsMatterAndTheyKeepSixteenBits()
    {
        AchievementDef four = TitleFixtures.Achievement(9001, QuestKind.UseItem, 1, 0, new[] { 7, 8, 9, 10 });
        Assert.Equal((7, 8, 9), (four.InitialCount(0), four.InitialCount(1), four.InitialCount(2)));
        Assert.Equal(4, four.CheckCounts.Count);

        AchievementDef wide = TitleFixtures.Achievement(9002, QuestKind.UseItem, 1, 0, new[] { 65536 + 7, -1 });
        Assert.Equal((7, 65535, 0), (wide.InitialCount(0), wide.InitialCount(1), wide.InitialCount(2)));                 // 高位丢掉,负数按 16 位补码

        AchievementDef zero = TitleFixtures.Achievement(9003, QuestKind.UseItem, 1, 0, new[] { 0 });
        Assert.Equal((0, 0, 0), (zero.InitialCount(0), zero.InitialCount(1), zero.InitialCount(2)));                     // 写了 0 就是 0,不再回到 1
    }

    [Fact]
    public void ThePostalRewardReadsTwoNumbersAndTheFirstTwoTexts()
    {
        QuestDef quest = QuestScript.Parse(null, 9001, "n_quest/a.qst");
        var b = new TokenStreamBuilder();
        b.Tag("[send postal]").Ints(960, 3).Keyed(1, "t").Keyed(1, "b").Keyed(1, "extra");
        AchievementPostal postal = AchievementScript.Parse(quest, Lines(b)).Postal;
        Assert.Equal((960, 3, "t", "b", false), (postal.ItemCode, postal.Count, postal.Title.Key, postal.Body.Key, postal.IsEmpty));

        var b2 = new TokenStreamBuilder();
        b2.Tag("[send postal]").Int(960);
        postal = AchievementScript.Parse(quest, Lines(b2)).Postal;
        Assert.Equal((960, 0, true), (postal.ItemCode, postal.Count, postal.Title.IsEmpty));

        Assert.True(AchievementScript.Parse(quest, Lines(new TokenStreamBuilder().Tag("[check count]").Int(1))).Postal.IsEmpty);
        Assert.Equal("plain", AchievementScript.Parse(quest, Lines(new TokenStreamBuilder().Tag("[send postal]").Ints(1, 1).Str("plain"))).Postal.Title.Key);
    }

    [Fact]
    public void TheTitleRewardIsTheFirstItemRewardOfAnItemRewardQuest()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[grade]").Str("[title]").Tag("[type]").Str("[clear quest]").Tag("[int data]").Int(8001);
        b.Tag("[reward int data]").Ints(901, 1, 902, 1);
        QuestDef quest = QuestScript.Parse(Lines(b), 9001, "n_quest/a.qst");
        AchievementDef def = AchievementScript.Parse(quest, Lines(b));
        Assert.Equal((901, QuestKind.ClearQuest, 8001, true), (def.TitleItemCode, def.Condition, def.ClearQuestId, def.IsVerifiable));
        Assert.Equal("[clear quest]", def.ConditionTag);

        var b2 = new TokenStreamBuilder();
        b2.Tag("[grade]").Str("[title]").Tag("[reward type]").Str("[grow type]").Tag("[reward int data]").Ints(2, 1);
        Assert.Equal(0, AchievementScript.Parse(QuestScript.Parse(Lines(b2), 9002, "n_quest/b.qst"), Lines(b2)).TitleItemCode);   // 不是物品奖励
        Assert.Equal(0, TitleFixtures.Achievement(9003, QuestKind.UseItem, 1, 0).TitleItemCode);                          // 没有奖励
    }

    [Fact]
    public void OnlyAClearQuestAchievementWithAnIntegerIsVerifiable()
    {
        Assert.True(TitleFixtures.Achievement(1, QuestKind.ClearQuest, 1, 0, null, new[] { 8001, 5 }).IsVerifiable);
        Assert.Equal(8001, TitleFixtures.Achievement(1, QuestKind.ClearQuest, 1, 0, null, new[] { 8001, 5 }).ClearQuestId);
        Assert.False(TitleFixtures.Achievement(2, QuestKind.ClearQuest, 1, 0).IsVerifiable);                                // 没有整数
        Assert.False(TitleFixtures.Achievement(3, QuestKind.HuntMonster, 1, 0, null, new[] { 8001 }).IsVerifiable);        // 别的种类:只登记
        Assert.False(TitleFixtures.Achievement(4, QuestKind.ClearQuest, 1, 0, null, new[] { 0 }).IsVerifiable);            // 任务编号 0 不算
    }

    [Fact]
    public void TheDefinitionKeepsTheQuestsMinimumLevelAndPath()
    {
        AchievementDef def = TitleFixtures.Achievement(9002, QuestKind.HuntMonster, 10, 902, new[] { 3 });
        Assert.Equal((9002, 10, "n_quest/a9002.qst"), (def.Id, def.MinLevel, def.Path));
        Assert.Throws<ArgumentNullException>(() => new AchievementDef(null, Array.Empty<int>(), default));
        Assert.Throws<ArgumentNullException>(() => AchievementScript.Parse(null, null));
    }

    // ------------------------------------------------------------------ 目录

    [Fact]
    public void TheCatalogKeepsOnlyTitleGradeQuestsSortedByIdAndTheFirstOfADuplicate()
    {
        var catalog = new AchievementCatalog(new[]
        {
            TitleFixtures.Achievement(30, QuestKind.UseItem, 1, 0),
            TitleFixtures.Achievement(10, QuestKind.ClearQuest, 1, 0),
            TitleFixtures.Achievement(10, QuestKind.HuntMonster, 1, 0),
            new AchievementDef(new QuestDef(20, "n_quest/q20.qst") { Grade = QuestGrade.Epic }, Array.Empty<int>(), default),
            null,
        });
        Assert.Equal(new[] { 10, 30 }, catalog.All().Select(d => d.Id).ToArray());
        Assert.Equal(QuestKind.ClearQuest, catalog.Get(10).Condition);
        Assert.Null(catalog.Get(20));
        Assert.False(catalog.Contains(20));
        Assert.True(catalog.Contains(30));
        Assert.Equal(2, catalog.Count);
        Assert.Throws<ArgumentNullException>(() => new AchievementCatalog(null));
        Assert.Equal(0, AchievementCatalog.Empty.Count);
    }

    [Fact]
    public void TheFixtureCatalogIgnoresTheNonTitleQuestAndCountsConditionKinds()
    {
        AchievementCatalog catalog = TitleFixtures.Achievements();
        Assert.Equal(9, catalog.Count);
        Assert.Null(catalog.Get(8999));
        Dictionary<QuestKind, int> counts = catalog.CountsByCondition().ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal((5, 1, 1, 1, 1), (counts[QuestKind.ClearQuest], counts[QuestKind.HuntMonster], counts[QuestKind.UseItem], counts[QuestKind.GetScore], counts[QuestKind.HuntEnemy]));
    }

    [Fact]
    public void LoadingReadsEveryTitleGradeQuestFromTheDataWithItsCheckCountAndPostal()
    {
        Fixture fx = new Fixture()
            .List(QuestCatalog.ListPath, (9001, "a9001.qst"), (9002, "a9002.qst"), (8001, "q8001.qst"))
            .Tokens("n_quest/a9001.qst", w => w.Tag("[grade]").Str("[title]").Tag("[type]").Str("[clear quest]").Tag("[int data]").Int(8001)
                .Tag("[level]").Int(5).Int(99).Tag("[reward int data]").Int(901).Int(1)
                .Tag("[check count]").Int(4).Tag("[send postal]").Int(960).Int(2).Str("mt").Str("mb"))
            .Tokens("n_quest/a9002.qst", w => w.Tag("[grade]").Str("[title]").Tag("[type]").Str("[use item]").Tag("[check count]").Int(2).Int(1))
            .Tokens("n_quest/q8001.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[clear quest]"));
        var data = new GameData(fx.Build());
        AchievementCatalog catalog = AchievementCatalog.Load(data, new QuestCatalog(data));
        Assert.Equal(new[] { 9001, 9002 }, catalog.All().Select(d => d.Id).ToArray());
        AchievementDef first = catalog.Get(9001);
        Assert.Equal((5, 901, 8001, 4, 0), (first.MinLevel, first.TitleItemCode, first.ClearQuestId, first.InitialCount(0), first.InitialCount(1)));
        Assert.Equal((960, 2, "mt", "mb"), (first.Postal.ItemCode, first.Postal.Count, first.Postal.Title.Key, first.Postal.Body.Key));
        Assert.Equal((2, 1, 0), (catalog.Get(9002).InitialCount(0), catalog.Get(9002).InitialCount(1), catalog.Get(9002).InitialCount(2)));
        Assert.Null(catalog.Get(8001));

        Assert.Throws<ArgumentNullException>(() => AchievementCatalog.Load(null, new QuestCatalog(data)));
        Assert.Throws<ArgumentNullException>(() => AchievementCatalog.Load(data, null));
    }

    // ------------------------------------------------------------------ 触发:计数与边界

    [Fact]
    public void ACounterOfThreeIsOneShortAfterTwoTriggersAndCompleteAfterTheThird()
    {
        AchievementTracker tracker = NewTracker();                                                       // 9002:计数 3,最低等级 10
        for (int i = 1; i <= 2; i++)
        {
            Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9002, 1, 0, 0, 10, out bool done));
            Assert.False(done);
            Assert.False(tracker.IsCleared(9002));
            Assert.Equal(3 - i, tracker.ProgressOf(9002).Remaining0);
        }
        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9002, 1, 0, 0, 10, out bool completed));
        Assert.True(completed);
        Assert.True(tracker.IsCleared(9002));
        Assert.True(tracker.ProgressOf(9002).IsComplete);
    }

    [Fact]
    public void OneTriggerOfTheFullAmountCompletesAndOneShortDoesNot()
    {
        AchievementTracker oneShort = NewTracker();
        oneShort.Trigger(9002, 2, 0, 0, 10, out bool done);
        Assert.False(done);
        Assert.Equal(1, oneShort.ProgressOf(9002).Remaining0);

        AchievementTracker exact = NewTracker();
        exact.Trigger(9002, 3, 0, 0, 10, out done);
        Assert.True(done);
        Assert.Equal(0, exact.ProgressOf(9002).Remaining0);
    }

    [Fact]
    public void TheDecrementSaturatesAtZeroInsteadOfGoingNegative()
    {
        AchievementTracker tracker = NewTracker();
        tracker.Trigger(9002, 5, 0, 0, 10, out bool done);                                               // 比还差的多
        Assert.True(done);
        Assert.Equal((0, 0, 0), (tracker.ProgressOf(9002).Remaining0, tracker.ProgressOf(9002).Remaining1, tracker.ProgressOf(9002).Remaining2));
        tracker.Trigger(9003, 65535, 65535, 65535, 1, out done);
        Assert.True(done);
    }

    [Fact]
    public void ThreeCountersEachNeedTheirOwnTriggerValue()
    {
        AchievementTracker tracker = NewTracker();                                                       // 9003:计数 2、1、3
        tracker.Trigger(9003, 2, 0, 0, 1, out bool done);
        Assert.False(done);
        Assert.Equal((0, 1, 3), (tracker.ProgressOf(9003).Remaining0, tracker.ProgressOf(9003).Remaining1, tracker.ProgressOf(9003).Remaining2));
        tracker.Trigger(9003, 0, 1, 0, 1, out done);
        Assert.False(done);
        tracker.Trigger(9003, 0, 0, 2, 1, out done);
        Assert.False(done);                                                                              // 第三个还差 1
        Assert.Equal((0, 0, 1), (tracker.ProgressOf(9003).Remaining0, tracker.ProgressOf(9003).Remaining1, tracker.ProgressOf(9003).Remaining2));
        tracker.Trigger(9003, 0, 0, 1, 1, out done);
        Assert.True(done);

        AchievementTracker once = NewTracker();
        once.Trigger(9003, 2, 1, 3, 1, out done);
        Assert.True(done);
        AchievementTracker wrongAxis = NewTracker();
        wrongAxis.Trigger(9003, 0, 0, 9, 1, out done);                                                   // 只有第三个计数被减,前两个还在
        Assert.False(done);
    }

    [Fact]
    public void TheFirstTriggerCreatesTheRecordFromTheInitialCounts()
    {
        AchievementTracker tracker = NewTracker();
        Assert.False(tracker.TryGet(9002, out _));
        Assert.Equal(3, tracker.ProgressOf(9002).Remaining0);                                            // 没有记录时界面看到的是初始计数
        Assert.Equal((0, 0), (tracker.EntryCount, tracker.Version));
        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9002, 0, 0, 0, 10, out bool done));      // 触发值 0:建记录、不减
        Assert.False(done);
        Assert.True(tracker.TryGet(9002, out AchievementProgress progress));
        Assert.Equal((3, 0, 0, 1, 1), (progress.Remaining0, progress.Remaining1, progress.Remaining2, tracker.EntryCount, tracker.Version));

        AchievementTracker defaults = NewTracker();                                                      // 9001 没写 [check count]:默认 1,0,0
        defaults.Trigger(9001, 0, 0, 0, 1, out done);
        Assert.False(done);
        Assert.Equal(1, defaults.ProgressOf(9001).Remaining0);
        defaults.Trigger(9001, 1, 0, 0, 1, out done);
        Assert.True(done);
    }

    [Fact]
    public void AZeroCheckCountCompletesOnTheFirstAcceptedTrigger()
    {
        var tracker = new AchievementTracker(new AchievementCatalog(new[] { TitleFixtures.Achievement(1, QuestKind.UseItem, 1, 0, new[] { 0 }) }));
        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(1, 0, 0, 0, 1, out bool done));
        Assert.True(done);
    }

    // ------------------------------------------------------------------ 触发:回复码

    [Fact]
    public void AnUnknownAchievementAndANonTitleQuestAreInvalid()
    {
        AchievementTracker tracker = NewTracker();
        Assert.Equal(AchievementTriggerCode.Invalid, tracker.Trigger(777, 1, 0, 0, 99, out bool done));
        Assert.False(done);
        Assert.Equal(AchievementTriggerCode.Invalid, tracker.Trigger(8999, 1, 0, 0, 99, out done));
        Assert.Equal((0, 0), (tracker.EntryCount, tracker.Version));
    }

    [Fact]
    public void ALevelBelowTheMinimumIsInvalidAndExactlyTheMinimumIsAccepted()
    {
        AchievementTracker tracker = NewTracker();                                                       // 9002 最低等级 10
        Assert.Equal(AchievementTriggerCode.Invalid, tracker.Trigger(9002, 1, 0, 0, 9, out _));
        Assert.Equal(0, tracker.EntryCount);
        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9002, 1, 0, 0, 10, out _));
        Assert.Equal(1, tracker.EntryCount);
    }

    [Fact]
    public void ACompletedAchievementCannotBeClaimedAgain()
    {
        AchievementTracker tracker = NewTracker();
        tracker.Trigger(9001, 1, 0, 0, 1, out bool done);
        Assert.True(done);
        int version = tracker.Version;
        Assert.Equal(AchievementTriggerCode.Rejected, tracker.Trigger(9001, 1, 0, 0, 1, out done));
        Assert.False(done);
        Assert.Equal(AchievementTriggerCode.Rejected, tracker.Trigger(9001, 0, 0, 0, 1, out done));
        Assert.Equal(version, tracker.Version);                                                          // 被拒的触发什么都不改
        Assert.Equal(AchievementTriggerCode.Invalid, tracker.Trigger(9001, 1, 0, 0, 0, out _));          // 等级这一关先于"已完成"
    }

    [Fact]
    public void TheReplyCodesFollowTheServerOrderUnknownThenLevelThenClearedThenGate()
    {
        AchievementTracker tracker = NewTracker();
        var gateCalls = new List<(int, int)>();
        tracker.TriggerGate = (id, level) =>
        {
            gateCalls.Add((id, level));
            return false;
        };
        Assert.Equal(AchievementTriggerCode.Invalid, tracker.Trigger(777, 1, 0, 0, 99, out _));          // 未知:不问额外条件
        Assert.Equal(AchievementTriggerCode.Invalid, tracker.Trigger(9002, 1, 0, 0, 9, out _));          // 等级不够:不问额外条件
        Assert.Empty(gateCalls);
        Assert.Equal(AchievementTriggerCode.Rejected, tracker.Trigger(9001, 1, 0, 0, 3, out _));         // 过了前两关才问
        Assert.Equal(new[] { (9001, 3) }, gateCalls);
        Assert.Equal((0, 0), (tracker.EntryCount, tracker.Version));                                     // 额外条件不过:不建记录

        tracker.TriggerGate = null;
        tracker.Trigger(9001, 1, 0, 0, 3, out _);
        gateCalls.Clear();
        tracker.TriggerGate = (id, level) =>
        {
            gateCalls.Add((id, level));
            return true;
        };
        Assert.Equal(AchievementTriggerCode.Rejected, tracker.Trigger(9001, 1, 0, 0, 3, out _));         // 已完成先于额外条件
        Assert.Empty(gateCalls);
    }

    [Fact]
    public void AGateThatPassesLetsTheTriggerCount()
    {
        AchievementTracker tracker = NewTracker();
        tracker.TriggerGate = (id, level) => level >= 70;
        Assert.Equal(AchievementTriggerCode.Rejected, tracker.Trigger(9001, 1, 0, 0, 69, out _));
        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9001, 1, 0, 0, 70, out bool done));
        Assert.True(done);
    }

    [Fact]
    public void TriggerValuesMustFitInSixteenBits()
    {
        AchievementTracker tracker = NewTracker();
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Trigger(9001, -1, 0, 0, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Trigger(9001, 0, 65536, 0, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Trigger(9001, 0, 0, -1, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Trigger(9001, 0, 0, 65536, 1, out _));
        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9001, 65535, 0, 0, 1, out bool done));
        Assert.True(done);
        Assert.Equal(65535, AchievementTracker.MaxCount);
    }

    // ------------------------------------------------------------------ 查看

    [Fact]
    public void EntriesAreSortedByIdAndClearedCountOnlyCountsCompleteOnes()
    {
        AchievementTracker tracker = NewTracker();
        tracker.Trigger(9004, 1, 0, 0, 1, out _);
        tracker.Trigger(9002, 1, 0, 0, 10, out _);
        tracker.Trigger(9001, 1, 0, 0, 1, out _);
        Assert.Equal(new[] { 9001, 9002, 9004 }, tracker.Entries().Select(e => e.Id).ToArray());
        Assert.Equal((3, 2), (tracker.EntryCount, tracker.ClearedCount));
        Assert.True(tracker.IsCleared(9004));
        Assert.False(tracker.IsCleared(9002));
        Assert.False(tracker.IsCleared(9005));                                                           // 没有记录就是没完成
        Assert.False(tracker.IsCleared(777));
        Assert.Equal(new AchievementProgress(777, 0, 0, 0), tracker.ProgressOf(777));                    // 不存在:全 0 的占位
    }

    // ------------------------------------------------------------------ 直接补完成、读档

    [Fact]
    public void MarkCompleteZeroesTheRecordWhetherOrNotItExisted()
    {
        AchievementTracker tracker = NewTracker();
        Assert.False(tracker.MarkComplete(777));
        Assert.True(tracker.MarkComplete(9001));                                                         // 没有记录:建一条全 0
        Assert.True(tracker.IsCleared(9001));
        int version = tracker.Version;
        Assert.True(tracker.MarkComplete(9001));                                                         // 已完成:不算变化
        Assert.Equal(version, tracker.Version);

        tracker.Trigger(9003, 2, 0, 0, 1, out _);                                                        // 有进度:补成 0
        Assert.True(tracker.MarkComplete(9003));
        Assert.True(tracker.IsCleared(9003));
        Assert.Equal(version + 2, tracker.Version);
        Assert.False(tracker.MarkComplete(8999));
    }

    [Fact]
    public void RestoreChecksTheAchievementTheRangeAndTheEntryLimit()
    {
        AchievementTracker tracker = NewTracker();
        Assert.True(tracker.Restore(9002, 2, 0, 0));
        Assert.Equal(2, tracker.ProgressOf(9002).Remaining0);
        Assert.False(tracker.Restore(777, 1, 0, 0));                                                     // 数据里没有这个成就了
        Assert.False(tracker.Restore(8999, 1, 0, 0));
        Assert.False(tracker.Restore(9001, -1, 0, 0));
        Assert.False(tracker.Restore(9001, 0, 65536, 0));
        Assert.False(tracker.Restore(9001, 0, 0, 70000));
        Assert.True(tracker.Restore(9001, 65535, 65535, 65535));
        Assert.True(tracker.Restore(9002, 0, 0, 0));                                                     // 同一编号后一条覆盖前一条
        Assert.True(tracker.IsCleared(9002));
        Assert.Equal(2, tracker.EntryCount);
    }

    [Fact]
    public void ARoleCannotHoldMoreThanFiveHundredTwelveRecords()
    {
        var defs = new List<AchievementDef>();
        for (int id = 1; id <= AchievementTracker.MaxEntries + 1; id++)
        {
            defs.Add(TitleFixtures.Achievement(id, QuestKind.UseItem, 1, 0));
        }
        var tracker = new AchievementTracker(new AchievementCatalog(defs));
        for (int id = 1; id <= AchievementTracker.MaxEntries; id++)
        {
            Assert.True(tracker.Restore(id, 1, 0, 0), "id " + id);
        }
        Assert.Equal(512, tracker.EntryCount);
        Assert.False(tracker.Restore(AchievementTracker.MaxEntries + 1, 1, 0, 0));                       // 第 513 条放不下
        Assert.True(tracker.Restore(AchievementTracker.MaxEntries, 0, 0, 0));                            // 已有的编号可以覆盖
        Assert.Equal(512, tracker.EntryCount);
    }

    [Fact]
    public void ClearDropsAllRecordsAndOnlyCountsAsAChangeWhenThereWereSome()
    {
        AchievementTracker tracker = NewTracker();
        tracker.Clear();
        Assert.Equal(0, tracker.Version);
        tracker.Trigger(9001, 1, 0, 0, 1, out _);
        int version = tracker.Version;
        tracker.Clear();
        Assert.Equal((0, version + 1), (tracker.EntryCount, tracker.Version));
        Assert.False(tracker.IsCleared(9001));
    }

    [Fact]
    public void ConstructionRejectsANullCatalog()
    {
        Assert.Throws<ArgumentNullException>(() => new AchievementTracker(null));
    }
}
