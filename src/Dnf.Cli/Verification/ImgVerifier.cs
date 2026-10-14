using System.Diagnostics;
using System.Text.Json;
using Dnf.Data;

namespace Dnf.Cli.Verification;

/// <summary>
/// 全量扫描 ImagePacks2 下的 NPK 与其中的 IMG v2,与 Python 参考实现(out/img_scan.json)的计数逐项对拍。
/// 每个 zlib 帧都校验 Adler-32;1×1 的截断占位帧按已知规则计入 anomaly:zlib(参考里同样是 97 个)。
/// </summary>
internal static class ImgVerifier
{
    private const int MaxExamples = 25;

    private sealed class Accumulator
    {
        public readonly Dictionary<string, long> Counters = new(StringComparer.Ordinal);
        public long CompBytes;
        public long RawBytes;
        public readonly List<string> Examples = new();

        public void Bump(string key, long by = 1)
        {
            Counters[key] = Counters.TryGetValue(key, out long cur) ? cur + by : by;
        }

        public void Anomaly(string kind, string detail)
        {
            Bump("anomaly:" + kind);
            if (Examples.Count < MaxExamples)
            {
                Examples.Add(kind + ": " + detail);
            }
        }
    }

    public static VerificationResult Run(DnfLocation loc, Options opts)
    {
        var r = new VerificationResult("imgs");
        var sw = Stopwatch.StartNew();
        using JsonDocument reference = JsonHelpers.Load(Path.Combine(loc.OutDir, "img_scan.json"));

        // 大文件先跑,避免尾部拖尾
        string[] paths = Directory.GetFiles(loc.NpkDir, "*.*")
            .Where(p => p.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => new FileInfo(p).Length)
            .ToArray();
        r.Metrics["npk_files"] = (long)paths.Length;

        var total = new Accumulator();
        var gate = new object();
        int done = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = opts.Threads };
        Parallel.ForEach(paths, options, () => new Accumulator(), (path, _, local) =>
        {
            ScanNpk(path, local);
            int d = Interlocked.Increment(ref done);
            if (d % 200 == 0 || d == paths.Length)
            {
                Console.Error.WriteLine("  imgs: " + d + "/" + paths.Length + " NPK, " + sw.Elapsed.TotalSeconds.ToString("F0") + "s");
            }
            return local;
        }, local =>
        {
            lock (gate)
            {
                foreach (KeyValuePair<string, long> kv in local.Counters)
                {
                    total.Bump(kv.Key, kv.Value);
                }
                total.CompBytes += local.CompBytes;
                total.RawBytes += local.RawBytes;
                foreach (string ex in local.Examples)
                {
                    if (total.Examples.Count < MaxExamples)
                    {
                        total.Examples.Add(ex);
                    }
                }
            }
        });

        JsonElement root = reference.RootElement;
        r.ExpectEqual("NPK 文件数", root.GetProperty("npk_files").GetInt64(), (long)paths.Length);

        var expected = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.GetProperty("counters").EnumerateObject())
        {
            expected[p.Name] = p.Value.GetInt64();
        }
        foreach (KeyValuePair<string, long> kv in expected)
        {
            if (!total.Counters.TryGetValue(kv.Key, out long got))
            {
                r.Fail("计数 " + kv.Key + ": C# 没有(参考 " + kv.Value + ")");
            }
            else if (got != kv.Value)
            {
                r.Fail("计数 " + kv.Key + ": 参考 " + kv.Value + ",C# " + got);
            }
        }
        foreach (KeyValuePair<string, long> kv in total.Counters)
        {
            if (!expected.ContainsKey(kv.Key))
            {
                r.Fail("计数 " + kv.Key + ": 参考里没有(C# " + kv.Value + ")");
            }
        }
        JsonElement sizes = root.GetProperty("sizes");
        r.ExpectEqual("压缩字节数", sizes.GetProperty("comp_bytes").GetInt64(), total.CompBytes);
        r.ExpectEqual("解压字节数", sizes.GetProperty("raw_bytes").GetInt64(), total.RawBytes);

        // 与已知的已解释异常比较:只允许 anomaly:zlib(1×1 截断占位帧),其余任何异常都是失败
        long zlibAnomalies = total.Counters.TryGetValue("anomaly:zlib", out long z) ? z : 0L;
        long otherAnomalies = total.Counters.Where(kv => kv.Key.StartsWith("anomaly:", StringComparison.Ordinal) && kv.Key != "anomaly:zlib").Sum(kv => kv.Value);
        r.Expect(otherAnomalies == 0, "出现未解释的异常 " + otherAnomalies + " 个: " + string.Join("; ", total.Examples.Where(e => !e.StartsWith("zlib", StringComparison.Ordinal)).Take(5)));

        r.Metrics["counters"] = new SortedDictionary<string, long>(total.Counters, StringComparer.Ordinal);
        r.Metrics["comp_bytes"] = total.CompBytes;
        r.Metrics["raw_bytes"] = total.RawBytes;
        r.Metrics["explained_placeholder_frames"] = zlibAnomalies;
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    private static void ScanNpk(string path, Accumulator c)
    {
        string npkName = Path.GetFileName(path);
        NpkArchive npk;
        try
        {
            npk = NpkArchive.Open(path);
        }
        catch (DnfFormatException ex)
        {
            c.Anomaly("toc", npkName + " " + ex.Message);
            return;
        }
        using (npk)
        {
            foreach (NpkEntry entry in npk.Entries)
            {
                c.Bump("entries");
                byte[] buf;
                try
                {
                    buf = npk.Read(entry);
                }
                catch (DnfFormatException)
                {
                    c.Anomaly("short-read", entry.Name);
                    continue;
                }
                if (ImgFile.IsLegacy(buf))
                {
                    c.Bump("legacy_img");
                    continue;
                }
                if (!ImgFile.IsV2(buf))
                {
                    c.Bump("unknown_magic");
                    c.Anomaly("magic", entry.Name);
                    continue;
                }
                ImgFile img;
                try
                {
                    img = ImgFile.Parse(buf);
                }
                catch (DnfFormatException ex)
                {
                    c.Anomaly("parse", entry.Name + " " + ex.Message);
                    continue;
                }
                c.Bump("img_v2");
                if (img.ExpectedLength != buf.Length)
                {
                    c.Anomaly("data-size", entry.Name + " expected " + img.ExpectedLength + " actual " + buf.Length);
                }
                c.Bump("frames", img.FrameCount);
                if (img.FrameCount == 0)
                {
                    c.Bump("img_with_0_frames");
                }
                for (int i = 0; i < img.FrameCount; i++)
                {
                    ScanFrame(img, entry.Name, i, c);
                }
            }
        }
    }

    private static void ScanFrame(ImgFile img, string entryName, int i, Accumulator c)
    {
        ImgFrameInfo f = img.GetFrame(i);
        if (f.IsLink)
        {
            c.Bump("frame_link");
            try
            {
                img.ResolveLink(i);
            }
            catch (DnfFormatException)
            {
                c.Anomaly("link", entryName + " frame " + i + " -> " + f.LinkTarget);
            }
            return;
        }
        string typeKey = "0x" + f.Type.ToString("X2");
        c.Bump("frame_type_" + typeKey);
        c.Bump("frame_comp_" + f.Compress);
        c.Bump("type_" + typeKey + "_comp_" + f.Compress);
        int bpp = ImgPixelFormat.BytesPerPixel(f.Type);
        if (bpp == 0)
        {
            c.Anomaly("frame-type", entryName + " frame " + i + " type 0x" + f.Type.ToString("X"));
            return;
        }
        if (f.Width < 0 || f.Height < 0)
        {
            c.Anomaly("negative-size", entryName + " frame " + i);
            return;
        }
        long want = (long)f.Width * f.Height * bpp;
        if (f.Width == 0 || f.Height == 0)
        {
            c.Bump("frame_empty");
        }
        if (f.Compress != 5 && f.Compress != 6)
        {
            c.Anomaly("compress", entryName + " frame " + i + " comp " + f.Compress);
            return;
        }
        byte[] raw;
        try
        {
            raw = img.DecodeRaw(i, out ImgDecodeStatus status, verifyChecksum: true);
            if (status == ImgDecodeStatus.TruncatedPlaceholder)
            {
                c.Anomaly("zlib", entryName + " frame " + i + " truncated 1x1 placeholder");
                return;
            }
        }
        catch (DnfFormatException ex)
        {
            c.Anomaly("zlib", entryName + " frame " + i + " " + ex.Message);
            return;
        }
        c.CompBytes += ImgFile.StoredLength(f);
        c.RawBytes += raw.Length;
        if (raw.Length != want)
        {
            c.Anomaly("raw-len", entryName + " frame " + i + " got " + raw.Length + " want " + want);
        }
        else
        {
            c.Bump("frame_ok");
        }
    }
}
