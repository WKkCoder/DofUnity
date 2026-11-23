using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch [仓库根] --list</c>(只读目录摘要模式)的命令行:解析结果、与四个换频道选项的互斥、重复/未知参数的归类、
/// 各问题的先后次序,以及走到 <see cref="OnlineSwitchProbe.RunAsync"/> 时"用法错误一律退出码 64、只打一行、绝不碰端口"
/// (没读配置、没拿会话、更不会联网;用"一碰就失败"的假端口证明)。
/// 命令行都是合成的,没有任何真实值;没有睡眠。
/// </summary>
public sealed class OnlineProbeKnobsGapListArgumentsTests
{
    // 用法行(handwritten):不含 --list 记号时只有换频道的写法;含 --list 记号时后面接目录摘要的写法
    private const string PlainSyntax =
        "dnf-netprobe switch [仓库根] --to-index <K> | --to-port <N> [--client-version <u32>] [--server-group <0..17>]";

    private const string BothSyntaxes = PlainSyntax + " | dnf-netprobe switch [仓库根] --list";

    private static string[] Split(string line)
    {
        return line.Length == 0 ? Array.Empty<string>() : line.Split(' ');
    }

    private static (bool Ok, OnlineSwitchArguments Parsed, OnlineProbeArgumentProblem Problem) Parse(string line)
    {
        bool ok = OnlineSwitchArguments.TryParse(Split(line), out OnlineSwitchArguments parsed, out OnlineProbeArgumentProblem problem);
        return (ok, parsed, problem);
    }

    private static async Task<(int Exit, List<string> Lines)> RunWithUntouchablePort(FailIfCalledPort port, string line)
    {
        var lines = new List<string>();
        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(Split(line), DefaultRoot, port, lines.Add), 10);
        return (exit, lines);
    }

    // ======== 一、解析结果 ========

    // 保护:单独一个 --list 就是目录摘要模式,不带任何换频道目标或开关(没有目标也不报 MissingTarget)
    [Fact]
    public void List_alone_is_a_list_run_that_sets_nothing_else()
    {
        (bool ok, OnlineSwitchArguments parsed, OnlineProbeArgumentProblem problem) = Parse("--list");

        Assert.True(ok);
        Assert.Equal(OnlineProbeArgumentProblem.None, problem);
        Assert.True(parsed.ListMode);
        Assert.Null(parsed.RepoRoot);
        Assert.Equal(-1, parsed.ToIndex);
        Assert.Equal(0, parsed.ToPort);
        Assert.False(parsed.TargetIsIndex);
        Assert.Null(parsed.ClientVersion);
        Assert.Null(parsed.ServerGroup);
    }

    // 保护:仓库根仍然只能放第一个位置,放在 --list 前面时被原样保留
    [Fact]
    public void A_repo_root_in_front_of_list_is_kept()
    {
        (bool ok, OnlineSwitchArguments parsed, OnlineProbeArgumentProblem problem) = Parse("SYNTH-LIST-ROOT --list");

        Assert.True(ok);
        Assert.Equal(OnlineProbeArgumentProblem.None, problem);
        Assert.True(parsed.ListMode);
        Assert.Equal("SYNTH-LIST-ROOT", parsed.RepoRoot);
    }

    // 保护:不给 --list 就不是目录摘要模式(原有的换频道写法解析结果不变,ListMode 为假)
    [Theory]
    [InlineData("--to-index 0")]
    [InlineData("--to-port 5")]
    [InlineData("SYNTH-LIST-ROOT --to-index 3 --client-version 1 --server-group 2")]
    public void Without_the_list_option_the_run_is_not_a_list_run(string line)
    {
        (bool ok, OnlineSwitchArguments parsed, _) = Parse(line);

        Assert.True(ok);
        Assert.False(parsed.ListMode);
    }

    // 保护:选项名就是 --list(区分大小写,没有简写)
    [Fact]
    public void The_option_names_are_exactly_these()
    {
        Assert.Equal("--list", OnlineSwitchArguments.ListOption);
        Assert.Equal("dnf-netprobe switch [仓库根] --list", OnlineSwitchProbe.ListSyntax);
        Assert.Equal(PlainSyntax, OnlineSwitchProbe.Syntax);
    }

    // ======== 二、互斥:--list 与四个换频道选项,先后次序无关 ========

    // 保护:--list 与 --to-index / --to-port / --client-version / --server-group 任何一个同时出现都是 ConflictingList,
    // 不论谁在前;各选项自己的取值都合规(取值有问题的情况见下面"先后次序"的用例)
    [Theory]
    [InlineData("--list --to-index 1")]
    [InlineData("--to-index 1 --list")]
    [InlineData("--list --to-port 47399")]
    [InlineData("--to-port 47399 --list")]
    [InlineData("--list --client-version 1")]
    [InlineData("--client-version 1 --list")]
    [InlineData("--list --server-group 2")]
    [InlineData("--server-group 2 --list")]
    [InlineData("--list --server-group 0")]
    [InlineData("SYNTH-LIST-ROOT --list --to-index 0")]
    [InlineData("SYNTH-LIST-ROOT --to-port 5 --list")]
    [InlineData("--list --to-index 1 --to-port 2")]
    [InlineData("--to-index 1 --client-version 7 --list --server-group 3")]
    public void List_conflicts_with_every_other_option_whatever_the_order(string line)
    {
        (bool ok, OnlineSwitchArguments parsed, OnlineProbeArgumentProblem problem) = Parse(line);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(OnlineProbeArgumentProblem.ConflictingList, problem);
    }

    // 保护:每个冲突选项单独存在也足以触发冲突(四个选项各一行,逐个断言,防止判定里漏掉某一个)
    [Theory]
    [InlineData("--to-index", "0")]
    [InlineData("--to-port", "1")]
    [InlineData("--client-version", "0")]
    [InlineData("--server-group", "0")]
    public void Each_of_the_four_options_alone_is_enough_to_conflict_with_list(string option, string value)
    {
        (bool ok, _, OnlineProbeArgumentProblem problem) = Parse("--list " + option + " " + value);

        Assert.False(ok);
        Assert.Equal(OnlineProbeArgumentProblem.ConflictingList, problem);
    }

    // ======== 三、重复、未知、取值问题的归类与先后次序 ========

    // 保护:--list 出现两次是 DuplicateOption(哪怕中间夹着别的选项),不是别的问题
    [Theory]
    [InlineData("--list --list")]
    [InlineData("SYNTH-LIST-ROOT --list --list")]
    [InlineData("--list --to-index 1 --list")]
    public void List_given_twice_is_a_duplicate_option(string line)
    {
        (bool ok, OnlineSwitchArguments parsed, OnlineProbeArgumentProblem problem) = Parse(line);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(OnlineProbeArgumentProblem.DuplicateOption, problem);
    }

    // 保护:--list 旁边出现不认识的记号、第二个位置参数、仓库根放错位置、大小写不对、带等号等,一律是 UnknownArgument
    [Theory]
    [InlineData("--list SYNTH-EXTRA")]
    [InlineData("SYNTH-LIST-ROOT --list SYNTH-EXTRA")]
    [InlineData("--list SYNTH-LIST-ROOT")]
    [InlineData("--list --bogus")]
    [InlineData("--LIST")]
    [InlineData("--List")]
    [InlineData("--list=1")]
    [InlineData("--lists")]
    [InlineData("--lis")]
    public void Unknown_or_misplaced_tokens_next_to_list_are_unknown_arguments(string line)
    {
        (bool ok, OnlineSwitchArguments parsed, OnlineProbeArgumentProblem problem) = Parse(line);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(OnlineProbeArgumentProblem.UnknownArgument, problem);
    }

    // 保护:不带两个短横的 list / -list 在第一个位置是"仓库根",不是模式字;没有 --list 就仍然缺目标
    [Theory]
    [InlineData("list")]
    [InlineData("-list")]
    public void A_bare_list_word_is_only_a_repo_root(string line)
    {
        (bool ok, _, OnlineProbeArgumentProblem problem) = Parse(line);

        Assert.False(ok);
        Assert.Equal(OnlineProbeArgumentProblem.MissingTarget, problem);
    }

    // 保护:各选项自己的取值/重复问题先于"与 --list 互斥"报告(按记号先后遇到先报);--list 本身不改变这些选项的取值规则
    [Theory]
    [InlineData("--list --to-index abc", OnlineProbeArgumentProblem.BadToIndex)]
    [InlineData("--list --to-index", OnlineProbeArgumentProblem.BadToIndex)]
    [InlineData("--to-port 0 --list", OnlineProbeArgumentProblem.BadToPort)]
    [InlineData("--list --to-port 65536", OnlineProbeArgumentProblem.BadToPort)]
    [InlineData("--list --client-version x", OnlineProbeArgumentProblem.BadClientVersion)]
    [InlineData("--list --client-version 4294967296", OnlineProbeArgumentProblem.BadClientVersion)]
    [InlineData("--server-group 18 --list", OnlineProbeArgumentProblem.BadServerGroup)]
    [InlineData("--list --server-group", OnlineProbeArgumentProblem.BadServerGroup)]
    [InlineData("--list --to-index 1 --to-index 2", OnlineProbeArgumentProblem.DuplicateOption)]
    [InlineData("--list --server-group 1 --server-group 1", OnlineProbeArgumentProblem.DuplicateOption)]
    public void Option_value_and_duplicate_problems_are_reported_before_the_list_conflict(string line, OnlineProbeArgumentProblem expected)
    {
        (bool ok, OnlineSwitchArguments parsed, OnlineProbeArgumentProblem problem) = Parse(line);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(expected, problem);
    }

    // 保护:--list 紧跟在要取值的选项后面时被当成那个选项的值(取值不合规),不会被当成目录摘要开关
    [Theory]
    [InlineData("--to-index --list", OnlineProbeArgumentProblem.BadToIndex)]
    [InlineData("--client-version --list", OnlineProbeArgumentProblem.BadClientVersion)]
    [InlineData("--server-group --list", OnlineProbeArgumentProblem.BadServerGroup)]
    public void A_list_token_in_a_value_position_is_a_bad_value_not_a_mode(string line, OnlineProbeArgumentProblem expected)
    {
        (bool ok, _, OnlineProbeArgumentProblem problem) = Parse(line);

        Assert.False(ok);
        Assert.Equal(expected, problem);
    }

    // 保护:空记号(null)是 UnknownArgument,与是否有 --list 无关;整个参数表为 null 与空表一样(没有 --list,缺目标)
    [Fact]
    public void A_null_token_is_unknown_and_a_null_list_is_empty()
    {
        bool okNullToken = OnlineSwitchArguments.TryParse(new string[] { "--list", null }, out _, out OnlineProbeArgumentProblem nullTokenProblem);
        bool okNullList = OnlineSwitchArguments.TryParse(null, out OnlineSwitchArguments parsed, out OnlineProbeArgumentProblem nullListProblem);

        Assert.False(okNullToken);
        Assert.Equal(OnlineProbeArgumentProblem.UnknownArgument, nullTokenProblem);
        Assert.False(okNullList);
        Assert.Null(parsed);
        Assert.Equal(OnlineProbeArgumentProblem.MissingTarget, nullListProblem);
    }

    // ======== 四、MentionsList ========

    // 保护:是否"提到了 --list"只看记号本身(区分大小写、整词相等),不管它在哪个位置、取值是否合规
    [Theory]
    [InlineData("--list", true)]
    [InlineData("SYNTH-LIST-ROOT --list", true)]
    [InlineData("--to-index 1 --list", true)]
    [InlineData("--to-index --list", true)]
    [InlineData("--list --list", true)]
    [InlineData("--to-index 1", false)]
    [InlineData("", false)]
    [InlineData("--listx", false)]
    [InlineData("--LIST", false)]
    [InlineData("list", false)]
    [InlineData("--lis", false)]
    public void Mentions_list_only_when_a_token_is_exactly_the_option(string line, bool expected)
    {
        Assert.Equal(expected, OnlineSwitchArguments.MentionsList(Split(line)));
    }

    // 保护:null 参数表与含 null 记号的参数表都不抛异常,按"没提到"处理(含 null 记号但后面有 --list 仍然提到)
    [Fact]
    public void Mentions_list_copes_with_null_inputs()
    {
        Assert.False(OnlineSwitchArguments.MentionsList(null));
        Assert.False(OnlineSwitchArguments.MentionsList(new string[] { null }));
        Assert.True(OnlineSwitchArguments.MentionsList(new string[] { null, "--list" }));
    }

    // ======== 五、走到 RunAsync:用法错误 ========

    // 保护:--list 用法错误时退出码 64、只打一行 "PROBE_SWITCH usage problem=<枚举名> exit=64 syntax: …",用法行里同时列出两种写法;
    // 端口一次都没被碰(没读配置、没拿会话);用户敲的内容不回显
    [Theory]
    [InlineData("--list --to-index 1", "ConflictingList")]
    [InlineData("--to-index 1 --list", "ConflictingList")]
    [InlineData("--list --to-port 47399", "ConflictingList")]
    [InlineData("--list --client-version 1", "ConflictingList")]
    [InlineData("--list --server-group 2", "ConflictingList")]
    [InlineData("SYNTH-LIST-ROOT --list --server-group 2", "ConflictingList")]
    [InlineData("--list --list", "DuplicateOption")]
    [InlineData("SYNTH-LIST-ROOT --list --list", "DuplicateOption")]
    [InlineData("--list SYNTH-SECRET-TYPED", "UnknownArgument")]
    [InlineData("--list --bogus-SYNTH-SECRET-TYPED", "UnknownArgument")]
    [InlineData("--list --server-group 99", "BadServerGroup")]
    [InlineData("--list --to-index SYNTH-SECRET-TYPED", "BadToIndex")]
    [InlineData("--to-index --list", "BadToIndex")]
    public async Task An_invalid_list_command_line_exits_64_with_one_usage_line_and_never_touches_the_port(string line, string problemName)
    {
        var port = new FailIfCalledPort();

        (int exit, List<string> lines) = await RunWithUntouchablePort(port, line);

        Assert.Equal(64, exit);
        Assert.Equal(0, port.Calls);
        Assert.Equal("PROBE_SWITCH usage problem=" + problemName + " exit=64 syntax: " + BothSyntaxes, Assert.Single(lines));
        Assert.DoesNotContain("SYNTH-SECRET-TYPED", string.Join("\n", lines));
        Assert.DoesNotContain("SYNTH-LIST-ROOT", string.Join("\n", lines));
    }

    // 保护:命令行里没有 --list 这个记号时,用法行与以前逐字节一致(只有换频道一种写法);
    // 大小写不对的 --LIST 不算提到 --list
    [Theory]
    [InlineData("", "MissingTarget")]
    [InlineData("--to-index 0 --bogus", "UnknownArgument")]
    [InlineData("--to-index 1 --to-port 2", "ConflictingTargets")]
    [InlineData("--to-port 0", "BadToPort")]
    [InlineData("--LIST", "UnknownArgument")]
    [InlineData("--lists", "UnknownArgument")]
    public async Task A_usage_error_without_the_list_token_keeps_the_single_syntax_line(string line, string problemName)
    {
        var port = new FailIfCalledPort();

        (int exit, List<string> lines) = await RunWithUntouchablePort(port, line);

        Assert.Equal(64, exit);
        Assert.Equal(0, port.Calls);
        Assert.Equal("PROBE_SWITCH usage problem=" + problemName + " exit=64 syntax: " + PlainSyntax, Assert.Single(lines));
    }

    // 保护:合规的 --list 命令行不是用法错误,会走到端口(读设置那一步);只给仓库根时读设置收到的就是它,没给就是默认仓库根
    [Theory]
    [InlineData("--list", DefaultRoot)]
    [InlineData("SYNTH-LIST-ROOT --list", "SYNTH-LIST-ROOT")]
    public async Task A_valid_list_command_line_reaches_the_port_with_the_right_repo_root(string line, string expectedRoot)
    {
        var port = new FakePort();
        var lines = new List<string>();

        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(Split(line), DefaultRoot, port, lines.Add), 10);

        Assert.Equal(0, exit);
        Assert.Equal(1, port.LoadCalls);
        Assert.Equal(expectedRoot, port.RepoRootSeen);
        Assert.DoesNotContain(lines, l => l.Contains(" usage "));
    }
}
