using System;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 主角的生命、魔法与受击相关的属性。**占位**:数取自角色脚本 .chr 里 [initial value] 一节(第 1 级的初始值),
    /// 成长类型([growtype])、等级与装备怎么叠加成最终面板(客户端的算法还没读,服务端没有这部分)都没有做,
    /// 所以升级、换装备不会改变这里的数。证据 E-086。
    /// 魔法值(证据 E-087):放技能时扣 .skl 的 [consume MP](<see cref="TrySpendMp"/>),按 .chr 的 [MP regen speed] 回复(<see cref="RegenMp"/>);
    /// 回复速度的单位(每分钟)是从基线魔法球液面的粗测推断的,客户端的扣蓝与回蓝代码没有读到。
    /// </summary>
    public sealed class HeroVitals
    {
        public int Level { get; }
        public int HpMax { get; }
        public int Hp { get; private set; }
        public int MpMax { get; }
        public int Mp { get; private set; }

        /// <summary>魔法值恢复速度:每分钟恢复多少点([MP regen speed],单位是推断的,见类注释);0 表示不恢复。</summary>
        public double MpRegenPerMinute { get; }

        /// <summary>
        /// 魔法值恢复攒下的量,单位"点 × 60000":每步加 速度(点/分钟)× 毫秒,攒够 60000 就是 1 点。
        /// 不用"点"做单位是为了让很多小步累加不产生浮点误差(整数速度与整数毫秒的乘积是精确的)。
        /// </summary>
        private double _mpBank;

        private const double BankPerPoint = 60000.0;

        /// <summary>物理防御力([physical defense])。</summary>
        public double PhysicalDefense { get; }

        /// <summary>魔法防御力([magical defense])。</summary>
        public double MagicalDefense { get; }

        /// <summary>受击恢复属性([hit recovery]),受击硬直公式里的 R(越大硬直越短,证据 E-080)。</summary>
        public int HitRecovery { get; }

        public bool IsAlive => Hp > 0;

        public HeroVitals(int level, int hpMax, int mpMax, double physicalDefense, double magicalDefense, int hitRecovery, double mpRegenPerMinute = 0.0)
        {
            Level = level < 1 ? 1 : level;
            HpMax = hpMax < 1 ? 1 : hpMax;
            Hp = HpMax;
            MpMax = mpMax < 0 ? 0 : mpMax;
            Mp = MpMax;
            PhysicalDefense = physicalDefense < 0 ? 0 : physicalDefense;
            MagicalDefense = magicalDefense < 0 ? 0 : magicalDefense;
            HitRecovery = hitRecovery < 0 ? 0 : hitRecovery;
            MpRegenPerMinute = double.IsNaN(mpRegenPerMinute) || mpRegenPerMinute < 0.0 ? 0.0 : mpRegenPerMinute;
        }

        /// <summary>
        /// 读 .chr 的 [initial value](同名标签取第一次出现,正好是初始值那一节):[HP MAX]、[MP MAX]、[physical defense]、[magical defense]、[hit recovery]、
        /// [MP regen speed]。没写的项取 1、0、0、0、0、0(血量至少是 1)。
        /// 注意:同一个标签在脚本里既可能存成整数也可能存成浮点数(真实的 .chr 里生命、魔法与受击恢复就是浮点数),
        /// 所以一律按"第一个数值"读(整数用 IntOr 会把浮点数当成没写,血量退成 1),再四舍五入成整数。
        /// </summary>
        public static HeroVitals FromChr(CharacterDef chr, int level = 1)
        {
            if (chr == null)
            {
                return new HeroVitals(level, 1, 0, 0, 0, 0);
            }
            TagTable tags = chr.Tags;
            return new HeroVitals(level, Round(NumberOr(tags, "[HP MAX]", 1.0)), Round(NumberOr(tags, "[MP MAX]", 0.0)), NumberOr(tags, "[physical defense]", 0.0),
                NumberOr(tags, "[magical defense]", 0.0), Round(NumberOr(tags, "[hit recovery]", 0.0)), NumberOr(tags, "[MP regen speed]", 0.0));
        }

        private static double NumberOr(TagTable tags, string tag, double fallback)
        {
            if (tags.TryGetLine(tag, out ScriptLine line))
            {
                double? n = line.FirstNumber();
                if (n.HasValue)
                {
                    return n.Value;
                }
            }
            return fallback;
        }

        private static int Round(double value)
        {
            if (double.IsNaN(value))
            {
                return 0;
            }
            double r = Math.Round(value, MidpointRounding.AwayFromZero);
            return r >= int.MaxValue ? int.MaxValue : r <= int.MinValue ? int.MinValue : (int)r;
        }

        /// <summary>扣血(不低于 0);返回这一下是否把主角打死(从活着到 0)。</summary>
        public bool Damage(int amount)
        {
            if (Hp <= 0)
            {
                return false;
            }
            Hp = Math.Max(0, Hp - Math.Max(0, amount));
            return Hp == 0;
        }

        /// <summary>
        /// 扣魔法值:够就扣并返回 true;不够(含主角已死)不扣,返回 false。消耗 ≤ 0 恒为 true。
        /// 放技能时在技能真正开始的那一刻调用(与冷却同一时刻,证据 E-087)。
        /// </summary>
        public bool TrySpendMp(int amount)
        {
            if (amount <= 0)
            {
                return true;
            }
            if (Hp <= 0 || Mp < amount)
            {
                return false;
            }
            Mp -= amount;
            return true;
        }

        /// <summary>
        /// 魔法值恢复 <paramref name="deltaMs"/> 毫秒:按每分钟 <see cref="MpRegenPerMinute"/> 点连续攒,攒够 1 点才加到 <see cref="Mp"/>(小数留到下一步),
        /// 加到上限为止(满了就不再攒)。主角死了不恢复。墙钟时间,不受命中停顿影响(与冷却一样的假设)。
        /// </summary>
        public void RegenMp(double deltaMs)
        {
            if (deltaMs <= 0.0 || Hp <= 0 || MpRegenPerMinute <= 0.0)
            {
                return;
            }
            if (Mp >= MpMax)
            {
                _mpBank = 0.0;
                return;
            }
            _mpBank += MpRegenPerMinute * deltaMs;
            double whole = Math.Floor(_mpBank / BankPerPoint);
            if (whole >= 1.0)
            {
                _mpBank -= whole * BankPerPoint;
                Mp = (int)Math.Min((double)MpMax, Mp + whole);
                if (Mp >= MpMax)
                {
                    _mpBank = 0.0;
                }
            }
        }

        /// <summary>回满生命与魔法(复活、重置用)。</summary>
        public void Restore()
        {
            Hp = HpMax;
            Mp = MpMax;
            _mpBank = 0.0;
        }

        /// <summary>
        /// 把另一份的当前生命与魔法抄过来(夹在自己的上限内):副本换房间时每个房间有自己的会话与生命值对象,血量要跟着人走。
        /// 抄过来是 0 就是 0(死了的主角不会因为换房间复活)。
        /// </summary>
        public void CopyStateFrom(HeroVitals other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }
            Hp = Math.Max(0, Math.Min(HpMax, other.Hp));
            Mp = Math.Max(0, Math.Min(MpMax, other.Mp));
            _mpBank = Mp >= MpMax ? 0.0 : other._mpBank;
        }
    }
}
