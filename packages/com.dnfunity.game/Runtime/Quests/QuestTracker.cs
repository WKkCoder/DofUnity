using System;
using System.Collections.Generic;

// 证据:E-123(服务端静态读码,非 REA)。猎杀匹配:Quest::CheckKillMonster 0x083535d6(副本与难度 −1 表示不限、怪物编号与敌人种类要相等),
// CUser::CheckQuestMonster 0x0866cb04 / CheckQuestMap 0x0866cac6 是服务端收客户端上报后的入口;任务物品掉落按 rand(100) < 百分率
// (CUser::_postQuestTriggerItem 0x086689ee 一带);限时通关比较 秒数 × 1000 与用时毫秒(check_quest_subtype_condition 0x086acb52)。
// 原版里"谁来上报这些事件"是客户端的事,这里的挂接点是自己定的(推断)。
namespace Dnf.Game
{
    /// <summary>
    /// 事件驱动的任务进度:击杀、通过房间、通关、拜访 NPC 分别推进进行中任务的计数,返回有变化的任务编号;
    /// 以及击杀、通关时任务物品的掷骰。<paramref name="find"/> 按编号取任务定义(通常是 <see cref="QuestCatalog.Get"/>)。
    /// </summary>
    public static class QuestTracker
    {
        /// <summary>
        /// 击杀一只怪:对每个进行中的猎杀类任务,第一个匹配且还没杀够的目标组减一。
        /// <paramref name="enemyType"/> 见 <see cref="QuestRules.EnemyTypeOfRank"/>;<c>[hunt monster]</c> 的目标组不看敌人种类。
        /// </summary>
        public static List<int> OnKill(QuestLog log, Func<int, QuestDef> find, int dungeon, int difficulty, int monster, int enemyType)
        {
            var changed = new List<int>();
            foreach (ActiveQuest a in log.Active)
            {
                QuestDef q = find(a.Id);
                if (q == null || (q.Kind != QuestKind.HuntMonster && q.Kind != QuestKind.HuntEnemy))
                {
                    continue;
                }
                List<QuestKillGroup> groups = q.KillGroups();
                for (int i = 0; i < groups.Count && i < a.Remaining.Length; i++)
                {
                    QuestKillGroup g = groups[i];
                    if (a.Remaining[i] > 0 && KillMatches(g, dungeon, difficulty, monster, enemyType))
                    {
                        a.Remaining[i]--;
                        changed.Add(a.Id);
                        break;
                    }
                }
            }
            return changed;
        }

        /// <summary>
        /// 一次击杀是否匹配这一组目标:副本与难度 −1 不限,怪物编号相同(目标里写负数的当作"任何怪物",这是推断:
        /// 本地数据里有几组猎杀目标写 −1,服务端没有这类比较),<see cref="QuestKillGroup.EnemyType"/> 为 0 时不看敌人种类。
        /// </summary>
        public static bool KillMatches(QuestKillGroup g, int dungeon, int difficulty, int monster, int enemyType)
        {
            return (g.Dungeon == -1 || g.Dungeon == dungeon)
                && (g.Difficulty == -1 || g.Difficulty == difficulty)
                && (g.Monster < 0 || g.Monster == monster)
                && (g.EnemyType == 0 || g.EnemyType == enemyType);
        }

        /// <summary>通过了一个房间(地图编号):对应的 <see cref="QuestKind.ClearMap"/> 任务达成。</summary>
        public static List<int> OnMapClear(QuestLog log, Func<int, QuestDef> find, int mapId)
        {
            var changed = new List<int>();
            foreach (ActiveQuest a in log.Active)
            {
                QuestDef q = find(a.Id);
                if (q != null && q.Kind == QuestKind.ClearMap && q.ClearMapId() == mapId && a.Remaining.Length > 0 && a.Remaining[0] > 0)
                {
                    a.Remaining[0] = 0;
                    changed.Add(a.Id);
                }
            }
            return changed;
        }

        /// <summary>
        /// 通关一个副本:<see cref="QuestKind.ConditionUnderClear"/> 的"通关"与"限时通关"子类型匹配(副本、难度 −1 = 不限)就减一次;
        /// 限时通关还要 <paramref name="clearMilliseconds"/> 不超过 秒数 × 1000。
        /// </summary>
        public static List<int> OnDungeonClear(QuestLog log, Func<int, QuestDef> find, int dungeon, int difficulty, int clearMilliseconds)
        {
            var changed = new List<int>();
            foreach (ActiveQuest a in log.Active)
            {
                QuestDef q = find(a.Id);
                if (q == null || q.Kind != QuestKind.ConditionUnderClear || a.Remaining.Length == 0 || a.Remaining[0] <= 0)
                {
                    continue;
                }
                int[] d = q.IntData;
                if (d.Length < 2 || (d[0] != -1 && d[0] != dungeon) || (d[1] != -1 && d[1] != difficulty))
                {
                    continue;
                }
                if (q.SubType == QuestRules.SubClearDungeon)
                {
                    a.Remaining[0]--;
                    changed.Add(a.Id);
                }
                else if (q.SubType == QuestRules.SubClearInTime && d.Length == 3 && (long)clearMilliseconds <= (long)d[2] * 1000)
                {
                    a.Remaining[0]--;
                    changed.Add(a.Id);
                }
            }
            return changed;
        }

        /// <summary>和某个 NPC 说了话:要拜访它的 <see cref="QuestKind.MeetNpc"/> 任务达成。</summary>
        public static List<int> OnTalk(QuestLog log, Func<int, QuestDef> find, int npcId)
        {
            var changed = new List<int>();
            foreach (ActiveQuest a in log.Active)
            {
                QuestDef q = find(a.Id);
                if (q != null && q.Kind == QuestKind.MeetNpc && q.MeetNpcId() == npcId && a.Remaining.Length > 0 && a.Remaining[0] > 0)
                {
                    a.Remaining[0] = 0;
                    changed.Add(a.Id);
                }
            }
            return changed;
        }

        /// <summary>
        /// 击杀时的任务物品:对每个进行中的任务里匹配这只怪的掉落项各掷一次(<paramref name="rand100"/> 给 0 到 99,小于百分率就掉)。
        /// 收集类任务里已经够数的物品不再掉(<paramref name="itemCount"/> 给背包里的数量)。
        /// </summary>
        public static List<QuestItem> RollKillDrops(QuestLog log, Func<int, QuestDef> find, int dungeon, int difficulty, int monster, int enemyType, Func<int> rand100, Func<int, int> itemCount)
        {
            var drops = new List<QuestItem>();
            foreach (ActiveQuest a in log.Active)
            {
                QuestDef q = find(a.Id);
                if (q == null)
                {
                    continue;
                }
                foreach (QuestMonsterDrop d in q.MonsterDrops)
                {
                    bool match = d.Monster == monster
                        && (d.EnemyType == 0 || d.EnemyType == enemyType)
                        && (d.Dungeon == -1 || d.Dungeon == dungeon)
                        && (d.Difficulty == -1 || d.Difficulty == difficulty);
                    if (match && !Enough(q, d.Item, itemCount) && rand100() < d.Rate)
                    {
                        drops.Add(new QuestItem(d.Item, d.Count));
                    }
                }
            }
            return drops;
        }

        /// <summary>通关时的任务物品:进行中任务的 <see cref="QuestDef.ClearDrops"/> 里匹配副本与难度的各掷一次。</summary>
        public static List<QuestItem> RollClearDrops(QuestLog log, Func<int, QuestDef> find, int dungeon, int difficulty, Func<int> rand100, Func<int, int> itemCount)
        {
            var drops = new List<QuestItem>();
            foreach (ActiveQuest a in log.Active)
            {
                QuestDef q = find(a.Id);
                if (q == null)
                {
                    continue;
                }
                foreach (QuestClearDrop d in q.ClearDrops)
                {
                    bool match = (d.Dungeon == -1 || d.Dungeon == dungeon) && (d.Difficulty == -1 || d.Difficulty == difficulty);
                    if (match && !Enough(q, d.Item, itemCount) && rand100() < d.Rate)
                    {
                        drops.Add(new QuestItem(d.Item, d.Count));
                    }
                }
            }
            return drops;
        }

        /// <summary>收集类任务里这个物品背包里已经够数了(别的类型的任务不设上限)。</summary>
        private static bool Enough(QuestDef q, int item, Func<int, int> itemCount)
        {
            if (q.Kind != QuestKind.Seeking || itemCount == null)
            {
                return false;
            }
            foreach (QuestItem need in q.SeekingItems())
            {
                if (need.Id == item)
                {
                    return itemCount(item) >= need.Count;
                }
            }
            return false;
        }
    }
}
