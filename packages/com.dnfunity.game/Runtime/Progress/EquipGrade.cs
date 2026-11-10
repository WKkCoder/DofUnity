using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据(服务端静态读码 + 本机数据对照,没有运行;地址是 df_game_r.orig 的虚拟地址,数值不入库):
// 品级值的生成 WongWork::GenerateRandomUpgrade::generateEquipGrade 0x08533932:五档的权重各 1000(合计 5000),
//   档位的代表值是写死在函数里的五个整数,它们恰好就是 etc 里 [grade_0] 到 [grade_4] 五段各自的第一个样本(本机逐项核对一致,所以代表值可以从数据取);
//   "按角色等级的倍率表"(CDataManager+0x540,vector<pair<int,float>>)非空时才抽签,为空函数直接返回第 0 档的代表值;
//   倍率只乘第 0 档的权重((int)(权重 × 倍率),x87 截断),总权重随之调整;r = randInt(总权重) 含上界,
//   依次累加五档权重,第一个"r 不大于累计值"的档胜出。倍率的取法见 MultiplierFor(原样搬过来:第一项键小于等级取第一项;等级小于最后一项键取最后一项;否则取键相等的项,有重复取最后一个)。
// 掉落强化等级 generateUpgrade 0x08533676 是同一套抽取:按稀有度取一张"(强化等级,权重)"表(CDataManager+0x528,map<稀有度,ItemFarmingData>),
//   第一项权重同样乘倍率、抽法同上,表里总权重为 0 时返回 0。那张表的导入函数没有找到,所以表由调用方给。
// etc 文件 equipmentgradesample.etc 的导入 importEquipmentGradeSampleInfo 0x0890e18d:五段 [grade_0] 到 [grade_4],每段 100 个 0 到 32767 的整数样本;
//   服务端除上面的代表值外没有别的使用,样本怎么变成属性浮动在客户端(受保护,读不到),所以这一步只做"生成规则 + 个体上的品级值"。
namespace Dnf.Game
{
    /// <summary>品级样本(<c>Etc/equipmentgradesample.etc</c>):五档,每档一串整数样本;每档第一个样本是该档的代表值。</summary>
    public sealed class EquipGradeSamples
    {
        /// <summary>服务端写死的档数。</summary>
        public const int Bands = 5;

        private readonly int[][] _samples;

        private EquipGradeSamples(int[][] samples)
        {
            _samples = samples;
        }

        /// <summary>读到几档(按 <c>[grade_N]</c> 段的下标,连续从 0 开始)。</summary>
        public int BandCount => _samples.Length;

        /// <summary>某一档的全部样本(下标越界返回空表)。</summary>
        public IReadOnlyList<int> Samples(int band)
        {
            return (uint)band < (uint)_samples.Length ? _samples[band] : new int[0];
        }

        /// <summary>某一档的代表值(第一个样本);没有这一档或这一档没有样本返回 0。</summary>
        public int Representative(int band)
        {
            return (uint)band < (uint)_samples.Length && _samples[band].Length > 0 ? _samples[band][0] : 0;
        }

        /// <summary>代表值对应的档;不是任何一档的代表值返回 −1(有重复时取最小的档)。</summary>
        public int BandOf(int value)
        {
            for (int i = 0; i < _samples.Length; i++)
            {
                if (_samples[i].Length > 0 && _samples[i][0] == value)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 读脚本:每个 <c>[grade_N]</c> 标签后面的整数依次是该档样本(<c>[/grade_N]</c> 收尾)。至少要有 <c>[grade_0]</c>,下标必须连续,不然返回 false。
        /// </summary>
        public static bool TryParse(ScriptLine[] lines, out EquipGradeSamples samples)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }
            var bands = new SortedDictionary<int, int[]>();
            foreach (ScriptLine line in lines)
            {
                int band = BandIndexOf(line.Tag);
                if (band < 0)
                {
                    continue;
                }
                var values = new List<int>();
                foreach (ScriptValue value in line.Values)
                {
                    if (value.IsNumber)
                    {
                        values.Add(value.Int);
                    }
                }
                bands[band] = values.ToArray();
            }
            samples = null;
            if (bands.Count == 0)
            {
                return false;
            }
            var list = new List<int[]>();
            foreach (KeyValuePair<int, int[]> pair in bands)
            {
                if (pair.Key != list.Count)
                {
                    return false;
                }
                list.Add(pair.Value);
            }
            samples = new EquipGradeSamples(list.ToArray());
            return true;
        }

        private static int BandIndexOf(string tag)
        {
            const string prefix = "[grade_";
            if (tag == null || tag.Length <= prefix.Length + 1 || !tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || tag[tag.Length - 1] != ']')
            {
                return -1;
            }
            string digits = tag.Substring(prefix.Length, tag.Length - prefix.Length - 1);
            return int.TryParse(digits, out int n) && n >= 0 ? n : -1;
        }
    }

    /// <summary>掉落时的"按稀有度的随机强化"与"品级值"的加权抽取(服务端 <c>GenerateRandomUpgrade</c>)。</summary>
    public static class EquipGradeGenerator
    {
        /// <summary>五档的默认权重(服务端每档 1000)。</summary>
        public const int EqualWeight = 1000;

        /// <summary>
        /// 倍率表里按等级取倍率,次序原样照搬服务端:表空为 1;第一项的键小于等级取第一项的倍率;否则等级小于最后一项的键取最后一项的倍率;
        /// 否则取键等于等级的项(有重复取最后一个,没有保持 1)。
        /// </summary>
        public static float MultiplierFor(IReadOnlyList<KeyValuePair<int, float>> table, int level)
        {
            float m = 1f;
            if (table == null || table.Count == 0)
            {
                return m;
            }
            int last = table.Count - 1;
            if (table[0].Key < level)
            {
                m = table[0].Value;
            }
            else if (level < table[last].Key)
            {
                m = table[last].Value;
            }
            else
            {
                for (int i = 0; i <= last; i++)
                {
                    if (table[i].Key == level)
                    {
                        m = table[i].Value;
                    }
                }
            }
            return m;
        }

        /// <summary>
        /// 加权抽取:第 0 项权重先乘 <paramref name="firstMultiplier"/>((int)(权重 × 倍率),x87 扩展精度、截断),总权重 = 各项之和(第 0 项用新值),
        /// 随机数取 0 到总权重(含上界),依次累加,第一个"随机数不大于累计值"的项胜出。没有项、总权重不是正数返回 −1。
        /// </summary>
        public static int Pick(IReadOnlyList<int> weights, float firstMultiplier, IDropRandom random)
        {
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }
            if (weights == null || weights.Count == 0)
            {
                return -1;
            }
            var w = new long[weights.Count];
            long total = 0;
            for (int i = 0; i < w.Length; i++)
            {
                w[i] = weights[i];
            }
            w[0] = X87.TruncateToInt(X87.Mul(X87.FromInt(weights[0]), X87.FromSingle(firstMultiplier)));
            foreach (long x in w)
            {
                total += x;
            }
            if (total <= 0 || total > int.MaxValue)
            {
                return -1;
            }
            int roll = random.NextInclusive((int)total);
            long acc = 0;
            for (int i = 0; i < w.Length; i++)
            {
                acc += w[i];
                if (roll <= acc)
                {
                    return i;
                }
            }
            return w.Length - 1;
        }

        /// <summary>
        /// 生成一件掉落装备的品级值:倍率表为空时(服务端行为)直接给第 0 档的代表值,不抽签;否则按档位权重抽一档,给那档的代表值。
        /// <paramref name="weights"/> 不给时五档各 <see cref="EqualWeight"/>。
        /// </summary>
        public static int Generate(EquipGradeSamples samples, IDropRandom random, IReadOnlyList<KeyValuePair<int, float>> multiplierTable, int level,
            IReadOnlyList<int> weights = null)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }
            if (multiplierTable == null || multiplierTable.Count == 0)
            {
                return samples.Representative(0);
            }
            IReadOnlyList<int> w = weights ?? new[] { EqualWeight, EqualWeight, EqualWeight, EqualWeight, EqualWeight };
            int band = Pick(w, MultiplierFor(multiplierTable, level), random);
            return band < 0 ? samples.Representative(0) : samples.Representative(band);
        }
    }
}
