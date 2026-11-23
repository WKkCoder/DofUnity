using Dnf.Game;
using Kit = Dnf.Play.Tests.OnlineSettlementGapBKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 结算摘要构造器(<see cref="OnlineSettlementSummaryBuilder"/>)与结算数据载体的边界补测(区域 B):
/// 经验块长度 1/2 的分支、本地回退值的夹取、奖励槽选择、掉落条目的数量缺省与任务标记、物品名解析的物品号门槛、
/// <see cref="OnlineSettlementSummary.MarkExpApplied"/> 的夹取、<see cref="OnlineDungeonSettlementData"/> 十参构造的超时缺省。
/// 全部合成数据,期望值手算。
/// </summary>
public sealed class OnlineSettlementGapBModelTests
{
    // ---- 已发经验记录 ----

    /// <summary>保护的行为:已发经验记账精确记下发出的点数(1 就是 1),0 与负数夹成 0,升级数按前后等级差算。</summary>
    [Fact]
    public void MarkExpApplied_records_the_exact_amount_and_clamps_non_positive_amounts_to_zero()
    {
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(Kit.Data(null));
        Assert.False(summary.ExpApplied);
        Assert.Equal(0, summary.ExpAppliedAmount);
        Assert.Equal(0, summary.LevelsGained);

        // 正数里最小的 1:原样记下(不是 0);等级 3→4 升 1 级
        summary.MarkExpApplied(1, 3, 4);
        Assert.True(summary.ExpApplied);
        Assert.Equal(1, summary.ExpAppliedAmount);
        Assert.Equal(3, summary.LevelBefore);
        Assert.Equal(4, summary.LevelAfter);
        Assert.Equal(1, summary.LevelsGained);

        // 2 与大数原样;等级没变时升级数为 0
        summary.MarkExpApplied(2, 4, 4);
        Assert.Equal(2, summary.ExpAppliedAmount);
        Assert.Equal(0, summary.LevelsGained);
        summary.MarkExpApplied(1300, 1, 2);
        Assert.Equal(1300, summary.ExpAppliedAmount);

        // 0 与负数夹成 0(记账不会出现负的"已发经验")
        summary.MarkExpApplied(0, 5, 5);
        Assert.True(summary.ExpApplied);
        Assert.Equal(0, summary.ExpAppliedAmount);
        summary.MarkExpApplied(-9, 5, 6);
        Assert.Equal(0, summary.ExpAppliedAmount);
        Assert.Equal(1, summary.LevelsGained);
    }

    // ---- 经验块长度 1 / 2 ----

    /// <summary>
    /// 保护的行为:经验值表只有一项(只有基础经验)时,评分加成缺省为"没有"(不是 1、也不越界读第 2 项),
    /// 有把握增量恰好等于基础经验;不存在的槽不参与与本地的对账。
    /// </summary>
    [Fact]
    public void A_single_entry_exp_table_gives_base_exp_only_with_no_rank_bonus_and_no_out_of_range_read()
    {
        // 本地基础经验与服务端相同(1500);本地评分加成故意取非零 40:服务端没有这一槽,不得拿它比较
        DungeonResult local = Kit.LocalResult(expBase: 1500, expRankBonus: 40);
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(expValues: new[] { 1500 })),
            Kit.LocalWith(local));

        Assert.True(summary.BaseExp.HasValue);
        Assert.Equal(1500, summary.BaseExp.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, summary.BaseExp.Confidence);
        Assert.Equal(SettlementFieldSource.Server, summary.BaseExp.Source);

        Assert.False(summary.RankBonusExp.HasValue);
        Assert.False(summary.RankBonusExp.ShouldDisplay);
        Assert.Equal(SettlementFieldSource.None, summary.RankBonusExp.Source);

        Assert.Equal(1500, summary.ConfidentExpGrant); // 1500 + 评分加成缺省 0
        Assert.False(summary.UnknownExpSlot2.HasValue);
        Assert.Empty(summary.NamedBonusExpLines);
        Assert.Empty(summary.DiffFields); // 基础经验一致;评分加成那一槽不存在,不对账
    }

    /// <summary>
    /// 保护的行为:经验值表恰好两项时,评分加成有值(服务端、推断),有把握增量是两者之和,
    /// 与本地评分加成不一致记 <c>rank_bonus_exp</c>;第 2 槽(未知字段)不存在,保持缺失、不越界。
    /// </summary>
    [Fact]
    public void A_two_entry_exp_table_exposes_the_rank_bonus_sums_both_and_reports_the_diff()
    {
        // 基础 1500 与本地一致;评分加成服务端 300、本地 120 → 只差一项
        DungeonResult local = Kit.LocalResult(expBase: 1500, expRankBonus: 120);
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(expValues: new[] { 1500, 300 })),
            Kit.LocalWith(local));

        Assert.True(summary.BaseExp.HasValue);
        Assert.Equal(1500, summary.BaseExp.Value);

        Assert.True(summary.RankBonusExp.HasValue);
        Assert.True(summary.RankBonusExp.ShouldDisplay);
        Assert.Equal(300, summary.RankBonusExp.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, summary.RankBonusExp.Confidence);
        Assert.Equal(SettlementFieldSource.Server, summary.RankBonusExp.Source);

        Assert.Equal(1800, summary.ConfidentExpGrant); // 1500 + 300
        Assert.Equal(new[] { "rank_bonus_exp" }, summary.DiffFields);

        Assert.False(summary.UnknownExpSlot2.HasValue); // 没有第 2 槽
        Assert.False(summary.UnknownExpSlot2.ShouldDisplay);
        Assert.Empty(summary.NamedBonusExpLines);
    }

    /// <summary>保护的行为:恰好三项时第 2 槽被读成"未知"(有值但永不显示),具名分项仍为空(下标 3 起才有)。</summary>
    [Fact]
    public void A_three_entry_exp_table_records_slot_two_as_unknown_and_never_displays_it()
    {
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(expValues: new[] { 1500, 300, 777 })));

        Assert.True(summary.UnknownExpSlot2.HasValue);
        Assert.Equal(777, summary.UnknownExpSlot2.Value);
        Assert.Equal(SettlementFieldConfidence.Unknown, summary.UnknownExpSlot2.Confidence);
        Assert.False(summary.UnknownExpSlot2.ShouldDisplay);
        Assert.Equal(1800, summary.ConfidentExpGrant); // 未知槽不进增量
        Assert.Empty(summary.NamedBonusExpLines);
    }

    // ---- 本地回退 ----

    /// <summary>
    /// 保护的行为:没有服务端经验块、只有本地结算时,基础经验为正才显示、评分加成为正才显示(0 保持缺失),
    /// 都标"观察/本地";本地值不进有把握增量。
    /// </summary>
    [Fact]
    public void Local_only_settlement_shows_each_exp_field_only_when_positive()
    {
        // 评分加成为 0:不显示
        OnlineSettlementSummary zeroBonus = OnlineSettlementSummaryBuilder.Build(Kit.Data(null), Kit.LocalWith(Kit.LocalResult(expBase: 700, expRankBonus: 0)));
        Assert.True(zeroBonus.BaseExp.HasValue);
        Assert.Equal(700, zeroBonus.BaseExp.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, zeroBonus.BaseExp.Confidence);
        Assert.Equal(SettlementFieldSource.Local, zeroBonus.BaseExp.Source);
        Assert.False(zeroBonus.RankBonusExp.HasValue);
        Assert.False(zeroBonus.RankBonusExp.ShouldDisplay);
        Assert.Equal(0, zeroBonus.ConfidentExpGrant);

        // 评分加成为 1:显示本地观测值 1
        OnlineSettlementSummary oneBonus = OnlineSettlementSummaryBuilder.Build(Kit.Data(null), Kit.LocalWith(Kit.LocalResult(expBase: 700, expRankBonus: 1)));
        Assert.True(oneBonus.RankBonusExp.HasValue);
        Assert.True(oneBonus.RankBonusExp.ShouldDisplay);
        Assert.Equal(1, oneBonus.RankBonusExp.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, oneBonus.RankBonusExp.Confidence);
        Assert.Equal(SettlementFieldSource.Local, oneBonus.RankBonusExp.Source);

        // 基础经验为 0:不显示;评分加成 5 照常显示
        OnlineSettlementSummary zeroBase = OnlineSettlementSummaryBuilder.Build(Kit.Data(null), Kit.LocalWith(Kit.LocalResult(expBase: 0, expRankBonus: 5)));
        Assert.False(zeroBase.BaseExp.HasValue);
        Assert.False(zeroBase.BaseExp.ShouldDisplay);
        Assert.True(zeroBase.RankBonusExp.HasValue);
        Assert.Equal(5, zeroBase.RankBonusExp.Value);
        Assert.Equal(0, zeroBase.ConfidentExpGrant);
    }

    /// <summary>
    /// 保护的行为:本地回退的 long 经验值夹进 int 范围:1 还是 1、5 还是 5(小正数不被吞成 0),
    /// 恰好 int 上限与超过上限的都夹到 <c>int.MaxValue</c>;基础经验与评分加成走同一套夹取。
    /// </summary>
    [Theory]
    [InlineData(1L, 1)]
    [InlineData(2L, 2)]
    [InlineData(5L, 5)]
    [InlineData(65536L, 65536)]
    [InlineData(2147483646L, 2147483646)]  // int.MaxValue - 1:原样
    [InlineData(2147483647L, 2147483647)]  // 恰好 int.MaxValue
    [InlineData(2147483648L, 2147483647)]  // int.MaxValue + 1:夹住
    [InlineData(4294967295L, 2147483647)]  // uint.MaxValue(通关经验是 32 位无符号):夹住
    public void Local_fallback_exp_values_are_clamped_into_int_range_without_swallowing_small_values(long local, int expected)
    {
        OnlineSettlementSummary baseOnly = OnlineSettlementSummaryBuilder.Build(Kit.Data(null), Kit.LocalWith(Kit.LocalResult(expBase: local, expRankBonus: 0)));
        Assert.True(baseOnly.BaseExp.HasValue);
        Assert.Equal(expected, baseOnly.BaseExp.Value);

        OnlineSettlementSummary bonusOnly = OnlineSettlementSummaryBuilder.Build(Kit.Data(null), Kit.LocalWith(Kit.LocalResult(expBase: 0, expRankBonus: local)));
        Assert.True(bonusOnly.RankBonusExp.HasValue);
        Assert.Equal(expected, bonusOnly.RankBonusExp.Value);
    }

    // ---- 奖励槽选择 ----

    private static OnlineSettlementField<int> GoldOf(OnlineSettlementRewardSlotData[] slots, int? partySlot)
    {
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(rewards: slots)),
            new OnlineSettlementLocalContext { PartySlot = partySlot });
        return summary.RewardGold;
    }

    /// <summary>保护的行为:没有指定队伍槽时,取第一个状态非 0 的奖励槽(状态 0 = 空槽;状态 1、2、3 都算有效)。</summary>
    [Fact]
    public void Reward_gold_without_a_party_slot_comes_from_the_first_non_empty_slot()
    {
        // 槽 0 空、槽 1 状态 1(金额 200)、槽 2 状态 2(300)、槽 3 空:取槽 1
        OnlineSettlementField<int> gold = GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(1, 200), Kit.Slot(2, 300), Kit.Slot(0, 400) }, null);
        Assert.True(gold.HasValue);
        Assert.Equal(200, gold.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, gold.Confidence);
        Assert.Equal(SettlementFieldSource.Server, gold.Source);

        // 首槽状态 2:取首槽
        Assert.Equal(300, GoldOf(new[] { Kit.Slot(2, 300), Kit.Slot(1, 200) }, null).Value);

        // 只有最后一个槽状态为 1:取它(状态 1 本身有效)
        OnlineSettlementField<int> lastOnly = GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(0, 200), Kit.Slot(1, 300) }, null);
        Assert.True(lastOnly.HasValue);
        Assert.Equal(300, lastOnly.Value);

        // 其它非零状态(3)同样有效
        Assert.Equal(500, GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(3, 500) }, null).Value);
    }

    /// <summary>保护的行为:所有奖励槽都是状态 0(空)时没有奖励金币字段(不伪造 0);没有奖励槽时同理。</summary>
    [Fact]
    public void Reward_gold_is_absent_when_every_slot_is_empty_or_there_are_no_slots()
    {
        OnlineSettlementField<int> allEmpty = GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(0, 200) }, null);
        Assert.False(allEmpty.HasValue);
        Assert.False(allEmpty.ShouldDisplay);

        // 即便指定了队伍槽,槽也是空的:仍然没有
        Assert.False(GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(0, 200) }, 1).HasValue);

        Assert.False(GoldOf(Array.Empty<OnlineSettlementRewardSlotData>(), null).HasValue);
    }

    /// <summary>
    /// 保护的行为:指定了有效且非空的队伍槽时优先取它(状态 1 也算非空,哪怕前面还有别的有效槽);
    /// 队伍槽是空槽、越界或为负时退回"第一个非空槽"。
    /// </summary>
    [Fact]
    public void Reward_gold_prefers_the_party_slot_when_it_is_non_empty_and_otherwise_falls_back()
    {
        // 队伍槽 1 状态 1(金额 200),槽 0 状态 2(300)在前:队伍槽优先 → 200
        Assert.Equal(200, GoldOf(new[] { Kit.Slot(2, 300), Kit.Slot(1, 200) }, 1).Value);

        // 队伍槽 2 状态 2(300),前面槽 1 也有效(200):取 300
        Assert.Equal(300, GoldOf(new[] { Kit.Slot(1, 100), Kit.Slot(2, 200), Kit.Slot(2, 300) }, 2).Value);

        // 队伍槽 1 是空槽(状态 0,金额 100):不取,退回第一个非空槽 0 → 300
        Assert.Equal(300, GoldOf(new[] { Kit.Slot(2, 300), Kit.Slot(0, 100) }, 1).Value);

        // 队伍槽越界(5)与为负(-1):同样退回第一个非空槽 → 200
        Assert.Equal(200, GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(2, 200) }, 5).Value);
        Assert.Equal(200, GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(2, 200) }, -1).Value);
        // 队伍槽恰好等于槽数(越界一位):退回
        Assert.Equal(200, GoldOf(new[] { Kit.Slot(0, 100), Kit.Slot(2, 200) }, 2).Value);
    }

    /// <summary>保护的行为:奖励金币与本地拾取金币对账——取到的槽金额与本地不同才记 <c>reward_gold</c>。</summary>
    [Fact]
    public void Reward_gold_diff_is_reported_against_the_selected_slot_value()
    {
        var slots = new[] { Kit.Slot(0, 100), Kit.Slot(1, 200), Kit.Slot(2, 300) };
        OnlineSettlementSummary differs = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(rewards: slots)),
            new OnlineSettlementLocalContext { LocalGoldEarned = 100 }); // 本地 100 ≠ 选中的槽 1 的 200
        Assert.Equal(new[] { "reward_gold" }, differs.DiffFields);

        OnlineSettlementSummary equal = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(rewards: slots)),
            new OnlineSettlementLocalContext { LocalGoldEarned = 200 }); // 与选中的槽 1 相同
        Assert.Empty(equal.DiffFields);
    }

    // ---- 掉落条目 ----

    /// <summary>
    /// 保护的行为:掉落列表先放任务清图物品、再按槽序放奖励槽里的物品(状态 2 才有物品);
    /// 任务物品标 <c>IsQuestItem</c>、奖励槽物品不标;数量 ≤ 0 缺省为 1,正数原样。
    /// </summary>
    [Fact]
    public void Drops_list_quest_items_first_then_reward_slot_items_with_the_quest_flag_and_count_defaults()
    {
        var quest = new[] { Kit.Quest(9811, 4), Kit.Quest(9812, 0) };
        var rewards = new[]
        {
            Kit.Slot(2, 10, itemId: 9821, itemData: 0),   // 数量缺省(0)→ 1
            Kit.Slot(1, 20, itemId: 9822, itemData: 7),   // 状态 1:没有物品,不产生掉落
            Kit.Slot(2, 30, itemId: 9823, itemData: -4),  // 负数 → 1
            Kit.Slot(2, 40, itemId: 9824, itemData: 1),   // 1 → 1
            Kit.Slot(2, 50, itemId: 9825, itemData: 5),   // 5 → 5
            Kit.Slot(0, 60, itemId: 9826, itemData: 3),   // 空槽:没有物品
        };

        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(questItems: quest, rewards: rewards)),
            local: null,
            resolveItemName: id => "合成物品" + id);

        Assert.Equal(6, summary.Drops.Count);

        Assert.Equal(9811, summary.Drops[0].ItemId);
        Assert.Equal(4, summary.Drops[0].Count);
        Assert.True(summary.Drops[0].IsQuestItem);
        Assert.Equal("合成物品9811", summary.Drops[0].Name);

        Assert.Equal(9812, summary.Drops[1].ItemId);
        Assert.Equal(1, summary.Drops[1].Count);
        Assert.True(summary.Drops[1].IsQuestItem);

        Assert.Equal(9821, summary.Drops[2].ItemId);
        Assert.Equal(1, summary.Drops[2].Count);
        Assert.False(summary.Drops[2].IsQuestItem);

        Assert.Equal(9823, summary.Drops[3].ItemId);
        Assert.Equal(1, summary.Drops[3].Count);
        Assert.False(summary.Drops[3].IsQuestItem);

        Assert.Equal(9824, summary.Drops[4].ItemId);
        Assert.Equal(1, summary.Drops[4].Count);
        Assert.False(summary.Drops[4].IsQuestItem);

        Assert.Equal(9825, summary.Drops[5].ItemId);
        Assert.Equal(5, summary.Drops[5].Count);
        Assert.False(summary.Drops[5].IsQuestItem);
        Assert.Equal("合成物品9825", summary.Drops[5].Name);
    }

    // ---- 物品名解析 ----

    /// <summary>
    /// 保护的行为:物品名解析器只对物品号 &gt; 0 调用(0、负数不去解析,名字缺省);物品号 1 要去解析;
    /// 解析器抛异常或返回空串时该条目退回"未解析",构造本身不抛。
    /// </summary>
    [Fact]
    public void Item_name_resolver_is_called_only_for_positive_item_ids_and_failures_degrade_to_unresolved()
    {
        var calls = new List<int>();
        Func<int, string> resolver = id =>
        {
            calls.Add(id);
            return "合成名" + id;
        };
        var quest = new[] { Kit.Quest(0, 1), Kit.Quest(-1, 1), Kit.Quest(1, 1), Kit.Quest(2, 1) };
        var rewards = new[]
        {
            Kit.Slot(2, 0, itemId: 0, itemData: 1),
            Kit.Slot(2, 0, itemId: -3, itemData: 1),
            Kit.Slot(2, 0, itemId: 1, itemData: 1),
            Kit.Slot(2, 0, itemId: 3, itemData: 1),
        };

        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(questItems: quest, rewards: rewards)),
            local: null,
            resolveItemName: resolver);

        // 调用序列:任务物品 1、2,然后奖励槽 1、3;物品号 0、-1、-3 没有去解析
        Assert.Equal(new[] { 1, 2, 1, 3 }, calls);

        Assert.Equal(8, summary.Drops.Count);
        Assert.False(summary.Drops[0].NameResolved);
        Assert.Null(summary.Drops[0].Name);
        Assert.False(summary.Drops[1].NameResolved);
        Assert.True(summary.Drops[2].NameResolved);
        Assert.Equal("合成名1", summary.Drops[2].Name);
        Assert.True(summary.Drops[3].NameResolved);
        Assert.Equal("合成名2", summary.Drops[3].Name);
        Assert.False(summary.Drops[4].NameResolved);
        Assert.False(summary.Drops[5].NameResolved);
        Assert.True(summary.Drops[6].NameResolved);
        Assert.Equal("合成名1", summary.Drops[6].Name);
        Assert.True(summary.Drops[7].NameResolved);
        Assert.Equal("合成名3", summary.Drops[7].Name);

        // 抛异常、返回空串:都当没解析到
        OnlineSettlementSummary failing = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(questItems: new[] { Kit.Quest(7, 1), Kit.Quest(8, 1) })),
            local: null,
            resolveItemName: id => id == 7 ? throw new InvalidOperationException("合成故障") : string.Empty);
        Assert.Equal(2, failing.Drops.Count);
        Assert.False(failing.Drops[0].NameResolved);
        Assert.False(failing.Drops[1].NameResolved);

        // 没有解析器:全部未解析
        OnlineSettlementSummary none = OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(questItems: new[] { Kit.Quest(1, 1) })));
        Assert.False(Assert.Single(none.Drops).NameResolved);
    }

    // ---- 十参构造 ----

    /// <summary>
    /// 保护的行为:结算数据的十参构造(旧调用方)不是超时态;十一参构造把超时标记原样带出,
    /// 摘要沿用该标记;十参构造的其余字段一一对应、互不串位。
    /// </summary>
    [Fact]
    public void Settlement_data_ten_argument_constructor_is_not_timed_out_and_eleven_argument_carries_the_flag()
    {
        var result = new OnlineSettlementResultData(0, 1000, 0, 0, 0, false, Array.Empty<OnlineSettlementBestTime>());
        OnlineSettlementRewardData reward = Kit.Reward(expValues: new[] { 5 });

        var ten = new OnlineDungeonSettlementData(
            9711, "dungeon/test/ten.dgn", 3, 14, 15, 16,
            OnlineSettlementParseState.Succeeded, result,
            OnlineSettlementParseState.Failed, reward);
        Assert.False(ten.IsTimedOut);
        Assert.Equal(9711, ten.DungeonId);
        Assert.Equal("dungeon/test/ten.dgn", ten.DungeonPath);
        Assert.Equal(3, ten.Difficulty);
        Assert.Equal(14, ten.ReportedKills);
        Assert.Equal(15, ten.DroppedKills);
        Assert.Equal(16, ten.RoomsEntered);
        Assert.Equal(OnlineSettlementParseState.Succeeded, ten.ResultParseState);
        Assert.Same(result, ten.Result);
        Assert.Equal(OnlineSettlementParseState.Failed, ten.RewardParseState);
        Assert.Same(reward, ten.Reward);

        var timedOut = new OnlineDungeonSettlementData(
            9711, "dungeon/test/ten.dgn", 3, 14, 15, 16,
            OnlineSettlementParseState.Succeeded, result,
            OnlineSettlementParseState.Failed, reward,
            isTimedOut: true);
        Assert.True(timedOut.IsTimedOut);

        var notTimedOut = new OnlineDungeonSettlementData(
            9711, "dungeon/test/ten.dgn", 3, 14, 15, 16,
            OnlineSettlementParseState.Succeeded, result,
            OnlineSettlementParseState.Failed, reward,
            isTimedOut: false);
        Assert.False(notTimedOut.IsTimedOut);

        Assert.False(OnlineSettlementSummaryBuilder.Build(ten).IsTimedOut);
        Assert.True(OnlineSettlementSummaryBuilder.Build(timedOut).IsTimedOut);
    }
}
