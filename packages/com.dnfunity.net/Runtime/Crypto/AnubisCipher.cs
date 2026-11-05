using System;

namespace Dnf.Net.Crypto
{
    /// <summary>槽 13:构造态向量(密钥在对象构造时内部生成),E-263。块长 16 字节,密钥 16 字节。算法尚未实现。</summary>
    public sealed class AnubisCipher : BlockCipher
    {
        public override int Slot => 13;

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
