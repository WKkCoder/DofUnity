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
                Define(n, I, "技能等级(未学为 0)", B, c =>
                {
                    GameObject owner = SkillOwner(c.SelfAs<GameObject>());
                    return c.Return(owner.GetSkillLevel(c.Int32(0)));
                });
            }
            foreach (string n in new[] { "sq_GetIntData", "sq_getIntData" })
            {
                Define(n, NativeStatus.Verified,
                    "技能静态数据 [static data] 的第 idx 项:参数 (技能, idx[, 等级]),等级忽略(静态数据不分等级);只有一个参数 (idx) 时按当前状态登记的技能取;决斗场模式取 [pvp] 里的(E-036)", B, c =>
                    {
                        GameObject o = c.SelfAs<GameObject>();
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
                    "技能按等级取值表 [level info] 的第 idx 列:参数 (技能, idx[, 等级]);省略等级时取该技能当前等级;只有一个参数时按当前状态登记的技能取(E-036)", B, c =>
                    {
                        GameObject o = c.SelfAs<GameObject>();
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
                Define(n, I, "把'切换状态(状态, 优先级, 是否重置计时器)'发给对象;当前数据数组随包发出(回调结束后处理)", B, c =>
                {
                    QueueState(c.SelfAs<GameObject>(), c.Int32(0), c.Int32(1), c.BoolOr(2, false));
                    return c.ReturnNothing();
                });
            }
            Define("sq_IntVectClear", I, "清空对象的数据数组", B, c =>
            {
                c.SelfAs<GameObject>().DataVector.Values.Clear();
                return c.ReturnNothing();
            });
            Define("sq_IntVectPush", I, "往对象的数据数组末尾加一个整数", B, c =>
            {
                c.SelfAs<GameObject>().DataVector.Values.Add(c.Int(0));
                return c.ReturnNothing();
            });
            DefineAny("sq_GetVectorData", I, "取整数数组的第 i 项(越界或数组无效为 0):obj.sq_GetVectorData(数组, i) 与 sq_GetVectorData(数组, i) 等价", c =>
                c.Return(c.Arg<IntVector>(0) is IntVector v ? v.Get(c.Int(1)) : 0));
            DefineAny("sq_getVectorData", I, "同 sq_GetVectorData", c =>
                c.Return(c.Arg<IntVector>(0) is IntVector v ? v.Get(c.Int(1)) : 0));

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
            Define("sq_GetCurrentAttackInfo", I, "对象当前的攻击信息对象", B, c => c.Return(c.SelfAs<GameObject>().CurrentAttackInfo));
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
