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
    }
}
