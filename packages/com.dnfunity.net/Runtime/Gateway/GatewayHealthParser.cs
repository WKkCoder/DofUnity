using System;
using System.Text;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 从 <c>/health</c> 的 JSON 响应里取顶层的 status 与时间戳。运行时包没有 JSON 库可依赖(netstandard2.1 不带 System.Text.Json),
    /// 而这里只需要两个顶层字段,所以用一个很小的扫描器:字符串(含转义)、数字/true/false/null 字面量原样取文本,
    /// 嵌套的对象与数组整体跳过(其中的同名键不算)。不是合法 JSON 对象、缺 status、尾部有多余内容都返回 false。
    /// </summary>
    public static class GatewayHealthParser
    {
        /// <summary>解析;成功时 <paramref name="health"/> 非空。</summary>
        public static bool TryParse(string json, out GatewayHealth health)
        {
            health = null;
            if (json == null)
            {
                return false;
            }
            int pos = 0;
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                pos = 1;
            }
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] != '{')
            {
                return false;
            }
            pos++;
            string status = null;
            string timestamp = null;
            bool first = true;
            while (true)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length)
                {
                    return false;
                }
                if (json[pos] == '}')
                {
                    // 空对象 "{}" 或最后一个成员之后;逗号之后紧跟 '}'(尾随逗号)走不到这里,因为逗号后要求下一个是键
                    pos++;
                    break;
                }
                if (!first)
                {
                    if (json[pos] != ',')
                    {
                        return false;
                    }
                    pos++;
                    SkipWhitespace(json, ref pos);
                }
                string key;
                if (pos >= json.Length || json[pos] != '"' || !TryReadString(json, ref pos, out key))
                {
                    return false;
                }
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':')
                {
                    return false;
                }
                pos++;
                SkipWhitespace(json, ref pos);
                string value;
                if (pos >= json.Length || !TryReadValue(json, ref pos, out value))
                {
                    return false;
                }
                if (string.Equals(key, "status", StringComparison.OrdinalIgnoreCase))
                {
                    status = value;
                }
                else if (timestamp == null && IsTimestampKey(key))
                {
                    timestamp = value;
                }
                first = false;
            }
            SkipWhitespace(json, ref pos);
            if (pos != json.Length || status == null)
            {
                return false;
            }
            health = new GatewayHealth(status, timestamp);
            return true;
        }

        private static bool IsTimestampKey(string key)
        {
            foreach (string candidate in GatewayProtocol.HealthTimestampKeys)
            {
                if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n'))
            {
                pos++;
            }
        }

        /// <summary>读一个值:字符串给解码后的文本,数字/true/false 给字面量文本,null 与嵌套结构给 null。</summary>
        private static bool TryReadValue(string s, ref int pos, out string value)
        {
            value = null;
            char c = s[pos];
            if (c == '"')
            {
                return TryReadString(s, ref pos, out value);
            }
            if (c == '{' || c == '[')
            {
                return TrySkipNested(s, ref pos);
            }
            int start = pos;
            while (pos < s.Length && s[pos] != ',' && s[pos] != '}' && s[pos] != ']' && s[pos] != ' ' && s[pos] != '\t' && s[pos] != '\r' && s[pos] != '\n')
            {
                pos++;
            }
            string literal = s.Substring(start, pos - start);
            if (literal == "null")
            {
                return true;
            }
            if (literal == "true" || literal == "false" || IsNumber(literal))
            {
                value = literal;
                return true;
            }
            return false;
        }

        private static bool IsNumber(string literal)
        {
            if (literal.Length == 0)
            {
                return false;
            }
            bool digit = false;
            foreach (char c in literal)
            {
                if (c >= '0' && c <= '9')
                {
                    digit = true;
                }
                else if (c != '-' && c != '+' && c != '.' && c != 'e' && c != 'E')
                {
                    return false;
                }
            }
            return digit;
        }

        /// <summary>从开引号读到闭引号,处理转义;pos 停在闭引号之后。</summary>
        private static bool TryReadString(string s, ref int pos, out string value)
        {
            value = null;
            StringBuilder sb = new StringBuilder();
            pos++;
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"')
                {
                    value = sb.ToString();
                    return true;
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (pos >= s.Length)
                {
                    return false;
                }
                char e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        {
                            if (pos + 4 > s.Length)
                            {
                                return false;
                            }
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int h = HexDigit(s[pos + i]);
                                if (h < 0)
                                {
                                    return false;
                                }
                                code = (code << 4) | h;
                            }
                            pos += 4;
                            sb.Append((char)code);
                            break;
                        }
                    default:
                        return false;
                }
            }
            return false;
        }

        /// <summary>跳过一个嵌套的对象或数组(识别其中的字符串,括号不配对返回 false);pos 停在它之后。</summary>
        private static bool TrySkipNested(string s, ref int pos)
        {
            int depth = 0;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '"')
                {
                    string ignored;
                    if (!TryReadString(s, ref pos, out ignored))
                    {
                        return false;
                    }
                    continue;
                }
                pos++;
                if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }
            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }
            return -1;
        }
    }
}
