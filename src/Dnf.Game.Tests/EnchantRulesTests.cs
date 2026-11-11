using static Dnf.Game.Tests.EnhanceFixtures;

namespace Dnf.Game.Tests;

/// <summary>
/// 附魔:宝珠换出卡片、卡的部位限制、新卡覆盖旧卡、错误码与次序。数据全是合成的:装备 900 起,宝珠 930,卡 931(武器、戒指)与 932(上衣、肩部)。
/// 次序来自服务端 <c>OnEnchantByBead</c> 的静态读码(见 EnchantRules.cs 头部);"部位限制来自卡的 [string data] 部位标签"是推断。
/// </summary>
public class EnchantRulesTests
{
    private static readonly ItemInfo Bead = Stack(930, new EnhanceData(monsterCardId: 931));

    private static readonly ItemInfo Card = Stack(931, new EnhanceData(
        enchantStats: Stats(EquipStat.PhysicalAttack, 7),
        enchantParts: new[] { EquipmentParts.Weapon, EquipmentParts.Ring }));

    private static readonly ItemInfo OtherCard = Stack(932, new EnhanceData(
        enchantStats: Stats(EquipStat.HpMax, 30),
        enchantParts: new[] { EquipmentParts.Coat, EquipmentParts.Shoulder }));

    private static readonly ItemInfo Weapon = Gear(900, EquipmentParts.Weapon);
    private static readonly ItemInfo Coat = Gear(901, EquipmentParts.Coat);

    private static ItemInfo Lookup(int code)
    {
        switch (code)
        {
            case 931: return Card;
            case 932: return OtherCard;
            default: return null;
        }
    }

    [Fact]
    public void TheCodesArePinnedToTheServerValues()
    {
        Assert.Equal(new[] { 0, 0x11, 0x13, 0x17 }, new[] { EnchantCodes.Ok, EnchantCodes.Invalid, EnchantCodes.World, EnchantCodes.Part });
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    [InlineData(21, true)]
    [InlineData(22, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void OnlyPartsTenToTwentyOneCanBeEnchanted(int part, bool expected)
    {
        Assert.Equal(expected, EnchantRules.IsEnchantable(part));
    }

    // ------------------------------------------------------------------ 卡的部位

    [Fact]
    public void ACardAllowsExactlyTheNamedParts()
    {
        Assert.True(EnchantRules.CardAllows(Card, EquipmentParts.Weapon));
        Assert.True(EnchantRules.CardAllows(Card, EquipmentParts.Ring));
        Assert.False(EnchantRules.CardAllows(Card, EquipmentParts.Coat));
        Assert.False(EnchantRules.CardAllows(Card, EquipmentParts.MagicStone));
        Assert.True(EnchantRules.CardAllows(OtherCard, EquipmentParts.Shoulder));
    }

    [Fact]
    public void AnUnenchantablePartIsRefusedEvenIfTheCardListsIt()
    {
        var odd = Stack(933, new EnhanceData(enchantParts: new[] { EquipmentParts.Creature, EquipmentParts.AuroraAvatar, EquipmentParts.Weapon }));
        Assert.False(EnchantRules.CardAllows(odd, EquipmentParts.Creature));
        Assert.False(EnchantRules.CardAllows(odd, EquipmentParts.AuroraAvatar));
        Assert.True(EnchantRules.CardAllows(odd, EquipmentParts.Weapon));
    }

    [Fact]
    public void ACardWithoutDataOrAPartListAllowsNothing()
    {
        Assert.False(EnchantRules.CardAllows(null, EquipmentParts.Weapon));
        Assert.False(EnchantRules.CardAllows(Stack(934), EquipmentParts.Weapon));
        Assert.False(EnchantRules.CardAllows(Stack(935, new EnhanceData(enchantStats: Stats(EquipStat.HpMax, 1))), EquipmentParts.Weapon));
    }

    // ------------------------------------------------------------------ 宝珠附魔

    [Fact]
    public void ABeadTurnsIntoItsCardOnTheEquipment()
    {
        EnchantResult r = EnchantRules.ApplyBead(Weapon, Bead, Lookup);
        Assert.True(r.Success);
        Assert.Equal(0, r.Code);
        Assert.True(r.ConsumesBead);
        Assert.Same(Card, r.Card);
        Assert.Equal(931, r.Equipment.Instance.EnchantCode);
        Assert.Equal(0, r.PreviousCardCode);
        Assert.Same(Weapon, r.Equipment.Definition);
        Assert.Null(Weapon.Instance);   // 共享的定义没动
    }

    [Fact]
    public void ANewCardOverwritesTheOldOneWithoutAnyRefund()
    {
        ItemInfo enchanted = With(Weapon, 0, enchant: 932);
        EnchantResult r = EnchantRules.ApplyBead(enchanted, Bead, Lookup);
        Assert.True(r.Success);
        Assert.Equal(931, r.Equipment.Instance.EnchantCode);
        Assert.Equal(932, r.PreviousCardCode);
        Assert.Equal(932, enchanted.Instance.EnchantCode);   // 原来那份拷贝没被改
    }

    [Fact]
    public void TheSameCardAgainStillSucceeds()
    {
        ItemInfo enchanted = With(Weapon, 0, enchant: 931);
        EnchantResult r = EnchantRules.ApplyBead(enchanted, Bead, Lookup);
        Assert.True(r.Success);
        Assert.Equal((931, 931), (r.Equipment.Instance.EnchantCode, r.PreviousCardCode));
    }

    [Fact]
    public void EnchantingKeepsTheRestOfTheEquipmentState()
    {
        ItemInfo gear = With(Weapon, 7, amplified: true, identified: false, grade: 3, damaged: true);
        ItemInfo after = EnchantRules.ApplyBead(gear, Bead, Lookup).Equipment;
        Assert.Equal(new EquipInstance(7, true, false, 931, 3, true), after.Instance);
    }

    [Fact]
    public void ACardOnTheWrongPartIsRefusedAndNothingChanges()
    {
        EnchantResult r = EnchantRules.ApplyBead(Coat, Bead, Lookup);
        Assert.False(r.Success);
        Assert.Equal(EnchantCodes.Part, r.Code);
        Assert.False(r.ConsumesBead);
        Assert.Same(Coat, r.Equipment);
        Assert.Same(Card, r.Card);
    }

    [Fact]
    public void AWrongPartDoesNotEraseAnExistingEnchantment()
    {
        ItemInfo enchanted = With(Coat, 0, enchant: 932);
        EnchantResult r = EnchantRules.ApplyBead(enchanted, Bead, Lookup);
        Assert.Equal(EnchantCodes.Part, r.Code);
        Assert.Same(enchanted, r.Equipment);
        Assert.Equal(932, r.PreviousCardCode);
    }

    [Fact]
    public void EveryPartTheCardNamesAcceptsIt()
    {
        foreach (int part in new[] { EquipmentParts.Weapon, EquipmentParts.Ring })
        {
            Assert.True(EnchantRules.ApplyBead(Gear(910 + part, part), Bead, Lookup).Success);
        }
        foreach (int part in new[] { EquipmentParts.Coat, EquipmentParts.Pants, EquipmentParts.Shoes, EquipmentParts.MagicStone })
        {
            Assert.Equal(EnchantCodes.Part, EnchantRules.ApplyBead(Gear(910 + part, part), Bead, Lookup).Code);
        }
    }

    // ------------------------------------------------------------------ 错误码

    [Fact]
    public void MissingPiecesAreInvalid()
    {
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(null, Bead, Lookup).Code);
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Weapon, null, Lookup).Code);
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Stack(940), Bead, Lookup).Code);      // 装备格里不是装备
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Weapon, Gear(941), Lookup).Code);     // 宝珠格里不是可叠加物品
        // 即使它带着有效的换卡编号,不是可叠加物品照样拒绝(只有"可叠加"这一关能拦住它)
        ItemInfo gearWithCardId = Gear(947, enhance: new EnhanceData(monsterCardId: 931));
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Weapon, gearWithCardId, Lookup).Code);
        Assert.True(EnchantRules.ApplyBead(Weapon, Bead, Lookup).Success);
    }

    [Fact]
    public void ABeadThatCannotBeTurnedIntoACardIsInvalid()
    {
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Weapon, Stack(942), Lookup).Code);                                         // 没有数据
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Weapon, Stack(943, new EnhanceData(setMaster: 1)), Lookup).Code);           // 卡片编号为 0
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Weapon, Stack(944, new EnhanceData(monsterCardId: 999)), Lookup).Code);     // 卡片定义查不到
    }

    [Fact]
    public void AFailedAttemptReportsTheExistingEnchantment()
    {
        ItemInfo enchanted = With(Weapon, 2, enchant: 932);
        Assert.Equal(932, EnchantRules.ApplyBead(enchanted, null, Lookup).PreviousCardCode);
        Assert.Equal(932, EnchantRules.ApplyBead(enchanted, Stack(942), Lookup).PreviousCardCode);
        Assert.Equal(0, EnchantRules.ApplyBead(null, Bead, Lookup).PreviousCardCode);
    }

    [Fact]
    public void TheWorldCheckComesAfterTheCardIsFoundAndBeforeThePartCheck()
    {
        ItemInfo seenEquip = null;
        ItemInfo seenBead = null;
        Func<ItemInfo, ItemInfo, bool> deny = (e, b) =>
        {
            seenEquip = e;
            seenBead = b;
            return false;
        };
        EnchantResult r = EnchantRules.ApplyBead(Weapon, Bead, Lookup, deny);
        Assert.Equal(EnchantCodes.World, r.Code);
        Assert.Same(Weapon, seenEquip);
        Assert.Same(Bead, seenBead);
        Assert.Same(Weapon, r.Equipment);

        // 世界兼容不过时,部位对不对不重要:先报 0x13
        Assert.Equal(EnchantCodes.World, EnchantRules.ApplyBead(Coat, Bead, Lookup, deny).Code);
        // 宝珠换不出卡片比世界兼容更早
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyBead(Weapon, Stack(942), Lookup, deny).Code);
        // 通过时照常
        Assert.True(EnchantRules.ApplyBead(Weapon, Bead, Lookup, (e, b) => true).Success);
        Assert.Equal(EnchantCodes.Part, EnchantRules.ApplyBead(Coat, Bead, Lookup, (e, b) => true).Code);
    }

    [Fact]
    public void ALookupIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => EnchantRules.ApplyBead(Weapon, Bead, null));
    }

    // ------------------------------------------------------------------ 直接用卡

    [Fact]
    public void ACardCanBeAppliedDirectlyWithTheSameRules()
    {
        EnchantResult ok = EnchantRules.ApplyCard(Weapon, Card);
        Assert.True(ok.Success);
        Assert.Equal(931, ok.Equipment.Instance.EnchantCode);

        EnchantResult wrong = EnchantRules.ApplyCard(Coat, Card);
        Assert.Equal(EnchantCodes.Part, wrong.Code);
        Assert.Same(Coat, wrong.Equipment);

        EnchantResult over = EnchantRules.ApplyCard(With(Coat, 0, enchant: 931), OtherCard);
        Assert.True(over.Success);
        Assert.Equal((932, 931), (over.Equipment.Instance.EnchantCode, over.PreviousCardCode));
    }

    [Fact]
    public void ApplyingNothingOrToANonEquipmentIsInvalid()
    {
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyCard(null, Card).Code);
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyCard(Weapon, null).Code);
        Assert.Equal(EnchantCodes.Invalid, EnchantRules.ApplyCard(Stack(945), Card).Code);
    }

    [Fact]
    public void AnItemThatIsNotACardCannotBeApplied()
    {
        // 没有 [enchant] 块与部位表:没有任何部位接受它
        Assert.Equal(EnchantCodes.Part, EnchantRules.ApplyCard(Weapon, Stack(946)).Code);
    }

    [Fact]
    public void ADamagedOrUnidentifiedEquipmentCanStillBeEnchanted()
    {
        // 附魔不看耐久与增幅鉴定(服务端 OnEnchantByBead 不检查它们)
        ItemInfo odd = With(Weapon, 3, amplified: true, identified: false, damaged: true);
        Assert.True(EnchantRules.ApplyBead(odd, Bead, Lookup).Success);
    }
}
