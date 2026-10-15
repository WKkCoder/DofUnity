using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>读取 [damage fomula constant] / creatureCommon.cre 的结果。</summary>
    public sealed class DamageConstantsLoadReport
    {
        /// <summary>要找的标签找到了(creatureCommon 没有"必须出现"的标签,这一项恒为 true)。</summary>
        public bool Complete { get; internal set; }

        /// <summary>读数值时读到别的标记(或没有标记)的次数。原版这时取 0 并继续,这里同样。</summary>
        public int BadValues { get; internal set; }

        /// <summary>遇到原版不认识的标签时,它的名字;原版在这里中止加载并报错。没遇到为 null。</summary>
        public string UnknownTag { get; internal set; }
    }

    /// <summary>
    /// 伤害公式用到的全局常量(证据 E-049)。来源有三处:
    /// 一是 Etc/EtcParameter.etc 里 [damage fomula constant] 一节的 26 个浮点(服务端 importEtcParameterScriptForServer @0x080966FA 按文件顺序读入);
    /// 二是服务端程序里写死的两个初值(最大总防御率、默认暴击倍率,加载函数从不改它们);
    /// 三是 Creature/Common/creatureCommon.cre 里与召唤物伤害有关的三节([damage]、[module damage percent]、[assault creature damage penalty])。
    /// 原版数值只在运行时从用户自备的 PVF 读取,仓库里不保存。
    /// </summary>
    public sealed class DamageConstants
    {
        private const string FormulaTag = "[damage fomula constant]";

        private readonly List<int> _creatureDamageVector = new List<int>();
        private readonly Dictionary<int, float> _creatureModuleRates = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _creatureIndexRates = new Dictionary<int, float>();

        // ---- [damage fomula constant] 的 26 个浮点,按文件顺序 ----

        /// <summary>第 1 项:物理伤害常量(公式里"物理攻击力 / 它 + 1"的除数)。</summary>
        public float PhysicalDamageRate { get; set; }

        /// <summary>第 2 项:物理伤害附加常量(伤害公式不用)。</summary>
        public float PhysicalDamageAdd { get; set; }

        /// <summary>第 3 项:物理减伤常量(防御率里"等级 * 它 + 防御力"的系数)。</summary>
        public float PhysicalReduceRate { get; set; }

        /// <summary>第 4 项:物理减伤附加常量(伤害公式不用)。</summary>
        public float PhysicalReduceAdd { get; set; }

        /// <summary>第 5 项:物理随机幅度的底(幅度 = 底 + 等级 * 系数)。</summary>
        public float PhysicalRandomBase { get; set; }

        /// <summary>第 6 项:物理随机幅度的系数。</summary>
        public float PhysicalRandomRate { get; set; }

        /// <summary>第 7 项:魔法伤害常量。</summary>
        public float MagicalDamageRate { get; set; }

        /// <summary>第 8 项:魔法伤害附加常量(伤害公式不用)。</summary>
        public float MagicalDamageAdd { get; set; }

        /// <summary>第 9 项:魔法减伤常量。</summary>
        public float MagicalReduceRate { get; set; }

        /// <summary>第 10 项:魔法减伤附加常量(伤害公式不用)。</summary>
        public float MagicalReduceAdd { get; set; }

        /// <summary>第 11 项:魔法随机幅度的底。</summary>
        public float MagicalRandomBase { get; set; }

        /// <summary>第 12 项:魔法随机幅度的系数。</summary>
        public float MagicalRandomRate { get; set; }

        /// <summary>第 13 项:元素抗性折算的底(伤害 *= (底 - 抗性 * 缩放) * 0.01)。</summary>
        public float CommonElementBase { get; set; }

        /// <summary>第 14 项:通用元素常量(伤害公式不用)。</summary>
        public float CommonElementRate { get; set; }

        /// <summary>第 15 项:PVP 的元素常量(伤害公式不用)。</summary>
        public float CommonElementRateInPvp { get; set; }

        /// <summary>第 16 项:天梯 PVP 的元素常量(伤害公式不用)。</summary>
        public float CommonElementRateInLadderPvp { get; set; }

        /// <summary>第 17 项:HP 增长常量(伤害公式不用)。</summary>
        public float HpIncreaseRate { get; set; }

        /// <summary>第 18 项:HP 恢复常量(伤害公式不用)。</summary>
        public float HpRegenRate { get; set; }

        /// <summary>第 19 项:MP 增长常量(伤害公式不用)。</summary>
        public float MpIncreaseRate { get; set; }

        /// <summary>第 20 项:MP 恢复常量(伤害公式不用)。</summary>
        public float MpRegenRate { get; set; }

        /// <summary>第 21 项:状态异常的伤害常量(伤害公式不用)。</summary>
        public float ActiveStatusDamageRate { get; set; }

        /// <summary>第 22 项:状态异常的防御常量(伤害公式不用)。</summary>
        public float ActiveStatusDefenseRate { get; set; }

        /// <summary>第 23 项:物理防御附加常量(伤害公式不用)。</summary>
        public float PhysicalDefenseAdd { get; set; }

        /// <summary>第 24 项:魔法防御附加常量(伤害公式不用)。</summary>
        public float MagicalDefenseAdd { get; set; }

        /// <summary>第 25 项:HP 恢复附加倍率(伤害公式不用)。</summary>
        public float HpRegenRateAddRate { get; set; }

        /// <summary>第 26 项:MP 恢复附加倍率(伤害公式不用)。</summary>
        public float MpRegenRateAddRate { get; set; }

        // ---- 服务端程序里写死的初值(@0x0936E4A8 / @0x0936E4AC,加载函数从不写它们) ----

        /// <summary>最大总防御率:伤害乘以(1 - 防御率 - 穿透项)时,这个乘数的下限(随后再与 0 取大)。初值 0.01。</summary>
        public float MaxTotalDefenseRate { get; set; } = 0.01f;

        /// <summary>默认暴击倍率,暴击时伤害乘 (缩放 * 它 + 加成)。初值 1.5。</summary>
        public float CriticalDamageDefaultRate { get; set; } = 1.5f;

        // ---- creatureCommon.cre ----

        /// <summary>[damage] 一节的整数(召唤物等级伤害公式的参数,需要至少 7 个才生效)。</summary>
        public IReadOnlyList<int> CreatureDamageVector => _creatureDamageVector;

        /// <summary>替换 [damage] 向量(测试与手工构造用)。</summary>
        public void SetCreatureDamageVector(params int[] values)
        {
            _creatureDamageVector.Clear();
            _creatureDamageVector.AddRange(values);
        }

        /// <summary>
        /// 登记"玩法模块 → 召唤物伤害倍率"。已经登记过同一个模块时,新倍率先乘上旧的再覆盖(原版 addCreatureModuleRate 就是这么做的)。
        /// </summary>
        public void AddCreatureModuleRate(int module, float rate)
        {
            if (_creatureModuleRates.TryGetValue(module, out float old))
            {
                rate = (float)((double)rate * old);
            }
            _creatureModuleRates[module] = rate;
        }

        /// <summary>登记"召唤物序号 → 伤害折减倍率"。</summary>
        public void SetCreatureIndexRate(int index, float rate)
        {
            _creatureIndexRates[index] = rate;
        }

        /// <summary>某个玩法模块的召唤物伤害倍率;没登记为 1。</summary>
        public float CreatureModuleRate(int module)
        {
            return _creatureModuleRates.TryGetValue(module, out float rate) ? rate : 1.0f;
        }

        /// <summary>某个召唤物序号的伤害折减倍率;没登记为 1。</summary>
        public float CreatureIndexRate(int index)
        {
            return _creatureIndexRates.TryGetValue(index, out float rate) ? rate : 1.0f;
        }

        /// <summary>
        /// 召唤物的等级伤害倍率(服务端 getDamageRateCreature @0x080955E1):
        /// L = min(等级, 50) + 附加;rate = (((额外 + v0 + L*v1) * ((v2 + L*v3) / v4 + 1) + (v5 + L*v3) / v6) / 100)。
        /// 全部用双精度,中间量每步落地(汇编里每一步都是双精度存取),最后转成 float;向量不足 7 项时为 1。
        /// </summary>
        public float CreatureLevelRate(int level, int add, int extra)
        {
            if (level > 50)
            {
                level = 50;
            }
            int total = unchecked(level + add);
            double rate = 1.0;
            if (_creatureDamageVector.Count > 6)
            {
                List<int> v = _creatureDamageVector;
                double sum = v[0] + (double)extra;
                double lead = unchecked((double)v[1] * total) + sum;
                double ratioNumerator = unchecked((double)v[3] * total) + v[2];
                double product = (ratioNumerator / v[4] + 1.0) * lead;
                double tailNumerator = unchecked((double)v[3] * total) + v[5];
                rate = ((tailNumerator / v[6] + product) / 100.0) * rate;
            }
            return (float)rate;
        }

        // ---- 加载 ----

        /// <summary>
        /// 从 EtcParameter.etc 的标记流读 [damage fomula constant]:丢弃前面的标记直到找到这个标签,然后按顺序读 26 个浮点。
        /// 读到别的标记时原版取 0 继续,这里同样并计数。找不到标签时返回的报告 Complete 为 false(原版会一直读到结尾后卡住)。
        /// </summary>
        public DamageConstantsLoadReport LoadEtcParameter(ScriptCursor cursor)
        {
            if (cursor == null)
            {
                throw new ArgumentNullException(nameof(cursor));
            }
            var report = new DamageConstantsLoadReport();
            while (true)
            {
                cursor.ScanType(out string tag, false);
                if (tag == null)
                {
                    return report;
                }
                if (tag == FormulaTag)
                {
                    break;
                }
            }
            float Next()
            {
                float value = cursor.ScanFloat(out bool ok);
                if (!ok)
                {
                    report.BadValues++;
                }
                return value;
            }
            PhysicalDamageRate = Next();
            PhysicalDamageAdd = Next();
            PhysicalReduceRate = Next();
            PhysicalReduceAdd = Next();
            PhysicalRandomBase = Next();
            PhysicalRandomRate = Next();
            MagicalDamageRate = Next();
            MagicalDamageAdd = Next();
            MagicalReduceRate = Next();
            MagicalReduceAdd = Next();
            MagicalRandomBase = Next();
            MagicalRandomRate = Next();
            CommonElementBase = Next();
            CommonElementRate = Next();
            CommonElementRateInPvp = Next();
            CommonElementRateInLadderPvp = Next();
            HpIncreaseRate = Next();
            HpRegenRate = Next();
            MpIncreaseRate = Next();
            MpRegenRate = Next();
            ActiveStatusDamageRate = Next();
            ActiveStatusDefenseRate = Next();
            PhysicalDefenseAdd = Next();
            MagicalDefenseAdd = Next();
            HpRegenRateAddRate = Next();
            MpRegenRateAddRate = Next();
            report.Complete = true;
            return report;
        }

        /// <summary>
        /// 读 creatureCommon.cre(服务端 importCreatureCommonScript @0x08A383F6)。顶层循环与原版一致:
        /// 每次读一个标签;不是标签开头(夹着游离的值)就停止,按"加载成功"返回;标签不认识则报告并中止。
        /// 单值标签按原版的个数读掉;[damage] / [mana] / 两个"成对"的节读到非期望类型为止(那个标记被消耗,
        /// 正好是它的结束标签)。只有伤害有关的三节被保存。
        /// </summary>
        public DamageConstantsLoadReport LoadCreatureCommon(ScriptCursor cursor)
        {
            if (cursor == null)
            {
                throw new ArgumentNullException(nameof(cursor));
            }
            var report = new DamageConstantsLoadReport { Complete = true };
            _creatureDamageVector.Clear();
            while (cursor.ScanType(out string tag, true))
            {
                bool ok;
                switch (tag)
                {
                    case "[attack constant]":
                        Count(cursor.ScanFloat(out ok), ok, report);
                        break;
                    case "[response delay]":
                    case "[second per fill point spend]":
                    case "[second per fill point charge]":
                    case "[MAX fill point]":
                    case "[fill point to eat feed]":
                    case "[evolution fade in delay]":
                    case "[evolution fade out delay]":
                    case "[evolution delay]":
                    case "[dying delay]":
                        Count(cursor.ScanInt(out ok), ok, report);
                        break;
                    case "[ambient in room list]":
                    case "[ambient in main game]":
                    case "[ambient in pvp]":
                        Count(cursor.ScanInt(out ok), ok, report);
                        Count(cursor.ScanInt(out ok), ok, report);
                        break;
                    case "[damage]":
                        while (true)
                        {
                            int value = cursor.ScanInt(out ok);
                            if (!ok)
                            {
                                break;
                            }
                            _creatureDamageVector.Add(value);
                        }
                        break;
                    case "[mana]":
                        while (true)
                        {
                            cursor.ScanInt(out ok);
                            if (!ok)
                            {
                                break;
                            }
                        }
                        break;
                    case "[module damage percent]":
                        _creatureModuleRates.Clear();
                        while (cursor.ScanStr(out string moduleTag))
                        {
                            int percent = cursor.ScanInt(out ok);
                            if (!ok)
                            {
                                break;
                            }
                            AddCreatureModuleRate(ModuleTypeFromTag(moduleTag), (float)((double)percent / 100.0));
                        }
                        break;
                    case "[assault creature damage penalty]":
                        _creatureIndexRates.Clear();
                        while (true)
                        {
                            int index = cursor.ScanInt(out ok);
                            if (!ok)
                            {
                                break;
                            }
                            int percent = cursor.ScanInt(out ok);
                            if (!ok)
                            {
                                break;
                            }
                            _creatureIndexRates[index] = (float)((double)percent / 100.0);
                        }
                        break;
                    default:
                        report.UnknownTag = tag;
                        report.Complete = false;
                        return report;
                }
            }
            return report;
        }

        private static void Count(float value, bool ok, DamageConstantsLoadReport report)
        {
            _ = value;
            if (!ok)
            {
                report.BadValues++;
            }
        }

        private static void Count(int value, bool ok, DamageConstantsLoadReport report)
        {
            _ = value;
            if (!ok)
            {
                report.BadValues++;
            }
        }

        // 服务端 MODULE_NAME_TAG 表里每个模块的"开始标签"(静态初始化 @0x088C3055 逐项写入,观察);
        // getModuleTypeFromTag 取第一个相等的下标,所以同名标签只会得到最小的那个模块号。
        private static readonly string[] ModuleTags =
        {
            "[dungeon]", "[dungeon]", "[dungeon]", "[dungeon]", "[pvp]", "[pvp]", "[dungeon]", "[warroom]",
            "[dungeon]", "[assault]", "[death tower]", "[dungeon]", "[dungeon]", "[dungeon]", "[dungeon]", "[boss tower]",
            string.Empty,
        };

        /// <summary>玩法模块的开始标签 → 模块号(第一个相等的下标);不认识返回 -1。</summary>
        public static int ModuleTypeFromTag(string tag)
        {
            for (int i = 0; i < ModuleTags.Length; i++)
            {
                if (ModuleTags[i] == tag)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
