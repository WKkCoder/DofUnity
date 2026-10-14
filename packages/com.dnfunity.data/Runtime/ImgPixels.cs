using System;
using System.Buffers.Binary;

namespace Dnf.Data
{
    /// <summary>
    /// 像素格式转换。输出统一为 RGBA8888(字节顺序 R、G、B、A),行优先,不填充。
    /// 4444 每通道乘 17;1555 用位复制 (v&lt;&lt;3)|(v&gt;&gt;2),A 位为 1 时不透明、为 0 时全透明。
    /// 8888 在内存里是 B、G、R、A。
    /// 注意:这是参考实现的约定,尚未与原版客户端的屏幕输出逐像素比较。
    /// </summary>
    public static class ImgPixels
    {
        private static uint[] _lut4444;
        private static uint[] _lut1555;

        private static uint[] Lut4444
        {
            get
            {
                uint[] t = _lut4444;
                if (t == null)
                {
                    t = new uint[65536];
                    for (int p = 0; p < 65536; p++)
                    {
                        uint a = (uint)((p >> 12) & 15), r = (uint)((p >> 8) & 15), g = (uint)((p >> 4) & 15), b = (uint)(p & 15);
                        t[p] = Pack(r * 17, g * 17, b * 17, a * 17);
                    }
                    _lut4444 = t;
                }
                return t;
            }
        }

        private static uint[] Lut1555
        {
            get
            {
                uint[] t = _lut1555;
                if (t == null)
                {
                    t = new uint[65536];
                    for (int p = 0; p < 65536; p++)
                    {
                        uint a = (uint)((p >> 15) & 1), r = (uint)((p >> 10) & 31), g = (uint)((p >> 5) & 31), b = (uint)(p & 31);
                        t[p] = Pack((r << 3) | (r >> 2), (g << 3) | (g >> 2), (b << 3) | (b >> 2), a != 0 ? 255u : 0u);
                    }
                    _lut1555 = t;
                }
                return t;
            }
        }

        /// <summary>把四个通道按 R、G、B、A 的内存顺序打成小端 uint。</summary>
        private static uint Pack(uint r, uint g, uint b, uint a) => r | (g << 8) | (b << 16) | (a << 24);

        /// <param name="pixelType">0x0E、0x0F 或 0x10。</param>
        /// <param name="raw">按存储格式排布的原始像素。</param>
        /// <param name="pixelCount">像素个数(宽 × 高)。</param>
        public static byte[] ToRgba(int pixelType, ReadOnlySpan<byte> raw, int pixelCount)
        {
            if (pixelCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelCount));
            }
            int bpp = ImgPixelFormat.BytesPerPixel(pixelType);
            if (bpp == 0)
            {
                throw new DnfFormatException("未知像素类型 0x" + pixelType.ToString("X"));
            }
            if (raw.Length < (long)pixelCount * bpp)
            {
                throw new DnfFormatException("原始像素不足: 需要 " + (long)pixelCount * bpp + " 实得 " + raw.Length);
            }
            var rgba = new byte[checked(pixelCount * 4)];
            if (pixelType == ImgPixelFormat.Argb8888)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    int s = i * 4;
                    rgba[s] = raw[s + 2];
                    rgba[s + 1] = raw[s + 1];
                    rgba[s + 2] = raw[s];
                    rgba[s + 3] = raw[s + 3];
                }
                return rgba;
            }
            uint[] lut = pixelType == ImgPixelFormat.Argb4444 ? Lut4444 : Lut1555;
            Span<byte> dest = rgba;
            for (int i = 0; i < pixelCount; i++)
            {
                ushort p = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(i * 2));
                BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(i * 4), lut[p]);
            }
            return rgba;
        }
    }
}
