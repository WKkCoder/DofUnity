using System;
using System.Collections.Generic;

namespace Dnf.Game.Ai
{
    /// <summary>
    /// .ai 脚本语言的词表与编号(证据 E-052,来自客户端的三张静态表与解析函数的分支):
    ///   控制标签 7 个;内置条件类型 0–7;关键字表 A(类型号 0x08–0x3f);函数名表 B(代码 0x0e–0x1b);运算符表(代码 0–13)。
    /// 条件节点里存两个编号:类型号(哪个关键字)和内部码(怎么比较或怎么求值)。
    /// 编号只是引擎内部的约定,语义要看求值端;这里只管"读脚本时每个条件后面跟几个参数"。
    /// </summary>
    public static class AiVocabulary
    {
        // ---- 控制标签(下标即引擎里的编号) ----
        public const string Think = "[think]";
        public const string EndThink = "[/think]";
        public const string Return = "[return]";
        public const string True = "[true]";
        public const string False = "[false]";
        public const string ReturnStr = "[return str]";
        public const string ImportAi = "[import ai]";
        public const string AiPattern = "[ai pattern]";

        public const int CtlThink = 0;
        public const int CtlEndThink = 1;
        public const int CtlReturn = 2;
        public const int CtlTrue = 3;
        public const int CtlFalse = 4;
        public const int CtlReturnStr = 5;
        public const int CtlImport = 6;

        private static readonly string[] ControlTags =
        {
            Think, EndThink, Return, True, False, ReturnStr, ImportAi,
        };

        /// <summary>控制标签的编号 0–6;不是控制标签时 -1。</summary>
        public static int ControlIndex(string text)
        {
            if (text == null)
            {
                return -1;
            }
            for (int i = 0; i < ControlTags.Length; i++)
            {
                if (string.Equals(ControlTags[i], text, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        // ---- 条件类型号 ----
        /// <summary>[stat change] 的类型号:它只是个前缀,后面还要再读一个条件关键字。</summary>
        public const int KindStatChange = 0x0f;

        private static readonly string[] BuiltinKinds =
        {
            "[void]", "[mind]", "[hp]", "[mp]", "[state]", "[event]", "[random]", "[last result]",
        };

        /// <summary>关键字表 A:下标 0 对应类型号 0x08。</summary>
        private static readonly string[] TableA =
        {
            "[warlike]", "[weapon type]", "[target distance]", "[target state]", "[target combo bonus]",
            "[my active status]", "[my state]", "[stat change]", "[attack success]", "[hit]",
            "[element]", "[casting]", "[distance]", "[my active status on]", "[target active status]",
            "[attack type]", "[target type]", "[attack condition]", "[use skill]", "[cooltime]",
            "[my death]", "[target death]", "[revive]", "[combo]", "[aerial]",
            "[overkill]", "[set friend character]", "[set target monster]", "[set friend object]", "[set bound]",
            "[auto attack direction]", "[my attack]", "[my hp down]", "[my mp down]", "[check use skill]",
            "[check skill on]", "[check use target skill]", "[check casting target skill]", "[set party member]",
            "[party member alive]", "[party member hp]", "[check item slot cooltime]", "[skill cooltime correction]",
            "[my level]", "[check target direction]", "[check party buff on]", "[command]", "[cube item number]",
            "[second type]", "[check team]", "[set team]", "[map index]", "[check key cooltime]",
            "[set friend monster]", "[action after hit]", "[set target position]",
        };

        public const int FirstTableAKind = 0x08;

        /// <summary>关键字对应的类型号(内置 0–7、表 A 0x08–0x3f);不认识时 -1。</summary>
        public static int KindOf(string text)
        {
            if (text == null)
            {
                return -1;
            }
            for (int i = 0; i < BuiltinKinds.Length; i++)
            {
                if (string.Equals(BuiltinKinds[i], text, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            for (int i = 0; i < TableA.Length; i++)
            {
                if (string.Equals(TableA[i], text, StringComparison.Ordinal))
                {
                    return FirstTableAKind + i;
                }
            }
            return -1;
        }

        /// <summary>类型号对应的关键字文本;没有时 null。</summary>
        public static string KindName(int kind)
        {
            if (kind >= 0 && kind < BuiltinKinds.Length)
            {
                return BuiltinKinds[kind];
            }
            int a = kind - FirstTableAKind;
            return a >= 0 && a < TableA.Length ? TableA[a] : null;
        }

        // ---- 运算符表与函数名表 ----
        /// <summary>运算符表:下标即内部码 0–13。其中 4 个是函数名风格的写法([void] 与 [my level] 用到)。</summary>
        private static readonly string[] Operators =
        {
            "check random()", "check bonus random()", "%>=", ">=", "==", "is in range()", "get random()",
            "%==", "%>", ">", "%<=", "<=", "%<", "<",
        };

        /// <summary>函数名表 B:下标 0 对应代码 0x0e。</summary>
        private static readonly string[] Functions =
        {
            "is target in attack area()", "is target in attack area_box()", "is target in attack area_surround()",
            "is target in attack area_absolute()", "enemy number of in attack area()", "is the skill in cooltime()",
            "number of in attack area()", "party member number()", "is attack action enable()",
            "is custom action enable()", "damage check()", "is target object in area()",
            "is the load skill in cooltime()", "is free state()",
        };

        public const int FirstFunctionCode = 0x0e;

        /// <summary>运算符或函数名对应的内部码:运算符 0–13,函数 0x0e–0x1b;不认识时 -1。</summary>
        public static int OperatorCode(string text)
        {
            if (text == null)
            {
                return -1;
            }
            for (int i = 0; i < Operators.Length; i++)
            {
                if (string.Equals(Operators[i], text, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            for (int i = 0; i < Functions.Length; i++)
            {
                if (string.Equals(Functions[i], text, StringComparison.Ordinal))
                {
                    return FirstFunctionCode + i;
                }
            }
            return -1;
        }

        /// <summary>内部码对应的运算符或函数名;没有时 null。</summary>
        public static string OperatorName(int code)
        {
            if (code >= 0 && code < Operators.Length)
            {
                return Operators[code];
            }
            int f = code - FirstFunctionCode;
            return f >= 0 && f < Functions.Length ? Functions[f] : null;
        }

        // ---- 类型号 → 内部码 ----
        private static readonly HashSet<int> FixedEqualsKinds = new HashSet<int>
        {
            0x0b, 0x0e, 0x10, 0x11, 0x13, 0x14, 0x17, 0x19, 0x1a, 0x1c, 0x1d, 0x1e, 0x21, 0x3e,
        };

        private static readonly Dictionary<int, int> KindToCode = new Dictionary<int, int>
        {
            { 0x0d, 0x1d }, { 0x12, 0x34 }, { 0x15, 0x20 }, { 0x16, 0x1e }, { 0x18, 0x1f }, { 0x1b, 0x33 },
            { 0x22, 0x21 }, { 0x23, 0x22 }, { 0x24, 0x23 }, { 0x25, 0x24 }, { 0x26, 0x25 }, { 0x27, 0x26 },
            { 0x28, 0x27 }, { 0x29, 0x28 }, { 0x2a, 0x29 }, { 0x2b, 0x2a }, { 0x2c, 0x2b }, { 0x2d, 0x2c },
            { 0x2e, 0x2d }, { 0x2f, 0x2e }, { 0x31, 0x2f }, { 0x32, 0x30 }, { 0x34, 0x31 }, { 0x35, 0x32 },
            { 0x39, 0x35 }, { 0x3a, 0x36 }, { 0x3c, 0x37 }, { 0x3d, 0x38 }, { 0x3f, 0x39 },
        };

        /// <summary>"等于"这个内部码:不读运算符的关键字都用它。</summary>
        public const int CodeEquals = 4;

        /// <summary>"检查键冷却"的内部码:参数是一个字符串键名。</summary>
        public const int CodeKeyName = 0x37;

        /// <summary>"自己的攻击"的内部码:读完一个参数后内部码改成"等于"。</summary>
        public const int CodeMyAttack = 0x26;

        /// <summary>这个类型号的条件后面要不要读一个运算符(或函数名)。</summary>
        public static bool ReadsOperator(int kind)
        {
            return !FixedEqualsKinds.Contains(kind) && !KindToCode.ContainsKey(kind);
        }

        /// <summary>
        /// 不读运算符的类型号的内部码:一批固定用"等于",其余各有自己的码。
        /// 要读运算符的类型号(<see cref="ReadsOperator"/>)返回 -1,内部码由运算符决定。
        /// </summary>
        public static int FixedCodeOf(int kind)
        {
            if (FixedEqualsKinds.Contains(kind))
            {
                return CodeEquals;
            }
            return KindToCode.TryGetValue(kind, out int code) ? code : -1;
        }

        /// <summary>某个内部码的条件后面跟几个参数(数字,或在 OperatingValue.lst 里有定义的名字)。键名码的 1 个参数是字符串键。</summary>
        public static int ParamCount(int code)
        {
            switch (code)
            {
                case 0:
                case 1:
                case 5:
                case 6:
                case 0x16:
                case 0x17:
                case 0x22:
                case 0x27:
                case 0x28:
                case 0x2b:
                case 0x2c:
                case 0x30:
                case 0x32:
                case 0x39:
                    return 2;
                case 2:
                case 3:
                case 4:
                case 7:
                case 8:
                case 9:
                case 10:
                case 11:
                case 12:
                case 13:
                case 0x13:
                case 0x18:
                case 0x1a:
                case 0x1d:
                case 0x1e:
                case 0x1f:
                case 0x20:
                case 0x21:
                case 0x23:
                case 0x25:
                case 0x26:
                case 0x29:
                case 0x2a:
                case 0x2d:
                case 0x2f:
                case 0x33:
                case 0x34:
                case 0x35:
                case 0x36:
                case 0x37:
                case 0x38:
                    return 1;
                case 0x0e:
                case 0x12:
                case 0x24:
                    return 4;
                case 0x0f:
                case 0x11:
                    return 6;
                case 0x10:
                    return 8;
                case 0x14:
                case 0x19:
                    return 7;
                default:
                    return 0;
            }
        }
    }
}
