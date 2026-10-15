using System;

namespace Dnf.Game
{
    /// <summary>
    /// 伤害公式(证据 E-049):对一份 <see cref="DamageVariables"/> 算出这一击的伤害。
    /// 依据是服务端"伤害校验"模块里的 computeHpDamageSDC(@0x08095C32)及其子函数——服务端用同一份结构复算客户端发来的伤害,
    /// 与客户端的差不超过 1 才算合法,所以这是原版伤害的唯一标准实现;客户端的暴击判定、结构初始化与填充函数的偏移与它一致(观察)。
    ///
    /// 数值语义(从汇编读出,不是从反编译读出——反编译会把中间的 float 落地合并掉):
    /// 每条 C 语句的结果落地成 float(这里的 <c>F(...)</c>),语句内部用 x87 扩展精度(这里用 double,
    /// 与扩展精度的差异只在极少数舍入边界上,概率约 2^-29 每次运算);
    /// 所有 (int) 转换都是"向零取整"(FISTP 且舍入控制为截断),溢出得到 int.MinValue;
    /// 比较按 x87 的无序语义:遇到 NaN,"大于/小于"都不成立。
    /// 倍率里的 0.01、1.05 等常量是 float 常量(@0x08B12F54 起),与这里的 float 字面量位模式一致,由测试核对。
    /// 服务端的 getAbilityConvertRateSDC(@0x080950FB)恒返回 1.0,下面凡是乘它的地方都省略了;
    /// 客户端那一版是否也是常量 1.0 未验证。
    /// </summary>
    public static class DamageFormula
    {
        /// <summary>0.01(@0x08B12F54)。百分比换算。</summary>
        public const float Percent = 0.01f;

        /// <summary>1.05(@0x08B12F68)。攻击元素与自身带元素相同时的加成。</summary>
        public const float SameElementBonus = 1.05f;

        /// <summary>0.07(@0x08B12F6C)。无视防御攻击的随机幅度占比。</summary>
        public const float FixedSpreadRate = 0.07f;

        /// <summary>0.25(@0x08B12F70)。"四分之一加成"的比例。</summary>
        public const float QuarterRate = 0.25f;

        /// <summary>100(@0x08B12F64)。暴击率换算与末尾百分比加成的除数。</summary>
        public const float Hundred = 100.0f;

        /// <summary>1.1(指令里的立即数 0x3F8CCCCD)。额外命中加成倍率。</summary>
        public const float BonusHitRate = 1.1f;

        /// <summary>3.0(指令里的立即数 0x40400000)。暴击率的基础值。</summary>
        public const float CriticalBaseChance = 3.0f;

        /// <summary>暴击判定的取模(0..9999 的随机数与"暴击率 * 100"比较)。</summary>
        public const int CriticalRollModulus = 10000;

        /// <summary>随机幅度的取模与分段数:下标 = 随机数 % 1000,分 999 段。</summary>
        public const int SpreadRollModulus = 1000;

        /// <summary>随机幅度的分段数。</summary>
        public const int SpreadSteps = 999;

        private static float F(double value)
        {
            return (float)value;
        }

        /// <summary>
        /// x87 FISTP(截断)的整数转换:向零取整;超出 int 范围或为 NaN 时得到 int.MinValue("整数不确定值")。
        /// </summary>
        public static int TruncateToInt(double value)
        {
            if (double.IsNaN(value) || value >= 2147483648.0 || value <= -2147483649.0)
            {
                return int.MinValue;
            }
            return (int)value;
        }

        /// <summary>
        /// 计算一击的伤害(computeHpDamageSDC)。结果同时写入 <paramref name="p"/> 的输出字段:
        /// 是否暴击(+0x1CE)、额外命中加成量(+0x1D0)与最终伤害(+0x1E6)。随机种子只在局部副本上推进,不改结构里的种子。
        /// </summary>
        public static int Compute(DamageVariables p, DamageConstants k)
        {
            if (p == null)
            {
                throw new ArgumentNullException(nameof(p));
            }
            if (k == null)
            {
                throw new ArgumentNullException(nameof(k));
            }

            uint seed = p.Seed;
            byte kind = (byte)p.Kind;
            float d = Inflict(p, k);

            // 攻击自带元素与攻击的元素标记一致:加成。
            sbyte declared = p.DeclaredElement;
            if ((byte)declared != 4 && IsValidElement(p, declared))
            {
                d = F((double)d * SameElementBonus);
            }

            // 防御方是活动对象且攻击带元素:伤害改为各带元素里"折算后最大"的那一个(起点 -1,所以没有任何元素标记时不进入)。
            if (DamageObjectTypes.Is(p.DefenderType, DamageObjectTypes.Active) && !IsValidElement(p, 4))
            {
                float best = -1.0f;
                for (int i = 0; i < 4; i++)
                {
                    if (p.ByteAt(0x0A + i) != 0)
                    {
                        int resist = p.Int32At(0x7F + 4 * i);
                        float candidate = F((((double)k.CommonElementBase - (double)resist * p.ElementResistScale) * Percent) * d);
                        if (candidate > best)
                        {
                            best = candidate;
                        }
                    }
                }
                d = best;
            }

            // 随机幅度:物理/魔法取"底 + 等级 * 系数",无视防御取伤害的 7%,模块 14 不加。
            float spread = 0.0f;
            if (kind == 0)
            {
                spread = k.PhysicalRandomBase;
                int level = BasisLevel(p, p.UseBonusBase);
                spread = F((double)level * k.PhysicalRandomRate + spread);
            }
            else if (kind == 1)
            {
                spread = k.MagicalRandomBase;
                int level = BasisLevel(p, p.UseBonusBase);
                spread = F((double)level * k.MagicalRandomRate + spread);
            }
            else if (kind == 2)
            {
                spread = F((double)d * FixedSpreadRate);
            }
            if ((byte)p.Module == 0x0E)
            {
                spread = 0.0f;
            }
            int spreadRoll = SdcRandom.Next(ref seed);
            d = F((double)d + UniformVelocity(-spread, spread, spreadRoll % SpreadRollModulus, SpreadSteps));

            // 防御率(无视防御种类、被标记跳过防御、防御方不是活动对象时都不套用)。
            if (DamageObjectTypes.Is(p.DefenderType, DamageObjectTypes.Active) && kind != 2 && !p.IgnoreDefense)
            {
                DefenseRate(p, k, out float defenseRate, out float penetration);
                float multiplier = F((1.0 - (double)defenseRate) - (double)penetration * p.PenetrationScale);
                if (multiplier < k.MaxTotalDefenseRate)
                {
                    multiplier = k.MaxTotalDefenseRate;
                }
                if (multiplier < 0.0f)
                {
                    multiplier = 0.0f;
                }
                d = F((double)d * multiplier);
            }

            // 固定值加成与固定减伤、减伤率(物理与魔法各一套)。
            if (kind == 0)
            {
                d = FlatTerms(p, d, p.BonusBaseCountPhysical, p.PhysicalPercent, p.PhysicalFlatReduceA, p.PhysicalFlatReduceB, p.PhysicalReduceRate);
            }
            else if (kind == 1)
            {
                d = FlatTerms(p, d, p.BonusBaseCountMagical, p.MagicalPercent, p.MagicalFlatReduceA, p.MagicalFlatReduceB, p.MagicalReduceRate);
            }
            if (d < 0.0f)
            {
                d = 0.0f;
            }

            // 暴击(使用并推进同一个随机种子)。
            p.IsCritical = CheckCritical(p, k, ref d, ref seed);

            // 额外命中加成:乘 1.1,多出来的部分记下来。
            if (p.BonusHit)
            {
                p.BonusHitDamage = TruncateToInt((double)d * BonusHitRate - d);
                d = F((double)d * BonusHitRate);
            }

            d = F(((double)p.FinalPercentA * d) * Percent);
            d = F(((double)p.PowerPercent * d) * Percent);
            if (p.ConditionalFlag(0))
            {
                d = F(((double)p.ConditionalPercent(0) * Percent) * d);
            }
            if (p.ConditionalFlag(1))
            {
                d = F(((double)p.ConditionalPercent(1) * Percent) * d);
            }
            if (p.ConditionalFlag(2))
            {
                d = F(((double)p.ConditionalPercent(2) * Percent) * d);
            }
            d = F((double)p.FinalRate * d);
            if (d < 1.0f)
            {
                d = 1.0f;
            }

            // 防御方的"主人"是角色:把 26 个类型倍率依次乘上去(暴击用后一组)。
            if (DamageObjectTypes.Is(p.DefenderOwnerType, DamageObjectTypes.Character))
            {
                int baseIndex = p.IsCritical ? DamageVariables.TypeRateCount : 0;
                for (int i = 0; i < DamageVariables.TypeRateCount; i++)
                {
                    d = F((double)p.TypeRate(baseIndex + i) * d);
                }
            }

            bool attackerActive = DamageObjectTypes.Is(p.AttackerType, DamageObjectTypes.Active);
            if (attackerActive && p.AttackerRate != 1.0f)
            {
                d = F((double)p.AttackerRate * d);
            }
            if (attackerActive && p.AttackerFlat != 0)
            {
                d = F((double)p.AttackerFlat + d);
            }
            d = F((double)p.TailRate * d);

            bool attackerCharacter = DamageObjectTypes.Is(p.AttackerType, DamageObjectTypes.Character);
            if (p.QuarterBonusFlag && attackerCharacter)
            {
                int quarter = TruncateToInt((double)d * QuarterRate);
                d = F((double)quarter + d);
            }
            if (p.FlatAddFlag)
            {
                d = F((double)p.FlatAdd + d);
            }
            if (p.TailRate2Flag && attackerCharacter)
            {
                d = F((double)p.TailRate2 * d);
            }
            d = F((((double)p.TailPercent * d) / Hundred) + d);
            d = F((double)p.TailAdd + d);

            int result = TruncateToInt(d);
            p.Result = result;
            return result;
        }

        /// <summary>
        /// 固定值加成、固定减伤与减伤率(computeHpDamageSDC 里按攻击种类分开的那一段)。
        /// 加成项 = ((固定值基数 [+ 附加次数 * 单位量]) * (百分比加成)) * (种类百分比加成),加到伤害上;
        /// 防御方为活动对象且未跳过防御时,再减去 (A * 穿透缩放 + B);最后伤害 -= 伤害 * 减伤率。每一步都落地成 float。
        /// </summary>
        private static float FlatTerms(DamageVariables p, float damage, int bonusCount, float kindPercent, int reduceA, int reduceB, float reduceRate)
        {
            float bonus = p.FlatBonus;
            if (p.UseBonusBase)
            {
                bonus = F((double)bonusCount * p.BonusBaseScale + bonus);
            }
            bonus = F((((double)p.DamagePercent * Percent) + 1.0) * bonus);
            bonus = F((((double)kindPercent * Percent) + 1.0) * bonus);
            damage = F((double)damage + bonus);
            if (DamageObjectTypes.Is(p.DefenderType, DamageObjectTypes.Active) && !p.IgnoreDefense)
            {
                float reduction = (float)reduceA;
                reduction = F((double)p.PenetrationScale * reduction);
                reduction = F((double)reduceB + reduction);
                damage = F((double)damage - reduction);
            }
            return F((double)damage - (double)reduceRate * damage);
        }

        /// <summary>
        /// 命中对象分发(getDamageInflict @0x08095193):伤害对象是被动对象走被动对象路径(它又在"召唤物命中"开关齐备时走召唤物公式),
        /// 是召唤物走召唤物公式,其余走碰撞对象路径。
        /// </summary>
        public static float Inflict(DamageVariables p, DamageConstants k)
        {
            if (DamageObjectTypes.Is(p.DamagerType, DamageObjectTypes.Passive))
            {
                if (p.CreatureHit && p.CreatureSource)
                {
                    return InflictCreature(p, k);
                }
                return InflictCollision(p, k);
            }
            if (DamageObjectTypes.Is(p.DamagerType, DamageObjectTypes.Creature))
            {
                return InflictCreature(p, k);
            }
            return InflictCollision(p, k);
        }

        /// <summary>
        /// 碰撞对象路径的基础伤害(getDamageInflictCollisionObject @0x08095275),每步落地成 float。
        /// 基础值 [+ 附加基础值] → 乘 (百分比 * 0.01 + 1) → 乘种类百分比 → 乘 (攻击力 / 常量 + 1)(无视防御种类没有这一项)→ 加上 额外倍率 * 伤害。
        /// </summary>
        public static float InflictCollision(DamageVariables p, DamageConstants k)
        {
            byte kind = (byte)p.Kind;
            float x = p.Base;
            if (p.UseBonusBase)
            {
                if (kind == 0 || kind == 2)
                {
                    x = F((double)p.BonusBasePhysical + x);
                }
                else if (kind == 1)
                {
                    x = F((double)p.BonusBaseMagical + x);
                }
            }
            x = F((((double)p.DamagePercent * Percent) + 1.0) * x);
            if (kind == 0)
            {
                x = F((((double)p.PhysicalPercent * Percent) + 1.0) * x);
            }
            else if (kind == 1)
            {
                x = F((((double)p.MagicalPercent * Percent) + 1.0) * x);
            }
            if (kind == 0)
            {
                x = F(((double)p.PhysicalAttack / k.PhysicalDamageRate + 1.0) * x);
            }
            else if (kind == 1)
            {
                x = F(((double)p.MagicalAttack / k.MagicalDamageRate + 1.0) * x);
            }
            return F((double)p.ExtraRate * x + x);
        }

        /// <summary>
        /// 召唤物路径的基础伤害(getDamageInflictCreature @0x08095508):"召唤物命中"开关关闭得 0;
        /// 否则 基础值 * 等级倍率 * +0x2B 倍率 * 玩法模块倍率(模块 9 再乘召唤物序号的折减),每步落地成 float。
        /// </summary>
        public static float InflictCreature(DamageVariables p, DamageConstants k)
        {
            if (!p.CreatureHit)
            {
                return 0.0f;
            }
            float baseValue = p.Base;
            float rate = k.CreatureLevelRate(p.CreatureLevel, p.CreatureLevelAdd, TruncateToInt(p.CreatureExtra));
            float x = F((double)rate * baseValue);
            x = F((double)p.CreatureRate * x);
            x = F((double)k.CreatureModuleRate(p.Module) * x);
            if ((byte)p.Module == 9)
            {
                x = F((double)k.CreatureIndexRate(p.CreatureIndex) * x);
            }
            return x;
        }

        /// <summary>
        /// 防御率与穿透(getDefenseRateDamager @0x08095810):
        /// 防御力 = 整数防御力 * (防御百分比 * 0.01 + 1),穿透 = 穿透点数 * 0.01;防御被削弱时"每级减伤系数"翻倍;
        /// 防御率 = max(防御力,0) / max(等级 * 系数 + max(防御力,0), 1)。等级固定取攻击者的第一等级(+0x1B3)。
        /// </summary>
        public static void DefenseRate(DamageVariables p, DamageConstants k, out float rate, out float penetration)
        {
            byte kind = (byte)p.Kind;
            int level = BasisLevel(p, false);
            float defense = 0.0f;
            float reduce = 0.0f;
            float pen = 0.0f;
            if (kind == 0)
            {
                defense = (float)p.PhysicalDefense;
                defense = F(((double)p.PhysicalDefensePercent * Percent + 1.0) * defense);
                reduce = k.PhysicalReduceRate;
                pen = F((double)p.PhysicalPenetration * Percent);
            }
            else if (kind == 1)
            {
                defense = (float)p.MagicalDefense;
                defense = F(((double)p.MagicalDefensePercent * Percent + 1.0) * defense);
                reduce = k.MagicalReduceRate;
                pen = F((double)p.MagicalPenetration * Percent);
            }
            if (p.DefenseWeakened)
            {
                reduce = F((double)reduce + reduce);
            }
            float positive = 0.0f < defense ? defense : 0.0f;
            float denominator = F((double)level * reduce + positive);
            if (denominator < 1.0f)
            {
                denominator = 1.0f;
            }
            rate = F((double)positive / denominator);
            penetration = pen;
        }

        /// <summary>
        /// 暴击判定(checkIsCriticalSDC @0x08095A16):暴击率 = 3 + 各项加成(只算该算的),
        /// 随机数 % 10000 小于"暴击率 * 100"(严格小于,即 暴击率 * 100 &gt; 随机数)时暴击;暴击时伤害乘 (缩放 * 默认倍率 + 加成)。
        /// 随机种子推进一次(不论是否暴击)。返回并写入 +0x1CE。
        /// </summary>
        public static bool CheckCritical(DamageVariables p, DamageConstants k, ref float damage, ref uint seed)
        {
            byte kind = (byte)p.Kind;
            float chance = CriticalBaseChance;
            chance = F((double)p.CriticalRate + chance);
            if (p.CreatureSource)
            {
                if (DamageObjectTypes.Is(p.AttackerType, DamageObjectTypes.Creature))
                {
                    chance = F((double)p.CreatureCriticalStat + chance);
                }
            }
            else if (DamageObjectTypes.Is(p.AttackerType, DamageObjectTypes.Active))
            {
                if (kind == 0)
                {
                    chance = F((double)p.CriticalStatPhysical + chance);
                }
                else if (kind == 1)
                {
                    chance = F((double)p.CriticalStatMagical + chance);
                }
            }
            if (p.SkillCriticalFlag)
            {
                chance = F((double)p.SkillCriticalRate + chance);
                if (DamageObjectTypes.Is(p.AttackerType, DamageObjectTypes.Active))
                {
                    if (kind == 0)
                    {
                        chance = F((double)p.SkillCriticalStatPhysical + chance);
                    }
                    else if (kind == 1)
                    {
                        chance = F((double)p.SkillCriticalStatMagical + chance);
                    }
                }
            }
            if (DamageObjectTypes.Is(p.DefenderType, DamageObjectTypes.Active))
            {
                chance = F((double)chance - p.CriticalResist);
            }
            int roll = SdcRandom.Next(ref seed) % CriticalRollModulus;
            if ((double)chance * Hundred > roll)
            {
                float multiplier = k.CriticalDamageDefaultRate;
                if (DamageObjectTypes.Is(p.AttackerType, DamageObjectTypes.Active))
                {
                    multiplier = F((double)p.CriticalMultiplier * multiplier);
                }
                multiplier = F((double)p.CriticalAdd + multiplier);
                damage = F((double)damage * multiplier);
                p.IsCritical = true;
            }
            else
            {
                p.IsCritical = false;
            }
            return p.IsCritical;
        }

        /// <summary>
        /// 在 [a, b] 之间按下标均匀取值(GetUniformVelocityFloatSDC @0x088924C1):
        /// 下标 &gt;= 分段数得 b,下标 &lt;= 0 得 a,否则 a + (b - a) * 下标 / 分段数。结果不落地成 float(调用方把它加进伤害后才落地)。
        /// </summary>
        public static double UniformVelocity(float a, float b, int index, int count)
        {
            if (index >= count)
            {
                return b;
            }
            if (index <= 0)
            {
                return a;
            }
            return (((double)b - (double)a) * index) / count + a;
        }

        /// <summary>
        /// 取随机幅度用的等级(getComputeDamageBasisLevelAttacker @0x080957C3):
        /// 要求用第二等级且攻击者是角色时取 +0x1B4,否则取 +0x1B3(有符号字节)。
        /// </summary>
        public static int BasisLevel(DamageVariables p, bool useAlternate)
        {
            if (useAlternate && DamageObjectTypes.Is(p.AttackerType, DamageObjectTypes.Character))
            {
                return p.LevelAlt;
            }
            return p.Level;
        }

        /// <summary>
        /// 元素标记是否成立(isValidAttackInfoElement @0x0809576E):下标 4("无元素")表示四个元素标记全为 0;
        /// 其余下标就是读 +0x0A 起对应的那个字节。下标越界时原版读结构里相邻的字节(合法数据不会走到),这里按同样的偏移读。
        /// </summary>
        public static bool IsValidElement(DamageVariables p, int index)
        {
            if (index == 4)
            {
                return p.ByteAt(0x0A) == 0 && p.ByteAt(0x0B) == 0 && p.ByteAt(0x0C) == 0 && p.ByteAt(0x0D) == 0;
            }
            return p.ByteAt(0x0A + index) != 0;
        }
    }
}
