using System;
using System.Collections.Generic;

namespace Dnf.Net.Frame
{
    /// <summary>解码出来的帧属于哪一种。</summary>
    public enum DecodedKind
    {
        /// <summary>普通帧:会话解密(必要时再解压)。</summary>
        Session = 0,

        /// <summary>启动包(类 0、包号 1):固定种子变换,里面是会话密钥块。</summary>
        Bootstrap,

        /// <summary>不加密的帧(<see cref="ServerFrameDecoder.PlainNotiIds"/> 里的包号):载荷原样。</summary>
        Plain,

        /// <summary>服务端在接受连接阶段拒绝时发的 21 字节明文帧(类 1、包号 1),发完即关连接。</summary>
        Rejected,
    }

    /// <summary>一个服务端到客户端的帧解码后的结果。</summary>
    public sealed class DecodedFrame
    {
        internal DecodedFrame(PacketHeader header, DecodedKind kind, byte[] payload, bool wasCompressed, bool secondLayerChecked, bool secondLayerOk)
        {
            Header = header;
            Kind = kind;
            Payload = payload;
            WasCompressed = wasCompressed;
            SecondLayerChecked = secondLayerChecked;
            SecondLayerOk = secondLayerOk;
        }

        /// <summary>帧头。</summary>
        public PacketHeader Header { get; }

        /// <summary>帧的种类。</summary>
        public DecodedKind Kind { get; }

        /// <summary>
        /// 载荷:解密(必要时解压)后的字节,从第一个字段起。会话加密补的 0x00 仍在末尾,载荷长度不在帧里,
        /// 要靠按字段顺序读来确定(见 <see cref="PacketReader"/>)。
        /// </summary>
        public byte[] Payload { get; }

        /// <summary>是否经过了 zlib 解压。</summary>
        public bool WasCompressed { get; }

        /// <summary>是否做了第二层完整性检查。</summary>
        public bool SecondLayerChecked { get; }

        /// <summary>第二层检查的结果(没检查时为 true)。</summary>
        public bool SecondLayerOk { get; }
    }

    /// <summary>
    /// 服务端到客户端的整帧解码(15 字节头:类、包号、整帧长度、第一校验、第二校验)。
    /// 顺序:检查整帧长度 → 检查校验(两个校验值都是对帧头之后的字节算 crc32N,第二个的高 3 字节是包完整性第二层,只比低字节)→
    /// 按类与包号选还原方式(启动包用固定种子变换;<see cref="PlainNotiIds"/> 里的包原样;其余走会话解密)→
    /// 包号为 2、9、0x57 的帧再做 zlib 解压(与类无关)。
    /// 例外:类 1、包号 1、整帧恰好 21 字节的是拒绝帧(明文,两个校验值相等,没有第二层),不解密。
    /// </summary>
    public sealed class ServerFrameDecoder
    {
        /// <summary>拒绝帧的整帧长度。</summary>
        public const int RejectFrameLength = 21;

        private static readonly int[] s_compressedIds = { 2, 9, 0x57 };

        private readonly FrameLayout _layout;

        /// <summary>不走会话加密的类 0 包号,默认含 0x231(外部反作弊库的封包)。</summary>
        public HashSet<int> PlainNotiIds { get; } = new HashSet<int> { 0x231 };

        /// <summary>会话密码;收到启动包、取出密钥块之前为 null,此时普通帧返回 <see cref="FrameStatus.NoCipher"/>。</summary>
        public IPayloadCipher Cipher { get; set; }

        /// <summary>是否检查两个校验值,默认是。</summary>
        public bool VerifyChecksums { get; set; } = true;

        /// <summary>第二层完整性的接收方;为 null 时不检查。不符只记录在结果里,不拒收。</summary>
        public SecondLayerReceiver SecondLayer { get; set; }

        /// <summary>解压后的载荷上限,默认单包缓冲 95000 字节。</summary>
        public int MaxInflatedLength { get; set; } = FrameLayout.PacketBufferSize;

        /// <summary>创建解码器;布局默认服务端到客户端的 15 字节头。</summary>
        public ServerFrameDecoder(FrameLayout layout = null)
        {
            _layout = layout ?? FrameLayout.ServerToClient;
        }

        /// <summary>使用的布局。</summary>
        public FrameLayout Layout => _layout;

        /// <summary>某个包号是否是压缩包号。</summary>
        public static bool IsCompressedId(int packetId)
        {
            return Array.IndexOf(s_compressedIds, packetId) >= 0;
        }

        /// <summary>解码 <see cref="FrameAssembler"/> 切出来的一帧。</summary>
        public FrameStatus Decode(RawFrame raw, out DecodedFrame decoded)
        {
            return Decode(raw.Bytes, out decoded);
        }

        /// <summary>解码一个完整的帧(字节数必须等于头里的整帧长度)。失败时 <paramref name="decoded"/> 为 null。</summary>
        public FrameStatus Decode(byte[] frame, out DecodedFrame decoded)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            decoded = null;
            if (!PacketHeader.TryRead(_layout, frame, out PacketHeader header)) return FrameStatus.TruncatedHeader;
            if (header.TotalLength != (uint)frame.Length) return FrameStatus.LengthMismatch;

            int bodyLength = frame.Length - _layout.CipherOffset;
            byte[] body = new byte[bodyLength];
            Buffer.BlockCopy(frame, _layout.CipherOffset, body, 0, bodyLength);
            int id = header.PacketId;

            if (header.Class == PacketClass.Cmd && id == 1 && frame.Length == RejectFrameLength)
            {
                if (VerifyChecksums && (header.Check != Crc32N.Compute(body) || header.Second != header.Check))
                    return FrameStatus.ChecksumMismatch;
                decoded = new DecodedFrame(header, DecodedKind.Rejected, body, false, false, true);
                return FrameStatus.Ok;
            }

            DecodedKind kind;
            if (header.Class == PacketClass.Noti && id == 1) kind = DecodedKind.Bootstrap;
            else if (header.Class == PacketClass.Noti && PlainNotiIds.Contains(id)) kind = DecodedKind.Plain;
            else kind = DecodedKind.Session;

            bool compressed = IsCompressedId(id);

            if (VerifyChecksums && !(kind == DecodedKind.Plain && compressed))
            {
                uint crc = Crc32N.Compute(body);
                if (header.Check != crc || (header.Second & 0xFF) != (crc & 0xFF)) return FrameStatus.ChecksumMismatch;
            }

            bool secondChecked = false;
            bool secondOk = true;
            if (SecondLayer != null)
            {
                secondChecked = true;
                secondOk = SecondLayer.Check(body, header.Second, PacketIntegrity.ResetsStateServerToClient(header.Class, id));
            }

            byte[] payload;
            switch (kind)
            {
                case DecodedKind.Bootstrap:
                    payload = BootstrapTransform.Decode(body);
                    break;
                case DecodedKind.Plain:
                    payload = body;
                    break;
                default:
                    if (Cipher == null) return FrameStatus.NoCipher;
                    payload = Cipher.Decrypt(id, body);
                    if (payload == null) return FrameStatus.CipherFailed;
                    break;
            }

            if (compressed)
            {
                if (!ZlibInflate.TryInflate(payload, MaxInflatedLength, out byte[] inflated, out bool tooLarge))
                    return tooLarge ? FrameStatus.PayloadTooLarge : FrameStatus.InflateFailed;
                payload = inflated;
            }

            decoded = new DecodedFrame(header, kind, payload, compressed, secondChecked, secondOk);
            return FrameStatus.Ok;
        }
    }
}
