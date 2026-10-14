using System;
using System.Collections.Generic;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 把 <see cref="SpriteFrame"/>(RGBA、第一行在上)变成 Unity 的 <see cref="Sprite"/>:
    /// 纹理格式 RGBA32、点采样、不要 mipmap、边缘夹紧;精灵的枢轴在左上角,1 个单位 = 1 个像素。
    /// Unity 纹理的第 0 行在下面,所以上传时按行翻转。
    /// 精灵按 <see cref="SpriteFrame"/> 引用缓存(DnfAssets 对同一帧总是返回同一个对象),超过上限时淘汰最久没用的。
    /// 颜色按"画面像素"原样上传,所以工程必须用 Gamma 色彩空间(DNF 在伽马空间里做混合)。
    /// </summary>
    public sealed class SpriteTextures : IDisposable
    {
        private sealed class Entry
        {
            public Sprite Sprite;
            public Texture2D Texture;
            public long LastUse;
        }

        private readonly Dictionary<SpriteFrame, Entry> _entries = new Dictionary<SpriteFrame, Entry>();
        private readonly int _maxEntries;
        private long _clock;

        public int Count => _entries.Count;

        public SpriteTextures(int maxEntries = 2048)
        {
            if (maxEntries < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntries));
            }
            _maxEntries = maxEntries;
        }

        /// <summary>取(必要时创建)某一帧的精灵;宽或高为 0 的帧没有可画的东西,返回 null。</summary>
        public Sprite Get(SpriteFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }
            if (frame.Width == 0 || frame.Height == 0)
            {
                return null;
            }
            if (_entries.TryGetValue(frame, out Entry e))
            {
                e.LastUse = ++_clock;
                return e.Sprite;
            }
            if (_entries.Count >= _maxEntries)
            {
                EvictOldest();
            }
            Texture2D tex = Upload(frame);
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, frame.Width, frame.Height), new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
            sprite.name = tex.name;
            _entries[frame] = new Entry { Sprite = sprite, Texture = tex, LastUse = ++_clock };
            return sprite;
        }

        private static Texture2D Upload(SpriteFrame frame)
        {
            int w = frame.Width;
            int h = frame.Height;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                name = "dnf-frame-" + w + "x" + h,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var flipped = new byte[frame.Rgba.Length];
            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(frame.Rgba, y * stride, flipped, (h - 1 - y) * stride, stride);
            }
            tex.SetPixelData(flipped, 0);
            tex.Apply(false, true);
            return tex;
        }

        private void EvictOldest()
        {
            SpriteFrame oldestKey = null;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<SpriteFrame, Entry> kv in _entries)
            {
                if (kv.Value.LastUse < oldest)
                {
                    oldest = kv.Value.LastUse;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey != null)
            {
                Release(_entries[oldestKey]);
                _entries.Remove(oldestKey);
            }
        }

        private static void Release(Entry e)
        {
            UnityObjects.Destroy(e.Sprite);
            UnityObjects.Destroy(e.Texture);
        }

        public void Dispose()
        {
            foreach (Entry e in _entries.Values)
            {
                Release(e);
            }
            _entries.Clear();
        }
    }
}
