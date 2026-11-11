using static Dnf.Game.Tests.EnhanceFixtures;

namespace Dnf.Game.Tests;

/// <summary>
/// 附加来源叠在装备栏汇总上:没装时口径一点不变,装上之后每一项也像一件装备那样先折算再相加;
/// 默认来源把附魔、套装与个体状态加成接进去,按槽版本缓存。数据全是合成的(装备 900 起,卡 931,套装 940 到 944)。
/// </summary>
public class EquipOverlayTests
{
    private sealed class FixedOverlay : IEquipOverlay
    {
        private readonly List<EquipStats> _terms;

        public FixedOverlay(params EquipStats[] terms)
        {
            _terms = new List<EquipStats>(terms);
        }

        public int Calls { get; private set; }

        public IReadOnlyList<EquipStats> Terms(EquipmentSlots slots)
        {
            Calls++;
            return _terms;
        }
    }

    private sealed class Bonus : IEquipInstanceBonus
    {
        private readonly Func<ItemInfo, EquipStats> _fn;

        public Bonus(Func<ItemInfo, EquipStats> fn)
        {
            _fn = fn;
        }

        public EquipStats BonusOf(ItemInfo worn) => _fn(worn);
    }

    private static (long, long, long, long, long, long, long, long, long) Fields(StatBlock s)
    {
        return (s.HpMax, s.MpMax, s.PhysicalAttack, s.PhysicalDefense, s.MagicalAttack, s.MagicalDefense, s.InventoryLimit, s.MpRegenSpeed, s.HitRecovery);
    }

    private static ItemInfo GearWith(int code, int part, EquipStats stats)
    {
        ItemInfo item = Gear(code, part);
        item.Stats = stats;
        return item;
    }

    private static readonly ItemInfo Card = Stack(931, new EnhanceData(
        enchantStats: Stats(EquipStat.PhysicalAttack, 7),
        enchantParts: new[] { EquipmentParts.Weapon }));

    private static readonly Dictionary<int, ItemInfo> Defs = BuildDefs();

    private static Dictionary<int, ItemInfo> BuildDefs()
    {
        var defs = new Dictionary<int, ItemInfo> { [931] = Card, [932] = Stack(932, new EnhanceData(enchantStats: EquipStats.Empty)) };
        var master = new EnhanceData(
            setMembers: new[] { 940, 941, 942 },
            setFullBonus: Stats(EquipStat.HpMax, 100),
            setPieceBonuses: new[] { new SetPieceBonus(2, Stats(EquipStat.MpMax, 20)) });
        defs[940] = Gear(940, EquipmentParts.Coat, enhance: master);
        defs[941] = Gear(941, EquipmentParts.Shoulder, enhance: new EnhanceData(setMaster: 940));
        defs[942] = Gear(942, EquipmentParts.Pants, enhance: new EnhanceData(setMaster: 940));
        return defs;
    }

    private static ItemInfo Lookup(int code) => Defs.TryGetValue(code, out ItemInfo item) ? item : null;

    // ------------------------------------------------------------------ 没装附加来源

    [Fact]
    public void WithoutAnOverlayTheTotalsAreExactlyTheBaseSum()
    {
        var slots = new EquipmentSlots();
        ItemInfo a = GearWith(900, EquipmentParts.Weapon, Stats(EquipStat.PhysicalAttack, 12.5));
        ItemInfo b = GearWith(901, EquipmentParts.Coat, Stats(EquipStat.HpMax, 40));
        slots.Restore(EquipmentParts.Weapon, a);
        slots.Restore(EquipmentParts.Coat, b);
        Assert.Null(slots.Overlay);
        Assert.Equal(a.Stats.Plus(b.Stats), slots.TotalStats());
        Assert.Equal(a.Stats.ToStatBlock() + b.Stats.ToStatBlock(), slots.TotalStatBlock());
    }

    [Fact]
    public void AnEmptySlotsStaysEmptyWithAnEmptyOverlay()
    {
        var slots = new EquipmentSlots { Overlay = new FixedOverlay() };
        Assert.True(slots.TotalStats().IsEmpty);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L), Fields(slots.TotalStatBlock()));
    }

    // ------------------------------------------------------------------ 折进汇总

    [Fact]
    public void AnOverlayTermIsAddedToTheBaseSum()
    {
        var slots = new EquipmentSlots();
        slots.Restore(EquipmentParts.Weapon, GearWith(900, EquipmentParts.Weapon, Stats(EquipStat.PhysicalAttack, 10)));
        slots.Overlay = new FixedOverlay(Stats(EquipStat.PhysicalAttack, 5), Stats(EquipStat.HpMax, 100));
        EquipStats total = slots.TotalStats();
        Assert.Equal((15.0, 100.0), (total[EquipStat.PhysicalAttack], total[EquipStat.HpMax]));
        StatBlock block = slots.TotalStatBlock();
        Assert.Equal((150L, 1000L), (block.PhysicalAttack, block.HpMax));
    }

    [Fact]
    public void AnOverlayWorksEvenWhenNothingIsWorn()
    {
        var slots = new EquipmentSlots { Overlay = new FixedOverlay(Stats(EquipStat.HpMax, 8)) };
        Assert.Equal(8.0, slots.TotalStats()[EquipStat.HpMax]);
        Assert.Equal(80L, slots.TotalStatBlock().HpMax);
    }

    [Fact]
    public void EveryTermIsConvertedOnItsOwnBeforeTheyAreAdded()
    {
        // 与每件装备同一口径:先各自折成十分之一单位的整数再相加。0.04 单独折是 0,两个加起来的 0.08 折是 1——汇总取前者
        var slots = new EquipmentSlots { Overlay = new FixedOverlay(Stats(EquipStat.HpMax, 0.04), Stats(EquipStat.HpMax, 0.04)) };
        Assert.Equal(0L, slots.TotalStatBlock().HpMax);
        Assert.Equal(0.08, slots.TotalStats()[EquipStat.HpMax], 6);
        Assert.Equal(1L, slots.TotalStats().ToStatBlock().HpMax);
    }

    [Fact]
    public void TheBlockTotalAlwaysEqualsTheSumOfItsPartsForRandomWearsAndTerms()
    {
        var random = new Random(20261110);
        for (int round = 0; round < 200; round++)
        {
            var slots = new EquipmentSlots();
            var parts = new List<EquipStats>();
            for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
            {
                if (random.Next(2) == 0)
                {
                    EquipStats stats = RandomStats(random);
                    slots.Restore(part, GearWith(1000 + part, part, stats));
                    parts.Add(stats);
                }
            }
            var terms = new List<EquipStats>();
            for (int i = random.Next(4); i > 0; i--)
            {
                terms.Add(RandomStats(random));
            }
            slots.Overlay = new FixedOverlay(terms.ToArray());
            parts.AddRange(terms);

            StatBlock expected = default;
            EquipStats expectedStats = EquipStats.Empty;
            foreach (EquipStats part in parts)
            {
                expected += part.ToStatBlock();
                expectedStats = expectedStats.Plus(part);
            }
            Assert.Equal(Fields(expected), Fields(slots.TotalStatBlock()));
            Assert.Equal(expectedStats, slots.TotalStats());
        }
    }

    private static EquipStats RandomStats(Random random)
    {
        var builder = new EquipStats.Builder();
        foreach (EquipStat stat in Enum.GetValues<EquipStat>())
        {
            if (random.Next(4) == 0)
            {
                builder.Set(stat, random.Next(0, 400) / 4.0);
            }
        }
        return builder.Build();
    }

    // ------------------------------------------------------------------ 版本

    [Fact]
    public void ChangingTheOverlayBumpsTheVersionAndTheSameOneDoesNot()
    {
        var slots = new EquipmentSlots();
        var overlay = new FixedOverlay();
        int v0 = slots.Version;
        slots.Overlay = overlay;
        Assert.Equal(v0 + 1, slots.Version);
        slots.Overlay = overlay;
        Assert.Equal(v0 + 1, slots.Version);
        slots.Overlay = null;
        Assert.Equal(v0 + 2, slots.Version);
        slots.Overlay = null;
        Assert.Equal(v0 + 2, slots.Version);
    }

    [Fact]
    public void RefreshBumpsTheVersion()
    {
        var slots = new EquipmentSlots();
        int v0 = slots.Version;
        slots.Refresh();
        slots.Refresh();
        Assert.Equal(v0 + 2, slots.Version);
    }

    [Fact]
    public void TakeRemovesTheItemAndCountsAsAChange()
    {
        var slots = new EquipmentSlots();
        ItemInfo weapon = Gear(900, EquipmentParts.Weapon);
        slots.Restore(EquipmentParts.Weapon, weapon);
        int v = slots.Version;
        Assert.Same(weapon, slots.Take(EquipmentParts.Weapon));
        Assert.Null(slots.Get(EquipmentParts.Weapon));
        Assert.Equal(v + 1, slots.Version);
        // 空格子与没有格子的部位:什么也不动
        Assert.Null(slots.Take(EquipmentParts.Weapon));
        Assert.Null(slots.Take(EquipmentParts.Creature));
        Assert.Null(slots.Take(0));
        Assert.Equal(v + 1, slots.Version);
    }

    [Fact]
    public void ReplacingAnItemWithItsStateCopyIsAChangeButTheSameReferenceIsNot()
    {
        var slots = new EquipmentSlots();
        ItemInfo weapon = Gear(900, EquipmentParts.Weapon);
        slots.Restore(EquipmentParts.Weapon, weapon);
        int v = slots.Version;
        slots.Restore(EquipmentParts.Weapon, weapon);
        Assert.Equal(v, slots.Version);
        slots.Restore(EquipmentParts.Weapon, With(weapon, 3));
        Assert.Equal(v + 1, slots.Version);
    }

    // ------------------------------------------------------------------ 默认来源:附魔

    private static (EquipmentSlots Slots, EquipBonusOverlay Overlay) Rig()
    {
        var slots = new EquipmentSlots();
        var overlay = new EquipBonusOverlay(Lookup);
        overlay.Attach(slots);
        return (slots, overlay);
    }

    [Fact]
    public void AttachingHooksTheOverlayOntoTheSlots()
    {
        var slots = new EquipmentSlots();
        var overlay = new EquipBonusOverlay(Lookup);
        int v = slots.Version;
        overlay.Attach(slots);
        Assert.Same(overlay, slots.Overlay);
        Assert.True(slots.Version > v);
    }

    [Fact]
    public void AttachingToASecondSlotsReleasesTheFirst()
    {
        var first = new EquipmentSlots();
        var second = new EquipmentSlots();
        var overlay = new EquipBonusOverlay(Lookup);
        overlay.Attach(first);
        overlay.Attach(second);
        Assert.Null(first.Overlay);
        Assert.Same(overlay, second.Overlay);
        overlay.Attach(second);   // 再装同一个不出错
        Assert.Same(overlay, second.Overlay);
    }

    [Fact]
    public void AnEnchantedItemAddsItsCardsStats()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        slots.Restore(EquipmentParts.Weapon, With(Gear(900, EquipmentParts.Weapon), 0, enchant: 931));
        Assert.Equal(7.0, slots.TotalStats()[EquipStat.PhysicalAttack]);
        Assert.Equal(70L, slots.TotalStatBlock().PhysicalAttack);
        Assert.Equal(7.0, overlay.EnchantOf(slots.Get(EquipmentParts.Weapon))[EquipStat.PhysicalAttack]);
    }

    [Fact]
    public void AnItemWithoutAnEnchantmentOrWithAnUnknownCardAddsNothing()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        ItemInfo plain = Gear(900, EquipmentParts.Weapon);
        ItemInfo unknown = With(Gear(901, EquipmentParts.Coat), 0, enchant: 999);
        ItemInfo noStats = With(Gear(902, EquipmentParts.Ring), 0, enchant: 900);   // 900 不是卡
        slots.Restore(EquipmentParts.Weapon, plain);
        slots.Restore(EquipmentParts.Coat, unknown);
        slots.Restore(EquipmentParts.Ring, noStats);
        Assert.Null(overlay.EnchantOf(plain));
        Assert.Null(overlay.EnchantOf(unknown));
        Assert.Null(overlay.EnchantOf(noStats));
        Assert.Null(overlay.EnchantOf(null));
        Assert.True(slots.TotalStats().IsEmpty);
    }

    [Fact]
    public void TwoEnchantedItemsAddUp()
    {
        (EquipmentSlots slots, _) = Rig();
        slots.Restore(EquipmentParts.Weapon, With(Gear(900, EquipmentParts.Weapon), 0, enchant: 931));
        slots.Restore(EquipmentParts.Ring, With(Gear(901, EquipmentParts.Ring), 0, enchant: 931));
        Assert.Equal(14.0, slots.TotalStats()[EquipStat.PhysicalAttack]);
    }

    [Fact]
    public void SwitchingEnchantmentOffRemovesItAndBumpsTheVersion()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        slots.Restore(EquipmentParts.Weapon, With(Gear(900, EquipmentParts.Weapon), 0, enchant: 931));
        Assert.True(overlay.EnchantEnabled);
        int v = slots.Version;
        overlay.EnchantEnabled = false;
        Assert.True(slots.Version > v);
        Assert.True(slots.TotalStats().IsEmpty);
        overlay.EnchantEnabled = true;
        Assert.Equal(7.0, slots.TotalStats()[EquipStat.PhysicalAttack]);
    }

    // ------------------------------------------------------------------ 默认来源:套装

    [Fact]
    public void ACompleteSetAddsItsBonusesAndOneMissingPieceDoesNot()
    {
        (EquipmentSlots slots, _) = Rig();
        slots.Restore(EquipmentParts.Coat, Defs[940]);
        slots.Restore(EquipmentParts.Shoulder, Defs[941]);
        EquipStats two = slots.TotalStats();
        Assert.Equal((20.0, 0.0), (two[EquipStat.MpMax], two[EquipStat.HpMax]));   // 2 件档到了,还没穿齐
        slots.Restore(EquipmentParts.Pants, Defs[942]);
        EquipStats full = slots.TotalStats();
        Assert.Equal((20.0, 100.0), (full[EquipStat.MpMax], full[EquipStat.HpMax]));
        slots.Take(EquipmentParts.Pants);
        Assert.Equal(0.0, slots.TotalStats()[EquipStat.HpMax]);   // 脱掉一件,穿齐的加成立刻没了
        slots.Take(EquipmentParts.Shoulder);
        Assert.True(slots.TotalStats().IsEmpty);                    // 剩一件,2 件档也没了
    }

    [Fact]
    public void SwitchingSetsOffRemovesThem()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        slots.Restore(EquipmentParts.Coat, Defs[940]);
        slots.Restore(EquipmentParts.Shoulder, Defs[941]);
        slots.Restore(EquipmentParts.Pants, Defs[942]);
        Assert.False(slots.TotalStats().IsEmpty);      // 先算一遍,缓存里有了套装项
        int v = slots.Version;
        overlay.SetsEnabled = false;
        Assert.True(slots.Version > v);                // 开关一变就丢缓存、升版本
        Assert.True(slots.TotalStats().IsEmpty);
        Assert.False(overlay.SetsEnabled);
        overlay.SetsEnabled = true;
        Assert.False(slots.TotalStats().IsEmpty);
    }

    [Fact]
    public void AnEnchantCardWithAnEmptyBlockAddsNoTerm()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        ItemInfo weapon = With(Gear(900, EquipmentParts.Weapon), 0, enchant: 932);
        slots.Restore(EquipmentParts.Weapon, weapon);
        Assert.NotNull(overlay.EnchantOf(weapon));      // 卡有 [enchant] 块,只是空的
        Assert.Empty(overlay.Terms(slots));
    }

    [Fact]
    public void TheSetBonusFoldsLikeAnyOtherTerm()
    {
        (EquipmentSlots slots, _) = Rig();
        slots.Restore(EquipmentParts.Coat, Defs[940]);
        slots.Restore(EquipmentParts.Shoulder, Defs[941]);
        slots.Restore(EquipmentParts.Pants, Defs[942]);
        StatBlock block = slots.TotalStatBlock();
        Assert.Equal((1000L, 200L), (block.HpMax, block.MpMax));
    }

    // ------------------------------------------------------------------ 默认来源:个体状态加成

    [Fact]
    public void AnInstanceBonusProviderIsAskedForEachWornItem()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        ItemInfo weapon = With(Gear(900, EquipmentParts.Weapon), 4);
        ItemInfo plain = Gear(901, EquipmentParts.Coat);
        slots.Restore(EquipmentParts.Weapon, weapon);
        slots.Restore(EquipmentParts.Coat, plain);
        var asked = new List<int>();
        overlay.InstanceBonus = new Bonus(item =>
        {
            asked.Add(item.Code);
            return item.Instance == null ? null : Stats(EquipStat.PhysicalAttack, item.Instance.Upgrade * 2);
        });
        Assert.Equal(8.0, slots.TotalStats()[EquipStat.PhysicalAttack]);
        Assert.Equal(new[] { 900, 901 }, asked);
        Assert.NotNull(overlay.InstanceBonus);
    }

    [Fact]
    public void EmptyOrNullInstanceBonusesAreSkipped()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        slots.Restore(EquipmentParts.Weapon, Gear(900, EquipmentParts.Weapon));
        slots.Restore(EquipmentParts.Coat, Gear(901, EquipmentParts.Coat));
        int call = 0;
        overlay.InstanceBonus = new Bonus(_ => ++call == 1 ? null : EquipStats.Empty);
        Assert.True(slots.TotalStats().IsEmpty);
        Assert.Empty(overlay.Terms(slots));
    }

    [Fact]
    public void ChangingTheInstanceBonusSourceBumpsTheVersion()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        int v = slots.Version;
        overlay.InstanceBonus = new Bonus(_ => null);
        Assert.True(slots.Version > v);
    }

    // ------------------------------------------------------------------ 缓存

    [Fact]
    public void TermsAreCachedPerSlotsVersionAndRecomputedAfterAChange()
    {
        var slots = new EquipmentSlots();
        int lookups = 0;
        var counting = new EquipBonusOverlay(code =>
        {
            lookups++;
            return Lookup(code);
        });
        counting.Attach(slots);
        slots.Restore(EquipmentParts.Weapon, With(Gear(900, EquipmentParts.Weapon), 0, enchant: 931));
        IReadOnlyList<EquipStats> first = counting.Terms(slots);
        int afterFirst = lookups;
        Assert.Same(first, counting.Terms(slots));
        Assert.Equal(afterFirst, lookups);   // 版本没变,不重算
        slots.Restore(EquipmentParts.Ring, With(Gear(901, EquipmentParts.Ring), 0, enchant: 931));
        IReadOnlyList<EquipStats> second = counting.Terms(slots);
        Assert.NotSame(first, second);
        Assert.True(lookups > afterFirst);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void InvalidateForcesARecomputeAndNotifiesTheSlots()
    {
        (EquipmentSlots slots, EquipBonusOverlay overlay) = Rig();
        slots.Restore(EquipmentParts.Weapon, With(Gear(900, EquipmentParts.Weapon), 0, enchant: 931));
        IReadOnlyList<EquipStats> first = overlay.Terms(slots);
        int v = slots.Version;
        overlay.Invalidate();
        Assert.True(slots.Version > v);
        Assert.NotSame(first, overlay.Terms(slots));
    }

    [Fact]
    public void ACacheForOneSlotsIsNotReusedForAnother()
    {
        var overlay = new EquipBonusOverlay(Lookup);
        var a = new EquipmentSlots();
        var b = new EquipmentSlots();
        a.Restore(EquipmentParts.Weapon, With(Gear(900, EquipmentParts.Weapon), 0, enchant: 931));
        b.Restore(EquipmentParts.Ring, Gear(901, EquipmentParts.Ring));
        Assert.Equal(a.Version, b.Version);               // 两个栏的版本号相同,只能靠"是不是同一个栏"区分缓存
        Assert.Single(overlay.Terms(a));
        Assert.Empty(overlay.Terms(b));
        Assert.Single(overlay.Terms(a));
    }

    [Fact]
    public void InvalidateOnAnUnattachedOverlayIsHarmless()
    {
        var overlay = new EquipBonusOverlay(Lookup);
        overlay.Invalidate();
        overlay.EnchantEnabled = false;
        overlay.SetsEnabled = false;
        Assert.False(overlay.EnchantEnabled);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => new EquipBonusOverlay(null));
        var overlay = new EquipBonusOverlay(Lookup);
        Assert.Throws<ArgumentNullException>(() => overlay.Attach(null));
        Assert.Throws<ArgumentNullException>(() => overlay.Terms(null));
    }

    // ------------------------------------------------------------------ 综合

    [Fact]
    public void EnchantAndSetAndBaseStatsAllShowUpInOneTotal()
    {
        (EquipmentSlots slots, _) = Rig();
        // 套装主件自己也有基础属性:用同编号同套装数据的另一份,不去改共享的定义
        ItemInfo coat = Gear(940, EquipmentParts.Coat, enhance: Defs[940].Enhance);
        coat.Stats = Stats(EquipStat.HpMax, 50);
        slots.Restore(EquipmentParts.Coat, coat);
        slots.Restore(EquipmentParts.Shoulder, Defs[941]);
        slots.Restore(EquipmentParts.Pants, Defs[942]);
        slots.Restore(EquipmentParts.Weapon, With(Gear(900, EquipmentParts.Weapon), 0, enchant: 931));
        EquipStats total = slots.TotalStats();
        Assert.Equal((150.0, 20.0, 7.0), (total[EquipStat.HpMax], total[EquipStat.MpMax], total[EquipStat.PhysicalAttack]));
    }
}
