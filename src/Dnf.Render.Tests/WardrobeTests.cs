using static Dnf.Render.Tests.WardrobeFixture;

namespace Dnf.Render.Tests;

/// <summary>
/// 装扮外观(r14-avatar):穿装扮按部位替换基础外观的图层,脱下恢复,图层按顺序键整体排序,武器与其余部位不受影响。
/// 全部用合成夹具(<see cref="WardrobeFixture"/>:每个图层是一块纯色矩形),断言落在像素上:
/// 哪些像素变了(只能在预期的图层矩形里)、变成了什么颜色、谁压着谁。
/// 基础外观自下而上:皮肤(键 650,推断值)、上衣 1111、裤子 1313、鞋 1414、头发 2222、帽子 2400、武器 3000。
/// </summary>
public class WardrobeTests
{
    private static readonly (int R, int G, int B) SkinColor = Skin0.Color;

    private static bool InAny(IEnumerable<Box> boxes, (int X, int Y) p) => boxes.Any(b => b.Contains(p.X, p.Y));

    private static void AssertSamePixels(RgbaCanvas expected, RgbaCanvas actual)
    {
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    // ---------------------------------------------------------------- 夹具自检与基础外观

    [Fact]
    public void TheBaseLookIsTheDefaultOutfitDrawnByKey()
    {
        using var rig = new WardrobeRig();
        Assert.Equal("skin coat_a pants_a shoes_a hair_a cap_a swordb", WardrobeRig.Names(rig.Base));
        RgbaCanvas shot = rig.Shot(rig.Base);
        Assert.Equal(CoatDefaultArt.Color, WardrobeRig.At(shot, 0, -20));
        Assert.Equal(PantsDefaultArt.Color, WardrobeRig.At(shot, 0, -10));
        Assert.Equal(ShoesDefaultArt.Color, WardrobeRig.At(shot, 0, -5));      // 鞋(1414)压着裤子(1313)
        Assert.Equal(HairDefaultArt.Color, WardrobeRig.At(shot, 0, -38));
        Assert.Equal(CapDefaultArt.Color, WardrobeRig.At(shot, 0, -44));       // 帽子(2400)压着头发(2222)
        Assert.Equal(BladeStartArt.Color, WardrobeRig.At(shot, 17, -20));
        Assert.Equal(Background, WardrobeRig.At(shot, -11, -40));
        Assert.Equal(SkinColor, WardrobeRig.At(shot, 9, -39));
    }

    [Fact]
    public void NothingWornIsTheBaseItselfAndEveryPartIsEmpty()
    {
        using var rig = new WardrobeRig();
        AvatarWardrobe w = rig.Wardrobe;
        Assert.Same(rig.Base, w.Base);
        Assert.Same(rig.Base, w.Current);
        Assert.Equal(0, w.Version);
        Assert.Equal(0, w.WornCount);
        Assert.Empty(w.WornItems);
        for (int part = -2; part < 12; part++)
        {
            Assert.Null(w.Worn(part));
        }
    }

    // ---------------------------------------------------------------- 替换、脱下、换穿

    [Fact]
    public void WearingACoatReplacesTheDefaultCoatLayersAndNothingElse()
    {
        using var rig = new WardrobeRig();
        RgbaCanvas before = rig.Shot();
        AvatarItem coat = rig.Read(CoatOne);

        Assert.True(rig.Wardrobe.Wear(coat));
        Assert.Same(coat, rig.Wardrobe.Worn(3));
        Assert.NotSame(rig.Base, rig.Wardrobe.Current);
        // 新上衣的两层(coat_b 键 600 在皮肤后面,coat_a 键 1110 在裤子前面)按键排进整体顺序,默认上衣的层没有了
        Assert.Equal("coat_b skin coat_a pants_a shoes_a hair_a cap_a swordb", WardrobeRig.Names(rig.Wardrobe.Current));

        RgbaCanvas after = rig.Shot();
        // 变化只在默认上衣与新上衣各层的矩形里
        var allowed = new List<Box> { CoatDefaultArt.Box, CoatOneFrontArt.Box, CoatOneBackArt.Box };
        List<(int X, int Y)> diff = WardrobeRig.Diff(before, after);
        Assert.NotEmpty(diff);
        Assert.All(diff, p => Assert.True(InAny(allowed, p), "意外的变化像素 " + p));

        Assert.Equal(SkinColor, WardrobeRig.At(after, 0, -20));                       // 默认上衣下半截没了:露出皮肤
        Assert.Equal(Background, WardrobeRig.At(after, -11, -20));                    // 皮肤外面的那部分露出底色
        Assert.Equal(CoatOneFrontArt.Color, WardrobeRig.At(after, 0, -30));
        Assert.Equal(CoatOneFrontArt.Color, WardrobeRig.At(after, -11, -30));         // coat_a(1110)压着 coat_b(600)
        Assert.Equal(CoatOneBackArt.Color, WardrobeRig.At(after, -13, -33));          // coat_b 在皮肤外面的部分看得见
        Assert.Equal(SkinColor, WardrobeRig.At(after, 0, -35));                       // coat_b 与皮肤重叠的部分被皮肤(650)盖住
        Assert.Equal(PantsDefaultArt.Color, WardrobeRig.At(after, 0, -10));           // 裤子没动
        Assert.Equal(BladeStartArt.Color, WardrobeRig.At(after, 17, -20));            // 武器没动
    }

    [Fact]
    public void TakingTheCoatOffRestoresTheBaseLookExactly()
    {
        using var rig = new WardrobeRig();
        RgbaCanvas before = rig.Shot();
        Assert.True(rig.Wardrobe.Wear(rig.Read(CoatOne)));
        Assert.NotEqual(before.Pixels, rig.Shot().Pixels);

        Assert.True(rig.Wardrobe.TakeOff(3));
        Assert.Null(rig.Wardrobe.Worn(3));
        Assert.Same(rig.Base, rig.Wardrobe.Current);                 // 什么也没穿时就是基础外观本身
        AssertSamePixels(before, rig.Shot());
        Assert.False(rig.Wardrobe.TakeOff(3));                       // 已经脱了
        Assert.Equal(2, rig.Wardrobe.Version);                       // 穿一次、脱一次
    }

    [Fact]
    public void TakingOffOnePartKeepsTheOthersAndMatchesWearingOnlyThem()
    {
        using var rig = new WardrobeRig();
        AvatarItem hat = rig.Read(Hat);
        Assert.True(rig.Wardrobe.Wear(rig.Read(CoatOne)));
        Assert.True(rig.Wardrobe.Wear(hat));
        Assert.True(rig.Wardrobe.TakeOff(3));
        Assert.Same(hat, rig.Wardrobe.Worn(0));
        Assert.Null(rig.Wardrobe.Worn(3));

        using var other = new WardrobeRig();
        Assert.True(other.Wardrobe.Wear(other.Read(Hat)));
        Assert.Equal(WardrobeRig.Names(other.Wardrobe.Current), WardrobeRig.Names(rig.Wardrobe.Current));
        AssertSamePixels(other.Shot(), rig.Shot());
    }

    [Fact]
    public void WearingAnotherCoatReplacesTheFirstWithoutLeavingTraces()
    {
        using var rig = new WardrobeRig();
        Assert.True(rig.Wardrobe.Wear(rig.Read(CoatOne)));
        AvatarItem two = rig.Read(CoatTwo);
        Assert.True(rig.Wardrobe.Wear(two));
        Assert.Same(two, rig.Wardrobe.Worn(3));
        Assert.Equal(1, rig.Wardrobe.WornCount);
        Assert.Equal(2, rig.Wardrobe.Version);

        RgbaCanvas shot = rig.Shot();
        Assert.False(WardrobeRig.HasColor(shot, CoatOneFrontArt.Color));
        Assert.False(WardrobeRig.HasColor(shot, CoatOneBackArt.Color));
        Assert.False(WardrobeRig.HasColor(shot, CoatDefaultArt.Color));
        Assert.True(WardrobeRig.HasColor(shot, CoatTwoArt.Color));
        Assert.Equal("skin coat_a pants_a shoes_a hair_a cap_a swordb", WardrobeRig.Names(rig.Wardrobe.Current));

        using var fresh = new WardrobeRig();
        Assert.True(fresh.Wardrobe.Wear(fresh.Read(CoatTwo)));
        AssertSamePixels(fresh.Shot(), shot);
    }

    [Fact]
    public void AReplacingItemKeepsThePlaceOfTheOneItReplacesWhenKeysTie()
    {
        using var rig = new WardrobeRig();
        Assert.True(rig.Wardrobe.Wear(rig.Read(PantsTie)));
        // 换上的裤子(键 1414)与默认鞋(键 1414)同键:裤子留在原来裤子的位置,所以鞋后画、压着它
        Assert.Equal("skin coat_a pants_a shoes_a hair_a cap_a swordb", WardrobeRig.Names(rig.Wardrobe.Current));
        string[] paths = rig.Wardrobe.Current.Layers.Select(l => l.ImagePath).ToArray();
        Assert.True(Array.IndexOf(paths, PantsTieArt.Path) < Array.IndexOf(paths, ShoesDefaultArt.Path));
        RgbaCanvas shot = rig.Shot();
        Assert.Equal(ShoesDefaultArt.Color, WardrobeRig.At(shot, 0, -3));
        Assert.Equal(PantsTieArt.Color, WardrobeRig.At(shot, 0, -9));
    }

    [Fact]
    public void AReplacedPartLeavesExactlyOneLayerOfItsKind()
    {
        using var rig = new WardrobeRig();
        Assert.True(rig.Wardrobe.Wear(rig.Read(Hat)));
        List<AvatarLayer> caps = rig.Wardrobe.Current.Layers.Where(l => l.Name == "cap_a").ToList();
        Assert.Single(caps);
        Assert.Equal(HatArt.Path, caps[0].ImagePath);
        Assert.Equal(2410, caps[0].Key);
        Assert.DoesNotContain(rig.Wardrobe.Current.Layers, l => l.ImagePath == CapDefaultArt.Path);
    }

    // ---------------------------------------------------------------- 每个部位只动自己的区域

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
    public void EachPartTouchesOnlyItsOwnRegionAndTakeOffRestoresIt(int part)
    {
        using var rig = new WardrobeRig();
        (Equ worn, Equ[] baseEqu) = PartCase(part);
        RgbaCanvas before = rig.Shot();

        Assert.True(rig.Wardrobe.Wear(rig.Read(worn)), "部位 " + part);
        Assert.NotNull(rig.Wardrobe.Worn(part));
        for (int other = 0; other < AvatarWardrobe.PartCount; other++)
        {
            if (other != part)
            {
                Assert.Null(rig.Wardrobe.Worn(other));                       // 别的格子没被占
            }
        }

        // 该部位的新旧图层与皮肤(皮肤部位自己)是唯一允许变的区域
        var allowed = new List<Box>(worn.Boxes);
        foreach (Equ b in baseEqu)
        {
            allowed.AddRange(b.Boxes);
        }
        if (part == AvatarWardrobe.SkinPart)
        {
            allowed.Add(Skin0.Box);
            allowed.Add(Skin1.Box);
        }
        RgbaCanvas after = rig.Shot();
        List<(int X, int Y)> diff = WardrobeRig.Diff(before, after);
        Assert.NotEmpty(diff);
        Assert.All(diff, p => Assert.True(InAny(allowed, p), "部位 " + part + " 动到了区域外的像素 " + p));

        Assert.True(rig.Wardrobe.TakeOff(part));
        Assert.Same(rig.Base, rig.Wardrobe.Current);
        AssertSamePixels(before, rig.Shot());
    }

    // ---------------------------------------------------------------- 图层整体按键排序

    [Fact]
    public void LayersOfAllPartsAreSortedByKeyTogetherNotPartByPart()
    {
        using var rig = new WardrobeRig();
        foreach (Equ e in new[] { Hair, Neck, Belt, Face })
        {
            Assert.True(rig.Wardrobe.Wear(rig.Read(e)));
        }
        AvatarOutfit current = rig.Wardrobe.Current;
        // 头发的后层 100 与脖子的后层 300 在皮肤(650)后面,头发前层 2200、脸 2300 夹在腰饰的两层(1500、2300)与帽子(2400)之间;
        // 同键(脸与腰饰第二层 2300、脖子前层与武器 3000)时先画的是脸、再腰饰;脖子在武器前
        Assert.Equal("hair_b neck_a skin coat_a pants_a shoes_a belt_a hair_a face_a belt_b cap_a neck_b swordb", WardrobeRig.Names(current));
        int[] keys = current.Layers.Select(l => l.Key).ToArray();
        Assert.Equal(keys.OrderBy(k => k).ToArray(), keys);

        RgbaCanvas shot = rig.Shot();
        Assert.Equal(HairBackArt.Color, WardrobeRig.At(shot, -11, -40));      // 头发后层在皮肤外面的部分看得见
        Assert.Equal(NeckBackArt.Color, WardrobeRig.At(shot, -13, -33));      // 脖子后层同理(上衣默认层的矩形够不着这里)
        Assert.Equal(BeltArt.Color, WardrobeRig.At(shot, 0, -19));            // 腰饰(1500)压着上衣(1111)
        Assert.Equal(BeltArt.Color, WardrobeRig.At(shot, 0, -18));            // 也压着裤子(1313)
        Assert.Equal(FaceArt.Color, WardrobeRig.At(shot, 0, -39));            // 脸(2300)压着皮肤,也压着头发前层(2200)
        Assert.Equal(BeltExtraArt.Color, WardrobeRig.At(shot, 0, -36));       // 同键时腰饰的第二层后画,盖住脸
        Assert.Equal(HairFrontArt.Color, WardrobeRig.At(shot, -6, -38));      // 头发前层(2200)压着皮肤
    }

    [Fact]
    public void AnExtraPartTiedWithTheWeaponIsDrawnBeforeIt()
    {
        using var rig = new WardrobeRig();
        Assert.True(rig.Wardrobe.Wear(rig.Read(Neck)));
        Assert.Equal("neck_a skin coat_a pants_a shoes_a hair_a cap_a neck_b swordb", WardrobeRig.Names(rig.Wardrobe.Current));
        RgbaCanvas shot = rig.Shot();
        Assert.Equal(NeckFrontArt.Color, WardrobeRig.At(shot, 12, -28));      // 武器左边的部分是脖子前层
        Assert.Equal(NeckFrontArt.Color, WardrobeRig.At(shot, 21, -28));      // 武器右边的部分也是
        Assert.Equal(BladeStartArt.Color, WardrobeRig.At(shot, 15, -28));     // 重叠处武器(同键 3000,放在最后)盖住它
    }

    [Fact]
    public void TheLookDoesNotDependOnTheOrderOfWearing()
    {
        string expectedNames = null;
        RgbaCanvas expectedShot = null;
        var items = new[] { Hair, Face, Neck, Belt };
        foreach (int[] order in Permutations(items.Length))
        {
            using var rig = new WardrobeRig();
            foreach (int i in order)
            {
                Assert.True(rig.Wardrobe.Wear(rig.Read(items[i])));
            }
            string names = WardrobeRig.Names(rig.Wardrobe.Current);
            RgbaCanvas shot = rig.Shot();
            if (expectedNames == null)
            {
                expectedNames = names;
                expectedShot = shot;
                continue;
            }
            Assert.Equal(expectedNames, names);
            AssertSamePixels(expectedShot, shot);
        }
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var current = Enumerable.Range(0, n).ToArray();
        return Permute(current, 0);
    }

    private static IEnumerable<int[]> Permute(int[] a, int at)
    {
        if (at == a.Length)
        {
            yield return (int[])a.Clone();
            yield break;
        }
        for (int i = at; i < a.Length; i++)
        {
            (a[at], a[i]) = (a[i], a[at]);
            foreach (int[] p in Permute(a, at + 1))
            {
                yield return p;
            }
            (a[at], a[i]) = (a[i], a[at]);
        }
    }

    // ---------------------------------------------------------------- 皮肤

    [Fact]
    public void ASkinAvatarSwapsTheSkinImageAndTakingItOffBringsTheDefaultBack()
    {
        using var rig = new WardrobeRig();
        RgbaCanvas before = rig.Shot();
        Assert.Equal(5, rig.Base.SkinNumber);
        Assert.True(rig.Wardrobe.Wear(rig.Read(SkinWorn)));

        AvatarOutfit current = rig.Wardrobe.Current;
        Assert.Equal(102, current.SkinNumber);                                 // 变体 (1, 2) 是 1×100+2,不是只看第二个数
        Assert.Equal(rig.Class.BodyImage(102), current.Layers.First(l => l.Name == "skin").ImagePath);
        Assert.Equal(rig.Base.Items.Count, current.Items.Count);               // 皮肤不是图层装备
        Assert.Equal(5, rig.Base.SkinNumber);                                  // 基础外观不被改动
        RgbaCanvas after = rig.Shot();
        Assert.Equal(Skin1.Color, WardrobeRig.At(after, -9, -39));
        Assert.Equal(CoatDefaultArt.Color, WardrobeRig.At(after, 0, -20));     // 上衣还在

        Assert.True(rig.Wardrobe.TakeOff(AvatarWardrobe.SkinPart));
        Assert.Same(rig.Base, rig.Wardrobe.Current);
        AssertSamePixels(before, rig.Shot());
    }

    // ---------------------------------------------------------------- 穿不出来的

    [Fact]
    public void EveryWearableItemCanBeShown()
    {
        using var rig = new WardrobeRig();
        foreach (Equ e in new[] { Hat, Hair, Face, CoatOne, CoatTwo, Pants, Shoes, Neck, Belt, SkinWorn })
        {
            Assert.True(rig.Wardrobe.CanShow(rig.Read(e)), e.Path);
        }
    }

    [Fact]
    public void ItemsThatShowNothingAreRefusedAndChangeNothing()
    {
        using var rig = new WardrobeRig();
        RgbaCanvas before = rig.Shot();
        foreach (Equ e in new[] { HatOtherJob, Aurora, CoatNoImage, CoatHalfImage, BeltNoLayer, SkinNoVariation, SkinNoImage, WeaponStart })
        {
            AvatarItem item = rig.Read(e);
            Assert.NotNull(item);
            Assert.False(rig.Wardrobe.CanShow(item), e.Path);
            Assert.False(rig.Wardrobe.Wear(item), e.Path);
        }
        Assert.False(rig.Wardrobe.CanShow(null));
        Assert.False(rig.Wardrobe.Wear(null));
        Assert.Equal(0, rig.Wardrobe.Version);
        Assert.Equal(0, rig.Wardrobe.WornCount);
        Assert.Same(rig.Base, rig.Wardrobe.Current);
        AssertSamePixels(before, rig.Shot());
    }

    [Fact]
    public void AnItemThatCannotBeShownDoesNotDisturbTheOneAlreadyWorn()
    {
        using var rig = new WardrobeRig();
        AvatarItem coat = rig.Read(CoatOne);
        Assert.True(rig.Wardrobe.Wear(coat));
        int version = rig.Wardrobe.Version;
        AvatarOutfit current = rig.Wardrobe.Current;
        Assert.False(rig.Wardrobe.Wear(rig.Read(CoatNoImage)));               // 同部位的缺图件被拒,原来的还穿着
        Assert.Same(coat, rig.Wardrobe.Worn(3));
        Assert.Equal(version, rig.Wardrobe.Version);
        Assert.Same(current, rig.Wardrobe.Current);
    }

    // ---------------------------------------------------------------- 部位表

    [Fact]
    public void TheTenPartsFollowTheServerTagOrder()
    {
        string[] expected =
        {
            "[hat avatar]", "[hair avatar]", "[face avatar]", "[coat avatar]", "[pants avatar]",
            "[shoes avatar]", "[breast avatar]", "[waist avatar]", "[skin avatar]", "[aurora avatar]",
        };
        Assert.Equal(10, AvatarWardrobe.PartCount);
        Assert.Equal(8, AvatarWardrobe.SkinPart);
        for (int part = 0; part < expected.Length; part++)
        {
            Assert.Equal(expected[part], AvatarWardrobe.TagOf(part));
            Assert.Equal(part, AvatarWardrobe.PartOf(expected[part]));
            Assert.Equal(part, AvatarWardrobe.PartOf(expected[part].ToUpperInvariant()));
        }
        Assert.Null(AvatarWardrobe.TagOf(-1));
        Assert.Null(AvatarWardrobe.TagOf(10));
        Assert.Equal(-1, AvatarWardrobe.PartOf("[weapon]"));
        Assert.Equal(-1, AvatarWardrobe.PartOf("[coat]"));
        Assert.Equal(-1, AvatarWardrobe.PartOf(string.Empty));
        Assert.Equal(-1, AvatarWardrobe.PartOf((string)null));
        Assert.Equal(-1, AvatarWardrobe.PartOf((AvatarItem)null));
    }

    [Fact]
    public void ItemsLandInThePartTheirEquipmentTypeNames()
    {
        using var rig = new WardrobeRig();
        for (int part = 0; part < 9; part++)
        {
            (Equ worn, _) = PartCase(part);
            Assert.Equal(part, AvatarWardrobe.PartOf(rig.Read(worn)));
            Assert.True(rig.Wardrobe.Wear(rig.Read(worn)));
            Assert.Equal(worn.FullPath, rig.Wardrobe.Worn(part).EquPath);
        }
        Assert.Equal(9, rig.Wardrobe.WornCount);
        Assert.Null(rig.Wardrobe.Worn(9));
        Assert.Equal(-1, AvatarWardrobe.PartOf(rig.Read(WeaponStart)));
        Assert.Equal(9, AvatarWardrobe.PartOf(rig.Read(Aurora)));                // 灵气认得出是第 9 部位,只是画不出来
    }

    // ---------------------------------------------------------------- 武器与别的外观成分

    [Fact]
    public void ChangingTheWeaponKeepsTheWornAvatarsAndTheirOrder()
    {
        using var rig = new WardrobeRig();
        AvatarItem coat = rig.Read(CoatOne);
        AvatarItem neck = rig.Read(Neck);
        Assert.True(rig.Wardrobe.Wear(coat));
        Assert.True(rig.Wardrobe.Wear(neck));
        RgbaCanvas before = rig.Shot();

        AvatarOutfit result = rig.Wardrobe.SetWeapon(rig.Read(WeaponOther));
        Assert.Same(result, rig.Wardrobe.Current);
        Assert.Same(coat, rig.Wardrobe.Worn(3));
        Assert.Same(neck, rig.Wardrobe.Worn(6));
        Assert.Equal("neck_a coat_b skin coat_a pants_a shoes_a hair_a cap_a neck_b swordb", WardrobeRig.Names(result));

        RgbaCanvas after = rig.Shot();
        // 变的只有两把武器的矩形(以及脖子前层与新武器的重叠处)
        var allowed = new List<Box> { BladeStartArt.Box, BladeOtherArt.Box };
        Assert.All(WardrobeRig.Diff(before, after), p => Assert.True(InAny(allowed, p), "意外的变化像素 " + p));
        Assert.Equal(BladeOtherArt.Color, WardrobeRig.At(after, 17, -20));
        Assert.Equal(Background, WardrobeRig.At(after, 14, -20));              // 旧武器没了

        // 脱掉装扮后是"新武器的基础外观"本身
        Assert.True(rig.Wardrobe.Clear());
        Assert.Same(rig.Wardrobe.Base, rig.Wardrobe.Current);
        Assert.Equal("skin coat_a pants_a shoes_a hair_a cap_a swordb", WardrobeRig.Names(rig.Wardrobe.Current));
        Assert.Equal(BladeOtherArt.Color, WardrobeRig.At(rig.Shot(), 17, -20));
    }

    [Fact]
    public void TakingTheWeaponOffLeavesTheAvatarsAlone()
    {
        using var rig = new WardrobeRig();
        Assert.True(rig.Wardrobe.Wear(rig.Read(Hat)));
        rig.Wardrobe.SetWeapon(null);
        Assert.Equal("skin coat_a pants_a shoes_a hair_a cap_a", WardrobeRig.Names(rig.Wardrobe.Current));
        Assert.False(WardrobeRig.HasColor(rig.Shot(), BladeStartArt.Color));
        Assert.NotNull(rig.Wardrobe.Worn(0));
        Assert.Empty(rig.Wardrobe.Current.WeaponAnimationPaths("attack1.ani"));
    }

    [Fact]
    public void AvatarsDoNotChangeTheWeaponAnimationLayers()
    {
        using var rig = new WardrobeRig();
        List<string> before = rig.Wardrobe.Current.WeaponAnimationPaths("attack1.ani");
        Assert.NotEmpty(before);
        foreach (Equ e in new[] { Hat, Hair, Face, CoatOne, Pants, Shoes, Neck, Belt, SkinWorn })
        {
            Assert.True(rig.Wardrobe.Wear(rig.Read(e)));
        }
        Assert.Equal(before, rig.Wardrobe.Current.WeaponAnimationPaths("attack1.ani"));
        Assert.Single(rig.Wardrobe.Current.Items, i => i.EquipmentType == "[weapon]");
    }

    [Fact]
    public void RebasingKeepsTheWornAvatarsOnTheNewBase()
    {
        using var rig = new WardrobeRig();
        Assert.True(rig.Wardrobe.Wear(rig.Read(Hat)));
        AvatarOutfit newBase = rig.Base.WithWeapon(rig.Read(WeaponOther));
        AvatarOutfit result = rig.Wardrobe.Rebase(newBase);
        Assert.Same(newBase, rig.Wardrobe.Base);
        Assert.Same(result, rig.Wardrobe.Current);
        Assert.NotNull(rig.Wardrobe.Worn(0));
        Assert.Equal(BladeOtherArt.Color, WardrobeRig.At(rig.Shot(), 17, -20));
        Assert.Throws<ArgumentNullException>(() => rig.Wardrobe.Rebase(null));
    }

    // ---------------------------------------------------------------- 版本、清空、遍历

    [Fact]
    public void VersionCountsRealChangesOnly()
    {
        using var rig = new WardrobeRig();
        AvatarWardrobe w = rig.Wardrobe;
        AvatarItem hat = rig.Read(Hat);
        Assert.True(w.Wear(hat));
        Assert.Equal(1, w.Version);
        Assert.True(w.Wear(hat));                       // 同一个对象再穿:成功但没变
        Assert.Equal(1, w.Version);
        Assert.False(w.Wear(rig.Read(Aurora)));         // 穿不出来:没变
        Assert.Equal(1, w.Version);
        Assert.True(w.Wear(rig.Read(CoatOne)));
        Assert.Equal(2, w.Version);
        Assert.True(w.TakeOff(0));
        Assert.Equal(3, w.Version);
        Assert.False(w.TakeOff(0));                     // 本来就空
        Assert.False(w.TakeOff(-1));
        Assert.False(w.TakeOff(10));
        Assert.Equal(3, w.Version);
        Assert.True(w.Clear());
        Assert.Equal(4, w.Version);
        Assert.False(w.Clear());
        Assert.Equal(4, w.Version);
        w.Rebase(w.Base);                               // 同一个底座:没变
        Assert.Equal(4, w.Version);
        w.SetWeapon(rig.Read(WeaponOther));
        Assert.Equal(5, w.Version);
    }

    [Fact]
    public void ClearTakesEverythingOffAtOnce()
    {
        using var rig = new WardrobeRig();
        RgbaCanvas before = rig.Shot();
        foreach (Equ e in new[] { Hat, Hair, Face, CoatOne, Pants, Shoes, Neck, Belt, SkinWorn })
        {
            Assert.True(rig.Wardrobe.Wear(rig.Read(e)));
        }
        Assert.Equal(9, rig.Wardrobe.WornCount);
        Assert.True(rig.Wardrobe.Clear());
        Assert.Equal(0, rig.Wardrobe.WornCount);
        Assert.Same(rig.Base, rig.Wardrobe.Current);
        AssertSamePixels(before, rig.Shot());
    }

    [Fact]
    public void WornItemsComeOutByPartNumberWhateverTheWearingOrder()
    {
        using var rig = new WardrobeRig();
        foreach (Equ e in new[] { Belt, Hat, CoatOne, Face })
        {
            Assert.True(rig.Wardrobe.Wear(rig.Read(e)));
        }
        Assert.Equal(new[] { Hat.FullPath, Face.FullPath, CoatOne.FullPath, Belt.FullPath }, rig.Wardrobe.WornItems.Select(i => i.EquPath).ToArray());
    }

    [Fact]
    public void ConstructorRejectsNulls()
    {
        using var rig = new WardrobeRig();
        Assert.Throws<ArgumentNullException>(() => new AvatarWardrobe(null, rig.Base));
        Assert.Throws<ArgumentNullException>(() => new AvatarWardrobe(rig.Assets, null));
    }
}
