using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapA2Kit;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算控制器"1/75 被拒、服务端回城信号、再来一次计数、1/75 与等回城超时"的<b>整份日志序列</b>测试(逐行相等的手算字面量):
/// 1/75 被拒(四种错误码 × 三个选择,表驱动,逐项断言提示种类与错误码)、服务端回城的各种信号形态(选择之后确认 / 选择之前服务端主动回城,各五种帧组合)、
/// 连续多次"再来一次"的计数与重置、1/75 回包超时与等回城超时(三个选择各一行)、1/75 里被忽略的回复不写日志。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟)。时间:空闲轮询 1000、开始结算 1000;1016 排空 72/73 回复并发 74;1032 读到 74 发送成功进入菜单(倒计时到点 6032)。
/// </summary>
public sealed class OnlineSettlementGapA2LogRejectAndReturnTests
{
    /// <summary>走到菜单就绪(逻辑时钟 1032);<paramref name="onEplp"/> 是服务端对 1/75 的应答。</summary>
    private static void ReachMenu(DirectRig rig, Action<byte[]> onEplp)
    {
        AutoAdvance(rig.Transport, CardRightBytes(1), onEplp);
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(1032, rig.NowMs);
        Assert.Equal(StandardPreamble, AllLogs(rig));
    }

    // ----------------------------------------------------------------------------------------------
    // 1/75 被拒:四种错误码 × 三个选择
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=Retry code=8", (byte)8, OnlineSettlementNoticeKind.EplpRejectedNotLeader)]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=Retry code=133", (byte)133, OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen)]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=Retry code=1", (byte)1, OnlineSettlementNoticeKind.EplpRejectedInvalidCommand)]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=Retry code=7", (byte)7, OnlineSettlementNoticeKind.EplpRejectedOther)]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=SelectOtherDungeon code=8", (byte)8, OnlineSettlementNoticeKind.EplpRejectedNotLeader)]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=SelectOtherDungeon code=133", (byte)133, OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen)]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=SelectOtherDungeon code=1", (byte)1, OnlineSettlementNoticeKind.EplpRejectedInvalidCommand)]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=SelectOtherDungeon code=7", (byte)7, OnlineSettlementNoticeKind.EplpRejectedOther)]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=ReturnToTown code=8", (byte)8, OnlineSettlementNoticeKind.EplpRejectedNotLeader)]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=ReturnToTown code=133", (byte)133, OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen)]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=ReturnToTown code=1", (byte)1, OnlineSettlementNoticeKind.EplpRejectedInvalidCommand)]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=ReturnToTown code=7", (byte)7, OnlineSettlementNoticeKind.EplpRejectedOther)]
    public void A_rejected_1_75_logs_the_command_and_error_code_and_either_falls_back_to_town_or_degrades_the_menu(
        SettlementChoice choice, string chooseLine, string failLine, byte code, OnlineSettlementNoticeKind kind)
    {
        // 保护:1/75 被服务端拒绝(回复 [0, 错误码, 命令]):写"被拒"行(命令名、错误码),提示种类按错误码区分
        // (8 非队长 / 133=0x85 神秘商店未购 / 1 非法命令 / 其他),提示里带错误码;
        // 被拒的是回城 → 写回退行(来源 eplp_rejected_fallback、信号 fallback)并直接回城;
        // 被拒的是再次挑战或选其他副本 → 退回降级菜单(待决选择清空、被拒的那一项禁用、另两项仍可选、倒计时不重置),
        // 随后玩家改选回城,1/75 执行确认 + 0/2,序列收尾。
        using var rig = new DirectRig();
        int eplpCalls = 0;
        ReachMenu(rig, payload =>
        {
            eplpCalls++;
            if (eplpCalls == 1)
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(code, payload[1]));
            }
            else
            {
                ConfirmWithNoti2(rig.Transport, payload);
            }
        });

        Assert.True(rig.Ctrl.Choose(choice));
        Assert.Equal(Seq(StandardPreamble, chooseLine), AllLogs(rig));
        rig.Poll();   // 1048:读到被拒的回复

        Assert.Equal(kind, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(code, rig.Ctrl.CurrentNotice?.Code);
        if (choice == SettlementChoice.ReturnToTown)
        {
            Assert.Equal(
                Seq(StandardPreamble, chooseLine, failLine, "DNF_ONLINE stage=settle event=town reason=eplp_rejected_fallback area=pre_dungeon signal=fallback"),
                AllLogs(rig));
            Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
            Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
            Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));
            return;
        }

        Assert.Equal(Seq(StandardPreamble, chooseLine, failLine), AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, rig.Ctrl.Phase);
        Assert.Equal(SettlementChoice.None, rig.Ctrl.PendingChoice);
        Assert.Empty(rig.Completed);
        Assert.Equal(4984, rig.Ctrl.RemainingCountdownMs);   // 倒计时 1032 起算,1048 时剩 5000-16;被拒不重置
        foreach (SettlementChoice c in new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown })
        {
            Assert.Equal(c != choice, rig.Ctrl.IsChoiceEnabled(c));
        }
        Assert.False(rig.Ctrl.Choose(choice));   // 被拒的那一项不能再选,也不会多发
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll();
        Assert.Equal(Seq(StandardPreamble, chooseLine, failLine, ChooseReturnByPlayer, TownConfirmedByNoti2), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(new ushort[] { 72, 73, 74, 75, 75 }, SentIds(rig));
        Assert.Equal(new byte[] { 1, 2 }, rig.Transport.Sent[^1].Payload);
    }

    // ----------------------------------------------------------------------------------------------
    // 服务端回城信号的各种帧组合
    // ----------------------------------------------------------------------------------------------

    /// <summary>推一组回城信号帧(用户区域通知的唯一号、村、区、坐标与朝向都是合成值)。</summary>
    private static void PushSignal(DirectRig rig, string signal)
    {
        FakeSettlementTransport t = rig.Transport;
        switch (signal)
        {
            case "noti2":
                PushNoti2(t);
                break;
            case "pair":
                t.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 1, 2, 150, 200, 1));
                t.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 2));
                break;
            case "both":
                PushNoti2(t);
                t.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 1, 2, 150, 200, 1));
                t.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 2));
                break;
            case "noti2_with_user_area_only":
                PushNoti2(t);
                t.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 1, 2, 150, 200, 1));
                break;
            default:   // "noti2_with_other_users_pair":别的角色的用户区域 + 区域用户列表不算自己的回城确认,只有 0/2 算
                t.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(OtherUid, 4, 4, 10, 20, 0));
                t.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(4, 4));
                PushNoti2(t);
                break;
        }
    }

    private static void AssertReturnTarget(OnlineSettlementReturnTarget target, bool serverArea)
    {
        if (serverArea)
        {
            Assert.Equal(OnlineSettlementReturnSource.ServerUserArea, target.Source);
            Assert.Equal(new TownPlace(1, 2, TownEntry.Return), target.Place);
            Assert.Equal(150.0, target.X);
            Assert.Equal(200.0, target.Y);
            Assert.True(target.FacingLeft.HasValue && target.FacingLeft.Value);   // 朝向字节 1 → 朝左
        }
        else
        {
            Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, target.Source);
            Assert.Equal(new TownPlace(1, 3, TownEntry.Return), target.Place);
            Assert.Null(target.X);
            Assert.Null(target.Y);
            Assert.Null(target.FacingLeft);
        }
    }

    [Theory]
    [InlineData("noti2", false, "DNF_ONLINE stage=settle event=town reason=confirmed area=pre_dungeon signal=noti2", false)]
    [InlineData("pair", false, "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=area_pair", true)]
    [InlineData("both", false, "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=noti2_and_area", true)]
    [InlineData("noti2_with_user_area_only", false, "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=noti2", true)]
    [InlineData("noti2_with_other_users_pair", false, "DNF_ONLINE stage=settle event=town reason=confirmed area=pre_dungeon signal=noti2", false)]
    [InlineData("noti2", true, "DNF_ONLINE stage=settle event=town reason=confirmed area=pre_dungeon signal=noti2", false)]
    [InlineData("pair", true, "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=area_pair", true)]
    [InlineData("both", true, "DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=noti2_and_area", true)]
    public void A_return_signal_after_the_choice_is_logged_with_its_own_signal_and_area_source_whatever_the_frame_order(
        string signal, bool signalBeforeReply, string townLine, bool serverArea)
    {
        // 保护:玩家选回城之后,服务端回城信号(0/2 / 0/23+0/24 成对 / 三帧齐 / 只有 0/2 加自己的 0/23 / 别人的 0/23+0/24 加 0/2)
        // 与 1/75 执行确认在同一次轮询里到达(信号在回复之前或之后),收尾行写"确认"、信号名按组合区分(noti2 / area_pair / noti2_and_area)、
        // 落点来源(服务端给的区域 / 进副本前城镇区);落点对象的坐标与朝向对应;选定回调只收到一次回城。
        using var rig = new DirectRig();
        ReachMenu(rig, payload =>
        {
            if (signalBeforeReply)
            {
                PushSignal(rig, signal);
                rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
            }
            else
            {
                rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
                PushSignal(rig, signal);
            }
        });

        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        rig.Poll();

        Assert.Equal(Seq(StandardPreamble, ChooseReturnByPlayer, townLine), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.False(rig.Ctrl.ServerInitiatedReturn);
        AssertReturnTarget(Assert.Single(rig.Targets), serverArea);
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));
    }

    [Theory]
    [InlineData("noti2", "DNF_ONLINE stage=settle event=town reason=server_return area=pre_dungeon signal=noti2", false)]
    [InlineData("pair", "DNF_ONLINE stage=settle event=town reason=server_return area=server_area signal=area_pair", true)]
    [InlineData("both", "DNF_ONLINE stage=settle event=town reason=server_return area=server_area signal=noti2_and_area", true)]
    [InlineData("noti2_with_user_area_only", "DNF_ONLINE stage=settle event=town reason=server_return area=server_area signal=noti2", true)]
    [InlineData("noti2_with_other_users_pair", "DNF_ONLINE stage=settle event=town reason=server_return area=pre_dungeon signal=noti2", false)]
    public void A_return_signal_before_any_choice_is_logged_as_a_server_initiated_return_without_a_choose_line(
        string signal, string townLine, bool serverArea)
    {
        // 保护:玩家还没选择,服务端先把人带回城(例如 60 秒回城定时器到点):没有选择行,收尾行的原因是 server_return,
        // 信号名与落点来源同上;控制器记下"服务端主动回城"、提示"已被服务端带回城镇"、选定回调收到回城,一条 1/75 都没发。
        using var rig = new DirectRig();
        ReachMenu(rig, _ => { });

        PushSignal(rig, signal);
        rig.Poll();

        Assert.Equal(Seq(StandardPreamble, townLine), AllLogs(rig));
        Assert.True(rig.Ctrl.ServerInitiatedReturn);
        Assert.Equal(OnlineSettlementNoticeKind.ServerReturnedToTown, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        AssertReturnTarget(Assert.Single(rig.Targets), serverArea);
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
    }

    [Fact]
    public void Only_half_of_the_area_pair_is_not_a_return_signal_until_the_other_half_arrives()
    {
        // 保护:只来 0/23(或只来 0/24)不算回城完成,没有任何新日志、菜单不动;另一半到了才成对,按"服务端主动回城 + 成对信号 + 服务端落点"收尾。
        using var rig = new DirectRig();
        ReachMenu(rig, _ => { });

        rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(1, 2));
        rig.Poll();
        Assert.Equal(StandardPreamble, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);

        rig.Transport.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, 1, 2, 150, 200, 1));
        rig.Poll();
        Assert.Equal(Seq(StandardPreamble, "DNF_ONLINE stage=settle event=town reason=server_return area=server_area signal=area_pair"), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        AssertReturnTarget(Assert.Single(rig.Targets), true);
    }

    // ----------------------------------------------------------------------------------------------
    // 再来一次:连续多次的计数与重置
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Consecutive_retries_count_up_in_the_log_and_a_town_or_dungeon_choice_resets_the_count()
    {
        // 保护:同一控制器连续做多次结算——再来一次(第 1 次)→ 再来一次(第 2 次)→ 选其他副本(计数清零)→ 再来一次(又是第 1 次)→
        // 返回城镇(计数清零)→ 再来一次(第 1 次);每次的整份日志逐行相等(重进一轮的计数写在 retry 行里),RetryCycleCount 属性同步。
        using var rig = new DirectRig();
        AutoAdvance(rig.Transport, CardRightBytes(1), payload => ConfirmWithNoti2(rig.Transport, payload));
        var expected = new List<string>();
        long clock = 1000;

        void Settle(SettlementChoice choice, string chooseLine, string endLine, int cycleAfter)
        {
            rig.Transport.ClearSent();
            rig.PollAt(clock);
            rig.StartSettlement();
            rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
            Assert.True(rig.Ctrl.Choose(choice));
            rig.Poll();
            expected.AddRange(StandardPreamble);
            expected.Add(chooseLine);
            expected.Add(endLine);
            Assert.Equal(expected.ToArray(), AllLogs(rig));
            Assert.Equal(cycleAfter, rig.Ctrl.RetryCycleCount);
            Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
            clock += 10000;
        }

        const string retry1 = "DNF_ONLINE stage=settle event=retry status=reenter cycle=1";
        const string retry2 = "DNF_ONLINE stage=settle event=retry status=reenter cycle=2";
        const string dungeonConfirmed = "DNF_ONLINE stage=settle event=choose choice=SelectDungeon source=confirmed";

        Settle(SettlementChoice.Retry, ChooseRetryByPlayer, retry1, 1);
        Settle(SettlementChoice.Retry, ChooseRetryByPlayer, retry2, 2);
        Settle(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, dungeonConfirmed, 0);
        Settle(SettlementChoice.Retry, ChooseRetryByPlayer, retry1, 1);
        Settle(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, TownConfirmedByNoti2, 0);
        Settle(SettlementChoice.Retry, ChooseRetryByPlayer, retry1, 1);

        Assert.Equal(
            new[]
            {
                SettlementChoice.Retry, SettlementChoice.Retry, SettlementChoice.SelectDungeon,
                SettlementChoice.Retry, SettlementChoice.ReturnToTown, SettlementChoice.Retry,
            },
            rig.Completed);
        Assert.Single(rig.Targets);   // 只有那一次回城落了目标
        Assert.Equal(6 * 9, rig.Logs.Count);
    }

    // ----------------------------------------------------------------------------------------------
    // 1/75 回包超时、等回城超时
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_timeout step=Retry")]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_timeout step=SelectOtherDungeon")]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, "DNF_ONLINE stage=settle event=fail reason=eplp_timeout step=ReturnToTown")]
    public void A_1_75_without_reply_logs_its_command_timeout_and_falls_back_to_town_without_resending(
        SettlementChoice choice, string chooseLine, string timeoutLine)
    {
        // 保护:1/75 回复等不到(超时 300 毫秒,到点即超时):写"1/75 超时"行并带命令名,然后无条件安全回城
        // (收尾行来源 eplp_timeout、信号 timeout_fallback、落点来源进副本前城镇区),提示是"1/75 超时",1/75 只发过一次。
        // 时间:选择发生在逻辑时钟 1032,超时在 1332。
        using var rig = new DirectRig();
        ReachMenu(rig, _ => { });

        Assert.True(rig.Ctrl.Choose(choice));
        rig.PollAt(1331);
        Assert.Equal(Seq(StandardPreamble, chooseLine), AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);

        rig.PollAt(1332);
        Assert.Equal(
            Seq(StandardPreamble, chooseLine, timeoutLine, "DNF_ONLINE stage=settle event=town reason=eplp_timeout area=pre_dungeon signal=timeout_fallback"),
            AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.Equal(OnlineSettlementNoticeKind.EplpTimeout, rig.Ctrl.CurrentNotice?.Kind);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));
        AssertReturnTarget(Assert.Single(rig.Targets), false);
    }

    [Theory]
    [InlineData(SettlementChoice.Retry, ChooseRetryByPlayer, 1748L,
        "DNF_ONLINE stage=settle event=fail reason=return_wait_timeout step=Retry",
        "DNF_ONLINE stage=settle event=town reason=return_timeout area=pre_dungeon signal=timeout_fallback",
        SettlementChoice.ReturnToTown)]
    [InlineData(SettlementChoice.SelectDungeon, ChooseSelectDungeonByPlayer, 1748L,
        "DNF_ONLINE stage=settle event=fail reason=return_wait_timeout step=SelectDungeon",
        "DNF_ONLINE stage=settle event=choose choice=SelectDungeon source=return_timeout",
        SettlementChoice.SelectDungeon)]
    [InlineData(SettlementChoice.ReturnToTown, ChooseReturnByPlayer, 1348L,
        "DNF_ONLINE stage=settle event=fail reason=return_wait_timeout step=ReturnToTown",
        "DNF_ONLINE stage=settle event=town reason=return_timeout area=pre_dungeon signal=timeout_fallback",
        SettlementChoice.ReturnToTown)]
    public void Waiting_for_the_server_return_signal_times_out_per_choice_with_its_own_log_and_safe_completion(
        SettlementChoice choice, string chooseLine, long timeoutAtMs, string timeoutLine, string endLine, SettlementChoice completedChoice)
    {
        // 保护:1/75 执行确认之后一直等不到回城信号:到点写"等回城超时"行并带待决选择名;
        // 回城用 TownReturnWaitTimeoutMs(这里 300),另两项用 ReturnWaitTimeoutMs(这里 700);
        // 超时收尾——选的是"选其他副本"就照选的交还(写一行来源为 return_timeout 的选择行,不落城镇目标),
        // 选的是"再次挑战"或"回城"都安全回城(收尾行来源 return_timeout、信号 timeout_fallback),绝不重进副本。
        // 时间:选择在 1032;1048 读到执行确认进入等回城,起点 1048;回城 1048+300=1348,其余 1048+700=1748。
        using var rig = new DirectRig(o =>
        {
            o.ReturnWaitTimeoutMs = 700;
            o.TownReturnWaitTimeoutMs = 300;
        });
        ReachMenu(rig, payload => rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1])));   // 只确认,不推回城信号

        Assert.True(rig.Ctrl.Choose(choice));
        rig.Poll();   // 1048
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Equal(Seq(StandardPreamble, chooseLine), AllLogs(rig));

        rig.PollAt(timeoutAtMs - 1);
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        Assert.Equal(Seq(StandardPreamble, chooseLine), AllLogs(rig));

        rig.PollAt(timeoutAtMs);
        Assert.Equal(Seq(StandardPreamble, chooseLine, timeoutLine, endLine), AllLogs(rig));
        Assert.Equal(new[] { completedChoice }, rig.Completed);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(completedChoice == SettlementChoice.ReturnToTown ? 1 : 0, rig.Targets.Count);
        Assert.Equal(new ushort[] { 72, 73, 74, 75 }, SentIds(rig));   // 绝不补发 1/15、1/16 一类命令
    }

    // ----------------------------------------------------------------------------------------------
    // 1/75 里被忽略的回复不写日志
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Ignored_1_75_replies_write_no_log_and_do_not_move_the_phase()
    {
        // 保护:等 1/75 回复期间,下面这些回复都被忽略——没有任何日志、阶段不变、不回城:
        // 畸形(不足 3 字节)、回显的命令不是刚发出的那条、成功但标志是 2(只是界面光标同步,不是执行确认);
        // 只有真正的执行确认 [1,1,2] + 0/2 才收尾。
        using var rig = new DirectRig();
        ReachMenu(rig, _ => { });
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        string[] before = Seq(StandardPreamble, ChooseReturnByPlayer);
        Assert.Equal(before, AllLogs(rig));

        rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1 });          // 畸形
        rig.Transport.Push(PacketClass.Cmd, 75, new byte[0]);                  // 空
        rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(0));                    // 回显的是再次挑战,不是返回城镇
        rig.Transport.Push(PacketClass.Cmd, 75, EplpRejected(8, 0));           // 别的命令的拒绝回复
        rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 2, 2 });       // 标志 2:光标同步
        rig.Poll();
        Assert.Equal(before, AllLogs(rig));
        Assert.Equal(OnlineSettlementPhase.WaitingEplpReply, rig.Ctrl.Phase);
        Assert.Empty(rig.Completed);
        Assert.Null(rig.Ctrl.CurrentNotice?.Code);
        Assert.Equal(OnlineSettlementNoticeKind.WaitingReturn, rig.Ctrl.CurrentNotice?.Kind);

        rig.Transport.Push(PacketClass.Cmd, 75, new byte[] { 1, 1, 2 });
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.WaitingServerReturn, rig.Ctrl.Phase);
        Assert.Equal(before, AllLogs(rig));   // 执行确认本身不写日志,只改阶段

        PushNoti2(rig.Transport);
        rig.Poll();
        Assert.Equal(Seq(before, TownConfirmedByNoti2), AllLogs(rig));
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
    }
}
