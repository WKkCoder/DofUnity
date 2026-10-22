using System;
using System.Collections.Generic;
using System.IO;
using Dnf.Data;

// 证据:Script.pvf 与 ImagePacks2 的布局见 E-001、E-002;像素规则见 E-023。
namespace Dnf.Render
{
    /// <summary>
    /// 用户自备数据的统一入口:Script.pvf(脚本、动画)+ ImagePacks2(图像)。
    /// 只读;解码结果只缓存在内存里,不落盘。把"从原版数据里取东西"的路径集中在这里,
    /// 这样 Unity 端、命令行与测试用同一份代码。
    /// </summary>
    public sealed class DnfAssets : ISpriteSource, IDisposable
    {
        private const int MaxCachedImgs = 24;

        private readonly Dictionary<string, AniFile> _anis = new Dictionary<string, AniFile>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptLine[]> _scripts = new Dictionary<string, ScriptLine[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, SpriteFrame> _frames = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        private readonly Dictionary<string, ImgFile> _imgs = new Dictionary<string, ImgFile>(StringComparer.Ordinal);
        private readonly List<string> _imgOrder = new List<string>();

        public PvfArchive Pvf { get; }
        public PvfStringTable Strings { get; }
        public NpkLibrary Images { get; }

        /// <summary>
        /// 用户自备数据所在的目录(<see cref="Open"/> 打开的那个;用构造函数直接组装时为 null)。
        /// 用来找放在数据旁边的、用户自己提供的可选文件(例如经验表之外的 monster_reward_ref.csv,证据 E-111)。
        /// </summary>
        public string DataDirectory { get; private set; }

        public DnfAssets(PvfArchive pvf, NpkLibrary images)
        {
            Pvf = pvf ?? throw new ArgumentNullException(nameof(pvf));
            Images = images ?? throw new ArgumentNullException(nameof(images));
            Strings = new PvfStringTable(Pvf.Read("stringtable.bin"));
        }

        /// <summary>打开游戏目录:里面应当有 Script.pvf 与 ImagePacks2/。</summary>
        public static DnfAssets Open(string dnfDirectory)
        {
            string pvfPath = Path.Combine(dnfDirectory, "Script.pvf");
            string packs = Path.Combine(dnfDirectory, "ImagePacks2");
            if (!File.Exists(pvfPath))
            {
                throw new FileNotFoundException("游戏目录里没有 Script.pvf", pvfPath);
            }
            PvfArchive pvf = PvfArchive.Open(pvfPath);
            try
            {
                return new DnfAssets(pvf, new NpkLibrary(packs)) { DataDirectory = dnfDirectory };
            }
            catch
            {
                pvf.Dispose();
                throw;
            }
        }

        /// <summary>读取并解析一个标记流脚本;路径不区分大小写,会先规范化。不存在或不是标记流脚本时返回 null。</summary>
        public ScriptLine[] ReadScript(string path)
        {
            string key = PvfPath.Normalize(path);
            if (_scripts.TryGetValue(key, out ScriptLine[] cached))
            {
                return cached;
            }
            ScriptLine[] doc = null;
            if (Pvf.TryGetEntry(key, out PvfEntry entry))
            {
                byte[] raw = Pvf.Read(entry);
                if (TokenScript.IsTokenScript(raw))
                {
                    doc = ScriptDocument.Parse(raw, Strings);
                }
            }
            _scripts[key] = doc;
            return doc;
        }

        /// <summary>读取并解析一个二进制 .ani。不存在或不是二进制 .ani 时返回 null。</summary>
        public AniFile LoadAni(string path)
        {
            string key = PvfPath.Normalize(path);
            if (_anis.TryGetValue(key, out AniFile cached))
            {
                return cached;
            }
            AniFile ani = null;
            if (Pvf.TryGetEntry(key, out PvfEntry entry))
            {
                byte[] raw = Pvf.Read(entry);
                if (raw.Length >= 8 && !TokenScript.IsTokenScript(raw))
                {
                    ani = AniFile.Parse(raw);
                }
            }
            _anis[key] = ani;
            return ani;
        }

        /// <summary>
        /// 取一个精灵帧(链接帧会跟到目标帧)。IMG 不存在、是旧版 IMG 或帧号越界时返回 false。
        /// 帧头的位置信息取自链接目标帧,这一点没有样本核对。
        /// </summary>
        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            sprite = null;
            if (imageName == null || frame < 0)
            {
                return false;
            }
            string cacheKey = imageName.ToLowerInvariant() + "#" + frame;
            if (_frames.TryGetValue(cacheKey, out sprite))
            {
                return sprite != null;
            }
            ImgFile img = GetImg(imageName.ToLowerInvariant());
            if (img == null || frame >= img.FrameCount)
            {
                _frames[cacheKey] = null;
                return false;
            }
            byte[] rgba = img.DecodeRgba(frame, out ImgFrameInfo info);
            sprite = new SpriteFrame(info.Width, info.Height, info.X, info.Y, info.FrameWidth, info.FrameHeight, rgba);
            _frames[cacheKey] = sprite;
            return true;
        }

        private ImgFile GetImg(string lowerName)
        {
            if (_imgs.TryGetValue(lowerName, out ImgFile img))
            {
                _imgOrder.Remove(lowerName);
                _imgOrder.Add(lowerName);
                return img;
            }
            if (!Images.TryRead(lowerName, out byte[] data, out _) || !ImgFile.IsV2(data))
            {
                return null;
            }
            img = ImgFile.Parse(data);
            if (_imgs.Count >= MaxCachedImgs)
            {
                string oldest = _imgOrder[0];
                _imgOrder.RemoveAt(0);
                _imgs.Remove(oldest);
            }
            _imgs[lowerName] = img;
            _imgOrder.Add(lowerName);
            return img;
        }

        public void Dispose()
        {
            Images.Dispose();
            Pvf.Dispose();
        }
    }
}
