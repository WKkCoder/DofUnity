using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>城镇会话对外说明"主角走进了哪个出口"的接口(流程据此换区域或打开世界地图)。</summary>
    public interface ITownExitSource
    {
        /// <summary>主角走进、还没被流程处理的出口;没有则为 null。会话换掉之前一直保持。</summary>
        TownExit? PendingExit { get; }

        /// <summary>某个出口"往外推"的方向 (dx, dy),恰有一个非零;不认识的出口全为 0。自动机据此决定往哪个方向推。</summary>
        void OutwardOf(TownExit exit, out int dx, out int dy);

        /// <summary>主角的位置与朝向(流程带着朝向过出口,自动机据位置决定往哪走)。</summary>
        double HeroX { get; }

        double HeroY { get; }

        bool HeroFacingLeft { get; }
    }

    /// <summary>
    /// 出口的触发规则(证据 E-095,推断):主角站在出口矩形里、并且正朝"外面"推方向键就走出去。
    /// "外面" = 出口矩形最靠近的那条可走区域外包矩形的边的方向(上、下、左、右):赛丽亚房间的出口在下边、艾尔文防线的四个出口各在上下左右;
    /// 触发由方向键决定而不是位置——主角被墙挡住时仍在推,所以贴着区域边缘站进出口矩形再推一下就走了。
    /// 刚落在出口里时(落脚点就是通回来源的那个出口的中心)先解除触发,走出矩形之后才重新有效;不然一落地就被弹回去。
    /// 没有写可走区域(外包矩形未知)时,出口里按任意方向键都算。
    /// 这一整套触发规则没有在客户端代码里找到对应的函数,是按数据形状与原版连拍里的落脚点(镜头位置)推出来的。
    /// </summary>
    public sealed class TownExitTracker
    {
        private readonly List<TownExit> _exits = new List<TownExit>();
        private readonly List<bool> _disarmed = new List<bool>();
        private readonly List<int[]> _outward = new List<int[]>();

        /// <param name="exits">地图的全部出口;没有去处的(目标是 -2 之类)和不是地下城入口的会被忽略。</param>
        /// <param name="bounds">可走区域的外包矩形;未知时为 null。</param>
        public TownExitTracker(IEnumerable<TownExit> exits, WorldRect? bounds)
        {
            Bounds = bounds;
            foreach (TownExit e in exits ?? new List<TownExit>())
            {
                if (!e.HasTarget && !e.IsDungeonGate)
                {
                    continue;
                }
                _exits.Add(e);
                _disarmed.Add(false);
                int dx = 0, dy = 0;
                if (bounds.HasValue)
                {
                    Outward(e.Rect, bounds.Value, out dx, out dy);
                }
                _outward.Add(new[] { dx, dy });
            }
        }

        public WorldRect? Bounds { get; }

        /// <summary>会触发的出口(过滤之后)。</summary>
        public IReadOnlyList<TownExit> Exits => _exits;

        /// <summary>出口矩形的"外面"方向:(dx, dy) 恰有一个非零。最靠近外包矩形哪条边就朝哪边;离两条边一样近时取水平。</summary>
        public static void Outward(WorldRect exit, WorldRect bounds, out int dx, out int dy)
        {
            double cx = exit.X + exit.Width * 0.5;
            double cy = exit.Y + exit.Height * 0.5;
            double left = cx - bounds.X;
            double right = bounds.X + bounds.Width - cx;
            double top = cy - bounds.Y;
            double bottom = bounds.Y + bounds.Height - cy;
            double best = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            dx = 0;
            dy = 0;
            if (left == best)
            {
                dx = -1;
            }
            else if (right == best)
            {
                dx = 1;
            }
            else if (top == best)
            {
                dy = -1;
            }
            else
            {
                dy = 1;
            }
        }

        /// <summary>主角落在 (x, y):包含这个点的出口先解除触发,直到主角走出它。</summary>
        public void Arrive(double x, double y)
        {
            for (int i = 0; i < _exits.Count; i++)
            {
                _disarmed[i] = _exits[i].Rect.Contains(x, y);
            }
        }

        /// <summary>
        /// 主角在 (x, y)、方向键 (dirX, dirY)(各为 -1、0、1)时,这一帧有没有走进出口;有则返回它。
        /// 走出矩形的出口会重新生效。
        /// </summary>
        public TownExit? Step(double x, double y, int dirX, int dirY)
        {
            TownExit? taken = null;
            for (int i = 0; i < _exits.Count; i++)
            {
                if (!_exits[i].Rect.Contains(x, y))
                {
                    _disarmed[i] = false;
                    continue;
                }
                if (taken.HasValue || _disarmed[i])
                {
                    continue;
                }
                if (Pushing(_outward[i], dirX, dirY))
                {
                    taken = _exits[i];
                }
            }
            return taken;
        }

        private static bool Pushing(int[] outward, int dirX, int dirY)
        {
            if (outward[0] == 0 && outward[1] == 0)
            {
                return dirX != 0 || dirY != 0;
            }
            return (outward[0] != 0 && Math.Sign(dirX) == outward[0]) || (outward[1] != 0 && Math.Sign(dirY) == outward[1]);
        }

        /// <summary>某个出口"往外推"的方向;不是本表里的出口则全为 0。</summary>
        public void OutwardOf(TownExit exit, out int dx, out int dy)
        {
            for (int i = 0; i < _exits.Count; i++)
            {
                if (_exits[i].Rect.X == exit.Rect.X && _exits[i].Rect.Y == exit.Rect.Y && _exits[i].Town == exit.Town && _exits[i].Area == exit.Area)
                {
                    dx = _outward[i][0];
                    dy = _outward[i][1];
                    return;
                }
            }
            dx = 0;
            dy = 0;
        }
    }

    /// <summary>城镇里一块区域的静态信息:类型、地图、可走区域、出口。由 <see cref="TownAtlas"/> 读出。</summary>
    public sealed class TownAreaInfo
    {
        public int Town { get; }
        public TownArea Def { get; }
        public string MapPath { get; }
        public IReadOnlyList<WorldRect> Walkable { get; }
        public IReadOnlyList<TownExit> Exits { get; }

        /// <summary>可走区域的外包矩形;没有写可走区域时为 null。</summary>
        public WorldRect? Bounds { get; }

        public TownAreaInfo(int town, TownArea def, string mapPath, IReadOnlyList<WorldRect> walkable, IReadOnlyList<TownExit> exits)
        {
            Town = town;
            Def = def;
            MapPath = mapPath;
            Walkable = walkable ?? new List<WorldRect>();
            Exits = exits ?? new List<TownExit>();
            Bounds = BoundsOf(Walkable);
        }

        public int Area => Def.Id;

        public TownAreaKind Kind => Def.Kind;

        public static WorldRect? BoundsOf(IReadOnlyList<WorldRect> rects)
        {
            if (rects == null || rects.Count == 0)
            {
                return null;
            }
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (WorldRect r in rects)
            {
                x0 = Math.Min(x0, r.X);
                y0 = Math.Min(y0, r.Y);
                x1 = Math.Max(x1, r.X + r.Width);
                y1 = Math.Max(y1, r.Y + r.Height);
            }
            return new WorldRect(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>通往 (town, area) 的出口(同一目标有多个时取第一个)。</summary>
        public bool TryFindExitTo(int town, int area, out TownExit exit)
        {
            foreach (TownExit e in Exits)
            {
                if (e.HasTarget && e.Town == town && e.Area == area)
                {
                    exit = e;
                    return true;
                }
            }
            exit = default;
            return false;
        }

        /// <summary>通往世界地图(地下城入口)的出口。</summary>
        public bool TryFindDungeonGate(out TownExit exit)
        {
            foreach (TownExit e in Exits)
            {
                if (e.IsDungeonGate)
                {
                    exit = e;
                    return true;
                }
            }
            exit = default;
            return false;
        }

        /// <summary>把 (x, y) 挪到可走区域里最近的点(已经在里面就不动);没有写可走区域时原样返回。</summary>
        public void NearestWalkable(double x, double y, out double outX, out double outY)
        {
            outX = x;
            outY = y;
            double best = double.MaxValue;
            foreach (WorldRect r in Walkable)
            {
                double cx = Math.Max(r.X, Math.Min(r.X + r.Width, x));
                double cy = Math.Max(r.Y, Math.Min(r.Y + r.Height, y));
                double d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                if (d < best)
                {
                    best = d;
                    outX = cx;
                    outY = cy;
                }
            }
        }
    }

    /// <summary>主角在新区域里的出生状态。</summary>
    public readonly struct TownArrival
    {
        public double X { get; }
        public double Y { get; }
        public bool FacingLeft { get; }

        public TownArrival(double x, double y, bool facingLeft)
        {
            X = x;
            Y = y;
            FacingLeft = facingLeft;
        }
    }

    /// <summary>
    /// 城镇图册(证据 E-095):读 town/town.lst 与每座城镇的 .twn,再按需读每块区域地图的 [virtual movable area] 与 [town movable area],
    /// 回答"这块区域长什么样、出口通哪里、从哪里进来落在哪"。纯数据:不读图像,所以用合成脚本就能测。
    ///
    /// 落脚点规则(推断,按原版连拍的镜头位置标定;服务端的区域移动函数把落脚坐标与朝向当参数收,所以落脚位置大概率由客户端的请求给出,
    /// 这是推断,没有读到客户端发请求的函数):
    /// · 从相邻区域走过来(<see cref="TownEntry.Exit"/>):落在这块区域里通回来源的那个出口矩形的中心,再挪进可走区域;
    /// · 从副本或选择界面回来(<see cref="TownEntry.Return"/>):落在通往世界地图的出口的中心;
    /// · 登录或其他情形:网关区域落在 [gate] 给的位置,其他区域落在可走区域外包矩形的中心。
    /// 朝向:网关登录朝右;走出口时朝向跟着主角走(<see cref="TownPlace.FacingLeft"/>);从副本回来朝向区域里面(出口在水平方向偏左的朝右,偏右的朝左)。落地时的朝向没有样本,是推断。
    /// </summary>
    public sealed class TownAtlas
    {
        private readonly Dictionary<int, string> _townPaths;
        private readonly Func<string, ScriptLine[]> _read;
        private readonly Func<string, bool> _exists;
        private readonly Dictionary<int, TownDef> _towns = new Dictionary<int, TownDef>();
        private readonly Dictionary<long, TownAreaInfo> _areas = new Dictionary<long, TownAreaInfo>();

        public TownAtlas(IDictionary<int, string> townPaths, Func<string, ScriptLine[]> readScript, Func<string, bool> exists)
        {
            _townPaths = new Dictionary<int, string>(townPaths ?? throw new ArgumentNullException(nameof(townPaths)));
            _read = readScript ?? throw new ArgumentNullException(nameof(readScript));
            _exists = exists ?? (_ => false);
        }

        /// <summary>用游戏数据打开(town/town.lst)。</summary>
        public static TownAtlas Open(DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            return new TownAtlas(TownDef.ReadList(assets), assets.ReadScript, p => assets.Pvf.TryGetEntry(PvfPath.Normalize(p), out _));
        }

        public IEnumerable<int> TownIds => _townPaths.Keys;

        /// <summary>城镇定义;列表里没有这个编号或读不到时为 null。</summary>
        public TownDef GetTown(int town)
        {
            if (_towns.TryGetValue(town, out TownDef cached))
            {
                return cached;
            }
            TownDef def = null;
            if (_townPaths.TryGetValue(town, out string path))
            {
                def = TownDef.Parse(path, _read(path));
            }
            _towns[town] = def;
            return def;
        }

        /// <summary>某块区域的信息;城镇或区域不存在、或地图读不到时为 null。</summary>
        public TownAreaInfo GetArea(int town, int area)
        {
            long key = ((long)town << 32) | (uint)area;
            if (_areas.TryGetValue(key, out TownAreaInfo cached))
            {
                return cached;
            }
            TownAreaInfo info = null;
            TownDef def = GetTown(town);
            TownArea a = def?.FindArea(area);
            if (a != null)
            {
                string map = TownDef.ResolveMapPath(a.RawMapPath, _exists);
                ScriptLine[] doc = _read(map);
                if (doc != null)
                {
                    var walk = new List<WorldRect>();
                    foreach (ScriptLine line in doc)
                    {
                        if (!line.TagIs("[virtual movable area]"))
                        {
                            continue;
                        }
                        List<int> v = line.Ints();
                        for (int i = 0; i + 3 < v.Count; i += 4)
                        {
                            walk.Add(new WorldRect(v[i], v[i + 1], v[i + 2], v[i + 3]));
                        }
                    }
                    info = new TownAreaInfo(town, a, map, walk, MapScript.ParseTownExits(doc));
                }
            }
            _areas[key] = info;
            return info;
        }

        /// <summary>城镇 <paramref name="town"/> 里第一块带通往世界地图出口的区域(按区域编号从小到大找);没有则返回 null。</summary>
        public TownPlace? FindDungeonGate(int town)
        {
            TownDef def = GetTown(town);
            if (def == null)
            {
                return null;
            }
            var ids = new List<int>();
            foreach (TownArea a in def.Areas)
            {
                ids.Add(a.Id);
            }
            ids.Sort();
            foreach (int id in ids)
            {
                TownAreaInfo info = GetArea(town, id);
                if (info != null && info.TryFindDungeonGate(out _))
                {
                    return new TownPlace(town, id, TownEntry.Return);
                }
            }
            return null;
        }

        /// <summary>主角出生在 <paramref name="place"/> 时的位置与朝向。区域不存在时抛 <see cref="DnfFormatException"/>。</summary>
        public TownArrival Arrive(TownPlace place)
        {
            TownAreaInfo info = GetArea(place.Town, place.Area)
                ?? throw new DnfFormatException("城镇图册里没有这块区域: " + place.Town + "/" + place.Area);
            double x = 0, y = 0;
            bool have = false, fromExit = false;
            TownExit e;
            if (place.Entry == TownEntry.Exit && info.TryFindExitTo(place.FromTown, place.FromArea, out e))
            {
                have = true;
                fromExit = true;
                x = e.Rect.X + e.Rect.Width * 0.5;
                y = e.Rect.Y + e.Rect.Height * 0.5;
            }
            else if (place.Entry == TownEntry.Return && info.TryFindDungeonGate(out e))
            {
                have = true;
                fromExit = true;
                x = e.Rect.X + e.Rect.Width * 0.5;
                y = e.Rect.Y + e.Rect.Height * 0.5;
            }
            if (!have && info.Def.TryGetGatePoint(out int gx, out int gy))
            {
                have = true;
                x = gx;
                y = gy;
            }
            if (!have)
            {
                if (info.Bounds.HasValue)
                {
                    WorldRect b = info.Bounds.Value;
                    x = b.X + b.Width * 0.5;
                    y = b.Y + b.Height * 0.5;
                }
            }
            info.NearestWalkable(x, y, out x, out y);
            bool facingLeft = false;
            if (place.Entry == TownEntry.Exit)
            {
                facingLeft = place.FacingLeft;                       // 走出口:朝向跟着主角走
            }
            else if (fromExit && info.Bounds.HasValue)
            {
                WorldRect b = info.Bounds.Value;
                facingLeft = x > b.X + b.Width * 0.5;                // 从副本回来:朝向区域里面
            }
            return new TownArrival(x, y, facingLeft);
        }

        /// <summary>
        /// 从 <paramref name="from"/> 往地下城入口走,第一步该走的出口(在最短路上,按出口在地图脚本里的顺序广度优先):
        /// 已经在入口区域时就是通往世界地图的出口。够不着时返回 false。自动机用。
        /// </summary>
        public bool TryRouteToDungeonGate(TownPlace from, out TownExit first)
        {
            first = default;
            var seen = new HashSet<long> { Key(from.Town, from.Area) };
            var queue = new Queue<KeyValuePair<TownPlace, TownExit?>>();
            queue.Enqueue(new KeyValuePair<TownPlace, TownExit?>(from, null));
            while (queue.Count > 0)
            {
                KeyValuePair<TownPlace, TownExit?> cur = queue.Dequeue();
                TownAreaInfo info = GetArea(cur.Key.Town, cur.Key.Area);
                if (info == null)
                {
                    continue;
                }
                if (info.TryFindDungeonGate(out TownExit gate))
                {
                    first = cur.Value ?? gate;
                    return true;
                }
                foreach (TownExit e in info.Exits)
                {
                    if (!e.HasTarget || !seen.Add(Key(e.Town, e.Area)))
                    {
                        continue;
                    }
                    queue.Enqueue(new KeyValuePair<TownPlace, TownExit?>(new TownPlace(e.Town, e.Area), cur.Value ?? e));
                }
            }
            return false;
        }

        private static long Key(int town, int area)
        {
            return ((long)town << 32) | (uint)area;
        }
    }
}
