using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 验收后补的流程层测试(变异存活项):选服务器、选频道两个阶段里
/// ① 方向键怎么接到界面的 <c>Move</c>(一行 = 网格的列数)、
/// ② 选频道阶段的空格(就绪 = 回车;读失败/没有频道 = 直连默认频道;忙时不发第二次连接)、
/// ③ 选频道里读不到目录之后的"重试"(读取中界面是"正在读"、Esc 取消后回到读失败页)。
/// 期望值都是手算的字面量:选服务器网格 2 列、选频道网格 3 列;频道端口 = 30000 + 序号,默认频道端口 31999。
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
}
