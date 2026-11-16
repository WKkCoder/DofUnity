using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 外壳的共用画法(背景、暗角、卡片、提示条、按键说明)与两张辅助画面:状态画面(选服务器、连频道、加载)与选角界面上的提示叠层。
/// 没有字体的场合文字是点阵字库的英文占位,用点阵字库自己画出来的帧做对照。
/// </summary>
public class OnlineShellScenesTests
{
    private static bool SameFrame(SpriteFrame a, SpriteFrame b) => ShellFrames.Same(a, b);

    private static SpriteFrame Expect(string text, int scale, uint fill, uint edge = ShellPaint.Navy) => new OverlayArt().Text(text, scale, fill, edge);

    private static ShellPaint NewPaint() => new ShellPaint(new OverlayArt { Localize = false });

    private static string[] Tags(List<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToArray();

    private static SceneSprite One(List<SceneSprite> sprites, string tag) => Assert.Single(sprites, s => s.AnimationPath == tag);

    private static CharacterSelectScene ArtScene()
    {
        var sprites = new ShellFakeSprites();
        sprites.AddBackground();
        return new CharacterSelectScene(sprites);
    }

    private static readonly string[] StatusBackdrop = { "online-backdrop", "online-vignette" };

    private static readonly string[] StatusPanel =
    {
        "online-status-panel-shadow", "online-status-panel", "online-status-panel-corner-0", "online-status-panel-corner-1",
        "online-status-panel-corner-2", "online-status-panel-corner-3",
    };

    // ------------------------------------------------------------------------------------------------ 状态画面

    [Fact]
    public void The_status_screen_has_a_card_with_a_spinner_a_centred_title_and_a_centred_hint()
    {
        var screen = new OnlineShellStatusScreen(null);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(StatusBackdrop.Concat(StatusPanel).Concat(new[]
        {
            "online-status-spinner", "online-status-title", "online-status-hint-key-0", "online-status-hint-keytext-0", "online-status-hint-text-0",
        }).ToArray(), Tags(sprites));
        SceneSprite panel = One(sprites, "online-status-panel");
        Assert.Equal((180, 200, 440, 150), (panel.ScreenX, panel.ScreenY, panel.Frame.Width, panel.Frame.Height));
        SceneSprite spinner = One(sprites, "online-status-spinner");
        Assert.Equal((400 - 19, 226, 38, 38), (spinner.ScreenX, spinner.ScreenY, spinner.Frame.Width, spinner.Frame.Height));
        SceneSprite title = One(sprites, "online-status-title");
        Assert.True(SameFrame(Expect("ENTERING GAME...", 2, ShellPaint.Ink), title.Frame));
        Assert.Equal(400 - title.Frame.Width / 2, title.ScreenX);
        Assert.Equal(278, title.ScreenY);
        Assert.True(spinner.ScreenY + spinner.Frame.Height < title.ScreenY);
        Assert.True(title.ScreenY + title.Frame.Height < panel.ScreenY + panel.Frame.Height);
        Assert.True(SameFrame(Expect("CANCEL", 1, ShellPaint.Muted), One(sprites, "online-status-hint-text-0").Frame));
        Assert.True(SameFrame(Expect("ESC", 1, ShellPaint.Ink), One(sprites, "online-status-hint-keytext-0").Frame));
        Assert.Equal(OnlineShellArt.HintTop, One(sprites, "online-status-hint-key-0").ScreenY);
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(sprites.Select(s => s.SortKey).OrderBy(k => k).Distinct().ToArray(), sprites.Select(s => s.SortKey).ToArray());
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

        Assert.Equal(StatusBackdrop.Concat(StatusPanel).Concat(new[]
        {
            "online-status-icon", "online-status-title", "online-status-notice-bar", "online-status-notice-accent", "online-status-notice",
            "online-status-hint-key-0", "online-status-hint-keytext-0", "online-status-hint-text-0",
            "online-status-hint-key-1", "online-status-hint-keytext-1", "online-status-hint-text-1",
        }).ToArray(), Tags(sprites));
        Assert.Equal((440, 196), (One(sprites, "online-status-panel").Frame.Width, One(sprites, "online-status-panel").Frame.Height));
        Assert.True(SameFrame(Expect("SELECTING SERVER...", 2, ShellPaint.Ink), One(sprites, "online-status-title").Frame));
        SceneSprite icon = One(sprites, "online-status-icon");
        Assert.Equal((400 - 19, 226, 38, 38), (icon.ScreenX, icon.ScreenY, icon.Frame.Width, icon.Frame.Height));
        Assert.True(SameFrame(NewPaint().Icon(ShellTone.Error, 38), icon.Frame));
        SceneSprite notice = One(sprites, "online-status-notice");
        Assert.True(SameFrame(Expect("CANNOT CONNECT CHANNEL", 1, OnlineShellArt.ToneTextColor(ShellTone.Error)), notice.Frame));
        SceneSprite bar = One(sprites, "online-status-notice-bar");
        Assert.Equal(OnlineShellArt.NoticeBarWidth(NewPaint(), "CANNOT CONNECT CHANNEL", 392, false), bar.Frame.Width);
        Assert.Equal(400 - bar.Frame.Width / 2, bar.ScreenX);
        Assert.Equal((326, OnlineShellArt.NoticeBarHeight), (bar.ScreenY, bar.Frame.Height));
        Assert.Equal((bar.ScreenX + 20, bar.ScreenY + (OnlineShellArt.NoticeBarHeight - notice.Frame.Height) / 2), (notice.ScreenX, notice.ScreenY));
        Assert.True(bar.ScreenY + bar.Frame.Height < One(sprites, "online-status-panel").ScreenY + 196);
        Assert.True(SameFrame(Expect("RETRY", 1, ShellPaint.Muted), One(sprites, "online-status-hint-text-0").Frame));
        Assert.True(SameFrame(Expect("BACK", 1, ShellPaint.Muted), One(sprites, "online-status-hint-text-1").Frame));

        screen.Notice = new OnlineNotice(OnlineNoticeKind.Cancelled);
        List<SceneSprite> info = screen.Build(0);
        Assert.True(SameFrame(Expect("CANCELLED", 1, OnlineShellArt.ToneTextColor(ShellTone.Info)), One(info, "online-status-notice").Frame));
        Assert.True(SameFrame(NewPaint().Icon(ShellTone.Info, 38), One(info, "online-status-icon").Frame));

        screen.Notice = new OnlineNotice(OnlineNoticeKind.RegisterOk);
        Assert.True(SameFrame(NewPaint().Icon(ShellTone.Success, 38), One(screen.Build(0), "online-status-icon").Frame));

        screen.Notice = null;
        Assert.DoesNotContain("online-status-notice", Tags(screen.Build(0)));
        Assert.Contains("online-status-spinner", Tags(screen.Build(0)));
        Assert.Equal(150, One(screen.Build(0), "online-status-panel").Frame.Height);
    }

    [Fact]
    public void The_status_spinner_turns_with_time_and_the_icon_stands_still()
    {
        var screen = new OnlineShellStatusScreen(null);

        SceneSprite at0 = One(screen.Build(0), "online-status-spinner");
        SceneSprite at80 = One(screen.Build(80), "online-status-spinner");
        SceneSprite at800 = One(screen.Build(800), "online-status-spinner");
        Assert.False(SameFrame(at0.Frame, at80.Frame));
        Assert.Same(at0.Frame, at800.Frame);
        Assert.Same(at0.Frame, One(screen.Build(79), "online-status-spinner").Frame);

        screen.Notice = new OnlineNotice(OnlineNoticeKind.NetworkError);
        Assert.Same(One(screen.Build(0), "online-status-icon").Frame, One(screen.Build(500), "online-status-icon").Frame);
        Assert.Same(One(screen.Build(0), "online-status-title").Frame, One(screen.Build(500), "online-status-title").Frame);
    }

    [Fact]
    public void The_status_screen_uses_the_character_select_background_when_available()
    {
        var sprites = new ShellFakeSprites();
        SpriteFrame frame = sprites.AddBackground(9, 2, 4);
        var screen = new OnlineShellStatusScreen(sprites);

        List<SceneSprite> scene = screen.Build(0);

        SceneSprite bg = scene[0];
        Assert.Equal("online-background", bg.AnimationPath);
        Assert.Same(frame, bg.Frame);
        Assert.Equal((2, 4), (bg.ScreenX, bg.ScreenY));
        Assert.Equal("online-vignette", scene[1].AnimationPath);
    }

    [Fact]
    public void The_status_screen_speaks_chinese_with_fonts_at_the_designed_pixel_sizes()
    {
        var fonts = new RecordingFonts();
        var screen = new OnlineShellStatusScreen(null, fonts)
        {
            Title = OnlineShellLabel.ChannelConnecting,
            Hint = OnlineShellLabel.ChannelHint,
            Notice = new OnlineNotice(OnlineNoticeKind.NetworkError),
        };

        screen.Build(0);

        foreach (string text in new[] { "正在连接频道…", "网络错误或连接超时", "重试", "返回登录", "Enter", "Esc" })
        {
            Assert.True(fonts.Asked(text), text);
        }
        Assert.Equal(new[] { 12, 14, 24 }, fonts.Sizes.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void The_status_screen_titles_hold_in_one_line_inside_the_card_with_and_without_fonts()
    {
        foreach (IGlyphFonts fonts in new IGlyphFonts[] { null, new SizedFonts() })
        {
            foreach (OnlineShellLabel title in new[] { OnlineShellLabel.ServerSelect, OnlineShellLabel.ChannelConnecting, OnlineShellLabel.Loading })
            {
                var screen = new OnlineShellStatusScreen(null, fonts) { Title = title };

                List<SceneSprite> sprites = screen.Build(0);

                SceneSprite panel = One(sprites, "online-status-panel");
                SceneSprite text = One(sprites, "online-status-title");
                Assert.True(text.ScreenX >= panel.ScreenX && text.ScreenX + text.Frame.Width <= panel.ScreenX + panel.Frame.Width, title.ToString());
            }
        }
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
    public void The_overlay_adds_one_centred_notice_bar_on_top_of_everything_and_clear_of_the_start_button()
    {
        var inner = new CharacterSelectScreen(OnlineShellRoster.Placeholder(ShellTestData.Roster(2)), ArtScene());
        var overlay = new OnlineShellNoticeOverlay(inner) { Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed) };

        List<SceneSprite> plain = inner.Build(0);
        List<SceneSprite> sprites = overlay.Build(0);

        Assert.Equal(plain.Count + 4, sprites.Count);
        Assert.Equal(new[] { "online-select-notice-bar", "online-select-notice-accent", "online-select-notice-icon", "online-select-notice" }, Tags(sprites.Skip(plain.Count).ToList()));
        SceneSprite bar = sprites[plain.Count];
        SceneSprite text = sprites[^1];
        Assert.True(SameFrame(Expect("CANNOT ENTER WITH THIS CHARACTER", 1, OnlineShellArt.ToneTextColor(ShellTone.Error)), text.Frame));
        Assert.Equal(OnlineShellArt.NoticeBarWidth(NewPaint(), "CANNOT ENTER WITH THIS CHARACTER", 560), bar.Frame.Width);
        Assert.Equal(400 - bar.Frame.Width / 2, bar.ScreenX);
        Assert.Equal((OnlineShellNoticeOverlay.NoticeY, OnlineShellArt.NoticeBarHeight), (bar.ScreenY, bar.Frame.Height));
        Assert.True(bar.ScreenY + bar.Frame.Height <= CharacterSelectArt.StartY, "提示条不能压住\"开始\"按钮");
        Assert.True(bar.ScreenY + bar.Frame.Height <= CharacterSelectArt.SmallButtonY);
        Assert.True(sprites.Skip(plain.Count).All(s => s.SortKey > plain.Max(p => p.SortKey)));
        Assert.All(sprites.Skip(plain.Count), s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        SceneSprite icon = sprites[plain.Count + 2];
        Assert.True(SameFrame(NewPaint().Icon(ShellTone.Error, 20), icon.Frame));
    }

    [Fact]
    public void The_overlay_colours_info_notices_gold_and_picks_up_a_changed_notice()
    {
        var inner = new CharacterSelectScreen(OnlineShellRoster.Placeholder(ShellTestData.Roster(0)));
        var overlay = new OnlineShellNoticeOverlay(inner) { Notice = new OnlineNotice(OnlineNoticeKind.NoCharacters) };

        List<SceneSprite> first = overlay.Build(0);
        Assert.True(SameFrame(Expect("NO CHARACTER", 1, OnlineShellArt.ToneTextColor(ShellTone.Info)), One(first, "online-select-notice").Frame));
        Assert.True(SameFrame(NewPaint().Icon(ShellTone.Info, 20), One(first, "online-select-notice-icon").Frame));

        overlay.Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed);
        List<SceneSprite> second = overlay.Build(0);
        Assert.True(SameFrame(Expect("CANNOT ENTER WITH THIS CHARACTER", 1, OnlineShellArt.ToneTextColor(ShellTone.Error)), One(second, "online-select-notice").Frame));
        Assert.True(SameFrame(NewPaint().Icon(ShellTone.Error, 20), One(second, "online-select-notice-icon").Frame));

        overlay.Notice = null;
        Assert.DoesNotContain("online-select-notice", Tags(overlay.Build(0)));
    }

    [Fact]
    public void The_overlay_numbers_its_sprites_above_whatever_the_inner_scene_used()
    {
        var overlay = new OnlineShellNoticeOverlay(new StubScene(100)) { Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed) };

        List<SceneSprite> sprites = overlay.Build(0);

        Assert.Equal(new[] { 100, 101, 102, 103, 104 }, sprites.Select(s => s.SortKey).ToArray());
    }

    [Fact]
    public void The_overlay_needs_an_inner_scene()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineShellNoticeOverlay(null));
    }

    [Fact]
    public void The_overlay_speaks_chinese_with_fonts_and_keeps_every_notice_inside_the_screen()
    {
        var fonts = new RecordingFonts();
        var inner = new CharacterSelectScreen(OnlineShellRoster.Placeholder(ShellTestData.Roster(1)));
        var overlay = new OnlineShellNoticeOverlay(inner, fonts) { Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed) };

        overlay.Build(0);

        Assert.True(fonts.Asked("这个角色暂时进不去，请换一个"));
        Assert.DoesNotContain((int)',', fonts.CodePoints);

        foreach (IGlyphFonts any in new IGlyphFonts[] { null, new SizedFonts() })
        {
            foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None))
            {
                var wide = new OnlineShellNoticeOverlay(new StubScene(0), any) { Notice = new OnlineNotice(kind, seconds: 30, detail: "x") };

                SceneSprite bar = wide.Build(0).Single(s => s.AnimationPath == "online-select-notice-bar");

                Assert.True(bar.ScreenX >= 0 && bar.ScreenX + bar.Frame.Width <= OnlineShellArt.ScreenWidth, kind.ToString());
                Assert.True(bar.Frame.Width <= 560);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ 语气

    [Fact]
    public void A_notice_is_an_error_a_success_or_just_information()
    {
        Assert.Equal(ShellTone.Info, OnlineShellArt.ToneOf(null));
        Assert.Equal(ShellTone.Error, OnlineShellArt.ToneOf(new OnlineNotice(OnlineNoticeKind.WrongCredentials)));
        Assert.Equal(ShellTone.Error, OnlineShellArt.ToneOf(new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: 3)));
        Assert.Equal(ShellTone.Success, OnlineShellArt.ToneOf(new OnlineNotice(OnlineNoticeKind.RegisterOk)));
        Assert.Equal(ShellTone.Success, OnlineShellArt.ToneOf(new OnlineNotice(OnlineNoticeKind.PasswordChanged)));
        Assert.Equal(ShellTone.Info, OnlineShellArt.ToneOf(new OnlineNotice(OnlineNoticeKind.Cancelled)));
        Assert.Equal(ShellTone.Info, OnlineShellArt.ToneOf(new OnlineNotice(OnlineNoticeKind.NoCharacters)));
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None))
        {
            var notice = new OnlineNotice(kind, seconds: 5);
            Assert.True(notice.IsError == (OnlineShellArt.ToneOf(notice) == ShellTone.Error), kind.ToString());
        }
    }

    [Fact]
    public void Each_tone_has_its_own_text_colour_and_bar_style()
    {
        var tones = new[] { ShellTone.Error, ShellTone.Success, ShellTone.Wait, ShellTone.Info };

        Assert.Equal(0xFFC4A6u, OnlineShellArt.ToneTextColor(ShellTone.Error));
        Assert.Equal(0xB8F3CEu, OnlineShellArt.ToneTextColor(ShellTone.Success));
        Assert.Equal(0xC6DCFFu, OnlineShellArt.ToneTextColor(ShellTone.Wait));
        Assert.Equal(0xFFE9A0u, OnlineShellArt.ToneTextColor(ShellTone.Info));
        Assert.Equal(4, tones.Select(OnlineShellArt.ToneTextColor).Distinct().Count());
        Assert.Equal(4, tones.Select(t => OnlineShellArt.ToneBox(t).Key).Distinct().Count());
        ShellBox error = OnlineShellArt.ToneBox(ShellTone.Error);
        ShellBox success = OnlineShellArt.ToneBox(ShellTone.Success);
        ShellBox wait = OnlineShellArt.ToneBox(ShellTone.Wait);
        Assert.True(((error.EdgeTop >> 16) & 0xFF) > (error.EdgeTop & 0xFF));                  // 错误:偏红
        Assert.True(((success.EdgeTop >> 8) & 0xFF) > ((success.EdgeTop >> 16) & 0xFF));       // 成功:偏绿
        Assert.True((wait.EdgeTop & 0xFF) > ((wait.EdgeTop >> 16) & 0xFF));                    // 等待:偏蓝
        Assert.All(tones, t => Assert.True((OnlineShellArt.ToneBox(t).Top >> 24) >= 0xF0));      // 底色基本不透明,字才看得清
    }

    // ------------------------------------------------------------------------------------------------ 提示条

    [Fact]
    public void The_notice_bar_width_is_the_chrome_plus_the_text_clamped_to_the_maximum()
    {
        ShellPaint paint = NewPaint();

        Assert.Equal(0, OnlineShellArt.NoticeBarWidth(paint, string.Empty, 400));
        Assert.Equal(0, OnlineShellArt.NoticeBarWidth(paint, null, 400));
        Assert.Equal(56 + OverlayArt.TextWidth("HI", 1), OnlineShellArt.NoticeBarWidth(paint, "HI", 400));
        Assert.Equal(34 + OverlayArt.TextWidth("HI", 1), OnlineShellArt.NoticeBarWidth(paint, "HI", 400, false));
        Assert.Equal(60, OnlineShellArt.NoticeBarWidth(paint, "A VERY LONG NOTICE LINE", 60));
        Assert.True(OnlineShellArt.NoticeBarWidth(paint, "WRONG ACCOUNT OR PASSWORD", 560) < 560);
    }

    [Fact]
    public void A_notice_bar_has_a_base_an_accent_an_icon_and_the_text_at_fixed_offsets()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 7;

        OnlineShellArt.AddNoticeBar(sprites, ref order, paint, ShellTone.Error, "FAILED", 10, 20, 300, 0, "n");

        Assert.Equal(new[] { "n-bar", "n-accent", "n-icon", "n" }, Tags(sprites));
        Assert.Equal(11, order);
        Assert.Equal(new[] { 7, 8, 9, 10 }, sprites.Select(s => s.SortKey).ToArray());
        SceneSprite bar = One(sprites, "n-bar");
        Assert.Equal((10, 20, 300, 32), (bar.ScreenX, bar.ScreenY, bar.Frame.Width, bar.Frame.Height));
        Assert.True(SameFrame(paint.Box(300, 32, 8, OnlineShellArt.ToneBox(ShellTone.Error)), bar.Frame));
        SceneSprite accent = One(sprites, "n-accent");
        Assert.Equal((11, 21, 4, 30), (accent.ScreenX, accent.ScreenY, accent.Frame.Width, accent.Frame.Height));
        SceneSprite icon = One(sprites, "n-icon");
        Assert.Equal((26, 26, 20, 20), (icon.ScreenX, icon.ScreenY, icon.Frame.Width, icon.Frame.Height));
        SceneSprite text = One(sprites, "n");
        Assert.True(SameFrame(Expect("FAILED", 1, OnlineShellArt.ToneTextColor(ShellTone.Error)), text.Frame));
        Assert.Equal((54, 20 + (32 - text.Frame.Height) / 2), (text.ScreenX, text.ScreenY));
    }

    [Fact]
    public void A_waiting_notice_bar_draws_the_spinner_at_the_phase_of_the_time_instead_of_an_icon()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddNoticeBar(sprites, ref order, paint, ShellTone.Wait, "WAIT", 0, 0, 200, 240, "w");

        Assert.Equal(new[] { "w-bar", "w-accent", "w-spinner", "w" }, Tags(sprites));
        Assert.Same(paint.Spinner(20, 3, ShellPaint.Blue), One(sprites, "w-spinner").Frame);
    }

    [Fact]
    public void A_notice_bar_without_the_icon_starts_its_text_closer_and_takes_one_sprite_less()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddNoticeBar(sprites, ref order, paint, ShellTone.Info, "NOTE", 10, 20, 200, 0, "p", false);

        Assert.Equal(new[] { "p-bar", "p-accent", "p" }, Tags(sprites));
        Assert.Equal(3, order);
        Assert.Equal(30, One(sprites, "p").ScreenX);
    }

    [Fact]
    public void A_notice_bar_without_text_draws_nothing_and_a_long_text_is_cut_to_the_bar()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddNoticeBar(sprites, ref order, paint, ShellTone.Error, string.Empty, 0, 0, 200, 0, "e");
        OnlineShellArt.AddNoticeBar(sprites, ref order, paint, ShellTone.Error, null, 0, 0, 200, 0, "e");
        Assert.Empty(sprites);
        Assert.Equal(0, order);

        OnlineShellArt.AddNoticeBar(sprites, ref order, paint, ShellTone.Error, "A VERY LONG NOTICE LINE THAT CANNOT FIT", 10, 10, 100, 0, "c");
        SceneSprite text = One(sprites, "c");
        Assert.True(text.ScreenX + text.Frame.Width <= 10 + 100);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(79, 0)]
    [InlineData(80, 1)]
    [InlineData(159, 1)]
    [InlineData(720, 9)]
    [InlineData(800, 0)]
    [InlineData(880, 1)]
    [InlineData(-100, 0)]
    public void The_spinner_head_moves_one_slot_every_80_ms_and_wraps_every_ten(long timeMs, int phase)
    {
        Assert.Equal(phase, OnlineShellArt.SpinnerPhase(timeMs));
    }

    // ------------------------------------------------------------------------------------------------ 按键说明

    [Fact]
    public void Hints_that_are_empty_or_missing_add_nothing()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddHints(sprites, ref order, paint, null, 400, 548, "h");
        OnlineShellArt.AddHints(sprites, ref order, paint, new ShellHint[0], 400, 548, "h");

        Assert.Empty(sprites);
        Assert.Equal(0, order);
    }

    [Fact]
    public void One_hint_is_a_key_cap_with_the_key_name_and_the_text_beside_it_centred_as_a_row()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddHints(sprites, ref order, paint, new[] { new ShellHint("ESC", "CANCEL") }, 400, 548, "h");

        Assert.Equal(new[] { "h-key-0", "h-keytext-0", "h-text-0" }, Tags(sprites));
        int keyWidth = OverlayArt.TextWidth("ESC", 1) + 16;                         // 键名宽加两边各 8
        int textWidth = OverlayArt.TextWidth("CANCEL", 1);
        int total = keyWidth + 8 + textWidth;
        SceneSprite cap = One(sprites, "h-key-0");
        Assert.Equal((400 - total / 2, 548, keyWidth, 22), (cap.ScreenX, cap.ScreenY, cap.Frame.Width, cap.Frame.Height));
        SceneSprite key = One(sprites, "h-keytext-0");
        Assert.Equal(cap.ScreenX + (keyWidth - key.Frame.Width) / 2, key.ScreenX);
        Assert.Equal(548 + (22 - key.Frame.Height) / 2, key.ScreenY);
        SceneSprite text = One(sprites, "h-text-0");
        Assert.Equal(cap.ScreenX + keyWidth + 8, text.ScreenX);
        Assert.Equal(548 + (22 - text.Frame.Height) / 2, text.ScreenY);
        Assert.True(Math.Abs((cap.ScreenX + text.ScreenX + text.Frame.Width) / 2 - 400) <= 1, "整排按键说明水平居中");
    }

    [Fact]
    public void Two_hints_are_spaced_by_26_pixels_and_a_short_key_name_still_gets_a_30_pixel_cap()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddHints(sprites, ref order, paint, new[] { new ShellHint("A", "ONE"), new ShellHint("B", "TWO") }, 300, 100, "h");

        Assert.Equal(6, sprites.Count);
        Assert.Equal(30, One(sprites, "h-key-0").Frame.Width);
        Assert.Equal(30, One(sprites, "h-key-1").Frame.Width);
        SceneSprite firstText = One(sprites, "h-text-0");
        Assert.Equal(firstText.ScreenX + firstText.Frame.Width + 26, One(sprites, "h-key-1").ScreenX);
        int left = One(sprites, "h-key-0").ScreenX;
        int right = One(sprites, "h-text-1").ScreenX + One(sprites, "h-text-1").Frame.Width;
        Assert.True(Math.Abs((left + right) / 2 - 300) <= 1);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, sprites.Select(s => s.SortKey).ToArray());
    }

    [Fact]
    public void A_hint_without_text_still_draws_its_key_cap()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddHints(sprites, ref order, paint, new[] { new ShellHint("ESC", string.Empty) }, 400, 548, "h");

        Assert.Equal(new[] { "h-key-0", "h-keytext-0" }, Tags(sprites));
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
    public void Adding_a_frame_numbers_it_and_adding_nothing_changes_nothing()
    {
        var sprites = new List<SceneSprite>();
        int order = 4;
        SpriteFrame frame = new OverlayArt().Solid(3, 2, 0x123456);

        Assert.Null(OnlineShellArt.Add(sprites, ref order, null, 1, 2, "x"));
        Assert.Empty(sprites);
        Assert.Equal(4, order);

        SceneSprite added = OnlineShellArt.Add(sprites, ref order, frame, 5, 6, "x");

        Assert.Same(added, sprites.Single());
        Assert.Equal((5, 6, 4, "x", OverlayArt.UiRank), (added.ScreenX, added.ScreenY, added.SortKey, added.AnimationPath, added.LayerRank));
        Assert.Same(frame, added.Frame);
        Assert.Equal(5, order);
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
        Assert.True(ShellFrames.HasColor(sprites[0].Frame, CharacterSelectArt.FallbackBackdrop));
    }

    [Fact]
    public void The_background_helper_places_the_character_select_frame_at_its_own_offset_behind_everything()
    {
        var art = new OverlayArt { Localize = false };
        var fake = new ShellFakeSprites();
        SpriteFrame frame = fake.AddBackground(3, 6, 8);
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddBackground(sprites, ref order, art, fake);

        Assert.Equal(1, order);
        Assert.Same(frame, sprites[0].Frame);
        Assert.Equal((6, 8, "online-background"), (sprites[0].ScreenX, sprites[0].ScreenY, sprites[0].AnimationPath));
        Assert.Equal(OverlayArt.UiRank, sprites[0].LayerRank);
    }

    [Fact]
    public void The_vignette_is_one_screen_sized_frame_at_the_origin_shared_by_every_call_of_a_paint()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddVignette(sprites, ref order, paint);
        OnlineShellArt.AddVignette(sprites, ref order, paint);

        Assert.Equal(2, sprites.Count);
        Assert.Equal((0, 0, 800, 600, "online-vignette"), (sprites[0].ScreenX, sprites[0].ScreenY, sprites[0].Frame.Width, sprites[0].Frame.Height, sprites[0].AnimationPath));
        Assert.Same(sprites[0].Frame, sprites[1].Frame);
        Assert.True(ShellFrames.Pixel(sprites[0].Frame, 400, 300).A < ShellFrames.Pixel(sprites[0].Frame, 0, 0).A);
    }

    [Fact]
    public void A_card_is_a_shadow_a_glass_base_and_four_gold_corner_brackets()
    {
        ShellPaint paint = NewPaint();
        var sprites = new List<SceneSprite>();
        int order = 0;

        OnlineShellArt.AddCard(sprites, ref order, paint, 100, 50, 200, 120, "c");

        Assert.Equal(new[] { "c-shadow", "c", "c-corner-0", "c-corner-1", "c-corner-2", "c-corner-3" }, Tags(sprites));
        Assert.Equal(6, order);
        SceneSprite shadow = One(sprites, "c-shadow");
        Assert.Equal((100 - 26, 50 - 14, 200 + 52, 120 + 52), (shadow.ScreenX, shadow.ScreenY, shadow.Frame.Width, shadow.Frame.Height));
        SceneSprite card = One(sprites, "c");
        Assert.Equal((100, 50, 200, 120), (card.ScreenX, card.ScreenY, card.Frame.Width, card.Frame.Height));
        Assert.True(SameFrame(paint.Box(200, 120, OnlineShellArt.CardRadius, OnlineShellArt.CardStyle), card.Frame));
        Assert.Equal(new[] { (97, 47), (285, 47), (97, 155), (285, 155) }, Enumerable.Range(0, 4).Select(i => (One(sprites, "c-corner-" + i).ScreenX, One(sprites, "c-corner-" + i).ScreenY)).ToArray());
        Assert.True(sprites.Select(s => s.SortKey).SequenceEqual(Enumerable.Range(0, 6)));
    }

    [Fact]
    public void The_card_is_glass_over_the_background_but_still_dark_enough_to_read_on()
    {
        ShellBox style = OnlineShellArt.CardStyle;

        Assert.True((style.Top >> 24) >= 0xE0 && (style.Bottom >> 24) >= 0xE0);
        Assert.True(((style.Bottom >> 16) & 0xFF) < 0x20 && ((style.Bottom >> 8) & 0xFF) < 0x30);
        Assert.True(style.EdgeWidth >= 1);
        Assert.Equal(OnlineShellArt.ScreenWidth, 800);
        Assert.Equal(OnlineShellArt.ScreenHeight, 600);
    }

    /// <summary>只有一个可以设排序起点的假画面:返回的精灵从 <c>first</c> 开始编号,用来验证叠层排在里面那张画面的上面。</summary>
    private sealed class StubScene : ISceneSource
    {
        private readonly int _first;

        public StubScene(int first)
        {
            _first = first;
        }

        public List<SceneSprite> Build(long timeMs)
        {
            SpriteFrame frame = new OverlayArt().Solid(2, 2, 0x112233);
            return new List<SceneSprite> { OverlayArt.Place(frame, 0, 0, _first, "stub") };
        }
    }
}
