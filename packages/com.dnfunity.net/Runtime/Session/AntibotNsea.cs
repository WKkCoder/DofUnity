namespace Dnf.Net.Session
{
    /// <summary>
    /// NSEA 自定义 128 位分组；密钥取 GenKey 结果的前 16 字节，链值初始为 0，块与块之间按密文链接（见 <see cref="AntibotNseaCore"/>）。
    /// 整块处理完后，不足 16 字节的余量交给 Seattos。
    /// </summary>
    internal static class AntibotNsea
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotNseaCore core = new AntibotNseaCore(key32);
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
