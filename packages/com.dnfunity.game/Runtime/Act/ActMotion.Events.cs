using System;
using System.Collections.Generic;
using Dnf.Data;

// [MOTION] 里的叠加层与声音登记的读取:把 [SUB ANI]、[SUB ANI WITH XY]、[SUB ANI WITH XYZ]、[SOUND]、[SOUND PROBABILITY] 读成结构化条目(类型见 ActMotionTypes.cs)。
namespace Dnf.Game.Act
{
    public sealed partial class ActMotion
    {
        /// <summary>读得全的叠加层条目,按文件顺序(<see cref="ActOverlayLayer.Index"/> 升序)。</summary>
        public IReadOnlyList<ActOverlayLayer> Layers { get; } = Array.Empty<ActOverlayLayer>();

        /// <summary>叠加层的合成顺序(数据结论):按 (层序键升序, 文件顺序升序),先画的在前;<see cref="ActOverlayLayer.Rank"/> 就是它在这个表里的位置。</summary>
        public IReadOnlyList<ActOverlayLayer> CompositeOrder { get; } = Array.Empty<ActOverlayLayer>();

        /// <summary>读得全的声音条目,按文件顺序([SOUND] 与 [SOUND PROBABILITY] 混排,<see cref="ActSoundEvent.Index"/> 升序)。</summary>
        public IReadOnlyList<ActSoundEvent> Sounds { get; } = Array.Empty<ActSoundEvent>();

        /// <summary>读不全被丢掉的条目个数(缺路径或标签、后面的整数不够、块里游离的整数);样本里是 0。</summary>
        public int MalformedEntries { get; }

        /// <summary>有叠加层或声音登记,事件流里有东西可出。</summary>
        public bool HasEvents => Layers.Count > 0 || Sounds.Count > 0;

        internal ActMotion(string baseAni, List<string> subAnis, List<ActOverlayLayer> layers, List<ActSoundEvent> sounds, int malformed)
            : this(baseAni, subAnis)
        {
            Layers = layers;
            Sounds = sounds;
            MalformedEntries = malformed;
            var order = new List<ActOverlayLayer>(layers);
            order.Sort(CompareComposite);
            for (int i = 0; i < order.Count; i++)
            {
                order[i].Rank = i;
            }
            CompositeOrder = order;
        }

        private static int CompareComposite(ActOverlayLayer a, ActOverlayLayer b)
        {
            return a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Index.CompareTo(b.Index);
        }
    }

    /// <summary>[MOTION] 子标签里叠加层与声音条目的读法。</summary>
    internal static class ActMotionCompiler
    {
        /// <summary>
        /// 读一个 [MOTION] 节点的直接子标签。一条 = 一个字符串加规定个数的整数(浮点按向零截断,与读整数的规则一致);
        /// 字符串后整数不够(遇到下一个字符串、别的类型或块尾)的那一条丢掉并记一次;块里不在字符串后面的游离值也记一次。
        /// </summary>
        internal static void Read(ActNode motion, List<ActOverlayLayer> layers, List<ActSoundEvent> sounds, out int malformed)
        {
            int bad = 0;
            foreach (ActNode c in motion.Children)
            {
                if (c.Is("[SUB ANI]"))
                {
                    ReadLayers(c, ActOverlayForm.Plain, 2, layers, ref bad);
                }
                else if (c.Is("[SUB ANI WITH XY]"))
                {
                    ReadLayers(c, ActOverlayForm.WithXY, 4, layers, ref bad);
                }
                else if (c.Is("[SUB ANI WITH XYZ]"))
                {
                    ReadLayers(c, ActOverlayForm.WithXYZ, 5, layers, ref bad);
                }
                else if (c.Is("[SOUND]"))
                {
                    ReadSounds(c, false, sounds, ref bad);
                }
                else if (c.Is("[SOUND PROBABILITY]"))
                {
                    ReadSounds(c, true, sounds, ref bad);
                }
            }
            malformed = bad;
        }

        private static void ReadLayers(ActNode block, ActOverlayForm form, int intCount, List<ActOverlayLayer> into, ref int bad)
        {
            ScriptValue[] v = block.Values;
            int i = 0;
            while (i < v.Length)
            {
                if (!StartsEntry(v[i]))
                {
                    bad++;
                    i++;
                    continue;
                }
                string path = v[i].Text;
                int first = i + 1;
                int got = CountNumbers(v, first, intCount);
                i = first + got;
                if (got < intCount)
                {
                    bad++;
                    continue;
                }
                int start = block.IntAt(first);
                int depth = block.IntAt(first + 1);
                int x = intCount >= 4 ? block.IntAt(first + 2) : 0;
                int y = intCount >= 4 ? block.IntAt(first + 3) : 0;
                int z = intCount >= 5 ? block.IntAt(first + 4) : 0;
                into.Add(new ActOverlayLayer(into.Count, form, path, start, depth, x, y, z));
            }
        }

        private static void ReadSounds(ActNode block, bool probabilistic, List<ActSoundEvent> into, ref int bad)
        {
            int intCount = probabilistic ? 2 : 1;
            ScriptValue[] v = block.Values;
            int i = 0;
            while (i < v.Length)
            {
                if (!StartsEntry(v[i]))
                {
                    bad++;
                    i++;
                    continue;
                }
                string key = v[i].Text;
                int first = i + 1;
                int got = CountNumbers(v, first, intCount);
                i = first + got;
                if (got < intCount)
                {
                    bad++;
                    continue;
                }
                int frame = block.IntAt(first);
                int percent = probabilistic ? block.IntAt(first + 1) : 100;      // [SOUND] 没有概率这一项,加载函数里固定 100
                into.Add(new ActSoundEvent(into.Count, key, frame, percent, probabilistic));
            }
        }

        private static bool StartsEntry(ScriptValue v) => v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text);

        private static bool IsNumeric(ScriptValue v) => v.IsNumber || v.Kind == ScriptValueKind.Float;

        /// <summary>从 <paramref name="from"/> 起连续有几个数值(最多数到 <paramref name="max"/> 个)。</summary>
        private static int CountNumbers(ScriptValue[] v, int from, int max)
        {
            int n = 0;
            while (n < max && from + n < v.Length && IsNumeric(v[from + n]))
            {
                n++;
            }
            return n;
        }
    }
}
