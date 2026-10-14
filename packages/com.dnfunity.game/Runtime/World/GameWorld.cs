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

        /// <summary>脚本用 sq_AddMessage(编号) 弹出的界面提示的编号(按先后);没有界面,只记录,测试可以检查。</summary>
        public List<int> UiMessages { get; } = new List<int>();

        /// <summary>sq_LoadSkillEffectAni(技能, 路径) 预载过的特效动画路径(按技能分);只记录,真正的预载由渲染层负责。</summary>
        public Dictionary<int, List<string>> PreloadedSkillEffects { get; } = new Dictionary<int, List<string>>();

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
            IndexPassiveScripts();
            return summary;
        }

        private static string StateKey(string module, int job, int state)
        {
            return module + "|" + job + "|" + state;
        }

        public const string GenericModule = "IRDSQRCharacter";
        public const string AvengerModule = "CNAvenger";

        /// <summary>找某个职业某个状态编号的登记信息;没有脚本的状态返回 null。</summary>
        public StateInfo FindState(int job, int state)
        {
            return FindState(GenericModule, job, state);
        }

        /// <summary>找某个模块、职业、状态编号的登记信息(复仇者模块的状态登记在 <see cref="AvengerModule"/> 下)。</summary>
        public StateInfo FindState(string module, int job, int state)
        {
            return _states.TryGetValue(StateKey(module, job, state), out StateInfo s) ? s : null;
        }

        /// <summary>登记的全部状态(各模块、各职业)。</summary>
        public IEnumerable<StateInfo> AllStates => _states.Values;

        /// <summary>某个职业在某个模块里登记的全部状态(默认是通用模块)。</summary>
        public IEnumerable<StateInfo> StatesOfJob(int job, string module = GenericModule)
        {
            foreach (StateInfo s in _states.Values)
            {
                if (s.Job == job && s.Module == module)
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
        public GameObject SpawnCharacter(int job, string name, int x, int y, int z, int direction, string module = GenericModule)
        {
            GameObject obj = Spawn(ObjectKind.Character, name, x, y, z, direction);
            obj.Job = job;
            obj.Module = module;
            obj.Def = Data.GetCharacter(job);
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
            if (!obj.Valid || obj.Destroying)
            {
                return;
            }
            obj.Destroying = true;
            if (obj.Kind == ObjectKind.PassiveObject && obj.StateInfo != null)
            {
                InvokeCallback("destroy", obj.StateInfo, out _, obj);
                InvokeCallback("onDestroyObject", obj.StateInfo, out _, obj, obj);
            }
            foreach (Appendage a in new List<Appendage>(obj.Appendages))
            {
                EndAppendage(a);
            }
            obj.Valid = false;
            _graveyard.Add(new KeyValuePair<int, NativeObject>(TimeMs + 5000, obj));
        }

        /// <summary>
        /// 把一个临时的宿主对象(独立动画、技能对象等)交给虚拟机,并在 ttlMs 毫秒后从虚拟机注销。
        /// 脚本把它存进变量表长期持有时,过期后再用它会被当成无效对象(null)——这个时限是为了不让句柄表无限增长。
        /// </summary>
        internal void Transient(NativeObject o, int ttlMs = 60000)
        {
            Handle(o);
            _graveyard.Add(new KeyValuePair<int, NativeObject>(TimeMs + ttlMs, o));
        }

        // 已创建、还没有交给世界(sq_AddObject)的特效对象:超时仍没有加入的丢弃
        private readonly List<KeyValuePair<int, GameObject>> _pending = new List<KeyValuePair<int, GameObject>>();

        /// <summary>
        /// 创建一个只画不算的特效对象。addNow 为假时先放在"待加入"里,脚本调用 sq_AddObject 后才进入世界;
        /// 动画不循环时,autoDestroy 为真则播完自动销毁,否则最多存活 60 秒。
        /// </summary>
        internal GameObject CreateDrawOnly(GameObject owner, string aniPath, bool autoDestroy, bool addNow)
        {
            var obj = new GameObject(this, ObjectKind.Other, "draw")
            {
                X = owner?.X ?? 0,
                Y = owner?.Y ?? 0,
                Z = owner?.Z ?? 0,
                Direction = owner?.Direction ?? Direction.Right,
            };
            obj.Parent = owner;
            obj.Source = owner;
            obj.AutoDestroyOnAnimationEnd = autoDestroy;
            obj.LifeLimitMs = 60000;
            obj.Animator.Set(aniPath == null ? null : Data.GetAnimation(aniPath), aniPath);
            Handle(obj);
            if (addNow)
            {
                _objects.Add(obj);
            }
            else
            {
                _pending.Add(new KeyValuePair<int, GameObject>(TimeMs + 60000, obj));
            }
            return obj;
        }

        /// <summary>把待加入的特效对象交给世界(sq_AddObject);已经在世界里的不重复加入。</summary>
        internal void AddToWorld(GameObject child, GameObject parent)
        {
            if (child == null || !child.Valid)
            {
                return;
            }
            if (parent != null)
            {
                child.Parent = parent;
            }
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pending[i].Value, child))
                {
                    _pending.RemoveAt(i);
                }
            }
            if (!_objects.Contains(child))
            {
                _objects.Add(child);
            }
        }

        private void SweepGraveyard()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Key <= TimeMs)
                {
                    GameObject lost = _pending[i].Value;
                    _pending.RemoveAt(i);
                    Release(lost.DataVector);
                    Release(lost);
                }
            }
            for (int i = _graveyard.Count - 1; i >= 0; i--)
            {
                if (_graveyard[i].Key <= TimeMs)
                {
                    NativeObject dead = _graveyard[i].Value;
                    if (dead is GameObject g)
                    {
                        foreach (VarStore v in g.AllVarStores())
                        {
                            ReleaseVarStore(v);
                        }
                        Release(g.DataVector);
                        if (g.CurrentAnimationView != null)
                        {
                            Release(g.CurrentAnimationView);
                        }
                        if (g.CurrentAttackInfo != null)
                        {
                            Release(g.CurrentAttackInfo);
                        }
                    }
                    Release(dead);
                    _graveyard.RemoveAt(i);
                }
            }
        }

        /// <summary>注销一张变量表和它名下的计时器、动画、粒子发生器等对象。</summary>
        internal void ReleaseVarStore(VarStore v)
        {
            foreach (NativeObject o in v.CtVector)
            {
                Release(o);
            }
            foreach (NativeObject o in v.TimerVector)
            {
                Release(o);
            }
            foreach (NativeObject o in v.Slots.Values)
            {
                if (o is StubObject)
                {
                    Release(o);
                }
            }
            Release(v);
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
            StepMs = dtMs;
            RunDelayed();
            var snapshot = new List<GameObject>(_objects);
            foreach (GameObject obj in snapshot)
            {
                if (!obj.Valid)
                {
                    continue;
                }
                obj.AgeMs += dtMs;
                obj.StateTimeMs += dtMs;
                if (obj.LifeLimitMs >= 0 && obj.AgeMs >= obj.LifeLimitMs)
                {
                    Destroy(obj);
                    continue;
                }
                if (obj.Kind == ObjectKind.PassiveObject)
                {
                    DrainPackets(obj);
                    InvokeCallback("procAppend", obj, obj);
                }
                else
                {
                    ProcessSkillRequest(obj);
                    DrainPackets(obj);
                    InvokeCallback("onProc", obj, obj);
                    InvokeCallback("onProcCon", obj, obj);
                }
                IntegrateMove(obj, dtMs);
                AdvanceTimeEvents(obj, dtMs);
                DrainPackets(obj);
                AdvanceAppendages(obj, dtMs);
                AdvanceChangeStatuses(obj, dtMs);
                AdvanceAnimation(obj, dtMs);
                DrainPackets(obj);
            }
            ResolveHits();
            foreach (GameObject obj in snapshot)
            {
                DrainPackets(obj);
            }
            DrainWorldPackets();
            _objects.RemoveAll(o => !o.Valid);
            SweepGraveyard();
        }

        // 延时动作(sq_PostDelayedMessage 等):到点的在下一次推进开头按登记顺序执行
        private readonly List<KeyValuePair<int, Action>> _delayed = new List<KeyValuePair<int, Action>>();

        /// <summary>delayMs 毫秒(游戏时间)之后执行一次 action;动作里不应抛异常。</summary>
        internal void PostDelayed(int delayMs, Action action)
        {
            _delayed.Add(new KeyValuePair<int, Action>(TimeMs + Math.Max(0, delayMs), action));
        }

        private void RunDelayed()
        {
            if (_delayed.Count == 0)
            {
                return;
            }
            var due = new List<Action>();
            for (int i = 0; i < _delayed.Count; )
            {
                if (_delayed[i].Key <= TimeMs)
                {
                    due.Add(_delayed[i].Value);
                    _delayed.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
            foreach (Action a in due)
            {
                a();
            }
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
                        if (obj.Valid && ((obj.Def is PassiveObjectDef po && po.DestroyOnAnimationEnd) || obj.AutoDestroyOnAnimationEnd))
                        {
                            Destroy(obj);
                        }
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
