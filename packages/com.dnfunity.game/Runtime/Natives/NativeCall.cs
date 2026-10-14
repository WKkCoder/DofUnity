using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 原生接口被脚本调用时的现场,在 <see cref="SqCall"/> 之上统一了两种写法:
    /// 成员写法 obj.sq_X(a, b) 与全局写法 sq_X(obj, a, b)。接口实现只看"对象 + 其余参数",参数编号从 0 起且不含对象。
    /// </summary>
    public readonly struct NativeCall
    {
        private readonly SqCall _c;
        private readonly int _offset;

        internal NativeCall(GameWorld world, SqCall call, int offset, NativeObject self)
        {
            World = world;
            _c = call;
            _offset = offset;
            Self = self;
        }

        public GameWorld World { get; }

        /// <summary>被调用的对象(成员写法的 this,全局写法的第一个参数);对象无效时为 null。</summary>
        public NativeObject Self { get; }

        /// <summary>除对象以外脚本传来的参数个数。</summary>
        public int ArgCount => _c.ArgCount - _offset;

        public bool Has(int i)
        {
            return i >= 0 && i < ArgCount;
        }

        public SqType TypeOf(int i)
        {
            return _c.TypeOf(i + _offset);
        }

        public bool IsNull(int i)
        {
            return !Has(i) || _c.IsNull(i + _offset);
        }

        public long Int(int i)
        {
            return _c.Int(i + _offset);
        }

        public int Int32(int i)
        {
            return _c.Int32(i + _offset);
        }

        public long IntOr(int i, long fallback)
        {
            return Has(i) ? _c.IntOr(i + _offset, fallback) : fallback;
        }

        public float Float(int i)
        {
            return _c.Float(i + _offset);
        }

        public float FloatOr(int i, float fallback)
        {
            return Has(i) ? _c.FloatOr(i + _offset, fallback) : fallback;
        }

        public bool Bool(int i)
        {
            return _c.Bool(i + _offset);
        }

        public bool BoolOr(int i, bool fallback)
        {
            return Has(i) && !_c.IsNull(i + _offset) ? _c.Bool(i + _offset) : fallback;
        }

        public string Str(int i)
        {
            return _c.String(i + _offset);
        }

        /// <summary>第 i 个参数是原生对象时返回它(类型不符或对象已无效返回 null);参数不是对象(例如 null)也返回 null。</summary>
        public T Arg<T>(int i) where T : NativeObject
        {
            if (!Has(i) || !_c.TryHandle(i + _offset, out SqHandle h))
            {
                return null;
            }
            return World.Resolve(h) as T;
        }

        /// <summary>对象本身,按类型取;类型不符时抛出脚本错误(说明脚本把方法用在了不支持的对象上)。</summary>
        public T SelfAs<T>() where T : NativeObject
        {
            if (Self is T t)
            {
                return t;
            }
            throw new SqNativeException("这个接口不能用在 " + (Self == null ? "无效对象" : Self.TypeName) + " 上,需要 " + typeof(T).Name);
        }

        public int Return(long v)
        {
            return _c.Return(v);
        }

        public int Return(int v)
        {
            return _c.Return(v);
        }

        public int Return(float v)
        {
            return _c.Return(v);
        }

        public int Return(bool v)
        {
            return _c.Return(v);
        }

        public int Return(string v)
        {
            return _c.Return(v);
        }

        /// <summary>返回一个宿主对象(必要时先交给虚拟机);null 返回脚本里的 null。</summary>
        public int Return(NativeObject o)
        {
            if (o == null)
            {
                return _c.ReturnNull();
            }
            return _c.Return(World.Handle(o));
        }

        public int ReturnNull()
        {
            return _c.ReturnNull();
        }

        public int ReturnNothing()
        {
            return _c.ReturnNothing();
        }
    }
}
