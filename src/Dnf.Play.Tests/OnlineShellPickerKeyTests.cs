using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 验收后补的流程层测试(变异存活项):选服务器、选频道两个阶段里
/// ① 方向键怎么接到界面的 <c>Move</c>(一行 = 网格的列数)、
/// ② 选频道阶段的空格(就绪 = 回车;读失败/没有频道 = 直连默认频道;忙时不发第二次连接)、
/// ③ 选频道里读不到目录之后的"重试"(读取中界面是"正在读"、Esc 取消后回到读失败页)、
/// ④ 选频道界面的忙标志只有"刷新"才是"刷新中"(登录后第一次读目录、读失败或没有频道之后的"重试"都不是刷新:读取挂着时忙标志是"不忙")、
/// ⑤ 重新读目录时旧提示要先清掉(读取一开始就清,不是等读完才变;重试与刷新都一样)。
/// 期望值都是手算的字面量:选服务器网格 2 列、选频道网格 3 列;频道端口 = 30000 + 序号,默认频道端口 31999;
/// ④⑤ 用 <c>PickerData.Many(2, 3)</c>(合成的 "服务器1"、"服务器2",各 3 个频道)。
/// </summary>
public sealed class OnlineShellPickerKeyTests
{
    private static void ClickCenter(PickerRig rig, HitRect rect, int pumps = 3)
    {
        rig.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, pumps);
    }

    // ------------------------------------------------------------------ 选服务器:方向键(每个键单独按一次、各断言一次选中序号)

    [Fact]
    public void On_the_server_screen_down_and_up_move_one_row_of_two_entries()
    {
        // 7 个服务器、都能选、都有频道
        var rig = new PickerRig(PickerData.Many(7));
        rig.ReachServerSelect();
        Assert.Equal(7, rig.Server.Servers.Count);
        Assert.Equal(0, rig.Server.Selected);

        rig.Key(OnlineShellKey.Down);
        Assert.Equal(2, rig.Server.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(4, rig.Server.Selected);
        rig.Key(OnlineShellKey.Up);
        Assert.Equal(2, rig.Server.Selected);
        rig.Key(OnlineShellKey.Up);
        Assert.Equal(0, rig.Server.Selected);
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
    }

    [Fact]
    public void On_the_server_screen_right_and_left_move_one_entry_and_down_and_up_work_from_the_second_column()
    {
        var rig = new PickerRig(PickerData.Many(7));
        rig.ReachServerSelect();

        rig.Key(OnlineShellKey.Right);
        Assert.Equal(1, rig.Server.Selected);
        rig.Key(OnlineShellKey.Left);
        Assert.Equal(0, rig.Server.Selected);

        rig.Key(OnlineShellKey.Right);
        Assert.Equal(1, rig.Server.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(3, rig.Server.Selected);
        rig.Key(OnlineShellKey.Up);
        Assert.Equal(1, rig.Server.Selected);
    }

    [Fact]
    public void On_the_server_screen_the_arrow_keys_do_not_move_past_the_first_or_the_last_entry()
    {
        var rig = new PickerRig(PickerData.Many(7));
        rig.ReachServerSelect();

        rig.Key(OnlineShellKey.Up);
        Assert.Equal(0, rig.Server.Selected);              // 0 号按"上"不动
        rig.Key(OnlineShellKey.Left);
        Assert.Equal(0, rig.Server.Selected);              // 0 号按"左"不动

        rig.Key(OnlineShellKey.Down);
        Assert.Equal(2, rig.Server.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(4, rig.Server.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(6, rig.Server.Selected);              // 最后一行只有 6 号
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(6, rig.Server.Selected);              // 再往下没有入口:不动
        rig.Key(OnlineShellKey.Right);
        Assert.Equal(6, rig.Server.Selected);              // 7 号不存在:不动
        rig.Key(OnlineShellKey.Left);
        Assert.Equal(5, rig.Server.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(5, rig.Server.Selected);              // 5 号的下一行(7 号)不存在:不动
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
    }

    // ------------------------------------------------------------------ 选频道:方向键

    [Fact]
    public void On_the_channel_screen_down_and_up_move_one_row_of_three_channels()
    {
        // 7 个频道、都进得去
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 7)));
        rig.ReachChannelSelect();
        Assert.Equal(7, rig.Channel.Channels.Count);
        Assert.Equal(0, rig.Channel.Selected);

        rig.Key(OnlineShellKey.Down);
        Assert.Equal(3, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(6, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Up);
        Assert.Equal(3, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Up);
        Assert.Equal(0, rig.Channel.Selected);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.ConnectCalls);
    }

    [Fact]
    public void On_the_channel_screen_right_and_left_move_one_channel_and_down_and_up_work_from_the_second_column()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 7)));
        rig.ReachChannelSelect();

        rig.Key(OnlineShellKey.Right);
        Assert.Equal(1, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Left);
        Assert.Equal(0, rig.Channel.Selected);

        rig.Key(OnlineShellKey.Right);
        Assert.Equal(1, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(4, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Up);
        Assert.Equal(1, rig.Channel.Selected);
    }

    [Fact]
    public void On_the_channel_screen_the_arrow_keys_do_not_move_past_the_first_or_the_last_channel()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 7)));
        rig.ReachChannelSelect();

        rig.Key(OnlineShellKey.Up);
        Assert.Equal(0, rig.Channel.Selected);             // 0 号按"上"不动
        rig.Key(OnlineShellKey.Left);
        Assert.Equal(0, rig.Channel.Selected);             // 0 号按"左"不动

        rig.Key(OnlineShellKey.Down);
        Assert.Equal(3, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(6, rig.Channel.Selected);             // 最后一行只有 6 号
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(6, rig.Channel.Selected);             // 再往下没有频道:不动
        rig.Key(OnlineShellKey.Right);
        Assert.Equal(6, rig.Channel.Selected);             // 7 号不存在:不动
        rig.Key(OnlineShellKey.Left);
        Assert.Equal(5, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(5, rig.Channel.Selected);             // 5 号的下一行(8 号)不存在:不动
        Assert.Equal(0, rig.Backend.ConnectCalls);
    }

    // ------------------------------------------------------------------ 选频道:空格

    [Fact]
    public void Space_on_the_ready_channel_screen_connects_the_selected_channel_just_like_enter()
    {
        // 默认频道端口 31999;目录里的频道端口是 30001..30003,选第二个(30002),所以连成默认频道一眼就能看出来
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3)), options => options.DefaultChannelPort = 31999);
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Right);
        Assert.Equal(1, rig.Channel.Selected);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);

        rig.Key(OnlineShellKey.Space);

        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(0, rig.Backend.SwitchCalls);
        Assert.Equal(new ChannelTarget(30002), rig.Backend.LastTarget);
        Assert.Equal(1, rig.Directory.LoadCalls);                    // 没有去重读目录
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Space_on_a_ready_channel_screen_with_nothing_startable_connects_nothing()
    {
        var directory = PickerData.Directory(new DirectoryServer("希洛克", new[]
        {
            PickerData.Channel(1, ChannelCongestion.Full),
            PickerData.Channel(2, noTarget: true),
        }));
        var rig = new PickerRig(directory);
        rig.ReachChannelSelect();
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(-1, rig.Channel.Selected);

        rig.Key(OnlineShellKey.Space);

        Assert.Equal(0, rig.Backend.ConnectCalls);                   // 就绪页的空格是"游戏开始",不是直连默认频道
        Assert.Equal(1, rig.Directory.LoadCalls);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Space_on_the_failed_channel_screen_connects_the_default_channel_instead_of_rereading_the_directory()
    {
        PickerRig rig = ReachFailedChannelScreen();
        Assert.Equal(1, rig.Backend.ConnectCalls);                   // 上一次直连失败的那一次
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));

        rig.Key(OnlineShellKey.Space);

        Assert.Equal(2, rig.Backend.ConnectCalls);
        Assert.Equal(new ChannelTarget(31999), rig.Backend.LastTarget);
        Assert.Equal(1, rig.Directory.LoadCalls);                    // 不是去重读目录
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Space_on_the_empty_channel_screen_connects_the_default_channel_instead_of_rereading_the_directory()
    {
        PickerRig rig = ReachEmptyChannelScreen();
        Assert.Equal(0, rig.Backend.ConnectCalls);

        rig.Key(OnlineShellKey.Space);

        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(new ChannelTarget(31999), rig.Backend.LastTarget);
        Assert.Equal(2, rig.Directory.LoadCalls);                    // 初次读 + 刷新各一次,空格没有再读
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Space_while_connecting_never_sends_a_second_connect_on_the_ready_or_the_failed_channel_screen()
    {
        // 就绪页:回车开始连(连接挂着),再按空格
        var ready = new PickerRig();
        var pendingReady = new TaskCompletionSource<OnlineConnectOutcome>();
        ready.Backend.OnConnect = (t, ct) => pendingReady.Task;
        ready.ReachChannelSelect();
        ready.Key(OnlineShellKey.Enter);
        Assert.Equal(ChannelBusy.Connecting, ready.Channel.Busy);
        ready.Key(OnlineShellKey.Space);
        ready.Key(OnlineShellKey.Space);
        Assert.Equal(1, ready.Backend.ConnectCalls);
        Assert.Equal(1, ready.Directory.LoadCalls);
        Assert.Equal(ChannelBusy.Connecting, ready.Channel.Busy);
        pendingReady.SetResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
        ready.Pump();
        Assert.Equal(OnlineShellStage.CharacterSelect, ready.Flow.Stage);
        Assert.Equal(1, ready.Backend.ConnectCalls);

        // 读失败页:空格直连默认频道(连接挂着),再按空格
        var failed = new PickerRig();
        var pendingFailed = new TaskCompletionSource<OnlineConnectOutcome>();
        failed.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        failed.Backend.OnConnect = (t, ct) => pendingFailed.Task;
        failed.ReachServerSelect();
        failed.Key(OnlineShellKey.Space);
        Assert.Equal(OnlineShellStage.ChannelSelect, failed.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, failed.Channel.Phase);
        Assert.Equal(ChannelBusy.Connecting, failed.Channel.Busy);
        failed.Key(OnlineShellKey.Space);
        failed.Key(OnlineShellKey.Space);
        Assert.Equal(1, failed.Backend.ConnectCalls);
        Assert.Equal(1, failed.Directory.LoadCalls);
        pendingFailed.SetResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
        failed.Pump();
        Assert.Equal(OnlineShellStage.CharacterSelect, failed.Flow.Stage);
        Assert.Equal(1, failed.Backend.ConnectCalls);
    }

    // ------------------------------------------------------------------ 选频道里读不到目录之后的重试

    /// <summary>走到"选频道停在读失败页":读目录失败 → 选服务器页直连默认频道 → 连接也失败,留在选频道(这时没有目录)。</summary>
    private static PickerRig ReachFailedChannelScreen()
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
        rig.ReachServerSelect();
        rig.Key(OnlineShellKey.Space);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, rig.Channel.Phase);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(1, rig.Directory.LoadCalls);
        return rig;
    }

    /// <summary>走到"选频道停在没有频道页":刷新读到的目录里已经没有选定的服务器。</summary>
    private static PickerRig ReachEmptyChannelScreen()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3), PickerData.Server("卡恩", 2, 4)));
        rig.ReachChannelSelect();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("卡恩", 2, 4))));
        ClickCenter(rig, ChannelSelectScreen.RefreshRect());
        Assert.Equal(PickerPhase.Empty, rig.Channel.Phase);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(2, rig.Directory.LoadCalls);
        return rig;
    }

    [Theory]
    [InlineData("failed", "enter")]
    [InlineData("failed", "retry-button")]
    [InlineData("failed", "refresh-button")]
    [InlineData("empty", "enter")]
    [InlineData("empty", "retry-button")]
    [InlineData("empty", "refresh-button")]
    public void Retrying_on_the_channel_screen_shows_loading_while_the_read_hangs_and_esc_cancels_back_to_the_failed_page(string from, string how)
    {
        PickerRig rig = from == "failed" ? ReachFailedChannelScreen() : ReachEmptyChannelScreen();
        int reads = rig.Directory.LoadCalls;
        ServerDirectory directoryBefore = rig.Flow.Directory;           // 读失败页没有目录(null);没有频道页留着最近一次读到的
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;

        switch (how)
        {
            case "enter":
                rig.Key(OnlineShellKey.Enter);
                break;
            case "retry-button":
                ClickCenter(rig, ChannelSelectScreen.RetryRect());
                break;
            default:
                ClickCenter(rig, ChannelSelectScreen.RefreshRect());
                break;
        }

        // 读取中:停在选频道、界面显示"正在读"(不是还停在失败页)、恰好一个在途读取
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Loading, rig.Channel.Phase);
        Assert.True(rig.Flow.Busy);
        Assert.Equal(reads + 1, rig.Directory.LoadCalls);
        Assert.False(rig.Directory.LastToken.IsCancellationRequested);

        // Esc 只取消这次读取:回到读失败页、提示"已取消"、没有在途操作、没有再发读取
        rig.Key(OnlineShellKey.Escape);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, rig.Channel.Phase);
        Assert.Equal(OnlineNoticeKind.Cancelled, rig.Flow.Notice.Kind);
        Assert.Equal(OnlineNoticeKind.Cancelled, rig.Channel.Notice.Kind);
        Assert.True(rig.Directory.LastToken.IsCancellationRequested);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(reads + 1, rig.Directory.LoadCalls);
        Assert.Contains(rig.Rig.LinesFor("channel"), l => l.Contains("event=fail") && l.Contains("reason=cancelled"));

        // 迟到的结果不算数(这次读取已经被丢掉)
        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 4))));
        rig.Pump();
        Assert.Equal(PickerPhase.Failed, rig.Channel.Phase);
        Assert.Same(directoryBefore, rig.Flow.Directory);

        // 退路还在:回车再读一次,这次读到了
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 2))));
        rig.Key(OnlineShellKey.Enter);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(2, rig.Channel.Channels.Count);
        Assert.Equal(reads + 2, rig.Directory.LoadCalls);
    }

    // ------------------------------------------------------------------ 读目录挂着时的现场:忙标志与提示(④⑤ 共用)

    /// <summary>走到"选服务器停在读失败页":第一次读目录失败(网络错误),阶段机与界面上都有这条失败提示。</summary>
    private static PickerRig ReachFailedServerScreen()
    {
        var rig = new PickerRig(PickerData.Many(2, 3));
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.ReachServerSelect();
        Assert.Equal(PickerPhase.Failed, rig.Server.Phase);
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.NotNull(rig.Server.Notice);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Server.Notice.Kind);
        Assert.Equal(1, rig.Directory.LoadCalls);
        return rig;
    }

    /// <summary>让接下来的读目录一直挂着(不完成),返回之后用来放行它的完成源。</summary>
    private static TaskCompletionSource<ServerDirectoryResult> HangDirectory(PickerRig rig)
    {
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;
        return pending;
    }

    /// <summary>在读失败页(或没有频道页)上发起"重试":回车、重试按钮,或选频道界面的刷新按钮(那里它与重试是同一个意图)。</summary>
    private static void RetryRead(PickerRig rig, bool onServerScreen, string how)
    {
        switch (how)
        {
            case "enter":
                rig.Key(OnlineShellKey.Enter);
                break;
            case "retry-button":
                ClickCenter(rig, onServerScreen ? ServerSelectScreen.RetryRect() : ChannelSelectScreen.RetryRect());
                break;
            default:
                ClickCenter(rig, ChannelSelectScreen.RefreshRect());
                break;
        }
    }

    // ------------------------------------------------------------------ ④ 忙标志:只有"刷新"才是"刷新中"

    [Fact]
    public void While_the_first_directory_read_hangs_after_login_the_channel_screen_is_not_busy_and_stays_not_busy_after_it_finishes()
    {
        var rig = new PickerRig(PickerData.Many(2, 3));
        TaskCompletionSource<ServerDirectoryResult> pending = HangDirectory(rig);

        rig.Rig.Fill();
        rig.Rig.Submit();
        rig.Pump(3);

        // 登录后第一次读目录在途:选服务器界面是"正在读"、恰好一个在途读取;这不是刷新,选频道界面的忙标志是"不忙"
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Loading, rig.Server.Phase);
        Assert.True(rig.Flow.Busy);
        Assert.Equal(1, rig.Directory.LoadCalls);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        rig.Pump(5);                                                  // 再推几帧:忙标志每帧都按在途操作重算,仍然不是"刷新中"
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.True(rig.Flow.Busy);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Many(2, 3)));
        rig.Pump(3);

        Assert.Equal(PickerPhase.Ready, rig.Server.Phase);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
    }

    [Fact]
    public void A_refresh_from_the_ready_channel_screen_shows_refreshing_while_it_hangs_and_none_again_after_it_finishes()
    {
        var rig = new PickerRig(PickerData.Many(2, 3));
        rig.ReachChannelSelect();
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        TaskCompletionSource<ServerDirectoryResult> pending = HangDirectory(rig);

        ClickCenter(rig, ChannelSelectScreen.RefreshRect());

        // 刷新在途:忙标志是"刷新中"、旧列表还在(就绪、3 个频道)、恰好一个在途读取
        Assert.True(rig.Flow.Busy);
        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Equal(ChannelBusy.Refreshing, rig.Channel.Busy);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(3, rig.Channel.Channels.Count);
        rig.Pump(5);
        Assert.Equal(ChannelBusy.Refreshing, rig.Channel.Busy);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Many(2, 3)));
        rig.Pump(3);

        Assert.False(rig.Flow.Busy);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(3, rig.Channel.Channels.Count);
    }

    [Theory]
    [InlineData("server", "enter")]
    [InlineData("server", "retry-button")]
    [InlineData("channel-failed", "enter")]
    [InlineData("channel-failed", "retry-button")]
    [InlineData("channel-failed", "refresh-button")]
    [InlineData("channel-empty", "enter")]
    [InlineData("channel-empty", "retry-button")]
    public void A_retry_after_a_failed_or_empty_read_is_not_a_refresh_so_the_channel_screen_is_not_busy_while_it_hangs(string screen, string how)
    {
        bool onServerScreen = screen == "server";
        PickerRig rig = onServerScreen ? ReachFailedServerScreen() : screen == "channel-failed" ? ReachFailedChannelScreen() : ReachEmptyChannelScreen();
        int reads = rig.Directory.LoadCalls;
        TaskCompletionSource<ServerDirectoryResult> pending = HangDirectory(rig);

        RetryRead(rig, onServerScreen, how);

        // 读取中:恰好一个在途读取;界面是"正在读"(不是还停在失败页)
        Assert.True(rig.Flow.Busy);
        Assert.Equal(reads + 1, rig.Directory.LoadCalls);
        Assert.Equal(onServerScreen ? OnlineShellStage.ServerSelect : OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Loading, onServerScreen ? rig.Server.Phase : rig.Channel.Phase);
        // 这是读失败之后的补读,不是刷新(界面上没有旧列表):选频道界面的忙标志必须是"不忙",不能是"刷新中"
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        rig.Pump(5);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.True(rig.Flow.Busy);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Many(2, 3)));
        rig.Pump(3);

        Assert.False(rig.Flow.Busy);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
    }

    // ------------------------------------------------------------------ ⑤ 重新读目录时旧提示要先清掉

    [Theory]
    [InlineData("enter")]
    [InlineData("retry-button")]
    public void Retrying_the_failed_server_screen_clears_the_old_notice_the_moment_the_read_starts(string how)
    {
        PickerRig rig = ReachFailedServerScreen();          // 前提:失败页上有一条"网络错误"的提示
        TaskCompletionSource<ServerDirectoryResult> pending = HangDirectory(rig);

        RetryRead(rig, true, how);

        // 重读开始、还没读完:旧提示已经清掉(三处都是:阶段机、选服务器界面、选频道界面),界面是"正在读"
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Loading, rig.Server.Phase);
        Assert.True(rig.Flow.Busy);
        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Server.Notice);
        Assert.Null(rig.Channel.Notice);
        rig.Pump(5);                                         // 挂着的这段时间里一直没有提示
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Server.Notice);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Many(2, 3)));
        rig.Pump(3);

        Assert.Equal(PickerPhase.Ready, rig.Server.Phase);
        Assert.Null(rig.Flow.Notice);
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("retry-button")]
    [InlineData("refresh-button")]
    public void Retrying_the_failed_channel_screen_clears_the_old_connect_notice_the_moment_the_read_starts(string how)
    {
        PickerRig rig = ReachFailedChannelScreen();         // 前提:直连默认频道失败,失败页上有一条"连接失败"的提示
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, rig.Flow.Notice.Kind);
        Assert.NotNull(rig.Channel.Notice);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, rig.Channel.Notice.Kind);
        TaskCompletionSource<ServerDirectoryResult> pending = HangDirectory(rig);

        RetryRead(rig, false, how);

        // 重读开始、还没读完:旧提示已经清掉,界面是"正在读"
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Loading, rig.Channel.Phase);
        Assert.True(rig.Flow.Busy);
        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Channel.Notice);
        rig.Pump(5);
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Channel.Notice);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Many(2, 3)));
        rig.Pump(3);

        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Null(rig.Flow.Notice);
    }

    [Theory]
    [InlineData("failed-refresh")]
    [InlineData("failed-connect")]
    public void Refreshing_again_while_a_notice_is_showing_clears_it_the_moment_the_read_starts_and_keeps_the_old_list(string source)
    {
        var rig = new PickerRig(PickerData.Many(2, 3));
        rig.ReachChannelSelect();
        OnlineNoticeKind expected;
        if (source == "failed-refresh")
        {
            // 刷新读失败:保留旧列表、出提示
            rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
            ClickCenter(rig, ChannelSelectScreen.RefreshRect());
            expected = OnlineNoticeKind.NetworkError;
        }
        else
        {
            // 连频道失败:留在就绪页、旧列表还在、出提示
            rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
            rig.Key(OnlineShellKey.Enter);
            expected = OnlineNoticeKind.ConnectFailed;
        }
        // 前提:就绪页、旧列表(3 个频道)还在,提示可见,没有在途操作
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(3, rig.Channel.Channels.Count);
        Assert.False(rig.Flow.Busy);
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(expected, rig.Flow.Notice.Kind);
        Assert.NotNull(rig.Channel.Notice);
        Assert.Equal(expected, rig.Channel.Notice.Kind);
        int reads = rig.Directory.LoadCalls;
        TaskCompletionSource<ServerDirectoryResult> pending = HangDirectory(rig);

        ClickCenter(rig, ChannelSelectScreen.RefreshRect());

        // 刷新开始、还没读完:提示已经清掉(不是等读完才变),旧列表还在,忙标志是"刷新中"
        Assert.True(rig.Flow.Busy);
        Assert.Equal(reads + 1, rig.Directory.LoadCalls);
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Channel.Notice);
        Assert.Equal(ChannelBusy.Refreshing, rig.Channel.Busy);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(3, rig.Channel.Channels.Count);
        rig.Pump(5);
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Channel.Notice);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Many(2, 3)));
        rig.Pump(3);

        Assert.False(rig.Flow.Busy);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.Null(rig.Flow.Notice);
        Assert.Equal(3, rig.Channel.Channels.Count);
    }
}
