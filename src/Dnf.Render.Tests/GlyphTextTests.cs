using Dnf.Render;

namespace Dnf.Render.Tests;

/// <summary>文字排版(<see cref="GlyphText"/>):合成的方块字体,不依赖任何真实字体或原版数据。</summary>
public class GlyphTextTests
{
    // 方块字体里:窄字前进 3、全角字前进 4、空格前进 2;宽字用 "一二三四五" 这些全角汉字。
    private const string FiveWide = "一二三四五";

    [Fact]
    public void MeasureSumsAdvances()
    {
        var f = new BlockFont();
        Assert.Equal(6, GlyphText.Measure(f, "ab"));
        Assert.Equal(8, GlyphText.Measure(f, "一二"));
        Assert.Equal(7, GlyphText.Measure(f, "a一"));
        Assert.Equal(8, GlyphText.Measure(f, "a b"));
        Assert.Equal(0, GlyphText.Measure(f, string.Empty));
        Assert.Equal(0, GlyphText.Measure(f, null));
    }

    [Fact]
    public void MeasureOfSeveralLinesIsTheWidestLine()
    {
        var f = new BlockFont();
        Assert.Equal(9, GlyphText.Measure(f, "ab\nabc"));
        Assert.Equal(9, GlyphText.Measure(f, "abc\r\nab"));
    }

    [Fact]
    public void MissingGlyphsAreEstimatedFromTheFontSize()
    {
        var f = new BlockFont();
        f.Missing.Add('一');
        f.Missing.Add('x');
        Assert.Equal(4, GlyphText.AdvanceOf(f, '一'));     // 全角:一个字号
        Assert.Equal(2, GlyphText.AdvanceOf(f, 'x'));      // 其余:半个字号
        Assert.Equal(3, GlyphText.AdvanceOf(f, 'a'));
    }

    [Fact]
    public void WrapWithoutAWidthSplitsOnNewlinesOnly()
    {
        var f = new BlockFont();
        Assert.Equal(new[] { "ab", "cd" }, GlyphText.Wrap(f, "ab\ncd", 0));
        Assert.Equal(new[] { "a", string.Empty, "b" }, GlyphText.Wrap(f, "a\n\nb", 0));
        Assert.Equal(new[] { "a", "b" }, GlyphText.Wrap(f, "a\r\nb\n", 0));
        Assert.Empty(GlyphText.Wrap(f, string.Empty, 10));
        Assert.Empty(GlyphText.Wrap(f, "\n\n", 10));
    }

    [Fact]
    public void WrapBreaksBetweenAnyTwoWideCharacters()
    {
        var f = new BlockFont();
        Assert.Equal(new[] { "一二", "三四", "五" }, GlyphText.Wrap(f, FiveWide, 8));
        Assert.Equal(new[] { FiveWide }, GlyphText.Wrap(f, FiveWide, 20));
    }

    [Fact]
    public void PunctuationThatCannotStartALineHangsOnTheLineBefore()
    {
        var f = new BlockFont();
        Assert.Equal(new[] { "一二，", "三" }, GlyphText.Wrap(f, "一二，三", 8));
        Assert.Equal(new[] { "一二。", "三四" }, GlyphText.Wrap(f, "一二。三四", 8));
    }

    [Fact]
    public void AnOpeningBracketNeverEndsALine()
    {
        var f = new BlockFont();
        Assert.Equal(new[] { "一二", "（三" }, GlyphText.Wrap(f, "一二（三", 8));
    }

    [Fact]
    public void LatinWordsBreakAtSpaces()
    {
        var f = new BlockFont();
        Assert.Equal(new[] { "abc", "def" }, GlyphText.Wrap(f, "abc def", 9));
        Assert.Equal(new[] { "abc", "def" }, GlyphText.Wrap(f, "abc    def", 9));
    }

    [Fact]
    public void AWordLongerThanTheLineIsCutByForce()
    {
        var f = new BlockFont();
        Assert.Equal(new[] { "ab", "cd", "ef", "g" }, GlyphText.Wrap(f, "abcdefg", 6));
    }

    [Fact]
    public void ALineNarrowerThanOneGlyphStillMakesProgress()
    {
        var f = new BlockFont();
        Assert.Equal(new[] { "一", "二", "三" }, GlyphText.Wrap(f, "一二三", 2));
    }

    [Fact]
    public void LatinThenWideBreaksBeforeTheWideCharacter()
    {
        var f = new BlockFont();
        // a(3) b(3) → 6;"一"(4) 放不下(10 > 8),在它前面断
        Assert.Equal(new[] { "ab", "一二" }, GlyphText.Wrap(f, "ab一二", 8));
    }

    [Fact]
    public void TruncateKeepsWhatFitsAndAddsAnEllipsis()
    {
        var f = new BlockFont();
        Assert.Equal(FiveWide, GlyphText.Truncate(f, FiveWide, 20));
        Assert.Equal(FiveWide, GlyphText.Truncate(f, FiveWide, 0));
        // 一二三(12) + 省略号(4) = 16 > 14,去一个字:一二 + … = 12
        Assert.Equal("一二\u2026", GlyphText.Truncate(f, FiveWide, 14));
        Assert.Equal("\u2026", GlyphText.Truncate(f, FiveWide, 3));
        Assert.Equal(string.Empty, GlyphText.Truncate(f, null, 10));
    }

    [Fact]
    public void TruncateFallsBackToDotsWhenTheFontHasNoEllipsis()
    {
        var f = new BlockFont();
        f.Missing.Add(0x2026);
        // 三个点每个窄字前进 3,共 9;一二(8) + 9 = 17 > 14,去字到 一(4) + 9 = 13
        Assert.Equal("一...", GlyphText.Truncate(f, FiveWide, 14));
    }

    [Fact]
    public void RenderOfEmptyTextIsNull()
    {
        var f = new BlockFont();
        Assert.Null(GlyphText.Render(f, string.Empty, 0xFFFFFFu, 0u));
        Assert.Null(GlyphText.Render(f, null, 0xFFFFFFu, 0u));
        Assert.Null(GlyphText.Render(f, "\n\n", 0xFFFFFFu, 0u));
    }

    [Fact]
    public void RenderRejectsANullFont()
    {
        Assert.Throws<ArgumentNullException>(() => GlyphText.Render(null, "a", 0u, 0u));
        Assert.Throws<ArgumentNullException>(() => GlyphText.Measure(null, "a"));
        Assert.Throws<ArgumentNullException>(() => GlyphText.Wrap(null, "a", 5));
        Assert.Throws<ArgumentNullException>(() => GlyphText.Truncate(null, "a", 5));
        Assert.Throws<ArgumentNullException>(() => GlyphText.AdvanceOf(null, 'a'));
    }

    [Fact]
    public void RenderPlacesTheGlyphWithAOnePixelOutline()
    {
        var f = new BlockFont();
        SpriteFrame s = GlyphText.Render(f, "a", 0xFF0000u, 0x0000FFu);
        // 排版框 3 × 4(前进 3、行距 4),墨迹 2 × 3 在框内;四周各加 1 像素描边
        Assert.Equal(5, s.Width);
        Assert.Equal(6, s.Height);
        Assert.Equal(5, s.CanvasWidth);
        Assert.Equal(0, s.OffsetX);
        // 填色:x 1..2,y 1..3
        Assert.Equal((255, 0, 0, 255), Pixel(s, 1, 1));
        Assert.Equal((255, 0, 0, 255), Pixel(s, 2, 3));
        // 描边:填色的八邻域
        Assert.Equal((0, 0, 255, 255), Pixel(s, 0, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(s, 3, 1));
        Assert.Equal((0, 0, 255, 255), Pixel(s, 1, 4));
        // 更远的地方是全透明
        Assert.Equal((0, 0, 0, 0), Pixel(s, 4, 1));
        Assert.Equal((0, 0, 0, 0), Pixel(s, 1, 5));
    }

    [Fact]
    public void RenderCountsMatchTheGlyphInk()
    {
        var f = new BlockFont();
        SpriteFrame s = GlyphText.Render(f, "ab", 0xFFFFFFu, 0x000000u);
        int fill = 0;
        for (int y = 0; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                if (Pixel(s, x, y) == (255, 255, 255, 255))
                {
                    fill++;
                }
            }
        }
        Assert.Equal(12, fill);   // 两个 2×3 的块
    }

    [Fact]
    public void PartialCoverageBlendsOverTheOutline()
    {
        var f = new BlockFont { Alpha = 128 };
        SpriteFrame s = GlyphText.Render(f, "a", 0xFFFFFFu, 0x000000u);
        // 填色处:覆盖率 128;描边取邻域最大覆盖率 128,垫在下面
        // alpha = 128 + 128 * 127 / 255 = 191;红 = 255 * 128 * 255 / (191 * 255) = 170
        (int r, int g, int b, int a) = Pixel(s, 1, 1);
        Assert.Equal(191, a);
        Assert.Equal(170, r);
        Assert.Equal(170, g);
        Assert.Equal(170, b);
        // 只有描边的像素:覆盖率 0 + 描边 128
        Assert.Equal((0, 0, 0, 128), Pixel(s, 0, 0));
    }

    [Fact]
    public void RenderWrapsIntoSeveralLines()
    {
        var f = new BlockFont();
        SpriteFrame s = GlyphText.Render(f, FiveWide, 0xFFFFFFu, 0u, maxWidth: 8);
        // 三行(二二一),每行行距 4、行间空 2:4*3 + 2*2 = 16,加描边 2
        Assert.Equal(18, s.Height);
        // 排版框宽 8,加描边 2
        Assert.Equal(10, s.Width);
    }

    [Fact]
    public void LineGapIsConfigurable()
    {
        var f = new BlockFont();
        SpriteFrame tight = GlyphText.Render(f, "a\nb", 0xFFFFFFu, 0u, lineGap: 0);
        SpriteFrame loose = GlyphText.Render(f, "a\nb", 0xFFFFFFu, 0u, lineGap: 5);
        Assert.Equal(8 + 2, tight.Height);
        Assert.Equal(8 + 5 + 2, loose.Height);
    }

    [Fact]
    public void MaxLinesTruncatesWithAnEllipsisOnTheLastLine()
    {
        var f = new BlockFont();
        SpriteFrame s = GlyphText.Render(f, FiveWide, 0xFFFFFFu, 0u, maxWidth: 8, maxLines: 2);
        // 两行:4*2 + 2 = 10,加描边 2
        Assert.Equal(12, s.Height);
        // 第二行是 "三…":用墨迹数核对 —— 第一行 2 个全角块 + 第二行 2 个全角块(三、…),每块 9 像素
        int fill = 0;
        for (int y = 0; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                if (Pixel(s, x, y).Item4 == 255 && Pixel(s, x, y).Item1 == 255)
                {
                    fill++;
                }
            }
        }
        Assert.Equal(4 * 9, fill);
    }

    [Fact]
    public void MaxLinesLargerThanTheTextChangesNothing()
    {
        var f = new BlockFont();
        SpriteFrame a = GlyphText.Render(f, "一二", 0xFFFFFFu, 0u, maxWidth: 20, maxLines: 3);
        SpriteFrame b = GlyphText.Render(f, "一二", 0xFFFFFFu, 0u, maxWidth: 20);
        Assert.Equal(a.Rgba, b.Rgba);
    }

    [Fact]
    public void GlyphsThatOverhangTheLayoutBoxAreNotClipped()
    {
        var f = new BlockFont { BearingX = -2 };
        SpriteFrame s = GlyphText.Render(f, "a", 0xFFFFFFu, 0x000000u);
        // 墨迹从 x = -2 到 0(宽 2),排版框 0..3;画布从 -2 到 3,加描边:5 + 2 = 7
        Assert.Equal(7, s.Width);
        int fill = 0;
        for (int y = 0; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                if (Pixel(s, x, y) == (255, 255, 255, 255))
                {
                    fill++;
                }
            }
        }
        Assert.Equal(6, fill);
    }

    [Fact]
    public void MissingGlyphsDrawNothingButStillAdvance()
    {
        var f = new BlockFont();
        f.Missing.Add('?');
        SpriteFrame withMissing = GlyphText.Render(f, "a?b", 0xFFFFFFu, 0u);
        SpriteFrame without = GlyphText.Render(f, "ab", 0xFFFFFFu, 0u);
        // 缺的字按半个字号(2)估宽
        Assert.Equal(without.Width + 2, withMissing.Width);
    }

    [Fact]
    public void SpacesAdvanceWithoutInk()
    {
        var f = new BlockFont();
        SpriteFrame s = GlyphText.Render(f, "a b", 0xFFFFFFu, 0u);
        SpriteFrame t = GlyphText.Render(f, "ab", 0xFFFFFFu, 0u);
        Assert.Equal(t.Width + 2, s.Width);
    }

    [Fact]
    public void SurrogatePairsAreOneGlyph()
    {
        var f = new BlockFont();
        string pair = char.ConvertFromUtf32(0x20000);
        Assert.Equal(4, GlyphText.Measure(f, pair));
        Assert.Equal(new[] { pair + pair }, GlyphText.Wrap(f, pair + pair, 20));
        Assert.Equal(new[] { pair, pair }, GlyphText.Wrap(f, pair + pair, 5));
    }

    [Fact]
    public void WideClassification()
    {
        Assert.True(GlyphText.IsWide('中'));
        Assert.True(GlyphText.IsWide('，'));
        Assert.True(GlyphText.IsWide('あ'));
        Assert.True(GlyphText.IsWide('한'));
        Assert.True(GlyphText.IsWide(0x20000));
        Assert.False(GlyphText.IsWide('a'));
        Assert.False(GlyphText.IsWide('1'));
        Assert.False(GlyphText.IsWide(' '));
    }

    [Fact]
    public void GlyphBitmapValidatesItsArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlyphBitmap(-1, 1, 0, 0, 1, new byte[0]));
        Assert.Throws<ArgumentNullException>(() => new GlyphBitmap(1, 1, 0, 0, 1, null));
        Assert.Throws<ArgumentException>(() => new GlyphBitmap(2, 2, 0, 0, 1, new byte[3]));
        GlyphBitmap blank = GlyphBitmap.Blank(5);
        Assert.Equal(0, blank.Width);
        Assert.Equal(5, blank.Advance);
    }

    private static (int, int, int, int) Pixel(SpriteFrame s, int x, int y)
    {
        int o = (y * s.Width + x) * 4;
        return (s.Rgba[o], s.Rgba[o + 1], s.Rgba[o + 2], s.Rgba[o + 3]);
    }
}
