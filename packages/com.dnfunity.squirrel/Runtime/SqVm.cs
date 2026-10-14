using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Dnf.Squirrel
{
    /// <summary>
    /// 原生函数:脚本调用时执行。读参数用 <see cref="SqCall"/>,返回值用 call.Return(...)(返回 1)或 call.ReturnNothing()(返回 0)。
    /// 抛出 <see cref="SqNativeException"/> 或任何异常都会变成脚本里的错误,不会穿过原生栈。
    /// </summary>
    public delegate int SqNativeFn(SqCall call);

    /// <summary>
    /// 一张放原生函数的表:既可以是全局的静态表(脚本里 IRDSQRCharacter.pushState(...) 这种写法),
    /// 也可以是对象原型(脚本里 obj.sq_GetXPos() 这种写法,对象表以它为委托)。
    /// </summary>
    public sealed class SqNativeTable
    {
        private readonly SqVm _vm;
        private readonly List<KeyValuePair<string, SqNativeFn>> _methods = new List<KeyValuePair<string, SqNativeFn>>();

        internal SqNativeTable(SqVm vm, string name, long id)
        {
            _vm = vm;
            Name = name;
            Id = id;
        }

        public string Name { get; private set; }

        internal long Id { get; private set; }

        internal IEnumerable<KeyValuePair<string, SqNativeFn>> Methods
        {
            get { return _methods; }
        }

        /// <summary>登记一个原生方法(同名则覆盖)。</summary>
        public SqNativeTable Method(string name, SqNativeFn fn)
        {
            _methods.Add(new KeyValuePair<string, SqNativeFn>(name, fn));
            _vm.SetSlotClosure(Id, name, fn);
            return this;
        }

        /// <summary>登记一个整数常量槽。</summary>
        public SqNativeTable Constant(string name, long value)
        {
            _vm.SetSlotInteger(Id, name, value);
            return this;
        }
    }

    /// <summary>
    /// 一个 Squirrel 虚拟机(Squirrel 3.2,整数 64 位、浮点 32 位)。非线程安全:同一个虚拟机只能在一个线程里用。
    /// 脚本里的字符串是字节串,与 .NET 字符串之间按"一个字节对应一个字符"(Latin-1)转换。
    /// </summary>
    public sealed class SqVm : IDisposable
    {
        private static readonly object s_lock = new object();
        private static readonly Dictionary<IntPtr, SqVm> s_vms = new Dictionary<IntPtr, SqVm>();
        private static readonly SqNative.ClosureFn s_closure = ClosureEntry;
        private static readonly SqNative.LogFn s_log = LogEntry;
        private static readonly IntPtr s_closurePtr = Marshal.GetFunctionPointerForDelegate(s_closure);

        private const string ObjectsKey = "dnf_objs";
        private const string TablesKey = "dnf_tables";

        private IntPtr _h;
        private readonly List<SqNativeFn> _natives = new List<SqNativeFn>();
        private readonly StringBuilder _errors = new StringBuilder();
        private long _nextTableId = 1;

        public SqVm(int stackSize = 1024)
        {
            if (SqNative.dnfsq_integer_size() != 8)
            {
                throw new InvalidOperationException("原生库的整数不是 64 位,与约定不符");
            }
            _h = SqNative.dnfsq_open(stackSize, s_log);
            if (_h == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建 Squirrel 虚拟机");
            }
            lock (s_lock)
            {
                s_vms[_h] = this;
            }
            // 注册表里放两张表:对象表(句柄 -> 对象)和原生表(编号 -> 表)
            SqNative.sq_pushregistrytable(_h);
            PushString(ObjectsKey);
            SqNative.sq_newtable(_h);
            SqNative.sq_newslot(_h, -3, 0);
            PushString(TablesKey);
            SqNative.sq_newtable(_h);
            SqNative.sq_newslot(_h, -3, 0);
            SqNative.sq_pop(_h, 1);
        }

        public IntPtr Handle
        {
            get { return _h; }
        }

        /// <summary>原生库里的 Squirrel 版本号,3.2 是 320。</summary>
        public static int Version
        {
            get { return SqNative.dnfsq_version(); }
        }

        /// <summary>脚本 print 的输出(level 0)与错误、调用栈(level 1)。</summary>
        public event Action<int, string> Log;

        public long StackTop
        {
            get { return SqNative.sq_gettop(_h); }
        }

        public void Dispose()
        {
            if (_h == IntPtr.Zero)
            {
                return;
            }
            IntPtr h = _h;
            _h = IntPtr.Zero;
            lock (s_lock)
            {
                s_vms.Remove(h);
            }
            SqNative.dnfsq_close(h);
        }

        private void EnsureOpen()
        {
            if (_h == IntPtr.Zero)
            {
                throw new ObjectDisposedException("SqVm");
            }
        }

        // ---------------------------------------------------------------- 编译与运行
        /// <summary>只编译不运行,检查语法。</summary>
        public bool TryCompile(string name, byte[] source, out string error)
        {
            EnsureOpen();
            _errors.Length = 0;
            int rc = SqNative.dnfsq_compile_only(_h, source ?? new byte[0], source == null ? 0 : source.Length, SqText.ToCString(name));
            error = rc == 0 ? null : ErrorText();
            return rc == 0;
        }

        /// <summary>编译并在根表上下文里运行一段脚本(典型用途:定义函数与常量)。</summary>
        public bool TryRun(string name, byte[] source, out string error)
        {
            EnsureOpen();
            _errors.Length = 0;
            int rc = SqNative.dnfsq_run_buffer(_h, source ?? new byte[0], source == null ? 0 : source.Length, SqText.ToCString(name));
            error = rc == 0 ? null : ErrorText();
            return rc == 0;
        }

        /// <summary>同上,源码是 .NET 字符串(按 UTF-8 编码成字节;原版脚本用 byte[] 重载,字节原样交给虚拟机)。</summary>
        public bool TryRun(string name, string source, out string error)
        {
            return TryRun(name, Encoding.UTF8.GetBytes(source ?? string.Empty), out error);
        }

        public void Run(string name, byte[] source)
        {
            string error;
            if (!TryRun(name, source, out error))
            {
                throw new SqScriptException(error);
            }
        }

        public void Run(string name, string source)
        {
            Run(name, Encoding.UTF8.GetBytes(source));
        }

        private string ErrorText()
        {
            string compile = SqText.FromCStringUtf8(SqNative.dnfsq_compile_error(_h));
            string text = _errors.ToString();
            if (text.Length == 0)
            {
                text = compile.Length != 0 ? compile : "(没有错误文本)";
            }
            return text.TrimEnd('\n', '\r');
        }

        // ---------------------------------------------------------------- 调用脚本函数
        /// <summary>根表里有没有这个名字的函数。</summary>
        public bool HasFunction(string name)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            try
            {
                SqNative.sq_pushroottable(_h);
                PushString(name);
                if (SqNative.sq_rawget(_h, -2) < 0)
                {
                    return false;
                }
                SqType t = SqNative.ToSqType(SqNative.sq_gettype(_h, -1));
                return t == SqType.Closure || t == SqType.NativeClosure;
            }
            finally
            {
                SqNative.sq_settop(_h, top);
            }
        }

        /// <summary>读根表里的整数(常量);没有或类型不对返回 false。</summary>
        public bool TryGetGlobalInteger(string name, out long value)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            value = 0;
            try
            {
                SqNative.sq_pushroottable(_h);
                PushString(name);
                if (SqNative.sq_rawget(_h, -2) < 0)
                {
                    return false;
                }
                return SqNative.sq_getinteger(_h, -1, out value) >= 0;
            }
            finally
            {
                SqNative.sq_settop(_h, top);
            }
        }

        /// <summary>调用根表里的脚本函数。参数可以是 null、bool、整数、浮点、字符串、<see cref="SqHandle"/>;返回值同理。</summary>
        public object Call(string function, params object[] args)
        {
            object result;
            string error;
            if (!TryCall(function, out result, out error, args))
            {
                throw new SqScriptException(error);
            }
            return result;
        }

        public bool TryCall(string function, out object result, out string error, params object[] args)
        {
            EnsureOpen();
            result = null;
            error = null;
            long top = SqNative.sq_gettop(_h);
            _errors.Length = 0;
            try
            {
                SqNative.sq_pushroottable(_h);
                PushString(function);
                if (SqNative.sq_rawget(_h, -2) < 0)
                {
                    error = "未定义的函数 " + function;
                    return false;
                }
                SqNative.sq_remove(_h, -2);
                SqNative.sq_pushroottable(_h);
                int n = args == null ? 0 : args.Length;
                for (int i = 0; i < n; i++)
                {
                    PushValue(args[i]);
                }
                if (SqNative.sq_call(_h, n + 1, 1, 1) < 0)
                {
                    error = ErrorText();
                    return false;
                }
                result = ReadValue(-1);
                return true;
            }
            finally
            {
                SqNative.sq_settop(_h, top);
            }
        }

        /// <summary>根表里这个名字的脚本函数声明了几个形参(不含 this);不存在或不是脚本函数返回 -1。</summary>
        public int GetFunctionArity(string name)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            try
            {
                SqNative.sq_pushroottable(_h);
                PushString(name);
                if (SqNative.sq_rawget(_h, -2) < 0)
                {
                    return -1;
                }
                return ClosureArityAtTop();
            }
            finally
            {
                SqNative.sq_settop(_h, top);
            }
        }

        /// <summary>栈顶是脚本闭包时返回声明的形参个数(不含 this),否则 -1。</summary>
        private int ClosureArityAtTop()
        {
            if (SqNative.ToSqType(SqNative.sq_gettype(_h, -1)) != SqType.Closure)
            {
                return -1;
            }
            ulong nparams;
            ulong nfree;
            if (SqNative.sq_getclosureinfo(_h, -1, out nparams, out nfree) < 0)
            {
                return -1;
            }
            return (int)nparams - 1;
        }

        /// <summary>
        /// 调用根表里的脚本函数,实参个数按函数声明的形参个数调整:多出来的丢掉,不足的补 null
        /// (Squirrel 要求实参个数与形参个数完全一致,而引擎回调的参数个数在同一类回调的各个脚本里并不统一)。
        /// 函数不存在时返回 false 且 <paramref name="error"/> 为 null;脚本抛出错误时返回 false 且 error 有文本。
        /// </summary>
        public bool TryCallAdapted(string function, out object result, out string error, params object[] args)
        {
            EnsureOpen();
            result = null;
            error = null;
            long top = SqNative.sq_gettop(_h);
            _errors.Length = 0;
            try
            {
                SqNative.sq_pushroottable(_h);
                PushString(function);
                if (SqNative.sq_rawget(_h, -2) < 0)
                {
                    return false;
                }
                SqNative.sq_remove(_h, -2);
                int available = args == null ? 0 : args.Length;
                int declared = ClosureArityAtTop();
                if (declared < 0)
                {
                    if (SqNative.ToSqType(SqNative.sq_gettype(_h, -1)) != SqType.NativeClosure)
                    {
                        return false;
                    }
                    declared = available;
                }
                SqNative.sq_pushroottable(_h);
                for (int i = 0; i < declared; i++)
                {
                    if (i < available)
                    {
                        PushValue(args[i]);
                    }
                    else
                    {
                        SqNative.sq_pushnull(_h);
                    }
                }
                if (SqNative.sq_call(_h, declared + 1, 1, 1) < 0)
                {
                    error = ErrorText();
                    return false;
                }
                result = ReadValue(-1);
                return true;
            }
            finally
            {
                SqNative.sq_settop(_h, top);
            }
        }

        // ---------------------------------------------------------------- 原生表、全局函数、对象
        /// <summary>在根表里登记一个全局原生函数。</summary>
        public void RegisterGlobal(string name, SqNativeFn fn)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            SqNative.sq_pushroottable(_h);
            PushString(name);
            PushClosure(fn, name);
            SqNative.sq_newslot(_h, -3, 0);
            SqNative.sq_settop(_h, top);
        }

        /// <summary>在根表里登记一个整数常量。</summary>
        public void RegisterGlobalInteger(string name, long value)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            SqNative.sq_pushroottable(_h);
            PushString(name);
            SqNative.sq_pushinteger(_h, value);
            SqNative.sq_newslot(_h, -3, 0);
            SqNative.sq_settop(_h, top);
        }

        /// <summary>
        /// 新建一张原生表。exposeGlobal 为真时放进根表的同名槽(静态表用法);否则只作对象原型。
        /// baseTable 的全部方法会被复制进来(继承)。
        /// </summary>
        public SqNativeTable DefineTable(string name, bool exposeGlobal, SqNativeTable baseTable = null)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            long id = _nextTableId++;
            SqNative.sq_newtable(_h);                 // [t]
            PushRegistrySub(TablesKey);               // [t, tables]
            SqNative.sq_pushinteger(_h, id);          // [t, tables, id]
            SqNative.sq_push(_h, -3);                 // [t, tables, id, t]
            SqNative.sq_newslot(_h, -3, 0);           // [t, tables]
            SqNative.sq_pop(_h, 1);                   // [t]
            if (exposeGlobal)
            {
                SqNative.sq_pushroottable(_h);        // [t, root]
                PushString(name);                     // [t, root, key]
                SqNative.sq_push(_h, -3);             // [t, root, key, t]
                SqNative.sq_newslot(_h, -3, 0);       // [t, root]
            }
            SqNative.sq_settop(_h, top);
            var table = new SqNativeTable(this, name, id);
            if (baseTable != null)
            {
                foreach (KeyValuePair<string, SqNativeFn> m in baseTable.Methods)
                {
                    table.Method(m.Key, m.Value);
                }
            }
            return table;
        }

        /// <summary>创建(或替换)一个原生对象:一张带 _h 槽、以 proto 为委托的表,并登记在虚拟机里,保证同一句柄总是同一个对象。</summary>
        public void NewObject(SqNativeTable proto, SqHandle handle)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            SqNative.sq_newtable(_h);                              // [obj]
            PushString("_h");
            SqNative.sq_pushinteger(_h, handle.Id);
            SqNative.sq_newslot(_h, -3, 0);                        // obj._h = id
            PushTable(proto.Id);                                   // [obj, proto]
            SqNative.sq_setdelegate(_h, -2);                       // 弹出 proto
            PushRegistrySub(ObjectsKey);                           // [obj, objs]
            SqNative.sq_pushinteger(_h, handle.Id);                // [obj, objs, id]
            SqNative.sq_push(_h, -3);                              // [obj, objs, id, obj]
            SqNative.sq_newslot(_h, -3, 0);                        // [obj, objs]
            SqNative.sq_settop(_h, top);
        }

        /// <summary>从虚拟机里注销一个原生对象(脚本里残留的引用仍可用,但 PushObject 找不到它)。</summary>
        public void ReleaseObject(SqHandle handle)
        {
            EnsureOpen();
            long top = SqNative.sq_gettop(_h);
            PushRegistrySub(ObjectsKey);
            SqNative.sq_pushinteger(_h, handle.Id);
            SqNative.sq_rawdeleteslot(_h, -2, 0);
            SqNative.sq_settop(_h, top);
        }

        /// <summary>把已登记的原生对象压栈;没登记过则压 null。</summary>
        public void PushObject(SqHandle handle)
        {
            PushRegistrySub(ObjectsKey);                           // [objs]
            SqNative.sq_pushinteger(_h, handle.Id);                // [objs, id]
            if (SqNative.sq_rawget(_h, -2) < 0)                    // 失败时 key 已被弹出:[objs]
            {
                SqNative.sq_pushnull(_h);                          // [objs, null]
            }
            SqNative.sq_remove(_h, -2);                            // [obj]
        }

        /// <summary>读栈上某个位置(必须是表)的 _h 槽。</summary>
        internal bool TryReadHandle(long idx, out SqHandle handle)
        {
            handle = default(SqHandle);
            long top = SqNative.sq_gettop(_h);
            try
            {
                SqNative.sq_push(_h, idx);
                PushString("_h");
                if (SqNative.sq_rawget(_h, -2) < 0)
                {
                    return false;
                }
                long id;
                if (SqNative.sq_getinteger(_h, -1, out id) < 0)
                {
                    return false;
                }
                handle = new SqHandle(id);
                return true;
            }
            finally
            {
                SqNative.sq_settop(_h, top);
            }
        }

        // ---------------------------------------------------------------- 缺省取值(用来发现脚本需要哪些原生接口)
        /// <summary>
        /// 脚本读根表里不存在的名字时,调用 factory(名字):返回原生函数就把它登记成全局函数并返回给脚本,返回 null 表示"确实没有"。
        /// 用来在接口还没实现时探路:factory 里记录名字,返回一个什么也不做的函数。
        /// </summary>
        public void SetMissingGlobalHandler(Func<string, SqNativeFn> factory)
        {
            EnsureOpen();
            SqNativeTable delegateTable = DefineTable("(root delegate)", false);
            delegateTable.Method("_get", MissingLookup(factory, -1));
            long top = SqNative.sq_gettop(_h);
            SqNative.sq_pushroottable(_h);
            PushTable(delegateTable.Id);
            SqNative.sq_setdelegate(_h, -2);
            SqNative.sq_settop(_h, top);
        }

        /// <summary>
        /// 同上,作用在一个对象原型上:对象调用原型里没有的方法时,按名字向 factory 要。
        /// propertyFactory 可选:对象读原型里没有的"属性"(例如 obj.sq_var,不带括号)时先问它,
        /// 返回的函数会被立即调用(this 是这个对象),它的返回值就是属性值,不会缓存;返回 null 表示不是属性,再走 factory。
        /// </summary>
        public void SetMissingMemberHandler(SqNativeTable proto, Func<string, SqNativeFn> factory, Func<string, SqNativeFn> propertyFactory = null)
        {
            EnsureOpen();
            proto.Method("_get", MissingLookup(factory, proto.Id, propertyFactory));
        }

        /// <summary>
        /// 对象写原型里没有的槽(obj.名字 = 值)时调用 setter(call):call 的参数 0 是名字、参数 1 是值;
        /// setter 返回 false 表示不接受,脚本里会得到"索引不存在"的错误。
        /// </summary>
        public void SetMissingSetHandler(SqNativeTable proto, Func<SqCall, bool> setter)
        {
            EnsureOpen();
            proto.Method("_set", call =>
            {
                if (call.TypeOf(0) != SqType.String || !setter(call))
                {
                    throw new SqNotFoundException();
                }
                return call.ReturnNothing();
            });
        }

        private SqNativeFn MissingLookup(Func<string, SqNativeFn> factory, long cacheTableId, Func<string, SqNativeFn> propertyFactory = null)
        {
            return call =>
            {
                if (call.TypeOf(0) != SqType.String)
                {
                    throw new SqNotFoundException();
                }
                string name = call.String(0);
                if (propertyFactory != null)
                {
                    SqNativeFn getter = propertyFactory(name);
                    if (getter != null)
                    {
                        return getter(call);
                    }
                }
                SqNativeFn fn = factory(name);
                if (fn == null)
                {
                    throw new SqNotFoundException();
                }
                // 缓存到目标表(根表或原型),以后同名查找不再经过这里
                long top = SqNative.sq_gettop(_h);
                if (cacheTableId < 0)
                {
                    SqNative.sq_pushroottable(_h);
                }
                else
                {
                    PushTable(cacheTableId);
                }
                PushString(name);
                PushClosure(fn, name);
                SqNative.sq_newslot(_h, -3, 0);
                SqNative.sq_settop(_h, top);
                // 返回同一个函数
                PushClosure(fn, name);
                return 1;
            };
        }

        // ---------------------------------------------------------------- 栈辅助
        internal void SetSlotClosure(long tableId, string name, SqNativeFn fn)
        {
            long top = SqNative.sq_gettop(_h);
            PushTable(tableId);
            PushString(name);
            PushClosure(fn, name);
            SqNative.sq_newslot(_h, -3, 0);
            SqNative.sq_settop(_h, top);
        }

        internal void SetSlotInteger(long tableId, string name, long value)
        {
            long top = SqNative.sq_gettop(_h);
            PushTable(tableId);
            PushString(name);
            SqNative.sq_pushinteger(_h, value);
            SqNative.sq_newslot(_h, -3, 0);
            SqNative.sq_settop(_h, top);
        }

        private void PushRegistrySub(string key)
        {
            SqNative.sq_pushregistrytable(_h);
            PushString(key);
            SqNative.sq_rawget(_h, -2);
            SqNative.sq_remove(_h, -2);
        }

        private void PushTable(long id)
        {
            PushRegistrySub(TablesKey);
            SqNative.sq_pushinteger(_h, id);
            SqNative.sq_rawget(_h, -2);
            SqNative.sq_remove(_h, -2);
        }

        private void PushString(string s)
        {
            byte[] b = SqText.ToCString(s);
            SqNative.sq_pushstring(_h, b, b.Length - 1);
        }

        private void PushClosure(SqNativeFn fn, string name)
        {
            int id;
            lock (_natives)
            {
                _natives.Add(fn);
                id = _natives.Count - 1;
            }
            SqNative.sq_pushinteger(_h, id);                       // 自由变量:原生函数编号
            SqNative.sq_newclosure(_h, s_closurePtr, 1);
            SqNative.sq_setnativeclosurename(_h, -1, SqText.ToCString(name));
        }

        private void PushValue(object v)
        {
            if (v == null)
            {
                SqNative.sq_pushnull(_h);
            }
            else if (v is bool)
            {
                SqNative.sq_pushbool(_h, (bool)v ? 1UL : 0UL);
            }
            else if (v is int)
            {
                SqNative.sq_pushinteger(_h, (int)v);
            }
            else if (v is long)
            {
                SqNative.sq_pushinteger(_h, (long)v);
            }
            else if (v is short || v is byte || v is sbyte || v is ushort)
            {
                SqNative.sq_pushinteger(_h, Convert.ToInt64(v));
            }
            else if (v is uint)
            {
                SqNative.sq_pushinteger(_h, (uint)v);
            }
            else if (v is float)
            {
                SqNative.sq_pushfloat(_h, (float)v);
            }
            else if (v is double)
            {
                SqNative.sq_pushfloat(_h, (float)(double)v);
            }
            else if (v is string)
            {
                PushString((string)v);
            }
            else if (v is SqHandle)
            {
                PushObject((SqHandle)v);
            }
            else
            {
                throw new ArgumentException("不支持的参数类型 " + v.GetType().Name);
            }
        }

        private object ReadValue(long idx)
        {
            SqType t = SqNative.ToSqType(SqNative.sq_gettype(_h, idx));
            switch (t)
            {
                case SqType.Null:
                    return null;
                case SqType.Integer:
                    {
                        long i;
                        SqNative.sq_getinteger(_h, idx, out i);
                        return i;
                    }
                case SqType.Float:
                    {
                        float f;
                        SqNative.sq_getfloat(_h, idx, out f);
                        return f;
                    }
                case SqType.Bool:
                    {
                        ulong b;
                        SqNative.sq_getbool(_h, idx, out b);
                        return b != 0;
                    }
                case SqType.String:
                    {
                        IntPtr p;
                        long size;
                        SqNative.sq_getstringandsize(_h, idx, out p, out size);
                        return SqText.FromPointer(p, size);
                    }
                case SqType.Table:
                    {
                        SqHandle h;
                        if (TryReadHandle(idx < 0 ? SqNative.sq_gettop(_h) + idx + 1 : idx, out h))
                        {
                            return h;
                        }
                        return new SqOpaque(t);
                    }
                default:
                    return new SqOpaque(t);
            }
        }

        // ---------------------------------------------------------------- 原生回调入口
        [MonoPInvokeCallback(typeof(SqNative.ClosureFn))]
        private static long ClosureEntry(IntPtr vm)
        {
            SqVm owner;
            lock (s_lock)
            {
                s_vms.TryGetValue(vm, out owner);
            }
            if (owner == null)
            {
                return SqNative.sq_throwerror(vm, SqText.ToCStringUtf8("原生函数在未登记的虚拟机上被调用"));
            }
            return owner.Dispatch();
        }

        private long Dispatch()
        {
            long top = SqNative.sq_gettop(_h);
            long id;
            if (SqNative.sq_getinteger(_h, top, out id) < 0)
            {
                return SqNative.sq_throwerror(_h, SqText.ToCStringUtf8("原生函数缺少编号"));
            }
            SqNativeFn fn;
            lock (_natives)
            {
                fn = _natives[(int)id];
            }
            var call = new SqCall(this, (int)(top - 1));      // 栈:this、参数...、自由变量(编号)
            try
            {
                return fn(call);
            }
            catch (SqNotFoundException)
            {
                SqNative.sq_pushnull(_h);
                return SqNative.sq_throwobject(_h);
            }
            catch (Exception e)
            {
                return SqNative.sq_throwerror(_h, SqText.ToCStringUtf8(e.GetType().Name + ": " + e.Message));
            }
        }

        [MonoPInvokeCallback(typeof(SqNative.LogFn))]
        private static void LogEntry(IntPtr vm, int level, IntPtr text)
        {
            SqVm owner;
            lock (s_lock)
            {
                s_vms.TryGetValue(vm, out owner);
            }
            if (owner == null)
            {
                return;
            }
            string s = SqText.FromCStringUtf8(text);
            if (level != 0)
            {
                owner._errors.Append(s);
                owner._errors.Append('\n');
            }
            Action<int, string> handler = owner.Log;
            if (handler != null)
            {
                handler(level, s);
            }
        }
    }
}
