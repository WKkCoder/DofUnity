using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 称号图鉴的定义(图鉴脚本 titlebook)与从定义里查规则:读法、分页(含服务端写法上的怪异)、"这件称号放哪一格"、"这一页收齐没有"。
/// 证据是服务端静态读码(导入 importTitleBookEtcScript、取用 _getFitSlot/_checkFitSlot/_getSectionIdx/_getTabComplete,地址在笔记里),
/// 数据全是合成的(见 <see cref="TitleFixtures"/>);真实数据的整库数一遍在 title-check 里。
/// </summary>
public class TitleBookDefTests
{
    private static TitleBookDef Parse(TokenStreamBuilder b) => TitleBookScript.Parse(TitleFixtures.Lines(b));

    // ------------------------------------------------------------------ 读定义

    [Fact]
    public void CollectionsAreReadPerCategoryWithFlagsAchievementsAndItems()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.Equal((6, 2, 1, 2), (def.SlotsOf(TitleCategory.General).Count, def.SlotsOf(TitleCategory.Specific).Count, def.SlotsOf(TitleCategory.Despair).Count, def.SlotsOf(TitleCategory.Event).Count));

        TitleSlotDef two = def.SlotAt(TitleCategory.General, 2);
        Assert.Equal((2, 0, 9003, true, false), (two.Index, two.Flag, two.AchievementId, two.IsBound, two.IsEmpty));
        Assert.Equal(new[] { 903, 913 }, two.Items);

        TitleSlotDef empty = def.SlotAt(TitleCategory.General, 3);
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsBound);
        Assert.Empty(empty.Items);

        TitleSlotDef unbound = def.SlotAt(TitleCategory.General, 5);
        Assert.Equal((0, -1, false, false), (unbound.Flag, unbound.AchievementId, unbound.IsBound, unbound.IsEmpty));
        Assert.Equal(new[] { 905 }, unbound.Items);

        TitleSlotDef activity = def.SlotAt(TitleCategory.Event, 1);
        Assert.Equal((1, -1, false), (activity.Flag, activity.AchievementId, activity.IsBound));
        Assert.Equal(new[] { -1 }, activity.Items);

        Assert.Equal((5, 2, 1, 2), (def.DefinedSlotCount(TitleCategory.General), def.DefinedSlotCount(TitleCategory.Specific), def.DefinedSlotCount(TitleCategory.Despair), def.DefinedSlotCount(TitleCategory.Event)));
        Assert.Equal(4, def.SkippedTags);                                                   // 四个 [/title collection info]
        Assert.Equal((0, 0, 0), (def.TruncatedSlotLists, def.SlotIndexMismatches, def.SkippedUnknownCategory));
    }

    [Fact]
    public void SlotLookupIsNullOutsideTheDefinedRange()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.Null(def.SlotAt(TitleCategory.General, 6));
        Assert.Null(def.SlotAt(TitleCategory.General, -1));
        Assert.Null(def.SlotAt((TitleCategory)9, 0));
        Assert.Empty(def.SlotsOf((TitleCategory)9));
    }

    [Fact]
    public void DefaultRewardsAndTheTopLevelFlagsAreRead()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.Equal(2, def.DefaultRewards.Count);
        Assert.Equal(("[hp max]", "+", 100.0, TitleRewardKind.Stat), (def.DefaultRewards[0].StatTag, def.DefaultRewards[0].ValueType, def.DefaultRewards[0].Value, def.DefaultRewards[0].Kind));
        Assert.Equal(("[mp max]", 50.0), (def.DefaultRewards[1].StatTag, def.DefaultRewards[1].Value));
        Assert.True(def.ViewEventMark);
        Assert.True(def.HasEventMinLevel);
        Assert.Equal(20, def.EventMinLevel);

        Assert.Equal(2, def.ConditionMessages.Count);
        Assert.Equal((TitleCategory.General, 1, "cond_g1"), (def.ConditionMessages[0].Category, def.ConditionMessages[0].Slot, def.ConditionMessages[0].Text.Key));
        Assert.Equal((TitleCategory.Specific, 0, "cond_s0"), (def.ConditionMessages[1].Category, def.ConditionMessages[1].Slot, def.ConditionMessages[1].Text.Key));

        Assert.Equal(2, def.Adjusts.Count);
        Assert.Equal((8001, 901, 9001), (def.Adjusts[0].QuestId, def.Adjusts[0].ItemCode, def.Adjusts[0].AchievementId));
        Assert.Equal((8004, 904, 9004), (def.Adjusts[1].QuestId, def.Adjusts[1].ItemCode, def.Adjusts[1].AchievementId));
    }

    [Fact]
    public void TheFlagsAreFalseWhenTheirTagsAreAbsent()
    {
        TitleBookDef def = Parse(new TokenStreamBuilder().Tag("[view titlebook eventmark]").Int(0));
        Assert.False(def.ViewEventMark);
        Assert.False(def.HasEventMinLevel);
        Assert.Equal(0, def.EventMinLevel);
        Assert.Empty(def.DefaultRewards);
    }

    [Fact]
    public void SectionsReadTheirFieldsAndRewardsAndClipTheEndToTheFirstEmptySlot()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.Equal(2, def.Sections.Count);

        TitleSectionDef general = def.Sections[0];
        Assert.Equal((TitleCategory.General, 0, 0, 3), (general.Category, general.PageIndex, general.Start, general.End));    // 第 3 格是空格:终点截到 3
        Assert.Equal(("sec_g0", "rew_g0"), (general.SectionName.Key, general.RewardName.Key));
        Assert.Equal(("icon/g0.img", 3, 2), (general.IconPath, general.IconFrame, general.FrameType));
        Assert.Equal(("img/g0.img", 1, "eff/g0.ani"), (general.ImagePath, general.ImageFrame, general.EffectPath));
        Assert.False(general.IsLocked);
        Assert.Equal(new[] { TitleRewardKind.Stat, TitleRewardKind.Stat, TitleRewardKind.Emotion, TitleRewardKind.Postal }, general.Rewards.Select(r => r.Kind).ToArray());
        Assert.Equal(("[hp max]", 30.0), (general.Rewards[0].StatTag, general.Rewards[0].Value));
        Assert.Equal(7, general.Rewards[2].EmotionId);
        TitleReward postal = general.PostalReward();
        Assert.Equal((950, 2), (postal.ItemCode, postal.ItemCount));
        Assert.Equal(new[] { "mail_title", "mail_body" }, postal.Texts.Select(t => t.Key).ToArray());

        TitleSectionDef specific = def.Sections[1];
        Assert.Equal((TitleCategory.Specific, 0, 0, 10), (specific.Category, specific.PageIndex, specific.Start, specific.End));  // 没有空格可截,终点留在 10
        Assert.True(specific.IsLocked);
        Assert.Equal("locked_s0", specific.LockMessage.Key);
        Assert.Null(specific.PostalReward());
    }

    [Fact]
    public void ThePageIndexCountsTheEarlierPagesOfTheSameCategoryOnly()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[section]").Str("general");
        b.Tag("[section name]").Keyed(1, "a").Tag("[/reward type]");
        b.Tag("[section name]").Keyed(1, "b").Tag("[/reward type]");
        b.Tag("[/section]");
        b.Tag("[section]").Str("specific");
        b.Tag("[section name]").Keyed(1, "c").Tag("[/reward type]");
        b.Tag("[/section]");
        b.Tag("[section]").Str("general");
        b.Tag("[section name]").Keyed(1, "d").Tag("[/reward type]");
        b.Tag("[/section]");
        TitleBookDef def = Parse(b);
        Assert.Equal(new[] { ("a", 0, 0), ("b", 1, 10), ("c", 0, 0), ("d", 2, 20) },
            def.Sections.Select(s => (s.SectionName.Key, s.PageIndex, s.Start)).ToArray());
        Assert.Equal(new[] { TitleCategory.General, TitleCategory.General, TitleCategory.Specific, TitleCategory.General }, def.Sections.Select(s => s.Category).ToArray());
        Assert.Equal(new[] { 10, 20, 10, 30 }, def.Sections.Select(s => s.End).ToArray());      // 没有格定义 → 不截
    }

    [Fact]
    public void ASectionWithoutItsEndMarkerCountsOnlyThePagesThatEnded()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[section]").Str("general");
        b.Tag("[section name]").Keyed(1, "done").Tag("[/reward type]");
        b.Tag("[section name]").Keyed(1, "dangling");                                           // 脚本在这里读完:这一页没有结束标记
        TitleBookDef def = Parse(b);
        Assert.Single(def.Sections);
        Assert.Equal("done", def.Sections[0].SectionName.Key);
    }

    [Fact]
    public void AnUnknownCategoryIsSkippedWholeAndCounted()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("bogus").Int(1).Ints(0, 0, 9001, 1, 901).Tag("[/title collection info]");
        b.Tag("[section]").Str("bogus");
        b.Tag("[section name]").Keyed(1, "x").Tag("[/reward type]");
        b.Tag("[/section]");
        b.Tag("[title collection info]").Str("general").Int(1).Ints(0, 0, 9002, 1, 902).Tag("[/title collection info]");
        TitleBookDef def = Parse(b);
        Assert.Equal(2, def.SkippedUnknownCategory);
        Assert.Empty(def.Sections);
        Assert.Single(def.SlotsOf(TitleCategory.General));
        Assert.Equal(9002, def.SlotAt(TitleCategory.General, 0).AchievementId);
    }

    [Fact]
    public void ATruncatedSlotListKeepsTheEarlierSlotsAndCountsTheCut()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(3).Ints(0, 0, 9001, 1, 901).Ints(1, 0, 9002);   // 第二格的物品个数读不出来
        TitleBookDef def = Parse(b);
        Assert.Single(def.SlotsOf(TitleCategory.General));
        Assert.Equal(1, def.TruncatedSlotLists);

        var b2 = new TokenStreamBuilder();
        b2.Tag("[title collection info]").Str("general").Int(3).Ints(0, 0, 9001, 2, 901);                // 声明两个物品只给了一个
        TitleBookDef def2 = Parse(b2);
        Assert.Empty(def2.SlotsOf(TitleCategory.General));
        Assert.Equal(1, def2.TruncatedSlotLists);

        var b3 = new TokenStreamBuilder();
        b3.Tag("[title collection info]").Str("general");                                                // 连格数都没有
        Assert.Equal(1, Parse(b3).TruncatedSlotLists);
    }

    [Fact]
    public void ASlotIndexThatDisagreesWithItsPositionIsCountedAndThePositionWins()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(2).Ints(5, 0, 9001, 1, 901).Ints(1, 0, 9002, 1, 902);
        TitleBookDef def = Parse(b);
        Assert.Equal(1, def.SlotIndexMismatches);                                                        // 第二格写的 1 恰好等于它的位置
        Assert.Equal(new[] { 0, 1 }, def.SlotsOf(TitleCategory.General).Select(s => s.Index).ToArray());
        Assert.Equal(9001, def.SlotAt(TitleCategory.General, 0).AchievementId);
    }

    [Fact]
    public void TheSameCategoryWrittenTwiceAppendsToTheVector()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(1).Ints(0, 0, 9001, 1, 901);
        b.Tag("[title collection info]").Str("general").Int(1).Ints(0, 0, 9002, 1, 902);
        TitleBookDef def = Parse(b);
        Assert.Equal(2, def.SlotsOf(TitleCategory.General).Count);
        Assert.Equal(9002, def.SlotAt(TitleCategory.General, 1).AchievementId);
        Assert.Equal(1, def.SlotIndexMismatches);                                                        // 第二段的序号 0 与向量位置 1 不一致
    }

    [Fact]
    public void UnknownTopLevelTagsAreSkippedAndANullScriptGivesAnEmptyDefinition()
    {
        TitleBookDef def = Parse(new TokenStreamBuilder().Tag("[something else]").Int(1).Tag("[/something else]"));
        Assert.Equal(2, def.SkippedTags);
        Assert.Empty(def.Sections);
        TitleBookDef none = TitleBookScript.Parse(null);
        Assert.Empty(none.Sections);
        Assert.Equal(0, none.DefinedSlotCount(TitleCategory.General));
    }

    [Fact]
    public void AnUnshapedRewardTagMakesNoRewardAndAFloatValueIsKept()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[section]").Str("general");
        b.Tag("[mystery tag]").Int(5);                                                                   // 不是"字符串 + 数"的写法:没有奖励
        b.Tag("[move speed]").Str("+").Float(2.5f);
        b.Tag("[give emotion]");                                                                         // 没有编号:按 0 读
        b.Tag("[send postal]");                                                                          // 空邮件
        b.Tag("[/reward type]");
        b.Tag("[/section]");
        TitleSectionDef section = Parse(b).Sections.Single();
        Assert.Equal(new[] { TitleRewardKind.Stat, TitleRewardKind.Emotion, TitleRewardKind.Postal }, section.Rewards.Select(r => r.Kind).ToArray());
        Assert.Equal(("[move speed]", 2.5), (section.Rewards[0].StatTag, section.Rewards[0].Value));
        Assert.Equal(0, section.Rewards[1].EmotionId);
        Assert.Equal((0, 0, 0), (section.Rewards[2].ItemCode, section.Rewards[2].ItemCount, section.Rewards[2].Texts.Length));
    }

    [Fact]
    public void AConditionMessageListStopsAtTheFirstGroupThatDoesNotFit()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[custom condition message]").Str("general").Int(1).Keyed(1, "ok").Str("bogus").Int(2).Keyed(1, "never").Str("event").Int(3).Keyed(1, "never2");
        TitleBookDef def = Parse(b);
        Assert.Single(def.ConditionMessages);
        Assert.Equal("ok", def.ConditionMessages[0].Text.Key);

        var b2 = new TokenStreamBuilder();
        b2.Tag("[custom condition message]").Str("general").Int(1);                                      // 不成组
        Assert.Empty(Parse(b2).ConditionMessages);
    }

    [Fact]
    public void AnAdjustListDropsAnIncompleteTail()
    {
        TitleBookDef def = Parse(new TokenStreamBuilder().Tag("[adjust list]").Ints(1, 2, 3, 4, 5));
        Assert.Single(def.Adjusts);
        Assert.Equal((1, 2, 3), (def.Adjusts[0].QuestId, def.Adjusts[0].ItemCode, def.Adjusts[0].AchievementId));
    }

    // ------------------------------------------------------------------ 取用规则

    [Fact]
    public void TheFitSlotIsTheFirstSlotThatListsTheItemInCategoryOrder()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.True(def.TryFindFitSlot(903, out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.General, 2), (c, s));
        Assert.True(def.TryFindFitSlot(913, out c, out s));
        Assert.Equal((TitleCategory.General, 2), (c, s));
        Assert.True(def.TryFindFitSlot(905, out c, out s));
        Assert.Equal((TitleCategory.General, 5), (c, s));
        Assert.True(def.TryFindFitSlot(906, out c, out s));
        Assert.Equal((TitleCategory.Specific, 0), (c, s));
        Assert.True(def.TryFindFitSlot(908, out c, out s));
        Assert.Equal((TitleCategory.Despair, 0), (c, s));
        Assert.False(def.TryFindFitSlot(999, out c, out s));
        Assert.Equal(-1, s);
    }

    [Fact]
    public void AnItemListedInTwoCategoriesGoesToTheEarlierCategoryAndTheEarlierSlot()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("specific").Int(1).Ints(0, 0, 9001, 1, 950).Tag("[/title collection info]");
        b.Tag("[title collection info]").Str("general").Int(3).Ints(0, 0, 9002, 1, 951).Ints(1, 0, 9003, 1, 950).Ints(2, 0, 9004, 1, 950);
        TitleBookDef def = Parse(b);
        Assert.True(def.TryFindFitSlot(950, out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.General, 1), (c, s));                                               // 分类按枚举次序(普通在特殊前),不是脚本里写的次序
    }

    [Fact]
    public void TheMinusOnePlaceholderOfAnActivitySlotFindsTheFirstActivitySlot()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.True(def.TryFindFitSlot(-1, out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.Event, 0), (c, s));
    }

    [Fact]
    public void ASlotAcceptsWhatItListsAndAnythingWhenItIsBoundToNoAchievement()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.True(def.CheckFitSlot(901, TitleCategory.General, 0));
        Assert.False(def.CheckFitSlot(902, TitleCategory.General, 0));                                   // 绑了成就的格只收它列出的
        Assert.True(def.CheckFitSlot(903, TitleCategory.General, 2));
        Assert.True(def.CheckFitSlot(913, TitleCategory.General, 2));
        Assert.False(def.CheckFitSlot(901, TitleCategory.General, 2));
        Assert.True(def.CheckFitSlot(901, TitleCategory.General, 5));                                    // 不绑成就:什么都收
        Assert.True(def.CheckFitSlot(777, TitleCategory.Event, 0));                                      // 活动格同理
        Assert.True(def.CheckFitSlot(777, TitleCategory.General, 3));                                    // 空格的成就编号也是 −1:按服务端这一条也什么都收
    }

    [Fact]
    public void ASlotOutsideTheDefinitionAcceptsNothing()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.False(def.CheckFitSlot(901, TitleCategory.General, 6));
        Assert.False(def.CheckFitSlot(901, TitleCategory.General, -1));
        Assert.False(def.CheckFitSlot(901, (TitleCategory)9, 0));
        Assert.False(def.CheckFitSlot(901, TitleCategory.Despair, 1));
    }

    [Fact]
    public void OnlyASlotWithoutAnAchievementCanGiveItsTitleBackToTheBag()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.False(def.CanTakeBack(TitleCategory.General, 0));                                         // 绑了成就:只能直接穿
        Assert.True(def.CanTakeBack(TitleCategory.General, 5));
        Assert.True(def.CanTakeBack(TitleCategory.Event, 0));
        Assert.False(def.CanTakeBack(TitleCategory.General, 6));                                         // 没定义的格
        Assert.False(def.CanTakeBack((TitleCategory)9, 0));
    }

    [Fact]
    public void ASlotBelongsToTheFirstPageWhoseRangeContainsItAndTheEndIsInclusive()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.Equal(0, def.SectionIndexAt(TitleCategory.General, 0));
        Assert.Equal(0, def.SectionIndexAt(TitleCategory.General, 2));
        Assert.Equal(0, def.SectionIndexAt(TitleCategory.General, 3));                                   // 终点是"不含"的,服务端却用 ≤:空格 3 也算这一页
        Assert.Equal(-1, def.SectionIndexAt(TitleCategory.General, 4));
        Assert.Equal(1, def.SectionIndexAt(TitleCategory.Specific, 1));
        Assert.Equal(-1, def.SectionIndexAt(TitleCategory.Despair, 0));                                  // 这个分类没有页
        Assert.Same(def.Sections[0], def.SectionAt(TitleCategory.General, 1));
        Assert.Null(def.SectionAt(TitleCategory.Event, 0));
    }

    [Fact]
    public void AFullPageHandsTheFirstSlotOfTheNextPageToItselfAndTheNextPageKeepsTheRest()
    {
        TitleBookDef def = TitleFixtures.QuirkBook();
        Assert.Equal(new[] { 10, 11 }, def.Sections.Select(s => s.End).ToArray());
        Assert.Equal(0, def.SectionIndexAt(TitleCategory.General, 9));
        Assert.Equal(0, def.SectionIndexAt(TitleCategory.General, 10));                                  // 本该是第二页的第一格,被第一页(终点 10)抢走
        Assert.Equal(1, def.SectionIndexAt(TitleCategory.General, 11));
        Assert.Equal(-1, def.SectionIndexAt(TitleCategory.General, 12));
    }

    // ------------------------------------------------------------------ 整页收齐

    [Fact]
    public void APageIsCompleteWhenEveryBoundSlotOfItHasItsAchievementCleared()
    {
        TitleBookDef def = TitleFixtures.Book();
        TitleSectionDef page = def.Sections[0];                                                          // 收第 0、1、2 格:成就 9001、9002、9003
        var cleared = new HashSet<int> { 9001, 9002 };
        Assert.False(def.IsSectionComplete(page, cleared.Contains));                                     // 差一个
        cleared.Add(9003);
        Assert.True(def.IsSectionComplete(page, cleared.Contains));
        Assert.True(def.IsSectionComplete(page, id => id != 9999));                                      // 多出来的成就不影响
        Assert.False(def.IsSectionComplete(page, _ => false));
    }

    [Fact]
    public void ASlotThatIsEmptyOrBoundToNoAchievementDoesNotTakePartInCompleteness()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(4)
            .Ints(0, 0, 9001, 1, 901)
            .Ints(1, 0, -1, 1, 905)                                                                      // 不绑成就
            .Ints(2, 1, -1, 1, -1)                                                                       // 活动格
            .Ints(3, 0, 9002, 1, 902);
        b.Tag("[section]").Str("general").Tag("[section name]").Keyed(1, "p").Tag("[/reward type]").Tag("[/section]");
        TitleBookDef def = Parse(b);
        Assert.Equal(10, def.Sections[0].End);
        Assert.False(def.IsSectionComplete(def.Sections[0], id => id == 9001));
        Assert.True(def.IsSectionComplete(def.Sections[0], id => id is 9001 or 9002));
    }

    [Fact]
    public void APageWithoutAnyBoundSlotIsCompleteVacuouslyAndSoIsACategoryWithoutDefinitions()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("event").Int(1).Ints(0, 1, -1, 1, -1);
        b.Tag("[section]").Str("event").Tag("[section name]").Keyed(1, "e").Tag("[/reward type]").Tag("[/section]");
        b.Tag("[section]").Str("despair").Tag("[section name]").Keyed(1, "d").Tag("[/reward type]").Tag("[/section]");
        TitleBookDef def = Parse(b);
        Assert.True(def.IsSectionComplete(def.Sections[0], _ => false));                                 // 活动页:没有任何绑成就的格 → 空洞为真
        Assert.True(def.IsSectionComplete(def.Sections[1], _ => false));                                 // 这个分类一格都没定义
    }

    [Fact]
    public void CompletenessSkipsSlotsPastTheDefinitionInsteadOfReadingOutsideTheVector()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(2).Ints(0, 0, 9001, 1, 901).Ints(1, 0, 9002, 1, 902);
        b.Tag("[section]").Str("general").Tag("[section name]").Keyed(1, "p").Tag("[/reward type]").Tag("[/section]");
        TitleBookDef def = Parse(b);
        Assert.Equal(10, def.Sections[0].End);                                                           // 没有空格:终点留在 10,而定义只有两格
        Assert.True(def.IsSectionComplete(def.Sections[0], id => id is 9001 or 9002));
        Assert.False(def.IsSectionComplete(def.Sections[0], id => id == 9001));
    }

    [Fact]
    public void CompletenessRejectsNullArguments()
    {
        TitleBookDef def = TitleFixtures.Book();
        Assert.Throws<ArgumentNullException>(() => def.IsSectionComplete(null, _ => true));
        Assert.Throws<ArgumentNullException>(() => def.IsSectionComplete(def.Sections[0], null));
    }
}
