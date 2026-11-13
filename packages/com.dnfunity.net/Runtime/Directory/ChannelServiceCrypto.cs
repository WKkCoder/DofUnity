using System;
using System.Security.Cryptography;
using Dnf.Net.Frame;

namespace Dnf.Net.ChannelDirectory
{
    /// <summary>
    /// Channel Service(7001)的加密与压缩变换:AES-128-ECB 零填充 + zlib 解压(上限 0x32000)。
    /// <para>
    /// 【观】<c>DNF.exe</c> <c>0x01157400</c>(PadMode=0 零填充)、<c>0x010fa49e</c>(初始 key=<c>"Neople"</c>)、
    /// <c>0x010f51d7..0x010f52a5</c>/<c>0x010f53b4..0x010f5441</c>(先 zlib 解压,校验长度为 16 的倍数,再 AES 解密)。
    /// </para>
    /// </summary>
    public static class ChannelServiceCrypto
    {
        /// <summary>将任意字节零填充到 16 字节倍数后做 AES-128-ECB 加密。</summary>
        public static byte[] EncryptAes128EcbZeroPadded(byte[] key16, byte[] plaintext, int minimumPaddedLength = 0)
        {
            ValidateKey(key16);
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (minimumPaddedLength < 0 || (minimumPaddedLength % ChannelServiceConstants.AesBlockSize) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumPaddedLength));
            }

            int blocks = (plaintext.Length + ChannelServiceConstants.AesBlockSize - 1) / ChannelServiceConstants.AesBlockSize;
            int paddedLen = Math.Max(minimumPaddedLength, Math.Max(ChannelServiceConstants.AesBlockSize, blocks * ChannelServiceConstants.AesBlockSize));
            byte[] padded = new byte[paddedLen];
            Buffer.BlockCopy(plaintext, 0, padded, 0, plaintext.Length);

            using (Aes aes = CreateAesEcb(key16))
            using (ICryptoTransform enc = aes.CreateEncryptor())
            {
                return enc.TransformFinalBlock(padded, 0, padded.Length);
            }
        }

        /// <summary>将长度为 16 倍数的密文做 AES-128-ECB 解密(<c>PaddingMode.None</c>,不剥离零填充)。</summary>
        public static bool TryDecryptAes128Ecb(byte[] key16, byte[] ciphertext, out byte[] plaintext)
        {
            ValidateKey(key16);
            plaintext = null;
            if (ciphertext == null || ciphertext.Length == 0 || (ciphertext.Length % ChannelServiceConstants.AesBlockSize) != 0)
            {
                return false;
            }
            using (Aes aes = CreateAesEcb(key16))
            using (ICryptoTransform dec = aes.CreateDecryptor())
            {
                plaintext = dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                return true;
            }
        }

        /// <summary>
        /// 按客户端接收链路顺序解出 0x09 / 0x03 载荷:先 zlib 解压(上限 <paramref name="maxUncompressedBytes"/>),
        /// 确认解压结果长度非零且为 16 的倍数,再做 AES-128-ECB 解密。
        /// </summary>
        public static bool TryInflateZlibAndDecrypt(
            byte[] key16,
            byte[] compressedBody,
            out byte[] plaintext,
            out string error,
            int maxUncompressedBytes = ChannelServiceConstants.MaxPayloadBytes)
        {
            ValidateKey(key16);
            plaintext = null;
            error = null;
            if (compressedBody == null || compressedBody.Length == 0 || compressedBody.Length > maxUncompressedBytes)
            {
                error = "压缩载荷长度无效";
                return false;
            }

            if (!ZlibInflate.TryInflate(compressedBody, maxUncompressedBytes, out byte[] inflated, out bool tooLarge))
            {
                error = tooLarge ? "zlib 解压超过上限" : "zlib 解压或 Adler-32 校验失败";
                return false;
            }

            if (inflated.Length == 0 || (inflated.Length % ChannelServiceConstants.AesBlockSize) != 0)
            {
                error = "解压结果长度不是 16 字节的倍数";
                return false;
            }

            if (!TryDecryptAes128Ecb(key16, inflated, out plaintext))
            {
                error = "AES-128-ECB 解密失败";
                return false;
            }

            return true;
        }

        private static Aes CreateAesEcb(byte[] key16)
        {
            Aes aes = Aes.Create();
            aes.KeySize = 128;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key16;
            return aes;
        }

        private static void ValidateKey(byte[] key16)
        {
            if (key16 == null) throw new ArgumentNullException(nameof(key16));
            if (key16.Length != ChannelServiceConstants.AesKeySize)
            {
                throw new ArgumentException("AES-128 密钥必须为 16 字节", nameof(key16));
            }
        }
    }
}
