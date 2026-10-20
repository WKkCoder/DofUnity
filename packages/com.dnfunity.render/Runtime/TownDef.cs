using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 城镇里一块区域的类型(证据 E-095)。.twn 的 [area] 里写成 "[normal]"、"[gate]" 一类的字符串;
    /// 枚举值是服务端 .twn 加载函数(0x08aadaa8,按 [normal]、[gate]、[fishing place]、[pub]、[dungeon gate]、[safe zone against a]、[safe zone against b]
    /// 的次序依次比较并给 0 到 6)的内部编号;客户端是否用同样的编号没有核对。数据里只出现普通、网关、地下城入口与两种安全区。
    /// </summary>
    public enum TownAreaKind
    {
        Normal = 0,

        /// <summary>网关:赛丽亚的房间一类(登录进城的落脚处)。后面跟两个数(进入时主角的位置)。</summary>
        Gate = 1,

        FishingPlace = 2,
        Pub = 3,

        /// <summary>地下城入口区域:走到它的 (-1, -1) 出口会打开世界地图(选择地下城)。后面跟一个数(世界地图编号,worldmap.lst 的键)。</summary>
        DungeonGate = 4,

        SafeZoneA = 5,
        SafeZoneB = 6,
    }

    /// <summary>城镇定义(.twn)里的一块区域:区域编号、地图脚本、类型与小地图矩形。</summary>
    public sealed class TownArea
    {
        public int Id { get; }

        /// <summary>脚本里写的地图路径(相对 map/ 目录),例如 "Elvengard/Gate.map"。真正读哪个文件见 <see cref="TownDef.ResolveMapPath"/>。</summary>
        public string RawMapPath { get; }

        public TownAreaKind Kind { get; }

        /// <summary>类型后面的数:网关两个(进入位置),地下城入口一个(世界地图编号),其余没有。</summary>
        public IReadOnlyList<int> Params { get; }

        /// <summary>小地图上这块区域的矩形(左、上、右、下);没有写则为 null。</summary>
        public int[] MiniMapRect { get; }

        /// <summary>写了 [use private store] 的区域(只在一个城镇里出现,含义没有研究)。</summary>
        public bool UsesPrivateStore { get; }

        public TownArea(int id, string rawMapPath, TownAreaKind kind, IReadOnlyList<int> parameters, int[] miniMapRect, bool usesPrivateStore)
        {
            Id = id;
            RawMapPath = rawMapPath;
            Kind = kind;
            Params = parameters ?? new List<int>();
            MiniMapRect = miniMapRect;
            UsesPrivateStore = usesPrivateStore;
        }

        /// <summary>网关区域写了进入位置时给出它(世界坐标)。</summary>
        public bool TryGetGatePoint(out int x, out int y)
        {
            if (Kind == TownAreaKind.Gate && Params.Count >= 2)
            {
                x = Params[0];
                y = Params[1];
                return true;
            }
            x = 0;
            y = 0;
            return false;
        }

        /// <summary>地下城入口区域写了世界地图编号时给出它;否则为 -1。</summary>
        public int WorldMapId => Kind == TownAreaKind.DungeonGate && Params.Count >= 1 ? Params[0] : -1;
    }

    /// <summary>
    /// 城镇定义(town/*.twn,证据 E-095):一座城镇由若干区域(地图)组成。标签:[entering title](进城时显示的标题动画)、
    /// [cutscene image](加载画面的整屏插图,图名 + 帧号)、[area](区域编号、地图、小地图矩形、类型与类型参数,以 [/area] 结束)、
    /// [name](名字,字符串键)、[limit level]、[need quest]。town/town.lst 是"城镇编号 .twn 文件名"的列表。
    /// </summary>
    public sealed class TownDef
    {
        public const string ListPath = "town/town.lst";
        public const string Directory = "town/";

        public string Path { get; }

        /// <summary>进城标题动画的 PVF 路径(小写,已加 town/ 前缀),例如 town/title/elvengard.ani;没有时为 null。</summary>
        public string EnteringTitle { get; }

        /// <summary>加载画面插图的图包名(小写,带 sprite/),例如 sprite/map/cutscene/elvengard.img;没有时为 null。</summary>
        public string CutsceneImage { get; }

        public int CutsceneFrame { get; }

        /// <summary>[name] 的字符串键(形如 name_12345,指向字符串表);没有时为 null。</summary>
        public string NameKey { get; }

        public IReadOnlyList<TownArea> Areas { get; }

        public TownDef(string path, string enteringTitle, string cutsceneImage, int cutsceneFrame, string nameKey, IReadOnlyList<TownArea> areas)
        {
            Path = path;
            EnteringTitle = enteringTitle;
            CutsceneImage = cutsceneImage;
            CutsceneFrame = cutsceneFrame;
            NameKey = nameKey;
            Areas = areas ?? new List<TownArea>();
        }

        public TownArea FindArea(int id)
        {
            foreach (TownArea a in Areas)
            {
                if (a.Id == id)
                {
                    return a;
                }
            }
            return null;
        }

        /// <summary>
        /// 区域地图的 PVF 路径:脚本里的相对路径加上 map/ 前缀、转小写;同目录下带 "(r)" 前缀的同名文件存在时优先用它
        /// ("Elvengard/Gate.map" → map/elvengard/(r)gate.map;全库 77 个区域里 14 个走 "(r)" 版本,其余 63 个用原名,没有找不到的)。
        /// <paramref name="exists"/> 用来问 PVF 里有没有某个路径。
        /// </summary>
        public static string ResolveMapPath(string rawMapPath, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(rawMapPath))
            {
                return null;
            }
            string plain = "map/" + WorldMapDef.ToScriptPath(rawMapPath);
            int slash = plain.LastIndexOf('/');
            string variant = plain.Substring(0, slash + 1) + "(r)" + plain.Substring(slash + 1);
            if (exists != null && exists(variant))
            {
                return variant;
            }
            return plain;
        }

        /// <summary>解析 .twn;没有任何可识别的标签时返回 null。</summary>
        public static TownDef Parse(string path, ScriptLine[] doc)
        {
            if (doc == null)
            {
                return null;
            }
            string title = null, cutscene = null, nameKey = null;
            int cutsceneFrame = 0;
            var areas = new List<TownArea>();
            bool any = false;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[entering title]"))
                {
                    any = true;
                    string t = line.FirstString();
                    // 个别城镇写的是不带文件名的 "Title/"(没有标题动画):当作没有
                    title = t != null && t.EndsWith(".ani", StringComparison.OrdinalIgnoreCase) ? Directory + WorldMapDef.ToScriptPath(t) : null;
                }
                else if (line.TagIs("[cutscene image]"))
                {
                    any = true;
                    cutscene = WorldMapDef.ToImageName(line.FirstString());
                    List<int> n = line.Ints();
                    cutsceneFrame = n.Count > 0 ? n[0] : 0;
                }
                else if (line.TagIs("[name]"))
                {
                    any = true;
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.Keyed || v.Kind == ScriptValueKind.String)
                        {
                            nameKey = v.Text;
                            break;
                        }
                    }
                }
                else if (line.TagIs("[area]"))
                {
                    any = true;
                    TownArea area = ParseArea(line);
                    if (area != null)
                    {
                        areas.Add(area);
                    }
                }
            }
            return any ? new TownDef(path, title, cutscene, cutsceneFrame, nameKey, areas) : null;
        }

        private static TownArea ParseArea(ScriptLine line)
        {
            ScriptValue[] v = line.Values;
            int id = 0;
            bool haveId = false;
            string map = null;
            var kind = TownAreaKind.Normal;
            var parameters = new List<int>();
            int[] mini = null;
            bool store = false;
            for (int i = 0; i < v.Length; i++)
            {
                ScriptValue x = v[i];
                if (x.IsNumber)
                {
                    if (!haveId)
                    {
                        id = x.Int;
                        haveId = true;
                    }
                    else
                    {
                        parameters.Add(x.Int);
                    }
                    continue;
                }
                if (x.Kind != ScriptValueKind.String || x.Text == null)
                {
                    continue;
                }
                string t = x.Text.ToLowerInvariant();
                if (map == null && !t.StartsWith("[", StringComparison.Ordinal))
                {
                    map = x.Text;
                }
                else if (t == "[minimap rect]")
                {
                    var r = new List<int>();
                    while (i + 1 < v.Length && v[i + 1].IsNumber)
                    {
                        r.Add(v[++i].Int);
                    }
                    mini = r.Count >= 4 ? new[] { r[0], r[1], r[2], r[3] } : null;
                }
                else if (t == "[use private store]")
                {
                    store = true;
                    if (i + 1 < v.Length && v[i + 1].IsNumber)
                    {
                        i++; // 后面跟着的那个数是这个开关的值,不是类型参数
                    }
                }
                else if (TryParseKind(t, out TownAreaKind k))
                {
                    kind = k;
                    parameters.Clear();
                }
            }
            return haveId && map != null ? new TownArea(id, map, kind, parameters, mini, store) : null;
        }

        /// <summary>类型字符串("[normal]" 一类,小写)转成枚举。</summary>
        public static bool TryParseKind(string tag, out TownAreaKind kind)
        {
            switch (tag)
            {
                case "[normal]": kind = TownAreaKind.Normal; return true;
                case "[gate]": kind = TownAreaKind.Gate; return true;
                case "[fishing place]": kind = TownAreaKind.FishingPlace; return true;
                case "[pub]": kind = TownAreaKind.Pub; return true;
                case "[dungeon gate]": kind = TownAreaKind.DungeonGate; return true;
                case "[safe zone against a]": kind = TownAreaKind.SafeZoneA; return true;
                case "[safe zone against b]": kind = TownAreaKind.SafeZoneB; return true;
                default: kind = TownAreaKind.Normal; return false;
            }
        }

        public static TownDef Read(DnfAssets assets, string path)
        {
            return Parse(path, assets.ReadScript(path));
        }

        /// <summary>读 town/town.lst:城镇编号 → .twn 的 PVF 路径(文件名前加 town/,小写)。读不到返回空表。</summary>
        public static Dictionary<int, string> ReadList(DnfAssets assets)
        {
            return WorldMapDef.ParseList(assets.ReadScript(ListPath), Directory);
        }
    }

    /// <summary>
    /// 地图脚本 [town movable area] 里的一个出口(证据 E-095):世界坐标里的一个矩形,和矩形走进去之后去的地方 (城镇编号, 区域编号)。
    /// 目标是 (-1, -1) 表示地下城入口(打开世界地图);负数的其他目标(例如 -2)表示没有去处(在原版里这样的出口落在可走区域之外,走不到)。
    /// </summary>
    public readonly struct TownExit
    {
        public WorldRect Rect { get; }
        public int Town { get; }
        public int Area { get; }

        public TownExit(WorldRect rect, int town, int area)
        {
            Rect = rect;
            Town = town;
            Area = area;
        }

        public bool IsDungeonGate => Town == -1 && Area == -1;

        /// <summary>去另一块城镇区域(目标编号都不是负数)。</summary>
        public bool HasTarget => Town >= 0 && Area >= 0;

        public override string ToString()
        {
            return "exit(" + Rect.X + "," + Rect.Y + "," + Rect.Width + "," + Rect.Height + ")->" + Town + "/" + Area;
        }
    }

    /// <summary>主角落在城镇的哪里:城镇编号 + 区域编号,加上怎么来的(决定落脚点,见 <see cref="TownAtlas"/>)。</summary>
    public enum TownEntry
    {
        /// <summary>从城镇之外进来(登录、选角之后):网关区域落在 [gate] 给的位置,其他区域落在区域的中间。</summary>
        Login = 0,

        /// <summary>从相邻区域的出口走过来:落在这块区域里通回来源的那个出口的中心。</summary>
        Exit = 1,

        /// <summary>从副本或副本选择界面回来:落在地下城入口区域通往世界地图的那个出口的中心。</summary>
        Return = 2,
    }

    public readonly struct TownPlace : IEquatable<TownPlace>
    {
        public int Town { get; }
        public int Area { get; }
        public TownEntry Entry { get; }

        /// <summary>从哪块区域走过来(只在 <see cref="TownEntry.Exit"/> 时有意义)。</summary>
        public int FromTown { get; }

        public int FromArea { get; }

        /// <summary>走出口时主角的朝向(只在 <see cref="TownEntry.Exit"/> 时有意义):主角这个对象一路跟着走,过出口朝向不变。</summary>
        public bool FacingLeft { get; }

        public TownPlace(int town, int area, TownEntry entry = TownEntry.Login, int fromTown = -1, int fromArea = -1, bool facingLeft = false)
        {
            Town = town;
            Area = area;
            Entry = entry;
            FromTown = fromTown;
            FromArea = fromArea;
            FacingLeft = facingLeft;
        }

        public bool Equals(TownPlace other)
        {
            return Town == other.Town && Area == other.Area;
        }

        public override bool Equals(object obj)
        {
            return obj is TownPlace p && Equals(p);
        }

        public override int GetHashCode()
        {
            return Town * 397 ^ Area;
        }

        public override string ToString()
        {
            return Town + "/" + Area + " " + Entry;
        }
    }
}
