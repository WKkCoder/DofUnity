using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器的"入口一侧"缺口补测:选项默认值、创建参数校验、钩子的挂接/撤销、许可帧过滤、
/// 以及请求进副本时(选择屏上)各个前置分支——被禁条目、副本编号边界、难度夹紧、断线预检、"进入中"提示。
/// 全部用合成数据,不连任何服务;时间只走场景台的逻辑时钟。
/// </summary>
public sealed class OnlineDungeonFlowGapEntryTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";

    // ------------------------------------------------------------------------------------------------
    // 选项默认值 / 创建参数
    // ------------------------------------------------------------------------------------------------

    /// <summary>保护"选项默认值":退出等待 500ms、瞬态提示 4000ms、返城延迟取结算延迟 3000ms,其余时限与回调一概不预设。</summary>
    [Fact]
    public void Default_options_carry_the_documented_limits_and_leave_every_callback_unset()
    {
        var options = new OnlineDungeonFlowOptions();

        Assert.Equal(500, options.QuitWaitTimeoutMs);            // 退出时最多等 500ms 让后台任务收敛
        Assert.Equal(4000L, options.TransientNoticeDurationMs);  // 返城后的瞬态提示 4 秒
        Assert.Equal(3000.0, options.ReturnDelayMs);             // = DungeonSession.SettlementDelayMs(3 秒)
        Assert.Equal(DungeonSession.SettlementDelayMs, options.ReturnDelayMs);
        Assert.Null(options.LoadingNoticeTimeout);
        Assert.Null(options.DungeonInfoTimeout);
        Assert.Null(options.StartMapTimeout);
        Assert.Null(options.CombatOptions);
        Assert.Null(options.Log);
        Assert.Null(options.NetLog);
        Assert.Null(options.OnDisconnected);
        Assert.Null(options.CurrentArea);
        Assert.Null(options.HeroLevel);
        Assert.Null(options.Atlas);
        Assert.Null(options.ReturnPlace);
        Assert.Null(options.Fonts);
    }

    private static DungeonPermissionSnapshot SnapshotOf(params (int Id, byte Tier)[] entries)
    {
        DungeonPermissionSnapshot snapshot = DungeonPermissionParser.Collect(new[]
        {
            DungeonEntryWire.Decoded(CombatKit.Noti, DungeonPermissionParser.PacketId, PermissionPayload(entries)),
        });
        Assert.NotNull(snapshot);
        return snapshot;
    }

    /// <summary>保护"创建时先校验再产生副作用":会话为 null(或策略为 null)就抛 ArgumentNullException,而且许可表一个字都不能被初始快照写进去。</summary>
    [Fact]
    public void Create_rejects_a_null_session_or_policy_before_loading_any_permission_state()
    {
        var table = new OnlineDungeonPermissionTable();
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, hasLocalData: _ => true, hasUltimate: _ => true);
        var flow = new GameFlow(new TestTownWorld(new TownPlace(1, 0), new TownPlace(1, 1)), (p, _) => throw new InvalidOperationException("unused"), Array.Empty<DungeonEntry>());
        DungeonPermissionSnapshot snapshot = SnapshotOf((DungeonAId, 3));
        using var session = SessionProbe.NewUnconnected();

        ArgumentNullException noSession = Assert.Throws<ArgumentNullException>(
            () => OnlineDungeonFlowController.Create(null, snapshot, flow, policy, _ => null));
        Assert.Equal("session", noSession.ParamName);
        Assert.False(table.HasSnapshot);   // 初始快照还没载入:校验在副作用之前
        Assert.Equal(0, table.EntryCount);

        ArgumentNullException noPolicy = Assert.Throws<ArgumentNullException>(
            () => OnlineDungeonFlowController.Create(session, snapshot, flow, null, _ => null));
        Assert.Equal("policy", noPolicy.ParamName);
    }

    /// <summary>
    /// 保护"用真会话创建":载入初始许可快照、在真会话的两个事件上各挂一个处理器,并把流程的三个钩子接管过来;
    /// 退出时两个事件都撤干净,流程的钩子归还。
    /// </summary>
    [Fact]
    public void Create_with_a_real_unconnected_session_loads_the_snapshot_and_attaches_then_detaches_cleanly()
    {
        var table = new OnlineDungeonPermissionTable();
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, hasLocalData: _ => true, hasUltimate: _ => true);
        var flow = new GameFlow(new TestTownWorld(new TownPlace(1, 0), new TownPlace(1, 1)), (p, _) => throw new InvalidOperationException("unused"), Array.Empty<DungeonEntry>());
        using var session = SessionProbe.NewUnconnected();

        using OnlineDungeonFlowController ctrl = OnlineDungeonFlowController.Create(session, SnapshotOf((DungeonAId, 3), (DungeonBId, 1)), flow, policy, _ => null);

        Assert.Same(policy, ctrl.Policy);
        Assert.Same(flow, ctrl.Flow);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, ctrl.Phase);
        Assert.True(table.HasSnapshot);
        Assert.Equal(2, table.EntryCount);
        Assert.Equal(3, table.GetTier(DungeonAId));
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(1, SessionProbe.DisconnectedCount(session));
        Assert.NotNull(flow.OnlineStartDungeon);
        Assert.NotNull(flow.OnlineGiveUpDungeon);
        Assert.NotNull(flow.ConfigureSelect);

        ctrl.PrepareForQuit();

        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));
        Assert.Null(flow.OnlineStartDungeon);
        Assert.Null(flow.OnlineGiveUpDungeon);
        Assert.Null(flow.ConfigureSelect);
    }

    // ------------------------------------------------------------------------------------------------
    // 钩子的挂接与撤销
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护"撤销只做一次且归还流程标志":构造时每个端口事件各订阅 1 次;PrepareForQuit 把它们各退订 1 次并把"暂缓退出副本"标志清成 false;
    /// 之后的 Dispose 不得再退订、也不得再动宿主后来挂回去的钩子。
    /// </summary>
    [Fact]
    public void Quit_detaches_handlers_once_clears_the_hold_flag_and_does_not_touch_hooks_again_on_dispose()
    {
        using var rig = new Rig();
        Assert.Equal(1, rig.Port.FrameReceivedAdds);
        Assert.Equal(1, rig.Port.DisconnectedAdds);
        Assert.Equal(0, rig.Port.FrameReceivedRemoves);
        Assert.Equal(0, rig.Port.DisconnectedRemoves);
        rig.Flow.OnlineHoldDungeonExit = true;   // 假装结算菜单正在停留

        rig.Ctrl.PrepareForQuit();

        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(1, rig.Port.FrameReceivedRemoves);
        Assert.Equal(1, rig.Port.DisconnectedRemoves);
        Assert.Null(rig.Flow.OnlineStartDungeon);
        Assert.Null(rig.Flow.OnlineGiveUpDungeon);
        Assert.Null(rig.Flow.ConfigureSelect);

        // 宿主在 PrepareForQuit 之后又把放弃回城钩子挂回来:随后的 Dispose 属于"已经撤销过",不能把它再撤掉
        rig.Flow.OnlineGiveUpDungeon = rig.Ctrl.RequestGiveUpDungeon;
        rig.Ctrl.Dispose();

        Assert.Equal(1, rig.Port.FrameReceivedRemoves);
        Assert.Equal(1, rig.Port.DisconnectedRemoves);
        Assert.NotNull(rig.Flow.OnlineGiveUpDungeon);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);

        // 再次 Dispose 是空操作
        rig.Ctrl.Dispose();
        Assert.Equal(1, rig.Port.FrameReceivedRemoves);
    }

    // ------------------------------------------------------------------------------------------------
    // 许可帧过滤
    // ------------------------------------------------------------------------------------------------

    /// <summary>保护"只认 通知类 + 许可包号":别的通知包号、同包号但是命令类的帧都必须被忽略(不改表、不写日志);真正的许可帧照常生效并写一行日志。</summary>
    [Fact]
    public void Permission_updates_only_apply_to_the_notification_class_with_the_permission_packet_id()
    {
        using var rig = new Rig();
        int version = rig.Table.Version;
        int notices = rig.Table.NoticeCount;

        rig.Port.PushFrame(PacketClass.Noti, DungeonPermissionParser.PacketId + 1, PermissionPayload((DungeonAId, 0)));   // 别的通知
        rig.Port.PushFrame(PacketClass.Cmd, DungeonPermissionParser.PacketId, PermissionPayload((DungeonAId, 0)));       // 同包号但是命令类
        rig.Port.PushFrame(PacketClass.Noti, DungeonPermissionParser.PacketId + 1, new byte[] { 5, 0 });                 // 载荷若被误读会算"坏帧"

        Assert.Equal(version, rig.Table.Version);
        Assert.Equal(notices, rig.Table.NoticeCount);
        Assert.Equal(0, rig.Table.MalformedNoticeCount);
        Assert.Equal(3, rig.Table.GetTier(DungeonAId));
        Assert.Empty(rig.Logs());

        rig.Port.PushFrame(PacketClass.Noti, DungeonPermissionParser.PacketId, PermissionPayload((DungeonAId, 1)));

        Assert.Equal(1, rig.Table.GetTier(DungeonAId));
        Assert.Equal(version + 1, rig.Table.Version);
        Assert.Equal(new[] { P + "event=perms entries=2 malformed=0" }, rig.Logs());   // 表里有 A、B 两条
    }

    // ------------------------------------------------------------------------------------------------
    // 请求进副本的前置分支
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护"选择屏上遇到被禁条目":就在原屏上收口——同一个选择屏对象不换、忙标志清掉、提示换成"空提示"(种类 None 而不是 null),
    /// 不进入进副本阶段、不碰独占收包、不发任何命令,只写一行 blocked 日志。
    /// </summary>
    [Fact]
    public void Blocked_entry_on_the_select_screen_keeps_the_same_screen_clears_busy_and_resets_the_notice()
    {
        using var rig = new Rig();
        rig.Start();
        DungeonSelectScreen screen = rig.Flow.Select;
        screen.Busy = true;
        screen.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Failed);
        rig.Table.MarkServerRejected(DungeonAId, 14);
        int history = rig.Flow.History.Count;
        int serial = rig.Flow.StageSerial;

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 2);

        Assert.Same(screen, rig.Flow.Select);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(serial, rig.Flow.StageSerial);
        Assert.Equal(history, rig.Flow.History.Count);
        Assert.False(screen.Busy);
        Assert.NotNull(screen.Notice);
        Assert.Equal(DungeonSelectNoticeKind.None, screen.Notice.Kind);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(0, rig.Port.EnterExclusiveCalls);
        Assert.Equal(0, rig.Port.CreateRunnerCalls);
        Assert.Empty(rig.Port.Commands);
        // 难度 2 在副本 A 的上限 3 之内原样保留;原因取被禁的种类名
        Assert.Equal(new[] { P + "event=select outcome=blocked difficulty=2 max=3 reason=ServerRejected" }, rig.Logs());
    }

    /// <summary>保护"不在选择屏时遇到被禁条目":改走失败回退——重新打开选择屏停在原条目与(夹紧后的)难度上,不带提示,切换原因写明被禁的种类。</summary>
    [Fact]
    public void Blocked_entry_outside_the_select_screen_reopens_the_select_screen_without_a_notice()
    {
        using var rig = new Rig();
        rig.Flow.EnterTown("boot");
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Null(rig.Flow.Select);
        rig.Table.MarkServerRejected(DungeonBId, null);

        rig.Flow.OnlineStartDungeon(rig.Entries[1], 3);   // 副本 B 的上限是 1:难度从 3 夹到 1

        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.NotNull(rig.Flow.Select);
        Assert.Equal(1, rig.Flow.Select.Index);
        Assert.Equal(1, rig.Flow.Select.Difficulty);
        Assert.Null(rig.Flow.Select.Notice);
        Assert.False(rig.Flow.Select.Busy);
        Assert.Equal("select blocked ServerRejected", rig.Flow.History[rig.Flow.History.Count - 1].Reason);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new[] { P + "event=select outcome=blocked difficulty=1 max=1 reason=ServerRejected" }, rig.Logs());
    }

    /// <summary>
    /// 保护"难度夹紧":请求难度先夹到 [0, min(该副本上限, 3)]——负数夹到 0、超出上限夹到上限;
    /// 夹紧后的值同时进日志、进选副本命令(第三字节前的模式字节)。
    /// </summary>
    [Theory]
    [InlineData(0, -3, 0, 3, 0x8D, 0x23, 0)]   // 副本 A(9101 = 0x238D)上限 3:-3 → 0
    [InlineData(0, 2, 2, 3, 0x8D, 0x23, 2)]    // 范围内原样
    [InlineData(0, 9, 3, 3, 0x8D, 0x23, 3)]    // 超过全局上限 3 → 3
    [InlineData(1, 3, 1, 1, 0x8E, 0x23, 1)]    // 副本 B(9102 = 0x238E)上限 1:3 → 1
    public void Requested_difficulty_is_clamped_into_the_dungeon_limit_and_sent_as_the_mode(int entryIndex, int requested, int expected, int expectedMax, int idLow, int idHigh, int mode)
    {
        using var rig = new Rig(o =>
        {
            o.LoadingNoticeTimeout = TimeSpan.FromSeconds(30);
            o.DungeonInfoTimeout = TimeSpan.FromSeconds(30);
            o.StartMapTimeout = TimeSpan.FromSeconds(30);
        });
        rig.Port.SetResponder((cls, id, _) => cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId ? LoadingFrames() : Array.Empty<CombatKit.ServerFrame>());
        rig.Start();

        rig.Flow.OnlineStartDungeon(rig.Entries[entryIndex], requested);

        // 日志是同步写的:不必等后台任务
        Assert.Equal(
            new[]
            {
                P + "event=select outcome=enter difficulty=" + expected + " max=" + expectedMax,
                P + "event=enter areakind=unknown entrance=true difficulty=" + expected,
            },
            rig.Logs());
        Assert.Equal(expected, rig.Flow.Difficulty);

        rig.WaitForCommand(OnlineDungeonEntry.SelectDungeonPacketId);
        (PacketClass Class, int PacketId, byte[] Payload) select = rig.Port.Commands.First(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonEntry.SelectDungeonPacketId);
        // 载荷 = u16 副本号(小端) + 模式 + 第三字节(固定 2) + 安全字节(0)
        Assert.Equal(new[] { (byte)idLow, (byte)idHigh, (byte)mode, (byte)2, (byte)0 }, select.Payload);
    }

    /// <summary>
    /// 保护"副本编号 65535 合法":编号恰好是 u16 上限时不被拦,照常进入进副本阶段并把编号 FF FF 发出去。
    /// </summary>
    [Fact]
    public void Dungeon_id_at_the_u16_maximum_is_accepted_and_sent_unchanged()
    {
        var entries = new[] { new DungeonEntry("dungeon/test/dungmax.dgn", "DUNGMAX", new DungeonCard(65535, 100, 100, 0, null, 0, 1)) };
        using var rig = new Rig(o =>
        {
            o.LoadingNoticeTimeout = TimeSpan.FromSeconds(30);
            o.DungeonInfoTimeout = TimeSpan.FromSeconds(30);
            o.StartMapTimeout = TimeSpan.FromSeconds(30);
        }, entries);
        rig.Port.SetResponder((cls, id, _) => cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId ? LoadingFrames() : Array.Empty<CombatKit.ServerFrame>());
        rig.Start();

        rig.Flow.OnlineStartDungeon(entries[0], 0);

        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.DoesNotContain(rig.Logs(), l => l.Contains("MissingDungeonId"));
        rig.WaitForCommand(OnlineDungeonEntry.SelectDungeonPacketId);
        (PacketClass Class, int PacketId, byte[] Payload) select = rig.Port.Commands.First(c => c.Class == PacketClass.Cmd && c.PacketId == OnlineDungeonEntry.SelectDungeonPacketId);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x02, 0x00 }, select.Payload);   // 65535 = 0xFFFF
    }

    /// <summary>
    /// 保护"解析出的副本编号越界就拦":策略前两次问编号(状态判定、难度上限)给合法值、第三次(真正解析)给越界值时,
    /// 写 MissingDungeonId 失败日志、重开选择屏并带"失败"提示、不进进副本阶段;65535 本身(同一条路径)则放行。
    /// </summary>
    [Theory]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void An_out_of_range_resolved_dungeon_id_fails_the_start_with_a_failed_notice(int resolved)
    {
        int calls = 0;
        var entries = new[] { new DungeonEntry("dungeon/test/dungx.dgn", "DUNGX") };
        using var rig = new Rig(null, entries, table => OnlineDungeonSelectPolicy.Create(
            table,
            idResolver: _ => ++calls <= 2 ? DungeonAId : resolved,   // 先后被 EntryStatus、DifficultyLimit 各问一次,第三次才是 ResolveDungeonId
            hasLocalData: _ => true,
            hasUltimate: _ => true));
        rig.Start();
        calls = 0;   // 选择屏打开时屏幕自己也问过上限:从请求这一刻重新数

        rig.Flow.OnlineStartDungeon(entries[0], 0);

        Assert.True(calls >= 3);   // 重开选择屏时屏幕自己还会再问一次上限,所以只要求"至少到过第三次"
        Assert.Equal(new[] { P + "event=fail reason=MissingDungeonId step=ResolveId" }, rig.Logs());
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.NotNull(rig.Flow.Select.Notice);
        Assert.Equal(DungeonSelectNoticeKind.Failed, rig.Flow.Select.Notice.Kind);
        Assert.False(rig.Flow.Select.Busy);
        Assert.Equal("online missing id", rig.Flow.History[rig.Flow.History.Count - 1].Reason);
        Assert.Equal(0, rig.Port.EnterExclusiveCalls);
        Assert.Empty(rig.Port.Commands);
    }

    /// <summary>
    /// 保护"进副本前预检断线":端口说"已断"、或控制器自己收到过断线事件(两者任一为真,不是必须同时),都要写 PreEnter 断线日志、
    /// 触发一次断线收尾、拒绝进入(不进阶段、不碰独占收包、不发命令)。
    /// </summary>
    [Theory]
    [InlineData(true)]    // 只有端口的断线标志为真(没有触发过 Disconnected 事件)
    [InlineData(false)]   // 只有控制器自己记下的断线事件为真(端口的标志还是"没断")
    public void Pre_enter_disconnect_check_triggers_when_either_the_port_or_the_event_reports_it(bool portFlagOnly)
    {
        using var rig = new Rig();
        rig.Start();
        if (portFlagOnly)
        {
            rig.Port.IsDisconnected = true;
        }
        else
        {
            rig.Port.RaiseDisconnectedEventOnly(OnlineDisconnectReason.PeerClosed);
        }

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);

        string[] expected = portFlagOnly
            ? new[] { P + "event=fail reason=Disconnected step=PreEnter" }
            : new[] { P + "event=fail reason=Disconnected step=PeerClosed", P + "event=fail reason=Disconnected step=PreEnter" };
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(1, rig.Disconnects);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Port.EnterExclusiveCalls);
        Assert.Equal(0, rig.Port.CreateRunnerCalls);
        Assert.Empty(rig.Port.Commands);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.Select.Busy);

        // 再请求一次:断线收尾只做一次
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);
        Assert.Equal(1, rig.Disconnects);
    }

    /// <summary>
    /// 保护"进入中提示":请求被接受后,没有加载过渡画的流程里选择屏被标忙并显示"进入中"提示,
    /// 控制器的当前提示也是"进入中"(叠层靠它画),阶段是 Entering。
    /// </summary>
    [Fact]
    public void Accepted_request_shows_the_entering_notice_on_both_the_controller_and_the_busy_select_screen()
    {
        using var rig = new Rig(o =>
        {
            o.LoadingNoticeTimeout = TimeSpan.FromSeconds(30);
            o.DungeonInfoTimeout = TimeSpan.FromSeconds(30);
            o.StartMapTimeout = TimeSpan.FromSeconds(30);
        });
        rig.Start();

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 0);

        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.NotNull(rig.Ctrl.ActiveNotice);
        Assert.Equal(DungeonSelectNoticeKind.Entering, rig.Ctrl.ActiveNotice.Kind);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.True(rig.Flow.Select.Busy);
        Assert.Equal(DungeonSelectNoticeKind.Entering, rig.Flow.Select.Notice.Kind);

        // 控制器拥有的是运行期提示:回到 Idle(这里用 Dispose 收尾前先看一眼 Poll 不会误清它)
        rig.Poll();
        Assert.Equal(DungeonSelectNoticeKind.Entering, rig.Ctrl.ActiveNotice.Kind);
    }
}
