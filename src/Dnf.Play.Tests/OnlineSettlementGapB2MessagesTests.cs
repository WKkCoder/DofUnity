namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 结算页提示与标签文案的"逐种类、逐版本"精确文本补测(区域 B,第二轮):
/// 提示的每一种(含带错误码与不带错误码的拒绝)× 有字体/无字体两个版本的精确文本与语气,
/// 自定义文案的优先级,未知道具名,以及加成标签的默认值。
/// 期望文本全部是手抄的字面量(不从被测代码里取);提示种类用枚举全集核对,新增了种类而表里漏行会立刻失败。
/// </summary>
public sealed class OnlineSettlementGapB2MessagesTests
{
    // ---- 提示种类表:种类 → (有字体文案, 无字体文案, 语气) ----

    private static readonly (OnlineSettlementNoticeKind Kind, string Zh, string En, ShellTone Tone)[] NoticeTable =
    {
        (OnlineSettlementNoticeKind.None, "", "", ShellTone.Error),
        (OnlineSettlementNoticeKind.Advancing, "正在推进结算状态，请稍候…", "ADVANCING SETTLEMENT...", ShellTone.Wait),
        (OnlineSettlementNoticeKind.CardSelectSkipped, "无翻牌资格，等待服务端推进结算菜单", "NO CARD RIGHT; WAITING SERVER MENU", ShellTone.Info),
        (OnlineSettlementNoticeKind.AdvanceNoReply, "结算推进未收到预期回应，可安全返回城镇", "SETTLE ADVANCE NO REPLY; RETURN AVAILABLE", ShellTone.Error),
        (OnlineSettlementNoticeKind.WaitingReturn, "已发送结算选择，正在等待服务端回城…", "WAITING SERVER RETURN...", ShellTone.Wait),
        (OnlineSettlementNoticeKind.SettlementTimeout, "等待结算通知超时，可选择返回城镇", "SETTLEMENT NOTICE TIMED OUT; RETURN TO TOWN", ShellTone.Error),
        (OnlineSettlementNoticeKind.EplpRejectedNotLeader, "仅队长可操作结算菜单，请选择返回城镇", "MENU REJECTED: NOT PARTY LEADER (8)", ShellTone.Error),
        (OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen, "神秘商店处于打开状态，请先关闭或返回城镇", "MENU REJECTED: SECRET SHOP OPEN (0x85)", ShellTone.Error),
        (OnlineSettlementNoticeKind.EplpRejectedInvalidCommand, "服务端判定结算菜单命令无效，已回退安全选项", "MENU REJECTED: INVALID COMMAND (1)", ShellTone.Error),
        // 通用拒绝(没带错误码):不出现括号
        (OnlineSettlementNoticeKind.EplpRejectedOther, "结算菜单请求被服务端拒绝", "MENU REJECTED BY SERVER", ShellTone.Error),
        (OnlineSettlementNoticeKind.EplpTimeout, "等待服务端结算响应超时，正在安全返回城镇", "SETTLEMENT REPLY TIMED OUT; RETURNING", ShellTone.Error),
        (OnlineSettlementNoticeKind.ServerReturnedToTown, "服务端已将角色送回城镇", "RETURNED TO TOWN BY SERVER", ShellTone.Info),
        (OnlineSettlementNoticeKind.SendFailed, "结算命令发送失败，正在安全收尾", "SETTLEMENT SEND FAILED", ShellTone.Error),
        (OnlineSettlementNoticeKind.Disconnected, "与频道服务器的连接已断开", "CHANNEL CONNECTION LOST", ShellTone.Error),
    };

    public static IEnumerable<object[]> TextRows()
    {
        foreach (var row in NoticeTable)
        {
            yield return new object[] { row.Kind, row.Zh, row.En };
        }
    }

    public static IEnumerable<object[]> ToneRows()
    {
        foreach (var row in NoticeTable)
        {
            yield return new object[] { row.Kind, row.Tone };
        }
    }

    /// <summary>保护的行为:文案表覆盖枚举全集、不重复——新增提示种类却没补文案/测试行时在这里失败,不会悄悄漏掉。</summary>
    [Fact]
    public void Notice_table_covers_every_notice_kind_exactly_once()
    {
        var tableKinds = NoticeTable.Select(r => r.Kind).ToList();
        Assert.Equal(tableKinds.Count, tableKinds.Distinct().Count());

        var all = Enum.GetValues<OnlineSettlementNoticeKind>().OrderBy(k => (int)k).ToList();
        Assert.Equal(all, tableKinds.OrderBy(k => (int)k).ToList());
        Assert.Equal(14, all.Count); // 手数:None + 13 种提示
    }

    /// <summary>
    /// 保护的行为:每一种提示在有字体、无字体两个版本下的文本逐字精确(含标点与省略号);
    /// 不带错误码构造时,三种带固定码的拒绝文案也是各自的固定文本,通用拒绝没有括号;构造参数原样带出。
    /// </summary>
    [Theory]
    [MemberData(nameof(TextRows))]
    public void Message_is_exact_for_every_notice_kind_in_both_font_versions(OnlineSettlementNoticeKind kind, string zh, string en)
    {
        var notice = new OnlineSettlementNotice(kind);

        Assert.Equal(zh, notice.Message(hasFonts: true));
        Assert.Equal(en, notice.Message(hasFonts: false));
        Assert.Equal(kind, notice.Kind);
        Assert.Null(notice.Code);
        Assert.Null(notice.CustomMessage);
    }

    /// <summary>保护的行为:提示的语气按种类固定——等待类是 Wait,两个说明类是 Info,其余(含 None)都是 Error;带错误码不改语气。</summary>
    [Theory]
    [MemberData(nameof(ToneRows))]
    public void Tone_is_fixed_per_notice_kind(OnlineSettlementNoticeKind kind, ShellTone tone)
    {
        Assert.Equal(tone, new OnlineSettlementNotice(kind).Tone);
        Assert.Equal(tone, new OnlineSettlementNotice(kind, 8).Tone);
    }

    /// <summary>
    /// 保护的行为:控制器真实的构造方式(拒绝类带着服务端错误码)下,三种固定码的文案仍是固定文本,
    /// 不随附带的码变化(文案里的 8 / 0x85 / 1 是写死的)。
    /// </summary>
    [Theory]
    [InlineData(OnlineSettlementNoticeKind.EplpRejectedNotLeader, 8, "仅队长可操作结算菜单，请选择返回城镇", "MENU REJECTED: NOT PARTY LEADER (8)")]
    [InlineData(OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen, 0x85, "神秘商店处于打开状态，请先关闭或返回城镇", "MENU REJECTED: SECRET SHOP OPEN (0x85)")]
    [InlineData(OnlineSettlementNoticeKind.EplpRejectedInvalidCommand, 1, "服务端判定结算菜单命令无效，已回退安全选项", "MENU REJECTED: INVALID COMMAND (1)")]
    public void Mapped_rejections_keep_their_fixed_text_when_the_server_code_is_attached(OnlineSettlementNoticeKind kind, int code, string zh, string en)
    {
        var notice = new OnlineSettlementNotice(kind, (byte)code);

        Assert.Equal((byte?)code, notice.Code);
        Assert.Equal(zh, notice.Message(hasFonts: true));
        Assert.Equal(en, notice.Message(hasFonts: false));
        Assert.Equal(ShellTone.Error, notice.Tone);
    }

    /// <summary>
    /// 保护的行为:通用拒绝带错误码时,括号里按十进制写出码值(0 也要写,255 不被截断);两个版本的括号写法不同:
    /// 有字体 "(错误码 N)"、无字体 "(N)"。
    /// </summary>
    [Theory]
    [InlineData(0, "结算菜单请求被服务端拒绝 (错误码 0)", "MENU REJECTED BY SERVER (0)")]
    [InlineData(1, "结算菜单请求被服务端拒绝 (错误码 1)", "MENU REJECTED BY SERVER (1)")]
    [InlineData(8, "结算菜单请求被服务端拒绝 (错误码 8)", "MENU REJECTED BY SERVER (8)")]
    [InlineData(34, "结算菜单请求被服务端拒绝 (错误码 34)", "MENU REJECTED BY SERVER (34)")]
    [InlineData(133, "结算菜单请求被服务端拒绝 (错误码 133)", "MENU REJECTED BY SERVER (133)")]
    [InlineData(255, "结算菜单请求被服务端拒绝 (错误码 255)", "MENU REJECTED BY SERVER (255)")]
    public void Generic_rejection_with_a_code_prints_the_decimal_code_in_both_font_versions(int code, string zh, string en)
    {
        var notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.EplpRejectedOther, (byte)code);

        Assert.Equal((byte?)code, notice.Code);
        Assert.Equal(zh, notice.Message(hasFonts: true));
        Assert.Equal(en, notice.Message(hasFonts: false));
        Assert.Equal(ShellTone.Error, notice.Tone);
    }

    /// <summary>保护的行为:错误码只对通用拒绝有意义——其余每一种提示附带错误码后文案与不带码时完全相同(表驱动,枚举全集)。</summary>
    [Fact]
    public void The_error_code_changes_the_text_of_the_generic_rejection_only()
    {
        foreach (OnlineSettlementNoticeKind kind in Enum.GetValues<OnlineSettlementNoticeKind>())
        {
            var bare = new OnlineSettlementNotice(kind);
            var withCode = new OnlineSettlementNotice(kind, 77);
            if (kind == OnlineSettlementNoticeKind.EplpRejectedOther)
            {
                Assert.Equal("结算菜单请求被服务端拒绝 (错误码 77)", withCode.Message(true));
                Assert.Equal("MENU REJECTED BY SERVER (77)", withCode.Message(false));
                continue;
            }
            Assert.Equal(bare.Message(true), withCode.Message(true));
            Assert.Equal(bare.Message(false), withCode.Message(false));
        }
    }

    /// <summary>保护的行为:无字体版本的所有文案(含带码的通用拒绝)只含 ASCII——内置像素字库画不出汉字;有字体版本含汉字。</summary>
    [Fact]
    public void Texts_without_fonts_are_pure_ascii_and_texts_with_fonts_contain_han_characters()
    {
        foreach (OnlineSettlementNoticeKind kind in Enum.GetValues<OnlineSettlementNoticeKind>())
        {
            if (kind == OnlineSettlementNoticeKind.None)
            {
                continue;
            }
            foreach (OnlineSettlementNotice notice in new[] { new OnlineSettlementNotice(kind), new OnlineSettlementNotice(kind, 200) })
            {
                string en = notice.Message(false);
                string zh = notice.Message(true);
                Assert.NotEmpty(en);
                Assert.All(en, c => Assert.True(c < 128, kind + " 无字体文案含非 ASCII 字符:" + en));
                Assert.Contains(zh, c => c >= 0x4E00 && c <= 0x9FFF);
            }
        }
    }

    // ---- 自定义文案与未知种类 ----

    /// <summary>保护的行为:非空的自定义文案压过一切种类原样返回(两个版本相同、不加工);空串与 null 退回种类文案;纯空白算有内容。</summary>
    [Fact]
    public void A_custom_message_wins_over_the_kind_text_unless_it_is_null_or_empty()
    {
        foreach (OnlineSettlementNoticeKind kind in Enum.GetValues<OnlineSettlementNoticeKind>())
        {
            var custom = new OnlineSettlementNotice(kind, 9, "合成自定义 provided");
            Assert.Equal("合成自定义 provided", custom.Message(hasFonts: true));
            Assert.Equal("合成自定义 provided", custom.Message(hasFonts: false));
            Assert.Equal("合成自定义 provided", custom.CustomMessage);
            Assert.Equal((byte?)9, custom.Code);

            // 空串、null:退回种类自己的文案
            var empty = new OnlineSettlementNotice(kind, null, "");
            var none = new OnlineSettlementNotice(kind, null, null);
            Assert.Equal(none.Message(true), empty.Message(true));
            Assert.Equal(none.Message(false), empty.Message(false));
        }

        // 退回的确是表里的文案(不是空串):拿一种非空的种类核对
        Assert.Equal("正在推进结算状态，请稍候…", new OnlineSettlementNotice(OnlineSettlementNoticeKind.Advancing, null, "").Message(true));
        Assert.Equal("ADVANCING SETTLEMENT...", new OnlineSettlementNotice(OnlineSettlementNoticeKind.Advancing, null, null).Message(false));

        // 纯空白不是"空":照原样返回
        Assert.Equal(" ", new OnlineSettlementNotice(OnlineSettlementNoticeKind.Advancing, null, " ").Message(true));
        Assert.Equal("\t", new OnlineSettlementNotice(OnlineSettlementNoticeKind.Disconnected, null, "\t").Message(false));
    }

    /// <summary>保护的行为:枚举之外的种类(防御)与 None 一样没有文案、不抛异常,语气是 Error。</summary>
    [Fact]
    public void An_undefined_kind_has_no_text_and_the_error_tone()
    {
        var unknown = new OnlineSettlementNotice((OnlineSettlementNoticeKind)99);
        Assert.Equal(string.Empty, unknown.Message(hasFonts: true));
        Assert.Equal(string.Empty, unknown.Message(hasFonts: false));
        Assert.Equal(ShellTone.Error, unknown.Tone);

        var withCode = new OnlineSettlementNotice((OnlineSettlementNoticeKind)(-1), 5);
        Assert.Equal(string.Empty, withCode.Message(hasFonts: true));
        Assert.Equal(string.Empty, withCode.Message(hasFonts: false));
        Assert.Equal(ShellTone.Error, withCode.Tone);
    }

    // ---- 未知道具名 ----

    /// <summary>
    /// 保护的行为:没解析到名字的掉落条目显示占位名("未知道具"/"UNKNOWN ITEM"),数量大于 1 才在后面追加 " x数量"
    /// (数量 1 不追加,大数按十进制不带分组符);解析到名字的条目两个版本都显示名字本身。
    /// </summary>
    [Fact]
    public void Drop_labels_use_the_placeholder_for_unresolved_names_and_append_the_count_only_above_one()
    {
        var unresolved1 = new OnlineSettlementDropItem(9801, 1, null, isQuestItem: false);
        Assert.False(unresolved1.NameResolved);
        Assert.Equal("未知道具", unresolved1.DisplayLabel(hasFonts: true));
        Assert.Equal("UNKNOWN ITEM", unresolved1.DisplayLabel(hasFonts: false));

        var unresolved2 = new OnlineSettlementDropItem(9801, 2, null, isQuestItem: false);
        Assert.Equal("未知道具 x2", unresolved2.DisplayLabel(hasFonts: true));
        Assert.Equal("UNKNOWN ITEM x2", unresolved2.DisplayLabel(hasFonts: false));

        var unresolvedBig = new OnlineSettlementDropItem(9801, 12345, null, isQuestItem: true);
        Assert.Equal("未知道具 x12345", unresolvedBig.DisplayLabel(hasFonts: true));
        Assert.Equal("UNKNOWN ITEM x12345", unresolvedBig.DisplayLabel(hasFonts: false));

        var unresolvedMax = new OnlineSettlementDropItem(9801, int.MaxValue, "", isQuestItem: false);
        Assert.Equal("未知道具 x2147483647", unresolvedMax.DisplayLabel(hasFonts: true));
        Assert.Equal("UNKNOWN ITEM x2147483647", unresolvedMax.DisplayLabel(hasFonts: false));

        var named1 = new OnlineSettlementDropItem(9802, 1, "合成名甲", isQuestItem: false);
        Assert.True(named1.NameResolved);
        Assert.Equal("合成名甲", named1.DisplayLabel(hasFonts: true));
        Assert.Equal("合成名甲", named1.DisplayLabel(hasFonts: false));

        var named3 = new OnlineSettlementDropItem(9802, 3, "Gapb Named", isQuestItem: true);
        Assert.Equal("Gapb Named x3", named3.DisplayLabel(hasFonts: true));
        Assert.Equal("Gapb Named x3", named3.DisplayLabel(hasFonts: false));
    }

    /// <summary>保护的行为:掉落条目构造时数量 ≤ 0 缺省成 1、正数原样,空名字归一为 null(未解析),其余字段原样带出。</summary>
    [Fact]
    public void Drop_item_constructor_defaults_the_count_to_one_and_normalises_empty_names()
    {
        Assert.Equal(1, new OnlineSettlementDropItem(7, 0, "n", false).Count);
        Assert.Equal(1, new OnlineSettlementDropItem(7, -3, "n", false).Count);
        Assert.Equal(1, new OnlineSettlementDropItem(7, 1, "n", false).Count);
        Assert.Equal(2, new OnlineSettlementDropItem(7, 2, "n", false).Count);
        Assert.Equal(int.MaxValue, new OnlineSettlementDropItem(7, int.MaxValue, "n", false).Count);

        var empty = new OnlineSettlementDropItem(7, 4, "", isQuestItem: true);
        Assert.Null(empty.Name);
        Assert.False(empty.NameResolved);
        Assert.True(empty.IsQuestItem);
        Assert.Equal(7, empty.ItemId);

        var nullName = new OnlineSettlementDropItem(8, 1, null, isQuestItem: false);
        Assert.Null(nullName.Name);
        Assert.False(nullName.IsQuestItem);
        Assert.Equal(8, nullName.ItemId);

        var named = new OnlineSettlementDropItem(9, 5, "合成名乙", isQuestItem: false);
        Assert.Equal("合成名乙", named.Name);
        Assert.True(named.NameResolved);
        Assert.Equal(5, named.Count);
    }

    // ---- 加成标签的默认值 ----

    /// <summary>
    /// 保护的行为:键不在 13 个具名之列(基础/评分加成自己的键、空串、null、大小写不同、带空格、陌生键)时,
    /// 标签退回默认 "加成经验"/"BONUS EXP";数量前恒为 " +",十进制不带分组符,0 也照写。
    /// </summary>
    [Theory]
    [InlineData("base")]
    [InlineData("rank_bonus")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("AVATAR")]
    [InlineData("avatar ")]
    [InlineData("pcroom")]
    [InlineData("synthetic_key")]
    public void Unknown_bonus_keys_fall_back_to_the_default_label(string key)
    {
        var line = new OnlineSettlementExpLine(3, key, 25, SettlementFieldConfidence.Inferred);
        Assert.Equal("加成经验 +25", line.DisplayLabel(hasFonts: true));
        Assert.Equal("BONUS EXP +25", line.DisplayLabel(hasFonts: false));
        Assert.Equal(key ?? string.Empty, line.Key);

        var big = new OnlineSettlementExpLine(3, key, int.MaxValue, SettlementFieldConfidence.Inferred);
        Assert.Equal("加成经验 +2147483647", big.DisplayLabel(hasFonts: true));
        Assert.Equal("BONUS EXP +2147483647", big.DisplayLabel(hasFonts: false));

        var thousands = new OnlineSettlementExpLine(3, key, 1234567, SettlementFieldConfidence.Inferred);
        Assert.Equal("BONUS EXP +1234567", thousands.DisplayLabel(hasFonts: false));

        var zero = new OnlineSettlementExpLine(3, key, 0, SettlementFieldConfidence.Inferred);
        Assert.Equal("BONUS EXP +0", zero.DisplayLabel(hasFonts: false));
    }

    /// <summary>
    /// 保护的行为:经验分项"可显示"= 把握程度不是 Unknown 且数量为正;构造器按原样记下下标、数量、把握程度,
    /// 键为 null 时记成空串。
    /// </summary>
    [Fact]
    public void An_exp_line_displays_only_when_it_is_known_and_positive()
    {
        Assert.True(new OnlineSettlementExpLine(3, "avatar", 1, SettlementFieldConfidence.Inferred).ShouldDisplay);
        Assert.True(new OnlineSettlementExpLine(3, "avatar", 1, SettlementFieldConfidence.Observed).ShouldDisplay);
        Assert.False(new OnlineSettlementExpLine(3, "avatar", 1, SettlementFieldConfidence.Unknown).ShouldDisplay);
        Assert.False(new OnlineSettlementExpLine(3, "avatar", 0, SettlementFieldConfidence.Inferred).ShouldDisplay);
        Assert.False(new OnlineSettlementExpLine(3, "avatar", -1, SettlementFieldConfidence.Inferred).ShouldDisplay);
        Assert.False(new OnlineSettlementExpLine(3, "avatar", 0, SettlementFieldConfidence.Unknown).ShouldDisplay);

        var line = new OnlineSettlementExpLine(11, null, 42, SettlementFieldConfidence.Observed);
        Assert.Equal(11, line.Index);
        Assert.Equal(string.Empty, line.Key);
        Assert.Equal(42, line.Amount);
        Assert.Equal(SettlementFieldConfidence.Observed, line.Confidence);
    }
}
