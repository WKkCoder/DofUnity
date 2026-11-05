using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 7:名字推断为 XTEA,没有逐位对拍。块长 8 字节,密钥 16 字节。算法尚未实现。</summary>
    public sealed class XteaCipher : BlockCipher
    {
        public override int Slot => 7;

        public override int BlockSize => 8;

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
