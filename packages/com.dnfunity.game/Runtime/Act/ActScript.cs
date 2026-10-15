using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game.Act
{
    /// <summary>
    /// 动作脚本(.act)语法树的一个节点:一个标签、它后面的值、(块标签的)子节点。
    /// 只是语法层——节点的语义(行为、条件)由解释器按标签名决定。
    /// </summary>
    public sealed class ActNode
    {
        private static readonly IReadOnlyList<ActNode> NoChildren = Array.Empty<ActNode>();

        private List<ActNode> _children;

        public ActNode(string tag, ScriptValue[] values)
        {
            Tag = tag;
            Values = values ?? Array.Empty<ScriptValue>();
        }

        /// <summary>标签原文(含方括号,大小写保持原样;原版对标签是区分大小写比较的,例如 [hold] 与 [HOLD] 含义不同)。</summary>
        public string Tag { get; }

        /// <summary>标签后面的值(到下一个标签为止)。</summary>
        public ScriptValue[] Values { get; }

        /// <summary>是块:文件里有与之配对的闭合标签(如 [/TRIGGER])。叶子标签没有子节点。</summary>
        public bool IsBlock { get; internal set; }

        /// <summary>配对闭合标签的原文(块才有;笔误闭合如 [/TIRGGER] 配对时这里是文件里的实际写法)。</summary>
        public string Closer { get; internal set; }

        public ActNode Parent { get; internal set; }

        public IReadOnlyList<ActNode> Children => _children ?? NoChildren;

        internal void Add(ActNode child)
        {
            if (_children == null)
            {
                _children = new List<ActNode>();
            }
            child.Parent = this;
            _children.Add(child);
        }

        public bool Is(string tag) => string.Equals(Tag, tag, StringComparison.Ordinal);

        /// <summary>第一个同名子节点;没有返回 null。</summary>
        public ActNode Find(string tag)
        {
            foreach (ActNode c in Children)
            {
                if (c.Is(tag))
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>所有同名子节点(按出现顺序)。</summary>
        public IEnumerable<ActNode> FindAll(string tag)
        {
            foreach (ActNode c in Children)
            {
                if (c.Is(tag))
                {
                    yield return c;
                }
            }
        }

        /// <summary>第 i 个值当整数(整数或十六进制整数;浮点按向零截断,与客户端读整数的规则一致);读不到返回 fallback。</summary>
        public int IntAt(int i, int fallback = 0)
        {
            if ((uint)i >= (uint)Values.Length)
            {
                return fallback;
            }
            ScriptValue v = Values[i];
            if (v.IsNumber)
            {
                return v.Int;
            }
            return v.Kind == ScriptValueKind.Float ? ScriptCursor.TruncateToInt(v.Float) : fallback;
        }

        /// <summary>第 i 个值当字符串(字符串、括号词、"种类+键"的键);读不到返回 null。</summary>
        public string TextAt(int i)
        {
            if ((uint)i >= (uint)Values.Length)
            {
                return null;
            }
            return Values[i].Text;
        }

        public override string ToString() => IsBlock ? Tag + " {" + Children.Count + "}" : Tag;
    }

    /// <summary>解析一个 .act 的结果与不规整之处的统计。</summary>
    public sealed class ActScript
    {
        internal ActScript(List<ActNode> roots, int strayClosers, int repairedClosers, int leadingValues)
        {
            Roots = roots;
            StrayClosers = strayClosers;
            RepairedClosers = repairedClosers;
            LeadingValues = leadingValues;
        }

        /// <summary>顶层节点(通常是 [MOTION]、[TRIGGER]、[BEHAVIOR] 的序列)。</summary>
        public IReadOnlyList<ActNode> Roots { get; }

        /// <summary>找不到可配对的开标签、被丢弃的闭合标签个数。</summary>
        public int StrayClosers { get; }

        /// <summary>闭合标签名与任何开标签都对不上(疑似笔误,如 [/TIRGGER]),按"最内层的块标签"配对的个数。</summary>
        public int RepairedClosers { get; }

        /// <summary>第一个标签之前的游离值的个数。</summary>
        public int LeadingValues { get; }

        public bool Clean => StrayClosers == 0 && RepairedClosers == 0 && LeadingValues == 0;

        /// <summary>顶层的 [MOTION] 个数等便捷统计。</summary>
        public int CountRoots(string tag)
        {
            int n = 0;
            foreach (ActNode r in Roots)
            {
                if (r.Is(tag))
                {
                    n++;
                }
            }
            return n;
        }
    }
}
