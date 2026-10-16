using System.Diagnostics;
using System.Globalization;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// hero-check:拿一组原版连拍(<c>re/baseline/runs/…/index.tsv</c> 里带时间戳的一串截图),按"起始状态 + 按键时间表"模拟我们的主角与镜头,
/// 在每张截图的时刻把主角合成出来,逐张统计主角自己的像素与截图的吻合率,并在 ±3 像素(--radius 可改,最大 16)范围内搜索最佳偏移。
/// 用来验证朝向镜像、站立/行走动画的帧与计时、行走速度和镜头规则(证据 E-065、E-066)。
/// 起始的站立动画相位未知,先用第一张截图在 0 到 1050 毫秒里扫一遍挑最好的(--phase auto)。
/// 截图与 index.tsv 是用户机器上的录像,只留在本机,不入库。
/// </summary>
internal static class HeroCheck
{
    private sealed class Shot
    {
        public double Seconds;
        public string Name = "";
        public string Path = "";
    }

    private sealed class KeyEvent
    {
        public double Ms;
        public int DirX;
        public int DirY;
    }

    private struct Pose
    {
        public double X, Y, CamX;
        public bool Left;
        public HeroMotion Motion;
        public long MotionMs;
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.IndexFile == null || opts.ShotsDir == null || opts.Prefix == null || opts.Start == null)
        {
            throw new UserError("hero-check 需要 --index <index.tsv> --shots <截图目录> --prefix <截图名前缀> --start x,y,left|right [--keys 毫秒:方向,…] [--phase auto|毫秒]");
        }
        var sw = Stopwatch.StartNew();
        List<Shot> shots = LoadShots(opts.IndexFile, opts.ShotsDir, opts.Prefix);
        if (shots.Count == 0)
        {
            throw new UserError("index.tsv 里没有名字以 " + opts.Prefix + " 开头、且在截图目录里找得到的连拍帧");
        }
        (double startX, double startY, bool startLeft) = ParseStart(opts.Start);
        List<KeyEvent> keys = ParseKeys(opts.Keys);
        (int camMin, int camMax) = ParseCamRange(opts.CamRange);

        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        TownHero hero = SceneCompare.BuildDefaultHero(assets, opts.MapPath, opts.HeroWeapon, startX, startY, startLeft, opts.HeroWait);
        var matcher = new HeroMatcher();
        var rasters = new Raster[shots.Count];
        for (int i = 0; i < shots.Count; i++)
        {
            rasters[i] = PngReader.Load(shots[i].Path);
        }
        double t0 = shots[0].Seconds;
        var times = new double[shots.Count];
        for (int i = 0; i < shots.Count; i++)
        {
            times[i] = (shots[i].Seconds - t0) * 1000.0;
        }

        double phase;
        if (opts.Phase == null || opts.Phase == "auto")
        {
            // 站立动画的起始相位:在第一次按键之前的那些截图上(最多 12 张,均匀取)把吻合率加起来,扫 0 到 1050 毫秒挑最大的
            double firstKey = keys.Count == 0 ? double.MaxValue : keys[0].Ms;
            var usable = new List<int>();
            for (int i = 0; i < shots.Count; i++)
            {
                if (times[i] < firstKey)
                {
                    usable.Add(i);
                }
            }
            if (usable.Count == 0)
            {
                usable.Add(0);
            }
            int stride = Math.Max(1, usable.Count / 12);
            double bestScore = -1;
            phase = 0;
            for (double ph = 0; ph < 1050; ph += 10)
            {
                Pose[] poses = Simulate(hero, startX, startY, startLeft, ph, camMin, camMax, keys, times);
                double sum = 0;
                int n = 0;
                for (int k = 0; k < usable.Count; k += stride)
                {
                    sum += Score(assets, hero, matcher, poses[usable[k]], opts.GroundY, rasters[usable[k]], 0, 0);
                    n++;
                }
                double score = sum / n;
                if (score > bestScore)
                {
                    bestScore = score;
                    phase = ph;
                }
            }
            Console.WriteLine("起始站立相位(用第一次按键之前的截图扫出,步长 10 ms): " + phase + " ms,这些截图的平均主角吻合率 " + (100 * bestScore).ToString("F1") + "%");
        }
        else if (!double.TryParse(opts.Phase, NumberStyles.Float, CultureInfo.InvariantCulture, out phase))
        {
            throw new UserError("--phase 需要 auto 或毫秒数");
        }

        Pose[] states = Simulate(hero, startX, startY, startLeft, phase, camMin, camMax, keys, times);
        Console.WriteLine("按键时间表: " + (keys.Count == 0 ? "(无)" : string.Join(", ", keys.Select(k => k.Ms.ToString("F0") + "ms→" + DirName(k.DirX, k.DirY)))));
        Console.WriteLine("名字            t(ms)  世界x   世界y  镜头x  朝向  动作  动作ms  帧   零偏移吻合  最佳偏移(dx,dy)  最佳吻合  同动作里最合的帧");
        int good = 0, zeroBest = 0, within1 = 0, frameAgree = 0;
        for (int i = 0; i < shots.Count; i++)
        {
            Pose p = states[i];
            hero.Place(p.X, p.Y, p.Left, p.Motion, p.MotionMs);
            double zero = Score(assets, hero, matcher, p, opts.GroundY, rasters[i], 0, 0);
            double best = zero;
            int bdx = 0, bdy = 0;
            for (int dy = -opts.Radius; dy <= opts.Radius; dy++)
            {
                for (int dx = -opts.Radius; dx <= opts.Radius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    double sc = Score(assets, hero, matcher, p, opts.GroundY, rasters[i], dx, dy);
                    if (sc > best + 1e-9)
                    {
                        best = sc;
                        bdx = dx;
                        bdy = dy;
                    }
                }
            }
            (int bestFrame, double bestFrameScore) = BestFrame(assets, hero, matcher, p, opts.GroundY, rasters[i]);
            hero.Place(p.X, p.Y, p.Left, p.Motion, p.MotionMs);
            if (bestFrame == hero.CurrentFrame)
            {
                frameAgree++;
            }
            if (zero >= 0.90)
            {
                good++;
            }
            if (bdx == 0 && bdy == 0)
            {
                zeroBest++;
            }
            if (Math.Abs(bdx) <= 1 && Math.Abs(bdy) <= 1)
            {
                within1++;
            }
            Console.WriteLine(shots[i].Name.PadRight(15) + times[i].ToString("F0").PadLeft(6) + p.X.ToString("F1").PadLeft(8) + p.Y.ToString("F1").PadLeft(8)
                + ((int)Math.Floor(p.CamX)).ToString().PadLeft(7) + (p.Left ? "  左  " : "  右  ") + (p.Motion == HeroMotion.Move ? "行走" : "站立")
                + p.MotionMs.ToString().PadLeft(8) + hero.CurrentFrame.ToString().PadLeft(5) + (100 * zero).ToString("F1").PadLeft(10) + "%"
                + ("(" + bdx + "," + bdy + ")").PadLeft(16) + (100 * best).ToString("F1").PadLeft(10) + "%"
                + ("帧 " + bestFrame + " (" + (100 * bestFrameScore).ToString("F1") + "%)" + (bestFrame == hero.CurrentFrame ? "" : "  ≠模拟")).PadLeft(24));
        }
        Console.WriteLine();
        Console.WriteLine("共 " + shots.Count + " 张:零偏移吻合 ≥90% 的 " + good + " 张;最佳偏移就是 (0,0) 的 " + zeroBest + " 张;最佳偏移在 ±1 像素内的 " + within1
            + " 张;模拟的帧就是最合的帧的 " + frameAgree + " 张");
        Console.Error.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }

    /// <summary>同一个动作(站立或行走)里,逐个帧试一遍(姿态与镜头照模拟的),返回与截图最吻合的帧号及吻合率。</summary>
    private static (int, double) BestFrame(DnfAssets assets, TownHero hero, HeroMatcher matcher, Pose p, int groundY, Raster shot)
    {
        var seen = new HashSet<int>();
        int bestFrame = -1;
        double bestScore = -1;
        for (long ms = 0; ms < 4000; ms += 10)
        {
            hero.Place(p.X, p.Y, p.Left, p.Motion, ms);
            int frame = hero.CurrentFrame;
            if (!seen.Add(frame))
            {
                continue;
            }
            double score = Score(assets, hero, matcher, new Pose { X = p.X, Y = p.Y, CamX = p.CamX, Left = p.Left, Motion = p.Motion, MotionMs = ms }, groundY, shot, 0, 0);
            if (score > bestScore)
            {
                bestScore = score;
                bestFrame = frame;
            }
        }
        return (bestFrame, bestScore);
    }

    private static double Score(DnfAssets assets, TownHero hero, HeroMatcher matcher, Pose p, int groundY, Raster shot, int dx, int dy)
    {
        hero.Place(p.X, p.Y, p.Left, p.Motion, p.MotionMs);
        var projection = new TownProjection((int)Math.Floor(p.CamX) - dx, groundY + dy);
        (int opaque, int exact, _) = matcher.Measure(hero.Sprites(assets, projection), shot);
        return opaque == 0 ? 0 : (double)exact / opaque;
    }

    /// <summary>每 1 毫秒推进一步,记下每张截图时刻的状态。</summary>
    private static Pose[] Simulate(TownHero hero, double x, double y, bool left, double phaseMs, int camMin, int camMax, List<KeyEvent> keys, double[] times)
    {
        hero.Place(x, y, left, HeroMotion.Rest, (long)phaseMs);
        var cam = new TownCamera(camMin, camMax, x);
        var result = new Pose[times.Length];
        int next = 0;
        int dirX = 0, dirY = 0, keyIndex = 0;
        double now = 0;
        double end = times[times.Length - 1];
        const double Step = 1.0;
        while (true)
        {
            while (keyIndex < keys.Count && keys[keyIndex].Ms <= now)
            {
                dirX = keys[keyIndex].DirX;
                dirY = keys[keyIndex].DirY;
                keyIndex++;
            }
            while (next < times.Length && times[next] <= now + 1e-9)
            {
                result[next++] = new Pose { X = hero.X, Y = hero.Y, CamX = cam.X, Left = hero.FacingLeft, Motion = hero.Motion, MotionMs = hero.MotionMs };
            }
            if (next >= times.Length || now > end + 1)
            {
                break;
            }
            hero.Update(Step, dirX, dirY);
            cam.Follow(hero.X);
            now += Step;
        }
        return result;
    }

    private static List<Shot> LoadShots(string indexFile, string shotsDir, string prefix)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(shotsDir, "*.png", SearchOption.AllDirectories))
        {
            byName[Path.GetFileNameWithoutExtension(file)] = file;
        }
        var shots = new List<Shot>();
        foreach (string line in File.ReadLines(indexFile))
        {
            string[] f = line.Split('\t');
            if (f.Length < 4 || f[2] != "burst" || !f[3].StartsWith(prefix, StringComparison.Ordinal) || !byName.TryGetValue(f[3], out string? path))
            {
                continue;
            }
            if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t1)
                || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t2))
            {
                continue;
            }
            shots.Add(new Shot { Seconds = (t1 + t2) / 2.0, Name = f[3], Path = path });
        }
        shots.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        return shots;
    }

    private static (double, double, bool) ParseStart(string text)
    {
        string[] p = text.Split(',');
        if (p.Length != 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) || p[2] is not ("left" or "right"))
        {
            throw new UserError("--start 需要 x,y,left|right(例如 615,234,right)");
        }
        return (x, y, p[2] == "left");
    }

    private static List<KeyEvent> ParseKeys(string? text)
    {
        var list = new List<KeyEvent>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }
        foreach (string item in text.Split(','))
        {
            string[] kv = item.Split(':');
            if (kv.Length != 2 || !double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
            {
                throw new UserError("--keys 的每一项形如 毫秒:方向(left、right、up、down、none,可用 + 组合),例如 444:left,3000:none");
            }
            int dx = 0, dy = 0;
            foreach (string dir in kv[1].Split('+'))
            {
                switch (dir)
                {
                    case "left": dx = -1; break;
                    case "right": dx = 1; break;
                    case "up": dy = -1; break;
                    case "down": dy = 1; break;
                    case "none": break;
                    default: throw new UserError("--keys 里不认识的方向 " + dir);
                }
            }
            list.Add(new KeyEvent { Ms = ms, DirX = dx, DirY = dy });
        }
        list.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        return list;
    }

    private static (int, int) ParseCamRange(string? text)
    {
        if (text == null)
        {
            return (0, 96);
        }
        string[] p = text.Split(',');
        if (p.Length != 2 || !int.TryParse(p[0], out int lo) || !int.TryParse(p[1], out int hi) || hi < lo)
        {
            throw new UserError("--cam-range 需要 最小,最大(例如 0,96)");
        }
        return (lo, hi);
    }

    private static string DirName(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return "无";
        }
        return (dx < 0 ? "左" : dx > 0 ? "右" : "") + (dy < 0 ? "上" : dy > 0 ? "下" : "");
    }
}
