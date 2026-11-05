using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 11:Skipjack(服务端 CNCryptoSkipjack:Initialize 0x0809ca00、Encrypt 0x0809cc58、Decrypt 0x0809cd4a;槽表见 E-204)。
    /// 与公开规格的关系:与 NIST 公布的 Skipjack 规格一致(80 位密钥、32 轮、16 位字按大端装载;规格里的公开向量逐位通过)。
    /// 密钥片 10 字节全部参与(本机模拟器探测:任一字节翻转都会改变密文,E-263);密钥字节按轮循环使用,每个 G 置换用 4 个。
    /// </summary>
    public sealed class SkipjackCipher : BlockCipher
    {
        /// <summary>公开算法常量:Skipjack 的 F 表(NIST 规格,256 字节;与服务端内嵌表逐项一致)。由 tools/py/gen_crypto_tables.py 与公开文本校验。</summary>
        private static readonly byte[] F =
        {
            0xA3, 0xD7, 0x09, 0x83, 0xF8, 0x48, 0xF6, 0xF4, 0xB3, 0x21, 0x15, 0x78, 0x99, 0xB1, 0xAF, 0xF9,
            0xE7, 0x2D, 0x4D, 0x8A, 0xCE, 0x4C, 0xCA, 0x2E, 0x52, 0x95, 0xD9, 0x1E, 0x4E, 0x38, 0x44, 0x28,
            0x0A, 0xDF, 0x02, 0xA0, 0x17, 0xF1, 0x60, 0x68, 0x12, 0xB7, 0x7A, 0xC3, 0xE9, 0xFA, 0x3D, 0x53,
            0x96, 0x84, 0x6B, 0xBA, 0xF2, 0x63, 0x9A, 0x19, 0x7C, 0xAE, 0xE5, 0xF5, 0xF7, 0x16, 0x6A, 0xA2,
            0x39, 0xB6, 0x7B, 0x0F, 0xC1, 0x93, 0x81, 0x1B, 0xEE, 0xB4, 0x1A, 0xEA, 0xD0, 0x91, 0x2F, 0xB8,
            0x55, 0xB9, 0xDA, 0x85, 0x3F, 0x41, 0xBF, 0xE0, 0x5A, 0x58, 0x80, 0x5F, 0x66, 0x0B, 0xD8, 0x90,
            0x35, 0xD5, 0xC0, 0xA7, 0x33, 0x06, 0x65, 0x69, 0x45, 0x00, 0x94, 0x56, 0x6D, 0x98, 0x9B, 0x76,
            0x97, 0xFC, 0xB2, 0xC2, 0xB0, 0xFE, 0xDB, 0x20, 0xE1, 0xEB, 0xD6, 0xE4, 0xDD, 0x47, 0x4A, 0x1D,
            0x42, 0xED, 0x9E, 0x6E, 0x49, 0x3C, 0xCD, 0x43, 0x27, 0xD2, 0x07, 0xD4, 0xDE, 0xC7, 0x67, 0x18,
            0x89, 0xCB, 0x30, 0x1F, 0x8D, 0xC6, 0x8F, 0xAA, 0xC8, 0x74, 0xDC, 0xC9, 0x5D, 0x5C, 0x31, 0xA4,
            0x70, 0x88, 0x61, 0x2C, 0x9F, 0x0D, 0x2B, 0x87, 0x50, 0x82, 0x54, 0x64, 0x26, 0x7D, 0x03, 0x40,
            0x34, 0x4B, 0x1C, 0x73, 0xD1, 0xC4, 0xFD, 0x3B, 0xCC, 0xFB, 0x7F, 0xAB, 0xE6, 0x3E, 0x5B, 0xA5,
            0xAD, 0x04, 0x23, 0x9C, 0x14, 0x51, 0x22, 0xF0, 0x29, 0x79, 0x71, 0x7E, 0xFF, 0x8C, 0x0E, 0xE2,
            0x0C, 0xEF, 0xBC, 0x72, 0x75, 0x6F, 0x37, 0xA1, 0xEC, 0xD3, 0x8E, 0x62, 0x8B, 0x86, 0x10, 0xE8,
            0x08, 0x77, 0x11, 0xBE, 0x92, 0x4F, 0x24, 0xC5, 0x32, 0x36, 0x9D, 0xCF, 0xF3, 0xA6, 0xBB, 0xAC,
            0x5E, 0x6C, 0xA9, 0x13, 0x57, 0x25, 0xB5, 0xE3, 0xBD, 0xA8, 0x3A, 0x01, 0x05, 0x59, 0x2A, 0x46,
        };

        private const int Rounds = 32;
        private const int KeyBytes = 10;

        private readonly byte[] _key = new byte[KeyBytes];
        private bool _keyed;

        public override int Slot => 11;

        public override int BlockSize => 8;

        public override int KeyLength => KeyBytes;

        public override void SetKey(byte[] key, int offset)
        {
            CheckRange(key, offset, KeyLength, nameof(key), nameof(offset));
            Array.Copy(key, offset, _key, 0, KeyBytes);
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            int w1 = (input[inOffset] << 8) | input[inOffset + 1];
            int w2 = (input[inOffset + 2] << 8) | input[inOffset + 3];
            int w3 = (input[inOffset + 4] << 8) | input[inOffset + 5];
            int w4 = (input[inOffset + 6] << 8) | input[inOffset + 7];
            for (int round = 1; round <= Rounds; round++)
            {
                int g = G(w1, round);
                if (IsRuleA(round))
                {
                    // 规则 A:新 w1 = G(w1) ^ w4 ^ 轮号,其余依次右移。
                    int n1 = g ^ w4 ^ round;
                    w4 = w3;
                    w3 = w2;
                    w2 = g;
                    w1 = n1;
                }
                else
                {
                    // 规则 B:新 w1 = 旧 w4,新 w3 = w1 ^ w2 ^ 轮号。
                    int n3 = w1 ^ w2 ^ round;
                    int n1 = w4;
                    w4 = w3;
                    w3 = n3;
                    w2 = g;
                    w1 = n1;
                }
            }
            Store(w1, w2, w3, w4, output, outOffset);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            int w1 = (input[inOffset] << 8) | input[inOffset + 1];
            int w2 = (input[inOffset + 2] << 8) | input[inOffset + 3];
            int w3 = (input[inOffset + 4] << 8) | input[inOffset + 5];
            int w4 = (input[inOffset + 6] << 8) | input[inOffset + 7];
            for (int round = Rounds; round >= 1; round--)
            {
                if (IsRuleA(round))
                {
                    // 规则 A 的逆:旧 w1 = G^-1(w2),旧 w4 = w1 ^ w2 ^ 轮号。
                    int o4 = w1 ^ w2 ^ round;
                    int o1 = GInverse(w2, round);
                    w2 = w3;
                    w3 = w4;
                    w4 = o4;
                    w1 = o1;
                }
                else
                {
                    // 规则 B 的逆:旧 w1 = G^-1(w2),旧 w2 = 旧 w1 ^ w3 ^ 轮号,旧 w4 = w1。
                    int o1 = GInverse(w2, round);
                    int o2 = o1 ^ w3 ^ round;
                    int o4 = w1;
                    w3 = w4;
                    w4 = o4;
                    w2 = o2;
                    w1 = o1;
                }
            }
            Store(w1, w2, w3, w4, output, outOffset);
        }

        /// <summary>第 1–8、17–24 轮用规则 A,第 9–16、25–32 轮用规则 B(NIST 规格)。</summary>
        private static bool IsRuleA(int round)
        {
            return (round - 1) / 8 % 2 == 0;
        }

        /// <summary>置换 G:四步 Feistel,第 r 轮取密钥字节 (4(r-1)+i) mod 10,i = 0..3。</summary>
        private int G(int w, int round)
        {
            int g1 = (w >> 8) & 0xFF;
            int g2 = w & 0xFF;
            int p = 4 * (round - 1);
            g1 ^= F[g2 ^ _key[p % KeyBytes]];
            g2 ^= F[g1 ^ _key[(p + 1) % KeyBytes]];
            g1 ^= F[g2 ^ _key[(p + 2) % KeyBytes]];
            g2 ^= F[g1 ^ _key[(p + 3) % KeyBytes]];
            return (g1 << 8) | g2;
        }

        /// <summary>G 的逆:同样四个密钥字节,反向使用。</summary>
        private int GInverse(int w, int round)
        {
            int g1 = (w >> 8) & 0xFF;
            int g2 = w & 0xFF;
            int p = 4 * (round - 1);
            g2 ^= F[g1 ^ _key[(p + 3) % KeyBytes]];
            g1 ^= F[g2 ^ _key[(p + 2) % KeyBytes]];
            g2 ^= F[g1 ^ _key[(p + 1) % KeyBytes]];
            g1 ^= F[g2 ^ _key[p % KeyBytes]];
            return (g1 << 8) | g2;
        }

        private static void Store(int w1, int w2, int w3, int w4, byte[] output, int outOffset)
        {
            output[outOffset] = (byte)(w1 >> 8);
            output[outOffset + 1] = (byte)w1;
            output[outOffset + 2] = (byte)(w2 >> 8);
            output[outOffset + 3] = (byte)w2;
            output[outOffset + 4] = (byte)(w3 >> 8);
            output[outOffset + 5] = (byte)w3;
            output[outOffset + 6] = (byte)(w4 >> 8);
            output[outOffset + 7] = (byte)w4;
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
    }
}
