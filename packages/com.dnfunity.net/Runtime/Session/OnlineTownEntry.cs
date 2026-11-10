using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Gateway;

// 进城的"启动包之后"那一段(EnterOnSessionAsync)与它的窗口长度是内部成员,假会话测试要直接走到它们
[assembly: InternalsVisibleTo("Dnf.Net.Tests")]

namespace Dnf.Net.Session
{
    /// <summary>联网进城的进度阶段(单调前进,失败时停在出错的那一步)。</summary>
    public enum OnlineStage
    {
        /// <summary>网关登录(换令牌)。</summary>
        GatewayLogin = 0,

        /// <summary>取游戏服地址。</summary>
        GameServerAddress = 1,

        /// <summary>连游戏频道。</summary>
        ChannelConnect = 2,

        /// <summary>收频道启动包并初始化会话密码。</summary>
        Bootstrap = 3,

        /// <summary>发游戏登录命令并等成功回包。</summary>
        GameLogin = 4,

        /// <summary>逐槽选角,直到进城完成。</summary>
        SelectSlot = 5,

        /// <summary>全部走完,已进城(终局)。</summary>
        Done = 6,
    }

    /// <summary>联网进城失败的原因(与停在哪一步一起看)。</summary>
    public enum OnlineFailure
    {
        /// <summary>没有失败。</summary>
        None = 0,

        /// <summary>网关登录没拿到令牌。</summary>
        GatewayLoginFailed,

        /// <summary>没有可用的游戏服地址。</summary>
        NoGameServerAddress,

        /// <summary>连不上游戏频道。</summary>
        ConnectFailed,

        /// <summary>没收到频道启动包。</summary>
        BootstrapFailed,

        /// <summary>连接在接受阶段被服务端拒绝。</summary>
        Rejected,

        /// <summary>游戏登录回包里给的是失败码(码在 <see cref="OnlineEntryResult.LoginFailureCode"/>)。</summary>
        GameLoginRejected,

        /// <summary>游戏登录没等到回包。</summary>
        GameLoginNoReply,

        /// <summary>逐个槽试完,没有一个能进城。</summary>
        NoUsableSlot,

        /// <summary>登录请求本地检查不过(字段不合规)。</summary>
        BadLoginRequest,
    }

    /// <summary>联网进城的入参(凭据由调用方提供,本类不保存到任何地方)。</summary>
    public sealed class OnlineEntrySettings
    {
        /// <summary>网关连接参数(地址与密钥)。</summary>
        public GatewayOptions Gateway { get; set; }

        /// <summary>账号(只在进程内用)。</summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>口令明文(只在进程内用;出站前按网关规格取 MD5)。</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>8 字节鼠标口令密文;为空时发全零(服务端大概率回失败,见 E-357)。</summary>
        public byte[] MouseCipher { get; set; }

        /// <summary>客户端数据版本(要与服务端参数脚本一致,填错回码 0x17,E-328)。</summary>
        public uint DataVersion { get; set; }

        /// <summary>游戏频道端口。</summary>
        public int ChannelPort { get; set; } = 30011;

        /// <summary>逐槽选角最多试多少个槽(从 0 起)。</summary>
        public int MaxSlots { get; set; } = 6;
    }

    /// <summary>联网进城的结局。成功时 <see cref="Session"/> 与连接归调用方,记得释放本结果对象。</summary>
    public sealed class OnlineEntryResult : IDisposable
    {
        private IDisposable _connection;

        internal OnlineEntryResult(OnlineStage stage, OnlineFailure failure, byte? loginFailureCode, int slot,
            GameClientSession session, IDisposable connection, int framesSeen, int secondLayerMismatches,
            TownEntrySnapshot snapshot = null)
        {
            Stage = stage;
            Failure = failure;
            LoginFailureCode = loginFailureCode;
            Slot = slot;
            Session = session;
            _connection = connection;
            FramesSeen = framesSeen;
            SecondLayerMismatches = secondLayerMismatches;
            Snapshot = snapshot;
        }

        /// <summary>停在的阶段(成功为 <see cref="OnlineStage.Done"/>)。</summary>
        public OnlineStage Stage { get; }

        /// <summary>失败原因(成功为 <see cref="OnlineFailure.None"/>)。</summary>
        public OnlineFailure Failure { get; }

        /// <summary>是否已经进城。</summary>
        public bool Entered => Stage == OnlineStage.Done;

        /// <summary>游戏登录的失败码(仅 <see cref="OnlineFailure.GameLoginRejected"/> 时有)。</summary>
        public byte? LoginFailureCode { get; }

        /// <summary>进城用的槽位(没成功为 -1)。</summary>
        public int Slot { get; }

        /// <summary>已进城的会话(没成功为 null);结果对象释放时随之关闭。</summary>
        public GameClientSession Session { get; }

        /// <summary>全程解出的入站帧数。</summary>
        public int FramesSeen { get; }

        /// <summary>第二层校验不符的帧数。</summary>
        public int SecondLayerMismatches { get; }

        /// <summary>
        /// 进城回程明文帧的只读快照(没进城为 null;进城后永不为 null,字段缺口见 <see cref="TownEntrySnapshot"/>)。
        /// 喂给解析器的帧是城镇阶段收到的全部帧,各一遍:登录回包窗口、登录后拉角色列表的窗口(应答若被封进信封,会话层拆开后的内层帧也在其中)、
        /// 选角窗口、尾窗口;进副本阶段的帧不在其中。
        /// 只是附加信息:不影响阶段与失败判定。
        /// </summary>
        public TownEntrySnapshot Snapshot { get; }

        /// <summary>关闭连接与会话。</summary>
        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>
    /// 联网进城编排：网关登录 → 取游戏服地址 → 连频道收启动包 → 游戏登录 →（顺带请求一次角色列表，应答可能被封进信封，不依赖它选角，
    /// 但它的帧会留给进城快照回填角色的等级、职业与成长类型）→ 逐槽选角 → 等到进城完成(E-357、E-358、E-359)。
    /// 全程没有落盘与打印;凭据只在进程内。
    /// </summary>
    public sealed class OnlineTownEntry
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan BootstrapTimeout = TimeSpan.FromSeconds(15);

        private readonly OnlineEntrySettings _settings;

        /// <summary>按入参创建编排。</summary>
        public OnlineTownEntry(OnlineEntrySettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>跑完整条链;中途失败返回带原因的结果,取消时抛 <see cref="OperationCanceledException"/>。</summary>
        public async Task<OnlineEntryResult> RunAsync(Action<OnlineStage> onStage = null, CancellationToken cancellationToken = default)
        {
            int framesSeen = 0;

            onStage?.Invoke(OnlineStage.GatewayLogin);
            string token;
            using (GatewayClient gateway = CreateGateway())
            {
                GatewayResult login = await gateway.LoginAsync(_settings.Account, _settings.Password, cancellationToken).ConfigureAwait(false);
                if (!login.IsSuccess || login.Token == null)
                {
                    return Fail(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed, null, -1, framesSeen, null, null, 0);
                }
                token = login.Token;

                onStage?.Invoke(OnlineStage.GameServerAddress);
                GatewayResult ip = await gateway.GetGameServerIpAsync(cancellationToken).ConfigureAwait(false);
                if (!ip.IsSuccess || ip.Text == null || !IPAddress.TryParse(ip.Text.Trim(), out IPAddress address))
                {
                    return Fail(OnlineStage.GameServerAddress, OnlineFailure.NoGameServerAddress, null, -1, framesSeen, null, null, 0);
                }

                onStage?.Invoke(OnlineStage.ChannelConnect);
                TcpClient tcp = new TcpClient();
                Task connectTask = tcp.ConnectAsync(address, _settings.ChannelPort);
                Task timeoutTask = Task.Delay(ConnectTimeout, cancellationToken);
                if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) != connectTask || !tcp.Connected)
                {
                    tcp.Dispose();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    return Fail(OnlineStage.ChannelConnect, OnlineFailure.ConnectFailed, null, -1, framesSeen, null, null, 0);
                }

                var session = new GameClientSession(tcp.GetStream());
                onStage?.Invoke(OnlineStage.Bootstrap);
                bool boot = await session.ReceiveBootstrapAsync(BootstrapTimeout, cancellationToken).ConfigureAwait(false);
                if (!boot)
                {
                    OnlineFailure failure = session.Rejected ? OnlineFailure.Rejected : OnlineFailure.BootstrapFailed;
                    return Fail(OnlineStage.Bootstrap, failure, null, -1, framesSeen, null, tcp, 0);
                }

                return await EnterOnSessionAsync(session, token, LocalAddressOf(tcp), tcp, onStage, null, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 收到启动包之后的后半段:游戏登录 → 顺带拉一次角色列表 → 逐槽选角 → 进城后做快照。
        /// 从 <see cref="RunAsync"/> 里拆出来,只为让假会话(内存里的管道)能走到真代码;<see cref="RunAsync"/> 把网关、取地址、
        /// 连频道、收启动包接在它前面,行为与拆出来之前一致。<paramref name="connection"/> 是与会话同生死的连接
        /// (真链路是 <see cref="TcpClient"/>,测试里可以是任何 <see cref="IDisposable"/>),成功或失败的结果对象都接管它的释放。
        /// </summary>
        internal async Task<OnlineEntryResult> EnterOnSessionAsync(GameClientSession session, string token, uint localAddress,
            IDisposable connection, Action<OnlineStage> onStage, OnlineTownWindows windows, CancellationToken cancellationToken)
        {
            OnlineTownWindows w = windows ?? OnlineTownWindows.Default;

            onStage?.Invoke(OnlineStage.GameLogin);
            var earlyFrames = new List<DecodedFrame>();
            OnlineEntryResult loginFailure = await TryGameLogin(session, token, localAddress, connection, w, earlyFrames, cancellationToken).ConfigureAwait(false);
            if (loginFailure != null)
            {
                return loginFailure;
            }

            onStage?.Invoke(OnlineStage.SelectSlot);
            for (int slot = 0; slot < _settings.MaxSlots; slot++)
            {
                FrameStatus sent = await session.SendAsync(PacketClass.Cmd, (ushort)SlotJudge.SelectPacketId, new byte[] { (byte)slot }, cancellationToken).ConfigureAwait(false);
                if (sent != FrameStatus.Ok)
                {
                    break;
                }
                List<DecodedFrame> window = await session.ReceiveAsync(w.Slot, stopAfterFrames: 64, cancellationToken).ConfigureAwait(false);
                SlotOutcome outcome = SlotJudge.Judge(window);
                if (outcome.Kind == SlotOutcomeKind.Entered)
                {
                    List<DecodedFrame> tail = await session.ReceiveAsync(w.Tail, cancellationToken: cancellationToken).ConfigureAwait(false);

                    // 进城后给明文帧做只读快照,喂的是城镇阶段收到的全部帧,各一遍:登录回包窗口与登录后拉角色列表的窗口(早先被丢弃的那部分;
                    // 应答被信封封住的,会话层拆开后的内层帧已紧跟在信封帧后面)、选角窗口、尾窗口。进副本阶段的帧不在其中(快照在这里定下,之后不再喂)。
                    // 解析器对错位与未知帧号只跳过,不抛。
                    var entryFrames = new List<DecodedFrame>(earlyFrames.Count + window.Count + tail.Count);
                    entryFrames.AddRange(earlyFrames);
                    entryFrames.AddRange(window);
                    entryFrames.AddRange(tail);
                    TownEntrySnapshot snapshot = TownEntryParser.Parse(entryFrames, slot);

                    // 帧数按会话的脱敏事件总数计(只进不减)。
                    return new OnlineEntryResult(OnlineStage.Done, OnlineFailure.None, null, slot, session, connection, session.Events.Count, session.SecondLayerMismatches, snapshot);
                }
            }
            return Fail(OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, null, -1, session.Events.Count, null, connection, session.SecondLayerMismatches);
        }

        /// <summary>本机出口地址按小端 u32 取(登录请求里要带);不是 IPv4 或取不到就是 0。</summary>
        private static uint LocalAddressOf(TcpClient tcp)
        {
            if (tcp.Client.LocalEndPoint is IPEndPoint local && local.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = local.Address.GetAddressBytes();
                return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            }
            return 0;
        }

        /// <summary>
        /// 游戏登录一步:成功返回 null;失败返回现成的结果对象(连接由结果接管释放)。
        /// 登录回包窗口里收到的全部帧,以及登录成功后顺带拉的那次角色列表的窗口里收到的全部帧,按到达顺序追加进 <paramref name="earlyFrames"/>
        /// (调用方进城后交给 <see cref="TownEntryParser"/>;登录失败时结果对象不带快照,追加的内容不会被用到)。
        /// </summary>
        private async Task<OnlineEntryResult> TryGameLogin(GameClientSession session, string token, uint localAddress,
            IDisposable connection, OnlineTownWindows windows, List<DecodedFrame> earlyFrames, CancellationToken cancellationToken)
        {
            byte[] mouseCipher = _settings.MouseCipher != null && _settings.MouseCipher.Length == LoginRequest.MouseCipherBytes
                ? _settings.MouseCipher
                : new byte[LoginRequest.MouseCipherBytes];
            var request = new LoginRequest
            {
                PatchTime = 0,
                Token = Encoding.ASCII.GetBytes(token),
                UserName = Encoding.ASCII.GetBytes(_settings.Account),
                PasswordBlock = Encoding.ASCII.GetBytes(Md5HexLower(_settings.Password)),
                LoginMode = 0,
                MouseCipher = mouseCipher,
                DataVersion = _settings.DataVersion,
                ServerGroup = 0,
                LocalAddress = localAddress,
            };
            if (request.Validate() != LoginRequestProblem.None)
            {
                return Fail(OnlineStage.GameLogin, OnlineFailure.BadLoginRequest, null, -1, session.Events.Count, null, connection, 0);
            }

            FrameStatus sent = await session.SendAsync(PacketClass.Cmd, LoginRequest.PacketId, request.Build(), cancellationToken).ConfigureAwait(false);
            if (sent != FrameStatus.Ok)
            {
                return Fail(OnlineStage.GameLogin, OnlineFailure.GameLoginNoReply, null, -1, session.Events.Count, null, connection, 0);
            }

            List<DecodedFrame> window = await session.ReceiveAsync(windows.Login, stopAfterFrames: 8, cancellationToken).ConfigureAwait(false);
            earlyFrames.AddRange(window);
            DecodedFrame reply = window.Find(f => f.Header.Class == PacketClass.Cmd && f.Header.PacketId == LoginRequest.PacketId && f.Payload.Length >= 1);
            if (reply == null)
            {
                return Fail(OnlineStage.GameLogin, OnlineFailure.GameLoginNoReply, null, -1, session.Events.Count, null, connection, 0);
            }
            byte? failureCode = LoginReplyParse.FailureCode(reply.Payload);
            if (reply.Payload[0] != 1 || failureCode.HasValue)
            {
                return Fail(OnlineStage.GameLogin, OnlineFailure.GameLoginRejected, failureCode, -1, session.Events.Count, null, connection, 0);
            }

            // 顺带发一次角色列表请求(类型 2):应答现在可能被封进 561 信封(E-358)。不依赖它选角(没有应答也照常往下走),
            // 但窗口里的帧留给调用方(早先是收了就丢),进城后交给 TownEntryParser 回填快照的等级、职业与成长类型。
            if (LoginReplyParse.TryGetAccountId(reply.Payload, out int accountId))
            {
                var listRequest = new byte[3];
                listRequest[0] = (byte)(accountId & 0xff);
                listRequest[1] = (byte)((accountId >> 8) & 0xff);
                listRequest[2] = 2;
                await session.SendAsync(PacketClass.Cmd, 8, listRequest, cancellationToken).ConfigureAwait(false);
                earlyFrames.AddRange(await session.ReceiveAsync(windows.CharacterList, stopAfterFrames: 12, cancellationToken).ConfigureAwait(false));
            }
            return null;
        }

        private GatewayClient CreateGateway()
        {
            // 游戏服与网关都在自有网络上,这里关掉系统代理直连(与探针同一做法)。
            var transport = new HttpGatewayTransport(
                new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseProxy = false },
                _settings.Gateway.Timeout);
            return new GatewayClient(_settings.Gateway, transport);
        }

        private static string Md5HexLower(string text)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static OnlineEntryResult Fail(OnlineStage stage, OnlineFailure failure, byte? code, int slot, int framesSeen, GameClientSession session, IDisposable connection, int mismatches)
        {
            return new OnlineEntryResult(stage, failure, code, slot, session, connection, framesSeen, mismatches);
        }
    }

    /// <summary>
    /// 联网进城里"启动包之后"各收帧窗口的长度:登录、角色列表、选角、尾窗口。线上用 <see cref="Default"/>(实测定下的一组:8、2、10、3 秒);
    /// 合成测试换一组很短的,免得每个用例白等十几秒。窗口只决定"最多等多久",收帧数的上限(登录 8、列表 12、选角 64)不变。
    /// </summary>
    internal sealed class OnlineTownWindows
    {
        /// <summary>线上用的一组。</summary>
        public static readonly OnlineTownWindows Default = new OnlineTownWindows(
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));

        public OnlineTownWindows(TimeSpan login, TimeSpan characterList, TimeSpan slot, TimeSpan tail)
        {
            Login = login;
            CharacterList = characterList;
            Slot = slot;
            Tail = tail;
        }

        /// <summary>等游戏登录回包的窗口。</summary>
        public TimeSpan Login { get; }

        /// <summary>发出角色列表请求后收应答的窗口。</summary>
        public TimeSpan CharacterList { get; }

        /// <summary>发出选角命令后收回程的窗口。</summary>
        public TimeSpan Slot { get; }

        /// <summary>判定进城后再收尾巴的窗口。</summary>
        public TimeSpan Tail { get; }
    }
}
