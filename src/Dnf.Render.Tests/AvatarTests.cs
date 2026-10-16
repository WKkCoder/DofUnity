using Dnf.Data;

namespace Dnf.Render.Tests;

public class AvatarTests
{
    private const string Skin = AvatarFixture.AvatarImageRoot + "/skin/tt_body0000.img";
    private const string CoatA = AvatarFixture.AvatarImageRoot + "/coat/tt_coat0105a.img";
    private const string CoatB = AvatarFixture.AvatarImageRoot + "/coat/tt_coat0105b.img";
    private const string HairA = AvatarFixture.AvatarImageRoot + "/hair/tt_hair0000a.img";
    private const string SwordB = AvatarFixture.WeaponImageRoot + "/blade/sword0002b.img";
    private const string SwordC = AvatarFixture.WeaponImageRoot + "/blade/sword0002c.img";

    private static AvatarClass Class(DnfAssets assets)
    {
        return AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
    }

    [Fact]
    public void ClassLayoutComesFromTheBodyImagePath()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = Class(assets);
        Assert.Equal("sprite/character/test/equipment/avatar", cls.AvatarRoot);
        Assert.Equal("sprite/character/test/equipment/weapon", cls.WeaponRoot);
        Assert.Equal("tt_", cls.FilePrefix);
        Assert.Equal("sprite/character/test/equipment/avatar/skin/tt_body0007.img", cls.BodyImage(7));
        Assert.Throws<DnfFormatException>(() => AvatarClass.FromChr(assets, "character/test/missing.chr", AvatarFixture.JobTag));
    }

    [Fact]
    public void PartsNumberTheirImageAsAHundredPlusBAndReadOnlyTheirOwnJobBlock()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = Class(assets);

        // 本职业的块排在别的职业之后:变体 1 5 → 0105,不是别的职业的 9 9
        AvatarItem coat = AvatarItem.Read(assets, cls, AvatarFixture.EquipRoot + "/avatar/coat/acoat_default.equ");
        Assert.NotNull(coat);
        Assert.Equal("[coat avatar]", coat.EquipmentType);
        Assert.True(coat.HasVariation);
        Assert.Equal((1, 5), (coat.VariationA, coat.VariationB));
        Assert.Equal(new[] { ("coat_a", 1111, CoatA), ("coat_b", 700, CoatB) },
            coat.Layers.Select(l => (l.Name, l.Key, l.ImagePath)).ToArray());

        // 本职业的块排在前面:读到下一个 [animation job] 就停,别的职业的 7 7 与 hair_q 不能混进来
        AvatarItem hair = AvatarItem.Read(assets, cls, AvatarFixture.EquipRoot + "/avatar/hair/ahair_default.equ");
        Assert.Equal((0, 0), (hair.VariationA, hair.VariationB));
        Assert.Equal(new[] { ("hair_a", 2222, HairA) }, hair.Layers.Select(l => (l.Name, l.Key, l.ImagePath)).ToArray());
    }

    [Fact]
    public void AnItemWithoutABlockForThisJobHasNoLayersAndAMissingScriptIsNull()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = Class(assets);
        AvatarItem cap = AvatarItem.Read(assets, cls, AvatarFixture.EquipRoot + "/avatar/cap/ahat_other.equ");
        Assert.NotNull(cap);
        Assert.False(cap.HasVariation);
        Assert.Empty(cap.Layers);
        Assert.Null(AvatarItem.Read(assets, cls, AvatarFixture.EquipRoot + "/avatar/cap/nope.equ"));
    }

    [Fact]
    public void SkinItemsReportTheirImageNumber()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarItem skin = AvatarItem.Read(assets, Class(assets), AvatarFixture.EquipRoot + "/avatar/skin/askin_default.equ");
        Assert.True(skin.IsSkin);
        Assert.Equal(0, skin.AvatarNumber);
        Assert.Empty(skin.Layers);
    }

    [Fact]
    public void WeaponsUseOnlyBAndFindTheirImageByFileNameUnderTheWeaponRoot()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarItem sword = AvatarItem.Read(assets, Class(assets), AvatarFixture.EquipRoot + "/" + AvatarFixture.WeaponEqu);
        Assert.Equal((3, 2), (sword.VariationA, sword.VariationB));
        // 目录 sword/ 与 blade/ 不同,按文件名找;A = 3 不进文件名;swordd 的图片不存在 → 路径为 null
        Assert.Equal(new (string, int, string)[] { ("swordb", 650, SwordB), ("swordc", 2999, SwordC), ("swordd", 3000, null) },
            sword.Layers.Select(l => (l.Name, l.Key, l.ImagePath)).ToArray());
    }

    [Fact]
    public void ATrailingDigitInAWeaponLayerNameStaysAfterTheLetter()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarItem digit = AvatarItem.Read(assets, Class(assets), AvatarFixture.EquipRoot + "/weapon/sword/sw_digit.equ");
        Assert.Equal(AvatarFixture.WeaponImageRoot + "/blade/sword0002b1.img", digit.Layers.Single().ImagePath);
    }

    [Fact]
    public void OutfitSortsByKeyAscendingAndTheSkinGoesBeforeALayerWithTheSameKey()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, Class(assets), AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);

        // 皮肤 650(SkinKey);发型 2222;上衣 1111、700;武器 650、2999、3000(最后一层没有图片,被丢掉)
        Assert.Equal(new[] { ("skin", 650, Skin), ("swordb", 650, SwordB), ("coat_b", 700, CoatB), ("coat_a", 1111, CoatA), ("hair_a", 2222, HairA), ("swordc", 2999, SwordC) },
            outfit.Layers.Select(l => (l.Name, l.Key, l.ImagePath)).ToArray());
        Assert.Equal(AvatarOutfit.SkinKey, outfit.Layers[0].Key);
    }

    [Fact]
    public void DefaultOutfitSkipsMissingItemsAndWorksWithoutAWeapon()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, Class(assets), AvatarFixture.EquipRoot, null);
        // 夹具里没有 pants、shoes、cap 的 default 脚本:只剩 皮肤、上衣、发型
        Assert.Equal(new[] { "skin", "coat_b", "coat_a", "hair_a" }, outfit.Layers.Select(l => l.Name).ToArray());

        AvatarOutfit explicitPath = AvatarOutfit.CreateDefault(assets, Class(assets), AvatarFixture.EquipRoot,
            AvatarFixture.EquipRoot + "/" + AvatarFixture.WeaponEqu);
        Assert.Contains(explicitPath.Layers, l => l.Name == "swordc");
    }

    [Fact]
    public void EveryLayerUsesTheAniFramesImageFrameAndTheModelPosition()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, Class(assets), AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        AniFile rest = assets.LoadAni(AvatarFixture.RestAni);
        var projection = new TownProjection(0, 200);

        // 帧 1 的 IMG 帧号是 1,每层的红色通道 = 基色 + 1;coat_b 的帧 1 是透明占位帧,不出精灵
        List<SceneSprite> sprites = outfit.SpritesAt(assets, rest, AvatarFixture.RestAni, 1, projection, 100, 50, 0, false, 50, -2);
        Assert.Equal(5, sprites.Count);
        Assert.Equal(new[] { 201, 61, 11, 201, 251 }, sprites.Select(s => (int)s.Frame.Rgba[0]).ToArray()); // skin, swordb, coat_a, hair_a, swordc
        Assert.All(sprites, s =>
        {
            Assert.Equal(-2, s.NpcId);
            Assert.Equal(50, s.SortKey);
            Assert.Equal(1, s.AniFrame);
            Assert.False(s.Mirrored);
        });
        // 位置 = 世界 - 镜头 + .ani 偏移 + IMG 头偏移:skin x = 100 + (-5) + (-3) = 92,y = 50 + 200 + (-8) + (-4) = 238
        Assert.Equal((92, 238), (sprites[0].ScreenX, sprites[0].ScreenY));
        // swordb 偏移 (-10,-12)
        Assert.Equal((100 - 5 - 10, 50 + 200 - 8 - 12), (sprites[1].ScreenX, sprites[1].ScreenY));
        // hair 偏移 (0,0)
        Assert.Equal((95, 242), (sprites[3].ScreenX, sprites[3].ScreenY));
    }

    [Fact]
    public void FrameZeroDrawsCoatBWithItsOwnHeaderOffset()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, Class(assets), AvatarFixture.EquipRoot, null);
        AniFile rest = assets.LoadAni(AvatarFixture.RestAni);
        List<SceneSprite> sprites = outfit.SpritesAt(assets, rest, AvatarFixture.RestAni, 0, new TownProjection(0, 200), 100, 50, 0, false, 50, -2);
        Assert.Equal(new[] { "skin", "coat_b", "coat_a", "hair_a" }, outfit.Layers.Select(l => l.Name).ToArray());
        Assert.Equal(4, sprites.Count);
        // coat_b(第二个)偏移 (1,2):x = 100 - 5 + 1 = 96,y = 50 + 200 - 8 + 2 = 244
        Assert.Equal((96, 244), (sprites[1].ScreenX, sprites[1].ScreenY));
    }

    [Fact]
    public void FacingLeftMirrorsEveryLayerAboutTheWorldX()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, Class(assets), AvatarFixture.EquipRoot, null);
        AniFile rest = assets.LoadAni(AvatarFixture.RestAni);
        var projection = new TownProjection(10, 200);
        List<SceneSprite> sprites = outfit.SpritesAt(assets, rest, AvatarFixture.RestAni, 0, projection, 100, 50, 0, true, 50, -2);
        Assert.All(sprites, s => Assert.True(s.Mirrored));
        // 翻转后的左边缘 = 世界 x - 镜头 - (.ani x + IMG x + 宽):skin = 100 - 10 - (-5 - 3 + 2) = 96;hair(IMG x = 0)= 90 - (-5 + 0 + 2) = 93
        Assert.Equal(96, sprites[0].ScreenX);
        Assert.Equal(93, sprites[3].ScreenX);
    }

    [Fact]
    public void AMissingAniOrAFrameOutOfRangeGivesNoSprites()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, Class(assets), AvatarFixture.EquipRoot, null);
        AniFile rest = assets.LoadAni(AvatarFixture.RestAni);
        var projection = new TownProjection(0, 200);
        Assert.Empty(outfit.SpritesAt(assets, null, "x", 0, projection, 0, 0, 0, false, 0, -2));
        Assert.Empty(outfit.SpritesAt(assets, rest, "x", 7, projection, 0, 0, 0, false, 0, -2));
        Assert.Empty(outfit.SpritesAt(assets, rest, "x", -1, projection, 0, 0, 0, false, 0, -2));
    }

    [Fact]
    public void SpriteFrameKnowsWhenAllPixelsAreTransparent()
    {
        Assert.True(new SpriteFrame(1, 1, 300, -200, 1, 1, new byte[] { 9, 9, 9, 0 }).IsBlank);
        Assert.False(new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[] { 0, 0, 0, 1 }).IsBlank);
        Assert.True(new SpriteFrame(0, 0, 0, 0, 0, 0, System.Array.Empty<byte>()).IsBlank);
    }

    [Fact]
    public void NamesWithPrefixIsCaseInsensitive()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        List<string> names = assets.Images.NamesWithPrefix("SPRITE/Character/Test/Equipment/Weapon/");
        Assert.Equal(3, names.Count);
        Assert.Empty(assets.Images.NamesWithPrefix(""));
        Assert.Empty(assets.Images.NamesWithPrefix("sprite/nothing/"));
    }
}

public class MapAreaTests
{
    [Fact]
    public void ReadsVirtualMovableAreaRectanglesAndTheirEdgesAreInside()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        List<WorldRect> area = MapScript.ReadVirtualMovableArea(assets, AvatarFixture.AreaMap);
        Assert.Equal(2, area.Count);
        Assert.Equal((10, 20, 100, 50), (area[0].X, area[0].Y, area[0].Width, area[0].Height));
        Assert.Equal((100, 60, 30, 40), (area[1].X, area[1].Y, area[1].Width, area[1].Height));
        Assert.True(area[0].Contains(10, 20));
        Assert.True(area[0].Contains(110, 70));
        Assert.False(area[0].Contains(9.9, 20));
        Assert.False(area[0].Contains(110.1, 70));
        Assert.Throws<DnfFormatException>(() => MapScript.ReadVirtualMovableArea(assets, "map/test/nope.map"));
    }

    [Fact]
    public void AMapWithoutTheTagGivesAnEmptyList()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Empty(MapScript.ReadVirtualMovableArea(assets, "map/test/town.map"));
    }
}

public class TownCameraTests
{
    [Fact]
    public void StartsWithTheHeroCenteredAndClamped()
    {
        Assert.Equal(96, new TownCamera(0, 96, 615).X);     // 215 夹到 96
        Assert.Equal(0, new TownCamera(0, 96, 300).X);      // -100 夹到 0
        Assert.Equal(50, new TownCamera(0, 96, 450).X);     // 居中,没夹
    }

    [Fact]
    public void InsideTheDeadZoneTheCameraStaysPut()
    {
        var cam = new TownCamera(0, 96, 615);
        // 镜头 96:屏幕 x = 世界 x - 96。世界 x 在 446 到 546 之间时屏幕 x 在 350 到 450 之间
        cam.Follow(500);
        cam.Follow(480);
        cam.Follow(447);
        Assert.Equal(96, cam.X);
    }

    [Fact]
    public void LeavingTheLeftEdgeDragsTheCameraSoTheHeroSitsAt350()
    {
        var cam = new TownCamera(0, 96, 615);
        cam.Follow(440);                       // 屏幕 344 < 350 → 镜头 = 440 - 350 = 90
        Assert.Equal(90, cam.X);
        cam.Follow(423);                       // 73(实测:行走连拍里镜头依次是 94、73、50、28、7、0)
        Assert.Equal(73, cam.X);
        cam.Follow(346);
        Assert.Equal(0, cam.X);                // -4 夹到 0
        cam.Follow(330);
        Assert.Equal(0, cam.X);
    }

    [Fact]
    public void LeavingTheRightEdgeDragsTheCameraSoTheHeroSitsAt450()
    {
        var cam = new TownCamera(0, 96, 300);  // 镜头 0
        cam.Follow(474);                       // 屏幕 474 > 450 → 镜头 24(实测:进城后第一张截图,主角屏幕 450、镜头 24)
        Assert.Equal(24, cam.X);
        cam.Follow(574);
        Assert.Equal(96, cam.X);               // 124 夹到 96
        cam.Follow(700);
        Assert.Equal(96, cam.X);
    }

    [Fact]
    public void TheCameraRemembersItsPositionAndTheIntegerPartIsTheFloor()
    {
        var cam = new TownCamera(0, 96, 300);
        cam.Follow(450.6);                     // 屏幕 450.6 > 450 → 镜头 0.6
        Assert.Equal(0.6, cam.X, 6);
        Assert.Equal(0, cam.IntX);
        cam.Follow(500.2);                     // 镜头 50.2
        Assert.Equal(50, cam.IntX);
        cam.Place(-5);
        Assert.Equal(0, cam.X);
        cam.Place(300);
        Assert.Equal(96, cam.X);
    }
}

public class TownHeroTests
{
    private static readonly WorldRect[] Wide = { new WorldRect(0, 0, 2000, 1000) };

    private static TownHero Hero(DnfAssets assets, IReadOnlyList<WorldRect> area, double x, double y, bool left = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ChrPath, outfit, area, x, y, left);
    }

    [Fact]
    public void CreatePicksTheRestAndMoveAnimationsFromTheCharacterScript()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 100, 100);
        Assert.Equal(AvatarFixture.RestAni, hero.CurrentAnimationPath);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        hero.Update(10, 1, 0);
        Assert.Equal(AvatarFixture.MoveAni, hero.CurrentAnimationPath);
    }

    [Fact]
    public void WalksAtTheMeasuredSpeedsAndFacesTheDirectionOfHorizontalInput()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 500, 500);
        hero.Update(1000, 1, 0);
        Assert.Equal(500 + TownHero.DefaultWalkSpeedX, hero.X, 6);
        Assert.Equal(500, hero.Y, 6);
        Assert.False(hero.FacingLeft);

        hero.Update(1000, -1, 0);
        Assert.Equal(500, hero.X, 6);
        Assert.True(hero.FacingLeft);

        hero.Update(1000, 0, 1);
        Assert.Equal(500 + TownHero.DefaultWalkSpeedY, hero.Y, 6);
        Assert.True(hero.FacingLeft);          // 只按上下:朝向不变

        hero.Update(500, 0, -1);
        Assert.Equal(500 + TownHero.DefaultWalkSpeedY - TownHero.DefaultWalkSpeedY / 2, hero.Y, 6);
    }

    [Fact]
    public void DiagonalMovementAppliesEachAxisAtItsOwnSpeed()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 500, 500);
        hero.Update(1000, -1, 1);
        Assert.Equal(500 - TownHero.DefaultWalkSpeedX, hero.X, 6);
        Assert.Equal(500 + TownHero.DefaultWalkSpeedY, hero.Y, 6);
    }

    [Fact]
    public void AnyKeyMeansMoveNoKeyMeansRestAndTheMotionRestartsOnEveryChange()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 500, 500);
        hero.Update(250, 0, 0);
        Assert.Equal((HeroMotion.Rest, 250L), (hero.Motion, hero.MotionMs));
        hero.Update(30, 1, 0);
        Assert.Equal((HeroMotion.Move, 30L), (hero.Motion, hero.MotionMs));
        hero.Update(30, 1, 1);                 // 仍在行走:不重新开始
        Assert.Equal(60L, hero.MotionMs);
        hero.Update(30, -1, 1);                // 换方向也不重新开始
        Assert.Equal(90L, hero.MotionMs);
        hero.Update(20, 0, 0);
        Assert.Equal((HeroMotion.Rest, 20L), (hero.Motion, hero.MotionMs));
    }

    [Fact]
    public void RestFramesFollowTheAniDelaysAtNormalSpeed()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 500, 500);
        // rest.ani:帧 0 100 ms,帧 1 300 ms,循环 400 ms
        int At(long ms)
        {
            hero.Place(500, 500, false, HeroMotion.Rest, ms);
            return hero.CurrentFrame;
        }
        Assert.Equal(new[] { 0, 0, 1, 1, 0 }, new[] { At(0), At(99), At(100), At(399), At(400) });
    }

    [Fact]
    public void WalkFramesPlayAtTheMeasuredRateOfOnePointTwo()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 500, 500);
        Assert.Equal(1.2, hero.MoveAnimationRate, 6);
        // move.ani:三帧各 100 ms;倍率 1.2 时每帧约 83.3 ms
        int At(long ms)
        {
            hero.Place(500, 500, false, HeroMotion.Move, ms);
            return hero.CurrentFrame;
        }
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 0 }, new[] { At(0), At(83), At(84), At(166), At(167), At(249), At(250) });
        hero.MoveAnimationRate = 1.0;
        Assert.Equal(new[] { 0, 0, 1 }, new[] { At(0), At(99), At(100) });
    }

    [Fact]
    public void StopsAtTheEdgeOfTheMovableAreaButKeepsPlayingTheWalkMotion()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var area = new[] { new WorldRect(10, 20, 100, 50) };
        TownHero hero = Hero(assets, area, 40, 40);
        hero.Update(1000, -1, 0);
        Assert.Equal(10, hero.X, 6);           // 到边界(闭区间)就停
        Assert.Equal(HeroMotion.Move, hero.Motion);
        Assert.True(hero.FacingLeft);
        hero.Update(1000, -1, 0);
        Assert.Equal(10, hero.X, 6);
        Assert.Equal(2000L, hero.MotionMs);    // 撞墙后动作照常计时

        hero.Update(1000, 1, 0);
        Assert.Equal(110, hero.X, 6);          // 右边界也包含
    }

    [Fact]
    public void BlockedOnOneAxisTheHeroSlidesAlongTheWall()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var area = new[] { new WorldRect(10, 20, 100, 50) };
        TownHero hero = Hero(assets, area, 10, 30);
        hero.Update(100, -1, 1);               // 向左被挡住,向下照走 9 像素
        Assert.Equal(10, hero.X, 6);
        Assert.Equal(39, hero.Y, 6);
        hero.Update(1000, 0, 1);
        Assert.Equal(70, hero.Y, 6);           // 下边界 20 + 50
    }

    [Fact]
    public void TheAreaIsTheUnionOfItsRectangles()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        List<WorldRect> area = MapScript.ReadVirtualMovableArea(assets, AvatarFixture.AreaMap);
        // L 形:(10,20,100,50) ∪ (100,60,30,40)。从 (105, 65) 向右能走到 130,向下能走到 100
        TownHero hero = Hero(assets, area, 105, 65);
        hero.Update(2000, 1, 0);
        Assert.Equal(130, hero.X, 6);
        hero.Update(2000, 0, 1);
        Assert.Equal(100, hero.Y, 6);
        // 从第一个矩形的左上角向右下:x 在第一个矩形里,y 越过 70 后只在第二个矩形里,x 必须 ≥ 100
        TownHero other = Hero(assets, area, 20, 65);
        other.Update(2000, 0, 1);
        Assert.Equal(70, other.Y, 6);          // x = 20 不在第二个矩形里,y 最多到 70
    }

    [Fact]
    public void AnEmptyAreaMeansNoLimit()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, new List<WorldRect>(), 0, 0);
        hero.Update(1000, -1, -1);
        Assert.Equal(-TownHero.DefaultWalkSpeedX, hero.X, 6);
        Assert.Equal(-TownHero.DefaultWalkSpeedY, hero.Y, 6);
    }

    [Fact]
    public void SpritesCarryTheHeroIdTheFloorOfTheWorldYAndTheCurrentFrame()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 100.9, 50.7);
        hero.Place(100.9, 50.7, false, HeroMotion.Rest, 150);   // rest.ani 的帧 1
        List<SceneSprite> sprites = hero.Sprites(assets, new TownProjection(0, 200));
        Assert.NotEmpty(sprites);
        Assert.All(sprites, s =>
        {
            Assert.Equal(TownHero.ObjectId, s.NpcId);
            Assert.Equal(50, s.SortKey);
            Assert.Equal(1, s.AniFrame);
            Assert.Equal(AvatarFixture.RestAni, s.AnimationPath);
        });
        Assert.Equal(92, sprites[0].ScreenX);   // 世界 x 取整数部分 100:100 - 5 - 3
    }

    [Fact]
    public void FacingLeftMirrorsTheSprites()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = Hero(assets, Wide, 100, 50, left: true);
        Assert.All(hero.Sprites(assets, new TownProjection(0, 200)), s => Assert.True(s.Mirrored));
        hero.Update(10, 1, 0);
        Assert.All(hero.Sprites(assets, new TownProjection(0, 200)), s => Assert.False(s.Mirrored));
    }

    [Fact]
    public void ASceneWithTheHeroDrawsHimBetweenNpcsByWorldY()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = TownProjection.FromE024;
        List<TownScene.ResolvedNpc> npcs = TownScene.Resolve(assets, TownScene.ReadMapNpcs(assets, "map/test/town.map"));
        var layers = new List<TownScene.ResolvedLayer>();
        var frame = new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[] { 1, 2, 3, 255 });
        SceneSprite Extra(int sortKey, int id) => new SceneSprite(frame, 5, 5, sortKey, id, "hero", 0);

        // NPC 在世界 y = 163
        List<SceneSprite> behind = TownScene.BuildResolvedScene(assets, layers, npcs, projection, 0, new[] { Extra(100, -2) });
        Assert.Equal(new[] { -2, 32 }, behind.Select(s => s.NpcId).ToArray());
        List<SceneSprite> front = TownScene.BuildResolvedScene(assets, layers, npcs, projection, 0, new[] { Extra(200, -2) });
        Assert.Equal(new[] { 32, -2 }, front.Select(s => s.NpcId).ToArray());
        // 同 y:主角在 NPC 后面放进列表,所以画在前面(上面)
        List<SceneSprite> tie = TownScene.BuildResolvedScene(assets, layers, npcs, projection, 0, new[] { Extra(163, -2) });
        Assert.Equal(new[] { 32, -2 }, tie.Select(s => s.NpcId).ToArray());
        // 不带额外精灵的老重载结果不变
        Assert.Equal(new[] { 32 }, TownScene.BuildResolvedScene(assets, layers, npcs, projection, 0).Select(s => s.NpcId).ToArray());
    }

    [Fact]
    public void ASessionUpdatesTheHeroThenTheCameraAndBuildsTheHeroIntoTheScene()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        List<WorldRect> area = new List<WorldRect> { new WorldRect(0, 0, 2000, 1000) };
        TownHero hero = Hero(assets, area, 300, 100);
        var camera = new TownCamera(0, 96, hero.X);
        var session = new TownSession(assets, AvatarFixture.AreaMap, hero, camera);
        Assert.Equal((0, 0), (session.NpcCount, session.LayerCount));
        Assert.Equal(0, session.Projection.CameraX);

        // 向右走 1 秒:世界 x 300 → 522.5,屏幕 522.5 > 450 → 镜头 72.5,取整 72
        session.Update(1000, 1, 0);
        Assert.Equal(300 + TownHero.DefaultWalkSpeedX, hero.X, 6);
        Assert.Equal(72.5, camera.X, 6);
        Assert.Equal(72, session.Projection.CameraX);

        List<SceneSprite> scene = session.Build(0);
        Assert.NotEmpty(scene);
        Assert.All(scene, s => Assert.Equal(TownHero.ObjectId, s.NpcId));
        // skin 的屏幕 x = 世界 522 - 镜头 72 + (-5) + (-3) = 442
        Assert.Equal(442, scene[0].ScreenX);
    }
}
