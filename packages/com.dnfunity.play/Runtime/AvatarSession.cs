using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

// 本项目自己的设计(r15-avatarhost):把规则层的装扮栏(Dnf.Game 的 AvatarSlots / AvatarActions / AvatarBagView)与渲染层的衣柜(Dnf.Render 的 AvatarWardrobe)
// 接成一个会话,给宿主一个入口(穿、脱、同步到城镇主角、存档与读档)。两边互不引用,接法按 r14-avatar 的接线契约:
// canShow 钩子 = 用 ItemInfo.Path 读出 AvatarItem 再问衣柜;规则链先过、钩子后问;穿上或脱下成功之后才同步衣柜;穿不出来时背包、装扮栏、衣柜都不动。
// 这里不加规则:能不能穿、换下来的放哪一格,都由 AvatarActions 说了算。
namespace Dnf.Play
{
    /// <summary>从存档还原装扮的结果统计。</summary>
    public sealed class AvatarRestoreReport
    {
        public AvatarRestoreReport(int worn, int moved, int dropped)
        {
            Worn = worn;
            Moved = moved;
            Dropped = dropped;
        }

        /// <summary>穿回装扮栏的件数。</summary>
        public int Worn { get; }

        /// <summary>存档里穿着、但现在穿不回去(规则不过、画面上穿不出来、同部位有两行、数据变了)所以放回背包的件数。</summary>
        public int Moved { get; }

        /// <summary>找不到物品(编号不在用户的数据里)或背包放不下而丢掉的条数。</summary>
        public int Dropped { get; }
    }

    /// <summary>
    /// 装扮会话:装扮栏(<see cref="Slots"/>)、衣柜(<see cref="Wardrobe"/>)与主角的进度(<see cref="Progress"/>:背包、职业、等级、穿着的武器)。
    /// 与普通装备栏互不相干:这里不读也不写 <see cref="HeroProgress.Worn"/>(装备栏的格子、版本号、属性都不动),存档里用独立的 <c>avatar</c> 行。
    /// <para>
    /// 衣柜的底座(默认外观加武器)在构造时造好一次;之后主角换了武器,<see cref="Sync"/> 经 <see cref="AvatarWardrobe.SetWeapon"/> 换底座,穿着的装扮保留
    /// (不能像 <see cref="HeroLook.Apply"/> 那样直接改主角的外观,否则衣柜里的底座就和主角脱节了)。
    /// </para>
    /// <para>
    /// 接进宿主的写法:读档时 <c>progress.Restore(save, lookup); avatars.Restore(save, lookup);</c>,存档时 <c>var save = progress.Snapshot(); avatars.Capture(save);</c>
    /// (<see cref="HeroSaver.Capture"/> 做第二步);穿脱之后 <c>avatars.Sync(hero)</c>;城镇世界每次造会话也 Sync(<see cref="AvatarTownWorld"/>)。
    /// </para>
    /// </summary>
    public sealed class AvatarSession
    {
        private readonly DnfAssets _assets;
        private readonly AvatarWardrobe _wardrobe;
        private readonly string _starterWeapon;
        private readonly Dictionary<int, bool> _showable = new Dictionary<int, bool>();

        /// <summary>用游戏数据里的默认外观(<see cref="DemoHero.CreateOutfit"/>,起手武器)做衣柜底座。</summary>
        public AvatarSession(DnfAssets assets, HeroProgress progress)
            : this(assets, progress, null)
        {
        }

        /// <param name="assets">游戏数据。</param>
        /// <param name="progress">主角的进度(背包、职业、等级、穿着的武器)。</param>
        /// <param name="baseOutfit">衣柜底座的工厂(默认外观加武器);没给就用 <see cref="DemoHero.CreateOutfit"/>。读不出底座(角色脚本不在)时衣柜是 null、<see cref="Ready"/> 为 false。</param>
        /// <param name="starterWeapon">没穿武器时外观上的那把(装备脚本路径);没给就是 <see cref="DemoHero.StarterWeapon"/>,与 <see cref="HeroLook.Apply"/> 一致。测试里换成合成素材里的那一把。</param>
        public AvatarSession(DnfAssets assets, HeroProgress progress, Func<AvatarOutfit> baseOutfit, string starterWeapon = null)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            Progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _starterWeapon = starterWeapon ?? DemoHero.StarterWeapon;
            try
            {
                AvatarOutfit start = baseOutfit != null ? baseOutfit() : DemoHero.CreateOutfit(assets);
                _wardrobe = start == null ? null : new AvatarWardrobe(assets, start);
            }
            catch (Dnf.Data.DnfFormatException)
            {
                _wardrobe = null;
            }
        }

        public HeroProgress Progress { get; }

        /// <summary>装扮栏(部位 0 到 9);规则层的容器,只有 <see cref="Wear"/>、<see cref="TakeOff"/>、<see cref="Restore(HeroSave, Func{int, ItemInfo})"/> 会改它。</summary>
        public AvatarSlots Slots { get; } = new AvatarSlots();

        /// <summary>衣柜;读不出底座时为 null。</summary>
        public AvatarWardrobe Wardrobe => _wardrobe;

        /// <summary>装扮功能能不能用(衣柜造出来了)。不能用时穿一律得到 <see cref="EquipResult.NotShowable"/>(规则链没过的仍是规则链的结果)。</summary>
        public bool Ready => _wardrobe != null;

        /// <summary>背包装备页里的时装与装扮栏的视图(调用时刻的快照;背包或装扮栏变了要重建)。职业与等级取主角现在的。</summary>
        public AvatarBagView View()
        {
            return AvatarBagView.Build(Progress.Bag, Slots, Progress.Job, Progress.Level);
        }

        /// <summary>
        /// 这件时装在这个职业的画面上穿不穿得出来(脚本读得出来、衣柜也穿得出来,即 <see cref="Wear"/> 里问规则层的那个钩子的答案)。同一个物品编号只读一次脚本、结果记下来
        /// (它只取决于脚本与素材,不随穿着变),所以界面可以每帧问光标处那一件。没有衣柜时不行。
        /// </summary>
        public bool CanShow(ItemInfo info)
        {
            if (info == null || _wardrobe == null)
            {
                return false;
            }
            if (!_showable.TryGetValue(info.Code, out bool ok))
            {
                ok = TryShow(info, out _);
                _showable[info.Code] = ok;
            }
            return ok;
        }

        /// <summary>
        /// 把背包装备页第 <paramref name="bagIndex"/> 格的时装穿上:交给 <see cref="AvatarActions.Wear"/>(规则链,再问一句"这个职业的画面上穿不穿得出来"),
        /// 通过之后把同一件读成的 <see cref="AvatarItem"/> 交给衣柜。结果原样返回:不是 <see cref="EquipResult.Ok"/> 时背包、装扮栏、衣柜都没动。
        /// </summary>
        public EquipResult Wear(int bagIndex)
        {
            AvatarItem shown = null;
            EquipResult result = AvatarActions.Wear(Progress.Bag, Slots, bagIndex, Progress.Job, Progress.Level, info => TryShow(info, out shown));
            if (result == EquipResult.Ok)
            {
                _wardrobe.Wear(shown);
            }
            return result;
        }

        /// <summary>把 <paramref name="part"/> 号槽的时装脱下放回背包(<see cref="AvatarActions.TakeOff"/>),成功后衣柜也脱下;槽是空的或背包装备页没空格返回 false。</summary>
        public bool TakeOff(int part)
        {
            if (!AvatarActions.TakeOff(Progress.Bag, Slots, part))
            {
                return false;
            }
            _wardrobe?.TakeOff(part);
            return true;
        }

        /// <summary>
        /// 让主角的外观与装备栏、装扮栏一致:先让衣柜的底座穿上装备栏里的武器(没穿退回起手武器;读不出这件武器的外观就保持原样,同 <see cref="HeroLook.Apply"/>),
        /// 再把衣柜现在的外观赋给 <paramref name="hero"/>。已经一致(或没有主角、衣柜)什么也不做。返回有没有换主角的外观。
        /// </summary>
        public bool Sync(TownHero hero)
        {
            if (hero == null || _wardrobe == null)
            {
                return false;
            }
            AlignWeapon();
            if (ReferenceEquals(hero.Outfit, _wardrobe.Current))
            {
                return false;
            }
            hero.Outfit = _wardrobe.Current;
            return true;
        }

        /// <summary>
        /// 穿着装备的攻击与暴击加总(<see cref="HeroPanel.WornEquipment(System.Func{string, EquipmentStats}, TownHero, HeroProgress)"/>),但不算装扮件:
        /// 装扮进了主角的外观之后,面板读外观里每一件的脚本,装扮的属性加成本版本不做(规则层约定),所以这里把装扮件的数值当 0。
        /// </summary>
        public EquipmentStats WornStats(TownHero hero)
        {
            return WornStats(path => EquipmentStats.Read(_assets, path), hero);
        }

        /// <summary>同上,单件装备的数值由 <paramref name="read"/> 读(测试里用合成的)。</summary>
        public EquipmentStats WornStats(Func<string, EquipmentStats> read, TownHero hero)
        {
            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }
            return HeroPanel.WornEquipment(path => IsWornAvatar(path) ? default : read(path), hero, Progress);
        }

        /// <summary>
        /// 把装扮栏写进存档(先清掉 <paramref name="save"/> 里原有的装扮字段):每个有东西的部位一行,部位号从小到大。
        /// 接在 <see cref="HeroProgress.Snapshot"/> 之后(<see cref="HeroSaver.Capture"/>)。
        /// </summary>
        public void Capture(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            save.Avatars.Clear();
            for (int part = AvatarSlots.First; part <= AvatarSlots.Last; part++)
            {
                ItemInfo worn = Slots.Get(part);
                if (worn != null)
                {
                    save.Avatars.Add(new SavedAvatar(part, worn.Code));
                }
            }
        }

        /// <summary>
        /// 把存档里的装扮还原到这个会话上(整个替换装扮栏与衣柜的穿着);<paramref name="lookup"/> 按物品编号取物品。接在 <see cref="HeroProgress.Restore"/> 之后
        /// (背包已经还原好)。每一行照 <see cref="HeroProgress.Restore"/> 对穿着装备的做法:规则链通过(<see cref="AvatarRules.Check(ItemInfo, int, int, int)"/>)、
        /// 部位还空着、画面上穿得出来,才穿回去;穿不回去的放回背包,编号找不到或背包放不下的丢掉,数目在返回值里。
        /// </summary>
        public AvatarRestoreReport Restore(HeroSave save, Func<int, ItemInfo> lookup)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }
            Slots.Clear();
            _wardrobe?.Clear();
            int worn = 0;
            int moved = 0;
            int dropped = 0;
            foreach (SavedAvatar saved in save.Avatars)
            {
                ItemInfo info = lookup(saved.Code);
                AvatarItem shown = null;
                if (info == null)
                {
                    dropped++;
                }
                else if (Slots.Get(saved.Part) == null
                    && AvatarRules.Check(info, saved.Part, Progress.Job, Progress.Level) == EquipResult.Ok
                    && TryShow(info, out shown)
                    && Slots.Restore(saved.Part, info))
                {
                    _wardrobe.Wear(shown);
                    worn++;
                }
                else if (Progress.Bag.RestoreStack(info, 1) == 1)
                {
                    moved++;
                }
                else
                {
                    dropped++;
                }
            }
            return new AvatarRestoreReport(worn, moved, dropped);
        }

        /// <summary>宿主用:物品按编号从用户的数据里读(<see cref="ItemCatalog.Get"/>),其余同 <see cref="Restore(HeroSave, Func{int, ItemInfo})"/>。存档里没有装扮行时不读目录。</summary>
        public AvatarRestoreReport RestoreFromData(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            if (save.Avatars.Count == 0)
            {
                return Restore(save, code => null);
            }
            var catalog = new ItemCatalog(new GameData(new Dnf.Script.PvfScriptSource(_assets.Pvf)));
            return Restore(save, catalog.Get);
        }

        /// <summary>这件时装的脚本读得出来、衣柜也穿得出来(<see cref="AvatarWardrobe.CanShow"/>);读出来的 <see cref="AvatarItem"/> 在 <paramref name="shown"/> 里。没有衣柜时不行。</summary>
        private bool TryShow(ItemInfo info, out AvatarItem shown)
        {
            shown = _wardrobe == null || info == null ? null : AvatarItem.Read(_assets, _wardrobe.Base.Class, info.Path);
            return shown != null && _wardrobe.CanShow(shown);
        }

        /// <summary>衣柜的底座穿装备栏里的武器(没穿用起手武器);读不出这件武器的外观(或它不是武器)时保持原样。</summary>
        private void AlignWeapon()
        {
            string want = Progress.WeaponPath ?? _starterWeapon;
            if (HeroLook.SameEquipPath(want, HeroLook.WeaponPathOf(_wardrobe.Base)))
            {
                return;
            }
            string path = want.Replace('\\', '/');
            if (!path.StartsWith("equipment/", StringComparison.OrdinalIgnoreCase))
            {
                path = DemoHero.SwordmanEquipRoot + "/" + path;
            }
            AvatarItem weapon = AvatarItem.Read(_assets, _wardrobe.Base.Class, path);
            if (weapon != null && string.Equals(weapon.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase))
            {
                _wardrobe.SetWeapon(weapon);
            }
        }

        private bool IsWornAvatar(string path)
        {
            foreach (ItemInfo item in Slots.Items)
            {
                if (HeroLook.SameEquipPath(item.Path, path))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
