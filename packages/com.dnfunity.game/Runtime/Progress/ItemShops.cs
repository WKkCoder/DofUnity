using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-119(服务端 importItemShopScript 0x089dc5b4 读 .shp 的标签;loadItemShopFiles 0x08512aa0 按 itemshop.lst 的编号建"编号 → 脚本"表;
// checkValidShopItem 0x08512cda 在 [sell item] 里找商品;NPC 的 [role] "[item shop]" N 里的 N 就是 itemshop.lst 的编号)。
namespace Dnf.Game
{
    /// <summary>
    /// 商店的一页商品:<c>[sell item]</c> 里两个 −2 之间(或开头到第一个 −2、最后一个 −2 到结尾)的整数。
    /// 每个数是一个格子:大于等于 0 是物品编号,−1 是空格;格子按 <see cref="ItemShopDef.Columns"/> 列一行排(行的列数是从 −1 的补位方式推出来的,证据 E-119)。
    /// </summary>
    public sealed class ItemShopTab
    {
        public ItemShopTab(IReadOnlyList<int> cells, string nameKey)
        {
            Cells = cells ?? throw new ArgumentNullException(nameof(cells));
            NameKey = nameKey;
        }

        /// <summary>格子:物品编号,−1 是空格。</summary>
        public IReadOnlyList<int> Cells { get; }

        /// <summary><c>[tab name]</c> 里这一页的字符串键(没写为 null;原文在 <c>.kor.str</c> 里,没有文字系统之前不显示)。</summary>
        public string NameKey { get; }

        /// <summary>第 <paramref name="index"/> 格的物品编号;越界或空格返回 −1。</summary>
        public int CodeAt(int index)
        {
            return (uint)index < (uint)Cells.Count && Cells[index] >= 0 ? Cells[index] : -1;
        }

        /// <summary>这一页有几件商品(不数空格)。</summary>
        public int ItemCount
        {
            get
            {
                int n = 0;
                foreach (int c in Cells)
                {
                    if (c >= 0)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>这一页要几行(按 <see cref="ItemShopDef.Columns"/> 列排,最后一行可以不满)。</summary>
        public int RowCount => (Cells.Count + ItemShopDef.Columns - 1) / ItemShopDef.Columns;
    }

    /// <summary>
    /// 一家 NPC 商店(<c>itemshop/*.shp</c>):<see cref="Index"/> 是它在 <c>itemshop.lst</c> 里的编号(NPC 的 <c>[item shop]</c> 角色写的就是它),
    /// <see cref="NpcId"/> 是脚本里的 <c>[NPC]</c>(店主的 NPC 编号,检查与测试用,运行时不靠它找店)。
    /// </summary>
    public sealed class ItemShopDef
    {
        /// <summary>格子的列数(推断:商店脚本用 −1 补位成整行,整行是 7 个;窗口用同样的列数,证据 E-119)。</summary>
        public const int Columns = 7;

        public ItemShopDef(int index, string path, int npcId, string typeTag, string messageKey, IReadOnlyList<ItemShopTab> tabs)
        {
            Index = index;
            Path = path;
            NpcId = npcId;
            TypeTag = typeTag;
            MessageKey = messageKey;
            Tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        }

        /// <summary><c>itemshop.lst</c> 里的编号。</summary>
        public int Index { get; }

        /// <summary>规范化后的脚本路径。</summary>
        public string Path { get; }

        /// <summary><c>[NPC]</c>:店主的编号;没写为 −1。</summary>
        public int NpcId { get; }

        /// <summary><c>[type]</c>("[etc shop]"、"[weapon shop]");没写为 null。</summary>
        public string TypeTag { get; }

        /// <summary><c>[message]</c> 的字符串键(店主的招呼语);没写为 null。</summary>
        public string MessageKey { get; }

        public IReadOnlyList<ItemShopTab> Tabs { get; }

        /// <summary>所有页加起来有几件商品(不数空格)。</summary>
        public int ItemCount
        {
            get
            {
                int n = 0;
                foreach (ItemShopTab tab in Tabs)
                {
                    n += tab.ItemCount;
                }
                return n;
            }
        }

        /// <summary>这家店卖不卖 <paramref name="code"/>(服务端 <c>checkValidShopItem</c> 的判断:在 <c>[sell item]</c> 里就卖)。</summary>
        public bool Sells(int code)
        {
            if (code < 0)
            {
                return false;
            }
            foreach (ItemShopTab tab in Tabs)
            {
                foreach (int c in tab.Cells)
                {
                    if (c == code)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>第一件商品的编号(没有商品返回 −1;探针与检查用)。</summary>
        public int FirstCode()
        {
            foreach (ItemShopTab tab in Tabs)
            {
                foreach (int c in tab.Cells)
                {
                    if (c >= 0)
                    {
                        return c;
                    }
                }
            }
            return -1;
        }
    }

    /// <summary>从商店脚本(<c>.shp</c>)读 <see cref="ItemShopDef"/>。</summary>
    public static class ItemShopScript
    {
        /// <summary>
        /// 把按标签拆好的脚本读成商店。<c>[sell item]</c> 的整数里 −2 是分页符、−1 是空格;最后一个 −2 之后还有格子就是最后一页
        /// (只有一页的店根本没有 −2)。<c>[tab name]</c> 的字符串键按顺序对应各页。没有 <c>[sell item]</c> 的脚本得到一家没有商品的店;
        /// <paramref name="lines"/> 为 null 返回 null。
        /// </summary>
        public static ItemShopDef Parse(ScriptLine[] lines, int index, string path)
        {
            if (lines == null)
            {
                return null;
            }
            int npc = -1;
            string type = null;
            string message = null;
            var tabNames = new List<string>();
            var tabs = new List<List<int>>();
            foreach (ScriptLine line in lines)
            {
                if (line.TagIs("[NPC]"))
                {
                    List<int> ints = line.Ints();
                    npc = ints.Count > 0 ? ints[0] : -1;
                }
                else if (line.TagIs("[type]"))
                {
                    type = line.FirstString();
                }
                else if (line.TagIs("[message]"))
                {
                    message = FirstKey(line);
                }
                else if (line.TagIs("[tab name]"))
                {
                    tabNames.Clear();
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.Keyed)
                        {
                            tabNames.Add(v.Text);
                        }
                    }
                }
                else if (line.TagIs("[sell item]"))
                {
                    tabs.Clear();
                    var current = new List<int>();
                    foreach (int n in line.Ints())
                    {
                        if (n == -2)
                        {
                            tabs.Add(current);
                            current = new List<int>();
                        }
                        else
                        {
                            current.Add(n < 0 ? -1 : n);
                        }
                    }
                    if (current.Count > 0)
                    {
                        tabs.Add(current);
                    }
                }
            }
            var result = new List<ItemShopTab>(tabs.Count);
            for (int i = 0; i < tabs.Count; i++)
            {
                result.Add(new ItemShopTab(tabs[i], i < tabNames.Count ? tabNames[i] : null));
            }
            return new ItemShopDef(index, path, npc, type, message, result);
        }

        private static string FirstKey(ScriptLine line)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Keyed)
                {
                    return v.Text;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 商店目录:<c>itemshop/itemshop.lst</c> 里的"编号 → 脚本路径",以及按编号读出的 <see cref="ItemShopDef"/>(缓存)。
    /// 编号就是 NPC <c>[item shop]</c> 角色的参数;列表或脚本读不到时返回 null / 空表,不抛异常。
    /// </summary>
    public sealed class ItemShopCatalog
    {
        public const string ListPath = "itemshop/itemshop.lst";

        private readonly GameData _data;
        private readonly Dictionary<int, ItemShopDef> _cache = new Dictionary<int, ItemShopDef>();
        private Dictionary<int, string> _paths;

        public ItemShopCatalog(GameData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        private Dictionary<int, string> Paths
        {
            get
            {
                if (_paths == null)
                {
                    _paths = new Dictionary<int, string>();
                    Dictionary<int, string> raw = _data.ReadList(ListPath);
                    if (raw != null)
                    {
                        foreach (KeyValuePair<int, string> pair in raw)
                        {
                            _paths[pair.Key] = PvfPath.ResolveRelativeTo(ListPath, pair.Value);
                        }
                    }
                }
                return _paths;
            }
        }

        /// <summary>列表里列了多少家店。</summary>
        public int ListedCount => Paths.Count;

        /// <summary>列表里的商店编号(升序)。</summary>
        public IReadOnlyList<int> Indices()
        {
            var list = new List<int>(Paths.Keys);
            list.Sort();
            return list;
        }

        /// <summary>按编号取一家店(缓存);编号不在列表里、脚本读不到返回 null。</summary>
        public ItemShopDef Get(int index)
        {
            if (_cache.TryGetValue(index, out ItemShopDef cached))
            {
                return cached;
            }
            ItemShopDef def = null;
            if (Paths.TryGetValue(index, out string path))
            {
                def = ItemShopScript.Parse(_data.ReadScript(path), index, path);
            }
            _cache[index] = def;
            return def;
        }
    }
}
