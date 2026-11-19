using System;
using System.Collections.Generic;
using Dnf.Game;

// 跨职业技能状态族配方(阶段 2a,静态读码 + 服务端枚举对拍):
// - 状态 0x11(增益):基类 BaseChar(0x00863020 进入 / 0x008680c0 逐帧 / 0x00863020 状态退出调虚槽 +0x9ac)全 11 职业共用,
//   基类 0x0085deb0 覆盖 3 个全职业通用增益,各职业子类覆盖 +0x9ac 按技能编号挂属性附加体(0x0043dc00,虚槽 +0xa4 写入属性号/加和模式/数值)。
//   属性号取自服务端 loadAppendageType(0x0889253b)枚举。
// - 状态 0x0d(投掷/二段施放):基类 BaseChar(0x00863369 / 0x0086916b / 0x00687480)全 9 职业共用,支持阶段 0 传 0 走 70ms 兜底(0x008636b5)与阶段 0 常驻霸体。
// - 为避免不同职业间局部技能编号(1..168)相互碰撞,跨职业扩展条目按 (job, skillId) 作用域登记,不改变既有首职业默认表计数与既有测试口径。
namespace Dnf.Play
{
    /// <summary>
    /// 服务端属性附加体类型枚举号(静态读码:服务端 loadAppendageType 0x0889253b,与客户端 0x0043dc00 虚槽 +0xa4 首参一致)。
    /// 这里只收本次跨职业 0x11 增益读码核对过的属性号;未在消费端接入的属性仍先落表留证。
    /// </summary>
    public static class BuffStatIds
    {
        /// <summary>物理攻击力/力量([physical attack])。</summary>
        public const int PhysicalAttack = 0;

        /// <summary>魔法攻击力/智力([magical attack],同 <see cref="BuffStat.MagicalAttack"/>)。</summary>
        public const int MagicalAttack = 1;

        /// <summary>装备物理攻击力([equipment physical attack])。</summary>
        public const int EquipmentPhysicalAttack = 4;

        /// <summary>跳跃力([jump power],同 <see cref="BuffStat.JumpPower"/>)。</summary>
        public const int JumpPower = 8;

        /// <summary>攻击速度([attack speed])。</summary>
        public const int AttackSpeed = 10;

        /// <summary>施放速度([cast speed])。</summary>
        public const int CastSpeed = 12;

        /// <summary>物理暴击率([physical critical hit rate])。</summary>
        public const int PhysicalCriticalHitRate = 15;

        /// <summary>硬直([hit recovery])。</summary>
        public const int HitRecovery = 30;

        /// <summary>命中率/回避修正([good hit])。</summary>
        public const int GoodHit = 33;
    }

    /// <summary>
    /// 多属性 0x11 增益附加体(0x0043dc00)里除主属性之外的一条属性修正(每次调用虚槽 +0xa4 对应一条):
    /// 指定属性枚举号(<see cref="StatType"/>)、数值来源([static data] 下标或 [level info] 列)与缩放系数(如 0.1、-0.1、-0.001)。
    /// </summary>
    public sealed class BuffStatEntry
    {
        public BuffStatEntry(int statType, int amountStaticIndex, int amountColumn, double amountScale = 1.0)
        {
            if (statType < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(statType), "属性附加体枚举号不能为负");
            }
            if (amountStaticIndex < 0 && amountColumn < 0)
            {
                throw new ArgumentException("数值来源在 [static data] 下标与 [level info] 列中至少要指定一个非负项", nameof(amountColumn));
            }
            if (double.IsNaN(amountScale) || double.IsInfinity(amountScale))
            {
                throw new ArgumentOutOfRangeException(nameof(amountScale), "属性缩放系数必须是有限实数");
            }
            StatType = statType;
            AmountStaticIndex = amountStaticIndex;
            AmountColumn = amountColumn;
            AmountScale = amountScale;
        }

        /// <summary>属性附加体类型枚举号(<see cref="BuffStatIds"/>)。</summary>
        public int StatType { get; }

        /// <summary>数值在 [static data] 里的下标;负数表示改取 <see cref="AmountColumn"/>。</summary>
        public int AmountStaticIndex { get; }

        /// <summary>数值在 [level info] 里的列(<see cref="AmountStaticIndex"/> 为负时使用)。</summary>
        public int AmountColumn { get; }

        /// <summary>读出来的整数乘它才是写入附加体的数值(含负号与十分比/千分比系数)。</summary>
        public double AmountScale { get; }

        /// <summary>按技能定义与当前等级算出这条属性修正的数值;读不到定义为 0。</summary>
        public double Evaluate(SkillDef def, int level)
        {
            if (def == null)
            {
                return 0.0;
            }
            int raw = AmountStaticIndex >= 0 ? def.GetStatic(AmountStaticIndex, false) : def.GetLevel(level, AmountColumn, false);
            return raw * AmountScale;
        }
    }

    public sealed partial class BuffRecipe
    {
        /// <summary>
        /// 同一个 0x11 增益附加体(0x0043dc00)额外写入的属性修正(主属性在 <see cref="Stat"/>);没有额外属性为空表。
        /// </summary>
        public IReadOnlyList<BuffStatEntry> ExtraStats { get; private set; } = Array.Empty<BuffStatEntry>();

        /// <summary>复制一份并挂上该增益附加体的额外属性修正(不影响原配方对象)。</summary>
        public BuffRecipe WithExtraStats(params BuffStatEntry[] extras)
        {
            if (extras == null)
            {
                throw new ArgumentNullException(nameof(extras));
            }
            var list = new List<BuffStatEntry>(extras.Length);
            foreach (BuffStatEntry entry in extras)
            {
                if (entry == null)
                {
                    throw new ArgumentException("额外属性修正表不能含空项", nameof(extras));
                }
                list.Add(entry);
            }
            BuffRecipe copy = (BuffRecipe)MemberwiseClone();
            copy.ExtraStats = list.AsReadOnly();
            return copy;
        }
    }

    /// <summary>按职业作用域登记的一条 0x11 增益配方。</summary>
    public sealed class JobBuffEntry
    {
        public JobBuffEntry(int job, BuffRecipe recipe)
        {
            if (job < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(job), "职业编号不能为负");
            }
            Job = job;
            Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        }

        public int Job { get; }

        public BuffRecipe Recipe { get; }
    }

    /// <summary>按职业作用域登记的一条 0x13 防御姿态配方。</summary>
    public sealed class JobGuardEntry
    {
        public JobGuardEntry(int job, GuardRecipe recipe)
        {
            if (job < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(job), "职业编号不能为负");
            }
            Job = job;
            Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        }

        public int Job { get; }

        public GuardRecipe Recipe { get; }
    }

    /// <summary>按职业作用域登记的一条 0x0d 投掷/二段施放配方。</summary>
    public sealed class JobThrowEntry
    {
        public JobThrowEntry(int job, ThrowRecipe recipe)
        {
            if (job < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(job), "职业编号不能为负");
            }
            Job = job;
            Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        }

        public int Job { get; }

        public ThrowRecipe Recipe { get; }
    }

    internal readonly struct JobSkillKey : IEquatable<JobSkillKey>
    {
        public JobSkillKey(int job, int skillId)
        {
            Job = job;
            SkillId = skillId;
        }

        public int Job { get; }

        public int SkillId { get; }

        public bool Equals(JobSkillKey other)
        {
            return Job == other.Job && SkillId == other.SkillId;
        }

        public override bool Equals(object obj)
        {
            return obj is JobSkillKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Job * 397) ^ SkillId;
            }
        }
    }

    public sealed partial class SkillRecipes
    {
        private Dictionary<JobSkillKey, BuffRecipe> _jobBuffs;
        private Dictionary<JobSkillKey, GuardRecipe> _jobGuards;
        private Dictionary<JobSkillKey, ThrowRecipe> _jobThrows;
        private IReadOnlyList<JobBuffEntry> _jobBuffList;
        private IReadOnlyList<JobGuardEntry> _jobGuardList;
        private IReadOnlyList<JobThrowEntry> _jobThrowList;

        /// <summary>按职业作用域登记的跨职业 0x11 增益条目数(不含通用基类增益与首职业旧专属槽)。</summary>
        public int JobBuffCount => _jobBuffList == null ? 0 : _jobBuffList.Count;

        /// <summary>按职业作用域登记的全部 0x11 增益条目。</summary>
        public IReadOnlyList<JobBuffEntry> AllJobBuffs => _jobBuffList ?? Array.Empty<JobBuffEntry>();

        /// <summary>按职业作用域登记的跨职业 0x13 防御姿态条目数。</summary>
        public int JobGuardCount => _jobGuardList == null ? 0 : _jobGuardList.Count;

        /// <summary>按职业作用域登记的全部 0x13 防御姿态条目。</summary>
        public IReadOnlyList<JobGuardEntry> AllJobGuards => _jobGuardList ?? Array.Empty<JobGuardEntry>();

        /// <summary>按职业作用域登记的跨职业 0x0d 投掷条目数。</summary>
        public int JobThrowCount => _jobThrowList == null ? 0 : _jobThrowList.Count;

        /// <summary>按职业作用域登记的全部 0x0d 投掷条目。</summary>
        public IReadOnlyList<JobThrowEntry> AllJobThrows => _jobThrowList ?? Array.Empty<JobThrowEntry>();

        /// <summary>
        /// 复制一份并追加按职业作用域隔离的跨职业配方(不改动原实例)。同一 (job, skillId) 重复传入时后者覆盖前者。
        /// </summary>
        public SkillRecipes WithJobScoped(IEnumerable<JobBuffEntry> jobBuffs = null, IEnumerable<JobGuardEntry> jobGuards = null, IEnumerable<JobThrowEntry> jobThrows = null)
        {
            var buffs = _jobBuffs == null ? new Dictionary<JobSkillKey, BuffRecipe>() : new Dictionary<JobSkillKey, BuffRecipe>(_jobBuffs);
            var buffList = _jobBuffList == null ? new List<JobBuffEntry>() : new List<JobBuffEntry>(_jobBuffList);
            MergeScoped(jobBuffs, nameof(jobBuffs), e => e.Job, e => e.Recipe, r => r.SkillId, buffs, buffList);

            var guards = _jobGuards == null ? new Dictionary<JobSkillKey, GuardRecipe>() : new Dictionary<JobSkillKey, GuardRecipe>(_jobGuards);
            var guardList = _jobGuardList == null ? new List<JobGuardEntry>() : new List<JobGuardEntry>(_jobGuardList);
            MergeScoped(jobGuards, nameof(jobGuards), e => e.Job, e => e.Recipe, r => r.SkillId, guards, guardList);

            var throws = _jobThrows == null ? new Dictionary<JobSkillKey, ThrowRecipe>() : new Dictionary<JobSkillKey, ThrowRecipe>(_jobThrows);
            var throwList = _jobThrowList == null ? new List<JobThrowEntry>() : new List<JobThrowEntry>(_jobThrowList);
            MergeScoped(jobThrows, nameof(jobThrows), e => e.Job, e => e.Recipe, r => r.SkillId, throws, throwList);

            SkillRecipes copy = (SkillRecipes)MemberwiseClone();
            copy._jobBuffs = buffs;
            copy._jobBuffList = buffList.AsReadOnly();
            copy._jobGuards = guards;
            copy._jobGuardList = guardList.AsReadOnly();
            copy._jobThrows = throws;
            copy._jobThrowList = throwList.AsReadOnly();
            return copy;
        }

        private static void MergeScoped<TEntry, TRecipe>(
            IEnumerable<TEntry> incoming,
            string paramName,
            Func<TEntry, int> getJob,
            Func<TEntry, TRecipe> getRecipe,
            Func<TRecipe, int> getSkillId,
            Dictionary<JobSkillKey, TRecipe> map,
            List<TEntry> list)
            where TEntry : class
        {
            if (incoming == null)
            {
                return;
            }
            foreach (TEntry entry in incoming)
            {
                if (entry == null)
                {
                    throw new ArgumentException("职业作用域配方条目不能为 null", paramName);
                }
                int job = getJob(entry);
                TRecipe recipe = getRecipe(entry);
                int skillId = getSkillId(recipe);
                map[new JobSkillKey(job, skillId)] = recipe;
                list.RemoveAll(e => getJob(e) == job && getSkillId(getRecipe(e)) == skillId);
                list.Add(entry);
            }
        }

        /// <summary>按职业作用域查多段技能配方:目前仅首职业(0)有专属多段表,非首职业返回 null 以免 1..168 撞号。</summary>
        public SkillRecipe Find(int job, int skillId)
        {
            return job == EquipRules.SwordmanJob ? Find(skillId) : null;
        }

        /// <summary>
        /// 按职业作用域查 0x11 增益配方:所有非负职业共享基类通用增益表(<see cref="AllBuffs"/>);
        /// 再查该职业专属登记表(<see cref="AllJobBuffs"/>);首职业(0)另回退到旧职业专属表(<see cref="AllClassBuffs"/>)。
        /// </summary>
        public BuffRecipe FindBuff(int job, int skillId)
        {
            if (job < 0)
            {
                return null;
            }
            BuffRecipe shared = FindBuff(skillId);
            if (shared != null)
            {
                return shared;
            }
            return FindClassBuff(job, skillId);
        }

        /// <summary>按职业作用域查职业专属 0x11 增益(不含基类通用增益);非负职业查对应职业表,首职业(0)回退旧专属表。</summary>
        public BuffRecipe FindClassBuff(int job, int skillId)
        {
            if (job < 0)
            {
                return null;
            }
            if (_jobBuffs != null && _jobBuffs.TryGetValue(new JobSkillKey(job, skillId), out BuffRecipe scoped))
            {
                return scoped;
            }
            return job == EquipRules.SwordmanJob ? FindClassBuff(skillId) : null;
        }

        /// <summary>按职业作用域查 0x13 防御姿态配方;首职业(0)回退旧表,其余职业仅查本职业登记表。</summary>
        public GuardRecipe FindGuard(int job, int skillId)
        {
            if (job < 0)
            {
                return null;
            }
            if (_jobGuards != null && _jobGuards.TryGetValue(new JobSkillKey(job, skillId), out GuardRecipe scoped))
            {
                return scoped;
            }
            return job == EquipRules.SwordmanJob ? FindGuard(skillId) : null;
        }

        /// <summary>按职业作用域查 0x0d 投掷配方;首职业(0)回退旧表,其余职业仅查本职业登记表。</summary>
        public ThrowRecipe FindThrow(int job, int skillId)
        {
            if (job < 0)
            {
                return null;
            }
            if (_jobThrows != null && _jobThrows.TryGetValue(new JobSkillKey(job, skillId), out ThrowRecipe scoped))
            {
                return scoped;
            }
            return job == EquipRules.SwordmanJob ? FindThrow(skillId) : null;
        }

        /// <summary>
        /// 给 <see cref="Default"/> 挂上阶段 2a 已静态读码定案的跨职业 0x11 / 0x0d / 0x13 配方(所有地址均出自普通节读码与服务端 0x0889253b 枚举对拍)。
        /// </summary>
        private static SkillRecipes AttachCrossJobDefaults(SkillRecipes baseRecipes)
        {
            // 职业甲(0)与职业癸(9)共用角色子类 0x01706a24(工厂 0x00d1b736),+0x9ac 均走 0x00ce2290:
            // - 技能 38(0x26,循环表 0x019fba84 -> 状态 0x11,解开 M102 遗留的"分派表默认出口"疑点):0x00ce2290 创建属性附加体 0x0043dc00(0,1),
            //   时长取 [level info] 第 0 列,虚槽 +0xa4 依次写入属性 4(装备物理攻击力)=第 2 列、属性 15(物理暴击率)=第 3 列 × 0.1(常量 0x0157893c)。
            //   未建模副字段:对象 +0x6b18 写入第 1 列。
            BuffRecipe job0And9Buff38 = new BuffRecipe(38, (BuffStat)BuffStatIds.EquipmentPhysicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 2)
                .WithExtraStats(new BuffStatEntry(BuffStatIds.PhysicalCriticalHitRate, amountStaticIndex: -1, amountColumn: 3, amountScale: 0.1));

            // - 职业癸(9)的技能 3(0x03)与首职业同走 0x00ce2290 自动防御分支(概率=第 0 列 × 0.01,时长=第 1 列,借用本职业技能 1 的 0x13 防御姿态)。
            BuffRecipe job9AutoGuard = new BuffRecipe(3, BuffStat.AutoGuardChance, durationColumn: 1, amountStaticIndex: -1, amountColumn: 0, amountScale: 0.01, guardSkillId: 1);

            // 职业乙(1,子类 0x0170749c,+0x9ac=0x00bc9340)与职业辛(7,子类 0x01707f1c,+0x9ac=0x00b98e40):
            // - 技能 39(0x27):两类 +0x9ac 代码一致(0x00bc9340 与 0x00b98e40),创建属性附加体 0x0043dc00(0,1),
            //   时长取 [level info] 第 1 列,属性 15(物理暴击率)=第 0 列 × 0.1(0x0157893c),属性 33(命中/good hit)=第 2 列 × -0.1(0x015abe58);无其他状态副作用。
            BuffRecipe job1And7Buff39 = new BuffRecipe(39, (BuffStat)BuffStatIds.PhysicalCriticalHitRate, durationColumn: 1, amountStaticIndex: -1, amountColumn: 0, amountScale: 0.1)
                .WithExtraStats(new BuffStatEntry(BuffStatIds.GoodHit, amountStaticIndex: -1, amountColumn: 2, amountScale: -0.1));

            // - 职业乙(1)的技能 37(0x25,仅 0x00bc9340 分支;0x00b98e40 未实现该分支故不给职业辛):创建属性附加体 0x0043dc00(0,1),
            //   时长取 [level info] 第 0 列,属性 0(物理攻击力)=第 1 列、属性 10(攻击速度)=-[static data][0]、属性 30(硬直)=[static data][0] × -0.001(0x015abe68)。
            //   未建模视觉与副字段:武器外观叠层 0x007a4e20 与对象 +0x61ac 写入 [static data][1] × 0.01。
            BuffRecipe job1Buff37 = new BuffRecipe(37, (BuffStat)BuffStatIds.PhysicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1)
                .WithExtraStats(
                    new BuffStatEntry(BuffStatIds.AttackSpeed, amountStaticIndex: 0, amountColumn: -1, amountScale: -1.0),
                    new BuffStatEntry(BuffStatIds.HitRecovery, amountStaticIndex: 0, amountColumn: -1, amountScale: -0.001));

            // 职业戊(4,子类 0x01709eec 与 0x0170b3dc,+0x9ac 均转 0x00c7b680):
            // - 技能 47(0x2f):0x00c7b680 创建属性附加体 0x0043dc00(0,0x2f),时长取第 0 列,属性 0(物理攻击力)=第 1 列、属性 33(good hit)=第 2 列 × -0.1(0x015abe58);
            //   另有未在服务端 0x0889253b 命名的属性码 0x23(第 3 列 × 0.01)与受保护副附加体 0x021c9dec(0,0,2),均留作未决未入表。
            BuffRecipe job4Buff47 = new BuffRecipe(47, (BuffStat)BuffStatIds.PhysicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1)
                .WithExtraStats(new BuffStatEntry(BuffStatIds.GoodHit, amountStaticIndex: -1, amountColumn: 2, amountScale: -0.1));

            // - 技能 48(0x30):0x00c7b680 创建属性附加体 0x0043dc00(0,0x30),时长取第 0 列,属性 1(魔法攻击力,已有消费端 BuffStat.MagicalAttack)=第 1 列、
            //   属性 12(施放速度)=第 2 列;另有受保护副附加体 0x021c9dec(0,3,2)未入表。
            BuffRecipe job4Buff48 = new BuffRecipe(48, BuffStat.MagicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1)
                .WithExtraStats(new BuffStatEntry(BuffStatIds.CastSpeed, amountStaticIndex: -1, amountColumn: 2, amountScale: 1.0));

            // 职业癸(9)与首职业同属 0x01706a24,技能 1(0x13 防御姿态):动作 [etc motion][0](两帧无攻击框),参数列与首职业完全一致。
            var job9Guard1 = new GuardRecipe(1, motionIndex: 0, minHoldStaticIndex: 0, recoilSpeedColumn: 0, recoilMsColumn: 1, physicalColumn: 2, magicalColumn: 3);

            // 状态 0x0d(投掷/二段施放):职业甲(0)与职业癸(9)共用 0x01706a24,过程键口 0x00cc3c70 压入 [phase=0, mode=0, skillId=0x20, chargeMs=0(走基类 0x008636b5 70ms 兜底),
            // shootMs=[static data][0], pose=1];进入块 0x00cdf55e(无 0x0049c8f0 分叉,两职业完全同码)只在阶段 0 放出老式被动对象 20018(0x4e32,伤害列 0 走 0x00913d70),
            // 并在 0x00cdf726 无条件开启阶段 0 霸体(setSuperArmor(1,1,-1))。
            ThrowRecipe job0And9Throw32 = ThrowRecipe.WithZeroCharge(
                skillId: 32,
                shootStaticIndex: 0,
                alwaysArmorInCharge: true,
                poseSelector: 1,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20018, stage: 0, frame: 0, damageRateColumn: 0, damageFixedColumn: 0),
                });

            return baseRecipes.WithJobScoped(
                jobBuffs: new[]
                {
                    new JobBuffEntry(0, job0And9Buff38),
                    new JobBuffEntry(9, job0And9Buff38),
                    new JobBuffEntry(9, job9AutoGuard),
                    new JobBuffEntry(1, job1And7Buff39),
                    new JobBuffEntry(7, job1And7Buff39),
                    new JobBuffEntry(1, job1Buff37),
                    new JobBuffEntry(4, job4Buff47),
                    new JobBuffEntry(4, job4Buff48),
                },
                jobGuards: new[]
                {
                    new JobGuardEntry(9, job9Guard1),
                },
                jobThrows: new[]
                {
                    new JobThrowEntry(0, job0And9Throw32),
                    new JobThrowEntry(9, job0And9Throw32),
                });
        }
    }
}
