using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网副本选择与进入的规则(r18-dungeonflow):
    /// <list type="bullet">
    /// <item>许可表档位 → 选择屏最高可选难度(<b>【推断】</b>:将服务端档位视为"1 起的已通关难度档",故可选上限为 <c>clamp(tier, 0, cap)</c>,
    /// 缺条目或未收到表时为 0〔只可选普通〕,不超过离线 <see cref="DungeonClears.HighestSelectable"/> 同档上限;多值语义未在真服验证)。</item>
    /// <item>难度 0..3 映射到进副本命令的模式 0..3(最大 4)。</item>
    /// <item>入口区不是进副本的硬前置(E-433 更正 E-420):只将区种类名记入日志,绝不拦截或先发换区命令。</item>
    /// </list>
    /// </summary>
    public static class OnlineDungeonPermissionRules
    {
        /// <summary>联网选副本命令允许的最大模式字节(与 <c>OnlineDungeonEntry.MaxMode</c> 一致)。</summary>
        public const int MaxMode = DungeonClears.Difficulties - 1;

        /// <summary>
        /// 【推断】许可表档位换算成选择屏可选最高难度(0 到 <see cref="DungeonSelectScreen.MaxDifficultyIndex"/>):
        /// 缺条目(0)或非正数 → 0(普通);否则夹在 [0, 3](没有难度 3 时上限为 2)。
        /// </summary>
        public static int DifficultyLimitForTier(int tier, bool hasUltimate = true)
        {
            if (tier <= 0)
            {
                return 0;
            }
            int cap = hasUltimate ? DungeonSelectScreen.MaxDifficultyIndex : DungeonSelectScreen.MaxDifficultyIndex - 1;
            return tier >= cap ? cap : tier;
        }

        /// <summary>选择屏难度编号(0 到 3)转成选副本命令(CMD 16)的模式字节(0 到 4)。</summary>
        public static byte DifficultyToMode(int difficulty)
        {
            if (difficulty <= 0)
            {
                return 0;
            }
            return difficulty >= MaxMode ? (byte)MaxMode : (byte)difficulty;
        }

        /// <summary>城镇区域图册条目的种类名(用于脱敏日志,未知时返回 <c>unknown</c>;不输出数字编号)。</summary>
        public static string AreaKindToken(TownAreaInfo areaInfo)
        {
            return areaInfo != null ? areaInfo.Kind.ToString() : "unknown";
        }
    }

    /// <summary>
    /// 联网副本许可表的 Play 层只读/可增量合并状态(不引用 Dnf.Net):
    /// 保存进城时的初始快照与会话期间后续推来的单条/整表 (0,5) 合并结果(后到覆盖先到),以及本局内被服务端 CMD 16 拒绝过的副本记录。
    /// 没有条目 = 没收到过(当 0),不等于"不许可"。"许可过期"概念不存在。
    /// </summary>
    public sealed class OnlineDungeonPermissionTable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<int, byte> _tiers = new Dictionary<int, byte>();
        private readonly Dictionary<int, byte?> _rejected = new Dictionary<int, byte?>();

        /// <summary>是否至少收到过一次 (0,5) 许可帧(包含空表或仅含坏帧的快照)。</summary>
        public bool HasSnapshot { get; private set; }

        /// <summary>成功合并进来的 (0,5) 通知数。</summary>
        public int NoticeCount { get; private set; }

        /// <summary>载荷读不通而丢弃的 (0,5) 通知数。</summary>
        public int MalformedNoticeCount { get; private set; }

        /// <summary>当前表里记录的副本条目数。</summary>
        public int EntryCount
        {
            get
            {
                lock (_sync)
                {
                    return _tiers.Count;
                }
            }
        }

        /// <summary>每次表或拒绝状态变化递增,供选择屏按需刷新。</summary>
        public int Version { get; private set; }

        /// <summary>用进城时收集到的初始快照重置表内容。</summary>
        public void LoadInitial(IEnumerable<KeyValuePair<int, byte>> entries, bool hasSnapshot, int noticeCount = 0, int malformedCount = 0)
        {
            lock (_sync)
            {
                _tiers.Clear();
                _rejected.Clear();
                if (entries != null)
                {
                    foreach (KeyValuePair<int, byte> kv in entries)
                    {
                        if (kv.Key >= 0)
                        {
                            _tiers[kv.Key] = kv.Value;
                        }
                    }
                }
                HasSnapshot = hasSnapshot;
                NoticeCount = noticeCount > 0 ? noticeCount : 0;
                MalformedNoticeCount = malformedCount > 0 ? malformedCount : 0;
                Version++;
            }
        }

        /// <summary>合入一条新的 (0,5) 通知:按到达顺序逐条 upsert(后到覆盖先到),并解除对应副本的本局拒绝标记。</summary>
        public void ApplyUpdate(IEnumerable<KeyValuePair<int, byte>> entries)
        {
            lock (_sync)
            {
                if (entries != null)
                {
                    foreach (KeyValuePair<int, byte> kv in entries)
                    {
                        if (kv.Key >= 0)
                        {
                            _tiers[kv.Key] = kv.Value;
                            _rejected.Remove(kv.Key);
                        }
                    }
                }
                HasSnapshot = true;
                NoticeCount++;
                Version++;
            }
        }

        /// <summary>收到一条损坏的 (0,5) 帧:只记坏帧计数,保留已有表不变。</summary>
        public void RecordMalformedNotice()
        {
            lock (_sync)
            {
                HasSnapshot = true;
                MalformedNoticeCount++;
            }
        }

        /// <summary>查某副本的许可档位;表里没有时返回 0(表示没收到过,不是不许可)。</summary>
        public byte GetTier(int dungeonId)
        {
            lock (_sync)
            {
                return _tiers.TryGetValue(dungeonId, out byte tier) ? tier : (byte)0;
            }
        }

        /// <summary>表里是否有该副本的条目。</summary>
        public bool TryGetTier(int dungeonId, out byte tier)
        {
            lock (_sync)
            {
                return _tiers.TryGetValue(dungeonId, out tier);
            }
        }

        /// <summary>按规则给出该副本当前可选最高难度(0..3)。</summary>
        public int DifficultyLimit(int dungeonId, bool hasUltimate = true)
        {
            return OnlineDungeonPermissionRules.DifficultyLimitForTier(GetTier(dungeonId), hasUltimate);
        }

        /// <summary>本局内记住被服务端 CMD 16 拒绝过的副本,在选择屏灰显并阻止再次无效点击。</summary>
        public void MarkServerRejected(int dungeonId, byte? rejectionCode = null)
        {
            if (dungeonId < 0)
            {
                return;
            }
            lock (_sync)
            {
                _rejected[dungeonId] = rejectionCode;
                Version++;
            }
        }

        /// <summary>该副本本局内是否刚被服务端拒绝过。</summary>
        public bool IsServerRejected(int dungeonId, out byte? rejectionCode)
        {
            lock (_sync)
            {
                return _rejected.TryGetValue(dungeonId, out rejectionCode);
            }
        }

        /// <summary>清掉所有本局拒绝标记。</summary>
        public void ClearServerRejections()
        {
            lock (_sync)
            {
                if (_rejected.Count > 0)
                {
                    _rejected.Clear();
                    Version++;
                }
            }
        }
    }

    /// <summary>副本选择条目被灰显/禁止进入的原因分类。</summary>
    public enum DungeonEntryBlockKind
    {
        None = 0,
        ServerRejected = 1,
        MissingDungeonId = 2,
        MissingLocalData = 3,
        Custom = 4,
    }

    /// <summary>单个副本条目在联网选择屏上的可进入状态。</summary>
    public readonly struct DungeonEntryStatus
    {
        public static readonly DungeonEntryStatus Allowed = new DungeonEntryStatus(DungeonEntryBlockKind.None, null, null);

        public DungeonEntryStatus(DungeonEntryBlockKind kind, byte? code = null, string customMessage = null)
        {
            Kind = kind;
            Code = code;
            CustomMessage = customMessage;
        }

        public DungeonEntryBlockKind Kind { get; }
        public byte? Code { get; }
        public string CustomMessage { get; }
        public bool Disabled => Kind != DungeonEntryBlockKind.None;

        /// <summary>返回给玩家看的脱敏提示文案(不含副本编号与路径)。</summary>
        public string ReasonText(bool hasFonts)
        {
            if (!Disabled)
            {
                return string.Empty;
            }
            if (!string.IsNullOrEmpty(CustomMessage))
            {
                return CustomMessage;
            }
            switch (Kind)
            {
                case DungeonEntryBlockKind.ServerRejected:
                    return hasFonts
                        ? (Code.HasValue ? "服务端暂不允许进入该副本 (拒绝码 " + Code.Value.ToString(CultureInfo.InvariantCulture) + ")" : "服务端暂不允许进入该副本")
                        : (Code.HasValue ? "SERVER REJECTED (" + Code.Value.ToString(CultureInfo.InvariantCulture) + ")" : "SERVER REJECTED");
                case DungeonEntryBlockKind.MissingDungeonId:
                    return hasFonts ? "缺少副本编号映射，暂不可进入" : "MISSING DUNGEON ID";
                case DungeonEntryBlockKind.MissingLocalData:
                    return hasFonts ? "本地缺少该副本数据，暂不可进入" : "MISSING LOCAL DATA";
                default:
                    return hasFonts ? "当前不可进入该副本" : "DUNGEON UNAVAILABLE";
            }
        }
    }

    /// <summary>联网副本选择屏与进出副本过程里的状态提示种类。</summary>
    public enum DungeonSelectNoticeKind
    {
        None = 0,
        Entering = 1,
        GivingUp = 2,
        Rejected = 3,
        Timeout = 4,
        RoomFailed = 5,
        Disconnected = 6,
        Aborted = 7,
        Failed = 8,
        Info = 9,
    }

    /// <summary>副本选择/进副本/回城期间的非阻塞提示状态。</summary>
    public sealed class DungeonSelectNotice
    {
        public DungeonSelectNotice(DungeonSelectNoticeKind kind, string customMessage = null, long expiresAtMs = 0)
        {
            Kind = kind;
            CustomMessage = customMessage;
            ExpiresAtMs = expiresAtMs;
        }

        public DungeonSelectNoticeKind Kind { get; }
        public string CustomMessage { get; }
        public long ExpiresAtMs { get; }

        public ShellTone Tone
        {
            get
            {
                switch (Kind)
                {
                    case DungeonSelectNoticeKind.Entering:
                    case DungeonSelectNoticeKind.GivingUp:
                        return ShellTone.Wait;
                    case DungeonSelectNoticeKind.Info:
                        return ShellTone.Info;
                    default:
                        return ShellTone.Error;
                }
            }
        }

        public string Message(bool hasFonts)
        {
            if (!string.IsNullOrEmpty(CustomMessage))
            {
                return CustomMessage;
            }
            switch (Kind)
            {
                case DungeonSelectNoticeKind.Entering:
                    return hasFonts ? "正在请求进入联网副本…" : "ENTERING ONLINE DUNGEON...";
                case DungeonSelectNoticeKind.GivingUp:
                    return hasFonts ? "正在放弃副本并返回城镇…" : "RETURNING TO TOWN...";
                case DungeonSelectNoticeKind.Rejected:
                    return hasFonts ? "服务端拒绝进入该副本" : "ENTRY REJECTED BY SERVER";
                case DungeonSelectNoticeKind.Timeout:
                    return hasFonts ? "等待服务端响应超时" : "SERVER RESPONSE TIMED OUT";
                case DungeonSelectNoticeKind.RoomFailed:
                    return hasFonts ? "无法生成联网副本房间" : "FAILED TO BUILD ONLINE ROOM";
                case DungeonSelectNoticeKind.Disconnected:
                    return hasFonts ? "与频道服务器的连接已断开" : "CHANNEL CONNECTION LOST";
                case DungeonSelectNoticeKind.Aborted:
                    return hasFonts ? "联网副本已中止，已返回城镇" : "ONLINE RUN ABORTED";
                case DungeonSelectNoticeKind.Failed:
                    return hasFonts ? "进入联网副本失败" : "ONLINE DUNGEON ENTRY FAILED";
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// 联网副本选择屏策略(把 <see cref="OnlineDungeonPermissionTable"/>、本地副本编号解析、<see cref="UltimateDungeonList"/> 与文字表适配成
    /// <see cref="DungeonSelectScreen"/> 认得的中性委托)。封装在 Play 层,使宿主无需直接引用 <c>Dnf.Game</c>(避免 Unity CS0012)。
    /// </summary>
    public sealed class OnlineDungeonSelectPolicy
    {
        private readonly OnlineDungeonPermissionTable _table;
        private readonly Func<DungeonEntry, int> _resolveId;
        private readonly Func<int, bool> _hasUltimate;
        private readonly Func<DungeonEntry, bool> _hasLocalData;
        private readonly Func<DungeonEntry, string> _resolveEntryName;

        private OnlineDungeonSelectPolicy(
            OnlineDungeonPermissionTable table,
            Func<DungeonEntry, int> resolveId,
            Func<int, bool> hasUltimate,
            Func<DungeonEntry, bool> hasLocalData,
            Func<DungeonEntry, string> resolveEntryName,
            string regionDisplayName)
        {
            _table = table;
            _resolveId = resolveId;
            _hasUltimate = hasUltimate;
            _hasLocalData = hasLocalData;
            _resolveEntryName = resolveEntryName;
            RegionDisplayName = regionDisplayName;
        }

        public OnlineDungeonPermissionTable Table => _table;
        public string RegionDisplayName { get; }

        /// <summary>为选择屏与控制器建立完整策略;所有外部资源均可为空(测试用合成夹具)。</summary>
        public static OnlineDungeonSelectPolicy Create(
            OnlineDungeonPermissionTable table,
            DnfAssets assets = null,
            Func<DungeonEntry, int> idResolver = null,
            Func<DungeonEntry, bool> hasLocalData = null,
            WorldTexts texts = null,
            string worldMapPath = null,
            Func<int, bool> hasUltimate = null)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            UltimateDungeonList ult = assets == null ? UltimateDungeonList.Empty : UltimateDungeonList.Parse(assets.ReadScript(UltimateDungeonList.Path));
            var idCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var localCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int ResolveId(DungeonEntry entry)
            {
                if (entry == null)
                {
                    return -1;
                }
                if (entry.Card != null && entry.Card.DungeonId >= 0)
                {
                    return entry.Card.DungeonId;
                }
                if (idResolver != null)
                {
                    return idResolver(entry);
                }
                if (assets == null || string.IsNullOrEmpty(entry.Path))
                {
                    return -1;
                }
                if (!idCache.TryGetValue(entry.Path, out int id))
                {
                    id = DungeonRoomPool.DungeonIdOf(assets, entry.Path);
                    idCache[entry.Path] = id;
                }
                return id;
            }

            bool CheckLocalData(DungeonEntry entry)
            {
                if (entry == null)
                {
                    return false;
                }
                if (hasLocalData != null)
                {
                    return hasLocalData(entry);
                }
                if (string.IsNullOrEmpty(entry.Path))
                {
                    return false;
                }
                if (assets == null)
                {
                    return true;
                }
                if (!localCache.TryGetValue(entry.Path, out bool exists))
                {
                    exists = assets.ReadScript(entry.Path) != null;
                    localCache[entry.Path] = exists;
                }
                return exists;
            }

            string ResolveName(DungeonEntry entry)
            {
                if (entry == null || texts == null || assets == null || string.IsNullOrEmpty(entry.Path))
                {
                    return null;
                }
                if (!nameCache.TryGetValue(entry.Path, out string name))
                {
                    name = ResolveScriptDisplayName(assets.ReadScript(entry.Path), StrLibrary.KindDungeon, texts);
                    nameCache[entry.Path] = name;
                }
                return name;
            }

            Func<int, bool> ultimateCheck = hasUltimate ?? (assets != null ? (Func<int, bool>)ult.Contains : _ => true);
            string regionName = null;
            if (texts != null && assets != null && !string.IsNullOrEmpty(worldMapPath))
            {
                regionName = ResolveScriptDisplayName(assets.ReadScript(worldMapPath), StrLibrary.KindWorldMap, texts);
            }
            return new OnlineDungeonSelectPolicy(table, ResolveId, ultimateCheck, CheckLocalData, ResolveName, regionName);
        }

        /// <summary>读脚本里的 <c>[name]</c>:支持"种类 + 键"与普通字符串键两种写法。</summary>
        public static string ResolveScriptDisplayName(ScriptLine[] doc, int defaultKind, WorldTexts texts)
        {
            if (doc == null || texts == null)
            {
                return null;
            }
            string keyed = texts.ScriptName(doc);
            if (!string.IsNullOrEmpty(keyed))
            {
                return keyed;
            }
            for (int i = 0; i < doc.Length; i++)
            {
                ScriptLine line = doc[i];
                if (line == null || !line.TagIs("[name]"))
                {
                    continue;
                }
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text))
                    {
                        string text = texts.Text(defaultKind, v.Text);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return text;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>解析条目的副本编号(0..65535);解析不到返回 -1。</summary>
        public int ResolveDungeonId(DungeonEntry entry) => _resolveId(entry);

        /// <summary>给定副本条目的联网可选最高难度(0..3)。</summary>
        public int DifficultyLimit(DungeonEntry entry)
        {
            int id = _resolveId(entry);
            if (id < 0 || id > ushort.MaxValue)
            {
                return 0;
            }
            return _table.DifficultyLimit(id, _hasUltimate(id));
        }

        /// <summary>给定副本条目是否可用或该灰显及原因。</summary>
        public DungeonEntryStatus EntryStatus(DungeonEntry entry)
        {
            if (entry == null)
            {
                return new DungeonEntryStatus(DungeonEntryBlockKind.MissingDungeonId);
            }
            int id = _resolveId(entry);
            if (id < 0 || id > ushort.MaxValue)
            {
                return new DungeonEntryStatus(DungeonEntryBlockKind.MissingDungeonId);
            }
            if (!_hasLocalData(entry))
            {
                return new DungeonEntryStatus(DungeonEntryBlockKind.MissingLocalData);
            }
            if (_table.IsServerRejected(id, out byte? code))
            {
                return new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, code);
            }
            return DungeonEntryStatus.Allowed;
        }

        /// <summary>副本条目的本地化中文名(取不到返回 null)。</summary>
        public string EntryDisplayName(DungeonEntry entry) => _resolveEntryName(entry);
    }

    // ------------------------------------------------------------------------------------------------
    // 给 r18-settleui 的中性结算交接类型(不引用 Dnf.Net)
    // ------------------------------------------------------------------------------------------------

    /// <summary>结算载荷在联网战斗状态机里的解析状态(中性镜像)。</summary>
    public enum OnlineSettlementParseState
    {
        NotApplicable = 0,
        Succeeded = 1,
        Failed = 2,
    }

    /// <summary>通关结果通知里单名队员的个人最佳用时。</summary>
    public readonly struct OnlineSettlementBestTime
    {
        public OnlineSettlementBestTime(ushort uniqueId, int bestTime, bool isNewRecord)
        {
            UniqueId = uniqueId;
            BestTime = bestTime;
            IsNewRecord = isNewRecord;
        }

        public ushort UniqueId { get; }
        public int BestTime { get; }
        public bool IsNewRecord { get; }
    }

    /// <summary>通关结果通知(0x22)的只读中性数据。</summary>
    public sealed class OnlineSettlementResultData
    {
        public OnlineSettlementResultData(
            byte rankValue,
            int clearTime,
            sbyte timeBonus,
            sbyte baseScore,
            byte questMazeFlag,
            bool hasBestTimeBlock,
            IReadOnlyList<OnlineSettlementBestTime> entries)
        {
            RankValue = rankValue;
            ClearTime = clearTime;
            TimeBonus = timeBonus;
            BaseScore = baseScore;
            QuestMazeFlag = questMazeFlag;
            HasBestTimeBlock = hasBestTimeBlock;
            Entries = entries ?? Array.Empty<OnlineSettlementBestTime>();
        }

        public byte RankValue { get; }
        public int ClearTime { get; }
        public sbyte TimeBonus { get; }
        public sbyte BaseScore { get; }
        public byte QuestMazeFlag { get; }
        public bool IsQuestMaze => QuestMazeFlag == 1;
        public bool HasBestTimeBlock { get; }
        public IReadOnlyList<OnlineSettlementBestTime> Entries { get; }
    }

    /// <summary>通关奖励通知(0x23)里的任务清图物品条目。</summary>
    public readonly struct OnlineSettlementItemData
    {
        public OnlineSettlementItemData(ushort slot, int itemId, int data, ushort durability, byte amplifyType, ushort amplifyValue)
        {
            Slot = slot;
            ItemId = itemId;
            Data = data;
            Durability = durability;
            AmplifyType = amplifyType;
            AmplifyValue = amplifyValue;
        }

        public ushort Slot { get; }
        public int ItemId { get; }
        public int Data { get; }
        public ushort Durability { get; }
        public byte AmplifyType { get; }
        public ushort AmplifyValue { get; }
    }

    /// <summary>通关奖励通知(0x23)里单个队员槽位的奖励信息。</summary>
    public readonly struct OnlineSettlementRewardSlotData
    {
        public OnlineSettlementRewardSlotData(byte status, int reserved, int value, int itemId, int itemData)
        {
            Status = status;
            Reserved = reserved;
            Value = value;
            ItemId = itemId;
            ItemData = itemData;
        }

        public byte Status { get; }
        public int Reserved { get; }
        public int Value { get; }
        public bool HasItem => Status == 2;
        public int ItemId { get; }
        public int ItemData { get; }
    }

    /// <summary>通关奖励通知(0x23)的只读中性数据。</summary>
    public sealed class OnlineSettlementRewardData
    {
        public OnlineSettlementRewardData(
            IReadOnlyList<int> expValues,
            byte expKind,
            IReadOnlyList<OnlineSettlementItemData> questItems,
            IReadOnlyList<OnlineSettlementRewardSlotData> rewards,
            int receiverValue,
            IReadOnlyList<byte> reservedBytes,
            IReadOnlyList<OnlineSettlementRewardSlotData> premiumRewards,
            int percent,
            bool isReturnUserParty,
            bool isEventCharacParty,
            bool hasDeadUserPenalty)
        {
            ExpValues = expValues ?? Array.Empty<int>();
            ExpKind = expKind;
            QuestItems = questItems ?? Array.Empty<OnlineSettlementItemData>();
            Rewards = rewards ?? Array.Empty<OnlineSettlementRewardSlotData>();
            ReceiverValue = receiverValue;
            ReservedBytes = reservedBytes ?? Array.Empty<byte>();
            PremiumRewards = premiumRewards ?? Array.Empty<OnlineSettlementRewardSlotData>();
            Percent = percent;
            IsReturnUserParty = isReturnUserParty;
            IsEventCharacParty = isEventCharacParty;
            HasDeadUserPenalty = hasDeadUserPenalty;
        }

        public IReadOnlyList<int> ExpValues { get; }
        public int BaseExp => ExpValues.Count > 0 ? ExpValues[0] : 0;
        public int RankBonusExp => ExpValues.Count > 1 ? ExpValues[1] : 0;
        public byte ExpKind { get; }
        public IReadOnlyList<OnlineSettlementItemData> QuestItems { get; }
        public IReadOnlyList<OnlineSettlementRewardSlotData> Rewards { get; }
        public int ReceiverValue { get; }
        public IReadOnlyList<byte> ReservedBytes { get; }
        public IReadOnlyList<OnlineSettlementRewardSlotData> PremiumRewards { get; }
        public int Percent { get; }
        public bool IsReturnUserParty { get; }
        public bool IsEventCharacParty { get; }
        public bool HasDeadUserPenalty { get; }
    }

    /// <summary>交接给 <c>r18-settleui</c> 的完整一局联网结算快照(Play 层中性类型)。</summary>
    public sealed class OnlineDungeonSettlementData
    {
        public OnlineDungeonSettlementData(
            int dungeonId,
            string dungeonPath,
            int difficulty,
            int reportedKills,
            int droppedKills,
            int roomsEntered,
            OnlineSettlementParseState resultParseState,
            OnlineSettlementResultData result,
            OnlineSettlementParseState rewardParseState,
            OnlineSettlementRewardData reward)
        {
            DungeonId = dungeonId;
            DungeonPath = dungeonPath ?? string.Empty;
            Difficulty = difficulty;
            ReportedKills = reportedKills;
            DroppedKills = droppedKills;
            RoomsEntered = roomsEntered;
            ResultParseState = resultParseState;
            Result = result;
            RewardParseState = rewardParseState;
            Reward = reward;
        }

        public int DungeonId { get; }
        public string DungeonPath { get; }
        public int Difficulty { get; }
        public int ReportedKills { get; }
        public int DroppedKills { get; }
        public int RoomsEntered { get; }
        public OnlineSettlementParseState ResultParseState { get; }
        public OnlineSettlementResultData Result { get; }
        public OnlineSettlementParseState RewardParseState { get; }
        public OnlineSettlementRewardData Reward { get; }
    }

    /// <summary>
    /// 联网进副本阶段的脱敏日志行格式(prefix <see cref="Prefix"/>):
    /// 只含状态名、枚举名与计数,不含副本/怪物编号、路径、坐标、角色名、IP 或端口。
    /// </summary>
    public static class OnlineDungeonLog
    {
        public const string Prefix = "DNF_ONLINE stage=dungeon";

        public static string Select(string outcome, int difficulty, int maxDifficulty, string reason = null)
        {
            string line = Prefix + " event=select outcome=" + Token(outcome)
                + " difficulty=" + N(difficulty)
                + " max=" + N(maxDifficulty);
            if (!string.IsNullOrEmpty(reason))
            {
                line += " reason=" + Token(reason);
            }
            return line;
        }

        public static string Enter(string areaKind, bool isEntranceArea, int difficulty)
        {
            return Prefix + " event=enter areakind=" + Token(areaKind)
                + " entrance=" + (isEntranceArea ? "true" : "false")
                + " difficulty=" + N(difficulty);
        }

        public static string Room(string status, bool multiRoom, int spawned, int expected, int rooms = 1)
        {
            return Prefix + " event=room status=" + Token(status)
                + " multi=" + (multiRoom ? "on" : "off")
                + " spawned=" + N(spawned)
                + " expected=" + N(expected)
                + " rooms=" + N(rooms);
        }

        public static string Leave(string reason, int reported, int dropped, int rooms, string resultParse = null, string rewardParse = null)
        {
            string line = Prefix + " event=leave reason=" + Token(reason)
                + " reported=" + N(reported)
                + " dropped=" + N(dropped)
                + " rooms=" + N(rooms);
            if (!string.IsNullOrEmpty(resultParse))
            {
                line += " result=" + Token(resultParse);
            }
            if (!string.IsNullOrEmpty(rewardParse))
            {
                line += " reward=" + Token(rewardParse);
            }
            return line;
        }

        public static string ReturnArea(string areaMatch)
        {
            return Prefix + " event=return area=" + Token(areaMatch);
        }

        public static string Fail(string reason, string step = null, byte? code = null)
        {
            string line = Prefix + " event=fail reason=" + Token(reason);
            if (!string.IsNullOrEmpty(step))
            {
                line += " step=" + Token(step);
            }
            if (code.HasValue)
            {
                line += " code=" + N(code.Value);
            }
            return line;
        }

        public static string PermissionUpdate(int entries, int malformed)
        {
            return Prefix + " event=perms entries=" + N(entries) + " malformed=" + N(malformed);
        }

        private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Token(string value) => string.IsNullOrEmpty(value) ? "unknown" : value;
    }
}
