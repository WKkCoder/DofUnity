using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play
{
    /// <summary>结算页单个字段的读码/证据把握程度;标为 <see cref="Unknown"/> 的字段绝不画到屏幕上。</summary>
    public enum SettlementFieldConfidence
    {
        /// <summary>未知(客户端/服务端语义未读通):页面不显示。</summary>
        Unknown = 0,

        /// <summary>推断(按函数名/用法推断,未在真服交叉核对):按规则展示并标注。</summary>
        Inferred = 1,

        /// <summary>观察/已知(静态读码确证或本地确定算法):正常展示。</summary>
        Observed = 2,
    }

    /// <summary>结算页单个字段的取值来源。</summary>
    public enum SettlementFieldSource
    {
        None = 0,
        Server = 1,
        Local = 2,
        Placeholder = 3,
    }

    /// <summary>带来源与把握程度标记的结算中性字段。</summary>
    public readonly struct OnlineSettlementField<T>
    {
        public OnlineSettlementField(bool hasValue, T value, SettlementFieldConfidence confidence, SettlementFieldSource source)
        {
            HasValue = hasValue;
            Value = value;
            Confidence = confidence;
            Source = source;
        }

        public bool HasValue { get; }
        public T Value { get; }
        public SettlementFieldConfidence Confidence { get; }
        public SettlementFieldSource Source { get; }

        /// <summary>仅当有值且把握程度不是 <see cref="SettlementFieldConfidence.Unknown"/> 时才允许在结算页绘制。</summary>
        public bool ShouldDisplay => HasValue && Confidence != SettlementFieldConfidence.Unknown;

        public static OnlineSettlementField<T> Missing() =>
            new OnlineSettlementField<T>(false, default(T), SettlementFieldConfidence.Unknown, SettlementFieldSource.None);

        public static OnlineSettlementField<T> Observed(T value, SettlementFieldSource source) =>
            new OnlineSettlementField<T>(true, value, SettlementFieldConfidence.Observed, source);

        public static OnlineSettlementField<T> Inferred(T value, SettlementFieldSource source) =>
            new OnlineSettlementField<T>(true, value, SettlementFieldConfidence.Inferred, source);

        public static OnlineSettlementField<T> Unknown(T value, SettlementFieldSource source) =>
            new OnlineSettlementField<T>(true, value, SettlementFieldConfidence.Unknown, source);
    }

    /// <summary>结算页掉落/任务清图物品条目(按物品号查本地数据取名,查不到显示占位,日志与画面不漏原始实服编号)。</summary>
    public readonly struct OnlineSettlementDropItem
    {
        public OnlineSettlementDropItem(int itemId, int count, string name, bool isQuestItem)
        {
            ItemId = itemId;
            Count = count > 0 ? count : 1;
            Name = string.IsNullOrEmpty(name) ? null : name;
            IsQuestItem = isQuestItem;
        }

        public int ItemId { get; }
        public int Count { get; }
        public string Name { get; }
        public bool IsQuestItem { get; }
        public bool NameResolved => !string.IsNullOrEmpty(Name);

        public string DisplayLabel(bool hasFonts)
        {
            string title = NameResolved ? Name : (hasFonts ? "未知道具" : "UNKNOWN ITEM");
            if (Count > 1)
            {
                return title + " x" + Count.ToString(CultureInfo.InvariantCulture);
            }
            return title;
        }
    }

    /// <summary>0x23 经验块里单个分项的展示记录(绝不把 16 项相加当“总获得经验”,下标 2 标为 Unknown 永不显示)。</summary>
    public readonly struct OnlineSettlementExpLine
    {
        public OnlineSettlementExpLine(int index, string key, int amount, SettlementFieldConfidence confidence)
        {
            Index = index;
            Key = key ?? string.Empty;
            Amount = amount;
            Confidence = confidence;
        }

        public int Index { get; }
        public string Key { get; }
        public int Amount { get; }
        public SettlementFieldConfidence Confidence { get; }
        public bool ShouldDisplay => Confidence != SettlementFieldConfidence.Unknown && Amount > 0;

        public string DisplayLabel(bool hasFonts)
        {
            string title = TitleOf(Key, hasFonts);
            return title + " +" + Amount.ToString(CultureInfo.InvariantCulture);
        }

        private static string TitleOf(string key, bool hasFonts)
        {
            switch (key)
            {
                case "avatar": return hasFonts ? "装扮加成经验" : "AVATAR EXP";
                case "event": return hasFonts ? "活动加成经验" : "EVENT EXP";
                case "item": return hasFonts ? "道具加成经验" : "ITEM EXP";
                case "privilege": return hasFonts ? "特权加成经验" : "PRIVILEGE EXP";
                case "pcroom_party": return hasFonts ? "网吧队友加成" : "PCROOM EXP";
                case "channel": return hasFonts ? "频道加成经验" : "CHANNEL EXP";
                case "upper_member": return hasFonts ? "上位队员经验" : "UPPER EXP";
                case "creature": return hasFonts ? "宠物加成经验" : "CREATURE EXP";
                case "return_user": return hasFonts ? "回归队伍加成" : "RETURN EXP";
                case "new_char": return hasFonts ? "新角色活动加成" : "NEWCHAR EXP";
                case "manage": return hasFonts ? "管理加成经验" : "MANAGE EXP";
                case "growth_power": return hasFonts ? "成长力经验" : "GROWTH EXP";
                case "premium_extra": return hasFonts ? "特权附加经验" : "PREMIUM EXP";
                default: return hasFonts ? "加成经验" : "BONUS EXP";
            }
        }
    }

    /// <summary>构建联网结算页视图时传入的本地对照上下文(用于本地评分字母计算与服务端差异对账)。</summary>
    public sealed class OnlineSettlementLocalContext
    {
        public RunStats? LocalStats { get; set; }
        public DungeonResult LocalResult { get; set; }
        public long LocalGoldEarned { get; set; }
        public int LocalItemsPicked { get; set; }
        public long LocalExpEarned { get; set; }
        public ushort? SelfUniqueId { get; set; }
        public int? PartySlot { get; set; }
    }

    /// <summary>联网结算页面的状态/错误提示种类。</summary>
    public enum OnlineSettlementNoticeKind
    {
        None = 0,
        Advancing = 1,
        CardSelectSkipped = 2,
        AdvanceNoReply = 3,
        WaitingReturn = 4,
        SettlementTimeout = 5,
        EplpRejectedNotLeader = 6,
        EplpRejectedSecretShopOpen = 7,
        EplpRejectedInvalidCommand = 8,
        EplpRejectedOther = 9,
        EplpTimeout = 10,
        ServerReturnedToTown = 11,
        SendFailed = 12,
        Disconnected = 13,
    }

    /// <summary>联网结算阶段的非阻塞提示状态。</summary>
    public sealed class OnlineSettlementNotice
    {
        public OnlineSettlementNotice(OnlineSettlementNoticeKind kind, byte? code = null, string customMessage = null)
        {
            Kind = kind;
            Code = code;
            CustomMessage = customMessage;
        }

        public OnlineSettlementNoticeKind Kind { get; }
        public byte? Code { get; }
        public string CustomMessage { get; }

        public ShellTone Tone
        {
            get
            {
                switch (Kind)
                {
                    case OnlineSettlementNoticeKind.Advancing:
                    case OnlineSettlementNoticeKind.WaitingReturn:
                        return ShellTone.Wait;
                    case OnlineSettlementNoticeKind.CardSelectSkipped:
                    case OnlineSettlementNoticeKind.ServerReturnedToTown:
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
                case OnlineSettlementNoticeKind.Advancing:
                    return hasFonts ? "正在推进结算状态，请稍候…" : "ADVANCING SETTLEMENT...";
                case OnlineSettlementNoticeKind.CardSelectSkipped:
                    return hasFonts ? "无翻牌资格，等待服务端推进结算菜单" : "NO CARD RIGHT; WAITING SERVER MENU";
                case OnlineSettlementNoticeKind.AdvanceNoReply:
                    return hasFonts ? "结算推进未收到预期回应，可安全返回城镇" : "SETTLE ADVANCE NO REPLY; RETURN AVAILABLE";
                case OnlineSettlementNoticeKind.WaitingReturn:
                    return hasFonts ? "已发送结算选择，正在等待服务端回城…" : "WAITING SERVER RETURN...";
                case OnlineSettlementNoticeKind.SettlementTimeout:
                    return hasFonts ? "等待结算通知超时，可选择返回城镇" : "SETTLEMENT NOTICE TIMED OUT; RETURN TO TOWN";
                case OnlineSettlementNoticeKind.EplpRejectedNotLeader:
                    return hasFonts ? "仅队长可操作结算菜单，请选择返回城镇" : "MENU REJECTED: NOT PARTY LEADER (8)";
                case OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen:
                    return hasFonts ? "神秘商店处于打开状态，请先关闭或返回城镇" : "MENU REJECTED: SECRET SHOP OPEN (0x85)";
                case OnlineSettlementNoticeKind.EplpRejectedInvalidCommand:
                    return hasFonts ? "服务端判定结算菜单命令无效，已回退安全选项" : "MENU REJECTED: INVALID COMMAND (1)";
                case OnlineSettlementNoticeKind.EplpRejectedOther:
                    return hasFonts
                        ? (Code.HasValue ? "结算菜单请求被服务端拒绝 (错误码 " + Code.Value.ToString(CultureInfo.InvariantCulture) + ")" : "结算菜单请求被服务端拒绝")
                        : (Code.HasValue ? "MENU REJECTED BY SERVER (" + Code.Value.ToString(CultureInfo.InvariantCulture) + ")" : "MENU REJECTED BY SERVER");
                case OnlineSettlementNoticeKind.EplpTimeout:
                    return hasFonts ? "等待服务端结算响应超时，正在安全返回城镇" : "SETTLEMENT REPLY TIMED OUT; RETURNING";
                case OnlineSettlementNoticeKind.ServerReturnedToTown:
                    return hasFonts ? "服务端已将角色送回城镇" : "RETURNED TO TOWN BY SERVER";
                case OnlineSettlementNoticeKind.SendFailed:
                    return hasFonts ? "结算命令发送失败，正在安全收尾" : "SETTLEMENT SEND FAILED";
                case OnlineSettlementNoticeKind.Disconnected:
                    return hasFonts ? "与频道服务器的连接已断开" : "CHANNEL CONNECTION LOST";
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>联网结算页的只读中性汇总视图(每个字段均带来源与把握程度标记,未知字段绝不展示)。</summary>
    public sealed class OnlineSettlementSummary
    {
        public OnlineSettlementSummary(
            int dungeonId,
            string dungeonPath,
            int difficulty,
            int roomsEntered,
            bool isTimedOut,
            OnlineSettlementParseState resultParseState,
            OnlineSettlementParseState rewardParseState,
            bool hasRank,
            int rankFrame,
            string rankLetter,
            SettlementFieldSource rankSource,
            OnlineSettlementField<byte> serverRankValue,
            OnlineSettlementField<int> clearTimeMs,
            OnlineSettlementField<int> personalBestTimeMs,
            OnlineSettlementField<bool> isNewRecord,
            OnlineSettlementField<bool> isQuestMaze,
            OnlineSettlementField<int> kills,
            int droppedKills,
            OnlineSettlementField<int> baseExp,
            OnlineSettlementField<int> rankBonusExp,
            IReadOnlyList<OnlineSettlementExpLine> namedBonusExpLines,
            OnlineSettlementField<int> rewardGold,
            IReadOnlyList<OnlineSettlementDropItem> drops,
            OnlineSettlementField<bool> isReturnUserParty,
            OnlineSettlementField<bool> hasDeadUserPenalty,
            OnlineSettlementField<int> unknownExpSlot2,
            OnlineSettlementField<int> unknownReceiverValue,
            OnlineSettlementField<int> unknownPercent,
            OnlineSettlementField<bool> unknownIsEventCharacParty,
            OnlineSettlementField<int> unknownPremiumRewardActiveCount,
            IReadOnlyList<string> diffFields,
            int confidentExpGrant)
        {
            DungeonId = dungeonId;
            DungeonPath = dungeonPath ?? string.Empty;
            Difficulty = difficulty;
            RoomsEntered = roomsEntered;
            IsTimedOut = isTimedOut;
            ResultParseState = resultParseState;
            RewardParseState = rewardParseState;
            HasRank = hasRank;
            RankFrame = rankFrame;
            RankLetter = rankLetter ?? "F";
            RankSource = rankSource;
            ServerRankValue = serverRankValue;
            ClearTimeMs = clearTimeMs;
            PersonalBestTimeMs = personalBestTimeMs;
            IsNewRecord = isNewRecord;
            IsQuestMaze = isQuestMaze;
            Kills = kills;
            DroppedKills = droppedKills;
            BaseExp = baseExp;
            RankBonusExp = rankBonusExp;
            NamedBonusExpLines = namedBonusExpLines ?? Array.Empty<OnlineSettlementExpLine>();
            RewardGold = rewardGold;
            Drops = drops ?? Array.Empty<OnlineSettlementDropItem>();
            IsReturnUserParty = isReturnUserParty;
            HasDeadUserPenalty = hasDeadUserPenalty;
            UnknownExpSlot2 = unknownExpSlot2;
            UnknownReceiverValue = unknownReceiverValue;
            UnknownPercent = unknownPercent;
            UnknownIsEventCharacParty = unknownIsEventCharacParty;
            UnknownPremiumRewardActiveCount = unknownPremiumRewardActiveCount;
            DiffFields = diffFields ?? Array.Empty<string>();
            ConfidentExpGrant = confidentExpGrant > 0 ? confidentExpGrant : 0;
        }

        public int DungeonId { get; }
        public string DungeonPath { get; }
        public int Difficulty { get; }
        public int RoomsEntered { get; }
        public bool IsTimedOut { get; }
        public OnlineSettlementParseState ResultParseState { get; }
        public OnlineSettlementParseState RewardParseState { get; }

        /// <summary>评分字母(永远沿用本地离线同款算法,绝不取服务端 0x22 的 RankValue 字节)。</summary>
        public bool HasRank { get; }
        public int RankFrame { get; }
        public string RankLetter { get; }
        public SettlementFieldSource RankSource { get; }

        /// <summary>服务端 0x22 原始 RankValue 字节:客户端读完即丢弃、不参与评分字母显示(标为 Unknown 不画)。</summary>
        public OnlineSettlementField<byte> ServerRankValue { get; }

        public OnlineSettlementField<int> ClearTimeMs { get; }
        public OnlineSettlementField<int> PersonalBestTimeMs { get; }
        public OnlineSettlementField<bool> IsNewRecord { get; }
        public OnlineSettlementField<bool> IsQuestMaze { get; }
        public OnlineSettlementField<int> Kills { get; }
        public int DroppedKills { get; }

        /// <summary>0x23 下标 0 基础通关经验(推断;服务端优先于本地估算)。</summary>
        public OnlineSettlementField<int> BaseExp { get; }

        /// <summary>0x23 下标 1 评分加成经验(推断;服务端优先于本地估算)。</summary>
        public OnlineSettlementField<int> RankBonusExp { get; }

        /// <summary>0x23 下标 3..15 中非零的具名加成经验分项(不含下标 2,且绝不把 16 项相加当作“总获得经验”)。</summary>
        public IReadOnlyList<OnlineSettlementExpLine> NamedBonusExpLines { get; }

        /// <summary>0x23 奖励槽里的数值(按用法推断为金币数量,未同步至主角钱包)。</summary>
        public OnlineSettlementField<int> RewardGold { get; }

        /// <summary>0x23 任务清图物品与奖励槽物品(按本地物品表解析名字,查不到显示占位)。</summary>
        public IReadOnlyList<OnlineSettlementDropItem> Drops { get; }

        public OnlineSettlementField<bool> IsReturnUserParty { get; }
        public OnlineSettlementField<bool> HasDeadUserPenalty { get; }

        // 以下五项来自 0x23 的未知字段:只以 Unknown 记录供测试/对账,ShouldDisplay 恒为 false,绝不画到屏幕上。
        public OnlineSettlementField<int> UnknownExpSlot2 { get; }
        public OnlineSettlementField<int> UnknownReceiverValue { get; }
        public OnlineSettlementField<int> UnknownPercent { get; }
        public OnlineSettlementField<bool> UnknownIsEventCharacParty { get; }
        public OnlineSettlementField<int> UnknownPremiumRewardActiveCount { get; }

        /// <summary>服务端与本地估算不一致且采用服务端为准的字段名清单(不含任何数值,供脱敏日志用)。</summary>
        public IReadOnlyList<string> DiffFields { get; }

        /// <summary>仅由服务端有把握的 BaseExp + RankBonusExp 构成的可落入本地 HeroProgress 的经验增量(不含 16 项其余分项)。</summary>
        public int ConfidentExpGrant { get; }

        public bool ExpApplied { get; private set; }
        public int ExpAppliedAmount { get; private set; }
        public int LevelBefore { get; private set; }
        public int LevelAfter { get; private set; }
        public int LevelsGained => LevelAfter > LevelBefore ? LevelAfter - LevelBefore : 0;

        public void MarkExpApplied(int appliedAmount, int levelBefore, int levelAfter)
        {
            ExpApplied = true;
            ExpAppliedAmount = appliedAmount > 0 ? appliedAmount : 0;
            LevelBefore = levelBefore;
            LevelAfter = levelAfter;
        }
    }

    /// <summary>
    /// 把 <see cref="OnlineDungeonSettlementData"/>(来自 r18-sessionext 解析出口)与本地战绩对照合成为
    /// <see cref="OnlineSettlementSummary"/> 的纯函数构造器:
    /// <list type="bullet">
    /// <item>评分字母永远采用本地离线同款 <see cref="DungeonResult"/>,绝不采用服务端 0x22 的 <c>RankValue</c> 字节;</item>
    /// <item>通关用时、击杀、基础通关经验、评分加成经验、奖励金币在服务端有有效解析值时一律以服务端为准,与本地不一致记入 <see cref="OnlineSettlementSummary.DiffFields"/>;</item>
    /// <item>经验块 16 项绝不相加充当“总经验”,且跳过未知的下标 2;</item>
    /// <item>服务端缺失或标“未知”的字段不伪造假数。</item>
    /// </list>
    /// </summary>
    public static class OnlineSettlementSummaryBuilder
    {
        private static readonly string[] NamedExpKeys =
        {
            "base",          // 0 (独立展示)
            "rank_bonus",    // 1 (独立展示)
            null,            // 2 (未知 — 永不展示)
            "avatar",        // 3
            "event",         // 4
            "item",          // 5
            "privilege",     // 6
            "pcroom_party",  // 7
            "channel",       // 8
            "upper_member",  // 9
            "creature",      // 10
            "return_user",   // 11
            "new_char",      // 12
            "manage",        // 13
            "growth_power",  // 14
            "premium_extra", // 15
        };

        public static OnlineSettlementSummary Build(
            OnlineDungeonSettlementData data,
            OnlineSettlementLocalContext local = null,
            Func<int, string> resolveItemName = null)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var diffs = new List<string>(5);
            OnlineSettlementResultData result = data.ResultParseState == OnlineSettlementParseState.Succeeded ? data.Result : null;
            OnlineSettlementRewardData reward = data.RewardParseState == OnlineSettlementParseState.Succeeded ? data.Reward : null;
            RunStats? localStats = local?.LocalStats;
            DungeonResult localResult = local?.LocalResult;

            // 1. 评分字母:永远取本地离线同款算法,服务端 RankValue 仅标 Unknown 不参与显示。
            bool hasRank = localResult != null && localResult.HasRank;
            int rankFrame = hasRank ? localResult.Frame : (ResultArt.FrameCount - 1);
            string rankLetter = hasRank ? localResult.Letter : "F";
            SettlementFieldSource rankSource = hasRank ? SettlementFieldSource.Local : SettlementFieldSource.None;
            OnlineSettlementField<byte> serverRankValue = result != null
                ? OnlineSettlementField<byte>.Unknown(result.RankValue, SettlementFieldSource.Server)
                : OnlineSettlementField<byte>.Missing();

            // 2. 通关用时:服务端有效(HasBestTimeBlock && ClearTime >= 0)时以服务端为准;否则退回本地用时;都没有则缺失(占位 --:--)
            OnlineSettlementField<int> clearTimeMs;
            if (result != null && result.HasBestTimeBlock && result.ClearTime >= 0)
            {
                clearTimeMs = OnlineSettlementField<int>.Inferred(result.ClearTime, SettlementFieldSource.Server);
                if (localStats.HasValue && localStats.Value.ClearMs > 0 && localStats.Value.ClearMs != result.ClearTime)
                {
                    diffs.Add("clear_time");
                }
            }
            else if (localStats.HasValue && localStats.Value.ClearMs > 0)
            {
                clearTimeMs = OnlineSettlementField<int>.Observed(localStats.Value.ClearMs, SettlementFieldSource.Local);
            }
            else
            {
                clearTimeMs = OnlineSettlementField<int>.Missing();
            }

            // 3. 个人最佳用时 & 新纪录 & 任务迷宫(仅当 HasBestTimeBlock 为真时解析)
            OnlineSettlementField<int> personalBest = OnlineSettlementField<int>.Missing();
            OnlineSettlementField<bool> isNewRecord = OnlineSettlementField<bool>.Missing();
            OnlineSettlementField<bool> isQuestMaze = OnlineSettlementField<bool>.Missing();
            if (result != null && result.HasBestTimeBlock)
            {
                isQuestMaze = OnlineSettlementField<bool>.Observed(result.IsQuestMaze, SettlementFieldSource.Server);
                if (TryFindSelfBestTime(result.Entries, local?.SelfUniqueId, out OnlineSettlementBestTime best))
                {
                    if (best.BestTime >= 0)
                    {
                        personalBest = OnlineSettlementField<int>.Inferred(best.BestTime, SettlementFieldSource.Server);
                    }
                    isNewRecord = OnlineSettlementField<bool>.Observed(best.IsNewRecord, SettlementFieldSource.Server);
                }
            }

            // 4. 击杀数:以联网上报数为准,与本地不一致记 diff
            OnlineSettlementField<int> kills = OnlineSettlementField<int>.Observed(data.ReportedKills, SettlementFieldSource.Server);
            if (localStats.HasValue && localStats.Value.Kills != data.ReportedKills)
            {
                diffs.Add("kills");
            }

            // 5. 经验块:只取有把握的 BaseExp(0)、RankBonusExp(1) 与具名分项(3..15);跳过未知的下标 2;绝不把 16 项相加
            OnlineSettlementField<int> baseExp = OnlineSettlementField<int>.Missing();
            OnlineSettlementField<int> rankBonusExp = OnlineSettlementField<int>.Missing();
            OnlineSettlementField<int> unknownExpSlot2 = OnlineSettlementField<int>.Missing();
            var namedBonus = new List<OnlineSettlementExpLine>();
            int confidentGrant = 0;

            if (reward != null && reward.ExpValues != null && reward.ExpValues.Count > 0)
            {
                int srvBase = reward.ExpValues[0];
                baseExp = OnlineSettlementField<int>.Inferred(srvBase, SettlementFieldSource.Server);
                if (localResult != null && localResult.ExpBase != srvBase)
                {
                    diffs.Add("base_exp");
                }

                int srvRank = reward.ExpValues.Count > 1 ? reward.ExpValues[1] : 0;
                if (reward.ExpValues.Count > 1)
                {
                    rankBonusExp = OnlineSettlementField<int>.Inferred(srvRank, SettlementFieldSource.Server);
                    if (localResult != null && localResult.ExpRankBonus != srvRank)
                    {
                        diffs.Add("rank_bonus_exp");
                    }
                }

                if (reward.ExpValues.Count > 2)
                {
                    unknownExpSlot2 = OnlineSettlementField<int>.Unknown(reward.ExpValues[2], SettlementFieldSource.Server);
                }

                int upper = Math.Min(reward.ExpValues.Count, NamedExpKeys.Length);
                for (int i = 3; i < upper; i++)
                {
                    int amount = reward.ExpValues[i];
                    string key = NamedExpKeys[i];
                    if (amount > 0 && !string.IsNullOrEmpty(key))
                    {
                        namedBonus.Add(new OnlineSettlementExpLine(i, key, amount, SettlementFieldConfidence.Inferred));
                    }
                }

                long grantSum = (srvBase > 0 ? (long)srvBase : 0L) + (srvRank > 0 ? (long)srvRank : 0L);
                confidentGrant = grantSum >= int.MaxValue ? int.MaxValue : (int)grantSum;
            }
            else if (localResult != null)
            {
                if (localResult.ExpBase > 0)
                {
                    baseExp = OnlineSettlementField<int>.Observed(ClampToInt(localResult.ExpBase), SettlementFieldSource.Local);
                }
                if (localResult.ExpRankBonus > 0)
                {
                    rankBonusExp = OnlineSettlementField<int>.Observed(ClampToInt(localResult.ExpRankBonus), SettlementFieldSource.Local);
                }
            }

            // 6. 奖励金币(推断)与掉落物品
            OnlineSettlementField<int> rewardGold = OnlineSettlementField<int>.Missing();
            var drops = new List<OnlineSettlementDropItem>();
            OnlineSettlementField<bool> isReturnUser = OnlineSettlementField<bool>.Missing();
            OnlineSettlementField<bool> hasDeadPenalty = OnlineSettlementField<bool>.Missing();
            OnlineSettlementField<int> unknownReceiver = OnlineSettlementField<int>.Missing();
            OnlineSettlementField<int> unknownPercent = OnlineSettlementField<int>.Missing();
            OnlineSettlementField<bool> unknownEventCharac = OnlineSettlementField<bool>.Missing();
            OnlineSettlementField<int> unknownPremium = OnlineSettlementField<int>.Missing();

            if (reward != null)
            {
                if (TrySelectRewardSlot(reward.Rewards, local?.PartySlot, out OnlineSettlementRewardSlotData activeSlot))
                {
                    rewardGold = OnlineSettlementField<int>.Inferred(activeSlot.Value, SettlementFieldSource.Server);
                    if (local != null && local.LocalGoldEarned > 0 && local.LocalGoldEarned != activeSlot.Value)
                    {
                        diffs.Add("reward_gold");
                    }
                }

                if (reward.QuestItems != null)
                {
                    for (int i = 0; i < reward.QuestItems.Count; i++)
                    {
                        OnlineSettlementItemData item = reward.QuestItems[i];
                        string name = SafeResolveItemName(resolveItemName, item.ItemId);
                        drops.Add(new OnlineSettlementDropItem(item.ItemId, item.Data > 0 ? item.Data : 1, name, isQuestItem: true));
                    }
                }

                if (reward.Rewards != null)
                {
                    for (int i = 0; i < reward.Rewards.Count; i++)
                    {
                        OnlineSettlementRewardSlotData slot = reward.Rewards[i];
                        if (slot.HasItem)
                        {
                            string name = SafeResolveItemName(resolveItemName, slot.ItemId);
                            drops.Add(new OnlineSettlementDropItem(slot.ItemId, slot.ItemData > 0 ? slot.ItemData : 1, name, isQuestItem: false));
                        }
                    }
                }

                isReturnUser = OnlineSettlementField<bool>.Observed(reward.IsReturnUserParty, SettlementFieldSource.Server);
                hasDeadPenalty = OnlineSettlementField<bool>.Inferred(reward.HasDeadUserPenalty, SettlementFieldSource.Server);

                unknownReceiver = OnlineSettlementField<int>.Unknown(reward.ReceiverValue, SettlementFieldSource.Server);
                unknownPercent = OnlineSettlementField<int>.Unknown(reward.Percent, SettlementFieldSource.Server);
                unknownEventCharac = OnlineSettlementField<bool>.Unknown(reward.IsEventCharacParty, SettlementFieldSource.Server);
                int premiumCount = 0;
                if (reward.PremiumRewards != null)
                {
                    for (int i = 0; i < reward.PremiumRewards.Count; i++)
                    {
                        if (reward.PremiumRewards[i].Status != 0)
                        {
                            premiumCount++;
                        }
                    }
                }
                unknownPremium = OnlineSettlementField<int>.Unknown(premiumCount, SettlementFieldSource.Server);
            }

            return new OnlineSettlementSummary(
                data.DungeonId,
                data.DungeonPath,
                data.Difficulty,
                data.RoomsEntered,
                data.IsTimedOut,
                data.ResultParseState,
                data.RewardParseState,
                hasRank,
                rankFrame,
                rankLetter,
                rankSource,
                serverRankValue,
                clearTimeMs,
                personalBest,
                isNewRecord,
                isQuestMaze,
                kills,
                data.DroppedKills,
                baseExp,
                rankBonusExp,
                namedBonus.ToArray(),
                rewardGold,
                drops.ToArray(),
                isReturnUser,
                hasDeadPenalty,
                unknownExpSlot2,
                unknownReceiver,
                unknownPercent,
                unknownEventCharac,
                unknownPremium,
                diffs.ToArray(),
                confidentGrant);
        }

        private static bool TryFindSelfBestTime(
            IReadOnlyList<OnlineSettlementBestTime> entries,
            ushort? selfUniqueId,
            out OnlineSettlementBestTime match)
        {
            match = default(OnlineSettlementBestTime);
            if (entries == null || entries.Count == 0)
            {
                return false;
            }
            if (selfUniqueId.HasValue)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].UniqueId == selfUniqueId.Value)
                    {
                        match = entries[i];
                        return true;
                    }
                }
                return false;
            }
            if (entries.Count == 1)
            {
                match = entries[0];
                return true;
            }
            return false;
        }

        private static bool TrySelectRewardSlot(
            IReadOnlyList<OnlineSettlementRewardSlotData> slots,
            int? partySlot,
            out OnlineSettlementRewardSlotData selected)
        {
            selected = default(OnlineSettlementRewardSlotData);
            if (slots == null || slots.Count == 0)
            {
                return false;
            }
            if (partySlot.HasValue && partySlot.Value >= 0 && partySlot.Value < slots.Count && slots[partySlot.Value].Status != 0)
            {
                selected = slots[partySlot.Value];
                return true;
            }
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Status != 0)
                {
                    selected = slots[i];
                    return true;
                }
            }
            return false;
        }

        private static string SafeResolveItemName(Func<int, string> resolver, int itemId)
        {
            if (resolver == null || itemId <= 0)
            {
                return null;
            }
            try
            {
                string name = resolver(itemId);
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int ClampToInt(long value)
        {
            if (value <= 0)
            {
                return 0;
            }
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
    }

    /// <summary>
    /// 封装在 Play 层的本地评分、物品名查询与主角有把握经验刷新帮助器,
    /// 对 <c>Dnf.Client</c> 屏蔽 <c>Dnf.Game</c>/<c>Dnf.Script</c> 类型以防 Unity CS0012。
    /// </summary>
    public static class OnlineSettlementHeroHelpers
    {
        /// <summary>使用与离线 <see cref="DungeonSession"/> 同一套算法计算本地评分与参考经验(不发经验、不写档)。</summary>
        public static DungeonResult ComputeLocalResult(
            DnfAssets assets,
            string dungeonPath,
            int dungeonId,
            int difficulty,
            RunStats stats,
            HeroProgress progress)
        {
            if (assets == null)
            {
                return null;
            }
            RankSystem rank = RankSystem.Parse(assets.ReadScript(RankSystem.Path));
            ScriptLine[] dungeonScript = string.IsNullOrEmpty(dungeonPath) ? null : assets.ReadScript(dungeonPath);
            int standardLevel = dungeonScript != null ? GoldDrop.StandardLevel(dungeonScript) : 1;
            float expWeight = 1f;
            if (dungeonScript != null)
            {
                IReadOnlyList<DungeonMaze> mazes = DungeonMaze.ParseAll(dungeonPath, dungeonScript);
                if (mazes.Count > 0 && mazes[0].ExpWeight > 0f)
                {
                    expWeight = mazes[0].ExpWeight;
                }
            }
            return DungeonResult.Compute(stats, rank, progress, dungeonId, standardLevel, difficulty, expWeight, members: 1);
        }

        /// <summary>构造带按编号缓存的“物品编号 → 本地化名字”查询器;缺失数据或查不到返回 null。</summary>
        public static Func<int, string> CreateItemNameResolver(DnfAssets assets, WorldTexts texts)
        {
            if (assets == null || texts == null)
            {
                return null;
            }
            ItemCatalog catalog = null;
            var cache = new Dictionary<int, string>();
            return itemId =>
            {
                if (itemId <= 0)
                {
                    return null;
                }
                if (cache.TryGetValue(itemId, out string cached))
                {
                    return cached;
                }
                if (catalog == null)
                {
                    catalog = new ItemCatalog(new GameData(new PvfScriptSource(assets.Pvf)));
                }
                ItemInfo info = catalog.Get(itemId);
                string name = info != null ? texts.ItemName(info) : null;
                cache[itemId] = name;
                return name;
            };
        }

        /// <summary>
        /// 按服务端有把握的已知经验增量(<see cref="OnlineSettlementSummary.ConfidentExpGrant"/> = BaseExp + RankBonusExp)刷新内存中的联网主角进度;
        /// 不写离线存档、不同步物品或金币、不把 16 项经验块全部相加。每次结算至多调用一次。
        /// </summary>
        public static int ApplyConfidentExp(HeroProgress progress, OnlineSettlementSummary summary, out int levelBefore, out int levelAfter)
        {
            levelBefore = progress != null ? progress.Level : 0;
            levelAfter = levelBefore;
            if (progress == null || summary == null || summary.ExpApplied || summary.ConfidentExpGrant <= 0)
            {
                return 0;
            }
            int grant = summary.ConfidentExpGrant;
            progress.Gain(grant);
            levelAfter = progress.Level;
            summary.MarkExpApplied(grant, levelBefore, levelAfter);
            return grant;
        }
    }

    /// <summary>
    /// 联网结算阶段的脱敏日志格式化(prefix <see cref="Prefix"/>):
    /// 只输出状态名、字段名、错误码与计数,绝不含经验/金币/用时数值、物品号、副本号、角色名、IP、端口或路径。
    /// </summary>
    public static class OnlineSettlementLog
    {
        public const string Prefix = "DNF_ONLINE stage=settle";

        public static string Show(bool timedOut, OnlineSettlementParseState resultState, OnlineSettlementParseState rewardState, int drops, int diffs)
        {
            return Prefix + " event=show timedout=" + (timedOut ? "true" : "false")
                + " result=" + resultState
                + " reward=" + rewardState
                + " drops=" + N(drops)
                + " diffs=" + N(diffs);
        }

        public static string Diff(string field)
        {
            return Prefix + " event=diff field=" + Token(field);
        }

        public static string Advance(string step, string status = null)
        {
            string line = Prefix + " event=advance step=" + Token(step);
            if (!string.IsNullOrEmpty(status))
            {
                line += " status=" + Token(status);
            }
            return line;
        }

        public static string Choose(SettlementChoice choice, string source)
        {
            return Prefix + " event=choose choice=" + choice + " source=" + Token(source);
        }

        public static string Retry(string status, int cycle)
        {
            return Prefix + " event=retry status=" + Token(status) + " cycle=" + N(cycle);
        }

        public static string Town(string reason, string areaSource, string signal = null)
        {
            string line = Prefix + " event=town reason=" + Token(reason) + " area=" + Token(areaSource);
            if (!string.IsNullOrEmpty(signal))
            {
                line += " signal=" + Token(signal);
            }
            return line;
        }

        public static string Fail(string reason, byte? code = null, string step = null)
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

        private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Token(string value) => string.IsNullOrEmpty(value) ? "unknown" : value;
    }
}
