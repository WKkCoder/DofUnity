using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 会话被交给游戏(<see cref="OnlineClientSession.Detach"/>)那一刻,命令信箱里的事(r21-createcharui2 收尾时补的缺口):
/// 创建/删除角色用的 <c>Detach</c> 交接路径上,"关掉信箱"这一步此前没有任何测试能分辨有没有做。
/// 与同目录的 Gate/Settle 测试同风格:全部走内存里的假管道和确定性的写门,每个等待都有上限,不靠墙钟睡眠同步;
/// 命令包号与载荷都是 900 起的合成值,不连任何真实服务。
/// </summary>
public sealed class OnlineClientSessionDetachTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script()
    {
        return HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            null);
    }

    /// <summary>
    /// 心跳泵正卡在一条在途命令的写里(写门没放),信箱里又排了一条还没写的命令;这时会话被交给游戏。
    /// 交接不能等那条在途写(它已经交给网络栈,取消管不到),但信箱里没写出去的命令必须<b>当场</b>以"已释放"失败——
    /// 写门还关着的时候就要结束,不能一直排到泵腾出手来;之后新命令也一律按"已释放"拒绝。
    /// 放开写门后在途的那条照常写完,泵不会再回头去写已经失败的那一条:交出去的连接上只有在途命令的一帧。
    /// </summary>
    [Fact]
    public async Task Handing_the_session_over_fails_a_queued_command_as_disposed_at_once_and_never_writes_it_afterwards()
    {
        var holder = new GatedChannel();
        OnlineFlowRig rig = GatedRig(holder);
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);

        var writing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.Gate.BeforeWrite = async () =>
        {
            writing.TrySetResult(0);
            await release.Task;
        };

        OnlineEntryResult handedOver = null;
        try
        {
            Task<FrameStatus> inFlight = client.SendCommandAsync(901, new byte[] { 1 });
            await Bounded(writing.Task);                      // 泵已经把 901 的写停在流里
            holder.Gate.BeforeWrite = null;                   // 往后的写不再被拦

            Task<FrameStatus> queued = client.SendCommandAsync(902, new byte[] { 2 });
            Assert.False(queued.IsCompleted);                 // 泵忙在写里,902 只能排在信箱里

            handedOver = client.Detach(OnlineTownEntry.Fail(
                OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, null, -1, 0, null, null, 0));
            Assert.Equal(OnlineClientSessionState.Disposed, client.State);

            // 写门还关着:信箱里的 902 要已经以"已释放"结束(不是"停泵"的无效操作、也不是还在排队)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(queued));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(client.SendCommandAsync(903, new byte[] { 3 })));

            release.TrySetResult(0);
            Assert.Equal(FrameStatus.Ok, await Bounded(inFlight));
        }
        finally
        {
            release.TrySetResult(0);
            handedOver?.Dispose();
        }

        Assert.Contains(holder.Pipe.ClientFrames, f => f.Id == 901);
        Assert.DoesNotContain(holder.Pipe.ClientFrames, f => f.Id == 902 || f.Id == 903);
    }

    // ------------------------------------------------------------------ 把写卡在流里的假频道(与故障测试里的同款,那边是私有的)

    private sealed class GatedChannel
    {
        internal WriteGateStream Gate;
        internal DungeonEntryPipe Pipe;
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
