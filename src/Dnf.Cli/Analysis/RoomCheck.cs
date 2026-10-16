using System.Globalization;
using System.Diagnostics;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// room-check:对一组原版连拍,逐张量出副本房间的镜头 x(证据 E-067、E-068)。
/// 做法:把房间的背景、瓦片、图层动画与门(不含主角、怪物、界面)按候选镜头 x 合成,在没有界面的区域(x 小于 730、y 在 30 到 330 之间)
/// 数与截图精确一致的像素,取最多的镜头 x(先隔 8 像素粗扫,再在 ±8 像素里细扫)。
/// 主角与怪物会盖住少数像素,不影响取最大值;视差背景让镜头 x 在整数像素上可分辨。截图只在本机,不入库。
/// </summary>
internal static class RoomCheck
{
    private const int W = 800, H = 600;
    private const int RegionX1 = 730, RegionY0 = 30, RegionY1 = 330;

    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.ShotsDir == null || opts.Prefix == null)
        {
            throw new UserError("room-check 需要 --shots <截图目录> --prefix <截图名前缀> [--index <index.tsv>] --map <房间地图> [--gate 门…]");
        }
        var sw = Stopwatch.StartNew();
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        RoomMap room = RoomMap.Read(assets, opts.MapPath);
        RoomContent content = RoomContent.Load(assets, room, SceneCompare.BuildGates(assets, room, opts));

        var files = new List<(string Name, string Path, double Seconds)>();
        Dictionary<string, double> times = LoadTimes(opts.IndexFile);
        foreach (string file in Directory.EnumerateFiles(opts.ShotsDir, "*.png", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith(opts.Prefix, StringComparison.Ordinal))
            {
                files.Add((name, file, times.TryGetValue(name, out double s) ? s : double.NaN));
            }
        }
        if (files.Count == 0)
        {
            throw new UserError("截图目录里没有名字以 " + opts.Prefix + " 开头的 png");
        }
        double t0 = files.Select(f => f.Seconds).Where(s => !double.IsNaN(s)).DefaultIfEmpty(0).Min();

        Console.WriteLine("房间 " + opts.MapPath + ":宽 " + room.Width + ",镜头 x 范围 0.." + room.MaxCameraX + "。比对区域 x∈[0," + RegionX1 + ") y∈[" + RegionY0 + "," + RegionY1 + ")");
        Console.WriteLine("名字              t(ms)  镜头x  吻合区域精确一致");
        var canvas = new RgbaCanvas(W, H);
        foreach ((string name, string path, double seconds) in files)
        {
            Raster shot = PngReader.Load(path);
            int maxCam = room.MaxCameraX;
            int best = 0;
            double bestScore = -1;
            for (int cam = 0; cam <= maxCam; cam += 8)
            {
                double sc = Score(assets, content, canvas, shot, cam, opts);
                if (sc > bestScore)
                {
                    bestScore = sc;
                    best = cam;
                }
            }
            int lo = Math.Max(0, best - 8), hi = Math.Min(maxCam, best + 8);
            for (int cam = lo; cam <= hi; cam++)
            {
                double sc = Score(assets, content, canvas, shot, cam, opts);
                if (sc > bestScore)
                {
                    bestScore = sc;
                    best = cam;
                }
            }
            string t = double.IsNaN(seconds) ? "-" : ((seconds - t0) * 1000.0).ToString("F0", CultureInfo.InvariantCulture);
            Console.WriteLine(name.PadRight(18) + t.PadLeft(6) + best.ToString().PadLeft(7) + (100 * bestScore).ToString("F1").PadLeft(10) + "%");
        }
        Console.Error.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }

    private static double Score(DnfAssets assets, RoomContent content, RgbaCanvas canvas, Raster shot, int cam, Options opts)
    {
        List<SceneSprite> sprites = RoomScene.Build(assets, content, cam, opts.GroundY, opts.TimeMs, null);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);
        byte[] p = canvas.Pixels;
        int exact = 0, total = 0;
        for (int y = RegionY0; y < RegionY1; y++)
        {
            for (int x = 0; x < RegionX1; x++)
            {
                int o = (y * W + x) * 4;
                uint c = shot.Pixels[y * shot.Width + x];
                total++;
                if (p[o] == (byte)(c >> 16) && p[o + 1] == (byte)(c >> 8) && p[o + 2] == (byte)c)
                {
                    exact++;
                }
            }
        }
        return (double)exact / total;
    }

    /// <summary>读 index.tsv:连拍帧名 → 两次时间戳的平均值(秒)。</summary>
    private static Dictionary<string, double> LoadTimes(string? indexFile)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (indexFile == null)
        {
            return map;
        }
        foreach (string line in File.ReadLines(indexFile))
        {
            string[] f = line.Split('\t');
            if (f.Length < 4 || f[2] != "burst")
            {
                continue;
            }
            if (double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t1)
                && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t2))
            {
                map[f[3]] = (t1 + t2) / 2.0;
            }
        }
        return map;
    }
}
