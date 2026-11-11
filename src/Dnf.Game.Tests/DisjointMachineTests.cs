using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 分解机:开始前的耐久检查、整修、升档、每次分解后的结算。依据:服务端 OnStartMission 0x085d2a16、complete_disjoint 0x085d31a0、
/// OnRepairDisjointMachine 0x085d35ce、OnUpgradeDisjointMachine 0x085d37e8、GetDisjointMachineRepairCost 0x085d3c74、GetDisjointMachineRepair 0x085d3cc8(静态读码)。
/// 合成数据:整修表三档 (全价 10000、耐久上限 300)、(15000、300)、(20000、400);升档费用 30000(升到 1 档)、50000(升到 2 档);
/// 副职业等级与角色等级的要求见 <see cref="ExpertJobTestData.Etc"/>(键 2 → 10 级、键 3 → 20 级)。
/// </summary>
public class DisjointMachineTests
{
    private static readonly ExpertJobScript Script = Disjointer();
    private static readonly ExpertJobEtc EtcTable = Etc();

    // ------------------------------------------------------------------ 开始前

    [Theory]
    [InlineData(1, true)]
    [InlineData(300, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void TheMachineNeedsPositiveEnduranceToStart(int endurance, bool ok)
    {
        Assert.Equal(ok ? ExpertJobError.Ok : ExpertJobError.MachineBroken, DisjointMachine.CheckStart(endurance));
    }

    // ------------------------------------------------------------------ 整修全价

    [Theory]
    [InlineData(0, 0, 10000)]        // 耐久 0:全价
    [InlineData(0, 150, 5000)]       // 10000 − 10000 × 150 ÷ 300
    [InlineData(0, 299, 34)]         // 10000 − 2990000 ÷ 300(向零取整 9966)
    [InlineData(0, 300, 0)]
    [InlineData(0, 400, 0)]          // 耐久超过上限时算出负数,按 0
    [InlineData(1, 100, 10000)]      // 15000 − 1500000 ÷ 300
    [InlineData(2, 0, 20000)]
    [InlineData(2, 100, 15000)]      // 20000 − 2000000 ÷ 400
    [InlineData(3, 0, 0)]            // 没有这一档的整修项
    [InlineData(-1, 0, 0)]
    public void TheRepairPriceIsTheFullPriceMinusTheEnduranceShare(int grade, int endurance, int price)
    {
        Assert.Equal(price, DisjointMachine.RepairCost(Script, grade, endurance));
    }

    [Fact]
    public void ARepairRowWithZeroEnduranceCapOrNoScriptCostsNothing()
    {
        ExpertJobScript zero = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[endurance repair cost]", 100, 0) });
        Assert.Equal(0, DisjointMachine.RepairCost(zero, 0, 5));
        Assert.Equal(0, DisjointMachine.RepairCost(null, 0, 5));
    }

    // ------------------------------------------------------------------ 整修

    [Fact]
    public void EnoughGoldRepairsToTheGradeCapForThePriceOfTheMissingEndurance()
    {
        MachineRepairPlan plan = DisjointMachine.PlanRepair(Script, 0, 100, 10000);
        Assert.True(plan.Ok);
        Assert.Equal(6667, plan.Cost);            // 10000 − 10000 × 100 ÷ 300 = 10000 − 3333
        Assert.Equal(300, plan.NewEndurance);

        MachineRepairPlan exact = DisjointMachine.PlanRepair(Script, 0, 100, 6667);   // 金币刚好
        Assert.True(exact.Ok);
        Assert.Equal(300, exact.NewEndurance);

        MachineRepairPlan third = DisjointMachine.PlanRepair(Script, 2, 0, 20000);
        Assert.Equal(400, third.NewEndurance);    // 第 3 档上限 400
    }

    [Fact]
    public void ShortOfTheFullPriceYouBuyWholeUnitsOfEnduranceAtTheUnitPrice()
    {
        // 单价 = 全价 ÷ 上限 = 10000 ÷ 300 = 33(整除)。金币 1000:能买 1000 ÷ 33 = 30 点,实付 1000 − 1000 % 33 = 990。
        MachineRepairPlan plan = DisjointMachine.PlanRepair(Script, 0, 0, 1000);
        Assert.True(plan.Ok);
        Assert.Equal(990, plan.Cost);
        Assert.Equal(30, plan.NewEndurance);

        // 已有耐久的接着加:耐久 50,全价 10000 − 10000 × 50 ÷ 300 = 8334;金币 330 → 买 10 点,实付 330 → 60。
        MachineRepairPlan more = DisjointMachine.PlanRepair(Script, 0, 50, 330);
        Assert.True(more.Ok);
        Assert.Equal(330, more.Cost);
        Assert.Equal(60, more.NewEndurance);
    }

    [Fact]
    public void APartialRepairCanOvershootTheCapByRounding()
    {
        // 耐久 100,全价 6667;金币 6666 差一点:买 6666 ÷ 33 = 202 点,100 + 202 = 302 > 上限 300(服务端的写法:没有再夹一次上限)。
        MachineRepairPlan plan = DisjointMachine.PlanRepair(Script, 0, 100, 6666);
        Assert.True(plan.Ok);
        Assert.Equal(6666, plan.Cost);
        Assert.Equal(302, plan.NewEndurance);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 0)]      // 不到一点耐久的钱:一点也买不到,耐久仍是 0
    [InlineData(32, 0)]
    public void ARepairThatLeavesTheMachineBrokenIsRefused(long gold, int endurance)
    {
        MachineRepairPlan plan = DisjointMachine.PlanRepair(Script, 0, endurance, gold);
        Assert.Equal(ExpertJobError.Refused, plan.Error);
        Assert.False(plan.Ok);
        Assert.Equal(0, plan.Cost);
        Assert.Equal(endurance, plan.NewEndurance);
    }

    [Fact]
    public void ARepairThatEndsOnExactlyOnePointOfEnduranceIsEnough()
    {
        // 单价 33:金币 33 买 1 点,实付 33,耐久 0 → 1;不算"修完还是坏的"。
        MachineRepairPlan one = DisjointMachine.PlanRepair(Script, 0, 0, 33);
        Assert.True(one.Ok);
        Assert.Equal(33, one.Cost);
        Assert.Equal(1, one.NewEndurance);

        // 耐久 1 的机器一点也买不起(金币 0):耐久仍是 1,放行(只看新耐久是否小于 1)。
        MachineRepairPlan keep = DisjointMachine.PlanRepair(Script, 0, 1, 0);
        Assert.True(keep.Ok);
        Assert.Equal(0, keep.Cost);
        Assert.Equal(1, keep.NewEndurance);
    }

    [Fact]
    public void ARepairThatBuysNothingButLeavesEnduranceIsNotRefused()
    {
        // 耐久 5、金币 20 < 单价 33:买不到点数,实付 0,耐久不变(服务端同样放行:只看新耐久是否小于 1)。
        MachineRepairPlan plan = DisjointMachine.PlanRepair(Script, 0, 5, 20);
        Assert.True(plan.Ok);
        Assert.Equal(0, plan.Cost);
        Assert.Equal(5, plan.NewEndurance);
    }

    [Fact]
    public void AUnitPriceOfOneStillAllowsPartialRepairButBelowOneDoesNot()
    {
        // 全价 300、上限 300:单价正好 1,金币 100 → 买 100 点。
        ExpertJobScript unitOne = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[endurance repair cost]", 300, 300) });
        MachineRepairPlan one = DisjointMachine.PlanRepair(unitOne, 0, 0, 100);
        Assert.True(one.Ok);
        Assert.Equal(100, one.Cost);
        Assert.Equal(100, one.NewEndurance);

        // 全价 10、上限 300:单价 0(不足一),部分整修买不到任何点数。
        ExpertJobScript cheap = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[endurance repair cost]", 10, 300) });
        Assert.Equal(ExpertJobError.Refused, DisjointMachine.PlanRepair(cheap, 0, 0, 5).Error);
        // 耐久 5:全价 10(50 ÷ 300 取整 0),金币 5 不够 → 金币不足(这一版不让"买不到点数还扣全价")。
        MachineRepairPlan broke = DisjointMachine.PlanRepair(cheap, 0, 5, 5);
        Assert.Equal(ExpertJobError.NotEnoughGold, broke.Error);
        Assert.Equal(10, broke.Cost);
        Assert.Equal(5, broke.NewEndurance);
    }

    [Fact]
    public void AGradeWithoutARepairRowOrWithoutAScriptCannotBeRepaired()
    {
        Assert.Equal(ExpertJobError.Refused, DisjointMachine.PlanRepair(Script, 3, 0, 99999).Error);
        Assert.Equal(ExpertJobError.Refused, DisjointMachine.PlanRepair(null, 0, 0, 99999).Error);
    }

    [Fact]
    public void AHugeGoldBalanceIsHandledWithoutOverflow()
    {
        MachineRepairPlan plan = DisjointMachine.PlanRepair(Script, 0, 0, long.MaxValue);
        Assert.True(plan.Ok);
        Assert.Equal(10000, plan.Cost);
        Assert.Equal(300, plan.NewEndurance);
    }

    // ------------------------------------------------------------------ 升档

    [Fact]
    public void UpgradingFromGradeZeroNeedsExpertLevelTwoCharacterLevelTenAndTheGold()
    {
        MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(Script, EtcTable, 0, 2, 10, 30000);
        Assert.True(plan.Ok);
        Assert.Equal(1, plan.NewGrade);
        Assert.Equal(30000, plan.Cost);
        Assert.True(plan.ResetsEndurance);
        Assert.Equal(300, plan.NewEndurance);      // 新档(1)在整修表里是第 2 项,上限 300
    }

    [Fact]
    public void UpgradingFromGradeOneUsesTheSecondCostAndTheThirdRepairRow()
    {
        MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(Script, EtcTable, 1, 3, 20, 50000);
        Assert.True(plan.Ok);
        Assert.Equal(2, plan.NewGrade);
        Assert.Equal(50000, plan.Cost);
        Assert.Equal(400, plan.NewEndurance);
        Assert.True(plan.ResetsEndurance);
    }

    [Fact]
    public void TheTopGradeCannotBeUpgraded()
    {
        MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(Script, EtcTable, 2, 4, 99, 999999);
        Assert.Equal(ExpertJobError.NotPermitted, plan.Error);
        Assert.Equal(2, plan.NewGrade);          // 失败时保持原档
        Assert.False(plan.ResetsEndurance);
    }

    [Theory]
    [InlineData(1, false)]       // 副职业等级必须大于新档(1):等于不行
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public void TheExpertLevelMustBeStrictlyAboveTheNewGrade(int expertLevel, bool allowed)
    {
        MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(Script, EtcTable, 0, expertLevel, 99, 99999);
        Assert.Equal(allowed ? ExpertJobError.Ok : ExpertJobError.NotPermitted, plan.Error);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]       // 角色等级不低于上限(键 2 → 10)就行
    [InlineData(11, true)]
    public void TheCharacterLevelMustReachTheLimitOfTheNewGrade(int charLevel, bool allowed)
    {
        MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(Script, EtcTable, 0, 2, charLevel, 99999);
        Assert.Equal(allowed ? ExpertJobError.Ok : ExpertJobError.LevelTooLow, plan.Error);
    }

    [Fact]
    public void AMissingLimitEntryRefusesTheUpgradeBeforeTheCharacterLevelIsLookedAt()
    {
        ExpertJobEtc partial = ExpertJobEtc.Parse(new[] { L("[expertjob level limit]", 1, 10) });
        MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(Script, partial, 0, 2, 99, 99999);
        Assert.Equal(ExpertJobError.NotPermitted, plan.Error);
        Assert.Equal(ExpertJobError.NotPermitted, DisjointMachine.PlanUpgrade(Script, null, 0, 2, 99, 99999).Error);
    }

    [Theory]
    [InlineData(29999, false)]
    [InlineData(30000, true)]
    public void TheGoldMustCoverTheUpgradeCost(long gold, bool allowed)
    {
        MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(Script, EtcTable, 0, 2, 10, gold);
        Assert.Equal(allowed ? ExpertJobError.Ok : ExpertJobError.Refused, plan.Error);
        Assert.Equal(30000, plan.Cost);
    }

    [Fact]
    public void AnUpgradeWithoutAPositiveCostIsRefusedWhateverTheGold()
    {
        ExpertJobScript free = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[]
        {
            L("[endurance repair cost]", 100, 10, 200, 20),
            L("[upgrade cost]", 1, 0, 2, 0),
        });
        Assert.Equal(ExpertJobError.Refused, DisjointMachine.PlanUpgrade(free, EtcTable, 0, 3, 99, 999999).Error);
        ExpertJobScript noCosts = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[endurance repair cost]", 100, 10, 200, 20) });
        Assert.Equal(ExpertJobError.Refused, DisjointMachine.PlanUpgrade(noCosts, EtcTable, 0, 3, 99, 999999).Error);
    }

    [Fact]
    public void TheUpgradeChecksRunInTheServerOrder()
    {
        // 最高档 → 副职业等级 → 上限表有没有 → 角色等级 → 费用与金币。
        Assert.Equal(ExpertJobError.NotPermitted, DisjointMachine.PlanUpgrade(Script, EtcTable, 2, 1, 1, 0).Error);        // 已是最高档
        Assert.Equal(ExpertJobError.NotPermitted, DisjointMachine.PlanUpgrade(Script, EtcTable, 0, 1, 1, 0).Error);        // 副职业等级不够(先于角色等级)
        Assert.Equal(ExpertJobError.LevelTooLow, DisjointMachine.PlanUpgrade(Script, EtcTable, 0, 2, 1, 0).Error);          // 角色等级不够(先于金币)
        Assert.Equal(ExpertJobError.Refused, DisjointMachine.PlanUpgrade(Script, EtcTable, 0, 2, 10, 0).Error);            // 金币
        Assert.Equal(ExpertJobError.NotPermitted, DisjointMachine.PlanUpgrade(null, EtcTable, 0, 2, 10, 99999).Error);
    }

    // ------------------------------------------------------------------ 每次分解后的结算

    [Fact]
    public void CompletingDrawsEnduranceThenExpThenTheBonusRollInThatOrderAndRange()
    {
        // 耐久消耗区间 1 到 3(3 个数)、经验区间 2 到 4(3 个数)、额外经验抽值 0 到 99。
        var rng = new ScriptedDisjointRandom(0, 0, 0);
        DisjointCompletion c = DisjointMachine.Complete(Script, 5, 0f, 0, rng);
        Assert.Equal(new[] { 3, 3, 100 }, rng.Ranges);
        Assert.Equal(1, c.EnduranceLost);
        Assert.Equal(4, c.NewEndurance);
        Assert.Equal(2, c.BaseGain);
        Assert.False(c.BonusHit);
        Assert.Equal(2, c.TotalGain);
        Assert.False(c.MachineBroken);

        var high = new ScriptedDisjointRandom(2, 2, 99);
        DisjointCompletion top = DisjointMachine.Complete(Script, 5, 0f, 0, high);
        Assert.Equal(3, top.EnduranceLost);
        Assert.Equal(2, top.NewEndurance);
        Assert.Equal(4, top.BaseGain);
        Assert.Equal(4, top.TotalGain);
    }

    [Fact]
    public void TheEnduranceLossNeverExceedsWhatIsLeftAndZeroEnduranceBreaksTheMachine()
    {
        DisjointCompletion two = DisjointMachine.Complete(Script, 2, 0f, 0, new ScriptedDisjointRandom(2, 0, 0));      // 抽到 3,只剩 2
        Assert.Equal(2, two.EnduranceLost);
        Assert.Equal(0, two.NewEndurance);
        Assert.True(two.MachineBroken);

        DisjointCompletion exact = DisjointMachine.Complete(Script, 3, 0f, 0, new ScriptedDisjointRandom(2, 0, 0));
        Assert.Equal(0, exact.NewEndurance);
        Assert.True(exact.MachineBroken);

        DisjointCompletion left = DisjointMachine.Complete(Script, 4, 0f, 0, new ScriptedDisjointRandom(2, 0, 0));
        Assert.Equal(1, left.NewEndurance);
        Assert.False(left.MachineBroken);
    }

    [Fact]
    public void TheBonusExpHitsWhenTheRollIsBelowTheChanceAsAFloatComparison()
    {
        // 概率 12.5:抽值 12 < 12.5 命中,13 不中;命中加额外经验 6。
        DisjointCompletion hit = DisjointMachine.Complete(Script, 5, 12.5f, 6, new ScriptedDisjointRandom(0, 2, 12));
        Assert.True(hit.BonusHit);
        Assert.Equal(4, hit.BaseGain);
        Assert.Equal(10, hit.TotalGain);

        DisjointCompletion miss = DisjointMachine.Complete(Script, 5, 12.5f, 6, new ScriptedDisjointRandom(0, 2, 13));
        Assert.False(miss.BonusHit);
        Assert.Equal(4, miss.TotalGain);

        // 概率 0 永远不中,概率 100 永远中。
        Assert.False(DisjointMachine.Complete(Script, 5, 0f, 6, new ScriptedDisjointRandom(0, 0, 0)).BonusHit);
        Assert.True(DisjointMachine.Complete(Script, 5, 100f, 6, new ScriptedDisjointRandom(0, 0, 99)).BonusHit);
    }

    [Fact]
    public void AReversedIntervalIsTreatedAsASinglePoint()
    {
        ExpertJobScript reversed = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[endurance reduce]", 5, 2), L("[gain exp]", 9, 1) });
        var rng = new ScriptedDisjointRandom(0, 0, 0);
        DisjointCompletion c = DisjointMachine.Complete(reversed, 10, 0f, 0, rng);
        Assert.Equal(new[] { 1, 1, 100 }, rng.Ranges);
        Assert.Equal(5, c.EnduranceLost);
        Assert.Equal(9, c.BaseGain);
    }

    [Fact]
    public void AnIntervalExactlyOneShortOfASinglePointIsAlsoASinglePoint()
    {
        // 上界比下界小 1:区间的"个数"算出来是 0,仍按 1 个数处理(向随机数问的范围是 1,不是 0)。
        ExpertJobScript off = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[endurance reduce]", 5, 4), L("[gain exp]", 9, 8) });
        var rng = new ScriptedDisjointRandom(0, 0, 0);
        DisjointCompletion c = DisjointMachine.Complete(off, 10, 0f, 0, rng);
        Assert.Equal(new[] { 1, 1, 100 }, rng.Ranges);
        Assert.Equal(5, c.EnduranceLost);
        Assert.Equal(9, c.BaseGain);
    }

    [Fact]
    public void CompletingRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => DisjointMachine.Complete(null, 5, 0f, 0, new ScriptedDisjointRandom()));
        Assert.Throws<ArgumentNullException>(() => DisjointMachine.Complete(Script, 5, 0f, 0, null));
    }
}
