using System;

// 证据(客户端静态读码,函数地址见 out/enhance/NOTES.md;原版数值不入库):
//  · 强化等级换算成属性,发生在客户端生成装备属性集的函数(0x0078d4a0)里:按装备的部位分四路,把强化表(etc/upgrade.etc)一行里的几格
//    写进属性集里专门的几个格子(不是脚本里那些基础属性格);属性集再由聚合函数(0x00785150)逐格加进角色的累加记录。
//    行里取哪一格:武器用 A(整数,再乘按职业与武器种类分档的倍率,见 IWeaponUpgradeRate)、防具(部位 12 到 16)用 f1(整数)与 f2(浮点)、
//    首饰(17 到 19)用 f3(整数)与 f4(浮点)、辅助装备与魔法石(20、21)用 f5(整数);称号(11)与宠物不写。表里 B 组(B g1…g5)同样处理,写进属性集里"第二档"的格子。
//  · 表的行下标 = 装备的强化等级 − 1(强化 0 级没有行,不加属性);整数项向零截断,单精度浮点运算。
//  · 整数项落进哪一种角色属性,靠两条观察推出(推断):属性集里这几个格子的次序与装备脚本里装备攻击防御四项的次序平行;
//    全库装备脚本里,武器几乎都带装备物理攻击与装备魔法攻击,防具几乎都带装备物理防御、首饰几乎都带装备魔法防御,
//    辅助装备与魔法石全都带四项基础属性——与客户端"武器写两格、防具写两格、首饰写两格、辅助写一格并加进四个基础属性累加格"的分路一一对得上。
//  · 防具的 f2、首饰的 f4(浮点)客户端以浮点加进另外的累加格,含义没有查明(未知),所以这里只记下数、不折进 EquipStats。
//  · 两个组(A 与 B)是普通与另一档(客户端在某个全局模式下读第二档,推断为对战模式)的两套;脚本里装备攻击防御的"两个数"也是这两档,
//    所以投影成 EquipStats 时第一个数是 A 组、第二个数是 B 组。
//  · 没有查明的:放大(增幅)过的装备是否改用增幅表;成长型装备按角色等级线性换算的那一段;这些数在角色伤害里最终怎么被用。
namespace Dnf.Game
{
    /// <summary>强化在某一档(A 组或 B 组)下带给一件装备的各项,都是整数(浮点项另记)。</summary>
    public readonly struct UpgradeChannels : IEquatable<UpgradeChannels>
    {
        public UpgradeChannels(int physicalAttack, int magicalAttack, int physicalDefense, float armorFloat, int magicalDefense, float accessoryFloat, int allStats)
        {
            PhysicalAttack = physicalAttack;
            MagicalAttack = magicalAttack;
            PhysicalDefense = physicalDefense;
            ArmorFloat = armorFloat;
            MagicalDefense = magicalDefense;
            AccessoryFloat = accessoryFloat;
            AllStats = allStats;
        }

        /// <summary>武器的强化物理攻击(强化表 A × 倍率,向零取整)。</summary>
        public int PhysicalAttack { get; }

        /// <summary>武器的强化魔法攻击。</summary>
        public int MagicalAttack { get; }

        /// <summary>防具的强化物理防御(强化表 f1,向零取整)。</summary>
        public int PhysicalDefense { get; }

        /// <summary>防具的浮点项(强化表 f2,含义没有查明)。</summary>
        public float ArmorFloat { get; }

        /// <summary>首饰的强化魔法防御(强化表 f3,向零取整)。</summary>
        public int MagicalDefense { get; }

        /// <summary>首饰的浮点项(强化表 f4,含义没有查明)。</summary>
        public float AccessoryFloat { get; }

        /// <summary>辅助装备与魔法石的强化四维(强化表 f5,向零取整,加进四项基础属性各一份)。</summary>
        public int AllStats { get; }

        /// <summary>所有项都是 0。</summary>
        public bool IsEmpty =>
            PhysicalAttack == 0 && MagicalAttack == 0 && PhysicalDefense == 0 && ArmorFloat == 0f && MagicalDefense == 0 && AccessoryFloat == 0f && AllStats == 0;

        public bool Equals(UpgradeChannels other)
        {
            return PhysicalAttack == other.PhysicalAttack && MagicalAttack == other.MagicalAttack && PhysicalDefense == other.PhysicalDefense
                && ArmorFloat.Equals(other.ArmorFloat) && MagicalDefense == other.MagicalDefense && AccessoryFloat.Equals(other.AccessoryFloat)
                && AllStats == other.AllStats;
        }

        public override bool Equals(object obj) => obj is UpgradeChannels other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = PhysicalAttack;
                h = (h * 31) + MagicalAttack;
                h = (h * 31) + PhysicalDefense;
                h = (h * 31) + ArmorFloat.GetHashCode();
                h = (h * 31) + MagicalDefense;
                h = (h * 31) + AccessoryFloat.GetHashCode();
                h = (h * 31) + AllStats;
                return h;
            }
        }

        public override string ToString()
        {
            return "物攻" + PhysicalAttack + " 魔攻" + MagicalAttack + " 物防" + PhysicalDefense + " 魔防" + MagicalDefense + " 四维" + AllStats
                + " 浮点(" + ArmorFloat.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                + AccessoryFloat.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// 一件装备按强化等级得到的属性(A、B 两组),见文件头的证据。<see cref="Of"/> 从装备与强化表算出,
    /// <see cref="ToStats"/> 投影成 <see cref="EquipStats"/>(第一个数 A 组、第二个数 B 组)交给 <see cref="EquipBonusOverlay"/>。
    /// </summary>
    public sealed class EquipUpgradeEffect
    {
        /// <summary>没有强化属性。</summary>
        public static readonly EquipUpgradeEffect None = new EquipUpgradeEffect(0, default, default);

        private EquipUpgradeEffect(int level, UpgradeChannels normal, UpgradeChannels alternate)
        {
            Level = level;
            Normal = normal;
            Alternate = alternate;
        }

        /// <summary>算出这些数时的强化等级。</summary>
        public int Level { get; }

        /// <summary>A 组(普通那一档)。</summary>
        public UpgradeChannels Normal { get; }

        /// <summary>B 组(另一档)。</summary>
        public UpgradeChannels Alternate { get; }

        public bool IsEmpty => Normal.IsEmpty && Alternate.IsEmpty;

        /// <summary>
        /// 按强化表算一件装备的强化属性。不是装备、没有强化(0 级)、部位不在 10 到 21、强化等级超出表(表的行下标 = 等级 − 1)、
        /// 稀有度或装备等级超出表的范围时返回 <see cref="None"/>。武器用 <paramref name="weaponRate"/> 取倍率(不给按 1)。
        /// 装备等级取 <see cref="ItemInfo.Grade"/>(成长型装备的线性换算没做)。
        /// </summary>
        public static EquipUpgradeEffect Of(ItemInfo item, UpgradeTable table, IWeaponUpgradeRate weaponRate = null)
        {
            if (item == null || table == null || !item.IsEquipment || item.Instance == null)
            {
                return None;
            }
            int level = item.Instance.Upgrade;
            if (level <= 0)
            {
                return None;
            }
            UpgradeInfo info = table.GetInfo(item.Rarity, item.Grade, level - 1, item.Part);
            if (info == null)
            {
                return None;
            }
            IWeaponUpgradeRate rate = weaponRate ?? FlatWeaponUpgradeRate.One;
            UpgradeChannels normal = ChannelsOf(info.StatsA, item, rate);
            UpgradeChannels alternate = ChannelsOf(info.StatsB, item, rate);
            return normal.IsEmpty && alternate.IsEmpty ? None : new EquipUpgradeEffect(level, normal, alternate);
        }

        /// <summary>
        /// 一组(8 格,排法见 <see cref="UpgradeInfo.StatsA"/>)按部位分路:武器 [0],防具 [1] 与 [5],首饰 [2] 与 [6],辅助与魔法石 [3];
        /// 整数项向零截断。武器要装备自己带对应的基础攻击(物理或魔法,取整后不为 0)才有那一路(客户端的属性集里都是整数)。
        /// </summary>
        public static UpgradeChannels ChannelsOf(float[] group, ItemInfo item, IWeaponUpgradeRate weaponRate)
        {
            if (group == null)
            {
                throw new ArgumentNullException(nameof(group));
            }
            if (group.Length < 8)
            {
                throw new ArgumentException("一组要有 8 格", nameof(group));
            }
            if (item == null)
            {
                return default;
            }
            int part = item.Part;
            if (part == EquipmentParts.Weapon)
            {
                int physical = 0;
                int magical = 0;
                IWeaponUpgradeRate rate = weaponRate ?? FlatWeaponUpgradeRate.One;
                if ((int)item.Stats[EquipStat.EquipmentPhysicalAttack] != 0)
                {
                    physical = (int)(float)(group[0] * rate.RateOf(item, true));
                }
                if ((int)item.Stats[EquipStat.EquipmentMagicalAttack] != 0)
                {
                    magical = (int)(float)(group[0] * rate.RateOf(item, false));
                }
                return new UpgradeChannels(physical, magical, 0, 0f, 0, 0f, 0);
            }
            if (part >= EquipmentParts.Coat && part <= EquipmentParts.Waist)
            {
                return new UpgradeChannels(0, 0, (int)group[1], group[5], 0, 0f, 0);
            }
            if (part >= EquipmentParts.Amulet && part <= EquipmentParts.Ring)
            {
                return new UpgradeChannels(0, 0, 0, 0f, (int)group[2], group[6], 0);
            }
            if (part == EquipmentParts.Support || part == EquipmentParts.MagicStone)
            {
                return new UpgradeChannels(0, 0, 0, 0f, 0, 0f, (int)group[3]);
            }
            return default;
        }

        /// <summary>
        /// 投影成属性集:武器的物理攻击、魔法攻击进装备物理攻击(<see cref="EquipStat.EquipmentPhysicalAttack"/>)、装备魔法攻击;
        /// 防具的整数项进装备物理防御、首饰的进装备魔法防御;辅助与魔法石的四维进物攻、魔攻、物防、魔防四项基础属性各一份。
        /// 每一项第一个数是 A 组、有 B 组的数时第二个数是 B 组。两个浮点项不投影(含义没有查明)。
        /// </summary>
        public EquipStats ToStats()
        {
            if (IsEmpty)
            {
                return EquipStats.Empty;
            }
            var b = new EquipStats.Builder();
            Put(b, EquipStat.EquipmentPhysicalAttack, Normal.PhysicalAttack, Alternate.PhysicalAttack);
            Put(b, EquipStat.EquipmentMagicalAttack, Normal.MagicalAttack, Alternate.MagicalAttack);
            Put(b, EquipStat.EquipmentPhysicalDefense, Normal.PhysicalDefense, Alternate.PhysicalDefense);
            Put(b, EquipStat.EquipmentMagicalDefense, Normal.MagicalDefense, Alternate.MagicalDefense);
            Put(b, EquipStat.PhysicalAttack, Normal.AllStats, Alternate.AllStats);
            Put(b, EquipStat.PhysicalDefense, Normal.AllStats, Alternate.AllStats);
            Put(b, EquipStat.MagicalAttack, Normal.AllStats, Alternate.AllStats);
            Put(b, EquipStat.MagicalDefense, Normal.AllStats, Alternate.AllStats);
            return b.Build();
        }

        private static void Put(EquipStats.Builder builder, EquipStat stat, int first, int second)
        {
            if (first == 0 && second == 0)
            {
                return;
            }
            if (second == 0)
            {
                builder.Set(stat, first);
            }
            else
            {
                builder.Set(stat, first, second);
            }
        }
    }
}
