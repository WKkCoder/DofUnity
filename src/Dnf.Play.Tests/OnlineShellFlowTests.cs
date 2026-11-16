using System.Text.RegularExpressions;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳阶段机的真值表:每个阶段 × 每种事件(提交、成功、各类失败、取消、超时、断线、重复点击、后端异常、Esc)落到哪个阶段、给什么提示、调了后端几次、日志写什么。
/// 全部用假后端与合成时间,没有任何真实地址、账号或网络;账号口令都是编的。
/// </summary>
public class OnlineShellFlowTests
{
    private static HitRect Rect(LoginButton b) => LoginScreen.ButtonRect(b);

    private static void ClickCenter(OnlineShellFlow flow, HitRect r) => flow.Click(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static TaskCompletionSource<T> Gate<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CharacterSelectScene ArtScene()
    {
        var sprites = new ShellFakeSprites();
        sprites.AddBackground();
        return new CharacterSelectScene(sprites);
    }

    /// <summary>在另一个线程上跑一段动作并等它跑完(模拟后端线程上完成任务、触发断线事件)。</summary>
    private static void OnOtherThread(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    // ------------------------------------------------------------------------------------------------ 起点与登录

    [Fact]
    public void The_shell_starts_at_login_showing_the_login_screen_and_logs_it()
    {
        var rig = new ShellRig();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.StageSerial);
        Assert.Same(rig.Flow.Login, rig.Flow.Scene);
        Assert.Null(rig.Flow.Notice);
        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.QuitRequested);
        Assert.Null(rig.Flow.Characters);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
        Assert.Equal(new[] { "DNF_ONLINE stage=login event=enter reason=start" }, rig.Lines);
        Assert.Empty(rig.Backend.Calls);
        Assert.True(rig.Backend.HasDisconnectedSubscribers);
    }

    [Fact]
    public void An_initial_username_is_prefilled_with_the_password_focused()
    {
        var rig = new ShellRig(o => o.InitialUsername = "player_01");

        Assert.Equal("player_01", rig.Flow.Login.Username);
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
        Assert.Equal(LoginField.Password, rig.Flow.Login.Focus);
    }

    [Fact]
    public void Submitting_sends_the_typed_credentials_once_and_marks_the_shell_busy()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill("player_01", "pw-one");

        rig.Submit();

        Assert.Equal(1, rig.Backend.LoginCalls);
        Assert.Equal("player_01", rig.Backend.LastUser);
        Assert.Equal("pw-one", rig.Backend.LastPassword);
        Assert.True(rig.Flow.Busy);
        Assert.True(rig.Flow.Login.Busy);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Null(rig.Flow.Notice);
    }

    [Fact]
    public void Repeated_enter_and_clicks_while_a_login_is_in_flight_send_nothing_more()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();

        for (int i = 0; i < 5; i++)
        {
            rig.Flow.Key(OnlineShellKey.Enter);
            ClickCenter(rig.Flow, Rect(LoginButton.Login));
            ClickCenter(rig.Flow, Rect(LoginButton.Register));
            rig.Poll(10);
        }

        Assert.Equal(1, rig.Backend.LoginCalls);
        Assert.Equal(0, rig.Backend.RegisterCalls);
        Assert.Equal(new[] { "login" }, rig.Backend.Calls);
    }

    [Fact]
    public void A_double_submit_in_one_frame_sends_one_request()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Flow.Key(OnlineShellKey.Enter);
        ClickCenter(rig.Flow, Rect(LoginButton.Login));
        rig.Poll();

        Assert.Equal(1, rig.Backend.LoginCalls);
    }

    [Fact]
    public void The_flow_itself_refuses_a_second_request_even_if_the_screens_busy_flag_was_cleared_from_outside()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        Assert.True(rig.Flow.Busy);

        rig.Flow.Login.SetBusy(false);                      // 界面的"忙"标志被外面改掉了(登录界面对象是公开的):这时界面自己不再挡回车
        rig.Submit();

        Assert.Equal(1, rig.Backend.LoginCalls);            // 阶段机自己还有一道:有请求在途就不发第二个
        Assert.True(rig.Flow.Busy);
        gate.SetResult(OnlineAuthOutcome.Ok());
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.LoginCalls);
    }

    [Fact]
    public void A_successful_login_clears_the_password_and_moves_on_to_the_server_stage()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill("player_01", "pw-one");

        rig.Submit();

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(2, rig.Flow.StageSerial);
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
        Assert.Equal("player_01", rig.Flow.Login.Username);
        Assert.False(rig.Flow.Login.Busy);
        Assert.Null(rig.Flow.Notice);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=login event=enter reason=start",
            "DNF_ONLINE stage=login event=ok reason=auth",
            "DNF_ONLINE stage=server event=enter reason=login_ok",
        }, rig.Lines);
    }

    [Theory]
    [InlineData(OnlineNoticeKind.WrongCredentials, "wrong_credentials")]
    [InlineData(OnlineNoticeKind.AccountBanned, "banned")]
    [InlineData(OnlineNoticeKind.NetworkError, "network")]
    [InlineData(OnlineNoticeKind.ProtocolError, "protocol")]
    [InlineData(OnlineNoticeKind.NoGameServer, "no_game_server")]
    [InlineData(OnlineNoticeKind.Rejected, "rejected")]
    [InlineData(OnlineNoticeKind.Unexpected, "unexpected")]
    public void Each_login_failure_stays_at_login_with_its_own_notice_and_releases_the_session(OnlineNoticeKind kind, string code)
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(kind));
        rig.Fill("player_01", "pw-one");

        rig.Submit();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.StageSerial);
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(kind, rig.Flow.Notice.Kind);
        Assert.Equal(kind, rig.Flow.Login.Notice.Kind);
        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.Login.Busy);
        Assert.Equal("pw-one", rig.Flow.Login.Password);          // 失败不清密码,玩家改了再试
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=login event=fail reason=" + code, rig.Lines[^1]);
        Assert.Equal(0, rig.Backend.ConnectCalls);
    }

    [Fact]
    public void A_failed_login_can_be_retried_by_the_player_but_never_retries_by_itself()
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials));
        rig.Fill();
        rig.Submit();
        rig.Pump(20);
        Assert.Equal(1, rig.Backend.LoginCalls);

        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Ok());
        rig.Submit();
        Assert.Equal(2, rig.Backend.LoginCalls);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
    }

    [Fact]
    public void A_new_submit_clears_the_previous_error_while_the_request_is_in_flight()
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials));
        rig.Fill();
        rig.Submit();
        Assert.NotNull(rig.Flow.Notice);

        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Submit();

        Assert.Null(rig.Flow.Notice);
        Assert.True(rig.Flow.Busy);
    }

    // ------------------------------------------------------------------------------------------------ 限流

    [Fact]
    public void A_rate_limited_login_shows_a_countdown_blocks_submits_until_it_ends_and_then_clears()
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.RateLimited, 7));
        rig.Fill();
        rig.Submit();

        Assert.Equal(OnlineNoticeKind.RateLimited, rig.Flow.Notice.Kind);
        Assert.Equal(7, rig.Flow.Notice.Seconds);
        Assert.Equal("DNF_ONLINE stage=login event=fail reason=rate_limited", rig.Lines[^1]);

        // 倒计时往下走
        rig.Poll(1000);
        Assert.Equal(6, rig.Flow.Notice.Seconds);
        rig.Poll(2500);
        Assert.Equal(4, rig.Flow.Notice.Seconds);      // 剩 3.5 秒,向上取整

        // 倒计时没结束时再提交:不发请求,提示还在
        rig.Submit();
        Assert.Equal(1, rig.Backend.LoginCalls);
        Assert.Equal(OnlineNoticeKind.RateLimited, rig.Flow.Notice.Kind);

        rig.Poll(3499);                                  // 距离结束还差 1 毫秒
        Assert.Equal(1, rig.Flow.Notice.Seconds);
        rig.Submit();
        Assert.Equal(1, rig.Backend.LoginCalls);

        rig.Poll(1);                                     // 恰好到点:提示清掉
        Assert.Null(rig.Flow.Notice);
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Ok());
        rig.Submit();
        Assert.Equal(2, rig.Backend.LoginCalls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    public void A_missing_or_tiny_retry_after_still_holds_for_at_least_one_second(int given, int shown)
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.RateLimited, given));
        rig.Fill();

        rig.Submit();

        Assert.Equal(shown, rig.Flow.Notice.Seconds);
        rig.Submit();
        Assert.Equal(1, rig.Backend.LoginCalls);
    }

    [Fact]
    public void The_rate_limit_hold_also_applies_to_register_and_change_password()
    {
        var rig = new ShellRig();
        rig.Backend.OnRegister = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.RateLimited, 30));
        rig.Fill();
        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();
        Assert.Equal(1, rig.Backend.RegisterCalls);
        Assert.Equal(30, rig.Flow.Notice.Seconds);

        rig.Submit();
        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();
        ClickCenter(rig.Flow, Rect(LoginButton.OpenChangePassword));
        rig.Flow.Key(OnlineShellKey.Tab);                   // 修改密码形态:旧密码框里已有字,Tab 到新密码框输入
        rig.Flow.Type("new-pw");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();

        Assert.Equal(0, rig.Backend.LoginCalls);
        Assert.Equal(1, rig.Backend.RegisterCalls);
        Assert.Equal(0, rig.Backend.ChangePasswordCalls);
        Assert.Equal(OnlineNoticeKind.RateLimited, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void A_submit_handled_in_the_very_poll_where_the_hold_ends_goes_through_but_one_millisecond_earlier_does_not()
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.RateLimited, 5));
        rig.Fill();
        rig.Submit();                                       // t=1000,冷却到 t=6000
        Assert.Equal(5, rig.Flow.Notice.Seconds);

        rig.Poll(4999);                                     // t=5999:还差 1 毫秒
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(1, rig.Backend.LoginCalls);
        Assert.Equal(OnlineNoticeKind.RateLimited, rig.Flow.Notice.Kind);

        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Ok());
        rig.Flow.Key(OnlineShellKey.Enter);                 // 回车只记意图;下面这次轮询恰好到点,意图在清冷却之前处理
        rig.Poll(1);                                        // t=6000

        Assert.Equal(2, rig.Backend.LoginCalls);
    }

    // ------------------------------------------------------------------------------------------------ 本地输入检查与未配置(都不发请求)

    [Theory]
    [InlineData("abc")]
    [InlineData("has space")]
    [InlineData("bad-name")]
    [InlineData("名字不对哦")]
    [InlineData("")]
    public void An_invalid_username_is_rejected_locally_without_a_request(string user)
    {
        var rig = new ShellRig();
        rig.Fill(user, "pw");

        rig.Submit();

        Assert.Empty(rig.Backend.Calls);
        Assert.Equal(OnlineNoticeKind.InvalidUsername, rig.Flow.Notice.Kind);
        Assert.False(rig.Flow.Busy);
        Assert.Equal("DNF_ONLINE stage=login event=fail reason=invalid_username", rig.Lines[^1]);
    }

    [Theory]
    [InlineData("abcd")]
    [InlineData("a_b_")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    public void Usernames_at_the_length_and_charset_boundaries_are_sent(string user)
    {
        var rig = new ShellRig();
        rig.Fill(user, "pw");

        rig.Submit();

        Assert.Equal(1, rig.Backend.LoginCalls);
        Assert.Equal(user, rig.Backend.LastUser);
    }

    [Fact]
    public void An_empty_password_is_rejected_locally_without_a_request()
    {
        var rig = new ShellRig();
        rig.Flow.Type("player_01");

        rig.Submit();

        Assert.Empty(rig.Backend.Calls);
        Assert.Equal(OnlineNoticeKind.InvalidPassword, rig.Flow.Notice.Kind);
        Assert.Equal("DNF_ONLINE stage=login event=fail reason=invalid_password", rig.Lines[^1]);
    }

    [Fact]
    public void Register_and_change_password_are_checked_locally_too()
    {
        var rig = new ShellRig();
        rig.Flow.Type("abc");
        rig.Flow.Key(OnlineShellKey.Tab);
        rig.Flow.Type("pw");
        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();
        Assert.Equal(OnlineNoticeKind.InvalidUsername, rig.Flow.Notice.Kind);

        ClickCenter(rig.Flow, Rect(LoginButton.OpenChangePassword));
        rig.Poll();
        rig.Flow.Login.SetUsername("player_01");
        rig.Flow.Key(OnlineShellKey.Enter);                // 新密码是空的
        rig.Poll();
        Assert.Equal(OnlineNoticeKind.InvalidPassword, rig.Flow.Notice.Kind);

        Assert.Empty(rig.Backend.Calls);
    }

    [Fact]
    public void A_missing_connection_config_shows_not_configured_from_the_start_and_sends_nothing()
    {
        var rig = new ShellRig(o =>
        {
            o.Configured = false;
            o.ConfigDetail = "缺少 server_url";
        });

        Assert.Equal(OnlineNoticeKind.NotConfigured, rig.Flow.Notice.Kind);
        Assert.Equal("缺少 server_url", rig.Flow.Notice.Detail);
        Assert.Equal(OnlineNoticeKind.NotConfigured, rig.Flow.Login.Notice.Kind);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=login event=enter reason=start",
            "DNF_ONLINE stage=login event=fail reason=not_configured",
        }, rig.Lines);

        rig.Fill();
        rig.Submit();
        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();
        ClickCenter(rig.Flow, Rect(LoginButton.OpenChangePassword));
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(5);

        Assert.Empty(rig.Backend.Calls);
        Assert.Equal(OnlineNoticeKind.NotConfigured, rig.Flow.Notice.Kind);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
    }

    [Fact]
    public void The_not_configured_notice_wins_over_invalid_input_and_rate_limits_because_it_is_checked_first()
    {
        var rig = new ShellRig(o => o.Configured = false);
        rig.Flow.Type("x");
        rig.Submit();

        Assert.Equal(OnlineNoticeKind.NotConfigured, rig.Flow.Notice.Kind);
        Assert.Empty(rig.Backend.Calls);
    }

    // ------------------------------------------------------------------------------------------------ 注册与改密

    [Fact]
    public void A_successful_register_shows_a_hint_stays_at_login_keeps_the_typed_credentials_and_does_not_log_in()
    {
        var rig = new ShellRig();
        rig.Fill("player_01", "pw-one");

        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();

        Assert.Equal(1, rig.Backend.RegisterCalls);
        Assert.Equal("player_01", rig.Backend.LastUser);
        Assert.Equal("pw-one", rig.Backend.LastPassword);
        Assert.Equal(0, rig.Backend.LoginCalls);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.RegisterOk, rig.Flow.Notice.Kind);
        Assert.False(rig.Flow.Notice.IsError);
        Assert.Equal("pw-one", rig.Flow.Login.Password);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=login event=ok reason=registered", rig.Lines[^1]);
    }

    [Theory]
    [InlineData(OnlineNoticeKind.UserExists, "user_exists")]
    [InlineData(OnlineNoticeKind.RegistrationClosed, "registration_closed")]
    [InlineData(OnlineNoticeKind.InvalidUsername, "invalid_username")]
    [InlineData(OnlineNoticeKind.InvalidPassword, "invalid_password")]
    [InlineData(OnlineNoticeKind.NetworkError, "network")]
    public void A_failed_register_shows_its_category_and_does_not_touch_the_session(OnlineNoticeKind kind, string code)
    {
        var rig = new ShellRig();
        rig.Backend.OnRegister = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(kind));
        rig.Fill();

        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();

        Assert.Equal(kind, rig.Flow.Notice.Kind);
        Assert.Equal("DNF_ONLINE stage=login event=fail reason=" + code, rig.Lines[^1]);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        Assert.False(rig.Flow.Busy);
    }

    [Fact]
    public void Change_password_goes_through_its_own_mode_sends_old_and_new_and_clears_the_secrets_on_success()
    {
        var rig = new ShellRig();
        rig.Flow.Type("player_01");
        ClickCenter(rig.Flow, Rect(LoginButton.OpenChangePassword));
        rig.Poll();
        Assert.Equal(LoginMode.ChangePassword, rig.Flow.Login.Mode);
        Assert.Equal(LoginField.Password, rig.Flow.Login.Focus);
        rig.Flow.Type("old-pw");
        rig.Flow.Key(OnlineShellKey.Tab);
        rig.Flow.Type("new-pw");

        ClickCenter(rig.Flow, Rect(LoginButton.Confirm));
        rig.Poll();

        Assert.Equal(1, rig.Backend.ChangePasswordCalls);
        Assert.Equal("player_01", rig.Backend.LastUser);
        Assert.Equal("old-pw", rig.Backend.LastPassword);
        Assert.Equal("new-pw", rig.Backend.LastNewPassword);
        Assert.Equal(OnlineNoticeKind.PasswordChanged, rig.Flow.Notice.Kind);
        Assert.Equal(LoginMode.Login, rig.Flow.Login.Mode);
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
        Assert.Equal(string.Empty, rig.Flow.Login.NewPassword);
        Assert.Equal(0, rig.Backend.LoginCalls);
        Assert.Equal("DNF_ONLINE stage=login event=ok reason=password_changed", rig.Lines[^1]);
    }

    [Fact]
    public void A_failed_change_password_keeps_the_mode_and_the_typed_values()
    {
        var rig = new ShellRig();
        rig.Backend.OnChangePassword = (u, o, n, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials));
        rig.Flow.Type("player_01");
        ClickCenter(rig.Flow, Rect(LoginButton.OpenChangePassword));
        rig.Flow.Type("old-pw");
        rig.Flow.Key(OnlineShellKey.Tab);
        rig.Flow.Type("new-pw");
        rig.Poll();

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();

        Assert.Equal(OnlineNoticeKind.WrongCredentials, rig.Flow.Notice.Kind);
        Assert.Equal(LoginMode.ChangePassword, rig.Flow.Login.Mode);
        Assert.Equal("old-pw", rig.Flow.Login.Password);
        Assert.Equal("new-pw", rig.Flow.Login.NewPassword);
    }

    // ------------------------------------------------------------------------------------------------ 取消、超时、异常(登录阶段)

    [Fact]
    public void Escape_while_a_login_is_in_flight_cancels_it_releases_the_session_and_ignores_its_late_result()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        CancellationToken token = rig.Backend.LastToken;
        Assert.False(token.IsCancellationRequested);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.True(token.IsCancellationRequested);
        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.Login.Busy);
        Assert.Equal(OnlineNoticeKind.Cancelled, rig.Flow.Notice.Kind);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=login event=fail reason=cancelled", rig.Lines[^1]);

        gate.SetResult(OnlineAuthOutcome.Ok());                 // 迟到的成功结果
        rig.Pump(5);

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.ConnectCalls);
        Assert.Equal(1, rig.Backend.LoginCalls);

        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Ok());
        rig.Submit();                                           // 取消之后玩家可以再登录
        Assert.Equal(2, rig.Backend.LoginCalls);
    }

    [Fact]
    public void A_login_that_takes_too_long_times_out_exactly_at_the_limit_and_is_abandoned()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        CancellationToken token = rig.Backend.LastToken;

        rig.Poll(59999);
        Assert.True(rig.Flow.Busy);
        Assert.False(token.IsCancellationRequested);

        rig.Poll(1);

        Assert.False(rig.Flow.Busy);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.Equal("timeout", rig.Flow.Notice.Reason);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=login event=fail reason=timeout", rig.Lines[^1]);

        gate.SetResult(OnlineAuthOutcome.Ok());
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    [Fact]
    public void The_timeout_is_configurable()
    {
        var rig = new ShellRig(o => o.TimeoutMs = 500);
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();

        rig.Poll(499);
        Assert.True(rig.Flow.Busy);
        rig.Poll(1);
        Assert.False(rig.Flow.Busy);
    }

    [Fact]
    public void Escape_while_register_is_in_flight_cancels_it_without_touching_the_session()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnRegister = (u, p, ct) => gate.Task;
        rig.Fill();
        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();
        Assert.True(rig.Flow.Busy);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.Login.Busy);
        Assert.Equal(OnlineNoticeKind.Cancelled, rig.Flow.Notice.Kind);
        Assert.True(rig.Backend.LastToken.IsCancellationRequested);
        Assert.Equal(0, rig.Backend.DisconnectCalls);          // 注册没有会话可放
    }

    [Fact]
    public void A_register_that_times_out_does_not_touch_the_session()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnRegister = (u, p, ct) => gate.Task;
        rig.Fill();
        ClickCenter(rig.Flow, Rect(LoginButton.Register));
        rig.Poll();

        rig.Poll(60000);

        Assert.Equal("timeout", rig.Flow.Notice.Reason);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        Assert.False(rig.Flow.Login.Busy);
    }

    [Fact]
    public void Backend_exceptions_at_login_become_notices_and_never_crash_the_shell()
    {
        // 同步抛出的连接类异常 → 网络错误
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => throw new IOException("boom");
        rig.Fill();
        rig.Submit();
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.Equal("network", rig.Flow.Notice.Reason);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);

        // 任务出错(其它异常)→ 意外错误
        rig.Backend.OnLogin = (u, p, ct) => Task.FromException<OnlineAuthOutcome>(new InvalidOperationException("boom"));
        rig.Submit();
        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);

        // 后端自己的超时(任务被取消)→ 网络超时
        rig.Backend.OnLogin = (u, p, ct) => Task.FromCanceled<OnlineAuthOutcome>(new CancellationToken(true));
        rig.Submit();
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.Equal("timeout", rig.Flow.Notice.Reason);

        // 没给任务、给了空结果 → 意外错误
        rig.Backend.OnLogin = (u, p, ct) => null;
        rig.Submit();
        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);
        rig.Backend.OnLogin = (u, p, ct) => Task.FromResult<OnlineAuthOutcome>(null);
        rig.Submit();
        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);

        Assert.Equal(5, rig.Backend.LoginCalls);
        Assert.Equal(5, rig.Backend.DisconnectCalls);
    }

    [Fact]
    public void A_socket_level_error_at_login_counts_as_a_network_error()
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (u, p, ct) => Task.FromException<OnlineAuthOutcome>(new System.Net.Sockets.SocketException());
        rig.Fill();

        rig.Submit();

        Assert.Equal("network", rig.Flow.Notice.Reason);
    }

    [Fact]
    public void A_disconnect_event_while_at_login_is_ignored_because_there_is_no_connection()
    {
        var rig = new ShellRig();
        rig.Backend.RaiseDisconnected();

        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.StageSerial);
        Assert.Null(rig.Flow.Notice);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
    }

    [Fact]
    public void Escape_at_an_idle_login_does_nothing_and_in_change_password_mode_goes_back_to_login_mode()
    {
        var rig = new ShellRig();
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Empty(rig.Backend.Calls);

        ClickCenter(rig.Flow, Rect(LoginButton.OpenChangePassword));
        rig.Poll();
        Assert.Equal(LoginMode.ChangePassword, rig.Flow.Login.Mode);
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.Equal(LoginMode.Login, rig.Flow.Login.Mode);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    [Fact]
    public void Typing_and_tab_work_at_login_and_are_ignored_at_other_stages()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);

        rig.Flow.Type("zzz");
        rig.Flow.Key(OnlineShellKey.Tab);

        Assert.Equal("player_01", rig.Flow.Login.Username);
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
    }

    // ------------------------------------------------------------------------------------------------ 选服务器与选频道(占位)

    [Fact]
    public void Without_auto_pass_the_server_stage_waits_for_choose_server_then_the_channel_stage_waits_for_choose_channel()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        rig.Pump(5);
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.ConnectCalls);
        Assert.NotSame(rig.Flow.Login, rig.Flow.Scene);
        Assert.IsAssignableFrom<ISceneSource>(rig.Flow.Scene);

        rig.Flow.ChooseServer();
        rig.Poll();
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        rig.Pump(5);
        Assert.Equal(0, rig.Backend.ConnectCalls);          // 没有自动放行:等选频道的界面给目标

        rig.Flow.ChooseChannel(new ChannelTarget(31000, "host-a"));
        rig.Poll();
        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(new ChannelTarget(31000, "host-a"), rig.Backend.LastTarget);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=login event=enter reason=start",
            "DNF_ONLINE stage=login event=ok reason=auth",
            "DNF_ONLINE stage=server event=enter reason=login_ok",
            "DNF_ONLINE stage=server event=ok reason=choose",
            "DNF_ONLINE stage=channel event=enter reason=choose",
            "DNF_ONLINE stage=channel event=ok reason=connected",
            "DNF_ONLINE stage=charselect event=enter reason=connected",
        }, rig.Lines);
    }

    [Fact]
    public void Escape_at_the_server_stage_goes_back_to_login_and_releases_the_session_without_a_notice()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Null(rig.Flow.Notice);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=login event=enter reason=back", rig.Lines[^1]);
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
    }

    [Fact]
    public void Escape_at_the_channel_stage_without_auto_pass_goes_back_to_the_server_stage_keeping_the_session()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        rig.Flow.ChooseServer();
        rig.Poll();
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
    }

    [Fact]
    public void Enter_at_the_channel_stage_only_retries_after_a_failure_and_never_starts_the_first_connection()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        rig.Flow.ChooseServer();
        rig.Poll();
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);

        rig.Flow.Key(OnlineShellKey.Enter);                 // 还没有选定频道、也没有失败可重试:回车什么都不做
        rig.Pump(3);

        Assert.Equal(0, rig.Backend.ConnectCalls);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Choosing_a_channel_target_is_validated_and_the_default_port_comes_from_the_options()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelTarget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelTarget(65536));
        Assert.Throws<ArgumentException>(() => new ChannelTarget(1, string.Empty));
        Assert.Equal(1, new ChannelTarget(1).Port);
        Assert.Equal(65535, new ChannelTarget(65535).Port);
        Assert.Null(new ChannelTarget(30011).Host);
        Assert.Equal("host-a", new ChannelTarget(30011, "host-a").Host);
        Assert.Equal("ChannelTarget", new ChannelTarget(30011, "host-a").ToString());       // 端口与主机不进文字输出
        Assert.True(new ChannelTarget(30011, "host-a").Equals(new ChannelTarget(30011, "host-a")));
        Assert.False(new ChannelTarget(30011, "host-a").Equals(new ChannelTarget(30011, "host-b")));
        Assert.False(new ChannelTarget(30011, "host-a").Equals(new ChannelTarget(30012, "host-a")));
        Assert.False(new ChannelTarget(30011).Equals(new ChannelTarget(30011, "host-a")));
        Assert.False(new ChannelTarget(30011).Equals(null));
        Assert.Equal(new ChannelTarget(30011, "host-a").GetHashCode(), new ChannelTarget(30011, "host-a").GetHashCode());

        var rig = new ShellRig();
        Assert.Throws<ArgumentNullException>(() => rig.Flow.ChooseChannel(null));
    }

    [Fact]
    public void The_default_channel_is_connected_automatically_with_the_configured_port_and_no_host()
    {
        var rig = new ShellRig(o => o.DefaultChannelPort = 31022);
        rig.Fill();
        rig.Submit();

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(0, rig.Backend.SwitchCalls);
        Assert.Equal(31022, rig.Backend.LastTarget.Port);
        Assert.Null(rig.Backend.LastTarget.Host);
        Assert.True(rig.Flow.Busy);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=login event=enter reason=start",
            "DNF_ONLINE stage=login event=ok reason=auth",
            "DNF_ONLINE stage=server event=enter reason=login_ok",
            "DNF_ONLINE stage=server event=ok reason=auto",
            "DNF_ONLINE stage=channel event=enter reason=auto",
        }, rig.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void An_out_of_range_default_port_is_rejected_when_the_shell_is_built(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShellRig(o => o.DefaultChannelPort = port));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void The_default_port_boundaries_are_accepted(int port)
    {
        var rig = new ShellRig(o => o.DefaultChannelPort = port);
        rig.Fill();
        rig.Submit();
        Assert.Equal(port, rig.Backend.LastTarget.Port);
    }

    [Fact]
    public void Connecting_hands_the_roster_over_untouched_and_enters_character_select()
    {
        var rig = new ShellRig();
        OnlineCharacterList roster = ShellTestData.Roster(3);
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(roster));
        rig.Fill();
        rig.Submit();

        rig.Pump(2);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Same(roster, rig.Flow.Characters);
        Assert.Equal(3, rig.Flow.Characters.Entries.Count);
        Assert.Null(rig.Flow.Notice);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=channel event=ok reason=connected",
            "DNF_ONLINE stage=charselect event=enter reason=connected",
        }, rig.Lines.TakeLast(2));
    }

    [Theory]
    [InlineData(OnlineNoticeKind.ConnectFailed, "connect_failed")]
    [InlineData(OnlineNoticeKind.GameLoginRejected, "game_login_rejected")]
    [InlineData(OnlineNoticeKind.CharacterListMissing, "no_character_list")]
    [InlineData(OnlineNoticeKind.NetworkError, "network")]
    [InlineData(OnlineNoticeKind.ProtocolError, "protocol")]
    [InlineData(OnlineNoticeKind.Rejected, "rejected")]
    public void A_connect_failure_holds_at_the_channel_stage_with_its_notice_and_waits_for_an_explicit_retry(OnlineNoticeKind kind, string code)
    {
        var rig = new ShellRig();
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(kind));
        rig.Fill();
        rig.Submit();

        rig.Pump(20);                                       // 不会自动重试

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(kind, rig.Flow.Notice.Kind);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(0, rig.Backend.DisconnectCalls);       // 会话还在,可以直接重连
        Assert.Equal("DNF_ONLINE stage=channel event=fail reason=" + code, rig.Lines[^1]);

        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(1)));
        rig.Flow.Key(OnlineShellKey.Enter);                 // 玩家明确要求重试
        rig.Pump(2);

        Assert.Equal(2, rig.Backend.ConnectCalls);
        Assert.Equal(0, rig.Backend.SwitchCalls);           // 没有连接,重连用的是"连接"而不是"换频道"
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Enter_at_the_channel_stage_while_connecting_does_not_start_a_second_connect()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineConnectOutcome>();
        rig.Backend.OnConnect = (t, ct) => gate.Task;
        rig.Fill();
        rig.Submit();

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(1, rig.Backend.ConnectCalls);
    }

    [Fact]
    public void Escape_in_the_connect_failure_hold_goes_back_to_login_and_releases_the_session()
    {
        var rig = new ShellRig();
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
        rig.Fill();
        rig.Submit();
        rig.Pump(2);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Null(rig.Flow.Notice);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=login event=enter reason=back", rig.Lines[^1]);
    }

    [Fact]
    public void Escape_while_connecting_cancels_the_connect_and_holds_without_dropping_the_session()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineConnectOutcome>();
        rig.Backend.OnConnect = (t, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        CancellationToken token = rig.Backend.LastToken;

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Cancelled, rig.Flow.Notice.Kind);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=channel event=fail reason=cancelled", rig.Lines[^1]);

        gate.SetResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(1)));   // 迟到的成功结果被丢弃
        rig.Pump(5);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Null(rig.Flow.Characters);
        Assert.Equal(1, rig.Backend.ConnectCalls);
    }

    [Fact]
    public void A_connect_timeout_holds_with_a_network_notice_and_can_be_retried()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineConnectOutcome>();
        rig.Backend.OnConnect = (t, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        CancellationToken token = rig.Backend.LastToken;

        rig.Poll(59999);
        Assert.True(rig.Flow.Busy);
        rig.Poll(1);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.Equal("DNF_ONLINE stage=channel event=fail reason=timeout", rig.Lines[^1]);
        Assert.Equal(0, rig.Backend.DisconnectCalls);

        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(1)));
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void An_io_error_while_connecting_is_a_lost_connection_and_goes_back_to_login_with_a_prompt()
    {
        var rig = new ShellRig();
        rig.Backend.OnConnect = (t, ct) => Task.FromException<OnlineConnectOutcome>(new IOException("lost"));
        rig.Fill();
        rig.Submit();

        rig.Pump(2);

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Null(rig.Flow.Characters);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=channel event=fail reason=disconnected",
            "DNF_ONLINE stage=login event=enter reason=disconnected",
        }, rig.Lines.TakeLast(2));
    }

    [Fact]
    public void Other_connect_exceptions_hold_with_an_unexpected_notice_and_a_null_outcome_is_unexpected()
    {
        var rig = new ShellRig();
        rig.Backend.OnConnect = (t, ct) => throw new InvalidOperationException("boom");
        rig.Fill();
        rig.Submit();
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);

        rig.Backend.OnConnect = (t, ct) => Task.FromResult<OnlineConnectOutcome>(null);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);
        Assert.Equal(2, rig.Backend.ConnectCalls);
    }

    [Fact]
    public void A_connect_outcome_reporting_disconnected_is_treated_as_a_lost_connection()
    {
        var rig = new ShellRig();
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.Disconnected));
        rig.Fill();
        rig.Submit();

        rig.Pump(2);

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void A_disconnect_event_while_connecting_goes_back_to_login_and_cancels_the_connect()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineConnectOutcome>();
        rig.Backend.OnConnect = (t, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        CancellationToken token = rig.Backend.LastToken;

        rig.Backend.RaiseDisconnected();
        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.True(token.IsCancellationRequested);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
    }

    // ------------------------------------------------------------------------------------------------ 选角

    [Fact]
    public void Character_select_draws_one_placeholder_slot_per_server_character_but_keeps_the_full_list()
    {
        var rig = new ShellRig();
        OnlineCharacterList roster = ShellTestData.Roster(15);
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(roster));
        rig.Fill();
        rig.Submit();
        rig.Pump(2);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        List<SceneSprite> sprites = rig.Flow.Scene.Build(0);
        Assert.Equal(CharacterRoster.Capacity, sprites.Count(s => s.AnimationPath == "select-char-name"));
        Assert.Equal(15, rig.Flow.Characters.Entries.Count);
        Assert.Same(roster, rig.Flow.Characters);
    }

    [Fact]
    public void The_placeholder_roster_shows_levels_and_generic_names_never_the_server_names()
    {
        CharacterRoster roster = OnlineShellRoster.Placeholder(ShellTestData.Roster(3));

        Assert.Equal(3, roster.Count);
        Assert.Equal("LV10 ROLE1", roster[0].Caption);
        Assert.Equal("LV12 ROLE3", roster[2].Caption);
        Assert.Equal("Job1", roster[1].JobName);
        Assert.Equal(0, OnlineShellRoster.Placeholder(null).Count);
        Assert.Equal(0, OnlineShellRoster.Placeholder(ShellTestData.Roster(0)).Count);
    }

    [Fact]
    public void Confirming_the_selected_character_goes_through_loading_into_the_game_and_hands_control_over()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect(3);

        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();

        Assert.Equal(1, rig.Backend.SelectCalls);
        Assert.Equal(1, rig.Backend.LastSlot);
        Assert.Equal(1, rig.Flow.SelectedSlot);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Null(rig.Flow.Scene);
        Assert.Equal(1, rig.Flow.SelectedCharacter.Job);
        Assert.Equal(11, rig.Flow.SelectedCharacter.Level);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=charselect event=ok reason=confirm",
            "DNF_ONLINE stage=loading event=enter reason=select",
            "DNF_ONLINE stage=loading event=ok reason=selected",
            "DNF_ONLINE stage=ingame event=enter reason=handover",
        }, rig.Lines.TakeLast(4));
    }

    [Fact]
    public void While_loading_repeated_confirms_and_clicks_do_not_send_a_second_select()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        var gate = Gate<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => gate.Task;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        Assert.NotSame(rig.Flow.Login, rig.Flow.Scene);

        for (int i = 0; i < 5; i++)
        {
            rig.Flow.Key(OnlineShellKey.Enter);
            rig.Flow.Key(OnlineShellKey.Space);
            rig.Flow.Click(CharacterSelectArt.StartX + 5, CharacterSelectArt.StartY + 5);
            rig.Poll(10);
        }

        Assert.Equal(1, rig.Backend.SelectCalls);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
    }

    [Fact]
    public void Arrow_keys_move_the_selection_and_confirm_uses_the_moved_slot_with_space_as_a_confirm_too()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect(4);
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Left);
        rig.Flow.Key(OnlineShellKey.Down);                      // 第二行没有角色:不动
        rig.Flow.Key(OnlineShellKey.Up);
        rig.Flow.Key(OnlineShellKey.Right);

        rig.Flow.Key(OnlineShellKey.Space);
        rig.Poll();

        Assert.Equal(2, rig.Backend.LastSlot);
    }

    [Fact]
    public void A_select_failure_that_allows_another_slot_returns_to_character_select_keeping_the_slot_with_a_notice()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect(3);
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true));
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Enter);

        rig.Poll();

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.SelectFailed, rig.Flow.Notice.Kind);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        Assert.NotNull(rig.Flow.Characters);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=loading event=fail reason=select_failed",
            "DNF_ONLINE stage=charselect event=enter reason=select_failed",
        }, rig.Lines.TakeLast(2));

        // 选择停在原来的槽位上,玩家可以换一个再试
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Ok());
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(2, rig.Backend.LastSlot);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(2, rig.Backend.SelectCalls);
    }

    [Fact]
    public void A_select_failure_that_cannot_try_another_slot_means_the_link_is_dead_and_goes_back_to_login()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, false));
        rig.Flow.Key(OnlineShellKey.Enter);

        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Null(rig.Flow.Characters);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
    }

    [Fact]
    public void A_select_outcome_reporting_disconnected_goes_back_to_login_even_if_another_slot_is_allowed()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.Disconnected, true));
        rig.Flow.Key(OnlineShellKey.Enter);

        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    [Fact]
    public void An_empty_account_shows_a_hint_and_confirm_does_nothing()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect(0);

        Assert.Equal(OnlineNoticeKind.NoCharacters, rig.Flow.Notice.Kind);
        Assert.False(rig.Flow.Notice.IsError);
        Assert.Empty(rig.Flow.Characters.Entries);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Flow.Key(OnlineShellKey.Space);
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);
    }

    [Fact]
    public void Escape_at_character_select_goes_back_to_login_dropping_the_session_and_the_roster()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Null(rig.Flow.Notice);
        Assert.Null(rig.Flow.Characters);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal("DNF_ONLINE stage=login event=enter reason=back", rig.Lines[^1]);
    }

    [Fact]
    public void Escape_at_character_select_without_auto_pass_goes_back_to_the_channel_stage()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        rig.Flow.ChooseServer();
        rig.Poll();
        rig.Flow.ChooseChannel(new ChannelTarget(30011));
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
    }

    [Fact]
    public void The_exit_button_on_character_select_requests_quit_and_nothing_else()
    {
        var rig = new ShellRig(o => o.Art = ArtScene());
        rig.ReachCharacterSelect();

        HitRect quit = CharacterSelectArt.ButtonRect(CharacterSelectButton.Quit);
        rig.Flow.Click(quit.X + 2, quit.Y + 2);
        rig.Poll();

        Assert.True(rig.Flow.QuitRequested);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
    }

    [Fact]
    public void The_channel_button_on_character_select_goes_to_the_channel_stage_and_switches_channel_with_the_default_target()
    {
        var rig = new ShellRig(o =>
        {
            o.Art = ArtScene();
            o.DefaultChannelPort = 31022;
        });
        rig.ReachCharacterSelect();
        Assert.Equal(1, rig.Backend.ConnectCalls);

        HitRect channel = CharacterSelectArt.ButtonRect(CharacterSelectButton.Channel);
        rig.Flow.Click(channel.X + 2, channel.Y + 2);
        rig.Poll();

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.SwitchCalls);
        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(31022, rig.Backend.LastTarget.Port);
        Assert.Equal("DNF_ONLINE stage=charselect event=ok reason=channel", rig.Lines.First(l => l.Contains("reason=channel") && l.Contains("event=ok")));

        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Without_art_the_channel_button_area_is_not_a_button()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();

        HitRect channel = CharacterSelectArt.ButtonRect(CharacterSelectButton.Channel);
        rig.Flow.Click(channel.X + 2, channel.Y + 2);
        rig.Poll();

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SwitchCalls);
    }

    [Fact]
    public void Clicking_the_start_button_confirms_the_selected_character()
    {
        var rig = new ShellRig(o => o.Art = ArtScene());
        rig.ReachCharacterSelect();

        rig.Flow.Click(CharacterSelectArt.StartX + 5, CharacterSelectArt.StartY + 5);
        rig.Poll();

        Assert.Equal(1, rig.Backend.SelectCalls);
        Assert.Equal(0, rig.Backend.LastSlot);
    }

    [Fact]
    public void Clicking_a_character_slot_selects_it()
    {
        var rig = new ShellRig(o => o.Art = ArtScene());
        rig.ReachCharacterSelect(3);
        CharacterSelectArt.SlotOrigin(2, out int x, out int y);

        rig.Flow.Click(x + 10, y + 10);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();

        Assert.Equal(2, rig.Backend.LastSlot);
    }

    // ------------------------------------------------------------------------------------------------ 加载

    [Fact]
    public void Escape_while_loading_cancels_the_select_and_reconnects_through_the_channel_stage()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        var gate = Gate<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => gate.Task;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        CancellationToken token = rig.Backend.LastToken;
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(2, rig.Backend.ConnectCalls);          // 选角取消后连接不能再用:自动放行重新连接
        Assert.Equal(0, rig.Backend.SwitchCalls);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=loading event=fail reason=cancelled",
            "DNF_ONLINE stage=channel event=enter reason=cancelled",
        }, rig.Lines.TakeLast(2));

        gate.SetResult(OnlineSelectOutcome.Ok());           // 迟到的成功:不能把外壳带进游戏
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);     // 重连的结果才算数
        Assert.Equal(1, rig.Backend.SelectCalls);
    }

    [Fact]
    public void A_late_select_success_after_cancel_never_enters_the_game()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        rig.Flow.ChooseServer();
        rig.Poll();
        rig.Flow.ChooseChannel(new ChannelTarget(30011));
        rig.Pump(3);
        var gate = Gate<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => gate.Task;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        gate.SetResult(OnlineSelectOutcome.Ok());
        rig.Pump(5);

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
    }

    [Fact]
    public void A_select_that_takes_too_long_times_out_and_goes_back_to_login()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        var gate = Gate<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => gate.Task;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        CancellationToken token = rig.Backend.LastToken;

        rig.Poll(59999);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        rig.Poll(1);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.Equal("timeout", rig.Flow.Notice.Reason);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=loading event=fail reason=timeout",
            "DNF_ONLINE stage=login event=enter reason=timeout",
        }, rig.Lines.TakeLast(2));
    }

    [Fact]
    public void An_io_error_while_selecting_goes_back_to_login_with_a_disconnect_prompt()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        rig.Backend.OnSelect = (s, ct) => Task.FromException<OnlineSelectOutcome>(new IOException("lost"));
        rig.Flow.Key(OnlineShellKey.Enter);

        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=loading event=fail reason=disconnected",
            "DNF_ONLINE stage=login event=enter reason=disconnected",
        }, rig.Lines.TakeLast(2));
    }

    [Fact]
    public void Other_select_exceptions_and_null_outcomes_go_back_to_login_with_an_unexpected_notice()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        rig.Backend.OnSelect = (s, ct) => throw new InvalidOperationException("boom");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);

        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(1)));
        rig.Flow.Login.Prefill("player_01", "pw-two");      // 回到登录之后密码已清,重新输入
        rig.Submit();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        rig.Backend.OnSelect = (s, ct) => Task.FromResult<OnlineSelectOutcome>(null);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Unexpected, rig.Flow.Notice.Kind);
    }

    // ------------------------------------------------------------------------------------------------ 进入游戏之后

    [Fact]
    public void A_disconnect_after_entering_the_game_leaves_the_game_state_and_returns_to_login_with_a_prompt()
    {
        var rig = new ShellRig();
        rig.ReachInGame();
        int serial = rig.Flow.StageSerial;

        rig.Backend.RaiseDisconnected();
        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(serial + 1, rig.Flow.StageSerial);
        Assert.Same(rig.Flow.Login, rig.Flow.Scene);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.Null(rig.Flow.Characters);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
        Assert.Null(rig.Flow.SelectedCharacter);
        Assert.Equal(new[]
        {
            "DNF_ONLINE stage=ingame event=fail reason=disconnected",
            "DNF_ONLINE stage=login event=enter reason=disconnected",
        }, rig.Lines.TakeLast(2));
    }

    [Fact]
    public void After_a_disconnect_the_player_can_log_in_again_with_a_fresh_session()
    {
        var rig = new ShellRig();
        rig.ReachInGame();
        rig.Backend.RaiseDisconnected();
        rig.Poll();
        Assert.Equal(string.Empty, rig.Flow.Login.Password);         // 掉线回登录:口令不留

        rig.Flow.Login.Prefill("player_01", "pw-two");
        rig.Submit();
        rig.Pump(3);

        Assert.Equal(2, rig.Backend.LoginCalls);
        Assert.Equal("pw-two", rig.Backend.LastPassword);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Going_back_to_login_always_clears_the_password_boxes_even_when_something_was_written_into_them_meanwhile(bool viaDisconnect)
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        // 离开登录之后外壳不再把键盘交给输入框,但登录界面对象是公开的(宿主为本机开发预填时直接写它):回到登录时一律清掉
        rig.Flow.Login.Prefill("player_01", "left-behind");
        Assert.Equal("left-behind", rig.Flow.Login.Password);

        if (viaDisconnect)
        {
            rig.Backend.RaiseDisconnected();
        }
        else
        {
            rig.Flow.Key(OnlineShellKey.Escape);
        }
        rig.Poll();

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
        Assert.Equal(string.Empty, rig.Flow.Login.NewPassword);
        Assert.Equal("player_01", rig.Flow.Login.Username);     // 账号可以留着
    }

    [Fact]
    public void Keys_clicks_and_typing_are_ignored_once_the_game_has_started()
    {
        var rig = new ShellRig();
        rig.ReachInGame();
        int serial = rig.Flow.StageSerial;
        int calls = rig.Backend.Calls.Count;

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Flow.Key(OnlineShellKey.Tab);
        rig.Flow.Click(400, 300);
        rig.Flow.Type("abc");
        rig.Pump(5);

        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(serial, rig.Flow.StageSerial);
        Assert.Equal(calls, rig.Backend.Calls.Count);
    }

    [Fact]
    public void Swapping_the_character_goes_back_through_the_channel_stage_with_a_channel_switch_and_a_fresh_roster()
    {
        var rig = new ShellRig();
        rig.ReachInGame();
        OnlineCharacterList second = ShellTestData.Roster(5);
        rig.Backend.OnSwitch = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(second));

        rig.Flow.SwapCharacter();
        rig.Poll();

        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.SwitchCalls);
        Assert.Equal(1, rig.Backend.ConnectCalls);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Same(second, rig.Flow.Characters);
        Assert.Contains("DNF_ONLINE stage=ingame event=ok reason=swap_character", rig.Lines);
    }

    [Fact]
    public void Swap_character_before_the_game_starts_does_nothing()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();

        rig.Flow.SwapCharacter();
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SwitchCalls);
    }

    // ------------------------------------------------------------------------------------------------ 线程、关闭、日志

    [Fact]
    public void A_result_completed_on_another_thread_changes_nothing_until_the_main_thread_polls()
    {
        var rig = new ShellRig(o => o.AutoPassPlaceholders = false);
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        int serial = rig.Flow.StageSerial;
        int lines = rig.Lines.Count;

        OnOtherThread(() => gate.SetResult(OnlineAuthOutcome.Ok()));

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(serial, rig.Flow.StageSerial);
        Assert.Equal(lines, rig.Lines.Count);
        Assert.True(rig.Flow.Busy);
        Assert.True(rig.Flow.Login.Busy);

        rig.Poll();

        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
    }

    [Fact]
    public void A_disconnect_raised_on_another_thread_only_takes_effect_at_the_next_poll()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();

        OnOtherThread(() => rig.Backend.RaiseDisconnected());

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Null(rig.Flow.Notice);
        rig.Poll();
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void Every_log_line_is_written_on_the_polling_thread()
    {
        var threads = new HashSet<int>();
        var rig = new ShellRig(o => o.Log = line => threads.Add(Environment.CurrentManagedThreadId));
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();

        OnOtherThread(() => gate.SetResult(OnlineAuthOutcome.Ok()));
        rig.Pump(3);
        OnOtherThread(() => rig.Backend.RaiseDisconnected());
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(new[] { Environment.CurrentManagedThreadId }, threads.ToArray());
    }

    [Fact]
    public void Shutdown_releases_the_session_cancels_the_request_and_stops_reacting()
    {
        var rig = new ShellRig();
        var gate = Gate<OnlineAuthOutcome>();
        rig.Backend.OnLogin = (u, p, ct) => gate.Task;
        rig.Fill();
        rig.Submit();
        CancellationToken token = rig.Backend.LastToken;
        int lines = rig.Lines.Count;

        rig.Flow.Shutdown();
        rig.Flow.Shutdown();

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(1, rig.Backend.DisconnectCalls);
        Assert.False(rig.Backend.HasDisconnectedSubscribers);
        gate.SetResult(OnlineAuthOutcome.Ok());
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Flow.Click(10, 10);
        rig.Flow.Type("x");
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(lines, rig.Lines.Count);
        Assert.Equal(1, rig.Backend.LoginCalls);
    }

    [Fact]
    public void Log_lines_have_a_fixed_shape_and_never_contain_credentials_addresses_or_names()
    {
        const string user = "secretuser77";
        const string password = "secretpass99";
        const string host = "secret-host";
        var shape = new Regex(@"^DNF_ONLINE stage=(login|server|channel|charselect|loading|ingame) event=(enter|ok|fail) reason=[a-z_]+$");

        // 走一遍:自动放行(默认频道)、进游戏、掉线、再登录失败、取消
        var auto = new ShellRig(o => o.DefaultChannelPort = 31999);
        auto.Fill(user, password);
        auto.Submit();
        auto.Pump(3);
        auto.Flow.Key(OnlineShellKey.Enter);
        auto.Pump(3);
        auto.Backend.RaiseDisconnected();
        auto.Poll();
        auto.Backend.OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials));
        auto.Flow.Login.Prefill(user, password);
        auto.Submit();

        // 再走一遍:手动选服务器与频道(目标里有端口与主机)
        var manual = new ShellRig(o => o.AutoPassPlaceholders = false);
        manual.Fill(user, password);
        manual.Submit();
        manual.Flow.ChooseServer();
        manual.Poll();
        manual.Flow.ChooseChannel(new ChannelTarget(31998, host));
        manual.Pump(3);
        manual.Flow.Key(OnlineShellKey.Enter);
        manual.Pump(3);
        manual.Flow.SwapCharacter();
        manual.Pump(3);

        Assert.True(auto.Lines.Count > 8);
        Assert.True(manual.Lines.Count > 8);
        foreach (string line in auto.Lines.Concat(manual.Lines))
        {
            Assert.Matches(shape, line);
            Assert.DoesNotContain(user, line);
            Assert.DoesNotContain(password, line);
            Assert.DoesNotContain(host, line);
            Assert.DoesNotContain("31999", line);
            Assert.DoesNotContain("31998", line);
            Assert.DoesNotContain("Role", line);
        }
    }

    [Fact]
    public void A_log_sink_that_throws_cannot_break_the_flow()
    {
        var rig = new ShellRig(o => o.Log = line => throw new InvalidOperationException("sink broke"));
        rig.Fill();

        rig.Submit();
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Stage_serials_count_every_stage_change_and_scenes_belong_to_their_stage()
    {
        var rig = new ShellRig();
        var serials = new List<int> { rig.Flow.StageSerial };
        ISceneSource login = rig.Flow.Scene;

        rig.Fill();
        rig.Submit();
        serials.Add(rig.Flow.StageSerial);                      // 一次轮询里经过登录 → 选服务器 → 选频道,序号加了 2
        ISceneSource channel = rig.Flow.Scene;
        rig.Pump(2);
        serials.Add(rig.Flow.StageSerial);
        ISceneSource select = rig.Flow.Scene;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        serials.Add(rig.Flow.StageSerial);                      // 加载 → 进入游戏(同一次轮询里完成)

        Assert.Equal(new[] { 1, 3, 4, 6 }, serials);
        Assert.Same(rig.Flow.Login, login);
        Assert.NotSame(login, channel);
        Assert.NotSame(channel, select);
        Assert.NotSame(login, select);
        Assert.Null(rig.Flow.Scene);
    }

    [Fact]
    public void Constructor_guards_reject_a_missing_backend_and_a_non_positive_timeout()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineShellFlow(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShellRig(o => o.TimeoutMs = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShellRig(o => o.TimeoutMs = -1));
    }
}
