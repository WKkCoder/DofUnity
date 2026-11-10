namespace Dnf.Net.Session
{
    /// <summary>
    /// 算法 5:TEA 族自定义变体(32 轮);8 字节 ECB、小端字;密钥 key32 前 16 字节。
    /// 每轮先按当前 sum 更新 v0,再 sum -= delta,再更新 v1;尾部 keylen=16。
    /// </summary>
    internal static class AntibotTean
    {
        private const uint Delta = 0x9E3779B9u;
        private const int Rounds = 32;
        private const int TailKeyBytes = 16;

        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            uint[] k = new uint[4];
            for (int i = 0; i < 4; i++)
            {
                int o = i * 4;
                k[i] = (uint)(key32[o] | (key32[o + 1] << 8) | (key32[o + 2] << 16) | (key32[o + 3] << 24));
            }

            int blocks = buf.Length >> 3;
            for (int b = 0; b < blocks; b++)
            {
                int o = b * 8;
                uint v0 = LoadLe32(buf, o);
                uint v1 = LoadLe32(buf, o + 4);
                if (encrypt)
                {
                    TeanEnc(ref v0, ref v1, k);
                }
                else
                {
                    TeanDec(ref v0, ref v1, k);
                }

                StoreLe32(buf, o, v0);
                StoreLe32(buf, o + 4, v1);
            }

            AntibotTail.ProcessLastBytes(key32, TailKeyBytes, encrypt, buf);
        }

        private static void TeanEnc(ref uint v0, ref uint v1, uint[] k)
        {
            uint sum = 0;
            for (int i = 0; i < Rounds; i++)
            {
                v0 += (((v1 << 4) ^ (v1 >> 5)) + (v1 ^ sum)) + k[sum & 3];
                sum += 0x9E3779B9u;
                v1 += (v0 ^ sum) + k[(sum >> 11) & 3] + (((v0 << 4) ^ (v0 >> 5)));
            }
        }

        private static void TeanDec(ref uint v0, ref uint v1, uint[] k)
        {
            uint sum = unchecked(Delta * (uint)Rounds);
            for (int i = 0; i < Rounds; i++)
            {
                v1 -= (v0 ^ sum) + k[(sum >> 11) & 3] + (((v0 << 4) ^ (v0 >> 5)));
                sum -= 0x9E3779B9u;
                v0 -= (((v1 << 4) ^ (v1 >> 5)) + (v1 ^ sum)) + k[sum & 3];
            }
        }

        private static uint LoadLe32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void StoreLe32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }
    }
}
