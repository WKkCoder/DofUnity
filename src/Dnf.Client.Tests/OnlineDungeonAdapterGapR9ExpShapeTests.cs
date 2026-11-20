using System.Reflection;
using System.Runtime.CompilerServices;
using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 结算映射器"经验值表长度"的第九轮缺口测试(不改前几轮的 <c>OnlineDungeonAdapterGapMapperTests</c>、
/// <c>OnlineDungeonAdapterGapR6ListShapeTests</c>):中性奖励数据里,基础经验取经验值表第 0 项、段位加成经验取第 1 项,表里没有那一项时都是 0。
/// 本程序集里此前的映射测试只造过 0、1、3、16 项的经验值表,"恰好 2 项"(第 1 项刚好存在的最小表长)一直是空档:
/// 把中性类型里 <c>Count &gt; 1</c> 的边界挪成 <c>Count &gt; 2</c> 时,只有恰好 2 项的表会读错(段位加成经验变成 0),其余长度都看不出差别,
/// 整套测试只会靠无关用例的偶发失败"碰巧"带出这种改动。这里给这个形状专门的断言,单独只跑本文件的用例就必须能分辨出来。
/// 联网层真解析器永远给 16 项,所以与第六轮一样用反射造形状各异的奖励通知(其构造函数与条目构造函数是 internal),
/// 只喂映射器,不经过状态机、套接字、线程或墙钟;所有字段都填互不相同的值。
/// </summary>
public sealed class OnlineDungeonAdapterGapR9ExpShapeTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ------------------------------------------------------------------------------------------
    // 造形状各异的奖励通知(反射调 internal 构造函数;从第六轮的列表形状测试复制,那边是 private,按规则不改原文件)
    // ------------------------------------------------------------------------------------------

    private static SettlementItemEntry Item(ushort slot, int itemId, int data, ushort durability, byte amplifyType, ushort amplifyValue)
    {
        object made = Activator.CreateInstance(
            typeof(SettlementItemEntry), Any, null, new object[] { slot, itemId, data, durability, amplifyType, amplifyValue }, null);
        Assert.NotNull(made);
        return (SettlementItemEntry)made;
    }

    private static SettlementRewardInfo Slot(byte status, int reserved, int value, int itemId, int itemData)
    {
        object made = Activator.CreateInstance(
            typeof(SettlementRewardInfo), Any, null, new object[] { status, reserved, value, itemId, itemData }, null);
        Assert.NotNull(made);
        return (SettlementRewardInfo)made;
    }

    private static SettlementNoticeB NoticeB(
        int[] exp, byte expKind, SettlementItemEntry[] items, SettlementRewardInfo[] rewards, int receiverValue,
        byte[] reserved, SettlementRewardInfo[] premium, int percent, bool returnUser, bool eventCharac, bool deadPenalty)
    {
        object made = Activator.CreateInstance(
            typeof(SettlementNoticeB), Any, null,
            new object[] { exp, expKind, items, rewards, receiverValue, reserved, premium, percent, returnUser, eventCharac, deadPenalty, 0 },
            null);
        Assert.NotNull(made);
        return (SettlementNoticeB)made;
    }

    /// <summary>只带经验值表、其余列表全空且标量全零的奖励通知(用来单独考察表长对基础/加成经验的影响)。</summary>
    private static SettlementNoticeB NoticeWithExpOnly(int[] exp)
    {
        return NoticeB(
            exp, 0, new SettlementItemEntry[0], new SettlementRewardInfo[0], 0,
            new byte[0], new SettlementRewardInfo[0], 0, false, false, false);
    }

    private static OnlineDungeonCombatResult ResultWithReward(SettlementNoticeB reward)
    {
        var result = (OnlineDungeonCombatResult)RuntimeHelpers.GetUninitializedObject(typeof(OnlineDungeonCombatResult));
        SetBacking(result, "Events", Array.Empty<OnlineCombatEvent>());
        SetBacking(result, "SettlementResult", null);
        SetBacking(result, "SettlementReward", reward);
        return result;
    }

    private static void SetBacking(OnlineDungeonCombatResult target, string property, object value)
    {
        FieldInfo field = typeof(OnlineDungeonCombatResult).GetField("<" + property + ">k__BackingField", Any);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    /// <summary>只有奖励记录、没有结果记录也没有到达事件:奖励段按"有数据"隐式成功、结果段不适用、结果数据为空。头部字段(六个)取互不相同的值。</summary>
    private static OnlineDungeonSettlementData MapReward(SettlementNoticeB notice)
    {
        OnlineDungeonSettlementData data = OnlineDungeonSettlementMapper.Map(9811, "dungeon/test/r9exp.dgn", 1, 23, 8, 6, ResultWithReward(notice));

        Assert.Equal(OnlineSettlementParseState.NotApplicable, data.ResultParseState);
        Assert.Null(data.Result);
        Assert.Equal(OnlineSettlementParseState.Succeeded, data.RewardParseState);
        Assert.NotNull(data.Reward);
        return data;
    }

    // ------------------------------------------------------------------------------------------
    // 恰好 2 项的经验值表
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:经验值表恰好两项 {8123, 4567} 时,两项都被拷贝(项数不少、顺序与取值不变),基础经验取第 0 项 8123、
    /// 段位加成经验取第 1 项 4567——第 1 项刚好存在的最小表长,必须取到它而不是退化成 0。
    /// 同一份通知里其余字段都取互不相同的值(任务物品 2 件、奖励槽 2 个、特权槽 3 个、保留字节 2 个、标志只有"死亡惩罚"为真),
    /// 证明这条形状下各列表与标量各归各位、不串位。
    /// 推导:Count = 2;BaseExp = Count &gt; 0 ? [0] : 0 = 8123;RankBonusExp = Count &gt; 1 ? [1] : 0 = 4567。
    /// </summary>
    [Fact]
    public void ExactlyTwoExpValuesGiveBothTheBaseAndTheRankBonusExp()
    {
        SettlementNoticeB notice = NoticeB(
            exp: new[] { 8123, 4567 },
            expKind: 5,
            items: new[] { Item(51, 52051, 62051, 721, 6, 821), Item(52, 52052, 62052, 722, 7, 822) },
            rewards: new[] { Slot(2, 151, 262, 363, 474), Slot(0, 0, 0, 0, 0) },
            receiverValue: 40404,
            reserved: new byte[] { 0x6B, 0x7C },
            premium: new[] { Slot(1, 161, 272, 0, 0), Slot(2, 162, 273, 373, 484), Slot(0, 0, 0, 0, 0) },
            percent: 20202,
            returnUser: false,
            eventCharac: false,
            deadPenalty: true);

        OnlineDungeonSettlementData data = MapReward(notice);
        OnlineSettlementRewardData reward = data.Reward;

        // 调用方给的头部字段
        Assert.Equal(9811, data.DungeonId);
        Assert.Equal("dungeon/test/r9exp.dgn", data.DungeonPath);
        Assert.Equal(1, data.Difficulty);
        Assert.Equal(23, data.ReportedKills);
        Assert.Equal(8, data.DroppedKills);
        Assert.Equal(6, data.RoomsEntered);

        // 经验值表:恰好两项,都在,顺序不变
        Assert.Equal(2, reward.ExpValues.Count);
        Assert.Equal(8123, reward.ExpValues[0]);
        Assert.Equal(4567, reward.ExpValues[1]);
        Assert.Equal(8123, reward.BaseExp);
        Assert.Equal(4567, reward.RankBonusExp);
        Assert.Equal((byte)5, reward.ExpKind);

        // 其余列表:任务物品 2 件
        Assert.Equal(2, reward.QuestItems.Count);
        Assert.Equal((ushort)51, reward.QuestItems[0].Slot);
        Assert.Equal(52051, reward.QuestItems[0].ItemId);
        Assert.Equal(62051, reward.QuestItems[0].Data);
        Assert.Equal((ushort)721, reward.QuestItems[0].Durability);
        Assert.Equal((byte)6, reward.QuestItems[0].AmplifyType);
        Assert.Equal((ushort)821, reward.QuestItems[0].AmplifyValue);
        Assert.Equal((ushort)52, reward.QuestItems[1].Slot);
        Assert.Equal(52052, reward.QuestItems[1].ItemId);
        Assert.Equal(62052, reward.QuestItems[1].Data);
        Assert.Equal((ushort)722, reward.QuestItems[1].Durability);
        Assert.Equal((byte)7, reward.QuestItems[1].AmplifyType);
        Assert.Equal((ushort)822, reward.QuestItems[1].AmplifyValue);

        // 奖励槽 2 个(第 0 个带物品、第 1 个无人)
        Assert.Equal(2, reward.Rewards.Count);
        Assert.Equal((byte)2, reward.Rewards[0].Status);
        Assert.Equal(151, reward.Rewards[0].Reserved);
        Assert.Equal(262, reward.Rewards[0].Value);
        Assert.Equal(363, reward.Rewards[0].ItemId);
        Assert.Equal(474, reward.Rewards[0].ItemData);
        Assert.True(reward.Rewards[0].HasItem);
        Assert.Equal((byte)0, reward.Rewards[1].Status);
        Assert.False(reward.Rewards[1].HasItem);

        // 特权槽 3 个(无物品 / 带物品 / 无人)
        Assert.Equal(3, reward.PremiumRewards.Count);
        Assert.Equal((byte)1, reward.PremiumRewards[0].Status);
        Assert.Equal(161, reward.PremiumRewards[0].Reserved);
        Assert.Equal(272, reward.PremiumRewards[0].Value);
        Assert.False(reward.PremiumRewards[0].HasItem);
        Assert.Equal((byte)2, reward.PremiumRewards[1].Status);
        Assert.Equal(162, reward.PremiumRewards[1].Reserved);
        Assert.Equal(273, reward.PremiumRewards[1].Value);
        Assert.Equal(373, reward.PremiumRewards[1].ItemId);
        Assert.Equal(484, reward.PremiumRewards[1].ItemData);
        Assert.True(reward.PremiumRewards[1].HasItem);
        Assert.Equal((byte)0, reward.PremiumRewards[2].Status);
        Assert.False(reward.PremiumRewards[2].HasItem);

        // 标量:接收者值、保留字节、比例、三个标志(只有死亡惩罚为真)
        Assert.Equal(40404, reward.ReceiverValue);
        Assert.Equal(new byte[] { 0x6B, 0x7C }, reward.ReservedBytes.ToArray());
        Assert.Equal(20202, reward.Percent);
        Assert.False(reward.IsReturnUserParty);
        Assert.False(reward.IsEventCharacParty);
        Assert.True(reward.HasDeadUserPenalty);
    }

    // ------------------------------------------------------------------------------------------
    // 表长逐个跨过边界
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:经验值表长度每跨过一个边界,映射出的基础经验与段位加成经验随之变化,且表里每一项都被拷贝(项数不少、顺序与取值不变):
    /// 0 项 → 两者都缺,0 / 0;1 项 → 基础有值、加成缺,6001 / 0;2 项(第 1 项刚好存在)→ 6001 / 6018;3、4 项 → 仍只看前两项,6001 / 6018。
    /// 表项取 6001 + 17 × 下标(6001、6018、6035、6052),下面的期望值按这个式子手算成字面量,不用被测公式再算一遍。
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 6001, 0)]
    [InlineData(2, 6001, 6018)]
    [InlineData(3, 6001, 6018)]
    [InlineData(4, 6001, 6018)]
    public void MappedBaseAndRankBonusExpFollowTheExpListLengthAcrossEachBoundary(int length, int expectedBase, int expectedRankBonus)
    {
        var exp = new int[length];
        for (int i = 0; i < length; i++)
        {
            exp[i] = 6001 + 17 * i;
        }

        OnlineSettlementRewardData reward = MapReward(NoticeWithExpOnly(exp)).Reward;

        Assert.Equal(length, reward.ExpValues.Count);
        Assert.Equal(exp, reward.ExpValues.ToArray());
        Assert.Equal(expectedBase, reward.BaseExp);
        Assert.Equal(expectedRankBonus, reward.RankBonusExp);
    }

    /// <summary>
    /// 保护:中性奖励数据本身(不经映射器)也在表长恰为 2 时同时给出基础与段位加成经验,表长 1 与 3 落在边界两侧
    /// (1 项:加成缺 → 0;3 项:仍只取第 1 项,第 2 项 3303 不参与)。用来在映射测试失败时分辨"问题在映射器还是在中性类型"。
    /// 推导:{3301} → 基础 3301、加成 0;{3301, 3302} → 3301 / 3302;{3301, 3302, 3303} → 3301 / 3302。
    /// </summary>
    [Fact]
    public void NeutralRewardDataTakesTheRankBonusFromTheSecondEntryOnceTwoEntriesExist()
    {
        OnlineSettlementRewardData one = NeutralReward(new[] { 3301 });
        Assert.Equal(3301, one.BaseExp);
        Assert.Equal(0, one.RankBonusExp);

        OnlineSettlementRewardData two = NeutralReward(new[] { 3301, 3302 });
        Assert.Equal(3301, two.BaseExp);
        Assert.Equal(3302, two.RankBonusExp);

        OnlineSettlementRewardData three = NeutralReward(new[] { 3301, 3302, 3303 });
        Assert.Equal(3301, three.BaseExp);
        Assert.Equal(3302, three.RankBonusExp);
    }

    private static OnlineSettlementRewardData NeutralReward(IReadOnlyList<int> exp)
    {
        return new OnlineSettlementRewardData(exp, 0, null, null, 0, null, null, 0, false, false, false);
    }
}
