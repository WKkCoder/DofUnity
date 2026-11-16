using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>联网副本房间接线的结局(<see cref="OnlineDungeonRoom.Status"/>)。</summary>
    public enum OnlineRoomStatus
    {
        /// <summary>还没造过(或联网战斗的状态机还没建好)。</summary>
        NotReady = 0,

        /// <summary>房间造好了:至少有一只本地怪与联网对应表里的记录对上了,<see cref="OnlineDungeonRoom.Run"/> 可用。</summary>
        Ready = 1,

        /// <summary>起始地图里没有怪物记录:没有可摆的怪,也没有要报的击杀,不进联网房间。</summary>
        NoMonsters = 2,

        /// <summary>宿主的房间工厂没造出房间(返回 null)。</summary>
        NoRoom = 3,

        /// <summary>房间造出来了,但里面一只能对上记录的本地怪都没有(房间地图没有出生点模板,或模板里的怪物全造不出来):不进联网房间。</summary>
        NothingSpawned = 4,
    }

    /// <summary>
    /// 宿主造房间的工厂:拿到出生点计划(要原样交给 <see cref="RoomCombatSession.Create"/> 的 <c>spawnPlan</c> 参数,
    /// 例如 <c>DemoCombat.CreateSession(..., spawnPlan: plan)</c>),返回造好的房间会话;造不出来返回 null。
    /// </summary>
    public delegate RoomCombatSession OnlineRoomFactory(Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> spawnPlan);

    /// <summary>
    /// 联网多房间副本里宿主造某一间房时要知道的事(<see cref="OnlineRoomEntryFactory"/> 的参数,r18-hostmulti):这是联网状态机的第几间房、在迷宫的哪一格、
    /// 服务端给的地图编号、主角从哪一侧出口进来、通向下一间的出口在哪一侧(没有就是最后一间)、对上了的本地迷宫(出口物件编号、每格的出口码都在它里面)。
    /// 房间里要摆哪几侧出口、每侧画什么,由 <see cref="OnlineDungeonRoom.GateKindAt"/> 按这个迷宫给(与离线副本同一条规则);<see cref="Exit"/> 那一侧必须摆上出口物件,否则主角走不出去。
    /// </summary>
    public sealed class OnlineRoomEntry
    {
        public OnlineRoomEntry(int room, int cellX, int cellY, int mapId, GateSide? entry, GateSide? exit, DungeonMaze maze, int monsters)
        {
            Room = room;
            CellX = cellX;
            CellY = cellY;
            MapId = mapId;
            Entry = entry;
            Exit = exit;
            Maze = maze ?? throw new ArgumentNullException(nameof(maze));
            Monsters = monsters;
        }

        /// <summary>联网状态机的房间序号(起始房间 0)。</summary>
        public int Room { get; }

        /// <summary>迷宫格坐标(服务端起始地图通知里的格子)。</summary>
        public int CellX { get; }

        public int CellY { get; }

        /// <summary>起始地图通知里的地图编号;通知里没有为 −1。</summary>
        public int MapId { get; }

        /// <summary>主角从哪一侧出口进来;起始房间为 null。</summary>
        public GateSide? Entry { get; }

        /// <summary>通向下一间的出口在哪一侧;最后一间为 null。</summary>
        public GateSide? Exit { get; }

        /// <summary>对上了的本地迷宫(候选不止一个时是第一个)。</summary>
        public DungeonMaze Maze { get; }

        /// <summary>这一格的出口码(<see cref="ExitBits"/> 的组合)。</summary>
        public int Exits => Maze.ExitsAt(CellX, CellY);

        /// <summary>这间房的怪表有几只怪(服务端起始地图通知里的怪记录数);可以是 0(后面的房间可以是空房间)。</summary>
        public int Monsters { get; }

        public bool IsStart => !Entry.HasValue;

        public bool IsLast => !Exit.HasValue;
    }

    /// <summary>
    /// 联网多房间副本里宿主造某一间房的工厂:<paramref name="entry"/> 说明是哪一间,<paramref name="spawnPlan"/> 要原样交给 <see cref="RoomCombatSession.Create"/>。
    /// 造不出来返回 null(起始房间回到单房间的老路,之后的房间放弃这一局);工厂自己抛的异常同样按"造不出来"处理。
    /// </summary>
    public delegate RoomCombatSession OnlineRoomEntryFactory(OnlineRoomEntry entry, Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> spawnPlan);

    /// <summary>
    /// 联网多房间副本的宿主给的材料(r18-hostmulti):候选迷宫(<see cref="Mazes"/>,默认迷宫;一个副本定义可以有好几个,服务端开局随机挑一个)、造房间的工厂、
    /// 几个可选的数据查询(这张图登记在哪些出口码下、是不是首领房间)、日志出口、等换房的上限。不给数据查询就不做对应的核对。
    /// </summary>
    public sealed class OnlineMultiRoomSetup
    {
        /// <summary>等换房结果(新房间或被拒)的缺省上限(毫秒,墙钟):30 秒。联网层自己的换房与装载握手各有 10 秒的超时,这是它们都不起作用的情形(例如一直排不进闸门)的兜底。</summary>
        public const double DefaultMoveWaitLimitMs = 30000.0;

        private double _moveWaitLimitMs = DefaultMoveWaitLimitMs;

        public OnlineMultiRoomSetup(IReadOnlyList<DungeonMaze> mazes, OnlineRoomEntryFactory createRoom)
        {
            Mazes = mazes ?? throw new ArgumentNullException(nameof(mazes));
            CreateRoom = createRoom ?? throw new ArgumentNullException(nameof(createRoom));
        }

        /// <summary>候选迷宫(默认迷宫;任务迷宫不用)。</summary>
        public IReadOnlyList<DungeonMaze> Mazes { get; }

        /// <summary>造房间的工厂。</summary>
        public OnlineRoomEntryFactory CreateRoom { get; }

        /// <summary>(地图编号, 出口码)→ 这张图是不是登记在这个出口码下;地图不认识要返回 true。null 不核对。</summary>
        public Func<int, int, bool> MapAllowsExits { get; set; }

        /// <summary>地图编号 → 是不是首领房间(是就是最后一间);null 不用这条线索。</summary>
        public Func<int, bool> IsBossMap { get; set; }

        /// <summary>日志出口(一行一调用,行首 <see cref="OnlineDungeonRoom.LogPrefix"/>);null 不写日志。</summary>
        public Action<string> Log { get; set; }

        /// <summary>等换房结果的上限(毫秒,墙钟);必须是有限的正数。</summary>
        /// <exception cref="ArgumentOutOfRangeException">不是有限的正数。</exception>
        public double MoveWaitLimitMs
        {
            get { return _moveWaitLimitMs; }
            set
            {
                if (!(value > 0.0) || double.IsInfinity(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "等换房的上限必须是有限的正数");
                }
                _moveWaitLimitMs = value;
            }
        }
    }

    /// <summary>
    /// 联网副本的房间接线(r15-dungeonhost,副本宿主呈现合流):把联网战斗的状态机(<see cref="OnlineDungeonCombatRunner"/>,协议与对应表都在联网层)
    /// 和 Play 层既有的房间怪物/离线战斗表示(<see cref="RoomCombatSession"/>)接起来,不新造任何战斗:
    /// <list type="number">
    /// <item>怪物:对应表里有几只怪(起始地图的怪记录),房间里就造几只本地怪——按 <see cref="RoomSpawnPlan"/> 用房间地图自己的出生点当模板;
    /// 第 i 只本地怪的 <see cref="MonsterActor.Index"/> 就是对应表的本地编号 i,造不出来的只是缺席、不让后面的序号前移(<see cref="Missing"/>)。
    /// 本地编号怎么变成服务端编号只有联网层一处在做(<see cref="OnlineCombatMonster.ServerId"/>),这里不碰、不复制。</item>
    /// <item>击杀:本地怪死亡由 <see cref="RoomKillWatcher"/> 观察(包在 <see cref="HostedRoomRun"/> 里),每只怪只交一次序号,
    /// 序号经 <see cref="OnlineDungeonCombatRunner.PostKill"/> 排给状态机;状态机拦下的(编号不在表里、重复编号、已报过、结算已发出)不发包,这里也不绕过它。</item>
    /// <item>回城:状态机走到 <see cref="OnlineCombatPhase.Settled"/>(两个结算通知收齐)并且跑器已结束,这一局才算完,<see cref="HostedRoomRun.Choice"/> 变成回城;
    /// 在此之前本地怪杀光了也不回城——清图要等服务端的清图通知,不是本地说了算。</item>
    /// </list>
    /// <para>
    /// <b>多房间(r18-hostmulti)</b>:<see cref="CreateRun"/> 带了 <see cref="OnlineMultiRoomSetup"/> 时,起始房间对上本地迷宫、规划得出"往哪个出口走"(<see cref="HostedRoomMazePlan"/>),
    /// 就进多房间模式:同一个 <see cref="HostedRoomRun"/> 里房间一间换一间,宿主这一侧只多做下面几件事(每步由 <see cref="HostedRoomRun.Pump"/> 叫一次,在击杀交出去之后):
    /// <list type="number">
    /// <item>主角杀光房间、走到通向下一间的出口:排换房请求(目标格取自迷宫规划,主角位置夹进这扇出口的识别矩形里,与服务端校验过门位置用的是同一个矩形),击杀与换房都绑定当前房间序号。</item>
    /// <item>联网状态机收到新房间的起始地图通知(换了表):核对格坐标就是请求的目标格、迷宫仍对得上、新房间的本地怪与新表一一对上,然后造新房间、<see cref="HostedRoomRun.EnterRoom"/>;
    /// 装载完成命令是联网战斗跑器自己发的(进新房间的下一轮,在新房间第一条击杀之前),这里不碰。</item>
    /// <item>最后一间没有出口:清图与结算沿用单房间的老路;清图通知一到(阶段离开 <see cref="OnlineCombatPhase.Fighting"/>)出口就不再用。</item>
    /// <item>走不下去时放弃这一局(<see cref="HostedRoomRun.Abort"/>,流程回城镇,与进副本失败同一条路),每种都有明确的原因(<see cref="HostedAbort"/>)与一行日志:
    /// 换房被拒到额度、等不到新房间或装载握手(联网层 10 秒超时,或这里的 <see cref="OnlineMultiRoomSetup.MoveWaitLimitMs"/> 兜底)、联网战斗中途收场(断线、整段超时)、新房间的格子或迷宫对不上、
    /// 造不出新房间或没有出口物件、新房间的怪造不全。绝不在对不上的时候发"不安全"的换房命令:目标格只来自候选迷宫一致的规划,不一致就不进多房间。</item>
    /// </list>
    /// 起始房间对不上、规划不出下一步、或这个副本本来就只有一间(规划结论是"最后一间")时不进多房间,走与以前一字不变的单房间路径。
    /// </para>
    /// 只在宿主主线程用。联网战斗跑在后台线程(<c>OnlineDungeonRunner.RunAsync</c>),本类只读它的对应表(每间房建好之后不再变)与阶段,击杀与换房只通过线程安全的 <c>PostKill</c>、<c>PostMoveRoom</c> 交出去。
    /// 数据与线路一概不碰:日志行只有状态名、房间序号与只数,不含任何编号、地图、路径或坐标。
    /// </summary>
    public sealed class OnlineDungeonRoom
    {
        /// <summary>联网房间日志行的统一前缀(与进副本、战斗的 DNF_NET stage=… 行同一族)。</summary>
        public const string LogPrefix = "DNF_NET stage=room";

        /// <summary><see cref="PickRoomMap"/> 退而求其次时最多查几张候选房间(每张要读一遍脚本,不能把整座副本的地图都读完)。</summary>
        public const int MaxMapScan = 24;

        private readonly OnlineDungeonCombatRunner _runner;
        private readonly List<int> _missing = new List<int>();

        // —— 多房间(r18-hostmulti):只在 CreateRun 进了多房间模式之后才有值,单房间时这些一概不碰 ——
        private OnlineMultiRoomSetup _multi;
        private HostedRoomRun _run;
        private IReadOnlyList<DungeonMaze> _mazes;
        private readonly HashSet<(int X, int Y)> _visited = new HashSet<(int X, int Y)>();
        private HostedMazeStep _step;
        private int _refusalsSeen;
        private int _attempts;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _moveSinceMs;

        /// <param name="runner">进副本之后接着跑的联网战斗跑器(<see cref="OnlineDungeonRunner.UseCombat"/> 返回的那个)。</param>
        /// <exception cref="ArgumentNullException"><paramref name="runner"/> 为 null。</exception>
        public OnlineDungeonRoom(OnlineDungeonCombatRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>联网战斗的状态机已经建好(进副本拿到起始地图之后,它在后台线程里紧接着建;没拿到起始地图就永远不会建)。</summary>
        public bool MachineReady => _runner.Machine != null;

        /// <summary>接线结局;<see cref="CreateRun"/> 之前是 <see cref="OnlineRoomStatus.NotReady"/>。</summary>
        public OnlineRoomStatus Status { get; private set; }

        /// <summary>对应表里的怪数(起始地图的怪记录数);状态机没建好是 0。</summary>
        public int Expected { get; private set; }

        /// <summary>房间里与记录对上的本地怪数(序号在表内、每个序号只算一次;不含脚本召唤怪)。</summary>
        public int Spawned { get; private set; }

        /// <summary>房间里对不上记录的出生点怪数(序号不在表内或重复):宿主的工厂没有照出生点计划造房间时才会有;这些怪死了状态机会拦下、不发包。</summary>
        public int Extra { get; private set; }

        /// <summary>对应表里没有本地怪的记录的本地编号(造不出来的出生点模板):服务端不会清图,因为这些怪没法在本地被打死。</summary>
        public IReadOnlyList<int> Missing => _missing;

        /// <summary>造好的这一局(<see cref="Status"/> 为 <see cref="OnlineRoomStatus.Ready"/> 时才有)。</summary>
        public HostedRoomRun Run { get; private set; }

        /// <summary>这一局进了多房间模式(<see cref="CreateRun"/> 带了 <see cref="OnlineMultiRoomSetup"/>,而且起始房间对上了本地迷宫、规划得出出口)。</summary>
        public bool MultiRoom { get; private set; }

        /// <summary>起始房间对本地迷宫的结果(没带 <see cref="OnlineMultiRoomSetup"/> 时是 <see cref="HostedMazeStatus.NoMaze"/>)。</summary>
        public HostedMazeStatus Alignment { get; private set; } = HostedMazeStatus.NoMaze;

        /// <summary>
        /// 造联网房间这一局:按对应表的怪数出一份出生点计划交给 <paramref name="createRoom"/>,拿回房间之后核对本地怪与表的对应,
        /// 再包成 <see cref="HostedRoomRun"/>(击杀接联网跑器的 <c>PostKill</c>,回城看状态机是否走到结算完成)。
        /// 不能走通的情形(没有状态机、没有怪、没造出房间、一只对得上的都没有)返回 null、在 <see cref="Status"/> 里写明原因,宿主据此退回城镇;房间工厂自己抛的异常原样传出。
        /// 只造一次:已经造成功就直接返回那一局。
        /// <para>
        /// 带 <paramref name="multi"/> 且起始房间对得上迷宫、规划出了出口时进多房间模式(见类说明),起始房间改由 <see cref="OnlineMultiRoomSetup.CreateRoom"/> 造
        /// (它造不出来、造出来的房间没有出口物件,或抛了异常,就退回 <paramref name="createRoom"/> 的单房间老路);其余情形一字不变地走老路。
        /// </para>
        /// </summary>
        /// <param name="createRoom">房间工厂(单房间)。</param>
        /// <param name="returnDelayMs">联网结算完成之后再等多久回城(毫秒,留给玩家看一眼清空的房间;宿主取离线副本清图到结算的间隔)。</param>
        /// <param name="multi">多房间的材料;null(缺省)就是单房间,与以前一字不变。</param>
        /// <exception cref="ArgumentNullException"><paramref name="createRoom"/> 为 null。</exception>
        public HostedRoomRun CreateRun(OnlineRoomFactory createRoom, double returnDelayMs = 0.0, OnlineMultiRoomSetup multi = null)
        {
            if (createRoom == null)
            {
                throw new ArgumentNullException(nameof(createRoom));
            }
            if (Run != null)
            {
                return Run;
            }
            OnlineDungeonCombat machine = _runner.Machine;
            if (machine == null)
            {
                Status = OnlineRoomStatus.NotReady;
                return null;
            }
            int expected = machine.Monsters.Count;
            Expected = expected;
            if (expected == 0)
            {
                Status = OnlineRoomStatus.NoMonsters;
                return null;
            }

            RoomCombatSession room = null;
            HostedMazeStep first = default;
            bool engaged = false;
            if (multi != null && TryAlign(machine, multi, out first))
            {
                room = TryCreateStart(machine, multi, first, expected);
                engaged = room != null;
            }
            if (room == null)
            {
                room = createRoom(map => RoomSpawnPlan.Fill(expected, map.Monsters));
            }
            if (room == null)
            {
                Status = OnlineRoomStatus.NoRoom;
                return null;
            }
            Reconcile(room, expected);
            if (Spawned == 0)
            {
                Status = OnlineRoomStatus.NothingSpawned;
                return null;
            }

            Run = engaged ? StartMulti(room, multi, first, returnDelayMs) : new HostedRoomRun(room, id => _runner.PostKill(id), Settled, returnDelayMs);
            Status = OnlineRoomStatus.Ready;
            return Run;
        }

        /// <summary>
        /// 一行日志:状态名与各个只数(<c>DNF_NET stage=room status=Ready spawned=3 expected=3 missing=0 extra=0</c>),不含任何编号。
        /// </summary>
        public string LogLine()
        {
            return LogPrefix + " status=" + Status + " spawned=" + Spawned.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " expected=" + Expected.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " missing=" + _missing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " extra=" + Extra.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 为联网房间挑房间地图(宿主在副本的房间候选里选;纯函数,只看候选表头与"这张图有几项 [monster]"):
        /// 先按起始地图通知里的地图编号(<paramref name="mapId"/>;推断它是 map/map.lst 里的编号,没有真服核对)找同编号的候选,找到而且那张图有怪才用
        /// (<paramref name="exact"/> 为 true);编号缺失、不在候选里、或那张图没有怪时,退到候选表里按先后第一张有怪的"普通房间"(不是首领、不是假首领;
        /// <paramref name="exact"/> 为 false)——出生点模板取自房间地图自己的 [monster],一张没有怪的房间造不出任何本地怪。
        /// 退而求其次时最多查前 <see cref="MaxMapScan"/> 张。一张都挑不出(候选为空、全都没有怪)返回 null。
        /// </summary>
        /// <param name="rooms">副本的房间候选(<see cref="DungeonRoomPool.Rooms"/>);null 当作空。</param>
        /// <param name="mapId">起始地图通知里的地图编号;没有(地图类型不带地图体)就是 null。</param>
        /// <param name="monsterCount">按地图路径返回这张图的 [monster] 项数;读不出来返回 -1(当作没有怪)。</param>
        /// <param name="exact">是不是按地图编号对上的。</param>
        /// <exception cref="ArgumentNullException"><paramref name="monsterCount"/> 为 null。</exception>
        public static string PickRoomMap(IReadOnlyList<RoomCandidate> rooms, ushort? mapId, Func<string, int> monsterCount, out bool exact)
        {
            if (monsterCount == null)
            {
                throw new ArgumentNullException(nameof(monsterCount));
            }
            exact = false;
            if (rooms == null || rooms.Count == 0)
            {
                return null;
            }
            RoomCandidate tried = null;   // 按编号查过、但那张图没有怪的候选:退而求其次时不再读第二遍
            if (mapId.HasValue)
            {
                foreach (RoomCandidate room in rooms)
                {
                    if (room != null && room.MapId == mapId.Value)
                    {
                        if (monsterCount(room.MapPath) > 0)
                        {
                            exact = true;
                            return room.MapPath;
                        }
                        tried = room;
                        break;
                    }
                }
            }
            int scanned = 0;
            foreach (RoomCandidate room in rooms)
            {
                if (room == null || ReferenceEquals(room, tried) || room.IsBoss || room.IsDummy)
                {
                    continue;
                }
                if (scanned++ >= MaxMapScan)
                {
                    break;
                }
                if (monsterCount(room.MapPath) > 0)
                {
                    return room.MapPath;
                }
            }
            return null;
        }

        /// <summary>
        /// 多房间时为某一间房挑房间地图(纯函数):先按服务端给的地图编号(<paramref name="mapId"/>,负数是没有)找同编号的候选——首领房间也行(最后一间的编号就是首领图)——
        /// 需要怪(<paramref name="needMonsters"/>,即这间房的怪表非空)时那张图还得有怪才用(<paramref name="exact"/> 为 true);
        /// 对不上时退到"登记在这一格出口码(<paramref name="exits"/>)下的普通房间"里按先后第一张(需要怪就得有怪),再退到所有普通房间(都不是首领、不是假首领)。
        /// 退而求其次时各最多查前 <see cref="MaxMapScan"/> 张(同一张图不读第二遍)。一张都挑不出返回 null。
        /// </summary>
        /// <param name="exits">这一格的出口码(<see cref="OnlineRoomEntry.Exits"/>)。</param>
        /// <param name="needMonsters">这间房的怪表非空(造房间要出生点模板)。</param>
        /// <param name="monsterCount">按地图路径返回这张图的 [monster] 项数;读不出来返回 -1(当作没有怪)。只在 <paramref name="needMonsters"/> 为真时调用。</param>
        /// <exception cref="ArgumentNullException"><paramref name="monsterCount"/> 为 null。</exception>
        public static string PickEntryMap(IReadOnlyList<RoomCandidate> rooms, int mapId, int exits, bool needMonsters, Func<string, int> monsterCount, out bool exact)
        {
            if (monsterCount == null)
            {
                throw new ArgumentNullException(nameof(monsterCount));
            }
            exact = false;
            if (rooms == null || rooms.Count == 0)
            {
                return null;
            }
            RoomCandidate tried = null;
            if (mapId >= 0)
            {
                foreach (RoomCandidate room in rooms)
                {
                    if (room != null && room.MapId == mapId)
                    {
                        if (!needMonsters || monsterCount(room.MapPath) > 0)
                        {
                            exact = true;
                            return room.MapPath;
                        }
                        tried = room;
                        break;
                    }
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                int scanned = 0;
                foreach (RoomCandidate room in rooms)
                {
                    if (room == null || ReferenceEquals(room, tried) || room.IsBoss || room.IsDummy || (pass == 0 && !room.HasDirection(exits)))
                    {
                        continue;
                    }
                    if (pass == 1 && room.HasDirection(exits))
                    {
                        continue;       // 第一轮已经看过
                    }
                    if (scanned++ >= MaxMapScan)
                    {
                        break;
                    }
                    if (!needMonsters || monsterCount(room.MapPath) > 0)
                    {
                        return room.MapPath;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 迷宫的某一格某一侧该画什么出口(与离线副本同一条规则,<c>DungeonLayout.GateKindAt</c>):有出口且通向首领格 = 首领出口,通向别的格 = 普通出口;
        /// 没有出口时左右是墙、上下什么也没有。首领格取迷宫的首领候选格(<see cref="DungeonMaze.IsBossCell"/>)。
        /// </summary>
        public static GateKind GateKindAt(DungeonMaze maze, int x, int y, GateSide side)
        {
            if (maze == null)
            {
                throw new ArgumentNullException(nameof(maze));
            }
            if (!maze.HasExit(x, y, side))
            {
                return side == GateSide.Left || side == GateSide.Right ? GateKind.Wall : GateKind.None;
            }
            return maze.TryNeighbor(x, y, side, out int nx, out int ny) && maze.IsBossCell(nx, ny) ? GateKind.Boss : GateKind.Normal;
        }

        /// <summary>联网战斗已走到终局(两个结算通知收齐)并且跑器已结束:这一局完了,可以回城。</summary>
        private bool Settled()
        {
            OnlineDungeonCombat machine = _runner.Machine;
            return _runner.Ended && machine != null && machine.Phase == OnlineCombatPhase.Settled;
        }

        /// <summary>核对房间里的出生点怪与对应表:每个本地编号至多一只本地怪;缺的记进 <see cref="Missing"/>,多的与重复的算 <see cref="Extra"/>。</summary>
        private void Reconcile(RoomCombatSession room, int expected)
        {
            Spawned = Tally(room, expected, _missing, out int extra);
            Extra = extra;
        }

        /// <summary>数一间房里与怪表对上的本地怪数;<paramref name="missing"/> 不为 null 时把缺的本地编号填进去(先清空)。</summary>
        private static int Tally(RoomCombatSession room, int expected, List<int> missing, out int extra)
        {
            var present = new bool[expected];
            int spawned = 0;
            extra = 0;
            foreach (MonsterActor monster in room.Monsters)
            {
                if (monster.IsSummoned)
                {
                    continue;
                }
                int id = monster.Index;
                if (id >= 0 && id < expected && !present[id])
                {
                    present[id] = true;
                    spawned++;
                }
                else
                {
                    extra++;
                }
            }
            if (missing != null)
            {
                missing.Clear();
                for (int i = 0; i < expected; i++)
                {
                    if (!present[i])
                    {
                        missing.Add(i);
                    }
                }
            }
            return spawned;
        }

        // ------------------------------------------------------------------------------------------------
        // 多房间(r18-hostmulti)
        // ------------------------------------------------------------------------------------------------

        private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

        private void Write(string text)
        {
            _multi?.Log?.Invoke(LogPrefix + " " + text);
        }

        private static void Write(OnlineMultiRoomSetup multi, string text)
        {
            multi.Log?.Invoke(LogPrefix + " " + text);
        }

        private static bool BossMap(OnlineMultiRoomSetup multi, int mapId)
        {
            return mapId >= 0 && multi.IsBossMap != null && multi.IsBossMap(mapId);
        }

        private static bool HasGate(RoomCombatSession session, GateSide side)
        {
            foreach (RoomGate gate in session.Room.Content.Gates)
            {
                if (gate.Side == side)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>起始房间对本地迷宫、规划出口;对得上而且规划出了一步才返回 true(<paramref name="step"/> 是那一步),其余情形写一行 <c>fallback=single</c> 的日志、返回 false。</summary>
        private bool TryAlign(OnlineDungeonCombat machine, OnlineMultiRoomSetup multi, out HostedMazeStep step)
        {
            step = default;
            StartMapNotice notice = machine.Map;
            if (machine.Room != 0 || notice == null)
            {
                Write(multi, "align status=NotAtStart room=" + N(machine.Room) + " fallback=single");
                return false;
            }
            int cellX = notice.CellX;
            int cellY = notice.CellY;
            int mapId = notice.MapId.HasValue ? notice.MapId.Value : -1;
            IReadOnlyList<DungeonMaze> aligned = HostedRoomMazePlan.Align(multi.Mazes, cellX, cellY, mapId, true, multi.MapAllowsExits, out HostedMazeStatus status);
            Alignment = status;
            if (status != HostedMazeStatus.Aligned)
            {
                Write(multi, "align status=" + status + " mazes=" + N(aligned.Count) + " fallback=single");
                return false;
            }
            var visited = new HashSet<(int X, int Y)> { (cellX, cellY) };
            step = HostedRoomMazePlan.Plan(aligned, cellX, cellY, visited, BossMap(multi, mapId));
            if (!step.IsStep)
            {
                Write(multi, "align status=Aligned mazes=" + N(aligned.Count) + " plan=" + step.Kind + " fallback=single");
                return false;
            }
            _mazes = aligned;
            _visited.Clear();
            _visited.Add((cellX, cellY));
            Write(multi, "align status=Aligned mazes=" + N(aligned.Count) + " plan=Step exit=" + step.Side);
            return true;
        }

        /// <summary>用多房间工厂造起始房间;造不出来、没有出口物件、抛异常都返回 null(写一行日志),调用方退回单房间的老路。</summary>
        private RoomCombatSession TryCreateStart(OnlineDungeonCombat machine, OnlineMultiRoomSetup multi, HostedMazeStep step, int expected)
        {
            StartMapNotice notice = machine.Map;
            int mapId = notice.MapId.HasValue ? notice.MapId.Value : -1;
            var entry = new OnlineRoomEntry(machine.Room, notice.CellX, notice.CellY, mapId, null, step.Side, _mazes[0], expected);
            RoomCombatSession session;
            try
            {
                session = multi.CreateRoom(entry, map => RoomSpawnPlan.Fill(expected, map.Monsters));
            }
            catch (Exception e)
            {
                Write(multi, "align status=RoomBuild exception=" + e.GetType().Name + " fallback=single");
                return null;
            }
            if (session == null)
            {
                Write(multi, "align status=NoRoom fallback=single");
                return null;
            }
            if (!HasGate(session, step.Side))
            {
                Write(multi, "align status=NoExitGate exit=" + step.Side + " fallback=single");
                return null;
            }
            return session;
        }

        private HostedRoomRun StartMulti(RoomCombatSession room, OnlineMultiRoomSetup multi, HostedMazeStep step, double returnDelayMs)
        {
            _multi = multi;
            _step = step;
            HostedRoomRun run = null;
            run = new HostedRoomRun(room, id => _runner.PostKill(run.RoomIndex, id), Settled, returnDelayMs);
            _run = run;
            run.SetExit(step.Side);
            run.ExitRequest = PostMove;
            run.Pump = Tick;
            MultiRoom = true;
            return run;
        }

        private double NowMs => _clock.Elapsed.TotalMilliseconds;

        private void Abort(HostedRoomRun run, HostedAbort reason)
        {
            if (run.Aborted)
            {
                return;
            }
            run.Abort(reason);
            OnlineDungeonCombat machine = _runner.Machine;
            Write("abort reason=" + reason + " room=" + N(run.RoomIndex) + " rooms=" + N(run.RoomsEntered) + " phase=" + machine?.Phase
                + " netEnded=" + (_runner.Ended ? "true" : "false") + " moveInFlight=" + (machine != null && machine.MoveInFlight ? "true" : "false")
                + " load=" + machine?.LoadState + " fallback=town");
        }

        /// <summary>联网战斗在"没结算完"时收场了(断线、整段超时、换房或装载握手超时),按收场那一刻的状态归类(状态机留着断线时它在等什么)。</summary>
        private static HostedAbort ClassifyEnd(OnlineDungeonCombat machine)
        {
            if (machine.MoveInFlight)
            {
                return HostedAbort.MoveTimeout;
            }
            if (machine.LoadState == OnlineRoomLoadState.AwaitingNotice)
            {
                return HostedAbort.LoadTimeout;
            }
            return HostedAbort.NetEnded;
        }

        /// <summary>换房请求等了太久还没有结果,按状态机的闸门说它卡在哪一道。</summary>
        private static HostedAbort ClassifyWait(OnlineCombatBlock gate)
        {
            switch (gate)
            {
                case OnlineCombatBlock.RoomNotCleared:
                    return HostedAbort.RoomNotCleared;
                case OnlineCombatBlock.LoadPending:
                    return HostedAbort.LoadTimeout;
                case OnlineCombatBlock.MoveBudgetExhausted:
                    return HostedAbort.MoveRefused;
                default:
                    return HostedAbort.MoveTimeout;
            }
        }

        /// <summary>
        /// 每步在击杀交出去之后被 <see cref="HostedRoomRun"/> 叫一次:联网战斗收场了?清图了?进了新房间?换房被拒了?等太久了?
        /// 都是读状态机(线程安全的只读成员),写的只有 <see cref="HostedRoomRun"/> 与本类自己的字段。
        /// </summary>
        private void Tick(HostedRoomRun run)
        {
            OnlineDungeonCombat machine = _runner.Machine;
            if (machine == null)
            {
                return;
            }
            if (_runner.Ended)
            {
                if (machine.Phase != OnlineCombatPhase.Settled)
                {
                    Abort(run, ClassifyEnd(machine));
                }
                return;
            }
            if (machine.Phase != OnlineCombatPhase.Fighting && run.ExitSide.HasValue)
            {
                run.SetExit(null);          // 服务端清图了(只在最后一间):不再有出口可走
                Write("exit status=Closed phase=" + machine.Phase + " room=" + N(run.RoomIndex));
            }
            int room = machine.Room;        // 先读房间序号:状态机换房时它是最后写的,读到新序号就说明新表与新地图已经就位
            if (room != run.RoomIndex)
            {
                Enter(run, machine, room);
                return;
            }
            if (run.MovePending)
            {
                WatchMove(run, machine);
            }
        }

        private void WatchMove(HostedRoomRun run, OnlineDungeonCombat machine)
        {
            int refusals = machine.RoomRefusals;
            if (refusals > _refusalsSeen)
            {
                _refusalsSeen = refusals;
                run.MoveRefused();          // 请求已经被状态机出队了:等冷却过了、主角还在门口就再排一次
                Write("move status=Refused room=" + N(run.RoomIndex) + " refusals=" + N(refusals));
                if (machine.MoveGate(run.RoomIndex) == OnlineCombatBlock.MoveBudgetExhausted)
                {
                    Abort(run, HostedAbort.MoveRefused);
                }
                return;
            }
            if (NowMs - _moveSinceMs >= _multi.MoveWaitLimitMs)
            {
                Abort(run, ClassifyWait(machine.MoveGate(run.RoomIndex)));
            }
        }

        /// <summary>
        /// 状态机进了新房间:核对格坐标(必须就是请求的目标格)、迷宫(仍有候选解释得了这一格)、新房间(能造、有出口物件、怪与新表一一对上),都过了才
        /// <see cref="HostedRoomRun.EnterRoom"/>;任何一项过不了就放弃这一局(原因与日志都明写)。
        /// </summary>
        private void Enter(HostedRoomRun run, OnlineDungeonCombat machine, int room)
        {
            StartMapNotice notice = machine.Map;
            IReadOnlyList<OnlineCombatMonster> table = machine.Monsters;
            if (machine.Room != room)
            {
                return;                     // 读的当口又换了:下一步再看
            }
            int cellX = notice.CellX;
            int cellY = notice.CellY;
            int mapId = notice.MapId.HasValue ? notice.MapId.Value : -1;
            if (room != run.RoomIndex + 1 || !_step.IsStep || cellX != _step.TargetX || cellY != _step.TargetY)
            {
                Abort(run, HostedAbort.CellMismatch);
                return;
            }
            IReadOnlyList<DungeonMaze> aligned = HostedRoomMazePlan.Align(_mazes, cellX, cellY, mapId, false, _multi.MapAllowsExits, out HostedMazeStatus status);
            if (status != HostedMazeStatus.Aligned)
            {
                Abort(run, HostedAbort.CellMismatch);
                return;
            }
            _visited.Add((cellX, cellY));
            HostedMazeStep next = HostedRoomMazePlan.Plan(aligned, cellX, cellY, _visited, BossMap(_multi, mapId));
            GateSide entryGate = ExitBits.Opposite(_step.Side);
            GateSide? exit = next.IsStep ? next.Side : (GateSide?)null;
            int expected = table.Count;
            var entry = new OnlineRoomEntry(room, cellX, cellY, mapId, entryGate, exit, aligned[0], expected);
            RoomCombatSession session;
            try
            {
                session = _multi.CreateRoom(entry, map => RoomSpawnPlan.Fill(expected, map.Monsters));
            }
            catch (Exception e)
            {
                Write("enter status=RoomBuild room=" + N(room) + " exception=" + e.GetType().Name);
                Abort(run, HostedAbort.RoomBuild);
                return;
            }
            if (session == null || (exit.HasValue && !HasGate(session, exit.Value)))
            {
                Write("enter status=" + (session == null ? "NoRoom" : "NoExitGate") + " room=" + N(room));
                Abort(run, HostedAbort.RoomBuild);
                return;
            }
            int spawned = Tally(session, expected, null, out int extra);
            if (spawned < expected)
            {
                Write("enter status=Mismatch room=" + N(room) + " spawned=" + N(spawned) + " expected=" + N(expected) + " extra=" + N(extra));
                Abort(run, HostedAbort.RoomMismatch);
                return;
            }

            run.EnterRoom(session, room, mapId < 0 ? 0 : mapId, entryGate, exit);
            _mazes = aligned;
            _step = next;
            _refusalsSeen = machine.RoomRefusals;
            Write("enter status=Ready room=" + N(room) + " spawned=" + N(spawned) + " expected=" + N(expected) + " extra=" + N(extra) + " exit="
                + (exit.HasValue ? exit.Value.ToString() : "none"));
        }

        /// <summary>
        /// <see cref="HostedRoomRun.ExitRequest"/>:主角杀光房间并触发了出口。只放行规划里的那一侧;目标格取自规划(所有候选迷宫一致的那一格),主角位置夹进这扇出口的识别矩形;
        /// 联网战斗收场了或目标格拼不出合法请求就不排(返回 false,由 <see cref="HostedRoomRun"/> 按"没排上"处理)。
        /// </summary>
        private bool PostMove(GateSide side, int heroX, int heroY)
        {
            HostedRoomRun run = _run;
            if (run == null || run.Aborted || _runner.Ended || !_step.IsStep || side != _step.Side)
            {
                return false;
            }
            foreach (DungeonMaze maze in _mazes)
            {
                if (!maze.InBounds(_step.TargetX, _step.TargetY))
                {
                    Abort(run, HostedAbort.CellMismatch);      // 目标格超出某个候选迷宫的宽高:服务端遇到会断线,绝不发
                    return false;
                }
            }
            int x = heroX;
            int y = heroY;
            WorldRect? area = run.Room.Room.Map.GateRects[(int)side];
            if (area.HasValue)
            {
                WorldRect r = area.Value;
                x = Math.Max(r.X, Math.Min(r.X + r.Width - 1, x));
                y = Math.Max(r.Y, Math.Min(r.Y + r.Height - 1, y));
            }
            OnlineMoveRoomRequest request;
            try
            {
                request = new OnlineMoveRoomRequest(_step.TargetX, _step.TargetY, x, y, false);
            }
            catch (ArgumentOutOfRangeException)
            {
                Abort(run, HostedAbort.CellMismatch);
                return false;
            }
            if (!_runner.PostMoveRoom(run.RoomIndex, request))
            {
                return false;
            }
            _attempts++;
            _moveSinceMs = NowMs;
            Write("move status=Posted room=" + N(run.RoomIndex) + " attempt=" + N(_attempts) + " exit=" + side);
            return true;
        }
    }
}
