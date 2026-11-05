using System;
using System.Collections.Generic;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 客户端到服务端的整帧编码(13 字节头:类、包号、整帧长度、校验字、序号)。
    /// 步骤:载荷(调用方需要时先用 <see cref="PacketIntegrity.AppendFoldHash"/> 追加折叠哈希)→ 会话加密(补齐到块长)→
    /// 校验字低字节 = 对 [头里从 <see cref="FrameLayout.ChecksumStart"/> 起的字节 + 补齐后的明文] 算 crc32N 取低 8 位 →
    /// 校验字高 3 字节 = 包完整性第二层(有发送方对象时),否则为 0。
    /// 补齐字节由调用方指定(服务端把它们当作载荷的一部分算校验,取值不影响是否被接受);包号在 <see cref="PlainPacketIds"/> 里的帧不加密、不补齐。
    /// 序号由调用方管理。服务端对包号 0x25C 的帧既不解密也不检查校验字节(载荷交给外部的反作弊库),本类不替它做任何事。
    /// </summary>
    public sealed class ClientFrameBuilder
    {
        /// <summary>服务端收包时不解密、不校验的包号(只有这一个)。</summary>
        public const int EnvelopePacketId = 0x25C;

        private readonly IPayloadCipher _cipher;
        private readonly SecondLayerSender _secondLayer;
        private readonly FrameLayout _layout;

        /// <summary>不走会话密码的包号集合,默认只含 <see cref="EnvelopePacketId"/>。</summary>
        public HashSet<int> PlainPacketIds { get; } = new HashSet<int> { EnvelopePacketId };

        /// <summary>
        /// 为真时,对 <see cref="PacketIntegrity.RequiresClientFoldHash"/> 为真的包自动在载荷之后追加折叠哈希(载荷长 1 到 400 时),
        /// 默认关闭(载荷原样);接入真实服务端时应打开,否则这 17 个包号会累加服务端的可疑事件计数。
        /// </summary>
        public bool AppendRequiredFoldHash { get; set; }

        /// <summary>
        /// 创建编码器。<paramref name="secondLayer"/> 为 null 时校验字高 3 字节写 0;<paramref name="layout"/> 默认客户端到服务端的 13 字节头。
        /// </summary>
        public ClientFrameBuilder(IPayloadCipher cipher, SecondLayerSender secondLayer = null, FrameLayout layout = null)
        {
            _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
            _secondLayer = secondLayer;
            _layout = layout ?? FrameLayout.ClientToServer;
        }

        /// <summary>
        /// 连真实服务端时的推荐配置:校验字高 3 字节写第二层(服务端收包钩子 0x08274f73 不符时不拒收,但会为每个新的类与包号累积可疑事件),
        /// 需要折叠哈希的 17 个包号自动追加折叠哈希。<paramref name="randomByte"/> 为 null 时用系统随机数。
        /// </summary>
        public static ClientFrameBuilder CreateForServer(IPayloadCipher cipher, Func<byte> randomByte = null)
        {
            SecondLayerSender sender = randomByte == null ? new SecondLayerSender() : new SecondLayerSender(randomByte);
            return new ClientFrameBuilder(cipher, sender) { AppendRequiredFoldHash = true };
        }

        /// <summary>使用的布局。</summary>
        public FrameLayout Layout => _layout;

        /// <summary>
        /// 对 [头里从 ChecksumStart 起的字节 + 补齐后的明文] 算校验字节。头里校验字、序号等字段必须已写好(校验字本身在范围之前,内容无关)。
        /// </summary>
        public static byte ComputeCheckByte(FrameLayout layout, ReadOnlySpan<byte> header, ReadOnlySpan<byte> paddedPlain)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            int headPart = layout.HeaderLength - layout.ChecksumStart;
            byte[] buffer = new byte[headPart + paddedPlain.Length];
            header.Slice(layout.ChecksumStart, headPart).CopyTo(buffer);
            paddedPlain.CopyTo(new Span<byte>(buffer, headPart, paddedPlain.Length));
            return ChecksumByte.MakeChecksumTo1Byte2(Crc32N.Compute(buffer));
        }

        /// <summary>
        /// 编码一帧。<paramref name="payload"/> 是要发的明文(含需要的折叠哈希);<paramref name="padByte"/> 是补齐字节的取值。
        /// 失败时 <paramref name="frame"/> 为 null。
        /// </summary>
        public FrameStatus TryBuild(PacketClass packetClass, ushort packetId, ushort sequence, byte[] payload, out byte[] frame, byte padByte = 0)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            frame = null;
            if (AppendRequiredFoldHash && PacketIntegrity.RequiresClientFoldHash(packetClass, packetId))
                payload = PacketIntegrity.AppendFoldHash(payload);
            byte[] cipherText;
            byte[] paddedPlain;
            if (PlainPacketIds.Contains(packetId))
            {
                cipherText = payload;
                paddedPlain = payload;
            }
            else
            {
                int padded = _cipher.PaddedLength(packetId, payload.Length);
                if (padded < payload.Length) return FrameStatus.EncryptFailed;
                paddedPlain = new byte[padded];
                Buffer.BlockCopy(payload, 0, paddedPlain, 0, payload.Length);
                for (int i = payload.Length; i < padded; i++) paddedPlain[i] = padByte;
                cipherText = _cipher.Encrypt(packetId, paddedPlain);
                if (cipherText == null || cipherText.Length != padded) return FrameStatus.EncryptFailed;
            }

            long total = (long)_layout.HeaderLength + cipherText.Length;
            if (total > _layout.MaxFrameLength) return FrameStatus.FrameTooLarge;

            byte[] result = new byte[total];
            PacketHeader header = new PacketHeader
            {
                Class = packetClass,
                PacketId = packetId,
                TotalLength = (uint)total,
                Check = 0,
                Second = sequence,
            };
            header.TryWrite(_layout, result);
            Buffer.BlockCopy(cipherText, 0, result, _layout.CipherOffset, cipherText.Length);

            byte low = ComputeCheckByte(_layout, result, paddedPlain);
            uint word = low;
            if (_secondLayer != null)
            {
                bool reset = PacketIntegrity.ResetsStateClientToServer(packetClass, packetId);
                word = _secondLayer.Seal(new ReadOnlySpan<byte>(result, _layout.CipherOffset, cipherText.Length), low, reset);
            }
            header.Check = word;
            header.TryWrite(_layout, result);
            frame = result;
            return FrameStatus.Ok;
        }
    }
}
