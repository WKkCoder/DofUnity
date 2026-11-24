using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineProbeKnobsGap2Support;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 的"连第一个频道"(<c>stage=connect</c>)与"换频道"(<c>stage=switch</c>)两步在每一种结局下打出的整行文本,
/// 以及这两步失败时的最后一行 <c>result=fail first=… second=…</c>(耗时归一成 N 之后逐字比):
/// 连不上、没收到启动包、被拒、游戏登录被拒(带失败码 0/9/255)、游戏登录没回应、登录请求本地检查不过、
/// 没有角色列表(未收到/畸形)、产品层给出的其它失败三种、会话调用抛异常(同步抛、抛成出错的任务、带内层异常)与返回 null。
/// 第一步失败退出码 3(没换频道、没断开),第二步失败退出码 4(没断开)。假端口、假会话;不连网、不读本机配置。
/// </summary>
public sealed class OnlineProbeKnobsGap2StepLinesTests
{
    private const string Cmd = "--to-index 0";
    private const string SettingsLine = "PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=0 serverGroup=0";
    private const string GatewayOkLine = "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Ok elapsedMs=N";
    private const string DirectoryOkLine = "PROBE_SWITCH stage=directory to=index:0 outcome=Ok elapsedMs=N";

    // 假会话默认的第一步结果:3 个角色、游戏登录 1 次、服务器组 3
    private const string ConnectOkLine = "PROBE_SWITCH stage=connect to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=3 attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N";

    private sealed record StepCase(string Name, Func<OnlineConnectResult> Make, string Suffix, string Outcome, int Attempts, int Group);

    // 手算的每一档:Suffix 是"stage=… to=… "之后的整段;Outcome、Attempts、Group 是最后一行里这一步对应的 first=/second=、attempts、group
    private static readonly StepCase[] Cases =
    {
        new("connect-failed",
            () => Failed(OnlineFailure.ConnectFailed, OnlineStage.ChannelConnect),
            "outcome=ConnectFailed failure=ConnectFailed reached=ChannelConnect loginFailureCode=- characterList=NotReceived characters=- attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "ConnectFailed", 0, 0),
        new("bootstrap-failed",
            () => Failed(OnlineFailure.BootstrapFailed, OnlineStage.Bootstrap),
            "outcome=BootstrapFailed failure=BootstrapFailed reached=Bootstrap loginFailureCode=- characterList=NotReceived characters=- attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "BootstrapFailed", 0, 0),
        new("rejected",
            () => Failed(OnlineFailure.Rejected, OnlineStage.ChannelConnect),
            "outcome=Rejected failure=Rejected reached=ChannelConnect loginFailureCode=- characterList=NotReceived characters=- attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "Rejected", 0, 0),
        new("game-login-rejected",
            () => Failed(OnlineFailure.GameLoginRejected, OnlineStage.GameLogin, loginCode: 9, attempts: 7, group: 17),
            "outcome=GameLoginRejected failure=GameLoginRejected reached=GameLogin loginFailureCode=9 characterList=NotReceived characters=- attempts=7 serverGroup=17 clientVersion=0 elapsedMs=N",
            "GameLoginRejected", 7, 17),
        new("game-login-rejected-with-code-zero",
            () => Failed(OnlineFailure.GameLoginRejected, OnlineStage.GameLogin, loginCode: 0, attempts: 1, group: 0),
            "outcome=GameLoginRejected failure=GameLoginRejected reached=GameLogin loginFailureCode=0 characterList=NotReceived characters=- attempts=1 serverGroup=0 clientVersion=0 elapsedMs=N",
            "GameLoginRejected", 1, 0),
        new("game-login-rejected-with-code-255",
            () => Failed(OnlineFailure.GameLoginRejected, OnlineStage.GameLogin, loginCode: 255, attempts: 2, group: 1),
            "outcome=GameLoginRejected failure=GameLoginRejected reached=GameLogin loginFailureCode=255 characterList=NotReceived characters=- attempts=2 serverGroup=1 clientVersion=0 elapsedMs=N",
            "GameLoginRejected", 2, 1),
        new("game-login-no-reply",
            () => Failed(OnlineFailure.GameLoginNoReply, OnlineStage.GameLogin, attempts: 1, group: 4),
            "outcome=GameLoginNoReply failure=GameLoginNoReply reached=GameLogin loginFailureCode=- characterList=NotReceived characters=- attempts=1 serverGroup=4 clientVersion=0 elapsedMs=N",
            "GameLoginNoReply", 1, 4),
        new("bad-login-request",
            () => Failed(OnlineFailure.BadLoginRequest, OnlineStage.GameLogin),
            "outcome=BadLoginRequest failure=BadLoginRequest reached=GameLogin loginFailureCode=- characterList=NotReceived characters=- attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "BadLoginRequest", 0, 0),
        new("other-failure-no-usable-slot",
            () => Failed(OnlineFailure.NoUsableSlot, OnlineStage.SelectSlot, attempts: 2, group: 1),
            "outcome=OtherFailure failure=NoUsableSlot reached=SelectSlot loginFailureCode=- characterList=NotReceived characters=- attempts=2 serverGroup=1 clientVersion=0 elapsedMs=N",
            "OtherFailure", 2, 1),
        new("other-failure-gateway-login-failed",
            () => Failed(OnlineFailure.GatewayLoginFailed, OnlineStage.GatewayLogin),
            "outcome=OtherFailure failure=GatewayLoginFailed reached=GatewayLogin loginFailureCode=- characterList=NotReceived characters=- attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "OtherFailure", 0, 0),
        new("other-failure-no-game-server-address",
            () => Failed(OnlineFailure.NoGameServerAddress, OnlineStage.GameServerAddress, attempts: 3, group: 14),
            "outcome=OtherFailure failure=NoGameServerAddress reached=GameServerAddress loginFailureCode=- characterList=NotReceived characters=- attempts=3 serverGroup=14 clientVersion=0 elapsedMs=N",
            "OtherFailure", 3, 14),
        new("character-list-not-received",
            () => ConnectedWithoutList(OnlineCharacterListStatus.NotReceived, attempts: 1, group: 3),
            "outcome=CharacterListNotReceived failure=None reached=GameLogin loginFailureCode=- characterList=NotReceived characters=- attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N",
            "CharacterListNotReceived", 1, 3),
        new("character-list-malformed",
            () => ConnectedWithoutList(OnlineCharacterListStatus.Malformed, attempts: 2, group: 5),
            "outcome=CharacterListMalformed failure=None reached=GameLogin loginFailureCode=- characterList=Malformed characters=- attempts=2 serverGroup=5 clientVersion=0 elapsedMs=N",
            "CharacterListMalformed", 2, 5),
    };

    public static IEnumerable<object[]> CaseNames()
    {
        return Cases.Select(c => new object[] { c.Name });
    }

    private static Exception FaultOf(string kind, string message)
    {
        return kind switch
        {
            "io" => new IOException(message),
            "invalid-operation" => new InvalidOperationException(message),
            _ => new InvalidOperationException(message, new TimeoutException(message)),
        };
    }

    // ======== 第一步:连第一个频道(退出码 3) ========

    // 保护:第一步每一档失败的整行——outcome=(结局名)、failure=、reached=、loginFailureCode=(无为 -,0 与 255 照写)、characterList=、characters=-、attempts=、serverGroup=、clientVersion=、elapsedMs=——
    // 逐字出现;最后一行是 result=fail first=<结局名> second=NotRun 并带这一步的 attempts1、group1;退出码 3;没有换频道、没有断开;会话已释放。
    // 推导:第一步没成 → 不会有 stage=switch 与 stage=disconnect 行;输出恰好五行(设置、网关登录 Ok、读目录 Ok、连第一个频道、最后一行)。
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task A_failing_first_channel_prints_its_exact_connect_line_and_the_exact_last_line_and_exits_3(string name)
    {
        StepCase c = Cases.Single(x => x.Name == name);
        var port = new FakePort();
        port.Session.OnConnect = _ => c.Make();

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(OnlineSwitchExitCodes.FirstConnectFailed, exit);
        Assert.Equal(3, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            DirectoryOkLine,
            "PROBE_SWITCH stage=connect to=index:0 " + c.Suffix,
            $"PROBE_SWITCH result=fail first={c.Outcome} second=NotRun attempts1={c.Attempts} attempts2=0 group1={c.Group} group2=0 clientVersion=0 elapsedMs=N",
        }, lines);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
    }

    // 保护:第一步的会话调用抛异常(同步抛与抛成"已出错的任务"一样)时,connect 行是 outcome=Faulted、failure=- reached=- loginFailureCode=- characterList=NotReceived characters=-、
    // 再接 fault=<最内层异常的类型名>,然后才是 attempts=0 serverGroup=0 clientVersion=0 elapsedMs=;最后一行 first=Faulted;退出码 3
    // 推导:没有结果对象 → 失败字段全写 "-";异常只报类型名(GetBaseException)
    [Theory]
    [InlineData("io", false, "IOException")]
    [InlineData("io", true, "IOException")]
    [InlineData("invalid-operation", false, "InvalidOperationException")]
    [InlineData("invalid-operation", true, "InvalidOperationException")]
    [InlineData("wrapped", false, "TimeoutException")]
    [InlineData("wrapped", true, "TimeoutException")]
    public async Task A_first_channel_call_that_throws_prints_one_faulted_connect_line_with_only_the_type_name(string kind, bool asTask, string typeName)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = asTask;
        Exception fault = FaultOf(kind, "SYNTH-EXCEPTION-MESSAGE " + GatewayHost + ":" + FirstPort + " " + Account);
        port.Session.OnConnect = _ => throw fault;

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(3, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            DirectoryOkLine,
            "PROBE_SWITCH stage=connect to=index:0 outcome=Faulted failure=- reached=- loginFailureCode=- characterList=NotReceived characters=- fault=" + typeName
                + " attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH result=fail first=Faulted second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N",
        }, lines);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort }, port.Session.Calls);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:第一步返回 null(没抛异常)时,connect 行仍是 outcome=Faulted 与全 "-" 的失败字段,但没有 fault= 这一段;退出码 3;最后一行 first=Faulted
    // 推导:没有结果对象且没有异常 → 只写固定的 "failure=- reached=- loginFailureCode=- characterList=NotReceived characters=-",不写 fault=
    [Fact]
    public async Task A_first_channel_call_that_returns_nothing_prints_a_faulted_connect_line_without_a_fault_field()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => null;

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(3, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            DirectoryOkLine,
            "PROBE_SWITCH stage=connect to=index:0 outcome=Faulted failure=- reached=- loginFailureCode=- characterList=NotReceived characters=- attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH result=fail first=Faulted second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N",
        }, lines);
    }

    // ======== 第二步:换频道(退出码 4) ========

    // 保护:第二步每一档失败的整行(stage=switch,字段与第一步同形),第一步的 connect 行是正常的 Ok 行,
    // 最后一行是 result=fail first=Ok second=<结局名> attempts1=1 attempts2=<这一步的次数> group1=3 group2=<这一步的组>;退出码 4;换频道只调用一次、没有断开;会话已释放。
    // 推导:标准目录候选 0 号 = 乙主机乙端口 → 换频道的调用记录是 switch-host:<乙主机>:<乙端口>;第二步没成 → 没有 stage=disconnect 行。
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task A_failing_switch_prints_its_exact_switch_line_and_the_exact_last_line_and_exits_4(string name)
    {
        StepCase c = Cases.Single(x => x.Name == name);
        var port = new FakePort();
        port.Session.OnSwitch = (_, _) => c.Make();

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(OnlineSwitchExitCodes.SwitchFailed, exit);
        Assert.Equal(4, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            DirectoryOkLine,
            ConnectOkLine,
            "PROBE_SWITCH stage=switch to=index:0 " + c.Suffix,
            $"PROBE_SWITCH result=fail first=Ok second={c.Outcome} attempts1=1 attempts2={c.Attempts} group1=3 group2={c.Group} clientVersion=0 elapsedMs=N",
        }, lines);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort, "switch-host:" + HostB + ":" + PortB }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
    }

    // 保护:第二步的会话调用抛异常(同步抛与抛成"已出错的任务"一样)时,switch 行是 outcome=Faulted、全 "-" 的失败字段、fault=<最内层异常的类型名>、attempts=0 serverGroup=0 …;
    // 最后一行 first=Ok second=Faulted attempts1=1 attempts2=0 group1=3 group2=0;退出码 4
    [Theory]
    [InlineData("io", false, "IOException")]
    [InlineData("io", true, "IOException")]
    [InlineData("invalid-operation", false, "InvalidOperationException")]
    [InlineData("invalid-operation", true, "InvalidOperationException")]
    [InlineData("wrapped", false, "TimeoutException")]
    [InlineData("wrapped", true, "TimeoutException")]
    public async Task A_switch_call_that_throws_prints_one_faulted_switch_line_with_only_the_type_name(string kind, bool asTask, string typeName)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = asTask;
        Exception fault = FaultOf(kind, "SYNTH-EXCEPTION-MESSAGE " + GatewayHost + ":" + PortB + " " + Account);
        port.Session.OnSwitch = (_, _) => throw fault;

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(4, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            DirectoryOkLine,
            ConnectOkLine,
            "PROBE_SWITCH stage=switch to=index:0 outcome=Faulted failure=- reached=- loginFailureCode=- characterList=NotReceived characters=- fault=" + typeName
                + " attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH result=fail first=Ok second=Faulted attempts1=1 attempts2=0 group1=3 group2=0 clientVersion=0 elapsedMs=N",
        }, lines);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:第二步返回 null(没抛异常)时,switch 行仍是 outcome=Faulted 与全 "-" 的失败字段,但没有 fault= 这一段;退出码 4
    [Fact]
    public async Task A_switch_call_that_returns_nothing_prints_a_faulted_switch_line_without_a_fault_field()
    {
        var port = new FakePort();
        port.Session.OnSwitch = (_, _) => null;

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(4, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            DirectoryOkLine,
            ConnectOkLine,
            "PROBE_SWITCH stage=switch to=index:0 outcome=Faulted failure=- reached=- loginFailureCode=- characterList=NotReceived characters=- attempts=0 serverGroup=0 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH result=fail first=Ok second=Faulted attempts1=1 attempts2=0 group1=3 group2=0 clientVersion=0 elapsedMs=N",
        }, lines);
    }

    // ======== 跟着命令行走的记号 ========

    // 保护:按端口选目标 + 两个开关时,第一步失败的各行里 to=port、clientVersion=4000000000 随命令行(步骤行里的 serverGroup= 是这一步实际写出的组 17,
    // 不是设置里的 5);设置行里的 serverGroup=5 是命令行给的;最后一行的 clientVersion 也是 4000000000
    // 推导:--to-port 47399 --client-version 4000000000 --server-group 5 → 设置行 clientVersion=4000000000 serverGroup=5;第一步结果的有效组 17、重试后共 7 次
    [Fact]
    public async Task The_first_step_lines_follow_the_port_target_and_the_two_knobs()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Failed(OnlineFailure.GameLoginRejected, OnlineStage.GameLogin, loginCode: 9, attempts: 7, group: 17);

        (int exit, string[] lines) = await Run(port, "--to-port " + TargetPort + " --client-version 4000000000 --server-group 5");

        Assert.Equal(3, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=port outcome=Ok clientVersion=4000000000 serverGroup=5",
            "PROBE_SWITCH stage=gateway-login to=port outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=port outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=connect to=port outcome=GameLoginRejected failure=GameLoginRejected reached=GameLogin loginFailureCode=9 characterList=NotReceived characters=- attempts=7 serverGroup=17 clientVersion=4000000000 elapsedMs=N",
            "PROBE_SWITCH result=fail first=GameLoginRejected second=NotRun attempts1=7 attempts2=0 group1=17 group2=0 clientVersion=4000000000 elapsedMs=N",
        }, lines);
    }

    // 保护:同样的命令行下第二步失败时,换频道走"同主机指定端口"的调用(switch-port:<端口>),各行里的 to=port 与客户端版本号随命令行
    [Fact]
    public async Task The_second_step_lines_follow_the_port_target_and_the_two_knobs()
    {
        var port = new FakePort();
        port.Session.OnSwitch = (_, _) => Failed(OnlineFailure.GameLoginNoReply, OnlineStage.GameLogin, attempts: 7, group: 17);

        (int exit, string[] lines) = await Run(port, "--to-port " + TargetPort + " --client-version 4000000000 --server-group 5");

        Assert.Equal(4, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=port outcome=Ok clientVersion=4000000000 serverGroup=5",
            "PROBE_SWITCH stage=gateway-login to=port outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=port outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=connect to=port outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=3 attempts=1 serverGroup=3 clientVersion=4000000000 elapsedMs=N",
            "PROBE_SWITCH stage=switch to=port outcome=GameLoginNoReply failure=GameLoginNoReply reached=GameLogin loginFailureCode=- characterList=NotReceived characters=- attempts=7 serverGroup=17 clientVersion=4000000000 elapsedMs=N",
            "PROBE_SWITCH result=fail first=Ok second=GameLoginNoReply attempts1=1 attempts2=7 group1=3 group2=17 clientVersion=4000000000 elapsedMs=N",
        }, lines);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort, "switch-port:" + TargetPort }, port.Session.Calls);
    }

    // ======== 角色列表条数的写法 ========

    // 保护:状态是"已收到"但角色列表对象为空(防御情形)时,这一步算成功,connect 行写 characterList=Received characters=-(不是 0,更不会因空引用崩溃),流程继续换频道
    // 推导:成功 + Received → 结局 Ok;列表对象为 null → 条数写 "-"
    [Fact]
    public async Task A_received_status_without_a_list_object_is_still_a_success_and_prints_a_dash_for_the_count()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => new OnlineConnectResult(OnlineStage.GameLogin, OnlineFailure.None, null, null, null, OnlineCharacterListStatus.Received, null, 1, 3);

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(0, exit);
        Assert.Equal(
            "PROBE_SWITCH stage=connect to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=- attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N",
            lines[3]);
        Assert.Contains("switch-host:" + HostB + ":" + PortB, port.Session.Calls);
    }

    // 保护:成功的两步各自写自己的角色条数、游戏登录次数与服务器组,换频道那一步的结果不会串到第一步那一行里
    // 推导:第一步 3 个角色/1 次/组 3;第二步 5 个角色/2 次/组 9(两步各不相同)
    [Fact]
    public async Task Each_successful_step_line_carries_its_own_character_count_attempts_and_group()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Connected(3, attempts: 1, group: 3);
        port.Session.OnSwitch = (_, _) => Connected(5, attempts: 2, group: 9);

        (int exit, string[] lines) = await Run(port, Cmd);

        Assert.Equal(0, exit);
        Assert.Equal(
            "PROBE_SWITCH stage=connect to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=3 attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N",
            lines[3]);
        Assert.Equal(
            "PROBE_SWITCH stage=switch to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=5 attempts=2 serverGroup=9 clientVersion=0 elapsedMs=N",
            lines[4]);
    }
}
