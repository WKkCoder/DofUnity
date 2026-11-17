using System;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网房间里"走到出口去"的走位(r18-hostmulti):<see cref="HostedRoomBot"/> 打完房间里的怪之后,朝通向下一间房的出口走,直到触发换房
    /// (<see cref="HostedRoomRun"/> 用与离线同一个判定 <see cref="DungeonSession.HeroTriggers"/> 看有没有触发)。
    /// <para>
    /// 这是 <see cref="DungeonBot"/> 里"走向出口"那一段(<c>Navigate</c>、<c>GateGoal</c>、<c>GateEntryY</c>、<c>UsePath</c>、<c>SteerByPath</c>、<c>Unstick</c>)的<b>移植</b>:
    /// 目标点怎么取(左右出口顶着门体 / 落在识别矩形里、上下出口往边缘外顶)、直线走不近(<see cref="DungeonBot.StallSteps"/> 步没变近、
    /// 或想动却挪不动 <see cref="DungeonBot.ExitStuckSteps"/> 步)就改走寻路、到了出口前面只顶着出口走、卡住摆脱的上下晃,常数一律引用 <see cref="DungeonBot"/> 的,一个数没改。
    /// 之所以是移植而不是直接复用:离线那一段要一个迷宫会话(<see cref="DungeonSession"/>)来决定去哪一侧,联网房间没有迷宫会话,
    /// 而 <c>DungeonBot.cs</c> 不在本线的改动范围里(离线自动机的行为不能动);两处逻辑今后应当合并成一份(记在交接的 openItems 里)。
    /// 只有出口位置(房间地图的 [pathgate pos])的那一侧才走得了;没有时 <see cref="Next"/> 返回 false。
    /// </para>
    /// 一个实例只管一局(换房间时自己清掉上一间的寻路状态);不是线程安全的,只在宿主主线程用。
    /// </summary>
    public sealed class HostedRoomDoorWalker
    {
        private RoomCombatSession _room;
        private WalkPathfinder _finder;
        private int[] _field;
        private string _fieldKey;
        private string _arrivedKey;
        private double _arrivedX;
        private double _arrivedY;
        private string _watchKey;
        private double _bestDist;
        private int _noProgress;
        private string _latchedKey;
        private double _lastX = double.NaN;
        private double _lastY = double.NaN;
        private int _stuck;
        private int _wiggle;
        private int _steps;

        /// <summary>已经走过几步(<see cref="Next"/> 被叫了几次,卡住摆脱的上下晃按它交替)。</summary>
        public int Steps => _steps;

        /// <summary>寻路接管过几次(直线走不近时);诊断用。</summary>
        public int PathEngaged { get; private set; }

        /// <summary>寻路接管时找不到路的步数;诊断用,不为 0 说明出口前面够不着。</summary>
        public int PathBlockedSteps { get; private set; }

        /// <summary>
        /// 朝 <paramref name="side"/> 侧的出口走一步:方向写进 <paramref name="dirX"/>、<paramref name="dirY"/>(取值 -1、0、1)。
        /// 房间地图没有这一侧出口的位置(没法走过去)时方向为 0 并返回 false;否则返回 true(方向可能是 0:已经站在出口前面不用动)。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="fight"/> 为 null。</exception>
        public bool Next(RoomCombatSession fight, GateSide side, out int dirX, out int dirY)
        {
            if (fight == null)
            {
                throw new ArgumentNullException(nameof(fight));
            }
            dirX = 0;
            dirY = 0;
            if (!ReferenceEquals(fight, _room))
            {
                Reset(fight);
            }
            int[] g = fight.Room.Map.Gates[(int)side];
            if (g == null)
            {
                return false;
            }
            _steps++;
            TownHero hero = fight.Hero;
            Navigate(fight, hero, side, g, out dirX, out dirY);
            Unstick(hero, ref dirX, ref dirY);
            return true;
        }

        private void Reset(RoomCombatSession fight)
        {
            _room = fight;
            _finder = null;
            _field = null;
            _fieldKey = null;
            _arrivedKey = null;
            _watchKey = null;
            _latchedKey = null;
            _lastX = double.NaN;
            _lastY = double.NaN;
            _stuck = 0;
            _wiggle = 0;
        }

        private void Navigate(RoomCombatSession fight, TownHero hero, GateSide side, int[] g, out int dirX, out int dirY)
        {
            dirX = 0;
            dirY = 0;
            double tx, ty;
            switch (side)
            {
                case GateSide.Left:
                    tx = g[0] - 8;
                    ty = GateEntryY(fight, hero, side, g[1]);
                    break;
                case GateSide.Right:
                    tx = g[0] + 8;
                    ty = GateEntryY(fight, hero, side, g[1]);
                    break;
                case GateSide.Up:
                    tx = g[0];
                    ty = g[1] - DungeonBot.PushBeyondEdge;                  // 上下出口靠"顶着可走带的边缘往外走"触发:目标放在带外,一直往外顶
                    break;
                default:
                    tx = g[0];
                    ty = g[1] + DungeonBot.PushBeyondEdge;
                    break;
            }
            // 出口走不近(隔着障碍、悬崖或另一层的可走带):改走寻路,走到出口前面那一带;最后几步仍由下面的"顶着出口走"完成
            string pathKey = "g" + (int)side;
            if (UsePath(pathKey, Math.Abs(tx - hero.X) + Math.Abs(ty - hero.Y)))
            {
                int pathX = 0, pathY = 0;
                if (SteerByPath(fight, hero, pathKey, GateGoal(fight, hero, side, g), ref pathX, ref pathY))
                {
                    dirX = pathX;
                    dirY = pathY;
                    return;
                }
                if (_arrivedKey == pathKey)
                {
                    // 到了出口前面:只顶着出口走(左右出口不再动 y、上下出口不再动 x),别又被下面的直线逻辑带歪
                    dirX = side == GateSide.Left ? -1 : (side == GateSide.Right ? 1 : 0);
                    dirY = side == GateSide.Up ? -1 : (side == GateSide.Down ? 1 : 0);
                    return;
                }
            }
            // 左右出口:x 一直往出口顶,y 不在出口识别矩形里时顺带挪进去(门体只盖在出口那一段 y 上,在别处顶着墙进不了门);上下出口先对齐 x 再走 y
            if (Math.Abs(tx - hero.X) > DungeonBot.Deadband || side == GateSide.Left || side == GateSide.Right)
            {
                dirX = tx < hero.X ? -1 : (tx > hero.X ? 1 : 0);
            }
            if ((side == GateSide.Left || side == GateSide.Right) && Math.Abs(ty - hero.Y) > 1.5)
            {
                dirY = ty < hero.Y ? -1 : 1;
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
        /// 对 <paramref name="key"/> 这个目标,<paramref name="dist"/>(离目标的距离)连续 <see cref="DungeonBot.StallSteps"/> 步没有变近就返回 true,
        /// 此后对同一个目标一直返回 true(直到换房间)。想动却挪不动 <see cref="DungeonBot.ExitStuckSteps"/> 步也立刻改走寻路。距离在变近时返回 false:照旧直线走。
        /// </summary>
        private bool UsePath(string key, double dist)
        {
            if (_latchedKey == key)
            {
                return true;
            }
            if (_stuck >= DungeonBot.ExitStuckSteps)
            {
                _latchedKey = key;
                PathEngaged++;
                return true;
            }
            if (_watchKey != key)
            {
                _watchKey = key;
                _bestDist = dist;
                _noProgress = 0;
                return false;
            }
            if (dist < _bestDist - 1.0)
            {
                _bestDist = dist;
                _noProgress = 0;
            }
            else
            {
                _noProgress++;
            }
            if (_noProgress >= DungeonBot.StallSteps)
            {
                _latchedKey = key;
                PathEngaged++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 顺着寻路往 <paramref name="goal"/> 走一步:把方向写进 dirX、dirY 并返回 true。主角已经在目标里、或这间房里到不了目标时返回 false(方向不动);
        /// 到不了的步数记在 <see cref="PathBlockedSteps"/>。出口不会动,同一个 <paramref name="key"/> 下目标表只算一遍。
        /// </summary>
        private bool SteerByPath(RoomCombatSession fight, TownHero hero, string key, Func<double, double, bool> goal, ref int dirX, ref int dirY)
        {
            if (_finder == null)
            {
                RoomMap map = fight.Room.Map;
                _finder = new WalkPathfinder(hero.Walk, hero.Blocker, map.Width + 32, map.HeightPixels + 32);
                _field = null;
                _fieldKey = null;
            }
            if (_finder.CellCount == 0)
            {
                return false;
            }
            if (_field == null || _fieldKey != key)
            {
                _field = _finder.Field(goal);
                _arrivedKey = null;
                _fieldKey = key;
            }
            if (!_finder.TryWaypoint(_field, hero.X, hero.Y, DungeonBot.PathLookahead, out double wx, out double wy, out int cost))
            {
                PathBlockedSteps++;
                return false;
            }
            if (_arrivedKey == key)
            {
                // 到过目标:顶着出口走的最后几步交给直线逻辑,别又被拽回格子中心(顶门要连着顶一小会儿才触发);走出一小段才重新接管
                if (Math.Abs(hero.X - _arrivedX) <= DungeonBot.ArriveSlack && Math.Abs(hero.Y - _arrivedY) <= DungeonBot.ArriveSlack)
                {
                    return false;
                }
                _arrivedKey = null;
            }
            if (cost <= DungeonBot.ArriveCost)
            {
                _arrivedKey = key;
                _arrivedX = hero.X;
                _arrivedY = hero.Y;
                return false;
            }
            int stepX = Math.Abs(wx - hero.X) > 0.5 ? (wx < hero.X ? -1 : 1) : 0;   // 窄道只比格子中心偏 1~2 像素也得挪过去,不然一直顶着墙
            int stepY = Math.Abs(wy - hero.Y) > 0.5 ? (wy < hero.Y ? -1 : 1) : 0;
            if (_stuck >= DungeonBot.NudgeAfter)
            {
                // 想往路点挪却连着几步没挪动:格子中心附近的障碍边界不一定正好在格子边上,差零点几像素就是过不去。朝路点那一侧推一下
                if (stepX == 0 && wx != hero.X)
                {
                    stepX = wx < hero.X ? -1 : 1;
                }
                if (stepY == 0 && wy != hero.Y)
                {
                    stepY = wy < hero.Y ? -1 : 1;
                }
            }
            if (stepX == 0 && stepY == 0)
            {
                return false;
            }
            dirX = stepX;
            dirY = stepY;
            return true;
        }

        /// <summary>
        /// 寻路去出口时的目标(与 <see cref="DungeonBot"/> 同一套):有门体的左右出口取"往门这一侧挪十二像素占位盒就压到门体"的那些格子;
        /// 没有门体(或门体不在出口那一行)的左右出口取离出口横向 <see cref="DungeonSession.TriggerReachSide"/> 像素以内、纵向在识别矩形里的整条带;
        /// 上下出口取占位盒压到识别矩形的那些格子。
        /// </summary>
        private static Func<double, double, bool> GateGoal(RoomCombatSession fight, TownHero hero, GateSide side, int[] g)
        {
            RoomGate gate = null;
            foreach (RoomGate candidate in fight.Room.Content.Gates)
            {
                if (candidate.Side == side)
                {
                    gate = candidate;
                    break;
                }
            }
            if ((side == GateSide.Left || side == GateSide.Right) && gate != null && gate.BodyReachesGate(hero.BodyWidth, hero.BodyDepth))
            {
                GateBody body = gate.Body;
                double shift = side == GateSide.Left ? -12.0 : 12.0;   // 离门体 12 像素以内的一带(主角一步走三四像素,目标太窄会一步跨过去)
                int width = hero.BodyWidth, depth = hero.BodyDepth;
                // 上下各 3 像素也得压到门体:主角的 y 是小数,站在门体上沿的一条线上顶着走,差一像素就从门体上面或下面滑过去了
                return (x, y) => body.Overlaps(x + shift, y, width, depth) && body.Overlaps(x + shift, y - 3, width, depth) && body.Overlaps(x + shift, y + 3, width, depth);
            }
            WorldRect area = gate != null ? gate.Area : GateAreas.For(side, g[0], g[1]);
            switch (side)
            {
                case GateSide.Left:
                    {
                        // 朝出口走时只有 x 在动:纵向(y)要离矩形上下沿留出余量,否则落在边界上顶着走永远不触发。
                        // 出口位置落在可走区域外(走不到离出口十二像素以内)时,顶在识别矩形里的可走区域边界上也算
                        double limit = g[0] + DungeonSession.TriggerReachSide;
                        double top = area.Y + DungeonBot.AreaMargin, bottom = area.Y + area.Height - DungeonBot.AreaMargin;
                        double colLo = area.X, colHi = area.X + area.Width;
                        IWalkArea walk = hero.Walk;
                        IHeroBlocker blocker = hero.Blocker;
                        return (x, y) => y >= top && y < bottom && (x <= limit || (x >= colLo && x < colHi && !Open(walk, blocker, x - 2, y)));
                    }
                case GateSide.Right:
                    {
                        double limit = g[0] - DungeonSession.TriggerReachSide;
                        double top = area.Y + DungeonBot.AreaMargin, bottom = area.Y + area.Height - DungeonBot.AreaMargin;
                        double colLo = area.X, colHi = area.X + area.Width;
                        IWalkArea walk = hero.Walk;
                        IHeroBlocker blocker = hero.Blocker;
                        return (x, y) => y >= top && y < bottom && (x >= limit || (x >= colLo && x < colHi && !Open(walk, blocker, x + 2, y)));
                    }
                default:
                    {
                        // 上下出口:占位盒压到识别矩形,站在这里朝出口方向走就触发(不要求被边缘挡住)。朝出口走时只有 y 在动:横向(x)留出余量
                        int width = hero.BodyWidth, depth = hero.BodyDepth;
                        var inner = new WorldRect(area.X + DungeonBot.AreaMargin, area.Y, Math.Max(1, area.Width - 2 * DungeonBot.AreaMargin), area.Height);
                        return (x, y) => GateAreas.OverlapsBox(inner, (int)Math.Floor(x), (int)Math.Floor(y), width, depth);
                    }
            }
        }

        private static bool Open(IWalkArea walk, IHeroBlocker blocker, double x, double y)
        {
            return walk.Contains(x, y) && (blocker == null || blocker.BlockedBy(x, y) == null);
        }

        /// <summary>
        /// 左右出口:主角的占位盒真能碰到门体的那一行 y。门体只盖在出口位置那一行上下的几行(门洞是楔形的,上面的走廊是通的),
        /// 站在别的行顶着走不会触发;从出口位置的 y 起往上下各找,第一个门体盖得到占位盒的 y 再加 1 像素的余量。找不到就用出口位置的 y。
        /// </summary>
        private static double GateEntryY(RoomCombatSession fight, TownHero hero, GateSide side, double fallbackY)
        {
            foreach (RoomGate gate in fight.Room.Content.Gates)
            {
                if (gate.Side != side)
                {
                    continue;
                }
                if (!gate.BodyReachesGate(hero.BodyWidth, hero.BodyDepth))
                {
                    return gate.Y;
                }
                for (int d = 0; d <= DungeonBot.GateRowReach; d++)
                {
                    if (gate.Body.Overlaps(gate.X, gate.Y + d, hero.BodyWidth, hero.BodyDepth))
                    {
                        return gate.Y + d + 1;
                    }
                    if (gate.Body.Overlaps(gate.X, gate.Y - d, hero.BodyWidth, hero.BodyDepth))
                    {
                        return gate.Y - d + 1;
                    }
                }
                return gate.Y;
            }
            return fallbackY;
        }

        /// <summary>
        /// 想动却几乎没动(被障碍物卡住)超过一小会儿,就垂直方向挪一阵再试。挥击、命中停顿、受击、倒地的动画锁着人时本来就动不了,不算"卡住"。
        /// </summary>
        private void Unstick(TownHero hero, ref int dirX, ref int dirY)
        {
            bool moved = double.IsNaN(_lastX) || Math.Abs(hero.X - _lastX) > 0.2 || Math.Abs(hero.Y - _lastY) > 0.2;
            _lastX = hero.X;
            _lastY = hero.Y;
            if ((dirX != 0 || dirY != 0) && !moved)
            {
                if (!hero.IsAttacking && !hero.IsHurt && !hero.IsDown)
                {
                    _stuck++;
                }
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
