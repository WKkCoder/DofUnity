using System;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物默认 AI 的"走位"部分(证据 E-114):游荡、发现目标、走向目标。规则与常数在 <see cref="MonsterMoveRules"/>,这里是每只怪物一个的状态。
    /// <para>
    /// 流程(取自客户端旧式模式机 0x008822b0 的更新与 0x00889da0 的移动节拍):
    /// ① 出生在游荡模式;没有目标时每隔 1 秒找一次(主角在 [sight] × 1.4 之内就算有效目标,距离里纵向差按深度缩放折算),有了目标又丢出视野时立刻放弃;
    /// ② 找到目标后进入敌对模式(近战朝主角所在的格子走,远程与主角保持视野的一半到九成);
    /// ③ 每个模式有自己的节拍:到点才重新选方向,选定的方向一直走到下一次选择,走着与站着的间隔不同;游荡时 40% 的几率随机选一个斜向走、纵向再有一半被取消。
    /// 怪物不是每一帧都朝主角校正,而是每隔约一秒校正一次,所以会冲过头、再折回来。
    /// </para>
    /// <para>
    /// 推断与经验值(都单独标出):<b>发现之后过 <see cref="NoticeDelayMs"/> 毫秒才进入敌对</b>是基线 S09b 里量到的迟滞(一个样本;客户端是把"目标改变"发给服务器、
    /// 再从服务器的回包里进入敌对模式,时延的来源没读到);客户端的敌对模式 10 秒后回到游荡、之后怎样再接战没读通,这里不回游荡;
    /// 绕背(模式 11)与受击后追击(模式 4)没有接,受击只做"立刻找目标、跳过迟滞"。
    /// </para>
    /// </summary>
    public sealed class MonsterBrain
    {
        /// <summary>发现目标到进入敌对模式的迟滞(毫秒)。经验值(非 REA):基线 S09b 里一只怪物出生后约 0.7 秒才掉头朝主角走,只有这一个样本。</summary>
        public const int NoticeDelayMs = 700;

        private readonly Func<int> _random;
        private readonly int _sight;
        private readonly int _warlike;
        private readonly int _roomWidth;
        private readonly bool _ranged;
        private readonly bool _stationary;
        private long _nextTickMs;
        private long _lastSetMs = -MonsterMoveRules.FindIntervalMs;
        private long _noticeAtMs;
        private bool _skipNotice;

        /// <param name="sight">[sight](没写取 <see cref="MonsterMoveRules.DefaultSight"/>);0 表示无限远。</param>
        /// <param name="warlike">[warlike](没写取 50)。</param>
        /// <param name="ranged">远程怪物(有 [range combat] 而没有 [melee combat]):敌对时与主角保持距离而不是贴上去。</param>
        /// <param name="roomWidth">房间的像素宽度:游荡时离两边不到 <see cref="MonsterMoveRules.EdgeMargin"/> 就掉头。</param>
        /// <param name="stationary">原地不动的怪物([fixture]、[stay]):不游荡、不找目标、不走。</param>
        /// <param name="random">客户端 rand() 的替身:返回非负整数(见 <see cref="Dnf.Render.ClientRandom"/>)。</param>
        public MonsterBrain(int sight, int warlike, bool ranged, int roomWidth, bool stationary, Func<int> random)
        {
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _sight = sight < 0 ? 0 : sight;
            _warlike = warlike;
            _ranged = ranged;
            _roomWidth = roomWidth;
            _stationary = stationary;
            DirX = MonsterMoveRules.None;
            DirY = MonsterMoveRules.None;
        }

        /// <summary>
        /// 按 .mob 的类别决定这只怪物用不用走位大脑,用的话造一个(随机数从 <paramref name="seed"/> 起,每只怪物各一个状态):
        /// 有 [melee combat] 的怪物走近战大脑;[stay] 的怪物是原地不动的大脑(客户端的找目标函数对它直接返回)。
        /// 其余返回 null,继续走占位:[oneside move](只走一个方向)、没有这几个类别的怪物(行为由 .ai 脚本指挥,我们还没有那套运行时),
        /// 以及只有 [range combat] 的远程怪物——模式 10 的取距规则已经写成了纯函数(<see cref="MonsterMoveRules.KeepDistanceX"/>),
        /// 但静态读码里它的纵向永远在走、而出招决策又只在站着时做,两条放在一起远程怪物永远不出招,说明读漏了让它停下的机制,读通之前不接(E-114)。
        /// </summary>
        public static MonsterBrain For(MonsterDef def, int roomWidth, uint seed)
        {
            if (def == null || HasCategory(def, "[oneside move]"))
            {
                return null;
            }
            bool melee = HasCategory(def, "[melee combat]");
            bool stay = HasCategory(def, "[stay]");
            if (!stay && !melee)
            {
                return null;
            }
            uint state = seed;
            return new MonsterBrain(def.Sight, def.Warlike, false, roomWidth, stay, () => ClientRandom.Next(ref state));
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

        /// <summary>处在敌对模式(近战或远程)。</summary>
        public bool IsHostile => Mode != MonsterMoveMode.Wander;

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

        public int Sight => _sight;

        /// <summary>
        /// 推进到 <paramref name="nowMs"/>(怪物自己的时钟):维护目标、必要时切模式、到点选方向。
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

            MaintainTarget(nowMs, heroPresent, sx, sy, hx, hy);

            if (HasTarget && Mode == MonsterMoveMode.Wander && nowMs >= _noticeAtMs + NoticeDelayMs)
            {
                ChangeMode(_ranged ? MonsterMoveMode.HostileRanged : MonsterMoveMode.HostileMelee, nowMs, sx, sy, hx, hy);
            }
            if (nowMs >= _nextTickMs)
            {
                Tick(nowMs, sx, sy, hx, hy);
            }
        }

        /// <summary>
        /// 挨了打:马上重新找目标(跳过一秒的间隔),找到了就不再等发现迟滞。客户端受击后会立刻找目标、并把被动模式转成追击(模式 4,没有接);
        /// 主角在视野之外挨的打(远程技能)不会让怪物有目标。
        /// </summary>
        public void NotifyDamaged()
        {
            if (_stationary)
            {
                return;
            }
            _lastSetMs = long.MinValue / 2;
            _skipNotice = true;
            if (HasTarget)
            {
                _noticeAtMs = long.MinValue / 2;
            }
        }

        /// <summary>
        /// 目标维护(0x008822b0):有目标时目标失效就重新找(找不到就清掉),没有目标时到了间隔再找。
        /// 每次找都会"设置目标"(哪怕设成没有),并记下时刻,下一次找至少隔 <see cref="MonsterMoveRules.FindIntervalMs"/>。
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
                // 受击后立刻找到的目标不等迟滞;正常发现从此刻起算
                _noticeAtMs = _skipNotice ? long.MinValue / 2 : nowMs;
            }
            _skipNotice = false;
        }

        /// <summary>换模式(0x00880910):换完立刻按新模式选一次方向,不等节拍。</summary>
        private void ChangeMode(MonsterMoveMode mode, long nowMs, int sx, int sy, int hx, int hy)
        {
            Mode = mode;
            Tick(nowMs, sx, sy, hx, hy);
        }

        /// <summary>移动节拍(0x00889da0):按模式选方向、排下一次的时刻、再按停下概率取消一部分方向。</summary>
        private void Tick(long nowMs, int sx, int sy, int hx, int hy)
        {
            int dirX = MonsterMoveRules.None, dirY = MonsterMoveRules.None;
            switch (Mode)
            {
                case MonsterMoveMode.Wander:
                    ChooseWander(sx, out dirX, out dirY);
                    break;
                case MonsterMoveMode.HostileMelee:
                    if (!HasTarget)
                    {
                        // 模式的方向选择里"没有目标就回游荡"(0x00891650);回去立刻再选一次
                        Mode = MonsterMoveMode.Wander;
                        ChooseWander(sx, out dirX, out dirY);
                    }
                    else
                    {
                        MonsterMoveRules.ApproachDirections(sx, sy, hx, hy, out dirX, out dirY);
                        if (MonsterMoveRules.ShouldReverse(_warlike, _random()))
                        {
                            dirX = MonsterMoveRules.Reverse(dirX);
                            dirY = MonsterMoveRules.Reverse(dirY);
                        }
                    }
                    break;
                case MonsterMoveMode.HostileRanged:
                    if (!HasTarget)
                    {
                        Mode = MonsterMoveMode.Wander;
                        ChooseWander(sx, out dirX, out dirY);
                    }
                    else
                    {
                        dirX = MonsterMoveRules.KeepDistanceX(_sight, sx, hx);
                        dirY = MonsterMoveRules.KeepDistanceY(sy, hy);
                    }
                    break;
            }
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
