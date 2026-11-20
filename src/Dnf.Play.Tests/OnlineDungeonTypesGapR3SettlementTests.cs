using Dnf.Play;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 结算交接中性类型(<c>OnlineSettlement*</c> 系列)的第三轮"缺口"测试:解析状态枚举的数值、
/// 奖励数据里"基础经验/段位加成经验"随经验值表长度的取值、各列表缺省为空、少量派生属性。
/// 这些类型在 Play 层、不引用联网层,所以用合成数据直接构造;适配器把联网载荷映射成它们的覆盖在 Client 测试里。
/// </summary>
public class OnlineDungeonTypesGapR3SettlementTests
{
    private static OnlineSettlementRewardData RewardWithExp(IReadOnlyList<int> expValues)
    {
        return new OnlineSettlementRewardData(expValues, 0, null, null, 0, null, null, 0, false, false, false);
    }

    /// <summary>
    /// 保护:结算解析状态的数值固定为 0(不适用)/1(成功)/2(失败),与联网层同名枚举一一对应(中性镜像),
    /// 三个名字取值互不相同——失败若被改成 1,就会与成功别名,按名字比较的判断全部失效。
    /// </summary>
    [Fact]
    public void ParseStateNumericValuesAreFixedAndPairwiseDistinct()
    {
        Assert.Equal(0, (int)OnlineSettlementParseState.NotApplicable);
        Assert.Equal(1, (int)OnlineSettlementParseState.Succeeded);
        Assert.Equal(2, (int)OnlineSettlementParseState.Failed);

        Assert.NotEqual(OnlineSettlementParseState.Succeeded, OnlineSettlementParseState.Failed);
        Assert.NotEqual(OnlineSettlementParseState.NotApplicable, OnlineSettlementParseState.Failed);
        Assert.NotEqual(OnlineSettlementParseState.NotApplicable, OnlineSettlementParseState.Succeeded);

        OnlineSettlementParseState[] all = Enum.GetValues<OnlineSettlementParseState>();
        Assert.Equal(3, all.Length);
        Assert.Equal(3, all.Distinct().Count());
        Assert.Equal(OnlineSettlementParseState.Failed, (OnlineSettlementParseState)2);
        Assert.Equal("Failed", ((OnlineSettlementParseState)2).ToString());
        Assert.Equal("Succeeded", ((OnlineSettlementParseState)1).ToString());
    }

    /// <summary>
    /// 保护:基础经验取经验值表第 0 项、段位加成经验取第 1 项,缺项时为 0——
    /// 空表(0 项)、单项(1 项:基础有值、加成为 0)、两项、多项(只看前两项)各取一例。
    /// 推导:BaseExp = Count &gt; 0 ? [0] : 0;RankBonusExp = Count &gt; 1 ? [1] : 0。
    /// </summary>
    [Fact]
    public void BaseAndRankBonusExpFollowTheExpValueListLength()
    {
        OnlineSettlementRewardData empty = RewardWithExp(Array.Empty<int>());
        Assert.Equal(0, empty.BaseExp);
        Assert.Equal(0, empty.RankBonusExp);
        Assert.Empty(empty.ExpValues);

        OnlineSettlementRewardData one = RewardWithExp(new[] { 321 });
        Assert.Equal(321, one.BaseExp);
        Assert.Equal(0, one.RankBonusExp);
        Assert.Single(one.ExpValues);

        OnlineSettlementRewardData two = RewardWithExp(new[] { 321, 654 });
        Assert.Equal(321, two.BaseExp);
        Assert.Equal(654, two.RankBonusExp);

        OnlineSettlementRewardData three = RewardWithExp(new[] { 321, 654, 987 });
        Assert.Equal(321, three.BaseExp);
        Assert.Equal(654, three.RankBonusExp);
        Assert.Equal(3, three.ExpValues.Count);

        OnlineSettlementRewardData none = RewardWithExp(null);
        Assert.Equal(0, none.BaseExp);
        Assert.Equal(0, none.RankBonusExp);
        Assert.Empty(none.ExpValues);

        OnlineSettlementRewardData negative = RewardWithExp(new[] { -5, -6 });
        Assert.Equal(-5, negative.BaseExp);
        Assert.Equal(-6, negative.RankBonusExp);
    }

    /// <summary>
    /// 保护:奖励数据构造时每个字段原样保存(同类型相邻字段用互不相同的值,防串位),
    /// 各列表传空引用时缺省成空集合而不是 null;槽位的"有物品"只在状态为 2 时成立。
    /// </summary>
    [Fact]
    public void RewardDataKeepsEveryConstructorFieldAndDefaultsNullListsToEmpty()
    {
        var items = new[] { new OnlineSettlementItemData(11, 22, 33, 44, 55, 66) };
        var rewards = new[] { new OnlineSettlementRewardSlotData(2, 71, 72, 73, 74) };
        var premium = new[] { new OnlineSettlementRewardSlotData(1, 81, 82, 83, 84), new OnlineSettlementRewardSlotData(0, 91, 92, 93, 94) };
        var reward = new OnlineSettlementRewardData(
            new[] { 5001, 5002 },
            7,
            items,
            rewards,
            8001,
            new byte[] { 3, 1, 4 },
            premium,
            8002,
            true,
            false,
            true);

        Assert.Equal(new[] { 5001, 5002 }, reward.ExpValues);
        Assert.Equal((byte)7, reward.ExpKind);
        Assert.Equal(8001, reward.ReceiverValue);
        Assert.Equal(8002, reward.Percent);
        Assert.Equal(new byte[] { 3, 1, 4 }, reward.ReservedBytes);
        Assert.True(reward.IsReturnUserParty);
        Assert.False(reward.IsEventCharacParty);
        Assert.True(reward.HasDeadUserPenalty);
        Assert.Same(items, reward.QuestItems);
        Assert.Same(rewards, reward.Rewards);
        Assert.Same(premium, reward.PremiumRewards);

        OnlineSettlementItemData item = reward.QuestItems[0];
        Assert.Equal((ushort)11, item.Slot);
        Assert.Equal(22, item.ItemId);
        Assert.Equal(33, item.Data);
        Assert.Equal((ushort)44, item.Durability);
        Assert.Equal((byte)55, item.AmplifyType);
        Assert.Equal((ushort)66, item.AmplifyValue);

        Assert.True(reward.Rewards[0].HasItem); // 状态 2
        Assert.False(reward.PremiumRewards[0].HasItem); // 状态 1
        Assert.False(reward.PremiumRewards[1].HasItem); // 状态 0
        Assert.Equal(71, reward.Rewards[0].Reserved);
        Assert.Equal(72, reward.Rewards[0].Value);
        Assert.Equal(73, reward.Rewards[0].ItemId);
        Assert.Equal(74, reward.Rewards[0].ItemData);

        var bare = new OnlineSettlementRewardData(null, 0, null, null, 0, null, null, 0, false, false, false);
        Assert.Empty(bare.ExpValues);
        Assert.Empty(bare.QuestItems);
        Assert.Empty(bare.Rewards);
        Assert.Empty(bare.ReservedBytes);
        Assert.Empty(bare.PremiumRewards);
        Assert.False(bare.IsReturnUserParty);
        Assert.False(bare.IsEventCharacParty);
        Assert.False(bare.HasDeadUserPenalty);
    }

    /// <summary>
    /// 保护:通关结果数据构造时每个字段原样保存(用时分与基础分取不同值),条目表缺省为空;
    /// "任务迷宫"派生属性只在标志恰为 1 时成立(0、1、2 各取一例);
    /// 整局快照里副本路径缺省为空串,其余字段与两个解析状态原样保存。
    /// </summary>
    [Fact]
    public void ResultAndSettlementDataKeepFieldsAndDeriveQuestMazeFromFlagOne()
    {
        var best = new[] { new OnlineSettlementBestTime(4001, 90210, true), new OnlineSettlementBestTime(4002, 80110, false) };
        var result = new OnlineSettlementResultData(3, 123456, -13, 57, 1, true, best);
        Assert.Equal((byte)3, result.RankValue);
        Assert.Equal(123456, result.ClearTime);
        Assert.Equal((sbyte)-13, result.TimeBonus);
        Assert.Equal((sbyte)57, result.BaseScore);
        Assert.Equal((byte)1, result.QuestMazeFlag);
        Assert.True(result.IsQuestMaze);
        Assert.True(result.HasBestTimeBlock);
        Assert.Same(best, result.Entries);
        Assert.Equal((ushort)4001, result.Entries[0].UniqueId);
        Assert.Equal(90210, result.Entries[0].BestTime);
        Assert.True(result.Entries[0].IsNewRecord);
        Assert.Equal((ushort)4002, result.Entries[1].UniqueId);
        Assert.Equal(80110, result.Entries[1].BestTime);
        Assert.False(result.Entries[1].IsNewRecord);

        Assert.False(new OnlineSettlementResultData(0, 0, 0, 0, 0, false, null).IsQuestMaze);
        Assert.False(new OnlineSettlementResultData(0, 0, 0, 0, 2, false, null).IsQuestMaze);
        OnlineSettlementResultData bare = new OnlineSettlementResultData(0, 0, 0, 0, 1, false, null);
        Assert.True(bare.IsQuestMaze);
        Assert.False(bare.HasBestTimeBlock);
        Assert.Empty(bare.Entries);

        var data = new OnlineDungeonSettlementData(
            9101, null, 2, 14, 3, 6,
            OnlineSettlementParseState.Succeeded, result,
            OnlineSettlementParseState.Failed, null);
        Assert.Equal(9101, data.DungeonId);
        Assert.Equal(string.Empty, data.DungeonPath);
        Assert.Equal(2, data.Difficulty);
        Assert.Equal(14, data.ReportedKills);
        Assert.Equal(3, data.DroppedKills);
        Assert.Equal(6, data.RoomsEntered);
        Assert.Equal(OnlineSettlementParseState.Succeeded, data.ResultParseState);
        Assert.Same(result, data.Result);
        Assert.Equal(OnlineSettlementParseState.Failed, data.RewardParseState);
        Assert.Null(data.Reward);

        var withPath = new OnlineDungeonSettlementData(
            1, "dungeon/test/gapr3-settle.dgn", 0, 0, 0, 0,
            OnlineSettlementParseState.NotApplicable, null,
            OnlineSettlementParseState.NotApplicable, null);
        Assert.Equal("dungeon/test/gapr3-settle.dgn", withPath.DungeonPath);
    }
}
