using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 战斗状态机(<see cref="OnlineDungeonCombat"/>)本身的测试:全程不碰会话,命令走记录用的发送口,服务端帧由测试逐帧喂进 <c>OnFrame</c>。
/// 数据全是合成的:怪编号 9100 起逐个加一(或另给的合成表),记录其余各项与编号、序号都不同;没有任何真实编号。
/// 读回命令载荷用独立的手写读法(<see cref="KillView"/>),不复用产品代码的拼装。
/// </summary>
public class OnlineDungeonCombatTests
{
    // ======== 主链 ========

    /// <summary>
    /// 主链:5 只怪的合成起始地图,不按线上顺序逐只击杀(每只的命令里怪编号必须是它自己记录的第 3 项),每只报完立刻试一次结算(闸门关着,必须被拦)、
    /// 再收确认;全部确认后清图通知没到闸门照旧关着;清图通知到了才放行;结算命令发出后两段通知按序到齐才可以回城。整条事件链一次比对。
    /// </summary>
    [Fact]
    public async Task The_full_chain_runs_from_the_start_map_to_ready_to_return()
    {
        var rig = new CombatRig(5);
        OnlineDungeonCombat m = rig.Machine;
        Assert.Equal(OnlineCombatPhase.Fighting, m.Phase);
        Assert.Equal(5, m.Total);
        Assert.True(m.HasMonsters);

        foreach (int local in new[] { 3, 0, 4, 1, 2 })
        {
            KillView sent = await rig.KillAndRead(local);
            Assert.Equal(rig.Ids[local], sent.MobId);
            Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[local].State);

            OnlineCombatSendResult early = await rig.Settle();
            Assert.Equal(OnlineCombatSendOutcome.Blocked, early.Outcome);
            Assert.Equal(OnlineCombatBlock.NotCleared, early.Block);

            Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Confirm(rig.Ids[local]));
            Assert.Equal(OnlineCombatMonsterState.Confirmed, m.Monsters[local].State);
        }

        Assert.Equal(5, rig.Port.KillCommands);
        Assert.Equal(0, rig.Port.SettlementCommands);
        Assert.Equal(5, m.Sent);
        Assert.Equal(5, m.Confirmed);
        Assert.Equal(0, m.PendingConfirmations);
        Assert.True(m.AllKillsReported);
        Assert.Equal(OnlineCombatPhase.Fighting, m.Phase);            // 全确认了,清图通知没到:闸门照旧关着

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Clear());
        Assert.Equal(OnlineCombatPhase.Cleared, m.Phase);
        Assert.Equal(0, rig.Port.SettlementCommands);                  // 通知到了也不自己发:发不发由调用方定

        OnlineCombatSendResult settle = await rig.Settle();
        Assert.True(settle.Sent);
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(1, rig.Port.SettlementCommands);
        (PacketClass cls, ushort id, byte[] payload) = rig.Port.Sent[rig.Port.Sent.Count - 1];
        Assert.Equal(PacketClass.Cmd, cls);
        Assert.Equal(49, id);
        Assert.Equal(33, payload.Length);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Result());
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);             // 只到一半
        Assert.Equal(0, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Reward());
        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));

        Assert.Equal(
            "Spawned " +
            "KillSent:3 SettlementBlocked:NotCleared KillConfirmed:3 " +
            "KillSent:0 SettlementBlocked:NotCleared KillConfirmed:0 " +
            "KillSent:4 SettlementBlocked:NotCleared KillConfirmed:4 " +
            "KillSent:1 SettlementBlocked:NotCleared KillConfirmed:1 " +
            "KillSent:2 SettlementBlocked:NotCleared KillConfirmed:2 " +
            "MapCleared SettlementSent SettlementArrived:Result SettlementArrived:Reward ReadyToReturnToTown",
            CombatKit.Trace(m.Events));
    }

    /// <summary>每一条击杀命令的载荷:怪编号对,没有击杀者,0 条攻击记录,被动物件标志 0,其余全零,共 54 字节。</summary>
    [Fact]
    public async Task Every_kill_command_has_the_documented_default_shape()
    {
        var rig = new CombatRig(4);
        for (int i = 0; i < 4; i++)
        {
            KillView v = await rig.KillAndRead(i);
            Assert.Equal(54, v.Length);
            Assert.Equal(rig.Ids[i], v.MobId);
            Assert.Equal(0xFFFF, v.Killer);
            Assert.Equal(0, v.AttackCount);
            Assert.Equal(0, v.Passive);
            Assert.All(v.Secure, s => Assert.Equal(0, s));
            Assert.All(v.TailAfterPassive, b => Assert.Equal(0, b));
        }
    }

    // ======== 对应表 ========

    [Fact]
    public void The_table_is_built_from_the_record_order_with_the_third_field_as_the_server_number()
    {
        int[] ids = { 9107, 9101, 9150, 9103, 9199 };
        var rig = new CombatRig(ids);

        Assert.Equal(5, rig.Machine.Monsters.Count);
        for (int i = 0; i < ids.Length; i++)
        {
            OnlineCombatMonster mon = rig.Machine.Monsters[i];
            Assert.Equal(i, mon.LocalId);
            Assert.Equal(ids[i], mon.ServerId);
            Assert.Equal(ids[i], mon.Record.C);
            Assert.Equal(rig.Map.Monsters[i], mon.Record);
            Assert.False(mon.DuplicateNumber);
            Assert.Equal(OnlineCombatMonsterState.Alive, mon.State);
        }
        Assert.Equal(0, rig.Machine.DuplicateNumbers);
        Assert.Equal("Spawned", CombatKit.Trace(rig.Machine.Events));
        OnlineCombatEvent spawned = rig.Machine.Events.Single();
        Assert.Equal(5, spawned.Total);
        Assert.Equal(0, spawned.Sent);
        Assert.Equal(0, spawned.Confirmed);
    }

    /// <summary>
    /// 编号表不是单调的(逐条各不相同):每只怪的命令里放的是它自己那条记录的第 3 项,不是本地序号、不是记录的别的项、也不是"第几次击杀"。
    /// 夹具自检保证别的项都不等于编号,所以任何取错项或取序号的写法都会在这里露馅。
    /// </summary>
    [Fact]
    public async Task Each_kill_command_carries_the_third_field_of_its_own_record()
    {
        int[] ids = { 9107, 9101, 9150, 9103, 9199 };
        var rig = new CombatRig(ids);

        for (int i = 0; i < ids.Length; i++)
        {
            StartMapMonsterRecord r = rig.Map.Monsters[i];
            Assert.Equal(ids[i], r.C);
            Assert.NotEqual(ids[i], i);
            Assert.NotEqual(ids[i], (int)r.A);
            Assert.NotEqual(ids[i], r.B);
            Assert.NotEqual(ids[i], r.D);
            Assert.NotEqual(ids[i], (int)r.E);
            Assert.NotEqual(ids[i], (int)r.F);
            Assert.NotEqual(ids[i], (int)r.G);
            Assert.NotEqual(ids[i], (int)r.H);
        }

        // 倒着报,免得"按报的先后取"与"按本地序号取"碰巧一致
        for (int i = ids.Length - 1; i >= 0; i--)
        {
            KillView sent = await rig.KillAndRead(i);
            Assert.Equal(ids[i], sent.MobId);
        }
        Assert.Equal(ids.OrderBy(x => x).ToArray(), rig.Port.Sent.Select(s => (int)KillView.Parse(s.Payload).MobId).OrderBy(x => x).ToArray());
    }

    /// <summary>反向查表:收到某个编号的确认,恰好记到那一只怪上,邻居不动。</summary>
    [Fact]
    public async Task The_reverse_lookup_credits_exactly_the_monster_whose_number_arrived()
    {
        var rig = new CombatRig(5);
        OnlineDungeonCombat m = rig.Machine;
        await rig.Kill(2);
        await rig.Kill(3);
        await rig.Kill(4);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Confirm(rig.Ids[3]));

        Assert.Equal(OnlineCombatMonsterState.Alive, m.Monsters[0].State);
        Assert.Equal(OnlineCombatMonsterState.Alive, m.Monsters[1].State);
        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[2].State);
        Assert.Equal(OnlineCombatMonsterState.Confirmed, m.Monsters[3].State);
        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[4].State);
        OnlineCombatEvent confirmed = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.KillConfirmed, confirmed.Kind);
        Assert.Equal(3, confirmed.LocalId);
        Assert.Equal(rig.Ids[3], confirmed.ServerId);
    }

    // ======== 本地拦截 ========

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task A_local_id_outside_the_table_is_blocked_and_nothing_goes_out(int local)
    {
        var rig = new CombatRig(5);
        OnlineDungeonCombat m = rig.Machine;

        OnlineCombatSendResult r = await rig.Kill(local);

        Assert.Equal(OnlineCombatSendOutcome.Blocked, r.Outcome);
        Assert.Equal(OnlineCombatBlock.UnknownLocalId, r.Block);
        Assert.False(r.Sent);
        Assert.Empty(rig.Port.Sent);
        Assert.Equal(1, m.Blocked);
        Assert.Equal(0, m.Sent);
        Assert.All(m.Monsters, mon => Assert.Equal(OnlineCombatMonsterState.Alive, mon.State));
        OnlineCombatEvent e = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.KillBlocked, e.Kind);
        Assert.Equal(local, e.LocalId);
        Assert.Equal(-1, e.ServerId);
        Assert.Equal(OnlineCombatBlock.UnknownLocalId, e.Block);

        Assert.True((await rig.Kill(0)).Sent);                         // 拦下之后正常的击杀照常
        Assert.Equal(1, rig.Port.KillCommands);
    }

    [Fact]
    public async Task A_monster_is_reported_at_most_once()
    {
        var rig = new CombatRig(3);
        OnlineDungeonCombat m = rig.Machine;

        Assert.True((await rig.Kill(1)).Sent);
        OnlineCombatSendResult again = await rig.Kill(1);
        Assert.Equal(OnlineCombatSendOutcome.Blocked, again.Outcome);
        Assert.Equal(OnlineCombatBlock.AlreadyReported, again.Block);
        Assert.Equal(1, rig.Port.KillCommands);

        rig.Confirm(rig.Ids[1]);
        OnlineCombatSendResult afterConfirm = await rig.Kill(1);
        Assert.Equal(OnlineCombatBlock.AlreadyReported, afterConfirm.Block);
        Assert.Equal(1, rig.Port.KillCommands);
        Assert.Equal(2, m.Blocked);
        Assert.Equal(1, m.Sent);
    }

    [Fact]
    public async Task Kills_are_closed_once_the_settlement_command_is_out()
    {
        var rig = new CombatRig(3);
        await rig.Kill(0);
        rig.Clear();
        Assert.True((await rig.Settle()).Sent);

        OnlineCombatSendResult late = await rig.Kill(1);

        Assert.Equal(OnlineCombatSendOutcome.Blocked, late.Outcome);
        Assert.Equal(OnlineCombatBlock.PhaseClosed, late.Block);
        Assert.Equal(1, rig.Port.KillCommands);
        Assert.Equal(OnlineCombatMonsterState.Alive, rig.Machine.Monsters[1].State);
    }

    /// <summary>清图通知之后、结算命令之前,漏报的击杀还可以报(服务端那边怎么处置是它的事),确认也照认。</summary>
    [Fact]
    public async Task Late_kills_between_the_clear_notice_and_the_settlement_are_still_reported()
    {
        var rig = new CombatRig(3);
        rig.Clear();

        Assert.True((await rig.Kill(2)).Sent);
        Assert.Equal(1, rig.Port.KillCommands);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Confirm(rig.Ids[2]));
        Assert.Equal(OnlineCombatMonsterState.Confirmed, rig.Machine.Monsters[2].State);
        Assert.Equal(OnlineCombatPhase.Cleared, rig.Machine.Phase);
    }

    [Fact]
    public async Task A_duplicated_number_in_the_map_cannot_be_reported_for_the_second_monster()
    {
        var rig = new CombatRig(new[] { 9100, 9100, 9102 });
        OnlineDungeonCombat m = rig.Machine;
        Assert.Equal(1, m.DuplicateNumbers);
        Assert.False(m.Monsters[0].DuplicateNumber);
        Assert.True(m.Monsters[1].DuplicateNumber);
        Assert.False(m.Monsters[2].DuplicateNumber);

        OnlineCombatSendResult blocked = await rig.Kill(1);
        Assert.Equal(OnlineCombatSendOutcome.Blocked, blocked.Outcome);
        Assert.Equal(OnlineCombatBlock.NoServerNumber, blocked.Block);
        Assert.Empty(rig.Port.Sent);
        Assert.False(m.AllKillsReported);

        Assert.True((await rig.Kill(0)).Sent);
        Assert.True((await rig.Kill(2)).Sent);
        Assert.True(m.AllKillsReported);                               // 重复编号的那只不计
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Confirm(9100));
        Assert.Equal(OnlineCombatMonsterState.Confirmed, m.Monsters[0].State);      // 记到第一只
        Assert.Equal(OnlineCombatMonsterState.Alive, m.Monsters[1].State);
    }

    // ======== 结算闸门 ========

    /// <summary>
    /// 没有收到清图通知,结算命令一律不发:不管本地报了几只、确认了几只(全报全确认也一样),要多少次拦多少次。
    /// 闸门只认服务端的清图通知,不看本地进度。
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task The_settlement_gate_stays_shut_while_no_clear_notice_has_arrived(int kills, bool confirm)
    {
        var rig = new CombatRig(3);
        for (int i = 0; i < kills; i++)
        {
            await rig.Kill(i);
            if (confirm)
            {
                rig.Confirm(rig.Ids[i]);
            }
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            OnlineCombatSendResult r = await rig.Settle();
            Assert.Equal(OnlineCombatSendOutcome.Blocked, r.Outcome);
            Assert.Equal(OnlineCombatBlock.NotCleared, r.Block);
            Assert.False(r.Sent);
        }

        Assert.Equal(0, rig.Port.SettlementCommands);
        Assert.Equal(OnlineCombatPhase.Fighting, rig.Machine.Phase);
        Assert.Equal(3, rig.Machine.Blocked);
        Assert.Equal(3, rig.Count(OnlineCombatEventKind.SettlementBlocked));
    }

    [Fact]
    public async Task The_clear_notice_alone_opens_the_gate()
    {
        var rig = new CombatRig(3);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Clear());

        Assert.Equal(OnlineCombatPhase.Cleared, rig.Machine.Phase);
        Assert.Equal(0, rig.Machine.Sent);                             // 服务端说了算,本地进度不拦
        Assert.True((await rig.Settle()).Sent);
        Assert.Equal(1, rig.Port.SettlementCommands);
    }

    [Fact]
    public async Task The_settlement_command_goes_out_once_after_the_clear_notice()
    {
        var rig = new CombatRig(2);
        rig.Clear();

        Assert.True((await rig.Settle()).Sent);
        OnlineCombatSendResult second = await rig.Settle();
        Assert.Equal(OnlineCombatSendOutcome.Blocked, second.Outcome);
        Assert.Equal(OnlineCombatBlock.SettlementAlreadySent, second.Block);

        (PacketClass cls, ushort id, byte[] payload) = rig.Port.Sent.Single();
        Assert.Equal(PacketClass.Cmd, cls);
        Assert.Equal(49, id);
        Assert.Equal(new byte[33], payload);

        rig.Result();
        rig.Reward();
        OnlineCombatSendResult third = await rig.Settle();               // 到齐之后再要也不发
        Assert.Equal(OnlineCombatBlock.SettlementAlreadySent, third.Block);
        Assert.Equal(1, rig.Port.SettlementCommands);
    }

    [Fact]
    public async Task A_second_clear_notice_is_a_duplicate_and_never_reopens_a_closed_gate()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Clear());
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Clear());
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.MapCleared));
        Assert.Equal(OnlineCombatPhase.Cleared, m.Phase);

        Assert.True((await rig.Settle()).Sent);
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Clear());     // 结算中再来一次:不能把阶段拉回"已清图"(会重发结算)
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(2, m.Duplicates);
        Assert.False((await rig.Settle()).Sent);
        Assert.Equal(1, rig.Port.SettlementCommands);
    }

    [Fact]
    public async Task The_map_cleared_event_carries_the_progress_at_that_moment()
    {
        var rig = new CombatRig(3);
        await rig.Kill(0);
        await rig.Kill(1);
        rig.Confirm(rig.Ids[0]);

        rig.Clear();

        OnlineCombatEvent cleared = rig.Machine.Events.Single(e => e.Kind == OnlineCombatEventKind.MapCleared);
        Assert.Equal(3, cleared.Total);
        Assert.Equal(2, cleared.Sent);
        Assert.Equal(1, cleared.Confirmed);
    }

    // ======== 确认:乱序、重复、未知、残缺 ========

    [Fact]
    public async Task A_confirmation_before_the_kill_was_reported_is_out_of_order_and_not_remembered()
    {
        var rig = new CombatRig(3);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Confirm(rig.Ids[1]));
        Assert.Equal(OnlineCombatMonsterState.Alive, m.Monsters[1].State);
        Assert.Equal(0, m.Confirmed);
        Assert.Equal(1, m.OutOfOrder);

        Assert.True((await rig.Kill(1)).Sent);
        // 早到的那条没有被缓存:报完击杀仍然在等
        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[1].State);
        Assert.Equal(0, m.Confirmed);
        Assert.Equal(1, m.PendingConfirmations);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Confirm(rig.Ids[1]));
        Assert.Equal(1, m.Confirmed);

        OnlineCombatEvent ignored = m.Events.Single(e => e.Kind == OnlineCombatEventKind.FrameIgnored);
        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, ignored.Verdict);
        Assert.Equal(1, ignored.LocalId);
        Assert.Equal(rig.Ids[1], ignored.ServerId);
        Assert.Equal(PacketClass.Noti, ignored.PacketClass);
        Assert.Equal(0x26, ignored.PacketId);
    }

    [Fact]
    public async Task A_duplicate_confirmation_does_not_advance_twice()
    {
        var rig = new CombatRig(3);
        OnlineDungeonCombat m = rig.Machine;
        await rig.Kill(0);
        await rig.Kill(1);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Confirm(rig.Ids[0]));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Confirm(rig.Ids[0]));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Confirm(rig.Ids[0]));

        Assert.Equal(1, m.Confirmed);
        Assert.Equal(2, m.Duplicates);
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.KillConfirmed));
        Assert.Equal(1, m.PendingConfirmations);                       // 第二只还在等
        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[1].State);
        Assert.Equal(OnlineCombatPhase.Fighting, m.Phase);
    }

    /// <summary>本地序号(0、1、2)与表外的编号都不是起始地图里的编号:忽略,各怪状态不动。序号冒充编号的写法会在这里被抓住。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9099)]
    [InlineData(9103)]
    [InlineData(9999)]
    [InlineData(65535)]
    public async Task A_confirmation_with_a_number_that_is_not_in_the_start_map_is_ignored(int number)
    {
        var rig = new CombatRig(3);            // 编号 9100、9101、9102
        OnlineDungeonCombat m = rig.Machine;
        for (int i = 0; i < 3; i++)
        {
            await rig.Kill(i);
        }

        Assert.Equal(OnlineCombatFrameVerdict.UnknownNumber, rig.Confirm(number));

        Assert.Equal(1, m.UnknownNumbers);
        Assert.Equal(0, m.Confirmed);
        Assert.All(m.Monsters, mon => Assert.Equal(OnlineCombatMonsterState.KillSent, mon.State));
        OnlineCombatEvent ignored = m.Events.Last();
        Assert.Equal(OnlineCombatEventKind.FrameIgnored, ignored.Kind);
        Assert.Equal(OnlineCombatFrameVerdict.UnknownNumber, ignored.Verdict);
        Assert.Equal(-1, ignored.LocalId);
        Assert.Equal(number, ignored.ServerId);
    }

    [Fact]
    public async Task A_confirmation_too_short_to_hold_a_number_is_malformed()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        await rig.Kill(0);

        Assert.Equal(OnlineCombatFrameVerdict.Malformed, m.OnFrame(PacketClass.Noti, 0x26, null));
        Assert.Equal(OnlineCombatFrameVerdict.Malformed, m.OnFrame(PacketClass.Noti, 0x26, new byte[0]));
        Assert.Equal(OnlineCombatFrameVerdict.Malformed, m.OnFrame(PacketClass.Noti, 0x26, new byte[] { 0x8C }));

        Assert.Equal(3, m.Malformed);
        Assert.Equal(0, m.Confirmed);
        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[0].State);
        // 刚好两个字节够用
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, m.OnFrame(PacketClass.Noti, 0x26, new byte[] { 0x8C, 0x23 }));
        Assert.Equal(1, m.Confirmed);
    }

    /// <summary>确认后面拖着会话加密补的零字节(真线上都是块长整数倍)一样认。</summary>
    [Fact]
    public async Task A_confirmation_with_trailing_padding_is_read_the_same()
    {
        var rig = new CombatRig(2);
        await rig.Kill(1);

        byte[] padded = CombatKit.ConfirmPayload(rig.Ids[1]).Concat(new byte[10]).ToArray();
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Machine.OnFrame(PacketClass.Noti, 0x26, padded));
        Assert.Equal(OnlineCombatMonsterState.Confirmed, rig.Machine.Monsters[1].State);
    }

    // ======== 击杀失败回包 ========

    [Fact]
    public async Task A_refusal_marks_the_monster_refused_and_a_later_confirmation_does_not_overturn_it()
    {
        var rig = new CombatRig(3);
        OnlineDungeonCombat m = rig.Machine;
        await rig.Kill(1);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(CombatKit.Refusal(rig.Ids[1])));

        Assert.Equal(OnlineCombatMonsterState.Refused, m.Monsters[1].State);
        Assert.Equal(1, m.Refused);
        Assert.Equal(0, m.Confirmed);
        Assert.Equal(0, m.PendingConfirmations);
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.KillRefused));

        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Confirm(rig.Ids[1]));
        Assert.Equal(OnlineCombatMonsterState.Refused, m.Monsters[1].State);
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(CombatKit.Refusal(rig.Ids[1])));
        Assert.Equal(1, m.Refused);

        OnlineCombatSendResult again = await rig.Kill(1);                // 已经报过(被拒了也算):不重发
        Assert.Equal(OnlineCombatBlock.AlreadyReported, again.Block);
        Assert.Equal(1, rig.Port.KillCommands);
    }

    [Fact]
    public void Refusals_that_do_not_fit_are_ignored()
    {
        var rig = new CombatRig(3);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Feed(CombatKit.Refusal(rig.Ids[0])));       // 还没报就来了失败回包
        Assert.Equal(OnlineCombatFrameVerdict.UnknownNumber, rig.Feed(CombatKit.Refusal(9999)));
        Assert.Equal(OnlineCombatFrameVerdict.Malformed, m.OnFrame(PacketClass.Cmd, 42, new byte[] { 0, 5 }));   // 失败形状却不足 4 字节
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Cmd, 42, new byte[] { 1, 0, 0, 0 }));   // 成功形状不是失败,当无关帧

        Assert.Equal(0, m.Refused);
        Assert.Equal(1, m.OutOfOrder);
        Assert.Equal(1, m.UnknownNumbers);
        Assert.Equal(1, m.Malformed);
        Assert.Equal(1, m.PassthroughCount);
    }

    // ======== 结算通知 ========

    [Fact]
    public async Task Settlement_notices_before_the_command_are_out_of_order_and_not_remembered()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Result());          // 战斗中、清图前
        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Reward());
        rig.Clear();
        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Result());          // 清图后、命令未发
        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Reward());
        Assert.Equal(4, m.OutOfOrder);
        Assert.Equal(OnlineCombatPhase.Cleared, m.Phase);

        Assert.True((await rig.Settle()).Sent);
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);                        // 早到的没被缓存:不会因为先前来过两条就直接算到齐
        Assert.Equal(0, rig.Count(OnlineCombatEventKind.SettlementArrived));
        Assert.Equal(0, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Result());
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Reward());
        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);
    }

    [Fact]
    public async Task The_reward_before_the_result_is_out_of_order_and_not_remembered()
    {
        var rig = new CombatRig(1);
        OnlineDungeonCombat m = rig.Machine;
        rig.Clear();
        await rig.Settle();

        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, rig.Reward());
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Result());
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);                        // 先到的奖励被丢了没缓存:仍差一条
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Reward());
        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);
    }

    [Fact]
    public async Task Duplicate_settlement_notices_are_ignored_and_ready_fires_once()
    {
        var rig = new CombatRig(1);
        OnlineDungeonCombat m = rig.Machine;
        rig.Clear();
        await rig.Settle();

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Result());
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Result());
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Reward());
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Reward());
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Result());

        Assert.Equal(3, m.Duplicates);
        Assert.Equal(2, rig.Count(OnlineCombatEventKind.SettlementArrived));
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));
        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);
        Assert.Equal(OnlineCombatEventKind.ReadyToReturnToTown,
            m.Events.Last(e => e.Kind != OnlineCombatEventKind.FrameIgnored).Kind);       // 终局事件是最后一条真事件
    }

    [Fact]
    public async Task Nothing_moves_after_the_chain_is_complete()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        await rig.FightToClear();
        await rig.Settle();
        rig.Result();
        rig.Reward();
        int events = m.Events.Count;
        int sent = rig.Port.Sent.Count;

        Assert.Equal(OnlineCombatBlock.PhaseClosed, (await rig.Kill(0)).Block);
        Assert.Equal(OnlineCombatBlock.SettlementAlreadySent, (await rig.Settle()).Block);
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Clear());
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Confirm(rig.Ids[0]));

        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);
        Assert.Equal(sent, rig.Port.Sent.Count);
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));
        Assert.True(m.Events.Count > events);                                     // 只多了"被拦"与"被忽略"的记录
    }

    // ======== 空地图口径 ========

    /// <summary>
    /// 空起始地图的口径:没有怪可报,"击杀都已上报"空真,但结算闸门照旧只认服务端的清图通知;不因为"没有怪"就放行。
    /// 本地编号都是未知的;清图通知到了,链照常走完。
    /// </summary>
    [Fact]
    public async Task An_empty_start_map_does_not_open_the_settlement_gate()
    {
        var rig = new CombatRig(new int[0]);
        OnlineDungeonCombat m = rig.Machine;
        Assert.Equal(0, m.Total);
        Assert.False(m.HasMonsters);
        Assert.True(m.AllKillsReported);
        Assert.Equal(OnlineCombatPhase.Fighting, m.Phase);

        OnlineCombatSendResult early = await rig.Settle();
        Assert.Equal(OnlineCombatSendOutcome.Blocked, early.Outcome);
        Assert.Equal(OnlineCombatBlock.NotCleared, early.Block);
        Assert.Equal(OnlineCombatBlock.UnknownLocalId, (await rig.Kill(0)).Block);
        Assert.Empty(rig.Port.Sent);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Clear());
        Assert.True((await rig.Settle()).Sent);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Result());
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Reward());
        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);
        Assert.Equal(
            "Spawned SettlementBlocked:NotCleared KillBlocked:0:UnknownLocalId MapCleared SettlementSent SettlementArrived:Result SettlementArrived:Reward ReadyToReturnToTown",
            CombatKit.Trace(m.Events));
        Assert.Equal(0, m.Events[0].Total);
    }

    /// <summary>起始地图是没有地图体的短形(地图类型 0):解析器给空怪表,状态机的口径与空地图一致。</summary>
    [Fact]
    public async Task A_start_map_without_a_body_behaves_like_an_empty_map()
    {
        var port = new RecordingCombatPort();
        var m = new OnlineDungeonCombat(CombatKit.MapWithoutBody(), port);

        Assert.Equal(0, m.Total);
        Assert.False(m.HasMonsters);
        OnlineCombatSendResult early = await m.RequestSettlementAsync();
        Assert.Equal(OnlineCombatBlock.NotCleared, early.Block);
        Assert.Empty(port.Sent);
    }

    // ======== 包集合之外与类的区分 ========

    [Fact]
    public void Frames_outside_the_chain_are_only_counted_and_passed_through()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(CombatKit.Unrelated(902)));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(CombatKit.Unrelated(901)));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(CombatKit.Unrelated(901)));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(new CombatKit.ServerFrame(CombatKit.Cmd, 905, new byte[] { 1, 2, 3 })));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(CombatKit.Heartbeat()));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Noti, 903, null));

        Assert.Equal(6, m.PassthroughCount);
        Assert.Equal(
            new[] { "Noti/0:1", "Noti/901:2", "Noti/902:1", "Noti/903:1", "Cmd/905:1" },
            m.Passthrough.Select(c => c.PacketClass + "/" + c.PacketId + ":" + c.Count).ToArray());
        Assert.Equal("Spawned", CombatKit.Trace(m.Events));                       // 透传不产事件
        Assert.Equal(OnlineCombatPhase.Fighting, m.Phase);
        Assert.Equal(0, m.OutOfOrder + m.Duplicates + m.UnknownNumbers + m.Malformed);
    }

    /// <summary>类 1 里带通知包号的帧(击杀确认的 0x26、清图的 0x1f)不是通知:不推进任何状态,只当无关帧。</summary>
    [Fact]
    public async Task Command_class_frames_with_notice_numbers_do_not_advance_anything()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        await rig.Kill(0);

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Cmd, 0x26, CombatKit.ConfirmPayload(rig.Ids[0])));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Cmd, 0x1f, new byte[0]));

        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[0].State);
        Assert.Equal(0, m.Confirmed);
        Assert.Equal(OnlineCombatPhase.Fighting, m.Phase);
        Assert.Equal(2, m.PassthroughCount);
    }

    [Fact]
    public async Task Command_class_frames_with_settlement_notice_numbers_do_not_complete_the_chain()
    {
        var rig = new CombatRig(1);
        OnlineDungeonCombat m = rig.Machine;
        rig.Clear();
        await rig.Settle();

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Cmd, 0x22, CombatKit.ResultPayload()));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Cmd, 0x23, CombatKit.RewardPayload()));

        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(0, rig.Count(OnlineCombatEventKind.SettlementArrived));
    }

    /// <summary>类 0 里带命令回包号的帧(42,即 0x2a)不是击杀失败回包:不读成失败。</summary>
    [Fact]
    public async Task Notice_class_frames_with_the_kill_reply_number_are_not_refusals()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        await rig.Kill(0);

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Noti, 42, CombatKit.RefusalPayload(rig.Ids[0])));

        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[0].State);
        Assert.Equal(0, m.Refused);
    }

    // ======== 发送失败 ========

    [Fact]
    public async Task A_frame_failure_leaves_the_state_alone_and_the_kill_can_be_retried()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        rig.Port.StatusOfSend = index => index == 0 ? FrameStatus.EncryptFailed : FrameStatus.Ok;

        OnlineCombatSendResult failed = await rig.Kill(0);

        Assert.Equal(OnlineCombatSendOutcome.FrameFailed, failed.Outcome);
        Assert.Equal(OnlineCombatBlock.FrameFailed, failed.Block);
        Assert.Equal(FrameStatus.EncryptFailed, failed.FrameStatus);
        Assert.False(failed.Sent);
        Assert.Equal(OnlineCombatMonsterState.Alive, m.Monsters[0].State);
        Assert.Equal(0, m.Sent);
        Assert.Equal(1, m.Blocked);
        Assert.Equal("Spawned KillBlocked:0:FrameFailed", CombatKit.Trace(m.Events));

        Assert.True((await rig.Kill(0)).Sent);                                    // 重试成功
        Assert.Equal(OnlineCombatMonsterState.KillSent, m.Monsters[0].State);
        Assert.Equal(1, m.Sent);
        Assert.Equal(2, rig.Port.KillCommands);                                    // 两次尝试都到了发送口
    }

    [Fact]
    public async Task A_settlement_frame_failure_keeps_the_gate_open_for_a_retry()
    {
        var rig = new CombatRig(1);
        OnlineDungeonCombat m = rig.Machine;
        rig.Clear();
        rig.Port.StatusOfSend = index => index == 0 ? FrameStatus.FrameTooLarge : FrameStatus.Ok;

        OnlineCombatSendResult failed = await rig.Settle();

        Assert.Equal(OnlineCombatSendOutcome.FrameFailed, failed.Outcome);
        Assert.Equal(FrameStatus.FrameTooLarge, failed.FrameStatus);
        Assert.Equal(OnlineCombatPhase.Cleared, m.Phase);                          // 没发出去:阶段不前进
        Assert.Equal(1, m.Blocked);

        Assert.True((await rig.Settle()).Sent);
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(2, rig.Port.SettlementCommands);
    }

    [Fact]
    public async Task A_send_exception_goes_through_and_the_state_is_unchanged()
    {
        var rig = new CombatRig(2);
        rig.Port.FailureOfSend = index => new IOException("synthetic 900");

        await Assert.ThrowsAsync<IOException>(() => rig.Kill(0));

        Assert.Equal(OnlineCombatMonsterState.Alive, rig.Machine.Monsters[0].State);
        Assert.Equal(0, rig.Machine.Sent);
        Assert.Equal("Spawned", CombatKit.Trace(rig.Machine.Events));

        rig.Port.FailureOfSend = index => null;
        Assert.True((await rig.Kill(0)).Sent);
    }

    // ======== 可调项与构造 ========

    [Fact]
    public async Task The_payload_factories_in_the_options_are_used()
    {
        var asked = new List<ushort>();
        byte[] custom = GatewayTestSupport.Pattern(61, 0x40, 3);
        byte[] settlement = GatewayTestSupport.Pattern(40, 0x10, 1);
        var options = new OnlineDungeonCombatOptions
        {
            KillPayload = mobId =>
            {
                asked.Add(mobId);
                return custom;
            },
            SettlementPayload = () => settlement,
        };
        var rig = new CombatRig(3, options);

        await rig.Kill(2);
        rig.Clear();
        await rig.Settle();

        Assert.Equal(new ushort[] { (ushort)rig.Ids[2] }, asked.ToArray());       // 工厂拿到的是服务端编号
        Assert.Equal(custom, rig.Port.Sent[0].Payload);
        Assert.Equal(settlement, rig.Port.Sent[1].Payload);
    }

    [Fact]
    public void The_constructor_refuses_missing_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonCombat(null, new RecordingCombatPort()));
        Assert.Throws<ArgumentNullException>(() => new OnlineDungeonCombat(CombatKit.Map(1), null));
    }

    [Fact]
    public async Task The_progress_counters_in_the_events_follow_the_chain()
    {
        var rig = new CombatRig(3);
        await rig.Kill(0);
        await rig.Kill(1);
        rig.Confirm(rig.Ids[0]);
        await rig.Kill(2);

        OnlineCombatEvent[] kills = rig.Machine.Events.Where(e => e.Kind == OnlineCombatEventKind.KillSent).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, kills.Select(e => e.Sent).ToArray());
        Assert.Equal(new[] { 0, 0, 1 }, kills.Select(e => e.Confirmed).ToArray());
        Assert.All(kills, e => Assert.Equal(3, e.Total));
        OnlineCombatEvent confirm = rig.Machine.Events.Single(e => e.Kind == OnlineCombatEventKind.KillConfirmed);
        Assert.Equal(2, confirm.Sent);
        Assert.Equal(1, confirm.Confirmed);
    }
}
