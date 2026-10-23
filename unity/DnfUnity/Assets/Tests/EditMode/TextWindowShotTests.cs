using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dnf.Client;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;
using NUnit.Framework;
using UnityEngine;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 窗口里的汉字(M29,证据 E-121):用系统字体和(有数据时)原版文字表,把背包、商店、系统菜单、复活提示、结算面板各画成 800×600 的 PNG 放在 out/m29/ 供本机查看版式。
    /// 背包与商店的图里有原版物品的名字、说明与图标,只在本机看,不入库、不贴到别处;其余几张只有本项目自己写的界面词。
    /// 断言只看结构(有没有名字与说明的精灵、有没有超出窗口),不对任何原版文字下结论。
    /// 没有带汉字的系统字体或没有 DNF_DIR 时相应的测试跳过。
    /// </summary>
    [Category("Text")]
    public class TextWindowShotTests
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

        private static OverlayArt Art()
        {
            if (_fonts == null)
            {
                Assert.Ignore("这台机器上没有带汉字的系统字体");
            }
            return new OverlayArt { Fonts = _fonts };
        }

        private static string DataDir()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实数据的窗口出图");
            }
            return dir;
        }

        private static void Save(List<SceneSprite> sprites, string name)
        {
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "m29"));
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

        [Test]
        public void TheBagShowsTheCursorItemsChineseNameAndExplain()
        {
            string dir = DataDir();
            OverlayArt art = Art();
            using (DnfAssets assets = DnfAssets.Open(dir))
            {
                PvfArchive pvf = assets.Pvf;
                var data = new GameData(new PvfScriptSource(pvf));
                var catalog = new ItemCatalog(data);
                var texts = new ItemTexts(new StrLibrary(path => pvf.TryGetEntry(path, out PvfEntry e) ? pvf.Read(e) : null));
                HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 40);

                // 挑几件穿得上、有名字也有说明的装备放进背包,光标落在第一件上
                List<ItemInfo> gear = catalog.Droppable()
                    .Where(i => i.IsEquipment && EquipRules.Check(i, EquipRules.SwordmanJob, 40) == EquipResult.Ok && texts.NameOf(i) != null && texts.ExplainOf(i) != null
                        && (i.Part != EquipmentParts.Weapon || HeroLook.CanShow(assets, i)))
                    .Take(12).ToList();
                Assume.That(gear.Count >= 3, "数据里找不到有名字和说明的可穿装备");
                foreach (ItemInfo item in gear)
                {
                    progress.AddItem(item);
                }
                var window = new BagWindow();
                window.UseAssets(assets);
                Assert.IsTrue(window.Show(GameStage.Town));

                List<SceneSprite> sprites = BagWindowArt.Build(window, progress, assets, art, default, texts);
                SceneSprite name = sprites.SingleOrDefault(s => s.AnimationPath == "bag-item-name");
                SceneSprite explain = sprites.SingleOrDefault(s => s.AnimationPath == "bag-item-explain");
                Assert.IsNotNull(name, "背包里光标处的物品应画出名字");
                Assert.IsNotNull(explain, "有说明时应画出说明");
                Assert.AreEqual(BagWindow.SlotX, name.ScreenX);
                Assert.Greater(explain.ScreenY, name.ScreenY + name.Frame.Height - 1);
                AssertInsideScreen(sprites, "背包");
                Save(sprites, "bag-zh.png");

                // 没装字体的画法:同一份数据不画名字与说明
                Assert.IsFalse(BagWindowArt.Build(window, progress, assets, new OverlayArt(), default, texts).Any(s => s.AnimationPath == "bag-item-name"));
            }
        }

        [Test]
        public void TheShopShowsChineseItemTextsAndPageNames()
        {
            string dir = DataDir();
            OverlayArt art = Art();
            using (DnfAssets assets = DnfAssets.Open(dir))
            {
                PvfArchive pvf = assets.Pvf;
                var data = new GameData(new PvfScriptSource(pvf));
                var texts = new ItemTexts(new StrLibrary(path => pvf.TryGetEntry(path, out PvfEntry e) ? pvf.Read(e) : null));
                ShopBook book = ShopBook.FromData(data);
                ItemShopDef def = book.FirstShopWithItems();
                Assume.That(def != null, "数据里没有有商品的商店");
                HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 40);
                progress.AddGold(123456);
                var window = new ShopWindow();
                window.Use(book);
                Assert.IsTrue(window.Show(GameStage.Town, def.Index));

                List<SceneSprite> sprites = ShopWindowArt.Build(window, progress, assets, art, texts);
                AssertInsideScreen(sprites, "商店");
                Save(sprites, "shop-zh.png");
                SceneSprite box = sprites.Single(s => s.AnimationPath == "shop-detail");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "shop-item-name") || box.Frame.Height == 190, "有名字时加高详情框,没名字(原版就空着)时保持原样");
            }
        }

        [Test]
        public void TheOwnPlaceholdersAreDrawnInChinese()
        {
            OverlayArt art = Art();

            var menu = new SystemMenu();
            Assert.IsTrue(menu.Show(GameStage.Town));
            List<SceneSprite> menuSprites = SystemMenuArt.Build(menu, art);
            AssertInsideScreen(menuSprites, "系统菜单");
            Save(menuSprites, "menu-zh.png");

            var revive = new List<SceneSprite>();
            int order = 0;
            ReviveArt.BuildPlaceholder(revive, ref order, art, 7, CoinVerdict.Pay, 3);
            AssertInsideScreen(revive, "复活提示");
            Save(revive, "revive-zh.png");

            var settle = new List<SceneSprite>();
            order = 0;
            SettlementArt.BuildFallback(settle, ref order, art);
            AssertInsideScreen(settle, "结算面板");
            Save(settle, "settlement-zh.png");

            // 中文版与英文占位版画出来的东西不同(进了字体路径)
            var dots = new List<SceneSprite>();
            order = 0;
            SettlementArt.BuildFallback(dots, ref order, new OverlayArt());
            Assert.AreEqual(dots.Count, settle.Count);
            Assert.IsTrue(dots.Zip(settle, (a, b) => a.Frame.Width != b.Frame.Width || a.Frame.Height != b.Frame.Height).Any(x => x));
        }
    }
}
