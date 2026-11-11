using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 存进仓库的资格(证据:服务端 CUser::exchange_interspace 0x0865f7b0 的个人仓库一支与账号金库一支、CAccountCargo::CheckInsertCondition 0x08289a4a、
/// CItem::IsImpossibleCharacCargo 0x085143be、IsTradeLimitAttachTypeItem 0x081201dc、CEquipItem::make_item 0x0851098a 与 CStackableItem::make_item 0x0850f8b8 的类别字节)。
/// 期望值是手抄的规则:个人仓库拒绝"脚本写了不让存的"、排除编号区间、任务类;账号金库要求 [packagable]、类别字节不在 4 到 8、不在排除区间、
/// 绑定类型不是 [trade]/[trade delete]、可叠加物品不是 [trade limit]。
/// </summary>
public class StorageEligibilityTests
{
    private static StorageSettings Plain(bool requirePackagable = false) => StorageFixtures.Settings(null, requirePackagable);

    private static StorageSettings WithFacts(int code, StorageItemFacts facts, bool requirePackagable = false)
    {
        return StorageFixtures.Settings(new Dictionary<int, StorageItemFacts> { [code] = facts }, requirePackagable);
    }

    // ------------------------------------------------------------------ 类别字节与绑定类型

    [Theory]
    [InlineData(EquipmentParts.Weapon, StorageEligibility.CategoryEquipment)]
    [InlineData(EquipmentParts.Coat, StorageEligibility.CategoryEquipment)]
    [InlineData(EquipmentParts.MagicStone, StorageEligibility.CategoryEquipment)]
    [InlineData(EquipmentParts.HatAvatar, StorageEligibility.CategoryAvatar)]
    [InlineData(EquipmentParts.AuroraAvatar, StorageEligibility.CategoryAvatar)]
    [InlineData(EquipmentParts.Creature, StorageEligibility.CategoryCreature)]
    [InlineData(EquipmentParts.ArtifactRed, StorageEligibility.CategoryArtifact)]
    [InlineData(EquipmentParts.ArtifactBlue, StorageEligibility.CategoryArtifact)]
    [InlineData(EquipmentParts.ArtifactGreen, StorageEligibility.CategoryArtifact)]
    [InlineData(EquipmentParts.Unknown, StorageEligibility.CategoryEquipment)]
    [InlineData(-1, StorageEligibility.CategoryEquipment)]
    public void EquipmentCategoryFollowsThePartRanges(int part, int expected)
    {
        Assert.Equal(expected, StorageEligibility.CategoryOf(StorageFixtures.Equip(900, part)));
    }

    [Theory]
    [InlineData(StackableTypes.Waste, StorageEligibility.CategoryConsumable)]
    [InlineData(StackableTypes.Material, StorageEligibility.CategoryMaterial)]
    [InlineData(2, StorageEligibility.CategoryMaterialExpertJob)]
    [InlineData(StackableTypes.Recipe, StorageEligibility.CategoryConsumable)]
    [InlineData(StackableTypes.Throw, StorageEligibility.CategoryConsumable)]
    [InlineData(StackableTypes.Quest, StorageEligibility.CategoryQuest)]
    [InlineData(9, StorageEligibility.CategoryCreatureStackable)]
    [InlineData(10, StorageEligibility.CategoryCreatureStackable)]
    [InlineData(StackableTypes.Etc, StorageEligibility.CategoryConsumable)]
    [InlineData(20, StorageEligibility.CategoryAvatarEmblem)]
    [InlineData(StackableTypes.Unknown, StorageEligibility.CategoryConsumable)]
    public void StackableCategoryFollowsTheStackableType(int type, int expected)
    {
        Assert.Equal(expected, StorageEligibility.CategoryOf(StorageFixtures.Stack(911, 100, type)));
    }

    [Fact]
    public void AMissingItemCountsAsPlainEquipmentForTheCategory()
    {
        Assert.Equal(StorageEligibility.CategoryEquipment, StorageEligibility.CategoryOf(null));
    }

    [Theory]
    [InlineData("[free]", StorageEligibility.AttachFree)]
    [InlineData("[trade]", StorageEligibility.AttachTrade)]
    [InlineData("[trade delete]", StorageEligibility.AttachTradeDelete)]
    [InlineData("[sealing]", StorageEligibility.AttachSealing)]
    [InlineData("[sealing trade]", StorageEligibility.AttachSealingTrade)]
    [InlineData("[account]", StorageEligibility.AttachAccount)]
    [InlineData("[trade limit]", StorageEligibility.AttachTradeLimit)]
    [InlineData("[TRADE Delete]", StorageEligibility.AttachTradeDelete)]
    [InlineData("[FREE]", StorageEligibility.AttachFree)]
    [InlineData("[Trade]", StorageEligibility.AttachTrade)]
    [InlineData("[Sealing]", StorageEligibility.AttachSealing)]
    [InlineData("[SEALING trade]", StorageEligibility.AttachSealingTrade)]
    [InlineData("[Account]", StorageEligibility.AttachAccount)]
    [InlineData("[TRADE LIMIT]", StorageEligibility.AttachTradeLimit)]
    [InlineData("[weird]", StorageEligibility.AttachUnknown)]
    [InlineData("", StorageEligibility.AttachUnknown)]
    [InlineData(null, StorageEligibility.AttachUnknown)]
    public void AttachTypeNumbersFollowTheServerOrder(string tag, int expected)
    {
        Assert.Equal(expected, StorageEligibility.AttachTypeOf(StorageFixtures.Equip(900, EquipmentParts.Coat, tag)));
    }

    [Fact]
    public void AttachTypeOfAMissingItemIsUnknownAndTheNumbersAreDistinct()
    {
        Assert.Equal(StorageEligibility.AttachUnknown, StorageEligibility.AttachTypeOf(null));
        var numbers = new[]
        {
            StorageEligibility.AttachFree, StorageEligibility.AttachTrade, StorageEligibility.AttachTradeDelete, StorageEligibility.AttachSealing,
            StorageEligibility.AttachSealingTrade, StorageEligibility.AttachAccount, StorageEligibility.AttachTradeLimit,
        };
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 7 }, numbers);
        Assert.Equal(-1, StorageEligibility.AttachUnknown);
    }

    [Fact]
    public void CategoryNumbersAreTheServerLiterals()
    {
        var numbers = new[]
        {
            StorageEligibility.CategoryEquipment, StorageEligibility.CategoryConsumable, StorageEligibility.CategoryMaterial, StorageEligibility.CategoryQuest,
            StorageEligibility.CategoryCreature, StorageEligibility.CategoryArtifact, StorageEligibility.CategoryCreatureStackable, StorageEligibility.CategoryAvatar,
            StorageEligibility.CategoryAvatarEmblem, StorageEligibility.CategoryMaterialExpertJob,
        };
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, numbers);
    }

    // ------------------------------------------------------------------ 个人仓库

    [Fact]
    public void PersonalAcceptsOrdinaryEquipmentAndStackables()
    {
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, StorageFixtures.Equip(900), Plain()));
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, StorageFixtures.Stack(911), Plain()));
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, StorageFixtures.Stack(911, 100, StackableTypes.Material), Plain()));
    }

    [Fact]
    public void PersonalRefusesWhatTheScriptForbidsAndTheQuestCategory()
    {
        ItemInfo gear = StorageFixtures.Equip(900);
        Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(StorageKind.Personal, gear, WithFacts(900, new StorageItemFacts(false, true, false))));
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, gear, WithFacts(900, new StorageItemFacts(true, false, true))));
        Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(StorageKind.Personal, StorageFixtures.Stack(911, 100, StackableTypes.Quest), Plain()));
    }

    [Fact]
    public void PersonalAcceptsTheOtherSpecialCategoriesAndEveryAttachType()
    {
        foreach (int part in new[] { EquipmentParts.Creature, EquipmentParts.ArtifactBlue, EquipmentParts.HatAvatar })
        {
            Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, StorageFixtures.Equip(900, part), Plain()));
        }
        foreach (string attach in new[] { "[free]", "[trade]", "[trade delete]", "[sealing]", "[sealing trade]", "[account]", "[trade limit]", "[weird]" })
        {
            Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, StorageFixtures.Stack(911, 100, StackableTypes.Waste, attach), Plain()));
        }
    }

    [Fact]
    public void ExcludedCodesAreRefusedByBothKindsWithInclusiveEnds()
    {
        StorageSettings settings = Plain();
        settings.ExcludedCodeFrom = 920;
        settings.ExcludedCodeTo = 925;
        foreach (StorageKind kind in new[] { StorageKind.Personal, StorageKind.Account })
        {
            Assert.Equal(StorageResult.Ok, StorageEligibility.Check(kind, StorageFixtures.Equip(919), settings));
            Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(kind, StorageFixtures.Equip(920), settings));
            Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(kind, StorageFixtures.Equip(925), settings));
            Assert.Equal(StorageResult.Ok, StorageEligibility.Check(kind, StorageFixtures.Equip(926), settings));
        }
    }

    // ------------------------------------------------------------------ 账号金库

    [Fact]
    public void AccountNeedsPackagableUnlessTheSwitchIsOff()
    {
        ItemInfo gear = StorageFixtures.Equip(900);
        Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(StorageKind.Account, gear, Plain(true)));
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Account, gear, WithFacts(900, new StorageItemFacts(true, false, false), true)));
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Account, gear, Plain(false)));
    }

    [Fact]
    public void DefaultSettingsAreTheFaithfulOnesSoAccountTakesNoItems()
    {
        Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(StorageKind.Account, StorageFixtures.Equip(900), null));
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, StorageFixtures.Equip(900), null));
        Assert.Equal(StorageResult.NoItem, StorageEligibility.Check(StorageKind.Personal, null, null));
        Assert.Equal(StorageResult.NoItem, StorageEligibility.Check(StorageKind.Account, null, Plain()));
    }

    [Theory]
    [InlineData(EquipmentParts.Weapon, true)]                       // 类别 1
    [InlineData(EquipmentParts.Creature, false)]                    // 类别 5
    [InlineData(EquipmentParts.ArtifactRed, false)]                 // 类别 6
    [InlineData(EquipmentParts.HatAvatar, false)]                   // 类别 8
    public void AccountRefusesCreatureArtifactAndAvatarEquipment(int part, bool allowed)
    {
        StorageResult expected = allowed ? StorageResult.Ok : StorageResult.NotAllowed;
        Assert.Equal(expected, StorageEligibility.Check(StorageKind.Account, StorageFixtures.Equip(900, part), Plain()));
    }

    [Theory]
    [InlineData(StackableTypes.Waste, true)]        // 类别 2
    [InlineData(StackableTypes.Material, true)]     // 类别 3:4 到 8 的下沿之外
    [InlineData(StackableTypes.Quest, false)]       // 类别 4:下沿
    [InlineData(9, false)]                          // 类别 7
    [InlineData(10, false)]                         // 类别 7
    [InlineData(20, true)]                          // 类别 9:上沿之外
    [InlineData(2, true)]                           // 类别 10
    public void AccountRefusesCategoriesFourToEight(int type, bool allowed)
    {
        StorageResult expected = allowed ? StorageResult.Ok : StorageResult.NotAllowed;
        Assert.Equal(expected, StorageEligibility.Check(StorageKind.Account, StorageFixtures.Stack(911, 100, type), Plain()));
    }

    [Theory]
    [InlineData("[free]", true)]
    [InlineData("[trade]", false)]
    [InlineData("[trade delete]", false)]
    [InlineData("[sealing]", true)]
    [InlineData("[sealing trade]", true)]
    [InlineData("[account]", true)]
    [InlineData("[weird]", true)]
    [InlineData(null, true)]
    public void AccountAttachRulesForEquipment(string attach, bool allowed)
    {
        StorageResult expected = allowed ? StorageResult.Ok : StorageResult.NotAllowed;
        Assert.Equal(expected, StorageEligibility.Check(StorageKind.Account, StorageFixtures.Equip(900, EquipmentParts.Coat, attach), Plain()));
    }

    [Fact]
    public void TradeLimitAttachOnlyBlocksStackables()
    {
        Assert.Equal(StorageResult.NotAllowed,
            StorageEligibility.Check(StorageKind.Account, StorageFixtures.Stack(911, 100, StackableTypes.Waste, "[trade limit]"), Plain()));
        Assert.Equal(StorageResult.Ok,
            StorageEligibility.Check(StorageKind.Account, StorageFixtures.Equip(900, EquipmentParts.Coat, "[trade limit]"), Plain()));
        Assert.Equal(StorageResult.Ok,
            StorageEligibility.Check(StorageKind.Account, StorageFixtures.Stack(911, 100, StackableTypes.Waste, "[sealing]"), Plain()));
    }

    [Fact]
    public void TheTwoVaultsDoNotShareTheirScriptFlags()
    {
        // [impossible contents] 的 charac cargo 只管个人仓库;[packagable] 只管账号金库。
        ItemInfo gear = StorageFixtures.Equip(900);
        StorageSettings noCargo = WithFacts(900, new StorageItemFacts(true, true, false), true);
        Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(StorageKind.Personal, gear, noCargo));
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Account, gear, noCargo));
        StorageSettings notPackagable = WithFacts(900, new StorageItemFacts(false, false, false), true);
        Assert.Equal(StorageResult.Ok, StorageEligibility.Check(StorageKind.Personal, gear, notPackagable));
        Assert.Equal(StorageResult.NotAllowed, StorageEligibility.Check(StorageKind.Account, gear, notPackagable));
    }
}
