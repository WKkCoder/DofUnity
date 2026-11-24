using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA4Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算开始中途失败之后的两个"接着发生什么":
/// ① 同一套真控制器上,第一次结算中途失败、第二次结算(回调恢复正常)完整走完——推进命令序列、菜单出现、选择、回城信号、落点——证明失败不泄漏任何状态到下一次;
/// ② 结算通知超时那条路(服务端只回 0x22)上同样的失败:处理函数照样就地吞掉,流程落到通用的"超时中止"收尾(与没有订阅者时一致)。
/// 真联网进副本控制器 + 真结算控制器(<see cref="FaultRig"/>),时间全走逻辑时钟;期望值是手算的整行字面量。
/// </summary>
public sealed class OnlineSettlementGapA4ConsecutiveTests
{
    private static bool LeftInDungeon(FaultRig rig)
    {
        return rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement
            || rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.SettlingExit;
    }

    [Theory]
    [InlineData("GetProgress", "InvalidOperationException")]
    [InlineData("LocalResultComputer", "NotSupportedException")]
    [InlineData("GetPartySlot", "NullReferenceException")]
    [InlineData("TransportFactory", "SyntheticStartFault")]
    public void A_settlement_that_failed_midway_leaves_nothing_behind_for_the_next_settlement_which_runs_the_whole_menu_flow(string pointName, string typeName)
    {
        // 保护:第一次结算在中途抛异常(前三个是"早抛",最后一个是"晚抛"——展示日志与绘制钩子都已就位),流程走默认结算退出回城;
        // 第二次结算回调恢复正常,必须像全新的一样:发 72 → 73 → 74、菜单就绪、玩家选回城、发 1/75、服务端回区域信号后落到服务端给的区域;
        // 整份结算日志 = 第一次的失败行(晚抛时前面还有展示+差异行)+ 第二次的标准完整序列,全程只有一行 start_fault。
        StartFaultPoint point = Enum.Parse<StartFaultPoint>(pointName);   // 注入点名写在 InlineData 里(枚举是 internal,不能出现在公开方法的参数里)
        int calls = 0;
        void Configure(OnlineSettlementOptions o)
        {
            switch (point)
            {
                case StartFaultPoint.GetProgress:
                {
                    var original = o.GetProgress;
                    o.GetProgress = () => ++calls == 1 ? throw MakeFault(typeName) : original();
                    break;
                }
                case StartFaultPoint.LocalResultComputer:
                {
                    var original = o.LocalResultComputer;
                    o.LocalResultComputer = (d, s, p) => ++calls == 1 ? throw MakeFault(typeName) : original(d, s, p);
                    break;
                }
                case StartFaultPoint.GetPartySlot:
                {
                    var original = o.GetPartySlot;
                    o.GetPartySlot = () => ++calls == 1 ? throw MakeFault(typeName) : original();
                    break;
                }
                case StartFaultPoint.TransportFactory:
                {
                    var original = o.TransportFactory;
                    o.TransportFactory = session => ++calls == 1 ? throw MakeFault(typeName) : original(session);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(point), point, "本用例不用这个注入点");
            }
        }

        using var rig = new FaultRig(Configure);
        WireStandardResponder(rig.Port);
        // 第二次结算用:72 → 回 [1];73 → 回"第 0 槽有权";75 → 回执行确认 + 服务端区域对 (1, 2)
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
        {
            rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
            rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 1, 2, 150, 200));
            rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 2));
        });

        // ---------------- 第一次结算:中途失败 ----------------
        rig.EnterTownAndDungeon();
        HostedRoomRun firstRun = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => LeftInDungeon(rig));

        Assert.Equal(1, calls);
        Assert.False(rig.Ready.HoldForMenu);
        Assert.False(rig.SettleCtrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.DungeonCtrl.Phase);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Null(firstRun.HudOverlay);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Empty(rig.Transport.Sent);

        long returnedAt = rig.StepUntilTown();
        Assert.Equal(rig.FinishedAtMs + FaultRig.ReturnDelayMs - 20, returnedAt);   // 打完时刻 + 30000 - 20(每圈 20,打完那圈已累计 20)
        rig.Step();
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal("choice ReturnToTown", rig.Flow.History.Last().Reason);
        Assert.Empty(rig.AppliedReturnTargets);
        string[] firstRoundSettle = point == StartFaultPoint.TransportFactory
            ? ShowAndDiffs.Append(StartFault(typeName)).ToArray()
            : new[] { StartFault(typeName) };
        Assert.Equal(firstRoundSettle, rig.SettleLines());

        // ---------------- 第二次结算:回调恢复正常,完整走完 ----------------
        rig.EnterTownAndDungeon();
        HostedRoomRun secondRun = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Assert.NotSame(firstRun, secondRun);
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.Equal(2, calls);   // 第二次结算里同一个回调被正常调了一次
        Assert.Equal(2, rig.ProbeCalls);
        Assert.True(rig.Ready.HoldForMenu);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.DungeonCtrl.Phase);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.NotNull(rig.SettleCtrl.Summary);
        Assert.False(rig.SettleCtrl.Summary.IsTimedOut);
        Assert.NotNull(secondRun.HudOverlay);
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);
        Assert.Equal(new ushort[] { 72, 73, 74 }, rig.Transport.Sent.Select(s => s.PacketId).ToArray());
        Assert.Equal(new byte[] { 0, 0 }, rig.Transport.Sent[2].Payload);   // 选牌:模式 0、第 0 槽有权 → 下标 0
        Assert.Equal((byte)0, rig.SettleCtrl.SelectedCardIndex);
        Assert.Equal(0, rig.SettleCtrl.RetryCycleCount);
        Assert.True(rig.SettleCtrl.RemainingCountdownMs > 0);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);

        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, rig.Transport.Sent.Select(s => s.PacketId).ToArray());
        Assert.Equal(new byte[] { 1, 2 }, rig.Transport.Sent[3].Payload);   // 1/75:执行标志 1、命令 2(回城)
        OnlineSettlementReturnTarget target = Assert.Single(rig.AppliedReturnTargets);
        Assert.Equal(OnlineSettlementReturnSource.ServerUserArea, target.Source);
        Assert.Equal(new TownPlace(1, 2, TownEntry.Return), target.Place);
        Assert.Equal(new TownPlace(1, 2, TownEntry.Return), rig.RawWorld.CreatedPlaces.Last());   // 落点:服务端给的区域(不是默认回城点 (1,1))
        Assert.Equal(2, rig.Flow.Settlements);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("online settlement ReturnToTown", rig.Flow.History.Last().Reason);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(rig.SettleCtrl.IsActive);
        Assert.Null(secondRun.HudOverlay);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Equal(0, rig.Disconnects);

        string[] secondRoundSettle =
        {
            OnlineSettlementGapA2Kit.Sent72, OnlineSettlementGapA2Kit.Ok72,
            OnlineSettlementGapA2Kit.Sent73, OnlineSettlementGapA2Kit.Ok73,
            OnlineSettlementGapA2Kit.Sent74, OnlineSettlementGapA2Kit.Ok74,
            OnlineSettlementGapA2Kit.ChooseReturnByPlayer,
            "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=area_pair",
        };
        Assert.Equal(firstRoundSettle.Concat(ShowAndDiffs).Concat(secondRoundSettle).ToArray(), rig.SettleLines());
        Assert.Single(rig.SettleLines(), l => l.Contains("event=fail"));
        Assert.DoesNotContain(rig.LogSnapshot(), l => l.Contains(SecretMessage) || l.Contains("SettlementHandlerFault"));
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("SyntheticStartFault")]
    public void A_start_fault_on_a_timed_out_settlement_falls_back_to_the_generic_timeout_abort_like_having_no_subscriber(string typeName)
    {
        // 保护:结算通知超时(服务端只回 0x22、等不到 0x23)那条路同样走 OnSettlementStarted——本地评分回调抛异常时就地吞掉、撤回挂起,
        // 流程控制器落到通用的"战斗超时中止"收尾(离开(Timeout)+ 失败(Timeout/Combat)、回城、记一次失败而不是结算、原因 "online abort Timeout"),
        // 与没有订阅者时一致;日志里展示行没写出(抛在展示之前),只多一行 start_fault。
        int calls = 0;
        using var rig = new FaultRig(
            configureSettle: o => o.LocalResultComputer = (_, _, _) =>
            {
                calls++;
                throw MakeFault(typeName);
            },
            configureDungeon: d => d.CombatOptions = new OnlineDungeonCombatOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SettlementTimeout = TimeSpan.FromMilliseconds(80),
            });
        WireStandardResponder(rig.Port, monsterCount: 1, omitSettlementB: true);   // 只回 0x22,0x23 永不到:必超时,没有竞态

        rig.EnterTownAndDungeon();
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        int pumpCalls = 0;
        Action<HostedRoomRun> pump = _ => pumpCalls++;
        run.Pump = pump;
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Idle);

        Assert.Equal(1, calls);
        Assert.Equal(1, rig.ProbeCalls);
        Assert.False(rig.Ready.HoldForMenu);
        Assert.False(rig.SettleCtrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.Null(rig.SettleCtrl.Summary);
        Assert.Null(run.HudOverlay);
        Assert.Same(pump, run.Pump);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Empty(rig.Transport.Sent);

        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(1, rig.Flow.Failures);
        Assert.Equal(0, rig.Flow.Settlements);
        Assert.Equal("online abort Timeout", rig.Flow.History.Last().Reason);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, rig.DungeonCtrl.ActiveNotice?.Kind);
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(0, rig.Disconnects);
        Assert.Empty(rig.AppliedReturnTargets);

        // 整份日志:进副本三行、离开(结算通知超时:结果解析成功、奖励不适用)、start_fault、通用超时中止的离开与失败两行
        string[] expected =
        {
            "DNF_ONLINE stage=dungeon event=select outcome=enter difficulty=0 max=1",
            "DNF_ONLINE stage=dungeon event=enter areakind=unknown entrance=true difficulty=0",
            "DNF_ONLINE stage=dungeon event=room status=Ready multi=off spawned=1 expected=1 rooms=1",
            "DNF_ONLINE stage=dungeon event=leave reason=SettlementTimeout reported=1 dropped=0 rooms=1 result=Succeeded reward=NotApplicable",
            StartFault(typeName),
            "DNF_ONLINE stage=dungeon event=leave reason=Timeout reported=1 dropped=0 rooms=1",
            "DNF_ONLINE stage=dungeon event=fail reason=Timeout step=Combat",
        };
        Assert.Equal(expected, rig.LogSnapshot());
    }
}
