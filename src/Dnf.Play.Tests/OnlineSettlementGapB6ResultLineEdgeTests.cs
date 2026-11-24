namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算战果文案行(<see cref="OnlineSettlementOverlay.BuildResultLines"/>,纯函数)的两处计数边界(第六轮 B 区补测):
/// ① 击杀行的"未计"后缀只在未计击杀数大于 0 时出现——恰为 1(最小的正数)也要带;
/// ② 具名加成经验行在"恰好 1 条可显示"时必须出现,0 条(空表、构造器把 null 规整成的空表)或唯一一条不可显示时一条都不出。
/// 摘要用 <see cref="OnlineSettlementGapCKit.SummarySpec"/> 逐字段直接造;每个场景都整份比对文案行(顺序也是被测对象),中英两套文案都核对。
/// 没有别的字段时战果行只有四条:用时占位 / (击杀、加成等插入的行)/ 掉落占位 / 固定的"未同步"提示。
/// </summary>
public sealed class OnlineSettlementGapB6ResultLineEdgeTests
{
    private static string[] Lines(OnlineSettlementSummary summary, bool hasFonts)
    {
        return OnlineSettlementOverlay.BuildResultLines(summary, hasFonts, rankLetterDrawn: false).ToArray();
    }

    /// <summary>"什么都没有"的战果行(英文):用时占位、掉落占位、未同步提示。</summary>
    private static readonly string[] BareEnglish = { "CLEAR TIME --:--", "DROPS: --", "NOTE: ITEMS/GOLD UNSYNCED" };

    private static readonly string[] BareChinese = { "通关用时 --:--", "掉落物品: --", "提示: 物品/金币暂未同步至本地" };

    /// <summary>在"什么都没有"的三行里,第一行(用时占位)之后插入若干行。</summary>
    private static string[] BareWith(bool hasFonts, params string[] insertedAfterClearTime)
    {
        string[] bare = hasFonts ? BareChinese : BareEnglish;
        return new[] { bare[0] }.Concat(insertedAfterClearTime).Concat(bare.Skip(1)).ToArray();
    }

    // 保护的行为:击杀行的"未计"后缀只在未计击杀数大于 0 时出现——0 与负数不带后缀,恰为 1(最小的正数)与 2 都带,很大的数照常带;
    // 后缀只取决于未计数,与击杀数本身无关(击杀数为 0 时 1 个未计照样带后缀)。整份比对四行文案(击杀行排在用时占位之后、掉落占位之前)。
    // 手算:未计 1 → 英文 "KILLS 7 (DROPPED 1)"、中文 "击杀 7 (未计 1)";未计 0 → 只有 "KILLS 7" / "击杀 7"。
    [Theory]
    [InlineData(-1, 7, "KILLS 7", "击杀 7")]
    [InlineData(0, 7, "KILLS 7", "击杀 7")]
    [InlineData(0, 0, "KILLS 0", "击杀 0")]
    [InlineData(1, 7, "KILLS 7 (DROPPED 1)", "击杀 7 (未计 1)")]
    [InlineData(1, 0, "KILLS 0 (DROPPED 1)", "击杀 0 (未计 1)")]
    [InlineData(2, 7, "KILLS 7 (DROPPED 2)", "击杀 7 (未计 2)")]
    [InlineData(2147483647, 12, "KILLS 12 (DROPPED 2147483647)", "击杀 12 (未计 2147483647)")]
    public void DroppedKillsSuffixAppearsOnlyWhenAtLeastOneKillWasNotCounted(int dropped, int kills, string englishKillLine, string chineseKillLine)
    {
        OnlineSettlementSummary summary = new OnlineSettlementGapCKit.SummarySpec
        {
            Kills = OnlineSettlementField<int>.Observed(kills, SettlementFieldSource.Server),
            DroppedKills = dropped,
        }.Build();
        Assert.Equal(dropped, summary.DroppedKills);

        Assert.Equal(
            new[] { "CLEAR TIME --:--", englishKillLine, "DROPS: --", "NOTE: ITEMS/GOLD UNSYNCED" },
            Lines(summary, hasFonts: false));
        Assert.Equal(
            new[] { "通关用时 --:--", chineseKillLine, "掉落物品: --", "提示: 物品/金币暂未同步至本地" },
            Lines(summary, hasFonts: true));
    }

    // 保护的行为:后缀挂在击杀行上,击杀数不可显示(把握程度未知,或字段缺失)时整条击杀行都不画,
    // 未计数(哪怕是 1 或 2)也不会单独漏到屏幕上——未知字段绝不上屏。
    [Fact]
    public void DroppedKillCountNeverShowsWithoutADisplayableKillCount()
    {
        OnlineSettlementField<int> hidden = OnlineSettlementField<int>.Unknown(7, SettlementFieldSource.Server);
        Assert.False(hidden.ShouldDisplay);
        OnlineSettlementField<int> missing = OnlineSettlementField<int>.Missing();
        Assert.False(missing.ShouldDisplay);

        foreach (int dropped in new[] { 1, 2 })
        {
            foreach (OnlineSettlementField<int> kills in new[] { hidden, missing })
            {
                OnlineSettlementSummary summary = new OnlineSettlementGapCKit.SummarySpec { Kills = kills, DroppedKills = dropped }.Build();
                Assert.Equal(BareEnglish, Lines(summary, hasFonts: false));
                Assert.Equal(BareChinese, Lines(summary, hasFonts: true));
            }
        }
    }

    // 保护的行为:具名加成经验行在"恰好 1 条可显示"时必须出现(列表只有一条也要列出来,不是"至少两条才列"),
    // 位置紧跟评分加成之后、死亡惩罚之前;标题随键名变化、金额带 "+",中英文各一份。
    // 手算:("avatar", 120, 推断) → "AVATAR EXP +120" / "装扮加成经验 +120";("channel", 80, 观察) → "CHANNEL EXP +80" / "频道加成经验 +80";
    // ("event", 1, 推断) → "EVENT EXP +1" / "活动加成经验 +1"(金额取最小的正数 1)。
    [Theory]
    [InlineData("avatar", 120, SettlementFieldConfidence.Inferred, "AVATAR EXP +120", "装扮加成经验 +120")]
    [InlineData("channel", 80, SettlementFieldConfidence.Observed, "CHANNEL EXP +80", "频道加成经验 +80")]
    [InlineData("event", 1, SettlementFieldConfidence.Inferred, "EVENT EXP +1", "活动加成经验 +1")]
    public void ExactlyOneDisplayableNamedBonusLineIsListed(string key, int amount, SettlementFieldConfidence confidence, string englishLine, string chineseLine)
    {
        var only = new OnlineSettlementExpLine(3, key, amount, confidence);
        Assert.True(only.ShouldDisplay);

        // 只有这一条具名加成:插在用时占位之后(没有别的行)
        OnlineSettlementSummary alone = new OnlineSettlementGapCKit.SummarySpec { NamedBonus = new[] { only } }.Build();
        Assert.Single(alone.NamedBonusExpLines);
        Assert.Equal(BareWith(false, englishLine), Lines(alone, hasFonts: false));
        Assert.Equal(BareWith(true, chineseLine), Lines(alone, hasFonts: true));

        // 夹在基础经验、评分加成与死亡惩罚之间:紧跟评分加成、先于死亡惩罚
        OnlineSettlementSummary between = new OnlineSettlementGapCKit.SummarySpec
        {
            ClearTime = OnlineSettlementField<int>.Observed(65000, SettlementFieldSource.Local),
            BaseExp = OnlineSettlementField<int>.Inferred(1800, SettlementFieldSource.Server),
            RankBonusExp = OnlineSettlementField<int>.Inferred(350, SettlementFieldSource.Server),
            NamedBonus = new[] { only },
            DeadPenalty = OnlineSettlementField<bool>.Inferred(true, SettlementFieldSource.Server),
            RewardParse = OnlineSettlementParseState.Succeeded,
        }.Build();
        Assert.Equal(
            new[]
            {
                "CLEAR TIME 01:05",
                "CLEAR EXP +1800",
                "RANK BONUS +350",
                englishLine,
                "DEAD PENALTY APPLIED",
                "DROPS: NONE",
                "NOTE: ITEMS/GOLD UNSYNCED",
            },
            Lines(between, hasFonts: false));
        Assert.Equal(
            new[]
            {
                "通关用时 01:05",
                "通关基础经验 +1800",
                "评分加成经验 +350",
                chineseLine,
                "已计死亡经验惩罚",
                "掉落物品: 无",
                "提示: 物品/金币暂未同步至本地",
            },
            Lines(between, hasFonts: true));
    }

    // 保护的行为:具名加成列表里一条可显示的都没有时,一条具名加成行都不出——
    // 空列表、构造器把 null 规整成的空列表、唯一一条把握程度未知(哪怕金额为正)、唯一一条金额为 0、唯一一条金额为负、两条都不可显示。
    // 整份文案行与"什么都没有"的三行完全一致。
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("one-unknown-confidence")]
    [InlineData("one-zero-amount")]
    [InlineData("one-negative-amount")]
    [InlineData("two-hidden-entries")]
    public void NamedBonusLinesStayHiddenWithoutAnyDisplayableEntry(string scenario)
    {
        IReadOnlyList<OnlineSettlementExpLine> named;
        switch (scenario)
        {
            case "null":
                named = null;
                break;
            case "empty":
                named = Array.Empty<OnlineSettlementExpLine>();
                break;
            case "one-unknown-confidence":
                named = new[] { new OnlineSettlementExpLine(5, "item", 33, SettlementFieldConfidence.Unknown) };
                break;
            case "one-zero-amount":
                named = new[] { new OnlineSettlementExpLine(4, "event", 0, SettlementFieldConfidence.Inferred) };
                break;
            case "one-negative-amount":
                named = new[] { new OnlineSettlementExpLine(3, "avatar", -5, SettlementFieldConfidence.Observed) };
                break;
            default:
                named = new[]
                {
                    new OnlineSettlementExpLine(5, "item", 33, SettlementFieldConfidence.Unknown),
                    new OnlineSettlementExpLine(4, "event", 0, SettlementFieldConfidence.Observed),
                };
                break;
        }

        OnlineSettlementSummary summary = new OnlineSettlementGapCKit.SummarySpec { NamedBonus = named }.Build();
        // 自检:摘要里确实没有任何可显示的具名加成(防止构造方式变了而测试悄悄变成空转)
        Assert.All(summary.NamedBonusExpLines, line => Assert.False(line.ShouldDisplay));

        Assert.Equal(BareEnglish, Lines(summary, hasFonts: false));
        Assert.Equal(BareChinese, Lines(summary, hasFonts: true));
    }

    // 保护的行为:同一份摘要里"恰好 1 个未计击杀"与"恰好 1 条具名加成"同时成立时两处都要出现,
    // 且各在自己的位置上(击杀行在用时之后、经验各项之前;具名加成在评分加成之后),整份行列表逐条比对。
    [Fact]
    public void OneDroppedKillAndOneNamedBonusLineAppearTogetherInTheirOwnPositions()
    {
        OnlineSettlementSummary summary = new OnlineSettlementGapCKit.SummarySpec
        {
            ClearTime = OnlineSettlementField<int>.Observed(65000, SettlementFieldSource.Local),
            Kills = OnlineSettlementField<int>.Observed(5, SettlementFieldSource.Server),
            DroppedKills = 1,
            BaseExp = OnlineSettlementField<int>.Inferred(1800, SettlementFieldSource.Server),
            RankBonusExp = OnlineSettlementField<int>.Inferred(350, SettlementFieldSource.Server),
            NamedBonus = new[] { new OnlineSettlementExpLine(8, "channel", 80, SettlementFieldConfidence.Inferred) },
            RewardParse = OnlineSettlementParseState.Succeeded,
        }.Build();

        Assert.Equal(
            new[]
            {
                "CLEAR TIME 01:05",
                "KILLS 5 (DROPPED 1)",
                "CLEAR EXP +1800",
                "RANK BONUS +350",
                "CHANNEL EXP +80",
                "DROPS: NONE",
                "NOTE: ITEMS/GOLD UNSYNCED",
            },
            Lines(summary, hasFonts: false));
        Assert.Equal(
            new[]
            {
                "通关用时 01:05",
                "击杀 5 (未计 1)",
                "通关基础经验 +1800",
                "评分加成经验 +350",
                "频道加成经验 +80",
                "掉落物品: 无",
                "提示: 物品/金币暂未同步至本地",
            },
            Lines(summary, hasFonts: true));
    }
}
