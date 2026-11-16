using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>联网多房间副本:拿服务端起始地图通知里的迷宫格去对本地迷宫(<see cref="HostedRoomMazePlan.Align"/>)的结果。</summary>
    public enum HostedMazeStatus
    {
        /// <summary>至少有一个候选迷宫解释得了这一格(格在迷宫里、不是空格、起点房间是起点格、地图的出口码对得上)。</summary>
        Aligned = 0,

        /// <summary>宿主没有给任何候选迷宫(副本定义读不出、只有任务迷宫)。</summary>
        NoMaze = 1,

        /// <summary>这一格不在任何候选迷宫里(越界、空格,起始房间还要求是 [start map] 里的起点格)。</summary>
        CellMismatch = 2,

        /// <summary>格在,但服务端给的房间地图没有登记在这一格的出口码下(服务端按出口码给格选图,所以这个迷宫不是服务端用的那个)。</summary>
        MapMismatch = 3,
    }

    /// <summary>从当前格往下走什么(<see cref="HostedRoomMazePlan.Plan"/>)。</summary>
    public enum HostedMazeNext
    {
        /// <summary>没有下一间了:当前格就是首领格(或首领格都走过了):这一间是最后一间,清图与结算沿用单房间的路。</summary>
        Last = 0,

        /// <summary>往某个出口走到某个邻格(<see cref="HostedMazeStep.Side"/>、<see cref="HostedMazeStep.TargetX"/>、<see cref="HostedMazeStep.TargetY"/>)。</summary>
        Step = 1,

        /// <summary>当前格不是首领格,又走不到任何还没走过的首领格:本地迷宫跟服务端对不上。</summary>
        Unreachable = 2,

        /// <summary>候选迷宫各说各的(走的方向不同,或有的说到头了有的说没到):不敢选。</summary>
        Ambiguous = 3,
    }

    /// <summary>"下一步往哪走"的结论。</summary>
    public readonly struct HostedMazeStep
    {
        public HostedMazeStep(HostedMazeNext kind, GateSide side = GateSide.Left, int targetX = 0, int targetY = 0)
        {
            Kind = kind;
            Side = side;
            TargetX = targetX;
            TargetY = targetY;
        }

        public HostedMazeNext Kind { get; }

        /// <summary>要走的出口在哪一侧(只有 <see cref="HostedMazeNext.Step"/> 时有意义)。</summary>
        public GateSide Side { get; }

        /// <summary>走到的邻格的格坐标(只有 <see cref="HostedMazeNext.Step"/> 时有意义)。</summary>
        public int TargetX { get; }

        public int TargetY { get; }

        public bool IsStep => Kind == HostedMazeNext.Step;

        internal bool Same(HostedMazeStep other)
        {
            return Kind == other.Kind && (Kind != HostedMazeNext.Step || (Side == other.Side && TargetX == other.TargetX && TargetY == other.TargetY));
        }
    }

    /// <summary>
    /// 联网多房间副本的本地迷宫对位与路线规划(r18-hostmulti):服务端只告诉客户端"现在在迷宫的哪一格、这间房的怪是什么"(起始地图通知里的格坐标与地图编号),
    /// 没告诉客户端迷宫长什么样;而本地要在这一间房里摆对的出口、告诉主角往哪个门走,就得知道"下一格是哪一格、从哪一侧出口出去"。
    /// 这个答案只能来自本地副本定义(.dgn)里的迷宫,于是有两件事要做,都是不碰任何数据文件、不读随机数的纯函数:
    /// <list type="number">
    /// <item><see cref="Align"/>:在候选迷宫里(一个 .dgn 可以有好几个默认迷宫,服务端开局随机挑一个,客户端不知道是哪个)筛出<b>解释得了</b>这一格的那些:
    /// 格在迷宫里且不是空格;起始房间还得是 [start map] 里的起点格;给了地图编号时,服务端按出口码给格选图(E-113),所以这张图必须登记在这一格的出口码下
    /// (指定了候选地图的格 [map specification] 例外,不看出口码)。一个也解释不了就返回原因,宿主据此不进多房间(退回单房间,什么也不多做)。</item>
    /// <item><see cref="Plan"/>:从当前格出发,朝最近的、还没走过的首领格走一步(广度优先,同长度的路按右、上、左、下的固定次序,绕开走过的格);候选迷宫的结论必须一致才算数。
    /// 走到首领格(或已知这张图是首领房间)就是最后一间。<b>走不通时不猜</b>:找不到路是 <see cref="HostedMazeNext.Unreachable"/>,候选各说各话是 <see cref="HostedMazeNext.Ambiguous"/>。</item>
    /// </list>
    /// 首领格是 [boss map] 里列的<b>候选</b>格(离线副本开局随机挑一个当真首领,服务端怎么挑的没有核对),所以规划只是"朝一个候选格走";真正的最后一间由服务端说了算(只在它那里清图),
    /// 走错的代价是到了一个不是首领的房间,而本地走完这间还会往下一个候选格走,没有死路;反过来绝不会在还有候选格没走过时提前认定"到头了"。
    /// 格坐标与服务端是同一套(列、行,第 0 行在最上,出口位的方向取法见 <see cref="ExitBits"/>)。
    /// </summary>
    public static class HostedRoomMazePlan
    {
        // 同长度的路按这个次序选(固定,不读随机数;结果与候选迷宫的先后无关)
        private static readonly GateSide[] Order = { GateSide.Right, GateSide.Up, GateSide.Left, GateSide.Down };

        /// <summary>
        /// 在候选迷宫里筛出解释得了 (<paramref name="cellX"/>, <paramref name="cellY"/>) 这一格的那些(顺序不变);一个也没有时 <paramref name="status"/> 写明哪一关挡下的。
        /// </summary>
        /// <param name="mazes">候选迷宫(null 与其中的 null 项当作没有)。</param>
        /// <param name="cellX">服务端起始地图通知里的格坐标。</param>
        /// <param name="cellY">服务端起始地图通知里的格坐标。</param>
        /// <param name="mapId">这间房的地图编号;不知道(通知里没有)给负数,不查出口码。</param>
        /// <param name="atStart">是不是副本的第一间房(起点房间必须落在 [start map] 的起点格上;没有列起点格时每个非空格都行)。</param>
        /// <param name="mapAllowsExits">(地图编号, 出口码)→ 这张图是不是登记在这个出口码下;地图不认识就该返回 true(不拿没有的数据否决迷宫);null 不查。</param>
        public static IReadOnlyList<DungeonMaze> Align(IReadOnlyList<DungeonMaze> mazes, int cellX, int cellY, int mapId, bool atStart,
            Func<int, int, bool> mapAllowsExits, out HostedMazeStatus status)
        {
            var kept = new List<DungeonMaze>();
            int candidates = 0;
            int inCell = 0;
            if (mazes != null)
            {
                foreach (DungeonMaze maze in mazes)
                {
                    if (maze == null)
                    {
                        continue;
                    }
                    candidates++;
                    if (!maze.InBounds(cellX, cellY) || maze.IsEmptyCell(cellX, cellY))
                    {
                        continue;
                    }
                    if (atStart && maze.StartCells.Count > 0 && !maze.IsStartCell(cellX, cellY))
                    {
                        continue;
                    }
                    inCell++;
                    bool specified = maze.MapSpecAt(cellX, cellY) != null || maze.BossMapSpecAt(cellX, cellY) != null;
                    if (mapAllowsExits != null && mapId >= 0 && !specified && !mapAllowsExits(mapId, maze.ExitsAt(cellX, cellY)))
                    {
                        continue;
                    }
                    kept.Add(maze);
                }
            }
            status = candidates == 0 ? HostedMazeStatus.NoMaze : kept.Count > 0 ? HostedMazeStatus.Aligned : inCell == 0 ? HostedMazeStatus.CellMismatch : HostedMazeStatus.MapMismatch;
            return kept;
        }

        /// <summary>
        /// 从 (<paramref name="cellX"/>, <paramref name="cellY"/>) 往哪走。<paramref name="visited"/> 里的格(走过的,包括当前格)不再走;
        /// <paramref name="bossMap"/> 为真(已知这间房的地图是首领房间)直接是最后一间。候选迷宫的结论不一致是 <see cref="HostedMazeNext.Ambiguous"/>。
        /// </summary>
        public static HostedMazeStep Plan(IReadOnlyList<DungeonMaze> mazes, int cellX, int cellY, ISet<(int X, int Y)> visited, bool bossMap)
        {
            if (mazes == null || mazes.Count == 0)
            {
                return new HostedMazeStep(HostedMazeNext.Unreachable);
            }
            HostedMazeStep? agreed = null;
            foreach (DungeonMaze maze in mazes)
            {
                if (maze == null)
                {
                    continue;
                }
                HostedMazeStep one = PlanOne(maze, cellX, cellY, visited, bossMap);
                if (!agreed.HasValue)
                {
                    agreed = one;
                }
                else if (!agreed.Value.Same(one))
                {
                    return new HostedMazeStep(HostedMazeNext.Ambiguous);
                }
            }
            return agreed ?? new HostedMazeStep(HostedMazeNext.Unreachable);
        }

        private static HostedMazeStep PlanOne(DungeonMaze maze, int x, int y, ISet<(int X, int Y)> visited, bool bossMap)
        {
            if (bossMap)
            {
                return new HostedMazeStep(HostedMazeNext.Last);
            }
            // 广度优先:走过的格当墙;队列里每格记着"从当前格出发的第一步"
            var blocked = new HashSet<(int, int)> { (x, y) };
            if (visited != null)
            {
                foreach ((int X, int Y) cell in visited)
                {
                    blocked.Add((cell.X, cell.Y));
                }
            }
            var queue = new Queue<(int X, int Y, GateSide First, int FirstX, int FirstY)>();
            foreach (GateSide side in Order)
            {
                Enqueue(maze, blocked, queue, x, y, side, null);
            }
            while (queue.Count > 0)
            {
                (int cx, int cy, GateSide first, int fx, int fy) = queue.Dequeue();
                if (maze.IsBossCell(cx, cy))
                {
                    return new HostedMazeStep(HostedMazeNext.Step, first, fx, fy);
                }
                foreach (GateSide side in Order)
                {
                    Enqueue(maze, blocked, queue, cx, cy, side, (first, fx, fy));
                }
            }
            return new HostedMazeStep(maze.IsBossCell(x, y) ? HostedMazeNext.Last : HostedMazeNext.Unreachable);
        }

        private static void Enqueue(DungeonMaze maze, HashSet<(int, int)> blocked, Queue<(int X, int Y, GateSide First, int FirstX, int FirstY)> queue,
            int x, int y, GateSide side, (GateSide Side, int X, int Y)? first)
        {
            if (!maze.TryNeighbor(x, y, side, out int nx, out int ny) || maze.IsEmptyCell(nx, ny) || !blocked.Add((nx, ny)))
            {
                return;
            }
            if (first.HasValue)
            {
                queue.Enqueue((nx, ny, first.Value.Side, first.Value.X, first.Value.Y));
            }
            else
            {
                queue.Enqueue((nx, ny, side, nx, ny));
            }
        }
    }
}
