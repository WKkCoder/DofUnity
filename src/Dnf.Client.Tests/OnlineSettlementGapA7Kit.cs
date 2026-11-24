using Dnf.Play;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 第七轮缺口补测"(OnlineSettlementGapA7*)共用夹具:
/// 合成"服务端奖励解析成功"的结算数据、两条手写的日志行字面量。
/// 驱动控制器的场景台复用前几轮的 <see cref="OnlineSettlementGapAKit.DirectRig"/> 与 <see cref="OnlineSettlementGapAKit.FakeSettlementTransport"/>(只用不改);
/// 所有编号、数值都是合成的。
/// </summary>
internal static class OnlineSettlementGapA7Kit
{
    /// <summary>通关结果段不适用、奖励段解析成功、没有掉落、没有差异字段时的"展示"日志(手写的整行字面量,不从生产代码取)。</summary>
    public const string ShowRewardOnly = "DNF_ONLINE stage=settle event=show timedout=false result=NotApplicable reward=Succeeded drops=0 diffs=0";

    /// <summary>开局发出 1/72 的日志行。</summary>
    public const string Sent72Line = "DNF_ONLINE stage=settle event=advance step=score_scroll status=sent";

    /// <summary>
    /// 合成的"服务端奖励解析成功"结算数据:通关结果段不适用;奖励段成功,只有奖励槽 0(状态 1:有金额、没有物品),金额为 <paramref name="slotGold"/>,
    /// 经验块、任务清图物品、其余槽位全为空;联网上报击杀 2。因为只有奖励段,摘要里唯一可能出现的差异字段就是金币对账的那一项。
    /// </summary>
    public static OnlineDungeonSettlementData RewardOnlyData(int slotGold)
    {
        var slot = new OnlineSettlementRewardSlotData(status: 1, reserved: 0, value: slotGold, itemId: 0, itemData: 0);
        var reward = new OnlineSettlementRewardData(
            expValues: null,
            expKind: 0,
            questItems: null,
            rewards: new[] { slot },
            receiverValue: 0,
            reservedBytes: null,
            premiumRewards: null,
            percent: 0,
            isReturnUserParty: false,
            isEventCharacParty: false,
            hasDeadUserPenalty: false);
        return new OnlineDungeonSettlementData(
            DungeonAId,
            "dungeon/test/dunga.dgn",
            difficulty: 0,
            reportedKills: 2,
            droppedKills: 0,
            roomsEntered: 1,
            resultParseState: OnlineSettlementParseState.NotApplicable,
            result: null,
            rewardParseState: OnlineSettlementParseState.Succeeded,
            reward: reward);
    }
}
