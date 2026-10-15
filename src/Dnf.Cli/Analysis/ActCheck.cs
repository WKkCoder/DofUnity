using System.Text;
using System.Text.Json;
using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf act-check [--dump &lt;文件&gt;] [--json &lt;文件&gt;]`:用 Dnf.Game 的 .act 语法树解析器读全库 .act(证据 E-050)。
///   统计文件数、节点数、块数、最大嵌套深度、笔误闭合与多余闭合;--json 额外写出"父标签 → 子标签 → 次数"的表,
///   与 `python3 tools/py/act_tree.py` 的输出逐项比较(两份独立实现);--dump 逐文件写一行摘要(按路径排序)。
///   只输出数字与标签名,不输出脚本里的值;输出文件放 out/,不入库。
/// </summary>
internal static class ActCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));
        var dump = new List<(string Path, string Text)>();
        // 父标签 → 子标签 → 次数;顶层用 "<root>"
        var children = new SortedDictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
        var blockCount = new SortedDictionary<string, long>(StringComparer.Ordinal);
        int files = 0, notTokens = 0, clean = 0, withStray = 0, withRepaired = 0, withLeading = 0, maxDepth = 0;
        long nodes = 0, blocks = 0, stray = 0, repaired = 0, leading = 0;

        foreach (PvfEntry e in pvf.Entries)
        {
            if (!e.Path.EndsWith(".act", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            byte[] raw = pvf.Read(e);
            if (!TokenScript.IsTokenScript(raw))
            {
                notTokens++;
                continue;
            }
            ActScript script = ActParser.Parse(raw, strings);
            files++;
            if (script.Clean)
            {
                clean++;
            }
            stray += script.StrayClosers;
            repaired += script.RepairedClosers;
            leading += script.LeadingValues;
            withStray += script.StrayClosers > 0 ? 1 : 0;
            withRepaired += script.RepairedClosers > 0 ? 1 : 0;
            withLeading += script.LeadingValues > 0 ? 1 : 0;
            int fileNodes = 0, fileBlocks = 0, fileDepth = 0;
            Walk(script.Roots, "<root>", 1);
            nodes += fileNodes;
            blocks += fileBlocks;
            if (fileDepth > maxDepth)
            {
                maxDepth = fileDepth;
            }
            if (opts.DumpFile != null)
            {
                dump.Add((e.Path.ToLowerInvariant(),
                    "nodes=" + fileNodes + " blocks=" + fileBlocks + " depth=" + fileDepth + " stray=" + script.StrayClosers
                    + " repaired=" + script.RepairedClosers + " leading=" + script.LeadingValues));
            }

            void Walk(IReadOnlyList<ActNode> list, string parent, int depth)
            {
                if (depth > fileDepth)
                {
                    fileDepth = depth;
                }
                if (list.Count == 0)
                {
                    return;
                }
                if (!children.TryGetValue(parent, out SortedDictionary<string, long>? map))
                {
                    map = new SortedDictionary<string, long>(StringComparer.Ordinal);
                    children[parent] = map;
                }
                foreach (ActNode node in list)
                {
                    fileNodes++;
                    map.TryGetValue(node.Tag, out long c);
                    map[node.Tag] = c + 1;
                    if (node.IsBlock)
                    {
                        fileBlocks++;
                        blockCount.TryGetValue(node.Tag, out long b);
                        blockCount[node.Tag] = b + 1;
                        Walk(node.Children, node.Tag, depth + 1);
                    }
                }
            }
        }

        Console.WriteLine("读取 " + files + " 个 .act(非标记流 " + notTokens + ");完全规整(没有多余、笔误闭合,没有游离值)的 " + clean + " 个");
        Console.WriteLine("  节点 " + nodes + "(块 " + blocks + ",块标签 " + blockCount.Count + " 种),最大嵌套深度 " + maxDepth);
        Console.WriteLine("  多余的闭合标签 " + stray + " 个 / " + withStray + " 个文件;笔误闭合按最内层块配对 " + repaired + " 个 / " + withRepaired
            + " 个文件;第一个标签之前的游离值 " + leading + " 个 / " + withLeading + " 个文件");

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
                files, notTokens, clean, nodes, blocks, stray, repaired, leading, maxDepth,
                blocksByTag = blockCount,
                children,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.JsonFile))!);
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }
}
