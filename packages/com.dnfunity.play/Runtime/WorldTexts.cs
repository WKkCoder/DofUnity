using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

// 证据:E-121(文字引用 = 种类编号 + 键;NPC 的名字在 npc.kor.str,怪物的在 monster.kor.str);E-122(NPC 脚本里 [field name] 是城镇里画在头顶的名字,
// [name] 是对话与列表里用的名字,二者在全库里多数一样,不一样的多是 [field name] 带职业头衔)。
namespace Dnf.Play
{
    /// <summary>
    /// 世界里的名字文字(M30):NPC 头顶的名字、怪物目标血条窗口里的怪物名。包一份 <see cref="StrLibrary"/>,按脚本里的"种类 + 键"取文字。
    /// 文字是用户自备数据里的原版内容,只在运行时读,不入库;取不到(没装这份文本、键不在表里)返回 null,调用方就不画名字。
    /// 宿主装好字体与文字表之后把它放进 <see cref="Shared"/>(同 <see cref="OverlayArt.SharedFonts"/> 的用法),没装的运行里整套名字都不出现,
    /// 所以离屏出图与对拍的画面不受影响。
    /// </summary>
    public sealed class WorldTexts
    {
        /// <summary>全进程共用的一份:宿主在真的在玩(有字体)时设一次;为 null 时所有名字都不画。</summary>
        public static WorldTexts Shared { get; set; }

        /// <summary>界面文字表(ui/ui.kor.str)里写"金额 + 金币"的那一条的键(格式串里的 <c>%d</c> 换成金额)。地上金币名牌用它是推断:基线上地上金币名牌的字与它同一个格式。</summary>
        public const string GoldAmountKey = "inventory_main_bottom06";

        private readonly StrLibrary _library;
        private ItemTexts _items;

        public WorldTexts(StrLibrary library)
        {
            _library = library ?? throw new ArgumentNullException(nameof(library));
        }

        /// <summary>底下的文字表集合。</summary>
        public StrLibrary Library => _library;

        /// <summary>一个文字引用(种类编号 + 键)换成要显示的文字;空值与查不到都返回 null。<c>%%</c> 还原成 <c>%</c>。</summary>
        public string Text(int kind, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            return ItemTexts.Display(_library.Get(kind, key));
        }

        /// <summary>物品的名字(地上物品的名牌用);没写键或取不到返回 null。</summary>
        public string ItemName(ItemInfo item)
        {
            if (item == null)
            {
                return null;
            }
            if (_items == null)
            {
                _items = new ItemTexts(_library);
            }
            return _items.NameOf(item);
        }

        /// <summary>地上金币堆的名牌字:金额按 <see cref="GoldAmountKey"/> 的格式串排;取不到格式串返回 null。</summary>
        public string GoldAmount(int amount)
        {
            string format = ItemTexts.Display(_library.Ui(GoldAmountKey));
            return format == null ? null : format.Replace("%d", amount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>怪物的名字:<c>[name]</c> 的键在 monster.kor.str 里;没写键或取不到返回 null。</summary>
        public string MonsterName(MonsterDef def)
        {
            return MonsterName(def?.NameKey);
        }

        /// <summary>同上,按 <c>[name]</c> 的键取(目标血条窗口的资料只带键)。</summary>
        public string MonsterName(string nameKey)
        {
            return Text(StrLibrary.KindMonster, nameKey);
        }

        /// <summary>
        /// NPC 头顶的名字:脚本里 <c>[field name]</c>(城镇里画的那个,常带职业头衔)优先,没有或取不到就用 <c>[name]</c>;都没有返回 null。
        /// 标签的值是"种类编号 + 键"(NPC 是种类 8);写成普通字符串的值不当文字引用。
        /// </summary>
        public string NpcPlateName(IReadOnlyList<ScriptLine> script)
        {
            if (script == null)
            {
                return null;
            }
            return FirstText(script, "[field name]") ?? FirstText(script, "[name]");
        }

        /// <summary>对话里用的名字:<c>[name]</c> 优先,没有或取不到再用 <c>[field name]</c>(任务对白里的 NPC 引用用它,M31);都没有返回 null。</summary>
        public string NpcDialogName(IReadOnlyList<ScriptLine> script)
        {
            if (script == null)
            {
                return null;
            }
            return FirstText(script, "[name]") ?? FirstText(script, "[field name]");
        }

        /// <summary>脚本里 <c>[name]</c> 的文字(AI 角色一类只有 [name] 的脚本用,M31);没有或取不到返回 null。</summary>
        public string ScriptName(IReadOnlyList<ScriptLine> script)
        {
            return script == null ? null : FirstText(script, "[name]");
        }

        private string FirstText(IReadOnlyList<ScriptLine> script, string tag)
        {
            for (int i = 0; i < script.Count; i++)
            {
                ScriptLine line = script[i];
                if (line == null || !line.TagIs(tag))
                {
                    continue;
                }
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind != ScriptValueKind.Keyed)
                    {
                        continue;
                    }
                    string text = Text(v.Int, v.Text);
                    if (text != null)
                    {
                        return text;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// NPC 编号 → 头顶的名字(M30):按编号找 NPC 脚本(npc.lst → .npc),取 <see cref="WorldTexts.NpcPlateName"/>;结果按编号缓存(包括"没有名字")。
    /// </summary>
    public sealed class NpcNames
    {
        private readonly Func<int, ScriptLine[]> _scriptOf;
        private readonly WorldTexts _texts;
        private readonly Dictionary<int, string> _cache = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _dialogCache = new Dictionary<int, string>();

        /// <param name="scriptOf">按 NPC 编号给出它的脚本行;没有这个 NPC 或脚本读不到返回 null。</param>
        public NpcNames(Func<int, ScriptLine[]> scriptOf, WorldTexts texts)
        {
            _scriptOf = scriptOf ?? throw new ArgumentNullException(nameof(scriptOf));
            _texts = texts ?? throw new ArgumentNullException(nameof(texts));
        }

        /// <summary>从数据目录造:npc/npc.lst 第一次用到时读,每个 NPC 的脚本按需读。</summary>
        public static NpcNames Create(DnfAssets assets, WorldTexts texts)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            Dictionary<int, string> catalog = null;
            return new NpcNames(id =>
            {
                if (catalog == null)
                {
                    catalog = TownScene.ReadNpcCatalog(assets);
                }
                return catalog.TryGetValue(id, out string file) ? assets.ReadScript(PvfPath.ResolveRelativeTo("npc/", file)) : null;
            }, texts);
        }

        /// <summary>这个 NPC 在对话里用的名字(<see cref="WorldTexts.NpcDialogName"/>,按编号缓存);没有名字返回 null。</summary>
        public string DialogNameOf(int npcId)
        {
            if (_dialogCache.TryGetValue(npcId, out string cached))
            {
                return cached;
            }
            string name = _texts.NpcDialogName(_scriptOf(npcId));
            _dialogCache[npcId] = name;
            return name;
        }

        /// <summary>这个 NPC 头顶要画的名字;没有名字返回 null。</summary>
        public string PlateOf(int npcId)
        {
            if (_cache.TryGetValue(npcId, out string cached))
            {
                return cached;
            }
            string name = _texts.NpcPlateName(_scriptOf(npcId));
            _cache[npcId] = name;
            return name;
        }
    }
}
