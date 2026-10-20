using System;
using System.Collections.Generic;
using System.IO;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 用户自备的声音数据(证据 E-089):<c>audio.xml</c> 的目录(标签 → 文件)、<c>SoundPacks</c> 里的 NPK(每个条目就是一整个 Ogg,原样存放)、
    /// <c>Music</c> 目录里散装的 Ogg。文件名在 audio.xml 里是反斜杠、大小写混用,查找时统一成正斜杠并不分大小写
    /// (包里的条目名是小写,散装文件在 macOS/Windows 上不分大小写、在 Android 上分,所以这里自己建索引)。
    /// 三样东西缺哪样都不抛异常:没有 audio.xml 就是空目录,没有 SoundPacks 或 Music 就查不到对应的文件——游戏照常跑,只是没声音。
    /// </summary>
    public sealed class SoundLibrary : IDisposable
    {
        public const string CatalogFileName = "audio.xml";
        public const string PacksFolderName = "SoundPacks";
        public const string MusicFolderName = "Music";

        private readonly NpkLibrary _packs;
        private readonly Dictionary<string, string> _loose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>audio.xml 的内容;没有这个文件时是空目录。</summary>
        public AudioCatalog Catalog { get; }

        /// <summary>数据目录里有 audio.xml。</summary>
        public bool HasCatalog { get; }

        /// <summary>SoundPacks 里的条目数(没有这个目录是 0)。</summary>
        public int PackedCount => _packs == null ? 0 : _packs.ImageCount;

        /// <summary>Music 目录里的散装文件数。</summary>
        public int LooseCount => _loose.Count;

        private SoundLibrary(AudioCatalog catalog, bool hasCatalog, NpkLibrary packs)
        {
            Catalog = catalog;
            HasCatalog = hasCatalog;
            _packs = packs;
        }

        /// <summary>打开数据目录(Script.pvf 所在的目录)里的声音数据。</summary>
        public static SoundLibrary Open(string dnfDir)
        {
            if (string.IsNullOrEmpty(dnfDir))
            {
                throw new ArgumentException("需要数据目录", nameof(dnfDir));
            }
            if (!Directory.Exists(dnfDir))
            {
                throw new DirectoryNotFoundException("找不到数据目录: " + dnfDir);
            }

            string catalogPath = FindFile(dnfDir, CatalogFileName);
            AudioCatalog catalog = catalogPath == null ? AudioCatalog.ParseText("<AudioTagDatabase/>") : AudioCatalog.Parse(File.ReadAllBytes(catalogPath));

            string packsDir = FindDirectory(dnfDir, PacksFolderName);
            NpkLibrary packs = packsDir != null && Directory.GetFiles(packsDir, "*.npk", SearchOption.TopDirectoryOnly).Length > 0 ? new NpkLibrary(packsDir) : null;

            var library = new SoundLibrary(catalog, catalogPath != null, packs);
            string musicDir = FindDirectory(dnfDir, MusicFolderName);
            if (musicDir != null)
            {
                library.IndexLoose(musicDir);
            }
            return library;
        }

        private void IndexLoose(string musicDir)
        {
            string root = Path.GetFullPath(musicDir);
            foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                _loose[Normalize(MusicFolderName + "/" + relative).ToLowerInvariant()] = path;
            }
        }

        private static string FindFile(string dir, string name)
        {
            foreach (string path in Directory.GetFiles(dir))
            {
                if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return null;
        }

        private static string FindDirectory(string dir, string name)
        {
            foreach (string path in Directory.GetDirectories(dir))
            {
                if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return null;
        }

        /// <summary>audio.xml 里的文件名 → 查找用的键:正斜杠、去掉两端空白与开头的斜杠。大小写由索引忽略。</summary>
        public static string Normalize(string file)
        {
            return file == null ? string.Empty : file.Trim().Replace('\\', '/').TrimStart('/');
        }

        /// <summary>这个文件(audio.xml 里的写法)在包里或 Music 目录里存在。</summary>
        public bool Exists(string file)
        {
            string key = Normalize(file);
            return key.Length > 0 && ((_packs != null && _packs.Contains(key)) || _loose.ContainsKey(key));
        }

        /// <summary>读出文件的全部字节(一个 Ogg);找不到返回 false。</summary>
        public bool TryRead(string file, out byte[] data)
        {
            string key = Normalize(file);
            data = null;
            if (key.Length == 0)
            {
                return false;
            }
            if (_packs != null && _packs.TryRead(key, out data, out _))
            {
                return true;
            }
            if (_loose.TryGetValue(key, out string path))
            {
                data = File.ReadAllBytes(path);
                return true;
            }
            return false;
        }

        /// <summary>包里与 Music 目录里全部文件的查找键(小写、正斜杠;命令行的覆盖检查用,不要逐帧调用)。</summary>
        public List<string> AllFiles()
        {
            var all = _packs == null ? new List<string>() : _packs.AllNames();
            foreach (string key in _loose.Keys)
            {
                all.Add(key);
            }
            return all;
        }

        /// <summary>散装文件的磁盘路径(Music 目录里的);包里的条目没有路径,返回 false。</summary>
        public bool TryGetLoosePath(string file, out string path)
        {
            return _loose.TryGetValue(Normalize(file), out path);
        }

        public void Dispose()
        {
            _packs?.Dispose();
        }
    }
}
