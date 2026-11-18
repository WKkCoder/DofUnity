using System;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>
    /// 客户端宿主里“在线进城”的总编排者(连接 <see cref="OnlineShellHost"/>、<see cref="OnlineTownCommandCoordinator"/> 与 <see cref="OnlineTownWorld"/>):
    /// <list type="bullet">
    /// <item>从选中角色(<see cref="OnlineCharacterEntry"/>)、<c>SelectCharacterLoadInfo</c> 与 <c>TownEntrySnapshot</c> 建立 <see cref="OnlineHeroProfile"/> 与起始城镇;</item>
    /// <item>把 <see cref="ITownWorld"/> 包成 <see cref="OnlineTownWorld"/> 处理 <c>1/38</c> 切区、回退与可配置开关;</item>
    /// <item>监听 <c>0/199</c> 踢下线与断线事件,退回登录页而不死循环自动重试;</item>
    /// <item>处理系统菜单"回选角"(重连当前频道拉新名单)与"退出游戏"(先退订,再由外壳关闭到频道的连接后退出;不发 <c>1/3</c>,真服怎么看待这种收尾是未验证的),确保所有事件退订成对。</item>
    /// </list>
    /// </summary>
    public sealed class OnlineTownController : IDisposable
    {
        private sealed class NullTownTransport : ITownCommandTransport
        {
            public event Action<TownInboundEvent> InboundReceived { add { } remove { } }
            public event Action Disconnected { add { } remove { } }

            public Task SendSetUserAreaAsync(byte targetVillage, byte targetArea, ushort x, ushort y,
                ushort srcVillage, ushort srcArea, byte flags, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public void Dispose() { }
        }

        private readonly OnlineShellHost _host;
        private readonly TownAtlas _atlas;
        private readonly OnlineTownCommandCoordinator _coordinator;
        private readonly Action<string> _log;
        private bool _exiting;
        private bool _disconnectLogged;
        private bool _disposed;

        private OnlineTownController(
            OnlineShellHost host,
            TownAtlas atlas,
            OnlineHeroProfile profile,
            int startTown,
            OnlineTownCommandCoordinator coordinator,
            Action<string> log)
        {
            _host = host;
            _atlas = atlas;
            Profile = profile;
            StartTown = startTown;
            _coordinator = coordinator;
            _log = log;
        }

        public OnlineHeroProfile Profile { get; }
        public int StartTown { get; }
        public OnlineTownCommandCoordinator Coordinator => _coordinator;
        public OnlineTownWorld World { get; private set; }
        public bool IsAreaChangePending => World != null && World.IsAreaChangePending;
        public OnlineTownPrompt CurrentPrompt => World?.CurrentPrompt;
        public bool DisconnectLogged => _disconnectLogged;

        /// <summary>按当前处于 <c>InGame</c> 的宿主创建在线城镇控制器并写一条脱敏进城日志。</summary>
        public static OnlineTownController Create(
            OnlineShellHost host,
            TownAtlas atlas,
            ICharacterTitles titles = null,
            bool areaSyncEnabled = true,
            Action<string> log = null,
            ITownCommandTransport customTransport = null,
            long commandTimeoutMs = OnlineTownCommandCoordinator.DefaultTimeoutMs)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }
            if (atlas == null)
            {
                throw new ArgumentNullException(nameof(atlas));
            }
            OnlineCharacterEntry entry = host.Flow.SelectedCharacter
                ?? throw new InvalidOperationException("当前没有已选中的联网角色");
            int selectedSlot = host.Flow.SelectedSlot;
            SelectCharacterLoadInfo load = host.SelectCharacter?.Load;
            TownEntrySnapshot snap = host.Snapshot;
            DungeonPermissionSnapshot perms = host.DungeonPermissions;

            OnlineTownSnapshotCheck snapCheck = OnlineHeroProfile.CompareSnapshot(
                entry,
                selectedSlot,
                snap != null,
                snap?.Slot,
                snap?.Level,
                snap?.Job,
                snap?.GrowType);

            bool hasRealTransport = customTransport != null || host.Session != null;
            OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(
                entry,
                selectedSlot,
                titles,
                load != null ? (ushort?)load.UniqueId : null,
                load != null ? (int?)load.Village : null,
                snapCheck,
                perms?.Entries != null ? (int?)perms.Entries.Count : null,
                isSyncedSession: hasRealTransport);

            int startTown = OnlineTownAreaRules.ResolveStartTown(atlas, profile.ServerVillage, out bool townFallback);
            ITownCommandTransport transport = customTransport
                ?? (host.Session != null ? (ITownCommandTransport)new OnlineTownSessionTransport(host.Session) : new NullTownTransport());

            bool effectiveAreaSync = areaSyncEnabled && hasRealTransport;
            var coordinator = new OnlineTownCommandCoordinator(transport, profile.ServerUniqueId, effectiveAreaSync, commandTimeoutMs);

            log?.Invoke(OnlineTownLog.Enter(profile, townFallback, coordinator.AreaSyncEnabled));
            return new OnlineTownController(host, atlas, profile, startTown, coordinator, log);
        }

        /// <summary>包住离线建造好的 <see cref="ITownWorld"/>,保持底层 <see cref="TownSession"/> 不被二次包装。</summary>
        public ITownWorld WrapWorld(ITownWorld inner)
        {
            World = new OnlineTownWorld(inner, _atlas, _coordinator, _log);
            return World;
        }

        /// <summary>每帧移动前记录主角安全位置。</summary>
        public void RecordBeforeStep()
        {
            World?.RecordSafePosition();
        }

        /// <summary>每帧在流程 <c>Poll</c> 前推进在线切区状态机并处理 <c>0/199</c> 踢下线/掉线通知。</summary>
        public void Poll(long nowMs)
        {
            if (_disposed)
            {
                return;
            }
            if (World != null)
            {
                World.Poll(nowMs);
            }
            else
            {
                _coordinator.Poll(nowMs);
            }
            if ((_coordinator.IsKicked || _coordinator.IsLost) && !_exiting && !_disconnectLogged)
            {
                _disconnectLogged = true;
                _log?.Invoke(_coordinator.IsKicked
                    ? OnlineTownLog.Disconnect("kicked", _coordinator.KickReasonCode)
                    : OnlineTownLog.Disconnect("connection_lost"));
                _host.Flow.NotifyDisconnected();
            }
        }

        /// <summary>当外壳抢先一步切回登录页(例如 TCP 先断触发了 <c>OnlineShellHost.Tick</c>),清收并写断线日志。</summary>
        public void FlushDisconnectBeforeDispose(long nowMs)
        {
            if (_disposed || _exiting || _disconnectLogged)
            {
                return;
            }
            _coordinator.Poll(nowMs);
            _disconnectLogged = true;
            _log?.Invoke(_coordinator.IsKicked
                ? OnlineTownLog.Disconnect("kicked", _coordinator.KickReasonCode)
                : OnlineTownLog.Disconnect("connection_lost"));
        }

        /// <summary>系统菜单选择了"回选角":立即退订城镇通道,让外壳重连当前频道并取回新角色表。</summary>
        public void ReturnToCharacterSelect()
        {
            if (_disposed || _exiting)
            {
                return;
            }
            _exiting = true;
            _log?.Invoke(OnlineTownLog.Exit("charselect", "requested"));
            Dispose();
            _host.ReturnToCharacterSelect();
        }

        /// <summary>
        /// 系统菜单选择了"退出游戏":先退订城镇通道,再由外壳关闭到频道的连接(<see cref="OnlineShellHost.DisconnectForQuitAsync"/> 只是调
        /// <see cref="OnlineClientSession.DisconnectAsync"/>,关掉这一条 TCP、网关登录保留)。<b>不发 <c>1/3</c></b>(联网层也没有 <c>1/3</c> 的构造器),
        /// 真服会不会把这样的收尾当异常掉线是未验证的。只执行一次;回选角之后再调是空操作;退出过程抛异常时写 <c>exit quit error</c> 日志,不外抛。
        /// </summary>
        public async Task QuitGameAsync(CancellationToken cancellationToken = default)
        {
            if (_exiting)
            {
                return;
            }
            _exiting = true;
            _log?.Invoke(OnlineTownLog.Exit("quit", "requested"));
            Dispose();
            try
            {
                await _host.DisconnectForQuitAsync(cancellationToken).ConfigureAwait(false);
                _log?.Invoke(OnlineTownLog.Exit("quit", "done"));
            }
            catch (Exception)
            {
                _log?.Invoke(OnlineTownLog.Exit("quit", "error"));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _coordinator.Dispose();
        }
    }
}
