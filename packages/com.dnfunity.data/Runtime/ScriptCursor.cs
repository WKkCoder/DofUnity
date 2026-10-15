using System;

namespace Dnf.Data
{
    /// <summary>
    /// 标记流的读取游标,复现原版加载代码里"逐个取标记"的原语(证据 E-047):
    /// 每次读取都前进一个标记,读到的不是想要的类型也已经消耗掉(不回退);读完以后一直返回"结束"(类型 0)。
    /// 各 Scan 方法对应原版加载代码里的五个读取函数,它们的宽容规则一并保留:
    ///   整数位置遇到浮点按向零取整,遇到别的标记返回 0 并报告失败;
    ///   浮点位置接受整数(转成 float),其它同上;
    ///   关键字位置接受标签、字符串或"种类 + 键"对,中间夹着的数值会被丢弃;
    ///   找标签时遇到的非标签标记都会被丢弃,并让这次读取算作"不干净"。
    /// 这些宽容规则决定了数据里有笔误时原版"读出什么",所以数据层照原样保留,而不是做成严格的语法检查。
    /// </summary>
    public sealed class ScriptCursor
    {
        /// <summary>读完以后返回的标记类型。</summary>
        public const byte EndType = 0;

        private readonly ScriptToken[] _tokens;
        private readonly PvfStringTable _strings;
        private int _position;

        public ScriptCursor(ScriptToken[] tokens, PvfStringTable strings)
        {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        }

        /// <summary>下一个要读的标记的下标。</summary>
        public int Position => _position;

        public int Length => _tokens.Length;

        public bool AtEnd => _position >= _tokens.Length;

        /// <summary>为了找到想要的标记而被丢弃的标记个数(统计用,不影响读取)。</summary>
        public int DiscardedTokens { get; private set; }

        /// <summary>取下一个标记并前进;已经读完时返回类型 0。</summary>
        public ScriptToken Next()
        {
            if (_position >= _tokens.Length)
            {
                return new ScriptToken(EndType, 0);
            }
            return _tokens[_position++];
        }

        /// <summary>标记值作为字符串池下标的文本;下标越界时为 null。</summary>
        public string TextOf(int stringIndex)
        {
            return (uint)stringIndex < (uint)_strings.Count ? _strings[stringIndex] : null;
        }

        /// <summary>
        /// 读下一个标签。只有"第一个标记就是标签"时返回 true;否则丢弃标记直到遇到标签或结尾,返回 false
        /// (找到的标签名仍通过 <paramref name="name"/> 给出,结尾时为 null)。
        /// 调用方(原版的顶层循环)在返回 false 时就停止加载,所以数据里一个游离的值会让它后面的内容全部读不到。
        /// <paramref name="report"/> 对应原版的"是否写错误日志"参数:为 false 时(跳过整节的模块内容)丢弃标记不算不规整,不计入 <see cref="DiscardedTokens"/>。
        /// </summary>
        public bool ScanType(out string name, bool report = true)
        {
            bool clean = true;
            name = null;
            while (true)
            {
                ScriptToken t = Next();
                if (t.Type == (byte)TokenType.Tag)
                {
                    name = TextOf(t.Value);
                }
                else
                {
                    clean = false;
                }
                if (t.Type == (byte)TokenType.Tag || t.Type == EndType)
                {
                    return clean;
                }
                if (report)
                {
                    DiscardedTokens++;
                }
            }
        }

        /// <summary>
        /// 读一个关键字:标签(取标签名)、字符串(取内容)或"种类 + 键"对(取键)。之前夹着的其它标记被丢弃;读到结尾返回 null。
        /// 关键字不在调用方认识的集合里时,调用方保持字段的默认值(这是原版对拼写错误的处理方式)。
        /// </summary>
        public string ScanTypeOrStr()
        {
            while (true)
            {
                ScriptToken t = Next();
                switch (t.Type)
                {
                    case (byte)TokenType.String:
                    case (byte)TokenType.Tag:
                        return TextOf(t.Value) ?? string.Empty;
                    case (byte)TokenType.StringRefKind:
                        ScriptToken key = Next();
                        if (key.Type == (byte)TokenType.StringRefKey)
                        {
                            return TextOf(key.Value) ?? string.Empty;
                        }
                        break;
                    case EndType:
                        return null;
                }
                DiscardedTokens++;
            }
        }

        /// <summary>读一个整数;浮点向零取整;读到别的标记时返回 0、<paramref name="ok"/> 为 false(那个标记已被消耗)。</summary>
        public int ScanInt(out bool ok)
        {
            ScriptToken t = Next();
            switch (t.Type)
            {
                case (byte)TokenType.Int:
                case (byte)TokenType.UInt:
                    ok = true;
                    return t.Value;
                case (byte)TokenType.Float:
                    ok = true;
                    return TruncateToInt(t.AsFloat);
                default:
                    ok = false;
                    return 0;
            }
        }

        /// <summary>读一个浮点数;整数转成 float;读到别的标记时返回 0、<paramref name="ok"/> 为 false(那个标记已被消耗)。</summary>
        public float ScanFloat(out bool ok)
        {
            ScriptToken t = Next();
            switch (t.Type)
            {
                case (byte)TokenType.Int:
                case (byte)TokenType.UInt:
                    ok = true;
                    return (float)t.Value;
                case (byte)TokenType.Float:
                    ok = true;
                    return t.AsFloat;
                default:
                    ok = false;
                    return 0f;
            }
        }

        /// <summary>读一个字符串(字符串标记,或"种类 + 键"对取键);读到别的标记时返回 false(那个标记已被消耗)。</summary>
        public bool ScanStr(out string text)
        {
            ScriptToken t = Next();
            if (t.Type == (byte)TokenType.String)
            {
                text = TextOf(t.Value) ?? string.Empty;
                return true;
            }
            if (t.Type == (byte)TokenType.StringRefKind)
            {
                ScriptToken key = Next();
                if (key.Type == (byte)TokenType.StringRefKey)
                {
                    text = TextOf(key.Value) ?? string.Empty;
                    return true;
                }
            }
            text = null;
            return false;
        }

        /// <summary>float 转 int:向零取整;超出范围或非数时给 int.MinValue(与 x87 的 fistp 在"向零舍入"下一致)。</summary>
        public static int TruncateToInt(float value)
        {
            if (float.IsNaN(value) || value >= 2147483648f || value < -2147483648f)
            {
                return int.MinValue;
            }
            return (int)value;
        }
    }
}
