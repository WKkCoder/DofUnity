using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

// 本项目自己的接线(M31):规则与数据在 Dnf.Game 的 Quests 目录(证据 E-123,服务端静态读码,非 REA);原版客户端怎么把任务事件送进去没有读到,
// 事件的挂接点(击杀、清房、通关、对话、拾取)、"条件满足且没有交任务 NPC 就自动完成"、全部是这一层的设计(推断)。
namespace Dnf.Play
{
    /// <summary>任务窗口里一行的状态。</summary>
    public enum QuestRowState
    {
        /// <summary>这个 NPC 发布、主角现在可以接。</summary>
        Available = 0,

        /// <summary>已经接了,条件还没满足(在交任务的 NPC 这里可以看提示)。</summary>
        InProgress = 1,

        /// <summary>条件满足了,在这个 NPC 这里可以交。</summary>
        Ready = 2,
    }

    /// <summary>NPC 头顶的任务标记(感叹号、问号):从低到高优先,Ready 盖过 Available 盖过 InProgress。</summary>
    public enum QuestMark
    {
        None = 0,

        /// <summary>灰色问号:有进行中的任务要在这里交,但还没做完。</summary>
        InProgress = 1,

        /// <summary>感叹号:有新任务可以接。</summary>
        Available = 2,

        /// <summary>问号:有任务可以交了。</summary>
        Ready = 3,
    }

    /// <summary>任务动作(接、交、放弃)的结果。</summary>
    public enum QuestActionResult
    {
        Ok = 0,
        NotFound = 1,
        NotAvailable = 2,
        NotReady = 3,
        NoRoom = 4,
        CannotGiveUp = 5,
    }

    /// <summary>提示的种类。</summary>
    public enum QuestNoticeKind
    {
        Accepted = 0,
        Progress = 1,
        Ready = 2,
        Completed = 3,
        Abandoned = 4,
        ItemFound = 5,
        BagFull = 6,
    }

    /// <summary>奖励或选择奖励里的一行:物品名(取不到是 null)、编号、数量。</summary>
    public readonly struct QuestRewardLine
    {
        public QuestRewardLine(int code, int count, string name)
        {
            Code = code;
            Count = count;
            Name = name;
        }

        public int Code { get; }

        public int Count { get; }

        public string Name { get; }
    }

    /// <summary>
    /// 任务窗口里的一行(也是任务日志里的一行):纯数据,不含 Dnf.Game 的类型(宿主层不引用 Dnf.Game)。
    /// 文字(名字与对白)取不到是 null,画的时候退回占位。
    /// </summary>
    public sealed class QuestRow
    {
        public int Id { get; set; }

        public QuestRowState State { get; set; }

        /// <summary>任务名;取不到是 null。</summary>
        public string Name { get; set; }

        /// <summary>这一状态下 NPC 说的话:可以接 = 接取对话,进行中 = 进行中对话,可以交 = 完成对话;取不到是 null。</summary>
        public string Message { get; set; }

        /// <summary>进度文字,如 <c>2/5</c>(多项用空格隔开);可以接或没有计数的任务是空串。</summary>
        public string Progress { get; set; } = string.Empty;

        /// <summary>要求的最低等级。</summary>
        public int MinLevel { get; set; }

        /// <summary>交任务的 NPC 编号;−1 表示条件满足就自动完成。</summary>
        public int HandInNpc { get; set; } = -1;

        /// <summary>完成时给的经验与金币(按这个角色现在的等级算)。</summary>
        public int Exp { get; set; }

        public int Gold { get; set; }

        /// <summary>转职任务给的新成长类型下标(完成时角色换成这个成长类型);没有这种奖励是 −1。</summary>
        public int Growtype { get; set; } = -1;

        /// <summary>觉醒任务给的觉醒号(1 一觉、2 二觉;完成时角色觉醒到这一步,条件不满足则不换);没有这种奖励是 0。</summary>
        public int Awakening { get; set; }

        /// <summary>固定给的物品。</summary>
        public List<QuestRewardLine> Rewards { get; } = new List<QuestRewardLine>();

        /// <summary>多选一的奖励(没有为空);交任务时用 <see cref="QuestBook.TurnIn"/> 的第二个参数选其中一个。</summary>
        public List<QuestRewardLine> Choices { get; } = new List<QuestRewardLine>();

        /// <summary>能放弃吗(<c>[cant giveup]</c> 非 0 的不能)。</summary>
        public bool CanAbandon { get; set; } = true;
    }

    /// <summary>任务系统给界面的一条提示(接了、有进展、可以交了、完成了、拿到任务物品、背包满了)。</summary>
    public sealed class QuestNotice
    {
        public QuestNotice(QuestNoticeKind kind, int questId, string title, string detail, string extra)
        {
            Kind = kind;
            QuestId = questId;
            Title = title ?? string.Empty;
            Detail = detail ?? string.Empty;
            Extra = extra ?? string.Empty;
        }

        public QuestNoticeKind Kind { get; }

        public int QuestId { get; }

        /// <summary>标题(占位英文,<see cref="UiText"/> 有中文;画的时候走 <see cref="OverlayArt.Text"/>)。</summary>
        public string Title { get; private set; }

        /// <summary>细节:任务名(带进度)或物品名,是数据文字,取不到时是占位的 <c>QUEST 编号</c>。</summary>
        public string Detail { get; private set; }

        /// <summary>完成时的奖励概要(占位英文,如 <c>EXP 100  GOLD 20</c>;换了成长类型的转职任务再加 <c>JOB CHANGE</c>,觉醒了的再加 <c>AWAKENING</c>),其它提示是空串。</summary>
        public string Extra { get; }

        internal void Update(string detail)
        {
            Detail = detail ?? string.Empty;
        }
    }

    /// <summary>
    /// 游戏里发生的、任务关心的事(击杀、清房、通关);副本会话在对应的时刻调用,任务系统据此累计计数、掉任务物品、判断条件满足。
    /// 副本会话只依赖这个接口,所以不用装任何任务数据也能测。
    /// </summary>
    public interface IQuestEvents
    {
        /// <summary>主角杀死了一只怪物。<paramref name="boss"/> 为真时敌人种类取"首领"(推断,见 <see cref="QuestRules.EnemyNamed"/>)。</summary>
        void MonsterKilled(int dungeonId, int difficulty, int monsterCode, bool boss);

        /// <summary>清空了一间房间(地图编号是 map/map.lst 里的号)。</summary>
        void MapCleared(int mapId);

        /// <summary>通关了整座副本(<paramref name="clearMilliseconds"/> 是从进副本到通关的毫秒数)。</summary>
        void DungeonCleared(int dungeonId, int difficulty, int clearMilliseconds);
    }

    /// <summary>
    /// 任务系统在游戏流程里的接口(M31):包着任务目录(<see cref="QuestCatalog"/>)、主角的进度(<see cref="HeroProgress.Quests"/> 与背包、等级、金币)、文字(<see cref="QuestTexts"/>)。
    /// 给 NPC 对话窗口(<see cref="QuestWindow"/>)用的查询:NPC 头顶的标记(<see cref="MarkOf"/>)、这个 NPC 这里的任务行(<see cref="RowsFor"/>)、日志(<see cref="LogRows"/>);
    /// 动作:接(<see cref="Accept"/>)、交(<see cref="TurnIn"/>)、放弃(<see cref="Abandon"/>)、拜访 NPC(<see cref="Talk"/>);
    /// 事件(<see cref="IQuestEvents"/>)由副本会话调用;<see cref="Update"/> 每帧调用一次(背包变了就重新判断收集类任务有没有凑齐)。
    /// 条件满足时:有交任务 NPC 的等着玩家去交(提示一次),没有交任务 NPC 的(<c>[complete npc index]</c> 为 −1)自动完成并发奖励(背包放不下就留着,以后再试)。
    /// 数量、编号来自用户自己的数据,这里没有任何原版内容。
    /// </summary>
    public sealed class QuestBook : IQuestEvents
    {
        private readonly QuestCatalog _catalog;
        private readonly HeroProgress _hero;
        private readonly Func<int, ItemInfo> _item;
        private readonly QuestTexts _texts;
        private readonly ItemTexts _itemTexts;
        private readonly Func<int, bool> _monsterExists;
        private readonly Random _random;
        private readonly List<QuestNotice> _notices = new List<QuestNotice>();
        private readonly HashSet<int> _readyNoted = new HashSet<int>();
        private int _bagSignature = -1;
        private bool _dirty = true;

        public QuestBook(QuestCatalog catalog, HeroProgress hero, Func<int, ItemInfo> item, QuestTexts texts = null, ItemTexts itemTexts = null,
            Func<int, bool> monsterExists = null, int seed = 1)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _item = item ?? throw new ArgumentNullException(nameof(item));
            _texts = texts;
            _itemTexts = itemTexts;
            _monsterExists = monsterExists;
            _random = new Random(seed);
        }

        /// <summary>从用户的数据目录造:任务目录、物品目录、字符串表、NPC 与怪物的名字都按需读。</summary>
        public static QuestBook FromAssets(DnfAssets assets, HeroProgress hero, int seed = 1)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (hero == null)
            {
                throw new ArgumentNullException(nameof(hero));
            }
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            StrLibrary library = WorldTexts.Shared?.Library
                ?? new StrLibrary(path => assets.Pvf.TryGetEntry(path, out PvfEntry entry) ? assets.Pvf.Read(entry) : null);
            var worldTexts = new WorldTexts(library);
            NpcNames npcs = NpcNames.Create(assets, worldTexts);
            Dictionary<int, string> apcList = null;
            var texts = new QuestTexts(library, npcs.DialogNameOf,
                id => worldTexts.MonsterName(data.GetMonster(id)),
                id =>
                {
                    if (apcList == null)
                    {
                        apcList = data.ReadList("aicharacter/aicharacter.lst") ?? new Dictionary<int, string>();
                    }
                    return apcList.TryGetValue(id, out string file) ? worldTexts.ScriptName(data.ReadScript(PvfPath.ResolveRelativeTo("aicharacter/", file))) : null;
                });
            var monsters = new HashSet<int>(data.MonsterIds());
            var items = new ItemCatalog(data);
            return new QuestBook(new QuestCatalog(data), hero, items.Get, texts, new ItemTexts(library), monsters.Contains, seed);
        }

        public HeroProgress Hero => _hero;

        public QuestCatalog Catalog => _catalog;

        public QuestLog Log => _hero.Quests;

        /// <summary>任务清单(quest.lst)里列了多少个任务(日志用;第一次读要解析清单,但不解析各个任务脚本)。</summary>
        public int ListedCount => _catalog.ListedCount;

        /// <summary>接过多少次任务、完成过多少次、放弃过多少次(这一次运行里;日志用)。</summary>
        public int Accepted { get; private set; }

        public int Completed { get; private set; }

        public int Abandoned { get; private set; }

        /// <summary>靠转职任务奖励换过多少次成长类型(这一次运行里;日志用)。</summary>
        public int JobChanges { get; private set; }

        /// <summary>靠觉醒任务奖励觉醒过多少次(这一次运行里;日志用)。</summary>
        public int Awakenings { get; private set; }

        /// <summary>任务的计数被击杀事件推进过多少次、拿到过多少件任务物品(日志用)。</summary>
        public int KillsCounted { get; private set; }

        public int QuestItemsFound { get; private set; }

        /// <summary>把任务目录的索引建好(第一次建要解析全部任务脚本,有一会儿);宿主在加载画面里调一次,免得第一次开窗卡一下。</summary>
        public void Warm()
        {
            _catalog.OfferedBy(-1);
            _catalog.CompletedAt(-1);
        }

        private QuestDef Find(int id) => _catalog.Get(id);

        private int ItemCount(int code) => _hero.Bag.CountOf(code);

        private int Rand100() => _random.Next(100);

        private QuestHero HeroView => new QuestHero(_hero.Level, _hero.Job, _hero.Growtype);

        /// <summary>
        /// 读档之后按数据核对进行中的任务:任务不存在或这一步不支持的丢掉;计数表长度对不上的重置成刚接下时的计数,每项夹在 0 到初始值之间。
        /// 返回丢掉或重置了几个。
        /// </summary>
        public int Reconcile()
        {
            int fixedCount = 0;
            var drop = new List<int>();
            var reset = new List<ActiveQuest>();
            foreach (ActiveQuest active in Log.Active)
            {
                QuestDef def = Find(active.Id);
                if (def == null || !QuestRules.IsSupported(def, _monsterExists))
                {
                    drop.Add(active.Id);
                    continue;
                }
                int[] initial = QuestRules.InitialRemaining(def);
                if (initial.Length != active.Remaining.Length)
                {
                    reset.Add(active);
                    continue;
                }
                for (int i = 0; i < initial.Length; i++)
                {
                    int clamped = Math.Max(0, Math.Min(active.Remaining[i], initial[i]));
                    if (clamped != active.Remaining[i])
                    {
                        active.Remaining[i] = clamped;
                        fixedCount++;
                    }
                }
            }
            foreach (int id in drop)
            {
                Log.Remove(id);
                fixedCount++;
            }
            foreach (ActiveQuest active in reset)
            {
                Log.Remove(active.Id);
                Log.Add(active.Id, QuestRules.InitialRemaining(Find(active.Id)));
                fixedCount++;
            }
            _readyNoted.Clear();
            _dirty = true;
            return fixedCount;
        }

        // ------------------------------------------------------------------ 查询

        /// <summary>这个 NPC 头顶该显示的标记。</summary>
        public QuestMark MarkOf(int npcId)
        {
            QuestMark best = QuestMark.None;
            QuestHero hero = HeroView;
            foreach (QuestDef def in _catalog.OfferedBy(npcId))
            {
                if (QuestRules.CheckOffer(def, hero, Log) == QuestOffer.Ok)
                {
                    best = QuestMark.Available;
                    break;
                }
            }
            foreach (ActiveQuest active in Log.Active)
            {
                QuestDef def = Find(active.Id);
                if (def == null || QuestRules.HandInNpc(def) != npcId)
                {
                    continue;
                }
                // 拜访类的任务:走到被拜访的 NPC 身边按一下就完成,所以在那个 NPC 头顶就当作"可以交"
                bool ready = QuestRules.IsReady(def, active, ItemCount) || def.Kind == QuestKind.MeetNpc;
                QuestMark mark = ready ? QuestMark.Ready : QuestMark.InProgress;
                if (mark > best)
                {
                    best = mark;
                }
            }
            return best;
        }

        /// <summary>这个 NPC 这里的任务行:可以交的在前,然后是可以接的(任务编号从小到大),最后是进行中的。</summary>
        public List<QuestRow> RowsFor(int npcId)
        {
            var ready = new List<QuestRow>();
            var available = new List<QuestRow>();
            var progress = new List<QuestRow>();
            QuestHero hero = HeroView;
            foreach (ActiveQuest active in Log.Active)
            {
                QuestDef def = Find(active.Id);
                if (def == null || QuestRules.HandInNpc(def) != npcId)
                {
                    continue;
                }
                bool isReady = QuestRules.IsReady(def, active, ItemCount);
                (isReady ? ready : progress).Add(BuildRow(def, isReady ? QuestRowState.Ready : QuestRowState.InProgress, active));
            }
            var offered = new List<QuestDef>();
            foreach (QuestDef def in _catalog.OfferedBy(npcId))
            {
                if (QuestRules.CheckOffer(def, hero, Log) == QuestOffer.Ok)
                {
                    offered.Add(def);
                }
            }
            offered.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (QuestDef def in offered)
            {
                available.Add(BuildRow(def, QuestRowState.Available, null));
            }
            var rows = new List<QuestRow>(ready.Count + available.Count + progress.Count);
            rows.AddRange(ready);
            rows.AddRange(available);
            rows.AddRange(progress);
            return rows;
        }

        /// <summary>任务日志:进行中的每个任务(接取次序);条件满足的标成可以交。</summary>
        public List<QuestRow> LogRows()
        {
            var rows = new List<QuestRow>();
            foreach (ActiveQuest active in Log.Active)
            {
                QuestDef def = Find(active.Id);
                if (def == null)
                {
                    continue;
                }
                bool ready = QuestRules.IsReady(def, active, ItemCount);
                rows.Add(BuildRow(def, ready ? QuestRowState.Ready : QuestRowState.InProgress, active));
            }
            return rows;
        }

        /// <summary>某个任务现在的行(给界面刷新用);任务不存在返回 null。</summary>
        public QuestRow RowOf(int questId)
        {
            QuestDef def = Find(questId);
            if (def == null)
            {
                return null;
            }
            ActiveQuest active = Log.Find(questId);
            if (active == null)
            {
                return BuildRow(def, QuestRowState.Available, null);
            }
            return BuildRow(def, QuestRules.IsReady(def, active, ItemCount) ? QuestRowState.Ready : QuestRowState.InProgress, active);
        }

        private QuestRow BuildRow(QuestDef def, QuestRowState state, ActiveQuest active)
        {
            QuestHero hero = HeroView;
            var row = new QuestRow
            {
                Id = def.Id,
                State = state,
                Name = _texts?.NameOf(def),
                MinLevel = def.MinLevel,
                HandInNpc = QuestRules.HandInNpc(def),
                CanAbandon = def.CantGiveUp == 0,
            };
            switch (state)
            {
                case QuestRowState.Available:
                    row.Message = _texts?.DependOf(def);
                    break;
                case QuestRowState.InProgress:
                    row.Message = _texts?.ConditionOf(def) ?? _texts?.DependOf(def);
                    break;
                default:
                    row.Message = _texts?.SolveOf(def) ?? _texts?.ConditionOf(def) ?? _texts?.DependOf(def);
                    break;
            }
            if (active != null)
            {
                row.Progress = ProgressText(def, active);
            }
            QuestReward reward = QuestRules.ComputeReward(def, hero, _catalog.Parameters, -1);
            row.Exp = reward.Exp;
            row.Gold = reward.Gold;
            row.Growtype = reward.Growtype;
            row.Awakening = reward.Awakening;
            foreach (QuestItem item in reward.Items)
            {
                row.Rewards.Add(Line(item));
            }
            foreach (QuestItem item in QuestRules.SelectableRewards(def, hero))
            {
                row.Choices.Add(Line(item));
            }
            return row;
        }

        private QuestRewardLine Line(QuestItem item)
        {
            ItemInfo info = _item(item.Id);
            return new QuestRewardLine(item.Id, item.Count, info == null ? null : _itemTexts?.NameOf(info));
        }

        /// <summary>进度文字:猎杀类每组 <c>已杀/总数</c>,收集类每种 <c>背包里有的(不超过要的)/要的</c>,其余 <c>已完成次数/总次数</c>,各项用空格隔开。</summary>
        public string ProgressText(QuestDef def, ActiveQuest active)
        {
            var parts = new List<string>();
            switch (def.Kind)
            {
                case QuestKind.HuntMonster:
                case QuestKind.HuntEnemy:
                {
                    List<QuestKillGroup> groups = def.KillGroups();
                    for (int i = 0; i < groups.Count && i < active.Remaining.Length; i++)
                    {
                        int total = Math.Max(0, groups[i].Count);
                        parts.Add(Math.Max(0, total - active.Remaining[i]).ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture));
                    }
                    break;
                }
                case QuestKind.Seeking:
                    foreach (QuestItem need in def.SeekingItems())
                    {
                        int have = Math.Min(ItemCount(need.Id), need.Count);
                        parts.Add(have.ToString(CultureInfo.InvariantCulture) + "/" + need.Count.ToString(CultureInfo.InvariantCulture));
                    }
                    break;
                default:
                {
                    int[] initial = QuestRules.InitialRemaining(def);
                    for (int i = 0; i < initial.Length && i < active.Remaining.Length; i++)
                    {
                        parts.Add(Math.Max(0, initial[i] - active.Remaining[i]).ToString(CultureInfo.InvariantCulture) + "/" + initial[i].ToString(CultureInfo.InvariantCulture));
                    }
                    break;
                }
            }
            return string.Join(" ", parts);
        }

        // ------------------------------------------------------------------ 动作

        /// <summary>接任务:能接(<see cref="QuestRules.CheckOffer"/>)、接取时给的物品背包放得下才成功。</summary>
        public QuestActionResult Accept(int questId)
        {
            QuestDef def = Find(questId);
            if (def == null)
            {
                return QuestActionResult.NotFound;
            }
            QuestHero hero = HeroView;
            if (QuestRules.CheckOffer(def, hero, Log) != QuestOffer.Ok)
            {
                return QuestActionResult.NotAvailable;
            }
            var give = new List<KeyValuePair<ItemInfo, int>>();
            foreach (QuestItem item in def.AcceptItems)
            {
                ItemInfo info = _item(item.Id);
                if (info != null && item.Count > 0 && item.AppliesTo(hero.Job, hero.GrowType))
                {
                    give.Add(new KeyValuePair<ItemInfo, int>(info, item.Count));
                }
            }
            if (!_hero.Bag.CanSwap(null, give))
            {
                Notify(QuestNoticeKind.BagFull, questId, "BAG FULL", NameOf(def), null);
                return QuestActionResult.NoRoom;
            }
            if (QuestRules.Accept(def, hero, Log, out QuestOffer _) == null)
            {
                return QuestActionResult.NotAvailable;
            }
            foreach (KeyValuePair<ItemInfo, int> g in give)
            {
                _hero.AddItem(g.Key, g.Value);
            }
            Accepted++;
            _readyNoted.Remove(questId);
            Notify(QuestNoticeKind.Accepted, questId, "QUEST ACCEPTED", NameOf(def), null);
            _dirty = true;
            EvaluateReady();
            return QuestActionResult.Ok;
        }

        /// <summary>
        /// 交任务:条件满足才行;收集类先交出材料、再领奖励,背包放不下奖励就整个不交(<see cref="QuestActionResult.NoRoom"/>)。
        /// <paramref name="selection"/> 是 <see cref="QuestRow.Choices"/> 里选的下标;有多选一奖励而没选(越界)时取第一项。
        /// </summary>
        public QuestActionResult TurnIn(int questId, int selection = -1)
        {
            return TurnInCore(questId, selection, true);
        }

        private QuestActionResult TurnInCore(int questId, int selection, bool noticeNoRoom)
        {
            QuestDef def = Find(questId);
            ActiveQuest active = Log.Find(questId);
            if (def == null || active == null)
            {
                return QuestActionResult.NotFound;
            }
            if (!QuestRules.IsReady(def, active, ItemCount))
            {
                return QuestActionResult.NotReady;
            }
            QuestHero hero = HeroView;
            List<QuestItem> choices = QuestRules.SelectableRewards(def, hero);
            int pick = choices.Count == 0 ? -1 : (selection >= 0 && selection < choices.Count ? selection : 0);
            QuestReward reward = QuestRules.ComputeReward(def, hero, _catalog.Parameters, pick);

            var take = new List<KeyValuePair<int, int>>();
            if (def.Kind == QuestKind.Seeking)
            {
                foreach (QuestItem need in def.SeekingItems())
                {
                    take.Add(new KeyValuePair<int, int>(need.Id, need.Count));
                }
            }
            var give = new List<KeyValuePair<ItemInfo, int>>();
            foreach (QuestItem item in reward.Items)
            {
                ItemInfo info = _item(item.Id);
                if (info != null)
                {
                    give.Add(new KeyValuePair<ItemInfo, int>(info, item.Count));
                }
            }
            if (!_hero.Bag.CanSwap(take, give))
            {
                if (noticeNoRoom)
                {
                    Notify(QuestNoticeKind.BagFull, questId, "BAG FULL", NameOf(def), null);
                }
                return QuestActionResult.NoRoom;
            }
            foreach (KeyValuePair<int, int> t in take)
            {
                _hero.Bag.Remove(t.Key, t.Value);
            }
            QuestRules.Complete(def, Log);
            _hero.Gain(reward.Exp);
            _hero.AddGold(reward.Gold);
            foreach (KeyValuePair<ItemInfo, int> g in give)
            {
                _hero.AddItem(g.Key, g.Value);
            }
            // 转职奖励:换成长类型(技能清空后重给白送的、点数回到总额、属性按新成长类型重算,都由 ChangeGrowtype 做);已经是这个成长类型就什么也不换
            bool jobChanged = reward.Growtype >= 0 && _hero.ChangeGrowtype(reward.Growtype);
            if (jobChanged)
            {
                JobChanges++;
            }
            // 觉醒奖励:第二成长类型换成奖励给的号(白送觉醒技能、属性换觉醒块,都由 Awaken 做);等级不到、次序不对、没转职的话任务照样完成,只是不觉醒
            bool awakened = reward.Awakening >= 1 && _hero.Awaken(reward.Awakening) == AwakeningCheck.Ok;
            if (awakened)
            {
                Awakenings++;
            }
            Completed++;
            _readyNoted.Remove(questId);
            string extra = reward.Exp > 0 || reward.Gold > 0
                ? "EXP " + reward.Exp.ToString(CultureInfo.InvariantCulture) + "  GOLD " + reward.Gold.ToString(CultureInfo.InvariantCulture)
                : null;
            if (jobChanged)
            {
                extra = extra == null ? "JOB CHANGE" : extra + "  JOB CHANGE";
            }
            if (awakened)
            {
                extra = extra == null ? "AWAKENING" : extra + "  AWAKENING";
            }
            Notify(QuestNoticeKind.Completed, questId, "QUEST COMPLETE", NameOf(def), extra);
            _dirty = true;
            return QuestActionResult.Ok;
        }

        /// <summary>放弃任务(<c>[cant giveup]</c> 非 0 的不行)。</summary>
        public QuestActionResult Abandon(int questId)
        {
            QuestDef def = Find(questId);
            if (def == null || !Log.IsActive(questId))
            {
                return QuestActionResult.NotFound;
            }
            if (!QuestRules.Abandon(def, Log))
            {
                return QuestActionResult.CannotGiveUp;
            }
            Abandoned++;
            _readyNoted.Remove(questId);
            Notify(QuestNoticeKind.Abandoned, questId, "QUEST ABANDONED", NameOf(def), null);
            _dirty = true;
            return QuestActionResult.Ok;
        }

        /// <summary>和 NPC 说话(城镇里走到身边按空格):进行中的拜访类任务以它为目标的就算完成目标。返回有没有推进任务。</summary>
        public bool Talk(int npcId)
        {
            List<int> changed = QuestTracker.OnTalk(Log, Find, npcId);
            NoteChanged(changed);
            return changed.Count > 0;
        }

        // ------------------------------------------------------------------ 事件(IQuestEvents)

        /// <inheritdoc />
        public void MonsterKilled(int dungeonId, int difficulty, int monsterCode, bool boss)
        {
            if (Log.Active.Count == 0)
            {
                return;
            }
            int enemyType = boss ? QuestRules.EnemyNamed : QuestRules.EnemyNormal;
            List<int> changed = QuestTracker.OnKill(Log, Find, dungeonId, difficulty, monsterCode, enemyType);
            KillsCounted += changed.Count;
            NoteChanged(changed);
            GiveDrops(QuestTracker.RollKillDrops(Log, Find, dungeonId, difficulty, monsterCode, enemyType, Rand100, ItemCount));
        }

        /// <inheritdoc />
        public void MapCleared(int mapId)
        {
            if (Log.Active.Count == 0)
            {
                return;
            }
            NoteChanged(QuestTracker.OnMapClear(Log, Find, mapId));
        }

        /// <inheritdoc />
        public void DungeonCleared(int dungeonId, int difficulty, int clearMilliseconds)
        {
            if (Log.Active.Count == 0)
            {
                return;
            }
            NoteChanged(QuestTracker.OnDungeonClear(Log, Find, dungeonId, difficulty, clearMilliseconds));
            GiveDrops(QuestTracker.RollClearDrops(Log, Find, dungeonId, difficulty, Rand100, ItemCount));
        }

        private void GiveDrops(List<QuestItem> drops)
        {
            foreach (QuestItem drop in drops)
            {
                ItemInfo info = _item(drop.Id);
                if (info == null || drop.Count <= 0)
                {
                    continue;
                }
                if (_hero.AddItem(info, drop.Count))
                {
                    QuestItemsFound += drop.Count;
                    Notify(QuestNoticeKind.ItemFound, 0, "QUEST ITEM", (_itemTexts?.NameOf(info) ?? "ITEM " + drop.Id.ToString(CultureInfo.InvariantCulture)) + " x" + drop.Count.ToString(CultureInfo.InvariantCulture), null);
                }
                else
                {
                    Notify(QuestNoticeKind.BagFull, 0, "BAG FULL", null, null);
                }
            }
            _dirty = true;
            EvaluateReady();
        }

        private void NoteChanged(List<int> changed)
        {
            foreach (int id in changed)
            {
                QuestDef def = Find(id);
                ActiveQuest active = Log.Find(id);
                if (def == null || active == null)
                {
                    continue;
                }
                if (!QuestRules.IsReady(def, active, ItemCount))
                {
                    string progress = ProgressText(def, active);
                    Notify(QuestNoticeKind.Progress, id, "QUEST", NameOf(def) + (progress.Length == 0 ? string.Empty : "  " + progress), null);
                }
            }
            _dirty = true;
            EvaluateReady();
        }

        /// <summary>
        /// 每帧调用一次:背包里的东西变了(拾取、买卖、用掉)就重新判断收集类任务有没有凑齐。
        /// </summary>
        public void Update()
        {
            int signature = _hero.Bag.TotalCount * 31 + _hero.ItemsGained;
            if (signature != _bagSignature)
            {
                _bagSignature = signature;
                _dirty = true;
            }
            if (_dirty)
            {
                EvaluateReady();
            }
        }

        /// <summary>
        /// 重新判断每个进行中的任务:条件满足的,有交任务 NPC 的提示一次"可以交了",没有的自动完成(背包放不下留着)。
        /// </summary>
        private void EvaluateReady()
        {
            _dirty = false;
            var snapshot = new List<ActiveQuest>(Log.Active);
            foreach (ActiveQuest active in snapshot)
            {
                QuestDef def = Find(active.Id);
                if (def == null)
                {
                    continue;
                }
                if (!QuestRules.IsReady(def, active, ItemCount))
                {
                    _readyNoted.Remove(active.Id);
                    continue;
                }
                if (QuestRules.HandInNpc(def) < 0)
                {
                    // 自动完成;背包放不下就留着(只提示第一次),等背包变了(Update 里的签名)或下一次事件再试
                    TurnInCore(active.Id, -1, _readyNoted.Add(active.Id));
                }
                else if (_readyNoted.Add(active.Id))
                {
                    Notify(QuestNoticeKind.Ready, active.Id, "QUEST READY", NameOf(def), null);
                }
            }
        }

        // ------------------------------------------------------------------ 提示

        private string NameOf(QuestDef def)
        {
            return _texts?.NameOf(def) ?? "QUEST " + def.Id.ToString(CultureInfo.InvariantCulture);
        }

        private void Notify(QuestNoticeKind kind, int questId, string title, string detail, string extra)
        {
            if (kind == QuestNoticeKind.Progress)
            {
                // 同一个任务的进度提示合并成最新的一条(连续杀怪不刷屏)
                foreach (QuestNotice old in _notices)
                {
                    if (old.Kind == QuestNoticeKind.Progress && old.QuestId == questId)
                    {
                        old.Update(detail);
                        return;
                    }
                }
            }
            _notices.Add(new QuestNotice(kind, questId, title, detail, extra));
        }

        /// <summary>等着显示的提示条数。</summary>
        public int PendingNotices => _notices.Count;

        /// <summary>取走并清空等着显示的提示(按发生次序)。</summary>
        public List<QuestNotice> TakeNotices()
        {
            var taken = new List<QuestNotice>(_notices);
            _notices.Clear();
            return taken;
        }
    }
}
