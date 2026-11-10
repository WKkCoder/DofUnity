using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 原 TenRc6:RC6-32/20,密钥取 key32 前 8 字节,标准 P/Q 初值但表项递推为减 0x61C88647;
    /// ECB 按 16 字节步进;余 <c>len%16</c> 字节走 <c>CTsLocalImp::Seattos</c>(<see cref="AntibotSeattos"/>),非 BOX3D。
    /// </summary>
    internal static class AntibotRc6
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            uint[] s = AntibotRc6Core.ExpandKey(key32);
            int off = 0;
            int full16 = buf.Length >> 4;
            for (int i = 0; i < full16; i++)
            {
                if (encrypt)
                {
                    AntibotRc6Core.EncryptBlock(s, buf, off);
                }
                else
                {
                    AntibotRc6Core.DecryptBlock(s, buf, off);
                }

                off += 16;
            }

            ApplySeattosSuffix(key32, encrypt, buf);
        }

        internal static void ApplySeattosSuffix(byte[] key32, bool encrypt, byte[] buf)
        {
            int rem = buf.Length & 0xF;
            if (rem == 0)
            {
                return;
            }

            int off = buf.Length - rem;
            byte[] tail = new byte[rem];
            Buffer.BlockCopy(buf, off, tail, 0, rem);
            AntibotSeattos.Transform(key32, encrypt, tail);
            Buffer.BlockCopy(tail, 0, buf, off, rem);
        }
    }

    internal static class AntibotRc6Core
    {
        private const uint Mask = 0xffffffffu;

        internal static uint[] ExpandKey(byte[] key)
        {
            uint[] s = new uint[44];
            s[0] = 0xB7E15163u;
            for (int i = 1; i < 44; i++)
            {
                s[i] = (s[i - 1] - 0x61C88647u) & Mask;
            }

            uint[] l = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                l[i] = key[i];
            }

            uint esi = 0;
            uint edi = 0;
            for (int i = 0; i < 0x84; i++)
            {
                int si = i % 44;
                int li = i & 7;
                uint t = (esi + edi + s[si]) & Mask;
                esi = RotR(t, 0x1d);
                s[si] = esi;
                int sh = (int)((esi + edi) & 0x1fu);
                uint v = (esi + edi + l[li]) & Mask;
                edi = RotL(v, sh);
                l[li] = edi;
            }

            return s;
        }

        internal static void EncryptBlock(uint[] s, byte[] buf, int off)
        {
            uint a = LoadLe(buf, off);
            uint b = LoadLe(buf, off + 4);
            uint c = LoadLe(buf, off + 8);
            uint d = LoadLe(buf, off + 12);
            b = (b + s[0]) & Mask;
            d = (d + s[1]) & Mask;
            for (int i = 2; i <= 40; i += 2)
            {
                uint t = RotL((b * (2u * b + 1u)) & Mask, 5);
                uint u = RotL((d * (2u * d + 1u)) & Mask, 5);
                a = (RotL(a ^ t, (int)(u & 31u)) + s[i]) & Mask;
                c = (RotL(c ^ u, (int)(t & 31u)) + s[i + 1]) & Mask;
                uint na = b;
                b = c;
                c = d;
                d = a;
                a = na;
            }

            a = (a + s[42]) & Mask;
            c = (c + s[43]) & Mask;
            StoreLe(buf, off, a);
            StoreLe(buf, off + 4, b);
            StoreLe(buf, off + 8, c);
            StoreLe(buf, off + 12, d);
        }

        internal static void DecryptBlock(uint[] s, byte[] buf, int off)
        {
            uint a = LoadLe(buf, off);
            uint b = LoadLe(buf, off + 4);
            uint c = LoadLe(buf, off + 8);
            uint d = LoadLe(buf, off + 12);
            c = (c - s[43]) & Mask;
            a = (a - s[42]) & Mask;
            for (int i = 40; i >= 2; i -= 2)
            {
                uint pa = d;
                d = c;
                c = b;
                b = a;
                a = pa;
                uint u = RotL((d * (2u * d + 1u)) & Mask, 5);
                uint t = RotL((b * (2u * b + 1u)) & Mask, 5);
                c = RotR(c - s[i + 1], (int)(t & 31u)) ^ u;
                a = RotR(a - s[i], (int)(u & 31u)) ^ t;
            }

            d = (d - s[1]) & Mask;
            b = (b - s[0]) & Mask;
            StoreLe(buf, off, a);
            StoreLe(buf, off + 4, b);
            StoreLe(buf, off + 8, c);
            StoreLe(buf, off + 12, d);
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
