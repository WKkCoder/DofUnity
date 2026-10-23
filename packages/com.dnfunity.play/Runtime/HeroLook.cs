using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 主角的外观跟着装备栏走(证据 E-118):装备栏里穿着武器时,主角的换装(<see cref="AvatarOutfit"/>)里的武器与它的动画图层、
    /// 命中框就是这一件;没穿用起手的演示武器。外观是不可变的,换武器就是造一套新的换装再赋给 <see cref="TownHero.Outfit"/>。
    /// 只有武器有动画图层;其余部位(上衣、裤子……)的装备这一步只影响攻击面板(<see cref="HeroPanel.WornEquipment(DnfAssets, TownHero, HeroProgress)"/>),不改外观。
    /// </summary>
    public static class HeroLook
    {
        /// <summary>
        /// 主角的动作要用到的武器图层动画(每个动作在武器脚本所在目录的每个图层目录里各有一个):三段普攻、奔跑攻击、空中攻击。
        /// 少了任何一个,那个动作上武器就没有图像也没有攻击框,所以 <see cref="CanShow"/> 要求它们都在。
        /// </summary>
        public static readonly string[] RequiredWeaponMotions = { "attack1.ani", "attack2.ani", "attack3.ani", "dashattack.ani", "jumpattack.ani" };

        /// <summary>外观里那件武器的脚本路径(.equ);没有武器(或没有外观)返回 null。</summary>
        public static string WeaponPathOf(AvatarOutfit outfit)
        {
            if (outfit == null)
            {
                return null;
            }
            foreach (AvatarItem item in outfit.Items)
            {
                if (string.Equals(item.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase))
                {
                    return item.EquPath;
                }
            }
            return null;
        }

        /// <summary>两个装备脚本路径是不是同一个(忽略大小写与斜杠方向;不以 equipment/ 开头的相对路径按剑魂的装备根目录补全)。</summary>
        public static bool SameEquipPath(string a, string b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            return string.Equals(FullEquipPath(a), FullEquipPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string FullEquipPath(string path)
        {
            string p = path.Replace('\\', '/');
            return p.StartsWith("equipment/", StringComparison.OrdinalIgnoreCase) ? p : DemoHero.SwordmanEquipRoot + "/" + p;
        }

        /// <summary>
        /// 把主角的外观换成穿着 <paramref name="weaponEqu"/> 的那一套(默认外观加这件武器);读不出这件武器的外观(外观里没有武器)时什么也不换,返回 false。
        /// </summary>
        public static bool TrySwapWeapon(DnfAssets assets, TownHero hero, string weaponEqu)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (hero?.Outfit == null || string.IsNullOrEmpty(weaponEqu))
            {
                return false;
            }
            AvatarItem weapon = AvatarItem.Read(assets, hero.Outfit.Class, FullEquipPath(weaponEqu));
            if (weapon == null || !string.Equals(weapon.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            hero.Outfit = hero.Outfit.WithWeapon(weapon);
            return true;
        }

        /// <summary>
        /// 让 <paramref name="hero"/> 的外观与装备栏一致:穿着武器就换成它,没穿退回起手武器;已经一致(或没给进度)什么也不做。返回有没有换外观。
        /// 战斗会话自己在 <see cref="RoomCombatSession.SyncProgress"/> 里做同样的事,这个给城镇里的主角用。
        /// </summary>
        public static bool Apply(DnfAssets assets, TownHero hero, HeroProgress progress)
        {
            if (hero == null || progress == null)
            {
                return false;
            }
            string want = progress.WeaponPath ?? DemoHero.StarterWeapon;
            return !SameEquipPath(want, WeaponPathOf(hero.Outfit)) && TrySwapWeapon(assets, hero, want);
        }

        /// <summary>
        /// 探针用(宿主的 -dnfbagat):按装备列表的编号从小到大找一件这个进度的职业与等级穿得上、画面上也穿得出来的武器(不是现在穿着或起手的那件),
        /// 放进背包;最多看 <paramref name="maxScan"/> 件。放进去了返回 true;找不到或背包放不下返回 false。
        /// </summary>
        public static bool GrantWearableWeapon(DnfAssets assets, HeroProgress progress, int maxScan = 30000)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }
            var catalog = new ItemCatalog(new GameData(new Dnf.Script.PvfScriptSource(assets.Pvf)));
            string current = progress.WeaponPath ?? DemoHero.StarterWeapon;
            int scanned = 0;
            foreach (int code in catalog.EquipmentCodes())
            {
                if (++scanned > maxScan)
                {
                    break;
                }
                ItemInfo item = catalog.Get(code);
                if (item == null || item.Part != EquipmentParts.Weapon || SameEquipPath(item.Path, current))
                {
                    continue;
                }
                if (EquipRules.Check(item, progress.Job, progress.Level) != EquipResult.Ok || !CanShow(assets, item))
                {
                    continue;
                }
                return progress.AddItem(item);
            }
            return false;
        }

        /// <summary>主角外观里的武器是不是装备栏要的那件(没穿武器要的是起手武器);没有主角或没有进度返回 false。</summary>
        public static bool Matches(TownHero hero, HeroProgress progress)
        {
            if (hero == null || progress == null)
            {
                return false;
            }
            return SameEquipPath(progress.WeaponPath ?? DemoHero.StarterWeapon, WeaponPathOf(hero.Outfit));
        }

        /// <summary>
        /// 这件装备画面上穿不穿得出来(<see cref="HeroProgress.Equip"/> 的 canShow):不是武器一律可以;武器要能造出剑魂的外观,
        /// 并且 <see cref="RequiredWeaponMotions"/> 里每个动作在它的图层动画里至少有一个(否则那个动作上没有武器、也没有攻击框)。
        /// 读不到剑魂的角色脚本时一件武器也穿不出来(返回 false,不抛异常)。
        /// </summary>
        public static bool CanShow(DnfAssets assets, ItemInfo item)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (item == null)
            {
                return false;
            }
            if (item.Part != EquipmentParts.Weapon)
            {
                return true;
            }
            AvatarClass cls;
            try
            {
                cls = AvatarClass.FromChr(assets, DemoHero.SwordmanChrPath, DemoHero.SwordmanJobTag);
            }
            catch (Dnf.Data.DnfFormatException)
            {
                return false;
            }
            return CanShow(assets, item, cls, RequiredWeaponMotions);
        }

        /// <summary>同上,职业与要检查的动作由调用方给(单测与检查命令用)。</summary>
        public static bool CanShow(DnfAssets assets, ItemInfo item, AvatarClass cls, IEnumerable<string> motions)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (item == null || cls == null || motions == null)
            {
                return false;
            }
            if (item.Part != EquipmentParts.Weapon)
            {
                return true;
            }
            AvatarItem weapon = AvatarItem.Read(assets, cls, FullEquipPath(item.Path));
            if (weapon == null || !string.Equals(weapon.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var outfit = new AvatarOutfit(cls, 0, new[] { weapon });
            foreach (string motion in motions)
            {
                bool found = false;
                foreach (string path in outfit.WeaponAnimationPaths(motion))
                {
                    if (assets.LoadAni(path) != null)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
