using System.Collections.Generic;
using Dnf.Data;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>一个暂时只有占位实现的宿主对象(动画、粒子发生器等):任何方法调用都由替身函数记数并返回 null。</summary>
    public sealed class StubObject : NativeObject
    {
        private readonly string _kind;

        public StubObject(string kind)
        {
            _kind = kind;
        }

        public override string TypeName => "Stub:" + _kind;
    }

    /// <summary>变量表、计时器、依附体登记等"存数据"的原生接口。语义依据名字与脚本用法推断。</summary>
    public sealed partial class GameWorld
    {
        private VarStore VarOf(NativeObject self, string name)
        {
            switch (self)
            {
                case GameObject o:
                    return o.GetVar(name);
                case Appendage a:
                    return a.GetVar(name);
                default:
                    return null;
            }
        }

        private GameTimer TimerOf(NativeObject self)
        {
            switch (self)
            {
                case GameObject o:
                    return o.Timer;
                case Appendage a:
                    return a.Timer;
                default:
                    return null;
            }
        }

        /// <summary>不带括号读取的"属性"(obj.sq_var、obj.sq_timer_ 等):返回读取函数,不是属性则返回 null。</summary>
        private SqNativeFn PropertyGetter(string name)
        {
            switch (name)
            {
                case "sq_var":
                    return call =>
                    {
                        VarStore v = VarOf(ResolveOrNull(call, -1), null);
                        return v == null ? call.ReturnNull() : call.Return(Handle(v));
                    };
                case "sq_timer_":
                case "timer_":
                    return call =>
                    {
                        GameTimer t = TimerOf(ResolveOrNull(call, -1));
                        return t == null ? call.ReturnNull() : call.Return(Handle(t));
                    };
                default:
                    return null;
            }
        }

        private static void RemoveByIdentity(List<NativeObject> list, NativeObject target)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i], target))
                {
                    list.RemoveAt(i);
                }
            }
        }

        private static bool ContainsIdentity(List<NativeObject> list, NativeObject target)
        {
            foreach (NativeObject o in list)
            {
                if (ReferenceEquals(o, target))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>对象向量里去掉已销毁的对象(每个世界时刻只清一次)后的列表。</summary>
        private List<NativeObject> LiveObjects(VarStore v)
        {
            if (v.ObjVectorPrunedAt != TimeMs)
            {
                v.ObjVectorPrunedAt = TimeMs;
                v.ObjVector.RemoveAll(o => (o is GameObject g && !g.Valid) || (o is Appendage a && !a.Valid));
            }
            return v.ObjVector;
        }

        private void RegisterVarNatives()
        {
            const NativeForm M = NativeForm.Member;
            const NativeStatus I = NativeStatus.Inferred;

            Define("getVar", NativeStatus.Verified,
                "对象或依附体上的变量表,按名字区分。读过客户端三个类的实现(E-048):getVar(名字) 都转到同一个按名字查找的函数 0x0087F9C0(以名字为键的映射,找不到就新建一张;名字区分大小写),"
                + "映射的位置分别是 0x0087FCC0 里 [this+0x208] 指向的对象、0x009DB490 里的 this+0xAC、0x00BBCF90 里的 this+0x66F4。"
                + "不带名字的 getVar():对象一类(0x0087FDC0、0x00BBD030)只是用字面量 \"default\" 调用按名字的函数,所以 getVar() 与 getVar(\"default\") 是同一张表;"
                + "依附体一类(0x005AC300)返回对象里直接嵌的 this+0xC4 那一张(就是 sq_var),它和名字叫 \"default\" 的不是同一张", M, c =>
            {
                string name = c.Has(0) && !c.IsNull(0) ? c.Str(0) : null;
                VarStore v = VarOf(c.Self, name);
                if (v == null)
                {
                    throw new SqNativeException("getVar 不能用在 " + (c.Self == null ? "无效对象" : c.Self.TypeName) + " 上");
                }
                return c.Return(v);
            });
            Define("getTimer", I, "对象或依附体自己的计时器", M, c =>
            {
                GameTimer t = TimerOf(c.Self);
                if (t == null)
                {
                    throw new SqNativeException("getTimer 不能用在 " + (c.Self == null ? "无效对象" : c.Self.TypeName) + " 上");
                }
                return c.Return(t);
            });

            // ---- 整数向量(get_vector 等)。五个接口的客户端实现都已读过(E-046),语义与下面一致。
            const NativeStatus V = NativeStatus.Verified;
            Define("get_vector", V, "变量表的整数向量:取第 i 项(越界或为负为 0)(客户端 0x005b7360)", M, c => c.Return(c.SelfAs<VarStore>().GetVector(c.Int(0))));
            Define("set_vector", V, "变量表的整数向量:设第 i 项;下标越界(含为负)什么也不做,不扩展(客户端 0x005b7390)", M, c =>
            {
                c.SelfAs<VarStore>().SetVector(c.Int(0), c.Int(1));
                return c.ReturnNothing();
            });
            Define("push_vector", V, "变量表的整数向量:末尾加一项(客户端 0x0042b190 转到向量的追加函数)", M, c =>
            {
                c.SelfAs<VarStore>().Vector.Add(c.Int(0));
                return c.ReturnNothing();
            });
            Define("clear_vector", V, "变量表的整数向量:清空(客户端 0x0042b180)", M, c =>
            {
                c.SelfAs<VarStore>().Vector.Clear();
                return c.ReturnNothing();
            });
            Define("size_vector", V, "变量表的整数向量:长度(客户端 0x005abc00)", M, c => c.Return(c.SelfAs<VarStore>().Vector.Count));

            // ---- 标量
            // 整数、布尔、浮点共用一张表(一个编号一项:32 位的值加类型标记),读的时候不看标记(客户端 0x010dd280 起的一组函数,E-046)
            Define("setInt", V, "变量表:按编号存整数(32 位;与布尔、浮点共用一张表,同一编号只有一项;客户端 0x010dd3a0)", M, c =>
            {
                c.SelfAs<VarStore>().SetInt(c.Int(0), c.Int32(1));
                return c.ReturnNothing();
            });
            Define("getInt", V, "变量表:按编号取整数(没存过为 0;读的是存的 32 位值,不管它是用 setBool 还是 setFloat 存的;客户端 0x010dd5f0)", M, c => c.Return(c.SelfAs<VarStore>().GetInt(c.Int(0))));
            Define("setBool", V, "变量表:按编号存布尔(存 0 或 1;客户端 0x010dd360)", M, c =>
            {
                c.SelfAs<VarStore>().SetBool(c.Int(0), c.Bool(1));
                return c.ReturnNothing();
            });
            Define("getBool", V, "变量表:按编号取布尔(存的值恰好为 1 才是真,没存过为假;客户端 0x010dd4a0)", M, c => c.Return(c.SelfAs<VarStore>().GetBool(c.Int(0))));
            Define("setFloat", V, "变量表:按编号存浮点(存单精度的位模式;客户端 0x010dd3d0)", M, c =>
            {
                c.SelfAs<VarStore>().SetFloat(c.Int(0), c.Float(1));
                return c.ReturnNothing();
            });
            Define("getFloat", V, "变量表:按编号取浮点(没存过为 0;把存的 32 位值当浮点读;客户端 0x010dd4d0)", M, c => c.Return(c.SelfAs<VarStore>().GetFloat(c.Int(0))));

            // ---- 对象向量与按编号存的对象槽
            Define("push_obj_vector", I, "变量表的对象向量:末尾加一个对象", M, c =>
            {
                NativeObject o = c.Arg<NativeObject>(0);
                if (o != null)
                {
                    c.SelfAs<VarStore>().ObjVector.Add(o);
                }
                return c.ReturnNothing();
            });
            Define("get_obj_vector", I,
                "变量表的对象向量:取第 i 个(越界为 null)。向量里已销毁的对象在下一个时刻起被清掉(推断:ChainLightning 靠'向量变空'判断光束全部消失),同一时刻内仍可取到", M, c =>
                {
                    List<NativeObject> list = LiveObjects(c.SelfAs<VarStore>());
                    long i = c.Int(0);
                    NativeObject o = i >= 0 && i < list.Count ? list[(int)i] : null;
                    return c.Return(o != null && o.Id != 0 && _byId.ContainsKey(o.Id) ? o : null);
                });
            Define("get_obj_vector_size", I, "变量表的对象向量:长度(参数忽略;不含已销毁的对象)", M, c => c.Return(LiveObjects(c.SelfAs<VarStore>()).Count));
            Define("get_obj_vector_index", I, "变量表的对象向量:某个对象的下标,不在里面为 -1", M, c =>
            {
                List<NativeObject> list = LiveObjects(c.SelfAs<VarStore>());
                NativeObject o = c.Arg<NativeObject>(0);
                for (int i = 0; i < list.Count; i++)
                {
                    if (o != null && ReferenceEquals(list[i], o))
                    {
                        return c.Return(i);
                    }
                }
                return c.Return(-1);
            });
            Define("is_obj_vector", I, "变量表的对象向量:是否包含这个对象", M, c =>
            {
                NativeObject o = c.Arg<NativeObject>(0);
                return c.Return(o != null && ContainsIdentity(LiveObjects(c.SelfAs<VarStore>()), o));
            });
            Define("remove_obj_vector", I, "变量表的对象向量:移除这个对象", M, c =>
            {
                NativeObject o = c.Arg<NativeObject>(0);
                if (o != null)
                {
                    RemoveByIdentity(c.SelfAs<VarStore>().ObjVector, o);
                }
                return c.ReturnNothing();
            });
            Define("clear_obj_vector", I, "变量表的对象向量:清空", M, c =>
            {
                c.SelfAs<VarStore>().ObjVector.Clear();
                return c.ReturnNothing();
            });
            foreach (string kind in new[] { "Object", "Appendage", "Animation" })
            {
                string k = kind;
                Define("set" + k, I, "变量表:按编号存一个" + k + "引用", M, c =>
                {
                    c.SelfAs<VarStore>().Slots[k + ":" + c.Int(0)] = c.Arg<NativeObject>(1);
                    return c.ReturnNothing();
                });
                Define("get" + k, I, "变量表:按编号取存过的" + k + "引用(没有为 null)", M, c =>
                {
                    c.SelfAs<VarStore>().Slots.TryGetValue(k + ":" + c.Int(0), out NativeObject o);
                    return c.Return(o);
                });
            }

            // ---- 计时器向量
            Define("push_ct_vector", I, "变量表的 ct 向量:新建一个计时器放到末尾并返回它", M, c => PushTimer(c, c.SelfAs<VarStore>().CtVector));
            Define("get_ct_vector", I, "变量表的 ct 向量:取第 i 个计时器(越界为 null)", M, c => c.Return(GetFrom(c.SelfAs<VarStore>().CtVector, c.Int(0))));
            Define("clear_ct_vector", I, "变量表的 ct 向量:清空", M, c =>
            {
                c.SelfAs<VarStore>().CtVector.Clear();
                return c.ReturnNothing();
            });
            Define("push_timer_vector", I, "变量表的计时器向量:新建一个计时器放到末尾并返回它", M, c => PushTimer(c, c.SelfAs<VarStore>().TimerVector));
            Define("get_timer_vector", I, "变量表的计时器向量:取第 i 个计时器(越界为 null)", M, c => c.Return(GetFrom(c.SelfAs<VarStore>().TimerVector, c.Int(0))));
            Define("clear_timer_vector", I, "变量表的计时器向量:清空", M, c =>
            {
                c.SelfAs<VarStore>().TimerVector.Clear();
                return c.ReturnNothing();
            });

            // ---- 动画与粒子发生器(渲染层的对象:这里只有占位,脚本拿到后调用的方法会被替身函数记数)
            Define("GetAnimationMap", NativeStatus.Inferred, "变量表:按名字取(没有则按路径载入)一个独立的动画对象 (名字, 路径);路径从数据根目录算起(例如 Character/Priest/Animation/Awakening4.ani,全库里确有这个文件)", M, c =>
            {
                VarStore v = c.SelfAs<VarStore>();
                string key = "ani:" + c.Str(0);
                if (!(v.Slots.TryGetValue(key, out NativeObject existing) && existing is AnimationObject))
                {
                    string path = PvfPath.Normalize(c.Str(1));
                    var animator = new Animator();
                    animator.Set(Data.GetAnimation(path), path);
                    existing = new AnimationObject(animator, false);
                    Handle(existing);
                    v.Slots[key] = existing;
                }
                return c.Return(existing);
            });
            Define("GetparticleCreaterMap", NativeStatus.Trace, "变量表:按名字取(没有则创建)一个粒子发生器;占位对象,方法调用只记数", M, c => c.Return(GetOrCreateSlot(c, "ptl:" + c.Str(0), "ParticleCreater")));
            Define("push_ani_vector", NativeStatus.Trace, "变量表的动画向量:末尾加一个动画(占位)", M, c =>
            {
                NativeObject o = c.Arg<NativeObject>(0);
                if (o != null)
                {
                    c.SelfAs<VarStore>().AniVector.Add(o);
                }
                return c.ReturnNothing();
            });
            Define("get_ani_vector", NativeStatus.Trace, "变量表的动画向量:取第 i 个(越界为 null)", M, c => c.Return(GetFrom(c.SelfAs<VarStore>().AniVector, c.Int(0))));
            Define("clear_ani_vector", NativeStatus.Trace, "变量表的动画向量:清空", M, c =>
            {
                c.SelfAs<VarStore>().AniVector.Clear();
                return c.ReturnNothing();
            });

            // ---- 计时器对象
            Define("Get", I, "计时器:已计时间(毫秒)", M, c => c.Return(c.SelfAs<GameTimer>().Get()));
            Define("Start", I, "计时器:开始计时(上限, 起点);上限与起点的含义是推断,未验证", M, c =>
            {
                c.SelfAs<GameTimer>().Start(c.IntOr(0, 0), c.IntOr(1, 0));
                return c.ReturnNothing();
            });
            Define("Reset", I, "计时器:归零并开始计时", M, c =>
            {
                c.SelfAs<GameTimer>().Reset();
                return c.ReturnNothing();
            });
            Define("resetInstant", I, "计时器:归零,并让周期事件立刻到期一次", M, c =>
            {
                c.SelfAs<GameTimer>().ResetInstant();
                return c.ReturnNothing();
            });
            Define("setParameter", I, "计时器:设置周期事件 (间隔毫秒, 次数;-1 不限)", M, c =>
            {
                c.SelfAs<GameTimer>().SetParameter(c.Int(0), c.Int32(1));
                return c.ReturnNothing();
            });
            Define("isOnEvent", I, "计时器:当前时间点是否到了周期事件(每个周期只返回一次真)", M, c => c.Return(c.SelfAs<GameTimer>().IsOnEvent(c.Int(0))));
            Define("getEventTerm", I, "计时器:周期事件的间隔", M, c => c.Return(c.SelfAs<GameTimer>().Interval));
        }

        private int PushTimer(NativeCall c, List<NativeObject> list)
        {
            var t = new GameTimer(this);
            Handle(t);
            list.Add(t);
            return c.Return(t);
        }

        private static NativeObject GetFrom(List<NativeObject> list, long i)
        {
            return i >= 0 && i < list.Count ? list[(int)i] : null;
        }

        private NativeObject GetOrCreateSlot(NativeCall c, string key, string kind)
        {
            VarStore v = c.SelfAs<VarStore>();
            if (!v.Slots.TryGetValue(key, out NativeObject o))
            {
                o = new StubObject(kind);
                Handle(o);
                v.Slots[key] = o;
            }
            return o;
        }
    }
}
