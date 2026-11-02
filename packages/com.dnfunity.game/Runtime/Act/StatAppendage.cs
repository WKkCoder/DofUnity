using System;
using System.Collections.Generic;

// 证据(E-161,静态读码,非 REA):.act 的 [APPENDAGE] 的加载块在 .act 加载大函数(0x01333530)的 0x013406f6 起,行为码 0x84([NO BUFF APPENDAGE] 是 0xd4,
// 同一块);[DELETE APPENDAGE] 的加载块在 0x01341cea 起,行为码 0xad;属性标签到类型号的字典是 0x0125aa00(只用这一个函数);
// 执行块在 0x010b56c0 的行为码 0x84 与 0xad 分支;一项属性改变写进属性记录的函数是 0x00984260(分支号 = 类型号);"一项是否相同"的比较是 0x00982c90。
namespace Dnf.Game.Act
{
    /// <summary>
    /// 属性改变的类型号:脚本头文件里 CHANGE_STATUS_TYPE_* 的取值(0 到 63;客户端把附加物写进属性记录的函数 0x00984260 按同一套号分支),
    /// 以及 .act 的 [APPENDAGE] 里的属性标签到类型号的字典(客户端 0x0125aa00 的逐项阅读,E-161)。
    /// </summary>
    public static class StatChangeTypes
    {
        public const int PhysicalAttack = 0;
        public const int MagicalAttack = 1;
        public const int MagicalDefense = 2;
        public const int PhysicalDefense = 3;
        public const int EquipmentPhysicalAttack = 4;
        public const int EquipmentPhysicalDefense = 5;
        public const int PhysicalAttackBonus = 6;
        public const int MagicalAttackBonus = 7;
        public const int JumpPower = 8;
        public const int AttackSpeed = 10;
        public const int MoveSpeed = 11;
        public const int CastSpeed = 12;
        public const int HpMax = 13;
        public const int HpRegenRate = 14;
        public const int PhysicalCriticalHitRate = 15;
        public const int MagicalCriticalHitRate = 16;
        public const int PhysicalBackAttackCritical = 17;
        public const int MagicalBackAttackCritical = 18;
        public const int ElementToleranceFire = 20;
        public const int ElementToleranceWater = 21;
        public const int ElementToleranceDark = 22;
        public const int ElementToleranceLight = 23;
        public const int ElementToleranceAll = 24;
        public const int ActiveStatusToleranceAll = 26;

        /// <summary>[stuck](脚本头文件的 ACTIVEPROPERTY_STUCK):写进硬直抗性那一格。</summary>
        public const int StuckResistance = 27;

        public const int MpRegenRate = 29;

        /// <summary>[hit recovery] 标签对应的号(脚本头文件注释"受击时的硬直"):写进属性记录里一个缺省 1.0 的浮点格(下标 110),眼下没有读它的地方。</summary>
        public const int HitStagger = 30;

        public const int EquipmentMagicalAttack = 31;
        public const int EquipmentMagicalDefense = 32;

        /// <summary>[good hit]:脚本头文件里的 STUCK("我打别人时的命中硬直")。</summary>
        public const int GoodHit = 33;

        public const int ElementAttackFire = 42;
        public const int ElementAttackWater = 43;
        public const int ElementAttackDark = 44;
        public const int ElementAttackLight = 45;
        public const int ElementAttackAll = 46;
        public const int MpMax = 48;
        public const int SeparateAttack = 53;
        public const int AntiEvil = 55;
        public const int PhysicalAbsoluteDamage = 56;
        public const int MagicalAbsoluteDamage = 60;

        /// <summary>[physical absolute defense]:写进属性记录下标 96 的浮点格,客户端的伤害填充函数把它读成公式里的"穿透"项(E-161)。</summary>
        public const int PhysicalAbsoluteDefense = 61;

        /// <summary>[magical absolute defense]:同上,下标 99,魔法一侧的"穿透"项。</summary>
        public const int MagicalAbsoluteDefense = 62;

        public const int VillageMoveSpeed = 63;

        /// <summary>字典里没有这个标签:客户端也造一项,类型 −1,执行时什么都不写。</summary>
        public const int Unknown = -1;

        private static readonly Dictionary<string, int> Tags = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "[physical attack]", PhysicalAttack },
            { "[magical attack]", MagicalAttack },
            { "[magical defense]", MagicalDefense },
            { "[physical defense]", PhysicalDefense },
            { "[equipment physical attack]", EquipmentPhysicalAttack },
            { "[equipment physical defense]", EquipmentPhysicalDefense },
            { "[physical attack bonus]", PhysicalAttackBonus },
            { "[magical attack bonus]", MagicalAttackBonus },
            { "[jump power]", JumpPower },
            { "[attack speed]", AttackSpeed },
            { "[move speed]", MoveSpeed },
            { "[cast speed]", CastSpeed },
            { "[hp max]", HpMax },
            { "[hp regen rate]", HpRegenRate },
            { "[physical critical hit rate]", PhysicalCriticalHitRate },
            { "[magical critical hit rate]", MagicalCriticalHitRate },
            { "[physical back attack critical rate]", PhysicalBackAttackCritical },
            { "[magical back attack critical rate]", MagicalBackAttackCritical },
            { "[fire element]", ElementToleranceFire },
            { "[water element]", ElementToleranceWater },
            { "[dark element]", ElementToleranceDark },
            { "[light element]", ElementToleranceLight },
            { "[all element]", ElementToleranceAll },
            { "[all active status resistance]", ActiveStatusToleranceAll },
            { "[stuck]", StuckResistance },
            { "[mp regen rate]", MpRegenRate },
            { "[hit recovery]", HitStagger },
            { "[equipment magical attack]", EquipmentMagicalAttack },
            { "[equipment magical defense]", EquipmentMagicalDefense },
            { "[good hit]", GoodHit },
            { "[fire element attack]", ElementAttackFire },
            { "[water element attack]", ElementAttackWater },
            { "[dark element attack]", ElementAttackDark },
            { "[light element attack]", ElementAttackLight },
            { "[all element attack]", ElementAttackAll },
            { "[mp max]", MpMax },
            { "[separate attack]", SeparateAttack },
            { "[antievil]", AntiEvil },
            { "[physical absolute damage]", PhysicalAbsoluteDamage },
            { "[magical absolute damage]", MagicalAbsoluteDamage },
            { "[physical absolute defense]", PhysicalAbsoluteDefense },
            { "[magical absolute defense]", MagicalAbsoluteDefense },
            { "[village moving speed]", VillageMoveSpeed },
        };

        /// <summary>
        /// .act 属性标签对应的类型号(客户端 0x0125aa00 的字典,逐项读出,E-161);不认得的标签返回 <see cref="Unknown"/>(该函数的兜底值 −1)。
        /// </summary>
        public static int FromTag(string tag)
        {
            return tag != null && Tags.TryGetValue(tag, out int type) ? type : Unknown;
        }

        /// <summary>类型号对应的标签(<see cref="FromTag"/> 的反查,命令行汇总打印用);没有的返回 "#号"。</summary>
        public static string TagOf(int type)
        {
            foreach (KeyValuePair<string, int> kv in Tags)
            {
                if (kv.Value == type)
                {
                    return kv.Key;
                }
            }
            return "#" + type;
        }

        /// <summary>攻击速度、移动速度、施法速度:这三种的数值进的是倍率(一个浮点数),不是整数基数(E-161)。</summary>
        public static bool IsSpeed(int type)
        {
            return type == AttackSpeed || type == MoveSpeed || type == CastSpeed;
        }
    }

    /// <summary>
    /// 一项属性改变:类型、按比例还是按点数、数值(整数)。.act 的写法 <c>[属性标签] [%]|[+]|[-] 数值</c>:操作符是 [%] 才算比例,
    /// 别的(包括 [+] 与 [-])都按点数,数值照写的符号用,[-] 不取反(客户端加载块只拿操作符与 [%] 比一次,E-161)。
    /// 两项"相同"当且仅当类型、比例标志与数值都相同(客户端 0x00982c90 的比较)。
    /// </summary>
    public readonly struct StatChange : IEquatable<StatChange>
    {
        public StatChange(int type, bool percent, int value)
        {
            Type = type;
            Percent = percent;
            Value = value;
        }

        public int Type { get; }

        /// <summary>按比例([%])。客户端的比例标志只分零与非零:零是点数。</summary>
        public bool Percent { get; }

        public int Value { get; }

        public bool Equals(StatChange other)
        {
            return Type == other.Type && Percent == other.Percent && Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is StatChange other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Type * 397 ^ Value;
                return Percent ? ~h : h;
            }
        }

        public override string ToString()
        {
            return "type " + Type + (Percent ? " % " : " + ") + Value;
        }
    }

    /// <summary>
    /// 属性改变写进怪物数值块 <see cref="MonsterStats"/> 的规则(客户端 0x00984260,E-161)。客户端的属性记录与服务端的"主动对象静态信息"同一个布局,
    /// 只是每个 4 字节槽放大成 12 字节的受保护容器(客户端容器偏移 ÷ 12 = <see cref="MonsterStats"/> 的下标),所以可以直接在数值块上做:
    /// 点数加到整数基数上;比例把"数值 × 0.01"加到整数后面的浮点倍率上(倍率缺省 1.0,所以 +50% 是 ×1.5,多项相加而不是相乘)。
    /// 例外:攻击速度、移动速度、施法速度的点数也进倍率(数值 × 0.001),整数基数不动;[stuck] 与 [hit recovery] 的比例是"在现有倍率上再加百分之几";
    /// 物理与魔法防御(普通一对)写完后基数与倍率都夹到不小于 0;绝对防御两格是浮点,点数直接加、比例 × 0.01;两个攻击"加成"只认比例,值直接加到整数格上。
    /// </summary>
    public static class StatChangeEffects
    {
        /// <summary>比例的单位:数值 × 0.01(客户端常量 0x01565cc0,启动时还写进了全局容器 0x01a6fbdc)。</summary>
        public const float PercentUnit = 0.01f;

        /// <summary>速度类点数的单位:数值 × 0.001(客户端全局容器 0x01a6fbe8)。</summary>
        public const float SpeedFlatUnit = 0.001f;

        /// <summary>整数基数的下标与它的倍率下标(类型 → 一对;客户端 0x00984260 每个分支里 LEA 的容器偏移 ÷ 12)。</summary>
        private static readonly Dictionary<int, (int Base, int Rate)> Pairs = new Dictionary<int, (int, int)>
        {
            { StatChangeTypes.PhysicalAttack, (MonsterStats.IxPhysicalAttack, MonsterStats.IxPhysicalAttack + 1) },
            { StatChangeTypes.MagicalAttack, (MonsterStats.IxMagicalAttack, MonsterStats.IxMagicalAttack + 1) },
            { StatChangeTypes.MagicalDefense, (MonsterStats.IxMagicalDefense, MonsterStats.IxMagicalDefense + 1) },
            { StatChangeTypes.PhysicalDefense, (MonsterStats.IxPhysicalDefense, MonsterStats.IxPhysicalDefense + 1) },
            { StatChangeTypes.EquipmentPhysicalAttack, (MonsterStats.IxEquipPhysicalAttack, MonsterStats.IxEquipPhysicalAttack + 1) },
            { StatChangeTypes.EquipmentPhysicalDefense, (MonsterStats.IxEquipPhysicalDefense, MonsterStats.IxEquipPhysicalDefense + 1) },
            { StatChangeTypes.EquipmentMagicalAttack, (MonsterStats.IxEquipMagicalAttack, MonsterStats.IxEquipMagicalAttack + 1) },
            { StatChangeTypes.EquipmentMagicalDefense, (MonsterStats.IxEquipMagicalDefense, MonsterStats.IxEquipMagicalDefense + 1) },
            { StatChangeTypes.HpMax, (MonsterStats.IxHpBase, MonsterStats.IxHpRate) },
            { StatChangeTypes.MpMax, (MonsterStats.IxMpBase, MonsterStats.IxMpRate) },
            { StatChangeTypes.HpRegenRate, (MonsterStats.IxHpRegen, MonsterStats.IxHpRegen + 1) },
            { StatChangeTypes.MpRegenRate, (MonsterStats.IxMpRegen, MonsterStats.IxMpRegen + 1) },
            { StatChangeTypes.JumpPower, (MonsterStats.IxJumpPower, MonsterStats.IxJumpPower + 1) },
        };

        /// <summary>速度类的倍率下标:攻击速度、移动速度、施法速度的整数基数在 +0,倍率在其后一格。</summary>
        private static int SpeedRateIndex(int type)
        {
            switch (type)
            {
                case StatChangeTypes.AttackSpeed:
                    return MonsterStats.IxAttackSpeed + 1;
                case StatChangeTypes.MoveSpeed:
                    return MonsterStats.IxMoveSpeed + 1;
                case StatChangeTypes.CastSpeed:
                    return MonsterStats.IxCastSpeed + 1;
                default:
                    return -1;
            }
        }

        /// <summary>速度类一项对倍率的增量(浮点,与客户端同样先乘后加)。</summary>
        public static float SpeedDelta(StatChange change)
        {
            return change.Percent ? (float)change.Value * PercentUnit : (float)change.Value * SpeedFlatUnit;
        }

        /// <summary>
        /// 把一项属性改变写进 <paramref name="stats"/>;这一类型在数值块里没有位置、或者这种写法什么都不写(暴击率、异常状态抗性、
        /// 绝对伤害、未知类型等,眼下没有读它们的地方,没有移植)返回 false。
        /// </summary>
        public static bool Apply(MonsterStats stats, StatChange change)
        {
            if (stats == null)
            {
                throw new ArgumentNullException(nameof(stats));
            }
            int type = change.Type;
            int speed = SpeedRateIndex(type);
            if (speed >= 0)
            {
                stats.SetF(speed, stats.F(speed) + SpeedDelta(change));
                return true;
            }
            if (Pairs.TryGetValue(type, out (int Base, int Rate) pair))
            {
                AddToPair(stats, pair.Base, pair.Rate, change);
                if (type == StatChangeTypes.MagicalDefense || type == StatChangeTypes.PhysicalDefense)
                {
                    // 普通防御一对写完夹到不小于 0:先倍率、再基数(分支里的次序;客户端对"装备防御"一对不夹)
                    if (stats.F(pair.Rate) < 0.0f)
                    {
                        stats.SetF(pair.Rate, 0.0f);
                    }
                    if (stats.Raw[pair.Base] < 0)
                    {
                        stats.Raw[pair.Base] = 0;
                    }
                }
                return true;
            }
            switch (type)
            {
                case StatChangeTypes.ElementToleranceFire:
                case StatChangeTypes.ElementToleranceWater:
                case StatChangeTypes.ElementToleranceDark:
                case StatChangeTypes.ElementToleranceLight:
                {
                    int k = type - StatChangeTypes.ElementToleranceFire;
                    AddToPair(stats, MonsterStats.IxElementResistance + k, MonsterStats.IxElementResistance + 4 + k, change);
                    return true;
                }
                case StatChangeTypes.ElementToleranceAll:
                    for (int k = 0; k < 4; k++)
                    {
                        AddToPair(stats, MonsterStats.IxElementResistance + k, MonsterStats.IxElementResistance + 4 + k, change);
                    }
                    return true;
                case StatChangeTypes.ElementAttackFire:
                case StatChangeTypes.ElementAttackWater:
                case StatChangeTypes.ElementAttackDark:
                case StatChangeTypes.ElementAttackLight:
                {
                    int k = type - StatChangeTypes.ElementAttackFire;
                    AddToPair(stats, MonsterStats.IxElementAttack + k, MonsterStats.IxElementAttack + 4 + k, change);
                    return true;
                }
                case StatChangeTypes.ElementAttackAll:
                    for (int k = 0; k < 4; k++)
                    {
                        AddToPair(stats, MonsterStats.IxElementAttack + k, MonsterStats.IxElementAttack + 4 + k, change);
                    }
                    return true;
                case StatChangeTypes.StuckResistance:
                    if (change.Percent)
                    {
                        // 比例:在现有倍率上再加"倍率 × 数值 × 0.01"(客户端读一次现值再乘)
                        float r = stats.F(MonsterStats.IxStuckResistanceRate);
                        stats.SetF(MonsterStats.IxStuckResistanceRate, r + r * (float)change.Value * PercentUnit);
                    }
                    else
                    {
                        stats.Raw[MonsterStats.IxStuckResistance] = unchecked(stats.Raw[MonsterStats.IxStuckResistance] + change.Value);
                    }
                    return true;
                case StatChangeTypes.HitStagger:
                    if (change.Percent)
                    {
                        float r = stats.F(MonsterStats.IxHitStagger);
                        stats.SetF(MonsterStats.IxHitStagger, r + r * (float)change.Value * PercentUnit);
                    }
                    else
                    {
                        stats.SetF(MonsterStats.IxHitStagger, stats.F(MonsterStats.IxHitStagger) + (float)change.Value);
                    }
                    return true;
                case StatChangeTypes.PhysicalAbsoluteDefense:
                    AddToFloat(stats, MonsterStats.IxPhysicalAbsoluteDefense, change);
                    return true;
                case StatChangeTypes.MagicalAbsoluteDefense:
                    AddToFloat(stats, MonsterStats.IxMagicalAbsoluteDefense, change);
                    return true;
                case StatChangeTypes.PhysicalAttackBonus:
                    return AddBonus(stats, MonsterStats.IxPhysicalAttackBonus, change);
                case StatChangeTypes.MagicalAttackBonus:
                    return AddBonus(stats, MonsterStats.IxMagicalAttackBonus, change);
                default:
                    return false;
            }
        }

        /// <summary>
        /// 怪物这一侧有没有读这类改变的地方(M63 接上的:生命上限、移动与攻击速度、攻击力两对、装备防御两对、四个元素抗性、绝对防御;E-161)。
        /// 别的类型只记在附加物集合里、不改任何数——普通防御一对客户端的伤害公式不读(读的是装备防御一对),所以也不算。
        /// </summary>
        public static bool MonsterReads(int type)
        {
            switch (type)
            {
                case StatChangeTypes.PhysicalAttack:
                case StatChangeTypes.MagicalAttack:
                case StatChangeTypes.EquipmentPhysicalAttack:
                case StatChangeTypes.EquipmentMagicalAttack:
                case StatChangeTypes.EquipmentPhysicalDefense:
                case StatChangeTypes.EquipmentMagicalDefense:
                case StatChangeTypes.HpMax:
                case StatChangeTypes.AttackSpeed:
                case StatChangeTypes.MoveSpeed:
                case StatChangeTypes.ElementToleranceFire:
                case StatChangeTypes.ElementToleranceWater:
                case StatChangeTypes.ElementToleranceDark:
                case StatChangeTypes.ElementToleranceLight:
                case StatChangeTypes.ElementToleranceAll:
                case StatChangeTypes.PhysicalAbsoluteDefense:
                case StatChangeTypes.MagicalAbsoluteDefense:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>主角这一侧有没有读这类改变的地方:只有移动速度与攻击速度(别的主角属性现在没有对应的计算,E-161)。</summary>
        public static bool HeroReads(int type)
        {
            return type == StatChangeTypes.MoveSpeed || type == StatChangeTypes.AttackSpeed;
        }

        private static void AddToPair(MonsterStats stats, int baseIndex, int rateIndex, StatChange change)
        {
            if (change.Percent)
            {
                stats.SetF(rateIndex, stats.F(rateIndex) + (float)change.Value * PercentUnit);
            }
            else
            {
                stats.Raw[baseIndex] = unchecked(stats.Raw[baseIndex] + change.Value);
            }
        }

        /// <summary>只有一个浮点格的类型:点数直接加数值,比例加 数值 × 0.01。</summary>
        private static void AddToFloat(MonsterStats stats, int index, StatChange change)
        {
            float add = change.Percent ? (float)change.Value * PercentUnit : (float)change.Value;
            stats.SetF(index, stats.F(index) + add);
        }

        /// <summary>两个攻击"加成"格:只在写法是比例时才加(数值原样加到整数上),点数写法什么都不做(客户端分支里只有比例这一条路)。</summary>
        private static bool AddBonus(MonsterStats stats, int index, StatChange change)
        {
            if (!change.Percent)
            {
                return false;
            }
            stats.Raw[index] = unchecked(stats.Raw[index] + change.Value);
            return true;
        }
    }

    /// <summary>
    /// 一个挂在对象身上的属性改变附加物(客户端一个 <c>[APPENDAGE]</c> 里的每一项各造一个,E-161):来源(行为拥有者)、一项属性改变、
    /// 持续毫秒(不大于 0 是没有时限,到被 [DELETE APPENDAGE] 删掉或对象消失为止)。
    /// </summary>
    public sealed class StatAppendage
    {
        public StatAppendage(IActEntity source, StatChange change, int durationMs)
        {
            Source = source;
            Change = change;
            DurationMs = durationMs;
            RemainingMs = durationMs;
        }

        public IActEntity Source { get; }

        public StatChange Change { get; }

        public int DurationMs { get; }

        /// <summary>还剩多少毫秒;没有时限的不用它。</summary>
        public double RemainingMs { get; internal set; }

        public bool Unlimited => DurationMs <= 0;
    }

    /// <summary>
    /// 一个对象身上所有属性改变附加物(客户端对象的附加物表里属性改变那一类,E-161)。
    /// 加:同一项(类型、比例标志、数值都相同)已经有了就先去掉旧的、再挂新的(客户端执行块在造新附加物前,把对象身上含这一项的旧附加物全部作废,
    /// 不看来源),所以相同的项不叠加、重新计时,不同的项相加;删:只删来源是行为拥有者、且含这一项的附加物(客户端对 [DELETE APPENDAGE] 的每一项各扫一遍);
    /// 时限到了自己消失。数值块上的效果由 <see cref="Apply"/> 从基础数值块重新算出(加减可交换,不用记"撤销")。
    /// </summary>
    public sealed class StatAppendageSet
    {
        private readonly List<StatAppendage> _items = new List<StatAppendage>();

        public int Count => _items.Count;

        public IReadOnlyList<StatAppendage> Items => _items;

        /// <summary>内容每变一次加一(挂上、顶替、删除、到期都算);调用方用它判断要不要重算。</summary>
        public int Version { get; private set; }

        /// <summary>挂一项;返回被顶替掉的旧附加物个数。</summary>
        public int Add(IActEntity source, StatChange change, int durationMs)
        {
            int replaced = _items.RemoveAll(a => a.Change.Equals(change));
            _items.Add(new StatAppendage(source, change, durationMs));
            Version++;
            return replaced;
        }

        /// <summary>删掉来源是 <paramref name="source"/>、且这一项与 <paramref name="change"/> 相同的附加物;返回删掉的个数。</summary>
        public int Remove(IActEntity source, StatChange change)
        {
            int removed = _items.RemoveAll(a => ReferenceEquals(a.Source, source) && a.Change.Equals(change));
            if (removed > 0)
            {
                Version++;
            }
            return removed;
        }

        public void Clear()
        {
            if (_items.Count > 0)
            {
                _items.Clear();
                Version++;
            }
        }

        /// <summary>过了 <paramref name="deltaMs"/> 毫秒:有时限的附加物倒计时,到期的去掉。返回有几个附加物到期。</summary>
        public int Advance(double deltaMs)
        {
            int expired = 0;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                StatAppendage a = _items[i];
                if (a.Unlimited)
                {
                    continue;
                }
                a.RemainingMs -= deltaMs;
                if (a.RemainingMs <= 0.0)
                {
                    _items.RemoveAt(i);
                    expired++;
                }
            }
            if (expired > 0)
            {
                Version++;
            }
            return expired;
        }

        /// <summary>
        /// 把所有附加物按挂上的先后写进 <paramref name="basis"/> 的一份拷贝并返回(基础数值块不改)。
        /// 三个速度倍率最后夹到不小于 0(客户端夹到配置里的下限,下限的值没有读到,取 0,E-161)。
        /// </summary>
        public MonsterStats Apply(MonsterStats basis)
        {
            if (basis == null)
            {
                throw new ArgumentNullException(nameof(basis));
            }
            MonsterStats stats = basis.Clone();
            foreach (StatAppendage a in _items)
            {
                StatChangeEffects.Apply(stats, a.Change);
            }
            ClampSpeed(stats, MonsterStats.IxAttackSpeed + 1);
            ClampSpeed(stats, MonsterStats.IxMoveSpeed + 1);
            ClampSpeed(stats, MonsterStats.IxCastSpeed + 1);
            return stats;
        }

        /// <summary>
        /// 速度类(<see cref="StatChangeTypes.IsSpeed"/>)现在的倍率:1.0 加上这一类型所有附加物的增量,不小于 0。
        /// 给没有数值块的对象(主角)用,结果与 <see cref="Apply"/> 里同一个倍率一致。
        /// </summary>
        public float SpeedRate(int type)
        {
            float rate = 1.0f;
            foreach (StatAppendage a in _items)
            {
                if (a.Change.Type == type)
                {
                    rate += StatChangeEffects.SpeedDelta(a.Change);
                }
            }
            return rate < 0.0f ? 0.0f : rate;
        }

        private static void ClampSpeed(MonsterStats stats, int rateIndex)
        {
            if (stats.F(rateIndex) < 0.0f)
            {
                stats.SetF(rateIndex, 0.0f);
            }
        }
    }
}
