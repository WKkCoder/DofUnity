using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 角色 .chr 的 [weapon hit info] 里一种武器的一项(证据 E-077):每项依次是 两个关键字(命中类别、是否出血)、
    /// 一个整数(命中停顿毫秒数)、一个浮点(命中延迟倍率)与两个浮点(含义没有查到)。
    /// 项的先后顺序就是武器种类,下标取装备脚本 [sub type] 的值(与 [weapon wav] 的先后顺序一致)。
    /// 攻击信息的 [hit info] 整数写 -1 或没有 [hit info] 时,命中停顿取这里的整数。数值是原版数据,不进仓库。
    /// </summary>
    public readonly struct WeaponHitInfo
    {
        /// <summary>第一个关键字原样(例如 "[cut]"、"[blow]")。</summary>
        public string HitKind { get; }

        /// <summary>第二个关键字:"[blood]" 为 true,"[no blood]" 为 false。</summary>
        public bool Blood { get; }

        /// <summary>命中停顿的毫秒数(攻击方与受击方各冻结这么久)。</summary>
        public int StunMs { get; }

        /// <summary>命中延迟倍率(受击一侧的反应时长要乘它;还没有接进来)。</summary>
        public float DelayRate { get; }

        /// <summary>最后两个浮点项(含义没有查到,按原样保留)。</summary>
        public float Extra1 { get; }

        public float Extra2 { get; }

        public WeaponHitInfo(string hitKind, bool blood, int stunMs, float delayRate, float extra1 = 0f, float extra2 = 0f)
        {
            HitKind = hitKind;
            Blood = blood;
            StunMs = stunMs;
            DelayRate = delayRate;
            Extra1 = extra1;
            Extra2 = extra2;
        }

        /// <summary>
        /// 按 字符串、字符串、整数、浮点、浮点、浮点 的节奏一项项读;值不够一项时停下。浮点位置上写了整数也接受。
        /// 没有这个标签就返回空数组。
        /// </summary>
        public static WeaponHitInfo[] ParseAll(TagTable tags)
        {
            if (tags == null || !tags.TryGetLine("[weapon hit info]", out ScriptLine line))
            {
                return Array.Empty<WeaponHitInfo>();
            }
            ScriptValue[] v = line.Values;
            var list = new List<WeaponHitInfo>();
            for (int i = 0; i + 5 < v.Length; i += 6)
            {
                if (v[i].Kind != ScriptValueKind.String || v[i + 1].Kind != ScriptValueKind.String)
                {
                    break;
                }
                list.Add(new WeaponHitInfo(
                    v[i].Text,
                    string.Equals(v[i + 1].Text, "[blood]", StringComparison.OrdinalIgnoreCase),
                    AsInt(v[i + 2]),
                    AsFloat(v[i + 3]),
                    AsFloat(v[i + 4]),
                    AsFloat(v[i + 5])));
            }
            return list.ToArray();
        }

        private static int AsInt(ScriptValue v)
        {
            return v.Kind == ScriptValueKind.Float ? (int)v.Float : v.Int;
        }

        private static float AsFloat(ScriptValue v)
        {
            return v.Kind == ScriptValueKind.Float ? v.Float : v.Int;
        }
    }
}
