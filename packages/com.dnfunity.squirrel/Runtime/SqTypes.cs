using System;

// 证据:Squirrel 3.2(逐字节未修改)的 C# 封装,见 E-033。
namespace Dnf.Squirrel
{
    /// <summary>Squirrel 值的基本类型(已去掉内部标志位)。</summary>
    public enum SqType
    {
        Null,
        Integer,
        Float,
        Bool,
        String,
        Table,
        Array,
        UserData,
        Closure,
        NativeClosure,
        Generator,
        UserPointer,
        Thread,
        FuncProto,
        Class,
        Instance,
        WeakRef,
        Outer,
        Unknown,
    }

    /// <summary>
    /// 脚本里的原生对象:宿主用一个整数标识自己的对象,脚本拿到的是一张带 _h 槽的表(原型里放着原生方法)。
    /// 宿主把它当参数传给脚本函数,脚本把它传回来时用 <see cref="SqCall.Handle"/> 取回整数。
    /// </summary>
    public readonly struct SqHandle : IEquatable<SqHandle>
    {
        public readonly long Id;

        public SqHandle(long id)
        {
            Id = id;
        }

        public bool Equals(SqHandle other)
        {
            return Id == other.Id;
        }

        public override bool Equals(object obj)
        {
            return obj is SqHandle h && Equals(h);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public override string ToString()
        {
            return "obj#" + Id;
        }
    }

    /// <summary>脚本里的其他值(表、数组、闭包……):宿主不展开,只知道类型。</summary>
    public readonly struct SqOpaque
    {
        public readonly SqType Type;

        public SqOpaque(SqType type)
        {
            Type = type;
        }

        public override string ToString()
        {
            return "<" + Type + ">";
        }
    }

    /// <summary>脚本编译失败,或脚本运行时抛出了错误。Message 里带虚拟机打印的错误与调用栈。</summary>
    public sealed class SqScriptException : Exception
    {
        public SqScriptException(string message)
            : base(message)
        {
        }
    }

    /// <summary>原生函数发现参数不对(个数、类型)。会变成脚本里的一个异常,不会让宿主崩溃。</summary>
    public sealed class SqNativeException : Exception
    {
        public SqNativeException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 原生函数抛出它,表示"没有这个成员":用在缺省取值(_get)里,Squirrel 会按"索引不存在"处理。
    /// </summary>
    public sealed class SqNotFoundException : Exception
    {
        public SqNotFoundException()
            : base("not found")
        {
        }
    }
}
