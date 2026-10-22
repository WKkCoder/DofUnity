using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 在一间房间里替自动机找路:把房间按 <see cref="Cell"/> 像素分格,每格问一次"主角站得下吗"(可走区域 + 障碍),
    /// 再从目标出发做多源最短路(八个方向,斜着走要求两边的格子都能走,免得贴着墙角蹭过去),得到每格到目标的代价。
    /// 贴着墙或门体边界 2 像素以内的格子走一步的代价是 <see cref="NarrowCost"/>、其余格子是 1:路线尽量走在离边界远的地方
    /// (主角的位置是小数、一步走三像素,贴着边界走会在"格子里能走、主角真身却卡着"之间来回抖),整条路只有窄缝时才从窄缝里挤过去。
    /// 自动机顺着代价下降的方向走,遇到整条可走带被障碍或悬崖隔开时就能绕路(高地图里的出口常在很远的另一层上)。
    /// 只是测试工具用的寻路,不代表原版的移动规则;主角本身的走位仍由 <see cref="TownHero"/> 逐像素检查。
    /// </summary>
    public sealed class WalkPathfinder
    {
        /// <summary>格子边长(像素):瓦片通行格宽 16、高 12,都是它的整数倍;取 2 是为了不漏掉障碍物脚印与可走带之间只剩两三像素的窄缝(主角真会被挤进去)。</summary>
        public const int Cell = 2;

        /// <summary>"窄"格(自己或上下左右相邻的一格站不下)走一步的代价;宽格是 1。</summary>
        public const int NarrowCost = 8;

        private static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] StepY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        private readonly bool[] _free;
        private readonly bool[] _wide;
        private readonly int _cols;
        private readonly int _rows;

        /// <summary>格子数(列 × 行);可走区域不限制时为 0(不需要寻路)。</summary>
        public int CellCount => _free == null ? 0 : _cols * _rows;

        /// <summary>
        /// 按房间的可走区域与障碍建格子。<paramref name="widthPixels"/>、<paramref name="heightPixels"/> 是要覆盖的范围(世界像素,从 0 起)。
        /// </summary>
        public WalkPathfinder(IWalkArea walk, IHeroBlocker blocker, int widthPixels, int heightPixels)
        {
            if (walk == null)
            {
                throw new ArgumentNullException(nameof(walk));
            }
            _cols = Math.Max(1, widthPixels / Cell + 1);
            _rows = Math.Max(1, heightPixels / Cell + 1);
            if (walk.IsUnrestricted)
            {
                _free = null;
                _wide = null;
                return;
            }
            _free = new bool[_cols * _rows];
            for (int gy = 0; gy < _rows; gy++)
            {
                for (int gx = 0; gx < _cols; gx++)
                {
                    double x = gx * Cell, y = gy * Cell;
                    _free[gy * _cols + gx] = walk.Contains(x, y) && (blocker == null || blocker.BlockedBy(x, y) == null);
                }
            }
            _wide = new bool[_cols * _rows];
            for (int gy = 1; gy < _rows - 1; gy++)
            {
                for (int gx = 1; gx < _cols - 1; gx++)
                {
                    int i = gy * _cols + gx;
                    _wide[i] = _free[i] && _free[i - 1] && _free[i + 1] && _free[i - _cols] && _free[i + _cols];
                }
            }
        }

        /// <summary>世界坐标 (x, y) 所在的格子站不站得下(格子中心那一点)。范围之外按站不下。</summary>
        public bool IsOpen(double x, double y)
        {
            if (_free == null)
            {
                return true;
            }
            int gx = (int)Math.Round(x / Cell), gy = (int)Math.Round(y / Cell);
            return gx >= 0 && gx < _cols && gy >= 0 && gy < _rows && _free[gy * _cols + gx];
        }

        /// <summary>
        /// 以矩形 [x0, x1] × [y0, y1](世界像素)里所有能站的格子为目标,算每格到目标的代价(目标格为 0,够不到的格子为 −1)。
        /// 目标矩形里没有能站的格子时返回全 −1。
        /// </summary>
        public int[] Field(double x0, double y0, double x1, double y1)
        {
            double lx = Math.Min(x0, x1), hx = Math.Max(x0, x1), ly = Math.Min(y0, y1), hy = Math.Max(y0, y1);
            return Field((x, y) => x >= lx && x <= hx && y >= ly && y <= hy);
        }

        /// <summary>
        /// 以所有"能站、且 <paramref name="isGoal"/>(格子中心的世界坐标)返回 true"的格子为目标,算每格到目标的代价(目标格为 0,够不到的格子为 −1)。
        /// 目标不是一个方块时用它(例如"往门这边挪几像素就压到门体"的那一圈格子:门洞是楔形的,贴着门体的格子不在一条直线上)。
        /// 目标里有"宽"格时只用宽格当目标(站在离边界远一点的地方顶着走更稳),一个宽格都没有才用窄格。
        /// </summary>
        public int[] Field(Func<double, double, bool> isGoal)
        {
            if (isGoal == null)
            {
                throw new ArgumentNullException(nameof(isGoal));
            }
            int count = _cols * _rows;
            var dist = new int[count];
            for (int i = 0; i < count; i++)
            {
                dist[i] = -1;
            }
            if (_free == null)
            {
                return dist;
            }
            var seeds = new List<int>();
            bool anyWide = false;
            for (int gy = 0; gy < _rows; gy++)
            {
                for (int gx = 0; gx < _cols; gx++)
                {
                    int i = gy * _cols + gx;
                    if (_free[i] && isGoal(gx * Cell, gy * Cell))
                    {
                        seeds.Add(i);
                        anyWide |= _wide[i];
                    }
                }
            }
            // Dial 算法:代价只有 1 与 NarrowCost 两种,用 NarrowCost + 1 个循环的桶就够了
            int bucketCount = NarrowCost + 1;
            var buckets = new Queue<int>[bucketCount];
            for (int i = 0; i < bucketCount; i++)
            {
                buckets[i] = new Queue<int>();
            }
            int pending = 0;
            foreach (int seed in seeds)
            {
                if (anyWide && !_wide[seed])
                {
                    continue;
                }
                dist[seed] = 0;
                buckets[0].Enqueue(seed);
                pending++;
            }
            for (int level = 0; pending > 0; level++)
            {
                Queue<int> queue = buckets[level % bucketCount];
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    pending--;
                    if (dist[cur] != level)
                    {
                        continue;   // 过期的条目:这格后来被更便宜的路线更新过
                    }
                    int cx = cur % _cols, cy = cur / _cols;
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = cx + StepX[d], ny = cy + StepY[d];
                        if (nx < 0 || nx >= _cols || ny < 0 || ny >= _rows)
                        {
                            continue;
                        }
                        int ni = ny * _cols + nx;
                        if (!_free[ni])
                        {
                            continue;
                        }
                        if (d >= 4 && (!_free[cy * _cols + nx] || !_free[ny * _cols + cx]))
                        {
                            continue;   // 斜着走:两边都得能走
                        }
                        int next = level + (_wide[ni] ? 1 : NarrowCost);
                        if (dist[ni] < 0 || next < dist[ni])
                        {
                            dist[ni] = next;
                            buckets[next % bucketCount].Enqueue(ni);
                            pending++;
                        }
                    }
                }
            }
            return dist;
        }

        /// <summary>
        /// 从 (x, y) 顺着 <paramref name="field"/>(<see cref="Field(Func{double, double, bool})"/> 的结果)往目标走最多 <paramref name="lookahead"/> 格,返回走到的那个格子中心。
        /// <paramref name="cost"/> 是起步那一格到目标的代价(0 = 已经站在目标格上;这时路点就是这一格)。主角所在的格子站不下(落在两格之间、贴着墙、窄缝里)时,
        /// 起步格是周围 5×5 格里代价最小的那一格(同代价取近的)。起步格到不了目标、周围也没有能到的格子时返回 false(cost = −1)。
        /// 到没到由调用方按 <paramref name="cost"/> 判断:主角一步能走三像素,目标是窄窄一列格子时会一步跨过去,不能要求恰好踩在目标格上。
        /// </summary>
        public bool TryWaypoint(int[] field, double x, double y, int lookahead, out double wx, out double wy, out int cost)
        {
            wx = x;
            wy = y;
            cost = -1;
            int cx = (int)Math.Round(x / Cell), cy = (int)Math.Round(y / Cell);
            int best = -1, bestDist = int.MaxValue;
            int own = cx >= 0 && cx < _cols && cy >= 0 && cy < _rows ? field[cy * _cols + cx] : -1;
            if (own >= 0)
            {
                best = cy * _cols + cx;
                bestDist = own;
            }
            else
            {
                int bestNear = int.MaxValue;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || nx >= _cols || ny < 0 || ny >= _rows)
                        {
                            continue;
                        }
                        int v = field[ny * _cols + nx];
                        int near = dx * dx + dy * dy;
                        if (v >= 0 && (v < bestDist || (v == bestDist && near < bestNear)))
                        {
                            bestDist = v;
                            bestNear = near;
                            best = ny * _cols + nx;
                        }
                    }
                }
            }
            if (best < 0)
            {
                return false;
            }
            cost = bestDist;
            int cur = best;
            for (int step = 0; step < lookahead && field[cur] > 0; step++)
            {
                int bx = cur % _cols, by = cur / _cols;
                int next = cur, nextDist = field[cur];
                for (int d = 0; d < 8; d++)
                {
                    int nx = bx + StepX[d], ny = by + StepY[d];
                    if (nx < 0 || nx >= _cols || ny < 0 || ny >= _rows)
                    {
                        continue;
                    }
                    int ni = ny * _cols + nx;
                    if (field[ni] >= 0 && field[ni] < nextDist)
                    {
                        if (d >= 4 && (!_free[by * _cols + nx] || !_free[ny * _cols + bx]))
                        {
                            continue;
                        }
                        next = ni;
                        nextDist = field[ni];
                    }
                }
                if (next == cur)
                {
                    break;
                }
                cur = next;
            }
            wx = (cur % _cols) * Cell;
            wy = (cur / _cols) * Cell;
            return true;
        }

        /// <summary>(x, y) 所在格到目标的代价;够不到或在范围之外为 −1。</summary>
        public int DistanceAt(int[] field, double x, double y)
        {
            int gx = (int)Math.Round(x / Cell), gy = (int)Math.Round(y / Cell);
            return gx >= 0 && gx < _cols && gy >= 0 && gy < _rows ? field[gy * _cols + gx] : -1;
        }
    }
}
