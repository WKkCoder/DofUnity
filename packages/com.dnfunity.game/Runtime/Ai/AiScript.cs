using System.Collections.Generic;

namespace Dnf.Game.Ai
{
    /// <summary>一个分支(或整个脚本)的结果种类。</summary>
    public enum AiResultKind
    {
        /// <summary>脚本里没写:返回默认值 -1。</summary>
        Default = 0,

        /// <summary>数值返回:[return] 数字。</summary>
        Number = 1,

        /// <summary>命名返回:[return str] "名字",或 [return] "名字"。名字由各角色(事件、选目标、动作、移动)按自己的名表解释。</summary>
        Name = 2,

        /// <summary>嵌套的 [think] 块。</summary>
        Think = 3,

        /// <summary>[import ai] "文件":换成同一难度下另一个 .ai 去求值。</summary>
        Import = 4,
    }

    /// <summary>"没写"的分支求值得 -1(引擎在结点创建时把两路返回都初始化成 -1)。</summary>
    public sealed class AiResult
    {
        public static readonly AiResult Default = new AiResult(AiResultKind.Default, -1f, null, null);

        public AiResult(AiResultKind kind, float number, string name, AiNode node)
        {
            Kind = kind;
            Number = number;
            Name = name;
            Node = node;
        }

        public AiResultKind Kind { get; }

        /// <summary>数值返回的值(脚本里整数、浮点都按浮点存;使用方取整)。</summary>
        public float Number { get; }

        /// <summary>命名返回的名字,或 [import ai] 的文件名。</summary>
        public string Name { get; }

        /// <summary>嵌套 [think] 的结点。</summary>
        public AiNode Node { get; }

        public static AiResult OfNumber(float value) => new AiResult(AiResultKind.Number, value, null, null);

        public static AiResult OfName(string name) => new AiResult(AiResultKind.Name, 0f, name, null);

        public static AiResult OfThink(AiNode node) => new AiResult(AiResultKind.Think, 0f, null, node);

        public static AiResult OfImport(string file) => new AiResult(AiResultKind.Import, 0f, file, null);
    }

    /// <summary>条件的一个参数:数字,或在 AICharacter/OperatingValue.lst 里定义过的名字(解析时已查成数字;没有名表时 Value 为 NaN)。</summary>
    public readonly struct AiParam
    {
        public AiParam(float value, string name)
        {
            Value = value;
            Name = name;
        }

        public float Value { get; }

        /// <summary>脚本里写的名字;写的是数字时为 null。</summary>
        public string Name { get; }

        public bool IsName => Name != null;
    }

    /// <summary>一个 [think] 结点:一个条件和真、假两路结果(两路里可以只写一路)。</summary>
    public sealed class AiNode
    {
        public AiNode(int kindId, int code, string keyword, string operatorText, bool statChangePrefix, AiParam[] parameters)
        {
            KindId = kindId;
            Code = code;
            Keyword = keyword;
            Operator = operatorText;
            StatChangePrefix = statChangePrefix;
            Params = parameters;
            False = AiResult.Default;
            True = AiResult.Default;
        }

        /// <summary>条件类型号(哪个关键字),见 <see cref="AiVocabulary"/>。</summary>
        public int KindId { get; }

        /// <summary>内部码:比较运算符(0–13)、[void] 函数(0x0e–0x1b)或关键字专属的求值码。</summary>
        public int Code { get; internal set; }

        /// <summary>条件关键字,如 "[hp]"、"[void]"。</summary>
        public string Keyword { get; }

        /// <summary>读到的运算符或函数名;这个关键字不写运算符时为 null。</summary>
        public string Operator { get; }

        /// <summary>条件关键字前面带了 [stat change] 前缀。</summary>
        public bool StatChangePrefix { get; }

        public AiParam[] Params { get; }

        /// <summary>条件为假时的结果。</summary>
        public AiResult False { get; internal set; }

        /// <summary>条件为真时的结果。</summary>
        public AiResult True { get; internal set; }
    }

    /// <summary>解析完的一个 .ai 文件。</summary>
    public sealed class AiScript
    {
        public AiScript(AiResult root, IReadOnlyList<string> errors, int nodeCount, int maxDepth, int consumedTokens, int totalTokens, IReadOnlyList<string> trailing)
        {
            Root = root;
            Errors = errors;
            NodeCount = nodeCount;
            MaxDepth = maxDepth;
            ConsumedTokens = consumedTokens;
            TotalTokens = totalTokens;
            Trailing = trailing;
        }

        /// <summary>整个脚本的结果:嵌套 [think]、直接返回、命名返回或导入。</summary>
        public AiResult Root { get; }

        /// <summary>解析中遇到的错误(未知关键字、未知运算符、参数不是数字或已知名字等)。原版遇到这些会打日志并让脚本读到一半。</summary>
        public IReadOnlyList<string> Errors { get; }

        public bool Clean => Errors.Count == 0;

        public int NodeCount { get; }

        /// <summary>[think] 的最大嵌套深度(最外层为 1)。</summary>
        public int MaxDepth { get; }

        public int ConsumedTokens { get; }

        public int TotalTokens { get; }

        /// <summary>解析结束后剩下的标记(原版不会再读它们,常见的是 [/ai pattern] 之类的闭合标签),最多记录前几个的文本。</summary>
        public IReadOnlyList<string> Trailing { get; }
    }
}
