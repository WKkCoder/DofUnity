using System;
using Dnf.Data;
using Dnf.Game.Act;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物的 [CHANGE ACTION FILE](M57,证据 E-151):脚本在运行中把怪物某一族动作(待机、行走、受击、倒地……)或某一招的 .act 换成另一个文件,
    /// 同时换掉这一族的动画(新 .act 的第一个 [BASE ANI])与触发器程序(开关回到新脚本里 [ENABLE] 的初值)。
    /// 客户端的做法(静态读码,高):执行块按路径新建一个动作对象换进怪物的动作表,旧的稍后销毁;被换的动作如果此刻正是怪物正在跑的那一个,
    /// 它继续跑,换了的新动作要等到下一次进入这一族(或脚本再次 [SET ACTION] 到它)才轮到——旧动作被标上"离开后销毁"并挂着替换指针,
    /// 控制器下一次设置当前动作时按替换指针取新的。所以这里:没在跑的族与招马上换;正在跑的先记成待换,在下一次进入这一族的时刻换上。
    /// 表里只有待机、行走、坐下、两个受击、倒地、翻倒、各招有宿主;后退、冲刺、冲刺攻击、跳跃、跳击、自定义列表这一版怪物没有对应的动作,只记数。
    /// 数据一律从用户自备的 PVF 里读,本类不含原版内容。
    /// </summary>
    public sealed partial class MonsterActor
    {
        private PassiveAction[] _pendingFamily;
        private PassiveAction[] _pendingAttack;
        private IActHost _pendingHost;

        /// <summary>已经换上的次数(含先记成待换、后来进入这一族时换上的)。</summary>
        public int ActionFilesApplied { get; private set; }

        /// <summary>换的时候这一族(这一招)正在跑、记成待换的次数。</summary>
        public int ActionFilesDeferred { get; private set; }

        /// <summary>写到这一版怪物没有对应动作的族(后退、冲刺、冲刺攻击、跳跃、跳击、自定义)的次数:只记数。</summary>
        public int ActionFilesUnhosted { get; private set; }

        /// <summary>被拒绝的次数:序号越界(客户端同样跳过)或新文件读不出来。</summary>
        public int ActionFilesRejected { get; private set; }

        /// <summary>新文件没有主动画、这一族(这一招)沿用原来的动画的次数。</summary>
        public int ActionFilesWithoutAnimation { get; private set; }

        /// <summary>这一族此刻的动画(换过的是换进来的那个);没有动画的族是 null。测试与审计用。</summary>
        public AniFile FamilyAni(MonsterActFamily family)
        {
            return _anis.Get(family);
        }

        /// <summary>第 <paramref name="index"/> 招此刻的动画;越界或这一招没有动画是 null。</summary>
        public AniFile AttackAni(int index)
        {
            return (uint)index < (uint)_anis.Attacks.Length ? _anis.Attacks[index] : null;
        }

        /// <summary>怪物有几招(与 <see cref="MonsterModel.Attacks"/> 同个数;换文件不改个数)。</summary>
        public int AttackCount => _anis.Attacks.Length;

        /// <summary>动画的 PVF 路径(模型里读出来的或换进来的);不认识的是 null。</summary>
        public string AnimationPathOf(AniFile ani)
        {
            return _anis.PathOf(ani);
        }

        /// <summary>有记成待换、还没换上的文件(测试用)。</summary>
        public bool HasPendingActionFile => _pendingFamily != null || _pendingAttack != null;

        /// <summary>
        /// 脚本的 [CHANGE ACTION FILE]:把 <paramref name="slot"/>(受击、出招两族再按 <paramref name="index"/> 选)换成 <paramref name="action"/>(新 .act 的程序与主动画)。
        /// <paramref name="host"/> 是给触发器运行器用的宿主(这只怪物还没有宿主时用它)。返回有没有换上(或记成待换);序号越界、新文件为空、
        /// 这一版没有对应动作的族返回 false 并记数。
        /// </summary>
        public bool ApplyActionFile(ActFileSlot slot, int index, PassiveAction action, IActHost host)
        {
            if (action == null)
            {
                ActionFilesRejected++;
                return false;
            }
            switch (slot)
            {
                case ActFileSlot.Stay:
                    return SwapFamily(MonsterActFamily.Stay, action, host);
                case ActFileSlot.Move:
                    return SwapFamily(MonsterActFamily.Move, action, host);
                case ActFileSlot.Sit:
                    return SwapFamily(MonsterActFamily.Sit, action, host);
                case ActFileSlot.Down:
                    return SwapFamily(MonsterActFamily.Down, action, host);
                case ActFileSlot.Overturn:
                    return SwapFamily(MonsterActFamily.Overturn, action, host);
                case ActFileSlot.Damage:
                    if (index != 0 && index != 1)
                    {
                        ActionFilesRejected++;       // 客户端:受击族的序号必须小于 2
                        return false;
                    }
                    return SwapFamily(index == 0 ? MonsterActFamily.Damage1 : MonsterActFamily.Damage2, action, host);
                case ActFileSlot.Attack:
                    if ((uint)index >= (uint)_anis.Attacks.Length)
                    {
                        ActionFilesRejected++;       // 客户端:出招列表的序号必须小于列表个数
                        return false;
                    }
                    return SwapAttack(index, action, host);
                default:
                    ActionFilesUnhosted++;
                    return false;
            }
        }

        private bool SwapFamily(MonsterActFamily family, PassiveAction action, IActHost host)
        {
            if (FamilyIsRunning(family))
            {
                if (_pendingFamily == null)
                {
                    _pendingFamily = new PassiveAction[MonsterFamilyActs.FamilyCount];
                }
                _pendingFamily[(int)family] = action;
                _pendingHost = host;
                ActionFilesDeferred++;
                return true;
            }
            InstallFamily(family, action, host);
            ActionFilesApplied++;
            return true;
        }

        private bool SwapAttack(int index, PassiveAction action, IActHost host)
        {
            if (Phase == MonsterPhase.Attack && _attackIndex == index)
            {
                if (_pendingAttack == null)
                {
                    _pendingAttack = new PassiveAction[_anis.Attacks.Length];
                }
                _pendingAttack[index] = action;
                _pendingHost = host;
                ActionFilesDeferred++;
                return true;
            }
            InstallAttack(index, action, host);
            ActionFilesApplied++;
            return true;
        }

        /// <summary>这一族此刻是怪物正在跑的那一个动作(换它要等下一次进入)。受击与倒地的几族按"现在放的就是它的动画"算。</summary>
        private bool FamilyIsRunning(MonsterActFamily family)
        {
            switch (family)
            {
                case MonsterActFamily.Stay:
                    return Phase == MonsterPhase.Wait;
                case MonsterActFamily.Move:
                    return Phase == MonsterPhase.Walk;
                case MonsterActFamily.Damage1:
                case MonsterActFamily.Damage2:
                case MonsterActFamily.Down:
                case MonsterActFamily.Overturn:
                    return Phase == MonsterPhase.Hurt && _hurtAni != null && ReferenceEquals(_hurtAni, _anis.Get(family));
                default:
                    return false;
            }
        }

        private void InstallFamily(MonsterActFamily family, PassiveAction action, IActHost host)
        {
            if (action.Ani != null)
            {
                _anis.Remember(action.Ani, action.AniPath);
                _anis.Set(family, action.Ani);
            }
            else
            {
                ActionFilesWithoutAnimation++;
            }
            IActHost runnerHost = _famHost ?? host;
            if (action.HasTriggers && runnerHost != null)
            {
                if (_fam == null)
                {
                    _fam = new FamilyHost[MonsterFamilyActs.FamilyCount];
                }
                _famHost = runnerHost;
                _fam[(int)family] = new FamilyHost
                {
                    Family = family,
                    Action = action,
                    Enabled = ActRunner.NewEnabledState(action.Program),
                };
            }
            else if (_fam != null)
            {
                _fam[(int)family] = null;
            }
        }

        private void InstallAttack(int index, PassiveAction action, IActHost host)
        {
            if (action.Ani != null)
            {
                _anis.Remember(action.Ani, action.AniPath);
                _anis.Attacks[index] = action.Ani;
                _strikeBoxes.Remove(index);          // 攻击框来自这一招的动画,缓存作废
            }
            else
            {
                ActionFilesWithoutAnimation++;
            }
            IActHost runnerHost = _actHost ?? host;
            if (_actPrograms == null)
            {
                if (!action.HasTriggers || runnerHost == null)
                {
                    return;
                }
                _actPrograms = new ActProgram[_anis.Attacks.Length];
                _actEnabled = new bool[_actPrograms.Length][];
                _actHost = runnerHost;
            }
            else if (index >= _actPrograms.Length)
            {
                Array.Resize(ref _actPrograms, index + 1);
                Array.Resize(ref _actEnabled, index + 1);
            }
            bool triggers = action.HasTriggers && runnerHost != null;
            _actPrograms[index] = triggers ? action.Program : null;
            _actEnabled[index] = triggers ? ActRunner.NewEnabledState(action.Program) : null;
            if (_actHost == null)
            {
                _actHost = runnerHost;
            }
            BindScriptTriggers();
        }

        /// <summary>
        /// 倒地动画换过了:倒地状态机里的"形状"(长停留帧、躺着的那一帧、各帧起点)是按动画推出来的,所以开始新的一次倒地之前、状态机不在倒地中时按新动画重建。
        /// 正在倒地的这一次照旧放完(换的是下一次进入倒地时用的文件)。
        /// </summary>
        private void SyncFlightAnimation()
        {
            if (_flight.Active || ReferenceEquals(_flightAni, _anis.Down))
            {
                return;
            }
            _flightAni = _anis.Down;
            _flight = new KnockdownFlight(_weight, new DownAnimation(_flightAni));
        }

        /// <summary>进入 <paramref name="next"/> 阶段的那一刻:把记成待换的文件换上(受击的几族在每次挨打、挑动画之前换,见 <see cref="ApplyPendingHurt"/>)。</summary>
        private void ApplyPendingFor(MonsterPhase next)
        {
            switch (next)
            {
                case MonsterPhase.Wait:
                    ApplyPendingFamily(MonsterActFamily.Stay);
                    break;
                case MonsterPhase.Walk:
                    ApplyPendingFamily(MonsterActFamily.Move);
                    break;
                case MonsterPhase.Attack:
                    if (_pendingAttack != null && (uint)_attackIndex < (uint)_pendingAttack.Length && _pendingAttack[_attackIndex] != null)
                    {
                        PassiveAction action = _pendingAttack[_attackIndex];
                        _pendingAttack[_attackIndex] = null;
                        InstallAttack(_attackIndex, action, _pendingHost);
                        ActionFilesApplied++;
                        ClearPendingIfDone();
                    }
                    break;
            }
        }

        /// <summary>挨打、挑受击动画之前:受击、倒地、翻倒几族里记成待换的换上(每一次挨打都是重新进入这一族)。</summary>
        private void ApplyPendingHurt()
        {
            if (_pendingFamily == null)
            {
                return;
            }
            ApplyPendingFamily(MonsterActFamily.Damage1);
            ApplyPendingFamily(MonsterActFamily.Damage2);
            ApplyPendingFamily(MonsterActFamily.Down);
            ApplyPendingFamily(MonsterActFamily.Overturn);
        }

        private void ApplyPendingFamily(MonsterActFamily family)
        {
            PassiveAction action = _pendingFamily?[(int)family];
            if (action == null)
            {
                return;
            }
            _pendingFamily[(int)family] = null;
            InstallFamily(family, action, _pendingHost);
            ActionFilesApplied++;
            ClearPendingIfDone();
        }

        private bool HasPendingFamily(MonsterActFamily family)
        {
            return _pendingFamily != null && _pendingFamily[(int)family] != null;
        }

        private void ClearPendingIfDone()
        {
            if (_pendingFamily != null && Array.TrueForAll(_pendingFamily, a => a == null))
            {
                _pendingFamily = null;
            }
            if (_pendingAttack != null && Array.TrueForAll(_pendingAttack, a => a == null))
            {
                _pendingAttack = null;
            }
        }
    }
}
