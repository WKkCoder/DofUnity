using System.Diagnostics;
using System.Reflection;
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

    // =====================================================================================================
    // 验收后补测试:逐条堵住变异抽检里存活的缺口(每组注释里标了它盯的变异编号)。
    // 协议数字(包号、槽号边界、帧数上限、心跳超时窗口)一律写字面值,不引用产品常量;数据全是合成的。
    // 所有等待都有上限(5 秒),回归时是断言失败而不是挂住。
    // =====================================================================================================

    /// <summary>与心跳、选角、角色列表都无关的通知帧(包号 900 不属于任何被识别的集合):用来制造"收到了一帧"。</summary>
    private static byte[] IdleNotice() => DungeonEntryWire.Notice(900, new byte[] { 9 });

    /// <summary>服务端发来的心跳请求:类 0(通知)、包号 0、载荷一个字节。</summary>
    private static byte[] HeartbeatRequest() => DungeonEntryWire.Notice(0, new byte[] { 0 });

    /// <summary>客户端已写出的心跳回复数:类 1(命令)、包号 0。</summary>
    private static int HeartbeatReplies(DungeonEntryPipe pipe) =>
        pipe.ClientFrames.Count(f => f.Class == PacketClass.Cmd && f.Id == 0);

    /// <summary>
    /// 经反射直接调用会话内部的断线通知:产品自己的路径造不出"已被换下的旧链路"和"释放进行到一半"这两种时序,
    /// 但守卫必须挡住它们,所以直接以这两种链路调用同一个入口。
    /// </summary>
    private static void NotifyDisconnectedDirectly(OnlineClientSession client, OnlineChannelLink link, OnlineDisconnectReason reason)
    {
        MethodInfo method = typeof(OnlineClientSession).GetMethod("NotifyDisconnected", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(client, new object[] { link, reason });
    }

    // ---- F02:连频道只允许从"已登录"进入;链路已经开着时再连是顺序错误,且不动现有链路 ----

    [Theory]
    [InlineData("connected")]
    [InlineData("selected")]
    [InlineData("exclusive")]
    public async Task Connect_is_refused_once_a_channel_is_open_and_leaves_the_live_link_alone(string state)
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client;
        OnlineClientSessionState expected;
        switch (state)
        {
            case "connected":
                (client, _) = await rig.Connected();
                expected = OnlineClientSessionState.Connected;
                break;
            case "selected":
                (client, _) = await rig.Selected();
                expected = OnlineClientSessionState.CharacterSelected;
                break;
            default:
                (client, _) = await rig.Selected();
                await client.EnterExclusiveReceiveAsync();
                expected = OnlineClientSessionState.ExclusiveReceive;
                break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(9011));

        Assert.Equal(expected, client.State);
        Assert.Single(rig.RequestedPorts);                          // 没有再去开第二条频道连接
        Assert.Single(rig.Channels);
        Assert.Equal(0, rig.Channels[0].Connection.DisposeCount);   // 现有链路原样保留,没被关
        Assert.NotNull(client.GameSession);
        client.Dispose();
    }

    // ---- F06:断开之后回到哪个状态,取决于手里有没有网关令牌 ----

    [Fact]
    public async Task Disconnect_before_any_login_keeps_the_session_created_and_connect_stays_an_order_error()
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client = rig.CreateSession();

        await Bounded(client.DisconnectAsync());

        Assert.Equal(OnlineClientSessionState.Created, client.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(9010));
        Assert.Empty(rig.Channels);
        Assert.True((await Bounded(client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password))).IsSuccess);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
    }

    [Fact]
    public async Task Disconnect_after_a_failed_login_keeps_the_session_created()
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client = rig.CreateSession();
        OnlineLoginResult failed = await Bounded(client.LoginAsync(OnlineFlowRig.Account, "wrong900"));
        Assert.Equal(OnlineFailure.GatewayLoginFailed, failed.Failure);

        await Bounded(client.DisconnectAsync());

        Assert.Equal(OnlineClientSessionState.Created, client.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(9010));
        Assert.Empty(rig.Channels);
    }

    // ---- H02:选角成功之前不能开心跳(链路已开但还没选角时,泵起来会抢读选角回包) ----

    [Theory]
    [InlineData("created", false)]
    [InlineData("logged-in", false)]
    [InlineData("connected", false)]
    [InlineData("selected", true)]
    public async Task Heartbeat_can_only_be_enabled_once_a_character_is_selected(string state, bool allowed)
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client;
        switch (state)
        {
            case "created":
                client = rig.CreateSession();
                break;
            case "logged-in":
                client = await rig.LoggedIn();
                break;
            case "connected":
                (client, _) = await rig.Connected();
                break;
            default:
                (client, _) = await rig.Selected();
                break;
        }

        if (allowed)
        {
            await Bounded(client.SetHeartbeatAsync(true));
            Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);
            await Bounded(client.SetHeartbeatAsync(false));
            Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetHeartbeatAsync(true));
            Assert.Equal(OnlineHeartbeatState.Disabled, client.HeartbeatState);
        }
        client.Dispose();
    }

    // ---- F07:网关"成功但没有令牌"的回包,会话看到的是登录失败(守卫在网关客户端里,会话这一层是第二道) ----

    [Theory]
    [InlineData("success")]
    [InlineData("0")]
    public async Task A_gateway_success_reply_without_a_token_is_a_login_failure(string plaintext)
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ForceReplyPlaintext = plaintext;
        OnlineClientSession client = rig.CreateSession();

        OnlineLoginResult result = await Bounded(client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password));

        Assert.False(result.IsSuccess);
        Assert.Equal(OnlineStage.GatewayLogin, result.Stage);
        Assert.Equal(OnlineFailure.GatewayLoginFailed, result.Failure);
        Assert.Equal(OnlineClientSessionState.Created, client.State);
        Assert.Equal(0, rig.FakeGateway.GetCount);                  // 登录没成功,就不去取游戏服地址
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(9010));
        Assert.Empty(rig.Channels);
    }

    // ---- F08:游戏服地址文本两端的空白被容忍,空白或不是地址的文本被拒绝(地址文本在网关客户端里已被规整) ----

    [Theory]
    [InlineData("  {0}\r\n", true)]
    [InlineData("\n\t{0}   ", true)]
    [InlineData(" \t\r\n ", false)]
    [InlineData("{0}.9", false)]
    [InlineData("{0} extra", false)]
    public async Task Game_server_address_text_is_trimmed_and_blank_or_malformed_text_is_refused(string format, bool accepted)
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.GameServerIp = string.Format(format, GatewayTestSupport.Dots(127, 0, 0, 1));
        OnlineClientSession client = rig.CreateSession();

        OnlineLoginResult result = await Bounded(client.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password));

        Assert.Equal(OnlineStage.GameServerAddress, result.Stage);
        if (accepted)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
            Assert.True((await Bounded(client.ConnectAsync(9010))).IsSuccess);   // 取到的地址真能用来连频道
        }
        else
        {
            Assert.Equal(OnlineFailure.NoGameServerAddress, result.Failure);
            Assert.Equal(OnlineClientSessionState.Created, client.State);
        }
        client.Dispose();
    }

    // ---- F11:连接失败时,结果不得声称"角色列表已收到" ----

    [Theory]
    [InlineData("channel")]
    [InlineData("closed")]
    [InlineData("refused")]
    [InlineData("login-rejected")]
    [InlineData("login-silent")]
    [InlineData("bad-request")]
    public async Task A_failed_connect_never_claims_a_character_list(string kind)
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client;
        switch (kind)
        {
            case "channel":
                rig.FailChannel = _ => true;
                client = await rig.LoggedIn();
                break;
            case "closed":
                rig.BootstrapFactory = _ => null;
                rig.CloseBeforeBootstrap = _ => true;
                client = await rig.LoggedIn();
                break;
            case "refused":
                rig.BootstrapFactory = _ => DungeonEntryWire.Refusal();
                client = await rig.LoggedIn();
                break;
            case "login-rejected":
                rig.ScriptFactory = _ => Script(HostTestKit.Frames(DungeonEntryWire.Reply(1, new byte[] { 0, 0x17, 0, 0 })));
                client = await rig.LoggedIn();
                break;
            case "login-silent":
                rig.ScriptFactory = _ => Script(HostTestKit.Frames());
                client = await rig.LoggedIn();
                break;
            default:
                string account = new string('a', 25);
                string md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(OnlineFlowRig.Password))).ToLowerInvariant();
                rig.FakeGateway.Accounts[account] = new FakeGateway.Account { Md5 = md5, Qq = string.Empty };
                client = rig.CreateSession();
                Assert.True((await client.LoginAsync(account, OnlineFlowRig.Password)).IsSuccess);
                break;
        }

        OnlineConnectResult result = await Bounded(client.ConnectAsync(9010));

        Assert.False(result.IsSuccess);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, result.CharacterListStatus);
        Assert.False(result.CharacterListReceived);
        Assert.Null(result.CharacterList);
        Assert.Null(result.Channel);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        client.Dispose();
    }

    // ---- F14:只有包号 2 的通知才被当作角色列表;别的通知不论载荷长得多像都不算 ----

    [Fact]
    public async Task A_notice_with_another_packet_id_is_never_read_as_the_character_list()
    {
        var rig = new OnlineFlowRig();
        // 诱饵:载荷是一份合法的单条目列表,但包号是 900;它排在真列表(包号 2、三个条目)前面
        byte[] decoy = DungeonEntryWire.Notice(900, UserInfoTestData.Packet(UserInfoTestData.Standard(7)));
        rig.ScriptFactory = _ => Script(
            login: HostTestKit.Frames(DungeonEntryWire.Reply(1, HostTestKit.LoginSuccess()), decoy),
            list: HostTestKit.Frames(HostTestKit.DirectListNotice()));

        (OnlineClientSession client, OnlineConnectResult result) = await rig.Connected();

        Assert.Equal(OnlineCharacterListStatus.Received, result.CharacterListStatus);
        Assert.Equal(3, result.CharacterList.Entries.Count);       // 真列表是三个条目,诱饵只有一个
        Assert.Equal(61, result.CharacterList.Entries[0].Level);   // 条目 0 的等级是 61(诱饵里是 68)
        client.Dispose();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("malformed")]
    public async Task A_decoy_notice_alone_leaves_the_character_list_not_received(string kind)
    {
        var rig = new OnlineFlowRig();
        byte[] body = kind == "valid" ? UserInfoTestData.Packet(UserInfoTestData.Standard(7)) : new byte[] { 2, 1 };
        rig.ScriptFactory = _ => Script(
            login: HostTestKit.Frames(DungeonEntryWire.Reply(1, HostTestKit.LoginSuccess()), DungeonEntryWire.Notice(900, body)),
            list: HostTestKit.Frames());

        (OnlineClientSession client, OnlineConnectResult result) = await rig.Connected();

        Assert.Equal(OnlineCharacterListStatus.NotReceived, result.CharacterListStatus);
        Assert.False(result.CharacterListReceived);
        Assert.Null(result.CharacterList);
        client.Dispose();
    }

    // ---- F17:槽号的合法范围是 0 到 255(含两端),范围之外在发任何帧之前就拒绝 ----

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public async Task The_one_byte_slot_range_includes_both_ends(int slot)
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected();

        OnlineSelectCharacterResult result = null;
        Exception error = await Record.ExceptionAsync(async () => result = await Bounded(client.SelectCharacterAsync(slot)));

        Assert.Null(error);
        Assert.True(result.Entered);
        Assert.Equal(slot, result.Slot);
        Assert.Equal(slot, result.RequestedSlot);
        DungeonEntryClientFrame select = rig.Channels[0].Pipe.ClientFrames.Single(f => f.Class == PacketClass.Cmd && f.Id == 4);
        Assert.Equal((byte)slot, select.Plain[0]);
        client.Dispose();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Out_of_range_slots_are_refused_before_any_frame_is_sent(int slot)
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected();
        int writesBefore = rig.Channels[0].Recording.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SelectCharacterAsync(slot));

        Assert.Equal(writesBefore, rig.Channels[0].Recording.Writes.Count);
        Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 4);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
        Assert.True((await Bounded(client.SelectCharacterAsync(0))).Entered);       // 被拒之后连接照常可用
        client.Dispose();
    }

    // ---- R10:选角窗口最多收 64 帧;成功回包被挤到窗口之后时,要从尾窗口里把它找回来 ----

    [Fact]
    public async Task A_select_reply_pushed_past_the_64_frame_window_is_taken_from_the_tail_window()
    {
        var rig = new OnlineFlowRig();
        SelectCharacterSpec late = SelectCharacterSpec.Variant(5);
        byte[][] frames = Enumerable.Repeat(IdleNotice(), 63)                   // 窗口里的前 63 帧:无关通知
            .Concat(HostTestKit.EnterComplete())                                // 第 64 帧:进城完成,窗口到此为止
            .Append(DungeonEntryWire.Reply(4, late.Build()))                    // 第 65 帧:成功回包,落在尾窗口
            .ToArray();
        rig.ScriptFactory = _ => Script(select: _ => frames);

        (OnlineClientSession client, OnlineSelectCharacterResult selected) = await rig.Selected();

        Assert.True(selected.Entered);
        Assert.NotNull(selected.SelectCharacter);
        Assert.True(selected.SelectCharacter.IsSuccess);
        Assert.Equal(late.Village, selected.SelectCharacter.Load.Village);
        client.Dispose();
    }

    [Fact]
    public async Task A_select_reply_inside_the_window_wins_over_a_later_one_in_the_tail_window()
    {
        var rig = new OnlineFlowRig();
        SelectCharacterSpec inWindow = SelectCharacterSpec.Variant(3);
        SelectCharacterSpec late = SelectCharacterSpec.Variant(5);
        Assert.NotEqual(inWindow.Village, late.Village);
        byte[][] frames = new[] { DungeonEntryWire.Reply(4, inWindow.Build()) }  // 第 1 帧:窗口里的成功回包
            .Concat(Enumerable.Repeat(IdleNotice(), 63))                         // 第 2 到 64 帧:无关通知
            .Append(DungeonEntryWire.Reply(4, late.Build()))                     // 第 65 帧:尾窗口里的另一份回包
            .ToArray();
        rig.ScriptFactory = _ => Script(select: _ => frames);

        (OnlineClientSession client, OnlineSelectCharacterResult selected) = await rig.Selected();

        Assert.True(selected.Entered);
        Assert.Equal(inWindow.Village, selected.SelectCharacter.Load.Village);
        client.Dispose();
    }

    // ---- H05:一直有帧进来,空闲计时就一直被重置,不应报心跳超时 ----

    [Fact]
    public async Task Steady_inbound_traffic_keeps_resetting_the_idle_timer()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromMilliseconds(1000);
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: new OnlineInlineDispatcher());
        client.Disconnected += log.Add;
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        // 22 次、每次隔 100 毫秒推一帧与心跳无关的通知,总长约 2.2 秒,是超时窗口(1 秒)的两倍多:
        // 空闲计时每收到一帧就重置的话,一次超时也不该报;不重置的话,约 1 秒时就会报。
        for (int i = 0; i < 22; i++)
        {
            pipe.Push(IdleNotice());
            await Task.Delay(100);
            Assert.Empty(log.Reasons);
        }
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);

        // 帧停了之后,超时从"最后一帧"算起,不会立刻报,窗口走完才报
        Stopwatch quiet = Stopwatch.StartNew();
        await WaitUntil(() => log.Count > 0);
        Assert.True(quiet.Elapsed >= TimeSpan.FromMilliseconds(250), "超时应从最后一帧起算,实际 " + quiet.ElapsedMilliseconds + " 毫秒就报了");
        Assert.Equal(new[] { OnlineDisconnectReason.HeartbeatTimeout }, log.Reasons);
        client.Dispose();
    }

    // ---- H07:心跳回复写成功就是写成功,不能当作写失败报断线 ----

    [Fact]
    public async Task A_successful_heartbeat_reply_is_not_reported_as_a_write_failure()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: new OnlineInlineDispatcher());
        client.Disconnected += log.Add;
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        pipe.Push(HeartbeatRequest());
        await WaitUntil(() => HeartbeatReplies(pipe) == 1);
        await Task.Delay(200);
        Assert.Empty(log.Reasons);
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);

        // 泵还活着:第二次请求照样被回复,事件仍然没有
        pipe.Push(HeartbeatRequest());
        await WaitUntil(() => HeartbeatReplies(pipe) == 2);
        await Task.Delay(100);
        Assert.Empty(log.Reasons);
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);
        client.Dispose();
    }

    [Fact]
    public async Task A_failed_heartbeat_write_is_reported_exactly_once_as_a_write_error()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: new OnlineInlineDispatcher());
        client.Disconnected += log.Add;
        DungeonEntryPipe pipe = rig.Channels[0].Pipe;

        pipe.WriteFailure = new IOException("synthetic");
        pipe.Push(HeartbeatRequest());
        await WaitUntil(() => log.Count > 0);
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
        await Task.Delay(100);

        Assert.Equal(new[] { OnlineDisconnectReason.WriteError }, log.Reasons);
        client.Dispose();
    }

    // ---- D01:同一条链路的断线只报一次(阶段调用与心跳泵先后发现、泵重启后再发现,都不重复) ----

    [Fact]
    public async Task A_link_that_closed_before_the_pump_starts_is_reported_once_across_stage_and_pump()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = index => Script(select: _ =>
        {
            // 选角回包到达的同时服务端关了连接:阶段调用在选角结束时发现一次,随后启动的泵第一圈又会发现一次
            DungeonEntryPipe pipe = rig.Channels[index].Pipe;
            pipe.Push(HostTestKit.EnterComplete()[0]);
            pipe.CloseServer();
            return HostTestKit.Frames();
        });
        var dispatcher = new OnlineInlineDispatcher();
        var log = new OnlineDisconnectLog();
        OnlineClientSession client = await rig.LoggedIn(autoHeartbeat: true, dispatcher: dispatcher);
        client.Disconnected += log.Add;
        Assert.True((await Bounded(client.ConnectAsync(9010))).IsSuccess);

        OnlineSelectCharacterResult selected = await Bounded(client.SelectCharacterAsync(0));

        Assert.True(selected.Entered);
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);   // 泵已经跑完并退出
        await Task.Delay(100);
        Assert.Equal(new[] { OnlineDisconnectReason.PeerClosed }, log.Reasons);
        Assert.Equal(1, dispatcher.Calls);
        client.Dispose();
    }

    [Fact]
    public async Task Restarting_the_pump_on_a_dead_link_does_not_report_it_again()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var dispatcher = new OnlineInlineDispatcher();
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: dispatcher);
        client.Disconnected += log.Add;

        rig.Channels[0].Pipe.CloseServer();
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
        Assert.Equal(new[] { OnlineDisconnectReason.PeerClosed }, log.Reasons);

        // 手动重开心跳:链路还是那条已经通知过的死链路,泵再次发现对端已关,但不得再报
        await Bounded(client.SetHeartbeatAsync(true));
        await WaitUntil(() => client.HeartbeatState == OnlineHeartbeatState.Disabled);
        await Task.Delay(100);
        Assert.Equal(new[] { OnlineDisconnectReason.PeerClosed }, log.Reasons);
        Assert.Equal(1, dispatcher.Calls);
        client.Dispose();
    }

    // ---- D02:只有当前链路才报断线;换下来的旧链路再出什么事都不触发 ----

    [Fact]
    public async Task A_link_that_is_no_longer_current_never_raises_the_disconnect_event()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var dispatcher = new OnlineInlineDispatcher();
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: dispatcher);
        client.Disconnected += log.Add;
        OnlineChannelLink oldLink = rig.Channels[0].Link;

        Assert.True((await Bounded(client.SwitchChannelAsync(9020))).IsSuccess);
        Assert.Equal(2, rig.Channels.Count);
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        Assert.Empty(log.Reasons);                                  // 换频道本身(含旧泵的收摊)不产生任何事件
        OnlineChannelLink newLink = rig.Channels[1].Link;

        // 旧链路(已被换下并关闭)被报告出错:它不是当前链路,不得触发事件
        NotifyDisconnectedDirectly(client, oldLink, OnlineDisconnectReason.ReadError);
        Assert.Empty(log.Reasons);
        Assert.Equal(0, dispatcher.Calls);

        // 对照:同样的通知落在当前链路上就是恰好一次事件,第二次通知被吞掉
        NotifyDisconnectedDirectly(client, newLink, OnlineDisconnectReason.ReadError);
        NotifyDisconnectedDirectly(client, newLink, OnlineDisconnectReason.WriteError);
        Assert.Equal(new[] { OnlineDisconnectReason.ReadError }, log.Reasons);
        Assert.Equal(1, dispatcher.Calls);
        client.Dispose();
    }

    [Fact]
    public async Task The_old_links_late_close_after_a_reconnect_raises_no_event()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: new OnlineInlineDispatcher());
        client.Disconnected += log.Add;
        OnlineFlowChannel old = rig.Channels[0];

        await Bounded(client.DisconnectAsync());
        Assert.True((await Bounded(client.ConnectAsync(9011))).IsSuccess);
        Assert.True((await Bounded(client.SelectCharacterAsync(0))).Entered);       // 新链路上泵重新跑起来了

        old.Link.ReadFault = true;                                  // 旧链路"出错"并且服务端关了连接
        old.Pipe.CloseServer();
        await Task.Delay(300);

        Assert.Empty(log.Reasons);
        Assert.Equal(OnlineHeartbeatState.Running, client.HeartbeatState);
        client.Dispose();
    }

    // ---- D03:释放之后(包括释放进行到一半)不再触发断线事件 ----

    [Fact]
    public async Task A_failure_noticed_inside_the_dispose_window_raises_no_event()
    {
        var rig = new OnlineFlowRig();
        var dispatcher = new OnlineInlineDispatcher();
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: dispatcher);
        client.Disconnected += log.Add;
        OnlineFlowChannel channel = rig.Channels[0];
        OnlineDisconnectReason[] insideWindow = null;
        // 释放链路的那一刻(会话状态已经是 Disposed,但链路引用还没清空),让会话"发现"这条链路坏了
        channel.Connection.OnDispose = () =>
        {
            NotifyDisconnectedDirectly(client, channel.Link, OnlineDisconnectReason.ReadError);
            insideWindow = log.Reasons;
        };

        client.Dispose();

        Assert.NotNull(insideWindow);                               // 回调确实在释放过程中跑过
        Assert.Empty(insideWindow);
        Assert.Equal(1, channel.Connection.DisposeCount);
        Assert.Empty(log.Reasons);
        Assert.Equal(0, dispatcher.Calls);

        // 释放之后再通知同一条链路,同样没有事件
        NotifyDisconnectedDirectly(client, channel.Link, OnlineDisconnectReason.PeerClosed);
        Assert.Empty(log.Reasons);
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
    }

    [Fact]
    public async Task A_link_failure_after_dispose_raises_no_event_even_with_the_pump_running()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        var log = new OnlineDisconnectLog();
        (OnlineClientSession client, _) = await rig.Selected(autoHeartbeat: true, dispatcher: new OnlineInlineDispatcher());
        client.Disconnected += log.Add;
        OnlineFlowChannel channel = rig.Channels[0];

        client.Dispose();
        channel.Link.ReadFault = true;
        channel.Pipe.CloseServer();
        await Task.Delay(200);

        Assert.Empty(log.Reasons);
        Assert.Equal(1, channel.Connection.DisposeCount);
    }

    // ---- D04:释放会话时底层连接被关闭(恰好一次,重复释放不重复关) ----

    [Theory]
    [InlineData("connected")]
    [InlineData("selected")]
    [InlineData("heartbeating")]
    [InlineData("exclusive")]
    [InlineData("failed-bootstrap")]
    public async Task Dispose_closes_the_underlying_connection_exactly_once(string state)
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client;
        switch (state)
        {
            case "connected":
                (client, _) = await rig.Connected();
                break;
            case "selected":
                (client, _) = await rig.Selected();
                break;
            case "heartbeating":
                (client, _) = await rig.Selected(autoHeartbeat: true);
                break;
            case "exclusive":
                (client, _) = await rig.Selected();
                await client.EnterExclusiveReceiveAsync();
                break;
            default:
                rig.BootstrapFactory = _ => DungeonEntryWire.Refusal();
                client = await rig.LoggedIn();
                Assert.False((await Bounded(client.ConnectAsync(9010))).IsSuccess);
                break;
        }
        Assert.Equal(0, rig.Channels[0].Connection.DisposeCount);   // 释放之前连接是开着的

        client.Dispose();

        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        client.Dispose();                                           // 重复释放是空操作
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
        Assert.Null(client.GameSession);
    }

    // ---- R01:RunAsync 不启动心跳泵(调用方自己用连接读包,泵会抢读) ----

    [Fact]
    public async Task RunAsync_forces_the_heartbeat_pump_off_even_when_the_hooks_ask_for_it()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.AutoHeartbeat = true;                             // 钩子要求自动心跳
        OnlineEntryResult result = await Bounded(rig.CreateTownEntry().RunAsync());
        Assert.True(result.Entered);

        // RunAsync 必须把它压成关闭:调用方自己读包,不能让会话起一个泵去抢读
        Assert.False(rig.Hooks.AutoHeartbeat);

        // 行为侧:服务端发来的心跳请求没有被后台泵回复,而是原样留在会话里等调用方来读
        rig.Channels[0].Pipe.Push(HeartbeatRequest());
        await Task.Delay(150);
        Assert.Equal(0, HeartbeatReplies(rig.Channels[0].Pipe));
        List<DecodedFrame> mine = await Bounded(result.Session.ReceiveAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Contains(mine, f => f.Header.Class == PacketClass.Noti && f.Header.PacketId == 0);
    }

    // ---- R03:所有槽都选不进去时,最终结果带回最后一次失败槽的选角结果 ----

    [Fact]
    public async Task RunAsync_when_every_slot_fails_returns_the_last_slots_failure()
    {
        var rig = new OnlineFlowRig();
        rig.Settings.MaxSlots = 3;
        byte[] codes = { 0x13, 4, 0x9f };                           // 每个槽的失败码都不同:区分"最后一个"与"第一个"
        rig.ScriptFactory = _ => Script(select: slot => HostTestKit.Frames(DungeonEntryWire.Reply(4, SelectCharacterSpec.Failure(codes[slot]))));

        OnlineEntryResult result = await Bounded(rig.CreateTownEntry().RunAsync());

        Assert.False(result.Entered);
        Assert.Equal(OnlineStage.SelectSlot, result.Stage);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.NotNull(result.SelectCharacter);
        Assert.False(result.SelectCharacter.IsSuccess);
        Assert.Equal(codes[2], result.SelectCharacter.FailureCode);
        Assert.Equal(new byte[] { 0, 1, 2 }, rig.Channels[0].Pipe.ClientFrames.Where(f => f.Id == 4).Select(f => f.Plain[0]).ToArray());
    }

    [Fact]
    public async Task RunAsync_keeps_the_last_explicit_failure_when_later_slots_stay_silent()
    {
        var rig = new OnlineFlowRig();
        rig.Settings.MaxSlots = 3;
        rig.ScriptFactory = _ => Script(select: slot => slot == 0
            ? HostTestKit.Frames(DungeonEntryWire.Reply(4, SelectCharacterSpec.Failure(4)))
            : HostTestKit.Frames());

        OnlineEntryResult result = await Bounded(rig.CreateTownEntry().RunAsync());

        Assert.False(result.Entered);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.NotNull(result.SelectCharacter);
        Assert.Equal((byte)4, result.SelectCharacter.FailureCode);
        Assert.Equal(SelectCharacterFailure.SlotOutOfRange, result.SelectCharacter.Failure);
    }

    // ---- R05:RunAsync 中途被取消或出错时,创建的会话与连接被释放,异常原样抛出 ----

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("select")]
    public async Task RunAsync_cancelled_mid_flight_releases_the_connection_and_throws(string phase)
    {
        var rig = new OnlineFlowRig();
        if (phase == "bootstrap")
        {
            rig.BootstrapFactory = _ => null;                       // 启动包永远不来:卡在等启动包
        }
        else
        {
            // 选角窗口拉长到 20 秒(登录、列表窗口保持很短),选角命令发出后服务端一直沉默:卡在等选角回包,只有取消能结束它
            rig.Hooks.Windows = new OnlineTownWindows(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(30));
            rig.ScriptFactory = _ => Script(select: _ => HostTestKit.Frames());
        }
        using var stop = new CancellationTokenSource();
        Task<OnlineEntryResult> run = rig.CreateTownEntry().RunAsync(cancellationToken: stop.Token);

        if (phase == "bootstrap")
        {
            await WaitUntil(() => rig.Channels.Count == 1);
        }
        else
        {
            await WaitUntil(() => rig.Channels.Count == 1 && rig.Channels[0].Pipe.ClientFrames.Any(f => f.Class == PacketClass.Cmd && f.Id == 4));
        }
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(run));
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
    }

    [Fact]
    public async Task A_select_send_failure_surfaces_as_an_io_error_and_RunAsync_releases_the_connection()
    {
        var rig = new OnlineFlowRig();
        Func<DungeonEntryClientFrame, IEnumerable<byte[]>> inner = Script();
        rig.ScriptFactory = index => frame =>
        {
            IEnumerable<byte[]> replies = inner(frame);
            if (frame.Class == PacketClass.Cmd && frame.Id == 8)
            {
                // 角色列表请求处理完之后连接"坏了":下一次写(选角命令)会失败
                rig.Channels[index].Pipe.WriteFailure = new IOException("synthetic write failure");
            }
            return replies;
        };

        IOException error = await Assert.ThrowsAsync<IOException>(() => Bounded(rig.CreateTownEntry().RunAsync()));

        Assert.Equal("synthetic write failure", error.Message);
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 4);   // 选角命令没有写出去
        Assert.Single(rig.Channels);                                                // 也没有换连接再试
    }
}
