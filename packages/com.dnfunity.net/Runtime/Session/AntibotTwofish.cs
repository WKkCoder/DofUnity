using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 7/8/11:Twofish(原 TenTwofish32/24/16,取派生密钥的前 32/24/16 字节,即 256/192/128 位密钥),ECB,块长 16 字节;
    /// 整块之后剩下的 <c>len % 16</c> 个字节走 SEATTOS 流变换(与 RC6 等 16 字节块算法共用 <see cref="AntibotRc6.ApplySeattosSuffix"/>)。
    /// <para>
    /// 与公开 Twofish(16 轮、q 置换、MDS 与 RS 矩阵、g 函数四张密钥相关的 S 表、PHT、轮子密钥、输入输出白化)逐字相同,
    /// 只有一处偏差:原库把密钥字节装成 32 位字时,每个字先按小端取、再把每个 16 位半内的两个字节对调(第 0、1 字节互换,第 2、3 字节互换)。
    /// 之后一切(RS 编码、Me/Mo 拆分、轮子密钥、S 表)都用这些对调后的字。块字本身仍按小端装配。
    /// </para>
    /// <para>
    /// 密钥长度的选法按公开规格:k = 密钥位数 / 64(256 位为 4、192 位为 3、128 位为 2)个 64 位密钥字,
    /// h 函数里"异或密钥字再过 q 置换"的级数就是 k 级——级数少于 4 时是<b>跳过开头的级</b>(取 q 置换计划表的第 4-k 到第 3 列,最后一级仍是第 4 列),
    /// 而不是补零凑满 4 级。k=4 时与旧写法逐位相同。
    /// </para>
    /// </summary>
    internal static class AntibotTwofish
    {
        internal static void Transform(byte[] key32, int keyBytes, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotTwofishCore ctx = AntibotTwofishCore.Schedule(key32, keyBytes);
            int off = 0;
            int aligned = buf.Length & ~0xF;
            while (off < aligned)
            {
                if (encrypt)
                {
                    ctx.EncryptBlock(buf, off);
                }
                else
                {
                    ctx.DecryptBlock(buf, off);
                }

                off += 16;
            }

            AntibotRc6.ApplySeattosSuffix(key32, encrypt, buf);
        }
    }

    /// <summary>Twofish 的密钥扩展与单块加解密(见 <see cref="AntibotTwofish"/>)。</summary>
    internal sealed class AntibotTwofishCore
    {
        private const int Rounds = 16;
        private const int MdsPoly = 0x169;
        private const int RsPoly = 0x14D;

        // q0、q1 置换的 4 位小表(公开规格附录 A 的 t0..t3),BuildQ 把它们按规格拼成 256 项的置换表。
        private static readonly int[][] Q0Tables =
        {
            new[] { 0x8, 0x1, 0x7, 0xD, 0x6, 0xF, 0x3, 0x2, 0x0, 0xB, 0x5, 0x9, 0xE, 0xC, 0xA, 0x4 },
            new[] { 0xE, 0xC, 0xB, 0x8, 0x1, 0x2, 0x3, 0x5, 0xF, 0x4, 0xA, 0x6, 0x7, 0x0, 0x9, 0xD },
            new[] { 0xB, 0xA, 0x5, 0xE, 0x6, 0xD, 0x9, 0x0, 0xC, 0x8, 0xF, 0x3, 0x2, 0x4, 0x7, 0x1 },
            new[] { 0xD, 0x7, 0xF, 0x4, 0x1, 0x2, 0x6, 0xE, 0x9, 0xB, 0x3, 0x0, 0x8, 0x5, 0xC, 0xA },
        };

        private static readonly int[][] Q1Tables =
        {
            new[] { 0x2, 0x8, 0xB, 0xD, 0xF, 0x7, 0x6, 0xE, 0x3, 0x1, 0x9, 0x4, 0x0, 0xA, 0xC, 0x5 },
            new[] { 0x1, 0xE, 0x2, 0xB, 0x4, 0xC, 0x3, 0x7, 0x6, 0xD, 0xA, 0x5, 0xF, 0x9, 0x0, 0x8 },
            new[] { 0x4, 0xC, 0x7, 0x5, 0x1, 0x6, 0x9, 0xA, 0x0, 0xE, 0xD, 0x8, 0x2, 0xB, 0x3, 0xF },
            new[] { 0xB, 0x9, 0x5, 0x1, 0xC, 0x3, 0xD, 0xE, 0x6, 0x4, 0x7, 0xF, 0x2, 0x0, 0x8, 0xA },
        };

        private static readonly int[][] Mds =
        {
            new[] { 0x01, 0xEF, 0x5B, 0x5B },
            new[] { 0x5B, 0xEF, 0xEF, 0x01 },
            new[] { 0xEF, 0x5B, 0x01, 0xEF },
            new[] { 0xEF, 0x01, 0xEF, 0x5B },
        };

        private static readonly int[][] Rs =
        {
            new[] { 0x01, 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E },
            new[] { 0xA4, 0x56, 0x82, 0xF3, 0x1E, 0xC6, 0x68, 0xE5 },
            new[] { 0x02, 0xA1, 0xFC, 0xC1, 0x47, 0xAE, 0x3D, 0x19 },
            new[] { 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E, 0x03 },
        };

        /// <summary>
        /// h 函数每个字节通道(0 到 3)在 5 个位置各用 q0(0)还是 q1(1):列 0 到 3 是异或密钥字之前的 q 置换,列 4 是最后一级。
        /// 密钥字只有 k 个时,用的是第 4-k 到第 3 列(跳过开头的列),不是第 0 到 k-1 列。
        /// </summary>
        private static readonly int[][] QPlan =
        {
            new[] { 1, 1, 0, 0, 1 },
            new[] { 0, 1, 1, 0, 0 },
            new[] { 0, 0, 0, 1, 1 },
            new[] { 1, 0, 1, 1, 0 },
        };

        private static readonly byte[][] Q = { BuildQ(Q0Tables), BuildQ(Q1Tables) };

        private readonly uint[] _subKeys = new uint[8 + 2 * Rounds];
        private readonly uint[][] _sbox = { new uint[256], new uint[256], new uint[256], new uint[256] };

        internal static AntibotTwofishCore Schedule(byte[] key, int keyBytes)
        {
            AntibotTwofishCore ctx = new AntibotTwofishCore();
            ctx.Init(key, keyBytes);
            return ctx;
        }

        private void Init(byte[] key, int keyBytes)
        {
            int k = keyBytes / 8;
            uint[] words = new uint[2 * k];
            for (int i = 0; i < words.Length; i++)
            {
                words[i] = SwapHalfBytes(LoadLe(key, i * 4));
            }

            // Me、Mo:偶数位与奇数位的密钥字;S 向量按规格排成 (S_{k-1}, ..., S_0),h 的链从最后一项开始用。
            uint[] me = new uint[k];
            uint[] mo = new uint[k];
            uint[] sVector = new uint[k];
            for (int i = 0; i < k; i++)
            {
                me[i] = words[2 * i];
                mo[i] = words[2 * i + 1];
                sVector[k - 1 - i] = RsMdsEncode(words[2 * i], words[2 * i + 1]);
            }

            for (int i = 0; i < 8 + 2 * Rounds; i += 2)
            {
                uint rho = 0x01010101u;
                uint a = H((uint)i * rho, me, k);
                uint b = RotL(H((uint)(i + 1) * rho, mo, k), 8);
                _subKeys[i] = a + b;
                _subKeys[i + 1] = RotL(a + 2u * b, 9);
            }

            for (int lane = 0; lane < 4; lane++)
            {
                for (int x = 0; x < 256; x++)
                {
                    _sbox[lane][x] = MdsColumn(lane, ChainByte(lane, x, sVector, k));
                }
            }
        }

        internal void EncryptBlock(byte[] buf, int off)
        {
            uint r0 = LoadLe(buf, off) ^ _subKeys[0];
            uint r1 = LoadLe(buf, off + 4) ^ _subKeys[1];
            uint r2 = LoadLe(buf, off + 8) ^ _subKeys[2];
            uint r3 = LoadLe(buf, off + 12) ^ _subKeys[3];
            for (int r = 0; r < Rounds; r++)
            {
                uint t0 = G(r0);
                uint t1 = G(RotL(r1, 8));
                uint f0 = t0 + t1 + _subKeys[2 * r + 8];
                uint f1 = t0 + 2u * t1 + _subKeys[2 * r + 9];
                uint n0 = RotR(r2 ^ f0, 1);
                uint n1 = RotL(r3, 1) ^ f1;
                r2 = r0;
                r3 = r1;
                r0 = n0;
                r1 = n1;
            }

            StoreLe(buf, off, r2 ^ _subKeys[4]);
            StoreLe(buf, off + 4, r3 ^ _subKeys[5]);
            StoreLe(buf, off + 8, r0 ^ _subKeys[6]);
            StoreLe(buf, off + 12, r1 ^ _subKeys[7]);
        }

        internal void DecryptBlock(byte[] buf, int off)
        {
            uint r2 = LoadLe(buf, off) ^ _subKeys[4];
            uint r3 = LoadLe(buf, off + 4) ^ _subKeys[5];
            uint r0 = LoadLe(buf, off + 8) ^ _subKeys[6];
            uint r1 = LoadLe(buf, off + 12) ^ _subKeys[7];
            for (int r = Rounds - 1; r >= 0; r--)
            {
                uint t0 = G(r2);
                uint t1 = G(RotL(r3, 8));
                uint f0 = t0 + t1 + _subKeys[2 * r + 8];
                uint f1 = t0 + 2u * t1 + _subKeys[2 * r + 9];
                uint p2 = RotL(r0, 1) ^ f0;
                uint p3 = RotR(r1 ^ f1, 1);
                r0 = r2;
                r1 = r3;
                r2 = p2;
                r3 = p3;
            }

            StoreLe(buf, off, r0 ^ _subKeys[0]);
            StoreLe(buf, off + 4, r1 ^ _subKeys[1]);
            StoreLe(buf, off + 8, r2 ^ _subKeys[2]);
            StoreLe(buf, off + 12, r3 ^ _subKeys[3]);
        }

        private uint G(uint x)
        {
            return _sbox[0][x & 0xFF] ^ _sbox[1][(x >> 8) & 0xFF] ^ _sbox[2][(x >> 16) & 0xFF] ^ _sbox[3][x >> 24];
        }

        /// <summary>规格里的 h(x, L):四个字节通道各过一条 q 链,再乘 MDS 矩阵合成一个字;<paramref name="list"/> 共 <paramref name="k"/> 个字。</summary>
        private static uint H(uint x, uint[] list, int k)
        {
            uint z = 0;
            for (int lane = 0; lane < 4; lane++)
            {
                z ^= MdsColumn(lane, ChainByte(lane, (int)((x >> (8 * lane)) & 0xFF), list, k));
            }

            return z;
        }

        /// <summary>
        /// 一个字节通道的 q 链:k 级"q 置换后异或密钥字(从列表最后一项起)",然后是列 4 的最后一级 q 置换。
        /// 第 stage 级用 QPlan 的第 (4-k+stage) 列,所以 k=2、3 时是跳过开头的列,而不是补零凑满 4 级。
        /// </summary>
        private static int ChainByte(int lane, int x, uint[] list, int k)
        {
            int first = 4 - k;
            int y = x;
            for (int stage = 0; stage < k; stage++)
            {
                uint word = list[k - 1 - stage];
                y = Q[QPlan[lane][first + stage]][y] ^ (int)((word >> (8 * lane)) & 0xFF);
            }

            return Q[QPlan[lane][4]][y];
        }

        /// <summary>RS 编码:把两个 32 位密钥字(共 8 字节)乘 GF(2^8) 上的 RS 矩阵,得到一个 S 向量字。</summary>
        private static uint RsMdsEncode(uint k0, uint k1)
        {
            uint word = 0;
            for (int row = 0; row < 4; row++)
            {
                int sum = 0;
                for (int col = 0; col < 8; col++)
                {
                    int keyByte = col < 4 ? (int)((k0 >> (8 * col)) & 0xFF) : (int)((k1 >> (8 * (col - 4))) & 0xFF);
                    sum ^= GfMul(Rs[row][col], keyByte, RsPoly);
                }

                word |= (uint)sum << (8 * row);
            }

            return word;
        }

        /// <summary>把每个 16 位半内的两个字节对调:字节序 0,1,2,3 变成 1,0,3,2。</summary>
        private static uint SwapHalfBytes(uint w)
        {
            return ((w & 0x00FF00FFu) << 8) | ((w & 0xFF00FF00u) >> 8);
        }

        /// <summary>MDS 矩阵的第 <paramref name="lane"/> 列乘一个字节,得到 32 位字(四个行结果按字节 0 到 3 排)。</summary>
        private static uint MdsColumn(int lane, int y)
        {
            uint word = 0;
            for (int row = 0; row < 4; row++)
            {
                word |= (uint)GfMul(Mds[row][lane], y, MdsPoly) << (8 * row);
            }

            return word;
        }

        private static int GfMul(int a, int b, int poly)
        {
            int result = 0;
            while (b != 0)
            {
                if ((b & 1) != 0)
                {
                    result ^= a;
                }

                a <<= 1;
                if ((a & 0x100) != 0)
                {
                    a ^= poly;
                }

                b >>= 1;
            }

            return result;
        }

        private static byte[] BuildQ(int[][] t)
        {
            byte[] q = new byte[256];
            for (int x = 0; x < 256; x++)
            {
                int a0 = x >> 4;
                int b0 = x & 15;
                int a1 = a0 ^ b0;
                int b1 = (a0 ^ Ror4(b0) ^ ((8 * a0) & 15)) & 15;
                int a2 = t[0][a1];
                int b2 = t[1][b1];
                int a3 = a2 ^ b2;
                int b3 = (a2 ^ Ror4(b2) ^ ((8 * a2) & 15)) & 15;
                int a4 = t[2][a3];
                int b4 = t[3][b3];
                q[x] = (byte)((b4 << 4) | a4);
            }

            return q;
        }

        private static int Ror4(int x)
        {
            return ((x >> 1) | (x << 3)) & 15;
        }

        private static uint LoadLe(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void StoreLe(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static uint RotL(uint v, int n)
        {
            n &= 31;
            return (v << n) | (v >> (32 - n));
        }

        private static uint RotR(uint v, int n)
        {
            return RotL(v, 32 - n);
        }
    }
}
