using System.Text.Json;
using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf act-motion-check [--json &lt;文件&gt;]`:读全库 .act 的 [MOTION],统计叠加层([SUB ANI]、[SUB ANI WITH XY]、[SUB ANI WITH XYZ])与声音([SOUND]、[SOUND PROBABILITY])的结构、
/// 与主动画帧数的对齐关系,并用 <see cref="ActMotionPlayer"/> 在每个有事件登记的 [MOTION] 上整套推一遍(逐帧、跳帧补报、收口、循环重装),
/// 统计事件个数与顺序摘要。--json 写出全部计数(扁平的 键 → 个数 与 摘要表),与独立实现 `python3 tools/py/act_motion_ref.py compare` 逐项比对。
/// 只输出数字与键名,不输出脚本里的路径或声音标签;输出文件放 out/,不入库。
/// </summary>
internal static class ActMotionCheck
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private sealed class Tally
    {
        public readonly SortedDictionary<string, long> Stats = new(StringComparer.Ordinal);

        public void Add(string key, long n = 1)
        {
            Stats.TryGetValue(key, out long c);
            Stats[key] = c + n;
        }
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));
        var t = new Tally();
        var soundKeys = new HashSet<string>(StringComparer.Ordinal);
        var aniFrames = new Dictionary<string, int>(StringComparer.Ordinal);
        ulong orderDigest = FnvOffset, passA = FnvOffset, passB = FnvOffset, endDigest = FnvOffset, loopDigest = FnvOffset;

        int FramesOf(string actPath, string relative)
        {
            string path = PvfPath.ResolveRelativeTo(actPath, relative);
            if (aniFrames.TryGetValue(path, out int cached))
            {
                return cached;
            }
            int frames = -1;
            if (pvf.TryGetEntry(path, out PvfEntry entry))
            {
                try
                {
                    frames = AniFile.Parse(pvf.Read(entry)).Frames.Length;
                }
                catch (DnfFormatException)
                {
                    frames = -1;
                }
            }
            aniFrames[path] = frames;
            return frames;
        }

        foreach (PvfEntry e in pvf.Entries)
        {
            if (!e.Path.EndsWith(".act", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            byte[] raw = pvf.Read(e);
            if (!TokenScript.IsTokenScript(raw))
            {
                t.Add("notTokens");
                continue;
            }
            ActProgram program = ActProgram.Compile(e.Path, ActParser.Parse(raw, strings));
            t.Add("files");
            string top = TopDirectory(e.Path);
            t.Add("dir." + top + ".files");
            if (program.Triggers.Count > 0)
            {
                t.Add("withTriggers");
            }
            if (program.Motions.Count == 0)
            {
                continue;
            }
            t.Add("withMotion");
            t.Add("dir." + top + ".withMotion");
            if (program.Motions.Count > 1)
            {
                t.Add("multiMotion");
            }
            ActMotion m = program.Motions[0];
            Tabulate(t, soundKeys, m, program.Triggers.Count > 0, top);
            Align(t, m, e.Path, FramesOf);
            if (m.HasEvents)
            {
                orderDigest = MixOrder(orderDigest, m);
                Simulate(t, m, ref passA, ref passB, ref endDigest, ref loopDigest);
            }
        }

        t.Add("soundKey.distinct", soundKeys.Count);
        var digests = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["order"] = orderDigest.ToString(),
            ["passA"] = passA.ToString(),
            ["passB"] = passB.ToString(),
            ["end"] = endDigest.ToString(),
            ["loop"] = loopDigest.ToString(),
        };

        foreach (KeyValuePair<string, long> kv in t.Stats)
        {
            Console.WriteLine(kv.Key + " " + kv.Value);
        }
        foreach (KeyValuePair<string, string> kv in digests)
        {
            Console.WriteLine("digest." + kv.Key + " " + kv.Value);
        }
        if (opts.JsonFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.JsonFile))!);
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(new { stats = t.Stats, digests }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    /// <summary>路径的第一级目录(小写);没有目录的归 "(root)"。</summary>
    private static string TopDirectory(string path)
    {
        int slash = path.IndexOf('/');
        return slash <= 0 ? "(root)" : path.Substring(0, slash).ToLowerInvariant();
    }

    /// <summary>个数分桶:0 到 <paramref name="cap"/> - 1 各一桶,不小于 <paramref name="cap"/> 的归 "cap+"。</summary>
    private static string Bucket(int n, int cap) => n >= cap ? cap + "+" : n.ToString();

    private static void Tabulate(Tally t, HashSet<string> soundKeys, ActMotion m, bool hasTriggers, string top)
    {
        t.Add("layersPerMotion." + Bucket(m.Layers.Count, 9));
        t.Add("soundsPerMotion." + Bucket(m.Sounds.Count, 9));
        if (m.MalformedEntries > 0)
        {
            t.Add("malformedFiles");
            t.Add("malformed", m.MalformedEntries);
        }
        bool hasLayers = m.Layers.Count > 0;
        bool hasSounds = m.Sounds.Count > 0;
        if (hasLayers)
        {
            t.Add("withLayers");
            t.Add("dir." + top + ".withLayers");
        }
        if (hasSounds)
        {
            t.Add("withSounds");
            t.Add("dir." + top + ".withSounds");
        }
        if (hasLayers && hasSounds)
        {
            t.Add("withBoth");
        }
        if (m.HasEvents)
        {
            t.Add(hasTriggers ? "events.withTriggers" : "events.noTriggers");
            t.Add("dir." + top + (hasTriggers ? ".eventsWithTriggers" : ".eventsNoTriggers"));
        }
        foreach (ActOverlayLayer l in m.Layers)
        {
            t.Add("layers");
            t.Add("layers." + (l.Form == ActOverlayForm.Plain ? "plain" : l.Form == ActOverlayForm.WithXY ? "xy" : "xyz"));
            t.Add("startFrame." + (l.StartFrame < 0 ? "neg" : l.StartFrame >= 10 ? "10+" : l.StartFrame.ToString()));
            t.Add("depth." + (l.Depth < 0 ? "neg" : l.Depth == 0 ? "zero" : "pos"));
        }
        if (m.Layers.Count >= 2)
        {
            bool asc = true, desc = true, equal = true;
            for (int i = 1; i < m.Layers.Count; i++)
            {
                int a = m.Layers[i - 1].Depth, b = m.Layers[i].Depth;
                asc &= a <= b;
                desc &= a >= b;
                equal &= a == b;
            }
            t.Add("depthOrder." + (equal ? "equal" : asc ? "asc" : desc ? "desc" : "mixed"));
        }
        foreach (ActSoundEvent s in m.Sounds)
        {
            t.Add("sounds");
            t.Add(s.Probabilistic ? "sounds.prob" : "sounds.plain");
            t.Add("soundFrame." + (s.Frame < 0 ? "neg" : s.Frame >= 10 ? "10+" : s.Frame.ToString()));
            if (s.Probabilistic)
            {
                t.Add("percent." + (s.Percent < 0 || s.Percent > 100 ? "other" : s.Percent == 0 ? "0" : s.Percent == 100 ? "100" : "1-99"));
            }
            soundKeys.Add(s.Key);
            if (s.Key.IndexOfAny(new[] { '/', '\\', '.' }) >= 0)
            {
                t.Add("soundKey.pathLike");
            }
        }
        if (m.Sounds.Count >= 2)
        {
            bool sorted = true;
            bool sameFrame = false;
            var seen = new HashSet<int>();
            for (int i = 0; i < m.Sounds.Count; i++)
            {
                if (i > 0 && m.Sounds[i - 1].Frame > m.Sounds[i].Frame)
                {
                    sorted = false;
                }
                if (!seen.Add(m.Sounds[i].Frame))
                {
                    sameFrame = true;
                }
            }
            t.Add("soundOrder." + (sorted ? "sorted" : "unsorted"));
            t.Add("soundSameFrame." + (sameFrame ? "yes" : "no"));
        }
    }

    /// <summary>叠加层起始帧、声音帧与主动画帧数的关系:主动画读不到(没写、路径解析不到、.ani 读不出)的另记。</summary>
    private static void Align(Tally t, ActMotion m, string actPath, Func<string, string, int> framesOf)
    {
        if (!m.HasEvents)
        {
            return;
        }
        if (string.IsNullOrEmpty(m.BaseAni))
        {
            t.Add("align.noBase");
            return;
        }
        int n = framesOf(actPath, m.BaseAni);
        if (n < 0)
        {
            t.Add("align.baseUnreadable");
            return;
        }
        t.Add("align.baseReadable");
        foreach (ActOverlayLayer l in m.Layers)
        {
            t.Add(l.StartFrame < n ? "align.layerInRange" : "align.layerOutOfRange");
        }
        foreach (ActSoundEvent s in m.Sounds)
        {
            t.Add(s.Frame >= 0 && s.Frame < n ? "align.soundInRange" : "align.soundOutOfRange");
        }
    }

    private static ulong Mix(ulong h, long v)
    {
        unchecked
        {
            return (h ^ (uint)v) * FnvPrime;
        }
    }

    private static ulong MixOrder(ulong h, ActMotion m)
    {
        h = Mix(h, m.Layers.Count);
        foreach (ActOverlayLayer l in m.CompositeOrder)
        {
            h = Mix(h, l.Index);
        }
        return h;
    }

    private static ulong MixEvents(ulong h, List<ActMotionEvent> events)
    {
        foreach (ActMotionEvent e in events)
        {
            h = Mix(h, (int)e.Code);
            h = Mix(h, e.Frame);
            h = Mix(h, e.Code == ActMotionEventCode.Sound ? e.Sound.Index : e.Layer.Index);
        }
        return h;
    }

    /// <summary>
    /// 整套推一遍:帧 0 到 M-1(M = 登记的最大起始帧与声音帧加一,至少 1)逐帧 → pass A;跳帧(0、3、6……,最后补到 M-1)→ pass B(补报,序列应与 A 完全一样);
    /// A 之后收口 → end;再从帧 0 推一遍(循环绕圈,声音重装、叠加层不重起)→ loop。
    /// </summary>
    private static void Simulate(Tally t, ActMotion m, ref ulong passA, ref ulong passB, ref ulong endDigest, ref ulong loopDigest)
    {
        int last = 0;
        foreach (ActOverlayLayer l in m.Layers)
        {
            last = Math.Max(last, l.StartFrame);
        }
        foreach (ActSoundEvent s in m.Sounds)
        {
            last = Math.Max(last, s.Frame);
        }
        int frames = last + 1;
        t.Add("sim.motions");

        var player = new ActMotionPlayer(m);
        var events = new List<ActMotionEvent>();
        for (int f = 0; f < frames; f++)
        {
            player.Advance(f, events);
        }
        CountEvents(t, "sim.passA", events);
        passA = MixEvents(passA, events);

        events.Clear();
        player.End(events);
        CountEvents(t, "sim.end", events);
        endDigest = MixEvents(endDigest, events);

        events.Clear();
        player.Begin();
        for (int f = 0; f < frames; f += 3)
        {
            player.Advance(f, events);
        }
        player.Advance(frames - 1, events);
        CountEvents(t, "sim.passB", events);
        passB = MixEvents(passB, events);

        // 循环绕圈:帧号从末尾回到 0,声音重新装填,叠加层已经起的不再起
        events.Clear();
        player.Advance(0, events);
        for (int f = 1; f < frames; f++)
        {
            player.Advance(f, events);
        }
        CountEvents(t, "sim.loop", events);
        loopDigest = MixEvents(loopDigest, events);
    }

    private static void CountEvents(Tally t, string prefix, List<ActMotionEvent> events)
    {
        foreach (ActMotionEvent e in events)
        {
            t.Add(prefix + ".events");
            t.Add(prefix + (e.Code == ActMotionEventCode.LayerStart ? ".starts" : e.Code == ActMotionEventCode.LayerEnd ? ".ends" : ".sounds"));
        }
    }
}
