using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// 证据:E-129(技能特效动画的预载表)。
namespace Dnf.Game
{
    /// <summary>
    /// 技能特效表:每个职业有一个脚本函数 <c>sq_LoadSkillEffect_&lt;职业&gt;(obj, skillIndex)</c>(在 PVF 的 <c>sqr/common.nut</c> 里),
    /// 客户端创建角色时对每个技能编号调一次;函数按技能编号分支,每个分支依次调 <c>sq_LoadSkillEffectAni(skillIndex, "路径")</c>,
    /// 先后顺序就是技能代码按"下标"取特效动画的顺序(例如第 0 个、第 1 个)。
    /// 本类在运行时读用户自备的这份脚本,把一个职业的函数解析成 技能编号 → 路径列表,代码里不写任何路径。
    /// 只认这几种写法:分支头 <c>skillIndex == 常量或数字</c>(一个条件里可以有几个,用 <c>||</c> 连),分支体用花括号,体里的预载调用;
    /// 常量取自同一份脚本里的 <c>名字 &lt;- 数字;</c> 行;行尾的 <c>//</c> 注释忽略。别的写法(嵌套条件里的临时变量等)按"属于最近的带编号的外层分支"处理。
    /// </summary>
    public sealed class SkillEffectTable
    {
        /// <summary>脚本在 PVF 里的路径。</summary>
        public const string ScriptPath = "sqr/common.nut";

        /// <summary>剑士(职业 0)的函数名后缀。</summary>
        public const string SwordmanJob = "Swordman";

        private static readonly Regex ConstantLine = new Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*<-\s*(-?\d+)\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex LineComment = new Regex(@"//[^\r\n]*", RegexOptions.CultureInvariant);

        private static readonly Regex Token = new Regex(
            @"(?<open>\{)|(?<close>\})|skillIndex\s*==\s*(?<id>[A-Za-z_][A-Za-z0-9_]*|-?\d+)|sq_LoadSkillEffectAni\s*\(\s*skillIndex\s*,\s*""(?<path>[^""]*)""\s*\)",
            RegexOptions.CultureInvariant);

        // 计数循环头:for(local i = 起 ; i <= 止 ; ++i)、for(i = 起 ; i < 止 ; ++i)、for(; i <= 止 ; ++i)(起值取前面的 local i = 数;),起止都是整数字面量
        private static readonly Regex ForHeader = new Regex(
            @"\bfor\s*\(\s*(?:local\s+)?(?<v>[A-Za-z_][A-Za-z0-9_]*)?\s*(?:=\s*(?<a>-?\d+))?\s*;\s*(?<v2>[A-Za-z_][A-Za-z0-9_]*)\s*(?<op><=|<)\s*(?<b>-?\d+)\s*;\s*(?:\+\+\s*(?<v3>[A-Za-z_][A-Za-z0-9_]*)|(?<v4>[A-Za-z_][A-Za-z0-9_]*)\s*\+\+)\s*\)",
            RegexOptions.CultureInvariant);

        private static readonly Regex LocalInit = new Regex(@"\blocal\s+(?<v>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<a>-?\d+)\s*;", RegexOptions.CultureInvariant);

        /// <summary>一个循环最多展开的次数(防脚本写出巨大的上限把内存吃光;真实脚本里最大是几次)。</summary>
        private const int MaxLoopCount = 64;

        private static readonly string[] None = new string[0];

        private readonly Dictionary<int, string[]> _byId;

        private SkillEffectTable(Dictionary<int, string[]> byId)
        {
            _byId = byId;
        }

        /// <summary>空表(没有脚本或没有这个职业的函数)。</summary>
        public static SkillEffectTable Empty { get; } = new SkillEffectTable(new Dictionary<int, string[]>());

        /// <summary>有预载特效的技能个数。</summary>
        public int SkillCount => _byId.Count;

        /// <summary>这个技能预载的特效动画路径(脚本里写的原样,相对角色目录),按预载顺序;没有返回空数组。</summary>
        public IReadOnlyList<string> EffectsOf(int skillId)
        {
            return _byId.TryGetValue(skillId, out string[] list) ? list : None;
        }

        /// <summary>读 <see cref="ScriptPath"/>,解析职业 <paramref name="job"/>(如 <see cref="SwordmanJob"/>)的函数;读不到脚本或函数时返回 <see cref="Empty"/>。</summary>
        public static SkillEffectTable Load(GameData data, string job)
        {
            if (data == null || string.IsNullOrEmpty(job) || !data.TryReadBytes(ScriptPath, out byte[] bytes))
            {
                return Empty;
            }
            // 脚本里的注释是本地代码页的文字,路径全是 ASCII;按单字节读,不会因注释的字节组合出错
            return Parse(Encoding.GetEncoding("iso-8859-1").GetString(bytes), job);
        }

        /// <summary>解析脚本文本 <paramref name="text"/> 里职业 <paramref name="job"/> 的 <c>sq_LoadSkillEffect_&lt;job&gt;</c> 函数。</summary>
        public static SkillEffectTable Parse(string text, string job)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(job))
            {
                return Empty;
            }
            text = LineComment.Replace(text, string.Empty);
            string body = FunctionBody(text, "sq_LoadSkillEffect_" + job);
            if (body == null)
            {
                return Empty;
            }
            body = ExpandLoops(body);
            var constants = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in ConstantLine.Matches(text))
            {
                if (int.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                {
                    constants[m.Groups[1].Value] = value;
                }
            }

            var lists = new Dictionary<int, List<string>>();
            var stack = new List<List<int>>();    // 每个已打开的花括号对应的技能编号(没有编号的块是 null)
            List<int> pending = null;             // 刚读到的分支头里的技能编号,等下一个 '{' 打开分支体
            foreach (Match m in Token.Matches(body))
            {
                if (m.Groups["open"].Success)
                {
                    stack.Add(pending);
                    pending = null;
                }
                else if (m.Groups["close"].Success)
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                else if (m.Groups["id"].Success)
                {
                    pending = pending ?? new List<int>();
                    string name = m.Groups["id"].Value;
                    if (int.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
                    {
                        pending.Add(number);
                    }
                    else if (constants.TryGetValue(name, out int resolved))
                    {
                        pending.Add(resolved);
                    }
                }
                else
                {
                    AddLoad(lists, stack, m.Groups["path"].Value);
                }
            }

            var result = new Dictionary<int, string[]>();
            foreach (KeyValuePair<int, List<string>> pair in lists)
            {
                result[pair.Key] = pair.Value.ToArray();
            }
            return new SkillEffectTable(result);
        }

        private static void AddLoad(Dictionary<int, List<string>> lists, List<List<int>> stack, string path)
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i] == null)
                {
                    continue;
                }
                foreach (int id in stack[i])
                {
                    if (!lists.TryGetValue(id, out List<string> list))
                    {
                        list = new List<string>();
                        lists[id] = list;
                    }
                    list.Add(path);
                }
                return;
            }
        }

        /// <summary>
        /// 把函数体里的计数循环(<see cref="ForHeader"/> 的写法)展开成逐次重复的文本:循环变量在 <c>" + 变量 + "</c> 的位置换成这一次的值,
        /// 这样 <c>sq_LoadSkillEffectAni(skillIndex, "前缀" + i + "后缀")</c> 在每一次都成了普通的路径字面量。
        /// 起值取不到、止值比起值小(一次也不跑)、次数超过 <see cref="MaxLoopCount"/>、循环体取不到的循环:一次也不跑的删掉,其余原样保留(里面的调用照旧读不到)。
        /// 循环体可以是花括号块、另一个循环或一条以分号结束的语句;外层循环先展开,里面的循环随后展开。
        /// </summary>
        internal static string ExpandLoops(string text)
        {
            Match h = ForHeader.Match(text);
            while (h.Success)
            {
                string v2 = h.Groups["v2"].Value;
                string v3 = h.Groups["v3"].Success ? h.Groups["v3"].Value : h.Groups["v4"].Value;
                bool sameVar = v2 == v3 && (!h.Groups["v"].Success || h.Groups["v"].Value == v2);
                int bodyStart = h.Index + h.Length;
                int bodyEnd = sameVar ? StatementEnd(text, bodyStart) : -1;
                if (bodyEnd < 0)
                {
                    h = ForHeader.Match(text, h.Index + 3);
                    continue;
                }
                int first = 0;
                bool haveFirst = false;
                if (h.Groups["a"].Success)
                {
                    haveFirst = int.TryParse(h.Groups["a"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out first);
                }
                else
                {
                    // 起值在循环外的 local 声明里:取循环前最后一次对这个变量的声明
                    foreach (Match m in LocalInit.Matches(text.Substring(0, h.Index)))
                    {
                        if (m.Groups["v"].Value == v2 && int.TryParse(m.Groups["a"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int declared))
                        {
                            first = declared;
                            haveFirst = true;
                        }
                    }
                }
                bool haveLast = int.TryParse(h.Groups["b"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int last);
                if (!haveFirst || !haveLast)
                {
                    h = ForHeader.Match(text, h.Index + 3);
                    continue;
                }
                if (h.Groups["op"].Value == "<")
                {
                    last--;
                }
                long count = (long)last - first + 1;
                if (count > MaxLoopCount)
                {
                    h = ForHeader.Match(text, h.Index + 3);
                    continue;
                }
                string body = text.Substring(bodyStart, bodyEnd - bodyStart);
                var expanded = new StringBuilder();
                var substitute = new Regex(@"""\s*\+\s*" + Regex.Escape(v2) + @"\s*\+\s*""", RegexOptions.CultureInvariant);
                for (int k = first; k <= last; k++)
                {
                    expanded.Append(substitute.Replace(body, k.ToString(CultureInfo.InvariantCulture))).Append('\n');
                }
                text = text.Substring(0, h.Index) + expanded + text.Substring(bodyEnd);
                // 展开出来的文本里可能还有里面的循环:从展开处重新找(此时外层循环已经没了,不会死循环)
                h = ForHeader.Match(text, h.Index);
            }
            return text;
        }

        /// <summary>从 <paramref name="start"/> 起一条语句的结束位置(不含);花括号块配对到右括号、循环连同循环体、其余到第一个分号;取不到返回 -1。</summary>
        private static int StatementEnd(string text, int start)
        {
            int i = start;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            if (i >= text.Length)
            {
                return -1;
            }
            if (text[i] == '{')
            {
                int depth = 0;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '{')
                    {
                        depth++;
                    }
                    else if (text[i] == '}' && --depth == 0)
                    {
                        return i + 1;
                    }
                }
                return -1;
            }
            Match inner = ForHeader.Match(text, i);
            if (inner.Success && inner.Index == i)
            {
                return StatementEnd(text, inner.Index + inner.Length);
            }
            int semicolon = text.IndexOf(';', i);
            return semicolon < 0 ? -1 : semicolon + 1;
        }

        /// <summary>函数 <paramref name="name"/> 的花括号体(不含外层括号);找不到或括号不配对返回 null。</summary>
        private static string FunctionBody(string text, string name)
        {
            var header = new Regex(@"function\s+" + Regex.Escape(name) + @"\s*\(", RegexOptions.CultureInvariant);
            Match h = header.Match(text);
            if (!h.Success)
            {
                return null;
            }
            int open = text.IndexOf('{', h.Index + h.Length);
            if (open < 0)
            {
                return null;
            }
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                }
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(open + 1, i - open - 1);
                    }
                }
            }
            return null;
        }
    }
}
