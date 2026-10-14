using System;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 攻击信息的取得与改写:按下标取 [etc attack info]、[attack info] 的一份、设置击飞与击退等字段、技能威力的查询。
    /// 字段只做记录(<see cref="AttackInfo.Fields"/>),伤害结算还没有实现;威力与倍率的单位、换算**未验证**。
    /// </summary>
    public sealed partial class GameWorld
    {
        private AttackInfo NewAttackInfo(string path, string source)
        {
            AttackInfoDef def = path == null ? null : Data.GetAttackInfo(path);
            if (def == null)
            {
                Errors.Add("攻击信息不存在:" + source);
            }
            var ai = new AttackInfo { Def = def, Source = source };
            Transient(ai);
            return ai;
        }

        private void SetField(NativeObject self, string name, float value)
        {
            AttackInfo ai = ResolveAttackInfo(self);
            if (ai != null)
            {
                ai.Fields[name] = value;
            }
        }

        private void RegisterAttackNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeForm M = NativeForm.Member;
            const NativeForm G = NativeForm.Global;
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus T = NativeStatus.Trace;

            Define("sq_GetCustomAttackInfo", I, "按 [etc attack info] 的下标取一份攻击信息(不改变当前攻击信息) (对象, 下标)", G, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                int index = c.Int32(0);
                return c.Return(NewAttackInfo(o.Def?.CustomAttackInfoPath(index), o.Name + " custom:" + index));
            });
            foreach (string n in new[] { "sq_GetDefaultAttackInfo", "getDefaultAttackInfo" })
            {
                Define(n, I, "角色:按 [attack info] 的下标取一份基础攻击信息 (下标);被动对象不带参数:取 .obj 的 [attack info]", B, c =>
                {
                    GameObject o = c.SelfAs<GameObject>();
                    if (!c.Has(0) && o.Def is PassiveObjectDef passive)
                    {
                        return c.Return(NewAttackInfo(passive.AttackInfoPath, o.Name + " default"));
                    }
                    int index = c.Int32(0);
                    return c.Return(NewAttackInfo(o.Character?.AttackInfoPath(index), o.Name + " basic:" + index));
                });
            }
            Define("sq_SetCurrentAttackInfoFromCustomIndex", I, "按 [etc attack info] 的下标设置当前攻击信息 (对象, 下标)", G, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                SetCurrentAttackInfo(o, o.Def?.CustomAttackInfoPath(c.Int32(0)), "custom:" + c.Int32(0));
                return c.ReturnNothing();
            });

            // ---- 记录型字段
            foreach (var (name, field) in new[]
            {
                ("sq_SetCurrentAttacknUpForce", "UpForce"),
                ("sq_SetCurrentAttacknBackForce", "BackForce"),
                ("sq_SetAttackInfoForceHitStunTime", "ForceHitStunTime"),
                ("sq_SetCurrentAttackeDamageAct", "DamageAct"),
                ("sq_setAttackInfoType", "InfoType"),
                ("setAttackType", "AttackType"),
                ("setElement", "Element"),
                ("sq_SetAttackInfoHitDelayRateDamager", "HitDelayRateDamager"),
                ("sq_SetAddWeaponDamage", "AddWeaponDamage"),
                ("setAttackXVelocity", "AttackXVelocity"),
                ("setAttackXVelocityFast", "AttackXVelocityFast"),
                ("setAttackXAccel", "AttackXAccel"),
                ("setAttackXAccelFast", "AttackXAccelFast"),
            })
            {
                string key = field;
                Define(name, T, "攻击信息字段:只记录,不参与结算(" + key + ")", name.StartsWith("sq_", StringComparison.Ordinal) ? B : M, c =>
                {
                    SetField(c.Self, key, c.FloatOr(0, 0f));
                    return c.ReturnNothing();
                });
            }
            Define("sq_SetCustomDamageType", T, "攻击信息:自定义伤害类型 (信息, 类型, 值):只记录", G, c =>
            {
                SetField(c.Self, "CustomDamageType" + c.Int32(0), c.FloatOr(1, 0f));
                return c.ReturnNothing();
            });
            foreach (var (name, field) in new[]
            {
                ("sq_GetCurrentAttackeDamageAct", "DamageAct"),
                ("sq_GetAttackType", "AttackType"),
                ("sq_GetAttackInfoHitDelayRateDamager", "HitDelayRateDamager"),
            })
            {
                string key = field;
                Define(name, T, "读取攻击信息字段(" + key + "):没设置过为 0", G, c =>
                {
                    AttackInfo ai = ResolveAttackInfo(c.Self);
                    return c.Return(ai != null && ai.Fields.TryGetValue(key, out float v) ? v : 0f);
                });
            }
            Define("sq_ChangeAttackTypeToOpposite", T, "攻击类型取反:只记录", G, c =>
            {
                AttackInfo ai = ResolveAttackInfo(c.Self);
                if (ai != null)
                {
                    ai.Fields["AttackTypeOpposite"] = 1f;
                }
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_SetChangeStatusIntoAttackInfo", "sq_SetChangeStatusIntoAttackInfoWithEtc" })
            {
                Define(n, T, "给攻击信息附加属性改变 (信息, 槽, 类型, 概率, 持续, 是否比例, 值):只记录类型与值", G, c =>
                {
                    AttackInfo ai = ResolveAttackInfo(c.Self);
                    ai?.ChangeStatuses.Add(new ChangeStatusParameter(c.Int32(1), false, c.FloatOr(c.ArgCount - 1, 0f)));
                    return c.ReturnNothing();
                });
            }

            // ---- 技能威力
            Define("sq_GetPowerWithPassive", I,
                "技能威力 (技能, 状态, 列, 等级, 倍率):[level info] 第 列 列乘以倍率;等级为 -1 取技能当前等级。被动技能的加成没有实现;第 4 个参数的含义是推断", B, c =>
                {
                    GameObject o = c.SelfAs<GameObject>();
                    return c.Return(PowerFromLevelTable(o, c.Int32(0), c.Int32(2), c.IntOr(3, -1), c.FloatOr(4, 1f)));
                });
            Define("sq_getPowerWithPassive", I, "同 sq_GetPowerWithPassive", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                return c.Return(PowerFromLevelTable(o, c.Int32(0), c.Int32(2), c.IntOr(3, -1), c.FloatOr(4, 1f)));
            });
            Define("sq_setAttackPowerWithPassive", I,
                "把技能威力写进当前攻击信息 (技能, 状态, 列, 等级, 倍率):列小于 0 时不改;规则同 sq_GetPowerWithPassive", M, c =>
                {
                    GameObject o = c.SelfAs<GameObject>();
                    int column = c.Int32(2);
                    if (column >= 0 && o.CurrentAttackInfo != null)
                    {
                        o.CurrentAttackInfo.AttackPower = PowerFromLevelTable(o, c.Int32(0), column, c.IntOr(3, -1), c.FloatOr(4, 1f));
                    }
                    return c.ReturnNothing();
                });
            DefineFree("sq_GetAttackBonusRate", I, "技能对象的攻击倍率 (技能对象, 列, 倍率):[level info] 第 列 列在技能当前等级的值乘以倍率;被动加成没有实现(依据 sq_GetAttackBonusRate(skill, 0, 1.0) 的写法推断)", c =>
            {
                SkillObject sk = c.Arg<SkillObject>(0);
                if (sk == null || sk.Owner == null)
                {
                    return c.Return(0f);
                }
                return c.Return(PowerFromLevelTable(sk.Owner, sk.SkillId, c.Int32(1), -1, c.FloatOr(2, 1f)));
            });
            Define("addHitObject", I, "把目标记为本次攻击已命中过(之后不再命中)", M, c =>
            {
                GameObject target = c.Arg<GameObject>(0);
                if (target != null)
                {
                    c.SelfAs<GameObject>().HitObjects.Add(target.Id);
                }
                return c.ReturnNothing();
            });
            Define("sq_AddHitObject", I, "同 addHitObject (攻击者, 目标)", G, c =>
            {
                GameObject target = c.Arg<GameObject>(0);
                if (target != null)
                {
                    c.SelfAs<GameObject>().HitObjects.Add(target.Id);
                }
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_SetAttackBoundingBoxSizeRate", "sq_ClearAttackBox", "sq_AddAttackBox", "sq_SendHitObjectPacketWithNoStuck", "sq_SendHitObjectPacketByAttackInfo" })
            {
                Define(n, T, "判定框的缩放与增删、特殊的命中发送:只记数", G, c => c.ReturnNothing());
            }
        }

        private float PowerFromLevelTable(GameObject o, int skill, int column, long levelArg, float rate)
        {
            GameObject owner = SkillOwner(o);
            SkillDef def = SkillOf(o, skill);
            if (def == null || column < 0)
            {
                return 0f;
            }
            int level = levelArg < 0 ? owner.GetSkillLevel(skill) : (int)levelArg;
            return def.GetLevel(level, column, PvpMode) * rate;
        }
    }
}
