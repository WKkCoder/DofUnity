using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// Square 单块轮函数（列 T 表 + 末轮 S 盒）；与标准 Square 并行轮不同，仍待与 0x202e10 完全对齐。
    /// </summary>
    internal static class AntibotSquareBlockRound
    {
        internal static void Encrypt(ref uint w0, ref uint w1, ref uint w2, ref uint w3, uint[] workKeys)
        {
            w0 ^= workKeys[0];
            w1 ^= workKeys[1];
            w2 ^= workKeys[2];
            w3 ^= workKeys[3];
            for (int r = 1; r < 8; r++)
            {
                RoundTable(ref w0, ref w1, ref w2, ref w3, workKeys, r * 4, true);
            }

            FinalGamma(ref w0, ref w1, ref w2, ref w3, workKeys, 32, AntibotSquareTables.SBox);
        }

        internal static void Decrypt(ref uint w0, ref uint w1, ref uint w2, ref uint w3, uint[] compactKeys)
        {
            w0 ^= compactKeys[32];
            w1 ^= compactKeys[33];
            w2 ^= compactKeys[34];
            w3 ^= compactKeys[35];
            for (int r = 7; r >= 1; r--)
            {
                RoundTable(ref w0, ref w1, ref w2, ref w3, compactKeys, r * 4, false);
            }

            FinalGamma(ref w0, ref w1, ref w2, ref w3, compactKeys, 0, AntibotSquareTables.InvSBox);
        }

        private static void RoundTable(ref uint w0, ref uint w1, ref uint w2, ref uint w3, uint[] keys, int keyOff, bool encrypt)
        {
            uint[] te0 = encrypt ? AntibotSquareTables.Te0 : AntibotSquareTables.Td0;
            uint[] te1 = encrypt ? AntibotSquareTables.Te1 : AntibotSquareTables.Td1;
            uint[] te2 = encrypt ? AntibotSquareTables.Te2 : AntibotSquareTables.Td2;
            uint[] te3 = encrypt ? AntibotSquareTables.Te3 : AntibotSquareTables.Td3;
            uint n0 = ColRound(w0, w1, w2, w3, 0, te0, te1, te2, te3) ^ keys[keyOff];
            uint n1 = ColRound(w0, w1, w2, w3, 1, te0, te1, te2, te3) ^ keys[keyOff + 1];
            uint n2 = ColRound(w0, w1, w2, w3, 2, te0, te1, te2, te3) ^ keys[keyOff + 2];
            uint n3 = ColRound(w0, w1, w2, w3, 3, te0, te1, te2, te3) ^ keys[keyOff + 3];
            w0 = n0;
            w1 = n1;
            w2 = n2;
            w3 = n3;
        }

        private static uint ColRound(uint w0, uint w1, uint w2, uint w3, int col, uint[] t0, uint[] t1, uint[] t2, uint[] t3)
        {
            byte b0 = (byte)(w0 >> (8 * col));
            byte b1 = (byte)(w1 >> (8 * col));
            byte b2 = (byte)(w2 >> (8 * col));
            byte b3 = (byte)(w3 >> (8 * col));
            return t0[b0] ^ t1[b1] ^ t2[b2] ^ t3[b3];
        }

        private static void FinalGamma(ref uint w0, ref uint w1, ref uint w2, ref uint w3, uint[] keys, int keyOff, byte[] sbox)
        {
            w0 = ApplyGammaWord(w0, sbox) ^ keys[keyOff];
            w1 = ApplyGammaWord(w1, sbox) ^ keys[keyOff + 1];
            w2 = ApplyGammaWord(w2, sbox) ^ keys[keyOff + 2];
            w3 = ApplyGammaWord(w3, sbox) ^ keys[keyOff + 3];
        }

        private static uint ApplyGammaWord(uint w, byte[] sbox)
        {
            byte b0 = sbox[w & 0xFF];
            byte b1 = sbox[(w >> 8) & 0xFF];
            byte b2 = sbox[(w >> 16) & 0xFF];
            byte b3 = sbox[(w >> 24) & 0xFF];
            return (uint)b0 | ((uint)b1 << 8) | ((uint)b2 << 16) | ((uint)b3 << 24);
        }
    }
}
