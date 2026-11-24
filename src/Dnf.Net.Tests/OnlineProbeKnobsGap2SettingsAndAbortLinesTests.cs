using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineProbeKnobsGap2Support;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 的"装配阶段"(<c>stage=settings</c>)失败的整行文本,以及两种"中途中止"的 <c>stage=abort</c> 行与随后的最后一行(耗时归一成 N 之后逐字比):
/// 读设置返回假、读设置或建会话抛异常(只报最内层异常的类型名)、建会话返回空;
/// 取消(<c>OperationCanceledException</c> 与它的子类,发生在装配、网关登录、读目录、连第一个频道、换频道、断开六处各一份,退出码 15);
/// 流程自己没料到的异常(读会话属性抛,只报类型名,退出码 70)。假端口、假会话;不连网、不读本机配置。
/// </summary>
public sealed class OnlineProbeKnobsGap2SettingsAndAbortLinesTests
{
    private const string SettingsOk = "PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=0 serverGroup=0";
    private const string GatewayOk = "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Ok elapsedMs=N";
    private const string DirectoryOk = "PROBE_SWITCH stage=directory to=index:0 outcome=Ok elapsedMs=N";
    private const string ConnectOk = "PROBE_SWITCH stage=connect to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=3 attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N";
    private const string SwitchOk = "PROBE_SWITCH stage=switch to=index:0 outcome=Ok failure=None reached=GameLogin loginFailureCode=- characterList=Received characters=2 attempts=1 serverGroup=3 clientVersion=0 elapsedMs=N";
    private const string FinalNothingRan = "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N";

    /// <summary>读设置与建会话的行为都可配置的假端口(可抛任意异常、可返回空会话);会话是共用件里的假会话。</summary>
    private sealed class ConfigurablePort : IOnlineSwitchPort
    {
        internal bool LoadReturns { get; set; } = true;
        internal Exception LoadThrows { get; set; }
        internal Exception CreateThrows { get; set; }
        internal bool CreateReturnsNull { get; set; }
        internal FakeSession Session { get; } = new();
        internal int CreateCalls { get; private set; }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            if (LoadThrows != null)
            {
                throw LoadThrows;
            }
            settings = LoadReturns ? OnlineSwitchTestKit.Settings() : null;
            problem = LoadReturns ? null : "SYNTH-PROBLEM-DETAIL " + Account + " " + GatewayHost;
            return LoadReturns;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            CreateCalls++;
            if (CreateThrows != null)
            {
                throw CreateThrows;
            }
            if (CreateReturnsNull)
            {
                return null;
            }
            Session.ClientVersion = settings.ClientVersion;
            return Session;
        }
    }

    /// <summary>读 <see cref="GameServerHost"/> 或 <see cref="ClientVersion"/> 时可以抛异常的假会话:模拟流程自己出了没料到的错(不是会话调用的失败)。</summary>
    private sealed class ThrowingSession : IOnlineSwitchSession
    {
        internal Exception HostThrows { get; set; }
        internal Exception VersionThrows { get; set; }
        internal bool Disposed { get; private set; }

        public uint ClientVersion => VersionThrows != null ? throw VersionThrows : 0u;
        public string GameServerHost => HostThrows != null ? throw HostThrows : GatewayHost;

        public Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken) => Task.FromResult(LoginOk());
        public Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(StandardDirectory());
        public Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private sealed class SinglePort : IOnlineSwitchPort
    {
        private readonly IOnlineSwitchSession _session;

        internal SinglePort(IOnlineSwitchSession session)
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

    // ======== 装配阶段失败 ========

    // 保护:读设置返回假(不带异常)时,设置行只有 "outcome=SettingsFailed"(没有 fault=、没有版本与组);最后一行里的客户端版本号取命令行给的值(会话没建出来),其余全是未执行;退出码 2;
    // 失败原因文字(合成的账号与主机)一个字也不出现
    // 推导:--to-port 47399 --client-version 7 → to=port;会话为空 → 最后一行 clientVersion=7
    [Fact]
    public async Task A_loader_that_returns_false_prints_the_bare_settings_failed_line_and_exits_2()
    {
        var port = new ConfigurablePort { LoadReturns = false };

        (int exit, string[] lines) = await Run(port, "--to-port " + TargetPort + " --client-version 7");

        Assert.Equal(OnlineSwitchExitCodes.SettingsFailed, exit);
        Assert.Equal(2, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=port outcome=SettingsFailed",
            "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=7 elapsedMs=N",
        }, lines);
        Assert.Equal(0, port.CreateCalls);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:读设置或建会话抛异常时,设置行是 "outcome=SettingsFailed fault=<最内层异常的类型名>"(只有类型名,没有消息),退出码 2,最后一行是未执行的失败行
    // 推导:取 GetBaseException():InvalidOperationException 本身 → InvalidOperationException;外层 InvalidOperationException 带内层 TimeoutException → TimeoutException
    [Theory]
    [InlineData("load", "invalid-operation", "PROBE_SWITCH stage=settings to=port outcome=SettingsFailed fault=InvalidOperationException")]
    [InlineData("load", "wrapped", "PROBE_SWITCH stage=settings to=port outcome=SettingsFailed fault=TimeoutException")]
    [InlineData("create", "invalid-operation", "PROBE_SWITCH stage=settings to=port outcome=SettingsFailed fault=InvalidOperationException")]
    [InlineData("create", "wrapped", "PROBE_SWITCH stage=settings to=port outcome=SettingsFailed fault=TimeoutException")]
    public async Task A_loader_or_session_factory_that_throws_prints_the_settings_failed_line_with_only_the_type_name(string where, string kind, string expectedSettingsLine)
    {
        string message = "SYNTH-EXCEPTION-MESSAGE " + GatewayHost + ":" + FirstPort + " " + Account;
        Exception fault = kind == "wrapped" ? new InvalidOperationException(message, new TimeoutException(message)) : new InvalidOperationException(message);
        var port = new ConfigurablePort();
        if (where == "load")
        {
            port.LoadThrows = fault;
        }
        else
        {
            port.CreateThrows = fault;
        }

        (int exit, string[] lines) = await Run(port, "--to-port " + TargetPort);

        Assert.Equal(2, exit);
        Assert.Equal(new[] { expectedSettingsLine, FinalNothingRan }, lines);
        Assert.Empty(port.Session.Calls);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:建会话返回空(没抛异常)时同样是装配失败:设置行是不带 fault= 的 "outcome=SettingsFailed",退出码 2
    [Fact]
    public async Task A_session_factory_that_returns_nothing_is_a_settings_failure_without_a_fault_field()
    {
        var port = new ConfigurablePort { CreateReturnsNull = true };

        (int exit, string[] lines) = await Run(port, "--to-index 4");

        Assert.Equal(2, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=index:4 outcome=SettingsFailed",
            FinalNothingRan,
        }, lines);
        Assert.Equal(1, port.CreateCalls);
    }

    // ======== 取消 ========

    // 保护:取消(OperationCanceledException 与它的子类 TaskCanceledException;同步抛与抛成"已出错的任务"一样)发生在六处中的任何一处,
    // 都不会被收成 Faulted 结局,而是打 "PROBE_SWITCH stage=abort outcome=Cancelled" 再打最后一行(此刻已完成的步骤如实写进最后一行),退出码 15;会话已释放。
    // 推导(默认假会话:第一步 3 个角色/1 次/组 3,第二步 2 个角色/1 次/组 3):
    //   装配/网关登录/读目录/连第一个频道处取消 → 两步都没记录;换频道处取消 → first=Ok(1 次、组 3)、second=NotRun;断开处取消 → 两步都 Ok。
    [Theory]
    [InlineData("settings", "canceled", false)]
    [InlineData("login", "canceled", false)]
    [InlineData("login", "task-canceled", true)]
    [InlineData("directory", "canceled", false)]
    [InlineData("directory", "task-canceled", true)]
    [InlineData("connect", "canceled", false)]
    [InlineData("connect", "task-canceled", true)]
    [InlineData("switch", "canceled", false)]
    [InlineData("switch", "task-canceled", true)]
    [InlineData("disconnect", "canceled", false)]
    [InlineData("disconnect", "task-canceled", true)]
    public async Task A_cancellation_anywhere_prints_the_abort_line_and_the_last_line_and_exits_15(string where, string kind, bool asTask)
    {
        Exception cancel = kind == "task-canceled" ? new TaskCanceledException() : new OperationCanceledException();
        var port = new ConfigurablePort();
        port.Session.FaultAsTask = asTask;
        switch (where)
        {
            case "settings": port.LoadThrows = cancel; break;
            case "login": port.Session.OnLogin = () => throw cancel; break;
            case "directory": port.Session.OnDirectory = () => throw cancel; break;
            case "connect": port.Session.OnConnect = _ => throw cancel; break;
            case "switch": port.Session.OnSwitch = (_, _) => throw cancel; break;
            default: port.Session.OnDisconnect = () => throw cancel; break;
        }
        const string abort = "PROBE_SWITCH stage=abort outcome=Cancelled";
        string[] expected = where switch
        {
            "settings" => new[] { abort, FinalNothingRan },
            "login" => new[] { SettingsOk, abort, FinalNothingRan },
            "directory" => new[] { SettingsOk, GatewayOk, abort, FinalNothingRan },
            "connect" => new[] { SettingsOk, GatewayOk, DirectoryOk, abort, FinalNothingRan },
            "switch" => new[]
            {
                SettingsOk, GatewayOk, DirectoryOk, ConnectOk, abort,
                "PROBE_SWITCH result=fail first=Ok second=NotRun attempts1=1 attempts2=0 group1=3 group2=0 clientVersion=0 elapsedMs=N",
            },
            _ => new[]
            {
                SettingsOk, GatewayOk, DirectoryOk, ConnectOk, SwitchOk, abort,
                "PROBE_SWITCH result=fail first=Ok second=Ok attempts1=1 attempts2=1 group1=3 group2=3 clientVersion=0 elapsedMs=N",
            },
        };

        (int exit, string[] lines) = await Run(port, "--to-index 0");

        Assert.Equal(OnlineSwitchExitCodes.Cancelled, exit);
        Assert.Equal(15, exit);
        Assert.Equal(expected, lines);
        Assert.Equal(where != "settings", port.Session.Disposed);          // 装配处取消时会话还没建出来
    }

    // ======== 流程自己没料到的异常 ========

    // 保护:流程自己出了没料到的错(选目标时读会话的主机属性抛异常),打 "PROBE_SWITCH stage=abort outcome=Unexpected fault=<最内层异常的类型名>" 再打最后一行,
    // 退出码 70;只有类型名,没有消息;会话已释放;前面已完成的阶段行照常保留
    // 推导:读目录成功之后才会读主机属性 → 输出是设置、网关登录 Ok、读目录 Ok、中止行、最后一行
    [Theory]
    [InlineData("invalid-operation", "InvalidOperationException")]
    [InlineData("wrapped", "TimeoutException")]
    public async Task A_bug_while_picking_the_target_prints_the_unexpected_abort_line_and_exits_70(string kind, string typeName)
    {
        string message = "SYNTH-HOST-READ " + GatewayHost + " " + Account;
        var session = new ThrowingSession
        {
            HostThrows = kind == "wrapped" ? new InvalidOperationException(message, new TimeoutException(message)) : new InvalidOperationException(message),
        };

        (int exit, string[] lines) = await Run(new SinglePort(session), "--to-index 0");

        Assert.Equal(OnlineSwitchExitCodes.Unexpected, exit);
        Assert.Equal(70, exit);
        Assert.Equal(new[]
        {
            SettingsOk,
            GatewayOk,
            DirectoryOk,
            "PROBE_SWITCH stage=abort outcome=Unexpected fault=" + typeName,
            FinalNothingRan,
        }, lines);
        Assert.True(session.Disposed);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:没料到的异常发生在刚建好会话、读客户端版本号时,输出只有中止行与最后一行(设置行还没打),最后一行的客户端版本号取命令行给的值(7);退出码 70;会话已释放
    // 推导:--client-version 7 → 流程自己的客户端版本号初值 7;读会话版本号抛异常,赋值没完成 → 仍是 7
    [Fact]
    public async Task A_bug_right_after_the_session_is_created_prints_only_the_abort_line_and_the_last_line()
    {
        var session = new ThrowingSession { VersionThrows = new NotSupportedException("SYNTH-VERSION-READ " + Account) };

        (int exit, string[] lines) = await Run(new SinglePort(session), "--to-index 0 --client-version 7");

        Assert.Equal(70, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=abort outcome=Unexpected fault=NotSupportedException",
            "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=7 elapsedMs=N",
        }, lines);
        Assert.True(session.Disposed);
    }
}
