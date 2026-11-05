using System;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 服务端的帧校验函数 <c>Encryption::crc32N</c>(0x0848d4c8,E-203):自定义多项式的 crc32(<c>make_crc_table</c> 0x0848d3e4 用常量 0x4DB89129 生成表,
    /// <c>crc32</c> 0x0848d472 初值与终值都取反),再把结果最低字节换成"四个字节异或再异或 0x18"、高 3 字节保持。
    /// 它不是标准 zlib crc32;金标准向量由本机模拟器跑原函数得到。
    /// </summary>
    public static class Crc32N
    {
        /// <summary>生成表用的多项式常量(0x0848d3e4)。</summary>
        public const uint Polynomial = 0x4DB89129u;

        /// <summary>折叠时异或进最低字节的常量。</summary>
        public const byte FoldConstant = 0x18;

        private static readonly uint[] s_table = BuildTable();

        // 逐句照原函数的循环:表项 0 为 0;crc 从 1 起,i 从 0x80 折半,每步 crc = (crc 最低位为 1 ? 多项式 : 0) ^ (crc >> 1),
        // 再把 crc 异或进所有"下标含 i 位"的表项。这不是标准的"256 次移位",所以不能换成常见的逐项生成写法。
        private static uint[] BuildTable()
        {
            uint[] t = new uint[256];
            uint crc = 1;
            for (int i = 0x80; i != 0; i >>= 1)
            {
                crc = ((crc & 1) != 0 ? Polynomial : 0u) ^ (crc >> 1);
                for (int j = 0; j < 256; j += 2 * i)
                {
                    t[i + j] = t[j] ^ crc;
                }
            }
            return t;
        }

        /// <summary>表项(0 到 255)。</summary>
        public static uint TableEntry(int index)
        {
            if ((uint)index > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return s_table[index];
        }

        /// <summary>不折叠的 crc32(<c>Encryption::crc32</c>):初值取反、逐字节查表、终值取反。</summary>
        public static uint Raw(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                c = s_table[(data[i] ^ c) & 0xFF] ^ (c >> 8);
            }
            return ~c;
        }

        /// <summary>把 crc 的最低字节替换为四个字节的异或再异或 0x18(高 3 字节不动)。</summary>
        public static uint Fold(uint crc)
        {
            uint folded = (crc & 0xFF) ^ ((crc >> 8) & 0xFF) ^ ((crc >> 16) & 0xFF) ^ (crc >> 24) ^ FoldConstant;
            return (crc & 0xFFFFFF00u) | (folded & 0xFF);
        }

        /// <summary><c>Encryption::crc32N</c>:<see cref="Fold"/>(<see cref="Raw"/>(data))。</summary>
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            return Fold(Raw(data));
        }

        /// <summary>对数组的一段计算;越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public static uint Compute(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return Compute(new ReadOnlySpan<byte>(data, offset, count));
        }
    }

    /// <summary>
    /// <c>_NS_PI_2ND_MakeChecksumTo1Byte2</c>(0x0808cc4b):把校验值截成最低一个字节(原函数对两个引用参数各做一次 <c>&amp; 0xff</c>)。
    /// 服务端收包时拿它比较"头里的校验字"与"重算的 crc32N"的低字节(E-203)。
    /// </summary>
    public static class ChecksumByte
    {
        /// <summary>取低 8 位。</summary>
        public static byte MakeChecksumTo1Byte2(uint value)
        {
            return (byte)(value & 0xFF);
        }

        /// <summary>对一段数据先算 <see cref="Crc32N"/> 再取低 8 位。</summary>
        public static byte Of(ReadOnlySpan<byte> data)
        {
            return MakeChecksumTo1Byte2(Crc32N.Compute(data));
        }
    }
}
