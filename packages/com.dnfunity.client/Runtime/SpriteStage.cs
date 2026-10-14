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
    /// 普通混合用本包的 Dnf/SpriteNormal(预乘 alpha),加法混合(<see cref="SpriteBlend.Additive"/>)用 Dnf/SpriteAdditive;
    /// 两个着色器都按 float 精度计算(内置的 Sprites/Default 在移动端会降成低精度,证据 E-031),
    /// 染色 alpha 通过 SpriteRenderer.color 的 a 分量传给它们。
    /// </summary>
    public sealed class SpriteStage : MonoBehaviour
    {
        /// <summary>着色器放在包的 Resources 文件夹里,这样播放器构建一定会带上它们。</summary>
        private const string AdditiveShaderResource = "DnfSpriteAdditive";
        private const string NormalShaderResource = "DnfSpriteNormal";

        private readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        private SpriteTextures _textures;
        private Camera _camera;
        private Material _additive;
        private Material _normal;
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
                // flipX 以枢轴(左上角)为轴翻转,翻转后的精灵占 [x - 宽, x];要让它占 [ScreenX, ScreenX + 宽],位置就得右移一个宽度
                r.flipX = s.Mirrored;
                r.transform.position = PixelScreen.ToWorld(s.Mirrored ? s.ScreenX + s.Frame.Width : s.ScreenX, s.ScreenY);
                if (s.Blend == SpriteBlend.Additive)
                {
                    r.sharedMaterial = AdditiveMaterial();
                    r.color = new Color(1f, 1f, 1f, Mathf.Clamp01(s.TintAlpha / 255f));
                }
                else
                {
                    // 普通混合忽略染色 alpha(与 CPU 合成器一致;它怎样生效没有核对过)
                    r.sharedMaterial = NormalMaterial();
                    r.color = Color.white;
                }
                r.gameObject.SetActive(true);
                used++;
            }
            for (int i = used; i < _pool.Count; i++)
            {
                _pool[i].gameObject.SetActive(false);
            }
            _shown = used;
        }

        private Material AdditiveMaterial()
        {
            if (_additive == null)
            {
                _additive = CreateMaterial(AdditiveShaderResource, "Dnf/SpriteAdditive");
            }
            return _additive;
        }

        private Material NormalMaterial()
        {
            if (_normal == null)
            {
                _normal = CreateMaterial(NormalShaderResource, "Dnf/SpriteNormal");
            }
            return _normal;
        }

        private static Material CreateMaterial(string resourceName, string shaderName)
        {
            Shader shader = Resources.Load<Shader>(resourceName);
            if (shader == null)
            {
                shader = Shader.Find(shaderName);
            }
            if (shader == null)
            {
                throw new InvalidOperationException("找不到着色器 " + shaderName + "(应当在 com.dnfunity.client/Runtime/Resources/ 里)");
            }
            return new Material(shader) { name = resourceName, hideFlags = HideFlags.HideAndDontSave };
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
                r.sharedMaterial = NormalMaterial();
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
            if (_additive != null)
            {
                UnityObjects.Destroy(_additive);
                _additive = null;
            }
            if (_normal != null)
            {
                UnityObjects.Destroy(_normal);
                _normal = null;
            }
        }
    }
}
