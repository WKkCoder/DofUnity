using System;
using System.Collections.Generic;
using Dnf.Game.Act;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 主角身上的属性改变附加物(怪物脚本的 [APPENDAGE] / [DELETE APPENDAGE] 打到主角身上;M63,证据 E-161)。
    /// <para>
    /// 推断(运行模型,没有对拍的数据):主角这一版没有"属性记录",附加物只落到两个有地方落的速度上:移动速度(类型 <see cref="StatChangeTypes.MoveSpeed"/>)
    /// 按"改变后的倍率 ÷ 之前的倍率"乘在速度系数上(<see cref="TownHero.SpeedFactorX"/>、<see cref="TownHero.SpeedFactorY"/>,与减速状态同一种相对缩放,
    /// 不覆盖别处对系数的修改),攻击速度(<see cref="StatChangeTypes.AttackSpeed"/>)同样乘在攻击动画的播放倍率上
    /// (<see cref="TownHero.AttackAnimationRate"/>),而且只在主角不在出招时换(动画进行到一半换倍率会让帧号跳变,可能漏掉或重打命中帧)。
    /// 其余类型(防御、生命上限、暴击等)主角没有可落的地方,只计数。死了就清空;房间换了主角也是新的,附加物不带过去。
    /// 没有生命条的模式(怪物不还手)不挂,与异常状态一致。
    /// </para>
    /// </summary>
    public sealed partial class RoomCombatSession
    {
        /// <summary>
        /// 速度倍率被压低时的下限:速度系数用"相对缩放",倍率压到 0 就再也除不回来;客户端的下限取自配置,没读到,取 0.05(E-161)。
        /// </summary>
        public const double HeroStatMinRate = 0.05;

        private readonly StatAppendageSet _heroStatAppendages = new StatAppendageSet();
        private readonly Dictionary<int, int> _heroStatTypeAdds = new Dictionary<int, int>();

        /// <summary>移动速度附加物已经乘在主角速度系数上的倍率(1 = 没有)。</summary>
        private double _heroStatMoveRate = 1.0;

        /// <summary>攻击速度附加物已经乘在主角攻击动画播放倍率上的倍率(1 = 没有)。</summary>
        private double _heroStatAttackRate = 1.0;

        /// <summary>主角身上现在的属性改变附加物(只读)。</summary>
        public StatAppendageSet HeroStatAppendages => _heroStatAppendages;

        /// <summary>挂到主角身上的属性改变附加物个数(含顶替掉旧的那一次)。</summary>
        public int HeroStatAppendagesAdded { get; private set; }

        /// <summary>挂上时顶替掉的旧附加物个数。</summary>
        public int HeroStatAppendagesReplaced { get; private set; }

        /// <summary>被 [DELETE APPENDAGE] 删掉的个数。</summary>
        public int HeroStatAppendagesRemoved { get; private set; }

        /// <summary>时限到了自己消失的个数(主角死亡清空的不算)。</summary>
        public int HeroStatAppendagesExpired { get; private set; }

        /// <summary>挂到主角身上的附加物按类型号分的次数。</summary>
        public IReadOnlyDictionary<int, int> HeroStatTypeCounts => _heroStatTypeAdds;

        /// <summary>这个房间里怪物身上挂过的属性改变附加物个数(含顶替掉旧的那一次)。</summary>
        public int MonsterStatAppendagesAdded => SumMonsters(m => m.StatAppendagesAdded);

        /// <summary>怪物身上挂上时顶替掉的旧附加物个数。</summary>
        public int MonsterStatAppendagesReplaced => SumMonsters(m => m.StatAppendagesReplaced);

        /// <summary>怪物身上被 [DELETE APPENDAGE] 删掉的个数。</summary>
        public int MonsterStatAppendagesRemoved => SumMonsters(m => m.StatAppendagesRemoved);

        /// <summary>怪物身上时限到了自己消失的个数。</summary>
        public int MonsterStatAppendagesExpired => SumMonsters(m => m.StatAppendagesExpired);

        /// <summary>怪物身上挂过的附加物按类型号分的次数(各怪物之和)。</summary>
        public Dictionary<int, int> MonsterStatTypeCounts
        {
            get
            {
                var total = new Dictionary<int, int>();
                foreach (MonsterActor m in _monsters)
                {
                    foreach (KeyValuePair<int, int> kv in m.StatAppendageTypeCounts)
                    {
                        total.TryGetValue(kv.Key, out int n);
                        total[kv.Key] = n + kv.Value;
                    }
                }
                return total;
            }
        }

        /// <summary>[APPENDAGE] 作用到主角(主角的 <see cref="HeroActEntity"/> 转过来的):没有生命条的模式与死了的主角不挂。</summary>
        public void AddHeroStatAppendage(StatChange change, int durationMs, IActEntity source)
        {
            if (Vitals == null || HeroDead)
            {
                return;
            }
            HeroStatAppendagesReplaced += _heroStatAppendages.Add(source, change, durationMs);
            HeroStatAppendagesAdded++;
            _heroStatTypeAdds.TryGetValue(change.Type, out int n);
            _heroStatTypeAdds[change.Type] = n + 1;
            ApplyHeroStatRates(_room.Hero);
        }

        /// <summary>[DELETE APPENDAGE] 作用到主角:删掉来源是 <paramref name="source"/> 的同一项。</summary>
        public void RemoveHeroStatAppendage(StatChange change, IActEntity source)
        {
            int removed = _heroStatAppendages.Remove(source, change);
            if (removed > 0)
            {
                HeroStatAppendagesRemoved += removed;
                ApplyHeroStatRates(_room.Hero);
            }
        }

        /// <summary>每一小步:附加物按墙钟倒计时(命中停顿里不冻结,推断);主角死了清空;速度倍率有变化或出招结束就落实。</summary>
        private void StepHeroStatAppendages(double deltaMs)
        {
            if (_heroStatAppendages.Count > 0)
            {
                if (HeroDead)
                {
                    _heroStatAppendages.Clear();
                }
                else
                {
                    HeroStatAppendagesExpired += _heroStatAppendages.Advance(deltaMs);
                }
            }
            ApplyHeroStatRates(_room.Hero);
        }

        /// <summary>把附加物集合现在的移动速度与攻击速度倍率相对地乘到主角身上(与上一次已乘的倍率比,只乘变化的部分)。</summary>
        private void ApplyHeroStatRates(TownHero hero)
        {
            double move = HeroRate(StatChangeTypes.MoveSpeed);
            if (Math.Abs(move - _heroStatMoveRate) > 1e-9)
            {
                double scale = move / _heroStatMoveRate;
                hero.SpeedFactorX *= scale;
                hero.SpeedFactorY *= scale;
                _heroStatMoveRate = move;
            }
            if (!hero.IsAttacking)
            {
                double attack = HeroRate(StatChangeTypes.AttackSpeed);
                if (Math.Abs(attack - _heroStatAttackRate) > 1e-9)
                {
                    hero.AttackAnimationRate *= attack / _heroStatAttackRate;
                    _heroStatAttackRate = attack;
                }
            }
        }

        private double HeroRate(int type)
        {
            if (_heroStatAppendages.Count == 0)
            {
                return 1.0;
            }
            double rate = _heroStatAppendages.SpeedRate(type);
            return rate < HeroStatMinRate ? HeroStatMinRate : rate;
        }
    }
}
