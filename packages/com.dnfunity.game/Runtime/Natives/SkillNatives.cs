using System;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>技能与状态相关的原生接口:技能等级与数据表、状态切换、攻击信息、整数数组。</summary>
    public sealed partial class GameWorld
    {
        /// <summary>技能数据归属的角色:被动对象等没有职业的对象,沿父对象向上找到有职业的那一个。</summary>
        private static GameObject SkillOwner(GameObject o)
        {
            GameObject cur = o;
            int guard = 0;
            while (cur != null && cur.Job < 0 && guard++ < 8)
            {
                cur = cur.Parent;
            }
            return cur ?? o;
        }

        private SkillDef SkillOf(GameObject o, int skillId)
        {
            GameObject owner = SkillOwner(o);
            return owner.Job < 0 ? null : Data.GetSkill(owner.Job, skillId);
        }

        private AttackInfo ResolveAttackInfo(NativeObject self)
        {
            if (self is AttackInfo ai)
            {
                return ai;
            }
            return self is GameObject o ? o.CurrentAttackInfo : null;
        }

        private void RegisterSkillNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeStatus I = NativeStatus.Inferred;

            foreach (string n in new[] { "sq_GetSkillLevel" })
            {
                Define(n, I,
                    "技能等级(未学为 0);全局写法传入 null 返回 -1。客户端 0x005ae4f0:空则 -1,否则取对象的 getSkillLevel(技能)(虚表槽 0x7d0,E-046);"
                    + "只有角色类的虚表里这个槽有实现(0x0084EE40),别的类的虚表没有这一项,所以脚本只会对角色调用;对非角色取所属角色是这里的推断(E-099)", B, c =>
                {
                    GameObject o = ObjOrNull(c);
                    return c.Return(o == null ? -1 : SkillOwner(o).GetSkillLevel(c.Int32(0)));
                });
            }
            foreach (string n in new[] { "sq_GetIntData", "sq_getIntData" })
            {
                Define(n, NativeStatus.Verified,
                    "技能静态数据 [static data] 的第 idx 项:参数 (技能, idx[, 等级]),等级忽略(静态数据不分等级);只有一个参数 (idx) 时按当前状态登记的技能取;决斗场模式取 [pvp] 里的(E-036)。"
                    + "全局写法传入 null 返回 -1,技能不存在返回 0(客户端 0x005ae4b0;真正取值的函数 0x00911930 只是跳到受保护区域,没有读)", B, c =>
                    {
                        GameObject o = ObjOrNull(c);
                        if (o == null)
                        {
                            return c.Return(-1);
                        }
                        if (c.ArgCount == 1)
                        {
                            GameObject owner = SkillOwner(o);
                            int current = o.StateInfo != null ? o.StateInfo.Skill : (owner.StateInfo != null ? owner.StateInfo.Skill : -1);
                            SkillDef own = SkillOf(o, current);
                            return c.Return(own == null ? 0 : own.GetStatic(c.Int32(0), PvpMode));
                        }
                        SkillDef def = SkillOf(o, c.Int32(0));
                        return c.Return(def == null ? 0 : def.GetStatic(c.Int32(1), PvpMode));
                    });
            }
            foreach (string n in new[] { "sq_GetLevelData", "sq_getLevelData" })
            {
                Define(n, NativeStatus.Verified,
                    "技能按等级取值表 [level info] 的第 idx 列:参数 (技能, idx[, 等级]);省略等级时取该技能当前等级;只有一个参数时按当前状态登记的技能取(E-036)。"
                    + "全局写法传入 null 返回 -1,技能不存在返回 0;结果是整数(客户端 0x005ae460 把浮点结果截断成整数;真正取值的函数 0x00911b90 在受保护区域里,没有读,"
                    + "所以是否还叠加了被动技能等加成不明)", B, c =>
                    {
                        GameObject o = ObjOrNull(c);
                        if (o == null)
                        {
                            return c.Return(-1);
                        }
                        int skill;
                        int column;
                        int level;
                        GameObject owner = SkillOwner(o);
                        if (c.ArgCount == 1)
                        {
                            // sq_GetLevelData(idx):技能取自当前状态的登记
                            skill = o.StateInfo != null ? o.StateInfo.Skill : (owner.StateInfo != null ? owner.StateInfo.Skill : -1);
                            column = c.Int32(0);
                            level = owner.GetSkillLevel(skill);
                        }
                        else
                        {
                            skill = c.Int32(0);
                            column = c.Int32(1);
                            level = c.ArgCount >= 3 ? c.Int32(2) : owner.GetSkillLevel(skill);
                        }
                        SkillDef def = SkillOf(o, skill);
                        return c.Return(def == null ? 0 : def.GetLevel(level, column, PvpMode));
                    });
            }
            foreach (string n in new[] { "sq_GetBonusRateWithPassive", "sq_getBonusRateWithPassive" })
            {
                Define(n, I,
                    "技能第 idx 列的攻击力百分比乘以倍率:参数 (技能, 状态, idx, 倍率);被动技能加成尚未实现,按 1 倍计(列注释为'攻击力(%)')", B, c =>
                    {
                        GameObject o = c.SelfAs<GameObject>();
                        int skill = c.Int32(0);
                        int column = c.Int32(2);
                        float rate = c.FloatOr(3, 1f);
                        GameObject owner = SkillOwner(o);
                        SkillDef def = SkillOf(o, skill);
                        int level = owner.GetSkillLevel(skill);
                        return c.Return(def == null ? 0f : def.GetLevel(level, column, PvpMode) * rate);
                    });
            }
            Define("sq_IsUseSkill", I, "玩家是否请求了这个技能且条件满足(这里:请求的技能编号与参数相同)", B, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                return c.Return(o.RequestedSkill >= 0 && o.RequestedSkill == c.Int32(0));
            });
            Define("sq_IsCommandEnable", I, "这个技能的指令当前是否可用(默认可用;setSkillCommandEnable 可以禁用)", B, c =>
            {
                return c.Return(!c.SelfAs<GameObject>().DisabledSkillCommands.Contains(c.Int32(0)));
            });
            Define("setSkillCommandEnable", I, "启用或禁用某个技能的指令", B, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                if (c.Bool(1))
                {
                    o.DisabledSkillCommands.Remove(c.Int32(0));
                }
                else
                {
                    o.DisabledSkillCommands.Add(c.Int32(0));
                }
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_AddSetStatePacket", "sq_addSetStatePacket" })
            {
                Define(n, NativeStatus.Verified,
                    "请求对象切换状态 (状态, 优先级, 是否带数据):第 3 个参数为真时随请求发出对象当前的数据数组(sq_IntVectPush 攒的那个),为假时不带数据(onSetState 收到空数组);回调结束后处理。"
                    + "客户端 0x0067cba0(sq_addSetStatePacket 在另一个类里是 0x00BB1500,数组位置不同,逻辑一样):真走 setState(状态, 数据数组, 优先级),假走 setState(状态, 空, 优先级),"
                    + "后两个参数都给 0(E-046、E-099)。'请求'与'执行'之间的优先级规则(单槽、优先级不低于待处理的才替换、死亡后拒绝)见 QueueState", B, c =>
                {
                    GameObject o = c.SelfAs<GameObject>();
                    bool sendData = c.BoolOr(2, false);
                    // 状态执行时的 isResetTimer 实参:客户端的执行函数(0x008360e0)的第 3 个参数,为真就重置状态计时器;
                    // 它的调用点没有一个给常量 0(见 PassiveNatives 里的统计),请求路径上的取值没有读到,按 true(推断)
                    QueueState(o, c.Int32(0), c.Int32(1), true, sendData ? o.DataVector.Values.ToArray() : Array.Empty<long>());
                    return c.ReturnNothing();
                });
            }
            Define("sq_IntVectClear", NativeStatus.Verified,
                "清空对象的数据数组:客户端 0x006844e0 与 0x00BB85B0(两个类)对 this+0x59D8、this+0x6708 处的容器做“从头删到尾”(0x01389550),E-048", B, c =>
            {
                c.SelfAs<GameObject>().DataVector.Values.Clear();
                return c.ReturnNothing();
            });
            Define("sq_IntVectPush", I,
                "往对象的数据数组末尾加一个整数:客户端 0x006844f0 与 0x00BB8CB0 往同一个容器(this+0x59D8、this+0x6708)追加,追加函数 0x00493770 是受保护区的跳板、没有读,所以“追加到末尾”与元素宽度只是按名字和参数形状推断(E-048)", B, c =>
            {
                c.SelfAs<GameObject>().DataVector.Values.Add(c.Int(0));
                return c.ReturnNothing();
            });
            // 越界或数组无效时是 -1,不是 0:客户端 0x005b80a0(全局写法)与 0x00bb2c20(成员写法)的实现都是"数组非空且下标小于长度才取值,否则返回 -1"(E-046)
            DefineAny("sq_GetVectorData", NativeStatus.Verified,
                "取整数数组的第 i 项(越界、为负或数组无效为 -1):obj.sq_GetVectorData(数组, i) 与 sq_GetVectorData(数组, i) 等价(客户端 0x005b80a0、0x00bb2c20,E-046)", c =>
                c.Return(c.Arg<IntVector>(0) is IntVector v ? v.Get(c.Int(1), -1) : -1));
            DefineAny("sq_getVectorData", NativeStatus.Verified, "同 sq_GetVectorData", c =>
                c.Return(c.Arg<IntVector>(0) is IntVector v ? v.Get(c.Int(1), -1) : -1));

            // ---- 攻击信息
            Define("sq_SetCurrentAttackInfo", NativeStatus.Verified,
                "按 CUSTOM_ATTACK_INFO_* 下标设置当前攻击信息:下标是角色 .chr 里 [etc attack info] 的下标(E-036);同时清空命中列表(推断)", B, c =>
                {
                    GameObject o = c.SelfAs<GameObject>();
                    UseAttackInfo(o, c);
                    return c.ReturnNothing();
                });
            Define("sq_setCurrentAttackInfo", NativeStatus.Verified, "同 sq_SetCurrentAttackInfo", B, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                UseAttackInfo(o, c);
                return c.ReturnNothing();
            });
            Define("sq_GetCurrentAttackInfo", I,
                "对象当前的攻击信息对象;对象为 null 时返回 null(客户端 0x005ae8e0:非空才调用对象的第 0x204 号虚函数,空则返回 0)。"
                + "虚函数(所有对象类共用 0x00442C60)返回对象内嵌的攻击信息(对象 +0x2D4 处),所以客户端里它永远不是空;这里没设过攻击信息时返回 null,是已知差异(E-099)", B, c =>
                c.Return(c.Self is GameObject o ? o.CurrentAttackInfo : null));
            foreach (string n in new[] { "sq_SetCurrentAttackBonusRate", "sq_setCurrentAttackBonusRate" })
            {
                Define(n, I, "设置攻击倍率(单位依脚本:列注释为 %):参数可以是攻击信息或对象", B, c =>
                {
                    AttackInfo ai = ResolveAttackInfo(c.Self);
                    if (ai != null)
                    {
                        ai.BonusRate = c.Float(0);
                    }
                    return c.ReturnNothing();
                });
            }
            Define("sq_GetCurrentAttackBonusRate", I, "读取攻击倍率;没设置过为 0", B, c =>
            {
                AttackInfo ai = ResolveAttackInfo(c.Self);
                return c.Return(ai != null && ai.BonusRate.HasValue ? ai.BonusRate.Value : 0f);
            });
            Define("sq_SetCurrentAttackPower", I, "设置攻击力", B, c =>
            {
                AttackInfo ai = ResolveAttackInfo(c.Self);
                if (ai != null)
                {
                    ai.AttackPower = c.Float(0);
                }
                return c.ReturnNothing();
            });
            Define("sq_SetCurrentAttackeHitStunTime", I, "设置受击硬直时间(毫秒;接口名里的 e 是原样拼写)", B, c =>
            {
                AttackInfo ai = ResolveAttackInfo(c.Self);
                if (ai != null)
                {
                    ai.HitStunTimeMs = c.Int32(0);
                }
                return c.ReturnNothing();
            });
        }

        /// <summary>
        /// sq_SetCurrentAttackInfo 的参数:整数是 [etc attack info] 的下标;攻击信息对象(例如 sq_GetCustomAttackInfo 取到的)则直接换上它。
        /// </summary>
        private void UseAttackInfo(GameObject o, NativeCall c)
        {
            if (c.TypeOf(0) == Dnf.Squirrel.SqType.Table)
            {
                AttackInfo ai = c.Arg<AttackInfo>(0);
                if (ai != null)
                {
                    if (o.CurrentAttackInfo != null && !ReferenceEquals(o.CurrentAttackInfo, ai))
                    {
                        Release(o.CurrentAttackInfo);
                    }
                    o.CurrentAttackInfo = ai;
                    o.HitObjects.Clear();
                }
                return;
            }
            SetCurrentAttackInfo(o, o.Def?.CustomAttackInfoPath(c.Int32(0)), "custom:" + c.Int32(0));
        }

        private void SetCurrentAttackInfo(GameObject o, string path, string source)
        {
            AttackInfoDef def = Data.GetAttackInfo(path);
            if (def == null)
            {
                Errors.Add("攻击信息不存在:" + o.Name + " " + source);
            }
            var ai = new AttackInfo { Def = def, Source = source };
            if (o.CurrentAttackInfo != null)
            {
                Release(o.CurrentAttackInfo);
            }
            o.CurrentAttackInfo = ai;
            o.HitObjects.Clear();
        }
    }
}
