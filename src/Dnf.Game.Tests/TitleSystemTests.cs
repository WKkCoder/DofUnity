namespace Dnf.Game.Tests;

/// <summary>
/// 称号与成就的整体:触发完成后放称号、排任务邮件与整页邮件(含服务端按"默认格"判整页的写法)、强制完成、登录核对、补发清单,
/// 以及图鉴收集的属性加成(推断,默认不接进摘要)。规则取自服务端静态读码(_checkComplete/_forceClear/_verifyAchievement/adjust,地址在笔记里);数据全是合成的。
/// </summary>
public class TitleSystemTests
{
    private static TitleSystem NewSystem() => TitleFixtures.NewSystem();

    /// <summary>普通分类第 0 页(收第 0、1、2 格,成就 9001、9002、9003)全部完成。</summary>
    private static void CompleteFirstPage(TitleSystem system)
    {
        system.Trigger(9001, 1, 0, 0, 10);
        system.Trigger(9002, 3, 0, 0, 10);
        system.Trigger(9003, 2, 1, 3, 10);
    }

    // ------------------------------------------------------------------ 触发后的结果

    [Fact]
    public void CompletingAnAchievementPutsItsTitleIntoTheSlotThatListsItAndQueuesTheTaskMail()
    {
        TitleSystem system = NewSystem();
        TitleTriggerOutcome outcome = system.Trigger(9001, 1, 0, 0, 1);
        Assert.Equal((AchievementTriggerCode.Ok, true, 901, true, TitleCategory.General, 0), (outcome.Code, outcome.Completed, outcome.TitleCode, outcome.TitlePlaced, outcome.Category, outcome.Slot));
        Assert.Equal(901, system.Book.CodeAt(TitleCategory.General, 0));
        TitlePostal postal = Assert.Single(outcome.Postals);                                             // 整页还没收齐:只有任务自带的邮件
        Assert.Equal((TitlePostalSource.Achievement, 9001, 960, 1, "ach_mail_t", "ach_mail_b"), (postal.Source, postal.SourceId, postal.ItemCode, postal.Count, postal.Title.Key, postal.Body.Key));
        Assert.Same(postal, Assert.Single(system.Outbox));
    }

    [Fact]
    public void ProgressThatDoesNotCompleteGivesNothing()
    {
        TitleSystem system = NewSystem();
        TitleTriggerOutcome outcome = system.Trigger(9002, 1, 0, 0, 10);                                 // 要 3 次
        Assert.Equal((AchievementTriggerCode.Ok, false, 0, false), (outcome.Code, outcome.Completed, outcome.TitleCode, outcome.TitlePlaced));
        Assert.Empty(outcome.Postals);
        Assert.Equal((0, 0), (system.Book.TotalCount, system.Outbox.Count));
        Assert.Equal(1, system.Achievements.EntryCount);
    }

    [Fact]
    public void ARefusedTriggerReturnsItsCodeAndChangesNothing()
    {
        TitleSystem system = NewSystem();
        Assert.Equal(AchievementTriggerCode.Invalid, system.Trigger(9002, 1, 0, 0, 9).Code);
        Assert.Equal(AchievementTriggerCode.Invalid, system.Trigger(777, 1, 0, 0, 99).Code);
        system.Trigger(9001, 1, 0, 0, 1);
        int version = system.Version;
        TitleTriggerOutcome again = system.Trigger(9001, 1, 0, 0, 1);
        Assert.Equal((AchievementTriggerCode.Rejected, false, false), (again.Code, again.Completed, again.TitlePlaced));
        Assert.Empty(again.Postals);
        Assert.Equal(version, system.Version);
        Assert.Single(system.Outbox);
    }

    [Fact]
    public void AClaimedAchievementCannotGetItsTitleOrMailAgainEvenIfTheTitleWasTakenOut()
    {
        TitleSystem system = NewSystem();
        system.Trigger(9001, 1, 0, 0, 1);
        Assert.Equal(901, system.Book.Remove(TitleCategory.General, 0));
        system.DrainOutbox();
        TitleTriggerOutcome again = system.Trigger(9001, 1, 0, 0, 1);
        Assert.Equal(AchievementTriggerCode.Rejected, again.Code);
        Assert.Equal(0, system.Book.TotalCount);                                                         // 没有补发称号
        Assert.Empty(system.Outbox);                                                                     // 也没有补发邮件
    }

    [Fact]
    public void TheLastAchievementOfAPageAlsoQueuesThePagePostalWhateverTheOrder()
    {
        foreach (int[] order in new[] { new[] { 9001, 9002, 9003 }, new[] { 9003, 9001, 9002 }, new[] { 9002, 9003, 9001 } })
        {
            TitleSystem system = NewSystem();
            var args = new Dictionary<int, (int, int, int)> { [9001] = (1, 0, 0), [9002] = (3, 0, 0), [9003] = (2, 1, 3) };
            TitleTriggerOutcome last = null;
            foreach (int id in order)
            {
                (int a, int b, int c) = args[id];
                last = system.Trigger(id, a, b, c, 10);
                if (id != order[2])
                {
                    Assert.DoesNotContain(last.Postals, p => p.Source == TitlePostalSource.Section);     // 前两个完成时整页还没收齐
                }
            }
            TitlePostal page = Assert.Single(last.Postals, p => p.Source == TitlePostalSource.Section);
            Assert.Equal((0, 950, 2, "mail_title", "mail_body"), (page.SourceId, page.ItemCode, page.Count, page.Title.Key, page.Body.Key));
            Assert.Equal(2, system.Outbox.Count);                                                        // 任务邮件 + 整页邮件
            Assert.Equal(3, system.Book.TotalCount);
        }
    }

    [Fact]
    public void ThePageMailComesAfterTheTaskMailInTheSameOutcome()
    {
        TitleSystem system = NewSystem();
        system.Trigger(9002, 3, 0, 0, 10);
        system.Trigger(9003, 2, 1, 3, 10);
        TitleTriggerOutcome last = system.Trigger(9001, 1, 0, 0, 10);                                    // 最后完成的 9001 自己带任务邮件
        Assert.Equal(new[] { TitlePostalSource.Achievement, TitlePostalSource.Section }, last.Postals.Select(p => p.Source).ToArray());
    }

    [Fact]
    public void ATitleListedInAnotherCategoryLandsThereAndALockedPageDoesNotBlockIt()
    {
        TitleSystem system = NewSystem();
        TitleTriggerOutcome outcome = system.Trigger(9005, 1, 0, 0, 1);                                  // 称号 906 在特殊分类第 0 格(那一页锁着)
        Assert.Equal((true, true, TitleCategory.Specific, 0), (outcome.Completed, outcome.TitlePlaced, outcome.Category, outcome.Slot));
        Assert.Equal(906, system.Book.CodeAt(TitleCategory.Specific, 0));
        Assert.Empty(outcome.Postals);                                                                   // 这一页没有邮件奖励
    }

    [Fact]
    public void AnAchievementWithoutATitleRewardJudgesTheDefaultSlotsPageLikeTheServer()
    {
        TitleSystem alone = NewSystem();
        TitleTriggerOutcome outcome = alone.Trigger(9008, 1, 0, 0, 1);                                   // 没有称号奖励
        Assert.Equal((true, 0, false, TitleCategory.General, 0), (outcome.Completed, outcome.TitleCode, outcome.TitlePlaced, outcome.Category, outcome.Slot));
        Assert.Empty(outcome.Postals);                                                                   // 默认格(普通 0)所在页没收齐

        TitleSystem system = NewSystem();
        CompleteFirstPage(system);
        Assert.Equal(2, system.Outbox.Count);                                                            // 9001 的任务邮件 + 整页邮件
        TitleTriggerOutcome extra = system.Trigger(9008, 1, 0, 0, 1);
        TitlePostal page = Assert.Single(extra.Postals);                                                 // 默认格判到第 0 页:整页邮件又发了一次
        Assert.Equal((TitlePostalSource.Section, 0, 950), (page.Source, page.SourceId, page.ItemCode));
    }

    [Fact]
    public void ATitleNobodyListsIsNotPlacedButTheAchievementStillCompletes()
    {
        TitleSystem system = NewSystem();
        TitleTriggerOutcome outcome = system.Trigger(9009, 1, 0, 0, 1);                                  // 称号 999 没有任何格列出
        Assert.Equal((true, 999, false, TitleCategory.General, 0), (outcome.Completed, outcome.TitleCode, outcome.TitlePlaced, outcome.Category, outcome.Slot));
        Assert.True(system.Achievements.IsCleared(9009));
        Assert.Equal(0, system.Book.TotalCount);
    }

    [Fact]
    public void ATitleMissingFromTheItemLibraryIsNotPlaced()
    {
        Dictionary<int, ItemInfo> items = TitleFixtures.Items();
        items.Remove(TitleFixtures.General0);
        TitleSystem system = TitleFixtures.NewSystem(items);
        TitleTriggerOutcome outcome = system.Trigger(9001, 1, 0, 0, 1);
        Assert.Equal((true, 901, false), (outcome.Completed, outcome.TitleCode, outcome.TitlePlaced));
        Assert.Equal(0, system.Book.TotalCount);
        Assert.Single(outcome.Postals);                                                                  // 任务邮件照发
    }

    [Fact]
    public void AFullPageHandsTheNextPagesFirstSlotToItAndTheMailIsSentAgain()
    {
        Dictionary<int, ItemInfo> items = new();
        var defs = new List<AchievementDef>();
        for (int i = 0; i < 11; i++)
        {
            items[9200 + i] = TitleFixtures.TitleItem(9200 + i);
            defs.Add(TitleFixtures.Achievement(9100 + i, QuestKind.UseItem, 1, 9200 + i));
        }
        var system = new TitleSystem(TitleFixtures.QuirkBook(), new AchievementCatalog(defs), TitleFixtures.Lookup(items));
        for (int i = 0; i < 9; i++)
        {
            Assert.DoesNotContain(system.Trigger(9100 + i, 1, 0, 0, 1).Postals, p => p.Source == TitlePostalSource.Section);
        }
        TitleTriggerOutcome tenth = system.Trigger(9109, 1, 0, 0, 1);                                    // 第 0 页(0 到 9 格)收齐
        Assert.Equal(951, Assert.Single(tenth.Postals).ItemCode);
        TitleTriggerOutcome eleventh = system.Trigger(9110, 1, 0, 0, 1);                                 // 第 10 格本该属于第 1 页,服务端判给第 0 页
        Assert.Equal((TitleCategory.General, 10), (eleventh.Category, eleventh.Slot));
        TitlePostal again = Assert.Single(eleventh.Postals);
        Assert.Equal((TitlePostalSource.Section, 0, 951), (again.Source, again.SourceId, again.ItemCode));
    }

    // ------------------------------------------------------------------ 强制完成

    [Fact]
    public void ForceClearCompletesAndPlacesTheTitleWithoutMailOrNotice()
    {
        TitleSystem system = NewSystem();
        Assert.True(system.ForceClear(9004, 0));
        Assert.True(system.Achievements.IsCleared(9004));
        Assert.Equal(904, system.Book.CodeAt(TitleCategory.General, 4));
        Assert.Empty(system.Outbox);
        int version = system.Version;
        Assert.False(system.ForceClear(9004, 0));                                                        // 已完成、称号也在:什么都不改
        Assert.Equal(version, system.Version);
    }

    [Fact]
    public void ForceClearRestoresAMissingTitleOfAClearedAchievementUnlessItIsWorn()
    {
        TitleSystem system = NewSystem();
        system.ForceClear(9004, 0);
        system.Book.Remove(TitleCategory.General, 4);
        Assert.False(system.ForceClear(9004, 904));                                                      // 正穿在身上:不补发
        Assert.Equal(0, system.Book.CodeAt(TitleCategory.General, 4));
        Assert.True(system.ForceClear(9004, 0));                                                         // 没穿:补发
        Assert.Equal(904, system.Book.CodeAt(TitleCategory.General, 4));
        system.Book.Remove(TitleCategory.General, 4);
        Assert.True(system.ForceClear(9004, 901));                                                       // 穿着的是别的称号:还是补发
        Assert.Equal(904, system.Book.CodeAt(TitleCategory.General, 4));
    }

    [Fact]
    public void ForceClearIsANoOpForAnAchievementWithoutATitleRewardOrAnUnknownOne()
    {
        TitleSystem system = NewSystem();
        Assert.False(system.ForceClear(9008, 0));                                                        // 没有称号奖励
        Assert.False(system.Achievements.IsCleared(9008));
        Assert.False(system.ForceClear(777, 0));
        Assert.False(system.ForceClear(8999, 0));
        Assert.Equal(0, system.Version);
    }

    // ------------------------------------------------------------------ 登录核对

    [Fact]
    public void VerifyForcesTheClearQuestAchievementsWhoseQuestIsAlreadyCleared()
    {
        TitleSystem system = NewSystem();
        Assert.Equal(2, system.Verify(q => q is 8001 or 8004, 0));
        Assert.True(system.Achievements.IsCleared(9001));
        Assert.True(system.Achievements.IsCleared(9004));
        Assert.False(system.Achievements.IsCleared(9003));
        Assert.Equal((901, 904), (system.Book.CodeAt(TitleCategory.General, 0), system.Book.CodeAt(TitleCategory.General, 4)));
        Assert.Empty(system.Outbox);                                                                     // 登录核对不发邮件
        Assert.Equal(0, system.Verify(q => q is 8001 or 8004, 0));                                       // 再核对一次没有改动
    }

    [Fact]
    public void VerifyLeavesOtherConditionKindsAndUnclearedQuestsAlone()
    {
        TitleSystem system = NewSystem();
        Assert.Equal(5, system.Verify(_ => true, 0));                                                    // 9001、9003、9004、9006、9007
        Assert.False(system.Achievements.IsCleared(9002));                                               // 猎杀:只登记
        Assert.False(system.Achievements.IsCleared(9005));                                               // 使用物品:只登记
        Assert.Equal(0, new TitleSystem(TitleFixtures.Book(), TitleFixtures.Achievements(), TitleFixtures.Lookup()).Verify(_ => false, 0));
    }

    [Fact]
    public void VerifyPutsTheTitlesInTheirSlotsIgnoringLevelAndLock()
    {
        TitleSystem system = NewSystem();
        system.Verify(q => q is 8006 or 8007, 0);                                                        // 特殊分类(锁着的页)与绝望分类
        Assert.Equal((907, 908), (system.Book.CodeAt(TitleCategory.Specific, 1), system.Book.CodeAt(TitleCategory.Despair, 0)));
    }

    [Fact]
    public void VerifyRepairsAClearedAchievementWhoseTitleWentMissingUnlessTheTitleIsWorn()
    {
        TitleSystem system = NewSystem();
        system.Trigger(9001, 1, 0, 0, 1);
        system.Book.Remove(TitleCategory.General, 0);
        Assert.Equal(0, system.Verify(q => q == 8001, 901));                                             // 穿着:不补
        Assert.Equal(1, system.Verify(q => q == 8001, 0));
        Assert.Equal(901, system.Book.CodeAt(TitleCategory.General, 0));
    }

    [Fact]
    public void VerifySkipsASlotWhoseAchievementIsNotInTheCatalog()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(2).Ints(0, 0, 424242, 1, 901).Ints(1, 0, 9001, 1, 901);
        var system = new TitleSystem(TitleBookScript.Parse(TitleFixtures.Lines(b)), TitleFixtures.Achievements(), TitleFixtures.Lookup());
        Assert.Equal(1, system.Verify(q => q == 8001, 0));
        Assert.True(system.Achievements.IsCleared(9001));
    }

    [Fact]
    public void VerifyAndAdjustRejectNullPredicates()
    {
        TitleSystem system = NewSystem();
        Assert.Throws<ArgumentNullException>(() => system.Verify(null, 0));
        Assert.Throws<ArgumentNullException>(() => system.Adjust(null));
    }

    // ------------------------------------------------------------------ 补发清单

    [Fact]
    public void AdjustCompletesAndPlacesTheTitleOfEachClearedQuestOnTheList()
    {
        TitleSystem system = NewSystem();
        Assert.Equal(1, system.Adjust(q => q == 8001));
        Assert.True(system.Achievements.IsCleared(9001));
        Assert.Equal(901, system.Book.CodeAt(TitleCategory.General, 0));
        Assert.False(system.Achievements.IsCleared(9004));
        Assert.Equal(0, system.Adjust(_ => false));
        Assert.Equal(2, system.Adjust(_ => true));
        Assert.Equal(904, system.Book.CodeAt(TitleCategory.General, 4));
        Assert.Empty(system.Outbox);
    }

    [Fact]
    public void AdjustDoesNotCheckWhetherTheAchievementWasAlreadyDoneSoItPutsTheTitleBackEachTime()
    {
        TitleSystem system = NewSystem();
        system.Trigger(9001, 1, 0, 0, 1);
        system.Book.Remove(TitleCategory.General, 0);
        Assert.Equal(1, system.Adjust(q => q == 8001));
        Assert.Equal(901, system.Book.CodeAt(TitleCategory.General, 0));
    }

    [Fact]
    public void AdjustSkipsAListEntryWhoseAchievementIsUnknown()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[adjust list]").Ints(8001, 901, 424242, 8004, 904, 9004);
        var system = new TitleSystem(TitleBookScript.Parse(TitleFixtures.Lines(b)), TitleFixtures.Achievements(), TitleFixtures.Lookup());
        Assert.Equal(0, system.Adjust(q => q == 8001));
        Assert.Equal(1, system.Adjust(q => q == 8004));
    }

    // ------------------------------------------------------------------ 收集加成

    [Fact]
    public void TheCollectionBonusIsTheDefaultRewardsPlusTheStatRewardsOfCompletePages()
    {
        TitleSystem system = NewSystem();
        TitleBonus none = system.Bonus();
        Assert.Equal((100.0, 50.0, 0, 2, 0), (none.Stats[EquipStat.HpMax], none.Stats[EquipStat.MpMax], none.PagesCounted, none.RewardsApplied, none.RewardsSkipped));
        Assert.True(system.Bonus(includeDefault: false).Stats.IsEmpty);

        CompleteFirstPage(system);
        TitleBonus page = system.Bonus();
        Assert.Equal((130.0, 50.0, 1, 3, 1), (page.Stats[EquipStat.HpMax], page.Stats[EquipStat.MpMax], page.PagesCounted, page.RewardsApplied, page.RewardsSkipped));   // 属性表里没有 [all element]:只计数
        Assert.Equal(30.0, system.Bonus(includeDefault: false).Stats[EquipStat.HpMax]);
    }

    [Fact]
    public void AnIncompletePageAddsNothingAndEachCompletePageAddsItsOwnRewards()
    {
        TitleSystem system = NewSystem();
        system.Trigger(9001, 1, 0, 0, 10);
        system.Trigger(9002, 3, 0, 0, 10);                                                               // 第 0 页还差 9003
        Assert.Equal(0, system.Bonus().PagesCounted);
        system.Trigger(9005, 1, 0, 0, 10);                                                               // 特殊页收 9005、9006:差一个
        Assert.False(system.Bonus().Stats.Has(EquipStat.PhysicalAttack));
        system.Trigger(9006, 1, 0, 0, 10);
        TitleBonus two = system.Bonus();
        Assert.Equal((7.0, 1, 0.0), (two.Stats[EquipStat.PhysicalAttack], two.PagesCounted, two.Stats[EquipStat.HpMax] - 100.0));
    }

    [Fact]
    public void APageWithoutAnyBoundSlotNeverCountsAndANonAbsoluteRewardIsSkipped()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[default reward]").Tag("[hp max]").Str("%").Int(10).Tag("[/default reward]");
        b.Tag("[title collection info]").Str("event").Int(1).Ints(0, 1, -1, 1, -1);
        b.Tag("[section]").Str("event").Tag("[section name]").Keyed(1, "e").Tag("[hp max]").Str("+").Int(99).Tag("[/reward type]").Tag("[/section]");
        TitleBookDef def = TitleBookScript.Parse(TitleFixtures.Lines(b));
        TitleBonus bonus = TitleBonus.Compute(def, _ => false);
        Assert.Equal((0, 0, 1), (bonus.PagesCounted, bonus.RewardsApplied, bonus.RewardsSkipped));       // 默认奖励的取值方式不是 "+":跳过;活动页不算
        Assert.True(bonus.Stats.IsEmpty);
        Assert.True(TitleBonus.None.Stats.IsEmpty);
        Assert.Throws<ArgumentNullException>(() => TitleBonus.Compute(null, _ => true));
        Assert.Throws<ArgumentNullException>(() => TitleBonus.Compute(def, null));
    }

    [Fact]
    public void ABoundSlotJustPastThePageEndDoesNotMakeThePageCount()
    {
        // 第 0 页收第 0 到 9 格,这 10 格都没绑成就;绑了成就的是第 10 格(在这一页的终点之外):这一页不能算"有成就格",收集加成不给。
        var cells = new List<int> { 11 };
        for (int k = 0; k < 10; k++)
        {
            cells.AddRange(new[] { k, 1, -1, 1, -1 });
        }
        cells.AddRange(new[] { 10, 1, 9001, 1, 901 });
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("event").Ints(cells.ToArray());
        b.Tag("[section]").Str("event").Tag("[section name]").Keyed(1, "e").Tag("[hp max]").Str("+").Int(99).Tag("[/reward type]").Tag("[/section]");
        TitleBookDef def = TitleBookScript.Parse(TitleFixtures.Lines(b));
        Assert.Equal((0, 10), (def.Sections[0].Start, def.Sections[0].End));
        TitleBonus bonus = TitleBonus.Compute(def, _ => false);
        Assert.Equal((0, 0), (bonus.PagesCounted, bonus.RewardsApplied));
        Assert.True(bonus.Stats.IsEmpty);
    }

    // ------------------------------------------------------------------ 邮件箱、清空、构造

    [Fact]
    public void DrainingTheOutboxHandsOverEverythingAndEmptiesIt()
    {
        TitleSystem system = NewSystem();
        CompleteFirstPage(system);
        List<TitlePostal> taken = system.DrainOutbox();
        Assert.Equal(2, taken.Count);
        Assert.Empty(system.Outbox);
        Assert.Empty(system.DrainOutbox());
    }

    [Fact]
    public void ClearEmptiesTheBookTheProgressAndTheOutbox()
    {
        TitleSystem system = NewSystem();
        CompleteFirstPage(system);
        Assert.True(system.Version > 0);
        system.Clear();
        Assert.Equal((0, 0, 0), (system.Book.TotalCount, system.Achievements.EntryCount, system.Outbox.Count));
        Assert.False(system.Achievements.IsCleared(9001));
    }

    [Fact]
    public void TheVersionIsTheSumOfTheBookAndTheTrackerVersions()
    {
        TitleSystem system = NewSystem();
        Assert.Equal(0, system.Version);
        system.Trigger(9001, 1, 0, 0, 1);
        Assert.Equal(system.Book.Version + system.Achievements.Version, system.Version);
        Assert.True(system.Version >= 2);
    }

    [Fact]
    public void ConstructionRejectsNullParts()
    {
        Assert.Throws<ArgumentNullException>(() => new TitleSystem(null, TitleFixtures.Achievements(), TitleFixtures.Lookup()));
        Assert.Throws<ArgumentNullException>(() => new TitleSystem(TitleFixtures.Book(), null, TitleFixtures.Lookup()));
        Assert.Throws<ArgumentNullException>(() => new TitleSystem(TitleFixtures.Book(), TitleFixtures.Achievements(), null));
    }
}
