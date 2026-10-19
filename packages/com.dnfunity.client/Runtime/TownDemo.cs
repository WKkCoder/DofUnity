using System;
using System.Collections;
using System.IO;
using Dnf.Play;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇演示:运行时读取用户自备的 Script.pvf 与 ImagePacks2,用 <see cref="TownView"/> 实时播放一张城镇地图
    /// (默认网关 map/elvengard/(r)gate.map:地图图层、叠加光效、NPC,以及一个用方向键走动的鬼剑士;没有名牌与界面)。
    /// 主角只在量过出生点与镜头范围的地图上出现(目前是赛丽亚房间,证据 E-065、E-066)。
    /// 地图是副本房间(有 [pathgate pos],例如 -dnfmap map/lorien/s2.map)时改播副本房间:瓦片、视差背景、图层动画、障碍物、出口,
    /// 以及一个站在房间通行表上走动的主角(<see cref="RoomSession"/>,证据 E-067、E-068);
    /// 实时运行时房间里还有地图 [monster] 摆好的怪物,X 键出普攻(三段连招),命中扣血、受击硬直、死亡消失,屏幕上有血条与伤害数字(<see cref="RoomCombatSession"/>,证据 E-069;
    /// 伤害是占位值,怪物行为是近似,不会伤害主角)。
    /// 带 -dnfdungeon &lt;副本定义路径&gt;(例如 dungeon/act1/lorien.dgn)时玩一整座副本:迷宫里的一格一格房间,怪物清完出口打开,走进出口换到相邻的房间,
    /// 屏幕右上角有占位小地图,首领房清空后出 CLEAR(<see cref="DungeonSession"/>,证据 E-070)。-dnfbot 让自动机替你打(-dnfexplore 先逛遍所有房间),
    /// -dnfseed 与 -dnfrandomlayout 指定战斗种子与随机选房。
    /// 不分发、不复制任何原版内容:数据只在内存里解码。
    ///
    /// 数据目录(里面要有 Script.pvf 与 ImagePacks2)按下面的顺序找:
    ///   1. 命令行 -dnfdir &lt;目录&gt;;2. 环境变量 DNF_DIR;3. 上次在界面里输入并保存的路径(PlayerPrefs);
    ///   4. (仅编辑器)仓库根目录 local.env 里的 DNF_DIR;5. 应用的持久数据目录下的 DNF 文件夹(Android/iOS 把数据放这里)。
    /// 都找不到就显示一个输入框。
    ///
    /// 命令行:-dnfdir &lt;目录&gt; -dnfmap &lt;PVF 内地图路径&gt; [-dnfshot &lt;输出.png&gt; [-dnftime &lt;毫秒&gt;]] [-dnfquitafter &lt;秒&gt;]
    ///         [-dnfnohero] [-dnfhero x,y,left|right,rest|move,毫秒[,镜头x]] [-dnfautowalk 秒:动作,…]
    ///         [-dnfmonsters | -dnfnomonsters] [-dnfplayat &lt;秒&gt;]
    ///         [-dnfdungeon &lt;副本定义路径&gt; [-dnfbot | -dnfexplore] [-dnfseed &lt;整数&gt;] [-dnfrandomlayout]]
    /// 带 -dnfshot 时只离屏渲染一帧写成 PNG 就退出(用来和 CPU 合成器逐字节对照,见 tools/unity-build-town.sh);
    /// 这时默认不画主角,带 -dnfhero 才把主角摆到指定的位置、朝向与动作进度(与 dnf render-town --scene --hero 同一写法)。
    /// 带 -dnfquitafter 时实时运行指定秒数后退出,并在日志里留一行 DNF_TOWN_DEMO ran …(用来在无窗口的环境里检查实时循环)。
    /// 带 -dnfautowalk 时不读键盘,按"开始运行后第几秒:动作"的时间表替主角按键(方向 left、right、up、down、none,可用 + 组合,再加 attack、jump 表示按一下攻击、跳跃键,a、s、d 表示按一下技能键;
    /// 见 <see cref="CombatScript"/>),用来无人值守地验证走动、撞墙、镜头跟随与挥击(例如 -dnfautowalk 0.5:left,3:none,3.2:attack)。-dnfnohero 完全不带主角。
    /// 副本房间默认带怪物(-dnfnomonsters 去掉);离屏渲染一帧时默认不带(与 CPU 合成的参照一致),带 -dnfmonsters 才画。
    /// 带 -dnfplayat &lt;秒&gt; 与 -dnfshot 时,离屏渲染前先让房间战斗按 -dnfautowalk 的时间表以固定 1/60 秒步长确定地模拟到这一刻(此时自动带怪物与主角),
    /// 与命令行 combat-check 同一份模拟,用来把战斗中的一帧与 CPU 合成逐字节对照(见 tools/unity-build-town.sh)。
    /// 副本同理:-dnfdungeon -dnfbot -dnfplayat &lt;秒&gt; -dnfshot 先让自动机按固定 1/60 秒步长打到这一刻,与命令行 dungeon-check --bot --shots-at 对照。
    /// 按键(副本房间里的主角,按原版默认键位,证据 E-072、E-076):方向键 走动(按住;同方向再按一下接着按住 = 奔跑);X 普攻(连按接下一段);C 跳跃;
    /// A S D 前三个技能快捷槽(新建的剑士是鬼斩、上挑、后跳,各有冷却);Z 与原版的另一个快捷槽没有确认绑什么,不响应。
    /// 空格 暂停/继续;暂停时左右方向键按 50 毫秒步进动画时间;H 显示/隐藏文字;Esc 退出;点击/触摸 暂停/继续。
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
        private IHeroSession _session;
        private bool _noHero;
        private bool _heroInput = true;
        private string _heroSpec;
        private bool? _monsters;
        private double _playAtSeconds = -1.0;
        private string _dungeonPath;
        private DungeonBot _bot;
        private bool _botExplore;
        private bool _useBot;
        private int _seed = 1;
        private bool _randomLayout;
        private CombatScript _script = new CombatScript();
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
                    case "-dnfnohero":
                        _noHero = true;
                        break;
                    case "-dnfhero":
                        if (value != null) { _heroSpec = value; i++; }
                        break;
                    case "-dnfautowalk":
                        if (value != null) { _script = CombatScript.Parse(value); i++; }
                        break;
                    case "-dnfmonsters":
                        _monsters = true;
                        break;
                    case "-dnfnomonsters":
                        _monsters = false;
                        break;
                    case "-dnfplayat":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double pa)) { _playAtSeconds = pa; i++; }
                        break;
                    case "-dnfdungeon":
                        if (value != null) { _dungeonPath = value; i++; }
                        break;
                    case "-dnfbot":
                        _useBot = true;
                        break;
                    case "-dnfexplore":
                        _useBot = true;
                        _botExplore = true;
                        break;
                    case "-dnfseed":
                        if (value != null && int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int seed)) { _seed = seed; i++; }
                        break;
                    case "-dnfrandomlayout":
                        _randomLayout = true;
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
                _session = TryCreateSession();
                _view = _session != null
                    ? new TownView(_assets, _session, StageBackground)
                    : new TownView(_assets, mapPath, TownProjection.FromE024, StageBackground);
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
            Debug.Log("DNF_TOWN_DEMO running map=" + (_session != null ? _session.MapPath : mapPath) + " npcs=" + _view.NpcCount + " layers=" + _view.LayerCount + HeroLog());
        }

        /// <summary>
        /// 要不要带主角:-dnfnohero 不带;离屏渲染一帧时默认不带(与 CPU 合成的参照一致),带 -dnfhero 才画;
        /// 其余情况只在量过出生点与镜头范围的地图上带。副本房间(地图里有 [tile])总是造一场房间游戏,
        /// 不带主角时只是不画主角、不读键盘(镜头停在出生点的位置,或 -dnfhero 指定的镜头 x)。
        /// </summary>
        private IHeroSession TryCreateSession()
        {
            if (!string.IsNullOrEmpty(_dungeonPath))
            {
                // 整座副本:自动机(或 -dnfautowalk 的时间表)在离屏渲染前先按固定 1/60 秒步长确定地打到指定时刻,与命令行 dungeon-check 同一局
                bool shot = !string.IsNullOrEmpty(_shotPath);
                DungeonSession dungeon = DungeonSession.Create(_assets, _dungeonPath, _seed, _randomLayout);
                _heroInput = true;
                if (_useBot)
                {
                    _bot = new DungeonBot(_botExplore);
                }
                if (shot && _playAtSeconds >= 0.0)
                {
                    if (_bot != null)
                    {
                        _bot.RunUntil(dungeon, _playAtSeconds * 1000.0);
                    }
                    else
                    {
                        _script.RunUntil(dungeon, _playAtSeconds * 1000.0);
                    }
                }
                return dungeon;
            }
            if (DemoRoom.IsRoomMap(_assets, mapPath))
            {
                bool shot = !string.IsNullOrEmpty(_shotPath);
                bool simulate = shot && _playAtSeconds >= 0.0;
                // 实时运行默认带怪物;离屏渲染一帧默认不带(与 CPU 合成的参照一致),要么带 -dnfmonsters,要么用 -dnfplayat 模拟一段战斗
                bool combat = _monsters ?? (!shot || simulate);
                RoomCombatSession fight = combat ? DemoCombat.CreateSession(_assets, mapPath) : null;
                RoomSession room = fight != null ? fight.Room : DemoRoom.CreateSession(_assets, mapPath);
                room.HeroVisible = !_noHero && (_heroSpec != null || simulate || !shot);
                if (_heroSpec != null)
                {
                    ApplyHeroSpec(room, _heroSpec);
                }
                _heroInput = !_noHero;
                if (fight != null && simulate)
                {
                    // 与 dnf combat-check 同一份模拟:固定 1/60 秒步长,按时间表替主角按键,一直推进到不早于指定时刻的第一步
                    _script.RunUntil(fight, _playAtSeconds * 1000.0);
                }
                if (fight != null)
                {
                    return fight;
                }
                return room;
            }
            _heroInput = true;
            if (_noHero)
            {
                return null;
            }
            bool known = DemoHero.TryGetSlice(mapPath, out TownSlice slice);
            if (_heroSpec == null && (!known || !string.IsNullOrEmpty(_shotPath)))
            {
                return null;
            }
            if (!known)
            {
                slice = DemoHero.SeriaRoom; // 只借用镜头范围;位置由 -dnfhero 指定
            }
            TownSession session = DemoHero.CreateSession(_assets, mapPath, slice);
            if (_heroSpec != null)
            {
                ApplyHeroSpec(session, _heroSpec);
            }
            return session;
        }

        /// <summary>x,y,left|right,rest|move,毫秒[,镜头x]:不带镜头 x 时镜头取"主角居中再夹住"。</summary>
        private static void ApplyHeroSpec(IHeroSession session, string spec)
        {
            string[] p = spec.Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if ((p.Length != 5 && p.Length != 6)
                || !double.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out double x)
                || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out double y)
                || (p[2] != "left" && p[2] != "right") || (p[3] != "rest" && p[3] != "move")
                || !long.TryParse(p[4], out long ms) || ms < 0)
            {
                throw new FormatException("-dnfhero 需要 x,y,left|right,rest|move,毫秒[,镜头x]");
            }
            session.Hero.Place(x, y, p[2] == "left", p[3] == "move" ? HeroMotion.Move : HeroMotion.Rest, ms);
            if (p.Length == 6)
            {
                if (!double.TryParse(p[5], System.Globalization.NumberStyles.Float, inv, out double cam))
                {
                    throw new FormatException("-dnfhero 的镜头 x 不是数字");
                }
                session.Camera.Place(cam);
            }
            else
            {
                session.Camera.Place(x - TownCamera.DefaultCenterX);
            }
        }

        private string HeroLog()
        {
            if (_session == null)
            {
                return " hero=none";
            }
            TownHero h = _session.Hero;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double minX = double.MaxValue;
            foreach (WorldRect r in h.Area)
            {
                minX = Math.Min(minX, r.X);
            }
            return " hero=(" + h.X.ToString("F1", inv) + "," + h.Y.ToString("F1", inv) + ") facing=" + (h.FacingLeft ? "left" : "right")
                + " motion=" + h.Motion + " cameraX=" + _session.Camera.IntX + " leftEdge=" + (h.Area.Count > 0 && h.X <= minX)
                + (IsRoomSession ? " room=true" : "")
                + (_session is RoomCombatSession fight
                    ? " monsters=" + fight.AliveCount + "/" + fight.Monsters.Count + " hits=" + fight.Hits + " kills=" + fight.Kills + " combo=" + fight.Combo
                        + " casts=" + fight.SkillCasts + " stage=" + (h.IsAttacking ? h.AttackStage + 1 : 0)
                    : string.Empty)
                + (_session is DungeonSession d
                    ? " dungeon=" + _dungeonPath + " cell=(" + d.CellX + "," + d.CellY + ") map=" + d.MapPath + " entered=" + d.RoomsEntered + " cleared=" + d.RoomsCleared
                        + " transitions=" + d.Transitions + " bossCleared=" + d.BossCleared + " exitReached=" + d.ExitReached
                        + " monsters=" + d.Current.AliveCount + "/" + d.Current.Monsters.Count + " hits=" + d.Hits + " kills=" + d.Kills + " damage=" + d.TotalDamage
                    : string.Empty);
        }

        /// <summary>副本房间(无论带不带怪物)。</summary>
        private bool IsRoomSession
        {
            get { return _session is RoomSession || _session is RoomCombatSession || _session is DungeonSession; }
        }

        private void SaveShotAndQuit()
        {
            try
            {
                var sprites = _view.ShowAt(_shotTimeMs);
                byte[] rgba = _view.Stage.Capture();
                WritePng(rgba, _shotPath);
                Debug.Log("DNF_TOWN_DEMO shot=" + _shotPath + " t=" + _shotTimeMs + " npcs=" + _view.NpcCount + " layers=" + _view.LayerCount
                    + " sprites=" + sprites.Count + HeroLog());
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
            _session = null;
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
                if (_session != null && !_paused)
                {
                    // 一帧最多推进 100 毫秒:窗口被拖动、打开数据等卡顿之后主角不会一步冲出去
                    double deltaMs = Math.Min(Time.unscaledDeltaTime * 1000.0, 100.0);
                    if (_bot != null && _session is DungeonSession botDungeon)
                    {
                        // 自动机按固定的 1/60 秒"心跳"决策并推进(与命令行 dungeon-check --bot 同一局):帧率抖动、卡顿只会让一帧里多补或少补几个心跳,
                        // 不会改变它的路线。它的走位、出手间隔都是按心跳数写的,按可变的帧长喂给它会在怪物旁来回晃(证据 E-072 的实时运行里出现过)。
                        _bot.Advance(botDungeon, deltaMs);
                    }
                    else
                    {
                        int dirX = 0, dirY = 0;
                        bool attack = false, jump = false;
                        SkillKeys skills = SkillKeys.None;
                        if (_heroInput)
                        {
                            attack = ReadHeroInput(out dirX, out dirY, out jump, out skills);
                        }
                        if (_session is ISkillSession skillSession)
                        {
                            skillSession.Update(deltaMs, dirX, dirY, attack, jump, skills);
                        }
                        else if (_session is IActionSession action)
                        {
                            action.Update(deltaMs, dirX, dirY, attack, jump);
                        }
                        else
                        {
                            _session.Update(deltaMs, dirX, dirY);
                        }
                    }
                }
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
                    + " clockMs=" + (long)_clockMs + " sprites=" + _spriteCount + HeroLog());
                _quitAfterSeconds = -1f;
                Application.Quit(0);
            }
        }

        /// <summary>
        /// 方向键(或 -dnfautowalk 的时间表)当前的状态:-1/0/1;返回这一帧有没有"刚按下攻击键"(X 键,或时间表里到点的 attack),
        /// <paramref name="jump"/> 是这一帧有没有"刚按下跳跃键"(C 键,或时间表里到点的 jump)。
        /// 按原版的默认键位(clientonly/hotkeysystem.co,证据 E-072):X 普通攻击、C 跳跃、A S D 是前三个技能快捷槽(鬼斩、上挑、后跳,证据 E-076);
        /// Z 与空格也是技能槽,但原版里它们绑什么技能没有确认,所以不响应(空格在本演示里是暂停)。
        /// </summary>
        private bool ReadHeroInput(out int dirX, out int dirY, out bool jump, out SkillKeys skills)
        {
            jump = false;
            skills = SkillKeys.None;
            if (!_script.IsEmpty)
            {
                return _script.Poll((Time.realtimeSinceStartup - _runningSince) * 1000.0, out dirX, out dirY, out jump, out skills);
            }
            dirX = 0;
            dirY = 0;
            if (Input.GetKey(KeyCode.LeftArrow)) { dirX -= 1; }
            if (Input.GetKey(KeyCode.RightArrow)) { dirX += 1; }
            if (Input.GetKey(KeyCode.UpArrow)) { dirY -= 1; }
            if (Input.GetKey(KeyCode.DownArrow)) { dirY += 1; }
            jump = Input.GetKeyDown(KeyCode.C);
            if (Input.GetKeyDown(KeyCode.A)) { skills |= SkillKeys.A; }
            if (Input.GetKeyDown(KeyCode.S)) { skills |= SkillKeys.S; }
            if (Input.GetKeyDown(KeyCode.D)) { skills |= SkillKeys.D; }
            return Input.GetKeyDown(KeyCode.X);
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
                        if (_paused || _session == null)
                        {
                            Step(-50.0);
                        }
                        break;
                    case KeyCode.RightArrow:
                        if (_paused || _session == null)
                        {
                            Step(50.0);
                        }
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

        /// <summary>技能快捷槽一行文字:键、技能编号、冷却(没实现的标 n/a)。</summary>
        private static string SkillsText(HeroSkills skills)
        {
            if (skills == null || skills.Slots.Count == 0)
            {
                return string.Empty;
            }
            var sb = new System.Text.StringBuilder("skills");
            foreach (HeroSkillSlot s in skills.Slots)
            {
                sb.Append("  ").Append(s.Key.ToString()).Append(" #").Append(s.SkillId);
                if (!HeroSkills.IsImplemented(s.SkillId))
                {
                    sb.Append(" n/a");
                }
                else if (s.Ready)
                {
                    sb.Append(" ready");
                }
                else
                {
                    sb.Append(' ').Append((s.CooldownLeftMs / 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append('s');
                }
            }
            return sb.Append('\n').ToString();
        }

        private void DrawHud()
        {
            float pad = 12f;
            string hero = "";
            string help;
            if (_session != null)
            {
                TownHero h = _session.Hero;
                hero = "hero (" + ((int)h.X) + ", " + ((int)h.Y) + ")  " + (h.FacingLeft ? "facing left" : "facing right") + "  " + h.Motion
                    + (h.IsAttacking && h.AttackKind == HeroAttackKind.Normal ? " stage " + (h.AttackStage + 1) : string.Empty)
                    + (h.IsAirborne ? "  z " + (int)h.Z : string.Empty)
                    + "  camera x " + _session.Camera.IntX + "\n";
                if (_session is DungeonSession dungeon)
                {
                    hero += "cell (" + dungeon.CellX + ", " + dungeon.CellY + ")   rooms entered " + dungeon.RoomsEntered + "  cleared " + dungeon.RoomsCleared
                        + "/" + (dungeon.Maze.Cols * dungeon.Maze.Rows) + "  moves " + dungeon.Transitions + (dungeon.BossCleared ? "   BOSS ROOM CLEARED" : "") + "\n"
                        + "monsters alive " + dungeon.Current.AliveCount + "/" + dungeon.Current.Monsters.Count + "  hits " + dungeon.Hits + "  kills " + dungeon.Kills
                        + "  damage " + dungeon.TotalDamage + "\n";
                }
                else if (_session is RoomCombatSession fight)
                {
                    hero += "monsters alive " + fight.AliveCount + "/" + fight.Monsters.Count + "  hits " + fight.Hits + "  kills " + fight.Kills
                        + "  combo " + fight.Combo + "  damage " + fight.TotalDamage + "\n";
                }
                if (_session is DungeonSession skillDungeon)
                {
                    hero += SkillsText(skillDungeon.Current.Skills);
                }
                else if (_session is RoomCombatSession skillFight)
                {
                    hero += SkillsText(skillFight.Skills);
                }
                if (_session is DungeonSession)
                {
                    help = (_bot != null ? "Auto-play bot is driving (bot input only)   " : "Arrow keys: walk (double-tap Left/Right: run)   X: attack (again to chain; while running: dash attack)   C: jump (X in the air: jump attack)   A: Hard Attack   S: Upper Slash   D: Back Step   ")
                        + "Space: pause   H: hide text   Esc: quit   (clear a room to open its gates, then walk into a gate; placeholder damage; no MP; monsters do not hurt you; Z/Space not bound)";
                }
                else
                {
                    help = _session is RoomCombatSession
                        ? "Arrow keys: walk (double-tap Left/Right: run)   X: attack (again to chain; while running: dash attack)   C: jump (X in the air: jump attack)   A: Hard Attack   S: Upper Slash   D: Back Step   Space: pause (then Left/Right step 50 ms)   H: hide text   Esc: quit   (placeholder damage; no MP; monsters do not hurt you)"
                        : "Arrow keys: walk   Space: pause (then Left/Right step 50 ms)   H: hide text   Esc: quit   (no UI, no combat)";
                }
            }
            else
            {
                help = "Space: pause   Left/Right: step 50 ms   H: hide text   Esc: quit   (no player character on this map)";
            }
            string shownMap = _session != null ? _session.MapPath : mapPath;
            string text = (_session is DungeonSession ? "DNF dungeon demo (Unity)   room: " : IsRoomSession ? "DNF dungeon room demo (Unity)   map: " : "DNF town demo (Unity)   map: ") + shownMap + "\n"
                + "t = " + ((long)_clockMs) + " ms" + (_paused ? " (paused)" : "") + "    " + _fps.ToString("F0") + " FPS    sprites: " + _spriteCount
                + "    NPCs: " + _view.NpcCount + "    map layers: " + _view.LayerCount + "\n"
                + hero + help;
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
