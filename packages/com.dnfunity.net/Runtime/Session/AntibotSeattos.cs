namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 15:自定义流密码 SEATTOS;16 字节常量表与 key[i&amp;7] 参与每字节变换,无尾部钩子。
    /// </summary>
    internal static class AntibotSeattos
    {
        private static readonly byte[] RoundConst =
        {
            0x37, 0x92, 0x44, 0x68, 0xA5, 0x3D, 0xCC, 0x7F, 0xBB, 0x0F, 0xD9, 0x88, 0xEE, 0x9A, 0xE9, 0x5A,
        };

        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            for (int i = 0; i < buf.Length; i++)
            {
                byte c = RoundConst[i & 0xF];
                byte k = key32[i & 7];
                byte x = (byte)(buf[i] ^ c ^ k);
                if (encrypt)
                {
                    x = (byte)(c + x);
                    buf[i] = x;
                    x = (byte)(c ^ buf[i]);
                    buf[i] = x;
                    buf[i] = (byte)(x ^ k);
                }
                else
                {
                    x = (byte)(x - c);
                    buf[i] = x;
                    x = (byte)(c ^ buf[i]);
                    buf[i] = x;
                    buf[i] = (byte)(x ^ k);
                }
            }
        }
    }
}
