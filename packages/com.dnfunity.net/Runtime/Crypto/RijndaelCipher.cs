using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 1:与标准 AES-128(ECB)逐位一致(E-263)。块长 16 字节,密钥 16 字节。算法尚未实现。</summary>
    public sealed class RijndaelCipher : BlockCipher
    {
        public override int Slot => 1;

        public override int BlockSize => 16;

        public override int KeyLength => 16;

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
