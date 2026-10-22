using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>房间在迷宫里的用途:起点(有 [dungeon start area] 的房间)、普通、首领([type] 为 [boss] 的房间)。</summary>
    public enum RoomRole
    {
        Start = 0,
        Normal = 1,
        Boss = 2,
    }

    /// <summary>房间地图的 [type]:服务端 get_map_type 的三个取值(0 普通、1 首领、2 假首领;E-113),各进各的候选表。</summary>
    public enum RoomType
    {
        Normal = 0,
        Boss = 1,
        Dummy = 2,
    }

    /// <summary>房间地图 [greed] 里的一对字母:出口码(<see cref="ExitBits"/> 的组合,'A' + 组合)与入口位(从哪几个出口进来这张图合适)。</summary>
    public readonly struct GreedPair
    {
        public int Exits { get; }
        public int Entrance { get; }

        public GreedPair(int exits, int entrance)
        {
            Exits = exits;
            Entrance = entrance;
        }
    }

    /// <summary>
    /// 可供迷宫选用的一张房间地图的表头:路径、编号(map.lst)、[greed] 的各对字母(出口码,入口位;证据 E-070、E-113)、
    /// 类型([type])、有没有出生区、属于哪些副本([dungeon] 里的副本编号)。只读这几个标签,不加载瓦片与动画。
    /// </summary>
    public sealed class RoomCandidate
    {
        private static readonly IReadOnlyList<int> NoDungeons = new int[0];

        public string MapPath { get; }
        public int MapId { get; }

        /// <summary>[greed] 第一对字母的出口码('A' + <see cref="ExitBits"/> 的组合);没写 [greed] 的地图按 (15, 15) 登记(服务端 load_map_files 的做法,E-113)。</summary>
        public int Exits { get; }

        /// <summary>[greed] 第一对字母的入口位:从这一格的哪几个出口进来这张图合适(E-113:服务端 CMap::CheckEntrance 拿它和"进来的方向"相与)。</summary>
        public int Second { get; }

        public RoomType Type { get; }
        public bool IsBoss => Type == RoomType.Boss;
        public bool IsDummy => Type == RoomType.Dummy;
        public bool HasStartArea { get; }

        /// <summary>[greed] 的全部字母对;一张图可以登记在好几种出口码下。</summary>
        public IReadOnlyList<GreedPair> Pairs { get; }

        /// <summary>[dungeon] 里的副本编号(dungeon/dungeon.lst);没写是空表。</summary>
        public IReadOnlyList<int> DungeonIds { get; }

        public RoomCandidate(string mapPath, int mapId, int exits, int second, bool isBoss, bool hasStartArea)
            : this(mapPath, mapId, isBoss ? RoomType.Boss : RoomType.Normal, new[] { new GreedPair(exits, second) }, hasStartArea, null)
        {
        }

        public RoomCandidate(string mapPath, int mapId, RoomType type, IReadOnlyList<GreedPair> pairs, bool hasStartArea, IReadOnlyList<int> dungeonIds)
        {
            MapPath = mapPath;
            MapId = mapId;
            Type = type;
            Pairs = pairs != null && pairs.Count > 0 ? pairs : new[] { new GreedPair(15, 15) };
            Exits = Pairs[0].Exits;
            Second = Pairs[0].Entrance;
            HasStartArea = hasStartArea;
            DungeonIds = dungeonIds ?? NoDungeons;
        }

        /// <summary>这张图有没有登记在出口码 <paramref name="exits"/> 下。</summary>
        public bool HasDirection(int exits)
        {
            foreach (GreedPair p in Pairs)
            {
                if (p.Exits == exits)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>登记在出口码 <paramref name="exits"/> 下的各对的入口位之并;没有登记返回 0。</summary>
        public int EntranceFor(int exits)
        {
            int bits = 0;
            foreach (GreedPair p in Pairs)
            {
                if (p.Exits == exits)
                {
                    bits |= p.Entrance;
                }
            }
            return bits;
        }

        /// <summary>所有对的入口位之并(指定了候选地图编号的格不看出口码,只看这个)。</summary>
        public int AnyEntrance
        {
            get
            {
                int bits = 0;
                foreach (GreedPair p in Pairs)
                {
                    bits |= p.Entrance;
                }
                return bits;
            }
        }

        /// <summary>从 <paramref name="entrance"/>(出口位的组合)进来是否合适:任何一对的入口位与它相交(服务端 0x0834ecaa)。</summary>
        public bool CheckEntrance(int entrance)
        {
            return (AnyEntrance & entrance) != 0;
        }
    }

    /// <summary>
    /// 一个副本的房间候选。证据 E-113(服务端 <c>CDataManager::load_map_files</c> 0x0835fe02、<c>CDungeon::InsertMap</c> 0x0834b988):
    /// 房间地图的 [dungeon] 列出它属于的副本编号(dungeon/dungeon.lst),[greed] 的每一对 (出口码, 入口位) 都登记一次;
    /// 没写 [dungeon] 的是对战地图,没写 [greed] 的按 (15, 15) 登记。这取代了 E-070 时的"目录与副本同名"约定
    /// (全库 333 个副本里只有 111 个符合那个约定;<see cref="Read"/> 还留着它,作为副本不在 dungeon.lst 里时的兜底)。
    /// 按 map.lst 的先后排列。
    /// </summary>
    public sealed class DungeonRoomPool
    {
        public const string MapListPath = "map/map.lst";
        public const string DungeonListPath = "dungeon/dungeon.lst";

        private static readonly ConditionalWeakTable<DnfAssets, MapIndex> Indexes = new ConditionalWeakTable<DnfAssets, MapIndex>();

        private readonly Dictionary<int, RoomCandidate> _byId = new Dictionary<int, RoomCandidate>();

        public IReadOnlyList<RoomCandidate> Rooms { get; }

        public DungeonRoomPool(IReadOnlyList<RoomCandidate> rooms)
        {
            Rooms = rooms ?? new List<RoomCandidate>();
            foreach (RoomCandidate r in Rooms)
            {
                if (!_byId.ContainsKey(r.MapId))
                {
                    _byId.Add(r.MapId, r);
                }
            }
        }

        /// <summary>按地图编号取候选;不在池里返回 null。</summary>
        public RoomCandidate ById(int mapId)
        {
            return _byId.TryGetValue(mapId, out RoomCandidate r) ? r : null;
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

        /// <summary>副本定义路径在 dungeon/dungeon.lst 里的副本编号;列表里没有这条路径返回 −1。</summary>
        public static int DungeonIdOf(DnfAssets assets, string dungeonPath)
        {
            if (string.IsNullOrEmpty(dungeonPath))
            {
                return -1;
            }
            string norm = dungeonPath.Replace('\\', '/').ToLowerInvariant();
            foreach (KeyValuePair<int, string> kv in WorldMapDef.ParseList(assets.ReadScript(DungeonListPath), "dungeon/"))
            {
                if (kv.Value == norm)
                {
                    return kv.Key;
                }
            }
            return -1;
        }

        /// <summary>
        /// 读一个副本的房间候选:先按副本编号找 [dungeon] 含这个编号的房间地图(E-113);副本不在 dungeon.lst 里、或一张也没找到时,退回目录与副本同名的老约定。
        /// 第一次调用要把 map.lst 里全部地图的表头读一遍并留在 <paramref name="assets"/> 名下(之后同一副本库里的各副本直接查)。
        /// </summary>
        public static DungeonRoomPool ReadFor(DnfAssets assets, string dungeonPath)
        {
            int id = DungeonIdOf(assets, dungeonPath);
            List<int> altar = AltarMapIds(assets.ReadScript(dungeonPath));
            if (id >= 0 || altar.Count > 0)
            {
                MapIndex index = Indexes.GetValue(assets, a => new MapIndex(a));
                var rooms = new List<RoomCandidate>();
                if (id >= 0)
                {
                    rooms.AddRange(index.ForDungeon(id));
                }
                foreach (int mapId in altar)
                {
                    RoomCandidate r = index.ById(mapId);
                    if (r != null && !rooms.Contains(r))
                    {
                        rooms.Add(r);
                    }
                }
                if (rooms.Count > 0)
                {
                    return new DungeonRoomPool(rooms);
                }
            }
            return Read(assets, FolderFor(dungeonPath));
        }

        /// <summary>
        /// 进阶祭坛类副本([dungeon type] "advance altar")不用 [dungeon] 编号归池,而是在副本定义里直接写地图编号:
        /// [advance altar map] 是一串 (难度名, 地图编号, 数) 三元组,[advance altar survival map] 是一串地图编号(可重复)。按出现的先后、去重后返回(E-113)。
        /// </summary>
        internal static List<int> AltarMapIds(ScriptLine[] doc)
        {
            var ids = new List<int>();
            if (doc == null)
            {
                return ids;
            }
            foreach (ScriptLine line in doc)
            {
                bool pairs = line.TagIs("[advance altar map]");
                if (!pairs && !line.TagIs("[advance altar survival map]"))
                {
                    continue;
                }
                ScriptValue[] v = line.Values;
                for (int i = 0; i < v.Length; i++)
                {
                    bool isId = v[i].IsNumber && (!pairs || (i > 0 && v[i - 1].Kind == ScriptValueKind.String));
                    if (isId && !ids.Contains(v[i].Int))
                    {
                        ids.Add(v[i].Int);
                    }
                }
            }
            return ids;
        }

        /// <summary>
        /// 读 map.lst(无标签,值是一对对 "编号 路径",路径相对 map/),留下目录以 <paramref name="folderPrefix"/>(如 "lorien/",不分大小写)开头的地图,
        /// 逐个读表头。缺脚本的地图跳过。这是 E-070 的"同名目录"约定,现在只作兜底;<see cref="ReadFor"/> 才是按 [dungeon] 编号找。
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
            foreach ((int id, string rel) in MapEntries(lst))
            {
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
            return new DungeonRoomPool(rooms);
        }

        /// <summary>map.lst 里的 (编号, 小写正斜杠路径),按文件里的先后。</summary>
        internal static IEnumerable<(int Id, string Path)> MapEntries(ScriptLine[] lst)
        {
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
                    yield return (id, rel);
                }
            }
        }

        /// <summary>读房间地图的表头;没有地图脚本时返回 null。没写 [greed] 的地图按 (15, 15) 登记(E-113)。</summary>
        public static RoomCandidate ReadHeader(ScriptLine[] doc, string path, int id)
        {
            if (doc == null)
            {
                return null;
            }
            var letters = new List<char>();
            var dungeons = new List<int>();
            RoomType type = RoomType.Normal;
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
                    string kind = line.FirstString();
                    if (string.Equals(kind, "[boss]", StringComparison.OrdinalIgnoreCase))
                    {
                        type = RoomType.Boss;
                    }
                    else if (string.Equals(kind, "[dummy]", StringComparison.OrdinalIgnoreCase))
                    {
                        type = RoomType.Dummy;
                    }
                    else
                    {
                        type = RoomType.Normal;
                    }
                }
                else if (line.TagIs("[dungeon start area]"))
                {
                    start = true;
                }
                else if (line.TagIs("[dungeon]"))
                {
                    dungeons.Clear();
                    dungeons.AddRange(line.Ints());
                }
            }
            var pairs = new List<GreedPair>();
            for (int i = 0; i + 1 < letters.Count; i += 2)
            {
                pairs.Add(new GreedPair(letters[i] - 'A', letters[i + 1] - 'A'));
            }
            return new RoomCandidate(path, id, type, pairs, start, dungeons);
        }

        /// <summary>
        /// 出口码等于 <paramref name="exits"/> 且用途合适的候选(按 map.lst 的先后):
        /// 起点 = 有出生区的图(服务端 insertStart 不看类型);普通 = 类型为普通的图(首领图与假首领图各进各的表);首领 = 类型为首领的图。
        /// </summary>
        public List<RoomCandidate> Find(int exits, RoomRole role)
        {
            var found = new List<RoomCandidate>();
            foreach (RoomCandidate r in Rooms)
            {
                if (!r.HasDirection(exits))
                {
                    continue;
                }
                bool fits;
                switch (role)
                {
                    case RoomRole.Start:
                        fits = r.HasStartArea;
                        break;
                    case RoomRole.Boss:
                        fits = r.Type == RoomType.Boss;
                        break;
                    default:
                        fits = r.Type == RoomType.Normal;
                        break;
                }
                if (fits)
                {
                    found.Add(r);
                }
            }
            return found;
        }

        /// <summary>出口码等于 <paramref name="exits"/> 的假首领图(没有真首领图可用时,首领格从这里挑,E-113)。</summary>
        public List<RoomCandidate> FindDummy(int exits)
        {
            var found = new List<RoomCandidate>();
            foreach (RoomCandidate r in Rooms)
            {
                if (r.Type == RoomType.Dummy && r.HasDirection(exits))
                {
                    found.Add(r);
                }
            }
            return found;
        }

        /// <summary>map.lst 里全部地图的表头,按 [dungeon] 里的副本编号分好;一个 <see cref="DnfAssets"/> 只建一次。</summary>
        private sealed class MapIndex
        {
            private readonly Dictionary<int, List<RoomCandidate>> _byDungeon = new Dictionary<int, List<RoomCandidate>>();
            private readonly Dictionary<int, RoomCandidate> _byId = new Dictionary<int, RoomCandidate>();

            public MapIndex(DnfAssets assets)
            {
                ScriptLine[] lst = assets.ReadScript(MapListPath);
                if (lst == null)
                {
                    throw new DnfFormatException("PVF 里没有 " + MapListPath);
                }
                foreach ((int id, string rel) in MapEntries(lst))
                {
                    string path = "map/" + rel;
                    RoomCandidate room = ReadHeader(assets.ReadScript(path), path, id);
                    if (room == null)
                    {
                        continue;
                    }
                    if (!_byId.ContainsKey(id))
                    {
                        _byId.Add(id, room);
                    }
                    foreach (int d in room.DungeonIds)
                    {
                        if (!_byDungeon.TryGetValue(d, out List<RoomCandidate> list))
                        {
                            list = new List<RoomCandidate>();
                            _byDungeon.Add(d, list);
                        }
                        list.Add(room);
                    }
                }
            }

            /// <summary>map.lst 里这个编号的地图表头(不论它属于哪个副本);没有返回 null。</summary>
            public RoomCandidate ById(int mapId)
            {
                return _byId.TryGetValue(mapId, out RoomCandidate r) ? r : null;
            }

            public IReadOnlyList<RoomCandidate> ForDungeon(int dungeonId)
            {
                return _byDungeon.TryGetValue(dungeonId, out List<RoomCandidate> list) ? list : (IReadOnlyList<RoomCandidate>)new RoomCandidate[0];
            }
        }
    }

    /// <summary>
    /// 一次副本的布局:迷宫(<see cref="DungeonMaze"/>)+ 每一格用哪张房间地图 + 起点格与首领格。
    /// 选图规则(证据 E-070,在基线整局的六个房间上核对过;E-113 按服务端读码补全):
    /// <list type="bullet">
    /// <item>出口码为 0(字母 A)的格是空的,不在迷宫里、没有房间(<see cref="RoomAt"/> 返回 null);</item>
    /// <item>格的出口组合 = 地图 [greed] 的某一对的出口码;起点格从 [start map] 列出的格里挑、要有起点候选;首领格从 [boss map] 列出的格里挑、要有首领候选(没有真首领图就用假首领图);其余格用普通图;</item>
    /// <item>[map specification] 给某格指定了候选地图编号时,那一格只在这些里挑(首领格看 [boss map specification]);</item>
    /// <item>起点格同时也是首领格(单格副本)时,起点房间就是首领房间;</item>
    /// <item>同一格有多张候选时,不给随机数就取最前面的(基线里就是这样选的:起点格选 s2、其邻格选 s3 而不是同码的 bn12、首领格选 b2),给了随机数就随机挑;
    /// 入口位能覆盖这一格全部出口的图排在前面(原版是进房间时按进来的方向现挑,这里提前定下,所以先挑哪个方向进都合适的)。</item>
    /// </list>
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

        /// <summary>这一格的房间;空格(出口码为 0)或迷宫外返回 null。</summary>
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
        /// (起点格、首领格也在各自候选格里随机挑)。
        /// 起点格与首领格的组合若会让别的格没有候选可用(例如首领格之外的某一格没有对应出口码的普通房间),就换下一个组合;
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

            // 起点格:[start map] 列出的格(没有列表时取每一格)里,有起点候选的格;空格不算
            List<(int X, int Y)> starts = CellsWithCandidates(maze, maze.StartCells, true, (x, y) => pool.Find(maze.ExitsAt(x, y), RoomRole.Start));
            if (starts.Count == 0)
            {
                throw new DnfFormatException("迷宫 " + maze.Path + " 的起点格里没有可用的起点房间(" + StartDiagnostics(maze, pool) + ")");
            }
            // 首领格:[boss map] 列出的格里,有首领候选的格(单格副本里起点格自己也算);没有就没有首领
            List<(int X, int Y)> bosses = CellsWithCandidates(maze, maze.BossCells, false, (x, y) =>
            {
                List<RoomCandidate> list = BossCandidates(maze, pool, x, y);
                if (list.Count == 0 && starts.Contains((x, y)))
                {
                    list = pool.Find(maze.ExitsAt(x, y), RoomRole.Start);
                }
                return list;
            });
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
                    if (bx == sx && by == sy && BossCandidates(maze, pool, bx, by).Count > 0 && bosses.Count > 1)
                    {
                        continue; // 起点格有真首领图可挑、另有别的首领格:起点格不兼首领格
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
            rooms[sy * maze.Cols + sx] = Pick(pool.Find(maze.ExitsAt(sx, sy), RoomRole.Start), maze.ExitsAt(sx, sy), false, random);
            if (bx >= 0 && !(bx == sx && by == sy))
            {
                rooms[by * maze.Cols + bx] = Pick(BossCandidates(maze, pool, bx, by), maze.ExitsAt(bx, by), HasBossSpec(maze, bx, by), random);
            }
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    if (rooms[y * maze.Cols + x] != null || maze.IsEmptyCell(x, y))
                    {
                        continue;
                    }
                    List<RoomCandidate> list = NormalCandidates(maze, pool, x, y);
                    if (list.Count == 0)
                    {
                        error = "迷宫 " + maze.Path + " 的格 (" + x + "," + y + ") 没有出口组合为 " + (char)('A' + maze.ExitsAt(x, y)) + " 的普通房间";
                        return null;
                    }
                    rooms[y * maze.Cols + x] = Pick(list, maze.ExitsAt(x, y), maze.MapSpecAt(x, y) != null, random);
                }
            }
            return new DungeonLayout(maze, rooms, sx, sy, bx, by);
        }

        /// <summary>起点格选不出房间时的说明:起点格各自的出口码、池里的图数、有出生区的图数与它们登记的出口码,方便看是哪一环对不上。</summary>
        private static string StartDiagnostics(DungeonMaze maze, DungeonRoomPool pool)
        {
            var cellCodes = new SortedSet<char>();
            int considered = 0;
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    bool listed = maze.StartCells.Count == 0 ? !maze.IsEmptyCell(x, y) : maze.IsStartCell(x, y);
                    if (listed)
                    {
                        considered++;
                        cellCodes.Add((char)('A' + maze.ExitsAt(x, y)));
                    }
                }
            }
            int withStart = 0;
            var roomCodes = new SortedSet<char>();
            foreach (RoomCandidate r in pool.Rooms)
            {
                if (!r.HasStartArea)
                {
                    continue;
                }
                withStart++;
                foreach (GreedPair p in r.Pairs)
                {
                    roomCodes.Add((char)('A' + p.Exits));
                }
            }
            return "起点格 " + considered + " 个,出口码 {" + string.Join(",", cellCodes) + "};房间候选 " + pool.Rooms.Count + " 张,有出生区 " + withStart
                + " 张,出口码 {" + string.Join(",", roomCodes) + "}";
        }

        private static bool HasBossSpec(DungeonMaze maze, int x, int y)
        {
            return maze.BossMapSpecAt(x, y) != null;
        }

        /// <summary>普通格的候选:[map specification] 指定了就只在指定的编号里(池里查得到的),否则是池里出口码对得上的普通图。</summary>
        private static List<RoomCandidate> NormalCandidates(DungeonMaze maze, DungeonRoomPool pool, int x, int y)
        {
            IReadOnlyList<int> spec = maze.MapSpecAt(x, y);
            return spec != null ? FromIds(pool, spec) : pool.Find(maze.ExitsAt(x, y), RoomRole.Normal);
        }

        /// <summary>首领格的候选:[boss map specification] 指定了就只在指定的编号里,否则是出口码对得上的首领图,再没有就用假首领图。</summary>
        private static List<RoomCandidate> BossCandidates(DungeonMaze maze, DungeonRoomPool pool, int x, int y)
        {
            IReadOnlyList<int> spec = maze.BossMapSpecAt(x, y);
            if (spec != null)
            {
                return FromIds(pool, spec);
            }
            List<RoomCandidate> list = pool.Find(maze.ExitsAt(x, y), RoomRole.Boss);
            return list.Count > 0 ? list : pool.FindDummy(maze.ExitsAt(x, y));
        }

        private static List<RoomCandidate> FromIds(DungeonRoomPool pool, IReadOnlyList<int> ids)
        {
            var list = new List<RoomCandidate>();
            foreach (int id in ids)
            {
                RoomCandidate r = pool.ById(id);
                if (r != null)
                {
                    list.Add(r);
                }
            }
            return list;
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

        private static List<(int X, int Y)> CellsWithCandidates(DungeonMaze maze, IReadOnlyList<CellRect> rects, bool emptyMeansAll,
            Func<int, int, List<RoomCandidate>> candidates)
        {
            var cells = new List<(int, int)>();
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    // 没有列表(取每一格)时跳过空格;列出来的起点格、首领格即使出口码是 A 也是真房间(单格副本的格出口码就是 A,E-113)
                    if (rects.Count == 0 && maze.IsEmptyCell(x, y))
                    {
                        continue;
                    }
                    bool inside = rects.Count == 0 ? emptyMeansAll : false;
                    foreach (CellRect r in rects)
                    {
                        if (r.Contains(x, y))
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (inside && candidates(x, y).Count > 0)
                    {
                        cells.Add((x, y));
                    }
                }
            }
            return cells;
        }

        /// <summary>
        /// 在候选里挑一张:入口位能覆盖这一格全部出口的排在前面(按候选的先后);不给随机数取最前面的,给了就在排在最前的那一档里随机。
        /// <paramref name="bySpec"/> 为真(候选是指定的编号)时不看出口码对应的那一对,看所有对的入口位之并。
        /// </summary>
        private static RoomCandidate Pick(List<RoomCandidate> list, int exits, bool bySpec, Random random)
        {
            var full = new List<RoomCandidate>();
            foreach (RoomCandidate r in list)
            {
                int entrance = bySpec ? r.AnyEntrance : r.EntranceFor(exits);
                if ((entrance & exits) == exits)
                {
                    full.Add(r);
                }
            }
            List<RoomCandidate> pickFrom = full.Count > 0 ? full : list;
            return pickFrom[random == null ? 0 : random.Next(pickFrom.Count)];
        }
    }
}
