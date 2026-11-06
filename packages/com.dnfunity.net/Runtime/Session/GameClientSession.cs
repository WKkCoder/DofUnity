using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Crypto;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 会话里发生的一件事,已脱敏:只有方向、类、包号、长度与校验结果,不含载荷内容,可以直接打印或写日志。
    /// </summary>
    public sealed class SessionEvent
    {
        /// <summary>创建事件记录。</summary>
        public SessionEvent(bool outbound, PacketClass packetClass, int packetId, int frameLength, int payloadLength, string note)
        {
            Outbound = outbound;
            PacketClass = packetClass;
            PacketId = packetId;
            FrameLength = frameLength;
            PayloadLength = payloadLength;
            Note = note ?? string.Empty;
        }

        /// <summary>true 为客户端发出,false 为服务端发来。</summary>
        public bool Outbound { get; }

        /// <summary>包类。</summary>
        public PacketClass PacketClass { get; }

        /// <summary>包号。</summary>
        public int PacketId { get; }

        /// <summary>整帧字节数(含头)。</summary>
        public int FrameLength { get; }

        /// <summary>解码后的载荷字节数(解不出时为 0)。</summary>
        public int PayloadLength { get; }

        /// <summary>备注:帧种类、是否压缩、第二层校验结果或失败原因。</summary>
        public string Note { get; }

        /// <summary>一行文字(不含任何载荷内容)。</summary>
        public override string ToString()
        {
            return string.Format("{0} {1}/{2} 整帧 {3} 字节,载荷 {4} 字节{5}", Outbound ? "C→S" : "S→C",
                PacketClass == PacketClass.Cmd ? "CMD" : (PacketClass == PacketClass.Noti ? "NOTI" : "类" + (int)PacketClass),
                PacketId, FrameLength, PayloadLength, Note.Length == 0 ? string.Empty : "(" + Note + ")");
        }
    }

    /// <summary>
    /// 游戏频道服务的客户端会话(跑在任意字节流上):先等服务端主动发来的启动包(类 0、包号 1),取出 334 字节的会话密钥块,
    /// 初始化 14 个算法槽;之后可以发命令帧、收通知与回包。服务端在连接建立后约 0.75 秒内发启动包(E-297),
    /// 30 秒内不收到登录命令会以原因 0x1d 断开(E-299)。
    /// 本类只做字节流与帧层的接缝,不含任何账号、令牌或地址;事件记录(<see cref="Events"/>)已脱敏。
    /// </summary>
    public sealed class GameClientSession
    {
        private const int ReadChunk = 4096;

        private readonly Stream _stream;
        private readonly Func<int, BlockCipher> _cipherFactory;
        private readonly Func<byte> _randomByte;
        private readonly FrameAssembler _assembler = new FrameAssembler(FrameLayout.ServerToClient);
        private readonly ServerFrameDecoder _decoder = new ServerFrameDecoder();
        private readonly SecondLayerReceiver _secondLayer = new SecondLayerReceiver();
        private readonly List<RawFrame> _feed = new List<RawFrame>();
        private readonly Queue<RawFrame> _queue = new Queue<RawFrame>();
        private readonly List<SessionEvent> _events = new List<SessionEvent>();
        private readonly byte[] _chunk = new byte[ReadChunk];
        private ClientFrameBuilder _builder;
        private ushort _sequence;
        private bool _closed;

        /// <summary>
        /// 在 <paramref name="stream"/> 上建立会话。<paramref name="cipherFactory"/> 为空时用 14 个真实算法槽
        /// (<see cref="CipherSlots.Create"/>);<paramref name="randomByte"/> 为空时第二层校验用系统随机数。
        /// </summary>
        public GameClientSession(Stream stream, Func<int, BlockCipher> cipherFactory = null, Func<byte> randomByte = null)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _cipherFactory = cipherFactory;
            _randomByte = randomByte;
            _decoder.SecondLayer = _secondLayer;
        }

        /// <summary>启动包里解出的频道信息(收到启动包之前为空)。</summary>
        public ChannelInfo Channel { get; private set; }

        /// <summary>服务端在接受连接阶段拒绝了(发来 21 字节明文帧,之后会关连接)。</summary>
        public bool Rejected { get; private set; }

        /// <summary>对端已关闭连接(读到流结束)。</summary>
        public bool PeerClosed => _closed;

        /// <summary>会话密码已就绪(启动包已解出密钥块),可以发命令。</summary>
        public bool IsReady => _builder != null;

        /// <summary>第二层校验失败的次数(服务端到客户端方向;只记录,不拒收)。</summary>
        public int SecondLayerMismatches => _secondLayer.Mismatches;

        /// <summary>已发生的事件(脱敏)。</summary>
        public IReadOnlyList<SessionEvent> Events => _events;

        /// <summary>最近一次解码失败的状态(没有失败为 <see cref="FrameStatus.Ok"/>)。</summary>
        public FrameStatus LastDecodeStatus { get; private set; }

        /// <summary>最近一次切帧错误(没有错误为 <see cref="FrameError.None"/>)。</summary>
        public FrameError LastFrameError { get; private set; }

        /// <summary>
        /// 等启动包:读到第一个完整帧并解码;是启动包就解析频道信息、初始化会话密码与发送端。
        /// 超时返回 false(没有收到);收到拒绝帧或流结束也返回 false。
        /// </summary>
        public async Task<bool> ReceiveBootstrapAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                List<DecodedFrame> frames = await ReceiveAsync(timeout - watch.Elapsed, stopAfterFrames: 1, cancellationToken).ConfigureAwait(false);
                if (frames.Count > 0)
                {
                    return TryAcceptBootstrap(frames[0]);
                }
                if (_closed)
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 发一帧命令或通知(明文 <paramref name="payload"/>:按服务端要求在需要的包号后追加折叠哈希,补齐、加密与两层校验都由帧层做)。
        /// 序号每帧加一。返回帧层状态。
        /// </summary>
        public async Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken = default)
        {
            if (_builder == null) throw new InvalidOperationException("还没有收到启动包,会话密码没有初始化");
            FrameStatus status = _builder.TryBuild(packetClass, packetId, _sequence, payload, out byte[] frame);
            if (status != FrameStatus.Ok)
            {
                _events.Add(new SessionEvent(true, packetClass, packetId, 0, payload.Length, "编码失败:" + status));
                return status;
            }
            _sequence = (ushort)(_sequence + 1);
            await _stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _events.Add(new SessionEvent(true, packetClass, packetId, frame.Length, payload.Length, string.Empty));
            return FrameStatus.Ok;
        }

        /// <summary>
        /// 在 <paramref name="window"/> 时间内收帧并解码,返回解出的帧(顺序与到达一致)。
        /// 到点、流结束或解出 <paramref name="stopAfterFrames"/> 帧(大于 0 时)就返回;没读完的半个帧留给下一次调用。
        /// </summary>
        public async Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames = 0, CancellationToken cancellationToken = default)
        {
            List<DecodedFrame> result = new List<DecodedFrame>();
            Stopwatch watch = Stopwatch.StartNew();
            while (true)
            {
                Decode(result, stopAfterFrames);
                if (stopAfterFrames > 0 && result.Count >= stopAfterFrames) break;
                TimeSpan left = window - watch.Elapsed;
                if (_closed || left <= TimeSpan.Zero || LastFrameError != FrameError.None) break;
                int n = await ReadSomeAsync(left, cancellationToken).ConfigureAwait(false);
                if (n < 0) break;                    // 到点
                if (n == 0)
                {
                    _closed = true;
                    break;
                }
                FrameError error = _assembler.Feed(_chunk, 0, n, _feed);
                foreach (RawFrame cut in _feed)
                {
                    _queue.Enqueue(cut);
                }
                _feed.Clear();
                if (error != FrameError.None)
                {
                    LastFrameError = error;
                    _events.Add(new SessionEvent(false, PacketClass.Noti, 0, n, 0, "切帧失败:" + error));
                }
            }
            return result;
        }

        // 按需解码:收到启动包之前不解后面的帧(会话密码还没有),所以最多解 limit 个(0 = 不限)
        private void Decode(List<DecodedFrame> sink, int limit)
        {
            while (_queue.Count > 0 && (limit <= 0 || sink.Count < limit))
            {
                RawFrame raw = _queue.Dequeue();
                FrameStatus status = _decoder.Decode(raw, out DecodedFrame frame);
                int total = raw.Bytes.Length;
                if (status != FrameStatus.Ok)
                {
                    LastDecodeStatus = status;
                    _events.Add(new SessionEvent(false, raw.Header.Class, raw.Header.PacketId, total, 0, "解码失败:" + status));
                    continue;
                }
                string note = frame.Kind + (frame.WasCompressed ? ",压缩" : string.Empty)
                    + (frame.SecondLayerChecked ? (frame.SecondLayerOk ? ",第二层相符" : ",第二层不符") : string.Empty);
                _events.Add(new SessionEvent(false, frame.Header.Class, frame.Header.PacketId, total, frame.Payload.Length, note));
                sink.Add(frame);
            }
        }

        private bool TryAcceptBootstrap(DecodedFrame frame)
        {
            if (frame.Kind == DecodedKind.Rejected)
            {
                Rejected = true;
                return false;
            }
            if (frame.Kind != DecodedKind.Bootstrap) return false;
            if (ChannelInfo.TryParse(frame.Payload, 0, frame.Payload.Length, out ChannelInfo info) != ChannelInfoStatus.Ok) return false;
            Channel = info;
            SessionCipher cipher = _cipherFactory == null ? new SessionCipher(info.KeyBlock) : new SessionCipher(info.KeyBlock, _cipherFactory);
            _decoder.Cipher = cipher;
            _builder = ClientFrameBuilder.CreateForServer(cipher, _randomByte);
            return true;
        }

        // 返回读到的字节数;0 = 流结束;-1 = 到点没有数据。
        // 注意不能靠取消令牌去掐底层读:Unity 的 Mono 运行时里 NetworkStream.ReadAsync 不响应取消,
        // 服务端静默时这样写会无限期等下去(控制台运行时则正常)。到点改成逻辑超时:
        // 底层至多一笔在途读,超时先返回 -1,读留在下一轮消费它真正读到的字节。
        private Task<int> _pendingRead;

        private async Task<int> ReadSomeAsync(TimeSpan left, CancellationToken cancellationToken)
        {
            if (_pendingRead == null)
            {
                _pendingRead = _stream.ReadAsync(_chunk, 0, _chunk.Length, CancellationToken.None);
            }
            Task<int> read = _pendingRead;
            if (!read.IsCompleted)
            {
                Task winner = await Task.WhenAny(read, Task.Delay(left, cancellationToken)).ConfigureAwait(false);
                if (winner != read)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    return -1;
                }
            }
            _pendingRead = null;
            try
            {
                return await read.ConfigureAwait(false);
            }
            catch (IOException)
            {
                _closed = true;
                return 0;
            }
            catch (ObjectDisposedException)
            {
                _closed = true;
                return 0;
            }
        }
    }
}
