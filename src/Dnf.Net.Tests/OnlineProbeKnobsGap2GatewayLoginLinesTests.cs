using Dnf.Net.Gateway;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineProbeKnobsGap2Support;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 的"网关登录"阶段在每一种失败结局下打出的整行文本(耗时归一成 N 之后逐字比):
/// <c>outcome=Failed failure=… reached=… gateway=… gatewayError=…</c> 四个字段与行尾 <c>elapsedMs=</c>、
/// <c>outcome=Faulted fault=…</c>(会话调用抛异常:同步抛与抛成出错的任务、带内层异常、返回 null 各一种),
/// 外加这几种结局下的退出码 5、"只登录一次、不读目录、不连频道"与最后一行。
/// 假端口、假会话、合成的网关结果;不连网、不读本机配置。
/// </summary>
public sealed class OnlineProbeKnobsGap2GatewayLoginLinesTests
{
    private const string SettingsLine = "PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=0 serverGroup=0";
    private const string FinalLine = "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N";

    private static OnlineLoginResult LoginWith(OnlineStage stage, OnlineFailure failure, GatewayResult gateway)
    {
        return new OnlineLoginResult(stage, failure, gateway);
    }

    private sealed record Case(string Name, Func<OnlineLoginResult> Make, string Line);

    // 每一档失败:网关结局与业务错误码故意取互不相同的值(登录被拒/封禁/其它业务错误/限流/网络错误/协议错误/本地输入不合规/没有游戏服地址……),
    // 两个字段的取值与 failure、reached 也各不相同,所以任何一个字段名、取值来源或顺序出错都会让整行对不上。
    private static readonly Case[] FailedCases =
    {
        new("credentials-rejected",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.ServerError("SYNTH-KEY-ONE", GatewayErrorCode.WrongCredentials)),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=ServerError gatewayError=WrongCredentials elapsedMs=N"),
        new("account-banned",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.ServerError("SYNTH-KEY-TWO", GatewayErrorCode.AccountBanned)),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=ServerError gatewayError=AccountBanned elapsedMs=N"),
        new("server-error-with-an-unknown-key",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.ServerError("SYNTH-KEY-THREE", GatewayErrorCode.Unknown)),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=ServerError gatewayError=Unknown elapsedMs=N"),
        new("server-error-generic-fail",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.ServerError("SYNTH-KEY-FOUR", GatewayErrorCode.Fail)),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=ServerError gatewayError=Fail elapsedMs=N"),
        new("rate-limited",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.RateLimited(7, 429, "SYNTH-MESSAGE")),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=RateLimited gatewayError=None elapsedMs=N"),
        new("transport-error",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.Transport(0, "SYNTH-MESSAGE", new IOException("SYNTH-INNER"))),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=TransportError gatewayError=None elapsedMs=N"),
        new("protocol-error",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.Protocol("SYNTH-MESSAGE")),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=ProtocolError gatewayError=None elapsedMs=N"),
        new("invalid-input",
            () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.Invalid("SYNTH-MESSAGE")),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=InvalidInput gatewayError=None elapsedMs=N"),
        new("no-gateway-detail-at-all",
            () => LoginFailed(),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=Success gatewayError=None elapsedMs=N"),
        new("no-game-server-address",
            () => LoginWith(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress, GatewayResult.NotConfigured(404)),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=NoGameServerAddress reached=GameServerAddress gateway=NotConfigured gatewayError=None elapsedMs=N"),
        new("game-server-address-unusable-though-the-call-succeeded",
            () => LoginWith(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress, GatewayResult.WithText("SYNTH-NOT-AN-ADDRESS", 200)),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=NoGameServerAddress reached=GameServerAddress gateway=Success gatewayError=None elapsedMs=N"),
        new("address-step-transport-error",
            () => LoginWith(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress, GatewayResult.Transport(500, "SYNTH-MESSAGE", null)),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=NoGameServerAddress reached=GameServerAddress gateway=TransportError gatewayError=None elapsedMs=N"),
        new("address-step-rate-limited",
            () => LoginWith(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress, GatewayResult.RateLimited(11, 429, "SYNTH-MESSAGE")),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=NoGameServerAddress reached=GameServerAddress gateway=RateLimited gatewayError=None elapsedMs=N"),
        new("address-step-protocol-error",
            () => LoginWith(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress, GatewayResult.Protocol("SYNTH-MESSAGE")),
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=NoGameServerAddress reached=GameServerAddress gateway=ProtocolError gatewayError=None elapsedMs=N"),
    };

    public static IEnumerable<object[]> FailedCaseNames()
    {
        return FailedCases.Select(c => new object[] { c.Name });
    }

    // 保护:网关登录失败那一行的每个令牌与字段名——outcome=Failed、failure=(失败原因)、reached=(停在的阶段)、gateway=(网关结局)、gatewayError=(业务错误码)、行尾 elapsedMs=——
    // 逐字出现、次序固定;退出码 5;只登录一次、没读目录、没连频道;会话已释放;最后一行是两步都没执行的失败行。
    // 推导:网关登录失败 → 立即以退出码 5 结束,输出恰好三行(设置阶段行、网关登录失败行、最后一行)。
    [Theory]
    [MemberData(nameof(FailedCaseNames))]
    public async Task A_failed_gateway_login_prints_its_four_fields_in_one_exact_line_and_exits_5(string name)
    {
        Case c = FailedCases.Single(x => x.Name == name);
        var port = new FakePort();
        port.Session.OnLogin = c.Make;

        (int exit, string[] lines) = await Run(port, "--to-index 0");

        Assert.Equal(OnlineSwitchExitCodes.GatewayAuthFailed, exit);
        Assert.Equal(5, exit);
        Assert.Equal(new[] { SettingsLine, c.Line, FinalLine }, lines);
        Assert.Equal(new[] { "login" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
    }

    // 保护:失败行里的四个字段各取自结果对象的对应属性(而不是互相串用):一个"什么都不一样"的合成结果,每个字段的取值各不相同
    // 推导:阶段 SelectSlot、失败 NoUsableSlot、网关 ServerError、错误码 UserExists——四个值两两不同,名字只可能来自各自的属性
    [Fact]
    public async Task Each_field_of_the_failed_line_comes_from_its_own_property()
    {
        var port = new FakePort();
        port.Session.OnLogin = () => LoginWith(OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, GatewayResult.ServerError("SYNTH-KEY", GatewayErrorCode.UserExists));

        (int exit, string[] lines) = await Run(port, "--to-index 0");

        Assert.Equal(5, exit);
        Assert.Equal(
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=NoUsableSlot reached=SelectSlot gateway=ServerError gatewayError=UserExists elapsedMs=N",
            lines[1]);
    }

    // 保护:失败行里的 to= 记号跟着命令行走(--to-port 时是 port),设置阶段行与最后一行里的客户端版本号、服务器组也随命令行的两个开关;
    // 失败行本身不带任何端口号、主机、账号、口令、令牌或异常文字(合成的消息与密钥一个都不出现)
    // 推导:--client-version 4000000000 --server-group 17 → 设置行 clientVersion=4000000000 serverGroup=17;最后一行 clientVersion=4000000000
    [Fact]
    public async Task The_failed_line_follows_the_port_target_token_and_leaks_nothing()
    {
        var port = new FakePort();
        port.Session.OnLogin = () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed,
            GatewayResult.Transport(502, "SYNTH-MESSAGE " + GatewayHost + " " + Account, new IOException("SYNTH-INNER " + Password)));

        (int exit, string[] lines) = await Run(port, "--to-port " + TargetPort + " --client-version 4000000000 --server-group 17");

        Assert.Equal(5, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=port outcome=Ok clientVersion=4000000000 serverGroup=17",
            "PROBE_SWITCH stage=gateway-login to=port outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=TransportError gatewayError=None elapsedMs=N",
            "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=4000000000 elapsedMs=N",
        }, lines);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
        Assert.Equal(new[] { "login" }, port.Session.Calls);
    }

    // 保护:同一条命令行换成按序号选目标时,失败行的 to= 是 index:K(K 取多位数也原样)
    [Fact]
    public async Task The_failed_line_carries_the_index_target_token()
    {
        var port = new FakePort();
        port.Session.OnLogin = () => LoginWith(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, GatewayResult.RateLimited(3, 429, "SYNTH-MESSAGE"));

        (int exit, string[] lines) = await Run(port, "--to-index 123456789");

        Assert.Equal(5, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=index:123456789 outcome=Ok clientVersion=0 serverGroup=0",
            "PROBE_SWITCH stage=gateway-login to=index:123456789 outcome=Failed failure=GatewayLoginFailed reached=GatewayLogin gateway=RateLimited gatewayError=None elapsedMs=N",
            FinalLine,
        }, lines);
    }

    // 保护:会话调用抛异常(同步抛与抛成"已出错的任务"都一样)时,网关登录那一行是 outcome=Faulted fault=<异常类型名> elapsedMs=…;
    // 只有类型名,没有消息;有内层异常时取最内层那个的类型名;退出码 5,只登录一次,最后一行是两步都没执行的失败行
    // 推导:取 GetBaseException():IOException 本身 → IOException;外层 InvalidOperationException 带内层 TimeoutException → TimeoutException
    [Theory]
    [InlineData("io", false, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=IOException elapsedMs=N")]
    [InlineData("io", true, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=IOException elapsedMs=N")]
    [InlineData("invalid-operation", false, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=InvalidOperationException elapsedMs=N")]
    [InlineData("invalid-operation", true, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=InvalidOperationException elapsedMs=N")]
    [InlineData("timeout", false, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=TimeoutException elapsedMs=N")]
    [InlineData("wrapped", false, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=TimeoutException elapsedMs=N")]
    [InlineData("wrapped", true, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=TimeoutException elapsedMs=N")]
    [InlineData("aggregate", false, "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault=IOException elapsedMs=N")]
    public async Task A_gateway_login_that_throws_prints_one_faulted_line_with_only_the_type_name(string kind, bool asTask, string expectedLine)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = asTask;
        string message = "SYNTH-EXCEPTION-MESSAGE " + GatewayHost + ":" + FirstPort + " " + Account;
        Exception fault = kind switch
        {
            "io" => new IOException(message),
            "invalid-operation" => new InvalidOperationException(message),
            "timeout" => new TimeoutException(message),
            "wrapped" => new InvalidOperationException(message, new TimeoutException(message)),
            _ => new AggregateException(new IOException(message)),
        };
        port.Session.OnLogin = () => throw fault;

        (int exit, string[] lines) = await Run(port, "--to-index 0");

        Assert.Equal(5, exit);
        Assert.Equal(new[] { SettingsLine, expectedLine, FinalLine }, lines);
        Assert.Equal(new[] { "login" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
    }

    // 保护:会话的网关登录返回 null(既没成功也没抛异常)时,这一行是 outcome=Faulted fault= 后面接空(没有异常可报类型名),随后直接是 elapsedMs=;
    // 退出码 5,只登录一次。这是现有行为的逐字钉住:若以后改成别的占位写法,这条要随之改并过评审
    // 推导:结果为 null → 打 " outcome=Faulted fault=" + (没有异常 = 空) → 再接 " elapsedMs="
    [Fact]
    public async Task A_gateway_login_that_returns_nothing_prints_a_faulted_line_with_an_empty_fault()
    {
        var port = new FakePort();
        port.Session.OnLogin = () => null;

        (int exit, string[] lines) = await Run(port, "--to-index 0");

        Assert.Equal(5, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            "PROBE_SWITCH stage=gateway-login to=index:0 outcome=Faulted fault= elapsedMs=N",
            FinalLine,
        }, lines);
        Assert.Equal(new[] { "login" }, port.Session.Calls);
    }

    // 保护:登录成功的那一行是 outcome=Ok elapsedMs=…(没有 failure/reached/gateway/gatewayError 这些失败字段),并且接着读目录
    [Fact]
    public async Task A_successful_gateway_login_prints_the_short_ok_line_and_goes_on_to_the_directory()
    {
        var port = new FakePort();
        port.Session.OnLogin = () => LoginWith(OnlineStage.GameServerAddress, OnlineFailure.None, GatewayResult.WithToken("SYNTH-TOKEN"));

        (int exit, string[] lines) = await Run(port, "--to-index 0");

        Assert.Equal(0, exit);
        Assert.Equal("PROBE_SWITCH stage=gateway-login to=index:0 outcome=Ok elapsedMs=N", lines[1]);
        Assert.Equal("directory", port.Session.Calls[1]);
        Assert.DoesNotContain("SYNTH-TOKEN", string.Join("\n", lines));
    }
}
