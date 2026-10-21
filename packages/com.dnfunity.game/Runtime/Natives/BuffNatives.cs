using System;
using System.Collections.Generic;
using Dnf.Squirrel;

// 证据:每个接口的状态与依据见 docs/native-audit.tsv(E-046、E-048)。
namespace Dnf.Game
{
    /// <summary>
    /// 属性改变依附体(buff/debuff)与依附体的杂项接口。属性改变只记录参数、按有效时间到期,不改变战斗属性(属性系统还没有)。
    /// </summary>
    public sealed partial class GameWorld
    {
        /// <summary>未挂到对象上的属性改变在虚拟机里保留的最长时间(毫秒),避免被丢弃的对象无限累积。</summary>
        private const int LooseChangeStatusLifetimeMs = 10 * 60 * 1000;

        private ChangeStatus NewChangeStatus(string name, int validTimeMs)
        {
            var cs = new ChangeStatus { Name = name, ValidTimeMs = validTimeMs };
            Transient(cs, LooseChangeStatusLifetimeMs);
            return cs;
        }

        private void AttachChangeStatus(ChangeStatus cs, GameObject parent, GameObject source)
        {
            if (cs == null || parent == null || !parent.Valid)
            {
                return;
            }
            cs.Parent = parent;
            cs.Source = source;
            cs.ElapsedMs = 0;
            cs.Valid = true;
            if (!parent.ChangeStatuses.Contains(cs))
            {
                parent.ChangeStatuses.Add(cs);
            }
        }

        /// <summary>推进对象身上属性改变的计时,到期的移除。</summary>
        private void AdvanceChangeStatuses(GameObject obj, int dtMs)
        {
            if (obj.ChangeStatuses.Count == 0)
            {
                return;
            }
            for (int i = obj.ChangeStatuses.Count - 1; i >= 0; i--)
            {
                ChangeStatus cs = obj.ChangeStatuses[i];
                cs.ElapsedMs += dtMs;
                if (!cs.Valid || (cs.ValidTimeMs > 0 && cs.ElapsedMs >= cs.ValidTimeMs))
                {
                    cs.Valid = false;
                    obj.ChangeStatuses.RemoveAt(i);
                }
            }
        }

        private static ChangeStatusParameter ParameterOf(NativeCall c, int first)
        {
            return new ChangeStatusParameter(c.Int32(first), c.BoolOr(first + 1, false), c.FloatOr(first + 2, 0f));
        }

        private void RegisterBuffNatives()
        {
            const NativeForm M = NativeForm.Member;
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus T = NativeStatus.Trace;

            DefineFree("sq_CreateChangeStatus", I, "创建属性改变依附体 (类型, 是否按比例, 数值, 有效毫秒):还没有挂到任何对象上", c =>
            {
                ChangeStatus cs = NewChangeStatus(null, c.Int32(3));
                cs.Parameters.Add(ParameterOf(c, 0));
                return c.Return(cs);
            });
            Define("addParameter", I, "属性改变依附体:再加一条 (类型, 是否按比例, 数值)", M, c =>
            {
                c.SelfAs<ChangeStatus>().Parameters.Add(ParameterOf(c, 0));
                return c.ReturnNothing();
            });
            Define("clearParameter", I, "属性改变依附体:清空参数", M, c =>
            {
                c.SelfAs<ChangeStatus>().Parameters.Clear();
                return c.ReturnNothing();
            });
            Define("setValidTime", I, "设置有效时间(毫秒;属性改变依附体或依附体),返回自己以便连着写", M, c =>
            {
                if (c.Self is ChangeStatus cs)
                {
                    cs.ValidTimeMs = c.Int32(0);
                }
                else if (c.Self is Appendage a)
                {
                    a.ValidTimeMs = c.Int32(0);
                }
                return c.Return(c.Self);
            });
            Define("getValidTime", I, "读取有效时间(毫秒)", M, c => c.Return(c.Self is ChangeStatus cs ? cs.ValidTimeMs : c.Self is Appendage a ? a.ValidTimeMs : 0));
            Define("getAppendageInfo", I, "依附体信息:就是依附体自己(getValidTime、setValidTime 直接作用在它身上)", M, c => c.Return(c.Self));
            Define("setAppendCauseSkill", T, "记录依附体由哪个技能引起 (种类, 职业, 技能, 等级)", M, c =>
            {
                if (c.Self is ChangeStatus cs)
                {
                    cs.CauseKind = c.Int32(0);
                    cs.SkillIndex = c.Int32(2);
                }
                else if (c.Self is Appendage a)
                {
                    a.SkillIndex = c.Int32(2);
                }
                return c.ReturnNothing();
            });
            Define("sq_GetSkillIndex", I, "依附体对应的技能编号", M, c => c.Return(c.Self is Appendage a ? a.SkillIndex : c.Self is ChangeStatus cs ? cs.SkillIndex : -1));
            Define("sq_getChangeStatus", I, "依附体名下按名字挂的属性改变;没有为 null", M, c =>
                c.Return(c.SelfAs<Appendage>().NamedChangeStatuses.TryGetValue(c.Str(0), out ChangeStatus cs) && cs.Valid ? cs : null));
            Define("sq_AddChangeStatus", I, "依附体写法:按名字加一个属性改变并挂到对象上 (名字, 对象, 来源, 有效毫秒, 类型, 是否按比例, 数值);属性改变对象写法:追加一项 (类型, 是否按比例, 数值)", M, c =>
            {
                if (c.Self is ChangeStatus existing)
                {
                    existing.Parameters.Add(ParameterOf(c, 0));
                    return c.Return(existing);
                }
                Appendage a = c.SelfAs<Appendage>();
                ChangeStatus cs = NewChangeStatus(c.Str(0), c.Int32(3));
                cs.Parameters.Add(ParameterOf(c, 4));
                a.NamedChangeStatuses[c.Str(0)] = cs;
                AttachChangeStatus(cs, c.Arg<GameObject>(1), c.Arg<GameObject>(2));
                return c.Return(cs);
            });
            Define("sq_AddChangeStatusAppendageID", I,
                "依附体写法:加一个属性改变并挂到对象上 (被依附者, 来源, 有效毫秒, 类型, 是否按比例, 数值, 依附体编号);没有名字,按默认名 changeStatus 登记(推断)", M, c =>
                {
                    Appendage a = c.SelfAs<Appendage>();
                    ChangeStatus cs = NewChangeStatus("changeStatus", c.Int32(2));
                    cs.Parameters.Add(ParameterOf(c, 3));
                    cs.AppendageId = (int)c.IntOr(6, 0);
                    a.NamedChangeStatuses["changeStatus"] = cs;
                    AttachChangeStatus(cs, c.Arg<GameObject>(0), c.Arg<GameObject>(1));
                    return c.Return(cs);
                });
            DefineFree("sq_RemoveChangeStatus", I, "让属性改变失效:参数是属性改变对象 (属性改变),或 (对象, 依附体编号) 移除对象身上带这个编号的全部", c =>
            {
                GameObject owner = c.Arg<GameObject>(0);
                if (owner != null)
                {
                    int id = c.Int32(1);
                    for (int i = owner.ChangeStatuses.Count - 1; i >= 0; i--)
                    {
                        if (owner.ChangeStatuses[i].AppendageId == id)
                        {
                            owner.ChangeStatuses[i].Valid = false;
                            owner.ChangeStatuses.RemoveAt(i);
                        }
                    }
                    return c.ReturnNothing();
                }
                ChangeStatus cs = c.Arg<ChangeStatus>(0);
                if (cs != null)
                {
                    cs.Valid = false;
                    cs.Parent?.ChangeStatuses.Remove(cs);
                }
                return c.ReturnNothing();
            });

            // ---- 把已创建的依附体挂到对象上(CNSquirrelAppendage.sq_Append / sq_AppendAppendageID)
            SqNativeTable t = AppendageTable;
            DefineStatic(t, "sq_Append", I, "把创建好的属性改变依附体挂到对象上 (依附体, 被依附者, 来源)", c =>
            {
                AttachChangeStatus(c.Arg<ChangeStatus>(0), c.Arg<GameObject>(1), c.Arg<GameObject>(2));
                return c.ReturnNothing();
            });
            DefineStatic(t, "sq_AppendAppendageID", I, "同 sq_Append,另带依附体编号和一个标志", c =>
            {
                ChangeStatus cs = c.Arg<ChangeStatus>(0);
                if (cs != null && c.Has(3))
                {
                    cs.AppendageId = c.Int32(3);
                }
                AttachChangeStatus(cs, c.Arg<GameObject>(1), c.Arg<GameObject>(2));
                return c.ReturnNothing();
            });
            Define("sq_Append", I, "属性改变对象写法:cs.sq_Append(被依附者, 来源[, 依附体编号, 标志, 回调]);等同 CNSquirrelAppendage.sq_Append(cs, 被依附者, 来源)", M, c =>
            {
                ChangeStatus cs = c.SelfAs<ChangeStatus>();
                if (c.Has(2) && c.TypeOf(2) == SqType.Integer)
                {
                    cs.AppendageId = c.Int32(2);
                }
                AttachChangeStatus(cs, c.Arg<GameObject>(0), c.Arg<GameObject>(1));
                return c.ReturnNothing();
            });
            Define("GetSquirrelAppendage", I, "对象身上某个路径的依附体 (脚本路径);没有为 null", M, c =>
                c.Return(c.Self is GameObject o ? FindAppendage(o, c.Str(0)) : null));
            Define("sq_DeleteAppendages", T, "删除依附体名下挂的属性改变", M, c =>
            {
                Appendage a = c.SelfAs<Appendage>();
                foreach (ChangeStatus cs in a.NamedChangeStatuses.Values)
                {
                    cs.Valid = false;
                    cs.Parent?.ChangeStatuses.Remove(cs);
                }
                a.NamedChangeStatuses.Clear();
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_AddEffectFront", "sq_DeleteEffectFront" })
            {
                Define(n, T, "依附体的前景特效:只记数", M, c => c.ReturnNothing());
            }

            // ---- 光环(范围内的敌人被吸引等):占位
            foreach (string n in new[] { "sq_AddAuraMaster", "sq_AddSquirrelAuraMaster" })
            {
                Define(n, T, "在依附体名下挂一个光环 (名字, 对象, 来源, 范围, ……):只返回占位对象", M, c =>
                {
                    Appendage a = c.SelfAs<Appendage>();
                    var aura = new ServiceObject("AuraMaster");
                    Transient(aura, LooseChangeStatusLifetimeMs);
                    a.Auras[c.Str(0)] = aura;
                    return c.Return(aura);
                });
            }
            foreach (string n in new[] { "sq_getAuraMaster", "sq_getSquirrelAuraMaster" })
            {
                Define(n, T, "依附体名下按名字挂的光环;没有为 null", M, c =>
                    c.Return(c.SelfAs<Appendage>().Auras.TryGetValue(c.Str(0), out ServiceObject aura) ? aura : null));
            }
            Define("setAttractionInfo", T, "光环:吸引范围内的敌人 (速度 x, 速度 y, 范围, ……):只记数", M, c => c.ReturnNothing());
        }
    }
}
