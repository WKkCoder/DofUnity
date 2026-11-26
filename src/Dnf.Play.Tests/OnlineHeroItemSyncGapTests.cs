using System.Text;
using Dnf.Game;
using Dnf.Game.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网主角物品同步补测(r20fix-onlineitems):既有用例的装备实例只覆盖"几个字段一起非默认",这里把
/// ① <see cref="HeroProgress.ApplyOnlineItems"/> 里装备个体实例的判定(<c>WithOnlineEquipInstance</c>):每个字段单独非默认、各自的钳制边界;
/// ② <see cref="OnlineItemSlotEntry"/> 的构造缺省值(数量缺省 1、其余缺省"没有个体状态")与每个参数落到自己的属性;
/// ③ 背包、快捷栏里的装备同样带个体实例;已穿着的装备原样重发不算变化、任何差异都要换掉;
/// ④ 金币钳制与 <see cref="OnlineItemSyncData"/> 的拷贝语义
/// 逐条钉住。全用合成编号(900001+/901001+),预期值全是手算字面量,不碰存档与网络。
/// </summary>
public sealed class OnlineHeroItemSyncGapTests
{
    private const int WeaponCode = 900001;
    private const int OtherWeaponCode = 900002;
    private const int CoatCode = 900003;
    private const int PotionCode = 901001;
    private const int MaterialCode = 901002;

    private static GameData BuildGameData()
    {
        var thresholds = new int[99];
        for (int i = 0; i < thresholds.Length; i++)
        {
            thresholds[i] = (i + 1) * 100;
        }
        var fx = new Fixture();
        fx.List(GameData.CharacterListPath, (EquipRules.SwordmanJob, "swordman/test.chr"))
            .Tokens("character/swordman/test.chr", w => w.Tag("[job]").Str("[swordman]"));
        fx.Tokens(ExpTable.Path, w => w.Ints(thresholds))
            .Tokens(SpTable.Path, w =>
            {
                w.Tag("[sp table]");
                for (int level = 1; level <= 60; level++)
                {
                    w.Int(level).Int(3);
                }
            });
        fx.List(ItemCatalog.EquipmentListPath,
            (WeaponCode, "test/weapon.equ"),
            (OtherWeaponCode, "test/other_weapon.equ"),
            (CoatCode, "test/coat.equ"));
        fx.Tokens("equipment/test/weapon.equ", w => w
            .Tag("[equipment type]").Str("[weapon]").Int(0)
            .Tag("[usable job]").Str("[swordman]"));
        fx.Tokens("equipment/test/other_weapon.equ", w => w
            .Tag("[equipment type]").Str("[weapon]").Int(0)
            .Tag("[usable job]").Str("[swordman]"));
        fx.Tokens("equipment/test/coat.equ", w => w
            .Tag("[equipment type]").Str("[coat]").Int(0)
            .Tag("[usable job]").Str("[all]"));
        fx.List(ItemCatalog.StackableListPath,
            (PotionCode, "test/potion.stk"),
            (MaterialCode, "test/material.stk"));
        fx.Tokens("stackable/test/potion.stk", w => w
            .Tag("[stackable type]").Str("[waste]").Int(0)
            .Tag("[stack limit]").Int(100));
        fx.Tokens("stackable/test/material.stk", w => w
            .Tag("[stackable type]").Str("[material]").Int(0)
            .Tag("[stack limit]").Int(1000));
        return new GameData(fx.Build());
    }

    private static HeroProgress OnlineHero()
    {
        var entry = new OnlineCharacterEntry(9301, Encoding.ASCII.GetBytes("synth"), 0, 0, 0, 20, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return HeroProgress.CreateOnline(BuildGameData(), null, OnlineHeroProfile.FromEntry(entry, 0));
    }

    private static OnlineItemSyncData WornOnly(params OnlineItemSlotEntry[] worn) =>
        new OnlineItemSyncData(false, 0L, null, null, worn, hasBagData: false, hasEquipData: true);

    private static OnlineItemSyncData BagOnly(params OnlineItemSlotEntry[] bag) =>
        new OnlineItemSyncData(false, 0L, bag, null, null, hasBagData: true, hasEquipData: false);

    private static ItemInfo SoleWorn(HeroProgress hero)
    {
        Assert.Equal(1, hero.Worn.EquippedCount);
        ItemInfo weapon = hero.Worn.Get(EquipmentParts.Weapon);
        Assert.NotNull(weapon);
        return weapon;
    }

    private static ItemInfo SoleBagEquipment(HeroProgress hero)
    {
        ItemStack stack = Assert.Single(hero.Bag.Tab(InventoryTab.Equipment));
        Assert.Equal(1, stack.Count);
        return stack.Info;
    }

    private static void AssertInstance(ItemInfo actual, bool expectInstance, int upgrade, bool amplified, bool identified, int enchant, int grade)
    {
        Assert.Equal(WeaponCode, actual.Code);
        if (!expectInstance)
        {
            Assert.Null(actual.Instance);
            Assert.False(actual.IsInstance);
            return;
        }
        Assert.True(actual.IsInstance);
        EquipInstance state = actual.Instance;
        Assert.NotNull(state);
        Assert.Equal(upgrade, state.Upgrade);
        Assert.Equal(amplified, state.Amplified);
        Assert.Equal(identified, state.Identified);
        Assert.Equal(enchant, state.EnchantCode);
        Assert.Equal(grade, state.GradeValue);
        Assert.False(state.Damaged);
    }

    // ───────────────────────── 簇 D:装备个体实例的判定与钳制 ─────────────────────────

    [Theory]
    //   线上给的 强化   增幅   已鉴定  附魔编号  品级  | 期望:有个体实例  强化  增幅   已鉴定  附魔  品级
    [InlineData(0, false, true, 0u, 0, false, 0, false, true, 0, 0)]               // 全默认:共享定义,没有个体实例
    [InlineData(1, false, true, 0u, 0, true, 1, false, true, 0, 0)]                // 只有强化 1
    [InlineData(0, true, true, 0u, 0, true, 0, true, true, 0, 0)]                  // 只有增幅
    [InlineData(0, false, false, 0u, 0, true, 0, false, false, 0, 0)]              // 只有未鉴定
    [InlineData(0, false, true, 1u, 0, true, 0, false, true, 1, 0)]                // 只有附魔编号 1(最小的非零)
    [InlineData(0, false, true, 905001u, 0, true, 0, false, true, 905001, 0)]      // 只有附魔编号(一个真实量级的合成值)
    [InlineData(0, false, true, 0u, 1, true, 0, false, true, 0, 1)]                // 只有品级 1
    [InlineData(255, true, false, 905002u, 88, true, 255, true, false, 905002, 88)] // 每个字段同时非默认
    public void Worn_and_bag_equipment_get_an_own_instance_exactly_when_some_field_is_non_default(
        int upgrade, bool amplified, bool identified, uint enchantCode, int grade,
        bool expectInstance, int expectUpgrade, bool expectAmplified, bool expectIdentified, int expectEnchant, int expectGrade)
    {
        var entry = new OnlineItemSlotEntry(10, WeaponCode, 1, upgrade, amplified, identified, enchantCode, grade);

        HeroProgress worn = OnlineHero();
        worn.ApplyOnlineItems(WornOnly(entry));
        AssertInstance(SoleWorn(worn), expectInstance, expectUpgrade, expectAmplified, expectIdentified, expectEnchant, expectGrade);

        // 同一个条目放在背包里:判定是同一个函数,结论必须一样
        HeroProgress bag = OnlineHero();
        bag.ApplyOnlineItems(BagOnly(new OnlineItemSlotEntry(9, WeaponCode, 1, upgrade, amplified, identified, enchantCode, grade)));
        AssertInstance(SoleBagEquipment(bag), expectInstance, expectUpgrade, expectAmplified, expectIdentified, expectEnchant, expectGrade);
    }

    [Theory]
    [InlineData(int.MinValue, false, 0)]  // 负数夹成 0,别的字段又全是默认 → 没有个体实例
    [InlineData(-5, false, 0)]
    [InlineData(-1, false, 0)]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 1)]
    [InlineData(254, true, 254)]
    [InlineData(255, true, 255)]          // 字节上限,原样保留
    [InlineData(256, true, 255)]          // 刚越界,夹成 255
    [InlineData(1000, true, 255)]
    [InlineData(int.MaxValue, true, 255)]
    public void Equipment_upgrade_level_is_clamped_into_zero_through_255(int upgrade, bool expectInstance, int expectUpgrade)
    {
        HeroProgress hero = OnlineHero();

        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: upgrade)));

        AssertInstance(SoleWorn(hero), expectInstance, expectUpgrade, false, true, 0, 0);
    }

    [Theory]
    [InlineData(0u, false, 0)]
    [InlineData(1u, true, 1)]
    [InlineData(905001u, true, 905001)]
    [InlineData(2147483646u, true, 2147483646)]
    [InlineData(2147483647u, true, 2147483647)]    // int.MaxValue 原样保留
    [InlineData(2147483648u, false, 0)]            // 超出 int 范围的附魔编号当作没有(别的字段又全是默认)
    [InlineData(4294967295u, false, 0)]
    public void Equipment_enchant_code_beyond_int_range_is_dropped_instead_of_wrapping_negative(uint enchantCode, bool expectInstance, int expectEnchant)
    {
        HeroProgress hero = OnlineHero();

        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, enchantCode: enchantCode)));

        AssertInstance(SoleWorn(hero), expectInstance, 0, false, true, expectEnchant, 0);
    }

    [Theory]
    [InlineData(int.MinValue, false, 0)]  // 负的品级夹成 0
    [InlineData(-1, false, 0)]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 1)]
    [InlineData(88, true, 88)]
    [InlineData(int.MaxValue, true, int.MaxValue)]
    public void Equipment_grade_value_below_zero_is_clamped_to_zero(int grade, bool expectInstance, int expectGrade)
    {
        HeroProgress hero = OnlineHero();

        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, gradeValue: grade)));

        AssertInstance(SoleWorn(hero), expectInstance, 0, false, true, 0, expectGrade);
    }

    [Fact]
    public void Non_default_fields_on_a_stackable_entry_never_create_an_equipment_instance()
    {
        HeroProgress hero = OnlineHero();

        hero.ApplyOnlineItems(BagOnly(new OnlineItemSlotEntry(9, MaterialCode, 6, upgradeLevel: 9, isAmplified: true, isIdentified: false, enchantCode: 905001, gradeValue: 7)));

        ItemStack stack = Assert.Single(hero.Bag.Tab(InventoryTab.Material));
        Assert.Equal(6, stack.Count);
        Assert.Equal(MaterialCode, stack.Info.Code);
        Assert.False(stack.Info.IsInstance);
        Assert.Null(stack.Info.Instance);
    }

    // ───────────────────────── OnlineItemSlotEntry 的构造缺省与参数落位 ─────────────────────────

    [Fact]
    public void Slot_entry_built_from_only_a_slot_and_a_code_is_one_plain_item()
    {
        var entry = new OnlineItemSlotEntry(14, 901002);

        Assert.Equal((ushort)14, entry.Slot);
        Assert.Equal(901002u, entry.ItemCode);
        Assert.Equal(1, entry.Count);
        Assert.Equal(0, entry.UpgradeLevel);
        Assert.False(entry.IsAmplified);
        Assert.True(entry.IsIdentified);
        Assert.Equal(0u, entry.EnchantCode);
        Assert.Equal(0, entry.GradeValue);
    }

    [Fact]
    public void Slot_entry_keeps_every_constructor_argument_in_its_own_property()
    {
        var entry = new OnlineItemSlotEntry(311, 901001, 17, 9, true, false, 905002, 41);

        Assert.Equal((ushort)311, entry.Slot);
        Assert.Equal(901001u, entry.ItemCode);
        Assert.Equal(17, entry.Count);
        Assert.Equal(9, entry.UpgradeLevel);
        Assert.True(entry.IsAmplified);
        Assert.False(entry.IsIdentified);
        Assert.Equal(905002u, entry.EnchantCode);
        Assert.Equal(41, entry.GradeValue);
    }

    [Fact]
    public void Bag_and_quick_entries_built_without_a_count_each_add_exactly_one_item()
    {
        HeroProgress hero = OnlineHero();
        var data = new OnlineItemSyncData(
            hasGold: false,
            gold: 0L,
            bagEntries: new[] { new OnlineItemSlotEntry(9, MaterialCode) },
            quickEntries: new[] { new OnlineItemSlotEntry(3, PotionCode) },
            wornEquips: null,
            hasBagData: true,
            hasEquipData: false);

        OnlineItemSyncReport report = hero.ApplyOnlineItems(data);

        Assert.Equal(1, hero.Bag.CountOf(MaterialCode));
        Assert.Equal(1, hero.Bag.CountOf(PotionCode));
        Assert.Equal(PotionCode, hero.QuickCodeAt(0));
        Assert.Equal(1, hero.QuickCountAt(0));
        Assert.Equal(1, report.BagApplied);
        Assert.Equal(1, report.QuickApplied);
        Assert.Equal(0, report.OverflowItemCount);
    }

    // ───────────────────────── 背包与快捷栏里的装备 ─────────────────────────

    [Fact]
    public void Equipment_in_bag_and_quick_entries_keeps_one_item_per_stack_with_its_own_instance()
    {
        HeroProgress hero = OnlineHero();
        var data = new OnlineItemSyncData(
            hasGold: false,
            gold: 0L,
            bagEntries: new[]
            {
                new OnlineItemSlotEntry(9, WeaponCode, 6, upgradeLevel: 3),
                new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 7, isAmplified: true),
                new OnlineItemSlotEntry(11, CoatCode, 4),
            },
            quickEntries: new[] { new OnlineItemSlotEntry(3, WeaponCode, 5, upgradeLevel: 9) },
            wornEquips: null,
            hasBagData: true,
            hasEquipData: false);

        OnlineItemSyncReport report = hero.ApplyOnlineItems(data);

        // 装备不叠加:无论 Data 位多大都是一件一格;快捷栏条目里的装备也先进背包(而且带着自己的个体实例)
        Assert.Equal(3, report.BagApplied);
        IReadOnlyList<ItemStack> equipment = hero.Bag.Tab(InventoryTab.Equipment);
        Assert.Collection(equipment,
            s => AssertStack(s, WeaponCode, upgrade: 9),   // 快捷栏条目先于背包条目进包
            s => AssertStack(s, WeaponCode, upgrade: 3),
            s => AssertStack(s, WeaponCode, upgrade: 7),
            s => AssertStack(s, CoatCode, upgrade: -1));   // -1:没有个体实例
        Assert.True(equipment[2].Info.Instance.Amplified);
        Assert.Equal(4, hero.Bag.CountOf(WeaponCode) + hero.Bag.CountOf(CoatCode));
    }

    private static void AssertStack(ItemStack stack, int code, int upgrade)
    {
        Assert.Equal(1, stack.Count);
        Assert.Equal(code, stack.Info.Code);
        if (upgrade < 0)
        {
            Assert.Null(stack.Info.Instance);
        }
        else
        {
            Assert.Equal(upgrade, stack.Info.Instance.Upgrade);
        }
    }

    // ───────────────────────── 已穿着装备的重发与替换 ─────────────────────────

    [Fact]
    public void Reapplying_identical_worn_equipment_is_not_a_change_and_any_difference_replaces_it()
    {
        HeroProgress hero = OnlineHero();
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 12, isAmplified: true)));
        ItemInfo placed = SoleWorn(hero);
        int version = hero.Worn.Version;
        Assert.Equal(12, placed.Instance.Upgrade);

        // 一字不差地再发一次:穿着的还是原来那个对象,栏位版本不动(宿主不会白白重算外观与面板)
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 12, isAmplified: true)));
        Assert.Same(placed, SoleWorn(hero));
        Assert.Equal(version, hero.Worn.Version);

        // 只差一级强化:换成新对象,版本恰好 +1
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 13, isAmplified: true)));
        ItemInfo upgraded = SoleWorn(hero);
        Assert.NotSame(placed, upgraded);
        Assert.Equal(13, upgraded.Instance.Upgrade);
        Assert.Equal(version + 1, hero.Worn.Version);

        // 只差增幅标志:同样要换
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 13)));
        Assert.False(SoleWorn(hero).Instance.Amplified);
        Assert.Equal(version + 2, hero.Worn.Version);

        // 同一部位换成另一个定义(两边都没有个体实例,"实例相等"但定义不同):必须真的换掉
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, OtherWeaponCode, 1)));
        ItemInfo other = SoleWorn(hero);
        Assert.Equal(OtherWeaponCode, other.Code);
        Assert.Null(other.Instance);
        Assert.Equal(version + 3, hero.Worn.Version);

        // 再发同一件:不变
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, OtherWeaponCode, 1)));
        Assert.Same(other, SoleWorn(hero));
        Assert.Equal(version + 3, hero.Worn.Version);

        // 同一定义从"有实例"回到"全默认":换回共享定义
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, OtherWeaponCode, 1, upgradeLevel: 4)));
        Assert.Equal(4, SoleWorn(hero).Instance.Upgrade);
        Assert.Equal(version + 4, hero.Worn.Version);
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, OtherWeaponCode, 1)));
        Assert.Null(SoleWorn(hero).Instance);
        Assert.Equal(version + 5, hero.Worn.Version);

        // 数据里没有这个部位:脱下,版本 +1;空栏再同步一次不动
        hero.ApplyOnlineItems(WornOnly());
        Assert.Equal(0, hero.Worn.EquippedCount);
        Assert.Equal(version + 6, hero.Worn.Version);
        hero.ApplyOnlineItems(WornOnly());
        Assert.Equal(version + 6, hero.Worn.Version);
    }

    [Fact]
    public void Worn_equipment_data_without_the_equip_flag_leaves_the_worn_slots_untouched()
    {
        HeroProgress hero = OnlineHero();
        hero.ApplyOnlineItems(WornOnly(new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 5)));
        int version = hero.Worn.Version;

        OnlineItemSyncReport report = hero.ApplyOnlineItems(new OnlineItemSyncData(
            false, 0L, null, null, new[] { new OnlineItemSlotEntry(12, CoatCode, 1) }, hasBagData: false, hasEquipData: false));

        Assert.False(report.EquipsSynced);
        Assert.Equal(0, report.EquipsApplied);
        Assert.Equal(WeaponCode, SoleWorn(hero).Code);
        Assert.Equal(version, hero.Worn.Version);
    }

    // ───────────────────────── 金币与数据包的拷贝语义 ─────────────────────────

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(-1L, 0L)]
    [InlineData(long.MinValue, 0L)]
    [InlineData(4294967295L, 4294967295L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void Synced_gold_is_applied_verbatim_with_negative_values_clamped_to_zero(long gold, long expected)
    {
        HeroProgress hero = OnlineHero();
        hero.ApplyOnlineItems(new OnlineItemSyncData(true, 777L, null, null, null, false, false));
        Assert.Equal(777L, hero.Gold);

        OnlineItemSyncReport synced = hero.ApplyOnlineItems(new OnlineItemSyncData(true, gold, null, null, null, false, false));

        Assert.True(synced.GoldSynced);
        Assert.Equal(expected, hero.Gold);

        // 数据包没有金币标志:金币保持上一次的取值,即使 Gold 字段里有别的数
        OnlineItemSyncReport kept = hero.ApplyOnlineItems(new OnlineItemSyncData(false, 123456L, null, null, null, false, false));
        Assert.False(kept.GoldSynced);
        Assert.Equal(expected, hero.Gold);
        Assert.Equal(0L, hero.GoldGained);
    }

    [Fact]
    public void Sync_data_copies_its_entry_lists_skips_null_entries_and_keeps_order()
    {
        var bag = new List<OnlineItemSlotEntry>
        {
            new OnlineItemSlotEntry(30, MaterialCode, 2),
            null,
            new OnlineItemSlotEntry(12, PotionCode, 3),
        };
        var quick = new List<OnlineItemSlotEntry> { null, new OnlineItemSlotEntry(4, PotionCode, 8) };
        var worn = new List<OnlineItemSlotEntry> { new OnlineItemSlotEntry(21, CoatCode, 1), null };

        var data = new OnlineItemSyncData(true, 5L, bag, quick, worn, true, true);

        // 构造之后改动原列表,数据包不受影响
        bag.Add(new OnlineItemSlotEntry(99, MaterialCode, 99));
        quick.Clear();
        worn[0] = new OnlineItemSlotEntry(10, WeaponCode, 1);

        Assert.Collection(data.BagEntries,
            e => Assert.Equal((ushort)30, e.Slot),
            e => Assert.Equal((ushort)12, e.Slot));
        OnlineItemSlotEntry onlyQuick = Assert.Single(data.QuickEntries);
        Assert.Equal((ushort)4, onlyQuick.Slot);
        Assert.Equal(8, onlyQuick.Count);
        OnlineItemSlotEntry onlyWorn = Assert.Single(data.WornEquips);
        Assert.Equal((ushort)21, onlyWorn.Slot);
        Assert.True(data.HasGold);
        Assert.Equal(5L, data.Gold);
        Assert.True(data.HasBagData);
        Assert.True(data.HasEquipData);
    }

    [Fact]
    public void Sync_data_built_from_null_or_all_null_lists_has_empty_lists()
    {
        var fromNull = new OnlineItemSyncData(false, 0L, null, null, null, false, false);
        var fromNulls = new OnlineItemSyncData(false, 0L,
            new OnlineItemSlotEntry[] { null, null }, Array.Empty<OnlineItemSlotEntry>(), new OnlineItemSlotEntry[] { null }, false, false);

        foreach (OnlineItemSyncData data in new[] { fromNull, fromNulls })
        {
            Assert.Empty(data.BagEntries);
            Assert.Empty(data.QuickEntries);
            Assert.Empty(data.WornEquips);
        }
    }
}
