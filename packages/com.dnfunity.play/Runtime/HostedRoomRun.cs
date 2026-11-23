using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网副本的房间托管(r15-dungeonhost):把一间已经造好的房间战斗会话(<see cref="RoomCombatSession"/>,怪物、战斗、画面、声音全是它自己的,
    /// 不另造一套)包成流程(<see cref="GameFlow"/>)认得的"一局副本"(<see cref="IRunSession"/>)。它只多做两件事:
    /// <list type="number">
    /// <item>每推进一步,用 <see cref="RoomKillWatcher"/> 看房间里有哪些出生点怪新死了,把它们的序号(<see cref="MonsterActor.Index"/>)逐个交给 <c>reportKill</c>——
    /// 每只怪只交一次,脚本召唤的怪不交。序号的含义由造房间的一方保证(联网宿主用 <see cref="RoomSpawnPlan"/> 造房间,序号就是联网对应表里的本地序号);
    /// 这里不认识联网层,<c>reportKill</c> 是宿主给的委托(返回有没有收下)。</item>
    /// <item>外面说"这一局结束了"(<c>finished</c> 返回 true,联网宿主给的是"结算收齐了")之后,再过 <c>returnDelayMs</c> 毫秒,
    /// <see cref="Choice"/> 变成 <see cref="SettlementChoice.ReturnToTown"/>,流程据此回城镇(<see cref="GameFlow.Poll"/> 本来的逻辑,没有为它改一个字)。
    /// 在 <c>finished</c> 为 true 之前 <see cref="Choice"/> 一直是 <see cref="SettlementChoice.None"/>:本地怪杀光不等于可以回城,回不回由外面说了算。</item>
    /// </list>
    /// 其余成员(画面、操作、声音、物品)原样转给房间会话。不是线程安全的,与房间会话一样只在宿主主线程用;两个委托也在主线程被调用。
    /// 主角死亡:房间会话默认没有复活窗口(<see cref="RoomCombatSession.Revive"/> 为 null),<see cref="Failed"/> 只在宿主给了复活窗口并走完失败计时(或宿主放弃,见下)时为真。
    /// <para>
    /// <b>多房间(r18-hostmulti)</b>:联网的多房间副本在这一局里换房间——同一个对象、同一局副本(流程那一侧一局一个 <see cref="IRunSession"/>,<see cref="GameFlow"/> 的阶段语义一个字没动),
    /// 里面的房间会话一间换一间(<see cref="EnterRoom"/>)。不选"每房间一个新的 run 再由外层串起来":流程只认"一局副本"的回城/失败,外层串联就得另造一层流程,而这里
    /// 换房的实质只是"当前房间会话换成新的、主角带着技能冷却/增益/血量走"(与离线 <see cref="DungeonSession"/> 的 Travel 同一套带法)。
    /// 换房的时序:本地怪杀光(<see cref="RoomCleared"/>)→ 主角触发通向下一间的出口(<see cref="ExitSide"/>,与离线同一个判定 <see cref="DungeonSession.HeroTriggers"/>)
    /// → 回调 <see cref="ExitRequest"/>(宿主排换房请求,带主角此刻的位置)→ 等待(<see cref="MovePending"/>)→ 宿主造好新房间调 <see cref="EnterRoom"/>。
    /// 宿主每步还能通过 <see cref="Pump"/> 在 <b>击杀交出去之后</b> 被叫一次(盯联网状态机:拒绝、新房间、超时、断线),最后一间没有出口,清图与结算沿用原路。
    /// 单房间(没设 <see cref="Pump"/>、<see cref="ExitRequest"/>、没调过 <see cref="EnterRoom"/>)时这些全部不起作用,行为与以前一字不变。
    /// </para>
    /// <para>
    /// <b>计数口径</b>:<see cref="Reported"/> 与 <see cref="Dropped"/> 是<b>整局副本累计</b>(所有房间加在一起;换房不清零),因为它们回答的是"这一局一共交出去多少击杀",
    /// 结算日志(<c>return … reported=…</c>)与自动机的"有没有进展"都按整局看;要按房间看用 <see cref="RoomReported"/>、<see cref="RoomDropped"/>(进新房间清零)。
    /// </para>
    /// </summary>
    public sealed class HostedRoomRun : ICommandSession, ISoundSource, IItemSession, IRunSession
    {
        /// <summary>
        /// 换房请求被拒(<see cref="MoveRefused"/>)或没排上(<see cref="ExitRequest"/> 返回 false)之后,多久(毫秒)之内不再触发出口:
        /// 主角还站在门口时,不能每一步都排一次。
        /// </summary>
        public const double RetryCooldownMs = 1000.0;

        private RoomCombatSession _room;
        private RoomKillWatcher _watcher;
        private readonly Func<int, bool> _reportKill;
        private readonly Func<bool> _finished;
        private readonly double _returnDelayMs;
        private readonly List<int> _kills = new List<int>();
        private readonly List<SoundCue> _carriedSounds = new List<SoundCue>();
        private double _waitedMs;
        private double _clockMs;
        private double _retryLeftMs;
        private bool _cleared;
        private bool _clearedAtEntry;
        private double _clearedAtMs;
        private bool _gateOpened;

        // 跨房间累计的本地战绩(供 r18-settleui 本地评分与对账,换房时累加,每局新 HostedRoomRun 自动清零)
        private int _priorKills;
        private int _priorComboKills;
        private int _priorMaxCombo;
        private int _priorHitsTaken;
        private int _priorRevivesUsed;
        private long _priorExpEarned;
        private long _priorGoldEarned;
        private int _priorItemsPicked;

        /// <param name="room">已经造好的房间会话(联网宿主用 <see cref="RoomCombatSession.Create"/> 的 <c>spawnPlan</c> 造)。</param>
        /// <param name="reportKill">交出一只怪的序号;返回 true 表示收下了,false 表示不收(例如联网战斗已经结束)。</param>
        /// <param name="finished">这一局是不是结束了(例如联网结算收齐);每步查一次,为 true 过一次就记住,不会再变回去。</param>
        /// <param name="returnDelayMs">看到结束之后再等多久才回城(毫秒,留给玩家看一眼清空的房间);负数当作 0。</param>
        /// <exception cref="ArgumentNullException">任何一个参数为 null。</exception>
        public HostedRoomRun(RoomCombatSession room, Func<int, bool> reportKill, Func<bool> finished, double returnDelayMs = 0.0)
        {
            _room = room ?? throw new ArgumentNullException(nameof(room));
            _reportKill = reportKill ?? throw new ArgumentNullException(nameof(reportKill));
            _finished = finished ?? throw new ArgumentNullException(nameof(finished));
            _returnDelayMs = returnDelayMs > 0.0 ? returnDelayMs : 0.0;
            _watcher = new RoomKillWatcher(room);
        }

        /// <summary>被托管的房间会话(宿主的日志与底栏读它;战斗本身只在它里面)。多房间时是<b>当前</b>这一间(换房之后换成新的)。</summary>
        public RoomCombatSession Room => _room;

        /// <summary>交给 <c>reportKill</c> 并被收下的击杀数(每只怪最多一次;多房间时是整局累计,见类说明)。</summary>
        public int Reported { get; private set; }

        /// <summary>交给 <c>reportKill</c> 但没被收下的击杀数(例如联网战斗已经结束):观察者不会再交第二遍,它们就此丢掉。多房间时是整局累计。</summary>
        public int Dropped { get; private set; }

        /// <summary><c>finished</c> 已经为 true 过(流程即将在 <c>returnDelayMs</c> 之后回城)。</summary>
        public bool Finished { get; private set; }

        /// <summary>结算选定的项:结束之后过了 <c>returnDelayMs</c> 是 <see cref="SettlementChoice.ReturnToTown"/>,在此之前是 <see cref="SettlementChoice.None"/>。</summary>
        public SettlementChoice Choice { get; private set; }

        /// <summary>副本失败:房间会话有复活窗口(宿主给的)并且失败计时走完了,或者宿主放弃了这一局(<see cref="Aborted"/>);没有复活窗口又没人放弃永远为假。</summary>
        public bool Failed => Aborted || (_room.Revive != null && _room.Revive.Failed);

        // —— 多房间(r18-hostmulti) ——

        /// <summary>
        /// 联网宿主每步的接线:在这一步的击杀交出去之后、出口检查之前被叫一次(参数是这个 run 自己)。宿主在里面盯联网状态机,
        /// 需要换房时调 <see cref="EnterRoom"/>、对方拒绝时调 <see cref="MoveRefused"/>、不能再往下打时调 <see cref="Abort"/>。已放弃(<see cref="Aborted"/>)之后不再被叫。
        /// 不设(缺省 null)就是单房间,什么也不多做。
        /// </summary>
        public Action<HostedRoomRun> Pump { get; set; }

        /// <summary>
        /// 主角触发了通向下一间的出口(<see cref="ExitSide"/>,而且本地怪已经杀光)时的回调,参数依次是出口那一侧与主角此刻的位置 (x, y)(取整);
        /// 返回 true 表示换房请求已经排上(之后进入等待:<see cref="MovePending"/>,直到 <see cref="EnterRoom"/>、<see cref="MoveRefused"/> 或 <see cref="Abort"/>),
        /// 返回 false 表示没排上(例如联网战斗已经结束):按被拒处理,<see cref="RetryCooldownMs"/> 之内不再触发。不设就没有出口触发。回调在主线程被调用。
        /// </summary>
        public Func<GateSide, int, int, bool> ExitRequest { get; set; }

        /// <summary>
        /// 这一局在多房间模式:设了 <see cref="Pump"/> 或 <see cref="ExitRequest"/>,或者换过房。自动机的日志据此多写换房字段;
        /// 单房间副本恒为 false(日志一字不变)。
        /// </summary>
        public bool MultiRoom => Pump != null || ExitRequest != null || RoomsEntered > 1;

        /// <summary>当前房间的序号(宿主在 <see cref="EnterRoom"/> 里给,联网宿主给的是联网状态机的房间序号;起始房间与单房间是 0)。</summary>
        public int RoomIndex { get; private set; }

        /// <summary>这一局进过几间房(起始房间算一间,每次 <see cref="EnterRoom"/> 加一)。</summary>
        public int RoomsEntered { get; private set; } = 1;

        /// <summary>当前房间里交给 <c>reportKill</c> 并被收下的击杀数(进新房间清零);整局累计见 <see cref="Reported"/>。</summary>
        public int RoomReported { get; private set; }

        /// <summary>当前房间里交出去但没被收下的击杀数(进新房间清零);整局累计见 <see cref="Dropped"/>。</summary>
        public int RoomDropped { get; private set; }

        /// <summary>通向下一间房的出口在哪一侧;null 表示没有(最后一间、单房间、或宿主没能对出下一间)。只有这一侧的出口会触发换房请求。</summary>
        public GateSide? ExitSide { get; private set; }

        /// <summary>当前房间的怪都没了(没有任何受击框、永远打不到的不算,与离线 <see cref="DungeonSession"/> 的清房判定同一条规则):出口这时才算开。</summary>
        public bool RoomCleared => AllMonstersGone(_room);

        /// <summary>换房请求已排上、还没有结果(新房间、被拒或放弃之前):主角该站着等,出口也不会重复触发。</summary>
        public bool MovePending { get; private set; }

        /// <summary>这一局一共排过几次换房请求(含被拒之后重排的)。</summary>
        public int MoveRequests { get; private set; }

        /// <summary>宿主通知过几次换房被拒(<see cref="MoveRefused"/>;整局累计)。</summary>
        public int MoveRefusals { get; private set; }

        /// <summary>宿主放弃了这一局(<see cref="Abort"/>):<see cref="Failed"/> 随之为真,流程回城。</summary>
        public bool Aborted { get; private set; }

        /// <summary>放弃的原因;没放弃是 <see cref="HostedAbort.None"/>。放弃之后不再变(第一次的原因留着)。</summary>
        public HostedAbort AbortReason { get; private set; }

        /// <summary>
        /// 现在房间里有没有通向下一间的出口物件(<see cref="ExitSide"/> 那一侧);没有出口时为 false。
        /// 房间里没有这个出口物件,主角就没法触发换房(宿主造房间时要保证有,见 <see cref="OnlineDungeonRoom"/>)。
        /// </summary>
        public bool HasExitGate => ExitSide.HasValue && FindGate(_room, ExitSide.Value) != null;

        /// <summary>这一局已经推进的本地时钟(毫秒;换房不清零)。</summary>
        public double ClockMs => _clockMs;

        /// <summary>整局累计的本地击杀数(各房间之和;换房不清零)。</summary>
        public int LocalKills => _priorKills + _room.Kills;

        /// <summary>整局累计的本地连击击杀数(各房间之和)。</summary>
        public int LocalComboKills => _priorComboKills + _room.ComboKills;

        /// <summary>整局最高连击数(各房间取最大)。</summary>
        public int LocalMaxCombo => _room.MaxCombo > _priorMaxCombo ? _room.MaxCombo : _priorMaxCombo;

        /// <summary>整局主角受击次数(各房间之和)。</summary>
        public int LocalHitsTaken => _priorHitsTaken + _room.HeroHits;

        /// <summary>整局使用的复活币次数(各房间之和)。</summary>
        public int LocalRevivesUsed => _priorRevivesUsed + _room.Revived;

        /// <summary>整局本地击杀经验累计(各房间之和)。</summary>
        public long LocalExpEarned => _priorExpEarned + _room.ExpEarned;

        /// <summary>整局本地拾取金币累计(各房间之和)。</summary>
        public long LocalGoldEarned => _priorGoldEarned + _room.GoldEarned;

        /// <summary>整局本地拾取物品件数累计(各房间之和)。</summary>
        public int LocalItemsPicked => _priorItemsPicked + _room.ItemsPicked;

        /// <summary>取当前这一局的本地战绩快照(用于离线同款评分字母计算与对账)。</summary>
        public RunStats SnapshotLocalStats()
        {
            double clamped = _clockMs < 0.0 ? 0.0 : (_clockMs > int.MaxValue ? int.MaxValue : _clockMs);
            return new RunStats(
                (int)Math.Round(clamped),
                LocalKills,
                LocalComboKills,
                LocalMaxCombo,
                LocalHitsTaken,
                LocalRevivesUsed);
        }

        /// <summary>
        /// 可选的外部结算/HUD 精灵供给(r18-settleui 用:联网结算停留期间在 <see cref="Build"/> 中把两层黑幕、营火、菜单与战果面板
        /// 送进 <see cref="RoomCombatSession.Build(long, IReadOnlyList{SceneSprite})"/> 按原版图层大类排序)。未设(缺省 null)时行为与以前一字不变。
        /// </summary>
        public Func<HostedRoomRun, long, IReadOnlyList<SceneSprite>> HudOverlay { get; set; }

        /// <summary>设(或清掉)通向下一间房的出口侧。联网宿主在起始房间对齐成功之后设,对不齐时清掉(这一间房就当最后一间)。</summary>
        public void SetExit(GateSide? side)
        {
            ExitSide = side;
        }

        /// <summary>
        /// 换房成功:把房间会话换成 <paramref name="next"/>(宿主用新房间的怪表造好的),主角带着走。顺序与离线 <see cref="DungeonSession"/> 换房一致:
        /// 先把旧房间里还没交出去的击杀交完、还没放完的声音留着;主角摆到 <paramref name="entry"/> 侧出口的落点(<see cref="RoomArrival.PlaceAtGate"/>,朝向沿用旧房间),镜头重来;
        /// 技能冷却、增益、等级进度、生命与魔法、复活窗口带到新房间;换房等待结束、出口换成 <paramref name="exit"/>、房间计数清零、击杀观察者换成新房间的。
        /// </summary>
        /// <param name="next">新房间的战斗会话(与当前的不是同一个对象)。</param>
        /// <param name="roomIndex">新房间的序号(联网宿主给状态机的房间序号)。</param>
        /// <param name="mapId">新房间的地图编号(落点的随机种子要用;没有就给 0)。</param>
        /// <param name="entry">主角从哪一侧出口进来(落在这一侧出口的识别矩形里);null 不改主角的位置。</param>
        /// <param name="exit">新房间通向再下一间的出口侧;null 表示这一间是最后一间(没有出口触发)。</param>
        /// <exception cref="ArgumentNullException"><paramref name="next"/> 为 null。</exception>
        /// <exception cref="ArgumentException"><paramref name="next"/> 就是当前房间会话。</exception>
        public void EnterRoom(RoomCombatSession next, int roomIndex, int mapId, GateSide? entry, GateSide? exit)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            if (ReferenceEquals(next, _room))
            {
                throw new ArgumentException("新房间必须是另一个房间会话", nameof(next));
            }

            PollKills();                                // 旧房间里最后一刻死的怪,在换掉观察者之前交完
            RoomCombatSession old = _room;
            old.DrainSounds(_carriedSounds);            // 旧房间还没交给画面的声音留着,下一次 DrainSounds 先给
            if (entry.HasValue)
            {
                RoomArrival.PlaceAtGate(next.Room, entry.Value, mapId, GateArrival.DefaultHeroObjectId, old.Hero.FacingLeft);
            }
            next.Camera.Restart(next.Hero.X, next.Hero.Y);
            if (next.Skills != null)
            {
                next.Skills.CopyCooldownsFrom(old.Skills);      // 技能冷却不因换房清零
            }
            if (next.Buffs != null)
            {
                next.Buffs.CopyStateFrom(old.Buffs);            // 生效中的增益跟着人走
            }
            next.SyncProgress();                                // 别的房间里升了级:先换上新等级的上限,再抄生命魔法
            if (old.Vitals != null && next.Vitals != null && !ReferenceEquals(old.Vitals, next.Vitals))
            {
                next.Vitals.CopyStateFrom(old.Vitals);          // 生命与魔法跟着人走
            }
            if (next.Revive == null)
            {
                next.Revive = old.Revive;                       // 复活窗口全局只有一份(离线工厂也是每间房共用同一个)
            }
            next.SkillsHeld = old.SkillsHeld;
            next.CommandsEnabled = old.CommandsEnabled;

            _priorKills += old.Kills;
            _priorComboKills += old.ComboKills;
            if (old.MaxCombo > _priorMaxCombo)
            {
                _priorMaxCombo = old.MaxCombo;
            }
            _priorHitsTaken += old.HeroHits;
            _priorRevivesUsed += old.Revived;
            _priorExpEarned += old.ExpEarned;
            _priorGoldEarned += old.GoldEarned;
            _priorItemsPicked += old.ItemsPicked;

            _room = next;
            _watcher = new RoomKillWatcher(next);
            RoomIndex = roomIndex;
            RoomsEntered++;
            RoomReported = 0;
            RoomDropped = 0;
            ExitSide = exit;
            MovePending = false;
            _retryLeftMs = 0.0;
            _gateOpened = false;
            _cleared = AllMonstersGone(next);           // 进门时本来就没有怪:出口直接是"开了很久"的样子(同离线)
            _clearedAtEntry = _cleared;
            _clearedAtMs = _clockMs;
        }

        /// <summary>
        /// 宿主通知:刚才排的换房请求被拒了(服务端回了失败)。等待结束(<see cref="MovePending"/> 变假),<see cref="RetryCooldownMs"/> 之后主角还在门口就会再触发一次;
        /// 要不要继续试(额度)由宿主判断,不让再试就调 <see cref="Abort"/>。没有在等换房时什么也不做。
        /// </summary>
        public void MoveRefused()
        {
            if (!MovePending)
            {
                return;
            }
            MovePending = false;
            MoveRefusals++;
            _retryLeftMs = RetryCooldownMs;
        }

        /// <summary>
        /// 宿主放弃这一局(换房被拒到额度、等不到新房间、新房间对不上、对端断开……):<see cref="Failed"/> 变真,出口不再触发,<see cref="Pump"/> 不再被叫;
        /// 流程据此回城镇(与进副本失败同一条路)。只认第一次的原因;<paramref name="reason"/> 不能是 <see cref="HostedAbort.None"/>。
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> 是 <see cref="HostedAbort.None"/>。</exception>
        public void Abort(HostedAbort reason)
        {
            if (reason == HostedAbort.None)
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "放弃必须给出原因");
            }
            if (Aborted)
            {
                return;
            }
            Aborted = true;
            AbortReason = reason;
            MovePending = false;
        }

        // —— IHeroSession / ISceneSource:原样转给房间会话 ——

        public string MapPath => _room.MapPath;

        public TownHero Hero => _room.Hero;

        public TownCamera Camera => _room.Camera;

        public int LayerCount => _room.LayerCount;

        public int NpcCount => _room.NpcCount;

        public List<SceneSprite> Build(long timeMs)
        {
            OpenExitGate(timeMs);
            IReadOnlyList<SceneSprite> hud = HudOverlay?.Invoke(this, timeMs);
            return hud != null && hud.Count > 0 ? _room.Build(timeMs, hud) : _room.Build(timeMs);
        }

        // —— 推进:先让房间会话走一步,再盯击杀与结束 ——

        public void Update(double deltaMs, int dirX, int dirY)
        {
            _room.Update(deltaMs, dirX, dirY);
            Step(deltaMs);
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            _room.Update(deltaMs, dirX, dirY, attackPressed, jumpPressed);
            Step(deltaMs);
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            _room.Update(deltaMs, dirX, dirY, attackPressed, jumpPressed, skillsPressed);
            Step(deltaMs);
        }

        public SkillKeys SkillsHeld
        {
            get { return _room.SkillsHeld; }
            set { _room.SkillsHeld = value; }
        }

        public bool CommandsEnabled
        {
            get { return _room.CommandsEnabled; }
            set { _room.CommandsEnabled = value; }
        }

        public void DrainSounds(List<SoundCue> into)
        {
            if (_carriedSounds.Count > 0)
            {
                into.AddRange(_carriedSounds);
                _carriedSounds.Clear();
            }
            _room.DrainSounds(into);
        }

        public UseResult TryUseQuickSlot(int slot)
        {
            return _room.TryUseQuickSlot(slot);
        }

        public UseResult TryUseItem(ItemInfo item)
        {
            return _room.TryUseItem(item);
        }

        private void Step(double deltaMs)
        {
            _clockMs += deltaMs > 0.0 ? deltaMs : 0.0;

            // 先交这一步新死的怪,再看结束:同一步里"最后一只怪的击杀"一定排在"回城"之前(多房间时也在"换房请求"之前)
            PollKills();

            if (Pump != null && !Aborted)
            {
                Pump(this);
            }

            if (Choice != SettlementChoice.None)
            {
                return;
            }
            if (ExitSide.HasValue && !Aborted && !Finished)
            {
                CheckExit(deltaMs);
            }
            if (!Finished && _finished())
            {
                Finished = true;
            }
            if (Finished)
            {
                _waitedMs += deltaMs > 0.0 ? deltaMs : 0.0;
                if (_waitedMs >= _returnDelayMs)
                {
                    Choice = SettlementChoice.ReturnToTown;
                }
            }
        }

        /// <summary>观察者看一遍当前房间,新死的怪逐个交给 <c>reportKill</c>(收下计 <see cref="Reported"/>、没收下计 <see cref="Dropped"/>,房间计数同步)。</summary>
        private void PollKills()
        {
            _kills.Clear();
            _watcher.Poll(_kills);
            for (int i = 0; i < _kills.Count; i++)
            {
                if (_reportKill(_kills[i]))
                {
                    Reported++;
                    RoomReported++;
                }
                else
                {
                    Dropped++;
                    RoomDropped++;
                }
            }
        }

        /// <summary>
        /// 出口检查:本地怪杀光、没有在等换房、冷却过了、主角触发了 <see cref="ExitSide"/> 那一侧的出口(与离线同一个判定),就回调 <see cref="ExitRequest"/>。
        /// 怪没杀光之前出口是关的,主角顶着门也不触发。
        /// </summary>
        private void CheckExit(double deltaMs)
        {
            if (!_cleared && AllMonstersGone(_room))
            {
                _cleared = true;
                _clearedAtMs = _clockMs;
            }
            if (_retryLeftMs > 0.0)
            {
                _retryLeftMs -= deltaMs > 0.0 ? deltaMs : 0.0;
                return;
            }
            Func<GateSide, int, int, bool> request = ExitRequest;
            if (MovePending || request == null || !_cleared)
            {
                return;
            }
            GateSide side = ExitSide.Value;
            RoomGate gate = FindGate(_room, side);
            TownHero hero = _room.Hero;
            if (gate == null || !DungeonSession.HeroTriggers(gate, hero))
            {
                return;
            }
            if (request(side, (int)Math.Round(hero.X), (int)Math.Round(hero.Y)))
            {
                MovePending = true;
                MoveRequests++;
            }
            else
            {
                _retryLeftMs = RetryCooldownMs;
            }
        }

        /// <summary>清房之后第一次出画面时让通向下一间的出口打开(只有这一个出口会开,其余原样);开的时刻按"清房到现在过了多久"倒推到 <paramref name="timeMs"/> 的时间线上。</summary>
        private void OpenExitGate(long timeMs)
        {
            if (!ExitSide.HasValue || _gateOpened)
            {
                return;
            }
            if (!_cleared && AllMonstersGone(_room))
            {
                _cleared = true;
                _clearedAtMs = _clockMs;
            }
            if (!_cleared)
            {
                return;
            }
            _gateOpened = true;
            RoomGate gate = FindGate(_room, ExitSide.Value);
            if (gate == null || gate.State == GateState.Open)
            {
                return;
            }
            if (_clearedAtEntry)
            {
                gate.OpenSettled();
            }
            else
            {
                gate.Open(timeMs - (long)Math.Round(_clockMs - _clearedAtMs));
            }
        }

        private static RoomGate FindGate(RoomCombatSession session, GateSide side)
        {
            foreach (RoomGate gate in session.Room.Content.Gates)
            {
                if (gate.Side == side)
                {
                    return gate;
                }
            }
            return null;
        }

        /// <summary>房间里该打的怪物都没了(没有任何受击框、永远打不到的不算;与 <see cref="DungeonSession"/> 的清房判定同一条规则)。</summary>
        private static bool AllMonstersGone(RoomCombatSession session)
        {
            foreach (MonsterActor monster in session.Monsters)
            {
                if (!monster.IsGone && monster.CanEverBeHit)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
