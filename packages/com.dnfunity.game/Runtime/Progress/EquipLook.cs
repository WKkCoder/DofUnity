using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据(对本机数据的观察与独立 Python 实现的对拍,不是客户端读码):
//  · 全库 63,776 件装备里,武器的外观标签在深度 0 的形状全是固定的:[animation job] 一个字符串、[variation] 两个整数、
//    [layer variation] 一个整数加一个字符串、[equipment ani script] 一个字符串;每个 [animation job] 段里层数与脚本数相等,
//    只有一段不是严格的"层、脚本、层、脚本"交替,所以层与脚本分开存、不做配对。
//  · 外观标签在块里也出现([level section ani]、[expand ani] 里,206 件):那是按强化等级或扩展动画换的备选外观,不是基础外观;
//    这里只读深度 0,块里的只计个数(<see cref="EquipLook.NestedTagCount"/>)。渲染层现有的 AvatarItem.Read 是平读,块里的备选图层会被并进来(块里有外观标签的武器有 114 件)。
namespace Dnf.Game
{
    /// <summary><c>[layer variation]</c> 的一层:顺序键(越小越先画,E-065)与层名。</summary>
    public readonly struct LookLayer
    {
        public LookLayer(int order, string name)
        {
            Order = order;
            Name = name;
        }

        public int Order { get; }

        public string Name { get; }
    }

    /// <summary>
    /// 一个 <c>[animation job]</c> 段:这个职业穿这件装备时的外观数据。
    /// <see cref="Job"/> 为 null 的段装着第一个 <c>[animation job]</c> 之前出现的外观标签(全库武器里没有这种段,只是读法的兜底)。
    /// </summary>
    public sealed class LookSegment
    {
        public LookSegment(string job, bool hasVariation, int variationA, int variationB, IEnumerable<LookLayer> layers, IEnumerable<string> aniScripts)
        {
            Job = job;
            HasVariation = hasVariation;
            VariationA = variationA;
            VariationB = variationB;
            Layers = new List<LookLayer>(layers ?? new LookLayer[0]).AsReadOnly();
            AniScripts = new List<string>(aniScripts ?? new string[0]).AsReadOnly();
        }

        /// <summary><c>[animation job]</c> 后面的职业标签,如 <c>[swordman]</c>(脚本里的写法,不改大小写)。</summary>
        public string Job { get; }

        /// <summary>有 <c>[variation]</c>(两个整数)。</summary>
        public bool HasVariation { get; }

        public int VariationA { get; }

        public int VariationB { get; }

        /// <summary><c>[layer variation]</c> 的层,按脚本里的次序。</summary>
        public IReadOnlyList<LookLayer> Layers { get; }

        /// <summary><c>[equipment ani script]</c> 的路径,按脚本里的次序(与层是否一一对应没有依据,用途没有核对,渲染层现在不读它)。</summary>
        public IReadOnlyList<string> AniScripts { get; }
    }

    /// <summary>
    /// 武器的外观图层数据(供渲染层换武器图层;本类只提供数据,不参与任何渲染):每个职业一段。
    /// 读法见 <see cref="Read"/>;只读深度 0 的标签。
    /// </summary>
    public sealed class EquipLook
    {
        private static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase)
        {
            ["[animation job]"] = Kind.AnimationJob,
            ["[variation]"] = Kind.Variation,
            ["[layer variation]"] = Kind.LayerVariation,
            ["[equipment ani script]"] = Kind.AniScript,
            ["[sub type]"] = Kind.SubType,
        };

        private enum Kind
        {
            AnimationJob,
            Variation,
            LayerVariation,
            AniScript,
            SubType,
        }

        public EquipLook(IEnumerable<LookSegment> segments, bool hasSubType, int subType, int nestedTagCount)
        {
            Segments = new List<LookSegment>(segments ?? new LookSegment[0]).AsReadOnly();
            HasSubType = hasSubType;
            SubType = subType;
            NestedTagCount = nestedTagCount;
        }

        /// <summary>各职业的段,按脚本里的次序。</summary>
        public IReadOnlyList<LookSegment> Segments { get; }

        /// <summary>有 <c>[sub type]</c>(武器种类下标,E-077)。</summary>
        public bool HasSubType { get; }

        /// <summary><c>[sub type]</c> 的第一个整数(同名标签第一次出现的);没写是 0。</summary>
        public int SubType { get; }

        /// <summary>块里面(深度 &gt; 0)出现的外观标签个数:备选外观,不进 <see cref="Segments"/>。</summary>
        public int NestedTagCount { get; }

        /// <summary>
        /// 第一个职业标签与 <paramref name="jobTag"/> 相同(不区分大小写)的段;没有返回 null。<paramref name="jobTag"/> 为 null 也返回 null
        /// (职业为空的那一段装的是第一个 <c>[animation job]</c> 之前的标签,不属于任何职业,要用就从 <see cref="Segments"/> 里取)。
        /// </summary>
        public LookSegment ForJob(string jobTag)
        {
            if (jobTag == null)
            {
                return null;
            }
            foreach (LookSegment segment in Segments)
            {
                if (string.Equals(segment.Job, jobTag, StringComparison.OrdinalIgnoreCase))
                {
                    return segment;
                }
            }
            return null;
        }

        /// <summary>
        /// 从物品脚本的标记流读外观数据(只对武器调用)。规则(与 <c>tools/py/equip_stats_ref.py</c> 的独立实现一致):
        /// 只认深度 0 的标签(块规则见 <see cref="ScriptBlocks"/>),块里的外观标签只计个数;
        /// <c>[animation job]</c> 开一段、取后面第一个字符串;<c>[variation]</c> 取前两个整数(后者覆盖前者);
        /// <c>[layer variation]</c> 取第一个整数与第一个字符串(缺一个就忽略这一行),在本段追加一层;
        /// <c>[equipment ani script]</c> 取第一个字符串,在本段追加一个脚本;<c>[sub type]</c> 取第一个整数,同名标签第一次的算。
        /// 第一个 <c>[animation job]</c> 之前出现的 [variation]、[layer variation]、[equipment ani script] 归进职业为空的段。
        /// </summary>
        public static EquipLook Read(ScriptToken[] tokens, PvfStringTable strings)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }
            if (strings == null)
            {
                throw new ArgumentNullException(nameof(strings));
            }
            int[] depths = ScriptBlocks.Depths(tokens, strings);
            var drafts = new List<Draft>();
            Draft current = null;
            bool hasSubType = false;
            int subType = 0;
            int nested = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Type != (byte)TokenType.Tag)
                {
                    continue;
                }
                string tag = ScriptBlocks.TagAt(strings, tokens[i].Value);
                if (tag == null || !Kinds.TryGetValue(tag, out Kind kind))
                {
                    continue;
                }
                if (depths[i] != 0)
                {
                    nested++;
                    continue;
                }
                int end = i + 1;
                while (end < tokens.Length && tokens[end].Type != (byte)TokenType.Tag)
                {
                    end++;
                }
                if (kind == Kind.AnimationJob)
                {
                    current = new Draft(FirstString(tokens, i + 1, end, strings));
                    drafts.Add(current);
                    continue;
                }
                if (kind == Kind.SubType)
                {
                    if (!hasSubType && FirstInt(tokens, i + 1, end, out int sub))
                    {
                        subType = sub;
                        hasSubType = true;
                    }
                    continue;
                }
                if (current == null)
                {
                    current = new Draft(null);
                    drafts.Add(current);
                }
                if (kind == Kind.Variation)
                {
                    if (TwoInts(tokens, i + 1, end, out int a, out int b))
                    {
                        current.HasVariation = true;
                        current.VariationA = a;
                        current.VariationB = b;
                    }
                }
                else if (kind == Kind.LayerVariation)
                {
                    string name = FirstString(tokens, i + 1, end, strings);
                    if (FirstInt(tokens, i + 1, end, out int order) && name != null)
                    {
                        current.Layers.Add(new LookLayer(order, name));
                    }
                }
                else
                {
                    string script = FirstString(tokens, i + 1, end, strings);
                    if (script != null)
                    {
                        current.Scripts.Add(script);
                    }
                }
            }
            var segments = new List<LookSegment>(drafts.Count);
            foreach (Draft d in drafts)
            {
                segments.Add(new LookSegment(d.Job, d.HasVariation, d.VariationA, d.VariationB, d.Layers, d.Scripts));
            }
            return new EquipLook(segments, hasSubType, subType, nested);
        }

        private sealed class Draft
        {
            public Draft(string job)
            {
                Job = job;
            }

            public string Job { get; }

            public bool HasVariation { get; set; }

            public int VariationA { get; set; }

            public int VariationB { get; set; }

            public List<LookLayer> Layers { get; } = new List<LookLayer>();

            public List<string> Scripts { get; } = new List<string>();
        }

        private static bool IsInt(ScriptToken t)
        {
            return t.Type == (byte)TokenType.Int || t.Type == (byte)TokenType.UInt;
        }

        private static bool FirstInt(ScriptToken[] tokens, int from, int to, out int value)
        {
            for (int i = from; i < to; i++)
            {
                if (IsInt(tokens[i]))
                {
                    value = tokens[i].Value;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        private static bool TwoInts(ScriptToken[] tokens, int from, int to, out int first, out int second)
        {
            first = 0;
            second = 0;
            int found = 0;
            for (int i = from; i < to && found < 2; i++)
            {
                if (!IsInt(tokens[i]))
                {
                    continue;
                }
                if (found == 0)
                {
                    first = tokens[i].Value;
                }
                else
                {
                    second = tokens[i].Value;
                }
                found++;
            }
            return found == 2;
        }

        private static string FirstString(ScriptToken[] tokens, int from, int to, PvfStringTable strings)
        {
            for (int i = from; i < to; i++)
            {
                if (tokens[i].Type == (byte)TokenType.String)
                {
                    return ScriptBlocks.TagAt(strings, tokens[i].Value);
                }
            }
            return null;
        }
    }
}
