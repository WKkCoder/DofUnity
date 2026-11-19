using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网城镇控制器(<see cref="OnlineTownController"/>)的逐项测试,跑在内存假后端与真宿主(外壳阶段机)上:
/// 同步开关矩阵(自定义传输、宿主手里的真会话、两者都没有;<c>areaSyncEnabled</c> 显式开关;默认值)、
/// 选角快照逐字段核对与副本许可条目数、断线日志(只写一次、不重复、退出过程中不写)、
/// 回选角与退出游戏的一次性与互斥、释放之后的空操作,以及每帧记录安全落点是否真的转给世界包装层。
/// 触发"走进出口发 1/38"的路径不在这里断言坐标。日志只看脱敏后的固定格式行。
/// </summary>
public sealed class OnlineTownControllerTests
{
    private const string EnterNoSnapshotOnline =
        "DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=on perms=none snap=none slot=na level=na job=na grow=na";

    private static OnlineTownController Create(TownHostRig rig, ITownCommandTransport custom = null, Action<string> log = null)
    {
        return OnlineTownController.Create(rig.Host, OnlineTownHostTests.BuildAtlas(), customTransport: custom, log: log ?? rig.Log);
    }

    private static TownHostRig EnteredRig(Func<int, OnlineSelectReport> select = null, bool realSession = false, int slot = 0)
    {
        var rig = new TownHostRig(select, realSession);
        rig.EnterInGame(slot);
        return rig;
    }

    private static string[] Matching(TownHostRig rig, string needle)
    {
        return rig.TownLines().Where(l => l.Contains(needle)).ToArray();
    }

    private static OnlineSelectReport Success(TownEntrySnapshot snapshot = null, DungeonPermissionSnapshot permissions = null)
    {
        return new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 1), snapshot, permissions);
    }

    // ------------------------------------------------------------------ 同步矩阵

    [Fact]
    public void A_custom_transport_counts_as_a_real_transport_and_area_sync_defaults_to_on()
    {
        using TownHostRig rig = EnteredRig();
        Assert.Null(rig.Host.Session);

        using OnlineTownController controller = Create(rig, new OnlineTownHostTests.RecordingTransport());

        Assert.True(controller.Profile.IsSyncedSession);
        Assert.True(controller.Coordinator.AreaSyncEnabled);
        Assert.Equal(new[] { EnterNoSnapshotOnline }, rig.TownLines());
    }

    [Fact]
    public void The_hosts_real_session_counts_as_a_real_transport_and_is_wrapped_in_the_session_transport()
    {
        using TownHostRig rig = EnteredRig(realSession: true);
        OnlineClientSession session = rig.Host.Session;
        Assert.NotNull(session);
        Assert.Same(rig.Port(0).Real, session);
        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));

        OnlineTownController controller = Create(rig);

        Assert.True(controller.Profile.IsSyncedSession);
        Assert.True(controller.Coordinator.AreaSyncEnabled);
        Assert.Equal(new[] { EnterNoSnapshotOnline }, rig.TownLines());
        // 控制器在真会话上挂了城镇传输:两个事件各一个订阅者,释放之后成对退订
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));

        controller.Dispose();

        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
    }

    [Fact]
    public void Without_a_custom_transport_or_a_real_session_the_profile_is_offline_and_area_sync_is_off()
    {
        using TownHostRig rig = EnteredRig();
        Assert.Null(rig.Host.Session);

        using OnlineTownController controller = Create(rig);

        Assert.False(controller.Profile.IsSyncedSession);
        Assert.False(controller.Coordinator.AreaSyncEnabled);
        Assert.Equal(
            new[]
            {
                "DNF_ONLINE stage=town event=enter mode=offline unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=off perms=none snap=none slot=na level=na job=na grow=na",
            },
            rig.TownLines());
    }

    [Fact]
    public void Asking_for_area_sync_without_any_real_transport_still_leaves_it_off()
    {
        using TownHostRig rig = EnteredRig();

        using OnlineTownController controller = OnlineTownController.Create(
            rig.Host, OnlineTownHostTests.BuildAtlas(), areaSyncEnabled: true, log: rig.Log);

        Assert.False(controller.Profile.IsSyncedSession);
        Assert.False(controller.Coordinator.AreaSyncEnabled);
        Assert.Contains(" mode=offline ", rig.TownLines()[0]);
        Assert.Contains(" areasync=off ", rig.TownLines()[0]);
    }

    [Fact]
    public void Turning_area_sync_off_wins_over_a_custom_transport()
    {
        using TownHostRig rig = EnteredRig();

        using OnlineTownController controller = OnlineTownController.Create(
            rig.Host, OnlineTownHostTests.BuildAtlas(), areaSyncEnabled: false, log: rig.Log, customTransport: new OnlineTownHostTests.RecordingTransport());

        Assert.True(controller.Profile.IsSyncedSession);
        Assert.False(controller.Coordinator.AreaSyncEnabled);
        Assert.Contains(" mode=online ", rig.TownLines()[0]);
        Assert.Contains(" areasync=off ", rig.TownLines()[0]);
    }

    [Fact]
    public void Turning_area_sync_off_wins_over_the_hosts_real_session()
    {
        using TownHostRig rig = EnteredRig(realSession: true);

        using OnlineTownController controller = OnlineTownController.Create(
            rig.Host, OnlineTownHostTests.BuildAtlas(), areaSyncEnabled: false, log: rig.Log);

        Assert.True(controller.Profile.IsSyncedSession);
        Assert.False(controller.Coordinator.AreaSyncEnabled);
        Assert.Contains(" mode=online ", rig.TownLines()[0]);
        Assert.Contains(" areasync=off ", rig.TownLines()[0]);
    }

    [Fact]
    public void Explicit_area_sync_on_with_a_transport_is_on()
    {
        using TownHostRig rig = EnteredRig();

        using OnlineTownController controller = OnlineTownController.Create(
            rig.Host, OnlineTownHostTests.BuildAtlas(), areaSyncEnabled: true, log: rig.Log, customTransport: new OnlineTownHostTests.RecordingTransport());

        Assert.True(controller.Coordinator.AreaSyncEnabled);
        Assert.Contains(" areasync=on ", rig.TownLines()[0]);
    }

    [Fact]
    public void Create_validates_its_arguments_and_needs_a_selected_character()
    {
        using TownHostRig rig = EnteredRig();
        TownAtlas atlas = OnlineTownHostTests.BuildAtlas();

        Assert.Equal("host", Assert.Throws<ArgumentNullException>(() => OnlineTownController.Create(null, atlas)).ParamName);
        Assert.Equal("atlas", Assert.Throws<ArgumentNullException>(() => OnlineTownController.Create(rig.Host, null)).ParamName);

        using var loginRig = new TownHostRig();
        Assert.Throws<InvalidOperationException>(() => OnlineTownController.Create(loginRig.Host, atlas));
    }

    [Fact]
    public void The_start_town_follows_the_server_village_and_falls_back_when_the_atlas_has_no_such_town()
    {
        using TownHostRig fiveRig = EnteredRig(select: slot => new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 5), null, null));
        using OnlineTownController five = Create(fiveRig);
        Assert.Equal(5, five.StartTown);
        Assert.Contains(" townfallback=false ", fiveRig.TownLines()[0]);

        using TownHostRig missingRig = EnteredRig(select: slot => new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 77), null, null));
        using OnlineTownController missing = Create(missingRig);
        Assert.Equal(1, missing.StartTown);
        Assert.Contains(" townfallback=true ", missingRig.TownLines()[0]);
    }

    [Fact]
    public void Without_a_select_payload_the_profile_uses_the_entry_id_and_the_start_town_falls_back()
    {
        using TownHostRig rig = EnteredRig(select: slot => new OnlineSelectReport(true, false, null));
        Assert.Null(rig.Host.SelectCharacter);

        using OnlineTownController controller = Create(rig);

        // 槽 0 的角色编号是 9101,没有选角载荷时服务端唯一号取它的低 16 位;没有村号就退回默认城镇并如实标出
        Assert.Equal((ushort)9101, controller.Profile.ServerUniqueId);
        Assert.Null(controller.Profile.ServerVillage);
        Assert.Equal(1, controller.StartTown);
        Assert.Contains(" townfallback=true ", rig.TownLines()[0]);
    }

    [Fact]
    public void The_command_timeout_reaches_the_coordinator_and_a_missing_one_means_five_seconds()
    {
        using TownHostRig rig = EnteredRig();
        TownAtlas atlas = OnlineTownHostTests.BuildAtlas();

        using OnlineTownController custom = OnlineTownController.Create(
            rig.Host, atlas, log: rig.Log, customTransport: new OnlineTownHostTests.RecordingTransport(), commandTimeoutMs: 1234);
        using OnlineTownController byDefault = OnlineTownController.Create(
            rig.Host, atlas, log: rig.Log, customTransport: new OnlineTownHostTests.RecordingTransport());

        Assert.Equal(1234L, custom.Coordinator.TimeoutMs);
        Assert.Equal(5000L, byDefault.Coordinator.TimeoutMs);
    }

    [Fact]
    public void The_titles_provider_names_the_class_in_the_profile_and_no_log_sink_is_fine()
    {
        using TownHostRig rig = EnteredRig();

        // 不给日志去处:进城日志被省略,不能因此抛异常
        using OnlineTownController controller = OnlineTownController.Create(
            rig.Host, OnlineTownHostTests.BuildAtlas(), titles: new FixedTitles(), customTransport: new OnlineTownHostTests.RecordingTransport());

        // 槽 0:职业 0、成长类型字节 0x12 → 成长类型 2、觉醒 1
        Assert.Equal("合成职业0-2-1", controller.Profile.ClassName);
        Assert.Equal("测试角色零", controller.Profile.Name);
        Assert.Empty(rig.TownLines());
    }

    [Fact]
    public void Without_a_wrapped_world_there_is_no_world_prompt_or_pending_area_change_and_afterwards_they_follow_it()
    {
        using TownHostRig rig = EnteredRig();
        using OnlineTownController controller = Create(rig, new OnlineTownHostTests.RecordingTransport());

        Assert.Null(controller.World);
        Assert.False(controller.IsAreaChangePending);
        Assert.Null(controller.CurrentPrompt);

        ITownWorld wrapped = controller.WrapWorld(new ProbeWorld(new ProbeSession()));

        Assert.Same(wrapped, controller.World);
        Assert.False(controller.IsAreaChangePending);
        Assert.Null(controller.CurrentPrompt);
    }

    // ------------------------------------------------------------------ 快照与许可

    [Fact]
    public void A_snapshot_that_agrees_with_the_selected_character_reports_four_matches_and_the_permission_count()
    {
        TownEntrySnapshot snapshot = NetObjects.Snapshot(slot: 0, level: 55, job: 0, growType: 2);
        DungeonPermissionSnapshot permissions = NetObjects.Permissions((901, 1), (902, 2), (903, 3));
        using TownHostRig rig = EnteredRig(select: slot => Success(snapshot, permissions));

        using OnlineTownController controller = Create(rig, new OnlineTownHostTests.RecordingTransport());

        OnlineTownSnapshotCheck check = controller.Profile.SnapshotCheck;
        Assert.True(check.HasSnapshot);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.SlotMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.LevelMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.JobMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.GrowTypeMatch);
        Assert.False(check.HasMismatch);
        Assert.Equal(3, controller.Profile.DungeonPermissionCount);
        Assert.Equal(
            new[]
            {
                "DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=on perms=present snap=present slot=match level=match job=match grow=match",
            },
            rig.TownLines());
    }

    [Theory]
    [InlineData(1, 55, 0, 2, false, true, true, true)]
    [InlineData(0, 56, 0, 2, true, false, true, true)]
    [InlineData(0, 55, 1, 2, true, true, false, true)]
    [InlineData(0, 55, 0, 3, true, true, true, false)]
    [InlineData(1, 56, 1, 3, false, false, false, false)]
    public void A_snapshot_that_disagrees_flags_exactly_the_fields_that_differ(
        int snapSlot, int snapLevel, int snapJob, int snapGrow, bool slotOk, bool levelOk, bool jobOk, bool growOk)
    {
        // 选中的是槽 0:等级 55、职业 0、成长类型 2(成长类型字节 0x12 的低半字节)
        TownEntrySnapshot snapshot = NetObjects.Snapshot(snapSlot, snapLevel, snapJob, snapGrow);
        using TownHostRig rig = EnteredRig(select: slot => Success(snapshot));

        using OnlineTownController controller = Create(rig);

        OnlineTownSnapshotCheck check = controller.Profile.SnapshotCheck;
        Assert.True(check.HasSnapshot);
        Assert.Equal(slotOk ? OnlineSnapshotMatch.Matched : OnlineSnapshotMatch.Mismatch, check.SlotMatch);
        Assert.Equal(levelOk ? OnlineSnapshotMatch.Matched : OnlineSnapshotMatch.Mismatch, check.LevelMatch);
        Assert.Equal(jobOk ? OnlineSnapshotMatch.Matched : OnlineSnapshotMatch.Mismatch, check.JobMatch);
        Assert.Equal(growOk ? OnlineSnapshotMatch.Matched : OnlineSnapshotMatch.Mismatch, check.GrowTypeMatch);
        Assert.Equal(!(slotOk && levelOk && jobOk && growOk), check.HasMismatch);
        string expectedTail = "snap=present slot=" + (slotOk ? "match" : "mismatch")
            + " level=" + (levelOk ? "match" : "mismatch")
            + " job=" + (jobOk ? "match" : "mismatch")
            + " grow=" + (growOk ? "match" : "mismatch");
        Assert.EndsWith(" perms=none " + expectedTail, rig.TownLines()[0]);
    }

    [Fact]
    public void The_second_slot_is_compared_with_its_own_entry()
    {
        // 槽 1:等级 32、职业 2、成长类型字节 0x01 → 成长类型 1
        TownEntrySnapshot snapshot = NetObjects.Snapshot(slot: 1, level: 32, job: 2, growType: 1);
        using TownHostRig rig = EnteredRig(select: slot => Success(snapshot), slot: 1);

        using OnlineTownController controller = Create(rig);

        OnlineTownSnapshotCheck check = controller.Profile.SnapshotCheck;
        Assert.Equal(OnlineSnapshotMatch.Matched, check.SlotMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.LevelMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.JobMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.GrowTypeMatch);
        Assert.False(check.HasMismatch);
    }

    [Fact]
    public void Snapshot_fields_that_the_server_did_not_provide_are_not_compared()
    {
        TownEntrySnapshot snapshot = NetObjects.Snapshot(slot: null, level: 55, job: null, growType: 2);
        using TownHostRig rig = EnteredRig(select: slot => Success(snapshot));

        using OnlineTownController controller = Create(rig);

        OnlineTownSnapshotCheck check = controller.Profile.SnapshotCheck;
        Assert.True(check.HasSnapshot);
        Assert.Equal(OnlineSnapshotMatch.NotProvided, check.SlotMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.LevelMatch);
        Assert.Equal(OnlineSnapshotMatch.NotProvided, check.JobMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, check.GrowTypeMatch);
        Assert.False(check.HasMismatch);
        Assert.EndsWith("snap=present slot=na level=match job=na grow=match", rig.TownLines()[0]);
    }

    [Fact]
    public void Without_a_snapshot_nothing_is_compared_and_without_permissions_the_count_is_unknown()
    {
        using TownHostRig rig = EnteredRig();

        using OnlineTownController controller = Create(rig);

        OnlineTownSnapshotCheck check = controller.Profile.SnapshotCheck;
        Assert.False(check.HasSnapshot);
        Assert.Equal(OnlineSnapshotMatch.NotProvided, check.SlotMatch);
        Assert.Equal(OnlineSnapshotMatch.NotProvided, check.LevelMatch);
        Assert.Equal(OnlineSnapshotMatch.NotProvided, check.JobMatch);
        Assert.Equal(OnlineSnapshotMatch.NotProvided, check.GrowTypeMatch);
        Assert.False(check.HasMismatch);
        Assert.Null(controller.Profile.DungeonPermissionCount);
    }

    [Fact]
    public void An_empty_permission_list_is_present_with_a_count_of_zero()
    {
        DungeonPermissionSnapshot permissions = NetObjects.Permissions();
        using TownHostRig rig = EnteredRig(select: slot => Success(null, permissions));

        using OnlineTownController controller = Create(rig);

        Assert.Equal(0, controller.Profile.DungeonPermissionCount);
        Assert.Contains(" perms=present ", rig.TownLines()[0]);
    }

    [Fact]
    public void A_single_permission_entry_counts_as_one()
    {
        DungeonPermissionSnapshot permissions = NetObjects.Permissions((904, 2));
        using TownHostRig rig = EnteredRig(select: slot => Success(null, permissions));

        using OnlineTownController controller = Create(rig);

        Assert.Equal(1, controller.Profile.DungeonPermissionCount);
    }

    // ------------------------------------------------------------------ 断线日志

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Polling_without_any_disconnect_logs_nothing_and_leaves_the_shell_alone(bool withWorld)
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controller = Create(rig, transport);
        if (withWorld)
        {
            controller.WrapWorld(new ProbeWorld(new ProbeSession()));
        }

        for (int i = 0; i < 4; i++)
        {
            controller.Poll(rig.Now += 16);
            rig.Tick();
        }

        Assert.Equal(new[] { EnterNoSnapshotOnline }, rig.TownLines());
        Assert.False(controller.DisconnectLogged);
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
        Assert.Null(rig.Host.Flow.Notice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_kick_is_logged_with_its_code_once_and_tells_the_shell_to_go_back_to_login(bool withWorld)
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controller = Create(rig, transport);
        if (withWorld)
        {
            controller.WrapWorld(new ProbeWorld(new ProbeSession()));
        }

        transport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1305));
        for (int i = 0; i < 5; i++)
        {
            controller.Poll(rig.Now += 16);
        }

        Assert.Equal(new[] { "DNF_ONLINE stage=town event=disconnect reason=kicked code=1305" }, Matching(rig, "event=disconnect"));
        Assert.True(controller.DisconnectLogged);
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
        rig.Tick();
        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Host.Flow.Notice?.Kind);
    }

    [Fact]
    public void A_lost_connection_is_logged_once_without_a_code_and_tells_the_shell_to_go_back_to_login()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controller = Create(rig, transport);

        transport.RaiseDisconnect();
        for (int i = 0; i < 5; i++)
        {
            controller.Poll(rig.Now += 16);
        }

        Assert.Equal(new[] { "DNF_ONLINE stage=town event=disconnect reason=connection_lost" }, Matching(rig, "event=disconnect"));
        Assert.True(controller.DisconnectLogged);
        rig.Tick();
        Assert.Equal(OnlineShellStage.Login, rig.Host.Stage);
    }

    [Fact]
    public void Nothing_more_is_logged_when_a_kick_follows_a_logged_loss_or_the_other_way_round()
    {
        using TownHostRig lossFirst = EnteredRig();
        var transportA = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controllerA = Create(lossFirst, transportA);
        transportA.RaiseDisconnect();
        controllerA.Poll(lossFirst.Now += 16);
        transportA.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1306));
        controllerA.Poll(lossFirst.Now += 16);
        controllerA.Poll(lossFirst.Now += 16);
        Assert.Equal(new[] { "DNF_ONLINE stage=town event=disconnect reason=connection_lost" }, Matching(lossFirst, "event=disconnect"));

        using TownHostRig kickFirst = EnteredRig();
        var transportB = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controllerB = Create(kickFirst, transportB);
        transportB.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1307));
        controllerB.Poll(kickFirst.Now += 16);
        transportB.RaiseDisconnect();
        controllerB.Poll(kickFirst.Now += 16);
        controllerB.Poll(kickFirst.Now += 16);
        Assert.Equal(new[] { "DNF_ONLINE stage=town event=disconnect reason=kicked code=1307" }, Matching(kickFirst, "event=disconnect"));
    }

    [Fact]
    public void Flushing_before_dispose_logs_a_queued_kick_once_and_later_flushes_and_polls_stay_silent()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controller = Create(rig, transport);

        transport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1308));
        controller.FlushDisconnectBeforeDispose(rig.Now += 16);
        controller.FlushDisconnectBeforeDispose(rig.Now += 16);
        controller.Poll(rig.Now += 16);

        Assert.Equal(new[] { "DNF_ONLINE stage=town event=disconnect reason=kicked code=1308" }, Matching(rig, "event=disconnect"));
        Assert.True(controller.DisconnectLogged);
        // 外壳是自己先回登录页的,这条路径不再去通知外壳
        rig.Tick();
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
    }

    [Fact]
    public void Flushing_when_neither_kicked_nor_lost_still_logs_a_lost_connection_once()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controller = Create(rig, transport);

        controller.FlushDisconnectBeforeDispose(rig.Now += 16);
        controller.FlushDisconnectBeforeDispose(rig.Now += 16);

        Assert.Equal(new[] { "DNF_ONLINE stage=town event=disconnect reason=connection_lost" }, Matching(rig, "event=disconnect"));
        Assert.True(controller.DisconnectLogged);
    }

    [Fact]
    public void Flushing_after_a_plain_dispose_logs_and_polls_nothing()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController controller = Create(rig, transport);
        controller.Dispose();

        controller.FlushDisconnectBeforeDispose(rig.Now += 16);

        Assert.Empty(Matching(rig, "event=disconnect"));
        Assert.False(controller.DisconnectLogged);
    }

    [Fact]
    public void Flushing_after_the_disconnect_was_already_logged_does_not_poll_again()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controller = Create(rig, transport);
        transport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1309));
        controller.Poll(rig.Now += 16);
        Assert.Single(Matching(rig, "event=disconnect"));

        // 之后再来一个"丢线"通知:已经写过日志,冲刷与轮询都不再写
        transport.RaiseDisconnect();
        controller.FlushDisconnectBeforeDispose(rig.Now += 16);
        controller.Poll(rig.Now += 16);

        Assert.Equal(new[] { "DNF_ONLINE stage=town event=disconnect reason=kicked code=1309" }, Matching(rig, "event=disconnect"));
    }

    [Fact]
    public void A_poll_after_dispose_never_logs_or_notifies_even_when_the_coordinator_saw_a_kick()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController controller = Create(rig, transport);
        transport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1310));
        controller.Coordinator.Poll(rig.Now += 16);
        Assert.True(controller.Coordinator.IsKicked);
        controller.Dispose();

        controller.Poll(rig.Now += 16);
        rig.Tick();

        Assert.Empty(Matching(rig, "event=disconnect"));
        Assert.False(controller.DisconnectLogged);
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
    }

    [Fact]
    public void Dispose_is_idempotent_and_releases_the_transport_once()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController controller = Create(rig, transport);

        controller.Dispose();
        controller.Dispose();

        Assert.True(transport.Disposed);
        Assert.Equal(transport.SubscribeCount, transport.UnsubscribeCount);
        Assert.Equal(2, transport.UnsubscribeCount);
    }

    // ------------------------------------------------------------------ 回选角与退出

    [Fact]
    public void Returning_to_character_select_runs_once_and_a_second_call_neither_logs_nor_asks_the_host_again()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController controller = Create(rig, transport);

        controller.ReturnToCharacterSelect();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);
        int switches = rig.Port(0).Inner.SwitchCalls + rig.Port(0).Inner.SwitchHostCalls;
        Assert.True(switches >= 1);
        Assert.True(transport.Disposed);

        controller.ReturnToCharacterSelect();
        for (int i = 0; i < 5; i++)
        {
            rig.Tick();
        }

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Host.Stage);
        Assert.Equal(switches, rig.Port(0).Inner.SwitchCalls + rig.Port(0).Inner.SwitchHostCalls);
        Assert.Equal(new[] { "DNF_ONLINE stage=town event=exit target=charselect outcome=requested" }, Matching(rig, "event=exit"));
    }

    [Fact]
    public async Task Quitting_after_returning_to_character_select_is_a_no_op()
    {
        using TownHostRig rig = EnteredRig();
        using OnlineTownController controller = Create(rig, new OnlineTownHostTests.RecordingTransport());
        controller.ReturnToCharacterSelect();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);

        await Waits.Bounded(controller.QuitGameAsync());

        Assert.Equal(new[] { "DNF_ONLINE stage=town event=exit target=charselect outcome=requested" }, Matching(rig, "event=exit"));
        rig.Tick();
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Host.Stage);
        Assert.Equal(0, rig.Port(0).Inner.DisposeCount);
    }

    [Fact]
    public void Returning_to_character_select_after_a_plain_dispose_does_nothing()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController controller = Create(rig, transport);
        controller.Dispose();
        int switches = rig.Port(0).Inner.SwitchCalls + rig.Port(0).Inner.SwitchHostCalls;

        controller.ReturnToCharacterSelect();
        for (int i = 0; i < 5; i++)
        {
            rig.Tick();
        }

        Assert.Empty(Matching(rig, "event=exit"));
        Assert.Equal(OnlineShellStage.InGame, rig.Host.Stage);
        Assert.Equal(switches, rig.Port(0).Inner.SwitchCalls + rig.Port(0).Inner.SwitchHostCalls);
    }

    [Fact]
    public async Task Quitting_closes_the_channel_connection_once_and_a_second_quit_neither_logs_nor_asks_the_host_again()
    {
        using TownHostRig rig = EnteredRig(realSession: true);
        var closes = new List<OnlineClientSession>();
        rig.Backend.SessionDisconnect = (session, token) =>
        {
            closes.Add(session);
            return Task.CompletedTask;
        };
        OnlineClientSession real = rig.Host.Session;
        OnlineTownController controller = Create(rig);

        await Waits.Bounded(controller.QuitGameAsync());
        await Waits.Bounded(controller.QuitGameAsync());

        Assert.Same(real, Assert.Single(closes));
        Assert.Equal(
            new[]
            {
                "DNF_ONLINE stage=town event=exit target=quit outcome=requested",
                "DNF_ONLINE stage=town event=exit target=quit outcome=done",
            },
            Matching(rig, "event=exit"));
        Assert.Equal(0, SessionProbe.FrameReceivedCount(real));
        Assert.Equal(0, SessionProbe.DisconnectedCount(real));
    }

    [Fact]
    public async Task A_failing_quit_is_logged_as_an_error_and_never_rethrown()
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController controller = Create(rig, transport);
        // 摘断线订阅时抛异常,会让后端的优雅断线失败(那一步在后端自己的 try 之外),宿主的退出随之失败
        rig.Port(0).ThrowOnLostRemove = true;
        try
        {
            await Waits.Bounded(controller.QuitGameAsync());
        }
        finally
        {
            rig.Port(0).ThrowOnLostRemove = false;
            rig.Backend.Dispose();
        }

        Assert.Equal(
            new[]
            {
                "DNF_ONLINE stage=town event=exit target=quit outcome=requested",
                "DNF_ONLINE stage=town event=exit target=quit outcome=error",
            },
            Matching(rig, "event=exit"));
        Assert.True(transport.Disposed);
    }

    // ------------------------------------------------------------------ 没有日志去处

    private static OnlineTownController CreateQuiet(TownHostRig rig, ITownCommandTransport custom)
    {
        // 不给日志去处:所有日志语句都要容忍它不存在
        return OnlineTownController.Create(rig.Host, OnlineTownHostTests.BuildAtlas(), customTransport: custom);
    }

    [Fact]
    public void Without_a_log_sink_a_kick_seen_by_poll_or_by_flush_still_goes_back_to_login()
    {
        using TownHostRig pollRig = EnteredRig();
        var pollTransport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController polled = CreateQuiet(pollRig, pollTransport);
        pollTransport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1305));
        polled.Poll(pollRig.Now += 16);
        pollRig.Tick();
        Assert.True(polled.DisconnectLogged);
        Assert.Equal(OnlineShellStage.Login, pollRig.Host.Stage);

        using TownHostRig flushRig = EnteredRig();
        var flushTransport = new OnlineTownHostTests.RecordingTransport();
        using OnlineTownController flushed = CreateQuiet(flushRig, flushTransport);
        flushTransport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1306));
        flushed.FlushDisconnectBeforeDispose(flushRig.Now += 16);
        Assert.True(flushed.DisconnectLogged);

        Assert.DoesNotContain(pollRig.TownLines(), l => l.Contains("event=disconnect"));
        Assert.DoesNotContain(flushRig.TownLines(), l => l.Contains("event=disconnect"));
    }

    [Fact]
    public async Task Without_a_log_sink_returning_to_character_select_and_quitting_work_even_when_the_quit_fails()
    {
        using TownHostRig backRig = EnteredRig();
        OnlineTownController back = CreateQuiet(backRig, new OnlineTownHostTests.RecordingTransport());
        back.ReturnToCharacterSelect();
        backRig.Until(() => backRig.Host.Stage == OnlineShellStage.CharacterSelect);

        using TownHostRig quitRig = EnteredRig();
        var quitTransport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController quitting = CreateQuiet(quitRig, quitTransport);
        await Waits.Bounded(quitting.QuitGameAsync());
        Assert.True(quitTransport.Disposed);
        Assert.False(quitRig.Backend.HasSession);

        using TownHostRig failRig = EnteredRig();
        var failTransport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController failing = CreateQuiet(failRig, failTransport);
        failRig.Port(0).ThrowOnLostRemove = true;
        try
        {
            await Waits.Bounded(failing.QuitGameAsync());
        }
        finally
        {
            failRig.Port(0).ThrowOnLostRemove = false;
            failRig.Backend.Dispose();
        }
        Assert.True(failTransport.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_disconnect_poll_or_flush_while_an_exit_is_in_progress_logs_nothing(bool quit)
    {
        using TownHostRig rig = EnteredRig();
        var transport = new OnlineTownHostTests.RecordingTransport();
        OnlineTownController controller = null;
        var reentered = new List<string>();
        string requested = quit ? "target=quit outcome=requested" : "target=charselect outcome=requested";
        controller = Create(rig, transport, line =>
        {
            rig.Log(line);
            if (line.Contains(requested))
            {
                // 退出请求的日志写出时,控制器已把"退出中"标志立起、还没释放:此时的再入式轮询与冲刷必须什么都不写
                reentered.Add("seen");
                controller.Poll(rig.Now += 16);
                controller.FlushDisconnectBeforeDispose(rig.Now += 16);
            }
        });
        transport.Push(new TownInboundEvent(TownInboundKind.Kicked, reasonCode: 1311));

        if (quit)
        {
            await Waits.Bounded(controller.QuitGameAsync());
        }
        else
        {
            controller.ReturnToCharacterSelect();
        }

        Assert.Single(reentered);
        Assert.Empty(Matching(rig, "event=disconnect"));
        Assert.False(controller.DisconnectLogged);
    }

    // ------------------------------------------------------------------ 每帧记录安全落点

    [Fact]
    public void Recording_before_the_step_reaches_the_wrapped_world_every_time()
    {
        using TownHostRig rig = EnteredRig();
        using OnlineTownController controller = Create(rig, new OnlineTownHostTests.RecordingTransport());
        controller.RecordBeforeStep();   // 还没有包装世界:什么也不发生
        var session = new ProbeSession();
        ITownWorld world = controller.WrapWorld(new ProbeWorld(session));
        world.Create(new TownPlace(1, 0));
        int afterCreate = session.PendingExitReads;
        Assert.True(afterCreate >= 1);

        controller.RecordBeforeStep();
        Assert.Equal(afterCreate + 1, session.PendingExitReads);
        controller.RecordBeforeStep();
        Assert.Equal(afterCreate + 2, session.PendingExitReads);
    }

    [Fact]
    public void Recording_before_the_step_after_dispose_does_not_throw()
    {
        using TownHostRig rig = EnteredRig();
        OnlineTownController controller = Create(rig, new OnlineTownHostTests.RecordingTransport());
        controller.WrapWorld(new ProbeWorld(new ProbeSession())).Create(new TownPlace(1, 0));
        controller.Dispose();

        controller.RecordBeforeStep();
    }

    private sealed class FixedTitles : ICharacterTitles
    {
        public string DefaultName => "测试默认名";

        public string ClassName(int job, int growType, int awakening) => "合成职业" + job + "-" + growType + "-" + awakening;
    }

    private sealed class ProbeSession : IHeroSession, ITownExitSource
    {
        public int PendingExitReads;

        public string MapPath => "probe";

        public TownHero Hero => null;

        public TownCamera Camera => null;

        public int LayerCount => 0;

        public int NpcCount => 0;

        public TownExit? PendingExit
        {
            get
            {
                PendingExitReads++;
                return null;
            }
        }

        public double HeroX => 0;

        public double HeroY => 0;

        public bool HeroFacingLeft => false;

        public void Update(double deltaMs, int dirX, int dirY)
        {
        }

        public void OutwardOf(TownExit exit, out int dx, out int dy)
        {
            dx = 0;
            dy = 0;
        }

        public List<SceneSprite> Build(long timeMs) => new();
    }

    private sealed class ProbeWorld : ITownWorld
    {
        private readonly ProbeSession _session;

        public ProbeWorld(ProbeSession session) => _session = session;

        public TownPlace Start => new TownPlace(1, 0);

        public TownPlace Return => new TownPlace(1, 0);

        public IHeroSession Create(TownPlace place) => _session;

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft) => null;

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            exit = default;
            return false;
        }
    }
}
