using System;
using System.Collections.Generic;
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
    /// 只在宿主主线程用。联网战斗跑在后台线程(<c>OnlineDungeonRunner.RunAsync</c>),本类只读它的对应表(建好之后不再变)与阶段,击杀只通过线程安全的 <c>PostKill</c> 交出去。
    /// 数据与线路一概不碰:日志行只有状态名与只数,不含任何编号。
    /// </summary>
    public sealed class OnlineDungeonRoom
    {
        /// <summary>联网房间日志行的统一前缀(与进副本、战斗的 DNF_NET stage=… 行同一族)。</summary>
        public const string LogPrefix = "DNF_NET stage=room";

        /// <summary><see cref="PickRoomMap"/> 退而求其次时最多查几张候选房间(每张要读一遍脚本,不能把整座副本的地图都读完)。</summary>
        public const int MaxMapScan = 24;

        private readonly OnlineDungeonCombatRunner _runner;
        private readonly List<int> _missing = new List<int>();

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

        /// <summary>
        /// 造联网房间这一局:按对应表的怪数出一份出生点计划交给 <paramref name="createRoom"/>,拿回房间之后核对本地怪与表的对应,
        /// 再包成 <see cref="HostedRoomRun"/>(击杀接联网跑器的 <c>PostKill</c>,回城看状态机是否走到结算完成)。
        /// 不能走通的情形(没有状态机、没有怪、没造出房间、一只对得上的都没有)返回 null、在 <see cref="Status"/> 里写明原因,宿主据此退回城镇;房间工厂自己抛的异常原样传出。
        /// 只造一次:已经造成功就直接返回那一局。
        /// </summary>
        /// <param name="createRoom">房间工厂。</param>
        /// <param name="returnDelayMs">联网结算完成之后再等多久回城(毫秒,留给玩家看一眼清空的房间;宿主取离线副本清图到结算的间隔)。</param>
        /// <exception cref="ArgumentNullException"><paramref name="createRoom"/> 为 null。</exception>
        public HostedRoomRun CreateRun(OnlineRoomFactory createRoom, double returnDelayMs = 0.0)
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

            RoomCombatSession room = createRoom(map => RoomSpawnPlan.Fill(expected, map.Monsters));
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

            Run = new HostedRoomRun(room, id => _runner.PostKill(id), Settled, returnDelayMs);
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

        /// <summary>联网战斗已走到终局(两个结算通知收齐)并且跑器已结束:这一局完了,可以回城。</summary>
        private bool Settled()
        {
            OnlineDungeonCombat machine = _runner.Machine;
            return _runner.Ended && machine != null && machine.Phase == OnlineCombatPhase.Settled;
        }

        /// <summary>核对房间里的出生点怪与对应表:每个本地编号至多一只本地怪;缺的记进 <see cref="Missing"/>,多的与重复的算 <see cref="Extra"/>。</summary>
        private void Reconcile(RoomCombatSession room, int expected)
        {
            var present = new bool[expected];
            int spawned = 0;
            int extra = 0;
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
            _missing.Clear();
            for (int i = 0; i < expected; i++)
            {
                if (!present[i])
                {
                    _missing.Add(i);
                }
            }
            Spawned = spawned;
            Extra = extra;
        }
    }
}
