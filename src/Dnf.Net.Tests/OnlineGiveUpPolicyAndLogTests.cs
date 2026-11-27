using System.Reflection;
using System.Text.RegularExpressions;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// <c>--give-up-on-failure</c> 的策略(哪些结局触发、哪些不触发)、日志行与结果行的拼法、缺省值与既有契约(退出码表、<c>ResultFields</c> 的字符串)不变的测试。
/// 都是纯函数,不连网。每个触发与不触发的结局各占一行用例(新增枚举成员时必须回来补一行,见 <c>Every_outcome_has_a_row_in_the_policy_tables</c>)。
/// </summary>
public class OnlineGiveUpPolicyAndLogTests
{
    private static OnlineFightArguments Parse(params string[] args)
    {
        Assert.True(OnlineFightArguments.TryParse(args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem), "应当解析成功,实际问题 " + problem);
        return parsed;
    }

    private const string Base = "outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1";

    // ======== 策略:战斗驱动的结局 ========

    [Theory]
    [InlineData(OnlineFightOutcome.RefusedStop, true)]
    [InlineData(OnlineFightOutcome.NoClearNotice, true)]
    [InlineData(OnlineFightOutcome.SettleIncomplete, true)]
    [InlineData(OnlineFightOutcome.AllDone, false)]
    [InlineData(OnlineFightOutcome.SessionClosed, false)]
    [InlineData(OnlineFightOutcome.Faulted, false)]
    [InlineData(OnlineFightOutcome.Cancelled, false)]
    public void Only_the_three_alive_session_failures_of_the_fight_trigger_a_give_up(OnlineFightOutcome outcome, bool expected)
    {
        Assert.Equal(expected, OnlineGiveUpOnFailure.ShouldGiveUp(outcome));
    }

    [Theory]
    [InlineData(OnlineAfterSettleOutcome.ScoreScrollTimeout, true)]
    [InlineData(OnlineAfterSettleOutcome.CardRightTimeout, true)]
    [InlineData(OnlineAfterSettleOutcome.SelectCardSendFailed, true)]
    [InlineData(OnlineAfterSettleOutcome.EplpTimeout, true)]
    [InlineData(OnlineAfterSettleOutcome.ReturnTimeout, true)]
    [InlineData(OnlineAfterSettleOutcome.ReplyFailed, true)]
    [InlineData(OnlineAfterSettleOutcome.ReplyMalformed, true)]
    [InlineData(OnlineAfterSettleOutcome.ReturnedToTown, false)]
    [InlineData(OnlineAfterSettleOutcome.SessionClosed, false)]
    [InlineData(OnlineAfterSettleOutcome.Faulted, false)]
    [InlineData(OnlineAfterSettleOutcome.Cancelled, false)]
    public void Only_the_alive_session_failures_of_the_settle_menu_trigger_a_give_up(OnlineAfterSettleOutcome outcome, bool expected)
    {
        Assert.Equal(expected, OnlineGiveUpOnFailure.ShouldGiveUp(outcome));
    }

    [Fact]
    public void Every_outcome_has_a_row_in_the_policy_tables()
    {
        // 上面两张表一共 7 + 11 行;新增结局枚举成员时这里会红,逼着回来决定它触不触发
        Assert.Equal(7, Enum.GetValues(typeof(OnlineFightOutcome)).Length);
        Assert.Equal(11, Enum.GetValues(typeof(OnlineAfterSettleOutcome)).Length);
        var fightTriggers = Enum.GetValues(typeof(OnlineFightOutcome)).Cast<OnlineFightOutcome>().Where(OnlineGiveUpOnFailure.ShouldGiveUp).ToArray();
        Assert.Equal(new[] { OnlineFightOutcome.RefusedStop, OnlineFightOutcome.NoClearNotice, OnlineFightOutcome.SettleIncomplete }, fightTriggers);
        var menuTriggers = Enum.GetValues(typeof(OnlineAfterSettleOutcome)).Cast<OnlineAfterSettleOutcome>().Where(OnlineGiveUpOnFailure.ShouldGiveUp).ToArray();
        Assert.Equal(7, menuTriggers.Length);
        Assert.DoesNotContain(OnlineAfterSettleOutcome.ReturnedToTown, menuTriggers);
        Assert.DoesNotContain(OnlineAfterSettleOutcome.SessionClosed, menuTriggers);
        Assert.DoesNotContain(OnlineAfterSettleOutcome.Faulted, menuTriggers);
        Assert.DoesNotContain(OnlineAfterSettleOutcome.Cancelled, menuTriggers);
    }

    // ======== 日志行 ========

    [Theory]
    [InlineData(OnlineGiveUpOutcome.Returned, "returned")]
    [InlineData(OnlineGiveUpOutcome.Timeout, "timeout")]
    [InlineData(OnlineGiveUpOutcome.Closed, "closed")]
    [InlineData(OnlineGiveUpOutcome.SendFailed, "sendfailed")]
    [InlineData(OnlineGiveUpOutcome.Skipped, "skipped")]
    [InlineData(OnlineGiveUpOutcome.Cancelled, "cancelled")]
    [InlineData(OnlineGiveUpOutcome.Faulted, "faulted")]
    public void Every_give_up_outcome_has_its_own_lowercase_token(OnlineGiveUpOutcome outcome, string token)
    {
        Assert.Equal(token, OnlineFightLog.GiveUpToken(outcome));
    }

    [Fact]
    public void The_give_up_outcome_values_are_pinned_and_the_default_is_returned()
    {
        Assert.Equal(0, (int)OnlineGiveUpOutcome.Returned);
        Assert.Equal(1, (int)OnlineGiveUpOutcome.Timeout);
        Assert.Equal(2, (int)OnlineGiveUpOutcome.Closed);
        Assert.Equal(3, (int)OnlineGiveUpOutcome.SendFailed);
        Assert.Equal(4, (int)OnlineGiveUpOutcome.Skipped);
        Assert.Equal(5, (int)OnlineGiveUpOutcome.Cancelled);
        Assert.Equal(6, (int)OnlineGiveUpOutcome.Faulted);
        Assert.Equal(7, Enum.GetValues(typeof(OnlineGiveUpOutcome)).Length);
        // 七个令牌互不相同
        string[] tokens = Enum.GetValues(typeof(OnlineGiveUpOutcome)).Cast<OnlineGiveUpOutcome>().Select(OnlineFightLog.GiveUpToken).ToArray();
        Assert.Equal(tokens.Length, tokens.Distinct().Count());
    }

    [Fact]
    public void The_step_line_has_exactly_the_documented_shape_and_only_enum_names()
    {
        Assert.Equal("DNF_NET stage=giveup step=sent reason=NoClearNotice", OnlineFightLog.GiveUpStep(OnlineFightLog.GiveUpSentStep, "NoClearNotice"));
        Assert.Equal("DNF_NET stage=giveup step=returned reason=RefusedStop", OnlineFightLog.GiveUpStep(OnlineFightLog.GiveUpToken(OnlineGiveUpOutcome.Returned), "RefusedStop"));
        Assert.Equal("DNF_NET stage=giveup step=timeout reason=SettleIncomplete", OnlineFightLog.GiveUpStep(OnlineFightLog.GiveUpToken(OnlineGiveUpOutcome.Timeout), "SettleIncomplete"));
        Assert.Equal("DNF_NET stage=giveup step=skipped reason=ReturnTimeout", OnlineFightLog.GiveUpStep(OnlineFightLog.GiveUpToken(OnlineGiveUpOutcome.Skipped), "ReturnTimeout"));
        Assert.Equal("DNF_NET stage=giveup", OnlineFightLog.GiveUpPrefix);
        Assert.Equal("sent", OnlineFightLog.GiveUpSentStep);

        // 任何触发结局与任何放弃结局拼出来的行都只含字母与固定的几个符号
        var shape = new Regex("^DNF_NET stage=giveup step=[a-z]+ reason=[A-Za-z]+$");
        var reasons = Enum.GetValues(typeof(OnlineFightOutcome)).Cast<OnlineFightOutcome>().Where(OnlineGiveUpOnFailure.ShouldGiveUp).Select(o => o.ToString())
            .Concat(Enum.GetValues(typeof(OnlineAfterSettleOutcome)).Cast<OnlineAfterSettleOutcome>().Where(OnlineGiveUpOnFailure.ShouldGiveUp).Select(o => o.ToString()));
        foreach (string reason in reasons)
        {
            foreach (OnlineGiveUpOutcome outcome in Enum.GetValues(typeof(OnlineGiveUpOutcome)))
            {
                string line = OnlineFightLog.GiveUpStep(OnlineFightLog.GiveUpToken(outcome), reason);
                Assert.Matches(shape, line);
            }
        }
    }

    [Fact]
    public void The_probe_whitelist_lets_the_give_up_lines_through_without_the_net_prefix_and_still_blocks_look_alikes()
    {
        Assert.Equal("stage=giveup step=sent reason=NoClearNotice", OnlineFightLog.ProbeText("DNF_NET stage=giveup step=sent reason=NoClearNotice"));
        Assert.Equal("stage=giveup step=timeout reason=SettleIncomplete", OnlineFightLog.ProbeText("DNF_NET stage=giveup step=timeout reason=SettleIncomplete"));

        // 既有四类之外的一律不放行;名字只是以 giveup 开头也不行
        Assert.Null(OnlineFightLog.ProbeText("DNF_NET stage=giveupx step=sent reason=X"));
        Assert.Null(OnlineFightLog.ProbeText("DNF_NET stage=giveaway step=sent reason=X"));
        Assert.Null(OnlineFightLog.ProbeText("stage=giveup step=sent reason=NoClearNotice"));
        Assert.Null(OnlineFightLog.ProbeText("DNF_NET stage=settle-menu step=72"));
        Assert.Null(OnlineFightLog.ProbeText("DNF_NET stage=dungeon begin dungeon=901 mode=0"));
        Assert.Null(OnlineFightLog.ProbeText(null));

        // 既有放行的三类不受影响
        Assert.Equal("stage=fight phase=kills posted=0", OnlineFightLog.ProbeText("DNF_NET stage=fight phase=kills posted=0"));
        Assert.Equal("stage=combat event=GiveUpSent", OnlineFightLog.ProbeText("DNF_NET stage=combat event=GiveUpSent"));
        Assert.Equal("stage=dungeon entered=1 monsters=3", OnlineFightLog.ProbeText("DNF_NET stage=dungeon entered=1 monsters=3"));
    }

    // ======== 结果行:默认(关)时与以前逐字相同,开了才多一个字段 ========

    [Theory]
    [InlineData("--dungeon|901|0")]
    [InlineData("--dungeon|901|0|--continue-on-refuse|--stay|5")]
    public void Switch_off_without_the_after_settle_options_the_result_fields_are_the_legacy_ones_even_if_a_give_up_exists(string joined)
    {
        OnlineFightArguments off = Parse(joined.Split('|'));
        Assert.False(off.GiveUpOnFailure);
        OnlineFightSummary s = GiveUpKit.Fight(OnlineFightOutcome.AllDone);

        Assert.Equal(Base, OnlineFightLog.ResultFieldsFor(off, s, null, null));
        // 就算(被错误地)传了放弃汇总,没开开关也不能多出字段
        Assert.Equal(Base, OnlineFightLog.ResultFieldsFor(off, s, null, GiveUpKit.GiveUp("NoClearNotice", OnlineGiveUpOutcome.Returned)));
        Assert.Equal(OnlineFightLog.ResultFields(s), OnlineFightLog.ResultFieldsFor(off, s, null, null));
        Assert.DoesNotContain("giveUp", OnlineFightLog.ResultFieldsFor(off, s, null, GiveUpKit.GiveUp("NoClearNotice", OnlineGiveUpOutcome.Timeout)));
    }

    [Theory]
    [InlineData("--dungeon|901|0|--min-clear-seconds|12")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|12")]
    public void Switch_off_with_the_after_settle_options_the_result_fields_are_the_legacy_ones_even_if_a_give_up_exists(string joined)
    {
        OnlineFightArguments off = Parse(joined.Split('|'));
        Assert.False(off.GiveUpOnFailure);
        OnlineFightSummary s = GiveUpKit.Fight(OnlineFightOutcome.AllDone, clearDuration: TimeSpan.FromMilliseconds(12345));
        OnlineAfterSettleSummary menu = GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnedToTown);
        OnlineGiveUpSummary stray = GiveUpKit.GiveUp("ReturnTimeout", OnlineGiveUpOutcome.Returned);

        Assert.Equal(Base + " clearMs=12345 menu=ReturnedToTown", OnlineFightLog.ResultFieldsFor(off, s, menu, null));
        Assert.Equal(Base + " clearMs=12345 menu=ReturnedToTown", OnlineFightLog.ResultFieldsFor(off, s, menu, stray));
        Assert.Equal(Base + " clearMs=12345 menu=-", OnlineFightLog.ResultFieldsFor(off, s, null, stray));
        Assert.Equal(OnlineFightLog.ResultFieldsWithAfterSettle(s, menu), OnlineFightLog.ResultFieldsFor(off, s, menu, stray));
        Assert.DoesNotContain("giveUp", OnlineFightLog.ResultFieldsFor(off, s, menu, stray));
    }

    [Theory]
    [InlineData(OnlineGiveUpOutcome.Returned, "returned")]
    [InlineData(OnlineGiveUpOutcome.Timeout, "timeout")]
    [InlineData(OnlineGiveUpOutcome.Closed, "closed")]
    [InlineData(OnlineGiveUpOutcome.SendFailed, "sendfailed")]
    [InlineData(OnlineGiveUpOutcome.Skipped, "skipped")]
    [InlineData(OnlineGiveUpOutcome.Cancelled, "cancelled")]
    [InlineData(OnlineGiveUpOutcome.Faulted, "faulted")]
    public void Switch_on_appends_exactly_one_give_up_field_after_the_legacy_fields(OnlineGiveUpOutcome outcome, string token)
    {
        OnlineFightArguments plain = Parse("--dungeon", "901", "0", "--give-up-on-failure");
        OnlineFightArguments settle = Parse("--dungeon", "901", "0", "--after-settle", "--min-clear-seconds", "12", "--give-up-on-failure");
        OnlineFightArguments minOnly = Parse("--dungeon", "901", "0", "--min-clear-seconds", "12", "--give-up-on-failure");
        OnlineFightSummary s = GiveUpKit.Fight(OnlineFightOutcome.NoClearNotice, clearDuration: TimeSpan.FromMilliseconds(12345));
        OnlineGiveUpSummary give = GiveUpKit.GiveUp("NoClearNotice", outcome);
        const string notReady = "outcome=NoClearNotice monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=0 settlement=none returnReady=0";

        Assert.Equal(notReady + " giveUp=" + token, OnlineFightLog.ResultFieldsFor(plain, s, null, give));
        Assert.Equal(notReady + " clearMs=12345 menu=- giveUp=" + token, OnlineFightLog.ResultFieldsFor(settle, s, null, give));
        Assert.Equal(notReady + " clearMs=12345 menu=ReturnTimeout giveUp=" + token, OnlineFightLog.ResultFieldsFor(settle, s, GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnTimeout), give));
        Assert.Equal(notReady + " clearMs=12345 menu=- giveUp=" + token, OnlineFightLog.ResultFieldsFor(minOnly, s, null, give));
        // 字段只多一个,而且在最后
        string line = OnlineFightLog.ResultFieldsFor(plain, s, null, give);
        Assert.Single(Regex.Matches(line, "giveUp="));
        Assert.StartsWith(OnlineFightLog.ResultFields(s) + " ", line);
    }

    [Fact]
    public void Switch_on_without_a_give_up_summary_writes_a_dash_so_the_field_is_always_present()
    {
        OnlineFightArguments plain = Parse("--dungeon", "901", "0", "--give-up-on-failure");
        OnlineFightArguments settle = Parse("--dungeon", "901", "0", "--after-settle", "--min-clear-seconds", "12", "--give-up-on-failure");
        OnlineFightSummary ok = GiveUpKit.Fight(OnlineFightOutcome.AllDone, clearDuration: TimeSpan.FromMilliseconds(12345));

        Assert.Equal(Base + " giveUp=-", OnlineFightLog.ResultFieldsFor(plain, ok, null, null));
        Assert.Equal(Base + " clearMs=12345 menu=ReturnedToTown giveUp=-", OnlineFightLog.ResultFieldsFor(settle, ok, GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnedToTown), null));
    }

    [Fact]
    public void The_legacy_result_field_builders_are_untouched_by_the_new_ones()
    {
        // 既有两个拼法的字符串与既有契约测试里的一字不差(默认路径用的就是它们)
        OnlineFightSummary s = GiveUpKit.Fight(OnlineFightOutcome.AllDone, clearDuration: TimeSpan.FromMilliseconds(12345));
        OnlineAfterSettleSummary menu = GiveUpKit.Menu(OnlineAfterSettleOutcome.ReturnedToTown);

        Assert.Equal(Base, OnlineFightLog.ResultFields(s));
        Assert.Equal(Base + " clearMs=12345 menu=ReturnedToTown", OnlineFightLog.ResultFieldsWithAfterSettle(s, menu));
        Assert.Equal(Base + " clearMs=12345 menu=-", OnlineFightLog.ResultFieldsWithAfterSettle(s, (OnlineAfterSettleSummary)null));
        Assert.Equal(Base + " clearMs=12345 menu=ReturnTimeout", OnlineFightLog.ResultFieldsWithAfterSettle(s, OnlineAfterSettleOutcome.ReturnTimeout));
        Assert.DoesNotContain("giveUp", OnlineFightLog.ResultFields(s));
        Assert.DoesNotContain("giveUp", OnlineFightLog.ResultFieldsWithAfterSettle(s, menu));
    }

    // ======== 驱动级放弃:跑器自己收场时,收场原因怎样映射成放弃的结局 ========

    public static TheoryData<OnlineDungeonCombatOutcome, bool, OnlineGiveUpOutcome> RunnerEndings()
    {
        // 跑器的 8 种收场 × "1/45 发出了没有":发出了才有超时与断线,没发出就收场只能是 Skipped
        return new TheoryData<OnlineDungeonCombatOutcome, bool, OnlineGiveUpOutcome>
        {
            { OnlineDungeonCombatOutcome.GaveUp, true, OnlineGiveUpOutcome.Returned },
            { OnlineDungeonCombatOutcome.GaveUp, false, OnlineGiveUpOutcome.Returned },
            { OnlineDungeonCombatOutcome.Timeout, true, OnlineGiveUpOutcome.Timeout },
            { OnlineDungeonCombatOutcome.Timeout, false, OnlineGiveUpOutcome.Skipped },
            { OnlineDungeonCombatOutcome.ConnectionClosed, true, OnlineGiveUpOutcome.Closed },
            { OnlineDungeonCombatOutcome.ConnectionClosed, false, OnlineGiveUpOutcome.Skipped },
            { OnlineDungeonCombatOutcome.FramingFault, true, OnlineGiveUpOutcome.Closed },
            { OnlineDungeonCombatOutcome.FramingFault, false, OnlineGiveUpOutcome.Skipped },
            { OnlineDungeonCombatOutcome.SendFailed, true, OnlineGiveUpOutcome.SendFailed },
            { OnlineDungeonCombatOutcome.SendFailed, false, OnlineGiveUpOutcome.SendFailed },
            { OnlineDungeonCombatOutcome.Cancelled, true, OnlineGiveUpOutcome.Cancelled },
            { OnlineDungeonCombatOutcome.Cancelled, false, OnlineGiveUpOutcome.Cancelled },
            { OnlineDungeonCombatOutcome.Faulted, true, OnlineGiveUpOutcome.Faulted },
            { OnlineDungeonCombatOutcome.Faulted, false, OnlineGiveUpOutcome.Faulted },
            { OnlineDungeonCombatOutcome.ReturnReady, true, OnlineGiveUpOutcome.Skipped },
            { OnlineDungeonCombatOutcome.ReturnReady, false, OnlineGiveUpOutcome.Skipped },
        };
    }

    private static Task<OnlineDungeonCombatResult> RunnerResult(OnlineDungeonCombatOutcome outcome, string faultType)
    {
        var machine = new OnlineDungeonCombat(CombatKit.Map(CombatKit.Sequential(1)), new RecordingCombatPort());
        return Task.FromResult(new OnlineDungeonCombatResult(outcome, machine, 0, 0, 0, 0, 0, faultType));
    }

    [Theory]
    [MemberData(nameof(RunnerEndings))]
    public void A_runner_that_ends_during_the_wait_maps_to_the_give_up_outcome_by_its_ending_and_whether_the_command_went_out(
        OnlineDungeonCombatOutcome ending, bool sent, OnlineGiveUpOutcome expected)
    {
        Task<OnlineDungeonCombatResult> done = RunnerResult(ending, "SyntheticFault");

        Assert.Equal(expected, OnlineFightDriver.GiveUpOutcomeOf(done, sent, out string faultType));
        Assert.Equal("SyntheticFault", faultType);                 // 跑器的异常类型名原样带出
    }

    [Fact]
    public void Every_runner_ending_has_a_row_in_the_mapping_table()
    {
        // 新增跑器收场原因时这里会红,逼着回来决定它在放弃里算什么
        Assert.Equal(8, Enum.GetValues(typeof(OnlineDungeonCombatOutcome)).Length);
        OnlineDungeonCombatOutcome[] covered = RunnerEndings().Select(row => (OnlineDungeonCombatOutcome)row[0]).Distinct().ToArray();
        foreach (OnlineDungeonCombatOutcome ending in Enum.GetValues(typeof(OnlineDungeonCombatOutcome)))
        {
            Assert.Contains(ending, covered);
        }
    }

    [Fact]
    public void A_runner_task_that_did_not_complete_normally_maps_to_faulted_without_a_fault_type()
    {
        Assert.Equal(OnlineGiveUpOutcome.Faulted, OnlineFightDriver.GiveUpOutcomeOf(Task.FromException<OnlineDungeonCombatResult>(new InvalidOperationException("x")), true, out string faulted));
        Assert.Null(faulted);
        Assert.Equal(OnlineGiveUpOutcome.Faulted, OnlineFightDriver.GiveUpOutcomeOf(Task.FromCanceled<OnlineDungeonCombatResult>(new CancellationToken(true)), false, out string canceled));
        Assert.Null(canceled);
    }

    // ======== 缺省值与选项边界 ========

    [Fact]
    public void Defaults_are_off_and_the_wait_is_the_runner_give_up_timeout()
    {
        Assert.False(new OnlineFightOptions().GiveUpOnFailure);
        Assert.Equal(TimeSpan.FromSeconds(15), OnlineFightOptions.DefaultGiveUpWait);
        Assert.Equal(OnlineFightOptions.DefaultGiveUpWait, new OnlineFightOptions().GiveUpWait);
        // 三处缺省是同一份:跑器的放弃上限、驱动的放弃等待、会话层放弃的等待
        Assert.Equal(new OnlineDungeonCombatOptions().GiveUpTimeout, OnlineFightOptions.DefaultGiveUpWait);
        Assert.Equal(new OnlineDungeonCombatOptions().GiveUpTimeout, OnlineAfterSettleOptions.DefaultGiveUpWaitTimeout);
        Assert.Equal(OnlineAfterSettleOptions.DefaultGiveUpWaitTimeout, new OnlineAfterSettleOptions().GiveUpWaitTimeout);
    }

    [Fact]
    public void A_non_positive_or_oversized_give_up_wait_is_refused_when_the_driver_is_built()
    {
        var rig = new GiveUpRig(1);
        foreach (TimeSpan bad in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromDays(40) })
        {
            var options = new OnlineFightOptions { GiveUpWait = bad };
            Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineFightDriver(rig.Runner, rig.Map, options));
        }
        // 上界恰好 int.MaxValue 毫秒可以
        var edge = new OnlineFightOptions { GiveUpWait = TimeSpan.FromMilliseconds(int.MaxValue) };
        Assert.NotNull(new OnlineFightDriver(rig.Runner, rig.Map, edge));

        var session = new CombatFakeSession();
        foreach (TimeSpan bad in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromDays(40) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { GiveUpWaitTimeout = bad }));
        }
        Assert.NotNull(new OnlineAfterSettleDriver(session, new OnlineAfterSettleOptions { GiveUpWaitTimeout = TimeSpan.FromMilliseconds(int.MaxValue) }));
    }

    // ======== 退出码:没有新增、没有改动 ========

    [Fact]
    public void The_exit_code_tables_are_exactly_the_old_ones_and_give_up_adds_no_code()
    {
        var fightCodes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (FieldInfo field in typeof(OnlineFightExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(int))
            {
                fightCodes[field.Name] = (int)field.GetRawConstantValue();
            }
        }
        Assert.Equal(
            new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["Success"] = 0, ["SettingsFailed"] = 2, ["TownEntryFailed"] = 3, ["AuthFailed"] = 5, ["DungeonEntryFailed"] = 6,
                ["RefusedStop"] = 10, ["NoClearNotice"] = 11, ["SettleIncomplete"] = 12, ["SessionClosed"] = 13, ["Faulted"] = 14,
                ["Cancelled"] = 15, ["Usage"] = 64, ["Unexpected"] = 70,
            },
            fightCodes);

        var menuCodes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (FieldInfo field in typeof(OnlineAfterSettleExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(int))
            {
                menuCodes[field.Name] = (int)field.GetRawConstantValue();
            }
        }
        Assert.Equal(
            new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["AdvanceTimeout"] = 16, ["EplpTimeout"] = 17, ["ReturnTimeout"] = 18, ["ReplyFailed"] = 19, ["ReplyMalformed"] = 20,
                ["SessionClosed"] = 21, ["Faulted"] = 22, ["SelectCardSendFailed"] = 23,
            },
            menuCodes);

        // 退出码只由战斗结局(与菜单结局)决定:放弃的结局不在任何映射的输入里
        Assert.Equal(10, OnlineFightExitCodes.ForOutcome(OnlineFightOutcome.RefusedStop));
        Assert.Equal(11, OnlineFightExitCodes.ForOutcome(OnlineFightOutcome.NoClearNotice));
        Assert.Equal(12, OnlineFightExitCodes.ForOutcome(OnlineFightOutcome.SettleIncomplete));
        Assert.Equal(18, OnlineAfterSettleExitCodes.ForOutcome(OnlineAfterSettleOutcome.ReturnTimeout));
    }
}
