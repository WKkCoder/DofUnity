namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 3：CDesBig，与单 DES、8 字节密钥的 ECB 逐块加密一致；仅使用 key32 前 8 字节。
    /// 整块循环见 <see cref="AntibotStandardDes"/>；尾部钩子 keylen=8。
    /// </summary>
    internal static class AntibotDesBig
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            ulong[] subkeys = AntibotStandardDes.ExpandKey(key32);
            int n = buf.Length >> 3;
            for (int i = 0; i < n; i++)
            {
                AntibotStandardDes.CryptBlock(buf, i << 3, subkeys, encrypt);
            }

            AntibotTail.ProcessLastBytes(key32, 8, encrypt, buf);
        }
    }

    /// <summary>标准 DES ECB 单块（与 CDesBig.des_ec / des_dc 一致）。</summary>
    internal static class AntibotStandardDes
    {
        private static readonly byte[] Ip =
        {
            58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
            62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
            57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
            61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7
        };

        private static readonly byte[] Fp =
        {
            40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
            38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
            36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
            34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25
        };

        private static readonly byte[] Pc1 =
        {
            57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2,
            59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36, 63, 55, 47, 39,
            31, 23, 15, 7, 62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37,
            29, 21, 13, 5, 28, 20, 12, 4
        };

        private static readonly byte[] Pc2 =
        {
            14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4,
            26, 8, 16, 7, 27, 20, 13, 2, 41, 52, 31, 37, 47, 55, 30, 40,
            51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32
        };

        private static readonly byte[] Rotations = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };

        private static readonly byte[] E =
        {
            32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13,
            12, 13, 14, 15, 16, 17, 16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25,
            24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1
        };

        private static readonly byte[] P =
        {
            16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
            2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25
        };

        private static readonly byte[,] Sbox =
        {
            {
                14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
                0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
                4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
                15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13
            },
            {
                15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
                3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
                0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
                13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9
            },
            {
                10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
                13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
                13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
                1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12
            },
            {
                7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
                13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
                10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
                3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14
            },
            {
                2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
                14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
                4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
                11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3
            },
            {
                12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
                10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
                9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
                4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13
            },
            {
                4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
                13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
                1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
                6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12
            },
            {
                13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
                1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
                7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
                2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11
            }
        };

        internal static ulong[] ExpandKey(byte[] key32)
        {
            ulong cd = 0;
            ulong keyBits = 0;
            for (int i = 0; i < 8; i++)
            {
                keyBits = (keyBits << 8) | key32[i];
            }

            for (int i = 0; i < 56; i++)
            {
                cd = (cd << 1) | ((keyBits >> (64 - Pc1[i])) & 1);
            }

            ulong[] sk = new ulong[16];
            uint c = (uint)(cd >> 28);
            uint d = (uint)(cd & 0xfffffff);
            for (int round = 0; round < 16; round++)
            {
                int n = Rotations[round];
                c = ((c << n) | (c >> (28 - n))) & 0xfffffff;
                d = ((d << n) | (d >> (28 - n))) & 0xfffffff;
                cd = ((ulong)c << 28) | d;
                ulong sub = 0;
                for (int i = 0; i < 48; i++)
                {
                    sub = (sub << 1) | ((cd >> (56 - Pc2[i])) & 1);
                }

                sk[round] = sub;
            }

            return sk;
        }

        internal static void CryptBlock(byte[] buf, int off, ulong[] subkeys, bool encrypt)
        {
            ulong block = 0;
            for (int i = 0; i < 8; i++)
            {
                block = (block << 8) | buf[off + i];
            }

            ulong bits = Permute(block, Ip, 64);
            uint l = (uint)(bits >> 32);
            uint r = (uint)(bits & 0xffffffff);
            for (int round = 0; round < 16; round++)
            {
                int ri = encrypt ? round : 15 - round;
                uint f = Feistel(r, subkeys[ri]);
                uint nl = r;
                r = l ^ f;
                l = nl;
            }

            bits = ((ulong)r << 32) | l;
            bits = Permute(bits, Fp, 64);
            for (int i = 7; i >= 0; i--)
            {
                buf[off + i] = (byte)(bits & 0xff);
                bits >>= 8;
            }
        }

        private static uint Feistel(uint r, ulong subkey)
        {
            ulong er = 0;
            for (int i = 0; i < 48; i++)
            {
                er = (er << 1) | ((ulong)((r >> (32 - E[i])) & 1));
            }

            er ^= subkey;
            uint outBits = 0;
            for (int box = 0; box < 8; box++)
            {
                int val = (int)((er >> (42 - box * 6)) & 0x3f);
                int row = ((val >> 5) << 1) | (val & 1);
                int col = (val >> 1) & 0xf;
                int s = Sbox[box, row * 16 + col];
                outBits = (outBits << 4) | (uint)s;
            }

            uint p = 0;
            for (int i = 0; i < 32; i++)
            {
                p = (p << 1) | ((outBits >> (32 - P[i])) & 1);
            }

            return p;
        }

        private static ulong Permute(ulong inBits, byte[] table, int nOut)
        {
            ulong outBits = 0;
            for (int i = 0; i < nOut; i++)
            {
                outBits = (outBits << 1) | ((inBits >> (64 - table[i])) & 1);
            }

            return outBits;
        }
    }
}
