using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 选服务器、选频道真界面接进阶段机之后的流程:假后端 + 假目录来源驱动(0/1/N 个服务器与频道、目录读取的每一种失败、刷新、连接成功与失败、换频道、变更服务器、
/// 目录读不到时的出口、取消、超时、断线)。真界面是可选注入:不注入时的行为由既有的外壳测试守着(这里只补"没注入就是占位"的几条)。
/// </summary>
public sealed class OnlineShellPickerFlowTests
{
    private static HitRect Rect(int x, int y, int w, int h) => new HitRect(x, y, w, h);

    private static (int X, int Y) Center(HitRect r) => (r.X + r.Width / 2, r.Y + r.Height / 2);

    private static void ClickCenter(PickerRig rig, HitRect rect, int pumps = 3)
    {
        (int x, int y) = Center(rect);
        rig.Click(x, y, pumps);
    }

    // ------------------------------------------------------------------ 注入与不注入

    [Fact]
    public void Without_a_directory_source_the_stages_stay_placeholders_and_no_picker_exists()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);

        rig.Fill();
        rig.Submit();
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Null(rig.Flow.ServerScreen);
        Assert.Null(rig.Flow.ChannelScreen);
        Assert.Null(rig.Flow.Directory);
        Assert.IsNotType<ServerSelectScreen>(rig.Flow.Scene);
        // 占位阶段的按键语义不变:回车是"重试"(这里没有在途操作,什么也不发生),Esc 回登录
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Pump();
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    [Fact]
    public void A_directory_source_with_auto_pass_is_refused_at_construction()
    {
        var directory = new FakeDirectoryBackend();
        var backend = new FakeOnlineBackend();

        Assert.Throws<ArgumentException>(() => new OnlineShellFlow(backend, new OnlineShellOptions { ChannelDirectory = directory }));
        Assert.Throws<ArgumentException>(() => new OnlineShellFlow(backend, new OnlineShellOptions { ChannelDirectory = directory, AutoPassPlaceholders = true }));
    }

    [Fact]
    public void Hover_and_pointer_calls_are_harmless_without_a_picker()
    {
        var rig = new ShellRig();

        rig.Flow.Hover(10, 10);
        rig.Flow.HoverNone();
        rig.Pump();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    // ------------------------------------------------------------------ 选服务器:读目录

    [Fact]
    public void After_login_the_server_screen_shows_loading_and_the_directory_is_read_once()
    {
        var rig = new PickerRig();
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;

        rig.Rig.Fill();
        rig.Rig.Submit();
        rig.Pump(2);

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Same(rig.Server, rig.Flow.Scene);
        Assert.Equal(PickerPhase.Loading, rig.Server.Phase);
        Assert.True(rig.Flow.Busy);
        Assert.Equal(1, rig.Directory.LoadCalls);
        Assert.Equal(0, rig.Backend.ConnectCalls);          // 没有自动放行

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 3))));
        rig.Pump(2);

        Assert.Equal(PickerPhase.Ready, rig.Server.Phase);
        Assert.False(rig.Flow.Busy);
        Assert.Single(rig.Server.Servers);
        Assert.Equal(0, rig.Server.Selected);
        Assert.Equal(1, rig.Directory.LoadCalls);
    }

    [Fact]
    public void An_empty_directory_shows_the_empty_state_and_enter_retries()
    {
        var rig = new PickerRig(PickerData.Directory());

        rig.ReachServerSelect();

        Assert.Equal(PickerPhase.Empty, rig.Server.Phase);
        Assert.False(rig.Server.CanStart);
        Assert.NotNull(rig.Flow.Directory);
        Assert.Empty(rig.Flow.Directory.Servers);

        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 2))));
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Equal(PickerPhase.Ready, rig.Server.Phase);
        Assert.Single(rig.Server.Servers);
    }

    [Theory]
    [InlineData(OnlineNoticeKind.NetworkError)]
    [InlineData(OnlineNoticeKind.ProtocolError)]
    [InlineData(OnlineNoticeKind.Rejected)]
    [InlineData(OnlineNoticeKind.Unexpected)]
    public void Every_directory_failure_keeps_the_server_screen_with_its_notice_and_enter_retries(OnlineNoticeKind kind)
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(kind));

        rig.ReachServerSelect();

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, rig.Server.Phase);
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(kind, rig.Flow.Notice.Kind);
        Assert.Equal(kind, rig.Server.Notice.Kind);
        Assert.False(rig.Server.CanStart);
        Assert.Null(rig.Flow.Directory);
        Assert.Contains(rig.Rig.LinesFor("server"), l => l.Contains("event=fail") && l.Contains("reason=" + OnlineNotices.Code(kind)));

        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 1))));
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Equal(PickerPhase.Ready, rig.Server.Phase);
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Server.Notice);
    }

    [Fact]
    public void A_failed_result_without_a_kind_becomes_an_unexpected_error()
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.None));

        rig.ReachServerSelect();

        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void A_null_result_or_a_null_task_is_an_unexpected_failure()
    {
        var nullResult = new PickerRig();
        nullResult.Directory.OnLoad = ct => Task.FromResult<ServerDirectoryResult>(null);
        nullResult.ReachServerSelect();
        Assert.Equal(PickerPhase.Failed, nullResult.Server.Phase);
        Assert.Equal(OnlineNoticeKind.Unexpected, nullResult.Flow.Notice.Kind);

        var nullTask = new PickerRig();
        nullTask.Directory.OnLoad = ct => null;
        nullTask.ReachServerSelect();
        Assert.Equal(PickerPhase.Failed, nullTask.Server.Phase);
        Assert.Equal(OnlineNoticeKind.Unexpected, nullTask.Flow.Notice.Kind);
    }

    [Fact]
    public void A_directory_source_that_throws_is_an_unexpected_failure_and_a_connection_error_goes_back_to_login()
    {
        var thrown = new PickerRig();
        thrown.Directory.OnLoad = ct => throw new InvalidOperationException("synthetic");
        thrown.ReachServerSelect();
        Assert.Equal(OnlineShellStage.ServerSelect, thrown.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, thrown.Server.Phase);
        Assert.Equal(OnlineNoticeKind.Unexpected, thrown.Flow.Notice.Kind);

        var dropped = new PickerRig();
        dropped.Directory.OnLoad = ct => Task.FromException<ServerDirectoryResult>(new IOException("synthetic"));
        dropped.Rig.Fill();
        dropped.Rig.Submit();
        dropped.Pump(3);
        Assert.Equal(OnlineShellStage.Login, dropped.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, dropped.Flow.Notice.Kind);
        Assert.True(dropped.Backend.DisconnectCalls >= 1);
    }

    [Fact]
    public void A_directory_read_that_never_finishes_times_out_into_the_failed_state()
    {
        var rig = new PickerRig(configure: o => o.TimeoutMs = 5000);
        rig.Directory.OnLoad = ct => new TaskCompletionSource<ServerDirectoryResult>().Task;
        rig.Rig.Fill();
        rig.Rig.Submit();
        rig.Pump(2);
        Assert.Equal(PickerPhase.Loading, rig.Server.Phase);

        rig.Poll(4999);
        Assert.Equal(PickerPhase.Loading, rig.Server.Phase);
        rig.Poll(1);

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, rig.Server.Phase);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.Equal("timeout", rig.Flow.Notice.Reason);
        Assert.False(rig.Flow.Busy);
        Assert.True(rig.Directory.LastToken.IsCancellationRequested);
    }

    [Fact]
    public void Esc_while_the_directory_is_loading_cancels_the_read_and_goes_back_to_login()
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => new TaskCompletionSource<ServerDirectoryResult>().Task;
        rig.Rig.Fill();
        rig.Rig.Submit();
        rig.Pump(2);
        Assert.True(rig.Flow.Busy);

        rig.Key(OnlineShellKey.Escape);

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
        Assert.True(rig.Directory.LastToken.IsCancellationRequested);
        Assert.True(rig.Backend.DisconnectCalls >= 1);
    }

    [Fact]
    public void A_late_directory_result_after_cancel_is_ignored()
    {
        var rig = new PickerRig();
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;
        rig.Rig.Fill();
        rig.Rig.Submit();
        rig.Pump(2);
        rig.Key(OnlineShellKey.Escape);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 2))));
        rig.Pump();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Null(rig.Flow.Directory);
    }

    // ------------------------------------------------------------------ 选服务器:选择

    [Fact]
    public void One_server_is_preselected_and_enter_enters_its_channels()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3)));

        rig.ReachServerSelect();
        Assert.Equal(0, rig.Server.Selected);
        Assert.True(rig.Server.CanStart);
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Same(rig.Channel, rig.Flow.Scene);
        Assert.Equal("希洛克", rig.Flow.PickedServer.Name);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(3, rig.Channel.Channels.Count);
        Assert.Equal(0, rig.Channel.Selected);
        Assert.Equal(0, rig.Backend.ConnectCalls);          // 选定服务器不发网络请求
    }

    [Fact]
    public void Several_servers_are_chosen_by_the_selection_and_the_channel_screen_shows_that_server()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3), PickerData.Server("卡恩", 2, 4), PickerData.Server("普雷", 4, 6)));

        rig.ReachServerSelect();
        rig.Key(OnlineShellKey.Right);
        Assert.Equal(1, rig.Server.Selected);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(1, rig.Server.Selected);               // 下一行没有入口:不动
        rig.Key(OnlineShellKey.Left);
        rig.Key(OnlineShellKey.Down);
        Assert.Equal(2, rig.Server.Selected);
        rig.Key(OnlineShellKey.Space);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal("普雷", rig.Flow.PickedServer.Name);
        Assert.Equal("普雷", rig.Channel.Server.Name);
        Assert.Equal(4, rig.Channel.Channels.Count);
        Assert.Equal(6, rig.Channel.Channels[0].Number);
    }

    [Fact]
    public void Clicking_an_entry_selects_it_and_clicking_start_chooses_it()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3), PickerData.Server("卡恩", 2, 4)));
        rig.ReachServerSelect();

        Assert.True(rig.Server.TryEntryRect(1, out HitRect entry));
        ClickCenter(rig, entry);
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(1, rig.Server.Selected);

        ClickCenter(rig, PickerArt.StartRect());

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal("卡恩", rig.Flow.PickedServer.Name);
    }

    [Fact]
    public void A_server_without_channels_cannot_be_selected_or_chosen()
    {
        var rig = new PickerRig(PickerData.Directory(new DirectoryServer("希洛克", null), PickerData.Server("卡恩", 2)));
        rig.ReachServerSelect();

        Assert.Equal(1, rig.Server.Selected);               // 灰牌被跳过,选中第一个能选的
        Assert.True(rig.Server.TryEntryRect(0, out HitRect gray));
        ClickCenter(rig, gray);
        Assert.Equal(1, rig.Server.Selected);
        rig.Key(OnlineShellKey.Left);
        Assert.Equal(1, rig.Server.Selected);
    }

    [Fact]
    public void An_external_choose_server_is_ignored_while_the_screen_has_nothing_to_choose()
    {
        var rig = new PickerRig();
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;
        rig.Rig.Fill();
        rig.Rig.Submit();
        rig.Pump(2);

        rig.Flow.ChooseServer();
        rig.Pump();
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 2))));
        rig.Pump(2);
        rig.Flow.ChooseServer();
        rig.Pump();
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Esc_on_the_server_screen_goes_back_to_login_and_forgets_the_directory()
    {
        var rig = new PickerRig();
        rig.ReachServerSelect();
        Assert.NotNull(rig.Flow.Directory);

        rig.Key(OnlineShellKey.Escape);

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Null(rig.Flow.Directory);
        Assert.Null(rig.Flow.PickedServer);
        Assert.True(rig.Backend.DisconnectCalls >= 1);

        // 下一次登录是全新的会话:目录重读(账号还留在登录界面里,口令被清掉了,焦点在口令框)
        rig.Flow.Type("synthetic-pw");
        rig.Rig.Submit();
        rig.Pump(3);
        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Equal(PickerPhase.Ready, rig.Flow.ServerScreen.Phase);
    }

    [Fact]
    public void The_back_button_equivalent_on_the_server_screen_is_escape_only()
    {
        // 选服务器界面没有"返回"按钮(基线上没有);Esc 经 Flow.Key 走原有返回,界面自己的 Back 意图也一样
        var rig = new PickerRig();
        rig.ReachServerSelect();

        rig.Server.Back();
        rig.Pump();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    // ------------------------------------------------------------------ 目录读不到的出口

    [Fact]
    public void Direct_connect_from_the_failed_server_screen_connects_the_default_channel()
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.ReachServerSelect();
        Assert.Equal(PickerPhase.Failed, rig.Server.Phase);

        ClickCenter(rig, ServerSelectScreen.DirectRect());

        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(new ChannelTarget(31999), rig.Backend.LastTarget);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Contains(rig.Rig.LinesFor("server"), l => l.Contains("reason=direct"));
    }

    [Fact]
    public void Space_on_the_failed_server_screen_is_the_direct_connect_exit_and_on_the_empty_one_too()
    {
        var failed = new PickerRig();
        failed.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.ProtocolError));
        failed.ReachServerSelect();
        failed.Key(OnlineShellKey.Space);
        Assert.Equal(1, failed.Backend.ConnectCalls);
        Assert.Equal(new ChannelTarget(31999), failed.Backend.LastTarget);

        var empty = new PickerRig(PickerData.Directory());
        empty.ReachServerSelect();
        empty.Key(OnlineShellKey.Space);
        Assert.Equal(1, empty.Backend.ConnectCalls);
        Assert.Equal(new ChannelTarget(31999), empty.Backend.LastTarget);
    }

    [Fact]
    public void Clicking_retry_on_the_failed_server_screen_reads_the_directory_again()
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.ReachServerSelect();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 2))));

        ClickCenter(rig, ServerSelectScreen.RetryRect());

        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Equal(PickerPhase.Ready, rig.Server.Phase);
    }

    [Fact]
    public void Direct_connect_failure_leaves_the_channel_screen_failed_with_retry_and_default_exits()
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
        rig.ReachServerSelect();

        rig.Key(OnlineShellKey.Space);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, rig.Channel.Phase);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, rig.Flow.Notice.Kind);

        // 出口 1:默认频道再连一次(这次成功)
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
        ClickCenter(rig, ChannelSelectScreen.DirectRect());
        Assert.Equal(2, rig.Backend.ConnectCalls);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Retry_on_the_failed_channel_screen_reads_the_directory_and_shows_the_channels()
    {
        var rig = new PickerRig();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
        rig.ReachServerSelect();
        rig.Key(OnlineShellKey.Space);
        Assert.Equal(PickerPhase.Failed, rig.Channel.Phase);

        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 4))));
        ClickCenter(rig, ChannelSelectScreen.RetryRect());

        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(4, rig.Channel.Channels.Count);
        Assert.Equal("希洛克", rig.Flow.PickedServer.Name);
    }

    [Fact]
    public void After_a_direct_connect_the_retried_directory_opens_the_server_that_holds_the_connected_channel()
    {
        // 默认频道是第 5 号,在第二个服务器里:直连成功、回到选频道(没有目录,读失败页)、重试读到目录,显示的应该是连着的频道所在的服务器,而不是第一个
        var rig = new PickerRig(null, options => options.DefaultChannelPort = PickerData.PortOf(5));
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
        rig.ReachServerSelect();
        rig.Key(OnlineShellKey.Space);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(PickerData.Target(5), rig.Backend.LastTarget);
        rig.Key(OnlineShellKey.Escape);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Failed, rig.Channel.Phase);
        Assert.Null(rig.Flow.PickedServer);

        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 3), PickerData.Server("卡恩", 2, 4))));
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal("卡恩", rig.Flow.PickedServer.Name);
        Assert.Equal("卡恩", rig.Channel.Server.Name);
        Assert.Equal(1, rig.Channel.Selected);                                  // 第 5 号是卡恩的第二个频道
        Assert.Equal(PickerData.Target(5), rig.Channel.SelectedChannel.Target);
    }

    // ------------------------------------------------------------------ 选频道:连接

    [Fact]
    public void Start_connects_the_selected_channel_with_its_target_and_enters_character_select()
    {
        var rig = new PickerRig();
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Right);
        Assert.Equal(1, rig.Channel.Selected);

        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(0, rig.Backend.SwitchCalls);
        Assert.Equal(PickerData.Target(2), rig.Backend.LastTarget);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.NotNull(rig.Flow.Characters);
    }

    [Fact]
    public void A_channel_with_a_host_connects_with_that_host()
    {
        var directory = PickerData.Directory(new DirectoryServer("希洛克", new[]
        {
            PickerData.Channel(1, host: "10.1.2.3"),
            PickerData.Channel(2),
        }));
        var rig = new PickerRig(directory);
        rig.ReachChannelSelect();

        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(new ChannelTarget(PickerData.PortOf(1), "10.1.2.3"), rig.Backend.LastTarget);
        Assert.Equal("10.1.2.3", rig.Backend.LastTarget.Host);
    }

    [Fact]
    public void While_connecting_the_screen_is_busy_and_ignores_start_and_navigation()
    {
        var rig = new PickerRig();
        var pending = new TaskCompletionSource<OnlineConnectOutcome>();
        rig.Backend.OnConnect = (t, ct) => pending.Task;
        rig.ReachChannelSelect();

        rig.Key(OnlineShellKey.Enter);

        Assert.True(rig.Flow.Busy);
        Assert.Equal(ChannelBusy.Connecting, rig.Channel.Busy);
        Assert.False(rig.Channel.CanStart);
        Assert.False(rig.Channel.CanRefresh);
        int selected = rig.Channel.Selected;
        rig.Key(OnlineShellKey.Right);
        Assert.Equal(selected, rig.Channel.Selected);
        rig.Key(OnlineShellKey.Enter);
        ClickCenter(rig, PickerArt.StartRect());
        Assert.Equal(1, rig.Backend.ConnectCalls);

        pending.SetResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
        rig.Pump();
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Theory]
    [InlineData(OnlineNoticeKind.ConnectFailed)]
    [InlineData(OnlineNoticeKind.GameLoginRejected)]
    [InlineData(OnlineNoticeKind.CharacterListMissing)]
    [InlineData(OnlineNoticeKind.NetworkError)]
    public void A_failed_connect_keeps_the_stage_the_list_and_the_selection_and_enter_retries_the_same_channel(OnlineNoticeKind kind)
    {
        var rig = new PickerRig();
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(kind));
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Right);

        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(kind, rig.Flow.Notice.Kind);
        Assert.Equal(kind, rig.Channel.Notice.Kind);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(1, rig.Channel.Selected);
        Assert.Equal(3, rig.Channel.Channels.Count);
        Assert.False(rig.Flow.Busy);

        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(2, rig.Backend.ConnectCalls);
        Assert.Equal(PickerData.Target(2), rig.Backend.LastTarget);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void A_connect_that_throws_or_times_out_stays_on_the_channel_screen_with_a_notice()
    {
        var thrown = new PickerRig();
        thrown.Backend.OnConnect = (t, ct) => throw new InvalidOperationException("synthetic");
        thrown.ReachChannelSelect();
        thrown.Key(OnlineShellKey.Enter);
        Assert.Equal(OnlineShellStage.ChannelSelect, thrown.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Unexpected, thrown.Flow.Notice.Kind);

        var slow = new PickerRig(configure: o => o.TimeoutMs = 3000);
        slow.Backend.OnConnect = (t, ct) => new TaskCompletionSource<OnlineConnectOutcome>().Task;
        slow.ReachChannelSelect();
        slow.Key(OnlineShellKey.Enter);
        slow.Poll(3000);
        Assert.Equal(OnlineShellStage.ChannelSelect, slow.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.NetworkError, slow.Flow.Notice.Kind);
        Assert.Equal(ChannelBusy.None, slow.Channel.Busy);
    }

    [Fact]
    public void Esc_while_connecting_cancels_the_connect_and_stays_on_the_channel_screen()
    {
        var rig = new PickerRig();
        rig.Backend.OnConnect = (t, ct) => new TaskCompletionSource<OnlineConnectOutcome>().Task;
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Enter);
        Assert.Equal(ChannelBusy.Connecting, rig.Channel.Busy);

        rig.Key(OnlineShellKey.Escape);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.True(rig.Backend.LastToken.IsCancellationRequested);
        Assert.Equal(OnlineNoticeKind.Cancelled, rig.Flow.Notice.Kind);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.True(rig.Channel.CanStart);
    }

    [Fact]
    public void A_full_channel_and_a_channel_without_a_port_cannot_be_started()
    {
        var directory = PickerData.Directory(new DirectoryServer("希洛克", new[]
        {
            PickerData.Channel(1, ChannelCongestion.Full),
            PickerData.Channel(2, noTarget: true),
            PickerData.Channel(3),
        }));
        var rig = new PickerRig(directory);
        rig.ReachChannelSelect();

        Assert.Equal(2, rig.Channel.Selected);              // 前两个进不去,选中第一个进得去的
        Assert.True(rig.Channel.TryItemRect(0, out HitRect full));
        ClickCenter(rig, full);
        Assert.Equal(2, rig.Channel.Selected);
        Assert.Equal(PickerLabel.ChannelFull, rig.Channel.Hint);
        Assert.True(rig.Channel.TryItemRect(1, out HitRect none));
        ClickCenter(rig, none);
        Assert.Equal(PickerLabel.ChannelUnavailable, rig.Channel.Hint);
        Assert.Equal(0, rig.Backend.ConnectCalls);

        rig.Key(OnlineShellKey.Enter);
        Assert.Equal(PickerData.Target(3), rig.Backend.LastTarget);
    }

    [Fact]
    public void A_server_whose_channels_are_all_unusable_shows_nothing_to_start()
    {
        var directory = PickerData.Directory(new DirectoryServer("希洛克", new[]
        {
            PickerData.Channel(1, ChannelCongestion.Full),
            PickerData.Channel(2, noTarget: true),
        }));
        var rig = new PickerRig(directory);
        rig.ReachChannelSelect();

        Assert.Equal(-1, rig.Channel.Selected);
        Assert.False(rig.Channel.CanStart);
        rig.Key(OnlineShellKey.Enter);
        ClickCenter(rig, PickerArt.StartRect());

        Assert.Equal(0, rig.Backend.ConnectCalls);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
    }

    [Fact]
    public void More_than_fifteen_channels_page_and_the_chosen_channel_comes_from_the_current_page()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 20)));
        rig.ReachChannelSelect();
        Assert.Equal(2, rig.Channel.PageCount);
        Assert.Equal(0, rig.Channel.Page);

        rig.Key(OnlineShellKey.Tab);
        Assert.Equal(1, rig.Channel.Page);
        Assert.Equal(15, rig.Channel.Selected);
        rig.Key(OnlineShellKey.ShiftTab);
        Assert.Equal(0, rig.Channel.Page);
        ClickCenter(rig, ChannelSelectScreen.NextPageRect());
        Assert.Equal(1, rig.Channel.Page);

        rig.Key(OnlineShellKey.Right);
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(PickerData.Target(17), rig.Backend.LastTarget);
    }

    // ------------------------------------------------------------------ 变更服务器、换频道

    [Fact]
    public void Esc_on_the_channel_screen_returns_to_the_server_screen_with_the_cached_directory_and_selection()
    {
        var rig = new PickerRig(PickerData.Many(3, 2));
        rig.ReachServerSelect();
        rig.Key(OnlineShellKey.Right);
        rig.Key(OnlineShellKey.Enter);
        Assert.Equal("服务器2", rig.Flow.PickedServer.Name);
        int serial = rig.Flow.StageSerial;

        rig.Key(OnlineShellKey.Escape);

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.NotEqual(serial, rig.Flow.StageSerial);
        Assert.Same(rig.Server, rig.Flow.Scene);
        Assert.Equal(PickerPhase.Ready, rig.Server.Phase);
        Assert.Equal(1, rig.Server.Selected);
        Assert.Equal(1, rig.Directory.LoadCalls);           // 目录是缓存的,没有重读
        Assert.Equal(0, rig.Backend.ConnectCalls);
    }

    [Fact]
    public void The_change_server_button_is_the_same_as_esc()
    {
        var rig = new PickerRig();
        rig.ReachChannelSelect();

        ClickCenter(rig, ChannelSelectScreen.ChangeServerRect());

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(1, rig.Directory.LoadCalls);
    }

    [Fact]
    public void Esc_twice_walks_the_back_chain_channel_server_login()
    {
        var rig = new PickerRig();
        rig.ReachChannelSelect();

        rig.Key(OnlineShellKey.Escape);
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        rig.Key(OnlineShellKey.Escape);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    [Fact]
    public void Changing_server_then_choosing_another_shows_the_other_servers_channels()
    {
        var rig = new PickerRig(PickerData.Many(2, 3));
        rig.ReachChannelSelect();
        Assert.Equal("服务器1", rig.Channel.Server.Name);
        ClickCenter(rig, ChannelSelectScreen.ChangeServerRect());
        rig.Key(OnlineShellKey.Right);

        rig.Key(OnlineShellKey.Enter);

        Assert.Equal("服务器2", rig.Channel.Server.Name);
        Assert.Equal(4, rig.Channel.Channels[0].Number);
        rig.Key(OnlineShellKey.Enter);
        Assert.Equal(PickerData.Target(4), rig.Backend.LastTarget);
    }

    [Fact]
    public void Coming_back_from_the_game_the_channel_screen_selects_the_current_channel_and_choosing_another_switches()
    {
        var rig = new PickerRig();
        rig.ReachInGame();
        Assert.Equal(1, rig.Backend.ConnectCalls);

        rig.Flow.SwapCharacter();
        rig.Pump();

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal("希洛克", rig.Channel.Server.Name);
        Assert.Equal(PickerData.Target(1), rig.Channel.SelectedChannel.Target);   // 当前连着的频道是预选

        rig.Key(OnlineShellKey.Right);
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(1, rig.Backend.SwitchCalls);
        Assert.Equal(PickerData.Target(2), rig.Backend.LastTarget);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void A_failed_switch_stays_with_a_notice_and_the_retry_reconnects_instead_of_switching()
    {
        var rig = new PickerRig();
        rig.ReachInGame();
        rig.Flow.SwapCharacter();
        rig.Pump();
        rig.Backend.OnSwitch = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
        rig.Key(OnlineShellKey.Right);

        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(1, rig.Backend.SwitchCalls);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, rig.Flow.Notice.Kind);
        Assert.Equal(PickerData.Target(2), rig.Channel.SelectedChannel.Target);

        // 切换失败之后旧连接已经没了:再试走"连"而不是"换"(既有阶段机语义)
        rig.Key(OnlineShellKey.Enter);
        Assert.Equal(2, rig.Backend.ConnectCalls);
        Assert.Equal(1, rig.Backend.SwitchCalls);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Quit_disconnects_and_requests_the_host_to_quit_even_while_connecting()
    {
        var rig = new PickerRig();
        rig.Backend.OnConnect = (t, ct) => new TaskCompletionSource<OnlineConnectOutcome>().Task;
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Enter);
        Assert.True(rig.Flow.Busy);
        int disconnects = rig.Backend.DisconnectCalls;

        ClickCenter(rig, ChannelSelectScreen.QuitRect());

        Assert.True(rig.Flow.QuitRequested);
        Assert.False(rig.Flow.Busy);
        Assert.True(rig.Backend.LastToken.IsCancellationRequested);
        Assert.True(rig.Backend.DisconnectCalls > disconnects);
    }

    [Fact]
    public void Quit_from_an_idle_channel_screen_also_disconnects()
    {
        var rig = new PickerRig();
        rig.ReachChannelSelect();

        ClickCenter(rig, ChannelSelectScreen.QuitRect());

        Assert.True(rig.Flow.QuitRequested);
        Assert.True(rig.Backend.DisconnectCalls >= 1);
        Assert.Equal(0, rig.Backend.ConnectCalls);
    }

    // ------------------------------------------------------------------ 刷新

    [Fact]
    public void Refresh_rereads_the_directory_and_replaces_the_list_keeping_the_selected_channel()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3)));
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Right);
        Assert.Equal(PickerData.Target(2), rig.Channel.SelectedChannel.Target);
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;

        ClickCenter(rig, ChannelSelectScreen.RefreshRect());

        Assert.Equal(ChannelBusy.Refreshing, rig.Channel.Busy);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);   // 刷新期间旧列表还在
        Assert.Equal(3, rig.Channel.Channels.Count);
        Assert.False(rig.Channel.CanStart);

        pending.SetResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 5))));
        rig.Pump();

        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.Equal(5, rig.Channel.Channels.Count);
        Assert.Equal(PickerData.Target(2), rig.Channel.SelectedChannel.Target);
        Assert.Equal(2, rig.Directory.LoadCalls);
        Assert.Equal(5, rig.Flow.Directory.Servers[0].Channels.Count);
        Assert.Equal(5, rig.Flow.PickedServer.Channels.Count);
    }

    [Theory]
    [InlineData(OnlineNoticeKind.NetworkError)]
    [InlineData(OnlineNoticeKind.ProtocolError)]
    [InlineData(OnlineNoticeKind.Rejected)]
    public void A_failed_refresh_keeps_the_old_list_and_shows_the_notice(OnlineNoticeKind kind)
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3)));
        rig.ReachChannelSelect();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(kind));

        ClickCenter(rig, ChannelSelectScreen.RefreshRect());

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(3, rig.Channel.Channels.Count);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.Equal(kind, rig.Channel.Notice.Kind);
        Assert.True(rig.Channel.CanStart);
        Assert.Equal(3, rig.Flow.Directory.Servers[0].Channels.Count);
    }

    [Fact]
    public void Esc_during_a_refresh_cancels_only_the_refresh_and_keeps_the_link_state()
    {
        var rig = new PickerRig();
        rig.ReachInGame();
        rig.Flow.SwapCharacter();
        rig.Pump();
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;
        ClickCenter(rig, ChannelSelectScreen.RefreshRect());
        Assert.Equal(ChannelBusy.Refreshing, rig.Channel.Busy);

        rig.Key(OnlineShellKey.Escape);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.True(rig.Directory.LastToken.IsCancellationRequested);
        Assert.Equal(ChannelBusy.None, rig.Channel.Busy);
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(OnlineNoticeKind.Cancelled, rig.Flow.Notice.Kind);

        // 连着的频道没丢:接着选别的频道仍是"换"
        rig.Key(OnlineShellKey.Right);
        rig.Key(OnlineShellKey.Enter);
        Assert.Equal(1, rig.Backend.SwitchCalls);
        Assert.Equal(1, rig.Backend.ConnectCalls);
    }

    [Fact]
    public void A_refresh_that_no_longer_lists_the_picked_server_shows_it_as_having_no_channels()
    {
        var rig = new PickerRig(PickerData.Directory(PickerData.Server("希洛克", 3), PickerData.Server("卡恩", 2, 4)));
        rig.ReachChannelSelect();
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("卡恩", 2, 4))));

        ClickCenter(rig, ChannelSelectScreen.RefreshRect());

        Assert.Equal(PickerPhase.Empty, rig.Channel.Phase);
        Assert.Equal("希洛克", rig.Channel.Server.Name);
        Assert.False(rig.Channel.CanStart);
        // 出口:再读(这次目录里又有了)
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(PickerData.Server("希洛克", 2))));
        ClickCenter(rig, ChannelSelectScreen.RetryRect());
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal(2, rig.Channel.Channels.Count);
    }

    [Fact]
    public void The_refresh_button_is_ignored_while_connecting()
    {
        var rig = new PickerRig();
        rig.Backend.OnConnect = (t, ct) => new TaskCompletionSource<OnlineConnectOutcome>().Task;
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Enter);

        ClickCenter(rig, ChannelSelectScreen.RefreshRect());

        Assert.Equal(1, rig.Directory.LoadCalls);
        Assert.Equal(ChannelBusy.Connecting, rig.Channel.Busy);
    }

    // ------------------------------------------------------------------ 断线、日志、悬停与时间

    [Fact]
    public void A_disconnect_event_on_the_channel_screen_returns_to_login_and_resets_the_picker()
    {
        var rig = new PickerRig();
        rig.ReachChannelSelect();

        rig.Backend.RaiseDisconnected();
        rig.Pump();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.Null(rig.Flow.Directory);
        Assert.Null(rig.Flow.PickedServer);
    }

    [Fact]
    public void Log_lines_name_the_stage_event_and_reason_without_addresses_or_ports()
    {
        var directory = PickerData.Directory(new DirectoryServer("希洛克", new[] { PickerData.Channel(1, host: "10.9.8.7") }));
        var rig = new PickerRig(directory);
        rig.ReachChannelSelect();
        rig.Key(OnlineShellKey.Enter);

        string all = string.Join("\n", rig.Rig.Lines);

        Assert.Contains("DNF_ONLINE stage=server event=ok reason=directory", all);
        Assert.Contains("DNF_ONLINE stage=server event=ok reason=choose", all);
        Assert.Contains("DNF_ONLINE stage=channel event=ok reason=connected", all);
        Assert.DoesNotContain("10.9.8.7", all);
        Assert.DoesNotContain("30001", all);
        Assert.DoesNotContain("希洛克", all);
    }

    [Fact]
    public void Hover_and_advance_reach_only_the_active_screen()
    {
        var rig = new PickerRig(configure: o => o.PickerScene = new ServerPickerScene(PickerSprites.Create()));
        rig.ReachServerSelect();
        long serverAge = rig.Server.AgeMs;
        long channelAge = rig.Channel.AgeMs;

        rig.Flow.Advance(250);
        Assert.Equal(serverAge + 250, rig.Server.AgeMs);
        Assert.Equal(channelAge, rig.Channel.AgeMs);

        rig.Flow.Hover(PickerArt.StartX + 5, PickerArt.StartY + 5);
        Assert.Contains(rig.Server.Build(0), s => s.AnimationPath == "server-start-glow" && s.TintAlpha == PickerArt.GlowHigh);
        rig.Flow.HoverNone();
        Assert.DoesNotContain(rig.Server.Build(0), s => s.AnimationPath == "server-start-glow" && s.TintAlpha == PickerArt.GlowHigh);

        rig.Key(OnlineShellKey.Enter);
        long before = rig.Channel.AgeMs;
        rig.Flow.Advance(100);
        Assert.Equal(before + 100, rig.Channel.AgeMs);
        rig.Flow.Hover(ChannelSelectScreen.ChangeServerRect().X + 3, ChannelSelectScreen.ChangeServerRect().Y + 3);
        Assert.NotEmpty(rig.Channel.Build(0));
    }

    [Fact]
    public void The_pointer_reaches_the_channel_screen_unshifted_down_to_the_last_pixel()
    {
        var rig = new PickerRig(configure: o => o.PickerScene = new ServerPickerScene(PickerSprites.Create()));
        rig.ReachChannelSelect();
        Assert.True(rig.Channel.TryItemRect(1, out HitRect second));
        Assert.True(rig.Channel.TryItemRect(2, out HitRect third));

        rig.Flow.Hover(second.X + second.Width - 1, second.Y + 1);              // 第二个频道(没选中)右边缘的最后一个像素
        SceneSprite hover = PickerSprites.One(rig.Channel.Build(0), "channel-item-hover");
        Assert.Equal((second.X, second.Y), (hover.ScreenX, hover.ScreenY));

        rig.Flow.Hover(second.X + second.Width, second.Y + 1);                  // 出去一个像素(两列之间的缝)
        Assert.False(PickerRig.Has(rig.Channel.Build(0), "channel-item-hover"));

        rig.Flow.Hover(third.X, third.Y + third.Height - 1);                    // 第三个频道左下角的第一个像素
        hover = PickerSprites.One(rig.Channel.Build(0), "channel-item-hover");
        Assert.Equal((third.X, third.Y), (hover.ScreenX, hover.ScreenY));
    }

    [Fact]
    public void The_pointer_reaches_the_server_screen_unshifted_down_to_the_last_pixel()
    {
        var rig = new PickerRig(configure: o => o.PickerScene = new ServerPickerScene(PickerSprites.Create()));
        rig.ReachServerSelect();
        Assert.True(rig.Server.TryEntryRect(1, out HitRect second));

        rig.Flow.Hover(second.X + second.Width - 1, second.Y + second.Height - 1);       // 第二个入口右下角的最后一个像素
        SceneSprite hover = PickerSprites.One(rig.Server.Build(0), "server-entry-hover");
        Assert.Equal((second.X, second.Y), (hover.ScreenX, hover.ScreenY));

        rig.Flow.Hover(second.X + second.Width, second.Y);                                // 出去一个像素
        Assert.False(PickerRig.Has(rig.Server.Build(0), "server-entry-hover"));
    }

    [Fact]
    public void Keys_have_no_effect_outside_the_two_picker_stages_and_the_loading_stage_still_cancels_with_esc()
    {
        var rig = new PickerRig();
        var pending = new TaskCompletionSource<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => pending.Task;
        rig.ReachCharacterSelect();

        rig.Key(OnlineShellKey.Enter);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        rig.Key(OnlineShellKey.Escape);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);      // 加载里 Esc 取消选角、回选频道(既有语义)
        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
    }

    [Fact]
    public void The_stage_serial_changes_when_the_scene_changes()
    {
        var rig = new PickerRig();
        rig.ReachServerSelect();
        int serverSerial = rig.Flow.StageSerial;
        ISceneSource serverScene = rig.Flow.Scene;

        rig.Key(OnlineShellKey.Enter);

        Assert.NotEqual(serverSerial, rig.Flow.StageSerial);
        Assert.NotSame(serverScene, rig.Flow.Scene);
        Assert.IsType<ChannelSelectScreen>(rig.Flow.Scene);
    }

    [Fact]
    public void Each_screen_builds_a_nonempty_scene_in_every_phase_of_the_flow()
    {
        var rig = new PickerRig();
        var pending = new TaskCompletionSource<ServerDirectoryResult>();
        rig.Directory.OnLoad = ct => pending.Task;
        rig.Rig.Fill();
        rig.Rig.Submit();
        rig.Pump(2);
        Assert.NotEmpty(rig.Flow.Scene.Build(0));            // 读目录中
        pending.SetResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.Pump();
        Assert.NotEmpty(rig.Flow.Scene.Build(0));            // 失败
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Many(3)));
        rig.Key(OnlineShellKey.Enter);
        Assert.NotEmpty(rig.Flow.Scene.Build(0));            // 就绪
        rig.Key(OnlineShellKey.Enter);
        Assert.NotEmpty(rig.Flow.Scene.Build(0));            // 选频道
    }

    [Fact]
    public void Returning_from_a_special_channel_locates_the_server_and_preselects_that_special_channel()
    {
        var server1 = new DirectoryServer("希洛克", new[] { PickerData.Channel(11) });
        var server2 = new DirectoryServer(
            "卡恩",
            new[] { PickerData.Channel(21) },
            false,
            new[] { new DirectoryChannel(1, "亡者峡谷", ChannelCongestion.Smooth, PickerData.Target(1), 20, 70, slotFrame: 8) });
        var rig = new PickerRig(PickerData.Directory(server1, server2), o => o.DefaultChannelPort = PickerData.PortOf(1));
        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError));
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));

        // 直连默认特殊频道(亡者峡谷 ch1)进选角,切回选频道后重读目录
        rig.ReachServerSelect();
        rig.Key(OnlineShellKey.Space);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        rig.Key(OnlineShellKey.Escape);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);

        rig.Directory.OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(PickerData.Directory(server1, server2)));
        rig.Key(OnlineShellKey.Enter);

        Assert.Equal(PickerPhase.Ready, rig.Channel.Phase);
        Assert.Equal("卡恩", rig.Flow.PickedServer.Name);
        Assert.Equal(0, rig.Channel.SelectedSpecial);
        Assert.Equal(1, rig.Channel.SelectedChannel.Number);
        Assert.Equal(PickerData.Target(1), rig.Channel.SelectedChannel.Target);
    }
}
