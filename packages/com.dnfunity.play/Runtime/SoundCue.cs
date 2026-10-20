using System;
using System.Collections.Generic;

namespace Dnf.Play
{
    /// <summary>声音提示的种类:决定放声音的一方怎么处理它(音量、同时放几个、要不要循环)。证据 E-090。</summary>
    public enum SoundCueKind
    {
        /// <summary>普通音效(挥剑、命中、跳跃、技能)。</summary>
        Effect,

        /// <summary>角色的语音(主角喊招、受击、死亡;怪物的叫声):同一个角色的语音不必叠在一起放。</summary>
        Voice,

        /// <summary>界面音(冷却完成)。</summary>
        Ui,

        /// <summary>
        /// 地图 [sound] 里的一项:背景音乐、环境声或偶尔响的音效,具体是哪一种要查 audio.xml(MUSIC 换背景音乐、AMBIENT 循环、其余按它自己的循环设置)。
        /// 换房间时同一个标签不要重放。
        /// </summary>
        RoomSound,

        /// <summary>
        /// 进了一个房间(地图路径作为标签):紧跟着的 <see cref="RoomSound"/> 是这个房间的全部地图声音。
        /// 放声音的一方据此把上一个房间的环境声停掉、只留两个房间都有的(同一个标签不重放)。
        /// </summary>
        RoomEnter,
    }

    /// <summary>
    /// 一次"该放这个声音了"的提示:声音标签(audio.xml 的 ID)加种类。时间点就是发出这个提示的那一小步。
    /// <see cref="Owner"/> 是发声的对象:0 是主角(或没有主人的声音),怪物是它在房间里的序号加 1;同一个主人的语音不叠在一起放。
    /// </summary>
    public readonly struct SoundCue
    {
        public string Key { get; }
        public SoundCueKind Kind { get; }
        public int Owner { get; }

        public SoundCue(string key, SoundCueKind kind, int owner = 0)
        {
            Key = key;
            Kind = kind;
            Owner = owner;
        }

        public override string ToString()
        {
            return Kind + ":" + Key;
        }
    }

    /// <summary>
    /// 声音提示的队列:游戏逻辑往里放,播放的一方(Unity 视图、命令行检查)每帧取走。没人取时只留最近的 <see cref="MaxPending"/> 条。
    /// 空标签被忽略。<see cref="TotalCount"/> 记录发出过的总数(含被挤掉的)。
    /// </summary>
    public sealed class SoundCueQueue
    {
        public const int MaxPending = 512;

        private readonly List<SoundCue> _pending = new List<SoundCue>();

        public long TotalCount { get; private set; }

        public int PendingCount => _pending.Count;

        /// <summary>等着被取走的提示(只读视图,测试里用)。</summary>
        public IReadOnlyList<SoundCue> Pending => _pending;

        public void Add(string key, SoundCueKind kind, int owner = 0)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            TotalCount++;
            if (_pending.Count >= MaxPending)
            {
                _pending.RemoveAt(0);
            }
            _pending.Add(new SoundCue(key, kind, owner));
        }

        /// <summary>把等着的提示全部移进 <paramref name="into"/>(追加在后面),队列清空。</summary>
        public void Drain(List<SoundCue> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            into.AddRange(_pending);
            _pending.Clear();
        }
    }

    /// <summary>能发出声音提示的游戏会话(房间战斗、整座副本):播放的一方每帧调 <see cref="DrainSounds"/> 取走新的提示。</summary>
    public interface ISoundSource
    {
        /// <summary>把上次取走之后新发出的提示追加到 <paramref name="into"/> 后面,并清空会话里的队列。</summary>
        void DrainSounds(List<SoundCue> into);
    }

    /// <summary>
    /// 怪物的五种声音标签(取自 .mob,证据 E-089):出场、受击、死亡、出招、环境。没写的为 null。
    /// 环境声(偶尔放一次的叫声)的节奏没有读,目前不放。
    /// </summary>
    public readonly struct MonsterSounds
    {
        public string Appear { get; }
        public string Damage { get; }
        public string Die { get; }
        public string Attack { get; }
        public string Ambient { get; }

        public MonsterSounds(string appear, string damage, string die, string attack, string ambient = null)
        {
            Appear = Clean(appear);
            Damage = Clean(damage);
            Die = Clean(die);
            Attack = Clean(attack);
            Ambient = Clean(ambient);
        }

        private static string Clean(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        public bool IsEmpty => Appear == null && Damage == null && Die == null && Attack == null && Ambient == null;
    }
}
