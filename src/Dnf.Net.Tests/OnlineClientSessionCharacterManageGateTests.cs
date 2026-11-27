using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网会话"创建/删除角色"在门闩与回复窗口上的补充测试(r21-createcharui2 第二轮):
/// 两次 Dispose 撞在一起时第二次不用等第一次、排队中的操作被取消时立刻结束(不用等占着门闩的那一个)、
/// 回复比名册窗口来得晚但仍在回复窗口之内时照样收下。
/// 与 <see cref="OnlineClientSessionCharacterManageSettleTests"/> 同风格:全部走内存里的假管道,数据是合成的(名字、槽号、职业都是 900 起的合成值),
/// 不连任何真实服务,也不触发任何真实创建或删除(写开关只对这条内存会话打开)。
/// 每个等待都有上限,不靠墙钟睡眠同步(唯一的一处延时见 <c>A_reply_that_arrives_later_than_the_roster_window...</c> 的说明:它只是给错误写法留出暴露的时间,正确写法的结局与它无关)。
/// </summary>
public sealed class OnlineClientSessionCharacterManageGateTests
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

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script()
    {
        return HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            null);
    }

    /// <summary>回复窗口 30 秒、名册窗口 100 毫秒:该等回复的地方写成名册窗口,调用会在 100 毫秒后就以"无回复"结束。</summary>
    private static OnlineTownWindows LongReplyWindow() => new OnlineTownWindows(
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(30));

    // ------------------------------------------------------------------ 两次 Dispose 撞在一起

    /// <summary>
    /// 写已经停在流里(占着门闩、取消管不到)时,第一次 Dispose 记下"已释放"后卡在等门闩上;
    /// 这时第二次 Dispose 看到"已经在释放了"就要立刻返回,不能也跟着去等门闩(那样它要等到第一次做完)。
    /// 放行写之后,在途的调用以"已释放"结束,第一次 Dispose 做完,连接只关闭一次。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_second_dispose_returns_at_once_while_the_first_one_still_waits_for_the_write_in_flight(bool create)
    {
        var holder = new GatedChannel();
        OnlineFlowRig rig = GatedRig(holder);
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;

        var writing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.Gate.BeforeWrite = async () =>
        {
            writing.TrySetResult(0);
            await release.Task;
        };
        Task<OnlineCharacterManageResult> call = Manage(client, create);
        try
        {
            await Bounded(writing.Task);

            holder.Pipe.WriteFailure = new ObjectDisposedException("synthetic-stream");
            Task first = Task.Run(client.Dispose);
            // 新命令被拒是第一次 Dispose 已经记下"已释放"的外部表现;此刻它卡在门闩上
            await WaitUntil(() => client.SendCommandAsync(902, new byte[] { 2 }).Exception?.InnerException is ObjectDisposedException);
            Assert.False(first.IsCompleted);

            Task second = Task.Run(client.Dispose);
            await Bounded(second);
            Assert.False(first.IsCompleted);

            release.TrySetResult(0);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(call));
            await Bounded(first);
        }
        finally
        {
            release.TrySetResult(0);
        }

        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
        Assert.Equal(1, holder.Connection.DisposeCount);
    }

    // ------------------------------------------------------------------ 排队中的操作被取消

    /// <summary>
    /// 第一个操作停在等回复上(占着门闩),第二个操作排在门闩后面;取消第二个操作的令牌,它要立刻以取消结束,
    /// 不能一直排到第一个操作放出门闩才结束;第一个操作不受影响,第二个操作也没有往连接上写任何东西。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelling_a_queued_operation_ends_it_at_once_without_waiting_for_the_one_holding_the_gate(bool create)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = LongReplyWindow();
        rig.ScriptFactory = _ => Script();
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        OnlineFlowChannel channel = rig.Channels[0];

        using var stopFirst = new CancellationTokenSource();
        using var stopSecond = new CancellationTokenSource();
        Task<OnlineCharacterManageResult> first = client.CreateCharacterAsync(3, Encoding.UTF8.GetBytes("Hero900"), 0, stopFirst.Token);
        try
        {
            await WaitUntil(() => channel.Pipe.ClientFrames.Any(f => f.Id == 5) && channel.Pipe.HasPendingRead);

            Task<OnlineCharacterManageResult> second = create
                ? client.CreateCharacterAsync(3, Encoding.UTF8.GetBytes("Hero902"), 0, stopSecond.Token)
                : client.DeleteCharacterAsync(1, Encoding.UTF8.GetBytes("Hero901"), stopSecond.Token);
            Assert.False(second.IsCompleted);

            stopSecond.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(second));
            Assert.False(first.IsCompleted);
            Assert.Single(channel.Pipe.ClientFrames, f => f.Id == 5);
            Assert.DoesNotContain(channel.Pipe.ClientFrames, f => f.Id == 6);
        }
        finally
        {
            stopFirst.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(first));
    }

    // ------------------------------------------------------------------ 回复比名册窗口来得晚

    /// <summary>
    /// 回复窗口是 30 秒、名册窗口只有 100 毫秒:发出请求后的第一段等待用的是回复窗口。
    /// 回复(和名册)在名册窗口早已过去之后才到,调用仍要收下它们、以成功结束;
    /// 把第一段等待也写成名册窗口的话,调用早在 100 毫秒时就以"无回复"结束了。
    /// 中间那段延时只是给这种错误写法留出暴露的时间,不是同步手段:正确写法在它之前、之后都在等,结局与它是否多等无关。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reply_that_arrives_later_than_the_roster_window_is_still_taken_within_the_reply_window(bool create)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = LongReplyWindow();
        rig.ScriptFactory = _ => Script();
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        OnlineFlowChannel channel = rig.Channels[0];
        ushort packetId = create ? (ushort)5 : (ushort)6;

        Task<OnlineCharacterManageResult> call = Manage(client, create);
        await WaitUntil(() => channel.Pipe.ClientFrames.Any(f => f.Id == packetId) && channel.Pipe.HasPendingRead);

        await Task.Delay(500);
        Assert.False(call.IsCompleted);

        // 回复与名册一次灌进去:读端一口气拿到两帧,不依赖两次灌入之间的间隔
        channel.Pipe.Push(DungeonEntryWire.Join(
            DungeonEntryWire.Reply(packetId, create ? new byte[] { 1 } : new byte[] { 1, 0 }),
            HostTestKit.DirectListNotice()));

        OnlineCharacterManageResult res = await Bounded(call);

        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.True(res.CommandSucceeded);
        Assert.True(res.IsSuccess);
        Assert.Equal(OnlineCharacterListStatus.Received, res.CharacterListStatus);
        Assert.NotNull(res.CharacterList);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    // ------------------------------------------------------------------ 把写卡在流里的假频道(与故障测试里的同款,那边是私有的)

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
