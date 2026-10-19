using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 无人值守的"自动打副本":每一步替主角决定按什么键。房间里有活着的怪物就走到最近的一只面前连续普攻,清完怪之后沿迷宫里到首领格的最短路走向对应的出口,
    /// 首领格清空后就停。完全确定(没有随机),命令行 dnf dungeon-check 与 Unity 播放器的 -dnfbot 跑出的是同一局;用来在没有人的时候验证出口开关、换房间与整条迷宫路线。
    /// 它只是测试工具,不代表原版的 AI,也不绕过任何官方服务。
    /// </summary>
    public sealed class DungeonBot
    {
        /// <summary>与怪物的横向距离不超过它就开始挥击(像素)。</summary>
        public const double ReachX = 52;

        /// <summary>与怪物的纵向距离不超过它才算同一条线上(像素)。</summary>
        public const double ReachY = 10;

        /// <summary>走位的死区(像素):到目标点这么近就不再动。</summary>
        public const double Deadband = 6;

        /// <summary>连续按攻击键的间隔(步)。</summary>
        public const int AttackEvery = 6;

        /// <summary>走向上下出口时,目标点放在出口线外这么远(像素):只要在出口附近就一直往外顶,由可走带的边缘挡住并触发出口。</summary>
        public const double PushBeyondEdge = 200;

        /// <summary>实时运行时一帧里最多补几个心跳(见 <see cref="Advance"/>)。</summary>
        public const int MaxBeatsPerFrame = 12;

        /// <summary>实时运行时最多欠多少毫秒的心跳:卡顿很久之后只补这么多,其余丢掉。</summary>
        public const double MaxDebtMs = 200.0;

        private readonly bool _explore;
        private double _debtMs;
        private int _steps;
        private double _lastX = double.NaN;
        private double _lastY = double.NaN;
        private int _stuck;
        private int _wiggle;

        /// <summary>
        /// <paramref name="explore"/> 为真时先把首领格以外的每一格都走进去一遍(每次走向最近的没进过的格),最后才去首领格;
        /// 为假时直接走到首领格的最短路。
        /// </summary>
        public DungeonBot(bool explore = false)
        {
            _explore = explore;
        }

        /// <summary>已经决策的步数。</summary>
        public int Steps => _steps;

        /// <summary>
        /// 从时刻 0 起按固定的 1/60 秒步长让自动机打副本,一直推进到模拟时刻不早于 <paramref name="toMs"/>,或者副本走完(首领清空 / 走出迷宫)为止;
        /// 返回停下时的模拟时刻(毫秒)。命令行 dungeon-check --bot 与播放器的 -dnfbot -dnfplayat 都是这一局,所以两边能逐帧对拍。
        /// </summary>
        public double RunUntil(DungeonSession session, double toMs)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            double t = 0;
            while (t < toMs && !session.Finished)
            {
                bool attack = Next(session, out int dx, out int dy);
                session.Update(CombatScript.StepMs, dx, dy, attack);
                t += CombatScript.StepMs;
            }
            return t;
        }

        /// <summary>
        /// 实时运行用:过去了一帧、<paramref name="deltaMs"/> 毫秒,按固定的 1/60 秒"心跳"补步——每个心跳先 <see cref="Next"/> 再让会话推进 <see cref="CombatScript.StepMs"/>,
        /// 直到欠的时间不足一个心跳(一帧最多补 <see cref="MaxBeatsPerFrame"/> 个,欠账不超过 <see cref="MaxDebtMs"/>)。返回这一帧补了几个心跳。
        /// 自动机的走位死区、出手间隔与卡住检测都是按心跳数写的,按可变的帧长直接喂给它,帧长一大(60 毫秒以上)就会在怪物旁来回晃、整座副本打不完;
        /// 按心跳补步,同一个会话不管帧长是多少、抖不抖,打出的是同一局(与 <see cref="RunUntil"/>、命令行 dnf dungeon-check --bot 一致)。
        /// </summary>
        public int Advance(DungeonSession session, double deltaMs)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            _debtMs = Math.Min(_debtMs + Math.Max(0.0, deltaMs), MaxDebtMs);
            int beats = 0;
            while (beats < MaxBeatsPerFrame && _debtMs >= CombatScript.StepMs && !session.Finished)
            {
                bool attack = Next(session, out int dx, out int dy);
                session.Update(CombatScript.StepMs, dx, dy, attack);
                _debtMs -= CombatScript.StepMs;
                beats++;
            }
            return beats;
        }

        /// <summary>读会话当前的状态,给出这一步的方向与是否按攻击键。副本已经走完时全部为 0 / false。</summary>
        public bool Next(DungeonSession session, out int dirX, out int dirY)
        {
            dirX = 0;
            dirY = 0;
            _steps++;
            if (session.Finished)
            {
                return false;
            }
            RoomCombatSession fight = session.Current;
            TownHero hero = fight.Hero;
            MonsterActor target = Nearest(fight, hero);
            bool attack = false;
            if (target != null)
            {
                double dx = target.X - hero.X;
                double dy = target.Y - hero.Y;
                dirY = Math.Abs(dy) > ReachY * 0.5 ? Math.Sign(dy) : 0;
                if (Math.Abs(dx) > ReachX)
                {
                    dirX = Math.Sign(dx);
                }
                else if (dx != 0 && (dx < 0) != hero.FacingLeft)
                {
                    dirX = Math.Sign(dx); // 背对着怪物:朝它挪一步把身子转过来
                }
                bool inLine = Math.Abs(dx) <= ReachX && Math.Abs(dy) <= ReachY;
                bool facing = dx == 0 || (dx < 0) == hero.FacingLeft;
                if (inLine && facing && _steps % AttackEvery == 0)
                {
                    attack = true;
                }
            }
            else if (fight.AliveCount == 0 && AllGone(fight))
            {
                Navigate(session, fight, hero, out dirX, out dirY);
            }
            Unstick(hero, ref dirX, ref dirY);
            return attack;
        }

        private static bool AllGone(RoomCombatSession fight)
        {
            foreach (MonsterActor m in fight.Monsters)
            {
                if (!m.IsGone)
                {
                    return false;
                }
            }
            return true;
        }

        private static MonsterActor Nearest(RoomCombatSession fight, TownHero hero)
        {
            MonsterActor best = null;
            double bestD = double.MaxValue;
            foreach (MonsterActor m in fight.Monsters)
            {
                if (!m.IsAlive)
                {
                    continue;
                }
                double d = Math.Abs(m.X - hero.X) + 4 * Math.Abs(m.Y - hero.Y);
                if (d < bestD)
                {
                    bestD = d;
                    best = m;
                }
            }
            return best;
        }

        private void Navigate(DungeonSession session, RoomCombatSession fight, TownHero hero, out int dirX, out int dirY)
        {
            dirX = 0;
            dirY = 0;
            if (!NextSide(session, _explore, out GateSide side))
            {
                return;
            }
            int[] g = fight.Room.Map.Gates[(int)side];
            if (g == null)
            {
                return;
            }
            double tx, ty;
            switch (side)
            {
                case GateSide.Left:
                    tx = g[0] - 8;
                    ty = hero.Y;
                    break;
                case GateSide.Right:
                    tx = g[0] + 8;
                    ty = hero.Y;
                    break;
                case GateSide.Up:
                    tx = g[0];
                    ty = g[1] - PushBeyondEdge;                          // 上下出口靠"顶着可走带的边缘往外走"触发:目标放在带外,一直往外顶
                    break;
                default:
                    tx = g[0];
                    ty = g[1] + PushBeyondEdge;
                    break;
            }
            // 左右出口只要 x 到位(y 不管);上下出口先对齐 x 再走 y
            if (Math.Abs(tx - hero.X) > Deadband || side == GateSide.Left || side == GateSide.Right)
            {
                dirX = tx < hero.X ? -1 : (tx > hero.X ? 1 : 0);
            }
            if (side == GateSide.Up || side == GateSide.Down)
            {
                if (Math.Abs(tx - hero.X) <= 40)
                {
                    dirY = ty < hero.Y ? -1 : (ty > hero.Y ? 1 : 0);
                }
            }
        }

        /// <summary>
        /// 下一步该走哪一侧的出口。<paramref name="explore"/> 为假:走向首领格(没有首领格就走向起点格、再走它迷宫外的出口)的最短路;
        /// 为真:先走向最近的、首领格以外没进过的格(路上不穿过首领格),都进过了再去首领格。已在目标格、或没有路时返回 false。
        /// </summary>
        public static bool NextSide(DungeonSession session, bool explore, out GateSide side)
        {
            side = GateSide.Left;
            DungeonLayout layout = session.Layout;
            DungeonMaze maze = layout.Maze;
            int cols = maze.Cols;
            bool pending = false;
            if (explore)
            {
                for (int y = 0; y < maze.Rows && !pending; y++)
                {
                    for (int x = 0; x < cols && !pending; x++)
                    {
                        pending = !session.IsVisited(x, y) && !layout.IsBossCell(x, y);
                    }
                }
            }
            int start = session.CellY * cols + session.CellX;
            var from = new int[cols * maze.Rows];
            var via = new GateSide[cols * maze.Rows];
            int goal = FindGoal(session, pending, start, from, via);
            if (goal < 0 && pending)
            {
                goal = FindGoal(session, false, start, from, via); // 没进过的格只能穿过首领格才到:直接去首领格
            }
            if (goal < 0)
            {
                return false;
            }
            if (goal == start)
            {
                // 已在目标格:有迷宫外的出口就走它,没有就停
                foreach (GateSide s in new[] { GateSide.Right, GateSide.Up, GateSide.Left, GateSide.Down })
                {
                    if (maze.HasExit(session.CellX, session.CellY, s) && !maze.TryNeighbor(session.CellX, session.CellY, s, out _, out _))
                    {
                        side = s;
                        return true;
                    }
                }
                return false;
            }
            int step = goal;
            while (from[step] != start)
            {
                step = from[step];
            }
            side = via[step];
            return true;
        }

        /// <summary>
        /// 宽度优先找目标格:<paramref name="pending"/> 为真时目标是最近的、首领格以外没进过的格,路上不穿过首领格;否则目标是首领格(没有首领格就是起点格本身)。
        /// 返回目标格的序号,找不到返回 −1;<paramref name="from"/> 记每格的上一格(起点为 −1),<paramref name="via"/> 记走进这一格用的出口侧。
        /// </summary>
        private static int FindGoal(DungeonSession session, bool pending, int start, int[] from, GateSide[] via)
        {
            DungeonLayout layout = session.Layout;
            DungeonMaze maze = layout.Maze;
            int cols = maze.Cols;
            for (int i = 0; i < from.Length; i++)
            {
                from[i] = -2;
            }
            from[start] = -1;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % cols, cy = cur / cols;
                bool atGoal = pending
                    ? cur != start && !session.IsVisited(cx, cy) && !layout.IsBossCell(cx, cy)
                    : IsGoal(layout, cx, cy, cur != start);
                if (atGoal)
                {
                    return cur;
                }
                foreach (GateSide s in new[] { GateSide.Right, GateSide.Up, GateSide.Left, GateSide.Down })
                {
                    if (maze.TryNeighbor(cx, cy, s, out int nx, out int ny) && from[ny * cols + nx] == -2)
                    {
                        if (pending && layout.IsBossCell(nx, ny))
                        {
                            continue;
                        }
                        from[ny * cols + nx] = cur;
                        via[ny * cols + nx] = s;
                        queue.Enqueue(ny * cols + nx);
                    }
                }
            }
            return -1;
        }

        private static bool IsGoal(DungeonLayout layout, int x, int y, bool notStart)
        {
            if (layout.HasBoss)
            {
                return layout.IsBossCell(x, y);
            }
            return !notStart; // 没有首领格:目标就是起点格(单格副本),走它迷宫外的出口
        }

        /// <summary>想动却几乎没动(被障碍物卡住)超过一小会儿,就垂直方向挪一阵再试。</summary>
        private void Unstick(TownHero hero, ref int dirX, ref int dirY)
        {
            bool moved = double.IsNaN(_lastX) || Math.Abs(hero.X - _lastX) > 0.2 || Math.Abs(hero.Y - _lastY) > 0.2;
            _lastX = hero.X;
            _lastY = hero.Y;
            if ((dirX != 0 || dirY != 0) && !moved)
            {
                _stuck++;
            }
            else
            {
                _stuck = 0;
            }
            if (_stuck > 30)
            {
                _wiggle = 40;
                _stuck = 0;
            }
            if (_wiggle > 0)
            {
                _wiggle--;
                dirY = (_steps / 40) % 2 == 0 ? 1 : -1;
            }
        }
    }
}
