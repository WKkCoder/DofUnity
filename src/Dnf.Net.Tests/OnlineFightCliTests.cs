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

    [Theory]
    [InlineData("--continue-on-refuse|some-root")]                              // 紧跟在第一个选项后面的裸字符串:不是仓库根
    [InlineData("--continue-on-refuse|some-root|--dungeon|901|0")]              // 同上,别的都对也一样是错
    [InlineData("some-root|other-root|--dungeon|901|0")]                        // 第二个裸字符串不能把第一个仓库根顶掉
    [InlineData("--dungeon|901|0|--continue-on-refuse|some-root")]
    [InlineData("--dungeon|901|0|--pace-ms|250|some-root")]
    public void A_bare_word_after_the_first_position_is_an_unknown_argument_and_never_a_repo_root(string joined)
    {
        Assert.Equal(OnlineFightArgumentProblem.UnknownArgument, Problem(joined));
    }

    [Fact]
    public void The_repo_root_taken_from_the_first_position_is_kept_as_it_was_given()
    {
        OnlineFightArguments a = Parse("first-root", "--continue-on-refuse", "--dungeon", "901", "0");

        Assert.Equal("first-root", a.RepoRoot);
        Assert.True(a.ContinueOnRefuse);
        Assert.Equal(901, a.DungeonId);
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

    [Fact]
    public void The_exit_code_numbers_are_an_external_contract_and_are_pinned_literally()
    {
        // 集成者读退出码判结局:数值一变,脚本就读错。这里一律用写死的数字,不拿常量名互相比
        Assert.Equal(0, OnlineFightExitCodes.Success);
        Assert.Equal(2, OnlineFightExitCodes.SettingsFailed);
        Assert.Equal(3, OnlineFightExitCodes.TownEntryFailed);
        Assert.Equal(5, OnlineFightExitCodes.AuthFailed);
        Assert.Equal(6, OnlineFightExitCodes.DungeonEntryFailed);
        Assert.Equal(10, OnlineFightExitCodes.RefusedStop);
        Assert.Equal(11, OnlineFightExitCodes.NoClearNotice);
        Assert.Equal(12, OnlineFightExitCodes.SettleIncomplete);
        Assert.Equal(13, OnlineFightExitCodes.SessionClosed);
        Assert.Equal(14, OnlineFightExitCodes.Faulted);
        Assert.Equal(15, OnlineFightExitCodes.Cancelled);
        Assert.Equal(64, OnlineFightExitCodes.Usage);
        Assert.Equal(70, OnlineFightExitCodes.Unexpected);

        // 一个也不多、不少:反射读出全部公开的整数常量,与上面写死的名单逐项相等(新加一个码而没钉数值,这里就会红)
        var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["Success"] = 0,
            ["SettingsFailed"] = 2,
            ["TownEntryFailed"] = 3,
            ["AuthFailed"] = 5,
            ["DungeonEntryFailed"] = 6,
            ["RefusedStop"] = 10,
            ["NoClearNotice"] = 11,
            ["SettleIncomplete"] = 12,
            ["SessionClosed"] = 13,
            ["Faulted"] = 14,
            ["Cancelled"] = 15,
            ["Usage"] = 64,
            ["Unexpected"] = 70,
        };
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (System.Reflection.FieldInfo field in typeof(OnlineFightExitCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(int))
            {
                actual[field.Name] = (int)field.GetRawConstantValue();
            }
        }
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(OnlineFailure.GatewayLoginFailed, 5)]
    [InlineData(OnlineFailure.GameLoginRejected, 5)]
    [InlineData(OnlineFailure.BadLoginRequest, 5)]
    [InlineData(OnlineFailure.GameLoginNoReply, 3)]
    public void The_exit_code_of_a_town_failure_is_a_literal_number(OnlineFailure failure, int expected)
    {
        Assert.Equal(expected, OnlineFightExitCodes.ForTownFailure(failure));
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

    [Theory]
    [InlineData(2, 1, 0, OnlineFightKillVerdict.Partial)]       // 差一只在途:已发 N、确认 N-1、没有被拒,不是"全部确认"
    [InlineData(3, 2, 0, OnlineFightKillVerdict.Partial)]
    [InlineData(5, 4, 0, OnlineFightKillVerdict.Partial)]
    [InlineData(7, 6, 0, OnlineFightKillVerdict.Partial)]
    [InlineData(1, 0, 0, OnlineFightKillVerdict.Silent)]        // 只发一只、一点回音也没有:静默
    [InlineData(2, 0, 0, OnlineFightKillVerdict.Silent)]
    [InlineData(2, 2, 0, OnlineFightKillVerdict.Accepted)]
    [InlineData(7, 7, 0, OnlineFightKillVerdict.Accepted)]
    [InlineData(5, 4, 1, OnlineFightKillVerdict.Refused)]       // 4 条确认加 1 条被拒:没有在途,同样是被拒
    public void The_kill_verdict_tells_one_missing_confirmation_from_a_fully_confirmed_set(int sent, int confirmed, int refused, OnlineFightKillVerdict expected)
    {
        OnlineFightSummary s = Summary(sent: sent, confirmed: confirmed, refused: refused, monsters: 7);

        Assert.Equal(expected, s.KillVerdict);
        Assert.Equal(sent - confirmed - refused, s.Pending);
        Assert.StartsWith("kill=" + expected.ToString().ToLowerInvariant() + " pending=" + (sent - confirmed - refused) + " ", OnlineFightLog.VerdictFields(s));
    }

    [Theory]
    [InlineData(0, 1, "kill=refused pending=1 firstRefused=0")]     // 第一只被拒是本地序号 0:写 0,不是 -
    [InlineData(1, 1, "kill=refused pending=1 firstRefused=1")]
    [InlineData(3, 2, "kill=refused pending=0 firstRefused=3")]
    [InlineData(-1, 0, "kill=partial pending=2 firstRefused=-")]    // 没有被拒才写 -
    public void The_first_refused_field_is_a_dash_only_when_no_monster_was_refused(int firstRefused, int refused, string expected)
    {
        OnlineFightSummary s = Summary(OnlineFightOutcome.RefusedStop, monsters: 6, sent: 4, confirmed: 2, refused: refused, firstRefused: firstRefused);

        Assert.Equal(expected, OnlineFightLog.VerdictFields(s));
    }

    // ======== 探针转行的白名单 ========

    [Theory]
    [InlineData("DNF_NET stage=dungeon step=SelectDungeon", "stage=dungeon step=SelectDungeon")]
    [InlineData("DNF_NET stage=dungeon frame n=0 step=WaitLoadingNotice NOTI/27(26)@561", "stage=dungeon frame n=0 step=WaitLoadingNotice NOTI/27(26)@561")]
    [InlineData("DNF_NET stage=dungeon done step=Done frames=9 unopened=0 outOfOrder=0 duplicate=0 loading27=26 info28=1 map29=1", "stage=dungeon done step=Done frames=9 unopened=0 outOfOrder=0 duplicate=0 loading27=26 info28=1 map29=1")]
    [InlineData("DNF_NET stage=dungeon failed step=WaitStartMap failure=Timeout send=- rejected=- code=- frames=2", "stage=dungeon failed step=WaitStartMap failure=Timeout send=- rejected=- code=- frames=2")]
    [InlineData("DNF_NET stage=combat begin monsters=7 duplicateNumbers=0", "stage=combat begin monsters=7 duplicateNumbers=0")]
    [InlineData("DNF_NET stage=combat event=KillSent local=0 sent=1 confirmed=0 total=7", "stage=combat event=KillSent local=0 sent=1 confirmed=0 total=7")]
    [InlineData("DNF_NET stage=fight phase=clearWait posted=7", "stage=fight phase=clearWait posted=7")]
    public void The_probe_forwards_dungeon_combat_and_fight_lines_without_the_net_prefix(string netLine, string expected)
    {
        Assert.Equal(expected, OnlineFightLog.ProbeText(netLine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DNF_NET stage=dungeon begin dungeon=901 mode=2")]       // 回显命令行里的副本编号与模式
    [InlineData("DNF_NET stage=town step=Login")]
    [InlineData("DNF_NET stage=dungeonX step=1")]                       // 类别名后面必须跟空格,不是任意以 dungeon 开头
    [InlineData("DNF_NET stage=combatant x=1")]
    [InlineData("DNF_NET stage=fighter x=1")]
    [InlineData("DNF_NET stage=")]
    [InlineData("DNF_NET snapshot slot=0 level=1 job=0 growType=0 enterComplete=1 seen=3 parsed=3 malformed=0")]  // 角色数值那一行
    [InlineData("DNF_NET heartbeat answered=3")]
    [InlineData("DNF_NET session events=9 received=9")]
    [InlineData("stage=dungeon step=Done")]                             // 没有 DNF_NET 前缀的不认
    [InlineData(" DNF_NET stage=dungeon step=Done")]
    [InlineData("PROBE_FIGHT stage=dungeon step=Done")]
    public void The_probe_drops_every_other_line_and_the_dungeon_begin_echo(string netLine)
    {
        Assert.Null(OnlineFightLog.ProbeText(netLine));
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

/// <summary><see cref="SystemFightClock"/> 的测试(线上用的那个时钟):单调、非正延时立即完成、取消、真等一小会儿。用的都是几十毫秒的小延时。</summary>
public class SystemFightClockTests
{
    [Fact]
    public void The_shared_instance_is_one_object_and_its_time_never_goes_back()
    {
        Assert.Same(SystemFightClock.Instance, SystemFightClock.Instance);
        TimeSpan before = SystemFightClock.Instance.Now;
        Thread.Sleep(10);
        TimeSpan after = SystemFightClock.Instance.Now;
        Assert.True(after > before);
        Assert.True(after - before >= TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void A_delay_that_is_not_positive_completes_at_once()
    {
        Assert.True(SystemFightClock.Instance.DelayAsync(TimeSpan.Zero, CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(SystemFightClock.Instance.DelayAsync(TimeSpan.FromSeconds(-5), CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void A_token_that_is_already_cancelled_gives_a_cancelled_task_even_for_a_zero_delay()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.True(SystemFightClock.Instance.DelayAsync(TimeSpan.Zero, cts.Token).IsCanceled);
        Assert.True(SystemFightClock.Instance.DelayAsync(TimeSpan.FromSeconds(30), cts.Token).IsCanceled);
    }

    [Fact]
    public async Task A_positive_delay_really_waits_and_a_cancellation_cuts_it_short()
    {
        TimeSpan start = SystemFightClock.Instance.Now;
        await SystemFightClock.Instance.DelayAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None);
        Assert.True(SystemFightClock.Instance.Now - start >= TimeSpan.FromMilliseconds(40), "应当真的等了一会儿");

        using var cts = new CancellationTokenSource();
        Task long30 = SystemFightClock.Instance.DelayAsync(TimeSpan.FromSeconds(30), cts.Token);
        Assert.False(long30.IsCompleted);
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));
        Task finished = await Task.WhenAny(long30, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(long30, finished);
        Assert.True(long30.IsCanceled);
    }
}
