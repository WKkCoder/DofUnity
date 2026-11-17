using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Play.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// 宿主一侧的"真界面"开关(r18-serverui):开着时登录之后停在选服务器、从真后端读频道目录、按玩家选的频道连;不开(默认)时一切照旧,
/// 两个阶段自动放行、不读目录、用默认频道端口连。会话用假的,后端是真的(<see cref="OnlineShellBackend"/>),所以连"目录项给的主机"这一步也走了真实的翻译。
/// </summary>
public sealed class OnlineShellHostServerUiTests
{
    private sealed class UiRig : IDisposable
    {
        public UiRig(bool serverUi, Action<FakeSessionPort> onPort = null, ServerPickerScene picker = null, int defaultChannelPort = 31999)
        {
            Rig = new BackendRig(true, onPort);
            var setup = new OnlineShellSetup(Rig.Template, OnlineShellConfigSource.ConfigFile, null, defaultChannelPort, BackendRig.User, null);
            Host = new OnlineShellHost(setup, Rig.Backend, null, null, null, serverUi, picker);
        }

        public BackendRig Rig { get; }

        public OnlineShellHost Host { get; }

        public OnlineShellFlow Flow => Host.Flow;

        public FakeSessionPort Port => Rig.Port(0);

        public long Now = 1000;

        public void Tick(OnlineShellFrameInput input = default)
        {
            Host.Tick(input, 16, Now += 16);
        }

        public void Until(Func<bool> done)
        {
            HostPump.Until(Host, ref Now, done);
        }

        public void Key(OnlineShellFrameInput input)
        {
            Tick(input);
        }

        public void Enter() => Tick(new OnlineShellFrameInput { Enter = true });

        /// <summary>预填了账号、焦点在口令框:打口令回车登录。</summary>
        public void Login()
        {
            Tick(new OnlineShellFrameInput { Text = BackendRig.Password });
            Enter();
        }

        /// <summary>登录并等到选服务器读完目录(成功或失败都算读完)。</summary>
        public void ReachServerSelect()
        {
            Login();
            Until(() => Host.Stage == OnlineShellStage.ServerSelect && Flow.ServerScreen.Phase != PickerPhase.Loading);
        }

        public void Dispose()
        {
            Host.Dispose();
        }
    }

    private static Action<FakeSessionPort> Directory(ChannelDirectorySnapshot snapshot)
    {
        return p => p.OnDirectory = ct => Task.FromResult(new OnlineDirectoryReport(ChannelDirectoryLoadOutcome.Ok, snapshot));
    }

    private static Action<FakeSessionPort> DirectoryFails(ChannelDirectoryLoadOutcome outcome)
    {
        return p => p.OnDirectory = ct => Task.FromResult(new OnlineDirectoryReport(outcome, null));
    }

    // ------------------------------------------------------------------ 不开真界面:一切照旧

    [Fact]
    public void Without_the_server_ui_the_two_stages_are_passed_automatically_and_the_directory_is_never_read()
    {
        using var rig = new UiRig(serverUi: false);

        rig.Login();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(0, rig.Port.DirectoryCalls);
        Assert.Equal(1, rig.Port.ConnectCalls);
        Assert.Equal(31999, rig.Port.LastPort);                            // 默认频道端口
        Assert.Equal(0, rig.Port.ConnectHostCalls);
        Assert.Null(rig.Flow.Directory);
    }

    [Fact]
    public void The_default_host_builds_no_picker_screens_and_ignores_the_pointer()
    {
        using var rig = new UiRig(serverUi: false);
        rig.Login();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Null(rig.Flow.ServerScreen);
        Assert.Null(rig.Flow.ChannelScreen);
        rig.Tick(new OnlineShellFrameInput { HasPointer = true, PointerX = 400, PointerY = 300 });      // 没有真界面时指针没有任何反应
        rig.Tick(default);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Host.Stage);
    }

    // ------------------------------------------------------------------ 开着:读目录、按选的连

    [Fact]
    public void With_the_server_ui_the_host_stops_at_the_server_stage_and_reads_the_directory()
    {
        using var rig = new UiRig(serverUi: true);

        rig.ReachServerSelect();

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Host.Stage);
        Assert.Equal(PickerPhase.Ready, rig.Flow.ServerScreen.Phase);
        Assert.Equal(1, rig.Port.DirectoryCalls);
        Assert.Equal(0, rig.Port.ConnectCalls);                            // 还没选,什么也没连
        Assert.Equal(0, rig.Port.ConnectHostCalls);
        Assert.Equal(new[] { "希洛克", "卡恩" }, rig.Flow.ServerScreen.Servers.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Walking_through_the_screens_connects_to_the_channel_the_player_picked()
    {
        using var rig = new UiRig(serverUi: true);
        rig.ReachServerSelect();

        rig.Enter();                                                       // 选定第一个服务器
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);
        Assert.Equal(PickerPhase.Ready, rig.Flow.ChannelScreen.Phase);
        Assert.Equal(3, rig.Flow.ChannelScreen.Channels.Count);
        rig.Tick(new OnlineShellFrameInput { Right = true });              // 第二个频道(序号 2)
        Assert.Equal(1, rig.Flow.ChannelScreen.Selected);
        rig.Enter();                                                       // 游戏开始:连接
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(1, rig.Port.ConnectCalls);
        Assert.Equal(31002, rig.Port.LastPort);
        Assert.Equal(0, rig.Port.ConnectHostCalls);

        rig.Enter();                                                       // 选角回车进入
        rig.Until(() => rig.Host.InGame);
    }

    [Fact]
    public void The_second_server_lists_its_own_channels_with_continued_numbers()
    {
        using var rig = new UiRig(serverUi: true);
        rig.ReachServerSelect();

        rig.Tick(new OnlineShellFrameInput { Right = true });              // 卡恩
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        Assert.Equal("卡恩", rig.Flow.ChannelScreen.Server.Name);
        Assert.Equal(new[] { 4, 5 }, rig.Flow.ChannelScreen.Channels.Select(c => c.Number).ToArray());
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);
        Assert.Equal(31004, rig.Port.LastPort);
    }

    [Fact]
    public void A_channel_with_an_ip_host_in_the_directory_is_connected_by_that_host()
    {
        ChannelDirectorySnapshot snapshot = DirectoryTestData.Snapshot(DirectoryTestData.Group("希洛克",
            DirectoryTestData.Entry("希洛克", 1, host: "10.1.2.3", port: 31555)));
        using var rig = new UiRig(serverUi: true, Directory(snapshot));
        rig.ReachServerSelect();
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(1, rig.Port.ConnectHostCalls);
        Assert.Equal("10.1.2.3", rig.Port.LastHost);
        Assert.Equal(31555, rig.Port.LastPort);
        Assert.Equal(0, rig.Port.ConnectCalls);
    }

    [Fact]
    public void A_channel_with_a_domain_host_in_the_directory_cannot_be_entered_and_nothing_is_connected()
    {
        ChannelDirectorySnapshot snapshot = DirectoryTestData.Snapshot(DirectoryTestData.Group("希洛克",
            DirectoryTestData.Entry("希洛克", 1, host: "game.example.com", port: 31555)));
        using var rig = new UiRig(serverUi: true, Directory(snapshot));
        rig.ReachServerSelect();
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        Assert.False(rig.Flow.ChannelScreen.CanStart);
        rig.Enter();
        for (int i = 0; i < 20; i++)
        {
            rig.Tick();
        }

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Host.Stage);
        Assert.Equal(0, rig.Port.ConnectCalls);
        Assert.Equal(0, rig.Port.ConnectHostCalls);
    }

    [Fact]
    public void A_full_channel_cannot_be_entered_and_the_next_free_one_can()
    {
        ChannelDirectorySnapshot snapshot = DirectoryTestData.Snapshot(DirectoryTestData.Group("希洛克",
            DirectoryTestData.Entry("希洛克", 1, tier: ChannelCongestionTier.Crowded, full: true),
            DirectoryTestData.Entry("希洛克", 2)));
        using var rig = new UiRig(serverUi: true, Directory(snapshot));
        rig.ReachServerSelect();
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        Assert.Equal(1, rig.Flow.ChannelScreen.Selected);                  // 默认选中第一个能进的,不是第一个
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(31002, rig.Port.LastPort);
    }

    // ------------------------------------------------------------------ 失败与出口

    [Fact]
    public void A_failed_directory_read_leaves_a_failed_server_screen_and_enter_reads_it_again()
    {
        int reads = 0;
        using var rig = new UiRig(serverUi: true, p =>
        {
            p.OnDirectory = ct =>
            {
                reads++;
                return reads == 1
                    ? Task.FromResult(new OnlineDirectoryReport(ChannelDirectoryLoadOutcome.IoError, null))
                    : Task.FromResult(new OnlineDirectoryReport(ChannelDirectoryLoadOutcome.Ok, DirectoryTestData.Default()));
            };
        });

        rig.ReachServerSelect();
        Assert.Equal(PickerPhase.Failed, rig.Flow.ServerScreen.Phase);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.ServerScreen.Notice.Kind);

        rig.Enter();                                                       // 重试
        rig.Until(() => rig.Flow.ServerScreen.Phase == PickerPhase.Ready);

        Assert.Equal(2, rig.Port.DirectoryCalls);
        Assert.Equal(2, rig.Flow.ServerScreen.Servers.Count);
    }

    [Fact]
    public void A_failed_directory_read_can_be_left_by_connecting_the_default_channel_directly()
    {
        using var rig = new UiRig(serverUi: true, DirectoryFails(ChannelDirectoryLoadOutcome.FrameError), defaultChannelPort: 31888);
        rig.ReachServerSelect();
        Assert.Equal(PickerPhase.Failed, rig.Flow.ServerScreen.Phase);

        rig.Tick(new OnlineShellFrameInput { Space = true });              // 失败时空格 = 直连默认频道
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(1, rig.Port.ConnectCalls);
        Assert.Equal(31888, rig.Port.LastPort);
    }

    [Fact]
    public void An_empty_directory_shows_the_empty_screen()
    {
        using var rig = new UiRig(serverUi: true, Directory(DirectoryTestData.Snapshot()));

        rig.ReachServerSelect();

        Assert.Equal(PickerPhase.Empty, rig.Flow.ServerScreen.Phase);
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Host.Stage);
    }

    [Fact]
    public void A_failed_connect_stays_on_the_channel_screen_with_a_notice_and_the_player_can_try_again()
    {
        int attempts = 0;
        using var rig = new UiRig(serverUi: true, p =>
        {
            p.OnConnect = (port, ct) =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromResult(new OnlineConnectReport(OnlineFailure.ConnectFailed, OnlineCharacterListStatus.NotReceived, null))
                    : Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, FakeSessionPort.FakeRoster(1)));
            };
        });
        rig.ReachServerSelect();
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        rig.Enter();
        rig.Until(() => rig.Flow.ChannelScreen.Notice != null);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Host.Stage);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, rig.Flow.ChannelScreen.Notice.Kind);

        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);
        Assert.Equal(2, rig.Port.ConnectCalls);
    }

    [Fact]
    public void Escape_from_the_channel_screen_returns_to_the_server_screen_without_reading_the_directory_again()
    {
        using var rig = new UiRig(serverUi: true);
        rig.ReachServerSelect();
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        rig.Tick(new OnlineShellFrameInput { Escape = true });
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ServerSelect);

        Assert.Equal(PickerPhase.Ready, rig.Flow.ServerScreen.Phase);
        Assert.Equal(1, rig.Port.DirectoryCalls);
    }

    [Fact]
    public void Leaving_to_the_login_screen_and_signing_in_again_reads_the_directory_from_the_new_session()
    {
        using var rig = new UiRig(serverUi: true);
        rig.ReachServerSelect();

        rig.Tick(new OnlineShellFrameInput { Escape = true });             // 选服务器 Esc → 回登录
        rig.Until(() => rig.Host.Stage == OnlineShellStage.Login);
        rig.Tick(new OnlineShellFrameInput { Text = BackendRig.Password });
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ServerSelect && rig.Flow.ServerScreen.Phase == PickerPhase.Ready);

        Assert.Equal(2, rig.Rig.PortCount);
        Assert.Equal(1, rig.Rig.Port(1).DirectoryCalls);
    }

    [Fact]
    public void The_quit_button_on_the_channel_screen_asks_the_host_to_quit()
    {
        using var rig = new UiRig(serverUi: true);
        rig.ReachServerSelect();
        rig.Enter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        rig.Flow.ChannelScreen.Quit();
        rig.Until(() => rig.Host.QuitRequested);

        Assert.True(rig.Host.QuitRequested);
    }

    // ------------------------------------------------------------------ 指针

    [Fact]
    public void The_pointer_in_a_frame_hovers_the_active_screen_and_no_pointer_clears_the_hover()
    {
        var sprites = PickerSprites.Create();
        using var rig = new UiRig(serverUi: true, picker: new ServerPickerScene(sprites));
        rig.ReachServerSelect();
        Assert.DoesNotContain(rig.Flow.ServerScreen.Build(0), s => s.AnimationPath == "server-start-glow" && s.TintAlpha == PickerArt.GlowHigh);

        rig.Tick(new OnlineShellFrameInput { HasPointer = true, PointerX = PickerArt.StartX + 5, PointerY = PickerArt.StartY + 5 });
        Assert.Contains(rig.Flow.ServerScreen.Build(0), s => s.AnimationPath == "server-start-glow" && s.TintAlpha == PickerArt.GlowHigh);

        rig.Tick(new OnlineShellFrameInput { HasPointer = true, PointerX = 3, PointerY = 3 });          // 指针移到别处:不再悬停
        Assert.DoesNotContain(rig.Flow.ServerScreen.Build(0), s => s.AnimationPath == "server-start-glow" && s.TintAlpha == PickerArt.GlowHigh);

        rig.Tick(new OnlineShellFrameInput { HasPointer = true, PointerX = PickerArt.StartX + 5, PointerY = PickerArt.StartY + 5 });
        rig.Tick(default);                                                                              // 这一帧没有指针(鼠标移出窗口)
        Assert.DoesNotContain(rig.Flow.ServerScreen.Build(0), s => s.AnimationPath == "server-start-glow" && s.TintAlpha == PickerArt.GlowHigh);
    }

    [Fact]
    public void A_click_in_a_frame_on_the_start_button_selects_the_server_even_when_the_same_frame_carries_the_pointer()
    {
        using var rig = new UiRig(serverUi: true);
        rig.ReachServerSelect();

        rig.Tick(new OnlineShellFrameInput
        {
            HasPointer = true,
            PointerX = PickerArt.StartX + 5,
            PointerY = PickerArt.StartY + 5,
            Click = true,
            ClickX = PickerArt.StartX + 5,
            ClickY = PickerArt.StartY + 5,
        });
        rig.Until(() => rig.Host.Stage == OnlineShellStage.ChannelSelect);

        Assert.Equal("希洛克", rig.Flow.ChannelScreen.Server.Name);
    }

}
