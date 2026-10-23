using Dnf.Render;

namespace Dnf.Render.Tests;

/// <summary>
/// 合成的"方块字体"(只给测试用,不含任何真实字形):字号 4,基线以上 3、以下 1,行距 4。
/// 窄字(ASCII 可见字符):2×3 的实心块,前进 3;全角字(<see cref="GlyphText.IsWide"/>):3×3 的实心块,前进 4;
/// 空格:没有墨迹,前进 2。<see cref="Missing"/> 里的码点"字体里没有"。<see cref="Alpha"/> 是实心块的覆盖率(默认 255)。
/// </summary>
public sealed class BlockFont : IGlyphSource
{
    public HashSet<int> Missing { get; } = new HashSet<int>();
    public byte Alpha { get; set; } = 255;
    public int BearingX { get; set; }
    public int Lookups { get; private set; }

    public int PixelSize => 4;
    public int Ascent => 3;
    public int Descent => 1;
    public int LineHeight => 4;

    public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
    {
        Lookups++;
        glyph = null;
        if (Missing.Contains(codePoint))
        {
            return false;
        }
        if (codePoint == ' ')
        {
            glyph = GlyphBitmap.Blank(2);
            return true;
        }
        bool wide = GlyphText.IsWide(codePoint);
        int w = wide ? 3 : 2;
        var alpha = new byte[w * 3];
        Array.Fill(alpha, Alpha);
        glyph = new GlyphBitmap(w, 3, BearingX, 3, wide ? 4 : 3, alpha);
        return true;
    }
}

/// <summary>字体集(测试用):不管要几号都给同一个 <see cref="BlockFont"/>,并记下被问过的字号。</summary>
public sealed class BlockFonts : IGlyphFonts
{
    public BlockFont Font { get; } = new BlockFont();

    public List<int> Requested { get; } = new List<int>();

    public IGlyphSource ForSize(int pixelSize)
    {
        Requested.Add(pixelSize);
        return Font;
    }
}
