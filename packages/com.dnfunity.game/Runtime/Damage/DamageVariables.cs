using System;
using System.Buffers.Binary;

namespace Dnf.Game
{
    /// <summary>
    /// 一次命中的伤害计算输入/输出(原版的"计算伤害变量"结构,证据 E-049)。
    /// 客户端按命中把它填好、算出伤害,再把整块(zlib 压缩后)发给服务端复算;服务端用同一个公式核对,
    /// 差超过 1 就记一次外挂嫌疑。所以这里按原版的字节布局存放(打包、不对齐,共 0x1FC 字节),
    /// 将来联网时可以直接收发,也能原样复现原版"按偏移读邻近字节"的行为。
    /// 字段名是移植者按公式里的用法起的(观察到的是偏移、类型和默认值,名字与作用属推断);每个属性的注释里标了偏移。
    /// 默认值取自客户端的初始化函数(0x00818110):大多数"倍率"类浮点为 1.0,"加成"类为 0,
    /// 两个防御百分比为整数 1,攻击者等级为 1。
    /// </summary>
    public sealed class DamageVariables
    {
        /// <summary>结构总字节数。</summary>
        public const int Size = 0x1FC;

        /// <summary>服务端接收缓冲的大小(字节):客户端发来的数据最多这么长,末尾的 +0x1F4/+0x1F8 两个浮点不在其内(观察)。</summary>
        public const int WireLimit = 500;

        /// <summary>类型倍率表每组的项数(普通命中一组、暴击一组)。</summary>
        public const int TypeRateCount = 26;

        private readonly byte[] _data;

        /// <summary>按客户端初始化函数的默认值创建。</summary>
        public DamageVariables()
        {
            _data = new byte[Size];
            ApplyClientDefaults();
        }

        private DamageVariables(byte[] data)
        {
            _data = data;
        }

        /// <summary>
        /// 从原始字节还原(例如服务端解压出来的数据)。<paramref name="count"/> 字节以后保持为 0(不套用默认值,
        /// 因为服务端自己的局部结构也是整块被覆盖的)。
        /// </summary>
        public static DamageVariables FromBytes(byte[] raw, int count)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }
            var data = new byte[Size];
            Array.Copy(raw, 0, data, 0, Math.Min(Math.Min(count, raw.Length), Size));
            return new DamageVariables(data);
        }

        /// <summary>整块字节的副本。</summary>
        public byte[] ToBytes()
        {
            return (byte[])_data.Clone();
        }

        public DamageVariables Clone()
        {
            return new DamageVariables((byte[])_data.Clone());
        }

        // ---- 按偏移读写(公式用;越界读返回 false 的 ByteAt) ----

        /// <summary>读偏移处的一个字节;越出结构范围返回 0(原版会读到结构外的内存,合法数据不会走到这里)。</summary>
        public byte ByteAt(int offset)
        {
            return (uint)offset < (uint)Size ? _data[offset] : (byte)0;
        }

        public int Int32At(int offset)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, 4));
        }

        public void SetInt32At(int offset, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_data.AsSpan(offset, 4), value);
        }

        public float SingleAt(int offset)
        {
            return BitConverter.Int32BitsToSingle(Int32At(offset));
        }

        public void SetSingleAt(int offset, float value)
        {
            SetInt32At(offset, BitConverter.SingleToInt32Bits(value));
        }

        private double DoubleAt(int offset)
        {
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(offset, 8)));
        }

        private void SetDoubleAt(int offset, double value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_data.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(value));
        }

        private bool FlagAt(int offset)
        {
            return _data[offset] != 0;
        }

        private void SetFlagAt(int offset, bool value)
        {
            _data[offset] = value ? (byte)1 : (byte)0;
        }

        private void ApplyClientDefaults()
        {
            const float one = 1.0f;
            foreach (int o in new[]
            {
                0x06, 0x13, 0x17, 0x1B, 0x1F, 0x23, 0x27, 0x2B, 0x37, 0x3B, 0x47, 0x4B, 0x4F, 0x53, 0x57, 0x5B, 0x5F,
                0x6B, 0x6F, 0x77, 0x8F, 0xB3, 0x1A6, 0x1F0,
            })
            {
                SetSingleAt(o, one);
            }
            for (int i = 0; i < TypeRateCount * 2; i++)
            {
                SetSingleAt(0xD6 + 4 * i, one);
            }
            SetInt32At(0x97, 1);
            SetInt32At(0xA3, 1);
            SetInt32At(0x1D5, 1);
            _data[0x1B3] = 1;
            _data[0x1B4] = 1;
        }

        // ---- 基础 ----

        /// <summary>+0x00:基础伤害(来自攻击信息或技能的计算)。</summary>
        public float Base { get => SingleAt(0x00); set => SetSingleAt(0x00, value); }

        /// <summary>+0x04:为真时加上"附加基础值"(+0x2F/+0x33),并以攻击者的第二等级(+0x1B4)取随机幅度。</summary>
        public bool UseBonusBase { get => FlagAt(0x04); set => SetFlagAt(0x04, value); }

        /// <summary>+0x05:攻击种类。0 物理、1 魔法、2 无视防御。别的值公式里什么都不做。</summary>
        public AttackKind Kind { get => (AttackKind)_data[0x05]; set => _data[0x05] = (byte)value; }

        /// <summary>+0x06:百分比加成(用 x*0.01+1 的形式乘上去)。</summary>
        public float DamagePercent { get => SingleAt(0x06); set => SetSingleAt(0x06, value); }

        /// <summary>+0x0A..+0x0D:攻击带不带火、水、暗、光(下标 0..3)。</summary>
        public bool ElementFlag(int index)
        {
            if ((uint)index > 3u)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return FlagAt(0x0A + index);
        }

        public void SetElementFlag(int index, bool value)
        {
            if ((uint)index > 3u)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            SetFlagAt(0x0A + index, value);
        }

        /// <summary>+0x0E:为真时跳过防御相关的各项(防御率、固定减伤)。</summary>
        public bool IgnoreDefense { get => FlagAt(0x0E); set => SetFlagAt(0x0E, value); }

        /// <summary>+0x0F:固定值加成的基数(物理/魔法共用,按种类乘上各自的百分比再加到伤害上)。</summary>
        public float FlatBonus { get => SingleAt(0x0F); set => SetSingleAt(0x0F, value); }

        // ---- 暴击 ----

        /// <summary>+0x13:暴击率加成(百分点,暴击率 = (3 + 各项) * 100 / 10000)。</summary>
        public float CriticalRate { get => SingleAt(0x13); set => SetSingleAt(0x13, value); }

        /// <summary>+0x17:技能暴击率加成(+0x1DE 为真时计入)。</summary>
        public float SkillCriticalRate { get => SingleAt(0x17); set => SetSingleAt(0x17, value); }

        /// <summary>+0x4B:攻击者的暴击属性(物理)。</summary>
        public float CriticalStatPhysical { get => SingleAt(0x4B); set => SetSingleAt(0x4B, value); }

        /// <summary>+0x4F:攻击者的暴击属性(魔法)。</summary>
        public float CriticalStatMagical { get => SingleAt(0x4F); set => SetSingleAt(0x4F, value); }

        /// <summary>+0x53:攻击者的技能暴击属性(物理)。</summary>
        public float SkillCriticalStatPhysical { get => SingleAt(0x53); set => SetSingleAt(0x53, value); }

        /// <summary>+0x57:攻击者的技能暴击属性(魔法)。</summary>
        public float SkillCriticalStatMagical { get => SingleAt(0x57); set => SetSingleAt(0x57, value); }

        /// <summary>+0x5B:攻击者是召唤物时用的暴击属性。</summary>
        public float CreatureCriticalStat { get => SingleAt(0x5B); set => SetSingleAt(0x5B, value); }

        /// <summary>+0xCF:防御方的暴击抗性(从暴击率里扣)。</summary>
        public float CriticalResist { get => SingleAt(0xCF); set => SetSingleAt(0xCF, value); }

        /// <summary>+0x6F:暴击倍率的缩放(攻击者为活动对象时乘在默认暴击倍率上)。</summary>
        public float CriticalMultiplier { get => SingleAt(0x6F); set => SetSingleAt(0x6F, value); }

        /// <summary>+0x73:暴击倍率的加成(加在缩放后的暴击倍率上)。</summary>
        public float CriticalAdd { get => SingleAt(0x73); set => SetSingleAt(0x73, value); }

        // ---- 倍率与加成 ----

        /// <summary>+0x1B:最终百分比 A(伤害 *= 值 * 0.01)。</summary>
        public float FinalPercentA { get => SingleAt(0x1B); set => SetSingleAt(0x1B, value); }

        /// <summary>+0x1F/+0x23/+0x27:三个条件百分比,各自由 +0xD3/+0xD4/+0xD5 的开关控制(伤害 *= 值 * 0.01)。</summary>
        public float ConditionalPercent(int index)
        {
            return SingleAt(ConditionalOffset(index));
        }

        public void SetConditionalPercent(int index, float value)
        {
            SetSingleAt(ConditionalOffset(index), value);
        }

        /// <summary>+0xD3/+0xD4/+0xD5:三个条件百分比的开关。</summary>
        public bool ConditionalFlag(int index)
        {
            return FlagAt(0xD3 + CheckThree(index));
        }

        public void SetConditionalFlag(int index, bool value)
        {
            SetFlagAt(0xD3 + CheckThree(index), value);
        }

        private static int ConditionalOffset(int index)
        {
            return 0x1F + 4 * CheckThree(index);
        }

        private static int CheckThree(int index)
        {
            if ((uint)index > 2u)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return index;
        }

        /// <summary>+0x2B:召唤物伤害公式里的倍率。</summary>
        public float CreatureRate { get => SingleAt(0x2B); set => SetSingleAt(0x2B, value); }

        /// <summary>+0x2F:附加基础值(物理与无视防御种类用,+0x04 为真时加到基础伤害上)。</summary>
        public float BonusBasePhysical { get => SingleAt(0x2F); set => SetSingleAt(0x2F, value); }

        /// <summary>+0x33:附加基础值(魔法用)。</summary>
        public float BonusBaseMagical { get => SingleAt(0x33); set => SetSingleAt(0x33, value); }

        /// <summary>+0x37:物理百分比加成。</summary>
        public float PhysicalPercent { get => SingleAt(0x37); set => SetSingleAt(0x37, value); }

        /// <summary>+0x3B:魔法百分比加成。</summary>
        public float MagicalPercent { get => SingleAt(0x3B); set => SetSingleAt(0x3B, value); }

        /// <summary>+0x3F:物理攻击力(除以常量后作为倍率)。</summary>
        public float PhysicalAttack { get => SingleAt(0x3F); set => SetSingleAt(0x3F, value); }

        /// <summary>+0x43:魔法攻击力。</summary>
        public float MagicalAttack { get => SingleAt(0x43); set => SetSingleAt(0x43, value); }

        /// <summary>+0x47:额外倍率(伤害 += 值 * 伤害)。</summary>
        public float ExtraRate { get => SingleAt(0x47); set => SetSingleAt(0x47, value); }

        /// <summary>+0x63:物理"附加基础值次数"(与 +0xB3 相乘后加到固定值加成基数上)。</summary>
        public int BonusBaseCountPhysical { get => Int32At(0x63); set => SetInt32At(0x63, value); }

        /// <summary>+0x67:魔法"附加基础值次数"。</summary>
        public int BonusBaseCountMagical { get => Int32At(0x67); set => SetInt32At(0x67, value); }

        /// <summary>+0xB3:附加基础值的单位量。</summary>
        public float BonusBaseScale { get => SingleAt(0xB3); set => SetSingleAt(0xB3, value); }

        /// <summary>+0x6B:最终倍率(伤害 *= 值,之后下限取 1)。</summary>
        public float FinalRate { get => SingleAt(0x6B); set => SetSingleAt(0x6B, value); }

        /// <summary>+0x77:攻击者倍率(攻击者为活动对象且不等于 1 时乘上去)。</summary>
        public float AttackerRate { get => SingleAt(0x77); set => SetSingleAt(0x77, value); }

        /// <summary>+0x7B:攻击者固定加成(攻击者为活动对象且不为 0 时加上去)。</summary>
        public int AttackerFlat { get => Int32At(0x7B); set => SetInt32At(0x7B, value); }

        // ---- 元素与防御 ----

        /// <summary>+0x7F..+0x8E:防御方的火、水、暗、光抗性(整数,下标 0..3)。</summary>
        public int ElementResist(int index)
        {
            if ((uint)index > 3u)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return Int32At(0x7F + 4 * index);
        }

        public void SetElementResist(int index, int value)
        {
            if ((uint)index > 3u)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            SetInt32At(0x7F + 4 * index, value);
        }

        /// <summary>+0x8F:元素抗性的缩放。</summary>
        public float ElementResistScale { get => SingleAt(0x8F); set => SetSingleAt(0x8F, value); }

        /// <summary>+0x93:物理防御力。</summary>
        public int PhysicalDefense { get => Int32At(0x93); set => SetInt32At(0x93, value); }

        /// <summary>+0x97:物理防御百分比(整数,按 值*0.01+1 乘到防御力上)。</summary>
        public int PhysicalDefensePercent { get => Int32At(0x97); set => SetInt32At(0x97, value); }

        /// <summary>+0x9B:物理穿透(百分点)。</summary>
        public float PhysicalPenetration { get => SingleAt(0x9B); set => SetSingleAt(0x9B, value); }

        /// <summary>+0x9F:魔法防御力。</summary>
        public int MagicalDefense { get => Int32At(0x9F); set => SetInt32At(0x9F, value); }

        /// <summary>+0xA3:魔法防御百分比。</summary>
        public int MagicalDefensePercent { get => Int32At(0xA3); set => SetInt32At(0xA3, value); }

        /// <summary>+0xA7:魔法穿透。</summary>
        public float MagicalPenetration { get => SingleAt(0xA7); set => SetSingleAt(0xA7, value); }

        /// <summary>+0x5F:穿透的缩放(防御率里扣的穿透项与固定减伤都乘它)。</summary>
        public float PenetrationScale { get => SingleAt(0x5F); set => SetSingleAt(0x5F, value); }

        /// <summary>+0xB7:物理固定减伤 A(乘穿透缩放)。</summary>
        public int PhysicalFlatReduceA { get => Int32At(0xB7); set => SetInt32At(0xB7, value); }

        /// <summary>+0xBB:魔法固定减伤 A。</summary>
        public int MagicalFlatReduceA { get => Int32At(0xBB); set => SetInt32At(0xBB, value); }

        /// <summary>+0xC7:物理固定减伤 B(直接相加)。</summary>
        public int PhysicalFlatReduceB { get => Int32At(0xC7); set => SetInt32At(0xC7, value); }

        /// <summary>+0xCB:魔法固定减伤 B。</summary>
        public int MagicalFlatReduceB { get => Int32At(0xCB); set => SetInt32At(0xCB, value); }

        /// <summary>+0xBF:物理减伤率(伤害 -= 值 * 伤害)。</summary>
        public float PhysicalReduceRate { get => SingleAt(0xBF); set => SetSingleAt(0xBF, value); }

        /// <summary>+0xC3:魔法减伤率。</summary>
        public float MagicalReduceRate { get => SingleAt(0xC3); set => SetSingleAt(0xC3, value); }

        /// <summary>+0x1E5:为真时防御力折算里的"每级减伤系数"翻倍(防御被削弱)。</summary>
        public bool DefenseWeakened { get => FlagAt(0x1E5); set => SetFlagAt(0x1E5, value); }

        /// <summary>
        /// +0xD6(普通命中)、+0x13E(暴击)起各 26 个浮点的类型倍率表。
        /// 下标 0..25 是普通命中,26..51 是暴击。表项的含义未知(服务端只是依次乘上去),默认全为 1。
        /// </summary>
        public float TypeRate(int index)
        {
            return SingleAt(TypeRateOffset(index));
        }

        public void SetTypeRate(int index, float value)
        {
            SetSingleAt(TypeRateOffset(index), value);
        }

        private static int TypeRateOffset(int index)
        {
            if ((uint)index >= (uint)(TypeRateCount * 2))
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return 0xD6 + 4 * index;
        }

        // ---- 对象与环境 ----

        /// <summary>+0x1B2:当前玩法模块(副本、PVP 等的枚举值;4、5 是 PVP 的两种,9 是突击战,14 时不加随机幅度)。有符号读取。</summary>
        public sbyte Module { get => unchecked((sbyte)_data[0x1B2]); set => _data[0x1B2] = unchecked((byte)value); }

        /// <summary>+0x1B3:攻击者等级(有符号字节)。</summary>
        public sbyte Level { get => unchecked((sbyte)_data[0x1B3]); set => _data[0x1B3] = unchecked((byte)value); }

        /// <summary>+0x1B4:攻击者第二等级(+0x04 为真且攻击者为角色时代替 +0x1B3 取随机幅度)。</summary>
        public sbyte LevelAlt { get => unchecked((sbyte)_data[0x1B4]); set => _data[0x1B4] = unchecked((byte)value); }

        /// <summary>+0x1B5:为真时命中走召唤物公式(召唤物路径的总开关)。</summary>
        public bool CreatureHit { get => FlagAt(0x1B5); set => SetFlagAt(0x1B5, value); }

        /// <summary>+0x1B6:为真时攻击者按召唤物算(暴击属性取 +0x5B)。</summary>
        public bool CreatureSource { get => FlagAt(0x1B6); set => SetFlagAt(0x1B6, value); }

        /// <summary>+0x1B7:召唤物等级(有符号字节)。</summary>
        public sbyte CreatureLevel { get => unchecked((sbyte)_data[0x1B7]); set => _data[0x1B7] = unchecked((byte)value); }

        /// <summary>+0x1B8:召唤物等级的附加(有符号字节)。</summary>
        public sbyte CreatureLevelAdd { get => unchecked((sbyte)_data[0x1B8]); set => _data[0x1B8] = unchecked((byte)value); }

        /// <summary>+0x1B9:召唤物序号(突击战模块用它查"突击召唤物伤害折减")。</summary>
        public sbyte CreatureIndex { get => unchecked((sbyte)_data[0x1B9]); set => _data[0x1B9] = unchecked((byte)value); }

        /// <summary>+0x1AA(8 字节浮点):召唤物公式里加进"额外量"的项,取整数部分使用。</summary>
        public double CreatureExtra { get => DoubleAt(0x1AA); set => SetDoubleAt(0x1AA, value); }

        /// <summary>+0x1BA:直接造成伤害的对象的种类码(被动对象 0x21、召唤物 0x81,其余走碰撞对象路径)。</summary>
        public uint DamagerType { get => (uint)Int32At(0x1BA); set => SetInt32At(0x1BA, unchecked((int)value)); }

        /// <summary>+0x1BE:攻击者(伤害来源的主人)的种类码。客户端由伤害对象的虚函数槽 0x338 取得。</summary>
        public uint AttackerType { get => (uint)Int32At(0x1BE); set => SetInt32At(0x1BE, unchecked((int)value)); }

        /// <summary>+0x1C2:防御方自己的种类码(活动对象才套用防御项)。</summary>
        public uint DefenderType { get => (uint)Int32At(0x1C2); set => SetInt32At(0x1C2, unchecked((int)value)); }

        /// <summary>+0x1C6:防御方的"主人"种类码(同样来自虚函数槽 0x338);为角色时套用类型倍率表。</summary>
        public uint DefenderOwnerType { get => (uint)Int32At(0x1C6); set => SetInt32At(0x1C6, unchecked((int)value)); }

        /// <summary>+0x1CA:命中序号。公式不用,服务端拿它给队伍战场记"已受击包数"。</summary>
        public int HitSerial { get => Int32At(0x1CA); set => SetInt32At(0x1CA, value); }

        /// <summary>+0x1CE:输出:这一击是否暴击(公式写入)。</summary>
        public bool IsCritical { get => FlagAt(0x1CE); set => SetFlagAt(0x1CE, value); }

        /// <summary>+0x1CF:为真时伤害额外乘 1.1,并把多出的部分记在 +0x1D0。</summary>
        public bool BonusHit { get => FlagAt(0x1CF); set => SetFlagAt(0x1CF, value); }

        /// <summary>+0x1D0:输出:额外命中加成的伤害量(公式写入)。</summary>
        public int BonusHitDamage { get => Int32At(0x1D0); set => SetInt32At(0x1D0, value); }

        /// <summary>+0x1D4:命中计数(公式不用)。</summary>
        public byte HitCounter { get => _data[0x1D4]; set => _data[0x1D4] = value; }

        /// <summary>+0x1D5:威力百分比(整数,伤害 *= 值 * 0.01)。</summary>
        public int PowerPercent { get => Int32At(0x1D5); set => SetInt32At(0x1D5, value); }

        /// <summary>+0x1D9:随机种子。公式在局部副本上推进,不改这个字段,所以同一份数据复算结果相同。</summary>
        public uint Seed { get => (uint)Int32At(0x1D9); set => SetInt32At(0x1D9, unchecked((int)value)); }

        /// <summary>+0x1DD:攻击自带的元素(0..3,4 表示无)。有符号读取。</summary>
        public sbyte DeclaredElement { get => unchecked((sbyte)_data[0x1DD]); set => _data[0x1DD] = unchecked((byte)value); }

        /// <summary>+0x1DE:为真时把技能暴击率与技能暴击属性计入暴击率。</summary>
        public bool SkillCriticalFlag { get => FlagAt(0x1DE); set => SetFlagAt(0x1DE, value); }

        /// <summary>+0x1DF:为真且攻击者是角色时,伤害再加上它的 25%(取整)。</summary>
        public bool QuarterBonusFlag { get => FlagAt(0x1DF); set => SetFlagAt(0x1DF, value); }

        /// <summary>+0x1E0:为真时加上 +0x1E1 的固定量。</summary>
        public bool FlatAddFlag { get => FlagAt(0x1E0); set => SetFlagAt(0x1E0, value); }

        /// <summary>+0x1E1:固定加成量。</summary>
        public int FlatAdd { get => Int32At(0x1E1); set => SetInt32At(0x1E1, value); }

        /// <summary>+0x1E6:输出:最终伤害(整数,公式写入)。</summary>
        public int Result { get => Int32At(0x1E6); set => SetInt32At(0x1E6, value); }

        /// <summary>+0x1A6:收尾倍率(默认 1)。</summary>
        public float TailRate { get => SingleAt(0x1A6); set => SetSingleAt(0x1A6, value); }

        /// <summary>+0x1EF:为真且攻击者是角色时,再乘 +0x1F0。</summary>
        public bool TailRate2Flag { get => FlagAt(0x1EF); set => SetFlagAt(0x1EF, value); }

        /// <summary>+0x1F0:收尾倍率 2(默认 1)。</summary>
        public float TailRate2 { get => SingleAt(0x1F0); set => SetSingleAt(0x1F0, value); }

        /// <summary>+0x1F4:收尾固定加成(默认 0)。位于服务端 500 字节接收缓冲之外。</summary>
        public float TailAdd { get => SingleAt(0x1F4); set => SetSingleAt(0x1F4, value); }

        /// <summary>+0x1F8:收尾百分比加成(默认 0,伤害 += 伤害 * 值 / 100)。位于服务端 500 字节接收缓冲之外。</summary>
        public float TailPercent { get => SingleAt(0x1F8); set => SetSingleAt(0x1F8, value); }
    }
}
