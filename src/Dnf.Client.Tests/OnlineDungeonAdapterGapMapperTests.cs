using System.Reflection;
using System.Runtime.CompilerServices;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 结算映射器(<see cref="OnlineDungeonSettlementMapper"/>:联网层通关结果/奖励载荷 → Play 层中性结算数据)的"缺口"测试。
/// 现有覆盖(<c>OnlineDungeonFlowTests</c>)只断言了排名、用时、基础经验、首个奖励槽等少数字段;这里给载荷里每个字段都填互不相同的值,
/// 映射后逐字段断言,并覆盖空列表/单项/多项、解析状态(到达事件决定、有数据而无事件按成功)的各种组合。
/// 结算载荷由联网层的真解析器从手拼字节解出;<see cref="OnlineDungeonCombatResult"/> 的构造函数是 internal,
/// 用真状态机(<see cref="CombatRig"/>)喂帧得到真实事件序列后,以反射造结果对象(与本程序集 <c>NetObjects</c> 同一做法);
/// "有数据而无到达事件""首个事件就是到达事件"这类真状态机产不出的形状,用反射改结果对象的只读属性造出来。
/// </summary>
public sealed class OnlineDungeonAdapterGapMapperTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ------------------------------------------------------------------------------------------
    // 合成载荷(手拼,取值互不相同;不含真实编号)
    // ------------------------------------------------------------------------------------------

    /// <summary>通关结果载荷:u8 评分、i32 用时、u8 用时分、u8 基础分、u8 迷宫标志、u8 条数、条数 × { u16 唯一号、i32 最佳用时、u8 新纪录 }。</summary>
    private static byte[] ResultBytes(byte rank, int clearTime, sbyte timeBonus, sbyte baseScore, byte questMaze,
        params (ushort UniqueId, int BestTime, bool IsNew)[] entries)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder()
            .U8(rank)
            .I32(clearTime)
            .U8(unchecked((byte)timeBonus))
            .U8(unchecked((byte)baseScore))
            .U8(questMaze)
            .U8(entries.Length);
        foreach ((ushort uniqueId, int bestTime, bool isNew) in entries)
        {
            b.U16(uniqueId).I32(bestTime).U8(isNew ? 1 : 0);
        }
        return b.ToArray();
    }

    /// <summary>
    /// 通关奖励载荷:经验 16 项(第 k 项 = 31000 + 137 × k,第 4 项前夹一个 u8 活动类型 2)、任务物品、4 个奖励槽、接收者值 99001、
    /// 4 个保留字节 11 22 33 44、4 个特权奖励槽、比例 87654、三个标志字节。
    /// </summary>
    private static byte[] RewardBytes(byte returnUser, byte eventCharac, byte deadPenalty,
        params (ushort Slot, int ItemId, int Data, ushort Durability, byte AmplifyType, ushort AmplifyValue)[] items)
    {
        var b = new NoticePayloadBuilder();
        for (int k = 0; k < 4; k++)
        {
            b.I32(31000 + 137 * k);
        }
        b.U8(2); // 活动类型
        for (int k = 4; k < 16; k++)
        {
            b.I32(31000 + 137 * k);
        }
        b.U8(items.Length);
        foreach ((ushort slot, int itemId, int data, ushort durability, byte amplifyType, ushort amplifyValue) in items)
        {
            b.U16(slot).I32(itemId).I32(data).U16(durability).U8(amplifyType).U16(amplifyValue);
        }
        b.U8(2).I32(111).I32(222).I32(333).I32(444); // 奖励槽 0:带物品
        b.U8(1).I32(112).I32(223); // 奖励槽 1:无物品
        b.U8(0); // 奖励槽 2:无人
        b.U8(2).I32(114).I32(225).I32(336).I32(447); // 奖励槽 3:带物品
        b.I32(99001);
        b.U8(0x11).U8(0x22).U8(0x33).U8(0x44);
        b.U8(0); // 特权槽 0:无
        b.U8(1).I32(121).I32(232); // 特权槽 1:无物品
        b.U8(2).I32(122).I32(233).I32(343).I32(454); // 特权槽 2:带物品
        b.U8(1).I32(124).I32(235); // 特权槽 3:无物品
        b.I32(87654);
        b.U8(returnUser).U8(eventCharac).U8(deadPenalty);
        return b.ToArray();
    }

    private static byte[] FullResultBytes() =>
        ResultBytes(201, 123456, -13, 57, 1, (9301, 41000, true), (9302, 42500, false), (9303, 40100, true));

    private static byte[] FullRewardBytes() =>
        RewardBytes(1, 0, 1,
            (11, 50001, 60001, 701, 3, 801),
            (12, 50002, 60002, 702, 4, 802));

    // ------------------------------------------------------------------------------------------
    // 造结果对象
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 用真状态机走到结算阶段:一只怪、击杀并确认、清图、发结算命令,再按给定载荷依次喂 0x22 与 0x23
    /// (传 null 表示这一段不喂)。返回用反射造的联网层结果对象。
    /// </summary>
    private static async Task<OnlineDungeonCombatResult> SettledRunAsync(byte[] resultPayload, byte[] rewardPayload)
    {
        var rig = new CombatRig(1);
        await rig.FightToClear();
        await rig.Settle();
        if (resultPayload != null)
        {
            rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeAParser.PacketId, resultPayload));
        }
        if (rewardPayload != null)
        {
            rig.Feed(new CombatKit.ServerFrame(CombatKit.Noti, SettlementNoticeBParser.PacketId, rewardPayload));
        }
        return ResultOf(rig.Machine);
    }

    /// <summary>联网层结果对象的构造函数是 internal:反射构造,只喂状态机与零计数。</summary>
    private static OnlineDungeonCombatResult ResultOf(OnlineDungeonCombat machine)
    {
        object made = Activator.CreateInstance(
            typeof(OnlineDungeonCombatResult), Any, null,
            new object[] { OnlineDungeonCombatOutcome.ReturnReady, machine, 0, 0, 0, 0, 0, null }, null);
        Assert.NotNull(made);
        return (OnlineDungeonCombatResult)made;
    }

    /// <summary>造一个只带事件列表与两份结算记录的结果对象(绕开状态机,用来表达真状态机产不出的形状)。</summary>
    private static OnlineDungeonCombatResult Fabricate(IReadOnlyList<OnlineCombatEvent> events, SettlementNoticeA a, SettlementNoticeB b)
    {
        var result = (OnlineDungeonCombatResult)RuntimeHelpers.GetUninitializedObject(typeof(OnlineDungeonCombatResult));
        SetBacking(result, "Events", events);
        SetBacking(result, "SettlementResult", a);
        SetBacking(result, "SettlementReward", b);
        return result;
    }

    private static void SetBacking(OnlineDungeonCombatResult target, string property, object value)
    {
        FieldInfo field = typeof(OnlineDungeonCombatResult).GetField("<" + property + ">k__BackingField", Any);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static OnlineCombatEvent Arrived(OnlineDungeonCombatResult run, OnlineCombatSettlementPart part)
    {
        return run.Events.Single(e => e.Kind == OnlineCombatEventKind.SettlementArrived && e.Part == part);
    }

    private static OnlineDungeonSettlementData MapAll(OnlineDungeonCombatResult combat)
    {
        return OnlineDungeonSettlementMapper.Map(9511, "dungeon/test/gapmap.dgn", 3, 17, 5, 4, combat);
    }

    // ------------------------------------------------------------------------------------------
    // 逐字段映射
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:映射器把调用方给的六个头部字段原样放进结算数据,把通关结果与通关奖励里的每个字段都拷贝到对应的中性字段,
    /// 不换位、不换源、不取反、不少拷一项(三条最佳用时、两件任务物品、四个奖励槽、四个特权槽、16 项经验、4 个保留字节)。
    /// </summary>
    [Fact]
    public async Task MapCopiesEveryHeaderResultAndRewardFieldFromASettledRun()
    {
        OnlineDungeonCombatResult run = await SettledRunAsync(FullResultBytes(), FullRewardBytes());

        OnlineDungeonSettlementData data = MapAll(run);

        // 调用方给的头部字段:各不相同
        Assert.Equal(9511, data.DungeonId);
        Assert.Equal("dungeon/test/gapmap.dgn", data.DungeonPath);
        Assert.Equal(3, data.Difficulty);
        Assert.Equal(17, data.ReportedKills);
        Assert.Equal(5, data.DroppedKills);
        Assert.Equal(4, data.RoomsEntered);
        Assert.Equal(OnlineSettlementParseState.Succeeded, data.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Succeeded, data.RewardParseState);

        // 通关结果:评分 201、用时 123456、用时分 -13(负数按有符号字节)、基础分 57、迷宫标志 1
        OnlineSettlementResultData result = data.Result;
        Assert.NotNull(result);
        Assert.Equal((byte)201, result.RankValue);
        Assert.Equal(123456, result.ClearTime);
        Assert.Equal((sbyte)-13, result.TimeBonus);
        Assert.Equal((sbyte)57, result.BaseScore);
        Assert.Equal((byte)1, result.QuestMazeFlag);
        Assert.True(result.IsQuestMaze);
        Assert.True(result.HasBestTimeBlock);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal((ushort)9301, result.Entries[0].UniqueId);
        Assert.Equal(41000, result.Entries[0].BestTime);
        Assert.True(result.Entries[0].IsNewRecord);
        Assert.Equal((ushort)9302, result.Entries[1].UniqueId);
        Assert.Equal(42500, result.Entries[1].BestTime);
        Assert.False(result.Entries[1].IsNewRecord);
        Assert.Equal((ushort)9303, result.Entries[2].UniqueId);
        Assert.Equal(40100, result.Entries[2].BestTime);
        Assert.True(result.Entries[2].IsNewRecord);

        // 通关奖励:经验 16 项(31000 + 137 × k)
        OnlineSettlementRewardData reward = data.Reward;
        Assert.NotNull(reward);
        Assert.Equal(
            new[]
            {
                31000, 31137, 31274, 31411, 31548, 31685, 31822, 31959,
                32096, 32233, 32370, 32507, 32644, 32781, 32918, 33055,
            },
            reward.ExpValues.ToArray());
        Assert.Equal(31000, reward.BaseExp);
        Assert.Equal(31137, reward.RankBonusExp);
        Assert.Equal((byte)2, reward.ExpKind);

        // 任务物品两件,字段两两不同(耐久 ≠ 增幅值,槽位 ≠ 编号 ≠ 附加信息)
        Assert.Equal(2, reward.QuestItems.Count);
        Assert.Equal((ushort)11, reward.QuestItems[0].Slot);
        Assert.Equal(50001, reward.QuestItems[0].ItemId);
        Assert.Equal(60001, reward.QuestItems[0].Data);
        Assert.Equal((ushort)701, reward.QuestItems[0].Durability);
        Assert.Equal((byte)3, reward.QuestItems[0].AmplifyType);
        Assert.Equal((ushort)801, reward.QuestItems[0].AmplifyValue);
        Assert.Equal((ushort)12, reward.QuestItems[1].Slot);
        Assert.Equal(50002, reward.QuestItems[1].ItemId);
        Assert.Equal(60002, reward.QuestItems[1].Data);
        Assert.Equal((ushort)702, reward.QuestItems[1].Durability);
        Assert.Equal((byte)4, reward.QuestItems[1].AmplifyType);
        Assert.Equal((ushort)802, reward.QuestItems[1].AmplifyValue);

        // 四个奖励槽
        Assert.Equal(4, reward.Rewards.Count);
        AssertSlot(reward.Rewards[0], status: 2, reserved: 111, value: 222, itemId: 333, itemData: 444, hasItem: true);
        AssertSlot(reward.Rewards[1], status: 1, reserved: 112, value: 223, itemId: 0, itemData: 0, hasItem: false);
        AssertSlot(reward.Rewards[2], status: 0, reserved: 0, value: 0, itemId: 0, itemData: 0, hasItem: false);
        AssertSlot(reward.Rewards[3], status: 2, reserved: 114, value: 225, itemId: 336, itemData: 447, hasItem: true);

        // 接收者值、保留字节、特权槽、比例、标志(返回用户 = 真、活动角色 = 假、死亡惩罚 = 真)
        Assert.Equal(99001, reward.ReceiverValue);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, reward.ReservedBytes.ToArray());
        Assert.Equal(4, reward.PremiumRewards.Count);
        AssertSlot(reward.PremiumRewards[0], status: 0, reserved: 0, value: 0, itemId: 0, itemData: 0, hasItem: false);
        AssertSlot(reward.PremiumRewards[1], status: 1, reserved: 121, value: 232, itemId: 0, itemData: 0, hasItem: false);
        AssertSlot(reward.PremiumRewards[2], status: 2, reserved: 122, value: 233, itemId: 343, itemData: 454, hasItem: true);
        AssertSlot(reward.PremiumRewards[3], status: 1, reserved: 124, value: 235, itemId: 0, itemData: 0, hasItem: false);
        Assert.Equal(87654, reward.Percent);
        Assert.True(reward.IsReturnUserParty);
        Assert.False(reward.IsEventCharacParty);
        Assert.True(reward.HasDeadUserPenalty);
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

    /// <summary>
    /// 保护:三个标志按各自来源映射(这一份用相反的取值:返回用户 = 假、活动角色 = 真、死亡惩罚 = 假;
    /// 线上字节非 0 即真,这里活动角色用 7),迷宫标志 0 → 非迷宫,以及 7 字节全零形状的结果映射成"没有最佳时间块"。
    /// </summary>
    [Fact]
    public async Task MapKeepsFlagsApartAndMapsTimeoutShapeWithoutBestTimeBlock()
    {
        byte[] zeroShape = new byte[] { 0, 0, 0, 0, 0, 0, 0 };
        OnlineDungeonCombatResult run = await SettledRunAsync(zeroShape, RewardBytes(0, 7, 0));

        OnlineDungeonSettlementData data = MapAll(run);

        Assert.Equal(OnlineSettlementParseState.Succeeded, data.ResultParseState);
        Assert.NotNull(data.Result);
        Assert.False(data.Result.HasBestTimeBlock); // 全零形状:没有最佳时间块
        Assert.Equal((byte)0, data.Result.RankValue);
        Assert.Equal(0, data.Result.ClearTime);
        Assert.Equal((byte)0, data.Result.QuestMazeFlag);
        Assert.False(data.Result.IsQuestMaze);
        Assert.Same(Array.Empty<OnlineSettlementBestTime>(), data.Result.Entries);

        Assert.Equal(OnlineSettlementParseState.Succeeded, data.RewardParseState);
        Assert.NotNull(data.Reward);
        Assert.False(data.Reward.IsReturnUserParty);
        Assert.True(data.Reward.IsEventCharacParty);
        Assert.False(data.Reward.HasDeadUserPenalty);
        Assert.Same(Array.Empty<OnlineSettlementItemData>(), data.Reward.QuestItems); // 任务物品 0 件:复用共享空数组
    }

    /// <summary>
    /// 保护:最佳用时表只有一项时这一项被完整保留(不被当成空表丢掉、不漏掉第 0 项);
    /// 表为空(条数 0)时复用共享的空数组而不是新分配一个;迷宫标志为 2 时原样保存但"是迷宫"为假(只有 1 才算)。
    /// </summary>
    [Fact]
    public async Task MapKeepsSingleBestTimeEntryAndSharesEmptyEntryArray()
    {
        OnlineDungeonCombatResult single = await SettledRunAsync(
            ResultBytes(88, 7777, 21, -4, 2, (9411, 33333, true)),
            RewardBytes(0, 0, 0, (21, 70001, 80001, 901, 5, 1001)));

        OnlineDungeonSettlementData oneEntry = MapAll(single);

        Assert.NotNull(oneEntry.Result);
        Assert.Single(oneEntry.Result.Entries);
        Assert.Equal((ushort)9411, oneEntry.Result.Entries[0].UniqueId);
        Assert.Equal(33333, oneEntry.Result.Entries[0].BestTime);
        Assert.True(oneEntry.Result.Entries[0].IsNewRecord);
        Assert.Equal((sbyte)21, oneEntry.Result.TimeBonus);
        Assert.Equal((sbyte)-4, oneEntry.Result.BaseScore);
        Assert.Equal((byte)2, oneEntry.Result.QuestMazeFlag);
        Assert.False(oneEntry.Result.IsQuestMaze);
        Assert.NotNull(oneEntry.Reward);
        Assert.Single(oneEntry.Reward.QuestItems);
        Assert.Equal((ushort)21, oneEntry.Reward.QuestItems[0].Slot);
        Assert.Equal(70001, oneEntry.Reward.QuestItems[0].ItemId);
        Assert.Equal(80001, oneEntry.Reward.QuestItems[0].Data);
        Assert.Equal((ushort)901, oneEntry.Reward.QuestItems[0].Durability);
        Assert.Equal((byte)5, oneEntry.Reward.QuestItems[0].AmplifyType);
        Assert.Equal((ushort)1001, oneEntry.Reward.QuestItems[0].AmplifyValue);

        OnlineDungeonCombatResult none = await SettledRunAsync(ResultBytes(88, 7777, 21, -4, 0), RewardBytes(0, 0, 0));
        OnlineDungeonSettlementData noEntry = MapAll(none);
        Assert.NotNull(noEntry.Result);
        Assert.True(noEntry.Result.HasBestTimeBlock); // 正常形状但条数为 0
        Assert.Same(Array.Empty<OnlineSettlementBestTime>(), noEntry.Result.Entries);
    }

    // ------------------------------------------------------------------------------------------
    // 解析状态:到达事件决定,有数据而无事件按成功
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:没有任何结算事件、也没有结算数据时,两段解析状态都是 NotApplicable、数据为 null
    /// (combat 为 null、状态机刚建好、只到了结果没到奖励三种情形);只到结果时奖励那段仍是 NotApplicable。
    /// </summary>
    [Fact]
    public async Task MapWithoutSettlementDataLeavesParseStatesNotApplicable()
    {
        OnlineDungeonSettlementData fromNull = MapAll(null);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, fromNull.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, fromNull.RewardParseState);
        Assert.Null(fromNull.Result);
        Assert.Null(fromNull.Reward);
        Assert.Equal(9511, fromNull.DungeonId);

        var rig = new CombatRig(1);
        OnlineDungeonSettlementData fresh = MapAll(ResultOf(rig.Machine));
        Assert.Equal(OnlineSettlementParseState.NotApplicable, fresh.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, fresh.RewardParseState);
        Assert.Null(fresh.Result);
        Assert.Null(fresh.Reward);

        OnlineDungeonCombatResult onlyResult = await SettledRunAsync(FullResultBytes(), null);
        OnlineDungeonSettlementData half = MapAll(onlyResult);
        Assert.Equal(OnlineSettlementParseState.Succeeded, half.ResultParseState);
        Assert.NotNull(half.Result);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, half.RewardParseState);
        Assert.Null(half.Reward);
    }

    /// <summary>
    /// 保护:载荷读不通时对应段是 Failed 且数据为 null(两段各自独立:只坏一段时另一段照常成功)。
    /// </summary>
    [Fact]
    public async Task MapReportsFailedStatePerSegmentWhenPayloadIsMalformed()
    {
        OnlineDungeonCombatResult bothBad = await SettledRunAsync(new byte[] { 1, 2 }, new byte[] { 3, 4 });
        OnlineDungeonSettlementData bad = MapAll(bothBad);
        Assert.Equal(OnlineSettlementParseState.Failed, bad.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Failed, bad.RewardParseState);
        Assert.Null(bad.Result);
        Assert.Null(bad.Reward);

        OnlineDungeonCombatResult rewardBad = await SettledRunAsync(FullResultBytes(), new byte[] { 3, 4 });
        OnlineDungeonSettlementData mixed = MapAll(rewardBad);
        Assert.Equal(OnlineSettlementParseState.Succeeded, mixed.ResultParseState);
        Assert.NotNull(mixed.Result);
        Assert.Equal(OnlineSettlementParseState.Failed, mixed.RewardParseState);
        Assert.Null(mixed.Reward);

        OnlineDungeonCombatResult resultBad = await SettledRunAsync(new byte[] { 1, 2 }, FullRewardBytes());
        OnlineDungeonSettlementData mixedOther = MapAll(resultBad);
        Assert.Equal(OnlineSettlementParseState.Failed, mixedOther.ResultParseState);
        Assert.Null(mixedOther.Result);
        Assert.Equal(OnlineSettlementParseState.Succeeded, mixedOther.RewardParseState);
        Assert.NotNull(mixedOther.Reward);
    }

    /// <summary>
    /// 保护:"有数据而没有到达事件"按成功处理(隐式成功),"到达事件说失败"则以事件为准、不被隐式成功覆盖;
    /// 事件列表里的第 0 项同样参与判断(不跳过),列表里的空元素与空列表被安全跳过。
    /// 这些形状真状态机产不出来(事件第 0 项恒是"怪已生成"),用反射改结果对象的只读属性造出来。
    /// </summary>
    [Fact]
    public async Task MapParseStateFollowsArrivalEventsAndImpliesSuccessForDataWithoutEvents()
    {
        OnlineDungeonCombatResult ok = await SettledRunAsync(FullResultBytes(), FullRewardBytes());
        OnlineDungeonCombatResult bad = await SettledRunAsync(new byte[] { 1, 2 }, new byte[] { 3, 4 });
        Assert.True(SettlementNoticeAParser.TryParse(FullResultBytes(), out SettlementNoticeA noticeA));
        Assert.True(SettlementNoticeBParser.TryParse(FullRewardBytes(), out SettlementNoticeB noticeB));
        OnlineCombatEvent spawned = ok.Events[0];
        Assert.Equal(OnlineCombatEventKind.MonstersSpawned, spawned.Kind);
        OnlineCombatEvent resultFailed = Arrived(bad, OnlineCombatSettlementPart.Result);
        OnlineCombatEvent rewardFailed = Arrived(bad, OnlineCombatSettlementPart.Reward);
        Assert.Equal(OnlineCombatSettlementParseStatus.Failed, resultFailed.SettlementParseStatus);
        Assert.Equal(OnlineCombatSettlementParseStatus.Failed, rewardFailed.SettlementParseStatus);

        // 有数据、没有到达事件:隐式成功
        OnlineDungeonSettlementData implied = MapAll(Fabricate(new[] { spawned }, noticeA, noticeB));
        Assert.Equal(OnlineSettlementParseState.Succeeded, implied.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Succeeded, implied.RewardParseState);
        Assert.NotNull(implied.Result);
        Assert.NotNull(implied.Reward);

        // 只有结果数据、没有奖励数据:只有结果段隐式成功,奖励段保持 NotApplicable
        OnlineDungeonSettlementData impliedResultOnly = MapAll(Fabricate(new[] { spawned }, noticeA, null));
        Assert.Equal(OnlineSettlementParseState.Succeeded, impliedResultOnly.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, impliedResultOnly.RewardParseState);
        Assert.Null(impliedResultOnly.Reward);

        // 只有奖励数据:只有奖励段隐式成功
        OnlineDungeonSettlementData impliedRewardOnly = MapAll(Fabricate(new[] { spawned }, null, noticeB));
        Assert.Equal(OnlineSettlementParseState.NotApplicable, impliedRewardOnly.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Succeeded, impliedRewardOnly.RewardParseState);

        // 有数据、但到达事件明说失败:以事件为准(不被隐式成功覆盖)
        OnlineDungeonSettlementData eventWins = MapAll(Fabricate(new[] { spawned, resultFailed, rewardFailed }, noticeA, noticeB));
        Assert.Equal(OnlineSettlementParseState.Failed, eventWins.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Failed, eventWins.RewardParseState);
        Assert.NotNull(eventWins.Result); // 数据照常映射,状态只看事件
        Assert.NotNull(eventWins.Reward);

        // 第 0 项就是到达事件:不能被跳过
        OnlineDungeonSettlementData firstResult = MapAll(Fabricate(new[] { resultFailed, spawned }, null, null));
        Assert.Equal(OnlineSettlementParseState.Failed, firstResult.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, firstResult.RewardParseState);
        OnlineDungeonSettlementData firstReward = MapAll(Fabricate(new[] { rewardFailed, spawned }, null, null));
        Assert.Equal(OnlineSettlementParseState.NotApplicable, firstReward.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.Failed, firstReward.RewardParseState);

        // 空元素被跳过、事件列表为空列表或 null 都不抛
        OnlineDungeonSettlementData withNull = MapAll(Fabricate(new OnlineCombatEvent[] { null, resultFailed }, null, null));
        Assert.Equal(OnlineSettlementParseState.Failed, withNull.ResultParseState);
        OnlineDungeonSettlementData emptyEvents = MapAll(Fabricate(Array.Empty<OnlineCombatEvent>(), null, null));
        Assert.Equal(OnlineSettlementParseState.NotApplicable, emptyEvents.ResultParseState);
        OnlineDungeonSettlementData nullEvents = MapAll(Fabricate(null, noticeA, null));
        Assert.Equal(OnlineSettlementParseState.Succeeded, nullEvents.ResultParseState);
        Assert.Equal(OnlineSettlementParseState.NotApplicable, nullEvents.RewardParseState);
    }
}
