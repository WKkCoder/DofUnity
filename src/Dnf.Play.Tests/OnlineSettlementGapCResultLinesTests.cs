namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算战果文案行(<see cref="OnlineSettlementOverlay.BuildResultLines"/>,纯函数)的出现条件与边界:
/// 难度行(只有难度大于 0 才出)、评分加成经验行(要"可显示"且"值大于 0")、死亡惩罚行与回归队伍行(要"可显示"且"值为真")。
/// 摘要用逐字段构造器直接造,能造出"值有但把握程度是未知(不可显示)"、"可显示但值为 0/假"这类组合。
/// 每个场景都整份比对文案行(顺序也是被测对象),中英两套文案都核对。
/// </summary>
public sealed class OnlineSettlementGapCResultLinesTests
{
    private static readonly string[] BareEnglish = { "CLEAR TIME --:--", "DROPS: --", "NOTE: ITEMS/GOLD UNSYNCED" };

    private static readonly string[] BareChinese = { "通关用时 --:--", "掉落物品: --", "提示: 物品/金币暂未同步至本地" };

    private static string[] Lines(OnlineSettlementSummary summary, bool hasFonts)
    {
        return OnlineSettlementOverlay.BuildResultLines(summary, hasFonts, rankLetterDrawn: false).ToArray();
    }

    /// <summary>在"什么都没有"的三行里,第一行(通关用时)之后插入若干行。</summary>
    private static string[] BareWith(bool hasFonts, params string[] insertedAfterClearTime)
    {
        string[] bare = hasFonts ? BareChinese : BareEnglish;
        return new[] { bare[0] }.Concat(insertedAfterClearTime).Concat(bare.Skip(1)).ToArray();
    }

    // 保护的行为:难度行只在难度大于 0 时出现;0(普通难度没有专门的行)与负数都不出,越界的难度显示问号而不是漏掉。
    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, "EXPERT")]
    [InlineData(2, "MASTER")]
    [InlineData(3, "KING")]
    [InlineData(4, "HERO")]
    [InlineData(9, "?")]
    public void DifficultyLineAppearsOnlyForDifficultyAboveZero(int difficulty, string expectedName)
    {
        OnlineSettlementSummary summary = new OnlineSettlementGapCKit.SummarySpec { Difficulty = difficulty }.Build();

        if (expectedName == null)
        {
            Assert.Equal(BareEnglish, Lines(summary, hasFonts: false));
            Assert.Equal(BareChinese, Lines(summary, hasFonts: true));
        }
        else
        {
            Assert.Equal(BareWith(false, "DIFFICULTY " + expectedName), Lines(summary, hasFonts: false));
            Assert.Equal(BareWith(true, "难度 " + expectedName), Lines(summary, hasFonts: true));
        }
    }

    // 保护的行为:评分加成经验行要求"可显示"且"值大于 0"两个条件同时成立——
    // 把握程度未知的字段哪怕带着正数也不能画(未知字段绝不上屏),可显示但值为 0 或负数也不画"+0"。
    [Fact]
    public void RankBonusExpLineNeedsBothDisplayableAndPositiveValue()
    {
        // 先确认造出来的组合确实是想要的那几种(防止构造方式变了而测试悄悄变成空转)
        OnlineSettlementField<int> hiddenButPositive = OnlineSettlementField<int>.Unknown(350, SettlementFieldSource.Server);
        Assert.False(hiddenButPositive.ShouldDisplay);
        Assert.Equal(350, hiddenButPositive.Value);
        OnlineSettlementField<int> shownButZero = OnlineSettlementField<int>.Inferred(0, SettlementFieldSource.Server);
        Assert.True(shownButZero.ShouldDisplay);
        Assert.Equal(0, shownButZero.Value);

        // 值有但不可显示(Unknown):没有这一行
        AssertRankBonusLines(OnlineSettlementField<int>.Unknown(350, SettlementFieldSource.Server), null);
        // 可显示但值为 0:没有这一行(不画 "+0")
        AssertRankBonusLines(OnlineSettlementField<int>.Inferred(0, SettlementFieldSource.Server), null);
        AssertRankBonusLines(OnlineSettlementField<int>.Observed(0, SettlementFieldSource.Local), null);
        // 可显示但值为负:同样没有
        AssertRankBonusLines(OnlineSettlementField<int>.Inferred(-5, SettlementFieldSource.Server), null);
        // 缺失(没有值也不可显示):没有
        AssertRankBonusLines(OnlineSettlementField<int>.Missing(), null);
        // 两个条件都满足:有这一行,边界值 1 也算
        AssertRankBonusLines(OnlineSettlementField<int>.Inferred(350, SettlementFieldSource.Server), 350);
        AssertRankBonusLines(OnlineSettlementField<int>.Observed(1, SettlementFieldSource.Local), 1);
    }

    private static void AssertRankBonusLines(OnlineSettlementField<int> bonus, int? expectedAmount)
    {
        OnlineSettlementSummary summary = new OnlineSettlementGapCKit.SummarySpec { RankBonusExp = bonus }.Build();
        if (expectedAmount == null)
        {
            Assert.Equal(BareEnglish, Lines(summary, hasFonts: false));
            Assert.Equal(BareChinese, Lines(summary, hasFonts: true));
            return;
        }
        string amount = expectedAmount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(BareWith(false, "RANK BONUS +" + amount), Lines(summary, hasFonts: false));
        Assert.Equal(BareWith(true, "评分加成经验 +" + amount), Lines(summary, hasFonts: true));
    }

    // 保护的行为:死亡经验惩罚行要求"可显示"且"值为真":把握程度未知的真值不画,可显示的假值也不画。
    [Fact]
    public void DeadPenaltyLineNeedsBothDisplayableAndTrueValue()
    {
        // 值为真但不可显示(Unknown):没有
        AssertFlagLines(deadPenalty: OnlineSettlementField<bool>.Unknown(true, SettlementFieldSource.Server), expectDead: false, expectReturn: false);
        // 可显示但值为假:没有
        AssertFlagLines(deadPenalty: OnlineSettlementField<bool>.Inferred(false, SettlementFieldSource.Server), expectDead: false, expectReturn: false);
        AssertFlagLines(deadPenalty: OnlineSettlementField<bool>.Observed(false, SettlementFieldSource.Server), expectDead: false, expectReturn: false);
        // 缺失:没有
        AssertFlagLines(deadPenalty: OnlineSettlementField<bool>.Missing(), expectDead: false, expectReturn: false);
        // 两个条件都满足:有
        AssertFlagLines(deadPenalty: OnlineSettlementField<bool>.Inferred(true, SettlementFieldSource.Server), expectDead: true, expectReturn: false);
    }

    // 保护的行为:回归用户队伍行与死亡惩罚行是同一套"可显示且为真"规则,且两行互不牵连、先后次序固定(死亡惩罚在前)。
    [Fact]
    public void ReturnUserPartyLineFollowsTheSameRuleAndComesAfterDeadPenalty()
    {
        AssertFlagLines(returnUser: OnlineSettlementField<bool>.Unknown(true, SettlementFieldSource.Server), expectDead: false, expectReturn: false);
        AssertFlagLines(returnUser: OnlineSettlementField<bool>.Observed(false, SettlementFieldSource.Server), expectDead: false, expectReturn: false);
        AssertFlagLines(returnUser: OnlineSettlementField<bool>.Observed(true, SettlementFieldSource.Server), expectDead: false, expectReturn: true);
        AssertFlagLines(
            deadPenalty: OnlineSettlementField<bool>.Inferred(true, SettlementFieldSource.Server),
            returnUser: OnlineSettlementField<bool>.Observed(true, SettlementFieldSource.Server),
            expectDead: true,
            expectReturn: true);
    }

    private static void AssertFlagLines(
        bool expectDead,
        bool expectReturn,
        OnlineSettlementField<bool>? deadPenalty = null,
        OnlineSettlementField<bool>? returnUser = null)
    {
        var spec = new OnlineSettlementGapCKit.SummarySpec
        {
            DeadPenalty = deadPenalty ?? OnlineSettlementField<bool>.Missing(),
            ReturnUser = returnUser ?? OnlineSettlementField<bool>.Missing(),
        };
        OnlineSettlementSummary summary = spec.Build();

        var english = new List<string>();
        var chinese = new List<string>();
        if (expectDead)
        {
            english.Add("DEAD PENALTY APPLIED");
            chinese.Add("已计死亡经验惩罚");
        }
        if (expectReturn)
        {
            english.Add("RETURN USER PARTY");
            chinese.Add("回归用户队伍");
        }
        Assert.Equal(BareWith(false, english.ToArray()), Lines(summary, hasFonts: false));
        Assert.Equal(BareWith(true, chinese.ToArray()), Lines(summary, hasFonts: true));
    }

    // 保护的行为:字段齐全时战果行的内容与先后次序(经验各项、惩罚、金币、掉落、提示)与服务端优先的规则一致:
    // 难度行排在个人最佳与击杀之间,评分加成紧跟基础经验,具名加成按下标顺序且受 4 条上限约束,最后一条永远是"未同步"提示。
    [Fact]
    public void FullSummaryProducesEveryLineInTheDocumentedOrder()
    {
        var named = new[]
        {
            new OnlineSettlementExpLine(3, "avatar", 120, SettlementFieldConfidence.Inferred),
            new OnlineSettlementExpLine(4, "event", 0, SettlementFieldConfidence.Inferred),      // 值为 0:不画
            new OnlineSettlementExpLine(5, "item", 33, SettlementFieldConfidence.Unknown),       // 未知:不画
            new OnlineSettlementExpLine(8, "channel", 80, SettlementFieldConfidence.Inferred),
        };
        var drops = new[]
        {
            new OnlineSettlementDropItem(9801, 3, "合成晶石", isQuestItem: true),
            new OnlineSettlementDropItem(9802, 1, null, isQuestItem: false),
        };
        var spec = new OnlineSettlementGapCKit.SummarySpec
        {
            Difficulty = 2,
            RewardParse = OnlineSettlementParseState.Succeeded,
            ClearTime = OnlineSettlementField<int>.Inferred(65000, SettlementFieldSource.Server),
            PersonalBest = OnlineSettlementField<int>.Inferred(42000, SettlementFieldSource.Server),
            IsNewRecord = OnlineSettlementField<bool>.Observed(true, SettlementFieldSource.Server),
            Kills = OnlineSettlementField<int>.Observed(7, SettlementFieldSource.Server),
            DroppedKills = 2,
            BaseExp = OnlineSettlementField<int>.Inferred(1800, SettlementFieldSource.Server),
            RankBonusExp = OnlineSettlementField<int>.Inferred(350, SettlementFieldSource.Server),
            NamedBonus = named,
            DeadPenalty = OnlineSettlementField<bool>.Inferred(true, SettlementFieldSource.Server),
            ReturnUser = OnlineSettlementField<bool>.Observed(true, SettlementFieldSource.Server),
            RewardGold = OnlineSettlementField<int>.Inferred(640, SettlementFieldSource.Server),
            Drops = drops,
        };
        OnlineSettlementSummary summary = spec.Build();

        Assert.Equal(
            new[]
            {
                "CLEAR TIME 01:05",
                "BEST TIME 00:42 [NEW RECORD!]",
                "DIFFICULTY MASTER",
                "KILLS 7 (DROPPED 2)",
                "CLEAR EXP +1800",
                "RANK BONUS +350",
                "AVATAR EXP +120",
                "CHANNEL EXP +80",
                "DEAD PENALTY APPLIED",
                "RETURN USER PARTY",
                "REWARD GOLD(EST) +640",
                "QUEST ITEM: 合成晶石 x3",
                "DROP: UNKNOWN ITEM",
                "NOTE: ITEMS/GOLD UNSYNCED",
            },
            Lines(summary, hasFonts: false));
        Assert.Equal(
            new[]
            {
                "通关用时 01:05",
                "个人最佳 00:42 [新纪录!]",
                "难度 MASTER",
                "击杀 7 (未计 2)",
                "通关基础经验 +1800",
                "评分加成经验 +350",
                "装扮加成经验 +120",
                "频道加成经验 +80",
                "已计死亡经验惩罚",
                "回归用户队伍",
                "奖励金币(推) +640",
                "任务道具: 合成晶石 x3",
                "奖励物品: 未知道具",
                "提示: 物品/金币暂未同步至本地",
            },
            Lines(summary, hasFonts: true));
    }
}
