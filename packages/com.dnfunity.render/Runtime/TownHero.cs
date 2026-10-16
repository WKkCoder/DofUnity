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

    public enum HeroMotion
    {
        /// <summary>站立(城镇里用 [rest motion],不是战斗用的 [waiting motion],证据 E-065)。</summary>
        Rest,

        /// <summary>行走([move motion])。</summary>
        Move,
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
        /// 倍率与角色脚本 [move speed] 850 的关系没有研究,取 1.2。
        /// </summary>
        public const double DefaultMoveAnimationRate = 1.2;

        private readonly AniFile _rest;
        private readonly AniFile _move;
        private readonly string _restPath;
        private readonly string _movePath;
        private readonly AniTimeline _restTimeline;
        private readonly AniTimeline _moveTimeline;
        private readonly IReadOnlyList<WorldRect> _area;
        private double _motionMs;

        public AvatarOutfit Outfit { get; set; }

        /// <summary>可走区域(矩形的并集);空表示不限制。</summary>
        public IReadOnlyList<WorldRect> Area => _area;

        public double X { get; private set; }
        public double Y { get; private set; }
        public bool FacingLeft { get; private set; }
        public HeroMotion Motion { get; private set; }
        public double WalkSpeedX { get; set; } = DefaultWalkSpeedX;
        public double WalkSpeedY { get; set; } = DefaultWalkSpeedY;
        public double MoveAnimationRate { get; set; } = DefaultMoveAnimationRate;

        /// <summary>当前动作已经播放的毫秒数(状态一变就从 0 重新数)。</summary>
        public long MotionMs => (long)_motionMs;

        public TownHero(AvatarOutfit outfit, AniFile rest, string restPath, AniFile move, string movePath,
            IReadOnlyList<WorldRect> area, double x, double y, bool facingLeft)
        {
            Outfit = outfit ?? throw new ArgumentNullException(nameof(outfit));
            _rest = rest ?? throw new ArgumentNullException(nameof(rest));
            _move = move ?? throw new ArgumentNullException(nameof(move));
            _restPath = restPath;
            _movePath = movePath;
            _restTimeline = new AniTimeline(rest);
            _moveTimeline = new AniTimeline(move);
            _area = area ?? new List<WorldRect>();
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = HeroMotion.Rest;
        }

        /// <summary>
        /// 按角色脚本的 [rest motion] 与 [move motion] 取站立与行走动画(路径相对角色脚本所在目录),并创建主角。
        /// </summary>
        public static TownHero Create(DnfAssets assets, string chrPath, AvatarOutfit outfit, IReadOnlyList<WorldRect> area,
            double x, double y, bool facingLeft)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            string restRel = null, moveRel = null;
            foreach (ScriptLine line in doc)
            {
                if (restRel == null && line.TagIs("[rest motion]"))
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
                throw new DnfFormatException("角色脚本缺少 [rest motion] 或 [move motion]: " + chrPath);
            }
            string restPath = PvfPath.ResolveRelativeTo(chrPath, restRel);
            string movePath = PvfPath.ResolveRelativeTo(chrPath, moveRel);
            AniFile rest = assets.LoadAni(restPath) ?? throw new DnfFormatException("缺少站立动画: " + restPath);
            AniFile move = assets.LoadAni(movePath) ?? throw new DnfFormatException("缺少行走动画: " + movePath);
            return new TownHero(outfit, rest, restPath, move, movePath, area, x, y, facingLeft);
        }

        /// <summary>把主角直接摆到指定的位置、朝向与动作进度(用来与原版截图逐帧对照,以及读档式的初始化)。</summary>
        public void Place(double x, double y, bool facingLeft, HeroMotion motion, long motionMs)
        {
            X = x;
            Y = y;
            FacingLeft = facingLeft;
            Motion = motion;
            _motionMs = motionMs < 0 ? 0 : motionMs;
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒。<paramref name="dirX"/>、<paramref name="dirY"/> 是方向键状态(取符号:负为左/上,正为右/下)。
        /// 有任何方向就进入行走,否则站立;状态变化时动作从头播放。水平方向非 0 时朝向跟着变,只按上下时保持原朝向。
        /// 移动被可走区域挡住时停在边界上,但动作仍按行走播放(与原版一致:撞墙后脚还在迈,证据 E-066)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY)
        {
            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            dirX = Math.Sign(dirX);
            dirY = Math.Sign(dirY);
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

        /// <summary>沿一个轴走 <paramref name="delta"/>,每次最多一像素地检查可走区域,走到边界就停在边界上。</summary>
        private double MoveAxis(double from, double delta, bool horizontal)
        {
            if (delta == 0 || _area.Count == 0)
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
                    break;
                }
                pos = candidate;
                remaining -= step;
            }
            return pos;
        }

        private bool InArea(double x, double y)
        {
            foreach (WorldRect r in _area)
            {
                if (r.Contains(x, y))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>当前动画与帧:站立或行走的 .ani、它的路径、此刻显示的帧号。</summary>
        public AniFile CurrentAni => Motion == HeroMotion.Move ? _move : _rest;

        public string CurrentAnimationPath => Motion == HeroMotion.Move ? _movePath : _restPath;

        public int CurrentFrame => Motion == HeroMotion.Move
            ? _moveTimeline.FrameAt((long)(_motionMs * MoveAnimationRate))
            : _restTimeline.FrameAt(MotionMs);

        /// <summary>主角的精灵(按换装图层顺序),位置模型与 NPC 相同。</summary>
        public List<SceneSprite> Sprites(DnfAssets assets, TownProjection projection)
        {
            int wx = (int)Math.Floor(X);
            int wy = (int)Math.Floor(Y);
            return Outfit.SpritesAt(assets, CurrentAni, CurrentAnimationPath, CurrentFrame, projection, wx, wy, 0, FacingLeft, wy, ObjectId);
        }
    }

    /// <summary>
    /// 一场城镇游戏:地图图层、NPC、主角与镜头。每帧先 <see cref="Update"/> 推进主角,再 <see cref="Build"/> 取绘制列表
    /// (地图图层与 NPC 的动画按墙钟时间 <c>timeMs</c> 播放,主角按自己的状态计时)。
    /// </summary>
    public sealed class TownSession
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
        public static TownHero CreateSwordman(DnfAssets assets, string mapPath, double x, double y, bool facingLeft, string weaponEqu = StarterWeapon)
        {
            AvatarClass cls = AvatarClass.FromChr(assets, SwordmanChrPath, SwordmanJobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, SwordmanEquipRoot, weaponEqu);
            return TownHero.Create(assets, SwordmanChrPath, outfit, MapScript.ReadVirtualMovableArea(assets, mapPath), x, y, facingLeft);
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
