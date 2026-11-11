using static Dnf.Game.Tests.EnhanceFixtures;

namespace Dnf.Game.Tests;

/// <summary>
/// 套装的件数判定与加成:穿齐才给 <c>[set ability]</c>、满 N 件才给 <c>[piece set ability] N</c>,少一件不触发。
/// 数据全是合成的:5 件套 940 到 944(3 件档、5 件档、穿齐),3 件套 950 到 952(只有穿齐),数值随手写。
/// 件数口径(穿齐 = 成员表里每件都穿着;各档累计)是推断,依据见 EquipSets.cs 头部。
/// </summary>
public class EquipSetsTests
{
    private static readonly int[] FiveParts =
    {
        EquipmentParts.Coat, EquipmentParts.Shoulder, EquipmentParts.Pants, EquipmentParts.Shoes, EquipmentParts.Waist,
    };

    private static Dictionary<int, ItemInfo> Defs = Build();

    private static Dictionary<int, ItemInfo> Build()
    {
        var defs = new Dictionary<int, ItemInfo>();
        var master = new EnhanceData(
            setMembers: new[] { 940, 941, 942, 943, 944 },
            setFullBonus: Stats(EquipStat.HpMax, 100),
            setPieceBonuses: new[]
            {
                new SetPieceBonus(5, Stats(EquipStat.PhysicalAttack, 50)),
                new SetPieceBonus(3, Stats(EquipStat.PhysicalAttack, 30)),
            });
        for (int i = 0; i < 5; i++)
        {
            defs[940 + i] = Gear(940 + i, FiveParts[i], enhance: i == 0 ? master : new EnhanceData(setMaster: 940));
        }
        var small = new EnhanceData(setMembers: new[] { 950, 951, 952 }, setFullBonus: Stats(EquipStat.MagicalAttack, 40));
        defs[950] = Gear(950, EquipmentParts.Weapon, enhance: small);
        defs[951] = Gear(951, EquipmentParts.Ring, enhance: new EnhanceData(setMaster: 950));
        defs[952] = Gear(952, EquipmentParts.Support, enhance: new EnhanceData(setMaster: 950));
        defs[900] = Gear(900);   // 与套装无关的装备
        return defs;
    }

    private static ItemInfo Lookup(int code) => Defs.TryGetValue(code, out ItemInfo item) ? item : null;

    private static List<ItemInfo> Wear(params int[] codes) => codes.Select(c => Defs[c]).ToList();

    private static List<ItemInfo> WearMask(int mask)
    {
        var worn = new List<ItemInfo>();
        for (int i = 0; i < 5; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                worn.Add(Defs[940 + i]);
            }
        }
        return worn;
    }

    // ------------------------------------------------------------------ 主件判定

    [Fact]
    public void MasterOfReadsTheMasterTagOrTheOwnMemberList()
    {
        Assert.Equal(0, EquipSets.MasterOf(null));
        Assert.Equal(0, EquipSets.MasterOf(Defs[900]));
        Assert.Equal(940, EquipSets.MasterOf(Defs[940]));   // 主件自己带成员表
        Assert.Equal(940, EquipSets.MasterOf(Defs[943]));   // 成员写了主件编号
        Assert.Equal(950, EquipSets.MasterOf(Defs[951]));
    }

    [Fact]
    public void TheMasterTagWinsOverTheOwnMemberList()
    {
        ItemInfo odd = Gear(960, enhance: new EnhanceData(setMaster: 940, setMembers: new[] { 960 }));
        Assert.Equal(940, EquipSets.MasterOf(odd));
    }

    [Fact]
    public void AnItemWithOnlyAFullBonusBlockIsNotItsOwnMaster()
    {
        // 没有成员表也没写主件编号:不知道属于哪一套
        ItemInfo block = Gear(961, enhance: new EnhanceData(setFullBonus: Stats(EquipStat.HpMax, 1)));
        Assert.Equal(0, EquipSets.MasterOf(block));
    }

    // ------------------------------------------------------------------ 件数判定

    [Theory]
    [InlineData(0b11111, 5, "3,5", true)]   // 穿齐
    [InlineData(0b01111, 4, "3", false)]    // 少一件(缺最后一件)
    [InlineData(0b11110, 4, "3", false)]    // 少一件(缺主件):主件不在也行,只数成员
    [InlineData(0b10111, 4, "3", false)]
    [InlineData(0b00111, 3, "3", false)]    // 正好 3 件
    [InlineData(0b11100, 3, "3", false)]
    [InlineData(0b00011, 2, "", false)]     // 3 件档差一件
    [InlineData(0b00001, 1, "", false)]
    public void TheTiersAndTheFullBonusFollowTheWornCount(int mask, int worn, string tiers, bool full)
    {
        IReadOnlyList<EquipSetState> states = EquipSets.Evaluate(WearMask(mask), Lookup);
        EquipSetState state = Assert.Single(states);
        Assert.Equal((940, worn, 5, full), (state.Master, state.Worn, state.Total, state.FullActive));
        Assert.Equal(tiers, string.Join(",", state.ActiveTiers));
    }

    [Fact]
    public void NothingWornMeansNoSetState()
    {
        Assert.Empty(EquipSets.Evaluate(new List<ItemInfo>(), Lookup));
        Assert.Empty(EquipSets.Evaluate(null, Lookup));
        Assert.Empty(EquipSets.Evaluate(Wear(900), Lookup));
    }

    [Fact]
    public void TheSameCodeWornTwiceCountsOnce()
    {
        EquipSetState state = Assert.Single(EquipSets.Evaluate(Wear(940, 940, 941, 941), Lookup));
        Assert.Equal(2, state.Worn);
    }

    [Fact]
    public void NullEntriesAreSkipped()
    {
        var worn = new List<ItemInfo> { Defs[940], null, Defs[941], null, Defs[942] };
        Assert.Equal(3, Assert.Single(EquipSets.Evaluate(worn, Lookup)).Worn);
    }

    [Fact]
    public void TwoSetsAreCountedSeparatelyAndListedByMasterCode()
    {
        IReadOnlyList<EquipSetState> states = EquipSets.Evaluate(Wear(951, 952, 950, 940, 941), Lookup);
        Assert.Equal(new[] { 940, 950 }, states.Select(s => s.Master));
        Assert.Equal(new[] { 2, 3 }, states.Select(s => s.Worn));
        Assert.False(states[0].FullActive);
        Assert.True(states[1].FullActive);
    }

    [Fact]
    public void AMemberMissingFromTheMastersListDoesNotCount()
    {
        // 编号 970 自称属于 940,但 940 的成员表里没有它
        ItemInfo stray = Gear(970, EquipmentParts.Wrist, enhance: new EnhanceData(setMaster: 940));
        EquipSetState state = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { Defs[940], Defs[941], stray }, Lookup));
        Assert.Equal(2, state.Worn);
    }

    [Fact]
    public void AWornPieceCountsForTheListItIsInEvenWithoutPointingBack()
    {
        // 成员表里的 1023 没写 [set item master],照样数进来;它自己单独穿着不会"召出"这套;主件 1020 不在自己的成员表里
        var set = new EnhanceData(setMembers: new[] { 1021, 1022, 1023 }, setFullBonus: Stats(EquipStat.HpMax, 7));
        ItemInfo master = Gear(1020, EquipmentParts.Weapon, enhance: set);
        ItemInfo a = Gear(1021, EquipmentParts.Coat, enhance: new EnhanceData(setMaster: 1020));
        ItemInfo b = Gear(1022, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 1020));
        ItemInfo c = Gear(1023, EquipmentParts.Shoes);
        Func<int, ItemInfo> lookup = code => code == 1020 ? master : null;
        EquipSetState state = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { a, b, c }, lookup));
        Assert.Equal((1020, 3, 3, true), (state.Master, state.Worn, state.Total, state.FullActive));
        Assert.Empty(EquipSets.Evaluate(new List<ItemInfo> { c }, lookup));
    }

    [Fact]
    public void AWornPieceStillCountsWhenItPointsAtAnotherMaster()
    {
        // 1032 在 1030 的成员表里,但它写的主件是 1099(另一套,成员表里没有 1032):1030 照样数到 2 件,1099 一件也没有所以不出现
        ItemInfo master = Gear(1030, EquipmentParts.Weapon, enhance: new EnhanceData(setMembers: new[] { 1031, 1032 }, setFullBonus: Stats(EquipStat.HpMax, 7)));
        ItemInfo other = Gear(1099, EquipmentParts.Ring, enhance: new EnhanceData(setMembers: new[] { 1099, 1098 }, setFullBonus: Stats(EquipStat.HpMax, 9)));
        ItemInfo a = Gear(1031, EquipmentParts.Coat, enhance: new EnhanceData(setMaster: 1030));
        ItemInfo b = Gear(1032, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 1099));
        Func<int, ItemInfo> lookup = code => code == 1030 ? master : code == 1099 ? other : null;
        EquipSetState state = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { a, b }, lookup));
        Assert.Equal((1030, 2, 2, true), (state.Master, state.Worn, state.Total, state.FullActive));
    }

    [Fact]
    public void MastersSharingOneMemberListGiveOneSetAndOneBonus()
    {
        // 两个主件写着同一张成员表(全库有 25 张这样的表),穿着件分别指向它们:只算编号小的,加成不重复
        int[] list = { 1042, 1043 };
        ItemInfo m1 = Gear(1040, EquipmentParts.Weapon, enhance: new EnhanceData(setMembers: list, setFullBonus: Stats(EquipStat.HpMax, 5)));
        ItemInfo m2 = Gear(1041, EquipmentParts.Ring, enhance: new EnhanceData(setMembers: list, setFullBonus: Stats(EquipStat.HpMax, 5)));
        ItemInfo a = Gear(1042, EquipmentParts.Coat, enhance: new EnhanceData(setMaster: 1041));
        ItemInfo b = Gear(1043, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 1040));
        Func<int, ItemInfo> lookup = code => code == 1040 ? m1 : code == 1041 ? m2 : null;
        var worn = new List<ItemInfo> { a, b };
        EquipSetState state = Assert.Single(EquipSets.Evaluate(worn, lookup));
        Assert.Equal((1040, 2, 2, true), (state.Master, state.Worn, state.Total, state.FullActive));
        Assert.Equal(5, Assert.Single(EquipSets.Terms(worn, lookup))[EquipStat.HpMax]);
    }

    [Fact]
    public void AMemberListSharedByTwoMastersIsNotMergedWithADifferentOneOfTheSameSize()
    {
        ItemInfo m1 = Gear(1060, EquipmentParts.Weapon, enhance: new EnhanceData(setMembers: new[] { 1062, 1063 }, setFullBonus: Stats(EquipStat.HpMax, 5)));
        ItemInfo m2 = Gear(1061, EquipmentParts.Ring, enhance: new EnhanceData(setMembers: new[] { 1064, 1065 }, setFullBonus: Stats(EquipStat.HpMax, 6)));
        var items = new[]
        {
            Gear(1062, EquipmentParts.Coat, enhance: new EnhanceData(setMaster: 1060)),
            Gear(1063, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 1060)),
            Gear(1064, EquipmentParts.Shoes, enhance: new EnhanceData(setMaster: 1061)),
            Gear(1065, EquipmentParts.Waist, enhance: new EnhanceData(setMaster: 1061)),
        };
        Func<int, ItemInfo> lookup = code => code == 1060 ? m1 : code == 1061 ? m2 : null;
        IReadOnlyList<EquipSetState> states = EquipSets.Evaluate(items, lookup);
        Assert.Equal(new[] { 1060, 1061 }, states.Select(s => s.Master));
        Assert.All(states, s => Assert.True(s.FullActive));
    }

    [Fact]
    public void APointerWithoutAnyListedPieceWornGivesNoState()
    {
        // 1053 自称属于 1050,但成员表里没有它;单独穿着时这套一件也没有
        ItemInfo master = Gear(1050, EquipmentParts.Weapon, enhance: new EnhanceData(setMembers: new[] { 1051, 1052 }, setFullBonus: Stats(EquipStat.HpMax, 5)));
        ItemInfo stray = Gear(1053, EquipmentParts.Coat, enhance: new EnhanceData(setMaster: 1050));
        Func<int, ItemInfo> lookup = code => code == 1050 ? master : null;
        Assert.Empty(EquipSets.Evaluate(new List<ItemInfo> { stray }, lookup));
        // 主件不在自己的成员表里:单独穿着主件也没有成员
        Assert.Empty(EquipSets.Evaluate(new List<ItemInfo> { master }, lookup));
    }

    [Fact]
    public void TheMasterNeedNotBeWornToResolveTheSet()
    {
        EquipSetState state = Assert.Single(EquipSets.Evaluate(Wear(941, 942, 943), Lookup));
        Assert.Equal((940, 3, 5), (state.Master, state.Worn, state.Total));
        Assert.True(state.HasTier(3));
        Assert.False(state.HasTier(5));
        Assert.False(state.HasTier(4));
    }

    [Fact]
    public void ASetWhoseMasterCannotBeFoundIsIgnoredUnlessTheMasterIsWorn()
    {
        Func<int, ItemInfo> none = _ => null;
        Assert.Empty(EquipSets.Evaluate(Wear(941, 942, 943), none));
        Assert.Empty(EquipSets.Evaluate(Wear(941, 942, 943), null));
        // 主件自己穿着就用穿着的那件
        Assert.Equal(4, Assert.Single(EquipSets.Evaluate(Wear(940, 941, 942, 943), none)).Worn);
        Assert.Equal(4, Assert.Single(EquipSets.Evaluate(Wear(940, 941, 942, 943), null)).Worn);
    }

    [Fact]
    public void AMasterRecordWithoutSetDataIsIgnored()
    {
        // 成员指向的"主件"其实没有套装数据
        ItemInfo member = Gear(971, enhance: new EnhanceData(setMaster: 900));
        Assert.Empty(EquipSets.Evaluate(new List<ItemInfo> { member }, Lookup));
    }

    [Fact]
    public void AMasterWithoutAMemberListCountsTheWornGroupAndNeverReportsFull()
    {
        var tierOnly = new EnhanceData(
            setFullBonus: Stats(EquipStat.HpMax, 9),
            setPieceBonuses: new[] { new SetPieceBonus(2, Stats(EquipStat.HpMax, 1)) });
        ItemInfo master = Gear(980, enhance: tierOnly);
        ItemInfo a = Gear(981, EquipmentParts.Coat, enhance: new EnhanceData(setMaster: 980));
        ItemInfo b = Gear(982, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 980));
        Func<int, ItemInfo> lookup = code => code == 980 ? master : null;
        EquipSetState state = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { a, b }, lookup));
        Assert.Equal((2, 0, false), (state.Worn, state.Total, state.FullActive));
        Assert.True(state.HasTier(2));
        EquipSetState one = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { a }, lookup));
        Assert.Empty(one.ActiveTiers);
    }

    [Fact]
    public void ASetWithoutAFullBonusBlockNeverReportsFull()
    {
        var tiers = new EnhanceData(setMembers: new[] { 990, 991 }, setPieceBonuses: new[] { new SetPieceBonus(2, Stats(EquipStat.HpMax, 1)) });
        ItemInfo a = Gear(990, EquipmentParts.Coat, enhance: tiers);
        ItemInfo b = Gear(991, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 990));
        Func<int, ItemInfo> lookup = code => code == 990 ? a : null;
        EquipSetState state = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { a, b }, lookup));
        Assert.False(state.FullActive);
        Assert.Equal(2, state.Worn);
        Assert.True(state.HasTier(2));
    }

    [Fact]
    public void TiersBelowOnePieceNeverApply()
    {
        var odd = new EnhanceData(
            setMembers: new[] { 992 },
            setPieceBonuses: new[] { new SetPieceBonus(0, Stats(EquipStat.HpMax, 1)), new SetPieceBonus(-1, Stats(EquipStat.HpMax, 2)), new SetPieceBonus(1, Stats(EquipStat.HpMax, 3)) });
        ItemInfo only = Gear(992, EquipmentParts.Coat, enhance: odd);
        EquipSetState state = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { only }, c => c == 992 ? only : null));
        Assert.Equal(new[] { 1 }, state.ActiveTiers);
    }

    [Fact]
    public void ARepeatedTierKeyIsReportedOnlyOnce()
    {
        var twice = new EnhanceData(
            setMembers: new[] { 995 },
            setPieceBonuses: new[] { new SetPieceBonus(1, Stats(EquipStat.HpMax, 1)), new SetPieceBonus(1, Stats(EquipStat.HpMax, 2)) });
        ItemInfo only = Gear(995, EquipmentParts.Coat, enhance: twice);
        EquipSetState state = Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { only }, c => c == 995 ? only : null));
        Assert.Equal(new[] { 1 }, state.ActiveTiers);
    }

    [Fact]
    public void WithoutAMemberListOnlyTheWornPiecesPointingAtTheMasterCount()
    {
        var tierOnly = new EnhanceData(setPieceBonuses: new[] { new SetPieceBonus(2, Stats(EquipStat.HpMax, 1)) });
        ItemInfo master = Gear(984, enhance: tierOnly);
        ItemInfo pointing = Gear(985, EquipmentParts.Coat, enhance: new EnhanceData(setMaster: 984));
        ItemInfo stranger = Gear(986, EquipmentParts.Pants);                                                        // 没有任何套装数据
        ItemInfo elsewhere = Gear(987, EquipmentParts.Shoes, enhance: new EnhanceData(setMaster: 988));             // 指向别的(没有数据的)主件
        EquipSetState state = Assert.Single(EquipSets.Evaluate(
            new List<ItemInfo> { pointing, stranger, elsewhere }, code => code == 984 ? master : null));
        Assert.Equal(1, state.Worn);
        Assert.Empty(state.ActiveTiers);
    }

    [Fact]
    public void ASingleMemberSetIsFullWithOnePiece()
    {
        var one = new EnhanceData(setMembers: new[] { 993 }, setFullBonus: Stats(EquipStat.HpMax, 5));
        ItemInfo only = Gear(993, EquipmentParts.Coat, enhance: one);
        Assert.True(Assert.Single(EquipSets.Evaluate(new List<ItemInfo> { only }, c => c == 993 ? only : null)).FullActive);
    }

    // ------------------------------------------------------------------ 加成项

    [Fact]
    public void ACompleteSetGivesTheFullBlockAndEveryReachedTier()
    {
        IReadOnlyList<EquipStats> terms = EquipSets.Terms(WearMask(0b11111), Lookup);
        Assert.Equal(3, terms.Count);
        Assert.Equal(100.0, terms[0][EquipStat.HpMax]);               // 穿齐的块在前
        Assert.Equal(30.0, terms[1][EquipStat.PhysicalAttack]);       // 然后各档按件数从小到大
        Assert.Equal(50.0, terms[2][EquipStat.PhysicalAttack]);
        EquipStats sum = EquipStats.Sum(terms);
        Assert.Equal((100.0, 80.0), (sum[EquipStat.HpMax], sum[EquipStat.PhysicalAttack]));   // 档位累计
    }

    [Fact]
    public void OnePieceShortOfTheSetGivesNoFullBlockButKeepsTheTier()
    {
        IReadOnlyList<EquipStats> terms = EquipSets.Terms(WearMask(0b01111), Lookup);
        EquipStats sum = EquipStats.Sum(terms);
        Assert.Equal(0.0, sum[EquipStat.HpMax]);
        Assert.Equal(30.0, sum[EquipStat.PhysicalAttack]);
    }

    [Fact]
    public void TwoPiecesGiveNothingAtAll()
    {
        Assert.Empty(EquipSets.Terms(WearMask(0b00011), Lookup));
        Assert.Empty(EquipSets.Terms(null, Lookup));
        Assert.Empty(EquipSets.Terms(Wear(900), Lookup));
    }

    [Fact]
    public void TwoCompleteSetsAddUp()
    {
        EquipStats sum = EquipStats.Sum(EquipSets.Terms(Wear(940, 941, 942, 943, 944, 950, 951, 952), Lookup));
        Assert.Equal((100.0, 80.0, 40.0), (sum[EquipStat.HpMax], sum[EquipStat.PhysicalAttack], sum[EquipStat.MagicalAttack]));
    }

    [Fact]
    public void EmptyBlocksAreNotListedAsTerms()
    {
        var master = new EnhanceData(
            setMembers: new[] { 994, 995 },
            setFullBonus: EquipStats.Empty,
            setPieceBonuses: new[] { new SetPieceBonus(1, EquipStats.Empty), new SetPieceBonus(2, Stats(EquipStat.HpMax, 4)) });
        ItemInfo a = Gear(994, EquipmentParts.Coat, enhance: master);
        ItemInfo b = Gear(995, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 994));
        IReadOnlyList<EquipStats> terms = EquipSets.Terms(new List<ItemInfo> { a, b }, c => c == 994 ? a : null);
        Assert.Single(terms);
        Assert.Equal(4.0, terms[0][EquipStat.HpMax]);
    }

    [Fact]
    public void TermsWorkWithTheMasterWornAndNoLookup()
    {
        IReadOnlyList<EquipStats> terms = EquipSets.Terms(WearMask(0b11111), null);
        Assert.Equal(3, terms.Count);
    }

    [Fact]
    public void TermsAreEmptyWhenTheMasterCannotBeResolved()
    {
        Assert.Empty(EquipSets.Terms(Wear(941, 942, 943, 944), _ => null));
    }

    [Fact]
    public void TheStateReportsItsTiersInAscendingOrder()
    {
        EquipSetState state = Assert.Single(EquipSets.Evaluate(WearMask(0b11111), Lookup));
        Assert.Equal(new[] { 3, 5 }, state.ActiveTiers);
        Assert.True(state.HasTier(5));
        Assert.False(state.HasTier(0));
    }
}
