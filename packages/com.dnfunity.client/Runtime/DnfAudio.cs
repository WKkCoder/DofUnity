using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dnf.Play;
using Dnf.Render;
using UnityEngine;
using UnityEngine.Networking;

namespace Dnf.Client
{
    /// <summary>
    /// 声音播放(M8,证据 E-089、E-090):把游戏会话发出的声音提示(<see cref="SoundCue"/>)交给 <see cref="SoundScheduler"/> 排成命令,
    /// 再用 Unity 的 <see cref="AudioSource"/> 放出来。声音文件是用户自备的 Ogg(SoundPacks 里的条目、Music 目录里散装的文件)。
    /// Unity 只能从文件解码 Ogg,所以包里的条目先写到临时目录、用 UnityWebRequestMultimedia 读成 AudioClip(读完删掉临时文件,
    /// 剪辑以压缩形式留在内存里,播放时再解码);Music 目录里散装的文件直接从原路径读。读文件在协程里做:第一次用到某个声音时要晚几帧才响,
    /// 晚了太久的音效(默认 <see cref="LateLimitSeconds"/> 秒)直接放弃,免得和画面对不上;音乐与房间环境声不放弃。
    /// 不碰画面与游戏逻辑:放不出声(没有声卡、文件坏了)只会让计数器里的 failed 增加,游戏照常运行。
    /// 混音比例(<see cref="ChannelVolume"/>)与同时放的个数上限(<see cref="ChannelCapacity"/>)是占位值,原版的混音表没有读。
    /// 带 -batchmode 的无人值守运行默认把总音量压成 0(整条链路照跑、计数器照记,只是不出声),带 -dnfsound 才出声;-dnfnosound 完全不开声音。
    /// </summary>
    public sealed class DnfAudio : MonoBehaviour
    {
        /// <summary>音效、语音、界面音:读文件超过这么久才响的就不放了。</summary>
        public const float LateLimitSeconds = 0.35f;

        /// <summary>同时存在的 AudioSource 上限(包括正在淡出的音乐)。</summary>
        public const int MaxSources = 48;

        /// <summary>内存里最多留这么多个剪辑;超出时丢掉最久没用的(正在放的不丢)。</summary>
        public const int MaxCachedClips = 400;

        /// <summary>换歌时旧音乐淡出的秒数。</summary>
        public const float MusicFadeSeconds = 0.6f;

        private sealed class Playback
        {
            public int Handle;
            public SoundChannel Channel;
            public string File;
            public bool Loop;
            public AudioSource Source;      // 剪辑还没读完时为 null
            public float RequestedAt;
            public int StartedFrame;
        }

        private sealed class ClipSlot
        {
            public string File;
            public AudioClip Clip;
            public bool Loading;
            public bool Failed;
            public float LastUse;
        }

        private struct Fade
        {
            public AudioSource Source;
            public float Left;
            public float StartVolume;
        }

        private readonly List<SoundCue> _cues = new List<SoundCue>();
        private readonly List<SoundCommand> _commands = new List<SoundCommand>();
        private readonly Dictionary<int, Playback> _playbacks = new Dictionary<int, Playback>();
        private readonly Dictionary<string, ClipSlot> _clips = new Dictionary<string, ClipSlot>(StringComparer.Ordinal);
        private readonly Stack<AudioSource> _free = new Stack<AudioSource>();
        private readonly List<AudioSource> _all = new List<AudioSource>();
        private readonly List<Fade> _fades = new List<Fade>();
        private readonly List<Playback> _scratch = new List<Playback>();
        private SoundLibrary _library;
        private SoundScheduler _scheduler;
        private string _tempDir;
        private bool _audible;
        private int _failureLogs;

        /// <summary>收到的声音提示总数。</summary>
        public long CueCount { get; private set; }

        /// <summary>真正在 AudioSource 上开始放的次数。</summary>
        public long StartedCount { get; private set; }

        /// <summary>剪辑读成功的次数 / 读失败的文件数。</summary>
        public long LoadedCount { get; private set; }

        public long FailedCount { get; private set; }

        /// <summary>因为剪辑读得太晚而放弃的次数。</summary>
        public long LateCount { get; private set; }

        /// <summary>因为同一通道放满了、顶掉最老的那个的次数。</summary>
        public long StolenCount { get; private set; }

        /// <summary>同一时刻最多有多少个声音在途(已开始放或在等剪辑)。</summary>
        public int MaxInFlight { get; private set; }

        /// <summary>背景音乐开始放的次数(调度器发出的音乐通道 Play 命令;每次开始在日志里留一行 DNF_MUSIC,E-097)。</summary>
        public int MusicStarts { get; private set; }

        /// <summary>结算界面出现时放的 M_RESULT(界面通道)次数(每次在日志里留一行 DNF_RESULT_SOUND,E-097)。</summary>
        public int ResultSounds { get; private set; }

        public SoundScheduler Scheduler => _scheduler;

        public SoundLibrary Library => _library;

        public bool Audible => _audible;

        public int InFlight => _playbacks.Count;

        public int CachedClips => _clips.Count;

        /// <summary>
        /// 打开数据目录里的声音数据并建好播放器。没有 audio.xml 时返回 null(没有声音数据,游戏照常跑)。
        /// <paramref name="audible"/> 为 false 时整条链路照跑但总音量是 0。调用方用 <see cref="UnityObjects.Destroy"/> 销毁返回对象的 gameObject。
        /// </summary>
        public static DnfAudio Open(string dnfDir, bool audible)
        {
            SoundLibrary library = SoundLibrary.Open(dnfDir);
            if (!library.HasCatalog)
            {
                library.Dispose();
                return null;
            }
            var go = new GameObject("DnfAudio") { hideFlags = HideFlags.HideAndDontSave };
            DnfAudio audio = go.AddComponent<DnfAudio>();
            audio._library = library;
            audio._scheduler = new SoundScheduler(library.Catalog);
            audio._audible = audible;
            audio._tempDir = Path.Combine(Application.temporaryCachePath, "dnf-audio");
            audio.PrepareTempDirectory();
            if (UnityEngine.Object.FindFirstObjectByType<AudioListener>() == null)
            {
                go.AddComponent<AudioListener>();   // 舞台相机不带监听器;没有监听器时 Unity 每帧警告
            }
            AudioListener.volume = audible ? 1f : 0f;
            return audio;
        }

        /// <summary>读一个 Ogg 文件的请求(file:// 地址):整个文件读进内存、保持压缩,播放时再解码。调用方负责发送与释放。</summary>
        public static UnityWebRequest CreateClipRequest(string path)
        {
            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);
            ((DownloadHandlerAudioClip)request.downloadHandler).compressed = true;
            return request;
        }

        // ------------------------------------------------------------------ 每帧

        /// <summary>
        /// 游戏每推进一步调一次:取走 <paramref name="source"/> 这一步发出的提示,推进调度器 <paramref name="deltaMs"/> 毫秒并执行产生的命令。
        /// <paramref name="source"/> 为 null 时只推进时间(周期音效、延迟声音照常到点)。
        /// </summary>
        public void Tick(double deltaMs, ISoundSource source)
        {
            if (_scheduler == null)
            {
                return;
            }
            _cues.Clear();
            source?.DrainSounds(_cues);
            CueCount += _cues.Count;
            _commands.Clear();
            _scheduler.Update(deltaMs, _cues, _commands);
            for (int i = 0; i < _commands.Count; i++)
            {
                Execute(_commands[i]);
            }
            MaxInFlight = Math.Max(MaxInFlight, _playbacks.Count);
        }

        /// <summary>
        /// 停掉所有声音(离开副本、换阶段时):调度器清掉在放的与还没开始的声音和房间声音的记录,执行产生的停止命令。
        /// 之后新一场游戏的第一个提示会重新起音乐与环境声。
        /// </summary>
        public void StopAll()
        {
            if (_scheduler == null)
            {
                return;
            }
            _commands.Clear();
            _scheduler.StopAll(_commands);
            for (int i = 0; i < _commands.Count; i++)
            {
                Execute(_commands[i]);
            }
        }

        /// <summary>演示暂停时所有声音一起暂停。</summary>
        public void SetPaused(bool paused)
        {
            AudioListener.pause = paused;
        }

        private void Update()
        {
            if (_scheduler == null)
            {
                return;
            }
            float dt = Time.unscaledDeltaTime;
            for (int i = _fades.Count - 1; i >= 0; i--)
            {
                Fade f = _fades[i];
                f.Left -= dt;
                if (f.Left <= 0f)
                {
                    ReturnSource(f.Source);
                    _fades.RemoveAt(i);
                    continue;
                }
                f.Source.volume = f.StartVolume * (f.Left / MusicFadeSeconds);
                _fades[i] = f;
            }
            if (AudioListener.pause)
            {
                return;
            }
            // 放完了的(没有连续循环的)告诉调度器:周期音效据此安排下一次
            _scratch.Clear();
            foreach (Playback p in _playbacks.Values)
            {
                if (p.Source != null && !p.Loop && Time.frameCount > p.StartedFrame + 1 && !p.Source.isPlaying)
                {
                    _scratch.Add(p);
                }
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                Release(_scratch[i], notify: true);
            }
        }

        // ------------------------------------------------------------------ 命令

        private void Execute(SoundCommand c)
        {
            if (c.Kind == SoundCommandKind.Stop)
            {
                StopPlayback(c.Handle);
                return;
            }
            if (c.Channel == SoundChannel.Music)
            {
                MusicStarts++;
                Debug.Log("DNF_MUSIC id=" + c.Id + " loop=" + c.Loop + " n=" + MusicStarts);
            }
            else if (c.Channel == SoundChannel.Ui && string.Equals(c.Id, SceneMusic.ResultSound, StringComparison.OrdinalIgnoreCase))
            {
                ResultSounds++;
                Debug.Log("DNF_RESULT_SOUND id=" + c.Id + " n=" + ResultSounds);
            }
            string key = Key(c.File);
            var playback = new Playback
            {
                Handle = c.Handle,
                Channel = c.Channel,
                File = key,
                Loop = c.Loop,
                RequestedAt = Time.realtimeSinceStartup,
            };
            _playbacks[c.Handle] = playback;
            if (!_clips.TryGetValue(key, out ClipSlot slot))
            {
                slot = new ClipSlot { File = key };
                _clips[key] = slot;
            }
            if (slot.Failed)
            {
                Release(playback, notify: true);
                return;
            }
            if (slot.Clip != null)
            {
                slot.LastUse = Time.realtimeSinceStartup;
                StartPlayback(playback, slot.Clip);
                return;
            }
            if (!slot.Loading)
            {
                slot.Loading = true;
                StartCoroutine(LoadClip(slot));
            }
        }

        private void StopPlayback(int handle)
        {
            if (!_playbacks.TryGetValue(handle, out Playback p))
            {
                return;
            }
            if (p.Source != null && p.Channel == SoundChannel.Music)
            {
                // 音乐淡出:调度器已经把它忘了,不用再通知
                _playbacks.Remove(handle);
                _fades.Add(new Fade { Source = p.Source, Left = MusicFadeSeconds, StartVolume = p.Source.volume });
                p.Source = null;
                return;
            }
            Release(p, notify: false);
        }

        private void StartPlayback(Playback p, AudioClip clip)
        {
            if (CountStarted(p.Channel) >= ChannelCapacity(p.Channel))
            {
                Playback oldest = OldestStarted(p.Channel);
                if (oldest != null)
                {
                    StolenCount++;
                    Release(oldest, notify: true);
                }
            }
            AudioSource source = TakeSource();
            if (source == null)
            {
                FailedCount++;
                Release(p, notify: true);
                return;
            }
            source.clip = clip;
            source.loop = p.Loop;
            source.volume = ChannelVolume(p.Channel);
            source.pitch = 1f;
            source.Play();
            p.Source = source;
            p.StartedFrame = Time.frameCount;
            StartedCount++;
        }

        private int CountStarted(SoundChannel channel)
        {
            int n = 0;
            foreach (Playback p in _playbacks.Values)
            {
                if (p.Source != null && p.Channel == channel)
                {
                    n++;
                }
            }
            return n;
        }

        private Playback OldestStarted(SoundChannel channel)
        {
            Playback oldest = null;
            foreach (Playback p in _playbacks.Values)
            {
                if (p.Source != null && p.Channel == channel && (oldest == null || p.Handle < oldest.Handle))
                {
                    oldest = p;
                }
            }
            return oldest;
        }

        /// <summary>结束一个声音:停掉、还回 AudioSource;<paramref name="notify"/> 为真时告诉调度器它没了(它自己停掉的不用通知)。</summary>
        private void Release(Playback p, bool notify)
        {
            _playbacks.Remove(p.Handle);
            if (p.Source != null)
            {
                ReturnSource(p.Source);
                p.Source = null;
            }
            if (notify)
            {
                _scheduler.NotifyFinished(p.Handle);
            }
        }

        private AudioSource TakeSource()
        {
            while (_free.Count > 0)
            {
                AudioSource s = _free.Pop();
                if (s != null)
                {
                    return s;
                }
            }
            if (_all.Count >= MaxSources)
            {
                return null;
            }
            var child = new GameObject("DnfAudioSource") { hideFlags = HideFlags.HideAndDontSave };
            child.transform.SetParent(transform, false);
            AudioSource created = child.AddComponent<AudioSource>();
            created.playOnAwake = false;
            created.spatialBlend = 0f;
            created.bypassEffects = true;
            created.bypassListenerEffects = true;
            created.bypassReverbZones = true;
            _all.Add(created);
            return created;
        }

        private void ReturnSource(AudioSource source)
        {
            if (source == null)
            {
                return;
            }
            source.Stop();
            source.clip = null;
            source.loop = false;
            _free.Push(source);
        }

        // ------------------------------------------------------------------ 读剪辑

        private IEnumerator LoadClip(ClipSlot slot)
        {
            string path = null;
            string temp = null;
            string error = null;
            AudioClip clip = null;
            if (!_library.TryGetLoosePath(slot.File, out path))
            {
                if (_library.TryRead(slot.File, out byte[] data))
                {
                    temp = WriteTemp(slot.File, data);
                    path = temp;
                    if (temp == null)
                    {
                        error = "could not write the temporary file";
                    }
                }
                else
                {
                    error = "no such sound file";
                }
            }
            if (error == null)
            {
                using (UnityWebRequest request = CreateClipRequest(path))
                {
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        clip = ((DownloadHandlerAudioClip)request.downloadHandler).audioClip;
                        if (clip == null || clip.loadState == AudioDataLoadState.Failed)
                        {
                            error = "the audio clip could not be decoded";
                            clip = null;
                        }
                    }
                    else
                    {
                        error = request.error;
                    }
                }
            }
            if (temp != null)
            {
                TryDelete(temp);
            }
            slot.Loading = false;
            if (clip == null)
            {
                slot.Failed = true;
                FailedCount++;
                if (_failureLogs++ < 5)
                {
                    Debug.LogWarning("DNF_AUDIO load failed: " + slot.File + " (" + error + ")");
                }
            }
            else
            {
                slot.Clip = clip;
                slot.LastUse = Time.realtimeSinceStartup;
                LoadedCount++;
            }
            StartWaiting(slot);
            TrimCache();
        }

        /// <summary>剪辑读完了(或失败了):让等着它的声音开始放(晚了太久的音效放弃)。</summary>
        private void StartWaiting(ClipSlot slot)
        {
            _scratch.Clear();
            foreach (Playback p in _playbacks.Values)
            {
                if (p.Source == null && p.File == slot.File)
                {
                    _scratch.Add(p);
                }
            }
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < _scratch.Count; i++)
            {
                Playback p = _scratch[i];
                bool shortSound = p.Channel == SoundChannel.Effect || p.Channel == SoundChannel.Voice || p.Channel == SoundChannel.Ui;
                if (slot.Clip == null)
                {
                    Release(p, notify: true);
                }
                else if (shortSound && now - p.RequestedAt > LateLimitSeconds)
                {
                    LateCount++;
                    Release(p, notify: true);
                }
                else
                {
                    StartPlayback(p, slot.Clip);
                }
            }
        }

        /// <summary>剪辑留得太多时,丢掉最久没用、现在没人用的。</summary>
        private void TrimCache()
        {
            while (_clips.Count > MaxCachedClips)
            {
                ClipSlot victim = null;
                foreach (ClipSlot s in _clips.Values)
                {
                    if (s.Loading || InUse(s.File) || IsFading(s.Clip))
                    {
                        continue;
                    }
                    if (victim == null || s.LastUse < victim.LastUse)
                    {
                        victim = s;
                    }
                }
                if (victim == null)
                {
                    return;
                }
                _clips.Remove(victim.File);
                if (victim.Clip != null)
                {
                    UnityObjects.Destroy(victim.Clip);
                }
            }
        }

        /// <summary>正在淡出的音乐还握着这个剪辑:淡出结束之前不能丢。</summary>
        private bool IsFading(AudioClip clip)
        {
            if (clip == null)
            {
                return false;
            }
            foreach (Fade f in _fades)
            {
                if (f.Source != null && f.Source.clip == clip)
                {
                    return true;
                }
            }
            return false;
        }

        private bool InUse(string file)
        {
            foreach (Playback p in _playbacks.Values)
            {
                if (p.File == file)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ 杂项

        private static string Key(string file)
        {
            return SoundLibrary.Normalize(file).ToLowerInvariant();
        }

        /// <summary>占位的混音比例(原版的混音表没有读)。</summary>
        public static float ChannelVolume(SoundChannel channel)
        {
            switch (channel)
            {
                case SoundChannel.Music: return 0.45f;
                case SoundChannel.Room: return 0.5f;
                case SoundChannel.Voice: return 0.9f;
                case SoundChannel.Ui: return 0.7f;
                default: return 0.8f;
            }
        }

        /// <summary>每个通道同时放几个的上限(占位)。</summary>
        public static int ChannelCapacity(SoundChannel channel)
        {
            switch (channel)
            {
                case SoundChannel.Music: return 2;
                case SoundChannel.Room: return 8;
                case SoundChannel.Voice: return 10;
                case SoundChannel.Ui: return 4;
                default: return 16;
            }
        }

        private void PrepareTempDirectory()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);   // 上次异常退出留下的
                }
                Directory.CreateDirectory(_tempDir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("DNF_AUDIO temp directory: " + e.Message);
            }
        }

        private string WriteTemp(string key, byte[] data)
        {
            try
            {
                var name = new StringBuilder(key.Length + 12);
                foreach (char ch in key)
                {
                    name.Append(char.IsLetterOrDigit(ch) ? ch : '_');
                    if (name.Length >= 120)
                    {
                        break;
                    }
                }
                name.Append('_').Append(((uint)key.GetHashCode()).ToString("x8")).Append(".ogg");
                string path = Path.Combine(_tempDir, name.ToString());
                Directory.CreateDirectory(_tempDir);
                File.WriteAllBytes(path, data);
                return path;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>一行日志用的计数(证据 E-090;不含任何路径或原版内容)。</summary>
        public string Summary()
        {
            if (_scheduler == null)
            {
                return "closed";
            }
            return "cues=" + CueCount + " played=" + _scheduler.PlayCount + " started=" + StartedCount + " stopped=" + _scheduler.StopCount
                + " coalesced=" + _scheduler.CoalescedCount + " unknown=" + _scheduler.UnknownCount
                + " loaded=" + LoadedCount + " failed=" + FailedCount + " late=" + LateCount + " stolen=" + StolenCount
                + " inFlight=" + _playbacks.Count + " maxInFlight=" + MaxInFlight + " clips=" + _clips.Count
                + " roomSounds=" + _scheduler.RoomSoundCount + " music=" + (_scheduler.MusicId ?? "none") + " musicStarts=" + MusicStarts + " resultSounds=" + ResultSounds + " audible=" + _audible;
        }

        private void OnDestroy()
        {
            foreach (AudioSource s in _all)
            {
                if (s != null)
                {
                    s.Stop();
                }
            }
            _playbacks.Clear();
            foreach (ClipSlot slot in _clips.Values)
            {
                if (slot.Clip != null)
                {
                    UnityObjects.Destroy(slot.Clip);
                }
            }
            _clips.Clear();
            _library?.Dispose();
            _library = null;
            _scheduler = null;
            AudioListener.pause = false;
            if (_tempDir != null)
            {
                try
                {
                    if (Directory.Exists(_tempDir))
                    {
                        Directory.Delete(_tempDir, true);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
