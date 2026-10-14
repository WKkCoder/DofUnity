using System;
using System.Collections.Generic;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 舞台:一台 800×600 像素对齐的正交相机,加上一组 SpriteRenderer,把 <see cref="SceneSprite"/> 列表画到 GPU 上。
    /// 它是 <see cref="RgbaCanvas"/>(CPU 合成)的 GPU 对应物:同一份列表,两条路径的结果应当一致。
    /// 绘制顺序 = 列表顺序(靠 sortingOrder,不靠 z)。
    /// </summary>
    public sealed class SpriteStage : MonoBehaviour
    {
        private readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        private SpriteTextures _textures;
        private Camera _camera;
        private int _shown;

        public Camera Camera => _camera;
        public int ShownCount => _shown;

        /// <summary>创建舞台(含相机)。<paramref name="background"/> 是 alpha 为 1 的纯色底。</summary>
        public static SpriteStage Create(Color background, string name = "DnfStage")
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            SpriteStage stage = go.AddComponent<SpriteStage>();
            stage._textures = new SpriteTextures();
            var camGo = new GameObject(name + "-camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(go.transform, false);
            stage._camera = camGo.AddComponent<Camera>();
            PixelScreen.Configure(stage._camera, background);
            // 相机只在 Capture 或显示时渲染,不要在 Update 里自动渲染进屏幕以外的目标
            return stage;
        }

        /// <summary>显示这组精灵(替换上一次的内容)。</summary>
        public void SetSprites(IReadOnlyList<SceneSprite> sprites)
        {
            if (sprites == null)
            {
                throw new ArgumentNullException(nameof(sprites));
            }
            int used = 0;
            for (int i = 0; i < sprites.Count; i++)
            {
                SceneSprite s = sprites[i];
                Sprite sprite = _textures.Get(s.Frame);
                if (sprite == null)
                {
                    continue;
                }
                SpriteRenderer r = Rent(used);
                r.sprite = sprite;
                r.sortingOrder = used;
                r.transform.position = PixelScreen.ToWorld(s.ScreenX, s.ScreenY);
                r.gameObject.SetActive(true);
                used++;
            }
            for (int i = used; i < _pool.Count; i++)
            {
                _pool[i].gameObject.SetActive(false);
            }
            _shown = used;
        }

        private SpriteRenderer Rent(int index)
        {
            while (_pool.Count <= index)
            {
                var go = new GameObject("sprite-" + _pool.Count) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(transform, false);
                var r = go.AddComponent<SpriteRenderer>();
                r.drawMode = SpriteDrawMode.Simple;
                r.maskInteraction = SpriteMaskInteraction.None;
                _pool.Add(r);
            }
            return _pool[index];
        }

        /// <summary>渲染一帧到 800×600 的离屏目标并读回:RGBA8888,第一行在上,与 <see cref="RgbaCanvas.Pixels"/> 同布局。</summary>
        public byte[] Capture()
        {
            return GpuCapture.Render(_camera, PixelScreen.Width, PixelScreen.Height);
        }

        private void OnDestroy()
        {
            _textures?.Dispose();
            _textures = null;
        }
    }
}
