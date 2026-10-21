using System;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 被动对象的创建与销毁、对象之间发送的"切换状态"消息、全局整数数组、对象的"组 + 唯一编号"标识。
    /// 创建、销毁与改变技能特效是世界级消息,在发出它的回调结束后处理;切换状态消息进目标对象自己的队列。
    /// </summary>
    public sealed partial class GameWorld
    {
        private IntVector _globalIntVector;

        /// <summary>脚本里的 sq_GetGlobalIntVector():全局共用的一个整数数组,常和 sq_AddSetStatePacket 一类接口配合使用(先清空、再压入、再发送)。</summary>
        public IntVector GlobalIntVector
        {
            get
            {
                if (_globalIntVector == null)
                {
                    _globalIntVector = new IntVector();
                    Handle(_globalIntVector);
                }
                return _globalIntVector;
            }
        }

        private static long[] ToArray(IntVector v)
        {
            return v == null ? Array.Empty<long>() : v.Values.ToArray();
        }

        /// <summary>
        /// sq_SendCreatePassiveObjectPacket 一族的共同实现。实参(不含对象):编号、一个未知的 0、x、y、z[、朝向];
        /// 没有朝向参数时用创建者的朝向。依据 40 个脚本里的 58 + 14 处调用核对了实参顺序,第二个参数在所有调用里几乎都是 0(含义未知)。
        /// </summary>
        private int SendCreatePassive(NativeCall c, bool absolute, bool nullIsMinusOne = false)
        {
            if (nullIsMinusOne && c.Self == null)
            {
                return c.Return(-1); // 客户端 0x005ae780:对象为空直接返回 -1,不报错(E-048)
            }
            GameObject creator = c.SelfAs<GameObject>();
            int id = c.Int32(0);
            int ox = c.Int32(2);
            int oy = c.Int32(3);
            int oz = c.Int32(4);
            int dir = c.Has(5) && !c.IsNull(5) ? c.Int32(5) : creator.Direction;
            byte[] data = WrittenData();
            QueueWorldPacket(() => CreatePassiveObject(creator, id, ox, oy, oz, dir, absolute, data));
            return c.Return(0);
        }

        private void RegisterPassiveNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeForm M = NativeForm.Member;
            const NativeForm G = NativeForm.Global;
            const NativeStatus I = NativeStatus.Inferred;

            // ---- 整数数组
            DefineFree("sq_GetGlobalIntVector", NativeStatus.Verified,
                "全局共用的整数数组:客户端 0x005adb90 返回固定地址 0x01A4DBC4 处的同一个对象(E-048)", c => c.Return(GlobalIntVector));
            DefineFree("sq_IntVectorClear", I, "清空整数数组 (数组)", c =>
            {
                c.Arg<IntVector>(0)?.Values.Clear();
                return c.ReturnNothing();
            });
            DefineFree("sq_IntVectorPush", I,
                "往整数数组末尾加一个整数 (数组, 值):数组为 null 时什么也不做(客户端 0x005d71b0,与 sq_IntVectPush 共用受保护区里的追加跳板 0x00493770,“追加到末尾”是推断,E-048)", c =>
            {
                c.Arg<IntVector>(0)?.Values.Add(c.Int(1));
                return c.ReturnNothing();
            });

            // ---- 创建与销毁被动对象
            Define("sq_SendCreatePassiveObjectPacket", I,
                "创建被动对象 (编号, 未知 0, x, y, z[, 朝向]):位置相对创建者;朝向参数为 NEUTRAL 时先换成创建者自己的朝向,朝向不是“右”时 x 偏移取负"
                + "(客户端创建函数 0x00878470,E-099);发送缓冲里写好的数据随消息交给 setCustomData_po_*;对象为 null 时返回 -1(客户端 0x005ae780,E-048);"
                + "其余情况返回 0(客户端返回第 0x340 号虚函数的结果,含义未知)", B, c => SendCreatePassive(c, false, true));
            Define("sq_p00_sendCreatePassiveObjectPacket", I, "同 sq_SendCreatePassiveObjectPacket(复仇者模块的写法)", M, c => SendCreatePassive(c, false));
            Define("sq_SendCreatePassiveObjectPacketPos", I, "创建被动对象 (创建者, 编号, 未知 0, x, y, z):位置是世界坐标", G, c => SendCreatePassive(c, true));
            Define("sq_SendCreatePassiveObjectPacketFromPassivePos", I,
                "被动对象创建被动对象 (创建者, 编号, 未知 0, x, y, z):位置按世界坐标处理(推断:脚本传目标的世界坐标;被注释掉的旧写法也传世界坐标)", G, c => SendCreatePassive(c, true));
            Define("sq_SendDestroyPacketPassiveObject", I, "销毁被动对象 (对象):在当前回调结束后销毁,销毁时调用 destroy_po_* 与 onDestroyObject_po_*", G, c =>
            {
                if (c.Self is GameObject o && o.Kind == ObjectKind.PassiveObject)
                {
                    QueueWorldPacket(() => Destroy(o));
                }
                return c.ReturnNothing();
            });

            // ---- 查询
            Define("getMyPassiveObject", I, "这个对象创建的、指定编号的第 index 个有效被动对象 (编号, index);没有为 null", M, c =>
                c.Return(FindPassiveObject(c.SelfAs<GameObject>(), c.Int32(0), c.Int32(1))));
            Define("sq_GetPassiveObject", I, "同 getMyPassiveObject (编号[, index])", B, c =>
                c.Return(FindPassiveObject(c.SelfAs<GameObject>(), c.Int32(0), c.Has(1) ? c.Int32(1) : 0)));
            Define("getTopCharacter", I, "沿父对象向上找到最顶层的角色;没有为 null", M, c => c.Return(TopCharacter(c.SelfAs<GameObject>())));
            Define("sq_GetCNRDObjectToSQRCharacter", I, "把对象当成角色用:是角色则返回它,否则 null", G, c => c.Return(c.Self is GameObject o && o.Kind == ObjectKind.Character ? o : null));
            Define("sq_ObjectToSQRCharacter", I, "把对象当成角色用:是角色则返回它,否则 null", G, c => c.Return(c.Self is GameObject o && o.Kind == ObjectKind.Character ? o : null));
            Define("sq_GetCNRDObjectToActiveObject", I, "把对象当成主动对象(角色、怪物)用:是则返回它,否则 null", G, c =>
                c.Return(c.Self is GameObject o && (o.Kind == ObjectKind.Character || o.Kind == ObjectKind.Monster) ? o : null));
            Define("sq_GetGroup", I, "对象所属的'组'(对象种类的编号);与 sq_GetUniqueId、sq_GetObject 配合用来在消息里指代对象", G, c =>
                c.Return(c.Self is GameObject o ? (int)o.Kind : -1));
            Define("sq_GetUniqueId", I, "对象的唯一编号(世界内不重复)", G, c => c.Return(c.Self == null ? -1L : c.Self.Id));
            Define("sq_GetObject", I, "按 (组, 唯一编号) 找对象 (参照对象, 组, 编号);找不到或已失效为 null", G, c =>
            {
                long uid = c.Int(1);
                int group = c.Int32(0);
                foreach (GameObject o in Objects)
                {
                    if (o.Valid && o.Id == uid && (int)o.Kind == group)
                    {
                        return c.Return(o);
                    }
                }
                return c.ReturnNull();
            });

            // ---- 状态消息
            // 这一族接口在客户端都落到同一个"请求切换状态"的虚函数(槽 0x30c,0x008779b0,E-099):参数 (状态, 数据数组, 优先级, 强制发送, 日志字符串)。
            // 其中第 4 个参数不是“重置计时器”:它只是“对象不是本机控制时也允许发送”的开关,单机里所有对象都归本机控制,不起作用。
            // 状态真正执行时的 isResetTimer:客户端 0x008360e0 的第 3 个参数,为真就重置状态计时器;它的 126 个调用点里 117 个给常量 1,
            // 其余 9 个实参来自寄存器或另有布局,没有一个是常量 0,所以请求路径一律按 true(收到回应后的调用点没有定位,属推断)。
            Define("sendStateOnlyPacket", NativeStatus.Verified,
                "给对象发'切换状态'消息 (状态),没有数据:客户端 0x00442d00:对象由本机控制时,请求 (状态, 无数据, 优先级 0) 并立刻发送(E-099)", M, c =>
            {
                QueueState(c.SelfAs<GameObject>(), c.Int32(0), 0, true, Array.Empty<long>());
                return c.ReturnNothing();
            });
            Define("sendStatePacket", NativeStatus.Verified,
                "给对象发'切换状态'消息 (状态, 参数):数据数组只有这一个参数;客户端 0x00443180:对象由本机控制时,请求 (状态, [参数], 优先级 0) 并立刻发送(E-099)", M, c =>
            {
                long[] data = c.Has(1) && !c.IsNull(1) ? new[] { c.Int(1) } : Array.Empty<long>();
                QueueState(c.SelfAs<GameObject>(), c.Int32(0), 0, true, data);
                return c.ReturnNothing();
            });
            Define("addSetStatePacket", NativeStatus.Verified,
                "给对象发'切换状态'消息 (状态, 整数数组, 优先级, 强制发送, 字符串):客户端直接跳进请求函数 0x008779b0;第 4 个参数是“非本机控制也发送”,不是重置计时器;"
                + "第 5 个只用于日志(E-099)。请求的优先级规则见 QueueState", M, c =>
            {
                QueueState(c.SelfAs<GameObject>(), c.Int32(0), c.Int32(2), true, ToArray(c.Arg<IntVector>(1)));
                return c.ReturnNothing();
            });
            Define("sq_AddSetStatePacketActiveObject", NativeStatus.Verified,
                "给主动对象发'切换状态'消息 (对象, 状态, 整数数组, 优先级):客户端 0x005adc30:对象为空什么也不做,否则请求 (状态, 数组, 优先级) 并立刻发送(E-099)", G, c =>
            {
                if (c.Self is GameObject o)
                {
                    QueueState(o, c.Int32(0), c.Int32(2), true, ToArray(c.Arg<IntVector>(1)));
                }
                return c.ReturnNothing();
            });
            Define("sq_SendChangeSkillEffectPacket", I,
                "告诉角色某个技能的特效要变 (角色, 技能):在当前回调结束后调用登记了该技能的状态的 onChangeSkillEffect_*,发送缓冲里的数据随消息给出", G, c =>
                {
                    GameObject chr = c.SelfAs<GameObject>();
                    int skill = c.Int32(0);
                    byte[] data = WrittenData();
                    QueueWorldPacket(() => ChangeSkillEffect(chr, skill, data));
                    return c.ReturnNothing();
                });
        }
    }
}
