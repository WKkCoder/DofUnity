using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 0:右循环移位后异或(移位量取低 5 位,密钥为 32 位整数,小端),E-204。块长 4 字节,密钥 8 字节。算法尚未实现。</summary>
    public sealed class ShiftCipher : BlockCipher
    {
        public override int Slot => 0;

        public override int BlockSize => 4;

        public override int KeyLength => 8;

        public override void SetKey(byte[] key, int offset)
        {
            throw new NotImplementedException();
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            throw new NotImplementedException();
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            throw new NotImplementedException();
        }
    }
}
