namespace Dnf.Net.Session
{
    /// <summary>
    /// NSEA 自定义 128 位分组；密钥前 16 字节；余量 Seattos。
    /// </summary>
    internal static class AntibotNsea
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotNseaCore.PrepareKey(key32);
            int off = 0;
            int aligned = buf.Length & ~0xF;
            while (off < aligned)
            {
                if (encrypt)
                {
                    AntibotNseaCore.EncryptBlock(buf, off);
                }
                else
                {
                    AntibotNseaCore.DecryptBlock(buf, off);
                }

                off += 16;
            }

            int rest = buf.Length - off;
            if (rest > 0)
            {
                ApplySeattosSuffix(key32, encrypt, buf, off, rest);
            }
        }

        private static void ApplySeattosSuffix(byte[] key32, bool encrypt, byte[] buf, int offset, int count)
        {
            byte[] tail = new byte[count];
            System.Buffer.BlockCopy(buf, offset, tail, 0, count);
            AntibotSeattos.Transform(key32, encrypt, tail);
            System.Buffer.BlockCopy(tail, 0, buf, offset, count);
        }
    }

    internal static class AntibotNseaCore
    {
        internal static void PrepareKey(byte[] key32)
        {
        }

        internal static void EncryptBlock(byte[] buf, int offset)
        {
        }

        internal static void DecryptBlock(byte[] buf, int offset)
        {
        }
    }
}
