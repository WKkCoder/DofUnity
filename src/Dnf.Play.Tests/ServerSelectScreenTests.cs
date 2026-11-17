using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>选服务器界面的内容状态、方向键、点击与热区(像素级)、滚动、画面构成(合成数据,不用原版素材)。</summary>
public sealed class ServerSelectScreenTests
{
    private static DirectoryServer Ok(string name = "希洛克", int channels = 2) => PickerData.Server(name, channels);

    private static DirectoryServer Gray(string name = "卡恩") => new DirectoryServer(name, null);

    private static ServerSelectScreen Ready(ServerDirectory directory, int select = 0, ShellFakeSprites art = null)
    {
        var screen = new ServerSelectScreen(art == null ? null : new ServerPickerScene(art));
        screen.SetDirectory(directory, select);
        return screen;
    }

    private static ServerSelectScreen ReadyMany(int servers, ShellFakeSprites art = null)
    {
        var list = new List<DirectoryServer>();
        for (int i = 0; i < servers; i++)
        {
            list.Add(Ok("服务器" + (i + 1)));
        }
        return Ready(PickerData.Directory(list.ToArray()), 0, art);
    }

    private static bool ClickCenter(ServerSelectScreen screen, HitRect rect) => screen.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static HitRect EntryRect(ServerSelectScreen screen, int index)
    {
        Assert.True(screen.TryEntryRect(index, out HitRect rect));
        return rect;
    }

    // ------------------------------------------------------------------ 内容

    [Fact]
    public void A_new_screen_is_loading_with_nothing_chosen()
    {
        var screen = new ServerSelectScreen();
        Assert.Equal(PickerPhase.Loading, screen.Phase);
        Assert.Equal(-1, screen.Selected);
        Assert.Null(screen.SelectedServer);
        Assert.False(screen.CanStart);
        Assert.Empty(screen.Servers);
        screen.Confirm();
        screen.Move(1);
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());
        Assert.Equal(-1, screen.Selected);
    }

    [Fact]
    public void A_directory_selects_the_first_server_and_enables_start()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("A"), Ok("B")));
        Assert.Equal(PickerPhase.Ready, screen.Phase);
        Assert.Equal(0, screen.Selected);
        Assert.Equal("A", screen.SelectedServer.Name);
        Assert.Equal(0, screen.ScrollRow);
        Assert.True(screen.CanStart);
        Assert.Equal(2, screen.Servers.Count);
        Assert.Equal(1, screen.RowCount);
    }

    [Fact]
    public void An_empty_directory_has_nothing_to_choose_and_confirm_means_retry()
    {
        var screen = new ServerSelectScreen();
        screen.SetDirectory(ServerDirectory.Empty);
        Assert.Equal(PickerPhase.Empty, screen.Phase);
        Assert.Equal(-1, screen.Selected);
        Assert.Equal(0, screen.ScrollRow);
        Assert.False(screen.CanStart);
        screen.Confirm();
        Assert.Equal(ServerSelectAction.Retry, screen.TakeAction());
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());
    }

    [Fact]
    public void A_null_directory_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerSelectScreen().SetDirectory(null));
    }

    [Fact]
    public void The_preselected_index_is_clamped_and_servers_without_channels_are_skipped()
    {
        ServerDirectory mixed = PickerData.Directory(Gray("a"), Ok("b"), Gray("c"), Ok("d"));
        Assert.Equal(1, Ready(mixed, 0).Selected);       // 第一个灰,往后找
        Assert.Equal(1, Ready(mixed, 1).Selected);
        Assert.Equal(3, Ready(mixed, 2).Selected);       // 灰的,往后找
        Assert.Equal(3, Ready(mixed, 3).Selected);
        Assert.Equal(3, Ready(mixed, 99).Selected);      // 超出范围夹到最后一个
        Assert.Equal(1, Ready(mixed, -5).Selected);      // 负数夹到第一个,再往后找

        ServerDirectory tail = PickerData.Directory(Ok("a"), Gray("b"), Gray("c"));
        Assert.Equal(0, Ready(tail, 2).Selected);        // 往后没有,往前找
    }

    [Fact]
    public void A_directory_of_only_empty_servers_leaves_nothing_selected_and_start_disabled()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Gray("a"), Gray("b")));
        Assert.Equal(PickerPhase.Ready, screen.Phase);
        Assert.Equal(-1, screen.Selected);
        Assert.False(screen.CanStart);
        screen.Confirm();
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());
        screen.Move(1);
        Assert.Equal(-1, screen.Selected);
    }

    [Fact]
    public void Loading_again_keeps_the_list_and_the_selection()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("a"), Ok("b"), Ok("c")), 2);
        screen.SetLoading();
        Assert.Equal(PickerPhase.Loading, screen.Phase);
        Assert.Equal(3, screen.Servers.Count);
        Assert.Equal(2, screen.Selected);
        Assert.False(screen.CanStart);
    }

    [Fact]
    public void Any_state_change_drops_a_pending_action()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok(), Ok("b")));
        screen.Confirm();
        screen.SetLoading();
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());

        screen.SetDirectory(PickerData.Directory(Ok()));
        screen.Confirm();
        screen.SetFailed();
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());

        screen.SetDirectory(PickerData.Directory(Ok()));
        screen.Confirm();
        screen.SetDirectory(PickerData.Directory(Ok("b")));
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());
    }

    [Fact]
    public void A_notice_is_kept_until_replaced_or_a_directory_arrives()
    {
        var screen = new ServerSelectScreen();
        var notice = new OnlineNotice(OnlineNoticeKind.NetworkError);
        screen.SetNotice(notice);
        Assert.Same(notice, screen.Notice);
        screen.SetFailed();
        Assert.Same(notice, screen.Notice);
        screen.SetDirectory(PickerData.Directory(Ok()));
        Assert.Null(screen.Notice);
        screen.SetNotice(notice);
        screen.SetNotice(null);
        Assert.Null(screen.Notice);
    }

    [Fact]
    public void Time_advances_by_whole_milliseconds_and_ignores_negative_steps()
    {
        var screen = new ServerSelectScreen();
        Assert.Equal(0, screen.AgeMs);
        screen.Advance(100.7);
        Assert.Equal(100, screen.AgeMs);
        screen.Advance(-50);
        screen.Advance(0);
        Assert.Equal(100, screen.AgeMs);
        screen.Advance(900);
        Assert.Equal(1000, screen.AgeMs);
    }

    // ------------------------------------------------------------------ 方向键与动作

    [Fact]
    public void Arrow_keys_walk_the_two_column_grid_without_wrapping_and_skip_gray_entries()
    {
        // 排法:  0 1 / 2(灰) 3 / 4 5
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("a"), Ok("b"), Gray("c"), Ok("d"), Ok("e"), Ok("f")));
        Assert.Equal(0, screen.Selected);
        screen.Move(1);
        Assert.Equal(1, screen.Selected);
        screen.Move(1);
        Assert.Equal(3, screen.Selected);                // 越过灰的 2
        screen.Move(1);
        Assert.Equal(4, screen.Selected);
        screen.Move(0, 1);
        Assert.Equal(4, screen.Selected);                // 下面没有了,不动
        screen.Move(0, -1);
        Assert.Equal(0, screen.Selected);                // 同一列往上:2 是灰的,越过,落在 0
    }

    [Fact]
    public void Down_and_up_stay_in_their_column()
    {
        // 排法:  0 1 / 2 3 / 4(灰) 5
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("a"), Ok("b"), Ok("c"), Ok("d"), Gray("e"), Ok("f")));
        screen.Move(0, 1);
        Assert.Equal(2, screen.Selected);
        screen.Move(0, 1);
        Assert.Equal(2, screen.Selected);                // 下面是灰的 4,再下面没有了
        screen.Move(0, -1);
        Assert.Equal(0, screen.Selected);
        screen.Move(0, -1);
        Assert.Equal(0, screen.Selected);                // 到顶不循环
        screen.Move(1);
        screen.Move(0, 1);
        screen.Move(0, 1);
        Assert.Equal(5, screen.Selected);                // 右列:1 → 3 → 5
        screen.Move(-1);
        Assert.Equal(3, screen.Selected);                // 左一格:5 → 4 灰 → 3
    }

    [Fact]
    public void Moving_left_at_the_first_server_or_right_at_the_last_does_not_wrap()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("a"), Ok("b"), Ok("c")));
        screen.Move(-1);
        Assert.Equal(0, screen.Selected);
        screen.Move(1);
        screen.Move(1);
        Assert.Equal(2, screen.Selected);
        screen.Move(1);
        Assert.Equal(2, screen.Selected);
        screen.Move(0, 0);
        Assert.Equal(2, screen.Selected);
    }

    [Fact]
    public void Moving_is_ignored_unless_the_directory_is_ready()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("a"), Ok("b")));
        screen.SetFailed();
        screen.Move(1);
        Assert.Equal(0, screen.Selected);
        screen.SetLoading();
        screen.Move(1);
        Assert.Equal(0, screen.Selected);
    }

    [Fact]
    public void Confirm_chooses_only_when_the_start_button_is_live_and_the_action_is_taken_once()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok()));
        screen.Confirm();
        Assert.Equal(ServerSelectAction.Choose, screen.TakeAction());
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());

        screen.SetLoading();
        screen.Confirm();
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());

        screen.SetFailed();
        screen.Confirm();
        Assert.Equal(ServerSelectAction.Retry, screen.TakeAction());
    }

    [Fact]
    public void Back_is_always_available()
    {
        var screen = new ServerSelectScreen();
        screen.Back();
        Assert.Equal(ServerSelectAction.Back, screen.TakeAction());
        screen.SetDirectory(PickerData.Directory(Ok()));
        screen.Back();
        Assert.Equal(ServerSelectAction.Back, screen.TakeAction());
        screen.SetFailed();
        screen.Back();
        Assert.Equal(ServerSelectAction.Back, screen.TakeAction());
        screen.SetDirectory(ServerDirectory.Empty);
        screen.Back();
        Assert.Equal(ServerSelectAction.Back, screen.TakeAction());
    }

    [Fact]
    public void Retry_and_direct_connect_exist_only_when_the_directory_failed_or_is_empty()
    {
        var screen = new ServerSelectScreen();
        screen.Retry();
        screen.DirectConnect();
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());          // 读取中

        screen.SetDirectory(PickerData.Directory(Ok()));
        screen.Retry();
        screen.DirectConnect();
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());          // 有内容

        screen.SetFailed();
        screen.Retry();
        Assert.Equal(ServerSelectAction.Retry, screen.TakeAction());
        screen.DirectConnect();
        Assert.Equal(ServerSelectAction.DirectConnect, screen.TakeAction());

        screen.SetDirectory(ServerDirectory.Empty);
        screen.Retry();
        Assert.Equal(ServerSelectAction.Retry, screen.TakeAction());
        screen.DirectConnect();
        Assert.Equal(ServerSelectAction.DirectConnect, screen.TakeAction());
    }

    // ------------------------------------------------------------------ 点击与热区

    [Fact]
    public void Entry_rectangles_follow_the_baseline_grid()
    {
        ServerSelectScreen screen = ReadyMany(6);
        HitRect first = EntryRect(screen, 0);
        Assert.Equal((332, 214, 170, 41), (first.X, first.Y, first.Width, first.Height));
        HitRect second = EntryRect(screen, 1);
        Assert.Equal((512, 214), (second.X, second.Y));
        HitRect third = EntryRect(screen, 2);
        Assert.Equal((332, 260), (third.X, third.Y));
        HitRect sixth = EntryRect(screen, 5);
        Assert.Equal((512, 214 + 46 * 2), (sixth.X, sixth.Y));
        Assert.False(screen.TryEntryRect(6, out _));
        Assert.False(screen.TryEntryRect(-1, out _));
    }

    [Fact]
    public void Clicking_an_entry_selects_it_to_the_pixel_and_gray_entries_swallow_the_click()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("a"), Ok("b"), Gray("c")));
        Assert.True(screen.Click(512, 214));             // 第二个入口的左上角
        Assert.Equal(1, screen.Selected);
        Assert.True(screen.Click(332, 214));
        Assert.Equal(0, screen.Selected);
        Assert.True(screen.Click(501, 254));             // 第一个入口的右下角
        Assert.Equal(0, screen.Selected);

        Assert.False(screen.Click(331, 214));            // 左边一格
        Assert.False(screen.Click(332, 213));            // 上边一格
        Assert.False(screen.Click(502, 214));            // 两个入口之间的缝
        Assert.False(screen.Click(332, 255));            // 行与行之间的缝
        Assert.False(screen.Click(0, 0));

        Assert.True(screen.Click(335, 262));             // 第三个入口(灰):吃掉这一下,选中不变
        Assert.Equal(0, screen.Selected);
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());
    }

    [Fact]
    public void The_start_button_hot_area_is_exactly_its_baseline_rectangle()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok()));
        Assert.True(screen.Click(337, 526));
        Assert.Equal(ServerSelectAction.Choose, screen.TakeAction());
        Assert.True(screen.Click(461, 568));             // 右下角像素
        Assert.Equal(ServerSelectAction.Choose, screen.TakeAction());
        Assert.False(screen.Click(336, 540));
        Assert.False(screen.Click(462, 540));
        Assert.False(screen.Click(400, 525));
        Assert.False(screen.Click(400, 569));
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());
    }

    [Fact]
    public void A_disabled_start_button_swallows_the_click_and_does_nothing()
    {
        var loading = new ServerSelectScreen();
        Assert.True(loading.Click(400, 540));
        Assert.Equal(ServerSelectAction.None, loading.TakeAction());

        ServerSelectScreen nothing = Ready(PickerData.Directory(Gray("a")));
        Assert.True(nothing.Click(400, 540));
        Assert.Equal(ServerSelectAction.None, nothing.TakeAction());

        ServerSelectScreen failed = Ready(PickerData.Directory(Ok()));
        failed.SetFailed();
        Assert.True(failed.Click(400, 540));
        Assert.Equal(ServerSelectAction.None, failed.TakeAction());
    }

    [Fact]
    public void Entries_are_not_clickable_while_loading_or_failed()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("a"), Ok("b")));
        screen.SetLoading();
        Assert.False(screen.Click(520, 220));
        Assert.Equal(0, screen.Selected);
        screen.SetFailed();
        Assert.False(screen.Click(520, 220));
        Assert.Equal(0, screen.Selected);
    }

    [Fact]
    public void The_retry_and_default_buttons_appear_only_when_failed_or_empty()
    {
        HitRect retry = ServerSelectScreen.RetryRect();
        HitRect direct = ServerSelectScreen.DirectRect();
        Assert.Equal((424, 372, 77, 23), (retry.X, retry.Y, retry.Width, retry.Height));
        Assert.Equal((513, 372, 77, 23), (direct.X, direct.Y, direct.Width, direct.Height));

        ServerSelectScreen screen = Ready(PickerData.Directory(Ok()));
        Assert.False(ClickCenter(screen, retry));                     // 有内容时这里没有按钮
        Assert.False(ClickCenter(screen, direct));
        Assert.Equal(ServerSelectAction.None, screen.TakeAction());

        screen.SetLoading();
        Assert.False(ClickCenter(screen, retry));

        screen.SetFailed();
        Assert.True(ClickCenter(screen, retry));
        Assert.Equal(ServerSelectAction.Retry, screen.TakeAction());
        Assert.True(ClickCenter(screen, direct));
        Assert.Equal(ServerSelectAction.DirectConnect, screen.TakeAction());
        Assert.False(screen.Click(retry.X - 1, retry.Y + 5));
        Assert.False(screen.Click(direct.X + direct.Width, direct.Y + 5));

        screen.SetDirectory(ServerDirectory.Empty);
        Assert.True(ClickCenter(screen, retry));
        Assert.Equal(ServerSelectAction.Retry, screen.TakeAction());
        Assert.True(ClickCenter(screen, direct));
        Assert.Equal(ServerSelectAction.DirectConnect, screen.TakeAction());
    }

    // ------------------------------------------------------------------ 滚动

    [Fact]
    public void More_than_ten_servers_scroll_and_the_selection_always_stays_visible()
    {
        ServerSelectScreen screen = ReadyMany(12);
        Assert.Equal(6, screen.RowCount);
        Assert.Equal(0, screen.ScrollRow);
        Assert.True(screen.TryEntryRect(0, out _));
        Assert.False(screen.TryEntryRect(10, out _));                 // 第 6 行还在可见区外

        for (int i = 0; i < 5; i++)
        {
            screen.Move(0, 1);
        }
        Assert.Equal(10, screen.Selected);
        Assert.Equal(1, screen.ScrollRow);
        Assert.False(screen.TryEntryRect(0, out _));
        HitRect rect = EntryRect(screen, 10);
        Assert.Equal((332, 214 + 46 * 4), (rect.X, rect.Y));          // 滚了一行,第 6 行落到第 5 行的位置

        screen.Move(0, -1);
        screen.Move(0, -1);
        screen.Move(0, -1);
        screen.Move(0, -1);
        Assert.Equal(2, screen.Selected);
        Assert.Equal(1, screen.ScrollRow);                            // 第 2 行(从 0 起为 1)仍在可见区里,不动
        screen.Move(0, -1);
        Assert.Equal(0, screen.Selected);
        Assert.Equal(0, screen.ScrollRow);
    }

    [Fact]
    public void A_preselected_server_below_the_fold_is_scrolled_into_view()
    {
        ServerSelectScreen screen = Ready(PickerData.Directory(Enumerable.Range(1, 12).Select(i => Ok("服务器" + i)).ToArray()), 11);
        Assert.Equal(11, screen.Selected);
        Assert.Equal(1, screen.ScrollRow);
        Assert.True(screen.TryEntryRect(11, out _));
    }

    [Fact]
    public void The_scroll_arrows_move_one_row_and_clamp()
    {
        ServerSelectScreen screen = ReadyMany(12);
        Assert.True(ClickCenter(screen, ServerSelectScreen.ScrollDownRect()));
        Assert.Equal(1, screen.ScrollRow);
        Assert.True(ClickCenter(screen, ServerSelectScreen.ScrollDownRect()));
        Assert.Equal(1, screen.ScrollRow);                            // 已经到底
        Assert.True(ClickCenter(screen, ServerSelectScreen.ScrollUpRect()));
        Assert.Equal(0, screen.ScrollRow);
        Assert.True(ClickCenter(screen, ServerSelectScreen.ScrollUpRect()));
        Assert.Equal(0, screen.ScrollRow);
        Assert.Equal(0, screen.Selected);                             // 滚动不动选中项
    }

    [Fact]
    public void Without_overflow_there_are_no_scroll_arrows_to_click()
    {
        ServerSelectScreen screen = ReadyMany(10);
        Assert.Equal(5, screen.RowCount);
        Assert.False(ClickCenter(screen, ServerSelectScreen.ScrollDownRect()));
        Assert.False(ClickCenter(screen, ServerSelectScreen.ScrollUpRect()));
        Assert.Equal(0, screen.ScrollRow);
    }

    [Fact]
    public void Scroll_arrow_rectangles_are_where_they_were_designed()
    {
        HitRect up = ServerSelectScreen.ScrollUpRect();
        HitRect down = ServerSelectScreen.ScrollDownRect();
        Assert.Equal((684, 212, 14, 22), (up.X, up.Y, up.Width, up.Height));
        Assert.Equal((684, 417, 14, 22), (down.X, down.Y, down.Width, down.Height));
    }

    // ------------------------------------------------------------------ 画面

    [Fact]
    public void The_scene_places_every_piece_at_its_baseline_position()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen screen = Ready(PickerData.Directory(PickerData.Server("希洛克", 3, 1, ChannelCongestion.Smooth, true), Ok("卡恩")), 0, art);
        screen.Advance(750);
        long age = screen.AgeMs;
        List<SceneSprite> scene = screen.Build(0);

        SceneSprite background = PickerSprites.One(scene, "server-background");
        Assert.Equal((0, 0), (background.ScreenX, background.ScreenY));
        SceneSprite board = PickerSprites.One(scene, "server-board");
        Assert.Equal((88, 114, 104, 90), (board.ScreenX, board.ScreenY, board.Frame.Width, board.Frame.Height));
        SceneSprite panel = PickerSprites.One(scene, "server-panel");
        Assert.Equal((102, 126, 287, 170), (panel.ScreenX, panel.ScreenY, panel.Frame.Width, panel.Frame.Height));

        SceneSprite illustration = PickerSprites.One(scene, "server-illustration");
        Assert.Equal((106, 129), (illustration.ScreenX, illustration.ScreenY));
        Assert.True(PickerSprites.IsFrame(illustration, art, ServerArt.BackgroundImage, 4));

        SceneSprite banner = PickerSprites.One(scene, "picker-banner");
        Assert.Equal((273, 73), (banner.ScreenX, banner.ScreenY));
        Assert.True(PickerSprites.IsFrame(banner, art, ServerArt.ChannelBaseImage, 1));
        SceneSprite label = PickerSprites.One(scene, "picker-banner-label");
        Assert.Equal((344, 72), (label.ScreenX, label.ScreenY));
        Assert.True(PickerSprites.IsFrame(label, art, ServerArt.ChannelBackgroundImage, 7));

        SceneSprite title = PickerSprites.One(scene, "server-title");
        Assert.Equal((345, 33), (title.ScreenX, title.ScreenY));
        Assert.True(PickerSprites.IsFrame(title, art, ServerArt.BaseImage, 6));
        SceneSprite heading = PickerSprites.One(scene, "server-heading");
        Assert.Equal((388, 138), (heading.ScreenX, heading.ScreenY));
        Assert.True(PickerSprites.IsFrame(heading, art, ServerArt.BaseImage, 0));
        Assert.Equal(SpriteBlend.Normal, heading.Blend);
        SceneSprite headingGlow = PickerSprites.One(scene, "server-heading-glow");
        Assert.Equal((388, 138), (headingGlow.ScreenX, headingGlow.ScreenY));
        Assert.True(PickerSprites.IsFrame(headingGlow, art, ServerArt.BaseImage, 1));
        Assert.Equal(SpriteBlend.Additive, headingGlow.Blend);
        Assert.Equal(PickerArt.TitleGlowTint(age), headingGlow.TintAlpha);
        Assert.True(headingGlow.SortKey > heading.SortKey);          // 光晕盖在字上面
        SceneSprite rule = PickerSprites.One(scene, "server-rule");
        Assert.Equal((346, 202), (rule.ScreenX, rule.ScreenY));
        Assert.Equal(SpriteBlend.Additive, rule.Blend);
        Assert.Equal(255, rule.TintAlpha);
        SceneSprite subtitle = PickerSprites.One(scene, "server-subtitle");
        Assert.Equal((438, 187), (subtitle.ScreenX, subtitle.ScreenY));

        SceneSprite entry = PickerSprites.One(scene, "server-entry");
        Assert.Equal((332, 214), (entry.ScreenX, entry.ScreenY));
        Assert.True(PickerSprites.IsFrame(entry, art, ServerArt.SlotImage, 8));
        SceneSprite entryGlow = PickerSprites.One(scene, "server-entry-glow");
        Assert.Equal((332, 214), (entryGlow.ScreenX, entryGlow.ScreenY));
        Assert.Equal(SpriteBlend.Additive, entryGlow.Blend);
        SceneSprite eventMark = PickerSprites.One(scene, "server-entry-event");
        Assert.Equal((465, 241), (eventMark.ScreenX, eventMark.ScreenY));
        Assert.True(PickerSprites.IsFrame(eventMark, art, ServerArt.BaseImage, 3));
        SceneSprite eventGlow = PickerSprites.One(scene, "server-entry-event-glow");
        Assert.Equal((465, 241), (eventGlow.ScreenX, eventGlow.ScreenY));
        Assert.Equal(SpriteBlend.Additive, eventGlow.Blend);
        Assert.Equal(PickerArt.TitleGlowTint(age), eventGlow.TintAlpha);
        Assert.True(eventMark.SortKey > eventGlow.SortKey);           // 角标在它的光上面

        // 没有素材的第二个服务器用程序板
        SceneSprite plate = PickerSprites.One(scene, "server-entry-plate");
        Assert.Equal((512, 214), (plate.ScreenX, plate.ScreenY));

        SceneSprite start = PickerSprites.One(scene, "server-start");
        Assert.Equal((337, 526), (start.ScreenX, start.ScreenY));
        Assert.True(PickerSprites.IsFrame(start, art, CharacterSelectArt.UiImage, CharacterSelectArt.StartNormalFrame));
        SceneSprite startGlow = PickerSprites.One(scene, "server-start-glow");
        Assert.Equal((337, 526), (startGlow.ScreenX, startGlow.ScreenY));
        Assert.Equal(SpriteBlend.Additive, startGlow.Blend);
        Assert.Equal(PickerArt.StartGlowTint(age), startGlow.TintAlpha);
        Assert.True(start.SortKey > entry.SortKey);

        // 排序键严格递增:列表顺序就是画的顺序
        for (int i = 1; i < scene.Count; i++)
        {
            Assert.True(scene[i].SortKey > scene[i - 1].SortKey);
        }
    }

    [Fact]
    public void The_event_mark_is_drawn_only_for_servers_flagged_with_it()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen plain = Ready(PickerData.Directory(Ok("希洛克")), 0, art);
        Assert.False(PickerRig.Has(plain.Build(0), "server-entry-event"));
        Assert.False(PickerRig.Has(plain.Build(0), "server-entry-event-glow"));
        ServerSelectScreen marked = Ready(PickerData.Directory(PickerData.Server("希洛克", 1, 1, ChannelCongestion.Smooth, true)), 0, art);
        Assert.True(PickerRig.Has(marked.Build(0), "server-entry-event"));
    }

    [Fact]
    public void The_start_button_picks_its_frame_from_the_state_and_only_glows_when_live()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("希洛克")), 0, art);
        screen.Advance(500);

        List<SceneSprite> normal = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(normal, "server-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartNormalFrame));
        Assert.Equal(PickerArt.StartGlowTint(500), PickerSprites.One(normal, "server-start-glow").TintAlpha);

        screen.Hover(400, 540);
        List<SceneSprite> hover = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(hover, "server-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartHoverFrame));
        Assert.Equal(PickerArt.GlowHigh, PickerSprites.One(hover, "server-start-glow").TintAlpha);
        screen.HoverNone();

        screen.SetLoading();
        List<SceneSprite> disabled = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(disabled, "server-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartDisabledFrame));
        Assert.False(PickerRig.Has(disabled, "server-start-glow"));
    }

    [Fact]
    public void Clicking_start_shows_the_pressed_frame_for_140_milliseconds()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("希洛克")), 0, art);
        screen.Click(400, 540);
        Assert.Equal(ServerSelectAction.Choose, screen.TakeAction());

        List<SceneSprite> pressed = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(pressed, "server-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartPressedFrame));
        Assert.False(PickerRig.Has(pressed, "server-start-glow"));

        screen.Advance(139);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "server-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartPressedFrame));
        screen.Advance(1);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "server-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartNormalFrame));
    }

    [Fact]
    public void Clicking_a_disabled_start_never_shows_the_pressed_frame()
    {
        ShellFakeSprites art = PickerSprites.Full();
        var screen = new ServerSelectScreen(new ServerPickerScene(art));
        screen.Click(400, 540);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "server-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartDisabledFrame));
    }

    [Fact]
    public void Hovering_an_unselected_selectable_entry_lights_it_but_selected_and_gray_ones_do_not()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("希洛克"), Ok("卡恩"), Gray("普雷")), 0, art);

        screen.Hover(520, 220);                                       // 第二个(可选、没选中)
        SceneSprite hover = PickerSprites.One(screen.Build(0), "server-entry-hover");
        Assert.Equal((512, 214), (hover.ScreenX, hover.ScreenY));
        Assert.Equal(SpriteBlend.Additive, hover.Blend);
        Assert.Equal(110, hover.TintAlpha);

        screen.Hover(340, 220);                                       // 选中的那个:已经有选中光
        Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"));
        screen.Hover(340, 265);                                       // 灰的
        Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"));
        screen.Hover(520, 220);
        screen.HoverNone();
        Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"));
    }

    [Fact]
    public void Without_art_the_screen_falls_back_to_a_solid_backdrop_and_plates()
    {
        var screen = new ServerSelectScreen();
        screen.SetDirectory(PickerData.Directory(Ok("希洛克"), Gray("卡恩")));
        List<SceneSprite> scene = screen.Build(0);
        SceneSprite backdrop = PickerSprites.One(scene, "server-background-backdrop");
        Assert.Equal((800, 600), (backdrop.Frame.Width, backdrop.Frame.Height));
        Assert.False(PickerRig.Has(scene, "server-board"));
        Assert.Equal(2, PickerRig.Count(scene, "server-entry-plate"));
        Assert.Equal(2, PickerRig.Count(scene, "server-entry-name"));
        Assert.True(PickerRig.Has(scene, "server-entry-note"));        // 灰牌上的"无频道"
        Assert.True(PickerRig.Has(scene, "server-entry-select"));      // 选中光缺素材时画程序描边
        Assert.True(PickerRig.Has(scene, "server-start"));
        Assert.False(PickerRig.Has(scene, "server-start-glow"));
    }

    [Fact]
    public void A_gray_entry_with_art_but_no_gray_version_is_dimmed()
    {
        ShellFakeSprites art = PickerSprites.Full();
        art.Frames[(ServerArt.SlotImage, 4)] = PickerSprites.Block(170, 41, 0, 0);     // 卡恩:只有彩色版(灰牌帧为 -1)
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("希洛克"), Gray("卡恩")), 0, art);
        List<SceneSprite> scene = screen.Build(0);
        Assert.True(PickerRig.Has(scene, "server-entry-gray"));
        Assert.True(PickerRig.Has(scene, "server-entry-dim"));
        Assert.False(PickerRig.Has(scene, "server-entry-note"));
    }

    [Fact]
    public void A_gray_entry_with_its_own_gray_art_is_not_dimmed()
    {
        ShellFakeSprites art = PickerSprites.Full();
        art.Frames[(ServerArt.SlotImage, 4)] = PickerSprites.Block(170, 41, 0, 0);
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("卡恩"), Gray("希洛克")), 0, art);
        List<SceneSprite> scene = screen.Build(0);
        SceneSprite gray = PickerSprites.One(scene, "server-entry-gray");
        Assert.True(PickerSprites.IsFrame(gray, art, ServerArt.SlotImage, 9));         // 希洛克的灰牌
        Assert.False(PickerRig.Has(scene, "server-entry-dim"));
    }

    [Fact]
    public void Each_phase_shows_its_message_and_the_right_extras()
    {
        var loading = new ServerSelectScreen();
        List<SceneSprite> sceneLoading = loading.Build(0);
        Assert.True(PickerRig.Has(sceneLoading, "server-spinner"));
        Assert.True(PickerRig.Has(sceneLoading, "server-message"));
        Assert.False(PickerRig.Has(sceneLoading, "server-retry"));
        Assert.False(PickerRig.Has(sceneLoading, "server-entry-plate"));

        var empty = new ServerSelectScreen();
        empty.SetDirectory(ServerDirectory.Empty);
        List<SceneSprite> sceneEmpty = empty.Build(0);
        Assert.True(PickerRig.Has(sceneEmpty, "server-message"));
        Assert.False(PickerRig.Has(sceneEmpty, "server-spinner"));
        Assert.True(PickerRig.Has(sceneEmpty, "server-retry"));
        Assert.True(PickerRig.Has(sceneEmpty, "server-direct"));

        var failed = new ServerSelectScreen();
        failed.SetFailed();
        List<SceneSprite> sceneFailed = failed.Build(0);
        Assert.True(PickerRig.Has(sceneFailed, "server-message"));
        Assert.True(PickerRig.Has(sceneFailed, "server-retry"));
        Assert.True(PickerRig.Has(sceneFailed, "server-direct"));
        SceneSprite retry = PickerSprites.One(sceneFailed, "server-retry");
        Assert.Equal((424, 372), (retry.ScreenX, retry.ScreenY));
        SceneSprite direct = PickerSprites.One(sceneFailed, "server-direct");
        Assert.Equal((513, 372), (direct.ScreenX, direct.ScreenY));

        ServerSelectScreen ready = Ready(PickerData.Directory(Ok()));
        List<SceneSprite> sceneReady = ready.Build(0);
        Assert.False(PickerRig.Has(sceneReady, "server-message"));
        Assert.False(PickerRig.Has(sceneReady, "server-retry"));
    }

    [Fact]
    public void A_notice_shows_a_bar_lower_when_there_is_content_and_higher_when_failed()
    {
        var failed = new ServerSelectScreen();
        failed.SetFailed();
        failed.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
        SceneSprite failedBar = PickerSprites.One(failed.Build(0), "server-notice-bar");
        Assert.Equal(330, failedBar.ScreenY);

        ServerSelectScreen ready = Ready(PickerData.Directory(Ok()));
        ready.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
        SceneSprite readyBar = PickerSprites.One(ready.Build(0), "server-notice-bar");
        Assert.Equal(446, readyBar.ScreenY);

        ServerSelectScreen none = Ready(PickerData.Directory(Ok()));
        Assert.False(PickerRig.Has(none.Build(0), "server-notice-bar"));
    }

    [Fact]
    public void Scroll_arrows_are_drawn_only_when_there_is_overflow_and_the_dead_end_is_dimmed()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen few = ReadyMany(10, art);
        Assert.False(PickerRig.Has(few.Build(0), "server-scroll-up"));

        ServerSelectScreen many = ReadyMany(12, art);
        List<SceneSprite> top = many.Build(0);
        SceneSprite up = PickerSprites.One(top, "server-scroll-up");
        Assert.Equal((686, 214), (up.ScreenX, up.ScreenY));
        SceneSprite down = PickerSprites.One(top, "server-scroll-down");
        Assert.Equal((686, 424), (down.ScreenX, down.ScreenY));
        Assert.True(PickerRig.Has(top, "server-scroll-up-dim"));       // 在顶:向上灰
        Assert.False(PickerRig.Has(top, "server-scroll-down-dim"));

        ClickCenter(many, ServerSelectScreen.ScrollDownRect());
        List<SceneSprite> bottom = many.Build(0);
        Assert.False(PickerRig.Has(bottom, "server-scroll-up-dim"));
        Assert.True(PickerRig.Has(bottom, "server-scroll-down-dim"));  // 在底:向下灰
    }

    [Fact]
    public void Only_the_visible_rows_are_drawn()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen screen = ReadyMany(12, art);
        Assert.Equal(10, PickerRig.Count(screen.Build(0), "server-entry-plate"));
        ClickCenter(screen, ServerSelectScreen.ScrollDownRect());
        List<SceneSprite> scrolled = screen.Build(0);
        Assert.Equal(10, PickerRig.Count(scrolled, "server-entry-plate"));
        Assert.Equal(214, scrolled.Where(s => s.AnimationPath == "server-entry-plate").Min(s => s.ScreenY));
    }

    [Fact]
    public void The_illustration_and_banner_text_follow_the_selected_server()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("卡恩"), Ok("希洛克")), 1, art);
        List<SceneSprite> scene = screen.Build(0);
        Assert.True(PickerRig.Has(scene, "server-illustration"));      // 希洛克有插画
        Assert.True(PickerRig.Has(scene, "picker-banner-label"));

        screen.Move(-1);                                              // 卡恩:图包里没有它的插画与名牌
        List<SceneSprite> other = screen.Build(0);
        Assert.False(PickerRig.Has(other, "server-illustration"));
        Assert.False(PickerRig.Has(other, "picker-banner-label"));
        Assert.True(PickerRig.Has(other, "picker-banner-text"));       // 横幅里改写名字(没有字体时是占位)

        var loading = new ServerSelectScreen(new ServerPickerScene(art));
        List<SceneSprite> none = loading.Build(0);
        Assert.False(PickerRig.Has(none, "server-illustration"));      // 没选中就没有插画
        Assert.False(PickerRig.Has(none, "picker-banner-text"));
    }
}
