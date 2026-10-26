using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 通关经验(证据 E-124):队伍总经验、每位队员的一份、评分加成的运算顺序与落地精度(80 位寄存器、单精度落地、截断、低 32 位)。
/// 数值全是合成的;期望值由一个独立的 x87 小模型(精确分数运算,每步舍入到 64 位有效数字,落单精度时舍入到 24 位)算出。
/// </summary>
public class ClearRewardTests
{
    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    private static ScriptLine Line(string tag, params ScriptValue[] values) => new ScriptLine(tag, values);

    private static ExpRates Rates()
    {
        return new ExpRates(new[] { 1f, 1.5f, 2f, 2.5f }, new[] { 1f, 2f, 3f, 4f, 5f }, new[] { 1f, 1.5f, 2f, 3f }, new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f });
    }

    private static MobRewardTable Rewards()
    {
        return MobRewardTable.FromRows(new[]
        {
            new KeyValuePair<int, long>(40, 1000),
            new KeyValuePair<int, long>(41, 997),
            new KeyValuePair<int, long>(60, 4_000_000_000L),
            new KeyValuePair<int, long>(70, 11184813),
        });
    }

    [Fact]
    public void DungeonTotalIsKillsTimesBaseOverTwoTimesPartyDifficultyAndWeight()
    {
        MobRewardTable rewards = Rewards();
        ExpRates rates = Rates();
        Assert.Equal(15000u, ClearReward.DungeonTotal(rewards, rates, 40, 30, 1, 0, 1f));           // 30 × 1000 / 2
        Assert.Equal(22500u, ClearReward.DungeonTotal(rewards, rates, 40, 30, 2, 0, 1f));           // 两人队 ×1.5
        Assert.Equal(67500u, ClearReward.DungeonTotal(rewards, rates, 40, 30, 1, 2, 1.5f));         // 难度 2(×3)、权重 1.5
        Assert.Equal(15000u, ClearReward.DungeonTotal(rewards, rates, 40, 30, 1, 9, 1f));           // 难度越界:×1
        Assert.Equal(0u, ClearReward.DungeonTotal(rewards, rates, 40, 0, 1, 0, 1f));                // 一只没杀
    }

    [Fact]
    public void DungeonTotalWithoutAnEntryForTheStandardLevelOrWithoutATableIsZero()
    {
        ExpRates rates = Rates();
        Assert.Equal(0u, ClearReward.DungeonTotal(Rewards(), rates, 55, 30, 1, 0, 1f));             // 表里没有 55 级
        Assert.Equal(0u, ClearReward.DungeonTotal(Rewards(), rates, 0, 30, 1, 0, 1f));              // 标准等级 0(没有 [basis level])
        Assert.Equal(0u, ClearReward.DungeonTotal(null, rates, 40, 30, 1, 0, 1f));
    }

    [Fact]
    public void TheStageBeforeTheDifficultyIsRoundedToSinglePrecision()
    {
        // 3 × 11184813 / 2 = 16777219.5:80 位里取整得 16777219,但服务端先把它落成单精度(最近的可表示数 16777220),所以是 16777220
        Assert.Equal(16777220u, ClearReward.DungeonTotal(Rewards(), ExpRates.Neutral, 70, 3, 1, 0, 1f));
    }

    [Fact]
    public void TheTotalWrapsAtThirtyTwoBitsLikeTheServersIntStore()
    {
        // 3 × 4000000000 / 2 = 6000000000(64 位取整后只存低 32 位)
        Assert.Equal(1705032704u, ClearReward.DungeonTotal(Rewards(), ExpRates.Neutral, 60, 3, 1, 0, 1f));
    }

    [Fact]
    public void MemberBaseAppliesTheLevelGapRateRoundsToSingleAndDividesByTheMembers()
    {
        Assert.Equal(15000u, ClearReward.MemberBase(15000, 40, 40, 1));
        Assert.Equal(11250u, ClearReward.MemberBase(22500, 40, 40, 2));
        Assert.Equal(75600u, ClearReward.MemberBase(67500, 38, 40, 1));          // 主角低 2 级:×1.12f
        Assert.Equal(750u, ClearReward.MemberBase(15000, 47, 40, 1));            // 主角高 7 级:×0.05f
        Assert.Equal(16777216u, ClearReward.MemberBase(16777217, 40, 40, 1));    // 总经验先落成单精度(16777217 不可表示)
        Assert.Equal(8388608u, ClearReward.MemberBase(16777217, 40, 40, 2));
        Assert.Equal(15000u, ClearReward.MemberBase(15000, 40, 40, 0));          // 队员数小于 1 当 1
    }

    [Fact]
    public void RankBonusTruncatesTheProductOfTheBaseAndTheSingleRate()
    {
        Assert.Equal(4500u, ClearReward.RankBonus(15000, 0.3f));                 // 0.3f 略大:4500.00018 → 4500
        Assert.Equal(13u, ClearReward.RankBonus(20, 0.7f));                      // 0.7f 略小:13.99999976 → 13
        Assert.Equal(1u, ClearReward.RankBonus(10, 0.1f));
        Assert.Equal(10u, ClearReward.RankBonus(100, 0.1f));
        Assert.Equal(1u, ClearReward.RankBonus(3, 0.5f));
        Assert.Equal(0u, ClearReward.RankBonus(12345, 0f));
        Assert.Equal(0u, ClearReward.RankBonus(0, 0.5f));
    }

    [Fact]
    public void ComputeChainsTotalBaseAndBonusAndTheRankIndexPicksTheRate()
    {
        MobRewardTable rewards = Rewards();
        ExpRates rates = Rates();
        ClearExp s = ClearReward.Compute(rewards, rates, 40, 40, 30, 0, 1f, 2);          // 档位 2:比例 0.3f
        Assert.Equal(15000u, s.DungeonTotal);
        Assert.Equal(15000u, s.Base);
        Assert.Equal(4500u, s.RankBonus);
        Assert.Equal(19500L, s.Total);

        ClearExp sss = ClearReward.Compute(rewards, rates, 40, 40, 30, 0, 1f, 4, members: 2);   // 两人队、档位 4:比例 0.5f
        Assert.Equal(22500u, sss.DungeonTotal);
        Assert.Equal(11250u, sss.Base);
        Assert.Equal(5625u, sss.RankBonus);
        Assert.Equal(16875L, sss.Total);

        ClearExp hard = ClearReward.Compute(rewards, rates, 40, 38, 30, 2, 1.5f, 1);            // 难度 2、权重 1.5、低 2 级、档位 1:比例 0.2f
        Assert.Equal(67500u, hard.DungeonTotal);
        Assert.Equal(75600u, hard.Base);
        Assert.Equal(15120u, hard.RankBonus);

        ClearExp over = ClearReward.Compute(rewards, rates, 40, 47, 30, 0, 1f, 3);              // 高 7 级:×0.05f;档位 3:比例 0.4f
        Assert.Equal(750u, over.Base);
        Assert.Equal(300u, over.RankBonus);
    }

    [Fact]
    public void NoRankIndexOrAnOutOfRangeOneGivesNoBonus()
    {
        MobRewardTable rewards = Rewards();
        ExpRates rates = Rates();
        ClearExp none = ClearReward.Compute(rewards, rates, 40, 40, 30, 0, 1f, -1);
        Assert.Equal(15000u, none.Base);
        Assert.Equal(0u, none.RankBonus);
        Assert.Equal(15000L, none.Total);
        Assert.Equal(0u, ClearReward.Compute(rewards, rates, 40, 40, 30, 0, 1f, 5).RankBonus);
    }

    [Fact]
    public void ComputeWithNothingIsZero()
    {
        ClearExp zero = ClearReward.Compute(null, null, 40, 40, 30, 0, 1f, 4);
        Assert.Equal(0L, zero.Total);
        Assert.Equal(0u, zero.DungeonTotal);
    }

    [Fact]
    public void ExpRatesParseTheRankTableAndRankIndexOutsideItIsZero()
    {
        ExpRates rates = ExpRates.Parse(new[] { Line("[clear rank exp bonusrate]", F(0.1f), F(0.2f), F(0.3f), F(0.4f), F(0.5f)) });
        Assert.Equal(0.1f, rates.RankBonusRate(0));
        Assert.Equal(0.5f, rates.RankBonusRate(4));
        Assert.Equal(0f, rates.RankBonusRate(-1));
        Assert.Equal(0f, rates.RankBonusRate(5));
        Assert.Equal(1f, rates.PartyRate(1));                      // 没写的三张表仍是中性
    }

    [Fact]
    public void MissingRankTableMeansNoBonusUnlikeTheOtherThreeTables()
    {
        Assert.Equal(0f, ExpRates.Neutral.RankBonusRate(4));
        Assert.Equal(0f, ExpRates.Parse(null).RankBonusRate(0));
        ExpRates partial = ExpRates.Parse(new[] { Line("[clear rank exp bonusrate]", F(0.1f), F(0.2f)) });
        Assert.Equal(0.2f, partial.RankBonusRate(1));
        Assert.Equal(0f, partial.RankBonusRate(2));                // 只写了两项,其余补 0
    }
}
