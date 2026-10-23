using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>怎样造出迷宫里某一格的房间战斗会话(房间的出口已按这一格的邻居摆好、怪物已摆好)。真实的见 <see cref="DungeonSession.CreateAssetFactory"/>;测试里换成合成的。</summary>
    public delegate RoomCombatSession DungeonRoomFactory(int cellX, int cellY);

    /// <summary>
    /// 一次副本:迷宫(<see cref="DungeonLayout"/>)里的一格一格房间,主角在房间里战斗,房间里的怪物都死光之后出口打开,走进出口就换到相邻的房间。证据 E-070、E-072。
    /// 做了:
    ///   · 出口开关:进房间时有怪物,出口关着;最后一只怪物消失(死亡动画播完)的那一刻出口开始淡出打开(基线上量到打开时刻 ≈ 目标死亡标记后 0.5 秒);
    ///     进房间时本来就没有怪物(或回到清空的房间)出口直接开着。
    ///   · 出口的碰撞体(E-072、E-074):左右出口 .obj 自带的 .til 是主角的碰撞体(盖在舞台格上),开着关着都在;关着的门与墙挡路,清房之后主角顶着门洞的楔形斜边走进去就换房间(推挤触发);
    ///     上下出口没有碰撞体,向上(下)走顶到可走带的边缘、且人在这个出口的识别矩形里(<see cref="GateAreas"/>;服务端也用这个矩形校验过门位置)就触发。没有碰撞体的左右出口(没有 .til 的合成数据、门体表里一个挡格都没有)退回到"离出口 12 像素以内、或在识别矩形里顶着可走区域的边界"的近似。
    ///   · 换房间:先淡出到全黑(250 毫秒,期间主角照常被操作)、黑屏停 130 毫秒时换房间、再淡入(250 毫秒),时长取自原版连拍;淡入淡出盖住整个画面(含小地图)。
    ///     新房间里主角落在"进来的那个出口"的识别矩形里:用客户端的随机数在矩形里取一个点、站不下就由近到远找最近的可站点(<see cref="RoomArrival"/>,证据 E-074);
    ///     左出口进来朝右、右出口进来朝左,上下出口朝向保持。镜头先放在 0 再按死区跟随主角。
    ///     房间会话按格缓存,回去时怪物不会复活(基线上走回第一个房间时就是清空的)。
    ///   · 首领格清空 → <see cref="BossCleared"/>;迷宫外的出口(只有单格副本的右边有)走进去 → <see cref="ExitReached"/>;
    ///     没有首领格的副本里,清空一间一个出得去的出口都没有的房间也算通关(推断:村庄袭击类的单格副本左右都是墙,不这样就永远走不完)。
    ///   · 屏幕右上角的副本小地图(<see cref="DungeonMinimapArt"/>,证据 E-109;缺图时退回像素格占位)。
    ///   · 首领清空 <see cref="SettlementDelayMs"/> 之后进入结算(证据 E-091):场景压暗(两层黑幕)、主角脚下生营火(<see cref="ClearCamp"/>)、
    ///     右上角出原版的"是否继续?"菜单(<see cref="SettlementArt"/>;再次挑战、选择其它地下城、返回城镇,快速组队没做);选择由 <see cref="Choose"/>、<see cref="Click"/> 记下,
    ///     <see cref="Choice"/> 之后外面决定去哪(流程在 <see cref="DungeonSession"/> 之外)。
    /// 没做 / 没量过:客户端"走进出口就换房间"的触发代码(没有读到,触发条件是推断)、主角对象编号的来源(<see cref="HeroObjectId"/>)、淡入期间是否接受操作(现在接受)、
    /// 转场时怪物是否冻结(现在不冻结)、清房时站姿切换的确切时刻、结算的时机与过程(清空到结算菜单之间的 3 秒是占位,掉落、评分、经验结算没读到)、掉落物。
    /// </summary>
    public sealed class DungeonSession : ISkillSession, ISoundSource, IRunSession
    {
        /// <summary>
        /// 没有碰撞体的左右出口的触发范围:主角离出口的横向距离不超过它(像素;近似,没有量过;真实数据里左右出口都有碰撞体)。
        /// 要小于进房间落点离出口的最小距离(识别矩形的边沿 + 占位盒半宽 20),否则一落地就又触发。
        /// </summary>
        public const int TriggerReachSide = 12;

        /// <summary>换房间的转场:淡出、全黑、淡入各多久(毫秒;原版连拍里量到 ≈250、≈130、≈250,证据 E-072)。</summary>
        public const double FadeOutMs = 250;

        public const double BlackMs = 130;
        public const double FadeInMs = 250;

        /// <summary>
        /// 首领清空到结算(营地与菜单)出现隔多久(毫秒):<b>占位</b>——基线里清空到结算之间有一段 38.5 秒没有拍到的空档,时机与过程(掉落、评分)没有读到。
        /// </summary>
        public const double SettlementDelayMs = 3000;

        /// <summary>结算开始时场景变暗的淡入时长(毫秒;占位)。</summary>
        public const double SettlementDimMs = 500;

        /// <summary>
        /// 结算时每一层黑幕的不透明度(0 到 255)。观察(证据 E-092):基线结算帧上地图图层(远景、中景、近景背景、树桩)整体变成原来的 ×0.61、
        /// 德利拉约 ×0.76 到 ×0.78(取不受加法光晕干扰的蓝色通道、她左半身回归,误差 ±0.02 量级)、火芯没有被压暗(亮到 255)。
        /// 推断:盖了两层相同的黑幕——一层在地图图层之上、物件之下(<see cref="OverlayArt.BackDimRank"/>),一层在物件之上、界面之下(<see cref="OverlayArt.DimRank"/>),
        /// 营火画在第二层之上;每层 alpha 56 即 ×0.780,两层叠起来 ×0.609,恰好是量到的两个数。物件(主角、怪物)与近景图层是不是 ×0.78 没有量到。
        /// 面板与界面不被盖住。
        /// </summary>
        public const int SettlementDimAlpha = 56;

        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        public const int MiniMapMarginRight = 10;
        public const int MiniMapTop = 30;

        private enum Phase
        {
            None,
            FadeOut,
            Black,
            FadeIn,
        }

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
        private readonly SoundCueQueue _sounds = new SoundCueQueue();
        private readonly List<SoundCue> _soundScratch = new List<SoundCue>();
        private double _clockMs;
        private Phase _phase;
        private double _phaseMs;
        private GateSide _pendingSide;
        private double _bossClearedAtMs;
        private ClearCamp _camp;
        private int _hoverRow = -1;

        /// <summary>结算战果小结的左边距与顶部(屏幕像素;占位版式)。</summary>
        public const int RewardPanelLeft = 24;

        public const int RewardPanelTop = 150;

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

        /// <summary>造营地(<see cref="ClearCamp"/>)与结算菜单要用的图包;可以是 null(没有图包时营地是空的,菜单退回像素字的占位面板)。</summary>
        public DnfAssets Assets { get; }

        /// <summary>首领清空时的副本时钟(毫秒);没清空时为 -1。</summary>
        public double BossClearedAtMs => BossCleared ? _bossClearedAtMs : -1;

        /// <summary>
        /// 结算已经开始:首领清空后过了 <see cref="SettlementDelayMs"/>。此后画面里是变暗的场景、营地与右上角的结算菜单(<see cref="SettlementArt"/>),
        /// 小地图与 CLEAR 字样撤掉;主角仍可操作。菜单由 <see cref="Choose"/>、<see cref="Click"/> 选择,选定之后 <see cref="Choice"/> 不再是 None,由外面决定下一步。
        /// </summary>
        public bool SettlementActive => BossCleared && _clockMs - _bossClearedAtMs >= SettlementDelayMs;

        /// <summary>结算开始之后过了多久(毫秒);没开始为 0。</summary>
        public double SettlementAgeMs => SettlementActive ? _clockMs - _bossClearedAtMs - SettlementDelayMs : 0.0;

        /// <summary>此刻每一层黑幕的不透明度:结算开始后在 <see cref="SettlementDimMs"/> 里线性升到 <see cref="SettlementDimAlpha"/>(淡入时长是占位)。</summary>
        public int SettlementDim
        {
            get
            {
                if (!SettlementActive)
                {
                    return 0;
                }
                double t = SettlementAgeMs / SettlementDimMs;
                return (int)Math.Round(SettlementDimAlpha * (t >= 1.0 ? 1.0 : t), MidpointRounding.AwayFromZero);
            }
        }

        /// <summary>结算营地;结算没开始时为 null。</summary>
        public ClearCamp Camp => _camp;

        /// <summary>结算菜单上选了什么;还没选是 <see cref="SettlementChoice.None"/>。</summary>
        public SettlementChoice Choice { get; private set; }

        /// <summary>鼠标此刻停在结算菜单的哪一行(0 到 3);不在菜单上为 -1。</summary>
        public int HoverRow => SettlementActive && Choice == SettlementChoice.None ? _hoverRow : -1;

        /// <summary>主角走进了迷宫外的出口(只有单格副本右边有这样的出口)。</summary>
        public bool ExitReached { get; private set; }

        /// <summary>副本是否已经走完:首领清空,或走出了迷宫。</summary>
        public bool Finished => BossCleared || ExitReached;

        public int RoomsEntered { get; private set; }
        public int RoomsCleared { get; private set; }

        /// <summary>进房间换位置的次数(每次走过一个出口算一次,换房间的那一刻计数)。</summary>
        public int Transitions { get; private set; }

        /// <summary>正在转场(淡出、全黑或淡入)。</summary>
        public bool InTransition => _phase != Phase.None;

        /// <summary>转场时盖住画面的黑色有多浓(0 到 255;淡出线性升到 255,全黑 255,淡入线性降到 0)。</summary>
        public int FadeAlpha
        {
            get
            {
                double a;
                switch (_phase)
                {
                    case Phase.FadeOut:
                        a = 255.0 * _phaseMs / FadeOutMs;
                        break;
                    case Phase.Black:
                        a = 255.0;
                        break;
                    case Phase.FadeIn:
                        a = 255.0 * (1.0 - _phaseMs / FadeInMs);
                        break;
                    default:
                        return 0;
                }
                int v = (int)Math.Round(a, MidpointRounding.AwayFromZero);
                return v < 0 ? 0 : (v > 255 ? 255 : v);
            }
        }

        /// <summary>整座副本累计的命中次数、击杀数与总伤害(各房间之和)。</summary>
        public int Hits => Sum(s => s.Hits);

        public int Kills => Sum(s => s.Kills);
        public long TotalDamage => SumLong(s => s.TotalDamage);

        /// <summary>整座副本里掉出过的金币堆数、捡起的堆数与捡到的金币总额(各房间之和,证据 E-116)。</summary>
        public int GoldDropped => Sum(s => s.GoldDropped);

        public int GoldPicked => Sum(s => s.GoldPicked);
        public long GoldEarned => SumLong(s => s.GoldEarned);
        public long GoldDroppedAmount => SumLong(s => s.GoldDroppedAmount);

        /// <summary>整座副本里掉出过多少件物品、捡起了多少件、有多少件够得着却因背包满了留在地上(各房间之和,证据 E-117)。</summary>
        public int ItemsDropped => Sum(s => s.ItemsDropped);

        public int ItemsPicked => Sum(s => s.ItemsPicked);
        public int ItemsBagFull => Sum(s => s.ItemsBagFull);

        /// <summary>整座副本里主角从击杀得到的经验总和(各房间之和,证据 E-111)。</summary>
        public long ExpEarned => SumLong(s => s.ExpEarned);

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

        /// <summary>主角的生命与魔法(当前房间会话的那一份,换房间时抄过去,见 <see cref="Travel"/>);没有时为 null。</summary>
        public HeroVitals Vitals => Current.Vitals;

        /// <summary>主角已经死了(血量归零;躺着、复活窗口开着或副本已失败都算)。</summary>
        public bool HeroDead => Current.HeroDead;

        /// <summary>
        /// 主角的死亡窗口与复活(证据 E-115),整座副本一个;创建时没给复活币库存就是 null(老行为:死了一直躺着、不会失败)。
        /// </summary>
        public HeroRevive Revive => Current.Revive;

        /// <summary>复活窗口开着而且此刻按复活键(X,即攻击键)能复活。</summary>
        public bool CanRevive => Current.CanRevive;

        /// <summary>
        /// 副本失败(证据 E-115):主角死后复活窗口的失败计时走完了还没复活。外面(流程)据此让主角出副本回城,不进结算;
        /// 没有复活对象时永远是假。
        /// </summary>
        public bool Failed => Revive != null && Revive.Failed;

        /// <summary>整座副本里主角被怪物打中的总次数。</summary>
        public int HeroHits => Sum(s => s.HeroHits);

        /// <summary>
        /// 整座副本的声音提示队列(证据 E-089、E-090):每一步把当前房间会话发出的提示按时间顺序收进来,换房间时先放新房间的 <see cref="SoundCueKind.RoomEnter"/> 标记与地图声音
        /// (第一次进去还有怪物出场声);播放的一方每帧调 <see cref="DrainSounds"/> 取走。
        /// </summary>
        public SoundCueQueue Sounds => _sounds;

        public void DrainSounds(List<SoundCue> into)
        {
            _sounds.Drain(into);
        }

        /// <summary>把一个房间会话里等着的声音提示搬进副本的队列(保持顺序)。</summary>
        private void ForwardSounds(RoomCombatSession session)
        {
            _soundScratch.Clear();
            session.DrainSounds(_soundScratch);
            foreach (SoundCue cue in _soundScratch)
            {
                _sounds.Add(cue.Key, cue.Kind, cue.Owner);
            }
        }
        public TownCamera Camera => Current.Camera;
        public int LayerCount => Current.LayerCount;
        public int NpcCount => Current.NpcCount;

        private readonly ISpriteSource _sprites;

        /// <summary>
        /// 主角对象的编号:进房间落点的随机种子是它乘地图编号(证据 E-074,见 <see cref="GateArrival.SeedFor"/>)。
        /// 原版里它由对象管理器分配,来源没有查明,所以是个可设的参数;缺省 <see cref="GateArrival.DefaultHeroObjectId"/>。
        /// </summary>
        public int HeroObjectId { get; }

        public DungeonSession(DungeonLayout layout, DungeonRoomFactory factory, int heroObjectId = GateArrival.DefaultHeroObjectId, DnfAssets assets = null, ISpriteSource sprites = null)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            HeroObjectId = heroObjectId;
            Assets = assets;
            _sprites = sprites ?? assets;
            _cells = new CellState[layout.Maze.Cols * layout.Maze.Rows];
            for (int y = 0; y < layout.Maze.Rows; y++)
            {
                for (int x = 0; x < layout.Maze.Cols; x++)
                {
                    _cells[y * layout.Maze.Cols + x] = new CellState { X = x, Y = y };
                }
            }
            Enter(layout.StartX, layout.StartY, false, GateSide.Left, false);
        }

        /// <summary>
        /// 读副本定义、房间候选与迷宫布局,造出真实的副本。<paramref name="randomLayout"/> 为真时按 <paramref name="seed"/> 随机挑每格的房间,
        /// 否则取候选里最前面的(基线上就是这样,证据 E-070)。<paramref name="seed"/> 同时是各房间战斗的随机种子。
        /// </summary>
        public static DungeonSession Create(DnfAssets assets, string dungeonPath, int seed = 1, bool randomLayout = false,
            string weaponEqu = DemoHero.StarterWeapon, int heroObjectId = GateArrival.DefaultHeroObjectId, bool heroVitals = true, HeroProgress progress = null,
            int heroDamageScale = 1, ReviveCoins coins = null, int monsterDamageScale = 1)
        {
            Random random = randomLayout ? new Random(seed) : null;
            DungeonMaze maze = DungeonMaze.Read(assets, dungeonPath, random)
                ?? throw new DnfFormatException("副本定义里没有可用的迷宫: " + dungeonPath);
            DungeonRoomPool pool = DungeonRoomPool.ReadFor(assets, dungeonPath);
            DungeonLayout layout = DungeonLayout.Create(maze, pool, random);
            AvatarOutfit outfit = DemoRoom.DefaultOutfit(assets, weaponEqu);
            // 给了复活币库存就有复活窗口与副本失败(证据 E-115):整座副本一个 HeroRevive,各房间共用;没给就是老行为(死了一直躺着)
            ScriptLine[] dungeonScript = assets.ReadScript(dungeonPath);
            HeroRevive revive = coins == null ? null : new HeroRevive(DungeonCoinRules.Parse(dungeonScript), coins);
            // 金币掉落按副本的标准等级([basis level],证据 E-116)选概率行
            return new DungeonSession(layout, CreateAssetFactory(assets, layout, outfit, seed, heroObjectId, heroVitals, progress, maze.ExpWeight, heroDamageScale, revive,
                monsterDamageScale, GoldDrop.StandardLevel(dungeonScript)), heroObjectId, assets);
        }

        /// <summary>
        /// 主角的等级与经验(证据 E-111);所有房间共用同一个对象,创建时没给就是 null(杀怪不给经验)。
        /// </summary>
        public HeroProgress Progress => Current.Progress;

        /// <summary>
        /// 用真实素材造房间:读这一格选中的房间地图,按迷宫给的出口种类摆出口,主角放在出生点
        /// (量过出生点的房间用量到的;其余按客户端的规则落在 [dungeon start area] 里,见 <see cref="RoomArrival.PlaceAtStart"/>,没有出生区的退到 <see cref="DemoRoom.DefaultSpawn"/>;
        /// 换房间时再按出口重新摆),怪物按地图 [monster] 摆。
        /// </summary>
        public static DungeonRoomFactory CreateAssetFactory(DnfAssets assets, DungeonLayout layout, AvatarOutfit outfit, int seed,
            int heroObjectId = GateArrival.DefaultHeroObjectId, bool heroVitals = true, HeroProgress progress = null, float expWeight = 1f,
            int heroDamageScale = 1, HeroRevive revive = null, int monsterDamageScale = 1, int dropLevel = 0)
        {
            return (cx, cy) =>
            {
                RoomCandidate cand = layout.RoomAt(cx, cy);
                RoomMap map = RoomMap.Read(assets, cand.MapPath);
                List<RoomGate> gates = DemoRoom.BuildGates(assets, map, layout.Maze.GateSet, side => layout.GateKindAt(cx, cy, side));
                double x, y;
                bool left = false;
                bool measured = DemoRoom.TryGetSlice(cand.MapPath, out RoomSlice slice) && slice.HasSpawn;
                if (measured)
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
                if (!measured)
                {
                    RoomArrival.PlaceAtStart(room, cand.MapId, heroObjectId);
                }
                RoomCombatSession combat = RoomCombatSession.Create(assets, room, seed + cy * layout.Maze.Cols + cx, heroVitals, progress, expWeight);
                combat.HeroDamageScale = heroDamageScale;
                combat.Revive = revive;
                combat.MonsterDamageScale = monsterDamageScale;
                combat.DropDungeonLevel = dropLevel;
                return combat;
            };
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Update(deltaMs, dirX, dirY, false, false);
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, false);
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒:切成不超过 <see cref="RoomCombatSession.MaxStepMs"/> 的小步,每一小步之后检查清房与出口,
        /// 所以换房间的时刻与帧率无关。攻击键与跳跃键是"刚按下"的边沿,只在第一小步里生效。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, jumpPressed, SkillKeys.None);
        }

        /// <summary>同上,多一组技能键(A、S、D 的"刚按下"边沿,同样只在第一小步里生效);技能放在当前房间的战斗会话里(证据 E-076)。</summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            if (deltaMs <= 0)
            {
                StepOnce(0, dirX, dirY, attackPressed, jumpPressed, skillsPressed);
                return;
            }
            double remaining = deltaMs;
            bool first = true;
            while (remaining > 0)
            {
                double step = remaining < RoomCombatSession.MaxStepMs ? remaining : RoomCombatSession.MaxStepMs;
                StepOnce(step, dirX, dirY, first && attackPressed, first && jumpPressed, first ? skillsPressed : SkillKeys.None);
                first = false;
                remaining -= step;
            }
        }

        private void StepOnce(double stepMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            bool inTransition = _phase != Phase.None;
            CellState cell = _cells[CellY * Maze.Cols + CellX];
            cell.Session.Update(stepMs, dirX, dirY, attackPressed, jumpPressed, skillsPressed);
            ForwardSounds(cell.Session);
            _clockMs += stepMs;
            if (!cell.Cleared && AllMonstersGone(cell.Session))
            {
                MarkCleared(cell, false);
            }
            if (SettlementActive && _layout.IsBossCell(cell.X, cell.Y))
            {
                if (_camp == null)
                {
                    // 营火生在主角脚下(推断,见 ClearCamp);营地从这一刻开始播放
                    _camp = ClearCamp.Create(Assets, cell.Session.MapPath, cell.Session.Hero.X, cell.Session.Hero.Y);
                    // 结算界面出现:放一声 M_RESULT(界面音;客户端里它是结果窗口显示时放的短音效,不是背景音乐,E-097)
                    _sounds.Add(SceneMusic.ResultSound, SoundCueKind.Ui);
                }
                else
                {
                    _camp.Advance(stepMs);
                }
            }
            if (cell.Cleared && !ExitReached && !inTransition)
            {
                CheckGates(cell);
            }
            if (inTransition)
            {
                // 转场计时放在这一步的最后:换房间发生在一步的末尾,新房间从下一步才开始被操作
                AdvanceTransition(stepMs);
            }
        }

        /// <summary>转场计时:淡出满了就换房间、转入全黑,全黑满了转入淡入,淡入满了结束;一步里跨过多个阶段时把多出来的时间带到下一阶段。</summary>
        private void AdvanceTransition(double stepMs)
        {
            _phaseMs += stepMs;
            if (_phase == Phase.FadeOut && _phaseMs >= FadeOutMs)
            {
                _phaseMs -= FadeOutMs;
                _phase = Phase.Black;
                Travel(_pendingSide);
            }
            if (_phase == Phase.Black && _phaseMs >= BlackMs)
            {
                _phaseMs -= BlackMs;
                _phase = Phase.FadeIn;
            }
            if (_phase == Phase.FadeIn && _phaseMs >= FadeInMs)
            {
                _phase = Phase.None;
                _phaseMs = 0;
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
            if (_layout.IsBossCell(cell.X, cell.Y) && !BossCleared)
            {
                BossCleared = true;
                _bossClearedAtMs = _clockMs;
            }
            else if (!_layout.HasBoss && !BossCleared && !HasOpenableExit(cell))
            {
                // 没有首领格、这间房又没有一个出得去的出口(例如整个副本只有一间、左右都是墙):清空它就是通关。通关条件的读码没有找到,这条是推断
                BossCleared = true;
                _bossClearedAtMs = _clockMs;
            }
        }

        private bool HasOpenableExit(CellState cell)
        {
            foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
            {
                if (IsOpenable(cell, side))
                {
                    return true;
                }
            }
            return false;
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
                if (IsOpenable(cell, gate.Side) && HeroTriggers(gate, hero))
                {
                    BeginTravel(gate.Side);
                    return;
                }
            }
        }

        /// <summary>
        /// 主角这一步是否触发了出口。门体盖着出口那一行的左右出口(<see cref="RoomGate.BodyReachesGate"/>):水平移动顶在这个出口的碰撞体上(<see cref="TownHero.PushedInto"/>);
        /// 其余的左右出口(没有门体、门体不在出口那一行、或门体表里没有一个挡格):人的 y 在识别矩形的纵向范围里,且离出口横向 <see cref="TriggerReachSide"/> 像素以内,
        /// 或者正朝出口走、被可走区域的边界挡住、人又在识别矩形里(出口位置落在可走区域外的地图);
        /// 上下出口:正向上(下)走、且占位盒压到这个出口的识别矩形(<see cref="RoomGate.Area"/>,服务端校验过门位置用的也是它);
        /// 不要求被边缘挡住:全库约一成的下出口,矩形下沿比可走带下沿还高(顶到边缘时人已在矩形外),要求"顶着边缘"它们就永远进不去(E-113 的统计);
        /// 触发条件是推断,客户端的触发代码没有读到。
        /// </summary>
        public static bool HeroTriggers(RoomGate gate, TownHero hero)
        {
            switch (gate.Side)
            {
                case GateSide.Left:
                    return gate.BodyReachesGate(hero.BodyWidth, hero.BodyDepth)
                        ? ReferenceEquals(hero.PushedInto, gate)
                        : InAreaRows(gate, hero) && (hero.X <= gate.X + TriggerReachSide || hero.PushedLeft && InAreaColumns(gate, hero));
                case GateSide.Right:
                    return gate.BodyReachesGate(hero.BodyWidth, hero.BodyDepth)
                        ? ReferenceEquals(hero.PushedInto, gate)
                        : InAreaRows(gate, hero) && (hero.X >= gate.X - TriggerReachSide || hero.PushedRight && InAreaColumns(gate, hero));
                case GateSide.Up:
                    return hero.TriedUp && GateAreas.OverlapsBox(gate.Area, (int)Math.Floor(hero.X), (int)Math.Floor(hero.Y), hero.BodyWidth, hero.BodyDepth);
                default:
                    return hero.TriedDown && GateAreas.OverlapsBox(gate.Area, (int)Math.Floor(hero.X), (int)Math.Floor(hero.Y), hero.BodyWidth, hero.BodyDepth);
            }
        }

        private static bool InAreaRows(RoomGate gate, TownHero hero)
        {
            int y = (int)Math.Floor(hero.Y);
            return y >= gate.Area.Y && y < gate.Area.Y + gate.Area.Height;
        }

        private static bool InAreaColumns(RoomGate gate, TownHero hero)
        {
            int x = (int)Math.Floor(hero.X);
            return x >= gate.Area.X && x < gate.Area.X + gate.Area.Width;
        }

        /// <summary>迷宫里这一侧有邻格:开始转场(先淡出);没有(单格副本的外出口):副本直接走完。</summary>
        private void BeginTravel(GateSide side)
        {
            if (!Maze.TryNeighbor(CellX, CellY, side, out _, out _))
            {
                ExitReached = true;
                return;
            }
            _phase = Phase.FadeOut;
            _phaseMs = 0;
            _pendingSide = side;
        }

        private void Travel(GateSide side)
        {
            TownHero hero = Current.Hero;
            HeroSkills carried = Current.Skills;
            HeroVitals carriedVitals = Current.Vitals;
            Maze.TryNeighbor(CellX, CellY, side, out int nx, out int ny);
            Enter(nx, ny, true, ExitBits.Opposite(side), hero.FacingLeft);
            Current.Skills.CopyCooldownsFrom(carried);   // 技能冷却不因换房间清零
            Current.SyncProgress();                      // 别的房间里升了级:先换上新等级的上限,再抄生命魔法(证据 E-111)
            if (carriedVitals != null && Current.Vitals != null && !ReferenceEquals(carriedVitals, Current.Vitals))
            {
                Current.Vitals.CopyStateFrom(carriedVitals);   // 生命与魔法也跟着人走
            }
            Transitions++;
        }

        private void Enter(int x, int y, bool viaGate, GateSide entry, bool carryFacingLeft)
        {
            CellState cell = _cells[y * Maze.Cols + x];
            bool fresh = cell.Session == null;
            if (fresh)
            {
                cell.Session = _factory(x, y) ?? throw new InvalidOperationException("房间工厂没有造出格 (" + x + "," + y + ")");
            }
            else
            {
                cell.Session.ReplayRoomSounds();   // 走回去过的房间:再发一遍它的地图声音(怪物出场声只在第一次)
            }
            ForwardSounds(cell.Session);
            CellX = x;
            CellY = y;
            if (viaGate)
            {
                PlaceHero(cell.Session, entry, _layout.RoomAt(x, y).MapId, carryFacingLeft);
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
        /// 把主角摆到 <paramref name="entry"/> 侧出口的落点(证据 E-074,<see cref="RoomArrival.PlaceAtGate"/>):在这个出口的识别矩形里按客户端的随机数取点、
        /// 站不下就找最近的可站点;左出口进来朝右、右出口进来朝左,上下出口保持 <paramref name="carryFacingLeft"/>。
        /// 镜头 x 先放在 0(带扩展瓦片的地图镜头 y 先放在最大值,客户端载入地图时的初值)再按死区跟随主角(原版进房间的第一张截图上镜头由此而来)。
        /// 房间地图没有这一侧的出口位置时保持房间里主角原来的位置。
        /// </summary>
        private void PlaceHero(RoomCombatSession session, GateSide entry, int mapId, bool carryFacingLeft)
        {
            RoomArrival.PlaceAtGate(session.Room, entry, mapId, HeroObjectId, carryFacingLeft);
            session.Camera.Restart(session.Hero.X, session.Hero.Y);
        }

        /// <summary>
        /// 选结算菜单上的一项(热键 F13 / F11 / F12 或点击都走这里)。结算还没开始、已经选过、选项不可用(快速组队)时返回 false,什么也不变。
        /// </summary>
        public bool Choose(SettlementChoice choice)
        {
            if (!SettlementActive || Choice != SettlementChoice.None || !SettlementArt.IsEnabled(choice))
            {
                return false;
            }
            Choice = choice;
            return true;
        }

        /// <summary>鼠标移到屏幕坐标 (<paramref name="x"/>, <paramref name="y"/>)(800×600 的画面坐标):菜单上对应的那一行亮起。</summary>
        public void SetPointer(int x, int y)
        {
            _hoverRow = SettlementArt.HitRow(x, y);
        }

        /// <summary>鼠标离开画面。</summary>
        public void ClearPointer()
        {
            _hoverRow = -1;
        }

        /// <summary>在屏幕坐标 (<paramref name="x"/>, <paramref name="y"/>) 点了一下:点中可用的菜单行就选上并返回这一项,否则返回 <see cref="SettlementChoice.None"/>。</summary>
        public SettlementChoice Click(int x, int y)
        {
            SettlementChoice choice = SettlementArt.HitTest(x, y);
            return choice != SettlementChoice.None && Choose(choice) ? choice : SettlementChoice.None;
        }

        public List<SceneSprite> Build(long timeMs)
        {
            CellState cell = _cells[CellY * Maze.Cols + CellX];
            OpenPendingGates(cell, timeMs);
            return cell.Session.Build(timeMs, BuildHud(cell));
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

        /// <summary>(<paramref name="x"/>, <paramref name="y"/>) 是否与当前格通过迷宫出口直接相连。</summary>
        public bool IsConnectedNeighbour(int x, int y)
        {
            foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
            {
                if (Maze.TryNeighbor(CellX, CellY, side, out int nx, out int ny) && nx == x && ny == y)
                {
                    return true;
                }
            }
            return false;
        }

        private List<SceneSprite> BuildHud(CellState cell)
        {
            var hud = new List<SceneSprite>(32);
            if (SettlementActive)
            {
                AddSettlement(hud, cell);
            }
            else
            {
                AddMiniMap(hud, cell);
            }
            int fade = FadeAlpha;
            if (fade > 0)
            {
                // 转场的黑幕盖在所有东西(含小地图)上面
                hud.Add(OverlayArt.Place(_art.Solid(ScreenWidth, ScreenHeight, OverlayArt.Black), 0, 0, hud.Count, "fade", fade));
            }
            return hud;
        }

        /// <summary>右上角的副本小地图(<see cref="DungeonMinimapArt"/>,证据 E-109;缺图时退回像素格占位与清房数),首领清空之后(结算开始之前)加一个大号的 CLEAR 字样。</summary>
        private void AddMiniMap(List<SceneSprite> hud, CellState currentCell)
        {
            int cols = Maze.Cols, rows = Maze.Rows;
            int order = 0;
            DungeonMinimapArt realArt = DungeonMinimapArt.For(_sprites, Maze.MinimapImage);
            if (realArt != null)
            {
                var realCells = new DungeonMinimapCell[cols * rows];
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        RoomCandidate room = _layout.RoomAt(x, y);
                        int mapId = room != null ? room.MapId : 0;
                        bool openNeighbour = currentCell.Cleared && IsConnectedNeighbour(x, y);
                        realCells[y * cols + x] = new DungeonMinimapCell(
                            Maze.ExitsAt(x, y),
                            mapId,
                            MiniStateOf(x, y),
                            _layout.IsBossCell(x, y),
                            openNeighbour);
                    }
                }
                realArt.Build(hud, ref order, cols, rows, realCells, currentCell.Cleared, _layout.IsBossCell(currentCell.X, currentCell.Y), _clockMs);
            }
            else
            {
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
                hud.Add(OverlayArt.Place(map, mx, MiniMapTop, order++, "minimap"));
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                SpriteFrame count = _art.Text(RoomsCleared.ToString(inv) + "/" + (cols * rows).ToString(inv), 2, OverlayArt.White);
                hud.Add(OverlayArt.Place(count, mx + map.Width - count.Width, MiniMapTop + map.Height + 2, order++, "minimap-count"));
            }
            if (BossCleared)
            {
                SpriteFrame clear = _art.Text("CLEAR", 8, OverlayArt.Yellow);
                hud.Add(OverlayArt.Place(clear, 400 - clear.Width / 2, 190, order++, "clear"));
            }
        }

        /// <summary>
        /// 结算画面(证据 E-091),从下到上:地图图层、第一层黑幕(<see cref="OverlayArt.BackDimRank"/>)、物件(主角、营地 NPC)、第二层黑幕(<see cref="OverlayArt.DimRank"/>)、
        /// 营火各层(不被压暗)、右上角的结算菜单。小地图在结算时撤掉(基线结算帧右上角只有菜单)。营地只画在首领格里。
        /// 两层黑幕与营火同在这份列表里:房间场景把它排在地图图层之后,同大类按列表顺序,所以第二层黑幕要先于营火加进来。
        /// </summary>
        private void AddSettlement(List<SceneSprite> hud, CellState cell)
        {
            int order = 0;
            int dim = SettlementDim;
            if (dim > 0)
            {
                SpriteFrame black = _art.Solid(ScreenWidth, ScreenHeight, OverlayArt.Black);
                hud.Add(OverlayArt.PlaceDim(black, dim, order++, "settle-dim-back", OverlayArt.BackDimRank));
                hud.Add(OverlayArt.PlaceDim(black, dim, order++, "settle-dim"));
            }
            if (_camp != null && _layout.IsBossCell(cell.X, cell.Y))
            {
                _camp.AddSprites(hud, Assets, cell.Session.Room.Projection);
            }
            SettlementArt art = SettlementArt.For(Assets);
            if (art != null)
            {
                art.Build(hud, ref order, _art, HoverRow);
            }
            else
            {
                SettlementArt.BuildFallback(hud, ref order, _art, HoverRow);
            }
            AddRewardPanel(hud, ref order);
        }

        /// <summary>
        /// 结算时屏幕左侧的战果小结:通关用时、击杀、经验、金币与捡到的物品件数(本局的合计)。<b>占位</b>:原版结果窗口的版式与内容没有读出来,
        /// 这里只是把本局的数字列出来,英文像素字,位置随意。
        /// </summary>
        private void AddRewardPanel(List<SceneSprite> hud, ref int order)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            long seconds = (long)(_bossClearedAtMs / 1000.0);
            string[] lines =
            {
                "CLEAR TIME " + (seconds / 60).ToString("00", inv) + ":" + (seconds % 60).ToString("00", inv),
                "KILLS " + Kills.ToString(inv),
                "EXP +" + ExpEarned.ToString(inv),
                "GOLD +" + GoldEarned.ToString(inv),
                "ITEMS " + ItemsPicked.ToString(inv),
            };
            int y = RewardPanelTop;
            foreach (string text in lines)
            {
                SpriteFrame line = _art.Text(text, 3, OverlayArt.White);
                hud.Add(OverlayArt.Place(line, RewardPanelLeft, y, order++, "reward"));
                y += line.Height + 6;
            }
        }
    }
}
