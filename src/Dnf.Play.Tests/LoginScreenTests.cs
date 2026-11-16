using System.Security.Cryptography;
using System.Text;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 登录界面:输入(打字、退格、字数上限、Tab 循环、回车、鼠标点输入框与按钮)、在途时的忽略、密码掩码(只画圆点、从不画密码)、
/// 提示与倒计时的显示、中文标签与英文占位、背景。全部用合成的图包与字体。
/// </summary>
public class LoginScreenTests
{
    private static HitRect Rect(LoginButton b) => LoginScreen.ButtonRect(b);

    private static void Click(LoginScreen screen, HitRect r) => screen.Click(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static List<SceneSprite> With(List<SceneSprite> sprites, string tag) => sprites.Where(s => s.AnimationPath == tag).ToList();

    private static SceneSprite One(List<SceneSprite> sprites, string tag) => Assert.Single(sprites, s => s.AnimationPath == tag);

    private static string[] Tags(List<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToArray();

    private static bool HasColor(SpriteFrame frame, uint rgb)
    {
        byte r = (byte)((rgb >> 16) & 0xFF), g = (byte)((rgb >> 8) & 0xFF), b = (byte)(rgb & 0xFF);
        for (int i = 0; i < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i] == r && frame.Rgba[i + 1] == g && frame.Rgba[i + 2] == b && frame.Rgba[i + 3] == 255)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>整张画面的指纹:每个精灵的标签、位置、尺寸与像素内容(用来证明"画出来的东西与密码内容无关")。</summary>
    private static string Signature(List<SceneSprite> sprites)
    {
        var sb = new StringBuilder();
        foreach (SceneSprite s in sprites)
        {
            sb.Append(s.AnimationPath).Append('@').Append(s.ScreenX).Append(',').Append(s.ScreenY).Append(':').Append(s.Frame.Width).Append('x').Append(s.Frame.Height);
            sb.Append('#').Append(Convert.ToHexString(SHA256.HashData(s.Frame.Rgba))).Append('\n');
        }
        return sb.ToString();
    }

    private static LoginScreen Typed(string username, string password)
    {
        var screen = new LoginScreen();
        screen.Type(username);
        screen.Tab();
        screen.Type(password);
        return screen;
    }

    // ------------------------------------------------------------------------------------------------ 打字

    [Fact]
    public void A_fresh_screen_is_empty_in_login_mode_with_the_username_focused()
    {
        var screen = new LoginScreen();

        Assert.Equal(string.Empty, screen.Username);
        Assert.Equal(string.Empty, screen.Password);
        Assert.Equal(string.Empty, screen.NewPassword);
        Assert.Equal(LoginMode.Login, screen.Mode);
        Assert.Equal(LoginField.Username, screen.Focus);
        Assert.False(screen.Busy);
        Assert.Equal(LoginAction.None, screen.PendingAction);
        Assert.Null(screen.Notice);
    }

    [Fact]
    public void Typing_goes_to_the_focused_field_only()
    {
        var screen = new LoginScreen();

        screen.Type("player");
        screen.Type("_01");
        screen.Tab();
        screen.Type("secret");

        Assert.Equal("player_01", screen.Username);
        Assert.Equal("secret", screen.Password);
        Assert.Equal(string.Empty, screen.NewPassword);
    }

    [Fact]
    public void Backspace_in_the_text_deletes_the_last_char_and_the_method_does_the_same_without_failing_on_empty()
    {
        var screen = new LoginScreen();
        screen.Type("abc\bd");
        Assert.Equal("abd", screen.Username);

        screen.Backspace();
        Assert.Equal("ab", screen.Username);

        screen.Type("\b\b\b\b");
        Assert.Equal(string.Empty, screen.Username);
        screen.Backspace();
        Assert.Equal(string.Empty, screen.Username);
    }

    [Fact]
    public void Backspace_edits_the_focused_field_not_the_others()
    {
        var screen = Typed("player_01", "secret");

        screen.Backspace();

        Assert.Equal("player_01", screen.Username);
        Assert.Equal("secre", screen.Password);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u001b")]
    [InlineData("\u0000")]
    [InlineData("\u007f")]
    public void Control_characters_are_not_typed(string control)
    {
        var screen = new LoginScreen();

        screen.Type("ab" + control + "cd");

        Assert.Equal("abcd", screen.Username);
    }

    [Fact]
    public void Space_and_non_ascii_characters_are_typed_and_left_to_the_validation_to_reject()
    {
        var screen = new LoginScreen();

        screen.Type("a b名");

        Assert.Equal("a b名", screen.Username);
    }

    [Fact]
    public void Null_and_empty_text_do_nothing()
    {
        var screen = new LoginScreen();

        screen.Type(null);
        screen.Type(string.Empty);

        Assert.Equal(string.Empty, screen.Username);
    }

    [Fact]
    public void The_username_is_capped_at_32_and_the_password_at_64()
    {
        Assert.Equal(32, LoginScreen.MaxUsernameLength);
        Assert.Equal(64, LoginScreen.MaxPasswordLength);
        var screen = new LoginScreen();

        screen.Type(new string('u', 40));
        screen.Tab();
        screen.Type(new string('p', 70));

        Assert.Equal(new string('u', 32), screen.Username);
        Assert.Equal(new string('p', 64), screen.Password);
        screen.Type("zzz");
        Assert.Equal(new string('p', 64), screen.Password);
        screen.Backspace();
        screen.Type("zzz");
        Assert.Equal(new string('p', 63) + "z", screen.Password);
    }

    [Fact]
    public void The_new_password_is_capped_at_64_too()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        screen.Tab();

        screen.Type(new string('n', 80));

        Assert.Equal(LoginField.NewPassword, screen.Focus);
        Assert.Equal(new string('n', 64), screen.NewPassword);
        Assert.Equal(string.Empty, screen.Password);
    }

    // ------------------------------------------------------------------------------------------------ 焦点

    [Fact]
    public void Tab_cycles_two_fields_in_login_mode_and_three_in_change_password_mode_in_both_directions()
    {
        var screen = new LoginScreen();
        Assert.Equal(LoginField.Username, screen.Focus);
        screen.Tab();
        Assert.Equal(LoginField.Password, screen.Focus);
        screen.Tab();
        Assert.Equal(LoginField.Username, screen.Focus);
        screen.Tab(true);
        Assert.Equal(LoginField.Password, screen.Focus);
        screen.Tab(true);
        Assert.Equal(LoginField.Username, screen.Focus);

        screen.SetMode(LoginMode.ChangePassword);
        Assert.Equal(LoginField.Password, screen.Focus);
        screen.Tab();
        Assert.Equal(LoginField.NewPassword, screen.Focus);
        screen.Tab();
        Assert.Equal(LoginField.Username, screen.Focus);
        screen.Tab();
        Assert.Equal(LoginField.Password, screen.Focus);
        screen.Tab(true);
        Assert.Equal(LoginField.Username, screen.Focus);
        screen.Tab(true);
        Assert.Equal(LoginField.NewPassword, screen.Focus);
        screen.Tab(true);
        Assert.Equal(LoginField.Password, screen.Focus);
    }

    [Fact]
    public void Clicking_a_field_focuses_it_and_clicking_the_third_field_only_works_in_change_password_mode()
    {
        var screen = new LoginScreen();

        Assert.True(screen.Click(LoginScreen.FieldRect(LoginField.Password).X + 3, LoginScreen.FieldRect(LoginField.Password).Y + 3));
        Assert.Equal(LoginField.Password, screen.Focus);
        Assert.True(screen.Click(LoginScreen.FieldRect(LoginField.Username).X + 3, LoginScreen.FieldRect(LoginField.Username).Y + 3));
        Assert.Equal(LoginField.Username, screen.Focus);
        HitRect third = LoginScreen.FieldRect(LoginField.NewPassword);
        Assert.False(screen.Click(third.X + 3, third.Y + 3));
        Assert.Equal(LoginField.Username, screen.Focus);

        screen.SetMode(LoginMode.ChangePassword);
        Assert.True(screen.Click(third.X + 3, third.Y + 3));
        Assert.Equal(LoginField.NewPassword, screen.Focus);
    }

    [Fact]
    public void Clicking_outside_everything_changes_nothing_and_reports_a_miss()
    {
        var screen = Typed("player_01", "secret");

        Assert.False(screen.Click(5, 5));
        Assert.False(screen.Click(LoginScreen.FieldX - 1, LoginScreen.FieldRect(LoginField.Username).Y + 3));
        Assert.Equal(LoginField.Password, screen.Focus);
        Assert.Equal(LoginAction.None, screen.PendingAction);
    }

    [Fact]
    public void Hit_testing_is_inclusive_at_the_top_left_and_exclusive_at_the_bottom_right()
    {
        HitRect login = Rect(LoginButton.Login);

        Assert.Equal(LoginButton.Login, LoginScreen.HitButton(LoginMode.Login, login.X, login.Y));
        Assert.Equal(LoginButton.Login, LoginScreen.HitButton(LoginMode.Login, login.X + login.Width - 1, login.Y + login.Height - 1));
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.Login, login.X - 1, login.Y));
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.Login, login.X + login.Width, login.Y));
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.Login, login.X, login.Y - 1));
        HitRect box = LoginScreen.FieldRect(LoginField.Username);
        Assert.True(LoginScreen.HitField(LoginMode.Login, box.X, box.Y, out LoginField field));
        Assert.Equal(LoginField.Username, field);
        Assert.False(LoginScreen.HitField(LoginMode.Login, box.X + box.Width, box.Y, out _));
        Assert.False(LoginScreen.HitField(LoginMode.Login, box.X, box.Y + box.Height, out _));
    }

    [Fact]
    public void Buttons_belong_to_their_mode()
    {
        HitRect login = Rect(LoginButton.Login);
        HitRect register = Rect(LoginButton.Register);
        HitRect open = Rect(LoginButton.OpenChangePassword);
        HitRect confirm = Rect(LoginButton.Confirm);
        HitRect back = Rect(LoginButton.Back);

        Assert.Equal(LoginButton.Login, LoginScreen.HitButton(LoginMode.Login, login.X + 5, login.Y + 5));
        Assert.Equal(LoginButton.Register, LoginScreen.HitButton(LoginMode.Login, register.X + 5, register.Y + 5));
        Assert.Equal(LoginButton.OpenChangePassword, LoginScreen.HitButton(LoginMode.Login, open.X + 5, open.Y + 5));
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.Login, confirm.X + 5, confirm.Y + confirm.Height - 5));
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.Login, back.X + 5, back.Y + back.Height - 5));

        Assert.Equal(LoginButton.Confirm, LoginScreen.HitButton(LoginMode.ChangePassword, confirm.X + 5, confirm.Y + 5));
        Assert.Equal(LoginButton.Back, LoginScreen.HitButton(LoginMode.ChangePassword, back.X + 5, back.Y + 5));
        // 改密码那一页没有登录页的三个按钮:点在它们的位置上,只会点到(恰好叠在那里的)确认或返回,不会点到登录、注册、修改密码
        foreach (HitRect r in new[] { login, register, open })
        {
            foreach ((int x, int y) in new[] { (r.X + 5, r.Y + 5), (r.X + r.Width / 2, r.Y + r.Height / 2), (r.X + r.Width - 5, r.Y + r.Height - 5) })
            {
                LoginButton expected = confirm.Contains(x, y) ? LoginButton.Confirm : (back.Contains(x, y) ? LoginButton.Back : LoginButton.None);
                Assert.Equal(expected, LoginScreen.HitButton(LoginMode.ChangePassword, x, y));
            }
        }
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.ChangePassword, login.X + 5, login.Y + 5));
        Assert.Equal(new HitRect(0, 0, 0, 0), Rect(LoginButton.None));
    }

    [Fact]
    public void Buttons_and_fields_do_not_overlap_each_other_inside_the_panel()
    {
        LoginButton[] loginButtons = { LoginButton.Login, LoginButton.Register, LoginButton.OpenChangePassword };
        for (int i = 0; i < loginButtons.Length; i++)
        {
            for (int j = i + 1; j < loginButtons.Length; j++)
            {
                Assert.False(Overlaps(Rect(loginButtons[i]), Rect(loginButtons[j])));
            }
        }
        Assert.False(Overlaps(Rect(LoginButton.Confirm), Rect(LoginButton.Back)));
        foreach (LoginField f in new[] { LoginField.Username, LoginField.Password, LoginField.NewPassword })
        {
            foreach (LoginButton b in loginButtons.Concat(new[] { LoginButton.Confirm, LoginButton.Back }))
            {
                Assert.False(Overlaps(LoginScreen.FieldRect(f), Rect(b)));
            }
        }
        Assert.False(Overlaps(LoginScreen.FieldRect(LoginField.Username), LoginScreen.FieldRect(LoginField.Password)));
        Assert.False(Overlaps(LoginScreen.FieldRect(LoginField.Password), LoginScreen.FieldRect(LoginField.NewPassword)));
        foreach (LoginButton b in loginButtons)
        {
            HitRect r = Rect(b);
            Assert.InRange(r.X, LoginScreen.PanelX, LoginScreen.PanelX + LoginScreen.PanelWidth);
            Assert.InRange(r.X + r.Width, LoginScreen.PanelX, LoginScreen.PanelX + LoginScreen.PanelWidth);
            Assert.InRange(r.Y + r.Height, LoginScreen.PanelY, LoginScreen.PanelY + LoginScreen.PanelHeightOf(LoginMode.Login));
        }
        foreach (LoginButton b in new[] { LoginButton.Confirm, LoginButton.Back })
        {
            HitRect r = Rect(b);
            Assert.InRange(r.X, LoginScreen.PanelX, LoginScreen.PanelX + LoginScreen.PanelWidth);
            Assert.InRange(r.X + r.Width, LoginScreen.PanelX, LoginScreen.PanelX + LoginScreen.PanelWidth);
            Assert.InRange(r.Y + r.Height, LoginScreen.PanelY, LoginScreen.PanelY + LoginScreen.PanelHeightOf(LoginMode.ChangePassword));
        }
    }

    private static bool Overlaps(HitRect a, HitRect b)
    {
        return a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    }

    // ------------------------------------------------------------------------------------------------ 提交

    [Fact]
    public void Enter_queues_login_in_login_mode_and_change_password_in_the_other_mode_and_take_clears_it()
    {
        var screen = new LoginScreen();
        screen.Enter();
        Assert.Equal(LoginAction.Login, screen.PendingAction);
        Assert.Equal(LoginAction.Login, screen.TakeAction());
        Assert.Equal(LoginAction.None, screen.PendingAction);
        Assert.Equal(LoginAction.None, screen.TakeAction());

        screen.SetMode(LoginMode.ChangePassword);
        screen.Enter();
        Assert.Equal(LoginAction.ChangePassword, screen.TakeAction());
    }

    [Fact]
    public void A_second_submit_before_the_first_is_taken_does_not_replace_it()
    {
        var screen = new LoginScreen();

        Click(screen, Rect(LoginButton.Register));
        screen.Enter();
        Click(screen, Rect(LoginButton.Login));

        Assert.Equal(LoginAction.Register, screen.TakeAction());
        Assert.Equal(LoginAction.None, screen.TakeAction());
    }

    [Fact]
    public void Clicking_the_buttons_queues_their_action_or_switches_the_mode()
    {
        var screen = new LoginScreen();
        Click(screen, Rect(LoginButton.Login));
        Assert.Equal(LoginAction.Login, screen.TakeAction());
        Click(screen, Rect(LoginButton.Register));
        Assert.Equal(LoginAction.Register, screen.TakeAction());

        Click(screen, Rect(LoginButton.OpenChangePassword));
        Assert.Equal(LoginMode.ChangePassword, screen.Mode);
        Assert.Equal(LoginAction.None, screen.PendingAction);
        Assert.Equal(LoginField.Password, screen.Focus);

        Click(screen, Rect(LoginButton.Confirm));
        Assert.Equal(LoginAction.ChangePassword, screen.TakeAction());

        Click(screen, Rect(LoginButton.Back));
        Assert.Equal(LoginMode.Login, screen.Mode);
        Assert.Equal(LoginAction.None, screen.PendingAction);
    }

    [Fact]
    public void Clicking_a_button_reports_a_hit_even_when_it_is_ignored()
    {
        var screen = new LoginScreen();
        screen.SetBusy(true);
        HitRect login = Rect(LoginButton.Login);

        Assert.True(screen.Click(login.X + 2, login.Y + 2));
        Assert.Equal(LoginAction.None, screen.PendingAction);
    }

    [Fact]
    public void While_busy_every_submit_path_is_ignored_but_typing_and_focus_still_work()
    {
        var screen = Typed("player_01", "secret");
        screen.SetBusy(true);
        Assert.True(screen.Busy);

        screen.Enter();
        Click(screen, Rect(LoginButton.Login));
        Click(screen, Rect(LoginButton.Register));
        Click(screen, Rect(LoginButton.OpenChangePassword));
        Assert.Equal(LoginAction.None, screen.PendingAction);
        Assert.Equal(LoginMode.Login, screen.Mode);

        screen.Type("x");
        screen.Tab();
        Assert.Equal("secretx", screen.Password);
        Assert.Equal(LoginField.Username, screen.Focus);

        screen.SetBusy(false);
        screen.Enter();
        Assert.Equal(LoginAction.Login, screen.TakeAction());
    }

    [Fact]
    public void While_busy_the_change_password_buttons_are_ignored_too()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        screen.SetBusy(true);

        Click(screen, Rect(LoginButton.Confirm));
        Click(screen, Rect(LoginButton.Back));
        screen.Enter();

        Assert.Equal(LoginAction.None, screen.PendingAction);
        Assert.Equal(LoginMode.ChangePassword, screen.Mode);
    }

    // ------------------------------------------------------------------------------------------------ 模式、预填、清密码

    [Fact]
    public void Leaving_change_password_mode_clears_the_new_password_and_fixes_the_focus()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        screen.Type("old");
        screen.Tab();
        screen.Type("new");
        Assert.Equal(LoginField.NewPassword, screen.Focus);

        screen.SetMode(LoginMode.Login);

        Assert.Equal(string.Empty, screen.NewPassword);
        Assert.Equal("old", screen.Password);
        Assert.Equal(LoginField.Password, screen.Focus);
    }

    [Fact]
    public void Leaving_change_password_mode_keeps_a_username_focus()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        screen.Tab();
        screen.Tab();
        Assert.Equal(LoginField.Username, screen.Focus);

        screen.SetMode(LoginMode.Login);

        Assert.Equal(LoginField.Username, screen.Focus);
    }

    [Fact]
    public void Setting_the_same_mode_changes_nothing()
    {
        var screen = new LoginScreen();
        screen.Type("abc");

        screen.SetMode(LoginMode.Login);

        Assert.Equal(LoginField.Username, screen.Focus);
        Assert.Equal("abc", screen.Username);
    }

    [Fact]
    public void Entering_change_password_mode_focuses_the_old_password_field_and_keeps_the_typed_values()
    {
        var screen = Typed("player_01", "secret");

        screen.SetMode(LoginMode.ChangePassword);

        Assert.Equal(LoginField.Password, screen.Focus);
        Assert.Equal("player_01", screen.Username);
        Assert.Equal("secret", screen.Password);
    }

    [Fact]
    public void Prefilling_sets_the_account_and_focuses_the_password_and_both_values_are_clipped()
    {
        var screen = new LoginScreen();

        screen.SetUsername(new string('a', 40));
        Assert.Equal(new string('a', 32), screen.Username);
        Assert.Equal(LoginField.Password, screen.Focus);

        screen.SetUsername(null);
        Assert.Equal(string.Empty, screen.Username);

        screen.Prefill(new string('b', 40), new string('c', 70));
        Assert.Equal(new string('b', 32), screen.Username);
        Assert.Equal(new string('c', 64), screen.Password);
        Assert.Equal(LoginField.Password, screen.Focus);

        screen.Prefill(null, null);
        Assert.Equal(string.Empty, screen.Username);
        Assert.Equal(string.Empty, screen.Password);
    }

    [Fact]
    public void Clearing_secrets_keeps_the_account()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        screen.Type("player_01");
        screen.Prefill("player_01", "old");
        screen.Tab();
        screen.Type("new");

        screen.ClearSecrets();

        Assert.Equal("player_01", screen.Username);
        Assert.Equal(string.Empty, screen.Password);
        Assert.Equal(string.Empty, screen.NewPassword);
    }

    [Fact]
    public void The_notice_is_stored_and_cleared()
    {
        var screen = new LoginScreen();
        var notice = new OnlineNotice(OnlineNoticeKind.WrongCredentials);

        screen.SetNotice(notice);
        Assert.Same(notice, screen.Notice);
        screen.SetNotice(null);
        Assert.Null(screen.Notice);
    }

    // ------------------------------------------------------------------------------------------------ 画面:结构

    private static SpriteFrame Expect(string text, int scale, uint fill, uint edge = ShellPaint.Navy) => new OverlayArt().Text(text, scale, fill, edge);

    private static bool SameFrame(SpriteFrame a, SpriteFrame b) => ShellFrames.Same(a, b);

    private static readonly string[] BackdropParts = { "online-backdrop", "online-vignette" };

    private static readonly string[] BannerParts = { "login-banner-glow", "login-banner", "login-banner-rule", "login-banner-gem" };

    private static readonly string[] PanelParts =
    {
        "login-panel-shadow", "login-panel", "login-panel-corner-0", "login-panel-corner-1", "login-panel-corner-2", "login-panel-corner-3",
    };

    private static readonly string[] HeaderParts = { "login-title-bar", "login-divider", "login-title", "login-subtitle" };

    private static string[] Parts(params string[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void The_login_form_has_exactly_these_parts_in_this_order_without_a_background_image()
    {
        var screen = new LoginScreen();

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(Parts(
            BackdropParts, BannerParts, PanelParts, HeaderParts,
            new[]
            {
                "login-focus-username", "login-box-username", "login-cell-username", "login-label-username", "login-placeholder-username", "login-caret",
                "login-box-password", "login-cell-password", "login-label-password", "login-placeholder-password",
                "login-rule",
                "login-button-shadow-login", "login-button-login", "login-button-text-login",
                "login-button-register", "login-button-text-register",
                "login-button-change-password", "login-button-text-change-password",
                "login-hint-key-0", "login-hint-keytext-0", "login-hint-text-0",
                "login-hint-key-1", "login-hint-keytext-1", "login-hint-text-1",
            }), Tags(sprites));
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(sprites.Select(s => s.SortKey).OrderBy(k => k).Distinct().ToArray(), sprites.Select(s => s.SortKey).ToArray());
    }

    [Fact]
    public void The_change_password_form_has_the_third_field_and_its_own_buttons_and_hints()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(Parts(
            BackdropParts, BannerParts, PanelParts, HeaderParts,
            new[]
            {
                "login-box-username", "login-cell-username", "login-label-username", "login-placeholder-username",
                "login-focus-password", "login-box-password", "login-cell-password", "login-label-password", "login-placeholder-password", "login-caret",
                "login-box-new-password", "login-cell-new-password", "login-label-new-password", "login-placeholder-new-password",
                "login-rule",
                "login-button-shadow-confirm", "login-button-confirm", "login-button-text-confirm",
                "login-button-back", "login-button-text-back",
                "login-hint-key-0", "login-hint-keytext-0", "login-hint-text-0",
                "login-hint-key-1", "login-hint-keytext-1", "login-hint-text-1",
                "login-hint-key-2", "login-hint-keytext-2", "login-hint-text-2",
            }), Tags(sprites));
        Assert.Equal(OverlayArt.TextWidth("CHANGE PASSWORD", 2), One(sprites, "login-title").Frame.Width);
    }

    [Fact]
    public void The_card_is_taller_in_change_password_mode_and_everything_fits_inside_it_above_the_hints()
    {
        Assert.True(LoginScreen.PanelHeightOf(LoginMode.ChangePassword) > LoginScreen.PanelHeightOf(LoginMode.Login));
        foreach (LoginMode mode in new[] { LoginMode.Login, LoginMode.ChangePassword })
        {
            int bottom = LoginScreen.PanelY + LoginScreen.PanelHeightOf(mode);
            HitRect notice = LoginScreen.NoticeRect(mode);
            Assert.True(notice.Y + notice.Height <= bottom);
            Assert.True(bottom + 16 <= OnlineShellArt.HintTop, "卡片底边与按键说明之间要留空");
            Assert.True(LoginScreen.PanelY + LoginScreen.PanelHeightOf(mode) <= LoginScreen.ScreenHeight);
        }
    }

    [Fact]
    public void Panel_boxes_and_buttons_are_drawn_where_they_are_hit_tested()
    {
        var screen = new LoginScreen();
        List<SceneSprite> sprites = screen.Build(0);

        SceneSprite panel = One(sprites, "login-panel");
        Assert.Equal((LoginScreen.PanelX, LoginScreen.PanelY, LoginScreen.PanelWidth, LoginScreen.PanelHeightOf(LoginMode.Login)),
            (panel.ScreenX, panel.ScreenY, panel.Frame.Width, panel.Frame.Height));
        SceneSprite shadow = One(sprites, "login-panel-shadow");
        Assert.Equal((LoginScreen.PanelX - 26, LoginScreen.PanelY - 14, LoginScreen.PanelWidth + 52, LoginScreen.PanelHeightOf(LoginMode.Login) + 52),
            (shadow.ScreenX, shadow.ScreenY, shadow.Frame.Width, shadow.Frame.Height));
        foreach (LoginField f in new[] { LoginField.Username, LoginField.Password })
        {
            HitRect r = LoginScreen.FieldRect(f);
            string name = f == LoginField.Username ? "username" : "password";
            SceneSprite box = One(sprites, "login-box-" + name);
            Assert.Equal((r.X, r.Y, r.Width, r.Height), (box.ScreenX, box.ScreenY, box.Frame.Width, box.Frame.Height));
            SceneSprite cell = One(sprites, "login-cell-" + name);
            Assert.Equal((r.X + 1, r.Y + 1, LoginScreen.LabelCellWidth, r.Height - 2), (cell.ScreenX, cell.ScreenY, cell.Frame.Width, cell.Frame.Height));
            SceneSprite label = One(sprites, "login-label-" + name);
            Assert.Equal(r.X + (LoginScreen.LabelCellWidth - label.Frame.Width) / 2, label.ScreenX);
            Assert.Equal(r.Y + (r.Height - label.Frame.Height) / 2, label.ScreenY);
        }
        foreach ((LoginButton b, string tag) in new[] { (LoginButton.Login, "login"), (LoginButton.Register, "register"), (LoginButton.OpenChangePassword, "change-password") })
        {
            HitRect r = Rect(b);
            SceneSprite button = One(sprites, "login-button-" + tag);
            Assert.Equal((r.X, r.Y, r.Width, r.Height), (button.ScreenX, button.ScreenY, button.Frame.Width, button.Frame.Height));
            SceneSprite text = One(sprites, "login-button-text-" + tag);
            Assert.Equal(r.X + (r.Width - text.Frame.Width) / 2, text.ScreenX);
            Assert.Equal(r.Y + (r.Height - text.Frame.Height) / 2, text.ScreenY);
        }
        HitRect primary = Rect(LoginButton.Login);
        SceneSprite push = One(sprites, "login-button-shadow-login");
        Assert.Equal((primary.X - 10, primary.Y - 5, primary.Width + 20, primary.Height + 20), (push.ScreenX, push.ScreenY, push.Frame.Width, push.Frame.Height));
        Assert.Empty(With(sprites, "login-button-shadow-register"));
    }

    [Fact]
    public void The_change_password_form_draws_its_boxes_and_buttons_where_they_are_hit_tested_too()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(LoginScreen.PanelHeightOf(LoginMode.ChangePassword), One(sprites, "login-panel").Frame.Height);
        foreach ((LoginField f, string name) in new[] { (LoginField.Username, "username"), (LoginField.Password, "password"), (LoginField.NewPassword, "new-password") })
        {
            HitRect r = LoginScreen.FieldRect(f);
            SceneSprite box = One(sprites, "login-box-" + name);
            Assert.Equal((r.X, r.Y, r.Width, r.Height), (box.ScreenX, box.ScreenY, box.Frame.Width, box.Frame.Height));
        }
        foreach ((LoginButton b, string tag) in new[] { (LoginButton.Confirm, "confirm"), (LoginButton.Back, "back") })
        {
            HitRect r = Rect(b);
            SceneSprite button = One(sprites, "login-button-" + tag);
            Assert.Equal((r.X, r.Y, r.Width, r.Height), (button.ScreenX, button.ScreenY, button.Frame.Width, button.Frame.Height));
        }
    }

    [Fact]
    public void The_four_gold_corner_brackets_sit_on_the_corners_of_the_card()
    {
        var screen = new LoginScreen();
        List<SceneSprite> sprites = screen.Build(0);
        int left = LoginScreen.PanelX, top = LoginScreen.PanelY;
        int right = left + LoginScreen.PanelWidth, bottom = top + LoginScreen.PanelHeightOf(LoginMode.Login);

        Assert.Equal((left - 3, top - 3), (One(sprites, "login-panel-corner-0").ScreenX, One(sprites, "login-panel-corner-0").ScreenY));
        Assert.Equal((right - 18 + 3, top - 3), (One(sprites, "login-panel-corner-1").ScreenX, One(sprites, "login-panel-corner-1").ScreenY));
        Assert.Equal((left - 3, bottom - 18 + 3), (One(sprites, "login-panel-corner-2").ScreenX, One(sprites, "login-panel-corner-2").ScreenY));
        Assert.Equal((right - 18 + 3, bottom - 18 + 3), (One(sprites, "login-panel-corner-3").ScreenX, One(sprites, "login-panel-corner-3").ScreenY));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((18, 18), (One(sprites, "login-panel-corner-" + i).Frame.Width, One(sprites, "login-panel-corner-" + i).Frame.Height));
            Assert.True(ShellFrames.Inked(One(sprites, "login-panel-corner-" + i).Frame) > 20);
        }
    }

    [Fact]
    public void The_header_has_a_gold_bar_a_title_a_subtitle_and_a_divider_at_fixed_places()
    {
        var screen = new LoginScreen();
        List<SceneSprite> sprites = screen.Build(0);

        SceneSprite bar = One(sprites, "login-title-bar");
        Assert.Equal((LoginScreen.FieldX - 14, LoginScreen.PanelY + 28, 4, 26), (bar.ScreenX, bar.ScreenY, bar.Frame.Width, bar.Frame.Height));
        SceneSprite divider = One(sprites, "login-divider");
        Assert.Equal((LoginScreen.FieldX, LoginScreen.PanelY + 90, LoginScreen.FieldWidth, 1), (divider.ScreenX, divider.ScreenY, divider.Frame.Width, divider.Frame.Height));
        Assert.Equal((LoginScreen.FieldX, LoginScreen.PanelY + 24), (One(sprites, "login-title").ScreenX, One(sprites, "login-title").ScreenY));
        Assert.Equal((LoginScreen.FieldX, LoginScreen.PanelY + 62), (One(sprites, "login-subtitle").ScreenX, One(sprites, "login-subtitle").ScreenY));
        Assert.True(One(sprites, "login-title").ScreenY + One(sprites, "login-title").Frame.Height <= One(sprites, "login-subtitle").ScreenY);
        Assert.True(One(sprites, "login-subtitle").ScreenY + One(sprites, "login-subtitle").Frame.Height <= divider.ScreenY);
        Assert.True(divider.ScreenY < LoginScreen.FieldRect(LoginField.Username).Y);
    }

    [Fact]
    public void The_game_title_sits_centred_in_its_band_above_the_card_with_a_rule_and_a_gem_under_it()
    {
        var screen = new LoginScreen();
        List<SceneSprite> sprites = screen.Build(0);

        SceneSprite banner = One(sprites, "login-banner");
        Assert.True(Math.Abs(banner.ScreenX + banner.Frame.Width / 2 - LoginScreen.ScreenWidth / 2) <= 1);
        Assert.True(banner.ScreenY >= 0);
        SceneSprite rule = One(sprites, "login-banner-rule");
        Assert.Equal((LoginScreen.ScreenWidth / 2 - 150, 300, 2), (rule.ScreenX, rule.Frame.Width, rule.Frame.Height));
        Assert.True(banner.ScreenY + banner.Frame.Height <= rule.ScreenY + rule.Frame.Height);
        Assert.True(rule.ScreenY + rule.Frame.Height < LoginScreen.PanelY);
        SceneSprite gem = One(sprites, "login-banner-gem");
        Assert.Equal((LoginScreen.ScreenWidth / 2 - 5, 10, 10), (gem.ScreenX, gem.Frame.Width, gem.Frame.Height));
        Assert.True(gem.ScreenY < rule.ScreenY && gem.ScreenY + gem.Frame.Height > rule.ScreenY);        // 菱形压在金线中间
        SceneSprite glow = One(sprites, "login-banner-glow");
        Assert.True(glow.SortKey < banner.SortKey);                                                      // 光晕垫在字下面
    }

    [Fact]
    public void Only_the_focused_field_has_the_gold_glow_the_gold_edge_and_the_gold_label()
    {
        static bool Gold((int R, int G, int B, int A) p) => p.R > 0xF0 && p.G > 0xB0 && p.B < 0x80;
        static (int R, int G, int B, int A) Top(SpriteFrame f) => ShellFrames.Pixel(f, f.Width / 2, 0);
        var screen = new LoginScreen();

        List<SceneSprite> first = screen.Build(0);
        Assert.Single(With(first, "login-focus-username"));
        Assert.Empty(With(first, "login-focus-password"));
        Assert.True(Gold(Top(One(first, "login-box-username").Frame)));
        Assert.False(Gold(Top(One(first, "login-box-password").Frame)));
        Assert.True(ShellFrames.HasColor(One(first, "login-label-username").Frame, ShellPaint.Gold));
        Assert.False(ShellFrames.HasColor(One(first, "login-label-password").Frame, ShellPaint.Gold));
        Assert.True(ShellFrames.HasColor(One(first, "login-label-password").Frame, ShellPaint.Muted));
        Assert.False(SameFrame(One(first, "login-cell-username").Frame, One(first, "login-cell-password").Frame));

        screen.Tab();
        List<SceneSprite> second = screen.Build(0);
        Assert.Empty(With(second, "login-focus-username"));
        Assert.Single(With(second, "login-focus-password"));
        Assert.False(Gold(Top(One(second, "login-box-username").Frame)));
        Assert.True(Gold(Top(One(second, "login-box-password").Frame)));
        Assert.True(ShellFrames.HasColor(One(second, "login-label-password").Frame, ShellPaint.Gold));
    }

    [Fact]
    public void The_focus_glow_surrounds_the_focused_box_by_eight_pixels()
    {
        var screen = new LoginScreen();
        screen.Tab();

        SceneSprite glow = One(screen.Build(0), "login-focus-password");

        HitRect box = LoginScreen.FieldRect(LoginField.Password);
        Assert.Equal((box.X - 8, box.Y - 8, box.Width + 16, box.Height + 16), (glow.ScreenX, glow.ScreenY, glow.Frame.Width, glow.Frame.Height));
    }

    [Fact]
    public void Switching_to_change_password_mode_focuses_the_old_password_field_glow()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Single(With(sprites, "login-focus-password"));
        Assert.Empty(With(sprites, "login-focus-username"));
        Assert.Empty(With(sprites, "login-focus-new-password"));
    }

    // ------------------------------------------------------------------------------------------------ 画面:背景

    [Fact]
    public void Without_a_sprite_source_or_without_the_frame_the_background_is_a_solid_colour()
    {
        var empty = new ShellFakeSprites();
        foreach (LoginScreen screen in new[] { new LoginScreen(), new LoginScreen(empty) })
        {
            SceneSprite bg = screen.Build(0)[0];
            Assert.Equal("online-backdrop", bg.AnimationPath);
            Assert.Equal((0, 0, 800, 600), (bg.ScreenX, bg.ScreenY, bg.Frame.Width, bg.Frame.Height));
            Assert.True(HasColor(bg.Frame, CharacterSelectArt.FallbackBackdrop));
        }
    }

    [Fact]
    public void With_the_character_select_background_frame_it_is_placed_at_the_frame_offset_and_is_the_first_sprite()
    {
        var sprites = new ShellFakeSprites();
        SpriteFrame frame = sprites.AddBackground(7, 3, 5);
        var screen = new LoginScreen(sprites);

        List<SceneSprite> scene = screen.Build(0);

        SceneSprite bg = scene[0];
        Assert.Equal("online-background", bg.AnimationPath);
        Assert.Same(frame, bg.Frame);
        Assert.Equal((3, 5), (bg.ScreenX, bg.ScreenY));
        Assert.Equal(OverlayArt.UiRank, bg.LayerRank);
        Assert.DoesNotContain("online-backdrop", Tags(scene));
        Assert.True(scene.Skip(1).All(s => s.SortKey > bg.SortKey));
    }

    [Fact]
    public void The_vignette_covers_the_whole_screen_right_above_the_background()
    {
        var sprites = new ShellFakeSprites();
        sprites.AddBackground();
        foreach (LoginScreen screen in new[] { new LoginScreen(), new LoginScreen(sprites) })
        {
            List<SceneSprite> scene = screen.Build(0);

            SceneSprite vignette = scene[1];
            Assert.Equal("online-vignette", vignette.AnimationPath);
            Assert.Equal((0, 0, 800, 600), (vignette.ScreenX, vignette.ScreenY, vignette.Frame.Width, vignette.Frame.Height));
            Assert.True(vignette.SortKey > scene[0].SortKey);
            Assert.True(scene.Skip(2).All(s => s.SortKey > vignette.SortKey));
        }
    }

    [Fact]
    public void The_background_comes_from_the_character_select_image_and_frame_only()
    {
        var sprites = new ShellFakeSprites();
        sprites.Frames[("sprite/other.img", CharacterSelectArt.BackgroundFrame)] = new SpriteFrame(800, 600, 0, 0, 800, 600, new byte[800 * 600 * 4]);
        sprites.Frames[(CharacterSelectArt.UiImage, CharacterSelectArt.TitleFrame)] = new SpriteFrame(800, 600, 0, 0, 800, 600, new byte[800 * 600 * 4]);

        SceneSprite bg = new LoginScreen(sprites).Build(0)[0];

        Assert.Equal("online-backdrop", bg.AnimationPath);
    }

    [Fact]
    public void An_empty_background_frame_falls_back_to_the_solid_colour()
    {
        var sprites = new ShellFakeSprites();
        sprites.Frames[(CharacterSelectArt.UiImage, CharacterSelectArt.BackgroundFrame)] = new SpriteFrame(0, 0, 0, 0, 0, 0, new byte[0]);

        Assert.Equal("online-backdrop", new LoginScreen(sprites).Build(0)[0].AnimationPath);
    }

    [Fact]
    public void The_background_uses_only_the_already_known_asset_path()
    {
        Assert.Equal("sprite/interface2/selectcharacter/selectcharacter.img", CharacterSelectArt.UiImage);
        Assert.Equal(0, CharacterSelectArt.BackgroundFrame);
    }

    // ------------------------------------------------------------------------------------------------ 画面:密码掩码

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(LoginScreen.MaskCapacity - 1)]
    [InlineData(LoginScreen.MaskCapacity)]
    [InlineData(LoginScreen.MaskCapacity + 1)]
    [InlineData(64)]
    public void The_password_is_drawn_as_one_bullet_per_character_up_to_the_box_width(int length)
    {
        var screen = new LoginScreen();
        screen.Tab();
        screen.Type(new string('a', length));

        List<SceneSprite> masks = With(screen.Build(0), "login-password-mask");

        Assert.Equal(Math.Min(length, LoginScreen.MaskCapacity), masks.Count);
        HitRect box = LoginScreen.FieldRect(LoginField.Password);
        for (int i = 0; i < masks.Count; i++)
        {
            Assert.Equal((LoginScreen.MaskSize, LoginScreen.MaskSize), (masks[i].Frame.Width, masks[i].Frame.Height));
            Assert.Equal(LoginScreen.TextLeft + i * LoginScreen.MaskStep, masks[i].ScreenX);
            Assert.Equal(box.Y + (box.Height - LoginScreen.MaskSize) / 2, masks[i].ScreenY);
            Assert.True(HasColor(masks[i].Frame, ShellPaint.Ink));
            Assert.True(masks[i].ScreenX + LoginScreen.MaskSize <= box.X + box.Width - LoginScreen.TextInset);
        }
    }

    [Fact]
    public void The_mask_capacity_is_what_the_text_area_holds()
    {
        int area = LoginScreen.FieldWidth - LoginScreen.LabelCellWidth - 2 * LoginScreen.TextInset;

        Assert.Equal(LoginScreen.MaskCapacity, area / LoginScreen.MaskStep);
        Assert.True(LoginScreen.MaskCapacity * LoginScreen.MaskStep <= area + (LoginScreen.MaskStep - LoginScreen.MaskSize));
        Assert.Equal(LoginScreen.FieldX + LoginScreen.LabelCellWidth + LoginScreen.TextInset, LoginScreen.TextLeft);
        Assert.True(LoginScreen.MaskCapacity >= 12, "至少能看出十来位密码的长度");
    }

    [Fact]
    public void The_username_field_never_gets_bullets_and_the_new_password_field_does()
    {
        var screen = new LoginScreen();
        screen.Type("player_01");
        screen.SetMode(LoginMode.ChangePassword);
        screen.Type("old");
        screen.Tab();
        screen.Type("newer");

        List<SceneSprite> masks = With(screen.Build(0), "login-password-mask");

        Assert.Equal(3 + 5, masks.Count);
        HitRect second = LoginScreen.FieldRect(LoginField.Password);
        HitRect third = LoginScreen.FieldRect(LoginField.NewPassword);
        int inset = (LoginScreen.FieldHeight - LoginScreen.MaskSize) / 2;
        Assert.Equal(3, masks.Count(m => m.ScreenY == second.Y + inset));
        Assert.Equal(5, masks.Count(m => m.ScreenY == third.Y + inset));
        Assert.DoesNotContain(masks, m => m.ScreenY == LoginScreen.FieldRect(LoginField.Username).Y + inset);
    }

    [Fact]
    public void The_picture_does_not_depend_on_what_the_password_says_only_on_how_long_it_is()
    {
        LoginScreen a = Typed("player_01", "aaaaaa");
        LoginScreen b = Typed("player_01", "Zq9#!k");
        LoginScreen c = Typed("player_01", "Zq9#!k7");

        Assert.Equal(Signature(a.Build(0)), Signature(b.Build(0)));
        Assert.NotEqual(Signature(a.Build(0)), Signature(c.Build(0)));
    }

    [Fact]
    public void The_new_and_old_password_are_masked_the_same_way_regardless_of_content()
    {
        var a = new LoginScreen();
        a.SetMode(LoginMode.ChangePassword);
        a.Type("oldpw1");
        a.Tab();
        a.Type("newpw22");
        var b = new LoginScreen();
        b.SetMode(LoginMode.ChangePassword);
        b.Type("XXXXXX");
        b.Tab();
        b.Type("YYYYYYY");

        Assert.Equal(Signature(a.Build(0)), Signature(b.Build(0)));
    }

    [Fact]
    public void The_picture_with_fonts_does_not_depend_on_the_password_content_either()
    {
        LoginScreen Make(string password)
        {
            var screen = new LoginScreen(null, new SizedFonts());
            screen.Type("player_01");
            screen.Tab();
            screen.Type(password);
            return screen;
        }

        Assert.Equal(Signature(Make("aaaaaa").Build(0)), Signature(Make("Zq9#!k").Build(0)));
        Assert.NotEqual(Signature(Make("aaaaaa").Build(0)), Signature(Make("aaaaaaa").Build(0)));
    }

    [Fact]
    public void The_password_never_reaches_the_text_renderer_even_with_fonts_installed()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);
        screen.Type("player_01");
        screen.Tab();
        screen.Type("zq#k!");
        screen.SetMode(LoginMode.ChangePassword);
        screen.Tab();
        screen.Type("jw~j&");

        screen.Build(0);
        screen.Build(700);

        Assert.True(fonts.Asked("player_01"));            // 账号是数据文字,走字体
        foreach (char secret in "zqk#!jw&~")
        {
            Assert.DoesNotContain((int)secret, fonts.CodePoints);
        }
    }

    [Fact]
    public void The_password_never_appears_in_the_sprites_when_there_are_no_fonts()
    {
        var screen = new LoginScreen();
        screen.Type("player_01");
        screen.Tab();
        screen.Type("abc");

        List<SceneSprite> sprites = screen.Build(0);

        // 账号框里只有账号文字;密码框里除了框、标签格、标签、圆点、占位文字与光标没有别的东西
        HitRect box = LoginScreen.FieldRect(LoginField.Password);
        string[] allowed = { "login-box-password", "login-cell-password", "login-label-password", "login-password-mask", "login-placeholder-password", "login-caret" };
        foreach (SceneSprite s in sprites.Where(s => s.ScreenX >= box.X && s.ScreenX < box.X + box.Width && s.ScreenY >= box.Y && s.ScreenY < box.Y + box.Height))
        {
            Assert.Contains(s.AnimationPath, allowed);
        }
        Assert.Empty(With(sprites, "login-placeholder-password"));            // 打了字就不再画占位文字
    }

    // ------------------------------------------------------------------------------------------------ 画面:账号、占位文字与光标

    [Fact]
    public void Without_fonts_the_username_is_drawn_in_capitals_with_dashes_and_question_marks_for_what_the_bitmap_font_lacks()
    {
        var screen = new LoginScreen();
        screen.Type("player_01");
        Assert.True(SameFrame(Expect("PLAYER-01", 2, ShellPaint.Ink), One(screen.Build(0), "login-username-text").Frame));

        var odd = new LoginScreen();
        odd.Type("名a");
        Assert.True(SameFrame(Expect("?A", 2, ShellPaint.Ink), One(odd.Build(0), "login-username-text").Frame));
        Assert.Equal("名a", odd.Username);               // 只影响显示,输入框里的内容不变
    }

    [Fact]
    public void An_empty_username_draws_no_text_and_the_text_is_vertically_centred_in_the_box()
    {
        var empty = new LoginScreen();
        Assert.Empty(With(empty.Build(0), "login-username-text"));

        var screen = new LoginScreen();
        screen.Type("abcd");
        SceneSprite text = One(screen.Build(0), "login-username-text");
        HitRect box = LoginScreen.FieldRect(LoginField.Username);
        Assert.Equal(LoginScreen.TextLeft, text.ScreenX);
        Assert.Equal(box.Y + (box.Height - text.Frame.Height) / 2, text.ScreenY);
    }

    [Fact]
    public void A_username_longer_than_the_text_area_keeps_its_end_and_the_caret_stays_inside_the_box()
    {
        HitRect box = LoginScreen.FieldRect(LoginField.Username);
        int area = box.Width - LoginScreen.LabelCellWidth - 2 * LoginScreen.TextInset;
        foreach (IGlyphFonts fonts in new IGlyphFonts[] { null, new SizedFonts() })
        {
            var screen = new LoginScreen(null, fonts);
            screen.Type("abcdefghijklmnopqrstuvwxyz012345");                 // 32 个

            List<SceneSprite> sprites = screen.Build(0);

            SceneSprite text = One(sprites, "login-username-text");
            SceneSprite caret = One(sprites, "login-caret");
            Assert.True(text.Frame.Width <= area + 2, "账号文字超出了文字区");
            Assert.True(caret.ScreenX + caret.Frame.Width <= box.X + box.Width);
            Assert.Equal(text.ScreenX + text.Frame.Width + 4, caret.ScreenX);
        }
    }

    [Fact]
    public void The_caret_blinks_every_500_ms_and_sits_after_the_text_or_the_bullets()
    {
        var screen = new LoginScreen();
        screen.Type("abcd");
        Assert.Single(With(screen.Build(0), "login-caret"));
        Assert.Single(With(screen.Build(499), "login-caret"));
        Assert.Empty(With(screen.Build(500), "login-caret"));
        Assert.Empty(With(screen.Build(999), "login-caret"));
        Assert.Single(With(screen.Build(1000), "login-caret"));

        List<SceneSprite> sprites = screen.Build(0);
        SceneSprite text = One(sprites, "login-username-text");
        SceneSprite caret = One(sprites, "login-caret");
        HitRect box = LoginScreen.FieldRect(LoginField.Username);
        Assert.Equal(text.ScreenX + text.Frame.Width + 4, caret.ScreenX);
        Assert.Equal((2, 20), (caret.Frame.Width, caret.Frame.Height));
        Assert.Equal(box.Y + (box.Height - 20) / 2, caret.ScreenY);
        Assert.True(HasColor(caret.Frame, ShellPaint.Ink));

        screen.Tab();
        screen.Type("xyz");
        SceneSprite second = One(screen.Build(0), "login-caret");
        HitRect pw = LoginScreen.FieldRect(LoginField.Password);
        Assert.Equal(LoginScreen.TextLeft + 3 * LoginScreen.MaskStep - (LoginScreen.MaskStep - LoginScreen.MaskSize) + 4, second.ScreenX);
        Assert.Equal(pw.Y + (pw.Height - 20) / 2, second.ScreenY);
    }

    [Fact]
    public void The_caret_after_an_empty_field_sits_at_the_left_of_the_text_area()
    {
        var screen = new LoginScreen();

        SceneSprite caret = One(screen.Build(0), "login-caret");

        Assert.Equal(LoginScreen.TextLeft, caret.ScreenX);
    }

    [Fact]
    public void An_empty_field_shows_a_faint_placeholder_after_the_caret_and_typing_removes_it()
    {
        var screen = new LoginScreen();

        List<SceneSprite> empty = screen.Build(0);

        SceneSprite username = One(empty, "login-placeholder-username");
        Assert.True(SameFrame(Expect("ENTER ACCOUNT", 1, ShellPaint.Faint), username.Frame));
        HitRect box = LoginScreen.FieldRect(LoginField.Username);
        Assert.Equal(LoginScreen.TextLeft + 6, username.ScreenX);
        Assert.Equal(box.Y + (box.Height - username.Frame.Height) / 2, username.ScreenY);
        Assert.True(username.ScreenX > One(empty, "login-caret").ScreenX + 2);                   // 光标在占位文字前面
        Assert.True(SameFrame(Expect("ENTER PASSWORD", 1, ShellPaint.Faint), One(empty, "login-placeholder-password").Frame));

        screen.Type("a");
        Assert.Empty(With(screen.Build(0), "login-placeholder-username"));
        Assert.Single(With(screen.Build(0), "login-placeholder-password"));
        screen.Tab();
        screen.Type("b");
        Assert.Empty(With(screen.Build(0), "login-placeholder-password"));
    }

    [Fact]
    public void The_change_password_form_has_placeholders_for_the_old_and_the_new_password()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.True(SameFrame(Expect("ENTER OLD PASSWORD", 1, ShellPaint.Faint), One(sprites, "login-placeholder-password").Frame));
        Assert.True(SameFrame(Expect("ENTER NEW PASSWORD", 1, ShellPaint.Faint), One(sprites, "login-placeholder-new-password").Frame));
        Assert.True(SameFrame(Expect("OLD PASSWORD", 1, ShellPaint.Gold), One(sprites, "login-label-password").Frame));
        Assert.True(SameFrame(Expect("NEW PASSWORD", 1, ShellPaint.Muted), One(sprites, "login-label-new-password").Frame));
    }

    // ------------------------------------------------------------------------------------------------ 画面:提示与等待

    [Fact]
    public void An_error_notice_is_a_red_bar_with_an_icon_in_the_notice_slot_and_the_rule_gives_way()
    {
        var screen = new LoginScreen();
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.WrongCredentials));

        List<SceneSprite> sprites = screen.Build(0);

        HitRect slot = LoginScreen.NoticeRect(LoginMode.Login);
        SceneSprite bar = One(sprites, "login-notice-bar");
        Assert.Equal((slot.X, slot.Y, slot.Width, slot.Height), (bar.ScreenX, bar.ScreenY, bar.Frame.Width, bar.Frame.Height));
        SceneSprite accent = One(sprites, "login-notice-accent");
        Assert.Equal((slot.X + 1, slot.Y + 1, 4, slot.Height - 2), (accent.ScreenX, accent.ScreenY, accent.Frame.Width, accent.Frame.Height));
        SceneSprite icon = One(sprites, "login-notice-icon");
        Assert.Equal((slot.X + 16, slot.Y + (slot.Height - 20) / 2, 20, 20), (icon.ScreenX, icon.ScreenY, icon.Frame.Width, icon.Frame.Height));
        SceneSprite text = One(sprites, "login-notice");
        Assert.True(SameFrame(Expect("WRONG ACCOUNT OR PASSWORD", 1, OnlineShellArt.ToneTextColor(ShellTone.Error)), text.Frame));
        Assert.Equal((slot.X + 44, slot.Y + (slot.Height - text.Frame.Height) / 2), (text.ScreenX, text.ScreenY));
        Assert.Equal((0xFF, 0x7A, 0x45, 255), ShellFrames.Pixel(accent.Frame, 2, 10));                 // 色条是语气的主色
        Assert.Empty(With(sprites, "login-rule"));
        Assert.Equal(new[] { "login-notice-bar", "login-notice-accent", "login-notice-icon", "login-notice" }, Tags(sprites).Where(t => t.StartsWith("login-notice")).ToArray());
        Assert.True(slot.Y > LoginScreen.FieldRect(LoginField.Password).Y + LoginScreen.FieldHeight);
        Assert.True(slot.Y + slot.Height < Rect(LoginButton.Login).Y);
    }

    [Fact]
    public void Success_and_information_notices_have_their_own_colours_and_icons_and_no_notice_brings_the_rule_back()
    {
        var screen = new LoginScreen();
        var paint = new ShellPaint(new OverlayArt());

        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.RegisterOk));
        List<SceneSprite> success = screen.Build(0);
        Assert.True(SameFrame(Expect("REGISTERED   PLEASE LOGIN", 1, OnlineShellArt.ToneTextColor(ShellTone.Success)), One(success, "login-notice").Frame));
        Assert.True(SameFrame(paint.Icon(ShellTone.Success, 20), One(success, "login-notice-icon").Frame));

        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.Cancelled));
        List<SceneSprite> info = screen.Build(0);
        Assert.True(SameFrame(Expect("CANCELLED", 1, OnlineShellArt.ToneTextColor(ShellTone.Info)), One(info, "login-notice").Frame));
        Assert.True(SameFrame(paint.Icon(ShellTone.Info, 20), One(info, "login-notice-icon").Frame));

        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
        Assert.True(SameFrame(paint.Icon(ShellTone.Error, 20), One(screen.Build(0), "login-notice-icon").Frame));

        screen.SetNotice(null);
        List<SceneSprite> none = screen.Build(0);
        Assert.DoesNotContain(none, s => s.AnimationPath.StartsWith("login-notice"));
        Assert.Single(With(none, "login-rule"));
    }

    [Fact]
    public void When_idle_the_notice_slot_tells_the_account_rule_in_one_line_inside_the_slot()
    {
        var screen = new LoginScreen();

        SceneSprite rule = One(screen.Build(0), "login-rule");

        HitRect slot = LoginScreen.NoticeRect(LoginMode.Login);
        Assert.True(SameFrame(Expect("ACCOUNT: 4-32 LETTERS OR DIGITS", 1, 0x7C93B8u), rule.Frame));
        Assert.Equal((slot.X + 2, slot.Y + (slot.Height - rule.Frame.Height) / 2), (rule.ScreenX, rule.ScreenY));
        Assert.True(rule.ScreenX + rule.Frame.Width <= slot.X + slot.Width);
    }

    [Fact]
    public void In_change_password_mode_the_notice_moves_below_the_third_field()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.WrongCredentials));

        List<SceneSprite> sprites = screen.Build(0);

        HitRect slot = LoginScreen.NoticeRect(LoginMode.ChangePassword);
        SceneSprite bar = One(sprites, "login-notice-bar");
        Assert.Equal((slot.X, slot.Y), (bar.ScreenX, bar.ScreenY));
        Assert.True(slot.Y >= LoginScreen.FieldRect(LoginField.NewPassword).Y + LoginScreen.FieldHeight);
        Assert.True(slot.Y + slot.Height <= Rect(LoginButton.Confirm).Y);
        Assert.True(slot.Y > LoginScreen.NoticeRect(LoginMode.Login).Y);
    }

    [Fact]
    public void The_rate_limit_notice_shows_the_seconds_left()
    {
        var screen = new LoginScreen();
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: 12));
        Assert.True(SameFrame(Expect("TOO MANY REQUESTS   RETRY IN 12 S", 1, OnlineShellArt.ToneTextColor(ShellTone.Error)), One(screen.Build(0), "login-notice").Frame));

        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: 9));
        Assert.True(SameFrame(Expect("TOO MANY REQUESTS   RETRY IN 9 S", 1, OnlineShellArt.ToneTextColor(ShellTone.Error)), One(screen.Build(0), "login-notice").Frame));
    }

    [Fact]
    public void While_busy_the_screen_shows_a_spinner_bar_instead_of_the_notice_and_greys_out_the_buttons()
    {
        var screen = new LoginScreen();
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.WrongCredentials));
        screen.SetBusy(true);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.DoesNotContain(sprites, s => s.AnimationPath.StartsWith("login-notice"));
        Assert.Empty(With(sprites, "login-rule"));
        HitRect slot = LoginScreen.NoticeRect(LoginMode.Login);
        SceneSprite bar = One(sprites, "login-busy-bar");
        Assert.Equal((slot.X, slot.Y, slot.Width, slot.Height), (bar.ScreenX, bar.ScreenY, bar.Frame.Width, bar.Frame.Height));
        SceneSprite waiting = One(sprites, "login-busy");
        Assert.True(SameFrame(Expect("PLEASE WAIT...", 1, OnlineShellArt.ToneTextColor(ShellTone.Wait)), waiting.Frame));
        Assert.Equal((slot.X + 44, slot.Y + (slot.Height - waiting.Frame.Height) / 2), (waiting.ScreenX, waiting.ScreenY));
        SceneSprite spinner = One(sprites, "login-busy-spinner");
        Assert.Equal((slot.X + 16, slot.Y + (slot.Height - 20) / 2, 20, 20), (spinner.ScreenX, spinner.ScreenY, spinner.Frame.Width, spinner.Frame.Height));
        Assert.Empty(With(sprites, "login-busy-icon"));
        foreach (string tag in new[] { "login", "register", "change-password" })
        {
            Assert.Single(With(sprites, "login-button-" + tag + "-disabled"));
            Assert.Empty(With(sprites, "login-button-" + tag));
        }
        Assert.Empty(With(sprites, "login-button-shadow-login"));
        Assert.True(HasColor(One(sprites, "login-button-text-login").Frame, 0x8A97ADu));
        Assert.False(HasColor(One(sprites, "login-button-text-login").Frame, ShellPaint.Warm));
        Assert.True(HasColor(One(sprites, "login-button-text-register").Frame, ShellPaint.Faint));
        Assert.False(HasColor(One(sprites, "login-button-text-register").Frame, 0xD6E4FFu));

        screen.SetBusy(false);
        sprites = screen.Build(0);
        Assert.DoesNotContain(sprites, s => s.AnimationPath.StartsWith("login-busy"));
        Assert.Single(With(sprites, "login-notice"));
        Assert.Single(With(sprites, "login-button-login"));
        Assert.Single(With(sprites, "login-button-shadow-login"));
        Assert.True(HasColor(One(sprites, "login-button-text-login").Frame, ShellPaint.Warm));
    }

    [Fact]
    public void The_busy_spinner_turns_with_time_and_comes_back_every_800_ms()
    {
        var screen = new LoginScreen();
        screen.SetBusy(true);

        SceneSprite at0 = One(screen.Build(0), "login-busy-spinner");
        SceneSprite at79 = One(screen.Build(79), "login-busy-spinner");
        SceneSprite at80 = One(screen.Build(80), "login-busy-spinner");
        SceneSprite at800 = One(screen.Build(800), "login-busy-spinner");

        Assert.Same(at0.Frame, at79.Frame);
        Assert.NotSame(at0.Frame, at80.Frame);
        Assert.False(SameFrame(at0.Frame, at80.Frame));
        Assert.Same(at0.Frame, at800.Frame);
    }

    [Fact]
    public void The_hint_row_changes_to_cancel_while_busy_and_follows_the_mode()
    {
        var screen = new LoginScreen();

        List<SceneSprite> idle = screen.Build(0);
        Assert.Equal(2, idle.Count(s => s.AnimationPath.StartsWith("login-hint-key-")));
        Assert.True(SameFrame(Expect("SWITCH", 1, ShellPaint.Muted), One(idle, "login-hint-text-0").Frame));
        Assert.True(SameFrame(Expect("LOGIN", 1, ShellPaint.Muted), One(idle, "login-hint-text-1").Frame));
        Assert.True(SameFrame(Expect("TAB", 1, ShellPaint.Ink), One(idle, "login-hint-keytext-0").Frame));
        Assert.True(SameFrame(Expect("ENTER", 1, ShellPaint.Ink), One(idle, "login-hint-keytext-1").Frame));

        screen.SetBusy(true);
        List<SceneSprite> busy = screen.Build(0);
        Assert.Single(busy, s => s.AnimationPath.StartsWith("login-hint-key-"));
        Assert.True(SameFrame(Expect("CANCEL", 1, ShellPaint.Muted), One(busy, "login-hint-text-0").Frame));
        Assert.True(SameFrame(Expect("ESC", 1, ShellPaint.Ink), One(busy, "login-hint-keytext-0").Frame));

        screen.SetBusy(false);
        screen.SetMode(LoginMode.ChangePassword);
        List<SceneSprite> changing = screen.Build(0);
        Assert.Equal(3, changing.Count(s => s.AnimationPath.StartsWith("login-hint-key-")));
        Assert.True(SameFrame(Expect("CONFIRM", 1, ShellPaint.Muted), One(changing, "login-hint-text-1").Frame));
        Assert.True(SameFrame(Expect("BACK", 1, ShellPaint.Muted), One(changing, "login-hint-text-2").Frame));
    }

    [Fact]
    public void The_hint_row_is_centred_on_the_screen_at_the_bottom()
    {
        foreach (LoginMode mode in new[] { LoginMode.Login, LoginMode.ChangePassword })
        {
            var screen = new LoginScreen();
            screen.SetMode(mode);
            List<SceneSprite> sprites = screen.Build(0);

            List<SceneSprite> row = sprites.Where(s => s.AnimationPath.StartsWith("login-hint-")).ToList();
            int left = row.Min(s => s.ScreenX);
            int right = row.Max(s => s.ScreenX + s.Frame.Width);

            Assert.Equal(OnlineShellArt.HintTop, One(sprites, "login-hint-key-0").ScreenY);
            Assert.True(Math.Abs((left + right) / 2 - LoginScreen.ScreenWidth / 2) <= 2, mode + " 的按键说明没有居中");
            Assert.True(left >= 0 && right <= LoginScreen.ScreenWidth);
        }
    }

    [Fact]
    public void Every_notice_kind_has_a_one_line_text_that_fits_inside_the_bar_with_and_without_fonts()
    {
        HitRect slot = LoginScreen.NoticeRect(LoginMode.Login);
        foreach (IGlyphFonts fonts in new IGlyphFonts[] { null, new SizedFonts() })
        {
            foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None))
            {
                var screen = new LoginScreen(null, fonts);
                screen.SetNotice(new OnlineNotice(kind, seconds: 30, detail: "x"));

                SceneSprite notice = One(screen.Build(0), "login-notice");

                Assert.True(notice.Frame.Width > 4, kind.ToString());
                Assert.True(notice.ScreenX + notice.Frame.Width <= slot.X + slot.Width, kind + " 超出提示条");
                Assert.True(notice.ScreenX + notice.Frame.Width <= LoginScreen.PanelX + LoginScreen.PanelWidth, kind + " 超出面板");
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ 画面:语言

    [Fact]
    public void Without_fonts_the_labels_are_english_placeholders_at_the_scale_of_their_size()
    {
        var screen = new LoginScreen();

        List<SceneSprite> sprites = screen.Build(0);

        Assert.True(SameFrame(Expect("LOGIN", 2, ShellPaint.Ink), One(sprites, "login-title").Frame));
        Assert.True(SameFrame(Expect("SIGN IN TO CONTINUE", 1, ShellPaint.Muted), One(sprites, "login-subtitle").Frame));
        Assert.True(SameFrame(Expect("ACCOUNT", 1, ShellPaint.Gold), One(sprites, "login-label-username").Frame));
        Assert.True(SameFrame(Expect("PASSWORD", 1, ShellPaint.Muted), One(sprites, "login-label-password").Frame));
        Assert.True(SameFrame(Expect("ENTER GAME", 2, ShellPaint.Warm, 0xF0B640u), One(sprites, "login-button-text-login").Frame));
        Assert.True(SameFrame(Expect("REGISTER", 1, 0xD6E4FFu), One(sprites, "login-button-text-register").Frame));
        Assert.True(SameFrame(Expect("CHANGE PASSWORD", 1, 0xD6E4FFu), One(sprites, "login-button-text-change-password").Frame));
        Assert.True(SameFrame(Expect("DNF", 5, ShellPaint.Gold, 0x24130Au), One(sprites, "login-banner").Frame));
    }

    [Fact]
    public void With_fonts_the_labels_are_chinese_and_use_the_designed_pixel_sizes()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);
        screen.Type("player_01");

        screen.Build(0);

        foreach (string text in new[]
        {
            "地下城与勇士", "账号登录", "登录后选择服务器与角色", "账号", "密码", "进入游戏", "注册", "修改密码",
            "请输入密码", "账号为 4 到 32 位字母、数字或下划线", "Tab", "Enter", "切换输入框",
        })
        {
            Assert.True(fonts.Asked(text), text);
        }
        Assert.Equal(new[] { 12, 14, 16, 17, 20, 26, 46 }, fonts.Sizes.OrderBy(v => v).ToArray());      // 按键名、小字、标签、账号、主按钮、标题、游戏名
        Assert.DoesNotContain((int)'旧', fonts.CodePoints);              // 登录形态里没有"旧密码"
        Assert.DoesNotContain((int)'新', fonts.CodePoints);
    }

    [Fact]
    public void With_fonts_the_change_password_form_is_chinese_too()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);
        screen.SetMode(LoginMode.ChangePassword);

        screen.Build(0);

        foreach (string text in new[] { "修改密码", "输入旧密码与新密码", "旧密码", "新密码", "请输入旧密码", "请输入新密码", "确认修改", "返回", "Esc" })
        {
            Assert.True(fonts.Asked(text), text);
        }
        Assert.DoesNotContain((int)'注', fonts.CodePoints);              // 修改密码形态里没有"注册"按钮
        Assert.DoesNotContain((int)'录', fonts.CodePoints);              // 也没有"登录"的任何一处
    }

    [Fact]
    public void With_fonts_the_busy_screen_says_please_wait_and_esc_cancel_in_chinese()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);
        screen.SetBusy(true);

        screen.Build(0);

        Assert.True(fonts.Asked("请稍候…"));
        Assert.True(fonts.Asked("取消"));
        Assert.True(fonts.Asked("Esc"));
    }

    [Fact]
    public void A_font_with_tiny_glyphs_still_builds_the_whole_page_with_a_banner_glow_that_fits_the_banner()
    {
        var screen = new LoginScreen(null, new TinyFonts());

        List<SceneSprite> sprites = screen.Build(0);

        SceneSprite banner = One(sprites, "login-banner");
        SceneSprite glow = One(sprites, "login-banner-glow");
        Assert.True(glow.Frame.Width >= 2 + 2 * 34 && glow.Frame.Height >= 2 + 2 * 34, "光晕本体至少 2 像素");
        // 光晕比标题字大一圈(向外扩 34),不会比标题字小:四边都不缩进到标题字里面去
        Assert.True(glow.ScreenX <= banner.ScreenX && glow.ScreenY <= banner.ScreenY);
        Assert.True(glow.ScreenX + glow.Frame.Width >= banner.ScreenX + banner.Frame.Width);
        Assert.True(glow.ScreenY + glow.Frame.Height >= banner.ScreenY + banner.Frame.Height);
        Assert.Contains("login-banner-rule", Tags(sprites));
        Assert.Contains("login-banner-gem", Tags(sprites));
    }

    [Fact]
    public void With_fonts_the_notices_are_chinese_with_full_width_punctuation_and_the_not_configured_notice_names_the_item()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.NotConfigured, detail: "缺少 server_url"));

        screen.Build(0);

        Assert.True(fonts.Asked("未配置连接：缺少 server_url"));
        Assert.DoesNotContain((int)':', fonts.CodePoints);
    }

    // ------------------------------------------------------------------------------------------------ 画面:缓存与整体

    [Fact]
    public void Every_frame_is_the_same_object_from_one_build_to_the_next_and_across_time()
    {
        foreach (IGlyphFonts fonts in new IGlyphFonts[] { null, new SizedFonts() })
        {
            var screen = new LoginScreen(null, fonts);
            screen.Type("player_01");
            screen.Tab();
            screen.Type("secret");
            screen.SetNotice(new OnlineNotice(OnlineNoticeKind.WrongCredentials));

            List<SceneSprite> a = screen.Build(0);
            List<SceneSprite> b = screen.Build(0);
            List<SceneSprite> c = screen.Build(100);

            Assert.Equal(a.Count, b.Count);
            Assert.Equal(a.Count, c.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].AnimationPath, b[i].AnimationPath);
                Assert.Same(a[i].Frame, b[i].Frame);
                Assert.Same(a[i].Frame, c[i].Frame);
            }
        }
    }

    [Fact]
    public void Every_state_builds_a_valid_ordered_scene_with_and_without_fonts_and_art()
    {
        var art = new ShellFakeSprites();
        art.AddBackground();
        var notices = new List<OnlineNotice> { null };
        notices.AddRange(Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None).Select(k => new OnlineNotice(k, seconds: 30, detail: "项")));
        foreach (IGlyphFonts fonts in new IGlyphFonts[] { null, new SizedFonts(), new RecordingFonts() })
        {
            foreach (ISpriteSource sprites in new ISpriteSource[] { null, art })
            {
                foreach (LoginMode mode in new[] { LoginMode.Login, LoginMode.ChangePassword })
                {
                    foreach (bool busy in new[] { false, true })
                    {
                        foreach (OnlineNotice notice in notices)
                        {
                            var screen = new LoginScreen(sprites, fonts);
                            screen.Type(new string('u', 32));
                            screen.Tab();
                            screen.Type(new string('p', 64));
                            screen.SetMode(mode);
                            screen.Type("q");
                            screen.SetBusy(busy);
                            screen.SetNotice(notice);

                            List<SceneSprite> scene = screen.Build(123);

                            Assert.NotEmpty(scene);
                            Assert.All(scene, s => Assert.True(s.Frame != null && s.Frame.Width > 0 && s.Frame.Height > 0, s.AnimationPath));
                            Assert.All(scene, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
                            int[] keys = scene.Select(s => s.SortKey).ToArray();
                            Assert.Equal(keys.OrderBy(k => k).Distinct().ToArray(), keys);
                        }
                    }
                }
            }
        }
    }
}
