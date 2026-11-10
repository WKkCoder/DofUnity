using System;
using System.Security.Cryptography;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 反机器人信封(类 0、包号 561)里密文区的变换:19 个算法(算法号 1 到 19),密钥由信封头里的序号 <c>seq</c> 与算法号派生。
    /// <para>
    /// 密钥派生(<see cref="DeriveKey"/>):32 字节 = <c>MD5(LE32(seq) ‖ LE32(algo))</c> 接 <c>MD5(LE32(seq) ‖ LE32(algo + 1))</c>。
    /// 派生本身不检查算法号范围(算法号越界时只是变换不可用)。
    /// </para>
    /// <para>
    /// 算法号:1 IDEA、2 DES 新、3 DES 大、4 CAST、5 TEAN、6 RC2、7 Twofish(密钥取前 32 字节)、8 Twofish(前 24)、9 MMB、10 NSEA、
    /// 11 Twofish(前 16)、12 Square、13 MARS、14 RC4、15 SEATTOS、16 GENE、17 RC6、18 RC5、19 GENE 新。
    /// 各算法对"整个缓冲"原地变换,长度不限于块长的整数倍:整块之后剩下的字节怎么处理,各算法不同(8 字节块的算法走一个共用的尾部搅动,
    /// 16 字节块的算法走 SEATTOS 流变换,流式算法本来就逐字节),细节见各算法类。长度 0 的缓冲原样返回。
    /// </para>
    /// <para>
    /// 约定(与目录里其它解析器同风格):不抛异常。算法号不在 1 到 19、数据为空引用、长度超过 <see cref="MaxDataLength"/>
    /// (原函数对超过 0xFFFF 字节的缓冲直接拒绝)时,<c>Try*</c> 返回 false 且结果为 null,<see cref="Encrypt"/>/<see cref="Decrypt"/> 返回 null。
    /// 返回的总是新数组,不改动传入的数组。
    /// </para>
    /// </summary>
    public static class AntibotCipher
    {
        /// <summary>最小合法算法号。</summary>
        public const int MinAlgorithm = 1;

        /// <summary>最大合法算法号。</summary>
        public const int MaxAlgorithm = 19;

        /// <summary>可处理的最大数据长度(字节)。</summary>
        public const int MaxDataLength = 0xFFFF;

        /// <summary>派生密钥的长度(字节)。</summary>
        public const int KeyLength = 32;

        /// <summary>算法号是否合法(1 到 19)。</summary>
        public static bool IsValidAlgorithm(int algo)
        {
            return algo >= MinAlgorithm && algo <= MaxAlgorithm;
        }

        /// <summary>
        /// 派生 32 字节密钥:<c>MD5(LE32(seq) ‖ LE32(algo)) ‖ MD5(LE32(seq) ‖ LE32(algo + 1))</c>(<c>algo + 1</c> 按 32 位回绕)。
        /// 任何算法号都能派生,不做范围检查。
        /// </summary>
        public static byte[] DeriveKey(uint seq, int algo)
        {
            byte[] key = new byte[KeyLength];
            byte[] block = new byte[8];
            WriteLittleEndian(block, 0, seq);
            using (MD5 md5 = MD5.Create())
            {
                WriteLittleEndian(block, 4, unchecked((uint)algo));
                Buffer.BlockCopy(md5.ComputeHash(block), 0, key, 0, 16);
                WriteLittleEndian(block, 4, unchecked((uint)algo + 1u));
                Buffer.BlockCopy(md5.ComputeHash(block), 0, key, 16, 16);
            }

            return key;
        }

        /// <summary>加密方向的变换;失败(算法号越界、数据为空引用或过长)返回 null,否则返回新数组。</summary>
        public static byte[] Encrypt(int algo, uint seq, byte[] data)
        {
            return TryTransform(algo, seq, data, true, out byte[] result) ? result : null;
        }

        /// <summary>解密方向的变换;失败(算法号越界、数据为空引用或过长)返回 null,否则返回新数组。</summary>
        public static byte[] Decrypt(int algo, uint seq, byte[] data)
        {
            return TryTransform(algo, seq, data, false, out byte[] result) ? result : null;
        }

        /// <summary>加密方向;成功返回 true 并给出新数组,失败返回 false 且 <paramref name="result"/> 为 null。</summary>
        public static bool TryEncrypt(int algo, uint seq, byte[] data, out byte[] result)
        {
            return TryTransform(algo, seq, data, true, out result);
        }

        /// <summary>解密方向;成功返回 true 并给出新数组,失败返回 false 且 <paramref name="result"/> 为 null。</summary>
        public static bool TryDecrypt(int algo, uint seq, byte[] data, out byte[] result)
        {
            return TryTransform(algo, seq, data, false, out result);
        }

        private static bool TryTransform(int algo, uint seq, byte[] data, bool encrypt, out byte[] result)
        {
            result = null;
            if (data == null || data.Length > MaxDataLength || !IsValidAlgorithm(algo))
            {
                return false;
            }

            byte[] buf = (byte[])data.Clone();
            byte[] key = DeriveKey(seq, algo);
            switch (algo)
            {
                case 1:
                    AntibotIdea.Transform(key, encrypt, buf);
                    break;
                case 2:
                    AntibotDesNew.Transform(key, encrypt, buf);
                    break;
                case 3:
                    AntibotDesBig.Transform(key, encrypt, buf);
                    break;
                case 4:
                    AntibotCast.Transform(key, encrypt, buf);
                    break;
                case 5:
                    AntibotTean.Transform(key, encrypt, buf);
                    break;
                case 6:
                    AntibotRc2.Transform(key, encrypt, buf);
                    break;
                case 7:
                    AntibotTwofish.Transform(key, 32, encrypt, buf);
                    break;
                case 8:
                    AntibotTwofish.Transform(key, 24, encrypt, buf);
                    break;
                case 9:
                    AntibotMmb.Transform(key, encrypt, buf);
                    break;
                case 10:
                    AntibotNsea.Transform(key, encrypt, buf);
                    break;
                case 11:
                    AntibotTwofish.Transform(key, 16, encrypt, buf);
                    break;
                case 12:
                    AntibotSquare.Transform(key, encrypt, buf);
                    break;
                case 13:
                    AntibotMars.Transform(key, encrypt, buf);
                    break;
                case 14:
                    AntibotRc4.Transform(key, encrypt, buf);
                    break;
                case 15:
                    AntibotSeattos.Transform(key, encrypt, buf);
                    break;
                case 16:
                    AntibotGene.Transform(key, encrypt, buf);
                    break;
                case 17:
                    AntibotRc6.Transform(key, encrypt, buf);
                    break;
                case 18:
                    AntibotRc5.Transform(key, encrypt, buf);
                    break;
                default:
                    AntibotGeneNew.Transform(key, encrypt, buf);
                    break;
            }

            result = buf;
            return true;
        }

        private static void WriteLittleEndian(byte[] dest, int offset, uint value)
        {
            dest[offset] = (byte)value;
            dest[offset + 1] = (byte)(value >> 8);
            dest[offset + 2] = (byte)(value >> 16);
            dest[offset + 3] = (byte)(value >> 24);
        }
    }
}
