using System;
using Dnf.Render;

// 宠物在城镇里的位置跟随(距离停靠、追赶、过远传送)。
// 【状态层里没有位置跟随的规则】:Game/Pets 与 PetSession 只管成长、饱食、装备格,NOTES 明确"不做跟随表现";
// 任务单说的"状态层的跟随状态机"在代码里只有宠物自身的状态(PetState:未装备 / 消耗中 / 回补)与饿死判断,城镇实体的"在不在场"就取自它们(见 TownPetField)。
// 位置怎么跟没有任何原版证据(没有读到客户端里对应的代码),所以这一份规则是本项目自己的设计,数值都是占位的:
//   · 停靠点 = 主角位置沿朝向偏移 span(负数在背后;span 取自 .cre 的 [character span],没写用缺省);
//   · 离停靠点超过停靠半径就追:每个轴上的速度 = 剩余距离 × 追赶率,封顶在宠物自己的速度(取自 .cre 的 [move speed]);这样主角连续走动时宠物也连续滑行、
//     停下来时平滑收到停靠点,而不是"落后一截再冲一下"的走走停停;
//   · 离停靠点超过传送距离(任一轴)就直接落到停靠点(主角瞬移、被击飞、过图之后宠物追不上的情形)。
// 这里只是位置上的"追与不追",不重新推导"宠物出没出战、饿没饿死":那些由状态层说了算。
namespace Dnf.Play
{
    /// <summary>跟随的两个状态:停在停靠点上、正在追。</summary>
    public enum PetFollowPhase
    {
        /// <summary>在停靠点上(含刚出现、刚传送过去)。</summary>
        Idle = 0,

        /// <summary>离停靠点还有距离,正在追。</summary>
        Chasing = 1,
    }

    /// <summary>被跟随的主角:世界坐标与朝向。</summary>
    public readonly struct PetMaster
    {
        public PetMaster(double x, double y, bool facingLeft)
        {
            X = x;
            Y = y;
            FacingLeft = facingLeft;
        }

        public double X { get; }

        public double Y { get; }

        public bool FacingLeft { get; }

        internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    }

    /// <summary>跟随规则的数值(全是本项目自己的占位设计,见文件头)。</summary>
    public sealed class PetFollowRules
    {
        /// <summary>缺省规则。</summary>
        public static readonly PetFollowRules Default = new PetFollowRules();

        /// <param name="chaseRate">追赶率(每秒):速度 = 剩余距离 × 它。</param>
        /// <param name="arriveRadius">停靠半径(像素):两个轴上离停靠点都不超过它就算到了,直接落在停靠点上。</param>
        /// <param name="teleportDistanceX">横向离停靠点超过它(严格大于)就传送。</param>
        /// <param name="teleportDistanceY">纵向离停靠点超过它(严格大于)就传送。</param>
        /// <param name="runDistance">追的时候横向离停靠点超过它(严格大于)用奔跑动作,否则行走。</param>
        /// <param name="homeDepth">停靠点在主角世界 y 之外再偏的像素(正数在主角前面,画家算法里压在主角上)。</param>
        /// <param name="defaultSpan">没写 [character span] 的宠物用的停靠距离(负数在背后)。</param>
        /// <param name="defaultSpeedX">没写 [move speed] 的宠物的横向最大速度(像素每秒);缺省取主角奔跑的横向速度。</param>
        /// <param name="defaultSpeedY">没写 [move speed] 的宠物的纵向最大速度(像素每秒);缺省取主角奔跑的纵向速度。</param>
        public PetFollowRules(double chaseRate = 5.0, double arriveRadius = 3.0, double teleportDistanceX = 400.0, double teleportDistanceY = 200.0,
            double runDistance = 120.0, double homeDepth = 0.0, double defaultSpan = -64.0,
            double defaultSpeedX = TownHero.DefaultDashSpeedX, double defaultSpeedY = TownHero.DefaultDashSpeedY)
        {
            ChaseRate = chaseRate;
            ArriveRadius = arriveRadius;
            TeleportDistanceX = teleportDistanceX;
            TeleportDistanceY = teleportDistanceY;
            RunDistance = runDistance;
            HomeDepth = homeDepth;
            DefaultSpan = defaultSpan;
            DefaultSpeedX = defaultSpeedX;
            DefaultSpeedY = defaultSpeedY;
        }

        public double ChaseRate { get; }

        public double ArriveRadius { get; }

        public double TeleportDistanceX { get; }

        public double TeleportDistanceY { get; }

        public double RunDistance { get; }

        public double HomeDepth { get; }

        public double DefaultSpan { get; }

        public double DefaultSpeedX { get; }

        public double DefaultSpeedY { get; }

        /// <summary>这只宠物的停靠距离:写了 [character span] 用它,没写用缺省。</summary>
        public double SpanOf(PetLook look)
        {
            return look != null && look.CharacterSpan.HasValue ? look.CharacterSpan.Value : DefaultSpan;
        }

        /// <summary>
        /// 这只宠物的横向最大速度(像素每秒):[move speed] 的第 0 个数(推断为横向直走)乘主角的横向换算系数
        /// (<see cref="TownHero.DefaultSpeedFactorX"/>,引擎速度到画面像素);没写、写的不是正数用缺省。
        /// </summary>
        public double SpeedXOf(PetLook look)
        {
            return SpeedOf(look, 0, TownHero.DefaultSpeedFactorX, DefaultSpeedX);
        }

        /// <summary>这只宠物的纵向最大速度:[move speed] 的第 2 个数(推断为纵向直走)乘主角的纵向换算系数;没写、写的不是正数用缺省。</summary>
        public double SpeedYOf(PetLook look)
        {
            return SpeedOf(look, 2, TownHero.DefaultSpeedFactorY, DefaultSpeedY);
        }

        private static double SpeedOf(PetLook look, int index, double factor, double fallback)
        {
            if (look == null || look.MoveSpeed.Count != PetLook.MoveSpeedCount || look.MoveSpeed[index] <= 0)
            {
                return fallback;
            }
            return look.MoveSpeed[index] * factor;
        }
    }

    /// <summary>跟随状态机一步的输出(测试与宿主直接读它,不必从画面反推)。</summary>
    public readonly struct PetFollowStep
    {
        public PetFollowStep(PetFollowPhase phase, double x, double y, double targetX, double targetY, bool facingLeft, bool teleported, PetMotion motion)
        {
            Phase = phase;
            X = x;
            Y = y;
            TargetX = targetX;
            TargetY = targetY;
            FacingLeft = facingLeft;
            Teleported = teleported;
            Motion = motion;
        }

        /// <summary>这一步结束时的状态。</summary>
        public PetFollowPhase Phase { get; }

        /// <summary>这一步结束时宠物的位置。</summary>
        public double X { get; }

        public double Y { get; }

        /// <summary>这一步用的停靠点(目标)。</summary>
        public double TargetX { get; }

        public double TargetY { get; }

        public bool FacingLeft { get; }

        /// <summary>这一步是传送过去的。</summary>
        public bool Teleported { get; }

        /// <summary>该放的动作:停着是站立,在追是行走,追得远是奔跑。</summary>
        public PetMotion Motion { get; }
    }

    /// <summary>
    /// 一只宠物的位置跟随(纯逻辑:不读时钟、不碰场景、不碰状态层)。构造时就落在主角身边的停靠点上;之后每帧给主角的位置与过了多少毫秒,
    /// <see cref="Step"/> 返回这一步的输出。
    /// </summary>
    public sealed class PetFollower
    {
        private readonly PetFollowRules _rules;
        private readonly double _span;
        private readonly double _speedX;
        private readonly double _speedY;

        public PetFollower(PetFollowRules rules, double span, double speedX, double speedY, PetMaster master)
        {
            _rules = rules ?? PetFollowRules.Default;
            _span = span;
            _speedX = speedX;
            _speedY = speedY;
            Place(master);
        }

        /// <summary>按宠物的外观(停靠距离、速度)造一个跟随器,落在主角身边。</summary>
        public static PetFollower For(PetFollowRules rules, PetLook look, PetMaster master)
        {
            PetFollowRules r = rules ?? PetFollowRules.Default;
            return new PetFollower(r, r.SpanOf(look), r.SpeedXOf(look), r.SpeedYOf(look), master);
        }

        public double X { get; private set; }

        public double Y { get; private set; }

        public bool FacingLeft { get; private set; }

        public PetFollowPhase Phase { get; private set; }

        /// <summary>传送过几次(出现与 <see cref="Place"/> 不算)。</summary>
        public int Teleports { get; private set; }

        /// <summary>主角在 <paramref name="master"/> 的位置与朝向时的停靠点。</summary>
        public void HomeOf(PetMaster master, out double x, out double y)
        {
            x = master.X + _span * (master.FacingLeft ? -1.0 : 1.0);
            y = master.Y + _rules.HomeDepth;
        }

        /// <summary>不经过追赶,直接落在停靠点上,朝向与主角一致(出现、换场景时用)。主角坐标不是有限数时什么也不做。</summary>
        public void Place(PetMaster master)
        {
            if (!master.IsFinite)
            {
                return;
            }
            HomeOf(master, out double hx, out double hy);
            X = hx;
            Y = hy;
            FacingLeft = master.FacingLeft;
            Phase = PetFollowPhase.Idle;
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒(负数与 0 一样)。顺序:先看离停靠点多远——任一轴超过传送距离就传送;两个轴都在停靠半径之内就落在停靠点上(站立,朝向同主角);
        /// 否则在追:每个轴上速度 = 剩余距离 × 追赶率、封顶在宠物自己的速度,这一步走的距离不超过剩余距离;追的时候朝向取横向运动方向。
        /// 主角坐标不是有限数时这一步什么也不动。
        /// </summary>
        public PetFollowStep Step(double deltaMs, PetMaster master)
        {
            if (!master.IsFinite)
            {
                return new PetFollowStep(Phase, X, Y, X, Y, FacingLeft, false, Phase == PetFollowPhase.Idle ? PetMotion.Stand : PetMotion.Walk);
            }
            HomeOf(master, out double hx, out double hy);
            double dx = hx - X;
            double dy = hy - Y;
            bool teleported = false;
            PetMotion motion;
            if (Math.Abs(dx) > _rules.TeleportDistanceX || Math.Abs(dy) > _rules.TeleportDistanceY)
            {
                X = hx;
                Y = hy;
                FacingLeft = master.FacingLeft;
                Phase = PetFollowPhase.Idle;
                Teleports++;
                teleported = true;
                motion = PetMotion.Stand;
            }
            else if (Math.Abs(dx) <= _rules.ArriveRadius && Math.Abs(dy) <= _rules.ArriveRadius)
            {
                X = hx;
                Y = hy;
                FacingLeft = master.FacingLeft;
                Phase = PetFollowPhase.Idle;
                motion = PetMotion.Stand;
            }
            else
            {
                double seconds = deltaMs > 0.0 ? deltaMs / 1000.0 : 0.0;
                double stepX = Reach(dx, _speedX, seconds);
                double stepY = Reach(dy, _speedY, seconds);
                motion = Math.Abs(dx) > _rules.RunDistance ? PetMotion.Run : PetMotion.Walk;
                X += stepX;
                Y += stepY;
                if (stepX != 0.0)
                {
                    FacingLeft = stepX < 0.0;
                }
                Phase = PetFollowPhase.Chasing;
            }
            return new PetFollowStep(Phase, X, Y, hx, hy, FacingLeft, teleported, motion);
        }

        /// <summary>这个轴上这一步走多远:速度 = 剩余距离 × 追赶率,封顶在 maxSpeed;不超过剩余距离。</summary>
        private double Reach(double distance, double maxSpeed, double seconds)
        {
            double speed = distance * _rules.ChaseRate;
            if (speed > maxSpeed)
            {
                speed = maxSpeed;
            }
            else if (speed < -maxSpeed)
            {
                speed = -maxSpeed;
            }
            double step = speed * seconds;
            return Math.Abs(step) > Math.Abs(distance) ? distance : step;
        }
    }
}
