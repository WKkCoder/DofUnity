namespace Dnf.Net.Session
{
    /// <summary>
    /// 反机器人信封里 13 个分组算法共用的"尾部处理":整块循环之后,缓冲里还剩 <c>长度 % 8</c>(1 到 7)个字节,
    /// 由 <see cref="AntibotTailBox"/> 按 n 号变换整体搅动这 n 个字节(位置固定在 <c>长度 &amp; ~7</c> 起)。
    /// 长度是 8 的整数倍时什么都不做。
    /// </summary>
    internal static class AntibotTail
    {
        /// <summary>
        /// 对 <paramref name="buf"/> 的末尾 <c>buf.Length % 8</c> 个字节原地做尾部变换。
        /// </summary>
        /// <param name="key">算法密钥缓冲(key32 的整段或前缀,至少 <paramref name="keyLength"/> 字节)。</param>
        /// <param name="keyLength">本算法传给尾部变换的密钥字节数(各算法不同,见各算法类)。</param>
        /// <param name="encrypt">方向:true 为加密。</param>
        /// <param name="buf">整个工作缓冲。</param>
        internal static void ProcessLastBytes(byte[] key, int keyLength, bool encrypt, byte[] buf)
        {
            int n = buf.Length & 7;
            if (n == 0)
            {
                return;
            }

            AntibotTailBox.Apply(key, keyLength, encrypt, buf, buf.Length - n, n);
        }
    }
}
