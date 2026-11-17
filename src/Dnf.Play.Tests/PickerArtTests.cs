using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>两张界面共用的画法与几何:呼吸光曲线、按钮热区、合成的边框/面板/小按钮的像素排布、翻页箭头。</summary>
public sealed class PickerArtTests
{
    private static SpriteFrame Solid(int width, int height, byte r, byte g = 0, byte b = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }
        return new SpriteFrame(width, height, 0, 0, width, height, rgba);
    }

    private static (byte R, byte G, byte B, byte A) Px(SpriteFrame frame, int x, int y)
    {
        int at = (y * frame.Width + x) * 4;
        return (frame.Rgba[at], frame.Rgba[at + 1], frame.Rgba[at + 2], frame.Rgba[at + 3]);
    }

    /// <summary>每个图块的红色分量是 10 × (图块序号 + 1),看一个像素就知道它来自九宫格里的哪一块。</summary>
    private static ShellFakeSprites NineTiles(int first, int tileWidth, int tileHeight)
    {
        var sprites = new ShellFakeSprites();
        for (int i = 0; i < 9; i++)
        {
            sprites.Frames[(PickerArt.WindowImage, first + i)] = Solid(tileWidth, tileHeight, (byte)(10 * (i + 1)));
        }
        return sprites;
    }

    // ------------------------------------------------------------------ 呼吸光

    [Theory]
    [InlineData(0, 0)]
    [InlineData(250, 10)]       // 9.75 → 10
    [InlineData(500, 20)]       // 19.5 → 20(四舍五入、远离零)
    [InlineData(750, 29)]
    [InlineData(1000, 39)]
    [InlineData(1250, 29)]
    [InlineData(1500, 20)]
    [InlineData(1750, 10)]
    [InlineData(2000, 0)]
    [InlineData(2250, 10)]
    [InlineData(4000, 0)]
    [InlineData(5000, 39)]
    public void The_start_glow_is_a_two_second_triangle_between_zero_and_39_and_the_title_glow_is_its_complement(long age, int expected)
    {
        Assert.Equal(expected, PickerArt.StartGlowTint(age));
        Assert.Equal(105 - expected, PickerArt.TitleGlowTint(age));
    }

    [Fact]
    public void The_pulse_constants_match_what_the_baselines_measured()
    {
        Assert.Equal(39, PickerArt.GlowHigh);
        Assert.Equal(2000, PickerArt.GlowPeriodMs);
        Assert.Equal(105, PickerArt.TitleGlowSum);
        // 基线 S02-client-00/01/02 三个时刻:标题光晕 76、104、87 与大按钮光 30、0、17 加起来 106、104、104,所以和取 105(差 1 以内)
        Assert.InRange(PickerArt.StartGlowTint(769) + PickerArt.TitleGlowTint(769), 104, 106);
    }

    [Fact]
    public void Pulse_starts_low_ignores_negative_time_and_survives_bad_arguments()
    {
        Assert.Equal(3, PickerArt.Pulse(-5, 2000, 3, 9));
        Assert.Equal(3, PickerArt.Pulse(0, 2000, 3, 9));
        Assert.Equal(9, PickerArt.Pulse(1000, 2000, 3, 9));
        Assert.Equal(6, PickerArt.Pulse(500, 2000, 3, 9));
        Assert.Equal(5, PickerArt.Pulse(100, 0, 5, 9));              // 周期不是正数:取低值
        Assert.Equal(5, PickerArt.Pulse(100, -10, 5, 9));
        Assert.Equal(9, PickerArt.Pulse(100, 2000, 9, 9));           // 高不比低大:取低值
        Assert.Equal(9, PickerArt.Pulse(100, 2000, 9, 5));
        Assert.Equal(255, PickerArt.Pulse(0, 0, 300, 400));          // 夹到 0..255
        Assert.Equal(0, PickerArt.Pulse(0, 0, -5, 10));
    }

    // ------------------------------------------------------------------ 几何

    [Fact]
    public void The_button_rectangles_match_the_baseline()
    {
        HitRect start = PickerArt.StartRect();
        Assert.Equal((337, 526, 125, 43), (start.X, start.Y, start.Width, start.Height));
        HitRect left = PickerArt.LeftButtonRect();
        Assert.Equal((137, 536, 77, 23), (left.X, left.Y, left.Width, left.Height));
        HitRect right = PickerArt.RightButtonRect();
        Assert.Equal((587, 536, 77, 23), (right.X, right.Y, right.Width, right.Height));
    }

    [Fact]
    public void The_start_button_frames_follow_the_state()
    {
        Assert.Equal(7, PickerArt.StartFrame(PickerButtonState.Normal));
        Assert.Equal(8, PickerArt.StartFrame(PickerButtonState.Hover));
        Assert.Equal(9, PickerArt.StartFrame(PickerButtonState.Pressed));
        Assert.Equal(10, PickerArt.StartFrame(PickerButtonState.Disabled));
    }

    [Fact]
    public void The_board_and_panel_origins_and_sizes_match_the_baseline()
    {
        Assert.Equal((88, 114, 50, 28), (PickerArt.BoardX, PickerArt.BoardY, PickerArt.BoardColumns, PickerArt.BoardRows));
        Assert.Equal((102, 126, 285, 168), (PickerArt.DimX, PickerArt.DimY, PickerArt.DimColumns, PickerArt.DimRows));
        Assert.Equal((0, 213), (PickerArt.BoardFirstFrame, PickerArt.DimFirstFrame));
        Assert.Equal((273, 73, 344, 72), (PickerArt.BannerX, PickerArt.BannerY, PickerArt.LabelX, PickerArt.LabelY));
    }

    // ------------------------------------------------------------------ AddFrame

    [Fact]
    public void AddFrame_places_the_frame_at_origin_plus_its_offset_in_the_ui_layer()
    {
        var sprites = new ShellFakeSprites();
        sprites.Frames[("img", 3)] = PickerSprites.Block(5, 6, 2, -3);
        var art = new PickerArt(sprites);
        var list = new List<SceneSprite>();
        int order = 10;
        Assert.True(art.AddFrame(list, ref order, "img", 3, 100, 50, "tag"));
        SceneSprite sprite = Assert.Single(list);
        Assert.Equal((102, 47), (sprite.ScreenX, sprite.ScreenY));
        Assert.Equal(10, sprite.SortKey);
        Assert.Equal(11, order);
        Assert.Equal("tag", sprite.AnimationPath);
        Assert.Equal(SpriteBlend.Normal, sprite.Blend);
        Assert.Equal(255, sprite.TintAlpha);
        Assert.Equal(OverlayArt.UiRank, sprite.LayerRank);
        Assert.Same(sprites.Frames[("img", 3)], sprite.Frame);
    }

    [Fact]
    public void AddFrame_can_be_additive_with_a_clamped_tint()
    {
        var sprites = new ShellFakeSprites();
        sprites.Frames[("img", 0)] = PickerSprites.Block(2, 2, 0, 0);
        var art = new PickerArt(sprites);
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddFrame(list, ref order, "img", 0, 0, 0, "a", true, 77);
        art.AddFrame(list, ref order, "img", 0, 0, 0, "b", true, 300);
        art.AddFrame(list, ref order, "img", 0, 0, 0, "c", true, -4);
        art.AddFrame(list, ref order, "img", 0, 0, 0, "d", false, 77);
        Assert.Equal((SpriteBlend.Additive, 77), (list[0].Blend, list[0].TintAlpha));
        Assert.Equal(255, list[1].TintAlpha);
        Assert.Equal(0, list[2].TintAlpha);
        Assert.Equal(SpriteBlend.Normal, list[3].Blend);
    }

    [Fact]
    public void AddFrame_adds_nothing_for_a_missing_negative_or_empty_frame()
    {
        var sprites = new ShellFakeSprites();
        sprites.Frames[("img", 0)] = PickerSprites.Block(2, 2, 0, 0);
        sprites.Frames[("img", 1)] = new SpriteFrame(0, 5, 0, 0, 0, 5, new byte[0]);
        sprites.Frames[("img", 2)] = new SpriteFrame(5, 0, 0, 0, 5, 0, new byte[0]);
        var art = new PickerArt(sprites);
        var list = new List<SceneSprite>();
        int order = 0;
        Assert.False(art.AddFrame(list, ref order, "img", 9, 0, 0, "x"));
        Assert.False(art.AddFrame(list, ref order, "other", 0, 0, 0, "x"));
        Assert.False(art.AddFrame(list, ref order, "img", -1, 0, 0, "x"));
        Assert.False(art.AddFrame(list, ref order, "img", 1, 0, 0, "x"));
        Assert.False(art.AddFrame(list, ref order, "img", 2, 0, 0, "x"));
        Assert.Empty(list);
        Assert.Equal(0, order);
        Assert.False(PickerArt.None.AddFrame(list, ref order, "img", 0, 0, 0, "x"));
    }

    [Fact]
    public void Art_objects_are_shared_per_sprite_source_and_a_null_source_is_handled()
    {
        var sprites = new ShellFakeSprites();
        Assert.Same(PickerArt.For(sprites), PickerArt.For(sprites));
        Assert.NotSame(PickerArt.For(sprites), PickerArt.For(new ShellFakeSprites()));
        Assert.Null(PickerArt.For(null));
        Assert.Throws<ArgumentNullException>(() => new PickerArt(null));
        Assert.Same(sprites, PickerArt.For(sprites).Sprites);
    }

    // ------------------------------------------------------------------ 合成的大图

    [Fact]
    public void The_board_is_composed_in_nine_slice_order_from_50_by_28_tiles()
    {
        ShellFakeSprites sprites = NineTiles(PickerArt.BoardFirstFrame, 2, 3);
        var art = new PickerArt(sprites);
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddBoard(list, ref order, "board");
        SceneSprite sprite = Assert.Single(list);
        Assert.Equal((88, 114), (sprite.ScreenX, sprite.ScreenY));
        SpriteFrame board = sprite.Frame;
        Assert.Equal((104, 90), (board.Width, board.Height));          // 2 + 50×2 + 2 与 3 + 28×3 + 3

        Assert.Equal(10, Px(board, 0, 0).R);                           // 左上角
        Assert.Equal(10, Px(board, 1, 2).R);
        Assert.Equal(20, Px(board, 2, 0).R);                           // 上边
        Assert.Equal(20, Px(board, 101, 2).R);
        Assert.Equal(30, Px(board, 102, 0).R);                         // 右上角
        Assert.Equal(30, Px(board, 103, 2).R);
        Assert.Equal(40, Px(board, 0, 3).R);                           // 左边
        Assert.Equal(40, Px(board, 1, 86).R);
        Assert.Equal(50, Px(board, 2, 3).R);                           // 中心
        Assert.Equal(50, Px(board, 101, 86).R);
        Assert.Equal(60, Px(board, 102, 3).R);                         // 右边
        Assert.Equal(60, Px(board, 103, 86).R);
        Assert.Equal(70, Px(board, 0, 87).R);                          // 左下角
        Assert.Equal(80, Px(board, 2, 87).R);                          // 下边
        Assert.Equal(80, Px(board, 101, 89).R);
        Assert.Equal(90, Px(board, 102, 87).R);                        // 右下角
        Assert.Equal(90, Px(board, 103, 89).R);
        Assert.Equal(255, Px(board, 50, 40).A);
    }

    [Fact]
    public void The_dim_panel_is_composed_the_same_way_from_285_by_168_tiles()
    {
        ShellFakeSprites sprites = NineTiles(PickerArt.DimFirstFrame, 1, 1);
        var art = new PickerArt(sprites);
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddDimPanel(list, ref order, "panel");
        SceneSprite sprite = Assert.Single(list);
        Assert.Equal((102, 126), (sprite.ScreenX, sprite.ScreenY));
        SpriteFrame panel = sprite.Frame;
        Assert.Equal((287, 170), (panel.Width, panel.Height));

        Assert.Equal(10, Px(panel, 0, 0).R);
        Assert.Equal(20, Px(panel, 1, 0).R);
        Assert.Equal(20, Px(panel, 285, 0).R);
        Assert.Equal(30, Px(panel, 286, 0).R);
        Assert.Equal(40, Px(panel, 0, 1).R);
        Assert.Equal(40, Px(panel, 0, 168).R);
        Assert.Equal(50, Px(panel, 1, 1).R);
        Assert.Equal(50, Px(panel, 285, 168).R);
        Assert.Equal(60, Px(panel, 286, 1).R);
        Assert.Equal(60, Px(panel, 286, 168).R);
        Assert.Equal(70, Px(panel, 0, 169).R);
        Assert.Equal(80, Px(panel, 1, 169).R);
        Assert.Equal(80, Px(panel, 285, 169).R);
        Assert.Equal(90, Px(panel, 286, 169).R);
    }

    [Fact]
    public void A_missing_tile_means_no_board_and_no_panel_instead_of_a_broken_one()
    {
        ShellFakeSprites sprites = NineTiles(PickerArt.BoardFirstFrame, 2, 3);
        sprites.Frames.Remove((PickerArt.WindowImage, 7));
        var art = new PickerArt(sprites);
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddBoard(list, ref order, "board");
        art.AddDimPanel(list, ref order, "panel");
        Assert.Empty(list);
        Assert.Equal(0, order);
    }

    [Fact]
    public void The_small_button_is_a_left_cap_a_tiled_middle_and_a_right_cap_77_wide()
    {
        var sprites = new ShellFakeSprites();
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ButtonLeftFrame)] = Solid(5, 23, 10);
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ButtonMiddleFrame)] = Solid(3, 23, 20);
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ButtonRightFrame)] = Solid(5, 23, 30);
        var art = new PickerArt(sprites);
        var overlay = new OverlayArt { Localize = false };
        var paint = new ShellPaint(overlay);
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddSmallButton(list, ref order, overlay, paint, new HitRect(10, 20, 77, 23), "OK", PickerButtonState.Normal, "btn");
        SceneSprite face = PickerSprites.One(list, "btn");
        Assert.Equal((10, 20, 77, 23), (face.ScreenX, face.ScreenY, face.Frame.Width, face.Frame.Height));
        Assert.Equal(10, Px(face.Frame, 0, 0).R);
        Assert.Equal(10, Px(face.Frame, 4, 22).R);
        Assert.Equal(20, Px(face.Frame, 5, 0).R);
        Assert.Equal(20, Px(face.Frame, 71, 10).R);
        Assert.Equal(30, Px(face.Frame, 72, 0).R);
        Assert.Equal(30, Px(face.Frame, 76, 22).R);
        Assert.False(PickerRig.Has(list, "btn-dim"));
    }

    [Fact]
    public void The_small_button_without_tiles_is_a_solid_block_with_the_same_text()
    {
        var art = new PickerArt(new ShellFakeSprites());
        var overlay = new OverlayArt { Localize = false };
        var paint = new ShellPaint(overlay);
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddSmallButton(list, ref order, overlay, paint, new HitRect(10, 20, 77, 23), "OK", PickerButtonState.Normal, "btn");
        SceneSprite face = PickerSprites.One(list, "btn");
        Assert.Equal((77, 23), (face.Frame.Width, face.Frame.Height));
        Assert.True(PickerRig.Has(list, "btn-text"));
    }

    [Fact]
    public void The_small_button_state_changes_the_text_color_and_adds_the_dim_layer_when_disabled()
    {
        var art = new PickerArt(new ShellFakeSprites());
        var overlay = new OverlayArt { Localize = false };
        var paint = new ShellPaint(overlay);
        List<SceneSprite> Make(PickerButtonState state)
        {
            var list = new List<SceneSprite>();
            int order = 0;
            art.AddSmallButton(list, ref order, overlay, paint, new HitRect(0, 0, 77, 23), "OK", state, "btn");
            return list;
        }
        SceneSprite normal = PickerSprites.One(Make(PickerButtonState.Normal), "btn-text");
        SceneSprite hover = PickerSprites.One(Make(PickerButtonState.Hover), "btn-text");
        Assert.False(normal.Frame.Rgba.SequenceEqual(hover.Frame.Rgba));       // 悬停时字更亮
        Assert.False(PickerRig.Has(Make(PickerButtonState.Hover), "btn-dim"));
        Assert.False(PickerRig.Has(Make(PickerButtonState.Pressed), "btn-dim"));
        List<SceneSprite> disabled = Make(PickerButtonState.Disabled);
        SceneSprite dim = PickerSprites.One(disabled, "btn-dim");
        Assert.Equal((0, 0, 77, 23), (dim.ScreenX, dim.ScreenY, dim.Frame.Width, dim.Frame.Height));
        Assert.True(dim.SortKey > PickerSprites.One(disabled, "btn-text").SortKey);   // 暗层盖在字上
    }

    [Fact]
    public void The_text_is_centered_in_the_small_button()
    {
        var art = new PickerArt(new ShellFakeSprites());
        var overlay = new OverlayArt { Localize = false };
        var paint = new ShellPaint(overlay);
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddSmallButton(list, ref order, overlay, paint, new HitRect(100, 200, 77, 23), "OK", PickerButtonState.Normal, "btn");
        SceneSprite text = PickerSprites.One(list, "btn-text");
        Assert.Equal(100 + (77 - text.Frame.Width) / 2, text.ScreenX);
        Assert.Equal(200 + (23 - text.Frame.Height) / 2, text.ScreenY);
    }

    // ------------------------------------------------------------------ 大按钮与背景

    [Fact]
    public void The_start_button_without_art_is_a_solid_block_whose_color_shows_the_state()
    {
        var art = new PickerArt(new ShellFakeSprites());
        var overlay = new OverlayArt { Localize = false };
        var live = new List<SceneSprite>();
        var gray = new List<SceneSprite>();
        int order = 0;
        art.AddStartButton(live, ref order, overlay, PickerButtonState.Normal, 0, "start");
        art.AddStartButton(gray, ref order, overlay, PickerButtonState.Disabled, 0, "start");
        SceneSprite liveFace = PickerSprites.One(live, "start");
        SceneSprite grayFace = PickerSprites.One(gray, "start");
        Assert.Equal((337, 526, 125, 43), (liveFace.ScreenX, liveFace.ScreenY, liveFace.Frame.Width, liveFace.Frame.Height));
        Assert.Equal((0x1A, 0x3A, 0x60), (Px(liveFace.Frame, 0, 0).R, Px(liveFace.Frame, 0, 0).G, Px(liveFace.Frame, 0, 0).B));
        Assert.Equal((0x30, 0x38, 0x40), (Px(grayFace.Frame, 0, 0).R, Px(grayFace.Frame, 0, 0).G, Px(grayFace.Frame, 0, 0).B));
    }

    [Fact]
    public void The_background_falls_back_to_a_solid_backdrop()
    {
        var art = new PickerArt(new ShellFakeSprites());
        var overlay = new OverlayArt { Localize = false };
        var list = new List<SceneSprite>();
        int order = 0;
        art.AddBackground(list, ref order, overlay, null, 0, "bg");
        SceneSprite backdrop = PickerSprites.One(list, "bg-backdrop");
        Assert.Equal((0, 0, 800, 600), (backdrop.ScreenX, backdrop.ScreenY, backdrop.Frame.Width, backdrop.Frame.Height));
        Assert.Equal((0x0A, 0x12, 0x20), (Px(backdrop.Frame, 5, 5).R, Px(backdrop.Frame, 5, 5).G, Px(backdrop.Frame, 5, 5).B));
    }

    [Fact]
    public void The_banner_label_is_drawn_from_art_or_replaced_by_text()
    {
        var sprites = new ShellFakeSprites();
        sprites.Frames[(ServerArt.ChannelBaseImage, PickerArt.BannerFrame)] = PickerSprites.Block(256, 36, 0, 0);
        sprites.Frames[(ServerArt.ChannelBackgroundImage, 7)] = PickerSprites.Block(150, 40, 0, 0);
        var art = new PickerArt(sprites);
        var overlay = new OverlayArt { Localize = false };
        var paint = new ShellPaint(overlay);

        var withLabel = new List<SceneSprite>();
        int order = 0;
        art.AddBanner(withLabel, ref order, paint, ServerArt.Find("希洛克"), "希洛克", "SERVER 1");
        Assert.True(PickerRig.Has(withLabel, "picker-banner-label"));
        Assert.False(PickerRig.Has(withLabel, "picker-banner-text"));

        var withText = new List<SceneSprite>();
        art.AddBanner(withText, ref order, paint, null, "未知", "SERVER 2");
        Assert.False(PickerRig.Has(withText, "picker-banner-label"));
        SceneSprite text = PickerSprites.One(withText, "picker-banner-text");
        Assert.Equal(273 + (256 - text.Frame.Width) / 2, text.ScreenX);          // 在横幅正中
        Assert.Equal(73 + (36 - text.Frame.Height) / 2, text.ScreenY);
    }

    // ------------------------------------------------------------------ 翻页箭头

    [Fact]
    public void Arrows_are_cached_22_by_18_with_transparent_corners()
    {
        SpriteFrame arrow = PickerArt.Arrow(true, true, false);
        Assert.Same(arrow, PickerArt.Arrow(true, true, false));
        Assert.Equal((22, 18), (arrow.Width, arrow.Height));
        Assert.Equal(0, Px(arrow, 0, 0).A);
        Assert.Equal(0, Px(arrow, 21, 0).A);
        Assert.Equal(0, Px(arrow, 0, 17).A);
        Assert.Equal(0, Px(arrow, 21, 17).A);
        Assert.Equal(255, Px(arrow, 1, 0).A);
        Assert.Equal(255, Px(arrow, 10, 9).A);
        Assert.Equal((22, 18), (PickerArt.ArrowWidth, PickerArt.ArrowHeight));
    }

    [Fact]
    public void Arrows_point_the_way_they_are_named()
    {
        SpriteFrame left = PickerArt.Arrow(true, true, false);
        SpriteFrame right = PickerArt.Arrow(false, true, false);
        (byte R, byte G, byte B, byte A) ink = (0xE9, 0xC4, 0x6A, 0xFF);
        // 朝左的三角:尖在左、底在右,右半边靠上的点在三角里;朝右的相反
        Assert.Equal(ink, Px(left, 14, 5));
        Assert.NotEqual(ink, Px(right, 14, 5));
        Assert.Equal(ink, Px(right, 7, 5));
        Assert.NotEqual(ink, Px(left, 7, 5));
        Assert.Equal(ink, Px(left, 10, 8));
        Assert.Equal(ink, Px(right, 10, 8));
    }

    [Fact]
    public void Arrow_states_differ_and_hover_only_matters_when_enabled()
    {
        SpriteFrame normal = PickerArt.Arrow(true, true, false);
        SpriteFrame hover = PickerArt.Arrow(true, true, true);
        SpriteFrame disabled = PickerArt.Arrow(true, false, false);
        Assert.NotSame(normal, hover);
        Assert.NotSame(normal, disabled);
        Assert.Same(disabled, PickerArt.Arrow(true, false, true));              // 灰着时悬停没有区别
        Assert.Equal((0xFF, 0xE9, 0xA0), (Px(hover, 10, 8).R, Px(hover, 10, 8).G, Px(hover, 10, 8).B));
        Assert.Equal((0x4A, 0x50, 0x5C), (Px(disabled, 10, 8).R, Px(disabled, 10, 8).G, Px(disabled, 10, 8).B));
        Assert.NotSame(PickerArt.Arrow(true, true, false), PickerArt.Arrow(false, true, false));
    }
}
