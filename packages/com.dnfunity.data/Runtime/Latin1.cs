using System;

namespace Dnf.Data
{
    /// <summary>
    /// 逐字节映射到同码位字符。PVF 路径与字符串池按这个方式解码,
    /// 不依赖任何代码页(IL2CPP 裁剪后也不会缺编码表)。
    /// </summary>
    public static class Latin1
    {
        public static string Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }
            if (bytes.Length <= 256)
            {
                Span<char> small = stackalloc char[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    small[i] = (char)bytes[i];
                }
                return new string(small);
            }
            var big = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                big[i] = (char)bytes[i];
            }
            return new string(big);
        }
    }
}
