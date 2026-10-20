using System;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物打主角的伤害(P5.M5,证据 E-086):把一次命中的输入填进 <see cref="DamageVariables"/>,再交给原版公式 <see cref="DamageFormula"/>(E-049)。
    /// **输入怎么填是推断**:客户端在命中时怎么填这块结构还没读通(只知道服务端用同一个公式复算),这里按公式各项的含义取最朴素的填法:
    /// 基础伤害 = 怪物的攻击力(物理或魔法,看攻击信息的 [attack type])× (100 + [damage bonus]) / 100;公式里的"攻击力"项填 0
    /// (怪物的攻击力已经折进基础伤害);攻击者等级 = 怪物等级(防御率的分母用它,E-049);防御方的防御力取自主角的 [initial value];
    /// 攻击者按怪物(0x211)、防御方按角色(0x111);暴击率加成压成 −3 把基础的 3% 抵消、攻击者的暴击属性填 0(怪物不暴击是假设)。
    /// </summary>
    public static class MonsterDamage
    {
        /// <summary>怪物的对象种类码(脚本头文件的 OBJECTTYPE_*,证据 E-049)。</summary>
        public const uint MonsterType = 0x211;

        /// <summary>角色的对象种类码。</summary>
        public const uint CharacterType = 0x111;

        /// <summary>
        /// 填好一次"怪物打主角"的输入。<paramref name="attack"/> 是怪物的攻击力(物理或魔法,调用方按攻击信息选好),
        /// <paramref name="seed"/> 是这一击的随机种子(公式在局部副本上推进它)。
        /// </summary>
        public static DamageVariables Fill(HeroStrikeInfo info, int attackerLevel, int attack, HeroVitals victim, uint seed)
        {
            if (victim == null)
            {
                throw new ArgumentNullException(nameof(victim));
            }
            var p = new DamageVariables();
            // 客户端初始化函数给的默认值不是"中性"的(百分比加成默认 1.0 会变成 ×1.01,额外倍率默认 1.0 会把伤害翻倍,
            // 最终百分比默认 1 会变成 ×0.01):这些项在命中时由客户端逐项填成真正的值,这里填成"没有加成"的中性值
            p.DamagePercent = 0.0f;
            p.PhysicalPercent = 0.0f;
            p.MagicalPercent = 0.0f;
            p.ExtraRate = 0.0f;
            p.FinalPercentA = 100.0f;
            p.PowerPercent = 100;
            p.PhysicalDefensePercent = 0;
            p.MagicalDefensePercent = 0;
            p.Kind = info.Magical ? AttackKind.Magic : AttackKind.Physic;
            p.Base = (float)(Math.Max(0, attack) * (100.0 + info.DamageBonus) / 100.0);
            p.PhysicalAttack = 0.0f;
            p.MagicalAttack = 0.0f;
            p.Level = (sbyte)Math.Max(1, Math.Min(sbyte.MaxValue, attackerLevel));
            p.PhysicalDefense = ToInt(victim.PhysicalDefense);
            p.MagicalDefense = ToInt(victim.MagicalDefense);
            p.AttackerType = MonsterType;
            p.DamagerType = MonsterType;
            p.DefenderType = CharacterType;
            // 暴击率 = 基础 3 + 暴击率加成 + 攻击者的暴击属性(默认值 1.0 不是中性的):怪物不暴击是假设,所以把加成压成 −3、属性填 0
            p.CriticalRate = -DamageFormula.CriticalBaseChance;
            p.CriticalStatPhysical = 0.0f;
            p.CriticalStatMagical = 0.0f;
            p.Seed = seed;
            return p;
        }

        /// <summary>防御力换成整数:四舍五入,夹在 0 到 int.MaxValue 之间(double 转 int 超出范围的结果没有定义)。</summary>
        private static int ToInt(double value)
        {
            double r = Math.Round(value);
            if (!(r > 0))
            {
                return 0;
            }
            return r >= int.MaxValue ? int.MaxValue : (int)r;
        }

        /// <summary>一击怪物打主角的伤害(至少 1)。</summary>
        public static int Roll(HeroStrikeInfo info, int attackerLevel, int attack, HeroVitals victim, DamageConstants constants, uint seed)
        {
            if (constants == null)
            {
                throw new ArgumentNullException(nameof(constants));
            }
            DamageVariables p = Fill(info, attackerLevel, attack, victim, seed);
            return Math.Max(1, DamageFormula.Compute(p, constants));
        }

        /// <summary>
        /// 读伤害公式用的全局常量:Etc/EtcParameter.etc 的 [damage fomula constant](26 个浮点,E-049)。读不到文件、或没有这一节时返回 null。
        /// 召唤物那三节(creatureCommon.cre)怪物打主角用不到,不读。
        /// </summary>
        public static DamageConstants LoadConstants(DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (!assets.Pvf.TryGetEntry(PvfPath.Normalize("Etc/EtcParameter.etc"), out PvfEntry entry))
            {
                return null;
            }
            byte[] raw = assets.Pvf.Read(entry);
            if (!TokenScript.IsTokenScript(raw))
            {
                return null;
            }
            var constants = new DamageConstants();
            DamageConstantsLoadReport report = constants.LoadEtcParameter(new ScriptCursor(TokenScript.Parse(raw), assets.Strings));
            return report.Complete ? constants : null;
        }
    }
}
