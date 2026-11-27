using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>fight</c> 新开关 <c>--give-up-on-failure</c>(默认关闭)的命令行解析测试:缺省关、打开、与其余选项同给、与 <c>--permits-only</c> 互斥、重复给是用法错误。
/// 纯函数,不连网、不需要会话。合成值(副本号 901 起)。
/// </summary>
public class OnlineGiveUpCliTests
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
    public void Without_the_switch_the_give_up_option_is_off_and_every_other_default_is_untouched()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "2");

        Assert.False(a.GiveUpOnFailure);
        Assert.False(a.PermitsOnly);
        Assert.False(a.AfterSettle);
        Assert.Null(a.MinClearSeconds);
        Assert.Equal(OnlineFightOptions.DefaultPaceMs, a.PaceMs);
        Assert.Equal(OnlineFightOptions.DefaultClearWait, a.ClearWait);
        Assert.Equal(OnlineFightOptions.DefaultSettleWait, a.SettleWait);
        Assert.False(a.ContinueOnRefuse);
        Assert.Equal(0, a.StaySeconds);
        // 命令行的缺省与驱动选项的缺省是同一份:没给开关就是没开
        Assert.Equal(new OnlineFightOptions().GiveUpOnFailure, a.GiveUpOnFailure);
    }

    [Fact]
    public void The_switch_turns_only_the_give_up_option_on()
    {
        OnlineFightArguments a = Parse("--dungeon", "901", "2", "--give-up-on-failure");

        Assert.True(a.GiveUpOnFailure);
        Assert.Equal(901, a.DungeonId);
        Assert.Equal(2, a.Mode);
        Assert.False(a.PermitsOnly);
        Assert.False(a.AfterSettle);
        Assert.Null(a.MinClearSeconds);
        Assert.Null(a.MenuChoice);
        Assert.Equal(OnlineFightOptions.DefaultPaceMs, a.PaceMs);
        Assert.False(a.ContinueOnRefuse);
        Assert.Equal(0, a.StaySeconds);
    }

    [Theory]
    [InlineData("--dungeon|901|0|--give-up-on-failure")]
    [InlineData("--give-up-on-failure|--dungeon|901|0")]
    [InlineData("some-root|--dungeon|901|2|--give-up-on-failure")]
    [InlineData("--dungeon|901|0|--give-up-on-failure|--continue-on-refuse|--stay|5")]
    [InlineData("--dungeon|901|0|--min-clear-seconds|10|--give-up-on-failure")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--give-up-on-failure")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--menu-choice|town|--give-up-on-failure")]
    [InlineData("--give-up-on-failure|--stay|0|--pace-ms|0|--dungeon|0|0")]
    public void The_switch_combines_with_the_other_options_in_any_position(string joined)
    {
        string[] args = joined.Split('|');
        OnlineFightArguments a = Parse(args);

        Assert.True(a.GiveUpOnFailure);
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
    [InlineData("--permits-only|--give-up-on-failure")]
    [InlineData("--give-up-on-failure|--permits-only")]
    [InlineData("some-root|--permits-only|--give-up-on-failure")]
    public void The_switch_and_permits_only_exclude_each_other_in_either_order(string joined)
    {
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem(joined));
    }

    [Fact]
    public void Permits_only_alone_still_parses_and_leaves_the_switch_off()
    {
        OnlineFightArguments a = Parse("--permits-only");

        Assert.True(a.PermitsOnly);
        Assert.False(a.GiveUpOnFailure);
    }

    [Fact]
    public void The_conflict_with_permits_only_is_reported_even_when_a_dungeon_is_also_given()
    {
        // 给了副本编号又给 --permits-only 本来就是冲突;加上开关结论不变(没有第二种问题名)
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem("--permits-only|--dungeon|901|0|--give-up-on-failure"));
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem("--give-up-on-failure|--dungeon|901|0|--permits-only"));
    }

    // ======== 重复给 ========

    [Theory]
    [InlineData("--dungeon|901|0|--give-up-on-failure|--give-up-on-failure")]
    [InlineData("--give-up-on-failure|--give-up-on-failure|--dungeon|901|0")]
    [InlineData("--give-up-on-failure|--dungeon|901|0|--stay|5|--give-up-on-failure")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--give-up-on-failure|--menu-choice|town|--give-up-on-failure")]
    [InlineData("--permits-only|--give-up-on-failure|--give-up-on-failure")]
    public void A_second_occurrence_is_a_usage_error_wherever_it_sits(string joined)
    {
        Assert.Equal(OnlineFightArgumentProblem.DuplicateOption, Problem(joined));
    }

    // ======== 不遮盖别的问题 ========

    [Theory]
    [InlineData("--give-up-on-failure", OnlineFightArgumentProblem.MissingDungeon)]
    [InlineData("some-root|--give-up-on-failure", OnlineFightArgumentProblem.MissingDungeon)]
    [InlineData("--dungeon|901|0|--give-up-on-failure|--min-clear-seconds|0", OnlineFightArgumentProblem.BadMinClearSeconds)]
    [InlineData("--dungeon|901|0|--give-up-on-failure|--pace-ms|abc", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--dungeon|901|0|--give-up-on-failure|--menu-choice|town", OnlineFightArgumentProblem.MenuChoiceWithoutAfterSettle)]
    [InlineData("--dungeon|901|0|--after-settle|--give-up-on-failure", OnlineFightArgumentProblem.MissingMinClearSeconds)]
    public void The_switch_does_not_hide_any_other_usage_problem(string joined, OnlineFightArgumentProblem expected)
    {
        Assert.Equal(expected, Problem(joined));
    }

    [Fact]
    public void The_abort_line_for_the_new_usage_problems_is_the_existing_sanitized_shape()
    {
        OnlineFightArgumentProblem conflict = Problem("secret-root|--permits-only|--give-up-on-failure");
        OnlineFightArgumentProblem duplicate = Problem("secret-root|--dungeon|901|0|--give-up-on-failure|--give-up-on-failure");

        string line1 = "PROBE_FIGHT abort at=usage reason=" + conflict + " exit=" + OnlineFightExitCodes.Usage;
        string line2 = "PROBE_FIGHT abort at=usage reason=" + duplicate + " exit=" + OnlineFightExitCodes.Usage;
        Assert.Equal("PROBE_FIGHT abort at=usage reason=Conflict exit=64", line1);
        Assert.Equal("PROBE_FIGHT abort at=usage reason=DuplicateOption exit=64", line2);
        Assert.DoesNotContain("secret", line1 + line2);
        Assert.DoesNotContain("give-up", line1 + line2);
    }

    // ======== 离线核对:空目录当仓库根,第一步就失败,不可能联网 ========

    [Theory]
    [InlineData("--dungeon|901|0|--give-up-on-failure")]
    [InlineData("--dungeon|901|0|--after-settle|--min-clear-seconds|10|--give-up-on-failure|--stay|3")]
    [InlineData("--dungeon|901|0")]
    public void With_an_empty_directory_as_the_repo_root_the_run_ends_at_the_settings_step_before_any_network(string joined)
    {
        string root = Path.Combine(Path.GetTempPath(), "dnf-giveup-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Empty(Directory.GetFileSystemEntries(root));
            OnlineFightArguments a = Parse(new[] { root }.Concat(joined.Split('|')).ToArray());
            Assert.Equal(root, a.RepoRoot);
            Assert.Equal(joined.Contains("--give-up-on-failure"), a.GiveUpOnFailure);

            // 与探针 fight 命令的第一步是同一个调用:空目录里没有 local.env,读不到任何联网入参,开关开不开都一样
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

    [Fact]
    public void The_new_switch_adds_no_exit_code_and_no_new_problem_name()
    {
        // 用法错误还是 64;问题名没有新增(开关的两种错误复用既有的 Conflict 与 DuplicateOption)
        Assert.Equal(64, OnlineFightExitCodes.Usage);
        string[] expected =
        {
            "None", "MissingDungeon", "BadDungeon", "BadPaceMs", "BadClearWait", "BadSettleWait", "BadStay", "UnknownArgument",
            "Conflict", "BadMinClearSeconds", "BadMenuChoice", "MissingMinClearSeconds", "MenuChoiceWithoutAfterSettle", "DuplicateOption",
        };
        Assert.Equal(expected, Enum.GetNames(typeof(OnlineFightArgumentProblem)));
        Assert.DoesNotContain(Enum.GetNames(typeof(OnlineFightArgumentProblem)), n => n.Contains("GiveUp", StringComparison.OrdinalIgnoreCase));
    }
}
