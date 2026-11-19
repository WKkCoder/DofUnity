using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client
{
    /// <summary>
    /// 联网副本结算就绪事件参数(交接给 <c>r18-settleui</c>):
    /// 触发时 <see cref="OnlineClientSession"/> 已完成 <see cref="OnlineClientSession.LeaveExclusiveReceiveAsync"/>,心跳泵已恢复、可立即发命令;
    /// 外部 UI 若需留在副本画面等待玩家翻牌/选按钮,将 <see cref="HoldForMenu"/> 设为 true,并在玩家选定后调用 <see cref="CompleteChoice"/>。
    /// </summary>
    public sealed class OnlineDungeonSettlementReady
    {
        private readonly Action<SettlementChoice> _complete;

        public OnlineDungeonSettlementReady(
            OnlineDungeonSettlementData data,
            OnlineClientSession session,
            Action<SettlementChoice> complete)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Session = session;
            _complete = complete ?? throw new ArgumentNullException(nameof(complete));
        }

        /// <summary>Play 层中性结算数据(通关结果、奖励、解析状态与击杀/房间统计)。</summary>
        public OnlineDungeonSettlementData Data { get; }

        /// <summary>已退出独占收包、恢复正常心跳与发包能力的在线会话(合成无真会话测试里可能为 null)。</summary>
        public OnlineClientSession Session { get; }

        /// <summary>是否由外部结算 UI 接管去留;设为 true 时流程停在 <see cref="GameStage.Dungeon"/> 直到调用 <see cref="CompleteChoice"/>。</summary>
        public bool HoldForMenu { get; set; }

        /// <summary>结束结算停留并把玩家选择(再次挑战 / 选择地下城 / 返回城镇)交回 <see cref="GameFlow"/>。</summary>
        public void CompleteChoice(SettlementChoice choice)
        {
            _complete(choice);
        }
    }

    /// <summary>把 <c>Dnf.Net.Session</c> 的结算通知记录映射到 <c>Dnf.Play</c> 中性结算数据结构(保持 <c>Dnf.Play</c> 不依赖 <c>Dnf.Net</c>)。</summary>
    public static class OnlineDungeonSettlementMapper
    {
        public static OnlineDungeonSettlementData Map(
            int dungeonId,
            string dungeonPath,
            int difficulty,
            int reportedKills,
            int droppedKills,
            int roomsEntered,
            OnlineDungeonCombatResult combat)
        {
            OnlineSettlementParseState resultState = OnlineSettlementParseState.NotApplicable;
            OnlineSettlementParseState rewardState = OnlineSettlementParseState.NotApplicable;

            if (combat != null && combat.Events != null)
            {
                for (int i = 0; i < combat.Events.Count; i++)
                {
                    OnlineCombatEvent ev = combat.Events[i];
                    if (ev == null || ev.Kind != OnlineCombatEventKind.SettlementArrived)
                    {
                        continue;
                    }
                    if (ev.Part == OnlineCombatSettlementPart.Result)
                    {
                        resultState = MapParseState(ev.SettlementParseStatus);
                    }
                    else if (ev.Part == OnlineCombatSettlementPart.Reward)
                    {
                        rewardState = MapParseState(ev.SettlementParseStatus);
                    }
                }
            }

            OnlineSettlementResultData resultData = MapResult(combat?.SettlementResult);
            OnlineSettlementRewardData rewardData = MapReward(combat?.SettlementReward);

            if (resultData != null && resultState == OnlineSettlementParseState.NotApplicable)
            {
                resultState = OnlineSettlementParseState.Succeeded;
            }
            if (rewardData != null && rewardState == OnlineSettlementParseState.NotApplicable)
            {
                rewardState = OnlineSettlementParseState.Succeeded;
            }

            return new OnlineDungeonSettlementData(
                dungeonId,
                dungeonPath,
                difficulty,
                reportedKills,
                droppedKills,
                roomsEntered,
                resultState,
                resultData,
                rewardState,
                rewardData);
        }

        private static OnlineSettlementParseState MapParseState(OnlineCombatSettlementParseStatus status)
        {
            switch (status)
            {
                case OnlineCombatSettlementParseStatus.Succeeded:
                    return OnlineSettlementParseState.Succeeded;
                case OnlineCombatSettlementParseStatus.Failed:
                    return OnlineSettlementParseState.Failed;
                default:
                    return OnlineSettlementParseState.NotApplicable;
            }
        }

        private static OnlineSettlementResultData MapResult(SettlementNoticeA src)
        {
            if (src == null)
            {
                return null;
            }
            OnlineSettlementBestTime[] entries;
            if (src.Entries == null || src.Entries.Count == 0)
            {
                entries = Array.Empty<OnlineSettlementBestTime>();
            }
            else
            {
                entries = new OnlineSettlementBestTime[src.Entries.Count];
                for (int i = 0; i < src.Entries.Count; i++)
                {
                    SettlementBestTimeEntry item = src.Entries[i];
                    entries[i] = new OnlineSettlementBestTime(item.UniqueId, item.BestTime, item.IsNewRecord);
                }
            }
            return new OnlineSettlementResultData(
                src.RankValue,
                src.ClearTime,
                src.TimeBonus,
                src.BaseScore,
                src.QuestMazeFlag,
                src.HasBestTimeBlock,
                entries);
        }

        private static OnlineSettlementRewardData MapReward(SettlementNoticeB src)
        {
            if (src == null)
            {
                return null;
            }
            int[] expValues = CopyIntList(src.ExpValues);
            OnlineSettlementItemData[] questItems = MapQuestItems(src.QuestItems);
            OnlineSettlementRewardSlotData[] rewards = MapRewardSlots(src.Rewards);
            byte[] reserved = CopyByteList(src.ReservedBytes);
            OnlineSettlementRewardSlotData[] premium = MapRewardSlots(src.PremiumRewards);

            return new OnlineSettlementRewardData(
                expValues,
                src.ExpKind,
                questItems,
                rewards,
                src.ReceiverValue,
                reserved,
                premium,
                src.Percent,
                src.IsReturnUserParty,
                src.IsEventCharacParty,
                src.HasDeadUserPenalty);
        }

        private static int[] CopyIntList(IReadOnlyList<int> list)
        {
            if (list == null || list.Count == 0)
            {
                return Array.Empty<int>();
            }
            var copy = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                copy[i] = list[i];
            }
            return copy;
        }

        private static byte[] CopyByteList(IReadOnlyList<byte> list)
        {
            if (list == null || list.Count == 0)
            {
                return Array.Empty<byte>();
            }
            var copy = new byte[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                copy[i] = list[i];
            }
            return copy;
        }

        private static OnlineSettlementItemData[] MapQuestItems(IReadOnlyList<SettlementItemEntry> items)
        {
            if (items == null || items.Count == 0)
            {
                return Array.Empty<OnlineSettlementItemData>();
            }
            var copy = new OnlineSettlementItemData[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                SettlementItemEntry item = items[i];
                copy[i] = new OnlineSettlementItemData(item.Slot, item.ItemId, item.Data, item.Durability, item.AmplifyType, item.AmplifyValue);
            }
            return copy;
        }

        private static OnlineSettlementRewardSlotData[] MapRewardSlots(IReadOnlyList<SettlementRewardInfo> slots)
        {
            if (slots == null || slots.Count == 0)
            {
                return Array.Empty<OnlineSettlementRewardSlotData>();
            }
            var copy = new OnlineSettlementRewardSlotData[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                SettlementRewardInfo slot = slots[i];
                copy[i] = new OnlineSettlementRewardSlotData(slot.Status, slot.Reserved, slot.Value, slot.ItemId, slot.ItemData);
            }
            return copy;
        }
    }

    /// <summary>把 <see cref="DungeonPermissionSnapshot"/> 与实时到达的 (0,5) 帧喂进 <see cref="OnlineDungeonPermissionTable"/>。</summary>
    public static class OnlineDungeonPermissionAdapter
    {
        /// <summary>用进城时收集到的初始副本许可快照填表。</summary>
        public static void LoadSnapshot(OnlineDungeonPermissionTable table, DungeonPermissionSnapshot snapshot)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            if (snapshot == null)
            {
                table.LoadInitial(null, hasSnapshot: false);
                return;
            }
            var entries = new List<KeyValuePair<int, byte>>(snapshot.Entries.Count);
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                DungeonPermissionEntry e = snapshot.Entries[i];
                entries.Add(new KeyValuePair<int, byte>(e.DungeonId, e.Tier));
            }
            table.LoadInitial(entries, hasSnapshot: true, snapshot.NoticeCount, snapshot.MalformedNoticeCount);
        }

        /// <summary>检查一帧是否为 (0,5) 副本许可通知;若是则返回 true,成功解包写入表(并计 <paramref name="parsed"/> = true),坏帧保留原表仅记计数。</summary>
        public static bool TryApplyFrame(OnlineDungeonPermissionTable table, OnlineReceivedFrame frame, out bool parsed)
        {
            parsed = false;
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            if (frame == null || frame.PacketClass != PacketClass.Noti || frame.PacketId != DungeonPermissionParser.PacketId)
            {
                return false;
            }
            return TryApplyNotificationPayload(table, frame.Payload, out parsed);
        }

        /// <summary>直接尝试把一条 (0,5) 通知明文载荷解包并合入许可表(成功返回 true;坏包仅计坏帧数、保留原表不变)。</summary>
        public static bool TryApplyNotificationPayload(OnlineDungeonPermissionTable table, byte[] payload, out bool parsed)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            if (DungeonPermissionParser.TryParse(payload, out DungeonPermissionNotice notice) && notice != null)
            {
                var entries = new List<KeyValuePair<int, byte>>(notice.Entries.Count);
                for (int i = 0; i < notice.Entries.Count; i++)
                {
                    DungeonPermissionEntry e = notice.Entries[i];
                    entries.Add(new KeyValuePair<int, byte>(e.DungeonId, e.Tier));
                }
                table.ApplyUpdate(entries);
                parsed = true;
            }
            else
            {
                table.RecordMalformedNotice();
                parsed = false;
            }
            return true;
        }
    }

    /// <summary>测试可替换的在线会话接缝,封装独占收包交接与 <see cref="OnlineDungeonRunner"/> 构建。</summary>
    internal interface IOnlineDungeonSessionPort
    {
        OnlineClientSession RawSession { get; }
        bool IsDisconnected { get; }
        event Action<PacketClass, int, byte[]> FrameReceived;
        event Action<OnlineDisconnectReason> Disconnected;
        Task EnterExclusiveReceiveAsync(CancellationToken cancellationToken);
        Task LeaveExclusiveReceiveAsync(CancellationToken cancellationToken);
        OnlineDungeonRunner CreateDungeonRunner();
    }

    internal sealed class RealOnlineDungeonSessionPort : IOnlineDungeonSessionPort
    {
        private readonly OnlineClientSession _session;
        private readonly Dictionary<Action<PacketClass, int, byte[]>, Action<OnlineReceivedFrame>> _frameBridges =
            new Dictionary<Action<PacketClass, int, byte[]>, Action<OnlineReceivedFrame>>();

        public RealOnlineDungeonSessionPort(OnlineClientSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public OnlineClientSession RawSession => _session;

        public bool IsDisconnected
        {
            get
            {
                if (_session.State == OnlineClientSessionState.Disposed)
                {
                    return true;
                }
                GameClientSession game = _session.GameSession;
                return game == null || game.PeerClosed || game.LastFrameError != FrameError.None;
            }
        }

        public event Action<PacketClass, int, byte[]> FrameReceived
        {
            add
            {
                if (value == null)
                {
                    return;
                }
                lock (_frameBridges)
                {
                    Action<OnlineReceivedFrame> wrapped = frame =>
                    {
                        if (frame != null)
                        {
                            value(frame.PacketClass, frame.PacketId, frame.Payload);
                        }
                    };
                    _frameBridges[value] = wrapped;
                    _session.FrameReceived += wrapped;
                }
            }
            remove
            {
                if (value == null)
                {
                    return;
                }
                lock (_frameBridges)
                {
                    if (_frameBridges.TryGetValue(value, out Action<OnlineReceivedFrame> wrapped))
                    {
                        _session.FrameReceived -= wrapped;
                        _frameBridges.Remove(value);
                    }
                }
            }
        }

        public event Action<OnlineDisconnectReason> Disconnected
        {
            add => _session.Disconnected += value;
            remove => _session.Disconnected -= value;
        }

        public Task EnterExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            return _session.EnterExclusiveReceiveAsync(cancellationToken);
        }

        public Task LeaveExclusiveReceiveAsync(CancellationToken cancellationToken)
        {
            return _session.LeaveExclusiveReceiveAsync(cancellationToken);
        }

        public OnlineDungeonRunner CreateDungeonRunner()
        {
            GameClientSession game = _session.GameSession;
            if (game == null)
            {
                throw new InvalidOperationException("在线会话没有就绪的底层连接。");
            }
            // 交互流程退出副本后通过 LeaveExclusiveReceiveAsync 切回主会话心跳泵,禁止旧保活泵介入。
            return new OnlineDungeonRunner(game, keepAlive: null);
        }
    }
}
