using System.Diagnostics;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// render-town --scene:用 <see cref="TownScene.BuildScene"/> 合成整个场景(地图图层 + NPC),与原版截图逐像素对照。
/// 截图里还有 HUD、聊天文字、主角与名牌,这些我们没有画,所以总体吻合率不会是 100%;
/// 网格表按 100×100 的格子给出吻合率,用来看哪些区域是场景本身、哪些是界面。
/// </summary>
internal static class SceneCompare
{
    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.Screenshot == null && opts.PngFile == null)
        {
            throw new UserError("--scene 需要 --shot <截图.png>(与原版截图对照),或者 --png <输出.png>(只输出我们的 800×600 合成结果)");
        }
        var sw = Stopwatch.StartNew();
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        Raster? shotOrNull = opts.Screenshot == null ? null : PngReader.Load(opts.Screenshot);
        var projection = new TownProjection(opts.CameraX, opts.GroundY);
        List<SceneSprite> sprites = TownScene.BuildScene(assets, opts.MapPath, projection, opts.TimeMs);
        Console.WriteLine("地图 " + opts.MapPath + ",时间 " + opts.TimeMs + " ms:绘制列表 " + sprites.Count + " 个精灵");
        foreach (SceneSprite s in sprites)
        {
            string who = s.NpcId >= 0 ? "NPC " + s.NpcId : "图层";
            Console.WriteLine("  [" + s.LayerRank + "] " + who.PadRight(8) + s.AnimationPath.PadRight(46) + "帧 " + s.AniFrame + "  "
                + (s.Blend == SpriteBlend.Additive ? "加法 A=" + s.TintAlpha : "普通").PadRight(12) + " 左上角(" + s.ScreenX + "," + s.ScreenY + ")");
        }

        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);

        const int W = 800, H = 600;
        if (shotOrNull == null)
        {
            // 没有原版截图:只输出合成结果,用作 Unity 播放器渲染的 CPU 参照
            PngWriter.Write(opts.PngFile!, W, H, canvas.Pixels);
            Console.WriteLine("已写 " + opts.PngFile + "(我们的 800×600 合成结果,背景 40,40,40)");
            return 0;
        }
        Raster shot = shotOrNull;
        int exact = 0, near = 0, total = W * H;
        const int Cell = 100;
        int cols = W / Cell, rows = H / Cell;
        var cellExact = new int[cols, rows];
        var mask = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                uint c = shot.Pixels[y * shot.Width + x];
                int dr = Math.Abs(canvas.Pixels[o] - (int)((c >> 16) & 0xFF));
                int dg = Math.Abs(canvas.Pixels[o + 1] - (int)((c >> 8) & 0xFF));
                int db = Math.Abs(canvas.Pixels[o + 2] - (int)(c & 0xFF));
                int d = Math.Max(dr, Math.Max(dg, db));
                bool isExact = d == 0;
                bool isNear = d <= 2;
                if (isExact)
                {
                    exact++;
                    cellExact[x / Cell, y / Cell]++;
                }
                if (isNear)
                {
                    near++;
                }
                mask[o] = (byte)(isExact ? 40 : isNear ? 230 : 230);
                mask[o + 1] = (byte)(isExact ? 220 : isNear ? 200 : 40);
                mask[o + 2] = 40;
                mask[o + 3] = 255;
            }
        }
        Console.WriteLine();
        Console.WriteLine("整屏:精确一致 " + exact + "/" + total + " (" + (100.0 * exact / total).ToString("F1") + "%),每通道差 ≤2 的 "
            + near + " (" + (100.0 * near / total).ToString("F1") + "%)");
        Console.WriteLine("按 100×100 的格子,精确一致的百分比(行 = y,列 = x):");
        Console.Write("      ");
        for (int cx = 0; cx < cols; cx++)
        {
            Console.Write((cx * Cell).ToString().PadLeft(5));
        }
        Console.WriteLine();
        for (int cy = 0; cy < rows; cy++)
        {
            Console.Write((cy * Cell).ToString().PadLeft(5) + " ");
            for (int cx = 0; cx < cols; cx++)
            {
                Console.Write((100.0 * cellExact[cx, cy] / (Cell * Cell)).ToString("F0").PadLeft(5));
            }
            Console.WriteLine();
        }

        foreach ((int x0, int y0, int x1, int y1) in opts.EvalRects)
        {
            int n = 0, e = 0, nr = 0;
            for (int y = Math.Max(0, y0); y < Math.Min(H, y1); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
                {
                    int o = (y * W + x) * 4;
                    uint c = shot.Pixels[y * shot.Width + x];
                    int d = Math.Max(Math.Abs(canvas.Pixels[o] - (int)((c >> 16) & 0xFF)),
                        Math.Max(Math.Abs(canvas.Pixels[o + 1] - (int)((c >> 8) & 0xFF)), Math.Abs(canvas.Pixels[o + 2] - (int)(c & 0xFF))));
                    n++;
                    if (d == 0)
                    {
                        e++;
                    }
                    if (d <= 2)
                    {
                        nr++;
                    }
                }
            }
            Console.WriteLine("区域 [" + x0 + "," + y0 + ")-[" + x1 + "," + y1 + "):精确一致 " + e + "/" + n + " (" + (100.0 * e / Math.Max(1, n)).ToString("F2")
                + "%),差 ≤2 的 " + nr + " (" + (100.0 * nr / Math.Max(1, n)).ToString("F2") + "%)");
        }

        if (opts.PngFile != null)
        {
            var outBuf = new byte[3 * W * H * 4];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int dst = (y * 3 * W + x) * 4;
                    int src = (y * W + x) * 4;
                    Buffer.BlockCopy(canvas.Pixels, src, outBuf, dst, 4);
                    uint c = shot.Pixels[y * shot.Width + x];
                    int mid = dst + W * 4;
                    outBuf[mid] = (byte)(c >> 16);
                    outBuf[mid + 1] = (byte)(c >> 8);
                    outBuf[mid + 2] = (byte)c;
                    outBuf[mid + 3] = 255;
                    Buffer.BlockCopy(mask, src, outBuf, dst + 2 * W * 4, 4);
                }
            }
            PngWriter.Write(opts.PngFile, 3 * W, H, outBuf);
            Console.WriteLine("已写 " + opts.PngFile + "(左=我们 中=原版 右=精确一致为绿、差 ≤2 为黄、其余为红)");
        }
        Console.Error.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }
}
