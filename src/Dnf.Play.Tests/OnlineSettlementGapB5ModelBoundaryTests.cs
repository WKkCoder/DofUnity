using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算中性模型(<see cref="OnlineSettlementModel"/> 所在文件里的各个类型与构造器 <see cref="OnlineSettlementSummaryBuilder"/>)的"数值边界"补测(区域 B 第五轮):
/// 每个"大于 0 / 大于等于 0 / 小于数量 / 恰好等于上限"的判断,在边界两侧都有用例(0 与 1、-1 与 0、数量 - 1 与数量、上限 - 1 与上限)。
/// 全部使用合成数据(物品号 9800 段、金额 100/500 一类的小整数)。
/// </summary>
public sealed class OnlineSettlementGapB5ModelBoundaryTests
{
    // ---------------------------------------------------------------- 辅助

    private static OnlineSettlementSummary BuildReward(OnlineSettlementRewardData reward, OnlineSettlementLocalContext local = null, Func<int, string> resolver = null)
    {
        return OnlineSettlementSummaryBuilder.Build(OnlineSettlementGapBKit.Data(reward), local, resolver);
    }

    private static OnlineSettlementSummary BuildExp(int[] exp, OnlineSettlementLocalContext local = null)
    {
        return BuildReward(OnlineSettlementGapBKit.Reward(expValues: exp), local);
    }

    /// <summary>通关结果段:只关心通关用时、是否有最佳用时块、任务迷宫标记与队员条目。</summary>
    private static OnlineSettlementResultData Result(int clearTime, bool hasBlock, byte questMazeFlag = 0, params OnlineSettlementBestTime[] entries)
    {
        return new OnlineSettlementResultData(rankValue: 0, clearTime: clearTime, timeBonus: 0, baseScore: 0, questMazeFlag: questMazeFlag, hasBestTimeBlock: hasBlock, entries: entries);
    }

    private static OnlineSettlementLocalContext LocalStats(int clearMs = 0, int kills = 0, ushort? self = null)
    {
        return new OnlineSettlementLocalContext
        {
            LocalStats = new RunStats(clearMs: clearMs, kills: kills, comboKills: 0, maxCombo: 0, hitsTaken: 0, revivesUsed: 0),
            SelfUniqueId = self,
        };
    }

    private static OnlineSettlementSummary MakeSummary(int confidentExpGrant)
    {
        return new OnlineSettlementSummary(
            dungeonId: 9801,
            dungeonPath: "dungeon/test/b5.dgn",
            difficulty: 0,
            roomsEntered: 1,
            isTimedOut: false,
            resultParseState: OnlineSettlementParseState.NotApplicable,
            rewardParseState: OnlineSettlementParseState.NotApplicable,
            hasRank: false,
            rankFrame: 0,
            rankLetter: "F",
            rankSource: SettlementFieldSource.None,
            serverRankValue: OnlineSettlementField<byte>.Missing(),
            clearTimeMs: OnlineSettlementField<int>.Missing(),
            personalBestTimeMs: OnlineSettlementField<int>.Missing(),
            isNewRecord: OnlineSettlementField<bool>.Missing(),
            isQuestMaze: OnlineSettlementField<bool>.Missing(),
            kills: OnlineSettlementField<int>.Missing(),
            droppedKills: 0,
            baseExp: OnlineSettlementField<int>.Missing(),
            rankBonusExp: OnlineSettlementField<int>.Missing(),
            namedBonusExpLines: null,
            rewardGold: OnlineSettlementField<int>.Missing(),
            drops: null,
            isReturnUserParty: OnlineSettlementField<bool>.Missing(),
            hasDeadUserPenalty: OnlineSettlementField<bool>.Missing(),
            unknownExpSlot2: OnlineSettlementField<int>.Missing(),
            unknownReceiverValue: OnlineSettlementField<int>.Missing(),
            unknownPercent: OnlineSettlementField<int>.Missing(),
            unknownIsEventCharacParty: OnlineSettlementField<bool>.Missing(),
            unknownPremiumRewardActiveCount: OnlineSettlementField<int>.Missing(),
            diffFields: null,
            confidentExpGrant: confidentExpGrant);
    }

    private static HeroProgress SyntheticProgress(int level = 1)
    {
        var expTable = new ExpTable(new[] { 1000, 3000, 7000, 15000, 30000 });
        var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50 });
        return new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, level);
    }

    // ---------------------------------------------------------------- 掉落物品条目与经验分项

    // 保护的行为:掉落物品的数量至少为 1(0 与负数都当 1,2 及以上原样),
    // 标签只在数量大于 1 时才带 " x数量" 后缀(数量 1 不带、数量 2 带;没有名字时有字体写"未知道具"、没有字体写 "UNKNOWN ITEM")。
    [Theory]
    [InlineData(-3, 1, "名牌")]
    [InlineData(0, 1, "名牌")]
    [InlineData(1, 1, "名牌")]
    [InlineData(2, 2, "名牌 x2")]
    [InlineData(40, 40, "名牌 x40")]
    public void DropItemCountIsAtLeastOneAndTheLabelAddsTheCountOnlyAboveOne(int given, int expectedCount, string expectedLabel)
    {
        var named = new OnlineSettlementDropItem(9801, given, "名牌", isQuestItem: false);
        Assert.Equal(expectedCount, named.Count);
        Assert.Equal(expectedLabel, named.DisplayLabel(hasFonts: true));
        Assert.Equal(expectedLabel, named.DisplayLabel(hasFonts: false));

        var unnamed = new OnlineSettlementDropItem(9801, given, null, isQuestItem: true);
        Assert.False(unnamed.NameResolved);
        Assert.Equal(expectedCount == 1 ? "未知道具" : "未知道具 x" + expectedCount, unnamed.DisplayLabel(hasFonts: true));
        Assert.Equal(expectedCount == 1 ? "UNKNOWN ITEM" : "UNKNOWN ITEM x" + expectedCount, unnamed.DisplayLabel(hasFonts: false));
    }

    // 保护的行为:经验分项只有"把握程度不是未知、且数额大于 0"才显示:数额 1 显示、0 与负数不显示,未知的永不显示;
    // 带来源标记的字段只有"有值且把握程度不是未知"才显示(有值但未知、无值但已知、缺失都不显示)。
    [Theory]
    [InlineData(SettlementFieldConfidence.Inferred, 1, true)]
    [InlineData(SettlementFieldConfidence.Inferred, 2, true)]
    [InlineData(SettlementFieldConfidence.Inferred, 0, false)]
    [InlineData(SettlementFieldConfidence.Inferred, -5, false)]
    [InlineData(SettlementFieldConfidence.Observed, 1, true)]
    [InlineData(SettlementFieldConfidence.Observed, 0, false)]
    [InlineData(SettlementFieldConfidence.Unknown, 5, false)]
    [InlineData(SettlementFieldConfidence.Unknown, 0, false)]
    public void ExpLineIsDisplayedOnlyWhenKnownAndPositive(SettlementFieldConfidence confidence, int amount, bool expected)
    {
        var line = new OnlineSettlementExpLine(4, "event", amount, confidence);
        Assert.Equal(expected, line.ShouldDisplay);
    }

    [Fact]
    public void FieldIsDisplayedOnlyWhenItHasAValueAndIsNotUnknown()
    {
        Assert.True(OnlineSettlementField<int>.Observed(0, SettlementFieldSource.Local).ShouldDisplay);
        Assert.True(OnlineSettlementField<int>.Inferred(5, SettlementFieldSource.Server).ShouldDisplay);
        Assert.False(OnlineSettlementField<int>.Unknown(5, SettlementFieldSource.Server).ShouldDisplay);
        Assert.False(OnlineSettlementField<int>.Missing().ShouldDisplay);
        // 无值但把握程度已知:不显示
        Assert.False(new OnlineSettlementField<int>(false, 0, SettlementFieldConfidence.Observed, SettlementFieldSource.Local).ShouldDisplay);
        Assert.False(new OnlineSettlementField<int>(false, 0, SettlementFieldConfidence.Inferred, SettlementFieldSource.Server).ShouldDisplay);
    }

    // ---------------------------------------------------------------- 摘要自身的夹取

    // 保护的行为:摘要构造器把"有把握的经验增量"夹成非负数(负数与 0 成 0,1 与 2 及以上原样)。
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void SummaryConfidentExpGrantIsClampedToNonNegative(int given, int expected)
    {
        Assert.Equal(expected, MakeSummary(given).ConfidentExpGrant);
    }

    // 保护的行为:升级数 = 升级后等级高出升级前的部分,相等或更低都是 0;标记经验已发放时同时记下前后等级。
    [Theory]
    [InlineData(1, 3, 2)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 2, 0)]
    public void LevelsGainedIsTheRiseOrZero(int before, int after, int expected)
    {
        OnlineSettlementSummary summary = MakeSummary(0);
        Assert.Equal(0, summary.LevelsGained);
        Assert.False(summary.ExpApplied);

        summary.MarkExpApplied(100, before, after);

        Assert.True(summary.ExpApplied);
        Assert.Equal((before, after), (summary.LevelBefore, summary.LevelAfter));
        Assert.Equal(expected, summary.LevelsGained);
    }

    // 保护的行为:标记经验已发放时,已发放数额夹成非负(负数与 0 成 0,1 与 2 原样)。
    [Theory]
    [InlineData(-7, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void MarkExpAppliedClampsTheAppliedAmountToNonNegative(int applied, int expected)
    {
        OnlineSettlementSummary summary = MakeSummary(0);
        summary.MarkExpApplied(applied, 1, 1);

        Assert.True(summary.ExpApplied);
        Assert.Equal(expected, summary.ExpAppliedAmount);
    }

    // ---------------------------------------------------------------- 通关用时与差异记录

    // 保护的行为:服务端通关用时"有最佳用时块且 ≥ 0"才有效(0 有效、-1 无效退回本地、1 有效);
    // 有效时来源是服务端、把握程度是推断;无效时退回本地用时(本地用时 > 0 才有,来源本地、观察)。
    [Theory]
    [InlineData(0, 5000, SettlementFieldSource.Server, 0)]
    [InlineData(1, 5000, SettlementFieldSource.Server, 1)]
    [InlineData(-1, 5000, SettlementFieldSource.Local, 5000)]
    [InlineData(-9, 5000, SettlementFieldSource.Local, 5000)]
    public void ServerClearTimeIsValidFromZeroElseTheLocalTimeIsUsed(int serverClear, int localClearMs, SettlementFieldSource expectedSource, int expectedValue)
    {
        var data = OnlineSettlementGapBKit.Data(null, Result(serverClear, hasBlock: true));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data, LocalStats(clearMs: localClearMs));

        Assert.True(summary.ClearTimeMs.HasValue);
        Assert.Equal(expectedSource, summary.ClearTimeMs.Source);
        Assert.Equal(expectedValue, summary.ClearTimeMs.Value);
        Assert.Equal(expectedSource == SettlementFieldSource.Server ? SettlementFieldConfidence.Inferred : SettlementFieldConfidence.Observed, summary.ClearTimeMs.Confidence);
    }

    // 保护的行为:没有最佳用时块时服务端通关用时不算数,只看本地用时:0 没有(缺失)、1 有(来源本地)。
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 1)]
    [InlineData(2, true, 2)]
    public void LocalClearTimeCountsFromOneMillisecondWhenThereIsNoServerTime(int localClearMs, bool expectedPresent, int expectedValue)
    {
        var data = OnlineSettlementGapBKit.Data(null, Result(9000, hasBlock: false));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data, LocalStats(clearMs: localClearMs));

        Assert.Equal(expectedPresent, summary.ClearTimeMs.HasValue);
        if (expectedPresent)
        {
            Assert.Equal(expectedValue, summary.ClearTimeMs.Value);
            Assert.Equal(SettlementFieldSource.Local, summary.ClearTimeMs.Source);
            Assert.Equal(SettlementFieldConfidence.Observed, summary.ClearTimeMs.Confidence);
        }
        Assert.DoesNotContain("clear_time", summary.DiffFields);
    }

    // 保护的行为:服务端用时有效时,本地用时 > 0 且与服务端不同才记 clear_time 差异:本地 0 不记、本地 1(≠ 服务端)记、相同不记。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5000, true)]
    [InlineData(9000, false)]
    public void ClearTimeDiffIsRecordedOnlyForAPositiveLocalTimeThatDiffers(int localClearMs, bool expectDiff)
    {
        var data = OnlineSettlementGapBKit.Data(null, Result(9000, hasBlock: true));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data, LocalStats(clearMs: localClearMs));

        Assert.Equal(expectDiff, summary.DiffFields.Contains("clear_time"));
        Assert.Equal(9000, summary.ClearTimeMs.Value);
    }

    // 保护的行为:个人最佳用时 ≥ 0 才算(0 算、-1 不算、1 算),"新纪录"标记与是否有最佳用时无关照样取服务端值;任务迷宫标记 == 1 才是真。
    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(-1, false, true)]
    [InlineData(-9, false, false)]
    public void PersonalBestIsValidFromZeroAndTheNewRecordFlagIsKeptRegardlessOfIt(int bestTime, bool expectBest, bool newRecord)
    {
        var entry = new OnlineSettlementBestTime(7, bestTime, newRecord);
        var data = OnlineSettlementGapBKit.Data(null, Result(30000, hasBlock: true, questMazeFlag: 1, entry));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data, LocalStats(self: 7));

        Assert.Equal(expectBest, summary.PersonalBestTimeMs.HasValue);
        if (expectBest)
        {
            Assert.Equal(bestTime, summary.PersonalBestTimeMs.Value);
            Assert.Equal(SettlementFieldSource.Server, summary.PersonalBestTimeMs.Source);
        }
        Assert.True(summary.IsNewRecord.HasValue);
        Assert.Equal(newRecord, summary.IsNewRecord.Value);
        Assert.True(summary.IsQuestMaze.HasValue);
        Assert.True(summary.IsQuestMaze.Value);
    }

    // 保护的行为:在队员条目里找自己:按唯一号精确匹配(第一个、最后一个都找得到,找不到就是没有最佳用时,不越界);
    // 没有自己的唯一号时,只有"恰好一个条目"才用它,零个或多个都不用;空条目没有。
    [Theory]
    [InlineData(7, 100, true)]
    [InlineData(9, 200, true)]
    [InlineData(8, 0, false)]
    public void SelfBestTimeIsFoundByUniqueIdAmongSeveralEntries(int self, int expectedBest, bool expectFound)
    {
        var entries = new[] { new OnlineSettlementBestTime(7, 100, false), new OnlineSettlementBestTime(9, 200, true) };
        var data = OnlineSettlementGapBKit.Data(null, Result(30000, hasBlock: true, 0, entries));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data, LocalStats(self: (ushort)self));

        Assert.Equal(expectFound, summary.PersonalBestTimeMs.HasValue);
        Assert.Equal(expectFound, summary.IsNewRecord.HasValue);
        if (expectFound)
        {
            Assert.Equal(expectedBest, summary.PersonalBestTimeMs.Value);
            Assert.Equal(self == 9, summary.IsNewRecord.Value);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void WithoutASelfIdOnlyASingleEntryIsUsed(int entryCount, bool expectFound)
    {
        OnlineSettlementBestTime[] entries = Enumerable.Range(0, entryCount).Select(i => new OnlineSettlementBestTime((ushort)(20 + i), 300 + i, i == 0)).ToArray();
        var data = OnlineSettlementGapBKit.Data(null, Result(30000, hasBlock: true, 0, entries));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data, LocalStats(self: null));

        Assert.Equal(expectFound, summary.PersonalBestTimeMs.HasValue);
        if (expectFound)
        {
            Assert.Equal(300, summary.PersonalBestTimeMs.Value);
            Assert.True(summary.IsNewRecord.Value);
        }
    }

    // 保护的行为:击杀差异——联网上报数与本地击杀数不同才记 kills 差异,相同不记;击杀数永远取联网上报数。
    [Theory]
    [InlineData(5, 5, false)]
    [InlineData(5, 6, true)]
    [InlineData(6, 5, true)]
    [InlineData(0, 0, false)]
    public void KillsDiffIsRecordedOnlyWhenLocalAndReportedDiffer(int localKills, int reportedKills, bool expectDiff)
    {
        var data = OnlineSettlementGapBKit.Data(null, null, reportedKills);
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data, LocalStats(kills: localKills));

        Assert.Equal(expectDiff, summary.DiffFields.Contains("kills"));
        Assert.Equal(reportedKills, summary.Kills.Value);
    }

    // ---------------------------------------------------------------- 经验块

    // 保护的行为:经验块按"数量"取字段——空表退回本地、1 项只有基础经验、2 项加评分加成、3 项加未知下标 2、4 项以上才有具名分项:
    //   [] → 没有服务端基础经验(退回本地 800/50);[500] → 基础 500,无评分加成、无未知下标 2,有把握增量 500;
    //   [500,70] → 评分加成 70、无未知下标 2,增量 570;[500,70,9] → 未知下标 2 = 9(只记录不显示),无具名分项。
    [Fact]
    public void ExpBlockFieldsAppearByCount()
    {
        OnlineSettlementLocalContext local = OnlineSettlementGapBKit.LocalWith(OnlineSettlementGapBKit.LocalResult(800, 50));

        OnlineSettlementSummary none = BuildExp(Array.Empty<int>(), local);
        Assert.Equal((800, SettlementFieldSource.Local), (none.BaseExp.Value, none.BaseExp.Source));
        Assert.Equal((50, SettlementFieldSource.Local), (none.RankBonusExp.Value, none.RankBonusExp.Source));
        Assert.Equal(0, none.ConfidentExpGrant);
        Assert.False(none.UnknownExpSlot2.HasValue);

        OnlineSettlementSummary one = BuildExp(new[] { 500 }, local);
        Assert.Equal((500, SettlementFieldSource.Server, SettlementFieldConfidence.Inferred), (one.BaseExp.Value, one.BaseExp.Source, one.BaseExp.Confidence));
        Assert.False(one.RankBonusExp.HasValue);
        Assert.False(one.UnknownExpSlot2.HasValue);
        Assert.Empty(one.NamedBonusExpLines);
        Assert.Equal(500, one.ConfidentExpGrant);
        Assert.Contains("base_exp", one.DiffFields);
        Assert.DoesNotContain("rank_bonus_exp", one.DiffFields);

        OnlineSettlementSummary two = BuildExp(new[] { 500, 70 }, local);
        Assert.Equal((70, SettlementFieldSource.Server, SettlementFieldConfidence.Inferred), (two.RankBonusExp.Value, two.RankBonusExp.Source, two.RankBonusExp.Confidence));
        Assert.False(two.UnknownExpSlot2.HasValue);
        Assert.Equal(570, two.ConfidentExpGrant);
        Assert.Contains("rank_bonus_exp", two.DiffFields);

        OnlineSettlementSummary three = BuildExp(new[] { 500, 70, 9 }, local);
        Assert.True(three.UnknownExpSlot2.HasValue);
        Assert.Equal(9, three.UnknownExpSlot2.Value);
        Assert.Equal(SettlementFieldConfidence.Unknown, three.UnknownExpSlot2.Confidence);
        Assert.False(three.UnknownExpSlot2.ShouldDisplay);
        Assert.Empty(three.NamedBonusExpLines);
        Assert.Equal(570, three.ConfidentExpGrant);
    }

    // 保护的行为:具名加成分项从下标 3 起,取"数额 > 0"的项(0 与负数跳过、1 保留),下标不超过 15,
    // 经验块再长(这里 20 项)也只有下标 3..15 共 13 项:键依次是 avatar … premium_extra,每项记原下标;长度恰为 5 时下标 3、4 都能取到、不越界。
    [Fact]
    public void NamedBonusLinesStartAtIndexThreeSkipNonPositiveAmountsAndStopAtIndexFifteen()
    {
        // 长度 5:[基础 100, 评分 20, 未知 3, avatar 40, event 0] → 只有 avatar
        OnlineSettlementSummary five = BuildExp(new[] { 100, 20, 3, 40, 0 });
        OnlineSettlementExpLine only = Assert.Single(five.NamedBonusExpLines);
        Assert.Equal((3, "avatar", 40, SettlementFieldConfidence.Inferred), (only.Index, only.Key, only.Amount, only.Confidence));

        // 长度 5:[.., avatar -5, event 1] → 负数跳过、1 保留
        OnlineSettlementSummary signs = BuildExp(new[] { 100, 20, 3, -5, 1 });
        OnlineSettlementExpLine one = Assert.Single(signs.NamedBonusExpLines);
        Assert.Equal((4, "event", 1), (one.Index, one.Key, one.Amount));

        // 长度 4:恰好只有下标 3
        OnlineSettlementSummary four = BuildExp(new[] { 100, 20, 3, 7 });
        Assert.Equal(new[] { (3, "avatar", 7) }, four.NamedBonusExpLines.Select(l => (l.Index, l.Key, l.Amount)).ToArray());

        // 长度 20,每项都是 下标 + 1:具名分项只有下标 3..15,共 13 项
        int[] values = Enumerable.Range(0, 20).Select(i => i + 1).ToArray();
        OnlineSettlementSummary many = BuildExp(values);
        string[] keys = { "avatar", "event", "item", "privilege", "pcroom_party", "channel", "upper_member", "creature", "return_user", "new_char", "manage", "growth_power", "premium_extra" };
        Assert.Equal(13, many.NamedBonusExpLines.Count);
        for (int i = 0; i < 13; i++)
        {
            Assert.Equal((3 + i, keys[i], 4 + i), (many.NamedBonusExpLines[i].Index, many.NamedBonusExpLines[i].Key, many.NamedBonusExpLines[i].Amount));
        }
        // 长度恰为 16:下标 15 是最后一个
        OnlineSettlementSummary sixteen = BuildExp(Enumerable.Range(0, 16).Select(i => i + 1).ToArray());
        Assert.Equal(13, sixteen.NamedBonusExpLines.Count);
        Assert.Equal(15, sixteen.NamedBonusExpLines[12].Index);
    }

    // 保护的行为:有把握的经验增量 = 服务端基础经验与评分加成里"为正的部分"之和,夹到 int 上限:
    // 正数才加(0 与负数当 0,1 要加),只有基础或只有评分都行,相加超过上限夹到 int.MaxValue,恰为上限减 1 时原样。
    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(0, 0, 0)]
    [InlineData(-100, 50, 50)]
    [InlineData(100, -50, 100)]
    [InlineData(-100, -50, 0)]
    [InlineData(int.MaxValue - 1, 0, int.MaxValue - 1)]
    [InlineData(int.MaxValue, 0, int.MaxValue)]
    [InlineData(int.MaxValue, 5, int.MaxValue)]
    [InlineData(2000000000, 2000000000, int.MaxValue)]
    public void ConfidentExpGrantSumsOnlyPositiveBaseAndRankAndClampsToIntMax(int baseExp, int rankExp, int expectedGrant)
    {
        OnlineSettlementSummary summary = BuildExp(new[] { baseExp, rankExp });

        Assert.Equal(expectedGrant, summary.ConfidentExpGrant);
        Assert.Equal(baseExp, summary.BaseExp.Value);
        Assert.Equal(rankExp, summary.RankBonusExp.Value);
    }

    // 保护的行为:没有服务端经验块时退回本地算出的经验:数额 > 0 才有(0 缺失、1 有),超过 int 上限夹到上限、恰为上限减 1 原样,负数缺失;
    // 退回本地时来源是本地、把握程度是观察,有把握增量为 0(本地估算不发经验)。
    [Theory]
    [InlineData(0L, 0L, false, false)]
    [InlineData(1L, 1L, true, true)]
    [InlineData(-5L, -5L, false, false)]
    [InlineData(2147483646L, 2147483646L, true, true)]
    [InlineData(5000000000L, 5000000000L, true, true)]
    [InlineData(0L, 7L, false, true)]
    [InlineData(7L, 0L, true, false)]
    public void LocalExpFallbackNeedsAPositiveAmountAndClampsToIntMax(long localBase, long localRank, bool expectBase, bool expectRank)
    {
        OnlineSettlementLocalContext local = OnlineSettlementGapBKit.LocalWith(OnlineSettlementGapBKit.LocalResult(localBase, localRank));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(OnlineSettlementGapBKit.Data(null), local);

        static int Clamp(long v) => v >= int.MaxValue ? int.MaxValue : (int)v;
        Assert.Equal(expectBase, summary.BaseExp.HasValue);
        Assert.Equal(expectRank, summary.RankBonusExp.HasValue);
        if (expectBase)
        {
            Assert.Equal((Clamp(localBase), SettlementFieldSource.Local, SettlementFieldConfidence.Observed), (summary.BaseExp.Value, summary.BaseExp.Source, summary.BaseExp.Confidence));
        }
        if (expectRank)
        {
            Assert.Equal((Clamp(localRank), SettlementFieldSource.Local, SettlementFieldConfidence.Observed), (summary.RankBonusExp.Value, summary.RankBonusExp.Source, summary.RankBonusExp.Confidence));
        }
        Assert.Equal(0, summary.ConfidentExpGrant);
    }

    // ---------------------------------------------------------------- 掉落物品与高级奖励计数

    // 保护的行为:任务清图物品与奖励槽物品全部进入掉落表(先任务物品、后奖励槽物品;只有状态 2 的槽才有物品),
    // 数量 ≤ 0 当 1、≥ 1 原样(数量 2 保持 2);物品号 ≤ 0 不查名字、物品号 1 要查;解析器返回空串当没有名字。
    [Fact]
    public void DropsKeepEveryQuestItemAndItemSlotWithCountsNamesAndResolverCalls()
    {
        var calls = new List<int>();
        string Resolver(int id)
        {
            calls.Add(id);
            return id == 3 ? string.Empty : "物" + id;
        }
        var reward = OnlineSettlementGapBKit.Reward(
            questItems: new[] { OnlineSettlementGapBKit.Quest(1, 0), OnlineSettlementGapBKit.Quest(2, 2), OnlineSettlementGapBKit.Quest(0, 5), OnlineSettlementGapBKit.Quest(3, -4) },
            rewards: new[]
            {
                OnlineSettlementGapBKit.Slot(2, 0, itemId: 4, itemData: 0),
                OnlineSettlementGapBKit.Slot(1, 77, itemId: 5, itemData: 9),
                OnlineSettlementGapBKit.Slot(2, 0, itemId: 6, itemData: 3),
                OnlineSettlementGapBKit.Slot(2, 0, itemId: -2, itemData: 1),
            });

        OnlineSettlementSummary summary = BuildReward(reward, null, Resolver);

        var shown = summary.Drops.Select(d => (d.ItemId, d.Count, d.Name, d.IsQuestItem)).ToArray();
        Assert.Equal(new (int, int, string, bool)[]
        {
            (1, 1, "物1", true),
            (2, 2, "物2", true),
            (0, 5, null, true),
            (3, 1, null, true),
            (4, 1, "物4", false),
            (6, 3, "物6", false),
            (-2, 1, null, false),
        }, shown);
        // 解析器只对物品号 > 0 的物品被叫,且每个物品一次:1、2、3(任务)、4、6(奖励槽)
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, calls);
    }

    // 保护的行为:高级奖励里"状态非 0"的槽位个数被记成未知字段(只记录不显示);状态 1、2 都算,0 不算;下标 0 的槽也要数。
    [Fact]
    public void PremiumRewardActiveCountCountsEveryNonZeroSlotIncludingTheFirst()
    {
        var reward = OnlineSettlementGapBKit.Reward(premium: new[]
        {
            OnlineSettlementGapBKit.Slot(1, 0),
            OnlineSettlementGapBKit.Slot(0, 0),
            OnlineSettlementGapBKit.Slot(2, 0),
            OnlineSettlementGapBKit.Slot(1, 0),
            OnlineSettlementGapBKit.Slot(0, 0),
        });

        OnlineSettlementSummary summary = BuildReward(reward);

        Assert.True(summary.UnknownPremiumRewardActiveCount.HasValue);
        Assert.Equal(3, summary.UnknownPremiumRewardActiveCount.Value);
        Assert.Equal(SettlementFieldConfidence.Unknown, summary.UnknownPremiumRewardActiveCount.Confidence);
        Assert.False(summary.UnknownPremiumRewardActiveCount.ShouldDisplay);
        Assert.Equal(0, BuildReward(OnlineSettlementGapBKit.Reward()).UnknownPremiumRewardActiveCount.Value);
    }

    // ---------------------------------------------------------------- 物品名解析与默认评分帧

    // 保护的行为:没有本地评分时评分帧默认取最后一帧(评分字母张数 − 1 = 8),字母 "F"、没有评分、来源无。
    [Fact]
    public void RankDefaultsToTheLastFrameAndLetterFWithoutALocalRank()
    {
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(OnlineSettlementGapBKit.Data(null));

        Assert.False(summary.HasRank);
        Assert.Equal(8, summary.RankFrame);
        Assert.Equal(ResultArt.FrameCount - 1, summary.RankFrame);
        Assert.Equal("F", summary.RankLetter);
        Assert.Equal(SettlementFieldSource.None, summary.RankSource);
    }

    // ---------------------------------------------------------------- 经验刷新帮助器的有把握增量边界

    // 保护的行为:有把握增量为 0 时不发经验、不标已发放(也不动等级);增量为 1 时要发(经验 + 1、已发放、返回 1)。
    [Fact]
    public void ApplyConfidentExpDoesNothingForAZeroGrantButAppliesAGrantOfOne()
    {
        HeroProgress progress = SyntheticProgress();
        OnlineSettlementSummary zero = BuildExp(new[] { 0, 0 });
        Assert.Equal(0, zero.ConfidentExpGrant);

        int appliedZero = OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, zero, out int zeroBefore, out int zeroAfter);

        Assert.Equal(0, appliedZero);
        Assert.False(zero.ExpApplied);
        Assert.Equal(0, progress.Exp);
        Assert.Equal((1, 1), (zeroBefore, zeroAfter));

        OnlineSettlementSummary one = BuildExp(new[] { 1, 0 });
        int appliedOne = OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, one, out int oneBefore, out int oneAfter);

        Assert.Equal(1, appliedOne);
        Assert.True(one.ExpApplied);
        Assert.Equal(1, one.ExpAppliedAmount);
        Assert.Equal(1, progress.Exp);
        Assert.Equal((1, 1), (oneBefore, oneAfter));
    }
}
