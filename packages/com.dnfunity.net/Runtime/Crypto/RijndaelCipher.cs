using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 1:标准 AES-128(ECB 单块),块长 16 字节,切片 16 字节全部参与(原版 CRijndael::Initialize 0x080b6220,Encrypt 0x0809c2e8;
    /// 本机模拟器对 16 条合成输入与 FIPS-197 公开向量逐位一致,E-263)。
    /// 自实现而不用 System.Security.Cryptography.Aes:纯托管、无需释放、不依赖平台的加密后端(IL2CPP/移动端),
    /// 对象在 SetKey 之后只读,同一个对象可以被多个线程同时加解密(SetKey 本身不能与加解密并发)。
    /// S 盒不贴表:在静态构造里按 FIPS-197 的定义算出(GF(2^8) 模 x^8+x^4+x^3+x+1 的乘法逆元,再做仿射变换),
    /// 由公开向量与模拟器向量把关。
    /// </summary>
    public sealed class RijndaelCipher : BlockCipher
    {
        private const int Rounds = 10;
        private const int RoundKeyBytes = 16 * (Rounds + 1);

        private static readonly byte[] Sbox = new byte[256];
        private static readonly byte[] InvSbox = new byte[256];

        // 列混合要乘 2、3,逆列混合要乘 9、11、13、14:预先按字节算成 256 项的表,加解密时查表
        // (逐位乘会让解密慢约 20 倍,XTime 里带分支的乘 2 也会让加密慢一半以上)。
        private static readonly byte[] Mul2 = new byte[256];
        private static readonly byte[] Mul3 = new byte[256];
        private static readonly byte[] Mul9 = new byte[256];
        private static readonly byte[] Mul11 = new byte[256];
        private static readonly byte[] Mul13 = new byte[256];
        private static readonly byte[] Mul14 = new byte[256];

        static RijndaelCipher()
        {
            // 以 3 为生成元走遍 GF(2^8) 的非零元素,得到对数/反对数表,再由 a^-1 = 3^(255 - log a) 求逆元。
            byte[] exp = new byte[256];
            byte[] log = new byte[256];
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                exp[i] = (byte)x;
                log[x] = (byte)i;
                x ^= XTime(x);          // x *= 3
            }
            for (int i = 0; i < 256; i++)
            {
                int inv = i == 0 ? 0 : exp[(255 - log[i]) % 255];
                // 仿射变换:b ^ rotl(b,1) ^ rotl(b,2) ^ rotl(b,3) ^ rotl(b,4) ^ 0x63
                int s = inv ^ RotL8(inv, 1) ^ RotL8(inv, 2) ^ RotL8(inv, 3) ^ RotL8(inv, 4) ^ 0x63;
                Sbox[i] = (byte)s;
                InvSbox[s] = (byte)i;
            }
            for (int i = 0; i < 256; i++)
            {
                Mul2[i] = (byte)XTime(i);
                Mul3[i] = (byte)(XTime(i) ^ i);
                Mul9[i] = (byte)Mul(i, 9);
                Mul11[i] = (byte)Mul(i, 11);
                Mul13[i] = (byte)Mul(i, 13);
                Mul14[i] = (byte)Mul(i, 14);
            }
        }

        private readonly byte[] _roundKeys = new byte[RoundKeyBytes];
        private bool _keyed;

        public override int Slot => 1;

        public override int BlockSize => 16;

        public override int KeyLength => 16;

        public override void SetKey(byte[] key, int offset)
        {
            SlotKit0To6.CheckKey(key, offset, KeyLength);
            Buffer.BlockCopy(key, offset, _roundKeys, 0, 16);
            int rcon = 1;
            for (int i = 16; i < RoundKeyBytes; i += 4)
            {
                int t0 = _roundKeys[i - 4];
                int t1 = _roundKeys[i - 3];
                int t2 = _roundKeys[i - 2];
                int t3 = _roundKeys[i - 1];
                if (i % 16 == 0)
                {
                    // RotWord + SubWord + Rcon
                    int first = t0;
                    t0 = Sbox[t1] ^ rcon;
                    t1 = Sbox[t2];
                    t2 = Sbox[t3];
                    t3 = Sbox[first];
                    rcon = XTime(rcon);
                }
                _roundKeys[i] = (byte)(_roundKeys[i - 16] ^ t0);
                _roundKeys[i + 1] = (byte)(_roundKeys[i - 15] ^ t1);
                _roundKeys[i + 2] = (byte)(_roundKeys[i - 14] ^ t2);
                _roundKeys[i + 3] = (byte)(_roundKeys[i - 13] ^ t3);
            }
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            Span<byte> s = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                s[i] = (byte)(input[inOffset + i] ^ _roundKeys[i]);
            }
            for (int round = 1; round <= Rounds; round++)
            {
                SubBytesShiftRows(s);
                if (round != Rounds)
                {
                    MixColumns(s);
                }
                AddRoundKey(s, round);
            }
            for (int i = 0; i < 16; i++)
            {
                output[outOffset + i] = s[i];
            }
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            Span<byte> s = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                s[i] = (byte)(input[inOffset + i] ^ _roundKeys[16 * Rounds + i]);
            }
            for (int round = Rounds - 1; round >= 0; round--)
            {
                InvShiftRowsSubBytes(s);
                AddRoundKey(s, round);
                if (round != 0)
                {
                    InvMixColumns(s);
                }
            }
            for (int i = 0; i < 16; i++)
            {
                output[outOffset + i] = s[i];
            }
        }

        private void AddRoundKey(Span<byte> s, int round)
        {
            int o = 16 * round;
            for (int i = 0; i < 16; i++)
            {
                s[i] ^= _roundKeys[o + i];
            }
        }

        // 状态按列优先存放:s[4 * 列 + 行] 就是输入字节的原顺序。
        private static void SubBytesShiftRows(Span<byte> s)
        {
            Span<byte> t = stackalloc byte[16];
            for (int c = 0; c < 4; c++)
            {
                for (int r = 0; r < 4; r++)
                {
                    t[4 * c + r] = Sbox[s[4 * ((c + r) & 3) + r]];     // 第 r 行向左移 r 格
                }
            }
            t.CopyTo(s);
        }

        private static void InvShiftRowsSubBytes(Span<byte> s)
        {
            Span<byte> t = stackalloc byte[16];
            for (int c = 0; c < 4; c++)
            {
                for (int r = 0; r < 4; r++)
                {
                    t[4 * ((c + r) & 3) + r] = InvSbox[s[4 * c + r]];  // 第 r 行向右移 r 格
                }
            }
            t.CopyTo(s);
        }

        private static void MixColumns(Span<byte> s)
        {
            for (int c = 0; c < 4; c++)
            {
                int a0 = s[4 * c];
                int a1 = s[4 * c + 1];
                int a2 = s[4 * c + 2];
                int a3 = s[4 * c + 3];
                s[4 * c] = (byte)(Mul2[a0] ^ Mul3[a1] ^ a2 ^ a3);
                s[4 * c + 1] = (byte)(a0 ^ Mul2[a1] ^ Mul3[a2] ^ a3);
                s[4 * c + 2] = (byte)(a0 ^ a1 ^ Mul2[a2] ^ Mul3[a3]);
                s[4 * c + 3] = (byte)(Mul3[a0] ^ a1 ^ a2 ^ Mul2[a3]);
            }
        }

        private static void InvMixColumns(Span<byte> s)
        {
            for (int c = 0; c < 4; c++)
            {
                int a0 = s[4 * c];
                int a1 = s[4 * c + 1];
                int a2 = s[4 * c + 2];
                int a3 = s[4 * c + 3];
                s[4 * c] = (byte)(Mul14[a0] ^ Mul11[a1] ^ Mul13[a2] ^ Mul9[a3]);
                s[4 * c + 1] = (byte)(Mul9[a0] ^ Mul14[a1] ^ Mul11[a2] ^ Mul13[a3]);
                s[4 * c + 2] = (byte)(Mul13[a0] ^ Mul9[a1] ^ Mul14[a2] ^ Mul11[a3]);
                s[4 * c + 3] = (byte)(Mul11[a0] ^ Mul13[a1] ^ Mul9[a2] ^ Mul14[a3]);
            }
        }

        /// <summary>GF(2^8) 里乘 2(模 0x11B)。</summary>
        private static int XTime(int x)
        {
            x <<= 1;
            if ((x & 0x100) != 0)
            {
                x ^= 0x11B;
            }
            return x & 0xFF;
        }

        /// <summary>GF(2^8) 里乘一个小常数(逐位累加;只在静态构造里建表时用)。</summary>
        private static int Mul(int x, int k)
        {
            int result = 0;
            while (k != 0)
            {
                if ((k & 1) != 0)
                {
                    result ^= x;
                }
                x = XTime(x);
                k >>= 1;
            }
            return result;
        }

        private static int RotL8(int x, int n)
        {
            return ((x << n) | (x >> (8 - n))) & 0xFF;
        }
    }
}
