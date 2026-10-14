using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 脚本能拿到的宿主对象(游戏对象、变量表、被依附体、整数数组……)的基类。
    /// 脚本里的对象是一张带 _h 槽的表,宿主靠 <see cref="Id"/> 找回自己的对象;Id 在第一次交给脚本时分配。
    /// </summary>
    public abstract class NativeObject
    {
        /// <summary>虚拟机里的编号;0 表示还没有交给过脚本。</summary>
        public long Id { get; internal set; }

        public GameWorld World { get; internal set; }

        public abstract string TypeName { get; }
    }

    /// <summary>
    /// 一个整数数组:脚本里 sq_IntVectPush 往对象的"数据数组"里压值、再随状态切换包一起发出去,
    /// 状态脚本的 onSetState(obj, state, datas, isResetTimer) 里拿到的 datas 就是发出时的那一份拷贝。
    /// </summary>
    public sealed class IntVector : NativeObject
    {
        public List<long> Values { get; } = new List<long>();

        public override string TypeName => "IntVector";

        public IntVector()
        {
        }

        public IntVector(IEnumerable<long> values)
        {
            Values.AddRange(values);
        }

        public long Get(long index)
        {
            return index >= 0 && index < Values.Count ? Values[(int)index] : 0;
        }
    }

    /// <summary>
    /// 对象上的变量表。obj.getVar() 取默认的一张,obj.getVar("名字") 取按名字区分的另一张(第一次取时创建)。
    /// 每张表里有三组按编号存取的标量(整数、布尔、浮点)和几个向量(整数向量、对象向量等)。
    /// 编号都是脚本里的常量(VAR_xxx);没写过的编号读出来是 0 / false / 0.0。名字、常量与向量的用途由各脚本约定,宿主不解释。
    /// </summary>
    public sealed class VarStore : NativeObject
    {
        public Dictionary<long, long> Ints { get; } = new Dictionary<long, long>();
        public Dictionary<long, bool> Bools { get; } = new Dictionary<long, bool>();
        public Dictionary<long, float> Floats { get; } = new Dictionary<long, float>();
        public List<long> Vector { get; } = new List<long>();
        public List<NativeObject> ObjVector { get; } = new List<NativeObject>();

        /// <summary>对象向量上一次清理失效对象的世界时间(毫秒);同一时刻内多次读取看到同一份列表。</summary>
        internal int ObjVectorPrunedAt { get; set; } = -1;
        public List<NativeObject> CtVector { get; } = new List<NativeObject>();
        public List<NativeObject> TimerVector { get; } = new List<NativeObject>();
        public List<NativeObject> AniVector { get; } = new List<NativeObject>();

        /// <summary>按"类别:编号"或"类别:名字"存的单个对象槽(setObject/getObject、GetAnimationMap 等)。</summary>
        public Dictionary<string, NativeObject> Slots { get; } = new Dictionary<string, NativeObject>();

        public override string TypeName => "VarStore";

        public long GetInt(long index)
        {
            return Ints.TryGetValue(index, out long v) ? v : 0;
        }

        public bool GetBool(long index)
        {
            return Bools.TryGetValue(index, out bool v) && v;
        }

        public float GetFloat(long index)
        {
            return Floats.TryGetValue(index, out float v) ? v : 0f;
        }

        public long GetVector(long index)
        {
            return index >= 0 && index < Vector.Count ? Vector[(int)index] : 0;
        }

        public void SetVector(long index, long value)
        {
            if (index < 0 || index > 1 << 20)
            {
                return;
            }
            while (Vector.Count <= index)
            {
                Vector.Add(0);
            }
            Vector[(int)index] = value;
        }
    }
}
