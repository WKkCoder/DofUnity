using System;
using System.Collections.Generic;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>一个原生接口实现的可信程度。</summary>
    public enum NativeStatus
    {
        /// <summary>只是占位:记录调用并返回中性值,语义没有实现。</summary>
        Trace = 0,

        /// <summary>按名字与脚本里的用法推断的实现,没有独立证据。</summary>
        Inferred = 1,

        /// <summary>与数据或独立证据核对过(见证据台账)。</summary>
        Verified = 2,
    }

    /// <summary>接口在脚本里可以怎样写:对象成员(obj.sq_X(...))、全局函数(sq_X(obj, ...)),或两种都行。</summary>
    [Flags]
    public enum NativeForm
    {
        Member = 1,
        Global = 2,
        Both = Member | Global,
    }

    internal delegate int NativeBody(NativeCall call);

    /// <summary>一个已登记的原生接口的元信息。</summary>
    public sealed class NativeInfo
    {
        public string Name { get; internal set; }
        public NativeStatus Status { get; internal set; }
        public NativeForm Forms { get; internal set; }

        /// <summary>一句话说明语义与依据(中文)。</summary>
        public string Note { get; internal set; }

        /// <summary>被脚本调用的次数(只在运行时累计)。</summary>
        public long Calls { get; internal set; }
    }

    public sealed partial class GameWorld
    {
        private readonly Dictionary<string, NativeInfo> _natives = new Dictionary<string, NativeInfo>(StringComparer.Ordinal);
        private SqNativeTable _objProto;

        /// <summary>已实现的原生接口。</summary>
        public IReadOnlyDictionary<string, NativeInfo> Natives => _natives;

        /// <summary>脚本调用了、但没有登记的对象成员名字及次数(替身函数记数,用来发现缺什么)。</summary>
        public IReadOnlyDictionary<string, int> UnknownMembers => _unknownMembers;

        private readonly SortedDictionary<string, int> _unknownMembers = new SortedDictionary<string, int>(StringComparer.Ordinal);

        /// <summary>每次原生调用前被调用(用于记录调用序列);为 null 则不记录。参数:接口名、对象、已解析好的调用现场。</summary>
        public Action<string, NativeObject> OnNativeCall { get; set; }

        // 最近几次原生调用的名字:脚本出错时附在错误信息后面,方便定位是哪个接口之后出的问题
        private readonly string[] _recentNatives = new string[8];
        private int _recentPos;

        private void Note(string name)
        {
            _recentNatives[_recentPos++ & 7] = name;
        }

        /// <summary>最近 count 次(最多 8 次)原生调用的名字,按时间先后用逗号连起来。</summary>
        internal string RecentNatives(int count)
        {
            var names = new List<string>();
            for (int i = Math.Min(count, 8); i >= 1; i--)
            {
                string n = _recentNatives[(_recentPos - i) & 7];
                if (n != null)
                {
                    names.Add(n);
                }
            }
            return string.Join(",", names);
        }

        private void SetUpNativeTables()
        {
            _objProto = Vm.DefineTable("NativeObject", false);
            Vm.SetMissingMemberHandler(_objProto, name =>
            {
                if (!_unknownMembers.ContainsKey(name))
                {
                    _unknownMembers[name] = 0;
                }
                return c =>
                {
                    _unknownMembers[name] = _unknownMembers[name] + 1;
                    OnNativeCall?.Invoke(name + " (未实现)", null);
                    return c.ReturnNull();
                };
            }, PropertyGetter);
            // 攻击信息"包"(sq_getNewAttackInfoPacket)的字段直接用属性写法赋值:info.sizeRate = 100;只记录
            Vm.SetMissingSetHandler(_objProto, call =>
            {
                if (!(ResolveOrNull(call, -1) is AttackInfo info))
                {
                    return false;
                }
                string key = call.String(0);
                switch (call.TypeOf(1))
                {
                    case SqType.Integer:
                        info.Properties[key] = call.Int(1);
                        return true;
                    case SqType.Float:
                        info.Properties[key] = call.Float(1);
                        return true;
                    case SqType.Bool:
                        info.Properties[key] = call.Bool(1);
                        return true;
                    case SqType.String:
                        info.Properties[key] = call.String(1);
                        return true;
                    default:
                        return false;
                }
            });
        }

        internal void Define(string name, NativeStatus status, string note, NativeForm forms, NativeBody body)
        {
            // 全局写法只给 sq_ 开头的接口:其余接口只作对象成员存在(也避免占用脚本自己定义的全局函数名)
            if (forms == NativeForm.Both && !name.StartsWith("sq_", StringComparison.Ordinal))
            {
                forms = NativeForm.Member;
            }
            var info = new NativeInfo { Name = name, Status = status, Forms = forms, Note = note };
            _natives[name] = info;
            if ((forms & NativeForm.Member) != 0)
            {
                _objProto.Method(name, call =>
                {
                    info.Calls++;
                    NativeObject self = ResolveOrNull(call, -1);
                    Note(name);
                    OnNativeCall?.Invoke(name, self);
                    return body(new NativeCall(this, call, 0, self));
                });
            }
            if ((forms & NativeForm.Global) != 0)
            {
                Vm.RegisterGlobal(name, call =>
                {
                    info.Calls++;
                    NativeObject self = ResolveOrNull(call, 0);
                    Note(name);
                    OnNativeCall?.Invoke(name, self);
                    return body(new NativeCall(this, call, 1, self));
                });
            }
        }

        /// <summary>
        /// 成员写法与全局写法的实参完全相同、都不含"对象"的接口:obj.sq_X(a, b) 与 sq_X(a, b) 等价(例如 sq_GetVectorData(数组, 下标))。
        /// 成员写法下 <see cref="NativeCall.Self"/> 是 this,全局写法下为 null。
        /// </summary>
        internal void DefineAny(string name, NativeStatus status, string note, NativeBody body)
        {
            var info = new NativeInfo { Name = name, Status = status, Forms = NativeForm.Both, Note = note };
            _natives[name] = info;
            _objProto.Method(name, call =>
            {
                info.Calls++;
                NativeObject self = ResolveOrNull(call, -1);
                Note(name);
                OnNativeCall?.Invoke(name, self);
                return body(new NativeCall(this, call, 0, self));
            });
            Vm.RegisterGlobal(name, call =>
            {
                info.Calls++;
                Note(name);
                OnNativeCall?.Invoke(name, null);
                return body(new NativeCall(this, call, 0, null));
            });
        }

        /// <summary>只有全局写法、不需要对象的接口(例如数学函数、随机数)。</summary>
        internal void DefineFree(string name, NativeStatus status, string note, NativeBody body)
        {
            var info = new NativeInfo { Name = name, Status = status, Forms = NativeForm.Global, Note = note };
            _natives[name] = info;
            Vm.RegisterGlobal(name, call =>
            {
                info.Calls++;
                Note(name);
                OnNativeCall?.Invoke(name, null);
                return body(new NativeCall(this, call, 0, null));
            });
        }

        /// <summary>argIndex 为 -1 取 this,否则取第 argIndex 个参数;不是有效的宿主对象则返回 null。</summary>
        private NativeObject ResolveOrNull(SqCall call, int argIndex)
        {
            SqHandle h;
            if (argIndex < 0)
            {
                try
                {
                    h = call.This();
                }
                catch (SqNativeException)
                {
                    return null;
                }
            }
            else if (!call.TryHandle(argIndex, out h))
            {
                return null;
            }
            return Resolve(h);
        }
    }
}
