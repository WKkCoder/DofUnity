using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 12:MULTI2(服务端 CNCryptoMulti2:Initialize 0x0809b14c、Encrypt 0x0809b3a4、Decrypt 0x0809b496;槽表见 E-204)。
    /// 与公开算法的关系:轮函数 π1–π4 与公开描述(ISO 9979 登记算法)一致;服务端固定 128 轮,
    /// 密钥片 40 字节的布局沿用 libtomcrypt:前 32 字节是 8 个大端字的系统密钥,后 8 字节是 2 个大端字的数据密钥。
    /// 40 字节全部参与(本机模拟器探测:任一字节翻转都会改变密文,E-263)。
    /// 注意:已有黄金向量(E-263)里这一行的密钥列只有 39 字节,最后一个字节是隐含的 0x00,使用时要补齐到 40 字节。
    /// </summary>
    public sealed class Multi2Cipher : BlockCipher
    {
        /// <summary>服务端使用的轮数(每 4 轮一组:π1、π2、π3、π4,共 32 组)。</summary>
        private const int Rounds = 128;

        private const int Groups = Rounds / 4;

        // 由数据密钥经系统密钥展开得到的 8 个轮密钥;相邻两组交替使用前 4 个与后 4 个。
        private readonly uint[] _uk = new uint[8];
        private bool _keyed;

        public override int Slot => 12;

        public override int BlockSize => 8;

        public override int KeyLength => 40;

        public override void SetKey(byte[] key, int offset)
        {
            CheckRange(key, offset, KeyLength, nameof(key), nameof(offset));
            var sk = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                sk[i] = LoadBe(key, offset + 4 * i);
            }
            uint p0 = LoadBe(key, offset + 32);
            uint p1 = LoadBe(key, offset + 36);

            Pi1(ref p0, ref p1);
            Pi2(ref p0, ref p1, sk[0]);
            _uk[0] = p0;
            Pi3(ref p0, ref p1, sk[1], sk[2]);
            _uk[1] = p1;
            Pi4(ref p0, ref p1, sk[3]);
            _uk[2] = p0;
            Pi1(ref p0, ref p1);
            _uk[3] = p1;
            Pi2(ref p0, ref p1, sk[4]);
            _uk[4] = p0;
            Pi3(ref p0, ref p1, sk[5], sk[6]);
            _uk[5] = p1;
            Pi4(ref p0, ref p1, sk[7]);
            _uk[6] = p0;
            Pi1(ref p0, ref p1);
            _uk[7] = p1;
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            uint p0 = LoadBe(input, inOffset);
            uint p1 = LoadBe(input, inOffset + 4);
            for (int g = 0; g < Groups; g++)
            {
                int t = (g & 1) * 4;
                Pi1(ref p0, ref p1);
                Pi2(ref p0, ref p1, _uk[t]);
                Pi3(ref p0, ref p1, _uk[t + 1], _uk[t + 2]);
                Pi4(ref p0, ref p1, _uk[t + 3]);
            }
            StoreBe(p0, output, outOffset);
            StoreBe(p1, output, outOffset + 4);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            uint p0 = LoadBe(input, inOffset);
            uint p1 = LoadBe(input, inOffset + 4);
            // π1–π4 各自都是自逆的,解密就是把每一步倒序再做一遍。
            for (int g = Groups - 1; g >= 0; g--)
            {
                int t = (g & 1) * 4;
                Pi4(ref p0, ref p1, _uk[t + 3]);
                Pi3(ref p0, ref p1, _uk[t + 1], _uk[t + 2]);
                Pi2(ref p0, ref p1, _uk[t]);
                Pi1(ref p0, ref p1);
            }
            StoreBe(p0, output, outOffset);
            StoreBe(p1, output, outOffset + 4);
        }

        private static void Pi1(ref uint p0, ref uint p1)
        {
            p1 ^= p0;
        }

        private static void Pi2(ref uint p0, ref uint p1, uint k1)
        {
            unchecked
            {
                uint t = p1 + k1;
                t = Rol(t, 1) + t - 1;
                t = Rol(t, 4) ^ t;
                p0 ^= t;
            }
        }

        private static void Pi3(ref uint p0, ref uint p1, uint k2, uint k3)
        {
            unchecked
            {
                uint t = p0 + k2;
                t = Rol(t, 2) + t + 1;
                t = Rol(t, 8) ^ t;
                t += k3;
                t = Rol(t, 1) - t;
                t = Rol(t, 16) ^ (p0 | t);
                p1 ^= t;
            }
        }

        private static void Pi4(ref uint p0, ref uint p1, uint k4)
        {
            unchecked
            {
                uint t = p1 + k4;
                t = Rol(t, 2) + t + 1;
                p0 ^= t;
            }
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

        private static uint Rol(uint x, int n)
        {
            return (x << n) | (x >> (32 - n));
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
