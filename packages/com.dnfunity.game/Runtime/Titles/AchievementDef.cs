using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:服务端静态读码(df_game_r,只读,没有运行)——
//  · "成就"在服务端就是任务分类 [title](ENUM_QUEST_GRADE 7)的任务,没有单独的成就文件:CAchievement::setTrigger 0x0828b678 只认 find_quest 找得到、分类为 7 的任务;
//  · 初始计数:Quest::get_init_achievement_trigger 0x08352fb4 给三个 16 位计数先赋 (1, 0, 0),再用 [check count] 的前三个整数依次覆盖(该向量在 Quest 对象的 +0x90,
//    Quest::set_quest 0x0835233e 从脚本的 +0x1b0 拷来;导入函数 importQuestScript 0x08a61813 在 0x08a642c8 把 [check count] 后面连续的整数读进 +0x1b0);
//  · 称号奖励:Quest::get_title_reward 0x08353082 = 分类 7 的任务奖励物品表(+0xc4)的第一项的物品编号,没有为 0;
//  · 任务自带的邮件奖励:_sendEachAchieveReward 0x0828c086(物品编号 +0xa4 不为 0 才发,数量 +0xa8,两段文字 +0x9c、+0xa0);
//  · 完成条件的判定在客户端:服务端不看条件种类,只数客户端发来的 setTrigger(Dispatcher_AchievementTrigger::process 0x081d7f1e)。
//    服务端自己会判的只有登录时的 _verifyAchievement 0x0828d092:类型为 [clear quest](12)且 [int data] 第一个整数是已完成任务的成就,补成完成。
// 本机数据形态(件数只记在笔记里,编号不入库):分类 [title] 的任务数与完成条件种类的分布见笔记;[check count] 后面至多一个整数(有的一个也没有)。
namespace Dnf.Game
{
    /// <summary>成就(任务)自带的邮件奖励(<c>[send postal]</c>:物品编号、数量、两段文字)。物品编号为 0 表示没有。</summary>
    public readonly struct AchievementPostal
    {
        public AchievementPostal(int itemCode, int count, TitleText title, TitleText body)
        {
            ItemCode = itemCode;
            Count = count;
            Title = title;
            Body = body;
        }

        public int ItemCode { get; }

        public int Count { get; }

        public TitleText Title { get; }

        public TitleText Body { get; }

        public bool IsEmpty => ItemCode == 0;
    }

    /// <summary>一个成就的定义(分类 [title] 的任务):初始计数、称号奖励、邮件奖励、完成条件种类。</summary>
    public sealed class AchievementDef
    {
        private readonly int[] _initial;

        public AchievementDef(QuestDef quest, IEnumerable<int> checkCounts, AchievementPostal postal)
        {
            Quest = quest ?? throw new ArgumentNullException(nameof(quest));
            var counts = new List<int>(checkCounts ?? Array.Empty<int>());
            CheckCounts = counts.AsReadOnly();
            _initial = new[] { 1, 0, 0 };
            for (int i = 0; i < counts.Count && i < 3; i++)
            {
                _initial[i] = unchecked((ushort)counts[i]);
            }
            Postal = postal;
        }

        /// <summary>对应的任务定义。</summary>
        public QuestDef Quest { get; }

        /// <summary>成就编号 = 任务编号。</summary>
        public int Id => Quest.Id;

        /// <summary>脚本路径。</summary>
        public string Path => Quest.Path;

        /// <summary>最低等级(<c>[level]</c> 第一个整数;角色等级低于它时服务端回复"无效")。</summary>
        public int MinLevel => Quest.MinLevel;

        /// <summary>完成条件的种类(<c>[type]</c>,数据里实际出现的见笔记;读不懂的种类只登记,条件由客户端发 setTrigger 来数)。</summary>
        public QuestKind Condition => Quest.Kind;

        /// <summary><c>[type]</c> 的原始关键字。</summary>
        public string ConditionTag => Quest.KindTag;

        /// <summary><c>[check count]</c> 的整数(原样);没写是空表。</summary>
        public IReadOnlyList<int> CheckCounts { get; }

        /// <summary>
        /// 初始计数(三个 16 位数):(1, 0, 0) 被 <see cref="CheckCounts"/> 的前三个整数依次覆盖;每次触发各自饱和地减去触发值,三个都减到 0 就算完成。
        /// 取 16 位是服务端计数的宽度(超过的高位丢掉)。
        /// </summary>
        public int InitialCount(int index)
        {
            return (uint)index < 3u ? _initial[index] : 0;
        }

        /// <summary>称号奖励的物品编号(<see cref="QuestDef.Rewards"/> 第一项的编号,奖励类型是物品时);没有为 0。</summary>
        public int TitleItemCode
        {
            get
            {
                if (Quest.Rewards.Count == 0 || !string.Equals(Quest.RewardType, "[item]", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
                return Quest.Rewards[0].Id;
            }
        }

        /// <summary>任务自带的邮件奖励。</summary>
        public AchievementPostal Postal { get; }

        /// <summary>
        /// 登录时服务端会替它核对的"被完成任务":类型为 <see cref="QuestKind.ClearQuest"/> 且 <c>[int data]</c> 有第一个整数时,就是这个整数;否则 0(没有)。
        /// </summary>
        public int ClearQuestId => Quest.Kind == QuestKind.ClearQuest && Quest.IntData.Length > 0 ? Quest.IntData[0] : 0;

        /// <summary>这个成就能在登录核对时从"某个任务已完成"补成完成。</summary>
        public bool IsVerifiable => ClearQuestId != 0;
    }

    /// <summary>读一个成就:任务定义之外再读 <c>[check count]</c> 与 <c>[send postal]</c>(<see cref="QuestScript"/> 不读这两个)。</summary>
    public static class AchievementScript
    {
        public static AchievementDef Parse(QuestDef quest, ScriptLine[] lines)
        {
            if (quest == null)
            {
                throw new ArgumentNullException(nameof(quest));
            }
            var counts = new List<int>();
            AchievementPostal postal = default;
            if (lines != null)
            {
                foreach (ScriptLine line in lines)
                {
                    if (line.TagIs("[check count]"))
                    {
                        // 导入函数先清空向量再读(同一标签写两次后一次覆盖前一次);读到第一个不是整数的值就停。
                        counts.Clear();
                        foreach (ScriptValue v in line.Values)
                        {
                            if (!v.IsNumber)
                            {
                                break;
                            }
                            counts.Add(v.Int);
                        }
                    }
                    else if (line.TagIs("[send postal]"))
                    {
                        postal = ReadPostal(line);
                    }
                }
            }
            return new AchievementDef(quest, counts, postal);
        }

        private static AchievementPostal ReadPostal(ScriptLine line)
        {
            List<int> ints = line.Ints();
            var texts = new List<TitleText>();
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Keyed)
                {
                    texts.Add(new TitleText(v.Int, v.Text));
                }
                else if (v.Kind == ScriptValueKind.String)
                {
                    texts.Add(new TitleText(-1, v.Text));
                }
            }
            return new AchievementPostal(
                ints.Count > 0 ? ints[0] : 0,
                ints.Count > 1 ? ints[1] : 0,
                texts.Count > 0 ? texts[0] : default,
                texts.Count > 1 ? texts[1] : default);
        }
    }

    /// <summary>成就目录:全部分类 [title] 的任务,按编号取。</summary>
    public sealed class AchievementCatalog
    {
        private readonly Dictionary<int, AchievementDef> _byId = new Dictionary<int, AchievementDef>();
        private readonly List<AchievementDef> _all = new List<AchievementDef>();

        public AchievementCatalog(IEnumerable<AchievementDef> defs)
        {
            if (defs == null)
            {
                throw new ArgumentNullException(nameof(defs));
            }
            foreach (AchievementDef def in defs)
            {
                if (def != null && def.Quest.Grade == QuestGrade.Title && !_byId.ContainsKey(def.Id))
                {
                    _byId.Add(def.Id, def);
                    _all.Add(def);
                }
            }
            _all.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        /// <summary>空目录。</summary>
        public static AchievementCatalog Empty => new AchievementCatalog(Array.Empty<AchievementDef>());

        /// <summary>读用户数据里的全部任务,留下分类 [title] 的(第一次要把整个任务库解一遍)。</summary>
        public static AchievementCatalog Load(GameData data, QuestCatalog quests)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (quests == null)
            {
                throw new ArgumentNullException(nameof(quests));
            }
            var defs = new List<AchievementDef>();
            foreach (QuestDef quest in quests.All())
            {
                if (quest.Grade == QuestGrade.Title)
                {
                    defs.Add(AchievementScript.Parse(quest, data.ReadScript(quest.Path)));
                }
            }
            return new AchievementCatalog(defs);
        }

        public int Count => _all.Count;

        /// <summary>全部成就,按编号升序。</summary>
        public IReadOnlyList<AchievementDef> All() => _all;

        /// <summary>按编号取;不是分类 [title] 的任务返回 null(服务端对它们回复"无效")。</summary>
        public AchievementDef Get(int id)
        {
            return _byId.TryGetValue(id, out AchievementDef def) ? def : null;
        }

        public bool Contains(int id) => _byId.ContainsKey(id);

        /// <summary>各完成条件种类各有几个成就(登记用:读不懂的种类只在这里露面)。</summary>
        public SortedDictionary<QuestKind, int> CountsByCondition()
        {
            var counts = new SortedDictionary<QuestKind, int>();
            foreach (AchievementDef def in _all)
            {
                counts.TryGetValue(def.Condition, out int n);
                counts[def.Condition] = n + 1;
            }
            return counts;
        }
    }
}
