using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Gateway;

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
        private TcpClient _tcp;

        internal OnlineEntryResult(OnlineStage stage, OnlineFailure failure, byte? loginFailureCode, int slot,
            GameClientSession session, TcpClient tcp, int framesSeen, int secondLayerMismatches)
        {
            Stage = stage;
            Failure = failure;
            LoginFailureCode = loginFailureCode;
            Slot = slot;
            Session = session;
            _tcp = tcp;
            FramesSeen = framesSeen;
            SecondLayerMismatches = secondLayerMismatches;
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

        /// <summary>关闭连接与会话。</summary>
        public void Dispose()
        {
            _tcp?.Dispose();
            _tcp = null;
        }
    }

    /// <summary>
    /// 联网进城编排：网关登录 → 取游戏服地址 → 连频道收启动包 → 游戏登录 →（顺带请求一次角色列表，应答可能被封进信封，先不依赖）
    /// → 逐槽选角 → 等到进城完成(E-357、E-358、E-359)。全程没有落盘与打印;凭据只在进程内。
    /// </summary>
    public sealed class OnlineTownEntry
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan BootstrapTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan LoginWindow = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ListWindow = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SlotWindow = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan TailWindow = TimeSpan.FromSeconds(3);

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

                onStage?.Invoke(OnlineStage.GameLogin);
                OnlineEntryResult loginFailure = await TryGameLogin(session, token, tcp, cancellationToken).ConfigureAwait(false);
                if (loginFailure != null)
                {
                    return loginFailure;
                }

                // 帧数按会话的脱敏事件总数计(只进不减)。
                framesSeen = session.Events.Count;

                onStage?.Invoke(OnlineStage.SelectSlot);
                for (int slot = 0; slot < _settings.MaxSlots; slot++)
                {
                    FrameStatus sent = await session.SendAsync(PacketClass.Cmd, (ushort)SlotJudge.SelectPacketId, new byte[] { (byte)slot }, cancellationToken).ConfigureAwait(false);
                    if (sent != FrameStatus.Ok)
                    {
                        break;
                    }
                    List<DecodedFrame> window = await session.ReceiveAsync(SlotWindow, stopAfterFrames: 64, cancellationToken).ConfigureAwait(false);
                    framesSeen = session.Events.Count;
                    SlotOutcome outcome = SlotJudge.Judge(window);
                    if (outcome.Kind == SlotOutcomeKind.Entered)
                    {
                        await session.ReceiveAsync(TailWindow, cancellationToken: cancellationToken).ConfigureAwait(false);
                        return new OnlineEntryResult(OnlineStage.Done, OnlineFailure.None, null, slot, session, tcp, session.Events.Count, session.SecondLayerMismatches);
                    }
                }
                return Fail(OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, null, -1, session.Events.Count, null, tcp, session.SecondLayerMismatches);
            }
        }

        /// <summary>游戏登录一步:成功返回 null;失败返回现成的结果对象(连接由结果接管释放)。</summary>
        private async Task<OnlineEntryResult> TryGameLogin(GameClientSession session, string token, TcpClient tcp, CancellationToken cancellationToken)
        {
            uint localAddress = 0;
            if (tcp.Client.LocalEndPoint is IPEndPoint local && local.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = local.Address.GetAddressBytes();
                localAddress = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            }

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
                return Fail(OnlineStage.GameLogin, OnlineFailure.BadLoginRequest, null, -1, session.Events.Count, null, tcp, 0);
            }

            FrameStatus sent = await session.SendAsync(PacketClass.Cmd, LoginRequest.PacketId, request.Build(), cancellationToken).ConfigureAwait(false);
            if (sent != FrameStatus.Ok)
            {
                return Fail(OnlineStage.GameLogin, OnlineFailure.GameLoginNoReply, null, -1, session.Events.Count, null, tcp, 0);
            }

            List<DecodedFrame> window = await session.ReceiveAsync(LoginWindow, stopAfterFrames: 8, cancellationToken).ConfigureAwait(false);
            DecodedFrame reply = window.Find(f => f.Header.Class == PacketClass.Cmd && f.Header.PacketId == LoginRequest.PacketId && f.Payload.Length >= 1);
            if (reply == null)
            {
                return Fail(OnlineStage.GameLogin, OnlineFailure.GameLoginNoReply, null, -1, session.Events.Count, null, tcp, 0);
            }
            byte? failureCode = LoginReplyParse.FailureCode(reply.Payload);
            if (reply.Payload[0] != 1 || failureCode.HasValue)
            {
                return Fail(OnlineStage.GameLogin, OnlineFailure.GameLoginRejected, failureCode, -1, session.Events.Count, null, tcp, 0);
            }

            // 顺带发一次角色列表请求(类型 2):应答现在可能被封进 561 信封(E-358),拿到也只留在会话事件里,不依赖它选角。
            if (LoginReplyParse.TryGetAccountId(reply.Payload, out int accountId))
            {
                var listRequest = new byte[3];
                listRequest[0] = (byte)(accountId & 0xff);
                listRequest[1] = (byte)((accountId >> 8) & 0xff);
                listRequest[2] = 2;
                await session.SendAsync(PacketClass.Cmd, 8, listRequest, cancellationToken).ConfigureAwait(false);
                await session.ReceiveAsync(ListWindow, stopAfterFrames: 12, cancellationToken).ConfigureAwait(false);
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

        private static OnlineEntryResult Fail(OnlineStage stage, OnlineFailure failure, byte? code, int slot, int framesSeen, GameClientSession session, TcpClient tcp, int mismatches)
        {
            return new OnlineEntryResult(stage, failure, code, slot, session, tcp, framesSeen, mismatches);
        }
    }
}
