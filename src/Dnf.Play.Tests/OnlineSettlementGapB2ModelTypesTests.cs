using Dnf.Data;
using Dnf.Game;
using Kit = Dnf.Play.Tests.OnlineSettlementGapBKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 结算数据载体与有把握经验刷新的补测(区域 B,第二轮):
/// 带来源与把握程度的字段结构(工厂、默认值、可显示判定)、枚举的数值、摘要十字段以上构造器逐字段对号与缺省值、
/// 已发经验记账,以及 <see cref="OnlineSettlementHeroHelpers.ApplyConfidentExp"/> 的各条守卫分支。全部合成数据。
/// </summary>
public sealed class OnlineSettlementGapB2ModelTypesTests
{
    // ---- 带来源与把握程度的字段 ----

    /// <summary>保护的行为:字段的三个工厂各自带对"有值、把握程度、来源";只有"有值且不是未知"才可显示;缺失字段是未知/无来源/不可显示。</summary>
    [Fact]
    public void Field_factories_set_the_confidence_and_source_and_only_known_values_display()
    {
        var missing = OnlineSettlementField<int>.Missing();
        Assert.False(missing.HasValue);
        Assert.Equal(0, missing.Value);
        Assert.Equal(SettlementFieldConfidence.Unknown, missing.Confidence);
        Assert.Equal(SettlementFieldSource.None, missing.Source);
        Assert.False(missing.ShouldDisplay);

        var observed = OnlineSettlementField<int>.Observed(5, SettlementFieldSource.Local);
        Assert.True(observed.HasValue);
        Assert.Equal(5, observed.Value);
        Assert.Equal(SettlementFieldConfidence.Observed, observed.Confidence);
        Assert.Equal(SettlementFieldSource.Local, observed.Source);
        Assert.True(observed.ShouldDisplay);

        var inferred = OnlineSettlementField<int>.Inferred(6, SettlementFieldSource.Server);
        Assert.True(inferred.HasValue);
        Assert.Equal(6, inferred.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, inferred.Confidence);
        Assert.Equal(SettlementFieldSource.Server, inferred.Source);
        Assert.True(inferred.ShouldDisplay);

        var unknown = OnlineSettlementField<int>.Unknown(7, SettlementFieldSource.Server);
        Assert.True(unknown.HasValue);
        Assert.Equal(7, unknown.Value);
        Assert.Equal(SettlementFieldConfidence.Unknown, unknown.Confidence);
        Assert.Equal(SettlementFieldSource.Server, unknown.Source);
        Assert.False(unknown.ShouldDisplay);

        // 没有值时,哪怕把握程度是"观察"也不显示
        var valueless = new OnlineSettlementField<int>(false, 9, SettlementFieldConfidence.Observed, SettlementFieldSource.Placeholder);
        Assert.False(valueless.ShouldDisplay);
        Assert.Equal(SettlementFieldSource.Placeholder, valueless.Source);

        // 默认结构等同缺失:未知、无来源、不显示
        OnlineSettlementField<bool> blank = default;
        Assert.False(blank.HasValue);
        Assert.False(blank.Value);
        Assert.Equal(SettlementFieldConfidence.Unknown, blank.Confidence);
        Assert.Equal(SettlementFieldSource.None, blank.Source);
        Assert.False(blank.ShouldDisplay);

        // 字节与布尔两种实例化同样成立
        Assert.Equal((byte)200, OnlineSettlementField<byte>.Unknown(200, SettlementFieldSource.Server).Value);
        Assert.True(OnlineSettlementField<bool>.Observed(true, SettlementFieldSource.Server).Value);
        Assert.False(OnlineSettlementField<bool>.Observed(false, SettlementFieldSource.Server).Value);
    }

    /// <summary>保护的行为:几个枚举的数值固定(默认值 0 恰是"未知/无来源/无提示",编号不撞车)。</summary>
    [Fact]
    public void Settlement_enums_keep_their_numeric_values()
    {
        Assert.Equal(0, (int)SettlementFieldConfidence.Unknown);
        Assert.Equal(1, (int)SettlementFieldConfidence.Inferred);
        Assert.Equal(2, (int)SettlementFieldConfidence.Observed);
        Assert.Equal(3, Enum.GetValues<SettlementFieldConfidence>().Length - 0);

        Assert.Equal(0, (int)SettlementFieldSource.None);
        Assert.Equal(1, (int)SettlementFieldSource.Server);
        Assert.Equal(2, (int)SettlementFieldSource.Local);
        Assert.Equal(3, (int)SettlementFieldSource.Placeholder);
        Assert.Equal(4, Enum.GetValues<SettlementFieldSource>().Length);

        // 提示种类的编号:手数 0..13,共 14 种,没有重号
        var expected = new Dictionary<OnlineSettlementNoticeKind, int>
        {
            { OnlineSettlementNoticeKind.None, 0 },
            { OnlineSettlementNoticeKind.Advancing, 1 },
            { OnlineSettlementNoticeKind.CardSelectSkipped, 2 },
            { OnlineSettlementNoticeKind.AdvanceNoReply, 3 },
            { OnlineSettlementNoticeKind.WaitingReturn, 4 },
            { OnlineSettlementNoticeKind.SettlementTimeout, 5 },
            { OnlineSettlementNoticeKind.EplpRejectedNotLeader, 6 },
            { OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen, 7 },
            { OnlineSettlementNoticeKind.EplpRejectedInvalidCommand, 8 },
            { OnlineSettlementNoticeKind.EplpRejectedOther, 9 },
            { OnlineSettlementNoticeKind.EplpTimeout, 10 },
            { OnlineSettlementNoticeKind.ServerReturnedToTown, 11 },
            { OnlineSettlementNoticeKind.SendFailed, 12 },
            { OnlineSettlementNoticeKind.Disconnected, 13 },
        };
        foreach (OnlineSettlementNoticeKind kind in Enum.GetValues<OnlineSettlementNoticeKind>())
        {
            Assert.Equal(expected[kind], (int)kind);
        }
        Assert.Equal(expected.Count, Enum.GetValues<OnlineSettlementNoticeKind>().Length);
        Assert.Equal(expected.Count, Enum.GetValues<OnlineSettlementNoticeKind>().Select(k => (int)k).Distinct().Count());
        Assert.Equal(OnlineSettlementNoticeKind.None, default(OnlineSettlementNoticeKind));
    }

    // ---- 摘要构造器 ----

    private static OnlineSettlementSummary Direct(
        string dungeonPath = "dungeon/test/direct.dgn",
        string rankLetter = "S",
        IReadOnlyList<OnlineSettlementExpLine> named = null,
        IReadOnlyList<OnlineSettlementDropItem> drops = null,
        IReadOnlyList<string> diffs = null,
        int confidentExpGrant = 7)
    {
        return new OnlineSettlementSummary(
            dungeonId: 11,
            dungeonPath: dungeonPath,
            difficulty: 2,
            roomsEntered: 3,
            isTimedOut: true,
            resultParseState: OnlineSettlementParseState.Failed,
            rewardParseState: OnlineSettlementParseState.Succeeded,
            hasRank: true,
            rankFrame: 4,
            rankLetter: rankLetter,
            rankSource: SettlementFieldSource.Local,
            serverRankValue: OnlineSettlementField<byte>.Unknown(5, SettlementFieldSource.Server),
            clearTimeMs: OnlineSettlementField<int>.Inferred(61000, SettlementFieldSource.Server),
            personalBestTimeMs: OnlineSettlementField<int>.Observed(62000, SettlementFieldSource.Local),
            isNewRecord: OnlineSettlementField<bool>.Observed(true, SettlementFieldSource.Server),
            isQuestMaze: OnlineSettlementField<bool>.Inferred(false, SettlementFieldSource.Placeholder),
            kills: OnlineSettlementField<int>.Observed(13, SettlementFieldSource.Server),
            droppedKills: 14,
            baseExp: OnlineSettlementField<int>.Inferred(15, SettlementFieldSource.Server),
            rankBonusExp: OnlineSettlementField<int>.Inferred(16, SettlementFieldSource.Local),
            namedBonusExpLines: named,
            rewardGold: OnlineSettlementField<int>.Inferred(17, SettlementFieldSource.Server),
            drops: drops,
            isReturnUserParty: OnlineSettlementField<bool>.Observed(true, SettlementFieldSource.Server),
            hasDeadUserPenalty: OnlineSettlementField<bool>.Inferred(true, SettlementFieldSource.Local),
            unknownExpSlot2: OnlineSettlementField<int>.Unknown(18, SettlementFieldSource.Server),
            unknownReceiverValue: OnlineSettlementField<int>.Unknown(19, SettlementFieldSource.Server),
            unknownPercent: OnlineSettlementField<int>.Unknown(20, SettlementFieldSource.Server),
            unknownIsEventCharacParty: OnlineSettlementField<bool>.Unknown(true, SettlementFieldSource.Server),
            unknownPremiumRewardActiveCount: OnlineSettlementField<int>.Unknown(21, SettlementFieldSource.Server),
            diffFields: diffs,
            confidentExpGrant: confidentExpGrant);
    }

    /// <summary>保护的行为:摘要构造器把每个参数记到同名属性上(取互不相同的哨兵值,串位会暴露);列表原样引用。</summary>
    [Fact]
    public void Summary_constructor_stores_every_argument_in_its_own_property()
    {
        var named = new[] { new OnlineSettlementExpLine(3, "avatar", 8, SettlementFieldConfidence.Inferred) };
        var drops = new[] { new OnlineSettlementDropItem(9, 2, "合成物", true) };
        var diffs = new[] { "kills", "reward_gold" };

        OnlineSettlementSummary s = Direct(named: named, drops: drops, diffs: diffs);

        Assert.Equal(11, s.DungeonId);
        Assert.Equal("dungeon/test/direct.dgn", s.DungeonPath);
        Assert.Equal(2, s.Difficulty);
        Assert.Equal(3, s.RoomsEntered);
        Assert.True(s.IsTimedOut);
        Assert.Equal(OnlineSettlementParseState.Failed, s.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Succeeded, s.RewardParseState);
        Assert.True(s.HasRank);
        Assert.Equal(4, s.RankFrame);
        Assert.Equal("S", s.RankLetter);
        Assert.Equal(SettlementFieldSource.Local, s.RankSource);
        Assert.Equal((byte)5, s.ServerRankValue.Value);
        Assert.False(s.ServerRankValue.ShouldDisplay);
        Assert.Equal(61000, s.ClearTimeMs.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, s.ClearTimeMs.Confidence);
        Assert.Equal(62000, s.PersonalBestTimeMs.Value);
        Assert.Equal(SettlementFieldSource.Local, s.PersonalBestTimeMs.Source);
        Assert.True(s.IsNewRecord.Value);
        Assert.False(s.IsQuestMaze.Value);
        Assert.Equal(SettlementFieldSource.Placeholder, s.IsQuestMaze.Source);
        Assert.Equal(13, s.Kills.Value);
        Assert.Equal(14, s.DroppedKills);
        Assert.Equal(15, s.BaseExp.Value);
        Assert.Equal(16, s.RankBonusExp.Value);
        Assert.Equal(SettlementFieldSource.Local, s.RankBonusExp.Source);
        Assert.Same(named, s.NamedBonusExpLines);
        Assert.Equal(17, s.RewardGold.Value);
        Assert.Same(drops, s.Drops);
        Assert.True(s.IsReturnUserParty.Value);
        Assert.True(s.HasDeadUserPenalty.Value);
        Assert.Equal(SettlementFieldSource.Local, s.HasDeadUserPenalty.Source);
        Assert.Equal(18, s.UnknownExpSlot2.Value);
        Assert.Equal(19, s.UnknownReceiverValue.Value);
        Assert.Equal(20, s.UnknownPercent.Value);
        Assert.True(s.UnknownIsEventCharacParty.Value);
        Assert.Equal(21, s.UnknownPremiumRewardActiveCount.Value);
        Assert.Same(diffs, s.DiffFields);
        Assert.Equal(7, s.ConfidentExpGrant);

        // 未标记前:没发过经验
        Assert.False(s.ExpApplied);
        Assert.Equal(0, s.ExpAppliedAmount);
        Assert.Equal(0, s.LevelBefore);
        Assert.Equal(0, s.LevelAfter);
        Assert.Equal(0, s.LevelsGained);
    }

    /// <summary>保护的行为:构造器的缺省值——路径缺省空串、评分字母缺省 "F"、三个列表缺省为空表(非 null)、负的有把握增量夹成 0。</summary>
    [Fact]
    public void Summary_constructor_defaults_missing_text_and_lists_and_clamps_negative_grants()
    {
        OnlineSettlementSummary s = Direct(dungeonPath: null, rankLetter: null, named: null, drops: null, diffs: null, confidentExpGrant: -5);
        Assert.Equal(string.Empty, s.DungeonPath);
        Assert.Equal("F", s.RankLetter);
        Assert.NotNull(s.NamedBonusExpLines);
        Assert.Empty(s.NamedBonusExpLines);
        Assert.NotNull(s.Drops);
        Assert.Empty(s.Drops);
        Assert.NotNull(s.DiffFields);
        Assert.Empty(s.DiffFields);
        Assert.Equal(0, s.ConfidentExpGrant);

        Assert.Equal(0, Direct(confidentExpGrant: 0).ConfidentExpGrant);
        Assert.Equal(1, Direct(confidentExpGrant: 1).ConfidentExpGrant);
        Assert.Equal(int.MaxValue, Direct(confidentExpGrant: int.MaxValue).ConfidentExpGrant);
        Assert.Equal(string.Empty, Direct(dungeonPath: string.Empty).DungeonPath);
        Assert.Equal("A", Direct(rankLetter: "A").RankLetter);
    }

    /// <summary>保护的行为:已发经验记账——记下发出的点数与前后等级,点数 ≤ 0 夹成 0,升级数只在等级上升时为差值。</summary>
    [Fact]
    public void Mark_exp_applied_records_amount_levels_and_gained_levels()
    {
        OnlineSettlementSummary s = Direct();
        s.MarkExpApplied(1300, 1, 2);
        Assert.True(s.ExpApplied);
        Assert.Equal(1300, s.ExpAppliedAmount);
        Assert.Equal(1, s.LevelBefore);
        Assert.Equal(2, s.LevelAfter);
        Assert.Equal(1, s.LevelsGained);

        s.MarkExpApplied(5, 4, 9);
        Assert.Equal(5, s.ExpAppliedAmount);
        Assert.Equal(5, s.LevelsGained); // 9 - 4

        s.MarkExpApplied(0, 6, 6);
        Assert.Equal(0, s.ExpAppliedAmount);
        Assert.Equal(0, s.LevelsGained);

        s.MarkExpApplied(-2, 7, 6); // 等级反而下降:升级数不为负
        Assert.Equal(0, s.ExpAppliedAmount);
        Assert.Equal(7, s.LevelBefore);
        Assert.Equal(6, s.LevelAfter);
        Assert.Equal(0, s.LevelsGained);
    }

    // ---- 有把握经验刷新 ----

    private static HeroProgress SyntheticProgress(int level = 1)
    {
        var expTable = new ExpTable(new[] { 1000, 3000, 7000, 15000, 30000 });
        var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50 });
        return new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, level);
    }

    private static OnlineSettlementSummary SummaryWithGrant(int baseExp, int rankBonus)
    {
        return OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(expValues: new[] { baseExp, rankBonus })));
    }

    /// <summary>保护的行为:缺主角进度或缺摘要时什么都不发、返回 0,等级输出沿用当前(没有进度时为 0);不动任何记账。</summary>
    [Fact]
    public void Apply_confident_exp_does_nothing_without_a_progress_or_a_summary()
    {
        OnlineSettlementSummary summary = SummaryWithGrant(1300, 0);

        int noProgress = OnlineSettlementHeroHelpers.ApplyConfidentExp(null, summary, out int before1, out int after1);
        Assert.Equal(0, noProgress);
        Assert.Equal(0, before1);
        Assert.Equal(0, after1);
        Assert.False(summary.ExpApplied);

        HeroProgress progress = SyntheticProgress(level: 1);
        int noSummary = OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, null, out int before2, out int after2);
        Assert.Equal(0, noSummary);
        Assert.Equal(1, before2);
        Assert.Equal(1, after2);
        Assert.Equal(0, progress.Exp);
        Assert.Equal(1, progress.Level);
    }

    /// <summary>保护的行为:有把握增量为 0(没有服务端经验块)时不发经验、不做"已发"标记;增量恰为 1 时照发。</summary>
    [Fact]
    public void Apply_confident_exp_skips_a_zero_grant_and_applies_the_smallest_positive_grant()
    {
        HeroProgress progress = SyntheticProgress(level: 1);
        OnlineSettlementSummary none = OnlineSettlementSummaryBuilder.Build(Kit.Data(null));
        Assert.Equal(0, none.ConfidentExpGrant);

        Assert.Equal(0, OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, none, out int before0, out int after0));
        Assert.Equal(1, before0);
        Assert.Equal(1, after0);
        Assert.False(none.ExpApplied);
        Assert.Equal(0, progress.Exp);

        OnlineSettlementSummary one = SummaryWithGrant(1, 0);
        Assert.Equal(1, OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, one, out int before1, out int after1));
        Assert.Equal(1, before1);
        Assert.Equal(1, after1);
        Assert.True(one.ExpApplied);
        Assert.Equal(1, one.ExpAppliedAmount);
        Assert.Equal(0, one.LevelsGained);
        Assert.Equal(1, progress.Exp);
    }

    /// <summary>
    /// 保护的行为:增量发给主角并升级(1 级升 2 级要 1000 点,发 1100+200=1300),
    /// 记账写下点数与前后等级;再调一次不重复发(返回 0、经验不变、等级输出沿用当前等级)。
    /// </summary>
    [Fact]
    public void Apply_confident_exp_grants_once_records_the_levels_and_refuses_a_second_grant()
    {
        HeroProgress progress = SyntheticProgress(level: 1);
        OnlineSettlementSummary summary = SummaryWithGrant(1100, 200);
        Assert.Equal(1300, summary.ConfidentExpGrant);

        int granted = OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, summary, out int before, out int after);

        Assert.Equal(1300, granted);
        Assert.Equal(1, before);
        Assert.Equal(2, after);
        Assert.Equal(2, progress.Level);
        Assert.Equal(1300, progress.Exp);
        Assert.Equal(0, progress.Gold);
        Assert.True(summary.ExpApplied);
        Assert.Equal(1300, summary.ExpAppliedAmount);
        Assert.Equal(1, summary.LevelBefore);
        Assert.Equal(2, summary.LevelAfter);
        Assert.Equal(1, summary.LevelsGained);

        int again = OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, summary, out int before2, out int after2);
        Assert.Equal(0, again);
        Assert.Equal(2, before2);
        Assert.Equal(2, after2);
        Assert.Equal(1300, progress.Exp);
        Assert.Equal(1300, summary.ExpAppliedAmount);
    }
}
