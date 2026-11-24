namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 联网结算画面"结果行文案"的逐字补测(区域 B,第三轮):
/// <see cref="OnlineSettlementOverlay.BuildResultLines"/> 是纯函数,这里对它的每一个分支、每一句文案(有字体中文 / 无字体英文两个版本)
/// 用 <c>Assert.Equal(预期数组, 实际列表)</c> 整行列表逐字钉住:基础经验占位的两个条件、掉落超限的"其余掉落"行、升级行、评分/用时/最佳/难度/击杀/
/// 经验/标志/金币/掉落物前缀/说明行。预期数组全部手写(不从被测函数推出来),数值在注释里写推导;全部合成数据,不含任何原版内容。
/// </summary>
public sealed class OnlineSettlementGapB3ResultLineTextTests
{
    // ---- 被多处引用的整句文案(逐字手写;改动产品里的任何一个字都会让引用它的用例失败) ----
    private const string ClearTimeNoneZh = "通关用时 --:--";
    private const string ClearTimeNoneEn = "CLEAR TIME --:--";
    private const string DropsNoneZh = "掉落物品: 无";
    private const string DropsNoneEn = "DROPS: NONE";
    private const string DropsUnknownZh = "掉落物品: --";
    private const string DropsUnknownEn = "DROPS: --";
    private const string BaseExpNoneZh = "通关基础经验 --";
    private const string BaseExpNoneEn = "CLEAR EXP --";
    private const string NoteZh = "提示: 物品/金币暂未同步至本地";
    private const string NoteEn = "NOTE: ITEMS/GOLD UNSYNCED";

    // ---- 合成摘要:只摆放结果行用到的输入,其余字段一律"缺失" ----

    private static OnlineSettlementField<int> Inferred(int value) => OnlineSettlementField<int>.Inferred(value, SettlementFieldSource.Server);

    private static OnlineSettlementField<int> Observed(int value) => OnlineSettlementField<int>.Observed(value, SettlementFieldSource.Server);

    private static OnlineSettlementField<int> UnknownInt(int value) => OnlineSettlementField<int>.Unknown(value, SettlementFieldSource.Server);

    private static OnlineSettlementField<bool> Flag(bool value) => OnlineSettlementField<bool>.Observed(value, SettlementFieldSource.Server);

    private static OnlineSettlementSummary Make(
        bool hasRank = false,
        string rankLetter = "F",
        int difficulty = 0,
        bool isTimedOut = false,
        OnlineSettlementParseState rewardState = OnlineSettlementParseState.NotApplicable,
        OnlineSettlementField<int> clearTime = default,
        OnlineSettlementField<int> bestTime = default,
        OnlineSettlementField<bool> newRecord = default,
        OnlineSettlementField<int> kills = default,
        int droppedKills = 0,
        OnlineSettlementField<int> baseExp = default,
        OnlineSettlementField<int> rankBonus = default,
        IReadOnlyList<OnlineSettlementExpLine> named = null,
        OnlineSettlementField<bool> deadPenalty = default,
        OnlineSettlementField<bool> returnUser = default,
        OnlineSettlementField<int> gold = default,
        IReadOnlyList<OnlineSettlementDropItem> drops = null)
    {
        return new OnlineSettlementSummary(
            dungeonId: 9731,
            dungeonPath: "dungeon/test/b3text.dgn",
            difficulty: difficulty,
            roomsEntered: 1,
            isTimedOut: isTimedOut,
            resultParseState: OnlineSettlementParseState.NotApplicable,
            rewardParseState: rewardState,
            hasRank: hasRank,
            rankFrame: 8,
            rankLetter: rankLetter,
            rankSource: hasRank ? SettlementFieldSource.Local : SettlementFieldSource.None,
            serverRankValue: default,
            clearTimeMs: clearTime,
            personalBestTimeMs: bestTime,
            isNewRecord: newRecord,
            isQuestMaze: default,
            kills: kills,
            droppedKills: droppedKills,
            baseExp: baseExp,
            rankBonusExp: rankBonus,
            namedBonusExpLines: named,
            rewardGold: gold,
            drops: drops,
            isReturnUserParty: returnUser,
            hasDeadUserPenalty: deadPenalty,
            unknownExpSlot2: default,
            unknownReceiverValue: default,
            unknownPercent: default,
            unknownIsEventCharacParty: default,
            unknownPremiumRewardActiveCount: default,
            diffFields: null,
            confidentExpGrant: 0);
    }

    /// <summary>n 件非任务掉落,名字依次是 "Item1".."Itemn"(数量 1,查得到名字)。</summary>
    private static OnlineSettlementDropItem[] PlainDrops(int n)
    {
        var drops = new OnlineSettlementDropItem[n];
        for (int i = 0; i < n; i++)
        {
            drops[i] = new OnlineSettlementDropItem(9900 + i, 1, "Item" + (i + 1), isQuestItem: false);
        }
        return drops;
    }

    /// <summary>同一份摘要分别按"有字体 / 无字体"取结果行,逐行比对手写的预期数组。</summary>
    private static void AssertLines(OnlineSettlementSummary summary, string[] zh, string[] en, bool rankLetterDrawn = false)
    {
        List<string> withFonts = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: true, rankLetterDrawn: rankLetterDrawn);
        Assert.Equal(zh, withFonts.ToArray());
        List<string> withoutFonts = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: false, rankLetterDrawn: rankLetterDrawn);
        Assert.Equal(en, withoutFonts.ToArray());
    }

    // ================================================================== 空摘要与最简摘要

    /// <summary>保护的行为:没有摘要时一行都不出(返回空列表,不是 null),与有字体/无字体、是否已画评分字母无关。</summary>
    [Fact]
    public void A_missing_summary_yields_an_empty_list_for_every_flag_combination()
    {
        foreach (bool fonts in new[] { true, false })
        {
            foreach (bool drawn in new[] { true, false })
            {
                List<string> lines = OnlineSettlementOverlay.BuildResultLines(null, fonts, drawn);
                Assert.NotNull(lines);
                Assert.Empty(lines);
            }
        }
    }

    /// <summary>
    /// 保护的行为:什么都不知道的摘要只出三行——用时占位 "--:--"、掉落占位 "--"(奖励段不是"解析成功")、固定的"未同步"说明;
    /// 没有评分、没有个人最佳、没有难度、没有击杀,奖励段"不适用"且没超时时也没有基础经验占位。
    /// </summary>
    [Fact]
    public void A_summary_with_nothing_known_prints_only_the_time_drops_and_note_placeholders()
    {
        AssertLines(
            Make(),
            zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn });
    }

    // ================================================================== 基础经验占位(两个条件各自独立)

    /// <summary>
    /// 保护的行为:没有可显示的基础经验时,"奖励解析失败"与"结算通知超时"两个条件**各自单独成立**都要出一行基础经验占位,
    /// 两者都不成立时不出这一行。行表里掉落行随奖励段状态变("解析成功且无掉落" → "无";其余 → "--")。
    /// </summary>
    [Theory]
    [InlineData(OnlineSettlementParseState.Failed, false, true, false)]        // 只有"奖励解析失败"成立
    [InlineData(OnlineSettlementParseState.NotApplicable, true, true, false)]  // 只有"超时"成立
    [InlineData(OnlineSettlementParseState.Succeeded, true, true, true)]       // 超时 + 奖励解析成功(但没有基础经验)
    [InlineData(OnlineSettlementParseState.Failed, true, true, false)]         // 两个都成立
    [InlineData(OnlineSettlementParseState.NotApplicable, false, false, false)] // 两个都不成立:奖励段不适用
    [InlineData(OnlineSettlementParseState.Succeeded, false, false, true)]     // 两个都不成立:奖励段解析成功
    public void The_missing_base_exp_placeholder_appears_when_either_reward_failed_or_timed_out(
        OnlineSettlementParseState rewardState, bool timedOut, bool placeholder, bool dropsNone)
    {
        OnlineSettlementSummary summary = Make(rewardState: rewardState, isTimedOut: timedOut);

        var zh = new List<string> { ClearTimeNoneZh };
        var en = new List<string> { ClearTimeNoneEn };
        if (placeholder)
        {
            zh.Add(BaseExpNoneZh);
            en.Add(BaseExpNoneEn);
        }
        zh.Add(dropsNone ? DropsNoneZh : DropsUnknownZh);
        en.Add(dropsNone ? DropsNoneEn : DropsUnknownEn);
        zh.Add(NoteZh);
        en.Add(NoteEn);

        AssertLines(summary, zh.ToArray(), en.ToArray());
    }

    /// <summary>
    /// 保护的行为:占位行的整句文案逐字固定("通关基础经验 --" / "CLEAR EXP --"),并且夹在用时行与掉落行之间的固定位置;
    /// 基础经验"有值但把握程度未知"时仍按"没有可显示的基础经验"处理(失败时照样出占位)。
    /// </summary>
    [Fact]
    public void The_placeholder_line_has_exact_text_and_an_unknown_confidence_base_exp_still_counts_as_missing()
    {
        OnlineSettlementSummary failed = Make(rewardState: OnlineSettlementParseState.Failed, baseExp: UnknownInt(777));
        AssertLines(
            failed,
            zh: new[] { "通关用时 --:--", "通关基础经验 --", "掉落物品: --", "提示: 物品/金币暂未同步至本地" },
            en: new[] { "CLEAR TIME --:--", "CLEAR EXP --", "DROPS: --", "NOTE: ITEMS/GOLD UNSYNCED" });
    }

    /// <summary>
    /// 保护的行为:基础经验可显示时只出"通关基础经验 +N"(含 0 与多位数),不再出占位——哪怕同时奖励解析失败且已超时。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    [InlineData(2147483647)]
    public void A_displayable_base_exp_prints_its_amount_and_never_the_placeholder(int amount)
    {
        string number = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OnlineSettlementSummary summary = Make(
            rewardState: OnlineSettlementParseState.Failed,
            isTimedOut: true,
            baseExp: Inferred(amount));

        AssertLines(
            summary,
            zh: new[] { ClearTimeNoneZh, "通关基础经验 +" + number, DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "CLEAR EXP +" + number, DropsUnknownEn, NoteEn });
    }

    // ================================================================== 掉落物与"其余掉落"

    /// <summary>
    /// 保护的行为:掉落物 6 件(超过展示上限 5)时只列前 5 件,第 6 件折进 "其余掉落 +1 项" / "MORE DROPS +1";
    /// 任务道具与普通掉落前缀不同("任务道具: " / "奖励物品: ",无字体 "QUEST ITEM: " / "DROP: ");查不到名字的掉落按"未知道具"占位并带数量。
    /// </summary>
    [Fact]
    public void Six_drops_show_five_items_and_one_overflow_line_with_the_remaining_count()
    {
        var drops = new[]
        {
            new OnlineSettlementDropItem(9901, 3, "Gem", isQuestItem: true),
            new OnlineSettlementDropItem(9902, 1, "Blade", isQuestItem: false),
            new OnlineSettlementDropItem(9903, 2, null, isQuestItem: false),
            new OnlineSettlementDropItem(9904, 1, null, isQuestItem: true),
            new OnlineSettlementDropItem(9905, 1, "Shield", isQuestItem: false),
            new OnlineSettlementDropItem(9906, 1, "Hidden", isQuestItem: false),
        };

        AssertLines(
            Make(rewardState: OnlineSettlementParseState.Succeeded, drops: drops),
            zh: new[]
            {
                ClearTimeNoneZh,
                "任务道具: Gem x3",
                "奖励物品: Blade",
                "奖励物品: 未知道具 x2",
                "任务道具: 未知道具",
                "奖励物品: Shield",
                "其余掉落 +1 项",
                NoteZh,
            },
            en: new[]
            {
                ClearTimeNoneEn,
                "QUEST ITEM: Gem x3",
                "DROP: Blade",
                "DROP: UNKNOWN ITEM x2",
                "QUEST ITEM: UNKNOWN ITEM",
                "DROP: Shield",
                "MORE DROPS +1",
                NoteEn,
            });
    }

    /// <summary>保护的行为:恰好等于展示上限(5 件)时不出"其余掉落"行;也不出"无/--"占位。</summary>
    [Fact]
    public void Exactly_the_display_limit_of_drops_adds_no_overflow_line()
    {
        Assert.Equal(5, OnlineSettlementOverlay.MaxDropsDisplayed);

        AssertLines(
            Make(rewardState: OnlineSettlementParseState.Succeeded, drops: PlainDrops(5)),
            zh: new[] { ClearTimeNoneZh, "奖励物品: Item1", "奖励物品: Item2", "奖励物品: Item3", "奖励物品: Item4", "奖励物品: Item5", NoteZh },
            en: new[] { ClearTimeNoneEn, "DROP: Item1", "DROP: Item2", "DROP: Item3", "DROP: Item4", "DROP: Item5", NoteEn });
    }

    /// <summary>保护的行为:少于上限(1 件、4 件)时按件数列出,不出"其余掉落"行。</summary>
    [Fact]
    public void Fewer_drops_than_the_limit_list_every_item_without_an_overflow_line()
    {
        AssertLines(
            Make(rewardState: OnlineSettlementParseState.Succeeded, drops: PlainDrops(1)),
            zh: new[] { ClearTimeNoneZh, "奖励物品: Item1", NoteZh },
            en: new[] { ClearTimeNoneEn, "DROP: Item1", NoteEn });

        AssertLines(
            Make(rewardState: OnlineSettlementParseState.Succeeded, drops: PlainDrops(4)),
            zh: new[] { ClearTimeNoneZh, "奖励物品: Item1", "奖励物品: Item2", "奖励物品: Item3", "奖励物品: Item4", NoteZh },
            en: new[] { ClearTimeNoneEn, "DROP: Item1", "DROP: Item2", "DROP: Item3", "DROP: Item4", NoteEn });
    }

    /// <summary>
    /// 保护的行为:剩余个数是"总数 - 5",逐位十进制(9 件 → +4;15 件 → +10 两位数);第 6 件起的物品名都不出现。
    /// </summary>
    [Theory]
    [InlineData(7, "2")]   // 7 - 5
    [InlineData(9, "4")]   // 9 - 5
    [InlineData(15, "10")] // 15 - 5,两位数
    public void The_overflow_line_carries_the_remaining_count_and_hides_the_later_items(int total, string remaining)
    {
        string[] zh =
        {
            ClearTimeNoneZh, "奖励物品: Item1", "奖励物品: Item2", "奖励物品: Item3", "奖励物品: Item4", "奖励物品: Item5",
            "其余掉落 +" + remaining + " 项", NoteZh,
        };
        string[] en =
        {
            ClearTimeNoneEn, "DROP: Item1", "DROP: Item2", "DROP: Item3", "DROP: Item4", "DROP: Item5",
            "MORE DROPS +" + remaining, NoteEn,
        };

        AssertLines(Make(rewardState: OnlineSettlementParseState.Succeeded, drops: PlainDrops(total)), zh, en);
    }

    /// <summary>
    /// 保护的行为:没有掉落物时,"奖励解析成功" → "掉落物品: 无" / "DROPS: NONE";其余状态(不适用、失败)→ "掉落物品: --" / "DROPS: --"。
    /// 有掉落物时这两句占位都不出。
    /// </summary>
    [Fact]
    public void An_empty_drop_list_prints_none_only_after_a_successful_reward_parse()
    {
        AssertLines(
            Make(rewardState: OnlineSettlementParseState.Succeeded),
            zh: new[] { ClearTimeNoneZh, "掉落物品: 无", NoteZh },
            en: new[] { ClearTimeNoneEn, "DROPS: NONE", NoteEn });

        AssertLines(
            Make(rewardState: OnlineSettlementParseState.NotApplicable),
            zh: new[] { ClearTimeNoneZh, "掉落物品: --", NoteZh },
            en: new[] { ClearTimeNoneEn, "DROPS: --", NoteEn });

        // 奖励解析失败:有基础经验占位,掉落仍是 "--"
        AssertLines(
            Make(rewardState: OnlineSettlementParseState.Failed),
            zh: new[] { ClearTimeNoneZh, "通关基础经验 --", "掉落物品: --", NoteZh },
            en: new[] { ClearTimeNoneEn, "CLEAR EXP --", "DROPS: --", NoteEn });
    }

    // ================================================================== 升级行

    /// <summary>
    /// 保护的行为:服务端经验刷新后升了级(<see cref="OnlineSettlementSummary.LevelsGained"/> &gt; 0)时,
    /// 在说明行之前出 "升级至 Lv.N" / "LEVELED UP TO N",N 是刷新后的等级(<see cref="OnlineSettlementSummary.LevelAfter"/>)。
    /// </summary>
    [Theory]
    [InlineData(2, 5, "5")]    // 升 3 级,到 5
    [InlineData(4, 5, "5")]    // 只升 1 级(门槛 &gt; 0 而不是 &gt; 1)
    [InlineData(9, 12, "12")]  // 两位数
    public void A_level_up_adds_a_line_with_the_level_after_just_before_the_note(int before, int after, string shown)
    {
        OnlineSettlementSummary summary = Make();
        summary.MarkExpApplied(900, before, after);
        Assert.True(summary.LevelsGained > 0);

        AssertLines(
            summary,
            zh: new[] { ClearTimeNoneZh, DropsUnknownZh, "升级至 Lv." + shown, NoteZh },
            en: new[] { ClearTimeNoneEn, DropsUnknownEn, "LEVELED UP TO " + shown, NoteEn });
    }

    /// <summary>
    /// 保护的行为:没升级(等级没变、等级反而变低、从没记过经验刷新)时不出升级行。
    /// </summary>
    [Fact]
    public void No_level_gain_means_no_level_up_line()
    {
        string[] zh = { ClearTimeNoneZh, DropsUnknownZh, NoteZh };
        string[] en = { ClearTimeNoneEn, DropsUnknownEn, NoteEn };

        // 从没记过经验刷新:LevelAfter 与 LevelBefore 都是 0
        AssertLines(Make(), zh, en);

        // 刷新过但等级没变
        OnlineSettlementSummary same = Make();
        same.MarkExpApplied(100, 3, 3);
        Assert.Equal(0, same.LevelsGained);
        AssertLines(same, zh, en);

        // 刷新后等级反而更低(不会发生,但"升了几级"不能是负数,也不出升级行)
        OnlineSettlementSummary lower = Make();
        lower.MarkExpApplied(100, 5, 3);
        Assert.Equal(0, lower.LevelsGained);
        AssertLines(lower, zh, en);
    }

    // ================================================================== 评分、用时、最佳、难度、击杀

    /// <summary>
    /// 保护的行为:有评分且还没把字母画成图时,第一行是文字评分 "评分 X" / "RANK X"(X 原样取评分字母);
    /// 字母已经画成图、或根本没有评分时,这一行不出。
    /// </summary>
    [Fact]
    public void The_rank_text_line_appears_only_for_a_rank_that_was_not_drawn_as_a_letter_sprite()
    {
        OnlineSettlementSummary ranked = Make(hasRank: true, rankLetter: "SSS");

        AssertLines(
            ranked,
            zh: new[] { "评分 SSS", ClearTimeNoneZh, DropsUnknownZh, NoteZh },
            en: new[] { "RANK SSS", ClearTimeNoneEn, DropsUnknownEn, NoteEn });

        AssertLines(
            ranked,
            zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn },
            rankLetterDrawn: true);

        // 没有评分:即使评分字母字段有内容也不出
        AssertLines(
            Make(hasRank: false, rankLetter: "SSS"),
            zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn });
    }

    /// <summary>
    /// 保护的行为:通关用时可显示就出 "通关用时 MM:SS" / "CLEAR TIME MM:SS"(0 毫秒也显示 00:00;分钟不折成小时),
    /// 不可显示(缺失或把握程度未知)出占位 "--:--"。
    /// </summary>
    [Fact]
    public void The_clear_time_line_formats_minutes_and_seconds_or_falls_back_to_the_placeholder()
    {
        // 0 毫秒:00:00
        AssertLines(
            Make(clearTime: Observed(0)),
            zh: new[] { "通关用时 00:00", DropsUnknownZh, NoteZh },
            en: new[] { "CLEAR TIME 00:00", DropsUnknownEn, NoteEn });

        // 3661000 毫秒 = 3661 秒 = 61 分 01 秒(分钟不折成小时)
        AssertLines(
            Make(clearTime: Inferred(3661000)),
            zh: new[] { "通关用时 61:01", DropsUnknownZh, NoteZh },
            en: new[] { "CLEAR TIME 61:01", DropsUnknownEn, NoteEn });

        // 有值但把握程度未知:按缺失处理,出占位
        AssertLines(
            Make(clearTime: UnknownInt(45000)),
            zh: new[] { "通关用时 --:--", DropsUnknownZh, NoteZh },
            en: new[] { "CLEAR TIME --:--", DropsUnknownEn, NoteEn });
    }

    /// <summary>
    /// 保护的行为:个人最佳可显示就出 "个人最佳 MM:SS" / "BEST TIME MM:SS";只有"新纪录"字段可显示且为真时才追加 " [新纪录!]" / " [NEW RECORD!]";
    /// 个人最佳不可显示时整行不出(即使新纪录为真)。
    /// </summary>
    [Fact]
    public void The_best_time_line_appends_the_new_record_tag_only_when_the_flag_is_displayable_and_true()
    {
        // 98000 毫秒 = 98 秒 = 01:38
        AssertLines(
            Make(clearTime: Observed(61000), bestTime: Inferred(98000), newRecord: Flag(true)),
            zh: new[] { "通关用时 01:01", "个人最佳 01:38 [新纪录!]", DropsUnknownZh, NoteZh },
            en: new[] { "CLEAR TIME 01:01", "BEST TIME 01:38 [NEW RECORD!]", DropsUnknownEn, NoteEn });

        // 新纪录为假 / 缺失 / 把握程度未知:都不追加标签
        foreach (OnlineSettlementField<bool> noTag in new[]
        {
            Flag(false),
            default(OnlineSettlementField<bool>),
            OnlineSettlementField<bool>.Unknown(true, SettlementFieldSource.Server),
        })
        {
            AssertLines(
                Make(clearTime: Observed(61000), bestTime: Inferred(98000), newRecord: noTag),
                zh: new[] { "通关用时 01:01", "个人最佳 01:38", DropsUnknownZh, NoteZh },
                en: new[] { "CLEAR TIME 01:01", "BEST TIME 01:38", DropsUnknownEn, NoteEn });
        }

        // 个人最佳缺失:没有这一行,新纪录标签也跟着不出
        AssertLines(
            Make(clearTime: Observed(61000), newRecord: Flag(true)),
            zh: new[] { "通关用时 01:01", DropsUnknownZh, NoteZh },
            en: new[] { "CLEAR TIME 01:01", DropsUnknownEn, NoteEn });
    }

    /// <summary>
    /// 保护的行为:难度大于 0 才出 "难度 名字" / "DIFFICULTY 名字",名字取难度名表(越界是 "?");0 与负数不出。
    /// </summary>
    [Fact]
    public void The_difficulty_line_names_the_difficulty_only_when_it_is_positive()
    {
        for (int difficulty = 1; difficulty <= 4; difficulty++)
        {
            string name = DifficultyNames.Of(difficulty);
            AssertLines(
                Make(difficulty: difficulty),
                zh: new[] { ClearTimeNoneZh, "难度 " + name, DropsUnknownZh, NoteZh },
                en: new[] { ClearTimeNoneEn, "DIFFICULTY " + name, DropsUnknownEn, NoteEn });
        }

        // 越界的难度号:名字是问号
        AssertLines(
            Make(difficulty: 99),
            zh: new[] { ClearTimeNoneZh, "难度 ?", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "DIFFICULTY ?", DropsUnknownEn, NoteEn });

        foreach (int none in new[] { 0, -1 })
        {
            AssertLines(
                Make(difficulty: none),
                zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
                en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn });
        }
    }

    /// <summary>
    /// 保护的行为:击杀数可显示就出 "击杀 N" / "KILLS N";有丢弃的击杀(&gt; 0)时追加 " (未计 M)" / " (DROPPED M)";
    /// 击杀数缺失时整行不出(即使有丢弃数)。
    /// </summary>
    [Fact]
    public void The_kills_line_appends_the_dropped_count_only_when_positive_and_vanishes_without_kills()
    {
        // 0 击杀也要显示
        AssertLines(
            Make(kills: Observed(0)),
            zh: new[] { ClearTimeNoneZh, "击杀 0", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "KILLS 0", DropsUnknownEn, NoteEn });

        AssertLines(
            Make(kills: Observed(23), droppedKills: 4),
            zh: new[] { ClearTimeNoneZh, "击杀 23 (未计 4)", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "KILLS 23 (DROPPED 4)", DropsUnknownEn, NoteEn });

        AssertLines(
            Make(kills: Observed(23), droppedKills: 0),
            zh: new[] { ClearTimeNoneZh, "击杀 23", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "KILLS 23", DropsUnknownEn, NoteEn });

        AssertLines(
            Make(droppedKills: 6),
            zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn });
    }

    // ================================================================== 经验加成、标志、金币

    /// <summary>
    /// 保护的行为:评分加成经验只在"可显示且大于 0"时出 "评分加成经验 +N" / "RANK BONUS +N";0、负数、把握程度未知都不出。
    /// </summary>
    [Fact]
    public void The_rank_bonus_line_needs_a_displayable_positive_amount()
    {
        AssertLines(
            Make(rankBonus: Inferred(300)),
            zh: new[] { ClearTimeNoneZh, "评分加成经验 +300", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "RANK BONUS +300", DropsUnknownEn, NoteEn });

        foreach (OnlineSettlementField<int> hidden in new[] { Inferred(0), Inferred(-5), UnknownInt(300), default(OnlineSettlementField<int>) })
        {
            AssertLines(
                Make(rankBonus: hidden),
                zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
                en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn });
        }
    }

    /// <summary>
    /// 保护的行为:具名加成经验最多列 4 条(<see cref="OnlineSettlementOverlay.MaxBonusExpDisplayed"/>),跳过不可显示的分项(把握程度未知、数额不为正),
    /// 被跳过的不占名额;标签沿用分项自己的有字体/无字体文案,格式 "标签 +数额"。
    /// </summary>
    [Fact]
    public void Named_bonus_exp_lines_are_capped_at_four_and_skip_non_displayable_entries()
    {
        Assert.Equal(4, OnlineSettlementOverlay.MaxBonusExpDisplayed);

        var named = new[]
        {
            new OnlineSettlementExpLine(3, "avatar", 0, SettlementFieldConfidence.Inferred),    // 数额 0:跳过
            new OnlineSettlementExpLine(4, "event", 80, SettlementFieldConfidence.Unknown),     // 未知:跳过
            new OnlineSettlementExpLine(5, "item", 60, SettlementFieldConfidence.Inferred),     // 第 1 条
            new OnlineSettlementExpLine(6, "privilege", 40, SettlementFieldConfidence.Observed), // 第 2 条
            new OnlineSettlementExpLine(8, "channel", 20, SettlementFieldConfidence.Inferred),  // 第 3 条
            new OnlineSettlementExpLine(10, "creature", 10, SettlementFieldConfidence.Inferred), // 第 4 条
            new OnlineSettlementExpLine(11, "return_user", 5, SettlementFieldConfidence.Inferred), // 超出 4 条:不出
        };

        AssertLines(
            Make(named: named),
            zh: new[]
            {
                ClearTimeNoneZh, "道具加成经验 +60", "特权加成经验 +40", "频道加成经验 +20", "宠物加成经验 +10", DropsUnknownZh, NoteZh,
            },
            en: new[]
            {
                ClearTimeNoneEn, "ITEM EXP +60", "PRIVILEGE EXP +40", "CHANNEL EXP +20", "CREATURE EXP +10", DropsUnknownEn, NoteEn,
            });
    }

    /// <summary>
    /// 保护的行为:死亡经验惩罚与回归用户队伍两个标志,只在"可显示且为真"时各出一行固定文案
    /// ("已计死亡经验惩罚" / "DEAD PENALTY APPLIED"、"回归用户队伍" / "RETURN USER PARTY");为假、缺失、把握程度未知都不出。
    /// </summary>
    [Fact]
    public void The_dead_penalty_and_return_party_lines_need_a_displayable_true_flag()
    {
        AssertLines(
            Make(deadPenalty: OnlineSettlementField<bool>.Inferred(true, SettlementFieldSource.Server), returnUser: Flag(true)),
            zh: new[] { ClearTimeNoneZh, "已计死亡经验惩罚", "回归用户队伍", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "DEAD PENALTY APPLIED", "RETURN USER PARTY", DropsUnknownEn, NoteEn });

        // 只有死亡惩罚
        AssertLines(
            Make(deadPenalty: Flag(true), returnUser: Flag(false)),
            zh: new[] { ClearTimeNoneZh, "已计死亡经验惩罚", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "DEAD PENALTY APPLIED", DropsUnknownEn, NoteEn });

        // 只有回归用户队伍
        AssertLines(
            Make(deadPenalty: Flag(false), returnUser: Flag(true)),
            zh: new[] { ClearTimeNoneZh, "回归用户队伍", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "RETURN USER PARTY", DropsUnknownEn, NoteEn });

        // 把握程度未知的"真"不显示
        AssertLines(
            Make(
                deadPenalty: OnlineSettlementField<bool>.Unknown(true, SettlementFieldSource.Server),
                returnUser: OnlineSettlementField<bool>.Unknown(true, SettlementFieldSource.Server)),
            zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn });
    }

    /// <summary>
    /// 保护的行为:奖励金币可显示就出 "奖励金币(推) +N" / "REWARD GOLD(EST) +N"(0 也显示);缺失或把握程度未知不出。
    /// </summary>
    [Fact]
    public void The_reward_gold_line_is_estimated_and_shown_even_for_zero()
    {
        AssertLines(
            Make(gold: Inferred(640)),
            zh: new[] { ClearTimeNoneZh, "奖励金币(推) +640", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "REWARD GOLD(EST) +640", DropsUnknownEn, NoteEn });

        AssertLines(
            Make(gold: Inferred(0)),
            zh: new[] { ClearTimeNoneZh, "奖励金币(推) +0", DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, "REWARD GOLD(EST) +0", DropsUnknownEn, NoteEn });

        AssertLines(
            Make(gold: UnknownInt(640)),
            zh: new[] { ClearTimeNoneZh, DropsUnknownZh, NoteZh },
            en: new[] { ClearTimeNoneEn, DropsUnknownEn, NoteEn });
    }

    // ================================================================== 全部分支一起

    /// <summary>
    /// 保护的行为:所有行同时出现时的整张列表与顺序(评分 → 用时 → 最佳 → 难度 → 击杀 → 基础经验 → 评分加成 → 具名加成 → 死亡惩罚 → 回归队伍 →
    /// 金币 → 掉落 → 其余掉落 → 升级 → 说明),有字体 / 无字体两个版本逐行手写;字母已画成图时只少第一行。
    /// </summary>
    [Fact]
    public void Every_line_together_keeps_its_exact_text_and_order_in_both_languages()
    {
        var named = new[]
        {
            new OnlineSettlementExpLine(3, "avatar", 120, SettlementFieldConfidence.Inferred),
            new OnlineSettlementExpLine(4, "event", 80, SettlementFieldConfidence.Inferred),
            new OnlineSettlementExpLine(5, "item", 60, SettlementFieldConfidence.Inferred),
            new OnlineSettlementExpLine(6, "privilege", 40, SettlementFieldConfidence.Inferred),
            new OnlineSettlementExpLine(8, "channel", 20, SettlementFieldConfidence.Inferred), // 第 5 条:被上限截掉
        };
        var drops = new[]
        {
            new OnlineSettlementDropItem(9901, 3, "Gem", isQuestItem: true),
            new OnlineSettlementDropItem(9902, 1, "Blade", isQuestItem: false),
            new OnlineSettlementDropItem(9903, 2, null, isQuestItem: false),
            new OnlineSettlementDropItem(9904, 1, null, isQuestItem: true),
            new OnlineSettlementDropItem(9905, 1, "Shield", isQuestItem: false),
            new OnlineSettlementDropItem(9906, 1, "Hidden", isQuestItem: false),
        };
        OnlineSettlementSummary summary = Make(
            hasRank: true,
            rankLetter: "S",
            difficulty: 2,
            rewardState: OnlineSettlementParseState.Succeeded,
            clearTime: Inferred(125000),   // 125 秒 = 02:05
            bestTime: Inferred(98000),     // 98 秒 = 01:38
            newRecord: Flag(true),
            kills: Observed(23),
            droppedKills: 4,
            baseExp: Inferred(1500),
            rankBonus: Inferred(300),
            named: named,
            deadPenalty: Flag(true),
            returnUser: Flag(true),
            gold: Inferred(640),
            drops: drops);
        summary.MarkExpApplied(1800, 3, 7);

        string difficultyName = DifficultyNames.Of(2);
        string[] zh =
        {
            "评分 S",
            "通关用时 02:05",
            "个人最佳 01:38 [新纪录!]",
            "难度 " + difficultyName,
            "击杀 23 (未计 4)",
            "通关基础经验 +1500",
            "评分加成经验 +300",
            "装扮加成经验 +120",
            "活动加成经验 +80",
            "道具加成经验 +60",
            "特权加成经验 +40",
            "已计死亡经验惩罚",
            "回归用户队伍",
            "奖励金币(推) +640",
            "任务道具: Gem x3",
            "奖励物品: Blade",
            "奖励物品: 未知道具 x2",
            "任务道具: 未知道具",
            "奖励物品: Shield",
            "其余掉落 +1 项",
            "升级至 Lv.7",
            NoteZh,
        };
        string[] en =
        {
            "RANK S",
            "CLEAR TIME 02:05",
            "BEST TIME 01:38 [NEW RECORD!]",
            "DIFFICULTY " + difficultyName,
            "KILLS 23 (DROPPED 4)",
            "CLEAR EXP +1500",
            "RANK BONUS +300",
            "AVATAR EXP +120",
            "EVENT EXP +80",
            "ITEM EXP +60",
            "PRIVILEGE EXP +40",
            "DEAD PENALTY APPLIED",
            "RETURN USER PARTY",
            "REWARD GOLD(EST) +640",
            "QUEST ITEM: Gem x3",
            "DROP: Blade",
            "DROP: UNKNOWN ITEM x2",
            "QUEST ITEM: UNKNOWN ITEM",
            "DROP: Shield",
            "MORE DROPS +1",
            "LEVELED UP TO 7",
            NoteEn,
        };

        AssertLines(summary, zh, en);
        AssertLines(summary, zh.Skip(1).ToArray(), en.Skip(1).ToArray(), rankLetterDrawn: true);
    }

    /// <summary>
    /// 保护的行为:说明行永远是最后一行,固定文案 "提示: 物品/金币暂未同步至本地" / "NOTE: ITEMS/GOLD UNSYNCED"
    /// (有字体 / 无字体都逐字);任何输入下它都在,而且只出现一次。
    /// </summary>
    [Fact]
    public void The_unsynced_note_is_always_the_single_last_line()
    {
        var summaries = new[]
        {
            Make(),
            Make(rewardState: OnlineSettlementParseState.Succeeded, drops: PlainDrops(8)),
            Make(rewardState: OnlineSettlementParseState.Failed, isTimedOut: true, hasRank: true, rankLetter: "B"),
        };
        foreach (OnlineSettlementSummary summary in summaries)
        {
            List<string> zh = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: true, rankLetterDrawn: false);
            List<string> en = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: false, rankLetterDrawn: false);
            Assert.Equal("提示: 物品/金币暂未同步至本地", zh[zh.Count - 1]);
            Assert.Equal("NOTE: ITEMS/GOLD UNSYNCED", en[en.Count - 1]);
            Assert.Single(zh, l => l.StartsWith("提示", StringComparison.Ordinal));
            Assert.Single(en, l => l.StartsWith("NOTE", StringComparison.Ordinal));
        }
    }

    // ================================================================== 时间格式

    /// <summary>
    /// 保护的行为:<see cref="OnlineSettlementOverlay.FormatTimeMs"/> 的分、秒各自补零到两位,毫秒向下取整到秒,负值夹到 0,分钟不折小时。
    /// </summary>
    [Theory]
    [InlineData(-1, "00:00")]
    [InlineData(0, "00:00")]
    [InlineData(999, "00:00")]    // 不足一秒:向下取整
    [InlineData(1000, "00:01")]
    [InlineData(9999, "00:09")]
    [InlineData(10000, "00:10")]
    [InlineData(59999, "00:59")]
    [InlineData(60000, "01:00")]
    [InlineData(65000, "01:05")]
    [InlineData(599000, "09:59")]
    [InlineData(600000, "10:00")]
    [InlineData(3661000, "61:01")] // 61 分 01 秒
    public void FormatTimeMs_pads_both_fields_and_floors_to_seconds(int ms, string expected)
    {
        Assert.Equal(expected, OnlineSettlementOverlay.FormatTimeMs(ms));
    }
}
