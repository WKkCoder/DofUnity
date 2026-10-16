using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>世界坐标里的矩形(x、y 是最小角,宽、高为正)。</summary>
    public readonly struct WorldRect
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public WorldRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>闭区间:边界上的点算在里面(主角能走到 [virtual movable area] 的左边界,证据 E-066)。</summary>
        public bool Contains(double x, double y)
        {
            return x >= X && x <= X + Width && y >= Y && y <= Y + Height;
        }
    }

    /// <summary>主角能站的地方。城镇用矩形的并集([virtual movable area]),副本房间用瓦片通行表(<see cref="RoomMap"/>)。</summary>
    public interface IWalkArea
    {
        /// <summary>为 true 时不限制移动(例如地图没有写可走区域)。</summary>
        bool IsUnrestricted { get; }

        bool Contains(double x, double y);
    }

    /// <summary>矩形并集(闭区间)构成的可走区域;没有矩形时不限制。</summary>
    public sealed class RectUnionArea : IWalkArea
    {
        private readonly IReadOnlyList<WorldRect> _rects;

        public RectUnionArea(IReadOnlyList<WorldRect> rects)
        {
            _rects = rects ?? new List<WorldRect>();
        }

        public bool IsUnrestricted => _rects.Count == 0;

        public bool Contains(double x, double y)
        {
            foreach (WorldRect r in _rects)
            {
                if (r.Contains(x, y))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>副本房间的可走区域:房间所有瓦片的通行表(证据 E-067),再扣掉不可穿过的障碍物的占地(证据 E-068,占地的取法是假设)。</summary>
    public sealed class RoomWalkArea : IWalkArea
    {
        private readonly RoomMap _map;
        private readonly IReadOnlyList<RoomObject> _obstacles;

        public RoomWalkArea(RoomMap map, IReadOnlyList<RoomObject> obstacles = null)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _obstacles = obstacles ?? new List<RoomObject>();
        }

        public bool IsUnrestricted => false;

        public bool Contains(double x, double y)
        {
            if (!_map.CanWalk(x, y))
            {
                return false;
            }
            foreach (RoomObject o in _obstacles)
            {
                if (o.Blocks(x, y))
                {
                    return false;
                }
            }
            return true;
        }
    }

    public enum HeroMotion
    {
        /// <summary>站立(城镇里用 [rest motion],不是战斗用的 [waiting motion],证据 E-065)。</summary>
        Rest,

        /// <summary>行走([move motion])。</summary>
        Move,

        /// <summary>普通攻击连招里的一击(.chr 的 [attack motion] 第 <see cref="TownHero.AttackStage"/> 项,证据 E-069)。</summary>
        Attack,
    }

    /// <summary>
    /// 城镇里的镜头(证据 E-066):死区跟随。主角在屏幕上 x 位于 [中心 − 半宽, 中心 + 半宽](默认 350 到 450)时镜头不动;
    /// 走出这个窗口,镜头就跟着走,把主角留在窗口边缘;镜头 x 再夹在 [最小, 最大] 之间(到头后主角才继续往那边走出窗口)。
    /// 依据两组原版测量(赛丽亚房间 <c>map/elvengard/(r)gate.map</c>):向左行走的 40 帧连拍里,镜头 x 在主角到达屏幕 350 之前一直是 96,
    /// 之后每帧跟着主角降到 0 为止;进城后的第一张截图里主角在屏幕 450、镜头 24。两个端点 0 与 96 是这张地图的实测值,
    /// 怎么由地图数据算出来没有研究;初始镜头按"主角居中再夹住"取(主角在 615 出生时得 96,与实测一致)。
    /// </summary>
    public sealed class TownCamera
    {
        public const int DefaultCenterX = 400;
        public const int DefaultHalfWidth = 50;

        public int CenterX { get; }
        public int HalfWidth { get; }
        public int MinX { get; }
        public int MaxX { get; }

        /// <summary>镜头在世界里的 x(屏幕左边缘对应的世界 x)。</summary>
        public double X { get; private set; }

        public int IntX => (int)Math.Floor(X);

        public TownCamera(int minX, int maxX, double heroX, int centerX = DefaultCenterX, int halfWidth = DefaultHalfWidth)
        {
            MinX = minX;
            MaxX = maxX;
            CenterX = centerX;
            HalfWidth = halfWidth;
            X = Clamp(heroX - centerX);
        }

        /// <summary>主角移动之后调用:按死区规则更新镜头。</summary>
        public void Follow(double heroX)
        {
            double screen = heroX - X;
            if (screen < CenterX - HalfWidth)
            {
                X = heroX - (CenterX - HalfWidth);
            }
            else if (screen > CenterX + HalfWidth)
            {
                X = heroX - (CenterX + HalfWidth);
            }
            X = Clamp(X);
        }

        /// <summary>直接放到某个镜头 x(用来对照原版截图)。</summary>
        public void Place(double x)
        {
            X = Clamp(x);
        }

        private double Clamp(double x)
        {
            return x < MinX ? MinX : (x > MaxX ? MaxX : x);
        }
    }

    /// <summary>
    /// 城镇里可以用键盘操作的主角:状态(站立/行走、朝向)、位置、在可走区域里的移动,以及按状态选 .ani 帧并用换装合成成精灵。
    /// 行走速度是在原版连拍上标定的(水平约 222.5 像素/秒、竖直约 90 像素/秒,证据 E-066);竖直速度只有一个房间里的两段样本,
    /// 斜着走时两个方向各按各的速度(是否归一化没有样本)。没有跳跃、奔跑与攻击。
    /// </summary>
    public sealed class TownHero
    {
        public const int ObjectId = -2;

        /// <summary>水平行走速度(像素/秒)。在赛丽亚房间向左走的 11 个点上线性回归得到 222.5。</summary>
        public const double DefaultWalkSpeedX = 222.5;

        /// <summary>竖直行走速度(像素/秒)。在副本领主房里向下走的 4 个点回归得 92.7,向上走的 2 点得 87.4,取 90。</summary>
        public const double DefaultWalkSpeedY = 90.0;

        /// <summary>
        /// 行走动画的播放倍率(证据 E-066):.ani 里每帧 100 毫秒,但原版连拍里行走一帧只有约 83 毫秒
        /// (在 23 个点上联合拟合起步时间与每帧时长,可行范围 82.4 到 84.3 毫秒,即倍率 1.19 到 1.21);站立动画按 .ani 的原速(倍率 1.0,30 张都对得上)。
        /// 倍率与角色脚本 [move speed] 的关系没有研究,取 1.2。
        /// </summary>
        public const double DefaultMoveAnimationRate = 1.2;

        private readonly AniFile _rest;
        private readonly AniFile _move;
        private readonly string _restPath;
        private readonly string _movePath;
        private readonly AniTimeline _restTimeline;
        private readonly AniTimeline _moveTimeline;
        private readonly IReadOnlyList<WorldRect> _area;
        private readonly IWalkArea _walk;
        private double _motionMs;
        private AniFile[] _attacks = new AniFile[0];
        private string[] _attackPaths = new string[0];
        private AniTimeline[] _attackTimelines = new AniTimeline[0];
        private bool _attackQueued;
        private AniFile _battleRest;
        private string _battleRestPath;
        private AniTimeline _battleRestTimeline;
        private bool _battleStance;

        /// <summary>攻击动画的播放倍率。取 1.0(.ani 原速);原版连拍里没有量过攻击一击的时长(证据 E-069),角色脚本的 [attack speed] 与它的关系没有研究。</summary>
        public const double DefaultAttackAnimationRate = 1.0;

        public AvatarOutfit Outfit { get; set; }

        /// <summary>可走区域(矩形的并集);空表示不限制。用 <see cref="Walk"/> 取一般意义上的可走区域(副本房间不是矩形)。</summary>
        public IReadOnlyList<WorldRect> Area => _area;

        /// <summary>一般意义上的可走区域。</summary>
        public IWalkArea Walk => _walk;

        public double X { get; private set; }
        public double Y { get; private set; }
        public bool FacingLeft { get; private set; }
        public HeroMotion Motion { get; private set; }
        public double WalkSpeedX { get; set; } = DefaultWalkSpeedX;
        public double WalkSpeedY { get; set; } = DefaultWalkSpeedY;
        public double MoveAnimationRate { get; set; } = DefaultMoveAnimationRate;
        public double AttackAnimationRate { get; set; } = DefaultAttackAnimationRate;

        /// <summary>角色脚本 [attack motion] 里的连招段数(没载入攻击动作时为 0)。</summary>
        public int AttackCount => _attacks.Length;

        /// <summary>
        /// 角色脚本的 [move speed](没有则 0)。与实测的行走速度 <see cref="WalkSpeedX"/>(像素/秒)的比,用来把怪物脚本的 [move speed] 换算成像素/秒
        /// (证据 E-069):两者是同一个单位只是假设,原版的换算没有读出来。
        /// </summary>
        public int MoveSpeedStat { get; private set; }

        /// <summary>正在挥的是连招第几段(从 0 数);不在攻击时为 0。</summary>
        public int AttackStage { get; private set; }

        /// <summary>从开始玩起累计挥出的次数(每开始一击加 1)。命中判定用它给"同一击只打中同一目标一次"去重。</summary>
        public long SwingNumber { get; private set; }

        /// <summary>是否正在挥击。</summary>
        public bool IsAttacking => Motion == HeroMotion.Attack;

        /// <summary>当前这一击的动画文件名(小写,形如 attack1.ani);不在攻击时为 null。</summary>
        public string AttackMotionFile
        {
            get
            {
                if (!IsAttacking || (uint)AttackStage >= (uint)_attackPaths.Length)
                {
                    return null;
                }
                string path = _attackPaths[AttackStage];
                return path.Substring(path.LastIndexOf('/') + 1).ToLowerInvariant();
            }
        }

        /// <summary>当前这一击已经过去的动画毫秒数(已乘播放倍率)。</summary>
        public long AttackAnimationMs => (long)(_motionMs * AttackAnimationRate);

        /// <summary>当前动作已经播放的毫秒数(状态一变就从 0 重新数)。</summary>
        public long MotionMs => (long)_motionMs;

        public TownHero(AvatarOutfit outfit, AniFile rest, string restPath, AniFile move, string movePath,
            IReadOnlyList<WorldRect> area, double x, double y, bool facingLeft)
            : this(outfit, rest, restPath, move, movePath, area ?? new List<WorldRect>(), null, x, y, facingLeft)
        {
        }

        public TownHero(AvatarOutfit outfit, AniFile rest, string restPath, AniFile move, string movePath,
            IWalkArea walk, double x, double y, bool facingLeft)
            : this(outfit, rest, restPath, move, movePath, new List<WorldRect>(), walk ?? throw new ArgumentNullException(nameof(walk)), x, y, facingLeft)
        {
        }

        private TownHero(AvatarOutfit outfit, AniFile rest, string restPath, AniFile move, string movePath,
            IReadOnlyList<WorldRect> area, IWalkArea walk, double x, double y, bool facingLeft)
        {
            Outfit = outfit ?? throw new ArgumentNullException(nameof(outfit));
            _rest = rest ?? throw new ArgumentNullException(nameof(rest));
            _move = move ?? throw new ArgumentNullException(nameof(move));
            _restPath = restPath;
            _movePath = movePath;
            _restTimeline = new AniTimeline(rest);
            _moveTimeline = new AniTimeline(move);
            _area = area;
            _walk = walk ?? new RectUnionArea(area);
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = HeroMotion.Rest;
        }

        /// <summary>城镇里站立用的角色脚本标签(证据 E-065)。</summary>
        public const string TownRestTag = "[rest motion]";

        /// <summary>副本里站立用的标签:战斗待机动作(证据 E-067)。</summary>
        public const string DungeonRestTag = "[waiting motion]";

        /// <summary>
        /// 按角色脚本的 <paramref name="restTag"/>(默认 [rest motion])与 [move motion] 取站立与行走动画(路径相对角色脚本所在目录),并创建主角。
        /// </summary>
        public static TownHero Create(DnfAssets assets, string chrPath, AvatarOutfit outfit, IReadOnlyList<WorldRect> area,
            double x, double y, bool facingLeft, string restTag = TownRestTag)
        {
            LoadMotions(assets, chrPath, restTag, out string restPath, out string movePath, out AniFile rest, out AniFile move);
            var hero = new TownHero(outfit, rest, restPath, move, movePath, area, x, y, facingLeft);
            hero.LoadAttackMotions(assets, chrPath);
            hero.LoadMoveSpeedStat(assets, chrPath);
            return hero;
        }

        /// <summary>同 <see cref="Create(DnfAssets, string, AvatarOutfit, IReadOnlyList{WorldRect}, double, double, bool, string)"/>,可走区域用一般的 <see cref="IWalkArea"/>(副本房间的瓦片通行表)。</summary>
        public static TownHero Create(DnfAssets assets, string chrPath, AvatarOutfit outfit, IWalkArea walk,
            double x, double y, bool facingLeft, string restTag = TownRestTag)
        {
            LoadMotions(assets, chrPath, restTag, out string restPath, out string movePath, out AniFile rest, out AniFile move);
            var hero = new TownHero(outfit, rest, restPath, move, movePath, walk, x, y, facingLeft);
            hero.LoadAttackMotions(assets, chrPath);
            hero.LoadMoveSpeedStat(assets, chrPath);
            return hero;
        }

        private static void LoadMotions(DnfAssets assets, string chrPath, string restTag, out string restPath, out string movePath,
            out AniFile rest, out AniFile move)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            string restRel = null, moveRel = null;
            foreach (ScriptLine line in doc)
            {
                if (restRel == null && line.TagIs(restTag))
                {
                    restRel = line.FirstString();
                }
                else if (moveRel == null && line.TagIs("[move motion]"))
                {
                    moveRel = line.FirstString();
                }
            }
            if (restRel == null || moveRel == null)
            {
                throw new DnfFormatException("角色脚本缺少 " + restTag + " 或 [move motion]: " + chrPath);
            }
            restPath = PvfPath.ResolveRelativeTo(chrPath, restRel);
            movePath = PvfPath.ResolveRelativeTo(chrPath, moveRel);
            rest = assets.LoadAni(restPath) ?? throw new DnfFormatException("缺少站立动画: " + restPath);
            move = assets.LoadAni(movePath) ?? throw new DnfFormatException("缺少行走动画: " + movePath);
        }

        /// <summary>把主角直接摆到指定的位置、朝向与动作进度(用来与原版截图逐帧对照,以及读档式的初始化)。</summary>
        public void Place(double x, double y, bool facingLeft, HeroMotion motion, long motionMs)
        {
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = motion == HeroMotion.Attack && _attacks.Length == 0 ? HeroMotion.Rest : motion;
            _motionMs = motionMs < 0 ? 0 : motionMs;
            AttackStage = 0;
            _attackQueued = false;
        }

        /// <summary>摆成"正在挥连招第 <paramref name="stage"/> 段、已播放 <paramref name="motionMs"/> 毫秒(按墙钟)"。没载入攻击动作时无效。</summary>
        public void PlaceAttack(double x, double y, bool facingLeft, int stage, long motionMs)
        {
            if (_attacks.Length == 0)
            {
                Place(x, y, facingLeft, HeroMotion.Rest, motionMs);
                return;
            }
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = HeroMotion.Attack;
            AttackStage = stage < 0 ? 0 : (stage >= _attacks.Length ? _attacks.Length - 1 : stage);
            _motionMs = motionMs < 0 ? 0 : motionMs;
            _attackQueued = false;
        }

        /// <summary>
        /// 载入战斗站立动作 [waiting motion] 作为"战斗姿态"的站立动画(<see cref="BattleStance"/> 为真时用它,否则用创建时的站立动画)。
        /// 缺这个标签或动画时返回 false,姿态保持不变。证据 E-070:房间里还有活着的怪物时主角站的是 [waiting motion],怪物都死光之后站的是 [rest motion]。
        /// </summary>
        public bool LoadBattleRest(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs(DungeonRestTag))
                {
                    continue;
                }
                string rel = line.FirstString();
                string path = rel == null ? null : PvfPath.ResolveRelativeTo(chrPath, rel);
                AniFile ani = path == null ? null : assets.LoadAni(path);
                if (ani == null || ani.Frames.Length == 0)
                {
                    return false;
                }
                _battleRest = ani;
                _battleRestPath = path;
                _battleRestTimeline = new AniTimeline(ani);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 战斗姿态:为真时站立用 <see cref="LoadBattleRest"/> 载入的动作。没载入时恒为假。
        /// 站着的时候切换姿态,站立动作从头播放。
        /// </summary>
        public bool BattleStance
        {
            get => _battleStance;
            set
            {
                bool next = value && _battleRest != null;
                if (next != _battleStance)
                {
                    _battleStance = next;
                    if (Motion == HeroMotion.Rest)
                    {
                        _motionMs = 0;
                    }
                }
            }
        }

        /// <summary>读 .chr 的 [move speed](第一个数,真实脚本里是浮点标记;取整);没有这个标签就是 0。</summary>
        public void LoadMoveSpeedStat(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            MoveSpeedStat = 0;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[move speed]"))
                {
                    double? n = line.FirstNumber();
                    MoveSpeedStat = n.HasValue ? (int)Math.Round(n.Value) : 0;
                    break;
                }
            }
        }

        /// <summary>
        /// 读 .chr 的 [attack motion](相对角色脚本所在目录的一串 .ani),作为连招的各段。读不到就保持没有攻击。
        /// </summary>
        public void LoadAttackMotions(DnfAssets assets, string chrPath)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            var anis = new List<AniFile>();
            var paths = new List<string>();
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[attack motion]"))
                {
                    continue;
                }
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind != ScriptValueKind.String || string.IsNullOrEmpty(v.Text))
                    {
                        continue;
                    }
                    string path = PvfPath.ResolveRelativeTo(chrPath, v.Text);
                    AniFile ani = assets.LoadAni(path);
                    if (ani == null || ani.Frames.Length == 0)
                    {
                        continue;
                    }
                    anis.Add(ani);
                    paths.Add(path);
                }
                break;
            }
            _attacks = anis.ToArray();
            _attackPaths = paths.ToArray();
            _attackTimelines = new AniTimeline[_attacks.Length];
            for (int i = 0; i < _attacks.Length; i++)
            {
                _attackTimelines[i] = new AniTimeline(_attacks[i]);
            }
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒。<paramref name="dirX"/>、<paramref name="dirY"/> 是方向键状态(取符号:负为左/上,正为右/下)。
        /// 有任何方向就进入行走,否则站立;状态变化时动作从头播放。水平方向非 0 时朝向跟着变,只按上下时保持原朝向。
        /// 移动被可走区域挡住时停在边界上,但动作仍按行走播放(与原版一致:撞墙后脚还在迈,证据 E-066)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY)
        {
            Update(deltaMs, dirX, dirY, false);
        }

        /// <summary>
        /// 同 <see cref="Update(double, int, int)"/>,多一个攻击键:站立或行走时按下就起手连招第 0 段(起手那一刻可以转身,之后朝向锁定,不能走动);
        /// 挥击中再按一次,挥完这一击后接下一段(最后一段之后回到站立)。连按缓冲的时间窗没有样本,按"挥击中任何时刻按下都算"处理(证据 E-069)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            dirX = Math.Sign(dirX);
            dirY = Math.Sign(dirY);
            if (Motion == HeroMotion.Attack)
            {
                AdvanceAttack(deltaMs, attackPressed);
                return;
            }
            if (attackPressed && _attacks.Length > 0)
            {
                if (dirX != 0)
                {
                    FacingLeft = dirX < 0;
                }
                BeginAttack(0, 0.0);
                AdvanceAttack(deltaMs, false);
                return;
            }
            bool moving = dirX != 0 || dirY != 0;
            HeroMotion next = moving ? HeroMotion.Move : HeroMotion.Rest;
            if (dirX != 0)
            {
                FacingLeft = dirX < 0;
            }
            if (next != Motion)
            {
                Motion = next;
                _motionMs = 0;
            }
            _motionMs += deltaMs;
            if (moving)
            {
                double seconds = deltaMs / 1000.0;
                X = MoveAxis(X, dirX * WalkSpeedX * seconds, true);
                Y = MoveAxis(Y, dirY * WalkSpeedY * seconds, false);
            }
        }

        private void BeginAttack(int stage, double carryMs)
        {
            Motion = HeroMotion.Attack;
            AttackStage = stage;
            _motionMs = carryMs;
            _attackQueued = false;
            SwingNumber++;
        }

        /// <summary>这一击的墙钟时长(毫秒):动画总时长除以播放倍率,向上取整。</summary>
        public double AttackDurationMs(int stage)
        {
            if ((uint)stage >= (uint)_attackTimelines.Length)
            {
                return 0.0;
            }
            double rate = AttackAnimationRate > 0.0 ? AttackAnimationRate : 1.0;
            return Math.Ceiling(_attackTimelines[stage].TotalMilliseconds / rate);
        }

        private void AdvanceAttack(double deltaMs, bool attackPressed)
        {
            if (attackPressed && AttackStage + 1 < _attacks.Length)
            {
                _attackQueued = true;
            }
            _motionMs += deltaMs;
            double duration = AttackDurationMs(AttackStage);
            if (_motionMs < duration)
            {
                return;
            }
            double carry = _motionMs - duration;
            if (_attackQueued && AttackStage + 1 < _attacks.Length)
            {
                BeginAttack(AttackStage + 1, carry);
                return;
            }
            Motion = HeroMotion.Rest;
            AttackStage = 0;
            _attackQueued = false;
            _motionMs = 0;
        }

        /// <summary>沿一个轴走 <paramref name="delta"/>,每次最多一像素地检查可走区域,走到边界就停在边界上。</summary>
        private double MoveAxis(double from, double delta, bool horizontal)
        {
            if (delta == 0 || _walk.IsUnrestricted)
            {
                return from + delta;
            }
            double pos = from;
            double remaining = Math.Abs(delta);
            double sign = delta < 0 ? -1.0 : 1.0;
            while (remaining > 0)
            {
                double step = remaining < 1.0 ? remaining : 1.0;
                double candidate = pos + sign * step;
                if (!InArea(horizontal ? candidate : X, horizontal ? Y : candidate))
                {
                    // 这一步出界:往前挪到刚好还在区域内的最远处(边界是整数,先试到边界上)
                    double edge = sign < 0 ? Math.Ceiling(candidate) : Math.Floor(candidate);
                    if (sign < 0 ? edge > pos : edge < pos)
                    {
                        edge = pos;
                    }
                    if (InArea(horizontal ? edge : X, horizontal ? Y : edge))
                    {
                        pos = edge;
                    }
                    else
                    {
                        // 整数边界本身不在区域内(瓦片通行格是半开区间):二分到边界,停在区域内的一侧
                        double inside = pos, outside = candidate;
                        for (int i = 0; i < 24; i++)
                        {
                            double mid = (inside + outside) * 0.5;
                            if (InArea(horizontal ? mid : X, horizontal ? Y : mid))
                            {
                                inside = mid;
                            }
                            else
                            {
                                outside = mid;
                            }
                        }
                        pos = inside;
                    }
                    break;
                }
                pos = candidate;
                remaining -= step;
            }
            return pos + 0.0; // 把 -0.0 变成 0.0(碰到 x = 0 的边界时会出现)
        }

        private bool InArea(double x, double y)
        {
            return _walk.Contains(x, y);
        }

        /// <summary>当前动画与帧:站立或行走的 .ani、它的路径、此刻显示的帧号。</summary>
        public AniFile CurrentAni => Motion == HeroMotion.Attack ? _attacks[AttackStage] : (Motion == HeroMotion.Move ? _move : (_battleStance ? _battleRest : _rest));

        public string CurrentAnimationPath => Motion == HeroMotion.Attack ? _attackPaths[AttackStage] : (Motion == HeroMotion.Move ? _movePath : (_battleStance ? _battleRestPath : _restPath));

        public int CurrentFrame => Motion == HeroMotion.Attack
            ? _attackTimelines[AttackStage].FrameAt(AttackAnimationMs)
            : (Motion == HeroMotion.Move
                ? _moveTimeline.FrameAt((long)(_motionMs * MoveAnimationRate))
                : (_battleStance ? _battleRestTimeline : _restTimeline).FrameAt(MotionMs));

        /// <summary>主角的精灵(按换装图层顺序),位置模型与 NPC 相同。</summary>
        public List<SceneSprite> Sprites(DnfAssets assets, TownProjection projection)
        {
            int wx = (int)Math.Floor(X);
            int wy = (int)Math.Floor(Y);
            return Outfit.SpritesAt(assets, CurrentAni, CurrentAnimationPath, CurrentFrame, projection, wx, wy, 0, FacingLeft, wy, ObjectId);
        }
    }

    /// <summary>带攻击键的场景游戏:每帧多一个"攻击键刚按下"的输入(副本房间里的战斗,证据 E-069)。</summary>
    public interface IActionSession : IHeroSession
    {
        void Update(double deltaMs, int dirX, int dirY, bool attackPressed);
    }

    /// <summary>
    /// 带一个可操作主角与跟随镜头的场景游戏(城镇或副本房间)。视图层(Unity 的 TownView)只依赖这个接口:
    /// 每帧 <see cref="Update"/> 推进主角与镜头,再用 <see cref="Build"/> 取绘制列表。
    /// </summary>
    public interface IHeroSession
    {
        string MapPath { get; }
        TownHero Hero { get; }
        TownCamera Camera { get; }
        int LayerCount { get; }
        int NpcCount { get; }
        void Update(double deltaMs, int dirX, int dirY);
        List<SceneSprite> Build(long timeMs);
    }

    /// <summary>
    /// 一场城镇游戏:地图图层、NPC、主角与镜头。每帧先 <see cref="Update"/> 推进主角,再 <see cref="Build"/> 取绘制列表
    /// (地图图层与 NPC 的动画按墙钟时间 <c>timeMs</c> 播放,主角按自己的状态计时)。
    /// </summary>
    public sealed class TownSession : IHeroSession
    {
        /// <summary>纵向常数,E-024 在网关地图上拟合出的 200。</summary>
        public const int DefaultGroundY = 200;

        private readonly DnfAssets _assets;
        private readonly List<TownScene.ResolvedLayer> _layers;
        private readonly List<TownScene.ResolvedNpc> _npcs;

        public string MapPath { get; }
        public TownHero Hero { get; }
        public TownCamera Camera { get; }
        public int GroundY { get; }
        public int NpcCount => _npcs.Count;
        public int LayerCount => _layers.Count;

        /// <summary>此刻的投影:镜头 x 由主角位置按 <see cref="Camera"/> 算出。</summary>
        public TownProjection Projection => new TownProjection(Camera.IntX, GroundY);

        public TownSession(DnfAssets assets, string mapPath, TownHero hero, TownCamera camera, int groundY = DefaultGroundY)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            MapPath = mapPath ?? throw new ArgumentNullException(nameof(mapPath));
            Hero = hero ?? throw new ArgumentNullException(nameof(hero));
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            GroundY = groundY;
            _layers = TownScene.ResolveLayers(assets, mapPath);
            _npcs = TownScene.Resolve(assets, TownScene.ReadMapNpcs(assets, mapPath));
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Hero.Update(deltaMs, dirX, dirY);
            Camera.Follow(Hero.X);
        }

        public List<SceneSprite> Build(long timeMs)
        {
            TownProjection projection = Projection;
            List<SceneSprite> hero = Hero.Sprites(_assets, projection);
            return TownScene.BuildResolvedScene(_assets, _layers, _npcs, projection, timeMs, hero);
        }
    }

    /// <summary>某张城镇地图上主角的出生点与镜头范围(都是在原版连拍上量出来的,证据 E-066;没有量过的地图没有这张表)。</summary>
    public readonly struct TownSlice
    {
        public double SpawnX { get; }
        public double SpawnY { get; }
        public bool SpawnFacingLeft { get; }
        public int CameraMinX { get; }
        public int CameraMaxX { get; }

        public TownSlice(double spawnX, double spawnY, bool spawnFacingLeft, int cameraMinX, int cameraMaxX)
        {
            SpawnX = spawnX;
            SpawnY = spawnY;
            SpawnFacingLeft = spawnFacingLeft;
            CameraMinX = cameraMinX;
            CameraMaxX = cameraMaxX;
        }
    }

    /// <summary>
    /// 演示用的默认主角(鬼剑士):用游戏数据自带的 *_default 装备加一件起始武器,站立与行走动画取自 swordman.chr。
    /// 这是一个"能在城镇里走动"的最小角色,不是角色创建、装备栏或存档;武器是演示选择。
    /// </summary>
    public static class DemoHero
    {
        public const string SwordmanChrPath = "character/swordman/swordman.chr";
        public const string SwordmanJobTag = "[swordman]";
        public const string SwordmanEquipRoot = "equipment/character/swordman";

        /// <summary>起始武器(铁匠铺新手刀,最低等级 1),相对 <see cref="SwordmanEquipRoot"/>。</summary>
        public const string StarterWeapon = "weapon/katana/kat_damaged.equ";

        /// <summary>赛丽亚房间(网关地图)的出生点与镜头范围:出生在 (615, 234) 朝右,镜头 x 在 0 到 96 之间。</summary>
        public static readonly TownSlice SeriaRoom = new TownSlice(615, 234, false, 0, 96);

        /// <summary>地图是否有量过的出生点与镜头范围。</summary>
        public static bool TryGetSlice(string mapPath, out TownSlice slice)
        {
            if (string.Equals(mapPath, "map/elvengard/(r)gate.map", StringComparison.OrdinalIgnoreCase))
            {
                slice = SeriaRoom;
                return true;
            }
            slice = default;
            return false;
        }

        /// <summary>造一个默认外观的鬼剑士,可走区域取自地图的 [virtual movable area]。</summary>
        public static TownHero CreateSwordman(DnfAssets assets, string mapPath, double x, double y, bool facingLeft, string weaponEqu = StarterWeapon,
            string restTag = TownHero.TownRestTag)
        {
            AvatarClass cls = AvatarClass.FromChr(assets, SwordmanChrPath, SwordmanJobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, SwordmanEquipRoot, weaponEqu);
            return TownHero.Create(assets, SwordmanChrPath, outfit, MapScript.ReadVirtualMovableArea(assets, mapPath), x, y, facingLeft, restTag);
        }

        /// <summary>造出地图上的主角与镜头并组成一场城镇游戏(出生点与镜头范围取自 <paramref name="slice"/>)。</summary>
        public static TownSession CreateSession(DnfAssets assets, string mapPath, TownSlice slice, string weaponEqu = StarterWeapon)
        {
            TownHero hero = CreateSwordman(assets, mapPath, slice.SpawnX, slice.SpawnY, slice.SpawnFacingLeft, weaponEqu);
            var camera = new TownCamera(slice.CameraMinX, slice.CameraMaxX, hero.X);
            return new TownSession(assets, mapPath, hero, camera);
        }
    }
}
