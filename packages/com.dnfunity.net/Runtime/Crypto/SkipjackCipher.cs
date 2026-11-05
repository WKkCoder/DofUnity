using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 11:名字推断为 SKIPJACK,没有逐位对拍。块长 8 字节,密钥 10 字节。算法尚未实现。</summary>
    public sealed class SkipjackCipher : BlockCipher
    {
        public override int Slot => 11;

        public override int BlockSize => 8;

        public override int KeyLength => 10;

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
