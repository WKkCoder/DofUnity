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

        public GatedRecordingStream(Stream inner) => _inner = inner;

        public Func<byte[], Task> OnWriteAsync { get; set; }
        public Func<int, int> OnReadResult { get; set; }

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

    [Fact]
    public async Task Queued_unsent_command_fails_with_IOException_on_pump_disconnect_and_write_error()
    {
        // 1) 读端返回 0(PeerClosed)时已在信箱里的命令必须以 IOException 结束且不写出
        OnlineFlowRig rig1 = CreateGatedRig(out List<GatedRecordingStream> streams1, out List<DungeonEntryPipe> pipes1);
        rig1.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client1, _) = await rig1.Selected(autoHeartbeat: true);
        Task<FrameStatus> queuedOnClose = null;
        var pushedOnRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams1[0].OnReadResult = n =>
        {
            if (n == 0)
            {
                queuedOnClose = client1.SendCommandAsync(902, new byte[] { 2 });
                pushedOnRead.TrySetResult(0);
            }
            return n;
        };
        pipes1[0].CloseServer();
        await Bounded(pushedOnRead.Task);
        await Assert.ThrowsAsync<IOException>(() => Bounded(queuedOnClose));
        Assert.DoesNotContain(pipes1[0].ClientFrames, f => f.Id == 902);
        client1.Dispose();

        // 2) 写失败(WriteFailure):第一条抛 IOException,Disconnected(WriteError) 恰好一次,队列第二条也以 IOException 结束
        OnlineFlowRig rig2 = CreateGatedRig(out List<GatedRecordingStream> streams2, out List<DungeonEntryPipe> pipes2);
        rig2.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client2, _) = await rig2.Selected(autoHeartbeat: true);
        var disconnects = new OnlineDisconnectLog();
        client2.Disconnected += disconnects.Add;

        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams2[0].OnWriteAsync = async _ =>
        {
            entered.TrySetResult(0);
            await release.Task;
        };
        Task<FrameStatus> cmd1 = client2.SendCommandAsync(901, new byte[] { 1 });
        await Bounded(entered.Task);
        streams2[0].OnWriteAsync = null;
        Task<FrameStatus> cmd2 = client2.SendCommandAsync(902, new byte[] { 2 });
        pipes2[0].WriteFailure = new IOException("synthetic write error");
        release.TrySetResult(0);

        await Assert.ThrowsAsync<IOException>(() => Bounded(cmd1));
        await Assert.ThrowsAsync<IOException>(() => Bounded(cmd2));
        await WaitUntil(() => disconnects.Count == 1);
        await WaitUntil(() => client2.HeartbeatState == OnlineHeartbeatState.Disabled);
        Assert.Equal(new[] { OnlineDisconnectReason.WriteError }, disconnects.Reasons);
        Assert.DoesNotContain(pipes2[0].ClientFrames, f => f.Id == 902);
        client2.Dispose();

        // 3) 读错误(ReadFault):在读端返回前入队的命令也以 IOException 结束且报 ReadError
        OnlineFlowRig rig3 = CreateGatedRig(out List<GatedRecordingStream> streams3, out List<DungeonEntryPipe> pipes3);
        rig3.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client3, _) = await rig3.Selected(autoHeartbeat: true);
        var disconnects3 = new OnlineDisconnectLog();
        client3.Disconnected += disconnects3.Add;
        Task<FrameStatus> queuedOnReadError = null;
        var enteredRead3 = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        streams3[0].OnReadResult = n =>
        {
            if (n == 0)
            {
                queuedOnReadError = client3.SendCommandAsync(902, new byte[] { 3 });
                enteredRead3.TrySetResult(0);
            }
            return n;
        };
        rig3.Channels[0].Link.ReadFault = true;
        pipes3[0].CloseServer();
        await Bounded(enteredRead3.Task);
        await Assert.ThrowsAsync<IOException>(() => Bounded(queuedOnReadError));
        await WaitUntil(() => disconnects3.Count == 1);
        Assert.Equal(new[] { OnlineDisconnectReason.ReadError }, disconnects3.Reasons);
        Assert.DoesNotContain(pipes3[0].ClientFrames, f => f.Id == 902);
        client3.Dispose();
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
}
