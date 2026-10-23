using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Dnf.Render;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using Object = UnityEngine.Object;

namespace Dnf.Client
{
    /// <summary>
    /// 叠加层的汉字:读玩家机器上的系统字体,用 Unity TextCore 的 <see cref="FontAsset"/> 栅格化(证据 E-121 的探针:批处理、无显卡也能出 8 位位图)。
    /// 每个字形一栅格化出来就拷进自己的缓存(<see cref="GlyphBitmap"/>),所以图集满了、被清掉都不影响已经取到的字;
    /// 渲染层(<see cref="GlyphText"/>)只认 <see cref="IGlyphSource"/>,不碰 Unity 的类型。游戏不带任何字体文件,找不到带汉字的字体时返回 null,界面退回英文占位。
    /// 只能在主线程用(TextCore 不是线程安全的)。
    /// </summary>
    public sealed class UnityGlyphFonts : IGlyphFonts, IDisposable
    {
        public const int MinPixelSize = 8;
        public const int MaxPixelSize = 96;

        /// <summary>检验一个字体有没有汉字用的两个字。</summary>
        public const string CoverageProbe = "中文";

        private readonly Dictionary<int, UnityGlyphSource> _sources = new Dictionary<int, UnityGlyphSource>();
        private bool _disposed;

        /// <summary>字体文件的路径。</summary>
        public string FontPath { get; }

        /// <summary>字体集合(.ttc)里的第几个字体,单字体文件是 0。</summary>
        public int FaceIndex { get; }

        /// <summary>已缓存的字形个数(各字号加起来)。</summary>
        public int GlyphCount
        {
            get
            {
                int n = 0;
                foreach (UnityGlyphSource s in _sources.Values)
                {
                    n += s.CachedCount;
                }
                return n;
            }
        }

        /// <summary>栅格化字形一共花的毫秒数(只算缓存没命中的那些)。</summary>
        public double RasterMilliseconds
        {
            get
            {
                double ms = 0;
                foreach (UnityGlyphSource s in _sources.Values)
                {
                    ms += s.RasterMilliseconds;
                }
                return ms;
            }
        }

        public int SizeCount => _sources.Count;

        private UnityGlyphFonts(string path, int faceIndex)
        {
            FontPath = path;
            FaceIndex = faceIndex;
        }

        /// <summary>
        /// 找一个带汉字的字体:先试 <paramref name="requested"/>(命令行或环境变量给的,"路径" 或 "路径#下标"),再按平台的候选清单,
        /// 最后在系统字体清单里按名字找。都没有返回 null,<paramref name="note"/> 写原因或选中的字体。
        /// </summary>
        public static UnityGlyphFonts TryCreate(string requested, out string note)
        {
            var tried = new List<string>();
            foreach (string candidate in Candidates(requested, Application.platform))
            {
                if (!TrySplit(candidate, out string path, out int face) || !File.Exists(path))
                {
                    continue;
                }
                tried.Add(System.IO.Path.GetFileName(path));
                UnityGlyphFonts fonts = null;
                try
                {
                    fonts = new UnityGlyphFonts(path, face);
                    if (fonts.CoversChinese())
                    {
                        note = "ready file=" + System.IO.Path.GetFileName(path) + " face=" + face;
                        return fonts;
                    }
                }
                catch (Exception e)
                {
                    tried[tried.Count - 1] += "(" + e.GetType().Name + ")";
                }
                fonts?.Dispose();
            }
            foreach (string path in OsFontsByName())
            {
                tried.Add(System.IO.Path.GetFileName(path));
                UnityGlyphFonts fonts = null;
                try
                {
                    fonts = new UnityGlyphFonts(path, 0);
                    if (fonts.CoversChinese())
                    {
                        note = "ready file=" + System.IO.Path.GetFileName(path) + " face=0 (found in the OS font list)";
                        return fonts;
                    }
                }
                catch (Exception e)
                {
                    tried[tried.Count - 1] += "(" + e.GetType().Name + ")";
                }
                fonts?.Dispose();
            }
            note = "none: no usable font with Chinese glyphs (tried " + (tried.Count == 0 ? "nothing" : string.Join(", ", tried)) + ")";
            return null;
        }

        /// <summary>
        /// 候选字体("路径" 或 "路径#下标"),按好看与常见的顺序。第一项是玩家指定的(有的话)。
        /// 路径只是猜的位置,<see cref="TryCreate"/> 会检查文件在不在、有没有汉字。
        /// </summary>
        public static List<string> Candidates(string requested, RuntimePlatform platform)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(requested))
            {
                list.Add(requested.Trim());
            }
            switch (platform)
            {
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                    list.Add("/System/Library/Fonts/Hiragino Sans GB.ttc");
                    list.Add("/System/Library/Fonts/STHeiti Medium.ttc");
                    list.Add("/System/Library/Fonts/PingFang.ttc");
                    list.Add("/System/Library/Fonts/Supplemental/Songti.ttc");
                    list.Add("/System/Library/Fonts/Supplemental/Arial Unicode.ttf");
                    list.Add("/Library/Fonts/Arial Unicode.ttf");
                    break;
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                    foreach (string name in new[] { "msyh.ttc", "msyh.ttf", "simhei.ttf", "simsun.ttc", "Deng.ttf", "mingliu.ttc", "msjh.ttc" })
                    {
                        list.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name));
                    }
                    break;
                case RuntimePlatform.Android:
                    foreach (string name in new[] { "NotoSansCJK-Regular.ttc", "NotoSansSC-Regular.otf", "NotoSansSC-Regular.ttf", "NotoSansHans-Regular.otf", "DroidSansFallback.ttf", "DroidSansFallbackFull.ttf" })
                    {
                        list.Add("/system/fonts/" + name);
                    }
                    break;
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                    list.Add("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc");
                    list.Add("/usr/share/fonts/truetype/wqy/wqy-microhei.ttc");
                    list.Add("/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf");
                    break;
            }
            return list;
        }

        /// <summary>"路径" 或 "路径#下标" 拆开;下标写错当 0。</summary>
        public static bool TrySplit(string candidate, out string path, out int face)
        {
            path = null;
            face = 0;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }
            int hash = candidate.LastIndexOf('#');
            if (hash > 0 && int.TryParse(candidate.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0)
            {
                path = candidate.Substring(0, hash);
                face = index;
                return true;
            }
            path = candidate;
            return true;
        }

        /// <summary>系统字体清单里文件名像中文字体的(手机上没有固定路径时的最后一招)。</summary>
        private static IEnumerable<string> OsFontsByName()
        {
            string[] all;
            try
            {
                all = Font.GetPathsToOSFonts();
            }
            catch (Exception)
            {
                yield break;
            }
            foreach (string path in all)
            {
                string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
                foreach (string hint in new[] { "cjk", "yahei", "msyh", "simhei", "simsun", "pingfang", "heiti", "hiragino sans gb", "songti", "notosanssc", "sourcehan", "wqy" })
                {
                    if (name.Contains(hint))
                    {
                        yield return path;
                        break;
                    }
                }
            }
        }

        private bool CoversChinese()
        {
            IGlyphSource probe = ForSize(16);
            if (probe == null)
            {
                return false;
            }
            foreach (char c in CoverageProbe)
            {
                if (!probe.TryGetGlyph(c, out GlyphBitmap g) || g.Width == 0)
                {
                    return false;
                }
            }
            return true;
        }

        public IGlyphSource ForSize(int pixelSize)
        {
            if (_disposed)
            {
                return null;
            }
            int size = Mathf.Clamp(pixelSize, MinPixelSize, MaxPixelSize);
            if (_sources.TryGetValue(size, out UnityGlyphSource source))
            {
                return source;
            }
            source = new UnityGlyphSource(FontPath, FaceIndex, size);
            _sources[size] = source;
            return source;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (UnityGlyphSource s in _sources.Values)
            {
                s.Dispose();
            }
            _sources.Clear();
        }

        /// <summary>一个字号:一个 <see cref="FontAsset"/>(动态图集)加一份拷出来的位图缓存。</summary>
        private sealed class UnityGlyphSource : IGlyphSource, IDisposable
        {
            private readonly FontAsset _asset;
            private readonly Dictionary<int, GlyphBitmap> _cache = new Dictionary<int, GlyphBitmap>();
            private readonly Stopwatch _clock = new Stopwatch();

            public int PixelSize { get; }
            public int Ascent { get; }
            public int Descent { get; }
            public int LineHeight { get; }
            public int CachedCount => _cache.Count;
            public double RasterMilliseconds => _clock.Elapsed.TotalMilliseconds;

            public UnityGlyphSource(string path, int faceIndex, int pixelSize)
            {
                PixelSize = pixelSize;
                int atlas = pixelSize <= 24 ? 512 : (pixelSize <= 48 ? 1024 : 2048);
                // 注意:带 hinting 的光栅模式(RASTER_HINTED)按像素格对齐,小字号汉字笔画才清楚;图集留 1 像素边距防止相邻字形串色
                _asset = FontAsset.CreateFontAsset(path, faceIndex, pixelSize, 1, GlyphRenderMode.RASTER_HINTED, atlas, atlas);
                if (_asset == null)
                {
                    throw new InvalidOperationException("TextCore 打不开字体文件");
                }
                _asset.hideFlags = HideFlags.HideAndDontSave;
                FaceInfo face = _asset.faceInfo;
                Ascent = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(face.ascentLine)));
                Descent = Mathf.Max(0, Mathf.CeilToInt(Mathf.Abs(face.descentLine)));
                // 行距:取字体给的行高与 上下伸出量 里大的,并且至少比字号多 2 像素,免得汉字行与行贴在一起
                LineHeight = Mathf.Max(Mathf.CeilToInt(face.lineHeight), Mathf.Max(Ascent + Descent, pixelSize + 2));
            }

            public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
            {
                if (_cache.TryGetValue(codePoint, out glyph))
                {
                    return glyph != null;
                }
                _clock.Start();
                try
                {
                    glyph = Rasterize(codePoint);
                }
                finally
                {
                    _clock.Stop();
                }
                _cache[codePoint] = glyph;
                return glyph != null;
            }

            private GlyphBitmap Rasterize(int codePoint)
            {
                if (codePoint < 0x20 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
                {
                    return null;
                }
                uint unicode = (uint)codePoint;
                if (!_asset.characterLookupTable.TryGetValue(unicode, out Character character))
                {
                    _asset.TryAddCharacters(char.ConvertFromUtf32(codePoint), out string missing, false);
                    if (!string.IsNullOrEmpty(missing) || !_asset.characterLookupTable.TryGetValue(unicode, out character))
                    {
                        return null;
                    }
                }
                Glyph g = character.glyph;
                if (g == null)
                {
                    return null;
                }
                GlyphMetrics m = g.metrics;
                int advance = Mathf.Max(0, Mathf.RoundToInt(m.horizontalAdvance));
                GlyphRect rect = g.glyphRect;
                if (rect.width <= 0 || rect.height <= 0)
                {
                    return GlyphBitmap.Blank(advance);
                }
                Texture2D[] atlases = _asset.atlasTextures;
                if (atlases == null || g.atlasIndex < 0 || g.atlasIndex >= atlases.Length || atlases[g.atlasIndex] == null)
                {
                    return null;
                }
                Texture2D atlas = atlases[g.atlasIndex];
                if (atlas.format != TextureFormat.Alpha8 || !atlas.isReadable)
                {
                    return null;
                }
                NativeArray<byte> raw = atlas.GetRawTextureData<byte>();
                int stride = atlas.width;
                var alpha = new byte[rect.width * rect.height];
                bool ink = false;
                for (int row = 0; row < rect.height; row++)
                {
                    // 图集的第 0 行在最下面,字形位图的第 0 行要在最上面
                    int from = (rect.y + rect.height - 1 - row) * stride + rect.x;
                    int to = row * rect.width;
                    for (int col = 0; col < rect.width; col++)
                    {
                        byte a = raw[from + col];
                        alpha[to + col] = a;
                        ink |= a != 0;
                    }
                }
                if (!ink)
                {
                    return GlyphBitmap.Blank(advance);       // 空格之类:字体给了一格没墨的位图,当作只有前进量
                }
                return new GlyphBitmap(rect.width, rect.height, Mathf.RoundToInt(m.horizontalBearingX), Mathf.RoundToInt(m.horizontalBearingY), advance, alpha);
            }

            public void Dispose()
            {
                _cache.Clear();
                if (_asset == null)
                {
                    return;
                }
                Texture2D[] atlases = _asset.atlasTextures;
                if (atlases != null)
                {
                    foreach (Texture2D t in atlases)
                    {
                        Destroy(t);
                    }
                }
                Destroy(_asset);
            }

            private static void Destroy(Object o)
            {
                if (o == null)
                {
                    return;
                }
                if (Application.isPlaying)
                {
                    Object.Destroy(o);
                }
                else
                {
                    Object.DestroyImmediate(o);
                }
            }
        }
    }
}
