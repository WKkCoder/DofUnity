using Dnf.Net.Frame;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 第七轮缺口补测":1/74(选牌)"同一条命令绝不自动重发"在宿主回调重入时的防线。
/// 正常路径上 1/74 只从"收到 1/73 回复且阶段仍是等 73"这一处发出,发出前阶段就改成"发送选牌中",重复回复、回复与超时同轮询等都被阶段闸门挡掉;
/// 唯一的缝隙是:阶段检查通过之后、阶段真正改掉之前,控制器要先写一行"73 回复成功"日志——宿主的日志回调若在此时重入轮询,
/// 同一批里排队的第二份 1/73 回复会在阶段还没改时被处理,发出第一条 1/74;外层随后才进入发送选牌的步骤,此时只有"已发选牌"标志能拦住第二次。
/// 场景台复用 <see cref="OnlineSettlementGapAKit.DirectRig"/>(手动逻辑时钟、无线程),全部合成数据。
/// </summary>
public class OnlineSettlementGapA7ReentrantPollTests
{
    private static ushort[] SentIds(DirectRig rig) => rig.Transport.Sent.Select(s => s.PacketId).ToArray();

    /// <summary>
    /// 保护的行为:同一次结算里,哪怕宿主日志回调在"73 回复成功"那行日志里重入了轮询、同批又排着第二份相同的 1/73 回复,
    /// 也只发出一条 1/74(载荷 [模式 0, 槽 1])、只写一行"发出选牌"日志;之后选牌发送成功照常进入菜单、倒计时满额开始。
    /// (若"已发选牌"标志没有置位,外层会在内层已发出之后再发一条 1/74,发送序列变成 72,73,74,74、日志里"发出选牌"出现两次。)
    /// </summary>
    [Fact]
    public void A_host_log_callback_that_reenters_poll_cannot_make_the_controller_send_the_card_pick_twice()
    {
        var logs = new List<string>();
        int reentries = 0;
        DirectRig rig = null!;
        rig = new DirectRig(o => o.Log = line =>
        {
            logs.Add(line);
            if (line == OnlineSettlementGapA2Kit.Ok73 && reentries == 0)
            {
                reentries++;
                rig.Ctrl.Poll(rig.NowMs, 0);   // 宿主日志回调里重入轮询(只在第一次写出"73 回复成功"时触发一次)
            }
        });
        using var owned = rig;

        // 推进到"等 73 回复":1/72 → 回复 → 发出 1/73
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.AdvancingCardRight, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72, 73 }, SentIds(rig));
        Assert.Equal(0, reentries);

        // 同一批里排两份相同的 1/73 回复(槽 0 无权;槽 1、2 有权 → 取第一个有权槽 1),一次轮询里处理
        byte[] rights = CardRightBytes(-1, 1, 1);
        rig.Transport.Push(PacketClass.Cmd, 73, rights);
        rig.Transport.Push(PacketClass.Cmd, 73, rights);
        rig.Poll();

        Assert.Equal(1, reentries);
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        Assert.Equal(new byte[] { 0, 1 }, rig.Transport.Sent[2].Payload);    // [模式 0, 槽 1]
        Assert.Equal((byte?)1, rig.Ctrl.SelectedCardIndex);
        Assert.False(rig.Ctrl.CardSelectSkipped);
        Assert.Equal(OnlineSettlementPhase.SendingSelectCard, rig.Ctrl.Phase);
        // 日志:内层先处理第二份回复(第二行"73 成功"),发出选牌只写一行
        string[] expected = { OnlineSettlementGapA2Kit.Show, OnlineSettlementGapA2Kit.Sent72, OnlineSettlementGapA2Kit.Ok72,
            OnlineSettlementGapA2Kit.Sent73, OnlineSettlementGapA2Kit.Ok73, OnlineSettlementGapA2Kit.Ok73, OnlineSettlementGapA2Kit.Sent74 };
        Assert.Equal(expected, logs);

        // 下一轮:选牌发送成功 → 菜单就绪、提示撤掉、倒计时满额(DirectRig 缺省 5000 毫秒)开始;不再多发任何命令
        rig.Poll();
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
        Assert.Null(rig.Ctrl.CurrentNotice);
        Assert.Equal(5000, rig.Ctrl.RemainingCountdownMs);
        Assert.Equal(new ushort[] { 72, 73, 74 }, SentIds(rig));
        Assert.Equal(expected.Append(OnlineSettlementGapA2Kit.Ok74).ToArray(), logs);
        Assert.Empty(rig.Completed);
        Assert.Equal(0, rig.Disconnects);
    }
}
