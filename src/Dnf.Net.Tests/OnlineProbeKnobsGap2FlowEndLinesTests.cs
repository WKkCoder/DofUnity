using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineProbeKnobsGap2Support;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 的"流程走完或走不下去"几种结局的整行文本(耗时归一成 N 之后逐字比):
/// 全部成功的整份输出(按序号选与按端口选各一份,数值互不相同)、"选不出目标"(<c>stage=target … outcome=NoTarget</c>,退出码 7)、
/// 断开阶段的 Ok 与 Faulted(断开出错不改变两步都成功的结论,最后一行仍是 <c>result=ok</c>),以及命令行不合规时每一种问题的用法行(退出码 64)。
/// 假端口、假会话;不连网、不读本机配置。
/// </summary>
public sealed class OnlineProbeKnobsGap2FlowEndLinesTests
{
    private const string PlainSyntax = "dnf-netprobe switch [仓库根] --to-index <K> | --to-port <N> [--client-version <u32>] [--server-group <0..17>]";
    private const string ListSyntax = "dnf-netprobe switch [仓库根] --list";

    // ======== 全部成功 ========

    // 保护:按序号选目标、全部成功时的整份输出逐字:七行(设置、网关登录、读目录、连第一个频道、换频道、断开、最后一行),
    // 数值取互不相同的:客户端版本 4294967295、服务器组 17、第一步 3 个角色/1 次/组 3、第二步 2 个角色/7 次/组 17;退出码 0,最后一行是 result=ok
    // 推导:候选 2 号 = 丙主机丁端口(标准目录:候选 0 = 乙主机乙端口,1 = 丙主机丙端口,2 = 丙主机丁端口)→ 换频道的调用记录是 switch-host:<丙主机>:<丁端口>
    [Fact]
    public async Task A_good_run_to_an_index_prints_exactly_these_seven_lines()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Connected(3, attempts: 1, group: 3);
        port.Session.OnSwitch = (_, _) => Connected(2, attempts: 7, group: 17);

        (int exit, string[] lines) = await Run(port, "--to-index 2 --client-version 4294967295 --server-group 17");

        Assert.Equal(OnlineSwitchExitCodes.Success, exit);
        Assert.Equal(0, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=index:2 outcome=Ok clientVersion=4294967295 serverGroup=17",
            "PROBE_SWITCH stage=gateway-login to=index:2 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=index:2 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=connect to=index:2 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=3 attempts=1 serverGroup=3 clientVersion=4294967295 elapsedMs=N",
            "PROBE_SWITCH stage=switch to=index:2 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=2 attempts=7 serverGroup=17 clientVersion=4294967295 elapsedMs=N",
            "PROBE_SWITCH stage=disconnect to=index:2 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=7 group1=3 group2=17 clientVersion=4294967295 elapsedMs=N",
        }, lines);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort, "switch-host:" + HostC + ":" + PortD, "disconnect" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
    }

    // 保护:按端口选目标、全部成功(两步都是 0 个角色的空列表)时的整份输出逐字:to=port、characters=0、两步各自的次数 2 与 3、组都是 0;最后一行 result=ok
    // 推导:空列表也算收到;换频道走"同主机指定端口"的调用 switch-port:<端口>
    [Fact]
    public async Task A_good_run_to_a_port_with_empty_character_lists_prints_exactly_these_seven_lines()
    {
        var port = new FakePort();
        port.Session.OnConnect = _ => Connected(0, attempts: 2, group: 0);
        port.Session.OnSwitch = (_, _) => Connected(0, attempts: 3, group: 0);

        (int exit, string[] lines) = await Run(port, "--to-port " + TargetPort);

        Assert.Equal(0, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=port outcome=Ok clientVersion=0 serverGroup=0",
            "PROBE_SWITCH stage=gateway-login to=port outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=port outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=connect to=port outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=0 attempts=2 serverGroup=0 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH stage=switch to=port outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=0 attempts=3 serverGroup=0 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH stage=disconnect to=port outcome=Ok elapsedMs=N",
            "PROBE_SWITCH result=ok first=Ok second=Ok attempts1=2 attempts2=3 group1=0 group2=0 clientVersion=0 elapsedMs=N",
        }, lines);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort, "switch-port:" + TargetPort, "disconnect" }, port.Session.Calls);
    }

    // ======== 选不出目标 ========

    // 保护:按序号选目标而候选不够时(标准目录有 3 个候选;空目录没有候选),整份输出是五行:设置、网关登录 Ok、读目录 Ok、
    // "stage=target to=index:K outcome=NoTarget"(没有 elapsedMs)、最后一行(两步都没执行);退出码 7;只登录与读目录,一个频道也没连
    // 推导:候选个数 3,K 取 3、9、999999999 都选不出;空目录 K=0 也选不出
    [Theory]
    [InlineData(3, false, "PROBE_SWITCH stage=target to=index:3 outcome=NoTarget")]
    [InlineData(9, false, "PROBE_SWITCH stage=target to=index:9 outcome=NoTarget")]
    [InlineData(999999999, false, "PROBE_SWITCH stage=target to=index:999999999 outcome=NoTarget")]
    [InlineData(0, true, "PROBE_SWITCH stage=target to=index:0 outcome=NoTarget")]
    public async Task A_target_that_cannot_be_picked_prints_the_target_line_and_exits_7(int index, bool emptyDirectory, string expectedTargetLine)
    {
        var port = new FakePort();
        if (emptyDirectory)
        {
            port.Session.OnDirectory = () => Directory();
        }

        (int exit, string[] lines) = await Run(port, "--to-index " + index);

        Assert.Equal(OnlineSwitchExitCodes.NoTarget, exit);
        Assert.Equal(7, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=index:" + index + " outcome=Ok clientVersion=0 serverGroup=0",
            "PROBE_SWITCH stage=gateway-login to=index:" + index + " outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=index:" + index + " outcome=Ok elapsedMs=N",
            expectedTargetLine,
            "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N",
        }, lines);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
    }

    // ======== 断开阶段 ========

    // 保护:断开阶段抛异常(同步抛与抛成"已出错的任务"一样)时,这一行是 outcome=Faulted fault=<最内层异常的类型名> elapsedMs=…,
    // 只有类型名;断开出错不改变"两步都成功"的结论:退出码仍是 0,最后一行是 result=ok
    // 推导:假会话默认第一步 3 个角色/1 次/组 3,第二步 2 个角色/1 次/组 3
    [Theory]
    [InlineData("io", false, "IOException")]
    [InlineData("io", true, "IOException")]
    [InlineData("invalid-operation", false, "InvalidOperationException")]
    [InlineData("invalid-operation", true, "InvalidOperationException")]
    [InlineData("wrapped", false, "TimeoutException")]
    [InlineData("wrapped", true, "TimeoutException")]
    public async Task A_disconnect_that_throws_prints_a_faulted_line_but_the_result_is_still_ok(string kind, bool asTask, string typeName)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = asTask;
        string message = "SYNTH-DISCONNECT-MESSAGE " + GatewayHost + ":" + FirstPort + " " + Account;
        Exception fault = kind switch
        {
            "io" => new IOException(message),
            "invalid-operation" => new InvalidOperationException(message),
            _ => new InvalidOperationException(message, new TimeoutException(message)),
        };
        port.Session.OnDisconnect = () => throw fault;

        (int exit, string[] lines) = await Run(port, "--to-index 0");

        Assert.Equal(0, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=0 serverGroup=0",
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=index:0 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=connect to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=3 attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH stage=switch to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=2 attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N",
            "PROBE_SWITCH stage=disconnect to=index:0 outcome=Faulted fault=" + typeName + " elapsedMs=N",
            "PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=1 group1=3 group2=3 clientVersion=0 elapsedMs=N",
        }, lines);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
        Assert.True(port.Session.Disposed);
    }

    // ======== 命令行不合规的用法行 ========

    // 保护:命令行不合规时只打一行 "PROBE_SWITCH usage problem=<问题枚举名> exit=64 syntax: <用法>",退出码 64;
    // 命令行里没有 --list 记号时用法只有换频道一种写法;出现 --list 记号时后面接 " | " 与目录摘要的写法;
    // 每一种问题(缺目标、目标冲突、各取值非法、重复选项、不认识的参数、--list 与别的选项冲突)各一行;端口一次都没被碰
    // 推导:第一类 8 种问题(无 --list)用法只有 PlainSyntax;第二类 3 种(有 --list)用法是 PlainSyntax + " | " + ListSyntax
    [Theory]
    [InlineData("", "MissingTarget", false)]
    [InlineData("--to-index 0 --to-port 5", "ConflictingTargets", false)]
    [InlineData("--to-index 0 --client-version abc", "BadClientVersion", false)]
    [InlineData("--to-index 0 --server-group 18", "BadServerGroup", false)]
    [InlineData("--to-index -1", "BadToIndex", false)]
    [InlineData("--to-port 0", "BadToPort", false)]
    [InlineData("--to-index 0 --to-index 1", "DuplicateOption", false)]
    [InlineData("--to-index 0 --bogus", "UnknownArgument", false)]
    [InlineData("--list --to-index 0", "ConflictingList", true)]
    [InlineData("--list --bogus", "UnknownArgument", true)]
    [InlineData("--list --list", "DuplicateOption", true)]
    public async Task Every_kind_of_bad_command_line_prints_one_exact_usage_line_and_exits_64(string commandLine, string problem, bool mentionsList)
    {
        var port = new FailIfCalledPort();

        (int exit, string[] lines) = await Run(port, commandLine);

        Assert.Equal(OnlineSwitchExitCodes.Usage, exit);
        Assert.Equal(64, exit);
        string syntax = mentionsList ? PlainSyntax + " | " + ListSyntax : PlainSyntax;
        Assert.Equal(new[] { "PROBE_SWITCH usage problem=" + problem + " exit=64 syntax: " + syntax }, lines);
        Assert.Equal(0, port.Calls);
    }
}
