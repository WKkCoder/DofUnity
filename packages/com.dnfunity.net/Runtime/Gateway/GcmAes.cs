using System;
using System.Security.Cryptography;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// AES-256-GCM(12 字节 nonce、16 字节标签、AAD 可选)。netstandard2.1 / Unity 没有 AesGcm,这里只借 <see cref="Aes"/> 的 ECB 单块加密自己拼:
    /// 定义见 McGrew &amp; Viega《The Galois/Counter Mode of Operation (GCM)》§2.3–2.5:
    /// H = E(K,0);J0 = nonce ‖ 00000001;计数从 inc32(J0) 起;GHASH 在 GF(2^128) 上用多项式 x^128+x^7+x^2+x+1(算法 1);
    /// 标签 = GHASH(H,A,C) ⊕ E(K,J0)。公开向量与 .NET AesGcm 的对拍在 Dnf.Net.Tests 里。
    /// 输出布局为"密文 ‖ 标签"。
    /// </summary>
    public static class GcmAes
    {
        /// <summary>密钥长度(字节):AES-256。</summary>
        public const int KeySize = 32;

        /// <summary>nonce 长度(字节):96 位,走 J0 = nonce ‖ 1 的快路径。</summary>
        public const int NonceSize = 12;

        /// <summary>标签长度(字节)。</summary>
        public const int TagSize = 16;

        private const int BlockSize = 16;

        /// <summary>一次最多生成这么多块的密钥流,限制长明文时的内存。</summary>
        private const int CounterChunkBlocks = 1024;

        /// <summary>加密并认证;返回 密文 ‖ 标签(长度 = 明文长度 + 16)。key/nonce 长度不对属于调用方错误,抛 <see cref="ArgumentException"/>。</summary>
        public static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad = null)
        {
            CheckKeyAndNonce(key, nonce);
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }
            byte[] ad = aad ?? new byte[0];
            byte[] output = new byte[plaintext.Length + TagSize];
            using (Aes aes = CreateAes(key))
            using (ICryptoTransform enc = aes.CreateEncryptor())
            {
                CtrXor(enc, nonce, plaintext, output, plaintext.Length);
                byte[] tag = ComputeTag(enc, nonce, ad, output, plaintext.Length);
                Buffer.BlockCopy(tag, 0, output, plaintext.Length, TagSize);
            }
            return output;
        }

        /// <summary>
        /// 验证并解密 <paramref name="sealedData"/>(密文 ‖ 标签)。认证失败、输入短于 16 字节、输入为 null 都返回 false(不抛),
        /// 且先验标签后解密,验证失败时不产生任何明文。key/nonce 长度不对属于调用方错误,抛 <see cref="ArgumentException"/>:
        /// 这两个值不来自网络,把它们的错误伪装成"认证失败"会掩盖配置问题。
        /// </summary>
        public static bool TryOpen(byte[] key, byte[] nonce, byte[] sealedData, out byte[] plaintext, byte[] aad = null)
        {
            plaintext = null;
            CheckKeyAndNonce(key, nonce);
            if (sealedData == null || sealedData.Length < TagSize)
            {
                return false;
            }
            byte[] ad = aad ?? new byte[0];
            int cipherLength = sealedData.Length - TagSize;
            using (Aes aes = CreateAes(key))
            using (ICryptoTransform enc = aes.CreateEncryptor())
            {
                byte[] expected = ComputeTag(enc, nonce, ad, sealedData, cipherLength);
                byte[] received = new byte[TagSize];
                Buffer.BlockCopy(sealedData, cipherLength, received, 0, TagSize);
                bool authentic = FixedTimeEquals(expected, received);
                Array.Clear(expected, 0, expected.Length);
                if (!authentic)
                {
                    return false;
                }
                byte[] plain = new byte[cipherLength];
                CtrXor(enc, nonce, sealedData, plain, cipherLength);
                plaintext = plain;
                return true;
            }
        }

        /// <summary>常量时间比较:长度相同时遍历全部字节,不因第一个差异提前返回;长度不同直接 false(长度本身不是秘密)。</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static void CheckKeyAndNonce(byte[] key, byte[] nonce)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (nonce == null)
            {
                throw new ArgumentNullException(nameof(nonce));
            }
            if (key.Length != KeySize)
            {
                throw new ArgumentException("key must be " + KeySize + " bytes (AES-256)");
            }
            if (nonce.Length != NonceSize)
            {
                throw new ArgumentException("nonce must be " + NonceSize + " bytes");
            }
        }

        private static Aes CreateAes(byte[] key)
        {
            Aes aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            return aes;
        }

        /// <summary>CTR 加解密(同一个运算):第 i 块用 E(K, nonce ‖ (i+2)),计数 32 位大端、从 2 起(1 留给 J0)。</summary>
        private static void CtrXor(ICryptoTransform enc, byte[] nonce, byte[] input, byte[] output, int length)
        {
            if (length == 0)
            {
                return;
            }
            int chunkBlocks = Math.Min((length + BlockSize - 1) / BlockSize, CounterChunkBlocks);
            byte[] counters = new byte[chunkBlocks * BlockSize];
            byte[] stream = new byte[counters.Length];
            uint counter = 2;
            int done = 0;
            while (done < length)
            {
                int remaining = length - done;
                int blocks = Math.Min((remaining + BlockSize - 1) / BlockSize, CounterChunkBlocks);
                for (int b = 0; b < blocks; b++)
                {
                    int o = b * BlockSize;
                    Buffer.BlockCopy(nonce, 0, counters, o, NonceSize);
                    counters[o + 12] = (byte)(counter >> 24);
                    counters[o + 13] = (byte)(counter >> 16);
                    counters[o + 14] = (byte)(counter >> 8);
                    counters[o + 15] = (byte)counter;
                    counter = unchecked(counter + 1);
                }
                enc.TransformBlock(counters, 0, blocks * BlockSize, stream, 0);
                int n = Math.Min(remaining, blocks * BlockSize);
                for (int i = 0; i < n; i++)
                {
                    output[done + i] = (byte)(input[done + i] ^ stream[i]);
                }
                done += n;
            }
            Array.Clear(stream, 0, stream.Length);
            Array.Clear(counters, 0, counters.Length);
        }

        /// <summary>标签 = GHASH(H, AAD, 密文) ⊕ E(K, J0)。密文取自 <paramref name="cipher"/> 的开头 <paramref name="cipherLength"/> 字节。</summary>
        private static byte[] ComputeTag(ICryptoTransform enc, byte[] nonce, byte[] aad, byte[] cipher, int cipherLength)
        {
            byte[] block = new byte[BlockSize];
            byte[] hash = new byte[BlockSize];
            enc.TransformBlock(block, 0, BlockSize, hash, 0);
            ulong hHi = ReadBigEndian64(hash, 0);
            ulong hLo = ReadBigEndian64(hash, 8);
            Array.Clear(hash, 0, hash.Length);

            ulong yHi = 0;
            ulong yLo = 0;
            GhashUpdate(ref yHi, ref yLo, hHi, hLo, aad, aad.Length);
            GhashUpdate(ref yHi, ref yLo, hHi, hLo, cipher, cipherLength);
            // 最后一块:len(A) ‖ len(C),各 64 位大端、单位是比特
            yHi ^= (ulong)aad.Length * 8UL;
            yLo ^= (ulong)cipherLength * 8UL;
            GfMultiply(ref yHi, ref yLo, hHi, hLo);

            Buffer.BlockCopy(nonce, 0, block, 0, NonceSize);
            block[12] = 0;
            block[13] = 0;
            block[14] = 0;
            block[15] = 1;
            byte[] maskBlock = new byte[BlockSize];
            enc.TransformBlock(block, 0, BlockSize, maskBlock, 0);
            byte[] tag = new byte[TagSize];
            WriteBigEndian64(tag, 0, yHi ^ ReadBigEndian64(maskBlock, 0));
            WriteBigEndian64(tag, 8, yLo ^ ReadBigEndian64(maskBlock, 8));
            Array.Clear(maskBlock, 0, maskBlock.Length);
            return tag;
        }

        /// <summary>把 data 的前 length 字节(不足一块的尾部补零)逐块并入 GHASH 累加器:Y = (Y ⊕ X_i) · H。</summary>
        private static void GhashUpdate(ref ulong yHi, ref ulong yLo, ulong hHi, ulong hLo, byte[] data, int length)
        {
            int full = length / BlockSize;
            for (int i = 0; i < full; i++)
            {
                yHi ^= ReadBigEndian64(data, i * BlockSize);
                yLo ^= ReadBigEndian64(data, i * BlockSize + 8);
                GfMultiply(ref yHi, ref yLo, hHi, hLo);
            }
            int rest = length - full * BlockSize;
            if (rest > 0)
            {
                byte[] last = new byte[BlockSize];
                Buffer.BlockCopy(data, full * BlockSize, last, 0, rest);
                yHi ^= ReadBigEndian64(last, 0);
                yLo ^= ReadBigEndian64(last, 8);
                GfMultiply(ref yHi, ref yLo, hHi, hLo);
            }
        }

        /// <summary>
        /// GF(2^128) 乘法(算法 1):X 为 (xHi, xLo)(大端,第 0 位是最高位),结果写回 X。
        /// 用掩码代替分支,运行时间不依赖 X 与 H 的内容;V 右移一位,移出的最低位为 1 时异或 R = 11100001 ‖ 0^120。
        /// </summary>
        private static void GfMultiply(ref ulong xHi, ref ulong xLo, ulong hHi, ulong hLo)
        {
            ulong zHi = 0;
            ulong zLo = 0;
            ulong vHi = hHi;
            ulong vLo = hLo;
            for (int i = 0; i < 128; i++)
            {
                ulong bit = i < 64 ? (xHi >> (63 - i)) & 1UL : (xLo >> (127 - i)) & 1UL;
                ulong take = unchecked(0UL - bit);
                zHi ^= vHi & take;
                zLo ^= vLo & take;
                ulong carry = vLo & 1UL;
                vLo = (vLo >> 1) | (vHi << 63);
                vHi >>= 1;
                vHi ^= unchecked(0UL - carry) & 0xE100000000000000UL;
            }
            xHi = zHi;
            xLo = zLo;
        }

        private static ulong ReadBigEndian64(byte[] data, int offset)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++)
            {
                v = (v << 8) | data[offset + i];
            }
            return v;
        }

        private static void WriteBigEndian64(byte[] data, int offset, ulong value)
        {
            for (int i = 7; i >= 0; i--)
            {
                data[offset + i] = (byte)value;
                value >>= 8;
            }
        }
    }
}
