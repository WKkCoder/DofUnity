using System;
using System.Collections.Generic;

// 证据:标记流脚本的语法(5 字节标记、类型 2 到 10)见 E-003;全库对拍见 E-008。
namespace Dnf.Data
{
    /// <summary>脚本值的种类(来自标记类型;含义见 <see cref="TokenType"/>)。</summary>
    public enum ScriptValueKind : byte
    {
        Int,
        /// <summary>以十六进制书写的整数(标记类型 3)。</summary>
        Hex,
        Float,
        /// <summary>字符串或关键字(标记类型 7,值是字符串池下标)。</summary>
        String,
        /// <summary>括号词,如 (UP)(标记类型 6,值按字符串池下标解释;越界时 Text 为 null)。</summary>
        Paren,
        /// <summary>"种类 + 键"的字符串引用(标记类型 9 加 10,键是字符串池下标)。<see cref="ScriptValue.Int"/> 是种类,<see cref="ScriptValue.Text"/> 是键。</summary>
        Keyed,
        /// <summary>分隔符(标记类型 8,值是字符串池下标:<see cref="ScriptValue.Text"/> 是分隔字符本身,如 "," 或 "&amp;";越界时为 null,E-140)。</summary>
        Separator,
        /// <summary>没有识别的标记类型(<see cref="ScriptValue.Int"/> 里是原始值)。</summary>
        Other,
    }

    public readonly struct ScriptValue
    {
        public ScriptValueKind Kind { get; }
        public int Int { get; }
        public string Text { get; }

        public ScriptValue(ScriptValueKind kind, int number, string text)
        {
            Kind = kind;
            Int = number;
            Text = text;
        }

        public float Float => BitConverter.Int32BitsToSingle(Int);

        public bool IsNumber => Kind == ScriptValueKind.Int || Kind == ScriptValueKind.Hex;
    }

    /// <summary>一个标签与它后面的值,直到下一个标签。文件开头第一个标签之前的值挂在 Tag 为 null 的行上。</summary>
    public sealed class ScriptLine
    {
        public string Tag { get; }
        public ScriptValue[] Values { get; }

        public ScriptLine(string tag, ScriptValue[] values)
        {
            Tag = tag;
            Values = values;
        }

        public bool TagIs(string tag) => string.Equals(Tag, tag, StringComparison.OrdinalIgnoreCase);

        /// <summary>所有整数值(Int 与 Hex),按出现顺序。</summary>
        public List<int> Ints()
        {
            var list = new List<int>(Values.Length);
            foreach (ScriptValue v in Values)
            {
                if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
            }
            return list;
        }

        /// <summary>第一个数值(整数、十六进制整数或浮点数,按出现顺序);没有则返回 null。(同一个属性在不同脚本里可能写成整数或浮点。)</summary>
        public double? FirstNumber()
        {
            foreach (ScriptValue v in Values)
            {
                if (v.IsNumber)
                {
                    return v.Int;
                }
                if (v.Kind == ScriptValueKind.Float)
                {
                    return v.Float;
                }
            }
            return null;
        }

        /// <summary>第一个字符串值(去掉引号后的内容);没有则返回 null。</summary>
        public string FirstString()
        {
            foreach (ScriptValue v in Values)
            {
                if (v.Kind == ScriptValueKind.String)
                {
                    return v.Text;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 把标记流脚本按"一个标签一行"解成结构,方便按标签名取值。只是语法层:不知道任何标签的语义。
    /// 与 tools/py/script_dump.py 的 decode_text 同一套规则(那边只用来给人看)。
    /// </summary>
    public static class ScriptDocument
    {
        public static ScriptLine[] Parse(ReadOnlySpan<byte> data, PvfStringTable strings)
        {
            if (strings == null)
            {
                throw new ArgumentNullException(nameof(strings));
            }
            ScriptToken[] tokens = TokenScript.Parse(data);
            var lines = new List<ScriptLine>();
            string tag = null;
            bool haveTag = false;
            var values = new List<ScriptValue>();

            for (int i = 0; i < tokens.Length; i++)
            {
                ScriptToken t = tokens[i];
                switch (t.Type)
                {
                    case (byte)TokenType.Tag:
                        if (haveTag || values.Count > 0)
                        {
                            lines.Add(new ScriptLine(tag, values.ToArray()));
                        }
                        tag = StringAt(strings, t.Value);
                        haveTag = true;
                        values.Clear();
                        break;
                    case (byte)TokenType.Int:
                        values.Add(new ScriptValue(ScriptValueKind.Int, t.Value, null));
                        break;
                    case (byte)TokenType.UInt:
                        values.Add(new ScriptValue(ScriptValueKind.Hex, t.Value, null));
                        break;
                    case (byte)TokenType.Float:
                        values.Add(new ScriptValue(ScriptValueKind.Float, t.Value, null));
                        break;
                    case (byte)TokenType.String:
                        values.Add(new ScriptValue(ScriptValueKind.String, t.Value, StringAt(strings, t.Value)));
                        break;
                    case (byte)TokenType.Paren:
                        values.Add(new ScriptValue(ScriptValueKind.Paren, t.Value, StringAt(strings, t.Value)));
                        break;
                    case (byte)TokenType.Separator:
                        values.Add(new ScriptValue(ScriptValueKind.Separator, t.Value, StringAt(strings, t.Value)));
                        break;
                    case (byte)TokenType.StringRefKind:
                        if (i + 1 < tokens.Length && tokens[i + 1].Type == (byte)TokenType.StringRefKey)
                        {
                            values.Add(new ScriptValue(ScriptValueKind.Keyed, t.Value, StringAt(strings, tokens[i + 1].Value)));
                            i++;
                        }
                        else
                        {
                            values.Add(new ScriptValue(ScriptValueKind.Other, t.Value, null));
                        }
                        break;
                    default:
                        values.Add(new ScriptValue(ScriptValueKind.Other, t.Value, null));
                        break;
                }
            }
            if (haveTag || values.Count > 0)
            {
                lines.Add(new ScriptLine(tag, values.ToArray()));
            }
            return lines.ToArray();
        }

        private static string StringAt(PvfStringTable strings, int index)
        {
            return (uint)index < (uint)strings.Count ? strings[index] : null;
        }
    }
}
