using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 一场副本房间里的游戏:房间内容(背景、瓦片、图层、障碍物、出口)、主角与跟随镜头。
    /// 每帧先 <see cref="Update"/> 推进主角与镜头,再 <see cref="Build"/> 取绘制列表。
    /// 主角站在房间的通行表上走动(<see cref="RoomWalkArea"/>);镜头沿用城镇里量到的死区跟随,范围 [0, 房间宽 − 屏宽](证据 E-066、E-068)。
    /// 目前没有怪物、攻击、出口的开关与换房间。
    /// </summary>
    public sealed class RoomSession : IActionSession
    {
        /// <summary>纵向常数,与城镇相同(洛兰第一、第二个房间上量到,证据 E-067)。</summary>
        public const int DefaultGroundY = TownSession.DefaultGroundY;

        private readonly DnfAssets _assets;

        public RoomContent Content { get; }
        public RoomMap Map => Content.Map;
        public string MapPath => Content.Map.MapPath;
        public TownHero Hero { get; }
        public TownCamera Camera { get; }
        public int GroundY { get; }

        private RoomGrid _grid;

        /// <summary>
        /// 舞台格:地图的瓦片通行表,再把每个出口的门体盖上去(证据 E-074)。进房间的落点用它判断主角站不站得下(<see cref="RoomArrival"/>)。
        /// 出口在房间造好之后不再变,所以第一次用到时建一次。
        /// </summary>
        public RoomGrid Grid
        {
            get
            {
                if (_grid == null)
                {
                    var grid = new RoomGrid(Map);
                    foreach (RoomGate gate in Content.Gates)
                    {
                        if (gate.Body != null)
                        {
                            grid.Stamp(gate.Body);
                        }
                    }
                    _grid = grid;
                }
                return _grid;
            }
        }

        /// <summary>为 false 时不画主角(离屏渲染一帧与 CPU 合成逐字节对照时用)。</summary>
        public bool HeroVisible { get; set; } = true;

        public int LayerCount => Content.Layers.Count + Content.Backgrounds.Count;
        public int NpcCount => 0;

        /// <summary>
        /// 屏幕震动给世界画面的偏移(像素;证据 E-133):整个世界层(地图、角色、怪物、特效、伤害数字)一起挪,界面不挪。0 是不震。
        /// 由战斗会话每一步按 [SHAKING] 写入。
        /// </summary>
        public int ShakeX { get; set; }

        public int ShakeY { get; set; }

        /// <summary>此刻的投影:镜头 x、y 由主角位置按 <see cref="Camera"/> 算出(纵向的镜头只有带扩展瓦片的地图才会动:地面常数减去镜头 y),再加上震屏偏移。</summary>
        public TownProjection Projection => new TownProjection(Camera.IntX - ShakeX, GroundY - Camera.IntY + ShakeY);

        public RoomSession(DnfAssets assets, RoomContent content, TownHero hero, TownCamera camera, int groundY = DefaultGroundY)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Hero = hero ?? throw new ArgumentNullException(nameof(hero));
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            GroundY = groundY;
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Update(deltaMs, dirX, dirY, false);
        }

        /// <summary>同 <see cref="Update(double, int, int)"/>,多一个攻击键(见 <see cref="TownHero.Update(double, int, int, bool)"/>)。</summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, false);
        }

        /// <summary>同上,再多一个跳跃键(见 <see cref="TownHero.Update(double, int, int, bool, bool)"/>)。</summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            Hero.Update(deltaMs, dirX, dirY, attackPressed, jumpPressed);
            Camera.Follow(Hero.X, Hero.Y);
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return Build(timeMs, null);
        }

        /// <summary>绘制列表,并把 <paramref name="extra"/>(怪物、血条等外部拼出来的精灵)和主角一起排进深度顺序。</summary>
        public List<SceneSprite> Build(long timeMs, IReadOnlyList<SceneSprite> extra)
        {
            var sprites = new List<SceneSprite>();
            if (HeroVisible)
            {
                sprites.AddRange(Hero.Sprites(_assets, Projection));
            }
            if (extra != null)
            {
                sprites.AddRange(extra);
            }
            return RoomScene.Build(_assets, Content, Camera.IntX - ShakeX, GroundY, timeMs, sprites.Count == 0 ? null : sprites, Camera.IntY - ShakeY);
        }
    }

    /// <summary>
    /// 某个房间在演示里的摆法:出生点、朝向,以及四个方向的出口是什么。
    /// 出生点只在量过的房间上有(<see cref="HasSpawn"/>);没量过的按出生区取默认(见 <see cref="DemoRoom.DefaultSpawn"/>)。
    /// </summary>
    public readonly struct RoomSlice
    {
        public bool HasSpawn { get; }
        public double SpawnX { get; }
        public double SpawnY { get; }
        public bool SpawnFacingLeft { get; }

        /// <summary>副本定义(.dgn)的 PVF 路径,出口物件的编号从它的 [pathgate object] 取。</summary>
        public string DungeonPath { get; }

        public GateKind Left { get; }
        public GateKind Right { get; }
        public GateKind Up { get; }
        public GateKind Down { get; }

        public RoomSlice(bool hasSpawn, double spawnX, double spawnY, bool spawnFacingLeft, string dungeonPath,
            GateKind left, GateKind right, GateKind up, GateKind down)
        {
            HasSpawn = hasSpawn;
            SpawnX = spawnX;
            SpawnY = spawnY;
            SpawnFacingLeft = spawnFacingLeft;
            DungeonPath = dungeonPath;
            Left = left;
            Right = right;
            Up = up;
            Down = down;
        }

        public GateKind KindOf(GateSide side)
        {
            switch (side)
            {
                case GateSide.Left: return Left;
                case GateSide.Right: return Right;
                case GateSide.Up: return Up;
                default: return Down;
            }
        }
    }

    /// <summary>演示用的副本房间:主角用 <see cref="DemoHero"/> 的默认外观,站立用战斗待机动作(证据 E-067)。</summary>
    public static class DemoRoom
    {
        public const string LorienDungeon = "dungeon/act1/lorien.dgn";

        /// <summary>
        /// 洛兰第一个房间:出生点 (876, 342) 朝右(在原版截图上以 47% 的像素吻合找到,周围一像素内都明显更低,证据 E-068)。
        /// 出口:右边通向普通房间(原版截图核对,证据 E-067);下边通向普通房间(被界面挡住,没有核对);左边是墙(没有核对);上边没有。
        /// </summary>
        public static readonly RoomSlice LorienFirst = new RoomSlice(true, 876, 342, false, LorienDungeon,
            GateKind.Wall, GateKind.Normal, GateKind.None, GateKind.Normal);

        /// <summary>
        /// 洛兰第二个房间:左边通向普通房间(原版截图核对)、右边是墙(原版截图核对)、上边与下边没有出口。
        /// 出生点没有量过(原版是从左边的门进来的)。
        /// (出口的取法后来由迷宫给出:这个房间的迷宫格只有左出口,证据 E-070;之前把下边猜成了普通出口,已更正。)
        /// </summary>
        public static readonly RoomSlice LorienSecond = new RoomSlice(false, 0, 0, false, LorienDungeon,
            GateKind.Normal, GateKind.Wall, GateKind.None, GateKind.None);

        /// <summary>地图是否有量过的摆法。</summary>
        public static bool TryGetSlice(string mapPath, out RoomSlice slice)
        {
            if (string.Equals(mapPath, "map/lorien/s2.map", StringComparison.OrdinalIgnoreCase))
            {
                slice = LorienFirst;
                return true;
            }
            if (string.Equals(mapPath, "map/lorien/s3.map", StringComparison.OrdinalIgnoreCase))
            {
                slice = LorienSecond;
                return true;
            }
            slice = default;
            return false;
        }

        /// <summary>
        /// 脚本是不是一张副本房间地图:有 [pathgate pos](四个出口的位置)。城镇地图也有 [tile] 列表,所以不能用 [tile] 判断;
        /// 城镇地图没有 [pathgate pos](全库的判别没有统计过,只核对了洛兰的房间与赛丽亚房间两类)。
        /// </summary>
        public static bool IsRoomMap(DnfAssets assets, string mapPath)
        {
            ScriptLine[] doc = assets.ReadScript(mapPath);
            if (doc == null)
            {
                return false;
            }
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[pathgate pos]"))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 没量过出生点的房间:取 [dungeon start area] 的中心,再挪到最近的能走的点;地图没有出生区时取房间中央、可走带的中间。
        /// 这是一个能用的默认值,不是原版的规则(只有一个房间的出生点量过,而它不在出生区中心)。
        /// </summary>
        public static void DefaultSpawn(RoomMap map, out double x, out double y)
        {
            double cx, cy;
            if (map.StartArea.HasValue)
            {
                WorldRect r = map.StartArea.Value;
                cx = r.X + r.Width * 0.5;
                cy = r.Y + r.Height * 0.5;
            }
            else
            {
                cx = map.Width * 0.5;
                cy = 240;
            }
            map.NearestWalkable(cx, cy, out x, out y);
        }

        /// <summary>按摆法造出口:每个方向 + 种类取 <see cref="PathGateSet"/> 里的物件编号,位置取地图的 [pathgate pos]。找不到物件或位置的方向跳过。</summary>
        public static List<RoomGate> BuildGates(DnfAssets assets, RoomMap map, RoomSlice slice)
        {
            PathGateSet set = slice.DungeonPath == null ? null : PathGateSet.Read(assets, slice.DungeonPath);
            return BuildGates(assets, map, set, slice.KindOf);
        }

        /// <summary>
        /// 按出口物件编号 <paramref name="set"/> 与"每个方向画什么"造出口;<paramref name="kindOf"/> 给每个方向的种类。
        /// 没有出口物件编号时返回空列表。
        /// </summary>
        public static List<RoomGate> BuildGates(DnfAssets assets, RoomMap map, PathGateSet set, Func<GateSide, GateKind> kindOf)
        {
            var gates = new List<RoomGate>();
            if (set == null)
            {
                return gates;
            }
            PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
            foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
            {
                GateKind kind = kindOf(side);
                int id = set.IdFor(side, kind);
                int[] pos = map.Gates[(int)side];
                if (id < 0 || pos == null || (kind == GateKind.Wall && IsStalePlaceholder(map, side, pos)))
                {
                    continue;
                }
                GateObject obj = GateObject.Read(assets, catalog, id);
                if (obj != null)
                {
                    gates.Add(new RoomGate(assets, obj, side, pos[0], pos[1], map.GateRects[(int)side]));
                }
            }
            return gates;
        }

        /// <summary>
        /// 墙(没有出口的左右两侧用来封洞的物件)的位置是不是一个没用上的占位值:左墙落在地图的右半边、右墙落在左半边的,封不住任何洞(<b>推断</b>,非原版规则)。
        /// 客户端的门体碰撞会把左出口的通行表第 0 列、右出口的第 13 列沿用到地图边缘(证据 E-074),真把这样的墙摆上去,
        /// 一道横穿整张地图的实心带会把房间切成两半(全库扫描里只有两张房间这样,<c>dnf gate-body-check</c> 能复现;不摆就都连通);原版里这样的房间照样能玩,
        /// 所以这样的墙不摆。出口(普通与首领)不受这条影响:它们的位置就是出口所在,迷宫说有就有。
        /// </summary>
        public static bool IsStalePlaceholder(RoomMap map, GateSide side, int[] pos)
        {
            if (map == null || pos == null || pos.Length < 2)
            {
                return false;
            }
            return side == GateSide.Left ? pos[0] * 2 > map.Width : side == GateSide.Right && pos[0] * 2 < map.Width;
        }

        /// <summary>演示用主角的默认外观(默认鬼剑士 + 武器)。</summary>
        public static AvatarOutfit DefaultOutfit(DnfAssets assets, string weaponEqu = DemoHero.StarterWeapon)
        {
            AvatarClass cls = AvatarClass.FromChr(assets, DemoHero.SwordmanChrPath, DemoHero.SwordmanJobTag);
            return AvatarOutfit.CreateDefault(assets, cls, DemoHero.SwordmanEquipRoot, weaponEqu);
        }

        /// <summary>
        /// 通用的房间构造:把 <paramref name="outfit"/> 外观的鬼剑士放在 (x, y)、<paramref name="facingLeft"/> 朝向;
        /// 站立动作是 [rest motion],另外载入战斗站立动作 [waiting motion] 并先进入战斗姿态(证据 E-070;之后由战斗会话按怪物死活切换)。
        /// 镜头先居中再夹在 [0, 房间宽 − 屏宽] 之内。
        /// </summary>
        public static RoomSession CreateRoom(DnfAssets assets, RoomMap map, IReadOnlyList<RoomGate> gates, AvatarOutfit outfit,
            double x, double y, bool facingLeft)
        {
            RoomContent content = RoomContent.Load(assets, map, gates);
            TownHero hero = TownHero.Create(assets, DemoHero.SwordmanChrPath, outfit, new RoomWalkArea(map, content.Objects), x, y, facingLeft,
                TownHero.TownRestTag);
            hero.LoadBattleRest(assets, DemoHero.SwordmanChrPath);
            hero.BattleStance = true;
            hero.Blocker = new GateBodies(gates, hero.BodyWidth, hero.BodyDepth);   // 出口的门框与墙挡路(证据 E-072、E-074)
            var camera = new TownCamera(0, map.MaxCameraX, hero.X, maxY: map.ExtendedHeightPixels);
            if (map.ExtendedHeightPixels > 0)
            {
                camera.Follow(hero.X, hero.Y);   // 纵向镜头从最大值起步,按死区追到主角
            }
            return new RoomSession(assets, content, hero, camera);
        }

        /// <summary>
        /// 造出一个房间里的游戏:读地图,按摆法(<see cref="TryGetSlice"/>)摆出口,把默认外观的鬼剑士放在出生点。
        /// 没有摆法的房间没有出口,出生点取 <see cref="DefaultSpawn"/>。
        /// </summary>
        public static RoomSession CreateSession(DnfAssets assets, string mapPath, string weaponEqu = DemoHero.StarterWeapon)
        {
            RoomMap map = RoomMap.Read(assets, mapPath);
            bool hasSlice = TryGetSlice(mapPath, out RoomSlice slice);
            List<RoomGate> gates = hasSlice ? BuildGates(assets, map, slice) : null;

            double x, y;
            bool facingLeft = false;
            if (hasSlice && slice.HasSpawn)
            {
                x = slice.SpawnX;
                y = slice.SpawnY;
                facingLeft = slice.SpawnFacingLeft;
            }
            else
            {
                DefaultSpawn(map, out x, out y);
            }
            return CreateRoom(assets, map, gates, DefaultOutfit(assets, weaponEqu), x, y, facingLeft);
        }
    }
}
