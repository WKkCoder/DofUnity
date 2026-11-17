using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>选频道界面的内容状态、选中与翻页、点击热区(像素级)、忙状态、画面构成(合成数据,不用原版素材)。</summary>
public sealed class ChannelSelectScreenTests
{
    private static DirectoryChannel Ch(int number, ChannelCongestion congestion = ChannelCongestion.Smooth, bool noTarget = false, int min = 0, int max = 0)
        => PickerData.Channel(number, congestion, "测试", 0, null, min, max, noTarget);

    private static DirectoryServer Srv(string name, params DirectoryChannel[] channels) => new DirectoryServer(name, channels);

    private static ChannelSelectScreen Ready(DirectoryServer server, ChannelTarget prefer = null, ShellFakeSprites art = null)
    {
        var screen = new ChannelSelectScreen(art == null ? null : new ServerPickerScene(art));
        screen.SetServer(server, prefer);
        return screen;
    }

    /// <summary>n 个频道,序号 1 到 n,都进得去。</summary>
    private static ChannelSelectScreen Plain(int count, ShellFakeSprites art = null) => Ready(PickerData.Server("希洛克", count), null, art);

    private static bool ClickCenter(ChannelSelectScreen screen, HitRect rect) => screen.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static HitRect ItemRect(ChannelSelectScreen screen, int index)
    {
        Assert.True(screen.TryItemRect(index, out HitRect rect));
        return rect;
    }

    private static ChannelSelectAction Take(ChannelSelectScreen screen) => screen.TakeAction(out _);

    // ------------------------------------------------------------------ 内容

    [Fact]
    public void A_new_screen_is_loading_with_nothing_chosen()
    {
        var screen = new ChannelSelectScreen();
        Assert.Equal(PickerPhase.Loading, screen.Phase);
        Assert.Equal(ChannelBusy.None, screen.Busy);
        Assert.Equal(-1, screen.Selected);
        Assert.Equal(0, screen.Page);
        Assert.Equal(1, screen.PageCount);
        Assert.Null(screen.Server);
        Assert.Null(screen.SelectedChannel);
        Assert.Empty(screen.Channels);
        Assert.False(screen.CanStart);
        Assert.False(screen.CanRefresh);                 // 第一次读目录期间不能刷新
        screen.Confirm();
        screen.Move(1);
        screen.Refresh();
        Assert.Equal(ChannelSelectAction.None, Take(screen));
    }

    [Fact]
    public void A_server_selects_the_first_channel_that_can_be_entered()
    {
        ChannelSelectScreen screen = Ready(Srv("希洛克", Ch(1, ChannelCongestion.Full), Ch(2, noTarget: true), Ch(3), Ch(4)));
        Assert.Equal(PickerPhase.Ready, screen.Phase);
        Assert.Equal(2, screen.Selected);
        Assert.Equal(3, screen.SelectedChannel.Number);
        Assert.True(screen.CanStart);
        Assert.True(screen.CanRefresh);
        Assert.Equal("希洛克", screen.Server.Name);
        Assert.Equal(4, screen.Channels.Count);
    }

    [Fact]
    public void The_preferred_target_is_selected_only_when_it_can_be_entered()
    {
        DirectoryServer server = Srv("希洛克", Ch(1, ChannelCongestion.Full), Ch(2, noTarget: true), Ch(3), Ch(4));
        Assert.Equal(3, Ready(server, PickerData.Target(4)).Selected);                           // 可进的偏好
        Assert.Equal(2, Ready(server, PickerData.Target(1)).Selected);                           // 偏好的是满载:忽略
        Assert.Equal(2, Ready(server, PickerData.Target(99)).Selected);                          // 目录里没有:忽略
        Assert.Equal(2, Ready(server, new ChannelTarget(PickerData.PortOf(4), "10.1.2.3")).Selected);   // 主机也是身份的一部分
    }

    [Fact]
    public void The_page_follows_the_selected_channel()
    {
        ChannelSelectScreen screen = Ready(PickerData.Server("希洛克", 20), PickerData.Target(17));
        Assert.Equal(16, screen.Selected);
        Assert.Equal(1, screen.Page);
        Assert.Equal(2, screen.PageCount);
        Assert.True(screen.TryItemRect(16, out _));
        Assert.False(screen.TryItemRect(0, out _));
    }

    [Fact]
    public void A_server_without_channels_is_empty_and_confirm_means_refresh()
    {
        ChannelSelectScreen screen = Ready(Srv("希洛克"));
        Assert.Equal(PickerPhase.Empty, screen.Phase);
        Assert.Equal(-1, screen.Selected);
        Assert.Equal(0, screen.Page);
        Assert.False(screen.CanStart);
        Assert.True(screen.CanRefresh);
        screen.Confirm();
        Assert.Equal(ChannelSelectAction.Refresh, Take(screen));
    }

    [Fact]
    public void A_server_whose_channels_are_all_unusable_has_nothing_selected()
    {
        ChannelSelectScreen screen = Ready(Srv("希洛克", Ch(1, ChannelCongestion.Full), Ch(2, noTarget: true)));
        Assert.Equal(PickerPhase.Ready, screen.Phase);
        Assert.Equal(-1, screen.Selected);
        Assert.False(screen.CanStart);
        screen.Confirm();
        Assert.Equal(ChannelSelectAction.None, Take(screen));
    }

    [Fact]
    public void A_null_server_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ChannelSelectScreen().SetServer(null));
    }

    [Fact]
    public void Showing_a_server_clears_busy_notice_hint_and_pending_action()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.SetBusy(ChannelBusy.Connecting);
        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
        screen.Quit();
        screen.SetServer(PickerData.Server("卡恩", 2));
        Assert.Equal(ChannelBusy.None, screen.Busy);
        Assert.Null(screen.Notice);
        Assert.Null(screen.Hint);
        Assert.Equal(ChannelSelectAction.None, Take(screen));
    }

    [Fact]
    public void Failing_clears_busy_and_the_pending_action_and_confirm_means_refresh()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.SetBusy(ChannelBusy.Refreshing);
        screen.SetFailed();
        Assert.Equal(PickerPhase.Failed, screen.Phase);
        Assert.Equal(ChannelBusy.None, screen.Busy);
        Assert.False(screen.CanStart);
        Assert.Equal(ChannelSelectAction.None, Take(screen));
        screen.Confirm();
        Assert.Equal(ChannelSelectAction.Refresh, Take(screen));
    }

    [Fact]
    public void Loading_again_keeps_the_channels_and_drops_the_pending_action()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.Confirm();
        screen.SetLoading();
        Assert.Equal(PickerPhase.Loading, screen.Phase);
        Assert.Equal(3, screen.Channels.Count);
        Assert.Equal(ChannelSelectAction.None, Take(screen));
        Assert.False(screen.CanStart);
    }

    [Fact]
    public void The_notice_is_kept_until_replaced_and_a_notice_clears_the_hint()
    {
        ChannelSelectScreen screen = Ready(Srv("希洛克", Ch(1, ChannelCongestion.Full), Ch(2)));
        ClickCenter(screen, ItemRect(screen, 0));
        Assert.Equal(PickerLabel.ChannelFull, screen.Hint);
        var notice = new OnlineNotice(OnlineNoticeKind.NetworkError);
        screen.SetNotice(notice);
        Assert.Same(notice, screen.Notice);
        Assert.Null(screen.Hint);
        screen.SetNotice(null);
        Assert.Null(screen.Notice);
    }

    [Fact]
    public void Time_advances_by_whole_milliseconds_and_ignores_negative_steps()
    {
        var screen = new ChannelSelectScreen();
        screen.Advance(100.9);
        screen.Advance(-10);
        Assert.Equal(100, screen.AgeMs);
    }

    // ------------------------------------------------------------------ 选中与翻页

    [Fact]
    public void Arrow_keys_step_through_the_grid_in_directory_order_and_skip_unusable_channels()
    {
        // 排法(每行 3 个):  1 2 3 / 4(满) 5 6 / 7 8(无端口) 9
        ChannelSelectScreen screen = Ready(Srv("希洛克",
            Ch(1), Ch(2), Ch(3), Ch(4, ChannelCongestion.Full), Ch(5), Ch(6), Ch(7), Ch(8, noTarget: true), Ch(9)));
        Assert.Equal(0, screen.Selected);
        screen.Move(1);
        Assert.Equal(1, screen.Selected);
        screen.Move(0, 1);
        Assert.Equal(4, screen.Selected);                // 下一行同列:2 → 5
        screen.Move(0, 1);
        Assert.Equal(4, screen.Selected);                // 5 下面是 8,无端口,再下面没有了
        screen.Move(1);
        Assert.Equal(5, screen.Selected);
        screen.Move(1);
        Assert.Equal(6, screen.Selected);                // 行尾再右一格:按目录顺序走到下一行第一个(下标 6)
        screen.Move(1);
        Assert.Equal(8, screen.Selected);                // 越过无端口的(下标 7)
        screen.Move(1);
        Assert.Equal(8, screen.Selected);                // 到头不循环
        screen.Move(-1);
        Assert.Equal(6, screen.Selected);
        screen.Move(0, -1);
        Assert.Equal(0, screen.Selected);                // 从下标 6 往上一行是下标 3(满载,进不去),再往上是 0
    }

    [Fact]
    public void Moving_across_the_page_edge_turns_the_page()
    {
        ChannelSelectScreen screen = Plain(20);
        screen.Move(0, 1);
        screen.Move(0, 1);
        screen.Move(0, 1);
        screen.Move(0, 1);
        Assert.Equal(12, screen.Selected);
        Assert.Equal(0, screen.Page);
        screen.Move(0, 1);
        Assert.Equal(15, screen.Selected);               // 下一页的第一行
        Assert.Equal(1, screen.Page);
        screen.Move(0, -1);
        Assert.Equal(12, screen.Selected);
        Assert.Equal(0, screen.Page);
        screen.Move(1);
        screen.Move(1);
        Assert.Equal(14, screen.Selected);
        Assert.Equal(0, screen.Page);
        screen.Move(1);
        Assert.Equal(15, screen.Selected);
        Assert.Equal(1, screen.Page);
        screen.Move(-1);
        Assert.Equal(14, screen.Selected);
        Assert.Equal(0, screen.Page);
    }

    [Fact]
    public void Moving_is_ignored_while_busy_and_unless_the_content_is_ready()
    {
        ChannelSelectScreen screen = Plain(5);
        screen.SetBusy(ChannelBusy.Connecting);
        screen.Move(1);
        Assert.Equal(0, screen.Selected);
        screen.SetBusy(ChannelBusy.None);
        screen.Move(1);
        Assert.Equal(1, screen.Selected);
        screen.SetFailed();
        screen.Move(1);
        Assert.Equal(1, screen.Selected);
    }

    [Fact]
    public void Page_count_rounds_up_by_fifteen()
    {
        Assert.Equal(1, Plain(1).PageCount);
        Assert.Equal(1, Plain(15).PageCount);
        Assert.Equal(2, Plain(16).PageCount);
        Assert.Equal(2, Plain(30).PageCount);
        Assert.Equal(3, Plain(31).PageCount);
        Assert.Equal(1, Ready(Srv("希洛克")).PageCount);
    }

    [Fact]
    public void Next_and_previous_page_select_the_first_usable_channel_of_the_page_and_clamp()
    {
        ChannelSelectScreen screen = Ready(Srv("希洛克", Enumerable.Range(1, 20).Select(n => n == 16 ? Ch(n, ChannelCongestion.Full) : Ch(n)).ToArray()));
        screen.NextPage();
        Assert.Equal(1, screen.Page);
        Assert.Equal(16, screen.Selected);               // 序号 16(下标 15)是满载,该页第一个进得去的是下标 16
        screen.NextPage();
        Assert.Equal(1, screen.Page);                    // 已经是最后一页
        screen.PrevPage();
        Assert.Equal(0, screen.Page);
        Assert.Equal(0, screen.Selected);
        screen.PrevPage();
        Assert.Equal(0, screen.Page);
    }

    [Fact]
    public void A_page_with_nothing_to_enter_leaves_nothing_selected()
    {
        var channels = new List<DirectoryChannel>();
        for (int n = 1; n <= 16; n++)
        {
            channels.Add(n <= 15 ? Ch(n) : Ch(n, ChannelCongestion.Full));
        }
        ChannelSelectScreen screen = Ready(new DirectoryServer("希洛克", channels));
        screen.NextPage();
        Assert.Equal(1, screen.Page);
        Assert.Equal(-1, screen.Selected);
        Assert.False(screen.CanStart);
    }

    [Fact]
    public void Paging_is_ignored_while_busy_and_unless_ready()
    {
        ChannelSelectScreen screen = Plain(20);
        screen.SetBusy(ChannelBusy.Refreshing);
        screen.NextPage();
        Assert.Equal(0, screen.Page);
        screen.SetBusy(ChannelBusy.None);
        screen.SetFailed();
        screen.NextPage();
        Assert.Equal(0, screen.Page);
    }

    // ------------------------------------------------------------------ 动作

    [Fact]
    public void Confirm_starts_the_selected_channel_and_the_action_is_taken_once_with_its_target()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.Move(1);
        screen.Confirm();
        Assert.Equal(ChannelSelectAction.Start, screen.TakeAction(out ChannelTarget target));
        Assert.Equal(PickerData.Target(2), target);
        Assert.Equal(ChannelSelectAction.None, screen.TakeAction(out ChannelTarget again));
        Assert.Null(again);
    }

    [Fact]
    public void Only_the_start_action_carries_a_target()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.Back();
        Assert.Equal(ChannelSelectAction.Back, screen.TakeAction(out ChannelTarget target));
        Assert.Null(target);
        screen.Quit();
        Assert.Equal(ChannelSelectAction.Quit, screen.TakeAction(out target));
        Assert.Null(target);
    }

    [Fact]
    public void Confirm_does_nothing_while_busy_or_loading()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.SetBusy(ChannelBusy.Connecting);
        screen.Confirm();
        Assert.Equal(ChannelSelectAction.None, Take(screen));
        screen.SetBusy(ChannelBusy.Refreshing);
        screen.Confirm();
        Assert.Equal(ChannelSelectAction.None, Take(screen));
        screen.SetBusy(ChannelBusy.None);
        screen.SetLoading();
        screen.Confirm();
        Assert.Equal(ChannelSelectAction.None, Take(screen));
    }

    [Fact]
    public void Back_and_quit_are_always_available_even_while_busy()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.SetBusy(ChannelBusy.Connecting);
        screen.Back();
        Assert.Equal(ChannelSelectAction.Back, Take(screen));
        screen.Quit();
        Assert.Equal(ChannelSelectAction.Quit, Take(screen));
        var loading = new ChannelSelectScreen();
        loading.Back();
        Assert.Equal(ChannelSelectAction.Back, Take(loading));
        loading.Quit();
        Assert.Equal(ChannelSelectAction.Quit, Take(loading));
    }

    [Fact]
    public void Refresh_is_available_unless_loading_the_first_time_or_busy()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.Refresh();
        Assert.Equal(ChannelSelectAction.Refresh, Take(screen));
        screen.SetBusy(ChannelBusy.Connecting);
        screen.Refresh();
        Assert.Equal(ChannelSelectAction.None, Take(screen));
        screen.SetBusy(ChannelBusy.None);
        screen.SetLoading();
        screen.Refresh();
        Assert.Equal(ChannelSelectAction.None, Take(screen));
        screen.SetFailed();
        screen.Refresh();
        Assert.Equal(ChannelSelectAction.Refresh, Take(screen));
    }

    [Fact]
    public void Direct_connect_exists_only_for_failed_or_empty_and_only_when_idle()
    {
        ChannelSelectScreen screen = Plain(3);
        screen.DirectConnect();
        Assert.Equal(ChannelSelectAction.None, Take(screen));            // 有内容

        screen.SetFailed();
        screen.DirectConnect();
        Assert.Equal(ChannelSelectAction.DirectConnect, Take(screen));

        screen.SetServer(Srv("希洛克"));
        screen.DirectConnect();
        Assert.Equal(ChannelSelectAction.DirectConnect, Take(screen));

        screen.SetBusy(ChannelBusy.Connecting);
        screen.DirectConnect();
        Assert.Equal(ChannelSelectAction.None, Take(screen));            // 忙着时没有

        var loading = new ChannelSelectScreen();
        loading.DirectConnect();
        Assert.Equal(ChannelSelectAction.None, Take(loading));
    }

    [Fact]
    public void Becoming_busy_drops_a_pending_action_and_clears_the_hint()
    {
        ChannelSelectScreen screen = Ready(Srv("希洛克", Ch(1, ChannelCongestion.Full), Ch(2)));
        ClickCenter(screen, ItemRect(screen, 0));
        Assert.NotNull(screen.Hint);
        screen.Confirm();
        screen.SetBusy(ChannelBusy.Connecting);
        Assert.Null(screen.Hint);
        Assert.Equal(ChannelSelectAction.None, Take(screen));
    }

    // ------------------------------------------------------------------ 点击与热区

    [Fact]
    public void Item_rectangles_follow_the_baseline_grid()
    {
        ChannelSelectScreen screen = Plain(20);
        HitRect first = ItemRect(screen, 0);
        Assert.Equal((116, 196, 186, 40), (first.X, first.Y, first.Width, first.Height));
        HitRect second = ItemRect(screen, 1);
        Assert.Equal((306, 196), (second.X, second.Y));
        HitRect fourth = ItemRect(screen, 3);
        Assert.Equal((116, 240), (fourth.X, fourth.Y));
        HitRect last = ItemRect(screen, 14);
        Assert.Equal((496, 372), (last.X, last.Y));
        Assert.False(screen.TryItemRect(15, out _));      // 下一页
        Assert.False(screen.TryItemRect(-1, out _));
        Assert.False(screen.TryItemRect(20, out _));
        screen.NextPage();
        HitRect top = ItemRect(screen, 15);
        Assert.Equal((116, 196), (top.X, top.Y));
        Assert.False(screen.TryItemRect(0, out _));
    }

    [Fact]
    public void Clicking_an_item_selects_it_to_the_pixel()
    {
        ChannelSelectScreen screen = Plain(6);
        Assert.True(screen.Click(306, 196));              // 第二项左上角
        Assert.Equal(1, screen.Selected);
        Assert.True(screen.Click(116, 196));
        Assert.Equal(0, screen.Selected);
        Assert.True(screen.Click(301, 235));              // 第一项右下角
        Assert.Equal(0, screen.Selected);
        Assert.True(screen.Click(496, 240));              // 第六项
        Assert.Equal(5, screen.Selected);

        Assert.False(screen.Click(115, 196));
        Assert.False(screen.Click(116, 195));
        Assert.False(screen.Click(302, 196));             // 项与项之间的缝
        Assert.False(screen.Click(116, 236));             // 行与行之间的缝
        Assert.False(screen.Click(500, 284));             // 没有第七项的位置
    }

    [Fact]
    public void Clicking_a_full_or_portless_channel_gives_a_hint_and_keeps_the_selection()
    {
        ChannelSelectScreen screen = Ready(Srv("希洛克", Ch(1, ChannelCongestion.Full), Ch(2, noTarget: true), Ch(3), Ch(4)));
        Assert.Equal(2, screen.Selected);
        Assert.True(ClickCenter(screen, ItemRect(screen, 0)));
        Assert.Equal(PickerLabel.ChannelFull, screen.Hint);
        Assert.Equal(2, screen.Selected);
        Assert.True(ClickCenter(screen, ItemRect(screen, 1)));
        Assert.Equal(PickerLabel.ChannelUnavailable, screen.Hint);
        Assert.Equal(2, screen.Selected);
        Assert.True(ClickCenter(screen, ItemRect(screen, 3)));
        Assert.Null(screen.Hint);                         // 选了别的就收起来
        Assert.Equal(3, screen.Selected);
        ClickCenter(screen, ItemRect(screen, 0));
        Assert.NotNull(screen.Hint);
        screen.Move(-1);
        Assert.Null(screen.Hint);                         // 方向键也收起来
        ClickCenter(screen, ItemRect(screen, 0));
        Assert.NotNull(screen.Hint);
        screen.SetBusy(ChannelBusy.Connecting);
        Assert.Null(screen.Hint);
    }

    [Fact]
    public void While_busy_items_swallow_the_click_without_selecting()
    {
        ChannelSelectScreen screen = Plain(4);
        screen.SetBusy(ChannelBusy.Connecting);
        Assert.True(ClickCenter(screen, ItemRect(screen, 2)));
        Assert.Equal(0, screen.Selected);
    }

    [Fact]
    public void The_bottom_buttons_sit_where_the_baseline_has_them()
    {
        Assert.Equal((337, 526, 125, 43), Tuple(PickerArt.StartRect()));
        Assert.Equal((137, 536, 77, 23), Tuple(ChannelSelectScreen.ChangeServerRect()));
        Assert.Equal((587, 536, 77, 23), Tuple(ChannelSelectScreen.QuitRect()));
        Assert.Equal((619, 466, 64, 20), Tuple(ChannelSelectScreen.RefreshRect()));
        Assert.Equal((354, 438, 22, 18), Tuple(ChannelSelectScreen.PrevPageRect()));
        Assert.Equal((422, 438, 22, 18), Tuple(ChannelSelectScreen.NextPageRect()));
        Assert.Equal((316, 350, 77, 23), Tuple(ChannelSelectScreen.RetryRect()));
        Assert.Equal((405, 350, 77, 23), Tuple(ChannelSelectScreen.DirectRect()));
    }

    private static (int, int, int, int) Tuple(HitRect rect) => (rect.X, rect.Y, rect.Width, rect.Height);

    [Fact]
    public void The_start_button_hot_area_is_exactly_its_rectangle_and_acts_only_when_live()
    {
        ChannelSelectScreen screen = Plain(3);
        Assert.True(screen.Click(337, 526));
        Assert.Equal(ChannelSelectAction.Start, Take(screen));
        Assert.True(screen.Click(461, 568));
        Assert.Equal(ChannelSelectAction.Start, Take(screen));
        Assert.False(screen.Click(336, 540));
        Assert.False(screen.Click(462, 540));
        Assert.False(screen.Click(400, 525));
        Assert.False(screen.Click(400, 569));
        Assert.Equal(ChannelSelectAction.None, Take(screen));

        screen.SetBusy(ChannelBusy.Connecting);
        Assert.True(screen.Click(400, 540));              // 灰着:吃掉
        Assert.Equal(ChannelSelectAction.None, Take(screen));

        ChannelSelectScreen none = Ready(Srv("希洛克", Ch(1, ChannelCongestion.Full)));
        Assert.True(none.Click(400, 540));
        Assert.Equal(ChannelSelectAction.None, Take(none));
    }

    [Fact]
    public void The_change_server_button_goes_back_but_not_while_busy()
    {
        ChannelSelectScreen screen = Plain(3);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.ChangeServerRect()));
        Assert.Equal(ChannelSelectAction.Back, Take(screen));
        Assert.True(screen.Click(137, 536));
        Assert.Equal(ChannelSelectAction.Back, Take(screen));
        Assert.False(screen.Click(136, 536));
        Assert.False(screen.Click(214, 536));
        screen.SetBusy(ChannelBusy.Connecting);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.ChangeServerRect()));
        Assert.Equal(ChannelSelectAction.None, Take(screen));
    }

    [Fact]
    public void The_quit_button_always_works()
    {
        ChannelSelectScreen screen = Plain(3);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.QuitRect()));
        Assert.Equal(ChannelSelectAction.Quit, Take(screen));
        screen.SetBusy(ChannelBusy.Connecting);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.QuitRect()));
        Assert.Equal(ChannelSelectAction.Quit, Take(screen));
        var loading = new ChannelSelectScreen();
        Assert.True(ClickCenter(loading, ChannelSelectScreen.QuitRect()));
        Assert.Equal(ChannelSelectAction.Quit, Take(loading));
    }

    [Fact]
    public void The_refresh_button_works_when_idle_and_is_dead_while_busy_or_loading()
    {
        ChannelSelectScreen screen = Plain(3);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.RefreshRect()));
        Assert.Equal(ChannelSelectAction.Refresh, Take(screen));
        Assert.True(screen.Click(619, 466));
        Assert.Equal(ChannelSelectAction.Refresh, Take(screen));
        Assert.False(screen.Click(618, 466));
        Assert.False(screen.Click(683, 466));
        screen.SetBusy(ChannelBusy.Refreshing);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.RefreshRect()));
        Assert.Equal(ChannelSelectAction.None, Take(screen));
        screen.SetBusy(ChannelBusy.None);

        var loading = new ChannelSelectScreen();
        Assert.False(ClickCenter(loading, ChannelSelectScreen.RefreshRect()));       // 第一次读目录:按钮是灰的、不在热区里
        Assert.Equal(ChannelSelectAction.None, Take(loading));
    }

    [Fact]
    public void The_pager_arrows_exist_only_with_more_than_one_page_and_only_when_idle()
    {
        ChannelSelectScreen one = Plain(15);
        Assert.False(ClickCenter(one, ChannelSelectScreen.NextPageRect()));
        Assert.False(ClickCenter(one, ChannelSelectScreen.PrevPageRect()));

        ChannelSelectScreen screen = Plain(20);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.NextPageRect()));
        Assert.Equal(1, screen.Page);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.PrevPageRect()));
        Assert.Equal(0, screen.Page);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.PrevPageRect()));
        Assert.Equal(0, screen.Page);                     // 第一页往前:不动
        screen.SetBusy(ChannelBusy.Connecting);
        Assert.True(ClickCenter(screen, ChannelSelectScreen.NextPageRect()));
        Assert.Equal(0, screen.Page);
    }

    [Fact]
    public void The_retry_and_default_buttons_exist_for_failed_and_empty_content_and_not_while_busy()
    {
        ChannelSelectScreen ready = Plain(3);
        Assert.False(ClickCenter(ready, ChannelSelectScreen.RetryRect()));
        Assert.False(ClickCenter(ready, ChannelSelectScreen.DirectRect()));

        ChannelSelectScreen failed = Plain(3);
        failed.SetFailed();
        Assert.True(ClickCenter(failed, ChannelSelectScreen.RetryRect()));
        Assert.Equal(ChannelSelectAction.Refresh, Take(failed));
        Assert.True(ClickCenter(failed, ChannelSelectScreen.DirectRect()));
        Assert.Equal(ChannelSelectAction.DirectConnect, Take(failed));
        Assert.False(failed.Click(315, 360));
        Assert.False(failed.Click(482, 360));
        Assert.False(ClickCenter(failed, ItemRectFallback()));                       // 失败时没有频道项

        ChannelSelectScreen empty = Ready(Srv("希洛克"));
        Assert.True(ClickCenter(empty, ChannelSelectScreen.RetryRect()));
        Assert.Equal(ChannelSelectAction.Refresh, Take(empty));
        Assert.True(ClickCenter(empty, ChannelSelectScreen.DirectRect()));
        Assert.Equal(ChannelSelectAction.DirectConnect, Take(empty));

        failed.SetBusy(ChannelBusy.Connecting);                                      // 直连默认频道时:两个按钮灰着,点了没反应
        Assert.True(ClickCenter(failed, ChannelSelectScreen.RetryRect()));
        Assert.Equal(ChannelSelectAction.None, Take(failed));
        Assert.True(ClickCenter(failed, ChannelSelectScreen.DirectRect()));
        Assert.Equal(ChannelSelectAction.None, Take(failed));
    }

    private static HitRect ItemRectFallback() => new HitRect(ChannelSelectScreen.ItemX, ChannelSelectScreen.ItemY, ChannelSelectScreen.ItemWidth, ChannelSelectScreen.ItemHeight);

    // ------------------------------------------------------------------ 画面

    [Fact]
    public void The_scene_places_every_piece_at_its_baseline_position()
    {
        ShellFakeSprites art = PickerSprites.Full();
        var server = new DirectoryServer("希洛克", new[]
        {
            Ch(1, ChannelCongestion.Smooth, false, 41, 55),
            Ch(2, ChannelCongestion.Full),
            Ch(3, ChannelCongestion.Crowded, true),
            Ch(4, ChannelCongestion.Good),
        });
        ChannelSelectScreen screen = Ready(server, null, art);
        List<SceneSprite> scene = screen.Build(0);

        SceneSprite background = PickerSprites.One(scene, "channel-background");
        Assert.Equal((0, 0), (background.ScreenX, background.ScreenY));
        SceneSprite illustration = PickerSprites.One(scene, "channel-illustration");
        Assert.Equal((2, 2), (illustration.ScreenX, illustration.ScreenY));
        Assert.True(PickerSprites.IsFrame(illustration, art, ServerArt.ChannelBackgroundImage, 6));
        SceneSprite board = PickerSprites.One(scene, "channel-board");
        Assert.Equal((88, 114, 104, 90), (board.ScreenX, board.ScreenY, board.Frame.Width, board.Frame.Height));
        SceneSprite title = PickerSprites.One(scene, "channel-title");
        Assert.Equal((344, 33), (title.ScreenX, title.ScreenY));
        Assert.True(PickerSprites.IsFrame(title, art, ServerArt.ChannelBaseImage, 0));
        SceneSprite banner = PickerSprites.One(scene, "picker-banner");
        Assert.Equal((273, 73), (banner.ScreenX, banner.ScreenY));
        SceneSprite label = PickerSprites.One(scene, "picker-banner-label");
        Assert.Equal((344, 72), (label.ScreenX, label.ScreenY));
        Assert.True(PickerSprites.IsFrame(label, art, ServerArt.ChannelBackgroundImage, 7));
        SceneSprite strip = PickerSprites.One(scene, "channel-strip");
        Assert.Equal((119, 137), (strip.ScreenX, strip.ScreenY));
        Assert.True(PickerSprites.IsFrame(strip, art, ServerArt.ChannelBaseImage, 14));
        SceneSprite rule = PickerSprites.One(scene, "channel-rule");
        Assert.Equal((118, 417), (rule.ScreenX, rule.ScreenY));
        Assert.True(PickerSprites.IsFrame(rule, art, ServerArt.ChannelBaseImage, 8));

        List<SceneSprite> items = scene.Where(s => s.AnimationPath == "channel-item").ToList();
        Assert.Equal(4, items.Count);
        Assert.Equal(new[] { (116, 196), (306, 196), (496, 196), (116, 240) }, items.Select(s => (s.ScreenX, s.ScreenY)).ToArray());
        Assert.All(items, s => Assert.True(PickerSprites.IsFrame(s, art, ServerArt.ChannelSlotImage, ChannelSelectScreen.ItemFrame)));

        SceneSprite select = PickerSprites.One(scene, "channel-item-select");
        Assert.Equal((116, 196), (select.ScreenX, select.ScreenY));
        Assert.Equal(SpriteBlend.Additive, select.Blend);
        Assert.True(PickerSprites.IsFrame(select, art, ServerArt.ChannelBaseImage, 2));

        // 字:第一行 (158,201)、推荐等级 (158,215)(只有有推荐等级的频道才有)、拥挤词右端 295
        List<SceneSprite> titles = scene.Where(s => s.AnimationPath == "channel-item-title").ToList();
        Assert.Equal(4, titles.Count);
        Assert.Equal((158, 201), (titles[0].ScreenX, titles[0].ScreenY));
        Assert.Equal((348, 201), (titles[1].ScreenX, titles[1].ScreenY));
        SceneSprite level = PickerSprites.One(scene, "channel-item-level");
        Assert.Equal((158, 215), (level.ScreenX, level.ScreenY));
        List<SceneSprite> words = scene.Where(s => s.AnimationPath == "channel-item-congestion").ToList();
        Assert.Equal(4, words.Count);
        Assert.Equal(295, words[0].ScreenX + words[0].Frame.Width);
        Assert.Equal(209, words[0].ScreenY);
        Assert.Equal(485, words[1].ScreenX + words[1].Frame.Width);

        // 满载与无端口的频道被压暗,压在字的上面;选中的那个不暗
        List<SceneSprite> dims = scene.Where(s => s.AnimationPath == "channel-item-dim").ToList();
        Assert.Equal(new[] { (306, 196), (496, 196) }, dims.Select(s => (s.ScreenX, s.ScreenY)).ToArray());
        Assert.True(dims[0].SortKey > titles[1].SortKey);
        Assert.False(PickerRig.Has(scene, "channel-item-busy"));
        Assert.False(PickerRig.Has(scene, "channel-page-prev"));       // 一页:没有翻页控件

        SceneSprite refresh = PickerSprites.One(scene, "channel-refresh");
        Assert.Equal((619, 466), (refresh.ScreenX, refresh.ScreenY));
        Assert.True(PickerSprites.IsFrame(refresh, art, ServerArt.ChannelBaseImage, 3));
        SceneSprite change = PickerSprites.One(scene, "channel-change");
        Assert.Equal((137, 536), (change.ScreenX, change.ScreenY));
        SceneSprite quit = PickerSprites.One(scene, "channel-quit");
        Assert.Equal((587, 536), (quit.ScreenX, quit.ScreenY));
        Assert.False(PickerRig.Has(scene, "channel-change-dim"));
        SceneSprite start = PickerSprites.One(scene, "channel-start");
        Assert.Equal((337, 526), (start.ScreenX, start.ScreenY));
        Assert.True(PickerSprites.IsFrame(start, art, CharacterSelectArt.UiImage, CharacterSelectArt.StartNormalFrame));
        Assert.True(PickerRig.Has(scene, "channel-start-glow"));

        for (int i = 1; i < scene.Count; i++)
        {
            Assert.True(scene[i].SortKey > scene[i - 1].SortKey);
        }
    }

    [Fact]
    public void Items_beyond_the_first_page_are_drawn_on_their_own_page_and_the_pager_shows()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ChannelSelectScreen screen = Plain(20, art);
        List<SceneSprite> first = screen.Build(0);
        Assert.Equal(15, PickerRig.Count(first, "channel-item"));
        SceneSprite prev = PickerSprites.One(first, "channel-page-prev");
        SceneSprite next = PickerSprites.One(first, "channel-page-next");
        Assert.Equal((354, 438), (prev.ScreenX, prev.ScreenY));
        Assert.Equal((422, 438), (next.ScreenX, next.ScreenY));
        Assert.Same(PickerArt.Arrow(true, false, false), prev.Frame);      // 第一页:向前灰
        Assert.Same(PickerArt.Arrow(false, true, false), next.Frame);
        SceneSprite label = PickerSprites.One(first, "channel-page-label");
        Assert.InRange(label.ScreenX + label.Frame.Width / 2, 398, 400);          // 页码居中在 399

        screen.NextPage();
        List<SceneSprite> second = screen.Build(0);
        Assert.Equal(5, PickerRig.Count(second, "channel-item"));
        Assert.Equal((116, 196), (second.First(s => s.AnimationPath == "channel-item").ScreenX, second.First(s => s.AnimationPath == "channel-item").ScreenY));
        Assert.Same(PickerArt.Arrow(true, true, false), PickerSprites.One(second, "channel-page-prev").Frame);
        Assert.Same(PickerArt.Arrow(false, false, false), PickerSprites.One(second, "channel-page-next").Frame);   // 最后一页:向后灰

        screen.Hover(ChannelSelectScreen.PrevPageRect().X + 2, ChannelSelectScreen.PrevPageRect().Y + 2);
        Assert.Same(PickerArt.Arrow(true, true, true), PickerSprites.One(screen.Build(0), "channel-page-prev").Frame);
    }

    [Fact]
    public void The_refresh_button_picks_its_frame_from_the_state()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ChannelSelectScreen screen = Plain(3, art);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-refresh"), art, ServerArt.ChannelBaseImage, 3));

        screen.Hover(625, 470);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-refresh"), art, ServerArt.ChannelBaseImage, 4));

        screen.Click(625, 470);
        Assert.Equal(ChannelSelectAction.Refresh, Take(screen));
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-refresh"), art, ServerArt.ChannelBaseImage, 5));
        screen.Advance(140);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-refresh"), art, ServerArt.ChannelBaseImage, 4));
        screen.HoverNone();

        screen.SetBusy(ChannelBusy.Refreshing);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-refresh"), art, ServerArt.ChannelBaseImage, 6));
        screen.SetBusy(ChannelBusy.None);
        screen.SetLoading();
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-refresh"), art, ServerArt.ChannelBaseImage, 6));
    }

    [Fact]
    public void The_start_button_picks_its_frame_from_the_state_and_only_glows_when_live()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ChannelSelectScreen screen = Plain(3, art);
        screen.Advance(500);
        List<SceneSprite> normal = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(normal, "channel-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartNormalFrame));
        Assert.Equal(PickerArt.StartGlowTint(500), PickerSprites.One(normal, "channel-start-glow").TintAlpha);

        screen.Hover(400, 540);
        List<SceneSprite> hover = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(hover, "channel-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartHoverFrame));
        Assert.Equal(PickerArt.GlowHigh, PickerSprites.One(hover, "channel-start-glow").TintAlpha);
        screen.HoverNone();

        screen.Click(400, 540);
        Assert.Equal(ChannelSelectAction.Start, Take(screen));
        List<SceneSprite> pressed = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(pressed, "channel-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartPressedFrame));
        Assert.False(PickerRig.Has(pressed, "channel-start-glow"));
        screen.Advance(139);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartPressedFrame));
        screen.Advance(1);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(screen.Build(0), "channel-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartNormalFrame));

        screen.SetBusy(ChannelBusy.Connecting);
        List<SceneSprite> busy = screen.Build(0);
        Assert.True(PickerSprites.IsFrame(PickerSprites.One(busy, "channel-start"), art, CharacterSelectArt.UiImage, CharacterSelectArt.StartDisabledFrame));
        Assert.False(PickerRig.Has(busy, "channel-start-glow"));
    }

    [Fact]
    public void Hovering_lights_an_unselected_usable_item_when_idle_only()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ChannelSelectScreen screen = Ready(Srv("希洛克", Ch(1), Ch(2), Ch(3, ChannelCongestion.Full)), null, art);

        screen.Hover(310, 200);                                         // 第二项:可进、没选中
        SceneSprite hover = PickerSprites.One(screen.Build(0), "channel-item-hover");
        Assert.Equal((306, 196), (hover.ScreenX, hover.ScreenY));
        Assert.Equal(SpriteBlend.Additive, hover.Blend);
        Assert.Equal(110, hover.TintAlpha);

        screen.Hover(120, 200);                                         // 选中的:已有选中框
        Assert.False(PickerRig.Has(screen.Build(0), "channel-item-hover"));
        screen.Hover(500, 200);                                         // 满载的
        Assert.False(PickerRig.Has(screen.Build(0), "channel-item-hover"));
        screen.Hover(310, 200);
        screen.SetBusy(ChannelBusy.Connecting);                         // 忙着
        Assert.False(PickerRig.Has(screen.Build(0), "channel-item-hover"));
        screen.SetBusy(ChannelBusy.None);
        screen.HoverNone();
        Assert.False(PickerRig.Has(screen.Build(0), "channel-item-hover"));
    }

    [Fact]
    public void While_busy_everything_but_quit_is_dimmed_and_a_wait_bar_shows()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ChannelSelectScreen screen = Plain(3, art);
        screen.SetBusy(ChannelBusy.Connecting);
        List<SceneSprite> scene = screen.Build(0);
        Assert.Equal(3, PickerRig.Count(scene, "channel-item-busy"));
        Assert.True(PickerRig.Has(scene, "channel-change-dim"));
        Assert.False(PickerRig.Has(scene, "channel-quit-dim"));
        SceneSprite bar = PickerSprites.One(scene, "channel-notice-bar");
        Assert.Equal(462, bar.ScreenY);
        Assert.True(PickerRig.Has(scene, "channel-notice-spinner"));

        screen.SetBusy(ChannelBusy.Refreshing);
        Assert.True(PickerRig.Has(screen.Build(0), "channel-notice-spinner"));
    }

    [Fact]
    public void The_status_bar_shows_the_busy_text_first_then_the_notice_then_the_hint()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ChannelSelectScreen screen = Ready(Srv("希洛克", Ch(1, ChannelCongestion.Full), Ch(2)), null, art);
        Assert.False(PickerRig.Has(screen.Build(0), "channel-notice-bar"));

        ClickCenter(screen, ItemRect(screen, 0));
        List<SceneSprite> hinted = screen.Build(0);
        Assert.True(PickerRig.Has(hinted, "channel-notice-bar"));
        Assert.True(PickerRig.Has(hinted, "channel-notice-icon"));

        screen.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
        Assert.True(PickerRig.Has(screen.Build(0), "channel-notice-icon"));

        screen.SetBusy(ChannelBusy.Connecting);                         // 忙着时盖过提示
        List<SceneSprite> busy = screen.Build(0);
        Assert.True(PickerRig.Has(busy, "channel-notice-spinner"));
        Assert.False(PickerRig.Has(busy, "channel-notice-icon"));
    }

    [Fact]
    public void Each_phase_shows_its_message_and_the_right_extras()
    {
        var loading = new ChannelSelectScreen();
        List<SceneSprite> sceneLoading = loading.Build(0);
        Assert.True(PickerRig.Has(sceneLoading, "channel-spinner"));
        Assert.True(PickerRig.Has(sceneLoading, "channel-message"));
        Assert.False(PickerRig.Has(sceneLoading, "channel-retry"));
        Assert.False(PickerRig.Has(sceneLoading, "channel-item-plate"));

        ChannelSelectScreen empty = Ready(Srv("希洛克"));
        List<SceneSprite> sceneEmpty = empty.Build(0);
        Assert.True(PickerRig.Has(sceneEmpty, "channel-message"));
        Assert.False(PickerRig.Has(sceneEmpty, "channel-spinner"));
        SceneSprite retry = PickerSprites.One(sceneEmpty, "channel-retry");
        Assert.Equal((316, 350), (retry.ScreenX, retry.ScreenY));
        SceneSprite direct = PickerSprites.One(sceneEmpty, "channel-direct");
        Assert.Equal((405, 350), (direct.ScreenX, direct.ScreenY));

        ChannelSelectScreen failed = Plain(3);
        failed.SetFailed();
        failed.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
        List<SceneSprite> sceneFailed = failed.Build(0);
        Assert.True(PickerRig.Has(sceneFailed, "channel-message"));
        Assert.True(PickerRig.Has(sceneFailed, "channel-retry"));
        Assert.Equal(306, PickerSprites.One(sceneFailed, "channel-notice-bar").ScreenY);
        Assert.False(PickerRig.Has(sceneFailed, "channel-item-plate"));

        ChannelSelectScreen ready = Plain(3);
        List<SceneSprite> sceneReady = ready.Build(0);
        Assert.False(PickerRig.Has(sceneReady, "channel-message"));
        Assert.False(PickerRig.Has(sceneReady, "channel-retry"));
    }

    [Fact]
    public void Without_art_the_screen_falls_back_to_a_solid_backdrop_and_plates()
    {
        ChannelSelectScreen screen = Plain(3);
        List<SceneSprite> scene = screen.Build(0);
        SceneSprite backdrop = PickerSprites.One(scene, "channel-background-backdrop");
        Assert.Equal((800, 600), (backdrop.Frame.Width, backdrop.Frame.Height));
        Assert.Equal(3, PickerRig.Count(scene, "channel-item-plate"));
        Assert.True(PickerRig.Has(scene, "channel-item-select-edge"));
        Assert.True(PickerRig.Has(scene, "channel-refresh-text"));
        Assert.False(PickerRig.Has(scene, "channel-board"));
        Assert.True(PickerRig.Has(scene, "channel-start"));
        Assert.False(PickerRig.Has(scene, "channel-start-glow"));
    }

    [Fact]
    public void Item_text_has_the_channel_name_the_level_line_and_a_congestion_word()
    {
        var server = new DirectoryServer("希洛克", new[]
        {
            Ch(1, ChannelCongestion.Smooth, false, 41, 55),
            Ch(2, ChannelCongestion.Crowded),
        });
        ChannelSelectScreen screen = Ready(server);
        List<SceneSprite> scene = screen.Build(0);
        Assert.Equal(2, PickerRig.Count(scene, "channel-item-title"));
        Assert.Equal(1, PickerRig.Count(scene, "channel-item-level"));            // 只有第一个有推荐等级
        Assert.Equal(2, PickerRig.Count(scene, "channel-item-congestion"));
    }

    [Fact]
    public void The_banner_shows_the_server_name_in_text_when_there_is_no_art_for_it()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ChannelSelectScreen known = Ready(PickerData.Server("希洛克", 2), null, art);
        List<SceneSprite> sceneKnown = known.Build(0);
        Assert.True(PickerRig.Has(sceneKnown, "picker-banner-label"));
        Assert.True(PickerRig.Has(sceneKnown, "channel-illustration"));

        ChannelSelectScreen unknown = Ready(PickerData.Server("没见过的服务器", 2), null, art);
        List<SceneSprite> sceneUnknown = unknown.Build(0);
        Assert.False(PickerRig.Has(sceneUnknown, "picker-banner-label"));
        Assert.False(PickerRig.Has(sceneUnknown, "channel-illustration"));
        Assert.True(PickerRig.Has(sceneUnknown, "picker-banner-text"));
    }
}
