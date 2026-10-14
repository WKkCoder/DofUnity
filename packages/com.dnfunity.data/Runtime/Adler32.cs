using System;

namespace Dnf.Data
{
    /// <summary>Adler-32(RFC 1950),用来校验 zlib 流尾部的校验和。</summary>
    public static class Adler32
    {
        private const uint Mod = 65521;
        private const int NMax = 5552; // 不溢出 32 位的最大连续字节数

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            int pos = 0;
            while (pos < data.Length)
            {
                int n = Math.Min(NMax, data.Length - pos);
                for (int i = 0; i < n; i++)
                {
                    a += data[pos + i];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
                pos += n;
            }
            return (b << 16) | a;
        }
    }
}
