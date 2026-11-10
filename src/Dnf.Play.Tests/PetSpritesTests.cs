using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 在临时目录里造一份只够出宠物精灵的"迷你游戏目录":Script.pvf 里是几个合成的二进制 .ani,ImagePacks2 里一个合成的 IMG(两帧:4×3 偏移 (1,1)、2×2 偏移 (3,2))。
/// 动画数据(帧偏移、时长、循环)全是测试自己编的。
/// </summary>
internal sealed class PetArtFixture : IDisposable
{
    public const string StayAni = "creature/p901/motion/stay.ani";
    public const string WalkAni = "creature/p901/motion/walk.ani";
    public const string BlankAni = "creature/p901/motion/blank.ani";
    public const string OnceAni = "creature/p901/motion/once.ani";
    public const string PlainStayAni = "creature/p903/motion/stay.ani";
    public const string TintAni = "creature/p901/motion/tint.ani";
    public const string EmptyAni = "creature/p901/motion/empty.ani";

    public string Root { get; }

    public PetArtFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "dnf-pet-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

        var files = new List<TestData.PvfFileSpec>
        {
            new("stringtable.bin", TestData.BuildStringTable(new List<byte[]> { Encoding.ASCII.GetBytes("x") }), 0),
            new(StayAni, Ani(Loop, Frame(0, -10, -20, 100), Frame(1, -14, -16, 200)), 0),   // 站立:两帧,100 毫秒 + 200 毫秒,循环
            new(WalkAni, Ani(Loop, Frame(1, -5, -7, 100)), 0),                                // 行走:一帧
            new(BlankAni, Ani(Loop, Frame(-1, 0, 0, 100)), 0),                                // 这一帧没有图
            new(OnceAni, Ani(None, Frame(0, -10, -20, 100), Frame(1, -14, -16, 100)), 0),     // 不循环:停在最后一帧
            new(PlainStayAni, Ani(Loop, Frame(0, -3, -4, 100)), 0),
            new(TintAni, Ani(Loop, Frame(0, -10, -20, 100, tintAlpha: 200), Frame(1, -14, -16, 200, tintAlpha: 90)), 0),   // 两帧各带一个染色 alpha,不插值
            new(EmptyAni, Ani(Loop), 0),                                                                                   // 读得到,但一帧也没有
        };
        (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
        File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);
        File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "pets.npk"), TestData.BuildNpk(new[] { ("sprite/creature/test/pet.img", Img()) }));
    }

    private static readonly (ushort, byte[])[] Loop = { (0, new byte[] { 1 }) };
    private static readonly (ushort, byte[])[] None = Array.Empty<(ushort, byte[])>();

    private static TestData.AniFrameSpec Frame(int imageFrame, int x, int y, int delayMs, int tintAlpha = -1)
    {
        var f = new TestData.AniFrameSpec { ImageIndex = imageFrame < 0 ? -1 : 0, FrameIndex = Math.Max(imageFrame, 0), X = x, Y = y };
        f.Props.Add((12, TestData.LE32(delayMs)));
        if (tintAlpha >= 0)
        {
            f.Props.Add((9, new byte[] { 255, 255, 255, (byte)tintAlpha }));   // 染色 R、G、B、A
        }
        return f;
    }

    private static byte[] Ani((ushort, byte[])[] globals, params TestData.AniFrameSpec[] frames)
    {
        return TestData.BuildAni(new[] { "Creature/Test/Pet.img" }, globals, frames);
    }

    private static byte[] Img()
    {
        TestData.ImgFrameSpec a = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 4, 3, Enumerable.Repeat((byte)200, 4 * 3 * 4).ToArray());
        a.X = 1;
        a.Y = 1;
        a.FrameWidth = 6;
        a.FrameHeight = 5;
        TestData.ImgFrameSpec b = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 2, 2, Enumerable.Repeat((byte)100, 2 * 2 * 4).ToArray());
        b.X = 3;
        b.Y = 2;
        b.FrameWidth = 5;
        b.FrameHeight = 4;
        return TestData.BuildImg(new[] { a, b });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>宠物形象走现成的 ANI 构建口(AniTimeline + MonsterSprites.SpriteAt),不写新的渲染:帧、位置、镜像都按同一套规则。</summary>
public class PetSpritesTests
{
    private static readonly TownProjection Projection = new TownProjection(24, 200);

    private static PetLook Look(string basic, string walk = null, string run = null, int height = 0)
    {
        return new PetLook(901, "creature/p901/p901.cre", basic, walk, run, height, null, null);
    }

    private static SceneSprite Build(PetSprites sprites, PetLook look, PetMotion motion, long animMs, bool facingLeft = false, int z = 37)
    {
        return sprites.Build(look, motion, animMs, 500, 300, z, facingLeft, Projection, 5001);
    }

    [Fact]
    public void TheStandAnimationPlaysFrameByFrameAtTheProjectedPositionFacingRight()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);
        PetLook look = Look(PetArtFixture.StayAni);

        // 帧 0(0 到 99 毫秒):x = 世界 500 − 镜头 24 + 帧偏移 −10 + 帧头 1 = 467;y = 300 − 高度 37 + 地面 200 + (−20) + 1 = 444
        SceneSprite f0 = Build(sprites, look, PetMotion.Stand, 0);
        Assert.Equal((467, 444, 300, 5001), (f0.ScreenX, f0.ScreenY, f0.SortKey, f0.NpcId));
        Assert.Equal((PetArtFixture.StayAni, 0, false, 4), (f0.AnimationPath, f0.AniFrame, f0.Mirrored, f0.Frame.Width));
        Assert.Equal(0, Build(sprites, look, PetMotion.Stand, 99).AniFrame);

        // 帧 1(100 到 299 毫秒):500 − 24 − 14 + 3 = 465;300 − 37 + 200 − 16 + 2 = 449;帧图是 2 像素宽
        SceneSprite f1 = Build(sprites, look, PetMotion.Stand, 100);
        Assert.Equal((465, 449, 1, 2), (f1.ScreenX, f1.ScreenY, f1.AniFrame, f1.Frame.Width));
        Assert.Equal(1, Build(sprites, look, PetMotion.Stand, 299).AniFrame);

        // 循环:300 毫秒回到第 0 帧
        Assert.Equal(0, Build(sprites, look, PetMotion.Stand, 300).AniFrame);
        Assert.Equal(1, Build(sprites, look, PetMotion.Stand, 300 + 150).AniFrame);
        Assert.Equal(255, f0.TintAlpha);                            // 普通混合:不透明
    }

    [Fact]
    public void FacingLeftMirrorsAroundTheWorldXWithTheFramesOwnWidth()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);
        PetLook look = Look(PetArtFixture.StayAni);

        // 镜像:左边缘 = 世界 x − 镜头 − (帧偏移 + 帧头偏移 + 像素块宽度);帧 0:500 − 24 − (−10 + 1 + 4) = 481
        SceneSprite f0 = Build(sprites, look, PetMotion.Stand, 0, facingLeft: true);
        Assert.Equal((481, 444, true), (f0.ScreenX, f0.ScreenY, f0.Mirrored));
        // 帧 1:500 − 24 − (−14 + 3 + 2) = 485
        SceneSprite f1 = Build(sprites, look, PetMotion.Stand, 100, facingLeft: true);
        Assert.Equal((485, 449, true), (f1.ScreenX, f1.ScreenY, f1.Mirrored));
    }

    [Fact]
    public void TheWorldHeightLiftsTheSpriteAndTheCameraAndGroundMoveIt()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);
        PetLook look = Look(PetArtFixture.StayAni);

        Assert.Equal(444 + 37, Build(sprites, look, PetMotion.Stand, 0, z: 0).ScreenY);        // 离地 0:比 37 时低 37 像素
        Assert.Equal(444 - 10, Build(sprites, look, PetMotion.Stand, 0, z: 47).ScreenY);
        SceneSprite moved = sprites.Build(look, PetMotion.Stand, 0, 500, 300, 37, false, new TownProjection(100, 150), 7);
        Assert.Equal((467 - 76, 444 - 50, 7), (moved.ScreenX, moved.ScreenY, moved.NpcId));
    }

    [Fact]
    public void WalkAndRunUseTheirOwnAnimationsAndRunFallsBackToWalkThenToStand()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);
        PetLook withWalk = Look(PetArtFixture.StayAni, walk: PetArtFixture.WalkAni);

        // 行走动画:图片帧 1,偏移 (−5,−7):500 − 24 − 5 + 3 = 474;300 − 37 + 200 − 7 + 2 = 458
        SceneSprite walk = Build(sprites, withWalk, PetMotion.Walk, 0);
        Assert.Equal((PetArtFixture.WalkAni, 474, 458), (walk.AnimationPath, walk.ScreenX, walk.ScreenY));
        // 没写奔跑动画:奔跑用行走的
        SceneSprite run = Build(sprites, withWalk, PetMotion.Run, 0);
        Assert.Equal((PetArtFixture.WalkAni, 474, 458), (run.AnimationPath, run.ScreenX, run.ScreenY));
        Assert.Equal(PetArtFixture.StayAni, Build(sprites, withWalk, PetMotion.Stand, 0).AnimationPath);

        // 什么动作都只有站立动画:追的时候也画站立
        PetLook onlyStand = Look(PetArtFixture.PlainStayAni);
        Assert.Equal(PetArtFixture.PlainStayAni, Build(sprites, onlyStand, PetMotion.Walk, 0).AnimationPath);
        Assert.Equal(PetArtFixture.PlainStayAni, Build(sprites, onlyStand, PetMotion.Run, 0).AnimationPath);
    }

    [Fact]
    public void AnAnimationFileThatCannotBeReadFallsBackAlongRunWalkStandAndNothingLeftMeansNoSprite()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);
        const string missing = "creature/p901/motion/missing.ani";

        // 奔跑的文件缺失:退到行走
        PetLook runMissing = Look(PetArtFixture.StayAni, walk: PetArtFixture.WalkAni, run: missing);
        Assert.Equal(PetArtFixture.WalkAni, Build(sprites, runMissing, PetMotion.Run, 0).AnimationPath);
        // 行走的文件缺失:退到站立(奔跑也一样,它先退行走、行走也缺,再退站立)
        PetLook walkMissing = Look(PetArtFixture.StayAni, walk: missing, run: missing);
        Assert.Equal(PetArtFixture.StayAni, Build(sprites, walkMissing, PetMotion.Walk, 0).AnimationPath);
        Assert.Equal(PetArtFixture.StayAni, Build(sprites, walkMissing, PetMotion.Run, 0).AnimationPath);
        // 站立缺失:站着时没有图;但追的时候有行走动画就有图
        PetLook standMissing = Look(missing, walk: PetArtFixture.WalkAni);
        Assert.Null(Build(sprites, standMissing, PetMotion.Stand, 0));
        Assert.Equal(PetArtFixture.WalkAni, Build(sprites, standMissing, PetMotion.Walk, 0).AnimationPath);
        // 全缺、全没写
        Assert.Null(Build(sprites, Look(missing, missing, missing), PetMotion.Run, 0));
        Assert.Null(Build(sprites, Look(null), PetMotion.Stand, 0));
        // 再问一次(缺失的也被记住了):结果不变
        Assert.Null(Build(sprites, Look(missing), PetMotion.Stand, 0));
        Assert.Null(Build(sprites, Look(missing), PetMotion.Stand, 0));
    }

    [Fact]
    public void AFrameWithoutAPictureAndANonLoopingAnimationBehaveAsTheAnimationBuilderSaysAndANullLookGivesNothing()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);

        Assert.Null(Build(sprites, Look(PetArtFixture.BlankAni), PetMotion.Stand, 0));          // 这一帧没有图
        PetLook once = Look(PetArtFixture.OnceAni);
        Assert.Equal(0, Build(sprites, once, PetMotion.Stand, 99).AniFrame);
        Assert.Equal(1, Build(sprites, once, PetMotion.Stand, 100).AniFrame);
        Assert.Equal(1, Build(sprites, once, PetMotion.Stand, 100_000).AniFrame);               // 不循环:停在最后一帧

        Assert.Null(sprites.Build(null, PetMotion.Stand, 0, 0, 0, 0, false, Projection, 1));
        Assert.Throws<ArgumentNullException>(() => new PetSprites(null));
    }

    [Fact]
    public void TheTintAlphaIsTheOneOfTheFrameShownAtThatMomentNotOfTheFirstFrame()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);
        PetLook look = Look(PetArtFixture.TintAni);

        Assert.Equal(200, Build(sprites, look, PetMotion.Stand, 0).TintAlpha);          // 帧 0(0 到 99 毫秒)
        Assert.Equal(200, Build(sprites, look, PetMotion.Stand, 99).TintAlpha);
        Assert.Equal(90, Build(sprites, look, PetMotion.Stand, 100).TintAlpha);         // 帧 1(100 到 299 毫秒)
        Assert.Equal(90, Build(sprites, look, PetMotion.Stand, 299).TintAlpha);
        Assert.Equal(200, Build(sprites, look, PetMotion.Stand, 300).TintAlpha);        // 循环回第 0 帧
    }

    [Fact]
    public void AnAnimationFileWithoutAnyFrameCountsAsMissingAndTheFallbackChainContinues()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var sprites = new PetSprites(assets);

        // 行走、奔跑的动画文件读得到、但一帧也没有:当作没有,退到站立
        PetLook emptyWalk = Look(PetArtFixture.StayAni, walk: PetArtFixture.EmptyAni, run: PetArtFixture.EmptyAni);
        Assert.Equal(PetArtFixture.StayAni, Build(sprites, emptyWalk, PetMotion.Walk, 0).AnimationPath);
        Assert.Equal(PetArtFixture.StayAni, Build(sprites, emptyWalk, PetMotion.Run, 0).AnimationPath);
        // 站立动画本身没有帧:站着时没有图
        Assert.Null(Build(sprites, Look(PetArtFixture.EmptyAni), PetMotion.Stand, 0));
    }

    [Fact]
    public void FromTheCreScriptThroughTheStateLayerAndTheFieldToTheDrawnSprite()
    {
        using var art = new PetArtFixture();
        using DnfAssets assets = DnfAssets.Open(art.Root);
        var rig = new PetTownKit.Rig();
        var field = new TownPetField(rig.Session, rig.Clock, rig.Looks, new PetSprites(assets), PetTownKit.SimpleRules);
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull);

        // 宠物 901 站在主角 (500,300) 背后 41:(459,300),离地 37(都来自 .cre)。站立动画帧 0:459 − 24 − 10 + 1 = 426;300 − 37 + 200 − 20 + 1 = 444
        var master = new PetMaster(500, 300, false);
        SceneSprite stand = Assert.Single(field.BuildSprites(Projection, master));
        Assert.Equal((426, 444, TownPetField.ObjectIdBase + pet.Uid, PetArtFixture.StayAni), (stand.ScreenX, stand.ScreenY, stand.NpcId, stand.AnimationPath));

        // 站着播 150 毫秒:动画走到第 1 帧(.cre 的站立动画 → AniTimeline → 帧):459 − 24 − 14 + 3 = 424;300 − 37 + 200 − 16 + 2 = 449
        field.Advance(150, master);
        SceneSprite later = Assert.Single(field.BuildSprites(Projection, master));
        Assert.Equal((424, 449, 1), (later.ScreenX, later.ScreenY, later.AniFrame));

        // 主角跑远了:宠物追(离停靠点超过 120 → 奔跑;901 没写奔跑动画 → 行走动画),动作一换动画从头播
        var far = new PetMaster(700, 300, false);
        field.Advance(100, far);
        TownPetEntity entity = Assert.Single(field.Entities);
        Assert.Equal(PetMotion.Run, entity.Motion);
        SceneSprite chasing = Assert.Single(field.BuildSprites(Projection, far));
        Assert.Equal(PetArtFixture.WalkAni, chasing.AnimationPath);
        Assert.Equal((0, 2), (chasing.AniFrame, chasing.Frame.Width));   // 行走动画只有一帧,指向图片的第 1 帧(2 像素宽)
        Assert.Equal((int)Math.Floor(entity.X) - 24 - 5 + 3, chasing.ScreenX);
    }
}
