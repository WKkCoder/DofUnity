using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Render.Tests.WardrobeFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 装扮会话(r15-avatarhost):规则层的装扮栏与渲染层的衣柜接成一个会话,交给城镇主角。验收口径:穿上、脱下之后城镇主角的图层序列真的变(不画像素,直接断言
/// 图层顺序与图片路径);换武器、穿脱时装互不丢对方;装扮不混进装备栏与攻击面板;城镇世界每次造会话都给主角穿上装扮。物品与素材全是合成的。
/// </summary>
public class AvatarSessionTests
{
    private const string BaseNames = "skin coat_a pants_a shoes_a hair_a cap_a swordb";
    private const string CoatNames = "coat_b skin coat_a pants_a shoes_a hair_a cap_a swordb";

    // ---------------------------------------------------------------- 城镇主角的图层序列

    [Fact]
    public void WearingACoatChangesTheTownHeroLayerSequenceAndTakingItOffRestoresIt()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        string[] before = AvatarHostRig.Paths(hero.Outfit);
        string skin = hero.Outfit.Layers.Single(l => l.Name == "skin").ImagePath;
        Assert.Equal(BaseNames, AvatarHostRig.Names(hero.Outfit));

        ItemInfo coat = h.Avatar(3);
        h.Give(coat);
        h.WearOk(coat.Code);
        Assert.True(h.Session.Sync(hero));

        // 新上衣两层:后层(600)排到皮肤前面,前层(1110)就在默认上衣原来的位置;别的图层原样
        Assert.Equal(CoatNames, AvatarHostRig.Names(hero.Outfit));
        Assert.Equal(
            new[] { CoatOneBackArt.Path, skin, CoatOneFrontArt.Path, PantsDefaultArt.Path, ShoesDefaultArt.Path, HairDefaultArt.Path, CapDefaultArt.Path, BladeStartArt.Path },
            AvatarHostRig.Paths(hero.Outfit));
        Assert.Same(h.Session.Wardrobe.Current, hero.Outfit);

        Assert.True(h.Session.TakeOff(3));
        Assert.True(h.Session.Sync(hero));
        Assert.Equal(before, AvatarHostRig.Paths(hero.Outfit));
        Assert.Same(h.Rig.Base, hero.Outfit);                                    // 一件也没穿时就是基础外观本身
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryPartChangesExactlyItsOwnLayersInTheTownHero(int part)
    {
        using var h = new AvatarHostRig();
        (Equ worn, Equ[] baseEqu) = PartCase(part);
        TownHero hero = h.NewHero();
        string[] before = AvatarHostRig.Paths(hero.Outfit);
        string baseSkin = hero.Outfit.Layers.Single(l => l.Name == "skin").ImagePath;

        ItemInfo item = h.Avatar(part);
        h.Give(item);
        h.WearOk(item.Code);
        Assert.True(h.Session.Sync(hero));
        string[] after = AvatarHostRig.Paths(hero.Outfit);

        // 预期:这一部位的基础图层被新件的图层取代,别的图层一层不少、相对次序不变(皮肤是同一个"皮肤"图层换一张图)
        string[] expectedRemoved = part == 8
            ? new[] { baseSkin }
            : baseEqu.SelectMany(e => e.Layers).Select(l => l.Art.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        string[] expectedAdded = part == 8
            ? new[] { Skin1.Path }
            : worn.Layers.Select(l => l.Art.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedRemoved, before.Except(after).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedAdded, after.Except(before).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(before.Length - expectedRemoved.Length + expectedAdded.Length, after.Length);
        Assert.Equal(before.Where(after.Contains), after.Where(before.Contains));

        Assert.True(h.Session.TakeOff(part));
        Assert.True(h.Session.Sync(hero));
        Assert.Equal(before, AvatarHostRig.Paths(hero.Outfit));
    }

    [Fact]
    public void AllNineWearablePartsTogetherGiveTheSameLookRegardlessOfTheOrderTheyWereWornIn()
    {
        string expected = null;
        foreach (int[] order in new[] { new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, new[] { 8, 7, 6, 5, 4, 3, 2, 1, 0 }, new[] { 4, 0, 8, 2, 6, 1, 7, 3, 5 } })
        {
            using var h = new AvatarHostRig();
            TownHero hero = h.NewHero();
            foreach (int part in order)
            {
                ItemInfo item = h.Avatar(part);
                h.Give(item);
                h.WearOk(item.Code);
                h.Session.Sync(hero);
            }
            Assert.Equal(9, h.Session.Slots.WornCount);
            string shown = string.Join("|", AvatarHostRig.Paths(hero.Outfit));
            expected ??= shown;
            Assert.Equal(expected, shown);
        }
    }

    [Fact]
    public void SwappingACoatPutsTheOldOneBackInTheCellTheNewOneCameFromAndChangesTheLook()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo one = h.Avatar(3);
        ItemInfo two = h.Make(AvatarHostRig.CodeCoatTwo, CoatTwo, 3);
        h.Give(one, two);
        h.WearOk(one.Code);
        h.Session.Sync(hero);
        Assert.Equal(CoatNames, AvatarHostRig.Names(hero.Outfit));

        int cell = h.IndexOf(two.Code);
        h.WearOk(two.Code);
        Assert.True(h.Session.Sync(hero));
        Assert.Equal(one.Code, h.Codes()[cell]);                                  // 旧件回到新件原来的那一格
        Assert.Equal(two.Code, h.Session.Slots.Get(3).Code);
        Assert.Equal(BaseNames, AvatarHostRig.Names(hero.Outfit));                // 新上衣只有一层,放在默认上衣的位置上
        Assert.Contains(CoatTwoArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(CoatOneFrontArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(CoatDefaultArt.Path, AvatarHostRig.Paths(hero.Outfit));
    }

    [Fact]
    public void ACoatThatCannotBeDrawnIsRefusedAndNothingMoves()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo gone = h.Make(AvatarHostRig.CodeCoatNoImage, CoatNoImage, 3);
        ItemInfo hat = h.Make(AvatarHostRig.CodeHatOtherJob, HatOtherJob, 0);
        h.Give(gone, hat);
        int[] codes = h.Codes();

        Assert.Equal(EquipResult.NotShowable, h.Session.Wear(h.IndexOf(gone.Code)));       // 图片不在图包里
        Assert.Equal(EquipResult.NotShowable, h.Session.Wear(h.IndexOf(hat.Code)));        // 本职业没有这件的动画块
        Assert.Equal(codes, h.Codes());
        Assert.Equal(0, h.Session.Slots.WornCount);
        Assert.Equal(0, h.Session.Wardrobe.WornCount);
        Assert.False(h.Session.Sync(hero));
        Assert.Same(h.Rig.Base, hero.Outfit);
    }

    [Fact]
    public void TheRulesFailureComesBeforeTheCannotDrawOne()
    {
        using var h = new AvatarHostRig(level: 20);
        ItemInfo wrongClass = h.Make(AvatarHostRig.CodeWrongClassHat, HatOtherJob, 0, job: "[fighter]");
        ItemInfo tooHigh = h.Make(AvatarHostRig.CodeHighLevelHat, HatOtherJob, 0, level: 50);
        h.Give(wrongClass, tooHigh);

        // 这两件画面上都画不出来(只有别的职业的动画块),但规则链先失败,报的是规则的原因
        Assert.False(h.Session.CanShow(wrongClass));
        Assert.False(h.Session.CanShow(tooHigh));
        Assert.Equal(EquipResult.WrongJob, h.Session.Wear(h.IndexOf(wrongClass.Code)));
        Assert.Equal(EquipResult.LevelTooLow, h.Session.Wear(h.IndexOf(tooHigh.Code)));
        Assert.Equal(2, h.Codes().Length);
        Assert.Equal(0, h.Session.Slots.WornCount);
    }

    [Fact]
    public void TakingOffNeedsAWornItemAndRoomInTheBagAndLeavesEverythingAloneWhenItFails()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo coat = h.Avatar(3);
        h.Give(coat);
        Assert.False(h.Session.TakeOff(3));                                        // 空的槽
        Assert.False(h.Session.TakeOff(EquipmentParts.Weapon));                    // 不是装扮部位
        Assert.False(h.Session.TakeOff(-1));

        h.WearOk(coat.Code);
        h.Session.Sync(hero);
        h.FillBag();                                                               // 装备页没有空格了
        string[] dressed = AvatarHostRig.Paths(hero.Outfit);
        Assert.False(h.Session.TakeOff(3));
        Assert.Equal(coat.Code, h.Session.Slots.Get(3).Code);                      // 还穿着
        Assert.NotNull(h.Session.Wardrobe.Worn(3));                                // 外观也还在
        Assert.False(h.Session.Sync(hero));
        Assert.Equal(dressed, AvatarHostRig.Paths(hero.Outfit));
    }

    // ---------------------------------------------------------------- 同步

    [Fact]
    public void SyncChangesTheHeroOnlyWhenTheLookDiffersAndFollowsTheCurrentWardrobeLook()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        Assert.False(h.Session.Sync(hero));                                        // 一致:什么也不做
        Assert.False(h.Session.Sync(null));

        ItemInfo hat = h.Avatar(0);
        h.Give(hat);
        h.WearOk(hat.Code);
        Assert.True(h.Session.Sync(hero));
        Assert.False(h.Session.Sync(hero));                                        // 已经一致

        TownHero other = h.NewHero();                                              // 另一个主角(另一块区域的会话)也能穿上
        Assert.True(h.Session.Sync(other));
        Assert.Same(hero.Outfit, other.Outfit);
    }

    [Fact]
    public void ASessionWithoutAWardrobeRefusesToWearAndLeavesTheHeroAlone()
    {
        using var h = new AvatarHostRig();
        var bare = new AvatarSession(h.Rig.Assets, h.Progress, () => null);
        Assert.False(bare.Ready);
        Assert.Null(bare.Wardrobe);

        ItemInfo hat = h.Avatar(0);
        ItemInfo wrongClass = h.Make(AvatarHostRig.CodeWrongClassHat, Hat, 0, job: "[fighter]");
        h.Give(hat, wrongClass);
        Assert.Equal(EquipResult.NotShowable, bare.Wear(h.IndexOf(hat.Code)));
        Assert.Equal(EquipResult.WrongJob, bare.Wear(h.IndexOf(wrongClass.Code)));          // 规则的原因仍然优先
        Assert.False(bare.TakeOff(0));
        Assert.False(bare.CanShow(hat));
        TownHero hero = h.NewHero();
        Assert.False(bare.Sync(hero));
        Assert.Same(h.Rig.Base, hero.Outfit);
        Assert.Equal(2, h.Codes().Length);
    }

    [Fact]
    public void ABaseOutfitThatCannotBeReadMakesTheWardrobeUnavailableInsteadOfThrowing()
    {
        using var h = new AvatarHostRig();
        var broken = new AvatarSession(h.Rig.Assets, h.Progress, () => throw new DnfFormatException("合成的坏底座"));
        Assert.False(broken.Ready);
        // 默认的底座是游戏数据里的默认外观;合成的素材里没有那份角色脚本,同样退成"不可用"
        var byDefault = new AvatarSession(h.Rig.Assets, h.Progress);
        Assert.False(byDefault.Ready);
        Assert.True(h.Session.Ready);
    }

    [Fact]
    public void TheSessionRejectsMissingArguments()
    {
        using var h = new AvatarHostRig();
        Assert.Throws<ArgumentNullException>(() => new AvatarSession(null, h.Progress));
        Assert.Throws<ArgumentNullException>(() => new AvatarSession(h.Rig.Assets, null));
        Assert.Throws<ArgumentNullException>(() => h.Session.WornStats(null, h.NewHero()));
        Assert.Throws<ArgumentNullException>(() => h.Session.Capture(null));
        Assert.Throws<ArgumentNullException>(() => h.Session.Restore(null, h.Lookup));
        Assert.Throws<ArgumentNullException>(() => h.Session.Restore(new HeroSave(), null));
        Assert.Throws<ArgumentNullException>(() => h.Session.RestoreFromData(null));
    }

    [Fact]
    public void CanShowAnswersTheSameAsTheWearHookAndForgetsNothingAboutTheWornState()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        ItemInfo otherJob = h.Make(AvatarHostRig.CodeHatOtherJob, HatOtherJob, 0);
        ItemInfo noImage = h.Make(AvatarHostRig.CodeCoatNoImage, CoatNoImage, 3);
        ItemInfo aurora = h.Make(AvatarHostRig.CodeAurora, Aurora, 9);
        Assert.True(h.Session.CanShow(hat));
        Assert.False(h.Session.CanShow(otherJob));
        Assert.False(h.Session.CanShow(noImage));
        Assert.False(h.Session.CanShow(aurora));
        Assert.False(h.Session.CanShow(null));

        // 穿上之后答案不变(只取决于脚本与素材),再问也一样
        h.Give(hat);
        h.WearOk(hat.Code);
        Assert.True(h.Session.CanShow(hat));
        Assert.False(h.Session.CanShow(otherJob));
        Assert.True(h.Session.CanShow(h.Avatar(0)));
    }

    // ---------------------------------------------------------------- 武器

    [Fact]
    public void ChangingTheWeaponKeepsTheWornAvatarsAndWearingAvatarsKeepsTheWeapon()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo coat = h.Avatar(3);
        ItemInfo neck = h.Avatar(6);
        ItemInfo weapon = h.Make(AvatarHostRig.CodeWeapon, WeaponOther, EquipmentParts.Weapon);
        h.Give(coat, neck, weapon);
        h.WearOk(coat.Code);
        h.WearOk(neck.Code);
        h.Session.Sync(hero);
        Assert.Equal("neck_a coat_b skin coat_a pants_a shoes_a hair_a cap_a neck_b swordb", AvatarHostRig.Names(hero.Outfit));
        Assert.Contains(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));

        // 换武器是装备栏的事:穿上之后,同步给主角的外观换了武器、装扮都还在,次序不变
        Assert.Equal(EquipResult.Ok, h.Progress.Equip(h.IndexOf(weapon.Code)));
        Assert.True(h.Session.Sync(hero));
        Assert.Equal("neck_a coat_b skin coat_a pants_a shoes_a hair_a cap_a neck_b swordb", AvatarHostRig.Names(hero.Outfit));
        Assert.Contains(BladeOtherArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.Equal(2, h.Session.Wardrobe.WornCount);

        // 之后穿脱时装不会把武器退回起手的那把
        Assert.True(h.Session.TakeOff(6));
        Assert.True(h.Session.Sync(hero));
        Assert.Equal(CoatNames, AvatarHostRig.Names(hero.Outfit));
        Assert.Contains(BladeOtherArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));

        // 脱掉时装、再脱武器:外观退回起手武器(同 HeroLook.Apply 的"没穿退回起手武器"),脱下的时装不会跟着回来
        Assert.True(h.Session.TakeOff(3));
        Assert.True(h.Progress.Unequip(EquipmentParts.Weapon));
        Assert.True(h.Session.Sync(hero));
        Assert.Contains(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(BladeOtherArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.Equal(1, hero.Outfit.Layers.Count(l => l.Name == "swordb"));
        Assert.Equal(BaseNames, AvatarHostRig.Names(hero.Outfit));
    }

    [Fact]
    public void ATakenOffWeaponGoesBackToTheStarterWeaponWhileTheWornAvatarsStay()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo hat = h.Avatar(0);
        ItemInfo weapon = h.Make(AvatarHostRig.CodeWeapon, WeaponOther, EquipmentParts.Weapon);
        h.Give(hat, weapon);
        h.WearOk(hat.Code);
        Assert.Equal(EquipResult.Ok, h.Progress.Equip(h.IndexOf(weapon.Code)));
        Assert.True(h.Session.Sync(hero));
        Assert.Contains(BladeOtherArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));

        Assert.True(h.Progress.Unequip(EquipmentParts.Weapon));
        Assert.True(h.Session.Sync(hero));
        Assert.Contains(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(BladeOtherArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.Contains(HatArt.Path, AvatarHostRig.Paths(hero.Outfit));            // 时装还穿着
        Assert.Equal(BladeStartArt.Path, h.Session.Wardrobe.Base.Layers.Single(l => l.Name == "swordb").ImagePath);   // 衣柜的底座也退回去了
        Assert.False(h.Session.Sync(hero));                                        // 已经一致,再同步什么也不做
    }

    [Fact]
    public void AWeaponSlotItemWhoseScriptIsNotAWeaponLeavesTheLookAlone()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        // 武器格里放一件脚本其实是帽子的东西:读得出来,但不是武器——外观里不能多出这一层、也不能把武器顶掉
        ItemInfo odd = h.Make(AvatarHostRig.CodeWeapon, Hat, EquipmentParts.Weapon);
        h.Give(odd);
        Assert.Equal(EquipResult.Ok, h.Progress.Equip(h.IndexOf(odd.Code)));
        Assert.False(h.Session.Sync(hero));                                        // 主角本来就穿着基础外观,什么也没换
        Assert.Same(h.Rig.Base, hero.Outfit);
        Assert.Same(h.Rig.Base, h.Session.Wardrobe.Base);
        Assert.Contains(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.DoesNotContain(HatArt.Path, AvatarHostRig.Paths(hero.Outfit));
    }

    [Fact]
    public void AWeaponTheLookCannotBeBuiltForLeavesTheLookAlone()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo hat = h.Avatar(0);
        ItemInfo ghost = h.Gear(AvatarHostRig.CodeWeapon, EquipmentParts.Weapon);          // 脚本不存在的武器
        h.Give(hat, ghost);
        h.WearOk(hat.Code);
        Assert.Equal(EquipResult.Ok, h.Progress.Equip(h.IndexOf(ghost.Code)));
        Assert.True(h.Session.Sync(hero));                                         // 装扮照样穿上
        Assert.Contains(HatArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.Contains(BladeStartArt.Path, AvatarHostRig.Paths(hero.Outfit));     // 武器外观读不出来:保持原样
        Assert.False(h.Session.Sync(hero));
    }

    // ---------------------------------------------------------------- 攻击面板

    [Fact]
    public void WornAvatarsAreLeftOutOfTheWornEquipmentStats()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo hat = h.Avatar(0);
        ItemInfo coat = h.Avatar(3);
        h.Give(hat, coat);
        h.WearOk(hat.Code);
        h.WearOk(coat.Code);
        h.Session.Sync(hero);

        // 合成的数值表:每件装备脚本一个攻击值。装扮件故意给大数,看会不会被算进去
        EquipmentStats Read(string path)
        {
            if (HeroLook.SameEquipPath(path, hat.Path))
            {
                return new EquipmentStats(500, 0);
            }
            if (HeroLook.SameEquipPath(path, coat.Path))
            {
                return new EquipmentStats(0, 700);
            }
            return HeroLook.SameEquipPath(path, WeaponStart.FullPath) ? new EquipmentStats(10, 4) : default;
        }

        EquipmentStats naked = HeroPanel.WornEquipment(Read, h.NewHero(), h.Progress);
        EquipmentStats dressed = h.Session.WornStats(Read, hero);
        Assert.Equal((10, 4), (dressed.PhysicalAttack, dressed.MagicalAttack));
        Assert.Equal((naked.PhysicalAttack, naked.MagicalAttack), (dressed.PhysicalAttack, dressed.MagicalAttack));

        // 对照:不滤的话,装扮的数值会混进来(证明上面的断言有力度)
        EquipmentStats raw = HeroPanel.WornEquipment(Read, hero, h.Progress);
        Assert.Equal((510, 704), (raw.PhysicalAttack, raw.MagicalAttack));
    }

    [Fact]
    public void TheEquipmentStatsStillCountTheNormalEquipmentTheHeroWears()
    {
        using var h = new AvatarHostRig();
        TownHero hero = h.NewHero();
        ItemInfo hat = h.Avatar(0);
        ItemInfo weapon = h.Make(AvatarHostRig.CodeWeapon, WeaponOther, EquipmentParts.Weapon);
        ItemInfo ring = h.Gear(AvatarHostRig.CodeGearRing, EquipmentParts.Ring);
        h.Give(hat, weapon, ring);
        h.WearOk(hat.Code);
        Assert.Equal(EquipResult.Ok, h.Progress.Equip(h.IndexOf(weapon.Code)));
        Assert.Equal(EquipResult.Ok, h.Progress.Equip(h.IndexOf(ring.Code)));
        h.Session.Sync(hero);

        EquipmentStats Read(string path)
        {
            if (HeroLook.SameEquipPath(path, hat.Path))
            {
                return new EquipmentStats(500, 500);
            }
            if (HeroLook.SameEquipPath(path, weapon.Path))
            {
                return new EquipmentStats(20, 8);
            }
            return HeroLook.SameEquipPath(path, ring.Path) ? new EquipmentStats(3, 1) : default;
        }

        EquipmentStats stats = h.Session.WornStats(Read, hero);
        Assert.Equal((23, 9), (stats.PhysicalAttack, stats.MagicalAttack));        // 装备栏里的武器与戒指,不含帽子装扮,也不含被顶掉的起手武器
    }

    // ---------------------------------------------------------------- 装扮栏与普通装备栏互不相干

    [Fact]
    public void NormalEquipmentAndAvatarsOfTheSamePartLookAreIndependent()
    {
        using var h = new AvatarHostRig();
        ItemInfo gearCoat = h.Gear(AvatarHostRig.CodeGearCoat, EquipmentParts.Coat);
        ItemInfo avatarCoat = h.Avatar(3);
        h.Give(gearCoat, avatarCoat);
        Assert.Equal(EquipResult.Ok, h.Progress.Equip(h.IndexOf(gearCoat.Code)));
        int version = h.Progress.EquipVersion;
        int equipped = h.Progress.EquippedCount;

        h.WearOk(avatarCoat.Code);
        Assert.Equal(gearCoat.Code, h.Progress.Worn.Get(EquipmentParts.Coat).Code);        // 装备栏的上衣没被动
        Assert.Equal(avatarCoat.Code, h.Session.Slots.Get(3).Code);
        Assert.Equal((version, equipped), (h.Progress.EquipVersion, h.Progress.EquippedCount));

        Assert.True(h.Session.TakeOff(3));
        Assert.Equal(gearCoat.Code, h.Progress.Worn.Get(EquipmentParts.Coat).Code);
        Assert.Equal((version, equipped), (h.Progress.EquipVersion, h.Progress.EquippedCount));

        h.WearOk(avatarCoat.Code);
        Assert.True(h.Progress.Unequip(EquipmentParts.Coat));                      // 脱普通上衣不影响穿着的时装
        Assert.Equal(avatarCoat.Code, h.Session.Slots.Get(3).Code);
        Assert.Equal(1, h.Session.Wardrobe.WornCount);
    }

    [Fact]
    public void AnAvatarCannotBeWornFromTheItemsPageAndTheAvatarViewHoldsOnlyAvatars()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        ItemInfo gearCoat = h.Gear(AvatarHostRig.CodeGearCoat, EquipmentParts.Coat);
        h.Give(hat, gearCoat);

        Assert.Equal(EquipResult.NotWearable, h.Progress.Equip(h.IndexOf(hat.Code)));       // 装备栏没有时装的格子
        Assert.Equal(0, h.Progress.EquippedCount);
        Assert.Equal(0, h.Session.Slots.WornCount);

        AvatarBagView view = h.Session.View();
        Assert.Equal(new[] { hat.Code }, view.Avatars.Select(e => e.Item.Code));
        Assert.Equal(new[] { h.IndexOf(gearCoat.Code) }, view.GearIndexes);
        Assert.Equal(h.IndexOf(hat.Code), view.Avatars[0].BagIndex);
    }

    // ---------------------------------------------------------------- 城镇世界

    [Fact]
    public void TheTownWorldDressesTheHeroOfEverySessionItCreates()
    {
        using var h = new AvatarHostRig();
        var inner = new RecordingTownWorld(() => new HeroTownSession(h.NewHero()));
        var world = new AvatarTownWorld(inner, h.Session);
        ItemInfo coat = h.Avatar(3);
        h.Give(coat);

        IHeroSession plain = world.Create(world.Start);                            // 还没穿:基础外观
        Assert.Equal(BaseNames, AvatarHostRig.Names(plain.Hero.Outfit));

        h.WearOk(coat.Code);
        IHeroSession dressed = world.Create(world.Return);                         // 换区域、回城:新会话里的主角穿着装扮
        Assert.Equal(CoatNames, AvatarHostRig.Names(dressed.Hero.Outfit));
        Assert.Same(h.Session.Wardrobe.Current, dressed.Hero.Outfit);
        Assert.Equal(BaseNames, AvatarHostRig.Names(plain.Hero.Outfit));           // 先前的会话不会自己变(要宿主 Sync)
        Assert.Equal((2, world.Return), (inner.Creates, inner.LastCreated));

        Assert.True(h.Session.TakeOff(3));
        IHeroSession again = world.Create(world.Start);
        Assert.Equal(BaseNames, AvatarHostRig.Names(again.Hero.Outfit));
    }

    [Fact]
    public void TheFlowEnteringTheTownAgainStillShowsTheAvatars()
    {
        using var h = new AvatarHostRig();
        var inner = new RecordingTownWorld(() => new HeroTownSession(h.NewHero()));
        var flow = new GameFlow(new AvatarTownWorld(inner, h.Session), (path, run) => null, Array.Empty<DungeonEntry>());
        ItemInfo hat = h.Avatar(0);
        h.Give(hat);

        flow.EnterTown();
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.DoesNotContain(HatArt.Path, AvatarHostRig.Paths(flow.Session.Hero.Outfit));
        h.WearOk(hat.Code);
        Assert.True(h.Session.Sync(flow.Session.Hero));
        Assert.Contains(HatArt.Path, AvatarHostRig.Paths(flow.Session.Hero.Outfit));

        TownHero first = flow.Session.Hero;
        flow.EnterTown("back");                                                    // 回城:世界造一个新会话
        Assert.NotSame(first, flow.Session.Hero);
        Assert.Contains(HatArt.Path, AvatarHostRig.Paths(flow.Session.Hero.Outfit));
        Assert.Equal(2, inner.Creates);
    }

    [Fact]
    public void TheTownWorldHandsEverythingElseToTheWorldItWraps()
    {
        using var h = new AvatarHostRig();
        var splash = new SplashScene("s", null, null, null, 0, 0, 100);
        var inner = new RecordingTownWorld(() => new HeroTownSession(h.NewHero())) { SplashToGive = splash, ThroughToGive = new TownPlace(7, 73), RouteToGive = true };
        var world = new AvatarTownWorld(inner, h.Session);
        Assert.Same(inner, world.Inner);
        Assert.Same(h.Session, world.Avatar);

        Assert.Equal(inner.Start, world.Start);
        Assert.Equal(inner.Return, world.Return);
        Assert.NotEqual(world.Start, world.Return);                                // 起点与回城点是两个不同的地方,各走各的

        // 参数一个不少地交给被包的世界(各给互不相同的值,换了位置、丢了值都看得出来)
        var from = new TownPlace(7, 71);
        var to = new TownPlace(7, 72);
        var gate = new TownExit(new WorldRect(5, 6, 7, 8), 7, 74);
        Assert.Same(splash, world.Splash(from, to));
        Assert.Equal((from, to), (inner.LastSplash.From.Value, inner.LastSplash.To));
        Assert.Same(splash, world.Splash(null, from));
        Assert.Equal((false, from), (inner.LastSplash.From.HasValue, inner.LastSplash.To));
        Assert.Equal(new TownPlace(7, 73), world.Through(from, gate, true));
        Assert.Equal((from, gate, true), inner.LastThrough);
        Assert.Equal(new TownPlace(7, 73), world.Through(to, gate, false));
        Assert.Equal((to, gate, false), inner.LastThrough);
        Assert.True(world.TryRoute(to, out TownExit exit));
        Assert.Equal(72, exit.Area);
        Assert.Equal(to, inner.LastRouteFrom);
        Assert.Equal((2, 2, 1, 0), (inner.Splashes, inner.Throughs, inner.Routes, inner.Creates));

        inner.ThroughToGive = null;
        inner.RouteToGive = false;
        Assert.Null(world.Through(world.Start, default, false));
        Assert.False(world.TryRoute(world.Start, out _));
    }

    [Fact]
    public void TheTownWorldToleratesSessionsWithoutAHeroAndRejectsMissingArguments()
    {
        using var h = new AvatarHostRig();
        Assert.Null(new AvatarTownWorld(new RecordingTownWorld(() => null), h.Session).Create(new TownPlace(7, 70)));
        IHeroSession noHero = new GameFlowTests.FakeHero("none");
        Assert.Same(noHero, new AvatarTownWorld(new RecordingTownWorld(() => noHero), h.Session).Create(new TownPlace(7, 70)));
        var inner = new RecordingTownWorld(() => null);
        Assert.Throws<ArgumentNullException>(() => new AvatarTownWorld(null, h.Session));
        Assert.Throws<ArgumentNullException>(() => new AvatarTownWorld(inner, null));
    }
}
