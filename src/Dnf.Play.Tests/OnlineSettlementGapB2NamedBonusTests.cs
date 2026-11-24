using Kit = Dnf.Play.Tests.OnlineSettlementGapBKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 结算摘要里"具名加成经验"清单与"有把握经验增量"的补测(区域 B,第二轮):
/// 经验值表下标 3..15 的 13 个具名槽——键、两个版本的精确标签、取 0/负数的槽不入列表、列表顺序、
/// 经验表长度(0/1/2/3/4/5/15/16/17/20)决定哪些槽存在,以及增量只由基础经验与评分加成相加(夹取)得出。
/// 全部合成数据,期望值手算。
/// </summary>
public sealed class OnlineSettlementGapB2NamedBonusTests
{
    // 具名加成表:下标 → (键, 有字体标题, 无字体标题)
    private static readonly (int Index, string Key, string Zh, string En)[] Named =
    {
        (3, "avatar", "装扮加成经验", "AVATAR EXP"),
        (4, "event", "活动加成经验", "EVENT EXP"),
        (5, "item", "道具加成经验", "ITEM EXP"),
        (6, "privilege", "特权加成经验", "PRIVILEGE EXP"),
        (7, "pcroom_party", "网吧队友加成", "PCROOM EXP"),
        (8, "channel", "频道加成经验", "CHANNEL EXP"),
        (9, "upper_member", "上位队员经验", "UPPER EXP"),
        (10, "creature", "宠物加成经验", "CREATURE EXP"),
        (11, "return_user", "回归队伍加成", "RETURN EXP"),
        (12, "new_char", "新角色活动加成", "NEWCHAR EXP"),
        (13, "manage", "管理加成经验", "MANAGE EXP"),
        (14, "growth_power", "成长力经验", "GROWTH EXP"),
        (15, "premium_extra", "特权附加经验", "PREMIUM EXP"),
    };

    public static IEnumerable<object[]> NamedRows()
    {
        foreach (var row in Named)
        {
            yield return new object[] { row.Index, row.Key, row.Zh, row.En };
        }
    }

    private static OnlineSettlementSummary BuildWith(int[] expValues)
    {
        return OnlineSettlementSummaryBuilder.Build(Kit.Data(Kit.Reward(expValues: expValues)));
    }

    /// <summary>保护的行为:每个具名槽单独取正数时,恰好列出这一条:下标、键、数量、"推断"把握程度、可显示,两个版本的标签逐字精确。</summary>
    [Theory]
    [MemberData(nameof(NamedRows))]
    public void Each_named_slot_alone_yields_one_line_with_its_key_and_exact_labels(int index, string key, string zh, string en)
    {
        // 基础 1000、评分加成 200、未知槽 555 照常;只有下标 index 取 300 + index,其余具名槽为 0
        var exp = new int[16];
        exp[0] = 1000;
        exp[1] = 200;
        exp[2] = 555;
        exp[index] = 300 + index;

        OnlineSettlementSummary summary = BuildWith(exp);

        OnlineSettlementExpLine line = Assert.Single(summary.NamedBonusExpLines);
        Assert.Equal(index, line.Index);
        Assert.Equal(key, line.Key);
        Assert.Equal(300 + index, line.Amount);
        Assert.Equal(SettlementFieldConfidence.Inferred, line.Confidence);
        Assert.True(line.ShouldDisplay);
        Assert.Equal(zh + " +" + (300 + index), line.DisplayLabel(hasFonts: true));
        Assert.Equal(en + " +" + (300 + index), line.DisplayLabel(hasFonts: false));
        Assert.Equal(1200, summary.ConfidentExpGrant); // 1000 + 200,具名槽不进增量
    }

    /// <summary>
    /// 保护的行为:满 16 项且全为正时,13 个具名槽按下标升序全部列出(各自的键与标签对号),
    /// 基础/评分加成/未知槽(下标 0/1/2)不进具名清单,各自走自己的字段。
    /// </summary>
    [Fact]
    public void A_full_sixteen_entry_table_lists_all_thirteen_named_slots_in_slot_order()
    {
        // 第 i 项 = 100 + 7 * i:100,107,114,121,128,135,142,149,156,163,170,177,184,191,198,205
        var exp = new int[16];
        for (int i = 0; i < exp.Length; i++)
        {
            exp[i] = 100 + 7 * i;
        }
        int[] expectedAmounts = { 121, 128, 135, 142, 149, 156, 163, 170, 177, 184, 191, 198, 205 };

        OnlineSettlementSummary summary = BuildWith(exp);

        Assert.Equal(100, summary.BaseExp.Value);
        Assert.Equal(107, summary.RankBonusExp.Value);
        Assert.Equal(114, summary.UnknownExpSlot2.Value);
        Assert.Equal(207, summary.ConfidentExpGrant); // 100 + 107
        Assert.Equal(13, summary.NamedBonusExpLines.Count);
        for (int n = 0; n < Named.Length; n++)
        {
            OnlineSettlementExpLine line = summary.NamedBonusExpLines[n];
            Assert.Equal(Named[n].Index, line.Index);
            Assert.Equal(Named[n].Key, line.Key);
            Assert.Equal(expectedAmounts[n], line.Amount);
            Assert.Equal(SettlementFieldConfidence.Inferred, line.Confidence);
            Assert.Equal(Named[n].Zh + " +" + expectedAmounts[n], line.DisplayLabel(hasFonts: true));
            Assert.Equal(Named[n].En + " +" + expectedAmounts[n], line.DisplayLabel(hasFonts: false));
        }
    }

    /// <summary>
    /// 保护的行为:具名加成里值为 0 的项不入清单(值为负的也不入),只列正数项,且保持槽位升序;
    /// 基础经验/评分加成/未知槽不受影响。
    /// </summary>
    [Fact]
    public void Zero_and_negative_named_slots_are_left_out_and_the_positive_ones_keep_slot_order()
    {
        // 下标:  0     1    2   3  4   5  6  7   8  9   10 11 12  13 14   15
        int[] exp = { 1000, 200, 555, 0, 40, 0, 0, 70, -5, 90, 0, 0, 12, 0, 140, 0 };

        OnlineSettlementSummary summary = BuildWith(exp);

        var lines = summary.NamedBonusExpLines;
        Assert.Equal(5, lines.Count);
        Assert.Equal((4, "event", 40), (lines[0].Index, lines[0].Key, lines[0].Amount));
        Assert.Equal((7, "pcroom_party", 70), (lines[1].Index, lines[1].Key, lines[1].Amount));
        Assert.Equal((9, "upper_member", 90), (lines[2].Index, lines[2].Key, lines[2].Amount));
        Assert.Equal((12, "new_char", 12), (lines[3].Index, lines[3].Key, lines[3].Amount));
        Assert.Equal((14, "growth_power", 140), (lines[4].Index, lines[4].Key, lines[4].Amount));
        Assert.Equal("活动加成经验 +40", lines[0].DisplayLabel(hasFonts: true));
        Assert.Equal("PCROOM EXP +70", lines[1].DisplayLabel(hasFonts: false));
        Assert.Equal("上位队员经验 +90", lines[2].DisplayLabel(hasFonts: true));
        Assert.Equal("NEWCHAR EXP +12", lines[3].DisplayLabel(hasFonts: false));
        Assert.Equal("成长力经验 +140", lines[4].DisplayLabel(hasFonts: true));
        Assert.All(lines, l => Assert.True(l.Amount > 0 && l.ShouldDisplay));

        Assert.Equal(1000, summary.BaseExp.Value);
        Assert.Equal(200, summary.RankBonusExp.Value);
        Assert.Equal(555, summary.UnknownExpSlot2.Value);
        Assert.Equal(1200, summary.ConfidentExpGrant);
    }

    /// <summary>保护的行为:十三个具名槽全是 0(或全为负)时清单为空——0 不是"有加成";基础经验仍照常。</summary>
    [Fact]
    public void All_zero_or_all_negative_named_slots_produce_no_lines()
    {
        var zeros = new int[16];
        zeros[0] = 1000;
        zeros[1] = 200;
        zeros[2] = 555;
        OnlineSettlementSummary zero = BuildWith(zeros);
        Assert.Empty(zero.NamedBonusExpLines);
        Assert.Equal(1000, zero.BaseExp.Value);
        Assert.Equal(1200, zero.ConfidentExpGrant);

        var negatives = new int[16];
        for (int i = 0; i < negatives.Length; i++)
        {
            negatives[i] = i < 3 ? 10 * (i + 1) : -i;
        }
        OnlineSettlementSummary negative = BuildWith(negatives);
        Assert.Empty(negative.NamedBonusExpLines);
        Assert.Equal(10, negative.BaseExp.Value);
        Assert.Equal(30, negative.ConfidentExpGrant);

        // 只有第一个具名槽(下标 3)为正、其余全 0:恰好一条
        var onlyFirst = new int[16];
        onlyFirst[3] = 1;
        OnlineSettlementExpLine only = Assert.Single(BuildWith(onlyFirst).NamedBonusExpLines);
        Assert.Equal(3, only.Index);
        Assert.Equal(1, only.Amount);

        // 只有最后一个具名槽(下标 15)为正、其余全 0:恰好一条
        var onlyLast = new int[16];
        onlyLast[15] = int.MaxValue;
        OnlineSettlementExpLine last = Assert.Single(BuildWith(onlyLast).NamedBonusExpLines);
        Assert.Equal(15, last.Index);
        Assert.Equal("premium_extra", last.Key);
        Assert.Equal(int.MaxValue, last.Amount);
        Assert.Equal("PREMIUM EXP +2147483647", last.DisplayLabel(hasFonts: false));
    }

    /// <summary>
    /// 保护的行为:经验值表长度决定哪些具名槽存在——0/1/2/3 项没有具名槽,4 项只有下标 3,
    /// 5 项有 3、4,15 项到下标 14,16 项到下标 15,超过 16 项时多出的部分(下标 16 起)被忽略、不越界。
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(15, 12)]
    [InlineData(16, 13)]
    [InlineData(17, 13)]
    [InlineData(20, 13)]
    public void The_exp_table_length_decides_which_named_slots_exist(int length, int expectedCount)
    {
        // 第 i 项 = 100 + i;下标 16 起故意放很大的数(9000 + i),若被误当具名槽或越界读会暴露
        var exp = new int[length];
        for (int i = 0; i < length; i++)
        {
            exp[i] = i < 16 ? 100 + i : 9000 + i;
        }

        OnlineSettlementSummary summary = BuildWith(exp);

        Assert.Equal(expectedCount, summary.NamedBonusExpLines.Count);
        for (int n = 0; n < expectedCount; n++)
        {
            OnlineSettlementExpLine line = summary.NamedBonusExpLines[n];
            Assert.Equal(Named[n].Index, line.Index);
            Assert.Equal(Named[n].Key, line.Key);
            Assert.Equal(100 + Named[n].Index, line.Amount);
        }
        // 下标 0/1/2 各自的字段:有几项就有几项
        Assert.Equal(length > 0, summary.BaseExp.HasValue);
        Assert.Equal(length > 1, summary.RankBonusExp.HasValue);
        Assert.Equal(length > 2, summary.UnknownExpSlot2.HasValue);
    }

    /// <summary>保护的行为:恰好 3 项时,哪怕未知槽(下标 2)是正数也没有具名清单;恰好 4 项时下标 3 取 0 不入清单、取正数入清单。</summary>
    [Fact]
    public void Three_and_four_entry_tables_only_list_slot_three_when_it_is_positive()
    {
        OnlineSettlementSummary three = BuildWith(new[] { 1000, 200, 777 });
        Assert.Empty(three.NamedBonusExpLines);
        Assert.Equal(777, three.UnknownExpSlot2.Value);
        Assert.Equal(1200, three.ConfidentExpGrant);

        OnlineSettlementSummary fourZero = BuildWith(new[] { 1000, 200, 777, 0 });
        Assert.Empty(fourZero.NamedBonusExpLines);

        OnlineSettlementSummary fourNegative = BuildWith(new[] { 1000, 200, 777, -1 });
        Assert.Empty(fourNegative.NamedBonusExpLines);

        OnlineSettlementSummary fourOne = BuildWith(new[] { 1000, 200, 777, 1 });
        OnlineSettlementExpLine line = Assert.Single(fourOne.NamedBonusExpLines);
        Assert.Equal((3, "avatar", 1), (line.Index, line.Key, line.Amount));
        Assert.Equal("装扮加成经验 +1", line.DisplayLabel(hasFonts: true));
        Assert.Equal("AVATAR EXP +1", line.DisplayLabel(hasFonts: false));
        Assert.Equal(1200, fourOne.ConfidentExpGrant);
    }

    /// <summary>保护的行为:没有经验块(奖励段"不适用")时没有具名清单,也不是 null(空表)。</summary>
    [Fact]
    public void Without_a_reward_block_the_named_list_is_an_empty_list_not_null()
    {
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(Kit.Data(null));
        Assert.NotNull(summary.NamedBonusExpLines);
        Assert.Empty(summary.NamedBonusExpLines);
    }

    /// <summary>
    /// 保护的行为:有把握增量 = 服务端基础经验(正才算)+ 评分加成(正才算),负数当 0,
    /// 和夹在 <c>int.MaxValue</c>;具名槽与未知槽的巨额数值绝不并入。
    /// </summary>
    [Theory]
    [InlineData(1000, 200, 1200, 1000)]
    [InlineData(-50, 300, 300, 0)]
    [InlineData(400, -7, 400, 400)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(-1, -1, 0, 0)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(0, 1, 1, 0)]
    [InlineData(int.MaxValue, 5, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue - 1, 1, int.MaxValue, int.MaxValue - 1)]
    [InlineData(int.MaxValue - 1, 0, int.MaxValue - 1, int.MaxValue - 1)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(2000000000, 2000000000, int.MaxValue, 2000000000)]
    public void The_confident_grant_adds_only_positive_base_and_rank_bonus_and_clamps(int baseExp, int rankBonus, int expected, int expectedBaseOnly)
    {
        // 下标 2 与 3..15 都放 9000(若被并入,总和会远超期望)
        var exp = new int[16];
        exp[0] = baseExp;
        exp[1] = rankBonus;
        for (int i = 2; i < exp.Length; i++)
        {
            exp[i] = 9000;
        }

        Assert.Equal(expected, BuildWith(exp).ConfidentExpGrant);

        // 只有基础经验一项:评分加成缺省 0
        Assert.Equal(expectedBaseOnly, BuildWith(new[] { baseExp }).ConfidentExpGrant);
    }
}
