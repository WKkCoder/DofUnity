using System.Text.RegularExpressions;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// r22-giveup-signal:探针 <c>fight</c> 新开关 <c>--frame-log</c>(默认关闭)的命令行解析、用法文字与胶水测试(转行白名单的契约测试在 <see cref="OnlineFrameLogContractTests"/>)。
/// 解析是纯函数,不连网、不需要会话;胶水(<c>src/Dnf.NetProbe/FightCommand.cs</c>)是没有测试工程的可执行程序,这里用读源码的办法守住"开关传给战斗选项、用法文字带上它、退出码表达式不碰它"。
/// 合成值(副本号 901 起)。
/// </summary>
public class OnlineFrameLogCliTests
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

    // ======== 缺省关、打开 ========

    [Fact]
    public void Without_the_switch_the_frame_log_is_off_and_every_other_default_is_untouched()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "2");

        Assert.False(a.FrameLog);
        Assert.False(a.GiveUpOnFailure);
        Assert.False(a.PermitsOnly);
        Assert.False(a.AfterSettle);
        Assert.Null(a.MinClearSeconds);
        Assert.Equal(OnlineFightOptions.DefaultPaceMs, a.PaceMs);
        Assert.Equal(OnlineFightOptions.DefaultClearWait, a.ClearWait);
        Assert.Equal(OnlineFightOptions.DefaultSettleWait, a.SettleWait);
        Assert.False(a.ContinueOnRefuse);
        Assert.Equal(0, a.StaySeconds);
        // 命令行的缺省与战斗选项的缺省是同一份:没给开关就是没开
        Assert.Equal(new OnlineDungeonCombatOptions().FrameLog, a.FrameLog);
    }

    [Fact]
    public void The_switch_turns_only_the_frame_log_on()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "2", "--frame-log");

        Assert.True(a.FrameLog);
        Assert.Equal(901, a.DungeonId);
        Assert.Equal(2, a.Mode);
        Assert.False(a.GiveUpOnFailure);                // 与放弃开关各管各的
        Assert.False(a.PermitsOnly);
        Assert.False(a.AfterSettle);
        Assert.Null(a.MinClearSeconds);
        Assert.Null(a.MenuChoice);
        Assert.Equal(OnlineFightOptions.DefaultPaceMs, a.PaceMs);
        Assert.False(a.ContinueOnRefuse);
        Assert.Equal(0, a.StaySeconds);
    }

    [Fact]
    public void The_give_up_switch_alone_leaves_the_frame_log_off()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "2", "--give-up-on-failure");

        Assert.True(a.GiveUpOnFailure);
        Assert.False(a.FrameLog);
    }

    [Theory]
    [InlineData("--dungeon|901|0|--frame-log")]
    [InlineData("--frame-log|--dungeon|901|0")]
    [InlineData("some-root|--dungeon|901|2|--frame-log")]
    [InlineData("--dungeon|901|0|--frame-log|--continue-on-refuse|--stay|5")]
    [InlineData("--dungeon|901|0|--min-clear-seconds|10|--frame-log")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--frame-log")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|town|--frame-log")]
    [InlineData("--dungeon|901|0|--give-up-on-failure|--frame-log")]
    [InlineData("--frame-log|--give-up-on-failure|--dungeon|901|0|--after-settle|--min-clear-seconds|10")]
    [InlineData("--frame-log|--stay|0|--pace-ms|0|--dungeon|0|0")]
    public void The_switch_combines_with_the_other_options_in_any_position(string joined)
    {
        string[] args = joined.Split('|');
        OnlineFightArguments a = Parse(args);

        Assert.True(a.FrameLog);
        Assert.Equal(args.Contains("--give-up-on-failure"), a.GiveUpOnFailure);
        Assert.Equal(args.Contains("--after-settle"), a.AfterSettle);
        Assert.Equal(args.Contains("--continue-on-refuse"), a.ContinueOnRefuse);
        Assert.Equal(args.Contains("--min-clear-seconds"), a.MinClearSeconds.HasValue);
        Assert.Equal(args.Contains("--menu-choice") ? "town" : null, a.MenuChoice);
        Assert.Equal(args.Contains("some-root") ? "some-root" : null, a.RepoRoot);
        int stayAt = Array.IndexOf(args, "--stay");
        Assert.Equal(stayAt >= 0 ? int.Parse(args[stayAt + 1]) : 0, a.StaySeconds);
        int paceAt = Array.IndexOf(args, "--pace-ms");
        Assert.Equal(paceAt >= 0 ? int.Parse(args[paceAt + 1]) : OnlineFightOptions.DefaultPaceMs, a.PaceMs);
        // 开关是纯开关,不吞后面的参数:副本号与模式照常读到
        Assert.Contains(a.DungeonId, new[] { 0, 901 });
        Assert.False(a.PermitsOnly);
    }

    // ======== 与 --permits-only 互斥 ========

    [Theory]
    [InlineData("--permits-only|--frame-log")]
    [InlineData("--frame-log|--permits-only")]
    [InlineData("some-root|--permits-only|--frame-log")]
    [InlineData("some-root|--frame-log|--permits-only")]
    public void The_switch_and_permits_only_exclude_each_other_in_either_order(string joined)
    {
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem(joined));
    }

    [Fact]
    public void Permits_only_alone_still_parses_and_leaves_the_frame_log_off()
    {
        OnlineFightArguments a = Parse("--permits-only");

        Assert.True(a.PermitsOnly);
        Assert.False(a.FrameLog);
    }

    [Fact]
    public void The_conflict_with_permits_only_is_reported_even_when_a_dungeon_or_the_give_up_switch_is_also_given()
    {
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem("--permits-only|--dungeon|901|0|--frame-log"));
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem("--frame-log|--dungeon|901|0|--permits-only"));
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem("--permits-only|--give-up-on-failure|--frame-log"));
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem("--frame-log|--give-up-on-failure|--permits-only"));
    }

    // ======== 重复给 ========

    [Theory]
    [InlineData("--dungeon|901|0|--frame-log|--frame-log")]
    [InlineData("--frame-log|--frame-log|--dungeon|901|0")]
    [InlineData("--frame-log|--dungeon|901|0|--stay|5|--frame-log")]
    [InlineData("--dungeon|901|0|--give-up-on-failure|--frame-log|--give-up-on-failure|--frame-log")]
    [InlineData("--permits-only|--frame-log|--frame-log")]
    public void A_second_occurrence_is_a_usage_error_wherever_it_sits(string joined)
    {
        Assert.Equal(OnlineFightArgumentProblem.DuplicateOption, Problem(joined));
    }

    // ======== 不遮盖别的问题 ========

    [Theory]
    [InlineData("--frame-log", OnlineFightArgumentProblem.MissingDungeon)]
    [InlineData("some-root|--frame-log", OnlineFightArgumentProblem.MissingDungeon)]
    [InlineData("--dungeon|901|0|--frame-log|--min-clear-seconds|0", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--frame-log|--pace-ms|abc", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--frame-log|--menu-choice|town", OnlineFightArgumentProblem.MenuChoiceWithoutAfterSettle)]
    [InlineData("--dungeon|901|0|--after-settle|--frame-log", OnlineFightArgumentProblem.MissingMinClearSeconds)]
    [InlineData("--dungeon|901|0|--frame-logs", OnlineFightArgumentProblem.UnknownArgument)]
    [InlineData("--dungeon|901|0|--frame-log=1", OnlineFightArgumentProblem.UnknownArgument)]
    [InlineData("--dungeon|901|0|--Frame-Log", OnlineFightArgumentProblem.UnknownArgument)]
    public void The_switch_does_not_hide_any_other_usage_problem(string joined, OnlineFightArgumentProblem expected)
    {
        Assert.Equal(expected, Problem(joined));
    }

    [Fact]
    public void The_abort_line_for_the_new_usage_problems_is_the_existing_sanitized_shape()
    {
        OnlineFightArgumentProblem conflict = Problem("secret-root|--permits-only|--frame-log");
        OnlineFightArgumentProblem duplicate = Problem("secret-root|--dungeon|901|0|--frame-log|--frame-log");

        string line1 = "PROBE_FIGHT abort at=usage reason=" + conflict + " exit=" + OnlineFightExitCodes.Usage;
        string line2 = "PROBE_FIGHT abort at=usage reason=" + duplicate + " exit=" + OnlineFightExitCodes.Usage;
        Assert.Equal("PROBE_FIGHT abort at=usage reason=Conflict exit=64", line1);
        Assert.Equal("PROBE_FIGHT abort at=usage reason=DuplicateOption exit=64", line2);
        Assert.DoesNotContain("secret", line1 + line2);
        Assert.DoesNotContain("frame-log", line1 + line2);
    }

    [Fact]
    public void The_new_switch_adds_no_exit_code_and_no_new_problem_name()
    {
        // 用法错误还是 64;问题名没有新增(开关的两种错误复用既有的 Conflict 与 DuplicateOption)
        Assert.Equal(64, OnlineFightExitCodes.Usage);
        Assert.Equal(2, OnlineFightExitCodes.SettingsFailed);
        string[] expected =
        {
            "None", "MissingDungeon", "BadDungeon", "BadPaceMs", "BadClearWait", "BadSettleWait", "BadStay", "UnknownArgument",
            "Conflict", "BadMinClearSeconds", "BadMenuChoice", "MissingMinClearSeconds", "MenuChoiceWithoutAfterSettle", "DuplicateOption",
        };
        Assert.Equal(expected, Enum.GetNames(typeof(OnlineFightArgumentProblem)));
    }

    // ======== 离线核对:空目录当仓库根,第一步就失败,不可能联网 ========

    [Theory]
    [InlineData("--dungeon|901|0|--frame-log")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--give-up-on-failure|--frame-log|--stay|3")]
    [InlineData("--dungeon|901|0")]
    public void With_an_empty_directory_as_the_repo_root_the_run_ends_at_the_settings_step_before_any_network(string joined)
    {
        string root = Path.Combine(Path.GetTempPath(), "dnf-framelog-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Empty(Directory.GetFileSystemEntries(root));
            OnlineFightArguments a = Parse(new[] { root }.Concat(joined.Split('|')).ToArray());
            Assert.Equal(root, a.RepoRoot);
            Assert.Equal(joined.Contains("--frame-log"), a.FrameLog);

            // 与探针 fight 命令的第一步是同一个调用:空目录里没有 local.env,读不到任何联网入参,开关开不开都一样 → 退出码 2
            Assert.False(LocalDevEntrySettings.TryLoad(a.RepoRoot, out OnlineEntrySettings settings, out string problem));
            Assert.Null(settings);
            Assert.False(string.IsNullOrEmpty(problem));
            Assert.Equal(2, OnlineFightExitCodes.SettingsFailed);
            Assert.Empty(Directory.GetFileSystemEntries(root));         // 什么也没写进去
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ======== 胶水(读源码):用法文字、开关传给战斗选项、退出码表达式不碰它 ========

    private static string FightCommandSource()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DnfUnity.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.True(dir != null, "找不到仓库根(应含 DnfUnity.slnx)");
        string path = Path.Combine(dir, "src", "Dnf.NetProbe", "FightCommand.cs");
        Assert.True(File.Exists(path), "找不到 " + path);
        return File.ReadAllText(path);
    }

    private static string RunCore(string source)
    {
        string normalized = Regex.Replace(source, @"\s+", " ");
        int start = normalized.IndexOf("private static async Task<int> RunCoreAsync(", StringComparison.Ordinal);
        int end = normalized.IndexOf("private static async Task StayAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 RunCoreAsync 的范围");
        return normalized.Substring(start, end - start);
    }

    [Fact]
    public void The_usage_text_ends_with_the_new_switch_and_names_it_exactly_once()
    {
        string source = FightCommandSource();
        Match m = Regex.Match(source, "private const string UsageText = \"(?<text>[^\"]*)\";");
        Assert.True(m.Success, "找不到 UsageText");
        string usage = m.Groups["text"].Value;

        Assert.EndsWith(" [--give-up-on-failure] [--frame-log]", usage, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(usage, Regex.Escape("--frame-log")));
        // 只多了这一个开关:去掉它之后就是上一版的用法文字
        Assert.EndsWith("[--after-settle [--menu-choice town]] [--give-up-on-failure]", usage.Substring(0, usage.Length - " [--frame-log]".Length), StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_reaches_the_combat_options_through_the_one_initializer_in_the_glue()
    {
        string source = FightCommandSource();
        string core = RunCore(source);

        const string open = "var combatOptions = new OnlineDungeonCombatOptions {";
        const string close = "}; OnlineDungeonCombatRunner runner = dungeonRunner.UseCombat(combatOptions);";
        int start = core.IndexOf(open, StringComparison.Ordinal);
        int end = core.IndexOf(close, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到战斗选项的初始化块");
        string initializer = core.Substring(start, end - start);

        Assert.Contains("FrameLog = parsed.FrameLog,", initializer, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, Regex.Escape("parsed.FrameLog")));       // 整个胶水里只有这一处读开关
    }

    [Fact]
    public void The_exit_code_expression_never_mentions_the_frame_log()
    {
        string core = RunCore(FightCommandSource());
        int result = core.IndexOf("Console.WriteLine(\"PROBE_FIGHT result \" + resultFields);", StringComparison.Ordinal);
        Assert.True(result >= 0, "找不到结果行");
        string tail = core.Substring(result);

        Assert.Contains("return OnlineFightExitCodes.ForOutcome(summary.Outcome);", tail);
        Assert.DoesNotContain("FrameLog", tail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("frame-log", tail, StringComparison.OrdinalIgnoreCase);
    }
}
