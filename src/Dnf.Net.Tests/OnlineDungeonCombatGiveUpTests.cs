using System.Diagnostics;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// N3b 战斗路径放弃回城(<see cref="OnlineDungeonCombatRunner.PostGiveUp"/>)与 N3c 可见性放宽测试。
/// 全部用合成编号(900+/9100+)、内存管道与假会话，不连真服。
/// </summary>
public class OnlineDungeonCombatGiveUpTests
{
    private static byte[] UserAreaBytes(ushort uniqueId = 901, byte village = 3, byte area = 7, ushort x = 320, ushort y = 210)
    {
        return new NoticePayloadBuilder().U16(uniqueId).U8(village).U8(area).U16(x).U16(y).U8(1).U8(1).ToArray();
    }

    private static byte[] AreaUsersBytes(byte village = 3, byte area = 7, ushort uniqueId = 902)
    {
        return new NoticePayloadBuilder().U8(village).U8(area).U16(1).U16(uniqueId).U16(100).U16(200).U8(0).U8(1).ToArray();
    }

    [Fact]
    public void Appended_enum_numeric_values_preserve_all_existing_values()
    {
        Assert.Equal(0, (int)OnlineDungeonCombatOutcome.ReturnReady);
        Assert.Equal(6, (int)OnlineDungeonCombatOutcome.Faulted);
        Assert.Equal(7, (int)OnlineDungeonCombatOutcome.GaveUp);

        Assert.Equal(0, (int)OnlineCombatEventKind.MonstersSpawned);
        Assert.Equal(17, (int)OnlineCombatEventKind.RoomLoaded);
        Assert.Equal(18, (int)OnlineCombatEventKind.GiveUpSent);
        Assert.Equal(19, (int)OnlineCombatEventKind.ReturnedToTown);

        Assert.Equal(0, (int)OnlineCombatBlock.None);
        Assert.Equal(13, (int)OnlineCombatBlock.NoLoadPending);
        Assert.Equal(14, (int)OnlineCombatBlock.GivingUp);

        // 结算解析状态是公开枚举,数值写进字面量;default 必须是"不适用"
        Assert.Equal(0, (int)OnlineCombatSettlementParseStatus.NotApplicable);
        Assert.Equal(1, (int)OnlineCombatSettlementParseStatus.Succeeded);
        Assert.Equal(2, (int)OnlineCombatSettlementParseStatus.Failed);
        Assert.Equal(OnlineCombatSettlementParseStatus.NotApplicable, default(OnlineCombatSettlementParseStatus));
    }

    [Fact]
    public void GiveUpTimeout_default_is_15_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), new OnlineDungeonCombatOptions().GiveUpTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(15000), new OnlineDungeonCombatOptions().GiveUpTimeout);
    }

    [Fact]
    public void Runner_validates_GiveUpTimeout_bounds()
    {
        var session = new CombatFakeSession();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { GiveUpTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { GiveUpTimeout = TimeSpan.FromMilliseconds(-10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { GiveUpTimeout = TimeSpan.FromDays(40) }));

        // 上界的边界:恰好 int.MaxValue 毫秒被接受,多一毫秒被拒
        var atLimit = new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { GiveUpTimeout = TimeSpan.FromMilliseconds(2147483647) });
        Assert.False(atLimit.Ended);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineDungeonCombatRunner(session, null, new OnlineDungeonCombatOptions { GiveUpTimeout = TimeSpan.FromMilliseconds(2147483648) }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostGiveUp_sends_empty_1_45_once_and_completes_on_0_23_and_0_24_in_either_order(bool areaUsersFirst)
    {
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == SessionLifecyclePayloads.GiveUpGamePacketId)
                {
                    var f23 = new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(903, 4, 8, 512, 240));
                    var f24 = new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(4, 8, 904));
                    return areaUsersFirst
                        ? new[] { CombatKit.Heartbeat(), CombatKit.Unrelated(912), f24, CombatKit.Heartbeat(), f23 }
                        : new[] { CombatKit.Unrelated(911), f23, CombatKit.Heartbeat(), f24 };
                }
                return server.Handle(cls, id, payload);
            },
        };
        var events = new List<OnlineCombatEvent>();
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.OnEvent = events.Add;
        var runner = new OnlineDungeonCombatRunner(session, null, options);
        var log = new HostTestKit.LogSink();

        Assert.True(runner.PostGiveUp());
        Assert.False(runner.PostGiveUp());                   // 重复请求返回 false
        Assert.False(runner.PostKill(0));                    // 放弃之后 PostKill 返回 false
        Assert.False(runner.PostMoveRoom(new OnlineMoveRoomRequest(1, 0, 10, 10, false)));

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), log.Add), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, r.Outcome);
        Assert.False(r.ReturnReady);
        Assert.False(runner.PostGiveUp());
        Assert.Equal(1, session.Commands(45));
        Assert.Empty(session.Sent.Single(s => s.Id == 45).Payload);
        Assert.Equal(0, session.Commands(42));
        Assert.Equal(0, session.Commands(49));
        Assert.Equal(areaUsersFirst ? 2 : 1, r.HeartbeatsAnswered);
        Assert.Equal(areaUsersFirst ? 3 : 2, r.PassthroughCount); // 心跳帧与无关帧均计入透传

        OnlineCombatEvent returnedEvent = Assert.Single(events, e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
        Assert.NotNull(r.UserArea);
        Assert.NotNull(r.AreaUsers);
        Assert.Same(r.UserArea, returnedEvent.UserArea);
        Assert.Same(r.AreaUsers, returnedEvent.AreaUsers);
        Assert.Equal((ushort)903, r.UserArea.UniqueId);
        Assert.Equal((byte)4, r.UserArea.Village);
        Assert.Equal((byte)8, r.UserArea.Area);
        Assert.Single(r.AreaUsers.Entries);

        Assert.Contains("DNF_NET stage=combat event=GiveUpSent", log.Lines);
        Assert.Contains("DNF_NET stage=combat event=ReturnedToTown userArea=1 areaUsers=1", log.Lines);
        Assert.Contains(log.Lines, l => l.StartsWith("DNF_NET stage=combat ended outcome=GaveUp "));
        Assert.All(log.Lines, line =>
        {
            Assert.DoesNotContain("903", line);
            Assert.DoesNotContain("904", line);
            Assert.DoesNotContain("512", line);
            Assert.DoesNotContain("240", line);
        });
    }

    [Fact]
    public async Task Enveloped_0_23_and_0_24_are_recognized_after_give_up()
    {
        var book = new DungeonEntryEnvelopeBook();
        int[] ids = CombatKit.Sequential(1);
        var session = new CombatFakeSession();
        var runner = new OnlineDungeonCombatRunner(session, book.Unwrap, CombatKit.Options());
        Task<OnlineDungeonCombatResult> run = GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.True(runner.PostGiveUp());
        await CombatKit.WaitUntil(() => session.Commands(45) == 1, "等待 1/45 发出");

        byte[] s23 = book.Seal(DungeonEntryWire.Inner(CombatKit.Noti, 23, UserAreaBytes(905, 2, 6)), 1);
        byte[] s24 = book.Seal(DungeonEntryWire.Inner(CombatKit.Noti, 24, AreaUsersBytes(2, 6, 906)), 2);
        session.PushUnit(DungeonEntryWire.Decoded(CombatKit.Noti, 561, s23), CombatKit.DecodedPlain(CombatKit.Noti, 23, UserAreaBytes(905, 2, 6)));
        session.PushUnit(DungeonEntryWire.Decoded(CombatKit.Noti, 561, s24), CombatKit.DecodedPlain(CombatKit.Noti, 24, AreaUsersBytes(2, 6, 906)));

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, r.Outcome);
        Assert.Equal(2, r.EnvelopesOpened);
        Assert.Equal(2, r.InnerCopiesSkipped);
        Assert.NotNull(r.UserArea);
        Assert.NotNull(r.AreaUsers);
        Assert.Equal((ushort)905, r.UserArea.UniqueId);
    }

    [Fact]
    public async Task Notices_0_23_and_0_24_arriving_before_1_45_do_not_count_and_only_one_does_not_complete()
    {
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        var session = CombatFakeSession.ForServer(server);
        // 放弃前就收到一次 0/23 和 0/24:应当当作无关帧透传,不能算作放弃回城完成
        session.Push(
            new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes(901, 1, 1)),
            new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes(1, 1, 902)));

        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.GiveUpTimeout = TimeSpan.FromMilliseconds(250);
        var runner = new OnlineDungeonCombatRunner(session, null, options);
        Task<OnlineDungeonCombatResult> run = GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        await CombatKit.WaitUntil(() => runner.Machine != null && runner.Machine.PassthroughCount >= 2, "放弃前的 0/23 与 0/24 已被当作透传消费");
        Assert.Null(runner.Machine.UserArea);
        Assert.Null(runner.Machine.AreaUsers);

        Assert.True(runner.PostGiveUp());
        await CombatKit.WaitUntil(() => session.Commands(45) == 1, "等待 1/45 发出");
        // 放弃后只来一个 0/23,没有 0/24 -> 不收尾,最终超 GiveUpTimeout
        session.Push(new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes(909, 9, 9)));

        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);
        Assert.NotNull(r.UserArea);
        Assert.Null(r.AreaUsers);
    }

    [Fact]
    public async Task Unparseable_0_23_and_0_24_payloads_still_finish_as_GaveUp_with_null_snapshots()
    {
        int[] ids = CombatKit.Sequential(1);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == 45)
                {
                    return new[]
                    {
                        new CombatKit.ServerFrame(CombatKit.Noti, 23, new byte[] { 1, 2 }),       // 短包
                        new CombatKit.ServerFrame(CombatKit.Noti, 24, new byte[] { 1, 2, 99, 0 }), // 个数超载荷
                    };
                }
                return null;
            },
        };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        Assert.True(runner.PostGiveUp());

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, r.Outcome);
        Assert.Null(r.UserArea);
        Assert.Null(r.AreaUsers);
        Assert.Equal(0, r.Malformed);
    }

    [Fact]
    public async Task GiveUp_wait_preserves_ConnectionClosed_FramingFault_and_Cancelled()
    {
        foreach (string mode in new[] { "closed", "fault", "cancel" })
        {
            var session = new CombatFakeSession();
            var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
            using var cts = new CancellationTokenSource();
            Task<OnlineDungeonCombatResult> run = GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(1), null, cts.Token), 20);

            Assert.True(runner.PostGiveUp());
            await CombatKit.WaitUntil(() => session.Commands(45) == 1, "等待 1/45 发出");

            if (mode == "closed") session.Closed = true;
            else if (mode == "fault") session.Fault = FrameError.LengthAboveMaximum;
            else cts.Cancel();

            OnlineDungeonCombatResult r = await run;
            OnlineDungeonCombatOutcome expected = mode switch
            {
                "closed" => OnlineDungeonCombatOutcome.ConnectionClosed,
                "fault" => OnlineDungeonCombatOutcome.FramingFault,
                _ => OnlineDungeonCombatOutcome.Cancelled,
            };
            Assert.Equal(expected, r.Outcome);
        }
    }

    [Fact]
    public async Task Queued_kills_are_discarded_and_clear_notice_after_give_up_never_sends_settlement()
    {
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == 45)
                {
                    // 发完 1/45 后哪怕收到清图通知,也不得再发结算命令
                    return new[] { CombatKit.Clear() };
                }
                return server.Handle(cls, id, payload);
            },
        };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        Assert.True(runner.PostKill(0));
        Assert.True(runner.PostMoveRoom(new OnlineMoveRoomRequest(1, 0, 10, 10, false)));
        Assert.True(runner.PostGiveUp());
        Assert.False(runner.PostKill(1));

        Task<OnlineDungeonCombatResult> run = GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);
        await CombatKit.WaitUntil(() => runner.Machine != null && runner.Machine.Phase == OnlineCombatPhase.Cleared, "清图通知已处理");
        await Task.Delay(80);
        Assert.Equal(0, session.Commands(42));
        Assert.Equal(0, session.Commands(48));
        Assert.Equal(0, session.Commands(49));

        session.Push(
            new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes()),
            new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes()));
        OnlineDungeonCombatResult r = await run;
        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, r.Outcome);
    }

    [Theory]
    [InlineData(OnlineCombatPhase.Fighting)]
    [InlineData(OnlineCombatPhase.Cleared)]
    [InlineData(OnlineCombatPhase.Settling)]
    public async Task GiveUp_succeeds_from_Fighting_Cleared_and_Settling_phases(OnlineCombatPhase triggerPhase)
    {
        int[] ids = CombatKit.Sequential(1);
        var server = new FakeCombatServer(ids) { WithholdClear = true };
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == 49) return null; // 结算扣着不回,让状态停在 Settling
                if (cls == PacketClass.Cmd && id == 45)
                {
                    return new[]
                    {
                        new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes(908, 5, 11)),
                        new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes(5, 11, 909)),
                    };
                }
                return server.Handle(cls, id, payload);
            },
        };
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.SettlementTimeout = TimeSpan.FromSeconds(30);
        var runner = new OnlineDungeonCombatRunner(session, null, options);
        if (triggerPhase == OnlineCombatPhase.Cleared)
        {
            options.OnEvent = e =>
            {
                if (e.Kind == OnlineCombatEventKind.MapCleared)
                {
                    Assert.True(runner.PostGiveUp());
                }
            };
        }
        Task<OnlineDungeonCombatResult> run = GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        if (triggerPhase == OnlineCombatPhase.Cleared)
        {
            await CombatKit.WaitUntil(() => runner.Machine != null, "状态机建立");
            session.Push(CombatKit.Clear());
        }
        else if (triggerPhase == OnlineCombatPhase.Settling)
        {
            await CombatKit.WaitUntil(() => runner.Machine != null, "状态机建立");
            session.Push(CombatKit.Clear());
            await CombatKit.WaitUntil(() => session.Commands(49) == 1, "已发结算命令,进入 Settling");
            Assert.True(runner.PostGiveUp());
        }
        else
        {
            await CombatKit.WaitUntil(() => runner.Machine != null, "处于 Fighting");
            Assert.True(runner.PostGiveUp());
        }

        OnlineDungeonCombatResult r = await run;

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, r.Outcome);
        Assert.Equal(1, session.Commands(45));
        if (triggerPhase == OnlineCombatPhase.Cleared)
        {
            Assert.Equal(0, session.Commands(49));
            Assert.Equal(OnlineCombatPhase.Cleared, r.Phase);
        }
        else if (triggerPhase == OnlineCombatPhase.Settling)
        {
            Assert.Equal(OnlineCombatPhase.Settling, r.Phase);
        }
    }

    [Fact]
    public async Task OnlineDungeonRunner_public_GameClientSession_overload_skips_keepalive_on_GaveUp_and_invokes_custom_keepalive()
    {
        int[] ids = CombatKit.Sequential(1);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class != PacketClass.Cmd) return null;
            if (frame.Id == 15) return new[] { DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()) };
            if (frame.Id == 16) return new[] { DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, CombatKit.Map29Payload(ids)) };
            if (frame.Id == 45) return new[] { DungeonEntryWire.Notice(23, UserAreaBytes(901, 2, 3)), DungeonEntryWire.Notice(24, AreaUsersBytes(2, 3, 902)) };
            return null;
        };

        // N3c + N3b: public OnlineDungeonRunner(GameClientSession, keepAlive: null) 走完放弃回城且不挂老保活泵
        var runner = new OnlineDungeonRunner(session, keepAlive: null);
        OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options());
        var log = new HostTestKit.LogSink();

        OnlineDungeonResult entryResult = await GatewayTestSupport.Bounded(
            runner.RunAsync(901, 1, log.Add, default, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                entered: _ => Assert.True(combat.PostGiveUp())),
            20);

        Assert.True(entryResult.Entered);
        Assert.True(combat.Ended);
        Assert.Contains("DNF_NET stage=combat event=GiveUpSent", log.Lines);
        Assert.Contains(log.Lines, l => l.StartsWith("DNF_NET stage=combat ended outcome=GaveUp "));
        Assert.Contains("DNF_NET stage=dungeon keepalive=skip reason=NoKeepAlive", log.Lines);
        Assert.Contains(log.Lines, l => l.StartsWith("DNF_NET stage=dungeon session "));

        // N3c: 传入自定保活委托时会被调用
        (GameClientSession session2, DungeonEntryPipe pipe2) = await HostTestKit.ReadySession();
        pipe2.Script = frame =>
        {
            if (frame.Class != PacketClass.Cmd) return null;
            if (frame.Id == 15) return new[] { DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()) };
            if (frame.Id == 16) return new[] { DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, CombatKit.Map29Payload(ids)) };
            return null;
        };
        int customCalled = 0;
        var runner2 = new OnlineDungeonRunner(session2, token =>
        {
            customCalled++;
            return Task.FromResult(4);
        });
        var log2 = new HostTestKit.LogSink();
        OnlineDungeonResult entry2 = await GatewayTestSupport.Bounded(
            runner2.RunAsync(901, 1, log2.Add, default, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), 20);
        Assert.True(entry2.Entered);
        Assert.Equal(1, customCalled);
        Assert.Contains("DNF_NET heartbeat answered=4", log2.Lines);
    }

    [Fact]
    public async Task State_machine_RequestGiveUpAsync_blocks_all_subsequent_commands_and_handles_frame_failure()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        rig.Port.StatusOfSend = index => index == 0 ? FrameStatus.EncryptFailed : FrameStatus.Ok;

        // 第一次发 1/45 帧编码失败:状态机不进入 IsGivingUp,可以重试
        OnlineCombatSendResult failed = await m.RequestGiveUpAsync();
        Assert.Equal(OnlineCombatSendOutcome.FrameFailed, failed.Outcome);
        Assert.Equal(FrameStatus.EncryptFailed, failed.FrameStatus);
        Assert.False(m.IsGivingUp);

        // 重试成功
        OnlineCombatSendResult sent = await m.RequestGiveUpAsync();
        Assert.True(sent.Sent);
        Assert.True(m.IsGivingUp);

        // 重复 RequestGiveUpAsync、杀怪、换房、装载完成、结算一律被 GivingUp 拦下
        Assert.Equal(OnlineCombatBlock.GivingUp, (await m.RequestGiveUpAsync()).Block);
        Assert.Equal(OnlineCombatBlock.GivingUp, (await m.ReportKillAsync(0)).Block);
        Assert.Equal(OnlineCombatBlock.GivingUp, (await m.ReportKillAsync(0, 0)).Block);
        Assert.Equal(OnlineCombatBlock.GivingUp, (await m.RequestSettlementAsync()).Block);
        Assert.Equal(OnlineCombatBlock.GivingUp, m.MoveGate(0));
        Assert.Equal(OnlineCombatBlock.GivingUp, (await m.RequestMoveRoomAsync(new OnlineMoveRoomRequest(1, 0, 10, 10, false))).Block);
        Assert.Equal(OnlineCombatBlock.GivingUp, (await m.ReportRoomLoadedAsync()).Block);

        // 只有 1/45 发到发送口(失败那次 + 成功那次 = 2),其余都没有发出
        Assert.Equal(2, rig.Port.Sent.Count);
        Assert.All(rig.Port.Sent, s => Assert.Equal((PacketClass.Cmd, (ushort)45), (s.Class, s.Id)));
    }

    // ======== 评审补强:变异幸存者对应的缺口(每条注释写明它钉住什么) ========

    private static string Fields(OnlineCombatEvent e)
    {
        return string.Join("|", e.Kind, e.LocalId, e.ServerId, e.Block, e.Verdict, e.Part, e.PacketClass, e.PacketId, e.ReplyCode, e.TrailingBytes);
    }

    private static string[] AllFields(OnlineDungeonCombat machine)
    {
        return machine.Events.Select(Fields).ToArray();
    }

    /// <summary>
    /// 运行结束后(这次运行从没请求过放弃)四个投递口一律返回 false,连接上也不会多出命令帧;
    /// 并且结束行打出的那一刻 <see cref="OnlineDungeonCombatRunner.Ended"/> 已经为真(运行体自己在收尾前置位,外层 finally 之后才轮到的置位赶不上这一刻)。
    /// </summary>
    [Fact]
    public async Task Post_methods_return_false_after_the_run_ended_even_without_any_give_up_request()
    {
        int[] ids = CombatKit.Sequential(1);
        var server = new FakeCombatServer(ids);
        var session = CombatFakeSession.ForServer(server);
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        var endedAtDoneLine = new List<bool>();
        var giveUpAtDoneLine = new List<bool>();
        void Log(string line)
        {
            if (line.StartsWith("DNF_NET stage=combat done ", StringComparison.Ordinal))
            {
                endedAtDoneLine.Add(runner.Ended);
                giveUpAtDoneLine.Add(runner.PostGiveUp());
            }
        }

        Assert.True(runner.PostKill(0));
        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), Log), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.True(runner.Ended);
        Assert.Equal(new[] { true }, endedAtDoneLine.ToArray());
        Assert.Equal(new[] { false }, giveUpAtDoneLine.ToArray());

        int sentBefore = session.Sent.Length;
        var move = new OnlineMoveRoomRequest(1, 0, 10, 10, false);
        Assert.False(runner.PostGiveUp());
        Assert.False(runner.PostKill(0));
        Assert.False(runner.PostKill(0, 0));
        Assert.False(runner.PostMoveRoom(move));
        Assert.False(runner.PostMoveRoom(0, move));
        Assert.Equal(sentBefore, session.Sent.Length);
        Assert.Equal(0, session.Commands(45));
    }

    /// <summary>放弃已请求之后,带房间序号的击杀投递也被拒,且这条击杀之后不会真的发出命令。</summary>
    [Fact]
    public async Task PostKill_with_room_returns_false_after_PostGiveUp_and_no_kill_command_is_ever_sent()
    {
        int[] ids = CombatKit.Sequential(2);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) => cls == PacketClass.Cmd && id == 45
                ? new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes()),
                    new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes()),
                }
                : null,
        };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());

        Assert.True(runner.PostGiveUp());
        Assert.False(runner.PostKill(0, 1));
        Assert.False(runner.PostKill(0, 0));
        Assert.False(runner.PostKill(1));
        Assert.False(runner.PostMoveRoom(0, new OnlineMoveRoomRequest(1, 0, 10, 10, false)));
        Assert.False(runner.PostGiveUp());

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, r.Outcome);
        Assert.Equal(0, session.Commands(42));
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(0, r.Sent);
    }

    /// <summary>
    /// 放弃超时从 1/45 发出的那一刻起算(不是从跑器启动、也不是从结算命令时刻起算):
    /// 战斗先跑 500 ms 以上,再放弃,服务端始终不回 0/23、0/24;放弃到超时结束至少要经过大半个 GiveUpTimeout(300 ms)。
    /// 下限用 250 ms(留 50 ms 余量给时钟粒度),上限只留 10 秒的宽界(机器再慢也不会误报)。
    /// </summary>
    [Fact]
    public async Task GiveUp_timeout_is_counted_from_the_moment_1_45_was_sent()
    {
        int[] ids = CombatKit.Sequential(2);
        var session = new CombatFakeSession();      // 没有任何回应:1/45 发出后永远等不到 0/23 与 0/24
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.GiveUpTimeout = TimeSpan.FromMilliseconds(300);
        var runner = new OnlineDungeonCombatRunner(session, null, options);
        Task<OnlineDungeonCombatResult> run = GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        await CombatKit.WaitUntil(() => runner.Machine != null, "状态机建立");
        await Task.Delay(500);      // 让跑器自己的计时先走过 GiveUpTimeout:起点若错放在跑器启动处,放弃后会立刻超时
        Stopwatch sinceGiveUp = Stopwatch.StartNew();
        Assert.True(runner.PostGiveUp());
        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(run, 10);
        sinceGiveUp.Stop();

        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, r.Outcome);
        Assert.Equal(1, session.Commands(45));
        Assert.True(sinceGiveUp.Elapsed >= TimeSpan.FromMilliseconds(250),
            "放弃到超时只过了 " + sinceGiveUp.ElapsedMilliseconds + " ms,应当不少于 GiveUpTimeout(300 ms)减去 50 ms 余量");
    }

    /// <summary>"回城"日志行里 userArea 与 areaUsers 各自如实反映快照有没有解出来(四种组合逐字对照)。</summary>
    [Theory]
    [InlineData(true, true, "DNF_NET stage=combat event=ReturnedToTown userArea=1 areaUsers=2")]
    [InlineData(false, true, "DNF_NET stage=combat event=ReturnedToTown userArea=0 areaUsers=2")]
    [InlineData(true, false, "DNF_NET stage=combat event=ReturnedToTown userArea=1 areaUsers=-")]
    [InlineData(false, false, "DNF_NET stage=combat event=ReturnedToTown userArea=0 areaUsers=-")]
    public async Task ReturnedToTown_log_line_reports_which_snapshots_were_parsed(bool userAreaParses, bool areaUsersParses, string expectedLine)
    {
        int[] ids = CombatKit.Sequential(1);
        byte[] twoUsers = new NoticePayloadBuilder().U8(3).U8(7).U16(2).U16(902).U16(100).U16(200).U8(0).U8(1).U16(903).U16(110).U16(210).U8(0).U8(1).ToArray();
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) => cls == PacketClass.Cmd && id == 45
                ? new[]
                {
                    new CombatKit.ServerFrame(CombatKit.Noti, 23, userAreaParses ? UserAreaBytes() : new byte[] { 1, 2 }),
                    new CombatKit.ServerFrame(CombatKit.Noti, 24, areaUsersParses ? twoUsers : new byte[] { 1, 2, 99, 0 }),
                }
                : null,
        };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        var log = new HostTestKit.LogSink();
        Assert.True(runner.PostGiveUp());

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), log.Add), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, r.Outcome);
        Assert.Equal(userAreaParses, r.UserArea != null);
        Assert.Equal(areaUsersParses, r.AreaUsers != null);
        Assert.Single(log.Lines, line => line.StartsWith("DNF_NET stage=combat event=ReturnedToTown ", StringComparison.Ordinal));
        Assert.Contains(expectedLine, log.Lines);
    }

    /// <summary>1/45 在帧层就编码失败:运行以 SendFailed 收场(不是原地重试到超时)。</summary>
    [Fact]
    public async Task Runner_ends_with_SendFailed_when_the_1_45_frame_cannot_be_encoded()
    {
        int[] ids = CombatKit.Sequential(1);
        var session = new CombatFakeSession { StatusOfSend = index => FrameStatus.EncryptFailed };
        OnlineDungeonCombatOptions options = CombatKit.Options(overallSeconds: 3);
        options.GiveUpTimeout = TimeSpan.FromMilliseconds(300);
        var runner = new OnlineDungeonCombatRunner(session, null, options);
        Assert.True(runner.PostGiveUp());

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.SendFailed, r.Outcome);
        Assert.False(runner.Machine.IsGivingUp);
        Assert.Equal(1, session.Commands(45));
        Assert.Equal(1, r.Blocked);
    }

    /// <summary>放弃期间重复的 0/23:第二份被判重复,第一份快照保留不被覆盖;回城事件带的是第一份。</summary>
    [Fact]
    public async Task Duplicate_0_23_during_give_up_is_ignored_and_the_first_snapshot_is_kept()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        Assert.True((await m.RequestGiveUpAsync()).Sent);
        var firstArea = new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes(911, 3, 7, 320, 210));
        var secondArea = new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes(922, 4, 8, 330, 220));

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(firstArea));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(secondArea));
        Assert.False(m.ReturnedToTown);
        Assert.Equal((ushort)911, m.UserArea.UniqueId);
        Assert.Equal(1, m.Duplicates);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes(3, 7, 931))));
        Assert.True(m.ReturnedToTown);
        Assert.Equal((ushort)911, m.UserArea.UniqueId);
        OnlineCombatEvent returned = m.Events.Single(e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
        Assert.Same(m.UserArea, returned.UserArea);
        Assert.Same(m.AreaUsers, returned.AreaUsers);

        // 回城之后不论哪一份再来都是重复,也不再产生第二个回城事件
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(secondArea));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes(4, 8, 932))));
        Assert.Equal(3, m.Duplicates);
        Assert.Single(m.Events, e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
        Assert.Equal((ushort)911, m.UserArea.UniqueId);
        Assert.Equal((ushort)931, m.AreaUsers.Entries[0].UniqueId);
    }

    /// <summary>放弃期间重复的 0/24:同样第二份判重复、第一份保留;0/24 先到、0/23 后到也能收尾。</summary>
    [Fact]
    public async Task Duplicate_0_24_during_give_up_is_ignored_and_the_first_snapshot_is_kept()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        Assert.True((await m.RequestGiveUpAsync()).Sent);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes(3, 7, 931))));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes(4, 8, 932))));
        Assert.False(m.ReturnedToTown);
        Assert.Equal((ushort)931, m.AreaUsers.Entries[0].UniqueId);

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes(911))));
        Assert.True(m.ReturnedToTown);
        Assert.Equal((ushort)931, m.AreaUsers.Entries[0].UniqueId);
        Assert.Equal(1, m.Duplicates);
        Assert.Single(m.Events, e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
    }

    /// <summary>
    /// 放弃期间被拦的击杀(按本地编号、按房间加本地编号两种入口):事件里 ServerId 是 -1(这只怪有真实的服务端编号 9101,
    /// 但放弃拦截不去查表,事件不带编号),LocalId 照实记。
    /// </summary>
    [Fact]
    public async Task Kills_blocked_by_give_up_are_recorded_without_a_server_id_through_both_entry_points()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;
        Assert.Equal(9101, rig.Ids[1]);
        Assert.True((await m.RequestGiveUpAsync()).Sent);

        OnlineCombatSendResult byLocal = await m.ReportKillAsync(1);
        OnlineCombatSendResult byRoom = await m.ReportKillAsync(0, 1);

        Assert.Equal(OnlineCombatSendOutcome.Blocked, byLocal.Outcome);
        Assert.Equal(OnlineCombatBlock.GivingUp, byLocal.Block);
        Assert.Equal(OnlineCombatSendOutcome.Blocked, byRoom.Outcome);
        Assert.Equal(OnlineCombatBlock.GivingUp, byRoom.Block);
        Assert.Equal(
            new[]
            {
                "KillBlocked|1|-1|GivingUp|Passthrough|None|Noti|-1|-1|-1",
                "KillBlocked|1|-1|GivingUp|Passthrough|None|Noti|-1|-1|-1",
            },
            m.Events.Where(e => e.Kind == OnlineCombatEventKind.KillBlocked).Select(Fields).ToArray());
        Assert.Single(rig.Port.Sent);      // 只有那条 1/45
    }

    /// <summary>
    /// 战斗主链(清图→结算→回城就绪)上每种事件的全部字段逐条对照(字面量):不带的字段一律 -1;包类与包号只有"忽略一帧"事件(FrameIgnored)
    /// 记它所忽略的那一帧,其余事件一律是 Noti、-1。三只怪的合成编号是 9100、9101、9102。
    /// </summary>
    [Fact]
    public async Task Event_fields_along_the_normal_fight_and_settlement_flow_match_literals()
    {
        var rig = new CombatRig(3);

        await rig.Kill(0);                                                       // KillSent
        await rig.Kill(0);                                                       // 已报过:KillBlocked(查得到编号 9100)
        await rig.Kill(7);                                                       // 不在对应表里:KillBlocked(无编号)
        rig.Confirm(9100);                                                       // KillConfirmed
        rig.Confirm(9100);                                                       // 重复确认:FrameIgnored,带这一帧的包类与包号(0 类、0x26=38)
        await rig.Settle();                                                      // 还没清图:SettlementBlocked
        await rig.Kill(1);
        rig.Confirm(9101);
        await rig.Kill(2);
        rig.Feed(CombatKit.Refusal(9102));                                       // 击杀被拒:KillRefused(1 类、包号 42 的回包)
        rig.Feed(CombatKit.Refusal(9102));                                       // 重复的拒绝:FrameIgnored(类 1、包号 42)
        rig.Clear();                                                             // MapCleared
        await rig.Settle();                                                      // SettlementSent
        await rig.Settle();                                                      // SettlementBlocked(已发过)
        rig.Result();                                                            // SettlementArrived(结果段)
        rig.Reward();                                                            // SettlementArrived(奖励段) + ReadyToReturnToTown

        Assert.Equal(
            new[]
            {
                "MonstersSpawned|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "KillSent|0|9100|None|Passthrough|None|Noti|-1|-1|-1",
                "KillBlocked|0|9100|AlreadyReported|Passthrough|None|Noti|-1|-1|-1",
                "KillBlocked|7|-1|UnknownLocalId|Passthrough|None|Noti|-1|-1|-1",
                "KillConfirmed|0|9100|None|Accepted|None|Noti|-1|-1|-1",
                "FrameIgnored|0|9100|None|Duplicate|None|Noti|38|-1|-1",
                "SettlementBlocked|-1|-1|NotCleared|Passthrough|None|Noti|-1|-1|-1",
                "KillSent|1|9101|None|Passthrough|None|Noti|-1|-1|-1",
                "KillConfirmed|1|9101|None|Accepted|None|Noti|-1|-1|-1",
                "KillSent|2|9102|None|Passthrough|None|Noti|-1|-1|-1",
                "KillRefused|2|9102|None|Accepted|None|Noti|-1|-1|-1",
                "FrameIgnored|2|9102|None|Duplicate|None|Cmd|42|-1|-1",
                "MapCleared|-1|-1|None|Accepted|None|Noti|-1|-1|-1",
                "SettlementSent|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "SettlementBlocked|-1|-1|SettlementAlreadySent|Passthrough|None|Noti|-1|-1|-1",
                "SettlementArrived|-1|-1|None|Accepted|Result|Noti|-1|-1|-1",
                "SettlementArrived|-1|-1|None|Accepted|Reward|Noti|-1|-1|-1",
                "ReadyToReturnToTown|-1|-1|None|Accepted|None|Noti|-1|-1|-1",
            },
            AllFields(rig.Machine));
    }

    /// <summary>放弃回城流程上每种事件的全部字段逐条对照(字面量):GiveUpSent、被拦的各种命令、重复的 0/23 与 0/24、ReturnedToTown 都不带本地编号与编号。</summary>
    [Fact]
    public async Task Event_fields_along_the_give_up_flow_match_literals()
    {
        var rig = new CombatRig(2);
        OnlineDungeonCombat m = rig.Machine;

        Assert.True((await m.RequestGiveUpAsync()).Sent);
        await m.ReportKillAsync(1);
        await m.ReportKillAsync(0, 0);
        await rig.Settle();
        await m.RequestMoveRoomAsync(new OnlineMoveRoomRequest(1, 0, 10, 10, false));
        await m.ReportRoomLoadedAsync();
        await m.RequestGiveUpAsync();                                            // 重复放弃:只返回被拦的结果,不产生事件
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes())));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 23, UserAreaBytes())));
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes())));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, 24, AreaUsersBytes())));

        Assert.Equal(
            new[]
            {
                "MonstersSpawned|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "GiveUpSent|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "KillBlocked|1|-1|GivingUp|Passthrough|None|Noti|-1|-1|-1",
                "KillBlocked|0|-1|GivingUp|Passthrough|None|Noti|-1|-1|-1",
                "SettlementBlocked|-1|-1|GivingUp|Passthrough|None|Noti|-1|-1|-1",
                "RoomMoveBlocked|-1|-1|GivingUp|Passthrough|None|Noti|-1|-1|-1",
                "RoomLoadedBlocked|-1|-1|GivingUp|Passthrough|None|Noti|-1|-1|-1",
                "FrameIgnored|-1|-1|None|Duplicate|None|Noti|23|-1|-1",
                "ReturnedToTown|-1|-1|None|Accepted|None|Noti|-1|-1|-1",
                "FrameIgnored|-1|-1|None|Duplicate|None|Noti|24|-1|-1",
            },
            AllFields(m));
    }

    /// <summary>
    /// 换房与装载握手流程上每种事件的全部字段逐条对照(字面量):换房被拦(没清房间、命令在途、装载未完)、换房被拒(带错误码 20)、
    /// 进新房间(带起始地图读完后剩的字节数,合成起始地图读完恰好 0 字节)、装载完成命令发出与被拦、装载完成通知。
    /// </summary>
    [Fact]
    public async Task Event_fields_along_the_room_move_and_load_flow_match_literals()
    {
        var rig = new RoomRig(2);
        var move = RoomKit.Move(4, 4);

        await rig.Machine.RequestMoveRoomAsync(move);                            // 当前房间没清:RoomMoveBlocked
        await rig.KillAndConfirmAll();                                           // 4 条:KillSent/KillConfirmed × 2
        await rig.Machine.RequestMoveRoomAsync(move);                            // RoomMoveSent
        await rig.Machine.RequestMoveRoomAsync(move);                            // 命令在途:RoomMoveBlocked
        rig.Feed(RoomKit.MoveRefusal(20));                                       // RoomMoveRefused(错误码 20)
        await rig.Machine.RequestMoveRoomAsync(move);                            // RoomMoveSent
        rig.Feed(RoomKit.StartMap(new[] { 9300, 9301 }, 4, 4));                  // RoomEntered
        await rig.Machine.RequestMoveRoomAsync(move);                            // 装载握手没完:RoomMoveBlocked
        await rig.Machine.ReportRoomLoadedAsync();                               // RoomLoadedSent
        await rig.Machine.ReportRoomLoadedAsync();                               // 已经报过:RoomLoadedBlocked
        rig.Feed(RoomKit.Loaded());                                              // RoomLoaded

        Assert.Equal(
            new[]
            {
                "MonstersSpawned|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "RoomMoveBlocked|-1|-1|RoomNotCleared|Passthrough|None|Noti|-1|-1|-1",
                "KillSent|0|9100|None|Passthrough|None|Noti|-1|-1|-1",
                "KillConfirmed|0|9100|None|Accepted|None|Noti|-1|-1|-1",
                "KillSent|1|9101|None|Passthrough|None|Noti|-1|-1|-1",
                "KillConfirmed|1|9101|None|Accepted|None|Noti|-1|-1|-1",
                "RoomMoveSent|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "RoomMoveBlocked|-1|-1|MoveInFlight|Passthrough|None|Noti|-1|-1|-1",
                "RoomMoveRefused|-1|-1|None|Accepted|None|Noti|-1|20|-1",
                "RoomMoveSent|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "RoomEntered|-1|-1|None|Accepted|None|Noti|-1|-1|0",
                "RoomMoveBlocked|-1|-1|LoadPending|Passthrough|None|Noti|-1|-1|-1",
                "RoomLoadedSent|-1|-1|None|Passthrough|None|Noti|-1|-1|-1",
                "RoomLoadedBlocked|-1|-1|NoLoadPending|Passthrough|None|Noti|-1|-1|-1",
                "RoomLoaded|-1|-1|None|Accepted|None|Noti|-1|-1|-1",
            },
            AllFields(rig.Machine));
    }
}
