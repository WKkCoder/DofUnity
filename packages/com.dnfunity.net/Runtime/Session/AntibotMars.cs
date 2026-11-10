namespace Dnf.Net.Session
{
    /// <summary>
    /// MARS（修订版）128 位分组；密钥取 GenKey 结果的前 16 字节（位长 0x80）。
    /// 整块做 ECB，不足 16 字节的余量交给 Seattos。
    /// </summary>
    internal static class AntibotMars
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotMarsCore core = new AntibotMarsCore(key32);
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
