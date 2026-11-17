using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 选角界面上的职业称号(第二行字):按服务端列表里的职业号、一转、觉醒阶段给出名字。查不到的返回 null,调用方换成英文占位
    /// (<see cref="OnlineCharacterRoster.PlaceholderClassName"/>)。
    /// </summary>
    public interface ICharacterTitles
    {
        /// <summary>名字字节为空(或过长)时界面上显示的名字。</summary>
        string DefaultName { get; }

        /// <summary>
        /// 职业称号。<paramref name="job"/> 是列表条目里的职业号;<paramref name="growType"/> 是一转(0 = 没转职的基础职业);
        /// <paramref name="awakening"/> 是觉醒阶段(0 = 没觉醒)。脚本里没有这个组合的名字时返回 null。
        /// </summary>
        string ClassName(int job, int growType, int awakening);
    }

    /// <summary>
    /// 没有游戏数据时的称号来源:职业称号一律查不到(界面显示英文占位),默认名是英文占位。离线测试、命令行出图与没有数据目录的宿主用它。
    /// </summary>
    public sealed class PlaceholderTitles : ICharacterTitles
    {
        public const string DefaultNameText = "NONAME";

        public static readonly PlaceholderTitles Instance = new PlaceholderTitles();

        private PlaceholderTitles()
        {
        }

        public string DefaultName => DefaultNameText;

        public string ClassName(int job, int growType, int awakening) => null;
    }

    /// <summary>
    /// 从角色脚本取职业称号。来源与对应关系(都在 PVF 里,证据 E-121 的文字表 + 本线在真实数据上核对过的结构):
    /// <list type="number">
    /// <item>职业号 → 角色脚本:<see cref="EquipRules.JobTag"/> 的 11 个职业名(装备脚本 <c>[usable job]</c> 用的同一套顺序,职业号与它同序是推断),
    /// 去掉括号后按空格拆词,<c>character/&lt;最后一个词&gt;/&lt;各词连写&gt;.chr</c>(例如 <c>[at gunner]</c> → <c>character/gunner/atgunner.chr</c>,
    /// <c>[demonic swordman]</c> → <c>character/swordman/demonicswordman.chr</c>)。</item>
    /// <item>一转名字:脚本头部的 <c>[growtype name]</c> 一行若干个文字引用(种类 0 = 角色文字表),<b>下标就是一转的值</b>(0 = 基础职业,1 起是各个转职)。</item>
    /// <item>觉醒名字:每个 <c>[growtype N]</c> 块里有一行 <c>[awakening name]</c>(两个文字引用,第一个是一觉、第二个是二觉);块号比一转大 1
    /// (<c>[growtype 2]</c> 属于一转 1,因为 <c>[growtype 1]</c> 是基础职业的成长表)。</item>
    /// </list>
    /// 一转没有觉醒名行、觉醒阶段超出名字个数、文字表里没有这个键或值是空的,都返回 null(界面用英文占位)。
    /// 脚本与文字表按需读取、读过的职业缓存下来。读取失败(脚本缺失、数据损坏)一律当"查不到"。
    /// </summary>
    public sealed class DnfCharacterTitles : ICharacterTitles
    {
        /// <summary>名字字节为空时显示的默认名(原版的默认名在客户端自带的字符串表里,没有读到;这里是占位)。</summary>
        public const string DefaultNameText = "无名";

        private readonly Func<string, ScriptLine[]> _readScript;
        private readonly StrLibrary _strings;
        private readonly Dictionary<int, JobTitles> _jobs = new Dictionary<int, JobTitles>();

        /// <param name="readScript">按 PVF 路径读脚本,没有返回 null(例如 <c>DnfAssets.ReadScript</c>)。</param>
        /// <param name="strings">角色文字表所在的文字库。</param>
        public DnfCharacterTitles(Func<string, ScriptLine[]> readScript, StrLibrary strings)
        {
            _readScript = readScript ?? throw new ArgumentNullException(nameof(readScript));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        }

        public string DefaultName => DefaultNameText;

        public string ClassName(int job, int growType, int awakening)
        {
            JobTitles titles = Load(job);
            return titles == null ? null : titles.NameOf(growType, awakening);
        }

        /// <summary>职业号对应的角色脚本路径;职业号越界返回 null。</summary>
        public static string ChrPathOf(int job)
        {
            string tag = EquipRules.JobTag(job);
            if (tag == null)
            {
                return null;
            }
            string[] words = tag.Trim('[', ']').Split(' ');
            return "character/" + words[words.Length - 1] + "/" + string.Concat(words) + ".chr";
        }

        private JobTitles Load(int job)
        {
            if (_jobs.TryGetValue(job, out JobTitles cached))
            {
                return cached;
            }
            JobTitles loaded = null;
            string path = ChrPathOf(job);
            if (path != null)
            {
                ScriptLine[] script = null;
                try
                {
                    script = _readScript(path);
                }
                catch (Exception e) when (e is DnfFormatException || e is InvalidOperationException || e is System.IO.IOException)
                {
                    script = null;
                }
                loaded = script == null ? null : JobTitles.Parse(script, _strings);
            }
            _jobs[job] = loaded;
            return loaded;
        }

        private sealed class JobTitles
        {
            private string[] _growth = Array.Empty<string>();
            private readonly Dictionary<int, string[]> _awakening = new Dictionary<int, string[]>();

            public static JobTitles Parse(ScriptLine[] script, StrLibrary strings)
            {
                var titles = new JobTitles();
                int block = -1;
                foreach (ScriptLine line in script)
                {
                    if (line == null || line.Tag == null)
                    {
                        continue;
                    }
                    if (TryBlockNumber(line.Tag, out int number))
                    {
                        block = number;
                    }
                    else if (line.TagIs("[growtype name]") && titles._growth.Length == 0)
                    {
                        titles._growth = Resolve(line, strings);
                    }
                    else if (line.TagIs("[awakening name]") && block >= 2 && !titles._awakening.ContainsKey(block - 1))
                    {
                        titles._awakening[block - 1] = Resolve(line, strings);
                    }
                }
                return titles;
            }

            public string NameOf(int growType, int awakening)
            {
                if (growType < 0 || awakening < 0)
                {
                    return null;
                }
                if (awakening > 0 && _awakening.TryGetValue(growType, out string[] names) && awakening <= names.Length)
                {
                    return names[awakening - 1];
                }
                if (awakening > 0 && growType > 0)
                {
                    return null;                    // 觉醒了但脚本里没有这一档的名字:不拿转职名冒充
                }
                return growType < _growth.Length ? _growth[growType] : null;
            }

            // 一行里的文字引用换成文字;查不到或空串的位置是 null(保持下标不错位)
            private static string[] Resolve(ScriptLine line, StrLibrary strings)
            {
                var names = new List<string>();
                foreach (ScriptValue value in line.Values)
                {
                    if (value.Kind != ScriptValueKind.Keyed)
                    {
                        continue;
                    }
                    string text = strings.Get(value.Int, value.Text);
                    names.Add(string.IsNullOrWhiteSpace(text) || text.StartsWith("//", StringComparison.Ordinal) ? null : text.Trim());
                }
                return names.ToArray();
            }

            // "[growtype 3]" → 3;"[growtype name]" 与别的标签不是块号
            private static bool TryBlockNumber(string tag, out int number)
            {
                const string prefix = "[growtype ";
                number = 0;
                if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !tag.EndsWith("]", StringComparison.Ordinal))
                {
                    return false;
                }
                string digits = tag.Substring(prefix.Length, tag.Length - prefix.Length - 1);
                return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
            }
        }
    }
}
