using System;
using System.Collections;
using System.IO;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇演示:运行时读取用户自备的 Script.pvf 与 ImagePacks2,用 <see cref="TownView"/> 实时播放一张城镇地图
    /// (默认网关 map/elvengard/(r)gate.map:地图图层、叠加光效、NPC;没有主角、名牌与界面)。
    /// 不分发、不复制任何原版内容:数据只在内存里解码。
    ///
    /// 数据目录(里面要有 Script.pvf 与 ImagePacks2)按下面的顺序找:
    ///   1. 命令行 -dnfdir &lt;目录&gt;;2. 环境变量 DNF_DIR;3. 上次在界面里输入并保存的路径(PlayerPrefs);
    ///   4. (仅编辑器)仓库根目录 local.env 里的 DNF_DIR;5. 应用的持久数据目录下的 DNF 文件夹(Android/iOS 把数据放这里)。
    /// 都找不到就显示一个输入框。
    ///
    /// 命令行:-dnfdir &lt;目录&gt; -dnfmap &lt;PVF 内地图路径&gt; [-dnfshot &lt;输出.png&gt; [-dnftime &lt;毫秒&gt;]] [-dnfquitafter &lt;秒&gt;]
    /// 带 -dnfshot 时只离屏渲染一帧写成 PNG 就退出(用来和 CPU 合成器逐字节对照,见 tools/unity-build-town.sh)。
    /// 带 -dnfquitafter 时实时运行指定秒数后退出,并在日志里留一行 DNF_TOWN_DEMO ran …(用来在无窗口的环境里检查实时循环)。
    /// 按键:空格 暂停/继续;左右方向键 暂停时按 50 毫秒步进;H 显示/隐藏文字;Esc 退出;点击/触摸 暂停/继续。
    /// 画面以整数倍放大并居中(留黑边),所以与原版 800×600 像素一一对应、不糊。
    /// </summary>
    public sealed class TownDemo : MonoBehaviour
    {
        public const string DefaultMap = "map/elvengard/(r)gate.map";
        public const string PrefsKey = "DnfDir";

        private enum State
        {
            Loading,
            NeedDirectory,
            Running,
            Failed,
        }

        [SerializeField] private string mapPath = DefaultMap;

        private static readonly Color StageBackground = new Color32(40, 40, 40, 255);

        private State _state = State.Loading;
        private string _message = "Starting…";
        private string _dirInput = "";
        private string _argDir;
        private string _shotPath;
        private long _shotTimeMs = 545;
        private float _quitAfterSeconds = -1f;
        private int _frames;
        private float _runningSince = -1f;
        private DnfAssets _assets;
        private TownView _view;
        private Camera _bars;
        private double _clockMs;          // 本帧显示的动画时间(毫秒)
        private double _originReal;       // t = 0 对应的 Time.realtimeSinceStartupAsDouble;动画时间按墙钟走,所以打开数据时的卡顿不会算进去
        private double _pausedAtMs;
        private bool _paused;
        private bool _hud = true;
        private int _spriteCount;
        private float _fps = 60f;
        private GUIStyle _label;
        private GUIStyle _field;
        private GUIStyle _button;

        private void Awake()
        {
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            // 黑边相机:先把整个窗口清成黑色,舞台相机只在中间的 800×600(的整数倍)视口里画
            var go = new GameObject("DnfBars") { hideFlags = HideFlags.HideAndDontSave };
            _bars = go.AddComponent<Camera>();
            _bars.clearFlags = CameraClearFlags.SolidColor;
            _bars.backgroundColor = Color.black;
            _bars.cullingMask = 0;
            _bars.depth = -100;
        }

        private IEnumerator Start()
        {
            ParseArguments();
            yield return null; // 让第一帧("Starting…")先出来,再做耗时的打开
            if (!TryResolveDirectory(out string dir))
            {
                _state = State.NeedDirectory;
                _message = "Could not find the game data folder (it must contain Script.pvf and ImagePacks2).";
                yield break;
            }
            yield return OpenAndRun(dir);
        }

        private void ParseArguments()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "-dnfdir":
                        if (value != null) { _argDir = value; i++; }
                        break;
                    case "-dnfmap":
                        if (value != null) { mapPath = value; i++; }
                        break;
                    case "-dnfshot":
                        if (value != null) { _shotPath = value; i++; }
                        break;
                    case "-dnftime":
                        if (value != null && long.TryParse(value, out long t)) { _shotTimeMs = t; i++; }
                        break;
                    case "-dnfquitafter":
                        if (value != null && float.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float q)) { _quitAfterSeconds = q; i++; }
                        break;
                }
            }
        }

        private static bool IsDataDirectory(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Script.pvf"));
        }

        private bool TryResolveDirectory(out string dir)
        {
            string saved = PlayerPrefs.GetString(PrefsKey, "");
            string[] candidates =
            {
                _argDir,
                Environment.GetEnvironmentVariable("DNF_DIR"),
                saved,
                ReadLocalEnv(),
                Path.Combine(Application.persistentDataPath, "DNF"),
            };
            foreach (string c in candidates)
            {
                if (IsDataDirectory(c))
                {
                    dir = c;
                    return true;
                }
            }
            dir = null;
            _dirInput = !string.IsNullOrEmpty(saved) ? saved : (_argDir ?? "");
            return false;
        }

        /// <summary>编辑器里:读仓库根目录 local.env 的 DNF_DIR(该文件已被 .gitignore 排除)。播放器里不读。</summary>
        private static string ReadLocalEnv()
        {
#if UNITY_EDITOR
            try
            {
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "local.env"));
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("DNF_DIR=", StringComparison.Ordinal))
                        {
                            return t.Substring("DNF_DIR=".Length).Trim().Trim('"', '\'');
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
#endif
            return null;
        }

        private IEnumerator OpenAndRun(string dir)
        {
            _state = State.Loading;
            _message = "Opening the game data…";
            yield return null;
            try
            {
                DisposeView();
                _assets = DnfAssets.Open(dir);
                _view = new TownView(_assets, mapPath, TownProjection.FromE024, StageBackground);
            }
            catch (Exception e)
            {
                Fail(e);
                yield break;
            }

            if (!string.IsNullOrEmpty(_shotPath))
            {
                SaveShotAndQuit();
                yield break;
            }

            if (_quitAfterSeconds < 0f)
            {
                PlayerPrefs.SetString(PrefsKey, dir); // 无人值守的检查运行不改动用户的偏好
            }
            _clockMs = 0;
            _paused = false;
            _originReal = Time.realtimeSinceStartupAsDouble;
            _state = State.Running;
            _runningSince = Time.realtimeSinceStartup;
            Debug.Log("DNF_TOWN_DEMO running map=" + mapPath + " npcs=" + _view.NpcCount + " layers=" + _view.LayerCount);
        }

        private void SaveShotAndQuit()
        {
            try
            {
                var sprites = _view.ShowAt(_shotTimeMs);
                byte[] rgba = _view.Stage.Capture();
                WritePng(rgba, _shotPath);
                Debug.Log("DNF_TOWN_DEMO shot=" + _shotPath + " t=" + _shotTimeMs + " npcs=" + _view.NpcCount + " layers=" + _view.LayerCount
                    + " sprites=" + sprites.Count);
                Application.Quit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("DNF_TOWN_DEMO shot failed: " + e);
                Application.Quit(1);
            }
        }

        private void Fail(Exception e)
        {
            DisposeView();
            Debug.LogError("DNF_TOWN_DEMO failed: " + e);
            if (!string.IsNullOrEmpty(_shotPath))
            {
                Application.Quit(1);
                return;
            }
            _state = State.Failed;
            _message = e.GetType().Name + ": " + e.Message;
        }

        private void DisposeView()
        {
            _view?.Dispose();
            _view = null;
            _assets?.Dispose();
            _assets = null;
        }

        private void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.05f);
            if (_state != State.Running || _view == null)
            {
                return;
            }
            _clockMs = NowMs();
            try
            {
                _spriteCount = _view.ShowAt((long)_clockMs).Count;
                _frames++;
            }
            catch (Exception e)
            {
                Fail(e);
                return;
            }
            if (_quitAfterSeconds >= 0f && Time.realtimeSinceStartup - _runningSince >= _quitAfterSeconds)
            {
                float seconds = Time.realtimeSinceStartup - _runningSince;
                Debug.Log("DNF_TOWN_DEMO ran frames=" + _frames + " seconds=" + seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    + " clockMs=" + (long)_clockMs + " sprites=" + _spriteCount);
                _quitAfterSeconds = -1f;
                Application.Quit(0);
            }
        }

        private double NowMs()
        {
            return _paused ? _pausedAtMs : (Time.realtimeSinceStartupAsDouble - _originReal) * 1000.0;
        }

        private void TogglePause()
        {
            if (_paused)
            {
                _originReal = Time.realtimeSinceStartupAsDouble - _pausedAtMs / 1000.0;
                _paused = false;
            }
            else
            {
                _pausedAtMs = NowMs();
                _paused = true;
            }
        }

        /// <summary>把动画时间拨动 <paramref name="deltaMs"/> 毫秒(不早于 0)。</summary>
        private void Step(double deltaMs)
        {
            double t = Math.Max(0.0, NowMs() + deltaMs);
            if (_paused)
            {
                _pausedAtMs = t;
            }
            else
            {
                _originReal = Time.realtimeSinceStartupAsDouble - t / 1000.0;
            }
            _clockMs = t;
        }

        private void LateUpdate()
        {
            if (_state != State.Running || _view == null)
            {
                return;
            }
            // 整数倍放大(窗口不到 800×600 时按比例缩小),居中,四周留黑边
            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            float scale = Mathf.Min((float)w / PixelScreen.Width, (float)h / PixelScreen.Height);
            if (scale >= 1f)
            {
                scale = Mathf.Floor(scale);
            }
            float vw = PixelScreen.Width * scale, vh = PixelScreen.Height * scale;
            _view.Stage.Camera.pixelRect = new Rect(Mathf.Floor((w - vw) * 0.5f), Mathf.Floor((h - vh) * 0.5f), vw, vh);
        }

        private void EnsureStyles()
        {
            if (_label != null)
            {
                return;
            }
            _label = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true };
            _field = new GUIStyle(GUI.skin.textField);
            _button = new GUIStyle(GUI.skin.button);
        }

        private void ApplyFontSizes()
        {
            int size = Mathf.Clamp(Screen.height / 36, 12, 40);
            _label.fontSize = size;
            _field.fontSize = size;
            _button.fontSize = size;
        }

        private void DrawShadowed(Rect rect, string text)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _label);
            GUI.color = Color.white;
            GUI.Label(rect, text, _label);
            GUI.color = old;
        }

        private void OnGUI()
        {
            EnsureStyles();
            ApplyFontSizes();
            Event e = Event.current;
            switch (_state)
            {
                case State.Running:
                    HandleRunningInput(e);
                    if (_hud)
                    {
                        DrawHud();
                    }
                    break;
                case State.NeedDirectory:
                case State.Failed:
                    DrawDirectoryPrompt(e);
                    break;
                default:
                    DrawShadowed(new Rect(20, 20, Screen.width - 40, Screen.height - 40), _message);
                    break;
            }
        }

        private void HandleRunningInput(Event e)
        {
            if (e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Space:
                        TogglePause();
                        break;
                    case KeyCode.LeftArrow:
                        Step(-50.0);
                        break;
                    case KeyCode.RightArrow:
                        Step(50.0);
                        break;
                    case KeyCode.H:
                        _hud = !_hud;
                        break;
                    case KeyCode.Escape:
                        Quit();
                        break;
                }
            }
            else if (e.type == EventType.MouseDown && e.button == 0)
            {
                TogglePause(); // 触摸屏上没有空格键
            }
        }

        private void DrawHud()
        {
            float pad = 12f;
            string text = "DNF town demo (Unity)   map: " + mapPath + "\n"
                + "t = " + ((long)_clockMs) + " ms" + (_paused ? " (paused)" : "") + "    " + _fps.ToString("F0") + " FPS    sprites: " + _spriteCount
                + "    NPCs: " + _view.NpcCount + "    map layers: " + _view.LayerCount + "\n"
                + "Space: pause   Left/Right: step 50 ms   H: hide text   Esc: quit   (no player character, no UI yet)";
            DrawShadowed(new Rect(pad, pad, Screen.width - 2 * pad, Screen.height * 0.25f), text);
        }

        private void DrawDirectoryPrompt(Event e)
        {
            float w = Mathf.Min(Screen.width - 40f, 900f);
            float x = (Screen.width - w) * 0.5f;
            float line = _label.fontSize * 1.6f;
            float y = Mathf.Max(20f, Screen.height * 0.2f);
            GUI.Label(new Rect(x, y, w, line * 3.5f), _message, _label);
            y += line * 3.5f;
            GUI.Label(new Rect(x, y, w, line), "Folder that contains Script.pvf and ImagePacks2:", _label);
            y += line;
            GUI.SetNextControlName("dnfdir");
            _dirInput = GUI.TextField(new Rect(x, y, w, line), _dirInput, _field);
            y += line * 1.3f;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            if (GUI.Button(new Rect(x, y, w * 0.4f, line * 1.2f), "Open", _button) || enter)
            {
                string dir = (_dirInput ?? "").Trim().Trim('"', '\'');
                if (IsDataDirectory(dir))
                {
                    StartCoroutine(OpenAndRun(dir));
                }
                else
                {
                    _state = State.NeedDirectory;
                    _message = "No Script.pvf in that folder.";
                }
            }
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OnDestroy()
        {
            DisposeView();
            if (_bars != null)
            {
                UnityObjects.Destroy(_bars.gameObject);
            }
        }

        /// <summary>把"第一行在上"的 RGBA 缓冲写成 PNG(Unity 的纹理第 0 行在下面,所以先翻转)。</summary>
        private static void WritePng(byte[] topDown, string path)
        {
            int w = PixelScreen.Width, h = PixelScreen.Height, stride = w * 4;
            var bottomUp = new byte[topDown.Length];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(topDown, y * stride, bottomUp, (h - 1 - y) * stride, stride);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            try
            {
                tex.SetPixelData(bottomUp, 0);
                tex.Apply(false);
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityObjects.Destroy(tex);
            }
        }
    }
}
