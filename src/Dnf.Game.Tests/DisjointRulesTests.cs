using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 分解规则:能不能分解、产物算式、随机数抽取次序、预览。依据:服务端 get_disjoint_result_ 0x085d234a、get_additional_item 0x0896d6ab、
/// GetPredictResult 0x085d1f68、_check_disjointable_item_state 0x081f8ba1、_checkDisjointableItem 0x081fa0f6、CheckPortable 一带(静态读码)。
/// 数据是合成的(见 <see cref="ExpertJobTestData"/>):基数 100;结果行 (机器档 0、稀有度 1)→主产物 950 系数 1.5,附加组 1(951、952)、大成功组 1(960)、大成功 10%。
/// 期望值是手算,并与另一份独立实现(Python 的精确分数版 tools/py/disjoint_ref.py)对过:价值 1000、等级 20、稀有度 1 时,
/// 价值 ×1.1 取整 1100 → 收购价 1100 → ×1000 ÷ 100 = 11000 → ×1.5 ÷ 1000 = 16.5 → 16 个主产物。
/// </summary>
public class DisjointRulesTests
{
    private static readonly ExpertJobScript Script = Disjointer();
    private static readonly ExpertJobEtc EtcTable = Etc();

    private static DisjointOutcome Run(DisjointItem item, DisjointContext ctx, ScriptedRandom rng) => DisjointRules.Compute(Script, EtcTable, item, ctx, rng);

    // ------------------------------------------------------------------ 类型这一关(宠物、非装备、时装、神器)

    [Fact]
    public void APetIsRefusedEvenUnderTheServerParityPolicy()
    {
        DisjointItem pet = Candidate(part: EquipmentParts.Creature);
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(pet));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(pet, DisjointPolicy.ServerParity));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItem(pet, true, DisjointPolicy.ServerParity));
    }

    [Fact]
    public void NoItemANonEquipmentOrItemCodeZeroIsRefused()
    {
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(null));
        DisjointItem stackable = Candidate();
        stackable.IsEquipment = false;
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(stackable));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(Candidate(code: 0)));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemType(Candidate()));
    }

    [Theory]
    [InlineData(EquipmentParts.Weapon)]
    [InlineData(EquipmentParts.Coat)]
    [InlineData(EquipmentParts.MagicStone)]
    [InlineData(EquipmentParts.Unknown)]
    public void OrdinaryEquipmentPartsAreDisjointable(int part)
    {
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemType(Candidate(part: part)));
    }

    [Theory]
    [InlineData(EquipmentParts.HatAvatar)]
    [InlineData(5)]
    [InlineData(EquipmentParts.AuroraAvatar)]
    public void AvatarsAreRefusedByTheDefaultPolicyAndLetThroughByServerParity(int part)
    {
        DisjointItem avatar = Candidate(part: part);
        Assert.True(avatar.IsAvatar);
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(avatar));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(avatar, DisjointPolicy.Default));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemType(avatar, DisjointPolicy.ServerParity));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemType(avatar, new DisjointPolicy(true, false)));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(avatar, new DisjointPolicy(false, true)));
    }

    [Theory]
    [InlineData(EquipmentParts.ArtifactRed)]
    [InlineData(EquipmentParts.ArtifactBlue)]
    [InlineData(EquipmentParts.ArtifactGreen)]
    public void ArtifactsAreRefusedByTheDefaultPolicyAndLetThroughByServerParity(int part)
    {
        DisjointItem artifact = Candidate(part: part);
        Assert.True(artifact.IsArtifact);
        Assert.False(artifact.IsCreature);
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(artifact));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemType(artifact, DisjointPolicy.ServerParity));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemType(artifact, new DisjointPolicy(false, true)));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(artifact, new DisjointPolicy(true, false)));
    }

    [Fact]
    public void ThePartClassesDoNotOverlapAtTheirEdges()
    {
        Assert.False(Candidate(part: EquipmentParts.Weapon).IsAvatar);                // 10 不是时装
        Assert.True(Candidate(part: EquipmentParts.AuroraAvatar).IsAvatar);            // 9 是
        Assert.False(Candidate(part: EquipmentParts.MagicStone).IsCreature);           // 21 不是宠物
        Assert.True(Candidate(part: EquipmentParts.Creature).IsCreature);              // 22 是
        Assert.False(Candidate(part: EquipmentParts.ArtifactRed).IsCreature);          // 23 是神器不是宠物
        Assert.False(Candidate(part: EquipmentParts.Creature).IsArtifact);
        Assert.False(Candidate(part: EquipmentParts.ArtifactGreen + 1).IsArtifact);    // 26 不是
        Assert.True(Candidate(part: EquipmentParts.ArtifactGreen).IsArtifact);

        DisjointItem stackable = Candidate(part: EquipmentParts.Creature);
        stackable.IsEquipment = false;
        Assert.False(stackable.IsCreature);
        Assert.False(stackable.IsAvatar);
        Assert.False(stackable.IsArtifact);
    }

    // ------------------------------------------------------------------ 物品规则这一关(鉴定、脚本禁分解、绑定)

    [Fact]
    public void AnUnidentifiedItemNeedsAMachine()
    {
        DisjointItem raw = Candidate(identified: false);
        Assert.Equal(ExpertJobError.Unidentified, DisjointRules.CheckItemRules(raw, false));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemRules(raw, true));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemRules(Candidate(), false));
    }

    [Fact]
    public void AnItemTheScriptForbidsIsNotPermittedWhateverTheMachine()
    {
        DisjointItem item = Candidate(impossible: true);
        Assert.Equal(ExpertJobError.NotPermitted, DisjointRules.CheckItemRules(item, true));
        Assert.Equal(ExpertJobError.NotPermitted, DisjointRules.CheckItemRules(item, false));
    }

    [Fact]
    public void ATradeDeleteItemIsRefusedAsInvalidAndOtherBindingsAreFine()
    {
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemRules(Candidate(attach: AttachTypes.TradeDelete), true));
        foreach (int attach in new[] { AttachTypes.Free, AttachTypes.Trade, AttachTypes.Sealing, AttachTypes.SealingTrade, AttachTypes.Account, AttachTypes.Unknown })
        {
            Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItemRules(Candidate(attach: attach), true));
        }
    }

    [Fact]
    public void TheRuleChecksRunInTheServerOrder()
    {
        // 未鉴定(没机器)先于脚本禁分解先于绑定类型。
        DisjointItem all = Candidate(identified: false, impossible: true, attach: AttachTypes.TradeDelete);
        Assert.Equal(ExpertJobError.Unidentified, DisjointRules.CheckItemRules(all, false));
        Assert.Equal(ExpertJobError.NotPermitted, DisjointRules.CheckItemRules(all, true));
        Assert.Equal(ExpertJobError.NotPermitted, DisjointRules.CheckItemRules(Candidate(impossible: true, attach: AttachTypes.TradeDelete), true));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemRules(null, true));
    }

    [Fact]
    public void TheTypeStageRunsBeforeTheRulesStage()
    {
        // 宠物又被脚本禁分解:先按类型拒绝(0x11),不是 0x13。
        DisjointItem pet = Candidate(part: EquipmentParts.Creature, impossible: true);
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItem(pet, true));
        // 默认策略挡下的时装又未鉴定:先 0x11,不是 0xae。
        DisjointItem avatar = Candidate(part: 3, identified: false);
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItem(avatar, false));
        Assert.Equal(ExpertJobError.Unidentified, DisjointRules.CheckItem(avatar, false, DisjointPolicy.ServerParity));
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckItem(Candidate(), true));
    }

    // ------------------------------------------------------------------ 便携分解器的等级检查

    [Fact]
    public void APortableWithoutAnIndexOrAnUnregisteredIndexAnswersOne()
    {
        Assert.Equal(ExpertJobError.LimitReached, DisjointRules.CheckPortable(Script, -1, Candidate()));
        Assert.Equal(ExpertJobError.LimitReached, DisjointRules.CheckPortable(Script, 2, Candidate()));
        Assert.Equal(ExpertJobError.LimitReached, DisjointRules.CheckPortable(null, 0, Candidate()));
        // 序号检查在物品检查之前:物品为空时,没登记的序号仍先回 1。
        Assert.Equal(ExpertJobError.LimitReached, DisjointRules.CheckPortable(Script, 2, null));
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckPortable(Script, 0, null));
    }

    [Theory]
    [InlineData(0, 29, true)]
    [InlineData(0, 30, true)]       // 便携序号 0 最高 30 级:不超过就行
    [InlineData(0, 31, false)]
    [InlineData(1, 60, true)]
    [InlineData(1, 61, false)]
    public void APortableDisjointsItemsUpToItsRegisteredLevel(int index, int usableLevel, bool allowed)
    {
        ExpertJobError result = DisjointRules.CheckPortable(Script, index, Candidate(usableLevel: usableLevel, grade: 999));
        Assert.Equal(allowed ? ExpertJobError.Ok : ExpertJobError.LevelTooHigh, result);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void AnItemWithoutAUsableLevelIsJudgedByItsItemGrade(int grade, bool allowed)
    {
        ExpertJobError result = DisjointRules.CheckPortable(Script, 0, Candidate(usableLevel: 0, grade: grade));
        Assert.Equal(allowed ? ExpertJobError.Ok : ExpertJobError.LevelTooHigh, result);
    }

    [Fact]
    public void TheUsableLevelWinsOverTheGradeWhenItIsSet()
    {
        // 要求等级 20 ≤ 便携上限 30,物品等级 99 不看。
        Assert.Equal(ExpertJobError.Ok, DisjointRules.CheckPortable(Script, 0, Candidate(usableLevel: 20, grade: 99)));
        // 要求等级 40 > 30,物品等级 1 不救。
        Assert.Equal(ExpertJobError.LevelTooHigh, DisjointRules.CheckPortable(Script, 0, Candidate(usableLevel: 40, grade: 1)));
    }

    // ------------------------------------------------------------------ 产物算式(手算)

    [Fact]
    public void ABigWinRollBelowTheThresholdPicksFromTheBigWinGroup()
    {
        var rng = new ScriptedRandom(999, 5000);
        DisjointOutcome o = Run(Candidate(value: 1000, grade: 20, rarity: 1), Others(), rng);
        Assert.True(o.Ok);
        Assert.Equal(new[] { 950, 960, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 16, 10, 0, 0, 0 }, o.Counts);      // 主产物 16;大成功产物 960 个数 = 20 ÷ 2.0 = 10
        Assert.Equal(new[] { 10000, 10000 }, rng.Ranges);
        Assert.Equal(0, rng.Remaining);
    }

    [Theory]
    [InlineData(1000, 0, 951, 2)]       // 普通组:951 权重 6000、除数 10 → 20 ÷ 10 = 2
    [InlineData(1000, 5000, 951, 2)]
    [InlineData(1000, 6000, 951, 2)]    // 抽值不大于权重就选(含等于)
    [InlineData(1000, 6001, 952, 4)]    // 6001 > 6000:减去 6000 剩 1,选 952(除数 5 → 4)
    [InlineData(1000, 9999, 952, 4)]
    public void TheNormalGroupIsPickedByCumulativeWeightWithInclusiveEdges(int roll1, int roll2, int item, int count)
    {
        DisjointOutcome o = Run(Candidate(value: 1000, grade: 20, rarity: 1), Others(), new ScriptedRandom(roll1, roll2));
        Assert.Equal(new[] { 950, item, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 16, count, 0, 0, 0 }, o.Counts);
    }

    [Theory]
    [InlineData(0, 960)]
    [InlineData(998, 960)]
    [InlineData(999, 960)]          // 大成功:抽值 < 10% × 100 = 1000
    [InlineData(1000, 951)]         // 抽值等于阈值不算大成功
    [InlineData(1001, 951)]
    [InlineData(9999, 951)]
    public void TheBigWinThresholdIsStrictlyBelowTenThousandthsOfTheRowPercent(int roll1, int item)
    {
        DisjointOutcome o = Run(Candidate(value: 1000, grade: 20, rarity: 1), Others(), new ScriptedRandom(roll1, 5000));
        Assert.Equal(item, o.Items[1]);
    }

    [Fact]
    public void ABigWinGroupIsUsedOnlyWhenTheRowNamesOne()
    {
        // 稀有度 3 的行没有大成功组,百分数 0:任何抽值都走普通组。
        DisjointOutcome o = Run(Candidate(value: 1000, grade: 15, rarity: 3), Others(), new ScriptedRandom(0, 5000));
        Assert.Equal(954, o.Items[1]);
        Assert.Equal(3, o.Counts[1]);     // 15 ÷ 4.0 = 3.75 → 3
    }

    [Fact]
    public void YourOwnMachineAddsSelfServiceDrawsForRarityTwoAndAbove()
    {
        // 稀有度 2、价值 5000:1.1 倍 5500 → 5500 → 55000 → ×2.0 ÷ 1000 = 110;大成功线 20% = 2000;
        // 抽值 1999 < 2000 → 大成功组 2(961 权重 3000 除数 1.0 → 30 ÷ 1 = 30);自用抽值 9 < 10 → 970 × 2。
        var rng = new ScriptedRandom(1999, 3000, 9);
        DisjointOutcome o = Run(Candidate(value: 5000, grade: 30, rarity: 2), Mine(), rng);
        Assert.Equal(new[] { 950, 961, 970, 0, 0 }, o.Items);
        Assert.Equal(new[] { 110, 30, 2, 0, 0 }, o.Counts);
        Assert.Equal(new[] { 10000, 10000, 100 }, rng.Ranges);

        // 自用抽值 10 不小于 10 → 没中。
        DisjointOutcome miss = Run(Candidate(value: 5000, grade: 30, rarity: 2), Mine(), new ScriptedRandom(1999, 3000, 10));
        Assert.Equal(new[] { 950, 961, 0, 0, 0 }, miss.Items);
        Assert.Equal(new[] { 110, 30, 0, 0, 0 }, miss.Counts);

        // 大成功线 2000:抽值 2000 不算大成功,走普通组 2(953 除数 2.0 → 15)。
        DisjointOutcome normal = Run(Candidate(value: 5000, grade: 30, rarity: 2), Mine(), new ScriptedRandom(2000, 3000, 99));
        Assert.Equal(new[] { 950, 953, 0, 0, 0 }, normal.Items);
        Assert.Equal(new[] { 110, 15, 0, 0, 0 }, normal.Counts);
    }

    [Fact]
    public void ARarityOneItemOnYourMachineDoesNotDrawTheSelfServiceNumber()
    {
        var rng = new ScriptedRandom(1000, 5000);
        DisjointOutcome o = Run(Candidate(value: 1000, grade: 20, rarity: 1), Mine(), rng);
        Assert.Equal(new[] { 950, 951, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 10000, 10000 }, rng.Ranges);     // 只抽两次
    }

    [Fact]
    public void AnotherPersonsMachineNeverDrawsTheSelfServiceNumberEvenForHighRarity()
    {
        var rng = new ScriptedRandom(1999, 3000);
        DisjointOutcome o = Run(Candidate(value: 5000, grade: 30, rarity: 2), Others(), rng);
        Assert.Equal(new[] { 950, 961, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 10000, 10000 }, rng.Ranges);
    }

    [Fact]
    public void TheMachineGradeBonusAddsToTheBigWinLineOnlyForTheOwner()
    {
        // 机器档 1:结果行 (1,稀有度 1) 系数 3.0 → 主产物 33;大成功百分数 30 → 3000;机主再加机器档表键 2 的 5 → +500 = 3500。
        DisjointOutcome ownerBig = Run(Candidate(), Mine(machineGrade: 1), new ScriptedRandom(3499, 5000));
        Assert.Equal(960, ownerBig.Items[1]);
        Assert.Equal(33, ownerBig.Counts[0]);
        DisjointOutcome ownerNormal = Run(Candidate(), Mine(machineGrade: 1), new ScriptedRandom(3500, 5000));
        Assert.Equal(951, ownerNormal.Items[1]);

        DisjointOutcome otherBig = Run(Candidate(), Others(machineGrade: 1), new ScriptedRandom(2999, 5000));
        Assert.Equal(960, otherBig.Items[1]);
        DisjointOutcome otherNormal = Run(Candidate(), Others(machineGrade: 1), new ScriptedRandom(3000, 5000));
        Assert.Equal(951, otherNormal.Items[1]);
    }

    [Fact]
    public void WithoutAnEtcTableTheMachineGradeBonusIsZero()
    {
        DisjointOutcome o = DisjointRules.Compute(Script, null, Candidate(), Mine(machineGrade: 1), new ScriptedRandom(3000, 5000));
        Assert.Equal(951, o.Items[1]);     // 阈值 3000(没有 +500)
    }

    [Fact]
    public void TheWornBigWinBonusIsAddedAsTruncatedTenThousandthsAndOnlyWhenTheOwnerIsActive()
    {
        // 机器档 1:3000 + 500 + 100 × 12.5 = 4750。
        DisjointContext active = Mine(machineGrade: 1, active: true, bonus: 12.5f);
        Assert.Equal(960, Run(Candidate(), active, new ScriptedRandom(4749, 5000)).Items[1]);
        Assert.Equal(951, Run(Candidate(), active, new ScriptedRandom(4750, 5000)).Items[1]);

        // 机主不在线(没有角色):穿戴加成不算,只有机器档加成 → 3500。
        DisjointContext inactive = Mine(machineGrade: 1, active: false, bonus: 12.5f);
        Assert.Equal(960, Run(Candidate(), inactive, new ScriptedRandom(3499, 5000)).Items[1]);
        Assert.Equal(951, Run(Candidate(), inactive, new ScriptedRandom(3500, 5000)).Items[1]);

        // 别人的机器:连机器档加成都没有,穿戴当然也没有 → 3000。
        DisjointContext others = Others(machineGrade: 1, active: true, bonus: 50f, variation: 70f);
        Assert.Equal(960, Run(Candidate(), others, new ScriptedRandom(2999, 5000)).Items[1]);
        Assert.Equal(951, Run(Candidate(), others, new ScriptedRandom(3000, 5000)).Items[1]);
    }

    [Fact]
    public void TheWornBigWinBonusIsTruncatedNotRounded()
    {
        // 机器档 1:3000 + 500 + 100 × 12.346 = 3500 + 1234.6 → 向零取整得 4734(四舍五入会是 4735)。
        DisjointContext ctx = Mine(machineGrade: 1, active: true, bonus: 12.346f);
        Assert.Equal(960, Run(Candidate(), ctx, new ScriptedRandom(4733, 5000)).Items[1]);
        Assert.Equal(951, Run(Candidate(), ctx, new ScriptedRandom(4734, 5000)).Items[1]);
    }

    [Fact]
    public void TheWornResultVariationAddsATruncatedPercentageToEverySlot()
    {
        // 机器档 1、变化 10%:主产物 33 + 3 = 36;大成功产物 10 + 1 = 11。
        DisjointContext ctx = Mine(machineGrade: 1, active: true, bonus: 12.5f, variation: 10f);
        DisjointOutcome o = Run(Candidate(), ctx, new ScriptedRandom(4749, 5000));
        Assert.Equal(new[] { 950, 960, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 36, 11, 0, 0, 0 }, o.Counts);

        // 普通组:附加 2 个,2 × 10% = 0.2 → 0。
        DisjointOutcome normal = Run(Candidate(), ctx, new ScriptedRandom(4750, 5000));
        Assert.Equal(new[] { 36, 2, 0, 0, 0 }, normal.Counts);
    }

    [Fact]
    public void TheVariationRateIsStoredAsASinglePrecisionFractionSoSeventyPercentOfTenIsSix()
    {
        // 70 ÷ 100 在单精度里是 0.69999999…,10 × 它 = 6.9999999 → 向零取整 6;而不是十进制的 7。
        DisjointContext ctx = Mine(machineGrade: 0, active: true, variation: 70f);
        DisjointOutcome o = Run(Candidate(), ctx, new ScriptedRandom(999, 5000));
        Assert.Equal(new[] { 950, 960, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 27, 16, 0, 0, 0 }, o.Counts);     // 16 + 11 = 27;10 + 6 = 16(不是 17)
    }

    [Fact]
    public void ANegativeVariationShrinksTheCountsTowardZeroButTheAdditionalCountStaysAtLeastOne()
    {
        DisjointOutcome o = Run(Candidate(), Mine(active: true, variation: -50f), new ScriptedRandom(1000, 5000));
        Assert.Equal(new[] { 950, 951, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 8, 1, 0, 0, 0 }, o.Counts);
    }

    [Fact]
    public void ZeroVariationLeavesTheCountsAsTheyAre()
    {
        DisjointOutcome o = Run(Candidate(), Mine(active: true), new ScriptedRandom(1000, 5000));
        Assert.Equal(new[] { 16, 2, 0, 0, 0 }, o.Counts);
    }

    [Fact]
    public void VariationAppliesToTheSelfServiceCountToo()
    {
        // 133.3%:主产物 110 → 256,大成功产物 30 → 69,自用额外产物 2 → 4。
        DisjointOutcome o = Run(Candidate(value: 5000, grade: 30, rarity: 2), Mine(active: true, variation: 133.3f), new ScriptedRandom(1999, 3000, 9));
        Assert.Equal(new[] { 256, 69, 4, 0, 0 }, o.Counts);
    }

    [Fact]
    public void AnUnidentifiedItemUsesTheUnidentifiedRow()
    {
        var rng = new ScriptedRandom(0, 0);
        DisjointOutcome o = Run(Candidate(identified: false), Others(), rng);
        Assert.True(o.Ok);
        Assert.Equal(new[] { 949, 0, 0, 0, 0 }, o.Items);       // 行 (0,1,未鉴定):主产物 949,系数 0.5 → 11000 × 0.5 ÷ 1000 = 5.5 → 5
        Assert.Equal(new[] { 5, 0, 0, 0, 0 }, o.Counts);
        Assert.Equal(2, rng.Ranges.Count);                      // 行里没有附加组,抽值照样抽(服务端先抽后挑)
    }

    [Fact]
    public void AMissingRowIsInvalidForIdentifiedAndNoResultForUnidentifiedAndDrawsNothing()
    {
        var rng = new ScriptedRandom();
        DisjointOutcome identified = Run(Candidate(rarity: 5), Others(), rng);
        Assert.Equal(ExpertJobError.InvalidItem, identified.Error);
        Assert.False(identified.Ok);
        Assert.All(identified.Items, i => Assert.Equal(0, i));
        Assert.All(identified.Counts, c => Assert.Equal(0, c));

        DisjointOutcome unidentified = Run(Candidate(rarity: 5, identified: false), Others(), rng);
        Assert.Equal(ExpertJobError.UnidentifiedNoResult, unidentified.Error);

        Assert.Equal(ExpertJobError.InvalidItem, Run(Candidate(rarity: 1), Others(machineGrade: 2), rng).Error);   // 机器档 2 没有行
        Assert.Empty(rng.Ranges);
    }

    [Fact]
    public void ARandomOptionItemIsPricedAtTheThirdRateInsteadOfTheFirst()
    {
        var rates = new ItemPriceRates(800, 900, 700);
        // 价值 1000 → 1100;随机选项 700‰ → 770 → 7700 → ×1.5 ÷ 1000 = 11.55 → 11;否则 800‰ → 880 → 8800 → 13.2 → 13。
        Assert.Equal(11, Run(Candidate(randomOption: true), Others(rates: rates), new ScriptedRandom(1000, 5000)).Counts[0]);
        Assert.Equal(13, Run(Candidate(randomOption: false), Others(rates: rates), new ScriptedRandom(1000, 5000)).Counts[0]);
        // 倍率表为 null:用替身(都是 1000)。
        Assert.Equal(16, Run(Candidate(randomOption: true), Others(), new ScriptedRandom(1000, 5000)).Counts[0]);
    }

    [Fact]
    public void ACountBelowOneIsRaisedToOneForTheMainAndTheAdditionalProduct()
    {
        // 价值 1:收购价 1 → 10 → ×1.5 ÷ 1000 = 0.015 → 0 → 至少 1。
        Assert.Equal(1, Run(Candidate(value: 1), Others(), new ScriptedRandom(1000, 5000)).Counts[0]);
        // 物品等级 2 ÷ 除数 10.0 = 0.2 → 0 → 至少 1。
        Assert.Equal(1, Run(Candidate(grade: 2), Others(), new ScriptedRandom(1000, 5000)).Counts[1]);
    }

    [Theory]
    [InlineData(5, 1000, 955, 1)]       // 组 3:等级 5 不在任何范围,停在最后一项 955,5 ÷ 8.0 = 0.625 → 至少 1
    [InlineData(35, 7000, 955, 4)]      // 等级 35 在第二项范围内,但抽值超过权重:仍停在最后一项 → 35 ÷ 8.0 = 4.375 → 4
    [InlineData(15, 5000, 954, 3)]      // 等级 15 在第一项范围内,抽值 5000 不大于权重 5000 → 954,15 ÷ 4.0 = 3.75 → 3
    [InlineData(15, 5001, 955, 1)]      // 5001 > 5000:第二项不在等级范围,没有选中,停在最后一项 955 → 15 ÷ 8.0 = 1.875 → 1
    [InlineData(35, 0, 955, 4)]         // 等级 35:第一项(10 到 20)不在范围,不消耗抽值;第二项选中
    public void AnAdditionalGroupFallsOffToItsLastEntryWhenNothingIsPicked(int grade, int roll2, int item, int count)
    {
        DisjointOutcome o = Run(Candidate(grade: grade, rarity: 3), Others(), new ScriptedRandom(1000, roll2));
        Assert.Equal(item, o.Items[1]);
        Assert.Equal(count, o.Counts[1]);
    }

    [Fact]
    public void TheSameItemMayAppearInTwoSlots()
    {
        // 稀有度 4 的普通组第一项就是主产物 950:产物里 950 出现两次。
        DisjointOutcome o = Run(Candidate(rarity: 4), Others(), new ScriptedRandom(0, 0));
        Assert.Equal(new[] { 950, 950, 0, 0, 0 }, o.Items);
        Assert.Equal(new[] { 11, 10, 0, 0, 0 }, o.Counts);
        List<KeyValuePair<int, int>> products = o.Products();
        Assert.Equal(2, products.Count);
        Assert.Equal(new KeyValuePair<int, int>(950, 11), products[0]);
        Assert.Equal(new KeyValuePair<int, int>(950, 10), products[1]);
    }

    [Fact]
    public void ProductsListsOnlySlotsWithAnItemAndACountInSlotOrder()
    {
        var o = new DisjointOutcome();
        o.Items[0] = 950; o.Counts[0] = 3;
        o.Items[1] = 0; o.Counts[1] = 5;       // 没有物品
        o.Items[2] = 970; o.Counts[2] = 0;     // 个数为 0
        o.Items[3] = 971; o.Counts[3] = -2;    // 个数为负
        o.Items[4] = 972; o.Counts[4] = 1;
        Assert.Equal(new[] { new KeyValuePair<int, int>(950, 3), new KeyValuePair<int, int>(972, 1) }, o.Products().ToArray());
        Assert.Equal(5, DisjointOutcome.Slots);
    }

    [Fact]
    public void AScriptWithoutABaseConstantRefusesInsteadOfDividingByZero()
    {
        ExpertJobScript noBase = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[]
        {
            L("[disjoint result]", 0, 1, 0, 950, 1.5f, 0, 0, 0),
        });
        var rng = new ScriptedRandom();
        DisjointOutcome o = DisjointRules.Compute(noBase, EtcTable, Candidate(), Others(), rng);
        Assert.Equal(ExpertJobError.NotPermitted, o.Error);
        Assert.Empty(rng.Ranges);
    }

    [Fact]
    public void AScriptWithoutSelfServiceNeverGivesTheExtraProductButStillDrawsTheNumber()
    {
        ExpertJobScript noSelf = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[]
        {
            L("[base const]", 100),
            L("[disjoint result]", 0, 2, 0, 950, 2.0f, 0, 0, 0),
        });
        var rng = new ScriptedRandom(1, 1, 0);
        DisjointOutcome o = DisjointRules.Compute(noSelf, EtcTable, Candidate(rarity: 2, value: 5000), Mine(), rng);
        Assert.Equal(new[] { 950, 0, 0, 0, 0 }, o.Items);
        Assert.Equal(3, rng.Ranges.Count);
    }

    [Fact]
    public void ComputeRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => DisjointRules.Compute(null, EtcTable, Candidate(), Mine(), new ScriptedRandom()));
        Assert.Throws<ArgumentNullException>(() => DisjointRules.Compute(Script, EtcTable, null, Mine(), new ScriptedRandom()));
        Assert.Throws<ArgumentNullException>(() => DisjointRules.Compute(Script, EtcTable, Candidate(), null, new ScriptedRandom()));
        Assert.Throws<ArgumentNullException>(() => DisjointRules.Compute(Script, EtcTable, Candidate(), Mine(), null));
    }

    // ------------------------------------------------------------------ 附加产物的挑选

    private static SelectRate Entry(int item, int weight, int min = 1, int max = 50, float divisor = 2.0f) => new(item, weight, divisor, min, max);

    [Fact]
    public void PickingFromAnEmptyOrMissingGroupGivesNothing()
    {
        Assert.False(DisjointRules.PickAdditional(null, 5, 10, out _));
        Assert.False(DisjointRules.PickAdditional(new SelectRate[0], 5, 10, out SelectRate none));
        Assert.Equal(0, none.Item);
    }

    [Fact]
    public void PickingConsumesTheRollWeightByWeightOnlyInsideTheGradeRange()
    {
        var group = new[] { Entry(1, 100, 1, 5), Entry(2, 100, 10, 20), Entry(3, 100, 10, 20) };
        // 等级 15:第一项不在范围,不消耗;第二项 100 → 抽值 100 选中它,抽值 101 扣掉 100 余 1 → 第三项。
        Assert.True(DisjointRules.PickAdditional(group, 100, 15, out SelectRate a));
        Assert.Equal(2, a.Item);
        Assert.True(DisjointRules.PickAdditional(group, 101, 15, out SelectRate b));
        Assert.Equal(3, b.Item);
        // 等级 3:只有第一项在范围内。
        Assert.True(DisjointRules.PickAdditional(group, 100, 3, out SelectRate c));
        Assert.Equal(1, c.Item);
    }

    [Fact]
    public void PickingSubtractsEveryWeightSoALaterEntryIsReachedThroughSeveralEarlierOnes()
    {
        var group = new[] { Entry(1, 100, 1, 50), Entry(2, 100, 1, 50), Entry(3, 100, 1, 50) };
        Assert.True(DisjointRules.PickAdditional(group, 100, 10, out SelectRate first));
        Assert.Equal(1, first.Item);
        Assert.True(DisjointRules.PickAdditional(group, 101, 10, out SelectRate secondLow));
        Assert.Equal(2, secondLow.Item);                      // 101 − 100 = 1,不大于 100
        Assert.True(DisjointRules.PickAdditional(group, 150, 10, out SelectRate secondMid));
        Assert.Equal(2, secondMid.Item);                      // 不扣权重的话 150 一路大于 100,会落到最后一项 3
        Assert.True(DisjointRules.PickAdditional(group, 200, 10, out SelectRate secondHigh));
        Assert.Equal(2, secondHigh.Item);
        Assert.True(DisjointRules.PickAdditional(group, 201, 10, out SelectRate third));
        Assert.Equal(3, third.Item);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(20, true)]
    [InlineData(9, false)]
    [InlineData(21, false)]
    public void TheGradeRangeIsInclusiveAtBothEnds(int grade, bool inRange)
    {
        // 只有一项时:在范围内抽值 > 权重 会停在这一项(最后一项);所以拿两项区分"选中"与"落到最后"。
        var group = new[] { Entry(1, 100, 10, 20), Entry(2, 100, 1, 50) };
        DisjointRules.PickAdditional(group, 100, grade, out SelectRate chosen);
        Assert.Equal(inRange ? 1 : 2, chosen.Item);
    }

    [Fact]
    public void AnEntryWithAnItemOfMinusOneYieldsNoAdditionalProduct()
    {
        ExpertJobScript script = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[]
        {
            L("[base const]", 100),
            L("[additional result]", Rate(1, 1, 50, -1, 10000, 2.0f)),
            L("[disjoint result]", 0, 1, 0, 950, 1.5f, 1, 0, 0),
        });
        DisjointOutcome o = DisjointRules.Compute(script, EtcTable, Candidate(), Others(), new ScriptedRandom(1000, 0));
        Assert.Equal(new[] { 950, 0, 0, 0, 0 }, o.Items);
        Assert.Equal(0, o.Counts[1]);
    }

    [Fact]
    public void AnAdditionalCountTooLargeForThirtyTwoBitsFallsBackToOne()
    {
        // 品级 ÷ 除数 超出 32 位:取整给的是整数不定值(一个负数),个数再被抬到至少 1,不会出现巨大的个数。
        ExpertJobScript script = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[]
        {
            L("[base const]", 100),
            L("[additional result]", Rate(1, 1, 50, 951, 10000, 1.0e-9f)),
            L("[disjoint result]", 0, 1, 0, 950, 1.5f, 1, 0, 0),
        });
        DisjointOutcome o = DisjointRules.Compute(script, EtcTable, Candidate(), Others(), new ScriptedRandom(9999, 0));
        Assert.Equal(new[] { 950, 951, 0, 0, 0 }, o.Items);
        Assert.Equal(1, o.Counts[1]);
    }

    // ------------------------------------------------------------------ 预览

    [Fact]
    public void ThePreviewForYourOwnMachineStartsWithTheSelfServiceItemAboveRarityOne()
    {
        Assert.Equal(new[] { 970, 950, 953, 961, 962 }, DisjointRules.PredictItems(Script, Candidate(rarity: 2), Mine()).ToArray());
        Assert.Equal(new[] { 950, 953, 961, 962 }, DisjointRules.PredictItems(Script, Candidate(rarity: 2), Others()).ToArray());
        Assert.Equal(new[] { 950, 951, 952, 960 }, DisjointRules.PredictItems(Script, Candidate(rarity: 1), Mine()).ToArray());
    }

    [Fact]
    public void ThePreviewForAMissingRowIsOnlyTheSelfServiceItemOrNothing()
    {
        Assert.Equal(new[] { 970 }, DisjointRules.PredictItems(Script, Candidate(rarity: 5), Mine()).ToArray());
        Assert.Empty(DisjointRules.PredictItems(Script, Candidate(rarity: 5), Others()));
        Assert.Empty(DisjointRules.PredictItems(Script, Candidate(rarity: 1), Mine(machineGrade: 2)));
    }

    [Fact]
    public void ThePreviewSkipsAdditionalAndBigWinItemsAlreadyListed()
    {
        // 稀有度 4:主产物 950,普通组 950、951,大成功组 951 → 只有 950、951。
        Assert.Equal(new[] { 950, 951 }, DisjointRules.PredictItems(Script, Candidate(rarity: 4), Others()).ToArray());
    }

    [Fact]
    public void ThePreviewFollowsTheUnidentifiedRowAndTheMachineGrade()
    {
        Assert.Equal(new[] { 949 }, DisjointRules.PredictItems(Script, Candidate(identified: false), Others()).ToArray());
        Assert.Equal(new[] { 950, 951, 952, 960 }, DisjointRules.PredictItems(Script, Candidate(rarity: 1), Others(machineGrade: 1)).ToArray());
    }

    [Fact]
    public void ThePreviewWithMissingArgumentsIsEmpty()
    {
        Assert.Empty(DisjointRules.PredictItems(null, Candidate(), Mine()));
        Assert.Empty(DisjointRules.PredictItems(Script, null, Mine()));
        Assert.Empty(DisjointRules.PredictItems(Script, Candidate(), null));
    }

    // ------------------------------------------------------------------ 与 Python 精确分数版对拍的黄金表

    // 每行:价值、物品等级、稀有度、随机选项、机器档、自用、机主在线、穿戴大成功加成、穿戴变化、倍率表(0 中性、1 为 800/900/700、2 为 1234/1/987)、
    // 三个随机数、期望错误码(0 无)、期望物品槽、期望个数槽。期望由 tools/py/disjoint_ref.py 的独立实现算出(python3 tools/py/disjoint_ref.py --csharp 48,种子 20261110)。
    public static IEnumerable<object[]> GoldenCases() => GoldenRows;

    private static readonly object[][] GoldenRows =
    {
        new object[] { 999, 42, 1, true, 0, true, false, 12.5f, 30.0f, 1, 3746, 5372, 68, 0, "950,951,0,0,0", "11,4,0,0,0" },
        new object[] { 1234567, 26, 4, false, 1, true, false, 99.9f, 70.0f, 2, 3650, 2860, 72, 17, "", "" },
        new object[] { 99999999, 32, 4, false, 0, true, false, 12.5f, 133.3f, 0, 5022, 7898, 6, 0, "950,951,970,0,0", "1,16,2,0,0" },
        new object[] { 7, 12, 3, true, 0, false, false, 1.0f, 0.0f, 1, 7110, 1375, 79, 0, "950,954,0,0,0", "1,3,0,0,0" },
        new object[] { 99, 52, 1, true, 0, true, true, 33.3f, 30.0f, 0, 802, 9610, 36, 0, "950,960,0,0,0", "1,33,0,0,0" },
        new object[] { 20000000, 2, 4, false, 0, true, false, 1.0f, 133.3f, 0, 7633, 3147, 56, 0, "950,950,0,0,0", "5251,1,0,0,0" },
        new object[] { 1, 45, 2, false, 0, true, true, 1.0f, -20.0f, 1, 954, 690, 1, 0, "950,961,970,0,0", "1,36,2,0,0" },
        new object[] { 54321, 44, 1, false, 0, false, false, 33.3f, 0.0f, 2, 310, 3861, 66, 0, "950,960,0,0,0", "1106,22,0,0,0" },
        new object[] { 20000000, 10, 4, false, 1, false, false, 0.0f, 133.3f, 2, 3816, 2971, 29, 17, "", "" },
        new object[] { 1000, 46, 3, true, 0, false, false, 0.0f, 0.0f, 1, 3637, 3978, 80, 0, "950,955,0,0,0", "19,5,0,0,0" },
        new object[] { 1000, 7, 1, true, 0, true, false, 33.3f, -20.0f, 0, 3063, 9963, 9, 0, "950,952,0,0,0", "16,1,0,0,0" },
        new object[] { 1234567, 32, 4, true, 0, false, false, 33.3f, 0.0f, 2, 343, 3826, 4, 0, "950,950,0,0,0", "13403,16,0,0,0" },
        new object[] { 1234567, 24, 1, false, 1, true, true, 33.3f, 70.0f, 1, 4142, 8597, 11, 0, "950,960,0,0,0", "55406,20,0,0,0" },
        new object[] { 100, 45, 4, true, 0, false, false, 12.5f, 0.0f, 2, 2069, 8576, 85, 0, "950,951,0,0,0", "1,22,0,0,0" },
        new object[] { 999, 23, 2, false, 1, true, true, 33.3f, 70.0f, 0, 6435, 8216, 39, 17, "", "" },
        new object[] { 1234567, 30, 1, false, 1, true, true, 33.3f, 10.0f, 2, 943, 6790, 50, 0, "950,960,0,0,0", "55301,16,0,0,0" },
        new object[] { 99, 2, 4, false, 0, true, true, 33.3f, 10.0f, 0, 4190, 2121, 55, 0, "950,950,0,0,0", "1,1,0,0,0" },
        new object[] { 4321, 43, 1, false, 1, true, true, 99.9f, 10.0f, 2, 6426, 6559, 35, 0, "950,960,0,0,0", "192,23,0,0,0" },
        new object[] { 7, 34, 2, false, 0, true, true, 12.5f, 10.0f, 0, 2622, 608, 67, 0, "950,961,0,0,0", "1,37,0,0,0" },
        new object[] { 1, 6, 2, false, 0, true, false, 0.0f, 70.0f, 2, 7395, 6712, 41, 0, "950,953,0,0,0", "1,3,0,0,0" },
        new object[] { 54321, 38, 4, false, 0, false, false, 12.5f, 70.0f, 2, 9850, 9375, 80, 0, "950,951,0,0,0", "737,19,0,0,0" },
        new object[] { 4321, 46, 1, true, 0, true, true, 99.9f, 10.0f, 2, 2837, 5528, 7, 0, "950,960,0,0,0", "77,25,0,0,0" },
        new object[] { 7, 14, 4, false, 0, false, false, 12.5f, 133.3f, 0, 2688, 9211, 31, 0, "950,951,0,0,0", "1,7,0,0,0" },
        new object[] { 1000, 19, 2, true, 1, false, false, 0.0f, 0.0f, 2, 2269, 6407, 72, 17, "", "" },
        new object[] { 99, 35, 3, true, 0, false, false, 33.3f, 10.0f, 1, 5545, 6789, 57, 0, "950,955,0,0,0", "1,4,0,0,0" },
        new object[] { 999, 59, 1, false, 0, false, false, 33.3f, 30.0f, 0, 3250, 9538, 0, 0, "950,952,0,0,0", "16,11,0,0,0" },
        new object[] { 20000000, 37, 1, false, 0, false, false, 1.0f, 0.0f, 1, 9600, 1670, 62, 0, "950,951,0,0,0", "6301,3,0,0,0" },
        new object[] { 20000000, 2, 1, false, 0, true, false, 99.9f, 0.0f, 1, 9847, 5410, 55, 0, "950,951,0,0,0", "6301,1,0,0,0" },
        new object[] { 99, 7, 1, true, 0, false, false, 33.3f, 30.0f, 2, 5220, 7161, 4, 0, "950,952,0,0,0", "1,1,0,0,0" },
        new object[] { 54321, 7, 4, false, 0, true, true, 1.0f, 10.0f, 1, 5178, 5477, 62, 0, "950,951,0,0,0", "525,3,0,0,0" },
        new object[] { 54321, 30, 1, false, 1, true, true, 0.0f, 30.0f, 0, 8678, 2698, 12, 0, "950,951,0,0,0", "2329,3,0,0,0" },
        new object[] { 7, 58, 1, true, 0, false, false, 12.5f, -20.0f, 1, 7969, 5676, 30, 0, "950,952,0,0,0", "1,11,0,0,0" },
        new object[] { 99, 39, 2, false, 0, false, false, 0.0f, 0.0f, 2, 7088, 21, 47, 0, "950,953,0,0,0", "2,19,0,0,0" },
        new object[] { 1000, 54, 1, false, 0, true, false, 1.0f, 30.0f, 2, 1301, 3849, 50, 0, "950,952,0,0,0", "20,10,0,0,0" },
        new object[] { 4321, 20, 3, true, 0, true, true, 1.0f, 30.0f, 2, 6086, 663, 60, 0, "950,954,0,0,0", "152,6,0,0,0" },
        new object[] { 7, 4, 2, false, 0, false, false, 33.3f, 30.0f, 0, 3340, 2633, 90, 0, "950,953,0,0,0", "1,2,0,0,0" },
        new object[] { 99, 57, 1, false, 0, false, false, 33.3f, 70.0f, 2, 1067, 7710, 5, 0, "950,952,0,0,0", "1,11,0,0,0" },
        new object[] { 7, 35, 3, false, 1, true, true, 1.0f, 0.0f, 2, 2524, 7393, 76, 17, "", "" },
        new object[] { 99999999, 32, 3, false, 1, false, false, 99.9f, 133.3f, 0, 4623, 6002, 65, 17, "", "" },
        new object[] { 99, 42, 2, false, 1, true, true, 1.0f, -20.0f, 0, 8308, 1331, 10, 17, "", "" },
        new object[] { 1234567, 58, 2, true, 1, true, true, 12.5f, 0.0f, 2, 1101, 3235, 42, 17, "", "" },
        new object[] { 999, 16, 2, false, 0, true, false, 12.5f, 30.0f, 2, 3194, 2099, 80, 0, "950,953,0,0,0", "27,8,0,0,0" },
        new object[] { 7, 43, 2, false, 1, true, false, 33.3f, 10.0f, 1, 3600, 943, 28, 17, "", "" },
        new object[] { 54321, 38, 2, true, 0, false, false, 0.0f, 30.0f, 2, 8815, 6335, 76, 0, "950,953,0,0,0", "1179,19,0,0,0" },
        new object[] { 4321, 60, 2, false, 1, true, true, 33.3f, -20.0f, 1, 2040, 269, 42, 17, "", "" },
        new object[] { 100, 48, 4, false, 0, true, true, 12.5f, 0.0f, 1, 4796, 6652, 91, 0, "950,951,0,0,0", "1,24,0,0,0" },
        new object[] { 99999999, 48, 4, false, 1, true, true, 1.0f, -20.0f, 2, 3294, 7820, 81, 17, "", "" },
        new object[] { 1000, 12, 1, false, 1, true, false, 1.0f, 30.0f, 0, 2283, 6429, 7, 0, "950,960,0,0,0", "33,6,0,0,0" },
    };

    private static readonly ItemPriceRates[] RateSets =
    {
        new ItemPriceRates(1000, 1000, 1000),
        new ItemPriceRates(800, 900, 700),
        new ItemPriceRates(1234, 1, 987),
    };

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void TheComputedProductsMatchTheIndependentPythonImplementation(int value, int grade, int rarity, bool randomOption, int machineGrade, bool mine, bool active,
        float bonus, float variation, int rateSet, int roll1, int roll2, int roll3, int error, string items, string counts)
    {
        DisjointContext ctx = new DisjointContext
        {
            MachineGrade = machineGrade,
            IsMine = mine,
            OwnerActive = active,
            OwnerBigWinBonus = bonus,
            OwnerResultVariation = variation,
            PriceRates = RateSets[rateSet],
        };
        var rng = new ScriptedRandom(roll1, roll2, roll3);
        DisjointOutcome o = Run(Candidate(value: value, grade: grade, rarity: rarity, randomOption: randomOption), ctx, rng);
        if (error != 0)
        {
            Assert.Equal((ExpertJobError)error, o.Error);
            Assert.Empty(rng.Ranges);     // 没有结果行时一个随机数也没抽
            return;
        }
        Assert.True(o.Ok);
        Assert.Equal(items.Split(',').Select(int.Parse).ToArray(), o.Items);
        Assert.Equal(counts.Split(',').Select(int.Parse).ToArray(), o.Counts);
        int draws = 2 + (mine && rarity >= 2 ? 1 : 0);
        Assert.Equal(draws, rng.Ranges.Count);
    }

    // ------------------------------------------------------------------ 其余小件

    [Theory]
    [InlineData("[free]", AttachTypes.Free)]
    [InlineData("[trade]", AttachTypes.Trade)]
    [InlineData("[trade delete]", AttachTypes.TradeDelete)]
    [InlineData("[sealing]", AttachTypes.Sealing)]
    [InlineData("[sealing trade]", AttachTypes.SealingTrade)]
    [InlineData("[account]", AttachTypes.Account)]
    [InlineData("[TRADE DELETE]", AttachTypes.TradeDelete)]
    [InlineData("[nonsense]", AttachTypes.Unknown)]
    [InlineData(null, AttachTypes.Unknown)]
    public void AttachTypeTagsMapToTheServerNumbers(string tag, int expected)
    {
        Assert.Equal(expected, AttachTypes.FromTag(tag));
    }

    [Fact]
    public void ACandidateIsBuiltFromTheItemInfoAndItsTags()
    {
        ItemInfo info = Gear(901, part: EquipmentParts.Coat, level: 15, grade: 33, rarity: 3, value: 4321, attach: "[trade delete]");
        ExpertJobItemTags tags = ExpertJobItemTags.Parse(new[] { L("[impossible contents]", "disjoint"), L("[random option]", 1) });
        DisjointItem item = DisjointItem.From(info, tags, identified: false);
        Assert.Equal(901, item.Code);
        Assert.True(item.IsEquipment);
        Assert.Equal(EquipmentParts.Coat, item.Part);
        Assert.Equal(AttachTypes.TradeDelete, item.AttachType);
        Assert.True(item.ImpossibleDisjoint);
        Assert.False(item.Identified);
        Assert.Equal(3, item.Rarity);
        Assert.Equal(33, item.Grade);
        Assert.Equal(15, item.UsableLevel);
        Assert.Equal(4321, item.Value);
        Assert.True(item.RandomOption);

        DisjointItem plain = DisjointItem.From(Gear(902), null);
        Assert.True(plain.Identified);               // 缺省已鉴定
        Assert.False(plain.ImpossibleDisjoint);
        Assert.False(plain.RandomOption);
        Assert.Equal(AttachTypes.Unknown, plain.AttachType);

        DisjointItem none = DisjointItem.From(null, null);
        Assert.Equal(0, none.Code);
        Assert.Equal(ExpertJobError.InvalidItem, DisjointRules.CheckItemType(none));
    }

    [Fact]
    public void TheSystemRandomStaysInRangeAndIsRepeatableForASeed()
    {
        var a = new SystemDisjointRandom(7);
        var b = new SystemDisjointRandom(7);
        for (int i = 0; i < 200; i++)
        {
            int n = 1 + i % 50;
            int x = a.Next(n);
            Assert.InRange(x, 0, n - 1);
            Assert.Equal(x, b.Next(n));
        }
        Assert.Equal(0, a.Next(1));
        Assert.Equal(0, a.Next(0));
        Assert.InRange(new SystemDisjointRandom().Next(10000), 0, 9999);
    }

    [Theory]
    [InlineData(1.9, 1)]
    [InlineData(-1.9, -1)]
    [InlineData(-0.99, 0)]
    [InlineData(0.0, 0)]
    [InlineData(2147483647.0, 2147483647)]
    [InlineData(2147483647.9, 2147483647)]            // 向零取整后还装得下
    [InlineData(2147483648.0, int.MinValue)]          // 装不下:整数不定值
    [InlineData(1.0e10, int.MinValue)]
    [InlineData(-2147483648.9, int.MinValue)]         // 向零取整后恰好是 int.MinValue,装得下(值与不定值相同)
    [InlineData(-2147483649.0, int.MinValue)]
    [InlineData(-1.0e10, int.MinValue)]
    [InlineData(double.NaN, int.MinValue)]
    [InlineData(double.PositiveInfinity, int.MinValue)]
    [InlineData(double.NegativeInfinity, int.MinValue)]
    public void ConvertingToAnIntegerTruncatesTowardZeroAndGivesTheIndefiniteValueOutOfRange(double v, int expected)
    {
        Assert.Equal(expected, DisjointRules.ToInt(v));
    }
}
