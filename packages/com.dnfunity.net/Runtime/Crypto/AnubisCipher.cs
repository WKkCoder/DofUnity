using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 13:Anubis(服务端 CNCryptoAnubis:Initialize 0x080993bc、Encrypt 0x08099614、Decrypt 0x08099706;槽表见 E-204)。
    /// 与公开算法的关系:使用 Anubis 原版 S 盒(不是后来修订过的「tweak」版),128 位块、128 位密钥(N=4、12 轮),
    /// 字按大端装载;NESSIE 公开的原版向量逐位通过。两个版本的 S 盒不同,同一把密钥会得到不同密文,所以必须内嵌原版 S 盒。
    /// 密钥片 16 字节全部参与(本机模拟器探测:任一字节翻转都会改变密文,E-263)。
    /// 已有黄金向量(E-263)里这一行的密钥来自服务端对象构造时自己生成的内部状态,不是外部给定的密钥;
    /// 复现它需要用本机模拟器还原出的那把 16 字节密钥,见测试。
    /// 实现取舍:S 盒内嵌 256 字节,其余 6 张表启动时由 S 盒与 GF(2^8) 乘法现算。
    /// </summary>
    public sealed class AnubisCipher : BlockCipher
    {
        /// <summary>密钥字数(128 位密钥)。</summary>
        private const int N = 4;

        /// <summary>轮数 = 8 + N。</summary>
        private const int Rounds = 8 + N;

        /// <summary>公开算法常量:Anubis 原版 S 盒(256 字节,是一个对合,与服务端内嵌表逐项一致)。由 tools/py/gen_crypto_tables.py 与公开文本校验。</summary>
        private static readonly byte[] Sbox =
        {
            0xA7, 0xD3, 0xE6, 0x71, 0xD0, 0xAC, 0x4D, 0x79, 0x3A, 0xC9, 0x91, 0xFC, 0x1E, 0x47, 0x54, 0xBD,
            0x8C, 0xA5, 0x7A, 0xFB, 0x63, 0xB8, 0xDD, 0xD4, 0xE5, 0xB3, 0xC5, 0xBE, 0xA9, 0x88, 0x0C, 0xA2,
            0x39, 0xDF, 0x29, 0xDA, 0x2B, 0xA8, 0xCB, 0x4C, 0x4B, 0x22, 0xAA, 0x24, 0x41, 0x70, 0xA6, 0xF9,
            0x5A, 0xE2, 0xB0, 0x36, 0x7D, 0xE4, 0x33, 0xFF, 0x60, 0x20, 0x08, 0x8B, 0x5E, 0xAB, 0x7F, 0x78,
            0x7C, 0x2C, 0x57, 0xD2, 0xDC, 0x6D, 0x7E, 0x0D, 0x53, 0x94, 0xC3, 0x28, 0x27, 0x06, 0x5F, 0xAD,
            0x67, 0x5C, 0x55, 0x48, 0x0E, 0x52, 0xEA, 0x42, 0x5B, 0x5D, 0x30, 0x58, 0x51, 0x59, 0x3C, 0x4E,
            0x38, 0x8A, 0x72, 0x14, 0xE7, 0xC6, 0xDE, 0x50, 0x8E, 0x92, 0xD1, 0x77, 0x93, 0x45, 0x9A, 0xCE,
            0x2D, 0x03, 0x62, 0xB6, 0xB9, 0xBF, 0x96, 0x6B, 0x3F, 0x07, 0x12, 0xAE, 0x40, 0x34, 0x46, 0x3E,
            0xDB, 0xCF, 0xEC, 0xCC, 0xC1, 0xA1, 0xC0, 0xD6, 0x1D, 0xF4, 0x61, 0x3B, 0x10, 0xD8, 0x68, 0xA0,
            0xB1, 0x0A, 0x69, 0x6C, 0x49, 0xFA, 0x76, 0xC4, 0x9E, 0x9B, 0x6E, 0x99, 0xC2, 0xB7, 0x98, 0xBC,
            0x8F, 0x85, 0x1F, 0xB4, 0xF8, 0x11, 0x2E, 0x00, 0x25, 0x1C, 0x2A, 0x3D, 0x05, 0x4F, 0x7B, 0xB2,
            0x32, 0x90, 0xAF, 0x19, 0xA3, 0xF7, 0x73, 0x9D, 0x15, 0x74, 0xEE, 0xCA, 0x9F, 0x0F, 0x1B, 0x75,
            0x86, 0x84, 0x9C, 0x4A, 0x97, 0x1A, 0x65, 0xF6, 0xED, 0x09, 0xBB, 0x26, 0x83, 0xEB, 0x6F, 0x81,
            0x04, 0x6A, 0x43, 0x01, 0x17, 0xE1, 0x87, 0xF5, 0x8D, 0xE3, 0x23, 0x80, 0x44, 0x16, 0x66, 0x21,
            0xFE, 0xD5, 0x31, 0xD9, 0x35, 0x18, 0x02, 0x64, 0xF2, 0xF1, 0x56, 0xCD, 0x82, 0xC8, 0xBA, 0xF0,
            0xEF, 0xE9, 0xE8, 0xFD, 0x89, 0xD7, 0xC7, 0xB5, 0xA4, 0x2F, 0x95, 0x13, 0x0B, 0xF3, 0xE0, 0x37,
        };

        // T0..T3:字节替换与扩散合并的查表,第 k 张表的第 i 个字节 = Sbox[x] 乘 Hadamard 行 (1,2,4,6) 的第 i^k 项。
        private static readonly uint[] T0 = BuildT(0);

        private static readonly uint[] T1 = BuildT(1);

        private static readonly uint[] T2 = BuildT(2);

        private static readonly uint[] T3 = BuildT(3);

        // 密钥调度专用:输入字节乘 (1,2,6,8),不经过 S 盒。
        private static readonly uint[] KeyMix = BuildKeyMix();

        // 密钥调度的轮常数:S 盒的连续 4 字节(第 r 个 = Sbox[4r .. 4r+3],大端)。
        private static readonly uint[] RoundConstants = BuildConstants();

        private readonly uint[] _ek = new uint[(Rounds + 1) * 4];
        private readonly uint[] _dk = new uint[(Rounds + 1) * 4];
        private bool _keyed;

        public override int Slot => 13;

        public override int BlockSize => 16;

        public override int KeyLength => 16;

        public override void SetKey(byte[] key, int offset)
        {
            CheckRange(key, offset, KeyLength, nameof(key), nameof(offset));
            var kappa = new uint[N];
            for (int i = 0; i < N; i++)
            {
                kappa[i] = LoadBe(key, offset + 4 * i);
            }
            var inter = new uint[N];
            var kk = new uint[4];
            var nk = new uint[4];
            for (int r = 0; r <= Rounds; r++)
            {
                // 轮密钥:由 kappa 的最后一个字起,逐个并入前面的字(每并入一个就做一次字节内混合)。
                for (int m = 0; m < 4; m++)
                {
                    kk[m] = Replicate(Sbox[(kappa[N - 1] >> (24 - 8 * m)) & 0xFF]);
                }
                for (int i = N - 2; i >= 0; i--)
                {
                    for (int m = 0; m < 4; m++)
                    {
                        uint v = kk[m];
                        nk[m] = Replicate(Sbox[(kappa[i] >> (24 - 8 * m)) & 0xFF])
                            ^ (KeyMix[v >> 24] & 0xFF000000u)
                            ^ (KeyMix[(v >> 16) & 0xFF] & 0x00FF0000u)
                            ^ (KeyMix[(v >> 8) & 0xFF] & 0x0000FF00u)
                            ^ (KeyMix[v & 0xFF] & 0x000000FFu);
                    }
                    Array.Copy(nk, kk, 4);
                }
                Array.Copy(kk, 0, _ek, 4 * r, 4);
                if (r == Rounds)
                {
                    break;
                }
                // 下一轮的 kappa:每个字由四个相邻字的各一个字节查表合成,再把第 0 个字异或轮常数。
                for (int i = 0; i < N; i++)
                {
                    int j = i;
                    uint v = T0[kappa[j] >> 24];
                    j = (j + N - 1) % N;
                    v ^= T1[(kappa[j] >> 16) & 0xFF];
                    j = (j + N - 1) % N;
                    v ^= T2[(kappa[j] >> 8) & 0xFF];
                    j = (j + N - 1) % N;
                    v ^= T3[kappa[j] & 0xFF];
                    inter[i] = v;
                }
                kappa[0] = inter[0] ^ RoundConstants[r];
                for (int i = 1; i < N; i++)
                {
                    kappa[i] = inter[i];
                }
            }
            // 解密轮密钥:次序反过来,中间各轮再经过一次字节替换与扩散。
            for (int i = 0; i < 4; i++)
            {
                _dk[i] = _ek[4 * Rounds + i];
                _dk[4 * Rounds + i] = _ek[i];
            }
            for (int r = 1; r < Rounds; r++)
            {
                for (int i = 0; i < 4; i++)
                {
                    uint v = _ek[4 * (Rounds - r) + i];
                    _dk[4 * r + i] = T0[Sbox[v >> 24]]
                        ^ T1[Sbox[(v >> 16) & 0xFF]]
                        ^ T2[Sbox[(v >> 8) & 0xFF]]
                        ^ T3[Sbox[v & 0xFF]];
                }
            }
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

        private static void Crypt(byte[] input, int inOffset, byte[] output, int outOffset, uint[] rk)
        {
            uint s0 = LoadBe(input, inOffset) ^ rk[0];
            uint s1 = LoadBe(input, inOffset + 4) ^ rk[1];
            uint s2 = LoadBe(input, inOffset + 8) ^ rk[2];
            uint s3 = LoadBe(input, inOffset + 12) ^ rk[3];
            for (int r = 1; r < Rounds; r++)
            {
                uint t0 = T0[s0 >> 24] ^ T1[s1 >> 24] ^ T2[s2 >> 24] ^ T3[s3 >> 24] ^ rk[4 * r];
                uint t1 = T0[(s0 >> 16) & 0xFF] ^ T1[(s1 >> 16) & 0xFF] ^ T2[(s2 >> 16) & 0xFF] ^ T3[(s3 >> 16) & 0xFF] ^ rk[4 * r + 1];
                uint t2 = T0[(s0 >> 8) & 0xFF] ^ T1[(s1 >> 8) & 0xFF] ^ T2[(s2 >> 8) & 0xFF] ^ T3[(s3 >> 8) & 0xFF] ^ rk[4 * r + 2];
                uint t3 = T0[s0 & 0xFF] ^ T1[s1 & 0xFF] ^ T2[s2 & 0xFF] ^ T3[s3 & 0xFF] ^ rk[4 * r + 3];
                s0 = t0;
                s1 = t1;
                s2 = t2;
                s3 = t3;
            }
            // 最后一轮没有扩散:每张表只取落在本字节位置上的那个字节(正是 S 盒的输出)。
            int last = 4 * Rounds;
            uint o0 = (T0[s0 >> 24] & 0xFF000000u) ^ (T1[s1 >> 24] & 0x00FF0000u) ^ (T2[s2 >> 24] & 0x0000FF00u) ^ (T3[s3 >> 24] & 0x000000FFu) ^ rk[last];
            uint o1 = (T0[(s0 >> 16) & 0xFF] & 0xFF000000u) ^ (T1[(s1 >> 16) & 0xFF] & 0x00FF0000u) ^ (T2[(s2 >> 16) & 0xFF] & 0x0000FF00u) ^ (T3[(s3 >> 16) & 0xFF] & 0x000000FFu) ^ rk[last + 1];
            uint o2 = (T0[(s0 >> 8) & 0xFF] & 0xFF000000u) ^ (T1[(s1 >> 8) & 0xFF] & 0x00FF0000u) ^ (T2[(s2 >> 8) & 0xFF] & 0x0000FF00u) ^ (T3[(s3 >> 8) & 0xFF] & 0x000000FFu) ^ rk[last + 2];
            uint o3 = (T0[s0 & 0xFF] & 0xFF000000u) ^ (T1[s1 & 0xFF] & 0x00FF0000u) ^ (T2[s2 & 0xFF] & 0x0000FF00u) ^ (T3[s3 & 0xFF] & 0x000000FFu) ^ rk[last + 3];
            StoreBe(o0, output, outOffset);
            StoreBe(o1, output, outOffset + 4);
            StoreBe(o2, output, outOffset + 8);
            StoreBe(o3, output, outOffset + 12);
        }

        private static uint[] BuildT(int k)
        {
            var row = new byte[] { 1, 2, 4, 6 };
            var t = new uint[256];
            for (int x = 0; x < 256; x++)
            {
                uint v = 0;
                for (int i = 0; i < 4; i++)
                {
                    v = (v << 8) | GfMul(Sbox[x], row[i ^ k]);
                }
                t[x] = v;
            }
            return t;
        }

        private static uint[] BuildKeyMix()
        {
            var row = new byte[] { 1, 2, 6, 8 };
            var t = new uint[256];
            for (int x = 0; x < 256; x++)
            {
                uint v = 0;
                for (int i = 0; i < 4; i++)
                {
                    v = (v << 8) | GfMul(x, row[i]);
                }
                t[x] = v;
            }
            return t;
        }

        private static uint[] BuildConstants()
        {
            var c = new uint[Rounds];
            for (int r = 0; r < Rounds; r++)
            {
                uint v = 0;
                for (int i = 0; i < 4; i++)
                {
                    v = (v << 8) | Sbox[4 * r + i];
                }
                c[r] = v;
            }
            return c;
        }

        private static uint Replicate(byte b)
        {
            return b * 0x01010101u;
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

        private static uint LoadBe(byte[] b, int i)
        {
            return unchecked((uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]));
        }

        private static void StoreBe(uint v, byte[] b, int i)
        {
            b[i] = (byte)(v >> 24);
            b[i + 1] = (byte)(v >> 16);
            b[i + 2] = (byte)(v >> 8);
            b[i + 3] = (byte)v;
        }
    }
}
