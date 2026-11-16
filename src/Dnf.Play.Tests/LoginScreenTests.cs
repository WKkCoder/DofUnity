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
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.ChangePassword, login.X + 5, login.Y + 5));
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.ChangePassword, register.X + 5, register.Y + 5));
        Assert.Equal(LoginButton.None, LoginScreen.HitButton(LoginMode.ChangePassword, open.X + 5, open.Y + 5));
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
        foreach (LoginButton b in loginButtons.Concat(new[] { LoginButton.Confirm, LoginButton.Back }))
        {
            HitRect r = Rect(b);
            Assert.InRange(r.X, LoginScreen.PanelX, LoginScreen.PanelX + LoginScreen.PanelWidth);
            Assert.InRange(r.X + r.Width, LoginScreen.PanelX, LoginScreen.PanelX + LoginScreen.PanelWidth);
            Assert.InRange(r.Y + r.Height, LoginScreen.PanelY, LoginScreen.PanelY + LoginScreen.PanelHeight);
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

    [Fact]
    public void The_login_form_has_exactly_these_parts_in_this_order_without_a_background_image()
    {
        var screen = new LoginScreen();

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(new[]
        {
            "online-backdrop",
            "login-panel-border", "login-panel", "login-title",
            "login-label-username", "login-box-border-username", "login-box-username", "login-caret",
            "login-label-password", "login-box-border-password", "login-box-password",
            "login-button-login", "login-button-text-login",
            "login-button-register", "login-button-text-register",
            "login-button-change-password", "login-button-text-change-password",
            "login-hint",
        }, Tags(sprites));
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(sprites.Select(s => s.SortKey).OrderBy(k => k).Distinct().ToArray(), sprites.Select(s => s.SortKey).ToArray());
    }

    [Fact]
    public void The_change_password_form_has_the_third_field_and_its_own_buttons()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(new[]
        {
            "online-backdrop",
            "login-panel-border", "login-panel", "login-title",
            "login-label-username", "login-box-border-username", "login-box-username",
            "login-label-password", "login-box-border-password", "login-box-password", "login-caret",
            "login-label-new-password", "login-box-border-new-password", "login-box-new-password",
            "login-button-confirm", "login-button-text-confirm",
            "login-button-back", "login-button-text-back",
            "login-hint",
        }, Tags(sprites));
        Assert.Equal(269, One(sprites, "login-title").Frame.Width);           // CHANGE PASSWORD,3 倍
    }

    [Fact]
    public void Panel_boxes_and_buttons_are_drawn_where_they_are_hit_tested()
    {
        var screen = new LoginScreen();
        List<SceneSprite> sprites = screen.Build(0);

        SceneSprite border = One(sprites, "login-panel-border");
        Assert.Equal((LoginScreen.PanelX, LoginScreen.PanelY, LoginScreen.PanelWidth, LoginScreen.PanelHeight), (border.ScreenX, border.ScreenY, border.Frame.Width, border.Frame.Height));
        foreach (LoginField f in new[] { LoginField.Username, LoginField.Password })
        {
            HitRect r = LoginScreen.FieldRect(f);
            SceneSprite box = One(sprites, "login-box-border-" + (f == LoginField.Username ? "username" : "password"));
            Assert.Equal((r.X, r.Y, r.Width, r.Height), (box.ScreenX, box.ScreenY, box.Frame.Width, box.Frame.Height));
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
    }

    [Fact]
    public void Only_the_focused_box_has_the_yellow_border()
    {
        var screen = new LoginScreen();
        Assert.True(HasColor(One(screen.Build(0), "login-box-border-username").Frame, OverlayArt.Yellow));
        Assert.False(HasColor(One(screen.Build(0), "login-box-border-password").Frame, OverlayArt.Yellow));

        screen.Tab();
        Assert.False(HasColor(One(screen.Build(0), "login-box-border-username").Frame, OverlayArt.Yellow));
        Assert.True(HasColor(One(screen.Build(0), "login-box-border-password").Frame, OverlayArt.Yellow));
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
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(64)]
    public void The_password_is_drawn_as_one_bullet_per_character_up_to_the_box_width(int length)
    {
        var screen = new LoginScreen();
        screen.Tab();
        screen.Type(new string('a', length));

        List<SceneSprite> masks = With(screen.Build(0), "login-password-mask");

        Assert.Equal(Math.Min(length, 24), masks.Count);
        HitRect box = LoginScreen.FieldRect(LoginField.Password);
        for (int i = 0; i < masks.Count; i++)
        {
            Assert.Equal((8, 8), (masks[i].Frame.Width, masks[i].Frame.Height));
            Assert.Equal(box.X + 8 + i * 12, masks[i].ScreenX);
            Assert.Equal(box.Y + 12, masks[i].ScreenY);
            Assert.True(HasColor(masks[i].Frame, OverlayArt.White));
            Assert.InRange(masks[i].ScreenX + 8, box.X, box.X + box.Width);
        }
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
        Assert.Equal(3, masks.Count(m => m.ScreenY == second.Y + 12));
        Assert.Equal(5, masks.Count(m => m.ScreenY == third.Y + 12));
        Assert.DoesNotContain(masks, m => m.ScreenY == LoginScreen.FieldRect(LoginField.Username).Y + 12);
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
        foreach (char secret in "zqk#!jw&")
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

        // 账号框里只有账号文字;密码框里除了框、圆点与光标没有别的东西
        HitRect box = LoginScreen.FieldRect(LoginField.Password);
        string[] allowed = { "login-box-border-password", "login-box-password", "login-password-mask", "login-caret" };
        foreach (SceneSprite s in sprites.Where(s => s.ScreenX >= box.X && s.ScreenX < box.X + box.Width && s.ScreenY >= box.Y && s.ScreenY < box.Y + box.Height))
        {
            Assert.Contains(s.AnimationPath, allowed);
        }
    }

    // ------------------------------------------------------------------------------------------------ 画面:账号与光标

    [Fact]
    public void Without_fonts_the_username_is_drawn_in_capitals_with_dashes_and_question_marks_for_what_the_bitmap_font_lacks()
    {
        var screen = new LoginScreen();
        screen.Type("player_01");
        Assert.Equal(OverlayArt.TextWidth("PLAYER-01", 1), One(screen.Build(0), "login-username-text").Frame.Width);

        var odd = new LoginScreen();
        odd.Type("名a");
        Assert.Equal(OverlayArt.TextWidth("?A", 1), One(odd.Build(0), "login-username-text").Frame.Width);
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
        Assert.Equal(box.X + 8, text.ScreenX);
        Assert.Equal(box.Y + (box.Height - text.Frame.Height) / 2, text.ScreenY);
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
        Assert.Equal(text.ScreenX + text.Frame.Width + 2, caret.ScreenX);
        Assert.Equal((2, 18), (caret.Frame.Width, caret.Frame.Height));
        Assert.Equal(box.Y + (box.Height - 18) / 2, caret.ScreenY);

        screen.Tab();
        screen.Type("xyz");
        SceneSprite second = One(screen.Build(0), "login-caret");
        HitRect pw = LoginScreen.FieldRect(LoginField.Password);
        Assert.Equal(pw.X + 8 + 3 * 12 + 2, second.ScreenX);
        Assert.Equal(pw.Y + (pw.Height - 18) / 2, second.ScreenY);
    }

    [Fact]
    public void The_caret_after_an_empty_field_sits_at_its_left_edge()
    {
        var screen = new LoginScreen();

        SceneSprite caret = One(screen.Build(0), "login-caret");

        Assert.Equal(LoginScreen.FieldRect(LoginField.Username).X + 8 + 2, caret.ScreenX);
    }

    // ------------------------------------------------------------------------------------------------ 画面:提示与等待

    [Fact]
    public void An_error_notice_is_drawn_in_orange_and_an_info_notice_in_yellow_below_the_password_field()
    {
        var screen = new LoginScreen();
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.WrongCredentials));

        SceneSprite error = One(screen.Build(0), "login-notice");
        Assert.True(HasColor(error.Frame, OverlayArt.Orange));
        Assert.False(HasColor(error.Frame, OverlayArt.Yellow));
        Assert.Equal(OverlayArt.TextWidth("WRONG ACCOUNT OR PASSWORD", 1), error.Frame.Width);
        Assert.Equal((LoginScreen.FieldX, LoginScreen.PanelY + 186), (error.ScreenX, error.ScreenY));
        Assert.True(error.ScreenY > LoginScreen.FieldRect(LoginField.Password).Y + LoginScreen.FieldHeight);
        Assert.True(error.ScreenY + error.Frame.Height < Rect(LoginButton.Login).Y);

        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.RegisterOk));
        SceneSprite info = One(screen.Build(0), "login-notice");
        Assert.True(HasColor(info.Frame, OverlayArt.Yellow));
        Assert.False(HasColor(info.Frame, OverlayArt.Orange));

        screen.SetNotice(null);
        Assert.Empty(With(screen.Build(0), "login-notice"));
    }

    [Fact]
    public void In_change_password_mode_the_notice_moves_below_the_third_field()
    {
        var screen = new LoginScreen();
        screen.SetMode(LoginMode.ChangePassword);
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.WrongCredentials));

        SceneSprite notice = One(screen.Build(0), "login-notice");

        Assert.Equal(LoginScreen.PanelY + 240, notice.ScreenY);
        Assert.True(notice.ScreenY >= LoginScreen.FieldRect(LoginField.NewPassword).Y + LoginScreen.FieldHeight);
        Assert.True(notice.ScreenY + notice.Frame.Height <= Rect(LoginButton.Confirm).Y);
    }

    [Fact]
    public void The_rate_limit_notice_shows_the_seconds_left()
    {
        var screen = new LoginScreen();
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: 12));
        int twelve = One(screen.Build(0), "login-notice").Frame.Width;
        Assert.Equal(OverlayArt.TextWidth("TOO MANY REQUESTS   RETRY IN 12 S", 1), twelve);

        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: 9));
        Assert.Equal(OverlayArt.TextWidth("TOO MANY REQUESTS   RETRY IN 9 S", 1), One(screen.Build(0), "login-notice").Frame.Width);
    }

    [Fact]
    public void While_busy_the_screen_shows_a_waiting_line_instead_of_the_notice_and_greys_out_the_buttons()
    {
        var screen = new LoginScreen();
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.WrongCredentials));
        screen.SetBusy(true);

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Empty(With(sprites, "login-notice"));
        SceneSprite waiting = One(sprites, "login-busy");
        Assert.Equal(OverlayArt.TextWidth("PLEASE WAIT...   ESC CANCEL", 1), waiting.Frame.Width);
        Assert.Equal((LoginScreen.FieldX, LoginScreen.PanelY + 186), (waiting.ScreenX, waiting.ScreenY));
        foreach (string tag in new[] { "login", "register", "change-password" })
        {
            Assert.Single(With(sprites, "login-button-" + tag + "-disabled"));
            Assert.Empty(With(sprites, "login-button-" + tag));
            Assert.True(HasColor(One(sprites, "login-button-text-" + tag).Frame, 0x8A8F98u));
            Assert.False(HasColor(One(sprites, "login-button-text-" + tag).Frame, CharacterSelectArt.ButtonTextColor));
        }

        screen.SetBusy(false);
        sprites = screen.Build(0);
        Assert.Empty(With(sprites, "login-busy"));
        Assert.Single(With(sprites, "login-notice"));
        Assert.Single(With(sprites, "login-button-login"));
        Assert.True(HasColor(One(sprites, "login-button-text-login").Frame, CharacterSelectArt.ButtonTextColor));
    }

    [Fact]
    public void Every_notice_kind_has_a_one_line_english_text_that_fits_inside_the_panel()
    {
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None))
        {
            var screen = new LoginScreen();
            screen.SetNotice(new OnlineNotice(kind, seconds: 30, detail: "x"));

            SceneSprite notice = One(screen.Build(0), "login-notice");

            Assert.True(notice.Frame.Width > 4, kind.ToString());
            Assert.True(notice.ScreenX + notice.Frame.Width <= LoginScreen.PanelX + LoginScreen.PanelWidth, kind + " 超出面板");
        }
    }

    // ------------------------------------------------------------------------------------------------ 画面:语言

    [Fact]
    public void Without_fonts_the_labels_are_english_placeholders_and_scale_one()
    {
        var screen = new LoginScreen();

        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(OverlayArt.TextWidth("LOGIN", 3), One(sprites, "login-title").Frame.Width);
        Assert.Equal(OverlayArt.TextWidth("ACCOUNT", 1), One(sprites, "login-label-username").Frame.Width);
        Assert.Equal(OverlayArt.TextWidth("PASSWORD", 1), One(sprites, "login-label-password").Frame.Width);
        Assert.Equal(OverlayArt.TextWidth("ENTER GAME", 1), One(sprites, "login-button-text-login").Frame.Width);
        Assert.Equal(OverlayArt.TextWidth("REGISTER", 1), One(sprites, "login-button-text-register").Frame.Width);
        Assert.Equal(OverlayArt.TextWidth("CHANGE PASSWORD", 1), One(sprites, "login-button-text-change-password").Frame.Width);
        Assert.Equal(OverlayArt.TextWidth("TAB SWITCH   ENTER LOGIN", 1), One(sprites, "login-hint").Frame.Width);
    }

    [Fact]
    public void With_fonts_the_labels_are_chinese_and_bigger()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);

        screen.Build(0);

        Assert.True(fonts.Asked("登录"));
        Assert.True(fonts.Asked("账号"));
        Assert.True(fonts.Asked("密码"));
        Assert.True(fonts.Asked("进入游戏"));
        Assert.True(fonts.Asked("注册"));
        Assert.True(fonts.Asked("修改密码"));
        Assert.True(fonts.Asked("回车"));
        Assert.Contains(OverlayArt.GlyphPixelSize(3), fonts.Sizes);       // 标题 3 倍
        Assert.Contains(OverlayArt.GlyphPixelSize(2), fonts.Sizes);       // 标签、按钮、提示 2 倍
        Assert.DoesNotContain((int)'旧', fonts.CodePoints);              // 登录形态里没有"旧密码"
    }

    [Fact]
    public void With_fonts_the_change_password_form_is_chinese_too()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);
        screen.SetMode(LoginMode.ChangePassword);

        screen.Build(0);

        Assert.True(fonts.Asked("修改密码"));
        Assert.True(fonts.Asked("旧密码"));
        Assert.True(fonts.Asked("新密码"));
        Assert.True(fonts.Asked("确认修改"));
        Assert.True(fonts.Asked("返回"));
        Assert.DoesNotContain((int)'注', fonts.CodePoints);              // 修改密码形态里没有"注册"按钮
    }

    [Fact]
    public void With_fonts_the_notices_are_chinese_and_the_not_configured_notice_names_the_item()
    {
        var fonts = new RecordingFonts();
        var screen = new LoginScreen(null, fonts);
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.NotConfigured, detail: "缺少 server_url"));

        screen.Build(0);

        Assert.True(fonts.Asked("未配置连接"));
        Assert.True(fonts.Asked("server_url"));
    }
}
