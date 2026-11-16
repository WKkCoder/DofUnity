using System.Diagnostics;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 真后端的会话生命周期与翻译接线。会话用假的(只造"报告"),网关用内存里的假网关,所以不连任何网络:
/// 一次登录一个全新的会话、旧会话在后台释放、过期会话的断线事件不算数、注册与改密不留会话、频道目标带主机时明说连不上。
/// </summary>
public sealed class OnlineShellBackendTests
{
    // ---- 登录:新会话、模板拷贝、凭据不外泄 ----

    [Fact]
    public async Task Login_builds_a_fresh_session_from_the_template_and_passes_the_credentials_only_to_the_login_call()
    {
        var rig = new BackendRig();

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        Assert.True(result.Success);
        FakeSessionPort port = Assert.Single(rig.PortsSnapshot());
        Assert.Equal(1, port.LoginCalls);
        Assert.Equal(BackendRig.User, port.LastUser);
        Assert.Equal(BackendRig.Password, port.LastPassword);
        // 会话设置里没有账号与口令(它们只经登录调用传递),其余来自模板
        Assert.Equal(string.Empty, port.Settings.Account);
        Assert.Equal(string.Empty, port.Settings.Password);
        Assert.Same(rig.Options, port.Settings.Gateway);
        Assert.Equal(7u, port.Settings.DataVersion);
        Assert.Equal(31000, port.Settings.ChannelPort);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, port.Settings.MouseCipher);
        // 模板没被碰:账号口令仍为空,鼠标口令密文是另一个数组(会话改它不影响模板)
        Assert.Equal(string.Empty, rig.Template.Account);
        Assert.Equal(string.Empty, rig.Template.Password);
        Assert.NotSame(rig.Template.MouseCipher, port.Settings.MouseCipher);
        Assert.True(rig.Backend.HasSession);
    }

    [Fact]
    public async Task Login_failures_come_back_as_categories_not_exceptions()
    {
        var rig = new BackendRig(onPort: p =>
            p.OnLogin = (user, password, ct) => Task.FromResult(
                new OnlineLoginReport(OnlineFailure.GatewayLoginFailed, GatewayOutcome.RateLimited, GatewayErrorCode.None, 21)));

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.RateLimited, result.Failure);
        Assert.Equal(21, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Login_runs_on_a_pool_thread_not_the_calling_thread()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        int loginThread = -1;
        var rig = new BackendRig(onPort: p =>
            p.OnLogin = (user, password, ct) =>
            {
                loginThread = Environment.CurrentManagedThreadId;
                return Task.FromResult(new OnlineLoginReport(OnlineFailure.None, GatewayOutcome.Success, GatewayErrorCode.None, 0));
            });

        Task<OnlineAuthOutcome> task = rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default);
        await GatewayTestSupport.Bounded(task);

        Assert.NotEqual(-1, loginThread);
        Assert.NotEqual(callerThread, loginThread);
    }

    [Fact]
    public async Task A_slow_login_does_not_block_the_caller()
    {
        // 登录卡住时,LoginAsync 仍然立刻返回一个没完成的任务(外壳在渲染线程上调用它)
        using var gate = new ManualResetEventSlim(false);
        var rig = new BackendRig(onPort: p =>
            p.OnLogin = async (user, password, ct) =>
            {
                await Task.Run(() => gate.Wait(), CancellationToken.None);
                return new OnlineLoginReport(OnlineFailure.None, GatewayOutcome.Success, GatewayErrorCode.None, 0);
            });

        var watch = Stopwatch.StartNew();
        Task<OnlineAuthOutcome> task = rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default);
        watch.Stop();

        Assert.False(task.IsCompleted);
        Assert.True(watch.ElapsedMilliseconds < 1000);
        gate.Set();
        Assert.True((await GatewayTestSupport.Bounded(task)).Success);
    }

    [Fact]
    public async Task A_cancelled_token_before_the_login_starts_never_reaches_the_session_call()
    {
        var rig = new BackendRig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Task<OnlineAuthOutcome> task = rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, rig.Port(0).LoginCalls);
    }

    [Fact]
    public async Task The_cancellation_token_is_handed_to_the_session_call()
    {
        var rig = new BackendRig();
        using var cts = new CancellationTokenSource();

        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, cts.Token));

        Assert.Equal(cts.Token, rig.Port(0).LastToken);
    }

    // ---- 会话的更替与释放 ----

    [Fact]
    public async Task Every_login_gets_a_new_session_and_the_old_one_is_released_off_the_calling_thread()
    {
        using var gate = new ManualResetEventSlim(false);
        var rig = new BackendRig(onPort: p => p.DisposeGate = gate);

        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        var watch = Stopwatch.StartNew();
        Task<OnlineAuthOutcome> second = rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default);
        watch.Stop();

        // 旧会话的释放被卡住了,第二次登录照样立刻开始(释放不在调用线程上做)
        Assert.True(watch.ElapsedMilliseconds < 1000);
        Assert.Equal(2, rig.PortCount);
        Assert.Equal(0, rig.Port(0).DisposeCount);

        gate.Set();
        Assert.True(rig.Port(0).WaitDisposed());
        Assert.NotEqual(Environment.CurrentManagedThreadId, rig.Port(0).DisposeThreadId);
        await GatewayTestSupport.Bounded(second);
        Assert.Equal(1, rig.Port(0).DisposeCount);
        Assert.Equal(0, rig.Port(1).DisposeCount);
    }

    [Fact]
    public async Task Disconnect_releases_the_session_once_and_is_idempotent()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        FakeSessionPort port = rig.Port(0);

        rig.Backend.Disconnect();
        rig.Backend.Disconnect();

        Assert.True(port.WaitDisposed());
        Assert.False(rig.Backend.HasSession);
        Assert.Null(rig.Backend.Session);
        await Task.Delay(50);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public void Disconnect_without_a_session_is_harmless()
    {
        var rig = new BackendRig();
        rig.Backend.Disconnect();
        Assert.False(rig.Backend.HasSession);
        Assert.Equal(0, rig.PortCount);
    }

    [Fact]
    public async Task Disconnect_right_after_a_login_call_releases_that_login_session()
    {
        // 登录请求刚发出(任务还没完成)就放弃:会话在调用线程上就已经挂好,紧跟着的 Disconnect 一定看得到它
        using var gate = new ManualResetEventSlim(false);
        var rig = new BackendRig(onPort: p =>
            p.OnLogin = async (user, password, ct) =>
            {
                await Task.Run(() => gate.Wait(), CancellationToken.None);
                return new OnlineLoginReport(OnlineFailure.None, GatewayOutcome.Success, GatewayErrorCode.None, 0);
            });

        Task<OnlineAuthOutcome> task = rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default);
        rig.Backend.Disconnect();

        Assert.True(rig.Port(0).WaitDisposed());
        Assert.False(rig.Backend.HasSession);
        gate.Set();
        await GatewayTestSupport.Bounded(task);
    }

    // ---- 断线事件 ----

    [Fact]
    public async Task A_connection_loss_of_the_current_session_is_forwarded()
    {
        var rig = new BackendRig();
        int lost = 0;
        rig.Backend.Disconnected += () => Interlocked.Increment(ref lost);
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        rig.Port(0).RaiseLost();

        Assert.Equal(1, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task Disconnect_does_not_raise_the_disconnected_event_even_if_the_session_reports_a_loss_while_closing()
    {
        var rig = new BackendRig(onPort: p => p.KeepHandlersOnUnsubscribe = true);
        int lost = 0;
        rig.Backend.Disconnected += () => Interlocked.Increment(ref lost);
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        FakeSessionPort port = rig.Port(0);

        rig.Backend.Disconnect();
        port.RaiseLost();

        Assert.Equal(0, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task A_retired_sessions_late_loss_event_is_ignored_and_the_new_sessions_still_counts()
    {
        var rig = new BackendRig(onPort: p => p.KeepHandlersOnUnsubscribe = true);
        int lost = 0;
        rig.Backend.Disconnected += () => Interlocked.Increment(ref lost);
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        rig.Port(0).RaiseLost();       // 旧会话迟到的断线通知:不算数
        Assert.Equal(0, Volatile.Read(ref lost));

        rig.Port(1).RaiseLost();       // 当前会话的才算
        Assert.Equal(1, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task The_loss_handler_is_removed_from_a_released_session()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        FakeSessionPort port = rig.Port(0);
        Assert.True(port.HasLostSubscribers);

        rig.Backend.Disconnect();

        Assert.False(port.HasLostSubscribers);
    }

    // ---- 连频道、换频道、选角 ----

    [Fact]
    public async Task Connect_passes_the_port_and_maps_the_roster()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        OnlineConnectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31234), default));

        Assert.True(result.Success);
        Assert.Equal(2, result.Characters.Entries.Count);
        Assert.Equal(1, rig.Port(0).ConnectCalls);
        Assert.Equal(0, rig.Port(0).SwitchCalls);
        Assert.Equal(31234, rig.Port(0).LastPort);
    }

    [Fact]
    public async Task Switch_uses_the_session_switch_call_and_not_connect()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        OnlineConnectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.SwitchChannelAsync(new ChannelTarget(31777), default));

        Assert.True(result.Success);
        Assert.Equal(0, rig.Port(0).ConnectCalls);
        Assert.Equal(1, rig.Port(0).SwitchCalls);
        Assert.Equal(31777, rig.Port(0).LastPort);
    }

    [Fact]
    public async Task A_channel_target_with_a_host_is_refused_without_touching_the_session()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        OnlineConnectOutcome connect = await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31000, "channel.invalid"), default));
        OnlineConnectOutcome change = await GatewayTestSupport.Bounded(rig.Backend.SwitchChannelAsync(new ChannelTarget(31000, "channel.invalid"), default));

        Assert.False(connect.Success);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, connect.Failure);
        Assert.False(change.Success);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, change.Failure);
        Assert.Equal(0, rig.Port(0).ConnectCalls);
        Assert.Equal(0, rig.Port(0).SwitchCalls);
    }

    [Fact]
    public async Task A_missing_roster_is_a_failure_with_its_own_category()
    {
        var rig = new BackendRig(onPort: p =>
            p.OnConnect = (port, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.NotReceived, null)));
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        OnlineConnectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.ConnectChannelAsync(new ChannelTarget(31000), default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.CharacterListMissing, result.Failure);
    }

    [Fact]
    public async Task Select_passes_the_slot_and_maps_the_outcome()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        OnlineSelectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.SelectCharacterAsync(3, default));

        Assert.True(result.Entered);
        Assert.Equal(3, rig.Port(0).LastSlot);
    }

    [Fact]
    public async Task A_rejected_selection_keeps_the_retry_hint()
    {
        var rig = new BackendRig(onPort: p =>
            p.OnSelect = (slot, ct) => Task.FromResult(new OnlineSelectReport(false, true, null)));
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        OnlineSelectOutcome result = await GatewayTestSupport.Bounded(rig.Backend.SelectCharacterAsync(0, default));

        Assert.False(result.Entered);
        Assert.Equal(OnlineNoticeKind.SelectFailed, result.Failure);
        Assert.True(result.MayTryAnotherSlot);
        Assert.Empty(rig.LogsSnapshot());
    }

    [Fact]
    public async Task A_successful_selection_logs_only_the_snapshot_line()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        await GatewayTestSupport.Bounded(rig.Backend.SelectCharacterAsync(2, default));

        string line = Assert.Single(rig.LogsSnapshot());
        Assert.Equal("DNF_NET snapshot slot=2", line);
    }

    [Fact]
    public async Task A_log_sink_that_throws_does_not_break_the_selection()
    {
        var rig = new BackendRig();
        var throwing = new OnlineShellBackend(
            rig.Template,
            line => throw new InvalidOperationException("sink failed"),
            settings => new FakeSessionPort(settings),
            options => new TrackingTransport(rig.Gateway));
        await GatewayTestSupport.Bounded(throwing.LoginAsync(BackendRig.User, BackendRig.Password, default));

        OnlineSelectOutcome result = await GatewayTestSupport.Bounded(throwing.SelectCharacterAsync(0, default));

        Assert.True(result.Entered);
        throwing.Dispose();
    }

    [Fact]
    public async Task Operations_before_a_login_or_after_a_disconnect_fault_with_invalid_operation()
    {
        var rig = new BackendRig();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.ConnectChannelAsync(new ChannelTarget(31000), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.SwitchChannelAsync(new ChannelTarget(31000), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.SelectCharacterAsync(0, default));

        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        rig.Backend.Disconnect();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.ConnectChannelAsync(new ChannelTarget(31000), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.SelectCharacterAsync(0, default));
    }

    [Fact]
    public async Task A_null_channel_target_faults_with_argument_null()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Backend.ConnectChannelAsync(null, default));
    }

    // ---- 注册与改密:临时网关客户端,不留会话 ----

    [Fact]
    public async Task Register_creates_the_account_through_a_temporary_gateway_client_without_a_session()
    {
        var rig = new BackendRig();

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync("new_player", "another-secret", default));

        Assert.True(result.Success);
        Assert.True(rig.Gateway.Accounts.ContainsKey("new_player"));
        Assert.Equal(0, rig.PortCount);
        Assert.False(rig.Backend.HasSession);
        TrackingTransport transport = Assert.Single(rig.Transports);
        Assert.True(transport.Disposed);
        // 口令只以 MD5 出站,明文不在任何请求里
        foreach (string plain in rig.Gateway.PostPlaintexts)
        {
            Assert.DoesNotContain("another-secret", plain);
        }
    }

    [Fact]
    public async Task Registering_an_existing_account_is_a_user_exists_notice()
    {
        var rig = new BackendRig();

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync(BackendRig.User, "whatever-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.UserExists, result.Failure);
    }

    [Fact]
    public async Task Registration_being_closed_is_reported_as_such()
    {
        var rig = new BackendRig();
        rig.Gateway.RegistrationOpen = false;

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync("new_player", "another-secret", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.RegistrationClosed, result.Failure);
    }

    [Fact]
    public async Task A_register_with_an_invalid_username_makes_no_request()
    {
        var rig = new BackendRig();

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync("ab", "another-secret", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.InvalidUsername, result.Failure);
        Assert.Equal(0, rig.Gateway.PostCount);
    }

    [Fact]
    public async Task A_rate_limited_register_carries_the_server_countdown()
    {
        var rig = new BackendRig();
        rig.Gateway.RateLimitMax = 0;
        rig.Gateway.RetryAfterHeader = TimeSpan.FromSeconds(17);

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync("new_player", "another-secret", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.RateLimited, result.Failure);
        Assert.Equal(17, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task A_network_failure_during_register_is_a_network_notice()
    {
        var rig = new BackendRig();
        rig.Gateway.ThrowOnRequest = new HttpRequestException("synthetic transport failure");

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync("new_player", "another-secret", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.NetworkError, result.Failure);
        Assert.True(Assert.Single(rig.Transports).Disposed);
    }

    [Fact]
    public async Task An_undecodable_reply_during_register_is_a_protocol_notice()
    {
        var rig = new BackendRig();
        rig.Gateway.ForceStatus = 200;
        rig.Gateway.ForceBody = "this is not an encrypted reply";

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync("new_player", "another-secret", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.ProtocolError, result.Failure);
    }

    [Fact]
    public async Task Change_password_succeeds_and_the_new_password_then_logs_in_at_the_gateway()
    {
        var rig = new BackendRig();

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.ChangePasswordAsync(BackendRig.User, BackendRig.Password, "brand-new-pw", default));

        Assert.True(result.Success);
        Assert.Equal(GatewayCommands.Md5Hex("brand-new-pw"), rig.Gateway.Accounts[BackendRig.User].Md5);
        Assert.Equal(0, rig.PortCount);
        foreach (string plain in rig.Gateway.PostPlaintexts)
        {
            Assert.DoesNotContain("brand-new-pw", plain);
            Assert.DoesNotContain(BackendRig.Password, plain);
        }
    }

    [Fact]
    public async Task Change_password_with_the_wrong_old_password_is_a_wrong_credentials_notice()
    {
        var rig = new BackendRig();

        OnlineAuthOutcome result = await GatewayTestSupport.Bounded(rig.Backend.ChangePasswordAsync(BackendRig.User, "not-the-password", "brand-new-pw", default));

        Assert.False(result.Success);
        Assert.Equal(OnlineNoticeKind.WrongCredentials, result.Failure);
        Assert.Equal(GatewayCommands.Md5Hex(BackendRig.Password), rig.Gateway.Accounts[BackendRig.User].Md5);
    }

    [Fact]
    public async Task Register_does_not_disturb_a_live_session()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        await GatewayTestSupport.Bounded(rig.Backend.RegisterAsync("new_player", "another-secret", default));

        Assert.True(rig.Backend.HasSession);
        Assert.Equal(0, rig.Port(0).DisposeCount);
    }

    // ---- 没有连接配置 ----

    [Fact]
    public async Task Without_a_template_every_network_operation_faults_and_no_session_is_created()
    {
        var rig = new BackendRig(configured: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.RegisterAsync("new_player", "another-secret", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.ChangePasswordAsync(BackendRig.User, "a", "b", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.ConnectChannelAsync(new ChannelTarget(31000), default));

        Assert.Equal(0, rig.PortCount);
        Assert.False(rig.Backend.HasSession);
        Assert.Equal(0, rig.Gateway.PostCount + rig.Gateway.GetCount);
    }

    // ---- 退出 ----

    [Fact]
    public async Task Dispose_waits_for_the_release_to_finish()
    {
        using var gate = new ManualResetEventSlim(false);
        var rig = new BackendRig(onPort: p => p.DisposeGate = gate);
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        FakeSessionPort port = rig.Port(0);

        Task release = Task.Run(() =>
        {
            Thread.Sleep(150);
            gate.Set();
        });
        rig.Backend.Dispose();

        Assert.Equal(1, port.DisposeCount);
        await release;
    }

    [Fact]
    public async Task Dispose_gives_up_waiting_after_about_a_second_if_the_release_hangs()
    {
        using var gate = new ManualResetEventSlim(false);
        var rig = new BackendRig(onPort: p => p.DisposeGate = gate);
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        var watch = Stopwatch.StartNew();
        rig.Backend.Dispose();
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds >= 800, "waited " + watch.ElapsedMilliseconds + " ms");
        Assert.True(watch.ElapsedMilliseconds < 5000, "waited " + watch.ElapsedMilliseconds + " ms");
        Assert.Equal(0, rig.Port(0).DisposeCount);
        gate.Set();
        Assert.True(rig.Port(0).WaitDisposed());
    }

    [Fact]
    public async Task After_dispose_the_backend_refuses_new_work_and_is_idempotent()
    {
        var rig = new BackendRig();
        await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));

        rig.Backend.Dispose();
        rig.Backend.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backend.SelectCharacterAsync(0, default));
        Assert.False(rig.Backend.HasSession);
        // 退出后才发起的登录造出的那个会话也被放弃了,不会泄漏
        FakeSessionPort[] ports = rig.PortsSnapshot();
        Assert.Equal(2, ports.Length);
        Assert.True(ports[0].WaitDisposed());
        Assert.True(ports[1].WaitDisposed());
        Assert.Equal(1, ports[0].DisposeCount);
        Assert.Equal(1, ports[1].DisposeCount);
    }

    // ---- 传输 ----

    [Fact]
    public void The_http_transport_factory_builds_a_real_transport_that_can_be_released()
    {
        Assert.True(GatewayOptions.TryCreate("http://localhost", GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey()), TimeSpan.FromSeconds(1), out GatewayOptions options, out _));

        IGatewayTransport transport = OnlineShellBackend.CreateHttpTransport(options);

        Assert.IsType<HttpGatewayTransport>(transport);
        (transport as IDisposable)?.Dispose();
    }
}
