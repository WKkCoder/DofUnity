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
        private int SendCreatePassive(NativeCall c, bool absolute)
        {
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
            DefineFree("sq_GetGlobalIntVector", I, "全局共用的整数数组", c => c.Return(GlobalIntVector));
            DefineFree("sq_IntVectorClear", I, "清空整数数组 (数组)", c =>
            {
                c.Arg<IntVector>(0)?.Values.Clear();
                return c.ReturnNothing();
            });
            DefineFree("sq_IntVectorPush", I, "往整数数组末尾加一个整数 (数组, 值)", c =>
            {
                c.Arg<IntVector>(0)?.Values.Add(c.Int(1));
                return c.ReturnNothing();
            });

            // ---- 创建与销毁被动对象
            Define("sq_SendCreatePassiveObjectPacket", I,
                "创建被动对象 (编号, 未知 0, x, y, z[, 朝向]):位置相对创建者(x 向前、朝向左时镜像),朝向参数为 NEUTRAL 时偏移不镜像;"
                + "发送缓冲里写好的数据随消息交给 setCustomData_po_*;返回 0(原版返回的编号含义未知)", B, c => SendCreatePassive(c, false));
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
            Define("sendStateOnlyPacket", I, "给对象发'切换状态'消息 (状态),没有数据", M, c =>
            {
                QueueState(c.SelfAs<GameObject>(), c.Int32(0), 0, false, Array.Empty<long>());
                return c.ReturnNothing();
            });
            Define("sendStatePacket", I, "给对象发'切换状态'消息 (状态, 参数):数据数组只有这一个参数", M, c =>
            {
                long[] data = c.Has(1) && !c.IsNull(1) ? new[] { c.Int(1) } : Array.Empty<long>();
                QueueState(c.SelfAs<GameObject>(), c.Int32(0), 0, false, data);
                return c.ReturnNothing();
            });
            Define("addSetStatePacket", I, "给对象发'切换状态'消息 (状态, 整数数组, 优先级, 是否重置计时器, 字符串)", M, c =>
            {
                QueueState(c.SelfAs<GameObject>(), c.Int32(0), c.Int32(2), c.BoolOr(3, false), ToArray(c.Arg<IntVector>(1)));
                return c.ReturnNothing();
            });
            Define("sq_AddSetStatePacketActiveObject", I, "给主动对象发'切换状态'消息 (对象, 状态, 整数数组, 优先级)", G, c =>
            {
                if (c.Self is GameObject o)
                {
                    QueueState(o, c.Int32(0), c.Int32(2), false, ToArray(c.Arg<IntVector>(1)));
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
