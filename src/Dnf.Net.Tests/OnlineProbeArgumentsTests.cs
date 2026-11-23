using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针两个开关(<c>--client-version</c>、<c>--server-group</c>)与 <c>switch</c> 子命令的命令行解析:纯函数,不读环境、不连网。
/// 边界、缺参、非数字、重复、二选一、"非法时不碰端口"。期望值全是字面量,旁边一行注明由来。
/// </summary>
public sealed class OnlineProbeArgumentsTests
{
    private static string[] Args(string line)
    {
        return line.Length == 0 ? Array.Empty<string>() : line.Split(' ');
    }

    // ======== 一、数值边界(纯函数) ========

    [Theory]
    [InlineData("0", true, 0u)]                       // 下界
    [InlineData("1", true, 1u)]
    [InlineData("42435", true, 42435u)]               // 0xA5C3
    [InlineData("4294967295", true, 4294967295u)]     // u32 上限
    [InlineData("4294967296", false, 0u)]             // 上限加 1
    [InlineData("9999999999", false, 0u)]             // 10 位但远超上限
    [InlineData("0000000005", true, 5u)]              // 10 位以内允许前导零(与十进制数值相同)
    [InlineData("00000000005", false, 0u)]            // 11 位:超过文本长度上限
    [InlineData("-1", false, 0u)]                     // 不认负号
    [InlineData("+1", false, 0u)]                     // 不认正号
    [InlineData(" 1", false, 0u)]                     // 不认空白
    [InlineData("1 ", false, 0u)]
    [InlineData("0x10", false, 0u)]                   // 不认十六进制
    [InlineData("1e3", false, 0u)]
    [InlineData("1.0", false, 0u)]
    [InlineData("１２", false, 0u)]                    // 全角数字不是十进制数字
    [InlineData("abc", false, 0u)]
    [InlineData("", false, 0u)]
    [InlineData(null, false, 0u)]
    public void Client_version_accepts_only_a_plain_decimal_u32(string text, bool ok, uint expected)
    {
        Assert.Equal(ok, OnlineProbeKnobParser.TryParseClientVersion(text, out uint value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0", true, 0)]                        // 下界(0 = 未指定,由会话按目录解)
    [InlineData("1", true, 1)]
    [InlineData("17", true, 17)]                      // 上界 = LoginRequest.MaxServerGroup
    [InlineData("18", false, 0)]                      // 上界加 1
    [InlineData("99999999999", false, 0)]             // 11 位:超过文本长度上限
    [InlineData("-1", false, 0)]
    [InlineData("+3", false, 0)]
    [InlineData("1.5", false, 0)]
    [InlineData("x", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void Server_group_accepts_only_zero_to_seventeen(string text, bool ok, int expected)
    {
        Assert.Equal(ok, OnlineProbeKnobParser.TryParseServerGroup(text, out ushort value));
        Assert.Equal((ushort)expected, value);
    }

    [Theory]
    [InlineData("0", true, 0)]                        // 目录序号从 0 起
    [InlineData("3", true, 3)]
    [InlineData("999999999", true, 999999999)]        // 9 位
    [InlineData("1000000000", false, 0)]              // 10 位:超过文本长度上限
    [InlineData("-1", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void To_index_accepts_only_a_non_negative_decimal(string text, bool ok, int expected)
    {
        Assert.Equal(ok, OnlineProbeKnobParser.TryParseToIndex(text, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0", false, 0)]                       // 端口从 1 起
    [InlineData("1", true, 1)]
    [InlineData("65535", true, 65535)]                // 端口上限
    [InlineData("65536", false, 0)]                   // 上限加 1
    [InlineData("-5", false, 0)]
    [InlineData("port", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void To_port_accepts_only_one_to_sixty_five_thousand_five_hundred_thirty_five(string text, bool ok, int expected)
    {
        Assert.Equal(ok, OnlineProbeKnobParser.TryParseToPort(text, out int value));
        Assert.Equal(expected, value);
    }

    // ======== 二、orchestrated 的两个开关 ========

    [Theory]
    [InlineData("")]
    [InlineData("synth-root")]
    [InlineData("synth-root --stay 5")]
    [InlineData("--stay 5 --try-dungeon 1 2 3 4")]
    public void Orchestrated_without_the_knobs_leaves_both_unset_and_ignores_the_legacy_options(string line)
    {
        Assert.True(OnlineOrchestratedKnobs.TryParse(Args(line), out OnlineOrchestratedKnobs knobs, out OnlineProbeArgumentProblem problem));

        Assert.Equal(OnlineProbeArgumentProblem.None, problem);
        Assert.Null(knobs.ClientVersion);
        Assert.Null(knobs.ServerGroup);
    }

    [Fact]
    public void Orchestrated_reads_both_knobs_in_either_order_and_keeps_an_explicit_zero()
    {
        Assert.True(OnlineOrchestratedKnobs.TryParse(Args("--client-version 42435 --server-group 5"), out OnlineOrchestratedKnobs a, out _));
        Assert.Equal(42435u, a.ClientVersion);          // 0xA5C3
        Assert.Equal((ushort)5, a.ServerGroup);

        Assert.True(OnlineOrchestratedKnobs.TryParse(Args("synth-root --server-group 17 --stay 3 --client-version 4294967295"), out OnlineOrchestratedKnobs b, out _));
        Assert.Equal(uint.MaxValue, b.ClientVersion);   // 4294967295
        Assert.Equal((ushort)17, b.ServerGroup);

        Assert.True(OnlineOrchestratedKnobs.TryParse(Args("--client-version 0 --server-group 0"), out OnlineOrchestratedKnobs zero, out _));
        Assert.Equal(0u, zero.ClientVersion);            // 显式 0 与"没给"不同:有值
        Assert.Equal((ushort)0, zero.ServerGroup);
        Assert.NotNull(zero.ClientVersion);
        Assert.NotNull(zero.ServerGroup);
    }

    [Theory]
    [InlineData("--client-version", OnlineProbeArgumentProblem.BadClientVersion)]                     // 缺值
    [InlineData("--client-version abc", OnlineProbeArgumentProblem.BadClientVersion)]                 // 非数字
    [InlineData("--client-version 4294967296", OnlineProbeArgumentProblem.BadClientVersion)]          // u32 上限加 1
    [InlineData("--client-version -1", OnlineProbeArgumentProblem.BadClientVersion)]
    [InlineData("--client-version --server-group 3", OnlineProbeArgumentProblem.BadClientVersion)]    // 值被写成了下一个选项
    [InlineData("--server-group", OnlineProbeArgumentProblem.BadServerGroup)]
    [InlineData("--server-group 18", OnlineProbeArgumentProblem.BadServerGroup)]                      // 上限加 1
    [InlineData("--server-group x", OnlineProbeArgumentProblem.BadServerGroup)]
    [InlineData("--client-version 1 --server-group 99", OnlineProbeArgumentProblem.BadServerGroup)]   // 前一个合规不能掩盖后一个
    [InlineData("--client-version 1 --client-version 1", OnlineProbeArgumentProblem.DuplicateOption)] // 值相同也算重复
    [InlineData("--client-version 1 --client-version 2", OnlineProbeArgumentProblem.DuplicateOption)]
    [InlineData("--server-group 1 --server-group 2", OnlineProbeArgumentProblem.DuplicateOption)]
    [InlineData("--server-group 1 --client-version 7 --server-group 1", OnlineProbeArgumentProblem.DuplicateOption)]
    public void Orchestrated_rejects_bad_values_and_duplicates(string line, OnlineProbeArgumentProblem expected)
    {
        Assert.False(OnlineOrchestratedKnobs.TryParse(Args(line), out OnlineOrchestratedKnobs knobs, out OnlineProbeArgumentProblem problem));

        Assert.Equal(expected, problem);
        Assert.Null(knobs);
    }

    [Fact]
    public void Applying_the_knobs_writes_only_the_given_ones()
    {
        var onlyVersion = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9);
        Assert.True(OnlineOrchestratedKnobs.TryParse(Args("--client-version 0"), out OnlineOrchestratedKnobs knobs, out _));
        knobs.ApplyTo(onlyVersion);
        Assert.Equal(0u, onlyVersion.ClientVersion);                 // 显式 0 覆盖了预置的 7
        Assert.Equal((ushort)9, onlyVersion.ServerGroup);            // 没给的不动

        var onlyGroup = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9);
        Assert.True(OnlineOrchestratedKnobs.TryParse(Args("--server-group 0"), out knobs, out _));
        knobs.ApplyTo(onlyGroup);
        Assert.Equal(7u, onlyGroup.ClientVersion);
        Assert.Equal((ushort)0, onlyGroup.ServerGroup);

        var neither = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9);
        Assert.True(OnlineOrchestratedKnobs.TryParse(Args(""), out knobs, out _));
        knobs.ApplyTo(neither);
        Assert.Equal(7u, neither.ClientVersion);
        Assert.Equal((ushort)9, neither.ServerGroup);

        Assert.Throws<ArgumentNullException>(() => knobs.ApplyTo(null));
    }

    // ---- Prepare:orchestrated 进城之前的装配段(读配置的函数是注入的,从不读本机配置) ----

    private sealed class Loader
    {
        internal int Calls;
        internal string RootSeen;
        internal bool Succeeds = true;
        internal OnlineEntrySettings Settings = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9);

        internal bool Load(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            Calls++;
            RootSeen = repoRoot;
            settings = Succeeds ? Settings : null;
            problem = Succeeds ? null : "SYNTH-PROBLEM-TEXT";
            return Succeeds;
        }
    }

    [Fact]
    public void Prepare_applies_the_knobs_after_the_loader_so_the_command_line_wins()
    {
        var loader = new Loader();
        var lines = new List<string>();

        int code = OnlineOrchestratedSetup.Prepare(Args("--client-version 0 --server-group 17"), DefaultRoot, loader.Load, lines.Add, out OnlineEntrySettings settings);

        Assert.Equal(0, code);
        Assert.Empty(lines);
        Assert.Same(loader.Settings, settings);
        Assert.Equal(0u, settings.ClientVersion);                   // 读到的配置里是 7,命令行的显式 0 优先
        Assert.Equal((ushort)17, settings.ServerGroup);             // 读到的配置里是 9
        Assert.Equal(1, loader.Calls);
    }

    [Fact]
    public void Prepare_without_the_knobs_returns_the_loaded_settings_untouched()
    {
        var loader = new Loader();
        var lines = new List<string>();

        int code = OnlineOrchestratedSetup.Prepare(Args("--stay 5"), DefaultRoot, loader.Load, lines.Add, out OnlineEntrySettings settings);

        Assert.Equal(0, code);
        Assert.Empty(lines);
        Assert.Same(loader.Settings, settings);
        Assert.Equal(7u, settings.ClientVersion);
        Assert.Equal((ushort)9, settings.ServerGroup);
    }

    [Theory]
    [InlineData("synth-root --stay 5", "synth-root")]            // 第一个不以 -- 开头的参数是仓库根(与以前一致)
    [InlineData("--stay 5 synth-root", DefaultRoot)]             // 不在第一个位置就不是仓库根
    [InlineData("--client-version 3", DefaultRoot)]
    [InlineData("", DefaultRoot)]
    public void Prepare_takes_the_repo_root_only_from_the_first_position(string line, string expectedRoot)
    {
        var loader = new Loader();

        int code = OnlineOrchestratedSetup.Prepare(Args(line), DefaultRoot, loader.Load, _ => { }, out _);

        Assert.Equal(0, code);
        Assert.Equal(expectedRoot, loader.RootSeen);
    }

    [Theory]
    [InlineData("--client-version")]
    [InlineData("--client-version abc")]
    [InlineData("--client-version 4294967296")]
    [InlineData("--server-group 18")]
    [InlineData("--server-group")]
    [InlineData("--server-group 1 --server-group 1")]
    [InlineData("--client-version 1 --client-version 1")]
    [InlineData("synth-root --client-version -1 --stay 3")]
    public void Prepare_prints_one_usage_line_exits_64_and_never_touches_the_loader(string line)
    {
        var loader = new Loader { Succeeds = false };      // 就算会失败也不该被调用:用法错误优先于装配失败
        var lines = new List<string>();

        int code = OnlineOrchestratedSetup.Prepare(Args(line), DefaultRoot, loader.Load, lines.Add, out OnlineEntrySettings settings);

        Assert.Equal(64, code);
        Assert.Equal(new[] { OnlineOrchestratedSetup.UsageLine }, lines);
        Assert.Equal(0, loader.Calls);
        Assert.Null(settings);
    }

    [Fact]
    public void Prepare_reports_a_settings_failure_with_exit_2_and_the_legacy_text()
    {
        var loader = new Loader { Succeeds = false };
        var lines = new List<string>();

        int code = OnlineOrchestratedSetup.Prepare(Args("--client-version 5"), DefaultRoot, loader.Load, lines.Add, out OnlineEntrySettings settings);

        Assert.Equal(2, code);
        Assert.Equal(new[] { "装配失败: SYNTH-PROBLEM-TEXT" }, lines);   // 与探针原来打的那一行一字不差
        Assert.Null(settings);
        Assert.Equal(1, loader.Calls);
    }

    [Fact]
    public void The_orchestrated_usage_line_names_both_knobs()
    {
        Assert.Contains("--client-version <u32>", OnlineOrchestratedSetup.UsageLine);
        Assert.Contains("--server-group <0..17>", OnlineOrchestratedSetup.UsageLine);
        Assert.Contains("orchestrated [仓库根]", OnlineOrchestratedSetup.UsageLine);
    }

    // ======== 三、switch 子命令的命令行 ========

    [Fact]
    public void Switch_reads_the_index_target_with_all_options()
    {
        Assert.True(OnlineSwitchArguments.TryParse(Args("synth-root --to-index 3 --client-version 42435 --server-group 5"),
            out OnlineSwitchArguments parsed, out OnlineProbeArgumentProblem problem));

        Assert.Equal(OnlineProbeArgumentProblem.None, problem);
        Assert.Equal("synth-root", parsed.RepoRoot);
        Assert.True(parsed.TargetIsIndex);
        Assert.Equal(3, parsed.ToIndex);
        Assert.Equal(0, parsed.ToPort);
        Assert.Equal(42435u, parsed.ClientVersion);        // 0xA5C3
        Assert.Equal((ushort)5, parsed.ServerGroup);
    }

    [Fact]
    public void Switch_reads_the_port_target_and_leaves_the_rest_unset()
    {
        Assert.True(OnlineSwitchArguments.TryParse(Args("--to-port 65535"), out OnlineSwitchArguments parsed, out _));

        Assert.Null(parsed.RepoRoot);
        Assert.False(parsed.TargetIsIndex);
        Assert.Equal(-1, parsed.ToIndex);                  // 没按序号选
        Assert.Equal(65535, parsed.ToPort);
        Assert.Null(parsed.ClientVersion);
        Assert.Null(parsed.ServerGroup);
    }

    [Fact]
    public void Switch_accepts_index_zero_and_explicit_zero_knobs_as_given_values()
    {
        Assert.True(OnlineSwitchArguments.TryParse(Args("--server-group 0 --to-index 0 --client-version 0"), out OnlineSwitchArguments parsed, out _));

        Assert.True(parsed.TargetIsIndex);                 // 序号 0 是合法目标,不等于"没给"
        Assert.Equal(0, parsed.ToIndex);
        Assert.NotNull(parsed.ClientVersion);
        Assert.Equal(0u, parsed.ClientVersion);
        Assert.NotNull(parsed.ServerGroup);
        Assert.Equal((ushort)0, parsed.ServerGroup);
    }

    [Theory]
    [InlineData("--to-index 1 --to-port 47399", OnlineProbeArgumentProblem.ConflictingTargets)]       // 同时给
    [InlineData("--to-port 47399 --to-index 1", OnlineProbeArgumentProblem.ConflictingTargets)]
    [InlineData("", OnlineProbeArgumentProblem.MissingTarget)]                                         // 都不给
    [InlineData("synth-root", OnlineProbeArgumentProblem.MissingTarget)]
    [InlineData("--client-version 1 --server-group 2", OnlineProbeArgumentProblem.MissingTarget)]
    [InlineData("--to-index", OnlineProbeArgumentProblem.BadToIndex)]                                   // 缺值
    [InlineData("--to-index abc", OnlineProbeArgumentProblem.BadToIndex)]
    [InlineData("--to-index -1", OnlineProbeArgumentProblem.BadToIndex)]
    [InlineData("--to-index 1000000000", OnlineProbeArgumentProblem.BadToIndex)]
    [InlineData("--to-port", OnlineProbeArgumentProblem.BadToPort)]
    [InlineData("--to-port 0", OnlineProbeArgumentProblem.BadToPort)]                                   // 端口下界减 1
    [InlineData("--to-port 65536", OnlineProbeArgumentProblem.BadToPort)]                               // 端口上界加 1
    [InlineData("--to-port x", OnlineProbeArgumentProblem.BadToPort)]
    [InlineData("--to-index 0 --client-version", OnlineProbeArgumentProblem.BadClientVersion)]
    [InlineData("--to-index 0 --client-version 4294967296", OnlineProbeArgumentProblem.BadClientVersion)]
    [InlineData("--to-index 0 --client-version -3", OnlineProbeArgumentProblem.BadClientVersion)]
    [InlineData("--to-index 0 --server-group", OnlineProbeArgumentProblem.BadServerGroup)]
    [InlineData("--to-index 0 --server-group 18", OnlineProbeArgumentProblem.BadServerGroup)]
    [InlineData("--to-index 0 --to-index 0", OnlineProbeArgumentProblem.DuplicateOption)]             // 值相同也算重复
    [InlineData("--to-port 47399 --to-port 47399", OnlineProbeArgumentProblem.DuplicateOption)]
    [InlineData("--to-index 0 --client-version 1 --client-version 1", OnlineProbeArgumentProblem.DuplicateOption)]
    [InlineData("--to-index 0 --server-group 1 --server-group 2", OnlineProbeArgumentProblem.DuplicateOption)]
    [InlineData("--to-index 0 --bogus", OnlineProbeArgumentProblem.UnknownArgument)]                    // 不认识的选项
    [InlineData("--to-index=0", OnlineProbeArgumentProblem.UnknownArgument)]                            // 不认 = 写法
    [InlineData("--TO-INDEX 0", OnlineProbeArgumentProblem.UnknownArgument)]                            // 选项名区分大小写
    [InlineData("--to-index 0 synth-root", OnlineProbeArgumentProblem.UnknownArgument)]                // 仓库根只能在第一个位置
    [InlineData("--to-index x --to-port y", OnlineProbeArgumentProblem.BadToIndex)]                     // 先遇到的问题先报
    public void Switch_rejects_every_malformed_command_line(string line, OnlineProbeArgumentProblem expected)
    {
        Assert.False(OnlineSwitchArguments.TryParse(Args(line), out OnlineSwitchArguments parsed, out OnlineProbeArgumentProblem problem));

        Assert.Equal(expected, problem);
        Assert.Null(parsed);
    }

    [Fact]
    public void Switch_rejects_null_tokens_and_treats_a_null_list_as_empty()
    {
        Assert.False(OnlineSwitchArguments.TryParse(new string[] { null }, out _, out OnlineProbeArgumentProblem nullToken));
        Assert.Equal(OnlineProbeArgumentProblem.UnknownArgument, nullToken);

        Assert.False(OnlineSwitchArguments.TryParse(null, out _, out OnlineProbeArgumentProblem none));
        Assert.Equal(OnlineProbeArgumentProblem.MissingTarget, none);
    }

    // ---- 非法时不触网:一个"一旦被碰就失败"的假端口 ----

    [Theory]
    [InlineData("")]
    [InlineData("--to-index 1 --to-port 47399")]
    [InlineData("--to-index")]
    [InlineData("--to-index -1")]
    [InlineData("--to-port 0")]
    [InlineData("--to-port 65536")]
    [InlineData("--to-index 0 --client-version 4294967296")]
    [InlineData("--to-index 0 --server-group 18")]
    [InlineData("--to-index 0 --to-index 0")]
    [InlineData("--to-index 0 --client-version 1 --client-version 1")]
    [InlineData("--to-index 0 --bogus SYNTH-SECRET-ARGUMENT")]
    [InlineData("--to-index 0 SYNTH-SECRET-ARGUMENT")]
    public async Task An_invalid_switch_command_line_exits_64_with_one_line_and_never_touches_the_port(string line)
    {
        var port = new FailIfCalledPort();
        var lines = new List<string>();

        int code = await OnlineSwitchProbe.RunAsync(Args(line), DefaultRoot, port, lines.Add);

        Assert.Equal(64, code);
        Assert.Equal(0, port.Calls);                       // 没读配置、没拿会话,所以不可能联网
        string only = Assert.Single(lines);
        Assert.StartsWith("PROBE_SWITCH usage problem=", only);
        Assert.Contains(" exit=64 ", only);
        Assert.Contains(OnlineSwitchProbe.Syntax, only);
        Assert.DoesNotContain("SYNTH-SECRET-ARGUMENT", only);   // 用法行不回显用户输入
    }

    [Fact]
    public void Orchestrated_option_names_are_matched_exactly_so_a_differently_cased_one_is_not_a_knob()
    {
        bool ok = OnlineOrchestratedKnobs.TryParse(Args("--CLIENT-VERSION 5 --Server-Group 3"), out OnlineOrchestratedKnobs knobs, out OnlineProbeArgumentProblem problem);

        Assert.True(ok);
        Assert.Equal(OnlineProbeArgumentProblem.None, problem);
        Assert.Null(knobs.ClientVersion);                  // 大小写不同的不是这个开关:和别的松散选项一样原样放过,不覆盖设置
        Assert.Null(knobs.ServerGroup);
    }

    [Fact]
    public async Task The_switch_runner_requires_a_port_and_an_output_sink()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => OnlineSwitchProbe.RunAsync(Args("--to-index 0"), DefaultRoot, null, _ => { }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OnlineSwitchProbe.RunAsync(Args("--to-index 0"), DefaultRoot, new FailIfCalledPort(), null));
    }
}
