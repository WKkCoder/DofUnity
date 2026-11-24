using System.Collections;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算摘要构造器 <see cref="OnlineSettlementSummaryBuilder"/> 里"选取奖励槽位"(队伍位置优先、无权退回第一个有权槽位)的补测(区域 B 第五轮):
/// 队伍位置在 0 / 中间 / 最后一个 / 等于槽位数 / 更大 / 负数 / null 的每种取值上,选中的槽位(从 <see cref="OnlineSettlementSummary.RewardGold"/> 的数值读出)都要对;
/// 槽位全无权、只有一个槽位、空列表时没有奖励金币。槽位状态:0 = 无权,1 = 金币,2 = 物品(1、2 都算有权)。
/// 另用只实现 <see cref="IReadOnlyList{T}"/> 的探针列表钉住"队伍位置 0 与没有队伍位置不是同一条路径"(0 号槽位先被直接核对一次再扫描)。
/// 数值全是合成的(槽位金额 100、200、300……)。
/// </summary>
public sealed class OnlineSettlementGapB5RewardSlotTests
{
    /// <summary>"状态:金额" 逗号分隔的槽位表,如 "1:100,0:200"。</summary>
    private static OnlineSettlementRewardSlotData[] Slots(string spec)
    {
        if (spec.Length == 0)
        {
            return Array.Empty<OnlineSettlementRewardSlotData>();
        }
        return spec.Split(',').Select(part =>
        {
            string[] kv = part.Split(':');
            return OnlineSettlementGapBKit.Slot(byte.Parse(kv[0]), int.Parse(kv[1]));
        }).ToArray();
    }

    private static OnlineSettlementSummary Build(IReadOnlyList<OnlineSettlementRewardSlotData> slots, int? partySlot, long localGold = 0)
    {
        var reward = new OnlineSettlementRewardData(
            expValues: null,
            expKind: 0,
            questItems: null,
            rewards: slots,
            receiverValue: 0,
            reservedBytes: null,
            premiumRewards: null,
            percent: 0,
            isReturnUserParty: false,
            isEventCharacParty: false,
            hasDeadUserPenalty: false);
        var local = new OnlineSettlementLocalContext { PartySlot = partySlot, LocalGoldEarned = localGold };
        return OnlineSettlementSummaryBuilder.Build(OnlineSettlementGapBKit.Data(reward), local);
    }

    // 保护的行为:选取奖励槽位的规则——队伍位置有效(0 ≤ 位置 < 槽位数)且那个槽位有权就取它,否则退回扫描到的第一个有权槽位;全无权才没有奖励金币。
    // 期望值(第三列)是被选中槽位的金额,-1 表示没有奖励金币。逐行推导:
    //   "1:100,1:200" 位置 0 → 槽位 0 有权,取 100;位置 1 → 取 200;位置 2(= 槽位数,越界)、5、-1、null → 退回第一个有权的槽位 0 = 100。
    //   "0:100,1:200" 位置 0 → 槽位 0 无权,退回第一个有权的槽位 1 = 200;位置 null 同。
    //   "1:100,0:200,1:300" 位置 1 → 槽位 1 无权,退回第一个有权的槽位 0 = 100(不是下一个有权的 300);位置 2(最后一个)→ 300。
    //   "2:100,1:200" 位置 null → 物品槽(状态 2)也算有权,取第一个 = 100;"0:100,2:200" 位置 1 → 200。
    //   "1:100" 单槽位:位置 0、null、1(越界)都取 100;"0:100" 与 "0:100,0:200":无论位置怎样都没有;空列表也没有。
    [Theory]
    [InlineData("1:100,1:200", 0, 100)]
    [InlineData("1:100,1:200", 1, 200)]
    [InlineData("1:100,1:200", 2, 100)]
    [InlineData("1:100,1:200", 5, 100)]
    [InlineData("1:100,1:200", -1, 100)]
    [InlineData("1:100,1:200", null, 100)]
    [InlineData("0:100,1:200", 0, 200)]
    [InlineData("0:100,1:200", 1, 200)]
    [InlineData("0:100,1:200", null, 200)]
    [InlineData("1:100,0:200,1:300", 0, 100)]
    [InlineData("1:100,0:200,1:300", 1, 100)]
    [InlineData("1:100,0:200,1:300", 2, 300)]
    [InlineData("1:100,0:200,1:300", 3, 100)]
    [InlineData("1:100,1:200,1:300", 2, 300)]
    [InlineData("1:100,1:200,1:300", 1, 200)]
    [InlineData("2:100,1:200", null, 100)]
    [InlineData("2:100,1:200", 1, 200)]
    [InlineData("0:100,2:200", null, 200)]
    [InlineData("0:100,2:200", 1, 200)]
    [InlineData("1:100", 0, 100)]
    [InlineData("1:100", null, 100)]
    [InlineData("1:100", 1, 100)]
    [InlineData("1:100", -1, 100)]
    [InlineData("0:100", 0, -1)]
    [InlineData("0:100", null, -1)]
    [InlineData("0:100", 1, -1)]
    [InlineData("0:100,0:200", 0, -1)]
    [InlineData("0:100,0:200", 1, -1)]
    [InlineData("0:100,0:200", 2, -1)]
    [InlineData("0:100,0:200", -1, -1)]
    [InlineData("0:100,0:200", null, -1)]
    [InlineData("", 0, -1)]
    [InlineData("", null, -1)]
    public void RewardSlotIsThePartySlotWhenItHasRightsElseTheFirstSlotWithRights(string slots, int? partySlot, int expectedGold)
    {
        OnlineSettlementSummary summary = Build(Slots(slots), partySlot);

        if (expectedGold < 0)
        {
            Assert.False(summary.RewardGold.HasValue);
            Assert.False(summary.RewardGold.ShouldDisplay);
            Assert.Equal(SettlementFieldSource.None, summary.RewardGold.Source);
            return;
        }
        Assert.True(summary.RewardGold.HasValue);
        Assert.Equal(expectedGold, summary.RewardGold.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, summary.RewardGold.Confidence);
        Assert.Equal(SettlementFieldSource.Server, summary.RewardGold.Source);
    }

    // 保护的行为:没有本地对照上下文(local 为 null)时同样按"第一个有权槽位"取奖励金币,不抛异常。
    [Fact]
    public void RewardSlotFallsBackToTheFirstSlotWithRightsWhenThereIsNoLocalContext()
    {
        var reward = OnlineSettlementGapBKit.Reward(rewards: Slots("0:100,1:200,1:300"));
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(OnlineSettlementGapBKit.Data(reward));

        Assert.True(summary.RewardGold.HasValue);
        Assert.Equal(200, summary.RewardGold.Value);
    }

    // 保护的行为:队伍位置选中的槽位决定"奖励金币与本地不一致"的对账——位置 1 选中金额 200 的槽位,本地金币 200 → 没有 reward_gold 差异;
    // 位置 0 选中金额 100 的槽位,本地金币 200 → 有差异;本地金币为 0(没有本地数据)时永远不记差异;位置越界(退回槽位 0 = 100)时拿 100 对账。
    [Theory]
    [InlineData(1, 200L, false)]
    [InlineData(0, 200L, true)]
    [InlineData(0, 100L, false)]
    [InlineData(1, 0L, false)]
    [InlineData(1, 1L, true)]
    [InlineData(7, 100L, false)]
    [InlineData(7, 200L, true)]
    public void RewardGoldDiffIsJudgedAgainstTheSelectedSlotOnly(int partySlot, long localGold, bool expectDiff)
    {
        OnlineSettlementSummary summary = Build(Slots("1:100,1:200"), partySlot, localGold);

        Assert.Equal(expectDiff, summary.DiffFields.Contains("reward_gold"));
    }

    /// <summary>只实现 <see cref="IReadOnlyList{T}"/> 的探针槽位表:记录 Count 与每个下标被读了几次。</summary>
    private sealed class SpySlots : IReadOnlyList<OnlineSettlementRewardSlotData>
    {
        private readonly OnlineSettlementRewardSlotData[] _items;
        private readonly int[] _indexReads;

        public SpySlots(OnlineSettlementRewardSlotData[] items)
        {
            _items = items;
            _indexReads = new int[items.Length];
        }

        public int CountReads { get; private set; }

        public int IndexReads(int index) => _indexReads[index];

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
                _indexReads[index]++;
                return _items[index];
            }
        }

        public IEnumerator<OnlineSettlementRewardSlotData> GetEnumerator() => ((IEnumerable<OnlineSettlementRewardSlotData>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // 保护的行为:队伍位置 0 不是"没有队伍位置":它先把 0 号槽位直接核对一次(再比较一次位置与槽位数),核对不过才退回扫描。
    // 0 号槽位无权时两条路径选中的槽位相同(都是第一个有权的槽位 1),所以结果读不出差别,只能从槽位表被读取的次数上区分:
    // 同一张表 "0:100,1:200",队伍位置 0 比队伍位置 null 多读一次 Count(位置 < 槽位数的比较)、多读一次 0 号槽位(直接核对),其余下标的读取次数相同。
    [Fact]
    public void PartySlotZeroChecksTheZeroSlotDirectlyBeforeScanningUnlikeNoPartySlot()
    {
        var withNull = new SpySlots(Slots("0:100,1:200"));
        var withZero = new SpySlots(Slots("0:100,1:200"));

        OnlineSettlementSummary byNull = Build(withNull, null);
        OnlineSettlementSummary byZero = Build(withZero, 0);

        // 两条路径的结果相同:都选槽位 1(金额 200)
        Assert.Equal(200, byNull.RewardGold.Value);
        Assert.Equal(200, byZero.RewardGold.Value);
        // 队伍位置 0:多一次 Count、多一次 0 号槽位读取;1 号槽位的读取次数相同
        Assert.Equal(withNull.CountReads + 1, withZero.CountReads);
        Assert.Equal(withNull.IndexReads(0) + 1, withZero.IndexReads(0));
        Assert.Equal(withNull.IndexReads(1), withZero.IndexReads(1));
    }

    // 保护的行为:队伍位置有效且那个槽位有权时,直接取它、不再扫描——队伍位置 1(有权)只读 1 号槽位两次(核对 + 取值)再加摘要里掉落循环各读一次;
    // 与队伍位置 null(扫描到 0 号槽位)相比,0 号槽位少读一次(没被扫描),1 号槽位多读(核对 + 取值)。
    [Fact]
    public void ValidPartySlotWithRightsIsTakenDirectlyWithoutScanningFromTheFirstSlot()
    {
        var withNull = new SpySlots(Slots("1:100,1:200"));
        var withOne = new SpySlots(Slots("1:100,1:200"));

        OnlineSettlementSummary byNull = Build(withNull, null);
        OnlineSettlementSummary byOne = Build(withOne, 1);

        Assert.Equal(100, byNull.RewardGold.Value);
        Assert.Equal(200, byOne.RewardGold.Value);
        // 扫描(位置 null):0 号槽位读 2 次(核对状态 + 取值),1 号槽位 0 次;直接取(位置 1):0 号槽位 0 次,1 号槽位 2 次
        // 两者之外摘要的掉落循环对每个槽位各读 1 次(状态判断),所以下面的总数各加 1
        Assert.Equal(3, withNull.IndexReads(0));
        Assert.Equal(1, withNull.IndexReads(1));
        Assert.Equal(1, withOne.IndexReads(0));
        Assert.Equal(3, withOne.IndexReads(1));
    }
}
