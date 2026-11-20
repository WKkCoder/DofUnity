using Dnf.Play;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 联网副本中性类型的第三轮"边界"补充(接在前几轮之后,不改既有文件):
/// 结算奖励槽位的"带物品"判定边界、离开副本日志里两个解析标签后缀对 null 与空串的处理。全部用合成数据。
/// </summary>
public class OnlineDungeonTypesGapR3EdgeTests
{
    /// <summary>
    /// 保护:奖励槽位"带物品"只在状态正好为 2 时成立(与 Net 层结算奖励槽位的同名判定一致):
    /// 状态 0、1 没有物品,2 有,大于 2 的状态 3、4、255 也没有;构造时给的五个字段原样保留。
    /// 推导:HasItem = (Status == 2);把相等放宽成"不小于 2"时,状态 3、4、255 会被误判成带物品。
    /// </summary>
    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, false)]
    [InlineData((byte)2, true)]
    [InlineData((byte)3, false)]
    [InlineData((byte)4, false)]
    [InlineData((byte)255, false)]
    public void RewardSlotHasItemOnlyWhenTheStatusIsExactlyTwo(byte status, bool expectedHasItem)
    {
        var slot = new OnlineSettlementRewardSlotData(status, 11, 22, 33, 44);

        Assert.Equal(status, slot.Status);
        Assert.Equal(expectedHasItem, slot.HasItem);
        Assert.Equal(11, slot.Reserved); // 构造顺序:状态、保留值、数值、物品编号、物品数据
        Assert.Equal(22, slot.Value);
        Assert.Equal(33, slot.ItemId);
        Assert.Equal(44, slot.ItemData);
    }

    /// <summary>
    /// 保护:离开副本日志里的"结算解析"标签是可选后缀——结果与奖励各自在 null 或空串时都不输出后缀,只有非空文本才输出;
    /// 两个后缀互相独立,顺序固定为 result 在前、reward 在后。
    /// 推导:基础行 = "DNF_ONLINE stage=dungeon event=leave reason=giveup reported=7 dropped=2 rooms=3";
    /// 后缀只在对应文本非空时追加 " result=文本" / " reward=文本"(空串若被放行会变成 " reward=unknown" 这种假字段)。
    /// </summary>
    [Fact]
    public void LeaveLogOmitsBothParseSuffixesForNullAndEmptyAndKeepsThemIndependent()
    {
        const string baseLine = "DNF_ONLINE stage=dungeon event=leave reason=giveup reported=7 dropped=2 rooms=3";

        Assert.Equal(baseLine, OnlineDungeonLog.Leave("giveup", 7, 2, 3));
        Assert.Equal(baseLine, OnlineDungeonLog.Leave("giveup", 7, 2, 3, resultParse: null, rewardParse: null));
        Assert.Equal(baseLine, OnlineDungeonLog.Leave("giveup", 7, 2, 3, resultParse: string.Empty, rewardParse: string.Empty));
        Assert.Equal(baseLine, OnlineDungeonLog.Leave("giveup", 7, 2, 3, resultParse: null, rewardParse: string.Empty));
        Assert.Equal(baseLine, OnlineDungeonLog.Leave("giveup", 7, 2, 3, resultParse: string.Empty, rewardParse: null));
        Assert.Equal(baseLine + " result=succeeded", OnlineDungeonLog.Leave("giveup", 7, 2, 3, resultParse: "succeeded", rewardParse: string.Empty));
        Assert.Equal(baseLine + " reward=failed", OnlineDungeonLog.Leave("giveup", 7, 2, 3, resultParse: string.Empty, rewardParse: "failed"));
        Assert.Equal(baseLine + " result=succeeded reward=failed", OnlineDungeonLog.Leave("giveup", 7, 2, 3, "succeeded", "failed"));
    }
}
