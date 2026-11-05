using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 9:Khazad(服务端 CNCryptoKhazad:Initialize 0x0809aba8、Encrypt 0x0809ae00、Decrypt 0x0809aef2;槽表见 E-204)。
    /// 与公开算法的关系:使用 Khazad 公开规格里修订后的 S 盒,8 轮,64 位块、128 位密钥,
    /// 字节序为大端;服务端内嵌的 8 张 T 表与按公开规格算出的表逐项一致(S 盒由 4 位小盒 P、Q 经三层结构生成,再乘扩散矩阵),
    /// 官方公开向量逐位通过。
    /// 密钥片 16 字节全部参与(本机模拟器探测:任一字节翻转都会改变密文,E-263)。
    /// 实现取舍:不内嵌 8 张 256 项的大表,而是启动时由 S 盒与 GF(2^8) 乘法现算,代码量小且便于核对。
    /// </summary>
    public sealed class KhazadCipher : BlockCipher
    {
        // 公开规格的两个 4 位小盒。
        private static readonly byte[] MiniP = { 0x3, 0xF, 0xE, 0x0, 0x5, 0x4, 0xB, 0xC, 0xD, 0xA, 0x9, 0x6, 0x7, 0x8, 0x2, 0x1 };

        private static readonly byte[] MiniQ = { 0x9, 0xE, 0x5, 0x6, 0xA, 0x2, 0x3, 0xC, 0xF, 0x0, 0x4, 0xD, 0x7, 0xB, 0x1, 0x8 };

        /// <summary>扩散矩阵(循环 Hadamard 型)第一行,多项式 x^8+x^4+x^3+x^2+1(0x11D)。</summary>
        private static readonly byte[] Row = { 0x01, 0x03, 0x04, 0x05, 0x06, 0x08, 0x0B, 0x07 };

        private const int Rounds = 8;

        private static readonly byte[] Sbox = BuildSbox();

        // Table[k][x] 的第 i 个字节 = Sbox[x] 乘 Row[i ^ k];第 k 张表对应输入字节所在的位置 k。
        private static readonly ulong[][] Table = BuildTables();

        // 轮常数:取 S 盒的连续 8 字节(第 r 个常数 = Sbox[8r .. 8r+7],大端)。
        private static readonly ulong[] RoundConstants = BuildConstants();

        private readonly ulong[] _ek = new ulong[Rounds + 1];
        private readonly ulong[] _dk = new ulong[Rounds + 1];
        private bool _keyed;

        public override int Slot => 9;

        public override int BlockSize => 8;

        public override int KeyLength => 16;

        public override void SetKey(byte[] key, int offset)
        {
            CheckRange(key, offset, KeyLength, nameof(key), nameof(offset));
            ulong k2 = LoadBe(key, offset);
            ulong k1 = LoadBe(key, offset + 8);
            // 加密轮密钥:把上一个轮密钥过一遍线性扩散,再异或轮常数与上上个轮密钥。
            for (int r = 0; r <= Rounds; r++)
            {
                ulong v = Diffuse(k1) ^ RoundConstants[r] ^ k2;
                _ek[r] = v;
                k2 = k1;
                k1 = v;
            }
            // 解密轮密钥:次序反过来,中间各轮再额外经过一次扩散变换(Khazad 的对合结构)。
            _dk[0] = _ek[Rounds];
            for (int r = 1; r < Rounds; r++)
            {
                _dk[r] = Diffuse(Substitute(_ek[Rounds - r]));
            }
            _dk[Rounds] = _ek[0];
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            Crypt(input, inOffset, output, outOffset, _ek);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            Crypt(input, inOffset, output, outOffset, _dk);
        }

        private static void Crypt(byte[] input, int inOffset, byte[] output, int outOffset, ulong[] rk)
        {
            ulong state = LoadBe(input, inOffset) ^ rk[0];
            for (int r = 1; r < Rounds; r++)
            {
                state = Diffuse(state) ^ rk[r];
            }
            // 最后一轮没有扩散,只做字节替换与轮密钥加。
            StoreBe(Substitute(state) ^ rk[Rounds], output, outOffset);
        }

        /// <summary>一轮的「字节替换 + 线性扩散」合并查表:8 个字节各查一张表后异或。</summary>
        private static ulong Diffuse(ulong v)
        {
            return Table[0][(int)(v >> 56)]
                ^ Table[1][(int)((v >> 48) & 0xFF)]
                ^ Table[2][(int)((v >> 40) & 0xFF)]
                ^ Table[3][(int)((v >> 32) & 0xFF)]
                ^ Table[4][(int)((v >> 24) & 0xFF)]
                ^ Table[5][(int)((v >> 16) & 0xFF)]
                ^ Table[6][(int)((v >> 8) & 0xFF)]
                ^ Table[7][(int)(v & 0xFF)];
        }

        /// <summary>对 8 个字节分别做 S 盒替换(不扩散)。</summary>
        private static ulong Substitute(ulong v)
        {
            ulong r = 0;
            for (int i = 0; i < 8; i++)
            {
                r = (r << 8) | Sbox[(int)((v >> (56 - 8 * i)) & 0xFF)];
            }
            return r;
        }

        private static byte[] BuildSbox()
        {
            var s = new byte[256];
            for (int x = 0; x < 256; x++)
            {
                // 三层:(P,Q) → 交换中间两个 2 位组 → (Q,P) → 交换中间两个 2 位组 → (P,Q)。
                int v = Layer(MiniP, MiniQ, x);
                v = SwapMiddle(v);
                v = Layer(MiniQ, MiniP, v);
                v = SwapMiddle(v);
                v = Layer(MiniP, MiniQ, v);
                s[x] = (byte)v;
            }
            return s;
        }

        private static int Layer(byte[] high, byte[] low, int x)
        {
            return (high[x >> 4] << 4) | low[x & 0xF];
        }

        private static int SwapMiddle(int x)
        {
            return (x & 0xC3) | ((x & 0x30) >> 2) | ((x & 0x0C) << 2);
        }

        private static ulong[][] BuildTables()
        {
            var t = new ulong[8][];
            for (int k = 0; k < 8; k++)
            {
                t[k] = new ulong[256];
                for (int x = 0; x < 256; x++)
                {
                    ulong v = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        v = (v << 8) | GfMul(Sbox[x], Row[i ^ k]);
                    }
                    t[k][x] = v;
                }
            }
            return t;
        }

        private static ulong[] BuildConstants()
        {
            var c = new ulong[Rounds + 1];
            for (int r = 0; r <= Rounds; r++)
            {
                ulong v = 0;
                for (int i = 0; i < 8; i++)
                {
                    v = (v << 8) | Sbox[8 * r + i];
                }
                c[r] = v;
            }
            return c;
        }

        /// <summary>GF(2^8) 上的乘法,模多项式 0x11D。</summary>
        private static byte GfMul(int a, int b)
        {
            int r = 0;
            while (b != 0)
            {
                if ((b & 1) != 0)
                {
                    r ^= a;
                }
                a <<= 1;
                if ((a & 0x100) != 0)
                {
                    a ^= 0x11D;
                }
                b >>= 1;
            }
            return (byte)r;
        }

        private void CheckBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckRange(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            CheckRange(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            if (!_keyed)
            {
                throw new InvalidOperationException("尚未设置密钥");
            }
        }

        private static void CheckRange(byte[] array, int offset, int length, string arrayName, string offsetName)
        {
            if (array == null)
            {
                throw new ArgumentNullException(arrayName);
            }
            if (offset < 0 || array.Length - offset < length)
            {
                throw new ArgumentOutOfRangeException(offsetName, "从该偏移起不足 " + length + " 字节");
            }
        }

        private static ulong LoadBe(byte[] b, int i)
        {
            ulong v = 0;
            for (int k = 0; k < 8; k++)
            {
                v = (v << 8) | b[i + k];
            }
            return v;
        }

        private static void StoreBe(ulong v, byte[] b, int i)
        {
            for (int k = 0; k < 8; k++)
            {
                b[i + k] = (byte)(v >> (56 - 8 * k));
            }
        }
    }
}
