using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dnf.Client;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using NUnit.Framework;
using UnityEngine;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 任务窗口与任务提示(M31,证据 E-123):用系统字体与用户自己的数据,把城镇里 NPC 头顶的任务标记、和 NPC 的任务对话窗口、任务日志、提示条与追踪
    /// 各画成 800×600 的 PNG 放在 out/m31/ 供本机查看版式。图里有原版任务与 NPC 的名字、对白与奖励,只在本机看,不入库、不贴到别处。
    /// 断言只看结构(有没有对应的精灵、有没有超出屏幕),不对任何原版文字下结论。没有带汉字的系统字体或没有 DNF_DIR 时相应的测试跳过。
    /// </summary>
    [Category("Text")]
    public class QuestWindowShotTests
    {
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

        private static string DataDir()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实数据的任务窗口出图");
            }
            if (_fonts == null)
            {
                Assert.Ignore("这台机器上没有带汉字的系统字体");
            }
            return dir;
        }

        private static void Save(List<SceneSprite> sprites, string name)
        {
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "m31"));
            Directory.CreateDirectory(dir);
            const int w = 800, h = 600;
            var flipped = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(canvas.Pixels, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);   // Unity 纹理的第 0 行在下面
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(flipped);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void AssertInsideScreen(List<SceneSprite> sprites, string what)
        {
            foreach (SceneSprite s in sprites)
            {
                Assert.GreaterOrEqual(s.ScreenX, 0, what + " " + s.AnimationPath);
                Assert.GreaterOrEqual(s.ScreenY, 0, what + " " + s.AnimationPath);
                Assert.LessOrEqual(s.ScreenX + s.Frame.Width, 800, what + " " + s.AnimationPath + " 超出右边");
                Assert.LessOrEqual(s.ScreenY + s.Frame.Height, 600, what + " " + s.AnimationPath + " 超出下边");
            }
        }

        /// <summary>
        /// 一个测试里画完全部(任务脚本有三千多个,建索引要一会儿,不为每张图各建一次):
        /// 城镇头顶标记 → 任务最多的那个 NPC 的对话窗口 → 接下选中的任务 → 任务日志 → 提示条与追踪叠在城镇上。
        /// </summary>
        [Test]
        public void TheQuestScreensAreDrawnFromTheRealData()
        {
            string dir = DataDir();
            var art = new OverlayArt { Fonts = _fonts };
            using (DnfAssets assets = DnfAssets.Open(dir))
            {
                HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 40);
                QuestBook book = QuestBook.FromAssets(assets, progress, 1);

                // ---- 城镇一屏:有任务可接的 NPC 头顶有感叹号(这个区域没有就跳过这一张)
                TownAtlas atlas = TownAtlas.Open(assets);
                TownSession session = TownWorld.CreateSession(assets, atlas, new TownPlace(1, 1), DemoHero.CreateOutfit(assets));
                session.Hero.Place(615, 234, false, HeroMotion.Rest, 0);
                session.Camera.Place(96);
                List<SceneSprite> scene = session.Build(545);
                var town = new List<SceneSprite>(scene);
                int order = 0;
                int marks = QuestHudArt.AddNpcMarks(town, ref order, art, book.MarkOf, scene, session.Npcs, session.Camera.IntX, 0);
                Assert.AreEqual(marks, town.Count(s => s.AnimationPath == "quest-mark"));
                if (marks > 0)
                {
                    Save(town, "town-marks.png");
                }

                // ---- 任务最多的那个 NPC
                int npcId = -1, most = 0;
                foreach (int id in book.Catalog.All().Select(d => d.NpcId).Where(n => n >= 0).Distinct().Take(600))
                {
                    int rows = book.RowsFor(id).Count;
                    if (rows > most)
                    {
                        most = rows;
                        npcId = id;
                    }
                }
                Assume.That(npcId >= 0 && most > 0, "40 级时没有 NPC 有任务可接");

                var window = new QuestWindow();
                window.Use(book);
                var names = NpcNames.Create(assets, new WorldTexts(new StrLibrary(path => assets.Pvf.TryGetEntry(path, out PvfEntry e) ? assets.Pvf.Read(e) : null)));
                Assert.IsTrue(window.ShowNpc(GameStage.Town, npcId, names.DialogNameOf(npcId), -1));
                Assert.AreEqual(most, window.Rows.Count);
                List<SceneSprite> npcShot = QuestWindowArt.Build(window, art);
                Assert.AreEqual(Math.Min(most, QuestWindow.VisibleRows), npcShot.Count(s => s.AnimationPath == "quest-row"));
                Assert.IsTrue(npcShot.Any(s => s.AnimationPath == "quest-button-primary"), "选中的是可接的任务,接受按钮是主按钮");
                AssertInsideScreen(npcShot, "任务对话窗口");
                Save(npcShot, "quest-npc.png");

                // ---- 接下选中的那个任务(空背包,不会放不下接任务送的东西)
                Assert.IsTrue(window.Activate());
                Assert.AreEqual(1, book.Accepted);
                Assert.AreEqual(1, progress.QuestsActive);
                window.Close();

                // ---- 任务日志
                Assert.IsTrue(window.ShowJournal(GameStage.Town));
                Assert.AreEqual(1, window.Rows.Count);
                List<SceneSprite> journal = QuestWindowArt.Build(window, art);
                Assert.IsTrue(journal.Any(s => s.AnimationPath == "quest-row"));
                AssertInsideScreen(journal, "任务日志");
                Save(journal, "quest-journal.png");
                window.Close();

                // ---- 提示条与追踪叠在城镇上
                var hud = new QuestHud();
                hud.Advance(16, book);
                Assert.GreaterOrEqual(hud.ToastsShown, 1, "接任务应出一条提示");
                Assert.AreEqual(1, hud.Tracked.Count);
                var overlay = new List<SceneSprite>(scene);
                overlay.AddRange(QuestHudArt.Build(hud, art, true));
                Assert.IsTrue(overlay.Any(s => s.AnimationPath == "quest-toast-bg"));
                Assert.IsTrue(overlay.Any(s => s.AnimationPath == "quest-tracker-bg"));
                AssertInsideScreen(QuestHudArt.Build(hud, art, true), "提示条与追踪");
                Save(overlay, "quest-hud.png");

                // 没有字体的画法:同一份数据不画任务名(任务行用占位名)
                var bare = new QuestWindow();
                bare.Use(book);
                Assert.IsTrue(bare.ShowJournal(GameStage.Town));
                Assert.IsFalse(QuestWindowArt.Build(bare, new OverlayArt()).Any(s => s.AnimationPath == "quest-message"), "没有字体不画对白");
            }
        }
    }
}
