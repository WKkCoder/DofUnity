using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Dnf.Data;
using Dnf.Script;

namespace Dnf.Game
{
    /// <summary>
    /// 被动对象(技能放出的飞行物、特效体)与随消息发送的二进制数据。
    /// 被动对象由 sq_SendCreatePassiveObjectPacket 创建:编号经 passiveobject.lst 找到 .obj(基础动画、攻击信息、自定义表),
    /// 脚本回调名是 &lt;回调&gt;_po_&lt;名字&gt;(名字取自 .obj 文件名,不区分大小写地对应到脚本里的写法,证据 E-036)。
    /// 原版里创建、销毁与切换状态是网络消息,这里的假设:消息在发出它的回调结束后、同一帧里处理(没有延迟)。
    /// </summary>
    public sealed partial class GameWorld
    {
        // ---------------------------------------------------------------- 二进制发送缓冲
        private readonly List<byte> _writeBuffer = new List<byte>();

        /// <summary>最近一次交给脚本的读取端(sq_BinaryGetReadSize 读它的位置)。</summary>
        internal PacketReader LastReader { get; private set; }

        /// <summary>清空发送缓冲,开始写新的一段数据(sq_BinaryStartWrite / obj.sq_StartWrite)。</summary>
        internal void StartWrite()
        {
            _writeBuffer.Clear();
        }

        /// <summary>往发送缓冲末尾写 width 字节(小端)。</summary>
        internal void WriteInt(long value, int width)
        {
            for (int i = 0; i < width; i++)
            {
                _writeBuffer.Add((byte)(value >> (8 * i)));
            }
        }

        internal void WriteFloat(float value)
        {
            _writeBuffer.AddRange(BitConverter.GetBytes(value));
        }

        /// <summary>取走缓冲里写好的数据(发送消息时调用;之后缓冲为空,没写数据的消息收到的是空数据)。</summary>
        internal byte[] TakeWritten()
        {
            byte[] data = _writeBuffer.ToArray();
            _writeBuffer.Clear();
            return data;
        }

        /// <summary>用一份数据创建读取端并交给虚拟机;回调结束后调用 <see cref="ReleaseReader"/>。</summary>
        internal PacketReader NewReader(byte[] data)
        {
            var reader = new PacketReader(data);
            Handle(reader);
            LastReader = reader;
            return reader;
        }

        internal void ReleaseReader(PacketReader reader)
        {
            Release(reader);
        }

        // ---------------------------------------------------------------- 世界级消息队列
        private readonly Queue<Action> _worldPackets = new Queue<Action>();

        /// <summary>登记一条要在当前回调结束后处理的世界级消息(创建与销毁被动对象、改变技能特效)。</summary>
        internal void QueueWorldPacket(Action action)
        {
            _worldPackets.Enqueue(action);
        }

        internal void DrainWorldPackets()
        {
            int guard = 0;
            while (_worldPackets.Count > 0 && guard++ < 4096)
            {
                _worldPackets.Dequeue()();
            }
            if (_worldPackets.Count > 0)
            {
                Errors.Add("世界消息队列超过 4096 条,已丢弃");
                _worldPackets.Clear();
            }
        }

        // ---------------------------------------------------------------- 被动对象的脚本名字
        // 小写名字 -> 脚本里 po_ 后面的写法
        private readonly Dictionary<string, string> _passiveNames = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Regex PassiveFunction = new Regex(@"function\s+[A-Za-z0-9]+?_po_([A-Za-z0-9_]+)\s*\(", RegexOptions.Compiled);

        /// <summary>扫描已加载的脚本,找出所有 xxx_po_名字 回调的名字写法(函数名区分大小写,.obj 文件名是小写的)。</summary>
        private void IndexPassiveScripts()
        {
            _passiveNames.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScriptRegistration r in Host.Registrations)
            {
                if (!seen.Add(r.ResolvedPath) || !Data.TryReadBytes(r.ResolvedPath, out byte[] bytes))
                {
                    continue;
                }
                string text = Encoding.ASCII.GetString(bytes);
                foreach (Match m in PassiveFunction.Matches(text))
                {
                    string name = m.Groups[1].Value;
                    string lower = name.ToLowerInvariant();
                    if (!_passiveNames.ContainsKey(lower))
                    {
                        _passiveNames.Add(lower, name);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- 创建与销毁
        /// <summary>
        /// 创建被动对象。位置:相对创建者 (offsetX 向前、offsetY 纵深、offsetZ 高度);
        /// 朝向参数为 NEUTRAL 时偏移不随创建者朝向镜像、新对象朝向取创建者的;为左或右时新对象朝该方向、偏移在朝左时镜像;
        /// absolute 为真时偏移就是世界坐标。创建后调用 setCustomData_po_名字(对象, 读取端)。找不到 .obj 返回 null 并记入 <see cref="Errors"/>。
        /// </summary>
        internal GameObject CreatePassiveObject(GameObject creator, int id, int offsetX, int offsetY, int offsetZ, int direction, bool absolute, byte[] data)
        {
            PassiveObjectDef def = Data.GetPassiveObject(id);
            if (def == null)
            {
                Errors.Add("被动对象 " + id + " 找不到 .obj");
                return null;
            }
            int facing = direction == Direction.Left || direction == Direction.Right ? direction : creator.Direction;
            bool mirror = !absolute && direction != Direction.Neutral && facing == Direction.Left;
            int x = absolute ? offsetX : creator.X + (mirror ? -offsetX : offsetX);
            int y = absolute ? offsetY : creator.Y + offsetY;
            int z = absolute ? offsetZ : creator.Z + offsetZ;

            GameObject obj = Spawn(ObjectKind.PassiveObject, def.DisplayName ?? def.BaseName, x, y, z, facing);
            obj.Def = def;
            obj.PassiveId = id;
            obj.Parent = creator;
            obj.Source = creator;
            obj.IsMyControl = creator.IsMyControl;
            obj.LifeLimitMs = def.TimeLimitMs;
            if (_passiveNames.TryGetValue(def.BaseName.ToLowerInvariant(), out string scriptName))
            {
                obj.StateInfo = new StateInfo("PassiveObject", -1, 0, -1, "po_" + scriptName, null);
            }
            SetAnimation(obj, def.BasicMotionPath);
            string attack = def.AttackInfoPath;
            if (attack != null)
            {
                SetCurrentAttackInfo(obj, attack, "obj:" + id);
            }
            if (obj.StateInfo != null)
            {
                PacketReader reader = NewReader(data);
                InvokeCallback("setCustomData", obj.StateInfo, out _, obj, reader);
                ReleaseReader(reader);
                DrainPackets(obj);
            }
            return obj;
        }

        /// <summary>销毁被动对象:先调 destroy_po_名字 与 onDestroyObject_po_名字(对象, 对象自己),再按普通对象销毁。</summary>
        internal void DestroyPassiveObject(GameObject obj)
        {
            Destroy(obj);
        }

        /// <summary>创建者名下、编号为 id 的第 index 个还有效的被动对象(按创建顺序);没有返回 null。</summary>
        internal GameObject FindPassiveObject(GameObject creator, int id, int index)
        {
            int n = 0;
            foreach (GameObject o in _objects)
            {
                if (o.Valid && o.Kind == ObjectKind.PassiveObject && o.PassiveId == id && ReferenceEquals(o.Parent, creator))
                {
                    if (n++ == index)
                    {
                        return o;
                    }
                }
            }
            return null;
        }

        /// <summary>沿父对象向上找到最顶层的角色(getTopCharacter);没有角色祖先返回 null。</summary>
        internal static GameObject TopCharacter(GameObject o)
        {
            GameObject cur = o;
            int guard = 0;
            while (cur != null && guard++ < 16)
            {
                if (cur.Kind == ObjectKind.Character)
                {
                    return cur;
                }
                cur = cur.Parent;
            }
            return null;
        }

        /// <summary>
        /// sq_SendChangeSkillEffectPacket:告诉角色某个技能的特效要变(依据名字推断):
        /// 调用登记了该技能的状态的 onChangeSkillEffect_后缀(角色, 技能, 数据),并通知角色名下带这个回调的被动对象。
        /// </summary>
        internal void ChangeSkillEffect(GameObject chr, int skill, byte[] data)
        {
            PacketReader reader = NewReader(data);
            try
            {
                foreach (StateInfo s in StatesOfJob(chr.Job, chr.Module))
                {
                    if (s.Skill == skill)
                    {
                        InvokeCallback("onChangeSkillEffect", s, out _, chr, (long)skill, reader);
                    }
                }
                foreach (GameObject o in new List<GameObject>(_objects))
                {
                    if (o.Valid && o.Kind == ObjectKind.PassiveObject && ReferenceEquals(o.Parent, chr) && o.StateInfo != null)
                    {
                        InvokeCallback("onChangeSkillEffect", o.StateInfo, out _, o, (long)skill, reader);
                    }
                }
            }
            finally
            {
                ReleaseReader(reader);
            }
        }
    }
}
