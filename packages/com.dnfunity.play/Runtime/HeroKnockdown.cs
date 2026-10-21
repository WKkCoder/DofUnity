using System;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 主角的倒地与起身(证据 E-079、E-082、E-083、E-085、E-105):被倒地类的攻击打中、或在空中、倒地中被打时,主角像怪物一样飞起来、落地颠一下、躺着、起身。
    /// 运动与动画时钟用与怪物共用的 <see cref="KnockdownFlight"/>(原版是同一个基类状态函数),每一步把结果摆给主角(<see cref="TownHero.SetDownPose"/>);
    /// 体重取角色脚本的 [weight]。与怪物不同的是 E-085 的"提前起身":躺地(阶段 2)装填之后,累计受到的伤害超过最大血量的
    /// <see cref="EarlyEndPercent"/>% 就立刻起身(落地时跳过颠一下与躺地,躺着时下一步起身),防止躺地后被连续打死;已死的主角不起身,躺在地上不动。
    /// </summary>
    public sealed class HeroKnockdown
    {
        /// <summary>
        /// 提前起身的伤害阈值(最大血量的百分数):<c>累计伤害 × 100 ÷ 最大血量 &gt; 15</c>(两个 <c>.rdata</c> 里的浮点常数,证据 E-085)。
        /// 与基线是否一致没有验证。
        /// </summary>
        public const double EarlyEndPercent = 15.0;

        /// <summary>已死而没有收到任何倒地请求(决策函数在"不产生反应"时返回、每帧函数补发的 DOWN)用的击退与抬升:引擎代码里的常数 400、400(证据 E-079 第 4 点)。</summary>
        public const int DeadDefaultPush = 400;
        public const int DeadDefaultLift = 400;

        private readonly TownHero _hero;
        private readonly KnockdownFlight _flight;
        private bool _armed;
        private double _accumulated;

        public HeroKnockdown(TownHero hero)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _flight = new KnockdownFlight(hero.WeightStat, new DownAnimation(hero.DownAni));
        }

        /// <summary>主角有倒地动作([down motion]);没有时被打中只走普通受击。</summary>
        public bool Available => _hero.CanBeKnockedDown;

        /// <summary>正在倒地的运动里(腾空、颠一下、躺着);已死的主角躺平之后不算。</summary>
        public bool Active => _flight.Active;

        /// <summary>倒地阶段:0 首次腾空、1 落地后颠一下、2 躺着;不在倒地里是 -1。</summary>
        public int Stage => _flight.Stage;

        /// <summary>提前起身已装填(躺地阶段进入之后、起身之前;E-085)。</summary>
        public bool Armed => _armed;

        /// <summary>装填之后累计受到的伤害(E-085)。</summary>
        public double Accumulated => _accumulated;

        /// <summary>提前起身已经触发(下一次落地或躺着的这一步就起身)。</summary>
        public bool EarlyEndPending => _flight.EarlyEnd;

        /// <summary>从开始玩起进入倒地(含倒地中被再次击飞)的次数。</summary>
        public int Begun { get; private set; }

        /// <summary>从开始玩起因为"累计伤害超过阈值"而提前起身的次数。</summary>
        public int EarlyEnds { get; private set; }

        /// <summary>从开始玩起自然起身(躺完)的次数,不含提前起身。</summary>
        public int GetUps { get; private set; }

        public double VerticalSpeed => _flight.VerticalSpeed;
        public double HorizontalSpeed => _flight.HorizontalSpeed;

        /// <summary>
        /// 请求倒地(决策函数的倒地分支,E-079):打断主角正在做的事,按这一击的 [push aside]、[lift up]、[bounce]、[ignore weight] 开始飞;
        /// 已经在倒地里再被打时,已经落过地(阶段 ≥ 1)而且这一击不弹起,请求的阶段是 1(颠一下),否则是 0。
        /// <paramref name="dead"/> 为真:这一击把主角打死——倒地照常走,躺下的那一刻转入死亡(躺着不再起身,E-082、E-084);
        /// 请求的阶段固定是 0(E-079 第 3 步)。
        /// 没有倒地动作时什么也不做,返回 false。
        /// </summary>
        /// <param name="down">这一击的倒地输入(抬升、弹起、不受体重、抖动用的两个随机数)。</param>
        /// <param name="pushAside">[push aside]:水平初速的来源。</param>
        /// <param name="pushToLeft">攻击方把主角往左推(主角转向攻击方,速度背离攻击方)。</param>
        public bool Begin(KnockdownInput down, int pushAside, bool pushToLeft, bool dead)
        {
            if (!_hero.CanBeKnockedDown)
            {
                return false;
            }
            int stage = 0;
            if (!dead && _flight.Active && _flight.Stage >= 1 && !down.Bounce)
            {
                stage = 1;
            }
            var request = new KnockdownRequest(stage, Knockdown.TypeOf(down.Bounce, down.IgnoreWeight, stage), pushAside, down.LiftUp);
            _flight.SeedRolls(unchecked(down.RollHorizontal * 1000 + down.RollHeight + 7));
            if (dead)
            {
                _flight.DieOnLying = true;
            }
            _hero.BeginDown();
            _flight.Place(_hero.X, _hero.Y, _hero.Z, _hero.FacingLeft);
            _flight.Begin(request, down.RollHorizontal, down.RollHeight, pushToLeft);
            Begun++;
            Settle();
            return true;
        }

        /// <summary>
        /// 记下主角这一次挨的伤害(E-085):提前起身装填之后才累计,累计 × 100 ÷ 最大血量超过 <see cref="EarlyEndPercent"/> 就触发。
        /// 每次被打中都调(不论这一击怎样处理反应),在请求倒地之前或之后调结果相同。
        /// </summary>
        public void NoteDamage(int damage, int maxHp)
        {
            if (!_armed || damage <= 0 || maxHp <= 0)
            {
                return;
            }
            _accumulated += damage;
            if (_accumulated * 100.0 / maxHp > EarlyEndPercent)
            {
                _flight.EarlyEnd = true;
            }
        }

        /// <summary>推进倒地 <paramref name="deltaMs"/> 毫秒(命中停顿里不推进):运动与动画时钟,落地、躺下、起身;结果摆给主角。</summary>
        public void Step(double deltaMs)
        {
            if (!_flight.Active)
            {
                return;
            }
            _flight.Place(_hero.X, _hero.Y, _hero.Z, _hero.FacingLeft);
            _flight.Update(deltaMs, _hero.Walk);
            Settle();
        }

        /// <summary>把飞行状态机算出的结果摆给主角,处理起身与死亡通知,更新提前起身的装填状态。</summary>
        private void Settle()
        {
            _hero.SetDownPose(_flight.X, _flight.Z, _flight.FacingLeft, _flight.AniMs);
            if (_flight.Active && _flight.Stage >= 2 && !_armed)
            {
                // 进入阶段 2(躺地)而且还没装填:装填,累计量清零(E-085 第 3 点)
                _armed = true;
                _accumulated = 0;
                _flight.EarlyEnd = false;
            }
            if (_flight.TakeEnded())
            {
                if (_flight.EarlyEnd)
                {
                    EarlyEnds++;
                }
                else
                {
                    GetUps++;
                }
                _hero.EndDown();
                Disarm();
            }
            else if (_flight.TakeDied())
            {
                Disarm();           // 死亡不再是倒地状态:装填解除(E-085 第 3 点)
            }
        }

        private void Disarm()
        {
            _armed = false;
            _accumulated = 0;
            _flight.EarlyEnd = false;
        }
    }
}
