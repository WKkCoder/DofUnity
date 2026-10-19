using System.Globalization;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// arrival-check:进房间的落点(证据 E-074)。
/// 不带 --scan:对副本里每个格子的房间,打印出口的识别矩形与按客户端规则算出的落点(矩形里的随机点 → 最终可站点),以及第一个房间的出生点;
/// 带 --scan 起:止 --expect 房间:侧:x:y:容差,…:在主角编号范围里找同时满足所有期望落点的编号(期望是对原版量到的位置,用来给没有查明来源的主角编号取个拟合值)。
/// 数据都只在本机,不入库。
/// </summary>
internal static class ArrivalCheck
{
    private sealed class Expectation
    {
        public string Room = "";
        public string Side = "";
        public int X;
        public int? Y;
        public int Tolerance;
    }

    private sealed class Cell
    {
        public int X;
        public int Y;
        public RoomCandidate Candidate = null!;
        public RoomSession Room = null!;
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        DungeonMaze maze = DungeonMaze.Read(assets, opts.Dungeon) ?? throw new UserError("副本定义里没有可用的迷宫: " + opts.Dungeon);
        DungeonRoomPool pool = DungeonRoomPool.Read(assets, DungeonRoomPool.FolderFor(opts.Dungeon));
        DungeonLayout layout = DungeonLayout.Create(maze, pool, opts.RandomLayout ? new Random(opts.Seed) : null);
        AvatarOutfit outfit = DemoRoom.DefaultOutfit(assets, opts.HeroWeapon);
        DungeonRoomFactory factory = DungeonSession.CreateAssetFactory(assets, layout, outfit, opts.Seed, opts.HeroId);

        var cells = new List<Cell>();
        for (int y = 0; y < maze.Rows; y++)
        {
            for (int x = 0; x < maze.Cols; x++)
            {
                if (maze.ExitsAt(x, y) == 0)
                {
                    continue;
                }
                RoomCombatSession session = factory(x, y);
                cells.Add(new Cell { X = x, Y = y, Candidate = layout.RoomAt(x, y), Room = session.Room });
            }
        }

        if (opts.Scan != null)
        {
            return Scan(cells, opts);
        }
        if (opts.Probe != null)
        {
            return Probe(cells, opts.Probe);
        }

        Console.WriteLine("副本 " + opts.Dungeon + ",主角对象编号 " + opts.HeroId + ",占位盒 "
            + cells[0].Room.Hero.BodyWidth + " × " + cells[0].Room.Hero.BodyDepth);
        foreach (Cell c in cells)
        {
            RoomMap map = c.Room.Map;
            Console.WriteLine();
            Console.WriteLine("格 (" + c.X + "," + c.Y + ") " + c.Candidate.MapPath + "  地图编号 " + c.Candidate.MapId + "  宽 " + map.Width
                + "  舞台格 " + c.Room.Grid.Columns + " × " + c.Room.Grid.Rows);
            if (map.StartArea.HasValue)
            {
                WorldRect r = map.StartArea.Value;
                ArrivalPoint? p = RoomArrival.ComputeAtStart(c.Room, c.Candidate.MapId, opts.HeroId);
                Console.WriteLine("  出生区 " + Rect(r) + "  → " + Describe(p));
            }
            foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
            {
                WorldRect? area = map.GateRects[(int)side];
                if (!area.HasValue)
                {
                    continue;
                }
                int[] pos = map.Gates[(int)side];
                GateKind kind = layout.GateKindAt(c.X, c.Y, side);
                RoomGate? gate = c.Room.Content.Gates.FirstOrDefault(g => g.Side == side);
                string body = gate?.Body == null ? "无门体" : "门体起点格 (" + gate.Body.OriginColumn + "," + gate.Body.OriginRow + ")";
                ArrivalPoint? p = RoomArrival.ComputeAtGate(c.Room, side, c.Candidate.MapId, opts.HeroId);
                Console.WriteLine("  " + SideName(side) + " 出口 (" + pos[0] + "," + pos[1] + ") " + kind + "  矩形 " + Rect(area.Value) + "  " + body + "  → " + Describe(p));
            }
        }
        return 0;
    }

    private static int Scan(List<Cell> cells, Options opts)
    {
        string[] range = opts.Scan!.Split(':');
        if (range.Length != 2 || !long.TryParse(range[0], out long from) || !long.TryParse(range[1], out long to) || to <= from)
        {
            throw new UserError("--scan 需要 起:止(整数,止不含)");
        }
        if (string.IsNullOrWhiteSpace(opts.Expect))
        {
            throw new UserError("--scan 需要 --expect 房间:侧:x:y:容差,…");
        }
        List<Expectation> expects = ParseExpectations(opts.Expect!);
        var rooms = new List<(Expectation e, Cell cell)>();
        foreach (Expectation e in expects)
        {
            Cell? cell = cells.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c.Candidate.MapPath), e.Room, StringComparison.OrdinalIgnoreCase));
            if (cell == null)
            {
                throw new UserError("副本里没有房间 " + e.Room);
            }
            rooms.Add((e, cell));
        }

        int hits = 0;
        for (long id = from; id < to; id++)
        {
            bool ok = true;
            foreach ((Expectation e, Cell cell) in rooms)
            {
                ArrivalPoint? p = e.Side == "start"
                    ? RoomArrival.ComputeAtStart(cell.Room, cell.Candidate.MapId, (int)id)
                    : RoomArrival.ComputeAtGate(cell.Room, ParseSide(e.Side), cell.Candidate.MapId, (int)id);
                if (!p.HasValue || Math.Abs(p.Value.X - e.X) > e.Tolerance || (e.Y.HasValue && Math.Abs(p.Value.Y - e.Y.Value) > e.Tolerance))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                hits++;
                if (hits <= 40)
                {
                    Console.WriteLine("编号 " + id + " 满足全部 " + rooms.Count + " 条期望");
                }
            }
        }
        Console.WriteLine("在 [" + from + ", " + to + ") 里共 " + hits + " 个编号满足全部期望");
        return 0;
    }

    /// <summary>
    /// --probe 房间:侧:y,…:沿高度 y 的水平线,从出口 x 起向房间里找第一个主角站得下的 x(门体边界;左出口往右找、右出口往左找),
    /// 同时给出连地图瓦片一起算的边界。y 写 * 时在出口矩形的高度范围里每 6 像素一行。
    /// </summary>
    private static int Probe(List<Cell> cells, string text)
    {
        foreach (string item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] f = item.Split(':');
            if (f.Length != 3)
            {
                throw new UserError("--probe 的每一项要写成 房间:侧:y,得到 " + item);
            }
            Cell cell = cells.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c.Candidate.MapPath), f[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new UserError("副本里没有房间 " + f[0]);
            GateSide side = ParseSide(f[1].ToLowerInvariant());
            if (side != GateSide.Left && side != GateSide.Right)
            {
                throw new UserError("--probe 只支持 left 或 right");
            }
            RoomGate gate = cell.Room.Content.Gates.FirstOrDefault(g => g.Side == side)
                ?? throw new UserError("这个房间在这一侧没有出口物件: " + item);
            WorldRect area = gate.Area;
            var ys = new List<int>();
            if (f[2] == "*")
            {
                for (int y = area.Y; y < area.Y + area.Height; y += 6)
                {
                    ys.Add(y);
                }
            }
            else
            {
                ys.Add(int.Parse(f[2], CultureInfo.InvariantCulture));
            }
            TownHero hero = cell.Room.Hero;
            int bw = hero.BodyWidth, bd = hero.BodyDepth;
            Console.WriteLine(f[0] + " " + SideName(side) + "出口 (" + gate.X + "," + gate.Y + ") 门体"
                + (gate.Body == null ? "无" : "起点格 (" + gate.Body.OriginColumn + "," + gate.Body.OriginRow + ")"));
            foreach (int y in ys)
            {
                int step = side == GateSide.Left ? 1 : -1;
                int doorEdge = int.MinValue, gridEdge = int.MinValue;
                for (int i = 0; i < 600; i++)
                {
                    int x = gate.X + step * i;
                    if (doorEdge == int.MinValue && (gate.Body == null || !gate.Body.Overlaps(x, y, bw, bd)))
                    {
                        doorEdge = x;
                    }
                    if (gridEdge == int.MinValue && cell.Room.Grid.IsFree(x, y, bw, bd) && hero.Walk.Contains(x, y))
                    {
                        gridEdge = x;
                    }
                    if (doorEdge != int.MinValue && gridEdge != int.MinValue)
                    {
                        break;
                    }
                }
                Console.WriteLine("  y=" + y + "  门体边界 x=" + (doorEdge == int.MinValue ? "无" : doorEdge.ToString())
                    + "  含瓦片边界 x=" + (gridEdge == int.MinValue ? "无" : gridEdge.ToString()));
            }
        }
        return 0;
    }

    private static List<Expectation> ParseExpectations(string text)
    {
        var result = new List<Expectation>();
        foreach (string item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] f = item.Split(':');
            if (f.Length != 5 || !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tol))
            {
                throw new UserError("--expect 的每一项要写成 房间:侧:x:y:容差,得到 " + item);
            }
            int? y = null;
            if (f[3] != "*")
            {
                y = int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int yy) ? yy : throw new UserError("--expect 的 y 要写整数或 *: " + item);
            }
            string side = f[1].ToLowerInvariant();
            if (side != "start")
            {
                ParseSide(side);
            }
            result.Add(new Expectation { Room = f[0], Side = side, X = x, Y = y, Tolerance = tol });
        }
        return result;
    }

    private static GateSide ParseSide(string s)
    {
        switch (s)
        {
            case "left": return GateSide.Left;
            case "right": return GateSide.Right;
            case "up": return GateSide.Up;
            case "down": return GateSide.Down;
            default: throw new UserError("出口的侧要写 start、left、right、up 或 down: " + s);
        }
    }

    private static string SideName(GateSide side)
    {
        switch (side)
        {
            case GateSide.Left: return "左";
            case GateSide.Right: return "右";
            case GateSide.Up: return "上";
            default: return "下";
        }
    }

    private static string Rect(WorldRect r)
    {
        return "[" + r.X + "," + (r.X + r.Width) + ")×[" + r.Y + "," + (r.Y + r.Height) + ")";
    }

    private static string Describe(ArrivalPoint? p)
    {
        if (!p.HasValue)
        {
            return "(没有矩形)";
        }
        ArrivalPoint v = p.Value;
        string text = "随机点 (" + v.RandomX + "," + v.RandomY + ") → 落点 (" + v.X + "," + v.Y + ")";
        if (v.RandomX != v.X || v.RandomY != v.Y)
        {
            text += v.Found ? "  [挪了 " + (v.X - v.RandomX) + "," + (v.Y - v.RandomY) + "]" : "  [半径内没有可站点,退到最近的可走点]";
        }
        return text;
    }
}
