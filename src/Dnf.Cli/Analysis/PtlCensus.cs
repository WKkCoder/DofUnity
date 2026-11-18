using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf ptl-census [--json <文件>]`:只读普查 Script.pvf 里全部 .ptl 文件、各脚本对 .ptl 的引用以及 .act `[PARTICLE]` 的参数分布。
/// 严格脱敏:只输出标签名、取值类型、括号关键字、计数、极值与分桶分布;绝不输出真实文件名、编号与路径。
/// </summary>
internal static class PtlCensus
{
    private const int MaxTrackPositions = 8;

    private sealed class IntStats
    {
        public long Count;
        public long Min = long.MaxValue;
        public long Max = long.MinValue;
        public long Neg;
        public long Zero;
        public long Pos;
        public readonly HashSet<long> Distinct = new();
        public readonly long[] Buckets = new long[9]; // <-500, -500..-101, -100..-11, -10..-1, 0, 1..10, 11..100, 101..500, >500

        public void Add(long v)
        {
            Count++;
            if (v < Min) Min = v;
            if (v > Max) Max = v;
            if (v < 0) Neg++;
            else if (v == 0) Zero++;
            else Pos++;
            if (Distinct.Count < 10000) Distinct.Add(v);
            int b = v < -500 ? 0
                : v <= -101 ? 1
                : v <= -11 ? 2
                : v < 0 ? 3
                : v == 0 ? 4
                : v <= 10 ? 5
                : v <= 100 ? 6
                : v <= 500 ? 7
                : 8;
            Buckets[b]++;
        }

        public string FormatSummary()
        {
            if (Count == 0) return "count=0";
            string smallSet = Distinct.Count <= 8 && Min >= -10 && Max <= 20
                ? $" vals={{{string.Join(",", Distinct.OrderBy(x => x))}}}"
                : string.Empty;
            return $"n={Count} [{Min}..{Max}] distinct={Distinct.Count}{(Distinct.Count >= 10000 ? "+" : "")} (<0:{Neg}, ==0:{Zero}, >0:{Pos}){smallSet}";
        }

        public SortedDictionary<string, object?> ToJson() => new(StringComparer.Ordinal)
        {
            ["count"] = Count,
            ["min"] = Count == 0 ? null : Min,
            ["max"] = Count == 0 ? null : Max,
            ["neg"] = Neg,
            ["zero"] = Zero,
            ["pos"] = Pos,
            ["distinct"] = Distinct.Count,
            ["buckets"] = new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                ["lt_-500"] = Buckets[0],
                ["-500_-101"] = Buckets[1],
                ["-100_-11"] = Buckets[2],
                ["-10_-1"] = Buckets[3],
                ["0"] = Buckets[4],
                ["1_10"] = Buckets[5],
                ["11_100"] = Buckets[6],
                ["101_500"] = Buckets[7],
                ["gt_500"] = Buckets[8],
            },
        };
    }

    private sealed class FloatStats
    {
        public long Count;
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
        public long Neg;
        public long Zero;
        public long Pos;
        public long NonFinite;
        public readonly HashSet<double> Distinct = new();

        public void Add(float f)
        {
            Count++;
            if (float.IsNaN(f) || float.IsInfinity(f))
            {
                NonFinite++;
                return;
            }
            if (f < Min) Min = f;
            if (f > Max) Max = f;
            if (f < 0f) Neg++;
            else if (f == 0f) Zero++;
            else Pos++;
            if (Distinct.Count < 10000) Distinct.Add(Math.Round(f, 6));
        }

        public string FormatSummary()
        {
            if (Count == 0) return "count=0";
            return $"n={Count} [{Min:G6}..{Max:G6}] distinct={Distinct.Count}{(Distinct.Count >= 10000 ? "+" : "")} (<0:{Neg}, ==0:{Zero}, >0:{Pos}, nonFinite:{NonFinite})";
        }
    }

    private sealed class PosStats
    {
        public readonly SortedDictionary<string, long> Kinds = new(StringComparer.Ordinal);
        public readonly IntStats Ints = new();
        public readonly FloatStats Floats = new();
        public readonly SortedDictionary<string, long> Keywords = new(StringComparer.Ordinal);
        public long NonBracketStrings;
        public readonly HashSet<string> NonBracketDistinct = new(StringComparer.OrdinalIgnoreCase);
        public long EmptyStrings;
        public readonly SortedDictionary<string, long> StringExts = new(StringComparer.Ordinal);
    }

    private sealed class TagStats
    {
        public long Count;
        public long TopLevelCount;
        public long InBlockCount;
        public int MaxDepth;
        public readonly HashSet<int> FileIds = new();
        public readonly SortedDictionary<string, long> Shapes = new(StringComparer.Ordinal);
        public readonly PosStats[] Positions = new PosStats[MaxTrackPositions];
        public long ExtraValuesCount;
    }

    private sealed class RefGroup
    {
        public long Refs;
        public readonly HashSet<int> FileIds = new();
    }

    private static readonly string[] SqParticleApis =
    {
        "sq_AddObjectParticleCreater",
        "sq_SetMoveParticle",
        "sq_AddParticleObject",
        "sq_CreateParticle",
        "sq_CreateParticleByGlobal",
        "sq_RemoveParticle",
        "sq_RemoveMoveParticle",
        "sq_SetObjectParticlePos",
        "sq_SetSpeedToMoveParticle",
        "GetparticleCreaterMap",
        "sq_GetParticleInfo",
        "getDustParticleType",
    };

    public static int Run(DnfLocation loc, Options opts)
    {
        // 注册 CP949 编码支持，将旧版单文件里的韩文标签按原编码识别而不是乱码
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding cp949 = Encoding.GetEncoding(949);

        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));

        // 1. .ptl 全库标签与块普查
        int ptlFiles = 0, ptlTokenFiles = 0, ptlNonToken = 0, legacyKoFiles = 0;
        long ptlLines = 0;
        long objectOpenCount = 0, objectCloseCount = 0;
        int objectFiles = 0, maxObjectDepth = 0, unmatchedClosers = 0, unclosedOpeners = 0;
        var tagStats = new SortedDictionary<string, TagStats>(StringComparer.Ordinal);
        var closerPairs = new SortedDictionary<string, (long Opens, long Closes, long Unmatched)>(StringComparer.Ordinal);
        var nonStandardCasing = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var legacyKoTags = new HashSet<string>(StringComparer.Ordinal);

        // 2. 全库 .ptl 引用普查（只按扩展名和标签归类）
        var tokenRefs = new SortedDictionary<string, SortedDictionary<string, RefGroup>>(StringComparer.Ordinal);
        int nutFiles = 0, nutFilesWithPtl = 0;
        long nutPtlMentions = 0;
        var nutApiHits = new SortedDictionary<string, (long Calls, int Files)>(StringComparer.Ordinal);
        foreach (string api in SqParticleApis) nutApiHits[api] = (0, 0);
        var otherFileRefs = new SortedDictionary<string, int>(StringComparer.Ordinal);

        // 3. .act [PARTICLE] 普查
        int actFiles = 0, actWithParticle = 0;
        long actParticleNodes = 0;
        var actParents = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var actShapes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var actParamSlots = new[] { new IntStats(), new IntStats(), new IntStats(), new IntStats() };
        long actAllZeroTriples = 0, actHasNonZeroTriples = 0;
        long actStringCount = 0;
        var actStringDistinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ptlRegex = new Regex(@"\.ptl\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var nutApiRegexes = SqParticleApis.ToDictionary(
            a => a,
            a => new Regex(@"\b" + Regex.Escape(a) + @"\b", RegexOptions.CultureInvariant));

        int entryId = 0;
        foreach (PvfEntry e in pvf.Entries)
        {
            entryId++;
            string pathLower = e.Path.ToLowerInvariant();
            string ext = Path.GetExtension(pathLower);
            if (string.IsNullOrEmpty(ext)) ext = "<none>";

            byte[] raw = pvf.Read(e);

            if (ext == ".ptl")
            {
                ptlFiles++;
                if (!TokenScript.IsTokenScript(raw))
                {
                    ptlNonToken++;
                    continue;
                }
                ptlTokenFiles++;
                ScriptLine[] lines = ScriptDocument.Parse(raw, strings);
                var blockStack = new List<string>();
                bool hasObject = false;
                bool hasLegacyKo = false;

                foreach (ScriptLine line in lines)
                {
                    if (line.Tag == null) continue;
                    ptlLines++;
                    string rawTag = FixCp949IfNeeded(line.Tag, cp949, out bool isKoTag);
                    if (isKoTag) hasLegacyKo = true;
                    string tag = rawTag.ToLowerInvariant();
                    if (isKoTag) legacyKoTags.Add(tag);
                    if (!string.Equals(rawTag, tag, StringComparison.Ordinal))
                    {
                        nonStandardCasing[rawTag] = nonStandardCasing.GetValueOrDefault(rawTag) + 1;
                    }

                    bool isCloser = tag.Length > 2 && tag[0] == '[' && tag[1] == '/';
                    int depthBefore = blockStack.Count;
                    if (isCloser)
                    {
                        string openName = "[" + tag.Substring(2);
                        int idx = blockStack.LastIndexOf(openName);
                        closerPairs.TryGetValue(openName, out var cp);
                        if (idx >= 0)
                        {
                            blockStack.RemoveRange(idx, blockStack.Count - idx);
                            closerPairs[openName] = (cp.Opens, cp.Closes + 1, cp.Unmatched);
                        }
                        else
                        {
                            unmatchedClosers++;
                            closerPairs[openName] = (cp.Opens, cp.Closes + 1, cp.Unmatched + 1);
                        }
                        if (tag == "[/object]") objectCloseCount++;
                    }
                    else if (tag == "[object]" || tag == "[오브젝트]")
                    {
                        if (tag == "[object]")
                        {
                            hasObject = true;
                            objectOpenCount++;
                        }
                        closerPairs.TryGetValue(tag, out var cp);
                        closerPairs[tag] = (cp.Opens + 1, cp.Closes, cp.Unmatched);
                        blockStack.Add(tag);
                        if (blockStack.Count > maxObjectDepth) maxObjectDepth = blockStack.Count;
                    }

                    int recordDepth = isCloser ? Math.Max(0, depthBefore - 1) : depthBefore;
                    if (!tagStats.TryGetValue(tag, out TagStats? ts))
                    {
                        ts = new TagStats();
                        tagStats[tag] = ts;
                    }
                    ts.Count++;
                    if (recordDepth == 0) ts.TopLevelCount++;
                    else ts.InBlockCount++;
                    if (recordDepth > ts.MaxDepth) ts.MaxDepth = recordDepth;
                    ts.FileIds.Add(entryId);

                    var shape = new StringBuilder();
                    for (int i = 0; i < line.Values.Length; i++)
                    {
                        ScriptValue v = line.Values[i];
                        if (i > 0) shape.Append(' ');
                        shape.Append(KindLetter(v.Kind));
                        if (i >= MaxTrackPositions)
                        {
                            ts.ExtraValuesCount++;
                            continue;
                        }
                        PosStats ps = ts.Positions[i] ??= new PosStats();
                        string kName = v.Kind.ToString();
                        ps.Kinds[kName] = ps.Kinds.GetValueOrDefault(kName) + 1;
                        switch (v.Kind)
                        {
                            case ScriptValueKind.Int:
                            case ScriptValueKind.Hex:
                                ps.Ints.Add(v.Int);
                                break;
                            case ScriptValueKind.Float:
                                ps.Floats.Add(v.Float);
                                break;
                            case ScriptValueKind.String:
                            case ScriptValueKind.Paren:
                            case ScriptValueKind.Keyed:
                                string s = FixCp949IfNeeded(v.Text ?? string.Empty, cp949, out _);
                                string sLow = s.ToLowerInvariant();
                                if (IsBracketKeyword(sLow))
                                {
                                    ps.Keywords[sLow] = ps.Keywords.GetValueOrDefault(sLow) + 1;
                                }
                                else
                                {
                                    ps.NonBracketStrings++;
                                    if (string.IsNullOrEmpty(s))
                                    {
                                        ps.EmptyStrings++;
                                    }
                                    else
                                    {
                                        ps.NonBracketDistinct.Add(sLow);
                                        string sExt = Path.GetExtension(sLow);
                                        if (string.IsNullOrEmpty(sExt)) sExt = "<no_ext>";
                                        ps.StringExts[sExt] = ps.StringExts.GetValueOrDefault(sExt) + 1;
                                    }
                                }
                                break;
                        }
                    }
                    string shapeStr = SummarizeShape(tag, line.Values.Length, shape.ToString());
                    ts.Shapes[shapeStr] = ts.Shapes.GetValueOrDefault(shapeStr) + 1;
                }
                if (hasObject) objectFiles++;
                if (hasLegacyKo) legacyKoFiles++;
                unclosedOpeners += blockStack.Count;
                continue;
            }

            // 扫描所有标记流脚本里的 .ptl 引用
            if (TokenScript.IsTokenScript(raw))
            {
                ScriptLine[] lines = ScriptDocument.Parse(raw, strings);
                foreach (ScriptLine line in lines)
                {
                    string tag = (line.Tag ?? "<leading>").ToLowerInvariant();
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind is ScriptValueKind.String or ScriptValueKind.Paren or ScriptValueKind.Keyed
                            && v.Text != null
                            && v.Text.IndexOf(".ptl", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!tokenRefs.TryGetValue(ext, out var byTag))
                            {
                                byTag = new SortedDictionary<string, RefGroup>(StringComparer.Ordinal);
                                tokenRefs[ext] = byTag;
                            }
                            if (!byTag.TryGetValue(tag, out RefGroup? rg))
                            {
                                rg = new RefGroup();
                                byTag[tag] = rg;
                            }
                            rg.Refs++;
                            rg.FileIds.Add(entryId);
                        }
                    }
                }

                if (ext == ".act")
                {
                    actFiles++;
                    ActScript act = ActParser.Parse(lines);
                    bool fileHas = false;
                    WalkAct(act.Roots, "<root>");
                    if (fileHas) actWithParticle++;

                    void WalkAct(IReadOnlyList<ActNode> nodes, string parentTag)
                    {
                        foreach (ActNode node in nodes)
                        {
                            if (string.Equals(node.Tag, "[PARTICLE]", StringComparison.OrdinalIgnoreCase))
                            {
                                fileHas = true;
                                actParticleNodes++;
                                actParents[parentTag] = actParents.GetValueOrDefault(parentTag) + 1;
                                var shape = new StringBuilder();
                                var ints = new List<long>();
                                for (int i = 0; i < node.Values.Length; i++)
                                {
                                    ScriptValue v = node.Values[i];
                                    if (i > 0) shape.Append(' ');
                                    shape.Append(KindLetter(v.Kind));
                                    if (v.IsNumber) ints.Add(v.Int);
                                    else if (v.Kind == ScriptValueKind.Float) ints.Add(ScriptCursor.TruncateToInt(v.Float));
                                    else if (v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text))
                                    {
                                        actStringCount++;
                                        actStringDistinct.Add(v.Text.ToLowerInvariant());
                                    }
                                }
                                string sStr = shape.ToString();
                                actShapes[sStr] = actShapes.GetValueOrDefault(sStr) + 1;
                                for (int idx = 0; idx < ints.Count && idx < actParamSlots.Length; idx++)
                                {
                                    actParamSlots[idx].Add(ints[idx]);
                                }
                                if (ints.Count >= 3)
                                {
                                    if (ints[0] == 0 && ints[1] == 0 && ints[2] == 0) actAllZeroTriples++;
                                    else actHasNonZeroTriples++;
                                }
                            }
                            if (node.IsBlock && node.Children.Count > 0)
                            {
                                WalkAct(node.Children, node.Tag);
                            }
                        }
                    }
                }
            }
            else if (ext == ".nut")
            {
                nutFiles++;
                string text = Encoding.UTF8.GetString(raw);
                int hits = ptlRegex.Matches(text).Count;
                if (hits > 0)
                {
                    nutFilesWithPtl++;
                    nutPtlMentions += hits;
                }
                foreach (string api in SqParticleApis)
                {
                    int c = nutApiRegexes[api].Matches(text).Count;
                    if (c > 0)
                    {
                        var prev = nutApiHits[api];
                        nutApiHits[api] = (prev.Calls + c, prev.Files + 1);
                    }
                }
            }
            else if (ext != ".ani" && ext != ".img" && ext != ".ogg" && !string.Equals(Path.GetFileName(pathLower), "stringtable.bin", StringComparison.Ordinal))
            {
                if (ContainsAsciiCaseInsensitive(raw, ".ptl"u8))
                {
                    otherFileRefs[ext] = otherFileRefs.GetValueOrDefault(ext) + 1;
                }
            }
        }

        // 计算完全共现的成对标签（仅统计 >=2 个文件，避免单个旧版韩文文件内标签两两组合产生噪音）
        var exactCooccurringPairs = new List<(string A, string B, int Files)>();
        var tagList = tagStats.Keys.ToList();
        for (int i = 0; i < tagList.Count; i++)
        {
            TagStats a = tagStats[tagList[i]];
            if (a.FileIds.Count < 2) continue;
            for (int j = i + 1; j < tagList.Count; j++)
            {
                TagStats b = tagStats[tagList[j]];
                if (a.FileIds.Count == b.FileIds.Count && a.FileIds.SetEquals(b.FileIds))
                {
                    exactCooccurringPairs.Add((tagList[i], tagList[j], a.FileIds.Count));
                }
            }
        }

        int enOpen = tagStats.Keys.Count(k => !legacyKoTags.Contains(k) && !k.StartsWith("[/", StringComparison.Ordinal));
        int enClose = tagStats.Keys.Count(k => !legacyKoTags.Contains(k) && k.StartsWith("[/", StringComparison.Ordinal));
        int koOpen = tagStats.Keys.Count(k => legacyKoTags.Contains(k) && !k.StartsWith("[/", StringComparison.Ordinal));
        int koClose = tagStats.Keys.Count(k => legacyKoTags.Contains(k) && k.StartsWith("[/", StringComparison.Ordinal));

        // 输出（脱敏）
        Console.WriteLine($"=== .ptl 全库普查 ===");
        Console.WriteLine($"文件: 共 {ptlFiles} 个 (标记流 {ptlTokenFiles}, 非标记流 {ptlNonToken}, 含旧版韩文标签文件 {legacyKoFiles}), 总标签行 {ptlLines}, 不同标签 {tagStats.Count} 种 (英文开 {enOpen} + 英文闭 {enClose} + 韩文旧版开 {koOpen} + 韩文旧版闭 {koClose})");
        Console.WriteLine($"块结构: [object] 出现 {objectOpenCount} 次 / {objectFiles} 个文件, [/object] {objectCloseCount} 次, 最大嵌套深度 {maxObjectDepth}, 未闭合开标签 {unclosedOpeners}, 多余闭标签 {unmatchedClosers}");
        if (nonStandardCasing.Count > 0)
        {
            Console.WriteLine($"大小写混合标签种数: {nonStandardCasing.Count} ({string.Join(", ", nonStandardCasing.Select(k => $"{k.Key}:{k.Value}"))})");
        }

        Console.WriteLine($"\n--- {tagStats.Count} 种标签明细 (次数 / 文件数 / 顶层:块内 / 形状 / 取值域) ---");
        foreach (KeyValuePair<string, TagStats> kv in tagStats.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            TagStats ts = kv.Value;
            string shapesStr = string.Join(", ", ts.Shapes.OrderByDescending(s => s.Value).Select(s => $"\"{s.Key}\":{s.Value}"));
            Console.WriteLine($"  {kv.Key,-30} count={ts.Count,5}  files={ts.FileIds.Count,4}  top/inObj={ts.TopLevelCount}/{ts.InBlockCount} (maxD={ts.MaxDepth})  shapes=[{shapesStr}]");
            for (int p = 0; p < MaxTrackPositions; p++)
            {
                PosStats? ps = ts.Positions[p];
                if (ps == null) break;
                var parts = new List<string>();
                if (ps.Ints.Count > 0) parts.Add("int:" + ps.Ints.FormatSummary());
                if (ps.Floats.Count > 0) parts.Add("flt:" + ps.Floats.FormatSummary());
                if (ps.Keywords.Count > 0)
                {
                    string kws = string.Join(", ", ps.Keywords.OrderByDescending(x => x.Value).Select(x => $"{x.Key}:{x.Value}"));
                    parts.Add("kw:{" + kws + "}");
                }
                if (ps.NonBracketStrings > 0)
                {
                    string exts = ps.StringExts.Count == 0
                        ? string.Empty
                        : " exts={" + string.Join(",", ps.StringExts.Select(x => $"{x.Key}:{x.Value}")) + "}";
                    parts.Add($"str:n={ps.NonBracketStrings}(distinct={ps.NonBracketDistinct.Count},empty={ps.EmptyStrings}{exts})");
                }
                if (parts.Count > 0)
                {
                    Console.WriteLine($"      arg[{p}]: {string.Join(" | ", parts)}");
                }
            }
        }

        Console.WriteLine("\n--- 成对 / 共现标签 ---");
        foreach (var cp in closerPairs)
        {
            Console.WriteLine($"  开闭对 {cp.Key} / [/{cp.Key.Substring(1)}: opens={cp.Value.Opens}, closes={cp.Value.Closes}, unmatched={cp.Value.Unmatched}");
        }
        if (exactCooccurringPairs.Count == 0)
        {
            Console.WriteLine("  文件级完全共现标签对: 无");
        }
        else
        {
            foreach (var p in exactCooccurringPairs)
            {
                Console.WriteLine($"  完全共现对 {p.A} <-> {p.B}: {p.Files} 个文件");
            }
        }

        Console.WriteLine("\n=== 各脚本引用 .ptl 次数 ===");
        foreach (var extKv in tokenRefs)
        {
            long sumRefs = extKv.Value.Values.Sum(v => v.Refs);
            int distinctFiles = extKv.Value.Values.SelectMany(v => v.FileIds).Distinct().Count();
            Console.WriteLine($"  {extKv.Key}: {sumRefs} 次引用 / {distinctFiles} 个文件");
            foreach (var tagKv in extKv.Value.OrderByDescending(x => x.Value.Refs))
            {
                Console.WriteLine($"      {tagKv.Key,-34} refs={tagKv.Value.Refs,5}  files={tagKv.Value.FileIds.Count,4}");
            }
        }
        Console.WriteLine($"  .nut (Squirrel): 扫描 {nutFiles} 个文件, 字面提及 .ptl {nutPtlMentions} 次 / {nutFilesWithPtl} 个文件");
        foreach (var kv in nutApiHits.Where(k => k.Value.Calls > 0).OrderByDescending(k => k.Value.Calls))
        {
            Console.WriteLine($"      API {kv.Key,-30} calls={kv.Value.Calls,4}  files={kv.Value.Files,3}");
        }
        if (otherFileRefs.Count > 0)
        {
            Console.WriteLine("  其它非标记流文件包含 .ptl 字节: " + string.Join(", ", otherFileRefs.Select(k => $"{k.Key}:{k.Value}文件")));
        }

        Console.WriteLine("\n=== .act [PARTICLE] 参数分布 ===");
        Console.WriteLine($"  .act 总文件 {actFiles}, 含 [PARTICLE] {actWithParticle} 个文件, 共 {actParticleNodes} 次");
        Console.WriteLine($"  父节点分布: {string.Join(", ", actParents.OrderByDescending(k => k.Value).Select(k => $"{k.Key}:{k.Value}"))}");
        Console.WriteLine($"  参数形状分布: {string.Join(", ", actShapes.OrderByDescending(k => k.Value).Select(k => $"\"{k.Key}\":{k.Value}"))}");
        Console.WriteLine($"  字符串实参(只报总数): {actStringCount} 次 (去重 {actStringDistinct.Count} 种)");
        Console.WriteLine($"  三整数全为0: {actAllZeroTriples}, 至少一个非0: {actHasNonZeroTriples}");
        for (int s = 0; s < actParamSlots.Length; s++)
        {
            if (actParamSlots[s].Count == 0) continue;
            IntStats st = actParamSlots[s];
            Console.WriteLine($"  int[{s}]: {st.FormatSummary()}");
            Console.WriteLine($"      分桶: <-500:{st.Buckets[0]}  [-500..-101]:{st.Buckets[1]}  [-100..-11]:{st.Buckets[2]}  [-10..-1]:{st.Buckets[3]}  0:{st.Buckets[4]}  [1..10]:{st.Buckets[5]}  [11..100]:{st.Buckets[6]}  [101..500]:{st.Buckets[7]}  >500:{st.Buckets[8]}");
        }

        if (opts.JsonFile != null)
        {
            var jsonTags = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in tagStats)
            {
                TagStats ts = kv.Value;
                var posList = new List<object?>();
                for (int p = 0; p < MaxTrackPositions; p++)
                {
                    PosStats? ps = ts.Positions[p];
                    if (ps == null) break;
                    posList.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["kinds"] = ps.Kinds,
                        ["ints"] = ps.Ints.Count > 0 ? ps.Ints.ToJson() : null,
                        ["floats"] = ps.Floats.Count > 0 ? new SortedDictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["count"] = ps.Floats.Count,
                            ["min"] = ps.Floats.Min,
                            ["max"] = ps.Floats.Max,
                            ["neg"] = ps.Floats.Neg,
                            ["zero"] = ps.Floats.Zero,
                            ["pos"] = ps.Floats.Pos,
                            ["distinct"] = ps.Floats.Distinct.Count,
                        } : null,
                        ["keywords"] = ps.Keywords,
                        ["nonBracketStrings"] = ps.NonBracketStrings,
                        ["nonBracketDistinct"] = ps.NonBracketDistinct.Count,
                        ["stringExts"] = ps.StringExts,
                    });
                }
                jsonTags[kv.Key] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["count"] = ts.Count,
                    ["files"] = ts.FileIds.Count,
                    ["topLevel"] = ts.TopLevelCount,
                    ["inObject"] = ts.InBlockCount,
                    ["maxDepth"] = ts.MaxDepth,
                    ["shapes"] = ts.Shapes,
                    ["positions"] = posList,
                };
            }

            var jsonOut = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ptlFiles"] = ptlFiles,
                ["ptlTokenFiles"] = ptlTokenFiles,
                ["ptlNonToken"] = ptlNonToken,
                ["legacyKoreanFiles"] = legacyKoFiles,
                ["ptlLines"] = ptlLines,
                ["distinctTags"] = tagStats.Count,
                ["tagBreakdown"] = new SortedDictionary<string, int>(StringComparer.Ordinal)
                {
                    ["englishOpen"] = enOpen,
                    ["englishClose"] = enClose,
                    ["legacyKoreanOpen"] = koOpen,
                    ["legacyKoreanClose"] = koClose,
                },
                ["objectOpens"] = objectOpenCount,
                ["objectCloses"] = objectCloseCount,
                ["objectFiles"] = objectFiles,
                ["maxObjectDepth"] = maxObjectDepth,
                ["unclosedOpeners"] = unclosedOpeners,
                ["unmatchedClosers"] = unmatchedClosers,
                ["nonStandardCasing"] = nonStandardCasing,
                ["tags"] = jsonTags,
                ["tokenRefs"] = tokenRefs.ToDictionary(
                    e => e.Key,
                    e => e.Value.ToDictionary(t => t.Key, t => new { refs = t.Value.Refs, files = t.Value.FileIds.Count })),
                ["nutStats"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nutFiles"] = nutFiles,
                    ["filesWithPtl"] = nutFilesWithPtl,
                    ["ptlMentions"] = nutPtlMentions,
                    ["apiHits"] = nutApiHits.ToDictionary(k => k.Key, k => new { calls = k.Value.Calls, files = k.Value.Files }),
                },
                ["actParticle"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["actFiles"] = actFiles,
                    ["filesWithParticle"] = actWithParticle,
                    ["nodes"] = actParticleNodes,
                    ["parents"] = actParents,
                    ["shapes"] = actShapes,
                    ["distinctStrings"] = actStringDistinct.Count,
                    ["allZeroTriples"] = actAllZeroTriples,
                    ["hasNonZeroTriples"] = actHasNonZeroTriples,
                    ["int0"] = actParamSlots[0].ToJson(),
                    ["int1"] = actParamSlots[1].ToJson(),
                    ["int2"] = actParamSlots[2].ToJson(),
                },
            };

            string full = Path.GetFullPath(opts.JsonFile);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, JsonSerializer.Serialize(jsonOut, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"\n已写出脱敏汇总 JSON -> {opts.JsonFile}");
        }

        return 0;
    }

    private static string FixCp949IfNeeded(string s, Encoding cp949, out bool hasNonAscii)
    {
        hasNonAscii = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] >= 0x80)
            {
                hasNonAscii = true;
                break;
            }
        }
        if (!hasNonAscii) return s;

        byte[] raw = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c > 0xFF) return s;
            raw[i] = (byte)c;
        }
        return cp949.GetString(raw);
    }

    private static string SummarizeShape(string tag, int valueCount, string rawShape)
    {
        if (tag == "[object]" && valueCount > 8)
        {
            int pairs = valueCount / 2;
            return valueCount % 2 == 0 ? $"(s i)*{pairs}" : $"(s i)*{pairs} +1";
        }
        return rawShape;
    }

    private static char KindLetter(ScriptValueKind k) => k switch
    {
        ScriptValueKind.Int => 'i',
        ScriptValueKind.Hex => 'h',
        ScriptValueKind.Float => 'f',
        ScriptValueKind.String => 's',
        ScriptValueKind.Paren => 'p',
        ScriptValueKind.Keyed => 'k',
        ScriptValueKind.Separator => ',',
        _ => '?',
    };

    private static bool IsBracketKeyword(string s)
    {
        if (s.Length < 2 || s[0] != '[' || s[^1] != ']') return false;
        return !s.Contains('/') && !s.Contains('\\') && !s.Contains('.');
    }

    private static bool ContainsAsciiCaseInsensitive(byte[] haystack, ReadOnlySpan<byte> needleLower)
    {
        if (haystack.Length < needleLower.Length) return false;
        for (int i = 0; i <= haystack.Length - needleLower.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needleLower.Length; j++)
            {
                byte b = haystack[i + j];
                if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
                if (b != needleLower[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }
}
