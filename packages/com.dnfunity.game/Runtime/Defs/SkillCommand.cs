using System;
using System.Collections.Generic;
using System.Text;
using Dnf.Data;

// 证据:E-140(.skl 的 [command] 只有八个括号词;分隔词是字符串池里的 "," 或 "&amp;";热键表里"技能"与"技能2"两个独立热键对应 SKILL 与 BUFF,是推断)。
namespace Dnf.Game
{
    /// <summary>
    /// 出招指令里的一个键(.skl 的 [command] 里只出现这八个括号词,E-140)。
    /// 方向键按"相对朝向"写:<see cref="Right"/> 是朝前、<see cref="Left"/> 是朝后(推断,没有在原版里量过——按键绑定判断在客户端的受保护节里,E-140)。
    /// </summary>
    public enum CommandKey : byte
    {
        Up = 0,
        Down = 1,
        Left = 2,
        Right = 3,
        Attack = 4,
        Jump = 5,
        /// <summary>热键表里"技能"那一格(默认 Z)。</summary>
        Skill = 6,
        /// <summary>热键表里"技能2"那一格(默认空格)。</summary>
        Buff = 7,
    }

    /// <summary>
    /// 一个技能的出招指令:.skl 的 [command] 里按出现顺序的键序列,最后一个键是"扳机"(它被按下的那一刻才判定放不放),
    /// 前面的是修饰(方向键按住,或在一个短窗口内依次按过)。相邻两个键之间的分隔词是字符串池里的 ","(先后)或 "&amp;"(同时),
    /// 同时的只出现在"方向键 &amp; 别的键"之间(全库汇总 31 处,E-140);没解出分隔字符时按先后处理。
    /// </summary>
    public sealed class SkillCommand
    {
        /// <summary>没有指令(没有 [command] 标签,或标签是空的)。</summary>
        public static readonly SkillCommand Empty = new SkillCommand(Array.Empty<CommandKey>(), Array.Empty<bool>(), 0);

        private readonly CommandKey[] _keys;
        private readonly bool[] _together;

        private SkillCommand(CommandKey[] keys, bool[] together, int unknown)
        {
            _keys = keys;
            _together = together;
            UnknownCount = unknown;
        }

        /// <summary>按出现顺序的键。</summary>
        public IReadOnlyList<CommandKey> Keys => _keys;

        /// <summary>键的个数。</summary>
        public int Length => _keys.Length;

        /// <summary>没有任何键(这样的技能按不出来,只能靠快捷槽)。</summary>
        public bool IsEmpty => _keys.Length == 0;

        /// <summary>没认出来的括号词个数(正常数据里为 0)。</summary>
        public int UnknownCount { get; }

        /// <summary>最后一个键(扳机);空指令时抛异常,先看 <see cref="IsEmpty"/>。</summary>
        public CommandKey Trigger => _keys[_keys.Length - 1];

        /// <summary>第 <paramref name="index"/> 个键与它后一个键是不是"同时"(分隔词 "&amp;");最后一个键没有后一个,返回 false。</summary>
        public bool TogetherWithNext(int index)
        {
            return index >= 0 && index < _together.Length && _together[index];
        }

        /// <summary>有没有"同时"的连接。</summary>
        public bool HasTogether
        {
            get
            {
                foreach (bool t in _together)
                {
                    if (t)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>是不是只由方向键 + 扳机组成(扳机前面没有攻击、跳跃、技能、增益键夹在中间)。</summary>
        public bool IsDirectionsThenTrigger
        {
            get
            {
                for (int i = 0; i + 1 < _keys.Length; i++)
                {
                    if (!IsDirection(_keys[i]))
                    {
                        return false;
                    }
                }
                return _keys.Length > 0;
            }
        }

        /// <summary>这个键是不是方向键。</summary>
        public static bool IsDirection(CommandKey key)
        {
            return key == CommandKey.Up || key == CommandKey.Down || key == CommandKey.Left || key == CommandKey.Right;
        }

        /// <summary>认一个括号词:带不带括号、大小写都行。</summary>
        public static bool TryParseKey(string text, out CommandKey key)
        {
            key = CommandKey.Up;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string t = text.Trim().Trim('(', ')').Trim();
            switch (t.ToUpperInvariant())
            {
                case "UP": key = CommandKey.Up; return true;
                case "DOWN": key = CommandKey.Down; return true;
                case "LEFT": key = CommandKey.Left; return true;
                case "RIGHT": key = CommandKey.Right; return true;
                case "ATTACK": key = CommandKey.Attack; return true;
                case "JUMP": key = CommandKey.Jump; return true;
                case "SKILL": key = CommandKey.Skill; return true;
                case "BUFF": key = CommandKey.Buff; return true;
                default: return false;
            }
        }

        /// <summary>从 [command] 那一行读(括号词按顺序收;分隔词 "&amp;" 标记"同时",其余分隔词与值跳过)。</summary>
        public static SkillCommand From(ScriptLine line)
        {
            if (line == null)
            {
                return Empty;
            }
            var keys = new List<CommandKey>();
            var together = new List<bool>();
            int unknown = 0;
            bool pendingTogether = false;
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Separator)
                {
                    pendingTogether = pendingTogether || (v.Text != null && v.Text.Trim() == "&");
                    continue;
                }
                if (v.Kind != ScriptValueKind.Paren)
                {
                    continue;
                }
                if (TryParseKey(v.Text, out CommandKey key))
                {
                    if (keys.Count > 0)
                    {
                        together[keys.Count - 1] = pendingTogether;
                    }
                    keys.Add(key);
                    together.Add(false);
                }
                else
                {
                    unknown++;
                }
                pendingTogether = false;
            }
            return keys.Count == 0 && unknown == 0 ? Empty : new SkillCommand(keys.ToArray(), together.ToArray(), unknown);
        }

        /// <summary>直接用键序列造一个指令(测试与程序化使用;所有连接都是先后)。</summary>
        public static SkillCommand Of(params CommandKey[] keys)
        {
            return OfTogether(keys);
        }

        /// <summary>同 <see cref="Of"/>,<paramref name="togetherAfter"/> 里列出与后一个键"同时"的键的下标。</summary>
        public static SkillCommand OfTogether(CommandKey[] keys, params int[] togetherAfter)
        {
            if (keys == null || keys.Length == 0)
            {
                return Empty;
            }
            var together = new bool[keys.Length];
            if (togetherAfter != null)
            {
                foreach (int i in togetherAfter)
                {
                    if (i >= 0 && i + 1 < keys.Length)
                    {
                        together[i] = true;
                    }
                }
            }
            return new SkillCommand((CommandKey[])keys.Clone(), together, 0);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _keys.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(_together[i - 1] ? '&' : '+');
                }
                sb.Append(_keys[i]);
            }
            return sb.ToString();
        }
    }
}
