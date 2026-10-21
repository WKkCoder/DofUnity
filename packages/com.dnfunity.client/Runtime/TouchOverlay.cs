using System;
using Dnf.Play;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 触屏操作层在 Unity 一侧的宿主(M13):把 <see cref="Input"/> 里的触摸(桌面上可以用鼠标模拟一根手指,无人值守时可以按 <see cref="TouchScript"/> 的时间表)
    /// 喂给纯逻辑的 <see cref="TouchControls"/>,并在 OnGUI 里画出摇杆与按钮。规则全在 <see cref="TouchControls"/>(有单元测试),这里只管读输入与画图。
    /// 控件是屏幕空间的(贴着安全区的边),不在 800×600 的画面里:宽屏手机上画面两侧的黑边正好放手指。图形是本项目自己画的半透明圆,不含任何原版内容。
    /// </summary>
    public sealed class TouchOverlay : IDisposable
    {
        private const int MouseFinger = 0;
        private const int TextureSize = 128;

        private readonly TouchControls _controls;
        private readonly TouchScript _script;
        private readonly bool _mouse;
        private bool _mouseDown;
        private Texture2D _disc;
        private Texture2D _ring;
        private GUIStyle _label;

        /// <param name="simulateWithMouse">桌面上没有触摸屏时,用鼠标左键模拟一根手指(只能验证单指的操作)。</param>
        /// <param name="script">非空时按时间表替手指操作(无人值守的检查),不再读真实的触摸与鼠标。</param>
        public TouchOverlay(bool simulateWithMouse, TouchScript script)
        {
            _mouse = simulateWithMouse;
            _script = script != null && !script.IsEmpty ? script : null;
            Insets(out double left, out double top, out double right, out double bottom);
            _controls = new TouchControls(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height), left, top, right, bottom);
        }

        /// <summary>安全区到屏幕四边的距离(像素)。拿不到安全区(无窗口的批处理模式、桌面)时整块屏幕都算安全。</summary>
        private static void Insets(out double left, out double top, out double right, out double bottom)
        {
            Rect safe = Screen.safeArea;
            if (safe.width <= 0f || safe.height <= 0f || Screen.width <= 0 || Screen.height <= 0)
            {
                left = 0;
                top = 0;
                right = 0;
                bottom = 0;
                return;
            }
            left = Mathf.Max(0f, safe.x);
            bottom = Mathf.Max(0f, safe.y);
            right = Mathf.Max(0f, Screen.width - (safe.x + safe.width));
            top = Mathf.Max(0f, Screen.height - (safe.y + safe.height));      // Unity 的安全区原点在左下
        }

        /// <summary>规则与状态。</summary>
        public TouchControls Controls
        {
            get { return _controls; }
        }

        /// <summary>这一帧的输入(攻击、跳跃、技能键是刚按下的边沿,各只给一次)。</summary>
        public TouchFrame Poll()
        {
            return _controls.Poll();
        }

        /// <summary>取走暂停键的"刚按下"。</summary>
        public bool TakePause()
        {
            return _controls.TakePause();
        }

        /// <summary>松开所有手指(切到没有触屏操作的界面、暂停时用)。</summary>
        public void Release()
        {
            _controls.Reset();
            _mouseDown = false;
        }

        /// <summary>
        /// 每帧读一次输入:屏幕尺寸或安全区变了先换摆法,然后(按顺序)用时间表、真实触摸或模拟的鼠标喂给控制器。
        /// <paramref name="scriptMs"/> 是时间表的时钟(开始运行后过了多少毫秒)。
        /// </summary>
        public void Pump(double scriptMs)
        {
            Insets(out double left, out double top, out double right, out double bottom);
            _controls.Resize(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height), left, top, right, bottom);
            if (_script != null)
            {
                _script.Advance(_controls, scriptMs);
                return;
            }
            int count = Input.touchCount;
            if (count > 0)
            {
                _mouseDown = false;
                for (int i = 0; i < count; i++)
                {
                    Touch t = Input.GetTouch(i);
                    double x = t.position.x, y = Screen.height - t.position.y;      // Unity 的触摸坐标原点在左下
                    switch (t.phase)
                    {
                        case TouchPhase.Began:
                            _controls.Down(t.fingerId, x, y);
                            break;
                        case TouchPhase.Moved:
                        case TouchPhase.Stationary:
                            _controls.Move(t.fingerId, x, y);
                            break;
                        default:
                            _controls.Up(t.fingerId);
                            break;
                    }
                }
                return;
            }
            if (!_mouse)
            {
                return;
            }
            Vector3 m = Input.mousePosition;
            double mx = m.x, my = Screen.height - m.y;
            if (Input.GetMouseButtonDown(0))
            {
                _mouseDown = true;
                _controls.Down(MouseFinger, mx, my);
            }
            else if (_mouseDown && Input.GetMouseButton(0))
            {
                _controls.Move(MouseFinger, mx, my);
            }
            if (_mouseDown && !Input.GetMouseButton(0))
            {
                _mouseDown = false;
                _controls.Up(MouseFinger);
            }
        }

        /// <summary>在 OnGUI 里画控件(只在重绘事件里画)。<paramref name="paused"/> 时暂停键画成"继续"。</summary>
        public void Draw(bool paused)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            EnsureResources();
            TouchLayout l = _controls.Layout;
            Color old = GUI.color;

            // 摇杆:底圈、(在奔跑区时)亮起的外圈、拇指
            bool active = _controls.StickActive;
            float r = (float)l.StickRadius;
            float ox = (float)_controls.StickOriginX, oy = (float)_controls.StickOriginY;
            DrawTexture(_disc, ox, oy, r, new Color(0f, 0f, 0f, active ? 0.30f : 0.18f));
            DrawTexture(_ring, ox, oy, r, new Color(1f, 1f, 1f, active ? 0.60f : 0.32f));
            if (_controls.InRunZone)
            {
                DrawTexture(_ring, ox, oy, r * 1.02f, new Color(1f, 0.62f, 0.15f, 0.9f));
            }
            DrawTexture(_disc, (float)_controls.StickThumbX, (float)_controls.StickThumbY, r * 0.42f,
                _controls.InRunZone ? new Color(1f, 0.72f, 0.3f, 0.75f) : new Color(1f, 1f, 1f, active ? 0.65f : 0.35f));

            // 按钮:半透明圆 + 文字
            foreach (TouchButton b in (TouchButton[])Enum.GetValues(typeof(TouchButton)))
            {
                TouchDisc d = l.Button(b);
                bool pressed = _controls.IsPressed(b);
                float x = (float)d.X, y = (float)d.Y, br = (float)d.Radius;
                DrawTexture(_disc, x, y, br, pressed ? new Color(1f, 1f, 1f, 0.55f) : new Color(0f, 0f, 0f, 0.30f));
                DrawTexture(_ring, x, y, br, new Color(1f, 1f, 1f, pressed ? 0.95f : 0.55f));
                _label.fontSize = Mathf.Max(10, (int)(br * 0.78f));
                _label.normal.textColor = pressed ? new Color(0f, 0f, 0f, 0.9f) : new Color(1f, 1f, 1f, 0.9f);
                GUI.color = Color.white;
                GUI.Label(new Rect(x - br, y - br, br * 2f, br * 2f), LabelOf(b, paused), _label);
            }
            GUI.color = old;
        }

        private static string LabelOf(TouchButton b, bool paused)
        {
            switch (b)
            {
                case TouchButton.Attack: return "ATK";
                case TouchButton.Jump: return "JMP";
                case TouchButton.SkillA: return "A";
                case TouchButton.SkillS: return "S";
                case TouchButton.SkillD: return "D";
                default: return paused ? ">" : "II";
            }
        }

        private static void DrawTexture(Texture2D tex, float cx, float cy, float radius, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(new Rect(cx - radius, cy - radius, radius * 2f, radius * 2f), tex, ScaleMode.StretchToFill, true);
        }

        private void EnsureResources()
        {
            if (_disc != null)
            {
                return;
            }
            _disc = MakeTexture(false);
            _ring = MakeTexture(true);
            _label = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                clipping = TextClipping.Overflow,
            };
        }

        /// <summary>程序生成的白色圆盘(或圆环),边缘有一像素的抗锯齿;绘制时用 GUI.color 上色。</summary>
        private static Texture2D MakeTexture(bool ring)
        {
            var tex = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color32[TextureSize * TextureSize];
            float c = (TextureSize - 1) * 0.5f, outer = TextureSize * 0.5f - 1f, thickness = TextureSize * 0.045f;
            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float dx = x - c, dy = y - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(outer + 0.5f - d);
                    if (ring)
                    {
                        a *= Mathf.Clamp01(d - (outer - thickness) + 0.5f);
                    }
                    pixels[y * TextureSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        public void Dispose()
        {
            UnityObjects.Destroy(_disc);
            UnityObjects.Destroy(_ring);
            _disc = null;
            _ring = null;
        }
    }
}
