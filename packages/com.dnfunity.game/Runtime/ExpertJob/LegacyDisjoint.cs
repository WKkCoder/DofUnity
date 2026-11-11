using System;
using System.Collections.Generic;
using Dnf.Data;

// 服务端静态读码(df_game_r.orig)的"旧版分解":没有分解机(机主为空)时走的路线,便携分解器物品用它。
//   装载:LoadDisJoint 0x0847300c → importDisjointScript 0x088e4d88(etc 目录下的 disjoint.etc);算产物:DisJoint::GetResult 0x084733f6、
//   _GetAdditionalItem 0x084732b4、_GetAdditionalItemInfo 0x08473224;预览:GetPredictResult 0x08473116。
// 观察:
//   · [cube index]("[no element]" 那一行的第一个值)是主产物;[cube creation const] 第一个整数是基数,后面六个浮点是各稀有度(0 到 5)的系数。
//   · [additional result] 六组(各稀有度一组):组内个数 n 与 n 个物品;[additional result const] 每个稀有度四个值:大成功除数、(除数非 0 才有后面三个)普通除数、大成功万分比百分数、没用到的一个整数。
//   · 附加产物:先在 n × 1000 里抽一个数,整除 1000 得组内下标;再抽 0 到 9999,小于"百分数 × 100"用大成功除数,否则用普通除数;个数 = max(1, 向零取整(物品等级 ÷ 除数))。
//   · 主产物个数的算法与机器路线一样,只是系数、基数来自这份表,且没有机器档、没有自用额外产物与穿戴加成。
//   · 稀有度超出 0 到 5 时服务端读的是表外内存,这里按"不能分解"处理(0x11)。
namespace Dnf.Game
{
    /// <summary>旧版分解的脚本(etc 目录下的 disjoint.etc 里与产物有关的部分;技能、时装徽章等标签只登记)。</summary>
    public sealed class LegacyDisjointScript
    {
        /// <summary>稀有度档数(0 到 5)。</summary>
        public const int Rarities = 6;

        /// <summary>脚本所在的目录。</summary>
        public const string Folder = "etc";

        /// <summary>数据路径(<see cref="Folder"/> 下的 disjoint.etc)。</summary>
        public const string Path = Folder + "/disjoint.etc";

        private readonly List<int> _mainItems = new List<int>();
        private readonly float[] _factors = new float[Rarities];
        private readonly float[] _bigDivisor = new float[Rarities];
        private readonly float[] _normalDivisor = new float[Rarities];
        private readonly int[] _bigPercent = new int[Rarities];
        private readonly int[] _groupCount = new int[Rarities];
        private readonly List<int>[] _groups = new List<int>[Rarities];
        private readonly SortedSet<string> _registered = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        public LegacyDisjointScript()
        {
            for (int i = 0; i < Rarities; i++)
            {
                _groups[i] = new List<int>();
            }
        }

        /// <summary>读到过、这一版不建模的标签(技能分解、时装徽章分解、彩票表)。</summary>
        public IReadOnlyCollection<string> RegisteredTags => _registered;

        /// <summary>主产物物品(<c>[cube index]</c> "[no element]" 的第一个值);没有写时为 0。</summary>
        public int MainItem => _mainItems.Count > 0 ? _mainItems[0] : 0;

        public int BaseConst { get; private set; }

        public float Factor(int rarity) => InRange(rarity) ? _factors[rarity] : 0f;

        public int BigPercent(int rarity) => InRange(rarity) ? _bigPercent[rarity] : 0;

        public int GroupCount(int rarity) => InRange(rarity) ? _groupCount[rarity] : 0;

        public IReadOnlyList<int> Group(int rarity) => InRange(rarity) ? _groups[rarity] : (IReadOnlyList<int>)new int[0];

        public static bool InRange(int rarity) => rarity >= 0 && rarity < Rarities;

        public static LegacyDisjointScript Parse(IReadOnlyList<ScriptLine> lines)
        {
            var s = new LegacyDisjointScript();
            if (lines == null)
            {
                return s;
            }
            foreach (ScriptLine line in lines)
            {
                if (line == null || line.Tag == null || line.Tag.StartsWith("[/", StringComparison.Ordinal))
                {
                    continue;
                }
                var r = new ExjReader(line.Values);
                if (line.TagIs("[cube index]"))
                {
                    // 第一个值是元素名(字符串),后面是物品;服务端 getElement(0x088bfe5b)把四种元素名映射到 0 到 3,其余(含 "[no element]")一律是 4,
                    // 主产物取第 4 号向量的第一项;四种元素的向量这一版用不到。
                    string element = r.TakeText();
                    var items = new List<int>();
                    while (r.TryInt(out int item))
                    {
                        items.Add(item);
                    }
                    if (element == null || !IsKnownElement(element))
                    {
                        s._mainItems.Clear();
                        s._mainItems.AddRange(items);
                    }
                }
                else if (line.TagIs("[cube creation const]"))
                {
                    if (r.TryInt(out int baseConst))
                    {
                        s.BaseConst = baseConst;
                    }
                    for (int i = 0; i < Rarities; i++)
                    {
                        if (r.TryFloat(out float f))
                        {
                            s._factors[i] = f;
                        }
                    }
                }
                else if (line.TagIs("[additional result]"))
                {
                    for (int g = 0; g < Rarities; g++)
                    {
                        if (!r.TryInt(out int n))
                        {
                            break;
                        }
                        s._groupCount[g] = n;
                        s._groups[g].Clear();
                        for (int k = 0; k < n; k++)
                        {
                            if (r.TryInt(out int item))
                            {
                                s._groups[g].Add(item);
                            }
                        }
                    }
                }
                else if (line.TagIs("[additional result const]"))
                {
                    for (int i = 0; i < Rarities; i++)
                    {
                        r.TryFloat(out float big);
                        s._bigDivisor[i] = big;
                        if (big != 0f || float.IsNaN(big))
                        {
                            r.TryFloat(out float normal);
                            s._normalDivisor[i] = normal;
                            r.TryInt(out int percent);
                            s._bigPercent[i] = percent;
                            r.TryInt(out int unused);
                        }
                        else
                        {
                            s._normalDivisor[i] = 0f;
                            s._bigPercent[i] = 0;
                        }
                    }
                }
                else
                {
                    s._registered.Add(line.Tag);
                }
            }
            return s;
        }

        private static bool IsKnownElement(string name)
        {
            return string.Equals(name, "[fire element]", StringComparison.Ordinal)
                || string.Equals(name, "[water element]", StringComparison.Ordinal)
                || string.Equals(name, "[dark element]", StringComparison.Ordinal)
                || string.Equals(name, "[light element]", StringComparison.Ordinal);
        }

        /// <summary>
        /// 旧版的产物(<c>DisJoint::GetResult</c>)。随机数次序:附加产物组内下标(范围 n × 1000)、大成功判定(10000);组为空时都不抽。
        /// 稀有度超出 0 到 5 → 0x11;没有主产物或基数为 0 → 0x13。
        /// </summary>
        public DisjointOutcome Compute(DisjointItem item, ItemPriceRates rates, IDisjointRandom rng)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }
            if (rng == null)
            {
                throw new ArgumentNullException(nameof(rng));
            }
            var o = new DisjointOutcome();
            if (!InRange(item.Rarity))
            {
                o.Error = ExpertJobError.InvalidItem;
                return o;
            }
            if (BaseConst == 0 || MainItem == 0)
            {
                o.Error = ExpertJobError.NotPermitted;
                return o;
            }
            ItemPriceRates r = rates ?? ItemPriceRates.Neutral;
            o.Items[0] = MainItem;
            int value = ExactFloat.TruncMul(item.Value, DisjointRules.ValueFactor);
            int sell = ItemPricing.UnitSellPrice(value, item.RandomOption ? r.KiriProtectable : r.Normal);
            int basis = unchecked(sell * 1000) / BaseConst;
            int count = ExactFloat.TruncMulDiv(basis, _factors[item.Rarity], DisjointRules.Thousand);
            o.Counts[0] = count < 1 ? 1 : count;

            int n = _groupCount[item.Rarity];
            if (n > 0)
            {
                int index = rng.Next(n * 1000) / 1000;
                IReadOnlyList<int> group = _groups[item.Rarity];
                int extra = index >= 0 && index < group.Count ? group[index] : 0;
                o.Items[1] = extra;
                if (extra != 0)
                {
                    int roll = rng.Next(DisjointRules.Scale);
                    float divisor = roll < _bigPercent[item.Rarity] * 100 ? _bigDivisor[item.Rarity] : _normalDivisor[item.Rarity];
                    int c = DisjointRules.ToInt((float)item.Grade / divisor);
                    o.Counts[1] = c < 1 ? 1 : c;
                }
            }
            return o;
        }

        /// <summary>预览(<c>GetPredictResult</c>):稀有度 0 到 5 各一张表 = 主产物 + 这一稀有度的附加产物;超出范围时服务端返回第 0 张表,这里照做。</summary>
        public List<int> Predict(int rarity)
        {
            var list = new List<int>();
            int r = InRange(rarity) ? rarity : 0;
            if (MainItem != 0)
            {
                list.Add(MainItem);
            }
            foreach (int item in _groups[r])
            {
                list.Add(item);
            }
            return list;
        }
    }
}
