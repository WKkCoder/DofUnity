using System;
using System.Collections.Generic;
using System.Text;
using Dnf.Data;

// 证据(全部是对本机数据的观察与独立 Python 实现的对拍,不是客户端读码):
//  · 属性表的标签 = 服务端 importEquipmentParameterInfo(0x089a75ea)读成纯数值的标签 ∩ 全库装备脚本里深度 0 纯数值的标签,
//    去掉价格、冷却、概率、技能附加体一类;外加服务端不读、只有客户端用的生命魔法类(.chr 里同名)。被排除的标签清单是本机普查的输出,不入库。
//  · 深度 0 的属性行在全库 63,776 件里没有一行带非数值、空取值、多于两个数;同件重复只有 1 件,重复的取值相同。
//  · 两个数的标签(装备攻击防御与独立攻击)第二个数的含义没有查明(推断:另一档,强化表的 B 组与它同口径,见 EquipUpgradeEffect 的文件头),
//    这里原样留着,汇总时同样相加,不参与任何计算。
namespace Dnf.Game
{
    /// <summary>
    /// 装备属性表里的一项:每一项对应 .equ 里一个深度 0 的数值标签(<see cref="EquipStats.TagOf"/>)。
    /// 成员按标签字面命名,不加猜测的含义;次序就是标签表的次序(<see cref="EquipStats.StatCount"/> 与测试核对了成员数)。
    /// </summary>
    public enum EquipStat
    {
        // —— 与 .chr 里九项属性同名(StatBlock;.equ 里没有 [strength] 一类的四维标签,同名的物攻魔攻物防魔防很可能就是它们)——
        HpMax = 0,
        MpMax,
        PhysicalAttack,
        PhysicalDefense,
        MagicalAttack,
        MagicalDefense,
        InventoryLimit,
        MpRegenSpeed,
        HitRecovery,

        // —— 装备自带的攻击与防御(脚本里写两个数,第二个的含义没有查明)——
        EquipmentPhysicalAttack,
        EquipmentMagicalAttack,
        EquipmentPhysicalDefense,
        EquipmentMagicalDefense,
        SeparateAttack,

        // —— 速度、暴击、硬直等 ——
        AttackSpeed,
        CastSpeed,
        MoveSpeed,
        JumpPower,
        Stuck,
        StuckResistance,
        PhysicalCriticalHit,
        MagicalCriticalHit,
        HpRegenSpeed,
        AntiEvil,

        // —— 与 .chr 属性同名族的比率(只汇总,不折进属性块)——
        MpMaxRate,
        MpRegenSpeedRate,

        // —— 元素强化与抗性 ——
        FireAttack,
        WaterAttack,
        DarkAttack,
        LightAttack,
        AllElementalAttack,
        FireResistance,
        WaterResistance,
        DarkResistance,
        LightResistance,
        AllElementalResistance,

        // —— 异常状态抗性 ——
        FreezeResistance,
        PoisonResistance,
        BleedingResistance,
        StunResistance,
        ConfuseResistance,
        CurseResistance,
        LightningResistance,
        SlowResistance,
        BlindResistance,
        BurnResistance,
        SleepResistance,
        StoneResistance,
        HoldResistance,
        PiercingResistance,
        AllActiveStatusResistance,
    }

    /// <summary>属性集里的一项:第一个数(所有计算只看它)与可选的第二个数。</summary>
    public readonly struct EquipStatEntry
    {
        public EquipStatEntry(EquipStat stat, double first, double second, bool hasSecond)
        {
            Stat = stat;
            First = first;
            Second = second;
            HasSecond = hasSecond;
        }

        public EquipStat Stat { get; }

        /// <summary>标签后面的第一个数(整数标记或浮点标记,按数值存)。</summary>
        public double First { get; }

        /// <summary>第二个数;<see cref="HasSecond"/> 为假时是 0。含义没有查明。</summary>
        public double Second { get; }

        public bool HasSecond { get; }

        internal EquipStatEntry Plus(EquipStatEntry other)
        {
            return new EquipStatEntry(Stat, First + other.First, Second + other.Second, HasSecond || other.HasSecond);
        }

        internal bool SameAs(EquipStatEntry other)
        {
            return Stat == other.Stat && First.Equals(other.First) && Second.Equals(other.Second) && HasSecond == other.HasSecond;
        }
    }

    /// <summary>
    /// 一件装备(或几件装备加总)的基础属性:<see cref="EquipStat"/> → 数值,只存有的项(稀疏、按项次序排好)。不可变。
    /// 读法见 <see cref="Read"/>:只认深度 0 的标签(块里的同名标签属于套装、等级段等,不是装备自己的),同名标签后一次覆盖前一次。
    /// <para>
    /// <b>单位与折算(推断)</b>:装备的这些数与 .chr 里同名属性(九项)用同一种显示单位——两边量级可比(本机核对过,不记数值)——
    /// 所以 <see cref="ToStatBlock"/> 把它们直接乘 10 加进"十分之一"单位的属性块。服务端不计算也不核对装备属性,
    /// 客户端把装备属性集加进角色累加记录的函数已经读到(聚合函数 0x00785150,见 out/enhance/NOTES.md),但这条"乘 10"的单位折算没有逐格核对,仍是推断。
    /// </para>
    /// </summary>
    public sealed class EquipStats : IEquatable<EquipStats>
    {
        /// <summary>属性表的项数(= <see cref="EquipStat"/> 的成员数)。</summary>
        public const int StatCount = 51;

        private static readonly string[] Tags =
        {
            "[hp max]", "[mp max]", "[physical attack]", "[physical defense]", "[magical attack]", "[magical defense]", "[inventory limit]",
            "[mp regen speed]", "[hit recovery]",
            "[equipment physical attack]", "[equipment magical attack]", "[equipment physical defense]", "[equipment magical defense]", "[separate attack]",
            "[attack speed]", "[cast speed]", "[move speed]", "[jump power]", "[stuck]", "[stuck resistance]", "[physical critical hit]",
            "[magical critical hit]", "[hp regen speed]", "[anti evil]",
            "[mp max rate]", "[mp regen speed rate]",
            "[fire attack]", "[water attack]", "[dark attack]", "[light attack]", "[all elemental attack]",
            "[fire resistance]", "[water resistance]", "[dark resistance]", "[light resistance]", "[all elemental resistance]",
            "[freeze resistance]", "[poison resistance]", "[bleeding resistance]", "[stun resistance]", "[confuse resistance]", "[curse resistance]",
            "[lightning resistance]", "[slow resistance]", "[blind resistance]", "[burn resistance]", "[sleep resistance]", "[stone resistance]",
            "[hold resistance]", "[piercing resistance]", "[all activestatus resistance]",
        };

        private static readonly Dictionary<string, EquipStat> ByTag = BuildByTag();

        /// <summary>什么属性都没有(不是装备,或装备没写任何属性项)。</summary>
        public static readonly EquipStats Empty = new EquipStats(new EquipStatEntry[0]);

        private readonly EquipStatEntry[] _entries;

        private EquipStats(EquipStatEntry[] entries)
        {
            _entries = entries;
        }

        private static Dictionary<string, EquipStat> BuildByTag()
        {
            var map = new Dictionary<string, EquipStat>(Tags.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Tags.Length; i++)
            {
                map.Add(Tags[i], (EquipStat)i);
            }
            return map;
        }

        /// <summary>一项对应的脚本标签(如 <c>[hp max]</c>);越界返回 null。</summary>
        public static string TagOf(EquipStat stat)
        {
            return (uint)stat < (uint)Tags.Length ? Tags[(int)stat] : null;
        }

        /// <summary>标签(不区分大小写)对应的项;不在表里返回 false。</summary>
        public static bool TryFromTag(string tag, out EquipStat stat)
        {
            if (tag == null)
            {
                stat = default;
                return false;
            }
            return ByTag.TryGetValue(tag, out stat);
        }

        /// <summary>有几项(不是项数上限,见 <see cref="StatCount"/>)。</summary>
        public int Count => _entries.Length;

        public bool IsEmpty => _entries.Length == 0;

        /// <summary>有的项,按 <see cref="EquipStat"/> 的次序。</summary>
        public IReadOnlyList<EquipStatEntry> Entries => _entries;

        private int Find(EquipStat stat)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Stat == stat)
                {
                    return i;
                }
                if (_entries[i].Stat > stat)
                {
                    return -1;
                }
            }
            return -1;
        }

        public bool Has(EquipStat stat) => Find(stat) >= 0;

        /// <summary>这一项的第一个数;没有这一项是 0。</summary>
        public double this[EquipStat stat]
        {
            get
            {
                int at = Find(stat);
                return at < 0 ? 0.0 : _entries[at].First;
            }
        }

        /// <summary>这一项的第二个数(含义没有查明);没有这一项或没有第二个数是 0。</summary>
        public double SecondOf(EquipStat stat)
        {
            int at = Find(stat);
            return at < 0 ? 0.0 : _entries[at].Second;
        }

        public bool HasSecond(EquipStat stat)
        {
            int at = Find(stat);
            return at >= 0 && _entries[at].HasSecond;
        }

        /// <summary>两份属性逐项相加(第一个数与第二个数各自相加);任何一边没有的项当 0。不改动两个操作数。</summary>
        public EquipStats Plus(EquipStats other)
        {
            if (other == null || other._entries.Length == 0)
            {
                return this;
            }
            if (_entries.Length == 0)
            {
                return other;
            }
            var merged = new EquipStatEntry[_entries.Length + other._entries.Length];
            int a = 0;
            int b = 0;
            int n = 0;
            while (a < _entries.Length || b < other._entries.Length)
            {
                if (b >= other._entries.Length || (a < _entries.Length && _entries[a].Stat < other._entries[b].Stat))
                {
                    merged[n++] = _entries[a++];
                }
                else if (a >= _entries.Length || other._entries[b].Stat < _entries[a].Stat)
                {
                    merged[n++] = other._entries[b++];
                }
                else
                {
                    merged[n++] = _entries[a++].Plus(other._entries[b++]);
                }
            }
            Array.Resize(ref merged, n);
            return new EquipStats(merged);
        }

        /// <summary>一批属性逐项相加(空序列得到 <see cref="Empty"/>)。</summary>
        public static EquipStats Sum(IEnumerable<EquipStats> parts)
        {
            if (parts == null)
            {
                throw new ArgumentNullException(nameof(parts));
            }
            EquipStats total = Empty;
            foreach (EquipStats part in parts)
            {
                total = total.Plus(part);
            }
            return total;
        }

        /// <summary>换掉(或加上)一项的第一个数,去掉原来的第二个数;返回新的属性集。</summary>
        public EquipStats With(EquipStat stat, double first)
        {
            return ToBuilder().Set(stat, first).Build();
        }

        /// <summary>换掉(或加上)一项的两个数;返回新的属性集。</summary>
        public EquipStats With(EquipStat stat, double first, double second)
        {
            return ToBuilder().Set(stat, first, second).Build();
        }

        private Builder ToBuilder()
        {
            var builder = new Builder();
            foreach (EquipStatEntry entry in _entries)
            {
                builder.Put(entry);
            }
            return builder;
        }

        /// <summary>
        /// 折成角色属性块(十分之一单位的整数,<see cref="StatBlock"/>),让装备的属性经 <c>HeroProgress</c> 的属性出口进入主角属性。
        /// 数值乘 10 用 <see cref="CharacterGrowth.ScaledRound"/>(单精度乘 10 再四舍五入,服务端读 .chr 属性的同一种换算)。折法:
        /// <list type="bullet">
        /// <item>生命上限、魔法上限、物攻、魔攻、负重上限、魔法恢复速度、受击恢复:同名相加(推断:与 .chr 同名同单位);</item>
        /// <item>物防、魔防:同名相加,<b>再加上</b>装备防御一对的第一个数(<see cref="EquipStat.EquipmentPhysicalDefense"/>、<see cref="EquipStat.EquipmentMagicalDefense"/>)。
        /// 依据:伤害公式读的是防御方的"装备防御"一对,不读普通防御一对(E-160、E-161,那是怪物一侧;主角一侧没有读到,推断同理),
        /// 而主角的防御值目前只有 <c>HeroVitals</c> 里这一个槽,装备防御不折进来就永远不起作用;</item>
        /// <item>其余项(装备攻击与暴击、速度、元素、抗性、比率……)不折,经 <c>HeroProgress.WornStats</c> 取。</item>
        /// </list>
        /// </summary>
        public StatBlock ToStatBlock()
        {
            return new StatBlock(
                Tenths(EquipStat.HpMax),
                Tenths(EquipStat.MpMax),
                Tenths(EquipStat.PhysicalAttack),
                Tenths(EquipStat.PhysicalDefense) + Tenths(EquipStat.EquipmentPhysicalDefense),
                Tenths(EquipStat.MagicalAttack),
                Tenths(EquipStat.MagicalDefense) + Tenths(EquipStat.EquipmentMagicalDefense),
                Tenths(EquipStat.InventoryLimit),
                Tenths(EquipStat.MpRegenSpeed),
                Tenths(EquipStat.HitRecovery));
        }

        private long Tenths(EquipStat stat)
        {
            int at = Find(stat);
            return at < 0 ? 0L : CharacterGrowth.ScaledRound((float)_entries[at].First);
        }

        // ------------------------------------------------------------ 读取

        /// <summary>
        /// 从物品脚本的标记流读基础属性。规则(与 <c>tools/py/equip_stats_ref.py</c> 的独立实现一致):
        /// ① 只认深度 0 的标签(块规则见 <see cref="ScriptBlocks"/>);② 标签在属性表里时,取它后面直到下一个标签的数值标记
        /// (整数、无符号整数、浮点;别的标记跳过),只留前两个;③ 同名标签后一次覆盖前一次;一个数值都没有的标签行不覆盖之前的。
        /// </summary>
        public static EquipStats Read(ScriptToken[] tokens, PvfStringTable strings)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }
            if (strings == null)
            {
                throw new ArgumentNullException(nameof(strings));
            }
            int[] depths = ScriptBlocks.Depths(tokens, strings);
            Builder builder = null;
            bool reading = false;
            EquipStat current = default;
            int taken = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                ScriptToken t = tokens[i];
                if (t.Type == (byte)TokenType.Tag)
                {
                    reading = false;
                    if (depths[i] == 0 && TryFromTag(ScriptBlocks.TagAt(strings, t.Value), out EquipStat stat))
                    {
                        reading = true;
                        current = stat;
                        taken = 0;
                    }
                    continue;
                }
                if (!reading)
                {
                    continue;
                }
                double value;
                if (t.Type == (byte)TokenType.Int)
                {
                    value = t.Value;
                }
                else if (t.Type == (byte)TokenType.UInt)
                {
                    value = (uint)t.Value;
                }
                else if (t.Type == (byte)TokenType.Float)
                {
                    value = t.AsFloat;
                }
                else
                {
                    continue;
                }
                if (builder == null)
                {
                    builder = new Builder();
                }
                if (taken == 0)
                {
                    builder.Set(current, value);
                }
                else if (taken == 1)
                {
                    builder.SetSecond(current, value);
                }
                taken++;
            }
            return builder == null ? Empty : builder.Build();
        }

        // ------------------------------------------------------------ 相等与显示

        public bool Equals(EquipStats other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            if (other == null || other._entries.Length != _entries.Length)
            {
                return false;
            }
            for (int i = 0; i < _entries.Length; i++)
            {
                if (!_entries[i].SameAs(other._entries[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public override bool Equals(object obj) => Equals(obj as EquipStats);

        public override int GetHashCode()
        {
            int hash = 17;
            foreach (EquipStatEntry e in _entries)
            {
                hash = unchecked(hash * 31 + (int)e.Stat);
                hash = unchecked(hash * 31 + e.First.GetHashCode());
                hash = unchecked(hash * 31 + e.Second.GetHashCode());
            }
            return hash;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (EquipStatEntry e in _entries)
            {
                if (sb.Length > 0)
                {
                    sb.Append(';');
                }
                sb.Append(TagOf(e.Stat)).Append('=').Append(e.First.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (e.HasSecond)
                {
                    sb.Append(',').Append(e.Second.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        // ------------------------------------------------------------ 构建器

        /// <summary>逐项累积属性,最后 <see cref="Build"/> 成不可变的 <see cref="EquipStats"/>。同一项再设一次就是覆盖。</summary>
        public sealed class Builder
        {
            private EquipStatEntry[] _items = new EquipStatEntry[8];
            private int _count;

            /// <summary>设一项的第一个数(去掉原来的第二个数)。</summary>
            public Builder Set(EquipStat stat, double first)
            {
                Put(new EquipStatEntry(stat, first, 0.0, false));
                return this;
            }

            /// <summary>设一项的两个数。</summary>
            public Builder Set(EquipStat stat, double first, double second)
            {
                Put(new EquipStatEntry(stat, first, second, true));
                return this;
            }

            internal void SetSecond(EquipStat stat, double second)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_items[i].Stat == stat)
                    {
                        _items[i] = new EquipStatEntry(stat, _items[i].First, second, true);
                        return;
                    }
                }
            }

            internal void Put(EquipStatEntry entry)
            {
                int at = 0;
                while (at < _count && _items[at].Stat < entry.Stat)
                {
                    at++;
                }
                if (at < _count && _items[at].Stat == entry.Stat)
                {
                    _items[at] = entry;
                    return;
                }
                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, _count * 2);
                }
                Array.Copy(_items, at, _items, at + 1, _count - at);
                _items[at] = entry;
                _count++;
            }

            public EquipStats Build()
            {
                if (_count == 0)
                {
                    return Empty;
                }
                var entries = new EquipStatEntry[_count];
                Array.Copy(_items, entries, _count);
                return new EquipStats(entries);
            }
        }
    }
}
