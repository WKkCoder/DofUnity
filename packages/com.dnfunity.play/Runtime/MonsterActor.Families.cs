using System;
using Dnf.Game.Act;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物的非出招动作族 .act 宿主(M53,证据 E-146)。
    /// 客户端的怪物对象为每一族动作各存一个 .act 动作对象(站立、行走、坐下、受击、倒地、翻倒、最后一幕、过程),各自带触发器列表;
    /// [ON DAMAGE] 与 [ON ATTACKSUCCESS] 的运行器只分发给"当前动作"与"过程动作"两个对象(高,静态读码)。
    /// 这些动作对象的每帧更新在受保护区里读不到,下面的运行模型是推断:一族只在它是当前动作时跑,过程动作从出生跑到死亡,
    /// 最后一幕从血量归零起跑到动画播完(身体 <see cref="MonsterTuning.DeathMs"/> 之后消失不打断它,脚本放出来的对象不跟着尸体走)。
    /// 每族一份触发器开关(跨动作保留,[TRIGGER] 的族写法改的就是它们);运行器每次进入这一族重新建(次数与单次位清零,变量不留,和出招一样)。
    /// </summary>
    public sealed partial class MonsterActor
    {
        /// <summary>没有动画的族(过程、没有 BASE ANI 的最后一幕)跑多久:最后一幕给 1 秒收尾。</summary>
        private const double LastFallbackMs = 1000.0;

        /// <summary>最后一幕在动画播完之后多留一会儿,让停在末帧的触发器与延迟行为也有机会跑完。</summary>
        private const double LastTailMs = 200.0;

        private sealed class FamilyHost
        {
            public MonsterActFamily Family;
            public PassiveAction Action;
            public bool[] Enabled;
            public ActRunner Runner;
            public double ClockMs;
            public bool SetPending;
        }

        private FamilyHost[] _fam;
        private IActHost _famHost;
        private FamilyHost _famActive;
        private bool _lastStarted;

        /// <summary>有至少一族装了 .act 触发器程序。</summary>
        public bool HasFamilyActs => _fam != null;

        /// <summary>各族运行器启动过的次数(进入一族、过程出生、最后一幕开始都算;测试与审计用)。</summary>
        public int FamilyRunsStarted { get; private set; }

        /// <summary>非出招动作族里的脚本 [SET ACTION] 落实了(切招、回待机、开始行走)的次数。</summary>
        public int FamilySetActionApplied { get; private set; }

        /// <summary>非出招动作族里的脚本 [SET ACTION] 没有落实的次数:指向的动作怪物没有、或是还没接的动作族(受击、倒地、自定义等),或怪物此刻不在待机与行走。</summary>
        public int FamilySetActionUnhosted { get; private set; }

        /// <summary>最后一幕此刻在跑(血量归零之后,脚本可以在身体消失之后继续放东西)。</summary>
        public bool LastActRunning => _fam != null && _fam[(int)MonsterActFamily.Last]?.Runner != null;

        /// <summary>
        /// 装上各非出招动作族的 .act 程序(只给有触发器的):之后待机、行走、坐下、受击、倒地的每一步让这一族的触发器跑一遍,
        /// 过程动作从现在起一直跑;<paramref name="host"/> 提供随机数与生成对象。传 null(或没有一族带触发器)撤掉全部。
        /// </summary>
        public void SetFamilyActs(MonsterFamilyActs acts, IActHost host)
        {
            _fam = null;
            _famHost = null;
            _famActive = null;
            _lastStarted = false;
            if (acts == null || host == null || !acts.Any)
            {
                return;
            }
            _famHost = host;
            _fam = new FamilyHost[MonsterFamilyActs.FamilyCount];
            for (int i = 0; i < _fam.Length; i++)
            {
                PassiveAction action = acts.Get((MonsterActFamily)i);
                if (action != null)
                {
                    _fam[i] = new FamilyHost
                    {
                        Family = (MonsterActFamily)i,
                        Action = action,
                        Enabled = ActRunner.NewEnabledState(action.Program),
                    };
                }
            }
            if (Hp > 0 && Phase != MonsterPhase.Gone)
            {
                StartFamily(_fam[(int)MonsterActFamily.Proc]);
            }
        }

        /// <summary><paramref name="family"/> 的运行器;这一族没有程序或此刻没在跑为 null。测试与审计用。</summary>
        public ActRunner FamilyRunner(MonsterActFamily family)
        {
            return _fam?[(int)family]?.Runner;
        }

        /// <summary>这一族第 <paramref name="trigger"/> 个触发器现在开着吗;这一族没有装程序(或序号越界)返回 null。</summary>
        public bool? FamilyTriggerState(MonsterActFamily family, int trigger)
        {
            bool[] state = _fam?[(int)family]?.Enabled;
            if (state == null || (uint)trigger >= (uint)state.Length)
            {
                return null;
            }
            return state[trigger];
        }

        /// <summary>现在是当前动作的那一族(待机、行走、受击、倒地对应的族);出招、没有对应族或没装程序为 null。测试用。</summary>
        public MonsterActFamily? ActiveFamily => _famActive?.Family;

        private FamilyHost Host(MonsterActFamily family)
        {
            return _fam[(int)family];
        }

        /// <summary>
        /// 此刻的当前动作对应哪一族(没装程序的族算没有):待机 → 站立,行走 → 行走,受击 → 按放的是哪个动画取受击 1、2、倒地、翻倒;
        /// 出招(有自己的运行器)、死亡与消失没有。
        /// </summary>
        private FamilyHost WantedFamily()
        {
            switch (Phase)
            {
                case MonsterPhase.Wait:
                    return Host(MonsterActFamily.Stay);
                case MonsterPhase.Walk:
                    return Host(MonsterActFamily.Move);
                case MonsterPhase.Hurt:
                    if (_hurtAni == null)
                    {
                        return null;
                    }
                    if (ReferenceEquals(_hurtAni, _model.Down))
                    {
                        return Host(MonsterActFamily.Down);
                    }
                    if (ReferenceEquals(_hurtAni, _model.Overturn))
                    {
                        return Host(MonsterActFamily.Overturn);
                    }
                    if (ReferenceEquals(_hurtAni, _model.Damage1))
                    {
                        return Host(MonsterActFamily.Damage1);
                    }
                    return ReferenceEquals(_hurtAni, _model.Damage2) ? Host(MonsterActFamily.Damage2) : null;
                default:
                    return null;
            }
        }

        private void StartFamily(FamilyHost f)
        {
            if (f == null)
            {
                return;
            }
            f.Runner = new ActRunner(f.Action.Program, ActSelf, _famHost, f.Enabled);
            f.ClockMs = 0;
            f.SetPending = true;
            FamilyRunsStarted++;
        }

        private static void StopFamily(FamilyHost f)
        {
            if (f != null)
            {
                f.Runner = null;
            }
        }

        /// <summary>
        /// 每一步(在出招、受击、思考之后):血量归零就让当前动作与过程动作退场、最后一幕开始;活着时跟着当前动作换族,
        /// 当前族与过程动作各跑一步,再按脚本给的速度移动;死了只跑最后一幕。
        /// </summary>
        private void TickFamilies(double deltaMs, IWalkArea walk)
        {
            if (_fam == null)
            {
                return;
            }
            if (Hp <= 0)
            {
                if (!_lastStarted)
                {
                    _lastStarted = true;
                    StopFamily(_famActive);
                    _famActive = null;
                    StopFamily(Host(MonsterActFamily.Proc));
                    StartFamily(Host(MonsterActFamily.Last));
                }
                TickFamily(Host(MonsterActFamily.Last), deltaMs);
                return;
            }
            FamilyHost want = WantedFamily();
            if (!ReferenceEquals(want, _famActive))
            {
                StopFamily(_famActive);
                _famActive = want;
                StartFamily(want);
            }
            if (_famActive != null)
            {
                TickFamily(_famActive, deltaMs);
            }
            TickFamily(Host(MonsterActFamily.Proc), deltaMs);
            // 出招有自己的运行器时速度由 TickActs 推进;倒地飞行与死亡自己管位置
            bool attackHasRunner = Phase == MonsterPhase.Attack && _actRunner != null;
            if (!attackHasRunner && Phase != MonsterPhase.Dying && Phase != MonsterPhase.Gone && !_flight.Active)
            {
                ApplyActMotion(deltaMs, walk);
            }
        }

        /// <summary>消失之后只剩最后一幕还在跑(脚本放出来的对象由会话接管,不跟着尸体走)。</summary>
        private void TickGhostFamilies(double deltaMs)
        {
            if (_fam == null)
            {
                return;
            }
            if (!_lastStarted)
            {
                // 没经过血量归零就消失(整房清场之类的直接收掉):最后一幕不放
                _lastStarted = true;
                return;
            }
            TickFamily(Host(MonsterActFamily.Last), deltaMs < 0 ? 0 : deltaMs);
        }

        private void TickFamily(FamilyHost f, double deltaMs)
        {
            ActRunner runner = f?.Runner;
            if (runner == null)
            {
                return;
            }
            f.ClockMs += deltaMs;
            if (f.SetPending)
            {
                f.SetPending = false;
                runner.OnSetAction();
                if (f.Runner != runner)
                {
                    return;
                }
            }
            runner.Update(FamilyFrame(f), (float)(deltaMs / 1000.0));
            if (f.Family == MonsterActFamily.Last && f.Runner == runner && f.ClockMs >= LastSpan(f))
            {
                f.Runner = null;
            }
        }

        /// <summary>
        /// 这一族动作此刻播到第几帧:它是当前动作、而且放的就是它自己的动画时跟着画面走(倒地时动画有自己的时钟,与停顿帧同步,E-082),
        /// 否则(过程、最后一幕、没放它自己的动画)用自己的时钟;没有动画的族固定第 0 帧。
        /// </summary>
        private int FamilyFrame(FamilyHost f)
        {
            AniTimeline timeline = f.Action.Timeline;
            if (timeline == null)
            {
                return 0;
            }
            if (ReferenceEquals(f, _famActive) && ReferenceEquals(CurrentAni, f.Action.Ani))
            {
                return Math.Max(0, CurrentFrame);
            }
            return Math.Max(0, timeline.FrameAt((long)f.ClockMs));
        }

        private static double LastSpan(FamilyHost f)
        {
            AniTimeline timeline = f.Action.Timeline;
            return timeline == null || timeline.TotalMilliseconds <= 0 ? LastFallbackMs : timeline.TotalMilliseconds + LastTailMs;
        }

        /// <summary>按脚本给的速度移动(像素/秒);Z 方向的速度受重力,落到地面就停(推断,E-130)。出招由 <see cref="TickActs"/> 调,其余阶段由 <see cref="TickFamilies"/> 调。</summary>
        private void ApplyActMotion(double deltaMs, IWalkArea walk)
        {
            double seconds = deltaMs / 1000.0;
            if (_actVx != 0 || _actVy != 0)
            {
                TryMove(_actVx * seconds, _actVy * seconds, walk);
            }
            if (_actFalling && !_hovering)
            {
                Z += _actVz * seconds;
                _actVz -= PassiveObject.Gravity * seconds;
                if (Z <= 0)
                {
                    Z = 0;
                    _actVz = 0;
                    _actFalling = false;
                }
            }
        }

        /// <summary>挨了打:唤醒当前动作与过程动作里带 [ON DAMAGE] 的触发器(出招的运行器由调用方处理;客户端分发给这两个对象,E-146)。</summary>
        private void FamilyOnDamage()
        {
            if (_fam == null)
            {
                return;
            }
            _famActive?.Runner?.OnDamage(ActTarget);
            Host(MonsterActFamily.Proc)?.Runner?.OnDamage(ActTarget);
        }

        /// <summary>打中了主角:唤醒当前动作与过程动作里带 [ON ATTACKSUCCESS] 的触发器。</summary>
        private void FamilyOnAttackSuccess(IActEntity victim)
        {
            if (_fam == null)
            {
                return;
            }
            _famActive?.Runner?.OnAttackSuccess(victim);
            Host(MonsterActFamily.Proc)?.Runner?.OnAttackSuccess(victim);
        }

        /// <summary>
        /// 脚本的 [SET ACTION] 出自非出招的动作族(站立、行走、过程……):怪物在待机或行走时,切招([ATTACK] n)、回待机([STAND]、[DEFAULT])、开始行走([MOVE]);
        /// 其余动作与别的阶段怪物这一版不接,记数。
        /// </summary>
        private void ActSetActionOutsideAttack(ActActionKind kind, int index)
        {
            if (Phase != MonsterPhase.Wait && Phase != MonsterPhase.Walk)
            {
                FamilySetActionUnhosted++;
                return;
            }
            switch (kind)
            {
                case ActActionKind.Attack:
                    if ((uint)index >= (uint)_model.Attacks.Count || _model.Attacks[index] == null)
                    {
                        FamilySetActionUnhosted++;
                        return;
                    }
                    if (_cooldownMs > 0)
                    {
                        // 上一招出完的间隔([attack delay])没过:不接(推断,E-146)。原版脚本直接切招有没有这道间隔没读到;不加的话行走里
                        // "离目标近就出招"的脚本会在出完招的第 7 毫秒又出一招,而出招动画多半没有受击框——主角永远打不到它,房间清不掉
                        FamilySetActionUnhosted++;
                        return;
                    }
                    _script?.AttackStarted(index);
                    _attackIndex = index;
                    _hasDestination = false;        // 与思考出招一致:出招取消手头的目的地,出完招要等下一次选目的地才再走(推断)
                    EnterPhase(MonsterPhase.Attack);
                    FamilySetActionApplied++;
                    break;
                case ActActionKind.Stand:
                case ActActionKind.Default:
                    if (Phase == MonsterPhase.Walk)
                    {
                        EnterPhase(MonsterPhase.Wait);
                    }
                    FamilySetActionApplied++;
                    break;
                case ActActionKind.Move:
                    if (Phase == MonsterPhase.Wait && _model.Move != null)
                    {
                        EnterPhase(MonsterPhase.Walk);
                    }
                    FamilySetActionApplied++;
                    break;
                default:
                    FamilySetActionUnhosted++;
                    break;
            }
        }

        /// <summary>
        /// 脚本的 [TRIGGER] 族写法指向站立、行走、坐下、倒地或过程这几族(E-145 的池号 → E-146 的宿主):写这一族的开关数组,
        /// 正在跑的运行器经 <see cref="ActRunner.ApplyTrigger"/>(复位清它的次数、单次位与计时),没在跑就直接写开关;
        /// 这一族没装程序或序号越界算落空(客户端:池为空或个数不大于序号就跳过)。[DEFAULT]、[CUSTOM] 还没有宿主,由调用方记 Unhosted。
        /// </summary>
        private void ActSwitchFamilyTrigger(MonsterActFamily family, int trigger, ActTriggerCommand command)
        {
            FamilyHost host = _fam?[(int)family];
            bool[] state = host?.Enabled;
            if (state == null || (uint)trigger >= (uint)state.Length)
            {
                ActSwitchMissed++;
                return;
            }
            ActSwitchApplied++;
            if (host.Runner != null)
            {
                host.Runner.ApplyTrigger(trigger, command);
                return;
            }
            switch (command)
            {
                case ActTriggerCommand.On:
                    state[trigger] = true;
                    break;
                case ActTriggerCommand.Off:
                    state[trigger] = false;
                    break;
            }
        }
    }
}
