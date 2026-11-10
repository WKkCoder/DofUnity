namespace Dnf.Net.Session
{
    /// <summary>
    /// MARS 128 位块；mars_setkey 取 key32 前 16 字节（位长 0x80）。余量 Seattos。
    /// </summary>
    internal static class AntibotMars
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotMarsCore.PrepareKey(key32);
            int off = 0;
            int aligned = buf.Length & ~0xF;
            while (off < aligned)
            {
                if (encrypt)
                {
                    AntibotMarsCore.EncryptBlock(buf, off);
                }
                else
                {
                    AntibotMarsCore.DecryptBlock(buf, off);
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

    internal static class AntibotMarsCore
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
