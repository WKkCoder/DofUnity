using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;
using static Dnf.Client.Tests.OnlineSettlementGapA7Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 第七轮缺口补测":结算开始时流程里没有联网房间局(城镇阶段没有局会话,或副本阶段但局会话不是联网房间局)时,
/// 控制器交给摘要构造器的"本地统计"缺省值。
/// 三个本地字段(金币 / 拾取物品数 / 经验)里只有金币会被读到:摘要构造器仅在"奖励段解析成功、选中了奖励槽、本地金币为正且与槽金额不同"时才记一条 reward_gold 差异;
/// 物品数与经验在整个仓库里没有任何读取者(只被控制器写进一个局部上下文对象,摘要构造器从不看它们),所以它们的缺省值没有可观察的行为,不在这里测。
/// </summary>
public class OnlineSettlementGapA7NoRunTests
{
    private static ushort[] SentIds(DirectRig rig) => rig.Transport.Sent.Select(s => s.PacketId).ToArray();

    /// <summary>
    /// 保护的行为:没有联网房间局时本地拾取的金币记为 0("没有本地金币",不参与对账)——
    /// 服务端奖励槽金额无论是 520 还是 0,都不会冒出 reward_gold 差异:摘要的差异清单为空、展示日志写 diffs=0、日志里没有任何 event=diff 行。
    /// (若缺省金币不是 0 而是个正数,它与槽金额不同,就会多出一条差异字段与对应日志行,展示日志也会变成 diffs=1。)
    /// 两种"没有房间局"的形态都测:城镇阶段(流程里没有局会话)、副本阶段但局会话是别种局。
    /// </summary>
    [Theory]
    [InlineData(false, 520)]
    [InlineData(true, 520)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public void A_settlement_without_a_hosted_run_has_no_local_gold_to_reconcile_with_the_server_reward(bool inDungeonWithForeignRun, int serverGold)
    {
        using var rig = new DirectRig();
        if (inDungeonWithForeignRun)
        {
            rig.Flow.StartDungeon("dungeon/test/dunga.dgn");
            Assert.Equal(GameStage.Dungeon, rig.Flow.Stage);
            Assert.IsType<OtherRun>(rig.Flow.Run);
        }
        else
        {
            Assert.Equal(GameStage.Town, rig.Flow.Stage);
            Assert.Null(rig.Flow.Run);
        }

        var completed = new List<SettlementChoice>();
        var ready = new OnlineDungeonSettlementReady(RewardOnlyData(serverGold), null, c => completed.Add(c));
        rig.PollAt(1000);
        rig.Ctrl.OnSettlementStarted(ready);

        // 结算已接管:停留已置位、进入 72 推进(只发出了 1/72)、还没有任何选择交回宿主
        Assert.True(ready.HoldForMenu);
        Assert.True(rig.Ctrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(new ushort[] { 72 }, SentIds(rig));
        Assert.Empty(completed);

        // 摘要:服务端奖励槽金额原样取用(520 / 0);联网上报击杀 2 无本地统计可比;所以差异清单必为空
        OnlineSettlementSummary summary = rig.Ctrl.Summary;
        Assert.NotNull(summary);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, summary.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Succeeded, summary.RewardParseState);
        Assert.True(summary.RewardGold.HasValue);
        Assert.Equal(serverGold, summary.RewardGold.Value);
        Assert.Equal(2, summary.Kills.Value);
        Assert.Empty(summary.Drops);
        Assert.Empty(summary.DiffFields);

        // 日志:只有"展示(diffs=0)"与"发出 1/72"两行,没有任何 event=diff 行
        Assert.Equal(new[] { ShowRewardOnly, Sent72Line }, rig.Logs);
        Assert.DoesNotContain(rig.Logs, l => l.Contains("event=diff"));
    }
}
