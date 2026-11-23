using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第十九轮补测之二(OnlineDungeonFlowGapR19DisposeNotice):控制器 <c>Dispose()</c> 之后,宿主每帧读的 <c>ActiveNotice</c> 必须是 null——
/// 释放时若还留着一条瞬时城镇提示(放弃回城超时、战斗中止收尾等处设置,缺省 4 秒到期),因为已释放的控制器轮询一上来就返回,
/// 到期检查再也不会跑,这条提示不清掉就会永远挂着、宿主一直画一条过期提示。
/// 同时钉住两条没变的既有行为作为对照:释放之前瞬时提示按期自动消失;<c>PrepareForQuit()</c> 不是释放,不动瞬时提示,控制器仍活着、提示照常到期。
/// 全部合成数据,不连任何服务;时间只走场景台的逻辑时钟。
/// </summary>
public sealed class OnlineDungeonFlowGapR19DisposeNoticeTests
{
    /// <summary>逻辑时钟每圈 +20ms,用场景台的方式轮询控制器与流程若干次。</summary>
    private static void PollSeveralTimes(Rig rig, int times = 5)
    {
        for (int i = 0; i < times; i++)
        {
            rig.NowMs += 20;
            rig.Poll();
        }
    }

    /// <summary>
    /// 走到"战斗中止收尾":进了副本之后宿主把房间中止(换房被拒够次数),控制器停战、放权、回城镇,并在城镇上亮一条瞬时提示
    /// (种类 Aborted;时长取缺省 4000ms,到期时刻 = 这次收尾轮询的逻辑时刻 + 4000)。返回收尾后立刻读到的 <c>ActiveNotice</c>。
    /// </summary>
    private static DungeonSelectNotice ReachTransientTownNotice(Rig rig)
    {
        ServeRoomDungeon(rig);
        EnterDungeon(rig);
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        run.Abort(HostedAbort.MoveRefused);
        rig.PumpControllerUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle, 600);
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        return rig.Ctrl.ActiveNotice;
    }

    [Fact]
    public void A_transient_town_notice_expires_on_schedule_while_the_controller_is_alive()
    {
        // 保护(既有行为,释放前):瞬时城镇提示在到期时刻前一毫秒还在、正好到期那一刻被轮询撤掉。
        // 手算:缺省时长 4000ms,到期时刻 = 收尾轮询的逻辑时刻 NowMs + 4000;所以 NowMs+3999 还在,NowMs+4000 没了。
        using var rig = new Rig();
        DungeonSelectNotice notice = ReachTransientTownNotice(rig);

        Assert.NotNull(notice);
        Assert.Equal(DungeonSelectNoticeKind.Aborted, notice.Kind);
        Assert.Equal(rig.NowMs + 4000, notice.ExpiresAtMs);

        rig.Ctrl.Poll(rig.NowMs + 3999);
        Assert.Same(notice, rig.Ctrl.ActiveNotice);
        rig.Ctrl.Poll(rig.NowMs + 4000);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Fact]
    public void Disposing_the_controller_clears_a_pending_transient_town_notice_and_it_stays_gone()
    {
        // 保护:释放时瞬时提示还没到期,释放之后 ActiveNotice 立刻是 null(对照:释放前同一时刻它是一条 Aborted 提示),
        // 之后怎么轮询——包括逻辑时钟越过原本的到期时刻、反复释放——都仍是 null,且不抛异常。
        using var rig = new Rig();
        DungeonSelectNotice notice = ReachTransientTownNotice(rig);
        long dueAt = notice.ExpiresAtMs;

        // 对照(防空转):释放前此刻就是有一条没到期的提示
        Assert.NotNull(rig.Ctrl.ActiveNotice);
        Assert.Equal(DungeonSelectNoticeKind.Aborted, rig.Ctrl.ActiveNotice.Kind);
        Assert.True(rig.NowMs < dueAt);

        rig.Ctrl.Dispose();

        Assert.Null(rig.Ctrl.ActiveNotice);
        Exception thrown = Record.Exception(() =>
        {
            PollSeveralTimes(rig);                               // 还在到期时刻之前
            rig.Ctrl.Poll(dueAt - 1);
            rig.Ctrl.Poll(dueAt);                                // 越过原本的到期时刻
            rig.Ctrl.Poll(dueAt + 60000);
            rig.NowMs = dueAt + 60000;
            PollSeveralTimes(rig);
            rig.Ctrl.Dispose();                                  // 重复释放是空操作
        });
        Assert.Null(thrown);
        Assert.Null(rig.Ctrl.ActiveNotice);
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
    }

    [Fact]
    public void Disposing_the_controller_after_the_transient_notice_has_already_expired_leaves_no_notice()
    {
        // 保护:提示在释放前已经按期撤掉(对照:此刻 ActiveNotice 已是 null),再释放也还是 null,不会凭空冒出一条。
        using var rig = new Rig();
        DungeonSelectNotice notice = ReachTransientTownNotice(rig);
        Assert.NotNull(notice);
        rig.Ctrl.Poll(notice.ExpiresAtMs);
        Assert.Null(rig.Ctrl.ActiveNotice);

        rig.Ctrl.Dispose();

        Assert.Null(rig.Ctrl.ActiveNotice);
        PollSeveralTimes(rig);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Fact]
    public void Preparing_to_quit_is_not_a_disposal_and_leaves_the_transient_town_notice_to_expire_normally()
    {
        // 保护:PrepareForQuit 只退订事件、取消并有限等待在途任务,不是释放——不清瞬时提示,控制器仍然活着,
        // 提示照常在到期时刻被轮询撤掉(对照:Dispose 在同一时刻就把它清掉,见上一个用例)。
        using var rig = new Rig();
        DungeonSelectNotice notice = ReachTransientTownNotice(rig);
        Assert.NotNull(notice);

        rig.Ctrl.PrepareForQuit();

        Assert.Same(notice, rig.Ctrl.ActiveNotice);
        rig.Ctrl.Poll(notice.ExpiresAtMs - 1);
        Assert.Same(notice, rig.Ctrl.ActiveNotice);
        rig.Ctrl.Poll(notice.ExpiresAtMs);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }
}
