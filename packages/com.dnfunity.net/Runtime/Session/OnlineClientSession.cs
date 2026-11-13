using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Gateway;

namespace Dnf.Net.Session
{
    /// <summary>分阶段联网会话所处的状态；状态迁移由公开方法串行完成。</summary>
    public enum OnlineClientSessionState
    {
        Created = 0,
        LoggedIn,
        Connected,
        CharacterSelected,
        ExclusiveReceive,
        Disposed,
    }

    /// <summary>心跳泵的显式状态。进副本前切到 <see cref="ExclusiveReceive"/>，不再由本对象收包。</summary>
    public enum OnlineHeartbeatState
    {
        Disabled = 0,
        Running,
        ExclusiveReceive,
    }

    /// <summary>非主动关闭时断线的原因。</summary>
    public enum OnlineDisconnectReason
    {
        PeerClosed = 0,
        ReadError,
        WriteError,
        HeartbeatTimeout,
    }

    /// <summary>角色列表的到达与解析状态；收到一个空列表也是 <see cref="Received"/>。</summary>
    public enum OnlineCharacterListStatus
    {
        NotReceived = 0,
        Received,
        Malformed,
    }

    /// <summary>把事件或进度回调切到宿主需要的线程；未提供时由会话用串行后台回调。</summary>
    public interface IOnlineDispatcher
    {
        void Dispatch(Action callback);
    }

    /// <summary>网关登录与取游戏服地址的结果；令牌和地址只留在会话内部。</summary>
    public sealed class OnlineLoginResult
    {
        internal OnlineLoginResult(OnlineStage stage, OnlineFailure failure)
        {
            Stage = stage;
            Failure = failure;
        }

        public OnlineStage Stage { get; }
        public OnlineFailure Failure { get; }
        public bool IsSuccess => Failure == OnlineFailure.None;
    }

    /// <summary>连频道、收启动包与游戏登录的结果。</summary>
    public sealed class OnlineConnectResult
    {
        internal OnlineConnectResult(OnlineStage stage, OnlineFailure failure, byte? loginFailureCode,
            ChannelInfo channel, UserInfoNotice characterList, OnlineCharacterListStatus characterListStatus,
            OnlineEntryResult entryResult)
        {
            Stage = stage;
            Failure = failure;
            LoginFailureCode = loginFailureCode;
            Channel = channel;
            CharacterList = characterList;
            CharacterListStatus = characterListStatus;
            EntryResult = entryResult;
        }

        public OnlineStage Stage { get; }
        public OnlineFailure Failure { get; }
        public bool IsSuccess => Failure == OnlineFailure.None;
        public byte? LoginFailureCode { get; }
        public ChannelInfo Channel { get; }
        public UserInfoNotice CharacterList { get; }
        public OnlineCharacterListStatus CharacterListStatus { get; }
        public bool CharacterListReceived => CharacterListStatus == OnlineCharacterListStatus.Received;
        internal OnlineEntryResult EntryResult { get; }
    }

    /// <summary>单个槽位的选角结果。失败后可否继续试下一槽由 <see cref="MayTryAnotherSlot"/> 表示。</summary>
    public sealed class OnlineSelectCharacterResult
    {
        internal OnlineSelectCharacterResult(int requestedSlot, bool mayTryAnotherSlot, OnlineEntryResult entryResult)
        {
            RequestedSlot = requestedSlot;
            MayTryAnotherSlot = mayTryAnotherSlot;
            EntryResult = entryResult;
        }

        public int RequestedSlot { get; }
        public int Slot => EntryResult.Slot;
        public OnlineStage Stage => EntryResult.Stage;
        public OnlineFailure Failure => EntryResult.Failure;
        public bool Entered => EntryResult.Entered;
        public bool MayTryAnotherSlot { get; }
        public SelectCharacterResult SelectCharacter => EntryResult.SelectCharacter;
        public TownEntrySnapshot Snapshot => EntryResult.Snapshot;
        public DungeonPermissionSnapshot DungeonPermissions => EntryResult.DungeonPermissions;
        internal OnlineEntryResult EntryResult { get; }
    }

    /// <summary>一次选角调用的内部结局；发送失败与可继续试的槽位失败分开。</summary>
    internal sealed class OnlineTownSelection
    {
        internal OnlineTownSelection(OnlineEntryResult result, bool canRetry)
        {
            Result = result;
            CanRetry = canRetry;
        }

        internal OnlineEntryResult Result { get; }
        internal bool CanRetry { get; }
    }

    /// <summary>测试替换网关与频道连接用的接缝；生产路径全部为 null 或默认值。</summary>
    internal sealed class OnlineClientSessionHooks
    {
        internal Func<GatewayOptions, GatewayClient> GatewayFactory { get; set; }
        internal Func<IPAddress, int, CancellationToken, Task<OnlineChannelLink>> ChannelFactory { get; set; }
        internal OnlineTownWindows Windows { get; set; }
        internal bool AutoHeartbeat { get; set; } = true;
        internal bool InlineCallbacks { get; set; }
        internal bool ValidateSlot { get; set; } = true;
        internal TimeSpan HeartbeatPoll { get; set; } = TimeSpan.FromSeconds(5);
        internal TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(11);
    }

    /// <summary>一条频道连接及其归属；观察流只记录错误类别，不保留数据或异常消息。</summary>
    internal sealed class OnlineChannelLink : IDisposable
    {
        internal OnlineChannelLink(GameClientSession session, IDisposable connection, uint localAddress,
            OnlineObservedStream observer = null)
        {
            Session = session;
            Connection = connection;
            LocalAddress = localAddress;
            Observer = observer;
        }

        internal GameClientSession Session { get; }
        internal IDisposable Connection { get; }
        internal uint LocalAddress { get; }
        internal OnlineObservedStream Observer { get; }
        internal bool ReadFault { get; set; }
        internal bool WriteFault { get; set; }
        internal bool DisconnectNotified { get; set; }
        internal bool ReadFailed => ReadFault || (Observer != null && Observer.ReadFailed);
        internal bool WriteFailed => WriteFault || (Observer != null && Observer.WriteFailed);

        public void Dispose()
        {
            Connection?.Dispose();
        }
    }

    /// <summary>在 GameClientSession 吞掉流异常之前记录读/写错误类别。</summary>
    internal sealed class OnlineObservedStream : Stream
    {
        private readonly Stream _inner;

        internal OnlineObservedStream(Stream inner)
        {
            _inner = inner;
        }

        internal bool ReadFailed { get; private set; }
        internal bool WriteFailed { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                ReadFailed = true;
                throw;
            }
            catch (ObjectDisposedException)
            {
                ReadFailed = true;
                throw;
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                WriteFailed = true;
                throw;
            }
            catch (ObjectDisposedException)
            {
                WriteFailed = true;
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    /// <summary>默认的串行后台派发器；同一会话的回调按投递顺序执行，不依赖 UnityEngine。</summary>
    internal sealed class OnlineBackgroundDispatcher : IOnlineDispatcher
    {
        internal static readonly OnlineBackgroundDispatcher Instance = new OnlineBackgroundDispatcher();
        private readonly object _gate = new object();
        private Task _tail = Task.CompletedTask;

        public void Dispatch(Action callback)
        {
            if (callback == null) return;
            lock (_gate)
            {
                _tail = _tail.ContinueWith(_ => callback(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// 可分阶段驱动的联网客户端：网关登录 → 连频道并列出角色 → 指定槽位选角。
    /// 方法通过内部门闩串行化；顺序错误抛 <see cref="InvalidOperationException"/>，取消抛 <see cref="OperationCanceledException"/>。
    /// 成功选角后默认自管心跳；进副本前调用 <see cref="EnterExclusiveReceiveAsync"/> 把收包权交给战斗路径。
    /// </summary>
    public sealed class OnlineClientSession : IDisposable
    {
        private readonly OnlineEntrySettings _settings;
        private readonly OnlineTownEntry _entry;
        private readonly Action<OnlineStage> _onStage;
        private readonly IOnlineDispatcher _dispatcher;
        private readonly OnlineClientSessionHooks _hooks;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _disposeStop = new CancellationTokenSource();
        private string _token;
        private IPAddress _address;
        private OnlineChannelLink _link;
        private List<DecodedFrame> _earlyFrames = new List<DecodedFrame>();
        private bool _selectionStageReported;
        private CancellationTokenSource _heartbeatStop;
        private Task _heartbeatTask;
        private bool _detached;

        /// <summary>创建分阶段会话。事件与进度默认串行投递到后台线程；宿主可给 dispatcher 切回主线程。</summary>
        public OnlineClientSession(OnlineEntrySettings settings, Action<OnlineStage> onStage = null, IOnlineDispatcher dispatcher = null)
            : this(settings, onStage, dispatcher, null)
        {
        }

        internal OnlineClientSession(OnlineEntrySettings settings, Action<OnlineStage> onStage,
            IOnlineDispatcher dispatcher, OnlineClientSessionHooks hooks)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _settings = new OnlineEntrySettings
            {
                Gateway = settings.Gateway,
                Account = settings.Account,
                Password = settings.Password,
                MouseCipher = settings.MouseCipher,
                DataVersion = settings.DataVersion,
                ChannelPort = settings.ChannelPort,
                MaxSlots = settings.MaxSlots,
            };
            _entry = new OnlineTownEntry(_settings);
            _onStage = onStage;
            _hooks = hooks ?? new OnlineClientSessionHooks();
            _dispatcher = _hooks.InlineCallbacks ? null : (dispatcher ?? OnlineBackgroundDispatcher.Instance);
        }

        public event Action<OnlineDisconnectReason> Disconnected;
        public OnlineClientSessionState State { get; private set; } = OnlineClientSessionState.Created;
        public OnlineHeartbeatState HeartbeatState { get; private set; } = OnlineHeartbeatState.Disabled;
        public GameClientSession GameSession => _link?.Session;

        /// <summary>网关登录并取游戏服地址；重复登录属于顺序错误。</summary>
        public async Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State != OnlineClientSessionState.Created)
                {
                    throw new InvalidOperationException("网关登录已经完成");
                }
                ReportStage(OnlineStage.GatewayLogin);
                using (GatewayClient gateway = _hooks.GatewayFactory != null
                    ? _hooks.GatewayFactory(_settings.Gateway)
                    : _entry.CreateGateway())
                {
                    GatewayResult login = await gateway.LoginAsync(account, password, cancellationToken).ConfigureAwait(false);
                    if (!login.IsSuccess || login.Token == null)
                    {
                        return new OnlineLoginResult(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed);
                    }
                    ReportStage(OnlineStage.GameServerAddress);
                    GatewayResult ip = await gateway.GetGameServerIpAsync(cancellationToken).ConfigureAwait(false);
                    if (!ip.IsSuccess || ip.Text == null || !IPAddress.TryParse(ip.Text.Trim(), out IPAddress address))
                    {
                        return new OnlineLoginResult(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress);
                    }
                    _settings.Account = account;
                    _settings.Password = password;
                    _token = login.Token;
                    _address = address;
                    State = OnlineClientSessionState.LoggedIn;
                    return new OnlineLoginResult(OnlineStage.GameServerAddress, OnlineFailure.None);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>连指定频道、收启动包并游戏登录；角色列表未到达时状态为 NotReceived，不伪装成空列表。</summary>
        public async Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State != OnlineClientSessionState.LoggedIn)
                {
                    throw new InvalidOperationException("当前状态不能连接频道");
                }
                await CloseLinkNoGateAsync().ConfigureAwait(false);
                return await ConnectNoGateAsync(channelPort, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>选一个槽位。失败且 MayTryAnotherSlot 为 true 时可继续试；成功后不能再次选角。</summary>
        public async Task<OnlineSelectCharacterResult> SelectCharacterAsync(int slot, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State != OnlineClientSessionState.Connected)
                {
                    throw new InvalidOperationException("当前状态不能选角");
                }
                if (_hooks.ValidateSlot && (slot < 0 || slot > byte.MaxValue))
                {
                    throw new ArgumentOutOfRangeException(nameof(slot));
                }
                try
                {
                    return await SelectNoGateAsync(slot, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ReceiveAsync 在取消时可能已经解出但未交付旧槽回包；这个连接不能再给另一槽复用。
                    State = OnlineClientSessionState.LoggedIn;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>断开旧频道，用同一令牌连新频道并重新游戏登录；该换频道语义尚未经真服验证。</summary>
        public async Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State == OnlineClientSessionState.ExclusiveReceive)
                {
                    throw new InvalidOperationException("独占收包期间不能换频道；先由战斗路径结束并显式断开");
                }
                if (_token == null || _address == null || State == OnlineClientSessionState.Created)
                {
                    throw new InvalidOperationException("尚未登录，不能换频道");
                }
                await CloseLinkNoGateAsync().ConfigureAwait(false);
                State = OnlineClientSessionState.LoggedIn;
                return await ConnectNoGateAsync(channelPort, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>关闭当前频道连接；保留网关登录结果，之后可重新 ConnectAsync。释放后调用为空操作。</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (State == OnlineClientSessionState.Disposed) return;
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == OnlineClientSessionState.Disposed) return;
                await CloseLinkNoGateAsync().ConfigureAwait(false);
                State = _token == null ? OnlineClientSessionState.Created : OnlineClientSessionState.LoggedIn;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>开关进城后的心跳泵。开关是幂等的；独占收包状态下不能重新开启。</summary>
        public async Task SetHeartbeatAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (enabled && HeartbeatState == OnlineHeartbeatState.ExclusiveReceive)
                {
                    throw new InvalidOperationException("独占收包期间不能开启心跳");
                }
                if (enabled && State != OnlineClientSessionState.CharacterSelected)
                {
                    throw new InvalidOperationException("选角成功前没有心跳泵可开启");
                }
                if (enabled)
                {
                    StartHeartbeatNoGate();
                }
                else if (HeartbeatState != OnlineHeartbeatState.ExclusiveReceive)
                {
                    await StopHeartbeatNoGateAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>停止心跳并把会话收包权永久交给进副本等独占路径。</summary>
        public async Task EnterExclusiveReceiveAsync(CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State != OnlineClientSessionState.CharacterSelected)
                {
                    throw new InvalidOperationException("只有选角成功后才能进入独占收包");
                }
                HeartbeatState = OnlineHeartbeatState.ExclusiveReceive;
                State = OnlineClientSessionState.ExclusiveReceive;
                await StopHeartbeatNoGateAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>立即取消心跳并关闭连接；可重复调用，不进行网络重试。</summary>
        public void Dispose()
        {
            if (State == OnlineClientSessionState.Disposed) return;
            _disposeStop.Cancel();
            _gate.Wait();
            try
            {
                if (State == OnlineClientSessionState.Disposed) return;
                State = OnlineClientSessionState.Disposed;
                CancellationTokenSource stop = _heartbeatStop;
                Task heartbeat = _heartbeatTask;
                _heartbeatStop = null;
                _heartbeatTask = null;
                stop?.Cancel();
                _link?.Dispose();
                _link = null;
                if (stop != null)
                {
                    (heartbeat ?? Task.CompletedTask).ContinueWith(_ => stop.Dispose(),
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        internal void EnsureSelectionStage()
        {
            if (_selectionStageReported) return;
            _selectionStageReported = true;
            ReportStage(OnlineStage.SelectSlot);
        }

        internal OnlineEntryResult Detach(OnlineEntryResult outcome)
        {
            OnlineChannelLink link = _link;
            OnlineEntryResult result = new OnlineEntryResult(outcome.Stage, outcome.Failure, outcome.LoginFailureCode,
                outcome.Slot, outcome.Entered ? link?.Session : null, link?.Connection, outcome.FramesSeen,
                outcome.SecondLayerMismatches, outcome.Snapshot, outcome.DungeonPermissions, outcome.SelectCharacter);
            _detached = true;
            State = OnlineClientSessionState.Disposed;
            _heartbeatStop?.Cancel();
            _link = null;
            return result;
        }

        internal OnlineEntryResult NoUsableSlot(SelectCharacterResult lastFailure)
        {
            return OnlineTownEntry.Fail(OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, null, -1,
                _link?.Session.Events.Count ?? 0, null, null, _link?.Session.SecondLayerMismatches ?? 0, lastFailure);
        }

        private async Task<OnlineConnectResult> ConnectNoGateAsync(int channelPort, CancellationToken cancellationToken)
        {
            _earlyFrames = new List<DecodedFrame>();
            _selectionStageReported = false;
            ReportStage(OnlineStage.ChannelConnect);
            OnlineChannelLink link = _hooks.ChannelFactory != null
                ? await _hooks.ChannelFactory(_address, channelPort, cancellationToken).ConfigureAwait(false)
                : await OpenChannelAsync(_address, channelPort, cancellationToken).ConfigureAwait(false);
            if (link == null)
            {
                return ConnectFailure(OnlineTownEntry.Fail(OnlineStage.ChannelConnect, OnlineFailure.ConnectFailed,
                    null, -1, 0, null, null, 0));
            }
            _link = link;
            ReportStage(OnlineStage.Bootstrap);
            bool boot = await link.Session.ReceiveBootstrapAsync(OnlineTownEntry.BootstrapTimeout, cancellationToken).ConfigureAwait(false);
            if (!boot)
            {
                NotifyIfDisconnected(link);
                OnlineFailure failure = link.Session.Rejected ? OnlineFailure.Rejected : OnlineFailure.BootstrapFailed;
                return ConnectFailure(OnlineTownEntry.Fail(OnlineStage.Bootstrap, failure, null, -1, 0, null, null, 0));
            }

            ReportStage(OnlineStage.GameLogin);
            OnlineEntryResult loginFailure = await _entry.TryGameLogin(link.Session, _token, link.LocalAddress,
                link.Connection, _hooks.Windows ?? OnlineTownWindows.Default, _earlyFrames, cancellationToken).ConfigureAwait(false);
            if (loginFailure != null)
            {
                NotifyIfDisconnected(link);
                return ConnectFailure(loginFailure);
            }
            NotifyIfDisconnected(link);
            State = OnlineClientSessionState.Connected;
            ParseCharacterList(out UserInfoNotice list, out OnlineCharacterListStatus listStatus);
            return new OnlineConnectResult(OnlineStage.GameLogin, OnlineFailure.None, null,
                link.Session.Channel, list, listStatus, null);
        }

        private OnlineConnectResult ConnectFailure(OnlineEntryResult result)
        {
            return new OnlineConnectResult(result.Stage, result.Failure, result.LoginFailureCode,
                null, null, OnlineCharacterListStatus.NotReceived, result);
        }

        private async Task<OnlineSelectCharacterResult> SelectNoGateAsync(int slot, CancellationToken cancellationToken)
        {
            EnsureSelectionStage();
            OnlineChannelLink link = _link;
            OnlineTownSelection selection = await _entry.SelectOnSessionAsync(link.Session, slot, _earlyFrames,
                link.Connection, _hooks.Windows ?? OnlineTownWindows.Default, cancellationToken).ConfigureAwait(false);
            NotifyIfDisconnected(link);
            if (selection.Result.Entered)
            {
                State = OnlineClientSessionState.CharacterSelected;
                if (_hooks.AutoHeartbeat)
                {
                    StartHeartbeatNoGate();
                }
            }
            return new OnlineSelectCharacterResult(slot, selection.CanRetry, selection.Result);
        }

        private async Task<OnlineChannelLink> OpenChannelAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            TcpClient tcp = new TcpClient();
            Task connectTask = tcp.ConnectAsync(address, port);
            Task timeoutTask = Task.Delay(OnlineTownEntry.ConnectTimeout, cancellationToken);
            if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) != connectTask || !tcp.Connected)
            {
                tcp.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                return null;
            }
            var observer = new OnlineObservedStream(tcp.GetStream());
            return new OnlineChannelLink(new GameClientSession(observer), tcp, OnlineTownEntry.LocalAddressOf(tcp), observer);
        }

        private void ParseCharacterList(out UserInfoNotice list, out OnlineCharacterListStatus status)
        {
            list = null;
            status = OnlineCharacterListStatus.NotReceived;
            foreach (DecodedFrame frame in _earlyFrames)
            {
                if (frame.Header.Class != PacketClass.Noti || frame.Header.PacketId != UserInfoParser.PacketId)
                {
                    continue;
                }
                if (UserInfoParser.TryParse(frame.Payload, out UserInfoNotice parsed, out UserInfoParseStatus parseStatus))
                {
                    list = parsed;
                    status = OnlineCharacterListStatus.Received;
                    return;
                }
                if (parseStatus == UserInfoParseStatus.Malformed)
                {
                    status = OnlineCharacterListStatus.Malformed;
                }
            }
        }

        private void StartHeartbeatNoGate()
        {
            if (HeartbeatState == OnlineHeartbeatState.Running || HeartbeatState == OnlineHeartbeatState.ExclusiveReceive) return;
            OnlineChannelLink link = _link;
            if (link == null) throw new InvalidOperationException("没有可保活的频道连接");
            var stop = new CancellationTokenSource();
            _heartbeatStop = stop;
            HeartbeatState = OnlineHeartbeatState.Running;
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(link, stop.Token));
        }

        private async Task StopHeartbeatNoGateAsync()
        {
            CancellationTokenSource stop = _heartbeatStop;
            Task task = _heartbeatTask;
            _heartbeatStop = null;
            _heartbeatTask = null;
            stop?.Cancel();
            if (task != null)
            {
                await task.ConfigureAwait(false);
            }
            stop?.Dispose();
            if (HeartbeatState != OnlineHeartbeatState.ExclusiveReceive)
            {
                HeartbeatState = OnlineHeartbeatState.Disabled;
            }
        }

        private async Task HeartbeatLoopAsync(OnlineChannelLink link, CancellationToken cancellationToken)
        {
            Stopwatch idle = Stopwatch.StartNew();
            try
            {
                while (!link.Session.PeerClosed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<DecodedFrame> window = await link.Session.ReceiveAsync(_hooks.HeartbeatPoll, 0, cancellationToken).ConfigureAwait(false);
                    if (window.Count > 0)
                    {
                        idle.Restart();
                    }
                    foreach (DecodedFrame frame in window)
                    {
                        if (!OnlineHeartbeat.IsHeartbeat(frame)) continue;
                        FrameStatus sent = await link.Session.SendAsync(PacketClass.Cmd, OnlineHeartbeat.ReplyPacketId,
                            OnlineHeartbeat.ReplyPayload, cancellationToken).ConfigureAwait(false);
                        if (sent != FrameStatus.Ok)
                        {
                            NotifyDisconnected(link, OnlineDisconnectReason.WriteError);
                            return;
                        }
                    }
                    if (link.ReadFailed || link.Session.LastFrameError != FrameError.None)
                    {
                        NotifyDisconnected(link, OnlineDisconnectReason.ReadError);
                        return;
                    }
                    if (link.Session.PeerClosed)
                    {
                        NotifyDisconnected(link, OnlineDisconnectReason.PeerClosed);
                        return;
                    }
                    if (idle.Elapsed >= _hooks.HeartbeatTimeout)
                    {
                        NotifyDisconnected(link, OnlineDisconnectReason.HeartbeatTimeout);
                        return;
                    }
                }
                NotifyDisconnected(link, OnlineDisconnectReason.PeerClosed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                NotifyDisconnected(link, link.WriteFailed ? OnlineDisconnectReason.WriteError : OnlineDisconnectReason.ReadError);
            }
            catch (ObjectDisposedException)
            {
                NotifyDisconnected(link, link.WriteFailed ? OnlineDisconnectReason.WriteError : OnlineDisconnectReason.ReadError);
            }
            finally
            {
                if (ReferenceEquals(_link, link) && HeartbeatState == OnlineHeartbeatState.Running)
                {
                    HeartbeatState = OnlineHeartbeatState.Disabled;
                }
            }
        }

        private void NotifyIfDisconnected(OnlineChannelLink link)
        {
            if (link.ReadFailed || link.Session.LastFrameError != FrameError.None)
            {
                NotifyDisconnected(link, OnlineDisconnectReason.ReadError);
            }
            else if (link.Session.PeerClosed)
            {
                NotifyDisconnected(link, OnlineDisconnectReason.PeerClosed);
            }
        }

        private void NotifyDisconnected(OnlineChannelLink link, OnlineDisconnectReason reason)
        {
            if (_detached || State == OnlineClientSessionState.Disposed || !ReferenceEquals(_link, link) || link.DisconnectNotified)
            {
                return;
            }
            link.DisconnectNotified = true;
            Action<OnlineDisconnectReason> handler = Disconnected;
            if (handler != null)
            {
                // 即使测试把进度回调设为内联，事件也不持门闩同步回调，允许处理者调用 Disconnect/Dispose。
                IOnlineDispatcher events = _dispatcher ?? OnlineBackgroundDispatcher.Instance;
                events.Dispatch(() => handler(reason));
            }
        }

        private async Task CloseLinkNoGateAsync()
        {
            await StopHeartbeatNoGateAsync().ConfigureAwait(false);
            _link?.Dispose();
            _link = null;
            HeartbeatState = OnlineHeartbeatState.Disabled;
        }

        private void ReportStage(OnlineStage stage)
        {
            if (_onStage == null) return;
            Dispatch(() => _onStage(stage));
        }

        private void Dispatch(Action callback)
        {
            if (_dispatcher == null)
            {
                callback();
            }
            else
            {
                _dispatcher.Dispatch(callback);
            }
        }

        private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeStop.Token);
        }

        private void ThrowIfDisposed()
        {
            if (State == OnlineClientSessionState.Disposed)
            {
                throw new ObjectDisposedException(nameof(OnlineClientSession));
            }
        }
    }
}
