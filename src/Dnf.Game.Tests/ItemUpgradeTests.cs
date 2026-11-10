using Dnf.Data;
using static Dnf.Game.Tests.EnhanceFixtures;

namespace Dnf.Game.Tests;

/// <summary>
/// 强化流程:抽签边界、失败惩罚三档、保护道具、直升材料与票据、前置检查的错误码与次序、称号与活动的调整。
/// 数据全是合成的(装备 900 起,材料 910,票据 920 起;表值随手写)。次序与边界来自服务端静态读码(见 ItemUpgrade.cs 头部的地址),这里钉住规则本身。
/// </summary>
public class ItemUpgradeTests
{
    private static readonly ItemInfo Coat = Gear(900);

    private static UpgradeResult Run(UpgradeSettings s, UpgradeRequest r) => ItemUpgrade.Run(s, r);

    // ------------------------------------------------------------------ 码值

    [Fact]
    public void ErrorCodesArePinnedToTheServerValues()
    {
        Assert.Equal(new[] { 0, 4, 7, 8, 9, 10, 0xd, 0x13, 0x15, 0x16, 0x17, 0x5f, 0xd8, 0xae },
            new[]
            {
                UpgradeCodes.Ok, UpgradeCodes.NoEquipment, UpgradeCodes.Durability, UpgradeCodes.TicketLevel, UpgradeCodes.TicketUnknown,
                UpgradeCodes.NotEnoughGold, UpgradeCodes.MaxLevel, UpgradeCodes.NotUpgradable, UpgradeCodes.NeedSlots, UpgradeCodes.Material,
                UpgradeCodes.ModeMismatch, UpgradeCodes.LevelCap, UpgradeCodes.Protected, UpgradeCodes.NotIdentified,
            });
        Assert.Equal(100000, ItemUpgrade.ProbabilityDenominator);
        Assert.Equal(99, ItemUpgrade.JumpRollMax);
    }

    // ------------------------------------------------------------------ 抽签

    [Fact]
    public void ARollEqualToTheThresholdSucceedsAndOneBelowFails()
    {
        // 失败 = 抽到的数小于阈值(服务端 randInt 含上界,分母 100000)
        var ok = new ScriptedRandom(20000);
        UpgradeResult success = Run(Settings(PlainTable(fail: 20000), ok), Request(Coat));
        Assert.Equal(UpgradeOutcome.Success, success.Outcome);
        Assert.Equal(new[] { 100000 }, ok.Maxes);
        Assert.Equal((20000, 20000L), (success.Roll, success.Threshold));

        var bad = new ScriptedRandom(19999);
        UpgradeResult failure = Run(Settings(PlainTable(fail: 20000), bad), Request(Coat));
        Assert.Equal(UpgradeOutcome.Failure, failure.Outcome);
        Assert.Equal(19999, failure.Roll);
    }

    [Fact]
    public void ZeroIsAFailureWhenTheThresholdIsOneAndOneIsASuccess()
    {
        Assert.Equal(UpgradeOutcome.Failure, Run(Settings(PlainTable(fail: 1), new ScriptedRandom(0)), Request(Coat)).Outcome);
        Assert.Equal(UpgradeOutcome.Success, Run(Settings(PlainTable(fail: 1), new ScriptedRandom(1)), Request(Coat)).Outcome);
    }

    [Fact]
    public void TheTopOfTheRangeIsRollable()
    {
        // 上界 100000 本身也是合法的抽法(含上界):阈值不超过分母时它总是成功
        Assert.Equal(UpgradeOutcome.Success, Run(Settings(PlainTable(fail: 100000), new ScriptedRandom(100000)), Request(Coat)).Outcome);
        Assert.Equal(UpgradeOutcome.Failure, Run(Settings(PlainTable(fail: 100000), new ScriptedRandom(99999)), Request(Coat)).Outcome);
    }

    [Fact]
    public void AZeroThresholdSucceedsWithoutDrawingARandomNumber()
    {
        var random = new ScriptedRandom();
        UpgradeResult r = Run(Settings(PlainTable(fail: 0), random), Request(Coat));
        Assert.Equal(UpgradeOutcome.Success, r.Outcome);
        Assert.Empty(random.Maxes);
        Assert.Equal(-1, r.Roll);
    }

    [Fact]
    public void SuccessAddsOneLevelAndBuildsAnInstanceCopy()
    {
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom(50000)), Request(Coat));
        Assert.Equal((0, 1), (r.OldLevel, r.NewLevel));
        Assert.Equal(1, r.Equipment.Instance.Upgrade);
        Assert.True(r.Equipment.IsInstance);
        Assert.Same(Coat, r.Equipment.Definition);
        Assert.Null(Coat.Instance);   // 共享的定义一点没动
        Assert.Equal(900, r.Equipment.Code);
        Assert.Equal(0, r.Penalty);
        Assert.False(r.Destroyed);
    }

    [Fact]
    public void AFullUpgradePathWalksEveryRowThenStopsAtTheEnd()
    {
        UpgradeTable table = PlainTable(rows: 12);
        ItemInfo item = Coat;
        for (int level = 0; level < 12; level++)
        {
            UpgradeResult r = Run(Settings(table, new ScriptedRandom(100000)), Request(item));
            Assert.Equal(UpgradeOutcome.Success, r.Outcome);
            Assert.Equal(level + 1, r.NewLevel);
            item = r.Equipment;
            Assert.Equal(level + 1, item.Instance.Upgrade);
        }
        UpgradeResult end = Run(Settings(table, new ScriptedRandom()), Request(item));
        Assert.Equal((UpgradeOutcome.Rejected, UpgradeCodes.MaxLevel), (end.Outcome, end.Code));
        Assert.Same(item, end.Equipment);
    }

    [Fact]
    public void LevelThirtyOneIsTheHardCeilingEvenForALongerTable()
    {
        UpgradeTable table = PlainTable(rows: 40);
        UpgradeResult at30 = Run(Settings(table, new ScriptedRandom(100000)), Request(With(Coat, 30)));
        Assert.Equal(UpgradeOutcome.Success, at30.Outcome);
        Assert.Equal(31, at30.NewLevel);
        UpgradeResult at31 = Run(Settings(table, new ScriptedRandom()), Request(With(Coat, 31)));
        Assert.Equal(UpgradeCodes.MaxLevel, at31.Code);
    }

    [Fact]
    public void GoldAndMaterialAreSpentWhetherTheAttemptSucceedsOrFails()
    {
        UpgradeResult win = Run(Settings(PlainTable(), new ScriptedRandom(99999)), Request(Coat));
        UpgradeResult lose = Run(Settings(PlainTable(), new ScriptedRandom(0)), Request(Coat));
        Assert.Equal((100L, 3), (win.GoldSpent, win.MaterialSpent));
        Assert.Equal((100L, 3), (lose.GoldSpent, lose.MaterialSpent));
        Assert.False(win.TicketSpent);
        Assert.False(lose.TicketSpent);
    }

    [Fact]
    public void RejectionsSpendNothingAndKeepTheEquipmentAsIs()
    {
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom()), Request(Coat, gold: 99));
        Assert.Equal(UpgradeCodes.NotEnoughGold, r.Code);
        Assert.Equal((0L, 0, false), (r.GoldSpent, r.MaterialSpent, r.TicketSpent));
        Assert.Same(Coat, r.Equipment);
        Assert.False(r.Success);
    }

    [Fact]
    public void AnAttemptThatPassesTheChecksNeedsARandomSource()
    {
        var settings = new UpgradeSettings { NormalTable = PlainTable() };
        Assert.Throws<InvalidOperationException>(() => Run(settings, Request(Coat)));
        // 检查不过的请求不需要随机源
        Assert.Equal(UpgradeCodes.NotEnoughGold, Run(settings, Request(Coat, gold: 1)).Code);
    }

    [Fact]
    public void RunAndPreviewRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ItemUpgrade.Run(null, Request(Coat)));
        Assert.Throws<ArgumentNullException>(() => ItemUpgrade.Run(Settings(PlainTable(), new ScriptedRandom()), null));
        Assert.Throws<ArgumentNullException>(() => ItemUpgrade.Preview(null, Request(Coat)));
        Assert.Throws<ArgumentNullException>(() => ItemUpgrade.Preview(Settings(PlainTable(), new ScriptedRandom()), null));
    }

    [Fact]
    public void WithARealGeneratorTheFailureRateMatchesTheTable()
    {
        UpgradeSettings settings = Settings(PlainTable(fail: 25000), new DropRandom(7));
        int fails = 0;
        const int Trials = 20000;
        for (int i = 0; i < Trials; i++)
        {
            if (Run(settings, Request(Coat)).Outcome == UpgradeOutcome.Failure)
            {
                fails++;
            }
        }
        double rate = fails / (double)Trials;
        Assert.InRange(rate, 0.23, 0.27);
    }

    [Fact]
    public void TheSameSeedReplaysTheSameOutcomes()
    {
        string Trace(int seed)
        {
            UpgradeSettings s = Settings(PlainTable(fail: 50000, penaltyType: 2), new DropRandom(seed));
            ItemInfo item = Coat;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 30; i++)
            {
                UpgradeResult r = Run(s, Request(item, gold: 1000000, materialCount: 1000));
                if (r.Equipment != null)
                {
                    item = r.Equipment;
                }
                sb.Append(r.Outcome == UpgradeOutcome.Success ? 'S' : r.Outcome == UpgradeOutcome.Failure ? 'F' : 'R').Append(r.NewLevel).Append(',');
            }
            return sb.ToString();
        }
        Assert.Equal(Trace(11), Trace(11));
        Assert.NotEqual(Trace(11), Trace(12));
    }

    // ------------------------------------------------------------------ 失败惩罚

    [Fact]
    public void PenaltyTypeOneLeavesTheLevelAlone()
    {
        UpgradeResult r = Run(Settings(PlainTable(penaltyType: 1), new ScriptedRandom(0)), Request(With(Coat, 5)));
        Assert.Equal((UpgradeOutcome.Failure, 1, 5, 5), (r.Outcome, r.Penalty, r.OldLevel, r.NewLevel));
        Assert.Equal(5, r.Equipment.Instance.Upgrade);
        Assert.False(r.Destroyed);
    }

    [Theory]
    [InlineData(5, 2, 3)]   // n 小于等级:降 n 级
    [InlineData(3, 2, 1)]   // 边界:n = 等级 - 1
    [InlineData(2, 2, 0)]   // n 等于等级:归零
    [InlineData(1, 2, 0)]   // n 大于等级:归零
    [InlineData(0, 2, 0)]
    [InlineData(10, 0, 10)] // n = 0:降 0 级(n 小于等级且标志为 0)
    public void PenaltyTypeTwoDropsByTheRowsLevelsOrResetsToZero(int level, int n, int expected)
    {
        UpgradeTable table = new TableBuilder().Rows(12, 20000, 2, n, Material, 3).FlatCost(100).Build();
        UpgradeResult r = Run(Settings(table, new ScriptedRandom(0)), Request(With(Coat, level)));
        Assert.Equal((UpgradeOutcome.Failure, 2, expected), (r.Outcome, r.Penalty, r.NewLevel));
        Assert.Equal(expected, (r.Equipment.Instance?.Upgrade) ?? 0);
    }

    [Fact]
    public void PenaltyTypeThreeDestroysTheEquipmentAndHandsBackProducts()
    {
        var product = Stack(950);
        ItemInfo seenEquipment = null;
        int seenLevel = -1;
        UpgradeSettings settings = Settings(PlainTable(penaltyType: 3), new ScriptedRandom(0));
        settings.DestroyProducts = (equip, level) =>
        {
            seenEquipment = equip;
            seenLevel = level;
            return new[] { new ItemStack(product, 2) };
        };
        ItemInfo worn = With(Coat, 7);
        UpgradeResult r = Run(settings, Request(worn));
        Assert.Equal((UpgradeOutcome.Failure, 3, true), (r.Outcome, r.Penalty, r.Destroyed));
        Assert.Null(r.Equipment);
        Assert.Same(worn, seenEquipment);
        Assert.Equal(7, seenLevel);
        Assert.Single(r.Products);
        Assert.Equal(2, r.Products[0].Count);
        Assert.Equal(0, r.NewLevel);
        Assert.Equal(0, r.Code);
    }

    [Fact]
    public void DestroyingWithoutAProductSourceGivesNoProducts()
    {
        UpgradeResult r = Run(Settings(PlainTable(penaltyType: 3), new ScriptedRandom(0)), Request(Coat));
        Assert.True(r.Destroyed);
        Assert.NotNull(r.Products);
        Assert.Empty(r.Products);
    }

    [Fact]
    public void AProtectionItemSavesAnUnamplifiedEquipmentAndResetsItToZero()
    {
        UpgradeRequest request = Request(With(Coat, 9));
        request.ProtectionCode = 944;
        UpgradeResult r = Run(Settings(PlainTable(penaltyType: 3), new ScriptedRandom(0)), request);
        Assert.Equal((UpgradeOutcome.Failure, 3, true, false), (r.Outcome, r.Penalty, r.Protected, r.Destroyed));
        Assert.Equal((UpgradeCodes.Protected, 944), (r.Code, r.ProtectionUsed));
        Assert.NotNull(r.Equipment);
        Assert.Equal(0, r.NewLevel);
        Assert.Equal(0, r.Equipment.Instance?.Upgrade ?? 0);
        Assert.Equal(900, r.Equipment.Code);
    }

    [Fact]
    public void AProtectionItemDoesNotHelpAnAmplifiedEquipment()
    {
        UpgradeRequest request = Request(With(Coat, 9, amplified: true), amplify: true);
        request.ProtectionCode = 944;
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom(0), PlainTable(penaltyType: 3)), request);
        Assert.Equal((true, false, 0), (r.Destroyed, r.Protected, r.ProtectionUsed));
        Assert.Null(r.Equipment);
    }

    [Fact]
    public void NoProtectionItemMeansDestroyEvenIfTheSessionOffersNothing()
    {
        UpgradeRequest request = Request(With(Coat, 9));
        request.ProtectionCode = 0;
        Assert.True(Run(Settings(PlainTable(penaltyType: 3), new ScriptedRandom(0)), request).Destroyed);
    }

    [Fact]
    public void AProtectionItemIsOnlyUsedWhenTheFailureWouldDestroy()
    {
        UpgradeRequest request = Request(With(Coat, 9));
        request.ProtectionCode = 944;
        UpgradeResult r = Run(Settings(PlainTable(penaltyType: 2), new ScriptedRandom(0)), request);
        Assert.Equal(0, r.ProtectionUsed);
        Assert.False(r.Protected);
        UpgradeResult win = Run(Settings(PlainTable(penaltyType: 3), new ScriptedRandom(100000)), request);
        Assert.Equal(0, win.ProtectionUsed);
    }

    [Fact]
    public void TheRaritysDestroyLevelTurnsFailuresIntoDestroysFromThatLevelUp()
    {
        UpgradeTable table = new TableBuilder().Rows(12, 20000, 1, 2, Material, 3).FlatCost(100).Destroy("common", 3).Build();
        UpgradeResult below = Run(Settings(table, new ScriptedRandom(0)), Request(With(Coat, 2)));
        Assert.Equal((1, false), (below.Penalty, below.Destroyed));
        UpgradeResult at = Run(Settings(table, new ScriptedRandom(0)), Request(With(Coat, 3)));
        Assert.Equal((3, true), (at.Penalty, at.Destroyed));
        // 别的稀有度没有销毁等级,仍按行自己的类型
        ItemInfo uncommon = Gear(901, rarity: 1);
        UpgradeResult other = Run(Settings(table, new ScriptedRandom(0)), Request(With(uncommon, 3)));
        Assert.Equal((1, false), (other.Penalty, other.Destroyed));
    }

    // ------------------------------------------------------------------ 前置检查

    [Fact]
    public void AMissingEquipmentIsCodeFour()
    {
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom()), new UpgradeRequest());
        Assert.Equal((UpgradeOutcome.Rejected, 4), (r.Outcome, r.Code));
        Assert.Equal(UpgradeCodes.NoEquipment, r.Code);
        Assert.Null(r.Equipment);
    }

    [Fact]
    public void AStackableInTheEquipmentSlotCannotBeUpgraded()
    {
        UpgradeRequest request = Request(Stack(912));
        Assert.Equal(UpgradeCodes.NotUpgradable, Run(Settings(PlainTable(), new ScriptedRandom()), request).Code);
    }

    [Fact]
    public void AnEmptyMaterialSlotIsCodeSixteen()
    {
        UpgradeRequest none = Request(Coat);
        none.Material = null;
        Assert.Equal(UpgradeCodes.Material, Run(Settings(PlainTable(), new ScriptedRandom()), none).Code);
        UpgradeRequest zero = Request(Coat, materialCount: 0);
        Assert.Equal(UpgradeCodes.Material, Run(Settings(PlainTable(), new ScriptedRandom()), zero).Code);
    }

    [Fact]
    public void TheMaterialMustMatchTheRowAndBeEnough()
    {
        UpgradeSettings settings = Settings(PlainTable(materialCount: 3), new ScriptedRandom(100000, 100000));
        UpgradeRequest wrong = Request(Coat);
        wrong.Material = Stack(911);
        Assert.Equal(UpgradeCodes.Material, Run(settings, wrong).Code);
        Assert.Equal(UpgradeCodes.Material, Run(settings, Request(Coat, materialCount: 2)).Code);
        Assert.Equal(UpgradeOutcome.Success, Run(settings, Request(Coat, materialCount: 3)).Outcome);   // 边界:正好够
    }

    [Fact]
    public void GoldMustCoverTheCostExactly()
    {
        UpgradeSettings settings = Settings(PlainTable(), new ScriptedRandom(100000));
        Assert.Equal(UpgradeCodes.NotEnoughGold, Run(settings, Request(Coat, gold: 99)).Code);
        Assert.Equal(UpgradeOutcome.Success, Run(settings, Request(Coat, gold: 100)).Outcome);
    }

    [Fact]
    public void ARowOutsideTheTableOrNoTableIsMaxLevel()
    {
        UpgradeResult r = Run(Settings(PlainTable(rows: 3), new ScriptedRandom()), Request(With(Coat, 3)));
        Assert.Equal(UpgradeCodes.MaxLevel, r.Code);
        Assert.Null(r.Info);
        Assert.Equal(UpgradeCodes.MaxLevel, Run(new UpgradeSettings { Random = new ScriptedRandom() }, Request(Coat)).Code);   // 没有表
    }

    [Fact]
    public void ThePartMustBeInTheEnchantableRangeAndNotTheTitleSlot()
    {
        // 部位 11(称号)取得到表行,所以走到前置检查被拒(0x13);部位在 10 到 21 之外取不到表行,服务端在更早一步返回 0xd
        UpgradeSettings settings = Settings(PlainTable(), new ScriptedRandom());
        Assert.Equal(UpgradeCodes.NotUpgradable, Run(settings, Request(Gear(902, part: EquipmentParts.TitleName))).Code);
        Assert.Equal(UpgradeCodes.MaxLevel, Run(settings, Request(Gear(903, part: EquipmentParts.AuroraAvatar))).Code);
        Assert.Equal(UpgradeCodes.MaxLevel, Run(settings, Request(Gear(904, part: EquipmentParts.Creature))).Code);
        Assert.Equal(UpgradeOutcome.Success, Run(Settings(PlainTable(), new ScriptedRandom(100000)), Request(Gear(905, part: EquipmentParts.Weapon))).Outcome);
        Assert.Equal(UpgradeOutcome.Success, Run(Settings(PlainTable(), new ScriptedRandom(100000)), Request(Gear(906, part: EquipmentParts.MagicStone))).Outcome);
    }

    [Fact]
    public void DamagedEquipmentIsCodeSeven()
    {
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom()), Request(With(Coat, 2, damaged: true)));
        Assert.Equal(UpgradeCodes.Durability, r.Code);
    }

    [Fact]
    public void TheRaritysMaxLevelCapsUpgrades()
    {
        UpgradeTable table = new TableBuilder().Rows(12, 0, 1, 2, Material, 3).FlatCost(100).Max("common", 5).Build();
        Assert.Equal(UpgradeOutcome.Success, Run(Settings(table, new ScriptedRandom()), Request(With(Coat, 4))).Outcome);
        Assert.Equal(UpgradeCodes.LevelCap, Run(Settings(table, new ScriptedRandom()), Request(With(Coat, 5))).Code);
        Assert.Equal(UpgradeCodes.LevelCap, Run(Settings(table, new ScriptedRandom()), Request(With(Coat, 6))).Code);
        // 别的稀有度不受限
        Assert.Equal(UpgradeOutcome.Success, Run(Settings(table, new ScriptedRandom()), Request(With(Gear(901, rarity: 1), 8))).Outcome);
    }

    [Fact]
    public void DestroyTierNeedsFreeSlotsForTheProducts()
    {
        UpgradeSettings settings = Settings(PlainTable(penaltyType: 3), new ScriptedRandom(0));
        settings.PredictDestroyTypes = _ => 2;
        UpgradeRequest few = Request(Coat);
        few.FreeSlots = 2;
        Assert.Equal(UpgradeCodes.NeedSlots, Run(settings, few).Code);
        UpgradeRequest enough = Request(Coat);
        enough.FreeSlots = 3;   // 2 种产物 + 1
        Assert.Equal(UpgradeOutcome.Failure, Run(settings, enough).Outcome);
    }

    [Fact]
    public void AnAmplifiedEquipmentNeedsOneMoreFreeSlot()
    {
        UpgradeSettings settings = Settings(PlainTable(), new ScriptedRandom(0), PlainTable(penaltyType: 3));
        settings.PredictDestroyTypes = _ => 2;
        UpgradeRequest request = Request(With(Coat, 1, amplified: true), amplify: true);
        request.FreeSlots = 3;
        Assert.Equal(UpgradeCodes.NeedSlots, Run(settings, request).Code);
        request.FreeSlots = 4;
        Assert.Equal(UpgradeOutcome.Failure, Run(settings, request).Outcome);
    }

    [Fact]
    public void TheSlotRequirementOnlyAppliesWhereAFailureWouldDestroy()
    {
        UpgradeSettings settings = Settings(PlainTable(penaltyType: 1), new ScriptedRandom(0));
        settings.PredictDestroyTypes = _ => 50;
        UpgradeRequest request = Request(Coat);
        request.FreeSlots = 0;
        Assert.Equal(UpgradeOutcome.Failure, Run(settings, request).Outcome);
    }

    [Fact]
    public void WithoutAPredictionTheDestroyTierStillNeedsOneSlot()
    {
        UpgradeSettings settings = Settings(PlainTable(penaltyType: 3), new ScriptedRandom(0));
        UpgradeRequest request = Request(Coat);
        request.FreeSlots = 0;
        Assert.Equal(UpgradeCodes.NeedSlots, Run(settings, request).Code);
        request.FreeSlots = 1;
        Assert.Equal(UpgradeOutcome.Failure, Run(settings, request).Outcome);
    }

    [Fact]
    public void TheCharacterLevelMustBeInsideTheEquipmentsLimitRange()
    {
        var ranges = new[] { new UpgradeLevelRange(1, 10, 20), new UpgradeLevelRange(2, 30, 40) };
        ItemInfo limited = Gear(907, enhance: new EnhanceData(upgradeRanges: ranges));
        UpgradeSettings settings = Settings(PlainTable(), new ScriptedRandom(100000, 100000, 100000));
        Assert.Equal(UpgradeCodes.NotUpgradable, Run(settings, Request(limited, level: 9)).Code);
        Assert.Equal(UpgradeOutcome.Success, Run(settings, Request(limited, level: 10)).Outcome);
        Assert.Equal(UpgradeOutcome.Success, Run(settings, Request(limited, level: 20)).Outcome);
        Assert.Equal(UpgradeCodes.NotUpgradable, Run(settings, Request(limited, level: 21)).Code);
    }

    [Fact]
    public void AmplifiedEquipmentUsesTheAmplifyRange()
    {
        var ranges = new[] { new UpgradeLevelRange(1, 10, 20), new UpgradeLevelRange(2, 30, 40) };
        ItemInfo limited = Gear(907, enhance: new EnhanceData(upgradeRanges: ranges));
        UpgradeSettings settings = Settings(PlainTable(), new ScriptedRandom(100000), PlainTable());
        ItemInfo amplified = With(limited, 0, amplified: true);
        Assert.Equal(UpgradeCodes.NotUpgradable, Run(settings, Request(amplified, level: 20, amplify: true)).Code);
        Assert.Equal(UpgradeOutcome.Success, Run(settings, Request(amplified, level: 30, amplify: true)).Outcome);
    }

    [Fact]
    public void AnEquipmentWithoutARangeOfItsKindIsNotLimited()
    {
        ItemInfo onlyAmplify = Gear(908, enhance: new EnhanceData(upgradeRanges: new[] { new UpgradeLevelRange(2, 30, 40) }));
        UpgradeSettings settings = Settings(PlainTable(), new ScriptedRandom(100000));
        Assert.Equal(UpgradeOutcome.Success, Run(settings, Request(onlyAmplify, level: 1)).Outcome);
    }

    [Fact]
    public void AnUnidentifiedAmplificationCannotBeUpgraded()
    {
        ItemInfo item = With(Coat, 1, amplified: true, identified: false);
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom(), PlainTable()), Request(item, amplify: true));
        Assert.Equal(UpgradeCodes.NotIdentified, r.Code);
    }

    [Fact]
    public void TheRequestKindMustMatchTheEquipmentState()
    {
        UpgradeSettings settings = Settings(PlainTable(), new ScriptedRandom(), PlainTable());
        Assert.Equal(UpgradeCodes.ModeMismatch, Run(settings, Request(Coat, amplify: true)).Code);
        Assert.Equal(UpgradeCodes.ModeMismatch, Run(settings, Request(With(Coat, 1, amplified: true), amplify: false)).Code);
    }

    [Fact]
    public void AmplifiedEquipmentRunsOnTheAmplifyTable()
    {
        UpgradeTable normal = PlainTable(fail: 0);
        UpgradeTable amplify = new TableBuilder().Rows(12, 50000, 1, 2, 911, 1).FlatCost(250).Build();
        UpgradeRequest request = Request(With(Coat, 2, amplified: true), amplify: true);
        request.Material = Stack(911);
        var random = new ScriptedRandom(49999);
        UpgradeResult r = Run(Settings(normal, random, amplify), request);
        Assert.Equal((UpgradeOutcome.Failure, 50000L, 250L), (r.Outcome, r.Threshold, r.GoldSpent));
        Assert.True(r.Equipment.Instance.Amplified);   // 增幅标志跟着走
    }

    [Fact]
    public void ChecksRunInTheServerOrder()
    {
        UpgradeSettings s = Settings(new TableBuilder().Rows(12, 20000, 3, 2, Material, 3).FlatCost(100).Max("common", 1).Build(), new ScriptedRandom(), PlainTable());
        s.PredictDestroyTypes = _ => 5;

        // 未鉴定 先于 材料
        ItemInfo raw = With(Coat, 1, amplified: true, identified: false);
        UpgradeRequest a = Request(raw, amplify: true);
        a.Material = Stack(999);
        Assert.Equal(UpgradeCodes.NotIdentified, Run(s, a).Code);

        // 材料 先于 金币
        UpgradeRequest b = Request(Coat, gold: 0);
        b.Material = Stack(999);
        Assert.Equal(UpgradeCodes.Material, Run(s, b).Code);

        // 金币 先于 部位(称号部位)
        Assert.Equal(UpgradeCodes.NotEnoughGold, Run(s, Request(Gear(902, part: EquipmentParts.TitleName), gold: 0)).Code);

        // 部位 先于 耐久
        Assert.Equal(UpgradeCodes.NotUpgradable, Run(s, Request(With(Gear(902, part: EquipmentParts.TitleName), 0, damaged: true))).Code);

        // 耐久 先于 稀有度封顶
        Assert.Equal(UpgradeCodes.Durability, Run(s, Request(With(Coat, 1, damaged: true))).Code);

        // 封顶 先于 销毁档的空位
        UpgradeRequest c = Request(With(Coat, 1));
        c.FreeSlots = 0;
        Assert.Equal(UpgradeCodes.LevelCap, Run(s, c).Code);

        // 空位 先于 角色等级区间
        ItemInfo ranged = Gear(909, rarity: 1, enhance: new EnhanceData(upgradeRanges: new[] { new UpgradeLevelRange(1, 40, 50) }));
        UpgradeRequest d = Request(ranged, level: 1);
        d.FreeSlots = 0;
        Assert.Equal(UpgradeCodes.NeedSlots, Run(s, d).Code);

        // 检查的错误先于"请求种类与装备状态不符"
        UpgradeRequest e = Request(Coat, gold: 0, amplify: true);
        Assert.Equal(UpgradeCodes.NotEnoughGold, Run(s, e).Code);
    }

    // ------------------------------------------------------------------ 直升材料

    private static UpgradeSettings JumpSettings(IDropRandom random, UpgradeTable amplify = null)
    {
        UpgradeSettings s = Settings(PlainTable(), random, amplify);
        s.LegacyJumpMaterials = new Dictionary<int, int> { [960] = 7 };
        return s;
    }

    private static ItemInfo LegacyJump => Stack(960);

    [Fact]
    public void ALegacyJumpMaterialRaisesTheLevelDirectlyWithoutGold()
    {
        var random = new ScriptedRandom(99);
        UpgradeRequest request = Request(Coat, gold: 0);
        request.Material = LegacyJump;
        request.MaterialCount = 1;
        UpgradeResult r = Run(JumpSettings(random), request);
        Assert.Equal((UpgradeOutcome.Success, true, 0, 7), (r.Outcome, r.Jump, r.OldLevel, r.NewLevel));
        Assert.Equal(7, r.Equipment.Instance.Upgrade);
        Assert.Equal((0L, 1), (r.GoldSpent, r.MaterialSpent));
        Assert.Equal(new[] { 99 }, random.Maxes);   // 直升抽的是 0 到 99,不是分母
    }

    [Fact]
    public void ALegacyJumpMaterialAlwaysSucceedsBecauseItsRateIsOneHundred()
    {
        UpgradeRequest request = Request(Coat);
        request.Material = LegacyJump;
        request.MaterialCount = 1;
        UpgradeSettings s = JumpSettings(new ScriptedRandom(0, 50, 99));
        Assert.All(new[] { 0, 1, 2 }, _ => Assert.Equal(UpgradeOutcome.Success, Run(s, request).Outcome));
    }

    [Fact]
    public void ALegacyMaterialIsIgnoredOnAmplifiedEquipment()
    {
        UpgradeRequest request = Request(With(Coat, 0, amplified: true), amplify: true);
        request.Material = LegacyJump;
        request.MaterialCount = 1;
        UpgradeResult r = Run(JumpSettings(new ScriptedRandom(), PlainTable()), request);
        Assert.Equal(UpgradeCodes.Material, r.Code);   // 当普通材料看,与行里的材料对不上
    }

    private static ItemInfo Ticket(int level, int rate, bool amplify = false, int code = 961)
    {
        var data = amplify ? new EnhanceData(amplifyTicket: new ReinforceTicket(level, rate)) : new EnhanceData(reinforceTicket: new ReinforceTicket(level, rate));
        return Stack(code, data);
    }

    [Fact]
    public void AReinforceTicketRollsAgainstItsPercentAndFailsWithoutPenalty()
    {
        UpgradeRequest request = Request(With(Coat, 4), gold: 0);
        request.Material = Ticket(9, 50);
        request.MaterialCount = 2;
        var win = new ScriptedRandom(49);   // 抽到的数小于成功率百分数才算成功
        UpgradeResult r1 = Run(Settings(PlainTable(penaltyType: 3), win), request);
        Assert.Equal((UpgradeOutcome.Success, 9), (r1.Outcome, r1.NewLevel));
        Assert.Equal(new[] { 99 }, win.Maxes);
        var lose = new ScriptedRandom(50);
        UpgradeResult r2 = Run(Settings(PlainTable(penaltyType: 3), lose), request);
        Assert.Equal((UpgradeOutcome.Failure, 4, 0, false), (r2.Outcome, r2.NewLevel, r2.Penalty, r2.Destroyed));
        Assert.Equal(4, r2.Equipment.Instance.Upgrade);
        Assert.Equal(1, r2.MaterialSpent);   // 直升材料一次一个,失败也扣
        Assert.Equal(0L, r2.GoldSpent);
    }

    [Fact]
    public void ARateOfOneHundredAlwaysWinsAndZeroNeverDoes()
    {
        UpgradeRequest request = Request(Coat);
        request.MaterialCount = 1;
        request.Material = Ticket(5, 100);
        Assert.Equal(UpgradeOutcome.Success, Run(Settings(PlainTable(), new ScriptedRandom(99)), request).Outcome);
        request.Material = Ticket(5, 0);
        Assert.Equal(UpgradeOutcome.Failure, Run(Settings(PlainTable(), new ScriptedRandom(0)), request).Outcome);
    }

    [Fact]
    public void TicketValuesOutsideTheServersRangesAreTreatedAsZero()
    {
        // 等级 0 到 31 之外读作 0:不是直升(count = 0),按普通材料走,与行里的材料对不上
        UpgradeRequest request = Request(Coat);
        request.MaterialCount = 1;
        request.Material = Ticket(32, 50);
        Assert.Equal(UpgradeCodes.Material, Run(Settings(PlainTable(), new ScriptedRandom()), request).Code);
        request.Material = Ticket(-1, 50);
        Assert.Equal(UpgradeCodes.Material, Run(Settings(PlainTable(), new ScriptedRandom()), request).Code);
        // 成功率 0 到 100 之外读作 0:仍是直升,但必定失败
        request.Material = Ticket(5, 101);
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom(0)), request);
        Assert.Equal((true, UpgradeOutcome.Failure), (r.Jump, r.Outcome));
        // 等级 31 是上边界,合法
        request.Material = Ticket(31, 100);
        Assert.Equal(31, Run(Settings(PlainTable(), new ScriptedRandom(0)), request).NewLevel);
    }

    [Fact]
    public void AnAmplifyTicketOnlyWorksOnAmplifiedEquipment()
    {
        UpgradeRequest plain = Request(Coat);
        plain.MaterialCount = 1;
        plain.Material = Ticket(5, 100, amplify: true);
        Assert.Equal(UpgradeCodes.NotUpgradable, Run(Settings(PlainTable(), new ScriptedRandom()), plain).Code);

        UpgradeRequest amp = Request(With(Coat, 0, amplified: true), amplify: true);
        amp.MaterialCount = 1;
        amp.Material = Ticket(5, 100, amplify: true);
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom(0), PlainTable()), amp);
        Assert.Equal((UpgradeOutcome.Success, 5, true), (r.Outcome, r.NewLevel, r.Jump));
        Assert.True(r.Equipment.Instance.Amplified);
    }

    [Fact]
    public void AReinforceTicketAlsoWorksOnAmplifiedEquipment()
    {
        UpgradeRequest amp = Request(With(Coat, 0, amplified: true), amplify: true);
        amp.MaterialCount = 1;
        amp.Material = Ticket(6, 100);
        UpgradeResult r = Run(Settings(PlainTable(), new ScriptedRandom(0), PlainTable()), amp);
        Assert.Equal((UpgradeOutcome.Success, 6), (r.Outcome, r.NewLevel));
    }

    [Fact]
    public void ATicketBeatsALegacyCodeForTheLevelButTheLegacyRateStaysOneHundred()
    {
        UpgradeSettings s = JumpSettings(new ScriptedRandom(99));
        UpgradeRequest request = Request(Coat);
        request.MaterialCount = 1;
        request.Material = Stack(960, new EnhanceData(reinforceTicket: new ReinforceTicket(3, 10)));
        UpgradeResult r = Run(s, request);
        Assert.Equal((UpgradeOutcome.Success, 3), (r.Outcome, r.NewLevel));   // 等级取券上的 3,成功率仍按旧式材料的 100
    }

    [Fact]
    public void AJumpNeverUsesTheTablesMaterialRowButStillChecksEverythingElse()
    {
        UpgradeRequest request = Request(With(Coat, 1, damaged: true));
        request.MaterialCount = 1;
        request.Material = Ticket(5, 100);
        Assert.Equal(UpgradeCodes.Durability, Run(Settings(PlainTable(), new ScriptedRandom()), request).Code);
    }

    // ------------------------------------------------------------------ 票据(第二物品)

    private static UpgradeSettings TicketSettings(IDropRandom random, UpgradeTicketDef def, int penaltyLevels = 2, int penaltyType = 3)
    {
        UpgradeTable table = new TableBuilder().Rows(12, 20000, penaltyType, penaltyLevels, Material, 3).FlatCost(100).Build();
        UpgradeSettings s = Settings(table, random, table);
        s.Tickets = new Dictionary<int, UpgradeTicketDef> { [920] = def };
        return s;
    }

    private static UpgradeRequest WithTicket(ItemInfo equip, bool amplify = false)
    {
        UpgradeRequest r = Request(equip, amplify: amplify);
        r.Ticket = Stack(920);
        return r;
    }

    [Fact]
    public void ATicketNotInTheRegistryIsCodeNine()
    {
        UpgradeSettings s = TicketSettings(new ScriptedRandom(), new UpgradeTicketDef(0, 31, 0));
        UpgradeRequest r = Request(Coat);
        r.Ticket = Stack(921);
        Assert.Equal(UpgradeCodes.TicketUnknown, Run(s, r).Code);
        s.Tickets = null;
        r.Ticket = Stack(920);
        Assert.Equal(UpgradeCodes.TicketUnknown, Run(s, r).Code);
    }

    [Fact]
    public void ATicketOnlyAppliesInsideItsLevelRange()
    {
        UpgradeSettings s = TicketSettings(new ScriptedRandom(100000, 100000), new UpgradeTicketDef(3, 5, 0));
        Assert.Equal(UpgradeCodes.TicketLevel, Run(s, WithTicket(With(Coat, 2))).Code);
        Assert.Equal(UpgradeCodes.TicketLevel, Run(s, WithTicket(With(Coat, 6))).Code);
        Assert.Equal(UpgradeOutcome.Success, Run(s, WithTicket(With(Coat, 3))).Outcome);
        Assert.Equal(UpgradeOutcome.Success, Run(s, WithTicket(With(Coat, 5))).Outcome);
    }

    [Fact]
    public void ATicketIsSpentOnSuccessAndOnFailure()
    {
        UpgradeSettings s = TicketSettings(new ScriptedRandom(100000, 0), new UpgradeTicketDef(0, 31, 0));
        Assert.True(Run(s, WithTicket(Coat)).TicketSpent);
        Assert.True(Run(s, WithTicket(Coat)).TicketSpent);
    }

    [Fact]
    public void ATicketWithFlagZeroRemovesThePenaltyEvenWhereTheRowWouldDestroy()
    {
        UpgradeSettings s = TicketSettings(new ScriptedRandom(0), new UpgradeTicketDef(0, 31, 0), penaltyType: 3);
        UpgradeResult r = Run(s, WithTicket(With(Coat, 6)));
        Assert.Equal((UpgradeOutcome.Failure, 1, false, 6), (r.Outcome, r.Penalty, r.Destroyed, r.NewLevel));
    }

    [Theory]
    [InlineData(6, 1, 2, 5)]    // 标志 1:n(行里的 2)小于等级 → 降标志那么多级
    [InlineData(6, 3, 2, 3)]
    [InlineData(2, 1, 2, 0)]    // n 不小于等级:归零(哪怕标志只有 1)
    [InlineData(3, 1, 2, 2)]
    [InlineData(6, -1, 2, 0)]   // 标志为负:失败归零
    public void ATicketWithAFlagDowngradesByTheFlag(int level, int flag, int n, int expected)
    {
        UpgradeSettings s = TicketSettings(new ScriptedRandom(0), new UpgradeTicketDef(0, 31, flag), penaltyLevels: n);
        UpgradeResult r = Run(s, WithTicket(With(Coat, level)));
        Assert.Equal((UpgradeOutcome.Failure, 2, expected), (r.Outcome, r.Penalty, r.NewLevel));
    }

    [Fact]
    public void ATicketCanWaiveTheGoldAndOverrideTheMaterialCount()
    {
        UpgradeSettings s = TicketSettings(new ScriptedRandom(100000, 100000, 100000),
            new UpgradeTicketDef(0, 31, 0, freeGold: true, normalMaterialCount: 5, amplifyMaterialCount: 2));
        UpgradeResult r = Run(s, new UpgradeRequest
        {
            Equipment = Coat,
            Material = Stack(Material),
            MaterialCount = 5,
            Gold = 0,
            Ticket = Stack(920),
            CharacterLevel = 50,
        });
        Assert.Equal((UpgradeOutcome.Success, 0L, 5), (r.Outcome, r.GoldSpent, r.MaterialSpent));
        // 数量不够新的要求(5)
        Assert.Equal(UpgradeCodes.Material, Run(s, new UpgradeRequest
        {
            Equipment = Coat,
            Material = Stack(Material),
            MaterialCount = 4,
            Gold = 0,
            Ticket = Stack(920),
            CharacterLevel = 50,
        }).Code);
        // 增幅强化走另一个数量
        ItemInfo amplified = With(Coat, 0, amplified: true);
        UpgradeResult amp = Run(s, new UpgradeRequest
        {
            Equipment = amplified,
            Material = Stack(Material),
            MaterialCount = 2,
            Gold = 0,
            Ticket = Stack(920),
            CharacterLevel = 50,
            RequestAmplify = true,
        });
        Assert.Equal((UpgradeOutcome.Success, 2), (amp.Outcome, amp.MaterialSpent));
    }

    // ------------------------------------------------------------------ 称号与活动

    [Fact]
    public void ATitleItemLowersTheFailureThreshold()
    {
        UpgradeSettings s = Settings(PlainTable(fail: 20000), new ScriptedRandom(14999, 15000));
        s.HasTitleItem = true;
        s.TitleProbIncrease = 5000;
        UpgradeResult fail = Run(s, Request(Coat));
        Assert.Equal((UpgradeOutcome.Failure, 15000L), (fail.Outcome, fail.Threshold));
        Assert.Equal(UpgradeOutcome.Success, Run(s, Request(Coat)).Outcome);
    }

    [Fact]
    public void AnOverlargeIncreaseWrapsAroundAndMeansCertainSuccess()
    {
        // 服务端用无符号数相减:20000 - 30000 绕成很大的数,大于分母就按 0(必定成功,也不抽签)
        var random = new ScriptedRandom();
        UpgradeSettings s = Settings(PlainTable(fail: 20000), random);
        s.HasTitleItem = true;
        s.TitleProbIncrease = 30000;
        UpgradeResult r = Run(s, Request(Coat));
        Assert.Equal((UpgradeOutcome.Success, 0L), (r.Outcome, r.Threshold));
        Assert.Empty(random.Maxes);
    }

    [Fact]
    public void ANegativeIncreaseRaisesTheThresholdButAnythingAboveTheDenominatorBecomesZero()
    {
        UpgradeSettings s = Settings(PlainTable(fail: 20000), new ScriptedRandom(24999));
        s.HasTitleItem = true;
        s.TitleProbIncrease = -5000;
        UpgradeResult r = Run(s, Request(Coat));
        Assert.Equal((UpgradeOutcome.Failure, 25000L), (r.Outcome, r.Threshold));

        UpgradeSettings over = Settings(PlainTable(fail: 99000), new ScriptedRandom());
        over.HasTitleItem = true;
        over.TitleProbIncrease = -5000;   // 104000 > 100000 → 0
        Assert.Equal(0L, Run(over, Request(Coat)).Threshold);
    }

    [Fact]
    public void ATitleIncreaseDoesNotTouchAZeroThreshold()
    {
        UpgradeSettings s = Settings(PlainTable(fail: 0), new ScriptedRandom());
        s.HasTitleItem = true;
        s.TitleProbIncrease = -5000;
        Assert.Equal(0L, Run(s, Request(Coat)).Threshold);
    }

    [Fact]
    public void NoTitleItemMeansTheIncreaseIsIgnored()
    {
        UpgradeSettings s = Settings(PlainTable(fail: 20000), new ScriptedRandom(19999));
        s.HasTitleItem = false;
        s.TitleProbIncrease = 5000;
        UpgradeResult r = Run(s, Request(Coat));
        Assert.Equal((UpgradeOutcome.Failure, 20000L), (r.Outcome, r.Threshold));
    }

    [Theory]
    [InlineData(0f, 100)]
    [InlineData(10f, 90)]
    [InlineData(50f, 50)]
    [InlineData(100f, 0)]
    [InlineData(33.3333f, 66)]   // 100 - 33.3333 = 66.6667,截断
    [InlineData(-10f, 110)]      // 负折扣就是加价
    public void ATitleItemsCostDiscountIsAPercentTruncated(float discount, int expected)
    {
        UpgradeSettings s = Settings(PlainTable(), new ScriptedRandom(100000));
        s.HasTitleItem = true;
        s.TitleCostDiscount = discount;
        Assert.Equal(expected, ItemUpgrade.PayableGold(s, 100, false));
        UpgradeResult r = Run(s, Request(Coat, gold: 1000));
        Assert.Equal(expected, r.GoldSpent);
    }

    [Fact]
    public void TheEventReducesTheCostOnlyForUnamplifiedEquipmentWithIntegerDivision()
    {
        UpgradeSettings s = Settings(PlainTable(), new ScriptedRandom());
        s.EventCostReducePercent = 30;
        Assert.Equal(70, ItemUpgrade.PayableGold(s, 100, false));
        Assert.Equal(100, ItemUpgrade.PayableGold(s, 100, true));
        s.EventCostReducePercent = 33;
        Assert.Equal(67, ItemUpgrade.PayableGold(s, 100, false));
        Assert.Equal(5, ItemUpgrade.PayableGold(s, 7, false));        // 7 * 33 / 100 = 2(整数除法),7 - 2 = 5
        s.EventCostReducePercent = 150;
        Assert.Equal(0, ItemUpgrade.PayableGold(s, 100, false));      // 不低于 0
        s.EventCostReducePercent = 0;
        Assert.Equal(100, ItemUpgrade.PayableGold(s, 100, false));
        s.EventCostReducePercent = -10;
        Assert.Equal(100, ItemUpgrade.PayableGold(s, 100, false));    // 只认正的
    }

    [Fact]
    public void TheEventReductionComesBeforeTheTitleDiscount()
    {
        UpgradeSettings s = Settings(PlainTable(), new ScriptedRandom());
        s.EventCostReducePercent = 50;
        s.HasTitleItem = true;
        s.TitleCostDiscount = 10f;
        Assert.Equal(45, ItemUpgrade.PayableGold(s, 100, false));   // 100 → 50 → 45
    }

    // ------------------------------------------------------------------ 预览

    [Fact]
    public void PreviewMatchesRunsRejectionsAndDoesNotRoll()
    {
        var random = new ScriptedRandom();
        UpgradeSettings s = Settings(PlainTable(), random);
        ItemUpgrade.Plan ok = ItemUpgrade.Preview(s, Request(Coat));
        Assert.Equal((0, 100L, 20000L, false), (ok.Code, ok.Gold, ok.Threshold, ok.Jump));
        Assert.Equal((910, 3), (ok.Info.MaterialCode, ok.Info.MaterialCount));
        Assert.Empty(random.Maxes);
        ItemUpgrade.Plan poor = ItemUpgrade.Preview(s, Request(Coat, gold: 5));
        Assert.Equal(UpgradeCodes.NotEnoughGold, poor.Code);
        Assert.Equal(100L, poor.Gold);
    }

    [Fact]
    public void PreviewShowsTheCostEvenWhenAnEarlierCheckRejects()
    {
        UpgradeSettings s = Settings(PlainTable(), new ScriptedRandom(), PlainTable());
        ItemUpgrade.Plan shortMaterial = ItemUpgrade.Preview(s, Request(Coat, materialCount: 1));
        Assert.Equal((UpgradeCodes.Material, 100L), (shortMaterial.Code, shortMaterial.Gold));
        ItemInfo raw = With(Coat, 1, amplified: true, identified: false);
        ItemUpgrade.Plan unidentified = ItemUpgrade.Preview(s, Request(raw, amplify: true));
        Assert.Equal((UpgradeCodes.NotIdentified, 100L), (unidentified.Code, unidentified.Gold));
    }

    [Fact]
    public void PreviewReportsTheJumpTargetAndRate()
    {
        UpgradeRequest request = Request(Coat);
        request.MaterialCount = 1;
        request.Material = Ticket(8, 40);
        ItemUpgrade.Plan plan = ItemUpgrade.Preview(Settings(PlainTable(), new ScriptedRandom()), request);
        Assert.Equal((0, true, 8, 40), (plan.Code, plan.Jump, plan.JumpLevel, plan.JumpRate));
        Assert.Equal(0L, plan.Gold);
    }

    [Fact]
    public void PenaltyTypeForFollowsTicketThenDestroyLevelThenRow()
    {
        UpgradeTable table = new TableBuilder().Rows(5, 1, 2, 1, 1, 1).Destroy("common", 3).Build();
        UpgradeInfo info = table.GetInfo(0, 1, 0, 12);
        Assert.Equal(2, ItemUpgrade.PenaltyTypeFor(table, Coat, info, null, 0));
        Assert.Equal(3, ItemUpgrade.PenaltyTypeFor(table, Coat, info, null, 3));
        Assert.Equal(1, ItemUpgrade.PenaltyTypeFor(table, Coat, info, new UpgradeTicketDef(0, 9, 0), 5));
        Assert.Equal(2, ItemUpgrade.PenaltyTypeFor(table, Coat, info, new UpgradeTicketDef(0, 9, 4), 0));
        Assert.Equal(2, ItemUpgrade.PenaltyTypeFor(table, Coat, info, new UpgradeTicketDef(0, 9, -1), 0));
    }
}
