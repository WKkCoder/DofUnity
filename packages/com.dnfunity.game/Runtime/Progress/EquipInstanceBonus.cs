using System;

namespace Dnf.Game
{
    /// <summary>
    /// 强化等级与品级值换算成属性的默认实现(<see cref="IEquipInstanceBonus"/>):一件穿着的装备身上有两样会变的东西,各给一份附加属性——
    /// <list type="bullet">
    /// <item>强化等级:查强化表,按部位分路(<see cref="EquipUpgradeEffect"/>);</item>
    /// <item>品级值:按"[stat random variation]"浮动基础属性(<see cref="EquipGradeVariation"/>),给出的是相对基础值的差。</item>
    /// </list>
    /// 两份加在一起交给 <see cref="EquipBonusOverlay"/>;<see cref="EquipStats"/> 的折算与 <c>HeroProgress</c> 的汇总结构都没动,
    /// 这些数像别的附加项一样先各自折成角色属性再相加。依据来自客户端读码,说明见 <see cref="EquipUpgradeEffect"/> 与 <see cref="EquipGradeVariation"/> 的文件头。
    /// <para>
    /// 改了开关或换了数据(<see cref="GradeVariation"/>、<see cref="WeaponRate"/>)之后,要调用 <see cref="EquipBonusOverlay.Invalidate"/> 让汇总重算。
    /// </para>
    /// </summary>
    public sealed class EquipInstanceBonus : IEquipInstanceBonus
    {
        private readonly Func<UpgradeTable> _table;

        /// <param name="table">强化表的来源(每次算都取一次,宿主换表后不用重建);给 null 或取到 null 就没有强化属性。</param>
        /// <param name="gradeVariation">品级浮动的数据;不给就没有品级浮动。</param>
        /// <param name="weaponRate">武器强化攻击的倍率;不给按 1。</param>
        public EquipInstanceBonus(Func<UpgradeTable> table, EquipGradeVariation gradeVariation = null, IWeaponUpgradeRate weaponRate = null)
        {
            _table = table;
            GradeVariation = gradeVariation;
            WeaponRate = weaponRate;
        }

        /// <summary>用一张固定的强化表造。</summary>
        public static EquipInstanceBonus For(UpgradeTable table, EquipGradeVariation gradeVariation = null, IWeaponUpgradeRate weaponRate = null)
        {
            return new EquipInstanceBonus(() => table, gradeVariation, weaponRate);
        }

        /// <summary>品级浮动的数据;null 表示没有。</summary>
        public EquipGradeVariation GradeVariation { get; set; }

        /// <summary>武器强化攻击的倍率;null 按 1。</summary>
        public IWeaponUpgradeRate WeaponRate { get; set; }

        /// <summary>强化等级的属性是否计入(默认计入)。</summary>
        public bool UpgradeEnabled { get; set; } = true;

        /// <summary>品级值的浮动是否计入(默认计入)。</summary>
        public bool GradeEnabled { get; set; } = true;

        /// <summary>一件装备按强化等级得到的属性(没有强化表或没有强化返回 <see cref="EquipUpgradeEffect.None"/>)。不看 <see cref="UpgradeEnabled"/>。</summary>
        public EquipUpgradeEffect UpgradeOf(ItemInfo worn)
        {
            UpgradeTable table = _table?.Invoke();
            return EquipUpgradeEffect.Of(worn, table, WeaponRate);
        }

        /// <summary>一件装备按品级值浮动的差(没有浮动数据或没有品级值是空)。不看 <see cref="GradeEnabled"/>。</summary>
        public EquipStats GradeDeltaOf(ItemInfo worn)
        {
            EquipGradeVariation variation = GradeVariation;
            return variation == null ? EquipStats.Empty : variation.DeltaOf(worn);
        }

        public EquipStats BonusOf(ItemInfo worn)
        {
            if (worn == null || !worn.IsEquipment || worn.Instance == null)
            {
                return null;
            }
            EquipStats total = EquipStats.Empty;
            if (UpgradeEnabled)
            {
                total = total.Plus(UpgradeOf(worn).ToStats());
            }
            if (GradeEnabled)
            {
                total = total.Plus(GradeDeltaOf(worn));
            }
            return total.IsEmpty ? null : total;
        }
    }
}
