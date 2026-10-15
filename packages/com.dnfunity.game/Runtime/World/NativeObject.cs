using System;
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

        /// <summary>第 index 项;下标越界(含为负)时返回 outOfRange。</summary>
        public long Get(long index, long outOfRange = 0)
        {
            return index >= 0 && index < Values.Count ? Values[(int)index] : outOfRange;
        }
    }

    /// <summary>
    /// 对象上的变量表。obj.getVar() 取默认的一张,obj.getVar("名字") 取按名字区分的另一张(第一次取时创建)。
    /// 每张表里有三组按编号存取的标量(整数、布尔、浮点)和几个向量(整数向量、对象向量等)。
    /// 编号都是脚本里的常量(VAR_xxx);没写过的编号读出来是 0 / false / 0.0。名字、常量与向量的用途由各脚本约定,宿主不解释。
    /// </summary>
    public sealed class VarStore : NativeObject
    {
        /// <summary>变量表里标量的类型标记:没存过为 0;setBool 为 1、setInt 为 2、setFloat 为 3(客户端 0x010dd360、0x010dd3a0、0x010dd3d0)。</summary>
        public const int TagNone = 0, TagBool = 1, TagInt = 2, TagFloat = 3;

        // 客户端的整数、布尔、浮点共用同一张表:一个编号只有一项,存 32 位的值和一个类型标记(E-046)。
        // 所以 setInt(5, 1) 之后 getBool(5) 为真;setFloat 之后用 getInt 读到的是浮点的位模式。
        // 取值的几个函数(0x010dd4a0、0x010dd5f0、0x010dd4d0)都不看类型标记。
        private readonly Dictionary<uint, KeyValuePair<uint, int>> _scalars = new Dictionary<uint, KeyValuePair<uint, int>>();

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

        private uint Bits(long index)
        {
            return _scalars.TryGetValue(unchecked((uint)index), out KeyValuePair<uint, int> e) ? e.Key : 0u;
        }

        /// <summary>按编号取整数:存的 32 位值按有符号数读(没存过为 0)。不管那一项是用哪种 set 存的。</summary>
        public long GetInt(long index)
        {
            return unchecked((int)Bits(index));
        }

        /// <summary>按编号取布尔:存的值恰好等于 1 才是真(不是"非 0":setInt(5, 2) 之后读到假)。</summary>
        public bool GetBool(long index)
        {
            return Bits(index) == 1u;
        }

        /// <summary>按编号取浮点:把存的 32 位值当单精度浮点读(没存过为 0)。</summary>
        public float GetFloat(long index)
        {
            return BitConverter.Int32BitsToSingle(unchecked((int)Bits(index)));
        }

        /// <summary>这个编号上存的类型标记(<see cref="TagInt"/> 等),没存过为 <see cref="TagNone"/>。</summary>
        public int TagOf(long index)
        {
            return _scalars.TryGetValue(unchecked((uint)index), out KeyValuePair<uint, int> e) ? e.Value : TagNone;
        }

        /// <summary>存整数:只保留低 32 位。</summary>
        public void SetInt(long index, long value)
        {
            _scalars[unchecked((uint)index)] = new KeyValuePair<uint, int>(unchecked((uint)value), TagInt);
        }

        public void SetBool(long index, bool value)
        {
            _scalars[unchecked((uint)index)] = new KeyValuePair<uint, int>(value ? 1u : 0u, TagBool);
        }

        public void SetFloat(long index, float value)
        {
            _scalars[unchecked((uint)index)] = new KeyValuePair<uint, int>(unchecked((uint)BitConverter.SingleToInt32Bits(value)), TagFloat);
        }

        public long GetVector(long index)
        {
            return index >= 0 && index < Vector.Count ? Vector[(int)index] : 0;
        }

        /// <summary>
        /// 设第 index 项。下标不小于当前长度(或为负数)时什么也不做,<b>不会扩展</b>向量。
        /// 依据:客户端的实现把下标当无符号数与长度比较,小于才写(0x005b7390,E-046);要加项只能用 push_vector。
        /// </summary>
        public void SetVector(long index, long value)
        {
            if (index >= 0 && index < Vector.Count)
            {
                Vector[(int)index] = value;
            }
        }
    }
}
