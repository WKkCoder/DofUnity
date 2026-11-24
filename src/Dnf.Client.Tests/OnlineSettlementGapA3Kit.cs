using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 第三轮缺口补测"(OnlineSettlementGapA3*)共用夹具:
/// 联网进副本控制器在"结算交接"那一段写出的日志行(整行字面量,手算写出,不从生产代码取)、
/// 一个"只带联网进副本控制器、不挂结算控制器、订阅者由用例自己摆"的场景台 <see cref="HandoffScene"/>(复用上两轮的 <see cref="SettleRig"/>)、
/// 以及几种已知类型的合成异常。全部是合成数据(编号 9101 等),不含任何真实数值。
/// </summary>
internal static class OnlineSettlementGapA3Kit
{
    // ----- 进副本三行(两条路相同) -----
    //  select:副本权限表给该副本的权限档是 1 → 难度上限 1;选的难度 0。
    //  enter:夹具没给当前区域(种类写 unknown);英雄等级缺省 1,等级 ≤ 1 一律算入口区域 → entrance=true。
    //  room:一只怪、单房间 → 摆出 1 只、预期 1 只、进过 1 个房间。
    public const string SelectEnter = "DNF_ONLINE stage=dungeon event=select outcome=enter difficulty=0 max=1";
    public const string EnterLine = "DNF_ONLINE stage=dungeon event=enter areakind=unknown entrance=true difficulty=0";
    public const string RoomReady = "DNF_ONLINE stage=dungeon event=room status=Ready multi=off spawned=1 expected=1 rooms=1";

    /// <summary>进副本三行。</summary>
    public static readonly string[] Opening = { SelectEnter, EnterLine, RoomReady };

    // ----- 结算交接那一段的日志行 -----
    //  上报 1(夹具杀光唯一一只怪并全部上报)、丢弃 0、房间 1。
    /// <summary>结算通知超时路径开局的"离开"行:0x22 解析成功、0x23 一直没来 → 奖励"不适用"。</summary>
    public const string LeaveSettlementTimeout =
        "DNF_ONLINE stage=dungeon event=leave reason=SettlementTimeout reported=1 dropped=0 rooms=1 result=Succeeded reward=NotApplicable";

    /// <summary>正常结算路径的"离开"行:两个结算通知都解析成功。</summary>
    public const string LeaveReturnReady =
        "DNF_ONLINE stage=dungeon event=leave reason=ReturnReady reported=1 dropped=0 rooms=1 result=Succeeded reward=Succeeded";

    /// <summary>通用的"战斗超时中止"那两行(超时路径上没人接手挂起时落到这里):离开原因取战斗结局名 Timeout,不带结果/奖励解析状态。</summary>
    public const string LeaveTimeout = "DNF_ONLINE stage=dungeon event=leave reason=Timeout reported=1 dropped=0 rooms=1";

    public const string FailTimeoutCombat = "DNF_ONLINE stage=dungeon event=fail reason=Timeout step=Combat";

    /// <summary>订阅者抛异常时控制器写的失败行:原因固定 SettlementHandlerFault,步骤写异常的类型名(不含命名空间、不含消息)。</summary>
    public static string HandlerFault(string exceptionTypeName)
    {
        return "DNF_ONLINE stage=dungeon event=fail reason=SettlementHandlerFault step=" + exceptionTypeName;
    }

    /// <summary>把若干行(字符串)与行数组按顺序拼成期望的整份日志序列。</summary>
    public static string[] Lines(params object[] parts) => OnlineSettlementGapA2Kit.Seq(parts);

    // ----- 订阅者的几种行为 -----

    /// <summary>"结算开始"事件订阅者的行为。</summary>
    public enum Subscriber
    {
        /// <summary>不订阅(事件上没有任何订阅者)。</summary>
        None,

        /// <summary>订阅、只记一下、不要求挂起也不抛。</summary>
        Plain,

        /// <summary>订阅、要求挂起等菜单、不抛。</summary>
        Hold,

        /// <summary>订阅、抛异常(抛之前没有要求挂起)。</summary>
        ThrowOnly,

        /// <summary>订阅、先要求挂起、再抛异常。</summary>
        HoldThenThrow,
    }

    /// <summary>自定义的合成异常(类型名固定,用来证明日志里写的是"实际抛出的类型名"而不是某个写死的名字)。</summary>
    internal sealed class SyntheticSubscriberFault : Exception
    {
        public SyntheticSubscriberFault() : base("synthetic subscriber fault")
        {
        }
    }

    /// <summary>按类型名造一个合成异常;类型名既是造异常的依据,也是期望日志里的字面量。</summary>
    public static Exception MakeFault(string typeName)
    {
        switch (typeName)
        {
            case "InvalidOperationException":
                return new InvalidOperationException("synthetic subscriber fault");
            case "NotSupportedException":
                return new NotSupportedException("synthetic subscriber fault");
            case "SyntheticSubscriberFault":
                return new SyntheticSubscriberFault();
            default:
                throw new ArgumentException("未知的合成异常类型名: " + typeName, nameof(typeName));
        }
    }

    // ----- 场景台 -----

    /// <summary>
    /// 带真联网进副本控制器(<see cref="SettleRig"/>,不挂结算控制器)的"结算交接"场景台:
    /// 一间单房间、一只怪的联网副本;<paramref name="timedOut"/> 为真时服务端只回 0x22、不回 0x23,战斗在 80 毫秒后以"结算通知超时"收场,
    /// 否则标准回 0x22 + 0x23。按 <paramref name="subscriber"/> 在 <c>SettlementStarted</c> 上摆订阅者(<paramref name="faultTypeName"/> 是抛出的异常类型名)。
    /// 时间全部走夹具的逻辑时钟(每圈 20 毫秒),只有战斗任务自己的 80 毫秒结算等待用真实时间(等不到 0x23 就一定超时,没有竞态)。
    /// </summary>
    internal sealed class HandoffScene : IDisposable
    {
        public SettleRig Rig { get; }

        /// <summary>订阅者被调用的次数。</summary>
        public int Invocations { get; private set; }

        /// <summary>最近一次交给订阅者的交接对象(没订阅或还没触发是 null)。</summary>
        public OnlineDungeonSettlementReady Ready { get; private set; }

        public HandoffScene(bool timedOut, Subscriber subscriber, string faultTypeName = null)
        {
            Action<OnlineDungeonFlowOptions> configureDungeon = null;
            if (timedOut)
            {
                configureDungeon = d => d.CombatOptions = new OnlineDungeonCombatOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    SettlementTimeout = TimeSpan.FromMilliseconds(80),
                };
            }
            Rig = new SettleRig(attachSettlement: false, configureDungeon: configureDungeon);
            WireStandardDungeonResponder(Rig, monsterCount: 1, omitSettlementB: timedOut);

            if (subscriber != Subscriber.None)
            {
                Rig.DungeonCtrl.SettlementStarted += ready =>
                {
                    Invocations++;
                    Ready = ready;
                    if (subscriber == Subscriber.Hold || subscriber == Subscriber.HoldThenThrow)
                    {
                        ready.HoldForMenu = true;
                    }
                    if (subscriber == Subscriber.ThrowOnly || subscriber == Subscriber.HoldThenThrow)
                    {
                        throw MakeFault(faultTypeName);
                    }
                };
            }
        }

        /// <summary>进城镇 → 进副本 → 杀光唯一一只怪(之后战斗会自己发结算命令)。</summary>
        public void EnterAndKillEverything()
        {
            Rig.EnterTownAndDungeon();
            Rig.KillAllInCurrentRun();
        }

        /// <summary>日志快照(只有联网进副本控制器的行,因为没挂结算控制器)。</summary>
        public string[] Logs() => Rig.LogSnapshot();

        /// <summary>流程已回到城镇、联网进副本控制器已回空闲。</summary>
        public bool BackInTownAndIdle => Rig.Flow.Stage == GameStage.Town && Rig.DungeonCtrl.Phase == OnlineDungeonFlowPhase.Idle;

        /// <summary>流程里所有切换的原因,按先后。</summary>
        public string[] FlowReasons() => Rig.Flow.History.Select(h => h.Reason).ToArray();

        public void Dispose()
        {
            Rig.Dispose();
        }
    }
}
