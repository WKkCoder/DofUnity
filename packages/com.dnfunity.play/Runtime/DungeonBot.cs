using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 无人值守的"自动打副本":每一步替主角决定按什么键。房间里有活着的怪物就走到最近的一只面前连续普攻,清完怪之后沿迷宫里到首领格的最短路走向对应的出口,
    /// 首领格清空后就停。完全确定(没有随机),命令行 dnf dungeon-check 与 Unity 播放器的 -dnfbot 跑出的是同一局;用来在没有人的时候验证出口开关、换房间与整条迷宫路线。
    /// 它只是测试工具,不代表原版的 AI,也不绕过任何官方服务。
    /// </summary>
    public sealed class DungeonBot
    {
        /// <summary>与怪物的横向距离不超过它就开始挥击(像素)。</summary>
        public const double ReachX = 52;

        /// <summary>与怪物的纵向距离不超过它才算同一条线上(像素)。</summary>
        public const double ReachY = 10;

        /// <summary>背对着怪物想转身时,离怪物的横向距离小于它(比走一步还近)就先朝外退一步,免得转身那一步越过怪物又背对着它(像素)。</summary>
        public const double TurnGap = 6;

        /// <summary>走位的死区(像素):到目标点这么近就不再动。</summary>
        public const double Deadband = 6;

        /// <summary>走向左右出口时,主角的 y 与出口位置的 y 相差不超过这么多像素才顶着出口走(门体只盖在出口位置附近的几行上,在别处顶着墙进不了门)。</summary>
        public const double GateRowReach = 20;

        /// <summary>
        /// 寻路的出口目标离识别矩形边界留出的余量(像素):朝出口走时左右出口只有 x 在动、上下出口只有 y 在动,
        /// 另一个方向的坐标落在矩形边界上就一直顶不进去(差零点几像素)。
        /// </summary>
        public const int AreaMargin = 6;

        /// <summary>想上下挪却连续这么多步没挪动,就认为怪物在主角够不着的一条线上,横向够得着时照样挥击。</summary>
        public const int YBlockedSteps = 20;

        /// <summary>
        /// 想上下挪却一直挪不动(<see cref="YBlockedSteps"/>)时,怪物与主角的纵向差不超过它(像素)还肯原地横向挥击:怪物站在可走带边缘外一点、够得着的那种。
        /// 再远就是隔着障碍物(房间里的可破坏物件也挡路),该绕路过去,不能在原地空挥(挥击的动作会一直打断绕路的脚步,成了死循环)。
        /// </summary>
        public const double YBlockedReachY = 40;

        /// <summary>寻路连着这么多步找不到路,就认为真的够不着,回到原地挥击的老办法(<see cref="YBlockedReachY"/> 之外也挥)。</summary>
        public const int PathFailedSteps = 30;

        /// <summary>连续按攻击键的间隔(步)。</summary>
        public const int AttackEvery = 6;

        /// <summary>离目标的距离连续这么多步没有变近,就改用寻路走向这个目标(直线走不通:隔着障碍、悬崖或另一层的可走带)。</summary>
        public const int StallSteps = 60;

        /// <summary>
        /// 走向出口时想动却连着这么多步没挪动,就不等 <see cref="StallSteps"/>,马上改用寻路。出口前面的口袋(主角落在出口门体的缺口里)直线走不出去,
        /// 摆脱卡住的上下乱晃又恰好把主角顶出刚进来的出口,来回换房间走不完(这种死循环与房间里新摆的物件无关,是把自动机运气好的那一局打破了)。
        /// </summary>
        public const int ExitStuckSteps = 12;

        /// <summary>寻路时连着这么多步没挪动,就朝路点两个方向都推一下(见 <see cref="SteerByPath"/>)。</summary>
        public const int NudgeAfter = 4;

        /// <summary>寻路时往前看几格(格边长见 <see cref="WalkPathfinder.Cell"/>),朝那个格子的中心走。</summary>
        public const int PathLookahead = 6;

        /// <summary>寻路最近一次给出的路点与主角所在格的代价(诊断用)。</summary>
        public string PathReport { get; private set; } = string.Empty;

        /// <summary>最近一步的意图(诊断用):打怪、走向哪个出口、寻路走向哪里,以及最后给出的方向。</summary>
        public string Intent { get; private set; } = string.Empty;

        /// <summary>到过寻路目标后,主角离到达点在这个范围(像素,x、y 各自)内都交给直线逻辑,不再被寻路拽回去。</summary>
        public const double ArriveSlack = 10;

        /// <summary>离寻路目标的代价不超过它(一个宽格,约 2 像素)就算到了。</summary>
        public const int ArriveCost = 1;

        /// <summary>寻路目标是怪物时,怪物挪出这么远(像素)就重新算一遍目标。</summary>
        public const double RetargetDistance = 16;

        /// <summary>走向上下出口时,目标点放在出口线外这么远(像素):只要在出口附近就一直往外顶,由可走带的边缘挡住并触发出口。</summary>
        public const double PushBeyondEdge = 200;

        /// <summary>实时运行时一帧里最多补几个心跳(见 <see cref="Advance"/>)。</summary>
        public const int MaxBeatsPerFrame = 12;

        /// <summary>实时运行时最多欠多少毫秒的心跳:卡顿很久之后只补这么多,其余丢掉。</summary>
        public const double MaxDebtMs = 200.0;

        /// <summary>房间清空后捡金币最多花多少个心跳(够不着、走不到的金币不能拖住自动机;证据 E-116 的拾取是玩家自己走过去捡)。</summary>
        public const int GoldChaseSteps = 480;

        /// <summary>捡金币时横向走到离它这么近(像素)就停,小于拾取范围 <see cref="FieldDrop.PickupHalfWidth"/>。</summary>
        public const double GoldStopX = 16;

        /// <summary>捡金币时进深方向走到离它这么近(像素)就停,小于拾取范围 <see cref="FieldDrop.PickupHalfDepth"/>。</summary>
        public const double GoldStopY = 5;

        /// <summary>每个房间里打物件最多花多少个心跳(够不着、被挡住的物件不能拖住自动机;见 <see cref="SmashObjects"/>)。</summary>
        public const int SmashSteps = 3600;

        /// <summary>同一个物件最多按多少次攻击键(只触发一次动作的、打不烂的物件不能一直打)。</summary>
        public const int SmashPresses = 12;

        /// <summary>打同一个物件最多花多少个心跳(走不到、打不着的物件不能一直占着房间的预算)。</summary>
        public const int SmashTargetSteps = 360;

        /// <summary>
        /// 寻路找不到路连着这么多步,就当作被物件挡住了(M34,证据 E-126):不管 <see cref="SmashObjects"/> 开没开,先把最近的可破坏物件打翻一个再重新找路
        /// (原版里挡路的物件靠玩家自己打碎;加载物件之前房间里没有这些障碍,自动机也没有这个动作)。
        /// </summary>
        public const int BreakoutAfter = 60;

        private readonly bool _explore;
        private double _debtMs;
        private int _steps;
        private double _lastX = double.NaN;
        private double _lastY = double.NaN;
        private int _stuck;
        private int _wiggle;
        private double _prevY = double.NaN;
        private int _yBlocked;

        /// <summary>这个房间里已经花在捡金币上的心跳数(换房间清零)。</summary>
        private int _goldSteps;

        /// <summary>这一步在捡金币(给 <see cref="Intent"/> 看)。</summary>
        private bool _collecting;

        /// <summary>
        /// 为真时,房间清空、金币捡完之后顺手把房间里的可破坏物件打翻(M34,证据 E-126):走到最近的、还没打翻也没触发过的物件跟前连续普攻,
        /// 打翻(或按了 <see cref="SmashPresses"/> 次攻击键还没动静)再找下一个,房间里最多花 <see cref="SmashSteps"/> 个心跳;物件里掉出的东西由同一套捡拾逻辑捡起。
        /// 默认关:现有的整座副本检查、扫描与对拍都不带它,路线不变;它只是测试工具(原版里是玩家自己去打物件),不代表原版的 AI。
        /// </summary>
        public bool SmashObjects { get; set; }

        /// <summary>这个房间里已经花在打物件上的心跳数(换房间清零)。</summary>
        private int _smashSteps;

        /// <summary>当前这个物件已经按了几次攻击键。</summary>
        private int _smashPresses;

        private BreakableObject _smashTarget;
        /// <summary>已经处理过(打翻了、触发了、挥够了次数、够不着)的物件;不随换房间清掉:离开房间又回来时不再回头去打同一个够不着的物件。</summary>
        private readonly HashSet<BreakableObject> _smashDone = new HashSet<BreakableObject>();

        /// <summary>各个房间已经花在打物件上的心跳数(<see cref="SmashSteps"/> 是每个房间一共的预算,进出房间不重新计)。</summary>
        private readonly Dictionary<RoomCombatSession, int> _smashUsed = new Dictionary<RoomCombatSession, int>();

        /// <summary>打物件的状态(预算、目标)现在属于哪一间房间(见 <see cref="EnterSmashRoom"/>)。</summary>
        private RoomCombatSession _smashRoom;

        /// <summary>寻路连着找不到通向当前物件的路的步数(到 <see cref="PathFailedSteps"/> 就放弃这个物件)。</summary>
        private int _smashPathFails;

        /// <summary>这一步在打物件(给 <see cref="Intent"/> 看)。</summary>
        private bool _smashing;

        /// <summary>当前这个物件已经花了几个心跳。</summary>
        private int _smashTargetSteps;

        /// <summary>这一步寻路找不到路(<see cref="SteerByPath"/> 里记,<see cref="Next"/> 里清)。</summary>
        /// <summary>现任目标(换目标要有明显更近的理由,见 <see cref="Nearest"/>)。</summary>
        private MonsterActor _target;
        private const double TargetKeepRatio = 1.5;
        private const double TargetKeepSlack = 24.0;

        private bool _pathFailed;

        /// <summary>寻路连着找不到路的步数。</summary>
        private int _blockedSteps;

        /// <summary>正在清障:被物件挡住了,先打翻最近的物件(见 <see cref="BreakoutAfter"/>)。</summary>
        private bool _breakout;

        /// <summary>开始清障时房间里已经打翻了几个物件;多于它说明挡路的东西被清掉了一个。</summary>
        private int _breakDestroyed;

        /// <summary>寻路的格子表按房间里已打翻 N 个物件时建的。</summary>
        private int _seenDestroyed;

        // 寻路:每间房间一张格子表;目标走不近时(见 StallSteps)对这个目标改走寻路,直到换目标或换房间
        private RoomCombatSession _pathRoom;
        private WalkPathfinder _finder;
        private int[] _field;
        private string _fieldKey;
        private string _arrivedKey;
        private double _arrivedX, _arrivedY;
        private double _fieldX = double.NaN;
        private double _fieldY = double.NaN;
        private string _watchKey;
        private string _latchedKey;
        private double _bestDist;
        private int _noProgress;

        /// <summary>寻路接管过几次(目标走不近时);诊断用。</summary>
        public int PathEngaged { get; private set; }

        /// <summary>寻路接管时找不到路的步数;诊断用,不为 0 说明有够不着的目标。</summary>
        public int PathBlockedSteps { get; private set; }

        /// <summary>
        /// <paramref name="explore"/> 为真时先把首领格以外的每一格都走进去一遍(每次走向最近的没进过的格),最后才去首领格;
        /// 为假时直接走到首领格的最短路。
        /// </summary>
        public DungeonBot(bool explore = false)
        {
            _explore = explore;
        }

        /// <summary>已经决策的步数。</summary>
        public int Steps => _steps;

        /// <summary>
        /// 从时刻 0 起按固定的 1/60 秒步长让自动机打副本,一直推进到模拟时刻不早于 <paramref name="toMs"/>,或者副本走完(首领清空 / 走出迷宫)为止;
        /// 返回停下时的模拟时刻(毫秒)。命令行 dungeon-check --bot 与播放器的 -dnfbot -dnfplayat 都是这一局,所以两边能逐帧对拍。
        /// <paramref name="afterClearMs"/> 大于 0 时,副本走完之后再让时间走这么久(主角站着不动,自动机不再决策)才停,结算画面(<see cref="DungeonSession.SettlementActive"/>)就是这样出现的;
        /// 与命令行 dungeon-check 的 --after-clear 同一个规则(走完那一步的时刻起算)。
        /// </summary>
        public double RunUntil(DungeonSession session, double toMs, double afterClearMs = 0.0)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            double t = 0;
            double finishedAt = -1;
            while (t < toMs && !session.Failed && (!session.Finished || (finishedAt >= 0 && t - finishedAt < afterClearMs)))
            {
                bool attack = Next(session, out int dx, out int dy);   // 走完之后全是 0 / false:只让时间走
                session.Update(CombatScript.StepMs, dx, dy, attack);
                t += CombatScript.StepMs;
                if (session.Finished && finishedAt < 0)
                {
                    finishedAt = t;
                }
            }
            return t;
        }

        /// <summary>
        /// 实时运行用:过去了一帧、<paramref name="deltaMs"/> 毫秒,按固定的 1/60 秒"心跳"补步——每个心跳先 <see cref="Next"/> 再让会话推进 <see cref="CombatScript.StepMs"/>,
        /// 直到欠的时间不足一个心跳(一帧最多补 <see cref="MaxBeatsPerFrame"/> 个,欠账不超过 <see cref="MaxDebtMs"/>)。返回这一帧补了几个心跳。
        /// 自动机的走位死区、出手间隔与卡住检测都是按心跳数写的,按可变的帧长直接喂给它,帧长一大(60 毫秒以上)就会在怪物旁来回晃、整座副本打不完;
        /// 按心跳补步,同一个会话不管帧长是多少、抖不抖,打出的是同一局(与 <see cref="RunUntil"/>、命令行 dnf dungeon-check --bot 一致)。
        /// </summary>
        public int Advance(DungeonSession session, double deltaMs)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            _debtMs = Math.Min(_debtMs + Math.Max(0.0, deltaMs), MaxDebtMs);
            int beats = 0;
            while (beats < MaxBeatsPerFrame && _debtMs >= CombatScript.StepMs && !session.Failed && (!session.Finished || WantsToCollect(session)))
            {
                bool attack = Next(session, out int dx, out int dy);
                session.Update(CombatScript.StepMs, dx, dy, attack);
                _debtMs -= CombatScript.StepMs;
                beats++;
            }
            return beats;
        }

        /// <summary>
        /// 副本走完(首领清空)之后自动机还想做的事:结算菜单还没选、主角活着、地上还有金币、捡金币的步数没用完。
        /// 为真时 <see cref="Advance"/> 与 <see cref="Next"/> 照常给出方向(别的不做),调用方不能在副本走完时就让主角站着。
        /// </summary>
        public bool WantsToCollect(DungeonSession session)
        {
            return session != null && session.BossCleared && session.Choice == SettlementChoice.None && !session.Failed && !session.HeroDead
                && _goldSteps < GoldChaseSteps && HasCollectable(session.Current);
        }

        /// <summary>地上有没有捡得了的东西(金币,或背包放得下的物品;够得着却放不下、按过一次攻击键的物品不算)。</summary>
        private static bool HasCollectable(RoomCombatSession fight)
        {
            foreach (FieldDrop d in fight.Drops)
            {
                if (!d.Blocked)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>读会话当前的状态,给出这一步的方向与是否按攻击键。副本已经走完时,除了 <see cref="WantsToCollect"/> 的捡金币,全部为 0 / false。</summary>
        public bool Next(DungeonSession session, out int dirX, out int dirY)
        {
            dirX = 0;
            dirY = 0;
            _steps++;
            if (session.Finished)
            {
                // 首领清空之后主角仍可操作:结算菜单还没选之前,把地上(首领掉的)金币捡完;别的什么也不做
                _collecting = false;
                if (WantsToCollect(session))
                {
                    RoomCombatSession last = session.Current;
                    if (Collect(last, last.Hero, out dirX, out dirY))
                    {
                        Intent = "捡金币 方向 (" + dirX + "," + dirY + ")";
                    }
                    return last.SelectedDrop != null;      // 物品要按攻击键才捡(证据 E-122)
                }
                return false;
            }
            if (session.HeroDead)
            {
                // 主角死了:复活窗口里能复活就按复活键(与玩家一样是攻击键),否则站着等(窗口没开、没币、到上限),失败计时走完流程自会回城(证据 E-115)
                _stuck = 0;
                _lastX = double.NaN;
                _lastY = double.NaN;
                Intent = session.CanRevive ? "复活" : "等待复活窗口";
                return session.CanRevive;
            }
            if (session.InTransition)
            {
                // 转场(淡出、全黑、淡入)里主角不会动:不能把它算成"想动却动不了"(否则一换房间就触发卡住检测,摆脱卡住的上下乱晃又把主角顶出刚进来的门)
                _stuck = 0;
                _lastX = double.NaN;
                _lastY = double.NaN;
                return false;
            }
            RoomCombatSession fight = session.Current;
            TownHero hero = fight.Hero;
            if (!ReferenceEquals(fight, _pathRoom))
            {
                _goldSteps = 0;
                EnterSmashRoom(fight);
                _blockedSteps = 0;
                _breakout = false;
                _seenDestroyed = fight.ObjectsDestroyed;
                _wiggle = 0;
                _stuck = 0;
                _lastX = double.NaN;
                _lastY = double.NaN;
                _pathRoom = fight;
                _finder = null;
                _field = null;
                _fieldKey = null;
                _arrivedKey = null;
                _watchKey = null;
                _latchedKey = null;
            }
            if (fight.ObjectsDestroyed != _seenDestroyed)
            {
                // 房间里有物件被打翻(可能是挡路的):寻路的格子表是按打翻之前的障碍物建的,重建
                _seenDestroyed = fight.ObjectsDestroyed;
                _finder = null;
                _field = null;
                _fieldKey = null;
                _arrivedKey = null;
                _watchKey = null;
                _latchedKey = null;
            }
            _pathFailed = false;
            _collecting = false;
            _smashing = false;
            if (!_breakout && _blockedSteps >= BreakoutAfter)
            {
                _blockedSteps = 0;
                _breakout = _smashSteps < SmashSteps && NearestBreakable(fight, hero) != null;
                _breakDestroyed = fight.ObjectsDestroyed;
            }
            if (_breakout)
            {
                if (fight.ObjectsDestroyed == _breakDestroyed && Smash(fight, hero, out dirX, out dirY, out bool breakAttack, true))
                {
                    Unstick(hero, ref dirX, ref dirY);
                    Intent = "清障 打物件 方向 (" + dirX + "," + dirY + ")" + (breakAttack ? " 出手" : string.Empty);
                    return breakAttack;
                }
                _breakout = false;       // 挡路的东西清掉了一个(或没有可打的了):回到平常的打法
                dirX = 0;
                dirY = 0;
            }
            MonsterActor target = Nearest(fight, hero);
            bool attack = false;
            if (target != null)
            {
                double dx = target.X - hero.X;
                double dy = target.Y - hero.Y;
                dirY = Math.Abs(dy) > ReachY * 0.5 ? Math.Sign(dy) : 0;
                if (Math.Abs(dx) > ReachX)
                {
                    dirX = Math.Sign(dx);
                }
                else if (dx != 0 && (dx < 0) != hero.FacingLeft)
                {
                    // 背对着怪物:朝它挪一步把身子转过来。离得比一步还近时这一步会越过怪物、又背对着它(来回晃一辈子):
                    // 先朝外退到够一步的距离,再转回来
                    dirX = Math.Abs(dx) < TurnGap ? -Math.Sign(dx) : Math.Sign(dx);
                }
                // 想往上下挪却一直挪不动(可走带的边缘或障碍物挡着,怪物站在主角够不着的一条线上):横向够得着就照样挥击,打得到与否由命中判定说了算
                bool yMoved = double.IsNaN(_prevY) || Math.Abs(hero.Y - _prevY) > 0.2;
                _yBlocked = dirY != 0 && !yMoved ? _yBlocked + 1 : 0;
                _prevY = hero.Y;
                bool lineBlocked = _yBlocked >= YBlockedSteps && (Math.Abs(dy) <= YBlockedReachY || _blockedSteps >= PathFailedSteps);
                bool inLine = Math.Abs(dx) <= ReachX && (Math.Abs(dy) <= ReachY || lineBlocked);
                bool facing = dx == 0 || (dx < 0) == hero.FacingLeft;
                if (inLine && facing && _steps % AttackEvery == 0)
                {
                    attack = true;
                }
                // 够不着(直线走不近):改走寻路,目标是怪物身边打得到的那一带
                double gap = Math.Max(0.0, Math.Abs(dx) - ReachX) + 4.0 * Math.Max(0.0, Math.Abs(dy) - ReachY);
                if (UsePath("m" + target.Index, gap) && gap > 0)
                {
                    double goalX0 = target.X - ReachX * 0.8, goalX1 = target.X + ReachX * 0.8, goalY0 = target.Y - 4, goalY1 = target.Y + 4;
                    SteerByPath(fight, hero, "m" + target.Index, (x, y) => x >= goalX0 && x <= goalX1 && y >= goalY0 && y <= goalY1,
                        target.X, target.Y, ref dirX, ref dirY);
                }
            }
            else
            {
                _yBlocked = 0;
                if (AllGone(fight))
                {
                    // 先把地上的金币捡完(限时),带 SmashObjects 时再把房间里的物件打翻(掉出的东西下一步又归捡拾管),最后去出口
                    if (!Collect(fight, hero, out dirX, out dirY))
                    {
                        if (Smash(fight, hero, out dirX, out dirY, out bool smashAttack))
                        {
                            attack = smashAttack;
                        }
                        else
                        {
                            Navigate(session, fight, hero, out dirX, out dirY);
                        }
                    }
                }
            }
            Unstick(hero, ref dirX, ref dirY);
            _blockedSteps = _pathFailed ? _blockedSteps + 1 : 0;
            if (fight.SelectedDrop != null)
            {
                attack = true;      // 够得着物品:攻击键就是拾取键,先把它捡了(证据 E-122)
            }
            Intent = (target != null ? "打怪 #" + target.Index : _collecting ? "捡金币" : _smashing ? "打物件" : "出口") + (_wiggle > 0 ? " 摆脱卡住" : string.Empty) + (_arrivedKey != null ? " 到达" + _arrivedKey : string.Empty)
                + (_latchedKey != null ? " 寻路接管" + _latchedKey : string.Empty) + " 方向 (" + dirX + "," + dirY + ")" + (attack ? " 出手" : string.Empty);
            return attack;
        }

        /// <summary>
        /// 房间清空后朝最近的一堆金币走(落地前也往它的当前位置靠,它落在原处附近);够近了就站住等它落地、被捡起。
        /// 总共最多 <see cref="GoldChaseSteps"/> 步,用完就不管了;地上没有金币返回 false。
        /// </summary>
        private bool Collect(RoomCombatSession fight, TownHero hero, out int dirX, out int dirY)
        {
            dirX = 0;
            dirY = 0;
            if (_goldSteps >= GoldChaseSteps)
            {
                return false;
            }
            FieldDrop best = null;
            double bestD = double.MaxValue;
            foreach (FieldDrop d in fight.Drops)
            {
                if (d.Blocked)
                {
                    continue;      // 背包放不下的物品:走过去也捡不了,别追
                }
                double dist = Math.Abs(d.X - hero.X) + 4 * Math.Abs(d.Y - hero.Y);
                if (dist < bestD)
                {
                    bestD = dist;
                    best = d;
                }
            }
            if (best == null)
            {
                return false;
            }
            _goldSteps++;
            _collecting = true;
            double dx = best.X - hero.X;
            double dy = best.Y - hero.Y;
            dirX = Math.Abs(dx) > GoldStopX ? Math.Sign(dx) : 0;
            dirY = Math.Abs(dy) > GoldStopY ? Math.Sign(dy) : 0;
            return true;
        }

        /// <summary>
        /// 打开 <see cref="SmashObjects"/> 时,房间清空、金币捡完之后朝最近的一个还没打翻也没触发过的可破坏物件走去、站到够得着的地方连续普攻;
        /// 物件打翻了(或者按了 <see cref="SmashPresses"/> 次攻击键还没有动静、或者它已经触发过)就记下来,不再回头。返回 false 表示这一步没有可做的(没打开、没有物件、预算用完),
        /// 由调用方去走出口。走位与怪物一样按物件受击框中心的横向距离与它所在线的纵向距离(<see cref="ReachX"/>、<see cref="ReachY"/>),够不着的物件靠每个房间的预算 <see cref="SmashSteps"/> 兜底。
        /// </summary>
        public bool Smash(RoomCombatSession fight, TownHero hero, out int dirX, out int dirY, out bool attack, bool force = false)
        {
            dirX = 0;
            dirY = 0;
            attack = false;
            EnterSmashRoom(fight);
            if ((!SmashObjects && !force) || _smashSteps >= SmashSteps)
            {
                return false;
            }
            if (_smashTarget != null && (_smashTarget.Destroyed || _smashTarget.Triggered || _smashPresses >= SmashPresses || _smashTargetSteps >= SmashTargetSteps))
            {
                _smashDone.Add(_smashTarget);
                _smashTarget = null;
            }
            if (_smashTarget == null)
            {
                _smashTarget = NearestBreakable(fight, hero);
                _smashPresses = 0;
                _smashTargetSteps = 0;
                _smashPathFails = 0;
                if (_smashTarget == null)
                {
                    return false;
                }
            }
            _smashSteps++;
            _smashTargetSteps++;
            _smashing = true;
            var box = _smashTarget.HurtBoxes[0];
            double dx = box.CenterX - hero.X;
            double dy = _smashTarget.Object.Y - hero.Y;
            if (Math.Abs(dx) > ReachX || Math.Abs(dy) > ReachY)
            {
                // 够不着:顺着寻路走到物件身边(直线走会顶在可走带的边缘上,主角恰好落在出口前面时一顶就换了房间,换回来又顶,走不完)
                string key = "b" + IndexOfBreakable(fight, _smashTarget);
                double gx0 = box.CenterX - ReachX * 0.95, gx1 = box.CenterX + ReachX * 0.95;
                double gy0 = _smashTarget.Object.Y - ReachY * 0.5, gy1 = _smashTarget.Object.Y + ReachY * 0.5;
                if (SteerByPath(fight, hero, key, (x, y) => x >= gx0 && x <= gx1 && y >= gy0 && y <= gy1, box.CenterX, _smashTarget.Object.Y, ref dirX, ref dirY))
                {
                    _smashPathFails = 0;
                    return true;
                }
                if (_pathFailed)
                {
                    // 找不到路:原地不动(别往可走带的边缘乱顶),几步之后换下一个物件
                    dirX = 0;
                    dirY = 0;
                    if (++_smashPathFails >= PathFailedSteps)
                    {
                        _smashDone.Add(_smashTarget);
                        _smashTarget = null;
                        _smashPathFails = 0;
                    }
                    return true;
                }
            }
            dirY = Math.Abs(dy) > ReachY * 0.5 ? Math.Sign(dy) : 0;
            if (Math.Abs(dx) > ReachX)
            {
                dirX = Math.Sign(dx);
            }
            else if (dx != 0 && (dx < 0) != hero.FacingLeft)
            {
                dirX = Math.Abs(dx) < TurnGap ? -Math.Sign(dx) : Math.Sign(dx);      // 背对着物件:与打怪物一样先转身
            }
            bool inLine = Math.Abs(dx) <= ReachX && Math.Abs(dy) <= ReachY;
            bool facing = dx == 0 || (dx < 0) == hero.FacingLeft;
            if (inLine && facing && _smashSteps % AttackEvery == 0)
            {
                attack = true;
                _smashPresses++;
            }
            return true;
        }

        /// <summary>
        /// 打物件的状态跟着房间走:换了房间就把上一间的步数预算记下来(进出房间不重新计,否则够不着的物件会让主角在两间房之间来回晃一辈子),
        /// 手里还没打到的物件当作在那间房里够不着、以后不再回头(走过去的路上就出了房间,说明路过出口),再取新房间已经花掉的预算,换一个空的目标。
        /// </summary>
        private void EnterSmashRoom(RoomCombatSession fight)
        {
            if (ReferenceEquals(fight, _smashRoom))
            {
                return;
            }
            if (_smashRoom != null)
            {
                _smashUsed[_smashRoom] = _smashSteps;
            }
            if (_smashTarget != null)
            {
                _smashDone.Add(_smashTarget);
            }
            _smashRoom = fight;
            _smashSteps = _smashUsed.TryGetValue(fight, out int used) ? used : 0;
            _smashPresses = 0;
            _smashTarget = null;
            _smashTargetSteps = 0;
            _smashPathFails = 0;
        }

        private static int IndexOfBreakable(RoomCombatSession fight, BreakableObject target)
        {
            for (int i = 0; i < fight.Breakables.Count; i++)
            {
                if (ReferenceEquals(fight.Breakables[i], target))
                {
                    return i;
                }
            }
            return -1;
        }

        private BreakableObject NearestBreakable(RoomCombatSession fight, TownHero hero)
        {
            BreakableObject best = null;
            double bestD = double.MaxValue;
            foreach (BreakableObject b in fight.Breakables)
            {
                if (b.Destroyed || b.Triggered || b.HurtBoxes.Count == 0 || _smashDone.Contains(b))
                {
                    continue;
                }
                double d = Math.Abs(b.HurtBoxes[0].CenterX - hero.X) + 4 * Math.Abs(b.Object.Y - hero.Y);
                if (d < bestD)
                {
                    bestD = d;
                    best = b;
                }
            }
            return best;
        }

        /// <summary>房间里该打的怪物都没了(没有任何受击框、永远打不到的不算,与 <see cref="DungeonSession"/> 的清房判定一致)。</summary>
        private static bool AllGone(RoomCombatSession fight)
        {
            foreach (MonsterActor m in fight.Monsters)
            {
                if (!m.IsGone && m.CanEverBeHit)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>自动机地面攻击够得着的最大离地高度(像素):更高的怪物(脚本把自己升到半空的)先放着,打别的,等它落下来再打(E-133)。</summary>
        private const double ReachZ = 60.0;

        /// <summary>
        /// 最近的活怪物。当前帧没有受击框的怪物(藏起来的、变身中的、只能等脚本放它出来的)和飞在半空够不着的怪物打不到,
        /// 先挑打得到的;全都打不到时才退而求其次靠近最近的那只等它现身,免得自动机对着一个打不中的怪物挥一辈子。
        /// </summary>
        private MonsterActor Nearest(RoomCombatSession fight, TownHero hero)
        {
            MonsterActor best = null, bestAny = null;
            double bestD = double.MaxValue, bestAnyD = double.MaxValue;
            double keptD = double.MaxValue;
            foreach (MonsterActor m in fight.Monsters)
            {
                if (!m.IsAlive || !m.CanEverBeHit)
                {
                    continue;
                }
                double d = Math.Abs(m.X - hero.X) + 4 * Math.Abs(m.Y - hero.Y);
                if (d < bestAnyD)
                {
                    bestAnyD = d;
                    bestAny = m;
                }
                bool hittable = m.HasDamageBox && m.Z <= ReachZ;
                if (d < bestD && hittable)
                {
                    bestD = d;
                    best = m;
                }
                if (m == _target && hittable)
                {
                    keptD = d;
                }
            }
            // 现任目标还活着、打得到、也没远到哪里去:接着打它。多只怪物叠在同一点时"最近"每一步都在换人,
            // 寻路按目标记的状态(路线、到达)每次都清零,自动机在原地来回晃一辈子
            if (_target != null && keptD < double.MaxValue && keptD <= bestD * TargetKeepRatio + TargetKeepSlack)
            {
                return _target;
            }
            _target = best ?? bestAny;
            return _target;
        }

        private void Navigate(DungeonSession session, RoomCombatSession fight, TownHero hero, out int dirX, out int dirY)
        {
            dirX = 0;
            dirY = 0;
            if (!NextSide(session, _explore, out GateSide side))
            {
                return;
            }
            int[] g = fight.Room.Map.Gates[(int)side];
            if (g == null)
            {
                return;
            }
            double tx, ty;
            switch (side)
            {
                case GateSide.Left:
                    tx = g[0] - 8;
                    ty = GateEntryY(fight, hero, side, g[1]);
                    break;
                case GateSide.Right:
                    tx = g[0] + 8;
                    ty = GateEntryY(fight, hero, side, g[1]);
                    break;
                case GateSide.Up:
                    tx = g[0];
                    ty = g[1] - PushBeyondEdge;                          // 上下出口靠"顶着可走带的边缘往外走"触发:目标放在带外,一直往外顶
                    break;
                default:
                    tx = g[0];
                    ty = g[1] + PushBeyondEdge;
                    break;
            }
            // 出口走不近(隔着障碍、悬崖或另一层的可走带):改走寻路,走到出口前面那一带;最后几步仍由下面的"顶着出口走"完成
            string pathKey = "g" + (int)side;
            if (UsePath(pathKey, Math.Abs(tx - hero.X) + Math.Abs(ty - hero.Y), latchWhenStuck: true))
            {
                int pathX = 0, pathY = 0;
                if (SteerByPath(fight, hero, pathKey, GateGoal(fight, hero, side, g), 0, 0, ref pathX, ref pathY))
                {
                    dirX = pathX;
                    dirY = pathY;
                    return;
                }
                if (_arrivedKey == pathKey)
                {
                    // 到了出口前面:只顶着出口走(左右出口不再动 y、上下出口不再动 x),别又被下面的直线逻辑带歪
                    dirX = side == GateSide.Left ? -1 : (side == GateSide.Right ? 1 : 0);
                    dirY = side == GateSide.Up ? -1 : (side == GateSide.Down ? 1 : 0);
                    return;
                }
            }
            // 左右出口:x 一直往出口顶,y 不在出口识别矩形里时顺带挪进去(门体只盖在出口那一段 y 上,在别处顶着墙进不了门);上下出口先对齐 x 再走 y
            if (Math.Abs(tx - hero.X) > Deadband || side == GateSide.Left || side == GateSide.Right)
            {
                dirX = tx < hero.X ? -1 : (tx > hero.X ? 1 : 0);
            }
            if ((side == GateSide.Left || side == GateSide.Right) && Math.Abs(ty - hero.Y) > 1.5)
            {
                dirY = ty < hero.Y ? -1 : 1;
            }
            if (side == GateSide.Up || side == GateSide.Down)
            {
                if (Math.Abs(tx - hero.X) <= 40)
                {
                    dirY = ty < hero.Y ? -1 : (ty > hero.Y ? 1 : 0);
                }
            }
        }

        /// <summary>
        /// 对 <paramref name="key"/> 这个目标,<paramref name="dist"/>(离目标的距离)连续 <see cref="StallSteps"/> 步没有变近就返回 true,
        /// 此后对同一个目标一直返回 true(直到换目标或换房间)。距离在变近时返回 false:照旧直线走。
        /// </summary>
        private bool UsePath(string key, double dist, bool latchWhenStuck = false)
        {
            if (_latchedKey == key)
            {
                return true;
            }
            if (latchWhenStuck && _stuck >= ExitStuckSteps)
            {
                _latchedKey = key;
                PathEngaged++;
                return true;
            }
            if (_watchKey != key)
            {
                _watchKey = key;
                _bestDist = dist;
                _noProgress = 0;
                return false;
            }
            if (dist < _bestDist - 1.0)
            {
                _bestDist = dist;
                _noProgress = 0;
            }
            else
            {
                _noProgress++;
            }
            if (_noProgress >= StallSteps)
            {
                _latchedKey = key;
                PathEngaged++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 顺着寻路往 <paramref name="goal"/>(格子中心的世界坐标是不是目标)走一步:把方向写进 dirX、dirY 并返回 true。
        /// 主角已经在目标里、或这间房里到不了目标时返回 false(方向不动);到不了的步数记在 <see cref="PathBlockedSteps"/>。
        /// 同一个 <paramref name="key"/> 下目标中心 (<paramref name="cx"/>, <paramref name="cy"/>) 没挪出 <see cref="RetargetDistance"/> 就复用上次算好的步数表(出口不会动;怪物会动,挪远了才重算)。
        /// </summary>
        private bool SteerByPath(RoomCombatSession fight, TownHero hero, string key, Func<double, double, bool> goal, double cx, double cy,
            ref int dirX, ref int dirY)
        {
            if (_finder == null)
            {
                RoomMap map = fight.Room.Map;
                _finder = new WalkPathfinder(hero.Walk, hero.Blocker, map.Width + 32, map.HeightPixels + 32);
                _field = null;
                _fieldKey = null;
            }
            if (_finder.CellCount == 0)
            {
                return false;
            }
            if (_field == null || _fieldKey != key || double.IsNaN(_fieldX) || Math.Abs(cx - _fieldX) > RetargetDistance || Math.Abs(cy - _fieldY) > RetargetDistance)
            {
                _field = _finder.Field(goal);
                _arrivedKey = null;
                _fieldKey = key;
                _fieldX = cx;
                _fieldY = cy;
            }
            if (!_finder.TryWaypoint(_field, hero.X, hero.Y, PathLookahead, out double wx, out double wy, out int cost))
            {
                PathBlockedSteps++;
                _pathFailed = true;
                return false;
            }
            PathReport = "所在格代价 " + cost + ",路点 (" + wx + "," + wy + ")";
            if (_arrivedKey == key)
            {
                // 到过目标:顶着出口走、站在怪物身边打的最后几步交给直线逻辑,别又被拽回格子中心(顶门要连着顶一小会儿才触发);走出一小段才重新接管
                if (Math.Abs(hero.X - _arrivedX) <= ArriveSlack && Math.Abs(hero.Y - _arrivedY) <= ArriveSlack)
                {
                    return false;
                }
                _arrivedKey = null;
            }
            if (cost <= ArriveCost)
            {
                _arrivedKey = key;
                _arrivedX = hero.X;
                _arrivedY = hero.Y;
                return false;
            }
            int stepX = Math.Abs(wx - hero.X) > 0.5 ? (wx < hero.X ? -1 : 1) : 0;   // 窄道只比格子中心偏 1~2 像素也得挪过去,不然一直顶着墙
            int stepY = Math.Abs(wy - hero.Y) > 0.5 ? (wy < hero.Y ? -1 : 1) : 0;
            if (_stuck >= NudgeAfter)
            {
                // 想往路点挪却连着几步没挪动:格子中心附近的障碍边界不一定正好在格子边上,差零点几像素(没到 0.5 的门槛)就是过不去。朝路点那一侧推一下,
                // 主角一步走三四像素,会越过那条边界
                if (stepX == 0 && wx != hero.X)
                {
                    stepX = wx < hero.X ? -1 : 1;
                }
                if (stepY == 0 && wy != hero.Y)
                {
                    stepY = wy < hero.Y ? -1 : 1;
                }
            }
            if (stepX == 0 && stepY == 0)
            {
                return false;
            }
            dirX = stepX;
            dirY = stepY;
            return true;
        }

        /// <summary>
        /// 寻路去某个出口时的目标。有门体的左右出口:"往门这一侧挪十二像素占位盒就压到门体(上下各三像素也压到)"的那些格子(门洞是楔形的,这一圈格子不在一条直线上,
        /// 站上去顶着走就会触发);没有门体(或门体不在出口那一行)的左右出口:离出口横向 <see cref="DungeonSession.TriggerReachSide"/> 像素以内、
        /// 纵向在识别矩形里的整条带;上下出口:占位盒压到识别矩形的那些格子(朝出口方向走就触发)。
        /// </summary>
        private static Func<double, double, bool> GateGoal(RoomCombatSession fight, TownHero hero, GateSide side, int[] g)
        {
            RoomGate gate = null;
            foreach (RoomGate candidate in fight.Room.Content.Gates)
            {
                if (candidate.Side == side)
                {
                    gate = candidate;
                    break;
                }
            }
            if ((side == GateSide.Left || side == GateSide.Right) && gate != null && gate.BodyReachesGate(hero.BodyWidth, hero.BodyDepth))
            {
                GateBody body = gate.Body;
                double shift = side == GateSide.Left ? -12.0 : 12.0;   // 离门体 12 像素以内的一带(主角一步走三四像素,目标太窄会一步跨过去)
                int width = hero.BodyWidth, depth = hero.BodyDepth;
                // 上下各 3 像素也得压到门体:主角的 y 是小数,站在门体上沿的一条线上顶着走,差一像素就从门体上面或下面滑过去了
                return (x, y) => body.Overlaps(x + shift, y, width, depth) && body.Overlaps(x + shift, y - 3, width, depth) && body.Overlaps(x + shift, y + 3, width, depth);
            }
            WorldRect area = gate != null ? gate.Area : GateAreas.For(side, g[0], g[1]);
            switch (side)
            {
                case GateSide.Left:
                    {
                        // 朝出口走时只有 x 在动:纵向(y)要离矩形上下沿留出余量,否则落在边界上顶着走永远不触发。
                        // 出口位置落在可走区域外(走不到离出口十二像素以内)时,顶在识别矩形里的可走区域边界上也算
                        double limit = g[0] + DungeonSession.TriggerReachSide;
                        double top = area.Y + AreaMargin, bottom = area.Y + area.Height - AreaMargin;
                        double colLo = area.X, colHi = area.X + area.Width;
                        IWalkArea walk = hero.Walk;
                        IHeroBlocker blocker = hero.Blocker;
                        return (x, y) => y >= top && y < bottom && (x <= limit || (x >= colLo && x < colHi && !Open(walk, blocker, x - 2, y)));
                    }
                case GateSide.Right:
                    {
                        double limit = g[0] - DungeonSession.TriggerReachSide;
                        double top = area.Y + AreaMargin, bottom = area.Y + area.Height - AreaMargin;
                        double colLo = area.X, colHi = area.X + area.Width;
                        IWalkArea walk = hero.Walk;
                        IHeroBlocker blocker = hero.Blocker;
                        return (x, y) => y >= top && y < bottom && (x >= limit || (x >= colLo && x < colHi && !Open(walk, blocker, x + 2, y)));
                    }
                default:
                    {
                        // 上下出口:占位盒压到识别矩形,站在这里朝出口方向走就触发(不要求被边缘挡住)。朝出口走时只有 y 在动:横向(x)留出余量
                        int width = hero.BodyWidth, depth = hero.BodyDepth;
                        var inner = new WorldRect(area.X + AreaMargin, area.Y, Math.Max(1, area.Width - 2 * AreaMargin), area.Height);
                        return (x, y) => GateAreas.OverlapsBox(inner, (int)Math.Floor(x), (int)Math.Floor(y), width, depth);
                    }
            }
        }

        private static bool Open(IWalkArea walk, IHeroBlocker blocker, double x, double y)
        {
            return walk.Contains(x, y) && (blocker == null || blocker.BlockedBy(x, y) == null);
        }

        /// <summary>
        /// 左右出口:主角的占位盒真能碰到门体的那一行 y。门体只盖在出口位置那一行上下的几行(门洞是楔形的,上面的走廊是通的),
        /// 站在别的行顶着走不会触发;从出口位置的 y 起往上下各找,第一个门体盖得到占位盒的 y 再加 1 像素的余量。找不到就用出口位置的 y。
        /// </summary>
        private static double GateEntryY(RoomCombatSession fight, TownHero hero, GateSide side, double fallbackY)
        {
            foreach (RoomGate gate in fight.Room.Content.Gates)
            {
                if (gate.Side != side)
                {
                    continue;
                }
                if (!gate.BodyReachesGate(hero.BodyWidth, hero.BodyDepth))
                {
                    return gate.Y;
                }
                for (int d = 0; d <= GateRowReach; d++)
                {
                    if (gate.Body.Overlaps(gate.X, gate.Y + d, hero.BodyWidth, hero.BodyDepth))
                    {
                        return gate.Y + d + 1;
                    }
                    if (gate.Body.Overlaps(gate.X, gate.Y - d, hero.BodyWidth, hero.BodyDepth))
                    {
                        return gate.Y - d + 1;
                    }
                }
                return gate.Y;
            }
            return fallbackY;
        }

        /// <summary>
        /// 下一步该走哪一侧的出口。<paramref name="explore"/> 为假:走向首领格(没有首领格就走向起点格、再走它迷宫外的出口)的最短路;
        /// 为真:先走向最近的、首领格以外没进过的格(路上不穿过首领格),都进过了再去首领格。已在目标格、或没有路时返回 false。
        /// </summary>
        public static bool NextSide(DungeonSession session, bool explore, out GateSide side)
        {
            side = GateSide.Left;
            DungeonLayout layout = session.Layout;
            DungeonMaze maze = layout.Maze;
            int cols = maze.Cols;
            bool pending = false;
            if (explore)
            {
                for (int y = 0; y < maze.Rows && !pending; y++)
                {
                    for (int x = 0; x < cols && !pending; x++)
                    {
                        pending = !session.IsVisited(x, y) && !layout.IsBossCell(x, y);
                    }
                }
            }
            int start = session.CellY * cols + session.CellX;
            var from = new int[cols * maze.Rows];
            var via = new GateSide[cols * maze.Rows];
            int goal = FindGoal(session, pending, start, from, via);
            if (goal < 0 && pending)
            {
                goal = FindGoal(session, false, start, from, via); // 没进过的格只能穿过首领格才到:直接去首领格
            }
            if (goal < 0)
            {
                return false;
            }
            if (goal == start)
            {
                // 已在目标格:有迷宫外的出口就走它,没有就停
                foreach (GateSide s in new[] { GateSide.Right, GateSide.Up, GateSide.Left, GateSide.Down })
                {
                    if (maze.HasExit(session.CellX, session.CellY, s) && !maze.TryNeighbor(session.CellX, session.CellY, s, out _, out _))
                    {
                        side = s;
                        return true;
                    }
                }
                return false;
            }
            int step = goal;
            while (from[step] != start)
            {
                step = from[step];
            }
            side = via[step];
            return true;
        }

        /// <summary>
        /// 宽度优先找目标格:<paramref name="pending"/> 为真时目标是最近的、首领格以外没进过的格,路上不穿过首领格;否则目标是首领格(没有首领格就是起点格本身)。
        /// 返回目标格的序号,找不到返回 −1;<paramref name="from"/> 记每格的上一格(起点为 −1),<paramref name="via"/> 记走进这一格用的出口侧。
        /// </summary>
        private static int FindGoal(DungeonSession session, bool pending, int start, int[] from, GateSide[] via)
        {
            DungeonLayout layout = session.Layout;
            DungeonMaze maze = layout.Maze;
            int cols = maze.Cols;
            for (int i = 0; i < from.Length; i++)
            {
                from[i] = -2;
            }
            from[start] = -1;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % cols, cy = cur / cols;
                bool atGoal = pending
                    ? cur != start && !session.IsVisited(cx, cy) && !layout.IsBossCell(cx, cy)
                    : IsGoal(layout, cx, cy, cur != start);
                if (atGoal)
                {
                    return cur;
                }
                foreach (GateSide s in new[] { GateSide.Right, GateSide.Up, GateSide.Left, GateSide.Down })
                {
                    if (maze.TryNeighbor(cx, cy, s, out int nx, out int ny) && from[ny * cols + nx] == -2)
                    {
                        if (pending && layout.IsBossCell(nx, ny))
                        {
                            continue;
                        }
                        from[ny * cols + nx] = cur;
                        via[ny * cols + nx] = s;
                        queue.Enqueue(ny * cols + nx);
                    }
                }
            }
            return -1;
        }

        private static bool IsGoal(DungeonLayout layout, int x, int y, bool notStart)
        {
            if (layout.HasBoss)
            {
                return layout.IsBossCell(x, y);
            }
            return !notStart; // 没有首领格:目标就是起点格(单格副本),走它迷宫外的出口
        }

        /// <summary>
        /// 想动却几乎没动(被障碍物卡住)超过一小会儿,就垂直方向挪一阵再试。
        /// 挥击、命中停顿、受击、倒地的动画锁着人时本来就动不了,不算"卡住"(否则最后一击之后的那一小会儿被算成卡住,摆脱卡住的上下乱晃正好把站在下边缘的主角顶进下出口,
        /// 逛房间的路线就跳进了首领格)。
        /// </summary>
        private void Unstick(TownHero hero, ref int dirX, ref int dirY)
        {
            bool moved = double.IsNaN(_lastX) || Math.Abs(hero.X - _lastX) > 0.2 || Math.Abs(hero.Y - _lastY) > 0.2;
            _lastX = hero.X;
            _lastY = hero.Y;
            if ((dirX != 0 || dirY != 0) && !moved)
            {
                if (!hero.IsAttacking && !hero.IsHurt && !hero.IsDown)
                {
                    _stuck++;
                }
            }
            else
            {
                _stuck = 0;
            }
            if (_stuck > 30)
            {
                _wiggle = 40;
                _stuck = 0;
            }
            if (_wiggle > 0)
            {
                _wiggle--;
                dirY = (_steps / 40) % 2 == 0 ? 1 : -1;
            }
        }
    }
}
