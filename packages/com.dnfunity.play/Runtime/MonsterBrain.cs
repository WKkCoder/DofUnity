using System;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 默认 AI 里按类别决定走位细节的几个开关(证据 E-114、E-148):近战还是远程、被远处打中后怎么靠近、靠近时要不要霸体。
    /// 类别名就是 .mob 的 [category] 项:<c>[melee combat]</c>、<c>[range combat]</c>、<c>[close-passive]</c>、<c>[close-carefully]</c>、
    /// <c>[close-tough]</c>、<c>[close-manace]</c>、<c>[full-superarmor]</c>。
    /// </summary>
    public readonly struct MonsterBrainTraits
    {
        public MonsterBrainTraits(bool melee, bool ranged, bool passive, bool carefully, bool tough, bool manace, bool fullSuperArmor, int bodyWidth)
        {
            Melee = melee;
            Ranged = ranged;
            Passive = passive;
            Carefully = carefully;
            Tough = tough;
            Manace = manace;
            FullSuperArmor = fullSuperArmor;
            BodyWidth = bodyWidth;
        }

        /// <summary>[melee combat]:发现目标后进近战敌对(模式 9)。</summary>
        public bool Melee { get; }

        /// <summary>[range combat]:没有 [melee combat] 时发现目标后进远程敌对(模式 10);有这个类别的怪物不绕背。</summary>
        public bool Ranged { get; }

        /// <summary>[close-passive]:被远处打中靠近到位之后回游荡,靠近的终点在目标另一侧。</summary>
        public bool Passive { get; }

        /// <summary>[close-carefully]:同 <see cref="Passive"/>。</summary>
        public bool Carefully { get; }

        /// <summary>[close-tough]:靠近时直奔目标、期间带霸体。</summary>
        public bool Tough { get; }

        /// <summary>[close-manace]:靠近时停在目标靠自己的这一侧、期间带霸体。</summary>
        public bool Manace { get; }

        /// <summary>[full-superarmor]:本来就一直霸体,靠近时不再另开。</summary>
        public bool FullSuperArmor { get; }

        /// <summary>怪物的占位宽度([width] 的第一个数,像素):靠近时离终点 宽度/2 + 10 以内就算到了。</summary>
        public int BodyWidth { get; }
    }

    /// <summary>
    /// 怪物默认 AI 的"走位"部分(证据 E-114、E-148):游荡、发现目标、走向目标、被远处打之后靠近、绕背冲刺。规则与常数在 <see cref="MonsterMoveRules"/>,
    /// 这里是每只怪物一个的状态。服务端没有怪物 AI 的代码(只管掉落与物品),怪物行为由客户端的"模式机"决定(主机客户端算,其余客户端收同步)。
    /// <para>
    /// 流程(取自客户端旧式模式机 0x008822b0 的更新、0x0088d270 的模式更新、0x00889da0 的移动节拍、0x00891650 的方向选择):
    /// ① 出生在游荡模式(13);没有目标时每隔 1 秒找一次(主角在 [sight] × 1.4 之内就算有效目标,距离里纵向差按深度缩放折算),有了目标又丢出视野时立刻放弃;
    /// ② 找到目标过一会儿(<see cref="NoticeDelayMs"/>)进入敌对:有一半的几率先绕背冲刺(模式 11,远程怪物除外),否则近战进模式 9、远程进模式 10;
    /// ③ 每个模式有自己的节拍:到点才重新选方向,选定的方向一直走到下一次选择,走着与站着的间隔不同;游荡时 40% 的几率随机选一个斜向走、纵向再有一半被取消;
    /// ④ 模式更新每 333 毫秒一次:敌对模式(9、10)进入满 10001 毫秒回游荡(之后不会自动再接战,要等目标丢了又找到、或者又被远处打中);
    /// 绕背冲过目标就转敌对;靠近到位转敌对(被动类别回游荡);追击、迟疑里目标丢了回游荡(迟疑回站桩);
    /// ⑤ 被远处(距离不小于 150)的攻击打中:不在靠近中就进靠近模式(12,视野 ×1.5),朝攻击者靠近;绕背冲刺里挨了打转追击(4);冲刺或靠近里打中了主角也转追击;
    /// ⑥ 在走路的动作里、位置一秒没变(被墙或障碍挡住),转"反向"(21)走 501 毫秒,然后转追击。
    /// 怪物不是每一帧都朝主角校正,而是每隔约一秒校正一次,所以会冲过头、再折回来。
    /// </para>
    /// <para>
    /// 推断与经验值(都单独标出):<b>发现之后过 <see cref="NoticeDelayMs"/> 毫秒才进入敌对</b>是基线 S09b 里量到的迟滞(一个样本;客户端是把"目标改变"发给服务器、
    /// 再从服务器的回包里进入敌对模式,时延的来源没读到);客户端的各模式还有一组"速度修正"数组(进入模式时写进一个控制对象,敌对与绕背写 100、
    /// 对强悍与凶猛类别的靠近写 200),读不到谁消费它,这里不改速度;被打中后客户端还会按 70% 的几率叫附近的同伴也去靠近(0x0088f170),没做;
    /// 客户端的方向选择函数在怪物没有任何出招记录时直接返回(不设方向),没有接。
    /// </para>
    /// </summary>
    public sealed class MonsterBrain
    {
        /// <summary>发现目标到进入敌对模式的迟滞(毫秒)。经验值(非 REA):基线 S09b 里一只怪物出生后约 0.7 秒才掉头朝主角走,只有这一个样本。</summary>
        public const int NoticeDelayMs = 700;

        /// <summary>走路的动作里位置不变超过这么久(毫秒)算被挡住(0x0088a4c0)。</summary>
        public const int StuckMs = 1000;

        /// <summary>反向模式(21)持续这么久(毫秒)后转追击(0x0088d270)。</summary>
        public const int ReverseMs = 501;

        private readonly Func<int> _random;
        private readonly int _baseSight;
        private readonly int _warlike;
        private readonly int _roomWidth;
        private readonly MonsterBrainTraits _traits;
        private readonly bool _stationary;
        private int _sight;
        private long _nextTickMs;
        private long _lastSetMs = -MonsterMoveRules.FindIntervalMs;
        private long _lastModeUpdateMs;
        private long _modeStartMs;
        private bool _engagePending;
        private long _engageAtMs;
        private int _destX;
        private int _destY;
        private long _clockMs;
        private bool _positionsKnown;
        private int _selfX;
        private int _selfY;
        private int _heroX;
        private int _heroY;

        /// <param name="sight">[sight](没写取 <see cref="MonsterMoveRules.DefaultSight"/>);0 表示无限远。</param>
        /// <param name="warlike">[warlike](没写取 50)。</param>
        /// <param name="ranged">远程怪物(有 [range combat] 而没有 [melee combat]):敌对时与主角保持距离而不是贴上去。</param>
        /// <param name="roomWidth">房间的像素宽度:游荡时离两边不到 <see cref="MonsterMoveRules.EdgeMargin"/> 就掉头。</param>
        /// <param name="stationary">原地不动的怪物([fixture]、[stay]):不游荡、不找目标、不走。</param>
        /// <param name="random">客户端 rand() 的替身:返回非负整数(见 <see cref="Dnf.Render.ClientRandom"/>)。</param>
        public MonsterBrain(int sight, int warlike, bool ranged, int roomWidth, bool stationary, Func<int> random)
            : this(sight, warlike, new MonsterBrainTraits(!ranged, ranged, false, false, false, false, false, 40), roomWidth, stationary, random)
        {
        }

        /// <param name="traits">按 .mob 类别定下的细节(近战或远程、靠近的方式、霸体),见 <see cref="MonsterBrainTraits"/>。</param>
        public MonsterBrain(int sight, int warlike, MonsterBrainTraits traits, int roomWidth, bool stationary, Func<int> random)
        {
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _baseSight = sight < 0 ? 0 : sight;
            _sight = _baseSight;
            _warlike = warlike;
            _traits = traits;
            _roomWidth = roomWidth;
            _stationary = stationary;
            DirX = MonsterMoveRules.None;
            DirY = MonsterMoveRules.None;
        }

        /// <summary>
        /// 按 .mob 的类别决定这只怪物用不用走位大脑,用的话造一个(随机数从 <paramref name="seed"/> 起,每只怪物各一个状态):
        /// 有 [melee combat] 或 [range combat] 的怪物走对应的大脑;[stay] 的怪物是原地不动的大脑(客户端的找目标函数对它直接返回)。
        /// 其余返回 null,继续走占位:[oneside move](只走一个方向)、没有这几个类别的怪物(行为由 .ai 脚本指挥,我们还没有那套运行时)。
        /// </summary>
        public static MonsterBrain For(MonsterDef def, int roomWidth, uint seed)
        {
            if (def == null || HasCategory(def, "[oneside move]"))
            {
                return null;
            }
            bool melee = HasCategory(def, "[melee combat]");
            bool ranged = HasCategory(def, "[range combat]");
            bool stay = HasCategory(def, "[stay]");
            if (!stay && !melee && !ranged)
            {
                return null;
            }
            var traits = new MonsterBrainTraits(
                melee,
                ranged,
                HasCategory(def, "[close-passive]"),
                HasCategory(def, "[close-carefully]"),
                HasCategory(def, "[close-tough]"),
                HasCategory(def, "[close-manace]"),
                HasCategory(def, "[full-superarmor]"),
                def.Width.Min);
            uint state = seed;
            return new MonsterBrain(def.Sight, def.Warlike, traits, roomWidth, stay, () => ClientRandom.Next(ref state));
        }

        private static bool HasCategory(MonsterDef def, string name)
        {
            string[] names = def.CategoryNames;
            if (names == null)
            {
                return false;
            }
            foreach (string n in names)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>当前模式。</summary>
        public MonsterMoveMode Mode { get; private set; } = MonsterMoveMode.Wander;

        /// <summary>有没有目标(主角在视野里被找到过、而且还在视野里)。</summary>
        public bool HasTarget { get; private set; }

        /// <summary>处在敌对模式(近战 9 或远程 10):客户端进入时出招概率乘 1.3、好战度加成 50,离开时还原。</summary>
        public bool IsHostile => Mode == MonsterMoveMode.HostileMelee || Mode == MonsterMoveMode.HostileRanged;

        /// <summary>正盯着目标的模式(迟疑、追击、敌对、绕背、靠近):站着时面朝主角。</summary>
        public bool IsEngaged
        {
            get
            {
                switch (Mode)
                {
                    case MonsterMoveMode.Hesitant:
                    case MonsterMoveMode.Chase:
                    case MonsterMoveMode.HostileMelee:
                    case MonsterMoveMode.HostileRanged:
                    case MonsterMoveMode.Flank:
                    case MonsterMoveMode.Approach:
                        return HasTarget;
                    default:
                        return false;
                }
            }
        }

        /// <summary>靠近模式里,强悍或凶猛类别的怪物带霸体(档位 1;客户端进入时打开、离开时关闭,已经是 [full-superarmor] 的不再另开)。</summary>
        public bool ApproachSuperArmor => Mode == MonsterMoveMode.Approach && (_traits.Tough || _traits.Manace) && !_traits.FullSuperArmor;

        /// <summary>横向方向(客户端编码:0 左、1 右、4 不动)。</summary>
        public int DirX { get; private set; }

        /// <summary>纵向方向(2 上、3 下、4 不动)。</summary>
        public int DirY { get; private set; }

        /// <summary>此刻在不在走(任一方向不是"不动")。</summary>
        public bool Moving => DirX != MonsterMoveRules.None || DirY != MonsterMoveRules.None;

        /// <summary>横向的符号:左 −1、右 +1、不动 0。</summary>
        public int StepX => DirX == MonsterMoveRules.Left ? -1 : (DirX == MonsterMoveRules.Right ? 1 : 0);

        /// <summary>纵向的符号:上 −1(y 变小)、下 +1、不动 0。</summary>
        public int StepY => DirY == MonsterMoveRules.Up ? -1 : (DirY == MonsterMoveRules.Down ? 1 : 0);

        /// <summary>下一次选方向的时刻(怪物自己的时钟,毫秒)。</summary>
        public long NextTickMs => _nextTickMs;

        public bool IsStationary => _stationary;

        /// <summary>当前视野(靠近模式里是 [sight] 的 1.5 倍)。</summary>
        public int Sight => _sight;

        /// <summary>靠近模式、绕背冲刺的终点横坐标(其余模式没有意义)。</summary>
        public int DestinationX => _destX;

        /// <summary>靠近模式的终点纵坐标。</summary>
        public int DestinationY => _destY;

        /// <summary>进入当前模式的时刻(怪物自己的时钟,毫秒)。</summary>
        public long ModeStartMs => _modeStartMs;

        /// <summary>
        /// 推进到 <paramref name="nowMs"/>(怪物自己的时钟):维护目标、必要时切模式、到点选方向。只在怪物处在站着或走路的状态时调用
        /// (客户端的模式更新与方向选择对受击、倒地、出招中的怪物不做,等它们回到站着再继续——时钟照走,所以超时照算)。
        /// 位置传世界坐标(内部取整,客户端的位置是整数)。<paramref name="heroPresent"/> 为假(主角不在或已倒下)时没有目标。
        /// </summary>
        public void Update(long nowMs, bool heroPresent, double selfX, double selfY, double heroX, double heroY)
        {
            if (_stationary)
            {
                return;
            }
            int sx = (int)Math.Floor(selfX), sy = (int)Math.Floor(selfY);
            int hx = (int)Math.Floor(heroX), hy = (int)Math.Floor(heroY);
            _clockMs = nowMs;
            _positionsKnown = heroPresent;
            _selfX = sx;
            _selfY = sy;
            _heroX = hx;
            _heroY = hy;

            MaintainTarget(nowMs, heroPresent, sx, sy, hx, hy);

            if (_engagePending)
            {
                if (!HasTarget)
                {
                    _engagePending = false;                 // 目标丢了,回包里的目标不再有效
                }
                else if (nowMs >= _engageAtMs)
                {
                    _engagePending = false;
                    Engage(nowMs, sx, sy, hx, hy);
                }
            }
            if (nowMs - _lastModeUpdateMs > MonsterMoveRules.ModeUpdateIntervalMs)
            {
                ModeUpdate(nowMs, sx, sy, hx, hy);
                _lastModeUpdateMs = nowMs;
            }
            if (nowMs >= _nextTickMs)
            {
                Tick(nowMs, sx, sy, hx, hy);
            }
        }

        /// <summary>
        /// 挨了打(0x0089c7b0):绕背冲刺里转追击;攻击者离自己不近(距离不小于 <see cref="MonsterMoveRules.FarHitDistance"/>)
        /// 而且还没在靠近中,就进靠近模式朝攻击者走。攻击者的位置取最近一次 <see cref="Update"/> 给的主角位置。
        /// 客户端有个"受击后立刻找目标"的开关([targeting on damage]),全库没有一个怪物写它,所以这里不做:没有目标的怪物仍按一秒的间隔找。
        /// </summary>
        public void NotifyDamaged()
        {
            if (_stationary)
            {
                return;
            }
            if (Mode == MonsterMoveMode.Flank)
            {
                ChangeMode(MonsterMoveMode.Chase, _clockMs, _selfX, _selfY, _heroX, _heroY);
            }
            TryApproach();
        }

        /// <summary>这只怪物打中了主角(0x00892880):绕背冲刺或靠近途中转追击。</summary>
        public void NotifyHitHero()
        {
            if (_stationary)
            {
                return;
            }
            if (Mode == MonsterMoveMode.Flank || Mode == MonsterMoveMode.Approach)
            {
                ChangeMode(MonsterMoveMode.Chase, _clockMs, _selfX, _selfY, _heroX, _heroY);
            }
        }

        /// <summary>
        /// 走路的动作里位置一秒没变(0x0088a4c0 的卡住检测):转反向模式(21),掉头走 <see cref="ReverseMs"/> 毫秒,然后转追击。
        /// 调用方负责计时与"在走路的动作里"的判断。
        /// </summary>
        public void NotifyStuck()
        {
            if (_stationary)
            {
                return;
            }
            ChangeMode(MonsterMoveMode.Reverse, _clockMs, _selfX, _selfY, _heroX, _heroY);
        }

        /// <summary>
        /// 目标维护(0x008822b0):有目标时目标失效就重新找(找不到就清掉),没有目标时到了间隔再找。
        /// 每次找都会"设置目标"(哪怕设成没有),并记下时刻,下一次找至少隔 <see cref="MonsterMoveRules.FindIntervalMs"/>。
        /// 新找到的目标要等回包(<see cref="NoticeDelayMs"/>)才进入敌对。
        /// </summary>
        private void MaintainTarget(long nowMs, bool heroPresent, int sx, int sy, int hx, int hy)
        {
            bool valid = heroPresent && MonsterMoveRules.IsValidTarget(_sight, sx, sy, hx, hy);
            if (HasTarget)
            {
                if (valid)
                {
                    return;
                }
                HasTarget = false;
                _lastSetMs = nowMs;
                return;
            }
            if (nowMs < _lastSetMs + MonsterMoveRules.FindIntervalMs)
            {
                return;
            }
            _lastSetMs = nowMs;
            if (valid)
            {
                HasTarget = true;
                _engagePending = true;
                _engageAtMs = nowMs + NoticeDelayMs;
            }
        }

        /// <summary>回包处理(0x0088b270):先试绕背,不绕就按类别进敌对——有 [melee combat] 进 9,否则有 [range combat] 进 10,都没有就不动。</summary>
        private void Engage(long nowMs, int sx, int sy, int hx, int hy)
        {
            if (TryFlank(nowMs, sx, sy, hx, hy))
            {
                return;
            }
            if (_traits.Melee)
            {
                ChangeMode(MonsterMoveMode.HostileMelee, nowMs, sx, sy, hx, hy);
            }
            else if (_traits.Ranged)
            {
                ChangeMode(MonsterMoveMode.HostileRanged, nowMs, sx, sy, hx, hy);
            }
        }

        /// <summary>尝试绕背(0x0088b410):掷骰一半的几率、有目标、没有 [range combat],就把终点定在目标另一侧视野一半的地方、进冲刺模式。</summary>
        private bool TryFlank(long nowMs, int sx, int sy, int hx, int hy)
        {
            int roll = _random();
            if (roll % 100 < MonsterMoveRules.FlankPercent && HasTarget && !_traits.Ranged)
            {
                _destX = MonsterMoveRules.FlankDestinationX(sx, hx, _sight);
                ChangeMode(MonsterMoveMode.Flank, nowMs, sx, sy, hx, hy);
                return true;
            }
            return false;
        }

        /// <summary>被远处打中后的靠近(0x0089dd20):按类别算终点,进靠近模式。</summary>
        private void TryApproach()
        {
            if (!_positionsKnown || Mode == MonsterMoveMode.Approach)
            {
                return;
            }
            if (MonsterMoveRules.Distance(_selfX, _selfY, _heroX, _heroY) < MonsterMoveRules.FarHitDistance)
            {
                return;
            }
            MonsterMoveRules.ApproachStyle style;
            if (_traits.Passive || _traits.Carefully)
            {
                style = MonsterMoveRules.ApproachStyle.Careful;
            }
            else if (_traits.Tough)
            {
                style = MonsterMoveRules.ApproachStyle.Tough;
            }
            else if (_traits.Manace)
            {
                style = MonsterMoveRules.ApproachStyle.Manace;
            }
            else
            {
                style = MonsterMoveRules.ApproachStyle.Plain;
            }
            int sideX = 0, sideY = 0, span = 0;
            if (style == MonsterMoveRules.ApproachStyle.Careful)
            {
                sideX = _random();
                sideY = _random();
                span = _random();
            }
            MonsterMoveRules.ApproachDestination(style, _selfX, _heroX, _heroY, sideX, sideY, span, out int destX, out int destY);
            _destX = Math.Max(0, Math.Min(_roomWidth, destX));
            _destY = destY;
            ChangeMode(MonsterMoveMode.Approach, _clockMs, _selfX, _selfY, _heroX, _heroY);
        }

        /// <summary>
        /// 模式更新(0x0088d270,每 333 毫秒一次):各模式的超时与转换。
        /// 迟疑:没目标转站桩;追击:没目标回游荡;敌对:满 10001 毫秒回游荡;绕背:没目标回游荡、冲过终点转敌对;
        /// 靠近:没目标满 5 秒回游荡,有目标时近战怪物把终点跟到目标当前位置、远程怪物转迟疑;反向:满 501 毫秒转追击。
        /// </summary>
        private void ModeUpdate(long nowMs, int sx, int sy, int hx, int hy)
        {
            switch (Mode)
            {
                case MonsterMoveMode.Hesitant:
                    if (!HasTarget)
                    {
                        ChangeMode(MonsterMoveMode.Idle, nowMs, sx, sy, hx, hy);
                    }
                    break;
                case MonsterMoveMode.Chase:
                    if (!HasTarget)
                    {
                        ChangeMode(MonsterMoveMode.Wander, nowMs, sx, sy, hx, hy);
                    }
                    break;
                case MonsterMoveMode.HostileMelee:
                case MonsterMoveMode.HostileRanged:
                    if (nowMs - _modeStartMs >= MonsterMoveRules.HostileTimeoutMs)
                    {
                        ChangeMode(MonsterMoveMode.Wander, nowMs, sx, sy, hx, hy);
                    }
                    break;
                case MonsterMoveMode.Flank:
                    if (!HasTarget)
                    {
                        ChangeMode(MonsterMoveMode.Wander, nowMs, sx, sy, hx, hy);
                    }
                    else if (MonsterMoveRules.FlankArrived(DirX, sx, _destX))
                    {
                        if (_traits.Melee)
                        {
                            ChangeMode(MonsterMoveMode.HostileMelee, nowMs, sx, sy, hx, hy);
                        }
                        else if (_traits.Ranged)
                        {
                            ChangeMode(MonsterMoveMode.HostileRanged, nowMs, sx, sy, hx, hy);
                        }
                    }
                    break;
                case MonsterMoveMode.Approach:
                    if (!HasTarget)
                    {
                        if (nowMs - _modeStartMs > MonsterMoveRules.ApproachNoTargetTimeoutMs)
                        {
                            ChangeMode(MonsterMoveMode.Wander, nowMs, sx, sy, hx, hy);
                        }
                    }
                    else if (MonsterMoveRules.IsValidTarget(_sight, sx, sy, hx, hy))
                    {
                        if (_traits.Melee)
                        {
                            _destX = hx;
                            _destY = hy;
                        }
                        else if (_traits.Ranged)
                        {
                            ChangeMode(MonsterMoveMode.Hesitant, nowMs, sx, sy, hx, hy);
                        }
                    }
                    break;
                case MonsterMoveMode.Reverse:
                    if (nowMs - _modeStartMs >= ReverseMs)
                    {
                        ChangeMode(MonsterMoveMode.Chase, nowMs, sx, sy, hx, hy);
                    }
                    break;
            }
        }

        /// <summary>
        /// 换模式(0x00880910 的完整流程):离开旧模式、进入新模式,然后马上按新模式选一次方向,不等节拍。
        /// 在方向选择函数里换模式用 <see cref="EnterMode"/>(选完方向后外层继续,见 <see cref="Tick"/>)。
        /// </summary>
        private void ChangeMode(MonsterMoveMode mode, long nowMs, int sx, int sy, int hx, int hy)
        {
            EnterMode(mode, nowMs);
            Tick(nowMs, sx, sy, hx, hy);
        }

        /// <summary>离开旧模式、进入新模式(0x00894fb0、0x0088a6e0):记下进入时刻;靠近模式把视野放大 1.5 倍、离开时按 1 / 1.5 取整还原。</summary>
        private void EnterMode(MonsterMoveMode mode, long nowMs)
        {
            if (Mode == MonsterMoveMode.Approach && mode != MonsterMoveMode.Approach)
            {
                _sight = (int)((float)_sight * (1.0f / MonsterMoveRules.ApproachSightFactor));
            }
            if (mode == MonsterMoveMode.Approach && Mode != MonsterMoveMode.Approach)
            {
                _sight = (int)((float)_sight * MonsterMoveRules.ApproachSightFactor);
            }
            Mode = mode;
            _modeStartMs = nowMs;
        }

        /// <summary>
        /// 移动节拍(0x00889da0):按模式选方向、排下一次的时刻、再按停下概率取消一部分方向。
        /// 方向选择里换了模式时(目标没了回游荡、靠近到位转敌对),外层照旧用选出来的方向(那时多半是"不动")与"新模式"的节拍表和停下表往下走。
        /// </summary>
        private void Tick(long nowMs, int sx, int sy, int hx, int hy)
        {
            int dirX = MonsterMoveRules.None, dirY = MonsterMoveRules.None;
            SelectDirections(nowMs, sx, sy, hx, hy, ref dirX, ref dirY);
            MonsterTempo tempo = MonsterMoveRules.TempoOf(Mode);
            bool idle = dirX == MonsterMoveRules.None && dirY == MonsterMoveRules.None;
            _nextTickMs = nowMs + tempo.Interval(idle, _random());
            if (_random() % 100 < MonsterMoveRules.StopPercentX(Mode))
            {
                dirX = MonsterMoveRules.None;
            }
            if (_random() % 100 < MonsterMoveRules.StopPercentY(Mode))
            {
                dirY = MonsterMoveRules.None;
            }
            DirX = dirX;
            DirY = dirY;
        }

        /// <summary>各模式的方向选择(0x00891650 的 switch)。</summary>
        private void SelectDirections(long nowMs, int sx, int sy, int hx, int hy, ref int dirX, ref int dirY)
        {
            switch (Mode)
            {
                case MonsterMoveMode.Wander:
                    ChooseWander(sx, out dirX, out dirY);
                    break;
                case MonsterMoveMode.Hesitant:
                    // 先看防撞边(在边上就朝里走),不在边上才有 30% 的几率朝目标走
                    if (!MonsterMoveRules.NearEdge(sx, _roomWidth, out dirX))
                    {
                        if (MonsterMoveRules.RollMove(MonsterMoveRules.HesitantMovePercent, _random()))
                        {
                            Approach(sx, sy, hx, hy, ref dirX, ref dirY);
                        }
                    }
                    break;
                case MonsterMoveMode.Chase:
                    // 80% 的几率朝目标走(同样先看防撞边),其余站着
                    if (MonsterMoveRules.RollMove(MonsterMoveRules.ChaseMovePercent, _random()))
                    {
                        if (!MonsterMoveRules.NearEdge(sx, _roomWidth, out dirX))
                        {
                            Approach(sx, sy, hx, hy, ref dirX, ref dirY);
                        }
                    }
                    break;
                case MonsterMoveMode.HostileMelee:
                    if (!HasTarget)
                    {
                        EnterMode(MonsterMoveMode.Wander, nowMs);   // 方向选择里"没有目标就回游荡":这一拍站着,间隔按游荡的站着算
                    }
                    else
                    {
                        Approach(sx, sy, hx, hy, ref dirX, ref dirY);
                    }
                    break;
                case MonsterMoveMode.HostileRanged:
                    if (!HasTarget)
                    {
                        EnterMode(MonsterMoveMode.Wander, nowMs);
                    }
                    else
                    {
                        dirX = MonsterMoveRules.KeepDistanceX(_sight, sx, hx);
                        dirY = MonsterMoveRules.KeepDistanceY(sy, hy);
                    }
                    break;
                case MonsterMoveMode.Flank:
                    if (!HasTarget)
                    {
                        EnterMode(MonsterMoveMode.Wander, nowMs);
                    }
                    else
                    {
                        dirX = MonsterMoveRules.FlankDirectionX(sx, hx);
                        dirY = MonsterMoveRules.None;
                    }
                    break;
                case MonsterMoveMode.Approach:
                    SelectApproach(nowMs, sx, sy, hx, hy, ref dirX, ref dirY);
                    break;
                case MonsterMoveMode.Reverse:
                    dirX = MonsterMoveRules.Reverse(DirX);
                    dirY = MonsterMoveRules.Reverse(DirY);
                    break;
                default:
                    break;                                          // 站桩:不走
            }
        }

        /// <summary>
        /// 靠近模式的方向选择(0x00891650 模式 12):朝终点走,离终点横向与纵向都不到 宽度/2 + 10 就到了;
        /// 没到但已经离目标不到 max(70, 视野 × 0.1) 也算到了;到了之后被动与谨慎类别回游荡,其余再试一次绕背,不绕就按类别进敌对。
        /// </summary>
        private void SelectApproach(long nowMs, int sx, int sy, int hx, int hy, ref int dirX, ref int dirY)
        {
            int margin = _traits.BodyWidth / 2 + MonsterMoveRules.ArriveMarginExtra;
            bool arrived = MonsterMoveRules.ApproachStep(sx, sy, _destX, _destY, margin, out dirX, out dirY);
            if (!arrived)
            {
                if (!HasTarget)
                {
                    return;
                }
                int near = Math.Max(MonsterMoveRules.NearTargetMinimum, (int)((float)_sight * MonsterMoveRules.NearTargetSightFactor));
                if (near <= MonsterMoveRules.Distance(sx, sy, hx, hy))
                {
                    return;
                }
            }
            if (_traits.Passive || _traits.Carefully)
            {
                EnterMode(MonsterMoveMode.Wander, nowMs);
                return;
            }
            if (!TryFlank(nowMs, sx, sy, hx, hy))
            {
                if (_traits.Melee)
                {
                    EnterMode(MonsterMoveMode.HostileMelee, nowMs);
                }
                else if (_traits.Ranged)
                {
                    EnterMode(MonsterMoveMode.HostileRanged, nowMs);
                }
            }
        }

        /// <summary>
        /// 朝目标所在的格子走(0x0088ab90):格子比较给出方向;敌对加成加 [warlike] 不超过 随机数 % 100 就取反两个方向
        /// (加成只有敌对模式 9、10 才有,追击、迟疑里是 0)。
        /// </summary>
        private void Approach(int sx, int sy, int hx, int hy, ref int dirX, ref int dirY)
        {
            MonsterMoveRules.ApproachDirections(sx, sy, hx, hy, out int ax, out int ay);
            if (MonsterMoveRules.ShouldReverse(_warlike, MonsterMoveRules.WarlikeBonusOf(Mode), _random()))
            {
                ax = MonsterMoveRules.Reverse(ax);
                ay = MonsterMoveRules.Reverse(ay);
            }
            dirX = ax;
            dirY = ay;
        }

        /// <summary>
        /// 游荡的方向选择(0x00891650 模式 13):<see cref="MonsterMoveRules.WanderStartPercent"/>% 的几率去走——先看防撞边
        /// (在边上就朝里走),否则横向随机左右;纵向随机上下;其余情况站着。
        /// </summary>
        private void ChooseWander(int sx, out int dirX, out int dirY)
        {
            dirX = MonsterMoveRules.None;
            dirY = MonsterMoveRules.None;
            if (_random() % 100 < MonsterMoveRules.WanderStartPercent)
            {
                if (!MonsterMoveRules.NearEdge(sx, _roomWidth, out dirX))
                {
                    dirX = _random() & 1;
                }
                dirY = (_random() & 1) + MonsterMoveRules.Up;
            }
        }
    }
}
