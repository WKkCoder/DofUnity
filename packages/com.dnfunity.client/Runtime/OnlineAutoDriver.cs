using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Play;

namespace Dnf.Client
{
    /// <summary>
    /// 联网全流程无人值守驱动参数(<c>-dnfonlineauto</c>,r20-e2e)。
    /// 不含任何凭据(账号与口令沿用既有 <c>-dnfnetroot</c> / <c>dnf-online.conf</c>),<see cref="ToString"/> 不回显敏感信息。
    /// </summary>
    public sealed class OnlineAutoOptions
    {
        /// <summary>最短清图时间上限(秒)。</summary>
        public const int MaxMinClearTimeSeconds = 3600;

        /// <summary>最大副本模式值(0..4)。</summary>
        public const int MaxDungeonMode = 4;

        private readonly SettlementChoice[] _settlementChoices;

        public OnlineAutoOptions(
            int characterSlot = 0,
            int? dungeonId = null,
            int dungeonMode = 0,
            IReadOnlyList<SettlementChoice> settlementChoices = null,
            int minClearTimeSeconds = 0)
        {
            if (characterSlot < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(characterSlot), characterSlot, "角色槽位不能为负数");
            }
            if (dungeonId.HasValue && (dungeonId.Value < 0 || dungeonId.Value > ushort.MaxValue))
            {
                throw new ArgumentOutOfRangeException(nameof(dungeonId), dungeonId, "副本编号必须在 0..65535");
            }
            if (dungeonMode < 0 || dungeonMode > MaxDungeonMode)
            {
                throw new ArgumentOutOfRangeException(nameof(dungeonMode), dungeonMode, "副本模式必须在 0..4");
            }
            if (minClearTimeSeconds < 0 || minClearTimeSeconds > MaxMinClearTimeSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(minClearTimeSeconds), minClearTimeSeconds, "最短清图时间必须在 0..3600 秒");
            }
            CharacterSlot = characterSlot;
            DungeonId = dungeonId;
            DungeonMode = dungeonMode;
            MinClearTimeSeconds = minClearTimeSeconds;
            if (settlementChoices == null || settlementChoices.Count == 0)
            {
                _settlementChoices = new[] { SettlementChoice.ReturnToTown };
            }
            else
            {
                _settlementChoices = new SettlementChoice[settlementChoices.Count];
                for (int i = 0; i < settlementChoices.Count; i++)
                {
                    SettlementChoice c = settlementChoices[i];
                    if (c != SettlementChoice.Retry && c != SettlementChoice.SelectDungeon && c != SettlementChoice.ReturnToTown)
                    {
                        throw new ArgumentOutOfRangeException(nameof(settlementChoices), c, "结算选项只支持 retry/select/town");
                    }
                    _settlementChoices[i] = c;
                }
            }
        }

        public int CharacterSlot { get; }
        public int? DungeonId { get; }
        public int DungeonMode { get; }
        public int MinClearTimeSeconds { get; }
        public IReadOnlyList<SettlementChoice> SettlementChoices => _settlementChoices;

        public override string ToString()
        {
            return "OnlineAutoOptions(char=" + CharacterSlot
                + ",dungeon=" + (DungeonId.HasValue ? DungeonId.Value.ToString(CultureInfo.InvariantCulture) : "auto")
                + ",mode=" + DungeonMode
                + ",settleCount=" + _settlementChoices.Length
                + ",minClearSec=" + MinClearTimeSeconds + ")";
        }

        /// <summary>
        /// 解析命令行参数里的 <c>-dnfonlineauto*</c> 开关。
        /// 未带 <c>-dnfonlineauto</c> 返回 true 且 <paramref name="options"/> 为 null；
        /// 带了但未同带 <c>-dnfonline</c> 返回 true、<paramref name="options"/> 为 null 且 <paramref name="ignoredReason"/> 为 <c>"NeedsDnfonline"</c>；
        /// 参数格式非法返回 false 且给出脱敏错误原因 <paramref name="error"/>。
        /// </summary>
        public static bool TryParse(
            IReadOnlyList<string> args,
            bool onlineMode,
            out OnlineAutoOptions options,
            out string ignoredReason,
            out string error)
        {
            options = null;
            ignoredReason = null;
            error = null;
            if (args == null)
            {
                return true;
            }

            bool requested = false;
            int charSlot = 0;
            int? dungeonId = null;
            int dungeonMode = 0;
            int minClearSec = 0;
            List<SettlementChoice> settleChoices = null;

            for (int i = 0; i < args.Count; i++)
            {
                string token = args[i];
                if (token == null)
                {
                    continue;
                }
                string next = i + 1 < args.Count ? args[i + 1] : null;
                switch (token)
                {
                    case "-dnfonlineauto":
                        requested = true;
                        break;
                    case "-dnfonlineauto-char":
                        if (next == null || !TryParseNonNegativeInt(next, out charSlot))
                        {
                            error = "BadCharSlot";
                            return false;
                        }
                        i++;
                        break;
                    case "-dnfonlineauto-dungeon":
                        if (i + 2 >= args.Count
                            || !TryParseNonNegativeInt(args[i + 1], out int dId)
                            || dId > ushort.MaxValue
                            || !TryParseNonNegativeInt(args[i + 2], out int dMode)
                            || dMode > MaxDungeonMode)
                        {
                            error = "BadDungeon";
                            return false;
                        }
                        dungeonId = dId;
                        dungeonMode = dMode;
                        i += 2;
                        break;
                    case "-dnfonlineauto-settle":
                        if (string.IsNullOrEmpty(next) || !TryParseSettleList(next, out settleChoices))
                        {
                            error = "BadSettleChoices";
                            return false;
                        }
                        i++;
                        break;
                    case "-dnfonlineauto-mincleartime":
                        if (next == null || !TryParseNonNegativeInt(next, out minClearSec) || minClearSec > MaxMinClearTimeSeconds)
                        {
                            error = "BadMinClearTime";
                            return false;
                        }
                        i++;
                        break;
                }
            }

            if (!requested)
            {
                return true;
            }
            if (!onlineMode)
            {
                ignoredReason = "NeedsDnfonline";
                return true;
            }

            options = new OnlineAutoOptions(charSlot, dungeonId, dungeonMode, settleChoices, minClearSec);
            return true;
        }

        private static bool TryParseNonNegativeInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 9)
            {
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseSettleList(string text, out List<SettlementChoice> list)
        {
            list = new List<SettlementChoice>();
            string[] parts = text.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                switch (p)
                {
                    case "retry":
                        list.Add(SettlementChoice.Retry);
                        break;
                    case "select":
                        list.Add(SettlementChoice.SelectDungeon);
                        break;
                    case "town":
                        list.Add(SettlementChoice.ReturnToTown);
                        break;
                    default:
                        list = null;
                        return false;
                }
            }
            return list.Count > 0;
        }
    }

    /// <summary>
    /// 联网全流程无人值守状态机(r20-e2e):
    /// 严格经由 <see cref="OnlineShellFlow"/>、<see cref="GameFlow"/>、<see cref="DungeonSelectScreen"/>、
    /// <see cref="HostedRoomBot"/> 与 <see cref="OnlineSettlementController"/> 的公开入口推进全流程,不造旁路;
    /// 任何阶段出错或断线立即停手并给出非 0 退出码,绝不自动重试。
    /// </summary>
    public sealed class OnlineAutoDriver
    {
        private readonly OnlineAutoOptions _options;
        private readonly Action<string> _log;

        private bool _loginSubmitted;
        private bool _serverSubmitted;
        private bool _channelSubmitted;
        private bool _charSubmitted;
        private int _charCursor;
        private int _lastShellStageSerial = -1;
        private bool _enteredGameOnce;
        private bool _expectedTownReturn = true;
        private bool _selectRequestedFromTown;
        private bool _selectConfirmed;
        private bool _settlementChosenForCurrentActive;
        private bool _awaitingFinalTown;
        private int _settleIndex;
        private int _completedSettlements;
        private int _retryCycles;
        private HostedRoomRun _activeRun;
        private HostedRoomBot _roomBot;
        private bool _resultLogged;

        public OnlineAutoDriver(OnlineAutoOptions options, Action<string> log = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log;
            Stage = "init";
        }

        public OnlineAutoOptions Options => _options;
        public string Stage { get; private set; }
        public bool Finished { get; private set; }
        public bool Succeeded { get; private set; }
        public int ExitCode => Finished ? (Succeeded ? 0 : 1) : 0;
        public int CompletedSettlements => _completedSettlements;
        public int RetryCycles => _retryCycles;
        public string ResultLine { get; private set; }
        public HostedRoomBot ActiveRoomBot => _roomBot;

        /// <summary>外壳占屏时(或每帧在 Tick 外壳之后)推进登录 → 选服 → 选频道 → 选角。</summary>
        public void StepShell(OnlineShellFlow flow, long elapsedMs)
        {
            if (Finished || flow == null)
            {
                return;
            }
            if (flow.Stage == OnlineShellStage.InGame)
            {
                _enteredGameOnce = true;
                return;
            }
            if (_enteredGameOnce && flow.Stage == OnlineShellStage.Login)
            {
                // 进游戏后掉线退回登录页:立即失败收尾,绝不自动重连
                Fail("disconnect", elapsedMs);
                return;
            }

            if (flow.Notice != null && flow.Notice.IsError)
            {
                Fail(StageCode(flow.Stage), elapsedMs);
                return;
            }

            if (_lastShellStageSerial != flow.StageSerial)
            {
                _lastShellStageSerial = flow.StageSerial;
                if (flow.Stage == OnlineShellStage.CharacterSelect)
                {
                    _charCursor = 0;
                }
            }

            switch (flow.Stage)
            {
                case OnlineShellStage.Login:
                    Stage = "login";
                    if (!flow.Busy && flow.Login.PendingAction == LoginAction.None)
                    {
                        if (_loginSubmitted)
                        {
                            Fail("login", elapsedMs);
                            return;
                        }
                        _loginSubmitted = true;
                        flow.Key(OnlineShellKey.Enter);
                    }
                    break;

                case OnlineShellStage.ServerSelect:
                    Stage = "server";
                    if (!flow.Busy)
                    {
                        if (flow.ServerScreen != null)
                        {
                            if (flow.ServerScreen.Notice != null && flow.ServerScreen.Notice.IsError)
                            {
                                Fail("server", elapsedMs);
                                return;
                            }
                            if (flow.ServerScreen.Phase == PickerPhase.Failed || flow.ServerScreen.Phase == PickerPhase.Empty)
                            {
                                Fail("server", elapsedMs);
                                return;
                            }
                            if (flow.ServerScreen.Phase == PickerPhase.Ready)
                            {
                                if (!flow.ServerScreen.CanStart)
                                {
                                    Fail("server", elapsedMs);
                                    return;
                                }
                                if (!_serverSubmitted)
                                {
                                    _serverSubmitted = true;
                                    flow.Key(OnlineShellKey.Enter);
                                }
                            }
                        }
                        else if (!_serverSubmitted)
                        {
                            _serverSubmitted = true;
                            flow.ChooseServer();
                        }
                    }
                    break;

                case OnlineShellStage.ChannelSelect:
                    Stage = "channel";
                    if (!flow.Busy)
                    {
                        if (flow.ChannelScreen != null)
                        {
                            if (flow.ChannelScreen.Notice != null && flow.ChannelScreen.Notice.IsError)
                            {
                                Fail("channel", elapsedMs);
                                return;
                            }
                            if (flow.ChannelScreen.Phase == PickerPhase.Failed || flow.ChannelScreen.Phase == PickerPhase.Empty)
                            {
                                Fail("channel", elapsedMs);
                                return;
                            }
                            if (flow.ChannelScreen.Phase == PickerPhase.Ready && flow.ChannelScreen.Busy == ChannelBusy.None)
                            {
                                if (!flow.ChannelScreen.CanStart)
                                {
                                    Fail("channel", elapsedMs);
                                    return;
                                }
                                if (!_channelSubmitted)
                                {
                                    _channelSubmitted = true;
                                    flow.Key(OnlineShellKey.Enter);
                                }
                            }
                        }
                        else if (_channelSubmitted)
                        {
                            Fail("channel", elapsedMs);
                        }
                    }
                    break;

                case OnlineShellStage.CharacterSelect:
                    Stage = "charselect";
                    if (!flow.Busy && !_charSubmitted)
                    {
                        IReadOnlyList<OnlineCharacterEntry> entries = flow.Characters?.Entries;
                        if (entries == null || entries.Count == 0)
                        {
                            Fail("charselect", elapsedMs);
                            return;
                        }
                        int target = PickCharacterSlot(entries);
                        if (target < 0)
                        {
                            Fail("charselect", elapsedMs);
                            return;
                        }
                        if (_charCursor != target)
                        {
                            StepCharCursorTowards(flow, target);
                            return;
                        }
                        _charSubmitted = true;
                        flow.Key(OnlineShellKey.Enter);
                    }
                    break;

                case OnlineShellStage.Loading:
                    Stage = "loading";
                    break;
            }
        }

        /// <summary>进入游戏后每帧推进:进世界地图选图 → 房间自动机打怪(含最短清图时间) → 结算菜单按序列选择 → 最终回城成功收场。</summary>
        public void StepGame(
            GameFlow flow,
            OnlineDungeonFlowController dungeonController,
            OnlineSettlementController settlementController,
            double deltaMs,
            long elapsedMs)
        {
            if (Finished || flow == null)
            {
                return;
            }
            if (dungeonController != null && dungeonController.Phase == OnlineDungeonFlowPhase.Entering)
            {
                // 进图五步(含 Retry 自动再进同一副本)在途期间静待进入新副本,避免把旧局收尾或过渡态误判为异常返城
                Stage = "loading";
                ResetRoomBot();
                _settlementChosenForCurrentActive = false;
                return;
            }

            switch (flow.Stage)
            {
                case GameStage.Town:
                    Stage = "town";
                    ResetRoomBot();
                    _selectConfirmed = false;
                    _settlementChosenForCurrentActive = false;
                    if (dungeonController != null && dungeonController.ActiveNotice != null && dungeonController.ActiveNotice.Tone == ShellTone.Error)
                    {
                        Fail("town", elapsedMs);
                        return;
                    }
                    if (_awaitingFinalTown)
                    {
                        CompleteSuccess(elapsedMs);
                        return;
                    }
                    if (!_expectedTownReturn)
                    {
                        Fail("dungeon", elapsedMs);
                        return;
                    }
                    if (!_selectRequestedFromTown)
                    {
                        _selectRequestedFromTown = true;
                        flow.EnterSelect("online auto");
                    }
                    break;

                case GameStage.DungeonSelect:
                    Stage = "select";
                    ResetRoomBot();
                    _settlementChosenForCurrentActive = false;
                    if (flow.Select == null)
                    {
                        return;
                    }
                    if (flow.Select.Notice != null && flow.Select.Notice.Tone == ShellTone.Error)
                    {
                        Fail("select", elapsedMs);
                        return;
                    }
                    if (flow.Select.Busy || _selectConfirmed)
                    {
                        return;
                    }
                    int targetIndex = FindDungeonEntryIndex(flow.Select, dungeonController != null ? dungeonController.Policy : null);
                    if (targetIndex < 0)
                    {
                        Fail("select", elapsedMs);
                        return;
                    }
                    if (flow.Select.Index != targetIndex)
                    {
                        StepSelectTowards(flow.Select, targetIndex);
                        return;
                    }
                    int desiredDiff = Math.Min(_options.DungeonMode, flow.Select.MaxDifficulty);
                    if (flow.Select.Difficulty != desiredDiff)
                    {
                        flow.Select.MoveDifficulty(desiredDiff > flow.Select.Difficulty ? 1 : -1);
                        return;
                    }
                    if (flow.Select.IsSelectedDisabled)
                    {
                        Fail("select", elapsedMs);
                        return;
                    }
                    _selectConfirmed = true;
                    flow.Select.Confirm();
                    break;

                case GameStage.Loading:
                    Stage = "loading";
                    break;

                case GameStage.Dungeon:
                    _selectRequestedFromTown = false;
                    _selectConfirmed = false;
                    if (settlementController != null && settlementController.IsActive)
                    {
                        Stage = "settle";
                        StepSettlement(settlementController, elapsedMs);
                    }
                    else
                    {
                        Stage = "dungeon";
                        _expectedTownReturn = false;
                        _settlementChosenForCurrentActive = false;
                        StepDungeonCombat(flow.Run as HostedRoomRun, deltaMs, elapsedMs);
                    }
                    break;
            }
        }

        private void StepDungeonCombat(HostedRoomRun run, double deltaMs, long elapsedMs)
        {
            if (run == null)
            {
                return;
            }
            if (!ReferenceEquals(_activeRun, run))
            {
                _activeRun = run;
                double minClearMs = _options.MinClearTimeSeconds * 1000.0;
                double totalLimitMs = Math.Max(HostedRoomBot.DefaultTotalLimitMs, minClearMs + 120000.0);
                _roomBot = new HostedRoomBot(totalLimitMs, HostedRoomBot.DefaultNoProgressLimitMs)
                {
                    MinClearTimeMs = minClearMs,
                };
            }
            _roomBot.Advance(run, deltaMs);
            if (_roomBot.Stopped && _roomBot.Reason != HostedBotStop.Settled)
            {
                Fail("dungeon", elapsedMs);
            }
        }

        private void StepSettlement(OnlineSettlementController settle, long elapsedMs)
        {
            if (settle.Phase == OnlineSettlementPhase.MenuDegraded
                || (settle.Summary != null && settle.Summary.IsTimedOut)
                || (settle.CurrentNotice != null && IsSettlementErrorNotice(settle.CurrentNotice.Kind)))
            {
                Fail("settle", elapsedMs);
                return;
            }
            if (_settlementChosenForCurrentActive || settle.Phase != OnlineSettlementPhase.MenuReady)
            {
                return;
            }

            SettlementChoice choice = NextSettlementChoice();
            if (settle.Choose(choice))
            {
                _settlementChosenForCurrentActive = true;
                _completedSettlements++;
                if (choice == SettlementChoice.Retry)
                {
                    _retryCycles++;
                    _expectedTownReturn = false;
                }
                else if (choice == SettlementChoice.SelectDungeon)
                {
                    _expectedTownReturn = false;
                }
                else if (choice == SettlementChoice.ReturnToTown)
                {
                    _expectedTownReturn = true;
                    if (_settleIndex >= _options.SettlementChoices.Count)
                    {
                        _awaitingFinalTown = true;
                    }
                }
            }
            else
            {
                Fail("settle", elapsedMs);
            }
        }

        private SettlementChoice NextSettlementChoice()
        {
            if (_settleIndex < _options.SettlementChoices.Count)
            {
                SettlementChoice c = _options.SettlementChoices[_settleIndex];
                _settleIndex++;
                return c;
            }
            return SettlementChoice.ReturnToTown;
        }

        private static bool IsSettlementErrorNotice(OnlineSettlementNoticeKind kind)
        {
            switch (kind)
            {
                case OnlineSettlementNoticeKind.AdvanceNoReply:
                case OnlineSettlementNoticeKind.SettlementTimeout:
                case OnlineSettlementNoticeKind.EplpRejectedNotLeader:
                case OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen:
                case OnlineSettlementNoticeKind.EplpRejectedInvalidCommand:
                case OnlineSettlementNoticeKind.EplpRejectedOther:
                case OnlineSettlementNoticeKind.EplpTimeout:
                case OnlineSettlementNoticeKind.SendFailed:
                case OnlineSettlementNoticeKind.Disconnected:
                    return true;
                default:
                    return false;
            }
        }

        private int PickCharacterSlot(IReadOnlyList<OnlineCharacterEntry> entries)
        {
            int count = entries.Count;
            if (_options.CharacterSlot < count && OnlineCharacterRoster.BlockOf(entries[_options.CharacterSlot]) == CharacterBlock.None)
            {
                return _options.CharacterSlot;
            }
            for (int i = 0; i < count; i++)
            {
                if (OnlineCharacterRoster.BlockOf(entries[i]) == CharacterBlock.None)
                {
                    return i;
                }
            }
            return -1;
        }

        private void StepCharCursorTowards(OnlineShellFlow flow, int target)
        {
            // 在 6 列网格上从 (0,0) 出发:先对齐列(第 0 行列 0..dstCol 必存在),再向下对齐行(每一步都在 count 内)
            CharacterRoster.Cell(_charCursor, out int curCol, out int curRow);
            CharacterRoster.Cell(target, out int dstCol, out int dstRow);
            if (curCol < dstCol)
            {
                _charCursor++;
                flow.Key(OnlineShellKey.Right);
            }
            else if (curCol > dstCol)
            {
                _charCursor--;
                flow.Key(OnlineShellKey.Left);
            }
            else if (curRow < dstRow)
            {
                _charCursor += CharacterRoster.Columns;
                flow.Key(OnlineShellKey.Down);
            }
            else if (curRow > dstRow)
            {
                _charCursor -= CharacterRoster.Columns;
                flow.Key(OnlineShellKey.Up);
            }
        }

        private int FindDungeonEntryIndex(DungeonSelectScreen select, OnlineDungeonSelectPolicy policy)
        {
            if (_options.DungeonId.HasValue && policy != null)
            {
                for (int i = 0; i < select.Entries.Count; i++)
                {
                    DungeonEntry e = select.Entries[i];
                    if (policy.ResolveDungeonId(e) == _options.DungeonId.Value && !select.StatusOf(e).Disabled)
                    {
                        return i;
                    }
                }
                return -1;
            }
            for (int i = 0; i < select.Entries.Count; i++)
            {
                if (!select.StatusOf(select.Entries[i]).Disabled)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void StepSelectTowards(DungeonSelectScreen select, int targetIndex)
        {
            int distFwd = StepsTo(select, targetIndex, 1);
            int distBwd = StepsTo(select, targetIndex, -1);
            if (distFwd > 0 && (distBwd < 0 || distFwd <= distBwd))
            {
                select.Move(1);
            }
            else if (distBwd > 0)
            {
                select.Move(-1);
            }
        }

        private static int StepsTo(DungeonSelectScreen select, int targetIndex, int dir)
        {
            int start = select.Index;
            int steps = 0;
            int limit = select.Entries.Count + 1;
            int cur = start;
            for (int i = 0; i < limit; i++)
            {
                select.Move(dir);
                steps++;
                if (select.Index == targetIndex)
                {
                    RestoreIndex(select, start);
                    return steps;
                }
                if (select.Index == cur)
                {
                    break;
                }
                cur = select.Index;
            }
            RestoreIndex(select, start);
            return -1;
        }

        private static void RestoreIndex(DungeonSelectScreen select, int target)
        {
            int limit = select.Entries.Count + 2;
            for (int i = 0; i < limit && select.Index != target; i++)
            {
                int prev = select.Index;
                select.Move(-1);
                if (select.Index == prev)
                {
                    break;
                }
            }
            for (int i = 0; i < limit && select.Index != target; i++)
            {
                int prev = select.Index;
                select.Move(1);
                if (select.Index == prev)
                {
                    break;
                }
            }
        }

        private void ResetRoomBot()
        {
            _activeRun = null;
            _roomBot = null;
        }

        private void CompleteSuccess(long elapsedMs)
        {
            Stage = "done";
            Succeeded = true;
            Finished = true;
            EmitResult("ok", "done", elapsedMs);
        }

        public void Fail(string stage, long elapsedMs)
        {
            if (Finished)
            {
                return;
            }
            Stage = string.IsNullOrEmpty(stage) ? "unknown" : stage;
            Succeeded = false;
            Finished = true;
            EmitResult("fail", Stage, elapsedMs);
        }

        private void EmitResult(string result, string stage, long elapsedMs)
        {
            if (_resultLogged)
            {
                return;
            }
            _resultLogged = true;
            long clampedMs = elapsedMs < 0 ? 0 : elapsedMs;
            ResultLine = "DNF_ONLINE result=" + result + " stage=" + stage + " elapsedMs=" + clampedMs.ToString(CultureInfo.InvariantCulture);
            _log?.Invoke(ResultLine);
        }

        private static string StageCode(OnlineShellStage stage)
        {
            return OnlineShellStages.Code(stage);
        }
    }
}
