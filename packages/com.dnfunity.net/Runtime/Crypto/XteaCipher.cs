using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 7:XTEA(服务端 CNCryptoXTea:Initialize 0x0809da20、Encrypt 0x0809dc78、Decrypt 0x0809dd6a;槽表见 E-204)。
    /// 与公开 XTEA 的关系:轮函数、32 轮、delta 0x9E3779B9 都一致;唯一的差别是字节序——密钥与数据块都按小端装成 32 位字。
    /// 推断(E-292):与 libtomcrypt 较早的公开版本相符,较新的公开版本改成了大端,同一把密钥会得到不同的密文;版本对应只是依公开源码比对,服务端二进制里没有版本字符串。
    /// 密钥片 16 字节全部参与(本机模拟器探测:任一字节翻转都会改变密文,E-263)。
    /// </summary>
    public sealed class XteaCipher : BlockCipher
    {
        /// <summary>公开 XTEA 的轮数(每轮含两个半轮)。</summary>
        private const int Rounds = 32;

        /// <summary>公开 XTEA 的 delta(黄金分割常数)。</summary>
        private const uint Delta = 0x9E3779B9u;

        private readonly uint[] _k = new uint[4];
        private bool _keyed;

        public override int Slot => 7;

        public override int BlockSize => 8;

        public override int KeyLength => 16;

        public override void SetKey(byte[] key, int offset)
        {
            CheckRange(key, offset, KeyLength, nameof(key), nameof(offset));
            // 小端装字:服务端的行为,见类注释。
            for (int i = 0; i < 4; i++)
            {
                _k[i] = LoadLe(key, offset + 4 * i);
            }
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            unchecked
            {
                uint y = LoadLe(input, inOffset);
                uint z = LoadLe(input, inOffset + 4);
                uint sum = 0;
                for (int r = 0; r < Rounds; r++)
                {
                    y += (((z << 4) ^ (z >> 5)) + z) ^ (sum + _k[sum & 3]);
                    sum += Delta;
                    z += (((y << 4) ^ (y >> 5)) + y) ^ (sum + _k[(sum >> 11) & 3]);
                }
                StoreLe(y, output, outOffset);
                StoreLe(z, output, outOffset + 4);
            }
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            unchecked
            {
                uint y = LoadLe(input, inOffset);
                uint z = LoadLe(input, inOffset + 4);
                uint sum = Delta * Rounds;
                for (int r = 0; r < Rounds; r++)
                {
                    z -= (((y << 4) ^ (y >> 5)) + y) ^ (sum + _k[(sum >> 11) & 3]);
                    sum -= Delta;
                    y -= (((z << 4) ^ (z >> 5)) + z) ^ (sum + _k[sum & 3]);
                }
                StoreLe(y, output, outOffset);
                StoreLe(z, output, outOffset + 4);
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

        private static uint LoadLe(byte[] b, int i)
        {
            return unchecked((uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)));
        }

        private static void StoreLe(uint v, byte[] b, int i)
        {
            b[i] = (byte)v;
            b[i + 1] = (byte)(v >> 8);
            b[i + 2] = (byte)(v >> 16);
            b[i + 3] = (byte)(v >> 24);
        }
    }
}
