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

    // --- r9-ps 读码注释:被动刷新 flushPassiveSkills(角色虚槽 +0x7dc)的触发时机图。只记静态读码结论,不新增接线,默认行为不变。
    // 标记:【观】= 有地址的观察;【推】= 推断;【未】= 未知。逐条地址与方法局限见工作树笔记 out/ps/PROGRESS.md(被 .gitignore 忽略,不入库)。
    // 入口:11 张角色虚表的 +0x7dc 全部 = 基类 0x00857900,没有职业覆盖。它遍历 255 个技能槽,getSkill(槽 +0x804)返回对象、且对象 +0xd4 受保护整数 == 1 的槽(推断:被动),
    //   无论等级都以(槽号,当前等级)调 +0x980(IRDSQRCharacter 实现 0x006885d0 → 脚本 ProcPassiveSkill_<职业>);登记 0x00689f70 只发生在脚本的 skill_level > 0 分支里,
    //   ProcPassiveSkill 里没有对称删除,读取按当前等级(0 → 0),所以过期登记无害,漏掉某个刷新点最多让"登记晚发生"。
    // 【观】惯用次序:addSkill(+0x7cc)→ flushSkillParameter(+0x7d8)→ flushPassiveSkills(+0x7dc)。以下都只对主角 [0x01ac790c](除注明外)。
    // 学习类:BUY_SKILL 0x0071d220(回包成功字节非 0;站点 0x0071d521/0x0071d5d6)、REFUND_SKILL 0x00719220(0x007193bd)、换技能页 CHANGE_ANOTHER_SKILL_TREE 0x0070c270 → 0x00851310(0x0085137f)、
    //   技能信息整包 SKILLINFO 0x007484e0 → 0x00851500(套当前页后刷新,0x0085155c)、完成任务 FINISH_QUEST 0x00724460 → 0x00851500(0x00724e03,进入条件【未】)。
    //   技能重置 NOTIPACKET_SKILL_INIT 0x007320b0 只弹提示、不刷新;随后由 SKILLINFO 触发只是【推】。
    // 升级类:EXP 包 0x0073f040 仅当包内等级 ≠ 记录等级(0x0073f2c5)时,对双技能页各 addSkill(0xae, 等级) 后刷新(0x0073f541/0x0073f58e);
    //   USERINFO 0x00515970 → 0x00512ab0 在本机等级上升时调 0x00814ed0(同样三步,0x00814f5b/0x00814f9b);USERINFO 里其他玩家的记录经 0x005135a0 → 0x00851500。
    // 重置/进出副本:角色虚槽 +0x434(resetDungeonStart)的 11 个类全部汇到基类 0x0086cc60,它在 0x0086cdbb 无条件刷新,与原因/标志参数无关。
    //   调用点(原因,标志,第三参):START_GAME 0x007407d0(2,0,1)、CNTownModule::onEnterModule 0x01113e40(2,x,1)、对战准备状态(0x01111840、0x0110b8e0 等,(2,0,1))、
    //   死亡/复活(DIE_STATE 0x00741280、WARROOM_DIE_STATE 0x0073c4a0、PVP_REVIVE 0x0073d530,原因 1)、录像回放 0x0096ff30、Tag 系统(START_MAP 链 0x00749c50 → 0x008f1800 → 0x008f0e60)。
    // 模块切换:changeModule 0x007615a0 → onChangeModule 0x00760e70 → 0x0075e870 → 对全体角色调 0x0086e4d0(重绑 255 个技能对象后刷新,0x0086e521)。
    //   仅当 to ∈ {1,3,4,5,7,9,10,11,12,13,14,16}、from ∉ {0,2},且全局标签串(0x01a6ee64)≠ 表[to](0x01d2d4f0)时才执行:
    //   模块 SELECT_CHARACTER/TOWN/SELECT_DUNGEON/MAIN_GAME/SELECT_CHANNEL/LOGIN/BLOOD_SYSTEM/DESPAIR_TOWER/BOSS_TOWER/ADVANCE_ALTAR 都是 "[dungeon]",PVP 与 FAIR_PVP "[pvp]",
    //   WARROOM "[warroom]",ASSAULT "[assault]",DEAD_TOWER "[death tower]",TOURNAMENT 为空串;所以村↔副本(TOWN↔MAIN_GAME)不触发。
    // 创建装配:AI 角色创建走虚槽 +0x9f4 → 0x00844a10(站点 0x00845253;START_MAP 链 0x00749c50 → 0x007e8510 → 0x007e7450 → 0x00843740 可达);
    //   TAG_CHARACTER_INFO → 0x006f1fd0、START_MAP → 0x006fca00(Ep1::setTutorialData)、UDP 包 0x0115abf0、回放连招 0x00972b20,直接或经 0x00851500 刷新。
    // 换装:角色虚槽 +0x548(装入,基类 0x00867c20)在 0x00867f1b、+0x550(取/脱槽位物品,基类 0x00865740)在 0x00865870、物品类虚槽 +0x110(基类 0x00795a60)在 0x00795bfd 刷新;
    //   条件:+0x548 须新旧物品不同且 0x0082d5a0(角色,槽)为真,+0x550 须槽位物品非空且 0x0082d5a0 为真,+0x110 须槽位状态在 0 与非 0 之间变化或被强制。
    //   已登记处理函数里主角接收者的调用点:UPDATE_ITEM_LIST 0x00753720(0x00753a3f、0x00753b3b)、DROP_ITEM / SELL_ITEM / DELETE_ITEM、COMPOUND_ITEM(及 _BY_EXPERT_JOB)、
    //   FINISH_QUEST、PC_ROOM_RENT_ITEM 的"卸下/消耗已穿戴物品"分支(各自的 +0x550 站点见笔记)、USE_RIGHT_OF_CHANGE_GROW_TYPE 0x00f88c40;
    //   onChangeModule 0x00760e70 在 from == 3 且条件成立时按槽重装(0x007613b2);雨天活动 EVENT_INFO 0x0074ddb0 → 0x00762330 → 0x00457bf0(武器槽取下再装回);
    //   角色信息包的整套装备:USERINFO 0x00515970 → 0x00514c30 → 0x007523c0(0x0075299e、0x00752adc)、TAG_CHARACTER_INFO 0x006f6670 → 0x006f7fb0 → 0x007523c0。
    //   另见:0x0084cdc0(状态/附加物改 [this+0x52fc] 后刷新)、0x0084df00(骑乘对象状态 5 时对骑乘者)、0x00864c10(虚槽 +0x72c,选角/地图开始链上)。
    // 【未】玩家主动穿脱的主流程:A#0x13 MOVE_ITEMSPACE 0x007088c0 与 B#0xd ITEM_LIST 0x00752b70 的函数体里没有换装调用点;+0x548 的 123 个调用函数里,
    //   接收者取自主角全局变量的只有 8 个,其余约 100 个(UI 类为主,接收者来自参数/成员指针)没追;
    //   脚本侧是否调用(只抽了部分职业的 211 个 .nut,0 处调用);受保护区 0x01dd9000–0x02c98000 里的调用点(按规定不读);公会成员信息 0x0072e9d0 写的 [hero+0x52f0] 的含义。
    //   直接调用图看不到虚调用与回调,以上是下界。时机图不完整,所以本轮不接线(见笔记第 4 节)。

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
