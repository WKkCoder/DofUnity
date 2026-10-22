using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 副本迷宫与房间布局(证据 E-070、E-113):.dgn 的 [greed] 出口码(右 1、上 2、左 4、下 8,字母 = 'A' + 组合;A = 空格)、起点格与首领格的坐标对列表、
/// 多个 [maze info] 段落与 [quest connection]、[map specification] 指定房间、房间地图 [greed] 的各对字母(出口码,入口位)与 [type]、[dungeon] 副本编号的选图规则、
/// 出口种类的取法;以及战斗姿态与"直接开着"的出口。数据全是合成的,不含任何原版内容。
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
    /// 起点格 (0,0)、(1,0),首领格 (0,2)、(1,2)(坐标对列表)。
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
    public void TheExpWeightIsTheFirstNumberOfTheExperienceTagWhetherIntOrFloatAndDefaultsToOne()
    {
        static ScriptLine Weight(ScriptValue value) => new ScriptLine("[experience increasing point]", new[] { value });
        static ScriptValue F(float v) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(v), null);
        static ScriptValue I(int v) => new ScriptValue(ScriptValueKind.Int, v, null);

        Assert.Equal(1f, DungeonMaze.Parse("d", MazeDoc())!.ExpWeight);                                   // 没写:缺省 1(证据 E-111)
        Assert.Equal(1.5f, DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", Weight(F(1.5f))))!.ExpWeight);  // 浮点
        Assert.Equal(3f, DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", Weight(I(3))))!.ExpWeight);      // 整数也按浮点读
        Assert.Equal(0f, DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", Weight(I(0))))!.ExpWeight);      // 0:这座副本不给经验
        Assert.Equal(1f, DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", new ScriptLine("[experience increasing point]", new ScriptValue[0])))!.ExpWeight);   // 没有数:还是 1
        Assert.Equal(1f, new DungeonMaze("d", 1, 1, new[] { 0 }, null, new[] { new CellRect(0, 0, 0, 0) }, new[] { new CellRect(0, 0, 0, 0) }, null).ExpWeight);   // 直接构造的缺省
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
    public void StartAndBossMapsAreListsOfCellCoordinatePairsNotRectangles()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc())!;
        Assert.True(maze.IsStartCell(0, 0));
        Assert.True(maze.IsStartCell(1, 0));
        Assert.False(maze.IsStartCell(0, 1));
        Assert.True(maze.IsBossCell(0, 2));
        Assert.True(maze.IsBossCell(1, 2));
        Assert.False(maze.IsBossCell(1, 1));
        // 四个数是两个格 (1,0) 与 (0,1),不是以它们为对角的矩形(那样 (0,0)、(1,1) 也会算进去,E-113);一个 [boss map] 可以接着写多对
        var multi = DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", Line("[boss map]", 1, 0, 0, 1)))!;
        Assert.True(multi.IsBossCell(1, 0));
        Assert.True(multi.IsBossCell(0, 1));
        Assert.False(multi.IsBossCell(0, 0));
        Assert.False(multi.IsBossCell(1, 1));
        Assert.Equal(4, multi.BossCells.Count);                                          // 原来的两格 + 新的两格,每格一个单格范围
        // 多出来的一个数丢掉;每一对一个单格范围
        var odd = DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", Line("[start map]", 1, 1, 0)))!;
        Assert.True(odd.IsStartCell(1, 1));
        Assert.False(odd.IsStartCell(0, 1));
        Assert.Equal((1, 1, 1, 1), (odd.StartCells[2].X0, odd.StartCells[2].Y0, odd.StartCells[2].X1, odd.StartCells[2].Y1));
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
    public void ARoomHeaderGivesEveryGreedPairTypeStartAreaAndDungeonIds()
    {
        RoomCandidate a = DungeonRoomPool.ReadHeader(new[] { Line("[type]", "[normal]"), Line("[greed]", "JJ BB"), Line("[dungeon start area]", 1, 2, 3, 4), Line("[dungeon]", 5, 7) }, "map/t/a.map", 3)!;
        Assert.Equal(("map/t/a.map", 3, 9, 9, false, true), (a.MapPath, a.MapId, a.Exits, a.Second, a.IsBoss, a.HasStartArea));
        Assert.Equal(new[] { (9, 9), (1, 1) }, a.Pairs.Select(p => (p.Exits, p.Entrance)).ToArray());          // 每一对字母 = 一个 (出口码, 入口位)
        Assert.Equal(new[] { 5, 7 }, a.DungeonIds.ToArray());
        RoomCandidate b = DungeonRoomPool.ReadHeader(new[] { Line("[type]", "[boss]"), Line("[greed]", "DD") }, "map/t/b.map", 9)!;
        Assert.Equal((3, 3, true, false, RoomType.Boss), (b.Exits, b.Second, b.IsBoss, b.HasStartArea, b.Type));
        Assert.Empty(b.DungeonIds);
        RoomCandidate d = DungeonRoomPool.ReadHeader(new[] { Line("[type]", "[dummy]"), Line("[greed]", "BB") }, "map/t/d.map", 4)!;
        Assert.Equal((RoomType.Dummy, true, false), (d.Type, d.IsDummy, d.IsBoss));
        // 没写 [greed] 的图、或只有半对的图,按 (15, 15) 登记(服务端 load_map_files 的做法,E-113);没写 [type] 是普通
        RoomCandidate c = DungeonRoomPool.ReadHeader(new[] { Line("[dungeon]", 5) }, "map/t/c.map", 1)!;
        Assert.Equal((15, 15, RoomType.Normal), (c.Pairs[0].Exits, c.Pairs[0].Entrance, c.Type));
        Assert.Single(c.Pairs);
        RoomCandidate half = DungeonRoomPool.ReadHeader(new[] { Line("[greed]", "J") }, "map/t/h.map", 1)!;
        Assert.Equal((15, 15), (half.Exits, half.Second));
        Assert.Null(DungeonRoomPool.ReadHeader(null!, "map/t/e.map", 1));
    }

    [Fact]
    public void ADirectionAndEntranceAreCheckedAgainstEveryPair()
    {
        // 两对:出口码 J(右 + 下)入口位 B(右);出口码 D(右 + 上)入口位 E(左)
        var room = new RoomCandidate("m", 1, RoomType.Normal, new[] { new GreedPair(9, 1), new GreedPair(3, 4) }, false, null);
        Assert.True(room.HasDirection(9));
        Assert.True(room.HasDirection(3));
        Assert.False(room.HasDirection(1));
        Assert.Equal(1, room.EntranceFor(9));
        Assert.Equal(4, room.EntranceFor(3));
        Assert.Equal(0, room.EntranceFor(12));
        Assert.Equal(5, room.AnyEntrance);
        Assert.True(room.CheckEntrance(4));                                              // 任何一对的入口位相交就行
        Assert.True(room.CheckEntrance(1 | 8));
        Assert.False(room.CheckEntrance(2 | 8));
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
    public void ARoomWithSeveralPairsIsFoundUnderEachExitCodeAndDummiesAreKeptApart()
    {
        var pool = new DungeonRoomPool(new[]
        {
            new RoomCandidate("m/two", 1, RoomType.Normal, new[] { new GreedPair(9, 9), new GreedPair(3, 3) }, false, null),
            new RoomCandidate("m/start", 2, RoomType.Normal, new[] { new GreedPair(9, 9) }, true, null),
            new RoomCandidate("m/dummy", 3, RoomType.Dummy, new[] { new GreedPair(3, 3) }, false, null),
            new RoomCandidate("m/boss", 4, RoomType.Boss, new[] { new GreedPair(9, 9) }, true, null),
        });
        Assert.Equal(new[] { 1, 2 }, pool.Find(9, RoomRole.Normal).Select(r => r.MapId).ToArray());   // 起点图本身也可以是普通格的房间;首领图不行
        Assert.Equal(new[] { 1 }, pool.Find(3, RoomRole.Normal).Select(r => r.MapId).ToArray());      // 假首领图不进普通候选
        Assert.Equal(new[] { 3 }, pool.FindDummy(3).Select(r => r.MapId).ToArray());
        Assert.Empty(pool.FindDummy(9));
        Assert.Equal(new[] { 2, 4 }, pool.Find(9, RoomRole.Start).Select(r => r.MapId).ToArray());    // 起点只看有没有出生区(服务端 insertStart 不看类型)
        Assert.Equal(new[] { 4 }, pool.Find(9, RoomRole.Boss).Select(r => r.MapId).ToArray());
        Assert.Same(pool.Rooms[2], pool.ById(3));
        Assert.Null(pool.ById(99));
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

        // 按副本编号找房间:dungeon.lst 里这个副本是 5 号,a.map 的 [dungeon] 里有 5;town.map 没写 [dungeon];Other/x.map 没有脚本
        DungeonRoomPool pool = DungeonRoomPool.ReadFor(assets, RoomFixture.DungeonPath);
        RoomCandidate room = Assert.Single(pool.Rooms);
        Assert.Equal((RoomFixture.MapPath, 1, 9, 9, false, true), (room.MapPath, room.MapId, room.Exits, room.Second, room.IsBoss, room.HasStartArea));
        Assert.Equal(new[] { 5 }, room.DungeonIds.ToArray());
        Assert.Equal(5, DungeonRoomPool.DungeonIdOf(assets, RoomFixture.DungeonPath));
        Assert.Equal(5, DungeonRoomPool.DungeonIdOf(assets, "Dungeon\\Test\\Test.dgn"));     // 路径的大小写与斜杠方向不要紧
        Assert.Equal(-1, DungeonRoomPool.DungeonIdOf(assets, "dungeon/test/none.dgn"));
        Assert.Equal(-1, DungeonRoomPool.DungeonIdOf(assets, null!));

        // 老约定:目录与副本同名。"test/" 下 a.map 与 town.map 都是候选(没有 [greed] 的图按 (15,15) 登记)
        DungeonRoomPool byFolder = DungeonRoomPool.Read(assets, DungeonRoomPool.FolderFor(RoomFixture.DungeonPath));
        Assert.Equal(new[] { 1, 2 }, byFolder.Rooms.Select(r => r.MapId).ToArray());
        Assert.Equal((15, 15), (byFolder.Rooms[1].Exits, byFolder.Rooms[1].Second));
        // 副本不在 dungeon.lst 里,或 [dungeon] 里没有一张图指向它:退回老约定,一张也没有就是空池(不抛错)
        Assert.Equal(2, DungeonRoomPool.ReadFor(assets, "dungeon/other/test.dgn").Rooms.Count);
        Assert.Empty(DungeonRoomPool.ReadFor(assets, RoomFixture.MultiDungeonPath).Rooms);

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
    public void AnAltarDungeonTakesItsRoomsFromTheMapIdsWrittenInTheDefinition()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // [advance altar map] "easy" 2 1:地图编号是紧跟在名字后面的 2,后面的 1 是别的数(与 a.map 的编号相同,不能当地图编号)
        DungeonRoomPool altar = DungeonRoomPool.ReadFor(assets, RoomFixture.AltarDungeonPath);
        Assert.Equal(new[] { 2 }, altar.Rooms.Select(r => r.MapId).ToArray());
        // [advance altar survival map] 2 2 1 2:全是地图编号,可重复,按先后去重
        DungeonRoomPool survival = DungeonRoomPool.ReadFor(assets, RoomFixture.SurvivalDungeonPath);
        Assert.Equal(new[] { 2, 1 }, survival.Rooms.Select(r => r.MapId).ToArray());
        // 副本定义不存在、也不在 dungeon.lst 里、目录名也没有对应的图:空池,不抛错
        Assert.Empty(DungeonRoomPool.ReadFor(assets, "dungeon/test/none.dgn").Rooms);
    }

    [Fact]
    public void ADungeonWithSeveralMazeSectionsUsesOnlyTheDefaultOnes()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        List<DungeonMaze> all = DungeonMaze.ParseAll(RoomFixture.MultiDungeonPath, assets.ReadScript(RoomFixture.MultiDungeonPath));
        Assert.Equal(new[] { -1, 0, -1, 1 }, all.Select(m => m.QuestConnection).ToArray());           // 没写 [quest connection] = −1 = 默认迷宫(E-113)
        Assert.Equal(new[] { (1, 1), (1, 1), (2, 1), (1, 1) }, all.Select(m => (m.Cols, m.Rows)).ToArray());
        Assert.NotNull(all[3].GateSet);                                                                 // 出口物件是副本级的,各段落共用

        DungeonMaze first = DungeonMaze.Read(assets, RoomFixture.MultiDungeonPath)!;                   // 不给随机数:第一张默认迷宫
        Assert.Equal((1, 1, -1), (first.Cols, first.Rows, first.QuestConnection));
        Assert.Equal(-1, DungeonMaze.Parse("d", assets.ReadScript(RoomFixture.MultiDungeonPath))!.QuestConnection);   // Parse 也取第一张默认迷宫

        var seen = new HashSet<int>();
        for (int seed = 0; seed < 40; seed++)
        {
            DungeonMaze m = DungeonMaze.Read(assets, RoomFixture.MultiDungeonPath, new Random(seed))!;  // 开局在默认迷宫里随机挑一张,任务迷宫不会被挑到
            Assert.Equal(-1, m.QuestConnection);
            seen.Add(m.Cols);
        }
        Assert.Equal(new[] { 1, 2 }, seen.OrderBy(c => c).ToArray());

        // 只有任务迷宫的副本没有默认迷宫
        Assert.Null(DungeonMaze.Parse("d", new[] { Line("[maze info]"), Line("[quest connection]", 0), Line("[size]", 1, 1), Line("[greed]", "BB") }));
        // 老格式:不写 [maze info],[size] 等直接放在副本定义里,算一个默认段落
        Assert.NotNull(DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]", "BB") }));
        // 坏段落(字母数不对)丢掉,不影响别的段落
        List<DungeonMaze> mixed = DungeonMaze.ParseAll("d", new[]
        {
            Line("[maze info]"), Line("[size]", 2, 1), Line("[greed]", "BB"),
            Line("[maze info]"), Line("[size]", 1, 1), Line("[greed]", "BB"),
        });
        Assert.Single(mixed);
        Assert.Empty(DungeonMaze.ParseAll("d", null!));
    }

    [Fact]
    public void MapSpecificationsAreKeptPerCellAndTheLastOneWins()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG",
            Line("[map specification]", 1, 0, 7, 8),
            Line("[map specification]", 1, 0, 9),                       // 同一格再写一次:换掉前一次(服务端先清空再读)
            Line("[map specification]", 0, 1, 4, 5, 6),
            Line("[map specification]", 9, 9, 1),                       // 迷宫外的格丢掉
            Line("[map specification]", 1),                             // 缺数:丢掉
            Line("[boss map specification]", 1, 2, 3)))!;
        Assert.Equal(new[] { 9 }, maze.MapSpecAt(1, 0)!.ToArray());
        Assert.Equal(new[] { 4, 5, 6 }, maze.MapSpecAt(0, 1)!.ToArray());
        Assert.Null(maze.MapSpecAt(0, 0));
        Assert.Null(maze.MapSpecAt(9, 9));
        Assert.Equal(new[] { 3 }, maze.BossMapSpecAt(1, 2)!.ToArray());
        Assert.Null(maze.BossMapSpecAt(0, 2));
        // 一个编号也没写:等于没写
        DungeonMaze none = DungeonMaze.Parse("d", MazeDoc("JJEE LLMM DDGG", Line("[map specification]", 0, 0)))!;
        Assert.Null(none.MapSpecAt(0, 0));
    }

    [Fact]
    public void AnEmptyCellHasNoRoomAndNeedsNone()
    {
        // 3 列 × 1 行:B(右) E(左) A(空)。第三格是空的:不要房间、RoomAt 是 null、也不是起点候选
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 3, 1), Line("[greed]", "BBEEAA"), Line("[start map]", 0, 0, 2, 0) })!;
        Assert.True(maze.IsEmptyCell(2, 0));
        Assert.False(maze.IsEmptyCell(0, 0));
        Assert.False(maze.IsEmptyCell(7, 7));                                           // 迷宫外不是"空格"
        var pool = new DungeonRoomPool(new[] { Room("s", 1, 'B', start: true), Room("e", 2, 'E') });
        DungeonLayout layout = DungeonLayout.Create(maze, pool);
        Assert.Equal("map/t/s.map", layout.RoomAt(0, 0)!.MapPath);
        Assert.Equal("map/t/e.map", layout.RoomAt(1, 0)!.MapPath);
        Assert.Null(layout.RoomAt(2, 0));
        // 没有列表时(每一格都是候选)空格不会被选作起点
        var noList = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "AAAA") })!;
        Assert.Throws<DnfFormatException>(() => DungeonLayout.Create(noList, new DungeonRoomPool(new[] { Room("a", 1, 'A', start: true) })));
        // 起点列表里写出来的格,出口码是 A 也照样能选:池里没有登记在 A 下的起点图就没有候选
        var listedEmpty = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "BBAA"), Line("[start map]", 1, 0) })!;
        Assert.Throws<DnfFormatException>(() => DungeonLayout.Create(listedEmpty, pool));
    }

    [Fact]
    public void ASingleCellDungeonWhoseCellCodeIsAIsStillARealRoom()
    {
        // 单格副本的格出口码就是 A(没有出口),房间图登记在 (A, A) 下;起点格写在 [start map] 里,所以不算空格
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]", "AA"), Line("[start map]", 0, 0), Line("[boss map]", 0, 0) })!;
        Assert.True(maze.IsEmptyCell(0, 0));
        var pool = new DungeonRoomPool(new[] { Room("only", 1, 'A', start: true) });
        DungeonLayout layout = DungeonLayout.Create(maze, pool);
        Assert.Equal("map/t/only.map", layout.RoomAt(0, 0)!.MapPath);
        Assert.Equal((0, 0, true), (layout.StartX, layout.StartY, layout.IsBossCell(0, 0)));
        Assert.Equal(GateKind.Wall, layout.GateKindAt(0, 0, GateSide.Left));              // 没有出口:左右是墙
        Assert.Equal(GateKind.None, layout.GateKindAt(0, 0, GateSide.Down));
    }

    [Fact]
    public void ASpecifiedCellPicksOnlyAmongTheSpecifiedMapsEvenIfTheirExitsDiffer()
    {
        // 2 列 × 1 行:B E。(1,0) 指定 20 号图(登记的是出口码 C,与格不同),池里本来有出口码 E 的普通图
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "BBEE"), Line("[start map]", 0, 0), Line("[map specification]", 1, 0, 99, 20) })!;
        var pool = new DungeonRoomPool(new[] { Room("s", 1, 'B', start: true), Room("e", 10, 'E'), Room("pinned", 20, 'C') });
        DungeonLayout layout = DungeonLayout.Create(maze, pool);
        Assert.Equal("map/t/pinned.map", layout.RoomAt(1, 0)!.MapPath);                 // 99 不在池里,跳过;剩下的 20 就是唯一候选
        // 指定的编号一个也不在池里:这一格没有候选
        DungeonMaze lost = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "BBEE"), Line("[start map]", 0, 0), Line("[map specification]", 1, 0, 99) })!;
        Assert.Throws<DnfFormatException>(() => DungeonLayout.Create(lost, pool));
        // 首领格看 [boss map specification]:指定的图不要求类型是首领图
        DungeonMaze boss = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "BBEE"), Line("[start map]", 0, 0), Line("[boss map]", 1, 0), Line("[boss map specification]", 1, 0, 20) })!;
        DungeonLayout bossLayout = DungeonLayout.Create(boss, pool);
        Assert.Equal(("map/t/pinned.map", 1, 0), (bossLayout.RoomAt(1, 0)!.MapPath, bossLayout.BossX, bossLayout.BossY));
    }

    [Fact]
    public void WithoutARealBossRoomTheBossCellTakesADummyRoom()
    {
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "BBEE"), Line("[start map]", 0, 0), Line("[boss map]", 1, 0) })!;
        var dummy = new RoomCandidate("map/t/dummy.map", 30, RoomType.Dummy, new[] { new GreedPair(4, 4) }, false, null);
        var pool = new DungeonRoomPool(new[] { Room("s", 1, 'B', start: true), Room("e", 10, 'E'), dummy });
        DungeonLayout layout = DungeonLayout.Create(maze, pool);
        Assert.Equal((1, 0), (layout.BossX, layout.BossY));
        Assert.Same(dummy, layout.RoomAt(1, 0));                                        // 没有真首领图:用假首领图;假首领图不会出现在普通格
        // 有真首领图就用真的
        var withBoss = new DungeonRoomPool(new[] { Room("s", 1, 'B', start: true), Room("e", 10, 'E'), dummy, Room("boss", 31, 'E', boss: true) });
        Assert.Equal("map/t/boss.map", DungeonLayout.Create(maze, withBoss).RoomAt(1, 0)!.MapPath);
        // 起点格不会选假首领图当普通图:普通格里没有别的 E 图时报错
        var onlyDummy = new DungeonRoomPool(new[] { Room("s", 1, 'B', start: true), dummy });
        DungeonMaze twoNormal = DungeonMaze.Parse("d", new[] { Line("[size]", 3, 1), Line("[greed]", "BBEEEE"), Line("[start map]", 0, 0), Line("[boss map]", 2, 0) })!;
        Assert.Throws<DnfFormatException>(() => DungeonLayout.Create(twoNormal, onlyDummy));
    }

    [Fact]
    public void ASingleCellDungeonWhoseStartIsTheBossCellUsesTheStartRoomAsTheBossRoom()
    {
        // 塔式副本:一格,起点格就是首领格,房间是带出生区的普通图(不是首领图)
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]", "BB"), Line("[start map]", 0, 0), Line("[boss map]", 0, 0) })!;
        var pool = new DungeonRoomPool(new[] { Room("tower", 1, 'B', start: true) });
        DungeonLayout layout = DungeonLayout.Create(maze, pool);
        Assert.Equal((0, 0, 0, 0, true), (layout.StartX, layout.StartY, layout.BossX, layout.BossY, layout.HasBoss));
        Assert.Equal("map/t/tower.map", layout.RoomAt(0, 0)!.MapPath);
        Assert.True(layout.IsBossCell(0, 0));
        // 有首领图可用、另有别的首领格时,起点格不兼首领格
        DungeonMaze two = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "BBEE"), Line("[start map]", 0, 0, 1, 0), Line("[boss map]", 0, 0, 1, 0) })!;
        var pool2 = new DungeonRoomPool(new[] { Room("s_b", 1, 'B', start: true), Room("s_e", 2, 'E', start: true), Room("boss_b", 3, 'B', boss: true), Room("boss_e", 4, 'E', boss: true) });
        DungeonLayout l2 = DungeonLayout.Create(two, pool2);
        Assert.NotEqual((l2.StartX, l2.StartY), (l2.BossX, l2.BossY));
    }

    [Fact]
    public void CandidatesWhoseEntranceCoversEveryExitComeFirst()
    {
        // (1,0) 出口码 E(左):候选 first 的入口位是 A(0),覆盖不了;second 的入口位是 E(左),能覆盖
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 2, 1), Line("[greed]", "BBEE"), Line("[start map]", 0, 0) })!;
        var first = new RoomCandidate("map/t/first.map", 10, RoomType.Normal, new[] { new GreedPair(4, 0) }, false, null);
        var second = new RoomCandidate("map/t/second.map", 11, RoomType.Normal, new[] { new GreedPair(4, 4) }, false, null);
        var pool = new DungeonRoomPool(new[] { Room("s", 1, 'B', start: true), first, second });
        Assert.Equal(11, DungeonLayout.Create(maze, pool).RoomAt(1, 0)!.MapId);
        for (int seed = 0; seed < 20; seed++)
        {
            Assert.Equal(11, DungeonLayout.Create(maze, pool, new Random(seed)).RoomAt(1, 0)!.MapId);   // 随机时也只在覆盖得了的那一档里挑
        }
        // 都覆盖不了:退回全部候选里最前面的
        var poolNone = new DungeonRoomPool(new[] { Room("s", 1, 'B', start: true), first });
        Assert.Equal(10, DungeonLayout.Create(maze, poolNone).RoomAt(1, 0)!.MapId);
    }

    [Fact]
    public void ARoomWithoutGreedFitsOnlyTheFourExitCell()
    {
        // 没写 [greed] 的图登记在出口码 15(四个出口都有)下,别的格用不上它
        DungeonMaze maze = DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]", "PP"), Line("[start map]", 0, 0) })!;
        RoomCandidate plain = DungeonRoomPool.ReadHeader(new[] { Line("[dungeon start area]", 1, 2, 3, 4) }, "map/t/plain.map", 7)!;
        DungeonLayout layout = DungeonLayout.Create(maze, new DungeonRoomPool(new[] { plain }));
        Assert.Same(plain, layout.RoomAt(0, 0));
        DungeonMaze other = DungeonMaze.Parse("d", new[] { Line("[size]", 1, 1), Line("[greed]", "BB"), Line("[start map]", 0, 0) })!;
        Assert.Throws<DnfFormatException>(() => DungeonLayout.Create(other, new DungeonRoomPool(new[] { plain })));
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
