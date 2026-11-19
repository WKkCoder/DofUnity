using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.ChannelDirectory;
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

    /// <summary>
    /// 网关登录与取游戏服地址的结果；令牌和地址只留在会话内部。
    /// 失败时另带"网关那一步到底是什么结局"的三个只读属性(<see cref="GatewayOutcome"/>、<see cref="GatewayErrorCode"/>、<see cref="RetryAfterSeconds"/>),
    /// 让界面能把"口令错、账号被封、被限流、网络或协议出错、本地输入不合规"分开说,而不是一律是"网关登录失败";它们只有枚举与秒数,不含令牌、地址或口令。
    /// </summary>
    public sealed class OnlineLoginResult
    {
        internal OnlineLoginResult(OnlineStage stage, OnlineFailure failure)
            : this(stage, failure, null)
        {
        }

        /// <param name="gateway">导致这个结果的那一次网关调用(登录或取游戏服地址)的结果;为 null 时三个属性取默认值。</param>
        internal OnlineLoginResult(OnlineStage stage, OnlineFailure failure, GatewayResult gateway)
        {
            Stage = stage;
            Failure = failure;
            if (gateway != null)
            {
                GatewayOutcome = gateway.Outcome;
                GatewayErrorCode = gateway.ErrorCode;
                RetryAfterSeconds = gateway.RetryAfterSeconds;
            }
        }

        public OnlineStage Stage { get; }
        public OnlineFailure Failure { get; }
        public bool IsSuccess => Failure == OnlineFailure.None;

        /// <summary>
        /// 那一次网关调用的结局类别:登录失败时是登录调用的,取游戏服地址失败时是取地址调用的,成功时是 <see cref="Gateway.GatewayOutcome.Success"/>。
        /// 注意取地址那一步"调用成功但地址不可用"(没有文本或不是 IP)时也是 Success,此时 <see cref="Failure"/> 才是 <see cref="OnlineFailure.NoGameServerAddress"/>。
        /// </summary>
        public GatewayOutcome GatewayOutcome { get; }

        /// <summary>服务端业务错误(<see cref="Gateway.GatewayOutcome.ServerError"/>)的错误枚举;其它情况是 <see cref="Gateway.GatewayErrorCode.None"/>。</summary>
        public GatewayErrorCode GatewayErrorCode { get; }

        /// <summary>被限流(<see cref="Gateway.GatewayOutcome.RateLimited"/>)时建议等待的秒数(至少 1);其它情况是 0。</summary>
        public int RetryAfterSeconds { get; }
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
        internal Func<string, int, CancellationToken, Task<ChannelDirectoryLoadResult>> DirectoryLoader { get; set; }
        internal OnlineTownWindows Windows { get; set; }
        internal bool AutoHeartbeat { get; set; } = true;
        internal bool InlineCallbacks { get; set; }
        internal bool ValidateSlot { get; set; } = true;
        internal TimeSpan HeartbeatPoll { get; set; } = TimeSpan.FromMilliseconds(50);
        internal TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(11);
        internal int SwitchLoginRetryCount { get; set; } = 6;
        internal TimeSpan SwitchLoginRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
        internal TimeSpan LogoutSettleWindow { get; set; } = TimeSpan.FromSeconds(5);
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
        private int _disconnectNotified;
        internal bool ReadFault { get; set; }
        internal bool WriteFault { get; set; }
        internal bool DisconnectNotified => Volatile.Read(ref _disconnectNotified) != 0;
        internal bool TryMarkDisconnectNotified() => Interlocked.Exchange(ref _disconnectNotified, 1) == 0;
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
    public sealed partial class OnlineClientSession : IDisposable
    {
        private readonly OnlineEntrySettings _settings;
        private readonly OnlineTownEntry _entry;
        private readonly Action<OnlineStage> _onStage;
        private readonly IOnlineDispatcher _dispatcher;
        private readonly OnlineClientSessionHooks _hooks;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _disposeStop = new CancellationTokenSource();
        private readonly object _mailboxGate = new object();
        private string _token;
        private IPAddress _address;
        private OnlineChannelLink _link;
        private List<DecodedFrame> _earlyFrames = new List<DecodedFrame>();
        private bool _selectionStageReported;
        private CancellationTokenSource _heartbeatStop;
        private Task _heartbeatTask;
        private CommandMailbox _activeMailbox;
        private bool _disposed;
        private bool _restoreHeartbeatOnLeave;
        private bool _detached;
        private bool _currentLinkAuthenticated;
        private long _lastAuthenticatedCloseTimestamp;
        private ChannelDirectorySnapshot _lastDirectory;

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
                ServerGroup = settings.ServerGroup,
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

        /// <summary>连接与换频道时写入 <see cref="LoginRequest.ServerGroup"/> 的服务器组编号（1..17；0 时尝试从最近目录按端口解析）。</summary>
        public ushort ServerGroup
        {
            get => _settings.ServerGroup;
            set => _settings.ServerGroup = value;
        }

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
                        return new OnlineLoginResult(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, login);
                    }
                    ReportStage(OnlineStage.GameServerAddress);
                    GatewayResult ip = await gateway.GetGameServerIpAsync(cancellationToken).ConfigureAwait(false);
                    if (!ip.IsSuccess || ip.Text == null || !IPAddress.TryParse(ip.Text.Trim(), out IPAddress address))
                    {
                        return new OnlineLoginResult(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress, ip);
                    }
                    _settings.Account = account;
                    _settings.Password = password;
                    _token = login.Token;
                    _address = address;
                    State = OnlineClientSessionState.LoggedIn;
                    return new OnlineLoginResult(OnlineStage.GameServerAddress, OnlineFailure.None, ip);
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
                return await ConnectWithSettleRetryNoGateAsync(_address, channelPort, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 连指定频道、收启动包并游戏登录；<paramref name="host"/> 为 null 时与只收端口的重载相同,
        /// 非 null 时用给定 IP 字面量(不做 DNS)代替网关游戏服地址。
        /// </summary>
        public async Task<OnlineConnectResult> ConnectAsync(string host, int channelPort, CancellationToken cancellationToken = default)
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
                if (!TryResolveChannelHost(host, out IPAddress address))
                {
                    return ConnectFailure(OnlineTownEntry.Fail(OnlineStage.ChannelConnect, OnlineFailure.ConnectFailed,
                        null, -1, 0, null, null, 0));
                }
                if (host == null)
                {
                    address = _address;
                }
                return await ConnectWithSettleRetryNoGateAsync(address, channelPort, cancellationToken).ConfigureAwait(false);
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
                return await ConnectWithSettleRetryNoGateAsync(_address, channelPort, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 断开旧频道并用给定 IP 字面量连新频道;<paramref name="host"/> 为 null 时与只收端口的重载相同。
        /// </summary>
        public async Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken = default)
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
                if (!TryResolveChannelHost(host, out IPAddress address))
                {
                    return ConnectFailure(OnlineTownEntry.Fail(OnlineStage.ChannelConnect, OnlineFailure.ConnectFailed,
                        null, -1, 0, null, null, 0));
                }
                if (host == null)
                {
                    address = _address;
                }
                return await ConnectWithSettleRetryNoGateAsync(address, channelPort, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 用登录拿到的游戏服地址拉取频道目录(单次只读,不重试);不占用连频道/选角的门闩,也不改会话状态机。
        /// </summary>
        public Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(
            int directoryPort = ChannelServiceConstants.DefaultPort,
            CancellationToken cancellationToken = default)
        {
            return LoadChannelDirectoryAsync(directoryPort, null, cancellationToken);
        }

        /// <summary>
        /// 用登录拿到的游戏服地址拉取频道目录(单次只读,不重试),并传入本地回退脚本(用于合并推荐等级等参数)。
        /// </summary>
        public async Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(
            int directoryPort,
            ChannelScriptData fallbackScript,
            CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            ThrowIfDisposed();
            if (_address == null)
            {
                throw new InvalidOperationException("尚未登录，不能读取频道目录");
            }
            string host = _address.ToString();
            ChannelDirectoryLoadResult result = _hooks.DirectoryLoader != null
                ? await _hooks.DirectoryLoader(host, directoryPort, cancellationToken).ConfigureAwait(false)
                : await ChannelDirectoryClient.LoadAsync(host, directoryPort, fallbackScript: fallbackScript, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            if (result != null && result.Succeeded && result.Snapshot != null)
            {
                Volatile.Write(ref _lastDirectory, result.Snapshot);
            }
            return result;
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
                CommandMailbox mailbox;
                lock (_mailboxGate)
                {
                    _restoreHeartbeatOnLeave = HeartbeatState == OnlineHeartbeatState.Running;
                    HeartbeatState = OnlineHeartbeatState.ExclusiveReceive;
                    State = OnlineClientSessionState.ExclusiveReceive;
                    mailbox = _activeMailbox;
                    _activeMailbox = null;
                }
                mailbox?.Close(CommandCloseReason.Stopped);
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
            if (_disposed || State == OnlineClientSessionState.Disposed) return;
            _disposeStop.Cancel();
            MarkDisposedAndCloseMailbox();
            _gate.Wait();
            try
            {
                if (State == OnlineClientSessionState.Disposed) return;
                State = OnlineClientSessionState.Disposed;
                _restoreHeartbeatOnLeave = false;
                _currentLinkAuthenticated = false;
                _lastAuthenticatedCloseTimestamp = 0;
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
            _restoreHeartbeatOnLeave = false;
            _currentLinkAuthenticated = false;
            _lastAuthenticatedCloseTimestamp = 0;
            State = OnlineClientSessionState.Disposed;
            MarkDisposedAndCloseMailbox();
            _heartbeatStop?.Cancel();
            _link = null;
            return result;
        }

        internal OnlineEntryResult NoUsableSlot(SelectCharacterResult lastFailure)
        {
            return OnlineTownEntry.Fail(OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, null, -1,
                _link?.Session.Events.Count ?? 0, null, null, _link?.Session.SecondLayerMismatches ?? 0, lastFailure);
        }

        private async Task<OnlineConnectResult> ConnectWithSettleRetryNoGateAsync(
            IPAddress address, int channelPort, CancellationToken cancellationToken)
        {
            double windowSeconds = _hooks.LogoutSettleWindow.TotalSeconds;
            bool withinSettleWindow = _lastAuthenticatedCloseTimestamp != 0
                && windowSeconds > 0
                && (Stopwatch.GetTimestamp() - _lastAuthenticatedCloseTimestamp) / (double)Stopwatch.Frequency <= windowSeconds;
            int maxRetries = withinSettleWindow && _hooks.SwitchLoginRetryCount > 0
                ? _hooks.SwitchLoginRetryCount
                : 0;

            try
            {
                OnlineConnectResult result = await ConnectNoGateAsync(
                    address, channelPort, maxRetries > 0, cancellationToken).ConfigureAwait(false);
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    if (result.IsSuccess || result.Failure != OnlineFailure.GameLoginRejected || result.LoginFailureCode != 1)
                    {
                        break;
                    }
                    await CloseLinkNoGateAsync().ConfigureAwait(false);
                    if (_hooks.SwitchLoginRetryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_hooks.SwitchLoginRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    bool suppressDisconnect = attempt + 1 < maxRetries;
                    result = await ConnectNoGateAsync(
                        address, channelPort, suppressDisconnect, cancellationToken).ConfigureAwait(false);
                }
                return result;
            }
            finally
            {
                _lastAuthenticatedCloseTimestamp = 0;
            }
        }

        private async Task<OnlineConnectResult> ConnectNoGateAsync(
            IPAddress address, int channelPort, bool suppressCode1Disconnect, CancellationToken cancellationToken)
        {
            _earlyFrames = new List<DecodedFrame>();
            _selectionStageReported = false;
            _restoreHeartbeatOnLeave = false;
            _currentLinkAuthenticated = false;
            ReportStage(OnlineStage.ChannelConnect);
            OnlineChannelLink link = _hooks.ChannelFactory != null
                ? await _hooks.ChannelFactory(address, channelPort, cancellationToken).ConfigureAwait(false)
                : await OpenChannelAsync(address, channelPort, cancellationToken).ConfigureAwait(false);
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
            ushort effectiveServerGroup = _settings.ServerGroup != 0
                ? _settings.ServerGroup
                : ResolveDirectoryServerGroup(address, channelPort);
            OnlineEntryResult loginFailure = await _entry.TryGameLogin(link.Session, _token, link.LocalAddress,
                link.Connection, _hooks.Windows ?? OnlineTownWindows.Default, _earlyFrames, cancellationToken,
                effectiveServerGroup).ConfigureAwait(false);
            if (loginFailure != null)
            {
                if (!(suppressCode1Disconnect && loginFailure.Failure == OnlineFailure.GameLoginRejected && loginFailure.LoginFailureCode == 1))
                {
                    NotifyIfDisconnected(link);
                }
                return ConnectFailure(loginFailure);
            }
            NotifyIfDisconnected(link);
            _currentLinkAuthenticated = true;
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
            var mailbox = new CommandMailbox();
            lock (_mailboxGate)
            {
                if (_disposed || State == OnlineClientSessionState.Disposed)
                {
                    stop.Dispose();
                    return;
                }
                _heartbeatStop = stop;
                _activeMailbox = mailbox;
                HeartbeatState = OnlineHeartbeatState.Running;
            }
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(link, mailbox, stop.Token));
        }

        private async Task StopHeartbeatNoGateAsync()
        {
            CloseActiveMailbox(CommandCloseReason.Stopped);
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
            lock (_mailboxGate)
            {
                if (HeartbeatState != OnlineHeartbeatState.ExclusiveReceive)
                {
                    HeartbeatState = OnlineHeartbeatState.Disabled;
                }
            }
        }

        private async Task HeartbeatLoopAsync(OnlineChannelLink link, CommandMailbox mailbox, CancellationToken cancellationToken)
        {
            Stopwatch idle = Stopwatch.StartNew();
            CommandCloseReason closeReason = CommandCloseReason.IoFault;
            try
            {
                while (!link.Session.PeerClosed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await DrainCommandsAsync(link, mailbox).ConfigureAwait(false))
                    {
                        return;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    List<DecodedFrame> window = await link.Session.ReceiveAsync(_hooks.HeartbeatPoll, 1, cancellationToken).ConfigureAwait(false);
                    if (window.Count > 0)
                    {
                        idle.Restart();
                    }
                    foreach (DecodedFrame frame in window)
                    {
                        if (OnlineHeartbeat.IsHeartbeat(frame))
                        {
                            FrameStatus sent = await link.Session.SendAsync(PacketClass.Cmd, OnlineHeartbeat.ReplyPacketId,
                                OnlineHeartbeat.ReplyPayload, CancellationToken.None).ConfigureAwait(false);
                            if (sent != FrameStatus.Ok)
                            {
                                NotifyDisconnected(link, OnlineDisconnectReason.WriteError);
                                return;
                            }
                            continue;
                        }
                        if (frame.Header.Class == PacketClass.Noti && frame.Header.PacketId == AntibotEnvelope.PacketId)
                        {
                            continue;
                        }
                        DeliverNonHeartbeatFrame(frame);
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
                closeReason = CommandCloseReason.Stopped;
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
                CloseMailboxOnPumpExit(link, mailbox, closeReason);
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
            if (!link.TryMarkDisconnectNotified())
            {
                return;
            }
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
            _restoreHeartbeatOnLeave = false;
            await StopHeartbeatNoGateAsync().ConfigureAwait(false);
            if (_link != null)
            {
                if (_currentLinkAuthenticated)
                {
                    _lastAuthenticatedCloseTimestamp = Stopwatch.GetTimestamp();
                }
                _link.Dispose();
                _link = null;
            }
            _currentLinkAuthenticated = false;
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

        private ushort ResolveDirectoryServerGroup(IPAddress address, int channelPort)
        {
            ChannelDirectorySnapshot snapshot = Volatile.Read(ref _lastDirectory);
            if (snapshot == null || snapshot.Channels.Count == 0 || channelPort < 1 || channelPort > 65535)
            {
                return 0;
            }
            ushort fallback = 0;
            for (int i = 0; i < snapshot.Channels.Count; i++)
            {
                ChannelEntry entry = snapshot.Channels[i];
                if (entry.Port != channelPort)
                {
                    continue;
                }
                if (!ChannelScriptData.TryMapServerGroupId(entry.GroupTag, out int groupId)
                    || groupId < 1
                    || groupId > LoginRequest.MaxServerGroup)
                {
                    continue;
                }
                if (address != null
                    && !string.IsNullOrWhiteSpace(entry.Host)
                    && IPAddress.TryParse(entry.Host.Trim(), out IPAddress entryHost)
                    && entryHost.Equals(address))
                {
                    return (ushort)groupId;
                }
                if (fallback == 0)
                {
                    fallback = (ushort)groupId;
                }
            }
            return fallback;
        }

        /// <summary><paramref name="host"/> 为 null 时由调用方使用网关地址;否则必须是可解析的 IP 字面量。</summary>
        private static bool TryResolveChannelHost(string host, out IPAddress address)
        {
            if (host == null)
            {
                address = null;
                return true;
            }
            if (string.IsNullOrWhiteSpace(host))
            {
                address = null;
                return false;
            }
            return IPAddress.TryParse(host.Trim(), out address);
        }
    }
}
