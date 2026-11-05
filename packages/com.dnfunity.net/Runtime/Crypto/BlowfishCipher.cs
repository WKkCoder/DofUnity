using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 2:与标准 Blowfish(ECB,56 字节密钥)不一致,原因未知(E-263)。块长 8 字节,密钥 56 字节。算法尚未实现。</summary>
    public sealed class BlowfishCipher : BlockCipher
    {
        public override int Slot => 2;

        public override int BlockSize => 8;

        public override int KeyLength => 56;

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
