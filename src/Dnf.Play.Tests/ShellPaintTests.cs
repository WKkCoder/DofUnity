using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳的"画笔"(<see cref="ShellPaint"/>):文字(有字体按字号画、没字体退回点阵字库、放不下缩小再截断、账号取结尾、标题加粗)
/// 与形状(圆角块、光晕、圆点、菱形、渐变线、角括号、暗角、转圈、提示图标、标题字)。形状的测试看像素:角上透明、里面不透明、颜色与渐变方向对。
/// 同样的参数必须返回同一个对象(Unity 里的贴图按帧对象缓存)。
/// </summary>
public class ShellPaintTests
{
    private static (OverlayArt Art, ShellPaint Paint) Plain()
    {
        var art = new OverlayArt { Localize = false };
        return (art, new ShellPaint(art));
    }

    private static ShellPaint WithFonts(IGlyphFonts fonts) => new ShellPaint(new OverlayArt { Localize = false, Fonts = fonts });

    private static ShellBox Flat(uint top, uint bottom, uint edge, int edgeWidth = 1, int gloss = 0, int lip = 0) => new ShellBox(top, bottom, edge, edge, edgeWidth, gloss, lip);

    // ------------------------------------------------------------------------------------------------ 小件

    [Theory]
    [InlineData(255, 0x123456u, 0xFF123456u)]
    [InlineData(0, 0x123456u, 0x00123456u)]
    [InlineData(-5, 0x123456u, 0x00123456u)]
    [InlineData(300, 0xABCDEFu, 0xFFABCDEFu)]
    [InlineData(10, 0xAA123456u, 0x0A123456u)]
    public void Argb_clamps_the_alpha_and_keeps_only_the_colour_bits(int alpha, uint rgb, uint expected)
    {
        Assert.Equal(expected, ShellPaint.Argb(alpha, rgb));
    }

    [Fact]
    public void Each_tone_has_its_own_main_colour()
    {
        Assert.Equal(ShellPaint.Red, ShellPaint.ToneColor(ShellTone.Error));
        Assert.Equal(ShellPaint.Green, ShellPaint.ToneColor(ShellTone.Success));
        Assert.Equal(ShellPaint.Blue, ShellPaint.ToneColor(ShellTone.Wait));
        Assert.Equal(ShellPaint.Gold, ShellPaint.ToneColor(ShellTone.Info));
        Assert.Equal(4, new[] { ShellTone.Error, ShellTone.Success, ShellTone.Wait, ShellTone.Info }.Select(ShellPaint.ToneColor).Distinct().Count());
    }

    [Fact]
    public void The_paint_needs_an_art()
    {
        Assert.Throws<ArgumentNullException>(() => new ShellPaint(null));
    }

    [Fact]
    public void The_paint_knows_whether_there_are_fonts()
    {
        Assert.False(Plain().Paint.HasFonts);
        Assert.True(WithFonts(new RecordingFonts()).HasFonts);
    }

    [Fact]
    public void A_box_style_keeps_its_fields_and_a_negative_edge_width_becomes_none()
    {
        var style = new ShellBox(1, 2, 3, 4, 5, 6, 7);

        Assert.Equal((1u, 2u, 3u, 4u, 5, 6, 7), (style.Top, style.Bottom, style.EdgeTop, style.EdgeBottom, style.EdgeWidth, style.Gloss, style.Lip));
        Assert.Equal(0, new ShellBox(1, 2, 3, 4, -3).EdgeWidth);
        var hint = new ShellHint("Tab", "切换");
        Assert.Equal(("Tab", "切换"), (hint.Key, hint.Text));
    }

    [Fact]
    public void Every_field_of_a_box_style_is_part_of_its_key()
    {
        var keys = new[]
        {
            new ShellBox(1, 2, 3, 4, 5, 6, 7).Key,
            new ShellBox(9, 2, 3, 4, 5, 6, 7).Key,
            new ShellBox(1, 9, 3, 4, 5, 6, 7).Key,
            new ShellBox(1, 2, 9, 4, 5, 6, 7).Key,
            new ShellBox(1, 2, 3, 9, 5, 6, 7).Key,
            new ShellBox(1, 2, 3, 4, 9, 6, 7).Key,
            new ShellBox(1, 2, 3, 4, 5, 9, 7).Key,
            new ShellBox(1, 2, 3, 4, 5, 6, 9).Key,
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal(new ShellBox(1, 2, 3, 4, 5, 6, 7).Key, new ShellBox(1, 2, 3, 4, 5, 6, 7).Key);
    }

    // ------------------------------------------------------------------------------------------------ 文字:没有字体

    [Theory]
    [InlineData(8, 1)]
    [InlineData(12, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(26, 2)]
    [InlineData(27, 3)]
    [InlineData(36, 3)]
    [InlineData(37, 4)]
    [InlineData(44, 4)]
    [InlineData(45, 5)]
    [InlineData(46, 5)]
    [InlineData(99, 5)]
    public void The_bitmap_scale_follows_the_pixel_size(int px, int scale)
    {
        Assert.Equal(scale, ShellPaint.BlockScale(px));
    }

    [Fact]
    public void Nothing_to_draw_gives_no_frame_from_every_text_method()
    {
        (_, ShellPaint plain) = Plain();
        ShellPaint withFonts = WithFonts(new RecordingFonts());

        foreach (ShellPaint paint in new[] { plain, withFonts })
        {
            Assert.Null(paint.Line(null, 16, 0xFFFFFF, 0));
            Assert.Null(paint.Line(string.Empty, 16, 0xFFFFFF, 0));
            Assert.Null(paint.Data(null, 16, 0xFFFFFF, 0, 100));
            Assert.Null(paint.Data(string.Empty, 16, 0xFFFFFF, 0, 100));
            Assert.Null(paint.Banner(null, 46, 1, 2, 3, 4));
            Assert.Null(paint.Banner(string.Empty, 46, 1, 2, 3, 4));
        }
    }

    [Fact]
    public void Without_fonts_a_line_is_the_bitmap_font_at_the_scale_of_its_pixel_size()
    {
        (OverlayArt art, ShellPaint paint) = Plain();

        Assert.Same(art.Text("ENTER GAME", 2, 0x3B2505, 0xF0B640), paint.Line("ENTER GAME", 20, 0x3B2505, 0xF0B640));
        Assert.Same(art.Text("REGISTER", 1, 0xD6E4FF, 0x0A1224), paint.Line("REGISTER", 16, 0xD6E4FF, 0x0A1224));
        Assert.Same(art.Text("TITLE", 3, 0xFFFFFF, 0), paint.Line("TITLE", 30, 0xFFFFFF, 0));
    }

    [Fact]
    public void Without_fonts_text_the_bitmap_font_cannot_draw_becomes_capitals_dashes_and_question_marks()
    {
        (OverlayArt art, ShellPaint paint) = Plain();

        Assert.Same(art.Text("AB-?", 1, 0xFFFFFF, 0), paint.Line("Ab_名", 14, 0xFFFFFF, 0));
        Assert.Same(art.Text("AB-?", 1, 0xFFFFFF, 0), paint.Data("Ab_名", 14, 0xFFFFFF, 0, 0));
    }

    [Fact]
    public void Without_fonts_a_line_that_does_not_fit_drops_a_scale_before_it_is_cut()
    {
        (OverlayArt art, ShellPaint paint) = Plain();

        // 15 个字:2 倍宽 180、1 倍宽 91
        Assert.Same(art.Text("CHANGE PASSWORD", 2, 0xFFFFFF, 0), paint.Line("CHANGE PASSWORD", 20, 0xFFFFFF, 0, 180));
        Assert.Same(art.Text("CHANGE PASSWORD", 1, 0xFFFFFF, 0), paint.Line("CHANGE PASSWORD", 20, 0xFFFFFF, 0, 100));
        Assert.Same(art.Text("CHANGE PASSWORD", 2, 0xFFFFFF, 0), paint.Line("CHANGE PASSWORD", 20, 0xFFFFFF, 0, 0));
    }

    [Fact]
    public void Without_fonts_a_line_that_still_does_not_fit_keeps_its_beginning_and_data_keeps_its_end()
    {
        (OverlayArt art, ShellPaint paint) = Plain();

        Assert.Same(art.Text("CHANGE PA", 1, 0xFFFFFF, 0), paint.Line("CHANGE PASSWORD", 16, 0xFFFFFF, 0, 60));
        Assert.Same(art.Text("-01-EXTRA", 1, 0xFFFFFF, 0), paint.Data("player_01_extra", 17, 0xFFFFFF, 0, 60));
        Assert.Equal(55, OverlayArt.TextWidth("CHANGE PA", 1));                     // 截完不超过给定的宽度
    }

    [Fact]
    public void Without_a_font_for_the_size_the_text_falls_back_to_the_bitmap_font_too()
    {
        var art = new OverlayArt { Localize = false, Fonts = new NoFonts() };
        var paint = new ShellPaint(art);

        Assert.Same(art.Text("OK", 1, 0xFFFFFF, 0), paint.Line("OK", 16, 0xFFFFFF, 0));
        Assert.Same(art.Text("OK", 1, 0xFFFFFF, 0), paint.Data("ok", 16, 0xFFFFFF, 0, 100));
        Assert.Same(art.Text("DNF", 5, 0x333333, 0x222222), paint.Banner("DNF", 46, 0x111111, 0x333333, 0x555555, 0x222222));
    }

    [Fact]
    public void Without_fonts_bold_changes_nothing_and_the_banner_is_a_single_colour_bitmap_title()
    {
        (OverlayArt art, ShellPaint paint) = Plain();

        Assert.Same(paint.Line("LOGIN", 26, 0xFFFFFF, 0, 0, false), paint.Line("LOGIN", 26, 0xFFFFFF, 0, 0, true));
        Assert.Same(art.Text("DNF", 5, 0xFF8800, 0x101010), paint.Banner("dnf", 46, 0xFFFFFF, 0xFF8800, 0x000000, 0x101010));
    }

    // ------------------------------------------------------------------------------------------------ 文字:有字体

    [Fact]
    public void With_fonts_a_line_is_drawn_by_the_font_at_the_asked_pixel_size_and_is_cached()
    {
        var fonts = new RecordingFonts();
        ShellPaint paint = WithFonts(fonts);

        SpriteFrame first = paint.Line("账号", 16, 0xFFFFFF, 0);
        SpriteFrame second = paint.Line("账号", 16, 0xFFFFFF, 0);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.True(fonts.Asked("账号"));
        Assert.Equal(new[] { 16 }, fonts.Sizes.ToArray());
        Assert.NotSame(first, paint.Line("账号", 16, 0xFFFFFE, 0));                // 颜色不同是另一张
        Assert.NotSame(first, paint.Line("账号", 17, 0xFFFFFF, 0));                // 字号不同是另一张
        Assert.NotSame(first, paint.Line("账号", 16, 0xFFFFFF, 1));                // 描边不同是另一张
        Assert.NotSame(first, paint.Line("账号", 16, 0xFFFFFF, 0, 50));            // 限宽不同是另一张
    }

    [Fact]
    public void With_fonts_data_text_is_drawn_as_typed_and_never_translated_or_capitalised()
    {
        var fonts = new RecordingFonts();
        ShellPaint paint = WithFonts(fonts);

        Assert.NotNull(paint.Data("ab_名", 17, 0xFFFFFF, 0, 200));

        Assert.True(fonts.Asked("ab_名"));
        Assert.DoesNotContain((int)'A', fonts.CodePoints);
        Assert.DoesNotContain((int)'-', fonts.CodePoints);
    }

    /// <summary>按"留开头、补省略号"截到不超过 <paramref name="maxWidth"/> 的最长写法(逐个试,和实现无关)。</summary>
    private static string HeadCut(string text, int size, int maxWidth)
    {
        IGlyphSource font = new SizedFonts().ForSize(size);
        for (int keep = text.Length; keep >= 0; keep--)
        {
            string candidate = text.Substring(0, keep) + GlyphText.Ellipsis;
            if (GlyphText.Measure(font, candidate) <= maxWidth)
            {
                return candidate;
            }
        }
        return GlyphText.Ellipsis;
    }

    /// <summary>按"留结尾、前面补省略号"截到不超过 <paramref name="maxWidth"/> 的最长写法。</summary>
    private static string TailCut(string text, int size, int maxWidth)
    {
        IGlyphSource font = new SizedFonts().ForSize(size);
        for (int keep = text.Length; keep >= 0; keep--)
        {
            string candidate = GlyphText.Ellipsis + text.Substring(text.Length - keep);
            if (GlyphText.Measure(font, candidate) <= maxWidth)
            {
                return candidate;
            }
        }
        return GlyphText.Ellipsis;
    }

    [Fact]
    public void A_long_line_shrinks_up_to_four_pixels_and_only_then_is_cut_with_an_ellipsis()
    {
        var fonts = new SizedFonts();
        ShellPaint paint = WithFonts(fonts);
        const string Text = "进入游戏进入游戏";                                        // 8 个全角字:20 号宽 160

        SpriteFrame fits = paint.Line(Text, 20, 0xFFFFFF, 0, 160);
        Assert.Equal(new[] { 20 }, fonts.Requested.ToArray());
        Assert.Equal(160 + 2, fits.Width);                                          // 字宽加两边各 1 像素描边

        fonts.Requested.Clear();
        SpriteFrame shrunk = paint.Line(Text, 20, 0xFFFFFF, 0, 140);                 // 17 号宽 136
        Assert.Equal(new[] { 20, 19, 18, 17 }, fonts.Requested.Distinct().ToArray());
        Assert.Equal(136 + 2, shrunk.Width);

        fonts.Requested.Clear();
        SpriteFrame cut = paint.Line(Text, 20, 0xFFFFFF, 0, 100);                    // 最小 16 号宽 128 还是放不下
        Assert.Equal(new[] { 20, 19, 18, 17, 16 }, fonts.Requested.Distinct().ToArray());
        Assert.DoesNotContain(15, fonts.Requested);
        Assert.Equal("进入游戏进…", HeadCut(Text, 16, 100));
        Assert.True(ShellFrames.Same(paint.Line(HeadCut(Text, 16, 100), 16, 0xFFFFFF, 0), cut));
    }

    [Fact]
    public void A_line_cut_at_the_smallest_size_keeps_its_beginning()
    {
        var fonts = new SizedFonts();
        ShellPaint paint = WithFonts(fonts);
        const string Text = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        SpriteFrame cut = paint.Line(Text, 16, 0xFFFFFF, 0, 60);                     // 12 号:每个字 6、省略号 12

        Assert.Equal("ABCDEFGH…", HeadCut(Text, 12, 60));
        Assert.True(ShellFrames.Same(paint.Line(HeadCut(Text, 12, 60), 12, 0xFFFFFF, 0), cut));
        Assert.True(cut.Width <= 60 + 2);
    }

    [Fact]
    public void Data_text_that_does_not_fit_keeps_its_end_behind_a_leading_ellipsis()
    {
        var fonts = new SizedFonts();
        ShellPaint paint = WithFonts(fonts);
        const string Text = "abcdefghijklmnopqrstuvwxyz";

        SpriteFrame tail = paint.Data(Text, 16, 0xFFFFFF, 0, 80);                    // 缩到 12 号还放不下,取结尾

        Assert.Equal("…pqrstuvwxyz", TailCut(Text, 12, 80));
        Assert.True(ShellFrames.Same(paint.Line(TailCut(Text, 12, 80), 12, 0xFFFFFF, 0), tail));
        Assert.True(tail.Width <= 80 + 2);
    }

    [Fact]
    public void Data_text_that_fits_is_drawn_whole_and_the_font_floor_is_eight_pixels()
    {
        var fonts = new SizedFonts();
        ShellPaint paint = WithFonts(fonts);

        SpriteFrame whole = paint.Data("player_01", 17, 0xFFFFFF, 0, 400);
        Assert.Equal(new[] { 17 }, fonts.Requested.Distinct().ToArray());
        Assert.True(ShellFrames.Same(paint.Line("player_01", 17, 0xFFFFFF, 0), whole));

        fonts.Requested.Clear();
        paint.Line("进入游戏进入游戏进入游戏", 10, 0xFFFFFF, 0, 20);                     // 10 号最多缩到 8
        Assert.Equal(new[] { 10, 9, 8 }, fonts.Requested.Distinct().ToArray());
    }

    [Fact]
    public void Bold_pushes_the_strokes_one_pixel_to_the_right()
    {
        var fonts = new SizedFonts();
        ShellPaint paint = WithFonts(fonts);

        SpriteFrame plain = paint.Line("AB", 16, 0xFFFFFF, 0x000000, 0, false);
        SpriteFrame bold = paint.Line("AB", 16, 0xFFFFFF, 0x000000, 0, true);

        Assert.NotSame(plain, bold);
        Assert.Equal(plain.Width + 1, bold.Width);
        Assert.Equal(plain.Height, bold.Height);
        Assert.Same(bold, paint.Line("AB", 16, 0xFFFFFF, 0x000000, 0, true));
        // 两个字、各 12 行:每行每个字多出一个填充色像素
        Assert.Equal(ShellFrames.Solid(plain, 0xFFFFFF) + 2 * 12, ShellFrames.Solid(bold, 0xFFFFFF));
        Assert.True(ShellFrames.Inked(bold) > ShellFrames.Inked(plain));
    }

    // ------------------------------------------------------------------------------------------------ 形状:圆角块

    [Fact]
    public void A_box_has_the_asked_size_transparent_corners_and_an_opaque_middle_with_a_gradient()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame box = paint.Box(40, 20, 8, Flat(0xFF204060, 0xFF102030, 0xFFFF0000));

        Assert.Equal((40, 20), (box.Width, box.Height));
        Assert.Equal(0, ShellFrames.Pixel(box, 0, 0).A);
        Assert.Equal(0, ShellFrames.Pixel(box, 39, 0).A);
        Assert.Equal(0, ShellFrames.Pixel(box, 0, 19).A);
        Assert.Equal(0, ShellFrames.Pixel(box, 39, 19).A);
        (int r, int g, int b, int a) = ShellFrames.Pixel(box, 20, 10);
        Assert.Equal(255, a);
        Assert.True(ShellFrames.Near(r, 0x18) && ShellFrames.Near(g, 0x30) && ShellFrames.Near(b, 0x48), $"中间 {r:X2}{g:X2}{b:X2}");
        Assert.True(ShellFrames.Pixel(box, 20, 2).R > ShellFrames.Pixel(box, 20, 17).R);        // 上亮下暗
    }

    [Fact]
    public void A_box_edge_is_one_pixel_of_the_edge_colour_and_the_fill_starts_right_inside()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame box = paint.Box(40, 20, 8, Flat(0xFF204060, 0xFF204060, 0xFFFF0000));

        Assert.Equal((255, 0, 0, 255), ShellFrames.Pixel(box, 20, 0));
        Assert.Equal((255, 0, 0, 255), ShellFrames.Pixel(box, 20, 19));
        Assert.Equal((255, 0, 0, 255), ShellFrames.Pixel(box, 0, 10));
        Assert.Equal((255, 0, 0, 255), ShellFrames.Pixel(box, 39, 10));
        Assert.Equal((0x20, 0x40, 0x60, 255), ShellFrames.Pixel(box, 20, 1));
        Assert.Equal((0x20, 0x40, 0x60, 255), ShellFrames.Pixel(box, 1, 10));
    }

    [Fact]
    public void A_box_without_an_edge_is_all_fill_and_a_translucent_fill_stays_translucent()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame solid = paint.Box(40, 20, 8, Flat(0xFF204060, 0xFF204060, 0xFFFF0000, 0));
        SpriteFrame glass = paint.Box(40, 20, 8, Flat(0x80204060, 0x80204060, 0xFFFF0000, 0));

        Assert.Equal((0x20, 0x40, 0x60, 255), ShellFrames.Pixel(solid, 20, 0));
        Assert.True(ShellFrames.Near(ShellFrames.Pixel(glass, 20, 10).A, 0x80));
    }

    [Fact]
    public void The_gloss_brightens_the_upper_half_only_and_the_lip_brightens_just_inside_the_edge()
    {
        (_, ShellPaint paint) = Plain();
        ShellBox plain = Flat(0xFF204060, 0xFF204060, 0xFF405080);

        SpriteFrame none = paint.Box(60, 40, 8, plain);
        SpriteFrame glossy = paint.Box(60, 40, 8, Flat(0xFF204060, 0xFF204060, 0xFF405080, 1, 120, 0));
        SpriteFrame lipped = paint.Box(60, 40, 8, Flat(0xFF204060, 0xFF204060, 0xFF405080, 1, 0, 200));

        Assert.True(ShellFrames.Pixel(glossy, 30, 4).R > ShellFrames.Pixel(none, 30, 4).R);
        Assert.Equal(ShellFrames.Pixel(none, 30, 36), ShellFrames.Pixel(glossy, 30, 36));        // 下半部分不受影响
        Assert.True(ShellFrames.Pixel(lipped, 30, 1).R > ShellFrames.Pixel(none, 30, 1).R);
        Assert.Equal(ShellFrames.Pixel(none, 30, 30), ShellFrames.Pixel(lipped, 30, 30));        // 离边线远的地方不受影响
    }

    [Fact]
    public void A_left_rounded_box_is_square_on_the_right_without_an_edge_there()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame cell = paint.Box(40, 20, 8, Flat(0xFF204060, 0xFF204060, 0xFFFF0000), ShellCorners.Left);

        Assert.Equal(0, ShellFrames.Pixel(cell, 0, 0).A);                                      // 左上角圆
        Assert.Equal(0, ShellFrames.Pixel(cell, 0, 19).A);
        Assert.Equal((255, 0, 0, 255), ShellFrames.Pixel(cell, 39, 0));                         // 右边是直角,上下的边线一直铺到头
        Assert.Equal((255, 0, 0, 255), ShellFrames.Pixel(cell, 39, 19));
        Assert.Equal((0x20, 0x40, 0x60, 255), ShellFrames.Pixel(cell, 39, 10));                // 右边没有边线
        Assert.Equal((255, 0, 0, 255), ShellFrames.Pixel(cell, 0, 10));                         // 左边有
    }

    [Fact]
    public void A_radius_larger_than_the_half_side_makes_a_pill_and_tiny_boxes_still_work()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame pill = paint.Box(30, 10, 100, Flat(0xFF204060, 0xFF204060, 0xFFFF0000));
        SpriteFrame tiny = paint.Box(2, 2, 1, Flat(0xFF204060, 0xFF204060, 0xFFFF0000, 0));

        Assert.Equal(0, ShellFrames.Pixel(pill, 0, 0).A);
        Assert.Equal(0, ShellFrames.Pixel(pill, 0, 9).A);
        Assert.Equal(255, ShellFrames.Pixel(pill, 15, 5).A);
        Assert.Equal((2, 2), (tiny.Width, tiny.Height));
        Assert.True(ShellFrames.Inked(tiny) > 0);
    }

    [Fact]
    public void A_box_is_cached_per_size_radius_corners_and_style()
    {
        (_, ShellPaint paint) = Plain();
        ShellBox a = Flat(0xFF204060, 0xFF102030, 0xFFFF0000);
        ShellBox b = Flat(0xFF204060, 0xFF102031, 0xFFFF0000);

        SpriteFrame first = paint.Box(40, 20, 8, a);

        Assert.Same(first, paint.Box(40, 20, 8, a));
        Assert.Same(first, paint.Box(40, 20, 8, Flat(0xFF204060, 0xFF102030, 0xFFFF0000)));
        Assert.NotSame(first, paint.Box(41, 20, 8, a));
        Assert.NotSame(first, paint.Box(40, 21, 8, a));
        Assert.NotSame(first, paint.Box(40, 20, 7, a));
        Assert.NotSame(first, paint.Box(40, 20, 8, b));
        Assert.NotSame(first, paint.Box(40, 20, 8, a, ShellCorners.Left));
        Assert.NotSame(first, new ShellPaint(new OverlayArt()).Box(40, 20, 8, a));         // 缓存是画笔自己的
    }

    [Fact]
    public void A_box_smaller_than_two_pixels_is_refused()
    {
        (_, ShellPaint paint) = Plain();

        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Box(1, 10, 2, Flat(1, 2, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Box(10, 1, 2, Flat(1, 2, 3)));
    }

    // ------------------------------------------------------------------------------------------------ 形状:光晕、点、菱形、线

    [Fact]
    public void A_glow_is_the_block_plus_the_spread_on_every_side_and_fades_outwards()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame glow = paint.Glow(40, 20, 6, 10, 0x336699, 200, true);

        Assert.Equal((60, 40), (glow.Width, glow.Height));
        Assert.Equal((0x33, 0x66, 0x99, 200), ShellFrames.Pixel(glow, 30, 20));                 // 块里面是最浓的
        int atEdge = ShellFrames.Pixel(glow, 9, 20).A;
        int middle = ShellFrames.Pixel(glow, 5, 20).A;
        int far = ShellFrames.Pixel(glow, 1, 20).A;
        Assert.True(atEdge > middle && middle > far, $"{atEdge} {middle} {far}");
        Assert.True(atEdge < 200);
        Assert.Equal(0, ShellFrames.Pixel(glow, 0, 0).A);
        Assert.Equal((0x33, 0x66, 0x99), (ShellFrames.Pixel(glow, 5, 20).R, ShellFrames.Pixel(glow, 5, 20).G, ShellFrames.Pixel(glow, 5, 20).B));
    }

    [Fact]
    public void A_glow_that_is_not_filled_is_empty_inside_and_a_ring_outside()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame ring = paint.Glow(40, 20, 6, 10, 0xFFD25E, 200, false);

        Assert.Equal(0, ShellFrames.Pixel(ring, 30, 20).A);
        Assert.True(ShellFrames.Pixel(ring, 9, 20).A > 100);
        Assert.Same(ring, paint.Glow(40, 20, 6, 10, 0xFFD25E, 200, false));
        Assert.NotSame(ring, paint.Glow(40, 20, 6, 10, 0xFFD25E, 200, true));
    }

    [Fact]
    public void A_glow_needs_a_spread_and_a_block()
    {
        (_, ShellPaint paint) = Plain();

        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Glow(40, 20, 6, 0, 0, 100, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Glow(1, 20, 6, 4, 0, 100, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Glow(40, 1, 6, 4, 0, 100, true));
    }

    [Fact]
    public void A_dot_is_a_round_filled_disc_with_the_asked_alpha()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame dot = paint.Dot(8, 0xF3F7FF);
        SpriteFrame faint = paint.Dot(8, 0xF3F7FF, 100);

        Assert.Equal((8, 8), (dot.Width, dot.Height));
        Assert.Equal((0xF3, 0xF7, 0xFF, 255), ShellFrames.Pixel(dot, 3, 3));
        Assert.Equal((0xF3, 0xF7, 0xFF, 255), ShellFrames.Pixel(dot, 4, 4));
        Assert.Equal(0, ShellFrames.Pixel(dot, 0, 0).A);
        Assert.Equal(0, ShellFrames.Pixel(dot, 7, 7).A);
        Assert.Equal(100, ShellFrames.Pixel(faint, 3, 3).A);
        Assert.Same(dot, paint.Dot(8, 0xF3F7FF));
        Assert.NotSame(dot, faint);
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Dot(0, 0));
        Assert.Equal(1, paint.Dot(1, 0xFFFFFF).Width);
    }

    [Fact]
    public void A_diamond_is_solid_in_the_middle_and_empty_in_the_corners()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame gem = paint.Diamond(10, 0xFFEBB0);

        Assert.Equal((10, 10), (gem.Width, gem.Height));
        Assert.Equal((0xFF, 0xEB, 0xB0, 255), ShellFrames.Pixel(gem, 4, 4));
        Assert.Equal((0xFF, 0xEB, 0xB0, 255), ShellFrames.Pixel(gem, 5, 5));
        foreach ((int x, int y) in new[] { (0, 0), (9, 0), (0, 9), (9, 9) })
        {
            Assert.Equal(0, ShellFrames.Pixel(gem, x, y).A);
        }
        Assert.True(ShellFrames.Pixel(gem, 5, 0).A > 0);                                       // 顶角有尖
        Assert.Same(gem, paint.Diamond(10, 0xFFEBB0));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Diamond(1, 0));
    }

    [Fact]
    public void A_rule_fades_from_the_start_through_the_middle_to_the_end()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame rule = paint.Rule(100, 2, 0x00FF00, 0, 255, 0);

        Assert.Equal((100, 2), (rule.Width, rule.Height));
        Assert.True(ShellFrames.Pixel(rule, 0, 0).A <= 6);
        Assert.True(ShellFrames.Pixel(rule, 49, 0).A >= 250);
        Assert.True(ShellFrames.Pixel(rule, 99, 0).A <= 6);
        Assert.True(ShellFrames.Pixel(rule, 25, 0).A is > 100 and < 160);
        Assert.Equal(ShellFrames.Pixel(rule, 30, 0), ShellFrames.Pixel(rule, 30, 1));
        Assert.Equal((0, 255, 0), (ShellFrames.Pixel(rule, 49, 0).R, ShellFrames.Pixel(rule, 49, 0).G, ShellFrames.Pixel(rule, 49, 0).B));
        Assert.Same(rule, paint.Rule(100, 2, 0x00FF00, 0, 255, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Rule(0, 2, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Rule(10, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void A_corner_bracket_has_two_arms_hugging_its_own_corner_and_fading_to_the_tips()
    {
        (_, ShellPaint paint) = Plain();
        const int Arm = 18;

        SpriteFrame topLeft = paint.Corner(0, Arm, 2, 0xFFD25E);
        SpriteFrame topRight = paint.Corner(1, Arm, 2, 0xFFD25E);
        SpriteFrame bottomLeft = paint.Corner(2, Arm, 2, 0xFFD25E);
        SpriteFrame bottomRight = paint.Corner(3, Arm, 2, 0xFFD25E);

        Assert.Equal(255, ShellFrames.Pixel(topLeft, 0, 0).A);
        Assert.Equal(255, ShellFrames.Pixel(topRight, Arm - 1, 0).A);
        Assert.Equal(255, ShellFrames.Pixel(bottomLeft, 0, Arm - 1).A);
        Assert.Equal(255, ShellFrames.Pixel(bottomRight, Arm - 1, Arm - 1).A);
        Assert.Equal(90, ShellFrames.Pixel(topLeft, Arm - 1, 0).A);                            // 臂的末端最淡
        Assert.Equal(90, ShellFrames.Pixel(topLeft, 0, Arm - 1).A);
        Assert.True(ShellFrames.Pixel(topLeft, 9, 0).A is > 90 and < 255);
        Assert.Equal(0, ShellFrames.Pixel(topLeft, Arm - 1, Arm - 1).A);                       // 括号里面是空的
        Assert.Equal(0, ShellFrames.Pixel(topLeft, 5, 5).A);
        Assert.Equal(0, ShellFrames.Pixel(topRight, 0, Arm - 1).A);
        Assert.Equal(2 * Arm * 2 - 2 * 2, ShellFrames.Inked(topLeft));                         // 两条臂,交叠的角算一次
        Assert.Same(topLeft, paint.Corner(0, Arm, 2, 0xFFD25E));
        Assert.NotSame(topLeft, topRight);
    }

    [Fact]
    public void A_corner_bracket_needs_arms_longer_than_its_thickness()
    {
        (_, ShellPaint paint) = Plain();

        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Corner(0, 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Corner(0, 4, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Corner(0, 4, 4, 0));
    }

    [Fact]
    public void The_vignette_is_clearest_in_the_middle_and_darkest_in_the_corners()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame dark = paint.Vignette(100, 80, 46, 205, 0x050A1A);

        Assert.Equal((100, 80), (dark.Width, dark.Height));
        Assert.Equal((5, 10, 26, 46), ShellFrames.Pixel(dark, 49, 39));
        Assert.Equal((5, 10, 26, 205), ShellFrames.Pixel(dark, 0, 0));
        Assert.Equal(205, ShellFrames.Pixel(dark, 99, 79).A);
        int quarter = ShellFrames.Pixel(dark, 25, 20).A;
        Assert.True(quarter > 46 && quarter < 205);
        Assert.True(ShellFrames.Pixel(dark, 5, 40).A > ShellFrames.Pixel(dark, 30, 40).A);
        Assert.Same(dark, paint.Vignette(100, 80, 46, 205, 0x050A1A));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Vignette(1, 80, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Vignette(100, 1, 0, 0, 0));
    }

    // ------------------------------------------------------------------------------------------------ 形状:转圈、图标

    [Fact]
    public void The_spinner_has_ten_distinct_phases_that_wrap_around_and_keep_one_colour()
    {
        (_, ShellPaint paint) = Plain();
        var frames = Enumerable.Range(0, 10).Select(i => paint.Spinner(20, i, 0x5B9BFF)).ToArray();

        for (int i = 0; i < 10; i++)
        {
            for (int j = i + 1; j < 10; j++)
            {
                Assert.False(ShellFrames.Same(frames[i], frames[j]), $"阶段 {i} 与 {j} 相同");
            }
        }
        Assert.Same(frames[0], paint.Spinner(20, 10, 0x5B9BFF));
        Assert.Same(frames[9], paint.Spinner(20, -1, 0x5B9BFF));
        Assert.Same(frames[3], paint.Spinner(20, 23, 0x5B9BFF));
        Assert.Same(frames[0], paint.Spinner(20, 0, 0x5B9BFF));
        Assert.NotSame(frames[0], paint.Spinner(21, 0, 0x5B9BFF));
        Assert.NotSame(frames[0], paint.Spinner(20, 0, 0x5B9BFE));

        SpriteFrame head = frames[0];
        Assert.Equal((20, 20), (head.Width, head.Height));
        for (int i = 0; i < head.Rgba.Length; i += 4)
        {
            if (head.Rgba[i + 3] != 0)
            {
                Assert.Equal((0x5B, 0x9B, 0xFF), (head.Rgba[i], head.Rgba[i + 1], head.Rgba[i + 2]));
            }
        }
    }

    [Fact]
    public void The_spinner_head_is_opaque_at_the_top_and_the_tail_is_fainter()
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame spinner = paint.Spinner(20, 0, 0x5B9BFF);

        int topAlpha = 0;
        for (int y = 0; y < 6; y++)
        {
            for (int x = 8; x < 12; x++)
            {
                topAlpha = Math.Max(topAlpha, ShellFrames.Pixel(spinner, x, y).A);
            }
        }
        Assert.Equal(255, topAlpha);
        bool faint = false;
        for (int i = 3; i < spinner.Rgba.Length; i += 4)
        {
            faint |= spinner.Rgba[i] > 0 && spinner.Rgba[i] < 90;
        }
        Assert.True(faint);
        Assert.Equal(0, ShellFrames.Pixel(spinner, 10, 10).A);                                  // 圈中间与四角是空的
        Assert.Equal(0, ShellFrames.Pixel(spinner, 0, 0).A);
        Assert.Equal(0, ShellFrames.Pixel(spinner, 19, 19).A);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(9)]
    public void The_spinner_tail_fades_gently_ten_dots_from_solid_to_about_a_sixth(int phase)
    {
        (_, ShellPaint paint) = Plain();
        const int Size = 38;
        SpriteFrame spinner = paint.Spinner(Size, phase, 0x5B9BFF);

        // 头在 phase 位,后面第 k 个点在 phase - k 位(逆时针);点心在圆环上,点比一像素大得多,点心那个像素的不透明度就是这个点的浓淡
        float c = Size / 2f;
        float ring = c - Math.Max(1.4f, Size * 0.115f) - 0.5f;
        var alphas = new int[10];
        for (int k = 0; k < 10; k++)
        {
            int place = ((phase - k) % 10 + 10) % 10;
            double angle = place * (2.0 * Math.PI / 10) - Math.PI / 2.0;
            alphas[k] = ShellFrames.Pixel(spinner, (int)Math.Floor(c + ring * Math.Cos(angle)), (int)Math.Floor(c + ring * Math.Sin(angle))).A;
        }

        Assert.Equal(255, alphas[0]);
        for (int k = 1; k < 10; k++)
        {
            Assert.True(alphas[k] < alphas[k - 1], $"尾巴第 {k} 个点应比前一个淡:{string.Join(",", alphas)}");
        }
        Assert.InRange(alphas[4], 150, 172);                                                    // 中段还很实
        Assert.InRange(alphas[9], 38, 52);                                                      // 尾巴末端淡但看得见
    }

    [Fact]
    public void The_spinner_needs_a_size_of_at_least_eight()
    {
        (_, ShellPaint paint) = Plain();

        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Spinner(7, 0, 0));
        Assert.Equal(8, paint.Spinner(8, 0, 0xFFFFFF).Width);
    }

    [Theory]
    [InlineData(ShellTone.Error, 0xFF, 0x7A, 0x45)]
    [InlineData(ShellTone.Success, 0x4F, 0xD1, 0x7F)]
    [InlineData(ShellTone.Info, 0xFF, 0xD2, 0x5E)]
    [InlineData(ShellTone.Wait, 0x5B, 0x9B, 0xFF)]
    public void An_icon_is_a_disc_in_the_tone_colour(ShellTone tone, int r, int g, int b)
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame icon = paint.Icon(tone, 20);

        Assert.Equal((20, 20), (icon.Width, icon.Height));
        Assert.Equal(0, ShellFrames.Pixel(icon, 0, 0).A);
        Assert.Equal(0, ShellFrames.Pixel(icon, 19, 19).A);
        Assert.Equal((r, g, b, 255), ShellFrames.Pixel(icon, 2, 10));                          // 圆盘靠左边缘的一点
        Assert.Equal((r, g, b, 255), ShellFrames.Pixel(icon, 17, 10));
        Assert.Same(icon, paint.Icon(tone, 20));
    }

    [Theory]
    [InlineData(ShellTone.Error, true)]
    [InlineData(ShellTone.Success, true)]
    [InlineData(ShellTone.Info, true)]
    [InlineData(ShellTone.Wait, false)]
    public void An_icon_carries_a_dark_symbol_except_for_waiting(ShellTone tone, bool hasSymbol)
    {
        (_, ShellPaint paint) = Plain();

        SpriteFrame icon = paint.Icon(tone, 20);

        int dark = 0;
        for (int i = 0; i < icon.Rgba.Length; i += 4)
        {
            if (icon.Rgba[i + 3] == 255 && icon.Rgba[i] < 0x30 && icon.Rgba[i + 1] < 0x30)
            {
                dark++;
            }
        }
        Assert.Equal(hasSymbol, dark > 6);
    }

    [Fact]
    public void The_three_symbols_and_the_plain_disc_all_look_different()
    {
        (_, ShellPaint paint) = Plain();
        var icons = new[] { ShellTone.Error, ShellTone.Success, ShellTone.Info, ShellTone.Wait }.Select(t => paint.Icon(t, 20)).ToArray();

        for (int i = 0; i < icons.Length; i++)
        {
            for (int j = i + 1; j < icons.Length; j++)
            {
                Assert.False(ShellFrames.Same(icons[i], icons[j]));
            }
        }
        Assert.NotSame(icons[0], paint.Icon(ShellTone.Error, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => paint.Icon(ShellTone.Error, 7));
    }

    // ------------------------------------------------------------------------------------------------ 标题字

    [Fact]
    public void The_banner_has_a_gradient_fill_an_edge_and_room_around_the_text()
    {
        var fonts = new SizedFonts();
        ShellPaint paint = WithFonts(fonts);

        SpriteFrame banner = paint.Banner("AB", 46, 0xFFF3C6, 0xFF9020, 0x80300E, 0x24130A);

        Assert.NotNull(banner);
        Assert.Contains(46, fonts.Requested);
        Assert.True(banner.Width > 2 * 23 + 12);                                                // 比文字本身宽(字距、描边、留白)
        Assert.True(banner.Height > 34 + 12);
        bool orange = false;
        for (int i = 0; i < banner.Rgba.Length; i += 4)
        {
            orange |= banner.Rgba[i + 3] == 255 && banner.Rgba[i] > 0xC0 && banner.Rgba[i + 1] > 0x60 && banner.Rgba[i + 1] < 0xF0 && banner.Rgba[i + 2] < 0x80;
        }
        Assert.True(orange, "字里应有接近中段橙色的像素");
        Assert.True(ShellFrames.Solid(banner, 0x24130A) > 20);                                   // 有描边色
        Assert.Equal(0, ShellFrames.Pixel(banner, 0, 0).A);                                     // 角上留白
        Assert.Same(banner, paint.Banner("AB", 46, 0xFFF3C6, 0xFF9020, 0x80300E, 0x24130A));
        Assert.NotSame(banner, paint.Banner("AB", 46, 0xFFF3C7, 0xFF9020, 0x80300E, 0x24130A));
        Assert.NotSame(banner, paint.Banner("ABC", 46, 0xFFF3C6, 0xFF9020, 0x80300E, 0x24130A));
    }

    [Fact]
    public void The_banner_fill_goes_from_the_top_colour_to_the_bottom_colour()
    {
        ShellPaint paint = WithFonts(new SizedFonts());

        SpriteFrame banner = paint.Banner("A", 46, 0xFFFFFF, 0x808080, 0x000000, 0x000000);

        int x = banner.Width / 2;
        int top = -1, bottom = -1;
        for (int y = 0; y < banner.Height; y++)
        {
            (int r, int g, int b, int a) = ShellFrames.Pixel(banner, x, y);
            if (a == 255 && r == g && g == b && r > 0)
            {
                if (top < 0)
                {
                    top = r;
                }
                bottom = r;
            }
        }
        Assert.True(top > 200, "上端接近白色:" + top);
        Assert.True(bottom < 80, "下端接近黑色:" + bottom);
    }

    [Fact]
    public void The_banner_leaves_room_for_spaces_and_for_glyphs_the_font_lacks()
    {
        ShellPaint sized = WithFonts(new SizedFonts());
        Assert.True(sized.Banner("A B", 46, 1, 2, 3, 4).Width > sized.Banner("AB", 46, 1, 2, 3, 4).Width);

        var lacking = new BlockFonts();
        lacking.Font.Missing.Add('B');
        ShellPaint paint = WithFonts(lacking);

        SpriteFrame withGap = paint.Banner("ABC", 46, 1, 2, 3, 4);
        SpriteFrame without = paint.Banner("AC", 46, 1, 2, 3, 4);

        Assert.True(withGap.Width > without.Width);
    }

    [Theory]
    [InlineData(46, 3)]
    [InlineData(28, 2)]
    [InlineData(14, 1)]
    [InlineData(13, 1)]
    public void The_banner_spaces_its_letters_by_a_fourteenth_of_the_size_and_at_least_one_pixel(int px, int tracking)
    {
        ShellPaint paint = WithFonts(new SizedFonts());

        // 全角字前进一个字号,字形比前进少 1 像素;最后一个字的笔位退回一个字距,整张图再各加 6 像素的边
        SpriteFrame three = paint.Banner("地下城", px, 1, 2, 3, 4);
        SpriteFrame one = paint.Banner("地", px, 1, 2, 3, 4);

        Assert.Equal(3 * (px + tracking) - tracking + 12, three.Width);
        Assert.Equal(px + 12, one.Width);
    }

    [Theory]
    [InlineData(46, true)]
    [InlineData(28, true)]
    [InlineData(27, false)]
    [InlineData(20, false)]
    public void The_banner_glyphs_are_made_exactly_one_pixel_bolder_from_28_pixels_up(int px, bool bold)
    {
        ShellPaint paint = WithFonts(new SizedFonts());
        // 填充三段同色(白),描边红:字形是个实心方块,左上角在 (6, 6)、宽 px - 1、高 px * 3 / 4;加粗就是白色再向外多一像素
        SpriteFrame banner = paint.Banner("地", px, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFF0000);
        int right = 6 + px - 1;
        int bottom = 6 + px * 3 / 4;
        int midY = 6 + px * 3 / 8;
        int midX = 6 + (px - 1) / 2;

        (int R, int G, int B, int A) white = (255, 255, 255, 255);
        (int R, int G, int B, int A) red = (255, 0, 0, 255);
        Assert.Equal(white, ShellFrames.Pixel(banner, 6, midY));                                // 字形本身
        Assert.Equal(white, ShellFrames.Pixel(banner, right - 1, midY));
        Assert.Equal(bold ? white : red, ShellFrames.Pixel(banner, 5, midY));                  // 字形外一像素:加粗是白,否则是描边
        Assert.Equal(bold ? white : red, ShellFrames.Pixel(banner, right, midY));
        Assert.Equal(bold ? white : red, ShellFrames.Pixel(banner, midX, 5));
        Assert.Equal(bold ? white : red, ShellFrames.Pixel(banner, midX, bottom));
        Assert.Equal(red, ShellFrames.Pixel(banner, 4, midY));                                  // 字形外两像素:都是描边(只多一像素)
        Assert.Equal(red, ShellFrames.Pixel(banner, right + 1, midY));
    }

    // ------------------------------------------------------------------------------------------------ 缓存是画笔自己的

    [Fact]
    public void Two_paints_do_not_share_frames_but_draw_the_same_pixels()
    {
        ShellPaint a = Plain().Paint;
        ShellPaint b = Plain().Paint;

        Assert.NotSame(a.Dot(8, 0xFFFFFF), b.Dot(8, 0xFFFFFF));
        Assert.True(ShellFrames.Same(a.Dot(8, 0xFFFFFF), b.Dot(8, 0xFFFFFF)));
        Assert.True(ShellFrames.Same(a.Spinner(20, 4, 0x5B9BFF), b.Spinner(20, 4, 0x5B9BFF)));
        Assert.True(ShellFrames.Same(a.Box(40, 20, 8, Flat(1, 2, 3)), b.Box(40, 20, 8, Flat(1, 2, 3))));
    }
}
