using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>联网结算菜单控制器的内部阶段。</summary>
    public enum OnlineSettlementPhase
    {
        Idle = 0,
        AdvancingScoreScroll = 1,
        AdvancingCardRight = 2,
        SendingSelectCard = 3,
        MenuReady = 4,
        MenuDegraded = 5,
        WaitingEplpReply = 6,
        WaitingServerReturn = 7,
    }

    /// <summary><see cref="OnlineSettlementController"/> 的可调配置项。</summary>
    public sealed class OnlineSettlementOptions
    {
        /// <summary>服务端 <c>TimerEPLPCommandWait</c> 的回城超时(60 秒;E-421)。</summary>
        public const long ServerReturnTimerMs = 60_000;

        /// <summary>客户端默认结算菜单倒计时(50 秒,严格短于服务端 60 秒定时器,预留 10 秒发 1/75 与收 0/2 或 0/23+0/24)。</summary>
        public const long DefaultMenuCountdownMs = 50_000;

        public const long DefaultAdvanceReplyTimeoutMs = 5_000;
        public const long DefaultEplpReplyTimeoutMs = 5_000;
        public const long DefaultReturnWaitTimeoutMs = 8_000;
        public const long DefaultTownReturnWaitTimeoutMs = 6_000;

        public long MenuCountdownMs { get; set; } = DefaultMenuCountdownMs;
        public long AdvanceReplyTimeoutMs { get; set; } = DefaultAdvanceReplyTimeoutMs;
        public long EplpReplyTimeoutMs { get; set; } = DefaultEplpReplyTimeoutMs;
        public long ReturnWaitTimeoutMs { get; set; } = DefaultReturnWaitTimeoutMs;
        public long TownReturnWaitTimeoutMs { get; set; } = DefaultTownReturnWaitTimeoutMs;

        public DnfAssets Assets { get; set; }
        public ISpriteSource Sprites { get; set; }
        public OverlayArt Art { get; set; }
        public WorldTexts Texts { get; set; }
        public TownAtlas Atlas { get; set; }
        public Func<int, string> ItemNameResolver { get; set; }
        public Func<HeroProgress> GetProgress { get; set; }
        public Func<ushort?> GetSelfUniqueId { get; set; }
        public Func<int?> GetPartySlot { get; set; }
        public Func<TownPlace?> GetCurrentTownPlace { get; set; }
        public Action<OnlineSettlementReturnTarget> ApplyReturnTarget { get; set; }
        public Action<string> Log { get; set; }
        public Action OnDisconnected { get; set; }
        public Func<OnlineClientSession, IOnlineSettlementTransport> TransportFactory { get; set; }
        public Func<OnlineDungeonSettlementData, RunStats, HeroProgress, DungeonResult> LocalResultComputer { get; set; }
    }

    /// <summary>
    /// 联网结算画面与结算后菜单控制器(r18-settleui):
    /// <list type="bullet">
    /// <item>订阅 <see cref="OnlineDungeonFlowController.SettlementStarted"/>,将 <c>HoldForMenu</c> 置为 true 并挂接 <see cref="HostedRoomRun.HudOverlay"/> 画两层黑幕、营地、原版菜单与服务端战果;</item>
    /// <item>自动按序发送 <c>1/72</c> → <c>1/73</c> → <c>1/74</c>(占位决定:选第一个有权槽位,mode=0;无有权槽时不发 1/74);同一条命令绝不自动重发;</item>
    /// <item>响应玩家选择(再次挑战 / 选择其他副本 / 返回城镇;快速组队与神秘商店禁用)或在 &lt;60 秒倒计时到点前默认发 <c>1/75 (1, 2)</c>;</item>
    /// <item>正确区分 <c>(1, 75)</c> 成功 <c>[1, flag, cmd]</c> 与失败 <c>[0, err, cmd]</c>(错误码 8 / 0x85 / 1 / 其他分别提示);</item>
    /// <item>识别服务端回城信号(<c>0/2</c> 或 <c>0/23 + 0/24</c>,含服务端主动回城),按 <c>0/23</c> 村/区/坐标或进副本前城镇区落位,并复位全部状态保证连续多次再来一次零泄漏。</item>
    /// </list>
    /// </summary>
    public sealed class OnlineSettlementController : IDisposable
    {
        public const byte ErrorNotLeader = 8;
        public const byte ErrorSecretShopOpen = 0x85;
        public const byte ErrorInvalidCommand = 1;

        /// <summary>1/74 SELECT_CARD 选牌模式固定取 0(常规普通翻牌区;非付费特权区 1)。</summary>
        public const byte DefaultSelectCardMode = 0;

        private readonly struct InboundFrame
        {
            public readonly PacketClass PacketClass;
            public readonly int PacketId;
            public readonly byte[] Payload;

            public InboundFrame(PacketClass packetClass, int packetId, byte[] payload)
            {
                PacketClass = packetClass;
                PacketId = packetId;
                Payload = payload;
            }
        }

        private readonly GameFlow _flow;
        private readonly OnlineSettlementOptions _options;
        private readonly Func<int, string> _itemNameResolver;
        private readonly OverlayArt _art;
        private readonly ConcurrentQueue<InboundFrame> _inbound = new ConcurrentQueue<InboundFrame>();

        private OnlineDungeonFlowController _attachedDungeon;
        private OnlineDungeonSettlementReady _ready;
        private HostedRoomRun _activeRun;
        private IOnlineSettlementTransport _transport;
        private bool _ownsTransport;
        private CancellationTokenSource _sendCts;
        private Task<FrameStatus> _activeSendTask;

        private OnlineSettlementPhase _phase = OnlineSettlementPhase.Idle;
        private OnlineSettlementSummary _summary;
        private OnlineSettlementNotice _notice;
        private ClearCamp _camp;

        private double _ageMs;
        private int _hoverRow = -1;
        private SettlementChoice _pendingChoice = SettlementChoice.None;
        private EplpCommand? _sentCommand;
        private int _rejectedChoicesMask;
        private int _sent75CommandMask;

        private bool _sent72;
        private bool _sent73;
        private bool _sent74;
        private bool _sentGiveUp;
        private bool _cardSelectSkipped;
        private byte? _selectedCardIndex;

        private bool _gotNoti2;
        private bool _gotUserArea;
        private bool _gotAreaUsers;
        private UserAreaNotice _returnUserArea;
        private ushort? _selfUniqueId;

        private bool _hasNowMs;
        private long _lastNowMs;
        private long _stepStartedAtMs = -1;
        private long _returnWaitLimitMs;
        private bool _countdownStartPending;
        private bool _countdownStarted;
        private long _countdownDeadlineMs;
        private long _remainingCountdownMs;

        private TownPlace? _preDungeonPlace;
        private int _retryCycleCount;
        private int _disconnectedFlag;
        private bool _disconnectNotifiedForActive;
        private bool _serverInitiatedReturn;
        private bool _disposed;

        public OnlineSettlementController(GameFlow flow, OnlineSettlementOptions options = null)
        {
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            _options = options ?? new OnlineSettlementOptions();
            _art = _options.Art ?? new OverlayArt();
            _itemNameResolver = _options.ItemNameResolver
                ?? OnlineSettlementHeroHelpers.CreateItemNameResolver(_options.Assets, _options.Texts ?? WorldTexts.Shared);
            if (_flow.Stage == GameStage.Town && _flow.Place.HasValue)
            {
                _preDungeonPlace = _flow.Place.Value;
            }
        }

        public OnlineSettlementPhase Phase => _phase;
        public bool IsActive => _phase != OnlineSettlementPhase.Idle;
        public OnlineSettlementSummary Summary => _summary;
        public OnlineSettlementNotice CurrentNotice => _notice;
        public SettlementChoice PendingChoice => _pendingChoice;
        public int HoverRow => _hoverRow;
        public long RemainingCountdownMs => _remainingCountdownMs;
        public bool CardSelectSkipped => _cardSelectSkipped;
        public byte? SelectedCardIndex => _selectedCardIndex;
        public bool ServerInitiatedReturn => _serverInitiatedReturn;
        public int RetryCycleCount => _retryCycleCount;
        public TownPlace? PreDungeonPlace => _preDungeonPlace;

        /// <summary>返回严格短于服务端 60 秒回城定时器的客户端菜单倒计时(毫秒)。</summary>
        public static long EffectiveMenuCountdownMs(long configured)
        {
            long limit = configured > 0 ? configured : OnlineSettlementOptions.DefaultMenuCountdownMs;
            long maxAllowed = OnlineSettlementOptions.ServerReturnTimerMs - 1_000;
            return limit > maxAllowed ? maxAllowed : limit;
        }

        /// <summary>把 <see cref="SettlementChoice"/> 映射到 <c>1/75 EPLP_COMMAND</c> 命令值(0=重试 / 1=选其他副本 / 2=回城)。</summary>
        public static bool TryMapChoiceToEplp(SettlementChoice choice, out EplpCommand command)
        {
            switch (choice)
            {
                case SettlementChoice.Retry:
                    command = EplpCommand.Retry;
                    return true;
                case SettlementChoice.SelectDungeon:
                    command = EplpCommand.SelectOtherDungeon;
                    return true;
                case SettlementChoice.ReturnToTown:
                    command = EplpCommand.ReturnToTown;
                    return true;
                default:
                    command = default(EplpCommand);
                    return false;
            }
        }

        /// <summary>按 <c>(1, 75)</c> 错误码区分提示种类(8=非队长 / 0x85=神秘商店未购 / 1=非法命令 / 其他)。</summary>
        public static OnlineSettlementNoticeKind ClassifyEplpError(byte errorCode)
        {
            switch (errorCode)
            {
                case ErrorNotLeader:
                    return OnlineSettlementNoticeKind.EplpRejectedNotLeader;
                case ErrorSecretShopOpen:
                    return OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen;
                case ErrorInvalidCommand:
                    return OnlineSettlementNoticeKind.EplpRejectedInvalidCommand;
                default:
                    return OnlineSettlementNoticeKind.EplpRejectedOther;
            }
        }

        /// <summary>把 <c>(1, 73)</c> 返回的 4 个槽位翻牌权转成选牌下标(【占位决定】取第一个值为 1 的槽位;都没有返回 -1 表示不发 1/74)。</summary>
        public static int PickCardSlotFromRights(System.Collections.Generic.IReadOnlyList<short> slotStates)
        {
            if (slotStates == null)
            {
                return -1;
            }
            for (int i = 0; i < slotStates.Count; i++)
            {
                if (slotStates[i] == 1)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>订阅联网副本控制器的 <see cref="OnlineDungeonFlowController.SettlementStarted"/> 事件。</summary>
        public void Attach(OnlineDungeonFlowController dungeonFlow)
        {
            if (_disposed)
            {
                return;
            }
            Detach();
            if (dungeonFlow == null)
            {
                return;
            }
            _attachedDungeon = dungeonFlow;
            _attachedDungeon.SettlementStarted += OnSettlementStarted;
        }

        /// <summary>退订联网副本控制器。</summary>
        public void Detach()
        {
            if (_attachedDungeon != null)
            {
                _attachedDungeon.SettlementStarted -= OnSettlementStarted;
                _attachedDungeon = null;
            }
        }

        /// <summary>某菜单行当前是否可选(快速组队永远禁用;结算通知超时仅开放返回城镇;推进/等待期间全部禁用;已被 1/75 拒绝过的项禁用)。</summary>
        public bool IsChoiceEnabled(SettlementChoice choice)
        {
            if (!IsActive || !SettlementArt.IsEnabled(choice) || _pendingChoice != SettlementChoice.None)
            {
                return false;
            }
            if (_phase != OnlineSettlementPhase.MenuReady && _phase != OnlineSettlementPhase.MenuDegraded)
            {
                return false;
            }
            if ((_rejectedChoicesMask & (1 << (int)choice)) != 0)
            {
                return false;
            }
            if (_summary != null && _summary.IsTimedOut)
            {
                return choice == SettlementChoice.ReturnToTown;
            }
            return true;
        }

        /// <summary>
        /// 收到 <see cref="OnlineDungeonFlowController.SettlementStarted"/> 交接事件:接管停留、展示结果并启动 72/73/74 推进。
        /// 方法体中途任何异常(宿主回调、摘要构造、营地创建、传输挂接……)都就地收拾、绝不外传:
        /// 撤回 <c>HoldForMenu</c>(流程控制器据此走与"没有订阅者"相同的默认结算退出,不会被永久留在副本里)、
        /// 把被摘掉的 <see cref="HostedRoomRun.Pump"/> 装回去、撤绘制钩子/传输订阅/取消源/入队帧并回到空闲,只写一行脱敏失败日志(令牌 <c>start_fault</c>,步骤只放异常类型名)。
        /// </summary>
        public void OnSettlementStarted(OnlineDungeonSettlementReady ready)
        {
            if (_disposed || ready == null)
            {
                return;
            }

            // 被本方法摘掉过 Pump 的那一局与它的原 Pump:失败时据此还原,使这一局与"没有订阅者"的默认路径等价(HudOverlay 由 ResetActiveState 撤)。
            HostedRoomRun pumpRun = null;
            Action<HostedRoomRun> pumpBackup = null;
            try
            {
                if (IsActive)
                {
                    ResetActiveState();
                }

                ready.HoldForMenu = true;
                _ready = ready;
                _activeRun = _flow.Run as HostedRoomRun;
                _serverInitiatedReturn = false;
                _disconnectNotifiedForActive = false;
                Interlocked.Exchange(ref _disconnectedFlag, 0);

                TownPlace? currentPlace = _options.GetCurrentTownPlace?.Invoke();
                if (currentPlace.HasValue)
                {
                    _preDungeonPlace = currentPlace.Value;
                }
                else if (_flow.Place.HasValue)
                {
                    _preDungeonPlace = _flow.Place.Value;
                }

                HeroProgress progress = _options.GetProgress?.Invoke();
                _selfUniqueId = _options.GetSelfUniqueId?.Invoke();
                RunStats? localStats = _activeRun != null ? (RunStats?)_activeRun.SnapshotLocalStats() : null;
                DungeonResult localResult = null;
                if (localStats.HasValue)
                {
                    localResult = _options.LocalResultComputer != null
                        ? _options.LocalResultComputer(ready.Data, localStats.Value, progress)
                        : OnlineSettlementHeroHelpers.ComputeLocalResult(
                            _options.Assets,
                            ready.Data.DungeonPath,
                            ready.Data.DungeonId,
                            ready.Data.Difficulty,
                            localStats.Value,
                            progress);
                }

                var localCtx = new OnlineSettlementLocalContext
                {
                    LocalStats = localStats,
                    LocalResult = localResult,
                    LocalGoldEarned = _activeRun?.LocalGoldEarned ?? 0,
                    LocalItemsPicked = _activeRun?.LocalItemsPicked ?? 0,
                    LocalExpEarned = _activeRun?.LocalExpEarned ?? 0,
                    SelfUniqueId = _selfUniqueId,
                    PartySlot = _options.GetPartySlot?.Invoke(),
                };

                _summary = OnlineSettlementSummaryBuilder.Build(ready.Data, localCtx, _itemNameResolver);
                WriteLog(OnlineSettlementLog.Show(
                    _summary.IsTimedOut,
                    _summary.ResultParseState,
                    _summary.RewardParseState,
                    _summary.Drops.Count,
                    _summary.DiffFields.Count));
                for (int i = 0; i < _summary.DiffFields.Count; i++)
                {
                    WriteLog(OnlineSettlementLog.Diff(_summary.DiffFields[i]));
                }

                // 仅刷新有把握的已知经验(BaseExp + RankBonusExp),每次结算至多一次;物品/金币不同步。
                if (progress != null && _summary.ConfidentExpGrant > 0)
                {
                    OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, _summary, out _, out _);
                }

                if (_activeRun != null && _options.Assets != null && _activeRun.Room?.Hero != null)
                {
                    _camp = ClearCamp.Create(_options.Assets, _activeRun.MapPath, _activeRun.Room.Hero.X, _activeRun.Room.Hero.Y);
                }
                if (_activeRun != null)
                {
                    pumpRun = _activeRun;
                    pumpBackup = _activeRun.Pump;
                    _activeRun.Pump = null;
                    _activeRun.HudOverlay = BuildHud;
                }

                AttachTransport(ready.Session);
                _sendCts = new CancellationTokenSource();

                if (_summary.IsTimedOut)
                {
                    WriteLog(OnlineSettlementLog.Fail("settlement_timeout", step: "notices"));
                    _phase = OnlineSettlementPhase.MenuDegraded;
                    _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.SettlementTimeout);
                    ArmCountdown();
                    return;
                }

                if (_transport == null)
                {
                    _phase = OnlineSettlementPhase.MenuReady;
                    _notice = null;
                    ArmCountdown();
                    return;
                }

                if (_transport.IsDisconnected)
                {
                    HandleDisconnect();
                    return;
                }

                BeginSend72();
            }
            catch (Exception e)
            {
                // 先撤回"挂起等结算":流程控制器在处理函数返回后才读它;不撤回就会挂在副本里,而这里已是空闲、没人收尾。
                ready.HoldForMenu = false;
                try
                {
                    if (pumpRun != null)
                    {
                        pumpRun.Pump = pumpBackup;
                    }
                    _retryCycleCount = 0;
                    ResetActiveState();
                }
                catch (Exception)
                {
                    // 收尾本身再出错也不外传(挂起已撤回,流程会走默认退出)。
                }
                WriteLog(OnlineSettlementLog.Fail("start_fault", step: e.GetType().Name));
            }
        }

        /// <summary>玩家通过热键(F13/F11/F12 或 1/2/3)或点击选择结算项。</summary>
        public bool Choose(SettlementChoice choice)
        {
            return TryChooseInternal(choice, "player");
        }

        public void SetPointer(int x, int y)
        {
            if (!IsActive || _pendingChoice != SettlementChoice.None)
            {
                _hoverRow = -1;
                return;
            }
            _hoverRow = SettlementArt.HitRow(x, y);
        }

        public void ClearPointer()
        {
            _hoverRow = -1;
        }

        public SettlementChoice Click(int x, int y)
        {
            if (!IsActive)
            {
                return SettlementChoice.None;
            }
            SettlementChoice hit = SettlementArt.HitTest(x, y);
            return hit != SettlementChoice.None && Choose(hit) ? hit : SettlementChoice.None;
        }

        /// <summary>宿主每帧推进:记录城镇区域、消费异步帧、检查发送状态/超时与 60 秒内倒计时。</summary>
        public void Poll(long nowMs, double deltaMs)
        {
            if (_disposed)
            {
                return;
            }
            _hasNowMs = true;
            _lastNowMs = nowMs;

            if (!IsActive)
            {
                if (_flow.Stage == GameStage.Town && _flow.Place.HasValue)
                {
                    _preDungeonPlace = _flow.Place.Value;
                }
                return;
            }

            if (deltaMs > 0.0)
            {
                _ageMs += deltaMs;
                _camp?.Advance(deltaMs);
            }

            // 首次进入 Poll 时把所有延迟挂接的计时起点对齐到当前逻辑/墙钟 nowMs
            if (_stepStartedAtMs < 0)
            {
                _stepStartedAtMs = nowMs;
            }
            if (_countdownStartPending)
            {
                _countdownStartPending = false;
                _countdownStarted = true;
                long limit = EffectiveMenuCountdownMs(_options.MenuCountdownMs);
                _countdownDeadlineMs = nowMs + limit;
                _remainingCountdownMs = limit;
            }

            // 1. 先检查断线
            if (Interlocked.Exchange(ref _disconnectedFlag, 0) != 0 || (_transport != null && _transport.IsDisconnected))
            {
                HandleDisconnect();
                return;
            }

            // 2. 检查当前在途发送任务结果
            if (!PumpActiveSendTask(nowMs))
            {
                return;
            }

            // 3. 排空所有已到达的非心跳帧(一次性读完同批到达的 0/2、0/23、0/24 与 1/72/73/75)
            DrainInboundFrames(nowMs);
            if (!IsActive)
            {
                return;
            }

            // 4. 若已收到服务端回城信号(0/2 或 0/23+0/24),立即收尾(含服务端主动回城识别)
            if (CheckServerReturnSignal())
            {
                return;
            }

            // 5. 各阶段超时与 <60 秒自动回城倒计时
            switch (_phase)
            {
                case OnlineSettlementPhase.AdvancingScoreScroll:
                    if (nowMs - _stepStartedAtMs >= EffectiveTimeout(_options.AdvanceReplyTimeoutMs, OnlineSettlementOptions.DefaultAdvanceReplyTimeoutMs))
                    {
                        WriteLog(OnlineSettlementLog.Fail("advance_72_timeout", step: "72"));
                        EnterDegradedMenu(new OnlineSettlementNotice(OnlineSettlementNoticeKind.AdvanceNoReply), nowMs);
                    }
                    break;

                case OnlineSettlementPhase.AdvancingCardRight:
                    if (nowMs - _stepStartedAtMs >= EffectiveTimeout(_options.AdvanceReplyTimeoutMs, OnlineSettlementOptions.DefaultAdvanceReplyTimeoutMs))
                    {
                        WriteLog(OnlineSettlementLog.Fail("advance_73_timeout", step: "73"));
                        EnterDegradedMenu(new OnlineSettlementNotice(OnlineSettlementNoticeKind.AdvanceNoReply), nowMs);
                    }
                    break;

                case OnlineSettlementPhase.SendingSelectCard:
                    if (nowMs - _stepStartedAtMs >= EffectiveTimeout(_options.AdvanceReplyTimeoutMs, OnlineSettlementOptions.DefaultAdvanceReplyTimeoutMs))
                    {
                        WriteLog(OnlineSettlementLog.Fail("advance_74_timeout", step: "74"));
                        EnterDegradedMenu(new OnlineSettlementNotice(OnlineSettlementNoticeKind.AdvanceNoReply), nowMs);
                    }
                    break;

                case OnlineSettlementPhase.MenuReady:
                case OnlineSettlementPhase.MenuDegraded:
                    if (_countdownStarted)
                    {
                        _remainingCountdownMs = Math.Max(0, _countdownDeadlineMs - nowMs);
                        if (nowMs >= _countdownDeadlineMs)
                        {
                            if (!TryChooseInternal(SettlementChoice.ReturnToTown, "timeout"))
                            {
                                CompleteTransition(SettlementChoice.ReturnToTown, "countdown_fallback", "timeout_fallback");
                            }
                        }
                    }
                    break;

                case OnlineSettlementPhase.WaitingEplpReply:
                    if (nowMs - _stepStartedAtMs >= EffectiveTimeout(_options.EplpReplyTimeoutMs, OnlineSettlementOptions.DefaultEplpReplyTimeoutMs))
                    {
                        WriteLog(OnlineSettlementLog.Fail("eplp_timeout", step: _sentCommand?.ToString()));
                        _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.EplpTimeout);
                        CompleteTransition(SettlementChoice.ReturnToTown, "eplp_timeout", "timeout_fallback");
                    }
                    break;

                case OnlineSettlementPhase.WaitingServerReturn:
                    if (nowMs - _stepStartedAtMs >= _returnWaitLimitMs)
                    {
                        WriteLog(OnlineSettlementLog.Fail("return_wait_timeout", step: _pendingChoice.ToString()));
                        // 若玩家选的是 Retry 却没等到服务端回城确认,不可贸然发 CMD 15/16,安全落回城镇
                        SettlementChoice safeChoice = _pendingChoice == SettlementChoice.SelectDungeon
                            ? SettlementChoice.SelectDungeon
                            : SettlementChoice.ReturnToTown;
                        CompleteTransition(safeChoice, "return_timeout", "timeout_fallback");
                    }
                    break;
            }
        }

        /// <summary>供 <see cref="HostedRoomRun.HudOverlay"/> 调用的渲染入口。</summary>
        public System.Collections.Generic.List<SceneSprite> BuildHud(HostedRoomRun run, long timeMs)
        {
            if (!IsActive || _summary == null)
            {
                return null;
            }
            var state = new OnlineSettlementRenderState
            {
                Summary = _summary,
                Notice = _notice,
                AgeMs = _ageMs,
                HoverRow = _hoverRow,
                IsChoiceEnabled = IsChoiceEnabled,
                RemainingCountdownMs = _remainingCountdownMs,
                ShowCountdown = (_phase == OnlineSettlementPhase.MenuReady || _phase == OnlineSettlementPhase.MenuDegraded) && (_countdownStarted || _countdownStartPending),
                Camp = _camp,
                Projection = run?.Room?.Room?.Projection,
                Assets = _options.Assets,
                Sprites = _options.Sprites ?? _options.Assets,
            };
            return OnlineSettlementOverlay.Build(_art, state, timeMs);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Detach();
            ResetActiveState();
            _notice = null;
            _retryCycleCount = 0;
        }

        private bool TryChooseInternal(SettlementChoice choice, string source)
        {
            if (!IsChoiceEnabled(choice))
            {
                return false;
            }
            _pendingChoice = choice;
            _hoverRow = -1;
            WriteLog(OnlineSettlementLog.Choose(choice, source));

            if (_transport == null)
            {
                CompleteTransition(choice, source, "local");
                return true;
            }

            if (_summary != null && _summary.IsTimedOut)
            {
                // 结算通知超时路径只提供安全回城:发一次 1/45 GIVEUP_GAME(绝不重发)并短等回城信号,超时/失败照样安全回城
                if (!_sentGiveUp)
                {
                    _sentGiveUp = true;
                    _phase = OnlineSettlementPhase.WaitingServerReturn;
                    _stepStartedAtMs = _hasNowMs ? _lastNowMs : -1;
                    _returnWaitLimitMs = EffectiveTimeout(_options.TownReturnWaitTimeoutMs, OnlineSettlementOptions.DefaultTownReturnWaitTimeoutMs);
                    _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.WaitingReturn);
                    StartSend(SessionLifecyclePayloads.GiveUpGamePacketId, SessionLifecyclePayloads.BuildGiveUpGame());
                }
                else
                {
                    CompleteTransition(SettlementChoice.ReturnToTown, source, "fallback");
                }
                return true;
            }

            if (!TryMapChoiceToEplp(choice, out EplpCommand cmd))
            {
                _pendingChoice = SettlementChoice.None;
                return false;
            }

            int cmdBit = 1 << (int)cmd;
            if ((_sent75CommandMask & cmdBit) != 0)
            {
                // 同一条 1/75 命令绝不自动重发
                if (choice == SettlementChoice.ReturnToTown)
                {
                    CompleteTransition(SettlementChoice.ReturnToTown, source, "fallback");
                    return true;
                }
                _pendingChoice = SettlementChoice.None;
                return false;
            }

            _sent75CommandMask |= cmdBit;
            _sentCommand = cmd;
            _phase = OnlineSettlementPhase.WaitingEplpReply;
            _stepStartedAtMs = _hasNowMs ? _lastNowMs : -1;
            _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.WaitingReturn);
            byte[] payload = SettlementMenuPayloads.BuildEplpCommand(SettlementMenuPayloads.EplpFlagExecute, cmd);
            StartSend(SettlementMenuPayloads.EplpCommandPacketId, payload);
            return true;
        }

        private void BeginSend72()
        {
            if (_sent72)
            {
                return;
            }
            _sent72 = true;
            _phase = OnlineSettlementPhase.AdvancingScoreScroll;
            _stepStartedAtMs = _hasNowMs ? _lastNowMs : -1;
            _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.Advancing);
            WriteLog(OnlineSettlementLog.Advance("score_scroll", "sent"));
            StartSend(SettlementMenuPayloads.ScoreScrollStatePacketId, SettlementMenuPayloads.BuildScoreScrollState());
        }

        private void BeginSend73(long nowMs)
        {
            if (_sent73)
            {
                return;
            }
            _sent73 = true;
            _phase = OnlineSettlementPhase.AdvancingCardRight;
            _stepStartedAtMs = nowMs;
            WriteLog(OnlineSettlementLog.Advance("card_right", "sent"));
            StartSend(SettlementMenuPayloads.CardSelectRightStatePacketId, SettlementMenuPayloads.BuildCardSelectRightState());
        }

        private void BeginSend74(long nowMs, byte cardIndex)
        {
            if (_sent74)
            {
                return;
            }
            _sent74 = true;
            _selectedCardIndex = cardIndex;
            _phase = OnlineSettlementPhase.SendingSelectCard;
            _stepStartedAtMs = nowMs;
            WriteLog(OnlineSettlementLog.Advance("select_card", "sent"));
            byte[] payload = SettlementMenuPayloads.BuildSelectCard(DefaultSelectCardMode, cardIndex);
            StartSend(SettlementMenuPayloads.SelectCardPacketId, payload);
        }

        private void StartSend(ushort packetId, byte[] payload)
        {
            if (_transport == null)
            {
                return;
            }
            try
            {
                CancellationToken ct = _sendCts != null ? _sendCts.Token : CancellationToken.None;
                Task<FrameStatus> task = _transport.SendCommandAsync(packetId, payload, ct);
                _activeSendTask = task ?? Task.FromResult(FrameStatus.NoCipher);
            }
            catch (Exception e)
            {
                _activeSendTask = Task.FromException<FrameStatus>(e);
            }
        }

        private bool PumpActiveSendTask(long nowMs)
        {
            Task<FrameStatus> task = _activeSendTask;
            if (task == null || !task.IsCompleted)
            {
                return true;
            }
            _activeSendTask = null;

            bool ok = false;
            if (task.IsFaulted)
            {
                _ = task.Exception;
            }
            else if (!task.IsCanceled)
            {
                ok = task.Result == FrameStatus.Ok;
            }

            if (!ok)
            {
                if (_transport != null && _transport.IsDisconnected)
                {
                    HandleDisconnect();
                    return false;
                }
                switch (_phase)
                {
                    case OnlineSettlementPhase.AdvancingScoreScroll:
                    case OnlineSettlementPhase.AdvancingCardRight:
                    case OnlineSettlementPhase.SendingSelectCard:
                        WriteLog(OnlineSettlementLog.Fail("advance_send_failed", step: _phase.ToString()));
                        EnterDegradedMenu(new OnlineSettlementNotice(OnlineSettlementNoticeKind.AdvanceNoReply), nowMs);
                        return true;
                    case OnlineSettlementPhase.WaitingEplpReply:
                        WriteLog(OnlineSettlementLog.Fail("eplp_send_failed", step: _sentCommand?.ToString()));
                        _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.SendFailed);
                        CompleteTransition(SettlementChoice.ReturnToTown, "eplp_send_failed", "fallback");
                        return false;
                    case OnlineSettlementPhase.WaitingServerReturn:
                        WriteLog(OnlineSettlementLog.Fail("return_send_failed", step: _pendingChoice.ToString()));
                        CompleteTransition(SettlementChoice.ReturnToTown, "return_send_failed", "fallback");
                        return false;
                }
            }
            else if (_phase == OnlineSettlementPhase.SendingSelectCard)
            {
                WriteLog(OnlineSettlementLog.Advance("select_card", "ok"));
                _phase = OnlineSettlementPhase.MenuReady;
                _notice = null;
                StartCountdownNow(nowMs);
            }
            return true;
        }

        private void DrainInboundFrames(long nowMs)
        {
            while (_inbound.TryDequeue(out InboundFrame frame))
            {
                if (frame.PacketClass == PacketClass.Noti)
                {
                    HandleNotificationFrame(frame.PacketId, frame.Payload);
                }
                else if (frame.PacketClass == PacketClass.Cmd)
                {
                    HandleCommandReplyFrame(frame.PacketId, frame.Payload, nowMs);
                }
                if (!IsActive)
                {
                    return;
                }
            }
        }

        private void HandleNotificationFrame(int packetId, byte[] payload)
        {
            if (packetId == UserInfoParser.PacketId)
            {
                // 0/2 USERINFO:服务端 CParty::ReturnToVillage 推送的回城完成信号之一(E-421)
                _gotNoti2 = true;
                return;
            }

            if (packetId == TownAreaPayloads.UserAreaPacketId)
            {
                // 0/23 USER_AREA:回城落位村/区/坐标
                if (TownAreaPayloads.TryParseUserArea(payload, out UserAreaNotice ua) && ua != null)
                {
                    if (_selfUniqueId.HasValue && ua.UniqueId != _selfUniqueId.Value)
                    {
                        return;
                    }
                    _gotUserArea = true;
                    if (_returnUserArea == null)
                    {
                        _returnUserArea = ua;
                    }
                }
                else
                {
                    // 载荷损坏仍记已收包号,落位退回进副本前城镇区
                    _gotUserArea = true;
                }
                return;
            }

            if (packetId == TownAreaPayloads.AreaUsersPacketId)
            {
                // 0/24 AREA_USERS:与 0/23 成对确认回城完成(E-422)
                _gotAreaUsers = true;
            }
        }

        private void HandleCommandReplyFrame(int packetId, byte[] payload, long nowMs)
        {
            if (packetId == SettlementMenuPayloads.ScoreScrollStatePacketId
                && _phase == OnlineSettlementPhase.AdvancingScoreScroll)
            {
                if (SettlementMenuPayloads.TryParseScoreScrollReply(payload, out _))
                {
                    WriteLog(OnlineSettlementLog.Advance("score_scroll", "ok"));
                    BeginSend73(nowMs);
                }
                else
                {
                    WriteLog(OnlineSettlementLog.Fail("advance_72_malformed", step: "72"));
                    EnterDegradedMenu(new OnlineSettlementNotice(OnlineSettlementNoticeKind.AdvanceNoReply), nowMs);
                }
                return;
            }

            if (packetId == SettlementMenuPayloads.CardSelectRightStatePacketId
                && _phase == OnlineSettlementPhase.AdvancingCardRight)
            {
                if (!SettlementMenuPayloads.TryParseCardSelectRightReply(payload, out CardSelectRightReplyNotice reply))
                {
                    WriteLog(OnlineSettlementLog.Fail("advance_73_malformed", step: "73"));
                    EnterDegradedMenu(new OnlineSettlementNotice(OnlineSettlementNoticeKind.AdvanceNoReply), nowMs);
                    return;
                }
                int cardSlot = PickCardSlotFromRights(reply.SlotStates);
                if (cardSlot >= 0)
                {
                    WriteLog(OnlineSettlementLog.Advance("card_right", "ok"));
                    BeginSend74(nowMs, (byte)cardSlot);
                }
                else
                {
                    // 【占位决定】无任何有权槽位时不发 1/74,走服务端定时器路径并开放菜单接受晚到
                    _cardSelectSkipped = true;
                    WriteLog(OnlineSettlementLog.Advance("card_skip", "no_right"));
                    _phase = OnlineSettlementPhase.MenuReady;
                    _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.CardSelectSkipped);
                    StartCountdownNow(nowMs);
                }
                return;
            }

            if (packetId == SettlementMenuPayloads.EplpCommandPacketId
                && _phase == OnlineSettlementPhase.WaitingEplpReply)
            {
                if (!SettlementMenuPayloads.TryParseEplpReply(payload, out EplpReplyNotice reply))
                {
                    return;
                }
                if (!_sentCommand.HasValue || !reply.Command.HasValue || reply.Command.Value != _sentCommand.Value)
                {
                    return;
                }
                if (reply.Succeeded)
                {
                    if (reply.Flag != SettlementMenuPayloads.EplpFlagExecute)
                    {
                        // flag == 2 仅为 UI 光标同步回显,不是执行确认
                        return;
                    }
                    _phase = OnlineSettlementPhase.WaitingServerReturn;
                    _stepStartedAtMs = nowMs;
                    _returnWaitLimitMs = _pendingChoice == SettlementChoice.ReturnToTown
                        ? EffectiveTimeout(_options.TownReturnWaitTimeoutMs, OnlineSettlementOptions.DefaultTownReturnWaitTimeoutMs)
                        : EffectiveTimeout(_options.ReturnWaitTimeoutMs, OnlineSettlementOptions.DefaultReturnWaitTimeoutMs);
                }
                else
                {
                    // 失败回复字节序为 [0, errorCode, command]:按错误码 8 / 0x85 / 1 / 其他分别提示
                    OnlineSettlementNoticeKind noticeKind = ClassifyEplpError(reply.ErrorCode);
                    WriteLog(OnlineSettlementLog.Fail("eplp_rejected", code: reply.ErrorCode, step: _sentCommand.Value.ToString()));
                    _notice = new OnlineSettlementNotice(noticeKind, reply.ErrorCode);
                    _rejectedChoicesMask |= 1 << (int)_pendingChoice;

                    if (_pendingChoice == SettlementChoice.ReturnToTown)
                    {
                        CompleteTransition(SettlementChoice.ReturnToTown, "eplp_rejected_fallback", "fallback");
                    }
                    else
                    {
                        _pendingChoice = SettlementChoice.None;
                        _sentCommand = null;
                        _phase = OnlineSettlementPhase.MenuDegraded;
                        if (!_countdownStarted && !_countdownStartPending)
                        {
                            StartCountdownNow(nowMs);
                        }
                    }
                }
            }
        }

        private bool CheckServerReturnSignal()
        {
            bool areaPair = _gotUserArea && _gotAreaUsers;
            bool returned = _gotNoti2 || areaPair;
            if (!returned)
            {
                return false;
            }
            string signal = _gotNoti2 && areaPair ? "noti2_and_area" : (_gotNoti2 ? "noti2" : "area_pair");

            if (_pendingChoice != SettlementChoice.None
                && (_phase == OnlineSettlementPhase.WaitingServerReturn || _phase == OnlineSettlementPhase.WaitingEplpReply))
            {
                CompleteTransition(_pendingChoice, "confirmed", signal);
                return true;
            }

            // 未做选择前(如服务端 60 秒回城定时器到点)收到 0/2 或 0/23+0/24:识别为“已被服务端回城”并安全收尾
            _serverInitiatedReturn = true;
            _notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.ServerReturnedToTown);
            CompleteTransition(SettlementChoice.ReturnToTown, "server_return", signal);
            return true;
        }

        private void CompleteTransition(SettlementChoice choice, string reason, string signal)
        {
            OnlineDungeonSettlementReady ready = _ready;
            if (choice == SettlementChoice.ReturnToTown)
            {
                OnlineSettlementReturnTarget target = BuildReturnTarget();
                try
                {
                    _options.ApplyReturnTarget?.Invoke(target);
                }
                catch (Exception)
                {
                }
                WriteLog(OnlineSettlementLog.Town(reason, target.SourceToken, signal));
                _retryCycleCount = 0;
            }
            else if (choice == SettlementChoice.Retry)
            {
                _retryCycleCount++;
                WriteLog(OnlineSettlementLog.Retry("reenter", _retryCycleCount));
            }
            else if (choice == SettlementChoice.SelectDungeon)
            {
                _retryCycleCount = 0;
                WriteLog(OnlineSettlementLog.Choose(choice, reason));
            }

            ResetActiveState();
            ready?.CompleteChoice(choice);
        }

        private OnlineSettlementReturnTarget BuildReturnTarget()
        {
            if (_returnUserArea != null)
            {
                bool areaExists = _options.Atlas == null || _options.Atlas.GetArea(_returnUserArea.Village, _returnUserArea.Area) != null;
                if (areaExists)
                {
                    var place = new TownPlace(_returnUserArea.Village, _returnUserArea.Area, TownEntry.Return);
                    return new OnlineSettlementReturnTarget(
                        OnlineSettlementReturnSource.ServerUserArea,
                        place,
                        _returnUserArea.X,
                        _returnUserArea.Y,
                        _returnUserArea.Direction != 0);
                }
            }
            if (_preDungeonPlace.HasValue)
            {
                TownPlace pre = _preDungeonPlace.Value;
                bool areaExists = _options.Atlas == null || _options.Atlas.GetArea(pre.Town, pre.Area) != null;
                if (areaExists)
                {
                    return new OnlineSettlementReturnTarget(
                        OnlineSettlementReturnSource.PreDungeonArea,
                        new TownPlace(pre.Town, pre.Area, TownEntry.Return));
                }
            }
            return new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.Default, null);
        }

        private void EnterDegradedMenu(OnlineSettlementNotice notice, long nowMs)
        {
            _phase = OnlineSettlementPhase.MenuDegraded;
            _notice = notice;
            StartCountdownNow(nowMs);
        }

        private void ArmCountdown()
        {
            long limit = EffectiveMenuCountdownMs(_options.MenuCountdownMs);
            if (_hasNowMs)
            {
                _countdownStartPending = false;
                _countdownStarted = true;
                _countdownDeadlineMs = _lastNowMs + limit;
                _remainingCountdownMs = limit;
            }
            else
            {
                _countdownStartPending = true;
                _countdownStarted = false;
                _remainingCountdownMs = limit;
            }
        }

        private void StartCountdownNow(long nowMs)
        {
            long limit = EffectiveMenuCountdownMs(_options.MenuCountdownMs);
            _countdownStartPending = false;
            _countdownStarted = true;
            _countdownDeadlineMs = nowMs + limit;
            _remainingCountdownMs = limit;
        }

        private void HandleDisconnect()
        {
            string step = _phase.ToString();
            WriteLog(OnlineSettlementLog.Fail("disconnected", step: step));
            bool shouldNotify = !_disconnectNotifiedForActive;
            _disconnectNotifiedForActive = true;
            _flow.OnlineHoldDungeonExit = false;
            ResetActiveState();
            _retryCycleCount = 0;
            if (shouldNotify)
            {
                _options.OnDisconnected?.Invoke();
            }
        }

        private void AttachTransport(OnlineClientSession session)
        {
            DetachTransport();
            if (_options.TransportFactory != null)
            {
                _transport = _options.TransportFactory(session);
                _ownsTransport = false;
            }
            else if (session != null)
            {
                _transport = new RealOnlineSettlementTransport(session);
                _ownsTransport = true;
            }
            if (_transport != null)
            {
                _transport.FrameReceived += OnTransportFrame;
                _transport.Disconnected += OnTransportDisconnected;
            }
        }

        private void DetachTransport()
        {
            if (_transport != null)
            {
                _transport.FrameReceived -= OnTransportFrame;
                _transport.Disconnected -= OnTransportDisconnected;
                if (_ownsTransport)
                {
                    _transport.Dispose();
                }
                _transport = null;
                _ownsTransport = false;
            }
        }

        private void OnTransportFrame(PacketClass packetClass, int packetId, byte[] payload)
        {
            if (_disposed || !IsActive)
            {
                return;
            }
            _inbound.Enqueue(new InboundFrame(packetClass, packetId, payload));
        }

        private void OnTransportDisconnected(OnlineDisconnectReason reason)
        {
            _ = reason;
            if (!_disposed && IsActive)
            {
                Interlocked.Exchange(ref _disconnectedFlag, 1);
            }
        }

        private void ResetActiveState()
        {
            DetachTransport();
            if (_activeRun != null && _activeRun.HudOverlay == (Func<HostedRoomRun, long, System.Collections.Generic.IReadOnlyList<SceneSprite>>)BuildHud)
            {
                _activeRun.HudOverlay = null;
            }
            CancellationTokenSource cts = _sendCts;
            _sendCts = null;
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    cts.Dispose();
                }
            }
            if (_activeSendTask != null)
            {
                ObserveFaulted(_activeSendTask);
                _activeSendTask = null;
            }
            while (_inbound.TryDequeue(out _))
            {
            }
            _ready = null;
            _activeRun = null;
            _camp = null;
            _summary = null;
            _phase = OnlineSettlementPhase.Idle;
            _ageMs = 0.0;
            _hoverRow = -1;
            _pendingChoice = SettlementChoice.None;
            _sentCommand = null;
            _rejectedChoicesMask = 0;
            _sent75CommandMask = 0;
            _sent72 = false;
            _sent73 = false;
            _sent74 = false;
            _sentGiveUp = false;
            _cardSelectSkipped = false;
            _selectedCardIndex = null;
            _gotNoti2 = false;
            _gotUserArea = false;
            _gotAreaUsers = false;
            _returnUserArea = null;
            _selfUniqueId = null;
            _stepStartedAtMs = -1;
            _returnWaitLimitMs = 0;
            _countdownStartPending = false;
            _countdownStarted = false;
            _countdownDeadlineMs = 0;
            _remainingCountdownMs = 0;
            Interlocked.Exchange(ref _disconnectedFlag, 0);
        }

        private static long EffectiveTimeout(long configured, long fallback)
        {
            return configured > 0 ? configured : fallback;
        }

        private static void ObserveFaulted(Task task)
        {
            if (task == null)
            {
                return;
            }
            if (task.IsCompleted)
            {
                _ = task.Exception;
                return;
            }
            task.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void WriteLog(string line)
        {
            Action<string> log = _options.Log;
            if (log == null || string.IsNullOrEmpty(line))
            {
                return;
            }
            try
            {
                log(line);
            }
            catch (Exception)
            {
            }
        }
    }
}
