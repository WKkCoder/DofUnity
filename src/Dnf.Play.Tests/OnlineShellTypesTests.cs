using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>外壳的基础类型:阶段短码、提示类别与短码、结果对象、名册记录、本地输入检查、占位名册。</summary>
public class OnlineShellTypesTests
{
    [Theory]
    [InlineData(OnlineShellStage.Login, "login")]
    [InlineData(OnlineShellStage.ServerSelect, "server")]
    [InlineData(OnlineShellStage.ChannelSelect, "channel")]
    [InlineData(OnlineShellStage.CharacterSelect, "charselect")]
    [InlineData(OnlineShellStage.Loading, "loading")]
    [InlineData(OnlineShellStage.InGame, "ingame")]
    [InlineData((OnlineShellStage)99, "unknown")]
    public void Stages_have_fixed_log_codes(OnlineShellStage stage, string code)
    {
        Assert.Equal(code, OnlineShellStages.Code(stage));
    }

    [Fact]
    public void Stages_are_numbered_in_flow_order()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[]
        {
            (int)OnlineShellStage.Login, (int)OnlineShellStage.ServerSelect, (int)OnlineShellStage.ChannelSelect,
            (int)OnlineShellStage.CharacterSelect, (int)OnlineShellStage.Loading, (int)OnlineShellStage.InGame,
        });
    }

    [Fact]
    public void Every_notice_kind_has_a_distinct_lowercase_snake_case_code()
    {
        var seen = new HashSet<string>();
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None))
        {
            string code = OnlineNotices.Code(kind);
            Assert.Matches("^[a-z]+(_[a-z]+)*$", code);
            Assert.True(seen.Add(code), "短码重复:" + code);
        }
        Assert.Equal("none", OnlineNotices.Code(OnlineNoticeKind.None));
        Assert.Equal("none", OnlineNotices.Code((OnlineNoticeKind)999));
    }

    [Theory]
    [InlineData(OnlineNoticeKind.WrongCredentials, "wrong_credentials")]
    [InlineData(OnlineNoticeKind.AccountBanned, "banned")]
    [InlineData(OnlineNoticeKind.RateLimited, "rate_limited")]
    [InlineData(OnlineNoticeKind.NetworkError, "network")]
    [InlineData(OnlineNoticeKind.ProtocolError, "protocol")]
    [InlineData(OnlineNoticeKind.NoGameServer, "no_game_server")]
    [InlineData(OnlineNoticeKind.InvalidUsername, "invalid_username")]
    [InlineData(OnlineNoticeKind.InvalidPassword, "invalid_password")]
    [InlineData(OnlineNoticeKind.NotConfigured, "not_configured")]
    [InlineData(OnlineNoticeKind.RegistrationClosed, "registration_closed")]
    [InlineData(OnlineNoticeKind.UserExists, "user_exists")]
    [InlineData(OnlineNoticeKind.Rejected, "rejected")]
    [InlineData(OnlineNoticeKind.RegisterOk, "registered")]
    [InlineData(OnlineNoticeKind.PasswordChanged, "password_changed")]
    [InlineData(OnlineNoticeKind.ConnectFailed, "connect_failed")]
    [InlineData(OnlineNoticeKind.GameLoginRejected, "game_login_rejected")]
    [InlineData(OnlineNoticeKind.CharacterListMissing, "no_character_list")]
    [InlineData(OnlineNoticeKind.SelectFailed, "select_failed")]
    [InlineData(OnlineNoticeKind.NoCharacters, "no_characters")]
    [InlineData(OnlineNoticeKind.Disconnected, "disconnected")]
    [InlineData(OnlineNoticeKind.Cancelled, "cancelled")]
    [InlineData(OnlineNoticeKind.Unexpected, "unexpected")]
    public void The_notice_codes_are_the_documented_ones(OnlineNoticeKind kind, string code)
    {
        Assert.Equal(code, OnlineNotices.Code(kind));
        Assert.Equal(code, new OnlineNotice(kind).Reason);
    }

    [Fact]
    public void Only_successes_cancellations_and_the_empty_account_hint_are_not_errors()
    {
        var notErrors = new[]
        {
            OnlineNoticeKind.None, OnlineNoticeKind.RegisterOk, OnlineNoticeKind.PasswordChanged, OnlineNoticeKind.NoCharacters, OnlineNoticeKind.Cancelled,
        };
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>())
        {
            Assert.Equal(!notErrors.Contains(kind), OnlineNotices.IsError(kind));
            Assert.Equal(!notErrors.Contains(kind), new OnlineNotice(kind).IsError);
        }
    }

    [Fact]
    public void A_notice_carries_only_kind_reason_seconds_and_detail()
    {
        var plain = new OnlineNotice(OnlineNoticeKind.NetworkError);
        Assert.Equal(OnlineNoticeKind.NetworkError, plain.Kind);
        Assert.Equal("network", plain.Reason);
        Assert.Equal(0, plain.Seconds);
        Assert.Null(plain.Detail);

        var full = new OnlineNotice(OnlineNoticeKind.NetworkError, "timeout", 5, "x");
        Assert.Equal("timeout", full.Reason);
        Assert.Equal(5, full.Seconds);
        Assert.Equal("x", full.Detail);
        Assert.Equal(0, new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: -4).Seconds);

        OnlineNotice again = full.WithSeconds(2);
        Assert.NotSame(full, again);
        Assert.Equal((OnlineNoticeKind.NetworkError, "timeout", 2, "x"), (again.Kind, again.Reason, again.Seconds, again.Detail));
        Assert.Equal(5, full.Seconds);                         // 不可变
    }

    // ------------------------------------------------------------------------------------------------ 结果对象

    [Fact]
    public void Auth_outcomes_report_success_or_a_failure_category_with_a_clamped_retry_after()
    {
        OnlineAuthOutcome ok = OnlineAuthOutcome.Ok();
        Assert.True(ok.Success);
        Assert.Equal(OnlineNoticeKind.None, ok.Failure);
        Assert.Equal(0, ok.RetryAfterSeconds);

        OnlineAuthOutcome limited = OnlineAuthOutcome.Fail(OnlineNoticeKind.RateLimited, 30);
        Assert.False(limited.Success);
        Assert.Equal(OnlineNoticeKind.RateLimited, limited.Failure);
        Assert.Equal(30, limited.RetryAfterSeconds);

        Assert.Equal(0, OnlineAuthOutcome.Fail(OnlineNoticeKind.RateLimited, -5).RetryAfterSeconds);
        Assert.Equal(0, OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials).RetryAfterSeconds);
        Assert.Equal(OnlineNoticeKind.Unexpected, OnlineAuthOutcome.Fail(OnlineNoticeKind.None).Failure);
    }

    [Fact]
    public void Connect_outcomes_carry_the_roster_on_success_and_treat_a_missing_roster_as_a_failure()
    {
        OnlineCharacterList list = ShellTestData.Roster(2);

        OnlineConnectOutcome ok = OnlineConnectOutcome.Ok(list);
        Assert.True(ok.Success);
        Assert.Same(list, ok.Characters);
        Assert.Equal(OnlineNoticeKind.None, ok.Failure);

        OnlineConnectOutcome empty = OnlineConnectOutcome.Ok(ShellTestData.Roster(0));
        Assert.True(empty.Success);                                    // 空名册是成功(账号下没有角色)

        OnlineConnectOutcome missing = OnlineConnectOutcome.Ok(null);
        Assert.False(missing.Success);
        Assert.Null(missing.Characters);
        Assert.Equal(OnlineNoticeKind.CharacterListMissing, missing.Failure);

        OnlineConnectOutcome failed = OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed);
        Assert.False(failed.Success);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, failed.Failure);
        Assert.Equal(OnlineNoticeKind.Unexpected, OnlineConnectOutcome.Fail(OnlineNoticeKind.None).Failure);
    }

    [Fact]
    public void Select_outcomes_say_whether_another_slot_may_be_tried()
    {
        OnlineSelectOutcome ok = OnlineSelectOutcome.Ok();
        Assert.True(ok.Entered);
        Assert.Equal(OnlineNoticeKind.None, ok.Failure);

        OnlineSelectOutcome retry = OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true);
        Assert.False(retry.Entered);
        Assert.True(retry.MayTryAnotherSlot);
        Assert.Equal(OnlineNoticeKind.SelectFailed, retry.Failure);

        Assert.False(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, false).MayTryAnotherSlot);
        Assert.Equal(OnlineNoticeKind.Unexpected, OnlineSelectOutcome.Fail(OnlineNoticeKind.None, true).Failure);
    }

    // ------------------------------------------------------------------------------------------------ 名册记录(一个字段都不丢)

    [Fact]
    public void A_character_entry_keeps_every_field_it_was_given()
    {
        var equips = new[]
        {
            new OnlineCharacterEquip(1, 11, 12, 13, 14, 15),
            new OnlineCharacterEquip(2, 21, 22, 23, 24, 25),
        };
        var name = new byte[] { 1, 2, 3 };

        var entry = new OnlineCharacterEntry(
            501, name, 2, 3, 4, 5, 6, 7, equips, 8, 9, 10u, 11, 12, 13u, 14, 15, 16, 17u);

        Assert.Equal((ushort)501, entry.Uid);
        Assert.Equal(new byte[] { 1, 2, 3 }, entry.NameBytes.ToArray());
        Assert.Equal(new byte[] { 2, 3, 4, 5, 6, 7 }, new[] { entry.X1, entry.Job, entry.GrowTypeByte, entry.Level, entry.D, entry.Status });
        Assert.Equal(new byte[] { 8, 9 }, new[] { entry.G, entry.H });
        Assert.Equal(10u, entry.I);
        Assert.Equal((byte)11, entry.J);
        Assert.Equal((byte)12, entry.Premium);
        Assert.Equal(13u, entry.K);
        Assert.Equal(new byte[] { 14, 15, 16 }, new[] { entry.A2, entry.B2, entry.C2 });
        Assert.Equal(17u, entry.D2);
        Assert.Equal(2, entry.Equips.Count);
        Assert.Equal((2, 21u, 22u, 23, 24u, 25u), (entry.Equips[1].Slot, entry.Equips[1].ItemId, entry.Equips[1].Blob, entry.Equips[1].Packed, entry.Equips[1].ClearAvatar, entry.Equips[1].Extra));
    }

    [Fact]
    public void A_character_entry_copies_its_inputs_and_hands_out_read_only_views()
    {
        var name = new byte[] { 1, 2, 3 };
        var equips = new List<OnlineCharacterEquip> { new OnlineCharacterEquip(1, 2, 3, 4, 5, 6) };
        var entry = new OnlineCharacterEntry(1, name, 0, 0, 0, 1, 0, 0, equips, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        name[0] = 99;
        equips.Clear();

        Assert.Equal(new byte[] { 1, 2, 3 }, entry.NameBytes.ToArray());
        Assert.Single(entry.Equips);
        Assert.True(((System.Collections.IList)entry.NameBytes).IsReadOnly);
        Assert.True(((System.Collections.IList)entry.Equips).IsReadOnly);
    }

    [Fact]
    public void A_character_entry_without_name_or_equips_has_empty_collections()
    {
        var entry = new OnlineCharacterEntry(1, null, 0, 0, 0, 1, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        Assert.Empty(entry.NameBytes);
        Assert.Empty(entry.Equips);
    }

    [Fact]
    public void A_character_list_keeps_its_header_all_entries_and_the_trailing_byte_count()
    {
        OnlineCharacterList list = ShellTestData.Roster(15);

        Assert.Equal((byte)6, list.A);
        Assert.Equal((byte)1, list.B);
        Assert.Equal((byte)2, list.C);
        Assert.Equal(77u, list.D);
        Assert.Equal(15, list.Entries.Count);
        Assert.Equal(4, list.TrailingByteCount);
        Assert.True(((System.Collections.IList)list.Entries).IsReadOnly);
        Assert.Empty(new OnlineCharacterList(0, 0, 0, 0, null, 0).Entries);
    }

    // ------------------------------------------------------------------------------------------------ 本地输入检查

    [Theory]
    [InlineData("abcd", true)]
    [InlineData("abc", false)]
    [InlineData("a_b_", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456", false)]
    [InlineData("abc d", false)]
    [InlineData("abc-d", false)]
    [InlineData("abc.d", false)]
    [InlineData("abcd名", false)]
    [InlineData("abcd\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("0000", true)]
    [InlineData("9999", true)]
    [InlineData("zzzz", true)]
    [InlineData("ZZZZ", true)]
    [InlineData("____", true)]
    [InlineData("abc@", false)]
    [InlineData("abc`", false)]
    [InlineData("abc[", false)]
    [InlineData("abc{", false)]
    [InlineData("abc/", false)]
    [InlineData("abc:", false)]
    public void Username_rules_match_the_gateway(string name, bool valid)
    {
        Assert.Equal(valid, OnlineShellValidation.IsValidUsername(name));
    }

    [Fact]
    public void Username_length_limits_are_4_and_32()
    {
        Assert.Equal(4, OnlineShellValidation.UsernameMinLength);
        Assert.Equal(32, OnlineShellValidation.UsernameMaxLength);
        Assert.Equal(OnlineShellValidation.UsernameMaxLength, LoginScreen.MaxUsernameLength);
    }

    [Theory]
    [InlineData("x", true)]
    [InlineData(" ", true)]
    [InlineData("名字", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_password_only_has_to_be_non_empty(string password, bool valid)
    {
        Assert.Equal(valid, OnlineShellValidation.IsValidPassword(password));
    }

    // ------------------------------------------------------------------------------------------------ 占位名册

    [Fact]
    public void The_placeholder_roster_is_in_list_order_with_generic_names_and_the_server_level_but_never_more_than_twelve()
    {
        OnlineCharacterList list = ShellTestData.Roster(15);

        CharacterRoster roster = OnlineShellRoster.Placeholder(list);

        Assert.Equal(CharacterRoster.Capacity, roster.Count);
        for (int i = 0; i < roster.Count; i++)
        {
            Assert.Equal("LV" + (10 + i) + " ROLE" + (i + 1), roster[i].Caption);
            Assert.Equal("Job" + i, roster[i].JobName);
        }
        Assert.Equal(15, list.Entries.Count);
        Assert.DoesNotContain("ABC", string.Join("|", roster.Slots.Select(s => s.Caption + s.JobName)));
    }

    [Fact]
    public void The_placeholder_roster_of_nothing_is_empty()
    {
        Assert.Equal(0, OnlineShellRoster.Placeholder(null).Count);
        Assert.Equal(0, OnlineShellRoster.Placeholder(ShellTestData.Roster(0)).Count);
    }

    // ------------------------------------------------------------------------------------------------ 文字表

    private static bool GlyphSafe(string text) => text.All(OverlayArt.HasGlyph);

    private static bool HasCjk(string text) => text.Any(c => c > 0x2E7F);

    [Fact]
    public void Every_label_has_english_placeholder_text_the_bitmap_font_can_draw_and_chinese_text()
    {
        foreach (OnlineShellLabel label in Enum.GetValues<OnlineShellLabel>())
        {
            string en = OnlineShellText.Label(label, false);
            string zh = OnlineShellText.Label(label, true);

            Assert.NotEmpty(en);
            Assert.True(GlyphSafe(en), label + ":英文占位必须全是点阵字库画得出的字符:" + en);
            Assert.NotEmpty(zh);
            Assert.True(HasCjk(zh), label + " 的中文里没有汉字");
            Assert.NotEqual(en, zh);
        }
        Assert.Equal(string.Empty, OnlineShellText.Label((OnlineShellLabel)999, false));
        Assert.Equal(string.Empty, OnlineShellText.Label((OnlineShellLabel)999, true));
    }

    [Fact]
    public void Chinese_labels_are_the_ones_the_task_names()
    {
        Assert.Equal("账号", OnlineShellText.Label(OnlineShellLabel.Username, true));
        Assert.Equal("密码", OnlineShellText.Label(OnlineShellLabel.Password, true));
        Assert.Equal("进入游戏", OnlineShellText.Label(OnlineShellLabel.EnterGame, true));
        Assert.Equal("注册", OnlineShellText.Label(OnlineShellLabel.Register, true));
        Assert.Equal("修改密码", OnlineShellText.Label(OnlineShellLabel.ChangePassword, true));
    }

    [Theory]
    [InlineData(OnlineShellLabel.GameTitle, "地下城与勇士")]
    [InlineData(OnlineShellLabel.LoginTitle, "账号登录")]
    [InlineData(OnlineShellLabel.LoginSubtitle, "登录后选择服务器与角色")]
    [InlineData(OnlineShellLabel.ChangePasswordTitle, "修改密码")]
    [InlineData(OnlineShellLabel.ChangePasswordSubtitle, "输入旧密码与新密码")]
    [InlineData(OnlineShellLabel.OldPassword, "旧密码")]
    [InlineData(OnlineShellLabel.NewPassword, "新密码")]
    [InlineData(OnlineShellLabel.Confirm, "确认修改")]
    [InlineData(OnlineShellLabel.Back, "返回")]
    [InlineData(OnlineShellLabel.UsernamePlaceholder, "请输入账号")]
    [InlineData(OnlineShellLabel.PasswordPlaceholder, "请输入密码")]
    [InlineData(OnlineShellLabel.OldPasswordPlaceholder, "请输入旧密码")]
    [InlineData(OnlineShellLabel.NewPasswordPlaceholder, "请输入新密码")]
    [InlineData(OnlineShellLabel.Waiting, "请稍候…")]
    [InlineData(OnlineShellLabel.ServerSelect, "正在选择服务器…")]
    [InlineData(OnlineShellLabel.ChannelConnecting, "正在连接频道…")]
    [InlineData(OnlineShellLabel.Loading, "正在进入游戏…")]
    public void The_designed_login_page_and_status_screens_say_these_chinese_words(OnlineShellLabel label, string text)
    {
        Assert.Equal(text, OnlineShellText.Label(label, true));
    }

    [Fact]
    public void The_account_rule_states_the_real_limits_in_both_languages()
    {
        string zh = OnlineShellText.Label(OnlineShellLabel.AccountRule, true);
        string en = OnlineShellText.Label(OnlineShellLabel.AccountRule, false);

        Assert.Contains(OnlineShellValidation.UsernameMinLength + " 到 " + OnlineShellValidation.UsernameMaxLength, zh);
        Assert.Contains(OnlineShellValidation.UsernameMinLength + "-" + OnlineShellValidation.UsernameMaxLength, en);
        Assert.Contains(OnlineShellValidation.UsernameMinLength + " 到 " + OnlineShellValidation.UsernameMaxLength,
            OnlineShellText.Notice(new OnlineNotice(OnlineNoticeKind.InvalidUsername), true));
    }

    private static readonly OnlineShellLabel[] HintLabels =
    {
        OnlineShellLabel.LoginHint, OnlineShellLabel.ChangePasswordHint, OnlineShellLabel.ChannelHint, OnlineShellLabel.ChannelHintCancel,
        OnlineShellLabel.LoadingHint, OnlineShellLabel.CharacterSelectHint, OnlineShellLabel.BusyHint,
    };

    [Fact]
    public void Hints_are_key_caps_with_what_the_key_does_and_the_flat_label_is_the_same_hints_in_one_line()
    {
        foreach (OnlineShellLabel label in HintLabels)
        {
            foreach (bool cjk in new[] { false, true })
            {
                ShellHint[] hints = OnlineShellText.Hints(label, cjk);

                Assert.NotEmpty(hints);
                Assert.All(hints, h => Assert.False(string.IsNullOrEmpty(h.Key) || string.IsNullOrEmpty(h.Text), label + "/" + cjk));
                Assert.Equal(string.Join("    ", hints.Select(h => h.Key + " " + h.Text)), OnlineShellText.Label(label, cjk));
                if (!cjk)
                {
                    Assert.All(hints, h => Assert.True(GlyphSafe(h.Key) && GlyphSafe(h.Text), label.ToString()));
                }
            }
        }
    }

    [Fact]
    public void Hint_keys_are_written_as_they_are_printed_on_the_keyboard()
    {
        Assert.Equal(new[] { "Tab", "Enter" }, OnlineShellText.Hints(OnlineShellLabel.LoginHint, true).Select(h => h.Key).ToArray());
        Assert.Equal(new[] { "TAB", "ENTER" }, OnlineShellText.Hints(OnlineShellLabel.LoginHint, false).Select(h => h.Key).ToArray());
        Assert.Equal(new[] { "Tab", "Enter", "Esc" }, OnlineShellText.Hints(OnlineShellLabel.ChangePasswordHint, true).Select(h => h.Key).ToArray());
        Assert.Equal(new[] { "Enter", "Esc" }, OnlineShellText.Hints(OnlineShellLabel.ChannelHint, true).Select(h => h.Key).ToArray());
        Assert.Equal(new[] { "方向键", "Enter", "Esc" }, OnlineShellText.Hints(OnlineShellLabel.CharacterSelectHint, true).Select(h => h.Key).ToArray());
        Assert.Equal(new[] { "ARROWS", "ENTER", "ESC" }, OnlineShellText.Hints(OnlineShellLabel.CharacterSelectHint, false).Select(h => h.Key).ToArray());
        ShellHint busy = Assert.Single(OnlineShellText.Hints(OnlineShellLabel.BusyHint, true));
        Assert.Equal(("Esc", "取消"), (busy.Key, busy.Text));
        Assert.Equal("Esc 取消", OnlineShellText.Label(OnlineShellLabel.BusyHint, true));
    }

    [Fact]
    public void Labels_that_are_not_hints_have_no_hint_entries()
    {
        foreach (OnlineShellLabel label in Enum.GetValues<OnlineShellLabel>().Where(l => !HintLabels.Contains(l)))
        {
            Assert.Empty(OnlineShellText.Hints(label, true));
            Assert.Empty(OnlineShellText.Hints(label, false));
        }
        Assert.Empty(OnlineShellText.Hints((OnlineShellLabel)999, true));
    }

    [Fact]
    public void Chinese_text_uses_full_width_punctuation_only()
    {
        char[] ascii = { ',', ':', ';', '!', '?', '(', ')' };
        foreach (OnlineShellLabel label in Enum.GetValues<OnlineShellLabel>())
        {
            Assert.True(OnlineShellText.Label(label, true).IndexOfAny(ascii) < 0, label.ToString());
        }
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None))
        {
            string text = OnlineShellText.Notice(new OnlineNotice(kind, seconds: 5, detail: "x"), true);
            Assert.True(text.IndexOfAny(ascii) < 0, kind + ":" + text);
        }
    }

    [Fact]
    public void Every_notice_kind_has_english_placeholder_text_the_bitmap_font_can_draw_and_chinese_text_all_different()
    {
        var english = new HashSet<string>();
        var chinese = new HashSet<string>();
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None))
        {
            var notice = new OnlineNotice(kind, seconds: 7, detail: "项");
            string en = OnlineShellText.Notice(notice, false);
            string zh = OnlineShellText.Notice(notice, true);

            Assert.NotEmpty(en);
            Assert.True(GlyphSafe(en), kind + ":" + en);
            Assert.True(HasCjk(zh), kind.ToString());
            Assert.True(english.Add(en), "英文提示重复:" + en);
            Assert.True(chinese.Add(zh), "中文提示重复:" + zh);
        }
    }

    [Fact]
    public void No_notice_or_none_kind_gives_empty_text()
    {
        Assert.Equal(string.Empty, OnlineShellText.Notice(null, true));
        Assert.Equal(string.Empty, OnlineShellText.Notice(null, false));
        Assert.Equal(string.Empty, OnlineShellText.Notice(new OnlineNotice(OnlineNoticeKind.None), true));
        Assert.Equal(string.Empty, OnlineShellText.Notice(new OnlineNotice(OnlineNoticeKind.None), false));
    }

    [Theory]
    [InlineData(OnlineNoticeKind.WrongCredentials, "账号或密码错误")]
    [InlineData(OnlineNoticeKind.AccountBanned, "该账号已被封停")]
    [InlineData(OnlineNoticeKind.NetworkError, "网络错误或连接超时")]
    [InlineData(OnlineNoticeKind.ProtocolError, "服务器响应异常")]
    [InlineData(OnlineNoticeKind.NoGameServer, "服务器没有配置游戏服地址")]
    [InlineData(OnlineNoticeKind.InvalidPassword, "密码不能为空")]
    [InlineData(OnlineNoticeKind.RegistrationClosed, "暂不开放注册")]
    [InlineData(OnlineNoticeKind.UserExists, "账号已存在")]
    [InlineData(OnlineNoticeKind.Disconnected, "与服务器的连接已断开")]
    [InlineData(OnlineNoticeKind.Cancelled, "已取消")]
    public void The_chinese_failure_categories_say_what_happened(OnlineNoticeKind kind, string text)
    {
        Assert.Equal(text, OnlineShellText.Notice(new OnlineNotice(kind), true));
    }

    [Theory]
    [InlineData(12, "请求过于频繁，请 12 秒后再试", "TOO MANY REQUESTS   RETRY IN 12 S")]
    [InlineData(1, "请求过于频繁，请 1 秒后再试", "TOO MANY REQUESTS   RETRY IN 1 S")]
    [InlineData(0, "请求过于频繁，请 1 秒后再试", "TOO MANY REQUESTS   RETRY IN 1 S")]
    public void The_rate_limit_text_counts_down_in_seconds_and_never_shows_zero(int seconds, string zh, string en)
    {
        var notice = new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: seconds);

        Assert.Equal(zh, OnlineShellText.Notice(notice, true));
        Assert.Equal(en, OnlineShellText.Notice(notice, false));
    }

    [Fact]
    public void The_not_configured_text_names_the_item_only_in_chinese()
    {
        var withDetail = new OnlineNotice(OnlineNoticeKind.NotConfigured, detail: "缺少 server_url");
        var without = new OnlineNotice(OnlineNoticeKind.NotConfigured);
        var empty = new OnlineNotice(OnlineNoticeKind.NotConfigured, detail: string.Empty);

        Assert.Equal("未配置连接：缺少 server_url", OnlineShellText.Notice(withDetail, true));
        Assert.Equal("未配置连接", OnlineShellText.Notice(without, true));
        Assert.Equal("未配置连接", OnlineShellText.Notice(empty, true));
        Assert.Equal("NOT CONFIGURED", OnlineShellText.Notice(withDetail, false));
        Assert.Equal("NOT CONFIGURED", OnlineShellText.Notice(without, false));
    }

    [Fact]
    public void Notice_text_shows_nothing_but_the_category_the_seconds_and_the_named_item()
    {
        // 提示文字里只有固定话术、秒数与点名的配置项:不会出现类别短码或别的数据
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None && k != OnlineNoticeKind.RateLimited && k != OnlineNoticeKind.NotConfigured))
        {
            var withReason = new OnlineNotice(kind, "secret_reason", 99, "secret-detail");
            Assert.DoesNotContain("secret", OnlineShellText.Notice(withReason, true));
            Assert.DoesNotContain("SECRET", OnlineShellText.Notice(withReason, false));
            Assert.DoesNotContain("99", OnlineShellText.Notice(withReason, true));
            Assert.DoesNotContain("99", OnlineShellText.Notice(withReason, false));
        }
    }

    [Fact]
    public void The_english_hint_lines_fit_the_screen_width_at_one_times_scale()
    {
        foreach (OnlineShellLabel label in new[]
        {
            OnlineShellLabel.LoginHint, OnlineShellLabel.ChangePasswordHint, OnlineShellLabel.Waiting, OnlineShellLabel.ChannelHint,
            OnlineShellLabel.CharacterSelectHint, OnlineShellLabel.LoadingHint, OnlineShellLabel.ChannelHintCancel,
        })
        {
            Assert.True(OverlayArt.TextWidth(OnlineShellText.Label(label, false), 1) < 800, label.ToString());
        }
    }
}
