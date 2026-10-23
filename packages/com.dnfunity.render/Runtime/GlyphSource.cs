using System;

namespace Dnf.Render
{
    /// <summary>
    /// 一个字形的单通道位图(每像素 0 到 255 的覆盖率),由字体栅格化得到。第 0 行在最上面;
    /// <see cref="BearingX"/> 是位图左边缘相对笔位置的水平偏移(像素,可为负),<see cref="BearingY"/> 是位图上边缘在基线之上的像素数,
    /// <see cref="Advance"/> 是画完这个字笔位置向右前进的像素数。空格这样没有墨迹的字形位图宽高为 0,只有 <see cref="Advance"/> 有意义。
    /// </summary>
    public sealed class GlyphBitmap
    {
        public int Width { get; }
        public int Height { get; }
        public int BearingX { get; }
        public int BearingY { get; }
        public int Advance { get; }

        /// <summary><see cref="Width"/> × <see cref="Height"/> 个字节,按行排。</summary>
        public byte[] Alpha { get; }

        public GlyphBitmap(int width, int height, int bearingX, int bearingY, int advance, byte[] alpha)
        {
            if (width < 0 || height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            if (alpha == null)
            {
                throw new ArgumentNullException(nameof(alpha));
            }
            if (alpha.Length != width * height)
            {
                throw new ArgumentException("位图字节数要等于 宽 × 高", nameof(alpha));
            }
            Width = width;
            Height = height;
            BearingX = bearingX;
            BearingY = bearingY;
            Advance = advance;
            Alpha = alpha;
        }

        /// <summary>没有墨迹、只占一段宽度的字形(空格)。</summary>
        public static GlyphBitmap Blank(int advance)
        {
            return new GlyphBitmap(0, 0, 0, 0, advance, new byte[0]);
        }
    }

    /// <summary>
    /// 某一个字号的字体:按码点给出字形位图。由宿主实现(Unity 里用 TextCore 读系统字体,见 UnityGlyphFonts),
    /// 渲染层只管把字形排成一段文字(<see cref="GlyphText"/>)。实现要自己缓存位图,同一个码点反复取要便宜。
    /// </summary>
    public interface IGlyphSource
    {
        /// <summary>字号(像素)。</summary>
        int PixelSize { get; }

        /// <summary>基线以上的像素数(行内最高的字形顶到基线)。</summary>
        int Ascent { get; }

        /// <summary>基线以下的像素数(正数)。</summary>
        int Descent { get; }

        /// <summary>行距:相邻两行基线之间的像素数(通常是 <see cref="Ascent"/> + <see cref="Descent"/> 加一点行间空)。</summary>
        int LineHeight { get; }

        /// <summary>取一个码点的字形;字体里没有这个字返回 false(排版时画成空白、按字号估一个宽度)。</summary>
        bool TryGetGlyph(int codePoint, out GlyphBitmap glyph);
    }

    /// <summary>一套字体的各个字号:同一个字体文件按不同的字号各栅格化一份。</summary>
    public interface IGlyphFonts
    {
        /// <summary>取 <paramref name="pixelSize"/> 像素的那一份;字号超出范围由实现夹紧,拿不到字体返回 null。</summary>
        IGlyphSource ForSize(int pixelSize);
    }
}
