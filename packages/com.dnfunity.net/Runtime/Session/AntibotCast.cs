using Dnf.Net.Crypto;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 4:CAST-128(ECB,8 字节块),密钥 16 字节;复用 <see cref="Cast5Cipher"/>;尾部 keylen=16。
    /// </summary>
    internal static class AntibotCast
    {
        private const int TailKeyBytes = 16;

        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            var cipher = new Cast5Cipher();
            cipher.SetKey(key32, 0);
            int blocks = buf.Length >> 3;
            for (int b = 0; b < blocks; b++)
            {
                int o = b * 8;
                if (encrypt)
                {
                    cipher.EncryptBlock(buf, o, buf, o);
                }
                else
                {
                    cipher.DecryptBlock(buf, o, buf, o);
                }
            }

            AntibotTail.ProcessLastBytes(key32, TailKeyBytes, encrypt, buf);
        }
    }
}
