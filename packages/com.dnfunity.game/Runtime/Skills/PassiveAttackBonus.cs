using System;
using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 被动威力加成登记表属于哪一族。客户端有两套互不相通的实现:
    /// 通用角色类(登记 0x00689f70、读取 0x00684510,表在对象 +0x5938)与另一个角色类(登记 0x00bb8d60、读取 0x00bb85c0,表在对象 +0x6670);
    /// 脚本里用接口名的大小写区分(带大写首字母的是通用角色类,小写首字母的是另一类)。静态读码,非 REA。
    /// </summary>
    public enum PassiveBonusFamily
    {
        /// <summary>通用角色类:列取值按浮点参与累加(0x00684510)。</summary>
        Standard = 0,
        /// <summary>另一个角色类:列取值先截断成整数再换算(0x00bb85c0 在 0x009129d0 之后调用截断函数 0x013d83e0)。</summary>
        Truncating = 1,
    }

    /// <summary>
    /// 被动威力加成的规则(证据 E-181 的更正与 E-231 的更正)。
    /// 观察(静态读码,非 REA):
    /// (1) 表是 map&lt;技能号, [level info] 列下标&gt;:登记 0x00689f70 = 按技能号取槽位(0x00e46620,MSVC map 的下标运算)后写入列下标,
    ///     同一技能号再登记只覆盖;删除 0x00689f90 = 按技能号擦除(0x00554660);两个接口都没有"终止标志"参数。
    ///     遍历按技能号升序;读取函数里看到的 +0x15 字节是红黑树节点的"哨兵"标志(调试迭代器自增前的合法性检查),不是"终止整表"的标志。
    /// (2) 读取 0x00684510(技能号参数不使用)只按 native 状态过滤:表里每个技能,若脚本里能取到它的技能对象,
    ///     且它的 [executable states] 含该状态(0x0090dd10,空表为否),就把"该技能当前等级的第 列 列取值(0x009129d0 → 0x00911b90,
    ///     与 sq_GetLevelData 同一取值函数)× 0.01(0x01565cc0)"加到基础倍率上。不看"主动技能是谁",也不看"已学"(未学的技能等级为 0,取值越界为 0)。
    /// (3) 脚本消费它的接口:sq_GetBonusRateWithPassive(0x006846a0)= 技能第 idx 列取值 × 加成后倍率 − 100,再截断成整数(0x009138c0);
    ///     sq_GetPowerWithPassive / sq_SetAttackPowerWithPassive 把加成后倍率乘进威力。
    /// 推断:未学被动时表为空,加成后倍率 = 基础倍率,所以默认行为不变。
    /// </summary>
    public static class PassiveAttackBonus
    {
        /// <summary>0x01565cc0 处的单精度 0.01(经 0x011a0de0 读出,E-181 的模拟器测量)。</summary>
        public const float RateScale = 0.01f;

        /// <summary>0x01562e38 处的单精度 100.0(本机读 DNF.exe 磁盘字节,非 REA):0x009138c0 在乘完倍率后减去它。</summary>
        public const float PercentBase = 100f;

        /// <summary>
        /// 技能在第 <paramref name="column"/> 列、等级 <paramref name="level"/> 的取值;等级不是正数、或超过取值表的级数时为 0
        /// (0x00911b90 用"等级 − 1"和表的级数做无符号比较,不小于就返回 0;注意 <see cref="SkillLevelTable.Get"/> 对超出级数的等级取最后一级,两者不同,
        /// 所以这里先自己判断上界)。
        /// </summary>
        public static float LevelColumn(SkillDef def, int level, int column, bool pvp)
        {
            if (def == null || level <= 0 || column < 0)
            {
                return 0f;
            }
            SkillLevelTable table = pvp && def.PvpLevelInfo.Levels > 0 ? def.PvpLevelInfo : def.LevelInfo;
            if (level > table.Levels)
            {
                return 0f;
            }
            return def.GetLevel(level, column, pvp);
        }

        /// <summary>
        /// 0x00684510 / 0x00bb85c0:在 <paramref name="baseRate"/> 上按技能号升序累加。<paramref name="entries"/> 必须已按技能号升序。
        /// 通用类:rate = (浮点)(取值 × 0.01 + rate),乘加在 x87 里一次舍入;
        /// 另一类:取值先截断成整数,再按单精度 乘 0.01、加 rate(两次舍入)。
        /// </summary>
        public static float Accumulate(float baseRate, int nativeState, IReadOnlyList<KeyValuePair<int, int>> entries,
            Func<int, SkillDef> resolveSkill, Func<int, int> levelOf, PassiveBonusFamily family, bool pvp)
        {
            float rate = baseRate;
            if (entries == null || entries.Count == 0 || resolveSkill == null || levelOf == null)
            {
                return rate;
            }
            foreach (KeyValuePair<int, int> entry in entries)
            {
                SkillDef def = resolveSkill(entry.Key);
                if (def == null || !SkillPassivePowerRate.PassiveAppliesInNativeState(def, nativeState))
                {
                    continue;
                }
                float data = LevelColumn(def, levelOf(entry.Key), entry.Value, pvp);
                if (family == PassiveBonusFamily.Truncating)
                {
                    float whole = (int)data;
                    float scaled = whole * RateScale;
                    rate = scaled + rate;
                }
                else
                {
                    rate = (float)((double)data * (double)RateScale + (double)rate);
                }
            }
            return rate;
        }

        /// <summary>
        /// 0x009138c0 的主路径:截断( 取值 × 倍率 − 100 )。负数向零截断(0x013d83e0 是截断转换)。
        /// 客户端在该技能对象还带着另一个"关联技能对象"(对象 +0x30)时会再乘一个系数,系数来自 0x0084b3f0,没有读通,这里不建模。
        /// </summary>
        public static int BonusPercent(float levelValue, float rate)
        {
            return (int)((double)levelValue * (double)rate - (double)PercentBase);
        }
    }

    /// <summary>某个主角对象的被动威力登记表(两族各一张),按技能号升序,语义同 MSVC 的 map&lt;int,int&gt;。</summary>
    public sealed class PassiveAttackBonusMap
    {
        private readonly SortedDictionary<int, int> _standard = new SortedDictionary<int, int>();
        private readonly SortedDictionary<int, int> _truncating = new SortedDictionary<int, int>();

        private SortedDictionary<int, int> Of(PassiveBonusFamily family) =>
            family == PassiveBonusFamily.Truncating ? _truncating : _standard;

        /// <summary>登记:同一技能号再登记只覆盖列下标(map 下标运算)。</summary>
        public void Set(PassiveBonusFamily family, int skillId, int column)
        {
            Of(family)[skillId] = column;
        }

        /// <summary>删除:按技能号擦除;没有该键时什么也不做。返回是否真的擦掉了一项。</summary>
        public bool Remove(PassiveBonusFamily family, int skillId)
        {
            return Of(family).Remove(skillId);
        }

        public int Count(PassiveBonusFamily family) => Of(family).Count;

        /// <summary>按技能号升序的快照。</summary>
        public IReadOnlyList<KeyValuePair<int, int>> Entries(PassiveBonusFamily family)
        {
            var list = new List<KeyValuePair<int, int>>(Of(family));
            return list;
        }
    }
}
