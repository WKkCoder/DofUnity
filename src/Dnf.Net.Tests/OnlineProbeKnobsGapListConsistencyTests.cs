using Dnf.Net.ChannelDirectory;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 一致性:目录摘要里每个条目的 <c>group=</c>,等于真会话(<see cref="OnlineClientSession"/>,内存假网关 + 内存双向管道,不绑端口、不连任何服务)
/// 连那个端口时实际写进游戏登录请求的服务器组(设置里的服务器组为 0,按目录里该端口所属的组解出)。
/// 目录里每个条目的端口互不相同(摘要对"单个条目"算组,会话是按"端口"在整份目录里找条目的,端口不重复时两者一致);
/// 端口重复时的已知差异与"设置里的服务器组非 0 时会话写设置值、摘要仍给目录值"各有一个用例把行为写明。
/// 登录请求体按服务端的读序逐字段读出(与 OnlineSwitchRealSessionTests 同法)。令牌固定、重试间隔 0,没有睡眠。
/// </summary>
public sealed class OnlineProbeKnobsGapListConsistencyTests
{
    private static readonly string GatewayIp = GatewayTestSupport.Dots(198, 51, 100, 7);
    private static readonly string OtherIp = GatewayTestSupport.Dots(198, 51, 100, 8);
    private static readonly string SynthToken = Convert.ToBase64String(GatewayTestSupport.Pattern(46, 1));

    private const int BasePort = 47401;

    // 计划:每个条目一个组标签(各占一个组),主机在网关地址与另一个地址之间交替;端口 BasePort+i 互不相同
    private static readonly (string Tag, string Host)[] Plan =
    {
        ("cain", "gw"),                  // 0:映射 1
        ("Cain Server", "other"),        // 1:大小写与 server 后缀噪声,映射 1
        ("siroco", "gw"),                // 2:映射 3
        ("starter", "other"),            // 3:映射 14
        ("SYNTH-GROUP", "gw"),           // 4:解不出 → 0
        (string.Empty, "gw"),            // 5:空标签 → 0
        (" Khazan ", "other"),           // 6:首尾空白与大小写,映射 13
        ("ruke", "gw"),                  // 7:映射 7
        ("SYNTH-OTHER-GROUP", "other"),  // 8:解不出 → 0
    };

    // 手写的期望(由上面每个标签按映射表推出)
    private static readonly int[] ExpectedGroups = { 1, 1, 3, 14, 0, 0, 13, 7, 0 };

    private static ChannelDirectorySnapshot PlanSnapshot()
    {
        var groups = new ChannelWireGroup[Plan.Length];
        for (int i = 0; i < Plan.Length; i++)
        {
            string host = Plan[i].Host == "gw" ? GatewayIp : OtherIp;
            groups[i] = new ChannelWireGroup(Plan[i].Tag, new[] { new ChannelWireEntry("SYNTH-CH-" + (i + 1), 200, 10, host, BasePort + i) });
        }
        return ChannelDirectorySnapshot.Build("0", true, null, groups);
    }

    private static OnlineFlowRig NewRig(ChannelDirectorySnapshot snapshot, ushort settingsGroup = 0)
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ForceReplyPlaintext = "0|" + SynthToken;
        rig.FakeGateway.GameServerIp = GatewayIp;
        rig.Hooks.SwitchLoginRetryDelay = TimeSpan.Zero;
        rig.Settings.ChannelPort = FirstPort;
        rig.Settings.ServerGroup = settingsGroup;
        rig.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(ChannelDirectoryLoadResult.Success(snapshot, 0, true));
        return rig;
    }

    /// <summary>登录请求体里的服务器组(按服务端读序读到那里:补丁时间、令牌、账号、口令、模式、鼠标、数据版本、客户端版本、组)。</summary>
    private static ushort ReadLoginGroup(DungeonEntryClientFrame frame)
    {
        var r = new PacketReader(frame.Plain);
        Assert.True(r.TryGetUInt(out _));                                   // 补丁时间
        Assert.True(r.TryGetInt(out int tokenLength));
        Assert.True(r.TryGetBinary(tokenLength, out _));
        Assert.True(r.TryGetInt(out int userLength));
        Assert.True(r.TryGetBinary(userLength, out _));
        Assert.True(r.TryGetInt(out int passwordLength));
        Assert.True(r.TryGetBinary(passwordLength, out _));
        Assert.True(r.TryGetByte(out _));                                   // 登录模式
        Assert.True(r.TryGetInt(out int mouseLength));
        Assert.True(r.TryGetBinary(mouseLength, out _));
        Assert.True(r.TryGetUInt(out _));                                   // 数据版本
        Assert.True(r.TryGetUInt(out _));                                   // 客户端版本
        Assert.True(r.TryGetUShort(out ushort group));
        return group;
    }

    /// <summary>新会话:网关登录 → 读目录 → 连给定端口,返回连接结果;失败直接让断言失败。</summary>
    private static async Task<OnlineConnectResult> ConnectOnFreshSession(OnlineFlowRig rig, int port)
    {
        using OnlineClientSession session = rig.CreateSession();
        OnlineLoginResult login = await GatewayTestSupport.Bounded(session.LoginAsync(rig.Settings.Account, rig.Settings.Password), 10);
        Assert.True(login.IsSuccess, login.Failure.ToString());
        ChannelDirectoryLoadResult directory = await GatewayTestSupport.Bounded(session.LoadChannelDirectoryAsync(), 10);
        Assert.True(directory.Succeeded, directory.Outcome.ToString());
        OnlineConnectResult connected = await GatewayTestSupport.Bounded(session.ConnectAsync(port), 10);
        Assert.True(connected.IsSuccess, connected.Failure.ToString());
        return connected;
    }

    // 保护:对每个条目,摘要里的 group 等于真会话连该端口时写进游戏登录请求的组(也等于连接结果里报告的有效组);同时对手写的期望值
    [Fact]
    public async Task The_group_in_each_summary_line_is_the_group_the_real_session_writes_into_the_login_frame()
    {
        ChannelDirectorySnapshot snapshot = PlanSnapshot();
        OnlineFlowRig rig = NewRig(snapshot);
        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(snapshot.Channels, GatewayIp, FirstPort);
        Assert.Equal(Plan.Length, summary.Entries.Count);

        for (int i = 0; i < Plan.Length; i++)
        {
            OnlineConnectResult connected = await ConnectOnFreshSession(rig, BasePort + i);

            DungeonEntryClientFrame loginFrame = rig.Channels[i].Pipe.ClientFrames.First(f => f.Id == LoginRequest.PacketId);
            ushort written = ReadLoginGroup(loginFrame);
            Assert.Equal((ushort)ExpectedGroups[i], written);                          // 手写期望
            Assert.Equal(written, summary.Entries[i].ServerGroup);                     // 摘要与实际写入一致
            Assert.Equal(written, connected.EffectiveServerGroup);
        }
        Assert.Equal(Plan.Length, rig.Channels.Count);
    }

    // 保护:设置里的服务器组非 0 时,会话写的是设置值(与目录无关),而摘要仍给"目录解出的值"(按约定,摘要只回答设置组为 0 时会写什么);
    // list 流程不改设置,真会话上跑 list 时输出也不受设置组影响
    [Fact]
    public async Task A_non_zero_settings_group_wins_in_the_session_while_the_summary_still_shows_the_directory_value()
    {
        ChannelDirectorySnapshot snapshot = PlanSnapshot();
        OnlineFlowRig rig = NewRig(snapshot, settingsGroup: 9);
        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(snapshot.Channels, GatewayIp, FirstPort);

        await ConnectOnFreshSession(rig, BasePort + 2);                                // 目录里这个端口的组是 3

        DungeonEntryClientFrame loginFrame = rig.Channels[0].Pipe.ClientFrames.First(f => f.Id == LoginRequest.PacketId);
        Assert.Equal((ushort)9, ReadLoginGroup(loginFrame));
        Assert.Equal((ushort)3, summary.Entries[2].ServerGroup);

        var lines = new List<string>();
        OnlineFlowRig listRig = NewRig(snapshot, settingsGroup: 9);
        int exit = await GatewayTestSupport.Bounded(
            OnlineSwitchProbe.RunAsync(new[] { "--list" }, DefaultRoot, new ListRigPort(listRig), lines.Add), 20);
        Assert.Equal(0, exit);
        Assert.Equal("PROBE_SWITCH dir entry=2 usable=1 first=0 candidate=2 group=3 kind=-1", lines.Single(l => l.StartsWith("PROBE_SWITCH dir entry=2 ")));
        Assert.Equal((ushort)9, listRig.Settings.ServerGroup);                         // list 没有改设置
        Assert.Empty(listRig.Channels);
    }

    // 已知差异(产品文档里写明):端口重复时,会话按"端口"找条目——先认主机与连接地址相同的、再取第一个解得出的;摘要给的是每个条目自己的组。
    // 这里两个条目共用一个端口:甲条目(另一个主机,组 1)与乙条目(网关主机,组 3);会话连这个端口时写的是乙的 3,甲这一行摘要写 1 ≠ 3,乙这一行 = 3
    [Fact]
    public async Task With_a_repeated_port_the_session_picks_the_exact_host_entry_while_the_summary_gives_each_entry_its_own_group()
    {
        const int shared = BasePort + 50;
        ChannelDirectorySnapshot snapshot = ChannelDirectorySnapshot.Build("0", true, null, new[]
        {
            new ChannelWireGroup("cain", new[] { new ChannelWireEntry("SYNTH-CH-1", 200, 10, OtherIp, shared) }),
            new ChannelWireGroup("siroco", new[] { new ChannelWireEntry("SYNTH-CH-2", 200, 10, GatewayIp, shared) }),
        });
        OnlineFlowRig rig = NewRig(snapshot);
        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(snapshot.Channels, GatewayIp, FirstPort);

        await ConnectOnFreshSession(rig, shared);

        ushort written = ReadLoginGroup(rig.Channels[0].Pipe.ClientFrames.First(f => f.Id == LoginRequest.PacketId));
        Assert.Equal((ushort)3, written);
        Assert.Equal((ushort)1, summary.Entries[0].ServerGroup);                       // 与会话实际写的不同(已知差异)
        Assert.Equal((ushort)3, summary.Entries[1].ServerGroup);
    }

    private sealed class ListRigPort : IOnlineSwitchPort
    {
        private readonly OnlineFlowRig _rig;

        internal ListRigPort(OnlineFlowRig rig)
        {
            _rig = rig;
        }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            settings = _rig.Settings;
            problem = null;
            return true;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            return new OnlineClientSwitchSession(new OnlineClientSession(settings, null, null, _rig.Hooks));
        }
    }
}
