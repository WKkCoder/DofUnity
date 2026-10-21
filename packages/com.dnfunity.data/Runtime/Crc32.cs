using System;

// 证据:PVF 文件与目录树的 CRC-32 校验规则见 E-011。
namespace Dnf.Data
{
    /// <summary>
    /// 标准 CRC-32(多项式 0xEDB88320,反射,初值与结果各取反),与 zlib 的 <c>crc32(data, seed)</c> 逐位一致。
    /// <c>seed</c> 是"此前已算出的 CRC",0 表示从头算,所以 <c>Compute(b, Compute(a)) == Compute(a 接 b)</c>。
    /// PVF 的完整性校验用它:状态初值取反的种子,逐字节查表更新,结果再取反。
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1u) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0u)
        {
            uint[] table = Table;
            uint crc = ~seed;
            for (int i = 0; i < data.Length; i++)
            {
                crc = (crc >> 8) ^ table[(crc ^ data[i]) & 0xFFu];
            }
            return ~crc;
        }
    }
}
