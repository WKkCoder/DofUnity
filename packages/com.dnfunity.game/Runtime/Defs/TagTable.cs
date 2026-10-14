using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 把一份标记流脚本(.chr、.skl、.atk、.obj 等)按标签名索引起来:同名标签取第一次出现。
    /// 标签名带方括号、不区分大小写,例如 "[etc motion]"。只是语法层的便利,不替任何标签赋予含义。
    /// </summary>
    public sealed class TagTable
    {
        private readonly Dictionary<string, ScriptLine> _first = new Dictionary<string, ScriptLine>(StringComparer.OrdinalIgnoreCase);

        public ScriptLine[] Lines { get; }

        public TagTable(ScriptLine[] lines)
        {
            Lines = lines ?? throw new ArgumentNullException(nameof(lines));
            foreach (ScriptLine line in lines)
            {
                if (line.Tag != null && !_first.ContainsKey(line.Tag))
                {
                    _first.Add(line.Tag, line);
                }
            }
        }

        public bool Has(string tag)
        {
            return _first.ContainsKey(tag);
        }

        public bool TryGetLine(string tag, out ScriptLine line)
        {
            return _first.TryGetValue(tag, out line);
        }

        /// <summary>某个标签那一行里所有字符串值(去掉引号后的内容),按出现顺序;没有该标签则返回空数组。</summary>
        public string[] Strings(string tag)
        {
            if (!_first.TryGetValue(tag, out ScriptLine line))
            {
                return Array.Empty<string>();
            }
            var list = new List<string>();
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.String)
                {
                    list.Add(v.Text);
                }
            }
            return list.ToArray();
        }

        /// <summary>某个标签那一行里的所有整数,按出现顺序;没有该标签则返回空数组。</summary>
        public int[] Ints(string tag)
        {
            return _first.TryGetValue(tag, out ScriptLine line) ? line.Ints().ToArray() : Array.Empty<int>();
        }

        /// <summary>某个标签那一行里的第一个整数;没有则返回 fallback。</summary>
        public int IntOr(string tag, int fallback)
        {
            int[] v = Ints(tag);
            return v.Length > 0 ? v[0] : fallback;
        }

        /// <summary>某个标签那一行里的第一个字符串;没有则返回 null。</summary>
        public string FirstString(string tag)
        {
            return _first.TryGetValue(tag, out ScriptLine line) ? line.FirstString() : null;
        }

        /// <summary>
        /// 解析"编号 + 路径"成对出现的列表脚本(*.lst):文件开头没有标签,值依次是 编号 "路径" 编号 "路径" ……。
        /// 路径保持原样(相对列表所在的目录由调用方处理)。
        /// </summary>
        public static Dictionary<int, string> ParseList(ScriptLine[] lines)
        {
            var map = new Dictionary<int, string>();
            foreach (ScriptLine line in lines)
            {
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 1 < v.Length; i++)
                {
                    if (v[i].IsNumber && v[i + 1].Kind == ScriptValueKind.String)
                    {
                        if (!map.ContainsKey(v[i].Int))
                        {
                            map.Add(v[i].Int, v[i + 1].Text);
                        }
                        i++;
                    }
                }
            }
            return map;
        }
    }
}
