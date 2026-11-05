using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 4:名字推断为 Twofish,没有逐位对拍。块长 16 字节,密钥 32 字节。算法尚未实现。</summary>
    public sealed class TwofishCipher : BlockCipher
    {
        public override int Slot => 4;

        public override int BlockSize => 16;

        public override int KeyLength => 32;

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
