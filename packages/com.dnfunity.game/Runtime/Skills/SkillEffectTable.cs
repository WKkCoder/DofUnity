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
