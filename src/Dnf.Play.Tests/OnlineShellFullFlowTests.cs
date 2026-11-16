using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 整条流程(假后端):登录 → 选服务器占位 → 选频道占位 → 选角占位 → 进入游戏,每一步看画面(各阶段画的是什么)、日志与后端的调用顺序。
/// 没有任何真实网络:后端是 <see cref="FakeOnlineBackend"/>,账号口令与角色都是合成的。
/// </summary>
public class OnlineShellFullFlowTests
{
    private static bool SameFrame(SpriteFrame a, SpriteFrame b)
    {
        return a.Width == b.Width && a.Height == b.Height && a.Rgba.AsSpan().SequenceEqual(b.Rgba);
    }

    private static SpriteFrame Expect(string text, int scale, uint color) => new OverlayArt().Text(text, scale, color);

    private static string[] Tags(ISceneSource scene) => scene.Build(0).Select(s => s.AnimationPath).ToArray();

    private static SceneSprite One(ISceneSource scene, string tag) => Assert.Single(scene.Build(0), s => s.AnimationPath == tag);

    private static CharacterSelectScene ArtScene()
    {
        var sprites = new ShellFakeSprites();
        sprites.AddBackground();
        return new CharacterSelectScene(sprites);
    }

    [Fact]
    public void The_whole_flow_with_placeholders_auto_passed_runs_login_connect_select_in_order_and_hands_over()
    {
        var rig = new ShellRig(o => o.Art = ArtScene());

        // 1. 登录界面
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Contains("login-title", Tags(rig.Flow.Scene));
        Assert.Contains("online-background", Tags(rig.Flow.Scene));

        // 2. 提交:一次轮询里经过登录、选服务器(自动放行),停在"连接频道"(请求在途)
        rig.Fill("player_01", "synthetic-pw");
        rig.Submit();
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.True(SameFrame(Expect("CONNECTING...", 3, OverlayArt.White), One(rig.Flow.Scene, "online-status-title").Frame));
        Assert.True(SameFrame(Expect("ESC CANCEL", 1, OverlayArt.White), One(rig.Flow.Scene, "online-status-hint").Frame));
        Assert.Contains("online-background", Tags(rig.Flow.Scene));

        // 3. 连上了:选角占位界面(底图来自选角界面,两个占位角色)
        rig.Pump(1);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        string[] select = Tags(rig.Flow.Scene);
        Assert.Contains("select-char-background", select);
        Assert.DoesNotContain("login-title", select);
        Assert.Equal(2, rig.Flow.Characters.Entries.Count);

        // 4. 选第二个角色并确认:进入游戏,外壳把控制权交出去(Scene 为空)
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Null(rig.Flow.Scene);
        Assert.Equal(1, rig.Flow.SelectedSlot);

        // 5. 后端调用顺序:登录、连接、选角,一次不多;会话一直开着直到外壳退出
        Assert.Equal(new[] { "login", "connect", "select" }, rig.Backend.Calls);
        Assert.Equal("player_01", rig.Backend.LastUser);
        Assert.Equal(30011, rig.Backend.LastTarget.Port);
        Assert.Equal(1, rig.Backend.LastSlot);
        rig.Flow.Shutdown();
        Assert.Equal(new[] { "login", "connect", "select", "disconnect" }, rig.Backend.Calls);

        // 6. 日志:每个阶段变化一行,没有账号、口令、地址、角色名
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=login event=enter reason=start",
            "DNF_ONLINE stage=login event=ok reason=auth",
            "DNF_ONLINE stage=server event=enter reason=login_ok",
            "DNF_ONLINE stage=server event=ok reason=auto",
            "DNF_ONLINE stage=channel event=enter reason=auto",
            "DNF_ONLINE stage=channel event=ok reason=connected",
            "DNF_ONLINE stage=charselect event=enter reason=connected",
            "DNF_ONLINE stage=charselect event=ok reason=confirm",
            "DNF_ONLINE stage=loading event=enter reason=select",
            "DNF_ONLINE stage=loading event=ok reason=selected",
            "DNF_ONLINE stage=ingame event=enter reason=handover",
        }, rig.Lines);
    }

    [Fact]
    public void The_whole_flow_with_the_placeholder_stages_stepped_by_hand_shows_each_placeholder_screen()
    {
        var rig = new ShellRig(o =>
        {
            o.AutoPassPlaceholders = false;
            o.Art = ArtScene();
        });
        rig.Fill();
        rig.Submit();
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.True(SameFrame(Expect("SELECTING SERVER...", 3, OverlayArt.White), One(rig.Flow.Scene, "online-status-title").Frame));

        rig.Flow.ChooseServer();
        rig.Poll();
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.ConnectCalls);
        Assert.True(SameFrame(Expect("CONNECTING...", 3, OverlayArt.White), One(rig.Flow.Scene, "online-status-title").Frame));

        rig.Flow.ChooseChannel(new ChannelTarget(31001));
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(31001, rig.Backend.LastTarget.Port);

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(new[] { "login", "connect", "select" }, rig.Backend.Calls);
    }

    [Fact]
    public void A_failed_connect_shows_the_reason_and_the_retry_hint_on_the_channel_screen()
    {
        var rig = new ShellRig();
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.GameLoginRejected));
        rig.Fill();
        rig.Submit();
        rig.Pump(2);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.True(SameFrame(Expect("GAME LOGIN REJECTED", 2, OverlayArt.Orange), One(rig.Flow.Scene, "online-status-notice").Frame));
        Assert.True(SameFrame(Expect("ENTER RETRY   ESC BACK", 1, OverlayArt.White), One(rig.Flow.Scene, "online-status-hint").Frame));

        var retry = new TaskCompletionSource<OnlineConnectOutcome>();
        rig.Backend.OnConnect = (t, ct) => retry.Task;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(1);                                              // 重试在途:提示清掉,提示行换回"取消"
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.True(rig.Flow.Busy);
        Assert.DoesNotContain("online-status-notice", Tags(rig.Flow.Scene));
        Assert.True(SameFrame(Expect("ESC CANCEL", 1, OverlayArt.White), One(rig.Flow.Scene, "online-status-hint").Frame));

        retry.SetResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(1)));
        rig.Pump(1);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void A_failed_login_shows_the_category_on_the_login_screen_and_the_countdown_ticks()
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.RateLimited, 10));
        rig.Fill();
        rig.Submit();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.True(SameFrame(Expect("TOO MANY REQUESTS   RETRY IN 10 S", 1, OverlayArt.Orange), One(rig.Flow.Scene, "login-notice").Frame));
        rig.Poll(1000);
        Assert.True(SameFrame(Expect("TOO MANY REQUESTS   RETRY IN 9 S", 1, OverlayArt.Orange), One(rig.Flow.Scene, "login-notice").Frame));
        rig.Poll(9000);
        Assert.DoesNotContain("login-notice", Tags(rig.Flow.Scene));
    }

    [Fact]
    public void While_a_login_is_in_flight_the_screen_shows_the_waiting_line_and_greyed_buttons()
    {
        var rig = new ShellRig();
        var gate = new TaskCompletionSource<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();

        string[] busy = Tags(rig.Flow.Scene);
        Assert.Contains("login-busy", busy);
        Assert.Contains("login-button-login-disabled", busy);

        gate.SetResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials));
        rig.Poll();
        string[] after = Tags(rig.Flow.Scene);
        Assert.DoesNotContain("login-busy", after);
        Assert.Contains("login-notice", after);
        Assert.Contains("login-button-login", after);
    }

    [Fact]
    public void A_select_failure_and_an_empty_account_show_their_notice_on_top_of_the_character_screen()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect(2);
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true));
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.True(SameFrame(Expect("CANNOT ENTER WITH THIS CHARACTER", 2, OverlayArt.Orange), One(rig.Flow.Scene, "online-select-notice").Frame));
        Assert.Equal(2, rig.Flow.Scene.Build(0).Count(s => s.AnimationPath == "select-char-name"));

        var empty = new ShellRig();
        empty.ReachCharacterSelect(0);
        Assert.Contains("select-char-empty", Tags(empty.Flow.Scene));
        Assert.True(SameFrame(Expect("NO CHARACTER", 2, OverlayArt.Yellow), One(empty.Flow.Scene, "online-select-notice").Frame));
    }

    [Fact]
    public void The_loading_screen_is_shown_while_the_character_is_being_entered()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        var gate = new TaskCompletionSource<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => gate.Task;

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();

        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        Assert.True(SameFrame(Expect("ENTERING GAME...", 3, OverlayArt.White), One(rig.Flow.Scene, "online-status-title").Frame));
        Assert.True(SameFrame(Expect("ESC CANCEL", 1, OverlayArt.White), One(rig.Flow.Scene, "online-status-hint").Frame));
    }

    [Fact]
    public void A_disconnect_in_the_game_puts_the_login_screen_back_with_the_prompt_and_an_empty_password()
    {
        var rig = new ShellRig();
        rig.Fill("player_01", "synthetic-pw");
        rig.Submit();
        rig.Pump(2);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);

        rig.Backend.RaiseDisconnected();
        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Contains("login-title", Tags(rig.Flow.Scene));
        Assert.True(SameFrame(Expect("DISCONNECTED FROM SERVER", 1, OverlayArt.Orange), One(rig.Flow.Scene, "login-notice").Frame));
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
        Assert.Equal("player_01", rig.Flow.Login.Username);
    }

    [Fact]
    public void The_whole_flow_runs_in_chinese_when_fonts_are_installed_and_the_account_reaches_the_screen_unchanged()
    {
        var fonts = new RecordingFonts();
        var rig = new ShellRig(o => o.Fonts = fonts);

        rig.Flow.Scene.Build(0);
        rig.Fill("Player_01", "synthetic-pw");
        rig.Submit();
        rig.Flow.Scene.Build(0);                                   // 连接频道的状态画面
        rig.Pump(2);
        rig.Flow.Scene.Build(0);                                   // 选角占位界面

        Assert.True(fonts.Asked("登录"));
        Assert.True(fonts.Asked("正在连接频道"));
        Assert.Equal("Player_01", rig.Backend.LastUser);          // 发给后端的是原样的账号(显示用的转换不动它)
    }

    [Fact]
    public void Going_back_to_login_releases_the_session_exactly_once_each_time()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        rig.Flow.Login.Prefill("player_01", "pw-two");
        rig.Submit();
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.Equal(new[] { "login", "disconnect", "login", "disconnect" }, rig.Backend.Calls);
    }
}
