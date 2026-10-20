using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>世界地图上的一个副本入口:副本编号(dungeon/dungeon.lst 里的号)与解锁条件(任务号;-1 表示没有条件)。</summary>
    public readonly struct WorldMapDungeon
    {
        public int DungeonId { get; }

        /// <summary>解锁条件。.wdm 的 [dungeon] 里每个副本编号后面跟一个数:-1 或一个任务号(推断:没读客户端怎么用它)。</summary>
        public int Condition { get; }

        public bool Unconditional => Condition == -1;

        public WorldMapDungeon(int dungeonId, int condition)
        {
            DungeonId = dungeonId;
            Condition = condition;
        }
    }

    /// <summary>
    /// 世界地图定义(worldmap/*.wdm,证据 E-094):副本选择界面背后的整张地图。
    /// 标签:[map image] "图名" 帧号(整屏底图,图名要加 "sprite/" 才是图包里的名字)、[ui path](这张地图的界面脚本 .ui)、
    /// [dungeon](成对的"副本编号 解锁条件")、[name](地图名,字符串键)。其余标签(地狱派对、进行中任务等)没有读。
    /// worldmap/worldmap.lst 是"编号 .wdm 文件名"的列表。
    /// </summary>
    public sealed class WorldMapDef
    {
        public const string ListPath = "worldmap/worldmap.lst";

        public string Path { get; }

        /// <summary>底图的图包名,例如 sprite/worldmap/lorien.img(小写、带 sprite/);没有 [map image] 时为 null。</summary>
        public string MapImage { get; }

        public int MapFrame { get; }

        /// <summary>界面脚本在 PVF 里的路径(小写),例如 worldmap/ui/lorien.ui;没有 [ui path] 时为 null。</summary>
        public string UiPath { get; }

        public IReadOnlyList<WorldMapDungeon> Dungeons { get; }

        public WorldMapDef(string path, string mapImage, int mapFrame, string uiPath, IReadOnlyList<WorldMapDungeon> dungeons)
        {
            Path = path;
            MapImage = mapImage;
            MapFrame = mapFrame;
            UiPath = uiPath;
            Dungeons = dungeons ?? new List<WorldMapDungeon>();
        }

        /// <summary>脚本里写的图名("WorldMap/Lorien.img")转成图包名("sprite/worldmap/lorien.img")。</summary>
        public static string ToImageName(string scriptImage)
        {
            return scriptImage == null ? null : "sprite/" + scriptImage.Replace('\\', '/').ToLowerInvariant();
        }

        /// <summary>脚本里写的 PVF 路径统一成小写、正斜杠。</summary>
        public static string ToScriptPath(string path)
        {
            return path == null ? null : path.Replace('\\', '/').ToLowerInvariant();
        }

        /// <summary>解析世界地图定义;没有任何可识别的标签(不是 .wdm)时返回 null。</summary>
        public static WorldMapDef Parse(string path, ScriptLine[] doc)
        {
            if (doc == null)
            {
                return null;
            }
            string image = null;
            int frame = 0;
            string ui = null;
            var dungeons = new List<WorldMapDungeon>();
            bool any = false;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[map image]"))
                {
                    any = true;
                    image = ToImageName(line.FirstString());
                    List<int> n = line.Ints();
                    frame = n.Count > 0 ? n[0] : 0;
                }
                else if (line.TagIs("[ui path]"))
                {
                    any = true;
                    ui = ToScriptPath(line.FirstString());
                }
                else if (line.TagIs("[dungeon]"))
                {
                    any = true;
                    List<int> n = line.Ints();
                    for (int i = 0; i + 1 < n.Count; i += 2)
                    {
                        dungeons.Add(new WorldMapDungeon(n[i], n[i + 1]));
                    }
                }
            }
            return any ? new WorldMapDef(path, image, frame, ui, dungeons) : null;
        }

        public static WorldMapDef Read(DnfAssets assets, string path)
        {
            return Parse(path, assets.ReadScript(path));
        }

        /// <summary>读 worldmap/worldmap.lst:编号 → .wdm 的 PVF 路径(文件名前加 worldmap/,小写)。读不到返回空表。</summary>
        public static Dictionary<int, string> ReadList(DnfAssets assets)
        {
            return ParseList(assets.ReadScript(ListPath), "worldmap/");
        }

        /// <summary>
        /// 解析"编号 路径"成对出现的列表脚本(*.lst:文件开头没有标签,值依次是 编号 "路径" 编号 "路径" ……):
        /// 编号 → <paramref name="directory"/> 加上小写、正斜杠的路径(列表里的路径相对列表所在的目录)。重复的编号取第一个。
        /// </summary>
        public static Dictionary<int, string> ParseList(ScriptLine[] doc, string directory)
        {
            var map = new Dictionary<int, string>();
            if (doc == null)
            {
                return map;
            }
            foreach (ScriptLine line in doc)
            {
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 1 < v.Length; i++)
                {
                    if (v[i].IsNumber && v[i + 1].Kind == ScriptValueKind.String && v[i + 1].Text != null)
                    {
                        if (!map.ContainsKey(v[i].Int))
                        {
                            map.Add(v[i].Int, (directory ?? string.Empty) + ToScriptPath(v[i + 1].Text));
                        }
                        i++;
                    }
                }
            }
            return map;
        }
    }

    /// <summary>
    /// 界面脚本(.ui,证据 E-094)里的一个控件:<c>[ui controls] "[类型]" "名字" x y 可见 "图名" 参数…</c>,后面可以跟 [common action] 等子标签,以 [/ui controls] 结束。
    /// 只读公共前缀:类型、名字、位置、是否可见、第一个路径字符串与它后面的值;每种控件的参数含义不同(世界地图用到的 [balloon]
    /// 在图名后面是"个数 1 气泡样式 副本编号")。
    /// </summary>
    public sealed class UiControl
    {
        /// <summary>控件类型,带方括号、小写:[image]、[balloon]、[text button]、[text] 等。</summary>
        public string Type { get; }

        public string Id { get; }
        public int X { get; }
        public int Y { get; }
        public bool Visible { get; }

        /// <summary>图名(脚本里的写法,例如 WorldMap/selectdungeon02.img);没有则为 null。</summary>
        public string Image { get; }

        /// <summary>图名之后(没有图名时是可见标志之后)的全部值,保持原样。</summary>
        public IReadOnlyList<ScriptValue> Rest { get; }

        public UiControl(string type, string id, int x, int y, bool visible, string image, IReadOnlyList<ScriptValue> rest)
        {
            Type = type;
            Id = id;
            X = x;
            Y = y;
            Visible = visible;
            Image = image;
            Rest = rest ?? new List<ScriptValue>();
        }

        /// <summary>图名之后的整数,按出现顺序(忽略字符串、键引用)。</summary>
        public List<int> Params()
        {
            var list = new List<int>();
            foreach (ScriptValue v in Rest)
            {
                if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
            }
            return list;
        }
    }

    /// <summary>界面脚本(.ui):按出现顺序排列的控件。</summary>
    public sealed class UiScript
    {
        public IReadOnlyList<UiControl> Controls { get; }

        public UiScript(IReadOnlyList<UiControl> controls)
        {
            Controls = controls ?? new List<UiControl>();
        }

        public UiControl Find(string id)
        {
            foreach (UiControl c in Controls)
            {
                if (string.Equals(c.Id, id, StringComparison.Ordinal))
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>解析界面脚本;没有任何 [ui controls] 时返回 null。</summary>
        public static UiScript Parse(ScriptLine[] doc)
        {
            if (doc == null)
            {
                return null;
            }
            var controls = new List<UiControl>();
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[ui controls]"))
                {
                    continue;
                }
                ScriptValue[] v = line.Values;
                if (v.Length < 2 || v[0].Kind != ScriptValueKind.String || v[1].Kind != ScriptValueKind.String)
                {
                    continue;
                }
                int i = 2;
                int x = 0, y = 0;
                bool visible = true;
                if (i + 2 < v.Length && v[i].IsNumber && v[i + 1].IsNumber && v[i + 2].IsNumber)
                {
                    x = v[i].Int;
                    y = v[i + 1].Int;
                    visible = v[i + 2].Int != 0;
                    i += 3;
                }
                string image = null;
                if (i < v.Length && v[i].Kind == ScriptValueKind.String)
                {
                    image = v[i].Text;
                    i++;
                }
                var rest = new List<ScriptValue>();
                for (; i < v.Length; i++)
                {
                    rest.Add(v[i]);
                }
                controls.Add(new UiControl((v[0].Text ?? string.Empty).ToLowerInvariant(), v[1].Text, x, y, visible, image, rest));
            }
            return controls.Count == 0 ? null : new UiScript(controls);
        }

        public static UiScript Read(DnfAssets assets, string path)
        {
            return Parse(assets.ReadScript(path));
        }
    }

    /// <summary>
    /// 副本定义(.dgn)里和选择界面卡片有关的标签(证据 E-094):[worldmap pattern info] 组号 位置号 "图名" 帧号(卡片的画:
    /// 图里每一帧是一个副本的 168×73 插图,副本名已经画在图里)、[minimum required level]、[basis level]、[name](字符串键)。
    /// </summary>
    public sealed class DungeonCardDef
    {
        public string Path { get; }

        /// <summary>卡片图的图包名(例如 sprite/worldmap/selectdungeonslot/elvengard.img);没有 [worldmap pattern info] 时为 null。</summary>
        public string SlotImage { get; }

        public int SlotFrame { get; }

        public int PatternGroup { get; }
        public int PatternSlot { get; }
        public int MinimumLevel { get; }
        public int BasisLevel { get; }

        public DungeonCardDef(string path, string slotImage, int slotFrame, int patternGroup, int patternSlot, int minimumLevel, int basisLevel)
        {
            Path = path;
            SlotImage = slotImage;
            SlotFrame = slotFrame;
            PatternGroup = patternGroup;
            PatternSlot = patternSlot;
            MinimumLevel = minimumLevel;
            BasisLevel = basisLevel;
        }

        public static DungeonCardDef Parse(string path, ScriptLine[] doc)
        {
            if (doc == null)
            {
                return null;
            }
            string image = null;
            int frame = 0, group = 0, slot = 0, min = 0, basis = 0;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[worldmap pattern info]"))
                {
                    List<int> n = line.Ints();
                    group = n.Count > 0 ? n[0] : 0;
                    slot = n.Count > 1 ? n[1] : 0;
                    frame = n.Count > 2 ? n[2] : 0;
                    image = WorldMapDef.ToImageName(line.FirstString());
                }
                else if (line.TagIs("[minimum required level]"))
                {
                    List<int> n = line.Ints();
                    min = n.Count > 0 ? n[0] : 0;
                }
                else if (line.TagIs("[basis level]"))
                {
                    List<int> n = line.Ints();
                    basis = n.Count > 0 ? n[0] : 0;
                }
            }
            return new DungeonCardDef(path, image, frame, group, slot, min, basis);
        }

        public static DungeonCardDef Read(DnfAssets assets, string path)
        {
            return Parse(path, assets.ReadScript(path));
        }
    }
}
