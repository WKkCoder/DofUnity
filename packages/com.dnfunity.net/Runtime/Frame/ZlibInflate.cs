using System;
using System.IO;
using System.IO.Compression;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 解 zlib 数据(2 字节头 + deflate 流 + 4 字节 Adler-32,后面可以跟补齐的 0x00)。
    /// deflate 流的结束位置读不出来,所以用 Adler-32 在尾部补零之前的位置确认:解压结果的 Adler-32(大端)必须紧跟在某个位置上,
    /// 其后全是 0x00;这样被截断的流不会被当成成功。
    /// </summary>
    internal static class ZlibInflate
    {
        public static bool TryInflate(byte[] data, int maxOutput, out byte[] output, out bool tooLarge)
        {
            output = null;
            tooLarge = false;
            if (data.Length < 7) return false;
            int cmf = data[0];
            int flg = data[1];
            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7) return false;
            if (((cmf << 8) | flg) % 31 != 0) return false;
            if ((flg & 0x20) != 0) return false;
            byte[] result;
            try
            {
                using (MemoryStream source = new MemoryStream(data, 2, data.Length - 2, false))
                using (DeflateStream inflater = new DeflateStream(source, CompressionMode.Decompress))
                using (MemoryStream sink = new MemoryStream())
                {
                    byte[] chunk = new byte[4096];
                    while (true)
                    {
                        int read = inflater.Read(chunk, 0, chunk.Length);
                        if (read <= 0) break;
                        if (sink.Length + read > maxOutput)
                        {
                            tooLarge = true;
                            return false;
                        }
                        sink.Write(chunk, 0, read);
                    }
                    result = sink.ToArray();
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            if (!AdlerFollows(data, Adler32(result))) return false;
            output = result;
            return true;
        }

        /// <summary>Adler-32(RFC 1950)。</summary>
        public static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte x in data)
            {
                a = (a + x) % 65521u;
                b = (b + a) % 65521u;
            }
            return (b << 16) | a;
        }

        private static bool AdlerFollows(byte[] data, uint adler)
        {
            for (int p = data.Length - 4; p >= 2; p--)
            {
                uint value = ((uint)data[p] << 24) | ((uint)data[p + 1] << 16) | ((uint)data[p + 2] << 8) | data[p + 3];
                if (value == adler) return true;
                // 只允许尾部有补齐的 0x00:p 之后第 4 个字节起必须全是 0
                if (data[p + 3] != 0) return false;
            }
            return false;
        }
    }
}
