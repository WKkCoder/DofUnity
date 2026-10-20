using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Play
{
    /// <summary>声音通道:音量分组与同时放几个的上限由放声音的一方按通道决定。</summary>
    public enum SoundChannel
    {
        /// <summary>背景音乐(同一时刻只有一首)。</summary>
        Music,

        /// <summary>房间的环境声与周期性的音效(地图 [sound] 里除音乐以外的项)。</summary>
        Room,

        /// <summary>一般音效:挥剑、命中、跳跃、技能。</summary>
        Effect,

        /// <summary>角色语音;同一个主人(<see cref="SoundCue.Owner"/>)的语音不叠放。</summary>
        Voice,

        /// <summary>界面音。</summary>
        Ui,
    }

    public enum SoundCommandKind
    {
        Play,
        Stop,
    }

    /// <summary>调度器给放声音的一方的命令:开始放某个文件,或停掉一个之前开始的声音(用句柄对应)。</summary>
    public readonly struct SoundCommand
    {
        public SoundCommandKind Kind { get; }

        /// <summary>每次开始放声音分配的编号;<see cref="SoundCommandKind.Stop"/> 用它指明停哪一个。</summary>
        public int Handle { get; }

        public SoundChannel Channel { get; }

        /// <summary>audio.xml 里叶子条目的 ID(<see cref="SoundCommandKind.Stop"/> 时为 null)。</summary>
        public string Id { get; }

        /// <summary>audio.xml 里的文件(已把反斜杠换成正斜杠;<see cref="SoundCommandKind.Stop"/> 时为 null)。</summary>
        public string File { get; }

        /// <summary>连续循环(播完立刻接着播)。有间隔的循环不是这个:那种由调度器在每次播完后重新安排。</summary>
        public bool Loop { get; }

        public SoundCommand(SoundCommandKind kind, int handle, SoundChannel channel, string id, string file, bool loop)
        {
            Kind = kind;
            Handle = handle;
            Channel = channel;
            Id = id;
            File = file;
            Loop = loop;
        }

        public override string ToString()
        {
            return Kind == SoundCommandKind.Play
                ? "Play#" + Handle + " " + Channel + " " + Id + (Loop ? " loop" : string.Empty)
                : "Stop#" + Handle + " " + Channel;
        }
    }

    /// <summary>
    /// 把声音提示(<see cref="SoundCue"/>)变成放声音的命令(证据 E-089、E-090)。不碰任何音频接口,只管"什么时候放哪个文件、什么时候停",所以可以不带声卡测试:
    ///   · 音效与界面音:按 audio.xml 展开(RANDOM 按权重抽、GROUP 逐项带延迟),同一个标签 <see cref="CoalesceMs"/> 毫秒内只放一次;
    ///   · 语音:同一个主人的新语音打断它上一个还没放完的;
    ///   · 房间声音:<see cref="SoundCueKind.RoomEnter"/> 之后的 <see cref="SoundCueKind.RoomSound"/> 是这个房间的全部地图声音——音乐只在换了一首时才换,
    ///     环境声与周期音效跟着房间走(两个房间都有的不重放、只有上一个房间有的停掉);没有 [sound] 音乐的房间沿用当前的音乐(推断);
    ///   · 循环:LOOP_DELAY=0 连续循环;大于 0 的周期音效在每次放完(放声音的一方调 <see cref="NotifyFinished"/>)之后隔这么久再抽一次放;没写的只放一次。
    /// 循环间隔与延迟的含义、"换房间沿用相同标签"都是推断(没有录音,客户端的调度代码没读),见证据 E-089、E-090。
    /// </summary>
    public sealed class SoundScheduler
    {
        /// <summary>同一个标签的音效在这么多毫秒内只放一次(同一步里两只怪物被同一击打中,不放两遍叠加)。</summary>
        public const double CoalesceMs = 30.0;

        private const int MaxDelayed = 256;

        private sealed class RoomSoundState
        {
            public string Id;
            public int Handle;
            public bool Periodic;
            public double NextDueMs;
        }

        private struct Delayed
        {
            public double DueMs;
            public int Handle;
            public SoundChannel Channel;
            public string Id;
            public string File;
            public bool Loop;
            public int VoiceOwner;
        }

        private readonly AudioCatalog _catalog;
        private readonly Func<double> _random;
        private readonly List<Delayed> _delayed = new List<Delayed>();
        private readonly Dictionary<int, int> _voiceByOwner = new Dictionary<int, int>();
        private readonly Dictionary<string, double> _lastPlayMs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly List<RoomSoundState> _room = new List<RoomSoundState>();
        private readonly List<string> _collected = new List<string>();
        private readonly Dictionary<int, SoundChannel> _active = new Dictionary<int, SoundChannel>();
        private bool _collecting;
        private double _nowMs;
        private int _nextHandle = 1;
        private int _musicHandle;

        /// <summary>现在在放的背景音乐的标签;没有为 null。</summary>
        public string MusicId { get; private set; }

        /// <summary>调度器自己的时钟(累计推进的毫秒)。</summary>
        public double NowMs => _nowMs;

        /// <summary>已经发出 Play 命令、还没停也没被通知放完的声音数。</summary>
        public int ActiveCount => _active.Count;

        /// <summary>地图声音里现在跟着房间走的项数(不含音乐)。</summary>
        public int RoomSoundCount => _room.Count;

        public long PlayCount { get; private set; }
        public long StopCount { get; private set; }

        /// <summary>提示的标签在 audio.xml 里找不到(或展开后没有文件)的次数。</summary>
        public long UnknownCount { get; private set; }

        /// <summary>因为同一标签刚放过而略过的次数。</summary>
        public long CoalescedCount { get; private set; }

        public SoundScheduler(AudioCatalog catalog, Func<double> random = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _random = random ?? new Random(1).NextDouble;
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒并处理这一批提示,产生的命令追加到 <paramref name="output"/>。
        /// 顺序:先按提示的先后处理,再收尾这一批里的房间声音,再放到点的延迟声音,最后放到点的周期音效。
        /// </summary>
        public void Update(double deltaMs, IReadOnlyList<SoundCue> cues, List<SoundCommand> output)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            if (deltaMs > 0)
            {
                _nowMs += deltaMs;
            }
            if (cues != null)
            {
                for (int i = 0; i < cues.Count; i++)
                {
                    Handle(cues[i], output);
                }
            }
            if (_collecting)
            {
                ApplyRoomSet(output);
                _collecting = false;
            }
            FlushDelayed(output);
            TickPeriodic(output);
        }

        /// <summary>放声音的一方告诉调度器:这个句柄的声音(没有连续循环的)放完了。周期音效据此安排下一次。</summary>
        public void NotifyFinished(int handle)
        {
            if (!_active.Remove(handle))
            {
                return;
            }
            if (handle == _musicHandle)
            {
                _musicHandle = 0;
            }
            foreach (KeyValuePair<int, int> kv in _voiceByOwner)
            {
                if (kv.Value == handle)
                {
                    _voiceByOwner.Remove(kv.Key);
                    break;
                }
            }
            foreach (RoomSoundState s in _room)
            {
                if (s.Handle == handle)
                {
                    s.Handle = 0;
                    if (s.Periodic)
                    {
                        s.NextDueMs = _nowMs + NextGapMs(s.Id);
                    }
                    break;
                }
            }
        }

        /// <summary>停掉所有在放的声音与还没开始的延迟声音(比如离开副本)。房间声音的记录也清掉。</summary>
        public void StopAll(List<SoundCommand> output)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            var notStarted = new HashSet<int>();
            foreach (Delayed d in _delayed)
            {
                notStarted.Add(d.Handle);                // 还在排队的没开始过,不用发 Stop
            }
            _delayed.Clear();
            var handles = new List<KeyValuePair<int, SoundChannel>>(_active);
            foreach (KeyValuePair<int, SoundChannel> kv in handles)
            {
                if (notStarted.Contains(kv.Key))
                {
                    _active.Remove(kv.Key);
                    continue;
                }
                Stop(kv.Key, kv.Value, output);
            }
            _voiceByOwner.Clear();
            _room.Clear();
            _collected.Clear();
            _collecting = false;
            MusicId = null;
            _musicHandle = 0;
        }

        // ------------------------------------------------------------------ 提示

        private void Handle(SoundCue cue, List<SoundCommand> output)
        {
            switch (cue.Kind)
            {
                case SoundCueKind.RoomEnter:
                    _collecting = true;
                    _collected.Clear();
                    break;
                case SoundCueKind.RoomSound:
                    if (_collecting)
                    {
                        _collected.Add(cue.Key);
                    }
                    else
                    {
                        AddRoomSounds(new[] { cue.Key }, output);
                    }
                    break;
                case SoundCueKind.Voice:
                    PlayVoice(cue, output);
                    break;
                case SoundCueKind.Ui:
                    PlayOneShot(cue.Key, SoundChannel.Ui, output);
                    break;
                default:
                    PlayOneShot(cue.Key, SoundChannel.Effect, output);
                    break;
            }
        }

        private void PlayOneShot(string key, SoundChannel channel, List<SoundCommand> output)
        {
            if (_lastPlayMs.TryGetValue(key, out double last) && _nowMs - last < CoalesceMs)
            {
                CoalescedCount++;
                return;
            }
            List<ResolvedSound> sounds = _catalog.Resolve(key, _random);
            if (sounds.Count == 0)
            {
                UnknownCount++;
                return;
            }
            _lastPlayMs[key] = _nowMs;
            foreach (ResolvedSound r in sounds)
            {
                Start(r, channel, false, 0, output);
            }
        }

        private void PlayVoice(SoundCue cue, List<SoundCommand> output)
        {
            List<ResolvedSound> sounds = _catalog.Resolve(cue.Key, _random);
            if (sounds.Count == 0)
            {
                UnknownCount++;
                return;
            }
            if (_voiceByOwner.TryGetValue(cue.Owner, out int previous))
            {
                _voiceByOwner.Remove(cue.Owner);
                Stop(previous, SoundChannel.Voice, output);
            }
            bool first = true;
            foreach (ResolvedSound r in sounds)
            {
                Start(r, SoundChannel.Voice, false, first ? cue.Owner : -1, output);
                first = false;
            }
        }

        // ------------------------------------------------------------------ 开始与停止

        /// <summary>开始放一个展开后的声音:有延迟的先排队。<paramref name="voiceOwner"/> 大于等于 0 时记下这是那个主人当前的语音。返回句柄。</summary>
        private int Start(ResolvedSound r, SoundChannel channel, bool loop, int voiceOwner, List<SoundCommand> output)
        {
            int handle = _nextHandle++;
            string file = r.File;
            if (r.DelaySeconds > 0.0005)
            {
                if (_delayed.Count >= MaxDelayed)
                {
                    _delayed.RemoveAt(0);
                }
                _delayed.Add(new Delayed { DueMs = _nowMs + r.DelaySeconds * 1000.0, Handle = handle, Channel = channel, Id = r.Id, File = file, Loop = loop, VoiceOwner = voiceOwner });
                _active[handle] = channel;
                if (voiceOwner >= 0)
                {
                    _voiceByOwner[voiceOwner] = handle;
                }
                return handle;
            }
            Emit(handle, channel, r.Id, file, loop, voiceOwner, output);
            return handle;
        }

        private void Emit(int handle, SoundChannel channel, string id, string file, bool loop, int voiceOwner, List<SoundCommand> output)
        {
            _active[handle] = channel;
            if (voiceOwner >= 0)
            {
                _voiceByOwner[voiceOwner] = handle;
            }
            PlayCount++;
            output.Add(new SoundCommand(SoundCommandKind.Play, handle, channel, id, file, loop));
        }

        private void Stop(int handle, SoundChannel channel, List<SoundCommand> output)
        {
            if (handle == 0)
            {
                return;
            }
            bool wasActive = _active.Remove(handle);
            // 还在延迟队列里没开始的:直接取消,不发 Stop
            for (int i = 0; i < _delayed.Count; i++)
            {
                if (_delayed[i].Handle == handle)
                {
                    _delayed.RemoveAt(i);
                    return;
                }
            }
            if (!wasActive)
            {
                return;                                  // 已经放完了
            }
            StopCount++;
            output.Add(new SoundCommand(SoundCommandKind.Stop, handle, channel, null, null, false));
        }

        private void FlushDelayed(List<SoundCommand> output)
        {
            if (_delayed.Count == 0)
            {
                return;
            }
            // 按到点的先后放;一次只会有几个,线性扫描就够
            while (true)
            {
                int best = -1;
                for (int i = 0; i < _delayed.Count; i++)
                {
                    if (_delayed[i].DueMs <= _nowMs && (best < 0 || _delayed[i].DueMs < _delayed[best].DueMs))
                    {
                        best = i;
                    }
                }
                if (best < 0)
                {
                    return;
                }
                Delayed d = _delayed[best];
                _delayed.RemoveAt(best);
                Emit(d.Handle, d.Channel, d.Id, d.File, d.Loop, d.VoiceOwner, output);
            }
        }

        // ------------------------------------------------------------------ 房间声音

        private void ApplyRoomSet(List<SoundCommand> output)
        {
            // 音乐:第一个 MUSIC 标签;和现在放的是同一首就不动,房间里没有音乐就沿用当前的
            string wantedMusic = null;
            foreach (string id in _collected)
            {
                if (_catalog.TryGet(id, out AudioEntry e) && e.Kind == AudioKind.Music)
                {
                    wantedMusic = e.Id;
                    break;
                }
            }
            if (wantedMusic != null && !string.Equals(wantedMusic, MusicId, StringComparison.OrdinalIgnoreCase))
            {
                if (_musicHandle != 0)
                {
                    Stop(_musicHandle, SoundChannel.Music, output);
                    _musicHandle = 0;
                }
                MusicId = wantedMusic;
                StartMusic(wantedMusic, output);
            }

            // 其余:跟着房间走;两边都有的保持,只有旧房间有的停掉,只有新房间有的开始
            var wanted = new List<string>();
            foreach (string id in _collected)
            {
                if (_catalog.TryGet(id, out AudioEntry e) && e.Kind != AudioKind.Music)
                {
                    if (!wanted.Exists(w => string.Equals(w, e.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        wanted.Add(e.Id);
                    }
                }
                else if (!_catalog.Contains(id))
                {
                    UnknownCount++;
                }
            }
            for (int i = _room.Count - 1; i >= 0; i--)
            {
                RoomSoundState s = _room[i];
                if (!wanted.Exists(w => string.Equals(w, s.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Stop(s.Handle, SoundChannel.Room, output);
                    _room.RemoveAt(i);
                }
            }
            AddRoomSounds(wanted, output);
        }

        private void AddRoomSounds(IEnumerable<string> ids, List<SoundCommand> output)
        {
            foreach (string id in ids)
            {
                if (!_catalog.TryGet(id, out AudioEntry entry))
                {
                    UnknownCount++;
                    continue;
                }
                if (entry.Kind == AudioKind.Music)
                {
                    if (!string.Equals(entry.Id, MusicId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_musicHandle != 0)
                        {
                            Stop(_musicHandle, SoundChannel.Music, output);
                            _musicHandle = 0;
                        }
                        MusicId = entry.Id;
                        StartMusic(entry.Id, output);
                    }
                    continue;
                }
                if (_room.Exists(s => string.Equals(s.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                StartRoomSound(entry.Id, output);
            }
        }

        private void StartMusic(string id, List<SoundCommand> output)
        {
            List<ResolvedSound> sounds = _catalog.Resolve(id, _random);
            if (sounds.Count == 0)
            {
                UnknownCount++;
                return;
            }
            ResolvedSound r = sounds[0];
            // 音乐按 LOOP_DELAY=0 连续循环;没写 LOOP_DELAY 的只放一遍
            _musicHandle = Start(r, SoundChannel.Music, r.Loops && r.LoopGapSeconds <= 0, -1, output);
        }

        private void StartRoomSound(string id, List<SoundCommand> output)
        {
            List<ResolvedSound> sounds = _catalog.Resolve(id, _random);
            if (sounds.Count == 0)
            {
                UnknownCount++;
                return;
            }
            ResolvedSound r = sounds[0];
            var state = new RoomSoundState { Id = id };
            _room.Add(state);
            if (r.Loops && r.LoopGapSeconds > 0)
            {
                state.Periodic = true;                                    // 第一次隔一个间隔之后再放,免得一进房间所有周期音效同时响
                state.NextDueMs = _nowMs + r.LoopGapSeconds * 1000.0;
            }
            else
            {
                state.Handle = Start(r, SoundChannel.Room, r.Loops, -1, output);   // 连续循环,或只放一遍
            }
            for (int i = 1; i < sounds.Count; i++)
            {
                Start(sounds[i], SoundChannel.Room, false, -1, output);   // GROUP 里的其余各项当一次性的放
            }
        }

        private void TickPeriodic(List<SoundCommand> output)
        {
            foreach (RoomSoundState s in _room)
            {
                if (!s.Periodic || s.Handle != 0 || s.NextDueMs > _nowMs)
                {
                    continue;
                }
                List<ResolvedSound> sounds = _catalog.Resolve(s.Id, _random);
                if (sounds.Count == 0)
                {
                    s.NextDueMs = double.MaxValue;
                    continue;
                }
                s.Handle = Start(sounds[0], SoundChannel.Room, false, -1, output);
                s.NextDueMs = double.MaxValue;                           // 放完(NotifyFinished)再安排下一次
                for (int i = 1; i < sounds.Count; i++)
                {
                    Start(sounds[i], SoundChannel.Room, false, -1, output);
                }
            }
        }

        /// <summary>周期音效下一次的间隔:重新展开一次,让随机的那部分重新掷;展开不出来就用一个保守的 30 秒。</summary>
        private double NextGapMs(string id)
        {
            List<ResolvedSound> sounds = _catalog.Resolve(id, _random);
            return sounds.Count > 0 && sounds[0].Loops && sounds[0].LoopGapSeconds > 0 ? sounds[0].LoopGapSeconds * 1000.0 : 30000.0;
        }
    }
}
