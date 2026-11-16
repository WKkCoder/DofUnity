using System.Text.RegularExpressions;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Play.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// 宿主一侧:一帧输入怎么交给阶段机(顺序与映射),以及"真后端 + 阶段机 + 宿主"接在一起之后的整条路径
/// (登录 → 连频道 → 选角 → 进入游戏、没有配置、断线、换角色、退出)。会话用假的,网关用内存里的假网关:不连任何网络。
/// </summary>
public sealed class OnlineShellHostTests
{
    private sealed class HostRig : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<string> _lines = new();

        public HostRig(bool configured = true, string prefillUser = BackendRig.User, string prefillPassword = null, Action<FakeSessionPort> onPort = null)
        {
            Rig = new BackendRig(configured, onPort);
            var setup = new OnlineShellSetup(
                Rig.Template,
                configured ? OnlineShellConfigSource.ConfigFile : OnlineShellConfigSource.None,
                configured ? null : "synthetic config problem",
                31000,
                prefillUser,
                prefillPassword);
            Host = new OnlineShellHost(
                setup,
                Rig.Backend,
                null,
                line =>
                {
                    lock (_sync)
                    {
                        _lines.Add(line);
                    }
                },
                null);
        }

        public BackendRig Rig { get; }

        public OnlineShellHost Host { get; }

        public long Now = 1000;

        public void Tick(OnlineShellFrameInput input = default)
        {
            Host.Tick(input, 16, Now += 16);
        }

        public void Until(Func<bool> done)
        {
            HostPump.Until(Host, ref Now, done);
        }

        public void Type(string text)
        {
            Tick(new OnlineShellFrameInput { Text = text });
        }

        public void Press(bool enter = false, bool tab = false)
        {
            Tick(new OnlineShellFrameInput { Enter = enter, Tab = tab });
        }

        public string[] Lines()
        {
            lock (_sync)
            {
                return _lines.ToArray();
            }
        }

        public void ReachCharacterSelect()
        {
            // 有预填账号时焦点已经在密码框上;没有预填时先 Tab 过去
            if (Host.Flow.Login.Focus == LoginField.Username)
            {
                Press(tab: true);
            }
            Type(BackendRig.Password);
            Press(enter: true);
            Until(() => Host.Stage == OnlineShellStage.CharacterSelect);
        }

        public void ReachInGame()
        {
            ReachCharacterSelect();
            Press(enter: true);
            Until(() => Host.InGame);
        }

        public void Dispose()
        {
            Host.Dispose();
        }
    }

    // ---- 一帧输入 → 阶段机 ----

    [Fact]
    public void Text_is_applied_before_keys_so_typing_then_Enter_in_one_frame_submits_what_was_typed()
    {
        var rig = new ShellRig();
        rig.Flow.Type("player_01");
        rig.Flow.Key(OnlineShellKey.Tab);

        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Text = "synthetic-pw", Enter = true });
        rig.Poll();

        Assert.Equal(1, rig.Backend.LoginCalls);
        Assert.Equal("synthetic-pw", rig.Backend.LastPassword);
    }

    [Fact]
    public void Text_and_Tab_in_the_same_frame_go_to_the_field_that_had_focus_before_the_Tab()
    {
        var rig = new ShellRig();

        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Text = "abcd", Tab = true });

        Assert.Equal("abcd", rig.Flow.Login.Username);
        Assert.Equal(string.Empty, rig.Flow.Login.Password);
        Assert.Equal(LoginField.Password, rig.Flow.Login.Focus);
    }

    [Fact]
    public void Shift_makes_the_Tab_go_backwards()
    {
        var rig = new ShellRig();
        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Tab = true });
        Assert.Equal(LoginField.Password, rig.Flow.Login.Focus);

        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Tab = true, Shift = true });

        Assert.Equal(LoginField.Username, rig.Flow.Login.Focus);
    }

    [Fact]
    public void Shift_without_Tab_does_nothing()
    {
        var rig = new ShellRig();

        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Shift = true });

        Assert.Equal(LoginField.Username, rig.Flow.Login.Focus);
    }

    [Fact]
    public void An_empty_frame_changes_nothing()
    {
        var rig = new ShellRig();
        rig.Fill();

        OnlineShellInput.Apply(rig.Flow, default);
        rig.Pump();

        Assert.Equal(0, rig.Backend.LoginCalls);
        Assert.Equal("player_01", rig.Flow.Login.Username);
        Assert.Equal("synthetic-pw", rig.Flow.Login.Password);
    }

    [Fact]
    public void A_click_on_the_login_button_submits_and_a_click_on_a_field_moves_the_focus()
    {
        var rig = new ShellRig();
        rig.Fill();
        HitRect usernameField = LoginScreen.FieldRect(LoginField.Username);
        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Click = true, ClickX = usernameField.X + 5, ClickY = usernameField.Y + 5 });
        Assert.Equal(LoginField.Username, rig.Flow.Login.Focus);

        HitRect button = LoginScreen.ButtonRect(LoginButton.Login);
        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Click = true, ClickX = button.X + (button.Width / 2), ClickY = button.Y + (button.Height / 2) });
        rig.Poll();

        Assert.Equal(1, rig.Backend.LoginCalls);
    }

    [Fact]
    public void A_click_that_is_not_flagged_as_a_click_is_ignored_even_with_coordinates()
    {
        var rig = new ShellRig();
        rig.Fill();
        HitRect button = LoginScreen.ButtonRect(LoginButton.Login);

        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Click = false, ClickX = button.X + 3, ClickY = button.Y + 3 });
        rig.Poll();

        Assert.Equal(0, rig.Backend.LoginCalls);
    }

    [Theory]
    [InlineData("R", 1)]
    [InlineData("RR", 2)]
    [InlineData("RL", 0)]
    [InlineData("RRL", 1)]
    [InlineData("D", CharacterRoster.Columns)]
    [InlineData("RD", CharacterRoster.Columns + 1)]
    [InlineData("RDU", 1)]
    [InlineData("RDL", CharacterRoster.Columns)]
    public void Arrow_keys_move_the_character_select_cursor_and_space_confirms_the_slot_it_ended_on(string keys, int expectedSlot)
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect(CharacterRoster.Columns + 2);

        foreach (char key in keys)
        {
            OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput
            {
                Left = key == 'L',
                Right = key == 'R',
                Up = key == 'U',
                Down = key == 'D',
            });
        }
        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Space = true });
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(expectedSlot, rig.Backend.LastSlot);
    }

    [Fact]
    public void Enter_confirms_the_cursor_slot_just_like_space()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect(3);
        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Right = true });

        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Enter = true });
        rig.Pump(3);

        Assert.Equal(1, rig.Backend.LastSlot);
    }

    [Fact]
    public void Escape_from_the_character_select_goes_back_to_login()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();

        OnlineShellInput.Apply(rig.Flow, new OnlineShellFrameInput { Escape = true });
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
    }

    [Fact]
    public void Applying_input_to_no_flow_is_an_error()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineShellInput.Apply(null, default));
    }

    // ---- 整条路径:真后端 + 阶段机 + 宿主 ----

    [Fact]
    public void A_full_login_reaches_the_game_and_keeps_the_session_alive()
    {
        using var rig = new HostRig();
        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.Equal(BackendRig.User, rig.Host.Flow.Login.Username);

        rig.ReachCharacterSelect();

        FakeSessionPort port = rig.Rig.Port(0);
        Assert.Equal(1, port.LoginCalls);
        Assert.Equal(BackendRig.User, port.LastUser);
        Assert.Equal(1, port.ConnectCalls);
        Assert.Equal(31000, port.LastPort);
        Assert.Equal(2, rig.Host.Flow.Characters.Entries.Count);

        rig.Press(enter: true);
        rig.Until(() => rig.Host.InGame);

        Assert.Equal(1, port.SelectCalls);
        Assert.Equal(0, port.LastSlot);
        Assert.True(rig.Rig.Backend.HasSession);
        Assert.Equal(0, port.DisposeCount);
        Assert.Contains("DNF_NET snapshot slot=0", rig.Rig.LogsSnapshot());
    }

    [Fact]
    public void The_stage_log_follows_the_stage_diagram_and_contains_no_account_password_address_or_port()
    {
        using var rig = new HostRig();

        rig.ReachInGame();

        string[] lines = rig.Lines();
        var shape = new Regex("^DNF_ONLINE stage=(login|server|channel|charselect|loading|ingame) event=(enter|ok|fail) reason=[a-z_]*$");
        foreach (string line in lines)
        {
            Assert.Matches(shape, line);
            Assert.DoesNotContain(BackendRig.User, line);
            Assert.DoesNotContain(BackendRig.Password, line);
            Assert.DoesNotContain("localhost", line);
            Assert.DoesNotContain("31000", line);
        }
        string[] entered = lines.Where(l => l.Contains(" event=enter ")).Select(l => l.Split(' ')[1]).ToArray();
        Assert.Equal(
            new[] { "stage=login", "stage=server", "stage=channel", "stage=charselect", "stage=loading", "stage=ingame" },
            entered);
    }

    [Fact]
    public void A_prefilled_password_logs_in_without_typing()
    {
        using var rig = new HostRig(prefillPassword: BackendRig.Password);

        rig.Press(enter: true);
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(BackendRig.Password, rig.Rig.Port(0).LastPassword);
    }

    [Fact]
    public void Without_a_connection_config_the_login_says_so_and_nothing_is_sent_or_created()
    {
        using var rig = new HostRig(configured: false);

        rig.Type(BackendRig.Password);
        rig.Press(enter: true);
        for (int i = 0; i < 20; i++)
        {
            rig.Tick();
        }

        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.Equal(OnlineNoticeKind.NotConfigured, rig.Host.Flow.Notice.Kind);
        Assert.Equal("synthetic config problem", rig.Host.Flow.Notice.Detail);
        Assert.Equal(0, rig.Rig.PortCount);
        Assert.False(rig.Rig.Backend.HasSession);
        Assert.Equal(0, rig.Rig.Gateway.GetCount + rig.Rig.Gateway.PostCount);
    }

    [Fact]
    public void A_connection_loss_in_the_game_returns_to_login_with_a_notice_and_releases_the_session()
    {
        using var rig = new HostRig();
        rig.ReachInGame();
        FakeSessionPort port = rig.Rig.Port(0);

        port.RaiseLost();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.Login);

        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Host.Flow.Notice.Kind);
        Assert.False(rig.Rig.Backend.HasSession);
        Assert.True(port.WaitDisposed());
        Assert.False(rig.Host.InGame);
    }

    [Fact]
    public void After_a_loss_the_player_can_log_in_again_with_a_brand_new_session()
    {
        using var rig = new HostRig();
        rig.ReachInGame();
        rig.Rig.Port(0).RaiseLost();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.Login);

        rig.Host.Flow.Login.Prefill(BackendRig.User, BackendRig.Password);
        rig.Press(enter: true);
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(2, rig.Rig.PortCount);
        Assert.Equal(1, rig.Rig.Port(1).LoginCalls);
        Assert.Equal(0, rig.Rig.Port(1).DisposeCount);
    }

    [Fact]
    public void Swapping_the_character_reconnects_on_the_same_session_and_lists_the_characters_again()
    {
        using var rig = new HostRig();
        rig.ReachInGame();
        FakeSessionPort port = rig.Rig.Port(0);

        rig.Host.SwapCharacter();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        Assert.Equal(1, rig.Rig.PortCount);
        Assert.Equal(1, port.SwitchCalls);
        Assert.Equal(1, port.ConnectCalls);
        Assert.Equal(1, port.LoginCalls);
        Assert.Equal(0, port.DisposeCount);
        Assert.Equal(31000, port.LastPort);
    }

    [Fact]
    public void Disposing_the_host_releases_the_session_and_later_ticks_do_nothing()
    {
        var rig = new HostRig();
        rig.ReachInGame();
        FakeSessionPort port = rig.Rig.Port(0);

        rig.Host.Dispose();
        rig.Host.Dispose();
        rig.Tick(new OnlineShellFrameInput { Escape = true });
        rig.Tick();

        Assert.Equal(1, port.DisposeCount);
        Assert.False(rig.Rig.Backend.HasSession);
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
    }

    [Fact]
    public void Registering_through_the_host_creates_the_account_and_stays_on_the_login_screen()
    {
        using var rig = new HostRig(prefillUser: "fresh_player");
        rig.Type("brand-new-secret");
        HitRect button = LoginScreen.ButtonRect(LoginButton.Register);

        rig.Tick(new OnlineShellFrameInput { Click = true, ClickX = button.X + 4, ClickY = button.Y + 4 });
        rig.Until(() => rig.Host.Flow.Notice != null && rig.Host.Flow.Notice.Kind == OnlineNoticeKind.RegisterOk);

        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.True(rig.Rig.Gateway.Accounts.ContainsKey("fresh_player"));
        Assert.Equal(0, rig.Rig.PortCount);
        Assert.False(rig.Host.Flow.Busy);
    }

    [Fact]
    public void A_wrong_password_at_the_gateway_stays_on_login_and_the_session_is_released()
    {
        using var rig = new HostRig(onPort: p =>
            p.OnLogin = (user, password, ct) => Task.FromResult(
                new OnlineLoginReport(OnlineFailure.GatewayLoginFailed, GatewayOutcome.ServerError, GatewayErrorCode.WrongCredentials, 0)));

        rig.Type("not-the-password");
        rig.Press(enter: true);
        rig.Until(() => rig.Host.Flow.Notice != null && rig.Host.Flow.Notice.Kind == OnlineNoticeKind.WrongCredentials);

        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.Equal(0, rig.Rig.Port(0).ConnectCalls);
        Assert.True(rig.Rig.Port(0).WaitDisposed());
        Assert.False(rig.Host.Flow.Busy);
    }

    // ---- 构造 ----

    [Fact]
    public void Create_builds_a_real_host_from_a_setup_and_can_be_disposed_without_any_network()
    {
        var rig = new BackendRig();
        var setup = new OnlineShellSetup(rig.Template, OnlineShellConfigSource.ConfigFile, null, 31000, "player_01", null);

        using (OnlineShellHost host = OnlineShellHost.Create(setup, null, null))
        {
            Assert.Equal(OnlineShellStage.Login, host.Stage);
            Assert.False(host.InGame);
            Assert.False(host.QuitRequested);
            Assert.Null(host.Session);
            host.Tick(default, 16, 1000);
        }
    }

    [Fact]
    public void Create_without_a_setup_is_an_error()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineShellHost.Create(null, null, null));
    }
}
