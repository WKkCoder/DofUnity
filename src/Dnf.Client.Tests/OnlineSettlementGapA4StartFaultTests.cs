using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapA4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算控制器在"结算开始"处理函数(<c>OnSettlementStarted</c>)中途遇到异常时的收拾(OnlineSettlementGapA4*):
/// 真结算控制器(不是假订阅者)挂在真联网进副本控制器上,对处理函数里每一个能注入异常的宿主回调/传输各写一个 Theory(多种异常类型,类型名写在 InlineData 里)。
/// 每个用例都断言:异常没有传出控制器与流程、控制器回到完全空闲(无绘制钩子、无传输订阅、这一局的 Pump 原样还在)、
/// <c>HoldForMenu</c> 撤回、流程控制器进"结算退出"而不是"挂起等结算"、按这一局自己的回城计时(虚拟时间)回城、
/// 日志里恰好一行失败行(令牌 start_fault + 异常类型名,不含消息)。
/// 夹具:一只怪、单房间、全部上报,房间局打完后等 30000 毫秒(虚拟)回城;期望值是手算的整行字面量。
/// </summary>
public sealed class OnlineSettlementGapA4StartFaultTests
{
    // 进副本三行 + 离开行(一只怪、单房间,与 A2 全流程用例同一份手算):
    //  select:权限表给该副本的权限档是 1 → 难度上限 1;选的难度 0。
    //  enter:夹具没给当前区域(种类 unknown);英雄等级缺省 1,等级 ≤ 1 一律算入口区域 → entrance=true。
    //  room:一只怪、单房间 → 摆出 1、预期 1、进过 1 个房间。
    //  leave:两个结算通知都解析成功;上报 1(夹具杀光唯一一只怪)、丢弃 0、房间 1。
    private static readonly string[] DungeonLines =
    {
        "DNF_ONLINE stage=dungeon event=select outcome=enter difficulty=0 max=1",
        "DNF_ONLINE stage=dungeon event=enter areakind=unknown entrance=true difficulty=0",
        "DNF_ONLINE stage=dungeon event=room status=Ready multi=off spawned=1 expected=1 rooms=1",
        "DNF_ONLINE stage=dungeon event=leave reason=ReturnReady reported=1 dropped=0 rooms=1 result=Succeeded reward=Succeeded",
    };

    /// <summary>一个注入方案:配置结算选项的回调、被调到的次数、(传输类注入点才有的)自定义传输,以及抛出点是否在展示日志之后。</summary>
    private sealed class Injection
    {
        public int Calls;
        public FaultTransport Transport;
        public Action<OnlineSettlementOptions> Configure;
        public bool ShowAlreadyLogged;
    }

    private static Injection Inject(StartFaultPoint point, string typeName)
    {
        var inj = new Injection();
        Exception Boom()
        {
            inj.Calls++;
            return MakeFault(typeName);
        }

        switch (point)
        {
            case StartFaultPoint.GetCurrentTownPlace:
                inj.Configure = o => o.GetCurrentTownPlace = () => throw Boom();
                break;
            case StartFaultPoint.GetProgress:
                inj.Configure = o => o.GetProgress = () => throw Boom();
                break;
            case StartFaultPoint.GetSelfUniqueId:
                inj.Configure = o => o.GetSelfUniqueId = () => throw Boom();
                break;
            case StartFaultPoint.LocalResultComputer:
                inj.Configure = o => o.LocalResultComputer = (_, _, _) => throw Boom();
                break;
            case StartFaultPoint.GetPartySlot:
                inj.Configure = o => o.GetPartySlot = () => throw Boom();
                break;
            case StartFaultPoint.TransportFactory:
                inj.ShowAlreadyLogged = true;
                inj.Configure = o => o.TransportFactory = _ => throw Boom();
                break;
            case StartFaultPoint.TransportDisconnectedSubscribe:
                inj.ShowAlreadyLogged = true;
                inj.Transport = new FaultTransport(onDisconnectedSubscribe: Boom);
                inj.Configure = o => o.TransportFactory = _ => inj.Transport;
                break;
            case StartFaultPoint.TransportIsDisconnected:
                inj.ShowAlreadyLogged = true;
                inj.Transport = new FaultTransport(onIsDisconnected: Boom);
                inj.Configure = o => o.TransportFactory = _ => inj.Transport;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point), point, "未知的注入点");
        }
        return inj;
    }

    private static bool LeftInDungeon(FaultRig rig)
    {
        return rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.HoldingForSettlement
            || rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.SettlingExit;
    }

    /// <summary>
    /// 共用场景:进副本、给这一局装一个哨兵 Pump、杀光、轮询到流程控制器处理完"结算开始"(要么挂起要么结算退出),
    /// 然后逐项断言失败后的现场、再用虚拟时间推进到默认回城。
    /// </summary>
    private static void AssertStartFaultIsContained(StartFaultPoint point, string typeName)
    {
        Injection inj = Inject(point, typeName);
        using var rig = new FaultRig(inj.Configure);
        WireStandardResponder(rig.Port);

        rig.EnterTownAndDungeon();
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        int pumpCalls = 0;
        Action<HostedRoomRun> pump = _ => pumpCalls++;
        run.Pump = pump;   // 房间托管的那一局原有的 Pump(多房间时是联网状态机的盯梢):失败路径必须原样留着
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => LeftInDungeon(rig));
        OnlineSettlementController ctrl = rig.SettleCtrl;

        // ---- 1. 注入的回调确实被调到恰好一次;异常没有传出结算控制器(排在它后面的探针被调到)、也没有被流程控制器接住 ----
        Assert.Equal(1, inj.Calls);
        Assert.Equal(1, rig.ProbeCalls);
        Assert.DoesNotContain(rig.LogSnapshot(), l => l.Contains("SettlementHandlerFault"));

        // ---- 2. 结算控制器回到完全空闲:无绘制钩子、无传输订阅、无摘要、这一局的 Pump 原样还在 ----
        Assert.False(ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, ctrl.Phase);
        Assert.Null(ctrl.Summary);
        Assert.Equal(0, ctrl.RetryCycleCount);
        Assert.Null(run.HudOverlay);
        Assert.Same(pump, run.Pump);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Empty(rig.Transport.Sent);
        if (inj.Transport != null)
        {
            Assert.Equal(0, inj.Transport.FrameSubscribers);
            Assert.Equal(0, inj.Transport.DisconnectSubscribers);
            Assert.Equal(0, inj.Transport.SentCount);
            Assert.False(inj.Transport.Disposed);   // 宿主工厂给的传输不归控制器所有,不能被释放
        }

        // ---- 3. 流程控制器:不挂起,走"没有订阅者"同样的默认结算退出;这一圈还没到回城计时,仍在副本里 ----
        Assert.NotNull(rig.Ready);
        Assert.False(rig.Ready.HoldForMenu);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.DungeonCtrl.Phase);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.Settlements);
        Assert.Empty(rig.AppliedReturnTargets);

        // ---- 4. 日志:结算行恰好是"(已写出的展示+差异行)+ 一行 start_fault + 异常类型名";整份日志里没有消息文本 ----
        string[] expectedSettle = inj.ShowAlreadyLogged
            ? ShowAndDiffs.Append(StartFault(typeName)).ToArray()
            : new[] { StartFault(typeName) };
        Assert.Equal(expectedSettle, rig.SettleLines());
        Assert.Single(rig.SettleLines(), l => l.Contains("event=fail"));
        string[] logsAtFailure = rig.LogSnapshot();
        Assert.Equal(DungeonLines.Concat(expectedSettle).ToArray(), logsAtFailure);
        Assert.DoesNotContain(logsAtFailure, l => l.Contains(SecretMessage));

        // ---- 5. 默认结算退出:Pump 仍每圈被叫一次;这一局自己的回城计时到点才回城——打完那一圈起算 30000 毫秒,
        //         每圈 20 毫秒、打完那一圈已累计 20,故在"打完时刻 + 30000 - 20"那一圈回城(不早、不被挂起拖晚) ----
        int pumpBefore = pumpCalls;
        rig.Step();
        Assert.Equal(pumpBefore + 1, pumpCalls);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.DungeonCtrl.Phase);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);

        Assert.True(rig.FinishedAtMs > 0, "没有观察到这一局打完");
        long returnedAt = rig.StepUntilTown();
        Assert.Equal(rig.FinishedAtMs + FaultRig.ReturnDelayMs - 20, returnedAt);
        rig.Step();
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.DungeonCtrl.Phase);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("choice ReturnToTown", rig.Flow.History.Last().Reason);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(ctrl.IsActive);
        Assert.Empty(rig.AppliedReturnTargets);
        Assert.Equal(0, rig.Disconnects);
        Assert.Equal(logsAtFailure, rig.LogSnapshot());   // 回城之后不再多写任何行
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_throwing_current_town_place_callback_is_contained_and_the_flow_takes_the_default_settlement_exit(string typeName)
    {
        // 保护:宿主的"当前城镇区域"回调(处理函数里第一个被调的宿主回调)抛异常——就地吞掉、控制器空闲、撤回挂起、流程走默认结算退出,日志只有一行失败行。
        AssertStartFaultIsContained(StartFaultPoint.GetCurrentTownPlace, typeName);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_throwing_progress_callback_is_contained_and_the_flow_takes_the_default_settlement_exit(string typeName)
    {
        // 保护:宿主的"英雄进度"回调抛异常——同上,不留软锁。
        AssertStartFaultIsContained(StartFaultPoint.GetProgress, typeName);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_throwing_self_unique_id_callback_is_contained_and_the_flow_takes_the_default_settlement_exit(string typeName)
    {
        // 保护:宿主的"自己的唯一编号"回调抛异常——同上,不留软锁。
        AssertStartFaultIsContained(StartFaultPoint.GetSelfUniqueId, typeName);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_throwing_local_result_computer_is_contained_and_the_flow_takes_the_default_settlement_exit(string typeName)
    {
        // 保护:宿主的"本地评分计算"回调抛异常(上一位工人复现缺陷用的那一处)——同上,不再留成 HoldingForSettlement + 控制器空闲 + 闸门一直关。
        AssertStartFaultIsContained(StartFaultPoint.LocalResultComputer, typeName);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_throwing_party_slot_callback_is_contained_and_the_flow_takes_the_default_settlement_exit(string typeName)
    {
        // 保护:宿主的"队伍槽位"回调抛异常(在摘要构造之前、本地评分之后才被调)——同上,不留软锁。
        AssertStartFaultIsContained(StartFaultPoint.GetPartySlot, typeName);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_throwing_transport_factory_late_in_the_start_is_contained_and_leaves_no_hook_behind(string typeName)
    {
        // 保护:"晚抛"——传输工厂抛异常时摘要已构造、展示日志已写、绘制钩子已挂(Pump 已摘):控制器必须把钩子撤掉、把 Pump 装回、回空闲,
        // 流程走默认结算退出;已经写出的展示/差异行保留(那是抛异常之前的事实),之后只多一行失败行。
        AssertStartFaultIsContained(StartFaultPoint.TransportFactory, typeName);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_transport_that_throws_while_subscribing_to_disconnect_is_contained_and_drops_the_half_made_subscription(string typeName)
    {
        // 保护:传输挂接到一半抛异常(帧事件已挂上、断线事件挂失败)——帧事件的订阅必须被摘掉(订阅数回 0),传输不被释放,其余同上。
        AssertStartFaultIsContained(StartFaultPoint.TransportDisconnectedSubscribe, typeName);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("NullReferenceException")]
    [InlineData("SyntheticStartFault")]
    public void A_transport_that_throws_when_asked_whether_it_is_disconnected_is_contained_and_drops_both_subscriptions(string typeName)
    {
        // 保护:最晚的抛点——传输、取消源、绘制钩子都已就位,问"是否断线"才抛:两个订阅都必须摘掉、钩子撤掉、Pump 装回、没发出任何命令。
        AssertStartFaultIsContained(StartFaultPoint.TransportIsDisconnected, typeName);
    }

    [Fact]
    public void Without_any_fault_the_controller_still_holds_the_exit_hooks_the_overlay_and_starts_the_advance()
    {
        // 保护(对照,防止上面那些用例空转):回调都正常时行为与以前一字不变——挂起标志保持 true、结算控制器离开空闲并开始推进、
        // 流程控制器进"挂起等结算"且闸门关着、绘制钩子挂上、这一局的 Pump 被摘掉(这正是失败路径要"装回"的东西)、两个传输订阅都在、日志里没有任何失败行。
        using var rig = new FaultRig();
        WireStandardResponder(rig.Port);
        rig.EnterTownAndDungeon();
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Action<HostedRoomRun> pump = _ => { };
        run.Pump = pump;
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => LeftInDungeon(rig));

        Assert.Equal(1, rig.ProbeCalls);
        Assert.True(rig.Ready.HoldForMenu);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.DungeonCtrl.Phase);
        Assert.True(rig.Flow.OnlineHoldDungeonExit);
        Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);

        Assert.True(rig.SettleCtrl.IsActive);
        Assert.NotEqual(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.NotNull(rig.SettleCtrl.Summary);
        Assert.NotNull(run.HudOverlay);
        Assert.Null(run.Pump);
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);

        string[] settle = rig.SettleLines();
        Assert.Equal(ShowAndDiffs.Append(Sent72).ToArray(), settle.Take(ShowAndDiffs.Length + 1).ToArray());
        Assert.DoesNotContain(settle, l => l.Contains("event=fail"));
        Assert.DoesNotContain(rig.LogSnapshot(), l => l.Contains("start_fault") || l.Contains("SettlementHandlerFault"));
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("SyntheticStartFault")]
    public void A_cleanup_that_itself_throws_is_contained_and_still_withdraws_the_hold_and_restores_the_pump(string typeName)
    {
        // 保护:收尾(摘传输订阅)本身也抛异常时——异常仍不外传(探针被调到),挂起照样撤回,这一局的 Pump 照样装回,
        // 日志仍只有一行 start_fault 且写的是<b>原始</b>异常的类型名(不是收尾里那个 InvalidCastException);流程走默认结算退出。
        // 收尾中途出错时其余清理只能尽力而为,所以这里不断言绘制钩子与订阅数;摘订阅只在第一次抛(之后正常,让夹具释放时不再出错)。
        int subscribeCalls = 0;
        int unsubscribeCalls = 0;
        var transport = new FaultTransport(
            onDisconnectedSubscribe: () =>
            {
                subscribeCalls++;
                return MakeFault(typeName);
            },
            onFrameUnsubscribe: () => ++unsubscribeCalls == 1 ? new InvalidCastException(SecretMessage) : null);
        using var rig = new FaultRig(o => o.TransportFactory = _ => transport);
        WireStandardResponder(rig.Port);
        rig.EnterTownAndDungeon();
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Action<HostedRoomRun> pump = _ => { };
        run.Pump = pump;
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => LeftInDungeon(rig));

        Assert.Equal(1, subscribeCalls);
        Assert.Equal(1, unsubscribeCalls);   // 收尾确实去摘了、并且在那里抛了
        Assert.Equal(1, rig.ProbeCalls);
        Assert.False(rig.Ready.HoldForMenu);
        Assert.Equal(OnlineDungeonFlowPhase.SettlingExit, rig.DungeonCtrl.Phase);
        Assert.False(rig.Flow.OnlineHoldDungeonExit);
        Assert.False(rig.SettleCtrl.IsActive);
        Assert.Same(pump, run.Pump);
        Assert.Equal(ShowAndDiffs.Append(StartFault(typeName)).ToArray(), rig.SettleLines());
        Assert.DoesNotContain(rig.LogSnapshot(), l => l.Contains("SettlementHandlerFault") || l.Contains(SecretMessage) || l.Contains("InvalidCastException"));
    }

    [Fact]
    public void A_throwing_item_name_resolver_is_absorbed_by_the_summary_builder_and_is_not_a_start_fault()
    {
        // 保护:物品名解析回调抛异常不是"开始失败"的注入点——摘要构造器自己吞掉(名字记为空),结算照常开始、仍挂起、没有失败行。
        // 标准战果里有两个带物品的掉落(一个任务物品、一个奖励槽物品),所以解析器被问到恰好两次(手算:展示行写 drops=2)。
        int calls = 0;
        using var rig = new FaultRig(o => o.ItemNameResolver = _ =>
        {
            calls++;
            throw new InvalidOperationException(SecretMessage);
        });
        WireStandardResponder(rig.Port);
        rig.EnterTownAndDungeon();
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => LeftInDungeon(rig));

        Assert.Equal(2, calls);
        Assert.True(rig.Ready.HoldForMenu);
        Assert.Equal(OnlineDungeonFlowPhase.HoldingForSettlement, rig.DungeonCtrl.Phase);
        Assert.True(rig.SettleCtrl.IsActive);
        Assert.Equal(ShowStandard, rig.SettleLines()[0]);
        Assert.DoesNotContain(rig.LogSnapshot(), l => l.Contains("start_fault") || l.Contains(SecretMessage));
    }
}
