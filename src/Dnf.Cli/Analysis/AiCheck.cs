using System.Text;
using System.Text.Json;
using Dnf.Data;
using Dnf.Game.Ai;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf ai-check [--dump &lt;文件&gt;] [--json &lt;文件&gt;]`:用 Dnf.Game 的 .ai 解析器读全库 .ai(证据 E-052)。
///   统计:能否读完、错误、解析后剩下的标记、结点与深度、关键字/运算符/内部码的出现次数、名字参数查不到名表的个数,
///   以及"某个 [void] 函数的真/假两路各是什么(数值返回/默认/嵌套/导入)"——用来判断带歧义的函数的真假方向。
///   名表来自数据包里的 AICharacter/OperatingValue.lst(运行时读,不入库)。
///   只输出数字、标签名和函数名,不输出脚本里的值;输出文件放 out/,不入库。
/// </summary>
internal static class AiCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));
        AiConstants? constants = null;
        if (pvf.TryGetEntry(AiConstants.ListPath, out PvfEntry listEntry))
        {
            constants = AiConstants.Parse(pvf.Read(listEntry), strings);
            Console.WriteLine("名表 " + AiConstants.ListPath + ":" + constants.Count + " 项");
        }
        else
        {
            Console.WriteLine("数据包里没有 " + AiConstants.ListPath + ",名字参数不查表");
        }

        int files = 0, notTokens = 0, clean = 0, withTrailing = 0, maxDepth = 0;
        long nodes = 0, unresolvedNames = 0, namedParams = 0;
        var errorKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var errorSamples = new List<string>();
        var trailingKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rootKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var keywords = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var operators = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var depths = new SortedDictionary<int, int>();
        var branchShapes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        // 函数 | 角色 → (真路,假路) → 次数
        var polarity = new SortedDictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
        var dump = new List<(string Path, string Text)>();

        foreach (PvfEntry e in pvf.Entries)
        {
            if (!e.Path.EndsWith(".ai", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            byte[] raw = pvf.Read(e);
            if (!TokenScript.IsTokenScript(raw))
            {
                notTokens++;
                continue;
            }
            AiScript script = AiParser.Parse(raw, strings, constants);
            files++;
            nodes += script.NodeCount;
            if (script.MaxDepth > maxDepth)
            {
                maxDepth = script.MaxDepth;
            }
            depths.TryGetValue(script.MaxDepth, out int dc);
            depths[script.MaxDepth] = dc + 1;
            if (script.Clean)
            {
                clean++;
            }
            else
            {
                foreach (string err in script.Errors)
                {
                    string kind = err.Contains('(') ? err[..err.IndexOf('(')] : err;
                    errorKinds.TryGetValue(kind, out int ec);
                    errorKinds[kind] = ec + 1;
                }
                if (errorSamples.Count < 25)
                {
                    errorSamples.Add(e.Path.ToLowerInvariant() + "\t" + script.Errors[0]);
                }
            }
            if (script.Trailing.Count > 0)
            {
                withTrailing++;
                string first = script.Trailing[0];
                string label = script.Trailing.Count == 1 ? first : first + " +" + (script.TotalTokens - script.ConsumedTokens - 1);
                trailingKinds.TryGetValue(first, out int tc);
                trailingKinds[first] = tc + 1;
                _ = label;
            }
            string rk = script.Root.Kind.ToString();
            rootKinds.TryGetValue(rk, out int rc);
            rootKinds[rk] = rc + 1;

            string role = RoleOf(e.Path);
            Walk(script.Root, role);

            if (opts.DumpFile != null)
            {
                dump.Add((e.Path.ToLowerInvariant(),
                    "nodes=" + script.NodeCount + " depth=" + script.MaxDepth + " errors=" + script.Errors.Count
                    + " trailing=" + (script.TotalTokens - script.ConsumedTokens) + " root=" + rk
                    + " next=" + (script.Trailing.Count > 0 ? script.Trailing[0] : "-")));
            }
        }

        void Walk(AiResult result, string role)
        {
            if (result.Kind != AiResultKind.Think)
            {
                return;
            }
            AiNode node = result.Node!;
            keywords.TryGetValue(node.Keyword, out long k);
            keywords[node.Keyword] = k + 1;
            if (node.Operator != null)
            {
                operators.TryGetValue(node.Operator, out long o);
                operators[node.Operator] = o + 1;
            }
            foreach (AiParam p in node.Params)
            {
                if (p.IsName)
                {
                    namedParams++;
                    if (float.IsNaN(p.Value) && constants != null)
                    {
                        unresolvedNames++;
                    }
                }
            }
            string shape = Shape(node.True) + "/" + Shape(node.False);
            branchShapes.TryGetValue(shape, out long b);
            branchShapes[shape] = b + 1;
            if (node.Keyword == "[void]" && node.Operator != null)
            {
                string key = node.Operator + " | " + role;
                if (!polarity.TryGetValue(key, out SortedDictionary<string, long>? map))
                {
                    map = new SortedDictionary<string, long>(StringComparer.Ordinal);
                    polarity[key] = map;
                }
                string pair = "真=" + Shape(node.True) + " 假=" + Shape(node.False);
                map.TryGetValue(pair, out long c);
                map[pair] = c + 1;
            }
            Walk(node.True, role);
            Walk(node.False, role);
        }

        Console.WriteLine("读取 " + files + " 个 .ai(非标记流 " + notTokens + ");读法完全规整(没有错误)的 " + clean + " 个");
        Console.WriteLine("  结点 " + nodes + ",最大嵌套深度 " + maxDepth + ";解析结束后还有剩余标记的文件 " + withTrailing + " 个");
        Console.WriteLine("  名字参数 " + namedParams + " 个,查不到名表的 " + unresolvedNames + " 个");
        PrintMap("错误种类", errorKinds);
        foreach (string s in errorSamples)
        {
            Console.WriteLine("    " + s);
        }
        PrintMap("剩余标记的第一个", trailingKinds);
        PrintMap("整个文件的结果种类", rootKinds);
        PrintMap("嵌套深度(文件数)", depths.ToDictionary(p => p.Key.ToString(), p => p.Value));
        PrintTop("条件关键字", keywords, 70);
        PrintTop("运算符/函数名", operators, 30);
        PrintTop("两路结果的形状(真/假)", branchShapes, 20);
        Console.WriteLine("[void] 函数的真假方向(按文件角色分):");
        foreach ((string key, SortedDictionary<string, long> map) in polarity)
        {
            long total = map.Values.Sum();
            if (total < 20)
            {
                continue;
            }
            Console.WriteLine("  " + key + "  共 " + total);
            foreach (KeyValuePair<string, long> kv in map.OrderByDescending(p => p.Value).Take(5))
            {
                Console.WriteLine("      " + kv.Key + " : " + kv.Value);
            }
        }

        if (opts.DumpFile != null)
        {
            dump.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.DumpFile))!);
            using var w = new StreamWriter(opts.DumpFile, false, new UTF8Encoding(false)) { NewLine = "\n" };
            foreach ((string path, string text) in dump)
            {
                w.WriteLine(path + "\t" + text);
            }
            Console.WriteLine("已写出 " + dump.Count + " 行 -> " + opts.DumpFile);
        }
        if (opts.JsonFile != null)
        {
            var summary = new
            {
                files, notTokens, clean, nodes, maxDepth, withTrailing, namedParams, unresolvedNames,
                errorKinds, trailingKinds, rootKinds, keywords, operators, branchShapes, polarity,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.JsonFile))!);
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    /// <summary>一路结果的粗分类:D 没写、N 负数、P 非负数、S 名字、T 嵌套、I 导入。</summary>
    private static string Shape(AiResult r)
    {
        switch (r.Kind)
        {
            case AiResultKind.Default:
                return "D";
            case AiResultKind.Number:
                return r.Number < 0 ? "N" : "P";
            case AiResultKind.Name:
                return "S";
            case AiResultKind.Think:
                return "T";
            default:
                return "I";
        }
    }

    private static string RoleOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (string role in new[] { "event", "destinationselect", "action", "movemethod" })
        {
            if (name.Contains(role))
            {
                return role;
            }
        }
        return "other";
    }

    private static void PrintMap<TKey>(string title, IDictionary<TKey, int> map) where TKey : notnull
    {
        Console.WriteLine("  " + title + ":" + (map.Count == 0 ? " 无" : string.Join(",  ", map.Select(p => p.Key + " " + p.Value))));
    }

    private static void PrintTop(string title, SortedDictionary<string, long> map, int n)
    {
        Console.WriteLine("  " + title + "(前 " + n + "):");
        foreach (KeyValuePair<string, long> kv in map.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(n))
        {
            Console.WriteLine("      " + kv.Key + " : " + kv.Value);
        }
    }
}
