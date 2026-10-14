using System;
using System.Collections.Generic;
using System.IO;
using Dnf.Client;
using Dnf.Render;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace DnfUnity.Tests
{
    /// <summary>
    /// GPU 路径与 CPU 合成器(<see cref="RgbaCanvas"/>)的对照。需要图形设备:带 -nographics 时这些测试被忽略。
    /// 运行方式: UNITY_GRAPHICS=1 tools/unity-test.sh EditMode
    /// 约定(与 PixelScreen、SpriteTextures 一致):工程用 Gamma 色彩空间,精灵点采样,1 个世界单位 = 1 个像素。
    /// 期望:alpha 只有 0 与 255 的像素,GPU 与 CPU 逐字节相同;半透明像素的混合取整方式不同,允许每通道差 1。
    /// </summary>
    [Category("Gpu")]
    public class GpuRenderTests
    {
        private static readonly Color32 Background = new Color32(40, 40, 40, 255);

        private static void RequireGraphics()
        {
            if (!RenderSelfTest.GraphicsAvailable)
            {
                Assert.Ignore("没有图形设备(带了 -nographics),跳过 GPU 对照");
            }
        }

        private static byte[] RenderCpu(IReadOnlyList<SceneSprite> sprites, Color32 bg) => RenderSelfTest.RenderCpu(sprites, bg);
        private static byte[] RenderGpu(IReadOnlyList<SceneSprite> sprites, Color32 bg) => RenderSelfTest.RenderGpu(sprites, bg);

        [Test]
        public void ProjectUsesGammaColorSpace()
        {
            Assert.AreEqual(ColorSpace.Gamma, QualitySettings.activeColorSpace,
                "DNF 在伽马空间里做混合,Unity 工程必须用 Gamma 色彩空间(Project Settings > Player > Color Space)");
        }

        [Test]
        public void EmptyStage_IsTheBackgroundColour()
        {
            RequireGraphics();
            byte[] gpu = RenderGpu(new List<SceneSprite>(), Background);
            byte[] cpu = RenderCpu(new List<SceneSprite>(), Background);
            RenderSelfTest.Diff d = RenderSelfTest.Compare(gpu, cpu);
            Assert.AreEqual(d.Pixels, d.Exact, d.ToString());
        }

        [Test]
        public void OpaqueSprites_GpuEqualsCpu_ByteForByte()
        {
            RequireGraphics();
            foreach (int seed in new[] { 1, 2, 3 })
            {
                List<SceneSprite> sprites = RenderSelfTest.RandomSprites(seed, 40, false);
                RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderGpu(sprites, Background), RenderCpu(sprites, Background));
                Assert.AreEqual(d.Pixels, d.Exact, "seed " + seed + ": " + d);
            }
        }

        [Test]
        public void SpritePlacement_IsPixelExact_AtEveryScreenEdge()
        {
            RequireGraphics();
            List<SceneSprite> sprites = RenderSelfTest.EdgeSprites();
            RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderGpu(sprites, Background), RenderCpu(sprites, Background));
            Assert.AreEqual(d.Pixels, d.Exact, d.ToString());
        }

        [Test]
        public void MirroredSprites_GpuEqualsCpu_ByteForByte()
        {
            RequireGraphics();
            List<SceneSprite> edges = RenderSelfTest.EdgeSprites(mirrored: true);
            RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderGpu(edges, Background), RenderCpu(edges, Background));
            Assert.AreEqual(d.Pixels, d.Exact, "边缘:" + d);
            foreach (int seed in new[] { 5, 6 })
            {
                List<SceneSprite> sprites = RenderSelfTest.RandomSprites(seed, 40, false, mirrorSome: true);
                d = RenderSelfTest.Compare(RenderGpu(sprites, Background), RenderCpu(sprites, Background));
                Assert.AreEqual(d.Pixels, d.Exact, "seed " + seed + ": " + d);
            }
        }

        [Test]
        public void SemiTransparent_SingleLayer_IsWithinOneOfCpu()
        {
            RequireGraphics();
            List<SceneSprite> sprites = RenderSelfTest.AlphaSweep(100, 100);
            foreach (var bg in new[] { new Color32(40, 40, 40, 255), new Color32(200, 90, 13, 255) })
            {
                RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderGpu(sprites, bg), RenderCpu(sprites, bg));
                TestContext.Out.WriteLine("底色 " + bg + ": " + d);
                Assert.LessOrEqual(d.MaxChannelDiff, 1, "底色 " + bg + ": " + d);
            }
        }

        [Test]
        public void SemiTransparent_Overlapping_StaysCloseToCpu()
        {
            RequireGraphics();
            List<SceneSprite> sprites = RenderSelfTest.RandomSprites(11, 30, true);
            RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderGpu(sprites, Background), RenderCpu(sprites, Background));
            TestContext.Out.WriteLine(d.ToString());
            // 多层叠加时两条路径每层的取整可能不同,误差会累积,但不会放大(后一层只乘以 (1-a) < 1)
            Assert.LessOrEqual(d.MaxChannelDiff, 3, d.ToString());
        }

        [Test]
        public void Additive_SingleLayer_AlphaAndTintSweep_IsWithinOneOfCpu()
        {
            RequireGraphics();
            // 加法公式 dst + src × a/255 × A/255:GPU 在浮点里算再量化,CPU 是整数一次取整;单层应当最多差 1
            int worst = 0;
            foreach (int tint in new[] { 1, 77, 139, 157, 200, 255 })
            {
                List<SceneSprite> sprites = RenderSelfTest.AdditiveSweep(100, 100, tint);
                foreach (var bg in new[] { new Color32(40, 40, 40, 255), new Color32(200, 90, 13, 255), new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) })
                {
                    RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderGpu(sprites, bg), RenderCpu(sprites, bg));
                    TestContext.Out.WriteLine("染色 alpha " + tint + ",底色 " + bg + ": " + d);
                    worst = Math.Max(worst, d.MaxChannelDiff);
                    Assert.LessOrEqual(d.MaxChannelDiff, 1, "染色 alpha " + tint + ",底色 " + bg + ": " + d);
                }
            }
            TestContext.Out.WriteLine("最大通道差 " + worst);
        }

        [Test]
        public void Additive_ShaderIsFoundAndAddsOntoTheBackground()
        {
            RequireGraphics();
            // 一个 4×4 的 (100,0,0,255) 块,染色 alpha 255,加到 (40,40,40) 上 → (140,40,40)
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = 100;
                px[i + 3] = 255;
            }
            var frame = new SpriteFrame(4, 4, 0, 0, 4, 4, px);
            var sprites = new List<SceneSprite> { new SceneSprite(frame, 10, 10, 0, 0, "synthetic", 0, false, SpriteBlend.Additive, 255) };
            byte[] gpu = RenderGpu(sprites, Background);
            int o = (11 * PixelScreen.Width + 11) * 4;
            Assert.AreEqual(new[] { 140, 40, 40, 255 }, new[] { (int)gpu[o], gpu[o + 1], gpu[o + 2], gpu[o + 3] });
            int outside = (11 * PixelScreen.Width + 20) * 4;
            Assert.AreEqual(new[] { 40, 40, 40, 255 }, new[] { (int)gpu[outside], gpu[outside + 1], gpu[outside + 2], gpu[outside + 3] });
        }

        [Test]
        public void Additive_RandomMixedSprites_StayCloseToCpu()
        {
            RequireGraphics();
            foreach (int seed in new[] { 21, 22 })
            {
                List<SceneSprite> sprites = RenderSelfTest.RandomSprites(seed, 30, true, mirrorSome: true, additiveSome: true);
                RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderGpu(sprites, Background), RenderCpu(sprites, Background));
                TestContext.Out.WriteLine("seed " + seed + ": " + d);
                // 加法层叠加时每层的取整误差会累积(后一层不缩小前面的误差),所以容差比单层宽
                Assert.LessOrEqual(d.MaxChannelDiff, 4, "seed " + seed + ": " + d);
            }
        }

        private static string DnfDirOrIgnore()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实数据的 GPU 对照");
            }
            return dir;
        }

        private static string RepoOut(string name)
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>把"第一行在上"的 RGBA 缓冲写成 PNG(Unity 的纹理第 0 行在下面,所以先翻转)。</summary>
        private static void WritePng(byte[] topDown, string path)
        {
            int w = PixelScreen.Width, h = PixelScreen.Height, stride = w * 4;
            var bottomUp = new byte[topDown.Length];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(topDown, y * stride, bottomUp, (h - 1 - y) * stride, stride);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            try
            {
                tex.SetPixelData(bottomUp, 0);
                tex.Apply(false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void RealTown_GpuMatchesCpu_AtSeveralTimes()
        {
            RequireGraphics();
            string dir = DnfDirOrIgnore();
            using (DnfAssets assets = DnfAssets.Open(dir))
            using (var view = new TownView(assets, "map/elvengard/(r)gate.map", TownProjection.FromE024, Background))
            {
                Assert.AreEqual(5, view.NpcCount, "网关地图的 [NPC] 列表有 5 个 NPC");
                Assert.AreEqual(5, view.LayerCount, "网关地图的 [animation] 有 5 个图层(其中 3 个是加法光效)");
                // 545 ms 与 1455 ms 是原版截图里光效的相位(证据 E-030),其余是任意时刻
                foreach (long t in new long[] { 0, 150, 545, 1234, 1455, 5000, 60000 })
                {
                    List<SceneSprite> sprites = view.ShowAt(t);
                    byte[] gpu = view.Stage.Capture();
                    byte[] cpu = RenderCpu(sprites, Background);
                    RenderSelfTest.Diff d = RenderSelfTest.Compare(gpu, cpu);
                    TestContext.Out.WriteLine("t=" + t + "ms: 精灵 " + sprites.Count + ";" + d);
                    Assert.LessOrEqual(d.MaxChannelDiff, 1, "t=" + t + ": " + d);
                    if (t == 545)
                    {
                        WritePng(gpu, RepoOut("unity-town-gpu.png"));
                    }
                }
            }
        }
    }
}
