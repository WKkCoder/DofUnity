using System;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 一件(或几件)装备带来的攻击与暴击(取自装备脚本 .equ 的 [equipment physical attack]、[equipment magical attack]、
    /// [physical critical hit]、[magical critical hit],每项取第一个数;证据 E-110)。读不到脚本或没写的项为 0。
    /// [equipment …attack] 后面有两个数,这里只取第一个,另一个的含义没有核对。
    /// </summary>
    public readonly struct EquipmentStats
    {
        public int PhysicalAttack { get; }
        public int MagicalAttack { get; }
        public float PhysicalCritical { get; }
        public float MagicalCritical { get; }

        public EquipmentStats(int physicalAttack, int magicalAttack, float physicalCritical = 0f, float magicalCritical = 0f)
        {
            PhysicalAttack = physicalAttack;
            MagicalAttack = magicalAttack;
            PhysicalCritical = physicalCritical;
            MagicalCritical = magicalCritical;
        }

        /// <summary>两件装备的加总。</summary>
        public EquipmentStats Plus(EquipmentStats other)
        {
            return new EquipmentStats(PhysicalAttack + other.PhysicalAttack, MagicalAttack + other.MagicalAttack,
                PhysicalCritical + other.PhysicalCritical, MagicalCritical + other.MagicalCritical);
        }

        /// <summary>读一件装备脚本;路径为空、读不到时全是 0。</summary>
        public static EquipmentStats Read(DnfAssets assets, string equPath)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            ScriptLine[] doc = string.IsNullOrEmpty(equPath) ? null : assets.ReadScript(equPath);
            return doc == null ? default : From(new TagTable(doc));
        }

        /// <summary>按已经索引好的装备脚本取。</summary>
        public static EquipmentStats From(TagTable tags)
        {
            if (tags == null)
            {
                throw new ArgumentNullException(nameof(tags));
            }
            return new EquipmentStats((int)Number(tags, "[equipment physical attack]"), (int)Number(tags, "[equipment magical attack]"),
                (float)Number(tags, "[physical critical hit]"), (float)Number(tags, "[magical critical hit]"));
        }

        private static double Number(TagTable tags, string tag)
        {
            if (tags.TryGetLine(tag, out ScriptLine line))
            {
                double? n = line.FirstNumber();
                if (n.HasValue)
                {
                    return n.Value;
                }
            }
            return 0.0;
        }
    }

    /// <summary>
    /// 主角打怪物时的攻击面板(证据 E-110),交给 <see cref="HitDamage"/> 当攻击者:
    /// 面板攻击力取自角色脚本 .chr 的 [initial value](第 1 级的初始值,向零取整成整数,与客户端属性记录"整数 × 倍率"取整的存法一致),
    /// 附加基础值(武器的攻击力)与暴击属性是穿着的装备脚本里相应项的加总,元素攻击为 0。
    /// <b>没做</b>:等级成长([growtype]/[awakening] 各段的逐级增量。服务端的 <c>CalcStat</c> 是"初始值 + 每升一级加一段增量,1 到 14 级用第一个成长类型,
    /// 15 到 49 级用所选类型,50 级起用觉醒段"——规则已读到,等有升级再接)、属性点与装备的其余加成(客户端把这些聚合成属性记录的函数没有找到,
    /// 服务端不计算也不核对)。所以这里只对一级的新角色成立。
    /// </summary>
    public sealed class HeroPanel
    {
        public int Level { get; }

        /// <summary>面板物理攻击力([physical attack] 取整)。</summary>
        public int PhysicalAttack { get; }

        /// <summary>面板魔法攻击力([magical attack] 取整)。</summary>
        public int MagicalAttack { get; }

        /// <summary>穿着装备的攻击与暴击加总。</summary>
        public EquipmentStats Equipment { get; }

        public HeroPanel(int level, int physicalAttack, int magicalAttack, EquipmentStats equipment)
        {
            Level = level < 1 ? 1 : level;
            PhysicalAttack = physicalAttack;
            MagicalAttack = magicalAttack;
            Equipment = equipment;
        }

        /// <summary>
        /// 读 .chr 的 [initial value] 里 [physical attack]、[magical attack](同名标签取第一次出现,正好是初始值那一节;没写取 0),
        /// 再配上装备。.chr 里这两项在脚本里可能存成整数也可能是浮点数,一律按"第一个数值"读。
        /// </summary>
        public static HeroPanel FromChr(CharacterDef chr, int level, EquipmentStats equipment)
        {
            if (chr == null)
            {
                return new HeroPanel(level, 0, 0, equipment);
            }
            TagTable tags = chr.Tags;
            return new HeroPanel(level, Truncate(NumberOr(tags, "[physical attack]")), Truncate(NumberOr(tags, "[magical attack]")), equipment);
        }

        /// <summary>主角穿着的全部装备(<see cref="AvatarOutfit"/> 里每一件的 .equ)的攻击与暴击加总;没有穿装备时是 0。</summary>
        public static EquipmentStats WornEquipment(DnfAssets assets, TownHero hero)
        {
            var total = default(EquipmentStats);
            if (hero?.Outfit == null)
            {
                return total;
            }
            foreach (AvatarItem item in hero.Outfit.Items)
            {
                total = total.Plus(EquipmentStats.Read(assets, item.EquPath));
            }
            return total;
        }

        /// <summary>攻击者面板:角色(0x111),附加基础值与暴击属性取装备,元素攻击为 0。</summary>
        public AttackerPanel ToAttacker()
        {
            return new AttackerPanel
            {
                Level = Level,
                PhysicalAttack = PhysicalAttack,
                MagicalAttack = MagicalAttack,
                PhysicalBonusBase = Equipment.PhysicalAttack,
                MagicalBonusBase = Equipment.MagicalAttack,
                PhysicalCritical = Equipment.PhysicalCritical,
                MagicalCritical = Equipment.MagicalCritical,
            };
        }

        private static double NumberOr(TagTable tags, string tag)
        {
            if (tags.TryGetLine(tag, out ScriptLine line))
            {
                double? n = line.FirstNumber();
                if (n.HasValue)
                {
                    return n.Value;
                }
            }
            return 0.0;
        }

        private static int Truncate(double value)
        {
            if (double.IsNaN(value))
            {
                return 0;
            }
            return value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;
        }
    }
}
