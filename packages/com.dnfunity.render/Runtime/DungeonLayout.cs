using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>房间在迷宫里的用途:起点(有 [dungeon start area] 的普通房间)、普通、首领([type] 为 [boss] 的房间)。</summary>
    public enum RoomRole
    {
        Start = 0,
        Normal = 1,
        Boss = 2,
    }

    /// <summary>
    /// 可供迷宫选用的一张房间地图的表头:路径、编号(map.lst)、[greed] 的两对字母(第一对 = 这张图的出口位组合,证据 E-070)、
    /// 是否首领图([type])、有没有出生区。只读这几个标签,不加载瓦片与动画。
    /// </summary>
    public sealed class RoomCandidate
    {
        public string MapPath { get; }
        public int MapId { get; }

        /// <summary>[greed] 第一对字母 = 'A' + <see cref="ExitBits"/> 的组合;没写 [greed] 的地图不会成为候选。</summary>
        public int Exits { get; }

        /// <summary>[greed] 第二对字母(含义没有研究:总是第一对的子集)。</summary>
        public int Second { get; }

        public bool IsBoss { get; }
        public bool HasStartArea { get; }

        public RoomCandidate(string mapPath, int mapId, int exits, int second, bool isBoss, bool hasStartArea)
        {
            MapPath = mapPath;
            MapId = mapId;
            Exits = exits;
            Second = second;
            IsBoss = isBoss;
            HasStartArea = hasStartArea;
        }
    }

    /// <summary>
    /// 一个副本的房间候选:map.lst 里目录与副本同名的那些房间地图(洛兰副本 lorien.dgn ↔ map/lorien/ 下的那些图;"同名"是约定,没有找到副本定义里的明文链接)。
    /// 按 map.lst 的先后排列。
    /// </summary>
    public sealed class DungeonRoomPool
    {
        public const string MapListPath = "map/map.lst";

        public IReadOnlyList<RoomCandidate> Rooms { get; }

        public DungeonRoomPool(IReadOnlyList<RoomCandidate> rooms)
        {
            Rooms = rooms ?? new List<RoomCandidate>();
        }

        /// <summary>副本定义路径对应的地图目录前缀:"dungeon/act1/lorien.dgn" → "lorien/"。</summary>
        public static string FolderFor(string dungeonPath)
        {
            string name = dungeonPath ?? string.Empty;
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }
            int dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                name = name.Substring(0, dot);
            }
            return name.ToLowerInvariant() + "/";
        }

        /// <summary>
        /// 读 map.lst(无标签,值是一对对 "编号 路径",路径相对 map/),留下目录以 <paramref name="folderPrefix"/>(如 "lorien/",不分大小写)开头的地图,
        /// 逐个读表头。缺脚本、没有 [greed] 的地图跳过。
        /// </summary>
        public static DungeonRoomPool Read(DnfAssets assets, string folderPrefix)
        {
            ScriptLine[] lst = assets.ReadScript(MapListPath);
            if (lst == null)
            {
                throw new DnfFormatException("PVF 里没有 " + MapListPath);
            }
            string prefix = (folderPrefix ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            var rooms = new List<RoomCandidate>();
            foreach (ScriptLine line in lst)
            {
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 1 < v.Length; i++)
                {
                    if (!v[i].IsNumber || v[i + 1].Kind != ScriptValueKind.String || v[i + 1].Text == null)
                    {
                        continue;
                    }
                    int id = v[i].Int;
                    string rel = v[i + 1].Text.Replace('\\', '/').ToLowerInvariant();
                    i++;
                    if (!rel.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string path = "map/" + rel;
                    RoomCandidate room = ReadHeader(assets.ReadScript(path), path, id);
                    if (room != null)
                    {
                        rooms.Add(room);
                    }
                }
            }
            return new DungeonRoomPool(rooms);
        }

        /// <summary>读房间地图的表头;没有 [greed] 时返回 null。</summary>
        public static RoomCandidate ReadHeader(ScriptLine[] doc, string path, int id)
        {
            if (doc == null)
            {
                return null;
            }
            var letters = new List<char>();
            bool boss = false;
            bool start = false;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[greed]"))
                {
                    letters.Clear();
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind != ScriptValueKind.String || v.Text == null)
                        {
                            continue;
                        }
                        foreach (char c in v.Text)
                        {
                            if (c >= 'A' && c <= 'Z')
                            {
                                letters.Add(c);
                            }
                            else if (c >= 'a' && c <= 'z')
                            {
                                letters.Add((char)(c - 'a' + 'A'));
                            }
                        }
                    }
                }
                else if (line.TagIs("[type]"))
                {
                    boss = string.Equals(line.FirstString(), "[boss]", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.TagIs("[dungeon start area]"))
                {
                    start = true;
                }
            }
            if (letters.Count < 2)
            {
                return null;
            }
            int second = letters.Count >= 4 ? letters[2] - 'A' : letters[0] - 'A';
            return new RoomCandidate(path, id, letters[0] - 'A', second, boss, start);
        }

        /// <summary>
        /// 出口组合等于 <paramref name="exits"/> 且用途合适的候选(按 map.lst 的先后):
        /// 起点 = 非首领图且有出生区;普通 = 非首领图;首领 = 首领图。
        /// </summary>
        public List<RoomCandidate> Find(int exits, RoomRole role)
        {
            var found = new List<RoomCandidate>();
            foreach (RoomCandidate r in Rooms)
            {
                if (r.Exits != exits)
                {
                    continue;
                }
                bool fits = role == RoomRole.Boss ? r.IsBoss : (!r.IsBoss && (role != RoomRole.Start || r.HasStartArea));
                if (fits)
                {
                    found.Add(r);
                }
            }
            return found;
        }
    }

    /// <summary>
    /// 一次副本的布局:迷宫(<see cref="DungeonMaze"/>)+ 每一格用哪张房间地图 + 起点格与首领格。
    /// 选图规则(证据 E-070,在基线整局的六个房间上核对过):格的出口组合 = 地图 [greed] 第一对;
    /// 起点格从 [start map] 的矩形里挑、要有起点候选;首领格从 [boss map] 的矩形里挑、要有首领候选;其余格用普通图。
    /// 同一格有多张候选时,不给随机数就取 map.lst 里最前面的(基线里就是这样选的:起点格选 s2、其邻格选 s3 而不是同码的 bn12、首领格选 b2),给了随机数就随机挑。
    /// </summary>
    public sealed class DungeonLayout
    {
        private readonly RoomCandidate[] _rooms;

        public DungeonMaze Maze { get; }
        public int StartX { get; }
        public int StartY { get; }

        /// <summary>首领格;迷宫里没有可用的首领格时为 (−1, −1)。</summary>
        public int BossX { get; }

        public int BossY { get; }

        public bool HasBoss => BossX >= 0;

        private DungeonLayout(DungeonMaze maze, RoomCandidate[] rooms, int startX, int startY, int bossX, int bossY)
        {
            Maze = maze;
            _rooms = rooms;
            StartX = startX;
            StartY = startY;
            BossX = bossX;
            BossY = bossY;
        }

        public RoomCandidate RoomAt(int x, int y)
        {
            return Maze.InBounds(x, y) ? _rooms[y * Maze.Cols + x] : null;
        }

        public bool IsBossCell(int x, int y)
        {
            return HasBoss && x == BossX && y == BossY;
        }

        /// <summary>
        /// 这一格某一侧画什么出口:有出口且通向首领格 = 首领出口,通向别的格(或迷宫外)= 普通出口;
        /// 没有出口时,左右是墙,上下什么也没有(出口物件里只有左墙、右墙,证据 E-070)。
        /// </summary>
        public GateKind GateKindAt(int x, int y, GateSide side)
        {
            if (!Maze.HasExit(x, y, side))
            {
                return side == GateSide.Left || side == GateSide.Right ? GateKind.Wall : GateKind.None;
            }
            return Maze.TryNeighbor(x, y, side, out int nx, out int ny) && IsBossCell(nx, ny) ? GateKind.Boss : GateKind.Normal;
        }

        /// <summary>
        /// 给迷宫的每一格选房间地图。<paramref name="random"/> 为 null 时取候选里最前面的;否则在候选里随机挑
        /// (起点格、首领格也在各自矩形内有候选的格里随机挑)。
        /// 起点格与首领格的组合若会让别的格没有候选可用(例如首领矩形里的另一格没有对应出口码的普通房间),就换下一个组合;
        /// 全都不行时抛 <see cref="DnfFormatException"/>。
        /// </summary>
        public static DungeonLayout Create(DungeonMaze maze, DungeonRoomPool pool, Random random = null)
        {
            if (maze == null)
            {
                throw new ArgumentNullException(nameof(maze));
            }
            if (pool == null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            // 起点格:[start map] 矩形里,有起点候选的格(矩形为空时取每一格)
            List<(int X, int Y)> starts = CellsWithCandidates(maze, pool, maze.StartCells, RoomRole.Start, true);
            if (starts.Count == 0)
            {
                throw new DnfFormatException("迷宫 " + maze.Path + " 的起点格里没有可用的起点房间");
            }
            // 首领格:[boss map] 矩形里,有首领候选的格;没有就没有首领
            List<(int X, int Y)> bosses = CellsWithCandidates(maze, pool, maze.BossCells, RoomRole.Boss, false);
            if (random != null)
            {
                Shuffle(starts, random);
                Shuffle(bosses, random);
            }

            string error = null;
            foreach ((int sx, int sy) in starts)
            {
                if (bosses.Count == 0)
                {
                    DungeonLayout none = TryAssign(maze, pool, random, sx, sy, -1, -1, out error);
                    if (none != null)
                    {
                        return none;
                    }
                    continue;
                }
                foreach ((int bx, int by) in bosses)
                {
                    if (bx == sx && by == sy)
                    {
                        continue;
                    }
                    DungeonLayout layout = TryAssign(maze, pool, random, sx, sy, bx, by, out error);
                    if (layout != null)
                    {
                        return layout;
                    }
                }
            }
            throw new DnfFormatException(error ?? ("迷宫 " + maze.Path + " 的起点格与首领格没有可用的组合"));
        }

        private static DungeonLayout TryAssign(DungeonMaze maze, DungeonRoomPool pool, Random random, int sx, int sy, int bx, int by, out string error)
        {
            error = null;
            var rooms = new RoomCandidate[maze.Cols * maze.Rows];
            rooms[sy * maze.Cols + sx] = Pick(pool.Find(maze.ExitsAt(sx, sy), RoomRole.Start), random);
            if (bx >= 0)
            {
                rooms[by * maze.Cols + bx] = Pick(pool.Find(maze.ExitsAt(bx, by), RoomRole.Boss), random);
            }
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    if (rooms[y * maze.Cols + x] != null)
                    {
                        continue;
                    }
                    List<RoomCandidate> list = pool.Find(maze.ExitsAt(x, y), RoomRole.Normal);
                    if (list.Count == 0)
                    {
                        error = "迷宫 " + maze.Path + " 的格 (" + x + "," + y + ") 没有出口组合为 " + (char)('A' + maze.ExitsAt(x, y)) + " 的普通房间";
                        return null;
                    }
                    rooms[y * maze.Cols + x] = Pick(list, random);
                }
            }
            return new DungeonLayout(maze, rooms, sx, sy, bx, by);
        }

        private static void Shuffle(List<(int X, int Y)> list, Random random)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (int X, int Y) t = list[i];
                list[i] = list[j];
                list[j] = t;
            }
        }

        private static List<(int X, int Y)> CellsWithCandidates(DungeonMaze maze, DungeonRoomPool pool, IReadOnlyList<CellRect> rects,
            RoomRole role, bool emptyMeansAll)
        {
            var cells = new List<(int, int)>();
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    bool inside = rects.Count == 0 ? emptyMeansAll : false;
                    foreach (CellRect r in rects)
                    {
                        if (r.Contains(x, y))
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (inside && pool.Find(maze.ExitsAt(x, y), role).Count > 0)
                    {
                        cells.Add((x, y));
                    }
                }
            }
            return cells;
        }

        private static RoomCandidate Pick(List<RoomCandidate> list, Random random)
        {
            return list[random == null ? 0 : random.Next(list.Count)];
        }
    }
}
