using System;
using System.Collections.Generic;
using System.IO;

// 证据:客户端的"数字编号文字表"内嵌在 DNF.exe 的 PE 资源里(命名类型 "FILE"、资源号 157),是 "SC01" 容器(见 EmbeddedTextContainer),
// 明文是 UTF-8 文本,每个有效行是 "十进制编号>文字"(与 *.kor.str 同族,见 E-121,但键是数字)。台账 E-442。
namespace Dnf.Data
{
    /// <summary>
    /// 客户端内嵌的数字编号文字表:编号 → 文字(例如选频道界面第二行的固定文字)。构造后不可变。
    /// <para>
    /// 文本格式:每个有效行是 <c>&lt;十进制整数&gt;&gt;&lt;文字&gt;</c>(第一个 <c>&gt;</c> 之前必须全是 ASCII 数字、之后到行尾是文字,文字里可以再出现 <c>&gt;</c>);
    /// 行以 LF 或 CRLF 结尾(只去掉行尾的一个 <c>\r</c>);<c>//</c> 开头的行是注释,空行忽略;
    /// 文字原样保留(前后空白不裁;两个字符的 <c>\n</c> 是给渲染器看的换行记号,<b>不转义</b>)。
    /// 编号用 int 解析,超过 int 范围的行跳过;必须一行以数字开头(行首空白、负号、非数字键都不算条目)。
    /// </para>
    /// <para>
    /// 实现的选择:同一个编号出现多次时<b>取第一个</b>(客户端怎么处理重复没有读过;真实表里没有重复编号)。
    /// </para>
    /// </summary>
    public sealed class ClientTextTable
    {
        /// <summary>内嵌文字表所在的 PE 资源类型名(命名类型)。</summary>
        public const string ResourceTypeName = "FILE";

        /// <summary>内嵌文字表的资源号。同一类型下还有别的号(别的表),这里不用。</summary>
        public const int DefaultResourceId = 157;

        private readonly Dictionary<int, string> _texts;

        private ClientTextTable(Dictionary<int, string> texts)
        {
            _texts = texts;
        }

        /// <summary>条目数。</summary>
        public int Count => _texts.Count;

        /// <summary>按编号取文字(原样,含 <c>\n</c> 记号);没有这个编号返回 false(<paramref name="text"/> 为 null)。</summary>
        public bool TryGet(int id, out string text)
        {
            return _texts.TryGetValue(id, out text);
        }

        /// <summary>解析一份文字表文本;不认识的行一律跳过,不抛异常(参数为 null 才抛)。</summary>
        public static ClientTextTable Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            var texts = new Dictionary<int, string>();
            int pos = 0;
            while (pos < text.Length)
            {
                int newline = text.IndexOf('\n', pos);
                int end = newline < 0 ? text.Length : newline;
                int next = newline < 0 ? text.Length : newline + 1;
                if (end > pos && text[end - 1] == '\r')
                {
                    end--;
                }
                ParseLine(text, pos, end, texts);
                pos = next;
            }
            return new ClientTextTable(texts);
        }

        /// <summary>
        /// 从可执行文件(<c>DNF.exe</c>)的 PE 资源里读文字表(<see cref="ResourceTypeName"/>、<see cref="DefaultResourceId"/>):
        /// 以 <c>FileShare.ReadWrite | FileShare.Delete</c> 只读打开(客户端正在运行也读得到),只读头与那一小段资源。
        /// 文件不存在、不是 PE、没有这条资源、容器坏、解析出 0 项,都返回 false 且不抛异常(<paramref name="table"/> 为 null)。
        /// </summary>
        public static bool TryLoadFromExecutable(string exePath, out ClientTextTable table)
        {
            table = null;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }
            try
            {
                using (var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!PeResources.TryReadNamedResource(stream, ResourceTypeName, DefaultResourceId, out byte[] resource)
                        || !EmbeddedTextContainer.TryDecode(resource, out string text))
                    {
                        return false;
                    }
                    ClientTextTable parsed = Parse(text);
                    if (parsed.Count == 0)
                    {
                        return false;
                    }
                    table = parsed;
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (ArgumentException)
            {
            }
            return false;
        }

        private static void ParseLine(string text, int start, int end, Dictionary<int, string> texts)
        {
            if (end <= start)
            {
                return;
            }
            if (end - start >= 2 && text[start] == '/' && text[start + 1] == '/')
            {
                return;
            }
            long id = 0;
            int i = start;
            while (i < end && text[i] >= '0' && text[i] <= '9')
            {
                id = id * 10 + (text[i] - '0');
                if (id > int.MaxValue)
                {
                    return;
                }
                i++;
            }
            if (i == start || i >= end || text[i] != '>')
            {
                return;
            }
            int key = (int)id;
            if (!texts.ContainsKey(key))
            {
                texts.Add(key, text.Substring(i + 1, end - i - 1));
            }
        }
    }
}
