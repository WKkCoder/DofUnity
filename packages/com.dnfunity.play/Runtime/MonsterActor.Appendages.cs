using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Game.Act;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物身上的属性改变附加物(.act 的 [APPENDAGE] / [DELETE APPENDAGE];M63,证据 E-161)。
    /// <para>
    /// 观察(静态读码,非 REA):客户端的附加物把"一项属性改变"写进对象的属性记录(与服务端 <see cref="MonsterStats"/> 同一个布局),
    /// 同一项重复挂会先顶替掉旧的,删除按来源与"同一项"匹配;各项的写法见 <see cref="StatChangeEffects"/>。
    /// 推断(运行模型,没有对拍的数据):生命上限、移动速度、出招速率、防御面板与攻击力都不在原值上直接改,
    /// 而是每次附加物集合变化时从基础数值块 <see cref="SetBaseStats"/> 重新算一遍(加减可交换,到期与删除不用记"撤销");
    /// 生命上限变小时当前生命夹到新上限,变大时当前生命不动;速度与出招速率按"改变后的倍率 ÷ 基础倍率"缩放出生时的速度(缺省倍率都是 1);
    /// 攻击力按"改变后的乘积 − 基础乘积"的差加在出生时的攻击力上(客户端对怪物当攻击方用哪一对数没读到,普通对与装备对的变化都算进去)。
    /// 没有读取者的类型(暴击、状态抗性、受击硬直倍率、绝对伤害等)只计数,不起作用。
    /// </para>
    /// </summary>
    public sealed partial class MonsterActor
    {
        /// <summary>
        /// 出招速率被附加物压低时的下限:出招动画的时长是 总时长 ÷ 速率,速率压到 0 这一招就永远放不完
        /// (客户端的速度下限取自配置,没读到;取 0.1,E-161)。
        /// </summary>
        public const double MinAttackRate = 0.1;

        private readonly StatAppendageSet _statAppendages = new StatAppendageSet();
        private readonly Dictionary<int, int> _statTypeAdds = new Dictionary<int, int>();
        private MonsterStats _statBase;
        private bool _statBasisTaken;
        private int _basisHpMax;
        private double _basisSpeedX;
        private double _basisAttackRate;
        private int _basisPhysicalAttack;
        private int _basisMagicalAttack;
        private DefenderPanel _basisDefense;

        /// <summary>怪物身上现在的属性改变附加物(只读;挂、删由 .act 的行为经 <see cref="ActSelf"/> 进来)。</summary>
        public StatAppendageSet StatAppendages => _statAppendages;

        /// <summary><see cref="SetBaseStats"/> 给的基础数值块(没给、也还没挂过附加物时为 null)。</summary>
        public MonsterStats BaseStats => _statBase;

        /// <summary>挂过的属性改变附加物个数(含顶替掉旧的那一次)。</summary>
        public int StatAppendagesAdded { get; private set; }

        /// <summary>挂上时顶替掉的旧附加物个数(同一项重复挂)。</summary>
        public int StatAppendagesReplaced { get; private set; }

        /// <summary>被 [DELETE APPENDAGE] 删掉的附加物个数。</summary>
        public int StatAppendagesRemoved { get; private set; }

        /// <summary>时限到了自己消失的附加物个数。</summary>
        public int StatAppendagesExpired { get; private set; }

        /// <summary>挂过的附加物按类型号(<see cref="StatChangeTypes"/>)分的次数;汇总审计用。</summary>
        public IReadOnlyDictionary<int, int> StatAppendageTypeCounts => _statTypeAdds;

        /// <summary>
        /// 装上算出这只怪物数值的那一块(<see cref="RoomMonsterBuilder"/> 造怪时给):属性改变附加物从它重新算出生命上限、速度、出招速率、
        /// 防御面板与攻击力。没给时第一次挂附加物那一刻按怪物当前的生命上限、攻击力与防御面板临时造一块。要在挂第一个附加物之前调。
        /// </summary>
        public void SetBaseStats(MonsterStats stats)
        {
            _statBase = stats;
        }

        /// <summary>[APPENDAGE] 落到这只怪物上的一项(<paramref name="durationMs"/> 不大于 0 是没有时限)。</summary>
        public void AddStatAppendage(StatChange change, int durationMs, IActEntity source)
        {
            TakeStatBasis();
            StatAppendagesReplaced += _statAppendages.Add(source, change, durationMs);
            StatAppendagesAdded++;
            _statTypeAdds.TryGetValue(change.Type, out int n);
            _statTypeAdds[change.Type] = n + 1;
            RecomputeStats();
        }

        /// <summary>[DELETE APPENDAGE] 落到这只怪物上的一项:删掉来源是 <paramref name="source"/> 的同一项。</summary>
        public void RemoveStatAppendage(StatChange change, IActEntity source)
        {
            if (_statAppendages.Count == 0)
            {
                return;
            }
            int removed = _statAppendages.Remove(source, change);
            if (removed > 0)
            {
                StatAppendagesRemoved += removed;
                RecomputeStats();
            }
        }

        /// <summary>过了 <paramref name="deltaMs"/> 毫秒(墙钟,命中停顿里不冻结,推断):有时限的附加物倒计时,到期的去掉并重算。</summary>
        private void AdvanceStatAppendages(double deltaMs)
        {
            if (_statAppendages.Count == 0)
            {
                return;
            }
            int expired = _statAppendages.Advance(deltaMs);
            if (expired > 0)
            {
                StatAppendagesExpired += expired;
                RecomputeStats();
            }
        }

        /// <summary>记下"没有附加物时"的出生值,之后每次重算都从它出发;只记一次。</summary>
        private void TakeStatBasis()
        {
            if (_statBasisTaken)
            {
                return;
            }
            _statBasisTaken = true;
            _basisHpMax = HpMax;
            _basisSpeedX = SpeedX;
            _basisAttackRate = AttackRate;
            _basisPhysicalAttack = PhysicalAttack;
            _basisMagicalAttack = MagicalAttack;
            _basisDefense = Defense;
            if (_statBase == null)
            {
                _statBase = BasisFromCurrentValues();
            }
        }

        /// <summary>没有给基础数值块时的替身:生命上限、攻击力、防御面板照怪物现在的值放进去,别的项是缺省。</summary>
        private MonsterStats BasisFromCurrentValues()
        {
            var stats = new MonsterStats();
            stats.Raw[MonsterStats.IxHpBase] = HpMax;
            stats.Raw[MonsterStats.IxPhysicalAttack] = PhysicalAttack;
            stats.Raw[MonsterStats.IxMagicalAttack] = MagicalAttack;
            if (Defense != null)
            {
                stats.Raw[MonsterStats.IxEquipPhysicalDefense] = Defense.PhysicalDefense;
                stats.Raw[MonsterStats.IxEquipMagicalDefense] = Defense.MagicalDefense;
                for (int i = 0; i < 4; i++)
                {
                    stats.Raw[MonsterStats.IxElementResistance + i] = Defense.ElementResist(i);
                }
                stats.SetF(MonsterStats.IxPhysicalAbsoluteDefense, Defense.PhysicalPenetration);
                stats.SetF(MonsterStats.IxMagicalAbsoluteDefense, Defense.MagicalPenetration);
            }
            return stats;
        }

        /// <summary>附加物集合变了:从基础数值块重新算出各项并落到怪物身上。</summary>
        private void RecomputeStats()
        {
            if (_statAppendages.Count == 0)
            {
                // 附加物全没了:原样放回出生值(不经过浮点倍率,免得留下舍入的尾巴)
                SetHpMaxKeepingHp(_basisHpMax);
                ChangeSpeeds(_basisSpeedX, _basisAttackRate);
                PhysicalAttack = _basisPhysicalAttack;
                MagicalAttack = _basisMagicalAttack;
                Defense = _basisDefense;
                return;
            }
            MonsterStats basis = _statBase;
            MonsterStats now = _statAppendages.Apply(basis);
            SetHpMaxKeepingHp(_basisHpMax + (now.HpMax - basis.HpMax));
            double attackRate = _basisAttackRate * RateRatio(now, basis, MonsterStats.IxAttackSpeed + 1);
            double floor = Math.Min(MinAttackRate, _basisAttackRate);
            ChangeSpeeds(_basisSpeedX * RateRatio(now, basis, MonsterStats.IxMoveSpeed + 1), attackRate < floor ? floor : attackRate);
            PhysicalAttack = Math.Max(0, _basisPhysicalAttack + ProductDelta(now, basis, MonsterStats.IxPhysicalAttack) + ProductDelta(now, basis, MonsterStats.IxEquipPhysicalAttack));
            MagicalAttack = Math.Max(0, _basisMagicalAttack + ProductDelta(now, basis, MonsterStats.IxMagicalAttack) + ProductDelta(now, basis, MonsterStats.IxEquipMagicalAttack));
            if (_basisDefense != null)
            {
                Defense = DefenderPanel.FromMonster(now, Level);
            }
        }

        /// <summary>生命上限改成 <paramref name="hpMax"/>(不小于 1);当前生命超过新上限就夹下来,没超过不动,已经死了的不动。</summary>
        private void SetHpMaxKeepingHp(int hpMax)
        {
            hpMax = hpMax < 1 ? 1 : hpMax;
            if (hpMax == HpMax)
            {
                return;
            }
            HpMax = hpMax;
            if (Hp > hpMax)
            {
                Hp = hpMax;
            }
        }

        /// <summary>改走路速度与出招速率;出招进行中速率变了,把已放的时间按新速率折回去,动画不跳帧。</summary>
        private void ChangeSpeeds(double speedX, double attackRate)
        {
            SpeedX = speedX < 0.0 ? 0.0 : speedX;
            SpeedY = SpeedX * MonsterTuning.VerticalRatio;
            if (attackRate != AttackRate)
            {
                if (Phase == MonsterPhase.Attack && _customIndex < 0 && attackRate > 0.0)
                {
                    _phaseMs *= AttackRate / attackRate;
                }
                AttackRate = attackRate;
            }
        }

        private static double RateRatio(MonsterStats now, MonsterStats basis, int rateIndex)
        {
            float b = basis.F(rateIndex);
            return b > 0.0f ? (double)now.F(rateIndex) / b : 1.0;
        }

        private static int ProductDelta(MonsterStats now, MonsterStats basis, int index)
        {
            return Product(now, index) - Product(basis, index);
        }

        private static int Product(MonsterStats stats, int index)
        {
            return (int)((float)stats.Raw[index] * stats.F(index + 1));
        }
    }
}
