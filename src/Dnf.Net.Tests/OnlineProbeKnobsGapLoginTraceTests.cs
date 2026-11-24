using Dnf.Net.Crypto;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 游戏登录一步(<c>OnlineTownEntry.TryGameLogin</c>)的"实际发出了什么"记录:只有登录请求真的写出去了才记;
/// 没有记录对象(<c>orchestrated</c> 进城的编排就是这条路径)时发出请求也不能抛。
/// 真 <see cref="GameClientSession"/> 接内存管道,脚本化的"服务端"回合成登录回包;"写出失败"由一个块长报错的算法替身造出
/// (会话密码的补齐长度取槽表、加密取算法对象,两者对不上时帧层返回编码失败)。不联网、没有睡眠依赖。
/// </summary>
public sealed class OnlineProbeKnobsGapLoginTraceTests
{
    /// <summary>
    /// 密钥设好之后把块长多报 1 的算法替身:设密钥前与槽表一致(通过会话密码取算法对象时的一致性检查),设密钥后块长与槽表对不上,
    /// 于是 <see cref="SessionCipher.Encrypt"/> 补出来的长度与帧层按槽表算出的补齐长度不同,<c>GameClientSession.SendAsync</c> 返回
    /// <see cref="FrameStatus.EncryptFailed"/>(没有写出任何字节)。块加解密本身什么也不做。
    /// </summary>
    private sealed class MisreportingBlockCipher : BlockCipher
    {
        private bool _keyed;

        public MisreportingBlockCipher(int slot)
        {
            Slot = slot;
        }

        public override int Slot { get; }

        public override int BlockSize => CipherSlots.BlockSizes[Slot] + (_keyed ? 1 : 0);

        public override int KeyLength => CipherSlots.KeyLengths[Slot];

        public override void SetKey(byte[] key, int offset)
        {
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
        }
    }

    /// <summary>已收到启动包的会话,算法槽用块长报错的替身(此后任何命令帧都写不出去)。</summary>
    private static async Task<(GameClientSession Session, DungeonEntryPipe Pipe)> SessionWhoseSendFails()
    {
        var pipe = new DungeonEntryPipe();
        pipe.Push(DungeonEntryWire.BootstrapFrame());
        var session = new GameClientSession(pipe, slot => new MisreportingBlockCipher(slot));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));
        return (session, pipe);
    }

    private static OnlineTownEntry EntryWithGroup(ushort serverGroup)
    {
        OnlineEntrySettings settings = HostTestKit.Settings();
        settings.ServerGroup = serverGroup;
        return new OnlineTownEntry(settings);
    }

    private static Task<OnlineEntryResult> GameLogin(OnlineTownEntry entry, GameClientSession session, CountingConnection connection,
        ushort? serverGroupOverride, GameLoginTrace trace)
    {
        return GatewayTestSupport.Bounded(
            entry.TryGameLogin(session, "TOKEN900", 0, connection, HostTestKit.TownWindows, new List<DecodedFrame>(), CancellationToken.None, serverGroupOverride, trace), 20);
    }

    /// <summary>
    /// 这条断言保护的行为:没有记录对象(<c>trace</c> 为 null)、登录请求写出成功时,游戏登录一步照常走完返回成功(null),不抛空引用——
    /// <c>orchestrated</c> 的进城编排就是这样调用的。同时确认请求确实写出去了(先是登录帧 1,再是角色列表请求 8)。
    /// 推导:写出成功 → 状态 Ok;记录对象为空就不记;回包是合成的成功回包 → 返回 null。
    /// </summary>
    [Fact]
    public async Task Without_a_trace_a_login_request_that_is_written_successfully_does_not_throw_and_the_login_succeeds()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());
        var connection = new CountingConnection();

        OnlineEntryResult failure = await GameLogin(EntryWithGroup(0), session, connection, null, null);

        Assert.Null(failure);
        Assert.Equal(new ushort[] { 1, 8 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
        Assert.Equal(0, connection.DisposeCount);               // 成功时连接还归调用方
    }

    /// <summary>
    /// 这条断言保护的行为:登录请求写出失败(帧层返回非 Ok)时,记录对象保持缺省——<c>Sent</c> 仍为 false、<c>ServerGroup</c> 仍为 0——
    /// 即使设置里的服务器组非 0、也给了覆盖值;结果是游戏登录阶段的"没回应"失败,没有任何字节写到管道上。
    /// 推导:替身算法让 <c>SendAsync</c> 返回 <see cref="FrameStatus.EncryptFailed"/> → 不记 → <c>Sent</c> 假、组 0;
    /// 失败结果 = 阶段 GameLogin、原因 GameLoginNoReply、没有失败码、没有槽;事件里最后一条是"编码失败"。
    /// </summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(7, null)]
    [InlineData(7, 11)]
    public async Task A_login_request_that_cannot_be_written_leaves_the_trace_untouched_and_fails_with_no_reply(int settingsGroup, int? overrideGroup)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await SessionWhoseSendFails();
        var trace = new GameLoginTrace();
        var connection = new CountingConnection();

        OnlineEntryResult failure = await GameLogin(EntryWithGroup((ushort)settingsGroup), session, connection,
            overrideGroup.HasValue ? (ushort)overrideGroup.Value : (ushort?)null, trace);

        Assert.NotNull(failure);
        Assert.False(trace.Sent);
        Assert.Equal((ushort)0, trace.ServerGroup);
        Assert.False(failure.Entered);
        Assert.Equal(OnlineStage.GameLogin, failure.Stage);
        Assert.Equal(OnlineFailure.GameLoginNoReply, failure.Failure);
        Assert.Null(failure.LoginFailureCode);
        Assert.Equal(-1, failure.Slot);
        Assert.Empty(pipe.ClientFrames);                         // 一个字节也没写出去
        SessionEvent last = session.Events[^1];
        Assert.True(last.Outbound);
        Assert.Contains("编码失败", last.Note);                   // 失败来自发送,不是没等到回包
        Assert.Equal(2, failure.FramesSeen);                     // 事件:收到的启动包 + 这条编码失败
        failure.Dispose();
        Assert.Equal(1, connection.DisposeCount);                // 失败结果接管连接的释放
    }

    /// <summary>
    /// 这条断言保护的行为(对照):同一套夹具里,请求写出成功时记录对象才被填上——<c>Sent</c> 为真,<c>ServerGroup</c> 取请求里最终写进去的值
    /// (没有覆盖值时是设置里的组,给了覆盖值时是覆盖值)。证明上一条里"保持缺省"不是因为记录对象根本填不上。
    /// 推导:设置组 7 → 记 7;覆盖 11 → 记 11;设置组 0 且无覆盖 → 记 0(请求里写的就是 0)。
    /// </summary>
    [Theory]
    [InlineData(7, null, 7)]
    [InlineData(7, 11, 11)]
    [InlineData(0, null, 0)]
    public async Task A_login_request_that_is_written_fills_the_trace_with_the_group_actually_in_the_request(int settingsGroup, int? overrideGroup, int expectedGroup)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());
        var trace = new GameLoginTrace();

        OnlineEntryResult failure = await GameLogin(EntryWithGroup((ushort)settingsGroup), session, new CountingConnection(),
            overrideGroup.HasValue ? (ushort)overrideGroup.Value : (ushort?)null, trace);

        Assert.Null(failure);
        Assert.True(trace.Sent);
        Assert.Equal((ushort)expectedGroup, trace.ServerGroup);
        Assert.Equal(new ushort[] { 1, 8 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }
}
