using System;
using System.Collections.Generic;

// 证据:E-136。种类与每种的数值公式见 ActiveStatusFormulas(服务端共享公式库,与原版机器码对拍过);
// 这一份是"身上挂着哪些状态、怎么走时间、掉几跳血"的运行期模型,规则出处与推断在每个成员的注释里标明。
namespace Dnf.Game
{
    /// <summary>施加一个异常状态的结果。</summary>
    public enum ActiveStatusApply
    {
        /// <summary>没有效果:种类是"没有"或元素,或者算出来的持续时间不是正数。</summary>
        Ignored,

        /// <summary>概率没掷中。</summary>
        Missed,

        /// <summary>新挂上了。</summary>
        Applied,

        /// <summary>同种状态已经在身上,新的持续得更久,换成新的(不叠加)。</summary>
        Refreshed,

        /// <summary>同种状态已经在身上,而且它剩得不比新的短:保持原样。</summary>
        Kept,
    }

    /// <summary>一次持续伤害("一跳"):哪种状态、掉多少血。</summary>
    public readonly struct ActiveStatusTick
    {
        public ActiveStatusKind Kind { get; }
        public int Damage { get; }

        public ActiveStatusTick(ActiveStatusKind kind, int damage)
        {
            Kind = kind;
            Damage = damage;
        }
    }

    /// <summary>
    /// 身上的一个异常状态:种类、总时长、已过时间、附加参数,以及持续伤害类的"每跳间隔、一共几跳、每跳多少"。
    /// 同一种状态同一时刻只有一个(再次挂上不叠加,见 <see cref="ActiveStatusSet.Apply"/>)。
    /// </summary>
    public sealed class ActiveStatusInstance
    {
        internal ActiveStatusInstance(ActiveStatusKind kind, int level, int durationMs, int[] extra, int intervalMs, int tickCount, int tickDamage)
        {
            Kind = kind;
            Level = level;
            DurationMs = durationMs;
            Extra = extra;
            IntervalMs = intervalMs;
            TickCount = tickCount;
            TickDamage = tickDamage;
        }

        public ActiveStatusKind Kind { get; }

        /// <summary>状态等级(攻击信息里写的;为 0 的怪物招式按一级的表值算时长)。</summary>
        public int Level { get; }

        /// <summary>这一次挂上的总时长(毫秒)。</summary>
        public int DurationMs { get; }

        /// <summary>附加参数(中毒、出血等的伤害量;减速的两个百分数……),个数由种类决定。</summary>
        public int[] Extra { get; }

        /// <summary>已经过去的毫秒数。</summary>
        public double ElapsedMs { get; internal set; }

        /// <summary>被"摇脱"缩短掉的毫秒数(<see cref="ActiveStatusSet.Shake"/>)。</summary>
        public double ShakenMs { get; internal set; }

        /// <summary>持续伤害的间隔(毫秒);没有持续伤害的状态是 0。</summary>
        public int IntervalMs { get; }

        /// <summary>持续伤害一共掉几跳;没有持续伤害的状态是 0。</summary>
        public int TickCount { get; }

        /// <summary>每一跳掉多少血。</summary>
        public int TickDamage { get; }

        /// <summary>已经掉过几跳。</summary>
        public int TicksDone { get; internal set; }

        /// <summary>还剩多少毫秒(不会是负数)。</summary>
        public double RemainingMs
        {
            get
            {
                double r = DurationMs - ShakenMs - ElapsedMs;
                return r > 0 ? r : 0;
            }
        }

        public bool Expired => RemainingMs <= 0;

        /// <summary>第 <paramref name="n"/> 跳(从 1 数)发生在状态开始之后多少毫秒:每个间隔一跳,最后一跳不晚于时长(时长不足一个间隔时就是时长那一刻的一跳)。</summary>
        public int TickTimeMs(int n)
        {
            long t = (long)n * IntervalMs;
            return (int)Math.Min(t, DurationMs);
        }
    }

    /// <summary>异常状态的运行期分类与常量。出处:E-136(读码)与推断,逐项标注。</summary>
    public static class ActiveStatusRules
    {
        /// <summary>
        /// 让主角动不了的种类:冰冻、眩晕、石化、睡眠、束缚。出处:客户端"能不能移动"判断(0x005ad4e0,静态读码)里查的集合是这五种加失明;
        /// 失明在这一版只当数据挂着(没有读到走路输入处理函数,它在受保护区域里),所以没放进来(推断)。
        /// </summary>
        public static bool LocksControl(ActiveStatusKind kind)
        {
            switch (kind)
            {
                case ActiveStatusKind.Freeze:
                case ActiveStatusKind.Stun:
                case ActiveStatusKind.Stone:
                case ActiveStatusKind.Sleep:
                case ActiveStatusKind.Hold:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>有持续伤害的种类:服务端的"同步种类"(<see cref="ActiveStatusFormulas.IsSync"/>):中毒、感电、石化、灼伤、出血。</summary>
        public static bool DealsDamage(ActiveStatusKind kind)
        {
            return ActiveStatusFormulas.IsSync((int)kind);
        }

        /// <summary>有益的种类(加速、祝福):怪物的招式带它们没有道理,只当数据挂着。</summary>
        public static bool IsBeneficial(ActiveStatusKind kind)
        {
            return kind == ActiveStatusKind.Haste || kind == ActiveStatusKind.Bless;
        }

        /// <summary>中毒与灼伤每一跳的间隔(毫秒):占位值,客户端的这两个持续伤害对象没读到(推断)。</summary>
        public const int PoisonBurnTickMs = 1000;

        /// <summary>数值表里没读到 [lightning time per number] 时感电与石化用的间隔(毫秒)。</summary>
        public const int FallbackLightningTickMs = 500;

        /// <summary>
        /// 持续伤害的间隔(毫秒)。出血:500(BLEEDING_DAMAGE_TERM,服务端常量,E-136);感电:数值表的 [lightning time per number]("每多少毫秒一次"数,推断是电击的间隔),
        /// 石化与感电共用同一个服务端分派分支,按同一个间隔(推断);中毒与灼伤:<see cref="PoisonBurnTickMs"/>(占位)。没有持续伤害的种类是 0。
        /// </summary>
        public static int TickIntervalMs(ActiveStatusTable table, ActiveStatusKind kind)
        {
            switch (kind)
            {
                case ActiveStatusKind.Bleeding:
                    return ActiveStatusFormulas.BleedingTermMs;
                case ActiveStatusKind.Lightning:
                case ActiveStatusKind.Stone:
                    return table != null && table.LightningTimePerNumber > 0 ? table.LightningTimePerNumber : FallbackLightningTickMs;
                case ActiveStatusKind.Poison:
                case ActiveStatusKind.Burn:
                    return PoisonBurnTickMs;
                default:
                    return 0;
            }
        }
    }

    /// <summary>
    /// 一个受害者(主角)身上的异常状态集合(M43,证据 E-136)。
    /// <list type="bullet">
    /// <item>施加:概率 = 攻击信息写的概率(百分数;写 1000 是必中);时长取攻击信息里写的毫秒数,没写(0)就按数值表的
    /// getActiveStatusDurationServer(<see cref="ActiveStatusFormulas.DurationMs"/>)。状态等级(攻击信息的第二个数)不为 0 时,概率、时长与部分附加参数
    /// 再按"状态等级与受害者等级之差"修正(客户端受击方的挂状态入口读到的规则,见 <see cref="ActiveStatusFormulas.LevelAdjustedProbability"/>);
    /// 等级为 0 的槽客户端那条路径直接不挂,这里照常挂、不做等级修正(怪物打主角是否另有服务端路径没读到,推断)。
    /// 攻击信息的 [human active status rate] 只在决斗类模式里对人类受害者生效(客户端 0x008260f0 里先问游戏模式),地下城里不用,所以这里不乘。</item>
    /// <item>同一种状态不叠加:再挂上时持续得更久的留下。</item>
    /// <item>持续伤害:<see cref="ActiveStatusRules.DealsDamage"/> 的种类把附加参数第 0 项(<see cref="ActiveStatusInstance.Extra"/>)当作整次的总量,
    /// 按 <see cref="ActiveStatusRules.TickIntervalMs"/> 摊开(<see cref="ActiveStatusFormulas.EvenTickDamage"/>;出血就是客户端公式,其余是推断),
    /// 每跳都不会超过服务端的允许值(<see cref="ActiveStatusFormulas.ServerAllowedDamage"/>)。</item>
    /// <item>失去控制(<see cref="ControlLocked"/>)、减速(<see cref="MoveSlowPercent"/>)是给调用方读的;睡着的挨打就醒(<see cref="NotifyHit"/>,
    /// 客户端受击处理里读到的"睡眠被打醒"分支,条件的细节没读通,这里按任何一击处理,推断);冰冻、眩晕、石化能乱按方向键摇脱(<see cref="Shake"/>,
    /// 服务端的 ACTIVESTATUS_SHAKABLE 表),每按一下缩短多少在客户端受保护区域里没读到,由调用方给占位值。</item>
    /// </list>
    /// </summary>
    public sealed class ActiveStatusSet
    {
        private readonly ActiveStatusTable _table;
        private readonly List<ActiveStatusInstance> _active = new List<ActiveStatusInstance>();

        public ActiveStatusSet(ActiveStatusTable table)
        {
            _table = table ?? ActiveStatusTable.Empty;
        }

        public ActiveStatusTable Table => _table;

        /// <summary>身上现有的状态(按挂上的先后)。</summary>
        public IReadOnlyList<ActiveStatusInstance> Active => _active;

        public int Count => _active.Count;

        /// <summary>从开始用起一共成功挂上(含刷新)的次数。</summary>
        public int AppliedCount { get; private set; }

        /// <summary>从开始用起掷了概率但没中的次数。</summary>
        public int MissedCount { get; private set; }

        /// <summary>从开始用起一共掉过的持续伤害(各跳之和)。</summary>
        public long TickDamageTotal { get; private set; }

        public bool Has(ActiveStatusKind kind)
        {
            return Find(kind) != null;
        }

        public ActiveStatusInstance Find(ActiveStatusKind kind)
        {
            foreach (ActiveStatusInstance s in _active)
            {
                if (s.Kind == kind)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>身上有任何会让主角失去控制的状态(<see cref="ActiveStatusRules.LocksControl"/>)。</summary>
        public bool ControlLocked
        {
            get
            {
                foreach (ActiveStatusInstance s in _active)
                {
                    if (ActiveStatusRules.LocksControl(s.Kind))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>失去控制的状态里还剩得最久的那个的毫秒数;没有是 0。</summary>
        public double LockRemainingMs
        {
            get
            {
                double best = 0;
                foreach (ActiveStatusInstance s in _active)
                {
                    if (ActiveStatusRules.LocksControl(s.Kind) && s.RemainingMs > best)
                    {
                        best = s.RemainingMs;
                    }
                }
                return best;
            }
        }

        /// <summary>
        /// 减速的移动速度减少百分数(减速状态附加参数第 0 项,夹在 0 到 90);没有减速是 0。
        /// 出处:减速带两个附加参数(服务端 makeEtcParameters,E-136),表里是 0 到 100 的百分数;哪一项管移动、哪一项管攻击是推断(第 0 项移动、第 1 项攻击)。
        /// </summary>
        public int MoveSlowPercent => SlowPercent(0);

        /// <summary>减速的攻击速度减少百分数(附加参数第 1 项,同上是推断);这一版的运行期没有接它,只读出来。</summary>
        public int AttackSlowPercent => SlowPercent(1);

        private int SlowPercent(int slot)
        {
            ActiveStatusInstance slow = Find(ActiveStatusKind.Slow);
            if (slow == null || slot >= slow.Extra.Length)
            {
                return 0;
            }
            return Math.Max(0, Math.Min(90, slow.Extra[slot]));
        }

        /// <summary>
        /// 施加一个状态。<paramref name="roll"/> 是调用方掷的 [0, 1) 随机数,<paramref name="victimLevel"/> 是受害者(主角)的等级,不大于 0 表示不知道、不做等级修正。
        /// 先看种类与时长能不能生效(不能是 <see cref="ActiveStatusApply.Ignored"/>);状态等级不为 0 且概率不是"必中"时,概率先按等级差修正
        /// (加速与祝福不修正);再拿 <c>|概率| / 100</c>(必中是 100%)与随机数比较;掷中后时长与附加参数按等级差修正,修正后时长不是正数也是 Ignored。
        /// </summary>
        public ActiveStatusApply Apply(ActiveStatusEntry entry, int victimLevel, double roll)
        {
            if (entry == null || entry.Kind == ActiveStatusKind.None || entry.Kind == ActiveStatusKind.Element)
            {
                return ActiveStatusApply.Ignored;
            }
            int kind = (int)entry.Kind;
            int duration = entry.Duration > 0 ? entry.Duration : ActiveStatusFormulas.DurationMs(_table, kind, Math.Max(1, entry.Level), 0, 1);
            if (duration <= 0)
            {
                return ActiveStatusApply.Ignored;
            }
            bool leveled = entry.Level != 0 && victimLevel > 0;
            float probability = entry.Probability;
            bool certain = probability == ActiveStatusFormulas.CertainProbability;
            if (leveled && !certain && !ActiveStatusRules.IsBeneficial(entry.Kind))
            {
                probability = ActiveStatusFormulas.LevelAdjustedProbability(_table, probability, entry.Level, victimLevel);
            }
            double chance = certain ? 1.0 : Math.Abs(probability) / 100.0;
            if (!(roll < chance))
            {
                MissedCount++;
                return ActiveStatusApply.Missed;
            }
            int[] extra = entry.Extra == null ? Array.Empty<int>() : (int[])entry.Extra.Clone();
            if (leveled)
            {
                duration = ActiveStatusFormulas.LevelAdjustedDuration(_table, kind, duration, entry.Level, victimLevel, extra.Length);
                extra = ActiveStatusFormulas.LevelAdjustedExtras(_table, kind, extra, entry.Level, victimLevel);
                if (duration <= 0)
                {
                    return ActiveStatusApply.Ignored;
                }
            }
            int interval = 0, ticks = 0, tickDamage = 0;
            if (ActiveStatusRules.DealsDamage(entry.Kind) && extra.Length > 0 && extra[0] > 0)
            {
                interval = ActiveStatusRules.TickIntervalMs(_table, entry.Kind);
                ticks = Math.Max(1, duration / interval);
                tickDamage = ActiveStatusFormulas.EvenTickDamage(new ActiveStatusDamageInput(extra[0], duration), interval);
            }
            var instance = new ActiveStatusInstance(entry.Kind, entry.Level, duration, extra, interval, ticks, tickDamage);
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].Kind != entry.Kind)
                {
                    continue;
                }
                if (instance.RemainingMs > _active[i].RemainingMs)
                {
                    _active[i] = instance;
                    AppliedCount++;
                    return ActiveStatusApply.Refreshed;
                }
                return ActiveStatusApply.Kept;
            }
            _active.Add(instance);
            AppliedCount++;
            return ActiveStatusApply.Applied;
        }

        /// <summary>
        /// 走时间:每个状态已过时间加 <paramref name="deltaMs"/>,到点的持续伤害各掉一跳(记进 <paramref name="ticks"/>,可以传 null),过期的摘掉。
        /// 被摇脱缩短的状态按缩短后的时长过期,过期之后没掉的跳不再掉。
        /// </summary>
        public void Advance(double deltaMs, List<ActiveStatusTick> ticks)
        {
            if (deltaMs <= 0 || _active.Count == 0)
            {
                return;
            }
            foreach (ActiveStatusInstance s in _active)
            {
                s.ElapsedMs += deltaMs;
                if (s.TickCount <= 0)
                {
                    continue;
                }
                double limit = Math.Min(s.ElapsedMs, s.DurationMs - s.ShakenMs);
                while (s.TicksDone < s.TickCount && s.TickTimeMs(s.TicksDone + 1) <= limit)
                {
                    s.TicksDone++;
                    TickDamageTotal += s.TickDamage;
                    ticks?.Add(new ActiveStatusTick(s.Kind, s.TickDamage));
                }
            }
            _active.RemoveAll(s => s.Expired);
        }

        /// <summary>
        /// 摇脱:冰冻、眩晕、石化(<see cref="ActiveStatusFormulas.IsShakable"/>)的剩余时间缩短 <paramref name="ms"/> 毫秒;身上有这类状态返回 true。
        /// 每按一下方向键缩短多少没有读到(客户端的摇脱处理在受保护区域),由调用方给占位值。
        /// </summary>
        public bool Shake(double ms)
        {
            bool any = false;
            foreach (ActiveStatusInstance s in _active)
            {
                if (ActiveStatusFormulas.IsShakable((int)s.Kind))
                {
                    s.ShakenMs += ms;
                    any = true;
                }
            }
            _active.RemoveAll(s => s.Expired);
            return any;
        }

        /// <summary>挨了一击:睡眠被打醒(推断见类注释)。</summary>
        public void NotifyHit()
        {
            _active.RemoveAll(s => s.Kind == ActiveStatusKind.Sleep);
        }

        /// <summary>清空(死亡、复活、换房间)。</summary>
        public void Clear()
        {
            _active.Clear();
        }
    }
}
