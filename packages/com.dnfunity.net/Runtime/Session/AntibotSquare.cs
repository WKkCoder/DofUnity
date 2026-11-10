namespace Dnf.Net.Session
{
    /// <summary>
    /// Square 128 位、8 轮；密钥取 GenKey 结果的前 16 字节。
    /// 整块做 ECB，不足 16 字节的余量交给 Seattos（不是 AntibotTail / ResidualEight）。
    /// </summary>
    internal static class AntibotSquare
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotSquareCore core = new AntibotSquareCore(key32);
            int aligned = buf.Length & ~0xF;
            for (int off = 0; off < aligned; off += 16)
            {
                if (encrypt)
                {
                    core.EncryptBlock(buf, off);
                }
                else
                {
                    core.DecryptBlock(buf, off);
                }
            }

            AntibotRc6.ApplySeattosSuffix(key32, encrypt, buf);
        }
    }
}
