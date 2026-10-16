using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>怎样造出迷宫里某一格的房间战斗会话(房间的出口已按这一格的邻居摆好、怪物已摆好)。真实的见 <see cref="DungeonSession.CreateAssetFactory"/>;测试里换成合成的。</summary>
    public delegate RoomCombatSession DungeonRoomFactory(int cellX, int cellY);

    /// <summary>
    /// 一次副本:迷宫(<see cref="DungeonLayout"/>)里的一格一格房间,主角在房间里战斗,房间里的怪物都死光之后出口打开,走进出口就换到相邻的房间。证据 E-070。
    /// 做了:
    ///   · 出口开关:进房间时有怪物,出口关着;最后一只怪物消失(死亡动画播完)的那一刻出口开始淡出打开(基线上量到打开时刻 ≈ 目标死亡标记后 0.5 秒);
    ///     进房间时本来就没有怪物(或回到清空的房间)出口直接开着。
    ///   · 换房间:主角走到打开的出口前,从相邻房间的对侧出口进去;左右出口:出口 x 向房间里偏移(左门 +74、右门 −70 像素,基线量到),y 与朝向保持,
    ///     上下出口:出口 y 向房间里偏移 24 像素(没量过)。房间会话按格缓存,回去时怪物不会复活(基线上走回第一个房间时就是清空的)。
    ///   · 首领格清空 → <see cref="BossCleared"/>;迷宫外的出口(只有单格副本的右边有)走进去 → <see cref="ExitReached"/>。
    ///   · 屏幕右上角的占位小地图。
    /// 没做 / 没量过:出口的触发范围(用的是近似:左右出口 24 像素、上下出口 12 像素且横向 80 像素以内)、关着的出口是不是挡路、换房间的转场效果与时长(现在是瞬间切换)、
    /// 清房时站姿切换的确切时刻、结算界面。
    /// </summary>
    public sealed class DungeonSession : IActionSession
    {
        /// <summary>从左边的出口进来:出口 x 向右的偏移(像素;基线上量到 74,证据 E-070)。</summary>
        public const int EntryOffsetLeftGate = 74;

        /// <summary>从右边的出口进来:出口 x 向左的偏移(像素;基线上量到 70)。</summary>
        public const int EntryOffsetRightGate = 70;

        /// <summary>从上下出口进来:出口 y 向房间里的偏移(像素;没有量过,占位)。</summary>
        public const int EntryOffsetVertical = 24;

        /// <summary>左右出口的触发范围:主角离出口的横向距离不超过它(像素;近似,没有量过)。</summary>
        public const int TriggerReachSide = 24;

        /// <summary>上下出口的触发范围:主角离出口的纵向距离不超过它(像素;近似)。</summary>
        public const int TriggerReachVertical = 12;

        /// <summary>上下出口的触发范围:主角与出口的横向距离不超过它(像素;近似)。</summary>
        public const int TriggerSpanVertical = 80;

        public const int MiniMapMarginRight = 10;
        public const int MiniMapTop = 30;

        private sealed class CellState
        {
            public int X;
            public int Y;
            public RoomCombatSession Session;
            public bool Visited;
            public bool Cleared;

            /// <summary>进房间时本来就没有怪物:出口直接开着,不播淡出。</summary>
            public bool Settled;

            public double ClearedAtMs;
            public bool GatesOpened;
        }

        private readonly DungeonLayout _layout;
        private readonly DungeonRoomFactory _factory;
        private readonly CellState[] _cells;
        private readonly OverlayArt _art = new OverlayArt();
        private double _clockMs;

        public DungeonLayout Layout => _layout;
        public DungeonMaze Maze => _layout.Maze;

        /// <summary>主角所在的格。</summary>
        public int CellX { get; private set; }

        public int CellY { get; private set; }

        /// <summary>当前房间的战斗会话。</summary>
        public RoomCombatSession Current => _cells[CellY * Maze.Cols + CellX].Session;

        /// <summary>副本里已经推进的时间(毫秒;按 <see cref="Update(double, int, int, bool)"/> 的时间累加)。</summary>
        public double ClockMs => _clockMs;

        /// <summary>首领格的房间已经清空。</summary>
        public bool BossCleared { get; private set; }

        /// <summary>主角走进了迷宫外的出口(只有单格副本右边有这样的出口)。</summary>
        public bool ExitReached { get; private set; }

        /// <summary>副本是否已经走完:首领清空,或走出了迷宫。</summary>
        public bool Finished => BossCleared || ExitReached;

        public int RoomsEntered { get; private set; }
        public int RoomsCleared { get; private set; }

        /// <summary>进房间换位置的次数(每次走过一个出口算一次)。</summary>
        public int Transitions { get; private set; }

        /// <summary>整座副本累计的命中次数、击杀数与总伤害(各房间之和)。</summary>
        public int Hits => Sum(s => s.Hits);

        public int Kills => Sum(s => s.Kills);
        public long TotalDamage => SumLong(s => s.TotalDamage);

        private int Sum(Func<RoomCombatSession, int> f)
        {
            int n = 0;
            foreach (CellState c in _cells)
            {
                if (c.Session != null)
                {
                    n += f(c.Session);
                }
            }
            return n;
        }

        private long SumLong(Func<RoomCombatSession, long> f)
        {
            long n = 0;
            foreach (CellState c in _cells)
            {
                if (c.Session != null)
                {
                    n += f(c.Session);
                }
            }
            return n;
        }

        /// <summary>某一格是否进去过。</summary>
        public bool IsVisited(int x, int y)
        {
            return Maze.InBounds(x, y) && _cells[y * Maze.Cols + x].Visited;
        }

        /// <summary>某一格的房间是否已经清空。</summary>
        public bool IsCleared(int x, int y)
        {
            return Maze.InBounds(x, y) && _cells[y * Maze.Cols + x].Cleared;
        }

        public string MapPath => Current.MapPath;
        public TownHero Hero => Current.Hero;
        public TownCamera Camera => Current.Camera;
        public int LayerCount => Current.LayerCount;
        public int NpcCount => Current.NpcCount;

        public DungeonSession(DungeonLayout layout, DungeonRoomFactory factory)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _cells = new CellState[layout.Maze.Cols * layout.Maze.Rows];
            for (int y = 0; y < layout.Maze.Rows; y++)
            {
                for (int x = 0; x < layout.Maze.Cols; x++)
                {
                    _cells[y * layout.Maze.Cols + x] = new CellState { X = x, Y = y };
                }
            }
            Enter(layout.StartX, layout.StartY, false, GateSide.Left, 0, false);
        }

        /// <summary>
        /// 读副本定义、房间候选与迷宫布局,造出真实的副本。<paramref name="randomLayout"/> 为真时按 <paramref name="seed"/> 随机挑每格的房间,
        /// 否则取候选里最前面的(基线上就是这样,证据 E-070)。<paramref name="seed"/> 同时是各房间战斗的随机种子。
        /// </summary>
        public static DungeonSession Create(DnfAssets assets, string dungeonPath, int seed = 1, bool randomLayout = false,
            string weaponEqu = DemoHero.StarterWeapon)
        {
            DungeonMaze maze = DungeonMaze.Read(assets, dungeonPath)
                ?? throw new DnfFormatException("副本定义里没有可用的迷宫: " + dungeonPath);
            DungeonRoomPool pool = DungeonRoomPool.Read(assets, DungeonRoomPool.FolderFor(dungeonPath));
            DungeonLayout layout = DungeonLayout.Create(maze, pool, randomLayout ? new Random(seed) : null);
            AvatarOutfit outfit = DemoRoom.DefaultOutfit(assets, weaponEqu);
            return new DungeonSession(layout, CreateAssetFactory(assets, layout, outfit, seed));
        }

        /// <summary>
        /// 用真实素材造房间:读这一格选中的房间地图,按迷宫给的出口种类摆出口,主角放在出生点
        /// (量过出生点的房间用量到的,其余取 <see cref="DemoRoom.DefaultSpawn"/>;换房间时再按出口重新摆),怪物按地图 [monster] 摆。
        /// </summary>
        public static DungeonRoomFactory CreateAssetFactory(DnfAssets assets, DungeonLayout layout, AvatarOutfit outfit, int seed)
        {
            return (cx, cy) =>
            {
                RoomCandidate cand = layout.RoomAt(cx, cy);
                RoomMap map = RoomMap.Read(assets, cand.MapPath);
                List<RoomGate> gates = DemoRoom.BuildGates(assets, map, layout.Maze.GateSet, side => layout.GateKindAt(cx, cy, side));
                double x, y;
                bool left = false;
                if (DemoRoom.TryGetSlice(cand.MapPath, out RoomSlice slice) && slice.HasSpawn)
                {
                    x = slice.SpawnX;
                    y = slice.SpawnY;
                    left = slice.SpawnFacingLeft;
                }
                else
                {
                    DemoRoom.DefaultSpawn(map, out x, out y);
                }
                RoomSession room = DemoRoom.CreateRoom(assets, map, gates, outfit, x, y, left);
                return RoomCombatSession.Create(assets, room, seed + cy * layout.Maze.Cols + cx);
            };
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Update(deltaMs, dirX, dirY, false);
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒:切成不超过 <see cref="RoomCombatSession.MaxStepMs"/> 的小步,每一小步之后检查清房与出口,
        /// 所以换房间的时刻与帧率无关。攻击键是"刚按下"的边沿,只在第一小步里生效。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            if (deltaMs <= 0)
            {
                StepOnce(0, dirX, dirY, attackPressed);
                return;
            }
            double remaining = deltaMs;
            bool first = true;
            while (remaining > 0)
            {
                double step = remaining < RoomCombatSession.MaxStepMs ? remaining : RoomCombatSession.MaxStepMs;
                StepOnce(step, dirX, dirY, first && attackPressed);
                first = false;
                remaining -= step;
            }
        }

        private void StepOnce(double stepMs, int dirX, int dirY, bool attackPressed)
        {
            CellState cell = _cells[CellY * Maze.Cols + CellX];
            cell.Session.Update(stepMs, dirX, dirY, attackPressed);
            _clockMs += stepMs;
            if (!cell.Cleared && AllMonstersGone(cell.Session))
            {
                MarkCleared(cell, false);
            }
            if (cell.Cleared && !ExitReached)
            {
                CheckGates(cell);
            }
        }

        private static bool AllMonstersGone(RoomCombatSession session)
        {
            foreach (MonsterActor m in session.Monsters)
            {
                if (!m.IsGone)
                {
                    return false;
                }
            }
            return true;
        }

        private void MarkCleared(CellState cell, bool settled)
        {
            cell.Cleared = true;
            cell.Settled = settled;
            cell.ClearedAtMs = _clockMs;
            RoomsCleared++;
            if (_layout.IsBossCell(cell.X, cell.Y))
            {
                BossCleared = true;
            }
        }

        private bool IsOpenable(CellState cell, GateSide side)
        {
            GateKind kind = _layout.GateKindAt(cell.X, cell.Y, side);
            return kind == GateKind.Normal || kind == GateKind.Boss;
        }

        private void CheckGates(CellState cell)
        {
            TownHero hero = cell.Session.Hero;
            foreach (RoomGate gate in cell.Session.Room.Content.Gates)
            {
                if (IsOpenable(cell, gate.Side) && HeroTouches(gate, hero))
                {
                    Travel(gate.Side);
                    return;
                }
            }
        }

        /// <summary>主角是否碰到了出口的触发范围(近似,见类的说明)。</summary>
        public static bool HeroTouches(RoomGate gate, TownHero hero)
        {
            switch (gate.Side)
            {
                case GateSide.Left:
                    return hero.X <= gate.X + TriggerReachSide;
                case GateSide.Right:
                    return hero.X >= gate.X - TriggerReachSide;
                case GateSide.Up:
                    return hero.Y <= gate.Y + TriggerReachVertical && Math.Abs(hero.X - gate.X) <= TriggerSpanVertical;
                default:
                    return hero.Y >= gate.Y - TriggerReachVertical && Math.Abs(hero.X - gate.X) <= TriggerSpanVertical;
            }
        }

        private void Travel(GateSide side)
        {
            TownHero hero = Current.Hero;
            if (!Maze.TryNeighbor(CellX, CellY, side, out int nx, out int ny))
            {
                ExitReached = true;
                return;
            }
            Enter(nx, ny, true, ExitBits.Opposite(side), hero.Y, hero.FacingLeft);
            Transitions++;
        }

        private void Enter(int x, int y, bool viaGate, GateSide entry, double carryY, bool carryFacingLeft)
        {
            CellState cell = _cells[y * Maze.Cols + x];
            if (cell.Session == null)
            {
                cell.Session = _factory(x, y) ?? throw new InvalidOperationException("房间工厂没有造出格 (" + x + "," + y + ")");
            }
            CellX = x;
            CellY = y;
            if (viaGate)
            {
                PlaceHero(cell.Session, entry, carryY, carryFacingLeft);
            }
            if (!cell.Visited)
            {
                cell.Visited = true;
                RoomsEntered++;
                if (AllMonstersGone(cell.Session))
                {
                    MarkCleared(cell, true);
                }
            }
        }

        /// <summary>
        /// 把主角摆到 <paramref name="entry"/> 侧出口前:左右出口 x = 出口 x ± 偏移、y 与朝向保持;上下出口 x = 出口 x、y = 出口 y ± 偏移、朝向保持。
        /// 再挪到最近的可走点,镜头居中后夹住(与城镇、房间里一样)。房间地图没有这一侧的出口位置时保持房间里主角原来的位置。
        /// </summary>
        private static void PlaceHero(RoomCombatSession session, GateSide entry, double carryY, bool carryFacingLeft)
        {
            RoomMap map = session.Room.Map;
            TownHero hero = session.Hero;
            int[] g = map.Gates[(int)entry];
            if (g != null)
            {
                double x, y;
                bool left;
                switch (entry)
                {
                    case GateSide.Left:
                        x = g[0] + EntryOffsetLeftGate;
                        y = carryY;
                        left = false;
                        break;
                    case GateSide.Right:
                        x = g[0] - EntryOffsetRightGate;
                        y = carryY;
                        left = true;
                        break;
                    case GateSide.Up:
                        x = g[0];
                        y = g[1] + EntryOffsetVertical;
                        left = carryFacingLeft;
                        break;
                    default:
                        x = g[0];
                        y = g[1] - EntryOffsetVertical;
                        left = carryFacingLeft;
                        break;
                }
                map.NearestWalkable(x, y, out double wx, out double wy);
                hero.Place(wx, wy, left, HeroMotion.Rest, 0);
            }
            session.Camera.Place(hero.X - TownCamera.DefaultCenterX);
        }

        public List<SceneSprite> Build(long timeMs)
        {
            CellState cell = _cells[CellY * Maze.Cols + CellX];
            OpenPendingGates(cell, timeMs);
            return cell.Session.Build(timeMs, BuildHud());
        }

        /// <summary>清房之后第一次出画面时,让出口开始打开;开的时刻按"清房到现在过了多久"倒推到 <paramref name="timeMs"/> 的时间线上。</summary>
        private void OpenPendingGates(CellState cell, long timeMs)
        {
            if (!cell.Cleared || cell.GatesOpened)
            {
                return;
            }
            cell.GatesOpened = true;
            long openedAt = timeMs - (long)Math.Round(_clockMs - cell.ClearedAtMs);
            foreach (RoomGate gate in cell.Session.Room.Content.Gates)
            {
                if (!IsOpenable(cell, gate.Side) || gate.State == GateState.Open)
                {
                    continue;
                }
                if (cell.Settled)
                {
                    gate.OpenSettled();
                }
                else
                {
                    gate.Open(openedAt);
                }
            }
        }

        /// <summary>某一格在小地图上的显示状态。</summary>
        public MiniMapState MiniStateOf(int x, int y)
        {
            if (x == CellX && y == CellY)
            {
                return MiniMapState.Current;
            }
            CellState c = _cells[y * Maze.Cols + x];
            if (c.Visited)
            {
                return c.Cleared ? MiniMapState.Cleared : MiniMapState.Visited;
            }
            foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
            {
                if (Maze.TryNeighbor(x, y, side, out int nx, out int ny) && _cells[ny * Maze.Cols + nx].Visited)
                {
                    return MiniMapState.Seen;
                }
            }
            return MiniMapState.Hidden;
        }

        private List<SceneSprite> BuildHud()
        {
            var hud = new List<SceneSprite>(4);
            int cols = Maze.Cols, rows = Maze.Rows;
            var cells = new MiniMapCell[cols * rows];
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    cells[y * cols + x] = new MiniMapCell(Maze.ExitsAt(x, y), MiniStateOf(x, y), _layout.IsBossCell(x, y));
                }
            }
            SpriteFrame map = _art.MiniMap(cols, rows, cells);
            int mx = 800 - map.Width - MiniMapMarginRight;
            hud.Add(OverlayArt.Place(map, mx, MiniMapTop, 0, "minimap"));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            SpriteFrame count = _art.Text(RoomsCleared.ToString(inv) + "/" + (cols * rows).ToString(inv), 2, OverlayArt.White);
            hud.Add(OverlayArt.Place(count, mx + map.Width - count.Width, MiniMapTop + map.Height + 2, 1, "minimap-count"));
            if (BossCleared)
            {
                SpriteFrame clear = _art.Text("CLEAR", 8, OverlayArt.Yellow);
                hud.Add(OverlayArt.Place(clear, 400 - clear.Width / 2, 190, 2, "clear"));
            }
            return hud;
        }
    }
}
