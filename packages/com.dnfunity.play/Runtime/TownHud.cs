using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

// 证据:E-088、E-111、E-112、E-120 的底栏(基线城镇帧与副本帧是同一条底栏:血蓝球、等级牌、经验条、SP、物品快捷槽、菜单与商城按钮、技能槽与热键字母;
// 比较用的是同一套帧号与位置,逐像素对过的是副本帧)。城镇里没有战斗,所以这里只给"站着的主角"的状态:血蓝满、技能槽都亮、没有冷却。
// 回城后血蓝是不是总是满的没有读到证据(进副本时 RoomCombatSession 给的是满的),这里按满画,是推断。
namespace Dnf.Play
{
    /// <summary>底栏各槽的显示状态(城镇与副本共用)。</summary>
    public static class HudStates
    {
        /// <summary>
        /// 物品快捷槽此刻的显示状态(证据 E-120):快捷栏里每个放了药水的格子——图标、背包里这种药水的件数、冷却还剩的比例。
        /// 没有等级进度(没有背包)时返回空表。
        /// </summary>
        public static List<HudItemState> ItemSlots(HeroProgress progress)
        {
            var states = new List<HudItemState>(QuickSlots.Count);
            if (progress == null)
            {
                return states;
            }
            for (int i = 0; i < QuickSlots.Count; i++)
            {
                ItemInfo item = progress.Quick[i];
                if (item != null)
                {
                    states.Add(new HudItemState(i, item.IconKey, item.IconFrame, progress.Bag.CountOf(item.Code), progress.Cooldowns.Fraction(item)));
                }
            }
            return states;
        }

        /// <summary>
        /// 技能槽的显示状态:每个槽都亮(能放)、没有冷却、小字不变灰,带上技能的等级。<paramref name="skills"/> 为 null 时返回空表。
        /// 副本里的状态(冷却、魔法够不够、主角腾不腾得出手)由战斗会话自己算(<c>RoomCombatSession.SkillSlotStates</c>)。
        /// </summary>
        public static List<HudSkillState> IdleSkillSlots(HeroSkills skills)
        {
            var states = new List<HudSkillState>(skills == null ? 0 : skills.Slots.Count);
            if (skills == null)
            {
                return states;
            }
            for (int i = 0; i < skills.Slots.Count; i++)
            {
                HeroSkillSlot s = skills.Slots[i];
                states.Add(new HudSkillState(i, s.Def, true, 0.0, s.Level, false));
            }
            return states;
        }
    }

    /// <summary>
    /// 城镇里的原版底栏(M30):和副本里同一条(<see cref="HudArt"/>),只是状态是"站在城镇里"的——血球魔法球是满的、技能槽亮着、没有冷却;
    /// 等级、经验条、SP 与物品快捷槽取自等级进度(<see cref="HeroProgress"/>),所以在副本里打到的经验、买的药水回城后就看得见。
    /// 技能槽里的图标与等级取角色脚本 [initial value] 里的初始技能表(同 <see cref="HeroSkills.Create"/>),整个会话只读一次。
    /// 画的位置在屏幕最下面 91 行,盖住城镇画面底部那条没有地图的暗带。取不到底栏的图包时 <see cref="TryCreate"/> 返回 null,城镇照旧不画底栏。
    /// </summary>
    public sealed class TownHud
    {
        private readonly HudArt _hud;
        private readonly HeroSkills _skills;
        private int _vitalsLevel = -1;
        private HeroVitals _vitals;

        public TownHud(HudArt hud, HeroSkills skills)
        {
            _hud = hud ?? throw new System.ArgumentNullException(nameof(hud));
            _skills = skills ?? new HeroSkills(new HeroSkillSlot[0]);
        }

        /// <summary>技能表(槽的个数与等级;日志与测试用)。</summary>
        public HeroSkills Skills => _skills;

        /// <summary>
        /// 从数据目录造一个:底栏图取自图包,技能槽按角色 0 的初始技能表排。读不到底栏的图时返回 null;读不到角色或技能时底栏照画,只是技能槽是空的。
        /// </summary>
        public static TownHud TryCreate(DnfAssets assets)
        {
            if (assets == null)
            {
                return null;
            }
            HudArt hud = HudArt.For(assets);
            if (hud == null)
            {
                return null;
            }
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            return new TownHud(hud, HeroSkills.Create(data, data.GetCharacter(0), 0));
        }

        /// <summary>
        /// 把底栏的精灵加进 <paramref name="into"/>(<paramref name="order"/> 一路递增);<paramref name="progress"/> 为 null 时什么也不画,返回 false。
        /// </summary>
        public bool Build(List<SceneSprite> into, ref int order, HeroProgress progress)
        {
            if (into == null || progress == null)
            {
                return false;
            }
            if (_vitals == null || _vitalsLevel != progress.Level)
            {
                _vitals = HeroVitals.FromStats(progress.Level, progress.Stats);
                _vitalsLevel = progress.Level;
            }
            HudExp exp = new HudExp(progress.ExpInLevel, progress.LevelSpan, progress.Level == Leveling.MaxLevel);
            _hud.Build(into, ref order, _vitals.Level, _vitals.HpMax, _vitals.HpMax, _vitals.MpMax, _vitals.MpMax, HudStates.IdleSkillSlots(_skills), exp,
                progress.SkillPoints, HudStates.ItemSlots(progress));
            return true;
        }
    }
}
