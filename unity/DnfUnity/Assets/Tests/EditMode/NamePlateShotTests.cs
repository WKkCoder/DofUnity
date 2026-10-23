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
    /// 城镇底栏与头顶名牌(M30,证据 E-122):用系统字体与(有数据时)原版文字表,把城镇一屏(NPC 名字、主角名牌、底栏)与副本一屏(主角名牌、目标血条里的怪物名字)
    /// 各画成 800×600 的 PNG 放在 out/m30/ 供本机查看版式与位置。图里有原版 NPC、怪物的名字,只在本机看,不入库、不贴到别处。
    /// 另有一屏副本地面(掉落物的名牌与被选中的那一件,out/m30/ground-labels.png)。
    /// 断言只看结构(有没有名牌精灵、位置规则),不对任何原版文字下结论。没有带汉字的系统字体或没有 DNF_DIR 时相应的测试跳过。
    /// </summary>
    [Category("Text")]
    public class NamePlateShotTests
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
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实数据的名牌出图");
            }
            if (_fonts == null)
            {
                Assert.Ignore("这台机器上没有带汉字的系统字体");
            }
            return dir;
        }

        private static StrLibrary Library(DnfAssets assets)
        {
            PvfArchive pvf = assets.Pvf;
            return new StrLibrary(path => pvf.TryGetEntry(path, out PvfEntry e) ? pvf.Read(e) : null);
        }

        private static void Save(List<SceneSprite> sprites, string name)
        {
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "m30"));
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

        [Test]
        public void TheTownScreenHasNpcNamesTheHeroPlateAndTheBottomBar()
        {
            string dir = DataDir();
            var art = new OverlayArt { Fonts = _fonts };
            using (DnfAssets assets = DnfAssets.Open(dir))
            {
                TownAtlas atlas = TownAtlas.Open(assets);
                TownSession session = TownWorld.CreateSession(assets, atlas, new TownPlace(1, 1), DemoHero.CreateOutfit(assets));
                session.Hero.Place(615, 234, false, HeroMotion.Rest, 0);
                session.Camera.Place(96);
                List<SceneSprite> scene = session.Build(545);

                var names = NpcNames.Create(assets, new WorldTexts(Library(assets)));
                var sprites = new List<SceneSprite>(scene);
                int order = 0;
                int cameraX = session.Camera.IntX;
                int npcPlates = NamePlates.AddNpcs(sprites, ref order, art, names, scene, session.Npcs, cameraX);
                Assert.IsTrue(NamePlates.AddHero(sprites, ref order, art, 12, CharacterRoster.LocalName,
                    (int)Math.Floor(session.Hero.X) - cameraX, (int)Math.Floor(session.Hero.Y) + session.GroundY));
                Assert.Greater(npcPlates, 0, "这个区域的 NPC 应至少有一个带名字");

                TownHud hud = TownHud.TryCreate(assets);
                Assert.IsNotNull(hud);
                HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 12);
                order = 0;
                Assert.IsTrue(hud.Build(sprites, ref order, progress));

                SceneSprite hero = sprites.Single(s => s.AnimationPath == "plate-hero");
                Assert.AreEqual(session.Hero.Y + session.GroundY - NamePlates.HeroInkAboveFeet, hero.ScreenY + NamePlates.InkBottom(hero.Frame), 1.0);
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "hud-bar"));
                Save(sprites, "town-plates.png");

                // 没有字体的画法:名牌一个都不出
                var bare = new List<SceneSprite>();
                order = 0;
                Assert.AreEqual(0, NamePlates.AddNpcs(bare, ref order, new OverlayArt(), names, scene, session.Npcs, cameraX));
            }
        }

        [Test]
        public void TheDungeonScreenHasTheHeroPlateAndTheMonsterNameInTheTargetWindow()
        {
            string dir = DataDir();
            IGlyphFonts savedFonts = OverlayArt.SharedFonts;
            WorldTexts savedTexts = WorldTexts.Shared;
            try
            {
                using (DnfAssets assets = DnfAssets.Open(dir))
                {
                    OverlayArt.SharedFonts = _fonts;
                    WorldTexts.Shared = new WorldTexts(Library(assets));
                    HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 12);
                    DungeonSession session = DungeonSession.Create(assets, TownDemo.DefaultDungeon, 1, false, progress: progress);
                    var bot = new DungeonBot(false);
                    for (int i = 0; i < 1500; i++)
                    {
                        bot.Advance(session, 16.7);
                        MonsterHpWindow window = session.Current.MonsterWindow;
                        if (window != null && window.Visible && session.Current.Vitals.IsAlive)
                        {
                            break;
                        }
                    }
                    MonsterHpWindow hp = session.Current.MonsterWindow;
                    Assume.That(hp != null && hp.Visible, "自动机没有在 25 秒内打中怪物");
                    List<SceneSprite> sprites = session.Build((long)session.ClockMs);
                    SceneSprite plate = sprites.SingleOrDefault(s => s.AnimationPath == "plate-hero");
                    Assert.IsNotNull(plate, "副本里有字体时主角头顶应有名牌");
                    Assert.IsNotNull(hp.Target.Panel.NameKey, "怪物脚本应有 [name]");
                    Save(sprites, "dungeon-plates.png");
                    Assert.IsTrue(sprites.Any(s => s.AnimationPath == "mhp-name"), "目标血条里应画出怪物名字");
                }
            }
            finally
            {
                OverlayArt.SharedFonts = savedFonts;
                WorldTexts.Shared = savedTexts;
            }
        }

        [Test]
        public void TheDungeonFloorShowsLabelsOnTheDropsAndTheOneInReachIsSelected()
        {
            string dir = DataDir();
            IGlyphFonts savedFonts = OverlayArt.SharedFonts;
            WorldTexts savedTexts = WorldTexts.Shared;
            try
            {
                using (DnfAssets assets = DnfAssets.Open(dir))
                {
                    OverlayArt.SharedFonts = _fonts;
                    WorldTexts.Shared = new WorldTexts(Library(assets));

                    // 每个稀有度各找一件有名字、有地上图的物品(按编号顺序往后找,不写死任何物品编号)
                    var catalog = new ItemCatalog(new GameData(new PvfScriptSource(assets.Pvf)));
                    var found = new ItemInfo[DropLabels.RarityColors.Length];
                    foreach (int code in catalog.StackableCodes().Take(400).Concat(catalog.EquipmentCodes().Take(5000)))
                    {
                        ItemInfo item = catalog.Get(code);
                        if (item == null || item.NameKey == null || item.FieldImage == null || item.Rarity < 0 || item.Rarity >= found.Length || found[item.Rarity] != null)
                        {
                            continue;
                        }
                        found[item.Rarity] = item;
                        if (found.All(f => f != null))
                        {
                            break;
                        }
                    }
                    ItemInfo[] items = found.Where(f => f != null).ToArray();
                    Assume.That(items.Length >= 2, "没找到足够多种稀有度的物品");

                    HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 12);
                    DungeonSession session = DungeonSession.Create(assets, TownDemo.DefaultDungeon, 1, false, progress: progress);
                    RoomCombatSession room = session.Current;
                    TownHero hero = session.Hero;
                    double hx = hero.X, hy = hero.Y;
                    for (int i = 0; i < items.Length; i++)
                    {
                        room.SpawnItem(items[i], hx - 90 - 75 * i, hy - 30 + 30 * (i % 3));
                    }
                    room.SpawnGold(12345, hx - 40, hy + 45);
                    for (int i = 0; i < 400 && room.Drops.Any(d => !d.Landed); i++)
                    {
                        session.Update(16.7, 0, 0);
                    }
                    Assert.IsFalse(room.Drops.Any(d => !d.Landed), "掉落物应当都落了地");

                    // 主角站到第一件物品旁(不按键):它的名牌画成选中
                    FieldDrop target = room.Drops.First(d => d.IsItem);
                    hero.Place(target.X, target.Y, false, HeroMotion.Rest, 0);
                    session.Update(16.7, 0, 0);
                    Assert.AreSame(target, room.SelectedDrop);
                    List<SceneSprite> shot = session.Build((long)session.ClockMs);
                    Assert.AreEqual(1, shot.Count(s => s.AnimationPath == "drop-label-selected"), "够得着的那一件画成选中");
                    Assert.GreaterOrEqual(shot.Count(s => s.AnimationPath.StartsWith("drop-label")), items.Length, "每件物品一块名牌");
                    Save(shot, "ground-labels.png");
                }
            }
            finally
            {
                OverlayArt.SharedFonts = savedFonts;
                WorldTexts.Shared = savedTexts;
            }
        }
    }
}
