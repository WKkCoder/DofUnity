using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 修订版 Gladman 风格 MARS 块核（128 位密钥→40 字轮密钥）。
    /// 原库 PIC 将线性相与搅拌索引用表对调（VA 0x259920 / 0x2598a0）。
    /// 密钥扩展已与 mars_setkey 一致；块函数见 AntibotMarsBlock（混溶为 mars_enc 展开式）。
    /// </summary>
    internal static partial class AntibotMarsCore
    {
        private const uint Mask32 = 0xFFFFFFFFu;

        private static uint[] _subkeys;

        internal static void PrepareKey(byte[] key32)
        {
            _subkeys = ExpandKey(key32);
        }

        internal static void EncryptBlock(byte[] buf, int offset)
        {
            if (_subkeys == null)
            {
                return;
            }

            MarsBlockTransform(buf, offset, true);
        }

        internal static void DecryptBlock(byte[] buf, int offset)
        {
            if (_subkeys == null)
            {
                return;
            }

            MarsBlockTransform(buf, offset, false);
        }

        private static uint[] ExpandKey(byte[] key32)
        {
            uint[] t = new uint[15];
            t[0] = ReadLe32(key32, 0);
            t[1] = ReadLe32(key32, 4);
            t[2] = ReadLe32(key32, 8);
            t[3] = ReadLe32(key32, 12);
            t[4] = 4;
            uint[] k = new uint[40];
            int kd = 0;
            for (int round = 0; round < 4; round++)
            {
                uint step = (uint)round;
                for (int j = 0; j < 15; j++)
                {
                    uint v = t[AntibotMarsTables.LinearOther[j]] ^ t[AntibotMarsTables.LinearIndex[j]];
                    t[j] = Ror32(v, 29) ^ t[j] ^ step;
                    step += 4;
                }

                for (int rep = 0; rep < 4; rep++)
                {
                    for (int j = 0; j < 15; j++)
                    {
                        int sidx = (int)(t[AntibotMarsTables.StirIndex[j]] & 0x1FF);
                        t[j] = Ror32(AntibotMarsTables.Sbox[sidx] + t[j], 23);
                    }
                }

                for (int j = 0; j < 10; j++)
                {
                    k[kd++] = t[AntibotMarsTables.SubkeyPick[j]];
                }
            }

            FixMultiplierSubkeys(k);
            return k;
        }

        private static void FixMultiplierSubkeys(uint[] k)
        {
            for (int i = 0; i < 16; i++)
            {
                int odd = i * 2 + 1;
                uint w = k[odd];
                uint esi = w | 3;
                uint d = WeakKeyMask(esi);
                if (d == 0)
                {
                    k[odd] = esi;
                    continue;
                }

                uint mask = MaskFromLowBits(d);
                byte rot = (byte)k[i * 2];
                uint bb = Rol32(AntibotMarsTables.MultFixTab[(int)(w & 3)], rot) & 0xFFFFFFFCu;
                k[odd] = esi ^ (mask & bb);
            }
        }

        private static uint WeakKeyMask(uint esi)
        {
            uint eax = esi;
            uint edx = esi;
            edx >>= 1;
            eax = (~eax ^ edx) & Mask32;
            eax &= 0x7FFFFFFF;
            uint ecx = eax;
            edx = eax;
            ecx >>= 1;
            eax &= ecx;
            edx >>= 2;
            edx &= eax;
            eax = edx;
            ecx = edx;
            eax >>= 3;
            ecx >>= 6;
            edx &= eax;
            edx &= ecx;
            return edx;
        }

        private static uint MaskFromLowBits(uint d)
        {
            uint eax = d + d;
            uint t2 = d << 2;
            eax |= t2;
            uint edx = eax * 4;
            edx |= eax;
            eax = edx << 4;
            return edx | eax;
        }

        private static uint Ror32(uint x, int n)
        {
            n &= 31;
            return (x >> n) | (x << (32 - n));
        }

        private static uint Rol32(uint x, int n)
        {
            n &= 31;
            return (x << n) | (x >> (32 - n));
        }

        private static uint ReadLe32(byte[] buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }
    }
}
