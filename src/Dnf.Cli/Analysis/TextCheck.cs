using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// text-check:汉字文字系统的整库检查(证据 E-121)。读 PVF 里每个种类的 *.kor.str 文字表(GB18030、"键>文字" 一行一条),
/// 1) 逐表用另一条路径(.NET 自带的 GB18030 编码加一个独立写的行解析)再解一遍,与 <see cref="StrBook"/> 的结果逐条比对;
/// 2) 值里不应有解码失败的替代字符;
/// 3) 物品目录(装备与可叠加)里每件物品的名字、说明键都能在文字表里查到;商店页名同理;
/// 4) 打印每张表解出的文字的 SHA-256(只打印在终端,留给 tools 里的 Python 参考实现按同样的规则比对)。
/// 文字是用户自己的数据:条数与哈希只打印在本机的终端上(不要抄进入库的文件),通过与否只看结构与比对结果。
/// </summary>
internal static class TextCheck
{
    private static readonly (int Kind, string Name)[] Kinds =
    {
        (StrLibrary.KindCharacter, "character"), (StrLibrary.KindDungeon, "dungeon"), (StrLibrary.KindEquipment, "equipment"), (StrLibrary.KindEtc, "etc"),
        (StrLibrary.KindItemShop, "itemshop"), (StrLibrary.KindMonster, "monster"), (StrLibrary.KindNpc, "npc"), (StrLibrary.KindQuest, "quest"),
        (StrLibrary.KindSkill, "skill"), (StrLibrary.KindStackable, "stackable"), (StrLibrary.KindTown, "town"), (StrLibrary.KindWorldMap, "worldmap"),
        (StrLibrary.KindCreature, "creature"), (StrLibrary.KindAiCharacter, "aicharacter"), (StrLibrary.KindRandomOption, "randomoption"),
    };

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        PvfArchive pvf = assets.Pvf;
        byte[]? Read(string path) => pvf.TryGetEntry(path, out PvfEntry e) ? pvf.Read(e) : null;
        var library = new StrLibrary(Read!);
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gb = Encoding.GetEncoding(54936);

        // 1. 每个文字表:读得到、解得出、与独立解码逐条一致
        var paths = new List<(string Label, string Path)> { ("ui", StrLibrary.UiPath) };
        foreach ((int kind, string name) in Kinds)
        {
            string? path = StrLibrary.PathOfKind(kind);
            Report(path != null, "种类 " + kind + "(" + name + ")有对应的文字表路径");
            if (path != null && !paths.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add((name, path));
            }
        }
        var watch = Stopwatch.StartNew();
        int present = 0, totalKeys = 0, replacement = 0, mismatched = 0, missingKeys = 0;
        long bytes = 0;
        var lines = new List<string>();
        foreach ((string label, string path) in paths)
        {
            byte[]? raw = Read(path);
            if (raw == null)
            {
                Console.WriteLine("  " + label + ":文件不在 PVF 里");
                continue;
            }
            present++;
            bytes += raw.Length;
            StrBook book = library.Book(path);
            var reference = ReferenceParse(gb.GetString(raw));
            int fffd = 0, wrong = 0, absent = 0;
            foreach (string key in book.Keys)
            {
                string text = book.Get(key);
                if (text.Contains('\uFFFD'))
                {
                    fffd++;
                }
                if (!reference.TryGetValue(key, out string? expected))
                {
                    absent++;
                }
                else if (!string.Equals(text, expected, StringComparison.Ordinal))
                {
                    wrong++;
                }
            }
            foreach (string key in reference.Keys)
            {
                if (!book.Contains(key))
                {
                    absent++;
                }
            }
            totalKeys += book.Count;
            replacement += fffd;
            mismatched += wrong;
            missingKeys += absent;
            lines.Add(label + " keys=" + book.Count + " bytes=" + raw.Length + " fffd=" + fffd + " mismatch=" + wrong + " absent=" + absent + " sha256=" + Hash(book));
        }
        watch.Stop();
        foreach (string line in lines)
        {
            Console.WriteLine("  " + line);
        }
        Console.WriteLine("  共 " + present + " 张表," + totalKeys + " 条," + bytes + " 字节,解码并比对用时 " + watch.ElapsedMilliseconds + " 毫秒");
        Report(present == paths.Count, "每张文字表都在 PVF 里(缺的 " + (paths.Count - present) + " 张)");
        Report(replacement == 0, "值里没有解码失败的替代字符(有的 " + replacement + " 条)");
        Report(mismatched == 0 && missingKeys == 0, "与独立解码(.NET 的 GB18030 加另写的行解析)逐条一致(文字不同 " + mismatched + " 条,键对不上 " + missingKeys + " 个)");

        // 2. 物品:名字、说明、背景故事都查得到
        // "查不到"分三种:键在这件物品所属的表里、但值是空的(原版数据里这件物品就没有这段文字,正常,画面上就不画);
        // 键只在别的表里有(脚本写的文字种类与键所在的表对不上);哪张表里都没有(真缺,算失败)
        var data = new GameData(new PvfScriptSource(pvf));
        var catalog = new ItemCatalog(data);
        var texts = new ItemTexts(library);
        var chars = new HashSet<int>();
        var misses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int items = 0, noKey = 0, withName = 0, withExplain = 0, withFlavor = 0;

        string Classify(ItemInfo item, params string?[] keys)
        {
            int kind = item.TextKind >= 0 ? item.TextKind : (item.IsEquipment ? StrLibrary.KindEquipment : StrLibrary.KindStackable);
            StrBook? own = library.BookOfKind(kind);
            foreach (string? key in keys)
            {
                if (key != null && own != null && own.Contains(key))
                {
                    return "empty";
                }
            }
            foreach (string? key in keys)
            {
                if (key == null)
                {
                    continue;
                }
                foreach ((string _, string path) in paths)
                {
                    if (library.Book(path).Contains(key))
                    {
                        return "elsewhere";
                    }
                }
            }
            return "nowhere";
        }

        void Count(string what, string kind) => misses[what + " " + kind] = misses.TryGetValue(what + " " + kind, out int n) ? n + 1 : 1;

        foreach (int code in catalog.EquipmentCodes().Concat(catalog.StackableCodes()))
        {
            ItemInfo? item = catalog.Get(code);
            if (item == null)
            {
                continue;
            }
            items++;
            if (item.NameKey == null)
            {
                noKey++;
                continue;
            }
            withName++;
            string? name = texts.NameOf(item);
            if (name == null)
            {
                Count("名字", Classify(item, item.NameKey));
            }
            else
            {
                AddChars(chars, name);
            }
            if (item.ExplainKey != null || item.BasicExplainKey != null)
            {
                withExplain++;
                string? explain = texts.ExplainOf(item);
                if (explain == null)
                {
                    string kind = Classify(item, item.ExplainKey, item.BasicExplainKey);
                    Count("说明", kind);
                    if (kind == "nowhere")
                    {
                        Console.WriteLine("    哪里都没有的说明键:物品 " + item.Code + " 文字种类 " + item.TextKind + " 说明键 " + item.ExplainKey + " 基础说明键 " + item.BasicExplainKey);
                    }
                }
                else
                {
                    AddChars(chars, explain);
                }
            }
            if (item.FlavorKey != null)
            {
                withFlavor++;
                string? flavor = texts.FlavorOf(item);
                if (flavor == null)
                {
                    Count("背景故事", Classify(item, item.FlavorKey));
                }
                else
                {
                    AddChars(chars, flavor);
                }
            }
        }
        Console.WriteLine("  物品 " + items + " 件:没写名字键的 " + noKey + "、写了名字键的 " + withName + "、写了说明键的 " + withExplain + "、写了背景故事键的 " + withFlavor
            + ";查到的文字里用到 " + chars.Count + " 种不同的字符");
        foreach (KeyValuePair<string, int> m in misses)
        {
            Console.WriteLine("    " + m.Key + " × " + m.Value);
        }
        int Total(string what, string kind) => misses.TryGetValue(what + " " + kind, out int n) ? n : 0;
        Report(items > 0, "物品目录读得出物品");
        // 原版数据里偶尔有悬空的键(写了键、哪张表里都没有,客户端也只能不显示);容忍千分之一以内,超过说明读表或解析出了问题
        Report(Total("名字", "nowhere") * 1000 <= withName, "写了名字键的物品,键多数在表里有(哪里都没有的 " + Total("名字", "nowhere") + " 件,千分之一以内;值为空的 " + Total("名字", "empty") + " 件、键在别的表里的 " + Total("名字", "elsewhere") + " 件不算失败)");
        Report(Total("说明", "nowhere") * 1000 <= withExplain, "写了说明键的物品,键多数在表里有(哪里都没有的 " + Total("说明", "nowhere") + " 件,千分之一以内;值为空的 " + Total("说明", "empty") + " 件、键在别的表里的 " + Total("说明", "elsewhere") + " 件不算失败)");
        Report(Total("背景故事", "nowhere") * 1000 <= withFlavor, "写了背景故事键的物品,键多数在表里有(哪里都没有的 " + Total("背景故事", "nowhere") + " 件,千分之一以内)");
        Report(withName - Total("名字", "empty") - Total("名字", "elsewhere") > withName / 2, "多数写了名字键的物品查得到非空的名字");

        // 3. 商店页名
        var shops = new ItemShopCatalog(data);
        int tabs = 0, tabKeys = 0, tabMiss = 0;
        foreach (int index in shops.Indices())
        {
            ItemShopDef? def = shops.Get(index);
            if (def == null)
            {
                continue;
            }
            foreach (ItemShopTab tab in def.Tabs)
            {
                tabs++;
                if (!string.IsNullOrEmpty(tab.NameKey))
                {
                    tabKeys++;
                    if (texts.ShopTabName(tab.NameKey) == null)
                    {
                        tabMiss++;
                    }
                }
            }
        }
        Console.WriteLine("  商店页 " + tabs + " 页:写了页名键的 " + tabKeys + "(查不到 " + tabMiss + ")");
        Report(tabMiss == 0, "写了页名键的商店页,页名都查得到(查不到的 " + tabMiss + " 页)");

        // 4. 占位英文的中文版:每条都有、没有空的
        Report(UiText.ExactCount > 0 && UiText.PatternCount > 0, "占位英文的中文对照表不是空的(整句 " + UiText.ExactCount + " 条、带数字的 " + UiText.PatternCount + " 条)");

        Console.WriteLine(bad == 0 ? "文字检查全部通过" : "文字检查有 " + bad + " 项未通过");
        return bad == 0 ? 0 : 1;
    }

    private static void AddChars(HashSet<int> set, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]))
            {
                i++;
            }
            set.Add(cp);
        }
    }

    /// <summary>独立于 <see cref="StrBook"/> 的行解析(只用来比对):去 BOM、按 \n 分行去 \r、跳过空行与 // 注释、第一个 '>' 前是键、后是文字、"\n" 两个字符换成换行,重复键取后一条。</summary>
    private static Dictionary<string, string> ReferenceParse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text.Substring(1);
        }
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            int gt = line.IndexOf('>');
            if (gt <= 0)
            {
                continue;
            }
            map[line.Substring(0, gt)] = line.Substring(gt + 1).Replace("\\n", "\n");
        }
        return map;
    }

    /// <summary>一张表的指纹:按键排序,逐条 "键 TAB 文字 LF",UTF-8,取 SHA-256 的十六进制。</summary>
    private static string Hash(StrBook book)
    {
        var sb = new StringBuilder();
        foreach (string key in book.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sb.Append(key).Append('\t').Append(book.Get(key)).Append('\n');
        }
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
