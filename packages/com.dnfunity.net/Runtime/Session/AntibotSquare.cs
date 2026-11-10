namespace Dnf.Net.Session
{
    /// <summary>
    /// Square 128 位、8 轮；密钥前 16 字节。余量 Seattos（非 AntibotTail / ResidualEight）。
    /// </summary>
    internal static class AntibotSquare
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotSquareCore.PrepareKey(key32);
            int off = 0;
            int aligned = buf.Length & ~0xF;
            while (off < aligned)
            {
                if (encrypt)
                {
                    AntibotSquareCore.EncryptBlock(buf, off);
                }
                else
                {
                    AntibotSquareCore.DecryptBlock(buf, off);
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
}
