using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineProbeKnobsGap2Support;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 的"读目录"阶段在每一种结局下打出的整行文本(耗时归一成 N 之后逐字比):
/// 目录加载的每一种失败枚举名、"成功却没有快照"(NoSnapshot)、会话调用抛异常(同步抛、抛成出错的任务、带内层异常)与返回 null 的 Faulted,
/// 外加这些结局下的退出码 6、"只登录一次、只读一次目录、不连频道"与最后一行。假端口、假会话;不连网、不读本机配置。
/// </summary>
public sealed class OnlineProbeKnobsGap2DirectoryLinesTests
{
    private const string SettingsLine = "PROBE_SWITCH stage=settings to=port outcome=Ok clientVersion=0 serverGroup=0";
    private const string GatewayOkLine = "PROBE_SWITCH stage=gateway-login to=port outcome=Ok elapsedMs=N";
    private const string FinalLine = "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N";

    private static string PortLine => "--to-port " + TargetPort;

    // 保护:目录加载的每一种失败枚举名(InvalidArgument、IoError、FrameError、KeyExchangeRejected、BadKeyExchangeLength、VersionRejected、
    // DecompressOrDecryptFailed、ParseFailed)原样写在 outcome= 之后,整行是 "stage=directory to=port outcome=<名字> elapsedMs=N";
    // 退出码 6;登录与读目录各一次,没有连频道;失败说明、握手码等附带信息一个字也不进输出。
    // 推导:目录失败 → 立即以退出码 6 结束,输出恰好四行(设置、网关登录 Ok、读目录失败、最后一行)。
    [Theory]
    [InlineData(ChannelDirectoryLoadOutcome.InvalidArgument, "PROBE_SWITCH stage=directory to=port outcome=InvalidArgument elapsedMs=N")]
    [InlineData(ChannelDirectoryLoadOutcome.IoError, "PROBE_SWITCH stage=directory to=port outcome=IoError elapsedMs=N")]
    [InlineData(ChannelDirectoryLoadOutcome.FrameError, "PROBE_SWITCH stage=directory to=port outcome=FrameError elapsedMs=N")]
    [InlineData(ChannelDirectoryLoadOutcome.KeyExchangeRejected, "PROBE_SWITCH stage=directory to=port outcome=KeyExchangeRejected elapsedMs=N")]
    [InlineData(ChannelDirectoryLoadOutcome.BadKeyExchangeLength, "PROBE_SWITCH stage=directory to=port outcome=BadKeyExchangeLength elapsedMs=N")]
    [InlineData(ChannelDirectoryLoadOutcome.VersionRejected, "PROBE_SWITCH stage=directory to=port outcome=VersionRejected elapsedMs=N")]
    [InlineData(ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed, "PROBE_SWITCH stage=directory to=port outcome=DecompressOrDecryptFailed elapsedMs=N")]
    [InlineData(ChannelDirectoryLoadOutcome.ParseFailed, "PROBE_SWITCH stage=directory to=port outcome=ParseFailed elapsedMs=N")]
    public async Task Each_directory_failure_prints_its_outcome_name_in_one_exact_line_and_exits_6(ChannelDirectoryLoadOutcome outcome, string expectedLine)
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Fail(
            outcome, "SYNTH-DETAIL " + GatewayHost + ":" + FirstPort, handshakeStatus: 77u, versionUpToDate: false, parseStatus: ChannelInfoParseStatus.Truncated);

        (int exit, string[] lines) = await Run(port, PortLine);

        Assert.Equal(OnlineSwitchExitCodes.DirectoryFailed, exit);
        Assert.Equal(6, exit);
        Assert.Equal(new[] { SettingsLine, GatewayOkLine, expectedLine, FinalLine }, lines);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
        Assert.DoesNotContain("77", string.Join("\n", lines));                                                   // 握手码不进输出
    }

    // 保护:目录读"成功"却没有快照(防御分支)时,这一行是 outcome=NoSnapshot,同样是退出码 6、不连频道
    // 推导:Succeeded 为真、Snapshot 为 null → 写 "NoSnapshot"(而不是枚举名 Ok)→ 退出码 6
    [Fact]
    public async Task A_directory_that_succeeds_without_a_snapshot_prints_NoSnapshot_and_exits_6()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Success(null, 0, true);

        (int exit, string[] lines) = await Run(port, PortLine);

        Assert.Equal(6, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            "PROBE_SWITCH stage=directory to=port outcome=NoSnapshot elapsedMs=N",
            FinalLine,
        }, lines);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
    }

    // 保护:读目录的会话调用抛异常(同步抛与抛成"已出错的任务"一样)时,这一行是 outcome=Faulted fault=<最内层异常的类型名> elapsedMs=…;
    // 只有类型名,没有消息;退出码 6;不连频道
    // 推导:取 GetBaseException():IOException 本身 → IOException;外层 InvalidOperationException 带内层 TimeoutException → TimeoutException
    [Theory]
    [InlineData("io", false, "PROBE_SWITCH stage=directory to=port outcome=Faulted fault=IOException elapsedMs=N")]
    [InlineData("io", true, "PROBE_SWITCH stage=directory to=port outcome=Faulted fault=IOException elapsedMs=N")]
    [InlineData("invalid-operation", false, "PROBE_SWITCH stage=directory to=port outcome=Faulted fault=InvalidOperationException elapsedMs=N")]
    [InlineData("invalid-operation", true, "PROBE_SWITCH stage=directory to=port outcome=Faulted fault=InvalidOperationException elapsedMs=N")]
    [InlineData("wrapped", false, "PROBE_SWITCH stage=directory to=port outcome=Faulted fault=TimeoutException elapsedMs=N")]
    [InlineData("wrapped", true, "PROBE_SWITCH stage=directory to=port outcome=Faulted fault=TimeoutException elapsedMs=N")]
    public async Task A_directory_load_that_throws_prints_one_faulted_line_with_only_the_type_name(string kind, bool asTask, string expectedLine)
    {
        var port = new FakePort();
        port.Session.FaultAsTask = asTask;
        string message = "SYNTH-EXCEPTION-MESSAGE " + GatewayHost + ":" + FirstPort + " " + Account;
        Exception fault = kind switch
        {
            "io" => new IOException(message),
            "invalid-operation" => new InvalidOperationException(message),
            _ => new InvalidOperationException(message, new TimeoutException(message)),
        };
        port.Session.OnDirectory = () => throw fault;

        (int exit, string[] lines) = await Run(port, PortLine);

        Assert.Equal(6, exit);
        Assert.Equal(new[] { SettingsLine, GatewayOkLine, expectedLine, FinalLine }, lines);
        Assert.Equal(new[] { "login", "directory" }, port.Session.Calls);
        Assert.DoesNotContain("SYNTH", string.Join("\n", lines));
    }

    // 保护:读目录返回 null(既没成功也没抛异常)时,这一行是 outcome=Faulted fault= 后面接空,随后直接是 elapsedMs=;退出码 6。
    // 这是现有行为的逐字钉住:若以后改成别的占位写法,这条要随之改并过评审
    // 推导:结果为 null → 打 " outcome=Faulted fault=" + (没有异常 = 空) → 再接 " elapsedMs="
    [Fact]
    public async Task A_directory_load_that_returns_nothing_prints_a_faulted_line_with_an_empty_fault()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => null;

        (int exit, string[] lines) = await Run(port, PortLine);

        Assert.Equal(6, exit);
        Assert.Equal(new[]
        {
            SettingsLine,
            GatewayOkLine,
            "PROBE_SWITCH stage=directory to=port outcome=Faulted fault= elapsedMs=N",
            FinalLine,
        }, lines);
    }

    // 保护:读目录失败行里的 to= 记号跟着命令行走(按序号选目标时是 index:K,K 即使超出候选个数也是读目录失败先于选目标,退出码仍是 6 而不是 7),
    // 设置阶段行与最后一行里的客户端版本号、服务器组随命令行的两个开关
    // 推导:--to-index 9 --client-version 4000000000 --server-group 17 → to=index:9;目录在选目标之前失败,所以不会出现 stage=target 行
    [Fact]
    public async Task The_directory_failure_line_follows_the_index_target_token_and_comes_before_target_selection()
    {
        var port = new FakePort();
        port.Session.OnDirectory = () => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, "SYNTH-DETAIL");

        (int exit, string[] lines) = await Run(port, "--to-index 9 --client-version 4000000000 --server-group 17");

        Assert.Equal(6, exit);
        Assert.Equal(new[]
        {
            "PROBE_SWITCH stage=settings to=index:9 outcome=Ok clientVersion=4000000000 serverGroup=17",
            "PROBE_SWITCH stage=gateway-login to=index:9 outcome=Ok elapsedMs=N",
            "PROBE_SWITCH stage=directory to=index:9 outcome=FrameError elapsedMs=N",
            "PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=4000000000 elapsedMs=N",
        }, lines);
    }

    // 保护:读目录成功的那一行是 outcome=Ok elapsedMs=…(没有别的字段),之后继续往下走
    [Fact]
    public async Task A_successful_directory_load_prints_the_short_ok_line()
    {
        var port = new FakePort();

        (int exit, string[] lines) = await Run(port, "--to-index 1");

        Assert.Equal(0, exit);
        Assert.Equal("PROBE_SWITCH stage=directory to=index:1 outcome=Ok elapsedMs=N", lines[2]);
        Assert.Equal("login", port.Session.Calls[0]);
        Assert.Equal("directory", port.Session.Calls[1]);
        Assert.Equal(1, port.Session.Count("directory"));
    }
}
