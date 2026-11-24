using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA5Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 控制器交给宿主绘制钩子的"视图快照"在各个阶段的取值(通过公开的 <c>BuildHud</c> 读回覆盖层精灵):
/// 倒计时条画不画、画几秒、四行菜单各自是可选(白)/悬停(黄)/禁用(灰)、顶部提示条有没有、控制器公开的剩余毫秒/悬停行/提示种类。
/// 无字体、无图包的版本(占位面板 + 点阵字库):倒计时文字是 "AUTO RETURN TO TOWN: 〈秒〉S"(1 倍点阵,≤10 秒用紧迫色),
/// 四行是 RETRY / PARTY / SELECT / TOWN(2 倍点阵),禁用灰 0x808080、悬停黄、可选白;同文字同倍数同颜色拿到同一个帧对象,所以用"同一对象"判定。
/// 倒计时条只在菜单可用(就绪或降级)且倒计时已开始/待开始时才画——玩家一选择(等回复/等回城)条就必须消失,哪怕倒计时早已开始。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟),菜单倒计时设成 30000 毫秒(整数秒,便于手算);期望值手算,推导写在注释里。
/// </summary>
public sealed class OnlineSettlementGapA5CountdownViewTests
{
    private const int HoverX = SettlementArt.PanelX + 10;

    private static int RowY(int row) => SettlementArt.RowTop + SettlementArt.RowHeight * row + 5;

    [Fact]
    public void The_view_follows_every_phase_from_the_first_advance_to_the_server_return_and_the_countdown_bar_vanishes_once_a_choice_is_pending()
    {
        // 保护:视图快照随阶段取值——推进三步(1/72、1/73、1/74 在途)里倒计时条不画、菜单四行全灰、顶部有"推进中"提示条、剩余毫秒是 0;
        // 菜单就绪(74 发送成功的那次轮询起算)画 30 秒倒计时条、提示条消失、行 0/2/3 白、行 1(快速组队)永远灰;随后每过 1000 毫秒少 1 秒;
        // 悬停行变黄(禁用的快速组队行不变色);玩家做出选择(等 1/75 回复、等服务端回城)倒计时条必须消失、四行全灰、悬停行归 -1,
        // 但控制器公开的剩余毫秒冻结在选择前最后一次轮询的值;收尾后视图为空、剩余毫秒归 0。
        using var rig = NewRig(out OverlayArt art, o => o.MenuCountdownMs = 30_000);

        rig.PollAt(1000);
        Assert.Null(Hud(rig));   // 空闲:没有视图
        rig.StartSettlement();

        // ---- 1/72 在途 ----
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        List<SceneSprite> hud = Hud(rig);
        AssertCountdownHidden(hud, "72");
        AssertRows(art, hud, Gray, Gray, Gray, Gray, "72");
        Assert.True(HasNoticeBar(hud));
        Assert.Equal(OnlineSettlementNoticeKind.Advancing, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));

        // ---- 1/73 在途 ----
        rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
        rig.PollAt(1016);
        Assert.Equal(OnlineSettlementPhase.AdvancingCardRight, rig.Ctrl.Phase);
        hud = Hud(rig);
        AssertCountdownHidden(hud, "73");
        AssertRows(art, hud, Gray, Gray, Gray, Gray, "73");
        Assert.True(HasNoticeBar(hud));
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);

        // ---- 1/74 在途 ----
        rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
        rig.PollAt(1032);
        Assert.Equal(OnlineSettlementPhase.SendingSelectCard, rig.Ctrl.Phase);
        hud = Hud(rig);
        AssertCountdownHidden(hud, "74");
        AssertRows(art, hud, Gray, Gray, Gray, Gray, "74");
        Assert.True(HasNoticeBar(hud));
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);

        // ---- 菜单就绪:1048 读到 74 发送成功,倒计时从这一刻起算(到点 31048),剩 30000 毫秒 = 30 秒 ----
        rig.PollAt(1048);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.CurrentNotice);
        hud = Hud(rig);
        AssertCountdownShown(art, hud, 30, "menu 1048");
        AssertRows(art, hud, OverlayArt.White, Gray, OverlayArt.White, OverlayArt.White, "menu 1048");
        Assert.False(HasNoticeBar(hud));
        Assert.Equal(30_000, rig.Ctrl.RemainingCountdownMs);

        // 再过 1000 毫秒:剩 31048 - 2048 = 29000 毫秒 = 29 秒
        rig.PollAt(2048, 1000);
        Assert.Equal(29_000, rig.Ctrl.RemainingCountdownMs);
        AssertCountdownShown(art, Hud(rig), 29, "menu 2048");

        // 悬停:第 0 行变黄;第 1 行(禁用)有悬停行号但不变色;第 3 行变黄
        rig.Ctrl.SetPointer(HoverX, RowY(0));
        Assert.Equal(0, rig.Ctrl.HoverRow);
        AssertRows(art, Hud(rig), OverlayArt.Yellow, Gray, OverlayArt.White, OverlayArt.White, "hover 0");
        rig.Ctrl.SetPointer(HoverX, RowY(1));
        Assert.Equal(1, rig.Ctrl.HoverRow);
        AssertRows(art, Hud(rig), OverlayArt.White, Gray, OverlayArt.White, OverlayArt.White, "hover 1");
        rig.Ctrl.SetPointer(HoverX, RowY(3));
        Assert.Equal(3, rig.Ctrl.HoverRow);
        AssertRows(art, Hud(rig), OverlayArt.White, Gray, OverlayArt.White, OverlayArt.Yellow, "hover 3");
        rig.Ctrl.ClearPointer();
        AssertRows(art, Hud(rig), OverlayArt.White, Gray, OverlayArt.White, OverlayArt.White, "hover cleared");

        // ---- 玩家选择"再来一次":等 1/75 回复。倒计时早已开始,但条必须消失;四行全灰、悬停行 -1、提示"等待回城" ----
        rig.Ctrl.SetPointer(HoverX, RowY(0));
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        hud = Hud(rig);
        AssertCountdownHidden(hud, "waiting eplp");
        AssertRows(art, hud, Gray, Gray, Gray, Gray, "waiting eplp");
        Assert.True(HasNoticeBar(hud));
        Assert.Equal(OnlineSettlementNoticeKind.WaitingReturn, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.Equal(29_000, rig.Ctrl.RemainingCountdownMs);

        // ---- 1/75 回复"执行"(发出的命令值回显):等服务端回城。条仍不画 ----
        byte sentCommand = rig.Transport.Sent.Last().Payload[1];
        rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(sentCommand));
        rig.PollAt(2064);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        hud = Hud(rig);
        AssertCountdownHidden(hud, "waiting return");
        AssertRows(art, hud, Gray, Gray, Gray, Gray, "waiting return");
        Assert.True(HasNoticeBar(hud));
        Assert.Equal(29_000, rig.Ctrl.RemainingCountdownMs);

        // ---- 服务端回城信号:收尾,视图为空,剩余毫秒归 0 ----
        PushNoti2(rig.Transport);
        rig.PollAt(2080);
        Assert.Equal(new[] { SettlementChoice.Retry }, rig.Completed);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Null(Hud(rig));
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
    }

    [Fact]
    public void A_degraded_menu_after_an_advance_timeout_shows_the_countdown_bar_and_hides_it_again_once_a_choice_is_pending()
    {
        // 保护:推进无回复降级成 MenuDegraded:倒计时条要画(从降级那一刻起算 30 秒)、行 0/2/3 白、提示"推进无回复";
        // 玩家选择"回城"后条消失。降级之前(推进中、差 1 毫秒到超时)不画。
        using var rig = NewRig(out OverlayArt art, o => o.MenuCountdownMs = 30_000);
        rig.PollAt(1000);
        rig.StartSettlement();

        rig.PollAt(1299);   // 299 < 300:仍在等 1/72 回复
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        AssertCountdownHidden(Hud(rig), "before timeout");

        rig.PollAt(1300);   // 1300 - 1000 = 300 ≥ 300:降级,倒计时从 1300 起算(到点 31300)
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.AdvanceNoReply, rig.Ctrl.CurrentNotice?.Kind);
        List<SceneSprite> hud = Hud(rig);
        AssertCountdownShown(art, hud, 30, "degraded 1300");
        AssertRows(art, hud, OverlayArt.White, Gray, OverlayArt.White, OverlayArt.White, "degraded 1300");
        Assert.True(HasNoticeBar(hud));
        Assert.Equal(30_000, rig.Ctrl.RemainingCountdownMs);

        rig.PollAt(2300);   // 31300 - 2300 = 29000
        Assert.Equal(29_000, rig.Ctrl.RemainingCountdownMs);
        AssertCountdownShown(art, Hud(rig), 29, "degraded 2300");

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        hud = Hud(rig);
        AssertCountdownHidden(hud, "degraded then choose");
        AssertRows(art, hud, Gray, Gray, Gray, Gray, "degraded then choose");
        Assert.Equal(OnlineSettlementNoticeKind.WaitingReturn, rig.Ctrl.CurrentNotice?.Kind);
    }

    [Fact]
    public void A_rejected_choice_returns_to_a_degraded_menu_that_shows_the_original_countdown_and_greys_out_only_the_rejected_row()
    {
        // 保护:菜单就绪后选"再来一次"被服务端拒绝(非队长,错误码 8)→ 回到降级菜单:倒计时条重新画出(沿用原来的到点时间,不重新起算:
        // 31048 - 2064 = 28984 毫秒 → 向上取整 29 秒)、被拒的第 0 行变灰(其余行白、快速组队灰)、提示换成"非队长"、悬停行 -1。
        using var rig = NewRig(out OverlayArt art, o => o.MenuCountdownMs = 30_000);
        rig.PollAt(1000);
        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(1032, rig.NowMs);   // 1016 一口气发完 73/74,1032 读到 74 成功,倒计时从 1032 起算(到点 31032)

        rig.PollAt(2032, 1000);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        AssertCountdownHidden(Hud(rig), "waiting");

        byte sentCommand = rig.Transport.Sent.Last().Payload[1];
        rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(OnlineSettlementController.ErrorNotLeader, sentCommand));
        rig.PollAt(2048);   // 31032 - 2048 = 28984
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.EplpRejectedNotLeader, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(SettlementChoice.None, rig.Ctrl.PendingChoice);
        Assert.Equal(28_984, rig.Ctrl.RemainingCountdownMs);
        List<SceneSprite> hud = Hud(rig);
        AssertCountdownShown(art, hud, 29, "rejected");
        AssertRows(art, hud, Gray, Gray, OverlayArt.White, OverlayArt.White, "rejected");
        Assert.True(HasNoticeBar(hud));
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));
    }

    [Fact]
    public void A_timed_out_settlement_shows_the_countdown_bar_even_before_the_first_poll_and_only_the_town_row_is_enabled()
    {
        // 保护:"结算通知超时"的结算在宿主还没轮询过时就开始(倒计时是"待开始"状态):视图已经画倒计时条(30 秒,剩余毫秒就是配置值),
        // 菜单只开放"返回城镇"(行 3 白,其余灰),提示"结算超时";第一次轮询把倒计时正式起算(5000 + 30000 = 35000 到点),条照画;
        // 剩 9000 毫秒时是 9 秒(紧迫色)。
        using var rig = NewRig(out OverlayArt art, o => o.MenuCountdownMs = 30_000);
        rig.StartSettlement(timedOut: true);   // 没有任何轮询在前

        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(OnlineSettlementNoticeKind.SettlementTimeout, rig.Ctrl.CurrentNotice?.Kind);
        List<SceneSprite> hud = Hud(rig);
        AssertCountdownShown(art, hud, 30, "timed out pending");
        AssertRows(art, hud, Gray, Gray, Gray, OverlayArt.White, "timed out pending");
        Assert.True(HasNoticeBar(hud));
        Assert.Equal(30_000, rig.Ctrl.RemainingCountdownMs);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));

        rig.PollAt(5000);   // 倒计时起算:到点 5000 + 30000 = 35000
        hud = Hud(rig);
        AssertCountdownShown(art, hud, 30, "timed out started");
        Assert.Equal(30_000, rig.Ctrl.RemainingCountdownMs);

        rig.PollAt(26_000);   // 35000 - 26000 = 9000 毫秒 → 9 秒(≤10 秒紧迫色)
        Assert.Equal(9_000, rig.Ctrl.RemainingCountdownMs);
        AssertCountdownShown(art, Hud(rig), 9, "timed out urgent");

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        hud = Hud(rig);
        AssertCountdownHidden(hud, "timed out then choose");
        AssertRows(art, hud, Gray, Gray, Gray, Gray, "timed out then choose");
        Assert.Equal(OnlineSettlementNoticeKind.WaitingReturn, rig.Ctrl.CurrentNotice?.Kind);
    }

    [Fact]
    public void A_local_settlement_without_a_transport_shows_the_menu_with_a_countdown_and_no_notice()
    {
        // 保护:没有传输(本地结算)时不走 72/73/74:直接菜单就绪,倒计时在开始那一刻起算(宿主已轮询过 → 立即起算),
        // 视图画 30 秒倒计时条、行 0/2/3 白、没有提示条;选"选择其他副本"立即收尾(没有任何发送)。
        using var rig = NewRig(out OverlayArt art, o =>
        {
            o.MenuCountdownMs = 30_000;
            o.TransportFactory = _ => null;
        });
        rig.PollAt(1000);
        rig.StartSettlement();

        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.CurrentNotice);
        List<SceneSprite> hud = Hud(rig);
        AssertCountdownShown(art, hud, 30, "local");
        AssertRows(art, hud, OverlayArt.White, Gray, OverlayArt.White, OverlayArt.White, "local");
        Assert.False(HasNoticeBar(hud));
        Assert.Equal(30_000, rig.Ctrl.RemainingCountdownMs);

        rig.PollAt(21_000);   // 31000 - 21000 = 10000 毫秒 → 10 秒(含 10 秒:紧迫色)
        AssertCountdownShown(art, Hud(rig), 10, "local urgent");

        Assert.True(rig.Ctrl.Choose(SettlementChoice.SelectDungeon));
        Assert.Equal(new[] { SettlementChoice.SelectDungeon }, rig.Completed);
        Assert.Empty(rig.Transport.Sent);
        Assert.Null(Hud(rig));
    }
}
