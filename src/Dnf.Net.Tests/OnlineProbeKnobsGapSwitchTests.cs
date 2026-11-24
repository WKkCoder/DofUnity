using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 子命令装配段里"装配回调返回 true 却没给出设置"的防御分支,以及它的对照(返回 true 且设置非空)。
/// 假端口是本文件自带的:装配回调的返回值与给出的设置可任意组合,<c>CreateSession</c> 被调用时可以直接让测试失败;
/// 会话用共用件里的假会话。不读本机配置、不联网。
/// </summary>
public sealed class OnlineProbeKnobsGapSwitchTests
{
    /// <summary>
    /// 装配回调的返回值与给出的设置可以任意组合的假端口。<see cref="CreateSession"/> 一旦被调用就记一笔,
    /// <see cref="FailOnCreate"/> 为真时还会抛异常(异常消息带合成标记,用来证明输出里没有异常信息)。
    /// </summary>
    private sealed class SettingsPort : IOnlineSwitchPort
    {
        internal bool LoadReturns { get; set; }
        internal OnlineEntrySettings LoadGives { get; set; }
        internal bool FailOnCreate { get; set; }
        internal FakeSession Session { get; } = new();
        internal int LoadCalls { get; private set; }
        internal int CreateCalls { get; private set; }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            LoadCalls++;
            settings = LoadGives;
            problem = "SYNTH-GAP-PROBLEM-DETAIL " + Account;     // 就算被读到,也不该出现在任何输出里
            return LoadReturns;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            CreateCalls++;
            if (FailOnCreate)
            {
                throw new InvalidOperationException("SYNTH-GAP-CREATE-MUST-NOT-HAPPEN " + GatewayHost);
            }
            Session.ClientVersion = settings.ClientVersion;
            return Session;
        }
    }

    private static async Task<(int Exit, List<string> Lines)> Run(SettingsPort port, string line)
    {
        var lines = new List<string>();
        string[] args = line.Length == 0 ? Array.Empty<string>() : line.Split(' ');
        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(args, DefaultRoot, port, lines.Add), 10);
        return (exit, lines);
    }

    /// <summary>
    /// 这条断言保护的行为:装配回调返回 true 但把设置置为 null(防御分支)时,等同装配失败——退出码 2、没有创建会话(假端口的创建一旦被调用就失败)、
    /// 没有任何会话调用;输出恰好两行(设置阶段行不带异常信息、最后一行),没有 fault= 也没有任何合成异常文字。
    /// 推导:<c>TryLoadSettings</c> 真且设置非空才往下装配;设置为空则会话保持 null → 打 <c>outcome=SettingsFailed</c>(无 fault)→ 退出码 2。
    /// 命令行里带 --client-version/--server-group 的几行还证明:没有去给 null 设置赋值(那会抛空引用);
    /// 最后一行里的客户端版本号取命令行给的值(没给为 0),因为会话没创建出来、没有"会话实际值"可取。
    /// </summary>
    [Theory]
    [InlineData("--to-index 0", "index:0", 0)]
    [InlineData("--to-port 47399", "port", 0)]
    [InlineData("--to-index 1 --client-version 7", "index:1", 7)]
    [InlineData("--to-port 47399 --server-group 3", "port", 0)]
    [InlineData("--to-index 2 --client-version 7 --server-group 3", "index:2", 7)]
    public async Task A_loader_that_succeeds_but_gives_no_settings_is_a_settings_failure_and_creates_no_session(string line, string expectedTo, int expectedVersionInFinalLine)
    {
        var port = new SettingsPort { LoadReturns = true, LoadGives = null, FailOnCreate = true };

        (int exit, List<string> lines) = await Run(port, line);

        Assert.Equal(OnlineSwitchExitCodes.SettingsFailed, exit);
        Assert.Equal(2, exit);
        Assert.Equal(1, port.LoadCalls);
        Assert.Equal(0, port.CreateCalls);
        Assert.Empty(port.Session.Calls);
        Assert.Equal(2, lines.Count);
        Assert.Equal("PROBE_SWITCH stage=settings to=" + expectedTo + " outcome=SettingsFailed", lines[0]);     // 没有 fault=
        Assert.StartsWith("PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion="
            + expectedVersionInFinalLine + " elapsedMs=", lines[1]);
        string all = string.Join("\n", lines);
        Assert.DoesNotContain("fault=", all);
        Assert.DoesNotContain("Exception", all);
        Assert.DoesNotContain("SYNTH-GAP", all);
        Assert.DoesNotContain(Account, all);
        Assert.DoesNotContain(GatewayHost, all);
    }

    /// <summary>
    /// 这条断言保护的行为:装配回调返回 false 时,即使它给出了一份非空设置也不能往下装配——退出码 2、没有创建会话。
    /// 推导:返回值为假 → 与设置是否为空无关 → 会话保持 null → 退出码 2。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_loader_that_fails_never_leads_to_a_session_even_when_it_still_hands_out_settings(bool handsOutSettings)
    {
        var port = new SettingsPort { LoadReturns = false, LoadGives = handsOutSettings ? Settings() : null, FailOnCreate = true };

        (int exit, List<string> lines) = await Run(port, "--to-index 0 --client-version 7");

        Assert.Equal(2, exit);
        Assert.Equal(1, port.LoadCalls);
        Assert.Equal(0, port.CreateCalls);
        Assert.Empty(port.Session.Calls);
        Assert.Equal("PROBE_SWITCH stage=settings to=index:0 outcome=SettingsFailed", lines[0]);
        Assert.DoesNotContain("fault=", string.Join("\n", lines));
    }

    /// <summary>
    /// 这条断言保护的行为(对照):装配回调返回 true 且设置非空时正常往下走——创建一次会话、设置阶段行是 Ok、整个流程走完退出码 0,
    /// 命令行里的两个开关已经写进设置、会话拿到的就是这份(被改过的)设置。
    /// 推导:共用件假会话的默认结果是第一个频道 3 个角色、换频道 2 个角色,都收到角色列表 → 两步 Ok → 退出码 0。
    /// </summary>
    [Fact]
    public async Task A_loader_that_succeeds_with_settings_goes_on_to_create_one_session_and_run_the_flow()
    {
        OnlineEntrySettings given = Settings(clientVersion: 5, serverGroup: 6);
        var port = new SettingsPort { LoadReturns = true, LoadGives = given };

        (int exit, List<string> lines) = await Run(port, "--to-index 0 --client-version 42 --server-group 4");

        Assert.Equal(0, exit);
        Assert.Equal(1, port.LoadCalls);
        Assert.Equal(1, port.CreateCalls);
        Assert.Equal(42u, given.ClientVersion);                  // 命令行的值覆盖了预置的 5
        Assert.Equal((ushort)4, given.ServerGroup);              // 覆盖了预置的 6
        Assert.Equal("PROBE_SWITCH stage=settings to=index:0 outcome=Ok clientVersion=42 serverGroup=4", lines[0]);
        Assert.Equal(new[] { "login", "directory", "connect:" + FirstPort, "switch-host:" + HostB + ":" + PortB, "disconnect" }, port.Session.Calls);
        Assert.True(port.Session.Disposed);
    }
}
