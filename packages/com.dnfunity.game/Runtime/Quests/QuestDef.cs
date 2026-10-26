using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-123(服务端静态读码,非 REA)。.qst 的导入函数 0x08a61813 读标记并填 QuestScript(默认值在 QuestScript::clear 0x0836deba:
// 等级与怪物组数没有默认,gold multiple 默认 100,exposeQuest 默认 true,[ignore quest level 4 exp] 默认 false);
// Quest::check_possible 0x08352d86 / checkPossibleJob 0x08352a5c / checkMatchedGrowType 0x08352afc / _isNeededCheckGrowType 0x08352c10
// 读目标角色表;CUser::compute_quest_basic_reward_exp/gold 0x0866e3e2 / 0x0866e5e6 读奖励;
// 整数数据的分组方式由 Quest::CheckKillMonster 0x083535d6 与 CUser::CheckQuestMonster 0x0866cb04 的比较次序、再加本地数据的列对照得出。
namespace Dnf.Game
{
    /// <summary><c>[target character]</c> 的一项(三个值一组):职业(11 = 所有职业)、转职方向(−1 = 任意)、觉醒(−1 = 任意)。</summary>
    public readonly struct QuestTarget
    {
        public QuestTarget(int job, int growType, int awakening)
        {
            Job = job;
            GrowType = growType;
            Awakening = awakening;
        }

        public int Job { get; }

        public int GrowType { get; }

        public int Awakening { get; }
    }

    /// <summary>
    /// 奖励或收集物品的一项:物品编号(奖励里编号 0 是"给金币"的标记,数量不是金额)与数量,
    /// 以及这一项给哪个职业(11 = 所有职业)、哪个转职方向(−1 = 任意)。
    /// 服务端 <c>RewardItemInfo</c> 的四个字段;<c>quest_reward</c> 只给"所有职业"或"职业与转职方向都对得上"的项。
    /// </summary>
    public readonly struct QuestItem
    {
        public QuestItem(int id, int count)
            : this(id, count, EquipRules.AllJobs, -1)
        {
        }

        public QuestItem(int id, int count, int job, int growType)
        {
            Id = id;
            Count = count;
            Job = job;
            GrowType = growType;
        }

        public int Id { get; }

        public int Count { get; }

        /// <summary>给哪个职业(11 = 所有职业)。</summary>
        public int Job { get; }

        /// <summary>给哪个转职方向(−1 = 任意)。</summary>
        public int GrowType { get; }

        /// <summary>这一项发给 (<paramref name="job"/>, <paramref name="growType"/>) 吗:所有职业,或职业相同且转职方向不限或相同。</summary>
        public bool AppliesTo(int job, int growType)
        {
            return Job == EquipRules.AllJobs || (Job == job && (GrowType == -1 || GrowType == growType));
        }
    }

    /// <summary>字符串引用(<c>[name]</c> 这类 "种类 + 键" 的值):交给 <c>StrLibrary.Get(Kind, Key)</c> 取文字。</summary>
    public readonly struct QuestText
    {
        public QuestText(int kind, string key)
        {
            Kind = kind;
            Key = key;
        }

        public int Kind { get; }

        public string Key { get; }

        public bool IsEmpty => string.IsNullOrEmpty(Key);
    }

    /// <summary>猎杀类任务的一组目标:在哪个副本(−1 = 任意)、什么难度(−1 = 任意)杀哪种怪(<see cref="EnemyType"/> 为 0 时不看敌人种类)几只。</summary>
    public readonly struct QuestKillGroup
    {
        public QuestKillGroup(int dungeon, int difficulty, int monster, int enemyType, int count)
        {
            Dungeon = dungeon;
            Difficulty = difficulty;
            Monster = monster;
            EnemyType = enemyType;
            Count = count;
        }

        public int Dungeon { get; }

        public int Difficulty { get; }

        public int Monster { get; }

        /// <summary>敌人种类;0 表示不看(<c>[hunt monster]</c> 没有这一列)。</summary>
        public int EnemyType { get; }

        public int Count { get; }
    }

    /// <summary>
    /// 击杀怪物时按概率掉落的任务物品(<c>[monster reward item]</c> 每 7 个值一组、<c>[enemy reward item]</c> 每 8 个值一组):
    /// 怪物、(敌人种类)、副本、难度、物品、数量、百分率、最后一个值(含义未定)。
    /// </summary>
    public sealed class QuestMonsterDrop
    {
        public QuestMonsterDrop(int monster, int enemyType, int dungeon, int difficulty, int item, int count, int rate, int extra)
        {
            Monster = monster;
            EnemyType = enemyType;
            Dungeon = dungeon;
            Difficulty = difficulty;
            Item = item;
            Count = count;
            Rate = rate;
            Extra = extra;
        }

        public int Monster { get; }

        /// <summary>敌人种类;0 表示来自 <c>[monster reward item]</c>(没有这一列,不看)。</summary>
        public int EnemyType { get; }

        public int Dungeon { get; }

        public int Difficulty { get; }

        public int Item { get; }

        public int Count { get; }

        /// <summary>掉落的百分率:服务端 <c>rand(100) &lt; rate + 加成</c>,所以 100 以上等于必掉。</summary>
        public int Rate { get; }

        /// <summary>每组最后一个值:含义还没弄清,原样保留。</summary>
        public int Extra { get; }
    }

    /// <summary>通关时按概率给的任务物品(<c>[clear reward item]</c> 每 6 个值一组):副本、难度(−1 = 任意)、物品、数量、百分率、最后一个值(含义未定)。</summary>
    public sealed class QuestClearDrop
    {
        public QuestClearDrop(int dungeon, int difficulty, int item, int count, int rate, int extra)
        {
            Dungeon = dungeon;
            Difficulty = difficulty;
            Item = item;
            Count = count;
            Rate = rate;
            Extra = extra;
        }

        public int Dungeon { get; }

        public int Difficulty { get; }

        public int Item { get; }

        public int Count { get; }

        public int Rate { get; }

        public int Extra { get; }
    }

    /// <summary>
    /// 一个任务脚本(<c>n_quest/*.qst</c>)里这一步用到的部分。字段名是自己的,含义见各字段的注释。
    /// 没读的标记(对话脚本、定时发信、出现地图、宠物进化要求等)不影响这一步支持的任务类型。
    /// </summary>
    public sealed class QuestDef
    {
        public QuestDef(int id, string path)
        {
            Id = id;
            Path = path;
        }

        /// <summary>任务编号(<c>quest.lst</c> 里的编号)。</summary>
        public int Id { get; }

        /// <summary>脚本路径(相对 PVF 根)。</summary>
        public string Path { get; }

        /// <summary>分类(<c>[grade]</c>);没写按可重复处理(服务端默认值 4)。</summary>
        public QuestGrade Grade { get; set; } = QuestGrade.NormalRepeat;

        /// <summary>类型(<c>[type]</c>)。</summary>
        public QuestKind Kind { get; set; } = QuestKind.Unknown;

        /// <summary><c>[type]</c> 的原始关键字(不认识的类型用来报告)。</summary>
        public string KindTag { get; set; }

        /// <summary>子类型(<c>[sub type]</c>),没写 −1。</summary>
        public int SubType { get; set; } = -1;

        /// <summary>整数数据(<c>[int data]</c>),含义由类型决定:见 <see cref="KillGroups"/>、<see cref="SeekingItems"/>。</summary>
        public int[] IntData { get; set; } = Array.Empty<int>();

        /// <summary>发布任务的 NPC 编号(<c>[npc index]</c>);没写 −1。</summary>
        public int NpcId { get; set; } = -1;

        /// <summary>交任务的 NPC 编号(<c>[complete npc index]</c>);−1 表示不用回 NPC 那里交(条件满足就算完成)。</summary>
        public int CompleteNpcId { get; set; } = -1;

        /// <summary>最低等级(<c>[level]</c> 第一个值;没写 1)。</summary>
        public int MinLevel { get; set; } = 1;

        /// <summary>最高等级(<c>[level]</c> 第二个值;没写不限)。</summary>
        public int MaxLevel { get; set; } = int.MaxValue;

        /// <summary>可接的角色:<c>[target character]</c> 的各项;没写时用 <c>[job]</c> 与 <c>[grow type]</c> 拼出一项(都没写 = 所有职业所有转职)。</summary>
        public List<QuestTarget> Targets { get; } = new List<QuestTarget>();

        /// <summary><c>[job change quest]</c>:转职任务标志(0 无;1 脚本自相矛盾;2 必须匹配转职方向;10 与 20 不看转职方向)。</summary>
        public int JobChange { get; set; }

        /// <summary>前置任务(<c>[pre required quest]</c>):每一行是一组,组内要全部完成,各组之间只要有一组满足就行。</summary>
        public List<int[]> PreRequired { get; } = new List<int[]>();

        /// <summary>互斥任务(<c>[collision quest]</c>):里面任何一个已经完成,这个任务就不再出现。</summary>
        public List<int> Collision { get; } = new List<int>();

        /// <summary>奖励种类(<c>[reward type]</c> 的关键字;没写等于 <c>[item]</c>)。</summary>
        public string RewardType { get; set; } = "[item]";

        /// <summary>固定奖励(<c>[reward int data]</c>:每项 编号 + 数量,可带 <c>[job]</c> 限定职业;编号 0 是"金币"标记)。</summary>
        public List<QuestItem> Rewards { get; } = new List<QuestItem>();

        /// <summary>多选一奖励(<c>[reward selection int data]</c>:写法同固定奖励;给谁看哪些项由 <see cref="QuestItem.AppliesTo"/> 决定)。</summary>
        public List<QuestItem> SelectRewards { get; } = new List<QuestItem>();

        /// <summary>接任务时给的物品(<c>[depend give item]</c>:写法同固定奖励)。</summary>
        public List<QuestItem> AcceptItems { get; } = new List<QuestItem>();

        /// <summary>金币倍率百分数(<c>[gold multiple]</c>,默认 100)。</summary>
        public int GoldPercent { get; set; } = 100;

        /// <summary>难度字母(<c>[difficulty]</c>,经验权重表的键);没写 '\0'(权重按 100)。</summary>
        public char DifficultyLetter { get; set; }

        /// <summary><c>[ignore quest level 4 exp]</c>:经验表按角色等级取、不按任务最低等级。</summary>
        public bool ExpUsesHeroLevel { get; set; }

        /// <summary><c>[event]</c>:活动任务(经验与金币都按角色等级取表,不扣等级差惩罚)。含义是推断的:服务端有一个同样作用的标志,与这个标记的对应没有逐字节确认。</summary>
        public bool IsEvent { get; set; }

        /// <summary><c>[exposed by npc]</c>:NPC 是否把这个任务列出来(默认 true)。</summary>
        public bool Exposed { get; set; } = true;

        /// <summary><c>[cant giveup]</c>:不能放弃(0 可以;非 0 不行)。</summary>
        public int CantGiveUp { get; set; }

        /// <summary>任务点(<c>[quest point]</c>)。</summary>
        public int QuestPoint { get; set; }

        /// <summary>任务碎片(<c>[quest piece]</c>)。</summary>
        public int QuestPiece { get; set; }

        /// <summary>任务名(<c>[name]</c>)。</summary>
        public QuestText Name { get; set; }

        /// <summary>接任务前 NPC 说的话(<c>[depend message]</c>)。</summary>
        public QuestText DependMessage { get; set; }

        /// <summary>任务进行中的条件说明(<c>[condition message]</c>)。</summary>
        public QuestText ConditionMessage { get; set; }

        /// <summary>交任务时 NPC 说的话(<c>[solve message]</c>)。</summary>
        public QuestText SolveMessage { get; set; }

        /// <summary>击杀掉落的任务物品(<c>[monster reward item]</c> 与 <c>[enemy reward item]</c> 的所有组)。</summary>
        public List<QuestMonsterDrop> MonsterDrops { get; } = new List<QuestMonsterDrop>();

        /// <summary>通关掉落的任务物品(<c>[clear reward item]</c>)。</summary>
        public List<QuestClearDrop> ClearDrops { get; } = new List<QuestClearDrop>();

        /// <summary>任务指引里的副本(<c>[dungeon info]</c>:副本编号与难度成对,难度 −1 = 任意)。</summary>
        public List<KeyValuePair<int, int>> DungeonHints { get; } = new List<KeyValuePair<int, int>>();

        /// <summary>
        /// 猎杀类任务的目标组:<see cref="QuestKind.HuntMonster"/> 每 4 个整数一组(副本、难度、怪物、数量),
        /// <see cref="QuestKind.HuntEnemy"/> 每 5 个一组(副本、难度、怪物、敌人种类、数量)。别的类型返回空表。
        /// </summary>
        public List<QuestKillGroup> KillGroups()
        {
            var groups = new List<QuestKillGroup>();
            int[] d = IntData;
            if (Kind == QuestKind.HuntMonster)
            {
                for (int i = 0; i + 4 <= d.Length; i += 4)
                {
                    groups.Add(new QuestKillGroup(d[i], d[i + 1], d[i + 2], 0, d[i + 3]));
                }
            }
            else if (Kind == QuestKind.HuntEnemy)
            {
                for (int i = 0; i + 5 <= d.Length; i += 5)
                {
                    groups.Add(new QuestKillGroup(d[i], d[i + 1], d[i + 2], d[i + 3], d[i + 4]));
                }
            }
            return groups;
        }

        /// <summary><see cref="QuestKind.Seeking"/> 要收集的物品:整数数据按 (物品编号, 数量) 成对。别的类型返回空表。</summary>
        public List<QuestItem> SeekingItems()
        {
            var items = new List<QuestItem>();
            if (Kind == QuestKind.Seeking)
            {
                for (int i = 0; i + 2 <= IntData.Length; i += 2)
                {
                    items.Add(new QuestItem(IntData[i], IntData[i + 1]));
                }
            }
            return items;
        }

        /// <summary>
        /// <see cref="QuestKind.MeetNpc"/> 要拜访的 NPC:整数数据的第一个值;没写时用交任务 NPC。别的类型返回 −1。
        /// </summary>
        public int MeetNpcId()
        {
            if (Kind != QuestKind.MeetNpc)
            {
                return -1;
            }
            return IntData.Length > 0 ? IntData[0] : CompleteNpcId;
        }

        /// <summary>
        /// 通过房间类任务的地图编号(<see cref="QuestKind.ClearMap"/>:整数数据第一个值;服务端比较的就是玩家上次通过的地图编号);
        /// 别的类型或没有数据返回 −1。
        /// </summary>
        public int ClearMapId()
        {
            return Kind == QuestKind.ClearMap && IntData.Length > 0 ? IntData[0] : -1;
        }
    }

    /// <summary>读一个 <c>.qst</c> 脚本。</summary>
    public static class QuestScript
    {
        /// <summary>把脚本的各行解成 <see cref="QuestDef"/>;不认识的标记忽略。</summary>
        public static QuestDef Parse(ScriptLine[] lines, int id, string path)
        {
            var q = new QuestDef(id, path);
            if (lines == null)
            {
                return q;
            }

            int job = EquipRules.AllJobs;
            int grow = -1;
            foreach (ScriptLine line in lines)
            {
                string tag = line.Tag;
                if (tag == null)
                {
                    continue;
                }
                switch (tag.ToLowerInvariant())
                {
                    case "[grade]":
                        if (QuestNames.TryGrade(line.FirstString(), out QuestGrade grade))
                        {
                            q.Grade = grade;
                        }
                        break;
                    case "[type]":
                        q.KindTag = line.FirstString();
                        q.Kind = QuestNames.KindOf(q.KindTag);
                        break;
                    case "[sub type]":
                        q.SubType = FirstInt(line, -1);
                        break;
                    case "[int data]":
                        q.IntData = line.Ints().ToArray();
                        break;
                    case "[npc index]":
                        q.NpcId = FirstInt(line, -1);
                        break;
                    case "[complete npc index]":
                        q.CompleteNpcId = FirstInt(line, -1);
                        break;
                    case "[level]":
                    {
                        List<int> v = line.Ints();
                        if (v.Count > 0)
                        {
                            q.MinLevel = v[0];
                        }
                        if (v.Count > 1)
                        {
                            q.MaxLevel = v[1];
                        }
                        break;
                    }
                    case "[job]":
                        job = line.FirstString() == null ? job : EquipRules.JobOf(line.FirstString());
                        break;
                    case "[grow type]":
                        grow = FirstInt(line, -1);
                        break;
                    case "[target character]":
                        ReadTargets(line, q.Targets);
                        break;
                    case "[job change quest]":
                        q.JobChange = FirstInt(line, 0);
                        break;
                    case "[pre required quest]":
                    {
                        List<int> v = line.Ints();
                        if (v.Count > 0)
                        {
                            q.PreRequired.Add(v.ToArray());
                        }
                        break;
                    }
                    case "[collision quest]":
                        q.Collision.AddRange(line.Ints());
                        break;
                    case "[reward type]":
                        q.RewardType = line.FirstString() ?? q.RewardType;
                        break;
                    case "[reward int data]":
                        ReadRewardItems(line, q.Rewards);
                        break;
                    case "[reward selection int data]":
                        ReadRewardItems(line, q.SelectRewards);
                        break;
                    case "[depend give item]":
                        ReadRewardItems(line, q.AcceptItems);
                        break;
                    case "[gold multiple]":
                        q.GoldPercent = FirstInt(line, 100);
                        break;
                    case "[difficulty]":
                    {
                        string letter = line.FirstString();
                        q.DifficultyLetter = string.IsNullOrEmpty(letter) ? '\0' : letter[0];
                        break;
                    }
                    case "[ignore quest level 4 exp]":
                        q.ExpUsesHeroLevel = FirstInt(line, 0) == 1;
                        break;
                    case "[event]":
                        q.IsEvent = FirstInt(line, 0) == 1;
                        break;
                    case "[exposed by npc]":
                        q.Exposed = FirstInt(line, 1) == 1;
                        break;
                    case "[cant giveup]":
                        q.CantGiveUp = FirstInt(line, 0);
                        break;
                    case "[quest point]":
                        q.QuestPoint = FirstInt(line, 0);
                        break;
                    case "[quest piece]":
                        q.QuestPiece = FirstInt(line, 0);
                        break;
                    case "[name]":
                        q.Name = TextOf(line);
                        break;
                    case "[depend message]":
                        q.DependMessage = TextOf(line);
                        break;
                    case "[condition message]":
                        q.ConditionMessage = TextOf(line);
                        break;
                    case "[solve message]":
                        q.SolveMessage = TextOf(line);
                        break;
                    case "[monster reward item]":
                        ReadMonsterDrops(line, 7, q.MonsterDrops);
                        break;
                    case "[enemy reward item]":
                        ReadMonsterDrops(line, 8, q.MonsterDrops);
                        break;
                    case "[clear reward item]":
                        ReadClearDrops(line, q.ClearDrops);
                        break;
                    case "[dungeon info]":
                    {
                        List<int> v = line.Ints();
                        for (int i = 0; i + 2 <= v.Count; i += 2)
                        {
                            q.DungeonHints.Add(new KeyValuePair<int, int>(v[i], v[i + 1]));
                        }
                        break;
                    }
                }
            }

            if (q.Targets.Count == 0)
            {
                q.Targets.Add(new QuestTarget(job, grow, -1));
            }
            return q;
        }

        private static int FirstInt(ScriptLine line, int fallback)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    return v.Int;
                }
            }
            return fallback;
        }

        private static QuestText TextOf(ScriptLine line)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Keyed)
                {
                    return new QuestText(v.Int, v.Text);
                }
            }
            return default;
        }

        /// <summary>
        /// 奖励物品表的写法(服务端 <c>ScanRewardItems</c> 0x08a65da5):每项是 <c>编号 数量</c>,
        /// 或者 <c>编号 "[job]" 职业 转职方向 数量</c>(只给这个职业与转职方向;不写就是所有职业、转职方向不限)。读到不成形的就停。
        /// </summary>
        private static void ReadRewardItems(ScriptLine line, List<QuestItem> into)
        {
            ScriptValue[] v = line.Values;
            int i = 0;
            while (i < v.Length)
            {
                if (!v[i].IsNumber)
                {
                    i++;
                    continue;
                }
                int id = v[i].Int;
                i++;
                int job = EquipRules.AllJobs;
                int grow = -1;
                if (i < v.Length && v[i].Kind == ScriptValueKind.String)
                {
                    bool jobForm = string.Equals(v[i].Text, "[job]", StringComparison.OrdinalIgnoreCase);
                    i++;
                    if (jobForm)
                    {
                        if (i + 1 >= v.Length || !v[i].IsNumber || !v[i + 1].IsNumber)
                        {
                            return;
                        }
                        job = v[i].Int;
                        grow = v[i + 1].Int;
                        i += 2;
                    }
                }
                if (i >= v.Length || !v[i].IsNumber)
                {
                    return;
                }
                into.Add(new QuestItem(id, v[i].Int, job, grow));
                i++;
            }
        }

        /// <summary><c>[target character]</c> 是"职业名、转职方向、觉醒"三个一组,可以重复多组。</summary>
        private static void ReadTargets(ScriptLine line, List<QuestTarget> into)
        {
            ScriptValue[] values = line.Values;
            int i = 0;
            while (i < values.Length)
            {
                if (values[i].Kind != ScriptValueKind.String)
                {
                    i++;
                    continue;
                }
                int job = EquipRules.JobOf(values[i].Text);
                int grow = -1;
                int awakening = -1;
                int taken = 0;
                int j = i + 1;
                while (j < values.Length && values[j].IsNumber && taken < 2)
                {
                    if (taken == 0)
                    {
                        grow = values[j].Int;
                    }
                    else
                    {
                        awakening = values[j].Int;
                    }
                    taken++;
                    j++;
                }
                into.Add(new QuestTarget(job, grow, awakening));
                i = j;
            }
        }

        private static void ReadMonsterDrops(ScriptLine line, int width, List<QuestMonsterDrop> into)
        {
            List<int> v = line.Ints();
            for (int i = 0; i + width <= v.Count; i += width)
            {
                if (width == 7)
                {
                    into.Add(new QuestMonsterDrop(v[i], 0, v[i + 1], v[i + 2], v[i + 3], v[i + 4], v[i + 5], v[i + 6]));
                }
                else
                {
                    into.Add(new QuestMonsterDrop(v[i], v[i + 1], v[i + 2], v[i + 3], v[i + 4], v[i + 5], v[i + 6], v[i + 7]));
                }
            }
        }

        private static void ReadClearDrops(ScriptLine line, List<QuestClearDrop> into)
        {
            List<int> v = line.Ints();
            for (int i = 0; i + 6 <= v.Count; i += 6)
            {
                into.Add(new QuestClearDrop(v[i], v[i + 1], v[i + 2], v[i + 3], v[i + 4], v[i + 5]));
            }
        }
    }
}
