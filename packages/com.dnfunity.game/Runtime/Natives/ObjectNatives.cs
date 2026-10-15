using Dnf.Data;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>对象本身的原生接口:位置、朝向、状态、父子关系、动画、移动。语义依据接口名与脚本里的用法推断,逐项标注可信程度。</summary>
    public sealed partial class GameWorld
    {
        private void RegisterAllNatives()
        {
            RegisterObjectNatives();
            RegisterMotionNatives();
            RegisterSkillNatives();
            RegisterVarNatives();
            RegisterEffectNatives();
            RegisterAppendageNatives();
            RegisterMathNatives();
            RegisterPacketNatives();
            RegisterPassiveNatives();
            RegisterAnimationNatives();
            RegisterMiscNatives();
            RegisterQueryNatives();
            RegisterAttackNatives();
            RegisterBuffNatives();
            RegisterSupportNatives();
        }

        /// <summary>
        /// "取值"类接口要操作的对象:全局写法传入 null 时为 null,调用方据此返回 -1;类型不符仍然报错。
        /// 依据:客户端这类接口(sq_GetXPos、sq_GetState、sq_GetDirection、sq_GetObjectHeight、sq_GetGroup、sq_GetUniqueId、sq_GetObjectId、sq_getJob、
        /// sq_getGrowType 等 17 个)的实现入口都先判空,为空直接返回 -1,不报错(E-046)。
        /// </summary>
        private static GameObject ObjOrNull(NativeCall c)
        {
            return c.Self == null ? null : c.SelfAs<GameObject>();
        }

        private void RegisterObjectNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeStatus I = NativeStatus.Inferred;

            foreach (string n in new[] { "getXPos", "sq_GetXPos", "GetXPos" })
            {
                Define(n, I, "对象的 x 坐标;全局写法传入 null 返回 -1(客户端 0x005adf40)", B, c => c.Return(ObjOrNull(c)?.X ?? -1));
            }
            foreach (string n in new[] { "getYPos", "sq_GetYPos", "GetYPos" })
            {
                Define(n, I, "对象的 y(纵深)坐标;全局写法传入 null 返回 -1(客户端 0x005adf60)", B, c => c.Return(ObjOrNull(c)?.Y ?? -1));
            }
            foreach (string n in new[] { "getZPos", "sq_GetZPos" })
            {
                Define(n, I, "对象的 z(高度)坐标;全局写法传入 null 返回 -1(客户端 0x005adf80)", B, c => c.Return(ObjOrNull(c)?.Z ?? -1));
            }
            foreach (string n in new[] { "setCurrentPos", "SetPos" })
            {
                Define(n, I, "设置对象的 (x, y, z) 坐标;第 4 个参数(若有)忽略;粒子发生器等占位对象忽略", NativeForm.Member, c =>
                {
                    if (c.Self is StubObject)
                    {
                        return c.ReturnNothing();
                    }
                    GameObject o = c.SelfAs<GameObject>();
                    o.X = c.Int32(0);
                    o.Y = c.Int32(1);
                    o.Z = c.Int32(2);
                    return c.ReturnNothing();
                });
            }
            DefineAny("sq_SetCurrentPos", I, "设置对象的 (x, y, z) 坐标 (对象, x, y, z):成员写法 obj.sq_SetCurrentPos(obj, ……) 与全局写法实参相同;粒子发生器等占位对象忽略", c =>
            {
                GameObject o = c.Arg<GameObject>(0);
                if (o != null)
                {
                    o.X = c.Int32(1);
                    o.Y = c.Int32(2);
                    o.Z = c.Int32(3);
                }
                return c.ReturnNothing();
            });
            Define("sq_setCurrentAxisPos", I, "设置对象某个轴的坐标 (轴 0 是 x、1 是 y、2 是 z, 值)", B, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                switch (c.Int32(0))
                {
                    case 0:
                        o.X = c.Int32(1);
                        break;
                    case 1:
                        o.Y = c.Int32(1);
                        break;
                    case 2:
                        o.Z = c.Int32(1);
                        break;
                }
                return c.ReturnNothing();
            });
            Define("sq_SetCurrentDirection", I, "设置对象的朝向 (对象, 朝向);粒子发生器等占位对象忽略", NativeForm.Global, c =>
            {
                if (c.Self is StubObject)
                {
                    return c.ReturnNothing();
                }
                c.SelfAs<GameObject>().Direction = c.Int32(0);
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "getDirection", "sq_GetDirection" })
            {
                Define(n, I, "对象的朝向(ENUM_DIRECTION_*);全局写法传入 null 返回 -1(客户端 0x005addb0)", B, c => c.Return(ObjOrNull(c)?.Direction ?? -1));
            }
            Define("setCurrentDirection", I, "设置对象的朝向", B, c =>
            {
                c.SelfAs<GameObject>().Direction = c.Int32(0);
                return c.ReturnNothing();
            });
            Define("setDirection", I, "设置对象的朝向", B, c =>
            {
                c.SelfAs<GameObject>().Direction = c.Int32(0);
                return c.ReturnNothing();
            });
            DefineFree("sq_GetOppositeDirection", I, "相反的朝向:左变右、右变左,其余原样", c =>
            {
                int d = c.Int32(0);
                return c.Return(d == Direction.Left ? Direction.Right : d == Direction.Right ? Direction.Left : d);
            });
            foreach (string n in new[] { "getState", "sq_GetState", "sq_GetSTATE" })
            {
                Define(n, I, "对象当前的状态编号;全局写法传入 null 返回 -1(客户端 0x005adc10)", B, c => c.Return(ObjOrNull(c)?.State ?? -1));
            }
            foreach (string n in new[] { "isMyControlObject", "sq_IsMyControlObject" })
            {
                Define(n, NativeStatus.Verified,
                    "是不是本机玩家控制的对象:客户端(0x007f9eb0)看对象的\"所有者\"(虚表 0x1B0 槽)是不是本机玩家的角色;角色的所有者是它自己,"
                    + "玩家创建的被动对象沿创建者找到所属角色,怪物不是。单机里:玩家角色与它创建的被动对象为真。"
                    + "客户端还有一个全局模式字段(值为 2 时无条件为真),含义没有查明,这里当作不是 2", B, c => c.Return(c.Self is GameObject o && o.IsMyControl));
            }
            Define("getParent", NativeStatus.Verified, "对象的父对象(依附体:被依附的对象);没有则 null(客户端 0x011d5270 读对象里偏移 4 的指针)", B, c =>
            {
                if (c.Self is Appendage a)
                {
                    return c.Return(a.Parent);
                }
                return c.Return(c.SelfAs<GameObject>().Parent);
            });
            Define("getSource", I, "依附体的来源对象(发起者);对象的话取它的来源", B, c =>
            {
                if (c.Self is Appendage a)
                {
                    return c.Return(a.Source);
                }
                return c.Return(c.SelfAs<GameObject>().Source);
            });
            Define("setValid", I, "依附体:设置是否有效(设为假会在下一次推进时结束);对象:设为假即销毁", B, c =>
            {
                bool valid = c.Bool(0);
                if (c.Self is Appendage a)
                {
                    a.Valid = valid;
                }
                else if (c.Self is GameObject o && !valid)
                {
                    Destroy(o);
                }
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "isValid" })
            {
                Define(n, I, "对象或依附体是否有效", B, c => c.Return(c.Self is Appendage a ? a.Valid : c.Self is GameObject o && o.Valid));
            }
            foreach (string n in new[] { "getObjectHeight", "sq_GetObjectHeight" })
            {
                Define(n, I, "对象的高度;全局写法传入 null 返回 -1(客户端 0x005ae590)", B, c => c.Return(ObjOrNull(c)?.Height ?? -1));
            }
            Define("sq_GetObjectTime", I, "对象创建后经过的毫秒数", B, c => c.Return(c.SelfAs<GameObject>().AgeMs));
            Define("getSkillSubState", NativeStatus.Verified, "技能子状态(技能脚本自定义的阶段编号;客户端 0x0042b0f0 读对象里一个受保护的整数,偏移 0x5468)", B, c => c.Return(c.SelfAs<GameObject>().SkillSubState));
            Define("setSkillSubState", I, "设置技能子状态", B, c =>
            {
                c.SelfAs<GameObject>().SkillSubState = c.Int32(0);
                return c.ReturnNothing();
            });
            Define("isDead", I, "对象是否死亡(生命值不大于 0)", B, c => c.Return(c.SelfAs<GameObject>().Hp <= 0));
            Define("getHp", I, "当前生命值", B, c => c.Return(c.SelfAs<GameObject>().Hp));
            Define("resetHitObjectList", I, "清空本次攻击已命中的对象列表(允许再次命中)", B, c =>
            {
                c.SelfAs<GameObject>().HitObjects.Clear();
                return c.ReturnNothing();
            });
        }

        private void RegisterMotionNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeStatus I = NativeStatus.Inferred;

            foreach (string n in new[] { "sq_SetCurrentAnimation", "sq_setCurrentAnimation", "setCurrentAnimation" })
            {
                Define(n, NativeStatus.Verified,
                    "按 CUSTOM_ANI_* 下标设置当前动画:下标是角色 .chr 或被动对象 .obj 里 [etc motion] 的下标(E-036);参数是动画对象时改播那个动画。同时清空命中列表", B, c =>
                    {
                        GameObject o = c.SelfAs<GameObject>();
                        string path = c.TypeOf(0) == SqType.Table ? c.Arg<AnimationObject>(0)?.Animator.Path : o.Def?.CustomAnimationPath(c.Int32(0));
                        o.HitObjects.Clear();
                        if (!SetAnimation(o, path))
                        {
                            Errors.Add("动画不存在:" + o.Name + " " + (c.TypeOf(0) == SqType.Table ? "(动画对象)" : "CUSTOM_ANI " + c.Int32(0)));
                        }
                        return c.ReturnNothing();
                    });
            }
            foreach (string n in new[] { "sq_StopMove", "sq_stopMove" })
            {
                Define(n, I, "停止静态移动", B, c =>
                {
                    StaticMove m = c.SelfAs<GameObject>().Move;
                    m.Active = false;
                    m.SpeedX = 0;
                    m.SpeedY = 0;
                    m.RemainderX = 0;
                    m.RemainderY = 0;
                    return c.ReturnNothing();
                });
            }
            foreach (string n in new[] { "sq_SetStaticMoveInfo", "sq_setStaticMoveInfo" })
            {
                Define(n, I,
                    "静态移动参数 (轴, 速度, 加速度, 标志):轴 0 是 x、1 是 y;速度单位为每秒距离,按速度立即移动(加速度忽略)。第 5、6 个参数(若有)忽略", B, c =>
                    {
                        StaticMove m = c.SelfAs<GameObject>().Move;
                        int axis = c.Int32(0);
                        int speed = c.Int32(1);
                        int accel = c.Int32(2);
                        if (axis == 0)
                        {
                            m.SpeedX = speed;
                            m.AccelX = accel;
                        }
                        else if (axis == 1)
                        {
                            m.SpeedY = speed;
                            m.AccelY = accel;
                        }
                        m.Active = true;
                        return c.ReturnNothing();
                    });
            }
            Define("setTimeEvent", I,
                "设置计时事件 (编号, 间隔毫秒, 次数, 是否立刻触发):同编号的会被替换;到期时调用 onTimeEvent_后缀(obj, 编号, 第几次),回调返回真则取消该事件", B, c =>
                {
                    c.SelfAs<GameObject>().TimeEvents.Set(c.Int32(0), c.Int32(1), c.Int32(2), c.BoolOr(3, false));
                    return c.ReturnNothing();
                });
            Define("stopTimeEvent", I, "取消一个计时事件", B, c =>
            {
                c.SelfAs<GameObject>().TimeEvents.Remove(c.Int32(0));
                return c.ReturnNothing();
            });
            Define("isExistTimeEvent", I, "这个编号的计时事件是否还在", B, c => c.Return(c.SelfAs<GameObject>().TimeEvents.Exists(c.Int32(0))));
            foreach (string n in new[] { "sq_SetMoveDirection", "sq_setMoveDirection" })
            {
                Define(n, I, "静态移动的方向 (x 方向, y 方向),取值 ENUM_DIRECTION_*,NEUTRAL 表示不动", B, c =>
                {
                    StaticMove m = c.SelfAs<GameObject>().Move;
                    m.DirectionX = c.Int32(0);
                    m.DirectionY = c.Int32(1);
                    m.Active = true;
                    return c.ReturnNothing();
                });
            }
        }
    }
}
