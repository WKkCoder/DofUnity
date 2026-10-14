using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dnf.Data;

namespace Dnf.Cli.Analysis;

/// <summary>
/// "反向识别画面里的精灵":拿一张原版客户端的截图,在全部 IMG 帧里找出"不透明像素与截图逐像素相同"的帧,
/// 并给出它在截图里的位置。
///
/// 为什么只看不透明像素(alpha=255):D3D 的常规混合下,不透明像素直接写入目标,没有混色;
/// 半透明、染色(RGBA)、缩放、叠加类特效的像素不会逐像素相同,所以它们不在这个工具的范围里。
/// 找到匹配 = 同时验证了 (1) 我们的像素解码与原版渲染一致,(2) 这个帧确实被画在了那个位置。
///
/// 做法:先把截图的颜色建成哈希表;每个 IMG 帧抽样 64 个不透明像素,先看这些颜色在截图里存不存在
/// (大多数帧在这一步被淘汰),再用"在截图里最罕见的颜色"当锚点,枚举锚点出现的位置去逐像素验证。
/// </summary>
internal static class SpriteLocator
{
    private const int SampleCount = 64;

    /// <summary>颜色的查表键:容差为 0 时是原色;容差 > 0 时每个通道量化到 5 位(步长 8)。</summary>
    private static uint Key(uint c, int tol)
    {
        if (tol == 0)
        {
            return c;
        }
        return ((c >> 19) & 31u) << 10 | ((c >> 11) & 31u) << 5 | ((c >> 3) & 31u);
    }

    /// <summary>两个 0x00RRGGBB 颜色在每个通道上的差都不超过 tol。</summary>
    private static bool Close(uint a, uint b, int tol)
    {
        if (a == b)
        {
            return true;
        }
        if (tol == 0)
        {
            return false;
        }
        return Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255)) <= tol
            && Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) <= tol
            && Math.Abs((int)(a & 255) - (int)(b & 255)) <= tol;
    }

    internal sealed record Match(string Npk, string Entry, int Frame, int Width, int Height, int Opaque, int InBounds, int X, int Y, double Ratio, int DistinctColors);

    public static int Run(DnfLocation loc, Options opts)
    {
        if (string.IsNullOrEmpty(opts.Screenshot))
        {
            throw new UserError("locate 需要 --shot <截图.png>");
        }
        Raster shot = PngReader.Load(opts.Screenshot);
        Console.Error.WriteLine("截图 " + shot.Width + "x" + shot.Height + ",共 " + shot.Pixels.Length + " 像素");

        var counts = new Dictionary<uint, int>();
        foreach (uint p0 in shot.Pixels)
        {
            uint p = Key(p0, opts.Tolerance);
            counts[p] = counts.TryGetValue(p, out int c) ? c + 1 : 1;
        }
        // 只给不太常见的颜色建位置表,作为锚点
        var positions = new Dictionary<uint, List<int>>();
        for (int i = 0; i < shot.Pixels.Length; i++)
        {
            uint p = Key(shot.Pixels[i], opts.Tolerance);
            if (counts[p] <= opts.MaxAnchor)
            {
                if (!positions.TryGetValue(p, out List<int>? list))
                {
                    list = new List<int>();
                    positions[p] = list;
                }
                list.Add(i);
            }
        }
        Console.Error.WriteLine("截图里有 " + counts.Count + " 种颜色,其中 " + positions.Count + " 种出现不超过 " + opts.MaxAnchor + " 次(可作锚点)");

        string[] npks = Directory.GetFiles(loc.NpkDir, "*.*")
            .Where(p => p.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
            .Where(p => string.IsNullOrEmpty(opts.NpkFilter) || Path.GetFileName(p).Contains(opts.NpkFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Console.Error.WriteLine("要扫描的 NPK: " + npks.Length + " 个");

        var matches = new ConcurrentBag<Match>();
        long framesScanned = 0, framesPassedColorGate = 0;
        var sw = Stopwatch.StartNew();
        int done = 0;
        Parallel.ForEach(npks, new ParallelOptions { MaxDegreeOfParallelism = opts.Threads }, path =>
        {
            string npkName = Path.GetFileName(path);
            using NpkArchive npk = NpkArchive.Open(path);
            long scanned = 0, passed = 0;
            foreach (NpkEntry entry in npk.Entries)
            {
                byte[] bytes = npk.Read(entry);
                if (!ImgFile.IsV2(bytes))
                {
                    continue;
                }
                ImgFile img;
                try
                {
                    img = ImgFile.Parse(bytes);
                }
                catch (DnfFormatException)
                {
                    continue;
                }
                for (int f = 0; f < img.FrameCount; f++)
                {
                    ImgFrameInfo info = img.GetFrame(f);
                    if (info.IsLink || info.Width <= 0 || info.Height <= 0 || info.Width > opts.MaxDim || info.Height > opts.MaxDim
                        || (long)info.Width * info.Height < opts.MinOpaque)
                    {
                        continue;
                    }
                    byte[] rgba;
                    try
                    {
                        rgba = img.DecodeRgba(f);
                    }
                    catch (DnfFormatException)
                    {
                        continue;
                    }
                    scanned++;
                    if (TryMatch(shot, counts, positions, rgba, info, opts, out Match? m, npkName, entry.Name, f, ref passed) && m != null)
                    {
                        matches.Add(m);
                    }
                }
            }
            Interlocked.Add(ref framesScanned, scanned);
            Interlocked.Add(ref framesPassedColorGate, passed);
            int d = Interlocked.Increment(ref done);
            if (d % 100 == 0 || d == npks.Length)
            {
                Console.Error.WriteLine("  进度 " + d + "/" + npks.Length + " 个 NPK,已扫描 " + Interlocked.Read(ref framesScanned)
                    + " 帧,匹配 " + matches.Count + " 个,用时 " + sw.Elapsed.TotalSeconds.ToString("F0") + "s");
            }
        });

        // 同一位置、同一像素内容的多个匹配(不同 IMG 里的相同帧)按"不透明像素数多、比例高"排在前面
        Match[] ranked = matches
            .OrderByDescending(m => m.Ratio >= 0.999 ? 1 : 0)
            .ThenByDescending(m => m.InBounds)
            .ThenByDescending(m => m.Ratio)
            .ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.JsonFile ?? Path.Combine(loc.OutDir, "locate.json")))!);
        string outPath = opts.JsonFile ?? Path.Combine(loc.OutDir, "locate.json");
        using (FileStream fs = File.Create(outPath))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("screenshot", Path.GetFileName(opts.Screenshot));
            w.WriteNumber("shot_width", shot.Width);
            w.WriteNumber("shot_height", shot.Height);
            w.WriteNumber("npk_count", npks.Length);
            w.WriteNumber("frames_scanned", framesScanned);
            w.WriteNumber("frames_passed_color_gate", framesPassedColorGate);
            w.WriteNumber("min_opaque", opts.MinOpaque);
            w.WriteNumber("min_ratio", opts.MinRatio);
            w.WriteNumber("tolerance", opts.Tolerance);
            w.WriteNumber("match_count", ranked.Length);
            w.WriteNumber("seconds", Math.Round(sw.Elapsed.TotalSeconds, 1));
            w.WritePropertyName("matches");
            w.WriteStartArray();
            foreach (Match m in ranked)
            {
                w.WriteStartObject();
                w.WriteString("npk", m.Npk);
                w.WriteString("entry", m.Entry);
                w.WriteNumber("frame", m.Frame);
                w.WriteNumber("w", m.Width);
                w.WriteNumber("h", m.Height);
                w.WriteNumber("opaque", m.Opaque);
                w.WriteNumber("in_bounds_opaque", m.InBounds);
                w.WriteNumber("x", m.X);
                w.WriteNumber("y", m.Y);
                w.WriteNumber("ratio", Math.Round(m.Ratio, 4));
                w.WriteNumber("distinct_colors", m.DistinctColors);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        Console.WriteLine("扫描 " + framesScanned + " 帧,通过颜色初筛 " + framesPassedColorGate + " 帧,匹配 " + ranked.Length + " 个;用时 "
            + sw.Elapsed.TotalSeconds.ToString("F0") + "s;结果写入 " + outPath);
        int shown = 0;
        foreach (Match m in ranked)
        {
            Console.WriteLine("  " + m.Npk + " | " + m.Entry + " #" + m.Frame + " " + m.Width + "x" + m.Height + " @(" + m.X + "," + m.Y + ") 不透明 "
                + m.InBounds + "/" + m.Opaque + " 一致比例 " + m.Ratio.ToString("P1") + " 颜色数 " + m.DistinctColors);
            if (++shown >= opts.Top)
            {
                break;
            }
        }
        return 0;
    }

    private static bool TryMatch(Raster shot, Dictionary<uint, int> counts, Dictionary<uint, List<int>> positions, byte[] rgba, ImgFrameInfo info,
        Options opts, out Match? match, string npk, string entry, int frame, ref long passedGate)
    {
        match = null;
        int w = info.Width, h = info.Height;
        int total = w * h;

        // 1. 数不透明像素,并等距抽样
        int opaque = 0;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] == 255)
            {
                opaque++;
            }
        }
        if (opaque < opts.MinOpaque)
        {
            return false;
        }
        int step = Math.Max(1, opaque / SampleCount);
        Span<int> sampleIdx = stackalloc int[SampleCount + 1];
        Span<uint> sampleColor = stackalloc uint[SampleCount + 1];
        int ns = 0, seen = 0;
        for (int i = 0; i < total && ns < SampleCount; i++)
        {
            if (rgba[i * 4 + 3] != 255)
            {
                continue;
            }
            if (seen % step == 0)
            {
                sampleIdx[ns] = i;
                sampleColor[ns] = Pack(rgba, i);
                ns++;
            }
            seen++;
        }
        if (ns < 8)
        {
            return false;
        }

        // 2. 颜色初筛:抽样颜色里至少 60% 要在截图里出现过
        int hit = 0;
        for (int k = 0; k < ns; k++)
        {
            if (counts.ContainsKey(Key(sampleColor[k], opts.Tolerance)))
            {
                hit++;
            }
        }
        if (hit * 10 < ns * 6)
        {
            return false;
        }
        passedGate++;

        // 3. 锚点:取最罕见的三种可作锚点的颜色
        Span<int> anchorK = stackalloc int[3];
        int na = 0;
        for (int round = 0; round < 3; round++)
        {
            int bestK = -1, bestCount = int.MaxValue;
            for (int k = 0; k < ns; k++)
            {
                if (!positions.ContainsKey(Key(sampleColor[k], opts.Tolerance)))
                {
                    continue;
                }
                bool used = false;
                for (int q = 0; q < na; q++)
                {
                    if (anchorK[q] == k || Key(sampleColor[anchorK[q]], opts.Tolerance) == Key(sampleColor[k], opts.Tolerance))
                    {
                        used = true;
                    }
                }
                if (used)
                {
                    continue;
                }
                int c = counts[Key(sampleColor[k], opts.Tolerance)];
                if (c < bestCount)
                {
                    bestCount = c;
                    bestK = k;
                }
            }
            if (bestK < 0)
            {
                break;
            }
            anchorK[na++] = bestK;
        }
        if (na == 0)
        {
            return false;
        }

        // 4. 枚举锚点位置,先用抽样像素快速验证,再对最好的位置做全量验证
        int bestOx = int.MinValue, bestOy = 0;
        double bestSampleRatio = 0;
        int sw = shot.Width, sh = shot.Height;
        var tried = new HashSet<long>();
        for (int a = 0; a < na; a++)
        {
            int ak = anchorK[a];
            int ax = sampleIdx[ak] % w, ay = sampleIdx[ak] / w;
            foreach (int pos in positions[Key(sampleColor[ak], opts.Tolerance)])
            {
                int ox = pos % sw - ax, oy = pos / sw - ay;
                if (!tried.Add(((long)ox << 32) ^ (uint)oy))
                {
                    continue;
                }
                int inb = 0, ok = 0;
                for (int k = 0; k < ns; k++)
                {
                    int px = sampleIdx[k] % w + ox, py = sampleIdx[k] / w + oy;
                    if (px < 0 || py < 0 || px >= sw || py >= sh)
                    {
                        continue;
                    }
                    inb++;
                    if (Close(shot.Pixels[py * sw + px], sampleColor[k], opts.Tolerance))
                    {
                        ok++;
                    }
                }
                if (inb >= 8)
                {
                    double ratio = (double)ok / inb;
                    if (ratio > bestSampleRatio)
                    {
                        bestSampleRatio = ratio;
                        bestOx = ox;
                        bestOy = oy;
                    }
                }
            }
        }
        if (bestOx == int.MinValue || bestSampleRatio < 0.6)
        {
            return false;
        }

        // 5. 全量验证
        int fullIn = 0, fullOk = 0;
        var distinct = new HashSet<uint>();
        for (int i = 0; i < total; i++)
        {
            if (rgba[i * 4 + 3] != 255)
            {
                continue;
            }
            int px = i % w + bestOx, py = i / w + bestOy;
            if (px < 0 || py < 0 || px >= sw || py >= sh)
            {
                continue;
            }
            fullIn++;
            uint c = Pack(rgba, i);
            if (Close(shot.Pixels[py * sw + px], c, opts.Tolerance))
            {
                fullOk++;
                if (distinct.Count < 64)
                {
                    distinct.Add(c);
                }
            }
        }
        if (fullIn < opts.MinOpaque || fullOk < opts.MinOpaque)
        {
            return false;
        }
        double fullRatio = (double)fullOk / fullIn;
        if (fullRatio < opts.MinRatio || distinct.Count < 12)
        {
            return false;
        }
        match = new Match(npk, entry, frame, w, h, opaque, fullIn, bestOx, bestOy, fullRatio, distinct.Count);
        return true;
    }

    private static uint Pack(byte[] rgba, int pixelIndex)
    {
        int o = pixelIndex * 4;
        return ((uint)rgba[o] << 16) | ((uint)rgba[o + 1] << 8) | rgba[o + 2];
    }
}
