using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 多房间状态机(<see cref="OnlineDungeonCombat"/> 的换房、换表、装载握手部分)的单元测试,不接会话:用记录用的发送口,手工喂服务端帧。
/// 覆盖:换房闸门的各道关口与先后次序、被服务端拒绝后什么都不前进、成功后整体换表且本地编号从 0 重来、旧房间的请求拦下、
/// 起始地图通知与换房回包只在换房命令在途时才认、装载握手(装载完成命令何时发、通知何时算数)、发送失败与异常、事件链。
/// 数据全是合成的:怪编号第一间 9100 起、第二间 9300 起,回包的错误码用 201 这类没有语义的合成值(状态机对任何码一视同仁)。
/// </summary>
public class OnlineDungeonCombatRoomTests
{
    private static readonly int[] SecondRoom = { 9300, 9301 };

    // ======== 起点 ========

    /// <summary>刚建好的状态机在房间 0,没有任何握手或换房在等;缺省选项不要求起始房间报装载完成(单图行为不变)。</summary>
    [Fact]
    public void A_fresh_machine_is_in_room_zero_with_nothing_pending()
    {
        var rig = new RoomRig(3);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(0, m.Room);
        Assert.Same(rig.Map, m.Map);
        Assert.False(m.MoveInFlight);
        Assert.Equal(OnlineRoomLoadState.None, m.LoadState);
        Assert.False(m.LoadPending);
        Assert.Equal(0, m.RoomRefusals + m.MoveRefusals + m.MovesSent + m.LoadReports);
        Assert.Equal(3, m.Total);
        Assert.All(m.Monsters, mon => Assert.Equal(0, mon.Room));
        Assert.False(m.RoomCleared);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, m.MoveGate());
        Assert.Empty(rig.Port.Sent);
    }

    /// <summary>没有怪的房间"清空"空真:闸门只剩别的关口(这里全开)。</summary>
    [Fact]
    public void A_room_without_monsters_counts_as_cleared()
    {
        var rig = new RoomRig(new int[0]);

        Assert.True(rig.Machine.RoomCleared);
        Assert.Equal(OnlineCombatBlock.None, rig.Machine.MoveGate());
    }

    // ======== 换房闸门 ========

    /// <summary>只认"全部被服务端确认"这条正面证据:没报的、报了没确认的、被拒的,都不算清空;编号重复没有独立编号的不参与。</summary>
    [Fact]
    public async Task A_move_waits_for_every_monster_to_be_confirmed()
    {
        var rig = new RoomRig(3);
        OnlineDungeonCombat m = rig.Machine;

        await rig.Kill(0);
        rig.Confirm(rig.Ids[0]);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, m.MoveGate());            // 另两只还活着

        await rig.Kill(1);                                                       // 报了、没确认
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, m.MoveGate());
        rig.Confirm(rig.Ids[1]);

        await rig.Kill(2);
        rig.Feed(CombatKit.Refusal(rig.Ids[2]));                                 // 被服务端拒了:不是"确认"
        Assert.Equal(OnlineCombatMonsterState.Refused, m.Monsters[2].State);
        Assert.False(m.RoomCleared);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, m.MoveGate());

        OnlineCombatSendResult blocked = await rig.Move();
        Assert.Equal(OnlineCombatSendOutcome.Blocked, blocked.Outcome);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, blocked.Block);
        Assert.Equal(0, rig.Commands(48));
    }

    [Fact]
    public async Task Monsters_without_an_independent_number_do_not_hold_the_gate()
    {
        var rig = new RoomRig(new[] { 9100, 9101, 9100 });               // 第三条与第一条编号相同:只有前两只能独立上报
        OnlineDungeonCombat m = rig.Machine;
        Assert.True(m.Monsters[2].DuplicateNumber);

        await rig.Kill(0);
        rig.Confirm(9100);
        await rig.Kill(1);
        rig.Confirm(9101);

        Assert.True(m.RoomCleared);
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());
        Assert.True((await rig.Move()).Sent);
    }

    /// <summary>被本地拦下的换房不发包、不动状态,记账并留一条带原因的事件。</summary>
    [Fact]
    public async Task A_blocked_move_sends_nothing_counts_and_leaves_an_event()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;

        OnlineCombatSendResult result = await rig.Move();

        Assert.False(result.Sent);
        Assert.Equal(OnlineCombatSendOutcome.Blocked, result.Outcome);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, result.Block);
        Assert.Empty(rig.Port.Sent);
        Assert.Equal(1, m.Blocked);
        Assert.Equal(0, m.MovesSent);
        Assert.False(m.MoveInFlight);
        OnlineCombatEvent e = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.RoomMoveBlocked, e.Kind);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, e.Block);
        Assert.Equal(0, e.Room);
        Assert.Equal(-1, e.LocalId);
        Assert.Equal(-1, e.ServerId);
    }

    /// <summary>放行的换房发出类 1、包号 48 的 43 字节命令,进入"在途":第二条换房被拦下,不重复发。</summary>
    [Fact]
    public async Task A_sent_move_goes_in_flight_and_a_second_one_is_blocked()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());

        OnlineCombatSendResult first = await rig.Move(4, 4, 901, 902);

        Assert.True(first.Sent);
        Assert.True(m.MoveInFlight);
        Assert.Equal(1, m.MovesSent);
        (PacketClass cls, ushort id, byte[] payload) = rig.Port.Sent.Last();
        Assert.Equal(PacketClass.Cmd, cls);
        Assert.Equal(48, id);
        Assert.Equal(43, payload.Length);
        Assert.Equal(OnlineCombatEventKind.RoomMoveSent, m.Events.Last().Kind);
        Assert.Equal(0, m.Events.Last().Room);

        Assert.Equal(OnlineCombatBlock.MoveInFlight, m.MoveGate());
        OnlineCombatSendResult second = await rig.Move();
        Assert.Equal(OnlineCombatSendOutcome.Blocked, second.Outcome);
        Assert.Equal(OnlineCombatBlock.MoveInFlight, second.Block);
        Assert.Equal(1, rig.Commands(48));
    }

    /// <summary>请求绑定的房间不是当前房间:房间对不上排在所有别的关口之前(哪怕别的关口也关着)。</summary>
    [Fact]
    public async Task A_move_bound_to_another_room_is_stale_before_anything_else()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(OnlineCombatBlock.StaleRoom, m.MoveGate(1));
        Assert.Equal(OnlineCombatBlock.StaleRoom, m.MoveGate(-1));
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, m.MoveGate(0));
        OnlineCombatSendResult stale = await m.RequestMoveRoomAsync(1, RoomKit.Move(4, 4));
        Assert.Equal(OnlineCombatSendOutcome.Blocked, stale.Outcome);
        Assert.Equal(OnlineCombatBlock.StaleRoom, stale.Block);
        Assert.Empty(rig.Port.Sent);

        await rig.KillAndConfirmAll();
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate(0));
        Assert.Equal(OnlineCombatBlock.StaleRoom, m.MoveGate(1));                // 全开了也一样
        Assert.Equal(0, rig.Commands(48));
    }

    /// <summary>清图通知到了(进入结算流程),换房闸门关死:排在"在途""握手""没清"之前。</summary>
    [Fact]
    public async Task Moves_are_closed_for_good_once_the_clear_notice_arrives()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;

        rig.Feed(CombatKit.Clear());                                             // 怪还活着、还没报:本该是 RoomNotCleared
        Assert.Equal(OnlineCombatPhase.Cleared, m.Phase);
        Assert.Equal(OnlineCombatBlock.PhaseClosed, m.MoveGate());

        OnlineCombatSendResult blocked = await rig.Move();
        Assert.Equal(OnlineCombatBlock.PhaseClosed, blocked.Block);
        Assert.Equal(0, rig.Commands(48));

        Assert.True((await m.RequestSettlementAsync()).Sent);
        Assert.Equal(OnlineCombatBlock.PhaseClosed, m.MoveGate());               // 结算中也一样
    }

    /// <summary>
    /// 清图闸门与换房闸门是两回事:一个房间全部确认只放开换房,结算命令的闸门仍然只认服务端的清图通知——
    /// 多房间副本里"某一间清空"不能被当成"副本清空",否则会在没通关时发结算命令。
    /// </summary>
    [Fact]
    public async Task Clearing_a_room_never_opens_the_settlement_gate()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        Assert.True(m.RoomCleared);

        OnlineCombatSendResult early = await m.RequestSettlementAsync();
        Assert.Equal(OnlineCombatSendOutcome.Blocked, early.Outcome);
        Assert.Equal(OnlineCombatBlock.NotCleared, early.Block);
        Assert.Equal(0, rig.Commands(49));

        Assert.True((await rig.Move()).Sent);                                      // 第一间全确认了:换房放行
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(RoomKit.StartMap(SecondRoom, 4, 4)));
        await rig.Handshake();
        await rig.KillAndConfirmAll();
        Assert.True(m.RoomCleared);
        OnlineCombatSendResult stillEarly = await m.RequestSettlementAsync();      // 第二间也全确认了,但服务端还没说清图
        Assert.Equal(OnlineCombatBlock.NotCleared, stillEarly.Block);
        Assert.Equal(0, rig.Commands(49));

        rig.Feed(CombatKit.Clear());
        Assert.True((await m.RequestSettlementAsync()).Sent);
        Assert.Equal(1, rig.Commands(49));
    }

    // ======== 被服务端拒绝 ========

    /// <summary>
    /// 服务端回了换房失败:状态机留在原房间,表、地图、怪的进度、握手状态一概不动;只有"在途"解除、拒绝记数加一;同一间可以再试。
    /// </summary>
    [Fact]
    public async Task A_refused_move_changes_nothing_but_the_refusal_count()
    {
        var rig = new RoomRig(3);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        IReadOnlyList<OnlineCombatMonster> tableBefore = m.Monsters;
        StartMapNotice mapBefore = m.Map;
        int sentBefore = m.Sent, confirmedBefore = m.Confirmed, totalBefore = m.Total;
        await rig.Move();

        OnlineCombatFrameVerdict verdict = rig.Feed(RoomKit.MoveRefusal(201, 6));

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, verdict);
        Assert.Equal(0, m.Room);
        Assert.Same(tableBefore, m.Monsters);
        Assert.Same(mapBefore, m.Map);
        Assert.All(m.Monsters, mon => Assert.Equal(OnlineCombatMonsterState.Confirmed, mon.State));
        Assert.Equal(OnlineCombatPhase.Fighting, m.Phase);
        Assert.False(m.MoveInFlight);
        Assert.Equal(OnlineRoomLoadState.None, m.LoadState);
        Assert.Equal(0, m.LoadReports);
        Assert.Equal(1, m.RoomRefusals);
        Assert.Equal(1, m.MoveRefusals);
        Assert.Equal(1, m.MovesSent);
        Assert.Equal((sentBefore, confirmedBefore, totalBefore), (m.Sent, m.Confirmed, m.Total));
        Assert.Equal(0, m.OutOfOrder + m.Duplicates + m.UnknownNumbers + m.Malformed + m.PassthroughCount);
        OnlineCombatEvent e = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.RoomMoveRefused, e.Kind);
        Assert.Equal(201, e.ReplyCode);
        Assert.Equal(0, e.Room);

        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());                      // 同一间可以再试
        Assert.True((await rig.Move()).Sent);
        Assert.Equal(2, rig.Commands(48));
    }

    /// <summary>拒绝额度是按房间算的:用完之后这一间不再发换房命令(本地拦下),别的关口排在它后面。</summary>
    [Fact]
    public async Task The_refusal_budget_per_room_ends_the_attempts()
    {
        var rig = new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { MaxMoveRefusalsPerRoom = 2 });
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();

        for (int i = 0; i < 2; i++)
        {
            Assert.True((await rig.Move()).Sent);
            rig.Feed(RoomKit.MoveRefusal(201));
        }

        Assert.Equal(2, m.RoomRefusals);
        Assert.Equal(OnlineCombatBlock.MoveBudgetExhausted, m.MoveGate());
        OnlineCombatSendResult blocked = await rig.Move();
        Assert.Equal(OnlineCombatBlock.MoveBudgetExhausted, blocked.Block);
        Assert.Equal(2, rig.Commands(48));
        Assert.Equal(OnlineCombatBlock.StaleRoom, m.MoveGate(1));
    }

    /// <summary>额度在进入新房间时清零(用掉一次、成功换房、新房间里又有完整的额度)。</summary>
    [Fact]
    public async Task The_refusal_budget_starts_over_in_the_next_room()
    {
        var rig = new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { MaxMoveRefusalsPerRoom = 1 });
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        await rig.Move();
        rig.Feed(RoomKit.MoveRefusal(201));
        Assert.Equal(OnlineCombatBlock.MoveBudgetExhausted, m.MoveGate());

        // 额度用完之后这一间走不了了;换一个额度大的机器验证"换房成功会清零"
        var rig2 = new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { MaxMoveRefusalsPerRoom = 2 });
        await rig2.KillAndConfirmAll();
        await rig2.Move();
        rig2.Feed(RoomKit.MoveRefusal(201));
        Assert.Equal(1, rig2.Machine.RoomRefusals);
        await rig2.Move();
        rig2.Feed(RoomKit.StartMap(SecondRoom, 4, 4));

        Assert.Equal(1, rig2.Machine.Room);
        Assert.Equal(0, rig2.Machine.RoomRefusals);
        Assert.Equal(1, rig2.Machine.MoveRefusals);                              // 累计的不清
    }

    /// <summary>换房回包只在换房命令在途时才算数;失败形状但读不下来记畸形、成功形状透传,二者都不解除"在途"。</summary>
    [Fact]
    public async Task The_refusal_reply_is_only_taken_while_a_move_is_in_flight()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(RoomKit.MoveRefusal(201)));
        Assert.Equal(1, m.PassthroughCount);
        Assert.Equal(0, m.RoomRefusals);

        await rig.KillAndConfirmAll();
        await rig.Move();
        Assert.Equal(OnlineCombatFrameVerdict.Malformed, rig.Feed(new CombatKit.ServerFrame(CombatKit.Cmd, 48, new byte[] { 0 })));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(new CombatKit.ServerFrame(CombatKit.Cmd, 48, new byte[] { 1, 201 })));
        Assert.True(m.MoveInFlight);
        Assert.Equal(1, m.Malformed);
        Assert.Equal(2, m.PassthroughCount);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(RoomKit.MoveRefusal(201)));
        Assert.False(m.MoveInFlight);
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(RoomKit.MoveRefusal(201)));      // 又没有在途的了
        Assert.Equal(1, m.MoveRefusals);
    }

    // ======== 成功换房:换表 ========

    /// <summary>
    /// 新房间的起始地图通知到了:对应表整体换成新房间的(本地编号重新从 0 起,服务端编号是新的),房间序号加一,地图换成新的;
    /// 旧表里的怪对象保持各自最后的状态;累计计数不清;握手从头开始。
    /// </summary>
    [Fact]
    public async Task A_successful_move_swaps_the_table_and_restarts_the_local_numbers()
    {
        var rig = new RoomRig(3);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        OnlineCombatMonster[] oldTable = m.Monsters.ToArray();
        await rig.Move(4, 4);

        OnlineCombatFrameVerdict verdict = rig.Feed(RoomKit.StartMap(SecondRoom, 4, 4));

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, verdict);
        Assert.Equal(1, m.Room);
        Assert.False(m.MoveInFlight);
        Assert.Equal(2, m.Monsters.Count);
        Assert.Equal(SecondRoom, m.Monsters.Select(x => (int)x.ServerId).ToArray());
        Assert.Equal(new[] { 0, 1 }, m.Monsters.Select(x => x.LocalId).ToArray());
        Assert.All(m.Monsters, x => Assert.Equal(1, x.Room));
        Assert.All(m.Monsters, x => Assert.Equal(OnlineCombatMonsterState.Alive, x.State));
        Assert.Equal(4, m.Map.CellX);
        Assert.Equal(4, m.Map.CellY);
        Assert.Equal(2, m.Map.Monsters.Count);
        Assert.NotSame(rig.Map, m.Map);
        Assert.All(oldTable, x => Assert.Equal(OnlineCombatMonsterState.Confirmed, x.State));
        Assert.All(oldTable, x => Assert.Equal(0, x.Room));
        Assert.Equal(5, m.Total);                                                // 各房间合计
        Assert.Equal(3, m.Sent);
        Assert.Equal(3, m.Confirmed);
        Assert.Equal(0, m.PendingConfirmations);
        Assert.Equal(0, m.RoomRefusals);
        Assert.Equal(OnlineRoomLoadState.ReportDue, m.LoadState);
        Assert.False(m.RoomCleared);
        Assert.False(m.AllKillsReported);

        OnlineCombatEvent e = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.RoomEntered, e.Kind);
        Assert.Equal(1, e.Room);
        Assert.Equal(2, e.RoomMonsters);
        Assert.Equal(5, e.Total);
        Assert.True(e.TrailingBytes >= 0, "读完起始地图之后载荷里剩的字节数要记下来,真服复验时对账");
    }

    /// <summary>换表之后击杀报的是新表的编号;旧编号的确认成了"未知编号"、不推进;新表没有的本地编号被拦下。</summary>
    [Fact]
    public async Task After_the_swap_kills_use_the_new_table_numbers()
    {
        var rig = new RoomRig(3);
        OnlineDungeonCombat m = rig.Machine;
        await rig.MoveToNextRoom(SecondRoom);
        await rig.Handshake();

        OnlineCombatSendResult first = await rig.Kill(0);
        Assert.True(first.Sent);
        KillView v = KillView.Parse(rig.Port.Sent.Last().Payload);
        Assert.Equal(9300, v.MobId);                                             // 新表的第 0 只,不是旧表的 9100
        Assert.Equal(OnlineCombatFrameVerdict.UnknownNumber, rig.Confirm(9100)); // 旧编号的确认:新表里没有
        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[0].State);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Confirm(9300));
        Assert.Equal(OnlineCombatMonsterState.Confirmed, m.Monsters[0].State);

        Assert.True((await rig.Kill(1)).Sent);
        Assert.Equal(9301, KillView.Parse(rig.Port.Sent.Last().Payload).MobId);

        OnlineCombatSendResult beyond = await rig.Kill(2);                       // 旧表有第 2 只,新表没有
        Assert.Equal(OnlineCombatSendOutcome.Blocked, beyond.Outcome);
        Assert.Equal(OnlineCombatBlock.UnknownLocalId, beyond.Block);
        Assert.Equal(1, m.UnknownNumbers);
    }

    /// <summary>
    /// 绑定了房间的击杀:房间对不上就在本地拦下,不查表、不发包、事件里没有服务端编号——
    /// 防止换房前后序号错位、把旧房间的请求报到新房间的怪身上。
    /// </summary>
    [Fact]
    public async Task A_kill_bound_to_another_room_never_reaches_the_wire()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;

        OnlineCombatSendResult future = await m.ReportKillAsync(1, 0);           // 房间 1 还没到
        Assert.Equal(OnlineCombatSendOutcome.Blocked, future.Outcome);
        Assert.Equal(OnlineCombatBlock.StaleRoom, future.Block);
        Assert.Empty(rig.Port.Sent);
        OnlineCombatEvent blocked = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.KillBlocked, blocked.Kind);
        Assert.Equal(0, blocked.LocalId);
        Assert.Equal(-1, blocked.ServerId);

        Assert.True((await m.ReportKillAsync(0, 0)).Sent);                       // 绑当前房间:正常发
        Assert.True((await m.ReportKillAsync(0, 1)).Sent);
        rig.Confirm(rig.Ids[0]);
        rig.Confirm(rig.Ids[1]);
        await rig.Move();
        rig.Feed(RoomKit.StartMap(SecondRoom, 4, 4));
        await rig.Handshake();

        int killsBefore = rig.Commands(42);
        OnlineCombatSendResult past = await m.ReportKillAsync(0, 1);             // 请求是在旧房间排的,序号 1 在新表里是 9301
        Assert.Equal(OnlineCombatBlock.StaleRoom, past.Block);
        Assert.Equal(killsBefore, rig.Commands(42));
        Assert.Equal(OnlineCombatMonsterState.Alive, m.Monsters[1].State);       // 新房间的怪没被碰
        Assert.True((await m.ReportKillAsync(1, 1)).Sent);
        Assert.Equal(9301, KillView.Parse(rig.Port.Sent.Last().Payload).MobId);
    }

    /// <summary>没有在途换房命令时来的起始地图通知不算在包集合里:只计数透传,表和房间都不动。</summary>
    [Fact]
    public async Task A_start_map_notice_is_only_taken_while_a_move_is_in_flight()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;
        IReadOnlyList<OnlineCombatMonster> table = m.Monsters;

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(RoomKit.StartMap(SecondRoom, 4, 4)));

        Assert.Equal(0, m.Room);
        Assert.Same(table, m.Monsters);
        Assert.Equal(1, m.PassthroughCount);
        OnlineCombatPacketCount counted = Assert.Single(m.Passthrough);
        Assert.Equal((PacketClass.Noti, 29, 1), (counted.PacketClass, counted.PacketId, counted.Count));

        await rig.MoveToNextRoom(new[] { 9300 });
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(RoomKit.StartMap(new[] { 9400 }, 5, 4)));   // 进了房间之后再来一份:同样透传
        Assert.Equal(1, m.Room);
        Assert.Equal(9300, m.Monsters[0].ServerId);
    }

    /// <summary>起始地图通知读不下来:记畸形、不换表、换房命令仍在途;之后好的那份照常被接受。</summary>
    [Fact]
    public async Task A_start_map_notice_that_cannot_be_read_keeps_the_move_in_flight()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        await rig.Move();

        Assert.Equal(OnlineCombatFrameVerdict.Malformed,
            rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 29, DungeonEntryWire.BrokenMap29())));

        Assert.Equal(1, m.Malformed);
        Assert.Equal(0, m.Room);
        Assert.True(m.MoveInFlight);
        Assert.Equal(OnlineCombatEventKind.FrameIgnored, m.Events.Last().Kind);
        Assert.Equal(OnlineCombatFrameVerdict.Malformed, m.Events.Last().Verdict);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(RoomKit.StartMap(SecondRoom, 4, 4)));
        Assert.Equal(1, m.Room);
        Assert.False(m.MoveInFlight);
    }

    /// <summary>换房命令在途时清图通知先到(最后一间被别人清了):闸门关死,随后到的起始地图通知记乱序、不换表。</summary>
    [Fact]
    public async Task A_start_map_notice_after_the_clear_notice_is_out_of_order()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        await rig.Move();
        rig.Feed(CombatKit.Clear());

        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Feed(RoomKit.StartMap(SecondRoom, 4, 4)));

        Assert.Equal(0, m.Room);
        Assert.Equal(1, m.OutOfOrder);
        Assert.Equal(OnlineCombatPhase.Cleared, m.Phase);
        Assert.True((await m.RequestSettlementAsync()).Sent);
    }

    /// <summary>新房间没有怪(起始地图通知里 0 条记录):空真,握手完成后可以立刻继续往下换。</summary>
    [Fact]
    public async Task A_room_that_arrives_empty_can_be_left_right_after_the_handshake()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.MoveToNextRoom(new int[0]);

        Assert.Empty(m.Monsters);
        Assert.False(m.HasMonsters);
        Assert.True(m.RoomCleared);
        Assert.Equal(OnlineCombatBlock.LoadPending, m.MoveGate());
        await rig.Handshake();
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());
        Assert.True((await rig.Move(5, 4)).Sent);
        Assert.Equal(1, m.Events.Last().Room);
    }

    /// <summary>新房间的怪表里服务端编号有重复:计入累计的 <see cref="OnlineDungeonCombat.DuplicateNumbers"/>,重复的怪不参与清空判断。</summary>
    [Fact]
    public async Task Duplicate_numbers_in_a_later_room_are_counted_and_do_not_hold_the_gate()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        await rig.Move();
        rig.Feed(RoomKit.StartMap(new[] { 9300, 9301, 9300 }, 4, 4));
        await rig.Handshake();

        Assert.Equal(1, m.DuplicateNumbers);
        Assert.True(m.Monsters[2].DuplicateNumber);
        Assert.Equal(4, m.Total);
        await rig.Kill(0);
        rig.Confirm(9300);
        await rig.Kill(1);
        rig.Confirm(9301);
        Assert.True(m.RoomCleared);
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());
    }

    // ======== 装载握手 ========

    /// <summary>
    /// 进了新房间:状态机要求发装载完成命令(类 1、包号 40,8 字节);发出前后换房都被 <see cref="OnlineCombatBlock.LoadPending"/> 拦着
    /// (排在"当前房间没清"之前);装载完成通知到了才完成握手;命令只发一次。
    /// </summary>
    [Fact]
    public async Task Entering_a_room_asks_for_one_loaded_report_and_then_waits_for_the_notice()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.MoveToNextRoom(SecondRoom);
        Assert.Equal(OnlineRoomLoadState.ReportDue, m.LoadState);
        Assert.True(m.LoadPending);
        Assert.Equal(OnlineCombatBlock.LoadPending, m.MoveGate());               // 怪都还活着,但握手关口排在前面
        int commandsBefore = rig.Port.Sent.Count;

        OnlineCombatSendResult sent = await m.ReportRoomLoadedAsync();

        Assert.True(sent.Sent);
        Assert.Equal(OnlineRoomLoadState.AwaitingNotice, m.LoadState);
        Assert.Equal(1, m.LoadReports);
        Assert.Equal(commandsBefore + 1, rig.Port.Sent.Count);
        (PacketClass cls, ushort id, byte[] payload) = rig.Port.Sent.Last();
        Assert.Equal(PacketClass.Cmd, cls);
        Assert.Equal(40, id);
        Assert.Equal(new byte[8], payload);
        Assert.Equal(0, RoomLoadedView.Parse(payload).First);
        Assert.Equal(OnlineCombatEventKind.RoomLoadedSent, m.Events.Last().Kind);
        Assert.Equal(1, m.Events.Last().Room);
        Assert.Equal(OnlineCombatBlock.LoadPending, m.MoveGate());               // 命令发了、通知没到:还拦着

        OnlineCombatSendResult again = await m.ReportRoomLoadedAsync();          // 不重复发
        Assert.Equal(OnlineCombatSendOutcome.Blocked, again.Outcome);
        Assert.Equal(OnlineCombatBlock.NoLoadPending, again.Block);
        Assert.Equal(commandsBefore + 1, rig.Port.Sent.Count);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(RoomKit.Loaded()));
        Assert.Equal(OnlineRoomLoadState.None, m.LoadState);
        Assert.False(m.LoadPending);
        Assert.Equal(OnlineCombatEventKind.RoomLoaded, m.Events.Last().Kind);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, m.MoveGate());            // 握手关口放开,剩下的是"没清"
    }

    /// <summary>没有等着报告的握手时,装载完成命令一律拦下不发包:还没换过房、已报过、已完成。</summary>
    [Fact]
    public async Task The_loaded_report_is_blocked_when_nothing_is_due()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;

        OnlineCombatSendResult beforeAnyMove = await m.ReportRoomLoadedAsync();
        Assert.Equal(OnlineCombatSendOutcome.Blocked, beforeAnyMove.Outcome);
        Assert.Equal(OnlineCombatBlock.NoLoadPending, beforeAnyMove.Block);
        Assert.Empty(rig.Port.Sent);
        OnlineCombatEvent e = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.RoomLoadedBlocked, e.Kind);
        Assert.Equal(OnlineCombatBlock.NoLoadPending, e.Block);

        await rig.MoveToNextRoom(SecondRoom);
        await rig.Handshake();
        OnlineCombatSendResult afterDone = await m.ReportRoomLoadedAsync();
        Assert.Equal(OnlineCombatBlock.NoLoadPending, afterDone.Block);
        Assert.Equal(1, rig.Commands(40));
        Assert.Equal(2, m.Blocked);
    }

    /// <summary>装载完成通知只在等它的时候才算数:没有握手、命令还没发、已完成之后来的,都只计数透传,不改状态。</summary>
    [Fact]
    public async Task A_loaded_notice_nobody_waits_for_is_passed_through()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(RoomKit.Loaded()));          // 没换过房
        await rig.MoveToNextRoom(SecondRoom);
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(RoomKit.Loaded()));          // 命令还没发
        Assert.Equal(OnlineRoomLoadState.ReportDue, m.LoadState);

        await rig.Handshake();
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(RoomKit.Loaded()));          // 已经完成
        Assert.Equal(OnlineRoomLoadState.None, m.LoadState);
        Assert.Equal(3, m.PassthroughCount);
        Assert.Equal(0, m.Duplicates + m.OutOfOrder + m.Malformed);
        OnlineCombatPacketCount counted = Assert.Single(m.Passthrough);
        Assert.Equal((PacketClass.Noti, 30, 3), (counted.PacketClass, counted.PacketId, counted.Count));
    }

    /// <summary>选项关掉房间握手:换房之后没有任何东西在等,装载完成命令被拦,换房闸门只剩"没清"。</summary>
    [Fact]
    public async Task With_the_room_handshake_switched_off_nothing_waits_after_a_move()
    {
        var rig = new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { AcknowledgeRoomLoaded = false });
        OnlineDungeonCombat m = rig.Machine;
        await rig.MoveToNextRoom(SecondRoom);

        Assert.Equal(OnlineRoomLoadState.None, m.LoadState);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, m.MoveGate());
        OnlineCombatSendResult blocked = await m.ReportRoomLoadedAsync();
        Assert.Equal(OnlineCombatBlock.NoLoadPending, blocked.Block);
        Assert.Equal(0, rig.Commands(40));
        await rig.KillAndConfirmAll();
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());
    }

    /// <summary>起始房间缺省不握手(单图行为逐字节不变);选项打开才要求,且需要房间握手总开关也开着。</summary>
    [Fact]
    public async Task The_start_room_handshake_is_off_by_default_and_opt_in()
    {
        Assert.Equal(OnlineRoomLoadState.None, new RoomRig(1).Machine.LoadState);

        var on = new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { AcknowledgeStartRoomLoaded = true });
        Assert.Equal(OnlineRoomLoadState.ReportDue, on.Machine.LoadState);
        Assert.Equal(OnlineCombatBlock.LoadPending, on.Machine.MoveGate());
        await on.Handshake();
        Assert.Equal(1, on.Commands(40));
        Assert.Equal(0, on.Machine.Room);
        Assert.Equal(OnlineCombatBlock.RoomNotCleared, on.Machine.MoveGate());

        var masterOff = new RoomRig(CombatKit.Sequential(1),
            new OnlineDungeonCombatOptions { AcknowledgeStartRoomLoaded = true, AcknowledgeRoomLoaded = false });
        Assert.Equal(OnlineRoomLoadState.None, masterOff.Machine.LoadState);
    }

    /// <summary>装载完成命令帧层失败:状态留在"该发了",计入拦下,可以重试;换房闸门仍被握手拦着。</summary>
    [Fact]
    public async Task A_failed_loaded_report_leaves_the_handshake_due_for_a_retry()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.MoveToNextRoom(SecondRoom);
        int before = rig.Port.Sent.Count;
        rig.Port.StatusOfSend = index => index == before ? FrameStatus.EncryptFailed : FrameStatus.Ok;

        OnlineCombatSendResult failed = await m.ReportRoomLoadedAsync();

        Assert.Equal(OnlineCombatSendOutcome.FrameFailed, failed.Outcome);
        Assert.Equal(FrameStatus.EncryptFailed, failed.FrameStatus);
        Assert.Equal(OnlineRoomLoadState.ReportDue, m.LoadState);
        Assert.Equal(0, m.LoadReports);
        Assert.Equal(1, m.Blocked);
        Assert.Equal(OnlineCombatEventKind.RoomLoadedBlocked, m.Events.Last().Kind);
        Assert.Equal(OnlineCombatBlock.FrameFailed, m.Events.Last().Block);

        Assert.True((await m.ReportRoomLoadedAsync()).Sent);
        Assert.Equal(OnlineRoomLoadState.AwaitingNotice, m.LoadState);
    }

    // ======== 发送失败与异常 ========

    [Fact]
    public async Task A_failed_move_leaves_the_machine_ready_to_retry()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        int before = rig.Port.Sent.Count;
        rig.Port.StatusOfSend = index => index == before ? FrameStatus.FrameTooLarge : FrameStatus.Ok;

        OnlineCombatSendResult failed = await rig.Move();

        Assert.Equal(OnlineCombatSendOutcome.FrameFailed, failed.Outcome);
        Assert.Equal(FrameStatus.FrameTooLarge, failed.FrameStatus);
        Assert.False(m.MoveInFlight);
        Assert.Equal(0, m.MovesSent);
        Assert.Equal(1, m.Blocked);
        Assert.Equal(OnlineCombatEventKind.RoomMoveBlocked, m.Events.Last().Kind);
        Assert.Equal(OnlineCombatBlock.FrameFailed, m.Events.Last().Block);
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());

        Assert.True((await rig.Move()).Sent);
        Assert.True(m.MoveInFlight);
    }

    [Fact]
    public async Task A_send_exception_leaves_the_move_and_loaded_states_unchanged()
    {
        var rig = new RoomRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.KillAndConfirmAll();
        rig.Port.FailureOfSend = index => new IOException("synthetic 900");

        await Assert.ThrowsAsync<IOException>(() => rig.Move());
        Assert.False(m.MoveInFlight);
        Assert.Equal(0, m.MovesSent);
        Assert.Equal(OnlineCombatBlock.None, m.MoveGate());

        rig.Port.FailureOfSend = index => null;
        await rig.Move();
        rig.Feed(RoomKit.StartMap(SecondRoom, 4, 4));
        rig.Port.FailureOfSend = index => new IOException("synthetic 901");
        await Assert.ThrowsAsync<IOException>(() => m.ReportRoomLoadedAsync());
        Assert.Equal(OnlineRoomLoadState.ReportDue, m.LoadState);
        Assert.Equal(0, m.LoadReports);
    }

    // ======== 载荷工厂 ========

    /// <summary>自定的换房载荷工厂生效(能放座位项等);长度不对的载荷在发出前就被拒,什么都没发、状态不变。</summary>
    [Fact]
    public async Task The_move_payload_factory_reaches_the_wire_and_must_return_the_exact_length()
    {
        var options = new OnlineDungeonCombatOptions
        {
            MovePayload = request => OnlineCombatPayloads.BuildMoveRoom(request.TargetX, request.TargetY, request.PositionX, request.PositionY,
                request.Layered, 7 * 130, new ushort[] { 911, 912, 913, 914 }, new uint[] { 1, 2, 3, 4 }, 931, 941),
        };
        var rig = new RoomRig(CombatKit.Sequential(1), options);
        await rig.KillAndConfirmAll();

        Assert.True((await rig.Move(4, 3, 901, 902)).Sent);

        MoveRoomView v = MoveRoomView.Parse(rig.Port.Sent.Last().Payload);
        Assert.Equal(4, v.TargetX);
        Assert.Equal(3, v.TargetY);
        Assert.Equal(901, v.PositionX);
        Assert.Equal(7 * 130, v.TrapBits);
        Assert.Equal(new ushort[] { 911, 912, 913, 914 }, v.Seats);
        Assert.Equal(941, v.Tail);

        foreach (Func<OnlineMoveRoomRequest, byte[]> bad in new Func<OnlineMoveRoomRequest, byte[]>[] { r => null, r => new byte[42], r => new byte[44] })
        {
            var rig2 = new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { MovePayload = bad });
            await rig2.KillAndConfirmAll();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig2.Move());
            Assert.DoesNotContain(rig2.Port.Sent, s => s.Id == 48);
            Assert.False(rig2.Machine.MoveInFlight);
            Assert.Equal(0, rig2.Machine.MovesSent);
        }
    }

    [Fact]
    public async Task The_loaded_payload_factory_reaches_the_wire_and_must_return_the_exact_length()
    {
        var rig = new RoomRig(CombatKit.Sequential(1),
            new OnlineDungeonCombatOptions { LoadedPayload = () => OnlineCombatPayloads.BuildRoomLoaded(901, 902) });
        await rig.MoveToNextRoom(SecondRoom);

        Assert.True((await rig.Machine.ReportRoomLoadedAsync()).Sent);

        RoomLoadedView v = RoomLoadedView.Parse(rig.Port.Sent.Last().Payload);
        Assert.Equal(901, v.First);
        Assert.Equal(902, v.Second);

        var bad = new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { LoadedPayload = () => new byte[7] });
        await bad.MoveToNextRoom(SecondRoom);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.Machine.ReportRoomLoadedAsync());
        Assert.Equal(OnlineRoomLoadState.ReportDue, bad.Machine.LoadState);
        Assert.Equal(0, bad.Port.Sent.Count(s => s.Id == 40));
    }

    [Fact]
    public async Task A_missing_request_is_refused_and_an_unusable_budget_is_refused()
    {
        var rig = new RoomRig(1);
        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Machine.RequestMoveRoomAsync(null));
        Assert.Empty(rig.Port.Sent);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { MaxMoveRefusalsPerRoom = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomRig(CombatKit.Sequential(1), new OnlineDungeonCombatOptions { MaxMoveRefusalsPerRoom = -1 }));
    }

    // ======== 整条事件链 ========

    /// <summary>
    /// 两间房一路走完的整条事件链(状态机层):杀光第一间 → 换房被拒 → 再换成功 → 换表 → 握手 → 杀光第二间 → 清图通知 → 结算两段 → 回城。
    /// 一次比对全部,顺序错一处都会露出来。
    /// </summary>
    [Fact]
    public async Task The_event_trace_of_a_two_room_walk_is_exactly_the_documented_chain()
    {
        var rig = new RoomRig(2);
        OnlineDungeonCombat m = rig.Machine;

        await rig.KillAndConfirmAll();
        await rig.Move();
        rig.Feed(RoomKit.MoveRefusal(201));
        await rig.Move();
        rig.Feed(RoomKit.StartMap(new[] { 9300 }, 4, 4));
        await rig.Handshake();
        await rig.Kill(0);
        rig.Confirm(9300);
        rig.Feed(CombatKit.Clear());
        await m.RequestSettlementAsync();
        rig.Feed(CombatKit.Result());
        rig.Feed(CombatKit.Reward());

        Assert.Equal(
            "Spawned KillSent:0 KillConfirmed:0 KillSent:1 KillConfirmed:1 " +
            "MoveSent:r0 MoveRefused:r0:code201 MoveSent:r0 RoomEntered:r1:m1 LoadedSent:r1 RoomLoaded:r1 " +
            "KillSent:0 KillConfirmed:0 MapCleared SettlementSent SettlementArrived:Result SettlementArrived:Reward ReadyToReturnToTown",
            CombatKit.Trace(m.Events));
        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);
        Assert.Equal(new[] { 42, 42, 48, 48, 40, 42, 49 }, rig.Wire());
        Assert.Equal((3, 3, 3), (m.Total, m.Sent, m.Confirmed));
        Assert.Equal(0, m.Blocked);
    }
}
