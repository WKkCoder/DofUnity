using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>分阶段联网会话：状态、结果、取消、断线、心跳互斥与旧 RunAsync 的逐帧等价性。</summary>
public sealed class OnlineClientSessionTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static async Task Bounded(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, finished);
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        await Bounded(Task.Run(async () =>
        {
            while (!condition()) await Task.Delay(10);
            return 0;
        }));
    }

    private static void AssertWritesEqual(IReadOnlyList<byte[]> expected, IReadOnlyList<byte[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    private static (string Token, string Account) LoginFields(DungeonEntryClientFrame frame)
    {
        int offset = 4; // PatchTime
        uint tokenLength = BitConverter.ToUInt32(frame.Plain, offset);
        offset += 4;
        string token = System.Text.Encoding.ASCII.GetString(frame.Plain, offset, (int)tokenLength);
        offset += (int)tokenLength;
        uint accountLength = BitConverter.ToUInt32(frame.Plain, offset);
        offset += 4;
        string account = System.Text.Encoding.ASCII.GetString(frame.Plain, offset, (int)accountLength);
        return (token, account);
    }

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script(
        byte[][] login = null, byte[][] list = null, Func<int, byte[][]> select = null)
    {
        return HostTestKit.TownScript(login ?? HostTestKit.LoginOk(), list ?? HostTestKit.Frames(HostTestKit.DirectListNotice()),
            select ?? (_ => HostTestKit.EnterComplete()));
    }

    [Fact]
    public async Task Login_stores_gateway_progress_without_opening_a_channel()
    {
        var rig = new OnlineFlowRig();
        var stages = new List<OnlineStage>();
        OnlineClientSession client = rig.CreateSession(stages.Add);

        OnlineLoginResult result = await client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password);

        Assert.True(result.IsSuccess);
        Assert.Equal(OnlineFailure.None, result.Failure);
        Assert.Equal(OnlineStage.GameServerAddress, result.Stage);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Equal(new[] { OnlineStage.GatewayLogin, OnlineStage.GameServerAddress }, stages);
        Assert.Empty(rig.Channels);
    }

    [Fact]
    public async Task Wrong_credentials_fail_at_gateway_login_and_allow_a_retry()
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client = rig.CreateSession();
        OnlineLoginResult failed = await client.LoginAsync(OnlineFlowRig.Account, "wrong900");
        Assert.False(failed.IsSuccess);
        Assert.Equal(OnlineStage.GatewayLogin, failed.Stage);
        Assert.Equal(OnlineFailure.GatewayLoginFailed, failed.Failure);
        Assert.Equal(OnlineClientSessionState.Created, client.State);
        OnlineLoginResult retry = await client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password);
        Assert.True(retry.IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-address")]
    public async Task Missing_or_invalid_server_address_is_distinct_from_login_failure(string address)
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.GameServerIp = address;
        OnlineClientSession client = rig.CreateSession();
        OnlineLoginResult result = await client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password);
        Assert.Equal(OnlineStage.GameServerAddress, result.Stage);
        Assert.Equal(OnlineFailure.NoGameServerAddress, result.Failure);
        Assert.Equal(OnlineClientSessionState.Created, client.State);
    }

    [Fact]
    public async Task Pre_cancelled_login_stops_before_gateway_io()
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client = rig.CreateSession();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password, stop.Token));
        Assert.Equal(0, rig.FakeGateway.PostCount);
    }

    [Fact]
    public async Task Cancellation_while_getting_address_does_not_advance_to_logged_in()
    {
        var rig = new OnlineFlowRig();
        int calls = 0;
        rig.FakeGateway.BeforeRespond = async ct =>
        {
            if (Interlocked.Increment(ref calls) == 2) await Task.Delay(Timeout.Infinite, ct);
        };
        OnlineClientSession client = rig.CreateSession();
        using var stop = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password, stop.Token));
        Assert.Equal(OnlineClientSessionState.Created, client.State);
    }

    [Fact]
    public async Task A_second_login_is_an_order_error()
    {
        OnlineClientSession client = await new OnlineFlowRig().LoggedIn();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password));
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("select")]
    [InlineData("switch")]
    [InlineData("heartbeat")]
    [InlineData("exclusive")]
    public async Task Operations_before_their_required_stage_fail_clearly(string operation)
    {
        OnlineClientSession client = new OnlineFlowRig().CreateSession();
        Task action = operation switch
        {
            "connect" => client.ConnectAsync(9010),
            "select" => client.SelectCharacterAsync(0),
            "switch" => client.SwitchChannelAsync(9010),
            "heartbeat" => client.SetHeartbeatAsync(true),
            _ => client.EnterExclusiveReceiveAsync(),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => action);
    }

    [Fact]
    public async Task Connect_returns_channel_and_the_actual_character_list()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, OnlineConnectResult result) = await rig.Connected();
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
        Assert.NotNull(result.Channel);
        Assert.True(result.CharacterListReceived);
        Assert.Equal(OnlineCharacterListStatus.Received, result.CharacterListStatus);
        Assert.Equal(3, result.CharacterList.Entries.Count);
        Assert.Equal(new ushort[] { 1, 8 }, rig.Channels[0].Pipe.ClientFrames.Select(f => f.Id).ToArray());
        (string token, string account) = LoginFields(rig.Channels[0].Pipe.ClientFrames.Single(f => f.Id == 1));
        Assert.Equal(rig.FakeGateway.LastToken, token);
        Assert.Equal(OnlineFlowRig.Account, account);
        Assert.NotNull(client.GameSession);
    }

    [Fact]
    public async Task Missing_list_is_not_reported_as_an_empty_list()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(list: HostTestKit.Frames());
        (_, OnlineConnectResult result) = await rig.Connected();
        Assert.False(result.CharacterListReceived);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, result.CharacterListStatus);
        Assert.Null(result.CharacterList);
    }

    [Fact]
    public async Task A_received_zero_entry_list_is_received_and_empty()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(list: HostTestKit.Frames(HostTestKit.DirectListNotice(UserInfoTestData.Packet())));
        (_, OnlineConnectResult result) = await rig.Connected();
        Assert.True(result.CharacterListReceived);
        Assert.NotNull(result.CharacterList);
        Assert.Empty(result.CharacterList.Entries);
    }

    [Fact]
    public async Task A_malformed_mode_two_list_is_not_silent_absence()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(list: HostTestKit.Frames(HostTestKit.DirectListNotice(new byte[] { 2, 1 })));
        (_, OnlineConnectResult result) = await rig.Connected();
        Assert.Equal(OnlineCharacterListStatus.Malformed, result.CharacterListStatus);
        Assert.Null(result.CharacterList);
    }

    [Fact]
    public async Task An_unsupported_user_info_mode_remains_not_received()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(list: HostTestKit.Frames(HostTestKit.DirectListNotice(new byte[] { 1, 9, 9 })));
        (_, OnlineConnectResult result) = await rig.Connected();
        Assert.Equal(OnlineCharacterListStatus.NotReceived, result.CharacterListStatus);
    }

    [Fact]
    public async Task A_list_arriving_with_the_login_reply_is_returned_by_connect()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            HostTestKit.Frames(DungeonEntryWire.Reply(1, HostTestKit.LoginSuccess()), HostTestKit.DirectListNotice()),
            HostTestKit.Frames());
        (_, OnlineConnectResult result) = await rig.Connected();
        Assert.True(result.CharacterListReceived);
        Assert.Equal(3, result.CharacterList.Entries.Count);
    }

    [Fact]
    public async Task An_unreadable_account_id_skips_only_the_list_request()
    {
        var rig = new OnlineFlowRig();
        byte[] login = { 1, 99, 0, 0, 0 };
        rig.ScriptFactory = _ => Script(HostTestKit.Frames(DungeonEntryWire.Reply(1, login)), HostTestKit.Frames());
        (OnlineClientSession client, OnlineConnectResult connected) = await rig.Connected();
        Assert.Equal(OnlineCharacterListStatus.NotReceived, connected.CharacterListStatus);
        Assert.Equal(new ushort[] { 1 }, rig.Channels[0].Pipe.ClientFrames.Select(f => f.Id).ToArray());
        OnlineSelectCharacterResult selected = await client.SelectCharacterAsync(0);
        Assert.True(selected.Entered);
    }

    [Fact]
    public async Task Game_login_rejection_preserves_its_failure_code()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(HostTestKit.Frames(DungeonEntryWire.Reply(1, new byte[] { 0, 0x17, 0, 0 })));
        OnlineClientSession client = await rig.LoggedIn();
        OnlineConnectResult result = await client.ConnectAsync(9010);
        Assert.Equal(OnlineStage.GameLogin, result.Stage);
        Assert.Equal(OnlineFailure.GameLoginRejected, result.Failure);
        Assert.Equal((byte)0x17, result.LoginFailureCode);
        Assert.Null(result.Channel);
    }

    [Fact]
    public async Task Game_login_without_a_reply_returns_no_reply()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(HostTestKit.Frames());
        OnlineClientSession client = await rig.LoggedIn();
        OnlineConnectResult result = await client.ConnectAsync(9010);
        Assert.Equal(OnlineFailure.GameLoginNoReply, result.Failure);
        Assert.Equal(OnlineStage.GameLogin, result.Stage);
    }

    [Fact]
    public async Task Locally_invalid_game_login_is_reported_without_a_game_frame()
    {
        var rig = new OnlineFlowRig();
        string account = new string('a', 25);
        string md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(OnlineFlowRig.Password))).ToLowerInvariant();
        rig.FakeGateway.Accounts[account] = new FakeGateway.Account { Md5 = md5, Qq = string.Empty };
        OnlineClientSession client = rig.CreateSession();
        Assert.True((await client.LoginAsync(account, OnlineFlowRig.Password)).IsSuccess);
        OnlineConnectResult result = await client.ConnectAsync(9010);
        Assert.Equal(OnlineFailure.BadLoginRequest, result.Failure);
        Assert.Empty(rig.Channels[0].Pipe.ClientFrames);
    }

    [Fact]
    public async Task Channel_connect_failure_has_its_own_stage()
    {
        var rig = new OnlineFlowRig();
        rig.FailChannel = _ => true;
        OnlineClientSession client = await rig.LoggedIn();
        OnlineConnectResult result = await client.ConnectAsync(9010);
        Assert.Equal(OnlineStage.ChannelConnect, result.Stage);
        Assert.Equal(OnlineFailure.ConnectFailed, result.Failure);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
    }

    [Fact]
    public async Task Closed_channel_before_bootstrap_fails_bootstrap()
    {
        var rig = new OnlineFlowRig();
        rig.BootstrapFactory = _ => null;
        rig.CloseBeforeBootstrap = _ => true;
        OnlineClientSession client = await rig.LoggedIn();
        OnlineConnectResult result = await client.ConnectAsync(9010);
        Assert.Equal(OnlineStage.Bootstrap, result.Stage);
        Assert.Equal(OnlineFailure.BootstrapFailed, result.Failure);
    }

    [Fact]
    public async Task Explicit_acceptance_refusal_maps_to_rejected()
    {
        var rig = new OnlineFlowRig();
        rig.BootstrapFactory = _ => DungeonEntryWire.Refusal();
        OnlineClientSession client = await rig.LoggedIn();
        OnlineConnectResult result = await client.ConnectAsync(9010);
        Assert.Equal(OnlineFailure.Rejected, result.Failure);
        Assert.Equal(OnlineStage.Bootstrap, result.Stage);
    }

    [Fact]
    public async Task Cancellation_interrupts_a_pending_bootstrap()
    {
        var rig = new OnlineFlowRig();
        rig.BootstrapFactory = _ => null;
        OnlineClientSession client = await rig.LoggedIn();
        using var stop = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(9010, stop.Token));
    }

    [Fact]
    public async Task Select_returns_parsed_reply_snapshot_and_selected_state()
    {
        var rig = new OnlineFlowRig();
        SelectCharacterSpec spec = SelectCharacterSpec.Variant(3);
        rig.ScriptFactory = _ => Script(select: _ => HostTestKit.Frames(DungeonEntryWire.Reply(4, spec.Build())));
        (OnlineClientSession client, OnlineSelectCharacterResult result) = await rig.Selected(2);
        Assert.True(result.Entered);
        Assert.Equal(2, result.Slot);
        Assert.Equal(2, result.RequestedSlot);
        Assert.NotNull(result.SelectCharacter);
        Assert.Equal(spec.Village, result.SelectCharacter.Load.Village);
        Assert.Equal(63, result.Snapshot.Level);
        Assert.Equal(OnlineClientSessionState.CharacterSelected, client.State);
        Assert.NotNull(client.GameSession);
    }

    [Fact]
    public async Task Explicit_slot_failure_can_retry_and_keeps_the_failure_result()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(select: slot => slot == 0
            ? HostTestKit.Frames(DungeonEntryWire.Reply(4, SelectCharacterSpec.Failure(4)))
            : HostTestKit.EnterComplete());
        (OnlineClientSession client, _) = await rig.Connected();
        OnlineSelectCharacterResult failed = await client.SelectCharacterAsync(0);
        Assert.False(failed.Entered);
        Assert.True(failed.MayTryAnotherSlot);
        Assert.Equal(SelectCharacterFailure.SlotOutOfRange, failed.SelectCharacter.Failure);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
        Assert.True((await client.SelectCharacterAsync(1)).Entered);
    }

    [Fact]
    public async Task No_slot_verdict_can_retry_without_inventing_a_select_result()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(select: _ => HostTestKit.Frames());
        (OnlineClientSession client, _) = await rig.Connected();
        OnlineSelectCharacterResult result = await client.SelectCharacterAsync(0);
        Assert.False(result.Entered);
        Assert.True(result.MayTryAnotherSlot);
        Assert.Null(result.SelectCharacter);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
    }

    [Fact]
    public async Task Selecting_again_after_success_is_an_order_error()
    {
        (OnlineClientSession client, _) = await new OnlineFlowRig().Selected();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SelectCharacterAsync(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task Public_slot_argument_must_fit_the_one_byte_protocol(int slot)
    {
        (OnlineClientSession client, _) = await new OnlineFlowRig().Connected();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SelectCharacterAsync(slot));
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    [Fact]
    public async Task Pre_cancelled_select_sends_no_frame()
    {
        (OnlineClientSession client, _) = await new OnlineFlowRig().Connected();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SelectCharacterAsync(0, stop.Token));
        Assert.DoesNotContain(client.GameSession.Events, e => e.Outbound && e.PacketId == 4);
    }

    [Fact]
    public async Task Disconnect_closes_the_link_and_allows_a_fresh_connect()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected();
        await client.DisconnectAsync();
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Null(client.GameSession);
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        Assert.True((await client.ConnectAsync(9011)).IsSuccess);
        Assert.Equal(2, rig.Channels.Count);
    }

    [Fact]
    public async Task Disconnect_is_idempotent()
    {
        OnlineClientSession client = await new OnlineFlowRig().LoggedIn();
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("connect")]
    [InlineData("select")]
    [InlineData("switch")]
    [InlineData("heartbeat")]
    [InlineData("exclusive")]
    public async Task Public_work_after_dispose_fails_immediately(string operation)
    {
        OnlineClientSession client = new OnlineFlowRig().CreateSession();
        client.Dispose();
        client.Dispose();
        Task action = operation switch
        {
            "login" => client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password),
            "connect" => client.ConnectAsync(9010),
            "select" => client.SelectCharacterAsync(0),
            "switch" => client.SwitchChannelAsync(9010),
            "heartbeat" => client.SetHeartbeatAsync(false),
            _ => client.EnterExclusiveReceiveAsync(),
        };
        await Assert.ThrowsAsync<ObjectDisposedException>(() => action);
    }

    [Fact]
    public async Task Disconnect_after_dispose_is_a_safe_no_op()
    {
        OnlineClientSession client = new OnlineFlowRig().CreateSession();
        client.Dispose();
        await client.DisconnectAsync();
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
    }

    [Fact]
    public async Task Automatic_heartbeat_starts_only_after_selection()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected(autoHeartbeat: true);
        Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
        Assert.True((await client.SelectCharacterAsync(0)).Entered);
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);
        await client.SetHeartbeatAsync(false);
    }

    [Fact]
    public async Task Manual_heartbeat_switch_is_idempotent_and_restartable()
    {
        (OnlineClientSession client, _) = await new OnlineFlowRig().Selected();
        Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
        await client.SetHeartbeatAsync(true);
        await client.SetHeartbeatAsync(true);
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);
        await client.SetHeartbeatAsync(false);
        await client.SetHeartbeatAsync(false);
        Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
        await client.SetHeartbeatAsync(true);
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);
        await client.SetHeartbeatAsync(false);
    }

    [Fact]
    public async Task Heartbeat_pump_answers_the_synthetic_heartbeat()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(OnlineHeartbeat.HeartbeatPacketId, new byte[] { 0 }));
        await WaitUntil(() => rig.Channels[0].Pipe.ClientFrames.Any(f => f.Id == OnlineHeartbeat.ReplyPacketId));
        DungeonEntryClientFrame reply = rig.Channels[0].Pipe.ClientFrames.Single(f => f.Id == OnlineHeartbeat.ReplyPacketId);
        Assert.Equal(PacketClass.Cmd, reply.Class);
        Assert.Equal(new byte[8], reply.Plain.Take(8).ToArray());
        await client.SetHeartbeatAsync(false);
    }

    [Theory]
    [InlineData(OnlineDisconnectReason.PeerClosed)]
    [InlineData(OnlineDisconnectReason.ReadError)]
    public async Task Heartbeat_pump_reports_peer_close_and_read_error(OnlineDisconnectReason expected)
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var observed = new TaskCompletionSource<OnlineDisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => observed.TrySetResult(reason);
        if (expected == OnlineDisconnectReason.ReadError) rig.Channels[0].Link.ReadFault = true;
        rig.Channels[0].Pipe.CloseServer();
        Assert.Equal(expected, await Bounded(observed.Task));
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
    }

    [Fact]
    public async Task Heartbeat_write_failure_reports_write_error()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var observed = new TaskCompletionSource<OnlineDisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => observed.TrySetResult(reason);
        rig.Channels[0].Pipe.WriteFailure = new IOException("synthetic");
        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(OnlineHeartbeat.HeartbeatPacketId, new byte[] { 0 }));
        Assert.Equal(OnlineDisconnectReason.WriteError, await Bounded(observed.Task));
    }

    [Fact]
    public async Task Heartbeat_inactivity_timeout_is_reported_once()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromMilliseconds(60);
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        int count = 0;
        var observed = new TaskCompletionSource<OnlineDisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => { Interlocked.Increment(ref count); observed.TrySetResult(reason); };
        Assert.Equal(OnlineDisconnectReason.HeartbeatTimeout, await Bounded(observed.Task));
        await Task.Delay(80);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Exclusive_receive_stops_the_pump_and_leaves_frames_for_dungeon()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        await client.EnterExclusiveReceiveAsync();
        Assert.Equal(OnlineClientSessionState.ExclusiveReceive, client.State);
        Assert.Equal(OnlineHeartbeatState.ExclusiveReceive, client.HeartbeatState);
        rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(900, new byte[] { 9 }));
        List<DecodedFrame> frames = await client.GameSession.ReceiveAsync(TimeSpan.FromMilliseconds(100));
        Assert.Contains(frames, f => f.Header.PacketId == 900);
        Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == OnlineHeartbeat.ReplyPacketId);
    }

    [Fact]
    public async Task Exclusive_receive_cannot_restart_heartbeat()
    {
        (OnlineClientSession client, _) = await new OnlineFlowRig().Selected();
        await client.EnterExclusiveReceiveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetHeartbeatAsync(true));
    }

    [Fact]
    public async Task Switch_closes_old_channel_reuses_login_and_returns_new_list_state()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = channel => channel == 0 ? Script() : Script(list: HostTestKit.Frames());
        (OnlineClientSession client, _) = await rig.Selected();
        byte[] firstLogin = rig.Channels[0].Pipe.ClientFrames.Single(f => f.Id == 1).Plain;

        OnlineConnectResult result = await client.SwitchChannelAsync(9020);

        Assert.True(result.IsSuccess);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, result.CharacterListStatus);
        Assert.Equal(new[] { 9010, 9020 }, rig.RequestedPorts);
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        Assert.Equal(firstLogin, rig.Channels[1].Pipe.ClientFrames.Single(f => f.Id == 1).Plain);
        Assert.Equal(rig.FakeGateway.LastToken, LoginFields(rig.Channels[1].Pipe.ClientFrames.Single(f => f.Id == 1)).Token);
        Assert.Equal(1, rig.FakeGateway.PostCount);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    [Fact]
    public async Task Switch_failure_does_not_run_gateway_login_again()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected();
        rig.FailChannel = channel => channel == 1;
        OnlineConnectResult result = await client.SwitchChannelAsync(9020);
        Assert.Equal(OnlineFailure.ConnectFailed, result.Failure);
        Assert.Equal(1, rig.FakeGateway.PostCount);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
    }

    [Fact]
    public async Task RunAsync_success_is_byte_for_byte_equivalent_to_staged_calls()
    {
        var wholeRig = new OnlineFlowRig();
        var stagedRig = new OnlineFlowRig();
        wholeRig.FakeGateway.ForceReplyPlaintext = "0|TOKEN900";
        stagedRig.FakeGateway.ForceReplyPlaintext = "0|TOKEN900";
        var wholeStages = new List<OnlineStage>();
        var stagedStages = new List<OnlineStage>();

        OnlineEntryResult whole = await wholeRig.CreateTownEntry().RunAsync(wholeStages.Add);
        OnlineClientSession staged = await stagedRig.LoggedIn(stagedStages.Add);
        Assert.True((await staged.ConnectAsync(9010)).IsSuccess);
        Assert.True((await staged.SelectCharacterAsync(0)).Entered);

        Assert.True(whole.Entered);
        Assert.Equal(wholeStages, stagedStages);
        AssertWritesEqual(wholeRig.Channels[0].Recording.Writes, stagedRig.Channels[0].Recording.Writes);
        Assert.Equal(0, whole.Slot);
        Assert.Equal(61, whole.Snapshot.Level);
        Assert.NotNull(whole.Session);
    }

    [Fact]
    public async Task RunAsync_keeps_blind_slot_retry_and_reports_select_stage_once()
    {
        var rig = new OnlineFlowRig();
        rig.Settings.MaxSlots = 3;
        rig.ScriptFactory = _ => Script(select: slot => slot == 2 ? HostTestKit.EnterComplete() : HostTestKit.Frames());
        var stages = new List<OnlineStage>();
        OnlineEntryResult result = await rig.CreateTownEntry().RunAsync(stages.Add);
        Assert.True(result.Entered);
        Assert.Equal(2, result.Slot);
        Assert.Equal(new byte[] { 0, 1, 2 }, rig.Channels[0].Pipe.ClientFrames.Where(f => f.Id == 4).Select(f => f.Plain[0]).ToArray());
        Assert.Equal(1, stages.Count(s => s == OnlineStage.SelectSlot));
    }

    [Fact]
    public async Task RunAsync_with_zero_slots_still_reports_select_stage_and_fails()
    {
        var rig = new OnlineFlowRig();
        rig.Settings.MaxSlots = 0;
        var stages = new List<OnlineStage>();
        OnlineEntryResult result = await rig.CreateTownEntry().RunAsync(stages.Add);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.Equal(OnlineStage.SelectSlot, result.Stage);
        Assert.Contains(OnlineStage.SelectSlot, stages);
    }

    [Theory]
    [InlineData("gateway", OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed)]
    [InlineData("channel", OnlineStage.ChannelConnect, OnlineFailure.ConnectFailed)]
    [InlineData("bootstrap", OnlineStage.Bootstrap, OnlineFailure.Rejected)]
    [InlineData("game", OnlineStage.GameLogin, OnlineFailure.GameLoginRejected)]
    public async Task RunAsync_failure_mapping_stays_on_the_original_stage(string kind, OnlineStage stage, OnlineFailure failure)
    {
        var rig = new OnlineFlowRig();
        switch (kind)
        {
            case "gateway": rig.Settings.Password = "wrong900"; break;
            case "channel": rig.FailChannel = _ => true; break;
            case "bootstrap": rig.BootstrapFactory = _ => DungeonEntryWire.Refusal(); break;
            default: rig.ScriptFactory = _ => Script(HostTestKit.Frames(DungeonEntryWire.Reply(1, new byte[] { 0, 0x17, 0, 0 }))); break;
        }
        OnlineEntryResult result = await rig.CreateTownEntry().RunAsync();
        Assert.False(result.Entered);
        Assert.Equal(stage, result.Stage);
        Assert.Equal(failure, result.Failure);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task RunAsync_cancellation_propagates()
    {
        var rig = new OnlineFlowRig();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.CreateTownEntry().RunAsync(cancellationToken: stop.Token));
    }

    [Fact]
    public async Task Cancelled_select_requires_reconnect_so_a_late_reply_cannot_select_another_slot()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = new OnlineTownWindows(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(30));
        rig.ScriptFactory = channel => channel == 0
            ? Script(select: slot =>
            {
                Task.Run(async () =>
                {
                    await Task.Delay(100);
                    rig.Channels[0].Pipe.Push(DungeonEntryWire.Notice(124, new byte[] { 0 }));
                });
                return HostTestKit.Frames();
            })
            : Script();
        (OnlineClientSession client, _) = await rig.Connected();
        using var stop = new CancellationTokenSource(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SelectCharacterAsync(0, stop.Token));
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);

        Assert.True((await client.ConnectAsync(9011)).IsSuccess);
        OnlineSelectCharacterResult selected = await client.SelectCharacterAsync(1);
        Assert.True(selected.Entered);
        Assert.Equal(1, selected.RequestedSlot);
        Assert.Equal(2, rig.Channels.Count);
    }

    [Fact]
    public async Task Dispose_cancels_pending_bootstrap_and_cannot_be_overwritten_by_its_continuation()
    {
        var rig = new OnlineFlowRig();
        rig.BootstrapFactory = _ => null;
        OnlineClientSession client = await rig.LoggedIn();
        Task<OnlineConnectResult> connecting = client.ConnectAsync(9010);
        await WaitUntil(() => rig.Channels.Count == 1);

        Task disposing = Task.Run(client.Dispose);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(connecting));
        await Bounded(disposing);
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
        Assert.Null(client.GameSession);
    }

    [Fact]
    public async Task Exclusive_mode_rejects_switch_but_allows_explicit_cleanup()
    {
        (OnlineClientSession client, _) = await new OnlineFlowRig().Selected();
        await client.EnterExclusiveReceiveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SwitchChannelAsync(9020));
        await client.DisconnectAsync();
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
    }

    [Fact]
    public async Task Connect_reports_peer_close_even_when_heartbeat_is_not_running()
    {
        var rig = new OnlineFlowRig();
        rig.BootstrapFactory = _ => null;
        rig.CloseBeforeBootstrap = _ => true;
        OnlineClientSession client = await rig.LoggedIn();
        var observed = new TaskCompletionSource<OnlineDisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => observed.TrySetResult(reason);
        OnlineConnectResult result = await client.ConnectAsync(9010);
        Assert.Equal(OnlineFailure.BootstrapFailed, result.Failure);
        Assert.Equal(OnlineDisconnectReason.PeerClosed, await Bounded(observed.Task));
    }

    [Fact]
    public async Task Disconnected_handler_may_dispose_without_self_deadlock()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true);
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ =>
        {
            client.Dispose();
            finished.TrySetResult(0);
        };
        rig.Channels[0].Pipe.CloseServer();
        await Bounded(finished.Task);
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
    }

    [Fact]
    public async Task A_custom_dispatcher_receives_progress_callbacks()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.InlineCallbacks = false;
        var dispatcher = new OnlineInlineDispatcher();
        var stages = new List<OnlineStage>();
        var client = new OnlineClientSession(rig.Settings, stages.Add, dispatcher, rig.Hooks);
        Assert.True((await client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password)).IsSuccess);
        Assert.Equal(2, dispatcher.Calls);
        Assert.Equal(new[] { OnlineStage.GatewayLogin, OnlineStage.GameServerAddress }, stages);
    }

    [Fact]
    public async Task Default_dispatcher_preserves_progress_order_in_the_background()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.InlineCallbacks = false;
        var stages = new List<OnlineStage>();
        var client = new OnlineClientSession(rig.Settings, stages.Add, null, rig.Hooks);
        Assert.True((await client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password)).IsSuccess);
        await WaitUntil(() => stages.Count == 2);
        Assert.Equal(new[] { OnlineStage.GatewayLogin, OnlineStage.GameServerAddress }, stages);
    }

    [Fact]
    public async Task RunAsync_multi_slot_retry_is_byte_for_byte_equivalent_to_staged_calls()
    {
        var wholeRig = new OnlineFlowRig();
        var stagedRig = new OnlineFlowRig();
        foreach (OnlineFlowRig rig in new[] { wholeRig, stagedRig })
        {
            rig.FakeGateway.ForceReplyPlaintext = "0|TOKEN900";
            rig.ScriptFactory = _ => Script(select: slot => slot == 2 ? HostTestKit.EnterComplete() : HostTestKit.Frames());
        }
        OnlineEntryResult whole = await wholeRig.CreateTownEntry().RunAsync();
        OnlineClientSession staged = await stagedRig.LoggedIn();
        Assert.True((await staged.ConnectAsync(9010)).IsSuccess);
        for (int slot = 0; slot <= 2; slot++)
        {
            OnlineSelectCharacterResult selected = await staged.SelectCharacterAsync(slot);
            Assert.Equal(slot == 2, selected.Entered);
        }
        Assert.Equal(2, whole.Slot);
        AssertWritesEqual(wholeRig.Channels[0].Recording.Writes, stagedRig.Channels[0].Recording.Writes);
    }

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("game")]
    public async Task RunAsync_failure_frames_match_the_same_staged_script(string kind)
    {
        var wholeRig = new OnlineFlowRig();
        var stagedRig = new OnlineFlowRig();
        foreach (OnlineFlowRig rig in new[] { wholeRig, stagedRig })
        {
            rig.FakeGateway.ForceReplyPlaintext = "0|TOKEN900";
            if (kind == "bootstrap") rig.BootstrapFactory = _ => DungeonEntryWire.Refusal();
            else rig.ScriptFactory = _ => Script(HostTestKit.Frames(DungeonEntryWire.Reply(1, new byte[] { 0, 0x17, 0, 0 })));
        }
        OnlineEntryResult whole = await wholeRig.CreateTownEntry().RunAsync();
        OnlineClientSession staged = await stagedRig.LoggedIn();
        OnlineConnectResult failure = await staged.ConnectAsync(9010);
        Assert.False(failure.IsSuccess);
        Assert.Equal(whole.Stage, failure.Stage);
        Assert.Equal(whole.Failure, failure.Failure);
        AssertWritesEqual(wholeRig.Channels[0].Recording.Writes, stagedRig.Channels[0].Recording.Writes);
    }
}
