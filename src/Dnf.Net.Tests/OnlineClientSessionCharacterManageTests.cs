using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

public sealed class OnlineClientSessionCharacterManageTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static byte[] ValidCreateName(string text = "Hero900") => Encoding.UTF8.GetBytes(text);

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script(
        byte[][] list = null,
        Func<byte, byte[], byte, byte[][]> onCreate = null,
        Func<byte, byte[], byte[][]> onDelete = null)
    {
        return HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            list ?? HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            frame =>
            {
                if (frame.Id == 5 && onCreate != null
                    && CharacterManagePayloads.TryParseCreateCharacterRequest(frame.Plain, out CreateCharacterRequest req))
                {
                    return onCreate(req.Job, req.CopyNameBytes(), req.EventFlag);
                }
                if (frame.Id == 6 && onDelete != null
                    && CharacterManagePayloads.TryParseDeleteCharacterRequest(frame.Plain, out DeleteCharacterRequest delReq))
                {
                    return onDelete(delReq.SlotIndex, delReq.CopyNameBytes());
                }
                return null;
            });
    }

    // ------------------------------------------------------------------ 默认关闭安全边界

    [Fact]
    public async Task AllowCharacterManageWrites_defaults_to_false_and_sends_zero_bytes()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            onCreate: (_, _, _) => HostTestKit.Frames(DungeonEntryWire.Reply(5, new byte[] { 1 })),
            onDelete: (_, _) => HostTestKit.Frames(DungeonEntryWire.Reply(6, new byte[] { 1, 0 })));
        (OnlineClientSession client, _) = await rig.Connected();
        Assert.False(client.AllowCharacterManageWrites);

        int writesBefore = rig.Channels[0].Recording.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateCharacterAsync(0, ValidCreateName()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteCharacterAsync(0, ValidCreateName()));

        Assert.Equal(writesBefore, rig.Channels[0].Recording.Writes.Count);
        Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5 || f.Id == 6);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    // ------------------------------------------------------------------ 状态机与参数校验

    [Theory]
    [InlineData("created")]
    [InlineData("logged_in")]
    [InlineData("selected")]
    [InlineData("exclusive")]
    public async Task Create_and_delete_reject_non_connected_states_with_zero_writes(string stateName)
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client;
        switch (stateName)
        {
            case "created":
                client = rig.CreateSession();
                break;
            case "logged_in":
                client = await rig.LoggedIn();
                break;
            case "selected":
                (client, _) = await rig.Selected();
                break;
            default:
                (client, _) = await rig.Selected();
                await client.EnterExclusiveReceiveAsync();
                break;
        }
        client.AllowCharacterManageWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateCharacterAsync(0, ValidCreateName()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteCharacterAsync(0, ValidCreateName()));
        if (rig.Channels.Count > 0)
        {
            Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5 || f.Id == 6);
        }
    }

    [Fact]
    public async Task Create_and_delete_after_dispose_throw_object_disposed()
    {
        var rig = new OnlineFlowRig();
        OnlineClientSession client = rig.CreateSession();
        client.AllowCharacterManageWrites = true;
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.CreateCharacterAsync(0, ValidCreateName()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DeleteCharacterAsync(0, ValidCreateName()));
    }

    [Fact]
    public async Task Input_validation_rejects_invalid_arguments_before_sending()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        int writesBefore = rig.Channels[0].Recording.Writes.Count;

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CreateCharacterAsync(0, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateCharacterAsync(11, ValidCreateName()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateCharacterAsync(0, ValidCreateName(), 128));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateCharacterAsync(0, Encoding.ASCII.GetBytes("abc")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateCharacterAsync(0, new byte[19]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateCharacterAsync(0, Encoding.UTF8.GetBytes("ab c")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateCharacterAsync(0, new byte[] { 0xF0, 0x9F, 0x98, 0x80 }));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.DeleteCharacterAsync(0, null));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteCharacterAsync(0, Array.Empty<byte>()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteCharacterAsync(0, Encoding.ASCII.GetBytes(new string('a', 30))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteCharacterAsync(0, new byte[] { 0x61, 0x00, 0x62 }));

        Assert.Equal(writesBefore, rig.Channels[0].Recording.Writes.Count);
    }

    // ------------------------------------------------------------------ 创建/删除成功与名册刷新

    [Fact]
    public async Task Create_success_sends_once_refreshes_character_list_and_replaces_early_frames()
    {
        UserInfoTestData.EntrySpec createdSpec = UserInfoTestData.Standard(3, nameLength: 7, equipCount: 0);
        createdSpec.Uid = 903;
        createdSpec.Name = ValidCreateName("Hero903");
        createdSpec.Job = 7;
        createdSpec.Level = 1;
        createdSpec.GrowType = 0x12;
        createdSpec.D2 = 0;

        byte[] refreshedPacket = UserInfoTestData.Packet(
            UserInfoTestData.Standard(0),
            UserInfoTestData.Standard(1),
            UserInfoTestData.Standard(2),
            createdSpec);

        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            onCreate: (job, nameBytes, eventFlag) => HostTestKit.Frames(
                DungeonEntryWire.Reply(5, new byte[] { 1 }),
                HostTestKit.DirectListNotice(refreshedPacket)));

        (OnlineClientSession client, OnlineConnectResult connected) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        Assert.Equal(3, connected.CharacterList.Entries.Count);
        Assert.Equal(3, client.CharacterList.Entries.Count);

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(7, ValidCreateName("Hero903"), 0));

        Assert.True(res.IsSuccess);
        Assert.True(res.CommandSucceeded);
        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.Received, res.CharacterListStatus);
        Assert.Equal(4, res.CharacterList.Entries.Count);
        Assert.Equal((byte)7, res.CharacterList.Entries[3].Job);
        Assert.Same(res.CharacterList, client.CharacterList);
        Assert.Single(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);

        // 随后选新角色 3 号槽位：必须使用替换后的 _earlyFrames（不能用旧名册）
        OnlineSelectCharacterResult selected = await Bounded(client.SelectCharacterAsync(3));
        Assert.True(selected.Entered);
        Assert.Equal(1, selected.Snapshot.Level);
        Assert.Equal(7, selected.Snapshot.Job);
        Assert.Equal(2, selected.Snapshot.GrowType);
    }

    [Fact]
    public async Task Create_handles_noti2_inside_561_and_noti2_arriving_before_cmd5()
    {
        UserInfoTestData.EntrySpec fourth = UserInfoTestData.Standard(3);
        fourth.Job = 9;
        fourth.D2 = 0;
        byte[] refreshedPacket = UserInfoTestData.Packet(UserInfoTestData.Standard(0), fourth);
        byte[] envelopedRoster = HostTestKit.RealEnvelope(
            (byte)PacketClass.Noti,
            2,
            UserInfoTestData.Zlib(refreshedPacket));
        byte[] nonMode2 = DungeonEntryWire.Notice(2, new byte[] { 1, 9, 9 });

        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            onCreate: (_, _, _) => HostTestKit.Frames(
                nonMode2,
                envelopedRoster,
                DungeonEntryWire.Reply(5, new byte[] { 1 })));

        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(9, ValidCreateName("Hero909"), 127));

        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.CharacterList.Entries.Count);
        Assert.Equal((byte)9, res.CharacterList.Entries[1].Job);
    }

    [Fact]
    public async Task Delete_success_refreshes_character_list_and_preserves_event_cleared_flag()
    {
        byte[] afterDelete = UserInfoTestData.Packet(UserInfoTestData.Standard(0), UserInfoTestData.Standard(2));
        var rig = new OnlineFlowRig();
        byte seenSlot = 255;
        byte[] seenConfirm = null;
        rig.ScriptFactory = _ => Script(
            onDelete: (slot, confirm) =>
            {
                seenSlot = slot;
                seenConfirm = confirm;
                return HostTestKit.Frames(
                    DungeonEntryWire.Reply(6, new byte[] { 1, 1 }),
                    HostTestKit.DirectListNotice(afterDelete));
            });

        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        byte[] confirmBytes = Encoding.UTF8.GetBytes("角色B");
        OnlineCharacterManageResult res = await Bounded(client.DeleteCharacterAsync(1, confirmBytes));

        Assert.True(res.IsSuccess);
        Assert.Equal((byte)1, seenSlot);
        Assert.Equal(confirmBytes, seenConfirm);
        Assert.True(res.DeleteResult.IsEventCharacterFlagCleared);
        Assert.Equal(2, res.CharacterList.Entries.Count);
        Assert.Equal(2, client.CharacterList.Entries.Count);
        Assert.Single(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 6);

        // 旧名册的 2 号槽已从 _earlyFrames 移除，进城快照不能再读到被删槽位的信息
        OnlineSelectCharacterResult staleSlotCheck = await Bounded(client.SelectCharacterAsync(2));
        Assert.True(staleSlotCheck.Entered);
        Assert.Null(staleSlotCheck.Snapshot.Level);
    }

    // ------------------------------------------------------------------ 全部服务端失败码与未知码

    [Theory]
    [InlineData(0x02, CharacterCreateFailure.DatabaseOrUnknownError)]
    [InlineData(0x04, CharacterCreateFailure.SlotLimitReached)]
    [InlineData(0x05, CharacterCreateFailure.ClientRestricted)]
    [InlineData(0x07, CharacterCreateFailure.CreationRestrictedEmptyAccount)]
    [InlineData(0x08, CharacterCreateFailure.CreationRestrictedByEvent)]
    [InlineData(0x12, CharacterCreateFailure.DuplicateActiveName)]
    [InlineData(0x13, CharacterCreateFailure.StateLocked)]
    [InlineData(0x14, CharacterCreateFailure.EventCharacterConditionFailed)]
    [InlineData(0x16, CharacterCreateFailure.ClientSilentIgnored)]
    [InlineData(0x17, CharacterCreateFailure.CreationBlocked)]
    [InlineData(0x18, CharacterCreateFailure.DuplicateDeletedName)]
    [InlineData(0x5f, CharacterCreateFailure.DailyCreationLimitReached)]
    [InlineData(0x60, CharacterCreateFailure.ChannelNotAllowed)]
    [InlineData(0x9f, CharacterCreateFailure.InvalidName)]
    [InlineData(0xd1, CharacterCreateFailure.ServiceRestricted)]
    [InlineData(0xab, CharacterCreateFailure.Unknown)]
    public async Task Create_rejection_maps_all_known_codes_and_preserves_unknown_code(byte rawCode, CharacterCreateFailure expected)
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            onCreate: (_, _, _) => HostTestKit.Frames(DungeonEntryWire.Reply(5, new byte[] { 0, rawCode })));
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        UserInfoNotice beforeList = client.CharacterList;

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(0, ValidCreateName()));

        Assert.False(res.IsSuccess);
        Assert.False(res.CommandSucceeded);
        Assert.Equal(OnlineCharacterManageOutcomeKind.Rejected, res.Outcome);
        Assert.Equal(expected, res.CreateResult.Failure);
        Assert.Equal(rawCode, res.RawFailureCode);
        Assert.Same(beforeList, client.CharacterList);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    [Theory]
    [InlineData(0x02, CharacterDeleteFailure.DatabaseOrUnknownError)]
    [InlineData(0x13, CharacterDeleteFailure.StateLocked)]
    [InlineData(0x15, CharacterDeleteFailure.EmptySlot)]
    [InlineData(0x16, CharacterDeleteFailure.CooldownOrActiveCharacter)]
    [InlineData(0x24, CharacterDeleteFailure.GuildMasterCannotDelete)]
    [InlineData(0x28, CharacterDeleteFailure.ClientDeletionBlocked)]
    [InlineData(0x72, CharacterDeleteFailure.TradePunished)]
    [InlineData(0xcf, CharacterDeleteFailure.RestingUserRestricted)]
    [InlineData(0xd1, CharacterDeleteFailure.ServiceRestricted)]
    [InlineData(0xd5, CharacterDeleteFailure.ItemLockActive)]
    [InlineData(0xe4, CharacterDeleteFailure.TradePunishRestricted)]
    [InlineData(0xe5, CharacterDeleteFailure.RestorePunishRestricted)]
    [InlineData(0xe9, CharacterDeleteFailure.SecurityServiceRequired)]
    [InlineData(0x4d, CharacterDeleteFailure.SharedSecurityRestriction)]
    [InlineData(0xee, CharacterDeleteFailure.SharedSecurityRestriction)]
    [InlineData(0xbc, CharacterDeleteFailure.Unknown)]
    public async Task Delete_rejection_maps_all_known_codes_and_preserves_unknown_code(byte rawCode, CharacterDeleteFailure expected)
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            onDelete: (_, _) => HostTestKit.Frames(DungeonEntryWire.Reply(6, new byte[] { 0, rawCode })));
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        OnlineCharacterManageResult res = await Bounded(client.DeleteCharacterAsync(0, ValidCreateName()));

        Assert.False(res.IsSuccess);
        Assert.False(res.CommandSucceeded);
        Assert.Equal(OnlineCharacterManageOutcomeKind.Rejected, res.Outcome);
        Assert.Equal(expected, res.DeleteResult.Failure);
        Assert.Equal(rawCode, res.RawFailureCode);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    // ------------------------------------------------------------------ 超时、缺包、取消、断线

    [Fact]
    public async Task Create_without_reply_returns_no_reply_and_does_not_retry()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(onCreate: (_, _, _) => HostTestKit.Frames());
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(0, ValidCreateName()));

        Assert.Equal(OnlineCharacterManageOutcomeKind.NoReply, res.Outcome);
        Assert.Single(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5);
    }

    [Fact]
    public async Task Create_and_delete_with_malformed_reply_return_malformed_reply()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(
            onCreate: (_, _, _) => HostTestKit.Frames(HostTestKit.RealEnvelope((byte)PacketClass.Cmd, 5, Array.Empty<byte>())),
            onDelete: (_, _) => HostTestKit.Frames(HostTestKit.RealEnvelope((byte)PacketClass.Cmd, 6, new byte[] { 0 })));
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        OnlineCharacterManageResult resCreate = await Bounded(client.CreateCharacterAsync(0, ValidCreateName()));
        Assert.Equal(OnlineCharacterManageOutcomeKind.MalformedReply, resCreate.Outcome);

        OnlineCharacterManageResult resDelete = await Bounded(client.DeleteCharacterAsync(0, ValidCreateName()));
        Assert.Equal(OnlineCharacterManageOutcomeKind.MalformedReply, resDelete.Outcome);
    }

    [Fact]
    public async Task Create_success_without_roster_push_reports_roster_missing()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(onCreate: (_, _, _) => HostTestKit.Frames(DungeonEntryWire.Reply(5, new byte[] { 1 })));
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(0, ValidCreateName()));

        Assert.False(res.IsSuccess);
        Assert.True(res.CommandSucceeded);
        Assert.Equal(OnlineCharacterManageOutcomeKind.RosterMissing, res.Outcome);
    }

    [Fact]
    public async Task Create_success_with_malformed_roster_reports_roster_malformed()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(onCreate: (_, _, _) => HostTestKit.Frames(
            DungeonEntryWire.Reply(5, new byte[] { 1 }),
            HostTestKit.DirectListNotice(new byte[] { 2, 1 })));
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(0, ValidCreateName()));

        Assert.False(res.IsSuccess);
        Assert.True(res.CommandSucceeded);
        Assert.Equal(OnlineCharacterManageOutcomeKind.RosterMalformed, res.Outcome);
    }

    [Fact]
    public async Task Pre_cancelled_create_sends_no_frame_and_keeps_connected()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateCharacterAsync(0, ValidCreateName(), 0, stop.Token));
        Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    [Fact]
    public async Task Mid_flight_cancellation_closes_link_and_resets_state_to_logged_in()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = new OnlineTownWindows(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(30));
        rig.ScriptFactory = _ => Script(onCreate: (_, _, _) => HostTestKit.Frames());
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        using var stop = new CancellationTokenSource(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateCharacterAsync(0, ValidCreateName(), 0, stop.Token));

        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Null(client.GameSession);
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
    }

    [Fact]
    public async Task Mid_flight_peer_close_resets_to_logged_in_and_notifies_disconnect()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(onDelete: (_, _) =>
        {
            rig.Channels[0].Pipe.CloseServer();
            return HostTestKit.Frames();
        });
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        var observed = new TaskCompletionSource<OnlineDisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => observed.TrySetResult(reason);

        OnlineCharacterManageResult res = await Bounded(client.DeleteCharacterAsync(0, ValidCreateName()));

        Assert.True(res.IsDisconnected);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        Assert.Equal(OnlineDisconnectReason.PeerClosed, await Bounded(observed.Task));
    }

    [Fact]
    public async Task Write_error_on_send_resets_to_logged_in_and_notifies_write_error()
    {
        var rig = new OnlineFlowRig();
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        var observed = new TaskCompletionSource<OnlineDisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => observed.TrySetResult(reason);
        rig.Channels[0].Pipe.WriteFailure = new IOException("synthetic-write-fail");

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(0, ValidCreateName()));

        Assert.True(res.IsDisconnected);
        Assert.Equal(OnlineClientSessionState.LoggedIn, client.State);
        Assert.Equal(1, rig.Channels[0].Connection.DisposeCount);
        Assert.Equal(OnlineDisconnectReason.WriteError, await Bounded(observed.Task));
    }

    [Fact]
    public void OutcomeKind_enum_values_are_distinct()
    {
        int[] values = Enum.GetValues(typeof(OnlineCharacterManageOutcomeKind))
            .Cast<OnlineCharacterManageOutcomeKind>()
            .Select(v => (int)v)
            .ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public async Task Rejected_reply_returns_immediately_without_waiting_for_second_frame()
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = new OnlineTownWindows(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(50));
        rig.ScriptFactory = _ => Script(onCreate: (_, _, _) => HostTestKit.Frames(
            DungeonEntryWire.Reply(5, new byte[] { 0, 2 })));
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(0, ValidCreateName()));
        sw.Stop();

        Assert.Equal(OnlineCharacterManageOutcomeKind.Rejected, res.Outcome);
        Assert.Equal(CharacterCreateFailure.DatabaseOrUnknownError, res.CreateResult.Failure);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected fast Rejected return, took {sw.ElapsedMilliseconds}ms");
    }
}
