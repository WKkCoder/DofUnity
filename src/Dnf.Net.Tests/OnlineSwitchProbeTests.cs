using System.Text;
using System.Text.RegularExpressions;
using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 子命令的流程,跑在假会话与假端口上(没有网络、不读本机配置):
/// 成功、各步失败对应的退出码、目标选择、"目录不够就一个频道也不连"、网关只登录一次、角色列表三种状态与零条目、
/// 两个开关的接线、输出行的格式与脱敏。真会话(内存假网关与内存管道)上的"只发登录帧"见 <see cref="OnlineSwitchRealSessionTests"/>。
/// </summary>
public sealed class OnlineSwitchProbeTests
{
    private static readonly Regex FinalLine = new(
        @"^PROBE_SWITCH result=(ok|fail) first=[A-Za-z]+ second=[A-Za-z]+ attempts1=\d+ attempts2=\d+ group1=\d+ group2=\d+ clientVersion=\d+ elapsedMs=\d+$");

    private static async Task<(int Exit, List<string> Lines)> Run(FakePort port, string line)
    {
        var lines = new List<string>();
        string[] args = line.Length == 0 ? Array.Empty<string>() : line.Split(' ');
        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(args, DefaultRoot, port, lines.Add), 10);
        return (exit, lines);
    }

    /// <summary>最后一行里的耗时是真实时钟,换成 N 再比。</summary>
    private static string Final(List<string> lines)
    {
        string last = lines[^1];
        Assert.Matches(FinalLine, last);
        return Regex.Replace(last, @"elapsedMs=\d+$", "elapsedMs=N");
    }

    private static string[] Stages(List<string> lines)
    {
        return lines.Select(l => Regex.Match(l, @" stage=(\S+)")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
    }

    private static string StepLine(List<string> lines, string stage)
    {
        return Assert.Single(lines, l => l.Contains(" stage=" + stage + " "));
    }

    // ======== 一、成功与目标选择 ========

    [Fact]
    public async Task A_good_run_to_an_index_target_goes_login_directory_connect_switch_disconnect_and_nothing_else()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Connected(3, attempts: 1, group: 3);
        port.Session.OnSwitch = (_, _) => Connected(2, attempts: 2, group: 3);

        (int exit, List<string> lines) = await Run(port, "--to-index 1");

        Assert.Equal(0, exit);
        // 候选(标准目录):0 号 = 乙主机乙端口,1 号 = 丙主机丙端口,2 号 = 丙主机丁端口;第一个频道本身与端口无效的条目被排除
        Assert.Equal(new[]
        {
            "login",
            "directory",
            "connect:" + FirstPort,
            "switch-host:" + HostC + ":" + PortC,
            "disconnect",
        }, port.Session.Calls);
        Assert.Equal(new[] { "settings", "gateway-login", "directory", "connect", "switch", "disconnect" }, Stages(lines));
        Assert.Equal("PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=2 group1=3 group2=3 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.True(port.Session.Disposed);
    }

    [Theory]
    [InlineData(0, HostB, PortB)]     // 候选 0
    [InlineData(1, HostC, PortC)]     // 候选 1(端口无效的那条被跳过)
    [InlineData(2, HostC, PortD)]     // 候选 2
    public async Task To_index_picks_the_kth_candidate_in_directory_order(int index, string expectedHost, int expectedPort)
    {
        var port = new FakePort();

        (int exit, _) = await Run(port, "--to-index " + index);

        Assert.Equal(0, exit);
        Assert.Contains("switch-host:" + expectedHost + ":" + expectedPort, port.Session.Calls);
        Assert.Single(port.Session.Calls, c => c.StartsWith("switch-"));
    }

    [Fact]
    public async Task To_index_beyond_the_candidates_connects_nothing_and_exits_7()
    {
        var port = new FakePort();

        (int exit, List<string> lines) = await Run(port, "--to-index 3");   // 候选只有 3 个(0..2)

        Assert.Equal(7, exit);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);    // 没有 connect、没有 switch
        Assert.Equal("PROBE_SWITCH stage=target to=index:3 outcome=NoTarget", StepLine(lines, "target"));
        Assert.Equal("PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.True(port.Session.Disposed);
    }

    [Fact]
    public async Task An_empty_directory_has_no_target_for_an_index_but_is_fine_for_a_port()
    {
        var byIndex = new FakePort();
        byIndex.Session.OnDirectory = () => Directory();
        (int indexExit, _) = await Run(byIndex, "--to-index 0");
        Assert.Equal(7, indexExit);
        Assert.DoesNotContain(byIndex.Session.Calls, c => c.StartsWith("connect:"));

        var byPort = new FakePort();
        byPort.Session.OnDirectory = () => Directory();
        (int portExit, _) = await Run(byPort, "--to-port " + TargetPort);
        Assert.Equal(0, portExit);                                           // 按端口选不依赖目录里有什么,只要目录读成功
    }

    [Fact]
    public async Task To_port_switches_on_the_same_host_without_looking_at_the_directory_entries()
    {
        var port = new FakePort();

        (int exit, List<string> lines) = await Run(port, "--to-port " + TargetPort);

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort, "switch-port:" + TargetPort, "disconnect" }, port.Session.Calls);
        Assert.Equal("PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=1 group1=3 group2=3 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task Every_step_line_carries_the_ordinal_target_token()
    {
        var byIndex = await Run(new FakePort(), "--to-index 2");
        Assert.All(byIndex.Lines.Where(l => !l.Contains(" result=")), l => Assert.Contains(" to=index:2", l));

        var byPort = await Run(new FakePort(), "--to-port " + TargetPort);
        Assert.All(byPort.Lines.Where(l => !l.Contains(" result=")), l => Assert.Contains(" to=port", l));
    }

    // ---- 候选的判定(纯函数) ----

    private static ChannelEntry[] Entries(params (string Host, int Port)[] channels)
    {
        return Directory(channels).Snapshot.Channels.ToArray();
    }

    [Fact]
    public void Candidates_exclude_exactly_the_same_host_and_port_as_the_first_channel()
    {
        ChannelEntry[] entries = Entries(
            (GatewayHost, FirstPort),     // 同主机同端口:排除
            (GatewayHost, PortB),         // 同主机不同端口:候选
            (HostB, FirstPort),           // 不同主机同端口:候选
            (HostB, PortB));              // 都不同:候选

        List<ChannelEntry> candidates = OnlineSwitchProbe.Candidates(entries, GatewayHost, FirstPort);

        Assert.Equal(new[] { PortB, FirstPort, PortB }, candidates.Select(c => c.Port));
        Assert.Equal(new[] { GatewayHost, HostB, HostB }, candidates.Select(c => c.Host));
    }

    [Fact]
    public void Candidates_drop_entries_without_a_valid_port()
    {
        ChannelEntry[] entries = Entries((HostB, 0), (HostB, 65536), (HostB, -1), (HostB, 65535), (HostB, 1));

        List<ChannelEntry> candidates = OnlineSwitchProbe.Candidates(entries, GatewayHost, FirstPort);

        Assert.Equal(new[] { 65535, 1 }, candidates.Select(c => c.Port));     // 端口有效 = 1..65535;目录顺序不变
    }

    [Fact]
    public void A_directory_entry_without_a_host_counts_as_the_gateway_host()
    {
        ChannelEntry[] entries = Entries(("", FirstPort), ("  ", PortB), ("", PortC));

        List<ChannelEntry> candidates = OnlineSwitchProbe.Candidates(entries, GatewayHost, FirstPort);

        Assert.Equal(new[] { PortB, PortC }, candidates.Select(c => c.Port));  // 没写主机且端口等于第一个频道端口的被排除
    }

    [Fact]
    public void Host_comparison_ignores_case_and_surrounding_blanks_and_reads_ip_literals_as_addresses()
    {
        string ip = GatewayTestSupport.Dots(198, 51, 100, 7);
        string other = GatewayTestSupport.Dots(198, 51, 100, 8);
        ChannelEntry[] entries = Entries(
            (GatewayHost.ToLowerInvariant(), FirstPort),   // 大小写不同:同一个主机
            (" " + GatewayHost + " ", FirstPort),          // 两端空白:同一个主机
            (ip, FirstPort),                               // 与下面第一个频道的 IP 字面量相同
            (other, FirstPort));                           // 不同地址

        Assert.Empty(OnlineSwitchProbe.Candidates(entries.Take(2).ToArray(), GatewayHost, FirstPort));

        List<ChannelEntry> ipCandidates = OnlineSwitchProbe.Candidates(entries.Skip(2).ToArray(), ip, FirstPort);
        Assert.Equal(new[] { other }, ipCandidates.Select(c => c.Host));
    }

    [Fact]
    public void Without_a_known_gateway_host_only_blank_hosts_match_the_first_channel()
    {
        ChannelEntry[] entries = Entries(("", FirstPort), (HostB, FirstPort));

        Assert.Equal(new[] { HostB }, OnlineSwitchProbe.Candidates(entries, null, FirstPort).Select(c => c.Host));
        Assert.Equal(new[] { HostB }, OnlineSwitchProbe.Candidates(entries, "  ", FirstPort).Select(c => c.Host));
    }

    [Fact]
    public void Select_by_index_handles_the_edges()
    {
        ChannelEntry[] entries = Entries((HostB, PortB), (HostC, PortC));

        Assert.Equal(PortB, OnlineSwitchProbe.SelectByIndex(entries, GatewayHost, FirstPort, 0).Port);
        Assert.Equal(PortC, OnlineSwitchProbe.SelectByIndex(entries, GatewayHost, FirstPort, 1).Port);
        Assert.Null(OnlineSwitchProbe.SelectByIndex(entries, GatewayHost, FirstPort, 2));    // 刚好差一个
        Assert.Null(OnlineSwitchProbe.SelectByIndex(entries, GatewayHost, FirstPort, -1));
        Assert.Null(OnlineSwitchProbe.SelectByIndex(null, GatewayHost, FirstPort, 0));
        Assert.Empty(OnlineSwitchProbe.Candidates(null, GatewayHost, FirstPort));
    }

    [Fact]
    public async Task A_candidate_without_a_host_is_reached_through_the_port_overload()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => Directory(("", PortB));      // 没写主机:就是网关给的主机上的端口

        (int exit, _) = await Run(port, "--to-index 0");

        Assert.Equal(0, exit);
        Assert.Equal("switch-port:" + PortB, Assert.Single(port.Session.Calls, c => c.StartsWith("switch-")));
    }

    // ======== 二、各步失败对应的退出码 ========

    [Fact]
    public async Task A_first_channel_failure_exits_3_and_never_tries_to_switch()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Failed(OnlineFailure.ConnectFailed, OnlineStage.ChannelConnect);

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(3, exit);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort }, port.Session.Calls);
        Assert.Equal("PROBE_SWITCH result=fail first=ConnectFailed second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.True(port.Session.Disposed);
    }

    [Fact]
    public async Task A_switch_failure_exits_4_and_reports_the_login_code_and_the_retry_budget()
    {
        var port = new FakePort();
        port.Session.OnSwitch = (_, _) => Failed(OnlineFailure.GameLoginRejected, OnlineStage.GameLogin, loginCode: 1, attempts: 7, group: 3);

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(4, exit);
        Assert.DoesNotContain("disconnect", port.Session.Calls);              // 换失败就结束(会话释放时关连接)
        Assert.Equal(1, port.Session.Calls.Count(c => c.StartsWith("switch-")));    // 探针自己不重试:只换了一次
        // 7 = 1 次首发 + 会话内部台账 E-443 的 6 次重试(这里是假会话直接给出的数)
        Assert.Equal("PROBE_SWITCH result=fail first=Ok second=GameLoginRejected attempts1=1 attempts2=7 group1=3 group2=3 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.Contains(" loginFailureCode=1 ", StepLine(lines, "switch"));
        Assert.True(port.Session.Disposed);
    }

    [Fact]
    public async Task The_exit_codes_3_and_4_belong_to_the_first_and_second_step_respectively()
    {
        var firstBad = new FakePort();
        firstBad.Session.OnConnect = _ => Failed(OnlineFailure.Rejected, OnlineStage.ChannelConnect);
        var secondBad = new FakePort();
        secondBad.Session.OnSwitch = (_, _) => Failed(OnlineFailure.Rejected, OnlineStage.ChannelConnect);

        Assert.Equal(3, (await Run(firstBad, "--to-port " + TargetPort)).Exit);
        Assert.Equal(4, (await Run(secondBad, "--to-port " + TargetPort)).Exit);
    }

    [Theory]
    [InlineData(OnlineFailure.ConnectFailed, "ConnectFailed")]
    [InlineData(OnlineFailure.BootstrapFailed, "BootstrapFailed")]
    [InlineData(OnlineFailure.Rejected, "Rejected")]
    [InlineData(OnlineFailure.GameLoginRejected, "GameLoginRejected")]
    [InlineData(OnlineFailure.GameLoginNoReply, "GameLoginNoReply")]
    [InlineData(OnlineFailure.BadLoginRequest, "BadLoginRequest")]
    [InlineData(OnlineFailure.NoUsableSlot, "OtherFailure")]        // 连频道不会给出的失败原因:归到"其它"
    public async Task Each_failure_kind_has_its_own_outcome_name_on_both_steps(OnlineFailure failure, string outcome)
    {
        var first = new FakePort();
        first.Session.OnConnect = _ => Failed(failure, OnlineStage.GameLogin);
        (int firstExit, List<string> firstLines) = await Run(first, "--to-port " + TargetPort);
        Assert.Equal(3, firstExit);
        Assert.Equal($"PROBE_SWITCH result=fail first={outcome} second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(firstLines));

        var second = new FakePort();
        second.Session.OnSwitch = (_, _) => Failed(failure, OnlineStage.GameLogin);
        (int secondExit, List<string> secondLines) = await Run(second, "--to-port " + TargetPort);
        Assert.Equal(4, secondExit);
        Assert.Equal($"PROBE_SWITCH result=fail first=Ok second={outcome} attempts1=1 attempts2=0 group1=3 group2=0 clientVersion=0 elapsedMs=N", Final(secondLines));
    }

    [Theory]
    [InlineData(OnlineFailure.ConnectFailed, OnlineSwitchStepOutcome.ConnectFailed)]
    [InlineData(OnlineFailure.BootstrapFailed, OnlineSwitchStepOutcome.BootstrapFailed)]
    [InlineData(OnlineFailure.Rejected, OnlineSwitchStepOutcome.Rejected)]
    [InlineData(OnlineFailure.GameLoginRejected, OnlineSwitchStepOutcome.GameLoginRejected)]
    [InlineData(OnlineFailure.GameLoginNoReply, OnlineSwitchStepOutcome.GameLoginNoReply)]
    [InlineData(OnlineFailure.BadLoginRequest, OnlineSwitchStepOutcome.BadLoginRequest)]
    [InlineData(OnlineFailure.GatewayLoginFailed, OnlineSwitchStepOutcome.OtherFailure)]
    [InlineData(OnlineFailure.NoGameServerAddress, OnlineSwitchStepOutcome.OtherFailure)]
    [InlineData(OnlineFailure.NoUsableSlot, OnlineSwitchStepOutcome.OtherFailure)]
    public void The_outcome_of_a_failed_step_follows_the_failure_kind(OnlineFailure failure, OnlineSwitchStepOutcome expected)
    {
        Assert.Equal(expected, OnlineSwitchProbe.OutcomeOf(Failed(failure, OnlineStage.GameLogin)));
    }

    [Fact]
    public void The_outcome_of_a_successful_step_follows_the_character_list_state_and_a_missing_result_is_a_fault()
    {
        Assert.Equal(OnlineSwitchStepOutcome.Ok, OnlineSwitchProbe.OutcomeOf(Connected(3)));
        Assert.Equal(OnlineSwitchStepOutcome.Ok, OnlineSwitchProbe.OutcomeOf(Connected(0)));    // 空列表也是收到了
        Assert.Equal(OnlineSwitchStepOutcome.CharacterListNotReceived,
            OnlineSwitchProbe.OutcomeOf(ConnectedWithoutList(OnlineCharacterListStatus.NotReceived)));
        Assert.Equal(OnlineSwitchStepOutcome.CharacterListMalformed,
            OnlineSwitchProbe.OutcomeOf(ConnectedWithoutList(OnlineCharacterListStatus.Malformed)));
        Assert.Equal(OnlineSwitchStepOutcome.Faulted, OnlineSwitchProbe.OutcomeOf(null));
    }

    [Fact]
    public async Task A_directory_failure_exits_6_and_connects_nothing()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "SYNTH-DETAIL " + GatewayHost);

        (int exit, List<string> lines) = await Run(port, "--to-port " + TargetPort);

        Assert.Equal(6, exit);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
        Assert.Equal("PROBE_SWITCH stage=directory to=port outcome=IoError elapsedMs=N", Regex.Replace(StepLine(lines, "directory"), @"elapsedMs=\d+$", "elapsedMs=N"));
        Assert.Equal("PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task A_directory_that_succeeds_without_a_snapshot_is_still_a_directory_failure()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Success(null, 0, true);

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(6, exit);
        Assert.Contains(" outcome=NoSnapshot ", StepLine(lines, "directory"));
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
    }

    [Fact]
    public async Task A_missing_directory_result_is_a_fault_and_exits_6()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => null;

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(6, exit);
        Assert.Contains(" outcome=Faulted ", StepLine(lines, "directory"));
    }

    [Fact]
    public async Task A_gateway_failure_exits_5_immediately_and_is_never_retried()
    {
        var port = new FakePort();
        port.Session.OnLogin = LoginFailed;

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(5, exit);
        Assert.Equal(new[] { "login" }, port.Session.Calls);                     // 只登录一次,没读目录、没连频道
        Assert.StartsWith("PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin ", StepLine(lines, "gateway-login"));
        Assert.Equal("PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.True(port.Session.Disposed);
    }

    [Fact]
    public async Task A_missing_gateway_result_is_a_fault_and_exits_5()
    {
        var port = new FakePort();
        port.Session.OnLogin = () => null;

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(5, exit);
        Assert.Contains(" outcome=Faulted ", StepLine(lines, "gateway-login"));
        Assert.Equal(new[] { "login" }, port.Session.Calls);
    }

    [Theory]
    [InlineData("good")]
    [InlineData("gateway-failure")]
    [InlineData("gateway-throws")]
    [InlineData("directory-failure")]
    [InlineData("no-target")]
    [InlineData("first-failure")]
    [InlineData("switch-failure")]
    public async Task The_gateway_login_is_called_exactly_once_on_every_path(string scenario)
    {
        var port = new FakePort();
        string line = "--to-index 0";
        switch (scenario)
        {
            case "gateway-failure": port.Session.OnLogin = LoginFailed; break;
            case "gateway-throws": port.Session.OnLogin = () => throw new IOException("SYNTH"); break;
            case "directory-failure": port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, "SYNTH"); break;
            case "no-target": line = "--to-index 9"; break;
            case "first-failure": port.Session.OnConnect = _ => Failed(OnlineFailure.ConnectFailed, OnlineStage.ChannelConnect); break;
            case "switch-failure": port.Session.OnSwitch = (_, _) => Failed(OnlineFailure.GameLoginNoReply, OnlineStage.GameLogin); break;
        }

        await Run(port, line);

        Assert.Equal(1, port.Session.Count("login"));
        Assert.Equal(1, port.CreateCalls);                                        // 也只拿了一个会话
        Assert.Equal(OnlineSwitchTestKit.Account, port.Session.LoginAccount);
        Assert.Equal(OnlineSwitchTestKit.Password, port.Session.LoginPassword);
    }

    // ======== 三、本地装配 ========

    [Fact]
    public async Task A_settings_failure_exits_2_without_a_session_and_prints_no_problem_text()
    {
        var port = new FakePort { LoadSucceeds = false };

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(2, exit);
        Assert.Equal(0, port.CreateCalls);
        Assert.Empty(port.Session.Calls);
        Assert.Equal(2, lines.Count);
        Assert.Equal("PROBE_SWITCH stage=settings to=index:0 outcome=SettingsFailed", lines[0]);
        Assert.Equal("PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    [Fact]
    public async Task A_session_that_cannot_be_created_is_also_a_settings_failure_with_only_the_type_name()
    {
        var port = new FakePort { ThrowOnCreate = true };

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(2, exit);
        Assert.Equal("PROBE_SWITCH stage=settings to=index:0 outcome=SettingsFailed fault=IOException", lines[0]);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    [Fact]
    public async Task The_repo_root_comes_from_the_command_line_or_else_the_default()
    {
        var given = new FakePort();
        await Run(given, "synth-root --to-index 0");
        Assert.Equal("synth-root", given.RepoRootSeen);

        var fallback = new FakePort();
        await Run(fallback, "--to-index 0");
        Assert.Equal(DefaultRoot, fallback.RepoRootSeen);
    }

    [Fact]
    public async Task The_knobs_are_applied_on_top_of_the_loaded_settings_before_the_session_is_created()
    {
        var port = new FakePort { Settings = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9) };

        (int exit, List<string> lines) = await Run(port, "--to-index 0 --client-version 42435 --server-group 5");

        Assert.Equal(0, exit);
        Assert.Same(port.Settings, port.SettingsSeenByCreate);
        Assert.Equal(42435u, port.SettingsSeenByCreate.ClientVersion);   // 0xA5C3;读到的配置里是 7
        Assert.Equal((ushort)5, port.SettingsSeenByCreate.ServerGroup);  // 读到的配置里是 9
        Assert.Equal("PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=42435 serverGroup=5", lines[0]);
        Assert.EndsWith("clientVersion=42435 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task Knobs_left_out_keep_the_loaded_values_and_an_explicit_zero_overrides_them()
    {
        var kept = new FakePort { Settings = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9) };
        (int keptExit, List<string> keptLines) = await Run(kept, "--to-index 0");
        Assert.Equal(0, keptExit);
        Assert.Equal(7u, kept.SettingsSeenByCreate.ClientVersion);
        Assert.Equal((ushort)9, kept.SettingsSeenByCreate.ServerGroup);
        Assert.Equal("PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=7 serverGroup=9", keptLines[0]);

        var zeroed = new FakePort { Settings = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9) };
        (int zeroExit, List<string> zeroLines) = await Run(zeroed, "--to-index 0 --client-version 0 --server-group 0");
        Assert.Equal(0, zeroExit);
        Assert.Equal(0u, zeroed.SettingsSeenByCreate.ClientVersion);
        Assert.Equal((ushort)0, zeroed.SettingsSeenByCreate.ServerGroup);
        Assert.Equal("PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=0 serverGroup=0", zeroLines[0]);
    }

    // ======== 四、角色列表的三种状态与零条目 ========

    [Fact]
    public async Task Zero_characters_on_both_lists_is_still_a_success()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Connected(0, 1, 3);
        port.Session.OnSwitch = (_, _) => Connected(0, 1, 3);

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(0, exit);
        Assert.Contains(" characterList=Received characters=0 ", StepLine(lines, "connect"));
        Assert.Contains(" characterList=Received characters=0 ", StepLine(lines, "switch"));
        Assert.Equal("PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=1 group1=3 group2=3 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task The_character_counts_are_reported_per_step()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Connected(3);
        port.Session.OnSwitch = (_, _) => Connected(5);

        (_, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Contains(" characterList=Received characters=3 ", StepLine(lines, "connect"));
        Assert.Contains(" characterList=Received characters=5 ", StepLine(lines, "switch"));
    }

    [Theory]
    [InlineData(OnlineCharacterListStatus.NotReceived, "CharacterListNotReceived")]
    [InlineData(OnlineCharacterListStatus.Malformed, "CharacterListMalformed")]
    public async Task A_step_without_a_usable_list_is_a_failure_of_that_step(OnlineCharacterListStatus status, string outcome)
    {
        var first = new FakePort();
        first.Session.OnConnect = _ => ConnectedWithoutList(status, attempts: 1, group: 3);
        (int firstExit, List<string> firstLines) = await Run(first, "--to-index 0");
        Assert.Equal(3, firstExit);
        Assert.DoesNotContain(first.Session.Calls, c => c.StartsWith("switch-"));
        Assert.Contains($" characterList={status} characters=- ", StepLine(firstLines, "connect"));
        Assert.Equal($"PROBE_SWITCH result=fail first={outcome} second=NotRun attempts1=1 attempts2=0 group1=3 group2=0 clientVersion=0 elapsedMs=N", Final(firstLines));

        var second = new FakePort();
        second.Session.OnSwitch = (_, _) => ConnectedWithoutList(status, attempts: 1, group: 3);
        (int secondExit, List<string> secondLines) = await Run(second, "--to-index 0");
        Assert.Equal(4, secondExit);
        Assert.Contains($" characterList={status} characters=- ", StepLine(secondLines, "switch"));
        Assert.Equal($"PROBE_SWITCH result=fail first=Ok second={outcome} attempts1=1 attempts2=1 group1=3 group2=3 clientVersion=0 elapsedMs=N", Final(secondLines));
    }

    // ======== 五、异常与取消 ========

    [Theory]
    [InlineData("login", false)]
    [InlineData("login", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    [InlineData("connect", false)]
    [InlineData("connect", true)]
    [InlineData("switch", false)]
    [InlineData("switch", true)]
    public async Task A_throwing_session_call_ends_that_step_with_only_the_exception_type_name(string where, bool asTask)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = asTask;
        string message = "SYNTH-EXCEPTION-MESSAGE " + GatewayHost + ":" + FirstPort + " " + Account;
        switch (where)
        {
            case "login": port.Session.OnLogin = () => throw new IOException(message); break;
            case "directory": port.Session.OnDirectory = () => throw new IOException(message); break;
            case "connect": port.Session.OnConnect = _ => throw new IOException(message); break;
            case "switch": port.Session.OnSwitch = (_, _) => throw new IOException(message); break;
        }
        int expectedExit = where switch { "login" => 5, "directory" => 6, "connect" => 3, _ => 4 };

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(expectedExit, exit);
        string all = string.Join("\n", lines);
        Assert.Contains(" outcome=Faulted", all);
        Assert.Contains(" fault=IOException", all);
        Assert.DoesNotContain("SYNTH", all);                      // 异常消息一个字也不出现
        Assert.DoesNotContain(Account, all);
        foreach (string portText in PortTexts)
        {
            Assert.DoesNotContain(portText, all);
        }
        Assert.Matches(FinalLine, lines[^1]);
        Assert.True(port.Session.Disposed);
    }

    [Fact]
    public async Task A_faulted_step_counts_as_no_login_attempts_and_group_zero()
    {
        var port = new FakePort();
        port.Session.OnSwitch = (_, _) => throw new InvalidOperationException("SYNTH");

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(4, exit);
        Assert.Equal("PROBE_SWITCH result=fail first=Ok second=Faulted attempts1=1 attempts2=0 group1=3 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.Contains(" failure=- reached=- loginFailureCode=- characterList=NotReceived characters=- fault=InvalidOperationException ", StepLine(lines, "switch"));
    }

    [Fact]
    public async Task A_failing_disconnect_does_not_change_a_successful_result()
    {
        var port = new FakePort();
        port.Session.OnDisconnect = () => throw new IOException("SYNTH-DISCONNECT " + GatewayHost);

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(0, exit);                                    // 两步都成功且两次都收到角色列表:断开只是收尾
        Assert.StartsWith("PROBE_SWITCH stage=disconnect to=index:0 outcome=Faulted fault=IOException elapsedMs=", StepLine(lines, "disconnect"));
        Assert.StartsWith("PROBE_SWITCH result=ok ", lines[^1]);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("switch")]
    public async Task Cancellation_ends_the_run_with_exit_15_and_still_prints_the_last_line(string where)
    {
        var port = new FakePort();
        if (where == "connect")
        {
            port.Session.OnConnect = _ => throw new OperationCanceledException();
        }
        else
        {
            port.Session.OnSwitch = (_, _) => throw new OperationCanceledException();
        }

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(15, exit);
        Assert.Contains("PROBE_SWITCH stage=abort outcome=Cancelled", lines);
        Assert.StartsWith("PROBE_SWITCH result=fail ", lines[^1]);
        Assert.Matches(FinalLine, lines[^1]);
        Assert.True(port.Session.Disposed);
    }

    [Fact]
    public async Task A_bug_in_the_flow_itself_exits_70_with_only_the_type_name()
    {
        // 选目标时要读会话的 GameServerHost:这个假会话让它抛异常,模拟流程自己出了没料到的错(不是会话调用的失败)
        var throwing = new ThrowingHostSession();
        var lines = new List<string>();

        int exit = await OnlineSwitchProbe.RunAsync(new[] { "--to-index", "0" }, DefaultRoot, new SingleSessionPort(throwing), lines.Add);

        Assert.Equal(70, exit);
        Assert.Contains("PROBE_SWITCH stage=abort outcome=Unexpected fault=InvalidOperationException", lines);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
        Assert.StartsWith("PROBE_SWITCH result=fail ", lines[^1]);
        Assert.True(throwing.Disposed);
    }

    private sealed class SingleSessionPort : IOnlineSwitchPort
    {
        private readonly IOnlineSwitchSession _session;

        internal SingleSessionPort(IOnlineSwitchSession session)
        {
            _session = session;
        }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            settings = OnlineSwitchTestKit.Settings();
            problem = null;
            return true;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings) => _session;
    }

    /// <summary>目录读成功之后选目标时才会读 <see cref="GameServerHost"/>:这里让它抛异常,模拟流程自己出了没料到的错。</summary>
    private sealed class ThrowingHostSession : IOnlineSwitchSession
    {
        internal bool Disposed { get; private set; }

        public uint ClientVersion => 0;
        public string GameServerHost => throw new InvalidOperationException("SYNTH-HOST-READ " + GatewayHost);

        public Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken) => Task.FromResult(LoginOk());
        public Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(StandardDirectory());
        public Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    // ======== 六、输出的格式与脱敏 ========

    [Fact]
    public async Task Every_output_line_starts_with_the_prefix_and_the_last_line_is_the_result_line()
    {
        foreach (string line in new[] { "--to-index 0", "--to-port " + TargetPort, "--to-index 99" })
        {
            (_, List<string> lines) = await Run(new FakePort(), line);

            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.StartsWith("PROBE_SWITCH ", l));
            Assert.Matches(FinalLine, lines[^1]);
            Assert.Single(lines, l => l.Contains(" result="));       // 最后一行只有一条,且只在最后
        }
    }

    [Fact]
    public async Task The_success_path_prints_exactly_these_lines_up_to_the_elapsed_times()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Connected(3, 1, 3);
        port.Session.OnSwitch = (_, _) => Connected(2, 2, 5);

        (_, List<string> lines) = await Run(port, "--to-index 2");

        string[] normalised = lines.Select(l => Regex.Replace(l, @"elapsedMs=\d+", "elapsedMs=N")).ToArray();
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=index:2 outcome=Ok clientVersion=0 serverGroup=0",
            "PROBE_SWITCH stage=gateway-login to=index:2 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=index:2 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=connect to=index:2 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=3 attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH stage=switch to=index:2 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=2 attempts=2 serverGroup=5 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH stage=disconnect to=index:2 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=2 group1=3 group2=5 clientVersion=0 elapsedMs=N",
        }, normalised);
    }

    [Fact]
    public async Task Nothing_synthetic_leaks_into_the_output_on_any_path()
    {
        // 带了合成的账号、口令、主机、频道名、组标签、端口、网关地址、角色名;输出里一个都不许出现
        string[] secrets =
        {
            Account, Password, UrlHost, GatewayHost, HostB, HostC, "SYNTH-CHANNEL", "SYNTH-GROUP", "SYNTH-PROBLEM", "synth-url",
        };
        UserInfoNotice notice = Notice(3);
        string[] characterNames = notice.Entries
            .SelectMany(e => new[] { Encoding.ASCII.GetString(e.NameBytes.ToArray()), Convert.ToHexString(e.NameBytes.ToArray()) })
            .ToArray();

        var scenarios = new Func<FakePort>[]
        {
            () => new FakePort(),
            () => { var p = new FakePort(); p.Session.OnConnect = _ => Connected(3, 1, 3); p.Session.OnSwitch = (_, _) => Connected(3, 7, 3); return p; },
            () => { var p = new FakePort(); p.Session.OnSwitch = (_, _) => Failed(OnlineFailure.GameLoginRejected, OnlineStage.GameLogin, 1, 7, 3); return p; },
            () => { var p = new FakePort(); p.Session.OnConnect = _ => Failed(OnlineFailure.ConnectFailed, OnlineStage.ChannelConnect); return p; },
            () => new FakePort { LoadSucceeds = false },
            () => { var p = new FakePort(); p.Session.OnLogin = LoginFailed; return p; },
            () => { var p = new FakePort(); p.Session.OnDirectory = () => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "SYNTH-DETAIL"); return p; },
        };

        foreach (string args in new[] { "--to-index 0", "--to-index 2", "--to-port " + TargetPort, "--to-index 9" })
        {
            foreach (Func<FakePort> make in scenarios)
            {
                (_, List<string> lines) = await Run(make(), args);
                string all = string.Join("\n", lines);

                foreach (string secret in secrets.Concat(characterNames))
                {
                    Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
                }
                foreach (string portText in PortTexts)
                {
                    Assert.DoesNotContain(portText, all);
                }
                Assert.DoesNotContain("http", all, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("://", all);
                Assert.DoesNotContain(".invalid", all);
                Assert.All(lines, l => Assert.StartsWith("PROBE_SWITCH ", l));
            }
        }
    }

    [Fact]
    public async Task The_usage_line_is_one_line_with_the_prefix_and_the_syntax()
    {
        var lines = new List<string>();

        int exit = await OnlineSwitchProbe.RunAsync(new[] { "--to-index", "0", "--to-port", "5" }, DefaultRoot, new FakePort(), lines.Add);

        Assert.Equal(64, exit);
        Assert.Equal(
            "PROBE_SWITCH usage problem=ConflictingTargets exit=64 syntax: dnf-netprobe switch [仓库根] --to-index <K> | --to-port <N> [--client-version <u32>] [--server-group <0..17>]",
            Assert.Single(lines));
    }

    [Fact]
    public void The_exit_code_table_is_the_documented_one()
    {
        Assert.Equal(0, OnlineSwitchExitCodes.Success);
        Assert.Equal(2, OnlineSwitchExitCodes.SettingsFailed);
        Assert.Equal(3, OnlineSwitchExitCodes.FirstConnectFailed);
        Assert.Equal(4, OnlineSwitchExitCodes.SwitchFailed);
        Assert.Equal(5, OnlineSwitchExitCodes.GatewayAuthFailed);
        Assert.Equal(6, OnlineSwitchExitCodes.DirectoryFailed);
        Assert.Equal(7, OnlineSwitchExitCodes.NoTarget);
        Assert.Equal(15, OnlineSwitchExitCodes.Cancelled);
        Assert.Equal(64, OnlineSwitchExitCodes.Usage);
        Assert.Equal(70, OnlineSwitchExitCodes.Unexpected);
    }
}
