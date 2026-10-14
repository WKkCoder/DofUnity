using System.Diagnostics;
using System.Text.Json;
using Dnf.Data;

namespace Dnf.Cli.Verification;

/// <summary>
/// 解析全库二进制 .ani,与 Python 参考实现(out/ani_verify.json)对拍:
/// 成功数、失败分类、各类属性与判定框的出现次数,以及帧延时的取值分布。
/// </summary>
internal static class AniVerifier
{
    private sealed class Accumulator
    {
        public long Total;
        public long Ok;
        public readonly Dictionary<string, long> Fail = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Stat = new(StringComparer.Ordinal);
        public readonly Dictionary<int, long> Delays = new();
        public long Frames;
        public long Boxes;
    }

    private static void Bump(Dictionary<string, long> d, string key)
    {
        d[key] = d.TryGetValue(key, out long cur) ? cur + 1 : 1;
    }

    public static VerificationResult Run(DnfLocation loc, Options opts)
    {
        var r = new VerificationResult("ani");
        var sw = Stopwatch.StartNew();
        using JsonDocument reference = JsonHelpers.Load(Path.Combine(loc.OutDir, "ani_verify.json"));
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);

        var total = new Accumulator();
        var gate = new object();
        var options = new ParallelOptions { MaxDegreeOfParallelism = opts.Threads };
        Parallel.For(0, pvf.Count, options, () => new Accumulator(), (i, _, local) =>
        {
            PvfEntry e = pvf.Entries[i];
            if (!e.Path.EndsWith(".ani", StringComparison.Ordinal))
            {
                return local;
            }
            byte[] data = pvf.Read(e);
            if (data.Length < 8 || (data[0] == 0xB0 && data[1] == 0xD0) || data[0] == (byte)'[')
            {
                Bump(local.Fail, "empty-or-text");
                return local;
            }
            local.Total++;
            AniFile ani;
            try
            {
                ani = AniFile.Parse(data);
            }
            catch (DnfFormatException ex)
            {
                Bump(local.Fail, ex.Message);
                return local;
            }
            local.Ok++;
            foreach (AniProperty g in ani.GlobalProperties)
            {
                Bump(local.Stat, "G:" + g.Id);
            }
            foreach (AniFrame f in ani.Frames)
            {
                local.Frames++;
                foreach (AniBox b in f.Boxes)
                {
                    local.Boxes++;
                    Bump(local.Stat, "B:" + b.Type);
                }
                foreach (AniProperty p in f.Properties)
                {
                    Bump(local.Stat, "F:" + p.Id);
                    if (p.Id == AniFrameProperty.Delay)
                    {
                        int ms = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(p.Payload);
                        local.Delays[ms] = local.Delays.TryGetValue(ms, out long cur) ? cur + 1 : 1;
                    }
                    else if (p.Id == AniFrameProperty.GraphicEffect)
                    {
                        Bump(local.Stat, "E:" + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(p.Payload));
                    }
                }
            }
            return local;
        }, local =>
        {
            lock (gate)
            {
                total.Total += local.Total;
                total.Ok += local.Ok;
                total.Frames += local.Frames;
                total.Boxes += local.Boxes;
                foreach (KeyValuePair<string, long> kv in local.Fail)
                {
                    total.Fail[kv.Key] = total.Fail.TryGetValue(kv.Key, out long c) ? c + kv.Value : kv.Value;
                }
                foreach (KeyValuePair<string, long> kv in local.Stat)
                {
                    total.Stat[kv.Key] = total.Stat.TryGetValue(kv.Key, out long c) ? c + kv.Value : kv.Value;
                }
                foreach (KeyValuePair<int, long> kv in local.Delays)
                {
                    total.Delays[kv.Key] = total.Delays.TryGetValue(kv.Key, out long c) ? c + kv.Value : kv.Value;
                }
            }
        });

        JsonElement root = reference.RootElement;
        r.Metrics["parsed_ok"] = total.Ok;
        r.Metrics["attempted"] = total.Total;
        r.Metrics["frames"] = total.Frames;
        r.Metrics["boxes"] = total.Boxes;
        r.Metrics["empty_or_text"] = total.Fail.TryGetValue("empty-or-text", out long eot) ? eot : 0L;
        r.ExpectEqual("成功解析的 .ani 数", root.GetProperty("ok").GetInt64(), total.Ok);
        r.ExpectEqual("尝试解析的 .ani 数", root.GetProperty("total").GetInt64(), total.Total);
        r.Expect(total.Ok == total.Total, "有 " + (total.Total - total.Ok) + " 个二进制 .ani 解析失败");

        var expectedFail = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.GetProperty("fail").EnumerateObject())
        {
            expectedFail[p.Name] = p.Value.GetInt64();
        }
        CompareMaps(r, "失败分类", expectedFail, total.Fail);

        var expectedStat = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.GetProperty("stat").EnumerateObject())
        {
            expectedStat[p.Name] = p.Value.GetInt64();
        }
        CompareMaps(r, "属性与判定框计数", expectedStat, total.Stat);
        r.Metrics["stat_keys"] = (long)total.Stat.Count;

        foreach (JsonElement pair in root.GetProperty("delay_top").EnumerateArray())
        {
            int ms = (int)pair[0].GetInt64();
            long count = pair[1].GetInt64();
            total.Delays.TryGetValue(ms, out long got);
            r.Expect(got == count, "帧延时 " + ms + "ms: 参考 " + count + ",C# " + got);
        }
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
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
        foreach (KeyValuePair<string, long> kv in actual)
        {
            if (!expected.ContainsKey(kv.Key))
            {
                r.Fail(what + ": 参考里没有 " + kv.Key + "(C# " + kv.Value + ")");
            }
        }
    }
}
