namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 联网结算脱敏日志格式化器(<see cref="OnlineSettlementLog"/>)的逐行精确补测(区域 B,第二轮):
/// 前缀、每个事件的每个片段(事件名、字段名、分隔与空格)、可选片段的有无与顺序、空值的 "unknown" 缺省、
/// 枚举名(解析状态/菜单选项用枚举全集核对)、数字的十进制写法。期望的整行都是手写的字面量。
/// </summary>
public sealed class OnlineSettlementGapB2LogTests
{
    private const string P = "DNF_ONLINE stage=settle";

    /// <summary>保护的行为:日志统一前缀是 "DNF_ONLINE stage=settle"(排查脚本按它过滤),且每个格式化器的整行都以 "前缀 + 空格 + event=" 开头。</summary>
    [Fact]
    public void Every_line_starts_with_the_settle_stage_prefix_followed_by_an_event_field()
    {
        Assert.Equal("DNF_ONLINE stage=settle", OnlineSettlementLog.Prefix);

        string[] lines =
        {
            OnlineSettlementLog.Show(false, OnlineSettlementParseState.Succeeded, OnlineSettlementParseState.Succeeded, 0, 0),
            OnlineSettlementLog.Diff("kills"),
            OnlineSettlementLog.Advance("s"),
            OnlineSettlementLog.Choose(SettlementChoice.Retry, "p"),
            OnlineSettlementLog.Retry("r", 1),
            OnlineSettlementLog.Town("r", "a"),
            OnlineSettlementLog.Fail("r"),
        };
        string[] events = { "show", "diff", "advance", "choose", "retry", "town", "fail" };
        for (int i = 0; i < lines.Length; i++)
        {
            Assert.StartsWith("DNF_ONLINE stage=settle event=" + events[i] + " ", lines[i], StringComparison.Ordinal);
        }
    }

    // ---- show ----

    /// <summary>保护的行为:显示事件按"超时标记、通关结果解析状态、奖励解析状态、掉落数、差异数"的顺序逐字段输出,超时写 true/false,数字十进制。</summary>
    [Theory]
    [InlineData(false, OnlineSettlementParseState.Succeeded, OnlineSettlementParseState.Failed, 2, 1, P + " event=show timedout=false result=Succeeded reward=Failed drops=2 diffs=1")]
    [InlineData(true, OnlineSettlementParseState.NotApplicable, OnlineSettlementParseState.NotApplicable, 0, 0, P + " event=show timedout=true result=NotApplicable reward=NotApplicable drops=0 diffs=0")]
    [InlineData(true, OnlineSettlementParseState.Failed, OnlineSettlementParseState.Succeeded, 12, 5, P + " event=show timedout=true result=Failed reward=Succeeded drops=12 diffs=5")]
    [InlineData(false, OnlineSettlementParseState.Succeeded, OnlineSettlementParseState.Succeeded, 1000, 2147483647, P + " event=show timedout=false result=Succeeded reward=Succeeded drops=1000 diffs=2147483647")]
    public void Show_line_lists_every_field_in_order(bool timedOut, OnlineSettlementParseState result, OnlineSettlementParseState reward, int drops, int diffs, string expected)
    {
        Assert.Equal(expected, OnlineSettlementLog.Show(timedOut, result, reward, drops, diffs));
    }

    /// <summary>保护的行为:解析状态在日志里写枚举名(三种全覆盖;结果与奖励两个位置各自独立,不串位)。</summary>
    [Fact]
    public void Show_line_writes_the_enum_name_of_every_parse_state_in_both_positions()
    {
        var names = new Dictionary<OnlineSettlementParseState, string>
        {
            { OnlineSettlementParseState.NotApplicable, "NotApplicable" },
            { OnlineSettlementParseState.Succeeded, "Succeeded" },
            { OnlineSettlementParseState.Failed, "Failed" },
        };
        Assert.Equal(names.Count, Enum.GetValues<OnlineSettlementParseState>().Length); // 枚举全集都在表里

        foreach (OnlineSettlementParseState state in Enum.GetValues<OnlineSettlementParseState>())
        {
            string name = names[state];
            Assert.Equal(P + " event=show timedout=false result=" + name + " reward=Succeeded drops=3 diffs=4",
                OnlineSettlementLog.Show(false, state, OnlineSettlementParseState.Succeeded, 3, 4));
            Assert.Equal(P + " event=show timedout=true result=Succeeded reward=" + name + " drops=3 diffs=4",
                OnlineSettlementLog.Show(true, OnlineSettlementParseState.Succeeded, state, 3, 4));
        }
    }

    // ---- diff ----

    /// <summary>保护的行为:差异事件写字段名(五个对账字段逐个核对);空串与 null 写 "unknown"。</summary>
    [Theory]
    [InlineData("clear_time", P + " event=diff field=clear_time")]
    [InlineData("kills", P + " event=diff field=kills")]
    [InlineData("base_exp", P + " event=diff field=base_exp")]
    [InlineData("rank_bonus_exp", P + " event=diff field=rank_bonus_exp")]
    [InlineData("reward_gold", P + " event=diff field=reward_gold")]
    [InlineData("", P + " event=diff field=unknown")]
    [InlineData(null, P + " event=diff field=unknown")]
    public void Diff_line_names_the_field_or_unknown(string field, string expected)
    {
        Assert.Equal(expected, OnlineSettlementLog.Diff(field));
    }

    // ---- advance ----

    /// <summary>保护的行为:推进事件总是带步骤名(空值写 unknown),状态只在非空时才追加 " status=…"(null 与空串都省略)。</summary>
    [Theory]
    [InlineData("score_scroll", null, P + " event=advance step=score_scroll")]
    [InlineData("score_scroll", "", P + " event=advance step=score_scroll")]
    [InlineData("score_scroll", "ok", P + " event=advance step=score_scroll status=ok")]
    [InlineData("card_select", "timeout", P + " event=advance step=card_select status=timeout")]
    [InlineData(null, null, P + " event=advance step=unknown")]
    [InlineData("", "", P + " event=advance step=unknown")]
    [InlineData(null, "ok", P + " event=advance step=unknown status=ok")]
    [InlineData("", "no_reply", P + " event=advance step=unknown status=no_reply")]
    public void Advance_line_always_has_a_step_and_a_status_only_when_given(string step, string status, string expected)
    {
        Assert.Equal(expected, OnlineSettlementLog.Advance(step, status));
    }

    /// <summary>保护的行为:状态参数缺省(不传)时与显式传 null 的结果一致,不带 status 片段。</summary>
    [Fact]
    public void Advance_line_without_a_status_argument_has_no_status_fragment()
    {
        Assert.Equal(P + " event=advance step=menu", OnlineSettlementLog.Advance("menu"));
    }

    // ---- choose ----

    /// <summary>保护的行为:选择事件写菜单选项的枚举名(五种全覆盖)与来源标记;来源为空写 unknown。</summary>
    [Fact]
    public void Choose_line_writes_every_menu_choice_name_and_the_source_marker()
    {
        var names = new Dictionary<SettlementChoice, string>
        {
            { SettlementChoice.None, "None" },
            { SettlementChoice.Retry, "Retry" },
            { SettlementChoice.QuickParty, "QuickParty" },
            { SettlementChoice.SelectDungeon, "SelectDungeon" },
            { SettlementChoice.ReturnToTown, "ReturnToTown" },
        };
        Assert.Equal(names.Count, Enum.GetValues<SettlementChoice>().Length);

        foreach (SettlementChoice choice in Enum.GetValues<SettlementChoice>())
        {
            string name = names[choice];
            Assert.Equal(P + " event=choose choice=" + name + " source=player", OnlineSettlementLog.Choose(choice, "player"));
            Assert.Equal(P + " event=choose choice=" + name + " source=timeout", OnlineSettlementLog.Choose(choice, "timeout"));
            Assert.Equal(P + " event=choose choice=" + name + " source=unknown", OnlineSettlementLog.Choose(choice, null));
            Assert.Equal(P + " event=choose choice=" + name + " source=unknown", OnlineSettlementLog.Choose(choice, ""));
        }
    }

    // ---- retry ----

    /// <summary>保护的行为:重试事件写状态与轮次(十进制,0 也照写),状态为空写 unknown。</summary>
    [Theory]
    [InlineData("reenter", 2, P + " event=retry status=reenter cycle=2")]
    [InlineData("reenter", 0, P + " event=retry status=reenter cycle=0")]
    [InlineData("rejected", 10, P + " event=retry status=rejected cycle=10")]
    [InlineData(null, 1, P + " event=retry status=unknown cycle=1")]
    [InlineData("", 7, P + " event=retry status=unknown cycle=7")]
    [InlineData("done", 2147483647, P + " event=retry status=done cycle=2147483647")]
    public void Retry_line_has_status_then_cycle(string status, int cycle, string expected)
    {
        Assert.Equal(expected, OnlineSettlementLog.Retry(status, cycle));
    }

    // ---- town ----

    /// <summary>保护的行为:回城事件按"原因、落点来源"顺序输出,信号只在非空时才追加 " signal=…";原因或来源为空写 unknown。</summary>
    [Theory]
    [InlineData("confirmed", "server_area", "area_pair", P + " event=town reason=confirmed area=server_area signal=area_pair")]
    [InlineData("confirmed", "server_area", null, P + " event=town reason=confirmed area=server_area")]
    [InlineData("confirmed", "server_area", "", P + " event=town reason=confirmed area=server_area")]
    [InlineData("timeout", "pre_dungeon", "none", P + " event=town reason=timeout area=pre_dungeon signal=none")]
    [InlineData(null, null, null, P + " event=town reason=unknown area=unknown")]
    [InlineData("", "", "", P + " event=town reason=unknown area=unknown")]
    [InlineData(null, "default", "sig", P + " event=town reason=unknown area=default signal=sig")]
    [InlineData("disconnect", "", "sig", P + " event=town reason=disconnect area=unknown signal=sig")]
    public void Town_line_has_reason_then_area_and_a_signal_only_when_given(string reason, string area, string signal, string expected)
    {
        Assert.Equal(expected, OnlineSettlementLog.Town(reason, area, signal));
    }

    /// <summary>保护的行为:信号参数缺省(不传)时没有 signal 片段。</summary>
    [Fact]
    public void Town_line_without_a_signal_argument_has_no_signal_fragment()
    {
        Assert.Equal(P + " event=town reason=timeout area=default", OnlineSettlementLog.Town("timeout", "default"));
    }

    // ---- fail ----

    /// <summary>
    /// 保护的行为:失败事件按"原因、步骤、错误码"的顺序输出,步骤与错误码各自只在给出时才追加
    /// (错误码 0 也算给出;空串步骤省略);原因为空写 unknown。
    /// </summary>
    [Theory]
    [InlineData("eplp_rejected", 8, "Retry", P + " event=fail reason=eplp_rejected step=Retry code=8")]
    [InlineData("eplp_rejected", 8, null, P + " event=fail reason=eplp_rejected code=8")]
    [InlineData("eplp_rejected", 8, "", P + " event=fail reason=eplp_rejected code=8")]
    [InlineData("send_failed", -1, "ReturnToTown", P + " event=fail reason=send_failed step=ReturnToTown")]
    [InlineData("disconnected", -1, null, P + " event=fail reason=disconnected")]
    [InlineData("disconnected", -1, "", P + " event=fail reason=disconnected")]
    [InlineData("eplp_rejected", 0, "SelectDungeon", P + " event=fail reason=eplp_rejected step=SelectDungeon code=0")]
    [InlineData("eplp_rejected", 133, null, P + " event=fail reason=eplp_rejected code=133")]
    [InlineData("eplp_rejected", 255, "Retry", P + " event=fail reason=eplp_rejected step=Retry code=255")]
    [InlineData(null, -1, null, P + " event=fail reason=unknown")]
    [InlineData("", 34, "step_x", P + " event=fail reason=unknown step=step_x code=34")]
    public void Fail_line_has_reason_then_step_then_code_with_the_optional_parts_only_when_given(string reason, int code, string step, string expected)
    {
        // code < 0 表示不传错误码(null)
        byte? errorCode = code < 0 ? (byte?)null : (byte)code;
        Assert.Equal(expected, OnlineSettlementLog.Fail(reason, errorCode, step));
    }

    /// <summary>保护的行为:只给原因时的缺省调用形态(步骤、错误码都不传)只有 reason 片段。</summary>
    [Fact]
    public void Fail_line_with_only_a_reason_has_neither_step_nor_code()
    {
        Assert.Equal(P + " event=fail reason=timeout", OnlineSettlementLog.Fail("timeout"));
        Assert.Equal(P + " event=fail reason=timeout code=9", OnlineSettlementLog.Fail("timeout", 9));
        Assert.Equal(P + " event=fail reason=timeout step=Retry", OnlineSettlementLog.Fail("timeout", step: "Retry"));
    }
}
