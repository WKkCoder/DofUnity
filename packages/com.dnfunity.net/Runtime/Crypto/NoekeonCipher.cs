using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 8:Noekeon(服务端 CNCryptoNoekeon:Initialize 0x0809b6f0、Encrypt 0x0809b948、Decrypt 0x0809ba3a;槽表见 E-204)。
    /// 与公开 Noekeon 的关系:直接密钥模式、16 轮、轮常数、θ 与 γ 都与公开规格一致,字按大端装载;
    /// 差别只有一处——π1/π2 的第一个字旋转的是第 0 个字(1 位),公开规格旋转的是第 1 个字(1 位)。
    /// 推断(E-292):这与公开密码库 libtomcrypt 较早版本的行为相符(服务端二进制里没有版本字符串,只是依公开源码比对);为了和服务端逐位一致,这里保留这一行为
    /// (本机模拟器向量证实:按公开规格实现的结果与服务端不同,按这个差别实现的结果逐位一致,E-263)。
    /// 密钥片 16 字节全部参与。
    /// </summary>
    public sealed class NoekeonCipher : BlockCipher
    {
        /// <summary>公开算法常量:16 轮加一个收尾,共 17 个轮常数(Noekeon 规格)。</summary>
        private static readonly uint[] RoundConstants =
        {
            0x80, 0x1B, 0x36, 0x6C, 0xD8, 0xAB, 0x4D, 0x9A, 0x2F,
            0x5E, 0xBC, 0x63, 0xC6, 0x97, 0x35, 0x6A, 0xD4,
        };

        private const int Rounds = 16;

        // 加密用工作密钥(直接密钥模式:就是密钥本身)与解密用工作密钥(密钥先过一次全零密钥的 θ)。
        private readonly uint[] _k = new uint[4];
        private readonly uint[] _dk = new uint[4];
        private bool _keyed;

        public override int Slot => 8;

        public override int BlockSize => 16;

        public override int KeyLength => 16;

        public override void SetKey(byte[] key, int offset)
        {
            CheckRange(key, offset, KeyLength, nameof(key), nameof(offset));
            for (int i = 0; i < 4; i++)
            {
                _k[i] = LoadBe(key, offset + 4 * i);
                _dk[i] = _k[i];
            }
            // 解密工作密钥 = θ(全零密钥)作用在密钥上;θ 里没有密钥加法这一项。
            uint a = _dk[0], b = _dk[1], c = _dk[2], d = _dk[3];
            Theta(null, ref a, ref b, ref c, ref d);
            _dk[0] = a;
            _dk[1] = b;
            _dk[2] = c;
            _dk[3] = d;
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            uint a = LoadBe(input, inOffset);
            uint b = LoadBe(input, inOffset + 4);
            uint c = LoadBe(input, inOffset + 8);
            uint d = LoadBe(input, inOffset + 12);
            for (int r = 0; r < Rounds; r++)
            {
                a ^= RoundConstants[r];
                Theta(_k, ref a, ref b, ref c, ref d);
                Pi1(ref a, ref c, ref d);
                Gamma(ref a, ref b, ref c, ref d);
                Pi2(ref a, ref c, ref d);
            }
            a ^= RoundConstants[Rounds];
            Theta(_k, ref a, ref b, ref c, ref d);
            StoreBe(a, output, outOffset);
            StoreBe(b, output, outOffset + 4);
            StoreBe(c, output, outOffset + 8);
            StoreBe(d, output, outOffset + 12);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            CheckBlock(input, inOffset, output, outOffset);
            uint a = LoadBe(input, inOffset);
            uint b = LoadBe(input, inOffset + 4);
            uint c = LoadBe(input, inOffset + 8);
            uint d = LoadBe(input, inOffset + 12);
            for (int r = Rounds; r > 0; r--)
            {
                Theta(_dk, ref a, ref b, ref c, ref d);
                a ^= RoundConstants[r];
                Pi1(ref a, ref c, ref d);
                Gamma(ref a, ref b, ref c, ref d);
                Pi2(ref a, ref c, ref d);
            }
            Theta(_dk, ref a, ref b, ref c, ref d);
            a ^= RoundConstants[0];
            StoreBe(a, output, outOffset);
            StoreBe(b, output, outOffset + 4);
            StoreBe(c, output, outOffset + 8);
            StoreBe(d, output, outOffset + 12);
        }

        /// <summary>θ:先把 a^c 做 8 位循环扩散混进 b、d,再把 b^d 的扩散混进 a、c;k 为 null 表示全零密钥。</summary>
        private static void Theta(uint[] k, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint t = a ^ c;
            t ^= Rol(t, 8) ^ Ror(t, 8);
            b ^= t;
            d ^= t;
            if (k != null)
            {
                b ^= k[1];
                d ^= k[3];
            }
            t = b ^ d;
            t ^= Rol(t, 8) ^ Ror(t, 8);
            a ^= t;
            c ^= t;
            if (k != null)
            {
                a ^= k[0];
                c ^= k[2];
            }
        }

        /// <summary>γ:Noekeon 的非线性层(与 NOR/AND 组合,中间交换 a、d)。</summary>
        private static void Gamma(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            b ^= ~(d | c);
            a ^= c & b;
            uint t = d;
            d = a;
            a = t;
            c ^= a ^ b ^ d;
            b ^= ~(d | c);
            a ^= c & b;
        }

        /// <summary>π1:循环左移。服务端的行为是移第 0 个字 1 位(公开规格移第 1 个字),见类注释。</summary>
        private static void Pi1(ref uint a, ref uint c, ref uint d)
        {
            a = Rol(a, 1);
            c = Rol(c, 5);
            d = Rol(d, 2);
        }

        /// <summary>π2:π1 的逆(循环右移)。</summary>
        private static void Pi2(ref uint a, ref uint c, ref uint d)
        {
            a = Ror(a, 1);
            c = Ror(c, 5);
            d = Ror(d, 2);
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

        private static uint Ror(uint x, int n)
        {
            return (x >> n) | (x << (32 - n));
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
