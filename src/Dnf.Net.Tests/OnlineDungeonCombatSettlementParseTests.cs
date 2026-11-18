using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// N4 结算解析结果测试:验证 <see cref="OnlineDungeonCombat"/> 与 <see cref="OnlineDungeonCombatRunner"/> 在
/// <see cref="OnlineCombatEventKind.SettlementArrived"/> 事件与结果对象上带出 <see cref="SettlementNoticeA"/>/<see cref="SettlementNoticeB"/>,
/// 放行与阶段推进仍只认包号与顺序(解析失败照常推进、不计 Malformed),乱序/重复帧不解析,
/// 0x12a 与 (0,5) 许可表不推进结算阶段,日志与 ToString 不泄漏解析出的任何数值。全部使用合成数据(编号 900 以上)。
/// </summary>
public class OnlineDungeonCombatSettlementParseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Normal_result_and_reward_payloads_attach_parsed_records_to_events_machine_and_result(int entryCount)
    {
        var aSpec = new SettlementTestPayloads.ASpec { Entries = new List<SettlementTestPayloads.BestTimeSpec>() };
        for (int i = 0; i < entryCount; i++)
        {
            aSpec.Entries.Add(new SettlementTestPayloads.BestTimeSpec(901 + i, 50000 + i * 111, i % 2));
        }
        var bSpec = new SettlementTestPayloads.BSpec();
        if (entryCount == 0)
        {
            bSpec.Items.Clear();
            bSpec.Rewards = new List<SettlementTestPayloads.RewardSpec>
            {
                new SettlementTestPayloads.RewardSpec(0),
                new SettlementTestPayloads.RewardSpec(1, 0, 1200),
                new SettlementTestPayloads.RewardSpec(2, 0, 2500, 902, 7),
                new SettlementTestPayloads.RewardSpec(0),
            };
        }
        byte[] aBytes = aSpec.Build(0xAA, 0xBB);
        byte[] bBytes = bSpec.Build(0xCC);

        int[] ids = CombatKit.Sequential(1);
        var server = new FakeCombatServer(ids);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == 49)
                {
                    return new[]
                    {
                        new CombatKit.ServerFrame(CombatKit.Noti, 0x22, aBytes),
                        new CombatKit.ServerFrame(CombatKit.Noti, 0x23, bBytes),
                    };
                }
                return server.Handle(cls, id, payload);
            },
        };

        var callbacks = new List<OnlineCombatEvent>();
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.OnEvent = callbacks.Add;
        var runner = new OnlineDungeonCombatRunner(session, null, options);
        Assert.True(runner.PostKill(0));

        OnlineDungeonCombatResult result = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, result.Outcome);
        Assert.Equal(OnlineCombatPhase.Settled, result.Phase);

        OnlineCombatEvent arrivedA = Assert.Single(callbacks, e => e.Kind == OnlineCombatEventKind.SettlementArrived && e.Part == OnlineCombatSettlementPart.Result);
        OnlineCombatEvent arrivedB = Assert.Single(callbacks, e => e.Kind == OnlineCombatEventKind.SettlementArrived && e.Part == OnlineCombatSettlementPart.Reward);

        Assert.Equal(OnlineCombatSettlementParseStatus.Succeeded, arrivedA.SettlementParseStatus);
        Assert.NotNull(arrivedA.SettlementResult);
        Assert.Null(arrivedA.SettlementReward);
        Assert.Same(arrivedA.SettlementResult, runner.Machine.SettlementResult);
        Assert.Same(arrivedA.SettlementResult, result.SettlementResult);
        Assert.True(arrivedA.SettlementResult.HasBestTimeBlock);
        Assert.Equal(aSpec.ClearTime, arrivedA.SettlementResult.ClearTime);
        Assert.Equal(entryCount, arrivedA.SettlementResult.Entries.Count);
        for (int i = 0; i < entryCount; i++)
        {
            Assert.Equal((ushort)(901 + i), arrivedA.SettlementResult.Entries[i].UniqueId);
            Assert.Equal(50000 + i * 111, arrivedA.SettlementResult.Entries[i].BestTime);
            Assert.Equal(i % 2 == 1, arrivedA.SettlementResult.Entries[i].IsNewRecord);
        }

        Assert.Equal(OnlineCombatSettlementParseStatus.Succeeded, arrivedB.SettlementParseStatus);
        Assert.Null(arrivedB.SettlementResult);
        Assert.NotNull(arrivedB.SettlementReward);
        Assert.Same(arrivedB.SettlementReward, runner.Machine.SettlementReward);
        Assert.Same(arrivedB.SettlementReward, result.SettlementReward);
        Assert.Equal(bSpec.Exp, arrivedB.SettlementReward.ExpValues.ToArray());
        Assert.Equal(bSpec.Items.Count, arrivedB.SettlementReward.QuestItems.Count);
        Assert.Equal(bSpec.Percent, arrivedB.SettlementReward.Percent);
        Assert.Equal(bSpec.Rewards[1].Status, arrivedB.SettlementReward.Rewards[1].Status);
        Assert.Equal(bSpec.Rewards[2].ItemId, arrivedB.SettlementReward.Rewards[2].ItemId);

        foreach (OnlineCombatEvent e in callbacks.Where(ev => ev.Kind != OnlineCombatEventKind.SettlementArrived))
        {
            Assert.Equal(OnlineCombatSettlementParseStatus.NotApplicable, e.SettlementParseStatus);
            Assert.Null(e.SettlementResult);
            Assert.Null(e.SettlementReward);
        }
    }

    [Fact]
    public async Task Seven_byte_all_zero_fallback_result_parses_with_HasBestTimeBlock_false()
    {
        var rig = new CombatRig(1);
        await rig.FightToClear();
        await rig.Settle();

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Machine.OnFrame(PacketClass.Noti, 0x22, new byte[7]));

        OnlineCombatEvent e = rig.Machine.Events.Last(ev => ev.Kind == OnlineCombatEventKind.SettlementArrived);
        Assert.Equal(OnlineCombatSettlementPart.Result, e.Part);
        Assert.Equal(OnlineCombatSettlementParseStatus.Succeeded, e.SettlementParseStatus);
        Assert.NotNull(e.SettlementResult);
        Assert.Same(e.SettlementResult, rig.Machine.SettlementResult);
        Assert.False(e.SettlementResult.HasBestTimeBlock);
        Assert.Equal(0, e.SettlementResult.ClearTime);
        Assert.Empty(e.SettlementResult.Entries);
    }

    [Fact]
    public async Task Seven_byte_non_zero_result_fails_both_parsers_and_still_advances_without_malformed()
    {
        var rig = new CombatRig(1);
        await rig.FightToClear();
        await rig.Settle();

        byte[] badFallback = new byte[7];
        badFallback[1] = 1;
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Machine.OnFrame(PacketClass.Noti, 0x22, badFallback));

        OnlineCombatEvent e = rig.Machine.Events.Last(ev => ev.Kind == OnlineCombatEventKind.SettlementArrived);
        Assert.Equal(OnlineCombatSettlementParseStatus.Failed, e.SettlementParseStatus);
        Assert.Null(e.SettlementResult);
        Assert.Null(rig.Machine.SettlementResult);
        Assert.Equal(0, rig.Machine.Malformed);
        Assert.Equal(0, rig.Count(OnlineCombatEventKind.FrameIgnored));

        // 放行仍只认包号:继续收到合法奖励后照常进入 Settled
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Machine.OnFrame(PacketClass.Noti, 0x23, new SettlementTestPayloads.BSpec().Build()));
        Assert.Equal(OnlineCombatPhase.Settled, rig.Machine.Phase);
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));
        Assert.NotNull(rig.Machine.SettlementReward);
    }

    [Fact]
    public async Task Malformed_settlement_payloads_still_advance_and_keep_existing_counts_unchanged()
    {
        var truncatedA = new byte[] { 1, 2, 3 };
        var countOverflowA = new SettlementTestPayloads.ASpec { DeclaredCount = 20 }.Build();
        var invalidRewardStatusB = new SettlementTestPayloads.BSpec
        {
            Rewards = new List<SettlementTestPayloads.RewardSpec>
            {
                new SettlementTestPayloads.RewardSpec(3), // 非法奖励状态(只允许 0/1/2)
                new SettlementTestPayloads.RewardSpec(0),
                new SettlementTestPayloads.RewardSpec(0),
                new SettlementTestPayloads.RewardSpec(0),
            },
        }.Build();
        var itemOverflowB = new SettlementTestPayloads.BSpec { DeclaredItemCount = 200 }.Build();

        foreach (byte[] badA in new[] { truncatedA, countOverflowA })
        {
            foreach (byte[] badB in new[] { new byte[10], invalidRewardStatusB, itemOverflowB })
            {
                var rig = new CombatRig(1);
                await rig.FightToClear();
                await rig.Settle();

                Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Machine.OnFrame(PacketClass.Noti, 0x22, badA));
                Assert.Equal(OnlineCombatPhase.Settling, rig.Machine.Phase);
                Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Machine.OnFrame(PacketClass.Noti, 0x23, badB));
                Assert.Equal(OnlineCombatPhase.Settled, rig.Machine.Phase);
                Assert.Equal(1, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));
                Assert.Equal(0, rig.Machine.Malformed);
                Assert.Equal(0, rig.Machine.OutOfOrder);
                Assert.Equal(0, rig.Machine.Duplicates);
                Assert.Equal(0, rig.Machine.PassthroughCount);
                Assert.Equal(0, rig.Count(OnlineCombatEventKind.FrameIgnored));

                OnlineCombatEvent[] arrived = rig.Machine.Events.Where(e => e.Kind == OnlineCombatEventKind.SettlementArrived).ToArray();
                Assert.Equal(2, arrived.Length);
                Assert.All(arrived, e => Assert.Equal(OnlineCombatSettlementParseStatus.Failed, e.SettlementParseStatus));
                Assert.Null(arrived[0].SettlementResult);
                Assert.Null(arrived[1].SettlementReward);
                Assert.Null(rig.Machine.SettlementResult);
                Assert.Null(rig.Machine.SettlementReward);
            }
        }
    }

    [Fact]
    public async Task Out_of_order_and_duplicate_settlement_frames_are_not_parsed()
    {
        byte[] validA = new SettlementTestPayloads.ASpec { ClearTime = 98765 }.Build();
        byte[] validB = new SettlementTestPayloads.BSpec { Percent = 77 }.Build();
        byte[] secondA = new SettlementTestPayloads.ASpec { ClearTime = 11111 }.Build();
        byte[] secondB = new SettlementTestPayloads.BSpec { Percent = 22 }.Build();

        var rig = new CombatRig(1);
        OnlineDungeonCombat m = rig.Machine;

        // 结算命令未发前到来:乱序,不解析
        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, m.OnFrame(PacketClass.Noti, 0x22, validA));
        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, m.OnFrame(PacketClass.Noti, 0x23, validB));
        Assert.Null(m.SettlementResult);
        Assert.Null(m.SettlementReward);

        await rig.FightToClear();
        await rig.Settle();

        // 奖励先于结果到来:乱序,不解析
        Assert.Equal(OnlineCombatFrameVerdict.OutOfOrder, m.OnFrame(PacketClass.Noti, 0x23, validB));
        Assert.Null(m.SettlementReward);

        // 正序到达
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, m.OnFrame(PacketClass.Noti, 0x22, validA));
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, m.OnFrame(PacketClass.Noti, 0x23, validB));
        SettlementNoticeA firstA = m.SettlementResult;
        SettlementNoticeB firstB = m.SettlementReward;
        Assert.NotNull(firstA);
        Assert.NotNull(firstB);

        // 重复到达:忽略,不替换也不解析
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, m.OnFrame(PacketClass.Noti, 0x22, secondA));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, m.OnFrame(PacketClass.Noti, 0x23, secondB));
        Assert.Same(firstA, m.SettlementResult);
        Assert.Same(firstB, m.SettlementReward);
        Assert.Equal(98765, m.SettlementResult.ClearTime);
        Assert.Equal(77, m.SettlementReward.Percent);

        foreach (OnlineCombatEvent ignored in m.Events.Where(e => e.Kind == OnlineCombatEventKind.FrameIgnored))
        {
            Assert.Equal(OnlineCombatSettlementParseStatus.NotApplicable, ignored.SettlementParseStatus);
            Assert.Null(ignored.SettlementResult);
            Assert.Null(ignored.SettlementReward);
        }
    }

    [Fact]
    public async Task Extra_0x12a_and_dungeon_permission_frames_are_passthrough_and_do_not_settle()
    {
        var rig = new CombatRig(1);
        OnlineDungeonCombat m = rig.Machine;
        await rig.FightToClear();
        await rig.Settle();

        // 0x12a 无载荷与 (0,5) 许可表在结算通知中途和之后到达均按透传处理,不算已结算
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Noti, 0x12a, Array.Empty<byte>()));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Noti, 5, new byte[] { 1, 0, 0, 1, 2 }));
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(0, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));

        Assert.Equal(OnlineCombatFrameVerdict.Accepted, m.OnFrame(PacketClass.Noti, 0x22, new SettlementTestPayloads.ASpec().Build()));
        Assert.Equal(OnlineCombatPhase.Settling, m.Phase);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, m.OnFrame(PacketClass.Noti, 0x23, new SettlementTestPayloads.BSpec().Build()));
        Assert.Equal(OnlineCombatPhase.Settled, m.Phase);

        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Noti, 0x12a, Array.Empty<byte>()));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, m.OnFrame(PacketClass.Noti, 5, new byte[] { 1, 0, 0, 1, 2 }));
        Assert.Equal(4, m.PassthroughCount);
        Assert.Equal(1, rig.Count(OnlineCombatEventKind.ReadyToReturnToTown));
    }

    [Fact]
    public async Task Settlement_log_lines_stay_byte_for_byte_identical_and_leak_no_parsed_values()
    {
        var aSpec = new SettlementTestPayloads.ASpec { ClearTime = 7654321, BaseScore = 0x5E, TimeBonus = 0x17 };
        var bSpec = new SettlementTestPayloads.BSpec { ReceiverValue = 8765432, Percent = 93 };
        aSpec.Entries[0].UniqueId = 987;
        aSpec.Entries[0].BestTime = 6543210;

        int[] ids = CombatKit.Sequential(1);
        var server = new FakeCombatServer(ids);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
            {
                if (cls == PacketClass.Cmd && id == 49)
                {
                    return new[]
                    {
                        new CombatKit.ServerFrame(CombatKit.Noti, 0x22, aSpec.Build()),
                        new CombatKit.ServerFrame(CombatKit.Noti, 0x23, bSpec.Build()),
                    };
                }
                return server.Handle(cls, id, payload);
            },
        };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        var log = new HostTestKit.LogSink();
        Assert.True(runner.PostKill(0));

        OnlineDungeonCombatResult r = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), log.Add), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, r.Outcome);
        Assert.NotNull(r.SettlementResult);
        Assert.NotNull(r.SettlementReward);
        Assert.Contains("DNF_NET stage=combat event=SettlementArrived part=Result", log.Lines);
        Assert.Contains("DNF_NET stage=combat event=SettlementArrived part=Reward", log.Lines);
        Assert.All(log.Lines, line =>
        {
            Assert.DoesNotContain("7654321", line);
            Assert.DoesNotContain("6543210", line);
            Assert.DoesNotContain("8765432", line);
            Assert.DoesNotContain("424242", line);
            Assert.DoesNotContain("987", line);
        });
    }

    [Fact]
    public async Task Real_session_end_to_end_delivers_parsed_settlement_via_OnEvent_and_result()
    {
        var aSpec = new SettlementTestPayloads.ASpec { ClearTime = 456789 };
        var bSpec = new SettlementTestPayloads.BSpec { Percent = 88 };
        byte[] aBytes = aSpec.Build();
        byte[] bBytes = bSpec.Build();

        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame =>
        {
            if (frame.Class != PacketClass.Cmd) return null;
            if (frame.Id == 49)
            {
                return new[]
                {
                    HostTestKit.RealEnvelope(CombatKit.Noti, 0x22, aBytes),
                    DungeonEntryWire.Notice(0x23, bBytes),
                };
            }
            return server.Handle(frame.Class, frame.Id, frame.PayloadForTest()).Select(r => r.Wire()).ToList();
        };

        var seen = new List<OnlineCombatEvent>();
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.OnEvent = seen.Add;
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, options);
        runner.PostKill(0);
        runner.PostKill(1);

        OnlineDungeonCombatResult result = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(ids), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.ReturnReady, result.Outcome);
        OnlineCombatEvent evA = Assert.Single(seen, e => e.Kind == OnlineCombatEventKind.SettlementArrived && e.Part == OnlineCombatSettlementPart.Result);
        OnlineCombatEvent evB = Assert.Single(seen, e => e.Kind == OnlineCombatEventKind.SettlementArrived && e.Part == OnlineCombatSettlementPart.Reward);
        Assert.Equal(OnlineCombatSettlementParseStatus.Succeeded, evA.SettlementParseStatus);
        Assert.Equal(OnlineCombatSettlementParseStatus.Succeeded, evB.SettlementParseStatus);
        Assert.Same(evA.SettlementResult, result.SettlementResult);
        Assert.Same(evB.SettlementReward, result.SettlementReward);
        Assert.Equal(456789, result.SettlementResult.ClearTime);
        Assert.Equal(88, result.SettlementReward.Percent);
    }
}

file static class ClientFrameExt
{
    public static byte[] PayloadForTest(this DungeonEntryClientFrame frame) => frame.Plain;
}
