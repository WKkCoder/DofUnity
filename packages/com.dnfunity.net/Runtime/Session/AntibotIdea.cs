namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 1:IDEA(64 位块、128 位密钥);块内 16 位字自缓冲按大端读入、按小端写出,
    /// 写出时中间两字 x1/x2 交换位置(与 0x1e5680 末置换一致);密钥扩展为 0x1e53b0 滑动窗口语义;
    /// 解密密钥 0x1e5420 子表排列。尾部 keylen=16。
    /// </summary>
    internal static class AntibotIdea
    {
        private const int TailKeyBytes = 16;
        private const int Modulus = 0x10001;

        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            ushort[] encSub = new ushort[52];
            ExpandKey(key32, encSub);
            ushort[] sub = encSub;
            if (!encrypt)
            {
                ushort[] decSub = new ushort[52];
                InvertSubkeys(encSub, decSub);
                sub = decSub;
            }

            int blocks = buf.Length >> 3;
            for (int b = 0; b < blocks; b++)
            {
                int o = b * 8;
                ushort x0;
                ushort x1;
                ushort x2;
                ushort x3;
                if (encrypt)
                {
                    x0 = (ushort)((buf[o] << 8) | buf[o + 1]);
                    x1 = (ushort)((buf[o + 2] << 8) | buf[o + 3]);
                    x2 = (ushort)((buf[o + 4] << 8) | buf[o + 5]);
                    x3 = (ushort)((buf[o + 6] << 8) | buf[o + 7]);
                }
                else
                {
                    x0 = (ushort)(buf[o] | (buf[o + 1] << 8));
                    x1 = (ushort)(buf[o + 2] | (buf[o + 3] << 8));
                    x2 = (ushort)(buf[o + 4] | (buf[o + 5] << 8));
                    x3 = (ushort)(buf[o + 6] | (buf[o + 7] << 8));
                }

                CipherBlock(ref x0, ref x1, ref x2, ref x3, sub);
                if (encrypt)
                {
                    StoreLe16(buf, o, x0);
                    StoreLe16(buf, o + 2, x2);
                    StoreLe16(buf, o + 4, x1);
                    StoreLe16(buf, o + 6, x3);
                }
                else
                {
                    buf[o] = (byte)(x0 >> 8);
                    buf[o + 1] = (byte)x0;
                    buf[o + 2] = (byte)(x2 >> 8);
                    buf[o + 3] = (byte)x2;
                    buf[o + 4] = (byte)(x1 >> 8);
                    buf[o + 5] = (byte)x1;
                    buf[o + 6] = (byte)(x3 >> 8);
                    buf[o + 7] = (byte)x3;
                }
            }

            AntibotTail.ProcessLastBytes(key32, TailKeyBytes, encrypt, buf);
        }

        private static void CipherBlock(ref ushort x0, ref ushort x1, ref ushort x2, ref ushort x3, ushort[] k)
        {
            int p = 0;
            for (int r = 0; r < 8; r++)
            {
                x0 = Mul(x0, k[p++]);
                x1 = (ushort)(x1 + k[p++]);
                x2 = (ushort)(x2 + k[p++]);
                x3 = Mul(x3, k[p++]);

                ushort t0 = (ushort)(x0 ^ x2);
                ushort t1 = (ushort)(x1 ^ x3);
                t0 = Mul(t0, k[p++]);
                t1 = (ushort)(t1 + t0);
                t1 = Mul(t1, k[p++]);
                t0 = (ushort)(t0 + t1);

                x0 = (ushort)(x0 ^ t1);
                x3 = (ushort)(x3 ^ t0);
                t0 = (ushort)(t0 ^ x1);
                x1 = (ushort)(t1 ^ x2);
                x2 = t0;
            }

            x0 = Mul(x0, k[p++]);
            x2 = (ushort)(x2 + k[p++]);
            x1 = (ushort)(x1 + k[p++]);
            x3 = Mul(x3, k[p]);
        }

        private static void ExpandKey(byte[] key, ushort[] ek)
        {
            for (int i = 0; i < 8; i++)
            {
                ek[i] = (ushort)(key[2 * i] | (key[2 * i + 1] << 8));
            }

            int eax = 0;
            for (int idx = 8; idx < 52; idx++)
            {
                int ecx = eax + 1;
                int t0;
                int t1;
                if (idx < 16)
                {
                    t0 = (eax + 2) & 7;
                    t1 = ecx & 7;
                }
                else if (idx < 22)
                {
                    t0 = (eax + 2) % 52;
                    t1 = ecx % 52;
                }
                else
                {
                    int baseIdx = 8 * (1 + (idx - 16) / 8);
                    t0 = baseIdx + ((eax + 2) & 7);
                    t1 = baseIdx + (ecx & 7);
                }

                ek[idx] = (ushort)(((ek[t0] >> 7) | (ek[t1] << 9)) & 0xFFFF);
                eax = ecx;
            }
        }

        private static void InvertSubkeys(ushort[] ek, ushort[] dk)
        {
            dk[0] = InvMul(ek[48]);
            dk[1] = (ushort)-ek[49];
            dk[2] = (ushort)-ek[50];
            dk[3] = InvMul(ek[51]);
            for (int k = 0; k < 8; k++)
            {
                int b = 4 + k * 6;
                int o = 46 - 6 * k;
                dk[b] = ek[o];
                dk[b + 1] = ek[o + 1];
                dk[b + 2] = InvMul(ek[o - 4]);
                if (k == 7)
                {
                    dk[b + 3] = (ushort)-ek[o - 3];
                    dk[b + 4] = (ushort)-ek[o - 2];
                }
                else
                {
                    dk[b + 3] = (ushort)-ek[o - 2];
                    dk[b + 4] = (ushort)-ek[o - 3];
                }

                dk[b + 5] = InvMul(ek[o - 1]);
            }
        }

        private static ushort Mul(ushort a, ushort b)
        {
            uint n = (uint)((a & 0xFFFF) * (b & 0xFFFF));
            if (n != 0)
            {
                uint x = (uint)(n & 0xFFFF);
                uint y = n >> 16;
                return (ushort)((x - y) + (x < y ? 1 : 0));
            }

            return (ushort)(1 - b);
        }

        private static ushort InvMul(ushort x)
        {
            if (x <= 1)
            {
                return x;
            }

            int t = 0;
            int newT = 1;
            int r = Modulus;
            int newR = x & 0xFFFF;
            while (newR != 0)
            {
                int q = r / newR;
                int tmpT = newT;
                newT = t - q * newT;
                t = tmpT;
                int tmpR = newR;
                newR = r - q * newR;
                r = tmpR;
            }

            if (r > 1)
            {
                return 0;
            }

            if (t < 0)
            {
                t += Modulus;
            }

            return (ushort)t;
        }

        private static void StoreLe16(byte[] buf, int o, ushort v)
        {
            buf[o] = (byte)v;
            buf[o + 1] = (byte)(v >> 8);
        }
    }
}
