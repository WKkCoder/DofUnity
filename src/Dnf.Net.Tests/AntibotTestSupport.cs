using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 反机器人信封测试共用的合成数据与构造小工具。CRC32、字节序与包结构都在这里按规格手写一遍,不借用被测实现,
/// 所以被测代码里的同类错误不会被测试"同样地写错"而放过。数据全部是合成的(递增字节、简单线性同余的伪随机字节)。
/// </summary>
internal static class AntibotTestSupport
{
    /// <summary>字节在四个位置都不相同的序号(0x003C00DA),大小端读错一定会露馅。</summary>
    public const uint SeqA = 3932378u;

    /// <summary>四个字节互不相同的另一个序号。</summary>
    public const uint SeqB = 0xA1B2C3D4u;

    /// <summary>可复现的伪随机字节。</summary>
    public static byte[] Synth(int seed, int length)
    {
        byte[] data = new byte[length];
        uint state = unchecked((uint)seed * 2654435761u + 12345u);
        for (int i = 0; i < length; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    public static string Hex(byte[] data)
    {
        return Convert.ToHexString(data).ToLowerInvariant();
    }

    /// <summary>标准 CRC32(IEEE,反射,多项式 0xEDB88320),逐位计算。</summary>
    public static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>算法包:9 字节包头(第 9 个字节 = 其后数据长度)接数据。</summary>
    public static byte[] AlgorithmPacket(int dataLength)
    {
        byte[] packet = new byte[9 + dataLength];
        for (int i = 0; i < packet.Length; i++)
        {
            packet[i] = unchecked((byte)(0xA0 + i * 3));
        }

        packet[8] = (byte)dataLength;
        return packet;
    }

    /// <summary>内层包:类(1 字节)、包号(小端 2 字节)、总长(小端 4 字节,默认是实际长度)再接载荷。</summary>
    public static byte[] InnerPacket(byte packetClass, ushort packetId, int bodyLength, int seed, uint? declaredTotal = null)
    {
        byte[] inner = new byte[7 + bodyLength];
        inner[0] = packetClass;
        inner[1] = (byte)packetId;
        inner[2] = (byte)(packetId >> 8);
        WriteLittleEndian32(inner, 3, declaredTotal ?? (uint)inner.Length);
        Synth(seed, bodyLength).CopyTo(inner, 7);
        return inner;
    }

    public static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] all = new byte[first.Length + second.Length];
        first.CopyTo(all, 0);
        second.CopyTo(all, first.Length);
        return all;
    }

    public static void WriteLittleEndian32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    public static uint ReadBigEndian32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
    }

    public static void WriteBigEndian32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    /// <summary>把信封各字段摆出来,可以逐项改坏;<see cref="ToBytes"/> 按规格拼出载荷。</summary>
    public sealed class Spec
    {
        /// <summary>头里的序号,也是加密用的序号。</summary>
        public uint Seq = SeqA;

        /// <summary>头里的算法号字段(无符号 32 位)。</summary>
        public uint AlgoField = 19;

        /// <summary>加密实际用的算法号;<see cref="CipherArea"/> 为空时必须是 1 到 19。</summary>
        public int CipherAlgo = 19;

        /// <summary>标志字节(不被解释)。</summary>
        public byte Flag = 0x5A;

        /// <summary>明文 = 算法包 + 内层包。</summary>
        public byte[] Plain = Array.Empty<byte>();

        /// <summary>非空时直接当密文区,不对 <see cref="Plain"/> 加密。</summary>
        public byte[] CipherArea;

        /// <summary>空 = 对 <see cref="Plain"/> 算标准 CRC32;否则写进头里的值。</summary>
        public uint? Crc;

        /// <summary>空 = 17 + 密文区长度;否则写进头里的总长。</summary>
        public int? Total;

        /// <summary>头里的头长字段(合法值 17)。</summary>
        public int HeaderLength = 17;

        /// <summary>加在"其后字节数"字段上的偏移(0 = 如实)。</summary>
        public int PrefixDelta;

        /// <summary>密文区之后的尾巴。</summary>
        public byte[] Tail = Array.Empty<byte>();

        public byte[] ToBytes()
        {
            byte[] cipher = CipherArea ?? AntibotCipher.Encrypt(CipherAlgo, Seq, Plain);
            Assert.NotNull(cipher);
            uint crc = Crc ?? Crc32(Plain);
            int total = Total ?? 17 + cipher.Length;
            byte[] payload = new byte[21 + cipher.Length + Tail.Length];
            WriteBigEndian32(payload, 0, unchecked((uint)(payload.Length - 4 + PrefixDelta)));
            payload[4] = (byte)(total >> 8);
            payload[5] = (byte)total;
            payload[6] = (byte)(HeaderLength >> 8);
            payload[7] = (byte)HeaderLength;
            payload[8] = Flag;
            WriteBigEndian32(payload, 9, Seq);
            WriteBigEndian32(payload, 13, AlgoField);
            WriteBigEndian32(payload, 17, crc);
            cipher.CopyTo(payload, 21);
            Tail.CopyTo(payload, 21 + cipher.Length);
            return payload;
        }
    }

    /// <summary>按算法号、序号与明文拼一个合法信封。</summary>
    public static byte[] ValidEnvelope(int algo, uint seq, byte[] plain)
    {
        return new Spec { CipherAlgo = algo, AlgoField = (uint)algo, Seq = seq, Plain = plain }.ToBytes();
    }
}
