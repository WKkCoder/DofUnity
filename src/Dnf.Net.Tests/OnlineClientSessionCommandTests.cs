using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Xunit.Abstractions;

namespace Dnf.Net.Tests;

/// <summary>
/// N2 城镇命令通道测试:<see cref="OnlineClientSession.SendCommandAsync"/> 与 <see cref="OnlineClientSession.FrameReceived"/>。
/// 全部基于假网关与内存管道(确定性门/同步点,不靠睡眠猜时序),编号与载荷全为合成值(900+)。
/// </summary>
public sealed class OnlineClientSessionCommandTests
{
    private readonly ITestOutputHelper _output;

    public OnlineClientSessionCommandTests(ITestOutputHelper output) => _output = output;

    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 15);

    private static async Task Bounded(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
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

    private static ushort ReadSequence(byte[] wire)
    {
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, wire, out PacketHeader header));
        return (ushort)header.Second;
    }

    private static OnlineFlowRig CreateGatedRig(
        out List<GatedRecordingStream> streams,
        out List<DungeonEntryPipe> pipes,
        Func<int, Func<DungeonEntryClientFrame, IEnumerable<byte[]>>> scriptFactory = null)
    {
        var rig = new OnlineFlowRig();
        var localStreams = new List<GatedRecordingStream>();
        var localPipes = new List<DungeonEntryPipe>();
        streams = localStreams;
        pipes = localPipes;
        rig.Hooks.ChannelFactory = (address, port, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            int index = localPipes.Count;
            rig.RequestedPorts.Add(port);
            var pipe = new DungeonEntryPipe
            {
                Script = scriptFactory?.Invoke(index)
                    ?? HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(HostTestKit.DirectListNotice()), _ => HostTestKit.EnterComplete()),
            };
            var gated = new GatedRecordingStream(pipe);
            var observer = new OnlineObservedStream(gated);
            var session = new GameClientSession(observer, slot => new SliceXorCipher(slot), () => 0x5a);
            var connection = new OnlineFlowConnection();
            var link = new OnlineChannelLink(session, connection, 0, observer);
            localPipes.Add(pipe);
            localStreams.Add(gated);
            rig.Channels.Add(new OnlineFlowChannel(pipe, new OnlineFlowRecordingStream(new MemoryStream()), link, connection));
            pipe.Push(DungeonEntryWire.BootstrapFrame());
            return Task.FromResult(link);
        };
        return rig;
    }

    /// <summary>包装底层流:记录原始写帧并允许测试在 WriteAsync/ReadAsync 上设置确定性拦截门。</summary>
    private sealed class GatedRecordingStream : Stream
    {
        private readonly Stream _inner;
        private readonly List<byte[]> _writes = new();
        private int _readCalls;

        public GatedRecordingStream(Stream inner) => _inner = inner;

        public Func<byte[], Task> OnWriteAsync { get; set; }
        public Func<int, int> OnReadResult { get; set; }

        /// <summary>每发起一笔读(ReadAsync 被调用,转给底层之前)同步调用一次,参数是发起后的累计读次数;null 为不做事。"读被发起"的事件屏障靠它。</summary>
        public Action<int> OnReadStarted { get; set; }

        /// <summary>目前为止发起过的读次数(会话至多一笔在途读:新读只在上一笔读完成之后才会发起)。</summary>
        public int ReadCalls => Volatile.Read(ref _readCalls);

        public IReadOnlyList<byte[]> Writes
        {
            get { lock (_writes) return _writes.ToArray(); }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int started = Interlocked.Increment(ref _readCalls);
            OnReadStarted?.Invoke(started);
            int n = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            Func<int, int> hook = OnReadResult;
            return hook != null ? hook(n) : n;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            byte[] copy = buffer.Skip(offset).Take(count).ToArray();
            Func<byte[], Task> hook = OnWriteAsync;
            if (hook != null)
            {
                await hook(copy).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_writes) _writes.Add(copy);
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingDispatcher : IOnlineDispatcher
    {
        private readonly object _gate = new();
        private readonly List<int> _threads = new();

        public int Calls { get { lock (_gate) return _threads.Count; } }
        public int[] ThreadIds { get { lock (_gate) return _threads.ToArray(); } }

        public void Dispatch(Action callback)
        {
            lock (_gate) _threads.Add(Environment.CurrentManagedThreadId);
            callback();
        }
    }

    // ======== 发命令:解码、载荷副本、序号递增与并发压力 ========

    [Fact]
    public async Task SendCommandAsync_encodes_command_frame_copies_payload_and_returns_Ok()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        byte[] payload = { 0x11, 0x22, 0x33, 0x44, 0x55 };
        byte[] expected = (byte[])payload.Clone();

        Task<FrameStatus> send = client.SendCommandAsync(901, payload);
        // 立刻修改入参缓冲区,不得影响实际发出的载荷
        payload[0] = 0xEE;
        payload[4] = 0xDD;

        Assert.Equal(FrameStatus.Ok, await Bounded(send));
        DungeonEntryClientFrame cmd = rig.Channels[0].Pipe.ClientFrames.Single(f => f.Id == 901);
        Assert.Equal(PacketClass.Cmd, cmd.Class);
        Assert.Equal(expected, cmd.Plain.Take(expected.Length).ToArray());
        Assert.All(cmd.Plain.Skip(expected.Length), b => Assert.Equal(0, b));

        // 空载荷与 null 载荷行为
        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(902, Array.Empty<byte>())));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SendCommandAsync(902, null));
        client.Dispose();
    }

    [Fact]
    public async Task Commands_interleaved_with_heartbeats_share_strictly_increasing_sequence_without_gaps()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        OnlineFlowChannel ch = rig.Channels[0];
        int baselineCount = ch.Recording.Writes.Count;

        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(901, new byte[] { 1 })));
        ch.Pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
        await WaitUntil(() => ch.Pipe.ClientFrames.Count(f => f.Id == 0) == 1);
        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(902, new byte[] { 2 })));
        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(903, new byte[] { 3 })));
        ch.Pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
        await WaitUntil(() => ch.Pipe.ClientFrames.Count(f => f.Id == 0) == 2);
        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(904, new byte[] { 4 })));

        IReadOnlyList<byte[]> writes = ch.Recording.Writes;
        ushort firstSeq = ReadSequence(writes[0]);
        for (int i = 0; i < writes.Count; i++)
        {
            Assert.Equal((ushort)(firstSeq + i), ReadSequence(writes[i]));
        }
        Assert.Equal(baselineCount + 6, writes.Count);
        Assert.Equal(new ushort[] { 901, 0, 902, 903, 0, 904 }, ch.Pipe.ClientFrames.Skip(baselineCount).Select(f => f.Id).ToArray());
        client.Dispose();
    }

    [Fact]
    public async Task Multithreaded_commands_and_heartbeats_complete_with_strict_sequence_and_decodable_frames()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        OnlineFlowChannel ch = rig.Channels[0];
        int baseline = ch.Recording.Writes.Count;

        const int workers = 4;
        const int perWorker = 25;
        const int heartbeats = 20;
        using var startGate = new ManualResetEventSlim(false);

        Task<FrameStatus[]>[] workerTasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            startGate.Wait();
            var statuses = new FrameStatus[perWorker];
            for (int i = 0; i < perWorker; i++)
            {
                statuses[i] = await client.SendCommandAsync((ushort)(910 + w), new byte[] { (byte)w, (byte)i });
            }
            return statuses;
        })).ToArray();

        Task hbTask = Task.Run(() =>
        {
            startGate.Wait();
            for (int i = 0; i < heartbeats; i++)
            {
                ch.Pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
            }
        });

        startGate.Set();
        FrameStatus[][] allStatuses = await Bounded(Task.WhenAll(workerTasks));
        await Bounded(hbTask);
        await WaitUntil(() => ch.Pipe.ClientFrames.Count >= baseline + workers * perWorker + heartbeats);

        foreach (FrameStatus[] list in allStatuses)
        {
            Assert.All(list, s => Assert.Equal(FrameStatus.Ok, s));
        }
        IReadOnlyList<byte[]> writes = ch.Recording.Writes;
        Assert.Equal(baseline + workers * perWorker + heartbeats, writes.Count);
        ushort seq0 = ReadSequence(writes[0]);
        for (int i = 0; i < writes.Count; i++)
        {
            Assert.Equal((ushort)(seq0 + i), ReadSequence(writes[i]));
        }
        for (int w = 0; w < workers; w++)
        {
            byte[] indices = ch.Pipe.ClientFrames
                .Where(f => f.Class == PacketClass.Cmd && f.Id == 910 + w)
                .Select(f => f.Plain[1])
                .ToArray();
            Assert.Equal(Enumerable.Range(0, perWorker).Select(x => (byte)x).ToArray(), indices);
        }
        client.Dispose();
    }

    [Fact]
    public async Task Frame_encode_failure_returns_status_without_writing_or_advancing_sequence()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        OnlineFlowChannel ch = rig.Channels[0];
        int writesBefore = ch.Recording.Writes.Count;
        ushort lastSeqBefore = ReadSequence(ch.Recording.Writes[writesBefore - 1]);

        FrameStatus status = await Bounded(client.SendCommandAsync(901, new byte[FrameLayout.PacketBufferSize]));
        Assert.Equal(FrameStatus.FrameTooLarge, status);
        Assert.Equal(writesBefore, ch.Recording.Writes.Count);

        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(902, new byte[] { 7 })));
        Assert.Equal(writesBefore + 1, ch.Recording.Writes.Count);
        Assert.Equal((ushort)(lastSeqBefore + 1), ReadSequence(ch.Recording.Writes[writesBefore]));
        client.Dispose();
    }

    // ======== 状态门与生命周期 ========

    [Theory]
    [InlineData("created")]
    [InlineData("logged-in")]
    [InlineData("connected")]
    [InlineData("exclusive")]
    [InlineData("selected-no-heartbeat")]
    [InlineData("selected-heartbeat-stopped")]
    public async Task SendCommandAsync_rejects_non_town_or_stopped_heartbeat_states_with_InvalidOperationException(string state)
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client;
        switch (state)
        {
            case "created":
                client = rig.CreateSession();
                break;
            case "logged-in":
                client = await rig.LoggedIn();
                break;
            case "connected":
                (client, _) = await rig.Connected();
                break;
            case "exclusive":
                (client, _) = await rig.Selected(autoHeartbeat: true);
                await client.EnterExclusiveReceiveAsync();
                break;
            case "selected-no-heartbeat":
                (client, _) = await rig.Selected(autoHeartbeat: false);
                break;
            default:
                (client, _) = await rig.Selected(autoHeartbeat: true);
                await client.SetHeartbeatAsync(false);
                break;
        }

        Task<FrameStatus> send = client.SendCommandAsync(901, new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => send);
        client.Dispose();
    }

    [Fact]
    public async Task SendCommandAsync_after_dispose_throws_ObjectDisposedException()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        client.Dispose();

        Task<FrameStatus> send = client.SendCommandAsync(901, new byte[] { 1 });
        await Assert.ThrowsAsync<ObjectDisposedException>(() => send);
    }

    [Theory]
    [InlineData("dispose", typeof(ObjectDisposedException))]
    [InlineData("disconnect", typeof(InvalidOperationException))]
    [InlineData("exclusive", typeof(InvalidOperationException))]
    [InlineData("switch", typeof(InvalidOperationException))]
    [InlineData("heartbeat-off", typeof(InvalidOperationException))]
    public async Task Queued_unsent_commands_never_hang_and_never_write_on_lifecycle_transition(string action, Type expectedException)
    {
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        GatedRecordingStream stream = streams[0];

        var enteredFirstWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.OnWriteAsync = async _ =>
        {
            enteredFirstWrite.TrySetResult(0);
            await releaseFirstWrite.Task;
        };

        Task<FrameStatus> first = client.SendCommandAsync(901, new byte[] { 1 });
        await Bounded(enteredFirstWrite.Task);

        // 第一条正在写出期间入队第二条:必然留在信箱队列里
        stream.OnWriteAsync = null;
        Task<FrameStatus> queued = client.SendCommandAsync(902, new byte[] { 2 });

        Task transition = action switch
        {
            "dispose" => Task.Run(client.Dispose),
            "disconnect" => client.DisconnectAsync(),
            "exclusive" => client.EnterExclusiveReceiveAsync(),
            "switch" => client.SwitchChannelAsync(9020),
            _ => client.SetHeartbeatAsync(false),
        };

        Exception caught = await Assert.ThrowsAnyAsync<Exception>(() => Bounded(queued));
        Assert.IsType(expectedException, caught);

        releaseFirstWrite.TrySetResult(0);
        await Bounded(transition);
        try { await Bounded(first); } catch (ObjectDisposedException) when (action == "dispose") { }
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 902);
        client.Dispose();
    }

    /// <summary>
    /// 事件屏障:让心跳泵确定"停在读上"之后测试才动服务端。服务端先发一个心跳请求,等泵回出心跳回包(泵已经起来、并处理完一个窗口),
    /// 再等到"回包之后发起的新读"。会话至多一笔在途读(<see cref="GameClientSession.ReceiveAsync"/> 新读只在上一笔读完成后才发起),
    /// 所以此刻泵一定在 ReceiveAsync 里等这一笔读:它回到循环顶排信箱,只可能发生在这一笔读返回之后。
    /// 调用方须在 Selected 之前把 HeartbeatPoll 设得很长,"轮询到点、泵回到循环顶先排一次信箱"这条旁路才不存在。
    /// 为什么需要它:Selected(autoHeartbeat: true) 只是把泵 Task.Run 出去就返回,冷进程里泵还没起步时测试线程已经把服务端关了;
    /// 读返回 0 的钩子于是在泵起步之前把命令塞进信箱,泵起步后第一件事是排信箱,把这条命令成功写了出去(写端还开着),命令没有以 IOException 结束。
    /// </summary>
    private static async Task ParkPumpOnReadAsync(GatedRecordingStream stream, DungeonEntryPipe pipe)
    {
        int readsAtReply = -1;
        var replied = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readAfterReply = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.OnWriteAsync = _ =>
        {
            // 回包写出之前记下此刻已发起的读数:泵写完回包才会回到循环顶发下一笔读,所以累计数大于它的读一定发起在回包之后
            Interlocked.CompareExchange(ref readsAtReply, stream.ReadCalls, -1);
            replied.TrySetResult(0);
            return Task.CompletedTask;
        };
        stream.OnReadStarted = started =>
        {
            int at = Volatile.Read(ref readsAtReply);
            if (at >= 0 && started > at)
            {
                readAfterReply.TrySetResult(0);
            }
        };
        pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
        await Bounded(replied.Task);
        await Bounded(readAfterReply.Task);
        stream.OnWriteAsync = null;
        stream.OnReadStarted = null;
    }

    [Fact]
    public async Task Queued_unsent_command_fails_with_IOException_when_pump_read_returns_zero_on_peer_close()
    {
        // 读端返回 0(PeerClosed)时已在信箱里的命令必须以 IOException 结束且不写出
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        rig.Hooks.HeartbeatPoll = TimeSpan.FromSeconds(30);   // 必须在 Selected 之前:排除"轮询到点回循环顶排信箱"的旁路
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        Task<FrameStatus> queuedOnClose = null;
        var pushedOnRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams[0].OnReadResult = n =>
        {
            if (n == 0)
            {
                queuedOnClose = client.SendCommandAsync(902, new byte[] { 2 });
                pushedOnRead.TrySetResult(0);
            }
            return n;
        };

        await ParkPumpOnReadAsync(streams[0], pipes[0]);   // 泵此刻停在读上:钩子里入信箱的命令只会遇到泵退出时的信箱关闭
        pipes[0].CloseServer();
        await Bounded(pushedOnRead.Task);
        await Assert.ThrowsAsync<IOException>(() => Bounded(queuedOnClose));
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 902);
        client.Dispose();
    }

    [Fact]
    public async Task Queued_unsent_command_fails_with_IOException_and_single_WriteError_when_pump_write_fails()
    {
        // 写失败(WriteFailure):第一条抛 IOException,Disconnected(WriteError) 恰好一次,队列第二条也以 IOException(而不是 InvalidOperationException)结束。
        // 与泵所处位置无关:泵一直卡在第一条命令的写门里,第二条必然留在信箱里,所以是确定性的。
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var disconnects = new OnlineDisconnectLog();
        client.Disconnected += disconnects.Add;

        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams[0].OnWriteAsync = async _ =>
        {
            entered.TrySetResult(0);
            await release.Task;
        };
        Task<FrameStatus> cmd1 = client.SendCommandAsync(901, new byte[] { 1 });
        await Bounded(entered.Task);
        streams[0].OnWriteAsync = null;
        Task<FrameStatus> cmd2 = client.SendCommandAsync(902, new byte[] { 2 });
        pipes[0].WriteFailure = new IOException("synthetic write error");
        release.TrySetResult(0);

        await Assert.ThrowsAsync<IOException>(() => Bounded(cmd1));
        await Assert.ThrowsAsync<IOException>(() => Bounded(cmd2));
        await WaitUntil(() => disconnects.Count == 1);
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
        Assert.Equal(new[] { OnlineDisconnectReason.WriteError }, disconnects.Reasons);
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 902);
        client.Dispose();
    }

    [Fact]
    public async Task Queued_unsent_command_fails_with_IOException_and_ReadError_when_pump_read_faults()
    {
        // 读错误(ReadFault):在读端返回前入队的命令也以 IOException 结束且报 ReadError
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        rig.Hooks.HeartbeatPoll = TimeSpan.FromSeconds(30);   // 必须在 Selected 之前:排除"轮询到点回循环顶排信箱"的旁路
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var disconnects = new OnlineDisconnectLog();
        client.Disconnected += disconnects.Add;
        Task<FrameStatus> queuedOnReadError = null;
        var enteredRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams[0].OnReadResult = n =>
        {
            if (n == 0)
            {
                queuedOnReadError = client.SendCommandAsync(902, new byte[] { 3 });
                enteredRead.TrySetResult(0);
            }
            return n;
        };

        await ParkPumpOnReadAsync(streams[0], pipes[0]);
        // ReadFault 对"下一笔读返回之后"的检查生效,必须紧贴 CloseServer 之前设置:二者之间不能再等任何东西,
        // 否则泵会在屏障之后的某次检查里先因读错误退出,钩子里入信箱的命令就直接得到 InvalidOperationException。
        rig.Channels[0].Link.ReadFault = true;
        pipes[0].CloseServer();
        await Bounded(enteredRead.Task);
        await Assert.ThrowsAsync<IOException>(() => Bounded(queuedOnReadError));
        await WaitUntil(() => disconnects.Count == 1);
        Assert.Equal(new[] { OnlineDisconnectReason.ReadError }, disconnects.Reasons);
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 902);
        client.Dispose();
    }

    // ======== 取消语义 ========

    [Fact]
    public async Task Cancellation_before_write_stops_queued_command_while_in_flight_write_finishes_intact()
    {
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);

        // 预取消令牌
        using (var preCancelled = new CancellationTokenSource())
        {
            preCancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendCommandAsync(901, new byte[] { 1 }, preCancelled.Token));
        }
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 901);

        // 在途写不受取消打断;还在队列的命令被取消后不写出
        using var inFlightCts = new CancellationTokenSource();
        using var queuedCts = new CancellationTokenSource();
        var enteredWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams[0].OnWriteAsync = async _ =>
        {
            enteredWrite.TrySetResult(0);
            await releaseWrite.Task;
        };

        Task<FrameStatus> inFlight = client.SendCommandAsync(902, new byte[] { 0xAA, 0xBB }, inFlightCts.Token);
        await Bounded(enteredWrite.Task);
        streams[0].OnWriteAsync = null;
        Task<FrameStatus> queued = client.SendCommandAsync(903, new byte[] { 0xCC }, queuedCts.Token);

        inFlightCts.Cancel();
        queuedCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(queued));

        releaseWrite.TrySetResult(0);
        Assert.Equal(FrameStatus.Ok, await Bounded(inFlight));
        Assert.Single(pipes[0].ClientFrames, f => f.Id == 902);
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 903);
        client.Dispose();
    }

    // ======== 非心跳帧观察者 ========

    [Fact]
    public async Task Observer_delivers_non_heartbeat_frames_in_order_including_cmd_replies_and_excludes_heartbeat()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var received = new ConcurrentQueue<OnlineReceivedFrame>();
        client.FrameReceived += f => received.Enqueue(f);

        DungeonEntryPipe pipe = rig.Channels[0].Pipe;
        pipe.Push(DungeonEntryWire.Notice(23, new byte[] { 1, 2, 3 }));
        pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));           // 心跳:必须回复,但不交付给观察者
        pipe.Push(DungeonEntryWire.Reply(38, new byte[] { 0, 0x44, 1 }));  // 类 1 命令回包也必须交付
        pipe.Push(DungeonEntryWire.Notice(24, new byte[] { 4, 5, 6, 7 }));

        await WaitUntil(() => received.Count == 3 && pipe.ClientFrames.Any(f => f.Id == 0));

        OnlineReceivedFrame[] frames = received.ToArray();
        Assert.Equal(3, frames.Length);
        Assert.Equal((PacketClass.Noti, 23), (frames[0].PacketClass, frames[0].PacketId));
        Assert.Equal(new byte[] { 1, 2, 3 }, frames[0].Payload.Take(3).ToArray());
        Assert.Equal((PacketClass.Cmd, 38), (frames[1].PacketClass, frames[1].PacketId));
        Assert.Equal(new byte[] { 0, 0x44, 1 }, frames[1].Payload.Take(3).ToArray());
        Assert.Equal((PacketClass.Noti, 24), (frames[2].PacketClass, frames[2].PacketId));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, frames[2].Payload.Take(4).ToArray());
        Assert.Single(pipe.ClientFrames, f => f.Id == 0);
        Assert.Equal("CMD/38(" + frames[1].PayloadLength + ")", frames[1].ToString());
        client.Dispose();
    }

    [Fact]
    public async Task Without_subscribers_non_heartbeat_frames_are_not_dispatched_and_wire_bytes_match_before()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var dispatcher = new RecordingDispatcher();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: dispatcher);
        int writesBefore = rig.Channels[0].Recording.Writes.Count;

        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(901, new byte[] { 9, 9 }));
        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));
        await WaitUntil(() => rig.Channels[0].Pipe.ClientFrames.Count(f => f.Id == 0) == 1);

        Assert.Equal(0, dispatcher.Calls);
        IReadOnlyList<byte[]> newWrites = rig.Channels[0].Recording.Writes.Skip(writesBefore).ToArray();
        Assert.Single(newWrites);
        DungeonEntryClientFrame reply = rig.Channels[0].Pipe.ClientFrames.Last();
        Assert.Equal(PacketClass.Cmd, reply.Class);
        Assert.Equal(0, reply.Id);
        Assert.Equal(new byte[8], reply.Plain.Take(8).ToArray());
        client.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_subscribers_isolated_snapshots_unsubscription_and_exception_swallowing(bool useInlineDispatcher)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        IOnlineDispatcher dispatcher = useInlineDispatcher ? new OnlineInlineDispatcher() : null;
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: dispatcher);

        var list1 = new ConcurrentQueue<OnlineReceivedFrame>();
        var list2 = new ConcurrentQueue<OnlineReceivedFrame>();
        Action<OnlineReceivedFrame> sub1 = f =>
        {
            byte[] p = f.Payload;
            if (p.Length > 0) p[0] ^= 0xFF; // 修改拿到的载荷副本,不得影响其它读取者或后续订阅者
            list1.Enqueue(f);
            throw new InvalidOperationException("synthetic handler fault");
        };
        Action<OnlineReceivedFrame> sub2 = f => list2.Enqueue(f);

        client.FrameReceived += sub1;
        client.FrameReceived += sub2;

        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(901, new byte[] { 0x12, 0x34 }));
        await WaitUntil(() => list2.Count == 1);

        Assert.Single(list1);
        Assert.Single(list2);
        Assert.True(list1.TryPeek(out OnlineReceivedFrame f1));
        Assert.True(list2.TryPeek(out OnlineReceivedFrame f2));
        Assert.Equal((byte)0x12, f1.Payload[0]); // 再次读 Payload 仍未被篡改
        Assert.Equal((byte)0x12, f2.Payload[0]);

        // 退订 sub1 后只有 sub2 收到第二帧
        client.FrameReceived -= sub1;
        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(902, new byte[] { 0x56 }));
        await WaitUntil(() => list2.Count == 2);
        Assert.Single(list1);
        client.Dispose();
    }

    [Fact]
    public async Task Envelope_561_delivers_inner_frame_once_without_envelope_and_inflates_compressed_frames()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var received = new ConcurrentQueue<OnlineReceivedFrame>();
        client.FrameReceived += f => received.Enqueue(f);
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        byte[] innerPlain = { 0x21, 0x22, 0x23, 0x24 };
        byte[] bigPlain = UserInfoTestData.Packet(UserInfoTestData.Standard(0));
        pipe.Push(HostTestKit.RealEnvelope(0, 905, innerPlain));                  // 561 信封普通内层帧
        pipe.Push(HostTestKit.RealEnvelope(0, 0, Array.Empty<byte>()));           // 561 信封心跳
        pipe.Push(DungeonEntryWire.Envelope(GatewayTestSupport.Pattern(25, 3)));  // 打不开的 561 信封
        pipe.Push(DungeonEntryWire.Notice(2, UserInfoTestData.Zlib(bigPlain)));   // 直接压缩帧
        pipe.Push(HostTestKit.RealEnvelope(0, 2, UserInfoTestData.Zlib(bigPlain))); // 561 信封内压缩帧

        await WaitUntil(() => received.Count == 3 && pipe.ClientFrames.Count(f => f.Id == 0) == 1);

        OnlineReceivedFrame[] items = received.ToArray();
        Assert.DoesNotContain(items, f => f.PacketId == AntibotEnvelope.PacketId);
        Assert.Equal(new[] { 905, 2, 2 }, items.Select(f => f.PacketId).ToArray());
        Assert.Equal(innerPlain, items[0].Payload.Take(innerPlain.Length).ToArray());
        Assert.False(items[0].WasCompressed);
        Assert.True(items[1].WasCompressed);
        Assert.Equal(bigPlain, items[1].Payload);
        Assert.True(items[2].WasCompressed);
        Assert.Equal(bigPlain, items[2].Payload);
        Assert.Contains(client.GameSession.Events, e => e.Note.StartsWith("信封拆包失败:", StringComparison.Ordinal));
        client.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnected_arrives_after_previously_received_frames_in_FIFO_order(bool useInlineDispatcher)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        IOnlineDispatcher dispatcher = useInlineDispatcher ? new OnlineInlineDispatcher() : null;
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: dispatcher);
        var order = new ConcurrentQueue<string>();
        client.FrameReceived += f => order.Enqueue("frame:" + f.PacketId);
        client.Disconnected += r => order.Enqueue("disconnect:" + r);

        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(901, new byte[] { 1 }));
        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(902, new byte[] { 2 }));
        await WaitUntil(() => rig.Channels[0].Pipe.ClientFrames.Count >= 0 && order.Count >= 2);
        rig.Channels[0].Pipe.CloseServer();

        await WaitUntil(() => order.Count == 3);
        Assert.Equal(new[] { "frame:901", "frame:902", "disconnect:PeerClosed" }, order.ToArray());
        client.Dispose();
    }

    [Fact]
    public async Task Sync_dispatcher_allows_SendCommandAsync_fire_and_forget_from_FrameReceived_and_custom_dispatcher_runs_callbacks()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var dispatcher = new RecordingDispatcher();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: dispatcher);

        Task<FrameStatus> reentrantSend = null;
        var delivered = new TaskCompletionSource<OnlineReceivedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += f =>
        {
            if (f.PacketId == 901 && reentrantSend == null)
            {
                reentrantSend = client.SendCommandAsync(909, new byte[] { 0x7A });
            }
            delivered.TrySetResult(f);
        };

        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(901, new byte[] { 1 }));
        OnlineReceivedFrame frame = await Bounded(delivered.Task);
        Assert.Equal(901, frame.PacketId);
        Assert.NotNull(reentrantSend);
        Assert.Equal(FrameStatus.Ok, await Bounded(reentrantSend));
        Assert.Single(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 909);
        Assert.True(dispatcher.Calls >= 1);
        client.Dispose();
    }

    [Fact]
    public async Task Production_default_poll_window_delivers_commands_and_frames_within_250ms()
    {
        var defaults = new OnlineClientSessionHooks();
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatPoll = defaults.HeartbeatPoll;
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromMinutes(11);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);

        int[] staggersMs = { 10, 21, 33, 42, 7 };
        var cmdMs = new List<double>(staggersMs.Length);
        var frameMs = new List<double>(staggersMs.Length);

        var frameClock = new Stopwatch();
        TaskCompletionSource<TimeSpan> pendingFrame = null;
        ushort expectedFrameId = 0;
        client.FrameReceived += f =>
        {
            if (f.PacketId == expectedFrameId)
            {
                Volatile.Read(ref pendingFrame)?.TrySetResult(frameClock.Elapsed);
            }
        };

        for (int i = 0; i < staggersMs.Length; i++)
        {
            await Task.Delay(staggersMs[i]);

            // 1) 命令入队到流写出的延迟 <= 250ms
            var writeClock = Stopwatch.StartNew();
            TimeSpan writeElapsed = default;
            var wrote = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            streams[0].OnWriteAsync = _ =>
            {
                writeElapsed = writeClock.Elapsed;
                wrote.TrySetResult(0);
                return Task.CompletedTask;
            };
            writeClock.Restart();
            Task<FrameStatus> sendTask = client.SendCommandAsync((ushort)(901 + i), new byte[] { 1, (byte)i });
            await Bounded(wrote.Task);
            streams[0].OnWriteAsync = null;
            Assert.Equal(FrameStatus.Ok, await Bounded(sendTask));
            Assert.True(writeElapsed <= TimeSpan.FromMilliseconds(250), $"命令写出延迟过高: {writeElapsed.TotalMilliseconds:F1}ms");
            cmdMs.Add(writeElapsed.TotalMilliseconds);

            // 2) 非心跳帧到达到交给派发器/观察者的延迟 <= 250ms
            await Task.Delay(staggersMs[(i + 2) % staggersMs.Length]);
            expectedFrameId = (ushort)(920 + i);
            var tcs = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pendingFrame, tcs);
            frameClock.Restart();
            pipes[0].Push(DungeonEntryWire.Notice(expectedFrameId, new byte[] { 3, (byte)i }));
            TimeSpan frameElapsed = await Bounded(tcs.Task);
            Volatile.Write(ref pendingFrame, null);
            Assert.True(frameElapsed <= TimeSpan.FromMilliseconds(250), $"帧交付延迟过高: {frameElapsed.TotalMilliseconds:F1}ms");
            frameMs.Add(frameElapsed.TotalMilliseconds);
        }

        _output.WriteLine(
            $"poll={defaults.HeartbeatPoll.TotalMilliseconds:F0}ms " +
            $"cmd_ms=[{string.Join(", ", cmdMs.Select(x => x.ToString("F2")))}] (avg={cmdMs.Average():F2}, max={cmdMs.Max():F2}) " +
            $"frame_ms=[{string.Join(", ", frameMs.Select(x => x.ToString("F2")))}] (avg={frameMs.Average():F2}, max={frameMs.Max():F2})");
        client.Dispose();
    }

    // ======== 评审补强:变异幸存者对应的缺口(每条注释写明它钉住什么) ========

    /// <summary>
    /// 泵每次只等一帧(<c>ReceiveAsync</c> 的 stopAfterFrames 为 1):HeartbeatPoll 设成 10 秒,一帧非心跳帧在轮询窗口还没到点时就必须交付。
    /// 若泵改成"收满整个窗口"(0)或"等两帧才返回"(2),这一帧要等 10 秒窗口结束才会交付,5 秒的有界等待会失败。
    /// </summary>
    [Fact]
    public async Task Pump_returns_from_the_receive_window_after_a_single_frame_instead_of_waiting_out_the_poll()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatPoll = TimeSpan.FromSeconds(10);     // 必须在 Selected 之前
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += f => delivered.TrySetResult(f.PacketId);

        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(901, new byte[] { 7 }));

        Assert.Equal(901, await Bounded(delivered.Task));
        client.Dispose();
    }

    /// <summary>
    /// 心跳回复的写不能被取消:回复写到一半(卡在写门里)时停泵(<c>SetHeartbeatAsync(false)</c> 的同步段已经取消了泵的令牌),
    /// 放开写门之后这条回复必须完整落到线上——半个帧或丢帧会破坏帧边界。若回复的写改成可被泵令牌取消,放开后写门之后的检查会抛取消,线上没有回复。
    /// </summary>
    [Fact]
    public async Task Heartbeat_reply_in_flight_is_written_completely_even_when_the_pump_is_stopped_meanwhile()
    {
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        int repliesBefore = pipes[0].ClientFrames.Count(f => f.Class == PacketClass.Cmd && f.Id == 0);
        var enteredReplyWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplyWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams[0].OnWriteAsync = async _ =>
        {
            enteredReplyWrite.TrySetResult(0);
            await releaseReplyWrite.Task;
        };

        pipes[0].Push(DungeonEntryWire.Notice(0, new byte[] { 0 }));          // 心跳请求:泵开始写回复,卡在写门里
        await Bounded(enteredReplyWrite.Task);
        streams[0].OnWriteAsync = null;
        Task stop = client.SetHeartbeatAsync(false);                          // 同步段:关信箱、取消泵令牌;随后等泵退出
        releaseReplyWrite.TrySetResult(0);
        await Bounded(stop);

        DungeonEntryClientFrame[] replies = pipes[0].ClientFrames.Where(f => f.Class == PacketClass.Cmd && f.Id == 0).Skip(repliesBefore).ToArray();
        DungeonEntryClientFrame reply = Assert.Single(replies);
        Assert.Equal(new byte[8], reply.Plain.Take(8).ToArray());
        Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
        client.Dispose();
    }

    /// <summary>
    /// 会话还没释放时写流抛 ObjectDisposedException(底层连接被别处关了):按写错误处理——
    /// 命令以 IOException 结束(InnerException 是那个 ObjectDisposedException,不是 ObjectDisposedException 本身),
    /// Disconnected(WriteError) 恰好一次,命令没有写出。
    /// </summary>
    [Fact]
    public async Task ObjectDisposedException_from_a_write_on_a_live_session_is_reported_once_as_WriteError_and_wrapped_in_IOException()
    {
        OnlineFlowRig rig = CreateGatedRig(out _, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var disconnects = new OnlineDisconnectLog();
        client.Disconnected += disconnects.Add;
        var inner = new ObjectDisposedException("synthetic stream disposed");
        pipes[0].WriteFailure = inner;

        IOException ex = await Assert.ThrowsAsync<IOException>(() => Bounded(client.SendCommandAsync(901, new byte[] { 1 })));

        Assert.Same(inner, ex.InnerException);
        await WaitUntil(() => disconnects.Count == 1);
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
        Assert.Equal(new[] { OnlineDisconnectReason.WriteError }, disconnects.Reasons);
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 901);
        client.Dispose();
    }

    /// <summary>
    /// 命令载荷的副本:1 字节的载荷要完整发出(只有长度 0 才走"空载荷"分支),入参之后被改也不影响;
    /// 发出的载荷长度(会话事件里的未补零长度)分别是 1 与 0。
    /// </summary>
    [Fact]
    public async Task SendCommandAsync_sends_a_one_byte_payload_intact_and_an_empty_payload_with_zero_length()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        byte[] one = { 0x5A };
        Task<FrameStatus> sendOne = client.SendCommandAsync(901, one);
        one[0] = 0x00;                                                         // 入参随后被改:发出的仍是 0x5A
        Assert.Equal(FrameStatus.Ok, await Bounded(sendOne));
        Assert.Equal(FrameStatus.Ok, await Bounded(client.SendCommandAsync(902, Array.Empty<byte>())));

        DungeonEntryClientFrame oneFrame = pipe.ClientFrames.Single(f => f.Id == 901);
        Assert.Equal((byte)0x5A, oneFrame.Plain[0]);
        Assert.All(oneFrame.Plain.Skip(1), b => Assert.Equal(0, b));
        SessionEvent[] sent = client.GameSession.Events.Where(e => e.Outbound && e.PacketClass == PacketClass.Cmd && (e.PacketId == 901 || e.PacketId == 902)).ToArray();
        Assert.Equal(new[] { (901, 1), (902, 0) }, sent.Select(e => (e.PacketId, e.PayloadLength)).ToArray());
        client.Dispose();
    }

    /// <summary>
    /// 观察者帧的载荷副本:任意长度(含 1 字节、0 字节)都原样保留;null 当空载荷;
    /// 构造后改动传入的数组、改动读到的 Payload 数组,都不影响之后的读取。
    /// </summary>
    [Fact]
    public void OnlineReceivedFrame_copies_payloads_of_every_length_and_treats_null_as_empty()
    {
        byte[] one = { 0x5A };
        var oneFrame = new OnlineReceivedFrame(PacketClass.Noti, 905, one, false);
        one[0] = 0x00;                                                         // 构造之后改动传入的数组:不影响帧
        Assert.Equal(new byte[] { 0x5A }, oneFrame.Payload);
        Assert.Equal(1, oneFrame.PayloadLength);
        byte[] returned = oneFrame.Payload;
        returned[0] = 0x00;                                                    // 改动读到的副本:后续读取不受影响
        Assert.Equal(new byte[] { 0x5A }, oneFrame.Payload);
        Assert.NotSame(oneFrame.Payload, oneFrame.Payload);                    // 每次读都是新副本

        byte[] three = { 1, 2, 3 };
        var threeFrame = new OnlineReceivedFrame(PacketClass.Cmd, 906, three, true);
        three[1] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, threeFrame.Payload);
        Assert.Equal(3, threeFrame.PayloadLength);
        Assert.True(threeFrame.WasCompressed);

        var emptyFrame = new OnlineReceivedFrame(PacketClass.Cmd, 907, Array.Empty<byte>(), false);
        Assert.Empty(emptyFrame.Payload);
        Assert.Equal(0, emptyFrame.PayloadLength);

        var nullFrame = new OnlineReceivedFrame(PacketClass.Cmd, 908, null, false);
        Assert.Empty(nullFrame.Payload);
        Assert.Equal(0, nullFrame.PayloadLength);
    }

    /// <summary>真会话路径上的同一件事:561 信封里内层帧的载荷恰好 1 字节、恰好 0 字节,交付给观察者的载荷长度与内容都对。</summary>
    [Fact]
    public async Task Delivered_frames_keep_one_byte_and_zero_length_payloads_exactly()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var received = new ConcurrentQueue<OnlineReceivedFrame>();
        client.FrameReceived += f => received.Enqueue(f);
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        pipe.Push(HostTestKit.RealEnvelope(0, 905, new byte[] { 0x5A }));
        pipe.Push(HostTestKit.RealEnvelope(0, 906, Array.Empty<byte>()));
        await WaitUntil(() => received.Count == 2);

        OnlineReceivedFrame[] frames = received.ToArray();
        Assert.Equal(new[] { 905, 906 }, frames.Select(f => f.PacketId).ToArray());
        Assert.Equal(new byte[] { 0x5A }, frames[0].Payload);
        Assert.Equal(1, frames[0].PayloadLength);
        Assert.Empty(frames[1].Payload);
        Assert.Equal(0, frames[1].PayloadLength);
        client.Dispose();
    }

    /// <summary>
    /// "释放进行中"窗口:Dispose 已把 _disposed 置位,却还在等门闩(另一个操作持着门闩等泵退出,而泵卡在一条在途命令的写门里),State 还不是 Disposed。
    /// 窗口里三件事都要对:新命令以 ObjectDisposedException 失败(而不是"心跳泵未运行"的 InvalidOperationException);第二次 Dispose 立刻返回(不去抢门闩);
    /// 在途命令的写随后抛 ObjectDisposedException,它以 ObjectDisposedException 结束(不是被当成写错误包成 IOException),也不触发 Disconnected。
    /// </summary>
    [Fact]
    public async Task Dispose_in_progress_window_rejects_new_commands_returns_from_a_second_dispose_and_fails_the_in_flight_write_as_disposed()
    {
        OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out List<DungeonEntryPipe> pipes);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var disconnects = new OnlineDisconnectLog();
        client.Disconnected += disconnects.Add;
        var enteredWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams[0].OnWriteAsync = async _ =>
        {
            enteredWrite.TrySetResult(0);
            await releaseWrite.Task;
        };
        Task<FrameStatus> inFlight = client.SendCommandAsync(901, new byte[] { 1 });
        await Bounded(enteredWrite.Task);
        streams[0].OnWriteAsync = null;
        pipes[0].WriteFailure = new ObjectDisposedException("synthetic stream disposed");     // 写门放行后这条在途写抛 ObjectDisposedException

        Task stop = null;
        Task dispose = null;
        try
        {
            stop = client.SetHeartbeatAsync(false);        // 同步段已关信箱;持着门闩等泵退出,泵卡在写门里,门闩被占住
            dispose = Task.Run(client.Dispose);            // 置位 _disposed 之后卡在门闩上
            await WaitUntil(() => client.SendCommandAsync(902, new byte[] { 2 }).Exception?.InnerException is ObjectDisposedException);
            await Bounded(Task.Run(client.Dispose));       // 第二次 Dispose:释放已在进行,立刻返回
        }
        finally
        {
            releaseWrite.TrySetResult(0);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(inFlight));
        await Bounded(stop);
        await Bounded(dispose);
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
        Assert.Empty(disconnects.Reasons);
        Assert.DoesNotContain(pipes[0].ClientFrames, f => f.Id == 902);
    }

    /// <summary>
    /// 释放窗口里的 <c>LeaveExclusiveReceiveAsync</c>:Leave 持着门闩、卡在内联的断线回调里(此时 Dispose 已置位 _disposed 并卡在门闩上),
    /// 回调放开后 Leave 往下走到"恢复心跳":会话已经在释放了,不能再起泵(起了泵就会有一个没人收尾的泵和一个永远是 Running 的心跳状态)。
    /// 检查点:释放结束后心跳状态必须是 Disabled。万一泵被误起,它的第一笔读在流的"读被发起"钩子里卡住,直到 Dispose 全部做完才放行,
    /// 使"泵的收尾发生在链路引用清空之后、状态改不回 Disabled"这条路径必然出现;泵起步与 Dispose 推进谁先谁后每轮不同,所以多轮重复。
    /// 正确实现里泵根本不会起来,钩子从不触发,每轮都是确定性的。
    /// </summary>
    [Fact]
    public async Task Leave_exclusive_receive_inside_the_dispose_window_does_not_restart_the_pump()
    {
        for (int round = 0; round < 30; round++)
        {
            OnlineFlowRig rig = CreateGatedRig(out List<GatedRecordingStream> streams, out _);
            rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: new OnlineInlineDispatcher());
            await Bounded(client.EnterExclusiveReceiveAsync());
            rig.Channels[0].Link.ReadFault = true;          // 独占期间链路出了读错误:Leave 里的断线检查会同步回调 Disconnected

            var enteredHandler = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseHandler = new ManualResetEventSlim(false);
            using var releasePump = new ManualResetEventSlim(false);
            client.Disconnected += _ =>
            {
                enteredHandler.TrySetResult(0);
                releaseHandler.Wait(TimeSpan.FromSeconds(15));
            };
            streams[0].OnReadStarted = _ => releasePump.Wait(TimeSpan.FromSeconds(15));

            Task leave = Task.Run(() => client.LeaveExclusiveReceiveAsync());
            Task dispose = null;
            try
            {
                await Bounded(enteredHandler.Task);                     // Leave 持着门闩,卡在内联断线回调里
                dispose = Task.Run(client.Dispose);                     // 置位 _disposed 之后卡在门闩上
                await WaitUntil(() => client.SendCommandAsync(901, new byte[] { 1 }).Exception?.InnerException is ObjectDisposedException);
            }
            finally
            {
                releaseHandler.Set();
            }

            await Bounded(leave);
            await Bounded(dispose);
            Assert.Equal(OnlineClientSessionState.Disposed, client.State);
            Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
            releasePump.Set();
        }
    }
}
