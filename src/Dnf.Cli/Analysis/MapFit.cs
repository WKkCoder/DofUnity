using System.Diagnostics;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// fit-map:对地图 [animation] 里的每个动画、每一帧,在原版截图里搜索它的像素块最可能出现的位置(逐通道容差内一致的像素最多),
/// 并与"按 NPC 的位置模型算出的预测位置"比较。只用不透明像素;半透明的光效层只给预测位置,不参与搜索。
/// 这是实验工具:结果是观察,位置模型是否成立由后续证据判断(证据 E-030)。
/// </summary>
internal static class MapFit
{
    private sealed class Peak
    {
        public int X, Y, Ok, Inb;
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.Screenshot == null)
        {
            throw new UserError("fit-map 需要 --shot <截图.png>");
        }
        var sw = Stopwatch.StartNew();
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        Raster shot = PngReader.Load(opts.Screenshot);
        List<MapAnimation> anims = MapScript.ReadAnimations(assets, opts.MapPath);
        var projection = new TownProjection(opts.CameraX, opts.GroundY);
        Console.WriteLine("地图 " + opts.MapPath + ":[animation] " + anims.Count + " 项;搜索范围 ±" + opts.RangeX + "×±" + opts.RangeY
            + ",容差 " + opts.FitTolerance + ",抽样 " + opts.Samples);

        var compare = new List<(SpriteFrame Frame, int X, int Y)>();
        foreach (MapAnimation a in anims)
        {
            AniFile ani = assets.LoadAni(a.AnimationPath);
            Console.WriteLine();
            Console.WriteLine(a.Layer.PadRight(14) + a.RawPath + "  世界(" + a.X + "," + a.Y + "," + a.Z + ")  " + (ani == null ? "动画缺失" : ani.Frames.Length + " 帧"));
            if (ani == null)
            {
                continue;
            }
            for (int k = 0; k < ani.Frames.Length; k++)
            {
                AniFrame f = ani.Frames[k];
                if (f.ImageIndex < 0 || f.ImageIndex >= ani.ImagePaths.Length)
                {
                    continue;
                }
                string img = "sprite/" + ani.ImagePaths[f.ImageIndex].Replace('\\', '/');
                if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite))
                {
                    Console.WriteLine("  帧 " + k + ": 缺图 " + img + "#" + f.FrameIndex);
                    continue;
                }
                projection.TopLeft(a.X, a.Y, a.Z, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, out int px, out int py);
                string head = "  帧 " + k + " " + sprite.Width + "x" + sprite.Height + " 预测左上角(" + px + "," + py + ")";
                string tint = f.TryGetTint(out byte tr, out byte tg, out byte tb, out byte ta) ? " 染色(" + tr + "," + tg + "," + tb + "," + ta + ")" : "";
                string eff = f.TryGetGraphicEffect(out int effect) ? " 效果" + effect : "";
                List<Peak> peaks = Search(shot, sprite, px, py, opts.RangeX, opts.RangeY, opts.FitTolerance, opts.Samples, out int sampleCount);
                if (peaks.Count == 0)
                {
                    Console.WriteLine(head + tint + eff + " 不透明像素太少(" + sampleCount + "),不搜索");
                    continue;
                }
                Peak best = peaks[0];
                (int total, int exact, int near) = Count(shot, sprite, best.X, best.Y, opts.FitTolerance);
                (int totalP, int exactP, int nearP) = Count(shot, sprite, px, py, opts.FitTolerance);
                Console.WriteLine(head + tint + eff);
                Console.WriteLine("      最佳(" + best.X + "," + best.Y + ") 偏离预测(" + (best.X - px).ToString("+0;-0;+0") + "," + (best.Y - py).ToString("+0;-0;+0")
                    + ") 抽样 " + best.Ok + "/" + best.Inb + " 全量:屏内不透明 " + total + ",精确 " + exact + " (" + Pct(exact, total) + "),容差内 " + near + " (" + Pct(near, total) + ")");
                Console.WriteLine("      在预测位置:屏内不透明 " + totalP + ",精确 " + exactP + " (" + Pct(exactP, totalP) + "),容差内 " + nearP + " (" + Pct(nearP, totalP) + ")");
                for (int p = 1; p < peaks.Count; p++)
                {
                    Console.WriteLine("      次峰(" + peaks[p].X + "," + peaks[p].Y + ") 抽样 " + peaks[p].Ok + "/" + peaks[p].Inb);
                }
                if (k == 0)
                {
                    compare.Add((sprite, best.X, best.Y));
                }
            }
        }

        if (opts.PngFile != null && compare.Count > 0)
        {
            WriteCompare(opts.PngFile, shot, compare, opts.FitTolerance);
            Console.WriteLine();
            Console.WriteLine("已写 " + opts.PngFile + "(左=把各层第 0 帧按最佳位置依次叠上 中=原版 右=容差内一致的像素显示为绿)");
        }
        Console.Error.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }

    /// <summary>
    /// fit-sprite:在截图里搜索某一张 IMG 帧的位置(与 fit-map 同一套搜索,只是不经过地图脚本)。
    /// 用来量地面瓦片、远景/中景和门这些不在 [animation] 里的东西;只用不透明像素,至少 500 个。
    /// </summary>
    public static int RunSprite(DnfLocation loc, Options opts)
    {
        if (opts.Screenshot == null || opts.Img == null)
        {
            throw new UserError("fit-sprite 需要 --shot <截图.png> 和 --img <IMG 路径>");
        }
        int predX = 0, predY = 0;
        if (opts.Pred != null)
        {
            string[] parts = opts.Pred.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out predX) || !int.TryParse(parts[1], out predY))
            {
                throw new UserError("--pred 需要 x,y 两个整数");
            }
        }
        var sw = Stopwatch.StartNew();
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        Raster shot = PngReader.Load(opts.Screenshot);
        int last = opts.FrameTo >= opts.Frame ? opts.FrameTo : opts.Frame;
        var scanHits = new List<(int Exact, string Line)>();
        for (int frameNo = opts.Frame; frameNo <= last; frameNo++)
        {
            if (!assets.TryGetFrame(opts.Img, frameNo, out SpriteFrame sprite))
            {
                if (frameNo == opts.Frame)
                {
                    throw new UserError("找不到 IMG 帧 " + opts.Img + "#" + frameNo);
                }
                break;
            }
            if (opts.Mirror == "on")
            {
                // --mirror on:把这一帧左右翻转后再找(怪物朝右时画的是翻转的图)
                var flipped = new byte[sprite.Rgba.Length];
                for (int y = 0; y < sprite.Height; y++)
                {
                    for (int x = 0; x < sprite.Width; x++)
                    {
                        Array.Copy(sprite.Rgba, (y * sprite.Width + x) * 4, flipped, (y * sprite.Width + (sprite.Width - 1 - x)) * 4, 4);
                    }
                }
                sprite = new SpriteFrame(sprite.Width, sprite.Height, sprite.OffsetX, sprite.OffsetY, sprite.CanvasWidth, sprite.CanvasHeight, flipped);
            }
            bool scan = last > opts.Frame;
            if (!scan)
            {
                Console.WriteLine(opts.Img + "#" + frameNo + " " + sprite.Width + "x" + sprite.Height + " 帧头偏移(" + sprite.OffsetX + "," + sprite.OffsetY
                    + ")  以(" + predX + "," + predY + ")为中心搜索 ±" + opts.RangeX + "×±" + opts.RangeY + ",容差 " + opts.FitTolerance + ",抽样 " + opts.Samples
                    + (opts.Mirror == "on" ? ",左右翻转" : string.Empty));
            }
            List<Peak> peaks = Search(shot, sprite, predX, predY, opts.RangeX, opts.RangeY, opts.FitTolerance, opts.Samples, out int sampleCount);
            if (peaks.Count == 0)
            {
                if (!scan)
                {
                    Console.WriteLine("不透明像素太少(" + sampleCount + "),不搜索");
                }
                continue;
            }
            foreach (Peak p in peaks)
            {
                (int total, int exact, int near) = Count(shot, sprite, p.X, p.Y, opts.FitTolerance);
                if (scan && (total < 300 || exact * 100 < total * opts.ScanMinPercent))
                {
                    continue;
                }
                string line = (scan ? "  帧 " + frameNo + " " + sprite.Width + "x" + sprite.Height + " " : string.Empty) + "  峰(" + p.X + "," + p.Y + ") 抽样 " + p.Ok + "/" + p.Inb
                    + " 全量:屏内不透明 " + total + ",精确 " + exact + " (" + Pct(exact, total) + "),容差内 " + near + " (" + Pct(near, total) + ")";
                if (scan)
                {
                    scanHits.Add((exact, line));
                }
                else
                {
                    Console.WriteLine(line);
                }
            }
        }
        if (last > opts.Frame)
        {
            scanHits.Sort((a, b) => b.Exact.CompareTo(a.Exact));
            for (int i = 0; i < scanHits.Count && i < opts.ScanTop; i++)
            {
                Console.WriteLine(scanHits[i].Line);
            }
        }
        Console.Error.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }

    private static string Pct(int a, int b) => b == 0 ? "-" : (100.0 * a / b).ToString("F1") + "%";

    private static bool Close(uint shotPixel, byte r, byte g, byte b, int tol)
    {
        return Math.Abs((int)((shotPixel >> 16) & 0xFF) - r) <= tol
            && Math.Abs((int)((shotPixel >> 8) & 0xFF) - g) <= tol
            && Math.Abs((int)(shotPixel & 0xFF) - b) <= tol;
    }

    /// <summary>在 (x0,y0) 处统计:屏内的不透明像素数、精确相同的个数、容差内相同的个数。</summary>
    private static (int total, int exact, int near) Count(Raster shot, SpriteFrame f, int x0, int y0, int tol)
    {
        int total = 0, exact = 0, near = 0;
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
                uint s = shot.Pixels[sy * shot.Width + sx];
                if (Close(s, f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2], 0))
                {
                    exact++;
                }
                if (Close(s, f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2], tol))
                {
                    near++;
                }
            }
        }
        return (total, exact, near);
    }

    private static List<Peak> Search(Raster shot, SpriteFrame f, int predX, int predY, int rangeX, int rangeY, int tol, int maxSamples, out int sampleCount)
    {
        int total = f.Width * f.Height;
        var opaque = new List<int>();
        for (int i = 0; i < total; i++)
        {
            if (f.Rgba[i * 4 + 3] == 255)
            {
                opaque.Add(i);
            }
        }
        sampleCount = opaque.Count;
        var result = new List<Peak>();
        if (opaque.Count < 500)
        {
            return result;
        }
        int step = Math.Max(1, opaque.Count / maxSamples);
        var sx = new List<int>();
        var sy = new List<int>();
        var sr = new List<byte>();
        var sg = new List<byte>();
        var sb = new List<byte>();
        for (int n = 0; n < opaque.Count; n += step)
        {
            int i = opaque[n];
            sx.Add(i % f.Width);
            sy.Add(i / f.Width);
            sr.Add(f.Rgba[i * 4]);
            sg.Add(f.Rgba[i * 4 + 1]);
            sb.Add(f.Rgba[i * 4 + 2]);
        }
        sampleCount = sx.Count;
        int ns = sx.Count;
        int[] xs = sx.ToArray(), ys = sy.ToArray();
        byte[] rs = sr.ToArray(), gs = sg.ToArray(), bs = sb.ToArray();
        int minInb = ns / 2;
        int w = shot.Width, h = shot.Height;
        uint[] px = shot.Pixels;

        int rows = 2 * rangeY + 1;
        var bestPerRow = new Peak?[rows];
        Parallel.For(0, rows, row =>
        {
            int oy = predY - rangeY + row;
            Peak? rowBest = null;
            for (int ox = predX - rangeX; ox <= predX + rangeX; ox++)
            {
                int inb = 0, ok = 0;
                for (int k = 0; k < ns; k++)
                {
                    int X = xs[k] + ox, Y = ys[k] + oy;
                    if ((uint)X >= (uint)w || (uint)Y >= (uint)h)
                    {
                        continue;
                    }
                    inb++;
                    uint s = px[Y * w + X];
                    if (Math.Abs((int)((s >> 16) & 0xFF) - rs[k]) <= tol && Math.Abs((int)((s >> 8) & 0xFF) - gs[k]) <= tol && Math.Abs((int)(s & 0xFF) - bs[k]) <= tol)
                    {
                        ok++;
                    }
                }
                if (inb >= minInb && (rowBest == null || ok > rowBest.Ok))
                {
                    rowBest = new Peak { X = ox, Y = oy, Ok = ok, Inb = inb };
                }
            }
            bestPerRow[row] = rowBest;
        });

        // 取分数最高、且彼此相距 ≥ 4 像素的前 3 个峰
        foreach (Peak p in bestPerRow.OfType<Peak>().OrderByDescending(p => p.Ok).ThenBy(p => Math.Abs(p.X - predX) + Math.Abs(p.Y - predY)))
        {
            if (result.All(q => Math.Abs(q.X - p.X) >= 4 || Math.Abs(q.Y - p.Y) >= 4))
            {
                result.Add(p);
                if (result.Count >= 3)
                {
                    break;
                }
            }
        }
        return result;
    }

    private static void WriteCompare(string path, Raster shot, List<(SpriteFrame Frame, int X, int Y)> layers, int tol)
    {
        const int W = 800, H = 600;
        var ours = new RgbaCanvas(W, H);
        ours.Clear(40, 40, 40, 255);
        foreach ((SpriteFrame frame, int x, int y) in layers)
        {
            ours.Draw(frame, x, y);
        }
        var outBuf = new byte[3 * W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int dst = (y * 3 * W + x) * 4;
                int src = (y * W + x) * 4;
                Buffer.BlockCopy(ours.Pixels, src, outBuf, dst, 4);
                uint c = shot.Pixels[y * shot.Width + x];
                int mid = dst + W * 4;
                outBuf[mid] = (byte)(c >> 16);
                outBuf[mid + 1] = (byte)(c >> 8);
                outBuf[mid + 2] = (byte)c;
                outBuf[mid + 3] = 255;
                int right = dst + 2 * W * 4;
                bool covered = ours.Pixels[src] != 40 || ours.Pixels[src + 1] != 40 || ours.Pixels[src + 2] != 40;
                bool same = covered && Close(c, ours.Pixels[src], ours.Pixels[src + 1], ours.Pixels[src + 2], tol);
                outBuf[right] = (byte)(same ? 40 : covered ? 230 : 30);
                outBuf[right + 1] = (byte)(same ? 220 : covered ? 40 : 30);
                outBuf[right + 2] = (byte)(same ? 40 : covered ? 40 : 30);
                outBuf[right + 3] = 255;
            }
        }
        PngWriter.Write(path, 3 * W, H, outBuf);
    }
}
