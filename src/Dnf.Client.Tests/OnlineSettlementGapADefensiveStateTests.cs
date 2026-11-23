using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算控制器里三处"防御性"分支的测试。这三处在公开操作序列下碰不到(各自前面另有一道门已经挡住了同样的情形),
/// 所以只能先用反射把控制器私有状态拨到"那道门失效"的位置,再看这道后备防线的可观察行为:
/// ① 同一条 1/75 命令绝不自动重发(哪怕"被拒记录"丢了);
/// ② 结算通知超时的菜单里,放弃回城的 1/45 绝不重发(哪怕菜单又可选了);
/// ③ 被拒的非回城选择把菜单退回降级之后,若倒计时没在跑就立刻重新起算。
/// <para>
/// 依赖的私有名字(都是 <see cref="OnlineSettlementController"/> 的私有字段,用反射改值,全部写在这里):
/// <c>_rejectedChoicesMask</c>(被 1/75 拒绝过的选择位掩码)、<c>_pendingChoice</c>(待决选择)、<c>_phase</c>(阶段)、
/// <c>_countdownStarted</c> / <c>_countdownStartPending</c>(倒计时已开始 / 待启动)。
/// 每个测试先走真实的公开流程到达某个状态,只在注释标明的那一步改私有字段,其余全是公开 API 与可观察结果(发出的包、选定回调、倒计时剩余)。
/// </para>
/// </summary>
public sealed class OnlineSettlementGapADefensiveStateTests
{
    private static void SetPrivate(OnlineSettlementController ctrl, string name, object value)
    {
        FieldInfo field = typeof(OnlineSettlementController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(ctrl, value);
    }

    private static int CountSent(DirectRig rig, ushort packetId) => rig.Transport.Sent.Count(s => s.PacketId == packetId);

    [Fact]
    public void The_same_1_75_command_is_never_sent_twice_even_if_the_record_of_its_rejection_is_lost()
    {
        // 保护:同一条 1/75 命令绝不自动重发。再次挑战(命令 0)被服务端拒绝后,正常情况下"被拒记录"会挡住再次选择;
        // 这里把被拒记录抹掉(私有掩码清零),再选一次再次挑战——后备防线("这条命令已发过")必须挡住:选择返回假、不发第二条 1/75、
        // 待决选择清空、菜单仍留在降级菜单可选别的项;命令彼此独立,选"选其他副本"(命令 1)照常能发。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
        {
            byte cmd = payload[1];
            if (cmd == (byte)EplpCommand.Retry)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(OnlineSettlementController.ErrorNotLeader, cmd));
            }
            else
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(cmd));
            }
        });
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));   // 正常的第一道门:被拒记录
        Assert.Equal(1, CountSent(rig, 75));

        // 抹掉被拒记录:第一道门失效,只剩"已发过这条命令"的后备防线
        SetPrivate(rig.Ctrl, "_rejectedChoicesMask", 0);
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));

        Assert.False(rig.Ctrl.Choose(SettlementChoice.Retry));
        Assert.Equal(1, CountSent(rig, 75));
        Assert.Equal(SettlementChoice.None, rig.Ctrl.PendingChoice);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);

        // 别的命令不受影响
        Assert.True(rig.Ctrl.Choose(SettlementChoice.SelectDungeon));
        Assert.Equal(2, CountSent(rig, 75));
        Assert.Equal(new byte[] { 1, 1 }, rig.Transport.Sent[^1].Payload);
    }

    [Fact]
    public void The_give_up_command_of_a_timed_out_settlement_is_never_sent_twice_even_if_the_menu_becomes_choosable_again()
    {
        // 保护:结算通知超时的菜单只开放返回城镇,选择后发一次 1/45(放弃)并等回城信号;放弃绝不重发。
        // 正常情况下选择之后待决选择非空,菜单不可再选;这里把待决选择与阶段拨回"降级菜单可选"(私有字段),
        // 再选一次回城——必须不发第二条 1/45,而是直接安全回城(选定回调收到回城)。
        using var rig = new DirectRig();
        rig.PollAt(1000);
        rig.StartSettlement(timedOut: true);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(new ushort[] { 45 }, rig.Transport.Sent.Select(s => s.PacketId).ToArray());
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));   // 正常的门:有待决选择时不可再选
        Assert.Empty(rig.Completed);

        // 拨回"菜单又可选"
        SetPrivate(rig.Ctrl, "_pendingChoice", SettlementChoice.None);
        SetPrivate(rig.Ctrl, "_phase", OnlineSettlementPhase.MenuDegraded);
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(new ushort[] { 45 }, rig.Transport.Sent.Select(s => s.PacketId).ToArray());   // 仍然只有一条 1/45
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(1, rig.LogCount(P + " event=town reason=player area=pre_dungeon signal=fallback"));
    }

    [Fact]
    public void A_rejection_that_returns_the_menu_to_degraded_restarts_a_countdown_that_was_not_running()
    {
        // 保护:被拒的非回城选择把菜单退回降级菜单时,若倒计时既没开始也没待启动,就从这一刻起重新起算(剩余回到满额),之后继续按轮询走;
        // 倒计时正常在跑时不重置(见另一半断言:先走到剩 4000 毫秒的状态,再拨掉倒计时标志,拒绝回复到来后回到 5000 并继续递减)。
        using var rig = new DirectRig();   // 菜单倒计时 5000 毫秒
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
            rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(OnlineSettlementController.ErrorNotLeader, payload[1])));
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);

        // 在菜单里停 1000 毫秒:剩余 4000
        long menuEntryMs = rig.NowMs;
        rig.PollAt(menuEntryMs + 1000);
        Assert.Equal(4000, rig.Ctrl.RemainingCountdownMs);

        // 拨掉倒计时标志(既没开始也没待启动),再选再次挑战;拒绝回复在下一次轮询(逻辑时钟 menuEntry + 1016)读到
        SetPrivate(rig.Ctrl, "_countdownStarted", false);
        SetPrivate(rig.Ctrl, "_countdownStartPending", false);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.PollAt(menuEntryMs + 1016);

        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.EplpRejectedNotLeader, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);

        // 重新起算之后继续递减:再过 2000 毫秒剩 3000
        rig.PollAt(menuEntryMs + 3016);
        Assert.Equal(3000, rig.Ctrl.RemainingCountdownMs);
        Assert.Empty(rig.Completed);
    }
}
