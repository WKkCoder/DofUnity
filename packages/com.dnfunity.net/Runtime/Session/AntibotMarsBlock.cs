using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 修订版 MARS 单块加解密核（轮函数与 mars_enc/mars_dec 同序；混溶为汇编展开式）。
    /// </summary>
    internal static partial class AntibotMarsCore
    {
        private static void MarsBlockTransform(byte[] buf, int offset, bool encrypt)
        {
            uint a = ReadLe32(buf, offset);
            uint b = ReadLe32(buf, offset + 4);
            uint c = ReadLe32(buf, offset + 8);
            uint d = ReadLe32(buf, offset + 12);
            uint[] sk = _subkeys;
            uint[] sb = AntibotMarsTables.Sbox;

            if (encrypt)
            {
                EncryptWords(sk, sb, ref a, ref b, ref c, ref d);
            }
            else
            {
                DecryptWords(sk, sb, ref a, ref b, ref c, ref d);
            }

            WriteLe32(buf, offset, a);
            WriteLe32(buf, offset + 4, b);
            WriteLe32(buf, offset + 8, c);
            WriteLe32(buf, offset + 12, d);
        }

        private static void WriteLe32(byte[] buf, int offset, uint v)
        {
            buf[offset] = (byte)v;
            buf[offset + 1] = (byte)(v >> 8);
            buf[offset + 2] = (byte)(v >> 16);
            buf[offset + 3] = (byte)(v >> 24);
        }

        private static uint MixWord(uint x)
        {
            return x;
        }

        private static uint EFunction(uint inp, int kd, uint[] sk, uint[] sb)
        {
            uint t = MixWord(inp + sk[kd]);
            t = Rol32(t, (int)(sk[kd + 1] & 31));
            ulong prod = (ulong)t * sk[kd + 2];
            t = (uint)(prod >> 32);
            uint u = MixWord(t ^ Rol32(t, (int)(sk[kd + 3] & 31)));
            t = Rol32(u, (int)(sk[kd + 4] & 31));
            t = MixWord(t + sb[u & 0x1FF]);
            return MixWord(t ^ sb[(u >> 9) & 0x1FF]);
        }

        private static void ForwardMix(uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            ForwardMixStep(sb, ref a, ref b, ref c, ref d);
        }

        private static void BackwardMix(uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            BackwardMixStep(sb, ref a, ref b, ref c, ref d);
        }

        private static void ForwardMixStep(uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint mA = a;
            uint mC = c;
            uint mD = d;
            uint esi = b;
            uint ecx;
            uint eax;

            esi = MixWord(esi ^ sb[mA & 0xFF]);
            esi = MixWord(esi + sb[256 + ((mA >> 8) & 0xFF)]);
            eax = Ror32(mA, 16);
            mC = MixWord(mC + sb[eax & 0xFF]);
            ecx = Ror32(mA, 24);
            mD = MixWord(mD ^ sb[256 + (ecx & 0xFF)]);
            mC = MixWord(mC ^ sb[esi & 0xFF]);
            uint edx = esi;
            mC = MixWord(mC + sb[256 + ((edx >> 8) & 0xFF)]);
            uint esiLow = Ror32(esi, 16) & 0xFF;
            uint mdc = Ror32(edx, 24);
            mC = MixWord(mC + sb[esiLow]);
            ecx = MixWord(ecx ^ sb[256 + (mdc & 0xFF)]);
            uint md8 = MixWord(mC + mdc);
            eax = mC;
            mC = Ror32(mC, 16);
            edx = mD;
            edx = MixWord(edx ^ sb[mC & 0xFF]);
            edx = MixWord(edx + sb[256 + ((eax >> 8) & 0xFF)]);
            uint m140 = Ror32(eax, 24);
            ecx = MixWord(ecx + sb[mC & 0xFF]);
            md8 = MixWord(md8 ^ sb[256 + (m140 & 0xFF)]);
            ecx = MixWord(ecx ^ sb[edx & 0xFF]);
            ecx = MixWord(ecx + sb[256 + ((edx >> 8) & 0xFF)]);
            uint m110 = Ror32(edx, 24);
            edx = Ror32(edx, 16);
            md8 = MixWord(md8 + sb[edx & 0xFF]);
            md8 = MixWord(md8 ^ sb[m110 & 0xFF]);
            uint edi = MixWord(ecx ^ m140);
            mC = MixWord(mC + sb[256 + ((ecx >> 8) & 0xFF)]);
            uint mac = Ror32(edi, 24);
            esi = MixWord(sb[256 + (m110 & 0xFF)] ^ m140);
            md8 = MixWord(md8 ^ sb[ecx & 0xFF]);
            esi = MixWord(esi ^ sb[mac & 0xFF]);
            uint m10c = MixWord(esi ^ sb[256 + (mac & 0xFF)]);
            esi = MixWord(esi ^ sb[md8 & 0xFF]);
            md8 = Ror32(md8, 16);
            uint md4 = Ror32(md8, 24);
            esi = MixWord(esi + sb[256 + ((md8 >> 8) & 0xFF)]);
            m10c = MixWord(m10c + sb[md8 & 0xFF]);
            uint ma8 = MixWord(mac + m10c);
            ma8 = MixWord(ma8 ^ sb[256 + (md4 & 0xFF)]);
            edi = esi;
            ecx = MixWord(ecx ^ sb[edi & 0xFF]);
            ecx = MixWord(ecx + sb[256 + ((esi >> 8) & 0xFF)]);
            esi = Ror32(esi, 16);
            edi = Ror32(edi, 24);
            ma8 = MixWord(ma8 + sb[esi & 0xFF]);
            ma8 = MixWord(ma8 + sb[256 + (edi & 0xFF)]);
            ecx = Ror32(ecx, 16);
            ma8 = MixWord(ma8 + sb[256 + (ecx & 0xFF)]);
            uint tmp = Ror32(ecx, 24);
            ecx = MixWord(Ror32(ecx, 16) & 0xFF);
            ma8 = MixWord(ma8 + sb[ecx]);
            ma8 = MixWord(ma8 ^ sb[tmp & 0xFF]);
            mD = MixWord(mD + ma8);
            a = mA;
            b = esi;
            c = mC;
            d = mD;
        }

        private static void BackwardMixStep(uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint mA = d;
            uint mC = c;
            uint mD = a;
            uint esi = b;
            uint ecx;
            uint eax;

            esi = MixWord(esi ^ sb[mA & 0xFF]);
            esi = MixWord(esi + sb[256 + ((mA >> 8) & 0xFF)]);
            eax = Ror32(mA, 16);
            mC = MixWord(mC + sb[eax & 0xFF]);
            ecx = Ror32(mA, 24);
            mD = MixWord(mD ^ sb[256 + (ecx & 0xFF)]);
            mC = MixWord(mC ^ sb[esi & 0xFF]);
            uint edx = esi;
            mC = MixWord(mC + sb[256 + ((edx >> 8) & 0xFF)]);
            uint esiLow = Ror32(esi, 16) & 0xFF;
            uint mdc = Ror32(edx, 24);
            mC = MixWord(mC + sb[esiLow]);
            ecx = MixWord(ecx ^ sb[256 + (mdc & 0xFF)]);
            uint md8 = MixWord(mC + mdc);
            eax = mC;
            mC = Ror32(mC, 16);
            edx = mD;
            edx = MixWord(edx ^ sb[mC & 0xFF]);
            edx = MixWord(edx + sb[256 + ((eax >> 8) & 0xFF)]);
            uint m140 = Ror32(eax, 24);
            ecx = MixWord(ecx + sb[mC & 0xFF]);
            md8 = MixWord(md8 ^ sb[256 + (m140 & 0xFF)]);
            ecx = MixWord(ecx ^ sb[edx & 0xFF]);
            ecx = MixWord(ecx + sb[256 + ((edx >> 8) & 0xFF)]);
            uint m110 = Ror32(edx, 24);
            edx = Ror32(edx, 16);
            md8 = MixWord(md8 + sb[edx & 0xFF]);
            md8 = MixWord(md8 ^ sb[m110 & 0xFF]);
            uint edi = MixWord(ecx ^ m140);
            mC = MixWord(mC + sb[256 + ((ecx >> 8) & 0xFF)]);
            uint mac = Ror32(edi, 24);
            esi = MixWord(sb[256 + (m110 & 0xFF)] ^ m140);
            md8 = MixWord(md8 ^ sb[ecx & 0xFF]);
            esi = MixWord(esi ^ sb[mac & 0xFF]);
            uint m10c = MixWord(esi ^ sb[256 + (mac & 0xFF)]);
            uint ma8 = MixWord(mac + m10c);
            esi = MixWord(esi ^ sb[md8 & 0xFF]);
            md8 = Ror32(md8, 16);
            uint md4 = Ror32(md8, 24);
            esi = MixWord(esi + sb[256 + ((md8 >> 8) & 0xFF)]);
            m10c = MixWord(m10c + sb[md8 & 0xFF]);
            ma8 = MixWord(ma8 ^ sb[256 + (md4 & 0xFF)]);
            edi = esi;
            ecx = MixWord(ecx ^ sb[edi & 0xFF]);
            ecx = MixWord(ecx + sb[256 + ((esi >> 8) & 0xFF)]);
            esi = Ror32(esi, 16);
            edi = Ror32(edi, 24);
            ma8 = MixWord(ma8 + sb[esi & 0xFF]);
            ma8 = MixWord(ma8 + sb[256 + (edi & 0xFF)]);
            ecx = Ror32(ecx, 16);
            ma8 = MixWord(ma8 + sb[256 + (ecx & 0xFF)]);
            uint tmp = Ror32(ecx, 24);
            ecx = MixWord(Ror32(ecx, 16) & 0xFF);
            ma8 = MixWord(ma8 + sb[ecx]);
            ma8 = MixWord(ma8 ^ sb[tmp & 0xFF]);
            mD = MixWord(mD + ma8);
            a = mD;
            b = esi;
            c = mC;
            d = mA;
        }

        private static void EncryptWords(uint[] sk, uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            a = MixWord(a + sk[0]);
            b = MixWord(b + sk[1]);
            c = MixWord(c + sk[2]);
            d = MixWord(d + sk[3]);
            ForwardMix(sb, ref a, ref b, ref c, ref d);
            CoreForwardEnc(sk, sb, ref a, ref b, ref c, ref d);
            CoreBackwardEnc(sk, sb, ref a, ref b, ref c, ref d);
            BackwardMix(sb, ref a, ref b, ref c, ref d);
            a = MixWord(a - sk[36]);
            b = MixWord(b - sk[37]);
            c = MixWord(c - sk[38]);
            d = MixWord(d - sk[39]);
        }

        private static void DecryptWords(uint[] sk, uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            a = MixWord(a + sk[36]);
            b = MixWord(b + sk[37]);
            c = MixWord(c + sk[38]);
            d = MixWord(d + sk[39]);
            ForwardMix(sb, ref a, ref b, ref c, ref d);
            CoreForwardDec(sk, sb, ref a, ref b, ref c, ref d);
            CoreBackwardDec(sk, sb, ref a, ref b, ref c, ref d);
            BackwardMix(sb, ref a, ref b, ref c, ref d);
            a = MixWord(a - sk[0]);
            b = MixWord(b - sk[1]);
            c = MixWord(c - sk[2]);
            d = MixWord(d - sk[3]);
        }

        private static void CoreForwardEnc(uint[] sk, uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint t;
            t = d; d = EFunction(d, 4, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
            t = c; c = EFunction(c, 9, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = b; b = EFunction(b, 14, sk, sb); a ^= t; c = MixWord(c - t); d = MixWord(d + t);
            t = a; a = EFunction(a, 19, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = d; d = EFunction(d, 24, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
            t = c; c = EFunction(c, 29, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = b; b = EFunction(b, 34, sk, sb); a ^= t; d = MixWord(d + t); c = MixWord(c - t);
            t = a; a = EFunction(a, 35, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
        }

        private static void CoreBackwardEnc(uint[] sk, uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint t;
            t = a; a = EFunction(a, 30, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = b; b = EFunction(b, 25, sk, sb); a ^= t; c = MixWord(c - t); d = MixWord(d + t);
            t = c; c = EFunction(c, 20, sk, sb); b ^= t; a = MixWord(a - t); d = MixWord(d + t);
            t = d; d = EFunction(d, 15, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
            t = a; a = EFunction(a, 10, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = b; b = EFunction(b, 5, sk, sb); a ^= t; d = MixWord(d + t); c = MixWord(c - t);
            t = c; c = EFunction(c, 4, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = d; d = EFunction(d, 9, sk, sb); c ^= t; a = MixWord(a - t); b = MixWord(b + t);
        }

        private static void CoreForwardDec(uint[] sk, uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint t;
            t = d; d = EFunction(d, 9, sk, sb); c ^= t; a = MixWord(a - t); b = MixWord(b + t);
            t = c; c = EFunction(c, 4, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = b; b = EFunction(b, 5, sk, sb); a ^= t; d = MixWord(d + t); c = MixWord(c - t);
            t = a; a = EFunction(a, 10, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = d; d = EFunction(d, 15, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
            t = c; c = EFunction(c, 20, sk, sb); b ^= t; a = MixWord(a - t); d = MixWord(d + t);
            t = b; b = EFunction(b, 25, sk, sb); a ^= t; c = MixWord(c - t); d = MixWord(d + t);
            t = a; a = EFunction(a, 30, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = a; a = EFunction(a, 35, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = b; b = EFunction(b, 34, sk, sb); a ^= t; d = MixWord(d + t); c = MixWord(c - t);
            t = c; c = EFunction(c, 29, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = d; d = EFunction(d, 24, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
            t = a; a = EFunction(a, 19, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = b; b = EFunction(b, 14, sk, sb); a ^= t; c = MixWord(c - t); d = MixWord(d + t);
            t = c; c = EFunction(c, 9, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = d; d = EFunction(d, 4, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
        }

        private static void CoreBackwardDec(uint[] sk, uint[] sb, ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint t;
            t = d; d = EFunction(d, 4, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
            t = c; c = EFunction(c, 9, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = b; b = EFunction(b, 14, sk, sb); a ^= t; c = MixWord(c - t); d = MixWord(d + t);
            t = a; a = EFunction(a, 19, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
            t = d; d = EFunction(d, 24, sk, sb); c ^= t; b = MixWord(b + t); a ^= t;
            t = c; c = EFunction(c, 29, sk, sb); b ^= t; d = MixWord(d - t); a = MixWord(a + t);
            t = b; b = EFunction(b, 34, sk, sb); a ^= t; d = MixWord(d + t); c = MixWord(c - t);
            t = a; a = EFunction(a, 35, sk, sb); d = MixWord(d - t); c ^= t; b = MixWord(b + t);
        }
    }
}
