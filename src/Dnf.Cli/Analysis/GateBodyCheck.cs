using Dnf.Data;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// gate-body-check:出口门体与墙的碰撞体会不会把房间切开(M34 清房自动机在冰宫的一间房里被一道横穿全图的"墙"困住,由此查起)。
/// 按每座副本、每个迷宫格,用与播放器同一条路径造出房间的出口(<see cref="DemoRoom.BuildGates"/>),把所有出口的门体盖到舞台格上,
/// 从每一个"进得来的出口"的识别矩形里能站的点出发,在 8 像素的取样格上做洪水填充(主角占位盒能站才算),
/// 看房间里每个怪物的出生点附近(40 像素内)是不是都够得着;够不着的房间再换三种盖法重算,看是哪一类碰撞体造成的:
/// 不盖墙的碰撞体、只盖"位置在本侧"的墙的碰撞体、全盖。只打聚合与分类计数,数值全是用户自己的数据,不入库。
/// </summary>
internal static class GateBodyCheck
{
    private const int Step = 8;
    private const int Reach = 40;

    private enum Mode
    {
        All,
        NoWalls,
        OwnSideWalls,
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        int rooms = 0, entries = 0, cut = 0, fixedNoWalls = 0, fixedOwnSide = 0, stillCut = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutRooms = new List<string>();
        var stillRooms = new List<string>();
        foreach (KeyValuePair<int, string> kv in WorldMapDef.ParseList(assets.ReadScript(DungeonRoomPool.DungeonListPath), "dungeon/"))
        {
            DungeonMaze? maze;
            DungeonLayout layout;
            try
            {
                maze = DungeonMaze.Read(assets, kv.Value, null);
                if (maze == null)
                {
                    continue;
                }
                layout = DungeonLayout.Create(maze, DungeonRoomPool.ReadFor(assets, kv.Value), null);
            }
            catch (DnfFormatException)
            {
                continue;
            }
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    if (maze.ExitsAt(x, y) == 0)
                    {
                        continue;
                    }
                    RoomCandidate? cand = layout.RoomAt(x, y);
                    if (cand == null || maze.IsBossCell(x, y) || !seen.Add(cand.MapPath + "|" + maze.ExitsAt(x, y)))
                    {
                        continue;
                    }
                    RoomMap map;
                    try
                    {
                        map = RoomMap.Read(assets, cand.MapPath);
                    }
                    catch (DnfFormatException)
                    {
                        continue;
                    }
                    int cx = x, cy = y;
                    List<RoomGate> gates = DemoRoom.BuildGates(assets, map, maze.GateSet, side => layout.GateKindAt(cx, cy, side));
                    if (map.Monsters.Count == 0)
                    {
                        continue;
                    }
                    rooms++;
                    var entrySides = new List<GateSide>();
                    foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
                    {
                        GateKind kind = layout.GateKindAt(cx, cy, side);
                        if ((kind == GateKind.Normal || kind == GateKind.Boss) && map.GateRects[(int)side].HasValue)
                        {
                            entrySides.Add(side);
                        }
                    }
                    foreach (GateSide side in entrySides)
                    {
                        entries++;
                        if (Unreached(map, gates, layout, cx, cy, side, Mode.All) == 0)
                        {
                            continue;
                        }
                        cut++;
                        string label = kv.Value + " (" + x + "," + y + ") 自 " + side + " 进";
                        cutRooms.Add(label);
                        bool a = Unreached(map, gates, layout, cx, cy, side, Mode.NoWalls) == 0;
                        bool b = Unreached(map, gates, layout, cx, cy, side, Mode.OwnSideWalls) == 0;
                        fixedNoWalls += a ? 1 : 0;
                        fixedOwnSide += b ? 1 : 0;
                        if (!a && !b)
                        {
                            stillCut++;
                            stillRooms.Add(label);
                        }
                    }
                }
            }
        }
        Console.WriteLine("房间 " + rooms + " 张(按地图 × 出口码去重,首领格除外),进得来的出口 " + entries + " 个");
        Console.WriteLine("  所有碰撞体都盖上时,有怪物出生点够不着的:" + cut + " 个入口");
        Console.WriteLine("    不盖墙的碰撞体就全够得着:" + fixedNoWalls + ";只盖位置在本侧的墙就全够得着:" + fixedOwnSide + ";两种都不行:" + stillCut);
        if (opts.Trace)
        {
            foreach (string r in cutRooms)
            {
                Console.WriteLine("  够不着:" + r);
            }
            foreach (string r in stillRooms)
            {
                Console.WriteLine("  仍够不着:" + r);
            }
        }
        return 0;
    }

    /// <summary>从 <paramref name="entry"/> 一侧进房间(出口识别矩形里能站的点),在给定盖法下够不着的怪物出生点有几个。</summary>
    private static int Unreached(RoomMap map, List<RoomGate> gates, DungeonLayout layout, int cx, int cy, GateSide entry, Mode mode)
    {
        var grid = new RoomGrid(map);
        foreach (RoomGate g in gates)
        {
            if (g.Body == null)
            {
                continue;
            }
            bool wall = layout.GateKindAt(cx, cy, g.Side) == GateKind.Wall;
            bool wrongHalf = g.Side == GateSide.Left ? g.X * 2 > grid.WidthPixels : g.Side == GateSide.Right && g.X * 2 < grid.WidthPixels;
            if (mode == Mode.NoWalls && wall)
            {
                continue;
            }
            if (mode == Mode.OwnSideWalls && wall && wrongHalf)
            {
                continue;
            }
            grid.Stamp(g.Body);
        }
        int cols = grid.WidthPixels / Step, rows = grid.HeightPixels / Step;
        var free = new bool[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                free[r * cols + c] = grid.IsFree(c * Step + Step / 2, r * Step + Step / 2, TownHero.DefaultBodyWidth, TownHero.DefaultBodyDepth);
            }
        }
        var seen = new bool[cols * rows];
        var queue = new Queue<int>();
        WorldRect rect = map.GateRects[(int)entry]!.Value;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double px = c * Step + Step / 2, py = r * Step + Step / 2;
                if (free[r * cols + c] && px >= rect.X && px < rect.X + rect.Width && py >= rect.Y && py < rect.Y + rect.Height)
                {
                    seen[r * cols + c] = true;
                    queue.Enqueue(r * cols + c);
                }
            }
        }
        if (queue.Count == 0)
        {
            return 0;   // 识别矩形里站不下:由"最近可站点"兜底,这里不判
        }
        int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int ci = i % cols, ri = i / cols;
            for (int k = 0; k < 4; k++)
            {
                int nc = ci + dx[k], nr = ri + dy[k];
                if (nc < 0 || nr < 0 || nc >= cols || nr >= rows)
                {
                    continue;
                }
                int j = nr * cols + nc;
                if (free[j] && !seen[j])
                {
                    seen[j] = true;
                    queue.Enqueue(j);
                }
            }
        }
        int unreached = 0;
        foreach (RoomMonsterSpawn m in map.Monsters)
        {
            bool ok = false;
            int span = Reach / Step + 1;
            int mc = m.X / Step, mr = m.Y / Step;
            for (int r = Math.Max(0, mr - span); r <= Math.Min(rows - 1, mr + span) && !ok; r++)
            {
                for (int c = Math.Max(0, mc - span); c <= Math.Min(cols - 1, mc + span) && !ok; c++)
                {
                    if (seen[r * cols + c] && Math.Abs(c * Step + Step / 2 - m.X) <= Reach && Math.Abs(r * Step + Step / 2 - m.Y) <= Reach)
                    {
                        ok = true;
                    }
                }
            }
            if (!ok)
            {
                unreached++;
            }
        }
        return unreached;
    }
}
