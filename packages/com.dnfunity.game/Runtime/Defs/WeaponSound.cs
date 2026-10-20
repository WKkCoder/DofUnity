using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 角色 .chr 的一个 [weapon wav] 块(证据 E-089):一种武器的四个声音标签,依次是 挥动 A、挥动 B、命中 A、命中 B。
    /// 块的先后顺序就是武器种类,下标取装备脚本 [sub type] 的值(与 [weapon hit info] 的先后顺序一致,证据 E-077)。
    /// A 通常是一个 RANDOM 标签(几个变体轮着出),B 是单个声音。写了空块(没有值)的武器没有这四个标签。
    /// 哪一击用 A、哪一击用 B 在客户端里没有读到,<see cref="RoomCombatSession"/> 里按"最后一段连招用 B,其余用 A"处理(推断)。
    /// </summary>
    public readonly struct WeaponSound
    {
        public string SwingA { get; }
        public string SwingB { get; }
        public string HitA { get; }
        public string HitB { get; }

        public bool IsEmpty => string.IsNullOrEmpty(SwingA) && string.IsNullOrEmpty(SwingB) && string.IsNullOrEmpty(HitA) && string.IsNullOrEmpty(HitB);

        public WeaponSound(string swingA, string swingB, string hitA, string hitB)
        {
            SwingA = swingA;
            SwingB = swingB;
            HitA = hitA;
            HitB = hitB;
        }

        /// <summary>读全部 [weapon wav] 块,按出现顺序;没有这个标签就返回空数组。值不足四个的位置留空。</summary>
        public static WeaponSound[] ParseAll(TagTable tags)
        {
            if (tags == null)
            {
                return Array.Empty<WeaponSound>();
            }
            var list = new List<WeaponSound>();
            foreach (ScriptLine line in tags.Lines)
            {
                if (!line.TagIs("[weapon wav]"))
                {
                    continue;
                }
                var s = new string[4];
                int n = 0;
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind == ScriptValueKind.String && n < 4)
                    {
                        s[n++] = v.Text;
                    }
                }
                list.Add(new WeaponSound(s[0], s[1], s[2], s[3]));
            }
            return list.ToArray();
        }
    }
}
