using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

// 证据:E-135(技能状态 0x18 的静态读码:主角自己调用创建被动对象的虚函数(槽 0x340),对象是 passiveobject.lst 里的老式 .obj)。
// 下面几处是推断,客户端的对象逐帧代码与服务端的创建处理没有读到:一个对象对每个怪物只打一次(与怪物放的被动对象打主角一次一致)、
// 没写消失条件的非循环对象在动画播完时消失、写了穿透数([piercing power])的对象不限个数地穿过去、命中不让主角定住(客户端的对象与主角各自有停顿)。
namespace Dnf.Play
{
    /// <summary>
    /// 主角放出去的被动对象(老式的,.obj 只有 [basic motion] 与 [attack info],没有 .act 触发器;波系技能放的就是这一类):
    /// 在世界里放一个位置与朝向,逐帧推进基础动画,动画播到的每一帧的攻击框(类型 15)与怪物的受击框相交就算打中,
    /// 同一个对象对同一个怪物只打一次。伤害用技能自己的(<see cref="Strike"/>:对象 [attack info] 的反应加技能等级的两个伤害列)。
    /// 怪物放的被动对象是 <see cref="PassiveObject"/>,两条路径互不影响。
    /// </summary>
    public sealed class HeroObject
    {
        /// <summary>没有时限、也不会播完(循环动画)的对象最长的寿命(毫秒):与怪物的被动对象同一个保险值。</summary>
        public const int MaxLifeMs = PassiveObject.MaxLifeMs;

        private readonly PassiveTemplate _template;
        private readonly AnimatedEffect _effect;
        private readonly HashSet<MonsterActor> _hitMonsters = new HashSet<MonsterActor>();
        private readonly HashSet<BreakableObject> _hitBreakables = new HashSet<BreakableObject>();
        private int _evaluated = -1;

        public HeroObject(PassiveTemplate template, HeroStrikeInfo strike, int skillId, double x, double y, double z, bool facingLeft)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));
            Strike = strike;
            SkillId = skillId;
            X = x;
            Y = y;
            Z = z;
            FacingLeft = facingLeft;
            PassiveAction basic = template.Basic;
            _effect = AnimatedEffect.CreateFromAni(basic.Ani, basic.AniPath, x, y, z, facingLeft, 1, true);
        }

        public PassiveTemplate Template => _template;

        /// <summary>命中用的攻击信息与伤害数。</summary>
        public HeroStrikeInfo Strike { get; }

        /// <summary>放出这个对象的技能编号。</summary>
        public int SkillId { get; }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }

        public bool FacingLeft { get; }

        /// <summary>已经放了多少毫秒(只在 <see cref="Update"/> 里增长,命中判定与消失都看它)。</summary>
        public double AgeMs { get; private set; }

        /// <summary>已经消失(时限到了、动画播完)。消失的这一步里攻击框仍会按最后到达的帧判一次(见 <see cref="TakeAttackBoxes"/>)。</summary>
        public bool Finished { get; private set; }

        /// <summary>此刻的帧号。</summary>
        public int CurrentFrame => _template.Basic.Timeline.FrameAt((long)AgeMs);

        /// <summary>已经打中过的怪物个数。</summary>
        public int MonstersHit => _hitMonsters.Count;

        /// <summary>这个怪物还没被这个对象打过。</summary>
        public bool CanHit(MonsterActor monster)
        {
            return monster != null && !_hitMonsters.Contains(monster);
        }

        public void MarkHit(MonsterActor monster)
        {
            _hitMonsters.Add(monster);
        }

        public bool CanHit(BreakableObject breakable)
        {
            return breakable != null && !_hitBreakables.Contains(breakable);
        }

        public void MarkHit(BreakableObject breakable)
        {
            _hitBreakables.Add(breakable);
        }

        /// <summary>推进 <paramref name="deltaMs"/> 毫秒,再看消失条件。</summary>
        public void Update(double deltaMs)
        {
            if (Finished || deltaMs <= 0)
            {
                return;
            }
            AgeMs += deltaMs;
            _effect?.Advance(deltaMs);
            AniTimeline timeline = _template.Basic.Timeline;
            bool ended = !timeline.Loops && AgeMs >= timeline.TotalMilliseconds;
            if (_template.TimeLimitMs > 0 && AgeMs >= _template.TimeLimitMs)
            {
                Finished = true;
            }
            else if (ended)
            {
                Finished = true;       // 非循环的动画播完就消失(没写消失条件的老式对象也一样,推断)
            }
            else if (AgeMs >= MaxLifeMs)
            {
                Finished = true;
            }
        }

        /// <summary>
        /// 自上次调用以来新到达的各帧的攻击框(世界坐标,类型 15,跟着朝向镜像、跟着高度抬升),每帧只给一次;这一步里连跳了几帧就把跳过的帧一起给
        /// (与主角的挥击判定一样,慢机器上帧补齐不漏命中)。循环动画绕回起点时从下一帧补到末尾再从头补到当前帧。
        /// </summary>
        public List<Aabb> TakeAttackBoxes()
        {
            var result = new List<Aabb>();
            AniFile ani = _template.Basic.Ani;
            if (ani == null)
            {
                return result;
            }
            int last = ani.Frames.Length - 1;
            int current = Math.Min(CurrentFrame, last);
            if (current >= _evaluated)
            {
                AddBoxes(result, ani, _evaluated + 1, current);
            }
            else
            {
                AddBoxes(result, ani, _evaluated + 1, last);      // 循环绕回:补到末尾再从头补
                AddBoxes(result, ani, 0, current);
            }
            _evaluated = current;
            return result;
        }

        private void AddBoxes(List<Aabb> into, AniFile ani, int from, int to)
        {
            int direction = FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(X), wy = (int)Math.Floor(Y), wz = (int)Math.Floor(Z);
            for (int f = Math.Max(0, from); f <= to && f < ani.Frames.Length; f++)
            {
                foreach (AniBox box in ani.Frames[f].Boxes)
                {
                    if (box.Type == AniBoxType.Attack)
                    {
                        into.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                    }
                }
            }
        }

        /// <summary>此刻的精灵;当前帧没有图或已经消失时返回 null。</summary>
        public SceneSprite Sprite(DnfAssets assets, TownProjection projection)
        {
            return Finished || _effect == null ? null : _effect.Sprite(assets, projection);
        }
    }
}
