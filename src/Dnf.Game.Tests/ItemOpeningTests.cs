using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 开启类道具(随机箱 <c>[booster]</c> 与固定礼包 <c>[usable cera package]</c>)的数据读法与开启流程:脚本里 <c>[booster info]</c> / <c>[package data]</c> 的块、
/// 按权重抽取(分布边界:必出、必不出、权重 0)、开启的原子性(任何一道检查不过都不扣、不发、不起冷却;过了扣一件并发对的东西)。
/// 编号、权重、数量全是合成的(9000xx 是箱子,9010xx 是奖励),期望值是手算;与原版数据无关。
/// </summary>
public class ItemOpeningTests
{
    // ------------------------------------------------------------------ 工具

    private static readonly int BoosterType = StackableTypes.FromTag("[booster]");
    private static readonly int PackageType = StackableTypes.FromTag("[usable cera package]");

    private static ItemInfo Stack(int code, int type, int limit = 100, int coolMs = 0, int coolGroup = -1, int minLevel = 0)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = type,
            StackLimit = limit,
            CoolTimeMs = coolMs,
            CoolGroup = coolGroup,
            MinimumLevel = minLevel,
        };
    }

    private static ItemInfo Reward(int code, int limit = 100) => Stack(code, StackableTypes.Etc, limit);

    private static ItemInfo Gear(int code)
    {
        return new ItemInfo(code, "equipment/test/e" + code + ".equ", ItemKind.Equipment) { Part = EquipmentParts.Coat };
    }

    private static BoosterBlock Block(BoosterKind kind, int draws, params (int Code, int Weight, int Count)[] entries)
    {
        return new BoosterBlock(kind, draws, entries.Select(e => new BoosterEntry(e.Code, e.Weight, e.Count)));
    }

    private static ItemInfo Box(int code, int coolMs = 0, int coolGroup = -1, params BoosterBlock[] blocks)
    {
        ItemInfo box = Stack(code, BoosterType, 1000, coolMs, coolGroup);
        box.Contents = new ItemContents(blocks, null, false);
        return box;
    }

    private static ItemInfo Package(int code, params (int Code, int Count)[] entries)
    {
        ItemInfo box = Stack(code, PackageType, 1000);
        box.Contents = new ItemContents(null, entries.Select(e => new PackageEntry(e.Code, e.Count)).ToList(), false);
        return box;
    }

    /// <summary>按次序给出抽到的数,并记下每次问的上界;给完以后一直给 0。</summary>
    private sealed class Dice
    {
        private readonly Queue<int> _values;

        public Dice(params int[] values)
        {
            _values = new Queue<int>(values);
        }

        public List<int> Maxes { get; } = new List<int>();

        public int Roll(int max)
        {
            Maxes.Add(max);
            return _values.Count > 0 ? _values.Dequeue() : 0;
        }
    }

    private sealed class Scenario
    {
        public Scenario(params int[] rolls)
        {
            Dice = new Dice(rolls);
            Opener = new ItemOpener(code => Catalog.TryGetValue(code, out ItemInfo info) ? info : null, Dice.Roll);
        }

        public Inventory Bag { get; set; } = new Inventory();

        public ItemCooldowns Cooldowns { get; } = new ItemCooldowns();

        public Dictionary<int, ItemInfo> Catalog { get; } = new Dictionary<int, ItemInfo>();

        public Dice Dice { get; }

        public ItemOpener Opener { get; }

        public List<(int Code, int Count)> Given { get; } = new List<(int, int)>();

        public int Job { get; set; } = EquipRules.SwordmanJob;

        public int Level { get; set; } = 50;

        public ItemPlaces Place { get; set; } = ItemPlaces.Town;

        public void Know(params ItemInfo[] items)
        {
            foreach (ItemInfo item in items)
            {
                Catalog[item.Code] = item;
            }
        }

        public void Put(ItemInfo item, int count)
        {
            Know(item);
            Assert.Equal(count, Bag.Add(item, count));
        }

        public OpenResult Open(ItemInfo box)
        {
            return Opener.Open(Bag, Cooldowns, box, Job, Level, Place, (info, count) =>
            {
                Given.Add((info.Code, count));
                Bag.Add(info, count);
            });
        }

        /// <summary>背包里每一格(页签、编号、数量)加上有几个冷却在走:前后比较"什么都没动"。</summary>
        public string Snapshot()
        {
            var parts = new List<string>();
            for (int t = 0; t < Inventory.TabCount; t++)
            {
                foreach (ItemStack stack in Bag.Tab((InventoryTab)t))
                {
                    parts.Add(t + ":" + stack.Info.Code + "x" + stack.Count);
                }
            }
            return string.Join(",", parts) + "|cd" + Cooldowns.Active;
        }
    }

    private static ItemInfo ParseStackable(TokenStreamBuilder b, ItemKind kind = ItemKind.Stackable)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, kind, 900001, "stackable/test/box.stk");
    }

    // ------------------------------------------------------------------ 脚本:[booster info]

    [Fact]
    public void BoosterInfoBlocksAreReadWithKindDrawsAndEntries()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[stackable type]").Str("[booster]").Int(0);
        b.Tag("[booster info]");
        b.Tag("[etc]").Ints(2, 901001, 300, 1, 901002, 700, 5).Tag("[/etc]");
        b.Tag("[stackable]").Ints(1, 901003, 1000, 10).Tag("[/stackable]");
        b.Tag("[/booster info]");
        ItemInfo box = ParseStackable(b);

        Assert.NotNull(box.Contents);
        Assert.False(box.Contents.Malformed);
        Assert.False(box.Contents.HasPackage);
        Assert.Equal(2, box.Contents.Boosters.Count);
        BoosterBlock first = box.Contents.Boosters[0];
        Assert.Equal((BoosterKind.Etc, 2, 1000L), (first.Kind, first.Draws, first.TotalWeight));
        Assert.Equal(new[] { (901001, 300, 1), (901002, 700, 5) }, first.Entries.Select(e => (e.Code, e.Weight, e.Count)).ToArray());
        BoosterBlock second = box.Contents.Boosters[1];
        Assert.Equal((BoosterKind.Stackable, 1, 1000L), (second.Kind, second.Draws, second.TotalWeight));
        Assert.Equal((901003, 1000, 10), (second.Entries[0].Code, second.Entries[0].Weight, second.Entries[0].Count));
    }

    [Fact]
    public void AvatarKindsCarryTwoExtraIntegersPerEntryWhichAreIgnored()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[booster info]");
        b.Tag("[avatar]").Ints(1, 901011, 40, 1, 7, 8, 901012, 60, 1, 9, 10).Tag("[/avatar]");
        b.Tag("[special avatar]").Ints(1, 901013, 100, 1, 0, 0).Tag("[/special avatar]");
        b.Tag("[/booster info]");
        ItemInfo box = ParseStackable(b);

        Assert.False(box.Contents.Malformed);
        Assert.Equal(new[] { (901011, 40, 1), (901012, 60, 1) }, box.Contents.Boosters[0].Entries.Select(e => (e.Code, e.Weight, e.Count)).ToArray());
        Assert.Equal(BoosterKind.SpecialAvatar, box.Contents.Boosters[1].Kind);
        Assert.Single(box.Contents.Boosters[1].Entries);
    }

    [Fact]
    public void RepeatedBlocksOfTheSameKindAreKeptSeparatelyInScriptOrder()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[booster info]");
        b.Tag("[etc]").Ints(1, 901001, 1, 1).Tag("[/etc]");
        b.Tag("[etc]").Ints(3, 901002, 5, 2).Tag("[/etc]");
        b.Tag("[equipment]").Ints(1, 901003, 1, 1).Tag("[/equipment]");
        b.Tag("[/booster info]");
        ItemInfo box = ParseStackable(b);

        Assert.Equal(new[] { BoosterKind.Etc, BoosterKind.Etc, BoosterKind.Equipment }, box.Contents.Boosters.Select(x => x.Kind).ToArray());
        Assert.Equal(new[] { 1, 3, 1 }, box.Contents.Boosters.Select(x => x.Draws).ToArray());
        Assert.Equal(901002, box.Contents.Boosters[1].Entries[0].Code);
    }

    [Fact]
    public void ABlockWithOnlyADrawCountIsValidAndEmpty()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[booster info]");
        b.Tag("[etc]").Ints(2).Tag("[/etc]");
        b.Tag("[/booster info]");
        ItemInfo box = ParseStackable(b);

        Assert.False(box.Contents.Malformed);
        Assert.Single(box.Contents.Boosters);
        Assert.Empty(box.Contents.Boosters[0].Entries);
        Assert.Equal(0L, box.Contents.Boosters[0].TotalWeight);
    }

    [Fact]
    public void EveryKindTagMapsToTheServersKindNumber()
    {
        string[] tags = { "[avatar]", "[special avatar]", "[cera]", "[creature]", "[equipment]", "[stackable]", "[etc]", "[special creature]", "[emblem]" };
        for (int i = 0; i < tags.Length; i++)
        {
            var b = new TokenStreamBuilder();
            b.Tag("[booster info]");
            b.Tag(tags[i]);
            if (i <= 1)
            {
                b.Ints(1, 901001, 10, 1, 0, 0);
            }
            else
            {
                b.Ints(1, 901001, 10, 1);
            }
            b.Tag("[/booster info]");
            ItemInfo box = ParseStackable(b);
            Assert.Equal((BoosterKind)i, box.Contents.Boosters.Single().Kind);
            Assert.Equal(i, (int)box.Contents.Boosters.Single().Kind);
        }
    }

    [Fact]
    public void TagNamesAreMatchedIgnoringCase()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[Booster Info]");
        b.Tag("[ETC]").Ints(1, 901001, 10, 1).Tag("[/ETC]");
        b.Tag("[/Booster Info]");
        b.Tag("[Package Data]").Ints(901002, 3);
        b.Tag("[/Package Data]");
        ItemInfo box = ParseStackable(b);

        Assert.Single(box.Contents.Boosters);
        Assert.Equal(new[] { new PackageEntry(901002, 3) }, box.Contents.Package.ToArray());
    }

    [Fact]
    public void ABadBlockMarksTheContentsMalformedButItsNeighboursAreStillRead()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[booster info]");
        b.Tag("[etc]").Ints(1, 901001, 10).Tag("[/etc]");                 // 少一个整数:长度对不上
        b.Tag("[stackable]").Ints(1, 901002, 10, 1).Tag("[/stackable]");
        b.Tag("[/booster info]");
        ItemInfo box = ParseStackable(b);

        Assert.True(box.Contents.Malformed);
        Assert.Single(box.Contents.Boosters);
        Assert.Equal(BoosterKind.Stackable, box.Contents.Boosters[0].Kind);
    }

    [Fact]
    public void AnUnknownKindOrAForeignTokenInsideABlockMakesItMalformed()
    {
        var unknown = new TokenStreamBuilder();
        unknown.Tag("[booster info]");
        unknown.Tag("[gold]").Ints(1, 901001, 10, 1).Tag("[/gold]");
        unknown.Tag("[/booster info]");
        Assert.True(ParseStackable(unknown).Contents.Malformed);

        var foreign = new TokenStreamBuilder();
        foreign.Tag("[booster info]");
        foreign.Tag("[etc]").Int(1).Str("oops").Int(901001).Int(10).Int(1).Tag("[/etc]");
        foreign.Tag("[/booster info]");
        ItemInfo box = ParseStackable(foreign);
        Assert.True(box.Contents.Malformed);
        Assert.Empty(box.Contents.Boosters);

        var avatarShort = new TokenStreamBuilder();                       // avatar 一类每条要 5 个整数,只给 3 个
        avatarShort.Tag("[booster info]");
        avatarShort.Tag("[avatar]").Ints(1, 901001, 10, 1).Tag("[/avatar]");
        avatarShort.Tag("[/booster info]");
        Assert.True(ParseStackable(avatarShort).Contents.Malformed);
    }

    // ------------------------------------------------------------------ 脚本:[package data]

    [Fact]
    public void PackageDataIsReadAsFixedPairsInOrder()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[stackable type]").Str("[usable cera package]").Int(0);
        b.Tag("[package data]").Ints(901001, 1, 901002, 10, 901003, 100).Tag("[/package data]");
        ItemInfo box = ParseStackable(b);

        Assert.False(box.Contents.Malformed);
        Assert.True(box.Contents.HasPackage);
        Assert.False(box.Contents.HasBooster);
        Assert.Equal(new[] { new PackageEntry(901001, 1), new PackageEntry(901002, 10), new PackageEntry(901003, 100) }, box.Contents.Package.ToArray());
    }

    [Fact]
    public void AnOddNumberOfPackageIntegersIsMalformed()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[package data]").Ints(901001, 1, 901002).Tag("[/package data]");
        ItemInfo box = ParseStackable(b);

        Assert.True(box.Contents.Malformed);
        Assert.False(box.Contents.HasPackage);
    }

    [Fact]
    public void ScriptsWithoutBoxDataHaveNoContentsAndEquipmentNeverDoes()
    {
        var plain = new TokenStreamBuilder();
        plain.Tag("[stackable type]").Str("[material]").Int(0);
        plain.Tag("[price]").Int(5);
        Assert.Null(ParseStackable(plain).Contents);

        var gear = new TokenStreamBuilder();
        gear.Tag("[equipment type]").Str("[coat]").Int(0);
        gear.Tag("[booster info]");
        gear.Tag("[etc]").Ints(1, 901001, 10, 1).Tag("[/etc]");
        gear.Tag("[/booster info]");
        Assert.Null(ParseStackable(gear, ItemKind.Equipment).Contents);
    }

    [Fact]
    public void ReadingContentsDoesNotDisturbTheOtherFieldsOfTheItem()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[stackable type]").Str("[booster]").Int(0);
        b.Tag("[minimum level]").Int(30);
        b.Tag("[cool time]").Int(1000);
        b.Tag("[booster info]");
        b.Tag("[etc]").Ints(1, 901001, 10, 1).Tag("[/etc]");
        b.Tag("[/booster info]");
        b.Tag("[price]").Int(77);
        ItemInfo box = ParseStackable(b);

        Assert.Equal((BoosterType, 30, 1000, 77), (box.StackableType, box.MinimumLevel, box.CoolTimeMs, box.Price));
        Assert.Equal(ItemUseClass.OpenBox, ItemUseTable.ClassOf(box));
        Assert.False(box.Contents.Malformed);                             // [/booster info] 之后的 [price] 不会被当成块的种类
        Assert.Single(box.Contents.Boosters);
    }

    [Fact]
    public void AnIntegerWrittenAsAnUnsignedTokenIsReadLikeAnInt()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[booster info]");
        b.Tag("[etc]").Int(1).UInt(901001).UInt(300).Int(2).Tag("[/etc]");
        b.Tag("[/booster info]");
        ItemInfo box = ParseStackable(b);

        Assert.False(box.Contents.Malformed);
        Assert.Equal((901001, 300, 2), (box.Contents.Boosters.Single().Entries[0].Code, box.Contents.Boosters.Single().Entries[0].Weight, box.Contents.Boosters.Single().Entries[0].Count));
    }

    // ------------------------------------------------------------------ 权重:IndexAt

    [Fact]
    public void IndexAtPicksTheFirstEntryWhoseCumulativeWeightExceedsTheRoll()
    {
        BoosterBlock block = Block(BoosterKind.Etc, 1, (901001, 3, 1), (901002, 2, 1), (901003, 5, 1));     // 累计 3、5、10
        Assert.Equal(10L, block.TotalWeight);
        int[] expected = { 0, 0, 0, 1, 1, 2, 2, 2, 2, 2 };
        for (int r = 0; r < expected.Length; r++)
        {
            Assert.Equal(expected[r], block.IndexAt(r));
        }
        Assert.Equal(-1, block.IndexAt(10));                        // 总和本身抽不到
        Assert.Equal(-1, block.IndexAt(11));
        Assert.Equal(-1, block.IndexAt(-1));
        Assert.Equal(-1, block.IndexAt(long.MaxValue));
    }

    [Fact]
    public void EnumeratingEveryRollGivesEachEntryExactlyItsWeight()
    {
        BoosterBlock block = Block(BoosterKind.Etc, 1, (901001, 7, 1), (901002, 0, 1), (901003, 3, 1), (901004, 12, 1));
        var tally = new int[4];
        for (int r = 0; r < block.TotalWeight; r++)
        {
            tally[block.IndexAt(r)]++;
        }
        Assert.Equal(new[] { 7, 0, 3, 12 }, tally);
    }

    [Fact]
    public void AZeroWeightEntryIsNeverPickedEvenOnTheFirstRoll()
    {
        BoosterBlock zeroFirst = Block(BoosterKind.Etc, 1, (901001, 0, 1), (901002, 4, 1));
        for (int r = 0; r < 4; r++)
        {
            Assert.Equal(1, zeroFirst.IndexAt(r));
        }
        BoosterBlock zeroLast = Block(BoosterKind.Etc, 1, (901001, 4, 1), (901002, 0, 1));
        for (int r = -1; r < 8; r++)
        {
            Assert.NotEqual(1, zeroLast.IndexAt(r));
        }
    }

    [Fact]
    public void ASingleEntryWhoseWeightIsTheTotalIsAlwaysPicked()
    {
        BoosterBlock always = Block(BoosterKind.Etc, 1, (901001, 1000, 1));
        Assert.Equal(0, always.IndexAt(0));
        Assert.Equal(0, always.IndexAt(999));
        Assert.Equal(-1, always.IndexAt(1000));
        BoosterBlock tiny = Block(BoosterKind.Etc, 1, (901001, 1, 1));              // 权重是相对值,不要求等于 1000
        Assert.Equal(0, tiny.IndexAt(0));
        Assert.Equal(1L, tiny.TotalWeight);
    }

    [Fact]
    public void WeightsAreRelativeAndNeedNotSumToOneThousand()
    {
        BoosterBlock over = Block(BoosterKind.Etc, 1, (901001, 1500, 1), (901002, 500, 1));
        Assert.Equal(2000L, over.TotalWeight);
        Assert.Equal(0, over.IndexAt(1499));
        Assert.Equal(1, over.IndexAt(1500));
        Assert.Equal(1, over.IndexAt(1999));
        BoosterBlock under = Block(BoosterKind.Etc, 1, (901001, 1, 1), (901002, 1, 1));
        Assert.Equal((0, 1), (under.IndexAt(0), under.IndexAt(1)));
    }

    [Fact]
    public void WithoutAnyWeightNothingIsPicked()
    {
        Assert.Equal(-1, Block(BoosterKind.Etc, 1).IndexAt(0));
        BoosterBlock allZero = Block(BoosterKind.Etc, 1, (901001, 0, 1), (901002, 0, 1));
        Assert.Equal(0L, allZero.TotalWeight);
        Assert.Equal(-1, allZero.IndexAt(0));
    }

    [Fact]
    public void ANegativeWeightCountsAsZeroInsteadOfRewindingTheCumulativeSum()
    {
        BoosterBlock block = Block(BoosterKind.Etc, 1, (901001, 5, 1), (901002, -3, 1), (901003, 5, 1));
        Assert.Equal(10L, block.TotalWeight);
        Assert.Equal(0, block.IndexAt(4));
        Assert.Equal(2, block.IndexAt(5));
        Assert.Equal(2, block.IndexAt(9));
    }

    [Theory]
    [InlineData(BoosterKind.Avatar, true)]
    [InlineData(BoosterKind.SpecialAvatar, true)]
    [InlineData(BoosterKind.Cera, false)]
    [InlineData(BoosterKind.Creature, false)]
    [InlineData(BoosterKind.Equipment, true)]
    [InlineData(BoosterKind.Stackable, true)]
    [InlineData(BoosterKind.Etc, true)]
    [InlineData(BoosterKind.SpecialCreature, false)]
    [InlineData(BoosterKind.Emblem, true)]
    public void OnlyTheKindsThisVersionCanGrantAreGrantable(BoosterKind kind, bool grantable)
    {
        Assert.Equal(grantable, BoosterBlock.IsGrantable(kind));
    }

    // ------------------------------------------------------------------ 抽取:Draw

    [Fact]
    public void EachDrawRollsOnceWithTheBlockTotalWeightAsTheBound()
    {
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 2, (901001, 30, 1), (901002, 70, 2)));
        var dice = new Dice(0, 99);
        List<PackageEntry> picked = ItemOpener.Draw(box, dice.Roll);

        Assert.Equal(new[] { 100, 100 }, dice.Maxes.ToArray());
        Assert.Equal(new[] { new PackageEntry(901001, 1), new PackageEntry(901002, 2) }, picked.ToArray());
    }

    [Fact]
    public void DrawsAreIndependentAndWithReplacement()
    {
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 3, (901001, 1, 1), (901002, 1, 1)));
        List<PackageEntry> picked = ItemOpener.Draw(box, new Dice(1, 1, 0).Roll);

        Assert.Equal(new[] { 901002, 901002, 901001 }, picked.Select(p => p.Code).ToArray());
    }

    [Fact]
    public void EveryBlockDrawsItsOwnTimesInScriptOrder()
    {
        ItemInfo box = Box(900001, 0, -1,
            Block(BoosterKind.Etc, 1, (901001, 5, 1), (901002, 5, 1)),
            Block(BoosterKind.Equipment, 2, (901003, 1, 1)),
            Block(BoosterKind.Etc, 1, (901004, 9, 3), (901005, 1, 4)));
        var dice = new Dice(5, 0, 0, 9);
        List<PackageEntry> picked = ItemOpener.Draw(box, dice.Roll);

        Assert.Equal(new[] { 10, 1, 1, 10 }, dice.Maxes.ToArray());
        Assert.Equal(new[] { (901002, 1), (901003, 1), (901003, 1), (901005, 4) }, picked.Select(p => (p.Code, p.Count)).ToArray());
    }

    [Fact]
    public void ABlockWithoutWeightOrDrawsConsumesNoRoll()
    {
        ItemInfo box = Box(900001, 0, -1,
            Block(BoosterKind.Etc, 2, (901001, 0, 1)),
            Block(BoosterKind.Etc, 0, (901002, 5, 1)),
            Block(BoosterKind.Etc, 3),
            Block(BoosterKind.Etc, -4, (901003, 5, 1)));
        var dice = new Dice();
        List<PackageEntry> picked = ItemOpener.Draw(box, dice.Roll);

        Assert.Empty(dice.Maxes);
        Assert.Empty(picked);
    }

    [Fact]
    public void AnEntryWithoutACountGivesNothing()
    {
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 5, 0), (901002, 5, -2)));
        Assert.Empty(ItemOpener.Draw(box, new Dice(0).Roll));
        Assert.Empty(ItemOpener.Draw(box, new Dice(7).Roll));
    }

    [Fact]
    public void ARollOutsideTheBlockPicksNothingInsteadOfFallingBackToAnEntry()
    {
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 2, (901001, 0, 1), (901002, 5, 1)));
        Assert.Empty(ItemOpener.Draw(box, new Dice(-1, 5).Roll));
    }

    [Fact]
    public void TheDrawCountIsCappedForSafety()
    {
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, int.MaxValue, (901001, 1, 1)));
        var dice = new Dice();
        List<PackageEntry> picked = ItemOpener.Draw(box, dice.Roll);

        Assert.Equal(ItemOpener.MaxDrawsPerBlock, picked.Count);
        Assert.Equal(ItemOpener.MaxDrawsPerBlock, dice.Maxes.Count);
    }

    [Fact]
    public void ATotalWeightBeyondTheIntRangeIsClampedToTheRollBound()
    {
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, int.MaxValue, 1), (901002, int.MaxValue, 1)));
        var dice = new Dice(int.MaxValue - 1);
        List<PackageEntry> picked = ItemOpener.Draw(box, dice.Roll);

        Assert.Equal(new[] { int.MaxValue }, dice.Maxes.ToArray());
        Assert.Equal(901001, picked.Single().Code);
    }

    [Fact]
    public void APackageGivesEverythingInScriptOrderWithoutRolling()
    {
        ItemInfo pack = Package(900002, (901001, 1), (901002, 10), (901003, 100));
        var dice = new Dice();
        List<PackageEntry> picked = ItemOpener.Draw(pack, dice.Roll);

        Assert.Empty(dice.Maxes);
        Assert.Equal(new[] { (901001, 1), (901002, 10), (901003, 100) }, picked.Select(p => (p.Code, p.Count)).ToArray());
    }

    [Fact]
    public void APackageEntryWithoutAPositiveCountGivesNothingAndTheRestStillComes()
    {
        ItemInfo pack = Package(900002, (901001, 0), (901002, 3), (901003, -1), (901004, 1));
        List<PackageEntry> picked = ItemOpener.Draw(pack, new Dice().Roll);

        Assert.Equal(new[] { (901002, 3), (901004, 1) }, picked.Select(p => (p.Code, p.Count)).ToArray());
    }

    [Fact]
    public void APackageWhoseEntriesAllGiveNothingIsRefusedAsNoEffectAndKept()
    {
        var s = new Scenario();
        ItemInfo pack = Package(900002, (901001, 0), (901002, -5));
        s.Put(pack, 1);
        s.Know(Reward(901001), Reward(901002));
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NoEffect, s.Open(pack));
    }

    [Fact]
    public void DrawingFromAPackageReturnsACopyNotTheScriptsList()
    {
        ItemInfo pack = Package(900002, (901001, 1));
        List<PackageEntry> picked = ItemOpener.Draw(pack, new Dice().Roll);
        picked.Clear();
        Assert.Single(pack.Contents.Package);
    }

    [Fact]
    public void ContentThatCannotBeGrantedHereDrawsNull()
    {
        RollSource roll = new Dice().Roll;
        Assert.Null(ItemOpener.Draw(null, roll));
        Assert.Null(ItemOpener.Draw(Stack(900001, BoosterType), roll));                                                    // 没有数据
        ItemInfo malformed = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1)));
        malformed.Contents = new ItemContents(malformed.Contents.Boosters, null, true);
        Assert.Null(ItemOpener.Draw(malformed, roll));                                                                     // 数据有坏
        Assert.Null(ItemOpener.Draw(Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1)), Block(BoosterKind.Cera, 1, (1, 1, 100))), roll));
        Assert.Null(ItemOpener.Draw(Box(900001, 0, -1, Block(BoosterKind.Creature, 1, (901002, 1, 1))), roll));
        Assert.Null(ItemOpener.Draw(Box(900001, 0, -1, Block(BoosterKind.SpecialCreature, 1, (901002, 1, 1))), roll));
        Assert.Null(ItemOpener.Draw(Box(900001, 0, -1), roll));                                                            // 随机箱没有块
        ItemInfo noPackage = Stack(900002, PackageType);
        noPackage.Contents = new ItemContents(new[] { Block(BoosterKind.Etc, 1, (901001, 1, 1)) }, null, false);
        Assert.Null(ItemOpener.Draw(noPackage, roll));                                                                     // 礼包没有 [package data]
        ItemInfo notAnOpener = Stack(900003, StackableTypes.Etc);
        notAnOpener.Contents = new ItemContents(new[] { Block(BoosterKind.Etc, 1, (901001, 1, 1)) }, null, false);
        Assert.Null(ItemOpener.Draw(notAnOpener, roll));                                                                   // 不是开启类
    }

    [Fact]
    public void AHalfGrantableBoxIsRefusedWholeNotSkippedBlockByBlock()
    {
        var dice = new Dice();
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Cera, 1, (1, 1, 100)), Block(BoosterKind.Etc, 1, (901001, 1, 1)));
        Assert.Null(ItemOpener.Draw(box, dice.Roll));
        Assert.Empty(dice.Maxes);
    }

    // ------------------------------------------------------------------ 开启:成功

    [Fact]
    public void OpeningTakesOneBoxAndGivesExactlyTheDrawnItems()
    {
        var s = new Scenario(1, 0);                                   // 第一次抽到 901002,第二次(下一块)抽到 901003
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1), (901002, 1, 5)), Block(BoosterKind.Etc, 1, (901003, 4, 2)));
        s.Put(box, 2);
        s.Know(Reward(901001), Reward(901002), Reward(901003));

        OpenResult r = s.Open(box);

        Assert.Equal(UseResult.Ok, r.Result);
        Assert.True(r.Ok);
        Assert.Equal(new[] { (901002, 5), (901003, 2) }, r.Grants.Select(g => (g.Info.Code, g.Count)).ToArray());
        Assert.Equal(new[] { (901002, 5), (901003, 2) }, s.Given.ToArray());
        Assert.Equal((1, 5, 2, 0), (s.Bag.CountOf(900001), s.Bag.CountOf(901002), s.Bag.CountOf(901003), s.Bag.CountOf(901001)));
    }

    [Fact]
    public void TheItemGivenIsTheOneTheRollSelectedAtEveryBoundary()
    {
        // 权重 3、2、5(累计 3、5、10):在每个边界上抽到的数,发出去的就是对应那一条
        int[] expectedCode = { 901001, 901001, 901001, 901002, 901002, 901003, 901003, 901003, 901003, 901003 };
        for (int roll = 0; roll < 10; roll++)
        {
            var s = new Scenario(roll);
            ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 3, 1), (901002, 2, 1), (901003, 5, 1)));
            s.Put(box, 1);
            s.Know(Reward(901001), Reward(901002), Reward(901003));

            OpenResult r = s.Open(box);

            Assert.Equal(UseResult.Ok, r.Result);
            Assert.Equal(expectedCode[roll], r.Grants.Single().Info.Code);
            Assert.Equal(1, s.Bag.CountOf(expectedCode[roll]));
            Assert.Equal(1, s.Bag.TotalCount);
        }
    }

    [Fact]
    public void AGuaranteedEntryIsGivenWhateverTheRollAndAZeroWeightEntryNever()
    {
        for (int roll = 0; roll < 1000; roll += 111)
        {
            var s = new Scenario(roll);
            ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 0, 1), (901002, 1000, 1)));
            s.Put(box, 1);
            s.Know(Reward(901001), Reward(901002));

            Assert.Equal(UseResult.Ok, s.Open(box).Result);
            Assert.Equal((0, 1), (s.Bag.CountOf(901001), s.Bag.CountOf(901002)));
        }
    }

    [Fact]
    public void OpeningAPackageGivesEveryEntryAndNeedsNoRoll()
    {
        var s = new Scenario();
        ItemInfo pack = Package(900002, (901001, 1), (901002, 10), (901003, 3));
        s.Put(pack, 1);
        s.Know(Reward(901001), Reward(901002), Reward(901003));

        OpenResult r = s.Open(pack);

        Assert.Equal(UseResult.Ok, r.Result);
        Assert.Empty(s.Dice.Maxes);
        Assert.Equal((0, 1, 10, 3), (s.Bag.CountOf(900002), s.Bag.CountOf(901001), s.Bag.CountOf(901002), s.Bag.CountOf(901003)));
        Assert.Equal(3, r.Grants.Count);
    }

    [Fact]
    public void ASecondDrawOfTheSameItemIsGivenAgainAndStacks()
    {
        var s = new Scenario(0, 0);
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 2, (901001, 1, 3)));
        s.Put(box, 1);
        s.Know(Reward(901001));

        OpenResult r = s.Open(box);

        Assert.Equal(2, r.Grants.Count);
        Assert.Equal(6, s.Bag.CountOf(901001));
        Assert.Equal(1, s.Bag.StackCount);
    }

    [Fact]
    public void OnlyOneBoxIsTakenFromAStackOfSeveral()
    {
        var s = new Scenario(0);
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1)));
        s.Put(box, 3);
        s.Know(Reward(901001));

        Assert.Equal(UseResult.Ok, s.Open(box).Result);
        Assert.Equal((2, 1), (s.Bag.CountOf(900001), s.Bag.CountOf(901001)));
    }

    [Fact]
    public void EquipmentRewardsTakeOneSlotPerPiece()
    {
        var s = new Scenario(0);
        ItemInfo pack = Package(900002, (901101, 3));
        ItemInfo coat = Gear(901101);
        s.Put(pack, 1);
        s.Know(coat);

        Assert.Equal(UseResult.Ok, s.Open(pack).Result);
        Assert.Equal(3, s.Bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal(3, s.Bag.CountOf(901101));
    }

    [Fact]
    public void TheRewardsGoThroughTheGiveCallbackInOrderAndNotAroundIt()
    {
        var s = new Scenario(0);
        ItemInfo pack = Package(900002, (901001, 2), (901002, 1));
        s.Put(pack, 1);
        s.Know(Reward(901001), Reward(901002));
        var seen = new List<(int, int)>();

        OpenResult r = s.Opener.Open(s.Bag, s.Cooldowns, pack, s.Job, s.Level, s.Place, (info, count) => seen.Add((info.Code, count)));

        Assert.Equal(UseResult.Ok, r.Result);
        Assert.Equal(new[] { (901001, 2), (901002, 1) }, seen.ToArray());
        Assert.Equal(0, s.Bag.CountOf(901001));                          // 放进背包是回调的事,这里的回调没放
        Assert.Equal(0, s.Bag.CountOf(900002));
    }

    [Fact]
    public void WithoutAGiveCallbackTheRewardsGoStraightIntoTheBag()
    {
        var s = new Scenario(0);
        ItemInfo pack = Package(900002, (901001, 2));
        s.Put(pack, 1);
        s.Know(Reward(901001));

        Assert.Equal(UseResult.Ok, s.Opener.Open(s.Bag, s.Cooldowns, pack, s.Job, s.Level, s.Place).Result);
        Assert.Equal(2, s.Bag.CountOf(901001));
    }

    // ------------------------------------------------------------------ 开启:冷却

    [Fact]
    public void OpeningStartsTheSharedCooldownAndAnImmediateSecondOpenIsRefused()
    {
        var s = new Scenario(0, 0);
        ItemInfo box = Box(900001, 1000, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1)));
        s.Put(box, 2);
        s.Know(Reward(901001));

        Assert.Equal(UseResult.Ok, s.Open(box).Result);
        Assert.True(s.Cooldowns.IsCooling(box));
        string before = s.Snapshot();
        Assert.Equal(UseResult.Cooling, s.Open(box).Result);
        Assert.Equal(before, s.Snapshot());
        s.Cooldowns.Advance(1000);
        Assert.Equal(UseResult.Ok, s.Open(box).Result);
        Assert.Equal((0, 2), (s.Bag.CountOf(900001), s.Bag.CountOf(901001)));
    }

    [Fact]
    public void AnOpenedBoxSharesItsCooldownGroupWithAPotionOfTheSameGroup()
    {
        var s = new Scenario(0);
        ItemInfo box = Box(900001, 1000, 7, Block(BoosterKind.Etc, 1, (901001, 1, 1)));
        ItemInfo potion = Stack(900050, StackableTypes.Waste, 100, 500, 7);
        s.Put(box, 1);
        s.Know(Reward(901001), potion);

        Assert.Equal(UseResult.Ok, s.Open(box).Result);
        Assert.True(s.Cooldowns.IsCooling(potion));
    }

    [Fact]
    public void ABoxWithoutACooldownTimeStartsNoCooldown()
    {
        var s = new Scenario(0);
        ItemInfo box = Box(900001, 0, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1)));
        s.Put(box, 1);
        s.Know(Reward(901001));

        Assert.Equal(UseResult.Ok, s.Open(box).Result);
        Assert.Equal(0, s.Cooldowns.Active);
    }

    // ------------------------------------------------------------------ 开启:失败不动

    private static Scenario ReadyScenario(out ItemInfo box)
    {
        var s = new Scenario(0);
        box = Box(900001, 1000, -1, Block(BoosterKind.Etc, 1, (901001, 1, 2)));
        s.Put(box, 2);
        s.Put(Reward(900777), 5);                                      // 背包里本来就有别的东西,前后比较才有意义
        s.Know(Reward(901001));
        return s;
    }

    private static void AssertUntouched(Scenario s, string before, UseResult expected, OpenResult r, int rolls = 0)
    {
        Assert.Equal(expected, r.Result);
        Assert.Empty(r.Grants);
        Assert.Equal(before, s.Snapshot());
        Assert.Empty(s.Given);
        Assert.Equal(rolls, s.Dice.Maxes.Count);
    }

    [Fact]
    public void AnItemThatIsNotAnOpeningClassIsRefusedAndNothingChanges()
    {
        Scenario s = ReadyScenario(out ItemInfo _);
        ItemInfo potion = Stack(900050, StackableTypes.Waste);
        ItemInfo material = Stack(900051, StackableTypes.Material);
        s.Put(potion, 1);
        s.Put(material, 1);
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NotConsumable, s.Open(potion));
        AssertUntouched(s, before, UseResult.NotConsumable, s.Open(material));
        AssertUntouched(s, before, UseResult.NotConsumable, s.Open(Gear(901101)));
        AssertUntouched(s, before, UseResult.NotConsumable, s.Open(null));
    }

    [Fact]
    public void OnlyTheBoxAndPackageRowsAreOpeningClassesAndEveryOtherRowIsRefusedAsNotConsumable()
    {
        // 逐行问派发表:只有种类 16 与 38 算开启类;选择箱(34)、商城礼包(14)、多重箱(26、27)、抽奖券(13)这些登记过的行也一样,
        // 开启入口先按"不是开启类"拒绝(而不是后面的"此处不可用"),什么都不动。
        int opening = 0;
        foreach (ItemUseRow row in ItemUseTable.Rows)
        {
            ItemInfo item = Stack(900400 + row.StackableType, row.StackableType);
            bool expected = row.StackableType == 16 || row.StackableType == 38;
            Assert.Equal(expected, ItemUseTable.IsOpening(item));
            if (expected)
            {
                opening++;
                continue;
            }

            Scenario s = ReadyScenario(out ItemInfo _);
            s.Put(item, 1);
            string before = s.Snapshot();
            AssertUntouched(s, before, UseResult.NotConsumable, s.Open(item));
        }

        Assert.Equal(2, opening);
    }

    [Fact]
    public void OpeningInTheDungeonIsRefusedAndNothingChanges()
    {
        Scenario s = ReadyScenario(out ItemInfo box);
        s.Place = ItemPlaces.Dungeon;
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.Unavailable, s.Open(box));
    }

    [Fact]
    public void ALevelBelowTheMinimumRefusesAndNothingChanges()
    {
        Scenario s = ReadyScenario(out ItemInfo box);
        box.MinimumLevel = 51;
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.LevelTooLow, s.Open(box));
        s.Level = 51;
        Assert.Equal(UseResult.Ok, s.Open(box).Result);                  // 正好够就能开
    }

    [Fact]
    public void AJobNotListedRefusesAndNothingChanges()
    {
        Scenario s = ReadyScenario(out ItemInfo box);
        box.UsableJobs.Add("[mage]");
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.WrongJob, s.Open(box));
        s.Job = EquipRules.JobOf("[mage]");
        Assert.Equal(UseResult.Ok, s.Open(box).Result);
    }

    [Fact]
    public void LevelIsCheckedBeforeJobLikeThePotionPath()
    {
        Scenario s = ReadyScenario(out ItemInfo box);
        box.MinimumLevel = 99;
        box.UsableJobs.Add("[mage]");

        Assert.Equal(UseResult.LevelTooLow, s.Open(box).Result);
        Assert.Equal(ConsumableRules.CheckUser(box, s.Job, s.Level), s.Open(box).Result);
    }

    [Fact]
    public void ABoxThatIsNotInTheBagRefusesAndNothingChanges()
    {
        Scenario s = ReadyScenario(out ItemInfo box);
        s.Bag.Remove(box.Code, 2);
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NotInBag, s.Open(box));
    }

    [Fact]
    public void ACoolingBoxRefusesAndNothingChanges()
    {
        Scenario s = ReadyScenario(out ItemInfo box);
        s.Cooldowns.Start(box);
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.Cooling, s.Open(box));
    }

    [Fact]
    public void ContentThisVersionCannotGrantRefusesWithoutDeductingOrRolling()
    {
        Scenario s = ReadyScenario(out ItemInfo _);
        ItemInfo cera = Box(900010, 0, -1, Block(BoosterKind.Cera, 1, (1, 1, 100)));
        ItemInfo pet = Box(900011, 0, -1, Block(BoosterKind.Creature, 1, (901001, 1, 1)));
        ItemInfo bare = Stack(900012, BoosterType);
        foreach (ItemInfo b in new[] { cera, pet, bare })
        {
            s.Put(b, 1);
        }
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NotYet, s.Open(cera));
        AssertUntouched(s, before, UseResult.NotYet, s.Open(pet));
        AssertUntouched(s, before, UseResult.NotYet, s.Open(bare));
    }

    [Fact]
    public void AnEntryThatCannotBeFoundOnlyMattersWhenItIsDrawn()
    {
        ItemInfo box = Box(900001, 1000, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1), (999999, 1, 1)));
        var drawsTheKnownOne = new Scenario(0);
        drawsTheKnownOne.Put(box, 1);
        drawsTheKnownOne.Know(Reward(901001));
        Assert.Equal(UseResult.Ok, drawsTheKnownOne.Open(box).Result);

        var drawsTheMissingOne = new Scenario(1);
        drawsTheMissingOne.Put(box, 1);
        drawsTheMissingOne.Know(Reward(901001));
        string before = drawsTheMissingOne.Snapshot();
        AssertUntouched(drawsTheMissingOne, before, UseResult.NotYet, drawsTheMissingOne.Open(box), rolls: 1);
    }

    [Fact]
    public void APackageWithAnUnknownCodeIsRefusedWholeAndNothingChanges()
    {
        var s = new Scenario();
        ItemInfo pack = Package(900002, (901001, 1), (999998, 1));
        s.Put(pack, 1);
        s.Know(Reward(901001));
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NotYet, s.Open(pack));
    }

    [Fact]
    public void ADegenerateBoxThatWouldGiveNothingIsRefusedNotConsumed()
    {
        var s = new Scenario();
        ItemInfo box = Box(900001, 1000, -1, Block(BoosterKind.Etc, 1, (901001, 0, 1)), Block(BoosterKind.Etc, 0, (901001, 5, 1)));
        s.Put(box, 1);
        s.Know(Reward(901001));
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NoEffect, s.Open(box));
    }

    [Fact]
    public void NoRollIsConsumedWhenAnEarlierCheckFails()
    {
        Scenario s = ReadyScenario(out ItemInfo box);
        s.Place = ItemPlaces.Dungeon;
        s.Open(box);
        s.Place = ItemPlaces.Town;
        s.Level = 1;
        box.MinimumLevel = 2;
        s.Open(box);
        s.Level = 50;
        s.Cooldowns.Start(box);
        s.Open(box);

        Assert.Empty(s.Dice.Maxes);
    }

    // ------------------------------------------------------------------ 开启:几项都不满足时报哪一项

    [Fact]
    public void WhenSeveralChecksFailTheEarliestOneInTheDocumentedOrderIsTheOneReported()
    {
        // 同时不满足:地点不对、等级不够、职业不符、不在背包里、冷却中、内容本地发不了。每修好一项,报的就换成次序里的下一项,每一步都什么也不动。
        var s = new Scenario();
        ItemInfo box = Box(900001, 1000, -1, Block(BoosterKind.Cera, 1, (1, 1, 100)));
        box.MinimumLevel = 51;
        box.UsableJobs.Add("[mage]");
        s.Know(box, Reward(901001));
        s.Cooldowns.Start(box);
        s.Place = ItemPlaces.Dungeon;
        s.Level = 1;
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.Unavailable, s.Open(box));
        s.Place = ItemPlaces.Town;
        AssertUntouched(s, before, UseResult.LevelTooLow, s.Open(box));
        s.Level = 51;
        AssertUntouched(s, before, UseResult.WrongJob, s.Open(box));
        s.Job = EquipRules.JobOf("[mage]");
        AssertUntouched(s, before, UseResult.NotInBag, s.Open(box));

        s.Put(box, 1);
        before = s.Snapshot();
        AssertUntouched(s, before, UseResult.Cooling, s.Open(box));

        s.Cooldowns.Clear();
        before = s.Snapshot();
        AssertUntouched(s, before, UseResult.NotYet, s.Open(box));
    }

    [Fact]
    public void ANonOpeningItemIsRefusedAsNotConsumableWhateverElseIsWrongWithIt()
    {
        var s = new Scenario();
        ItemInfo potion = Stack(900050, StackableTypes.Waste, 100, 1000, -1, 99);
        potion.UsableJobs.Add("[mage]");
        s.Know(potion);                                                   // 不在背包里、冷却中、地点和等级职业都不对
        s.Cooldowns.Start(potion);
        s.Place = ItemPlaces.Dungeon;
        s.Level = 1;
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NotConsumable, s.Open(potion));
    }

    [Fact]
    public void AnUnknownCodeIsReportedBeforeAFullBagBecauseTheGrantsAreResolvedFirst()
    {
        var s = new Scenario();
        s.Bag = new Inventory(2);                                         // 每页只有 2 格
        ItemInfo pack = Package(900002, (901001, 1), (901002, 1), (901003, 1), (999998, 1));
        s.Put(pack, 1);
        s.Know(Reward(901001), Reward(901002), Reward(901003));
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.NotYet, s.Open(pack));        // 999998 认不得,先报这个
        s.Know(Reward(999998));
        AssertUntouched(s, before, UseResult.BagFull, s.Open(pack));       // 认得了,才轮到背包放不下
    }

    // ------------------------------------------------------------------ 开启:背包放不下

    [Fact]
    public void ABagThatCannotHoldEveryRewardRefusesWithoutGivingHalfOfThem()
    {
        var s = new Scenario();
        s.Bag = new Inventory(2);                                         // 每页只有 2 格
        ItemInfo pack = Package(900002, (901001, 1), (901002, 1), (901003, 1));
        s.Put(pack, 1);
        s.Know(Reward(901001), Reward(901002), Reward(901003));
        string before = s.Snapshot();

        OpenResult r = s.Open(pack);

        AssertUntouched(s, before, UseResult.BagFull, r);
        Assert.Equal(1, s.Bag.CountOf(900002));
    }

    /// <summary>落在消耗品页的奖励(和礼包、箱子同一页,占格子才会互相挤)。</summary>
    private static ItemInfo Tonic(int code, int limit = 100) => Stack(code, StackableTypes.Waste, limit);

    [Fact]
    public void TheBoxOwnSlotCountsAsFreeWhenItIsTheLastOneOfItsStack()
    {
        var s = new Scenario();
        s.Bag = new Inventory(1);                                         // 消耗品页只有 1 格,被这件礼包占着
        ItemInfo pack = Package(900002, (901201, 1));
        s.Put(pack, 1);
        s.Know(Tonic(901201));

        Assert.Equal(UseResult.Ok, s.Open(pack).Result);
        Assert.Equal((0, 1), (s.Bag.CountOf(900002), s.Bag.CountOf(901201)));
    }

    [Fact]
    public void WhenTheStackStaysTheBoxSlotIsNotFreeAndAFullPageRefuses()
    {
        var s = new Scenario();
        s.Bag = new Inventory(1);
        ItemInfo pack = Package(900002, (901201, 1));
        s.Put(pack, 2);                                                   // 剩一件,格子还占着
        s.Know(Tonic(901201));
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.BagFull, s.Open(pack));
    }

    [Fact]
    public void ARewardMergingIntoAnExistingStackNeedsNoNewSlot()
    {
        var s = new Scenario();
        s.Bag = new Inventory(2);
        ItemInfo pack = Package(900002, (901201, 5));
        s.Put(pack, 1);
        s.Put(Tonic(901201), 10);                                         // 消耗品页第二格;奖励并进去
        Assert.Equal(2, s.Bag.StackCount);

        Assert.Equal(UseResult.Ok, s.Open(pack).Result);
        Assert.Equal(15, s.Bag.CountOf(901201));
    }

    [Fact]
    public void AMergeThatOverflowsTheStackLimitNeedsANewSlotAndRefusesOnAFullPage()
    {
        var s = new Scenario();
        s.Bag = new Inventory(2);
        ItemInfo pack = Package(900002, (901201, 5));
        s.Put(pack, 2);                                                   // 剩一件,它的格子还占着
        s.Put(Tonic(901201, 12), 10);                                     // 10 + 5 超过叠加上限 12,多出来的要新格子
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.BagFull, s.Open(pack));
    }

    [Fact]
    public void EquipmentRewardsRefuseWhenTheEquipmentPageHasTooFewSlots()
    {
        var s = new Scenario();
        s.Bag = new Inventory(2);
        ItemInfo pack = Package(900002, (901101, 3));
        s.Put(pack, 1);
        s.Know(Gear(901101));
        string before = s.Snapshot();

        AssertUntouched(s, before, UseResult.BagFull, s.Open(pack));
    }

    // ------------------------------------------------------------------ 随机源与参数检查

    [Fact]
    public void RollFromAskRandomForANumberBelowTheBoundAndReachesEveryValue()
    {
        RollSource roll = ItemOpener.RollFrom(new Random(12345));
        var seen = new HashSet<int>();
        for (int i = 0; i < 400; i++)
        {
            int value = roll(3);
            Assert.InRange(value, 0, 2);
            seen.Add(value);
        }
        Assert.Equal(3, seen.Count);
        Assert.Equal(0, roll(1));
    }

    [Fact]
    public void MissingCollaboratorsAreRejectedUpFront()
    {
        RollSource roll = new Dice().Roll;
        Assert.Throws<ArgumentNullException>(() => ItemOpener.RollFrom(null));
        Assert.Throws<ArgumentNullException>(() => new ItemOpener(null, roll));
        Assert.Throws<ArgumentNullException>(() => new ItemOpener(code => null, null));
        Assert.Throws<ArgumentNullException>(() => ItemOpener.Draw(Package(900002, (901001, 1)), null));
        var opener = new ItemOpener(code => null, roll);
        ItemInfo pack = Package(900002, (901001, 1));
        Assert.Throws<ArgumentNullException>(() => opener.Open(null, new ItemCooldowns(), pack, 0, 1, ItemPlaces.Town));
        Assert.Throws<ArgumentNullException>(() => opener.Open(new Inventory(), null, pack, 0, 1, ItemPlaces.Town));
        var desk = new TeleportDesk();
        Assert.Throws<ArgumentNullException>(() => desk.Request(null, new ItemCooldowns(), Scroll(900300), 0, 1, ItemPlaces.Town));
        Assert.Throws<ArgumentNullException>(() => desk.Request(new Inventory(), null, Scroll(900300), 0, 1, ItemPlaces.Town));
        Assert.Throws<ArgumentNullException>(() => desk.Commit(null, new ItemCooldowns(), 1));
        Assert.Throws<ArgumentNullException>(() => desk.Commit(new Inventory(), null, 1));
        Assert.Throws<ArgumentNullException>(() => new TeleportRequest(null));
        Assert.Throws<ArgumentNullException>(() => new OpenGrant(null, 1));
        Assert.Throws<ArgumentNullException>(() => new BoosterBlock(BoosterKind.Etc, 1, null));
    }

    // ------------------------------------------------------------------ 派发表

    [Fact]
    public void EveryStackableTypeFromZeroToThirtyEightHasExactlyOneRowAndItsTagRoundTrips()
    {
        Assert.Equal(39, ItemUseTable.Rows.Count);
        for (int type = 0; type <= 38; type++)
        {
            ItemUseRow row = ItemUseTable.RowOf(type);
            Assert.NotNull(row);
            Assert.Equal(type, row.StackableType);
            Assert.Equal(type, StackableTypes.FromTag(row.Tag));
            Assert.Same(ItemUseTable.Rows[type], row);
        }
        Assert.Null(ItemUseTable.RowOf(-1));
        Assert.Null(ItemUseTable.RowOf(39));
    }

    [Fact]
    public void OnlyRecoveryAndTheTwoOpeningClassesAreImplementedAndTeleportIsTheOnlyStub()
    {
        int[] implemented = ItemUseTable.Rows.Where(r => r.Status == ItemUseStatus.Implemented).Select(r => r.StackableType).ToArray();
        Assert.Equal(new[] { 0, 4, 16, 38 }, implemented);
        Assert.Equal(new[] { 37 }, ItemUseTable.Rows.Where(r => r.Status == ItemUseStatus.Stub).Select(r => r.StackableType).ToArray());
        Assert.Equal(ItemUseClass.Recovery, ItemUseTable.RowOf(0).Class);
        Assert.Equal(ItemUseClass.Recovery, ItemUseTable.RowOf(4).Class);
        Assert.Equal(ItemUseClass.OpenBox, ItemUseTable.RowOf(16).Class);
        Assert.Equal(ItemUseClass.OpenPackage, ItemUseTable.RowOf(38).Class);
        Assert.Equal(ItemUseClass.Teleport, ItemUseTable.RowOf(37).Class);
    }

    [Fact]
    public void RegisteredAndUnreadClassesHaveNoAllowedPlaceSoNothingIsUsableThroughThem()
    {
        foreach (ItemUseRow row in ItemUseTable.Rows.Where(r => r.Status == ItemUseStatus.Registered || r.Status == ItemUseStatus.Unread))
        {
            Assert.Equal(ItemPlaces.None, row.Places);
            ItemInfo item = Stack(900100 + row.StackableType, row.StackableType);
            Assert.False(ItemUseTable.AllowedAt(item, ItemPlaces.Town));
            Assert.False(ItemUseTable.AllowedAt(item, ItemPlaces.Dungeon));
        }
    }

    [Fact]
    public void TheOpeningClassesAndTeleportAreTownOnlyAndRecoveryIsDungeonOnly()
    {
        Assert.Equal(ItemPlaces.Town, ItemUseTable.RowOf(16).Places);
        Assert.Equal(ItemPlaces.Town, ItemUseTable.RowOf(38).Places);
        Assert.Equal(ItemPlaces.Town, ItemUseTable.RowOf(37).Places);
        Assert.Equal(ItemPlaces.Dungeon, ItemUseTable.RowOf(0).Places);
        Assert.Equal(ItemPlaces.Dungeon, ItemUseTable.RowOf(4).Places);
        ItemInfo box = Stack(900001, BoosterType);
        Assert.True(ItemUseTable.AllowedAt(box, ItemPlaces.Town));
        Assert.False(ItemUseTable.AllowedAt(box, ItemPlaces.Dungeon));
        Assert.False(ItemUseTable.AllowedAt(box, ItemPlaces.None));
        Assert.False(ItemUseTable.AllowedAt(box, ItemPlaces.Any));
        foreach (int type in new[] { StackableTypes.Waste, StackableTypes.Throw })
        {
            ItemInfo potion = Stack(900050, type);
            Assert.True(ItemUseTable.AllowedAt(potion, ItemPlaces.Dungeon));
            Assert.False(ItemUseTable.AllowedAt(potion, ItemPlaces.Town));       // 与药水在城镇被拒(Unavailable)一致
            Assert.False(ItemUseTable.AllowedAt(potion, ItemPlaces.None));
            Assert.False(ItemUseTable.AllowedAt(potion, ItemPlaces.Any));        // 单个地点才有意义,"任意"不是一个地点
        }
    }

    [Fact]
    public void ClassOfIgnoresEquipmentNullAndUnknownTypes()
    {
        Assert.Equal(ItemUseClass.None, ItemUseTable.ClassOf(null));
        Assert.Equal(ItemUseClass.None, ItemUseTable.ClassOf(Gear(901101)));
        ItemInfo gearWithAStackableType = Gear(901102);
        gearWithAStackableType.StackableType = BoosterType;                       // 装备不会有可叠加类型;即便有也不按它归类
        Assert.Equal(ItemUseClass.None, ItemUseTable.ClassOf(gearWithAStackableType));
        Assert.Null(ItemUseTable.RowOf(gearWithAStackableType));
        Assert.Equal(ItemUseClass.None, ItemUseTable.ClassOf(Stack(900001, StackableTypes.Unknown)));
        Assert.Equal(ItemUseClass.None, ItemUseTable.ClassOf(Stack(900001, 99)));
        Assert.Equal(ItemUseStatus.Unread, ItemUseTable.StatusOf(Stack(900001, StackableTypes.Unknown)));
        Assert.Equal(ItemUseStatus.Unread, ItemUseTable.StatusOf(null));
        Assert.False(ItemUseTable.IsOpening(null));
        Assert.False(ItemUseTable.IsOpening(Stack(900001, StackableTypes.Waste)));
        Assert.True(ItemUseTable.IsOpening(Stack(900001, BoosterType)));
        Assert.True(ItemUseTable.IsOpening(Stack(900001, PackageType)));
    }

    [Fact]
    public void OnlyRecoveryClassesCanEnterTheQuickSlotsNoMatterWhatTheyCarry()
    {
        var recovery = new RecoveryEffect(false, 50, 0);
        foreach (ItemUseRow row in ItemUseTable.Rows)
        {
            ItemInfo item = Stack(900200 + row.StackableType, row.StackableType);
            item.HpRecovery = recovery;
            bool expected = row.Class == ItemUseClass.Recovery;
            Assert.Equal(expected, QuickSlots.Eligible(item));
            Assert.Equal(expected, ItemUseTable.QuickSlotClass(item));
            var slots = new QuickSlots();
            Assert.Equal(expected ? 0 : -1, slots.Place(item));
        }
    }

    [Fact]
    public void TheRecoveryCheckNeverAcceptsAnOpeningBoxSoThePotionPathCannotConsumeOne()
    {
        ItemInfo box = Box(900001, 1000, -1, Block(BoosterKind.Etc, 1, (901001, 1, 1)));
        box.HpRecovery = new RecoveryEffect(false, 50, 0);                      // 即便脚本里带了回复字段
        ItemInfo pack = Package(900002, (901001, 1));
        pack.MpRecovery = new RecoveryEffect(false, 50, 0);
        Assert.Equal(UseResult.NotConsumable, ConsumableRules.Check(box, EquipRules.SwordmanJob, 99));
        Assert.Equal(UseResult.NotConsumable, ConsumableRules.Check(pack, EquipRules.SwordmanJob, 99));
        Assert.False(ConsumableRules.IsConsumableType(box));
        Assert.False(ConsumableRules.IsConsumableType(pack));
    }

    [Fact]
    public void ThePotionCheckStillAnswersTheSameThingsAsBefore()
    {
        ItemInfo potion = Stack(900050, StackableTypes.Waste, 99, 500, 1, 10);
        potion.HpRecovery = new RecoveryEffect(false, 50, 0);
        Assert.Equal(UseResult.LevelTooLow, ConsumableRules.Check(potion, EquipRules.SwordmanJob, 9));
        Assert.Equal(UseResult.Ok, ConsumableRules.Check(potion, EquipRules.SwordmanJob, 10));
        potion.UsableJobs.Add("[mage]");
        Assert.Equal(UseResult.WrongJob, ConsumableRules.Check(potion, EquipRules.SwordmanJob, 10));
        Assert.Equal(UseResult.NoEffect, ConsumableRules.Check(Stack(900051, StackableTypes.Waste), EquipRules.SwordmanJob, 99));
        ItemInfo thrown = Stack(900052, StackableTypes.Throw);
        thrown.MpRecovery = new RecoveryEffect(true, 5, 100);
        Assert.Equal(UseResult.Ok, ConsumableRules.Check(thrown, EquipRules.SwordmanJob, 1));
    }

    // ------------------------------------------------------------------ 传送桩

    private static ItemInfo Scroll(int code, int minLevel = 0, int coolMs = 0)
    {
        return Stack(code, StackableTypes.FromTag("[teleport potion]"), 100, coolMs, -1, minLevel);
    }

    [Fact]
    public void ATeleportUseIsRecordedAndNeitherDeductsNorStartsACooldown()
    {
        var s = new Scenario();
        var desk = new TeleportDesk();
        ItemInfo scroll = Scroll(900300, 0, 3000);
        s.Put(scroll, 2);
        string before = s.Snapshot();

        UseResult r = desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town);

        Assert.Equal(UseResult.Pending, r);
        Assert.Same(scroll, desk.Pending.Item);
        Assert.Equal((1, 0), (desk.Recorded, desk.Committed));
        Assert.Equal(before, s.Snapshot());
        Assert.Equal(2, s.Bag.CountOf(900300));
    }

    [Fact]
    public void CommittingATeleportTakesOneItemStartsTheCooldownAndClearsTheRequest()
    {
        var s = new Scenario();
        var desk = new TeleportDesk();
        ItemInfo scroll = Scroll(900300, 0, 3000);
        s.Put(scroll, 2);
        desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town);

        Assert.True(desk.Commit(s.Bag, s.Cooldowns, 3));

        Assert.Null(desk.Pending);
        Assert.Equal((1, 1), (desk.Recorded, desk.Committed));
        Assert.Equal(1, s.Bag.CountOf(900300));
        Assert.True(s.Cooldowns.IsCooling(scroll));
    }

    [Fact]
    public void ACommitWithoutARequestOrWithABadDestinationChangesNothing()
    {
        var s = new Scenario();
        var desk = new TeleportDesk();
        ItemInfo scroll = Scroll(900300);
        s.Put(scroll, 1);
        string before = s.Snapshot();

        Assert.False(desk.Commit(s.Bag, s.Cooldowns, 3));                           // 没有等着的请求
        desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town);
        Assert.False(desk.Commit(s.Bag, s.Cooldowns, 0));                           // 去向至少是 1
        Assert.False(desk.Commit(s.Bag, s.Cooldowns, -5));
        Assert.NotNull(desk.Pending);                                               // 请求还在,等城镇线选对去向
        Assert.Equal(before, s.Snapshot());
        Assert.Equal(0, desk.Committed);
    }

    [Fact]
    public void ACommitWhenTheItemIsGoneDropsTheRequestWithoutGoingNegative()
    {
        var s = new Scenario();
        var desk = new TeleportDesk();
        ItemInfo scroll = Scroll(900300);
        s.Put(scroll, 1);
        desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town);
        s.Bag.Remove(900300, 1);

        Assert.False(desk.Commit(s.Bag, s.Cooldowns, 2));
        Assert.Null(desk.Pending);
        Assert.Equal((0, 0), (s.Bag.TotalCount, desk.Committed));
    }

    [Fact]
    public void CancellingARequestChangesNothingInTheBag()
    {
        var s = new Scenario();
        var desk = new TeleportDesk();
        ItemInfo scroll = Scroll(900300, 0, 3000);
        s.Put(scroll, 1);
        desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town);
        string before = s.Snapshot();

        desk.Cancel();

        Assert.Null(desk.Pending);
        Assert.Equal(before, s.Snapshot());
        Assert.Equal((1, 0), (desk.Recorded, desk.Committed));
    }

    [Fact]
    public void ATeleportRequestFollowsTheSameChecksAsOpeningAndRecordsNothingWhenRefused()
    {
        var s = new Scenario();
        var desk = new TeleportDesk();
        ItemInfo scroll = Scroll(900300, 20, 3000);
        s.Put(scroll, 1);
        string before = s.Snapshot();

        Assert.Equal(UseResult.NotConsumable, desk.Request(s.Bag, s.Cooldowns, Stack(900301, StackableTypes.Waste), s.Job, s.Level, ItemPlaces.Town));
        Assert.Equal(UseResult.Unavailable, desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Dungeon));
        s.Level = 19;
        Assert.Equal(UseResult.LevelTooLow, desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town));
        s.Level = 50;
        scroll.UsableJobs.Add("[mage]");
        Assert.Equal(UseResult.WrongJob, desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town));
        scroll.UsableJobs.Clear();
        Assert.Equal(UseResult.NotInBag, desk.Request(s.Bag, s.Cooldowns, Scroll(900302), s.Job, s.Level, ItemPlaces.Town));
        Assert.Equal(before, s.Snapshot());
        Assert.Null(desk.Pending);
        Assert.Equal(0, desk.Recorded);

        s.Cooldowns.Start(scroll);
        Assert.Equal(UseResult.Cooling, desk.Request(s.Bag, s.Cooldowns, scroll, s.Job, s.Level, ItemPlaces.Town));
        Assert.Null(desk.Pending);
        Assert.Equal(0, desk.Recorded);
    }

    [Fact]
    public void ASecondRequestReplacesTheFirstOne()
    {
        var s = new Scenario();
        var desk = new TeleportDesk();
        ItemInfo a = Scroll(900300);
        ItemInfo b = Scroll(900301);
        s.Put(a, 1);
        s.Put(b, 1);
        desk.Request(s.Bag, s.Cooldowns, a, s.Job, s.Level, ItemPlaces.Town);
        desk.Request(s.Bag, s.Cooldowns, b, s.Job, s.Level, ItemPlaces.Town);

        Assert.Same(b, desk.Pending.Item);
        Assert.Equal(2, desk.Recorded);
    }
}
