using System;
using System.Runtime.InteropServices;

// 证据:Squirrel 3.2(逐字节未修改)的 C# 封装;212 个 .nut 全部能编译见 E-033,引擎加载脚本的约定见 E-035。
namespace Dnf.Squirrel
{
    /// <summary>字节串与 .NET 字符串之间的转换:一个字节对应一个字符(Latin-1),不假设 UTF-8,任意字节都能往返。</summary>
    internal static class SqText
    {
        public static byte[] ToCString(string s)
        {
            if (s == null)
            {
                s = string.Empty;
            }
            byte[] b = new byte[s.Length + 1];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                b[i] = c < 256 ? (byte)c : (byte)'?';
            }
            return b;
        }

        public static string FromBytes(byte[] b, int length)
        {
            char[] chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = (char)b[i];
            }
            return new string(chars);
        }

        public static string FromPointer(IntPtr p, long length)
        {
            if (p == IntPtr.Zero || length <= 0)
            {
                return string.Empty;
            }
            byte[] b = new byte[length];
            Marshal.Copy(p, b, 0, (int)length);
            return FromBytes(b, b.Length);
        }

        /// <summary>诊断文本(错误信息、调用栈)用 UTF-8:它们是给人看的,不要求字节往返。</summary>
        public static byte[] ToCStringUtf8(string s)
        {
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
            byte[] b = new byte[raw.Length + 1];
            Array.Copy(raw, b, raw.Length);
            return b;
        }

        public static string FromCStringUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero)
            {
                return string.Empty;
            }
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0)
            {
                n++;
            }
            if (n == 0)
            {
                return string.Empty;
            }
            byte[] b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return System.Text.Encoding.UTF8.GetString(b);
        }

        public static string FromCString(IntPtr p)
        {
            if (p == IntPtr.Zero)
            {
                return string.Empty;
            }
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0)
            {
                n++;
            }
            return FromPointer(p, n);
        }
    }

    /// <summary>
    /// 原生函数被脚本调用时看到的"调用现场"。栈上第 1 项是 this,参数从第 2 项开始;这里的参数编号从 0 起,
    /// 即 Arg(0) 是第一个参数。读取参数的类型不对时抛 <see cref="SqNativeException"/>,会变成脚本里的异常。
    /// </summary>
    public readonly struct SqCall
    {
        private readonly SqVm _vm;
        private readonly int _count;

        internal SqCall(SqVm vm, int count)
        {
            _vm = vm;
            _count = count;
        }

        public SqVm Vm
        {
            get { return _vm; }
        }

        /// <summary>脚本传来的参数个数(不含 this)。</summary>
        public int ArgCount
        {
            get { return _count - 1; }
        }

        private IntPtr H
        {
            get { return _vm.Handle; }
        }

        private long Index(int arg)
        {
            return arg + 2;
        }

        public bool Has(int arg)
        {
            return arg >= 0 && arg < ArgCount;
        }

        public SqType TypeOf(int arg)
        {
            if (!Has(arg))
            {
                return SqType.Null;
            }
            return SqNative.ToSqType(SqNative.sq_gettype(H, Index(arg)));
        }

        public bool IsNull(int arg)
        {
            return TypeOf(arg) == SqType.Null;
        }

        /// <summary>this 的类型(对全局函数是根表,对对象方法是对象表)。</summary>
        public SqType ThisType
        {
            get { return SqNative.ToSqType(SqNative.sq_gettype(H, 1)); }
        }

        private SqNativeException Wrong(int arg, string want)
        {
            return new SqNativeException("参数 " + (arg + 1) + " 需要" + want + ",实际是 " + TypeOf(arg));
        }

        public long Int(int arg)
        {
            switch (TypeOf(arg))
            {
                case SqType.Integer:
                    {
                        long v;
                        SqNative.sq_getinteger(H, Index(arg), out v);
                        return v;
                    }
                case SqType.Float:
                    {
                        float f;
                        SqNative.sq_getfloat(H, Index(arg), out f);
                        return (long)f;
                    }
                case SqType.Bool:
                    {
                        ulong b;
                        SqNative.sq_getbool(H, Index(arg), out b);
                        return b != 0 ? 1 : 0;
                    }
                default:
                    throw Wrong(arg, "整数");
            }
        }

        public int Int32(int arg)
        {
            return unchecked((int)Int(arg));
        }

        public long IntOr(int arg, long fallback)
        {
            return Has(arg) && !IsNull(arg) ? Int(arg) : fallback;
        }

        public float Float(int arg)
        {
            switch (TypeOf(arg))
            {
                case SqType.Float:
                    {
                        float f;
                        SqNative.sq_getfloat(H, Index(arg), out f);
                        return f;
                    }
                case SqType.Integer:
                    {
                        long v;
                        SqNative.sq_getinteger(H, Index(arg), out v);
                        return v;
                    }
                default:
                    throw Wrong(arg, "数");
            }
        }

        public float FloatOr(int arg, float fallback)
        {
            return Has(arg) && !IsNull(arg) ? Float(arg) : fallback;
        }

        public bool Bool(int arg)
        {
            switch (TypeOf(arg))
            {
                case SqType.Bool:
                    {
                        ulong b;
                        SqNative.sq_getbool(H, Index(arg), out b);
                        return b != 0;
                    }
                case SqType.Integer:
                    return Int(arg) != 0;
                case SqType.Null:
                    return false;
                default:
                    throw Wrong(arg, "布尔");
            }
        }

        public string String(int arg)
        {
            if (TypeOf(arg) != SqType.String)
            {
                throw Wrong(arg, "字符串");
            }
            IntPtr p;
            long size;
            SqNative.sq_getstringandsize(H, Index(arg), out p, out size);
            return SqText.FromPointer(p, size);
        }

        /// <summary>参数是原生对象(带 _h 槽的表)时取出它的句柄。</summary>
        public SqHandle Handle(int arg)
        {
            SqHandle h;
            if (!TryHandle(arg, out h))
            {
                throw Wrong(arg, "原生对象");
            }
            return h;
        }

        public bool TryHandle(int arg, out SqHandle handle)
        {
            handle = default(SqHandle);
            if (TypeOf(arg) != SqType.Table)
            {
                return false;
            }
            return _vm.TryReadHandle(Index(arg), out handle);
        }

        /// <summary>this 是原生对象时取出它的句柄(对象方法里最常用)。</summary>
        public SqHandle This()
        {
            SqHandle h;
            if (!_vm.TryReadHandle(1, out h))
            {
                throw new SqNativeException("this 不是原生对象");
            }
            return h;
        }

        // ---- 返回值:各个 Return 压入一个值并返回 1,可以直接 return call.Return(...) ----
        public int Return(long v)
        {
            SqNative.sq_pushinteger(H, v);
            return 1;
        }

        public int Return(int v)
        {
            SqNative.sq_pushinteger(H, v);
            return 1;
        }

        public int Return(float v)
        {
            SqNative.sq_pushfloat(H, v);
            return 1;
        }

        public int Return(bool v)
        {
            SqNative.sq_pushbool(H, v ? 1UL : 0UL);
            return 1;
        }

        public int Return(string v)
        {
            if (v == null)
            {
                return ReturnNull();
            }
            byte[] b = SqText.ToCString(v);
            SqNative.sq_pushstring(H, b, b.Length - 1);
            return 1;
        }

        public int Return(SqHandle v)
        {
            _vm.PushObject(v);
            return 1;
        }

        public int ReturnNull()
        {
            SqNative.sq_pushnull(H);
            return 1;
        }

        /// <summary>什么也不返回(脚本里得到 null)。</summary>
        public int ReturnNothing()
        {
            return 0;
        }
    }
}
