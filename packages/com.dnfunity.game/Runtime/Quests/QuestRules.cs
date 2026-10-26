using System;
using System.Collections.Generic;

// 证据:E-123(服务端静态读码,非 REA)。可接条件:Quest::check_possible 0x08352d86(职业、转职、等级区间)、QuestList::check_ahead_quest 0x08354fc8
// (前置:各组之间"或"、组内"与")、check_anti_quest 0x0835511e(互斥:一个也不能完成)、Quest::isRepeatableQuest 0x08352bee;
// 接受 UserQuest::accept_quest 0x086abf38;奖励 CUser::compute_quest_basic_reward_exp/gold 0x0866e3e2 / 0x0866e5e6。
// 服务端不管的部分(猎杀计数、过房间、拜访、收集)在原版里由客户端上报,这里的事件挂接是自己的实现(推断,见账本 E-123 的未知项)。
namespace Dnf.Game
{
    /// <summary>主角在任务规则眼里的样子。</summary>
    public readonly struct QuestHero
    {
        public QuestHero(int level, int job, int growType)
        {
            Level = level;
            Job = job;
            GrowType = growType;
        }

        public int Level { get; }

        /// <summary>职业下标(<see cref="EquipRules.JobOf"/>)。</summary>
        public int Job { get; }

        /// <summary>转职方向(没转职为 0)。</summary>
        public int GrowType { get; }
    }

    /// <summary>能不能接这个任务,不能的话是哪一条挡住的(按检查的先后)。</summary>
    public enum QuestOffer
    {
        Ok = 0,

        /// <summary>这一步没做的任务(分类、类型、奖励种类或数据形状不支持)。</summary>
        Unsupported = 1,

        /// <summary>角色的职业或转职方向不在目标里。</summary>
        Job = 2,

        /// <summary>等级不在区间内。</summary>
        Level = 3,

        /// <summary>已经在进行中。</summary>
        Active = 4,

        /// <summary>已完成过(可重复类除外)。</summary>
        Cleared = 5,

        /// <summary>前置任务没完成。</summary>
        PreRequired = 6,

        /// <summary>互斥的任务已经完成。</summary>
        Collision = 7,

        /// <summary>进行中的任务已满。</summary>
        NoSlot = 8,
    }

    /// <summary>交任务得到的奖励。</summary>
    public sealed class QuestReward
    {
        public int Exp { get; set; }

        public int Gold { get; set; }

        /// <summary>固定奖励物品(不含金币标记)与选中的那一项。</summary>
        public List<QuestItem> Items { get; } = new List<QuestItem>();

        /// <summary>任务点与任务碎片(只记录,这一步不发)。</summary>
        public int QuestPoint { get; set; }

        public int QuestPiece { get; set; }
    }

    /// <summary>任务的规则:哪些任务这一步支持、能不能接、完成条件、奖励。全是纯函数,状态在 <see cref="QuestLog"/>。</summary>
    public static class QuestRules
    {
        /// <summary><see cref="QuestKind.ConditionUnderClear"/> 的"通关"子类型(整数数据:副本、难度[、次数])。</summary>
        public const int SubClearDungeon = 6;

        /// <summary><see cref="QuestKind.ConditionUnderClear"/> 的"限时通关"子类型(整数数据:副本、难度、秒数)。服务端比较 秒数 × 1000 与上次通关用时(毫秒)。</summary>
        public const int SubClearInTime = 0;

        /// <summary>敌人种类:普通怪。</summary>
        public const int EnemyNormal = 1;

        /// <summary>敌人种类:怪物等级 5 到 8 的(服务端 <c>CheckQuestMonster</c> 的调用点按等级分种类)。</summary>
        public const int EnemyNamed = 2;

        /// <summary>怪物等级到敌人种类:5 到 8 是 2,其余是 1(3 与 10 在数据里出现过,含义没确认,这里不产生)。</summary>
        public static int EnemyTypeOfRank(int rank)
        {
            return rank >= 5 && rank <= 8 ? EnemyNamed : EnemyNormal;
        }

        /// <summary>NPC 会列出来的分类:史诗、普通唯一、可重复(服务端 <c>select_quest</c> 还有训练、成就、每日、特别,这一步没做它们各自的发放机制)。</summary>
        public static bool IsGradeOffered(QuestGrade grade)
        {
            return grade == QuestGrade.Epic || grade == QuestGrade.CommonUnique || grade == QuestGrade.NormalRepeat;
        }

        /// <summary>
        /// 这一步不支持的原因(短词);支持返回 null。<paramref name="monsterExists"/> 不为 null 时,猎杀目标里编号 ≥ 0 的怪物必须存在
        /// (有的猎杀目标写的不是怪物列表里的编号,杀不出来的任务不列出来)。
        /// </summary>
        public static string UnsupportedReason(QuestDef q, Func<int, bool> monsterExists = null)
        {
            if (q == null)
            {
                return "missing";
            }
            if (!IsGradeOffered(q.Grade))
            {
                return "grade";
            }
            if (q.NpcId < 0)
            {
                return "no-npc";
            }
            if (!q.Exposed)
            {
                return "hidden";
            }
            if (q.JobChange == 1)
            {
                return "job-change";
            }
            if (q.RewardType != null && !string.Equals(q.RewardType, "[item]", StringComparison.OrdinalIgnoreCase))
            {
                return "reward-type";
            }
            int[] d = q.IntData;
            switch (q.Kind)
            {
                case QuestKind.Seeking:
                    if (d.Length < 2 || d.Length % 2 != 0)
                    {
                        return "shape";
                    }
                    for (int i = 0; i + 2 <= d.Length; i += 2)
                    {
                        if (d[i] <= 0 || d[i + 1] <= 0)
                        {
                            return "shape";
                        }
                    }
                    return null;
                case QuestKind.HuntMonster:
                    return d.Length >= 4 && d.Length % 4 == 0 ? KillGroupsReason(q, monsterExists) : "shape";
                case QuestKind.HuntEnemy:
                    return d.Length >= 5 && d.Length % 5 == 0 ? KillGroupsReason(q, monsterExists) : "shape";
                case QuestKind.ClearMap:
                    return d.Length >= 1 && d[0] >= 0 ? null : "shape";
                case QuestKind.MeetNpc:
                    return q.MeetNpcId() >= 0 ? null : "shape";
                case QuestKind.ConditionUnderClear:
                    if (q.SubType == SubClearDungeon)
                    {
                        return d.Length == 2 || d.Length == 3 ? null : "shape";
                    }
                    if (q.SubType == SubClearInTime)
                    {
                        return d.Length == 3 && d[2] > 0 ? null : "shape";
                    }
                    return "sub-type";
                default:
                    return "kind";
            }
        }

        /// <summary>这一步是否支持这个任务(<paramref name="monsterExists"/> 见 <see cref="UnsupportedReason"/>)。</summary>
        public static bool IsSupported(QuestDef q, Func<int, bool> monsterExists = null)
        {
            return UnsupportedReason(q, monsterExists) == null;
        }

        private static string KillGroupsReason(QuestDef q, Func<int, bool> monsterExists)
        {
            foreach (QuestKillGroup g in q.KillGroups())
            {
                if (g.Count <= 0)
                {
                    return "shape";
                }
                if (g.Monster >= 0 && monsterExists != null && !monsterExists(g.Monster))
                {
                    return "monster";
                }
            }
            return null;
        }

        /// <summary>
        /// 角色能不能当这个任务的目标:目标表里要有一项职业相同(或 11 = 所有)的;
        /// 目标表里没有"该职业且转职方向不限"的项时,还要有一项转职方向也匹配(服务端 <c>check_possible</c> 传入的觉醒是 −1,所以只有觉醒不限的项算数)。
        /// <c>[job change quest]</c> 为 10 或 20 的不看转职方向。
        /// </summary>
        public static bool JobMatches(QuestDef q, int job, int growType)
        {
            bool jobOk = false;
            bool growFree = false;
            foreach (QuestTarget t in q.Targets)
            {
                if (t.Job == job || t.Job == EquipRules.AllJobs)
                {
                    jobOk = true;
                    if (t.GrowType == -1)
                    {
                        growFree = true;
                    }
                }
            }
            if (!jobOk)
            {
                return false;
            }
            if (growFree || q.JobChange == 10 || q.JobChange == 20)
            {
                return true;
            }
            foreach (QuestTarget t in q.Targets)
            {
                bool jobSame = t.Job == job || t.Job == EquipRules.AllJobs;
                if (jobSame && (t.GrowType == growType || t.GrowType == -1) && t.Awakening == -1)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>前置任务是否满足:没有前置视为满足;有的话至少一组全部完成。</summary>
        public static bool PreRequiredMet(QuestDef q, QuestLog log)
        {
            if (q.PreRequired.Count == 0)
            {
                return true;
            }
            foreach (int[] group in q.PreRequired)
            {
                bool all = true;
                foreach (int id in group)
                {
                    if (!log.IsCleared(id))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>能不能接:依次检查支持、职业、等级、已在进行、已完成、前置、互斥、位置。</summary>
        public static QuestOffer CheckOffer(QuestDef q, QuestHero hero, QuestLog log)
        {
            if (!IsSupported(q))
            {
                return QuestOffer.Unsupported;
            }
            if (!JobMatches(q, hero.Job, hero.GrowType))
            {
                return QuestOffer.Job;
            }
            if (hero.Level < q.MinLevel || hero.Level > q.MaxLevel)
            {
                return QuestOffer.Level;
            }
            if (log.IsActive(q.Id))
            {
                return QuestOffer.Active;
            }
            if (!QuestNames.IsRepeatable(q.Grade) && log.IsCleared(q.Id))
            {
                return QuestOffer.Cleared;
            }
            if (!PreRequiredMet(q, log))
            {
                return QuestOffer.PreRequired;
            }
            foreach (int id in q.Collision)
            {
                if (log.IsCleared(id))
                {
                    return QuestOffer.Collision;
                }
            }
            return log.HasFreeSlot ? QuestOffer.Ok : QuestOffer.NoSlot;
        }

        /// <summary>刚接下任务时的计数:猎杀类每组一项(组的数量),通关类是次数,过房间与拜访各一次;收集类为空表。</summary>
        public static int[] InitialRemaining(QuestDef q)
        {
            switch (q.Kind)
            {
                case QuestKind.HuntMonster:
                case QuestKind.HuntEnemy:
                {
                    List<QuestKillGroup> groups = q.KillGroups();
                    var r = new int[groups.Count];
                    for (int i = 0; i < r.Length; i++)
                    {
                        r[i] = Math.Max(0, groups[i].Count);
                    }
                    return r;
                }
                case QuestKind.ConditionUnderClear:
                    // 服务端 get_init_trigger:通关子类型带第三个值(次数)时按它,没有就是 1 次。
                    return new[] { q.SubType == SubClearDungeon && q.IntData.Length == 3 && q.IntData[2] > 0 ? q.IntData[2] : 1 };
                case QuestKind.ClearMap:
                case QuestKind.MeetNpc:
                    return new[] { 1 };
                default:
                    return Array.Empty<int>();
            }
        }

        /// <summary>接任务:通过检查就加进进行中并返回;否则返回 null,原因在 <paramref name="why"/>。物品(<see cref="QuestDef.AcceptItems"/>)由调用方发。</summary>
        public static ActiveQuest Accept(QuestDef q, QuestHero hero, QuestLog log, out QuestOffer why)
        {
            why = CheckOffer(q, hero, log);
            return why == QuestOffer.Ok ? log.Add(q.Id, InitialRemaining(q)) : null;
        }

        /// <summary>
        /// 条件是否满足(可以交了):计数全部归零;收集类还要背包里的数量够(<paramref name="itemCount"/> 按物品编号给出背包里的数量)。
        /// </summary>
        public static bool IsReady(QuestDef q, ActiveQuest a, Func<int, int> itemCount)
        {
            if (q == null || a == null || !a.CountersDone)
            {
                return false;
            }
            if (q.Kind == QuestKind.Seeking)
            {
                if (itemCount == null)
                {
                    return false;
                }
                foreach (QuestItem need in q.SeekingItems())
                {
                    if (itemCount(need.Id) < need.Count)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 交任务的 NPC:拜访类是被拜访的 NPC,其余用 <c>[complete npc index]</c>;−1 表示不用回 NPC 那里交(条件满足就自动完成)。
        /// </summary>
        public static int HandInNpc(QuestDef q)
        {
            if (q.Kind == QuestKind.MeetNpc && q.MeetNpcId() >= 0)
            {
                return q.MeetNpcId();
            }
            return q.CompleteNpcId;
        }

        /// <summary>完成:从进行中拿掉,非可重复类记入已完成。任务不在进行中返回 false。</summary>
        public static bool Complete(QuestDef q, QuestLog log)
        {
            if (!log.Remove(q.Id))
            {
                return false;
            }
            if (!QuestNames.IsRepeatable(q.Grade))
            {
                log.MarkCleared(q.Id);
            }
            return true;
        }

        /// <summary>放弃:<c>[cant giveup]</c> 非 0 的不行;不在进行中也不行。</summary>
        public static bool Abandon(QuestDef q, QuestLog log)
        {
            return q.CantGiveUp == 0 && log.Remove(q.Id);
        }

        /// <summary>多选一奖励里这个角色看得到的那几项(职业与转职方向对得上的,保持原有次序):选择奖励的下标是这张表里的下标(服务端 <c>ConvertRewardSelectIndex</c> 做的就是这个换算)。</summary>
        public static List<QuestItem> SelectableRewards(QuestDef q, QuestHero hero)
        {
            var list = new List<QuestItem>();
            foreach (QuestItem item in q.SelectRewards)
            {
                if (item.AppliesTo(hero.Job, hero.GrowType))
                {
                    list.Add(item);
                }
            }
            return list;
        }

        /// <summary>
        /// 奖励:经验 = 经验表[等级] × 难度权重 / 100 × 等级差惩罚 / 100(逐步整除),可重复类不给经验;
        /// 金币只在固定奖励里有编号 0 的项时给,= 金币表[等级] × 惩罚 / 100 × 金币倍率 / 100;
        /// 取表的等级默认是任务最低等级,<see cref="QuestDef.ExpUsesHeroLevel"/>(经验)与 <see cref="QuestDef.IsEvent"/>(经验与金币,并且不扣惩罚)改用角色等级。
        /// 固定奖励物品只给职业与转职方向对得上的项(<see cref="QuestItem.AppliesTo"/>)。
        /// <paramref name="selection"/> 是 <see cref="SelectableRewards"/> 里选的下标(−1 或越界 = 不选)。
        /// </summary>
        public static QuestReward ComputeReward(QuestDef q, QuestHero hero, QuestParameters p, int selection)
        {
            int heroLevel = hero.Level;
            var reward = new QuestReward { QuestPoint = q.QuestPoint, QuestPiece = q.QuestPiece };
            int difference = heroLevel - q.MinLevel;
            int penalty = q.IsEvent ? 100 : p.LevelPenalty(difference);

            if (q.Grade != QuestGrade.NormalRepeat)
            {
                int baseExp = !q.ExpUsesHeroLevel && !q.IsEvent ? p.ExpAt(q.MinLevel) : p.ExpAt(heroLevel);
                long exp = (long)baseExp * p.Weight(q.DifficultyLetter) / 100;
                reward.Exp = (int)(exp * penalty / 100);
            }

            bool itemRewards = q.RewardType == null || string.Equals(q.RewardType, "[item]", StringComparison.OrdinalIgnoreCase);
            if (itemRewards)
            {
                bool gold = false;
                foreach (QuestItem item in q.Rewards)
                {
                    if (item.Id == 0)
                    {
                        gold = true;
                    }
                    else if (item.Id > 0 && item.Count > 0 && item.AppliesTo(hero.Job, hero.GrowType))
                    {
                        reward.Items.Add(item);
                    }
                }
                if (gold)
                {
                    int baseGold = q.IsEvent ? p.GoldAt(heroLevel) : p.GoldAt(q.MinLevel);
                    long amount = (long)baseGold * penalty / 100;
                    reward.Gold = (int)(amount * q.GoldPercent / 100);
                }
            }

            List<QuestItem> choices = SelectableRewards(q, hero);
            if (selection >= 0 && selection < choices.Count)
            {
                QuestItem chosen = choices[selection];
                if (chosen.Id > 0 && chosen.Count > 0)
                {
                    reward.Items.Add(chosen);
                }
            }
            return reward;
        }
    }
}
