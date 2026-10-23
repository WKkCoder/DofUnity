using System;
using System.IO;
using Dnf.Client;
using Dnf.Data;
using Dnf.Render;
using Dnf.Play;
using NUnit.Framework;
using UnityEngine;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 汉字文字系统在 Unity 上的一半(M29,证据 E-121):系统字体经 TextCore 栅格化成字形位图,再交给渲染层排版。
    /// 不依赖显卡与原版数据;这台机器上没有带汉字的系统字体时,需要字体的测试跳过(其余照跑)。
    /// 文字只用公开的标准测试串与本项目自己写的界面词,不含原版内容。
    /// </summary>
    [Category("Text")]
    public class GlyphFontTests
    {
        private static UnityGlyphFonts _fonts;
        private static string _note;

        [OneTimeSetUp]
        public void OpenFonts()
        {
            _fonts = UnityGlyphFonts.TryCreate(null, out _note);
        }

        [OneTimeTearDown]
        public void CloseFonts()
        {
            _fonts?.Dispose();
            _fonts = null;
        }

        private static UnityGlyphFonts Fonts()
        {
            if (_fonts == null)
            {
                Assert.Ignore("这台机器上没有带汉字的系统字体:" + _note);
            }
            return _fonts;
        }

        [Test]
        public void TheRequestedFontIsTriedFirstAndEveryDesktopPlatformHasCandidates()
        {
            Assert.AreEqual("/tmp/x.ttc#1", UnityGlyphFonts.Candidates("  /tmp/x.ttc#1 ", RuntimePlatform.OSXPlayer)[0]);
            Assert.AreEqual(0, UnityGlyphFonts.Candidates(null, RuntimePlatform.IPhonePlayer).Count);
            foreach (RuntimePlatform p in new[] { RuntimePlatform.OSXPlayer, RuntimePlatform.OSXEditor, RuntimePlatform.WindowsPlayer, RuntimePlatform.WindowsEditor, RuntimePlatform.Android, RuntimePlatform.LinuxPlayer })
            {
                Assert.Greater(UnityGlyphFonts.Candidates(null, p).Count, 2, p.ToString());
            }
        }

        [Test]
        public void ACandidateMayCarryAFaceIndex()
        {
            Assert.IsTrue(UnityGlyphFonts.TrySplit("/a/b.ttc#2", out string path, out int face));
            Assert.AreEqual(("/a/b.ttc", 2), (path, face));
            Assert.IsTrue(UnityGlyphFonts.TrySplit("/a/b.ttf", out path, out face));
            Assert.AreEqual(("/a/b.ttf", 0), (path, face));
            Assert.IsTrue(UnityGlyphFonts.TrySplit("/a/b.ttc#x", out path, out face));
            Assert.AreEqual(("/a/b.ttc#x", 0), (path, face));
            Assert.IsFalse(UnityGlyphFonts.TrySplit("  ", out _, out _));
            Assert.IsFalse(UnityGlyphFonts.TrySplit(null, out _, out _));
        }

        [Test]
        public void AFontPathThatDoesNotExistFindsNothingUnlessAnotherCandidateWorks()
        {
            UnityGlyphFonts fonts = UnityGlyphFonts.TryCreate("/no/such/dir/missing.ttf", out string note);
            try
            {
                Assert.IsNotNull(note);
                Assert.AreEqual(_fonts != null, fonts != null);
            }
            finally
            {
                fonts?.Dispose();
            }
        }

        [Test]
        public void ChineseGlyphsHaveInkInTheirBitmapAndSensibleMetrics()
        {
            UnityGlyphFonts fonts = Fonts();
            foreach (int size in new[] { 12, 16, 20, 32 })
            {
                IGlyphSource source = fonts.ForSize(size);
                Assert.AreEqual(size, source.PixelSize);
                foreach (char c in "中文背包")
                {
                    Assert.IsTrue(source.TryGetGlyph(c, out GlyphBitmap g), "size " + size + " 缺字 " + c);
                    Assert.Greater(g.Width, 0);
                    Assert.Greater(g.Height, 0);
                    Assert.LessOrEqual(g.Width, size * 3 / 2, "宽 size " + size);
                    Assert.LessOrEqual(g.Height, size * 3 / 2, "高 size " + size);
                    Assert.GreaterOrEqual(g.Advance, size * 7 / 10);
                    Assert.LessOrEqual(g.Advance, size * 3 / 2);
                    AssertInkTouchesEveryEdge(g, c + "@" + size);
                    Assert.Greater(g.BearingY, 0);
                    Assert.LessOrEqual(g.BearingY, source.Ascent + 2);
                }
            }
        }

        /// <summary>位图应当紧贴墨迹:离四条边各一像素之内都有墨(笔画被切掉说明图集里取错了位置;字体的度量框允许比墨迹大出一圈的抗锯齿边)。</summary>
        private static void AssertInkTouchesEveryEdge(GlyphBitmap g, string what)
        {
            bool top = false, bottom = false, left = false, right = false;
            for (int x = 0; x < g.Width; x++)
            {
                for (int y = 0; y < Math.Min(2, g.Height); y++)
                {
                    top |= g.Alpha[y * g.Width + x] > 0;
                    bottom |= g.Alpha[(g.Height - 1 - y) * g.Width + x] > 0;
                }
            }
            for (int y = 0; y < g.Height; y++)
            {
                for (int x = 0; x < Math.Min(2, g.Width); x++)
                {
                    left |= g.Alpha[y * g.Width + x] > 0;
                    right |= g.Alpha[y * g.Width + g.Width - 1 - x] > 0;
                }
            }
            Assert.IsTrue(top && bottom && left && right, what + " 位图应当紧贴墨迹(四边一像素内各有墨)");
        }

        private static int Ink(GlyphBitmap g, int row)
        {
            int n = 0;
            for (int x = 0; x < g.Width; x++)
            {
                if (g.Alpha[row * g.Width + x] > 64)
                {
                    n++;
                }
            }
            return n;
        }

        [Test]
        public void BitmapsAreUpright()
        {
            IGlyphSource source = Fonts().ForSize(24);
            Assert.IsTrue(source.TryGetGlyph('T', out GlyphBitmap t));
            Assert.Greater(Ink(t, 0), Ink(t, t.Height - 1) * 2, "T 的横杠应在位图上方");
            Assert.IsTrue(source.TryGetGlyph('L', out GlyphBitmap l));
            Assert.Greater(Ink(l, l.Height - 1), Ink(l, 0) * 2, "L 的横杠应在位图下方");
        }

        [Test]
        public void SpaceIsBlankWithAnAdvanceAndOtherCodePointsAreMissing()
        {
            IGlyphSource source = Fonts().ForSize(16);
            Assert.IsTrue(source.TryGetGlyph(' ', out GlyphBitmap space));
            Assert.AreEqual(0, space.Width);
            Assert.AreEqual(0, space.Height);
            Assert.Greater(space.Advance, 0);
            Assert.IsFalse(source.TryGetGlyph(0x0A, out _));
            Assert.IsFalse(source.TryGetGlyph(-1, out _));
            Assert.IsFalse(source.TryGetGlyph(0xD800, out _));
            Assert.IsFalse(source.TryGetGlyph(0x110000, out _));
            Assert.IsFalse(source.TryGetGlyph(0x10FFFD, out _));
        }

        [Test]
        public void TheSameCodePointReturnsTheSameBitmapAndSizesAreClamped()
        {
            UnityGlyphFonts fonts = Fonts();
            IGlyphSource s16 = fonts.ForSize(16);
            Assert.IsTrue(s16.TryGetGlyph('中', out GlyphBitmap a));
            Assert.IsTrue(s16.TryGetGlyph('中', out GlyphBitmap b));
            Assert.AreSame(a, b);
            Assert.AreSame(s16, fonts.ForSize(16));
            Assert.AreEqual(UnityGlyphFonts.MinPixelSize, fonts.ForSize(1).PixelSize);
            Assert.AreEqual(UnityGlyphFonts.MaxPixelSize, fonts.ForSize(500).PixelSize);
            Assert.IsTrue(fonts.ForSize(32).TryGetGlyph('中', out GlyphBitmap big));
            Assert.Greater(big.Height, a.Height);
            Assert.Greater(fonts.GlyphCount, 0);
            Assert.GreaterOrEqual(fonts.SizeCount, 3);
        }

        [Test]
        public void LineMetricsAreAtLeastTheFontSize()
        {
            foreach (int size in new[] { 12, 16, 20, 40 })
            {
                IGlyphSource s = Fonts().ForSize(size);
                Assert.Greater(s.Ascent, 0);
                Assert.GreaterOrEqual(s.Descent, 0);
                Assert.GreaterOrEqual(s.LineHeight, size + 2);
                Assert.GreaterOrEqual(s.LineHeight, s.Ascent + s.Descent);
            }
        }

        [Test]
        public void ManyDifferentCharactersFillMoreThanOneAtlasWithoutLosingAny()
        {
            // 一个 512 的图集在 20 像素下装得下几百个字;取 1500 个常用汉字,前面取过的位图不能被冲掉
            IGlyphSource s = Fonts().ForSize(20);
            Assert.IsTrue(s.TryGetGlyph('中', out GlyphBitmap first));
            int got = 0;
            for (int cp = 0x4E00; cp < 0x4E00 + 1500; cp++)
            {
                if (s.TryGetGlyph(cp, out GlyphBitmap g) && g.Width > 0)
                {
                    got++;
                }
            }
            Assert.Greater(got, 1000);
            Assert.IsTrue(s.TryGetGlyph('中', out GlyphBitmap again));
            Assert.AreSame(first, again);
            AssertInkTouchesEveryEdge(first, "中");
        }

        [Test]
        public void GlyphTextLaysOutAndOutlinesChineseWithTheRealFont()
        {
            IGlyphSource s = Fonts().ForSize(16);
            SpriteFrame one = GlyphText.Render(s, "背包", 0xFFFFFFu, 0x000000u);
            SpriteFrame two = GlyphText.Render(s, "背包\n装备", 0xFFFFFFu, 0x000000u);
            Assert.IsNotNull(one);
            Assert.GreaterOrEqual(one.Width, 2 * 16 * 7 / 10);
            Assert.Greater(two.Height, one.Height + s.LineHeight / 2);
            int opaque = 0;
            for (int i = 3; i < one.Rgba.Length; i += 4)
            {
                if (one.Rgba[i] == 255)
                {
                    opaque++;
                }
            }
            Assert.Greater(opaque, 40);
            SpriteFrame wrapped = GlyphText.Render(s, "这是一段用来换行的比较长的中文说明文字", 0xFFFFFFu, 0x000000u, 100);
            Assert.LessOrEqual(wrapped.Width, 100 + 4);
            Assert.Greater(wrapped.Height, one.Height * 2);
            WriteSample(new[] { one, two, wrapped }, "glyph-sample.png");
        }

        [Test]
        public void TheOverlayUsesTheInstalledFontsAndTranslatesPlaceholders()
        {
            var art = new OverlayArt { Fonts = Fonts() };
            SpriteFrame chinese = art.Text("背包", 2);
            Assert.GreaterOrEqual(chinese.Height, 16);
            Assert.AreSame(chinese, art.Text("INVENTORY", 2));
            Assert.AreEqual(OverlayArt.TextHeight(2), art.Text("HP 100/200", 2).Height);       // 点阵字库画得出的仍走点阵
            Assert.AreEqual(OverlayArt.TextHeight(2), new OverlayArt().Text("背包", 2).Height);  // 没装字体:退回点阵
        }

        [Test]
        public void TheGb18030TableWorksInTheUnityRuntime()
        {
            // 公开的标准测试向量(GB18030 的 "中文" 与 "你好,世界")
            Assert.AreEqual("中文", Gb18030.Decode(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }));
            Assert.AreEqual("你好,世界", Gb18030.Decode(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3, 0x2C, 0xCA, 0xC0, 0xBD, 0xE7 }));
            var book = new StrBook(new byte[] { 0x6B, 0x31, 0x3E, 0xD6, 0xD0, 0xCE, 0xC4, 0x5C, 0x6E, 0xCE, 0xC4, 0x0D, 0x0A });   // k1>中文\n文
            Assert.AreEqual("中文\n文", book.Get("k1"));
        }

        private static void WriteSample(SpriteFrame[] frames, string name)
        {
            try
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "m29"));
                Directory.CreateDirectory(dir);
                int w = 0, h = 4;
                foreach (SpriteFrame f in frames)
                {
                    w = Math.Max(w, f.Width + 8);
                    h += f.Height + 4;
                }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var pixels = new Color32[w * h];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = new Color32(64, 64, 96, 255);
                }
                int top = 4;
                foreach (SpriteFrame f in frames)
                {
                    for (int y = 0; y < f.Height; y++)
                    {
                        for (int x = 0; x < f.Width; x++)
                        {
                            int s = (y * f.Width + x) * 4;
                            byte a = f.Rgba[s + 3];
                            if (a == 0)
                            {
                                continue;
                            }
                            int d = (h - 1 - (top + y)) * w + 4 + x;
                            Color32 back = pixels[d];
                            pixels[d] = new Color32((byte)((f.Rgba[s] * a + back.r * (255 - a)) / 255), (byte)((f.Rgba[s + 1] * a + back.g * (255 - a)) / 255), (byte)((f.Rgba[s + 2] * a + back.b * (255 - a)) / 255), 255);
                        }
                    }
                    top += f.Height + 4;
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            catch (IOException)
            {
            }
        }
    }
}
