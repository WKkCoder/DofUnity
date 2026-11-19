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
    /// <item>帧回调里只做纯字节解码与轻量事件转发,不在回调线程里阻塞等待。</item>
    /// </list>
    /// </summary>
    public sealed class OnlineTownSessionTransport : ITownCommandTransport
    {
        private readonly OnlineClientSession _session;
        private bool _disposed;

        public OnlineTownSessionTransport(OnlineClientSession session)
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
