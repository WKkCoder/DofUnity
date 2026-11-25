using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 钉住 <see cref="GameClientSession"/> 在收包超时/取消后把在途底层读任务留作 <c>_pendingRead</c>、
/// 随后底层流关闭导致该读任务失败时，不得在 GC 终结期泄漏 <see cref="TaskScheduler.UnobservedTaskException"/>；
/// 同时继续保留正常的流异常归类（<see cref="GameClientSession.PeerClosed"/>）。
/// </summary>
[Collection("UnobservedTaskException")]
public sealed class GameClientSessionUnobservedReadTests
{
    [Fact]
    public async Task Cancelled_receive_followed_by_stream_dispose_does_not_leak_unobserved_task_exception()
    {
        string readMarker = "pending-read-fault-" + Guid.NewGuid().ToString("N");
        string controlMarker = "control-unobserved-" + Guid.NewGuid().ToString("N");
        var observedMessages = new ConcurrentBag<string>();

        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            foreach (Exception inner in e.Exception.Flatten().InnerExceptions)
            {
                observedMessages.Add(inner.Message);
            }
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await CancelReceiveAndDisposeStreamAsync(readMarker);
            CreateUnobservedControlTask(controlMarker);

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        Assert.Contains(controlMarker, observedMessages);
        Assert.DoesNotContain(readMarker, observedMessages);
    }

    [Fact]
    public async Task Awaited_receive_still_treats_stream_io_exception_as_peer_closed()
    {
        using var stream = new FaultOnDisposeStream("awaited-read-fault");
        var session = new GameClientSession(stream);

        Task<List<Dnf.Net.Frame.DecodedFrame>> receiving = session.ReceiveAsync(TimeSpan.FromSeconds(10), 1);
        await stream.WaitReadStartedAsync();
        stream.Dispose();

        List<Dnf.Net.Frame.DecodedFrame> frames = await GatewayTestSupport.Bounded(receiving, 5);
        Assert.Empty(frames);
        Assert.True(session.PeerClosed);
    }

    [Fact]
    public async Task Refused_channel_connect_does_not_leak_unobserved_connect_exception()
    {
        var unobserved = new ConcurrentBag<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobserved.Add(e.Exception);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await ConnectToRefusedPortAsync();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        Assert.Empty(unobserved);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ConnectToRefusedPortAsync()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.ChannelFactory = null;
        rig.FakeGateway.GameServerIp = GatewayTestSupport.Dots(127, 0, 0, 1);
        using OnlineClientSession client = await rig.LoggedIn();
        OnlineConnectResult result = await GatewayTestSupport.Bounded(client.ConnectAsync(1), 10);
        Assert.False(result.IsSuccess);
        Assert.Equal(OnlineFailure.ConnectFailed, result.Failure);
    }

    [Fact]
    public async Task Cancelled_channel_connect_does_not_leak_unobserved_connect_exception()
    {
        var unobserved = new ConcurrentBag<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobserved.Add(e.Exception);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await CancelSlowChannelConnectAsync();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        Assert.Empty(unobserved);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CancelSlowChannelConnectAsync()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.ChannelFactory = null;
        rig.FakeGateway.GameServerIp = GatewayTestSupport.Dots(192, 0, 2, 10);
        using OnlineClientSession client = await rig.LoggedIn();
        using var cts = new CancellationTokenSource();
        Task<OnlineConnectResult> connecting = client.ConnectAsync(31011, cts.Token);
        cts.CancelAfter(15);
        try
        {
            await GatewayTestSupport.Bounded(connecting, 10);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CancelReceiveAndDisposeStreamAsync(string marker)
    {
        using var stream = new FaultOnDisposeStream(marker);
        var session = new GameClientSession(stream);
        using var cts = new CancellationTokenSource();

        Task<List<Dnf.Net.Frame.DecodedFrame>> receiving = session.ReceiveAsync(TimeSpan.FromSeconds(10), 1, cts.Token);
        await stream.WaitReadStartedAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
        stream.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateUnobservedControlTask(string marker)
    {
        _ = Task.FromException(new IOException(marker));
    }

    private sealed class FaultOnDisposeStream : Stream
    {
        private readonly string _faultMessage;
        private readonly TaskCompletionSource<bool> _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<int> _pendingRead;

        public FaultOnDisposeStream(string faultMessage)
        {
            _faultMessage = faultMessage;
        }

        public Task WaitReadStartedAsync() => _readStarted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _pendingRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readStarted.TrySetResult(true);
            return _pendingRead.Task;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pendingRead?.TrySetException(new IOException(_faultMessage));
            }
            base.Dispose(disposing);
        }
    }
}
