using System;
using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;

// 证据:E-121(文字引用 = 种类编号 + 键;任务的文字在 n_quest/quest.kor.str,种类 11);E-123(对白里夹着 NPC、怪物、AI 角色的引用标记,见 Expand)。
namespace Dnf.Play
{
    /// <summary>
    /// 任务的文字(M31):任务名、接取对话、进行中对话、完成对话,按 <c>.qst</c> 里的文字引用去字符串表取。
    /// 文字是用户自备数据里的原版内容,只在运行时读,不入库;取不到(没装这份文本、键不在表里、值是空的)返回 null,调用方退回占位画法。
    /// 对白里夹着引用标记 <c>&lt;npc::编号&gt;</c>、<c>&lt;monster::编号&gt;</c>、<c>&lt;apc::编号&gt;</c>(本机整库扫描里只出现这三种),
    /// <see cref="Expand"/> 把它们换成对应的名字;查不到名字的标记整个去掉。表里的百分号写成 <c>%%</c>,显示时还原成一个 %(同物品文字)。
    /// </summary>
    public sealed class QuestTexts
    {
        private readonly StrLibrary _library;
        private readonly Func<int, string> _npcName;
        private readonly Func<int, string> _monsterName;
        private readonly Func<int, string> _apcName;

        /// <param name="library">字符串表集合。</param>
        /// <param name="npcName">按 NPC 编号给名字(没有返回 null)。</param>
        /// <param name="monsterName">按怪物编号给名字。</param>
        /// <param name="apcName">按 AI 角色编号给名字。</param>
        public QuestTexts(StrLibrary library, Func<int, string> npcName = null, Func<int, string> monsterName = null, Func<int, string> apcName = null)
        {
            _library = library ?? throw new ArgumentNullException(nameof(library));
            _npcName = npcName;
            _monsterName = monsterName;
            _apcName = apcName;
        }

        /// <summary>任务名(没有 <c>[name]</c> 或取不到返回 null)。</summary>
        public string NameOf(QuestDef quest)
        {
            return quest == null ? null : Resolve(quest.Name);
        }

        /// <summary>接取任务时的对白(<c>[depend message]</c>)。</summary>
        public string DependOf(QuestDef quest)
        {
            return quest == null ? null : Resolve(quest.DependMessage);
        }

        /// <summary>任务进行中、条件还没满足时的对白(<c>[condition message]</c>)。</summary>
        public string ConditionOf(QuestDef quest)
        {
            return quest == null ? null : Resolve(quest.ConditionMessage);
        }

        /// <summary>交任务时的对白(<c>[solve message]</c>)。</summary>
        public string SolveOf(QuestDef quest)
        {
            return quest == null ? null : Resolve(quest.SolveMessage);
        }

        private string Resolve(QuestText text)
        {
            if (text.IsEmpty)
            {
                return null;
            }
            string raw = ItemTexts.Display(_library.Get(text.Kind, text.Key));
            return raw == null ? null : Expand(raw);
        }

        /// <summary>把对白里的引用标记换成名字;不是标记的 <c>&lt;</c> 原样留着。</summary>
        public string Expand(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.IndexOf('<') < 0)
            {
                return raw;
            }
            var sb = new StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (c == '<')
                {
                    int end = raw.IndexOf('>', i + 1);
                    if (end > i && end - i <= MaxTagLength && TryTag(raw, i + 1, end, out string name))
                    {
                        sb.Append(name);
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>引用标记最长多少字符(含两个尖括号);更长的不当标记。</summary>
        public const int MaxTagLength = 24;

        private bool TryTag(string raw, int start, int end, out string name)
        {
            name = null;
            int separator = raw.IndexOf("::", start, end - start, StringComparison.Ordinal);
            if (separator < 0)
            {
                return false;
            }
            string kind = raw.Substring(start, separator - start);
            if (!int.TryParse(raw.Substring(separator + 2, end - separator - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                return false;
            }
            Func<int, string> lookup;
            switch (kind)
            {
                case "npc":
                    lookup = _npcName;
                    break;
                case "monster":
                    lookup = _monsterName;
                    break;
                case "apc":
                    lookup = _apcName;
                    break;
                default:
                    return false;
            }
            name = lookup?.Invoke(id) ?? string.Empty;
            return true;
        }
    }
}
