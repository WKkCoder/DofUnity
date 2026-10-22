using System;

namespace Dnf.Game
{
    /// <summary>
    /// 一次命中里取自攻击信息(.atk)的那部分输入(证据 E-110)。
    /// 客户端在命中时(受击方槽 <c>+0x388</c> "伤害计算",<c>0x0087b870</c>)把攻击信息的各个字段按下面的对应抄进 <see cref="DamageVariables"/>:
    /// [damage] → 基础伤害、[attack type] → 攻击种类、[weapon damage apply] → 是否加附加基础值(武器攻击力)、[damage bonus] → 百分比加成、
    /// [absolute damage] → 固定值加成、[critical hit] → 暴击率加成、[ignore defense] → 跳过防御、[elemental property] → 四个元素标记、
    /// 三个 [… damage rate](人类、要塞、怪物)→ 三个条件百分比。字段名是我们自己起的。
    /// 不可变;默认值与 <see cref="AttackInfoData"/> 的默认值一致(三个伤害率默认 100)。
    /// </summary>
    public sealed class HitInput
    {
        /// <summary>[damage]:基础伤害(浮点,默认 0;普攻没有这一项,伤害全由武器攻击力与百分比决定)。</summary>
        public float Base { get; }

        /// <summary>[weapon damage apply]:为真时基础伤害再加上攻击者的附加基础值(武器等装备的攻击力)。</summary>
        public bool UseBonusBase { get; }

        /// <summary>[attack type]:物理、魔法、无视防御。</summary>
        public AttackKind Kind { get; }

        /// <summary>[damage bonus]:百分比加成(−15 表示打八五折,100 表示加倍)。</summary>
        public float DamagePercent { get; }

        /// <summary>[absolute damage]:固定值加成的基数。</summary>
        public int AbsoluteDamage { get; }

        /// <summary>[critical hit]:暴击率加成(百分点)。</summary>
        public float CriticalHit { get; }

        /// <summary>[ignore defense]:为真时跳过防御相关的各项。</summary>
        public bool IgnoreDefense { get; }

        /// <summary>[elemental property] 里的元素:第 i 位对应 <see cref="ElementKind"/> 的 i(火、水、暗、光);0 表示无元素。</summary>
        public int ElementMask { get; }

        /// <summary>[human damage rate]:对"人类"目标的伤害率(百分数,默认 100)。</summary>
        public float HumanRate { get; }

        /// <summary>[fort damage rate]:对"要塞"目标的伤害率(默认 100)。</summary>
        public float FortRate { get; }

        /// <summary>[monster damage rate]:对没有特殊状态的普通怪物的伤害率(默认 100)。</summary>
        public float MonsterRate { get; }

        public HitInput(float baseDamage, bool useBonusBase, AttackKind kind, float damagePercent, int absoluteDamage = 0, float criticalHit = 0f,
            bool ignoreDefense = false, int elementMask = 0, float humanRate = 100f, float fortRate = 100f, float monsterRate = 100f)
        {
            Base = baseDamage;
            UseBonusBase = useBonusBase;
            Kind = kind;
            DamagePercent = damagePercent;
            AbsoluteDamage = absoluteDamage;
            CriticalHit = criticalHit;
            IgnoreDefense = ignoreDefense;
            ElementMask = elementMask & 0xF;
            HumanRate = humanRate;
            FortRate = fortRate;
            MonsterRate = monsterRate;
        }

        /// <summary>按一份读好的攻击信息取输入。</summary>
        public static HitInput From(AttackInfoData info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }
            int mask = 0;
            foreach (ElementKind e in info.Elements)
            {
                if ((int)e < 4)
                {
                    mask |= 1 << (int)e;
                }
            }
            return new HitInput(info.Damage, info.WeaponDamageApply, info.AttackType, info.DamageBonus, info.AbsoluteDamage, info.CriticalHit,
                info.IgnoreDefense, mask, info.HumanDamageRate, info.FortDamageRate, info.MonsterDamageRate);
        }

        /// <summary>
        /// 手填的输入(没有 .atk 可读的场合,测试用):只有攻击种类与 [damage bonus],带武器攻击力(普攻的常见写法 [weapon damage apply] 1)。
        /// </summary>
        public static HitInput FromBonus(int damageBonus, bool magical)
        {
            return new HitInput(0f, true, magical ? AttackKind.Magic : AttackKind.Physic, damageBonus);
        }

        /// <summary>这一击带不带第 <paramref name="element"/> 号元素(0 火、1 水、2 暗、3 光)。</summary>
        public bool HasElement(int element)
        {
            return (uint)element < 4u && (ElementMask & (1 << element)) != 0;
        }

        /// <summary>
        /// 技能的伤害:技能脚本 [level info] 当前等级的第 0、1 列是"总倍率百分数"与"固定值"(证据 E-076),
        /// 换成这一击的两个输入:[damage bonus] = 倍率 − 100,[damage] = 固定值 × 100 / 倍率(这样 [damage] × (1 + 加成 / 100) 恰好等于固定值,
        /// 武器攻击力再按倍率放大)。这个换法是推断(客户端里鬼斩的状态函数按这个形状写 [damage],见 E-110),倍率 ≤ 0 时只留固定值。
        /// </summary>
        public HitInput WithSkillDamage(int ratePercent, int fixedDamage)
        {
            float damagePercent = ratePercent > 0 ? ratePercent - 100f : 0f;
            float baseDamage = ratePercent > 0 ? (float)(fixedDamage * 100.0 / ratePercent) : fixedDamage;
            return new HitInput(baseDamage, UseBonusBase, Kind, damagePercent, AbsoluteDamage, CriticalHit, IgnoreDefense, ElementMask, HumanRate, FortRate,
                MonsterRate);
        }
    }

    /// <summary>
    /// 攻击者的面板(命中时从攻击者对象读出来的数,证据 E-110)。客户端把角色的各项属性存成"整数 + 浮点倍率"的记录,读出来是
    /// "整数 × 倍率"向零取整;这里直接存取整后的结果。攻击力、暴击属性等的装备与成长叠加算法没有读到(服务端不算,见 E-110),
    /// 由调用方(<c>Dnf.Play</c> 的 <c>HeroPanel</c>)给出。
    /// </summary>
    public sealed class AttackerPanel
    {
        private readonly int[] _elementAttack = new int[4];

        /// <summary>对象种类码(角色 0x111)。</summary>
        public uint ObjectType { get; set; } = DamageObjectTypes.Character;

        /// <summary>等级:防御率的分母用它(1 到 127)。</summary>
        public int Level { get; set; } = 1;

        /// <summary>第二等级:带 [weapon damage apply] 的攻击算随机幅度时代替等级用(客户端默认 1,角色身上有某个状态时才不同,没读通)。</summary>
        public int SecondLevel { get; set; } = 1;

        /// <summary>面板物理攻击力(整数)。</summary>
        public int PhysicalAttack { get; set; }

        /// <summary>面板魔法攻击力(整数)。</summary>
        public int MagicalAttack { get; set; }

        /// <summary>物理附加基础值(武器等装备的物理攻击力;[weapon damage apply] 为真时加到基础伤害上)。</summary>
        public int PhysicalBonusBase { get; set; }

        /// <summary>魔法附加基础值(装备的魔法攻击力)。</summary>
        public int MagicalBonusBase { get; set; }

        /// <summary>物理暴击属性(百分点;暴击率 = 3 + 攻击信息的加成 + 它 − 防御方的暴击抗性)。</summary>
        public float PhysicalCritical { get; set; }

        /// <summary>魔法暴击属性(百分点)。</summary>
        public float MagicalCritical { get; set; }

        /// <summary>第 <paramref name="element"/> 号元素(0 火、1 水、2 暗、3 光)的元素攻击;防御方的抗性要减去它。</summary>
        public int ElementAttack(int element)
        {
            return _elementAttack[CheckElement(element)];
        }

        public void SetElementAttack(int element, int value)
        {
            _elementAttack[CheckElement(element)] = value;
        }

        internal static int CheckElement(int element)
        {
            if ((uint)element > 3u)
            {
                throw new ArgumentOutOfRangeException(nameof(element));
            }
            return element;
        }
    }

    /// <summary>
    /// 防御方的面板(证据 E-110)。怪物的数取自 <see cref="MonsterStats"/>(整数 × 紧跟其后的浮点倍率,向零取整,与角色属性记录同一种存法),
    /// 见 <see cref="FromMonster"/>。
    /// </summary>
    public sealed class DefenderPanel
    {
        private readonly int[] _elementResist = new int[4];

        /// <summary>对象种类码(怪物 0x211)。防御方是"活动对象"才套用防御项。</summary>
        public uint ObjectType { get; set; } = DamageObjectTypes.Monster;

        /// <summary>等级:额外命中加成的判断用(等级低于 <see cref="HitDamage.BonusHitLevelLimit"/> 才有)。</summary>
        public int Level { get; set; } = 1;

        public int PhysicalDefense { get; set; }

        public int MagicalDefense { get; set; }

        /// <summary>第 <paramref name="element"/> 号元素(0 火、1 水、2 暗、3 光)的元素抗性。</summary>
        public int ElementResist(int element)
        {
            return _elementResist[AttackerPanel.CheckElement(element)];
        }

        public void SetElementResist(int element, int value)
        {
            _elementResist[AttackerPanel.CheckElement(element)] = value;
        }

        /// <summary>
        /// 怪物当作防御方:防御力与元素抗性是 <see cref="MonsterStats"/> 里"整数 + 浮点倍率"的乘积向零取整
        /// (防御力下限 −1500,与客户端读属性记录时的夹取一致)。
        /// </summary>
        public static DefenderPanel FromMonster(MonsterStats stats, int level)
        {
            if (stats == null)
            {
                throw new ArgumentNullException(nameof(stats));
            }
            var panel = new DefenderPanel
            {
                Level = level < 1 ? 1 : level,
                PhysicalDefense = Clamp(Product(stats, MonsterStats.IxPhysicalDefense)),
                MagicalDefense = Clamp(Product(stats, MonsterStats.IxMagicalDefense)),
            };
            for (int i = 0; i < 4; i++)
            {
                // 元素抗性的整数在 0x0E 起,倍率在 0x12 起(中间隔着四个整数的距离)
                panel.SetElementResist(i, (int)((float)stats.Raw[MonsterStats.IxElementResistance + i] * stats.F(MonsterStats.IxElementResistance + 4 + i)));
            }
            return panel;
        }

        private static int Product(MonsterStats stats, int index)
        {
            return (int)((float)stats.Raw[index] * stats.F(index + 1));
        }

        private static int Clamp(int value)
        {
            return value < -1500 ? -1500 : value;
        }
    }

    /// <summary>
    /// 命中时的伤害输入填充与伤害计算(证据 E-110):把 <see cref="HitInput"/>(攻击信息)、<see cref="AttackerPanel"/>、<see cref="DefenderPanel"/>
    /// 按客户端命中时的填法填进 <see cref="DamageVariables"/>,再交给原版公式 <see cref="DamageFormula"/>(E-049)。
    /// 填法来自客户端的填充函数(<c>0x0087b870</c>)的逐项阅读;客户端初始化给的默认值不是中性的,这里把没有来源的项填成中性值。
    /// 没做:召唤物路径、PVP/突击战模块、攻击者身上的各类附加倍率(额外倍率、固定加成、尾部各项)与防御方的减伤率、穿透、百分比防御
    /// (这些在怪物与默认角色身上取默认值)、防御方是角色时的类型倍率表。
    /// </summary>
    public static class HitDamage
    {
        /// <summary>防御方等级低于这个数时,换了攻击者的一击算"额外命中"(伤害 × 1.1;证据 E-110)。</summary>
        public const int BonusHitLevelLimit = 50;

        /// <summary>
        /// 额外命中的条件:防御方等级低于 <see cref="BonusHitLevelLimit"/>,且这一击的攻击者(所有者)不是防御方上一次挨打时记下的那个。
        /// 防御方每次挨打之后记下攻击者(客户端的"受击后"槽 <c>+0x2e0</c> 写防御方 <c>+0x5f4</c>,对象初始化时清零),
        /// 所以一个怪物被同一个主角打,只有第一下算额外命中。
        /// </summary>
        public static bool BonusHitApplies(int defenderLevel, bool sameAttackerAsLast)
        {
            return defenderLevel < BonusHitLevelLimit && !sameAttackerAsLast;
        }

        /// <summary>
        /// 填好一次命中的 <see cref="DamageVariables"/>。<paramref name="bonusHit"/> 由调用方按 <see cref="BonusHitApplies"/> 给出,
        /// <paramref name="seed"/> 是这一击的随机种子(公式在局部副本上推进它)。
        /// </summary>
        public static DamageVariables Fill(HitInput input, AttackerPanel attacker, DefenderPanel defender, bool bonusHit, uint seed)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }
            if (attacker == null)
            {
                throw new ArgumentNullException(nameof(attacker));
            }
            if (defender == null)
            {
                throw new ArgumentNullException(nameof(defender));
            }
            var p = new DamageVariables();

            // 来自攻击信息的项
            p.Base = input.Base;
            p.UseBonusBase = input.UseBonusBase;
            p.Kind = input.Kind;
            p.DamagePercent = input.DamagePercent;
            for (int i = 0; i < 4; i++)
            {
                p.SetElementFlag(i, input.HasElement(i));
            }
            p.IgnoreDefense = input.IgnoreDefense;
            p.FlatBonus = input.AbsoluteDamage;
            p.CriticalRate = input.CriticalHit;
            p.SetConditionalPercent(0, input.HumanRate);
            p.SetConditionalPercent(1, input.FortRate);
            p.SetConditionalPercent(2, input.MonsterRate);
            // 三个开关:防御方身上带 0x14、0x15 号状态记号时前两个为真,三个记号都不带时第三个为真(客户端按防御方的记号表判断,
            // 记号的含义没读通);没有特殊状态的普通怪物只有第三个为真,按 [monster damage rate] 缩放
            p.SetConditionalFlag(0, false);
            p.SetConditionalFlag(1, false);
            p.SetConditionalFlag(2, true);

            // 来自攻击者面板的项
            p.PhysicalAttack = attacker.PhysicalAttack;
            p.MagicalAttack = attacker.MagicalAttack;
            p.BonusBasePhysical = attacker.PhysicalBonusBase;
            p.BonusBaseMagical = attacker.MagicalBonusBase;
            p.CriticalStatPhysical = attacker.PhysicalCritical;
            p.CriticalStatMagical = attacker.MagicalCritical;
            p.Level = (sbyte)Math.Max(1, Math.Min(sbyte.MaxValue, attacker.Level));
            p.LevelAlt = (sbyte)Math.Max(1, Math.Min(sbyte.MaxValue, attacker.SecondLevel));
            p.AttackerType = attacker.ObjectType;
            p.DamagerType = attacker.ObjectType;

            // 来自防御方面板的项:防御力;元素项 = 防御方的抗性 − 攻击者的元素攻击(只填攻击带的元素,公式也只读这些)
            p.PhysicalDefense = defender.PhysicalDefense;
            p.MagicalDefense = defender.MagicalDefense;
            for (int i = 0; i < 4; i++)
            {
                if (input.HasElement(i))
                {
                    p.SetElementResist(i, defender.ElementResist(i) - attacker.ElementAttack(i));
                }
            }
            p.DefenderType = defender.ObjectType;
            p.DefenderOwnerType = defender.ObjectType;

            // 客户端初始化的默认值不是中性的,又没有来源的项:填成"没有加成"的中性值
            p.PhysicalPercent = 0.0f;
            p.MagicalPercent = 0.0f;
            p.ExtraRate = 0.0f;
            p.FinalPercentA = 100.0f;
            p.PowerPercent = 100;
            p.PhysicalDefensePercent = 0;
            p.MagicalDefensePercent = 0;
            p.SkillCriticalRate = 0.0f;
            p.SkillCriticalStatPhysical = 0.0f;
            p.SkillCriticalStatMagical = 0.0f;
            p.CreatureCriticalStat = 0.0f;

            // 额外命中(填充函数末尾:防御方等级低于 50 且攻击者换了人;计数加一、封顶 4)
            p.BonusHit = bonusHit;
            p.HitCounter = bonusHit ? (byte)2 : (byte)1;
            p.Seed = seed;
            return p;
        }

        /// <summary>
        /// 一击的伤害(至少 1)。<paramref name="critical"/> 返回这一击是否暴击(伤害数字用另一套图)。
        /// </summary>
        public static int Roll(HitInput input, AttackerPanel attacker, DefenderPanel defender, bool bonusHit, DamageConstants constants, uint seed,
            out bool critical)
        {
            if (constants == null)
            {
                throw new ArgumentNullException(nameof(constants));
            }
            DamageVariables p = Fill(input, attacker, defender, bonusHit, seed);
            int damage = DamageFormula.Compute(p, constants);
            critical = p.IsCritical;
            return Math.Max(1, damage);
        }
    }
}
