using System;
using System.Collections.Generic;

// 证据:E-141(增益类技能:客户端把增益做成挂在主角附加体容器里的"属性附加体"——类型号取自服务端 .apd 的附加体类型枚举,数值加进合计属性的加和里,
// 带一个毫秒的持续时间;同一个技能再放就换掉旧的附加体、重新计时。持续时间走多久、倒下时清不清是推断,见下面各处标注)。
namespace Dnf.Play
{
    /// <summary>
    /// 增益改的属性。编号是服务端 .apd 附加体类型枚举里的号(E-141:1 魔法攻击力、8 跳跃力);这里只收已经接上消费者的属性。
    /// </summary>
    public enum BuffStat
    {
        /// <summary>魔法攻击力:加进主角的魔法攻击力面板(<see cref="RoomCombatSession.RollFormulaDamage"/> 算伤害时用)。</summary>
        MagicalAttack = 1,

        /// <summary>跳跃力:加进起跳初速度公式里的跳跃力(<see cref="Dnf.Render.TownHero.JumpPowerBonus"/>)。</summary>
        JumpPower = 8,
    }

    /// <summary>一个生效中的增益(客户端:主角附加体容器里的一个属性附加体)。</summary>
    public sealed class HeroBuff
    {
        internal HeroBuff(int skillId, BuffStat stat, double amount, double durationMs)
        {
            SkillId = skillId;
            Stat = stat;
            Amount = amount;
            DurationMs = durationMs;
            RemainingMs = durationMs;
        }

        /// <summary>放出这个增益的技能编号;同一个技能同时只有一个增益。</summary>
        public int SkillId { get; }

        public BuffStat Stat { get; }

        /// <summary>加在属性上的数(加和的一部分,不是倍率)。</summary>
        public double Amount { get; }

        /// <summary>整段持续时间(毫秒)。</summary>
        public double DurationMs { get; }

        /// <summary>还剩的毫秒数;到 0 就失效。</summary>
        public double RemainingMs { get; internal set; }

        /// <summary>剩余的比例(0 到 1),画增益图标的倒计时遮罩用。</summary>
        public double Fraction => DurationMs > 0.0 ? Math.Min(1.0, Math.Max(0.0, RemainingMs / DurationMs)) : 0.0;
    }

    /// <summary>
    /// 主角身上生效中的增益(客户端的附加体容器,E-141)。放增益类技能时,施法动作正常结束的那一刻(状态结束钩子)才挂上,中途被打断就没有;
    /// 同一个技能再放,旧的被换掉、持续时间重新数(客户端:容器里已有这个附加体就先摘下来再挂,时间重写)。
    /// 属性取"加和"(<see cref="Total"/>):生效中的同属性增益的数相加;倍率那一半(百分比类增益)还没有消费者,不做。
    /// 持续时间按墙钟毫秒倒数(客户端附加体的计时单位与时钟没读,按毫秒与墙钟推断);没有时长的增益挂不上。
    /// </summary>
    public sealed class HeroBuffs
    {
        private readonly List<HeroBuff> _active = new List<HeroBuff>();

        /// <summary>生效中的增益,按挂上的先后。</summary>
        public IReadOnlyList<HeroBuff> Active => _active;

        public int Count => _active.Count;

        /// <summary>挂上过多少次(换掉旧的重新挂也算一次)。</summary>
        public int Applied { get; private set; }

        /// <summary>到时失效过多少个(被换掉、被清空的不算)。</summary>
        public int Expired { get; private set; }

        /// <summary>
        /// 挂一个增益;同一个技能的旧增益先被换掉。<paramref name="durationMs"/> 不大于 0 时挂不上,返回 null。
        /// </summary>
        public HeroBuff Apply(int skillId, BuffStat stat, double amount, double durationMs)
        {
            if (durationMs <= 0.0)
            {
                return null;
            }
            RemoveSkill(skillId);
            var buff = new HeroBuff(skillId, stat, amount, durationMs);
            _active.Add(buff);
            Applied++;
            return buff;
        }

        /// <summary>这个技能的增益此刻生效吗。</summary>
        public bool Has(int skillId)
        {
            return Find(skillId) != null;
        }

        /// <summary>这个技能的增益;没有返回 null。</summary>
        public HeroBuff Find(int skillId)
        {
            foreach (HeroBuff b in _active)
            {
                if (b.SkillId == skillId)
                {
                    return b;
                }
            }
            return null;
        }

        /// <summary>某个属性上所有生效中增益的数之和(没有为 0)。</summary>
        public double Total(BuffStat stat)
        {
            double sum = 0.0;
            foreach (HeroBuff b in _active)
            {
                if (b.Stat == stat)
                {
                    sum += b.Amount;
                }
            }
            return sum;
        }

        /// <summary>计时往前走 <paramref name="deltaMs"/> 毫秒,到点的增益失效(同一步里先挂上的先失效)。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0 || _active.Count == 0)
            {
                return;
            }
            for (int i = 0; i < _active.Count; i++)
            {
                _active[i].RemainingMs -= deltaMs;
            }
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i].RemainingMs <= 0.0)
                {
                    _active.RemoveAt(i);
                    Expired++;
                }
            }
        }

        /// <summary>摘掉全部增益(主角倒下:推断,附加体随死亡清掉)。</summary>
        public void Clear()
        {
            _active.Clear();
        }

        /// <summary>换房间时把生效中的增益连同剩余时间抄过来(增益跟着人走);原先的先清掉,计数不抄。</summary>
        public void CopyStateFrom(HeroBuffs other)
        {
            if (other == null || ReferenceEquals(other, this))
            {
                return;
            }
            _active.Clear();
            foreach (HeroBuff b in other._active)
            {
                _active.Add(new HeroBuff(b.SkillId, b.Stat, b.Amount, b.DurationMs) { RemainingMs = b.RemainingMs });
            }
        }

        private void RemoveSkill(int skillId)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].SkillId == skillId)
                {
                    _active.RemoveAt(i);
                    return;
                }
            }
        }
    }
}
