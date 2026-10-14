using System.Diagnostics;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// render-town:用 Dnf.Render 的 CPU 合成器画出地图里的 NPC,可选地与一张原版截图逐像素对照。
/// 这是 tools/py/town_probe.py 的 C# 版:同一条链、同一个模型,数字应当一致(证据 E-024)。
/// </summary>
internal static class TownRender
{
    public static int Run(DnfLocation loc, Options opts)
    {
        var sw = Stopwatch.StartNew();
        Console.Error.WriteLine("建立 NPK 索引……");
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        Console.Error.WriteLine("索引完成:" + assets.Images.NpkCount + " 个 NPK," + assets.Images.ImageCount + " 个 IMG 名字(重名 "
            + assets.Images.DuplicateNames + "),用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");

        List<TownNpc> npcs = TownScene.ReadMapNpcs(assets, opts.MapPath);
        var projection = new TownProjection(opts.CameraX, opts.GroundY);
        List<TownScene.ResolvedNpc> resolved = TownScene.Resolve(assets, npcs);
        Console.WriteLine("地图 " + opts.MapPath + ":[NPC] " + npcs.Count + " 个,能解析出动画的 " + resolved.Count + " 个");

        Raster? shot = opts.Screenshot == null ? null : PngReader.Load(opts.Screenshot);

        // 选每个 NPC 要画的帧:默认按时间;--best-frame 则在它的全部帧里选与截图精确一致像素最多的
        var chosen = new List<SceneSprite>();
        Console.WriteLine("NPC   ani帧  峰值偏移  峰值精确  邻居最大  不透明  预测左上角   动画");
        foreach (TownScene.ResolvedNpc r in resolved)
        {
            SceneSprite? best = null;
            int bestExact = -1;
            int bestNeighbor = 0;
            int bestOpaque = 0;
            (int dx, int dy) bestOffset = (0, 0);
            IEnumerable<int> frames = opts.BestFrame && shot != null
                ? Enumerable.Range(0, r.Ani.Frames.Length)
                : new[] { new AniTimeline(r.Ani).FrameAt(opts.TimeMs) };
            foreach (int k in frames)
            {
                SceneSprite? s = TownScene.SpriteAt(assets, r, k, projection);
                if (s == null)
                {
                    continue;
                }
                if (shot == null)
                {
                    best = s;
                    break;
                }
                var grid = new Dictionary<(int, int), (int total, int exact)>();
                for (int dy = -opts.Radius; dy <= opts.Radius; dy++)
                {
                    for (int dx = -opts.Radius; dx <= opts.Radius; dx++)
                    {
                        grid[(dx, dy)] = ExactCount(shot, s.Frame, s.ScreenX + dx, s.ScreenY + dy);
                    }
                }
                KeyValuePair<(int, int), (int total, int exact)> peak = grid.OrderByDescending(kv => kv.Value.exact).First();
                if (peak.Value.exact > bestExact)
                {
                    bestExact = peak.Value.exact;
                    best = s;
                    bestOffset = peak.Key;
                    bestOpaque = peak.Value.total;
                    bestNeighbor = grid.Where(kv => kv.Key != peak.Key).Max(kv => kv.Value.exact);
                }
            }
            if (best == null)
            {
                Console.WriteLine(r.Npc.Id.ToString().PadRight(6) + "没有可画的帧");
                continue;
            }
            chosen.Add(best);
            if (shot != null)
            {
                Console.WriteLine(r.Npc.Id.ToString().PadRight(6) + best.AniFrame.ToString().PadRight(7)
                    + ("(" + bestOffset.dx.ToString("+0;-0;+0") + "," + bestOffset.dy.ToString("+0;-0;+0") + ")").PadRight(10)
                    + bestExact.ToString().PadRight(10) + bestNeighbor.ToString().PadRight(10) + bestOpaque.ToString().PadRight(8)
                    + ("(" + best.ScreenX + "," + best.ScreenY + ")").PadRight(13) + best.AnimationPath);
            }
            else
            {
                Console.WriteLine(r.Npc.Id.ToString().PadRight(6) + best.AniFrame.ToString().PadRight(7) + "-         -         -         -       "
                    + ("(" + best.ScreenX + "," + best.ScreenY + ")").PadRight(13) + best.AnimationPath);
            }
        }

        if (opts.PngFile != null)
        {
            List<SceneSprite> ordered = chosen.OrderBy(s => s.SortKey).ToList();
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, ordered);
            if (shot == null)
            {
                PngWriter.Write(opts.PngFile, canvas.Width, canvas.Height, canvas.Pixels);
            }
            else
            {
                PngWriter.Write(opts.PngFile, 2400, 600, SideBySide(canvas, shot, ordered));
            }
            Console.WriteLine("已写 " + opts.PngFile);
        }
        return 0;
    }

    /// <summary>在 (x0,y0) 处数不透明像素有多少个与截图完全相同。返回 (不透明且在屏幕内的像素数, 其中一致的个数)。</summary>
    internal static (int total, int exact) ExactCount(Raster shot, SpriteFrame f, int x0, int y0)
    {
        int total = 0, exact = 0;
        for (int y = 0; y < f.Height; y++)
        {
            int sy = y0 + y;
            if (sy < 0 || sy >= shot.Height)
            {
                continue;
            }
            for (int x = 0; x < f.Width; x++)
            {
                int sx = x0 + x;
                if (sx < 0 || sx >= shot.Width)
                {
                    continue;
                }
                int o = (y * f.Width + x) * 4;
                if (f.Rgba[o + 3] != 255)
                {
                    continue;
                }
                total++;
                uint want = ((uint)f.Rgba[o] << 16) | ((uint)f.Rgba[o + 1] << 8) | f.Rgba[o + 2];
                if ((shot.Pixels[sy * shot.Width + sx] & 0xFFFFFFu) == want)
                {
                    exact++;
                }
            }
        }
        return (total, exact);
    }

    /// <summary>左:我们的合成;中:原版截图;右:对照(绿 = 我们的不透明像素与截图相同,红 = 不同,暗 = 没有精灵)。</summary>
    private static byte[] SideBySide(RgbaCanvas ours, Raster shot, List<SceneSprite> sprites)
    {
        const int W = 800, H = 600;
        var outBuf = new byte[3 * W * H * 4];
        var mask = new byte[W * H * 4];
        for (int i = 0; i < mask.Length; i += 4)
        {
            mask[i] = mask[i + 1] = mask[i + 2] = 30;
            mask[i + 3] = 255;
        }
        foreach (SceneSprite s in sprites)
        {
            SpriteFrame f = s.Frame;
            for (int y = 0; y < f.Height; y++)
            {
                for (int x = 0; x < f.Width; x++)
                {
                    int sx = s.ScreenX + x, sy = s.ScreenY + y;
                    if (sx < 0 || sy < 0 || sx >= W || sy >= H)
                    {
                        continue;
                    }
                    int o = (y * f.Width + x) * 4;
                    if (f.Rgba[o + 3] != 255)
                    {
                        continue;
                    }
                    uint want = ((uint)f.Rgba[o] << 16) | ((uint)f.Rgba[o + 1] << 8) | f.Rgba[o + 2];
                    bool same = (shot.Pixels[sy * shot.Width + sx] & 0xFFFFFFu) == want;
                    int m = (sy * W + sx) * 4;
                    mask[m] = (byte)(same ? 40 : 230);
                    mask[m + 1] = (byte)(same ? 220 : 40);
                    mask[m + 2] = 40;
                }
            }
        }
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int dst = (y * 3 * W + x) * 4;
                Buffer.BlockCopy(ours.Pixels, (y * W + x) * 4, outBuf, dst, 4);
                uint c = shot.Pixels[y * shot.Width + x];
                int mid = dst + W * 4;
                outBuf[mid] = (byte)(c >> 16);
                outBuf[mid + 1] = (byte)(c >> 8);
                outBuf[mid + 2] = (byte)c;
                outBuf[mid + 3] = 255;
                Buffer.BlockCopy(mask, (y * W + x) * 4, outBuf, dst + 2 * W * 4, 4);
            }
        }
        return outBuf;
    }
}
