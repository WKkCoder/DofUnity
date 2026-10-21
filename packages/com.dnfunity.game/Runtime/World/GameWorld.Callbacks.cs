using System;
using System.Collections.Generic;
using System.Text;
using Dnf.Data;
using Dnf.Script;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>一次命中的记录(测试与诊断用)。</summary>
    public sealed class HitRecord
    {
        public int TimeMs;
        public string Attacker;
        public string Target;
        public string AttackInfo;
        public float? BonusRate;
        public bool Forced;
    }

    public sealed partial class GameWorld
    {
        /// <summary>命中记录(每次攻击框与受击框首次重叠、或脚本强制发送命中时增加一条)。</summary>
        public List<HitRecord> Hits { get; } = new List<HitRecord>();

        // ---------------------------------------------------------------- 回调调用
        private object[] ToScript(object[] args)
        {
            var r = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                r[i] = args[i] is NativeObject n ? (object)Handle(n) : args[i];
            }
            return r;
        }

        private static bool IsTrue(object result)
        {
            switch (result)
            {
                case null:
                    return false;
                case bool b:
                    return b;
                case long l:
                    return l != 0;
                case float f:
                    return f != 0f;
                default:
                    return true;
            }
        }

        /// <summary>调用根表里一个名字的脚本函数(参数个数按函数声明调整);函数不存在返回 false;脚本出错记入 <see cref="Errors"/> 并返回 false。</summary>
        internal bool CallScript(string function, out object result, params object[] args)
        {
            object[] converted = ToScript(args);
            if (TraceCallbacks && Vm.HasFunction(function))
            {
                CallbackTrace.Add(Describe(function, converted));
            }
            if (Vm.TryCallAdapted(function, out result, out string error, converted))
            {
                return true;
            }
            if (error != null)
            {
                Errors.Add(function + ": " + FirstLine(error) + " [最近的原生调用:" + RecentNatives(4) + "]");
            }
            return false;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string t = text.TrimStart('\n', '\r');
            int i = t.IndexOf('\n');
            return i < 0 ? t : t.Substring(0, i);
        }

        private static string Describe(string function, object[] args)
        {
            var sb = new StringBuilder(function).Append('(');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(args[i] == null ? "null" : args[i] is string s ? "\"" + s + "\"" : args[i].ToString());
            }
            return sb.Append(')').ToString();
        }

        /// <summary>调用 owner 当前状态的回调 prefix_后缀;owner 没有脚本状态时什么也不做。返回回调是否存在并运行完。</summary>
        internal bool InvokeCallback(string prefix, GameObject owner, params object[] args)
        {
            return InvokeCallback(prefix, owner.StateInfo, out _, args);
        }

        internal bool InvokeCallbackBool(string prefix, GameObject owner, params object[] args)
        {
            return InvokeCallback(prefix, owner.StateInfo, out object result, args) && IsTrue(result);
        }

        internal bool InvokeCallback(string prefix, StateInfo info, out object result, params object[] args)
        {
            result = null;
            if (info == null)
            {
                return false;
            }
            return CallScript(CallbackName(prefix, info), out result, args);
        }

        private readonly Dictionary<string, string> _callbackNames = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 状态回调的函数名:前缀_后缀。后缀与脚本里的写法大小写不同时(例如后缀 spincutter、函数 checkExecutableSkill_Spincutter),
        /// 退而求其次按不分大小写找(推断,见 <see cref="ScriptHost.ResolveFunctionIgnoringCase"/>)。
        /// </summary>
        internal string CallbackName(string prefix, StateInfo info)
        {
            string name = prefix + "_" + info.Suffix;
            if (_callbackNames.TryGetValue(name, out string resolved))
            {
                return resolved;
            }
            if (Vm.HasFunction(name))
            {
                return name;
            }
            string alt = Host.ResolveFunctionIgnoringCase(name);
            if (alt != null && Vm.HasFunction(alt))
            {
                _callbackNames[name] = alt;
                return alt;
            }
            return name;
        }

        // ---------------------------------------------------------------- 状态切换
        /// <summary>怪物在"当前状态或待处理状态是死亡"时不再接受新的切换请求(STATE_DIE,E-099)。</summary>
        private const int StateDie = 5;

        /// <summary>
        /// "切换状态"请求的落地(sq_AddSetStatePacket、addSetStatePacket、sendStatePacket 等):放进对象的待处理位置,在本次回调结束后由 <see cref="DrainPackets"/> 处理。
        /// 数据数组由调用方给出(sq_AddSetStatePacket 只在第 3 个参数为真时带上对象的数据数组)。
        /// 规则读自客户端的 setState(0x008779b0,E-099):对象只有<b>一个</b>待处理的请求;没有待处理请求,或待处理请求的优先级不高于新请求
        /// (待处理 ≤ 新)时,新请求替换它(数据数组一并替换);否则新请求被丢弃。怪物(类型位 0x211)在当前状态或待处理状态已是 STATE_DIE 时
        /// 一律拒绝;客户端对"类型位 0x111 且角色里 +0x551D 标志为真"的角色也是这样,那个标志的含义没查,这里不处理。
        /// 客户端另有两道门:对象里一个标志字节(+0x660,含义没查)、"本机控制或强制发送";单机里所有对象都归本机控制,后者不影响,前者当作恒为真。
        /// 与客户端的差异:客户端把待处理的请求作为网络消息发给服务端,收到回应才真正切换;这里在回调结束后、同一帧里切换。
        /// </summary>
        internal void QueueState(GameObject obj, int state, int priority, bool resetTimer, long[] data)
        {
            if (obj.Kind == ObjectKind.Monster && (obj.State == StateDie || (obj.Packets.Count > 0 && obj.Packets.Peek().State == StateDie)))
            {
                return;
            }
            if (obj.Packets.Count > 0)
            {
                if (obj.Packets.Peek().Priority > priority)
                {
                    return;
                }
                obj.Packets.Clear();
            }
            obj.Packets.Enqueue(new StatePacket
            {
                State = state,
                Priority = priority,
                ResetTimer = resetTimer,
                Data = data,
            });
        }

        /// <summary>
        /// 调试与测试用:不经过 checkCommandEnable / checkExecutableSkill,直接让对象在下一次推进里进入某个状态
        /// (优先级取"无视强制"(STATE_PRIORITY_IGNORE_FORCE = 4),重置计时器,数据由调用方给出,默认空)。
        /// 用来检查那些只能由别的流程触发的状态(如觉醒后才可用的技能)本身会不会出错;不代表游戏里真能这样进入。
        /// </summary>
        public void ForceState(GameObject obj, int state, params long[] data)
        {
            QueueState(obj, state, 4, true, data ?? new long[0]);
            DrainPackets(obj);
        }

        internal void DrainPackets(GameObject obj)
        {
            DrainObjectPackets(obj);
            DrainWorldPackets();
        }

        private void DrainObjectPackets(GameObject obj)
        {
            int guard = 0;
            while (obj.Packets.Count > 0 && guard++ < 256)
            {
                StatePacket p = obj.Packets.Dequeue();
                if (!obj.Valid)
                {
                    obj.Packets.Clear();
                    return;
                }
                EnterState(obj, p.State, p.Data, p.ResetTimer, p.Priority);
            }
            if (obj.Packets.Count > 0)
            {
                Errors.Add("状态切换队列超过 256 次:" + obj);
                obj.Packets.Clear();
            }
        }

        /// <summary>
        /// 切换状态:onEndState(旧) → 状态编号不同则清空计时事件 → 换状态 → onSetState(新) → onAfterSetState(新)。
        /// 顺序与"状态不同才清计时事件"是依据 IceCrash 脚本(同状态重入 onSetState、isExistTimeEvent 的判断与 onEndState 的 newState 比较)推断的,未验证。
        /// </summary>
        internal void EnterState(GameObject obj, int newState, long[] data, bool resetTimer, int priority)
        {
            if (obj.Kind == ObjectKind.PassiveObject)
            {
                // 被动对象没有 onSetState 一套回调,只有 setState_po_名字(对象, 状态, 数据)
                obj.State = newState;
                if (obj.StateInfo != null)
                {
                    var pvec = new IntVector(data);
                    Handle(pvec);
                    InvokeCallback("setState", obj.StateInfo, out _, obj, (long)newState, pvec);
                    Release(pvec);
                }
                return;
            }
            StateInfo old = obj.StateInfo;
            int oldState = obj.State;
            if (old != null)
            {
                InvokeCallback("onEndState", old, out _, obj, (long)newState);
            }
            if (newState != oldState)
            {
                obj.TimeEvents.Clear();
            }
            if (resetTimer)
            {
                // 客户端的状态执行函数(0x008360e0)第 3 个参数为真时重置的是"状态计时器"(sq_GetStateTimer 读的那个,对象 +0x30A0),
                // 不碰 getTimer() 的对象计时器(+0x54,脚本里只有依附体在用)(E-099)。它在脚本的 onSetState 之后才重置,这里先于脚本重置(已知差异)
                obj.StateTimeMs = 0;
            }
            obj.State = newState;
            StateInfo info = obj.Job >= 0 ? FindState(obj.Module, obj.Job, newState) : null;
            obj.StateInfo = info;
            if (info == null && newState == 0 && obj.Character != null)
            {
                // 站立等没有脚本的状态:引擎自己播放角色的待机动画
                SetAnimation(obj, obj.Character.MotionPath("[waiting motion]"));
            }
            if (info != null)
            {
                var vec = new IntVector(data);
                Handle(vec);
                InvokeCallback("onSetState", info, out _, obj, (long)newState, vec, resetTimer);
                InvokeCallback("onAfterSetState", info, out _, obj, (long)newState, vec, resetTimer);
                Release(vec);
            }
        }

        internal bool SetAnimation(GameObject obj, string path)
        {
            if (path == null)
            {
                return false;
            }
            AniFile clip = Data.GetAnimation(path);
            obj.Animator.Set(clip, path);
            return clip != null;
        }

        // ---------------------------------------------------------------- 技能请求
        /// <summary>最近一次技能请求的处理结果(诊断用:为什么没有进入状态)。</summary>
        public string LastSkillRequest { get; private set; } = string.Empty;

        /// <summary>玩家按下技能(热键或指令):记下请求,下一次推进时按引擎的流程检查并执行。</summary>
        public void RequestSkill(GameObject obj, int skillId)
        {
            obj.RequestedSkill = skillId;
        }

        /// <summary>
        /// 处理一次技能请求:对登记了该技能的状态,按"技能已学、当前状态允许([executable states])→ checkCommandEnable → checkExecutableSkill"的顺序问脚本。
        /// 流程是依据脚本里 sq_IsUseSkill / sq_IsCommandEnable 的用法推断的,耗蓝与冷却尚未实现。
        /// </summary>
        private void ProcessSkillRequest(GameObject obj)
        {
            int skill = obj.RequestedSkill;
            if (skill < 0 || obj.Job < 0)
            {
                return;
            }
            LastSkillRequest = "没有登记这个技能的状态";
            try
            {
                if (obj.GetSkillLevel(skill) <= 0)
                {
                    LastSkillRequest = "技能没学(等级 0)";
                    return;
                }
                foreach (StateInfo s in StatesOfJob(obj.Job, obj.Module))
                {
                    if (s.Skill != skill)
                    {
                        continue;
                    }
                    SkillDef def = Data.GetSkill(obj.Job, skill);
                    if (def != null && def.ExecutableStates.Length > 0 && Array.IndexOf(def.ExecutableStates, obj.State) < 0)
                    {
                        LastSkillRequest = "当前状态 " + obj.State + " 不在 [executable states] {" + string.Join(",", def.ExecutableStates) + "} 里";
                        continue;
                    }
                    if (Vm.HasFunction(CallbackName("checkCommandEnable", s)) && !(InvokeCallback("checkCommandEnable", s, out object ce, obj) && IsTrue(ce)))
                    {
                        LastSkillRequest = "checkCommandEnable 返回假";
                        continue;
                    }
                    if (InvokeCallback("checkExecutableSkill", s, out object ex, obj) && IsTrue(ex))
                    {
                        LastSkillRequest = "checkExecutableSkill 通过";
                        break;
                    }
                    LastSkillRequest = "checkExecutableSkill 返回假或没有这个函数";
                }
            }
            finally
            {
                obj.RequestedSkill = -1;
                DrainPackets(obj);
            }
        }

        // ---------------------------------------------------------------- 依附体
        /// <summary>
        /// 创建依附体:运行脚本(每次创建都重新运行,因为各个 ap_*.nut 都定义同名的全局函数 sq_AddFunctionName 与 sq_AddEffect,
        /// 不重新运行就会调到别的脚本定义的版本)→ 调用 sq_AddFunctionName(appendage) 登记回调 → sq_AddEffect(appendage) → onStart。
        /// </summary>
        internal Appendage CreateAppendage(GameObject parent, GameObject source, int skillIndex, string scriptRelativeToSqr)
        {
            if (!Host.RunScript(scriptRelativeToSqr, out string error))
            {
                Errors.Add("依附体脚本 " + scriptRelativeToSqr + ": " + FirstLine(error));
                return null;
            }
            var a = new Appendage
            {
                Parent = parent,
                Source = source,
                SkillIndex = skillIndex,
                ScriptPath = PvfPath.Normalize("sqr/" + scriptRelativeToSqr),
            };
            Handle(a);
            parent.Appendages.Add(a);
            CallScript("sq_AddFunctionName", out _, a);
            CallScript("sq_AddEffect", out _, a);
            CallAppendage(a, "onStart");
            a.Started = true;
            return a;
        }

        /// <summary>调用依附体登记的回调(事件名 -> 函数名);没有登记返回 false。</summary>
        internal bool CallAppendage(Appendage a, string eventName, out object result, params object[] extra)
        {
            result = null;
            if (!a.Callbacks.TryGetValue(eventName, out string function))
            {
                return false;
            }
            var args = new object[1 + extra.Length];
            args[0] = a;
            Array.Copy(extra, 0, args, 1, extra.Length);
            return CallScript(function, out result, args);
        }

        internal bool CallAppendage(Appendage a, string eventName)
        {
            return CallAppendage(a, eventName, out _);
        }

        private void AdvanceAppendages(GameObject obj, int dtMs)
        {
            if (obj.Appendages.Count == 0)
            {
                return;
            }
            foreach (Appendage a in new List<Appendage>(obj.Appendages))
            {
                if (!a.Valid)
                {
                    EndAppendage(a);
                    continue;
                }
                a.AgeMs += dtMs;
                CallAppendage(a, "proc");
                if (a.Valid && a.ValidTimeMs >= 0)
                {
                    a.ValidTimeMs -= dtMs;
                    if (a.ValidTimeMs <= 0)
                    {
                        // 有效时间到:先给脚本一个回调(事件名在原版里就拼作 onVaildTimeEnd),再结束
                        CallAppendage(a, "onVaildTimeEnd");
                        a.Valid = false;
                    }
                }
                if (a.Valid && CallAppendage(a, "isEnd", out object ended) && IsTrue(ended))
                {
                    a.Valid = false;
                }
                if (!a.Valid)
                {
                    EndAppendage(a);
                }
            }
        }

        /// <summary>结束一个依附体:onEnd → 从对象上摘下 → 从虚拟机注销。重复调用无害。</summary>
        internal void EndAppendage(Appendage a)
        {
            if (a.Parent == null || !a.Parent.Appendages.Contains(a))
            {
                return;
            }
            a.Valid = false;
            CallAppendage(a, "onEnd");
            a.Parent.Appendages.Remove(a);
            Release(a);
        }

        // ---------------------------------------------------------------- 命中判定
        /// <summary>
        /// 攻击框与受击框的重叠检测:对每个带当前攻击信息、且当前动画帧里有攻击框的对象,找与它重叠、还没命中过的敌方对象,
        /// 调用 onAttack_后缀(obj, damager, boundingBox, isStuck)。哪些对象互为敌人(这里:角色对怪物)、命中之后的伤害结算都还没有实现。
        /// </summary>
        private void ResolveHits()
        {
            var attackers = new List<GameObject>();
            foreach (GameObject o in _objects)
            {
                if (o.Valid && o.CurrentAttackInfo != null && o.Animator.CurrentFrame != null)
                {
                    attackers.Add(o);
                }
            }
            foreach (GameObject a in attackers)
            {
                AniFrame frame = a.Animator.CurrentFrame;
                if (frame == null)
                {
                    continue;
                }
                foreach (AniBox box in frame.Boxes)
                {
                    if (box.Type != AniBoxType.Attack)
                    {
                        continue;
                    }
                    Aabb attackBox = Combat.ToWorld(box, a.X, a.Y, a.Z, a.Direction);
                    foreach (GameObject t in new List<GameObject>(_objects))
                    {
                        if (!a.Valid || !t.Valid || ReferenceEquals(a, t) || !IsEnemy(a, t) || a.HitObjects.Contains(t.Id))
                        {
                            continue;
                        }
                        if (!TryGetDamageBox(t, out Aabb damageBox) || !attackBox.Overlaps(damageBox))
                        {
                            continue;
                        }
                        a.HitObjects.Add(t.Id);
                        ApplyHit(a, t, false, Overlap(attackBox, damageBox));
                        DrainPackets(a);
                    }
                }
            }
        }

        /// <summary>一次命中的处理:记录、onBeforeAttack、onAttack、onAfterAttack(顺序是依据名字的推断;伤害结算尚未实现)。</summary>
        internal void ApplyHit(GameObject attacker, GameObject target, bool forced, Aabb? region = null)
        {
            RecordHit(attacker, target, forced);
            Aabb area = region ?? (TryGetDamageBox(target, out Aabb damage) ? damage : new Aabb(target.X, target.X, target.Y, target.Y, target.Z, target.Z));
            var box = new BoundingBox(area);
            Transient(box, 2000);
            InvokeCallback("onBeforeAttack", attacker, attacker, target, box, false);
            InvokeCallback("onAttack", attacker, attacker, target, box, false);
            InvokeCallback("onAfterAttack", attacker, attacker, target, box, false);
        }

        private static Aabb Overlap(Aabb a, Aabb b)
        {
            return new Aabb(
                Math.Max(a.MinX, b.MinX), Math.Min(a.MaxX, b.MaxX),
                Math.Max(a.MinY, b.MinY), Math.Min(a.MaxY, b.MaxY),
                Math.Max(a.MinZ, b.MinZ), Math.Min(a.MaxZ, b.MaxZ));
        }

        internal void RecordHit(GameObject attacker, GameObject target, bool forced)
        {
            Hits.Add(new HitRecord
            {
                TimeMs = TimeMs,
                Attacker = attacker.Name,
                Target = target.Name,
                AttackInfo = attacker.CurrentAttackInfo == null ? null : attacker.CurrentAttackInfo.Source,
                BonusRate = attacker.CurrentAttackInfo == null ? null : attacker.CurrentAttackInfo.BonusRate,
                Forced = forced,
            });
        }

        /// <summary>两个对象是否互为敌人:这里只区分"角色一方"与"怪物一方"(被动对象跟随它的发起者)。</summary>
        internal static bool IsEnemy(GameObject a, GameObject b)
        {
            return Side(a) != Side(b) && Side(a) != 0 && Side(b) != 0;
        }

        private static int Side(GameObject o)
        {
            GameObject root = o;
            int guard = 0;
            while (root.Parent != null && guard++ < 8)
            {
                root = root.Parent;
            }
            switch (root.Kind)
            {
                case ObjectKind.Character:
                    return 1;
                case ObjectKind.Monster:
                    return 2;
                default:
                    return 0;
            }
        }

        private static bool TryGetDamageBox(GameObject t, out Aabb box)
        {
            if (t.FixedDamageBox.HasValue)
            {
                Aabb rel = t.FixedDamageBox.Value;
                box = new Aabb(t.X + rel.MinX, t.X + rel.MaxX, t.Y + rel.MinY, t.Y + rel.MaxY, t.Z + rel.MinZ, t.Z + rel.MaxZ);
                return true;
            }
            AniFrame frame = t.Animator.CurrentFrame;
            if (frame != null)
            {
                foreach (AniBox b in frame.Boxes)
                {
                    if (b.Type == AniBoxType.Damage)
                    {
                        box = Combat.ToWorld(b, t.X, t.Y, t.Z, t.Direction);
                        return true;
                    }
                }
            }
            box = default(Aabb);
            return false;
        }
    }
}
