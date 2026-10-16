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
    /// 副本定义(.dgn)里的迷宫:[maze info] 的 [size](列数、行数)、[greed](每格两个字母,行优先)、[start map] 与 [boss map](起点格与首领格的候选矩形),
    /// 以及 [pathgate object](出口物件编号,在 <see cref="PathGateSet"/> 里)。
    /// 第一个字母给出每格的出口(<see cref="ExitBits"/>);第二个字母的含义没有研究(在副本定义里总与第一个相同)。证据 E-070。
    /// </summary>
    public sealed class DungeonMaze
    {
        private readonly int[] _exits;
        private readonly int[] _second;

        public string Path { get; }
        public int Cols { get; }
        public int Rows { get; }
        public IReadOnlyList<CellRect> StartCells { get; }
        public IReadOnlyList<CellRect> BossCells { get; }

        /// <summary>出口物件编号;副本定义里没有 [pathgate object] 时为 null。</summary>
        public PathGateSet GateSet { get; }

        public DungeonMaze(string path, int cols, int rows, int[] exits, int[] second, IReadOnlyList<CellRect> startCells,
            IReadOnlyList<CellRect> bossCells, PathGateSet gateSet)
        {
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

        /// <summary>
        /// 解析副本定义。没有 [size]、或 [greed] 里的字母数不是 列 × 行 × 2 时返回 null(全库有一个 .dgn 的 [greed] 是空的)。
        /// 字母之外的字符(空格、换行)忽略;大小写不分。
        /// </summary>
        public static DungeonMaze Parse(string path, ScriptLine[] doc)
        {
            if (doc == null)
            {
                return null;
            }
            int cols = 0, rows = 0;
            var letters = new StringBuilder();
            var start = new List<CellRect>();
            var boss = new List<CellRect>();
            PathGateSet gates = null;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[size]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count >= 2)
                    {
                        cols = n[0];
                        rows = n[1];
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
                                letters.Append(c);
                            }
                            else if (c >= 'a' && c <= 'z')
                            {
                                letters.Append((char)(c - 'a' + 'A'));
                            }
                        }
                    }
                }
                else if (line.TagIs("[start map]"))
                {
                    AddRects(line.Ints(), start);
                }
                else if (line.TagIs("[boss map]"))
                {
                    AddRects(line.Ints(), boss);
                }
                else if (line.TagIs("[pathgate object]"))
                {
                    List<int> n = line.Ints();
                    gates = n.Count == PathGateSet.Count ? new PathGateSet(n) : null;
                }
            }
            if (cols < 1 || rows < 1 || letters.Length != cols * rows * 2)
            {
                return null;
            }
            var first = new int[cols * rows];
            var second = new int[cols * rows];
            for (int i = 0; i < cols * rows; i++)
            {
                first[i] = letters[i * 2] - 'A';
                second[i] = letters[i * 2 + 1] - 'A';
            }
            return new DungeonMaze(path, cols, rows, first, second, start, boss, gates);
        }

        public static DungeonMaze Read(DnfAssets assets, string path)
        {
            return Parse(path, assets.ReadScript(path));
        }

        private static void AddRects(List<int> n, List<CellRect> into)
        {
            for (int i = 0; i + 3 < n.Count; i += 4)
            {
                into.Add(new CellRect(n[i], n[i + 1], n[i + 2], n[i + 3]));
            }
        }
    }
}
