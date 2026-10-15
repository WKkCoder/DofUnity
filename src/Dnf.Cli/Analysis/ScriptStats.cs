using System.Text;
using System.Text.Json;
using Dnf.Data;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf script-stats --ext &lt;扩展名&gt; [--json &lt;文件&gt;]`:按"一个标签一行"读全库某一类标记流脚本(默认 ai),
/// 对每个标签统计:出现次数、涉及文件数、每个值位置上的值种类直方图、字符串值直方图(前 N 个)、整数与浮点的取值范围,
/// 以及"第一个字符串 → 形状"的表(形状如 "s i f2",s=字符串 i=整数 f=浮点 h=十六进制 p=括号词 k=键引用)。
/// 用途:在读客户端的脚本解释器之前,先弄清某种脚本的词汇(例如 .ai 里 [void] 调用的函数名与参数个数)。
/// --only &lt;路径子串&gt;(可重复)只统计路径含该子串的文件(例如 /ai/movemethod.ai)。
/// 输出的是词汇与形状,不是脚本全文;输出文件放 out/,不入库。
/// </summary>
internal static class ScriptStats
{
    private const int MaxPositions = 12;
    private const int MaxDistinct = 4000;

    private sealed class PositionStats
    {
        public readonly SortedDictionary<string, long> Kinds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Strings = new(StringComparer.Ordinal);
        public long StringOverflow;
        public long IntMin = long.MaxValue, IntMax = long.MinValue;
        public double FloatMin = double.MaxValue, FloatMax = double.MinValue;
    }

    private sealed class TagStats
    {
        public long Count;
        public readonly HashSet<string> Files = new(StringComparer.Ordinal);
        public readonly PositionStats[] Positions = new PositionStats[MaxPositions];
        public readonly SortedDictionary<string, long> Shapes = new(StringComparer.Ordinal);
        public readonly Dictionary<string, SortedDictionary<string, long>> ByFirstString = new(StringComparer.Ordinal);
        public long FirstStringOverflow;
        public long LongerThanMax;
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        string ext = (opts.Ext ?? "ai").TrimStart('.').ToLowerInvariant();
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));
        var tags = new SortedDictionary<string, TagStats>(StringComparer.Ordinal);
        int files = 0, notTokens = 0;
        long lines = 0;

        foreach (PvfEntry e in pvf.Entries)
        {
            if (!e.Path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string path = e.Path.ToLowerInvariant();
            if (opts.Only.Count > 0 && !opts.Only.Any(o => path.Contains(o.ToLowerInvariant(), StringComparison.Ordinal)))
            {
                continue;
            }
            byte[] raw = pvf.Read(e);
            if (!TokenScript.IsTokenScript(raw))
            {
                notTokens++;
                continue;
            }
            files++;
            foreach (ScriptLine line in ScriptDocument.Parse(raw, strings))
            {
                if (line.Tag == null)
                {
                    continue;
                }
                lines++;
                string tagName = line.Tag.ToLowerInvariant();
                if (!tags.TryGetValue(tagName, out TagStats? ts))
                {
                    ts = new TagStats();
                    tags[tagName] = ts;
                }
                ts.Count++;
                ts.Files.Add(path);
                var shape = new StringBuilder();
                string? first = null;
                for (int i = 0; i < line.Values.Length; i++)
                {
                    ScriptValue v = line.Values[i];
                    shape.Append(i == 0 ? "" : " ").Append(KindLetter(v.Kind));
                    if (v.Kind == ScriptValueKind.String && first == null)
                    {
                        first = v.Text ?? "";
                    }
                    if (i >= MaxPositions)
                    {
                        ts.LongerThanMax++;
                        continue;
                    }
                    PositionStats ps = ts.Positions[i] ??= new PositionStats();
                    string kind = v.Kind.ToString();
                    ps.Kinds[kind] = ps.Kinds.GetValueOrDefault(kind) + 1;
                    switch (v.Kind)
                    {
                        case ScriptValueKind.Int:
                        case ScriptValueKind.Hex:
                            ps.IntMin = Math.Min(ps.IntMin, v.Int);
                            ps.IntMax = Math.Max(ps.IntMax, v.Int);
                            break;
                        case ScriptValueKind.Float:
                            ps.FloatMin = Math.Min(ps.FloatMin, v.Float);
                            ps.FloatMax = Math.Max(ps.FloatMax, v.Float);
                            break;
                        case ScriptValueKind.String:
                        case ScriptValueKind.Paren:
                        case ScriptValueKind.Keyed:
                            string s = (v.Kind == ScriptValueKind.Keyed ? v.Int + ":" : "") + (v.Text ?? "<null>");
                            if (ps.Strings.TryGetValue(s, out long n))
                            {
                                ps.Strings[s] = n + 1;
                            }
                            else if (ps.Strings.Count < MaxDistinct)
                            {
                                ps.Strings[s] = 1;
                            }
                            else
                            {
                                ps.StringOverflow++;
                            }
                            break;
                    }
                }
                string shapeText = shape.ToString();
                ts.Shapes[shapeText] = ts.Shapes.GetValueOrDefault(shapeText) + 1;
                if (first != null)
                {
                    if (!ts.ByFirstString.TryGetValue(first, out SortedDictionary<string, long>? bySig))
                    {
                        if (ts.ByFirstString.Count >= MaxDistinct)
                        {
                            ts.FirstStringOverflow++;
                            continue;
                        }
                        bySig = new SortedDictionary<string, long>(StringComparer.Ordinal);
                        ts.ByFirstString[first] = bySig;
                    }
                    bySig[shapeText] = bySig.GetValueOrDefault(shapeText) + 1;
                }
            }
        }

        Console.WriteLine($"扩展名 .{ext}:{files} 个标记流文件(非标记流 {notTokens}),{lines} 行,{tags.Count} 个不同标签");
        int top = Math.Max(1, opts.Top);
        foreach (KeyValuePair<string, TagStats> kv in tags.OrderByDescending(k => k.Value.Count).Take(top))
        {
            Console.WriteLine($"  {kv.Key,-36} 次数 {kv.Value.Count,8}  文件 {kv.Value.Files.Count,6}  形状数 {kv.Value.Shapes.Count}");
        }

        if (opts.JsonFile != null)
        {
            var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["extension"] = ext,
                ["files"] = files,
                ["notTokens"] = notTokens,
                ["lines"] = lines,
            };
            var tagJson = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TagStats> kv in tags)
            {
                TagStats ts = kv.Value;
                var positions = new List<object>();
                for (int i = 0; i < MaxPositions; i++)
                {
                    PositionStats? ps = ts.Positions[i];
                    if (ps == null)
                    {
                        break;
                    }
                    positions.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["kinds"] = ps.Kinds,
                        ["intMin"] = ps.IntMin == long.MaxValue ? null : ps.IntMin,
                        ["intMax"] = ps.IntMax == long.MinValue ? null : ps.IntMax,
                        ["floatMin"] = ps.FloatMin == double.MaxValue ? null : ps.FloatMin,
                        ["floatMax"] = ps.FloatMax == double.MinValue ? null : ps.FloatMax,
                        ["distinctStrings"] = ps.Strings.Count,
                        ["stringOverflow"] = ps.StringOverflow,
                        ["strings"] = ps.Strings.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)
                            .ToDictionary(s => s.Key, s => s.Value),
                    });
                }
                tagJson[kv.Key] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["count"] = ts.Count,
                    ["files"] = ts.Files.Count,
                    ["shapes"] = ts.Shapes,
                    ["longerThanMax"] = ts.LongerThanMax,
                    ["positions"] = positions,
                    ["byFirstString"] = ts.ByFirstString,
                    ["firstStringOverflow"] = ts.FirstStringOverflow,
                };
            }
            root["tags"] = tagJson;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.JsonFile)) ?? ".");
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("已写 " + opts.JsonFile);
        }
        return 0;
    }

    private static char KindLetter(ScriptValueKind kind) => kind switch
    {
        ScriptValueKind.Int => 'i',
        ScriptValueKind.Hex => 'h',
        ScriptValueKind.Float => 'f',
        ScriptValueKind.String => 's',
        ScriptValueKind.Paren => 'p',
        ScriptValueKind.Keyed => 'k',
        ScriptValueKind.Separator => '|',
        _ => '?',
    };
}
