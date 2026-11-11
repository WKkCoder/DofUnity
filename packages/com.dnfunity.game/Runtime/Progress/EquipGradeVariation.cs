using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据(客户端静态读码,函数地址见 out/enhance/NOTES.md;原版数值与代码原文都不入库):
//  · 装备的"品级值"在客户端是一颗随机种子。复制一件装备的属性集时(0x0079a780),先用种子抽一个"档位"(0 到 4,见 BandOf,函数 0x00783bb0),
//    再对每一项基础属性按"百分比、上限"一对(Etc/etcparameter.etc 的 [stat random variation],两个数一对,共 17 对)做一次随机浮动(函数 0x0078cfb0)。
//  · 随机数是线性同余:每抽一次连走三步"乘 0x41c64e6d 加 0x3039",再把三步结果的高位按 10 位一段拼成一个数(NextRandom,函数 0x00e50e60)。
//  · 浮动只作用于第一个数(普通那一档);第二个数(另一档)与强化带来的各项原样拷贝,不浮动。
//  · 全部是 float 单精度运算(CVTSI2SS / MULSS / DIVSS / CVTTSS2SI),取整按"四舍五入、远离零"(函数 0x00767630),C# 的 float 运算逐步对得上。
//  · 这里只做读清楚的 10 对:生命、魔法、四项基础属性、装备攻击防御四项。后面 7 对(元素强化、抗性一类)对应哪些项、哪些装备免浮动没有读清,不做;
//    称号(部位 11)走"档位 5"的另一套算法,没有读清,不做;宠物(部位 22)客户端根本不浮动。
namespace Dnf.Game
{
    /// <summary>
    /// 品级值对装备基础属性的随机浮动(客户端复制装备属性集时做的那一步,见文件头的证据)。
    /// 品级值就是种子:同一个值永远浮出同一组数,所以存档只要存品级值。浮动的结果以"差值"给出(<see cref="DeltaOf(EquipStats, int, int)"/>),
    /// 加在装备基础属性之上(<see cref="EquipInstanceBonus"/> 把它交给 <see cref="EquipBonusOverlay"/>),<see cref="EquipStats"/> 的折算本身不动。
    /// <para>
    /// 档位决定浮动的形状:0 档偏负、4 档偏正、2 档居中(<see cref="Jitter"/> 里逐档写明);浮动幅度 = 基础值 × 百分比 ÷ 100(四舍五入),再不超过上限。
    /// 本类不带任何原版数值:百分比与上限从 <c>Etc/etcparameter.etc</c> 读(<see cref="TryParse"/>),测试用合成的数。
    /// </para>
    /// </summary>
    public sealed class EquipGradeVariation
    {
        /// <summary>[stat random variation] 里"百分比、上限"成对的数目(客户端读 17 对)。</summary>
        public const int PairCount = 17;

        /// <summary>本类用到的前几对(<see cref="Stats"/>)。</summary>
        public const int ModeledPairs = 10;

        /// <summary>档位的个数(0 到 4)。</summary>
        public const int BandCount = 5;

        /// <summary>第 i 对数对应的属性项,次序就是客户端逐项浮动的次序(随机数连着用,次序不能错)。</summary>
        private static readonly EquipStat[] Order =
        {
            EquipStat.HpMax,
            EquipStat.MpMax,
            EquipStat.PhysicalAttack,
            EquipStat.PhysicalDefense,
            EquipStat.MagicalAttack,
            EquipStat.MagicalDefense,
            EquipStat.EquipmentPhysicalAttack,
            EquipStat.EquipmentPhysicalDefense,
            EquipStat.EquipmentMagicalAttack,
            EquipStat.EquipmentMagicalDefense,
        };

        private readonly int[] _percent = new int[ModeledPairs];
        private readonly int[] _cap = new int[ModeledPairs];

        /// <summary>按"百分比"与"上限"两个序列造(各至少 <see cref="ModeledPairs"/> 个,多的忽略)。</summary>
        public EquipGradeVariation(IReadOnlyList<int> percents, IReadOnlyList<int> caps)
        {
            if (percents == null)
            {
                throw new ArgumentNullException(nameof(percents));
            }
            if (caps == null)
            {
                throw new ArgumentNullException(nameof(caps));
            }
            if (percents.Count < ModeledPairs || caps.Count < ModeledPairs)
            {
                throw new ArgumentException("百分比与上限至少各要 " + ModeledPairs + " 个");
            }
            for (int i = 0; i < ModeledPairs; i++)
            {
                _percent[i] = percents[i];
                _cap[i] = caps[i];
            }
        }

        /// <summary>被浮动的属性项,按第 0 到 <see cref="ModeledPairs"/> − 1 对的次序。</summary>
        public static IReadOnlyList<EquipStat> Stats => Order;

        /// <summary>第 <paramref name="pair"/> 对的百分比。</summary>
        public int PercentOf(int pair) => _percent[pair];

        /// <summary>第 <paramref name="pair"/> 对的上限。</summary>
        public int CapOf(int pair) => _cap[pair];

        /// <summary>
        /// 读 <c>Etc/etcparameter.etc</c> 里的 <c>[stat random variation]</c>:一串整数,两个一对(百分比、上限)。至少要有 <see cref="ModeledPairs"/> 对,
        /// 没有这个标签或不够返回 false;标签出现多次取最后一次(与其它表的读法一致,本机数据里只有一次)。
        /// </summary>
        public static bool TryParse(ScriptLine[] lines, out EquipGradeVariation variation)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }
            variation = null;
            ScriptLine found = null;
            foreach (ScriptLine line in lines)
            {
                if (line.Tag != null && line.TagIs("[stat random variation]"))
                {
                    found = line;
                }
            }
            if (found == null)
            {
                return false;
            }
            var numbers = new List<int>(found.Values.Length);
            foreach (ScriptValue value in found.Values)
            {
                if (value.IsNumber)
                {
                    numbers.Add(value.Int);
                }
                else if (value.Kind == ScriptValueKind.Float)
                {
                    numbers.Add((int)value.Float);
                }
            }
            int pairs = Math.Min(PairCount, numbers.Count / 2);
            if (pairs < ModeledPairs)
            {
                return false;
            }
            var percents = new int[pairs];
            var caps = new int[pairs];
            for (int i = 0; i < pairs; i++)
            {
                percents[i] = numbers[2 * i];
                caps[i] = numbers[(2 * i) + 1];
            }
            variation = new EquipGradeVariation(percents, caps);
            return true;
        }

        // ------------------------------------------------------------ 随机数与档位

        /// <summary>
        /// 客户端的随机数:状态走三步 <c>s = s × 0x41c64e6d + 0x3039</c>,结果取"第三步高 16 位的低 10 位"异或上
        /// "(第二步高 16 位的低 10 位 异或 (第一步高 16 位的低 11 位 左移 10 位)) 左移 10 位";状态更新成第三步的值。结果不超过 31 位,恒非负。
        /// </summary>
        public static int NextRandom(ref uint state)
        {
            unchecked
            {
                uint s1 = (state * 0x41c64e6du) + 0x3039u;
                uint s2 = (s1 * 0x41c64e6du) + 0x3039u;
                uint s3 = (s2 * 0x41c64e6du) + 0x3039u;
                state = s3;
                uint low = (s3 >> 16) & 0x3ffu;
                uint mid = (s2 >> 16) & 0x3ffu;
                uint high = (s1 >> 16) & 0x7ffu;
                return (int)(low ^ ((mid ^ (high << 10)) << 10));
            }
        }

        /// <summary>
        /// 品级值抽出的档位:拿种子的一份拷贝抽一个随机数(原种子不动),对 100 取余,小于 20 为 0 档、小于 40 为 1 档、小于 60 为 2 档、小于 80 为 3 档,其余为 4 档。
        /// </summary>
        public static int BandOf(int gradeValue)
        {
            uint copy = unchecked((uint)gradeValue);
            int r = NextRandom(ref copy) % 100;
            if (r < 20)
            {
                return 0;
            }
            if (r < 40)
            {
                return 1;
            }
            if (r < 60)
            {
                return 2;
            }
            return r < 80 ? 3 : 4;
        }

        // ------------------------------------------------------------ 单项浮动

        /// <summary>四舍五入、远离零:不小于 0 的数加 0.5 向零截断,负数减 0.5 向零截断(全程 float)。</summary>
        public static int RoundHalfAway(float value)
        {
            return value >= 0f ? (int)(float)(value + 0.5f) : (int)(float)(value - 0.5f);
        }

        /// <summary>
        /// 对一个基础值做一次浮动,返回浮动后的值。<paramref name="state"/> 是随机数状态(同一件装备的各项共用一个,按次序往下走);
        /// 基础值为 0 直接得 0 且不抽随机数。
        /// <para>
        /// 幅度 m = min(round(<paramref name="percent"/> × 基础值 ÷ 100), <paramref name="cap"/>);W = max(1, 2m + 1);
        /// a = max(1, ⌊0.2W⌋),b = max(1, ⌊0.4W⌋ − a),c = max(1, W − 2(a + b));r 是抽出的随机数。各档的结果(全部再加上基础值):
        /// 4 档 m − r mod a(种子恰为 0x3b9ac9fe 时不抽、直接 +m);3 档 max(0, m − a) − r mod b;2 档 min(0, b − m + a) + r mod c;
        /// 1 档 min(0, a − m) + r mod b;0 档 r mod a − m。(档位 5 是称号的另一套算法,这里没有,按不浮动。)
        /// </para>
        /// </summary>
        public static int Jitter(int baseValue, int percent, int cap, int band, ref uint state)
        {
            if (baseValue == 0)
            {
                return 0;
            }
            if (band < 0 || band >= BandCount)
            {
                return baseValue;
            }
            float product = (float)percent * (float)baseValue;
            int range = RoundHalfAway((float)(product / 100f));
            int m = range <= cap ? range : cap;
            int width = (2 * m) + 1;
            if (width < 1)
            {
                width = 1;
            }
            float w = width;
            int a = (int)(float)(w * 0.2f);
            if (a < 1)
            {
                a = 1;
            }
            int b = -(int)(float)(w * -0.4f) - a;
            if (b < 1)
            {
                b = 1;
            }
            int c = width - (2 * (b + a));
            if (c < 1)
            {
                c = 1;
            }
            if (band == 4 && state == 0x3b9ac9feu)
            {
                return m + baseValue;
            }
            int r = NextRandom(ref state);
            switch (band)
            {
                case 4:
                    return (m - (r % a)) + baseValue;
                case 3:
                    return ((m - a >= 0 ? m - a : 0) - (r % b)) + baseValue;
                case 2:
                {
                    int lead = b - m + a;
                    return ((r % c) + (lead < 1 ? lead : 0)) + baseValue;
                }
                case 1:
                {
                    int lead = a - m;
                    return ((r % b) + (lead < 1 ? lead : 0)) + baseValue;
                }
                default:
                    return ((r % a) - m) + baseValue;
            }
        }

        // ------------------------------------------------------------ 整件装备

        /// <summary>哪些部位会浮动:武器与防具首饰(10 起),但不含称号(11,另一套算法没读清)与宠物(22,客户端不浮动);穿戴部位以外的 26 起不算。</summary>
        public static bool AppliesTo(int part)
        {
            return part >= EquipmentParts.Weapon && part <= EquipmentParts.ArtifactGreen
                && part != EquipmentParts.TitleName && part != EquipmentParts.Creature;
        }

        /// <summary>
        /// 一件装备按品级值浮动之后,各项基础属性相对原来的差(只有第一个数,不为 0 的项才出现)。没有品级值(0)、部位不浮动、没有基础属性时是空。
        /// 属性项的次序固定为 <see cref="Stats"/>,基础值先向零取整成整数再浮动(装备属性在脚本里本来就是整数)。
        /// </summary>
        public EquipStats DeltaOf(EquipStats baseStats, int part, int gradeValue)
        {
            if (baseStats == null || baseStats.IsEmpty || gradeValue == 0 || !AppliesTo(part))
            {
                return EquipStats.Empty;
            }
            uint state = unchecked((uint)gradeValue);
            int band = BandOf(gradeValue);
            var delta = new EquipStats.Builder();
            bool any = false;
            for (int i = 0; i < ModeledPairs; i++)
            {
                int value = ToInt(baseStats[Order[i]]);
                int varied = Jitter(value, _percent[i], _cap[i], band, ref state);
                if (varied != value)
                {
                    delta.Set(Order[i], varied - value);
                    any = true;
                }
            }
            return any ? delta.Build() : EquipStats.Empty;
        }

        /// <summary>同上,取装备自己的基础属性、部位与个体状态里的品级值;不是带品级值的装备返回空。</summary>
        public EquipStats DeltaOf(ItemInfo item)
        {
            if (item == null || !item.IsEquipment || item.Instance == null)
            {
                return EquipStats.Empty;
            }
            return DeltaOf(item.Stats, item.Part, item.Instance.GradeValue);
        }

        private static int ToInt(double value)
        {
            if (double.IsNaN(value))
            {
                return 0;
            }
            if (value >= int.MaxValue)
            {
                return int.MaxValue;
            }
            return value <= int.MinValue ? int.MinValue : (int)value;
        }
    }
}
