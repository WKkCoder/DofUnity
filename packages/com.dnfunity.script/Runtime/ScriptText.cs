using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Dnf.Script
{
    /// <summary>脚本里定义的一个函数:名字与形参名(含默认值的形参只取名字;`...` 变长参数记为 "...")。</summary>
    public sealed class ScriptFunction
    {
        public ScriptFunction(string name, string[] parameters)
        {
            Name = name;
            Parameters = parameters;
        }

        public string Name { get; }

        public string[] Parameters { get; }
    }

    /// <summary>脚本文本的辅助:脚本是字节串(原版用韩文代码页写注释),按"一个字节一个字符"处理。</summary>
    public static class ScriptText
    {
        public static byte[] ToBytes(string text)
        {
            byte[] b = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                b[i] = text[i] < 256 ? (byte)text[i] : (byte)'?';
            }
            return b;
        }

        public static string FromBytes(byte[] data)
        {
            char[] chars = new char[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                chars[i] = (char)data[i];
            }
            return new string(chars);
        }

        private static readonly Regex FunctionDefinition = new Regex(@"\bfunction\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)", RegexOptions.Compiled);

        /// <summary>粗略列出脚本里用 `function 名字(参数)` 定义的函数(先去掉注释与字符串,避免误判);同名只取第一处。</summary>
        public static List<ScriptFunction> DefinedFunctionSignatures(byte[] source)
        {
            string text = StripCommentsAndStrings(source);
            var functions = new List<ScriptFunction>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in FunctionDefinition.Matches(text))
            {
                string name = m.Groups[1].Value;
                if (!seen.Add(name))
                {
                    continue;
                }
                var parameters = new List<string>();
                foreach (string raw in m.Groups[2].Value.Split(','))
                {
                    string p = raw.Trim();
                    int eq = p.IndexOf('=');
                    if (eq >= 0)
                    {
                        p = p.Substring(0, eq).Trim();
                    }
                    if (p.Length > 0)
                    {
                        parameters.Add(p);
                    }
                }
                functions.Add(new ScriptFunction(name, parameters.ToArray()));
            }
            return functions;
        }

        /// <summary>同 <see cref="DefinedFunctionSignatures"/>,只要名字。</summary>
        public static List<string> DefinedFunctions(byte[] source)
        {
            var names = new List<string>();
            foreach (ScriptFunction f in DefinedFunctionSignatures(source))
            {
                names.Add(f.Name);
            }
            return names;
        }

        /// <summary>去掉 // 与 # 行注释、块注释、字符串字面量(含 @"" 逐字字符串),各用一个空格代替。</summary>
        public static string StripCommentsAndStrings(byte[] src)
        {
            var sb = new StringBuilder(src.Length);
            int n = src.Length;
            int i = 0;
            while (i < n)
            {
                byte c = src[i];
                byte next = i + 1 < n ? src[i + 1] : (byte)0;
                if ((c == '/' && next == '/') || c == '#')
                {
                    while (i < n && src[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/'))
                    {
                        i++;
                    }
                    i = Math.Min(n, i + 2);
                    sb.Append(' ');
                }
                else if (c == '@' && next == '"')
                {
                    i += 2;
                    while (i < n)
                    {
                        if (src[i] == '"')
                        {
                            if (i + 1 < n && src[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }
                            break;
                        }
                        i++;
                    }
                    i = Math.Min(n, i + 1);
                    sb.Append(' ');
                }
                else if (c == '"' || c == '\'')
                {
                    byte quote = c;
                    i++;
                    while (i < n && src[i] != quote && src[i] != '\n')
                    {
                        i += src[i] == '\\' ? 2 : 1;
                    }
                    i = Math.Min(n, i + 1);
                    sb.Append(' ');
                }
                else
                {
                    sb.Append((char)c);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>全大写字母、数字与下划线的名字当作常量(枚举值)。</summary>
        public static bool LooksLikeConstant(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            foreach (char ch in name)
            {
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_'))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
