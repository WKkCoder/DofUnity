using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dnf.Client;
using Dnf.Data;
using Dnf.Play;
using Dnf.Render;
using NUnit.Framework;
using UnityEngine;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 联网选角界面(r18-charui)用系统汉字字体、用户自备数据里的底图与职业称号画出来:几种样子(普通、滚动、不能进入的提示、暂未开放的提示、正在进入)
    /// 画成 800×600 的 PNG 放在 out/charui/(文件名以 zh- 开头,只在本机看版式与中文,不入库);
    /// 断言只看结构:每行文字的墨迹中心是不是落在基线 S02 上量出的位置(与 CPU 参照的方块字体版同一套数字)、中文是不是真的走了字体、灰按钮有没有灰。
    /// 角色名字、编号、职业全是合成的;需要 DNF_DIR(底图与职业称号来自自备数据)和带汉字的系统字体,缺哪个相应的测试就跳过。
    /// </summary>
    [Category("Text")]
    public class CharacterSelectOnlineShotTests
    {
        private const int Width = 800;
        private const int Height = 600;

        private static UnityGlyphFonts _fonts;

        [OneTimeSetUp]
        public void OpenFonts()
        {
            _fonts = UnityGlyphFonts.TryCreate(null, out _);
        }

        [OneTimeTearDown]
        public void CloseFonts()
        {
            _fonts?.Dispose();
            _fonts = null;
        }

        private static void NeedFonts()
        {
            if (_fonts == null)
            {
                Assert.Ignore("这台机器上没有带汉字的系统字体");
            }
        }

        private static DnfAssets OpenArt()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("没有 DNF_DIR:联网选角的底图与职业称号来自自备数据,跳过(文字与版面的逻辑在 src/Dnf.Play.Tests 里用合成数据测过)");
            }
            return DnfAssets.Open(dir);
        }

        private static void Save(List<SceneSprite> sprites, string name)
        {
            var canvas = new RgbaCanvas(Width, Height);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "charui"));
            Directory.CreateDirectory(dir);
            var flipped = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(canvas.Pixels, y * Width * 4, flipped, (Height - 1 - y) * Width * 4, Width * 4);   // Unity 纹理的第 0 行在下面
            }
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(flipped);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        /// <summary>合成的服务端角色列表:名字是编的中文,职业循环 11 种,每 5 个里第 5 个 X1 = 1(不能进入),每 7 个里第 7 个待删除。</summary>
        private static OnlineCharacterList Roster(int count)
        {
            string[] numerals = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
            var entries = new List<OnlineCharacterEntry>();
            for (int i = 0; i < count; i++)
            {
                string name = "测试角色" + (i < numerals.Length ? numerals[i] : (i + 1).ToString());
                byte x1 = (byte)((i + 1) % 5 == 0 ? 1 : 0);
                uint d2 = (i + 1) % 7 == 0 ? 3u : 0u;
                entries.Add(new OnlineCharacterEntry(
                    (ushort)(900 + i), Encoding.UTF8.GetBytes(name), x1, (byte)(i % 11), (byte)(i % 3 == 0 ? 0 : 0x10 | (i % 3)), (byte)(10 + i), 0, 0,
                    new OnlineCharacterEquip[0], 0, 0, 0, 0, 0, 0, 0, 0, 0, d2));
            }
            return new OnlineCharacterList(6, 1, 2, 77u, entries, 0);
        }

        private static CharacterSelectScreen Online(DnfAssets assets, int count, int index = 0)
        {
            ICharacterTitles titles = OnlineCharacterSelectSetup.Create(assets, true).Titles;
            CharacterRoster roster = OnlineCharacterRoster.From(Roster(count), titles);
            return CharacterSelectScreen.Online(roster, CharacterSelectScene.ForOnline(assets), index, _fonts);
        }

        /// <summary>某个精灵里文字墨迹中心的屏幕坐标。</summary>
        private static (int X, int Y) InkAt(SceneSprite s)
        {
            CharacterSelectArt.InkCenter(s.Frame, out int ix, out int iy);
            return (s.ScreenX + ix, s.ScreenY + iy);
        }

        [Test]
        public void EveryCaptionSitsAtTheBaselineInkPositionAndSpeaksChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                CharacterSelectScreen screen = Online(assets, 8);
                List<SceneSprite> sprites = screen.Build(0);

                // 每个有角色的格子两行字(名字行、职业行);第 5 个角色(X1 = 1)再有一行红色标记,第 7 个待删除也有标记
                List<SceneSprite> captions = sprites.Where(s => s.AnimationPath == "select-char-caption").ToList();
                Assert.AreEqual(16, captions.Count, "8 个角色 × 2 行");
                for (int cell = 0; cell < 8; cell++)
                {
                    CharacterSelectArt.SlotOrigin(cell, out int sx, out int sy);
                    (int x1, int y1) = InkAt(captions[cell * 2]);
                    (int x2, int y2) = InkAt(captions[cell * 2 + 1]);
                    Assert.AreEqual(sx + 60, x1, 1, "第 " + cell + " 格名字行的墨迹中心 x(基线上是槽位左边 + 60)");
                    Assert.AreEqual(sx + 60, x2, 1, "第 " + cell + " 格职业行的墨迹中心 x");
                    Assert.AreEqual(sy + 178, y1, 1, "名字行的墨迹中心 y(基线上墨迹行 172 到 183)");
                    Assert.AreEqual(sy + 191, y2, 1, "职业行的墨迹中心 y(基线上墨迹行 185 到 196)");
                    Assert.LessOrEqual(captions[cell * 2].Frame.Width, CharacterSelectArt.SlotWidth, "名字放得进槽位宽度");
                }
                Assert.AreEqual(2, sprites.Count(s => s.AnimationPath == "select-char-marker"), "X1 = 1 与待删除各一行红字");

                // 中文真的走了字体:名字行比点阵占位的英文名宽得多的字形(4 个汉字 + 前缀)不是 '?' 方块
                Assert.GreaterOrEqual(captions[0].Frame.Width, 50);

                // 小按钮的字:四个按钮各一行,墨迹中心在按钮内 (39, 11);创立与删除灰着
                foreach (SceneSprite t in sprites.Where(s => s.AnimationPath == "select-char-button-text"))
                {
                    (int bx, int by) = InkAt(t);
                    Assert.AreEqual(CharacterSelectArt.SmallButtonY + 11, by, 1, "按钮字的墨迹中心 y");
                    Assert.That(new[] { 137, 216, 508, 587 }.Any(left => Math.Abs(left + 39 - bx) <= 1), "按钮字的墨迹中心 x 在按钮内 39,实际 " + bx);
                }
                Assert.AreEqual(2, sprites.Count(s => s.AnimationPath == "select-char-button-disabled"), "创立、删除灰着");
                Assert.AreEqual(2, sprites.Count(s => s.AnimationPath == "select-char-button"), "选择频道、结束游戏正常");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "select-char-start"), "游戏开始可用");
                Save(sprites, "zh-select-online.png");
            }
        }

        [Test]
        public void MoreThanTwelveCharactersScrollAndTheScrollBarIsDrawn()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                CharacterSelectScreen screen = Online(assets, 14);
                Assert.AreEqual(0, screen.FirstRow);
                List<SceneSprite> top = screen.Build(0);
                Assert.IsTrue(top.Any(s => s.AnimationPath == "select-char-scroll-thumb"), "超过 12 个出滚动条滑块");
                Assert.AreEqual(24, top.Count(s => s.AnimationPath == "select-char-caption"), "12 个角色 × 2 行");
                Save(top, "zh-select-online-top.png");

                Assert.IsTrue(screen.Scroll(1), "往下滚一行");
                Assert.AreEqual(1, screen.FirstRow);
                List<SceneSprite> scrolled = screen.Build(0);
                // 14 个角色:第 2 行(6 个)与第 3 行(2 个)在窗口里
                Assert.AreEqual(16, scrolled.Count(s => s.AnimationPath == "select-char-caption"));
                Save(scrolled, "zh-select-online-scrolled.png");
            }
        }

        [Test]
        public void ThePromptsAndTheBusyBarAreDrawnAboveTheStartButton()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                // 选中第 5 个(X1 = 1):确认只弹提示、不给结果
                CharacterSelectScreen blocked = Online(assets, 8, 4);
                blocked.Confirm();
                Assert.AreEqual(CharacterSelectOutcome.None, blocked.Outcome);
                Assert.AreEqual(CharacterSelectPrompt.CannotEnter, blocked.Prompt);
                List<SceneSprite> sprites = blocked.Build(0);
                SceneSprite bar = sprites.Single(s => s.AnimationPath == CharacterSelectArt.PromptTag + "-bar");
                Assert.LessOrEqual(bar.ScreenY + bar.Frame.Height, CharacterSelectArt.StartY, "提示条不压\"游戏开始\"");
                Save(sprites, "zh-select-online-cannot.png");

                // 点灰着的创立:暂未开放
                CharacterSelectScreen na = Online(assets, 3);
                na.Click(CharacterSelectArt.ButtonRect(CharacterSelectButton.Create).X + 5, CharacterSelectArt.SmallButtonY + 5);
                Assert.AreEqual(CharacterSelectPrompt.NotAvailable, na.Prompt);
                Save(na.Build(0), "zh-select-online-na.png");

                // 点了开始:按钮变灰,"正在进入…"
                CharacterSelectScreen busy = Online(assets, 3);
                busy.Confirm();
                Assert.IsTrue(busy.Busy);
                List<SceneSprite> busySprites = busy.Build(240);
                Assert.IsTrue(busySprites.Any(s => s.AnimationPath == "select-char-start-disabled"));
                Assert.IsTrue(busySprites.Any(s => s.AnimationPath == CharacterSelectArt.BusyTag + "-spinner"));
                Save(busySprites, "zh-select-online-busy.png");
            }
        }
    }
}
