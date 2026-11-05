using System;

namespace Dnf.Net.Crypto
{
    /// <summary>14 个会话算法槽的静态表(E-204、E-262):密钥长度、块长、按包号选槽。</summary>
    public static class CipherSlots
    {
        /// <summary>槽的个数。</summary>
        public const int Count = 14;

        /// <summary>CHANNELINFO 载荷里二进制密钥块的总长(字节)。</summary>
        public const int TotalKeyLength = 334;

        /// <summary>各槽的密钥长度(字节),按槽号 0 到 13。</summary>
        public static readonly int[] KeyLengths = { 8, 16, 56, 60, 32, 16, 16, 16, 16, 16, 16, 10, 40, 16 };

        /// <summary>各槽的块长(字节),按槽号 0 到 13(客户端虚函数的实测值)。</summary>
        public static readonly int[] BlockSizes = { 4, 16, 8, 16, 16, 16, 8, 8, 16, 8, 8, 8, 8, 16 };

        /// <summary>包号选槽:包号 % 14(0x08097160,E-204)。</summary>
        public static int SlotForPacket(int packetId)
        {
            return packetId % Count;
        }

        /// <summary>某个槽在 334 字节密钥块里的起始偏移。</summary>
        public static int KeyOffset(int slot)
        {
            if ((uint)slot >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }
            int offset = 0;
            for (int i = 0; i < slot; i++)
            {
                offset += KeyLengths[i];
            }
            return offset;
        }

        /// <summary>新建某个槽的算法对象(尚未设密钥)。</summary>
        public static BlockCipher Create(int slot)
        {
            switch (slot)
            {
                case 0: return new ShiftCipher();
                case 1: return new RijndaelCipher();
                case 2: return new BlowfishCipher();
                case 3: return new Rc6Cipher();
                case 4: return new TwofishCipher();
                case 5: return new TeaCipher();
                case 6: return new KasumiCipher();
                case 7: return new XteaCipher();
                case 8: return new NoekeonCipher();
                case 9: return new KhazadCipher();
                case 10: return new Cast5Cipher();
                case 11: return new SkipjackCipher();
                case 12: return new Multi2Cipher();
                case 13: return new AnubisCipher();
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }
    }
}
