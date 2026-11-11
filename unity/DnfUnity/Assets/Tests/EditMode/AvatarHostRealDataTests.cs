using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;
using NUnit.Framework;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 装扮宿主接线(r15-avatarhost)在真实数据上的冒烟:真实的角色脚本造衣柜底座、真实的城镇图册造城镇会话,
    /// 从装备列表里按编号挑穿得上的时装(只在内存里用,不记编号、不打印、不入库),穿上之后城镇主角的图层序列变、脱下恢复、
    /// 再造会话(换区域、回城)仍穿着、存档往返穿回来、换武器不丢装扮。断言只看结构(图层序列变没变、同不同),不对任何原版内容下结论。
    /// 没有 DNF_DIR(或目录里没有 Script.pvf)时跳过。
    /// </summary>
    [Category("Avatar")]
    public class AvatarHostRealDataTests
    {
        private static string DataDir()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实数据的装扮宿主检查");
            }
            return dir;
        }

        private static string[] Paths(AvatarOutfit outfit)
        {
            return outfit.Layers.Select(l => l.ImagePath).ToArray();
        }

        private static int BagIndexOf(HeroProgress progress, ItemInfo item)
        {
            IReadOnlyList<ItemStack> tab = progress.Bag.Tab(InventoryTab.Equipment);
            for (int i = 0; i < tab.Count; i++)
            {
                if (tab[i].Info.Code == item.Code)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>按装备编号从小到大读,挑部位各不相同、这个主角穿得上且画面上穿得出来的时装,最多 <paramref name="count"/> 件。</summary>
        private static List<ItemInfo> PickAvatars(DnfAssets assets, AvatarSession session, int count)
        {
            var catalog = new ItemCatalog(new GameData(new PvfScriptSource(assets.Pvf)));
            var picked = new List<ItemInfo>();
            foreach (int code in catalog.EquipmentCodes())
            {
                if (picked.Count >= count)
                {
                    break;
                }
                ItemInfo item = catalog.Get(code);
                if (item == null || !item.IsEquipment || !AvatarSlots.IsSlot(item.Part) || picked.Any(p => p.Part == item.Part))
                {
                    continue;
                }
                if (AvatarRules.Check(item, item.Part, session.Progress.Job, session.Progress.Level) == EquipResult.Ok && session.CanShow(item))
                {
                    picked.Add(item);
                }
            }
            return picked;
        }

        [Test]
        public void TheRealTownHeroIsDressedUndressedAndComesBackFromASave()
        {
            string dir = DataDir();
            using (DnfAssets assets = DnfAssets.Open(dir))
            {
                HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 60);
                var session = new AvatarSession(assets, progress);
                Assert.IsTrue(session.Ready, "真实角色脚本应能造出衣柜的底座");

                List<ItemInfo> picked = PickAvatars(assets, session, 3);
                Assume.That(picked.Count >= 2, "数据里找不到足够的穿得上的时装");

                // 真实的城镇图册,外面包一层装扮
                var inner = new AtlasTownWorld(assets, TownAtlas.Open(assets), 1, withSplashes: false, weaponPath: () => progress.WeaponPath);
                var world = new AvatarTownWorld(inner, session);
                TownHero hero = world.Create(world.Start).Hero;
                string[] naked = Paths(hero.Outfit);
                Assert.AreSame(session.Wardrobe.Base, hero.Outfit, "没穿装扮时主角穿的就是衣柜的底座");

                // 穿上第一件:图层序列变了,主角穿的是衣柜现在的外观
                foreach (ItemInfo item in picked)
                {
                    Assert.IsTrue(progress.AddItem(item));
                }
                Assert.AreEqual(EquipResult.Ok, session.Wear(BagIndexOf(progress, picked[0])));
                Assert.IsTrue(session.Sync(hero));
                string[] one = Paths(hero.Outfit);
                CollectionAssert.AreNotEqual(naked, one, "穿上时装之后图层序列应该变");
                Assert.AreSame(session.Wardrobe.Current, hero.Outfit);

                // 换区域、回城:新造的会话里的主角照样穿着
                CollectionAssert.AreEqual(one, Paths(world.Create(world.Return).Hero.Outfit));

                // 再穿其余的,脱掉第一件:图层各不相同,最后全部脱下回到穿之前
                for (int i = 1; i < picked.Count; i++)
                {
                    Assert.AreEqual(EquipResult.Ok, session.Wear(BagIndexOf(progress, picked[i])));
                }
                Assert.IsTrue(session.Sync(hero));
                string[] all = Paths(hero.Outfit);
                CollectionAssert.AreNotEqual(one, all);
                Assert.AreEqual(picked.Count, session.Wardrobe.WornCount);
                Assert.AreEqual(picked.Count, session.Slots.WornCount);

                // 存档往返:文本里有装扮行,新的一局读回来穿着同样的东西,外观的图层序列一样
                HeroSave save = progress.Snapshot();
                session.Capture(save);
                string text = save.ToText();
                Assert.AreEqual(picked.Count, text.Split('\n').Count(line => line.StartsWith("avatar ", StringComparison.Ordinal)));
                Assert.IsTrue(HeroSave.TryParse(text, out HeroSave read, out string error), error);
                HeroProgress fresh = HeroProgress.CreateForLocalHero(assets, 60);
                var freshSession = new AvatarSession(assets, fresh);
                fresh.RestoreFromData(read, assets);
                AvatarRestoreReport report = freshSession.RestoreFromData(read);
                Assert.AreEqual((picked.Count, 0, 0), (report.Worn, report.Moved, report.Dropped));
                CollectionAssert.AreEqual(all, Paths(freshSession.Wardrobe.Current));
                CollectionAssert.AreEqual(
                    Enumerable.Range(AvatarSlots.First, AvatarSlots.Count).Select(p => session.Slots.Get(p)?.Code ?? 0),
                    Enumerable.Range(AvatarSlots.First, AvatarSlots.Count).Select(p => freshSession.Slots.Get(p)?.Code ?? 0));

                // 全部脱下:图层序列回到最初
                foreach (ItemInfo item in picked)
                {
                    Assert.IsTrue(session.TakeOff(item.Part));
                }
                Assert.IsTrue(session.Sync(hero));
                CollectionAssert.AreEqual(naked, Paths(hero.Outfit));
                Assert.AreSame(session.Wardrobe.Base, hero.Outfit);
            }
        }

        [Test]
        public void ChangingTheRealWeaponKeepsTheAvatarsAndTheWornStatsLeaveThemOut()
        {
            string dir = DataDir();
            using (DnfAssets assets = DnfAssets.Open(dir))
            {
                HeroProgress progress = HeroProgress.CreateForLocalHero(assets, 60);
                var session = new AvatarSession(assets, progress);
                Assert.IsTrue(session.Ready);
                List<ItemInfo> picked = PickAvatars(assets, session, 2);
                Assume.That(picked.Count >= 1, "数据里找不到穿得上的时装");

                var inner = new AtlasTownWorld(assets, TownAtlas.Open(assets), 1, withSplashes: false, weaponPath: () => progress.WeaponPath);
                var world = new AvatarTownWorld(inner, session);
                TownHero hero = world.Create(world.Start).Hero;
                string starterPath = HeroLook.WeaponPathOf(hero.Outfit);
                Assert.IsNotNull(starterPath, "底座上有起手武器");

                foreach (ItemInfo item in picked)
                {
                    Assert.IsTrue(progress.AddItem(item));
                    Assert.AreEqual(EquipResult.Ok, session.Wear(BagIndexOf(progress, item)));
                }
                Assert.IsTrue(session.Sync(hero));
                EquipmentStats withStarter = session.WornStats(hero);
                TownHero bare = world.Create(world.Start).Hero;
                bare.Outfit = session.Wardrobe.Base;           // 同一个进度、不穿装扮的主角
                EquipmentStats bareStats = HeroPanel.WornEquipment(assets, bare, progress);
                Assert.AreEqual((bareStats.PhysicalAttack, bareStats.MagicalAttack), (withStarter.PhysicalAttack, withStarter.MagicalAttack),
                    "装扮不进攻击面板:穿着装扮与不穿装扮的主角面板一样");

                // 放进一把穿得上、画面上也穿得出来的武器并穿上:外观换了武器,装扮都还在,面板仍不含装扮
                Assume.That(HeroLook.GrantWearableWeapon(assets, progress), "数据里找不到穿得上的武器");
                IReadOnlyList<ItemStack> tab = progress.Bag.Tab(InventoryTab.Equipment);
                int weaponIndex = -1;
                for (int i = 0; i < tab.Count; i++)
                {
                    if (tab[i].Info.Part == EquipmentParts.Weapon)
                    {
                        weaponIndex = i;
                    }
                }
                Assert.GreaterOrEqual(weaponIndex, 0);
                Assert.AreEqual(EquipResult.Ok, progress.Equip(weaponIndex, item => HeroLook.CanShow(assets, item)));
                Assert.IsTrue(session.Sync(hero));
                Assert.IsTrue(HeroLook.SameEquipPath(HeroLook.WeaponPathOf(hero.Outfit), progress.WeaponPath), "外观上是穿着的那把武器");
                Assert.AreEqual(picked.Count, session.Wardrobe.WornCount);
                Assert.AreSame(session.Wardrobe.Current, hero.Outfit);
                EquipmentStats withWeapon = session.WornStats(hero);
                TownHero plain = world.Create(world.Start).Hero;
                plain.Outfit = session.Wardrobe.Base;          // 同一把武器、不穿装扮
                EquipmentStats plainStats = HeroPanel.WornEquipment(assets, plain, progress);
                Assert.AreEqual((plainStats.PhysicalAttack, plainStats.MagicalAttack), (withWeapon.PhysicalAttack, withWeapon.MagicalAttack));

                // 再造会话(回城)穿着这把武器加装扮;脱下武器退回起手武器(同 HeroLook.Apply),装扮仍在
                Assert.IsTrue(HeroLook.SameEquipPath(HeroLook.WeaponPathOf(world.Create(world.Return).Hero.Outfit), progress.WeaponPath));
                Assert.IsTrue(progress.Unequip(EquipmentParts.Weapon));
                Assert.IsTrue(session.Sync(hero));
                Assert.IsTrue(HeroLook.SameEquipPath(HeroLook.WeaponPathOf(hero.Outfit), DemoHero.StarterWeapon), "没穿武器退回起手武器");
                Assert.AreEqual(picked.Count, session.Wardrobe.WornCount);
            }
        }
    }
}
