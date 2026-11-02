using System;
using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 地图里一个怪物的"种类"(服务端 map_monster 里的一个字节,取值小于 5):按服务端的用法,
    /// 3 走首领表并带首领特性,1 与 2 走精英词缀(与 .mob 里的 [common champion ...] / [super champion ...] 对应),0 是普通怪。
    /// 名字是移植者按用法起的(推断);4 的用法没有找到。
    /// </summary>
    public enum MonsterKind
    {
        Normal = 0,
        CommonChampion = 1,
        SuperChampion = 2,
        Boss = 3,
        Other = 4,
    }

    /// <summary>
    /// 一个怪物最终的数值块,按原版"主动对象静态信息"(服务端 ActiveStaticInfo)的布局存放:
    /// 一个 int 数组,下标等于结构字节偏移除以 4;很多整数后面紧跟一个浮点倍率(缺省 1.0,以位模式存在同一个数组里)。
    /// 这样 <see cref="MonsterAbility"/> 里的每一步都能与服务端的函数逐行对应,也方便与独立的 Python 参考实现逐项比对。
    /// 字段名由移植者按用途起(观察到的是下标、缺省值与去向;名字属推断),没有认出含义的下标不给名字,保留在 <see cref="Raw"/> 里。
    /// </summary>
    public sealed class MonsterStats
    {
        /// <summary>整数个数:服务端的清零函数写到下标 0xBB 为止。</summary>
        public const int Size = 0xBC;

        // 下标(= 偏移 / 4)
        public const int IxLevel = 0;
        public const int IxHpBase = 1;
        public const int IxHpRate = 2;
        public const int IxMpBase = 3;
        public const int IxMpRate = 4;
        /// <summary>字节标志:缺省 1,取自脚本的 [life max](只有低字节有意义;全库仅 3 个怪物写了它)。</summary>
        public const int IxFlag = 5;
        public const int IxPhysicalAttack = 6;
        public const int IxPhysicalDefense = 8;
        public const int IxMagicalAttack = 10;
        public const int IxMagicalDefense = 12;
        public const int IxElementResistance = 0xE;
        public const int IxElementAttack = 0x16;
        public const int IxStatusResistance = 0x1E;
        public const int IxStuckResistance = 0x2B;
        public const int IxHpRegen = 0x42;
        public const int IxMpRegen = 0x44;
        public const int IxMoveSpeed = 0x46;
        public const int IxAttackSpeed = 0x48;
        public const int IxCastSpeed = 0x4A;
        public const int IxHitRecovery = 0x4C;
        public const int IxRigidity = 0x4E;
        public const int IxJumpPower = 0x50;
        public const int IxJumpSpeed = 0x52;
        public const int IxWeight = 0x54;
        public const int IxEquipPhysicalAttack = 0x56;
        public const int IxEquipPhysicalDefense = 0x58;
        public const int IxEquipMagicalAttack = 0x5A;
        public const int IxEquipMagicalDefense = 0x5C;

        // 下面几格是 .act 的属性改变附加物写到的位置(客户端 0x00984260 的各分支,E-161);下标 = 客户端属性记录里容器偏移 ÷ 12

        /// <summary>硬直抗性(异常状态抗性第 13 项)的浮点倍率:17 个抗性倍率从 0x2F 起,第 13 项在 0x3C。</summary>
        public const int IxStuckResistanceRate = 0x3C;

        /// <summary>物理绝对防御(浮点,缺省 0):客户端的伤害填充函数把它读进公式的"物理穿透"项。</summary>
        public const int IxPhysicalAbsoluteDefense = 0x60;

        /// <summary>魔法绝对防御(浮点,缺省 0):读进公式的"魔法穿透"项。</summary>
        public const int IxMagicalAbsoluteDefense = 0x63;

        /// <summary>[hit recovery] 属性改变写的浮点格(缺省 1.0);眼下没有读它的地方。</summary>
        public const int IxHitStagger = 0x6E;

        /// <summary>物理攻击"加成"整数格(只有比例写法的属性改变会写它,眼下没有读它的地方)。</summary>
        public const int IxPhysicalAttackBonus = 0x6F;

        /// <summary>魔法攻击"加成"整数格(同上)。</summary>
        public const int IxMagicalAttackBonus = 0x71;

        /// <summary>整数 / 浮点混存的原始数组。</summary>
        public int[] Raw { get; } = new int[Size];

        /// <summary>按服务端清零函数(ActiveStaticInfo::clear)的缺省值创建。</summary>
        public MonsterStats()
        {
            Clear();
        }

        public float F(int index)
        {
            return BitConverter.Int32BitsToSingle(Raw[index]);
        }

        public void SetF(int index, float value)
        {
            Raw[index] = BitConverter.SingleToInt32Bits(value);
        }

        public MonsterStats Clone()
        {
            var c = new MonsterStats();
            Array.Copy(Raw, c.Raw, Size);
            return c;
        }

        /// <summary>重置为缺省值(服务端 ActiveStaticInfo::clear 的逐项翻译,缺省值取自反汇编里的立即数)。</summary>
        public void Clear()
        {
            Array.Clear(Raw, 0, Size);
            const float one = 1.0f;
            SetF(2, one);
            SetF(4, one);
            Raw[5] = 1; // 这个槽实际是一个字节(原版 ActiveStaticInfo::clear 只写低字节),其余 3 字节未初始化;这里置 0
            SetF(7, one);
            SetF(9, one);
            SetF(0xB, one);
            SetF(0xD, one);
            for (int i = 0; i < 4; i++)
            {
                SetF(0x12 + i, one);
                SetF(0x1A + i, one);
            }
            for (int i = 0; i < 0x11; i++)
            {
                SetF(0x2F + i, one);
            }
            SetF(0x41, one);
            SetF(0x43, one);
            SetF(0x45, one);
            Raw[0x46] = 1000;
            SetF(0x47, one);
            Raw[0x48] = 1000;
            SetF(0x49, one);
            Raw[0x4A] = 1000;
            SetF(0x4B, one);
            SetF(0x4D, one);
            SetF(0x4F, one);
            SetF(0x51, one);
            Raw[0x52] = 100;
            SetF(0x53, one);
            SetF(0x55, one);
            SetF(0x57, one);
            SetF(0x59, one);
            SetF(0x5B, one);
            SetF(0x5D, one);
            SetF(0x6E, one);
            SetF(0x73, one);
            SetF(0x76, one);
            SetF(0xB7, one);
            SetF(0xB8, one);
            SetF(0xBB, one);
        }

        public int Level => Raw[IxLevel];

        /// <summary>生命值上限 = 基数 × 倍率(向零取整;原版 getHpMax)。</summary>
        public int HpMax => X87.TruncateToInt(X87.Mul(X87.FromInt(Raw[IxHpBase]), X87.FromSingle(F(IxHpRate))));

        /// <summary>魔法值上限 = 基数 × 倍率(原版 getMpMax)。</summary>
        public int MpMax => X87.TruncateToInt(X87.Mul(X87.FromInt(Raw[IxMpBase]), X87.FromSingle(F(IxMpRate))));

        public int PhysicalAttack => Raw[IxPhysicalAttack];
        public int PhysicalDefense => Raw[IxPhysicalDefense];
        public int MagicalAttack => Raw[IxMagicalAttack];
        public int MagicalDefense => Raw[IxMagicalDefense];
        public int EquipPhysicalAttack => Raw[IxEquipPhysicalAttack];
        public int EquipPhysicalDefense => Raw[IxEquipPhysicalDefense];
        public int EquipMagicalAttack => Raw[IxEquipMagicalAttack];
        public int EquipMagicalDefense => Raw[IxEquipMagicalDefense];

        /// <summary>火、水、暗、光(下标 0..3)的元素抗性。</summary>
        public int ElementResistance(int element)
        {
            return Raw[IxElementResistance + element];
        }

        /// <summary>火、水、暗、光(下标 0..3)的元素攻击。</summary>
        public int ElementAttack(int element)
        {
            return Raw[IxElementAttack + element];
        }

        /// <summary>17 种异常状态抗性之一(下标见 <see cref="MonsterDef.StatusResistanceTags"/>)。</summary>
        public int StatusResistance(int index)
        {
            return Raw[IxStatusResistance + index];
        }

        /// <summary>硬直抗性(异常状态抗性的第 13 项,[STUCK] 类别修正的就是它)。</summary>
        public int StuckResistance => Raw[IxStuckResistance];

        public int MoveSpeed => Raw[IxMoveSpeed];
        public int AttackSpeed => Raw[IxAttackSpeed];
        public int CastSpeed => Raw[IxCastSpeed];
        public int HitRecovery => Raw[IxHitRecovery];
        public int Rigidity => Raw[IxRigidity];
        public int JumpPower => Raw[IxJumpPower];
        public int JumpSpeed => Raw[IxJumpSpeed];
        public int Weight => Raw[IxWeight];

        /// <summary>每个周期恢复的生命值(= 生命上限 × [HP regen speed] / 1000,向零取整;周期长度未确认,不要当作每秒)。</summary>
        public int HpRegen => Raw[IxHpRegen];

        public int MpRegen => Raw[IxMpRegen];
    }

    /// <summary>
    /// 怪物数值的计算(证据 E-062):由服务端 setBasicAbilityByLevel(按等级与基础属性表算)与
    /// upgradeAbilityByMonsterParameterCategory(按 [ability category] 修正)逐行移植而来。
    /// 精度说明:服务端是 32 位 x86 程序,浮点走 x87 扩展精度(64 位有效位),中间结果留在 80 位寄存器里,FSTP 才舍入,
    /// FISTP 取整时被临时改成截断。这里用 <see cref="X87"/> 的精确模型逐运算复现,与服务端函数在模拟器里的运行结果逐位一致
    /// (全库 8,050 个 怪物×等级×种类 组合与 10 万个随机合成怪物,零不一致;见证据 E-062)。
    /// 没有实现的环节(各自有服务端函数,待做):按副本难度(upgradeAbilityByDifficultyOfDungeon)、
    /// 按队伍人数(upgradeAbilityByParty)、首领特性(服务端里是空函数)、精英词缀(upgradeAbilityByChampionFeature)、
    /// 死亡塔与血系统。
    /// </summary>
    public static class MonsterAbility
    {
        /// <summary>[ability category] 的 14 个类别名,下标即服务端的类别编号(取自服务端的名字表)。</summary>
        public static readonly string[] CategoryNames =
        {
            "[ATTACK_SPEED]", "[MOVE_SPEED]", "[PHYSICAL_ATTACK]", "[PHYSICAL_DEFENSE]", "[MAGICAL_ATTACK]",
            "[MAGICAL_DEFENSE]", "[EQUIPMENT_PHYSICAL_ATTACK]", "[EQUIPMENT_PHYSICAL_DEFENSE]",
            "[EQUIPMENT_MAGICAL_ATTACK]", "[EQUIPMENT_MAGICAL_DEFENSE]", "[HP MAX]", "[HIT_RECOVERY]", "[STUCK]",
            "[HP MAX BOSS]",
        };

        /// <summary>类别编号 → <see cref="MonsterStats.Raw"/> 下标(服务端的跳转表;10 与 13 都是生命值基数)。</summary>
        public static readonly int[] CategoryField =
        {
            MonsterStats.IxAttackSpeed, MonsterStats.IxMoveSpeed, MonsterStats.IxPhysicalAttack,
            MonsterStats.IxPhysicalDefense, MonsterStats.IxMagicalAttack, MonsterStats.IxMagicalDefense,
            MonsterStats.IxEquipPhysicalAttack, MonsterStats.IxEquipPhysicalDefense,
            MonsterStats.IxEquipMagicalAttack, MonsterStats.IxEquipMagicalDefense, MonsterStats.IxHpBase,
            MonsterStats.IxHitRecovery, MonsterStats.IxStuckResistance, MonsterStats.IxHpBase,
        };

        /// <summary>类别 [HP MAX](10)与 [HP MAX BOSS](13)的编号。</summary>
        public const int CategoryHpMax = 10;

        public const int CategoryHpMaxBoss = 13;

        /// <summary>类别名对应的编号(区分大小写,与服务端字符串比较一致);不认识返回 -1。</summary>
        public static int CategoryIndex(string name)
        {
            for (int i = 0; i < CategoryNames.Length; i++)
            {
                if (string.Equals(CategoryNames[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 随等级线性插值(GetLinearDataForMonsterAbility):cur 与 total 是"怪物等级减 [level] 最小值"与"最大值减最小值"。
        /// cur 不小于 total 取最大值;cur 不大于 0 取最小值;否则 最小 + (最大 - 最小) × cur / total(32 位整数乘法与有符号整除)。
        /// </summary>
        public static int Lerp(int min, int max, int cur, int total)
        {
            if (cur >= total)
            {
                return max;
            }
            if (cur <= 0)
            {
                return min;
            }
            return unchecked((max - min) * cur) / total + min;
        }

        /// <summary>"有名怪物"的编号范围(isNamedMonster):50000..51000 与 56000..60000。</summary>
        public static bool IsNamedMonster(int monsterId)
        {
            return (monsterId >= 50000 && monsterId <= 51000) || (monsterId >= 56000 && monsterId <= 60000);
        }

        /// <summary>
        /// 选基础属性表(getMonsterTableIndex)。后面的条件覆盖前面的:
        /// 脚本的 [ability table] 为 1 或 forceSummon → 3(召唤表);种类为首领 → 2;有名怪物编号 → 1;
        /// dungeonParam 在 2000..2002 或 forceWarRoom → 4(战争房表);都不是 → 0(普通表)。
        /// </summary>
        public static int SelectTable(MonsterDef def, int dungeonParam, MonsterKind kind, bool forceSummon, bool forceWarRoom)
        {
            int index = 0;
            if (def.AbilityTable == 1)
            {
                index = MonsterBaseTables.Summon;
            }
            if (forceSummon)
            {
                index = MonsterBaseTables.Summon;
            }
            if (kind == MonsterKind.Boss)
            {
                index = MonsterBaseTables.Boss;
            }
            if (IsNamedMonster(def.Id))
            {
                index = MonsterBaseTables.Named;
            }
            if ((dungeonParam > 1999 && dungeonParam < 2003) || forceWarRoom)
            {
                index = MonsterBaseTables.WarRoom;
            }
            return index;
        }

        /// <summary>
        /// 按等级算基础属性(setBasicAbilityByLevel)。level 是这个怪物在地图里的等级;dungeonParam 只在战争房表里替代等级去查表;
        /// ownTable 是该怪物自己的 [base table](已解析并合并,行号与公共表对应;没有传 null)。
        /// 查不到表行时只设等级,其余保持缺省。
        /// </summary>
        public static MonsterStats BasicByLevel(
            MonsterDef def,
            MonsterBaseTables tables,
            MonsterBaseTable ownTable,
            int level,
            int dungeonParam,
            MonsterKind kind,
            bool forceSummon = false,
            bool forceWarRoom = false)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            if (tables == null)
            {
                throw new ArgumentNullException(nameof(tables));
            }
            var s = new MonsterStats();
            s.Raw[MonsterStats.IxLevel] = level;
            int cur = level - def.Level.Min;
            int total = def.Level.Max - def.Level.Min;
            int tableIndex = SelectTable(def, dungeonParam, kind, forceSummon, forceWarRoom);
            int keyLevel = tableIndex == MonsterBaseTables.WarRoom ? dungeonParam : level;
            MonsterBaseTable table = tables[tableIndex];
            int rowIndex = table.IndexOfLevel(keyLevel);
            if (rowIndex < 0)
            {
                return s;
            }
            MonsterBaseRow row = table.Rows[rowIndex];
            if (ownTable != null && ownTable.Count > 0 && rowIndex < ownTable.Count)
            {
                // 怪物自带的表按同一行号取用(服务端不检查越界;这里越界时沿用公共表的行)
                row = ownTable.Rows[rowIndex];
            }

            int[] r = s.Raw;
            r[MonsterStats.IxHpBase] = row.HpBase;
            r[MonsterStats.IxMpBase] = row.MpBase;
            r[MonsterStats.IxPhysicalAttack] = row.PhysicalAttack;
            r[MonsterStats.IxPhysicalDefense] = row.PhysicalDefense;
            r[MonsterStats.IxMagicalAttack] = row.MagicalAttack;
            r[MonsterStats.IxMagicalDefense] = row.MagicalDefense;
            r[MonsterStats.IxEquipPhysicalAttack] = row.EquipPhysicalAttack;
            r[MonsterStats.IxEquipPhysicalDefense] = row.EquipPhysicalDefense;
            r[MonsterStats.IxEquipMagicalAttack] = row.EquipMagicalAttack;
            r[MonsterStats.IxEquipMagicalDefense] = row.EquipMagicalDefense;
            // 下标 5 是字节:脚本结构 +0x3B8(缺省 1,由 [life max] 写入)的低字节,其余 3 字节原版未初始化,这里置 0
            r[MonsterStats.IxFlag] = def.LifeMax & 0xFF;

            for (int i = 0; i < 4; i++)
            {
                IntPair p = def.ElementResistance[i];
                r[MonsterStats.IxElementResistance + i] = Lerp(p.Min, p.Max, cur, total);
            }
            for (int i = 0; i < 0x11; i++)
            {
                IntPair p = def.StatusResistance[i];
                r[MonsterStats.IxStatusResistance + i] = Lerp(p.Min, p.Max, cur, total);
            }
            r[MonsterStats.IxMoveSpeed] = Lerp(def.MoveSpeed.Min, def.MoveSpeed.Max, cur, total);
            r[MonsterStats.IxAttackSpeed] = Lerp(def.AttackSpeed.Min, def.AttackSpeed.Max, cur, total);
            r[MonsterStats.IxCastSpeed] = Lerp(def.CastSpeed.Min, def.CastSpeed.Max, cur, total);
            r[MonsterStats.IxHitRecovery] = Lerp(def.HitRecovery.Min, def.HitRecovery.Max, cur, total);
            r[MonsterStats.IxJumpPower] = Lerp(def.JumpPower.Min, def.JumpPower.Max, cur, total);
            r[MonsterStats.IxJumpSpeed] = Lerp(def.JumpSpeed.Min, def.JumpSpeed.Max, cur, total);
            r[MonsterStats.IxWeight] = Lerp(def.Weight.Min, def.Weight.Max, cur, total);

            // 生命、魔法的恢复:先把恢复速度除以 1000 落成 float,再乘上限(服务端按这个顺序,中间落地成 float)
            int hpRegenSpeed = Lerp(def.HpRegenSpeed.Min, def.HpRegenSpeed.Max, cur, total);
            int mpRegenSpeed = Lerp(def.MpRegenSpeed.Min, def.MpRegenSpeed.Max, cur, total);
            float hpRegenRate = X87.ToSingle(X87.Div(X87.FromInt(hpRegenSpeed), X87.FromInt(1000)));
            float mpRegenRate = X87.ToSingle(X87.Div(X87.FromInt(mpRegenSpeed), X87.FromInt(1000)));
            r[MonsterStats.IxHpRegen] = X87.TruncateToInt(X87.Mul(X87.FromInt(s.HpMax), X87.FromSingle(hpRegenRate)));
            r[MonsterStats.IxMpRegen] = X87.TruncateToInt(X87.Mul(X87.FromInt(s.MpMax), X87.FromSingle(mpRegenRate)));

            // 第 12 列除以 100 加进一个浮点字段(下标 0x6A);第 13 列除以 10 取整加进硬直抗性
            s.SetF(0x6A, X87.ToSingle(X87.Add(X87.FromSingle(s.F(0x6A)), X87.Div(X87.FromInt(row.Extra2C), X87.FromInt(100)))));
            r[MonsterStats.IxStuckResistance] += X87.TruncateToInt(X87.Div(X87.FromInt(row.StuckResistanceTenths), X87.FromInt(10)));

            for (int i = 0; i < 4; i++)
            {
                IntPair p = def.ElementAttack[i];
                r[MonsterStats.IxElementAttack + i] = Lerp(p.Min, p.Max, cur, total);
                r[MonsterStats.IxElementResistance + i] += row.ElementResistanceAdd(i);
                r[MonsterStats.IxElementAttack + i] += row.ElementAttackAdd(i);
            }
            r[MonsterStats.IxHitRecovery] += row.HitRecoveryAdd;
            r[MonsterStats.IxRigidity] = Lerp(def.Rigidity.Min, def.Rigidity.Max, cur, total);
            r[MonsterStats.IxRigidity] += row.RigidityAdd;
            for (int i = 0; i < 0x11; i++)
            {
                if (i != 0xD)
                {
                    r[MonsterStats.IxStatusResistance + i] += row.StatusResistanceAdd;
                }
            }
            return s;
        }

        /// <summary>
        /// 按 [ability category] 修正数值(upgradeAbilityByMonsterParameterCategory)。
        /// 乘:字段 = 截断(数值 / 100 × (float)字段)(字段先落成 float,数值 / 100 留在 80 位寄存器里不落地,乘完截断取整);
        /// 加:字段 += 数值 / 100(整数除法,向零取整)。
        /// 若列表里有"首领生命上限"(13):首领只用 13、跳过 10;非首领只用 10、跳过 13。
        /// </summary>
        public static void ApplyCategories(MonsterStats stats, IReadOnlyList<AbilityCategoryEntry> entries, bool boss)
        {
            if (stats == null)
            {
                throw new ArgumentNullException(nameof(stats));
            }
            if (entries == null)
            {
                return;
            }
            bool hasBossHp = false;
            foreach (AbilityCategoryEntry e in entries)
            {
                if (e.Type == CategoryHpMaxBoss)
                {
                    hasBossHp = true;
                    break;
                }
            }
            foreach (AbilityCategoryEntry e in entries)
            {
                if ((uint)e.Type >= (uint)CategoryField.Length)
                {
                    continue;
                }
                if (hasBossHp && boss && e.Type == CategoryHpMax)
                {
                    continue;
                }
                if (hasBossHp && !boss && e.Type == CategoryHpMaxBoss)
                {
                    continue;
                }
                int field = CategoryField[e.Type];
                if (e.Multiply)
                {
                    float asFloat = X87.ToSingle(X87.FromInt(stats.Raw[field]));
                    X87Value factor = X87.Div(X87.FromInt(e.Value), X87.FromInt(100));
                    stats.Raw[field] = X87.TruncateToInt(X87.Mul(factor, X87.FromSingle(asFloat)));
                }
                else
                {
                    stats.Raw[field] += e.Value / 100;
                }
            }
        }

        /// <summary>基础属性加 [ability category] 修正(服务端 set_monster_ability 的前两步;首领按种类判定)。</summary>
        public static MonsterStats Compute(
            MonsterDef def,
            MonsterBaseTables tables,
            MonsterBaseTable ownTable,
            int level,
            int dungeonParam,
            MonsterKind kind)
        {
            MonsterStats s = BasicByLevel(def, tables, ownTable, level, dungeonParam, kind);
            ApplyCategories(s, def.AbilityCategories, kind == MonsterKind.Boss);
            return s;
        }

        /// <summary>
        /// 同上,再接上第三步:副本难度加成(<see cref="MonsterDifficultyTable.Apply"/>,证据 E-125)。
        /// <paramref name="difficultyTable"/> 为 null 或没载入、难度不在 0 到 4 时与上面那个重载一样。首领特性与精英词缀(服务端的第四步)没有做。
        /// </summary>
        public static MonsterStats Compute(
            MonsterDef def,
            MonsterBaseTables tables,
            MonsterBaseTable ownTable,
            int level,
            int dungeonParam,
            MonsterKind kind,
            MonsterDifficultyTable difficultyTable,
            int difficulty,
            DungeonDifficultyBonus dungeonBonus = null)
        {
            MonsterStats s = Compute(def, tables, ownTable, level, dungeonParam, kind);
            difficultyTable?.Apply(s, difficulty, level, dungeonBonus);
            return s;
        }
    }
}
