using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Play;

[assembly: InternalsVisibleTo("Dnf.Client.Tests")]

namespace Dnf.Client
{
    /// <summary>登录一步的报告(只有枚举与秒数,不带令牌、地址或口令)。</summary>
    internal readonly struct OnlineLoginReport
    {
        public OnlineLoginReport(OnlineFailure failure, GatewayOutcome outcome, GatewayErrorCode errorCode, int retryAfterSeconds)
        {
            Failure = failure;
            Outcome = outcome;
            ErrorCode = errorCode;
            RetryAfterSeconds = retryAfterSeconds;
        }

        public OnlineFailure Failure { get; }
        public GatewayOutcome Outcome { get; }
        public GatewayErrorCode ErrorCode { get; }
        public int RetryAfterSeconds { get; }
    }

    /// <summary>连频道一步的报告。</summary>
    internal readonly struct OnlineConnectReport
    {
        public OnlineConnectReport(OnlineFailure failure, OnlineCharacterListStatus listStatus, UserInfoNotice list)
        {
            Failure = failure;
            ListStatus = listStatus;
            List = list;
        }

        public OnlineFailure Failure { get; }
        public OnlineCharacterListStatus ListStatus { get; }
        public UserInfoNotice List { get; }
    }

    /// <summary>选角一步的报告;<see cref="SnapshotLine"/> 是进城快照的日志行(只有槽号、等级、职业与计数,没有名字),没进去时为 null。</summary>
    internal readonly struct OnlineSelectReport
    {
        public OnlineSelectReport(bool entered, bool mayTryAnotherSlot, string snapshotLine)
        {
            Entered = entered;
            MayTryAnotherSlot = mayTryAnotherSlot;
            SnapshotLine = snapshotLine;
        }

        public bool Entered { get; }
        public bool MayTryAnotherSlot { get; }
        public string SnapshotLine { get; }
    }

    /// <summary>
    /// 一个联网会话的最小接口(<see cref="OnlineClientSession"/> 的一层薄皮):把它的结果对象压成上面几个"报告"。
    /// 存在的唯一理由是测试:联网层的结果类没有公开构造函数,假会话只能造报告;生命周期(一次登录一个新会话、放弃时在后台释放、过期会话的断线事件不算数)就能不连网络地测。
    /// </summary>
    internal interface IOnlineSessionPort : IDisposable
    {
        /// <summary>连接掉线(后台线程上)。</summary>
        event Action Lost;

        /// <summary>背后的真实会话(给以后的副本线接手用);假实现是 null。</summary>
        OnlineClientSession Real { get; }

        Task<OnlineLoginReport> LoginAsync(string username, string password, CancellationToken cancellationToken);

        Task<OnlineConnectReport> ConnectAsync(int channelPort, CancellationToken cancellationToken);

        Task<OnlineConnectReport> SwitchAsync(int channelPort, CancellationToken cancellationToken);

        Task<OnlineSelectReport> SelectAsync(int slot, CancellationToken cancellationToken);
    }

    /// <summary>真会话的皮。进城之后心跳由会话自己管(默认自管心跳),这里什么都不用做。</summary>
    internal sealed class RealOnlineSessionPort : IOnlineSessionPort
    {
        private readonly OnlineClientSession _session;

        public RealOnlineSessionPort(OnlineEntrySettings settings)
        {
            _session = new OnlineClientSession(settings);
            _session.Disconnected += OnDisconnected;
        }

        public event Action Lost;

        public OnlineClientSession Real => _session;

        public async Task<OnlineLoginReport> LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            OnlineLoginResult result = await _session.LoginAsync(username, password, cancellationToken).ConfigureAwait(false);
            return new OnlineLoginReport(result.Failure, result.GatewayOutcome, result.GatewayErrorCode, result.RetryAfterSeconds);
        }

        public async Task<OnlineConnectReport> ConnectAsync(int channelPort, CancellationToken cancellationToken)
        {
            return Report(await _session.ConnectAsync(channelPort, cancellationToken).ConfigureAwait(false));
        }

        public async Task<OnlineConnectReport> SwitchAsync(int channelPort, CancellationToken cancellationToken)
        {
            return Report(await _session.SwitchChannelAsync(channelPort, cancellationToken).ConfigureAwait(false));
        }

        public async Task<OnlineSelectReport> SelectAsync(int slot, CancellationToken cancellationToken)
        {
            OnlineSelectCharacterResult result = await _session.SelectCharacterAsync(slot, cancellationToken).ConfigureAwait(false);
            return new OnlineSelectReport(result.Entered, result.MayTryAnotherSlot, result.Entered ? OnlineNetLog.Snapshot(result.Snapshot) : null);
        }

        public void Dispose()
        {
            _session.Disconnected -= OnDisconnected;
            _session.Dispose();
        }

        private static OnlineConnectReport Report(OnlineConnectResult result)
        {
            return new OnlineConnectReport(result.Failure, result.CharacterListStatus, result.CharacterList);
        }

        private void OnDisconnected(OnlineDisconnectReason reason)
        {
            Lost?.Invoke();
        }
    }

    /// <summary>
    /// 联网外壳(<see cref="OnlineShellFlow"/>)的真后端:把外壳要的那几个网络操作接到联网层的分阶段会话(<see cref="OnlineClientSession"/>)上。
    /// <list type="bullet">
    /// <item>一次登录一个全新的会话(会话只能登录一次):上一个会话被放弃,在后台线程里释放(它的释放会等在途操作结束,不能在渲染线程上做)。</item>
    /// <item>所有操作都在线程池上跑,调用立刻返回任务:外壳在渲染线程上调用它们、之后只轮询任务有没有完成。</item>
    /// <item>注册与改密不需要会话,各开一个临时的网关客户端(与登录同一套传输:不跟随重定向、不走系统代理)。</item>
    /// <item><see cref="Disconnect"/> 立刻放弃当前会话、不触发 <see cref="Disconnected"/>;被放弃的旧会话之后再报断线(过期事件)一律不算数。</item>
    /// <item>进城成功之后会话继续活着(<see cref="Session"/>),归宿主管,直到放弃(退出、换角色、断线、回登录)。以后的进副本线从这里拿会话调 <c>EnterExclusiveReceiveAsync</c>。</item>
    /// <item>要连的频道带主机(<see cref="ChannelTarget.Host"/> 不为 null)现在不支持:联网会话只连网关给的游戏服地址,宁可明说连不上也不悄悄连到另一台。</item>
    /// </list>
    /// 不打印账号、口令、地址、令牌;只在选角成功时记一行进城快照(槽号、等级、职业与计数)。
    /// </summary>
    public sealed class OnlineShellBackend : IOnlineBackend, IDisposable
    {
        private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(1);

        private sealed class Slot
        {
            public Slot(IOnlineSessionPort port)
            {
                Port = port;
            }

            public IOnlineSessionPort Port { get; }

            public Action LostHandler;

            public volatile bool Retired;
        }

        private readonly OnlineEntrySettings _template;
        private readonly Func<OnlineEntrySettings, IOnlineSessionPort> _portFactory;
        private readonly Func<GatewayOptions, IGatewayTransport> _transportFactory;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private readonly List<Task> _disposals = new List<Task>();
        private Slot _slot;
        private bool _disposed;

        /// <param name="template">网关、鼠标口令密文、数据版本等(账号与口令留空:登录时由界面输入);为 null 时所有网络操作都失败(外壳在没有连接配置时本来就不会调用)。</param>
        /// <param name="log">日志去处(只写进城快照一行);可为 null。</param>
        public OnlineShellBackend(OnlineEntrySettings template, Action<string> log = null)
            : this(template, log, settings => new RealOnlineSessionPort(settings), CreateHttpTransport)
        {
        }

        internal OnlineShellBackend(OnlineEntrySettings template, Action<string> log,
            Func<OnlineEntrySettings, IOnlineSessionPort> portFactory, Func<GatewayOptions, IGatewayTransport> transportFactory)
        {
            _template = template;
            _log = log;
            _portFactory = portFactory ?? throw new ArgumentNullException(nameof(portFactory));
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        }

        /// <summary>连接掉线(只在当前会话、连接建立之后掉线时触发;可能在任何线程上)。</summary>
        public event Action Disconnected;

        /// <summary>当前的联网会话(登录之后到放弃之前);没有会话,或会话不是真实现(测试用的假会话)时是 null。</summary>
        public OnlineClientSession Session
        {
            get
            {
                lock (_lock)
                {
                    return _slot?.Port.Real;
                }
            }
        }

        /// <summary>现在有没有会话(登录请求发出之后到放弃之前)。</summary>
        public bool HasSession
        {
            get
            {
                lock (_lock)
                {
                    return _slot != null;
                }
            }
        }

        public Task<OnlineAuthOutcome> LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            Slot slot;
            try
            {
                slot = NewSlot();
            }
            catch (Exception e)
            {
                return Task.FromException<OnlineAuthOutcome>(e);
            }
            return Task.Run(async () =>
            {
                OnlineLoginReport report = await slot.Port.LoginAsync(username, password, cancellationToken).ConfigureAwait(false);
                return OnlineShellMapping.Login(report.Failure, report.Outcome, report.ErrorCode, report.RetryAfterSeconds);
            }, cancellationToken);
        }

        public Task<OnlineAuthOutcome> RegisterAsync(string username, string password, CancellationToken cancellationToken)
        {
            return GatewayCall(client => client.RegisterAsync(username, password, null, cancellationToken), cancellationToken);
        }

        public Task<OnlineAuthOutcome> ChangePasswordAsync(string username, string oldPassword, string newPassword, CancellationToken cancellationToken)
        {
            return GatewayCall(client => client.ChangePasswordAsync(username, oldPassword, newPassword, cancellationToken), cancellationToken);
        }

        public Task<OnlineConnectOutcome> ConnectChannelAsync(ChannelTarget target, CancellationToken cancellationToken)
        {
            return ConnectCore(target, false, cancellationToken);
        }

        public Task<OnlineConnectOutcome> SwitchChannelAsync(ChannelTarget target, CancellationToken cancellationToken)
        {
            return ConnectCore(target, true, cancellationToken);
        }

        public Task<OnlineSelectOutcome> SelectCharacterAsync(int slot, CancellationToken cancellationToken)
        {
            Slot current;
            try
            {
                current = Current();
            }
            catch (Exception e)
            {
                return Task.FromException<OnlineSelectOutcome>(e);
            }
            return Task.Run(async () =>
            {
                OnlineSelectReport report = await current.Port.SelectAsync(slot, cancellationToken).ConfigureAwait(false);
                if (report.Entered && report.SnapshotLine != null)
                {
                    Log(report.SnapshotLine);
                }
                return OnlineShellMapping.Select(report.Entered, report.MayTryAnotherSlot);
            }, cancellationToken);
        }

        /// <summary>放弃当前会话:立刻摘掉,在后台线程里释放;不阻塞、不触发 <see cref="Disconnected"/>,重复调用无害。</summary>
        public void Disconnect()
        {
            Slot old;
            lock (_lock)
            {
                old = _slot;
                _slot = null;
                if (old != null)
                {
                    old.Retired = true;
                }
            }
            DisposeOffThread(old);
        }

        /// <summary>放弃会话并最多等一秒让后台的释放做完(宿主退出时调;之后所有网络操作都失败)。</summary>
        public void Dispose()
        {
            Slot old;
            Task[] pending;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                old = _slot;
                _slot = null;
                if (old != null)
                {
                    old.Retired = true;
                }
            }
            DisposeOffThread(old);
            lock (_lock)
            {
                pending = _disposals.ToArray();
            }
            try
            {
                Task.WaitAll(pending, DisposeWait);
            }
            catch (AggregateException)
            {
                // 释放的任务自己吞掉了异常;这里没有别的可做
            }
        }

        /// <summary>联网用的网关传输:用 <see cref="CreateGatewayHandler"/> 的处理器(不跟随重定向、不走系统代理;与联网层登录用的是同一套,游戏服与网关都在自有网络上)。</summary>
        internal static IGatewayTransport CreateHttpTransport(GatewayOptions options)
        {
            return new HttpGatewayTransport(CreateGatewayHandler(), options.Timeout);
        }

        /// <summary>
        /// 网关传输的 HTTP 处理器:一律不走系统代理(登录请求带着口令,不能让系统代理截走或记录)、不跟随重定向(3xx 响应原样交给调用方)。
        /// 单独成方法是为了让这两条硬规矩能被测试直接钉住。
        /// </summary>
        internal static HttpClientHandler CreateGatewayHandler()
        {
            return new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        }

        private OnlineEntrySettings CopyTemplate()
        {
            return new OnlineEntrySettings
            {
                Gateway = _template.Gateway,
                MouseCipher = _template.MouseCipher == null ? null : (byte[])_template.MouseCipher.Clone(),
                DataVersion = _template.DataVersion,
                ChannelPort = _template.ChannelPort,
                MaxSlots = _template.MaxSlots,
            };
        }

        /// <summary>造一个全新的会话顶替当前的(旧的被放弃)。在调用线程上做,很轻:这样紧跟着的 <see cref="Disconnect"/> 一定看得到它。</summary>
        private Slot NewSlot()
        {
            if (_template == null || _template.Gateway == null)
            {
                throw new InvalidOperationException("没有连接配置");
            }
            IOnlineSessionPort port = _portFactory(CopyTemplate());
            var fresh = new Slot(port);
            fresh.LostHandler = () => OnLost(fresh);
            port.Lost += fresh.LostHandler;
            Slot old;
            bool disposed;
            lock (_lock)
            {
                disposed = _disposed;
                old = _slot;
                if (!disposed)
                {
                    if (old != null)
                    {
                        old.Retired = true;
                    }
                    _slot = fresh;
                }
            }
            if (disposed)
            {
                fresh.Retired = true;
                DisposeOffThread(fresh);
                throw new ObjectDisposedException(nameof(OnlineShellBackend));
            }
            DisposeOffThread(old);
            return fresh;
        }

        private Slot Current()
        {
            lock (_lock)
            {
                if (_disposed || _slot == null)
                {
                    throw new InvalidOperationException("没有可用的会话(还没登录,或已经放弃)");
                }
                return _slot;
            }
        }

        private Task<OnlineConnectOutcome> ConnectCore(ChannelTarget target, bool switching, CancellationToken cancellationToken)
        {
            if (target == null)
            {
                return Task.FromException<OnlineConnectOutcome>(new ArgumentNullException(nameof(target)));
            }
            Slot slot;
            try
            {
                slot = Current();
            }
            catch (Exception e)
            {
                return Task.FromException<OnlineConnectOutcome>(e);
            }
            if (target.Host != null)
            {
                return Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
            }
            return Task.Run(async () =>
            {
                OnlineConnectReport report = switching
                    ? await slot.Port.SwitchAsync(target.Port, cancellationToken).ConfigureAwait(false)
                    : await slot.Port.ConnectAsync(target.Port, cancellationToken).ConfigureAwait(false);
                return OnlineShellMapping.Connect(report.Failure, report.ListStatus, report.List);
            }, cancellationToken);
        }

        private Task<OnlineAuthOutcome> GatewayCall(Func<GatewayClient, Task<GatewayResult>> call, CancellationToken cancellationToken)
        {
            if (_template == null || _template.Gateway == null)
            {
                return Task.FromException<OnlineAuthOutcome>(new InvalidOperationException("没有连接配置"));
            }
            GatewayOptions options = _template.Gateway;
            return Task.Run(async () =>
            {
                IGatewayTransport transport = _transportFactory(options);
                try
                {
                    using (var client = new GatewayClient(options, transport))
                    {
                        GatewayResult result = await call(client).ConfigureAwait(false);
                        return OnlineShellMapping.Auth(result.Outcome, result.ErrorCode, result.RetryAfterSeconds);
                    }
                }
                finally
                {
                    (transport as IDisposable)?.Dispose();
                }
            }, cancellationToken);
        }

        private void OnLost(Slot slot)
        {
            if (slot.Retired)
            {
                return;
            }
            lock (_lock)
            {
                if (!ReferenceEquals(_slot, slot))
                {
                    return;
                }
            }
            Disconnected?.Invoke();
        }

        private void DisposeOffThread(Slot slot)
        {
            if (slot == null)
            {
                return;
            }
            slot.Port.Lost -= slot.LostHandler;
            Task task = Task.Run(() =>
            {
                try
                {
                    slot.Port.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败没有可做的:会话已经不再被引用
                }
            });
            lock (_lock)
            {
                _disposals.RemoveAll(t => t.IsCompleted);
                _disposals.Add(task);
            }
        }

        private void Log(string line)
        {
            try
            {
                _log?.Invoke(line);
            }
            catch (Exception)
            {
                // 日志去处出错不能拖垮网络操作
            }
        }
    }
}
