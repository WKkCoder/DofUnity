using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Client
{
    /// <summary>
    /// 联网结算菜单控制器对在线会话命令通道与非心跳帧观察者的最小抽象(r18-settleui):
    /// 生产路径经 <see cref="RealOnlineSettlementTransport"/> 转给已完成 <c>LeaveExclusiveReceiveAsync</c> 的
    /// <see cref="OnlineClientSession"/>(r18-sessionext N2);测试路径可用假传输直接注入回包与断线。
    /// </summary>
    public interface IOnlineSettlementTransport : IDisposable
    {
        bool IsDisconnected { get; }

        event Action<PacketClass, int, byte[]> FrameReceived;

        event Action<OnlineDisconnectReason> Disconnected;

        Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 把 <see cref="OnlineClientSession"/> 包装成 <see cref="IOnlineSettlementTransport"/>:
    /// 成对订阅/退订 <c>FrameReceived</c> 与 <c>Disconnected</c>,绝不释放底层会话(会话归 <see cref="OnlineShellHost"/> 所有)。
    /// </summary>
    public sealed class RealOnlineSettlementTransport : IOnlineSettlementTransport
    {
        private readonly OnlineClientSession _session;
        private readonly object _gate = new object();
        private readonly Dictionary<Action<PacketClass, int, byte[]>, Action<OnlineReceivedFrame>> _bridges =
            new Dictionary<Action<PacketClass, int, byte[]>, Action<OnlineReceivedFrame>>();
        private readonly List<Action<OnlineDisconnectReason>> _disconnectHandlers =
            new List<Action<OnlineDisconnectReason>>();
        private bool _disposed;

        public RealOnlineSettlementTransport(OnlineClientSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public bool IsDisconnected
        {
            get
            {
                if (_session.State == OnlineClientSessionState.Disposed)
                {
                    return true;
                }
                GameClientSession game = _session.GameSession;
                return game == null || game.PeerClosed || game.LastFrameError != FrameError.None;
            }
        }

        public event Action<PacketClass, int, byte[]> FrameReceived
        {
            add
            {
                if (value == null || _disposed)
                {
                    return;
                }
                lock (_gate)
                {
                    if (_disposed || _bridges.ContainsKey(value))
                    {
                        return;
                    }
                    Action<OnlineReceivedFrame> wrapped = frame =>
                    {
                        if (frame != null && !_disposed)
                        {
                            value(frame.PacketClass, frame.PacketId, frame.Payload);
                        }
                    };
                    _bridges[value] = wrapped;
                    _session.FrameReceived += wrapped;
                }
            }
            remove
            {
                if (value == null)
                {
                    return;
                }
                lock (_gate)
                {
                    if (_bridges.TryGetValue(value, out Action<OnlineReceivedFrame> wrapped))
                    {
                        _session.FrameReceived -= wrapped;
                        _bridges.Remove(value);
                    }
                }
            }
        }

        public event Action<OnlineDisconnectReason> Disconnected
        {
            add
            {
                if (value == null || _disposed)
                {
                    return;
                }
                lock (_gate)
                {
                    if (_disposed || _disconnectHandlers.Contains(value))
                    {
                        return;
                    }
                    _disconnectHandlers.Add(value);
                    _session.Disconnected += value;
                }
            }
            remove
            {
                if (value == null)
                {
                    return;
                }
                lock (_gate)
                {
                    if (_disconnectHandlers.Remove(value))
                    {
                        _session.Disconnected -= value;
                    }
                }
            }
        }

        public Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RealOnlineSettlementTransport));
            }
            return _session.SendCommandAsync(packetId, payload ?? Array.Empty<byte>(), cancellationToken);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                foreach (Action<OnlineReceivedFrame> wrapped in _bridges.Values)
                {
                    _session.FrameReceived -= wrapped;
                }
                _bridges.Clear();
                for (int i = 0; i < _disconnectHandlers.Count; i++)
                {
                    _session.Disconnected -= _disconnectHandlers[i];
                }
                _disconnectHandlers.Clear();
            }
        }
    }
}
