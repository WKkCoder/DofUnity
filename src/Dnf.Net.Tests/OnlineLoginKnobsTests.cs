using Dnf.Net.ChannelDirectory;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网层三个"开关"的可观察性(真服验证用):
/// (1) 客户端版本号(台账 E-441,登录请求里数据版本之后的 u32):设置项、会话复制、会话属性、在连频道与换频道两条路上写到正确位置,缺省时发出的帧逐字节不变;
/// (2) 实际写进登录请求的服务器组(设置非零原样,设置为 0 时按最近目录按端口解出,解不出为 0);
/// (3) 游戏登录实际发出的次数(含台账 E-443 结算窗口重试)。
/// 全部跑在内存假网关与内存双向管道上,不绑端口、不连服务;令牌固定成合成值,让整帧字节在多次运行间可比较;
/// 重试间隔置 0、"窗口外"用 1 个刻度的窗口模拟,没有任何睡眠。
/// </summary>
public sealed class OnlineLoginKnobsTests
{
    /// <summary>合成的客户端版本号:0xA5C3 = 42435(远大于一个字节,四个字节各不相同,位置错一位就读不回来)。</summary>
    private const uint SynthVersion = 0x0000A5C3u;

    /// <summary>
    /// 在改动之前的产品代码上,用下面 <see cref="NewRig"/> 的同一脚本(连频道 0 后换到频道 1)抓到的登录帧整帧字节(173 字节,十六进制):
    /// 账号 test900、口令 pw900、数据版本 900、服务器组 0、本机地址 0、令牌是 <see cref="SynthToken"/>,全是合成值。
    /// 缺省(不设版本号)时现在发出的整帧必须与它逐字节相同。
    /// </summary>
    private const string PreChangeLoginFrameHex =
        "010100AD0000000B5AE5290000300B0710AEE5F1CE958EEAF0F8C0C0DD727C605AAD828283908E97E4FFC3D0C975735646A88D9297938DCCD6F2C3A5FF78724678A78CBCA59E8CFADAF1C2FEEB7B727064A282CCF3D3DFA3B4CEE4E6EE093B3730EEE5F1FDE7ECC1878BE5A2AE053E61288FDDC9F7E7E7C08CD8B6A6A2083D61758CD495CEDCDFA3B4BA81959A300B07106AE6F1CED4DFA3B4BA81959A300B0710EEE5F1CED4DFA3B4BA81959A";

    private static readonly string SynthToken = Convert.ToBase64String(GatewayTestSupport.Pattern(46, 1));

    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static readonly byte[][] Code1Rejection = HostTestKit.Frames(DungeonEntryWire.Reply(1, new byte[] { 0, 1, 0, 0 }));

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script(byte[][] login = null)
    {
        return HostTestKit.TownScript(login ?? HostTestKit.LoginOk(), HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete());
    }

    /// <summary>新的内存环境:令牌固定、重试间隔 0(不睡眠);其余沿用 <see cref="OnlineFlowRig"/>(重试上限 6、结算窗口 5 秒)。</summary>
    private static OnlineFlowRig NewRig()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ForceReplyPlaintext = "0|" + SynthToken;
        rig.Hooks.SwitchLoginRetryDelay = TimeSpan.Zero;
        return rig;
    }

    private static DungeonEntryClientFrame[] LoginFrames(OnlineFlowRig rig, int channelIndex)
    {
        return rig.Channels[channelIndex].Pipe.ClientFrames.Where(f => f.Class == PacketClass.Cmd && f.Id == LoginRequest.PacketId).ToArray();
    }

    private static int TotalLoginFrames(OnlineFlowRig rig)
    {
        return Enumerable.Range(0, rig.Channels.Count).Sum(i => LoginFrames(rig, i).Length);
    }

    /// <summary>登录请求体各字段(按服务端读序读回)。</summary>
    private sealed class LoginFields
    {
        public uint PatchTime;
        public string Token;
        public string UserName;
        public string PasswordBlock;
        public byte Mode;
        public byte[] MouseCipher;
        public uint DataVersion;
        public uint ClientParam;
        public ushort ServerGroup;
        public uint LocalAddress;
        public int Consumed;
    }

    /// <summary>
    /// 与服务端读序一致,逐字段读回(同 <c>LoginRequestTests</c> 的读法):u32 补丁时间、u32 令牌长度加令牌、u32 用户名长度加用户名、
    /// u32 口令块长度加口令块、u8 登录模式、u32 密文长度加 8 字节密文、u32 数据版本、u32 客户端版本号、u16 服务器组、u32 本机地址。
    /// </summary>
    private static LoginFields ReadLogin(DungeonEntryClientFrame frame)
    {
        var r = new PacketReader(frame.Plain);
        var f = new LoginFields();
        Assert.True(r.TryGetUInt(out f.PatchTime));
        Assert.True(r.TryGetInt(out int tokenLength));
        Assert.True(r.TryGetBinary(tokenLength, out byte[] token));
        f.Token = System.Text.Encoding.ASCII.GetString(token);
        Assert.True(r.TryGetInt(out int userLength));
        Assert.True(r.TryGetBinary(userLength, out byte[] user));
        f.UserName = System.Text.Encoding.ASCII.GetString(user);
        Assert.True(r.TryGetInt(out int passwordLength));
        Assert.True(r.TryGetBinary(passwordLength, out byte[] password));
        f.PasswordBlock = System.Text.Encoding.ASCII.GetString(password);
        Assert.True(r.TryGetByte(out f.Mode));
        Assert.True(r.TryGetInt(out int mouseLength));
        Assert.Equal(LoginRequest.MouseCipherBytes, mouseLength);
        Assert.True(r.TryGetBinary(mouseLength, out f.MouseCipher));
        Assert.True(r.TryGetUInt(out f.DataVersion));
        Assert.True(r.TryGetUInt(out f.ClientParam));
        Assert.True(r.TryGetUShort(out f.ServerGroup));
        Assert.True(r.TryGetUInt(out f.LocalAddress));
        f.Consumed = r.Position;
        return f;
    }

    private static ChannelDirectorySnapshot Directory(string groupTag, int port)
    {
        return ChannelDirectorySnapshot.Build("0", true, null, new[]
        {
            new ChannelWireGroup(groupTag, new[] { new ChannelWireEntry("#ch.52", 200, 10, string.Empty, port) }),
        });
    }

    private static void ServeDirectory(OnlineFlowRig rig, ChannelDirectorySnapshot snapshot)
    {
        rig.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(ChannelDirectoryLoadResult.Success(snapshot, 0, true));
    }

    // ======== 一、客户端版本号 ========

    [Fact]
    public async Task Client_version_lands_between_data_version_and_server_group_on_both_connect_and_switch()
    {
        var rig = NewRig();
        rig.Settings.ClientVersion = SynthVersion;
        rig.Settings.ServerGroup = 5;

        (OnlineClientSession client, OnlineConnectResult connected) = await rig.Connected();
        Assert.True(connected.IsSuccess);
        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));
        Assert.True(switched.IsSuccess, switched.Failure.ToString());

        Assert.Equal(2, rig.Channels.Count);
        for (int channel = 0; channel < 2; channel++)
        {
            DungeonEntryClientFrame frame = Assert.Single(LoginFrames(rig, channel));
            LoginFields f = ReadLogin(frame);

            // 逐字段读回:数据版本 900(HostTestKit 的合成值)、版本号 0xA5C3、服务器组 5、本机地址 0,一个字段不错位
            Assert.Equal(0u, f.PatchTime);
            Assert.Equal(SynthToken, f.Token);
            Assert.Equal("test900", f.UserName);
            Assert.Equal(32, f.PasswordBlock.Length);
            Assert.Equal((byte)0, f.Mode);
            Assert.Equal(new byte[8], f.MouseCipher);
            Assert.Equal(900u, f.DataVersion);
            Assert.Equal(SynthVersion, f.ClientParam);
            Assert.Equal((ushort)5, f.ServerGroup);
            Assert.Equal(0u, f.LocalAddress);

            // 正好读完:4 + (4+64) + (4+7) + (4+32) + 1 + (4+8) + 4 + 4 + 2 + 4 = 146 字节(令牌 46 字节的 base64 是 64 个字符;口令块是 32 位十六进制)
            Assert.Equal(146, f.Consumed);

            // 按偏移数字段的第二种写法(不用读取器):版本号在 4+68+11+36+1+12+4 = 136 处,小端 C3 A5 00 00;其后 140 处是服务器组 05 00
            Assert.Equal(new byte[] { 0xC3, 0xA5, 0x00, 0x00 }, frame.Plain.Skip(136).Take(4).ToArray());
            Assert.Equal(new byte[] { 0x05, 0x00 }, frame.Plain.Skip(140).Take(2).ToArray());
        }
    }

    [Fact]
    public async Task Client_version_is_also_written_by_the_host_overloads_of_connect_and_switch()
    {
        var rig = NewRig();
        rig.Settings.ClientVersion = SynthVersion;
        OnlineClientSession client = await rig.LoggedIn();

        // 文本主机重载:null 主机退回网关给的地址;这条路与"只收端口"的重载共用同一段连接代码,版本号照样带上
        Assert.True((await Bounded(client.ConnectAsync(null, 9010))).IsSuccess);
        Assert.True((await Bounded(client.SwitchChannelAsync(null, 9020))).IsSuccess);

        Assert.Equal(SynthVersion, ReadLogin(Assert.Single(LoginFrames(rig, 0))).ClientParam);
        Assert.Equal(SynthVersion, ReadLogin(Assert.Single(LoginFrames(rig, 1))).ClientParam);
    }

    [Fact]
    public async Task Unset_and_explicit_zero_send_identical_bytes_and_match_the_pre_change_frame()
    {
        // 同一合成脚本跑两遍:一遍完全不设,一遍显式设 0;连频道 0 再换到频道 1,逐帧逐字节比较所有出站帧
        var unset = NewRig();
        var zero = NewRig();
        zero.Settings.ClientVersion = 0;
        foreach (OnlineFlowRig rig in new[] { unset, zero })
        {
            (OnlineClientSession client, _) = await rig.Connected();
            Assert.True((await Bounded(client.SwitchChannelAsync(9020))).IsSuccess);
        }

        Assert.Equal(2, unset.Channels.Count);
        Assert.Equal(unset.Channels.Count, zero.Channels.Count);
        for (int channel = 0; channel < unset.Channels.Count; channel++)
        {
            IReadOnlyList<byte[]> a = unset.Channels[channel].Recording.Writes;
            IReadOnlyList<byte[]> b = zero.Channels[channel].Recording.Writes;
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i], b[i]);
            }
        }

        // 与改动之前的产品代码一致:登录帧是改动前抓到的整帧字面量(两个频道上的登录帧都一样,因为令牌与设置都没变)
        byte[] expected = Convert.FromHexString(PreChangeLoginFrameHex);
        Assert.Equal(173, expected.Length);
        Assert.Equal(expected, unset.Channels[0].Recording.Writes[0]);
        Assert.Equal(expected, unset.Channels[1].Recording.Writes[0]);

        // 缺省时版本号位置上是四个 0 字节
        Assert.Equal(0u, ReadLogin(Assert.Single(LoginFrames(unset, 0))).ClientParam);
    }

    [Fact]
    public async Task A_non_zero_version_changes_exactly_the_version_bytes_of_the_login_frame_plain_body()
    {
        // 与缺省比:明文体只在版本号那四个字节上不同(位置 136..139),其余逐字节相同;不连带改别的字段
        var baseline = NewRig();
        var versioned = NewRig();
        versioned.Settings.ClientVersion = SynthVersion;
        foreach (OnlineFlowRig rig in new[] { baseline, versioned })
        {
            (_, OnlineConnectResult connected) = await rig.Connected();
            Assert.True(connected.IsSuccess);
        }

        byte[] a = Assert.Single(LoginFrames(baseline, 0)).Plain;
        byte[] b = Assert.Single(LoginFrames(versioned, 0)).Plain;
        Assert.Equal(a.Length, b.Length);
        int[] differing = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray();
        // 缺省 00 00 00 00 与 C3 A5 00 00 只在前两个字节上不同:偏移 136 与 137
        Assert.Equal(new[] { 136, 137 }, differing);
    }

    [Fact]
    public async Task Session_copies_the_client_version_from_settings_and_owns_its_copy()
    {
        var rig = NewRig();
        rig.Settings.ClientVersion = SynthVersion;
        OnlineClientSession client = rig.CreateSession();
        Assert.Equal(SynthVersion, client.ClientVersion);

        // 构造之后再改设置对象,不影响会话(会话持有自己的副本,与其余字段一致)
        rig.Settings.ClientVersion = 77;
        Assert.Equal(SynthVersion, client.ClientVersion);

        Assert.True((await Bounded(client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password))).IsSuccess);
        Assert.True((await Bounded(client.ConnectAsync(9010))).IsSuccess);
        Assert.Equal(SynthVersion, ReadLogin(Assert.Single(LoginFrames(rig, 0))).ClientParam);
    }

    [Fact]
    public async Task Changing_the_session_property_takes_effect_on_the_next_connect_and_the_next_switch()
    {
        var rig = NewRig();
        OnlineClientSession client = await rig.LoggedIn();
        Assert.Equal(0u, client.ClientVersion);

        client.ClientVersion = 0x1234;
        Assert.Equal(0x1234u, client.ClientVersion);
        Assert.True((await Bounded(client.ConnectAsync(9010))).IsSuccess);

        client.ClientVersion = SynthVersion;
        Assert.True((await Bounded(client.SwitchChannelAsync(9020))).IsSuccess);

        client.ClientVersion = 0;
        Assert.True((await Bounded(client.SwitchChannelAsync(9030))).IsSuccess);

        Assert.Equal(0x1234u, ReadLogin(Assert.Single(LoginFrames(rig, 0))).ClientParam);
        Assert.Equal(SynthVersion, ReadLogin(Assert.Single(LoginFrames(rig, 1))).ClientParam);
        Assert.Equal(0u, ReadLogin(Assert.Single(LoginFrames(rig, 2))).ClientParam);
        // 设置对象本身没被会话上的属性改动
        Assert.Equal(0u, rig.Settings.ClientVersion);
    }

    [Fact]
    public async Task The_largest_version_is_written_in_full_and_does_not_disturb_the_neighbours()
    {
        var rig = NewRig();
        rig.Settings.ClientVersion = uint.MaxValue;
        rig.Settings.ServerGroup = 17;
        (_, OnlineConnectResult connected) = await rig.Connected();
        Assert.True(connected.IsSuccess);

        LoginFields f = ReadLogin(Assert.Single(LoginFrames(rig, 0)));
        Assert.Equal(900u, f.DataVersion);
        Assert.Equal(uint.MaxValue, f.ClientParam);      // 4294967295 = FF FF FF FF
        Assert.Equal((ushort)17, f.ServerGroup);         // 0x11 = 服务器组上限
        Assert.Equal(0u, f.LocalAddress);
    }

    [Fact]
    public async Task Retry_attempts_after_a_settle_window_rejection_carry_the_version_too()
    {
        // 换频道时第一次被拒(码 1)、重试成功:被拒的那份与重试的那份登录请求里都带版本号
        var rig = NewRig();
        rig.Settings.ClientVersion = SynthVersion;
        rig.ScriptFactory = ch => ch == 1 ? Script(Code1Rejection) : Script();
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.True(switched.IsSuccess, switched.Failure.ToString());
        Assert.Equal(3, rig.Channels.Count);
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.Equal(SynthVersion, ReadLogin(Assert.Single(LoginFrames(rig, channel))).ClientParam);
        }
    }

    // ======== 二、游戏登录次数 ========

    [Fact]
    public async Task Attempts_is_one_when_the_login_passes_on_connect_and_on_switch()
    {
        var rig = NewRig();
        (OnlineClientSession client, OnlineConnectResult connected) = await rig.Connected();
        Assert.Equal(1, connected.GameLoginAttempts);

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.True(switched.IsSuccess);
        Assert.Equal(1, switched.GameLoginAttempts);
        Assert.Equal(2, TotalLoginFrames(rig));
    }

    [Fact]
    public async Task Attempts_is_two_after_one_settle_window_rejection_with_code_1_then_success()
    {
        var rig = NewRig();
        rig.ScriptFactory = ch => ch == 1 ? Script(Code1Rejection) : Script();
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.True(switched.IsSuccess, switched.Failure.ToString());
        Assert.Equal(2, switched.GameLoginAttempts);            // 1 + 重试 1 次
        Assert.Equal(3, rig.Channels.Count);                    // 初始连接 + 被拒的一次 + 重试成功的一次
        Assert.Equal(3, TotalLoginFrames(rig));
    }

    [Fact]
    public async Task Attempts_is_one_plus_the_retry_limit_when_every_retry_is_rejected()
    {
        var rig = NewRig();
        Assert.Equal(6, rig.Hooks.SwitchLoginRetryCount);       // 产品缺省的上限(台账 E-443:最多 6 次)
        rig.ScriptFactory = ch => ch == 0 ? Script() : Script(Code1Rejection);
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.False(switched.IsSuccess);
        Assert.Equal(OnlineFailure.GameLoginRejected, switched.Failure);
        Assert.Equal((byte)1, switched.LoginFailureCode);
        Assert.Equal(7, switched.GameLoginAttempts);            // 1 + 6
        Assert.Equal(1 + 7, rig.Channels.Count);                // 初始连接 + 七次换频道连接
        Assert.Equal(1 + 7, TotalLoginFrames(rig));
    }

    [Fact]
    public async Task Attempts_follow_a_smaller_retry_limit()
    {
        var rig = NewRig();
        rig.Hooks.SwitchLoginRetryCount = 3;
        rig.ScriptFactory = ch => ch == 0 ? Script() : Script(Code1Rejection);
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.False(switched.IsSuccess);
        Assert.Equal(4, switched.GameLoginAttempts);            // 1 + 3
        Assert.Equal(1 + 4, rig.Channels.Count);
    }

    [Fact]
    public async Task Attempts_is_zero_when_the_channel_connection_fails_before_any_login_is_sent()
    {
        var rig = NewRig();
        rig.FailChannel = index => index == 1;
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.False(switched.IsSuccess);
        Assert.Equal(OnlineFailure.ConnectFailed, switched.Failure);
        Assert.Equal(0, switched.GameLoginAttempts);
        Assert.Equal((ushort)0, switched.EffectiveServerGroup);
        Assert.Equal(1, TotalLoginFrames(rig));                 // 只有最初连接的那份
    }

    [Fact]
    public async Task Attempts_is_zero_when_the_connection_closes_before_the_bootstrap_arrives()
    {
        var rig = NewRig();
        rig.CloseBeforeBootstrap = _ => true;
        rig.BootstrapFactory = _ => null;
        OnlineClientSession client = await rig.LoggedIn();

        OnlineConnectResult connected = await Bounded(client.ConnectAsync(9010));

        Assert.False(connected.IsSuccess);
        Assert.Equal(OnlineStage.Bootstrap, connected.Stage);
        Assert.Equal(0, connected.GameLoginAttempts);
        Assert.Equal((ushort)0, connected.EffectiveServerGroup);
        Assert.Equal(0, TotalLoginFrames(rig));
    }

    [Fact]
    public async Task Attempts_is_zero_when_the_login_request_fails_the_local_check_and_nothing_is_sent()
    {
        var rig = NewRig();
        rig.Settings.ServerGroup = 18;                          // 0x11 + 1:本地检查不过,请求不会发出
        OnlineClientSession client = await rig.LoggedIn();

        OnlineConnectResult connected = await Bounded(client.ConnectAsync(9010));

        Assert.False(connected.IsSuccess);
        Assert.Equal(OnlineFailure.BadLoginRequest, connected.Failure);
        Assert.Equal(0, connected.GameLoginAttempts);
        Assert.Equal((ushort)0, connected.EffectiveServerGroup);
        Assert.Equal(0, TotalLoginFrames(rig));
    }

    [Fact]
    public async Task Attempts_is_one_when_the_rejection_comes_outside_the_settle_window_and_nothing_is_retried()
    {
        var rig = NewRig();
        // 窗口缩到 1 个刻度(100 纳秒):关连接到下一次连接之间的真实耗时远超它,等价于"已超过 5 秒窗口";不睡眠
        rig.Hooks.LogoutSettleWindow = TimeSpan.FromTicks(1);
        rig.ScriptFactory = ch => ch == 0 ? Script() : Script(Code1Rejection);
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.False(switched.IsSuccess);
        Assert.Equal((byte)1, switched.LoginFailureCode);
        Assert.Equal(1, switched.GameLoginAttempts);
        Assert.Equal(2, rig.Channels.Count);                    // 没有第三条连接:没重试
    }

    [Fact]
    public async Task Attempts_is_one_for_a_fresh_login_rejection_and_for_a_non_1_rejection_code()
    {
        // 刚网关登录后的首次连接被拒码 1:没有"刚关掉已通过登录的连接",不重试
        var fresh = NewRig();
        fresh.ScriptFactory = _ => Script(Code1Rejection);
        OnlineClientSession freshClient = await fresh.LoggedIn();
        OnlineConnectResult freshResult = await Bounded(freshClient.ConnectAsync(9010));
        Assert.Equal((byte)1, freshResult.LoginFailureCode);
        Assert.Equal(1, freshResult.GameLoginAttempts);
        Assert.Single(fresh.Channels);

        // 换频道时被拒码 0x17(数据版本不对):窗口内也只对码 1 重试
        var other = NewRig();
        other.ScriptFactory = ch => ch == 0 ? Script() : Script(HostTestKit.Frames(DungeonEntryWire.Reply(1, new byte[] { 0, 0x17, 0, 0 })));
        (OnlineClientSession otherClient, _) = await other.Connected();
        OnlineConnectResult otherResult = await Bounded(otherClient.SwitchChannelAsync(9020));
        Assert.Equal((byte)0x17, otherResult.LoginFailureCode);
        Assert.Equal(1, otherResult.GameLoginAttempts);
        Assert.Equal(2, other.Channels.Count);
    }

    [Fact]
    public async Task A_rejection_on_the_first_connect_after_the_gateway_login_is_never_retried_whatever_the_window()
    {
        // 没有"刚关掉的、已通过游戏登录的连接"就谈不上结算窗口:就算把窗口放到 100 年(36500 天),
        // 刚网关登录后的首次连接被拒码 1 也只发一次。这条守住"关连接的时间戳不为 0"这一前提(变异检验里它曾是存活的)
        var rig = NewRig();
        rig.Hooks.LogoutSettleWindow = TimeSpan.FromDays(36500);
        rig.ScriptFactory = _ => Script(Code1Rejection);
        OnlineClientSession client = await rig.LoggedIn();

        OnlineConnectResult result = await Bounded(client.ConnectAsync(9010));

        Assert.Equal((byte)1, result.LoginFailureCode);
        Assert.Equal(1, result.GameLoginAttempts);               // 1 次:没有重试
        Assert.Single(rig.Channels);
    }

    // ======== 三、实际写进登录请求的服务器组 ========

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    public async Task An_explicit_non_zero_group_is_reported_as_is_and_matches_the_frame(int group)
    {
        var rig = NewRig();
        rig.Settings.ServerGroup = (ushort)group;
        (OnlineClientSession client, OnlineConnectResult connected) = await rig.Connected();

        Assert.Equal((ushort)group, connected.EffectiveServerGroup);
        Assert.Equal((ushort)group, ReadLogin(Assert.Single(LoginFrames(rig, 0))).ServerGroup);

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));
        Assert.Equal((ushort)group, switched.EffectiveServerGroup);
        Assert.Equal((ushort)group, ReadLogin(Assert.Single(LoginFrames(rig, 1))).ServerGroup);
    }

    [Fact]
    public async Task A_zero_setting_resolves_the_group_from_the_latest_directory_by_port_on_connect_and_switch()
    {
        var rig = NewRig();
        ServeDirectory(rig, Directory("siroco", 30052));        // siroco 组映射到 3(与 OnlineClientSessionTests 里同一例)
        OnlineClientSession client = await rig.LoggedIn();
        Assert.True((await Bounded(client.LoadChannelDirectoryAsync())).Succeeded);
        Assert.Equal((ushort)0, client.ServerGroup);            // 设置里仍是 0:报告的是最终值,不是设置值

        OnlineConnectResult connected = await Bounded(client.ConnectAsync(30052));
        Assert.True(connected.IsSuccess);
        Assert.Equal((ushort)3, connected.EffectiveServerGroup);
        Assert.Equal((ushort)3, ReadLogin(Assert.Single(LoginFrames(rig, 0))).ServerGroup);

        // 换到目录里没有的端口:解不出,回到 0
        OnlineConnectResult elsewhere = await Bounded(client.SwitchChannelAsync(9020));
        Assert.True(elsewhere.IsSuccess);
        Assert.Equal((ushort)0, elsewhere.EffectiveServerGroup);
        Assert.Equal((ushort)0, ReadLogin(Assert.Single(LoginFrames(rig, 1))).ServerGroup);

        // 再换回目录里的端口:又解出 3
        OnlineConnectResult back = await Bounded(client.SwitchChannelAsync(30052));
        Assert.Equal((ushort)3, back.EffectiveServerGroup);
        Assert.Equal((ushort)3, ReadLogin(Assert.Single(LoginFrames(rig, 2))).ServerGroup);
    }

    [Fact]
    public async Task A_non_zero_setting_wins_over_the_directory()
    {
        var rig = NewRig();
        ServeDirectory(rig, Directory("siroco", 30052));
        rig.Settings.ServerGroup = 5;
        OnlineClientSession client = await rig.LoggedIn();
        Assert.True((await Bounded(client.LoadChannelDirectoryAsync())).Succeeded);

        OnlineConnectResult connected = await Bounded(client.ConnectAsync(30052));

        Assert.Equal((ushort)5, connected.EffectiveServerGroup);   // 目录里这个端口是 3,但设置的 5 优先
        Assert.Equal((ushort)5, ReadLogin(Assert.Single(LoginFrames(rig, 0))).ServerGroup);
    }

    [Fact]
    public async Task A_zero_setting_with_no_usable_mapping_stays_zero()
    {
        // 1) 一次目录也没读过
        var noDirectory = NewRig();
        OnlineClientSession a = await noDirectory.LoggedIn();
        OnlineConnectResult first = await Bounded(a.ConnectAsync(30052));
        Assert.Equal((ushort)0, first.EffectiveServerGroup);
        Assert.Equal((ushort)0, ReadLogin(Assert.Single(LoginFrames(noDirectory, 0))).ServerGroup);

        // 2) 目录里有这个端口,但组标签映射不到服务器组
        var unmappable = NewRig();
        ServeDirectory(unmappable, Directory("synth-nowhere", 30052));
        OnlineClientSession b = await unmappable.LoggedIn();
        Assert.True((await Bounded(b.LoadChannelDirectoryAsync())).Succeeded);
        OnlineConnectResult second = await Bounded(b.ConnectAsync(30052));
        Assert.True(second.IsSuccess);
        Assert.Equal((ushort)0, second.EffectiveServerGroup);
        Assert.Equal((ushort)0, ReadLogin(Assert.Single(LoginFrames(unmappable, 0))).ServerGroup);

        // 3) 目录读取失败过(没有快照):同样是 0
        var failedDirectory = NewRig();
        failedDirectory.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(
            ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "synthetic"));
        OnlineClientSession c = await failedDirectory.LoggedIn();
        Assert.False((await Bounded(c.LoadChannelDirectoryAsync())).Succeeded);
        Assert.Equal((ushort)0, (await Bounded(c.ConnectAsync(30052))).EffectiveServerGroup);
    }

    [Fact]
    public async Task The_group_reported_after_retries_is_the_one_in_the_last_login_request_actually_sent()
    {
        // 第一次换频道被拒(码 1),重试时频道连不上:一共只发出过一份请求,服务器组是它写的 3,不是 0
        var rig = NewRig();
        rig.Settings.ServerGroup = 3;
        rig.ScriptFactory = ch => ch == 0 ? Script() : Script(Code1Rejection);
        rig.FailChannel = index => index == 2;
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(9020));

        Assert.False(switched.IsSuccess);
        Assert.Equal(OnlineFailure.ConnectFailed, switched.Failure);
        Assert.Equal(1, switched.GameLoginAttempts);
        Assert.Equal((ushort)3, switched.EffectiveServerGroup);
    }

    [Fact]
    public async Task Retried_requests_report_the_group_resolved_for_the_port_each_time()
    {
        // 设置为 0、目录里该端口映射到 3:被拒一次再重试成功,最终报告 3,两份请求里都是 3
        var rig = NewRig();
        ServeDirectory(rig, Directory("siroco", 30052));
        rig.ScriptFactory = ch => ch == 1 ? Script(Code1Rejection) : Script();
        OnlineClientSession client = await rig.LoggedIn();
        Assert.True((await Bounded(client.LoadChannelDirectoryAsync())).Succeeded);
        Assert.True((await Bounded(client.ConnectAsync(9010))).IsSuccess);

        OnlineConnectResult switched = await Bounded(client.SwitchChannelAsync(30052));

        Assert.True(switched.IsSuccess, switched.Failure.ToString());
        Assert.Equal(2, switched.GameLoginAttempts);
        Assert.Equal((ushort)3, switched.EffectiveServerGroup);
        Assert.Equal((ushort)3, ReadLogin(Assert.Single(LoginFrames(rig, 1))).ServerGroup);
        Assert.Equal((ushort)3, ReadLogin(Assert.Single(LoginFrames(rig, 2))).ServerGroup);
    }
}
