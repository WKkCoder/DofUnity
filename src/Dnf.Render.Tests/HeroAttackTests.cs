using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 主角的普攻连招(证据 E-069):.chr 的 [attack motion] 给出各段,按攻击键起手,挥击中再按则接下一段;
/// 以及武器图层动画的路径与房间会话把外来精灵排进深度顺序。全部用合成数据(三段普攻分别是 400、300、400 毫秒)。
/// </summary>
public class HeroAttackTests
{
    private static TownHero NewHero(DnfAssets assets, string chr = AvatarFixture.ChrPath, double x = 100, double y = 50, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr, outfit, new List<WorldRect>(), x, y, facingLeft, TownHero.DungeonRestTag);
    }

    [Fact]
    public void TheChainComesFromTheCharacterScriptInOrderAndResolvesPathsCaseInsensitively()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.Equal(3, hero.AttackCount);
        Assert.Equal(new double[] { 400, 300, 400, 0 }, new[] { hero.AttackDurationMs(0), hero.AttackDurationMs(1), hero.AttackDurationMs(2), hero.AttackDurationMs(3) });
        Assert.False(hero.IsAttacking);
        Assert.Null(hero.AttackMotionFile);
        Assert.Equal(0L, hero.SwingNumber);

        // 脚本里第二段写成 "Animation/Attack2.ani"(大小写混合),照样载入
        hero.Update(16, 0, 0, true);
        Assert.Equal(AvatarFixture.Attack1Ani, hero.CurrentAnimationPath);
        Assert.Equal("attack1.ani", hero.AttackMotionFile);
    }

    [Fact]
    public void PressingAttackWhileIdleStartsStageZeroAndMayTurnTheHeroButDoesNotMoveIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, facingLeft: false);

        hero.Update(16, -1, 0, true);                       // 一边按左一边出招:起手那一刻转身,但这一帧不走
        Assert.True(hero.IsAttacking);
        Assert.Equal(HeroMotion.Attack, hero.Motion);
        Assert.Equal(0, hero.AttackStage);
        Assert.Equal(1L, hero.SwingNumber);
        Assert.True(hero.FacingLeft);
        Assert.Equal((100.0, 50.0), (hero.X, hero.Y));
        Assert.Equal(16L, hero.AttackAnimationMs);
    }

    [Fact]
    public void ASwingEndsAfterItsAnimationAndTheHeroGoesBackToRest()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        hero.Update(16, 0, 0, true);
        hero.Update(383, 0, 0);                             // 共 399 毫秒:还在挥
        Assert.True(hero.IsAttacking);
        Assert.Equal(3, hero.CurrentFrame);                 // 第四帧(300 到 400 毫秒)
        hero.Update(1, 0, 0);                               // 400 毫秒:挥完
        Assert.False(hero.IsAttacking);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(0, hero.AttackStage);
        Assert.Equal(1L, hero.SwingNumber);

        // 回到站立后可以再起手,挥击序号继续加
        hero.Update(16, 0, 0, true);
        Assert.Equal(2L, hero.SwingNumber);
        Assert.Equal(0, hero.AttackStage);
    }

    [Fact]
    public void PressingDuringASwingQueuesTheNextStageAndKeepsTheLeftoverTime()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        hero.Update(16, 0, 0, true);
        hero.Update(84, 0, 0, true);                        // 100 毫秒时再按一次:排队,第一段继续放完
        Assert.Equal(0, hero.AttackStage);
        hero.Update(310, 0, 0);                             // 410 毫秒:第一段 400 毫秒放完,多出的 10 毫秒带进第二段
        Assert.Equal(1, hero.AttackStage);
        Assert.Equal(2L, hero.SwingNumber);
        Assert.Equal("attack2.ani", hero.AttackMotionFile);
        Assert.Equal(10L, hero.AttackAnimationMs);

        // 第二段没有再按:放完(300 毫秒)就回站立
        hero.Update(289, 0, 0);
        Assert.True(hero.IsAttacking);
        hero.Update(1, 0, 0);
        Assert.False(hero.IsAttacking);
    }

    [Fact]
    public void TheWholeChainRunsInOrderAndPressesPastTheLastStageAreIgnored()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        var stages = new List<int>();

        // 每 10 毫秒按一次攻击键,一直按
        for (int i = 0; i < 200; i++)
        {
            hero.Update(10, 0, 0, true);
            stages.Add(hero.IsAttacking ? hero.AttackStage : -1);
        }
        // 400 + 300 + 400 毫秒 = 第 1 到 40 帧是第 0 段,41 到 70 帧是第 1 段,71 到 110 帧是第 2 段;
        // 第 2 段里按的键不排队(没有第 3 段),第 110 帧挥完回站立,第 111 帧的按键又从头起手
        Assert.Equal(0, stages[0]);
        Assert.Equal(0, stages[38]);
        Assert.Equal(1, stages[40]);
        Assert.Equal(2, stages[70]);
        Assert.Equal(2, stages[108]);
        Assert.DoesNotContain(3, stages);
        Assert.True(hero.SwingNumber >= 4);                 // 200 帧里至少打完一轮后又从头起手
        Assert.Contains(0, stages.Skip(110));
    }

    [Fact]
    public void TheHeroCannotWalkOrTurnWhileSwinging()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, facingLeft: false);

        hero.Update(16, 0, 0, true);
        hero.Update(100, -1, 1);                            // 挥击中按左下:既不走也不转身
        Assert.Equal((100.0, 50.0), (hero.X, hero.Y));
        Assert.False(hero.FacingLeft);
        Assert.Equal(HeroMotion.Attack, hero.Motion);
    }

    [Fact]
    public void TheAttackRateScalesTheWallClockDurationButNotTheAnimationClock()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.AttackAnimationRate = 2.0;
        Assert.Equal(200.0, hero.AttackDurationMs(0));       // 400 / 2
        Assert.Equal(150.0, hero.AttackDurationMs(1));

        hero.Update(16, 0, 0, true);
        hero.Update(84, 0, 0);                              // 墙钟 100 毫秒 = 动画 200 毫秒:第 3 帧
        Assert.Equal(200L, hero.AttackAnimationMs);
        Assert.Equal(2, hero.CurrentFrame);
        hero.Update(100, 0, 0);
        Assert.False(hero.IsAttacking);

        // 非法倍率(≤ 0)按 1 处理
        hero.AttackAnimationRate = 0;
        Assert.Equal(400.0, hero.AttackDurationMs(0));
    }

    [Fact]
    public void TheMoveSpeedStatComesFromTheCharacterScriptAndIsZeroWhenMissing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Equal(AvatarFixture.MoveSpeedStat, NewHero(assets).MoveSpeedStat);
        Assert.Equal(0, NewHero(assets, AvatarFixture.NoAttackChrPath).MoveSpeedStat);
    }

    [Fact]
    public void WithoutAttackMotionsTheAttackKeyDoesNothing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, AvatarFixture.NoAttackChrPath);
        Assert.Equal(0, hero.AttackCount);
        hero.Update(16, 0, 0, true);
        Assert.False(hero.IsAttacking);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(0L, hero.SwingNumber);

        // 按住攻击键的同时走:照常走
        hero.Update(1000, 1, 0, true);
        Assert.Equal(HeroMotion.Move, hero.Motion);
        Assert.True(hero.X > 100);

        hero.Place(0, 0, false, HeroMotion.Attack, 0);       // 没有攻击动作时摆成站立
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        hero.PlaceAttack(5, 6, true, 1, 123);
        Assert.Equal((HeroMotion.Rest, 5.0, 6.0, true), (hero.Motion, hero.X, hero.Y, hero.FacingLeft));
    }

    [Fact]
    public void PlaceAttackPutsTheHeroMidSwingAndClampsTheStage()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);

        hero.PlaceAttack(10, 20, true, 1, 150);
        Assert.True(hero.IsAttacking);
        Assert.Equal((1, 150L, true), (hero.AttackStage, hero.AttackAnimationMs, hero.FacingLeft));
        Assert.Equal(1, hero.CurrentFrame);                  // 第二段:三帧 × 100 毫秒,150 毫秒在第二帧

        hero.PlaceAttack(10, 20, false, 9, -5);              // 段号夹到最后一段,时间夹到 0
        Assert.Equal((2, 0L), (hero.AttackStage, hero.AttackAnimationMs));
        hero.PlaceAttack(10, 20, false, -3, 0);
        Assert.Equal(0, hero.AttackStage);

        hero.Place(1, 2, false, HeroMotion.Move, 40);        // 摆成别的动作:攻击状态清掉
        Assert.False(hero.IsAttacking);
        Assert.Equal(0, hero.AttackStage);
    }

    [Fact]
    public void WeaponLayerAnimationsLiveInAFolderPerLayerNextToTheWeaponScript()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);

        // 装备脚本里每个图层(含没有图片的 swordd)各有一个目录;文件名转小写
        Assert.Equal(new[]
        {
            AvatarFixture.WeaponLayerB + "/attack1.ani",
            AvatarFixture.WeaponLayerC + "/attack1.ani",
            AvatarFixture.EquipRoot + "/weapon/sword/swordd/attack1.ani",
        }, outfit.WeaponAnimationPaths("Attack1.ani").ToArray());
        Assert.Empty(outfit.WeaponAnimationPaths(null));
        Assert.Empty(outfit.WeaponAnimationPaths(""));

        // 没有武器时没有路径
        AvatarOutfit bare = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, null);
        Assert.Empty(bare.WeaponAnimationPaths("attack1.ani"));
    }

    private static RoomMap Corridor(int tiles)
    {
        var list = new List<TileFile>();
        for (int i = 0; i < tiles; i++)
        {
            list.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, RoomFixture.PassTable().Select(v => (byte)v).ToArray()));
        }
        return new RoomMap("map/test/corridor.map", "[normal]", list, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
            new List<int[]> { null, null, null, null }, null);
    }

    private static SpriteFrame Block(int w, int h, byte r)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(w, h, 0, 0, w, h, rgba);
    }

    [Fact]
    public void ARoomSessionSortsExtraSpritesIntoTheDepthOrderWithTheHero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var content = new RoomContent(Corridor(2), null, null, null, null);
        TownHero hero = NewHero(assets, x: 100, y: 50);
        var session = new RoomSession(assets, content, hero, new TownCamera(0, content.Map.MaxCameraX, hero.X));

        SpriteFrame behind = Block(4, 4, 11);
        SpriteFrame front = Block(4, 4, 22);
        SpriteFrame ui = Block(4, 4, 33);
        var extra = new List<SceneSprite>
        {
            new SceneSprite(ui, 0, 0, 0, -3, "ui", 0, false, SpriteBlend.Normal, 255, 6),       // 大类 6:盖在所有地图物体之上
            new SceneSprite(front, 0, 0, 80, 1001, "front", 0),                                 // 世界 y = 80 > 主角 50:画在主角之后(在上面)
            new SceneSprite(behind, 0, 0, 20, 1000, "behind", 0),                               // 世界 y = 20 < 50:画在主角之前(在下面)
        };
        List<SceneSprite> list = session.Build(0, extra);

        int heroCount = list.Count(s => s.NpcId == TownHero.ObjectId);
        Assert.True(heroCount > 0);
        List<string> order = list.Select(s => s.NpcId == TownHero.ObjectId ? "hero" : s.AnimationPath).Distinct().ToList();
        Assert.Equal(new[] { "behind", "hero", "front", "ui" }, order.ToArray());

        // 不带外来精灵时与以前一样;不画主角时只剩外来精灵
        Assert.Equal(heroCount, session.Build(0).Count);
        session.HeroVisible = false;
        Assert.Equal(3, session.Build(0, extra).Count);
        Assert.Empty(session.Build(0, new List<SceneSprite>()));
    }
}
