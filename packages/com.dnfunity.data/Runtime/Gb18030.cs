using System;

// 证据:*.kor.str 的编码是 GB18030(全库只出现单字节与双字节,没有四字节序列),见 E-121。
namespace Dnf.Data
{
    /// <summary>
    /// GB18030 解码器(只做解码),不依赖运行时有没有 GBK 编码器:IL2CPP 的 .NET Standard 2.1 配置不保证带它。
    /// 单字节 0x00–0x7F 是 ASCII;首字节 0x81–0xFE 配次字节 0x40–0xFE(0x7F 除外)是双字节,查 <see cref="Gb18030Table"/>;
    /// 四字节序列(次字节 0x30–0x39)整体吃掉、解成一个 U+FFFD;其余非法字节各解成一个 U+FFFD,不抛异常。
    /// </summary>
    public static class Gb18030
    {
        public const char Replacement = '\uFFFD';

        public static string Decode(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return Decode(new ReadOnlySpan<byte>(data));
        }

        public static string Decode(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return Decode(new ReadOnlySpan<byte>(data, offset, count));
        }

        public static string Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                return string.Empty;
            }
            var buffer = new char[bytes.Length];
            int n = 0;
            int i = 0;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b < 0x80)
                {
                    buffer[n++] = (char)b;
                    i++;
                    continue;
                }
                if (b == 0x80 || b == 0xFF || i + 1 >= bytes.Length)
                {
                    buffer[n++] = Replacement;
                    i++;
                    continue;
                }
                byte t = bytes[i + 1];
                if (t >= Gb18030Table.FirstTrail && t <= Gb18030Table.LastTrail && t != 0x7F)
                {
                    buffer[n++] = Gb18030Table.Rows[b - Gb18030Table.FirstLead][t - Gb18030Table.FirstTrail];
                    i += 2;
                    continue;
                }
                if (t >= 0x30 && t <= 0x39 && i + 3 < bytes.Length
                    && bytes[i + 2] >= 0x81 && bytes[i + 2] <= 0xFE && bytes[i + 3] >= 0x30 && bytes[i + 3] <= 0x39)
                {
                    buffer[n++] = Replacement;
                    i += 4;
                    continue;
                }
                buffer[n++] = Replacement;
                i++;
            }
            return new string(buffer, 0, n);
        }
    }
}
