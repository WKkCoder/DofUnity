using System;
using Dnf.Data;

// 证据:E-111(服务端 ImportCharacterScript 对 .chr 属性块的读法、CCharacter::get_growtype_stat、CCalcCharacStat::CalcStat)。
namespace Dnf.Game
{
    /// <summary>
    /// 一组角色属性(.chr 里 [initial value]、[growtype N]、[awakening N] 各块里的同名标签)。
    /// <b>单位是十分之一</b>:服务端读 .chr 时把每个浮点数乘 10.0(单精度)再四舍五入(远离零)成整数存放(<c>GetRoundInt(10.0f × 值)</c>),
    /// 升级时逐级加整数,所以这里也用"十分之一"的整数加,避免浮点累加误差;取显示值用 <see cref="Units"/>。
    /// 字段与 .chr 标签一一对应:[HP MAX]、[MP MAX]、[physical attack]、[physical defense]、[magical attack]、[magical defense]、
    /// [inventory limit]、[MP regen speed]、[hit recovery]。(服务端里攻防等是 16 位整数、生命魔法是 32 位;按 long 加,不模拟 16 位回绕——
    /// 按真实数据的量级它们不会溢出。)
    /// </summary>
    public readonly struct StatBlock
    {
        public long HpMax { get; }
        public long MpMax { get; }
        public long PhysicalAttack { get; }
        public long PhysicalDefense { get; }
        public long MagicalAttack { get; }
        public long MagicalDefense { get; }
        public long InventoryLimit { get; }
        public long MpRegenSpeed { get; }
        public long HitRecovery { get; }

        public StatBlock(long hpMax, long mpMax, long physicalAttack, long physicalDefense, long magicalAttack, long magicalDefense, long inventoryLimit,
            long mpRegenSpeed, long hitRecovery)
        {
            HpMax = hpMax;
            MpMax = mpMax;
            PhysicalAttack = physicalAttack;
            PhysicalDefense = physicalDefense;
            MagicalAttack = magicalAttack;
            MagicalDefense = magicalDefense;
            InventoryLimit = inventoryLimit;
            MpRegenSpeed = mpRegenSpeed;
            HitRecovery = hitRecovery;
        }

        /// <summary>十分之一单位的整数换成显示值(.chr 里写的那种数)。</summary>
        public static double Units(long tenths) => tenths / 10.0;

        public static StatBlock operator +(StatBlock a, StatBlock b)
        {
            return new StatBlock(a.HpMax + b.HpMax, a.MpMax + b.MpMax, a.PhysicalAttack + b.PhysicalAttack, a.PhysicalDefense + b.PhysicalDefense,
                a.MagicalAttack + b.MagicalAttack, a.MagicalDefense + b.MagicalDefense, a.InventoryLimit + b.InventoryLimit, a.MpRegenSpeed + b.MpRegenSpeed,
                a.HitRecovery + b.HitRecovery);
        }

        /// <summary>改一个标签对应的字段(十分之一单位);标签不是这九项之一返回 false。</summary>
        internal bool TrySet(string tag, long tenths, out StatBlock result)
        {
            result = this;
            switch (tag.ToLowerInvariant())
            {
                case "[hp max]":
                    result = new StatBlock(tenths, MpMax, PhysicalAttack, PhysicalDefense, MagicalAttack, MagicalDefense, InventoryLimit, MpRegenSpeed, HitRecovery);
                    return true;
                case "[mp max]":
                    result = new StatBlock(HpMax, tenths, PhysicalAttack, PhysicalDefense, MagicalAttack, MagicalDefense, InventoryLimit, MpRegenSpeed, HitRecovery);
                    return true;
                case "[physical attack]":
                    result = new StatBlock(HpMax, MpMax, tenths, PhysicalDefense, MagicalAttack, MagicalDefense, InventoryLimit, MpRegenSpeed, HitRecovery);
                    return true;
                case "[physical defense]":
                    result = new StatBlock(HpMax, MpMax, PhysicalAttack, tenths, MagicalAttack, MagicalDefense, InventoryLimit, MpRegenSpeed, HitRecovery);
                    return true;
                case "[magical attack]":
                    result = new StatBlock(HpMax, MpMax, PhysicalAttack, PhysicalDefense, tenths, MagicalDefense, InventoryLimit, MpRegenSpeed, HitRecovery);
                    return true;
                case "[magical defense]":
                    result = new StatBlock(HpMax, MpMax, PhysicalAttack, PhysicalDefense, MagicalAttack, tenths, InventoryLimit, MpRegenSpeed, HitRecovery);
                    return true;
                case "[inventory limit]":
                    result = new StatBlock(HpMax, MpMax, PhysicalAttack, PhysicalDefense, MagicalAttack, MagicalDefense, tenths, MpRegenSpeed, HitRecovery);
                    return true;
                case "[mp regen speed]":
                    result = new StatBlock(HpMax, MpMax, PhysicalAttack, PhysicalDefense, MagicalAttack, MagicalDefense, InventoryLimit, tenths, HitRecovery);
                    return true;
                case "[hit recovery]":
                    result = new StatBlock(HpMax, MpMax, PhysicalAttack, PhysicalDefense, MagicalAttack, MagicalDefense, InventoryLimit, MpRegenSpeed, tenths);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 角色的等级成长(证据 E-111)。.chr 里有一块初始值 [initial value],后面是最多 6 个 [growtype N] 块,
    /// 每个成长类型下还可有 [awakening 1]、[awakening 2] 两个觉醒块;成长块里的数是"每升一级加多少"。
    /// 服务端的算法(CalcStat):等级 N 的属性 = 初始值 + 对 L = 1 到 N - 1 逐级加一块增量,
    /// 增量取:L &lt; 15 用 <b>第一个成长类型(下标 0)</b>,15 ≤ L &lt; 50 用所选成长类型,L ≥ 50 用所选成长类型的觉醒块(觉醒下标 0 就是成长类型本身)。
    /// 没有选过成长类型的新角色成长类型是 0,所以全程用第一个成长类型的块。
    /// 下标约定:<c>[growtype N]</c> 对应下标 N - 1(服务端的 get_growtype_stat 用 "下标 &lt; 6");觉醒下标 1、2 对应 <c>[awakening 1]</c>、<c>[awakening 2]</c>。
    /// 其余的加成(装备、属性点、称号等)服务端在 CalcStat 之后另算,这里没做。
    /// </summary>
    public sealed class CharacterGrowth
    {
        public const int GrowtypeCount = 6;

        /// <summary>每个成长类型下的觉醒块数。</summary>
        public const int AwakeningCount = 2;

        /// <summary>等级低于它的逐级增量固定用成长类型 0 的块。</summary>
        public const int NoviceUntilLevel = 15;

        /// <summary>等级不低于它的逐级增量改用觉醒下标选的块。</summary>
        public const int AwakeningFromLevel = 50;

        private readonly StatBlock[] _growtypes = new StatBlock[GrowtypeCount];
        private readonly bool[] _hasGrowtype = new bool[GrowtypeCount];
        private readonly StatBlock[] _awakenings = new StatBlock[GrowtypeCount * AwakeningCount];

        /// <summary>初始值([initial value] 块,十分之一单位)。</summary>
        public StatBlock Initial { get; private set; }

        /// <summary>有没有读到 [initial value] 块。</summary>
        public bool HasInitial { get; private set; }

        /// <summary>读到了几个成长类型块。</summary>
        public int GrowtypeBlocks
        {
            get
            {
                int n = 0;
                foreach (bool has in _hasGrowtype)
                {
                    if (has)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>某个成长类型(下标 0 到 5)的逐级增量块;没有该块或下标越界是全 0。</summary>
        public StatBlock Growtype(int growtype)
        {
            return (uint)growtype < (uint)GrowtypeCount ? _growtypes[growtype] : default;
        }

        /// <summary>
        /// 从 <paramref name="level"/> 级升到下一级时加的增量块(CalcStat 里循环变量 L = level 时的取法)。
        /// <paramref name="awakening"/> 为 0 取成长类型本身,1、2 取觉醒块;下标越界按 0 处理(服务端越界会得到空指针,没有意义)。
        /// </summary>
        public StatBlock Increment(int level, int growtype, int awakening)
        {
            if ((uint)growtype >= (uint)GrowtypeCount)
            {
                growtype = 0;
            }
            if (level < NoviceUntilLevel)
            {
                return _growtypes[0];
            }
            if (level < AwakeningFromLevel || awakening < 1 || awakening > AwakeningCount)
            {
                return _growtypes[growtype];
            }
            return _awakenings[growtype * AwakeningCount + (awakening - 1)];
        }

        /// <summary>
        /// 等级 <paramref name="level"/> 的属性(CalcStat):初始值加上 L = 1 到 level - 1 每一级的增量块。等级小于 1 按 1 算。
        /// </summary>
        public StatBlock StatsAt(int level, int growtype = 0, int awakening = 0)
        {
            StatBlock total = Initial;
            for (int l = 1; l < level; l++)
            {
                total = total + Increment(l, growtype, awakening);
            }
            return total;
        }

        /// <summary>
        /// 读 .chr 的脚本行:[initial value]、[growtype 1] 到 [growtype 6]、其后的 [awakening 1]、[awakening 2] 各开一个块,
        /// 之后出现的九个属性标签写进当前块(同一块里后出现的覆盖先出现的);在任何块标签之前出现的属性标签忽略。
        /// <paramref name="lines"/> 为 null 返回没有任何数据的成长(全 0)。
        /// </summary>
        public static CharacterGrowth Parse(ScriptLine[] lines)
        {
            var growth = new CharacterGrowth();
            if (lines == null)
            {
                return growth;
            }
            // 当前块:0 = 无,1 = 初始值,2 = 成长类型,3 = 觉醒
            int mode = 0;
            int growtype = -1;
            int awakening = 0;
            StatBlock current = default;
            foreach (ScriptLine line in lines)
            {
                string tag = line.Tag;
                if (tag == null)
                {
                    continue;
                }
                if (line.TagIs("[initial value]"))
                {
                    growth.Commit(mode, growtype, awakening, current);
                    mode = 1;
                    current = default;
                    continue;
                }
                if (TryBlockIndex(tag, "[growtype ", out int g, GrowtypeCount))
                {
                    growth.Commit(mode, growtype, awakening, current);
                    mode = 2;
                    growtype = g;
                    awakening = 0;
                    current = default;
                    continue;
                }
                if (growtype >= 0 && TryBlockIndex(tag, "[awakening ", out int a, AwakeningCount))
                {
                    growth.Commit(mode, growtype, awakening, current);
                    mode = 3;
                    awakening = a;
                    current = default;
                    continue;
                }
                if (mode == 0)
                {
                    continue;
                }
                double? number = line.FirstNumber();
                if (number.HasValue && current.TrySet(tag, ScaledRound((float)number.Value), out StatBlock updated))
                {
                    current = updated;
                }
            }
            growth.Commit(mode, growtype, awakening, current);
            return growth;
        }

        private void Commit(int mode, int growtype, int awakening, StatBlock block)
        {
            switch (mode)
            {
                case 1:
                    Initial = block;
                    HasInitial = true;
                    break;
                case 2:
                    _growtypes[growtype] = block;
                    _hasGrowtype[growtype] = true;
                    break;
                case 3:
                    _awakenings[growtype * AwakeningCount + (awakening - 1)] = block;
                    break;
            }
        }

        /// <summary>"[growtype 3]" → 下标 2;"[awakening 2]" → 2(觉醒用原号)。<paramref name="limit"/> 是允许的最大号(含)。</summary>
        private static bool TryBlockIndex(string tag, string prefix, out int index, int limit)
        {
            index = -1;
            if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !tag.EndsWith("]", StringComparison.Ordinal))
            {
                return false;
            }
            string number = tag.Substring(prefix.Length, tag.Length - prefix.Length - 1);
            if (!int.TryParse(number, out int n) || n < 1 || n > limit)
            {
                return false;
            }
            index = prefix == "[growtype " ? n - 1 : n;
            return true;
        }

        /// <summary>
        /// 服务端读属性的换算:<c>GetRoundInt(10.0f × 值)</c>——单精度乘 10,再 "非负加 0.5、负数减 0.5" 后向零取整(四舍五入,远离零)。
        /// </summary>
        public static long ScaledRound(float value)
        {
            float scaled = 10.0f * value;
            double d = scaled;
            return (long)(scaled < 0f ? d - 0.5 : d + 0.5);
        }
    }
}
