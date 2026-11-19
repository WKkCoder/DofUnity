using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>从联网层收到的城镇相关下行事件种类(中性,不暴露 <c>Dnf.Net</c>)。</summary>
    public enum TownInboundKind
    {
        UserArea = 1,
        AreaUsers = 2,
        SetUserAreaError = 3,
        Kicked = 4,
    }

    /// <summary>0/24 AREA_USERS 里的一条玩家记录。</summary>
    public readonly struct TownInboundAreaUser
    {
        public TownInboundAreaUser(ushort uniqueId, ushort x, ushort y)
        {
            UniqueId = uniqueId;
            X = x;
            Y = y;
        }

        public ushort UniqueId { get; }
        public ushort X { get; }
        public ushort Y { get; }
    }

    /// <summary>城镇命令通道收到的中性事件。</summary>
    public readonly struct TownInboundEvent
    {
        public TownInboundEvent(
            TownInboundKind kind,
            byte village = 0,
            byte area = 0,
            ushort uniqueId = 0,
            IReadOnlyList<TownInboundAreaUser> areaUsers = null,
            byte errorCode = 0,
            int remainingMinutes = 0,
            int reasonCode = 0)
        {
            Kind = kind;
            Village = village;
            Area = area;
            UniqueId = uniqueId;
            AreaUsers = areaUsers ?? Array.Empty<TownInboundAreaUser>();
            ErrorCode = errorCode;
            RemainingMinutes = remainingMinutes;
            ReasonCode = reasonCode;
        }

        public TownInboundKind Kind { get; }
        public byte Village { get; }
        public byte Area { get; }
        public ushort UniqueId { get; }
        public IReadOnlyList<TownInboundAreaUser> AreaUsers { get; }
        public byte ErrorCode { get; }
        public int RemainingMinutes { get; }
        public int ReasonCode { get; }
    }

    /// <summary>Play 层城镇命令协作者对联网会话命令通道的最小抽象。</summary>
    public interface ITownCommandTransport : IDisposable
    {
        event Action<TownInboundEvent> InboundReceived;

        event Action Disconnected;

        Task SendSetUserAreaAsync(byte targetVillage, byte targetArea, ushort x, ushort y,
            ushort srcVillage, ushort srcArea, byte flags, CancellationToken cancellationToken);

        Task DisconnectAsync(CancellationToken cancellationToken);
    }

    public enum AreaChangeStatus
    {
        None = 0,
        Pending = 1,
        Confirmed = 2,
        Forbidden = 3,
        Timeout = 4,
        SendFailed = 5,
        RolledBack = 6,
        IgnoredDisabled = 7,
        Unmapped = 8,
    }

    public readonly struct AreaChangeResult
    {
        public AreaChangeResult(AreaChangeStatus status, TownPlace from, TownPlace to, byte errorCode = 0, int remainingMinutes = 0)
        {
            Status = status;
            From = from;
            To = to;
            ErrorCode = errorCode;
            RemainingMinutes = remainingMinutes;
        }

        public AreaChangeStatus Status { get; }
        public TownPlace From { get; }
        public TownPlace To { get; }
        public byte ErrorCode { get; }
        public int RemainingMinutes { get; }
    }

    /// <summary>
    /// 在线城镇命令通道协作者:
    /// <list type="bullet">
    /// <item>构造订阅、释放退订,后台回调只入队/置标志,绝不在回调线程阻塞等发送任务;</item>
    /// <item>严格单在途 <c>1/38</c>,等 <c>0/23</c>+<c>0/24</c>(Gate 只等 <c>0/24</c>)且核对自身 <c>UniqueId</c> 与目标 <c>(Village, Area)</c>;</item>
    /// <item>处理 <c>(1,38)</c> 错误(<c>0x44</c> 剩余分钟或一般拒绝)与超时静默回退,以及 <c>0/199</c> 踢下线/掉线通知。</item>
    /// </list>
    /// </summary>
    public sealed class OnlineTownCommandCoordinator : IDisposable
    {
        public const long DefaultTimeoutMs = 5000;
        public const byte DefaultAreaChangeFlags = 5;
        public const byte ForbiddenMoveErrorCode = 0x44;

        private sealed class PendingRequest
        {
            public TownPlace From;
            public TownPlace To;
            public TownAreaInfo TargetAreaInfo;
            public byte TargetVillage;
            public byte TargetArea;
            public Task SendTask;
            public CancellationTokenSource Cts;
            public long StartedMs;
            public bool GotUserArea;
            public bool GotAreaUsers;
        }

        private readonly ITownCommandTransport _transport;
        private readonly ushort _selfUniqueId;
        private readonly ConcurrentQueue<TownInboundEvent> _inbound = new ConcurrentQueue<TownInboundEvent>();
        private int _disconnectedFlag;
        private PendingRequest _inFlight;
        private bool _disposed;

        public OnlineTownCommandCoordinator(ITownCommandTransport transport, ushort selfUniqueId, bool areaSyncEnabled = true, long timeoutMs = DefaultTimeoutMs)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _selfUniqueId = selfUniqueId;
            AreaSyncEnabled = areaSyncEnabled;
            TimeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            _transport.InboundReceived += OnInboundReceived;
            _transport.Disconnected += OnTransportDisconnected;
        }

        public bool AreaSyncEnabled { get; set; }
        public long TimeoutMs { get; }
        public bool HasInFlight => _inFlight != null;
        public bool IsLost { get; private set; }
        public bool IsKicked { get; private set; }
        public int? KickReasonCode { get; private set; }
        public AreaChangeResult LastResult { get; private set; }

        /// <summary>
        /// 发起一次严格单在途的 <c>1/38</c> 切区请求。<paramref name="x"/>、<paramref name="y"/> 是主角到了目标区之后站的落点
        /// (服务端只把它们交给 <c>set_position</c>,不是源区里的位置);<paramref name="srcVillage"/>、<paramref name="srcArea"/> 才是来源。
        /// </summary>
        public bool BeginAreaChange(
            TownPlace from,
            TownPlace to,
            TownAreaInfo targetAreaInfo,
            byte targetVillage,
            byte targetArea,
            ushort x,
            ushort y,
            ushort srcVillage,
            ushort srcArea,
            long nowMs)
        {
            if (_disposed || !AreaSyncEnabled || IsLost || IsKicked || _inFlight != null)
            {
                return false;
            }
            var cts = new CancellationTokenSource();
            Task sendTask;
            try
            {
                sendTask = _transport.SendSetUserAreaAsync(targetVillage, targetArea, x, y, srcVillage, srcArea, DefaultAreaChangeFlags, cts.Token)
                    ?? Task.FromException(new InvalidOperationException("城镇发送任务为空"));
            }
            catch (Exception e)
            {
                cts.Dispose();
                _ = e;
                LastResult = new AreaChangeResult(AreaChangeStatus.SendFailed, from, to);
                return false;
            }
            _inFlight = new PendingRequest
            {
                From = from,
                To = to,
                TargetAreaInfo = targetAreaInfo,
                TargetVillage = targetVillage,
                TargetArea = targetArea,
                SendTask = sendTask,
                Cts = cts,
                StartedMs = nowMs,
            };
            LastResult = new AreaChangeResult(AreaChangeStatus.Pending, from, to);
            return true;
        }

        /// <summary>主线程每帧轮询收包、发送错误、完成条件与超时;绝不阻塞。</summary>
        public AreaChangeResult Poll(long nowMs)
        {
            if (_disposed)
            {
                return default;
            }
            if (Interlocked.Exchange(ref _disconnectedFlag, 0) != 0)
            {
                IsLost = true;
                CancelInFlight(AreaChangeStatus.SendFailed, out AreaChangeResult lostResult);
                return lostResult;
            }
            while (_inbound.TryDequeue(out TownInboundEvent ev))
            {
                if (ev.Kind == TownInboundKind.Kicked)
                {
                    IsKicked = true;
                    IsLost = true;
                    KickReasonCode = ev.ReasonCode;
                    CancelInFlight(AreaChangeStatus.SendFailed, out AreaChangeResult kickResult);
                    return kickResult;
                }
                if (_inFlight == null)
                {
                    continue;
                }
                if (ev.Kind == TownInboundKind.SetUserAreaError)
                {
                    AreaChangeStatus status = ev.ErrorCode == ForbiddenMoveErrorCode ? AreaChangeStatus.Forbidden : AreaChangeStatus.RolledBack;
                    var errResult = new AreaChangeResult(status, _inFlight.From, _inFlight.To, ev.ErrorCode, ev.RemainingMinutes);
                    CompleteInFlight(errResult);
                    return errResult;
                }
                if (ev.Kind == TownInboundKind.UserArea)
                {
                    if (ev.UniqueId == _selfUniqueId && ev.Village == _inFlight.TargetVillage && ev.Area == _inFlight.TargetArea)
                    {
                        _inFlight.GotUserArea = true;
                    }
                }
                else if (ev.Kind == TownInboundKind.AreaUsers)
                {
                    if (ev.Village == _inFlight.TargetVillage && ev.Area == _inFlight.TargetArea && ContainsSelf(ev.AreaUsers, _selfUniqueId))
                    {
                        _inFlight.GotAreaUsers = true;
                    }
                }
            }
            if (_inFlight == null)
            {
                return default;
            }
            if (_inFlight.SendTask.IsFaulted || _inFlight.SendTask.IsCanceled)
            {
                var failed = new AreaChangeResult(AreaChangeStatus.SendFailed, _inFlight.From, _inFlight.To);
                CompleteInFlight(failed);
                return failed;
            }
            if (OnlineTownAreaRules.IsAreaComplete(_inFlight.TargetAreaInfo, _inFlight.GotUserArea, _inFlight.GotAreaUsers))
            {
                var ok = new AreaChangeResult(AreaChangeStatus.Confirmed, _inFlight.From, _inFlight.To);
                CompleteInFlight(ok);
                return ok;
            }
            if (nowMs - _inFlight.StartedMs >= TimeoutMs)
            {
                var timeout = new AreaChangeResult(AreaChangeStatus.Timeout, _inFlight.From, _inFlight.To);
                CompleteInFlight(timeout);
                return timeout;
            }
            return default;
        }

        private void CancelInFlight(AreaChangeStatus status, out AreaChangeResult result)
        {
            if (_inFlight == null)
            {
                result = default;
                return;
            }
            result = new AreaChangeResult(status, _inFlight.From, _inFlight.To);
            CompleteInFlight(result);
        }

        private void CompleteInFlight(AreaChangeResult result)
        {
            PendingRequest req = _inFlight;
            _inFlight = null;
            LastResult = result;
            if (req == null)
            {
                return;
            }
            if (!req.SendTask.IsCompleted)
            {
                try
                {
                    req.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            req.Cts.Dispose();
        }

        private static bool ContainsSelf(IReadOnlyList<TownInboundAreaUser> users, ushort expectedUid)
        {
            if (users == null)
            {
                return false;
            }
            for (int i = 0; i < users.Count; i++)
            {
                if (users[i].UniqueId == expectedUid)
                {
                    return true;
                }
            }
            return false;
        }

        private void OnInboundReceived(TownInboundEvent ev)
        {
            if (!_disposed)
            {
                _inbound.Enqueue(ev);
            }
        }

        private void OnTransportDisconnected()
        {
            Interlocked.Exchange(ref _disconnectedFlag, 1);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _transport.InboundReceived -= OnInboundReceived;
            _transport.Disconnected -= OnTransportDisconnected;
            CompleteInFlight(default);
            _transport.Dispose();
        }
    }
}
