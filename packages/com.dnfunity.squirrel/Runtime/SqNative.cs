using System;
using System.Runtime.InteropServices;

namespace Dnf.Squirrel
{
    /// <summary>
    /// 原生库(native/dnfsq + native/squirrel)的 P/Invoke 声明。
    /// 约定:SQInteger 是 64 位(long),SQBool 是 64 位无符号(ulong),SQFloat 是 32 位(float);各平台一致。
    /// 字符串一律以 NUL 结尾的字节数组传递(脚本里的字符串是字节串,不假设 UTF-8)。
    /// </summary>
    internal static class SqNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        public const string Lib = "__Internal";
#else
        public const string Lib = "dnfsq";
#endif

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogFn(IntPtr vm, int level, IntPtr text);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate long ClosureFn(IntPtr vm);

        // ---- 宿主封装(native/dnfsq/dnfsq.cpp) ----
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dnfsq_open(int stackSize, LogFn log);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void dnfsq_close(IntPtr vm);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dnfsq_run_buffer(IntPtr vm, byte[] buf, int len, byte[] name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dnfsq_compile_only(IntPtr vm, byte[] buf, int len, byte[] name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dnfsq_compile_error(IntPtr vm);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dnfsq_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dnfsq_integer_size();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dnfsq_float_size();

        // ---- Squirrel C 接口(只声明用到的) ----
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_gettop(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_settop(IntPtr v, long newtop);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pop(IntPtr v, long n);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_push(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_remove(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pushroottable(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pushregistrytable(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pushnull(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pushinteger(IntPtr v, long n);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pushfloat(IntPtr v, float f);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pushbool(IntPtr v, ulong b);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_pushstring(IntPtr v, byte[] s, long len);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_newtable(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_newarray(IntPtr v, long size);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_arrayappend(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_newclosure(IntPtr v, IntPtr func, ulong nfreevars);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_newslot(IntPtr v, long idx, ulong bstatic);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_rawget(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_rawset(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_rawdeleteslot(IntPtr v, long idx, ulong pushval);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_setdelegate(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sq_gettype(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_getsize(IntPtr v, long idx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_getinteger(IntPtr v, long idx, out long i);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_getfloat(IntPtr v, long idx, out float f);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_getbool(IntPtr v, long idx, out ulong b);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_getstringandsize(IntPtr v, long idx, out IntPtr c, out long size);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_call(IntPtr v, long nparams, ulong retval, ulong raiseerror);

        /// <summary>闭包的形参个数(脚本闭包含 this,即声明个数加 1)与自由变量个数。idx 必须是脚本闭包或原生闭包。</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_getclosureinfo(IntPtr v, long idx, out ulong nparams, out ulong nfreevars);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_throwerror(IntPtr v, byte[] err);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_throwobject(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sq_reseterror(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_setnativeclosurename(IntPtr v, long idx, byte[] name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_collectgarbage(IntPtr v);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sq_tostring(IntPtr v, long idx);

        public static SqType ToSqType(int raw)
        {
            switch (raw & 0x00FFFFFF)
            {
                case 0x00000001: return SqType.Null;
                case 0x00000002: return SqType.Integer;
                case 0x00000004: return SqType.Float;
                case 0x00000008: return SqType.Bool;
                case 0x00000010: return SqType.String;
                case 0x00000020: return SqType.Table;
                case 0x00000040: return SqType.Array;
                case 0x00000080: return SqType.UserData;
                case 0x00000100: return SqType.Closure;
                case 0x00000200: return SqType.NativeClosure;
                case 0x00000400: return SqType.Generator;
                case 0x00000800: return SqType.UserPointer;
                case 0x00001000: return SqType.Thread;
                case 0x00002000: return SqType.FuncProto;
                case 0x00004000: return SqType.Class;
                case 0x00008000: return SqType.Instance;
                case 0x00010000: return SqType.WeakRef;
                case 0x00020000: return SqType.Outer;
                default: return SqType.Unknown;
            }
        }
    }
}
