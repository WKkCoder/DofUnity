namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 16:自定义流密码 GENE;密钥前 8 字节与固定掩码异或,LCG 洗牌 256 字节表,无尾部钩子。
    /// </summary>
    internal static class AntibotGene
    {
        private static readonly byte[] KeyMask = { 0x8A, 0xE6, 0x9B, 0xF3, 0xC1, 0x7D, 0x40, 0x25 };

        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            byte[] state = new byte[8];
            byte[] table = new byte[256];
            BuildTables(key32, encrypt, state, table);

            for (int i = 0; i < buf.Length; i++)
            {
                byte b = buf[i];
                if (encrypt)
                {
                    b ^= (byte)i;
                }

                byte k = state[i & 7];
                byte x = table[(byte)(b ^ k)];
                x ^= k;
                if (!encrypt)
                {
                    x ^= (byte)i;
                }

                buf[i] = x;
            }
        }

        private static void BuildTables(byte[] key32, bool encrypt, byte[] state, byte[] table)
        {
            for (int i = 0; i < 8; i++)
            {
                state[i] = (byte)(key32[i] ^ KeyMask[i]);
            }

            for (int i = 0; i < 256; i++)
            {
                table[i] = (byte)i;
            }

            uint w0 = (uint)((state[0] << 24) | (state[1] << 16) | (state[2] << 8) | state[3]);
            uint w1 = (uint)((state[4] << 24) | (state[5] << 16) | (state[6] << 8) | state[7]);
            uint seed = w0 ^ w1;
            for (int i = 1; i <= 0x100; i++)
            {
                seed = (seed * 0x343FDu + 0x269EC3u);
                int j = (int)((seed >> 16) & 0xFF);
                byte a = table[i - 1];
                byte b = table[j];
                table[i - 1] = b;
                table[j] = a;
            }

            if (!encrypt)
            {
                byte[] inv = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    inv[table[i]] = (byte)i;
                }

                for (int i = 0; i < 256; i++)
                {
                    table[i] = inv[i];
                }
            }
        }
    }
}
