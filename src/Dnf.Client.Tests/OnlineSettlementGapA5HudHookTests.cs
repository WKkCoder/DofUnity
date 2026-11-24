using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算控制器挂在联网房间局上的绘制钩子(<see cref="HostedRoomRun.HudOverlay"/>)的生命周期:
/// 进副本时没有、结算菜单显示期间有(指向控制器的 <c>BuildHud</c>)、结算收尾(复位)后摘掉——不残留到下一局或回城之后。
/// 用带真联网进副本控制器 + 结算控制器的 <see cref="OnlineSettlementGapAKit.SettleRig"/>(逻辑时钟轮询)。
/// </summary>
public sealed class OnlineSettlementGapA5HudHookTests
{
    [Fact]
    public void The_hud_hook_is_installed_for_the_menu_and_removed_when_the_settlement_finishes()
    {
        // 保护:结算收尾的复位把房间局上的绘制钩子摘掉(否则回城之后旧局的菜单覆盖层仍挂着);
        // 同时钉住:进副本时没有钩子、菜单显示期间有钩子(且原有的每帧推进被摘掉)。
        using var rig = new SettleRig();
        WireStandardDungeonResponder(rig);
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
        {
            rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
            PushNoti2(rig.Transport);
        });

        rig.EnterTownAndDungeon();
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        Assert.Null(run.HudOverlay);

        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.NotNull(run.HudOverlay);
        Assert.Null(run.Pump);

        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.SettleCtrl.Phase == OnlineSettlementPhase.Idle);

        Assert.Null(run.HudOverlay);
        Assert.False(rig.SettleCtrl.IsActive);
        Assert.Equal(1, rig.Flow.Settlements);
        Assert.Equal(0, rig.Flow.Failures);
    }
}
