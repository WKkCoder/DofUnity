namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 2：CDesNew，8 字节块、自定义 256 字节 S 盒与 16 步 Feistel 式 XOR 网络（非标准 DES）；密钥取 key32 前 15 字节。
    /// 与公开 DES/3DES 无关。尾部钩子 keylen=15。
    /// </summary>
    internal static class AntibotDesNew
    {
        private const int TailKeyBytes = 15;

        private static readonly byte[] Sbox =
        {
            0x20, 0x89, 0xef, 0xbc, 0x66, 0x7d, 0xdd, 0x48, 0xd4, 0x44, 0x51, 0x25, 0x56, 0xed, 0x93, 0x95,
            0x46, 0xe5, 0x11, 0x7c, 0x73, 0xcf, 0x21, 0x14, 0x7a, 0x8f, 0x19, 0xd7, 0x33, 0xb7, 0x8a, 0x8e,
            0x92, 0xd3, 0x6e, 0xad, 0x01, 0xe4, 0xbd, 0x0e, 0x67, 0x4e, 0xa2, 0x24, 0xfd, 0xa7, 0x74, 0xff,
            0x9e, 0x2d, 0xb9, 0x32, 0x62, 0xa8, 0xfa, 0xeb, 0x36, 0x8d, 0xc3, 0xf7, 0xf0, 0x3f, 0x94, 0x02,
            0xe0, 0xa9, 0xd6, 0xb4, 0x3e, 0x16, 0x75, 0x6c, 0x13, 0xac, 0xa1, 0x9f, 0xa0, 0x2f, 0x2b, 0xab,
            0xc2, 0xaf, 0xb2, 0x38, 0xc4, 0x70, 0x17, 0xdc, 0x59, 0x15, 0xa4, 0x82, 0x9d, 0x08, 0x55, 0xfb,
            0xd8, 0x2c, 0x5e, 0xb3, 0xe2, 0x26, 0x5a, 0x77, 0x28, 0xca, 0x22, 0xce, 0x23, 0x45, 0xe7, 0xf6,
            0x1d, 0x6d, 0x4a, 0x47, 0xb0, 0x06, 0x3c, 0x91, 0x41, 0x0d, 0x4d, 0x97, 0x0c, 0x7f, 0x5f, 0xc7,
            0x39, 0x65, 0x05, 0xe8, 0x96, 0xd2, 0x81, 0x18, 0xb5, 0x0a, 0x79, 0xbb, 0x30, 0xc1, 0x8b, 0xfc,
            0xdb, 0x40, 0x58, 0xe9, 0x60, 0x80, 0x50, 0x35, 0xbf, 0x90, 0xda, 0x0b, 0x6a, 0x84, 0x9b, 0x68,
            0x5b, 0x88, 0x1f, 0x2a, 0xf3, 0x42, 0x7e, 0x87, 0x1e, 0x1a, 0x57, 0xba, 0xb6, 0x9a, 0xf2, 0x7b,
            0x52, 0xa6, 0xd0, 0x27, 0x98, 0xbe, 0x71, 0xcd, 0x72, 0x69, 0xe1, 0x54, 0x49, 0xa3, 0x63, 0x6f,
            0xcc, 0x3d, 0xc8, 0xd9, 0xaa, 0x0f, 0xc6, 0x1c, 0xc0, 0xfe, 0x86, 0xea, 0xde, 0x07, 0xec, 0xf8,
            0xc9, 0x29, 0xb1, 0x9c, 0x5c, 0x83, 0x43, 0xf9, 0xf5, 0xb8, 0xcb, 0x09, 0xf1, 0x00, 0x1b, 0x2e,
            0x85, 0xae, 0x4b, 0x12, 0x5d, 0xd1, 0x64, 0x78, 0x4c, 0xd5, 0x10, 0x53, 0x04, 0x6b, 0x8c, 0x34,
            0x3a, 0x37, 0x03, 0xf4, 0x61, 0xc5, 0xee, 0xe3, 0x76, 0x31, 0x4f, 0xe6, 0xdf, 0xa5, 0x99, 0x3b,
        };

        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            int n = buf.Length >> 3;
            for (int i = 0; i < n; i++)
            {
                int off = i << 3;
                if (encrypt)
                {
                    EncryptBlock(key32, buf, off);
                }
                else
                {
                    DecryptBlock(key32, buf, off);
                }
            }

            AntibotTail.ProcessLastBytes(key32, TailKeyBytes, encrypt, buf);
        }

        private static void EncryptBlock(byte[] key, byte[] buf, int off)
        {
            int esi = 0;
            int r = 0;
            while (true)
            {
                XorSbox(buf, off, 0, key, r, esi, 4);
                if (r + 1 == 15)
                {
                    esi = key[7];
                    r = 1;
                    XorSbox(buf, off, 1, key, r - 1, esi, 5);
                    r++;
                    XorSbox(buf, off, 2, key, r - 1, esi, 6);
                    r++;
                    XorSbox(buf, off, 3, key, r - 1, esi, 7);
                    if (r == 15)
                    {
                        return;
                    }

                    r = TailEnc(buf, off, key, r, esi);
                    continue;
                }

                r += 2;
                XorSbox(buf, off, 1, key, r - 1, esi, 5);
                if (r == 15)
                {
                    esi = key[8];
                    r = 1;
                    XorSbox(buf, off, 2, key, r - 1, esi, 6);
                    r++;
                    XorSbox(buf, off, 3, key, r - 1, esi, 7);
                    if (r == 15)
                    {
                        return;
                    }

                    r = TailEnc(buf, off, key, r, esi);
                    continue;
                }

                r++;
                XorSbox(buf, off, 2, key, r - 1, esi, 6);
                if (r == 15)
                {
                    esi = key[9];
                    r = 1;
                    XorSbox(buf, off, 3, key, r - 1, esi, 7);
                    r = TailEnc(buf, off, key, r, esi);
                    continue;
                }

                r++;
                XorSbox(buf, off, 3, key, r - 1, esi, 7);
                if (r == 15)
                {
                    return;
                }

                r = TailEnc(buf, off, key, r, esi);
            }
        }

        private static int TailEnc(byte[] buf, int off, byte[] key, int h, int esi)
        {
            XorSbox(buf, off, 4, key, h, esi, 1);
            XorSboxPair(buf, off, 4, 5, 2);
            XorSbox(buf, off, 6, key, h + 1, esi, 3);
            XorSbox(buf, off, 7, key, h + 2, esi, 0);
            return h + 3;
        }

        private static void DecryptBlock(byte[] key, byte[] buf, int off)
        {
            int esi = key[9];
            int r = 14;
            while (true)
            {
                XorSbox(buf, off, 3, key, r, esi, 7);
                bool skip;
                if (r - 1 < 0)
                {
                    esi = key[8];
                    r = 13;
                    skip = true;
                }
                else
                {
                    skip = false;
                }

                if (!skip)
                {
                    r -= 2;
                    XorSbox(buf, off, 2, key, r + 1, esi, 6);
                    if (r < 0)
                    {
                        esi = key[7];
                        r = 13;
                        XorSbox(buf, off, 1, key, 14, esi, 5);
                        r--;
                        XorSbox(buf, off, 0, key, r + 1, esi, 4);
                        if (r < 0)
                        {
                            return;
                        }

                        r = TailDec(buf, off, key, r, esi);
                        continue;
                    }

                    r--;
                    XorSbox(buf, off, 1, key, r + 1, esi, 5);
                    if (r < 0)
                    {
                        esi = 0;
                        r = 13;
                        XorSbox(buf, off, 0, key, 14, esi, 4);
                        if (r < 0)
                        {
                            return;
                        }

                        r = TailDec(buf, off, key, r, esi);
                        continue;
                    }

                    r--;
                    XorSbox(buf, off, 0, key, r + 1, esi, 4);
                    if (r < 0)
                    {
                        return;
                    }
                }
                else
                {
                    XorSbox(buf, off, 2, key, r + 1, esi, 6);
                    r--;
                    XorSbox(buf, off, 1, key, r + 1, esi, 5);
                    if (r < 0)
                    {
                        esi = 0;
                        r = 13;
                        XorSbox(buf, off, 0, key, 14, esi, 4);
                        if (r < 0)
                        {
                            return;
                        }

                        r = TailDec(buf, off, key, r, esi);
                        continue;
                    }

                    r--;
                    XorSbox(buf, off, 0, key, r + 1, esi, 4);
                    if (r < 0)
                    {
                        return;
                    }
                }

                r = TailDec(buf, off, key, r, esi);
            }
        }

        private static int TailDec(byte[] buf, int off, byte[] key, int h, int esi)
        {
            XorSbox(buf, off, 7, key, h, esi, 0);
            XorSbox(buf, off, 6, key, h - 1, esi, 3);
            XorSboxPair(buf, off, 4, 5, 2);
            XorSbox(buf, off, 4, key, h - 2, esi, 1);
            return h - 3;
        }

        private static byte KeyAt(byte[] key, int index)
        {
            index %= 15;
            if (index < 0)
            {
                index += 15;
            }

            return key[index];
        }

        private static void XorSbox(byte[] buf, int off, int src, byte[] key, int ki, int esi, int dst)
        {
            int x = (buf[off + src] ^ KeyAt(key, ki) ^ esi) & 0xff;
            buf[off + dst] ^= Sbox[x];
        }

        private static void XorSboxPair(byte[] buf, int off, int a, int c, int dst)
        {
            int x = (buf[off + a] ^ buf[off + c]) & 0xff;
            buf[off + dst] ^= Sbox[x];
        }
    }
}
