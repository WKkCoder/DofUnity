using System;

namespace Dnf.Net.Session
{
    /// <summary><see cref="AntibotEnvelope.TryUnwrap(byte[], out byte[], out AntibotEnvelopeStatus)"/> 的结果。</summary>
    public enum AntibotEnvelopeStatus
    {
        /// <summary>解包成功。</summary>
        Ok = 0,

        /// <summary>载荷为空引用。</summary>
        NullPayload,

        /// <summary>载荷不足 21 字节(固定头长度)。</summary>
        TooShort,

        /// <summary>开头的 4 字节长度与其后的实际字节数不符。</summary>
        LengthPrefixMismatch,

        /// <summary>头长字段不是 17。</summary>
        BadHeaderLength,

        /// <summary>总长字段小于 17(密文长度为负)。</summary>
        TotalOutOfRange,

        /// <summary>载荷比"固定头 + 密文长度"短。</summary>
        Truncated,

        /// <summary>算法号不在 1 到 19。</summary>
        BadAlgorithm,

        /// <summary>解密没有给出结果(算法号合法时不会出现,留作防御)。</summary>
        CipherFailed,

        /// <summary>解密后密文区的 CRC32 与头里的校验值不符。</summary>
        CrcMismatch,

        /// <summary>解密后的明文装不下算法包头(9 字节),或算法包长度(9 + 第 9 个字节)超出明文。</summary>
        BadAlgorithmPacket,

        /// <summary>算法包之后剩下的内层包不足 7 字节,读不出类、包号与总长。</summary>
        InnerTooShort,

        /// <summary>内层包自带的总长与剩余字节数不符。</summary>
        InnerLengthMismatch,
    }

    /// <summary>
    /// 561 号信封(类 0、包号 <see cref="PacketId"/>)的拆包:把外部反作弊库封进信封的内层完整包还原出来。
    /// <para>
    /// 载荷结构(全部大端,偏移自载荷第一个字节):
    /// <c>[0..4)</c> u32 = 其后的字节数(必须与实际相符);<c>[4..6)</c> u16 <c>total</c> = 17 + 密文长度;<c>[6..8)</c> u16 头长,必须是 17;
    /// <c>[8]</c> 标志字节(不解释);<c>[9..13)</c> u32 <c>seq</c>;<c>[13..17)</c> u32 <c>algo</c>(1 到 19);<c>[17..21)</c> u32 CRC32(IEEE,与 zlib 的 crc32 同);
    /// 其后 <c>total - 17</c> 字节密文区;密文区之后允许有一个可选的 4 字节尾巴,忽略不看(载荷短于"21 + 密文长度"则拒绝)。
    /// </para>
    /// <para>
    /// 密文区按 <see cref="AntibotCipher"/> 用 <c>algo</c> 与 <c>seq</c> 解密,CRC32 是对**解密后**的整个密文区算的。明文 = 算法包 + 内层包:
    /// 算法包长度 = 9 + 明文第 9 个字节(下标 8);其后全部是内层完整包,开头是 <c>u8 类, u16 包号(小端), u32 总长(小端)</c>,总长应等于内层包的实际长度。
    /// </para>
    /// <para>
    /// 约定(与目录里其它解析器同风格):不抛异常,任何不符(长度、头长、算法号、CRC、内层自洽)一律返回 false 且 <c>innerPacket</c> 为 null;
    /// 返回的是新数组,不改动传入的载荷。
    /// </para>
    /// </summary>
    public static class AntibotEnvelope
    {
        /// <summary>信封的包号(0x231)。</summary>
        public const int PacketId = 561;

        /// <summary>固定头长度:4 字节长度 + 17 字节头(含 4 字节校验)。</summary>
        public const int FixedLength = 21;

        /// <summary>头里的"头长"字段必须是这个值。</summary>
        public const int HeaderLength = 17;

        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>拆包;成功返回 true 并给出内层完整包(含它自己的类、包号、总长),失败返回 false 且 <paramref name="innerPacket"/> 为 null。</summary>
        public static bool TryUnwrap(byte[] payload, out byte[] innerPacket)
        {
            return TryUnwrap(payload, out innerPacket, out _);
        }

        /// <summary>拆包并给出失败原因(成功为 <see cref="AntibotEnvelopeStatus.Ok"/>)。</summary>
        public static bool TryUnwrap(byte[] payload, out byte[] innerPacket, out AntibotEnvelopeStatus status)
        {
            innerPacket = null;
            if (payload == null)
            {
                status = AntibotEnvelopeStatus.NullPayload;
                return false;
            }

            if (payload.Length < FixedLength)
            {
                status = AntibotEnvelopeStatus.TooShort;
                return false;
            }

            if (ReadUInt32(payload, 0) != (uint)(payload.Length - 4))
            {
                status = AntibotEnvelopeStatus.LengthPrefixMismatch;
                return false;
            }

            int total = (payload[4] << 8) | payload[5];
            int headerLength = (payload[6] << 8) | payload[7];
            if (headerLength != HeaderLength)
            {
                status = AntibotEnvelopeStatus.BadHeaderLength;
                return false;
            }

            if (total < HeaderLength)
            {
                status = AntibotEnvelopeStatus.TotalOutOfRange;
                return false;
            }

            int cipherLength = total - HeaderLength;
            if (payload.Length < FixedLength + cipherLength)
            {
                status = AntibotEnvelopeStatus.Truncated;
                return false;
            }

            uint seq = ReadUInt32(payload, 9);
            uint algoValue = ReadUInt32(payload, 13);
            uint crc = ReadUInt32(payload, 17);
            if (algoValue < AntibotCipher.MinAlgorithm || algoValue > AntibotCipher.MaxAlgorithm)
            {
                status = AntibotEnvelopeStatus.BadAlgorithm;
                return false;
            }

            byte[] cipherArea = new byte[cipherLength];
            Buffer.BlockCopy(payload, FixedLength, cipherArea, 0, cipherLength);
            if (!AntibotCipher.TryDecrypt((int)algoValue, seq, cipherArea, out byte[] plain))
            {
                status = AntibotEnvelopeStatus.CipherFailed;
                return false;
            }

            if (Crc32(plain) != crc)
            {
                status = AntibotEnvelopeStatus.CrcMismatch;
                return false;
            }

            if (plain.Length < 9)
            {
                status = AntibotEnvelopeStatus.BadAlgorithmPacket;
                return false;
            }

            int algorithmPacketLength = 9 + plain[8];
            if (algorithmPacketLength > plain.Length)
            {
                status = AntibotEnvelopeStatus.BadAlgorithmPacket;
                return false;
            }

            int innerLength = plain.Length - algorithmPacketLength;
            if (innerLength < 7)
            {
                status = AntibotEnvelopeStatus.InnerTooShort;
                return false;
            }

            uint declared = (uint)plain[algorithmPacketLength + 3]
                | ((uint)plain[algorithmPacketLength + 4] << 8)
                | ((uint)plain[algorithmPacketLength + 5] << 16)
                | ((uint)plain[algorithmPacketLength + 6] << 24);
            if (declared != (uint)innerLength)
            {
                status = AntibotEnvelopeStatus.InnerLengthMismatch;
                return false;
            }

            byte[] inner = new byte[innerLength];
            Buffer.BlockCopy(plain, algorithmPacketLength, inner, 0, innerLength);
            innerPacket = inner;
            status = AntibotEnvelopeStatus.Ok;
            return true;
        }

        /// <summary>标准 CRC32(IEEE 802.3,多项式 0xEDB88320 的反射实现,初值与结果各取反),与 zlib 的 crc32 相同。</summary>
        internal static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
