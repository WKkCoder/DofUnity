using System.Collections;
using System.Reflection;
using Kit = Dnf.Play.Tests.OnlineSettlementGapBKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算摘要构造器 <see cref="OnlineSettlementSummaryBuilder"/> 里私有的"选取奖励槽位"(<c>TrySelectRewardSlot</c>)在槽位表为 null 与为空时的补测(区域 B 第七轮)。
/// 公开路径上槽位表到不了 null(<see cref="OnlineSettlementRewardData"/> 的构造器把 null 规整成空表),所以:
/// 一组用反射直接调私有方法(槽位表 null / 空表 × 队伍位置有值与无值),只看返回值、输出参数和对槽位表的读取;
/// 另一组用反射把载荷的 <c>Rewards</c> 后备字段写成 null,再走公开的 <see cref="OnlineSettlementSummaryBuilder.Build"/>,看整条路径上不抛异常、没有奖励金币。
/// 数值全是合成的。
/// </summary>
public sealed class OnlineSettlementGapB7RewardSlotNullTests
{
    /// <summary>调用前塞进输出参数的"脏值"(非默认):用来证明函数提前返回时把输出参数明确置成默认值,而不是保留调用方给的旧值。</summary>
    private static readonly OnlineSettlementRewardSlotData Dirty = Kit.Slot(2, 777, itemId: 31, itemData: 5);

    /// <summary>槽位五个字段的元组:结构体没有重载相等,断言时按字段比。</summary>
    private static (byte Status, int Reserved, int Value, int ItemId, int ItemData) Fields(OnlineSettlementRewardSlotData slot)
    {
        return (slot.Status, slot.Reserved, slot.Value, slot.ItemId, slot.ItemData);
    }

    private static readonly (byte, int, int, int, int) DefaultFields = (0, 0, 0, 0, 0);

    /// <summary>反射直调私有静态方法;方法内抛出的异常原样带回(不让反射包一层 TargetInvocationException 吞掉类型)。</summary>
    private static (bool Result, OnlineSettlementRewardSlotData Selected, Exception Error) Invoke(
        IReadOnlyList<OnlineSettlementRewardSlotData> slots,
        int? partySlot)
    {
        MethodInfo method = typeof(OnlineSettlementSummaryBuilder).GetMethod("TrySelectRewardSlot", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object[] args = { slots, partySlot, Dirty };
        try
        {
            bool result = (bool)method.Invoke(null, args);
            return (result, (OnlineSettlementRewardSlotData)args[2], null);
        }
        catch (TargetInvocationException e)
        {
            return (false, default(OnlineSettlementRewardSlotData), e.InnerException);
        }
    }

    /// <summary>只实现 <see cref="IReadOnlyList{T}"/> 的探针空表:记录 Count、下标与枚举器各被用了几次(自己的一份,不动既有文件里的探针)。</summary>
    private sealed class SpySlots : IReadOnlyList<OnlineSettlementRewardSlotData>
    {
        private readonly OnlineSettlementRewardSlotData[] _items;

        public SpySlots(OnlineSettlementRewardSlotData[] items)
        {
            _items = items;
        }

        public int CountReads { get; private set; }

        public int IndexReads { get; private set; }

        public int Enumerations { get; private set; }

        public int Count
        {
            get
            {
                CountReads++;
                return _items.Length;
            }
        }

        public OnlineSettlementRewardSlotData this[int index]
        {
            get
            {
                IndexReads++;
                return _items[index];
            }
        }

        public IEnumerator<OnlineSettlementRewardSlotData> GetEnumerator()
        {
            Enumerations++;
            return ((IEnumerable<OnlineSettlementRewardSlotData>)_items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // 保护的行为:槽位表为 null 时,选取函数直接返回 false、输出参数是默认值(五个字段全 0),不抛异常——不管队伍位置有没有值、取什么值。
    // 期望值手算:null 表没有任何槽位可选 → 返回 false;输出参数被置成 default(调用前塞的脏值 状态 2 / 金额 777 不能留下来)。
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(-1)]
    public void NullSlotListSelectsNothingAndDoesNotThrow(int? partySlot)
    {
        (bool result, OnlineSettlementRewardSlotData selected, Exception error) = Invoke(null, partySlot);

        Assert.Null(error);
        Assert.False(result);
        Assert.Equal(DefaultFields, Fields(selected));
    }

    // 保护的行为:槽位表为空表时同样返回 false、输出参数是默认值;而且函数在开头那一次"表是否为空"的判断后就返回——
    // 只读一次 Count,不读任何下标,不枚举(没有碰输出参数以外的任何东西)。
    // 期望值手算:空表 Count = 0 → 开头判断读 1 次 Count 即返回;若开头判断失效,队伍位置 ≥ 0 时会再多读几次 Count(位置比较一次、扫描循环一次)。
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void EmptySlotListSelectsNothingAndReadsOnlyTheCountOnce(int? partySlot)
    {
        var spy = new SpySlots(Array.Empty<OnlineSettlementRewardSlotData>());

        (bool result, OnlineSettlementRewardSlotData selected, Exception error) = Invoke(spy, partySlot);

        Assert.Null(error);
        Assert.False(result);
        Assert.Equal(DefaultFields, Fields(selected));
        Assert.Equal(1, spy.CountReads);
        Assert.Equal(0, spy.IndexReads);
        Assert.Equal(0, spy.Enumerations);
    }

    // 保护的行为:常见的两种空表实现(空数组、空 List)与 null 一样返回 false、输出参数为默认值,队伍位置有值与无值都一样。
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3)]
    public void PlainEmptyListsSelectNothing(int? partySlot)
    {
        foreach (IReadOnlyList<OnlineSettlementRewardSlotData> empty in new IReadOnlyList<OnlineSettlementRewardSlotData>[]
        {
            Array.Empty<OnlineSettlementRewardSlotData>(),
            new List<OnlineSettlementRewardSlotData>(),
        })
        {
            (bool result, OnlineSettlementRewardSlotData selected, Exception error) = Invoke(empty, partySlot);

            Assert.Null(error);
            Assert.False(result);
            Assert.Equal(DefaultFields, Fields(selected));
        }
    }

    // 保护的行为(对照):同一套反射调用在槽位表有内容时能取到输出参数——证明上面"输出参数是默认值"的断言不是反射调用读不到输出参数的假象。
    // 期望值手算:槽位 0 状态 0(无权)、槽位 1 状态 1(金币,金额 200)、槽位 2 状态 2(物品,金额 300)。
    //   队伍位置 null → 扫描到第一个有权的槽位 1 → (1, 200);队伍位置 2 → 槽位 2 有权直接取 → (2, 300)。
    [Fact]
    public void ControlNonEmptyListStillSelectsThroughTheSameReflectionCall()
    {
        OnlineSettlementRewardSlotData[] slots = { Kit.Slot(0, 100), Kit.Slot(1, 200), Kit.Slot(2, 300) };

        (bool scanned, OnlineSettlementRewardSlotData first, Exception scanError) = Invoke(slots, null);
        (bool direct, OnlineSettlementRewardSlotData party, Exception directError) = Invoke(slots, 2);

        Assert.Null(scanError);
        Assert.True(scanned);
        Assert.Equal(((byte)1, 0, 200, 0, 0), Fields(first));
        Assert.Null(directError);
        Assert.True(direct);
        Assert.Equal(((byte)2, 0, 300, 0, 0), Fields(party));
    }

    // 保护的行为:整条公开路径(Build)在奖励载荷的槽位表缺失(null)时不抛异常——没有奖励金币(无值、来源 None)、
    // 即使本地金币有值也不记 reward_gold 差异、槽位掉落循环被跳过(掉落里只剩任务物品那一件);队伍位置有值与无值一样。
    // 做法:载荷构造器会把 null 规整成空表,所以先正常构造,再用反射把自动属性 Rewards 的后备字段写成 null。
    // 期望值手算:任务物品 1 件(物品号 31、数量 2)→ 掉落 1 条;槽位表缺失 → 没有金币槽 → RewardGold 缺失;本地金币 123 无从对账 → 无差异。
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void BuildWithAMissingRewardSlotListHasNoRewardGoldAndDoesNotThrow(int? partySlot)
    {
        OnlineSettlementRewardData reward = Kit.Reward(
            questItems: new[] { Kit.Quest(31, 2) },
            rewards: new[] { Kit.Slot(1, 100), Kit.Slot(2, 200, itemId: 32, itemData: 3) });
        FieldInfo backing = typeof(OnlineSettlementRewardData).GetField("<Rewards>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(backing);
        backing.SetValue(reward, null);
        Assert.Null(reward.Rewards);
        var local = new OnlineSettlementLocalContext { PartySlot = partySlot, LocalGoldEarned = 123 };

        OnlineSettlementSummary summary = null;
        Exception error = Record.Exception(() => summary = OnlineSettlementSummaryBuilder.Build(Kit.Data(reward), local));

        Assert.Null(error);
        Assert.NotNull(summary);
        Assert.False(summary.RewardGold.HasValue);
        Assert.False(summary.RewardGold.ShouldDisplay);
        Assert.Equal(SettlementFieldSource.None, summary.RewardGold.Source);
        Assert.DoesNotContain("reward_gold", summary.DiffFields);
        Assert.Single(summary.Drops);
        Assert.Equal((31, 2, true), (summary.Drops[0].ItemId, summary.Drops[0].Count, summary.Drops[0].IsQuestItem));
    }
}
