using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 把宠物接进城镇会话的装饰器:不改 <see cref="TownSession"/>,包一层——里面的会话先走,宠物按主角的新位置跟一步;出图时并进里面的列表并重排;
/// 出口、地图声音、NPC 表原样转给里面。主角是用合成的迷你职业造的真 <see cref="TownHero"/>,宠物与数值同样是合成的。
/// </summary>
public class TownPetSceneTests
{
    private static TownHero NewHero(DnfAssets assets, double x, double y, bool left = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), x, y, left);
    }

    private static SceneSprite Extra(int sortKey, int id) => new SceneSprite(new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[4]), 5, 5, sortKey, id, "extra", 0);

    /// <summary>四个可选接口都实现的假城镇会话:主角与镜头是真的,出图的列表、出口、声音、NPC 表由测试给。</summary>
    private sealed class FakeTown : IHeroSession, ITownExitSource, IMapSoundSource, ITownNpcSource
    {
        public FakeTown(TownHero hero, TownCamera camera)
        {
            Hero = hero;
            Camera = camera;
        }

        public string MapPath { get; set; } = "map/test/fake.map";

        public TownHero Hero { get; }

        public TownCamera Camera { get; }

        public int LayerCount { get; set; } = 7;

        public int NpcCount { get; set; } = 3;

        public List<SceneSprite> Sprites { get; } = new List<SceneSprite>();

        public int Updates { get; private set; }

        public double LastDelta { get; private set; }

        public (int X, int Y) LastDirection { get; private set; }

        public long LastBuildTime { get; private set; } = -1;

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Updates++;
            LastDelta = deltaMs;
            LastDirection = (dirX, dirY);
            Hero.Update(deltaMs, dirX, dirY);
            Camera.Follow(Hero.X);
        }

        public List<SceneSprite> Build(long timeMs)
        {
            LastBuildTime = timeMs;
            return Sprites;
        }

        public TownExit? PendingExit { get; set; }

        public void OutwardOf(TownExit exit, out int dx, out int dy)
        {
            dx = exit.Town;
            dy = -exit.Area;
        }

        public double ReportedX { get; set; } = 11;

        public double ReportedY { get; set; } = 22;

        public bool ReportedLeft { get; set; } = true;

        public double HeroX => ReportedX;

        public double HeroY => ReportedY;

        public bool HeroFacingLeft => ReportedLeft;

        public IReadOnlyList<string> MapSounds { get; set; } = new[] { "TEST_MUSIC", "TEST_AMBIENT" };

        public IReadOnlyList<TownNpc> Npcs { get; set; } = new[] { new TownNpc(5, 1, 2, 3) };
    }

    /// <summary>什么可选接口都不实现、没有主角也没有镜头的假会话(早先的流程测试里就是这样的)。</summary>
    private sealed class BareSession : IHeroSession
    {
        public string MapPath => "map/test/bare.map";

        public TownHero Hero => null;

        public TownCamera Camera => null;

        public int LayerCount => 0;

        public int NpcCount => 0;

        public int Updates { get; private set; }

        public List<SceneSprite> Sprites { get; } = new List<SceneSprite>();

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Updates++;
        }

        public List<SceneSprite> Build(long timeMs) => Sprites;
    }

    private sealed class FakeWorld : ITownWorld
    {
        public Func<TownPlace, IHeroSession> Make { get; set; }

        public TownPlace Start { get; set; } = new TownPlace(1, 2);

        public TownPlace Return { get; set; } = new TownPlace(3, 4);

        public List<TownPlace> Created { get; } = new List<TownPlace>();

        public IHeroSession Create(TownPlace place)
        {
            Created.Add(place);
            return Make(place);
        }

        public SplashScene SplashResult { get; set; }

        public (TownPlace? From, TownPlace To) LastSplash { get; private set; }

        public SplashScene Splash(TownPlace? from, TownPlace to)
        {
            LastSplash = (from, to);
            return SplashResult;
        }

        public TownPlace? ThroughResult { get; set; }

        public (TownPlace From, TownExit Exit, bool FacingLeft) LastThrough { get; private set; }

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
        {
            LastThrough = (from, exit, facingLeft);
            return ThroughResult;
        }

        public bool RouteResult { get; set; }

        public TownExit RouteExit { get; set; }

        public TownPlace LastRouteFrom { get; private set; }

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            LastRouteFrom = from;
            exit = RouteExit;
            return RouteResult;
        }
    }

    // ------------------------------------------------------------ 更新与出图

    [Fact]
    public void TheInnerSessionIsUpdatedFirstAndThePetThenFollowsWhereTheHeroNowStands()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 100);
        var inner = new FakeTown(hero, new TownCamera(0, 96, hero.X));
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        var scene = new TownPetScene(inner, rig.Field);

        scene.Update(1000, 1, 0);                                   // 主角向右走 1 秒:300 → 522.5
        Assert.Equal((1, 1000.0, (1, 0)), (inner.Updates, inner.LastDelta, inner.LastDirection));
        Assert.Equal(300 + TownHero.DefaultWalkSpeedX, hero.X, 6);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Equal(hero.X + PetTownKit.FullSpan, entity.Last.TargetX, 9);   // 目标按主角走完之后的位置算(顺序反了就会是 300 − 41)
        Assert.Equal((hero.X + PetTownKit.FullSpan, hero.Y), (entity.X, entity.Y));   // 刚出现:直接在停靠点上

        // 再走:宠物追在后面,每一步的目标都是主角此刻位置往背后 41
        for (int i = 0; i < 5; i++)
        {
            scene.Update(100, 1, 0);
            Assert.Equal(hero.X + PetTownKit.FullSpan, entity.Last.TargetX, 9);
            Assert.True(entity.X <= entity.Last.TargetX + 1e-9);
        }
        Assert.Equal(PetFollowPhase.Chasing, entity.Last.Phase);
        Assert.True(entity.X < hero.X + PetTownKit.FullSpan);       // 还没追上
    }

    [Fact]
    public void TheDeltaTheSceneIsUpdatedWithIsTheTimeTheFollowingPetMovesFor()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 100);
        var inner = new FakeTown(hero, new TownCamera(0, 96, hero.X));
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        var scene = new TownPetScene(inner, rig.Field);

        scene.Update(16, 0, 0);                                     // 宠物出现在停靠点上,主角没动
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        double expected = 300 + PetTownKit.FullSpan;
        Assert.Equal(expected, entity.X, 9);

        // 之后每步主角走 100 毫秒,宠物追同样的 100 毫秒:简单规则下追赶率 5 × 0.1 秒 = 0.5,每步走掉离目标剩下距离的一半
        Assert.True(PetTownKit.FullSpeed[0] * TownHero.DefaultSpeedFactorX > 200.0);   // 前提:这几步的追赶速度(最多 195)碰不到宠物自己的最大速度
        for (int i = 0; i < 3; i++)
        {
            scene.Update(100, 1, 0);
            double target = hero.X + PetTownKit.FullSpan;
            expected += 0.5 * (target - expected);
            Assert.Equal(expected, entity.X, 9);
            Assert.Equal(hero.Y, entity.Y, 9);
            Assert.Equal(PetFollowPhase.Chasing, entity.Last.Phase);
        }
        Assert.True(entity.X > 300 + PetTownKit.FullSpan);          // 确实走了(delta 被换成 0 就会一步不动)
    }

    [Fact]
    public void BuildMergesThePetSpritesIntoTheInnerListAndSortsByDepthWithThePetInFrontOnTies()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 200);
        var inner = new FakeTown(hero, new TownCamera(0, 96, hero.X));
        SceneSprite behind = Extra(100, 1);
        SceneSprite front = Extra(300, 2);
        inner.Sprites.AddRange(new[] { behind, front });
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);                     // 宠物落在主角的 y = 200
        var scene = new TownPetScene(inner, rig.Field);

        List<SceneSprite> merged = scene.Build(1234);
        Assert.Equal(1234, inner.LastBuildTime);                    // 时间原样交给里面
        Assert.Equal(3, merged.Count);
        Assert.Equal(new[] { 1, TownPetField.ObjectIdBase + rig.Session.Equipped.Uid, 2 }, merged.Select(s => s.NpcId).ToArray());   // 100 < 200 < 300
        Assert.Same(behind, merged[0]);
        Assert.Same(front, merged[2]);
        Assert.Equal(2, inner.Sprites.Count);                       // 里面的列表没被改

        // 同一个 y:宠物排在后面,画在前面
        inner.Sprites.Add(Extra(200, 3));
        List<SceneSprite> tie = scene.Build(0);
        Assert.Equal(new[] { 1, 3, TownPetField.ObjectIdBase + rig.Session.Equipped.Uid, 2 }, tie.Select(s => s.NpcId).ToArray());
    }

    [Fact]
    public void WithoutAPetSpriteTheInnerListIsReturnedAsItIs()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 200);
        var inner = new FakeTown(hero, new TownCamera(0, 96, hero.X));
        inner.Sprites.Add(Extra(100, 1));
        var rig = new PetTownKit.Rig();
        rig.Adopt(PetTownKit.ItemFull);                             // 没出战
        var scene = new TownPetScene(inner, rig.Field);

        Assert.Same(inner.Sprites, scene.Build(0));

        rig.Session.Equip(rig.Session.Roster.Pets[0].Uid, 0, false);
        rig.Sprites.ReturnNothing = true;                           // 出战了,但这一帧没有图
        Assert.Same(inner.Sprites, scene.Build(0));
    }

    [Fact]
    public void AnEquippedPetShowsInTheNextBuildEvenIfTheSessionHasNotBeenUpdated()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 200);
        var inner = new FakeTown(hero, new TownCamera(0, 96, hero.X));
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.Adopt(PetTownKit.ItemFull);
        var scene = new TownPetScene(inner, rig.Field);
        Assert.Empty(scene.Build(0));

        rig.Window.Show();
        Assert.Equal(PetWindowResult.Equipped, rig.Window.Equip());   // 游戏暂停着,在窗口里让宠物出战
        Assert.Equal(TownPetField.ObjectIdBase + pet.Uid, Assert.Single(scene.Build(0)).NpcId);
        Assert.Equal(0, inner.Updates);

        Assert.Equal(PetWindowResult.Unequipped, rig.Window.Unequip());
        Assert.Empty(scene.Build(0));                                 // 收起:下一帧就没了
    }

    [Fact]
    public void ThePetIsProjectedWithTheInnerSessionsOwnCameraAndGround()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 里面是真的 TownSession:地面 y 以它自己的为准(不是装饰器构造时给的缺省 200)
        TownHero hero = NewHero(assets, 300, 100);
        var camera = new TownCamera(0, 96, hero.X);
        var session = new TownSession(assets, AvatarFixture.AreaMap, hero, camera, 123);
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        var scene = new TownPetScene(session, rig.Field);
        scene.Update(1000, 1, 0);                                   // 镜头跟着动
        scene.Build(0);
        RecordingSprites.Call call = rig.Sprites.Calls.Last();
        Assert.Equal((session.Projection.CameraX, 123), (call.Projection.CameraX, call.Projection.GroundY));
        Assert.True(call.Projection.CameraX > 0);
        Assert.True(scene.TryProjection(out TownProjection projection));
        Assert.Equal(session.Projection, projection);

        // 里面不是 TownSession:镜头 x 取自它的镜头,地面 y 取构造时给的
        TownHero other = NewHero(assets, 300, 100);
        var fake = new FakeTown(other, new TownCamera(0, 96, other.X));
        var rig2 = new PetTownKit.Rig();
        rig2.AdoptAndEquip(PetTownKit.ItemFull);
        var scene2 = new TownPetScene(fake, rig2.Field, 150);
        scene2.Update(1000, 1, 0);
        scene2.Build(0);
        RecordingSprites.Call call2 = rig2.Sprites.Calls.Last();
        Assert.Equal((fake.Camera.IntX, 150), (call2.Projection.CameraX, call2.Projection.GroundY));
    }

    // ------------------------------------------------------------ 接口原样转发

    [Fact]
    public void EverythingTheInnerSessionReportsIsForwardedUnchanged()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 100);
        var inner = new FakeTown(hero, new TownCamera(0, 96, hero.X));
        var rig = new PetTownKit.Rig();
        var scene = new TownPetScene(inner, rig.Field);

        Assert.Same(inner, scene.Inner);
        Assert.Same(rig.Field, scene.Pets);
        Assert.Equal(("map/test/fake.map", 7, 3), (scene.MapPath, scene.LayerCount, scene.NpcCount));
        Assert.Same(hero, scene.Hero);
        Assert.Same(inner.Camera, scene.Camera);

        // 出口、主角位置(接口自己报的,不是主角对象的)、声音、NPC 表
        var exit = new TownExit(new WorldRect(1, 2, 3, 4), 6, 9);
        Assert.Null(scene.PendingExit);
        inner.PendingExit = exit;
        Assert.Equal(exit.Rect.X, scene.PendingExit.Value.Rect.X);
        Assert.Equal((6, 9), (scene.PendingExit.Value.Town, scene.PendingExit.Value.Area));
        scene.OutwardOf(exit, out int dx, out int dy);
        Assert.Equal((6, -9), (dx, dy));
        Assert.Equal((11.0, 22.0, true), (scene.HeroX, scene.HeroY, scene.HeroFacingLeft));
        Assert.Equal(new[] { "TEST_MUSIC", "TEST_AMBIENT" }, scene.MapSounds.ToArray());
        Assert.Same(inner.Npcs, scene.Npcs);

        // 流程按接口认会话:装饰器本身四个接口都有
        Assert.IsAssignableFrom<IHeroSession>(scene);
        Assert.IsAssignableFrom<ITownExitSource>(scene);
        Assert.IsAssignableFrom<IMapSoundSource>(scene);
        Assert.IsAssignableFrom<ITownNpcSource>(scene);
    }

    [Fact]
    public void AnInnerSessionWithoutTheOptionalInterfacesGivesNeutralAnswers()
    {
        var inner = new BareSession();
        var rig = new PetTownKit.Rig();
        var scene = new TownPetScene(inner, rig.Field);

        Assert.Null(scene.PendingExit);
        scene.OutwardOf(new TownExit(new WorldRect(0, 0, 1, 1), 1, 1), out int dx, out int dy);
        Assert.Equal((0, 0), (dx, dy));
        Assert.Equal((0.0, 0.0, false), (scene.HeroX, scene.HeroY, scene.HeroFacingLeft));
        Assert.Empty(scene.MapSounds);
        Assert.Empty(scene.Npcs);
    }

    [Fact]
    public void ASessionWithNoHeroOrCameraIsWrappedWithoutTouchingThePets()
    {
        var inner = new BareSession();
        inner.Sprites.Add(Extra(1, 1));
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        var scene = new TownPetScene(inner, rig.Field);              // 造的时候不会因为没有主角而抛异常

        scene.Update(16, 0, 1);
        Assert.Equal(1, inner.Updates);                              // 里面照样被推进
        Assert.Same(inner.Sprites, scene.Build(0));                  // 没有主角没有镜头:不出宠物
        Assert.Equal((0, 0), (rig.Field.Spawned, rig.Field.Despawned));
        Assert.False(scene.TryMaster(out _));
        Assert.False(scene.TryProjection(out _));
    }

    // ------------------------------------------------------------ 换区域

    [Fact]
    public void ANewSessionPutsTheExistingEntityNextToTheHeroWithoutChasingOrRespawning()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);

        TownHero first = NewHero(assets, 100, 100);
        var scene1 = new TownPetScene(new FakeTown(first, new TownCamera(0, 96, first.X)), rig.Field);
        scene1.Update(16, 0, 0);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Equal(100.0 + PetTownKit.FullSpan, entity.X);

        // 走到另一块区域:主角在 700,朝左——新会话一造好,宠物就落在他右边 41 的地方,不是从原来的位置追过去
        TownHero second = NewHero(assets, 700, 150, left: true);
        var scene2 = new TownPetScene(new FakeTown(second, new TownCamera(0, 96, second.X)), rig.Field);
        Assert.Same(entity, Assert.Single(rig.Field.Entities));
        Assert.Equal((741.0, 150.0, true), (entity.X, entity.Y, entity.FacingLeft));
        Assert.Equal((PetFollowPhase.Idle, 0, 1), (entity.Last.Phase, entity.Follower.Teleports, rig.Field.Spawned));
        Assert.Same(scene2.Pets, scene1.Pets);
    }

    // ------------------------------------------------------------ 世界装饰器

    [Fact]
    public void TheWrappedWorldWrapsEverySessionItCreatesAndAnnouncesTownToThePets()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 100);
        var session = new FakeTown(hero, new TownCamera(0, 96, hero.X));
        var inner = new FakeWorld { Make = _ => session };
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull, fighting: true);
        Assert.Equal(PetState.Draining, pet.State);                  // 刚从副本回来:消耗中

        var world = new PetTownWorld(inner, rig.Field);
        var place = new TownPlace(5, 6);
        IHeroSession created = world.Create(place);
        var scene = Assert.IsType<TownPetScene>(created);
        Assert.Same(session, scene.Inner);
        Assert.Same(rig.Field, scene.Pets);
        Assert.Equal(new[] { place }, inner.Created.ToArray());
        Assert.Equal(PetState.Charging, pet.State);                  // 进城:回补
    }

    [Fact]
    public void TheWrappedWorldForwardsEverythingElseToTheInnerWorld()
    {
        var splash = new SplashScene("name", null, null, null, 0, 0, 1000);
        var exit = new TownExit(new WorldRect(1, 2, 3, 4), 7, 8);
        var inner = new FakeWorld
        {
            Make = _ => null,
            SplashResult = splash,
            ThroughResult = new TownPlace(9, 9),
            RouteResult = true,
            RouteExit = exit,
        };
        var rig = new PetTownKit.Rig();
        var world = new PetTownWorld(inner, rig.Field);

        Assert.Equal((new TownPlace(1, 2), new TownPlace(3, 4)), (world.Start, world.Return));
        Assert.Same(inner, world.Inner);
        Assert.Same(rig.Field, world.Pets);

        Assert.Same(splash, world.Splash(new TownPlace(1, 1), new TownPlace(2, 2)));
        Assert.Equal((new TownPlace(1, 1), new TownPlace(2, 2)), (inner.LastSplash.From.Value, inner.LastSplash.To));
        Assert.Same(splash, world.Splash(null, new TownPlace(2, 2)));
        Assert.Null(inner.LastSplash.From);

        Assert.Equal(new TownPlace(9, 9), world.Through(new TownPlace(1, 1), exit, true));
        Assert.Equal((new TownPlace(1, 1), true), (inner.LastThrough.From, inner.LastThrough.FacingLeft));
        Assert.Equal(7, inner.LastThrough.Exit.Town);

        Assert.True(world.TryRoute(new TownPlace(3, 3), out TownExit routed));
        Assert.Equal((new TownPlace(3, 3), 7, 8), (inner.LastRouteFrom, routed.Town, routed.Area));
        inner.RouteResult = false;
        Assert.False(world.TryRoute(new TownPlace(3, 3), out _));
        inner.ThroughResult = null;
        Assert.Null(world.Through(new TownPlace(1, 1), exit, false));
    }

    [Fact]
    public void AWorldThatMakesNoSessionGivesNoSessionAndDoesNotTouchThePets()
    {
        var inner = new FakeWorld { Make = _ => null };
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull, fighting: true);
        var world = new PetTownWorld(inner, rig.Field);

        Assert.Null(world.Create(new TownPlace(1, 1)));
        Assert.Equal(PetState.Draining, pet.State);                  // 没造出会话就没进城:状态不动
    }

    [Fact]
    public void UnwrapReturnsTheInnermostSessionOrWorldAndLeavesOthersAlone()
    {
        var bare = new BareSession();
        var rig = new PetTownKit.Rig();
        var once = new TownPetScene(bare, rig.Field);
        var twice = new TownPetScene(once, rig.Field);
        Assert.Same(bare, TownPetScene.Unwrap(twice));
        Assert.Same(bare, TownPetScene.Unwrap(once));
        Assert.Same(bare, TownPetScene.Unwrap(bare));
        Assert.Null(TownPetScene.Unwrap(null));

        var plain = new FakeWorld { Make = _ => null };
        var wrapped = new PetTownWorld(new PetTownWorld(plain, rig.Field), rig.Field);
        Assert.Same(plain, PetTownWorld.Unwrap(wrapped));
        Assert.Same(plain, PetTownWorld.Unwrap(plain));
        Assert.Null(PetTownWorld.Unwrap(null));
    }

    [Fact]
    public void TheDecoratorsNeedTheirParts()
    {
        var rig = new PetTownKit.Rig();
        Assert.Throws<ArgumentNullException>(() => new TownPetScene(null, rig.Field));
        Assert.Throws<ArgumentNullException>(() => new TownPetScene(new BareSession(), null));
        Assert.Throws<ArgumentNullException>(() => new PetTownWorld(null, rig.Field));
        Assert.Throws<ArgumentNullException>(() => new PetTownWorld(new FakeWorld(), null));
    }

    // ------------------------------------------------------------ 与真的 TownSession 配合

    [Fact]
    public void ARealTownSessionKeepsItsHeroDrawingAndItsOwnReportsWhenWrapped()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, 300, 100);
        var session = new TownSession(assets, AvatarFixture.AreaMap, hero, new TownCamera(0, 96, hero.X));
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        var scene = new TownPetScene(session, rig.Field);

        scene.Update(1000, 1, 0);
        List<SceneSprite> drawn = scene.Build(0);
        Assert.Contains(drawn, s => s.NpcId == TownHero.ObjectId);                       // 主角还在
        Assert.Contains(drawn, s => s.NpcId == TownPetField.ObjectIdBase + rig.Session.Equipped.Uid);   // 宠物在
        Assert.Equal(session.Build(0).Count + 1, drawn.Count);                           // 只多了宠物这一个精灵

        Assert.Equal((session.HeroX, session.HeroY, session.HeroFacingLeft), (scene.HeroX, scene.HeroY, scene.HeroFacingLeft));
        Assert.Equal((session.NpcCount, session.LayerCount, session.MapPath), (scene.NpcCount, scene.LayerCount, scene.MapPath));
        Assert.Same(session.Npcs, scene.Npcs);
        Assert.Equal(session.MapSounds.Count, scene.MapSounds.Count);
        Assert.Equal(session.PendingExit, scene.PendingExit);
        Assert.Same(session, TownPetScene.Unwrap(scene));                                // 宿主要认 TownSession 的地方用它取回
    }
}
