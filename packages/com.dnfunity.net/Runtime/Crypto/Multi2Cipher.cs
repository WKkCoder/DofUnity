using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 12:名字推断为 MULTI2,没有逐位对拍。块长 8 字节,密钥 40 字节。算法尚未实现。</summary>
    public sealed class Multi2Cipher : BlockCipher
    {
        public override int Slot => 12;

        public override int BlockSize => 8;

        public override int KeyLength => 40;

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
