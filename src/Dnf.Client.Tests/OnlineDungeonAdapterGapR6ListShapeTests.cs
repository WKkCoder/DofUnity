using System.Reflection;
using System.Runtime.CompilerServices;
using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 结算映射器"列表拷贝助手"的第六轮缺口测试(不改前几轮的 <c>OnlineDungeonAdapterGapMapperTests</c>):
/// 联网层真解析器产出的通关奖励通知形状是固定的(16 项经验、4 个保留字节、两组各 4 个奖励槽),
/// 前几轮的映射测试因此只走到过这些"满表"形状;映射器里五个列表助手(经验值、保留字节、任务物品、两组奖励槽)
/// 的"空表复用共享空数组 / 单项原样保留 / 多项逐项拷贝"三种形状,只有任务物品这一个被真解析器覆盖到。
/// 这里用反射造出形状各异的奖励通知(其构造函数与条目构造函数都是 internal,与本程序集对联网层 internal 类型的做法一致),
/// 再用反射把它挂进结果对象,只喂映射器——不经过任何状态机、套接字、线程或墙钟。
/// 三个形状让每个列表各自出现一次空、单、多,且互相搭配不同(防止助手之间串用);所有字段都填互不相同的值。
/// </summary>
public sealed class OnlineDungeonAdapterGapR6ListShapeTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ------------------------------------------------------------------------------------------
    // 造形状各异的奖励通知(反射调 internal 构造函数)
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

    // 从上一轮 Mapper 测试复制来的辅助(那边是 private,按任务要求不改原文件,所以复制并改了类内名字):造一个只带奖励记录的结果对象。
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

    /// <summary>映射并核对:只有奖励记录、没有结果记录也没有到达事件时,奖励段按"有数据"隐式成功、结果段不适用。</summary>
    private static OnlineSettlementRewardData Map(SettlementNoticeB notice)
    {
        OnlineDungeonSettlementData data = OnlineDungeonSettlementMapper.Map(9611, "dungeon/test/r6shape.dgn", 2, 7, 3, 5, ResultWithReward(notice));

        Assert.Equal(9611, data.DungeonId);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, data.ResultParseState);
        Assert.Null(data.Result);
        Assert.Equal(OnlineSettlementParseState.Succeeded, data.RewardParseState);
        Assert.NotNull(data.Reward);
        return data.Reward;
    }

    private static void AssertSlot(OnlineSettlementRewardSlotData slot, byte status, int reserved, int value, int itemId, int itemData, bool hasItem)
    {
        Assert.Equal(status, slot.Status);
        Assert.Equal(reserved, slot.Reserved);
        Assert.Equal(value, slot.Value);
        Assert.Equal(itemId, slot.ItemId);
        Assert.Equal(itemData, slot.ItemData);
        Assert.Equal(hasItem, slot.HasItem);
    }

    private static void AssertItem(OnlineSettlementItemData item, ushort slot, int itemId, int data, ushort durability, byte amplifyType, ushort amplifyValue)
    {
        Assert.Equal(slot, item.Slot);
        Assert.Equal(itemId, item.ItemId);
        Assert.Equal(data, item.Data);
        Assert.Equal(durability, item.Durability);
        Assert.Equal(amplifyType, item.AmplifyType);
        Assert.Equal(amplifyValue, item.AmplifyValue);
    }

    // ------------------------------------------------------------------------------------------
    // 三个形状:每个列表各出现一次 空 / 单 / 多
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:形状 A(经验值空、任务物品单项、奖励槽三项、保留字节空、特权槽单项)——空的经验值与保留字节复用共享空数组(基础/段位加成经验退化为 0),单项的任务物品与特权槽原样保留,三项奖励槽逐项逐字段保留且顺序不变。
    /// 标量字段(活动类型 6、接收者值 70707、比例 80808、标志 假/真/假)各取各的来源、互不串位。
    /// </summary>
    [Fact]
    public void ShapeAEmptyExpAndReservedSingleItemAndPremiumThreeRewardSlots()
    {
        SettlementNoticeB notice = NoticeB(
            exp: new int[0],
            expKind: 6,
            items: new[] { Item(31, 50031, 60031, 701, 7, 801) },
            rewards: new[] { Slot(2, 111, 222, 333, 444), Slot(1, 112, 223, 0, 0), Slot(0, 0, 0, 0, 0) },
            receiverValue: 70707,
            reserved: new byte[0],
            premium: new[] { Slot(1, 121, 232, 0, 0) },
            percent: 80808,
            returnUser: false,
            eventCharac: true,
            deadPenalty: false);

        OnlineSettlementRewardData reward = Map(notice);

        Assert.Same(Array.Empty<int>(), reward.ExpValues); // 空经验值:共享空数组
        Assert.Equal(0, reward.BaseExp);
        Assert.Equal(0, reward.RankBonusExp);
        Assert.Same(Array.Empty<byte>(), reward.ReservedBytes); // 空保留字节:共享空数组

        Assert.Single(reward.QuestItems);
        AssertItem(reward.QuestItems[0], 31, 50031, 60031, 701, 7, 801);

        Assert.Equal(3, reward.Rewards.Count);
        AssertSlot(reward.Rewards[0], 2, 111, 222, 333, 444, true);
        AssertSlot(reward.Rewards[1], 1, 112, 223, 0, 0, false);
        AssertSlot(reward.Rewards[2], 0, 0, 0, 0, 0, false);

        Assert.Single(reward.PremiumRewards);
        AssertSlot(reward.PremiumRewards[0], 1, 121, 232, 0, 0, false);

        Assert.Equal((byte)6, reward.ExpKind);
        Assert.Equal(70707, reward.ReceiverValue);
        Assert.Equal(80808, reward.Percent);
        Assert.False(reward.IsReturnUserParty);
        Assert.True(reward.IsEventCharacParty);
        Assert.False(reward.HasDeadUserPenalty);
    }

    /// <summary>
    /// 保护:形状 B(经验值单项、任务物品三项、奖励槽空、保留字节单项、特权槽两项)——单项的经验值与保留字节原样保留(基础经验 = 该项、段位加成经验 = 0),三项任务物品逐项逐字段保留,空奖励槽复用共享空数组而相邻的两项特权槽照常保留(两组槽位互不串用)。
    /// 标志取与形状 A 相反的一组(真/假/真),证明三个标志互不串位。
    /// </summary>
    [Fact]
    public void ShapeBSingleExpAndReservedThreeItemsEmptyRewardSlotsAndTwoPremiumSlots()
    {
        SettlementNoticeB notice = NoticeB(
            exp: new[] { 4111 },
            expKind: 3,
            items: new[]
            {
                Item(41, 51041, 61041, 711, 1, 811),
                Item(42, 51042, 61042, 712, 2, 812),
                Item(43, 51043, 61043, 713, 3, 813),
            },
            rewards: new SettlementRewardInfo[0],
            receiverValue: 60606,
            reserved: new byte[] { 0x5A },
            premium: new[] { Slot(2, 131, 242, 343, 454), Slot(0, 0, 0, 0, 0) },
            percent: 90909,
            returnUser: true,
            eventCharac: false,
            deadPenalty: true);

        OnlineSettlementRewardData reward = Map(notice);

        Assert.Single(reward.ExpValues);
        Assert.Equal(4111, reward.ExpValues[0]);
        Assert.Equal(4111, reward.BaseExp);
        Assert.Equal(0, reward.RankBonusExp); // 只有一项:段位加成经验退化为 0
        Assert.Equal(new byte[] { 0x5A }, reward.ReservedBytes.ToArray());

        Assert.Equal(3, reward.QuestItems.Count);
        AssertItem(reward.QuestItems[0], 41, 51041, 61041, 711, 1, 811);
        AssertItem(reward.QuestItems[1], 42, 51042, 61042, 712, 2, 812);
        AssertItem(reward.QuestItems[2], 43, 51043, 61043, 713, 3, 813);

        Assert.Same(Array.Empty<OnlineSettlementRewardSlotData>(), reward.Rewards); // 空奖励槽:共享空数组

        Assert.Equal(2, reward.PremiumRewards.Count);
        AssertSlot(reward.PremiumRewards[0], 2, 131, 242, 343, 454, true);
        AssertSlot(reward.PremiumRewards[1], 0, 0, 0, 0, 0, false);

        Assert.Equal((byte)3, reward.ExpKind);
        Assert.Equal(60606, reward.ReceiverValue);
        Assert.Equal(90909, reward.Percent);
        Assert.True(reward.IsReturnUserParty);
        Assert.False(reward.IsEventCharacParty);
        Assert.True(reward.HasDeadUserPenalty);
    }

    /// <summary>
    /// 保护:形状 C(经验值三项、任务物品空、奖励槽单项、保留字节三项、特权槽空)——三项经验值与三项保留字节逐项保留(基础 = 第 0 项、段位加成 = 第 1 项),空任务物品与空特权槽各自复用共享空数组。
    /// 单项奖励槽原样保留且带物品状态,标量字段取第三组互不相同的值。
    /// </summary>
    [Fact]
    public void ShapeCThreeExpAndReservedEmptyItemsAndPremiumSingleRewardSlot()
    {
        SettlementNoticeB notice = NoticeB(
            exp: new[] { 7001, 7002, 7003 },
            expKind: 4,
            items: new SettlementItemEntry[0],
            rewards: new[] { Slot(2, 141, 252, 353, 464) },
            receiverValue: 50505,
            reserved: new byte[] { 0x11, 0x22, 0x33 },
            premium: new SettlementRewardInfo[0],
            percent: 70707,
            returnUser: true,
            eventCharac: true,
            deadPenalty: false);

        OnlineSettlementRewardData reward = Map(notice);

        Assert.Equal(new[] { 7001, 7002, 7003 }, reward.ExpValues.ToArray());
        Assert.Equal(7001, reward.BaseExp);
        Assert.Equal(7002, reward.RankBonusExp);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, reward.ReservedBytes.ToArray());

        Assert.Same(Array.Empty<OnlineSettlementItemData>(), reward.QuestItems); // 空任务物品:共享空数组

        Assert.Single(reward.Rewards);
        AssertSlot(reward.Rewards[0], 2, 141, 252, 353, 464, true);

        Assert.Same(Array.Empty<OnlineSettlementRewardSlotData>(), reward.PremiumRewards); // 空特权槽:共享空数组

        Assert.Equal((byte)4, reward.ExpKind);
        Assert.Equal(50505, reward.ReceiverValue);
        Assert.Equal(70707, reward.Percent);
        Assert.True(reward.IsReturnUserParty);
        Assert.True(reward.IsEventCharacParty);
        Assert.False(reward.HasDeadUserPenalty);
    }
}
