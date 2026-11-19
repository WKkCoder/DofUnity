using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Data;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>联网进出副本控制器的运行阶段。</summary>
    public enum OnlineDungeonFlowPhase
    {
        Idle = 0,
        Entering = 1,
        InDungeon = 2,
        GivingUp = 3,
        StoppingCombatBeforeRelease = 4,
        ReleasingExclusive = 5,
        HoldingForSettlement = 6,
        SettlingExit = 7,
    }

    /// <summary>传给 <see cref="OnlineDungeonFlowController"/> 建房工厂的上下文。</summary>
    public sealed class OnlineDungeonRoomBuildContext
    {
        public OnlineDungeonRoomBuildContext(
            int dungeonId,
            string dungeonPath,
            int difficulty,
            StartMapNotice startMap,
            OnlineDungeonCombatRunner combatRunner)
        {
            DungeonId = dungeonId;
            DungeonPath = dungeonPath ?? throw new ArgumentNullException(nameof(dungeonPath));
            Difficulty = difficulty;
            StartMap = startMap ?? throw new ArgumentNullException(nameof(startMap));
            CombatRunner = combatRunner ?? throw new ArgumentNullException(nameof(combatRunner));
        }

        public int DungeonId { get; }
        public string DungeonPath { get; }
        public int Difficulty { get; }
        public StartMapNotice StartMap { get; }
        public OnlineDungeonCombatRunner CombatRunner { get; }
    }

    /// <summary>建房工厂的返回结局。</summary>
    public sealed class OnlineDungeonRoomBuildResult
    {
        private OnlineDungeonRoomBuildResult(bool succeeded, OnlineRoomStatus status, OnlineDungeonRoom room, HostedRoomRun run)
        {
            Succeeded = succeeded;
            Status = status;
            Room = room;
            Run = run;
        }

        public bool Succeeded { get; }
        public OnlineRoomStatus Status { get; }
        public OnlineDungeonRoom Room { get; }
        public HostedRoomRun Run { get; }

        public static OnlineDungeonRoomBuildResult Success(OnlineDungeonRoom room, HostedRoomRun run)
        {
            if (room == null)
            {
                throw new ArgumentNullException(nameof(room));
            }
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }
            return new OnlineDungeonRoomBuildResult(true, room.Status, room, run);
        }

        public static OnlineDungeonRoomBuildResult Failure(OnlineRoomStatus status, OnlineDungeonRoom room = null)
        {
            return new OnlineDungeonRoomBuildResult(false, status, room, null);
        }
    }

    /// <summary><see cref="OnlineDungeonFlowController"/> 的可调配置项。</summary>
    public sealed class OnlineDungeonFlowOptions
    {
        public TimeSpan? LoadingNoticeTimeout { get; set; }
        public TimeSpan? DungeonInfoTimeout { get; set; }
        public TimeSpan? StartMapTimeout { get; set; }
        public OnlineDungeonCombatOptions CombatOptions { get; set; }
        public double ReturnDelayMs { get; set; } = DungeonSession.SettlementDelayMs;
        public long TransientNoticeDurationMs { get; set; } = 4000;
        public int QuitWaitTimeoutMs { get; set; } = 500;
        public Action<string> Log { get; set; }
        public Action<string> NetLog { get; set; }
        public Action OnDisconnected { get; set; }
        public Func<TownAreaInfo> CurrentArea { get; set; }
        public Func<int> HeroLevel { get; set; }
        public TownAtlas Atlas { get; set; }
        public TownPlace? ReturnPlace { get; set; }
        public IGlyphFonts Fonts { get; set; }
    }

    /// <summary>
    /// 按真实选中的 <see cref="DungeonEntry.Path"/> 装配联网多房间与懒加载单房间兜底(r18-dungeonflow):
    /// 不在 <see cref="OnlineDungeonRoom.CreateRun"/> 之前提前跑 <c>PickRoomMap</c> 预检,保证多房间能走时不因单房预检误判。
    /// </summary>
    public static class OnlineDungeonRoomBuilder
    {
        public static OnlineDungeonRoomBuildResult Build(
            OnlineDungeonRoomBuildContext context,
            DnfAssets assets,
            HeroProgress progress,
            int seed,
            bool damageOn,
            double returnDelayMs = DungeonSession.SettlementDelayMs,
            Action<string> roomLog = null)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (assets == null)
            {
                return OnlineDungeonRoomBuildResult.Failure(OnlineRoomStatus.NoRoom);
            }

            DungeonRoomPool pool = DungeonRoomPool.ReadFor(assets, context.DungeonPath);
            string weaponPath = progress?.WeaponPath ?? DemoHero.StarterWeapon;
            var room = new OnlineDungeonRoom(context.CombatRunner);
            OnlineMultiRoomSetup multi = BuildMultiRoom(assets, context.DungeonPath, pool, progress, seed, damageOn, weaponPath, roomLog);

            HostedRoomRun run = room.CreateRun(
                plan => CreateSingleRoomFallback(assets, pool, context.StartMap.MapId, weaponPath, seed, damageOn, progress, plan, roomLog),
                returnDelayMs,
                multi);

            if (run == null || room.Status != OnlineRoomStatus.Ready)
            {
                return OnlineDungeonRoomBuildResult.Failure(room.Status, room);
            }
            return OnlineDungeonRoomBuildResult.Success(room, run);
        }

        private static RoomCombatSession CreateSingleRoomFallback(
            DnfAssets assets,
            DungeonRoomPool pool,
            ushort? mapId,
            string weaponPath,
            int seed,
            bool damageOn,
            HeroProgress progress,
            Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> plan,
            Action<string> roomLog)
        {
            string roomMap = OnlineDungeonRoom.PickRoomMap(pool.Rooms, mapId, path => CountRoomMonsters(assets, path), out _);
            if (roomMap == null)
            {
                SafeLog(roomLog, OnlineDungeonRoom.LogPrefix + " status=NoRoomMap rooms=" + pool.Rooms.Count + " fallback=select");
                return null;
            }
            return DemoCombat.CreateSession(assets, roomMap, weaponPath, seed, damageOn, progress, plan);
        }

        private static OnlineMultiRoomSetup BuildMultiRoom(
            DnfAssets assets,
            string dungeonPath,
            DungeonRoomPool pool,
            HeroProgress progress,
            int seed,
            bool damageOn,
            string weaponPath,
            Action<string> roomLog)
        {
            try
            {
                ScriptLine[] doc = assets.ReadScript(dungeonPath);
                if (doc == null)
                {
                    return null;
                }
                var mazes = new List<DungeonMaze>();
                foreach (DungeonMaze maze in DungeonMaze.ParseAll(dungeonPath, doc))
                {
                    if (maze.QuestConnection < 0)
                    {
                        mazes.Add(maze);
                    }
                }
                if (mazes.Count == 0)
                {
                    return null;
                }
                return new OnlineMultiRoomSetup(
                    mazes,
                    (entry, plan) => CreateMultiRoomSession(assets, pool, progress, seed, damageOn, weaponPath, entry, plan))
                {
                    MapAllowsExits = (mapId, exits) => pool.ById(mapId)?.HasDirection(exits) ?? true,
                    IsBossMap = mapId => pool.ById(mapId)?.IsBoss ?? false,
                    Log = roomLog,
                };
            }
            catch (Exception e)
            {
                SafeLog(roomLog, OnlineDungeonRoom.LogPrefix + " multi failed exception=" + e.GetType().Name + " fallback=single");
                return null;
            }
        }

        private static RoomCombatSession CreateMultiRoomSession(
            DnfAssets assets,
            DungeonRoomPool pool,
            HeroProgress progress,
            int seed,
            bool damageOn,
            string weaponPath,
            OnlineRoomEntry entry,
            Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> plan)
        {
            string mapPath = OnlineDungeonRoom.PickEntryMap(pool.Rooms, entry.MapId, entry.Exits, entry.Monsters > 0, path => CountRoomMonsters(assets, path), out _);
            if (mapPath == null)
            {
                return null;
            }
            RoomMap map = RoomMap.Read(assets, mapPath);
            List<RoomGate> gates = DemoRoom.BuildGates(
                assets,
                map,
                entry.Maze.GateSet,
                side => OnlineDungeonRoom.GateKindAt(entry.Maze, entry.CellX, entry.CellY, side));
            DemoRoom.DefaultSpawn(map, out double x, out double y);
            RoomSession room = DemoRoom.CreateRoom(assets, map, gates, DemoRoom.DefaultOutfit(assets, weaponPath), x, y, false);
            return HostedRoomCombat.CreateSession(assets, room, seed, damageOn, progress, plan);
        }

        private static int CountRoomMonsters(DnfAssets assets, string mapPath)
        {
            try
            {
                return RoomMap.Read(assets, mapPath).Monsters.Count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void SafeLog(Action<string> log, string line)
        {
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

    /// <summary>
    /// 在线副本流控制器(r18-dungeonflow):把 <see cref="DungeonSelectScreen"/>、<see cref="GameFlow"/> 与
    /// <see cref="OnlineClientSession"/> 的独占收包、进副本 5 步、多/单房间战斗、系统菜单放弃回城(1/45 → 0/23+0/24)、
    /// 结算 UI 交接(<see cref="SettlementStarted"/>)串成单一状态机,支持 Retry 多次重入与中途 Quit 安全收尾。
    /// </summary>
    public sealed class OnlineDungeonFlowController : IDisposable
    {
        private enum PendingExitKind
        {
            None = 0,
            EntryFailed = 1,
            RoomBuildFailed = 2,
            Settlement = 3,
            GiveUp = 4,
            CombatAbort = 5,
            RoomAbort = 6,
            Disconnected = 7,
        }

        private readonly IOnlineDungeonSessionPort _port;
        private readonly GameFlow _flow;
        private readonly OnlineDungeonSelectPolicy _policy;
        private readonly Func<OnlineDungeonRoomBuildContext, OnlineDungeonRoomBuildResult> _roomBuilder;
        private readonly OnlineDungeonFlowOptions _options;
        private readonly object _sync = new object();

        private OnlineDungeonFlowPhase _phase = OnlineDungeonFlowPhase.Idle;
        private int _seenPermissionVersion = -1;
        private bool _detachedFlow;
        private bool _disposed;
        private volatile bool _disconnected;
        private bool _disconnectNotified;

        // 当前局运行态
        private CancellationTokenSource _runCts;
        private DungeonEntry _activeEntry;
        private int _activeDungeonId;
        private string _activeDungeonPath;
        private int _activeDifficulty;
        private OnlineDungeonRunner _runner;
        private OnlineDungeonCombatRunner _combatRunner;
        private OnlineDungeonRoom _activeRoom;
        private HostedRoomRun _activeRun;

        private bool _exclusiveActive;
        private volatile bool _exclusiveReleased;
        private volatile bool _holdSettlementForMenu;
        private bool _entryFailedOnCmd15Or16Timeout;

        private Task<OnlineDungeonResult> _enterTask;
        private Task<OnlineDungeonCombatResult> _combatTask;
        private Task<bool> _releaseTask;

        private PendingExitKind _exitKind;
        private OnlineDungeonResult _pendingEntryResult;
        private OnlineDungeonCombatResult _pendingCombatResult;
        private HostedAbort _pendingRoomAbort;

        // 给 Town / Loading / Dungeon 叠层看的瞬态提示(Select 内提示直接放 Select.Notice)
        private DungeonSelectNotice _transientNotice;
        private DungeonSelectNotice _activeRunNotice;
        private bool _pendingSelectNoticeForNextOpen;
        private DungeonSelectNotice _nextSelectNotice;

        /// <summary>进副本结算就绪(且已完成 <c>LeaveExclusiveReceiveAsync</c> 恢复正常收发)后触发,供 <c>r18-settleui</c> 接手。</summary>
        public event Action<OnlineDungeonSettlementReady> SettlementStarted;

        public GameFlow Flow => _flow;
        public OnlineDungeonSelectPolicy Policy => _policy;
        public OnlineDungeonFlowPhase Phase => _phase;
        public int EnterExclusiveCount { get; private set; }
        public int LeaveExclusiveCount { get; private set; }

        /// <summary>当前需要由宿主叠层渲染的进出副本提示(如 Loading 降级时的 Entering 提示、GivingUp 提示、返城后短暂提示)。</summary>
        public DungeonSelectNotice ActiveNotice
        {
            get
            {
                if (_activeRunNotice != null && _activeRunNotice.Kind != DungeonSelectNoticeKind.None)
                {
                    return _activeRunNotice;
                }
                if (_transientNotice != null && _transientNotice.Kind != DungeonSelectNoticeKind.None)
                {
                    return _transientNotice;
                }
                return null;
            }
        }

        public static OnlineDungeonFlowController Create(
            OnlineClientSession session,
            DungeonPermissionSnapshot initialPermissions,
            GameFlow flow,
            OnlineDungeonSelectPolicy policy,
            Func<OnlineDungeonRoomBuildContext, OnlineDungeonRoomBuildResult> roomBuilder,
            OnlineDungeonFlowOptions options = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }
            OnlineDungeonPermissionAdapter.LoadSnapshot(policy.Table, initialPermissions);
            return new OnlineDungeonFlowController(new RealOnlineDungeonSessionPort(session), flow, policy, roomBuilder, options);
        }

        internal OnlineDungeonFlowController(
            IOnlineDungeonSessionPort port,
            GameFlow flow,
            OnlineDungeonSelectPolicy policy,
            Func<OnlineDungeonRoomBuildContext, OnlineDungeonRoomBuildResult> roomBuilder,
            OnlineDungeonFlowOptions options = null)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _roomBuilder = roomBuilder ?? throw new ArgumentNullException(nameof(roomBuilder));
            _options = options ?? new OnlineDungeonFlowOptions();

            _seenPermissionVersion = _policy.Table.Version;

            _port.FrameReceived += OnPortFrameReceived;
            _port.Disconnected += OnPortDisconnected;

            _flow.DifficultyLimit = _policy.DifficultyLimit;
            _flow.ConfigureSelect = ConfigureSelectScreen;
            _flow.OnlineStartDungeon = RequestEnterDungeon;
            _flow.OnlineGiveUpDungeon = RequestGiveUpDungeon;
            _flow.OnlineHoldDungeonExit = false;

            if (_flow.Stage == GameStage.DungeonSelect && _flow.Select != null)
            {
                ConfigureSelectScreen(_flow.Select);
            }
        }

        /// <summary>宿主每帧(推荐在 <see cref="GameFlow.Poll"/> 之前)调用一次:推进异步进图/战斗/放权/放弃收尾并同步许可更新。</summary>
        public void Poll(long nowMs)
        {
            if (_disposed)
            {
                return;
            }

            if (_transientNotice != null && _transientNotice.ExpiresAtMs > 0 && nowMs >= _transientNotice.ExpiresAtMs)
            {
                _transientNotice = null;
            }

            SyncPermissionTableToSelect();

            switch (_phase)
            {
                case OnlineDungeonFlowPhase.Idle:
                    _activeRunNotice = null;
                    break;
                case OnlineDungeonFlowPhase.Entering:
                    PumpEntering(nowMs);
                    break;
                case OnlineDungeonFlowPhase.InDungeon:
                    PumpInDungeon(nowMs);
                    break;
                case OnlineDungeonFlowPhase.GivingUp:
                    PumpGivingUp(nowMs);
                    break;
                case OnlineDungeonFlowPhase.StoppingCombatBeforeRelease:
                    PumpStoppingCombatBeforeRelease(nowMs);
                    break;
                case OnlineDungeonFlowPhase.ReleasingExclusive:
                    PumpReleasingExclusive(nowMs);
                    break;
                case OnlineDungeonFlowPhase.SettlingExit:
                    if (_flow.Stage != GameStage.Dungeon)
                    {
                        ClearRunState();
                        _phase = OnlineDungeonFlowPhase.Idle;
                    }
                    break;
                case OnlineDungeonFlowPhase.HoldingForSettlement:
                    break;
            }

            if (_phase == OnlineDungeonFlowPhase.Idle && _disconnected && !_disconnectNotified && _flow.Stage != GameStage.CharacterSelect)
            {
                TriggerDisconnectCleanup();
            }
        }

        /// <summary>系统菜单"放弃回城"在联网副本里直接触发的入口(同 <see cref="GameFlow.OnlineGiveUpDungeon"/>)。</summary>
        public void RequestGiveUpDungeon()
        {
            if (_disposed)
            {
                return;
            }
            if (_phase != OnlineDungeonFlowPhase.InDungeon || _combatRunner == null)
            {
                return;
            }
            _combatRunner.PostGiveUp();
            _phase = OnlineDungeonFlowPhase.GivingUp;
            _activeRunNotice = new DungeonSelectNotice(DungeonSelectNoticeKind.GivingUp);
        }

        /// <summary>退出前中止正在跑的联网异步任务并有限等待收敛,防止无主异常或退出卡死。</summary>
        public void PrepareForQuit()
        {
            if (_disposed)
            {
                return;
            }
            DetachHandlers();
            CancelAndWaitBounded(_options.QuitWaitTimeoutMs);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DetachHandlers();
            CancelAndWaitBounded(_options.QuitWaitTimeoutMs);
            ClearRunState();
            _phase = OnlineDungeonFlowPhase.Idle;
        }

        private void DetachHandlers()
        {
            if (!_detachedFlow)
            {
                _detachedFlow = true;
                _port.FrameReceived -= OnPortFrameReceived;
                _port.Disconnected -= OnPortDisconnected;
                if (_flow.OnlineStartDungeon == (Action<DungeonEntry, int>)RequestEnterDungeon)
                {
                    _flow.OnlineStartDungeon = null;
                }
                if (_flow.OnlineGiveUpDungeon == (Action)RequestGiveUpDungeon)
                {
                    _flow.OnlineGiveUpDungeon = null;
                }
                if (_flow.ConfigureSelect == (Action<DungeonSelectScreen>)ConfigureSelectScreen)
                {
                    _flow.ConfigureSelect = null;
                }
                _flow.OnlineHoldDungeonExit = false;
            }
        }

        private void ConfigureSelectScreen(DungeonSelectScreen screen)
        {
            if (screen == null)
            {
                return;
            }
            screen.EntryStatus = _policy.EntryStatus;
            screen.EntryDisplayName = _policy.EntryDisplayName;
            screen.RegionDisplayName = _policy.RegionDisplayName;
            if (_options.Fonts != null)
            {
                screen.Fonts = _options.Fonts;
            }
            screen.RefreshDifficulty();
            if (_pendingSelectNoticeForNextOpen)
            {
                screen.Notice = _nextSelectNotice;
                _pendingSelectNoticeForNextOpen = false;
                _nextSelectNotice = null;
            }
        }

        private void SyncPermissionTableToSelect()
        {
            int version = _policy.Table.Version;
            if (version == _seenPermissionVersion)
            {
                return;
            }
            _seenPermissionVersion = version;
            if (_flow.Stage == GameStage.DungeonSelect && _flow.Select != null)
            {
                _flow.Select.RefreshDifficulty();
            }
        }

        private void RequestEnterDungeon(DungeonEntry entry, int difficulty)
        {
            if (_disposed || entry == null || _phase != OnlineDungeonFlowPhase.Idle)
            {
                return;
            }

            DungeonEntryStatus status = _policy.EntryStatus(entry);
            int maxDiff = _policy.DifficultyLimit(entry);
            int clampedDiff = Math.Max(0, Math.Min(difficulty, Math.Min(maxDiff, DungeonSelectScreen.MaxDifficultyIndex)));

            if (status.Disabled)
            {
                WriteLog(OnlineDungeonLog.Select("blocked", clampedDiff, maxDiff, status.Kind.ToString()));
                if (_flow.Stage == GameStage.DungeonSelect && _flow.Select != null)
                {
                    _flow.Select.Busy = false;
                    _flow.Select.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.None);
                }
                else
                {
                    _flow.FailOnlineDungeonStart(entry, clampedDiff, null, "select blocked " + status.Kind);
                }
                return;
            }

            if (_disconnected || _port.IsDisconnected)
            {
                WriteLog(OnlineDungeonLog.Fail("Disconnected", "PreEnter"));
                _nextSelectNotice = new DungeonSelectNotice(DungeonSelectNoticeKind.Disconnected);
                TriggerDisconnectCleanup();
                return;
            }

            int dungeonId = _policy.ResolveDungeonId(entry);
            if (dungeonId < 0 || dungeonId > ushort.MaxValue)
            {
                WriteLog(OnlineDungeonLog.Fail("MissingDungeonId", "ResolveId"));
                _flow.FailOnlineDungeonStart(
                    entry,
                    clampedDiff,
                    new DungeonSelectNotice(DungeonSelectNoticeKind.Failed, null),
                    "online missing id");
                return;
            }

            TownAreaInfo area = _options.CurrentArea?.Invoke();
            int heroLevel = _options.HeroLevel?.Invoke() ?? 1;
            bool isEntranceArea = OnlineTownAreaRules.IsDungeonEntranceArea(area, heroLevel);
            string areaKindToken = area != null ? area.Kind.ToString() : "unknown";

            WriteLog(OnlineDungeonLog.Select("enter", clampedDiff, maxDiff));
            WriteLog(OnlineDungeonLog.Enter(areaKindToken, isEntranceArea, clampedDiff));

            ClearRunState();
            _activeEntry = entry;
            _activeDungeonId = dungeonId;
            _activeDungeonPath = entry.Path;
            _activeDifficulty = clampedDiff;
            _exclusiveActive = false;
            _exclusiveReleased = false;
            _holdSettlementForMenu = false;
            _entryFailedOnCmd15Or16Timeout = false;
            _transientNotice = null;
            _activeRunNotice = new DungeonSelectNotice(DungeonSelectNoticeKind.Entering);
            _runCts = new CancellationTokenSource();
            _phase = OnlineDungeonFlowPhase.Entering;

            _flow.BeginOnlineDungeonLoading(entry, clampedDiff, "online enter");
            if (_flow.Stage == GameStage.Loading)
            {
                _activeRunNotice = null;   // Loading 阶段自带整屏过渡画,无需重复叠提示
            }

            CancellationToken token = _runCts.Token;
            _enterTask = EnterAndStartCombatAsync(dungeonId, clampedDiff, token);
        }

        private async Task<OnlineDungeonResult> EnterAndStartCombatAsync(int dungeonId, int difficulty, CancellationToken ct)
        {
            await _port.EnterExclusiveReceiveAsync(ct).ConfigureAwait(false);
            lock (_sync)
            {
                _exclusiveActive = true;
                EnterExclusiveCount++;
            }

            OnlineDungeonRunner runner = _port.CreateDungeonRunner();
            _runner = runner;
            OnlineDungeonCombatRunner combatRunner = runner.UseCombat(CloneCombatOptions(_options.CombatOptions));
            _combatRunner = combatRunner;

            OnlineDungeonResult entryResult = await runner.EnterAsync(
                dungeonId,
                difficulty,
                _options.NetLog,
                ct,
                _options.LoadingNoticeTimeout,
                _options.DungeonInfoTimeout,
                _options.StartMapTimeout).ConfigureAwait(false);

            if (entryResult != null && entryResult.Entered && entryResult.StartMap != null && !ct.IsCancellationRequested)
            {
                _combatTask = combatRunner.RunAsync(entryResult.StartMap, _options.NetLog, ct);
            }
            return entryResult;
        }

        private void PumpEntering(long nowMs)
        {
            if (_enterTask == null || !_enterTask.IsCompleted)
            {
                return;
            }

            OnlineDungeonResult entryResult = null;
            bool canceled = _enterTask.IsCanceled;
            Exception fault = _enterTask.IsFaulted ? _enterTask.Exception?.GetBaseException() : null;
            if (!canceled && fault == null)
            {
                entryResult = _enterTask.Result;
            }

            if (fault != null || canceled || entryResult == null)
            {
                WriteLog(OnlineDungeonLog.Fail(canceled ? "Cancelled" : (fault != null ? fault.GetType().Name : "NullResult"), "Enter"));
                BeginReleaseOrFinish(
                    PendingExitKind.EntryFailed,
                    entryResult,
                    new DungeonSelectNotice(canceled ? DungeonSelectNoticeKind.Failed : DungeonSelectNoticeKind.Disconnected));
                return;
            }

            if (!entryResult.Entered || entryResult.StartMap == null)
            {
                HandleEntryRejectedOrFailed(entryResult);
                return;
            }

            if (_combatRunner == null || _combatRunner.Machine == null)
            {
                WriteLog(OnlineDungeonLog.Fail("CombatNotReady", "Machine"));
                CancelRunCts();
                BeginReleaseOrFinish(
                    PendingExitKind.RoomBuildFailed,
                    entryResult,
                    new DungeonSelectNotice(DungeonSelectNoticeKind.RoomFailed));
                return;
            }

            OnlineDungeonRoomBuildResult build;
            try
            {
                var ctx = new OnlineDungeonRoomBuildContext(
                    _activeDungeonId,
                    _activeDungeonPath,
                    _activeDifficulty,
                    entryResult.StartMap,
                    _combatRunner);
                build = _roomBuilder(ctx);
            }
            catch (Exception e)
            {
                WriteLog(OnlineDungeonLog.Fail("RoomBuildException", e.GetType().Name));
                build = OnlineDungeonRoomBuildResult.Failure(OnlineRoomStatus.NoRoom);
            }

            if (build == null || !build.Succeeded || build.Room == null || build.Run == null || build.Room.Status != OnlineRoomStatus.Ready)
            {
                OnlineRoomStatus status = build?.Status ?? OnlineRoomStatus.NoRoom;
                WriteLog(OnlineDungeonLog.Room(status.ToString(), build?.Room?.MultiRoom ?? false, build?.Room?.Spawned ?? 0, build?.Room?.Expected ?? 0));
                WriteLog(OnlineDungeonLog.Fail("RoomBuildFailed", status.ToString()));
                _activeRoom = build?.Room;
                _activeRun = build?.Run;
                CancelRunCts();
                TransitionToStopCombatThenRelease(PendingExitKind.RoomBuildFailed, entryResult);
                return;
            }

            _activeRoom = build.Room;
            _activeRun = build.Run;
            _activeRoom.CanFinish = () => _exclusiveReleased && !_holdSettlementForMenu;

            WriteLog(OnlineDungeonLog.Room(
                _activeRoom.Status.ToString(),
                _activeRoom.MultiRoom,
                _activeRoom.Spawned,
                _activeRoom.Expected,
                _activeRun.RoomsEntered));

            _activeRunNotice = null;
            _phase = OnlineDungeonFlowPhase.InDungeon;
            _flow.CompleteOnlineDungeonStart(_activeDungeonPath, _activeDifficulty, _activeRun, "online enter");
        }

        private void HandleEntryRejectedOrFailed(OnlineDungeonResult entryResult)
        {
            string step = entryResult.Stage.ToString();
            string reason = entryResult.Failure.ToString();
            WriteLog(OnlineDungeonLog.Fail(reason, step, entryResult.RejectionCode));

            DungeonSelectNotice notice;
            PendingExitKind kind = PendingExitKind.EntryFailed;
            if (entryResult.Failure == OnlineDungeonFailure.Rejected)
            {
                if (entryResult.RejectedPacketId == OnlineDungeonEntry.SelectDungeonPacketId)
                {
                    // CMD 16 拒绝才把该副本在本局内标成不可进入;CMD 15 拒绝绝不灰显。
                    _policy.Table.MarkServerRejected(_activeDungeonId, entryResult.RejectionCode);
                    _seenPermissionVersion = _policy.Table.Version;
                }
                notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected);
            }
            else if (entryResult.Failure == OnlineDungeonFailure.Timeout)
            {
                _entryFailedOnCmd15Or16Timeout = true;
                notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);
            }
            else if (entryResult.Failure == OnlineDungeonFailure.ConnectionClosed || entryResult.Failure == OnlineDungeonFailure.FramingFault)
            {
                kind = PendingExitKind.Disconnected;
                notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Disconnected);
            }
            else
            {
                notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Failed);
            }

            _pendingEntryResult = entryResult;
            BeginReleaseOrFinish(kind, entryResult, notice);
        }

        private void PumpInDungeon(long nowMs)
        {
            if (_combatTask != null && _combatTask.IsCompleted)
            {
                OnlineDungeonCombatResult combatResult = ExtractCombatResult(_combatTask);
                _pendingCombatResult = combatResult;
                if (combatResult != null && combatResult.Outcome == OnlineDungeonCombatOutcome.ReturnReady)
                {
                    TransitionToRelease(PendingExitKind.Settlement);
                    return;
                }
                if (combatResult != null && combatResult.Outcome == OnlineDungeonCombatOutcome.GaveUp)
                {
                    TransitionToRelease(PendingExitKind.GiveUp);
                    return;
                }
                if (IsDisconnectCombatOutcome(combatResult))
                {
                    TransitionToRelease(PendingExitKind.Disconnected);
                    return;
                }
                TransitionToRelease(PendingExitKind.CombatAbort);
                return;
            }

            if (_activeRun != null && _activeRun.Failed)
            {
                _pendingRoomAbort = _activeRun.Aborted ? _activeRun.AbortReason : HostedAbort.None;
                CancelRunCts();
                TransitionToStopCombatThenRelease(
                    _activeRun.Aborted ? PendingExitKind.RoomAbort : PendingExitKind.CombatAbort,
                    null);
                return;
            }

            if (_disconnected || _port.IsDisconnected)
            {
                CancelRunCts();
                TransitionToStopCombatThenRelease(PendingExitKind.Disconnected, null);
            }
        }

        private void PumpGivingUp(long nowMs)
        {
            if (_combatTask == null || !_combatTask.IsCompleted)
            {
                if (_disconnected || _port.IsDisconnected)
                {
                    CancelRunCts();
                    TransitionToStopCombatThenRelease(PendingExitKind.Disconnected, null);
                }
                return;
            }

            _pendingCombatResult = ExtractCombatResult(_combatTask);
            if (IsDisconnectCombatOutcome(_pendingCombatResult))
            {
                TransitionToRelease(PendingExitKind.Disconnected);
            }
            else
            {
                TransitionToRelease(PendingExitKind.GiveUp);
            }
        }

        private void PumpStoppingCombatBeforeRelease(long nowMs)
        {
            if (_combatTask == null || _combatTask.IsCompleted)
            {
                if (_combatTask != null && _pendingCombatResult == null)
                {
                    _pendingCombatResult = ExtractCombatResult(_combatTask);
                }
                TransitionToRelease(_exitKind);
            }
        }

        private void PumpReleasingExclusive(long nowMs)
        {
            if (_releaseTask != null && !_releaseTask.IsCompleted)
            {
                return;
            }

            bool releasedOk = _releaseTask == null || (!_releaseTask.IsFaulted && !_releaseTask.IsCanceled && _releaseTask.Result);
            PendingExitKind kind = _exitKind;

            switch (kind)
            {
                case PendingExitKind.EntryFailed:
                {
                    DungeonEntry entry = _activeEntry;
                    int diff = _activeDifficulty;
                    DungeonSelectNotice notice = _activeRunNotice ?? new DungeonSelectNotice(
                        _entryFailedOnCmd15Or16Timeout ? DungeonSelectNoticeKind.Timeout : DungeonSelectNoticeKind.Failed);
                    ClearRunState();
                    _phase = OnlineDungeonFlowPhase.Idle;
                    if (!releasedOk || _disconnected || _port.IsDisconnected)
                    {
                        TriggerDisconnectCleanup();
                    }
                    else
                    {
                        _flow.FailOnlineDungeonStart(entry, diff, notice, "online enter failed");
                    }
                    break;
                }

                case PendingExitKind.RoomBuildFailed:
                {
                    DungeonEntry entry = _activeEntry;
                    int diff = _activeDifficulty;
                    ClearRunState();
                    _phase = OnlineDungeonFlowPhase.Idle;
                    if (!releasedOk || _disconnected || _port.IsDisconnected)
                    {
                        TriggerDisconnectCleanup();
                    }
                    else
                    {
                        _flow.FailOnlineDungeonStart(
                            entry,
                            diff,
                            new DungeonSelectNotice(DungeonSelectNoticeKind.RoomFailed),
                            "online room build failed");
                    }
                    break;
                }

                case PendingExitKind.GiveUp:
                {
                    OnlineDungeonCombatResult combat = _pendingCombatResult;
                    int reported = _activeRun?.Reported ?? combat?.Sent ?? 0;
                    int dropped = _activeRun?.Dropped ?? combat?.Refused ?? 0;
                    int rooms = _activeRun?.RoomsEntered ?? (combat != null ? combat.Room + 1 : 1);
                    string reason = combat != null ? combat.Outcome.ToString() : "GiveUp";
                    WriteLog(OnlineDungeonLog.Leave(reason, reported, dropped, rooms));
                    WriteLog(OnlineDungeonLog.ReturnArea(ClassifyReturnArea(combat?.UserArea)));
                    bool timedOut = combat != null && combat.Outcome == OnlineDungeonCombatOutcome.Timeout;

                    ClearRunState();
                    _phase = OnlineDungeonFlowPhase.Idle;
                    if (!releasedOk || _disconnected || _port.IsDisconnected)
                    {
                        TriggerDisconnectCleanup();
                    }
                    else
                    {
                        if (timedOut)
                        {
                            SetTransientTownNotice(DungeonSelectNoticeKind.Timeout, nowMs);
                        }
                        _flow.CompleteOnlineGiveUp("online giveup");
                    }
                    break;
                }

                case PendingExitKind.CombatAbort:
                case PendingExitKind.RoomAbort:
                {
                    OnlineDungeonCombatResult combat = _pendingCombatResult;
                    int reported = _activeRun?.Reported ?? combat?.Sent ?? 0;
                    int dropped = _activeRun?.Dropped ?? combat?.Refused ?? 0;
                    int rooms = _activeRun?.RoomsEntered ?? (combat != null ? combat.Room + 1 : 1);
                    string reason = kind == PendingExitKind.RoomAbort
                        ? _pendingRoomAbort.ToString()
                        : (combat != null ? combat.Outcome.ToString() : "Aborted");
                    WriteLog(OnlineDungeonLog.Leave(reason, reported, dropped, rooms));
                    WriteLog(OnlineDungeonLog.Fail(reason, "Combat"));
                    DungeonSelectNoticeKind kindNotice = combat != null && combat.Outcome == OnlineDungeonCombatOutcome.Timeout
                        ? DungeonSelectNoticeKind.Timeout
                        : DungeonSelectNoticeKind.Aborted;

                    ClearRunState();
                    _phase = OnlineDungeonFlowPhase.Idle;
                    if (!releasedOk || _disconnected || _port.IsDisconnected)
                    {
                        TriggerDisconnectCleanup();
                    }
                    else
                    {
                        SetTransientTownNotice(kindNotice, nowMs);
                        _flow.CompleteOnlineDungeonAbort("online abort " + reason);
                    }
                    break;
                }

                case PendingExitKind.Disconnected:
                {
                    OnlineDungeonCombatResult combat = _pendingCombatResult;
                    int reported = _activeRun?.Reported ?? combat?.Sent ?? 0;
                    int dropped = _activeRun?.Dropped ?? combat?.Refused ?? 0;
                    int rooms = _activeRun?.RoomsEntered ?? (combat != null ? combat.Room + 1 : 1);
                    WriteLog(OnlineDungeonLog.Leave("Disconnected", reported, dropped, rooms));
                    WriteLog(OnlineDungeonLog.Fail("Disconnected", "Session"));
                    ClearRunState();
                    _phase = OnlineDungeonFlowPhase.Idle;
                    TriggerDisconnectCleanup();
                    break;
                }

                case PendingExitKind.Settlement:
                {
                    if (!releasedOk || _disconnected || _port.IsDisconnected)
                    {
                        ClearRunState();
                        _phase = OnlineDungeonFlowPhase.Idle;
                        TriggerDisconnectCleanup();
                        return;
                    }

                    OnlineDungeonCombatResult combat = _pendingCombatResult;
                    int reported = _activeRun?.Reported ?? combat?.Sent ?? 0;
                    int dropped = _activeRun?.Dropped ?? combat?.Refused ?? 0;
                    int rooms = _activeRun?.RoomsEntered ?? (combat != null ? combat.Room + 1 : 1);
                    OnlineDungeonSettlementData data = OnlineDungeonSettlementMapper.Map(
                        _activeDungeonId,
                        _activeDungeonPath,
                        _activeDifficulty,
                        reported,
                        dropped,
                        rooms,
                        combat);

                    WriteLog(OnlineDungeonLog.Leave(
                        "ReturnReady",
                        reported,
                        dropped,
                        rooms,
                        data.ResultParseState.ToString(),
                        data.RewardParseState.ToString()));

                    // 只在 LeaveExclusiveReceiveAsync 已完成之后才放行 CanFinish 与触发 SettlementStarted。
                    _exclusiveReleased = true;
                    _activeRunNotice = null;

                    var ready = new OnlineDungeonSettlementReady(data, _port.RawSession, CompleteSettlementChoice);
                    try
                    {
                        SettlementStarted?.Invoke(ready);
                    }
                    catch (Exception e)
                    {
                        WriteLog(OnlineDungeonLog.Fail("SettlementHandlerFault", e.GetType().Name));
                    }

                    if (ready.HoldForMenu)
                    {
                        _holdSettlementForMenu = true;
                        _flow.OnlineHoldDungeonExit = true;
                        _phase = OnlineDungeonFlowPhase.HoldingForSettlement;
                    }
                    else
                    {
                        _holdSettlementForMenu = false;
                        _flow.OnlineHoldDungeonExit = false;
                        _phase = OnlineDungeonFlowPhase.SettlingExit;
                    }
                    break;
                }

                default:
                    ClearRunState();
                    _phase = OnlineDungeonFlowPhase.Idle;
                    break;
            }
        }

        private void CompleteSettlementChoice(SettlementChoice choice)
        {
            if (_disposed)
            {
                return;
            }
            if (_phase != OnlineDungeonFlowPhase.HoldingForSettlement && _phase != OnlineDungeonFlowPhase.SettlingExit)
            {
                return;
            }
            ClearRunState();
            _phase = OnlineDungeonFlowPhase.Idle;
            _flow.CompleteOnlineSettlement(choice, "online settlement " + choice);
        }

        private void BeginReleaseOrFinish(PendingExitKind exitKind, OnlineDungeonResult entryResult, DungeonSelectNotice notice)
        {
            _pendingEntryResult = entryResult;
            _activeRunNotice = notice;
            TransitionToRelease(exitKind);
        }

        private void TransitionToStopCombatThenRelease(PendingExitKind exitKind, OnlineDungeonResult entryResult)
        {
            _exitKind = exitKind;
            _pendingEntryResult = entryResult;
            if (_combatTask != null && !_combatTask.IsCompleted)
            {
                _phase = OnlineDungeonFlowPhase.StoppingCombatBeforeRelease;
            }
            else
            {
                TransitionToRelease(exitKind);
            }
        }

        private void TransitionToRelease(PendingExitKind exitKind)
        {
            _exitKind = exitKind;
            _phase = OnlineDungeonFlowPhase.ReleasingExclusive;
            bool needRelease;
            lock (_sync)
            {
                needRelease = _exclusiveActive;
            }
            _releaseTask = needRelease ? ReleaseExclusiveAsync() : Task.FromResult(true);
        }

        private async Task<bool> ReleaseExclusiveAsync()
        {
            try
            {
                await _port.LeaveExclusiveReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    _exclusiveActive = false;
                    LeaveExclusiveCount++;
                }
                return true;
            }
            catch (ObjectDisposedException)
            {
                lock (_sync)
                {
                    _exclusiveActive = false;
                }
                return false;
            }
            catch (InvalidOperationException)
            {
                lock (_sync)
                {
                    _exclusiveActive = false;
                }
                return false;
            }
            catch (Exception e)
            {
                WriteLog(OnlineDungeonLog.Fail("LeaveExclusiveFault", e.GetType().Name));
                lock (_sync)
                {
                    _exclusiveActive = false;
                }
                return false;
            }
        }

        private string ClassifyReturnArea(UserAreaNotice userArea)
        {
            if (userArea == null || !_options.ReturnPlace.HasValue)
            {
                return "unknown";
            }
            TownPlace expected = _options.ReturnPlace.Value;
            if (_options.Atlas != null)
            {
                if (!OnlineTownAreaRules.TryMapToServerArea(_options.Atlas, expected, out byte village, out byte area))
                {
                    return "unknown";
                }
                return userArea.Village == village && userArea.Area == area ? "match" : "mismatch";
            }
            if (expected.Town < 0 || expected.Town > byte.MaxValue || expected.Area < 0 || expected.Area > byte.MaxValue)
            {
                return "unknown";
            }
            return userArea.Village == (byte)expected.Town && userArea.Area == (byte)expected.Area ? "match" : "mismatch";
        }

        private void SetTransientTownNotice(DungeonSelectNoticeKind kind, long nowMs)
        {
            long duration = _options.TransientNoticeDurationMs > 0 ? _options.TransientNoticeDurationMs : 4000;
            _transientNotice = new DungeonSelectNotice(kind, null, nowMs + duration);
        }

        private void TriggerDisconnectCleanup()
        {
            if (_disconnectNotified)
            {
                return;
            }
            _disconnectNotified = true;
            _activeRunNotice = null;
            _flow.OnlineHoldDungeonExit = false;
            _options.OnDisconnected?.Invoke();
        }

        private void ClearRunState()
        {
            CancelRunCts();
            if (_activeRoom != null)
            {
                _activeRoom.CanFinish = null;
            }
            _flow.OnlineHoldDungeonExit = false;
            _activeEntry = null;
            _activeDungeonId = 0;
            _activeDungeonPath = null;
            _activeDifficulty = 0;
            _runner = null;
            _combatRunner = null;
            _activeRoom = null;
            _activeRun = null;
            _enterTask = null;
            _combatTask = null;
            _releaseTask = null;
            _exclusiveReleased = false;
            _holdSettlementForMenu = false;
            _entryFailedOnCmd15Or16Timeout = false;
            _exitKind = PendingExitKind.None;
            _pendingEntryResult = null;
            _pendingCombatResult = null;
            _pendingRoomAbort = HostedAbort.None;
            _activeRunNotice = null;
        }

        private void CancelRunCts()
        {
            CancellationTokenSource cts = _runCts;
            _runCts = null;
            if (cts == null)
            {
                return;
            }
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

        private void CancelAndWaitBounded(int timeoutMs)
        {
            CancelRunCts();
            var pending = new List<Task>(3);
            if (_enterTask != null)
            {
                ObserveFaulted(_enterTask);
                pending.Add(_enterTask);
            }
            if (_combatTask != null)
            {
                ObserveFaulted(_combatTask);
                pending.Add(_combatTask);
            }
            if (_releaseTask != null)
            {
                ObserveFaulted(_releaseTask);
                pending.Add(_releaseTask);
            }
            if (pending.Count == 0 || timeoutMs <= 0)
            {
                return;
            }
            try
            {
                Task.WhenAll(pending).Wait(timeoutMs);
            }
            catch (Exception)
            {
            }
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

        private void OnPortFrameReceived(PacketClass packetClass, int packetId, byte[] payload)
        {
            if (_disposed)
            {
                return;
            }
            if (packetClass != PacketClass.Noti || packetId != DungeonPermissionParser.PacketId)
            {
                return;
            }
            OnlineDungeonPermissionAdapter.TryApplyNotificationPayload(_policy.Table, payload, out bool parsed);
            WriteLog(OnlineDungeonLog.PermissionUpdate(_policy.Table.EntryCount, parsed ? 0 : 1));
        }

        private void OnPortDisconnected(OnlineDisconnectReason reason)
        {
            _disconnected = true;
            WriteLog(OnlineDungeonLog.Fail("Disconnected", reason.ToString()));
        }

        private static OnlineDungeonCombatOptions CloneCombatOptions(OnlineDungeonCombatOptions src)
        {
            if (src == null)
            {
                return new OnlineDungeonCombatOptions();
            }
            return new OnlineDungeonCombatOptions
            {
                PollInterval = src.PollInterval,
                SettlementTimeout = src.SettlementTimeout,
                OverallTimeout = src.OverallTimeout,
                KillPayload = src.KillPayload,
                SettlementPayload = src.SettlementPayload,
                MovePayload = src.MovePayload,
                LoadedPayload = src.LoadedPayload,
                AcknowledgeRoomLoaded = src.AcknowledgeRoomLoaded,
                AcknowledgeStartRoomLoaded = src.AcknowledgeStartRoomLoaded,
                MaxMoveRefusalsPerRoom = src.MaxMoveRefusalsPerRoom,
                RoomHandshakeTimeout = src.RoomHandshakeTimeout,
                GiveUpTimeout = src.GiveUpTimeout,
                OnEvent = src.OnEvent,
            };
        }

        private static OnlineDungeonCombatResult ExtractCombatResult(Task<OnlineDungeonCombatResult> task)
        {
            if (task == null || !task.IsCompleted || task.IsCanceled || task.IsFaulted)
            {
                _ = task?.Exception;
                return null;
            }
            return task.Result;
        }

        private static bool IsDisconnectCombatOutcome(OnlineDungeonCombatResult result)
        {
            if (result == null)
            {
                return false;
            }
            return result.Outcome == OnlineDungeonCombatOutcome.ConnectionClosed
                || result.Outcome == OnlineDungeonCombatOutcome.FramingFault
                || result.Outcome == OnlineDungeonCombatOutcome.SendFailed;
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
