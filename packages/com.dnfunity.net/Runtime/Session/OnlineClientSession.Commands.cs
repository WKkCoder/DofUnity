using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    public sealed partial class OnlineClientSession
    {
        /// <summary>
        /// 城镇心跳泵收到的非心跳帧观察者(不含 0/0 心跳帧与 561 信封外壳;包含 561 解出的内层帧与类 1 命令回包)。
        /// <para>
        /// 派发与顺序:按到达顺序经构造函数传入的 <see cref="IOnlineDispatcher"/>(未传则用串行后台派发器)投递,不持有会话门闩;
        /// 多个订阅者各收到每帧一次的独立复制件,订阅者抛出的异常会被吞掉,不影响泵与其余订阅者;
        /// 断线事件(<see cref="Disconnected"/>)经同一派发器排队,因此保证排在此前已到达的帧之后;
        /// 没有订阅者时不复制载荷也不投递;订阅在 <see cref="EnterExclusiveReceiveAsync"/> / <see cref="LeaveExclusiveReceiveAsync"/> 往返之后仍然有效。
        /// </para>
        /// <para>
        /// 注意:若宿主注入了同步派发器,不得在回调内同步阻塞等待 <see cref="SendCommandAsync"/> 的结果(否则会卡住泵线程)。
        /// </para>
        /// </summary>
        public event Action<OnlineReceivedFrame> FrameReceived;

        /// <summary>
        /// 通过城镇心跳泵串行写出一帧命令(<see cref="PacketClass.Cmd"/>)。只在 <see cref="OnlineClientSessionState.CharacterSelected"/>
        /// 且心跳泵处于 <see cref="OnlineHeartbeatState.Running"/> 时可用;内部通过信箱把写操作留给泵所在逻辑线顺序执行,与心跳回包共用递增序号。
        /// <para>
        /// 返回任务在帧写出到流并刷新之后完成(使用 <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>,不在泵线程同步内联后续)。
        /// 帧层编码失败返回对应 <see cref="FrameStatus"/>(不写流、不推进序号);流写失败抛 <see cref="IOException"/> 并触发一次 <see cref="Disconnected"/>(<see cref="OnlineDisconnectReason.WriteError"/>)。
        /// 取消令牌只在命令尚未开始写出时生效(抛 <see cref="OperationCanceledException"/> 且不写出);一旦泵开始写流,不会中途打断写帧。
        /// 若未发出的命令遇到 <see cref="Dispose"/> 则抛 <see cref="ObjectDisposedException"/>;遇到 <see cref="DisconnectAsync"/>、
        /// <see cref="SwitchChannelAsync"/>、<see cref="EnterExclusiveReceiveAsync"/> 或 <see cref="SetHeartbeatAsync"/>(false) 则抛
        /// <see cref="InvalidOperationException"/>;遇到连接断开、读写失败或心跳超时导致泵退出则抛 <see cref="IOException"/>。
        /// </para>
        /// <para>
        /// 注意:若宿主注入了同步 <see cref="IOnlineDispatcher"/>,不得在 <see cref="FrameReceived"/> 或 <see cref="Disconnected"/>
        /// 回调内同步等待本方法返回的任务(可发起异步调用而不同步阻塞等待)。
        /// </para>
        /// </summary>
        /// <param name="packetId">命令包号(类固定为 <see cref="PacketClass.Cmd"/>)。</param>
        /// <param name="payload">明文载荷(调用时复制,调用方随后修改原数组不影响发出内容)。</param>
        /// <param name="cancellationToken">取消令牌(仅在命令尚未开始写出前生效)。</param>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> 为 null。</exception>
        /// <exception cref="ObjectDisposedException">会话已释放,或命令发出前会话被释放。</exception>
        /// <exception cref="InvalidOperationException">会话未处于选角后的城镇态、心跳泵未运行,或命令发出前状态切走/停泵。</exception>
        /// <exception cref="OperationCanceledException">命令在尚未开始写出前被取消。</exception>
        /// <exception cref="IOException">写流失败,或命令尚未写出时泵因断线/读写错误/超时而终止。</exception>
        public Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken = default)
        {
            PendingCommand command;
            CommandMailbox mailbox;
            lock (_mailboxGate)
            {
                if (_disposed || State == OnlineClientSessionState.Disposed)
                {
                    return Task.FromException<FrameStatus>(new ObjectDisposedException(nameof(OnlineClientSession)));
                }
                if (State != OnlineClientSessionState.CharacterSelected)
                {
                    return Task.FromException<FrameStatus>(new InvalidOperationException("只有选角成功且未进入独占收包时才能发送命令"));
                }
                mailbox = _activeMailbox;
                if (mailbox == null || HeartbeatState != OnlineHeartbeatState.Running)
                {
                    return Task.FromException<FrameStatus>(new InvalidOperationException("心跳泵未运行时不能发送命令"));
                }
                if (payload == null)
                {
                    return Task.FromException<FrameStatus>(new ArgumentNullException(nameof(payload)));
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled<FrameStatus>(cancellationToken);
                }

                byte[] copy = payload.Length == 0 ? Array.Empty<byte>() : (byte[])payload.Clone();
                command = new PendingCommand(mailbox, packetId, copy, cancellationToken);
                if (!mailbox.TryEnqueue(command))
                {
                    return Task.FromException<FrameStatus>(CommandMailbox.CreateCloseException(mailbox.CloseReason));
                }
            }

            if (cancellationToken.CanBeCanceled)
            {
                CancellationTokenRegistration registration = cancellationToken.Register(
                    static state => ((PendingCommand)state).CancelFromToken(), command, false);
                if (!mailbox.AttachRegistration(command, registration))
                {
                    registration.Dispose();
                }
            }

            return command.Completion.Task;
        }

        private void DeliverNonHeartbeatFrame(DecodedFrame frame)
        {
            Action<OnlineReceivedFrame> handler = FrameReceived;
            if (handler == null)
            {
                return;
            }

            Delegate[] subscribers = handler.GetInvocationList();
            byte[] source = frame.Payload ?? Array.Empty<byte>();
            var snapshot = new OnlineReceivedFrame(frame.Header.Class, frame.Header.PacketId, source, frame.WasCompressed);
            IOnlineDispatcher events = _dispatcher ?? OnlineBackgroundDispatcher.Instance;
            events.Dispatch(() =>
            {
                for (int i = 0; i < subscribers.Length; i++)
                {
                    try
                    {
                        ((Action<OnlineReceivedFrame>)subscribers[i])(snapshot);
                    }
                    catch (Exception)
                    {
                        // 订阅者自身异常被吞掉,不影响派发器后续回调与其他订阅者
                    }
                }
            });
        }

        private async Task<bool> DrainCommandsAsync(OnlineChannelLink link, CommandMailbox mailbox)
        {
            while (mailbox.TryDequeue(out PendingCommand command, out CancellationTokenRegistration registration))
            {
                registration.Dispose();
                try
                {
                    // 一旦开始向流写帧,绝不被调用方令牌中途取消,避免半个帧写出去破坏帧边界。
                    FrameStatus status = await link.Session.SendAsync(
                        PacketClass.Cmd, command.PacketId, command.Payload, CancellationToken.None).ConfigureAwait(false);
                    command.Completion.TrySetResult(status);
                }
                catch (IOException ex)
                {
                    NotifyDisconnected(link, OnlineDisconnectReason.WriteError);
                    command.Completion.TrySetException(ex);
                    return false;
                }
                catch (ObjectDisposedException ex)
                {
                    if (_disposed || State == OnlineClientSessionState.Disposed)
                    {
                        command.Completion.TrySetException(new ObjectDisposedException(nameof(OnlineClientSession)));
                    }
                    else
                    {
                        NotifyDisconnected(link, OnlineDisconnectReason.WriteError);
                        command.Completion.TrySetException(new IOException("连接已释放", ex));
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    command.Completion.TrySetException(ex);
                    return false;
                }
            }
            return true;
        }

        private void CloseActiveMailbox(CommandCloseReason reason)
        {
            CommandMailbox mailbox;
            lock (_mailboxGate)
            {
                mailbox = _activeMailbox;
                _activeMailbox = null;
            }
            mailbox?.Close(reason);
        }

        private void MarkDisposedAndCloseMailbox()
        {
            CommandMailbox mailbox;
            lock (_mailboxGate)
            {
                _disposed = true;
                mailbox = _activeMailbox;
                _activeMailbox = null;
            }
            mailbox?.Close(CommandCloseReason.Disposed);
        }

        private void CloseMailboxOnPumpExit(OnlineChannelLink link, CommandMailbox mailbox, CommandCloseReason closeReason)
        {
            lock (_mailboxGate)
            {
                if (ReferenceEquals(_activeMailbox, mailbox))
                {
                    _activeMailbox = null;
                }
                if (ReferenceEquals(_link, link) && HeartbeatState == OnlineHeartbeatState.Running)
                {
                    HeartbeatState = OnlineHeartbeatState.Disabled;
                }
            }
            mailbox.Close(closeReason);
        }

        private enum CommandCloseReason
        {
            Stopped = 0,
            Disposed = 1,
            IoFault = 2,
        }

        private enum PendingCommandState
        {
            Queued = 0,
            Dequeued = 1,
            Completed = 2,
        }

        private sealed class PendingCommand
        {
            private readonly CommandMailbox _owner;

            public PendingCommand(CommandMailbox owner, ushort packetId, byte[] payload, CancellationToken cancellationToken)
            {
                _owner = owner;
                PacketId = packetId;
                Payload = payload;
                CancellationToken = cancellationToken;
                Completion = new TaskCompletionSource<FrameStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public ushort PacketId { get; }
            public byte[] Payload { get; }
            public CancellationToken CancellationToken { get; }
            public TaskCompletionSource<FrameStatus> Completion { get; }
            public PendingCommandState State { get; set; }
            public CancellationTokenRegistration Registration { get; set; }

            public void CancelFromToken()
            {
                _owner.CancelCommand(this);
            }
        }

        private sealed class CommandMailbox
        {
            private readonly object _lock = new object();
            private readonly Queue<PendingCommand> _queue = new Queue<PendingCommand>();
            private bool _closed;
            private CommandCloseReason _closeReason;

            public CommandCloseReason CloseReason
            {
                get { lock (_lock) { return _closeReason; } }
            }

            public bool TryEnqueue(PendingCommand command)
            {
                lock (_lock)
                {
                    if (_closed)
                    {
                        return false;
                    }
                    command.State = PendingCommandState.Queued;
                    _queue.Enqueue(command);
                    return true;
                }
            }

            public bool AttachRegistration(PendingCommand command, CancellationTokenRegistration registration)
            {
                lock (_lock)
                {
                    if (command.State != PendingCommandState.Queued)
                    {
                        return false;
                    }
                    command.Registration = registration;
                    return true;
                }
            }

            public void CancelCommand(PendingCommand command)
            {
                lock (_lock)
                {
                    if (command.State != PendingCommandState.Queued)
                    {
                        return;
                    }
                    command.State = PendingCommandState.Completed;
                    command.Registration = default;
                }
                command.Completion.TrySetCanceled(command.CancellationToken);
            }

            public bool TryDequeue(out PendingCommand command, out CancellationTokenRegistration registration)
            {
                lock (_lock)
                {
                    while (_queue.Count > 0)
                    {
                        PendingCommand candidate = _queue.Dequeue();
                        if (candidate.State != PendingCommandState.Queued)
                        {
                            continue;
                        }
                        candidate.State = PendingCommandState.Dequeued;
                        registration = candidate.Registration;
                        candidate.Registration = default;
                        command = candidate;
                        return true;
                    }
                    command = null;
                    registration = default;
                    return false;
                }
            }

            public void Close(CommandCloseReason reason)
            {
                List<PendingCommand> pending = null;
                List<CancellationTokenRegistration> registrations = null;
                lock (_lock)
                {
                    if (_closed)
                    {
                        return;
                    }
                    _closed = true;
                    _closeReason = reason;
                    while (_queue.Count > 0)
                    {
                        PendingCommand item = _queue.Dequeue();
                        if (item.State != PendingCommandState.Queued)
                        {
                            continue;
                        }
                        item.State = PendingCommandState.Completed;
                        if (pending == null)
                        {
                            pending = new List<PendingCommand>();
                            registrations = new List<CancellationTokenRegistration>();
                        }
                        pending.Add(item);
                        registrations.Add(item.Registration);
                        item.Registration = default;
                    }
                }

                if (pending == null)
                {
                    return;
                }

                for (int i = 0; i < pending.Count; i++)
                {
                    registrations[i].Dispose();
                    pending[i].Completion.TrySetException(CreateCloseException(reason));
                }
            }

            public static Exception CreateCloseException(CommandCloseReason reason)
            {
                switch (reason)
                {
                    case CommandCloseReason.Disposed:
                        return new ObjectDisposedException(nameof(OnlineClientSession));
                    case CommandCloseReason.IoFault:
                        return new IOException("频道连接已断开或读写失败，未发出的命令未能写出");
                    default:
                        return new InvalidOperationException("会话状态变化或心跳泵已停止，未发出的命令已中止");
                }
            }
        }
    }
}
