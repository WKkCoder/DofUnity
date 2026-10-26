using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>地图 [passive object] 里的一项:物件编号与世界坐标(脚本里每个物件是 编号、x、y、z 四个值)。</summary>
    public readonly struct PassivePlacement
    {
        public int Id { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public PassivePlacement(int id, int x, int y, int z)
        {
            Id = id;
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// 被动物件(.obj)里的"障碍物"一类:一个常驻动画([basic motion])加一个占地([width] 宽 深)与能否穿过([pass type])。
    /// 洛兰第二个房间里的两棵烧焦的树桩就是这一类(证据 E-068)。粒子光源(编号 5 与 6 的 [string data] 写法)与出口(<see cref="GateObject"/>)不在这里。
    /// 放在 <c>mapobject/breakableobject/</c> 目录下、带 [int data] 的,还带着可破坏物件的资料(<see cref="Breakable"/>,证据 E-126)。
    /// </summary>
    public sealed class PassiveObjectDef
    {
        /// <summary>可破坏物件所在的目录(<c>passiveobject/mapobject/breakableobject/</c>);客户端有专门的可破坏类,类别靠这个目录判断是推断(证据 E-126)。</summary>
        public const string BreakableFolder = "/breakableobject/";

        public int Id { get; }
        public string ScriptPath { get; }

        /// <summary>[layer],小写带方括号;没写按 "[normal]"。</summary>
        public string Layer { get; }

        /// <summary>[width] 的两个数:占地的宽(x 方向)与深(y 方向)。</summary>
        public int FootprintWidth { get; }
        public int FootprintDepth { get; }

        /// <summary>[pass type] 是 "[do not pass]" 时主角走不进占地。</summary>
        public bool Blocking { get; }

        /// <summary>[basic motion] 的动画(PVF 路径,已按 .obj 所在目录解析,小写)。</summary>
        public string MotionPath { get; }

        /// <summary>可破坏物件的资料(见 <see cref="BreakableData"/>);不是这一类是 null。</summary>
        public BreakableData Breakable { get; }

        public PassiveObjectDef(int id, string scriptPath, string layer, int footprintWidth, int footprintDepth, bool blocking, string motionPath,
            BreakableData breakable = null)
        {
            Id = id;
            ScriptPath = scriptPath;
            Layer = layer;
            FootprintWidth = footprintWidth;
            FootprintDepth = footprintDepth;
            Blocking = blocking;
            MotionPath = motionPath;
            Breakable = breakable;
        }

        /// <summary>读一个障碍物类的被动物件;编号不在目录里、脚本缺失、或没有 [basic motion] 时返回 null。</summary>
        public static PassiveObjectDef Read(DnfAssets assets, PassiveObjectCatalog catalog, int id)
        {
            string path = catalog.PathOf(id);
            ScriptLine[] doc = path == null ? null : assets.ReadScript(path);
            if (doc == null)
            {
                return null;
            }
            string layer = "[normal]";
            string motion = null;
            int width = 0, depth = 0;
            bool blocking = false;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[layer]"))
                {
                    layer = (line.FirstString() ?? layer).ToLowerInvariant();
                }
                else if (line.TagIs("[width]"))
                {
                    List<int> n = line.Ints();
                    width = n.Count > 0 ? n[0] : 0;
                    depth = n.Count > 1 ? n[1] : 0;
                }
                else if (line.TagIs("[pass type]"))
                {
                    blocking = string.Equals(line.FirstString(), "[do not pass]", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.TagIs("[basic motion]"))
                {
                    string rel = line.FirstString();
                    if (rel != null)
                    {
                        motion = PvfPath.ResolveRelativeTo(path, rel);
                    }
                }
            }
            if (motion == null)
            {
                return null;
            }
            BreakableData breakable = path.IndexOf(BreakableFolder, StringComparison.OrdinalIgnoreCase) >= 0 ? BreakableData.Read(path, doc) : null;
            return new PassiveObjectDef(id, path, layer, width, depth, blocking, motion, breakable);
        }
    }

    /// <summary>
    /// 摆在房间里的一个障碍物:定义 + 位置 + 已解析好的动画。
    /// 占地按"以摆放点为中心的宽×深矩形"算(宽、深取自 [width];中心的取法没有样本核对,是假设)。
    /// 可破坏物件还会被打:受击时换播一个受击动画(<see cref="PlayReaction"/>,播完回到基础动画)、打够了就消失(<see cref="Destroy"/>,
    /// 不再画、不再挡人),这些由战斗会话驱动,证据 E-126。
    /// </summary>
    public sealed class RoomObject
    {
        private readonly AniFile _ani;
        private readonly AniTimeline _timeline;
        private AniFile _reactionAni;
        private AniTimeline _reactionTimeline;
        private string _reactionPath;
        private double _reactionMs;

        public PassiveObjectDef Def { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Rank { get; }

        /// <summary>来自 [special passive object] 的物件带着它的摆放项(动作、序号);普通的 [passive object] 是 null。</summary>
        public SpecialPlacement Special { get; }

        /// <summary>已经被破坏:不再画、不再挡人。</summary>
        public bool Destroyed { get; private set; }

        /// <summary>基础动画([basic motion]);战斗里取它的受击框(类型 14)。</summary>
        public AniFile Ani => _ani;

        /// <summary>正在播受击动画。</summary>
        public bool Reacting => _reactionAni != null;

        private RoomObject(PassiveObjectDef def, AniFile ani, int x, int y, int z, SpecialPlacement special)
        {
            Def = def;
            _ani = ani;
            _timeline = new AniTimeline(ani);
            X = x;
            Y = y;
            Z = z;
            Rank = TownScene.LayerRank(def.Layer);
            Special = special;
        }

        /// <summary>动画缺失或没有帧时返回 null。</summary>
        public static RoomObject Create(DnfAssets assets, PassiveObjectDef def, PassivePlacement placement, SpecialPlacement special = null)
        {
            AniFile ani = assets.LoadAni(def.MotionPath);
            if (ani == null || ani.Frames.Length == 0)
            {
                return null;
            }
            return new RoomObject(def, ani, placement.X, placement.Y, placement.Z, special);
        }

        /// <summary>世界 (x, y) 是否落在这个物件的占地里(只有 [do not pass] 的物件才拦人,打碎之后不拦)。</summary>
        public bool Blocks(double x, double y)
        {
            if (!Def.Blocking || Destroyed)
            {
                return false;
            }
            return Math.Abs(x - X) < Def.FootprintWidth * 0.5 && Math.Abs(y - Y) < Def.FootprintDepth * 0.5;
        }

        /// <summary>打碎:之后不再画、不再挡人。</summary>
        public void Destroy()
        {
            Destroyed = true;
            _reactionAni = null;
        }

        /// <summary>
        /// 换播第 <paramref name="index"/> 个受击动画([etc motion] 的第几项),从头开始;播完自动回到基础动画。
        /// 没有这一项(空槽、越界、读不到动画)返回 false,什么也不改。
        /// </summary>
        public bool PlayReaction(DnfAssets assets, int index)
        {
            string path = Def.Breakable?.ReactionMotion(index);
            if (Destroyed || path == null)
            {
                return false;
            }
            AniFile ani = assets.LoadAni(path);
            if (ani == null || ani.Frames.Length == 0)
            {
                return false;
            }
            _reactionAni = ani;
            _reactionTimeline = new AniTimeline(ani);
            _reactionPath = path;
            _reactionMs = 0;
            return true;
        }

        /// <summary>受击动画推进 <paramref name="deltaMs"/> 毫秒;不循环的播完就回到基础动画(循环的一直播,直到下一次受击换掉它)。</summary>
        public void Advance(double deltaMs)
        {
            if (_reactionAni == null || deltaMs <= 0)
            {
                return;
            }
            _reactionMs += deltaMs;
            if (!_reactionTimeline.Loops && _reactionMs >= _reactionTimeline.TotalMilliseconds)
            {
                _reactionAni = null;
            }
        }

        public SceneSprite SpriteAt(DnfAssets assets, TownProjection projection, long timeMs)
        {
            if (Destroyed)
            {
                return null;
            }
            AniFile ani = _reactionAni ?? _ani;
            AniTimeline timeline = _reactionAni != null ? _reactionTimeline : _timeline;
            string path = _reactionAni != null ? _reactionPath : Def.MotionPath;
            AniSample sample = timeline.Sample(_reactionAni != null ? (long)_reactionMs : timeMs);
            if (sample.Frame < 0)
            {
                return null;
            }
            AniFrame f = ani.Frames[sample.Frame];
            if (f.ImageIndex < 0 || f.ImageIndex >= ani.ImagePaths.Length)
            {
                return null;
            }
            string img = "sprite/" + ani.ImagePaths[f.ImageIndex].Replace('\\', '/');
            if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite))
            {
                return null;
            }
            bool mirrored = f.TryGetScale(out float scaleX, out _) && scaleX < 0f;
            projection.TopLeft(X, Y, Z, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, sprite.Width, mirrored, out int sx, out int sy);
            bool additive = f.TryGetGraphicEffect(out int effect) && effect == AniGraphicEffect.Additive;
            return new SceneSprite(sprite, sx, sy, Y, -1, path, sample.Frame, mirrored,
                additive ? SpriteBlend.Additive : SpriteBlend.Normal, timeline.TintAlphaAt(_reactionAni != null ? (long)_reactionMs : timeMs), Rank);
        }
    }

    /// <summary>
    /// 副本定义([pathgate object])里的十个出口物件编号。顺序(对洛兰的十个 .obj 文件名核对过):
    /// 左普通、左首领、左墙、右普通、右首领、右墙、上普通、上首领、下普通、下首领;上下没有"墙"一项。
    /// </summary>
    public sealed class PathGateSet
    {
        public const int Count = 10;

        private readonly int[] _ids;

        public IReadOnlyList<int> Ids => _ids;

        public PathGateSet(IReadOnlyList<int> ids)
        {
            if (ids == null || ids.Count != Count)
            {
                throw new ArgumentException("[pathgate object] 需要 " + Count + " 个编号");
            }
            _ids = new int[Count];
            for (int i = 0; i < Count; i++)
            {
                _ids[i] = ids[i];
            }
        }

        /// <summary>读副本定义(.dgn)的 [pathgate object];缺失或个数不对时返回 null。</summary>
        public static PathGateSet Read(DnfAssets assets, string dungeonPath)
        {
            ScriptLine[] doc = assets.ReadScript(dungeonPath);
            if (doc == null)
            {
                return null;
            }
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[pathgate object]"))
                {
                    List<int> n = line.Ints();
                    return n.Count == Count ? new PathGateSet(n) : null;
                }
            }
            return null;
        }

        /// <summary>某个方向、某种出口的物件编号;没有这一种(上下的墙)返回 -1。</summary>
        public int IdFor(GateSide side, GateKind kind)
        {
            switch (kind)
            {
                case GateKind.Normal:
                    return _ids[side == GateSide.Left ? 0 : side == GateSide.Right ? 3 : side == GateSide.Up ? 6 : 8];
                case GateKind.Boss:
                    return _ids[side == GateSide.Left ? 1 : side == GateSide.Right ? 4 : side == GateSide.Up ? 7 : 9];
                case GateKind.Wall:
                    return side == GateSide.Left ? _ids[2] : side == GateSide.Right ? _ids[5] : -1;
                default:
                    return -1;
            }
        }
    }

    /// <summary>房间某一侧的出口是什么:没有(不画)、墙(这一侧不通)、通向普通房间、通向首领房间。</summary>
    public enum GateKind
    {
        None = 0,
        Wall = 1,
        Normal = 2,
        Boss = 3,
    }
}
