using Dnf.Data;
using Dnf.Game;
using Kit = Dnf.Play.Tests.OnlineSettlementGapBKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 结算摘要构造器(<see cref="OnlineSettlementSummaryBuilder"/>)的字段级补测(区域 B,第二轮):
/// 评分字母的缺省与本地来源、身份字段与解析状态原样带出、通关用时的服务端/本地取舍、个人最佳与新纪录的匹配、
/// 击杀与经验的对账差异、本地经验回退、奖励旗标与未知字段、差异清单的顺序与金币对账门槛。全部合成数据,期望值手算。
/// </summary>
public sealed class OnlineSettlementGapB2BuilderTests
{
    private const int DungeonId = 9711;
    private const ushort SelfUid = 9501;
    private const ushort OtherUid = 9502;

    private static OnlineSettlementResultData Result(int clearTime, bool block = true, byte rank = 0, byte questMaze = 0, OnlineSettlementBestTime[] entries = null)
    {
        return new OnlineSettlementResultData(rank, clearTime, 0, 0, questMaze, block, entries);
    }

    // ---- 评分字母 ----

    /// <summary>保护的行为:没有本地评分(没传上下文、上下文里没结算结果、结算结果没评分)时,评分字母缺省 F、取最后一帧、没有来源。</summary>
    [Fact]
    public void Without_a_ranked_local_result_the_rank_falls_back_to_letter_f_the_last_frame_and_no_source()
    {
        Assert.Equal(9, ResultArt.FrameCount); // 帧号 0..8,F 是最后一帧 8

        DungeonResult unranked = Kit.LocalResult(100, 10);
        Assert.False(unranked.HasRank);

        foreach (OnlineSettlementLocalContext local in new[] { null, new OnlineSettlementLocalContext(), Kit.LocalWith(unranked) })
        {
            OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(Kit.Data(null), local);
            Assert.False(s.HasRank);
            Assert.Equal("F", s.RankLetter);
            Assert.Equal(8, s.RankFrame);
            Assert.Equal(SettlementFieldSource.None, s.RankSource);
        }
    }

    /// <summary>保护的行为:有评分的本地结算结果决定评分字母与帧号(来源标本地),服务端 0x22 的评分字节只记成"未知"、不参与。</summary>
    [Fact]
    public void A_ranked_local_result_supplies_the_letter_and_frame_while_the_server_rank_byte_stays_unknown()
    {
        var thresholds = new[] { 90, 80, 70, 60, 50, 40, 30, 20 };
        var steps = new[] { new TimeBonusStep(0, 1), new TimeBonusStep(10000, 2), new TimeBonusStep(20000, 3), new TimeBonusStep(40000, 5) };
        var averages = new[] { (DungeonId, 1, new[] { 100000, 110000, 120000, 130000, 140000 }) };
        var rank = new RankSystem(thresholds, steps, averages);
        var expTable = new ExpTable(new[] { 1000, 3000, 7000, 15000, 30000 });
        var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50 });
        var progress = new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, 1);
        var stats = new RunStats(clearMs: 30000, kills: 5, comboKills: 5, maxCombo: 12, hitsTaken: 0, revivesUsed: 0);
        DungeonResult ranked = DungeonResult.Compute(stats, rank, progress, DungeonId, standardLevel: 1, difficulty: 0, expWeight: 1f);
        Assert.True(ranked.HasRank);
        Assert.NotEqual("F", ranked.Letter);
        Assert.NotEqual(8, ranked.Frame);

        // 服务端评分字节故意取 7(与本地评分无关)
        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(null, Result(45000, rank: 7)),
            Kit.LocalWith(ranked));

        Assert.True(s.HasRank);
        Assert.Equal(ranked.Letter, s.RankLetter);
        Assert.Equal(ranked.Frame, s.RankFrame);
        Assert.Equal(SettlementFieldSource.Local, s.RankSource);

        Assert.True(s.ServerRankValue.HasValue);
        Assert.Equal((byte)7, s.ServerRankValue.Value);
        Assert.Equal(SettlementFieldConfidence.Unknown, s.ServerRankValue.Confidence);
        Assert.Equal(SettlementFieldSource.Server, s.ServerRankValue.Source);
        Assert.False(s.ServerRankValue.ShouldDisplay);

        // 没有通关结果段时,服务端评分字节缺失
        OnlineSettlementSummary noResult = OnlineSettlementSummaryBuilder.Build(Kit.Data(null), Kit.LocalWith(ranked));
        Assert.False(noResult.ServerRankValue.HasValue);
        Assert.Equal(SettlementFieldSource.None, noResult.ServerRankValue.Source);
    }

    // ---- 身份字段与解析状态 ----

    /// <summary>
    /// 保护的行为:副本号、路径、难度、房间数、超时标记、两段解析状态、被带走的击杀数原样带出且互不串位;
    /// 解析状态不是"成功"的那一段,即使载荷对象还在也不被读取(奖励失败时没有经验、金币、旗标)。
    /// </summary>
    [Fact]
    public void Identity_fields_and_parse_states_pass_straight_through_and_unsuccessful_segments_are_ignored()
    {
        var result = Result(45000, questMaze: 1, entries: new[] { new OnlineSettlementBestTime(SelfUid, 42000, true) });
        OnlineSettlementRewardData reward = Kit.Reward(expValues: new[] { 1800, 350 }, rewards: new[] { Kit.Slot(2, 640, 9821, 2) });

        var data = new OnlineDungeonSettlementData(
            DungeonId, "dungeon/test/b2pass.dgn", 3, 14, 15, 16,
            OnlineSettlementParseState.Succeeded, result,
            OnlineSettlementParseState.Failed, reward,
            isTimedOut: true);
        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(data);

        Assert.Equal(DungeonId, s.DungeonId);
        Assert.Equal("dungeon/test/b2pass.dgn", s.DungeonPath);
        Assert.Equal(3, s.Difficulty);
        Assert.Equal(16, s.RoomsEntered);
        Assert.True(s.IsTimedOut);
        Assert.Equal(OnlineSettlementParseState.Succeeded, s.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Failed, s.RewardParseState);
        Assert.Equal(15, s.DroppedKills);
        Assert.Equal(14, s.Kills.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, s.Kills.Confidence);
        Assert.Equal(SettlementFieldSource.Server, s.Kills.Source);

        // 通关结果段成功:用时、任务迷宫、个人最佳(只有一条记录)都有
        Assert.Equal(45000, s.ClearTimeMs.Value);
        Assert.True(s.IsQuestMaze.Value);
        Assert.Equal(42000, s.PersonalBestTimeMs.Value);
        Assert.True(s.IsNewRecord.Value);

        // 奖励段失败:载荷对象在也不读
        Assert.False(s.BaseExp.HasValue);
        Assert.False(s.RankBonusExp.HasValue);
        Assert.Equal(0, s.ConfidentExpGrant);
        Assert.False(s.RewardGold.HasValue);
        Assert.Empty(s.Drops);
        Assert.False(s.IsReturnUserParty.HasValue);
        Assert.False(s.HasDeadUserPenalty.HasValue);
        Assert.False(s.UnknownExpSlot2.HasValue);
        Assert.False(s.UnknownReceiverValue.HasValue);
        Assert.False(s.UnknownPercent.HasValue);
        Assert.False(s.UnknownIsEventCharacParty.HasValue);
        Assert.False(s.UnknownPremiumRewardActiveCount.HasValue);

        // 反过来:通关结果段"不适用"(对象在也不读),奖励段成功
        var data2 = new OnlineDungeonSettlementData(
            DungeonId, null, 0, 2, 0, 1,
            OnlineSettlementParseState.NotApplicable, result,
            OnlineSettlementParseState.Succeeded, reward);
        OnlineSettlementSummary s2 = OnlineSettlementSummaryBuilder.Build(data2);
        Assert.Equal(string.Empty, s2.DungeonPath);
        Assert.False(s2.IsTimedOut);
        Assert.False(s2.ClearTimeMs.HasValue);
        Assert.False(s2.ServerRankValue.HasValue);
        Assert.False(s2.IsQuestMaze.HasValue);
        Assert.False(s2.PersonalBestTimeMs.HasValue);
        Assert.False(s2.IsNewRecord.HasValue);
        Assert.Equal(1800, s2.BaseExp.Value);
        Assert.Equal(350, s2.RankBonusExp.Value);
        Assert.Equal(2150, s2.ConfidentExpGrant);
        Assert.Equal(640, s2.RewardGold.Value);
    }

    // ---- 通关用时 ----

    public static IEnumerable<object[]> ClearRows()
    {
        SettlementFieldConfidence inf = SettlementFieldConfidence.Inferred;
        SettlementFieldConfidence obs = SettlementFieldConfidence.Observed;
        SettlementFieldConfidence unk = SettlementFieldConfidence.Unknown;
        SettlementFieldSource srv = SettlementFieldSource.Server;
        SettlementFieldSource loc = SettlementFieldSource.Local;
        SettlementFieldSource none = SettlementFieldSource.None;
        //                          有最佳块 服务端用时 本地用时    有值   取值   把握  来源  差异
        yield return new object[] { true, 45000, 30000, true, 45000, inf, srv, true };   // 服务端优先,与本地不同 → 记差异
        yield return new object[] { true, 45000, 45000, true, 45000, inf, srv, false };  // 相同 → 无差异
        yield return new object[] { true, 45000, 0, true, 45000, inf, srv, false };      // 本地用时 0 不参与对账
        yield return new object[] { true, 45000, -1, true, 45000, inf, srv, false };     // 本地用时为负不参与对账
        yield return new object[] { true, 45000, null, true, 45000, inf, srv, false };   // 没有本地战绩
        yield return new object[] { true, 0, 5000, true, 0, inf, srv, true };            // 服务端用时 0 仍算有效(>= 0)
        yield return new object[] { true, 1, 1, true, 1, inf, srv, false };
        yield return new object[] { true, -1, 5000, true, 5000, obs, loc, false };       // 服务端为负:退回本地
        yield return new object[] { true, -1, null, false, 0, unk, none, false };        // 都没有 → 缺失
        yield return new object[] { false, 45000, 5000, true, 5000, obs, loc, false };   // 没有最佳块:服务端用时不可信,用本地
        yield return new object[] { false, 0, 0, false, 0, unk, none, false };           // 本地 0 不是有效用时
        yield return new object[] { false, 0, -3, false, 0, unk, none, false };
        yield return new object[] { false, 0, 1, true, 1, obs, loc, false };             // 本地 1 是有效用时
    }

    /// <summary>
    /// 保护的行为:通关用时——有最佳块且服务端用时 ≥ 0 时以服务端为准(推断/服务端),与本地正数用时不同才记 clear_time 差异;
    /// 否则本地用时为正才采用(观察/本地);都没有则缺失。
    /// </summary>
    [Theory]
    [MemberData(nameof(ClearRows))]
    public void Clear_time_prefers_a_valid_server_value_and_falls_back_to_the_local_clock(
        bool block, int serverClear, int? localClear, bool has, int value, SettlementFieldConfidence confidence, SettlementFieldSource source, bool diff)
    {
        RunStats? stats = localClear.HasValue ? new RunStats(localClear.Value, 0, 0, 0, 0, 0) : (RunStats?)null;
        var local = new OnlineSettlementLocalContext { LocalStats = stats };

        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(serverClear, block)), local);

        Assert.Equal(has, s.ClearTimeMs.HasValue);
        Assert.Equal(value, s.ClearTimeMs.Value);
        Assert.Equal(confidence, s.ClearTimeMs.Confidence);
        Assert.Equal(source, s.ClearTimeMs.Source);
        Assert.Equal(diff ? new[] { "clear_time" } : Array.Empty<string>(), s.DiffFields);
    }

    // ---- 个人最佳、新纪录、任务迷宫 ----

    /// <summary>
    /// 保护的行为:有最佳块时任务迷宫旗标(恰为 1 才是)总有;个人最佳与新纪录按本人编号匹配(没有编号时只在恰一条记录时取它),
    /// 匹配不到则缺失;个人最佳 ≥ 0 才有值(0 有效),为负时只有新纪录;没有最佳块时三者全缺。
    /// </summary>
    [Fact]
    public void Personal_best_and_new_record_are_matched_by_the_self_id_and_the_quest_maze_flag_is_exactly_one()
    {
        var self = new OnlineSettlementBestTime(SelfUid, 42000, true);
        var other = new OnlineSettlementBestTime(OtherUid, 39000, false);

        // 命中第二条(本人)
        OnlineSettlementSummary hit = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(null, Result(45000, entries: new[] { other, self })),
            new OnlineSettlementLocalContext { SelfUniqueId = SelfUid });
        Assert.True(hit.PersonalBestTimeMs.HasValue);
        Assert.Equal(42000, hit.PersonalBestTimeMs.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, hit.PersonalBestTimeMs.Confidence);
        Assert.Equal(SettlementFieldSource.Server, hit.PersonalBestTimeMs.Source);
        Assert.True(hit.IsNewRecord.HasValue);
        Assert.True(hit.IsNewRecord.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, hit.IsNewRecord.Confidence);
        Assert.Equal(SettlementFieldSource.Server, hit.IsNewRecord.Source);
        Assert.True(hit.IsQuestMaze.HasValue);
        Assert.False(hit.IsQuestMaze.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, hit.IsQuestMaze.Confidence);
        Assert.Equal(SettlementFieldSource.Server, hit.IsQuestMaze.Source);

        // 命中别人那一条(编号换成另一个人):取到 39000,不是新纪录
        OnlineSettlementSummary hitOther = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(null, Result(45000, entries: new[] { other, self })),
            new OnlineSettlementLocalContext { SelfUniqueId = OtherUid });
        Assert.Equal(39000, hitOther.PersonalBestTimeMs.Value);
        Assert.False(hitOther.IsNewRecord.Value);

        // 编号对不上任何一条:个人最佳与新纪录缺失,任务迷宫旗标仍在
        OnlineSettlementSummary miss = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(null, Result(45000, entries: new[] { other, self })),
            new OnlineSettlementLocalContext { SelfUniqueId = 1234 });
        Assert.False(miss.PersonalBestTimeMs.HasValue);
        Assert.False(miss.IsNewRecord.HasValue);
        Assert.True(miss.IsQuestMaze.HasValue);

        // 没有本人编号:恰一条记录时取它;两条时不瞎猜;没有记录时都缺
        OnlineSettlementSummary single = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(45000, entries: new[] { self })));
        Assert.Equal(42000, single.PersonalBestTimeMs.Value);
        Assert.True(single.IsNewRecord.Value);
        OnlineSettlementSummary two = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(45000, entries: new[] { other, self })));
        Assert.False(two.PersonalBestTimeMs.HasValue);
        Assert.False(two.IsNewRecord.HasValue);
        Assert.True(two.IsQuestMaze.HasValue);
        OnlineSettlementSummary none = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(45000)));
        Assert.False(none.PersonalBestTimeMs.HasValue);
        Assert.False(none.IsNewRecord.HasValue);
        Assert.True(none.IsQuestMaze.HasValue);

        // 个人最佳的边界:0 有效;负数不显示用时但新纪录旗标仍在
        OnlineSettlementSummary zero = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(45000, entries: new[] { new OnlineSettlementBestTime(SelfUid, 0, false) })));
        Assert.True(zero.PersonalBestTimeMs.HasValue);
        Assert.Equal(0, zero.PersonalBestTimeMs.Value);
        Assert.False(zero.IsNewRecord.Value);
        OnlineSettlementSummary negative = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(45000, entries: new[] { new OnlineSettlementBestTime(SelfUid, -1, true) })));
        Assert.False(negative.PersonalBestTimeMs.HasValue);
        Assert.True(negative.IsNewRecord.HasValue);
        Assert.True(negative.IsNewRecord.Value);

        // 任务迷宫旗标:恰为 1 才是
        Assert.True(OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(1, questMaze: 1))).IsQuestMaze.Value);
        Assert.False(OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(1, questMaze: 0))).IsQuestMaze.Value);
        Assert.False(OnlineSettlementSummaryBuilder.Build(Kit.Data(null, Result(1, questMaze: 2))).IsQuestMaze.Value);

        // 没有最佳块:三者全缺(哪怕记录和旗标在)
        OnlineSettlementSummary noBlock = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(null, Result(45000, block: false, questMaze: 1, entries: new[] { self })),
            new OnlineSettlementLocalContext { SelfUniqueId = SelfUid });
        Assert.False(noBlock.PersonalBestTimeMs.HasValue);
        Assert.False(noBlock.IsNewRecord.HasValue);
        Assert.False(noBlock.IsQuestMaze.HasValue);
    }

    // ---- 击杀数 ----

    /// <summary>保护的行为:击杀数总以联网上报数为准(观察/服务端);本地击杀数与之不同才记 kills 差异,没有本地战绩不对账。</summary>
    [Theory]
    [InlineData(7, 5, true)]
    [InlineData(7, 7, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, 3, true)]
    [InlineData(5, 0, true)]
    public void Kills_follow_the_reported_count_and_differences_are_recorded(int reported, int localKills, bool diff)
    {
        var local = new OnlineSettlementLocalContext { LocalStats = new RunStats(0, localKills, 0, 0, 0, 0) };
        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, null, reported), local);

        Assert.True(s.Kills.HasValue);
        Assert.Equal(reported, s.Kills.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, s.Kills.Confidence);
        Assert.Equal(SettlementFieldSource.Server, s.Kills.Source);
        Assert.Equal(diff ? new[] { "kills" } : Array.Empty<string>(), s.DiffFields);

        // 没有本地战绩:不对账
        OnlineSettlementSummary noLocal = OnlineSettlementSummaryBuilder.Build(Kit.Data(null, null, reported));
        Assert.Equal(reported, noLocal.Kills.Value);
        Assert.Empty(noLocal.DiffFields);
    }

    // ---- 经验对账与本地回退 ----

    public static IEnumerable<object[]> ExpDiffRows()
    {
        yield return new object[] { new[] { 1500, 300 }, 1500L, 300L, "" };
        yield return new object[] { new[] { 1500, 300 }, 1400L, 300L, "base_exp" };
        yield return new object[] { new[] { 1500, 300 }, 1500L, 250L, "rank_bonus_exp" };
        yield return new object[] { new[] { 1500, 300 }, 1400L, 250L, "base_exp,rank_bonus_exp" };
        yield return new object[] { new[] { 1500 }, 1500L, 40L, "" };            // 服务端没有评分加成那一槽:不对账
        yield return new object[] { new[] { 1500 }, 1400L, 40L, "base_exp" };
        yield return new object[] { new[] { 0, 0 }, 0L, 0L, "" };
        yield return new object[] { new[] { 0, 0 }, 1L, 0L, "base_exp" };
        yield return new object[] { new[] { 0, 5 }, 0L, 5L, "" };
        yield return new object[] { new[] { 0, 5 }, 0L, 4L, "rank_bonus_exp" };
    }

    /// <summary>保护的行为:服务端经验与本地结算结果不同才依次记 base_exp、rank_bonus_exp 差异;服务端缺那一槽不对账;没有本地结算结果不对账。</summary>
    [Theory]
    [MemberData(nameof(ExpDiffRows))]
    public void Exp_differences_are_recorded_per_slot_in_order(int[] serverExp, long localBase, long localRank, string expectedDiffs)
    {
        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(expValues: serverExp)),
            Kit.LocalWith(Kit.LocalResult(localBase, localRank)));
        Assert.Equal(expectedDiffs, string.Join(",", s.DiffFields));

        // 服务端为准:字段值是服务端的,不是本地的
        Assert.Equal(serverExp[0], s.BaseExp.Value);
        Assert.Equal(SettlementFieldSource.Server, s.BaseExp.Source);

        // 没有本地结算结果:永不对账
        Assert.Empty(OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(expValues: serverExp))).DiffFields);
    }

    /// <summary>
    /// 保护的行为:没有服务端经验块(奖励段缺失、或经验表为空)时退回本地结算结果:基础经验与评分加成各自为正才显示(观察/本地),
    /// 大数夹进 int 范围;本地值不进有把握增量,也不产生差异。
    /// </summary>
    [Theory]
    [InlineData(700L, 0L, true, 700, false, 0)]
    [InlineData(700L, 1L, true, 700, true, 1)]
    [InlineData(0L, 5L, false, 0, true, 5)]
    [InlineData(-3L, -4L, false, 0, false, 0)]
    [InlineData(1L, 2L, true, 1, true, 2)]
    [InlineData(2147483646L, 2147483647L, true, 2147483646, true, 2147483647)]
    [InlineData(2147483648L, 4294967295L, true, 2147483647, true, 2147483647)]
    public void Local_exp_values_are_used_only_without_a_server_exp_table(long localBase, long localRank, bool baseHas, int baseValue, bool rankHas, int rankValue)
    {
        DungeonResult localResult = Kit.LocalResult(localBase, localRank);

        foreach (OnlineSettlementRewardData reward in new OnlineSettlementRewardData[] { null, Kit.Reward(expValues: Array.Empty<int>()) })
        {
            OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(Kit.Data(reward), Kit.LocalWith(localResult));

            Assert.Equal(baseHas, s.BaseExp.HasValue);
            Assert.Equal(baseValue, s.BaseExp.Value);
            Assert.Equal(rankHas, s.RankBonusExp.HasValue);
            Assert.Equal(rankValue, s.RankBonusExp.Value);
            if (baseHas)
            {
                Assert.Equal(SettlementFieldConfidence.Observed, s.BaseExp.Confidence);
                Assert.Equal(SettlementFieldSource.Local, s.BaseExp.Source);
            }
            if (rankHas)
            {
                Assert.Equal(SettlementFieldConfidence.Observed, s.RankBonusExp.Confidence);
                Assert.Equal(SettlementFieldSource.Local, s.RankBonusExp.Source);
            }
            Assert.Equal(0, s.ConfidentExpGrant);
            Assert.Empty(s.DiffFields);
        }

        // 有服务端经验块时本地值被忽略
        OnlineSettlementSummary server = OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(expValues: new[] { 11, 22 })), Kit.LocalWith(localResult));
        Assert.Equal(11, server.BaseExp.Value);
        Assert.Equal(22, server.RankBonusExp.Value);
        Assert.Equal(SettlementFieldSource.Server, server.BaseExp.Source);
        Assert.Equal(SettlementFieldSource.Server, server.RankBonusExp.Source);
    }

    // ---- 奖励旗标与未知字段 ----

    /// <summary>
    /// 保护的行为:奖励段的旗标——回归队伍(观察/服务端)、死亡惩罚(推断/服务端),以及五个未知字段(接收值、百分比、活动角色队、
    /// 额外奖励有效数)按"未知/服务端"记录且永不显示;额外奖励有效数只数状态非 0 的槽。奖励段缺失时全部缺失。
    /// </summary>
    [Fact]
    public void Reward_flags_are_recorded_and_the_unknown_fields_never_display()
    {
        var reward = new OnlineSettlementRewardData(
            expValues: new[] { 1800, 350 },
            expKind: 1,
            questItems: null,
            rewards: null,
            receiverValue: 77777,
            reservedBytes: null,
            premiumRewards: new[] { Kit.Slot(2, 1), Kit.Slot(0, 2), Kit.Slot(1, 3), Kit.Slot(3, 4) },
            percent: 88888,
            isReturnUserParty: true,
            isEventCharacParty: true,
            hasDeadUserPenalty: true);
        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(Kit.Data(reward));

        Assert.True(s.IsReturnUserParty.HasValue);
        Assert.True(s.IsReturnUserParty.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, s.IsReturnUserParty.Confidence);
        Assert.Equal(SettlementFieldSource.Server, s.IsReturnUserParty.Source);
        Assert.True(s.HasDeadUserPenalty.HasValue);
        Assert.True(s.HasDeadUserPenalty.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, s.HasDeadUserPenalty.Confidence);
        Assert.Equal(SettlementFieldSource.Server, s.HasDeadUserPenalty.Source);

        Assert.Equal(77777, s.UnknownReceiverValue.Value);
        Assert.Equal(88888, s.UnknownPercent.Value);
        Assert.True(s.UnknownIsEventCharacParty.Value);
        Assert.Equal(3, s.UnknownPremiumRewardActiveCount.Value); // 状态 2、1、3 三个非 0 槽
        foreach (SettlementFieldConfidence confidence in new[]
        {
            s.UnknownReceiverValue.Confidence, s.UnknownPercent.Confidence, s.UnknownIsEventCharacParty.Confidence,
            s.UnknownPremiumRewardActiveCount.Confidence, s.UnknownExpSlot2.Confidence,
        })
        {
            Assert.Equal(SettlementFieldConfidence.Unknown, confidence);
        }
        Assert.True(s.UnknownReceiverValue.HasValue && s.UnknownPercent.HasValue && s.UnknownIsEventCharacParty.HasValue && s.UnknownPremiumRewardActiveCount.HasValue);
        Assert.False(s.UnknownReceiverValue.ShouldDisplay);
        Assert.False(s.UnknownPercent.ShouldDisplay);
        Assert.False(s.UnknownIsEventCharacParty.ShouldDisplay);
        Assert.False(s.UnknownPremiumRewardActiveCount.ShouldDisplay);
        Assert.Equal(SettlementFieldSource.Server, s.UnknownReceiverValue.Source);
        Assert.Equal(SettlementFieldSource.Server, s.UnknownPercent.Source);
        Assert.Equal(SettlementFieldSource.Server, s.UnknownIsEventCharacParty.Source);
        Assert.Equal(SettlementFieldSource.Server, s.UnknownPremiumRewardActiveCount.Source);

        // 全是缺省:旗标有值但为假,额外奖励有效数为 0(仍是"有值")
        OnlineSettlementSummary bare = OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(expValues: new[] { 1 })));
        Assert.True(bare.IsReturnUserParty.HasValue);
        Assert.False(bare.IsReturnUserParty.Value);
        Assert.True(bare.HasDeadUserPenalty.HasValue);
        Assert.False(bare.HasDeadUserPenalty.Value);
        Assert.True(bare.UnknownPremiumRewardActiveCount.HasValue);
        Assert.Equal(0, bare.UnknownPremiumRewardActiveCount.Value);
        Assert.Equal(0, bare.UnknownReceiverValue.Value);
        Assert.Equal(0, bare.UnknownPercent.Value);
        Assert.False(bare.UnknownIsEventCharacParty.Value);

        // 奖励段缺失:全部缺失
        OnlineSettlementSummary absent = OnlineSettlementSummaryBuilder.Build(Kit.Data(null));
        Assert.False(absent.IsReturnUserParty.HasValue);
        Assert.False(absent.HasDeadUserPenalty.HasValue);
        Assert.False(absent.UnknownReceiverValue.HasValue);
        Assert.False(absent.UnknownPercent.HasValue);
        Assert.False(absent.UnknownIsEventCharacParty.HasValue);
        Assert.False(absent.UnknownPremiumRewardActiveCount.HasValue);
        Assert.False(absent.UnknownExpSlot2.HasValue);
        Assert.False(absent.RewardGold.HasValue);
        Assert.Empty(absent.Drops);
    }

    // ---- 差异清单与金币对账 ----

    /// <summary>保护的行为:五个对账字段同时不一致时,差异清单依次是 clear_time、kills、base_exp、rank_bonus_exp、reward_gold。</summary>
    [Fact]
    public void All_five_differences_are_listed_in_a_fixed_order()
    {
        var local = new OnlineSettlementLocalContext
        {
            LocalStats = new RunStats(clearMs: 30000, kills: 5, comboKills: 5, maxCombo: 12, hitsTaken: 0, revivesUsed: 0),
            LocalResult = Kit.LocalResult(expBase: 1000, expRankBonus: 100),
            LocalGoldEarned = 123,
        };
        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(expValues: new[] { 1800, 350 }, rewards: new[] { Kit.Slot(2, 640) }), Result(45000), reportedKills: 7),
            local);

        Assert.Equal(new[] { "clear_time", "kills", "base_exp", "rank_bonus_exp", "reward_gold" }, s.DiffFields);
        Assert.Equal(640, s.RewardGold.Value);
    }

    /// <summary>保护的行为:金币只在本地拾取金额为正且与选中的奖励槽金额不同时才记 reward_gold 差异(0、负数、相同、没有本地上下文都不记)。</summary>
    [Theory]
    [InlineData(0L, false)]
    [InlineData(-5L, false)]
    [InlineData(640L, false)]
    [InlineData(639L, true)]
    [InlineData(641L, true)]
    [InlineData(1L, true)]
    public void Reward_gold_difference_needs_a_positive_local_amount_that_differs(long localGold, bool diff)
    {
        var slots = new[] { Kit.Slot(0, 100), Kit.Slot(2, 640) };
        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(rewards: slots)),
            new OnlineSettlementLocalContext { LocalGoldEarned = localGold });

        Assert.Equal(640, s.RewardGold.Value);
        Assert.Equal(diff ? new[] { "reward_gold" } : Array.Empty<string>(), s.DiffFields);

        // 没有本地上下文:不对账
        OnlineSettlementSummary noLocal = OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(rewards: slots)));
        Assert.Equal(640, noLocal.RewardGold.Value);
        Assert.Empty(noLocal.DiffFields);
    }

    // ---- 掉落清单 ----

    /// <summary>保护的行为:掉落清单先任务清图物品、再奖励槽物品(只有状态 2 的槽有物品),数量缺省 1,任务标记与名字解析逐条对号。</summary>
    [Fact]
    public void Drops_follow_quest_items_then_reward_slot_items_with_flags_counts_and_names()
    {
        var quest = new[] { Kit.Quest(9811, 4), Kit.Quest(9812, 0), Kit.Quest(9813, -2) };
        var rewards = new[]
        {
            Kit.Slot(2, 10, itemId: 9821, itemData: 0),
            Kit.Slot(1, 20, itemId: 9822, itemData: 7),
            Kit.Slot(2, 30, itemId: 9823, itemData: -4),
            Kit.Slot(2, 40, itemId: 9824, itemData: 1),
            Kit.Slot(2, 50, itemId: 9825, itemData: 5),
            Kit.Slot(0, 60, itemId: 9826, itemData: 3),
        };

        OnlineSettlementSummary s = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(questItems: quest, rewards: rewards)),
            local: null,
            resolveItemName: id => id == 9813 ? null : "合成" + id);

        int[] ids = { 9811, 9812, 9813, 9821, 9823, 9824, 9825 };
        int[] counts = { 4, 1, 1, 1, 1, 1, 5 };
        bool[] isQuest = { true, true, true, false, false, false, false };
        Assert.Equal(7, s.Drops.Count);
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(ids[i], s.Drops[i].ItemId);
            Assert.Equal(counts[i], s.Drops[i].Count);
            Assert.Equal(isQuest[i], s.Drops[i].IsQuestItem);
            Assert.Equal(ids[i] != 9813, s.Drops[i].NameResolved);
        }
        Assert.Equal("合成9811 x4", s.Drops[0].DisplayLabel(hasFonts: true));
        Assert.Equal("未知道具", s.Drops[2].DisplayLabel(hasFonts: true));
        Assert.Equal("UNKNOWN ITEM", s.Drops[2].DisplayLabel(hasFonts: false));
        Assert.Equal("合成9825 x5", s.Drops[6].DisplayLabel(hasFonts: false));
    }
}
