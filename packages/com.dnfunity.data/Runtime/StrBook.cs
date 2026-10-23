using System;
using System.Collections.Generic;

// 证据:*.kor.str 的格式见 E-121(每行 键>值,CRLF 换行,// 开头是注释,值里的 \n 两个字符表示换行,编码 GB18030)。
namespace Dnf.Data
{
    /// <summary>
    /// 一份 *.kor.str 文本表(物品名与说明、NPC 对白、任务、界面文字……):每行 <c>键&gt;值</c>,行尾 CRLF(兼容 LF),
    /// <c>//</c> 开头的是注释,空行忽略;值是 GB18030 字节,值里的 <c>\n</c>(反斜杠加字母 n)表示换行。
    /// 构造时只扫一遍字节、记下每个键对应的字节范围;值在第一次被取用时才解码(<c>equipment.kor.str</c> 有 8 MB 上下),之后缓存。
    /// 同一个键出现两次时后面的覆盖前面的(真实数据里没有重复键)。
    /// </summary>
    public sealed class StrBook
    {
        private readonly byte[] _data;
        private readonly Dictionary<string, long> _spans = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);

        public StrBook(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            Parse();
        }

        /// <summary>键值对的个数。</summary>
        public int Count => _spans.Count;

        public IEnumerable<string> Keys => _spans.Keys;

        public bool Contains(string key)
        {
            return key != null && _spans.ContainsKey(key);
        }

        /// <summary>取一个键的文本(已解码、<c>\n</c> 已换成换行);键不存在返回 false。</summary>
        public bool TryGet(string key, out string text)
        {
            text = null;
            if (key == null)
            {
                return false;
            }
            if (_cache.TryGetValue(key, out text))
            {
                return true;
            }
            if (!_spans.TryGetValue(key, out long span))
            {
                return false;
            }
            int start = (int)(span >> 32);
            int length = (int)(span & 0xFFFFFFFFL);
            text = Gb18030.Decode(_data, start, length).Replace("\\n", "\n");
            _cache[key] = text;
            return true;
        }

        /// <summary>取一个键的文本;不存在返回 null。</summary>
        public string Get(string key)
        {
            return TryGet(key, out string text) ? text : null;
        }

        private void Parse()
        {
            byte[] d = _data;
            int n = d.Length;
            int pos = 0;
            if (n >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF)
            {
                pos = 3;
            }
            while (pos < n)
            {
                int end = Array.IndexOf(d, (byte)'\n', pos);
                if (end < 0)
                {
                    end = n;
                }
                int lineEnd = end;
                if (lineEnd > pos && d[lineEnd - 1] == (byte)'\r')
                {
                    lineEnd--;
                }
                int next = end + 1;
                bool comment = lineEnd - pos >= 2 && d[pos] == (byte)'/' && d[pos + 1] == (byte)'/';
                if (lineEnd > pos && !comment)
                {
                    int gt = Array.IndexOf(d, (byte)'>', pos, lineEnd - pos);
                    if (gt > pos)
                    {
                        string key = Latin1.Decode(new ReadOnlySpan<byte>(d, pos, gt - pos));
                        int start = gt + 1;
                        _spans[key] = ((long)start << 32) | (uint)(lineEnd - start);
                    }
                }
                pos = next;
            }
        }
    }
}
