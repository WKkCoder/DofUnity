using System.Diagnostics;
using System.Globalization;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// hero-find:在一张原版截图里找主角站在哪。以 --start x,y,left|right 为中心、--search N(默认 24,最大 80)为半径,
/// 对动作(--motion rest|move,默认 rest;--hero-wait 用战斗待机)的每一个不同的动画帧各取一个时刻,把主角整个摆在中心,
/// 记下主角完全不透明的像素,再在窗口里逐个平移,数这些像素与截图精确一致的个数,列出吻合率最高的几个。
/// 比 hero-check 的 ±16 像素窗口大,而且同时搜位置与动画帧(截图开始前主角站了多久是未知的)。
/// 平移是整数像素,主角各层精灵的摆放只随世界坐标的整数部分平移,所以与逐个重新摆放等价。
/// 截图是用户机器上的录像,只留在本机,不入库(证据 E-070)。
/// </summary>
internal static class HeroFind
{
    private readonly struct Hit
    {
        public readonly int X, Y, Frame;
        public readonly long Phase;
        public readonly int Opaque, Exact;

        public Hit(int x, int y, int frame, long phase, int opaque, int exact)
        {
            X = x;
            Y = y;
            Frame = frame;
            Phase = phase;
            Opaque = opaque;
            Exact = exact;
        }

        public double Ratio => Opaque == 0 ? 0 : (double)Exact / Opaque;
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.Screenshot == null || opts.Start == null)
        {
            throw new UserError("hero-find 需要 --shot <截图.png> --start x,y,left|right [--map <房间地图>] [--cam-x N] [--hero-wait] [--motion rest|move] [--search N] [--top N]");
        }
        var sw = Stopwatch.StartNew();
        string[] p = opts.Start.Split(',');
        if (p.Length != 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double cx)
            || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double cy) || p[2] is not ("left" or "right"))
        {
            throw new UserError("--start 需要 x,y,left|right,例如 172,279,right");
        }
        bool left = p[2] == "left";
        HeroMotion motion = opts.Motion == "move" ? HeroMotion.Move : HeroMotion.Rest;
        int search = opts.Search;
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        Raster shot = PngReader.Load(opts.Screenshot);
        TownHero hero = SceneCompare.BuildDefaultHero(assets, opts.MapPath, opts.HeroWeapon, cx, cy, left, opts.HeroWait);
        var projection = new TownProjection(opts.CameraX, opts.GroundY);
        var onBlack = new RgbaCanvas(HeroMatcher.W, HeroMatcher.H);
        var onWhite = new RgbaCanvas(HeroMatcher.W, HeroMatcher.H);

        var hits = new List<Hit>();
        var seenFrames = new HashSet<int>();
        for (long ms = 0; ms < 4000; ms += 10)
        {
            hero.Place(cx, cy, left, motion, ms);
            int frame = hero.CurrentFrame;
            if (!seenFrames.Add(frame))
            {
                continue;
            }
            List<(int X, int Y, uint Rgb)> pixels = OpaquePixels(assets, hero, projection, onBlack, onWhite);
            if (pixels.Count == 0)
            {
                continue;
            }
            for (int dy = -search; dy <= search; dy++)
            {
                for (int dx = -search; dx <= search; dx++)
                {
                    int exact = 0;
                    foreach ((int x, int y, uint rgb) in pixels)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= shot.Width || sy >= shot.Height)
                        {
                            continue;
                        }
                        if ((shot.Pixels[sy * shot.Width + sx] & 0xFFFFFF) == rgb)
                        {
                            exact++;
                        }
                    }
                    hits.Add(new Hit((int)cx + dx, (int)cy + dy, frame, ms, pixels.Count, exact));
                }
            }
            // 只留吻合率最高的一批,免得列表太大
            hits.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
            if (hits.Count > 2000)
            {
                hits.RemoveRange(1000, hits.Count - 1000);
            }
        }

        hits.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
        Console.WriteLine("截图 " + Path.GetFileName(opts.Screenshot) + ",镜头 x " + opts.CameraX + ",朝向 " + (left ? "左" : "右") + ",动作 " + (motion == HeroMotion.Move ? "行走" : (opts.HeroWait ? "战斗待机" : "站立"))
            + ",以 (" + cx + "," + cy + ") 为中心搜索 ±" + search + " 像素,共试了 " + seenFrames.Count + " 个动画帧");
        Console.WriteLine("世界x  世界y  帧  动作ms  主角不透明像素  精确一致  吻合率");
        for (int i = 0; i < Math.Min(10, hits.Count); i++)
        {
            Hit h = hits[i];
            Console.WriteLine(h.X.ToString().PadLeft(5) + h.Y.ToString().PadLeft(7) + h.Frame.ToString().PadLeft(4) + h.Phase.ToString().PadLeft(8)
                + h.Opaque.ToString().PadLeft(14) + h.Exact.ToString().PadLeft(10) + (100.0 * h.Ratio).ToString("F1").PadLeft(8) + "%");
        }
        Console.Error.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }

    /// <summary>主角精灵分别合成到黑底与白底,两次相同的像素就是完全不透明的像素;返回它们的位置与颜色(0xRRGGBB)。</summary>
    private static List<(int X, int Y, uint Rgb)> OpaquePixels(DnfAssets assets, TownHero hero, TownProjection projection, RgbaCanvas onBlack, RgbaCanvas onWhite)
    {
        IReadOnlyList<SceneSprite> sprites = hero.Sprites(assets, projection);
        onBlack.Clear(0, 0, 0, 255);
        TownScene.Render(onBlack, sprites);
        onWhite.Clear(255, 255, 255, 255);
        TownScene.Render(onWhite, sprites);
        byte[] a = onBlack.Pixels;
        byte[] b = onWhite.Pixels;
        var result = new List<(int, int, uint)>();
        for (int y = 0; y < HeroMatcher.H; y++)
        {
            for (int x = 0; x < HeroMatcher.W; x++)
            {
                int o = (y * HeroMatcher.W + x) * 4;
                if (a[o] == b[o] && a[o + 1] == b[o + 1] && a[o + 2] == b[o + 2])
                {
                    result.Add((x, y, (uint)((a[o] << 16) | (a[o + 1] << 8) | a[o + 2])));
                }
            }
        }
        return result;
    }
}
