using System;
using System.Collections.Generic;
using Dnf.Render;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dnf.Client
{
    /// <summary>
    /// GPU 路径与 CPU 合成器(<see cref="RgbaCanvas"/>)的自检素材与比较函数。
    /// 编辑器里的 EditMode 测试与各平台的冒烟播放器共用,所以放在运行时包里。
    /// 合成数据都由固定种子生成,不含任何原版内容。
    /// </summary>
    public static class RenderSelfTest
    {
        public struct Diff
        {
            public int Pixels;
            public int Exact;
            public int Within1;
            public int Over1;
            public int MaxChannelDiff;
            public int FirstBadIndex;

            public bool AllExact => Exact == Pixels;

            public override string ToString()
            {
                return "像素 " + Pixels + ",逐字节相同 " + Exact + ",差 ≤1 " + Within1 + ",差 >1 " + Over1 + ",最大通道差 " + MaxChannelDiff
                    + (Over1 > 0 ? ",第一处 >1 在像素 " + FirstBadIndex : "");
            }
        }

        public static bool GraphicsAvailable => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>逐像素比较两个 RGBA 缓冲:一个像素的四个通道全相同算 Exact;最大通道差 ≤ 1 算 Within1。</summary>
        public static Diff Compare(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                throw new ArgumentException("两个缓冲的长度不同");
            }
            var d = new Diff { Pixels = a.Length / 4, FirstBadIndex = -1 };
            for (int i = 0; i < a.Length; i += 4)
            {
                int m = 0;
                for (int c = 0; c < 4; c++)
                {
                    m = Math.Max(m, Math.Abs(a[i + c] - b[i + c]));
                }
                d.MaxChannelDiff = Math.Max(d.MaxChannelDiff, m);
                if (m == 0)
                {
                    d.Exact++;
                }
                else if (m == 1)
                {
                    d.Within1++;
                }
                else
                {
                    d.Over1++;
                    if (d.FirstBadIndex < 0)
                    {
                        d.FirstBadIndex = i / 4;
                    }
                }
            }
            return d;
        }

        /// <summary>随机帧;位置范围故意超出屏幕四边,检查裁剪。<paramref name="semiTransparent"/> 为假时 alpha 只有 0 与 255。</summary>
        public static List<SceneSprite> RandomSprites(int seed, int count, bool semiTransparent)
        {
            var rng = new System.Random(seed);
            var list = new List<SceneSprite>();
            for (int i = 0; i < count; i++)
            {
                int w = rng.Next(1, 65);
                int h = rng.Next(1, 65);
                var px = new byte[w * h * 4];
                for (int k = 0; k < px.Length; k += 4)
                {
                    px[k] = (byte)rng.Next(256);
                    px[k + 1] = (byte)rng.Next(256);
                    px[k + 2] = (byte)rng.Next(256);
                    px[k + 3] = semiTransparent ? (byte)rng.Next(256) : (rng.Next(4) == 0 ? (byte)0 : (byte)255);
                }
                var frame = new SpriteFrame(w, h, 0, 0, w, h, px);
                list.Add(new SceneSprite(frame, rng.Next(-40, PixelScreen.Width + 10), rng.Next(-40, PixelScreen.Height + 10), i, i, "synthetic", 0));
            }
            return list;
        }

        /// <summary>4×3、每个像素颜色都不同的不透明块,放在四个角与四条边的内外:任何半个像素的偏移都会被发现。</summary>
        public static List<SceneSprite> EdgeSprites()
        {
            var px = new byte[4 * 3 * 4];
            for (int i = 0; i < 12; i++)
            {
                px[i * 4] = (byte)(20 * i + 5);
                px[i * 4 + 1] = (byte)(255 - 20 * i);
                px[i * 4 + 2] = (byte)(7 * i);
                px[i * 4 + 3] = 255;
            }
            var frame = new SpriteFrame(4, 3, 0, 0, 4, 3, px);
            int[] xs = { 0, 796, 0, 796, -2, 798, -2, 798, -4, 800, 400, 400, 399 };
            int[] ys = { 0, 0, 597, 597, -1, -1, 598, 598, 100, 100, -3, 600, 299 };
            var list = new List<SceneSprite>();
            for (int i = 0; i < xs.Length; i++)
            {
                list.Add(new SceneSprite(frame, xs[i], ys[i], i, 0, "synthetic", 0));
            }
            return list;
        }

        /// <summary>256×8:第 x 列的 alpha = x,每行一种颜色。压在纯色底上用来检验半透明混合。</summary>
        public static List<SceneSprite> AlphaSweep(int screenX, int screenY)
        {
            const int w = 256, h = 8;
            int[,] colours = { { 255, 0, 0 }, { 0, 255, 0 }, { 0, 0, 255 }, { 255, 255, 255 }, { 0, 0, 0 }, { 200, 100, 30 }, { 17, 99, 201 }, { 128, 128, 128 } };
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = (byte)colours[y, 0];
                    px[o + 1] = (byte)colours[y, 1];
                    px[o + 2] = (byte)colours[y, 2];
                    px[o + 3] = (byte)x;
                }
            }
            return new List<SceneSprite> { new SceneSprite(new SpriteFrame(w, h, 0, 0, w, h, px), screenX, screenY, 0, 0, "synthetic", 0) };
        }

        public static byte[] RenderCpu(IReadOnlyList<SceneSprite> sprites, Color32 background)
        {
            var canvas = new RgbaCanvas(PixelScreen.Width, PixelScreen.Height);
            canvas.Clear(background.r, background.g, background.b, 255);
            TownScene.Render(canvas, sprites);
            return canvas.Pixels;
        }

        public static byte[] RenderGpu(IReadOnlyList<SceneSprite> sprites, Color32 background)
        {
            SpriteStage stage = SpriteStage.Create(background, "gpu-selftest");
            try
            {
                stage.SetSprites(sprites);
                return stage.Capture();
            }
            finally
            {
                UnityObjects.Destroy(stage.gameObject);
            }
        }
    }
}
