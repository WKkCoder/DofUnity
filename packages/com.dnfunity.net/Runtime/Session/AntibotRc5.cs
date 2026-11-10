using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 原 TenRc5:RC5-32/12/128,密钥 key32 前 16 字节;每 16 字节步进原地加密时只改写前 8 字节;
    /// 余 <c>len%16</c> 走 <see cref="AntibotSeattos"/>(同 RC6)。
    /// </summary>
    internal static class AntibotRc5
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            uint[] s = AntibotRc5Core.ExpandKey(key32);
            int off = 0;
            int full16 = buf.Length >> 4;
            for (int i = 0; i < full16; i++)
            {
                if (encrypt)
                {
                    AntibotRc5Core.EncryptHalfBlock(s, buf, off);
                }
                else
                {
                    AntibotRc5Core.DecryptHalfBlock(s, buf, off);
                }

                off += 16;
            }

            AntibotRc6.ApplySeattosSuffix(key32, encrypt, buf);
        }
    }

    internal static class AntibotRc5Core
    {
        private const uint Mask = 0xffffffffu;

        internal static uint[] ExpandKey(byte[] key)
        {
            uint[] l = new uint[4];
            for (int i = 15; i >= 0; i--)
            {
                int idx = i >> 2;
                l[idx] = ((l[idx] << 8) + key[i]) & Mask;
            }

            uint[] s = new uint[26];
            s[0] = 0xB7E15163u;
            for (int i = 1; i < 26; i++)
            {
                s[i] = (s[i - 1] - 0x61C88647u) & Mask;
            }

            uint esi = 0;
            uint edi = 0;
            int j = 0;
            for (int step = 0; step < 0x4e; step++)
            {
                int si = step % 26;
                uint t = (esi + edi + s[si]) & Mask;
                esi = RotR(t, 0x1d);
                s[si] = esi;
                int sh = (int)((esi + edi) & 0x1fu);
                uint v = (esi + edi + l[j]) & Mask;
                edi = RotL(v, sh);
                l[j] = edi;
                j = (j + 1) & 3;
            }

            return s;
        }

        internal static void EncryptHalfBlock(uint[] s, byte[] buf, int off)
        {
            uint a = LoadLe(buf, off);
            uint b = LoadLe(buf, off + 4);
            a = (a + s[0]) & Mask;
            b = (b + s[1]) & Mask;
            for (int i = 2; i < 26; i += 2)
            {
                a = (RotL(a ^ b, (int)(b & 31u)) + s[i]) & Mask;
                b = (RotL(b ^ a, (int)(a & 31u)) + s[i + 1]) & Mask;
            }

            StoreLe(buf, off, a);
            StoreLe(buf, off + 4, b);
        }

        internal static void DecryptHalfBlock(uint[] s, byte[] buf, int off)
        {
            uint a = LoadLe(buf, off);
            uint b = LoadLe(buf, off + 4);
            for (int i = 24; i >= 2; i -= 2)
            {
                b = (RotR(b - s[i + 1], (int)(a & 31u)) ^ a) & Mask;
                a = (RotR(a - s[i], (int)(b & 31u)) ^ b) & Mask;
            }

            b = (b - s[1]) & Mask;
            a = (a - s[0]) & Mask;
            StoreLe(buf, off, a);
            StoreLe(buf, off + 4, b);
        }

        private static uint LoadLe(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void StoreLe(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static uint RotL(uint v, int n)
        {
            n &= 31;
            return ((v << n) | (v >> (32 - n))) & Mask;
        }

        private static uint RotR(uint v, int n)
        {
            return RotL(v, 32 - n);
        }
    }
}
