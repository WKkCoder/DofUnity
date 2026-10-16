using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>怪物的外观链条(证据 E-069):monster/monster.lst → .mob 的动作脚本 → .ani → IMG 帧,以及朝向与摆放。全部用合成数据。</summary>
public class MonsterTests
{
    private const string Stay = AvatarFixture.BlobAnimationDir + "/stay.ani";
    private const string Move = AvatarFixture.BlobAnimationDir + "/move.ani";
    private const string Damage1 = AvatarFixture.BlobAnimationDir + "/damage1.ani";
    private const string Damage2 = AvatarFixture.BlobAnimationDir + "/damage2.ani";
    private const string Down = AvatarFixture.BlobAnimationDir + "/down.ani";
    private const string Attack1 = AvatarFixture.BlobAnimationDir + "/attack1.ani";

    [Fact]
    public void TheCatalogMapsCodesToLowerCasePathsUnderTheMonsterFolder()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterCatalog catalog = MonsterCatalog.Read(assets);
        Assert.Equal(3, catalog.Count);
        Assert.Equal("monster/blob/blob.mob", catalog.PathOf(1));
        Assert.Equal("monster/blob/big.mob", catalog.PathOf(2));         // 脚本里是 "Blob\Big.mob":反斜杠换成斜杠、转小写
        Assert.Equal("monster/blob/missing.mob", catalog.PathOf(3));
        Assert.Null(catalog.PathOf(99));
    }

    [Fact]
    public void ACatalogThatIsNotInThePvfIsAnError()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Throws<DnfFormatException>(() => MonsterCatalog.Read(assets));
    }

    [Fact]
    public void AModelReadsEveryMotionRelativeToTheMobScript()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        Assert.NotNull(model);
        Assert.Equal(AvatarFixture.BlobMobPath, model.MobPath);
        Assert.Equal(Stay, model.PathOf(model.Waiting));
        Assert.Equal(Move, model.PathOf(model.Move));
        Assert.Equal(Damage1, model.PathOf(model.Damage1));
        Assert.Equal(Damage2, model.PathOf(model.Damage2));
        Assert.Equal(Down, model.PathOf(model.Down));
        Assert.Null(model.Overturn);                                       // 脚本里没有 [overturn motion]
        Assert.Equal(new[] { Attack1 }, model.Attacks.Select(a => model.PathOf(a)).ToArray());
        // 攻击信息保持脚本里的顺序,空串表示这个攻击动作没有攻击信息;相对 .mob 所在的目录
        Assert.Equal(new[] { "monster/blob/attackinfo/attack1.atk", string.Empty }, model.AttackInfoPaths.ToArray());
        Assert.Null(model.PathOf(null));

        // 受击动画只有一帧、停留 10000 毫秒;倒地动画有三帧
        Assert.Single(model.Damage1.Frames);
        Assert.Equal(3, model.Down.Frames.Length);
    }

    [Fact]
    public void AModelWithOnlyAWaitingMotionHasNullsAndAnEmptyAttackList()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel big = MonsterModel.Read(assets, "monster/blob/big.mob");
        Assert.NotNull(big.Waiting);
        Assert.Null(big.Move);
        Assert.Null(big.Damage1);
        Assert.Null(big.Damage2);
        Assert.Null(big.Down);
        Assert.Empty(big.Attacks);
        Assert.Empty(big.AttackInfoPaths);
        // 同一个动画文件:两个模型各自载入的对象不一定相同,但路径一样
        Assert.Equal(Stay, big.PathOf(big.Waiting));

        Assert.Null(MonsterModel.Read(assets, "monster/blob/missing.mob"));
    }

    [Fact]
    public void TheMonsterArtFacesRightSoFacingLeftMirrorsItAboutTheWorldX()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var projection = new TownProjection(10, 200);

        // 帧偏移 (-10, -20),IMG 头偏移 (-3, -8),像素块 6 × 8;世界 (100, 50, 0)
        SceneSprite right = MonsterSprites.SpriteAt(assets, model.Waiting, Stay, 1, projection, 100, 50, 0, true, 1000);
        Assert.NotNull(right);
        Assert.False(right.Mirrored);
        Assert.Equal((100 - 10 - 10 - 3, 50 + 200 - 20 - 8), (right.ScreenX, right.ScreenY));
        Assert.Equal(50, right.SortKey);
        Assert.Equal((1000, Stay, 1), (right.NpcId, right.AnimationPath, right.AniFrame));
        Assert.Equal(1, right.Frame.Rgba[1]);                              // 绿色通道 = IMG 帧号:待机第 1 帧取 IMG 帧 1
        Assert.Equal(6, right.Frame.Width);

        // 朝左:翻转后的左边缘 = 世界 x − 镜头 − (帧偏移 x + 头偏移 x + 宽) = 100 − 10 − (−10 − 3 + 6)
        SceneSprite left = MonsterSprites.SpriteAt(assets, model.Waiting, Stay, 1, projection, 100, 50, 0, false, 1000);
        Assert.True(left.Mirrored);
        Assert.Equal(100 - 10 - (-10 - 3 + 6), left.ScreenX);
        Assert.Equal(right.ScreenY, left.ScreenY);
        Assert.Same(right.Frame, left.Frame);                              // 同一个像素块,只是翻转绘制

        // 高度(z)抬起画面位置,不改排序键
        SceneSprite high = MonsterSprites.SpriteAt(assets, model.Waiting, Stay, 1, projection, 100, 50, 30, true, 1000);
        Assert.Equal(right.ScreenY - 30, high.ScreenY);
        Assert.Equal(50, high.SortKey);
    }

    [Fact]
    public void ASpriteIsNullWhenTheFrameIsOutOfRangeOrTheAniIsMissing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var projection = new TownProjection(0, 200);
        Assert.Null(MonsterSprites.SpriteAt(assets, null, "x", 0, projection, 0, 0, 0, true, 1000));
        Assert.Null(MonsterSprites.SpriteAt(assets, model.Waiting, Stay, 2, projection, 0, 0, 0, true, 1000));
        Assert.Null(MonsterSprites.SpriteAt(assets, model.Waiting, Stay, -1, projection, 0, 0, 0, true, 1000));
    }
}
