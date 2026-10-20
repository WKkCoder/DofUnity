using System;

namespace Dnf.Game
{
    /// <summary>
    /// 受击(DAMAGE)硬直用的三张表,取自用户自备数据里的 Etc/EtcParameter.etc(证据 E-080,静态读码,没有运行时验证):
    /// [hit delay time min]、[hit delay time max]、[hit recovery max],每个标签后面两个整数。
    /// 数值是原版数据,不进仓库:运行时从用户自备的 PVF 读;读不到就是 null,调用方退回占位硬直。
    /// </summary>
    public sealed class HitStunTable
    {
        public const string DelayMinTag = "[hit delay time min]";
        public const string DelayMaxTag = "[hit delay time max]";
        public const string RecoveryMaxTag = "[hit recovery max]";

        /// <summary>受击恢复达到(或超过)<see cref="RecoveryMax"/> 时的硬直(毫秒,倍率为 1)。</summary>
        public int DelayMin { get; }

        /// <summary>受击恢复为 0 时的硬直(毫秒,倍率为 1)。</summary>
        public int DelayMax { get; }

        /// <summary>硬直降到下限所需的受击恢复。</summary>
        public int RecoveryMax { get; }

        public HitStunTable(int delayMin, int delayMax, int recoveryMax)
        {
            DelayMin = delayMin;
            DelayMax = delayMax;
            RecoveryMax = recoveryMax;
        }

        /// <summary>
        /// 从 EtcParameter.etc 的标签表取三项;缺任何一项返回 null。每个标签有两个值,客户端按一个"主机/在线"条件(没有读清)选一个,
        /// 这里由 <paramref name="variant"/>(0 或 1)指定,默认取第一个;只有一个值时总是它。
        /// </summary>
        public static HitStunTable FromEtcParameter(TagTable tags, int variant = 0)
        {
            if (tags == null)
            {
                return null;
            }
            int[] min = tags.Ints(DelayMinTag);
            int[] max = tags.Ints(DelayMaxTag);
            int[] recovery = tags.Ints(RecoveryMaxTag);
            if (min.Length == 0 || max.Length == 0 || recovery.Length == 0)
            {
                return null;
            }
            return new HitStunTable(Pick(min, variant), Pick(max, variant), Pick(recovery, variant));
        }

        private static int Pick(int[] values, int variant)
        {
            return variant > 0 && variant < values.Length ? values[variant] : values[0];
        }

        /// <summary>
        /// 硬直毫秒数(客户端 0x00833a20):T = int((min + max(0, (max − min) × (recmax − R) / max(recmax, 1))) × rate),
        /// R = 受击方的受击恢复 − 攻击方所属者的同类属性;整数乘除向零取整,最后一步是单精度相乘再向零取整。
        /// </summary>
        public int StunMs(int victimRecovery, int attackerRecovery, float rate)
        {
            int r = unchecked(victimRecovery - attackerRecovery);
            int divisor = RecoveryMax > 1 ? RecoveryMax : 1;
            int extra = unchecked((DelayMax - DelayMin) * (RecoveryMax - r)) / divisor;
            if (extra < 0)
            {
                extra = 0;
            }
            float scaled = (float)unchecked(DelayMin + extra) * rate;
            return (int)scaled;
        }
    }

    /// <summary>一次受击(DAMAGE)反应的计算结果:硬直与击退。</summary>
    public readonly struct HitStunResult
    {
        /// <summary>硬直毫秒数(受击状态持续这么久,从被打中那一刻起算)。</summary>
        public int StunMs { get; }

        /// <summary>击退初速,像素/秒(0 表示不击退)。</summary>
        public int KnockSpeed { get; }

        /// <summary>击退减速度,像素/秒²(≤ 0;0 表示没有)。</summary>
        public int KnockDecel { get; }

        public HitStunResult(int stunMs, int knockSpeed, int knockDecel)
        {
            StunMs = stunMs;
            KnockSpeed = knockSpeed;
            KnockDecel = knockDecel;
        }

        /// <summary>匀减速到停所需的毫秒数;不击退时为 0。</summary>
        public double KnockMs => KnockSpeed > 0 && KnockDecel < 0 ? 1000.0 * KnockSpeed / -(double)KnockDecel : 0.0;

        /// <summary>匀减速到停走过的总像素 v0² / (2 × |a|);不击退时为 0。</summary>
        public double KnockPixels => KnockSpeed > 0 && KnockDecel < 0 ? (double)KnockSpeed * KnockSpeed / (2.0 * -(double)KnockDecel) : 0.0;
    }

    /// <summary>怪物挨一下时需要的、与受击硬直有关的输入(来自这一击的攻击信息与武器表);默认值表示"没有",调用方走占位路径。</summary>
    public readonly struct HitStunInput
    {
        /// <summary>三张表;null 表示没有(走占位)。</summary>
        public HitStunTable Table { get; }

        /// <summary>硬直倍率,见 <see cref="HitStun.Rate(float, float, bool, bool)"/>。</summary>
        public float Rate { get; }

        /// <summary>[force hit stun time];非 0 时直接当作硬直。</summary>
        public int ForceStunMs { get; }

        /// <summary>[knuck back] 的类型(0 普通、1 击退、2 短击退、3 按像素、−1 不后退)。</summary>
        public int KnockType { get; }

        /// <summary>命中模式为 2([hit info] 第一个关键字是 [burn]):不击退。</summary>
        public bool NoKnock { get; }

        public bool IsSet => Table != null;

        public HitStunInput(HitStunTable table, float rate, int forceStunMs = 0, int knockType = 0, bool noKnock = false)
        {
            Table = table;
            Rate = rate;
            ForceStunMs = forceStunMs;
            KnockType = knockType;
            NoKnock = noKnock;
        }
    }

    /// <summary>
    /// 受击(DAMAGE)状态"开始"时的硬直与击退(证据 E-080,静态读码,没有运行时验证):客户端 0x008360e0 的 DAMAGE 段与 0x00833a20。
    /// 下面的常数都是引擎代码里的立即数或 .rdata 常数。
    /// </summary>
    public static class HitStun
    {
        /// <summary>百分数与倍率之间的换算(0x01565cc0)。</summary>
        public const float PercentUnit = 0.01f;

        /// <summary>倍率换算成百分数用的 100.0(0x01562e38)。</summary>
        public const float PercentScale = 100.0f;

        /// <summary>魔法攻击([attack type] = 1)的倍率因子(0x01562254)。</summary>
        public const float MagicFactor = 0.7f;

        /// <summary>"没写"的倍率值(0x01562190);近似等于它就当没写,取 1.0。</summary>
        public const float UnsetScale = -1.0f;

        /// <summary>近似相等的相对误差(0x01562898,2⁻²³)。</summary>
        public const double UnsetEpsilon = 1.1920928955078125e-7;

        /// <summary>[knuck back] 类型 1、2 时硬直 = int(rate × 333.0)(0x015abee8)。</summary>
        public const float KnockTypeStunMs = 333.0f;

        /// <summary>默认击退初速(像素/秒)与持续(毫秒):没写 [knuck back] 时;持续再取 min(持续, 硬直)。</summary>
        public const int DefaultKnockSpeed = 400;

        public const int DefaultKnockMs = 150;

        /// <summary>[knuck back] 类型 −1:不后退,持续 30(初速为 0,没有位移)。</summary>
        public const int NoKnockKeepMs = 30;

        /// <summary>[knuck back] 类型 1(击退)与类型 2(短击退)的初速。</summary>
        public const int LongKnockSpeed = 800;

        public const int ShortKnockSpeed = 400;

        /// <summary>
        /// 这一击的命中延迟倍率(攻击信息 +0x114 的浮点,E-077):[hit info] 的浮点项不是 −1 就用它;是 −1(没写)取武器表那一项;
        /// 文件里写了 [is use weapon hit delay rate] 时改为"武器的 × 文件里的"(与停顿毫秒同一套规则,E-077;浮点这一半是推断)。
        /// <paramref name="weaponScale"/> 没有武器表时传 −1。
        /// </summary>
        public static float DelayScale(float infoScale, float weaponScale, bool useWeaponRate)
        {
            float scale = infoScale;
            if (IsUnset(scale))
            {
                scale = weaponScale;
            }
            else if (useWeaponRate && !IsUnset(weaponScale))
            {
                scale = weaponScale * infoScale;
            }
            return scale;
        }

        /// <summary>受击一侧真正用的倍率 L:没写(≈ −1)取 1.0;魔法攻击再乘 0.7(单精度)。</summary>
        public static float EffectiveScale(float scale, bool magical)
        {
            float l = scale;
            float diff = l - UnsetScale;
            bool differs = Math.Abs((double)l) * UnsetEpsilon < Math.Abs((double)diff);
            if (!differs)
            {
                l = 1.0f;
            }
            if (magical)
            {
                l *= MagicFactor;
            }
            return l;
        }

        /// <summary>
        /// 倍率换成硬直公式里的 rate:请求里存 p = int(100 × L);开始处理把它 × 0.01 × 100 再取整;公式函数再 × 0.01。
        /// 单精度,两次向零取整,所以 L = 1 时 rate 正好是 1,别的值会有一点点舍入。
        /// </summary>
        public static float RateFromScale(float effectiveScale)
        {
            int percent = (int)(PercentScale * effectiveScale);
            float x = (float)percent * PercentUnit;
            int item = (int)(x * PercentScale);
            return (float)item * PercentUnit;
        }

        /// <summary>从攻击信息与武器表一路算到 rate:<see cref="DelayScale"/> → <see cref="EffectiveScale"/> → <see cref="RateFromScale"/>。</summary>
        public static float Rate(float infoScale, float weaponScale, bool useWeaponRate, bool magical)
        {
            return RateFromScale(EffectiveScale(DelayScale(infoScale, weaponScale, useWeaponRate), magical));
        }

        /// <summary>
        /// 一次受击(DAMAGE)的硬直与击退。<paramref name="knockable"/> 对应客户端 this+0x3568 的受保护整数(为 0 时不击退;含义没查,默认真)。
        /// 类型 3(按像素后退)客户端走另一个函数(0x008b05c0,没读),这里按类型 0 处理。
        /// </summary>
        public static HitStunResult Damage(HitStunTable table, int victimRecovery, int attackerRecovery, float rate, int forceStunMs, int knockType,
            bool noKnock, bool knockable = true)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            int stun;
            int speed;
            int keep;
            if (knockType == 1 || knockType == 2)
            {
                stun = (int)(rate * KnockTypeStunMs);
                speed = knockType == 1 ? LongKnockSpeed : ShortKnockSpeed;
                keep = stun;
            }
            else
            {
                if (noKnock)
                {
                    speed = 0;
                    keep = 0;
                }
                else if (knockType == -1)
                {
                    speed = 0;
                    keep = NoKnockKeepMs;
                }
                else
                {
                    speed = DefaultKnockSpeed;
                    keep = DefaultKnockMs;
                }
                if (forceStunMs != 0)
                {
                    stun = forceStunMs;
                }
                else
                {
                    stun = table.StunMs(victimRecovery, attackerRecovery, rate);
                    if (keep > stun)
                    {
                        keep = stun;
                    }
                }
            }
            int decel;
            if (!knockable)
            {
                speed = 0;
                decel = 0;
            }
            else if (keep <= 0)
            {
                // 客户端只判持续为 0;负数不会出现(倍率为负才会),这里一并当作没有减速度
                decel = 0;
            }
            else
            {
                decel = unchecked(speed * -1000) / keep;
            }
            return new HitStunResult(stun, speed, decel);
        }

        private static bool IsUnset(float scale)
        {
            return !(Math.Abs((double)scale) * UnsetEpsilon < Math.Abs((double)(scale - UnsetScale)));
        }
    }
}
