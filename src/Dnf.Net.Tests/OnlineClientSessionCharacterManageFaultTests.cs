using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网会话"创建/删除角色"在写失败、释放与断线边界上的补充测试(r20fix-createcharui,簇 1)。
/// 与 <see cref="OnlineClientSessionCharacterManageTests"/> 互补:那边钉住主流程与各结局,这里钉住
/// "写流抛已释放异常时,如何区分会话自己被释放与流坏了"、"断线条件的每一项都要立刻结束等待"与"名册替换对空载荷旧帧的容忍"。
/// 全部走内存里的假管道,数据是合成的(名字、槽号、职业都是 900 起的合成值),不连任何真实服务,也不触发任何真实创建或删除
/// (写开关只对这条内存会话打开)。
/// </summary>
public sealed class OnlineClientSessionCharacterManageFaultTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static async Task Bounded(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, finished);
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        await Bounded(Task.Run(async () =>
        {
            while (!condition()) await Task.Delay(5);
            return 0;
        }));
    }

    private static Task<OnlineCharacterManageResult> Manage(OnlineClientSession client, bool create)
    {
        return create
            ? client.CreateCharacterAsync(3, Encoding.UTF8.GetBytes("Hero900"))
            : client.DeleteCharacterAsync(1, Encoding.UTF8.GetBytes("Hero901"));
    }

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script(
        Func<DungeonEntryClientFrame, IEnumerable<byte[]>> others = null, byte[][] list = null)
    {
        return HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            list ?? HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            others);
    }

    // ------------------------------------------------------------------ 写流失败:会话还活着时都是断线

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task A_failing_write_on_a_live_session_is_a_write_fault_with_nothing_else_changed(bool create, bool streamDisposed)
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        var disconnects = new OnlineDisconnectLog();
        client.Disconnected += disconnects.Add;
        OnlineFlowChannel channel = rig.Channels[0];
        channel.Pipe.WriteFailure = streamDisposed
            ? new ObjectDisposedException("synthetic-stream")
            : new IOException("synthetic-write-fail");

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(create ? OnlineCharacterManageKind.Create : OnlineCharacterManageKind.Delete, res.Operation);
        Assert.Equal(OnlineCharacterManageOutcomeKind.Disconnected, res.Outcome);
        Assert.True(res.IsDisconnected);
        Assert.False(res.IsSuccess);
        Assert.False(res.CommandSucceeded);
        Assert.Null(res.CreateResult);
        Assert.Null(res.DeleteResult);
        Assert.Null(res.CharacterList);
        Assert.Null(res.RawFailureCode);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);

        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Null(client.GameSession);
        Assert.Null(client.CharacterList);
        Assert.Equal(1, channel.Connection.DisposeCount);
        Assert.True(channel.Link.WriteFault);
        Assert.True(channel.Link.WriteFailed);
        Assert.Equal(new[] { OnlineDisconnectReason.WriteError }, disconnects.Reasons);
    }

    // ------------------------------------------------------------------ 写在途时会话被释放:是释放,不是断线

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_while_the_write_is_in_flight_throws_object_disposed_and_fires_no_disconnect(bool create)
    {
        var holder = new GatedChannel();
        OnlineFlowRig rig = GatedRig(holder);
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        var disconnects = new OnlineDisconnectLog();
        client.Disconnected += disconnects.Add;

        var writing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.Gate.BeforeWrite = async () =>
        {
            writing.TrySetResult(0);
            await release.Task;
        };
        Task<OnlineCharacterManageResult> call = Manage(client, create);
        await Bounded(writing.Task);

        // 写已经停在流里:放行之后流抛"已释放"。先释放会话,等它记下"已释放"(新命令被拒是它的外部表现),再放行写。
        holder.Pipe.WriteFailure = new ObjectDisposedException("synthetic-stream");
        Task dispose = Task.Run(client.Dispose);
        await WaitUntil(() => client.SendCommandAsync(902, new byte[] { 2 }).Exception?.InnerException is ObjectDisposedException);
        release.TrySetResult(0);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(call));
        await Bounded(dispose);

        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
        Assert.Empty(disconnects.Reasons);
        Assert.Equal(1, holder.Connection.DisposeCount);
    }

    // ------------------------------------------------------------------ 断线的每一项条件都要立刻结束回复等待

    [Theory]
    [InlineData(true, "peer_closed")]
    [InlineData(false, "peer_closed")]
    [InlineData(true, "frame_fault")]
    [InlineData(false, "frame_fault")]
    [InlineData(true, "read_fault")]
    [InlineData(false, "read_fault")]
    public async Task A_dead_connection_ends_the_reply_wait_at_once_instead_of_running_out_the_window(bool create, string fault)
    {
        var rig = new OnlineFlowRig();
        // 回复窗口拉到 30 秒:任何一项断线条件失灵,调用都会空转到窗口结束,5 秒的上限就会判它失败
        rig.Hooks.Windows = new OnlineTownWindows(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(30));
        rig.ScriptFactory = _ => Script(frame =>
        {
            if (frame.Id != 5 && frame.Id != 6)
            {
                return null;
            }
            if (fault == "peer_closed")
            {
                rig.Channels[0].Pipe.CloseServer();
                return HostTestKit.Frames();
            }
            if (fault == "frame_fault")
            {
                byte[] bad = new byte[15];
                bad[1] = 27;
                DungeonEntryWire.PutU32(bad, 3, 14);    // 整帧长度 14 小于下限 15:切帧出错
                return HostTestKit.Frames(bad);
            }
            return null;
        });
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        var disconnects = new OnlineDisconnectLog();
        client.Disconnected += disconnects.Add;
        OnlineFlowChannel channel = rig.Channels[0];
        if (fault == "read_fault")
        {
            channel.Link.ReadFault = true;
        }

        // 放到线程池上跑:被改坏时它在调用线程里同步空转,不放出去的话上限也计不了时
        OnlineCharacterManageResult res = await Bounded(Task.Run(() => Manage(client, create)));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Disconnected, res.Outcome);
        Assert.Equal(create ? OnlineCharacterManageKind.Create : OnlineCharacterManageKind.Delete, res.Operation);
        Assert.Null(res.CreateResult);
        Assert.Null(res.DeleteResult);
        Assert.Null(res.CharacterList);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Equal(1, channel.Connection.DisposeCount);
        OnlineDisconnectReason expected = fault == "peer_closed" ? OnlineDisconnectReason.PeerClosed : OnlineDisconnectReason.ReadError;
        Assert.Equal(new[] { expected }, disconnects.Reasons);
    }

    // ------------------------------------------------------------------ 名册替换:只换"模式 2"的旧 0/2 帧,别的 0/2 帧原样留着

    /// <summary>
    /// 进城时除了真名册,还收到一个信封内层的 0/2 帧(载荷由参数给)。创建成功后的名册替换只该移走"首字节是模式 2"的那些帧
    /// (哪怕只有这一个字节);空载荷、别的模式的帧不是模式 2,既不能被移走、判断它们时也不能越界。
    /// 进城快照把还留在早期帧里的、读不通的 0/2 帧计作"错位",所以选角之后的错位帧数就是"留下了几个":
    /// 空载荷 1、模式 1 与模式 3 各 1(都留下),单字节模式 2 与两字节模式 2 各 0(都被新名册顶替)。
    /// </summary>
    [Theory]
    [InlineData(new byte[0], 1)]
    [InlineData(new byte[] { 2 }, 0)]
    [InlineData(new byte[] { 2, 9 }, 0)]
    [InlineData(new byte[] { 1, 9 }, 1)]
    [InlineData(new byte[] { 3 }, 1)]
    public async Task Roster_replacement_removes_only_the_mode_2_early_frames_and_tolerates_the_rest(byte[] strayPayload, int expectedMalformed)
    {
        UserInfoTestData.EntrySpec createdSpec = UserInfoTestData.Standard(3, nameLength: 7, equipCount: 0);
        createdSpec.Uid = 903;
        createdSpec.Name = Encoding.UTF8.GetBytes("Hero903");
        createdSpec.Job = 7;
        createdSpec.Level = 1;
        createdSpec.GrowType = 0x12;
        createdSpec.D2 = 0;
        byte[] refreshedPacket = UserInfoTestData.Packet(
            UserInfoTestData.Standard(0),
            UserInfoTestData.Standard(1),
            UserInfoTestData.Standard(2),
            createdSpec);

        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            frame => frame.Id == 5
                ? HostTestKit.Frames(DungeonEntryWire.Reply(5, new byte[] { 1 }), HostTestKit.DirectListNotice(refreshedPacket))
                : null,
            HostTestKit.Frames(
                HostTestKit.DirectListNotice(),
                HostTestKit.RealEnvelope((byte)PacketClass.Noti, 2, strayPayload)));
        (OnlineClientSession client, OnlineConnectResult connected) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        Assert.Equal(3, connected.CharacterList.Entries.Count);

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(7, Encoding.UTF8.GetBytes("Hero903"), 0));

        Assert.True(res.IsSuccess);
        Assert.Equal(4, res.CharacterList.Entries.Count);
        Assert.Same(res.CharacterList, client.CharacterList);

        // 选新角色 3 号槽位:进城快照读的是替换后的名册(旧名册只有 3 个)
        OnlineSelectCharacterResult selected = await Bounded(client.SelectCharacterAsync(3));
        Assert.True(selected.Entered);
        Assert.Equal(1, selected.Snapshot.Level);
        Assert.Equal(7, selected.Snapshot.Job);
        Assert.Equal(expectedMalformed, selected.Snapshot.FramesMalformed);
    }

    // ------------------------------------------------------------------ 把写卡在流里的假频道

    private sealed class GatedChannel
    {
        internal WriteGateStream Gate;
        internal DungeonEntryPipe Pipe;
        internal OnlineFlowConnection Connection;
    }

    /// <summary>频道换成"写先过闸"的版本:闸口不看取消令牌,模拟写已经交给网络栈、取消管不到的时刻。</summary>
    private static OnlineFlowRig GatedRig(GatedChannel holder)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.ChannelFactory = (address, port, cancellationToken) =>
        {
            var pipe = new DungeonEntryPipe();
            pipe.Script = Script();
            var gate = new WriteGateStream(pipe);
            var recording = new OnlineFlowRecordingStream(gate);
            var observer = new OnlineObservedStream(recording);
            var session = new GameClientSession(observer, slot => new SliceXorCipher(slot), () => 0x5a);
            var connection = new OnlineFlowConnection();
            var link = new OnlineChannelLink(session, connection, 0, observer);
            rig.Channels.Add(new OnlineFlowChannel(pipe, recording, link, connection));
            holder.Gate = gate;
            holder.Pipe = pipe;
            holder.Connection = connection;
            pipe.Push(DungeonEntryWire.BootstrapFrame());
            return Task.FromResult(link);
        };
        return rig;
    }

    private sealed class WriteGateStream : Stream
    {
        private readonly Stream _inner;

        internal WriteGateStream(Stream inner)
        {
            _inner = inner;
        }

        /// <summary>非 null 时,每次写先等它;读与其余操作原样转交。</summary>
        internal Func<Task> BeforeWrite { get; set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Func<Task> before = BeforeWrite;
            if (before != null)
            {
                await before().ConfigureAwait(false);
            }
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }
}
