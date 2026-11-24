using System.Net;
using Dnf.Net.ChannelDirectory;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 子命令接到<b>真</b>会话上(内存假网关 + 内存双向管道,不绑端口、不连任何服务)的禁令验证:
/// 管道记下会话写出的每一帧,证明整个流程只发了"游戏登录(包号 1)"与连接流程自带的"角色列表请求(包号 8)",
/// 没有选角、没有进城、没有心跳、没有任何命令包;网关只登录一次;探针自己不重试(只有会话内部台账 E-443 的有限重试);
/// 目录不够时一个频道也不连。令牌固定、重试间隔置 0,没有睡眠。
/// </summary>
public sealed class OnlineSwitchRealSessionTests
{
    private static readonly string SynthToken = Convert.ToBase64String(GatewayTestSupport.Pattern(46, 1));
    private static readonly byte[][] Code1Rejection = HostTestKit.Frames(DungeonEntryWire.Reply(1, new byte[] { 0, 1, 0, 0 }));

    // 合成的两个"主机"(文档用地址段,用点分拼接避免源码里出现点分四段字面量)
    private static readonly string GatewayIp = GatewayTestSupport.Dots(198, 51, 100, 7);
    private static readonly string OtherIp = GatewayTestSupport.Dots(198, 51, 100, 8);

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script(byte[][] login = null)
    {
        return HostTestKit.TownScript(login ?? HostTestKit.LoginOk(), HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete());
    }

    /// <summary>把真会话接到 <see cref="IOnlineSwitchSession"/> 上的端口:读配置给假环境里的设置,拿会话给真会话。</summary>
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

    /// <summary>
    /// 新环境:令牌固定、重试间隔 0、频道端口是合成的第一个端口;网关给的游戏服地址是 <see cref="GatewayIp"/>。
    /// 目录三条:第一个频道本身(排除)、另一个主机上的乙端口(候选 0)、同主机的丙端口(候选 1);组标签默认映射到服务器组 3。
    /// </summary>
    private static (OnlineFlowRig Rig, RigPort Port, List<string> Addresses) NewEnv(string groupTag = "siroco")
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ForceReplyPlaintext = "0|" + SynthToken;
        rig.FakeGateway.GameServerIp = GatewayIp;
        rig.Hooks.SwitchLoginRetryDelay = TimeSpan.Zero;
        rig.Settings.ChannelPort = FirstPort;
        ChannelDirectorySnapshot snapshot = DirectorySnapshot(groupTag, (GatewayIp, FirstPort), (OtherIp, PortB), (GatewayIp, PortC));
        rig.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(ChannelDirectoryLoadResult.Success(snapshot, 0, true));

        // 记下每次连接用的地址(原有的假频道只记端口)
        var addresses = new List<string>();
        Func<IPAddress, int, CancellationToken, Task<OnlineChannelLink>> original = rig.Hooks.ChannelFactory;
        rig.Hooks.ChannelFactory = (address, port, token) =>
        {
            addresses.Add(address.ToString());
            return original(address, port, token);
        };
        return (rig, new RigPort(rig), addresses);
    }

    private static async Task<(int Exit, List<string> Lines)> Run(RigPort port, string line)
    {
        var lines = new List<string>();
        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(line.Split(' '), DefaultRoot, port, lines.Add), 30);
        return (exit, lines);
    }

    private static string Final(List<string> lines)
    {
        return System.Text.RegularExpressions.Regex.Replace(lines[^1], @"elapsedMs=\d+$", "elapsedMs=N");
    }

    /// <summary>登录请求体里的数据版本、客户端版本号与服务器组(按服务端读序读到那里)。</summary>
    private static (uint DataVersion, uint ClientVersion, ushort Group) ReadLogin(DungeonEntryClientFrame frame)
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
        Assert.True(r.TryGetUInt(out uint dataVersion));
        Assert.True(r.TryGetUInt(out uint clientVersion));
        Assert.True(r.TryGetUShort(out ushort group));
        return (dataVersion, clientVersion, group);
    }

    /// <summary>
    /// 禁令:每条频道上只允许出现类 1 的两种命令包——游戏登录(包号 1,每条频道至多 1 帧)与连接流程自带的角色列表请求(包号 8,至多 1 帧);
    /// 且写到线上的次数与解出的帧数一致(没有解不出来的字节)。选角(包号 4)、副本命令、心跳等一律不许出现。
    /// </summary>
    private static void AssertOnlyLoginAndListRequestFrames(OnlineFlowRig rig)
    {
        foreach (OnlineFlowChannel channel in rig.Channels)
        {
            DungeonEntryClientFrame[] frames = channel.Pipe.ClientFrames.ToArray();
            Assert.All(frames, f =>
            {
                Assert.Equal(PacketClass.Cmd, f.Class);
                Assert.True(f.Id == LoginRequest.PacketId || f.Id == 8, "unexpected outbound packet id " + f.Id);
            });
            Assert.True(frames.Count(f => f.Id == LoginRequest.PacketId) <= 1);
            Assert.True(frames.Count(f => f.Id == 8) <= 1);
            Assert.Equal(frames.Length, channel.Recording.Writes.Count);
        }
    }

    // ======== 成功路径 ========

    [Fact]
    public async Task A_good_switch_sends_only_the_login_and_list_request_frames_and_logs_in_to_the_gateway_once()
    {
        (OnlineFlowRig rig, RigPort port, List<string> addresses) = NewEnv();

        (int exit, List<string> lines) = await Run(port, "--to-index 0 --client-version 42435");

        Assert.Equal(0, exit);
        Assert.Equal(2, rig.Channels.Count);                                // 第一个频道加换过去的那个,没有第三条
        foreach (OnlineFlowChannel channel in rig.Channels)
        {
            // 每条频道上恰好两帧:游戏登录(1)与角色列表请求(8)
            Assert.Equal(new ushort[] { 1, 8 }, channel.Pipe.ClientFrames.Select(f => f.Id).ToArray());
            Assert.Equal(2, channel.Recording.Writes.Count);
            (uint dataVersion, uint clientVersion, ushort group) = ReadLogin(channel.Pipe.ClientFrames.First(f => f.Id == LoginRequest.PacketId));
            Assert.Equal(900u, dataVersion);                                // 合成设置里的数据版本
            Assert.Equal(42435u, clientVersion);                            // 0xA5C3,两次登录都带着
            Assert.Equal((ushort)3, group);                                 // 设置里是 0:按目录里该端口所属的组解出(合成组标签映射到 3)
            Assert.Equal(1, channel.Connection.DisposeCount);               // 连接都关了
        }
        AssertOnlyLoginAndListRequestFrames(rig);
        Assert.Equal(1, rig.FakeGateway.PostCount);                         // 网关只登录一次
        Assert.Equal(1, rig.FakeGateway.GetCount);                          // 外加一次取游戏服地址
        Assert.Equal(new[] { FirstPort, PortB }, rig.RequestedPorts);
        Assert.Equal(new[] { GatewayIp, OtherIp }, addresses);              // 候选 0 在另一个主机上:走了带主机的重载
        Assert.Equal(1, port.CreateCalls);
        Assert.Equal(OnlineClientSessionState.Disposed, port.Session.State);
        Assert.Contains(lines, l => l.Contains(" stage=connect ") && l.Contains(" characterList=Received characters=3 "));
        Assert.Contains(lines, l => l.Contains(" stage=switch ") && l.Contains(" characterList=Received characters=3 "));
        Assert.Equal("PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=1 group1=3 group2=3 clientVersion=42435 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task To_index_one_switches_to_another_port_on_the_gateway_host()
    {
        (OnlineFlowRig rig, RigPort port, List<string> addresses) = NewEnv();

        (int exit, _) = await Run(port, "--to-index 1");

        Assert.Equal(0, exit);
        Assert.Equal(new[] { FirstPort, PortC }, rig.RequestedPorts);
        Assert.Equal(new[] { GatewayIp, GatewayIp }, addresses);
        AssertOnlyLoginAndListRequestFrames(rig);
    }

    [Fact]
    public async Task To_port_switches_on_the_gateway_host_whatever_the_directory_holds()
    {
        (OnlineFlowRig rig, RigPort port, List<string> addresses) = NewEnv();

        (int exit, List<string> lines) = await Run(port, "--to-port " + TargetPort);

        Assert.Equal(0, exit);
        Assert.Equal(new[] { FirstPort, TargetPort }, rig.RequestedPorts);
        Assert.Equal(new[] { GatewayIp, GatewayIp }, addresses);
        AssertOnlyLoginAndListRequestFrames(rig);
        // 目录里没有这个端口,所以第二个频道的服务器组解不出来,是 0;第一个频道在目录里,是 3
        Assert.Equal("PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=1 group1=3 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task The_server_group_knob_reaches_both_login_frames_and_the_report()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();

        (int exit, List<string> lines) = await Run(port, "--to-index 0 --server-group 5");

        Assert.Equal(0, exit);
        foreach (OnlineFlowChannel channel in rig.Channels)
        {
            Assert.Equal((ushort)5, ReadLogin(channel.Pipe.ClientFrames.First(f => f.Id == LoginRequest.PacketId)).Group);
        }
        Assert.Equal("PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=1 group1=5 group2=5 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task Without_the_knobs_the_login_frames_carry_version_zero()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();

        await Run(port, "--to-index 0");

        foreach (OnlineFlowChannel channel in rig.Channels)
        {
            Assert.Equal(0u, ReadLogin(channel.Pipe.ClientFrames.First(f => f.Id == LoginRequest.PacketId)).ClientVersion);
        }
    }

    // ======== 只有会话内部的有限重试,探针自己不重试 ========

    [Fact]
    public async Task A_rejection_inside_the_settle_window_is_retried_once_by_the_session_and_reported_as_two_attempts()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();
        rig.ScriptFactory = ch => ch == 1 ? Script(Code1Rejection) : Script();     // 换频道的第一次被拒,会话内部重试一次就过

        (int exit, List<string> lines) = await Run(port, "--to-port " + PortB);

        Assert.Equal(0, exit);
        Assert.Equal(3, rig.Channels.Count);                                // 第一个频道 + 被拒的 + 重试通过的
        Assert.Equal(1, rig.FakeGateway.PostCount);
        AssertOnlyLoginAndListRequestFrames(rig);
        Assert.Equal("PROBE_SWITCH result=ok first=Ok second=Ok attempts1=1 attempts2=2 group1=3 group2=3 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task When_every_retry_is_rejected_only_the_sessions_own_budget_is_spent_and_the_probe_adds_none()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();
        rig.ScriptFactory = ch => ch == 0 ? Script() : Script(Code1Rejection);

        (int exit, List<string> lines) = await Run(port, "--to-port " + PortB);

        Assert.Equal(4, exit);
        // 1 个第一频道 + 换频道首发 1 次 + 会话内部台账 E-443 的 6 次重试 = 8 次连接;探针没有再加任何一次
        Assert.Equal(8, rig.RequestedPorts.Count);
        Assert.Equal(1, rig.FakeGateway.PostCount);                         // 重试不回网关
        AssertOnlyLoginAndListRequestFrames(rig);
        // 7 = 1 次首发 + 6 次重试(默认上限);每份请求里的组都是按目录里该端口解出的 3
        Assert.Equal("PROBE_SWITCH result=fail first=Ok second=GameLoginRejected attempts1=1 attempts2=7 group1=3 group2=3 clientVersion=0 elapsedMs=N", Final(lines));
        Assert.Contains(lines, l => l.Contains(" stage=switch ") && l.Contains(" loginFailureCode=1 "));
    }

    // ======== 失败路径:退出码、不再多连、网关不重试 ========

    [Fact]
    public async Task A_first_channel_that_cannot_be_reached_is_tried_exactly_once_and_nothing_is_switched()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();
        rig.FailChannel = _ => true;

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(3, exit);
        Assert.Single(rig.RequestedPorts);                                  // 只试了一次
        Assert.Empty(rig.Channels);
        Assert.Equal(1, rig.FakeGateway.PostCount);
        Assert.Equal("PROBE_SWITCH result=fail first=ConnectFailed second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task A_directory_with_too_few_candidates_connects_nothing_at_all()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();

        (int exit, List<string> lines) = await Run(port, "--to-index 2");      // 候选只有 0、1 两个

        Assert.Equal(7, exit);
        Assert.Empty(rig.RequestedPorts);                                   // 没有任何频道连接尝试
        Assert.Empty(rig.Channels);
        Assert.Equal(1, rig.FakeGateway.PostCount);                         // 读目录需要登录,网关登录了一次
        Assert.Equal("PROBE_SWITCH result=fail first=NotRun second=NotRun attempts1=0 attempts2=0 group1=0 group2=0 clientVersion=0 elapsedMs=N", Final(lines));
    }

    [Fact]
    public async Task A_directory_failure_connects_nothing()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();
        rig.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "SYNTH"));

        (int exit, _) = await Run(port, "--to-port " + TargetPort);

        Assert.Equal(6, exit);
        Assert.Empty(rig.RequestedPorts);
        Assert.Equal(1, rig.FakeGateway.PostCount);
    }

    [Fact]
    public async Task A_gateway_rejection_ends_at_once_with_exit_5_and_the_gateway_is_asked_only_once()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();
        rig.FakeGateway.ForceReplyPlaintext = null;                         // 固定回复会跳过口令校验,这里要真的校验
        rig.Settings.Password = "SYNTH-WRONG-PASSWORD";                     // 假网关里账号对应的口令摘要不匹配

        (int exit, List<string> lines) = await Run(port, "--to-index 0");

        Assert.Equal(5, exit);
        Assert.Equal(1, rig.FakeGateway.PostCount);                         // 绝不重试
        Assert.Equal(0, rig.FakeGateway.GetCount);                          // 登录没过就不取游戏服地址
        Assert.Empty(rig.RequestedPorts);
        Assert.Contains(lines, l => l.StartsWith("PROBE_SWITCH stage=gateway-login to=index:0 outcome=Failed failure=GatewayLoginFailed "));
        Assert.DoesNotContain("SYNTH-WRONG-PASSWORD", string.Join("\n", lines));
    }

    [Fact]
    public async Task A_gateway_that_answers_with_an_http_error_is_not_asked_again()
    {
        (OnlineFlowRig rig, RigPort port, _) = NewEnv();
        rig.FakeGateway.ForceStatus = 500;

        (int exit, _) = await Run(port, "--to-index 0");

        Assert.Equal(5, exit);
        Assert.Equal(1, rig.FakeGateway.PostCount);
        Assert.Empty(rig.RequestedPorts);
    }

    // ======== 适配器 ========

    [Fact]
    public async Task The_adapter_shows_the_gateway_host_only_after_the_login_and_forwards_the_client_version()
    {
        (OnlineFlowRig rig, _, _) = NewEnv();
        rig.Settings.ClientVersion = 42435;
        using var session = new OnlineClientSwitchSession(rig.CreateSession());

        Assert.Null(session.GameServerHost);                                // 还没登录
        Assert.Equal(42435u, session.ClientVersion);                        // 会话构造时复制了设置里的版本号

        OnlineLoginResult login = await GatewayTestSupport.Bounded(
            session.LoginAsync(rig.Settings.Account, rig.Settings.Password, CancellationToken.None), 10);

        Assert.True(login.IsSuccess);
        Assert.Equal(GatewayIp, session.GameServerHost);
    }

    [Fact]
    public void The_adapter_refuses_a_missing_session()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineClientSwitchSession(null));
    }
}
