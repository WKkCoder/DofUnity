using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 无头战斗的"对外约定"测试:探针 fight 子命令的命令行解析、退出码表、汇总字段的拼法(最终那行 <c>PROBE_FIGHT result …</c> 就是它)、
/// 击杀载荷待遇的一句话判读。都是纯函数,不连网、不需要会话。
/// </summary>
public class OnlineFightCliTests
{
    private static OnlineFightArguments Parse(params string[] args)
    {
        Assert.True(OnlineFightArguments.TryParse(args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem), "应当解析成功,实际问题 " + problem);
        Assert.Equal(OnlineFightArgumentProblem.None, problem);
        Assert.NotNull(parsed);
        return parsed;
    }

    private static OnlineFightArgumentProblem Problem(string joined)
    {
        // "|" 分隔各个参数;空串表示没有参数
        string[] args = joined.Length == 0 ? new string[0] : joined.Split('|');
        Assert.False(OnlineFightArguments.TryParse(args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem), "应当解析失败:" + joined);
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
        int firstRefused = -1)
    {
        return new OnlineFightSummary(outcome, monsters, sent, sent, confirmed, refused, blocked, clear, settlement, ready,
            ready ? OnlineCombatPhase.Settled : OnlineCombatPhase.Fighting, null, null, firstRefused, 0, TimeSpan.Zero, new TimeSpan[0]);
    }

    // ======== 命令行 ========

    [Fact]
    public void Only_the_dungeon_and_the_mode_are_required_and_everything_else_has_the_documented_default()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "2");

        Assert.Null(a.RepoRoot);
        Assert.Equal(901, a.DungeonId);
        Assert.Equal(2, a.Mode);
        Assert.Equal(400, a.PaceMs);
        Assert.Equal(TimeSpan.FromSeconds(25), a.ClearWait);
        Assert.Equal(TimeSpan.FromSeconds(25), a.SettleWait);
        Assert.False(a.ContinueOnRefuse);
        Assert.Equal(0, a.StaySeconds);
        // 命令行的缺省与驱动选项的缺省是同一份
        var options = new OnlineFightOptions();
        Assert.Equal(options.PaceMs, a.PaceMs);
        Assert.Equal(options.ClearWait, a.ClearWait);
        Assert.Equal(options.SettleWait, a.SettleWait);
        Assert.Equal(options.ContinueOnRefuse, a.ContinueOnRefuse);
    }

    [Fact]
    public void Every_option_is_read_and_a_repo_root_is_only_taken_from_the_first_position()
    {
        OnlineFightArguments a = Parse("some-root", "--dungeon", "901", "4", "--pace-ms", "250", "--clear-wait", "40", "--settle-wait", "30", "--continue-on-refuse", "--stay", "20");

        Assert.Equal("some-root", a.RepoRoot);
        Assert.Equal(901, a.DungeonId);
        Assert.Equal(4, a.Mode);
        Assert.Equal(250, a.PaceMs);
        Assert.Equal(TimeSpan.FromSeconds(40), a.ClearWait);
        Assert.Equal(TimeSpan.FromSeconds(30), a.SettleWait);
        Assert.True(a.ContinueOnRefuse);
        Assert.Equal(20, a.StaySeconds);

        // 选项的顺序随意;重复给的以最后一次为准
        OnlineFightArguments b = Parse("--stay", "5", "--pace-ms", "100", "--dungeon", "0", "0", "--pace-ms", "200");
        Assert.Null(b.RepoRoot);
        Assert.Equal(200, b.PaceMs);
        Assert.Equal(5, b.StaySeconds);
    }

    [Fact]
    public void The_limits_of_every_numeric_option_are_accepted()
    {
        OnlineFightArguments low = Parse("--dungeon", "0", "0", "--pace-ms", "0", "--clear-wait", "1", "--settle-wait", "1", "--stay", "0");
        Assert.Equal(0, low.DungeonId);
        Assert.Equal(0, low.Mode);
        Assert.Equal(0, low.PaceMs);
        Assert.Equal(TimeSpan.FromSeconds(1), low.ClearWait);
        Assert.Equal(TimeSpan.FromSeconds(1), low.SettleWait);
        Assert.Equal(0, low.StaySeconds);

        OnlineFightArguments high = Parse("--dungeon", "65535", "4", "--pace-ms", "60000", "--clear-wait", "3600", "--settle-wait", "3600", "--stay", "3600");
        Assert.Equal(65535, high.DungeonId);
        Assert.Equal(4, high.Mode);
        Assert.Equal(OnlineFightArguments.MaxPaceMs, high.PaceMs);
        Assert.Equal(TimeSpan.FromSeconds(OnlineFightArguments.MaxWaitSeconds), high.ClearWait);
        Assert.Equal(TimeSpan.FromSeconds(OnlineFightArguments.MaxWaitSeconds), high.SettleWait);
        Assert.Equal(OnlineFightArguments.MaxWaitSeconds, high.StaySeconds);
    }

    [Theory]
    [InlineData("", OnlineFightArgumentProblem.MissingDungeon)]
    [InlineData("some-root", OnlineFightArgumentProblem.MissingDungeon)]
    [InlineData("--continue-on-refuse", OnlineFightArgumentProblem.MissingDungeon)]
    [InlineData("--dungeon", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon|901", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon|901|5", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon|65536|0", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon|-1|0", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon|9x|0", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon||0", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon|901|0|--pace-ms", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--pace-ms|abc", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--pace-ms|-5", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--pace-ms|60001", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--pace-ms|1.5", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--pace-ms|+5", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--clear-wait|0", OnlineFightArgumentProblem.BadClearWait)]
    [InlineData("--dungeon|901|0|--clear-wait|3601", OnlineFightArgumentProblem.BadClearWait)]
    [InlineData("--dungeon|901|0|--clear-wait", OnlineFightArgumentProblem.BadClearWait)]
    [InlineData("--dungeon|901|0|--settle-wait|0", OnlineFightArgumentProblem.BadSettleWait)]
    [InlineData("--dungeon|901|0|--settle-wait|x", OnlineFightArgumentProblem.BadSettleWait)]
    [InlineData("--dungeon|901|0|--stay|-1", OnlineFightArgumentProblem.BadStay)]
    [InlineData("--dungeon|901|0|--stay|3601", OnlineFightArgumentProblem.BadStay)]
    [InlineData("--dungeon|901|0|--bogus", OnlineFightArgumentProblem.UnknownArgument)]
    [InlineData("--dungeon|901|0|some-root", OnlineFightArgumentProblem.UnknownArgument)]
    public void A_bad_command_line_is_refused_with_the_name_of_the_problem(string joined, OnlineFightArgumentProblem expected)
    {
        Assert.Equal(expected, Problem(joined));
    }

    // ======== 退出码 ========

    [Theory]
    [InlineData(OnlineFightOutcome.AllDone, 0)]
    [InlineData(OnlineFightOutcome.RefusedStop, 10)]
    [InlineData(OnlineFightOutcome.NoClearNotice, 11)]
    [InlineData(OnlineFightOutcome.SettleIncomplete, 12)]
    [InlineData(OnlineFightOutcome.SessionClosed, 13)]
    [InlineData(OnlineFightOutcome.Faulted, 14)]
    [InlineData(OnlineFightOutcome.Cancelled, 15)]
    public void Each_outcome_has_its_own_exit_code_and_only_success_is_zero(OnlineFightOutcome outcome, int expected)
    {
        Assert.Equal(expected, OnlineFightExitCodes.ForOutcome(outcome));
        Assert.Equal(outcome == OnlineFightOutcome.AllDone, expected == OnlineFightExitCodes.Success);
    }

    [Fact]
    public void Every_exit_code_of_the_subcommand_is_distinct()
    {
        int[] codes =
        {
            OnlineFightExitCodes.Success,
            OnlineFightExitCodes.SettingsFailed,
            OnlineFightExitCodes.TownEntryFailed,
            OnlineFightExitCodes.AuthFailed,
            OnlineFightExitCodes.DungeonEntryFailed,
            OnlineFightExitCodes.RefusedStop,
            OnlineFightExitCodes.NoClearNotice,
            OnlineFightExitCodes.SettleIncomplete,
            OnlineFightExitCodes.SessionClosed,
            OnlineFightExitCodes.Faulted,
            OnlineFightExitCodes.Cancelled,
            OnlineFightExitCodes.Usage,
            OnlineFightExitCodes.Unexpected,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes.Where(c => c != OnlineFightExitCodes.Success), c => Assert.NotEqual(0, c));
        // 与 orchestrated 子命令同义的两个码保持同值
        Assert.Equal(2, OnlineFightExitCodes.SettingsFailed);
        Assert.Equal(3, OnlineFightExitCodes.TownEntryFailed);
    }

    [Fact]
    public void Authentication_failures_get_their_own_exit_code_and_every_other_town_failure_the_generic_one()
    {
        var authentication = new[] { OnlineFailure.GatewayLoginFailed, OnlineFailure.GameLoginRejected, OnlineFailure.BadLoginRequest };
        foreach (OnlineFailure failure in Enum.GetValues<OnlineFailure>())
        {
            int expected = authentication.Contains(failure) ? OnlineFightExitCodes.AuthFailed : OnlineFightExitCodes.TownEntryFailed;
            Assert.Equal(expected, OnlineFightExitCodes.ForTownFailure(failure));
        }
    }

    // ======== 汇总字段 ========

    [Fact]
    public void The_result_fields_follow_the_agreed_format_exactly()
    {
        Assert.Equal(
            "outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1",
            OnlineFightLog.ResultFields(Summary()));
        Assert.Equal(
            "outcome=RefusedStop monsters=6 sent=3 confirmed=2 refused=1 blocked=0 clearNotice=0 settlement=none returnReady=0",
            OnlineFightLog.ResultFields(Summary(OnlineFightOutcome.RefusedStop, monsters: 6, sent: 3, confirmed: 2, refused: 1, clear: false, settlement: OnlineFightSettlement.None, ready: false, firstRefused: 2)));
        Assert.Equal(
            "outcome=SettleIncomplete monsters=2 sent=2 confirmed=2 refused=0 blocked=1 clearNotice=1 settlement=partial returnReady=0",
            OnlineFightLog.ResultFields(Summary(OnlineFightOutcome.SettleIncomplete, monsters: 2, sent: 2, confirmed: 2, blocked: 1, settlement: OnlineFightSettlement.Partial, ready: false)));
        Assert.Equal(OnlineFightLog.ResultFields(Summary()), Summary().ToString());
    }

    [Fact]
    public void The_verdict_fields_name_the_headline_answer_and_the_first_refused_local_number()
    {
        Assert.Equal("kill=accepted pending=0 firstRefused=-", OnlineFightLog.VerdictFields(Summary()));
        Assert.Equal(
            "kill=refused pending=1 firstRefused=2",
            OnlineFightLog.VerdictFields(Summary(OnlineFightOutcome.RefusedStop, monsters: 6, sent: 4, confirmed: 2, refused: 1, firstRefused: 2)));
    }

    [Theory]
    [InlineData(0, 0, 0, OnlineFightKillVerdict.None)]
    [InlineData(5, 5, 0, OnlineFightKillVerdict.Accepted)]
    [InlineData(5, 3, 0, OnlineFightKillVerdict.Partial)]
    [InlineData(5, 0, 0, OnlineFightKillVerdict.Silent)]
    [InlineData(5, 2, 1, OnlineFightKillVerdict.Refused)]
    [InlineData(3, 0, 1, OnlineFightKillVerdict.Refused)]
    [InlineData(1, 0, 1, OnlineFightKillVerdict.Refused)]
    [InlineData(1, 1, 0, OnlineFightKillVerdict.Accepted)]
    public void The_kill_verdict_reads_only_the_counts(int sent, int confirmed, int refused, OnlineFightKillVerdict expected)
    {
        OnlineFightSummary s = Summary(sent: sent, confirmed: confirmed, refused: refused, monsters: 7);
        Assert.Equal(expected, s.KillVerdict);
        Assert.Equal(sent - confirmed - refused, s.Pending);
    }

    [Fact]
    public void The_option_defaults_are_the_documented_ones_and_bad_options_are_refused()
    {
        var o = new OnlineFightOptions();
        Assert.Equal(400, o.PaceMs);
        Assert.False(o.ContinueOnRefuse);
        Assert.Equal(TimeSpan.FromSeconds(25), o.ClearWait);
        Assert.Equal(TimeSpan.FromSeconds(25), o.SettleWait);
        Assert.Equal(TimeSpan.FromMilliseconds(50), o.PollInterval);
        Assert.Null(o.Clock);

        var session = new CombatFakeSession();
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        StartMapNotice map = CombatKit.Map(2);
        Assert.Throws<ArgumentNullException>(() => new OnlineFightDriver(null, map));
        Assert.Throws<ArgumentNullException>(() => new OnlineFightDriver(runner, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineFightDriver(runner, map, new OnlineFightOptions { PaceMs = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineFightDriver(runner, map, new OnlineFightOptions { ClearWait = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineFightDriver(runner, map, new OnlineFightOptions { SettleWait = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineFightDriver(runner, map, new OnlineFightOptions { PollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineFightDriver(runner, map, new OnlineFightOptions { ClearWait = TimeSpan.FromDays(40) }));
        // 零间隔是合法的(不等)
        Assert.NotNull(new OnlineFightDriver(runner, map, new OnlineFightOptions { PaceMs = 0 }));
    }
}
