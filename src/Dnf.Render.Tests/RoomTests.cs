using Dnf.Data;

namespace Dnf.Render.Tests;

public class RoomTests
{
    private static RoomMap ReadMap(DnfAssets assets)
    {
        return RoomMap.Read(assets, RoomFixture.MapPath);
    }

    /// <summary>n 块一样的瓦片拼成的房间(通行表同 <see cref="RoomFixture.PassTable"/>),不经过 PVF。</summary>
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

    private static TownHero RoomHero(DnfAssets avatarAssets, IWalkArea walk, double x, double y)
    {
        AvatarClass cls = AvatarClass.FromChr(avatarAssets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(avatarAssets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(avatarAssets, AvatarFixture.ChrPath, outfit, walk, x, y, false, TownHero.DungeonRestTag);
    }

    [Fact]
    public void TheRoomScriptIsReadIntoTilesScrollsBackgroundsGatesAndPlacements()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);

        Assert.Equal("[normal]", map.Type);
        Assert.Equal(2, map.Tiles.Count);
        Assert.Equal(448, map.Width);
        Assert.Equal(0, map.MaxCameraX);              // 房间比一屏窄:镜头不动
        Assert.Equal((50, 80, 120), (map.FarScroll, map.MiddleScroll, map.NearScroll));
        Assert.Equal(new[] { "map/test/room/animation/far.ani", "map/test/room/animation/mid.ani" }, map.Backgrounds.Select(b => b.AnimationPath).ToArray());
        Assert.Equal(new[] { "[distantback]", "[middleback]" }, map.Backgrounds.Select(b => b.Layer).ToArray());
        Assert.Equal(new[] { 16, 120 }, map.Gates[(int)GateSide.Left]);
        Assert.Equal(new[] { 440, 130 }, map.Gates[(int)GateSide.Right]);
        Assert.Equal(new[] { 220, 60 }, map.Gates[(int)GateSide.Up]);
        Assert.Equal(new[] { 220, 190 }, map.Gates[(int)GateSide.Down]);
        WorldRect start = map.StartArea!.Value;
        Assert.Equal((300, 100, 60, 40), (start.X, start.Y, start.Width, start.Height));
        Assert.Single(map.Animations);
        Assert.Equal(new[] { (RoomFixture.TreeId, 200, 40, 0), (RoomFixture.ParticleId, 150, 150, 0), (999, 10, 10, 0) },
            map.PassiveObjects.Select(p => (p.Id, p.X, p.Y, p.Z)).ToArray());
        Assert.Equal(80, map.Tiles[0].ImagePosY);     // 没写 [img pos]:默认 80
        Assert.Equal(90, map.Tiles[1].ImagePosY);
        Assert.Equal(new[] { new KeyValuePair<string, int>("sprite/map/test/tile.img", 1) }, map.Tiles[1].Images.ToArray());
        Assert.Throws<DnfFormatException>(() => RoomMap.Read(assets, "map/test/missing.map"));
    }

    [Fact]
    public void TheMonsterListIsReadTenValuesAtATimeAndResynchronisesPastStrayValues()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);

        // 开头游离的 77 被跳过,两个完整的项被读出,末尾只有三个数的残项被丢弃
        Assert.Equal(2, map.Monsters.Count);
        RoomMonsterSpawn first = map.Monsters[0];
        Assert.Equal((1, 0, 1), (first.Code, first.FlagA, first.FlagB));
        Assert.Equal((300, 200, 0), (first.X, first.Y, first.Z));
        Assert.Equal((1, 0), (first.Grade, first.FlagC));
        Assert.Equal(("[fixed]", "[normal]"), (first.Placement, first.Class));
        Assert.False(first.IsBoss);

        RoomMonsterSpawn boss = map.Monsters[1];
        Assert.Equal((2, 1, 0), (boss.Code, boss.FlagA, boss.FlagB));
        Assert.Equal((420, 210, 5), (boss.X, boss.Y, boss.Z));
        Assert.Equal((2, 1), (boss.Grade, boss.FlagC));
        Assert.True(boss.IsBoss);

        // 城镇地图与没有 [monster] 的地图:没有怪物
        Assert.Empty(RoomMap.Read(assets, "map/test/room/town.map").Monsters);
    }

    [Fact]
    public void TheMaskIsCellsSixteenWideAndTwelveHighStartingAtTheOrigin()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);

        // 行 2 到 4 能走,即 y 在 [24, 60);行高 12 在边界上精确
        Assert.False(map.CanWalk(0, 23.9));
        Assert.True(map.CanWalk(0, 24));
        Assert.True(map.CanWalk(0, 59.9));
        Assert.False(map.CanWalk(0, 60));
        Assert.True(map.CanWalk(223.9, 30));          // 第一块瓦片的最后一列
        // 第二块瓦片的 (列 2, 行 3) 不能走:x 在 [256, 272),y 在 [36, 48)
        Assert.True(map.CanWalk(224 + 31.9, 40));
        Assert.False(map.CanWalk(224 + 32, 40));
        Assert.False(map.CanWalk(224 + 47.9, 40));
        Assert.True(map.CanWalk(224 + 48, 40));
        // 房间之外都不能走
        Assert.False(map.CanWalk(448, 40));
        Assert.False(map.CanWalk(-1, 40));
        Assert.False(map.CanWalk(10, -1));
        Assert.False(map.CanWalk(10, 600));
    }

    [Fact]
    public void NearestWalkableIsTheStraightLineNearestIntegerPoint()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);

        map.NearestWalkable(10, 5, out double x, out double y);
        Assert.Equal((10.0, 24.0), (x, y));           // 正下方 19 像素
        map.NearestWalkable(100.5, 30.25, out x, out y);
        Assert.Equal((100.5, 30.25), (x, y));         // 本身能走:原样
        map.NearestWalkable(500, 40, out x, out y);
        Assert.Equal((447.0, 40.0), (x, y));          // 房间右边之外
        map.NearestWalkable(10, 70, out x, out y);
        Assert.Equal((10.0, 59.0), (x, y));           // 下边之外:最后一个能走的整数 y
    }

    [Fact]
    public void BackgroundsScrollByTheirPercentAndTileHorizontallyByTheCanvasWidth()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);
        RoomContent content = RoomContent.Load(assets, map);
        List<SceneSprite> sprites = RoomScene.Build(assets, content, 100, 200, 0, null);

        // 远景:系数 50 → 位移 50;左上角 x = −50 + 帧偏移 2 + 帧头 3 = −45,重复周期 100;−45 那一块整个在屏幕左边,不画
        List<SceneSprite> far = sprites.Where(s => s.AnimationPath == "map/test/room/animation/far.ani").ToList();
        Assert.Equal(Enumerable.Range(0, 8).Select(k => 55 + 100 * k).ToArray(), far.Select(s => s.ScreenX).ToArray());
        Assert.All(far, s => Assert.Equal(80 - 3 + 4, s.ScreenY));
        Assert.All(far, s => Assert.Equal(0, s.LayerRank));

        // 中景:系数 80 → 位移 80;x = −80 + 0 + 3 = −77 → 23, 123, …
        List<SceneSprite> mid = sprites.Where(s => s.AnimationPath == "map/test/room/animation/mid.ani").ToList();
        Assert.Equal(Enumerable.Range(0, 8).Select(k => 23 + 100 * k).ToArray(), mid.Select(s => s.ScreenX).ToArray());
        Assert.All(mid, s => Assert.Equal(80 + 4, s.ScreenY));
        Assert.All(mid, s => Assert.Equal(1, s.LayerRank));

        // 镜头 0:不位移,−0 + 2 + 3 = 5 起
        List<SceneSprite> atZero = RoomScene.Build(assets, content, 0, 200, 0, null)
            .Where(s => s.AnimationPath == "map/test/room/animation/far.ani").ToList();
        Assert.Equal(5, atZero[0].ScreenX);
        Assert.Equal(8, atZero.Count);
    }

    [Fact]
    public void TilesSitEvery224PixelsAtTheirOwnImagePosition()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);
        List<SceneSprite> tiles = RoomScene.TileSprites(assets, map, 100);

        Assert.Equal(2, tiles.Count);
        Assert.Equal((-100 + 1, 80 + 2, 0), (tiles[0].ScreenX, tiles[0].ScreenY, tiles[0].AniFrame));
        Assert.Equal((224 - 100 + 0, 90 + 0, 1), (tiles[1].ScreenX, tiles[1].ScreenY, tiles[1].AniFrame));
        Assert.All(tiles, t => Assert.Equal(RoomScene.TileRank, t.LayerRank));
        // 镜头在第二块的右边:第一块整个在屏幕外,不给
        Assert.Single(RoomScene.TileSprites(assets, map, 300));
        Assert.Equal("map/test/room/tile/t1.til", RoomScene.TileSprites(assets, map, 300)[0].AnimationPath);
    }

    [Fact]
    public void LayersObstaclesAndGatesShareTheNormalRankAndAreSortedByWorldY()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);
        PathGateSet set = PathGateSet.Read(assets, RoomFixture.DungeonPath)!;
        var slice = new RoomSlice(false, 0, 0, false, RoomFixture.DungeonPath, GateKind.Normal, GateKind.Wall, GateKind.None, GateKind.Normal);
        RoomContent content = RoomContent.Load(assets, map, DemoRoom.BuildGates(assets, map, slice));
        List<SceneSprite> sprites = RoomScene.Build(assets, content, 100, 200, 0, null);

        // 障碍物:世界 (200, 40),帧头偏移 (-5, -18) → x = 200 − 100 − 5,y = 40 + 200 − 18
        SceneSprite tree = sprites.Single(s => s.AnimationPath == "map/test/animation/tree.ani");
        Assert.Equal((95, 222, 40, TownScene.NormalRank), (tree.ScreenX, tree.ScreenY, tree.SortKey, tree.LayerRank));
        // 地图的 [animation] 图层:世界 (100, 100),帧头偏移 (1, 2)
        SceneSprite glow = sprites.Single(s => s.AnimationPath == "map/test/room/animation/glow.ani");
        Assert.Equal((100 - 100 + 1, 100 + 200 + 2, 100), (glow.ScreenX, glow.ScreenY, glow.SortKey));
        // 门框与关着的门:位置 (16, 120);帧偏移 (0,0) 与 (1,1)
        SceneSprite frame = sprites.Single(s => s.AnimationPath.EndsWith("/frame.ani", StringComparison.Ordinal));
        SceneSprite door = sprites.Single(s => s.AnimationPath.EndsWith("/door.ani", StringComparison.Ordinal));
        Assert.Equal((16 - 100, 120 + 200, 120), (frame.ScreenX, frame.ScreenY, frame.SortKey));
        Assert.Equal((16 - 100 + 1, 120 + 200 + 1), (door.ScreenX, door.ScreenY));
        Assert.DoesNotContain(sprites, s => s.AnimationPath.EndsWith("/open.ani", StringComparison.Ordinal));

        // normal 大类按世界 y 排:树(40) < 图层(100) < 门(120);三个大类都在背景与瓦片之后
        int[] order = sprites.Where(s => s.LayerRank == TownScene.NormalRank).Select(s => s.SortKey).ToArray();
        Assert.Equal(order.OrderBy(v => v).ToArray(), order);
        Assert.True(sprites.FindIndex(s => s.LayerRank == TownScene.NormalRank) > sprites.FindLastIndex(s => s.LayerRank < TownScene.NormalRank));
        Assert.Equal(new[] { 801, 802, 803, 804, 805, 806, 807, 808, 809, 810 }, set.Ids.ToArray());
    }

    [Fact]
    public void AClosedGateDrawsItsFrameAndDoorAndAnOpenOneItsFrameAndLight()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);
        PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
        GateObject obj = GateObject.Read(assets, catalog, RoomFixture.LeftNormalId)!;
        Assert.Equal(new[] { (0, 1), (1, 1), (2, 1) }, obj.Parts.Select(p => (p.Kind, p.Variant)).ToArray());
        Assert.Null(obj.DoorTilePath);

        var gate = new RoomGate(assets, obj, GateSide.Left, 16, 120);
        var projection = new TownProjection(100, 200);
        List<SceneSprite> closed = gate.Sprites(assets, projection, 500);
        Assert.Equal(2, closed.Count);                // 门框、关着的门;光(种类 2)不画
        Assert.EndsWith("/frame.ani", closed[0].AnimationPath);
        Assert.EndsWith("/door.ani", closed[1].AnimationPath);
        Assert.Equal(GateState.Closed, gate.State);

        gate.Open(500);
        List<SceneSprite> open = gate.Sprites(assets, projection, 700);
        Assert.Equal(2, open.Count);
        Assert.EndsWith("/frame.ani", open[0].AnimationPath);
        Assert.EndsWith("/open.ani", open[1].AnimationPath);
        // 光的帧偏移 (2,2)
        Assert.Equal((16 - 100 + 2, 120 + 200 + 2), (open[1].ScreenX, open[1].ScreenY));
        gate.Open(900);                               // 已经开着:不重新计时
        Assert.Equal(500, gate.OpenedAtMs);
        gate.Close();
        Assert.Equal(GateState.Closed, gate.State);
        Assert.Equal("map/test/gate01.img", GateObject.FormatImageName("Map/Test/Gate%d%d.img", 1));
        Assert.Equal("map/test/gate05.img", GateObject.FormatImageName("Map/Test\\Gate%d%d.img", 5));
        Assert.Equal("map/test/plain.img", GateObject.FormatImageName("Map/Test/Plain.img", 5));
        Assert.Null(GateObject.Read(assets, catalog, 12345));
        _ = map;
    }

    [Fact]
    public void PathGateSetPicksTheTenIdsInTheDocumentedOrder()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        PathGateSet set = PathGateSet.Read(assets, RoomFixture.DungeonPath)!;
        Assert.Equal((801, 804, 807, 809), (set.IdFor(GateSide.Left, GateKind.Normal), set.IdFor(GateSide.Right, GateKind.Normal),
            set.IdFor(GateSide.Up, GateKind.Normal), set.IdFor(GateSide.Down, GateKind.Normal)));
        Assert.Equal((802, 805, 808, 810), (set.IdFor(GateSide.Left, GateKind.Boss), set.IdFor(GateSide.Right, GateKind.Boss),
            set.IdFor(GateSide.Up, GateKind.Boss), set.IdFor(GateSide.Down, GateKind.Boss)));
        Assert.Equal((803, 806, -1, -1), (set.IdFor(GateSide.Left, GateKind.Wall), set.IdFor(GateSide.Right, GateKind.Wall),
            set.IdFor(GateSide.Up, GateKind.Wall), set.IdFor(GateSide.Down, GateKind.Wall)));
        Assert.Equal(-1, set.IdFor(GateSide.Left, GateKind.None));
        Assert.Null(PathGateSet.Read(assets, "dungeon/test/missing.dgn"));
        Assert.Throws<ArgumentException>(() => new PathGateSet(new[] { 1, 2, 3 }));
    }

    [Fact]
    public void DemoGatesSkipSidesWithoutAnObjectOrAPosition()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);
        // 左边普通门(目录里有)、右边墙(806 不在目录里)、下边普通门(809 不在目录里)、上边没有
        var slice = new RoomSlice(false, 0, 0, false, RoomFixture.DungeonPath, GateKind.Normal, GateKind.Wall, GateKind.None, GateKind.Normal);
        List<RoomGate> gates = DemoRoom.BuildGates(assets, map, slice);
        Assert.Single(gates);
        Assert.Equal((GateSide.Left, 16, 120), (gates[0].Side, gates[0].X, gates[0].Y));
        // 没有副本定义:没有出口
        var none = new RoomSlice(false, 0, 0, false, "dungeon/test/missing.dgn", GateKind.Normal, GateKind.Normal, GateKind.Normal, GateKind.Normal);
        Assert.Empty(DemoRoom.BuildGates(assets, map, none));
        Assert.Equal(GateKind.Wall, slice.KindOf(GateSide.Right));
        Assert.Equal(GateKind.None, slice.KindOf(GateSide.Up));
    }

    [Fact]
    public void OnlyObstaclesWithAMotionAreDrawnAndOnlyDoNotPassOnesBlock()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);
        RoomContent content = RoomContent.Load(assets, map);
        // 摆放了树(700)、粒子光源(5,没有 [basic motion])与一个目录里没有的编号(999)
        Assert.Single(content.Objects);
        RoomObject tree = content.Objects[0];
        Assert.Equal((RoomFixture.TreeId, 40, 20, true), (tree.Def.Id, tree.Def.FootprintWidth, tree.Def.FootprintDepth, tree.Def.Blocking));
        Assert.Equal("map/test/animation/tree.ani", tree.Def.MotionPath);

        // 占地:以 (200, 40) 为中心的 40 × 20,边界不算在里面
        Assert.True(tree.Blocks(190, 45));
        Assert.True(tree.Blocks(200, 49.9));
        Assert.False(tree.Blocks(200, 50));
        Assert.False(tree.Blocks(180, 40));
        Assert.False(tree.Blocks(220, 40));
        var walk = new RoomWalkArea(map, content.Objects);
        Assert.False(walk.Contains(190, 45));          // 通行表上能走,但被树挡住
        Assert.True(walk.Contains(150, 45));
        Assert.False(walk.Contains(150, 100));         // 通行表上不能走
        Assert.True(new RoomWalkArea(map).Contains(190, 45));
        Assert.False(walk.IsUnrestricted);
    }

    [Fact]
    public void TheHeroStopsAtTheMaskBoundaryAndAtObstacles()
    {
        using var avatar = new AvatarFixture();
        using DnfAssets avatarAssets = DnfAssets.Open(avatar.Root);
        using var rooms = new RoomFixture();
        using DnfAssets roomAssets = DnfAssets.Open(rooms.Root);
        RoomMap map = ReadMap(roomAssets);
        RoomContent content = RoomContent.Load(roomAssets, map);
        var walk = new RoomWalkArea(map, content.Objects);

        // 向下走很久:停在通行带的下沿(y < 60),向上停在上沿(y ≥ 24)
        TownHero hero = RoomHero(avatarAssets, walk, 50, 40);
        hero.Update(10000, 0, 1);
        Assert.InRange(hero.Y, 59.99, 60.0);
        Assert.Equal(50, hero.X, 6);
        hero.Update(10000, 0, -1);
        Assert.InRange(hero.Y, 24.0, 24.01);

        // 向右走到树(占地 x 从 180 起):停在 180 之前
        TownHero toTree = RoomHero(avatarAssets, walk, 150, 40);
        toTree.Update(2000, 1, 0);
        Assert.InRange(toTree.X, 179.99, 180.0);
        // 同一个 x 往右走,只要 y 在树的占地之外(y = 55)就能穿过
        TownHero below = RoomHero(avatarAssets, walk, 150, 55);
        below.Update(2000, 1, 0);
        Assert.True(below.X > 300);
        // 第二块瓦片里 (列 2, 行 3) 是一块挡住的格子:从 y = 40 向右走,在 x = 256 之前停下
        TownHero toBlock = RoomHero(avatarAssets, walk, 230, 40);
        toBlock.Update(2000, 1, 0);
        Assert.InRange(toBlock.X, 255.99, 256.0);
    }

    [Fact]
    public void TheDungeonRestMotionIsTheWaitingMotionNotTheTownOne()
    {
        using var avatar = new AvatarFixture();
        using DnfAssets avatarAssets = DnfAssets.Open(avatar.Root);
        TownHero hero = RoomHero(avatarAssets, new RectUnionArea(new List<WorldRect>()), 0, 0);
        Assert.Equal(AvatarFixture.WaitAni, hero.CurrentAnimationPath);
        AvatarClass cls = AvatarClass.FromChr(avatarAssets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(avatarAssets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero town = TownHero.Create(avatarAssets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), 0, 0, false);
        Assert.Equal(AvatarFixture.RestAni, town.CurrentAnimationPath);
    }

    [Fact]
    public void ARoomSessionFollowsTheHeroWithTheDeadZoneCameraInsideTheRoom()
    {
        using var avatar = new AvatarFixture();
        using DnfAssets avatarAssets = DnfAssets.Open(avatar.Root);
        RoomMap map = Corridor(5);                    // 宽 1120:镜头 x 在 [0, 320]
        Assert.Equal(320, map.MaxCameraX);
        var content = new RoomContent(map, null, null, null, null);
        TownHero hero = RoomHero(avatarAssets, new RoomWalkArea(map), 600, 40);
        var camera = new TownCamera(0, map.MaxCameraX, hero.X);
        var session = new RoomSession(avatarAssets, content, hero, camera);
        Assert.Equal((0, 0), (session.NpcCount, session.LayerCount));
        Assert.Equal(200, session.Camera.IntX);        // 居中 600 − 400

        // 向右走 1 秒:世界 x 600 → 822.5,屏幕 622.5 > 450 → 镜头 372.5,夹到 320
        session.Update(1000, 1, 0);
        Assert.Equal(600 + TownHero.DefaultWalkSpeedX, hero.X, 6);
        Assert.Equal(320, session.Camera.X, 6);
        Assert.Equal(320, session.Projection.CameraX);
        // 向左走 1 秒:世界 x 822.5 → 600,屏幕 280 < 350 → 镜头 250
        session.Update(1000, -1, 0);
        Assert.Equal(250, session.Camera.X, 6);

        List<SceneSprite> withHero = session.Build(0);
        Assert.NotEmpty(withHero);
        Assert.All(withHero, s => Assert.Equal(TownHero.ObjectId, s.NpcId));
        session.HeroVisible = false;
        Assert.Empty(session.Build(0));
        Assert.Equal("map/test/corridor.map", session.MapPath);
    }

    [Fact]
    public void TheDefaultSpawnIsTheStartAreaCenterMovedToTheNearestWalkablePoint()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = ReadMap(assets);
        // 出生区 (300, 100, 60, 40) 的中心是 (330, 120),在通行带之外:挪到上方 60 − 1 = 59(最后一个能走的整数 y)
        DemoRoom.DefaultSpawn(map, out double x, out double y);
        Assert.Equal((330.0, 59.0), (x, y));
        // 没有出生区:房间中央、y = 240 附近 → 同样挪回通行带
        RoomMap noStart = Corridor(5);
        DemoRoom.DefaultSpawn(noStart, out x, out y);
        Assert.Equal((560.0, 59.0), (x, y));
    }

    [Fact]
    public void OnlyTheMeasuredLorienRoomsHaveADemoLayout()
    {
        Assert.True(DemoRoom.TryGetSlice("map/lorien/s2.map", out RoomSlice first));
        Assert.True(first.HasSpawn);
        Assert.Equal((876.0, 342.0, false), (first.SpawnX, first.SpawnY, first.SpawnFacingLeft));
        Assert.Equal((GateKind.Wall, GateKind.Normal, GateKind.None, GateKind.Normal), (first.Left, first.Right, first.Up, first.Down));
        Assert.True(DemoRoom.TryGetSlice("MAP/Lorien/S3.map", out RoomSlice second));
        Assert.False(second.HasSpawn);
        Assert.Equal((GateKind.Normal, GateKind.Wall), (second.Left, second.Right));
        Assert.False(DemoRoom.TryGetSlice("map/lorien/s0.map", out _));
    }

    [Fact]
    public void RoomMapDetectionLooksForTheGatePositionsNotTheTileList()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.True(DemoRoom.IsRoomMap(assets, RoomFixture.MapPath));
        Assert.False(DemoRoom.IsRoomMap(assets, "map/test/room/town.map"));    // 城镇地图也有 [tile]
        Assert.False(DemoRoom.IsRoomMap(assets, "map/test/missing.map"));
        Assert.False(DemoRoom.IsRoomMap(assets, RoomFixture.DungeonPath));
    }

    [Fact]
    public void ASolidCellIsAnyNonZeroPassValueAndCellsOutsideTheTableAreNotSolid()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TileFile tile = TileFile.Read(assets, RoomFixture.DoorBodyTile)!;
        Assert.Equal(6, tile.Rows);
        Assert.True(tile.IsSolid(10, 0));
        Assert.True(tile.IsSolid(13, 5));
        Assert.False(tile.IsSolid(9, 0));
        Assert.False(tile.IsSolid(0, 3));
        // 表外:不挡(与"不能走"相反,CanWalk 在表外是 false)
        Assert.False(tile.IsSolid(-1, 0));
        Assert.False(tile.IsSolid(14, 0));
        Assert.False(tile.IsSolid(10, 6));
        Assert.False(tile.IsSolid(10, -1));
        Assert.False(tile.CanWalk(-1, 0));
        Assert.False(tile.CanWalk(10, 0));
        Assert.True(tile.CanWalk(9, 0));

        // 通行值 1、5、6 这类也挡路
        var odd = new TileFile("odd", new List<KeyValuePair<string, int>>(), 80, new byte[] { 0, 1, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(new[] { false, true, true, true, false }, Enumerable.Range(0, 5).Select(c => odd.IsSolid(c, 0)).ToArray());
    }

    [Fact]
    public void AGateObjectEndingInATileReadsTheDoorAnchorAndTheGateGetsItsBodyFromIt()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
        GateObject obj = GateObject.Read(assets, catalog, RoomFixture.DoorBodyId)!;
        Assert.Equal(RoomFixture.DoorBodyTile, obj.DoorTilePath);
        Assert.Equal((13, 4), (obj.DoorAnchorColumn, obj.DoorAnchorRow));
        Assert.Equal("[bottom]", obj.Layer);
        Assert.Single(obj.Parts);                                        // .til 不是一段动画;门框的 (种类, 变体) 仍是第一对 (0, 1)
        Assert.Equal((0, 1), (obj.Parts[0].Kind, obj.Parts[0].Variant));

        // 没有 .til 的出口物件没有锚点也没有碰撞体
        GateObject plain = GateObject.Read(assets, catalog, RoomFixture.LeftNormalId)!;
        Assert.Equal((0, 0), (plain.DoorAnchorColumn, plain.DoorAnchorRow));
        Assert.Null(new RoomGate(assets, plain, GateSide.Left, 16, 120).Body);

        // 通行格左上角那一格 = (门 x / 16 − 列, 门 y / 12 − 行):先把门的位置换成格子再对齐(余数舍掉,证据 E-074)
        var gate = new RoomGate(assets, obj, GateSide.Right, 1104, 120);
        Assert.NotNull(gate.Body);
        Assert.Equal((1104 / 16 - 13, 120 / 12 - 4, GateSide.Right), (gate.Body!.OriginColumn, gate.Body.OriginRow, gate.Body.Side));
        var inexact = new RoomGate(assets, obj, GateSide.Left, 1119, 131);
        Assert.Equal((69 - 13, 10 - 4), (inexact.Body!.OriginColumn, inexact.Body.OriginRow));   // 1119 / 16 = 69、131 / 12 = 10,余数不进位

        // 构造函数里的锚点是可选的(默认 0、0)
        var manual = new GateObject(1, "x.obj", "[normal]", new List<GatePart>(), null);
        Assert.Equal((0, 0), (manual.DoorAnchorColumn, manual.DoorAnchorRow));
    }

    [Fact]
    public void TheGateBodyOverlapsTheSolidCellsThroughTheHeroFootprintAndExtendsToTheMapEdge()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TileFile tile = TileFile.Read(assets, RoomFixture.DoorBodyTile)!;
        // 门体表 14 列 × 6 行,第 10 到 13 列每行都挡路;左上角那一格是 (56, 6),所以挡路的格子是第 66 到 69 列(x ∈ [1056, 1120))、第 6 到 11 行(y ∈ [72, 144))
        var body = new GateBody(tile, 56, 6, GateSide.Right);
        // 占位盒 40 × 10:左右各 20、上下各 5,边界像素算在内;格子 = 像素 / 16、像素 / 12
        Assert.False(body.Overlaps(1035, 100, 40, 10));                  // 盒的右沿 1055 还在第 65 列
        Assert.True(body.Overlaps(1036, 100, 40, 10));                   // 右沿 1056 = 第 66 列的第一个像素:压着了
        Assert.True(body.Overlaps(1080, 100, 40, 10));
        Assert.True(body.Overlaps(1119, 100, 40, 10));
        Assert.True(body.Overlaps(1500, 100, 40, 10));                   // 右出口:第 13 列的值一直沿用到地图右缘
        Assert.True(body.Overlaps(1036.9, 100.9, 40, 10));               // 实数坐标先向下取整
        Assert.False(body.Overlaps(1035.9, 100.9, 40, 10));
        // 深度方向:盒 [y − 5, y + 5] 对第 6 到 11 行
        Assert.False(body.Overlaps(1080, 66, 40, 10));                   // 71 / 12 = 5 行
        Assert.True(body.Overlaps(1080, 67, 40, 10));                    // 72 / 12 = 6 行
        Assert.True(body.Overlaps(1080, 148, 40, 10));                   // 143 / 12 = 11 行
        Assert.False(body.Overlaps(1080, 149, 40, 10));                  // 144 / 12 = 12 行:表外
        // 表左边没有延伸(右出口)
        Assert.False(body.Overlaps(300, 100, 40, 10));
        // 盒的大小可换:宽 0、深 0 的盒只看一个格子
        Assert.False(body.Overlaps(1055, 100, 0, 0));
        Assert.True(body.Overlaps(1056, 100, 0, 0));
        Assert.Throws<ArgumentNullException>(() => new GateBody(null!, 0, 0, GateSide.Left));
    }

    [Fact]
    public void ALeftGateBodyReachesTheLeftMapEdgeWithColumnZeroAndNeverTheRight()
    {
        // 14 列 × 6 行:第 0、1、5 行整行挡路,第 2 到 4 行整行能走(RoomFixture.PassTable)
        var table = new TileFile("door", new List<KeyValuePair<string, int>>(), 80, RoomFixture.PassTable().Select(v => (byte)v).ToArray());
        var body = new GateBody(table, 3, 10, GateSide.Left);            // 表的左上角在第 3 列、第 10 行
        // 表内
        Assert.Equal(2, body.ValueAt(3, 10));
        Assert.Equal(0, body.ValueAt(3, 12));
        Assert.Equal(2, body.ValueAt(16, 15));
        // 表左边:左出口把第 0 列的值延伸到地图左缘(第 0 列起),只延伸表所在的行
        Assert.Equal(new byte[] { 2, 2, 2 }, new[] { body.ValueAt(0, 10), body.ValueAt(1, 11), body.ValueAt(2, 15) });
        Assert.Equal(new byte[] { 0, 0, 0 }, new[] { body.ValueAt(0, 12), body.ValueAt(1, 13), body.ValueAt(2, 14) });
        Assert.Equal(0, body.ValueAt(0, 9));                             // 表上方的行没有
        Assert.Equal(0, body.ValueAt(0, 16));                            // 表下方的行没有
        Assert.Equal(0, body.ValueAt(-1, 10));                           // 地图以外没有
        // 表右边:左出口不延伸
        Assert.Equal(0, body.ValueAt(17, 10));
        // 反过来,右出口的表左边不延伸
        Assert.Equal(0, new GateBody(table, 3, 10, GateSide.Right).ValueAt(0, 10));
        Assert.Equal(2, new GateBody(table, 3, 10, GateSide.Right).ValueAt(40, 10));   // 右出口:第 13 列的值(2)一直延伸出去
    }

    [Fact]
    public void GateBodiesReportTheOverlappedGateAndIgnoreGatesWithoutABody()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
        var withBody = new RoomGate(assets, GateObject.Read(assets, catalog, RoomFixture.DoorBodyId)!, GateSide.Right, 1104, 120);
        var plain = new RoomGate(assets, GateObject.Read(assets, catalog, RoomFixture.LeftNormalId)!, GateSide.Left, 16, 120);

        var bodies = new GateBodies(new RoomGate[] { plain, withBody, null });
        Assert.Equal(1, bodies.Count);
        Assert.Same(withBody, bodies.BlockedBy(1080, 100));
        Assert.Null(bodies.BlockedBy(300, 100));
        Assert.Equal(0, new GateBodies(null!).Count);
        Assert.Equal(0, new GateBodies(new[] { plain }).Count);
        Assert.Null(new GateBodies(new[] { plain }).BlockedBy(0, 0));
    }
}
