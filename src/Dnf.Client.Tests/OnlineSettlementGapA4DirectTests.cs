using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA4Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 直接调控制器公开的 <c>OnSettlementStarted</c>(<see cref="DirectRig"/>:手动逻辑时钟、无线程)的几个小用例:
/// 早退条件(已释放/空交接对象)保持不变;断线分支里宿主的"断线回调"抛异常同样就地吞掉;失败会结束"再来一次"的重试链(计数清零)。
/// 因为是直接调用,异常若传出控制器会直接让用例失败——这是"异常不外传"最直接的证据。期望值是手算的整行字面量。
/// </summary>
public sealed class OnlineSettlementGapA4DirectTests
{
    [Fact]
    public void A_null_handover_is_ignored_without_any_state_change_log_or_subscription()
    {
        // 保护:早退条件之一——交接对象为空时什么都不做(不抛、不写日志、不挂传输);异常保护不能把这条早退改掉。
        using var rig = new DirectRig();

        Exception thrown = Record.Exception(() => rig.Ctrl.OnSettlementStarted(null));

        Assert.Null(thrown);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Empty(rig.Logs);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Equal(0, rig.Disconnects);
    }

    [Fact]
    public void A_disposed_controller_ignores_a_settlement_start_and_does_not_take_the_hold()
    {
        // 保护:早退条件之二——已释放的控制器收到结算开始时不接管:挂起标志保持 false(不被置 true 也不需要撤回)、不写日志、不挂传输、不进活动阶段。
        using var rig = new DirectRig();
        rig.Ctrl.Dispose();

        rig.StartSettlement();

        Assert.False(rig.Ready.HoldForMenu);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.Summary);
        Assert.Empty(rig.Logs);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Empty(rig.Transport.Sent);
    }

    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("NotSupportedException")]
    [InlineData("SyntheticStartFault")]
    public void A_throwing_disconnect_callback_during_a_start_on_a_dead_transport_is_contained_too(string typeName)
    {
        // 保护:传输一开始就已断线时,处理函数走断线分支并调宿主的"断线回调";该回调抛异常也不能传出控制器——
        // 日志是展示行 + 断线行(原有,阶段仍是空闲)+ 一行 start_fault;挂起标志撤回、控制器空闲、两个传输订阅都摘掉、没发出任何命令。
        int calls = 0;
        using var rig = new DirectRig(o => o.OnDisconnected = () =>
        {
            calls++;
            throw MakeFault(typeName);
        });
        rig.Transport.IsDisconnected = true;
        rig.PollAt(1000);

        Exception thrown = Record.Exception(() => rig.StartSettlement());

        Assert.Null(thrown);
        Assert.Equal(1, calls);
        Assert.False(rig.Ready.HoldForMenu);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.Summary);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        Assert.Empty(rig.Transport.Sent);
        string[] expected =
        {
            OnlineSettlementGapA2Kit.Show,
            "DNF_ONLINE stage=settle event=fail reason=disconnected step=Idle",
            StartFault(typeName),
        };
        Assert.Equal(expected, rig.Logs.ToArray());
    }

    [Fact]
    public void A_disconnect_callback_that_does_not_throw_keeps_the_existing_dead_transport_start_behavior()
    {
        // 保护(对照):断线回调正常时,传输一开始就已断线的处理与以前一字不变——展示行 + 断线行(各一行)、回调被调一次、控制器空闲、
        // 订阅摘掉;没有 start_fault 行;挂起标志仍是进入时置的 true(流程控制器自己会在断线清理里收尾,这里不改)。
        using var rig = new DirectRig();
        rig.Transport.IsDisconnected = true;
        rig.PollAt(1000);

        rig.StartSettlement();

        Assert.Equal(1, rig.Disconnects);
        Assert.True(rig.Ready.HoldForMenu);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);
        string[] expected =
        {
            OnlineSettlementGapA2Kit.Show,
            "DNF_ONLINE stage=settle event=fail reason=disconnected step=Idle",
        };
        Assert.Equal(expected, rig.Logs.ToArray());
    }

    [Fact]
    public void A_start_fault_ends_the_retry_chain_so_the_next_chain_counts_again_from_one()
    {
        // 保护:"再来一次"链上的下一次结算开局失败(流程走默认结算退出回城,链到此结束)时,重试计数清零——与断线收尾一致;
        // 否则下一条链的第一次重试会被记成第 2 圈。手算:链 1 第一次重试 cycle=1;失败后清零;链 2 第一次重试仍是 cycle=1,全程没有 cycle=2。
        bool fail = false;
        using var rig = new DirectRig(o => o.GetProgress = () =>
        {
            if (fail)
            {
                throw MakeFault("InvalidOperationException");
            }
            return null;
        });
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
        {
            if (payload[1] == (byte)EplpCommand.Retry)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
                PushNoti2(rig.Transport);
            }
        });
        const string retryCycle1 = "DNF_ONLINE stage=settle event=retry status=reenter cycle=1";
        const string retryCycle2 = "DNF_ONLINE stage=settle event=retry status=reenter cycle=2";

        // 链 1:第一次结算 → 选再次挑战 → 服务端确认 → 重试计数 1
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.Poll();
        Assert.Equal(new[] { SettlementChoice.Retry }, rig.Completed);
        Assert.Equal(1, rig.Ctrl.RetryCycleCount);

        // 链 1 的下一次结算开局失败:不外传、控制器空闲、挂起撤回、计数清零
        fail = true;
        rig.StartSettlement();
        Assert.False(rig.Ready.HoldForMenu);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(0, rig.Ctrl.RetryCycleCount);
        Assert.Equal(1, rig.LogCount(StartFault("InvalidOperationException")));

        // 链 2:恢复正常,第一次重试仍记第 1 圈
        fail = false;
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        rig.Poll();
        Assert.Equal(new[] { SettlementChoice.Retry, SettlementChoice.Retry }, rig.Completed);
        Assert.Equal(1, rig.Ctrl.RetryCycleCount);
        Assert.Equal(2, rig.LogCount(retryCycle1));
        Assert.Equal(0, rig.LogCount(retryCycle2));
    }
}
