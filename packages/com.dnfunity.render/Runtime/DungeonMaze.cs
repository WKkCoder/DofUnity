using System;
using System.Collections.Generic;
using System.Text;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 副本迷宫格里的四个出口位。副本定义(.dgn)的 [greed] 每格有两个字母,第一个字母是 'A' + 这四位的组合,
    /// 表示这一格的房间有哪几个方向的出口(证据 E-070:全库 146 个多格迷宫里,24 种"方向对应位"的取法只有这一种让每条相邻格的边两侧一致)。
    /// </summary>
    public static class ExitBits
    {
        public const int Right = 1;
        public const int Up = 2;
        public const int Left = 4;
        public const int Down = 8;

        public static int Of(GateSide side)
        {
            switch (side)
            {
                case GateSide.Right: return Right;
                case GateSide.Up: return Up;
                case GateSide.Left: return Left;
                default: return Down;
            }
        }

        public static GateSide Opposite(GateSide side)
        {
            switch (side)
            {
                case GateSide.Left: return GateSide.Right;
                case GateSide.Right: return GateSide.Left;
                case GateSide.Up: return GateSide.Down;
                default: return GateSide.Up;
            }
        }

        /// <summary>从一格走出某个方向时,格坐标的变化:右 +x、左 −x、上 −y(第 0 行在最上)、下 +y。</summary>
        public static void Step(GateSide side, out int dx, out int dy)
        {
            dx = side == GateSide.Right ? 1 : (side == GateSide.Left ? -1 : 0);
            dy = side == GateSide.Down ? 1 : (side == GateSide.Up ? -1 : 0);
        }
    }

    /// <summary>迷宫格的矩形(含两端):副本定义的 [start map]、[boss map] 里每四个数 (x0, y0, x1, y1) 是一个。</summary>
    public readonly struct CellRect
    {
        public int X0 { get; }
        public int Y0 { get; }
        public int X1 { get; }
        public int Y1 { get; }

        public CellRect(int x0, int y0, int x1, int y1)
        {
            X0 = Math.Min(x0, x1);
            Y0 = Math.Min(y0, y1);
            X1 = Math.Max(x0, x1);
            Y1 = Math.Max(y0, y1);
        }

        public bool Contains(int x, int y)
        {
            return x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
        }
    }

    /// <summary>
    /// 副本定义(.dgn)里的迷宫:[maze info] 的 [size](列数、行数)、[greed](每格两个字母,行优先)、[start map] 与 [boss map](起点格与首领格的候选格),
    /// 以及 [pathgate object](出口物件编号,在 <see cref="PathGateSet"/> 里)。
    /// 第一个字母给出每格的出口(<see cref="ExitBits"/>),0(字母 A)表示这一格是空的、不在迷宫里;第二个字母的含义没有研究(在副本定义里总与第一个相同)。证据 E-070。
    /// <para>
    /// E-113(服务端读码更正):[start map]、[boss map] 里的数是**一对对的格坐标** (x, y) 的列表,不是矩形(E-070 在洛兰上量的两格横排,两种读法碰巧相同);
    /// 一个 .dgn 可以有多个 [maze info] 段落,段落里的 [quest connection] 写了任务迷宫类型(没写 = −1 = 默认迷宫);
    /// [map specification] x y 编号… 与 [boss map specification] x y 编号… 给个别格指定候选房间的地图编号。
    /// </para>
    /// </summary>
    public sealed class DungeonMaze
    {
        private readonly int[] _exits;
        private readonly int[] _second;
        private readonly Dictionary<int, int[]> _mapSpecs;
        private readonly Dictionary<int, int[]> _bossMapSpecs;

        public string Path { get; }
        public int Cols { get; }
        public int Rows { get; }

        /// <summary>起点格候选:每一对 (x, y) 一个只含这一格的 <see cref="CellRect"/>(E-113)。</summary>
        public IReadOnlyList<CellRect> StartCells { get; }

        /// <summary>首领格候选:每一对 (x, y) 一个只含这一格的 <see cref="CellRect"/>(E-113)。</summary>
        public IReadOnlyList<CellRect> BossCells { get; }

        /// <summary>
        /// 这个段落的 [quest connection] 类型:−1 = 没写(默认迷宫;服务端开局只在这些里随机挑一个),0、1 = 任务迷宫(接了对应任务才用,我们不用)。证据 E-113。
        /// </summary>
        public int QuestConnection { get; }

        /// <summary>出口物件编号;副本定义里没有 [pathgate object] 时为 null。</summary>
        public PathGateSet GateSet { get; }

        /// <summary>小地图房间格图包路径(由 .dgn 的 [minimap image] 转成小写 "sprite/…",证据 E-109);没写时为 null。</summary>
        public string MinimapImage { get; }

        /// <summary>
        /// 副本的经验权重(.dgn 的 [experience increasing point],一个浮点数,写成整数也按浮点读;证据 E-111):击杀经验的一个乘数。
        /// 没写这个标签时是 1(服务端读副本脚本前先把这个字段清成 1.0)。数值是原版数据,不入库。
        /// </summary>
        public float ExpWeight { get; }

        public DungeonMaze(string path, int cols, int rows, int[] exits, int[] second, IReadOnlyList<CellRect> startCells,
            IReadOnlyList<CellRect> bossCells, PathGateSet gateSet, string minimapImage = null, float expWeight = 1f,
            int questConnection = -1, IDictionary<int, int[]> mapSpecs = null, IDictionary<int, int[]> bossMapSpecs = null)
        {
            ExpWeight = expWeight;
            QuestConnection = questConnection;
            _mapSpecs = mapSpecs == null ? new Dictionary<int, int[]>() : new Dictionary<int, int[]>(mapSpecs);
            _bossMapSpecs = bossMapSpecs == null ? new Dictionary<int, int[]>() : new Dictionary<int, int[]>(bossMapSpecs);
            if (cols < 1 || rows < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(cols), "迷宫至少一格");
            }
            if (exits == null || exits.Length != cols * rows)
            {
                throw new ArgumentException("每格都要有出口编码", nameof(exits));
            }
            Path = path;
            Cols = cols;
            Rows = rows;
            _exits = (int[])exits.Clone();
            _second = second != null && second.Length == exits.Length ? (int[])second.Clone() : (int[])exits.Clone();
            StartCells = startCells ?? new List<CellRect>();
            BossCells = bossCells ?? new List<CellRect>();
            GateSet = gateSet;
            MinimapImage = NormalizeMinimapImage(minimapImage);
        }

        /// <summary>脚本里写的图名("Map/MiniMap/Act1.img")转成图包名("sprite/map/minimap/act1.img",证据 E-109 0x012fe559)。</summary>
        public static string NormalizeMinimapImage(string scriptImage)
        {
            if (string.IsNullOrWhiteSpace(scriptImage))
            {
                return null;
            }
            string clean = scriptImage.Trim().Replace('\\', '/').ToLowerInvariant();
            return clean.StartsWith("sprite/", StringComparison.Ordinal) ? clean : "sprite/" + clean;
        }

        public bool InBounds(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Cols && y < Rows;
        }

        /// <summary>这一格的出口位组合(<see cref="ExitBits"/>);格不在迷宫里时为 0。</summary>
        public int ExitsAt(int x, int y)
        {
            return InBounds(x, y) ? _exits[y * Cols + x] : 0;
        }

        /// <summary>[greed] 每格的第二个字母(0 到 25);含义没有研究。</summary>
        public int SecondCodeAt(int x, int y)
        {
            return InBounds(x, y) ? _second[y * Cols + x] : 0;
        }

        /// <summary>这一格是空的(出口码 A = 0):不在迷宫里,没有房间、谁也走不进去(服务端的校验函数遇到出口码为 0 的格直接跳过,E-113)。</summary>
        public bool IsEmptyCell(int x, int y)
        {
            return InBounds(x, y) && _exits[y * Cols + x] == 0;
        }

        /// <summary>这一格的 [map specification](指定的候选房间地图编号);没写返回 null。</summary>
        public IReadOnlyList<int> MapSpecAt(int x, int y)
        {
            return InBounds(x, y) && _mapSpecs.TryGetValue(y * Cols + x, out int[] ids) && ids.Length > 0 ? ids : null;
        }

        /// <summary>这一格的 [boss map specification](首领格指定的候选房间地图编号);没写返回 null。</summary>
        public IReadOnlyList<int> BossMapSpecAt(int x, int y)
        {
            return InBounds(x, y) && _bossMapSpecs.TryGetValue(y * Cols + x, out int[] ids) && ids.Length > 0 ? ids : null;
        }

        public bool HasExit(int x, int y, GateSide side)
        {
            return (ExitsAt(x, y) & ExitBits.Of(side)) != 0;
        }

        public bool IsStartCell(int x, int y)
        {
            foreach (CellRect r in StartCells)
            {
                if (r.Contains(x, y))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsBossCell(int x, int y)
        {
            foreach (CellRect r in BossCells)
            {
                if (r.Contains(x, y))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>从 (x, y) 走某个出口到的邻格;这一侧没有出口、或邻格在迷宫外时返回 false。</summary>
        public bool TryNeighbor(int x, int y, GateSide side, out int nx, out int ny)
        {
            ExitBits.Step(side, out int dx, out int dy);
            nx = x + dx;
            ny = y + dy;
            return HasExit(x, y, side) && InBounds(nx, ny);
        }

        /// <summary>每条相邻格的边两侧是否一致(一格的右出口 ⇔ 右邻格的左出口,下出口 ⇔ 下邻格的上出口)。</summary>
        public bool IsConsistent()
        {
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Cols; x++)
                {
                    if (x + 1 < Cols && HasExit(x, y, GateSide.Right) != HasExit(x + 1, y, GateSide.Left))
                    {
                        return false;
                    }
                    if (y + 1 < Rows && HasExit(x, y, GateSide.Down) != HasExit(x, y + 1, GateSide.Up))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>一个 [maze info] 段落里读到的原料(还没检查完整)。</summary>
        private sealed class Section
        {
            public int Cols, Rows;
            public int QuestConnection = -1;
            public readonly StringBuilder Letters = new StringBuilder();
            public readonly List<CellRect> Start = new List<CellRect>();
            public readonly List<CellRect> Boss = new List<CellRect>();
            public readonly List<(int X, int Y, int[] Ids)> MapSpecs = new List<(int, int, int[])>();
            public readonly List<(int X, int Y, int[] Ids)> BossMapSpecs = new List<(int, int, int[])>();
        }

        /// <summary>
        /// 解析副本定义里**所有**迷宫段落(一个 [maze info] 一个;没有写 [maze info] 标签、直接写 [size] 等的老格式算一个段落)。
        /// 没有 [size]、或 [greed] 里的字母数不是 列 × 行 × 2 的段落丢掉(全库有一个 .dgn 的 [greed] 是空的)。字母之外的字符(空格、换行)忽略;大小写不分。
        /// 出口物件、小地图图包、经验权重是副本级的标签,各段落共用。证据 E-070、E-113(服务端 0x088e5faa 读 [maze info] 与 [quest connection] 的段落规则)。
        /// </summary>
        public static List<DungeonMaze> ParseAll(string path, ScriptLine[] doc)
        {
            var mazes = new List<DungeonMaze>();
            if (doc == null)
            {
                return mazes;
            }
            PathGateSet gates = null;
            string minimapImage = null;
            float expWeight = 1f;
            var sections = new List<Section>();
            Section cur = null;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[experience increasing point]"))
                {
                    double? weight = line.FirstNumber();
                    if (weight.HasValue)
                    {
                        expWeight = (float)weight.Value;
                    }
                    continue;
                }
                if (line.TagIs("[pathgate object]"))
                {
                    List<int> n = line.Ints();
                    gates = n.Count == PathGateSet.Count ? new PathGateSet(n) : null;
                    continue;
                }
                if (line.TagIs("[minimap image]"))
                {
                    string img = line.FirstString();
                    if (!string.IsNullOrEmpty(img))
                    {
                        minimapImage = img;
                    }
                    continue;
                }
                if (line.TagIs("[maze info]"))
                {
                    cur = new Section();
                    sections.Add(cur);
                    continue;
                }
                bool sectionTag = line.TagIs("[size]") || line.TagIs("[greed]") || line.TagIs("[start map]") || line.TagIs("[boss map]")
                    || line.TagIs("[map specification]") || line.TagIs("[boss map specification]") || line.TagIs("[quest connection]");
                if (!sectionTag)
                {
                    continue;
                }
                if (cur == null)
                {
                    cur = new Section();
                    sections.Add(cur);
                }
                if (line.TagIs("[size]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count >= 2)
                    {
                        cur.Cols = n[0];
                        cur.Rows = n[1];
                    }
                }
                else if (line.TagIs("[greed]"))
                {
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
                                cur.Letters.Append(c);
                            }
                            else if (c >= 'a' && c <= 'z')
                            {
                                cur.Letters.Append((char)(c - 'a' + 'A'));
                            }
                        }
                    }
                }
                else if (line.TagIs("[start map]"))
                {
                    AddCells(line.Ints(), cur.Start);
                }
                else if (line.TagIs("[boss map]"))
                {
                    AddCells(line.Ints(), cur.Boss);
                }
                else if (line.TagIs("[quest connection]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count >= 1)
                    {
                        cur.QuestConnection = n[0];
                    }
                }
                else
                {
                    // [map specification] x y 编号… / [boss map specification] x y 编号…:同一格再写一次会整个换掉(服务端先清空再读)
                    List<int> n = line.Ints();
                    if (n.Count >= 2)
                    {
                        var ids = new int[n.Count - 2];
                        for (int i = 2; i < n.Count; i++)
                        {
                            ids[i - 2] = n[i];
                        }
                        (line.TagIs("[map specification]") ? cur.MapSpecs : cur.BossMapSpecs).Add((n[0], n[1], ids));
                    }
                }
            }
            foreach (Section s in sections)
            {
                if (s.Cols < 1 || s.Rows < 1 || s.Letters.Length != s.Cols * s.Rows * 2)
                {
                    continue;
                }
                var first = new int[s.Cols * s.Rows];
                var second = new int[s.Cols * s.Rows];
                for (int i = 0; i < first.Length; i++)
                {
                    first[i] = s.Letters[i * 2] - 'A';
                    second[i] = s.Letters[i * 2 + 1] - 'A';
                }
                mazes.Add(new DungeonMaze(path, s.Cols, s.Rows, first, second, s.Start, s.Boss, gates, minimapImage, expWeight,
                    s.QuestConnection, SpecsByCell(s.MapSpecs, s.Cols, s.Rows), SpecsByCell(s.BossMapSpecs, s.Cols, s.Rows)));
            }
            return mazes;
        }

        /// <summary>
        /// 解析副本定义,取第一张默认迷宫(<see cref="QuestConnection"/> 为 −1);没有可用的迷宫返回 null。
        /// </summary>
        public static DungeonMaze Parse(string path, ScriptLine[] doc)
        {
            foreach (DungeonMaze m in ParseAll(path, doc))
            {
                if (m.QuestConnection == -1)
                {
                    return m;
                }
            }
            return null;
        }

        /// <summary>
        /// 读副本定义的默认迷宫。有多张默认迷宫(服务端 <c>GetRandMaze</c> 开局随机挑一张)时:<paramref name="random"/> 为 null 取第一张,否则随机挑。
        /// </summary>
        public static DungeonMaze Read(DnfAssets assets, string path, Random random = null)
        {
            var defaults = new List<DungeonMaze>();
            foreach (DungeonMaze m in ParseAll(path, assets.ReadScript(path)))
            {
                if (m.QuestConnection == -1)
                {
                    defaults.Add(m);
                }
            }
            if (defaults.Count == 0)
            {
                return null;
            }
            return defaults.Count == 1 || random == null ? defaults[0] : defaults[random.Next(defaults.Count)];
        }

        /// <summary>按格下标(行 × 列数 + 列)整理指定表;同一格写了多次以最后一次为准(服务端每次先清空再读);坐标在迷宫外的丢掉。</summary>
        private static Dictionary<int, int[]> SpecsByCell(List<(int X, int Y, int[] Ids)> specs, int cols, int rows)
        {
            var byCell = new Dictionary<int, int[]>();
            foreach ((int x, int y, int[] ids) in specs)
            {
                if (x >= 0 && y >= 0 && x < cols && y < rows)
                {
                    byCell[y * cols + x] = ids;
                }
            }
            return byCell;
        }

        /// <summary>一对对的 (x, y) → 只含这一格的 <see cref="CellRect"/>;多出来的一个数丢掉。</summary>
        private static void AddCells(List<int> n, List<CellRect> into)
        {
            for (int i = 0; i + 1 < n.Count; i += 2)
            {
                into.Add(new CellRect(n[i], n[i + 1], n[i], n[i + 1]));
            }
        }
    }
}
