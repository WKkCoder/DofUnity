using System;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 主角的生命、魔法与受击相关的属性。**占位**:<see cref="FromChr"/> 取角色脚本 .chr 里 [initial value] 一节(第 1 级的初始值);
    /// 有等级进度时用 <see cref="FromStats"/>(初始值加上每升一级的增量,服务端 CalcStat 的算法,证据 E-111),升级时 <see cref="ApplyStats"/> 换上新等级的上限与属性
    /// (当前生命与魔法不变——没有证据表明升级会回复)。装备与属性点对生命魔法的加成都没有做。证据 E-086。
    /// 魔法值(证据 E-087):放技能时扣 .skl 的 [consume MP](<see cref="TrySpendMp"/>),按 .chr 的 [MP regen speed] 回复(<see cref="RegenMp"/>);
    /// 回复速度的单位(每分钟)是从基线魔法球液面的粗测推断的,客户端的扣蓝与回蓝代码没有读到。
    /// </summary>
    public sealed class HeroVitals
    {
        public int Level { get; private set; }
        public int HpMax { get; private set; }
        public int Hp { get; private set; }
        public int MpMax { get; private set; }
        public int Mp { get; private set; }

        /// <summary>魔法值恢复速度:每分钟恢复多少点([MP regen speed],单位是推断的,见类注释);0 表示不恢复。</summary>
        public double MpRegenPerMinute { get; private set; }

        /// <summary>
        /// 魔法值恢复攒下的量,单位"点 × 60000":每步加 速度(点/分钟)× 毫秒,攒够 60000 就是 1 点。
        /// 不用"点"做单位是为了让很多小步累加不产生浮点误差(整数速度与整数毫秒的乘积是精确的)。
        /// </summary>
        private double _mpBank;

        private const double BankPerPoint = 60000.0;

        /// <summary>物理防御力([physical defense])。</summary>
        public double PhysicalDefense { get; private set; }

        /// <summary>魔法防御力([magical defense])。</summary>
        public double MagicalDefense { get; private set; }

        /// <summary>受击恢复属性([hit recovery]),受击硬直公式里的 R(越大硬直越短,证据 E-080)。</summary>
        public int HitRecovery { get; private set; }

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

        /// <summary>
        /// 用某个等级的属性块(<see cref="CharacterGrowth.StatsAt"/>,十分之一单位的整数)造一份满血满蓝的状态(证据 E-111):
        /// 生命与魔法上限、受击恢复四舍五入成整数,防御力与魔法恢复速度取"十分之一"换回来的数。
        /// 等级 1 时与 <see cref="FromChr"/> 一致(初始值那一节的数)。
        /// </summary>
        public static HeroVitals FromStats(int level, StatBlock stats)
        {
            return new HeroVitals(level, FromTenths(stats.HpMax), FromTenths(stats.MpMax), stats.PhysicalDefense / 10.0, stats.MagicalDefense / 10.0,
                FromTenths(stats.HitRecovery), stats.MpRegenSpeed / 10.0);
        }

        private static int FromTenths(long tenths)
        {
            return Round(tenths / 10.0);
        }

        /// <summary>
        /// 升级后换上新等级的上限与属性(<paramref name="next"/> 是 <see cref="FromStats"/> 造出的新等级状态):当前生命与魔法保持不变(夹在新上限内)。
        /// 升级时有没有回复生命魔法没有证据,这里按"不回复"做;魔法恢复攒下的零头也留着。
        /// </summary>
        public void ApplyStats(HeroVitals next)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            Level = next.Level;
            HpMax = next.HpMax;
            MpMax = next.MpMax;
            PhysicalDefense = next.PhysicalDefense;
            MagicalDefense = next.MagicalDefense;
            HitRecovery = next.HitRecovery;
            MpRegenPerMinute = next.MpRegenPerMinute;
            Hp = Math.Min(Hp, HpMax);
            Mp = Math.Min(Mp, MpMax);
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
