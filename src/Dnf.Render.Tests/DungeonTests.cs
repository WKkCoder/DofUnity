using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 副本迷宫与房间布局(证据 E-070):.dgn 的 [greed] 出口码(右 1、上 2、左 4、下 8,字母 = 'A' + 组合)、起点格与首领格的矩形、
/// 地图 [greed] 第一对字母 = 房间的出口组合的选图规则、出口种类的取法;以及战斗姿态与"直接开着"的出口。数据全是合成的,不含任何原版内容。
/// </summary>
public class DungeonTests
{
    private static ScriptLine Line(string tag, params object[] values)
    {
        var list = new List<ScriptValue>();
        foreach (object v in values)
        {
            list.Add(v is int i ? new ScriptValue(ScriptValueKind.Int, i, null) : new ScriptValue(ScriptValueKind.String, 0, (string)v));
        }
        return new ScriptLine(tag, list.ToArray());
    }

    /// <summary>
    /// 2 列 × 3 行的迷宫,出口码:
    ///   (0,0) J = 右 + 下   (1,0) E = 左
    ///   (0,1) L = 右 + 上 + 下   (1,1) M = 左 + 下
    ///   (0,2) D = 右 + 上   (1,2) G = 上 + 左
    /// 起点格矩形 (0,0)-(1,0),首领格矩形 (0,2)-(1,2)。
    /// </summary>
    private static ScriptLine[] MazeDoc(string greed = "JJEE LLMM DDGG", params ScriptLine[] extra)
    {
        var doc = new List<ScriptLine>
        {
            Line("[pathgate object]", 801, 802, 803, 804, 805, 806, 807, 808, 809, 810),
            Line("[maze info]"),
            Line("[size]", 2, 3),
            Line("[greed]", greed),
            Line("[start map]", 0, 0, 1, 0),
            Line("[/start map]"),
            Line("[boss map]", 0, 2, 1, 2),
            Line("[/boss map]"),
            Line("[/maze info]"),
        };
        doc.AddRange(extra);
        return doc.ToArray();
    }

    private static RoomCandidate Room(string name, int id, char code, bool boss = false, bool start = false, char second = '\0')
    {
        return new RoomCandidate("map/t/" + name + ".map", id, code - 'A', (second == '\0' ? code : second) - 'A', boss, start);
    }

    private static DungeonRoomPool Pool(bool spareD = false)
    {
        var rooms = new List<RoomCandidate>
        {
            Room("start_j", 1, 'J', start: true),
            Room("dead_e", 2, 'E', start: true),
            Room("dead_e2", 3, 'E'),
            Room("l", 4, 'L'),
            Room("m", 5, 'M'),
            Room("boss_d", 6, 'D', boss: true),
            Room("g", 7, 'G'),
            Room("boss_g", 8, 'G', boss: true),
        };
        if (spareD)
        {
            rooms.Add(Room("spare_d", 9, 'D'));            // 让 D 格在不当首领格时也有普通房间可用
        }
        return new DungeonRoomPool(rooms);
    }

    [Fact]
    public void ExitBitsAreRightUpLeftDownAndOppositesAndStepsAgree()
    {
        Assert.Equal((1, 2, 4, 8), (ExitBits.Of(GateSide.Right), ExitBits.Of(GateSide.Up), ExitBits.Of(GateSide.Left), ExitBits.Of(GateSide.Down)));
        Assert.Equal(GateSide.Right, ExitBits.Opposite(GateSide.Left));
        Assert.Equal(GateSide.Left, ExitBits.Opposite(GateSide.Right));
        Assert.Equal(GateSide.Down, ExitBits.Opposite(GateSide.Up));
        Assert.Equal(GateSide.Up, ExitBits.Opposite(GateSide.Down));
        ExitBits.Step(GateSide.Right, out int dx, out int dy);
        Assert.Equal((1, 0), (dx, dy));
        ExitBits.Step(GateSide.Up, out dx, out dy);
        Assert.Equal((0, -1), (dx, dy));                    // 第 0 行在最上面
        ExitBits.Step(GateSide.Left, out dx, out dy);
        Assert.Equal((-1, 0), (dx, dy));
        ExitBits.Step(GateSide.Down, out dx, out dy);
        Assert.Equal((0, 1), (dx, dy));
    }

    [Fact]
    public void TheMazeParsesLettersAsRowMajorCellsWithExitsInTheFirstLetterOfEachPair()
    {
        DungeonMaze maze = DungeonMaze.Parse("dungeon/t/t.dgn", MazeDoc())!;
        Assert.Equal((2, 3), (maze.Cols, maze.Rows));
        Assert.Equal(new[] { 9, 4, 11, 12, 3, 6 }, new[] { maze.ExitsAt(0, 0), maze.ExitsAt(1, 0), maze.ExitsAt(0, 1), maze.ExitsAt(1, 1), maze.ExitsAt(0, 2), maze.ExitsAt(1, 2) });
        Assert.True(maze.HasExit(0, 0, GateSide.Right));
        Assert.True(maze.HasExit(0, 0, GateSide.Down));
        Assert.False(maze.HasExit(0, 0, GateSide.Left));
        Assert.False(maze.HasExit(0, 0, GateSide.Up));
        Assert.True(maze.HasExit(0, 1, GateSide.Up));
        Assert.Equal(0, maze.ExitsAt(5, 5));                // 迷宫外没有出口
        Assert.False(maze.InBounds(2, 0));
        Assert.True(maze.IsConsistent());
        Assert.NotNull(maze.GateSet);
        Assert.Equal(803, maze.GateSet!.IdFor(GateSide.Left, GateKind.Wall));
    }

    [Fact]
    public void StartAndBossRectanglesAreInclusiveAndTakeFourNumbersEach()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc())!;
        Assert.True(maze.IsStartCell(0, 0));
        Assert.True(maze.IsStartCell(1, 0));
        Assert.False(maze.IsStartCell(0, 1));
        Assert.True(maze.IsBossCell(0, 2));
        Assert.True(maze.IsBossCell(1, 2));
        Assert.False(maze.IsBossCell(1, 1));
        // 两端颠倒的矩形也认;一个 [boss map] 可以写多个矩形
        var multi = DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", Line("[boss map]", 1, 1, 0, 1, 0, 0, 0, 0)))!;
        Assert.True(multi.IsBossCell(0, 1));
        Assert.True(multi.IsBossCell(1, 1));
        Assert.True(multi.IsBossCell(0, 0));
        Assert.False(multi.IsBossCell(1, 0));
        var rect = new CellRect(3, 4, 1, 2);
        Assert.True(rect.Contains(2, 3));
        Assert.False(rect.Contains(0, 3));
        Assert.Equal((1, 2, 3, 4), (rect.X0, rect.Y0, rect.X1, rect.Y1));
    }

    [Fact]
    public void NeighborsFollowTheExitsAndStayInsideTheMaze()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc())!;
        Assert.True(maze.TryNeighbor(0, 0, GateSide.Right, out int nx, out int ny));
        Assert.Equal((1, 0), (nx, ny));
        Assert.True(maze.TryNeighbor(0, 1, GateSide.Down, out nx, out ny));
        Assert.Equal((0, 2), (nx, ny));
        Assert.False(maze.TryNeighbor(0, 0, GateSide.Left, out _, out _));          // 没有出口
        Assert.False(maze.TryNeighbor(1, 0, GateSide.Down, out _, out _));          // 没有出口
        // 出口通向迷宫外:有出口但没有邻格
        var edge = DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]", "BB") })!;
        Assert.True(edge.HasExit(0, 0, GateSide.Right));
        Assert.False(edge.TryNeighbor(0, 0, GateSide.Right, out _, out _));
    }

    [Fact]
    public void AnInconsistentEdgeIsDetected()
    {
        // (1,2) 改成没有任何出口:(0,2) 的右出口与 (1,1) 的下出口都在边的另一侧落空
        Assert.False(DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDAA"))!.IsConsistent());
        // 只有一条边不一致也要查出来:(1,0) 多了一个下出口,(1,1) 没有上出口
        Assert.False(DungeonMaze.Parse("d", MazeDoc("JJMM LLMM DDGG"))!.IsConsistent());
        Assert.True(DungeonMaze.Parse("d", MazeDoc("jjee llmm ddgg"))!.IsConsistent());    // 小写也认
    }

    [Fact]
    public void ABadGreedOrAMissingSizeGivesNoMaze()
    {
        Assert.Null(DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDG")));                     // 字母数不对
        Assert.Null(DungeonMaze.Parse("d", new[] { Line("[greed]", "JJ") }));              // 没有 [size]
        Assert.Null(DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]") }));
        Assert.Null(DungeonMaze.Parse("d", null!));
        // [greed] 可以拆成几个字符串
        var split = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "JJ", "EE") })!;
        Assert.Equal((9, 4), (split.ExitsAt(0, 0), split.ExitsAt(1, 0)));
        // 出口物件编号个数不对时没有出口物件
        var noGates = DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]", "AA"), Line("[pathgate object]", 1, 2, 3) })!;
        Assert.Null(noGates.GateSet);
    }

    [Fact]
    public void TheSecondLetterOfEachPairIsKeptButNotUsedForExits()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "JBEA") })!;
        Assert.Equal((9, 4), (maze.ExitsAt(0, 0), maze.ExitsAt(1, 0)));
        Assert.Equal((1, 0), (maze.SecondCodeAt(0, 0), maze.SecondCodeAt(1, 0)));
    }

    [Fact]
    public void TheMapFolderIsTheDungeonFileNameInLowerCase()
    {
        Assert.Equal("lorien/", DungeonRoomPool.FolderFor("dungeon/act1/lorien.dgn"));
        Assert.Equal("x/", DungeonRoomPool.FolderFor("X.DGN"));
        Assert.Equal("c/", DungeonRoomPool.FolderFor("a/b/c"));
    }

    [Fact]
    public void ARoomHeaderGivesExitsSecondPairBossAndStartArea()
    {
        RoomCandidate a = DungeonRoomPool.ReadHeader(new[] { Line("[type]", "[normal]"), Line("[greed]", "JJ BB"), Line("[dungeon start area]", 1, 2, 3, 4) }, "map/t/a.map", 3)!;
        Assert.Equal(("map/t/a.map", 3, 9, 1, false, true), (a.MapPath, a.MapId, a.Exits, a.Second, a.IsBoss, a.HasStartArea));
        RoomCandidate b = DungeonRoomPool.ReadHeader(new[] { Line("[type]", "[boss]"), Line("[greed]", "DD") }, "map/t/b.map", 9)!;
        Assert.Equal((3, 3, true, false), (b.Exits, b.Second, b.IsBoss, b.HasStartArea));       // 只有一对:第二对取第一对
        Assert.Null(DungeonRoomPool.ReadHeader(new[] { Line("[type]", "[normal]") }, "map/t/c.map", 1));
        Assert.Null(DungeonRoomPool.ReadHeader(new[] { Line("[greed]", "J") }, "map/t/d.map", 1));  // 不足一对
        Assert.Null(DungeonRoomPool.ReadHeader(null!, "map/t/e.map", 1));
    }

    [Fact]
    public void FindFiltersByExitsAndRoleInListOrder()
    {
        DungeonRoomPool pool = Pool();
        Assert.Equal(new[] { 1 }, pool.Find(9, RoomRole.Start).Select(r => r.MapId).ToArray());
        Assert.Equal(new[] { 2 }, pool.Find(4, RoomRole.Start).Select(r => r.MapId).ToArray());                // 有出生区的才是起点候选
        Assert.Equal(new[] { 2, 3 }, pool.Find(4, RoomRole.Normal).Select(r => r.MapId).ToArray());            // 普通候选不要求出生区,按列表顺序
        Assert.Equal(new[] { 7 }, pool.Find(6, RoomRole.Normal).Select(r => r.MapId).ToArray());               // 首领图不是普通候选
        Assert.Equal(new[] { 8 }, pool.Find(6, RoomRole.Boss).Select(r => r.MapId).ToArray());
        Assert.Empty(pool.Find(15, RoomRole.Normal));
    }

    [Fact]
    public void WithoutARandomTheLayoutTakesTheFirstCandidateOfEveryCell()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc())!;
        DungeonLayout layout = DungeonLayout.Create(maze, Pool());
        Assert.Equal((0, 0, 0, 2, true), (layout.StartX, layout.StartY, layout.BossX, layout.BossY, layout.HasBoss));
        Assert.Equal("map/t/start_j.map", layout.RoomAt(0, 0)!.MapPath);
        Assert.Equal("map/t/dead_e.map", layout.RoomAt(1, 0)!.MapPath);     // 起点格之外的 E 格用普通候选里最前面的
        Assert.Equal("map/t/l.map", layout.RoomAt(0, 1)!.MapPath);
        Assert.Equal("map/t/m.map", layout.RoomAt(1, 1)!.MapPath);
        Assert.Equal("map/t/boss_d.map", layout.RoomAt(0, 2)!.MapPath);     // 首领矩形里先扫到 (0,2)
        Assert.Equal("map/t/g.map", layout.RoomAt(1, 2)!.MapPath);          // 没被选作首领格的 G 格用普通图,不是 boss_g
        Assert.Null(layout.RoomAt(5, 5));
        Assert.True(layout.IsBossCell(0, 2));
        Assert.False(layout.IsBossCell(1, 2));
    }

    [Fact]
    public void GateKindsComeFromTheExitsAndWhetherTheNeighborIsTheBossCell()
    {
        DungeonLayout layout = DungeonLayout.Create(DungeonMaze.Parse("d", MazeDoc())!, Pool());
        // 有出口:通向首领格的是首领出口,其余是普通出口
        Assert.Equal(GateKind.Normal, layout.GateKindAt(0, 0, GateSide.Right));
        Assert.Equal(GateKind.Normal, layout.GateKindAt(0, 0, GateSide.Down));
        Assert.Equal(GateKind.Boss, layout.GateKindAt(0, 1, GateSide.Down));
        Assert.Equal(GateKind.Boss, layout.GateKindAt(1, 2, GateSide.Left));
        Assert.Equal(GateKind.Normal, layout.GateKindAt(1, 2, GateSide.Up));
        // 没有出口:左右是墙,上下什么也没有
        Assert.Equal(GateKind.Wall, layout.GateKindAt(0, 0, GateSide.Left));
        Assert.Equal(GateKind.Wall, layout.GateKindAt(1, 0, GateSide.Right));
        Assert.Equal(GateKind.None, layout.GateKindAt(0, 0, GateSide.Up));
        Assert.Equal(GateKind.None, layout.GateKindAt(1, 0, GateSide.Down));
    }

    [Fact]
    public void ARandomLayoutIsReproducibleAndAlwaysPicksRoomsWithTheRightExitsAndRoles()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc())!;
        var seen = new HashSet<string>();
        for (int seed = 0; seed < 40; seed++)
        {
            DungeonLayout a = DungeonLayout.Create(maze, Pool(spareD: true), new Random(seed));
            DungeonLayout b = DungeonLayout.Create(maze, Pool(spareD: true), new Random(seed));
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    RoomCandidate room = a.RoomAt(x, y)!;
                    Assert.Equal(room.MapPath, b.RoomAt(x, y)!.MapPath);
                    Assert.Equal(maze.ExitsAt(x, y), room.Exits);
                    Assert.Equal(a.IsBossCell(x, y), room.IsBoss);
                    if (x == a.StartX && y == a.StartY)
                    {
                        Assert.True(room.HasStartArea);
                    }
                }
            }
            Assert.True(maze.IsStartCell(a.StartX, a.StartY));
            Assert.True(maze.IsBossCell(a.BossX, a.BossY));
            seen.Add(a.StartX + "," + a.BossX + "," + a.RoomAt(1, 0)!.MapId + "," + a.RoomAt(1, 2)!.MapId);
        }
        Assert.True(seen.Count > 3);                                                    // 不同种子确实会选出不同的布局
    }

    [Fact]
    public void TheRandomChoiceNeverLeavesAnotherCellWithoutACandidate()
    {
        // 池里 D 只有首领图:首领矩形里的 (1,2) 当首领格会让 (0,2) 没有普通房间可用,所以随机时也只会选 (0,2) 当首领格
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc())!;
        for (int seed = 0; seed < 60; seed++)
        {
            DungeonLayout layout = DungeonLayout.Create(maze, Pool(), new Random(seed));
            Assert.Equal((0, 2), (layout.BossX, layout.BossY));
            Assert.Equal("map/t/boss_d.map", layout.RoomAt(0, 2)!.MapPath);
        }
        // 起点图同时是合格的普通图:去掉 dead_e2 后 E 只剩一张图,不论起点选 (0,0) 还是 (1,0),另一格都有普通候选可用
        var onlyJ = new DungeonRoomPool(Pool().Rooms.Where(r => r.MapId != 3).ToList());
        for (int seed = 0; seed < 30; seed++)
        {
            DungeonLayout layout = DungeonLayout.Create(maze, onlyJ, new Random(seed));
            Assert.NotNull(layout.RoomAt(0, 0));
            Assert.NotNull(layout.RoomAt(1, 0));
        }
    }

    [Fact]
    public void ACellWithoutAnyCandidateOrWithoutAStartRoomIsAnError()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc())!;
        var noL = new DungeonRoomPool(Pool().Rooms.Where(r => r.MapId != 4).ToList());
        Assert.Throws<DnfFormatException>(() => DungeonLayout.Create(maze, noL));
        var noStart = new DungeonRoomPool(Pool().Rooms.Where(r => r.MapId != 1 && r.MapId != 2).ToList());
        Assert.Throws<DnfFormatException>(() => DungeonLayout.Create(maze, noStart));
        Assert.Throws<ArgumentNullException>(() => DungeonLayout.Create(null!, Pool()));
        Assert.Throws<ArgumentNullException>(() => DungeonLayout.Create(maze, null!));
    }

    [Fact]
    public void WithoutABossRectangleThereIsNoBossAndAnEmptyStartRectangleMeansAnyCell()
    {
        var doc = new[] { Line("[size]", 2, 1), Line("[greed]", "JJEE") };
        DungeonLayout layout = DungeonLayout.Create(DungeonMaze.Parse("d", doc)!, Pool());
        Assert.False(layout.HasBoss);
        Assert.Equal((-1, -1), (layout.BossX, layout.BossY));
        Assert.Equal((0, 0), (layout.StartX, layout.StartY));                           // 没写起点矩形:扫到的第一个有起点候选的格
        Assert.Equal(GateKind.Normal, layout.GateKindAt(0, 0, GateSide.Right));         // 没有首领格:出口都是普通出口
    }

    [Fact]
    public void ARoomOutsideTheMazeExitIsANormalGate()
    {
        var doc = new[] { Line("[size]", 1, 1), Line("[greed]", "BB") };
        var pool = new DungeonRoomPool(new[] { Room("only", 1, 'B', start: true) });
        DungeonLayout layout = DungeonLayout.Create(DungeonMaze.Parse("d", doc)!, pool);
        Assert.Equal(GateKind.Normal, layout.GateKindAt(0, 0, GateSide.Right));         // 通向迷宫外
        Assert.Equal(GateKind.Wall, layout.GateKindAt(0, 0, GateSide.Left));
    }

    [Fact]
    public void ThePoolAndTheMazeReadFromAnActualScriptPackage()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        DungeonMaze maze = DungeonMaze.Read(assets, RoomFixture.DungeonPath)!;
        Assert.Equal((1, 1, 9), (maze.Cols, maze.Rows, maze.ExitsAt(0, 0)));
        Assert.True(maze.IsStartCell(0, 0));
        Assert.Empty(maze.BossCells);
        Assert.Equal(804, maze.GateSet!.IdFor(GateSide.Right, GateKind.Normal));

        DungeonRoomPool pool = DungeonRoomPool.Read(assets, DungeonRoomPool.FolderFor(RoomFixture.DungeonPath));
        // 目录 "test/" 下:a.map 有 [greed] 是候选;town.map 没有 [greed] 被跳过;Other/x.map 在别的目录
        RoomCandidate room = Assert.Single(pool.Rooms);
        Assert.Equal((RoomFixture.MapPath, 1, 9, 1, false, true), (room.MapPath, room.MapId, room.Exits, room.Second, room.IsBoss, room.HasStartArea));

        DungeonLayout layout = DungeonLayout.Create(maze, pool);
        Assert.Equal((0, 0, false), (layout.StartX, layout.StartY, layout.HasBoss));
        Assert.Same(room, layout.RoomAt(0, 0));
        Assert.Null(DungeonMaze.Read(assets, "dungeon/test/missing.dgn"));

        // 没有 map/map.lst 的数据包:读不了房间目录
        using var avatar = new AvatarFixture();
        using DnfAssets other = DnfAssets.Open(avatar.Root);
        Assert.Throws<DnfFormatException>(() => DungeonRoomPool.Read(other, "test/"));
    }

    [Fact]
    public void TheBattleStanceSwitchesTheStandingAnimationAndRestartsIt()
    {
        using var avatar = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(avatar.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero hero = TownHero.Create(assets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), 100, 100, false);
        Assert.Equal(AvatarFixture.RestAni, hero.CurrentAnimationPath);

        hero.BattleStance = true;                                       // 还没载入战斗站立动作:姿态不变
        Assert.False(hero.BattleStance);
        Assert.True(hero.LoadBattleRest(assets, AvatarFixture.ChrPath));
        Assert.False(hero.BattleStance);                                // 载入本身不换姿态
        Assert.Equal(AvatarFixture.RestAni, hero.CurrentAnimationPath);

        hero.Update(150, 0, 0);
        Assert.True(hero.MotionMs >= 150);
        hero.BattleStance = true;
        Assert.Equal(AvatarFixture.WaitAni, hero.CurrentAnimationPath);
        Assert.Equal(0, hero.MotionMs);                                 // 站着换姿态:站立动画从头播放
        hero.BattleStance = true;                                       // 同一个姿态再设一遍:不重新计时
        hero.Update(50, 0, 0);
        hero.BattleStance = true;
        Assert.True(hero.MotionMs >= 50);

        hero.Update(20, 1, 0);                                          // 走起来:用行走动画,与姿态无关
        Assert.Equal(HeroMotion.Move, hero.Motion);
        Assert.EndsWith("/move.ani", hero.CurrentAnimationPath);
        hero.BattleStance = false;
        Assert.EndsWith("/move.ani", hero.CurrentAnimationPath);
        Assert.True(hero.MotionMs > 0);                                 // 行走时换姿态不打断行走计时

        Assert.Throws<DnfFormatException>(() => hero.LoadBattleRest(assets, "character/test/missing.chr"));
    }

    [Fact]
    public void ASettledOpenGateIsOpenWithTheOneShotFadeAlreadyOver()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
        GateObject obj = GateObject.Read(assets, catalog, RoomFixture.LeftNormalId)!;
        var gate = new RoomGate(assets, obj, GateSide.Left, 16, 120);
        gate.OpenSettled();
        Assert.Equal((GateState.Open, RoomGate.SettledOpenedAtMs), (gate.State, gate.OpenedAtMs));
        List<SceneSprite> sprites = gate.Sprites(assets, new TownProjection(0, 200), 0);
        Assert.Equal(2, sprites.Count);                                 // 门框与开着时的光
        Assert.EndsWith("/open.ani", sprites[1].AnimationPath);
        gate.Open(500);                                                 // 已经开着:不重新计时
        Assert.Equal(RoomGate.SettledOpenedAtMs, gate.OpenedAtMs);
    }

    [Fact]
    public void TheMazeNormalizesItsMinimapImageTagToASpritePath()
    {
        DungeonMaze without = DungeonMaze.Parse("dungeon/t/t.dgn", MazeDoc())!;
        Assert.Null(without.MinimapImage);
        DungeonMaze withTag = DungeonMaze.Parse("dungeon/t/t.dgn", MazeDoc("JJEE LLMM DDGG", Line("[minimap image]", @"Map\MiniMap\Act2.img")))!;
        Assert.Equal("sprite/map/minimap/act2.img", withTag.MinimapImage);
        Assert.Equal("sprite/map/minimap/act1.img", DungeonMaze.NormalizeMinimapImage("sprite/Map/MiniMap/Act1.img"));
        Assert.Null(DungeonMaze.NormalizeMinimapImage("   "));
        Assert.Null(DungeonMaze.NormalizeMinimapImage(null));
    }
}
