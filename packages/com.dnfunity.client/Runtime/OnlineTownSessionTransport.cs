using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client
{
    /// <summary>
    /// 把 Play 层 <see cref="ITownCommandTransport"/> 接到 <see cref="OnlineClientSession"/> 的命令发送与帧通知事件上:
    /// <list type="bullet">
    /// <item>构造时成对订阅 <c>FrameReceived</c> 与 <c>Disconnected</c>,<see cref="Dispose"/> 时成对退订,且绝不释放底层的 <see cref="OnlineClientSession"/>(会话仍归 <see cref="OnlineShellHost"/> 所有);</item>
    /// <item>帧回调里只做纯字节解码与轻量事件转发,不在回调线程里阻塞等待;</item>
    /// <item>断开口(<see cref="DisconnectAsync"/>)只是关闭会话到频道服务器的连接(网关登录保留),不发 <c>1/3</c>;真服会不会把它当异常掉线是未验证的。</item>
    /// </list>
    /// </summary>
    public sealed class OnlineTownSessionTransport : ITownCommandTransport
    {
        /// <summary>
        /// 测试接缝(internal):适配器用到的会话那几样东西(两个事件、发命令、关连接)。默认的实现(<see cref="SessionPort"/>)原样转给 <see cref="OnlineClientSession"/>;
        /// 联网层的会话类是密封的、也没有连着频道时的可观察副作用,测试里换成假实现,才能不连网络就看到发出去的包号与载荷、取消令牌、订阅数与事件的转发。
        /// </summary>
        internal interface ITownSessionPort
        {
            event Action<OnlineReceivedFrame> FrameReceived;

            event Action<OnlineDisconnectReason> Disconnected;

            Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken);

            Task DisconnectAsync(CancellationToken cancellationToken);
        }

        private sealed class SessionPort : ITownSessionPort
        {
            private readonly OnlineClientSession _real;

            public SessionPort(OnlineClientSession real)
            {
                _real = real;
            }

            public event Action<OnlineReceivedFrame> FrameReceived
            {
                add { _real.FrameReceived += value; }
                remove { _real.FrameReceived -= value; }
            }

            public event Action<OnlineDisconnectReason> Disconnected
            {
                add { _real.Disconnected += value; }
                remove { _real.Disconnected -= value; }
            }

            public Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken)
            {
                return _real.SendCommandAsync(packetId, payload, cancellationToken);
            }

            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                return _real.DisconnectAsync(cancellationToken);
            }
        }

        private readonly ITownSessionPort _session;
        private bool _disposed;

        public OnlineTownSessionTransport(OnlineClientSession session)
            : this(new SessionPort(session ?? throw new ArgumentNullException(nameof(session))))
        {
        }

        internal OnlineTownSessionTransport(ITownSessionPort session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _session.FrameReceived += OnFrameReceived;
            _session.Disconnected += OnSessionDisconnected;
        }

        public event Action<TownInboundEvent> InboundReceived;

        public event Action Disconnected;

        public async Task SendSetUserAreaAsync(
            byte targetVillage,
            byte targetArea,
            ushort x,
            ushort y,
            ushort srcVillage,
            ushort srcArea,
            byte flags,
            CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OnlineTownSessionTransport));
            }
            byte[] payload = TownAreaPayloads.BuildSetUserArea(targetVillage, targetArea, x, y, srcVillage, srcArea, flags);
            FrameStatus status = await _session.SendCommandAsync(TownAreaPayloads.SetUserAreaPacketId, payload, cancellationToken).ConfigureAwait(false);
            if (status != FrameStatus.Ok)
            {
                throw new InvalidOperationException("城镇切区命令编码失败: " + status);
            }
        }

        /// <summary>关闭会话到频道服务器的连接(网关登录保留);不发 <c>1/3</c>,真服会不会把它当异常掉线是未验证的。</summary>
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return _session.DisconnectAsync(cancellationToken);
        }

        private void OnFrameReceived(OnlineReceivedFrame frame)
        {
            if (_disposed || frame == null)
            {
                return;
            }
            byte[] payload = frame.Payload;
            if (frame.PacketClass == PacketClass.Noti)
            {
                switch (frame.PacketId)
                {
                    case TownAreaPayloads.UserAreaPacketId:
                        if (TownAreaPayloads.TryParseUserArea(payload, out UserAreaNotice userArea))
                        {
                            InboundReceived?.Invoke(new TownInboundEvent(
                                TownInboundKind.UserArea,
                                userArea.Village,
                                userArea.Area,
                                userArea.UniqueId));
                        }
                        break;
                    case TownAreaPayloads.AreaUsersPacketId:
                        if (TownAreaPayloads.TryParseAreaUsers(payload, out AreaUsersNotice areaUsers))
                        {
                            IReadOnlyList<AreaUserEntry> src = areaUsers.Entries;
                            var mapped = new TownInboundAreaUser[src.Count];
                            for (int i = 0; i < src.Count; i++)
                            {
                                mapped[i] = new TownInboundAreaUser(src[i].UniqueId, src[i].X, src[i].Y);
                            }
                            InboundReceived?.Invoke(new TownInboundEvent(
                                TownInboundKind.AreaUsers,
                                areaUsers.Village,
                                areaUsers.Area,
                                areaUsers: mapped));
                        }
                        break;
                    case SessionLifecyclePayloads.DisconnectReasonPacketId:
                        if (SessionLifecyclePayloads.TryParseDisconnectReason(payload, out DisconnectReasonNotice kicked))
                        {
                            InboundReceived?.Invoke(new TownInboundEvent(
                                TownInboundKind.Kicked,
                                reasonCode: kicked.Reason));
                        }
                        break;
                }
            }
            else if (frame.PacketClass == PacketClass.Cmd && frame.PacketId == TownAreaPayloads.SetUserAreaPacketId)
            {
                if (TownAreaPayloads.TryParseSetUserAreaError(payload, out SetUserAreaErrorNotice reply))
                {
                    InboundReceived?.Invoke(new TownInboundEvent(
                        TownInboundKind.SetUserAreaError,
                        errorCode: reply.ErrorCode,
                        remainingMinutes: reply.ForbiddenMoveRemainingMinutes ?? 0));
                }
            }
        }

        private void OnSessionDisconnected(OnlineDisconnectReason reason)
        {
            _ = reason;
            if (!_disposed)
            {
                Disconnected?.Invoke();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _session.FrameReceived -= OnFrameReceived;
            _session.Disconnected -= OnSessionDisconnected;
        }
    }
}
