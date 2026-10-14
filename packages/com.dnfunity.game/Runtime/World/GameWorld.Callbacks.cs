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
                Errors.Add(function + ": " + FirstLine(error));
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
            return CallScript(prefix + "_" + info.Suffix, out result, args);
        }

        // ---------------------------------------------------------------- 状态切换
        /// <summary>sq_AddSetStatePacket 的落地:把"切换状态"放进对象的队列,在本次回调结束后由 <see cref="DrainPackets"/> 处理。</summary>
        internal void QueueState(GameObject obj, int state, int priority, bool resetTimer)
        {
            obj.Packets.Enqueue(new StatePacket
            {
                State = state,
                Priority = priority,
                ResetTimer = resetTimer,
                Data = obj.DataVector.Values.ToArray(),
            });
        }

        internal void DrainPackets(GameObject obj)
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
                obj.Timer.Reset();
            }
            obj.State = newState;
            StateInfo info = obj.Job >= 0 ? FindState(obj.Job, newState) : null;
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
            try
            {
                if (obj.GetSkillLevel(skill) <= 0)
                {
                    return;
                }
                foreach (StateInfo s in StatesOfJob(obj.Job))
                {
                    if (s.Skill != skill)
                    {
                        continue;
                    }
                    SkillDef def = Data.GetSkill(obj.Job, skill);
                    if (def != null && def.ExecutableStates.Length > 0 && Array.IndexOf(def.ExecutableStates, obj.State) < 0)
                    {
                        continue;
                    }
                    if (Vm.HasFunction("checkCommandEnable_" + s.Suffix) && !(InvokeCallback("checkCommandEnable", s, out object ce, obj) && IsTrue(ce)))
                    {
                        continue;
                    }
                    if (InvokeCallback("checkExecutableSkill", s, out object ex, obj) && IsTrue(ex))
                    {
                        break;
                    }
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
                        RecordHit(a, t, false);
                        InvokeCallback("onAttack", a, a, t, null, false);
                        DrainPackets(a);
                    }
                }
            }
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
