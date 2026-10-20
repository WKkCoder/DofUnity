using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Dnf.Data
{
    /// <summary>audio.xml 里一个条目的种类(元素名)。证据 E-089。</summary>
    public enum AudioKind
    {
        /// <summary>音效:一个声音文件。</summary>
        Effect,

        /// <summary>环境声:一个声音文件,通常循环。</summary>
        Ambient,

        /// <summary>背景音乐:一个声音文件(在 Music 目录里),循环。</summary>
        Music,

        /// <summary>随机组:按权重从几个标签里抽一个。</summary>
        Random,

        /// <summary>组:里面的标签按各自的延迟全部播放。</summary>
        Group,
    }

    /// <summary>RANDOM 与 GROUP 里的一项:指向另一个条目的标签,加上权重与延迟。</summary>
    public sealed class AudioItem
    {
        public string Tag { get; }

        /// <summary>RANDOM 的权重(属性 PROB,整数;GROUP 里没有这个属性,按 1)。</summary>
        public double Weight { get; }

        /// <summary>延迟(秒,属性 DELAY;单位是推断:取值 0.1、0.5、3 这类,与声音时长相称)。</summary>
        public double DelaySeconds { get; }

        /// <summary>延迟的随机范围(秒,属性 DELAY_RANGE)。取"延迟 + 范围 × 随机数"是推断。</summary>
        public double DelayRangeSeconds { get; }

        public bool HasLoopDelay { get; }
        public double LoopDelaySeconds { get; }
        public double LoopDelayRangeSeconds { get; }

        public AudioItem(string tag, double weight, double delaySeconds, double delayRangeSeconds, bool hasLoopDelay, double loopDelaySeconds, double loopDelayRangeSeconds)
        {
            Tag = tag;
            Weight = weight;
            DelaySeconds = delaySeconds;
            DelayRangeSeconds = delayRangeSeconds;
            HasLoopDelay = hasLoopDelay;
            LoopDelaySeconds = loopDelaySeconds;
            LoopDelayRangeSeconds = loopDelayRangeSeconds;
        }
    }

    /// <summary>audio.xml 里的一个条目(EFFECT、AMBIENT、MUSIC、RANDOM、GROUP)。</summary>
    public sealed class AudioEntry
    {
        public string Id { get; }
        public AudioKind Kind { get; }

        /// <summary>声音文件(属性 FILE,已把反斜杠换成斜杠;没有时为 null)。路径相对于游戏目录:音效在 SoundPacks 的包里,音乐在 Music 目录。</summary>
        public string File { get; }

        /// <summary>有没有 LOOP_DELAY 属性:没写表示播一次;写了(包括 0)表示循环,两次之间隔 LOOP_DELAY 秒(含义为推断)。</summary>
        public bool HasLoopDelay { get; }

        public double LoopDelaySeconds { get; }
        public double LoopDelayRangeSeconds { get; }

        /// <summary>开始前的延迟(秒)与随机范围。</summary>
        public double DelaySeconds { get; }

        public double DelayRangeSeconds { get; }
        public IReadOnlyList<AudioItem> Items { get; }

        public AudioEntry(string id, AudioKind kind, string file, bool hasLoopDelay, double loopDelaySeconds, double loopDelayRangeSeconds,
            double delaySeconds, double delayRangeSeconds, IReadOnlyList<AudioItem> items)
        {
            Id = id;
            Kind = kind;
            File = file;
            HasLoopDelay = hasLoopDelay;
            LoopDelaySeconds = loopDelaySeconds;
            LoopDelayRangeSeconds = loopDelayRangeSeconds;
            DelaySeconds = delaySeconds;
            DelayRangeSeconds = delayRangeSeconds;
            Items = items ?? Array.Empty<AudioItem>();
        }
    }

    /// <summary>
    /// 一个声音标签解开之后要播的一条:RANDOM 已抽定、GROUP 已展开,只剩声音文件。
    /// </summary>
    public readonly struct ResolvedSound
    {
        /// <summary>最后落到的那个 EFFECT/AMBIENT/MUSIC 条目的 ID。</summary>
        public string Id { get; }

        public AudioKind Kind { get; }

        /// <summary>声音文件,斜杠分隔,相对游戏目录。</summary>
        public string File { get; }

        /// <summary>从触发起算多少秒后开始播。</summary>
        public double DelaySeconds { get; }

        /// <summary>要不要循环;循环时两次之间隔 <see cref="LoopGapSeconds"/> 秒。</summary>
        public bool Loops { get; }

        public double LoopGapSeconds { get; }

        public ResolvedSound(string id, AudioKind kind, string file, double delaySeconds, bool loops, double loopGapSeconds)
        {
            Id = id;
            Kind = kind;
            File = file;
            DelaySeconds = delaySeconds;
            Loops = loops;
            LoopGapSeconds = loopGapSeconds;
        }
    }

    /// <summary>
    /// audio.xml:声音标签数据库(证据 E-089)。每个标签是一个 ID,指向一个声音文件(EFFECT/AMBIENT/MUSIC)、一组按权重随机的标签(RANDOM)
    /// 或一组带延迟的标签(GROUP)。同一个 ID 写了多次时先写的生效(全库有 39 处重复,其中 8 处内容不同,"先到先得"与文件实际存在的情况一致);
    /// ID 不区分大小写(客户端的名字比较是不区分大小写的)。
    /// 声音文件本身的读取(SoundPacks 的 NPK 与 Music 目录)与解码不在这里。
    /// </summary>
    public sealed class AudioCatalog
    {
        /// <summary>RANDOM/GROUP 互相引用的最大层数;防止数据里的环让展开死循环。</summary>
        public const int MaxDepth = 8;

        private readonly Dictionary<string, AudioEntry> _byId = new Dictionary<string, AudioEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AudioEntry> _entries = new List<AudioEntry>();

        /// <summary>按文件顺序、去掉重复之后的全部条目。</summary>
        public IReadOnlyList<AudioEntry> Entries => _entries;

        /// <summary>被"先到先得"丢掉的重复条目数。</summary>
        public int DuplicateCount { get; private set; }

        /// <summary>不认识的元素个数(忽略了)。</summary>
        public int UnknownElementCount { get; private set; }

        public int Count => _entries.Count;

        public bool TryGet(string id, out AudioEntry entry)
        {
            if (string.IsNullOrEmpty(id))
            {
                entry = null;
                return false;
            }
            return _byId.TryGetValue(id, out entry);
        }

        public bool Contains(string id)
        {
            return TryGet(id, out _);
        }

        public static AudioCatalog Parse(byte[] xml)
        {
            if (xml == null)
            {
                throw new ArgumentNullException(nameof(xml));
            }
            using (var stream = new MemoryStream(xml, false))
            {
                return Parse(stream);
            }
        }

        public static AudioCatalog ParseText(string xml)
        {
            if (xml == null)
            {
                throw new ArgumentNullException(nameof(xml));
            }
            using (var reader = new StringReader(xml))
            {
                return Read(XmlReader.Create(reader, Settings()));
            }
        }

        public static AudioCatalog Parse(Stream xml)
        {
            if (xml == null)
            {
                throw new ArgumentNullException(nameof(xml));
            }
            return Read(XmlReader.Create(xml, Settings()));
        }

        private static XmlReaderSettings Settings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            };
        }

        private static AudioCatalog Read(XmlReader reader)
        {
            var catalog = new AudioCatalog();
            using (reader)
            {
                if (!reader.ReadToFollowing("AudioTagDatabase"))
                {
                    throw new DnfFormatException("audio.xml 缺少根元素 AudioTagDatabase");
                }
                if (reader.IsEmptyElement)
                {
                    return catalog;
                }
                int depth = reader.Depth;
                reader.Read();
                while (reader.NodeType != XmlNodeType.None && reader.Depth > depth)
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        reader.Read();
                        continue;
                    }
                    catalog.ReadEntry(reader);
                }
            }
            return catalog;
        }

        private void ReadEntry(XmlReader reader)
        {
            string name = reader.Name;
            int depth = reader.Depth;
            bool empty = reader.IsEmptyElement;
            if (!TryKind(name, out AudioKind kind))
            {
                UnknownElementCount++;
                reader.Skip();
                return;
            }
            string id = reader.GetAttribute("ID");
            string file = reader.GetAttribute("FILE");
            string loop = reader.GetAttribute("LOOP_DELAY");
            AudioEntryAttributes a = AudioEntryAttributes.Read(reader);
            var items = new List<AudioItem>();
            if (!empty)
            {
                reader.Read();
                while (reader.NodeType != XmlNodeType.None && reader.Depth > depth)
                {
                    if (reader.NodeType == XmlNodeType.Element && string.Equals(reader.Name, "ITEM", StringComparison.OrdinalIgnoreCase))
                    {
                        string tag = reader.GetAttribute("TAG");
                        AudioEntryAttributes ia = AudioEntryAttributes.Read(reader);
                        double weight = ParseNumber(reader.GetAttribute("PROB"), 1.0);
                        if (!string.IsNullOrEmpty(tag))
                        {
                            items.Add(new AudioItem(tag.Trim(), weight, ia.Delay, ia.DelayRange, ia.HasLoop, ia.Loop, ia.LoopRange));
                        }
                    }
                    reader.Skip();
                }
            }
            else
            {
                reader.Read();
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }
            id = id.Trim();
            if (_byId.ContainsKey(id))
            {
                DuplicateCount++;
                return;
            }
            string normalized = string.IsNullOrWhiteSpace(file) ? null : file.Trim().Replace('\\', '/');
            var entry = new AudioEntry(id, kind, normalized, loop != null, a.Loop, a.LoopRange, a.Delay, a.DelayRange, items);
            _byId.Add(id, entry);
            _entries.Add(entry);
        }

        private static bool TryKind(string name, out AudioKind kind)
        {
            if (string.Equals(name, "EFFECT", StringComparison.OrdinalIgnoreCase))
            {
                kind = AudioKind.Effect;
            }
            else if (string.Equals(name, "AMBIENT", StringComparison.OrdinalIgnoreCase))
            {
                kind = AudioKind.Ambient;
            }
            else if (string.Equals(name, "MUSIC", StringComparison.OrdinalIgnoreCase))
            {
                kind = AudioKind.Music;
            }
            else if (string.Equals(name, "RANDOM", StringComparison.OrdinalIgnoreCase))
            {
                kind = AudioKind.Random;
            }
            else if (string.Equals(name, "GROUP", StringComparison.OrdinalIgnoreCase))
            {
                kind = AudioKind.Group;
            }
            else
            {
                kind = AudioKind.Effect;
                return false;
            }
            return true;
        }

        private static double ParseNumber(string text, double fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        private readonly struct AudioEntryAttributes
        {
            public readonly double Delay, DelayRange, Loop, LoopRange;
            public readonly bool HasLoop;

            private AudioEntryAttributes(double delay, double delayRange, bool hasLoop, double loop, double loopRange)
            {
                Delay = delay;
                DelayRange = delayRange;
                HasLoop = hasLoop;
                Loop = loop;
                LoopRange = loopRange;
            }

            public static AudioEntryAttributes Read(XmlReader reader)
            {
                string loop = reader.GetAttribute("LOOP_DELAY");
                return new AudioEntryAttributes(
                    ParseNumber(reader.GetAttribute("DELAY"), 0.0),
                    ParseNumber(reader.GetAttribute("DELAY_RANGE"), 0.0),
                    loop != null,
                    ParseNumber(loop, 0.0),
                    ParseNumber(reader.GetAttribute("LOOP_DELAY_RANGE"), 0.0));
            }
        }

        /// <summary>
        /// 把标签解成要播的声音:EFFECT/AMBIENT/MUSIC 就是自己;RANDOM 按权重抽一项再接着解;GROUP 的每一项都解,延迟累加。
        /// 标签不存在时返回空列表。<paramref name="random"/> 给 [0,1) 的随机数(不给就用一个固定种子的,保证可复现)。
        /// 循环属性取最外层写了 LOOP_DELAY 的那个条目(没有就取最后落到的那个叶子)。
        /// </summary>
        public List<ResolvedSound> Resolve(string id, Func<double> random = null)
        {
            var list = new List<ResolvedSound>();
            Func<double> rnd = random ?? new Random(1).NextDouble;
            ResolveInto(list, id, rnd, 0, 0.0, default);
            return list;
        }

        /// <summary>解的过程中一路带下去的循环设置:最外层写了 LOOP_DELAY 的条目(或项)定下来。</summary>
        private readonly struct LoopSpec
        {
            public readonly bool Loops;
            public readonly double Gap, Range;

            public LoopSpec(double gap, double range)
            {
                Loops = true;
                Gap = gap;
                Range = range;
            }
        }

        /// <summary>只要第一个解开的声音(单文件的场合);没有返回 false。</summary>
        public bool TryResolveFirst(string id, Func<double> random, out ResolvedSound sound)
        {
            List<ResolvedSound> all = Resolve(id, random);
            if (all.Count == 0)
            {
                sound = default;
                return false;
            }
            sound = all[0];
            return true;
        }

        /// <summary>
        /// 标签可能用到的全部文件:RANDOM 的每一项、GROUP 的每一项都算,去重,按第一次出现的顺序;循环引用与超过 <see cref="MaxDepth"/> 的部分略过,标签不存在返回空列表。
        /// 预加载与覆盖检查用(<see cref="Resolve"/> 只给出这一次抽到的)。
        /// </summary>
        public List<string> CollectFiles(string id)
        {
            var files = new List<string>();
            CollectInto(files, new HashSet<string>(StringComparer.OrdinalIgnoreCase), id, 0);
            return files;
        }

        private void CollectInto(List<string> files, HashSet<string> seen, string id, int depth)
        {
            if (depth > MaxDepth || !TryGet(id, out AudioEntry entry))
            {
                return;
            }
            if (entry.Kind == AudioKind.Random || entry.Kind == AudioKind.Group)
            {
                foreach (AudioItem item in entry.Items)
                {
                    CollectInto(files, seen, item.Tag, depth + 1);
                }
            }
            else if (!string.IsNullOrEmpty(entry.File) && seen.Add(entry.File))
            {
                files.Add(entry.File);
            }
        }

        private void ResolveInto(List<ResolvedSound> into, string id, Func<double> rnd, int depth, double baseDelay, LoopSpec loop)
        {
            if (depth > MaxDepth || !TryGet(id, out AudioEntry entry))
            {
                return;
            }
            double delay = baseDelay + entry.DelaySeconds + (entry.DelayRangeSeconds > 0 ? entry.DelayRangeSeconds * rnd() : 0.0);
            if (!loop.Loops && entry.HasLoopDelay)
            {
                loop = new LoopSpec(entry.LoopDelaySeconds, entry.LoopDelayRangeSeconds);
            }
            switch (entry.Kind)
            {
                case AudioKind.Random:
                {
                    AudioItem pick = PickWeighted(entry.Items, rnd);
                    if (pick != null)
                    {
                        ResolveItem(into, pick, rnd, depth, delay, loop);
                    }
                    break;
                }
                case AudioKind.Group:
                    foreach (AudioItem item in entry.Items)
                    {
                        ResolveItem(into, item, rnd, depth, delay, loop);
                    }
                    break;
                default:
                {
                    if (string.IsNullOrEmpty(entry.File))
                    {
                        return;
                    }
                    double gap = loop.Loops ? loop.Gap + (loop.Range > 0 ? loop.Range * rnd() : 0.0) : 0.0;
                    into.Add(new ResolvedSound(entry.Id, entry.Kind, entry.File, delay, loop.Loops, gap));
                    break;
                }
            }
        }

        private void ResolveItem(List<ResolvedSound> into, AudioItem item, Func<double> rnd, int depth, double baseDelay, LoopSpec loop)
        {
            double delay = baseDelay + item.DelaySeconds + (item.DelayRangeSeconds > 0 ? item.DelayRangeSeconds * rnd() : 0.0);
            if (!loop.Loops && item.HasLoopDelay)
            {
                loop = new LoopSpec(item.LoopDelaySeconds, item.LoopDelayRangeSeconds);
            }
            ResolveInto(into, item.Tag, rnd, depth + 1, delay, loop);
        }

        private static AudioItem PickWeighted(IReadOnlyList<AudioItem> items, Func<double> rnd)
        {
            if (items == null || items.Count == 0)
            {
                return null;
            }
            double total = 0;
            foreach (AudioItem item in items)
            {
                total += item.Weight > 0 ? item.Weight : 0;
            }
            if (total <= 0)
            {
                return items[0];
            }
            double roll = rnd() * total;
            foreach (AudioItem item in items)
            {
                double w = item.Weight > 0 ? item.Weight : 0;
                if (roll < w)
                {
                    return item;
                }
                roll -= w;
            }
            return items[items.Count - 1];
        }
    }
}
