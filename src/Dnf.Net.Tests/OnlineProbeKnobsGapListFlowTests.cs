using System.Text.RegularExpressions;
using Dnf.Net.ChannelDirectory;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch [仓库根] --list</c>(只读目录摘要)的流程:读本地设置 → 网关登录(只一次) → 读频道目录 → 逐条目打摘要 → 结束并释放会话。
/// 在假会话/假端口上逐行断言成功路径的全部输出,断言"从不连频道、不换频道、不断开、只登录一次、目录只读一次",
/// 各步失败对应的退出码(设置 2、网关 5、目录 6、取消 15、没料到的异常 70)与最后一行的失败阶段;
/// 另在真会话(内存假网关,不绑端口、不连任何服务)上证明整个流程零频道连接、网关只被问一次。
/// 全部合成数据,没有睡眠;耗时毫秒是真实时钟,逐行比较前统一换成 N。
/// </summary>
public sealed class OnlineProbeKnobsGapListFlowTests
{
    private static readonly string GatewayIp = GatewayTestSupport.Dots(198, 51, 100, 7);
    private static readonly string OtherIp = GatewayTestSupport.Dots(198, 51, 100, 8);

    private static string[] Split(string line)
    {
        return line.Length == 0 ? Array.Empty<string>() : line.Split(' ');
    }

    private static async Task<(int Exit, List<string> Lines)> Run(IOnlineSwitchPort port, string line = "--list", CancellationToken token = default)
    {
        var lines = new List<string>();
        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(Split(line), DefaultRoot, port, lines.Add, token), 10);
        return (exit, lines);
    }

    /// <summary>耗时是真实时钟:每行末尾的 elapsedMs=数字 换成 N 再比。</summary>
    private static string[] Normalized(IEnumerable<string> lines)
    {
        return lines.Select(l => Regex.Replace(l, @"elapsedMs=\d+$", "elapsedMs=N")).ToArray();
    }

    // ---- 严格的假会话:连频道/换频道/断开一律记账并抛异常(list 模式绝不该碰),并记下登录与读目录收到的取消令牌 ----
    private sealed class StrictSession : IOnlineSwitchSession
    {
        internal List<string> Calls { get; } = new();
        internal bool Disposed { get; private set; }
        internal CancellationToken LoginToken { get; private set; }
        internal CancellationToken DirectoryToken { get; private set; }
        internal Func<string> Host { get; set; } = () => GatewayHost;
        internal Func<CancellationToken, OnlineLoginResult> OnLogin { get; set; } = _ => LoginOk();
        internal Func<CancellationToken, ChannelDirectoryLoadResult> OnDirectory { get; set; } = _ => StandardDirectory();

        public uint ClientVersion => 0;

        public string GameServerHost
        {
            get
            {
                Calls.Add("host");
                return Host();
            }
        }

        public Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken)
        {
            Calls.Add("login");
            LoginToken = cancellationToken;
            return Task.FromResult(OnLogin(cancellationToken));
        }

        public Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(CancellationToken cancellationToken)
        {
            Calls.Add("directory");
            DirectoryToken = cancellationToken;
            return Task.FromResult(OnDirectory(cancellationToken));
        }

        public Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken)
        {
            Calls.Add("connect:" + channelPort);
            throw new NotSupportedException("SYNTH: list mode must never connect a channel");
        }

        public Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken)
        {
            Calls.Add("switch-port:" + channelPort);
            throw new NotSupportedException("SYNTH: list mode must never switch a channel");
        }

        public Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken)
        {
            Calls.Add("switch-host:" + host + ":" + channelPort);
            throw new NotSupportedException("SYNTH: list mode must never switch a channel");
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Calls.Add("disconnect");
            throw new NotSupportedException("SYNTH: list mode has no connection to disconnect");
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class StrictPort : IOnlineSwitchPort
    {
        internal StrictPort(StrictSession session)
        {
            Session = session;
        }

        internal StrictSession Session { get; }
        internal OnlineEntrySettings Settings { get; set; } = OnlineSwitchTestKit.Settings();
        internal Exception LoadThrows { get; set; }
        internal bool LoadReturnsNullSettings { get; set; }
        internal int LoadCalls { get; private set; }
        internal int CreateCalls { get; private set; }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            LoadCalls++;
            if (LoadThrows != null)
            {
                throw LoadThrows;
            }
            settings = LoadReturnsNullSettings ? null : Settings;
            problem = null;
            return true;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            CreateCalls++;
            return Session;
        }
    }

    // ======== 一、成功路径(假会话) ========

    // 保护:成功路径输出的每一行、每个字段与顺序。标准目录 = 一个组 5 个条目:0 号与第一个频道同主机同端口(first=1);
    // 2 号端口为 0(usable=0);候选依次是 1、3、4 号(candidate=0,1,2);组标签是合成的、解不出服务器组(group=0);默认脚本没有槽位规则(kind=-1)
    [Fact]
    public async Task A_good_list_run_prints_exactly_these_lines_up_to_the_elapsed_times()
    {
        var port = new FakePort();

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(0, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=Ok",
            "PROBE_SWITCH stage=gateway-login to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH dir entry=0 usable=1 first=1 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir entry=1 usable=1 first=0 candidate=0 group=0 kind=-1",
            "PROBE_SWITCH dir entry=2 usable=0 first=0 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir entry=3 usable=1 first=0 candidate=1 group=0 kind=-1",
            "PROBE_SWITCH dir entry=4 usable=1 first=0 candidate=2 group=0 kind=-1",
            "PROBE_SWITCH dir-summary entries=5 usable=4 candidates=3 firstInDirectory=1",
            "PROBE_SWITCH result=ok mode=list elapsedMs=N",
        }, Normalized(lines));
    }

    // 保护:成功路径只有"登录一次、读目录一次",没有任何连接/换频道/断开;会话被释放;登录用的是设置里的账号与口令
    [Fact]
    public async Task A_good_list_run_logs_in_once_reads_the_directory_once_and_never_connects()
    {
        var port = new FakePort();

        (int exit, _) = await Run(port);

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
        Assert.Equal(1, port.Session.Count("login"));
        Assert.Equal(1, port.Session.Count("directory"));
        Assert.DoesNotContain(port.Session.Calls, c => c.StartsWith("connect") || c.StartsWith("switch") || c == "disconnect");
        Assert.True(port.Session.Disposed);
        Assert.Equal(1, port.LoadCalls);
        Assert.Equal(1, port.CreateCalls);
        Assert.Equal(Account, port.Session.LoginAccount);
        Assert.Equal(Password, port.Session.LoginPassword);
    }

    // 保护:严格会话(连频道/换频道/断开一碰就记账并抛异常)上走完全程:只读了"登录、读目录、读一次游戏服主机",没有任何被禁调用;
    // 并且调用方给的取消令牌原样传进了登录与读目录
    [Fact]
    public async Task A_strict_session_sees_login_directory_and_the_host_only_and_the_callers_token_is_passed_on()
    {
        using var cts = new CancellationTokenSource();
        var session = new StrictSession();
        var port = new StrictPort(session);

        (int exit, _) = await Run(port, "--list", cts.Token);

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "login", "directory", "host" }, session.Calls);
        Assert.Equal(cts.Token, session.LoginToken);
        Assert.Equal(cts.Token, session.DirectoryToken);
        Assert.NotEqual(CancellationToken.None, session.LoginToken);
        Assert.True(session.Disposed);
    }

    // 保护:list 模式不改设置——设置里的客户端版本与服务器组原样保留(没有两个开关可用),输出里也没有这两个字段
    [Fact]
    public async Task The_settings_are_used_as_loaded_and_no_knob_is_applied_or_printed()
    {
        var port = new FakePort { Settings = Settings(clientVersion: 7, serverGroup: 9) };

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(0, exit);
        Assert.Same(port.Settings, port.SettingsSeenByCreate);
        Assert.Equal(7u, port.SettingsSeenByCreate.ClientVersion);
        Assert.Equal((ushort)9, port.SettingsSeenByCreate.ServerGroup);
        Assert.DoesNotContain(lines, l => l.Contains("clientVersion=") || l.Contains("serverGroup="));
        Assert.Equal("PROBE_SWITCH stage=settings to=list outcome=Ok", lines[0]);
    }

    // 保护:所有行都以前缀开头;阶段行写 to=list(不是 index/port 记号);"result=" 只出现在最后一行,且没有 first=/second= 这些换频道的字段
    [Fact]
    public async Task Every_line_has_the_prefix_stage_lines_say_to_list_and_the_last_line_is_the_only_result_line()
    {
        (_, List<string> lines) = await Run(new FakePort());

        Assert.All(lines, l => Assert.StartsWith("PROBE_SWITCH ", l));
        Assert.All(lines.Where(l => l.Contains(" stage=")), l => Assert.Contains(" to=list ", l + " "));
        Assert.DoesNotContain(lines, l => l.Contains("to=index") || l.Contains("to=port"));
        Assert.Single(lines, l => l.Contains(" result="));
        Assert.Contains(" result=", lines[^1]);
        Assert.DoesNotContain("first=", lines[^1]);
        Assert.DoesNotContain("second=", lines[^1]);
        Assert.DoesNotContain("attempts", lines[^1]);
    }

    // 保护:目录读成功但一个条目也没有:退出码 0,汇总行全 0(firstInDirectory 也是 0)
    [Fact]
    public async Task An_empty_directory_is_a_successful_empty_summary()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => Directory();

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(0, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=Ok",
            "PROBE_SWITCH stage=gateway-login to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH dir-summary entries=0 usable=0 candidates=0 firstInDirectory=0",
            "PROBE_SWITCH result=ok mode=list elapsedMs=N",
        }, Normalized(lines));
    }

    // ======== 二、网关登录失败:退出码 5、立即结束、不重试、不读目录 ========

    // 保护:网关登录失败 → 退出码 5;只调用一次登录、不读目录;阶段行带失败枚举/到达阶段/网关结局;最后一行带 failedStage=gateway-login
    [Fact]
    public async Task A_gateway_login_failure_exits_5_at_once_without_retry_and_without_reading_the_directory()
    {
        var port = new FakePort();
        port.Session.OnLogin = LoginFailed;

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(5, exit);
        Assert.Equal(new[] { "login" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=Ok",
            "PROBE_SWITCH stage=gateway-login to=list outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=Success gatewayError=None elapsedMs=N",
            "PROBE_SWITCH result=fail mode=list failedStage=gateway-login elapsedMs=N",
        }, Normalized(lines));
    }

    // 保护:网关给了业务错误时,只带结局与错误枚举名(不带错误键文字、不带账号);取地址那一步失败时 reached= 是到达的阶段
    [Fact]
    public async Task A_gateway_server_error_and_an_address_failure_show_only_enum_names()
    {
        var rejected = new FakePort();
        rejected.Session.OnLogin = () => new OnlineLoginResult(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed,
            GatewayResult.ServerError("SYNTH-ERROR-KEY-" + Account, GatewayErrorCode.WrongCredentials));
        var noAddress = new FakePort();
        noAddress.Session.OnLogin = () => new OnlineLoginResult(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress);

        (int rejectedExit, List<string> rejectedLines) = await Run(rejected);
        (int noAddressExit, List<string> noAddressLines) = await Run(noAddress);

        Assert.Equal(5, rejectedExit);
        Assert.Equal(
            "PROBE_SWITCH stage=gateway-login to=list outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=ServerError gatewayError=WrongCredentials elapsedMs=N",
            Normalized(rejectedLines)[1]);
        Assert.DoesNotContain("SYNTH", string.Join("\n", rejectedLines));
        Assert.Equal(5, noAddressExit);
        Assert.Equal(
            "PROBE_SWITCH stage=gateway-login to=list outcome=Failed failure=NoGameServerAddress reached=GameServerAddress gateway=Success gatewayError=None elapsedMs=N",
            Normalized(noAddressLines)[1]);
        Assert.Equal(new[] { "login" }, rejected.Session.Calls);
        Assert.Equal(new[] { "login" }, noAddress.Session.Calls);
    }

    // 保护:登录调用抛异常(同步抛,或抛成已出错的任务)都收成"网关一步 Faulted"、退出码 5,只带异常类型名,不改调用次数
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_login_that_throws_is_a_faulted_gateway_step_with_exit_5_and_only_the_type_name(bool faultAsTask)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = faultAsTask;
        port.Session.OnLogin = () => throw new IOException("SYNTH-LOGIN-EXCEPTION " + Account + " " + GatewayHost);

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(5, exit);
        Assert.Equal(new[] { "login" }, port.Session.Calls);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=Ok",
            "PROBE_SWITCH stage=gateway-login to=list outcome=Faulted fault=IOException elapsedMs=N",
            "PROBE_SWITCH result=fail mode=list failedStage=gateway-login elapsedMs=N",
        }, Normalized(lines));
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:登录没抛异常却给了 null 结果,同样是 Faulted(fault 用固定记号 NullResult,不留空),退出码 5
    [Fact]
    public async Task A_login_that_gives_no_result_is_a_faulted_gateway_step()
    {
        var port = new FakePort();
        port.Session.OnLogin = () => null;

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(5, exit);
        Assert.Equal("PROBE_SWITCH stage=gateway-login to=list outcome=Faulted fault=NullResult elapsedMs=N", Normalized(lines)[1]);
        Assert.Equal(new[] { "login" }, port.Session.Calls);
    }

    // ======== 三、读目录失败:退出码 6 ========

    // 保护:目录读失败 → 退出码 6;只登录一次、只读一次目录;阶段行的 outcome 是结局枚举名(不带失败详情文字);最后一行带 failedStage=directory
    [Theory]
    [InlineData(ChannelDirectoryLoadOutcome.IoError, "IoError")]
    [InlineData(ChannelDirectoryLoadOutcome.FrameError, "FrameError")]
    [InlineData(ChannelDirectoryLoadOutcome.KeyExchangeRejected, "KeyExchangeRejected")]
    public async Task A_directory_failure_exits_6_after_one_login_and_one_read_and_names_only_the_outcome(ChannelDirectoryLoadOutcome outcome, string name)
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Fail(outcome, "SYNTH-DETAIL " + GatewayHost);

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(6, exit);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=Ok",
            "PROBE_SWITCH stage=gateway-login to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=list outcome=" + name + " elapsedMs=N",
            "PROBE_SWITCH result=fail mode=list failedStage=directory elapsedMs=N",
        }, Normalized(lines));
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:目录"成功"却没有快照,也是退出码 6,结局记为 NoSnapshot
    [Fact]
    public async Task A_directory_that_succeeds_without_a_snapshot_exits_6_as_no_snapshot()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Success(null, 0, true);

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(6, exit);
        Assert.Equal("PROBE_SWITCH stage=directory to=list outcome=NoSnapshot elapsedMs=N", Normalized(lines)[2]);
        Assert.Equal("PROBE_SWITCH result=fail mode=list failedStage=directory elapsedMs=N", Normalized(lines)[^1]);
        Assert.Equal(4, lines.Count);
    }

    // 保护:读目录抛异常(同步或已出错任务)→ Faulted + 异常类型名、退出码 6、不再读第二次
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_directory_read_that_throws_is_a_faulted_step_with_exit_6_and_only_the_type_name(bool faultAsTask)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = faultAsTask;
        port.Session.OnDirectory = () => throw new IOException("SYNTH-DIRECTORY-EXCEPTION " + GatewayHost);

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(6, exit);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
        Assert.Equal("PROBE_SWITCH stage=directory to=list outcome=Faulted fault=IOException elapsedMs=N", Normalized(lines)[2]);
        Assert.Equal("PROBE_SWITCH result=fail mode=list failedStage=directory elapsedMs=N", Normalized(lines)[^1]);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:读目录没抛异常却给了 null 结果,同样是 Faulted(fault=NullResult),退出码 6
    [Fact]
    public async Task A_directory_read_that_gives_no_result_is_a_faulted_step()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => null;

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(6, exit);
        Assert.Equal("PROBE_SWITCH stage=directory to=list outcome=Faulted fault=NullResult elapsedMs=N", Normalized(lines)[2]);
    }

    // ======== 四、本地设置装配失败:退出码 2、没有会话 ========

    // 保护:读设置失败 → 退出码 2、一个会话也没创建、一次登录都没调;输出里没有失败详情、账号、主机;最后一行 failedStage=settings
    [Fact]
    public async Task A_settings_problem_exits_2_creates_no_session_and_leaks_nothing()
    {
        var port = new FakePort { LoadSucceeds = false };

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(2, exit);
        Assert.Equal(1, port.LoadCalls);
        Assert.Equal(0, port.CreateCalls);
        Assert.Empty(port.Session.Calls);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=SettingsFailed",
            "PROBE_SWITCH result=fail mode=list failedStage=settings elapsedMs=N",
        }, Normalized(lines));
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:读设置"成功"却没给设置对象,同样是退出码 2、没有会话
    [Fact]
    public async Task A_loader_that_succeeds_with_no_settings_exits_2_without_a_session()
    {
        var session = new StrictSession();
        var port = new StrictPort(session) { LoadReturnsNullSettings = true };

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(2, exit);
        Assert.Equal(0, port.CreateCalls);
        Assert.Empty(session.Calls);
        Assert.Equal("PROBE_SWITCH stage=settings to=list outcome=SettingsFailed", lines[0]);
        Assert.Equal(2, lines.Count);
    }

    // 保护:创建会话抛异常 → 退出码 2、只带异常类型名;没有任何登录调用
    [Fact]
    public async Task A_session_that_cannot_be_created_exits_2_with_only_the_exception_type_name()
    {
        var port = new FakePort { ThrowOnCreate = true };

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(2, exit);
        Assert.Equal(1, port.CreateCalls);
        Assert.Empty(port.Session.Calls);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=SettingsFailed fault=IOException",
            "PROBE_SWITCH result=fail mode=list failedStage=settings elapsedMs=N",
        }, Normalized(lines));
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:读设置时抛(非取消)异常 → 退出码 2、异常类型名;不创建会话
    [Fact]
    public async Task A_loader_that_throws_exits_2_with_the_type_name_and_creates_no_session()
    {
        var session = new StrictSession();
        var port = new StrictPort(session) { LoadThrows = new InvalidOperationException("SYNTH-LOAD-EXCEPTION " + Account) };

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(2, exit);
        Assert.Equal(0, port.CreateCalls);
        Assert.Equal("PROBE_SWITCH stage=settings to=list outcome=SettingsFailed fault=InvalidOperationException", lines[0]);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // ======== 五、取消与没料到的异常 ========

    // 保护:登录或读目录时被取消 → 退出码 15、打 abort 行与最后一行(failedStage 是被取消时所在的阶段);会话照样释放;不再往下走
    [Theory]
    [InlineData("login", "gateway-login", new[] { "login" })]
    [InlineData("directory", "directory", new[] { "login", "directory" })]
    public async Task Cancellation_ends_the_run_with_exit_15_and_still_prints_the_last_line(string where, string failedStage, string[] expectedCalls)
    {
        var session = new StrictSession();
        if (where == "login")
        {
            session.OnLogin = _ => throw new OperationCanceledException();
        }
        else
        {
            session.OnDirectory = _ => throw new OperationCanceledException();
        }
        var port = new StrictPort(session);

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(15, exit);
        Assert.Equal(expectedCalls, session.Calls);
        Assert.True(session.Disposed);
        Assert.Equal("PROBE_SWITCH stage=abort outcome=Cancelled", lines[^2]);
        Assert.Equal("PROBE_SWITCH result=fail mode=list failedStage=" + failedStage + " elapsedMs=N", Normalized(lines)[^1]);
    }

    // 保护:调用方的取消令牌真的传到会话里:令牌已取消、会话在登录里检查它 → 退出码 15(如果流程把令牌换成默认值,这里会一路成功)
    [Fact]
    public async Task A_token_that_is_already_cancelled_reaches_the_session_and_ends_the_run_with_exit_15()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var session = new StrictSession { OnLogin = token => { token.ThrowIfCancellationRequested(); return LoginOk(); } };
        var port = new StrictPort(session);

        (int exit, List<string> lines) = await Run(port, "--list", cts.Token);

        Assert.Equal(15, exit);
        Assert.Equal(new[] { "login" }, session.Calls);
        Assert.Equal("PROBE_SWITCH result=fail mode=list failedStage=gateway-login elapsedMs=N", Normalized(lines)[^1]);
    }

    // 保护:读设置阶段被取消(异常是取消类)也是退出码 15,不是 2;没有会话;最后一行 failedStage=settings
    [Fact]
    public async Task Cancellation_while_loading_settings_is_exit_15_not_a_settings_failure()
    {
        var session = new StrictSession();
        var port = new StrictPort(session) { LoadThrows = new OperationCanceledException() };

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(15, exit);
        Assert.Equal(0, port.CreateCalls);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=abort outcome=Cancelled",
            "PROBE_SWITCH result=fail mode=list failedStage=settings elapsedMs=N",
        }, Normalized(lines));
    }

    // 保护:流程自己出了没料到的错(摘要阶段读游戏服主机时抛)→ 退出码 70、只带异常类型名;阶段行都打过;failedStage=summary;会话照样释放
    [Fact]
    public async Task A_bug_in_the_flow_itself_exits_70_with_only_the_type_name()
    {
        var session = new StrictSession { Host = () => throw new InvalidOperationException("SYNTH-HOST-READ " + GatewayHost) };
        var port = new StrictPort(session);

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(70, exit);
        Assert.True(session.Disposed);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=Ok",
            "PROBE_SWITCH stage=gateway-login to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=abort outcome=Unexpected fault=InvalidOperationException",
            "PROBE_SWITCH result=fail mode=list failedStage=summary elapsedMs=N",
        }, Normalized(lines));
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:没料到的异常包了一层时,fault 取最内层异常的类型名(与换频道流程一致)
    [Fact]
    public async Task An_unexpected_wrapped_exception_reports_the_innermost_type_name()
    {
        var session = new StrictSession
        {
            Host = () => throw new InvalidOperationException("SYNTH-OUTER", new FormatException("SYNTH-INNER")),
        };

        (int exit, List<string> lines) = await Run(new StrictPort(session));

        Assert.Equal(70, exit);
        Assert.Contains("PROBE_SWITCH stage=abort outcome=Unexpected fault=FormatException", lines);
    }

    // ======== 六、真会话(内存假网关,不绑端口、不连任何服务) ========

    private sealed class RigPort : IOnlineSwitchPort
    {
        internal RigPort(OnlineFlowRig rig)
        {
            Rig = rig;
        }

        internal OnlineFlowRig Rig { get; }
        internal OnlineClientSession Session { get; private set; }
        internal int CreateCalls { get; private set; }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            settings = Rig.Settings;
            problem = null;
            return true;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            CreateCalls++;
            Session = new OnlineClientSession(settings, null, null, Rig.Hooks);
            return new OnlineClientSwitchSession(Session);
        }
    }

    private static readonly string SynthToken = Convert.ToBase64String(GatewayTestSupport.Pattern(46, 1));

    /// <summary>
    /// 新环境:令牌固定、频道端口是合成的第一个端口、网关给的游戏服地址是 <see cref="GatewayIp"/>;目录三条(组标签 siroco → 服务器组 3):
    /// 第一个频道本身(网关地址 + 第一个端口)、另一个主机上的乙端口、网关地址上的丙端口。读目录的次数记在返回的计数器里。
    /// </summary>
    private static (OnlineFlowRig Rig, RigPort Port, int[] DirectoryReads) NewEnv()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ForceReplyPlaintext = "0|" + SynthToken;
        rig.FakeGateway.GameServerIp = GatewayIp;
        rig.Hooks.SwitchLoginRetryDelay = TimeSpan.Zero;
        rig.Settings.ChannelPort = FirstPort;
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("siroco", (GatewayIp, FirstPort), (OtherIp, PortB), (GatewayIp, PortC));
        var reads = new int[1];
        rig.Hooks.DirectoryLoader = (_, _, _) =>
        {
            reads[0]++;
            return Task.FromResult(ChannelDirectoryLoadResult.Success(snapshot, 0, true));
        };
        return (rig, new RigPort(rig), reads);
    }

    // 保护:真会话上的 list 流程:退出码 0;网关只 POST 一次、取游戏服地址只一次、读目录一次;一个频道也没连(没有频道连接请求、没有频道对象);
    // 会话已释放;输出里第一个频道用的是网关给的主机(0 号 first=1),组按目录里组标签解出的 3
    [Fact]
    public async Task On_the_real_session_a_list_run_logs_in_once_reads_the_directory_once_and_opens_no_channel()
    {
        (OnlineFlowRig rig, RigPort port, int[] directoryReads) = NewEnv();

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(0, exit);
        Assert.Equal(1, rig.FakeGateway.PostCount);
        Assert.Equal(1, rig.FakeGateway.GetCount);
        Assert.Equal(1, directoryReads[0]);
        Assert.Empty(rig.RequestedPorts);
        Assert.Empty(rig.Channels);
        Assert.Equal(1, port.CreateCalls);
        Assert.Equal(OnlineClientSessionState.Disposed, port.Session.State);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=list outcome=Ok",
            "PROBE_SWITCH stage=gateway-login to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=list outcome=Ok elapsedMs=N",
            "PROBE_SWITCH dir entry=0 usable=1 first=1 candidate=- group=3 kind=-1",
            "PROBE_SWITCH dir entry=1 usable=1 first=0 candidate=0 group=3 kind=-1",
            "PROBE_SWITCH dir entry=2 usable=1 first=0 candidate=1 group=3 kind=-1",
            "PROBE_SWITCH dir-summary entries=3 usable=3 candidates=2 firstInDirectory=1",
            "PROBE_SWITCH result=ok mode=list elapsedMs=N",
        }, Normalized(lines));
    }

    // 保护:真会话上网关拒绝 → 退出码 5;网关只被问一次、没取地址、没读目录、没有任何频道;失败阶段 gateway-login;口令不出现在输出里
    [Fact]
    public async Task On_the_real_session_a_gateway_rejection_ends_at_once_with_one_post_and_no_directory_read()
    {
        (OnlineFlowRig rig, RigPort port, int[] directoryReads) = NewEnv();
        rig.FakeGateway.ForceReplyPlaintext = null;                         // 固定回复会跳过口令校验,这里要真的校验
        rig.Settings.Password = "SYNTH-WRONG-PASSWORD";                     // 假网关里账号对应的口令摘要不匹配

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(5, exit);
        Assert.Equal(1, rig.FakeGateway.PostCount);
        Assert.Equal(0, rig.FakeGateway.GetCount);
        Assert.Equal(0, directoryReads[0]);
        Assert.Empty(rig.RequestedPorts);
        Assert.Empty(rig.Channels);
        Assert.Equal("PROBE_SWITCH result=fail mode=list failedStage=gateway-login elapsedMs=N", Normalized(lines)[^1]);
        Assert.DoesNotContain("SYNTH-WRONG-PASSWORD", string.Join("\n", lines));
        Assert.Equal(OnlineClientSessionState.Disposed, port.Session.State);
    }

    // 保护:真会话上读目录失败 → 退出码 6;网关登录了一次;一个频道也没连
    [Fact]
    public async Task On_the_real_session_a_directory_failure_exits_6_and_opens_no_channel()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();
        rig.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "SYNTH"));

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(6, exit);
        Assert.Equal(1, rig.FakeGateway.PostCount);
        Assert.Empty(rig.RequestedPorts);
        Assert.Empty(rig.Channels);
        Assert.Equal("PROBE_SWITCH stage=directory to=list outcome=IoError elapsedMs=N", Normalized(lines)[2]);
    }
}
