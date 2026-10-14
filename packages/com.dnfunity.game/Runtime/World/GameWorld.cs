using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Script;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 一个最小的游戏世界:对象列表、状态机、计时事件、动画与命中判定,驱动原版的 Squirrel 状态脚本。
    /// 世界按毫秒推进(<see cref="Step"/>);脚本通过原生接口(见 Natives/)读写世界,
    /// 引擎回调(onSetState、onTimeEvent、onKeyFrameFlag、onEndCurrentAni、onAttack 等)由这里按状态的后缀名调用。
    /// 引擎侧的具体语义(回调顺序、计时事件何时清除等)多数是依据脚本用法的推断,逐条记在证据台账里。
    /// </summary>
    public sealed partial class GameWorld : IDisposable
    {
        private readonly Dictionary<long, NativeObject> _byId = new Dictionary<long, NativeObject>();
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly Dictionary<string, StateInfo> _states = new Dictionary<string, StateInfo>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<int, NativeObject>> _graveyard = new List<KeyValuePair<int, NativeObject>>();
        private long _nextId = 1;
        private bool _disposed;

        public GameData Data { get; }
        public ScriptHost Host { get; }
        public SqVm Vm => Host.Vm;

        /// <summary>世界时间(毫秒)。</summary>
        public int TimeMs { get; private set; }

        /// <summary>是否按决斗场规则取技能数据(SkillDef 里 [pvp] 的数据)。</summary>
        public bool PvpMode { get; set; }

        public IWorldEvents Events { get; set; } = NullWorldEvents.Instance;

        /// <summary>脚本回调里发生的错误(每项一行:回调名 + 首行错误文本)。</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>回调调用记录(<see cref="TraceCallbacks"/> 为真时累计:函数名与参数摘要)。</summary>
        public List<string> CallbackTrace { get; } = new List<string>();

        public bool TraceCallbacks { get; set; }

        public IReadOnlyList<GameObject> Objects => _objects;

        public GameWorld(GameData data, ScriptHost host)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            SetUpNativeTables();
            RegisterAllNatives();
        }

        /// <summary>
        /// 运行脚本入口(加载链),并把登记的状态建成查找表。返回加载摘要。
        /// 状态表按"模块(IRDSQRCharacter 或 CNAvenger)、职业、状态编号"索引。
        /// </summary>
        public ScriptLoadSummary LoadScripts()
        {
            ScriptLoadSummary summary = Host.LoadAll();
            _states.Clear();
            foreach (ScriptRegistration r in Host.Registrations)
            {
                if (r.Kind == RegistrationKind.State)
                {
                    _states[StateKey(r.Table, (int)r.Job, (int)r.Id)] = new StateInfo(r);
                }
            }
            return summary;
        }

        private static string StateKey(string module, int job, int state)
        {
            return module + "|" + job + "|" + state;
        }

        public const string GenericModule = "IRDSQRCharacter";

        /// <summary>找某个职业某个状态编号的登记信息;没有脚本的状态返回 null。</summary>
        public StateInfo FindState(int job, int state)
        {
            return _states.TryGetValue(StateKey(GenericModule, job, state), out StateInfo s) ? s : null;
        }

        /// <summary>某个职业登记的全部状态。</summary>
        public IEnumerable<StateInfo> StatesOfJob(int job)
        {
            foreach (StateInfo s in _states.Values)
            {
                if (s.Job == job && s.Module == GenericModule)
                {
                    yield return s;
                }
            }
        }

        // ---------------------------------------------------------------- 对象与句柄
        /// <summary>把宿主对象交给虚拟机(第一次时分配编号并创建脚本里的对象)。</summary>
        internal SqHandle Handle(NativeObject o)
        {
            if (o.Id == 0)
            {
                o.Id = _nextId++;
                o.World = this;
                _byId[o.Id] = o;
                Vm.NewObject(_objProto, new SqHandle(o.Id));
            }
            return new SqHandle(o.Id);
        }

        internal NativeObject Resolve(SqHandle h)
        {
            return _byId.TryGetValue(h.Id, out NativeObject o) ? o : null;
        }

        /// <summary>从虚拟机里注销一个宿主对象(脚本里残留的引用此后解析不到)。</summary>
        internal void Release(NativeObject o)
        {
            if (o.Id != 0)
            {
                Vm.ReleaseObject(new SqHandle(o.Id));
                _byId.Remove(o.Id);
            }
        }

        public GameObject Spawn(ObjectKind kind, string name, int x, int y, int z, int direction)
        {
            var obj = new GameObject(this, kind, name) { X = x, Y = y, Z = z, Direction = direction };
            _objects.Add(obj);
            Handle(obj);
            return obj;
        }

        /// <summary>创建一个玩家角色:读 .chr,状态置为站立,本机控制。</summary>
        public GameObject SpawnCharacter(int job, string name, int x, int y, int z, int direction)
        {
            GameObject obj = Spawn(ObjectKind.Character, name, x, y, z, direction);
            obj.Job = job;
            obj.Character = Data.GetCharacter(job);
            obj.IsMyControl = true;
            obj.MaxHp = obj.Hp = 1000;
            obj.MaxMp = obj.Mp = 1000;
            EnterState(obj, 0, Array.Empty<long>(), false, 0);
            return obj;
        }

        /// <summary>创建一个只有固定受击框的假人(怪物):用来承受攻击。</summary>
        public GameObject SpawnDummy(string name, int x, int y, int z, Aabb relativeBox)
        {
            GameObject obj = Spawn(ObjectKind.Monster, name, x, y, z, Direction.Left);
            obj.FixedDamageBox = relativeBox;
            obj.MaxHp = obj.Hp = 100000;
            return obj;
        }

        /// <summary>销毁一个对象:先结束它身上的依附体,再标记无效。对象在虚拟机里还保留一小段时间,避免脚本里残留的引用立刻失效。</summary>
        public void Destroy(GameObject obj)
        {
            if (!obj.Valid)
            {
                return;
            }
            foreach (Appendage a in new List<Appendage>(obj.Appendages))
            {
                EndAppendage(a);
            }
            obj.Valid = false;
            _graveyard.Add(new KeyValuePair<int, NativeObject>(TimeMs + 5000, obj));
        }

        private void SweepGraveyard()
        {
            for (int i = _graveyard.Count - 1; i >= 0; i--)
            {
                if (_graveyard[i].Key <= TimeMs)
                {
                    var g = (GameObject)_graveyard[i].Value;
                    foreach (VarStore v in g.AllVarStores())
                    {
                        Release(v);
                    }
                    Release(g.DataVector);
                    Release(g);
                    _graveyard.RemoveAt(i);
                }
            }
        }

        // ---------------------------------------------------------------- 推进
        /// <summary>推进世界 dtMs 毫秒。</summary>
        public void Step(int dtMs)
        {
            if (dtMs <= 0)
            {
                return;
            }
            TimeMs += dtMs;
            var snapshot = new List<GameObject>(_objects);
            foreach (GameObject obj in snapshot)
            {
                if (!obj.Valid)
                {
                    continue;
                }
                obj.AgeMs += dtMs;
                ProcessSkillRequest(obj);
                DrainPackets(obj);
                InvokeCallback("onProc", obj, obj);
                InvokeCallback("onProcCon", obj, obj);
                IntegrateMove(obj, dtMs);
                AdvanceTimeEvents(obj, dtMs);
                DrainPackets(obj);
                AdvanceAppendages(obj, dtMs);
                AdvanceAnimation(obj, dtMs);
                DrainPackets(obj);
            }
            ResolveHits();
            foreach (GameObject obj in snapshot)
            {
                DrainPackets(obj);
            }
            _objects.RemoveAll(o => !o.Valid);
            SweepGraveyard();
        }

        private void AdvanceTimeEvents(GameObject obj, int dtMs)
        {
            obj.TimeEvents.Advance(dtMs, (index, count) =>
            {
                if (!obj.Valid)
                {
                    return true;
                }
                // 回调返回真表示脚本要求中止这个计时事件(onTimeEvent_IceCrash 的注释:true 则回调中断)
                bool stop = InvokeCallbackBool("onTimeEvent", obj, obj, (long)index, (long)count);
                DrainPackets(obj);
                return stop;
            });
        }

        private void AdvanceAnimation(GameObject obj, int dtMs)
        {
            obj.Animator.Advance(
                dtMs,
                flag =>
                {
                    if (obj.Valid)
                    {
                        InvokeCallback("onKeyFrameFlag", obj, obj, (long)flag);
                        DrainPackets(obj);
                    }
                },
                () =>
                {
                    if (obj.Valid)
                    {
                        InvokeCallback("onEndCurrentAni", obj, obj);
                        DrainPackets(obj);
                    }
                });
        }

        private void IntegrateMove(GameObject obj, int dtMs)
        {
            StaticMove m = obj.Move;
            if (!m.Active)
            {
                return;
            }
            obj.X += StepAxis(m.DirectionX, m.SpeedX, dtMs, ref m.RemainderX);
            obj.Y += StepAxis(m.DirectionY, m.SpeedY, dtMs, ref m.RemainderY);
        }

        private static int StepAxis(int direction, int speedPerSecond, int dtMs, ref float remainder)
        {
            if (speedPerSecond == 0 || direction == Direction.Neutral)
            {
                return 0;
            }
            float delta = speedPerSecond * dtMs / 1000f + remainder;
            int whole = (int)delta;
            remainder = delta - whole;
            // 左与上为负方向,右与下为正方向(上下在纵深 y 轴上的取向是约定,未验证)
            bool negative = direction == Direction.Left || direction == Direction.Up;
            return negative ? -whole : whole;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Host.Dispose();
        }
    }
}
