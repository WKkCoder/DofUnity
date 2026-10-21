// 证据:攻击信息的静态部分读法见 E-047;倍率的单位与伤害公式见 E-049(这里只按脚本给出的数值保存)。
namespace Dnf.Game
{
    /// <summary>
    /// 对象当前使用的一份攻击信息:静态部分来自 .atk(<see cref="Def"/>),倍率与硬直等由脚本在运行时改写。
    /// 倍率的单位与伤害公式**未验证**,这里只按脚本给出的数值保存。
    /// </summary>
    public sealed class AttackInfo : NativeObject
    {
        public override string TypeName => "AttackInfo";

        public AttackInfoDef Def { get; internal set; }

        /// <summary>来源标识,例如 "custom:5"(CUSTOM_ATTACK_INFO_ 下标)或 "basic:0"。</summary>
        public string Source { get; internal set; }

        /// <summary>脚本设置的攻击倍率(sq_SetCurrentAttackBonusRate);未设置时为 null。</summary>
        public float? BonusRate { get; set; }

        /// <summary>脚本设置的攻击力(sq_SetCurrentAttackPower);未设置时为 null。</summary>
        public float? AttackPower { get; set; }

        /// <summary>脚本设置的受击硬直时间(毫秒,sq_SetCurrentAttackeHitStunTime);未设置时为 null。</summary>
        public int? HitStunTimeMs { get; set; }

        /// <summary>
        /// 脚本在运行时写进攻击信息的其余设置(击飞力、击退力、元素、攻击类型、命中延迟……),按接口名记录,战斗结算(P5)再解释。
        /// </summary>
        public System.Collections.Generic.Dictionary<string, float> Fields { get; } = new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>脚本用属性写法(info.sizeRate = ...)直接赋的字段,原样记录(sizeRate、changeStatusType 等);宿主还没有解释它们。</summary>
        public System.Collections.Generic.Dictionary<string, object> Properties { get; } = new System.Collections.Generic.Dictionary<string, object>();

        /// <summary>sq_SetChangeStatusIntoAttackInfo 附加的属性改变(命中时让目标得到的效果)。</summary>
        public System.Collections.Generic.List<ChangeStatusParameter> ChangeStatuses { get; } = new System.Collections.Generic.List<ChangeStatusParameter>();
    }
}
