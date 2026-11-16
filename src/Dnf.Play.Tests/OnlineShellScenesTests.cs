using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>外壳的共用画法与两张辅助画面:状态画面(选服务器、连频道、加载)与选角界面上的提示叠层。</summary>
public class OnlineShellScenesTests
{
    private static bool SameFrame(SpriteFrame a, SpriteFrame b)
    {
        return a.Width == b.Width && a.Height == b.Height && a.Rgba.AsSpan().SequenceEqual(b.Rgba);
    }

    private static SpriteFrame Expect(string text, int scale, uint color) => new OverlayArt().Text(text, scale, color);

    private static string[] Tags(List<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToArray();

    private static SceneSprite One(List<SceneSprite> sprites, string tag) => Assert.Single(sprites, s => s.AnimationPath == tag);

    private static CharacterSelectScene ArtScene()
    {
        var sprites = new ShellFakeSprites();
        sprites.AddBackground();
        return new CharacterSelectScene(sprites);
    }

    // ------------------------------------------------------------------------------------------------ 状态画面

    [Fact]
    public void The_status_screen_has_a_backdrop_a_centred_title_and_a_centred_hint()
    {
        var screen = new OnlineShellStatusScreen(null);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(new[] { "online-backdrop", "online-status-title", "online-status-hint" }, Tags(sprites));
        SceneSprite title = One(sprites, "online-status-title");
        Assert.True(SameFrame(Expect("ENTERING GAME...", 3, OverlayArt.White), title.Frame));
        Assert.Equal(400 - title.Frame.Width / 2, title.ScreenX);
        Assert.Equal(250, title.ScreenY);
        SceneSprite hint = One(sprites, "online-status-hint");
        Assert.True(SameFrame(Expect("ESC CANCEL", 1, OverlayArt.White), hint.Frame));
        Assert.Equal(400 - hint.Frame.Width / 2, hint.ScreenX);
        Assert.Equal(540, hint.ScreenY);
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
    }

    [Fact]
    public void The_status_screen_follows_its_title_hint_and_notice()
    {
        var screen = new OnlineShellStatusScreen(null)
        {
            Title = OnlineShellLabel.ServerSelect,
            Hint = OnlineShellLabel.ChannelHint,
            Notice = new OnlineNotice(OnlineNoticeKind.ConnectFailed),
        };

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(new[] { "online-backdrop", "online-status-title", "online-status-notice", "online-status-hint" }, Tags(sprites));
        Assert.True(SameFrame(Expect("SELECTING SERVER...", 3, OverlayArt.White), One(sprites, "online-status-title").Frame));
        SceneSprite notice = One(sprites, "online-status-notice");
        Assert.True(SameFrame(Expect("CANNOT CONNECT CHANNEL", 2, OverlayArt.Orange), notice.Frame));
        Assert.Equal(400 - notice.Frame.Width / 2, notice.ScreenX);
        Assert.Equal(310, notice.ScreenY);
        Assert.True(SameFrame(Expect("ENTER RETRY   ESC BACK", 1, OverlayArt.White), One(sprites, "online-status-hint").Frame));

        screen.Notice = new OnlineNotice(OnlineNoticeKind.Cancelled);
        Assert.True(SameFrame(Expect("CANCELLED", 2, OverlayArt.Yellow), One(screen.Build(0), "online-status-notice").Frame));

        screen.Notice = null;
        Assert.Equal(new[] { "online-backdrop", "online-status-title", "online-status-hint" }, Tags(screen.Build(0)));
    }

    [Fact]
    public void The_status_screen_uses_the_character_select_background_when_available()
    {
        var sprites = new ShellFakeSprites();
        SpriteFrame frame = sprites.AddBackground(9, 2, 4);
        var screen = new OnlineShellStatusScreen(sprites);

        SceneSprite bg = screen.Build(0)[0];

        Assert.Equal("online-background", bg.AnimationPath);
        Assert.Same(frame, bg.Frame);
        Assert.Equal((2, 4), (bg.ScreenX, bg.ScreenY));
    }

    [Fact]
    public void The_status_screen_speaks_chinese_with_fonts_and_uses_the_bigger_hint()
    {
        var fonts = new RecordingFonts();
        var screen = new OnlineShellStatusScreen(null, fonts)
        {
            Title = OnlineShellLabel.ChannelConnecting,
            Hint = OnlineShellLabel.ChannelHint,
            Notice = new OnlineNotice(OnlineNoticeKind.NetworkError),
        };

        screen.Build(0);

        Assert.True(fonts.Asked("正在连接频道"));
        Assert.True(fonts.Asked("网络错误或连接超时"));
        Assert.True(fonts.Asked("重试"));
        Assert.True(fonts.Asked("返回登录"));
        Assert.Contains(OverlayArt.GlyphPixelSize(2), fonts.Sizes);
    }

    // ------------------------------------------------------------------------------------------------ 提示叠层

    [Fact]
    public void The_overlay_without_a_notice_changes_nothing()
    {
        var inner = new CharacterSelectScreen(OnlineShellRoster.Placeholder(ShellTestData.Roster(2)));
        var overlay = new OnlineShellNoticeOverlay(inner);

        Assert.Equal(Tags(inner.Build(0)), Tags(overlay.Build(0)));
        Assert.Null(overlay.Notice);
    }

    [Fact]
    public void The_overlay_adds_one_centred_line_on_top_of_everything()
    {
        var inner = new CharacterSelectScreen(OnlineShellRoster.Placeholder(ShellTestData.Roster(2)), ArtScene());
        var overlay = new OnlineShellNoticeOverlay(inner) { Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed) };

        List<SceneSprite> plain = inner.Build(0);
        List<SceneSprite> sprites = overlay.Build(0);

        Assert.Equal(plain.Count + 1, sprites.Count);
        SceneSprite notice = sprites[^1];
        Assert.Equal("online-select-notice", notice.AnimationPath);
        Assert.True(SameFrame(Expect("CANNOT ENTER WITH THIS CHARACTER", 2, OverlayArt.Orange), notice.Frame));
        Assert.Equal(400 - notice.Frame.Width / 2, notice.ScreenX);
        Assert.Equal(506, notice.ScreenY);
        Assert.True(notice.SortKey > plain.Max(s => s.SortKey));
        Assert.Equal(OverlayArt.UiRank, notice.LayerRank);
    }

    [Fact]
    public void The_overlay_colours_info_notices_yellow_and_picks_up_a_changed_notice()
    {
        var inner = new CharacterSelectScreen(OnlineShellRoster.Placeholder(ShellTestData.Roster(0)));
        var overlay = new OnlineShellNoticeOverlay(inner) { Notice = new OnlineNotice(OnlineNoticeKind.NoCharacters) };

        SceneSprite first = One(overlay.Build(0), "online-select-notice");
        Assert.True(SameFrame(Expect("NO CHARACTER", 2, OverlayArt.Yellow), first.Frame));

        overlay.Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed);
        Assert.True(SameFrame(Expect("CANNOT ENTER WITH THIS CHARACTER", 2, OverlayArt.Orange), One(overlay.Build(0), "online-select-notice").Frame));
    }

    [Fact]
    public void The_overlay_needs_an_inner_scene()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineShellNoticeOverlay(null));
    }

    [Fact]
    public void The_overlay_speaks_chinese_with_fonts()
    {
        var fonts = new RecordingFonts();
        var inner = new CharacterSelectScreen(OnlineShellRoster.Placeholder(ShellTestData.Roster(1)));
        var overlay = new OnlineShellNoticeOverlay(inner, fonts) { Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed) };

        overlay.Build(0);

        Assert.True(fonts.Asked("这个角色暂时进不去,请换一个"));
    }

    // ------------------------------------------------------------------------------------------------ 共用画法

    [Theory]
    [InlineData("player_01", "PLAYER-01")]
    [InlineData("Ab#", "AB?")]
    [InlineData("a b", "A B")]
    [InlineData("名字", "??")]
    [InlineData("", "")]
    [InlineData("x_y_z", "X-Y-Z")]
    public void Glyph_safe_text_is_capital_with_dashes_and_question_marks(string text, string expected)
    {
        Assert.Equal(expected, OnlineShellArt.GlyphSafe(text));
    }

    [Fact]
    public void Notice_colour_is_orange_for_errors_and_yellow_for_information()
    {
        Assert.Equal(OverlayArt.Orange, OnlineShellArt.NoticeColor(new OnlineNotice(OnlineNoticeKind.WrongCredentials)));
        Assert.Equal(OverlayArt.Yellow, OnlineShellArt.NoticeColor(new OnlineNotice(OnlineNoticeKind.RegisterOk)));
        Assert.Equal(OverlayArt.Yellow, OnlineShellArt.NoticeColor(null));
    }

    [Fact]
    public void Empty_text_adds_no_sprite_and_text_is_placed_or_centred_where_asked()
    {
        var art = new OverlayArt { Localize = false };
        var sprites = new List<SceneSprite>();
        int order = 0;

        Assert.Null(OnlineShellArt.AddText(sprites, ref order, art, string.Empty, 5, 6, 1, OverlayArt.White, "t"));
        Assert.Null(OnlineShellArt.AddCentered(sprites, ref order, art, null, 100, 6, 1, OverlayArt.White, "c"));
        Assert.Empty(sprites);
        Assert.Equal(0, order);

        SpriteFrame placed = OnlineShellArt.AddText(sprites, ref order, art, "AB", 5, 6, 2, OverlayArt.White, "t");
        SpriteFrame centred = OnlineShellArt.AddCentered(sprites, ref order, art, "AB", 100, 8, 2, OverlayArt.White, "c");

        Assert.Equal(2, order);
        Assert.Equal((5, 6, 0), (sprites[0].ScreenX, sprites[0].ScreenY, sprites[0].SortKey));
        Assert.Same(placed, sprites[0].Frame);
        Assert.Equal((100 - centred.Width / 2, 8, 1), (sprites[1].ScreenX, sprites[1].ScreenY, sprites[1].SortKey));
    }

    [Fact]
    public void Data_text_is_null_for_nothing_and_goes_through_the_fonts_when_there_are_some()
    {
        var plain = new OverlayArt { Localize = false };
        Assert.Null(OnlineShellArt.DataText(plain, null, 1, OverlayArt.White));
        Assert.Null(OnlineShellArt.DataText(plain, string.Empty, 1, OverlayArt.White));
        SpriteFrame bitmap = OnlineShellArt.DataText(plain, "ab_", 1, OverlayArt.White);
        Assert.True(SameFrame(Expect("AB-", 1, OverlayArt.White), bitmap));

        var fonts = new RecordingFonts();
        var withFonts = new OverlayArt { Localize = false, Fonts = fonts };
        Assert.NotNull(OnlineShellArt.DataText(withFonts, "ab_", 2, OverlayArt.White));
        Assert.True(fonts.Asked("ab_"));                              // 有字体:原样画,不转大写
        Assert.DoesNotContain((int)'A', fonts.CodePoints);
    }

    [Fact]
    public void The_background_helper_uses_a_solid_fallback_and_numbers_sprites_from_the_given_order()
    {
        var art = new OverlayArt { Localize = false };
        var sprites = new List<SceneSprite>();
        int order = 5;

        OnlineShellArt.AddBackground(sprites, ref order, art, null);

        Assert.Equal(6, order);
        Assert.Equal(5, sprites[0].SortKey);
        Assert.Equal("online-backdrop", sprites[0].AnimationPath);
    }
}
