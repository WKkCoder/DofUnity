namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 1:IDEA(64 位块、128 位密钥、8.5 轮);块内 16 位字自缓冲按大端拼装、按小端写回;尾部 keylen=16。
    /// </summary>
    internal static class AntibotIdea
    {
        private const int TailKeyBytes = 16;

        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            ushort[] encSub = new ushort[52];
            ushort[] sub;
            if (encrypt)
            {
                ExpandKey(key32, encSub);
                sub = encSub;
            }
            else
            {
                ushort[] decSub = new ushort[52];
                ExpandKey(key32, encSub);
                InvertSubkeys(encSub, decSub);
                sub = decSub;
            }
            int blocks = buf.Length >> 3;
            for (int b = 0; b < blocks; b++)
            {
                int o = b * 8;
                ushort x0 = (ushort)((buf[o] << 8) | buf[o + 1]);
                ushort x1 = (ushort)((buf[o + 2] << 8) | buf[o + 3]);
                ushort x2 = (ushort)((buf[o + 4] << 8) | buf[o + 5]);
                ushort x3 = (ushort)((buf[o + 6] << 8) | buf[o + 7]);
                CipherBlock(ref x0, ref x1, ref x2, ref x3, sub);
                StoreLe16(buf, o, x0);
                StoreLe16(buf, o + 2, x1);
                StoreLe16(buf, o + 4, x2);
                StoreLe16(buf, o + 6, x3);
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
            int idx = 8;
            while (idx < 52)
            {
                int ecx = eax + 1;
                int t0 = idx < 16 ? (eax + 2) & 7 : (eax + 2) % 52;
                int t1 = idx < 16 ? ecx & 7 : ecx % 52;
                ek[idx] = (ushort)(((ek[t0] >> 7) | (ek[t1] << 9)) & 0xFFFF);
                idx++;
                eax = ecx;
            }
        }

        private static void InvertSubkeys(ushort[] ek, ushort[] dk)
        {
            int p = 51;
            dk[p--] = InvMul(ek[48]);
            dk[p--] = (ushort)(-ek[49]);
            dk[p--] = (ushort)(-ek[50]);
            dk[p--] = InvMul(ek[51]);

            for (int r = 7; r >= 0; r--)
            {
                dk[p--] = ek[r * 6 + 5];
                dk[p--] = ek[r * 6 + 4];
                dk[p--] = InvMul(ek[r * 6 + 3]);
                dk[p--] = (ushort)(-ek[r * 6 + 2]);
                dk[p--] = (ushort)(-ek[r * 6 + 1]);
                dk[p--] = InvMul(ek[r * 6]);
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

            ushort t0 = 1;
            ushort a = x;
            ushort t1 = (ushort)(0x10001 / a);
            ushort rem = (ushort)(0x10001 % a);
            if (rem == 1)
            {
                return (ushort)(0x10001 - t1);
            }

            while (true)
            {
                if (rem == 0)
                {
                    return 0;
                }

                ushort q = (ushort)(a / rem);
                a = rem;
                rem = (ushort)(a - (q * rem));
                t0 = (ushort)(t0 + (q * t1));
                if (rem == 1)
                {
                    return (ushort)(0x10001 - t0);
                }

                if (rem == 0)
                {
                    return (ushort)(0x10001 - t0);
                }

                q = (ushort)(a / rem);
                a = rem;
                rem = (ushort)(a - (q * rem));
                t1 = (ushort)(t1 + (q * t0));
                if (rem == 1)
                {
                    return (ushort)(0x10001 - t1);
                }
            }
        }

        private static void StoreLe16(byte[] buf, int o, ushort v)
        {
            buf[o] = (byte)v;
            buf[o + 1] = (byte)(v >> 8);
        }
    }
}
