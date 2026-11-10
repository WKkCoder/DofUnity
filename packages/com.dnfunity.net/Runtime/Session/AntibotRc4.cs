namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 14:标准 RC4 流密码,密钥取 key32 前 16 字节;无尾部钩子。
    /// </summary>
    internal static class AntibotRc4
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            byte[] state = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                state[i] = (byte)i;
            }

            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + state[i] + key32[i % 16]) & 0xFF;
                byte t = state[i];
                state[i] = state[j];
                state[j] = t;
            }

            int a = 0;
            j = 0;
            for (int n = 0; n < buf.Length; n++)
            {
                a = (a + 1) & 0xFF;
                j = (j + state[a]) & 0xFF;
                byte t = state[a];
                state[a] = state[j];
                state[j] = t;
                int k = state[(state[a] + state[j]) & 0xFF];
                buf[n] ^= (byte)k;
            }
        }
    }
}
