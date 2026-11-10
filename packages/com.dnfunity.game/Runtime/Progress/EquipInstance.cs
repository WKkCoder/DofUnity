using System;

// 证据:服务端背包物品 Inven_Item 上的个体字段(强化等级 GetUpgrade/SetUpgrade 0x080f506c/0x080f0ffc、
// 增幅属性 stAmplifyOption_t 的 hasAbility/isIdentified、附魔编号字节偏移 0xd 的整数(_DoEnchant 0x0849cb2c、OnEnchantByBead 0x0849ed1a)、
// 耐久 get_endurance 对比,以及掉落时的品级值 generateEquipGrade 0x08533932)。
namespace Dnf.Game
{
    /// <summary>
    /// 一件具体装备身上"会变"的状态。<see cref="ItemInfo"/> 是按物品编号共享的定义(没有个体状态),
    /// 装备一旦被强化、附魔或带了品级,就换成一份带 <see cref="EquipInstance"/> 的个体拷贝(<see cref="ItemInfo.WithInstance"/>):
    /// 拷贝随背包格与装备槽走(穿上、脱下、搬格子引用不变),装备类物品堆叠上限为 1,所以不会和别的件合并。
    /// <para>
    /// 字段与服务端的对应(都是读码结论,不含任何数值):
    /// <list type="bullet">
    /// <item><see cref="Upgrade"/>:强化等级(服务端一个字节;强化流程只允许在 0 到 30 级继续,封顶 31);</item>
    /// <item><see cref="Amplified"/>:有增幅属性(<c>hasAbility</c>):强化走增幅表、惩罚与费用规则略有不同;</item>
    /// <item><see cref="Identified"/>:增幅已鉴定(<c>isIdentified</c>):没鉴定的不能强化;</item>
    /// <item><see cref="EnchantCode"/>:附魔卡的物品编号(0 表示没有附魔),新附魔直接覆盖旧的;</item>
    /// <item><see cref="GradeValue"/>:品级值(生成时从五档里抽一档的代表值,0 表示没有);</item>
    /// <item><see cref="Damaged"/>:耐久没满(强化要求耐久满,否则被拒)。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class EquipInstance : IEquatable<EquipInstance>
    {
        /// <summary>强化等级上限(服务端 <c>_GetUpgradeInfo</c> 在等级不小于 31 时直接失败;强化券的目标等级也只收 0 到 31)。</summary>
        public const int MaxUpgrade = 31;

        private int _upgrade;
        private int _enchantCode;

        public EquipInstance()
        {
            Identified = true;
        }

        public EquipInstance(int upgrade, bool amplified = false, bool identified = true, int enchantCode = 0, int gradeValue = 0, bool damaged = false)
        {
            Upgrade = upgrade;
            Amplified = amplified;
            Identified = identified;
            EnchantCode = enchantCode;
            GradeValue = gradeValue;
            Damaged = damaged;
        }

        /// <summary>强化等级,0 到 255(服务端字节;正常强化只会到 <see cref="MaxUpgrade"/>)。</summary>
        public int Upgrade
        {
            get => _upgrade;
            set
            {
                if (value < 0 || value > 255)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "强化等级是一个字节(0 到 255)");
                }
                _upgrade = value;
            }
        }

        /// <summary>有增幅属性。</summary>
        public bool Amplified { get; set; }

        /// <summary>增幅已鉴定(没有增幅属性时这一项无意义,默认 true)。</summary>
        public bool Identified { get; set; }

        /// <summary>附魔卡的物品编号;0 表示没有附魔。</summary>
        public int EnchantCode
        {
            get => _enchantCode;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "附魔编号不能为负");
                }
                _enchantCode = value;
            }
        }

        /// <summary>品级值;0 表示没有(见 <see cref="EquipGradeSamples"/>)。</summary>
        public int GradeValue { get; set; }

        /// <summary>耐久没满。</summary>
        public bool Damaged { get; set; }

        /// <summary>每个字段都还是初始值(没有任何个体状态)。</summary>
        public bool IsDefault => _upgrade == 0 && !Amplified && Identified && _enchantCode == 0 && GradeValue == 0 && !Damaged;

        /// <summary>拷贝一份(改拷贝不影响原来的)。</summary>
        public EquipInstance Clone()
        {
            return new EquipInstance(_upgrade, Amplified, Identified, _enchantCode, GradeValue, Damaged);
        }

        public bool Equals(EquipInstance other)
        {
            return other != null && _upgrade == other._upgrade && Amplified == other.Amplified && Identified == other.Identified
                && _enchantCode == other._enchantCode && GradeValue == other.GradeValue && Damaged == other.Damaged;
        }

        public override bool Equals(object obj) => Equals(obj as EquipInstance);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = _upgrade;
                h = (h * 31) + (Amplified ? 1 : 0);
                h = (h * 31) + (Identified ? 1 : 0);
                h = (h * 31) + _enchantCode;
                h = (h * 31) + GradeValue;
                h = (h * 31) + (Damaged ? 1 : 0);
                return h;
            }
        }

        public override string ToString()
        {
            return "+" + _upgrade + (Amplified ? " 增幅" + (Identified ? string.Empty : "(未鉴定)") : string.Empty)
                + (_enchantCode != 0 ? " 附魔#" + _enchantCode : string.Empty) + (GradeValue != 0 ? " 品级值" + GradeValue : string.Empty)
                + (Damaged ? " 耐久未满" : string.Empty);
        }
    }
}
