namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 19:GENE 新。流式、与位置和密钥前 8 字节异或:<c>输出[i] = 输入[i] ^ (i &amp; 0xFF) ^ key[i &amp; 7]</c>;
    /// 加密与解密是同一个变换,没有尾部钩子,长度不限。
    /// </summary>
    internal static class AntibotGeneNew
    {
        /// <summary>对整个缓冲原地变换;<paramref name="key32"/> 至少 8 字节。</summary>
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = (byte)(buf[i] ^ (i & 0xFF) ^ key32[i & 7]);
            }
        }
    }
}
