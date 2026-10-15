using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game.Ai
{
    /// <summary>
    /// .ai 脚本的解析器,复现客户端加载函数的读法(证据 E-052,在全库 .ai 上验证):
    ///   文件开头可选 [ai pattern],然后是下面四种之一:一个 [think] 块、[return] 值、[return str] 名字、[import ai] 文件;
    ///   [think] 块 = 一个条件 + 至多两路分支([true]/[false],各接 [think]、[return]、[return str] 或 [import ai])+ [/think];
    ///   条件 = 关键字 [运算符或函数名] 参数们,每个关键字后面读几个参数由它的内部码决定(见 <see cref="AiVocabulary"/>);
    ///   紧跟条件的 [think] 或 [import ai](不写 [true])按"真"分支算。
    /// 原版的读取是宽容的:认不出的控制标签只是让当前结点结束,[/ai pattern] 之类的尾标签根本不读。这里照样处理,
    /// 并把没读到的标记记在 <see cref="AiScript.Trailing"/> 里;只有未知关键字、未知运算符、参数类型错这类原版会打日志的情形才算错误。
    /// </summary>
    public static class AiParser
    {
        /// <summary>解析 .ai 文件的原始字节(标记流)。<paramref name="constants"/> 为 null 时,名字参数不查表(值为 NaN),也不当作错误。</summary>
        public static AiScript Parse(ReadOnlySpan<byte> raw, PvfStringTable strings, AiConstants constants = null)
        {
            return Parse(TokenScript.Parse(raw), strings, constants);
        }

        public static AiScript Parse(ScriptToken[] tokens, PvfStringTable strings, AiConstants constants = null)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }
            var parser = new State(tokens, strings, constants);
            AiResult root = parser.ParseFile();
            return parser.Finish(root);
        }

        private sealed class State
        {
            private readonly ScriptCursor _cursor;
            private readonly ScriptToken[] _tokens;
            private readonly AiConstants _constants;
            private readonly List<string> _errors = new List<string>();
            private int _depth;
            private int _maxDepth;
            private int _nodes;

            public State(ScriptToken[] tokens, PvfStringTable strings, AiConstants constants)
            {
                _tokens = tokens;
                _cursor = new ScriptCursor(tokens, strings);
                _constants = constants;
            }

            public AiScript Finish(AiResult root)
            {
                var trailing = new List<string>();
                for (int i = _cursor.Position; i < _tokens.Length && trailing.Count < 6; i++)
                {
                    ScriptToken t = _tokens[i];
                    trailing.Add(t.Type == (byte)TokenType.Tag || t.Type == (byte)TokenType.String
                        ? (_cursor.TextOf(t.Value) ?? string.Empty)
                        : "<" + t.Type + ">");
                }
                return new AiScript(root, _errors, _nodes, _maxDepth, Math.Min(_cursor.Position, _tokens.Length), _tokens.Length, trailing);
            }

            // ---- 文件级 ----
            public AiResult ParseFile()
            {
                string word = ReadWord();
                if (string.Equals(word, AiVocabulary.AiPattern, StringComparison.Ordinal))
                {
                    word = ReadWord();
                }
                switch (AiVocabulary.ControlIndex(word))
                {
                    case AiVocabulary.CtlThink:
                        AiNode node = ParseNode();
                        return node != null ? AiResult.OfThink(node) : AiResult.Default;
                    case AiVocabulary.CtlReturn:
                        return ReadLeaf();
                    case AiVocabulary.CtlReturnStr:
                        return ReadNamedReturn();
                    case AiVocabulary.CtlImport:
                        return ReadImport();
                    default:
                        return AiResult.Default;
                }
            }

            // ---- 一个 [think] 块(开标签已经读过) ----
            private AiNode ParseNode()
            {
                _depth++;
                _nodes++;
                if (_depth > _maxDepth)
                {
                    _maxDepth = _depth;
                }
                try
                {
                    return ParseNodeBody();
                }
                finally
                {
                    _depth--;
                }
            }

            private AiNode ParseNodeBody()
            {
                int at = _cursor.Position;
                string keyword = ReadWord();
                int kind = AiVocabulary.KindOf(keyword);
                bool prefix = false;
                if (kind == AiVocabulary.KindStatChange)
                {
                    prefix = true;
                    keyword = ReadWord();
                    kind = AiVocabulary.KindOf(keyword);
                }
                if (kind < 0)
                {
                    Error(at, "invalid condition type");
                    return null;
                }

                int code;
                string op = null;
                if (AiVocabulary.ReadsOperator(kind))
                {
                    int opAt = _cursor.Position;
                    op = ReadString();
                    code = AiVocabulary.OperatorCode(op);
                    if (code < 0)
                    {
                        Error(opAt, "invalid operator");
                        return null;
                    }
                }
                else
                {
                    code = AiVocabulary.FixedCodeOf(kind);
                }

                AiParam[] parameters;
                if (code == AiVocabulary.CodeKeyName)
                {
                    string key = ReadString();
                    parameters = new[] { new AiParam(float.NaN, key) };
                }
                else
                {
                    int n = AiVocabulary.ParamCount(code);
                    parameters = new AiParam[n];
                    for (int i = 0; i < n; i++)
                    {
                        parameters[i] = ReadParam();
                    }
                }
                if (code == AiVocabulary.CodeMyAttack)
                {
                    code = AiVocabulary.CodeEquals;
                }

                var node = new AiNode(kind, code, keyword, op, prefix, parameters);
                int branches = 0;
                while (true)
                {
                    string word = ReadWord();
                    switch (AiVocabulary.ControlIndex(word))
                    {
                        case AiVocabulary.CtlThink:
                            // 紧跟条件的 [think]:嵌套块算"真"分支,随后必须是外层的 [/think]
                            node.True = ParseChild();
                            ReadWord();
                            return node;
                        case AiVocabulary.CtlImport:
                            node.True = ReadImport();
                            return node;
                        case AiVocabulary.CtlTrue:
                        case AiVocabulary.CtlFalse:
                            bool isTrue = AiVocabulary.ControlIndex(word) == AiVocabulary.CtlTrue;
                            if (!ReadBranchBody(out AiResult result))
                            {
                                return node;
                            }
                            if (isTrue)
                            {
                                node.True = result;
                            }
                            else
                            {
                                node.False = result;
                            }
                            branches++;
                            if (branches > 1)
                            {
                                ReadWord(); // 两路都读完以后的 [/think]
                                return node;
                            }
                            continue;
                        default:
                            // [/think],或认不出的标签(笔误的闭合标签也在这里):结点到此结束
                            return node;
                    }
                }
            }

            /// <summary>[true]/[false] 后面的一项。读到不认识的东西时返回 false,外层结点随之结束。</summary>
            private bool ReadBranchBody(out AiResult result)
            {
                result = AiResult.Default;
                string word = ReadWord();
                switch (AiVocabulary.ControlIndex(word))
                {
                    case AiVocabulary.CtlThink:
                        result = ParseChild();
                        return true;
                    case AiVocabulary.CtlReturn:
                        result = ReadLeaf();
                        return true;
                    case AiVocabulary.CtlReturnStr:
                        result = ReadNamedReturn();
                        return true;
                    case AiVocabulary.CtlImport:
                        result = ReadImport();
                        return true;
                    default:
                        return false;
                }
            }

            private AiResult ParseChild()
            {
                AiNode child = ParseNode();
                return child != null ? AiResult.OfThink(child) : AiResult.Default;
            }

            // ---- 读取原语(对应原版的几个"读下一个标记"函数) ----

            /// <summary>读一个标签或字符串;别的类型记一条错误,返回 null(那个标记已被消耗)。</summary>
            private string ReadWord()
            {
                int at = _cursor.Position;
                ScriptToken t = _cursor.Next();
                if (t.Type == (byte)TokenType.Tag || t.Type == (byte)TokenType.String)
                {
                    return _cursor.TextOf(t.Value) ?? string.Empty;
                }
                Error(at, "invalid type or string");
                return null;
            }

            /// <summary>读一个字符串(或"种类 + 键"对);别的类型记一条错误,返回 null。</summary>
            private string ReadString()
            {
                int at = _cursor.Position;
                if (_cursor.ScanStr(out string text))
                {
                    return text;
                }
                Error(at, "invalid string");
                return null;
            }

            /// <summary>读 [return] 后面的值:数字或名字。</summary>
            private AiResult ReadLeaf()
            {
                int at = _cursor.Position;
                ScriptToken t = _cursor.Next();
                switch (t.Type)
                {
                    case (byte)TokenType.Int:
                    case (byte)TokenType.UInt:
                        return AiResult.OfNumber(t.Value);
                    case (byte)TokenType.Float:
                        return AiResult.OfNumber(t.AsFloat);
                    case (byte)TokenType.String:
                        return AiResult.OfName(_cursor.TextOf(t.Value) ?? string.Empty);
                    case (byte)TokenType.StringRefKind:
                        ScriptToken key = _cursor.Next();
                        if (key.Type == (byte)TokenType.StringRefKey)
                        {
                            return AiResult.OfName(_cursor.TextOf(key.Value) ?? string.Empty);
                        }
                        break;
                }
                Error(at, "invalid return value");
                return AiResult.Default;
            }

            private AiResult ReadNamedReturn()
            {
                string name = ReadString();
                return name != null ? AiResult.OfName(name) : AiResult.Default;
            }

            private AiResult ReadImport()
            {
                string file = ReadString();
                return file != null ? AiResult.OfImport(file) : AiResult.Default;
            }

            /// <summary>读条件的一个参数:数字,或在名表里有定义的名字。</summary>
            private AiParam ReadParam()
            {
                int at = _cursor.Position;
                ScriptToken t = _cursor.Next();
                switch (t.Type)
                {
                    case (byte)TokenType.Int:
                    case (byte)TokenType.UInt:
                        return new AiParam(t.Value, null);
                    case (byte)TokenType.Float:
                        return new AiParam(t.AsFloat, null);
                    case (byte)TokenType.String:
                        string name = _cursor.TextOf(t.Value) ?? string.Empty;
                        if (_constants == null)
                        {
                            return new AiParam(float.NaN, name);
                        }
                        if (_constants.TryGet(name, out float value))
                        {
                            return new AiParam(value, name);
                        }
                        Error(at, "invalid parameter");
                        return new AiParam(float.NaN, name);
                }
                Error(at, "invalid parameter");
                return new AiParam(0f, null);
            }

            private void Error(int tokenIndex, string message)
            {
                _errors.Add(message + "(第 " + tokenIndex + " 个标记)");
            }
        }
    }
}
