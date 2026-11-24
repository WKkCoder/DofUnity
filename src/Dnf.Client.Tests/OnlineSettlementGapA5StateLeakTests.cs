using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA2Kit;
using static Dnf.Client.Tests.OnlineSettlementGapA5Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 同一个结算控制器连续做多次结算时,上一次的状态不许泄漏到下一次("第一轮把字段弄脏、第二轮观察"):
/// 回城信号标志(0/2、0/23、0/24)与回城落位、推进三步的"已发"标志、选牌下标与"跳过选牌"、结算通知超时的"已发放弃"、
/// 1/75 的"已发命令"与"被拒绝"集合、待决选择、悬停行、黑幕淡入计时、倒计时(待开始/剩余毫秒)、在途发送的取消令牌、传输订阅、入站队列。
/// 另有一组"收尾之后公开视图立刻回到默认值"的断言(摘要空、阶段空闲、剩余毫秒 0、悬停行 -1、待决选择无、选牌下标空、没有视图)。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟,无线程);每个用例的第一轮都故意以不同方式收尾,第二/三轮在同一控制器上观察。
/// 夹具缺省:菜单倒计时 5000、各回复/回城等待超时 300。期望值手算(推导写在注释里)。
/// </summary>
public sealed class OnlineSettlementGapA5StateLeakTests
{
    private const int HoverX = SettlementArt.PanelX + 10;
    private static int RowY(int row) => SettlementArt.RowTop + SettlementArt.RowHeight * row + 5;

    private static readonly ushort[] Chain =
    {
        SettlementMenuPayloads.ScoreScrollStatePacketId,
        SettlementMenuPayloads.CardSelectRightStatePacketId,
        SettlementMenuPayloads.SelectCardPacketId,
    };

    /// <summary>开一轮:自动应答推进、轮询到菜单就绪(第 1 轮 1016 排空 72/73 并发 74,1032 读到 74 成功)。</summary>
    private static void StartAndReachMenu(DirectRig rig, byte[] cardRight)
    {
        AutoAdvance(rig.Transport, cardRight);
        rig.Poll();
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
    }

    private static void EndByServerReturn(DirectRig rig)
    {
        PushNoti2(rig.Transport);
        rig.Poll();
    }

    private static byte SentCommandByte(DirectRig rig) => rig.Transport.Sent.Last().Payload[1];

    /// <summary>玩家选择 → 服务端回"执行" → 0/2 回城信号 → 按"被服务端确认"收尾。</summary>
    private static void ChooseAndConfirm(DirectRig rig, SettlementChoice choice)
    {
        Assert.True(rig.Ctrl.Choose(choice));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(SentCommandByte(rig)));
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        EndByServerReturn(rig);
    }

    /// <summary>收尾之后控制器公开的一切都回到默认:没有摘要、空闲、剩余毫秒 0、悬停行 -1、无待决选择、选牌下标空、没跳过选牌、没有视图。</summary>
    private static void AssertIdleDefaults(DirectRig rig)
    {
        OnlineSettlementController c = rig.Ctrl;
        Assert.False(c.IsActive);
        Assert.Equal(OnlineSettlementPhase.Idle, c.Phase);
        Assert.Null(c.Summary);
        Assert.Equal(0, c.RemainingCountdownMs);
        Assert.Equal(-1, c.HoverRow);
        Assert.Equal(SettlementChoice.None, c.PendingChoice);
        Assert.Null(c.SelectedCardIndex);
        Assert.False(c.CardSelectSkipped);
        Assert.Null(Hud(rig));
    }

    [Theory]
    [InlineData("noti2", "nothing")]
    [InlineData("pair", "nothing")]
    [InlineData("pair", "area_only")]
    [InlineData("pair", "users_only")]
    public void Return_signals_seen_in_an_earlier_settlement_do_not_count_toward_the_next_one(string first, string second)
    {
        // 保护:第一次结算以"0/2"或"0/23 + 0/24"收尾之后,第二次结算不会因为残留的回城标志(0/2、0/23、0/24 各一个)被当成"服务端已回城":
        // 第二次只来 0/23(或只来 0/24,或什么都不来)时,菜单照常就绪、不收尾;补齐另一半(或来 0/2)之后才按第二次自己的信号收尾,
        // 且落位用第二次自己的 0/23 坐标(村 3 区 6),而不是第一次的(村 2 区 5);第二次没有 0/23 时落位回到进副本前城镇区,而不是沿用第一次的服务端落位。
        using var rig = new DirectRig();
        rig.PollAt(1000);
        StartAndReachMenu(rig, CardRightBytes(1));
        if (first == "noti2")
        {
            PushNoti2(rig.Transport);
        }
        else
        {
            PushUserArea(rig.Transport, village: 2, area: 5);
            PushAreaUsers(rig.Transport, village: 2, area: 5);
        }
        rig.Poll();
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        AssertIdleDefaults(rig);
        OnlineSettlementReturnTarget firstTarget = Assert.Single(rig.Targets);
        Assert.Equal(first == "noti2" ? OnlineSettlementReturnSource.PreDungeonArea : OnlineSettlementReturnSource.ServerUserArea, firstTarget.Source);

        // ---- 第二次结算:先把半份(或没有)信号入队,再推进到菜单 ----
        rig.Transport.ClearSent();
        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.Poll();
        rig.StartSettlement();
        if (second == "area_only")
        {
            PushUserArea(rig.Transport, village: 3, area: 6);
        }
        else if (second == "users_only")
        {
            PushAreaUsers(rig.Transport, village: 3, area: 6);
        }
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        rig.Poll();
        rig.Poll();
        Assert.True(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Single(rig.Completed);
        Assert.Single(rig.Logs, l => l.Contains("reason=server_return"));
        Assert.False(rig.Ctrl.ServerInitiatedReturn);

        // ---- 补齐/到来:按第二次自己的信号收尾 ----
        if (second == "area_only")
        {
            PushAreaUsers(rig.Transport, village: 3, area: 6);
        }
        else if (second == "users_only")
        {
            PushUserArea(rig.Transport, village: 3, area: 6);
        }
        else
        {
            PushNoti2(rig.Transport);
        }
        rig.Poll();
        Assert.Equal(new[] { SettlementChoice.ReturnToTown, SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.True(rig.Ctrl.ServerInitiatedReturn);
        Assert.Equal(2, rig.Targets.Count);
        OnlineSettlementReturnTarget secondTarget = rig.Targets[1];
        if (second == "nothing")
        {
            Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, secondTarget.Source);
            Assert.Equal(1, secondTarget.Place!.Value.Town);
            Assert.Equal(3, secondTarget.Place!.Value.Area);
        }
        else
        {
            Assert.Equal(OnlineSettlementReturnSource.ServerUserArea, secondTarget.Source);
            Assert.Equal(3, secondTarget.Place!.Value.Town);
            Assert.Equal(6, secondTarget.Place!.Value.Area);
        }
        AssertIdleDefaults(rig);
    }

    [Fact]
    public void Three_settlements_in_a_row_each_send_the_whole_advance_chain_and_keep_their_own_card_choice()
    {
        // 保护:同一控制器连做三次结算——每次都从头发 1/72 → 1/73 → 1/74("已发"标志不残留,否则后面的次序会卡在半路);
        // 选牌下标只属于当次:第 1 次选第 2 张;第 2 次服务端无任何有权槽位 → 跳过选牌(下标为空、"已跳过"为真、不发 1/74、提示"跳过选牌"),
        // 不沿用第 1 次的下标;第 3 次又正常选第 1 张,"已跳过"标志不残留(为假)、菜单就绪后无提示;每次收尾后公开视图立刻回到默认。
        using var rig = new DirectRig();
        rig.PollAt(1000);

        // ---- 第 1 次:槽位 [-1, -1, 1, -1] → 第一个有权的是第 2 槽 ----
        StartAndReachMenu(rig, CardRightBytes(-1, -1, 1, -1));
        Assert.Equal(Chain, SentIds(rig));
        Assert.Equal((byte?)2, rig.Ctrl.SelectedCardIndex);
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Equal(StandardPreamble, AllLogs(rig));
        EndByServerReturn(rig);
        AssertIdleDefaults(rig);

        // ---- 第 2 次:全部无权 → 跳过选牌 ----
        rig.Transport.ClearSent();
        int logsBefore = rig.Logs.Count;
        StartAndReachMenu(rig, CardRightBytes(-1));
        Assert.Equal(new[] { Chain[0], Chain[1] }, SentIds(rig));
        Assert.True(rig.Ctrl.CardSelectSkipped);
        Assert.Null(rig.Ctrl.SelectedCardIndex);
        Assert.Equal(OnlineSettlementNoticeKind.CardSelectSkipped, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(new[] { Show, Sent72, Ok72, Sent73, SkipNoRight }, rig.Logs.Skip(logsBefore).Take(5).ToArray());
        EndByServerReturn(rig);
        AssertIdleDefaults(rig);

        // ---- 第 3 次:槽位 [-1, 1, -1, -1] → 第 1 槽 ----
        rig.Transport.ClearSent();
        logsBefore = rig.Logs.Count;
        StartAndReachMenu(rig, CardRightBytes(-1, 1));
        Assert.Equal(Chain, SentIds(rig));
        Assert.Equal((byte?)1, rig.Ctrl.SelectedCardIndex);
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Null(rig.Ctrl.CurrentNotice);
        Assert.Equal(StandardPreamble, rig.Logs.Skip(logsBefore).Take(7).ToArray());
        EndByServerReturn(rig);
        AssertIdleDefaults(rig);
        Assert.Equal(3, rig.Completed.Count);
    }

    [Fact]
    public void A_second_timed_out_settlement_sends_its_own_give_up_instead_of_falling_back_at_once()
    {
        // 保护:"结算通知超时"路径上第一次回城已发过 1/45 放弃游戏(每次结算至多发一次)——这个"已发"标志只属于第一次结算:
        // 第二次同样超时的结算选"返回城镇"时要重新发一次 1/45、进入"等回城"阶段(而不是以为已经发过、立刻按"兜底"收尾);
        // 第二次的回城信号到来后才收尾。
        long giveUp = SessionLifecyclePayloads.GiveUpGamePacketId;
        using var rig = new DirectRig();
        rig.PollAt(1000);
        rig.StartSettlement(timedOut: true);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(new[] { giveUp }, rig.Transport.Sent.Select(s => (long)s.PacketId).ToArray());
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        EndByServerReturn(rig);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        AssertIdleDefaults(rig);

        rig.Transport.ClearSent();
        rig.Poll();
        rig.StartSettlement(timedOut: true);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(new[] { giveUp }, rig.Transport.Sent.Select(s => (long)s.PacketId).ToArray());
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Single(rig.Completed);
        EndByServerReturn(rig);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown, SettlementChoice.ReturnToTown }, rig.Completed);
        AssertIdleDefaults(rig);
    }

    [Fact]
    public void Sent_and_rejected_menu_commands_of_an_earlier_settlement_do_not_block_the_next_one()
    {
        // 保护:第一次结算里"再来一次"被拒绝(被拒集合含它)、"选择其他副本"发出并被确认(已发集合含它),随后以服务端回城收尾;
        // 第二次结算的菜单里:没有待决选择、三个可选项都重新可选(被拒集合已清);"再来一次"能再次发出(1/75 命令值 = 重试),
        // 被拒绝后"选择其他副本"也能再次发出(已发集合已清)——否则这两次选择会被当成"同一命令不重发"而直接丢弃。
        OnlineSettlementController.TryMapChoiceToEplp(SettlementChoice.Retry, out EplpCommand retry);
        OnlineSettlementController.TryMapChoiceToEplp(SettlementChoice.SelectDungeon, out EplpCommand select);
        using var rig = new DirectRig();
        rig.PollAt(1000);
        StartAndReachMenu(rig, CardRightBytes(1));

        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        Assert.Equal((byte)retry, SentCommandByte(rig));
        rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(OnlineSettlementController.ErrorNotLeader, SentCommandByte(rig)));
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        ChooseAndConfirm(rig, SettlementChoice.SelectDungeon);
        Assert.Equal(new[] { SettlementChoice.SelectDungeon }, rig.Completed);
        AssertIdleDefaults(rig);

        // ---- 第二次 ----
        rig.Transport.ClearSent();
        StartAndReachMenu(rig, CardRightBytes(1));
        Assert.Equal(SettlementChoice.None, rig.Ctrl.PendingChoice);
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));
        rig.Transport.ClearSent();
        Assert.True(rig.Ctrl.Choose(SettlementChoice.Retry));
        Assert.Equal(new[] { SettlementMenuPayloads.EplpCommandPacketId }, SentIds(rig));
        Assert.Equal((byte)retry, SentCommandByte(rig));
        rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(OnlineSettlementController.ErrorNotLeader, (byte)retry));
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.True(rig.Ctrl.IsChoiceEnabled(SettlementChoice.SelectDungeon));
        rig.Transport.ClearSent();
        Assert.True(rig.Ctrl.Choose(SettlementChoice.SelectDungeon));
        Assert.Equal(new[] { SettlementMenuPayloads.EplpCommandPacketId }, SentIds(rig));
        Assert.Equal((byte)select, SentCommandByte(rig));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
    }

    [Fact]
    public void The_pointer_row_and_the_fade_in_age_start_from_zero_in_every_settlement()
    {
        // 保护:第一次结算收尾时鼠标仍悬停在第 2 行、黑幕淡入早已走满(56):收尾后悬停行立刻是 -1;
        // 第二次结算刚开始悬停行 -1、黑幕不画(淡入计时从 0 起);第一次轮询步长 4 毫秒 → 56 × 4 / 500 = 0.448 四舍五入为 0,仍不画;
        // 再 1 毫秒共 5 毫秒 → 56 × 5 / 500 = 0.56 → 1:两层黑幕都画、不透明度恰为 1(残留或多/少 1 毫秒的计时都会让这两个读数变化)。
        using var rig = NewRig(out OverlayArt art);
        rig.PollAt(1000);
        StartAndReachMenu(rig, CardRightBytes(1));
        rig.PollAt(rig.NowMs + 500, 500);   // 再走 500 毫秒:淡入走满
        List<SceneSprite> hud = Hud(rig);
        Assert.Equal(56, Assert.Single(hud, s => s.AnimationPath == "settle-dim").TintAlpha);
        Assert.Equal(56, Assert.Single(hud, s => s.AnimationPath == "settle-dim-back").TintAlpha);
        rig.Ctrl.SetPointer(HoverX, RowY(2));
        Assert.Equal(2, rig.Ctrl.HoverRow);
        AssertRows(art, Hud(rig), OverlayArt.White, Gray, OverlayArt.Yellow, OverlayArt.White, "hover before end");
        EndByServerReturn(rig);
        AssertIdleDefaults(rig);

        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.Poll();
        rig.StartSettlement();
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        hud = Hud(rig);
        Assert.DoesNotContain(hud, s => s.AnimationPath == "settle-dim" || s.AnimationPath == "settle-dim-back");

        long t = rig.NowMs;
        rig.PollAt(t + 4, 4);
        hud = Hud(rig);
        Assert.DoesNotContain(hud, s => s.AnimationPath == "settle-dim" || s.AnimationPath == "settle-dim-back");
        rig.PollAt(t + 5, 1);
        hud = Hud(rig);
        Assert.Equal(1, Assert.Single(hud, s => s.AnimationPath == "settle-dim").TintAlpha);
        Assert.Equal(1, Assert.Single(hud, s => s.AnimationPath == "settle-dim-back").TintAlpha);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);   // 第二个轮询读到 74 发送成功:菜单就绪
        AssertRows(art, hud, OverlayArt.White, Gray, OverlayArt.White, OverlayArt.White, "second menu");   // 第 2 行不再是上一次的悬停黄
    }

    [Fact]
    public void The_menu_countdown_of_a_later_settlement_starts_when_the_menu_becomes_available_even_if_the_replies_come_late()
    {
        // 保护:第一次结算收尾时倒计时还剩 4000 毫秒、倒计时"已开始":第二次结算的 72/73 回复晚了 3 秒到(虚拟时间;推进回复超时放宽到 10 秒)——
        // 在菜单可用之前(推进三步里,含"第二次的第一次轮询"之后)剩余毫秒必须是 0、倒计时条不画;
        // 菜单可用那一次轮询才起算(剩 5000 毫秒 = 5 秒,紧迫色),此后再过 4999 毫秒剩 1、再过 1 毫秒到点按"超时"选回城(日志 source=timeout);
        // 起算点若被前移到第二次的第一次轮询,到点会早 3 秒,下面的断言就不成立。
        using var rig = NewRig(out OverlayArt art, o => o.AdvanceReplyTimeoutMs = 10_000);
        rig.PollAt(1000);
        StartAndReachMenu(rig, CardRightBytes(1));
        rig.PollAt(rig.NowMs + 1000, 1000);   // 1032 起算,此刻 2032:5000 - 1000 = 4000
        Assert.Equal(4000, rig.Ctrl.RemainingCountdownMs);
        EndByServerReturn(rig);
        AssertIdleDefaults(rig);

        // ---- 第二次:自己手动回包,不自动应答 ----
        rig.Transport.SetSendHandler(null);
        rig.Transport.ClearSent();
        long t0 = rig.NowMs + 16;
        rig.PollAt(t0);   // 空闲轮询
        rig.StartSettlement();
        rig.PollAt(t0 + 16);   // 第二次的第一次轮询
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);
        AssertCountdownHidden(Hud(rig), "second first poll");

        rig.PollAt(t0 + 3000);   // 晚了 3 秒,回复仍没到
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);
        AssertCountdownHidden(Hud(rig), "second late");

        rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
        rig.PollAt(t0 + 3016);
        Assert.Equal(OnlineSettlementPhase.AdvancingCardRight, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);
        rig.Transport.Push(PacketClass.Cmd, 73, CardRightBytes(1));
        rig.PollAt(t0 + 3032);
        Assert.Equal(OnlineSettlementPhase.SendingSelectCard, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Ctrl.RemainingCountdownMs);
        AssertCountdownHidden(Hud(rig), "second 74");

        long menuAt = t0 + 3048;
        rig.PollAt(menuAt);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        AssertCountdownShown(art, Hud(rig), 5, "second menu");

        rig.PollAt(menuAt + 4999);
        Assert.Equal(1, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        rig.PollAt(menuAt + 5000);
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        Assert.Equal(SettlementChoice.ReturnToTown, rig.Ctrl.PendingChoice);
        Assert.Contains(ChooseReturnByCountdown, rig.Logs);
        Assert.Single(rig.Completed);
    }

    [Fact]
    public void A_finished_settlement_cancels_its_in_flight_sends_and_releases_the_transport_and_the_next_one_starts_with_fresh_ones()
    {
        // 保护:收尾(复位)时:发送用的取消令牌被取消(在途发送被通知取消);传输的帧/断线订阅都摘掉(订阅数回到 0);
        // 之后的新结算用新令牌(未被取消)、重新订阅(各 1 个)。
        var spy = new TokenSpyTransport();
        OnlineSettlementOptions captured = null;
        using var rig = new DirectRig(o => captured = o);
        captured!.TransportFactory = _ => spy.Attach(rig.Transport);
        rig.PollAt(1000);
        StartAndReachMenu(rig, CardRightBytes(1));
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);
        Assert.Equal(3, spy.Tokens.Count);
        Assert.All(spy.Tokens, t => Assert.False(t.IsCancellationRequested));

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(4, spy.Tokens.Count);
        Assert.False(spy.Tokens[3].IsCancellationRequested);
        EndByServerReturn(rig);   // 回复没到、直接收到 0/2:按"被服务端确认"收尾
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        AssertIdleDefaults(rig);
        Assert.All(spy.Tokens, t => Assert.True(t.IsCancellationRequested));
        Assert.Equal(0, rig.Transport.FrameSubscribers);
        Assert.Equal(0, rig.Transport.DisconnectSubscribers);

        // 第二次:新令牌没被取消、重新订阅
        StartAndReachMenu(rig, CardRightBytes(1));
        Assert.Equal(7, spy.Tokens.Count);
        Assert.All(spy.Tokens.Skip(4), t => Assert.False(t.IsCancellationRequested));
        Assert.Equal(1, rig.Transport.FrameSubscribers);
        Assert.Equal(1, rig.Transport.DisconnectSubscribers);
    }

    [Fact]
    public void Frames_queued_for_a_replaced_settlement_are_dropped_instead_of_leaking_into_the_replacement()
    {
        // 保护:第一次结算还没收尾时,一条 0/2 已入队(尚未被轮询处理),这时第二次结算开始、把第一次整体复位替换掉:
        // 入站队列在复位里清空——第二次轮询到菜单就绪也不会被那条旧的 0/2 误判成"服务端已回城";被替换的那次没有交还选择。
        using var rig = new DirectRig();
        rig.PollAt(1000);
        StartAndReachMenu(rig, CardRightBytes(1));
        PushNoti2(rig.Transport);   // 入队,不轮询

        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.StartSettlement();
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        rig.Poll();
        rig.Poll();
        Assert.True(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        Assert.DoesNotContain(rig.Logs, l => l.Contains("reason=server_return"));

        // 之后新到的 0/2 仍然有效
        EndByServerReturn(rig);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        AssertIdleDefaults(rig);
    }

    [Fact]
    public void The_retry_count_accumulates_across_retries_and_is_cleared_by_returning_to_town()
    {
        // 保护:连续两次"再来一次"累计 1、2(日志里 cycle=1、cycle=2),第三次"返回城镇"清零;
        // 每次收尾后的公开视图都回到默认(待决选择、悬停行等不残留)。
        using var rig = new DirectRig();
        rig.PollAt(1000);

        StartAndReachMenu(rig, CardRightBytes(1));
        ChooseAndConfirm(rig, SettlementChoice.Retry);
        Assert.Equal(1, rig.Ctrl.RetryCycleCount);
        AssertIdleDefaults(rig);

        StartAndReachMenu(rig, CardRightBytes(1));
        ChooseAndConfirm(rig, SettlementChoice.Retry);
        Assert.Equal(2, rig.Ctrl.RetryCycleCount);
        AssertIdleDefaults(rig);

        StartAndReachMenu(rig, CardRightBytes(1));
        ChooseAndConfirm(rig, SettlementChoice.ReturnToTown);
        Assert.Equal(0, rig.Ctrl.RetryCycleCount);
        AssertIdleDefaults(rig);

        Assert.Equal(
            new[] { SettlementChoice.Retry, SettlementChoice.Retry, SettlementChoice.ReturnToTown },
            rig.Completed);
        string[] retryLines = rig.Logs.Where(l => l.Contains("event=retry")).ToArray();
        Assert.Equal(
            new[]
            {
                "DNF_ONLINE stage=settle event=retry status=reenter cycle=1",
                "DNF_ONLINE stage=settle event=retry status=reenter cycle=2",
            },
            retryLines);
    }

    /// <summary>把另一个假传输包一层:记下每次发送用的取消令牌,事件订阅原样转发给内层。</summary>
    private sealed class TokenSpyTransport : IOnlineSettlementTransport
    {
        private FakeSettlementTransport _inner;

        public List<CancellationToken> Tokens { get; } = new List<CancellationToken>();

        public TokenSpyTransport Attach(FakeSettlementTransport inner)
        {
            _inner = inner;
            return this;
        }

        public bool IsDisconnected => _inner.IsDisconnected;

        public event Action<PacketClass, int, byte[]> FrameReceived
        {
            add { _inner.FrameReceived += value; }
            remove { _inner.FrameReceived -= value; }
        }

        public event Action<OnlineDisconnectReason> Disconnected
        {
            add { _inner.Disconnected += value; }
            remove { _inner.Disconnected -= value; }
        }

        public Task<FrameStatus> SendCommandAsync(ushort packetId, byte[] payload, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return _inner.SendCommandAsync(packetId, payload, cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
