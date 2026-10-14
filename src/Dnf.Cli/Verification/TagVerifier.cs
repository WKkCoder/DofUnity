using System.Diagnostics;
using System.Text.Json;
using Dnf.Data;

namespace Dnf.Cli.Verification;

/// <summary>
/// 解析全库所有标记流脚本,与 Python 参考实现生成的 tag 目录(out/tag_catalog.json)逐项对拍:
/// 标记类型总数、跳过的文件数、不同开标签数、开标签出现次数,以及每个扩展名下每个标签的出现数、文件数与闭标签数。
/// 同时重算"前 N 个标签占全部出现次数的百分比"(P2 的覆盖率)。
/// </summary>
internal static class TagVerifier
{
    private sealed class ExtAccumulator
    {
        public long Files;
        public long Tokens;
        public readonly Dictionary<string, long> Open = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Close = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> TagFiles = new(StringComparer.Ordinal);

        public void MergeFrom(ExtAccumulator other)
        {
            Files += other.Files;
            Tokens += other.Tokens;
            Add(Open, other.Open);
            Add(Close, other.Close);
            Add(TagFiles, other.TagFiles);
        }
    }

    private sealed class Accumulator
    {
        public readonly long[] TypeTotals = new long[256];
        public readonly Dictionary<string, long> Skipped = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ExtAccumulator> PerExt = new(StringComparer.Ordinal);
        public long BadIndexes;

        public ExtAccumulator Ext(string ext)
        {
            if (!PerExt.TryGetValue(ext, out ExtAccumulator? e))
            {
                e = new ExtAccumulator();
                PerExt[ext] = e;
            }
            return e;
        }
    }

    private static void Add(Dictionary<string, long> into, Dictionary<string, long> from)
    {
        foreach (KeyValuePair<string, long> kv in from)
        {
            into[kv.Key] = into.TryGetValue(kv.Key, out long cur) ? cur + kv.Value : kv.Value;
        }
    }

    private static void Bump(Dictionary<string, long> d, string key)
    {
        d[key] = d.TryGetValue(key, out long cur) ? cur + 1 : 1;
    }

    /// <summary>与 Python 参考实现同一规则:取文件名最后一个 "." 之后的部分,没有 "." 记为 (none)。</summary>
    internal static string ExtensionOf(string path)
    {
        int slash = path.LastIndexOf('/');
        string baseName = slash >= 0 ? path.Substring(slash + 1) : path;
        int dot = baseName.LastIndexOf('.');
        return dot >= 0 ? baseName.Substring(dot + 1) : "(none)";
    }

    public static VerificationResult Run(DnfLocation loc, Options opts)
    {
        var r = new VerificationResult("tags");
        var sw = Stopwatch.StartNew();
        using JsonDocument catalog = JsonHelpers.Load(Path.Combine(loc.OutDir, "tag_catalog.json"));
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var table = new PvfStringTable(pvf.Read("stringtable.bin"));

        var total = new Accumulator();
        var gate = new object();
        var options = new ParallelOptions { MaxDegreeOfParallelism = opts.Threads };
        Parallel.For(0, pvf.Count, options, () => new Accumulator(), (i, _, local) =>
        {
            PvfEntry e = pvf.Entries[i];
            string ext = ExtensionOf(e.Path);
            if (ext == "ani" || ext == "nut" || e.Length < 2)
            {
                Bump(local.Skipped, ext);
                return local;
            }
            byte[] data = pvf.Read(e);
            if (!TokenScript.IsTokenScript(data))
            {
                Bump(local.Skipped, ext + ":non-token");
                return local;
            }
            ScriptToken[] tokens = TokenScript.Parse(data);
            ExtAccumulator acc = local.Ext(ext);
            acc.Files++;
            acc.Tokens += tokens.Length;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScriptToken t in tokens)
            {
                local.TypeTotals[t.Type]++;
                if (t.Type != (byte)TokenType.Tag)
                {
                    continue;
                }
                if ((uint)t.Value >= (uint)table.Count)
                {
                    local.BadIndexes++;
                    continue;
                }
                string name = table[t.Value];
                if (name.StartsWith("[/", StringComparison.Ordinal))
                {
                    Bump(acc.Close, name.Substring(2, name.Length - 3));
                }
                else
                {
                    Bump(acc.Open, name);
                    seen.Add(name);
                }
            }
            foreach (string s in seen)
            {
                Bump(acc.TagFiles, s);
            }
            return local;
        }, local =>
        {
            lock (gate)
            {
                for (int k = 0; k < 256; k++)
                {
                    total.TypeTotals[k] += local.TypeTotals[k];
                }
                Add(total.Skipped, local.Skipped);
                total.BadIndexes += local.BadIndexes;
                foreach (KeyValuePair<string, ExtAccumulator> kv in local.PerExt)
                {
                    total.Ext(kv.Key).MergeFrom(kv.Value);
                }
            }
        });

        r.Expect(total.BadIndexes == 0, "标签下标越界 " + total.BadIndexes + " 处");
        r.Metrics["bad_tag_indexes"] = total.BadIndexes;

        JsonElement root = catalog.RootElement;
        CompareTokenTypes(r, root, total);
        CompareSkipped(r, root, total);

        // 全库开标签统计与覆盖率
        var globalOpen = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (ExtAccumulator e in total.PerExt.Values)
        {
            Add(globalOpen, e.Open);
        }
        long occurrences = globalOpen.Values.Sum();
        r.Metrics["distinct_open_tags"] = (long)globalOpen.Count;
        r.Metrics["open_tag_occurrences"] = occurrences;
        r.Metrics["files_parsed"] = total.PerExt.Values.Sum(e => e.Files);
        r.Metrics["tokens_parsed"] = total.PerExt.Values.Sum(e => e.Tokens);
        r.ExpectEqual("不同的开标签数", root.GetProperty("distinct_open_tags").GetInt64(), (long)globalOpen.Count);
        r.ExpectEqual("开标签出现次数", root.GetProperty("open_tag_occurrences").GetInt64(), occurrences);

        long[] ranked = globalOpen.Values.OrderByDescending(v => v).ToArray();
        var coverage = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long acc2 = 0;
        int[] marks = { 10, 25, 50, 100, 200, 300, 500, 1000, 2000 };
        double[] coveragePct = new double[marks.Length];
        int mi = 0;
        for (int i = 0; i < ranked.Length && mi < marks.Length; i++)
        {
            acc2 += ranked[i];
            if (i + 1 == marks[mi])
            {
                coveragePct[mi] = Math.Round(100.0 * acc2 / occurrences, 2, MidpointRounding.ToEven);
                coverage["top_" + marks[mi] + "_x100"] = (long)Math.Round(coveragePct[mi] * 100);
                mi++;
            }
        }
        r.Metrics["coverage_pct_x100_by_top_n"] = coverage;
        JsonElement refCover = root.GetProperty("coverage_pct_by_top_n");
        for (int k = 0; k < marks.Length; k++)
        {
            if (refCover.TryGetProperty(marks[k].ToString(), out JsonElement v))
            {
                r.Expect(Math.Abs(v.GetDouble() - coveragePct[k]) <= 0.011,
                    "覆盖率 top " + marks[k] + ": 参考 " + v.GetDouble() + ",C# " + coveragePct[k]);
            }
        }

        CompareExtensions(r, root, total);
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    private static void CompareTokenTypes(VerificationResult r, JsonElement root, Accumulator total)
    {
        var expected = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.GetProperty("token_types").EnumerateObject())
        {
            expected[p.Name] = p.Value.GetInt64();
        }
        var actual = new SortedDictionary<string, long>(StringComparer.Ordinal);
        for (int k = 0; k < 256; k++)
        {
            if (total.TypeTotals[k] != 0)
            {
                actual[k.ToString()] = total.TypeTotals[k];
            }
        }
        r.Metrics["token_types"] = actual;
        CompareMaps(r, "标记类型计数", expected, actual);
    }

    private static void CompareSkipped(VerificationResult r, JsonElement root, Accumulator total)
    {
        var expected = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.GetProperty("skipped").EnumerateObject())
        {
            expected[p.Name] = p.Value.GetInt64();
        }
        var actual = new SortedDictionary<string, long>(total.Skipped, StringComparer.Ordinal);
        r.Metrics["skipped_total"] = actual.Values.Sum();
        CompareMaps(r, "跳过的文件", expected, actual);
    }

    private static void CompareMaps(VerificationResult r, string what, IDictionary<string, long> expected, IDictionary<string, long> actual)
    {
        foreach (KeyValuePair<string, long> kv in expected)
        {
            if (!actual.TryGetValue(kv.Key, out long got))
            {
                r.Fail(what + ": C# 缺少 " + kv.Key + "(参考 " + kv.Value + ")");
            }
            else if (got != kv.Value)
            {
                r.Fail(what + ": " + kv.Key + " 参考 " + kv.Value + ",C# " + got);
            }
        }
        foreach (string key in actual.Keys)
        {
            if (!expected.ContainsKey(key))
            {
                r.Fail(what + ": 参考里没有 " + key + "(C# " + actual[key] + ")");
            }
        }
    }

    private static void CompareExtensions(VerificationResult r, JsonElement root, Accumulator total)
    {
        JsonElement exts = root.GetProperty("extensions");
        var refNames = new HashSet<string>(StringComparer.Ordinal);
        long tagsCompared = 0;
        foreach (JsonProperty ext in exts.EnumerateObject())
        {
            refNames.Add(ext.Name);
            if (!total.PerExt.TryGetValue(ext.Name, out ExtAccumulator? acc))
            {
                r.Fail("扩展名 ." + ext.Name + " 在 C# 里没有任何标记流脚本");
                continue;
            }
            JsonElement v = ext.Value;
            r.ExpectEqual("." + ext.Name + " 文件数", v.GetProperty("files").GetInt64(), acc.Files);
            r.ExpectEqual("." + ext.Name + " 标记数", v.GetProperty("tokens").GetInt64(), acc.Tokens);
            r.ExpectEqual("." + ext.Name + " 不同标签数", v.GetProperty("distinct_tags").GetInt64(), (long)acc.Open.Count);
            r.ExpectEqual("." + ext.Name + " 开标签出现次数", v.GetProperty("open_tag_occurrences").GetInt64(), acc.Open.Values.Sum());
            var seenTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement t in v.GetProperty("tags").EnumerateArray())
            {
                string tag = t.GetProperty("tag").GetString() ?? "";
                seenTags.Add(tag);
                tagsCompared++;
                long count = t.GetProperty("count").GetInt64();
                long files = t.GetProperty("files").GetInt64();
                long closer = t.GetProperty("closer").GetInt64();
                acc.Open.TryGetValue(tag, out long gotCount);
                acc.TagFiles.TryGetValue(tag, out long gotFiles);
                acc.Close.TryGetValue(tag.Length >= 2 ? tag.Substring(1, tag.Length - 2) : tag, out long gotCloser);
                if (gotCount != count || gotFiles != files || gotCloser != closer)
                {
                    r.Fail("." + ext.Name + " " + tag + ": 参考 (出现 " + count + ", 文件 " + files + ", 闭 " + closer + ") / C# (" + gotCount + ", " + gotFiles + ", " + gotCloser + ")");
                }
            }
            foreach (string tag in acc.Open.Keys)
            {
                if (!seenTags.Contains(tag))
                {
                    r.Fail("." + ext.Name + " 的标签 " + tag + " 不在参考目录里");
                }
            }
        }
        foreach (string ext in total.PerExt.Keys)
        {
            if (!refNames.Contains(ext))
            {
                r.Fail("C# 解析出的扩展名 ." + ext + " 不在参考目录里");
            }
        }
        r.Metrics["extensions"] = (long)total.PerExt.Count;
        r.Metrics["per_extension_tags_compared"] = tagsCompared;
    }
}
