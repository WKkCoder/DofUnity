using System;
using System.Collections.Generic;
using System.IO;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 一个目录(ImagePacks2)下全部 NPK 的合并索引:IMG 名字(小写)→ 它在哪个 NPK 的第几项。
    /// 建索引时每个 NPK 只读一次目录表,然后关闭;读取某个 IMG 时才按需打开对应的 NPK(保留最近用过的几个)。
    /// 同名 IMG 出现在多个 NPK 里时(全库 49 个),引擎的覆盖顺序**未研究**,这里取按文件名排序后最后出现的那个。
    /// </summary>
    public sealed class NpkLibrary : IDisposable
    {
        private const int MaxOpen = 16;

        private readonly object _gate = new object();
        private readonly string[] _npkPaths;
        private readonly Dictionary<string, Slot> _index = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, NpkArchive> _open = new Dictionary<int, NpkArchive>();
        private readonly List<int> _openOrder = new List<int>();

        private readonly struct Slot
        {
            public readonly int Npk;
            public readonly int Entry;

            public Slot(int npk, int entry)
            {
                Npk = npk;
                Entry = entry;
            }
        }

        /// <summary>索引里的 IMG 数(同名只算一个)。</summary>
        public int ImageCount => _index.Count;

        /// <summary>有多少个名字在多个 NPK 里重复出现。</summary>
        public int DuplicateNames { get; }

        public int NpkCount => _npkPaths.Length;

        public NpkLibrary(string imagePacksDirectory)
        {
            if (!Directory.Exists(imagePacksDirectory))
            {
                throw new DirectoryNotFoundException("找不到 ImagePacks2 目录: " + imagePacksDirectory);
            }
            string[] files = Directory.GetFiles(imagePacksDirectory, "*.npk", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            _npkPaths = files;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dup = 0;
            for (int n = 0; n < files.Length; n++)
            {
                using (NpkArchive a = NpkArchive.Open(files[n]))
                {
                    IReadOnlyList<NpkEntry> entries = a.Entries;
                    for (int e = 0; e < entries.Count; e++)
                    {
                        string name = entries[e].Name;
                        if (!seen.Add(name))
                        {
                            dup++;
                        }
                        _index[name] = new Slot(n, e);
                    }
                }
            }
            DuplicateNames = dup;
        }

        public bool Contains(string imageName) => imageName != null && _index.ContainsKey(imageName);

        /// <summary>读出 IMG 的原始字节。名字不区分大小写,形如 "sprite/map/postbox.img"。</summary>
        public bool TryRead(string imageName, out byte[] data, out string npkFileName)
        {
            data = null;
            npkFileName = null;
            if (imageName == null || !_index.TryGetValue(imageName, out Slot slot))
            {
                return false;
            }
            lock (_gate)
            {
                // 读取放在锁里:别的线程不能在这期间把这个 NPK 从"最近打开"表里淘汰并关闭
                NpkArchive archive = Open(slot.Npk);
                NpkEntry entry = archive.Entries[slot.Entry];
                data = archive.Read(entry);
            }
            npkFileName = Path.GetFileName(_npkPaths[slot.Npk]);
            return true;
        }

        private NpkArchive Open(int npk)
        {
            lock (_gate)
            {
                if (_open.TryGetValue(npk, out NpkArchive a))
                {
                    _openOrder.Remove(npk);
                    _openOrder.Add(npk);
                    return a;
                }
                if (_open.Count >= MaxOpen)
                {
                    int oldest = _openOrder[0];
                    _openOrder.RemoveAt(0);
                    _open[oldest].Dispose();
                    _open.Remove(oldest);
                }
                a = NpkArchive.Open(_npkPaths[npk]);
                _open[npk] = a;
                _openOrder.Add(npk);
                return a;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (NpkArchive a in _open.Values)
                {
                    a.Dispose();
                }
                _open.Clear();
                _openOrder.Clear();
            }
        }
    }
}
