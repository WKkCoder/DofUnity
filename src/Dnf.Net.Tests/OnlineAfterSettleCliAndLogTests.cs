using System.Reflection;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// r20-probesettle 的 CLI 解析、退出码(16+)与脱敏输出格式(不含真实值)测试。
/// </summary>
public class OnlineAfterSettleCliAndLogTests
{
    private static OnlineFightArguments Parse(params string[] args)
    {
        Assert.True(OnlineFightArguments.TryParse(args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem),
            "应当解析成功,实际问题 " + problem);
        Assert.Equal(OnlineFightArgumentProblem.None, problem);
        Assert.NotNull(parsed);
        return parsed;
    }

    private static OnlineFightArgumentProblem Problem(string joined)
    {
        string[] args = joined.Length == 0 ? Array.Empty<string>() : joined.Split('|');
        Assert.False(OnlineFightArguments.TryParse(args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem),
            "应当解析失败:" + joined);
        Assert.Null(parsed);
        return problem;
    }

    private static OnlineFightSummary Summary(
        OnlineFightOutcome outcome = OnlineFightOutcome.AllDone,
        int monsters = 7,
        int sent = 7,
        int confirmed = 7,
        int refused = 0,
        int blocked = 0,
        bool clear = true,
        OnlineFightSettlement settlement = OnlineFightSettlement.Complete,
        bool ready = true,
        TimeSpan? elapsed = null,
        TimeSpan? clearDuration = null)
    {
        return new OnlineFightSummary(
            outcome,
            monsters,
            sent,
            sent,
            confirmed,
            refused,
            blocked,
            clear,
            settlement,
            ready,
            ready ? OnlineCombatPhase.Settled : OnlineCombatPhase.Fighting,
            null,
            null,
            -1,
            0,
            elapsed ?? TimeSpan.Zero,
            Array.Empty<TimeSpan>(),
            clearDuration);
    }

    // ======== CLI 解析:新选项正常路径 ========

    [Fact]
    public void Without_new_options_all_new_properties_keep_their_disabled_defaults()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "2");

        Assert.False(a.AfterSettle);
        Assert.Null(a.MinClearSeconds);
        Assert.Null(a.MinClearDuration);
        Assert.Null(a.MenuChoice);
        Assert.Null(a.EffectiveMenuChoice);
    }

    [Fact]
    public void Min_clear_seconds_can_be_used_without_after_settle()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "1", "--min-clear-seconds", "15");

        Assert.False(a.AfterSettle);
        Assert.Equal(15, a.MinClearSeconds);
        Assert.Equal(TimeSpan.FromSeconds(15), a.MinClearDuration);
        Assert.Null(a.MenuChoice);
        Assert.Null(a.EffectiveMenuChoice);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("45", 45)]
    [InlineData("3600", 3600)]
    public void After_settle_with_min_clear_seconds_in_bounds_and_optional_menu_choice_town_succeeds(string secondsText, int expectedSeconds)
    {
        OnlineFightArguments a = Parse("root-dir", "--dungeon", "902", "3", "--min-clear-seconds", secondsText, "--after-settle");
        Assert.Equal("root-dir", a.RepoRoot);
        Assert.True(a.AfterSettle);
        Assert.Equal(expectedSeconds, a.MinClearSeconds);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), a.MinClearDuration);
        Assert.Null(a.MenuChoice);
        Assert.Equal(EplpCommand.ReturnToTown, a.EffectiveMenuChoice);

        OnlineFightArguments b = Parse("--after-settle", "--menu-choice", "town", "--min-clear-seconds", secondsText, "--dungeon", "902", "0");
        Assert.True(b.AfterSettle);
        Assert.Equal(expectedSeconds, b.MinClearSeconds);
        Assert.Equal("town", b.MenuChoice);
        Assert.Equal(EplpCommand.ReturnToTown, b.EffectiveMenuChoice);
    }

    // ======== CLI 解析:非法组合与边界 ========

    [Theory]
    [InlineData("--dungeon|901|0|--after-settle", OnlineFightArgumentProblem.MissingMinClearSeconds)]
    [InlineData("--after-settle|--dungeon|901|0", OnlineFightArgumentProblem.MissingMinClearSeconds)]
    [InlineData("--dungeon|901|0|--menu-choice|town", OnlineFightArgumentProblem.MenuChoiceWithoutAfterSettle)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|10|--menu-choice|town", OnlineFightArgumentProblem.MenuChoiceWithoutAfterSettle)]
    [InlineData("--dungeon|901|0|--min-clear-seconds", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|0", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|3601", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|-1", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|+5", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|1.5", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|abc", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|1000000000", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|retry", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|select", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|Town", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|TOWN", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|2", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--after-settle", OnlineFightArgumentProblem.DuplicateOption)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--min-clear-seconds|20", OnlineFightArgumentProblem.DuplicateOption)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|10|--min-clear-seconds|10", OnlineFightArgumentProblem.DuplicateOption)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|town|--menu-choice|town", OnlineFightArgumentProblem.DuplicateOption)]
    [InlineData("--dungeon|901|0|--min-clear-seconds|10|--min-clear-seconds|0", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|town|--menu-choice|retry", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--permits-only|--after-settle|--min-clear-seconds|10", OnlineFightArgumentProblem.Conflict)]
    [InlineData("--after-settle|--min-clear-seconds|10|--permits-only", OnlineFightArgumentProblem.Conflict)]
    [InlineData("--permits-only|--after-settle", OnlineFightArgumentProblem.Conflict)]
    [InlineData("--permits-only|--min-clear-seconds|10", OnlineFightArgumentProblem.Conflict)]
    [InlineData("--min-clear-seconds|10|--permits-only", OnlineFightArgumentProblem.Conflict)]
    [InlineData("--permits-only|--after-settle|--min-clear-seconds|10|--menu-choice|town", OnlineFightArgumentProblem.Conflict)]
    [InlineData("--permits-only|--menu-choice|town", OnlineFightArgumentProblem.Conflict)]
    [InlineData("--permits-only|--min-clear-seconds|0", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--permits-only|--menu-choice|retry", OnlineFightArgumentProblem.BadMenuChoice)]
    [InlineData("--after-settle|--min-clear-seconds|10", OnlineFightArgumentProblem.MissingDungeon)]
    public void Bad_or_conflicting_new_options_fail_with_expected_problem(string joined, OnlineFightArgumentProblem expected)
    {
        Assert.Equal(expected, Problem(joined));
    }

    [Fact]
    public void Bad_menu_choice_never_echoes_user_input_in_problem_name()
    {
        OnlineFightArgumentProblem p = Problem("secret-root|--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|secret-choice");
        Assert.Equal(OnlineFightArgumentProblem.BadMenuChoice, p);
        string abort = "PROBE_FIGHT abort at=usage reason=" + p + " exit=" + OnlineFightExitCodes.Usage;
        Assert.Equal("PROBE_FIGHT abort at=usage reason=BadMenuChoice exit=64", abort);
        Assert.DoesNotContain("secret", abort);
    }

    // ======== 退出码(16+) ========

    [Fact]
    public void After_settle_exit_codes_are_pinned_literally_and_disjoint_from_fight_exit_codes()
    {
        Assert.Equal(16, OnlineAfterSettleExitCodes.AdvanceTimeout);
        Assert.Equal(17, OnlineAfterSettleExitCodes.EplpTimeout);
        Assert.Equal(18, OnlineAfterSettleExitCodes.ReturnTimeout);
        Assert.Equal(19, OnlineAfterSettleExitCodes.ReplyFailed);
        Assert.Equal(20, OnlineAfterSettleExitCodes.ReplyMalformed);
        Assert.Equal(21, OnlineAfterSettleExitCodes.SessionClosed);
        Assert.Equal(22, OnlineAfterSettleExitCodes.Faulted);
        Assert.Equal(23, OnlineAfterSettleExitCodes.SelectCardSendFailed);

        var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["AdvanceTimeout"] = 16,
            ["EplpTimeout"] = 17,
            ["ReturnTimeout"] = 18,
            ["ReplyFailed"] = 19,
            ["ReplyMalformed"] = 20,
            ["SessionClosed"] = 21,
            ["Faulted"] = 22,
            ["SelectCardSendFailed"] = 23,
        };
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (FieldInfo field in typeof(OnlineAfterSettleExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(int))
            {
                actual[field.Name] = (int)field.GetRawConstantValue();
            }
        }
        Assert.Equal(expected, actual);

        var baseCodes = typeof(OnlineFightExitCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .Select(f => (int)f.GetRawConstantValue())
            .ToHashSet();
        Assert.All(actual.Values, code => Assert.DoesNotContain(code, baseCodes));
        Assert.Equal(actual.Count, actual.Values.Distinct().Count());
    }

    [Theory]
    [InlineData(OnlineAfterSettleOutcome.ReturnedToTown, 0)]
    [InlineData(OnlineAfterSettleOutcome.ScoreScrollTimeout, 16)]
    [InlineData(OnlineAfterSettleOutcome.CardRightTimeout, 16)]
    [InlineData(OnlineAfterSettleOutcome.EplpTimeout, 17)]
    [InlineData(OnlineAfterSettleOutcome.ReturnTimeout, 18)]
    [InlineData(OnlineAfterSettleOutcome.ReplyFailed, 19)]
    [InlineData(OnlineAfterSettleOutcome.ReplyMalformed, 20)]
    [InlineData(OnlineAfterSettleOutcome.SessionClosed, 21)]
    [InlineData(OnlineAfterSettleOutcome.Faulted, 22)]
    [InlineData(OnlineAfterSettleOutcome.SelectCardSendFailed, 23)]
    [InlineData(OnlineAfterSettleOutcome.Cancelled, 15)]
    public void Every_after_settle_outcome_maps_to_its_exact_exit_code(OnlineAfterSettleOutcome outcome, int expected)
    {
        Assert.Equal(0, (int)OnlineAfterSettleOutcome.ReturnedToTown);
        Assert.Equal(expected, OnlineAfterSettleExitCodes.ForOutcome(outcome));
    }

    // ======== 日志格式与旧路径不变性 ========

    [Fact]
    public void Legacy_result_fields_remain_byte_identical_even_with_clear_duration_set()
    {
        OnlineFightSummary s = Summary(elapsed: TimeSpan.FromSeconds(15), clearDuration: TimeSpan.FromSeconds(12));

        Assert.Equal(
            "outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1",
            OnlineFightLog.ResultFields(s));
        Assert.Equal(TimeSpan.FromSeconds(12), s.ClearDuration);
    }

    [Fact]
    public void Result_fields_with_after_settle_appends_clearMs_and_menu_verbatim()
    {
        OnlineFightSummary s = Summary(elapsed: TimeSpan.FromMilliseconds(18500), clearDuration: TimeSpan.FromMilliseconds(12345));
        var menu = new OnlineAfterSettleSummary(
            OnlineAfterSettleOutcome.ReturnedToTown, false, 1, null, 0, 2, 0, 0, 0, null, TimeSpan.FromMilliseconds(250));

        Assert.Equal(
            "outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1 clearMs=12345 menu=ReturnedToTown",
            OnlineFightLog.ResultFieldsWithAfterSettle(s, menu));
        Assert.Equal(
            "outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1 clearMs=12345 menu=-",
            OnlineFightLog.ResultFieldsWithAfterSettle(s, (OnlineAfterSettleSummary)null));
        Assert.Equal(
            "outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1 clearMs=12345 menu=ReturnTimeout",
            OnlineFightLog.ResultFieldsWithAfterSettle(s, OnlineAfterSettleOutcome.ReturnTimeout));

        // 负时长钳制到 0
        OnlineFightSummary neg = Summary(clearDuration: TimeSpan.FromMilliseconds(-50));
        Assert.Equal(
            "outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1 clearMs=0 menu=-",
            OnlineFightLog.ResultFieldsWithAfterSettle(neg, (OnlineAfterSettleSummary)null));
    }

    [Theory]
    [InlineData("72", true, "ok", 12, 0, "PROBE_FIGHT stage=settle-menu step=72 sent=1 reply=ok elapsedMs=12 ignored=0")]
    [InlineData("73", true, "timeout", 5000, 3, "PROBE_FIGHT stage=settle-menu step=73 sent=1 reply=timeout elapsedMs=5000 ignored=3")]
    [InlineData("74", false, "skipped", 0, 0, "PROBE_FIGHT stage=settle-menu step=74 sent=0 reply=skipped elapsedMs=0 ignored=0")]
    [InlineData("75", true, "failed", 80, 1, "PROBE_FIGHT stage=settle-menu step=75 sent=1 reply=failed elapsedMs=80 ignored=1")]
    [InlineData("return", false, "closed", 150, 4, "PROBE_FIGHT stage=settle-menu step=return sent=0 reply=closed elapsedMs=150 ignored=4")]
    public void After_settle_step_line_matches_exact_sanitized_format(string step, bool sent, string reply, long elapsedMs, int ignored, string expected)
    {
        Assert.Equal(expected, OnlineFightLog.AfterSettleStep(step, sent, reply, elapsedMs, ignored));
    }
}
