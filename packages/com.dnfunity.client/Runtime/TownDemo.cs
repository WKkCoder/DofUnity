using System;
using System.Collections;
using System.Collections.Generic;
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
    /// 带 -dnfflow 时玩整条流程(<see cref="GameFlow"/>,证据 E-093):从选角(-dnfflowfrom char,证据 E-096:方向键移动选中、回车或空格开始游戏、Esc 或点"结束游戏"退出,点角色格子选中)、
    /// 城镇(town,默认)、副本选择(select)或副本(dungeon)开始;
    /// 城镇走城镇图册(<see cref="AtlasTownWorld"/>,证据 E-095):登录落在赛丽亚房间、走进地图边缘的出口换区域、艾尔文防线走到地下城入口区域再走进右边的出口打开世界地图,
    /// 换区域与进图停在加载画面(过场图 + 边条 + 标题);-dnflegacytown 退回只有赛丽亚房间一张图的老行为(城镇里按回车打开副本选择的占位),-dnfnosplash 去掉所有加载画面。
    /// -dnftownarea 城镇/区域(例如 1/0)不带流程,直接看城镇图册里的一块区域(主角在登录落脚点,-dnfhero 可改位置;离屏渲染一帧与命令行 town-shot 对拍)。
    /// 选择界面上下键选、回车或空格进入、Esc 回城,
    /// 副本(-dnfdungeon,默认洛兰)打完在结算菜单上选再次挑战、选择其它地下城、返回城镇,各去各的阶段。
    /// 再带 -dnfbot(-dnfexplore)时由 <see cref="FlowBot"/> 替你走完全程,结算选择依次按 -dnfflowchoices(默认 retry,select,town,用完就停);
    /// -dnftimescale N(1 到 6)把每帧的时间步放大,无人值守的检查用它缩短墙钟时间。每次阶段切换在日志里留一行 DNF_FLOW。
    /// 不分发、不复制任何原版内容:数据只在内存里解码。
    ///
    /// 数据目录(里面要有 Script.pvf 与 ImagePacks2)按下面的顺序找:
    ///   1. 命令行 -dnfdir &lt;目录&gt;;2. 环境变量 DNF_DIR;3. 上次在界面里输入并保存的路径(PlayerPrefs);
    ///   4. (仅编辑器)仓库根目录 local.env 里的 DNF_DIR;5. 应用的持久数据目录下的 DNF 文件夹(Android/iOS 把数据放这里)。
    /// 都找不到就显示一个输入框。
    ///
    /// 命令行:-dnfdir &lt;目录&gt; -dnfmap &lt;PVF 内地图路径&gt; [-dnfshot &lt;输出.png&gt; [-dnftime &lt;毫秒&gt;]] [-dnfquitafter &lt;秒&gt;]
    ///         [-dnfnohero] [-dnfhero x,y,left|right,rest|move,毫秒[,镜头x]] [-dnfautowalk 秒:动作,…]
    ///         [-dnfmonsters | -dnfnomonsters] [-dnfnodamage] [-dnfplayat &lt;秒&gt;] [-dnfnosound | -dnfsound]
    ///         [-dnfdungeon &lt;副本定义路径&gt; [-dnfbot | -dnfexplore] [-dnfseed &lt;整数&gt;] [-dnfrandomlayout] [-dnfafterclear &lt;秒&gt;]]
    /// 带 -dnfshot 时只离屏渲染一帧写成 PNG 就退出(用来和 CPU 合成器逐字节对照,见 tools/unity-build-town.sh);
    /// 这时默认不画主角,带 -dnfhero 才把主角摆到指定的位置、朝向与动作进度(与 dnf render-town --scene --hero 同一写法)。
    /// 带 -dnfquitafter 时实时运行指定秒数后退出,并在日志里留一行 DNF_TOWN_DEMO ran …(用来在无窗口的环境里检查实时循环)。
    /// 带 -dnfautowalk 时不读键盘,按"开始运行后第几秒:动作"的时间表替主角按键(方向 left、right、up、down、none,可用 + 组合,再加 attack、jump 表示按一下攻击、跳跃键,a、s、d 表示按一下技能键;
    /// 见 <see cref="CombatScript"/>),用来无人值守地验证走动、撞墙、镜头跟随与挥击(例如 -dnfautowalk 0.5:left,3:none,3.2:attack)。-dnfnohero 完全不带主角。
    /// 副本房间默认带怪物(-dnfnomonsters 去掉);离屏渲染一帧时默认不带(与 CPU 合成的参照一致),带 -dnfmonsters 才画。
    /// 怪物会还手(E-086):主角有生命值、被打中会受击硬直、屏幕左下角有血条;-dnfnodamage 让主角没有生命值、打不到(与命令行 combat-check --no-damage 一致),
    /// 按时刻对拍的回归检查用它,免得还手改变事件的时刻。
    /// 带 -dnfplayat &lt;秒&gt; 与 -dnfshot 时,离屏渲染前先让房间战斗按 -dnfautowalk 的时间表以固定 1/60 秒步长确定地模拟到这一刻(此时自动带怪物与主角),
    /// 与命令行 combat-check 同一份模拟,用来把战斗中的一帧与 CPU 合成逐字节对照(见 tools/unity-build-town.sh)。
    /// 副本同理:-dnfdungeon -dnfbot -dnfplayat &lt;秒&gt; -dnfshot 先让自动机按固定 1/60 秒步长打到这一刻,与命令行 dungeon-check --bot --shots-at 对照;
    /// 再带 -dnfafterclear &lt;秒&gt; 时,副本走完(首领清空)之后主角站着不动、时间再走这么久(结算画面就这样出现),与命令行 --after-clear 同一个规则。
    /// 首领清空 3 秒后出结算菜单(再次挑战 F13、选择其它地下城 F11、返回城镇 F12,笔记本键盘用 1、2、3;或者点菜单行,触摸也行;
    /// 选定时日志里留一行 DNF_SETTLE choice=…,流程由外面接,证据 E-091)。
    /// 按键(副本房间里的主角,按原版默认键位,证据 E-072、E-076):方向键 走动(按住;同方向再按一下接着按住 = 奔跑);X 普攻(连按接下一段);C 跳跃;
    /// A S D 前三个技能快捷槽(新建的剑士是鬼斩、上挑、后跳,各有冷却);Z 与原版的另一个快捷槽没有确认绑什么,不响应。
    /// 空格 暂停/继续;暂停时左右方向键按 50 毫秒步进动画时间;H 显示/隐藏文字;Esc 退出(流程模式的城镇与副本里改为打开系统菜单,见下);点击/触摸 暂停/继续(开着触屏操作层时改用右上角的暂停键)。
    /// 主角死后(副本里,证据 E-115):躺平那一刻起屏幕上方是 10 秒倒计时,攻击键 X 就是使用复活币(原地满血复活、短暂无敌),币用完、到上限或计时走完副本失败、回城镇;-dnfcoins N 指定起始复活币数(默认 5,占位:没有背包),-dnfmonsterdamage N(测试旋钮)让怪物伤害乘上 N 以便无人值守地检查死亡。
    /// 系统菜单(本项目自己的占位设计,原版 Esc 菜单的版式没有读到,<see cref="SystemMenu"/>):流程模式下在城镇与副本里按 Esc 打开,游戏暂停;上下键(或鼠标悬停)选,回车、空格、数字键或点击确定,再按 Esc 关上;副本里是 继续 / 放弃回城 / 退出游戏,城镇里是 继续 / 回选角 / 退出游戏。-dnfmenuat 秒 [-dnfmenuchoose N] 无人值守地走一遍(副本阶段开始后第几秒打开、再过一秒选第 N 项),日志里留 DNF_MENU 行。
    /// 触屏操作层(M13,<see cref="TouchOverlay"/>、<see cref="TouchControls"/>;本项目自己的设计,原版是键盘游戏):手机与平板默认开,桌面带 -dnftouch 开(鼠标左键模拟一根手指)。
    /// 左下按住拖动是浮动摇杆(八向,推到最外圈 = 奔跑),右下是 ATK(普攻)、JMP(跳跃)与 A S D 三个技能键,右上角是暂停键;只在带主角的场景里出现(选角、副本选择、加载、结算菜单不出现,点菜单照旧)。
    /// -dnftouchscript 秒:动作,… 按时间表替手指操作(见 <see cref="TouchScript"/>,例如 0.5:stick=left+run,3:stick=none,3.5:tap=attack),-dnftouchshot &lt;png&gt; [-dnftouchshotat &lt;秒&gt;] 把整个窗口(含控件)存成 PNG,
    /// -dnfnohud 去掉左上角的文字层(手机上默认就没有)。
    /// 声音(<see cref="DnfAudio"/>,证据 E-089、E-090):战斗里有挥击、命中、角色语音、地图音乐与环境声,数据取自用户自备的 audio.xml、SoundPacks 与 Music;
    /// -dnfnosound 关闭;带 -batchmode 的无人值守运行默认静音(整条链路照跑,计数器写进日志的 DNF_AUDIO 行),-dnfsound 才出声;离屏渲染一帧不开声音。
    /// 桌面上画面以整数倍放大并居中(留黑边),所以与原版 800×600 像素一一对应、不糊;手机、平板与开着触屏操作层时改成填满安全区的高度(倍数不是整数,像素略有不均)。
    /// </summary>
    public sealed class TownDemo : MonoBehaviour
    {
        public const string DefaultMap = "map/elvengard/(r)gate.map";
        public const string PrefsKey = "DnfDir";

        /// <summary>-dnfflow 没给 -dnfdungeon 时进的副本。</summary>
        public const string DefaultDungeon = "dungeon/act1/lorien.dgn";

        /// <summary>-dnfflow -dnfbot 没给 -dnfflowchoices 时结算菜单依次做的选择。</summary>
        public const string DefaultFlowChoices = "retry,select,town";

        /// <summary>-dnfflow 没给 -dnfworldmap 时副本选择界面用的世界地图(基线 S05 拍的就是洛兰这张)。</summary>
        public const string DefaultWorldMap = "worldmap/lorien.wdm";

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
        private bool _noDamage;
        private bool _noExp;
        private int _heroLevel = 1;
        private HeroProgress _progress;
        private string _savePath;                          // -dnfsave <路径|none>(M26):存档文件;没给时有窗口的运行存在 persistentDataPath 里,无人值守(-batchmode)的运行不存档
        private bool _heroLevelGiven;                      // 命令行给了 -dnflevel:读档之后等级以命令行为准,并且没有明确的 -dnfsave 时不碰默认存档
        private FileHeroSaveStore _saveStore;
        private HeroSave _loadedSave;
        private HeroSaver _saver;
        private string _saveError;
        private bool _noteSaveLook;
        private int _savedLevel = 1;                       // 选角界面上画的等级:存档里的(没有存档是 1)
        private ReviveCoins _coins;
        private int _coinStock = ReviveCoins.DefaultStock;
        private int _monsterDamageScale = 1;
        private readonly OverlayArt _menuArt = new OverlayArt();
        private bool _menuWasOpen;
        private double _menuProbeAtSeconds = -1.0;
        private int _menuProbeChoice = 2;
        private float _stageRealStart;
        private float _menuProbeOpenedAt = -1f;
        private bool _menuProbeDone;
        private bool _menuPausedIt;
        private bool _bagWasOpen;
        private bool _overlayWasOpen;
        private double _bagProbeAtSeconds = -1.0;
        private float _bagProbeOpenedAt = -1f;
        private bool _bagProbeEquipped;
        private float _bagProbeClosedAt = -1f;
        private bool _bagProbeDone;
        private bool _shopWasOpen;
        private double _shopProbeAtSeconds = -1.0;
        private float _shopProbeOpenedAt = -1f;
        private bool _shopProbeTraded;
        private float _shopProbeClosedAt = -1f;
        private bool _shopProbeDone;
        private int _shopSeenSerial = -1;
        private int _shopTradesLogged;
        private EquipmentStats _bagWorn;
        private int _bagWornVersion = -1;
        private TownHero _bagWornHero;
        private int _noteDeaths;
        private int _noteRevives;
        private int _noteGoldPicked = -1;
        private int _noteItemsPicked = -1;
        private double _playAtSeconds = -1.0;
        private double _afterClearSeconds;
        private SettlementChoice _loggedChoice;
        private string _dungeonPath;
        private bool _flowMode;
        private string _flowFrom = "town";
        private string _flowChoices = DefaultFlowChoices;
        private string _worldMapPath = DefaultWorldMap;
        private bool _legacyTown;
        private bool _noSplash;
        private TownPlace? _townArea;
        private double _timeScale = 1.0;
        private GameFlow _flow;
        private FlowBot _flowBot;
        private int _shownSerial;
        private int _loggedEvents;
        private bool _flowDoneNoted;
        private DungeonBot _bot;
        private bool _botExplore;
        private bool _useBot;
        private int _seed = 1;
        private bool _randomLayout;
        private CombatScript _script = new CombatScript();
        private DnfAudio _audio;
        private bool _noSound;
        private bool _forceSound;
        private Camera _bars;
        private double _clockMs;          // 本帧显示的动画时间(毫秒)
        private double _originReal;       // t = 0 对应的 Time.realtimeSinceStartupAsDouble;动画时间按墙钟走,所以打开数据时的卡顿不会算进去
        private double _pausedAtMs;
        private bool _paused;
        private bool _hud = true;
        private TouchOverlay _touch;
        private bool _touchForced;                    // -dnftouch:桌面上也开触屏操作层(鼠标模拟一根手指)
        private string _touchScriptText;              // -dnftouchscript:按时间表替手指操作
        private string _touchShotPath;                // -dnftouchshot:实时运行到 -dnftouchshotat 秒时把整个窗口(含控件)截成 PNG
        private double _touchShotAtSeconds = 3.0;
        private bool _touchShotStarted;
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
            if (Application.isMobilePlatform)
            {
                _hud = false;                  // 手机上没有 H 键切换文字层,默认不让调试文字盖住画面
            }
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
                    case "-dnfnodamage":
                        _noDamage = true;
                        break;
                    case "-dnfnoexp":
                        _noExp = true;
                        break;
                    case "-dnflevel":
                        if (value != null && int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int heroLevel) && heroLevel >= 1 && heroLevel <= 85) { _heroLevel = heroLevel; _heroLevelGiven = true; i++; }
                        break;
                    case "-dnfsave":
                        if (value != null) { _savePath = value; i++; }
                        break;
                    case "-dnfcoins":
                        if (value != null && int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int stock) && stock >= 0 && stock <= 99) { _coinStock = stock; i++; }
                        break;
                    case "-dnfmonsterdamage":
                        if (value != null && int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int mds) && mds >= 1 && mds <= 100000) { _monsterDamageScale = mds; i++; }
                        break;
                    case "-dnfmenuat":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double mat) && mat >= 0.0) { _menuProbeAtSeconds = mat; i++; }
                        break;
                    case "-dnfmenuchoose":
                        if (value != null && int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int mch) && mch >= 1 && mch <= 9) { _menuProbeChoice = mch; i++; }
                        break;
                    case "-dnfbagat":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double bat) && bat >= 0.0) { _bagProbeAtSeconds = bat; i++; }
                        break;
                    case "-dnfshopat":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double sat) && sat >= 0.0) { _shopProbeAtSeconds = sat; i++; }
                        break;
                    case "-dnfplayat":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double pa)) { _playAtSeconds = pa; i++; }
                        break;
                    case "-dnfafterclear":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double ac)) { _afterClearSeconds = ac; i++; }
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
                    case "-dnfnosound":
                        _noSound = true;
                        break;
                    case "-dnfsound":
                        _forceSound = true;
                        break;
                    case "-dnfflow":
                        _flowMode = true;
                        break;
                    case "-dnfflowfrom":
                        if (value != null && (value == "char" || value == "town" || value == "select" || value == "dungeon")) { _flowFrom = value; i++; }
                        break;
                    case "-dnfflowchoices":
                        if (value != null) { _flowChoices = value; i++; }
                        break;
                    case "-dnfworldmap":
                        if (value != null) { _worldMapPath = value; i++; }
                        break;
                    case "-dnflegacytown":
                        _legacyTown = true;
                        break;
                    case "-dnfnosplash":
                        _noSplash = true;
                        break;
                    case "-dnftownarea":
                        if (value != null) { _townArea = ParseTownArea(value); i++; }
                        break;
                    case "-dnftimescale":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double ts)) { _timeScale = Math.Max(1.0, Math.Min(6.0, ts)); i++; }
                        break;
                    case "-dnfquitafter":
                        if (value != null && float.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float q)) { _quitAfterSeconds = q; i++; }
                        break;
                    case "-dnftouch":
                        _touchForced = true;
                        break;
                    case "-dnftouchscript":
                        if (value != null) { TouchScript.Parse(value); _touchScriptText = value; _touchForced = true; i++; }   // 先解析一遍:写错了在启动时就报错
                        break;
                    case "-dnftouchshot":
                        if (value != null) { _touchShotPath = value; _touchForced = true; i++; }
                        break;
                    case "-dnftouchshotat":
                        if (value != null && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double tsa)) { _touchShotAtSeconds = tsa; i++; }
                        break;
                    case "-dnfnohud":
                        _hud = false;
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
                if (_flowMode)
                {
                    StartFlow();
                    _session = _flow.Session;
                    _view = new TownView(_assets, _flow.Scene, StageBackground);
                    _shownSerial = _flow.StageSerial;
                    LogNewFlowEvents();
                }
                else
                {
                    _session = TryCreateSession();
                    _view = _session != null
                        ? new TownView(_assets, _session, StageBackground)
                        : new TownView(_assets, mapPath, TownProjection.FromE024, StageBackground);
                }
                _audio = TryCreateAudio(dir);
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
            CreateTouch();
            Debug.Log("DNF_TOWN_DEMO running map=" + (_session != null ? _session.MapPath : mapPath) + " npcs=" + _view.NpcCount + " layers=" + _view.LayerCount + HeroLog());
        }

        /// <summary>
        /// 声音只在实时运行、会话会发声音提示(房间战斗、整座副本)时才开;离屏渲染一帧不开,-dnfnosound 也不开。
        /// 带 -batchmode 的无人值守运行默认静音(整条链路照跑、计数器照记),带 -dnfsound 才出声。数据目录里没有 audio.xml 时安静地没有声音。
        /// </summary>
        private DnfAudio TryCreateAudio(string dir)
        {
            if (_noSound || !string.IsNullOrEmpty(_shotPath) || (!_flowMode && !(_session is ISoundSource)))
            {
                return null;
            }
            try
            {
                DnfAudio audio = DnfAudio.Open(dir, audible: !Application.isBatchMode || _forceSound);
                Debug.Log(audio == null
                    ? "DNF_AUDIO silent: no audio.xml in the data folder"
                    : "DNF_AUDIO ready tags=" + audio.Library.Catalog.Count + " packed=" + audio.Library.PackedCount + " loose=" + audio.Library.LooseCount + " audible=" + audio.Audible);
                return audio;
            }
            catch (Exception e)
            {
                Debug.LogWarning("DNF_AUDIO disabled: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
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
                return CreateDungeon(_dungeonPath, _seed);
            }
            if (_townArea.HasValue)
            {
                // 城镇图册里的一块区域(E-095):按登录落脚点出生的主角,房间式合成;-dnfhero 可以改主角与镜头的位置
                TownSession area = TownWorld.CreateSession(_assets, TownAtlas.Open(_assets), _townArea.Value, DemoHero.CreateOutfit(_assets));
                if (_heroSpec != null)
                {
                    ApplyHeroSpec(area, _heroSpec);
                }
                _heroInput = !_noHero;
                return area;
            }
            if (DemoRoom.IsRoomMap(_assets, mapPath))
            {
                bool shot = !string.IsNullOrEmpty(_shotPath);
                bool simulate = shot && _playAtSeconds >= 0.0;
                // 实时运行默认带怪物;离屏渲染一帧默认不带(与 CPU 合成的参照一致),要么带 -dnfmonsters,要么用 -dnfplayat 模拟一段战斗
                bool combat = _monsters ?? (!shot || simulate);
                RoomCombatSession fight = combat ? DemoCombat.CreateSession(_assets, mapPath, DemoHero.StarterWeapon, 1, !_noDamage, HeroProgressForRun()) : null;
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

        /// <summary>
        /// 整座副本:自动机(或 -dnfautowalk 的时间表)在离屏渲染前先按固定 1/60 秒步长确定地打到指定时刻,与命令行 dungeon-check 同一局。
        /// 流程模式下(<see cref="GameFlow"/>)自动机由 <see cref="FlowBot"/> 管,这里不留。
        /// </summary>
        private DungeonSession CreateDungeon(string path, int seed)
        {
            bool shot = !string.IsNullOrEmpty(_shotPath);
            DungeonSession dungeon = DungeonSession.Create(_assets, path, seed, _randomLayout, heroVitals: !_noDamage, progress: HeroProgressForRun(),
                coins: CoinsForRun(), monsterDamageScale: _monsterDamageScale);
            _heroInput = true;
            if (_useBot && !_flowMode)
            {
                _bot = new DungeonBot(_botExplore);
            }
            if (shot && _playAtSeconds >= 0.0)
            {
                DungeonBot shotBot = _bot ?? (_useBot ? new DungeonBot(_botExplore) : null);
                if (shotBot != null)
                {
                    shotBot.RunUntil(dungeon, _playAtSeconds * 1000.0, _afterClearSeconds * 1000.0);
                }
                else
                {
                    _script.RunUntil(dungeon, _playAtSeconds * 1000.0);
                }
            }
            return dungeon;
        }

        /// <summary>
        /// 主角的等级进度(证据 E-111):整次运行只造一个(流程里几局副本之间等级与经验留着);带 -dnfnoexp 或 -dnfnodamage(主角没有生命值与底栏)时没有,
        /// 与命令行 ExpCheck.ProgressFor 同一套条件,所以离屏渲染的画面与命令行逐字节对得上。-dnflevel 指定起始等级。
        /// </summary>
        private HeroProgress HeroProgressForRun()
        {
            if (_noExp || _noDamage)
            {
                return null;
            }
            if (_progress == null)
            {
                _progress = HeroProgress.CreateForLocalHero(_assets, _heroLevel);
                RestoreSave();
            }
            return _progress;
        }

        /// <summary>
        /// 存档(M26,本项目自己的设计):<c>-dnfsave 路径</c> 指定存档文件,<c>none</c> 不存;没给时有窗口的运行存在 persistentDataPath 的 hero.sav,
        /// 无人值守(-batchmode)的运行与带 -dnflevel 的测试运行不存(免得把真存档弄脏)。在选角界面之前读,选角界面上画存档里的等级。
        /// </summary>
        private void OpenSave()
        {
            string path = _savePath;
            if (path == null && !Application.isBatchMode && !_heroLevelGiven)
            {
                path = Path.Combine(Application.persistentDataPath, "hero.sav");
            }
            if (string.IsNullOrEmpty(path) || string.Equals(path, "none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _saveStore = new FileHeroSaveStore(path);
            _loadedSave = _saveStore.Load(out string note);
            _savedLevel = _loadedSave != null ? _loadedSave.Level : 1;
            Debug.Log("DNF_SAVE open file=" + Path.GetFileName(path) + " found=" + (_loadedSave != null) + (_loadedSave != null ? " level=" + _loadedSave.Level : string.Empty));
            if (note != null)
            {
                Debug.Log("DNF_SAVE problem " + note);
            }
        }

        /// <summary>等级进度造好之后把存档还原上去,再交给存档员看着;还原时丢了东西(物品编号在当前数据里找不到)就先把原存档备份成 .dropped,免得被少了东西的新存档覆盖。</summary>
        private void RestoreSave()
        {
            if (_saveStore == null || _progress == null)
            {
                return;
            }
            if (_loadedSave != null)
            {
                SaveApplyReport report = _progress.RestoreFromData(_loadedSave, _assets);
                if (_heroLevelGiven)
                {
                    _progress.SetLevel(_heroLevel);
                }
                Debug.Log("DNF_SAVE loaded level=" + _progress.Level + " exp=" + _progress.Exp + " gold=" + _progress.Gold + " bag=" + _progress.BagItems
                    + " equipped=" + _progress.EquippedCount + " stacks=" + report.BagStacks + " moved=" + report.Moved + " dropped=" + report.Dropped);
                if (report.Dropped > 0)
                {
                    BackupSave(".dropped");
                }
            }
            _saver = new HeroSaver(_saveStore, _progress, _loadedSave);
        }

        private void BackupSave(string suffix)
        {
            try
            {
                string backup = _saveStore.Path + suffix;
                if (File.Exists(_saveStore.Path) && !File.Exists(backup))
                {
                    File.Copy(_saveStore.Path, backup);
                    Debug.Log("DNF_SAVE backup " + Path.GetFileName(backup));
                }
            }
            catch (IOException e)
            {
                Debug.Log("DNF_SAVE problem backup failed: " + e.Message);
            }
        }

        /// <summary>
        /// 存一次档(<paramref name="reason"/> 写进日志;auto 是每帧的定时检查,别的是立刻存):内容没变不写,写了留一行 DNF_SAVE saved,写失败留一行 DNF_SAVE problem(同一个原因只写一次)。
        /// </summary>
        private void FlushSave(string reason)
        {
            if (_saver == null)
            {
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            bool wrote = reason == "auto" ? _saver.Tick(now) : _saver.Flush(now);
            if (wrote)
            {
                _saveError = null;
                Debug.Log("DNF_SAVE saved reason=" + reason + " level=" + _progress.Level + " exp=" + _progress.Exp + " gold=" + _progress.Gold + " bag=" + _progress.BagItems
                    + " equipped=" + _progress.EquippedCount + " saves=" + _saver.Saves);
            }
            else if (_saver.LastError != null && _saver.LastError != _saveError)
            {
                _saveError = _saver.LastError;
                Debug.Log("DNF_SAVE problem " + _saver.LastError);
            }
        }

        private void OnApplicationQuit()
        {
            FlushSave("quit");
        }

        /// <summary>
        /// 复活币库存(证据 E-115):整次运行一个(流程里几局副本之间币数留着;每局副本的"已用几次"在副本里重新数)。
        /// 占位:没有背包,起始库存是 <see cref="ReviveCoins.DefaultStock"/> 个,-dnfcoins N 指定(0 就是死了只能等失败回城)。
        /// </summary>
        private ReviveCoins CoinsForRun()
        {
            if (_coins == null)
            {
                _coins = new ReviveCoins(_coinStock);
            }
            return _coins;
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

        /// <summary>日志里的英雄一段 + 流程模式下的流程一段。</summary>
        private string HeroLog()
        {
            return HeroOnlyLog() + FlowLog();
        }

        private string FlowLog()
        {
            if (_flow == null)
            {
                return string.Empty;
            }
            return " flow=" + _flow.Stage + " serial=" + _flow.StageSerial + " runs=" + _flow.Runs + " settlements=" + _flow.Settlements
                + (_flowBot != null ? " botDone=" + _flowBot.Done + " choicesMade=" + _flowBot.ChoicesMade : string.Empty)
                + " failures=" + _flow.Failures + " giveups=" + _flow.GiveUps;
        }

        private string HeroOnlyLog()
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
                        + (fight.Vitals != null ? " heroHits=" + fight.HeroHits + " heroHp=" + fight.Vitals.Hp + "/" + fight.Vitals.HpMax + " heroMp=" + fight.Vitals.Mp + "/" + fight.Vitals.MpMax : string.Empty)
                        + (fight.Progress != null ? " heroLevel=" + fight.Progress.Level + " heroExp=" + fight.Progress.Exp + " levelUps=" + fight.Progress.LevelUps : string.Empty)
                    : string.Empty)
                + (_session is DungeonSession d
                    ? " dungeon=" + _dungeonPath + " cell=(" + d.CellX + "," + d.CellY + ") map=" + d.MapPath + " entered=" + d.RoomsEntered + " cleared=" + d.RoomsCleared
                        + " transitions=" + d.Transitions + " bossCleared=" + d.BossCleared + " exitReached=" + d.ExitReached
                        + " settlement=" + d.SettlementActive + " camp=" + (d.Camp != null ? d.Camp.FireLayers + "/" + d.Camp.NpcCount : "none") + " choice=" + d.Choice
                        + " monsters=" + d.Current.AliveCount + "/" + d.Current.Monsters.Count + " hits=" + d.Hits + " kills=" + d.Kills + " damage=" + d.TotalDamage
                        + (d.Vitals != null ? " heroHits=" + d.HeroHits + " heroHp=" + d.Vitals.Hp + "/" + d.Vitals.HpMax + " heroMp=" + d.Vitals.Mp + "/" + d.Vitals.MpMax : string.Empty)
                        + (d.Progress != null ? " heroLevel=" + d.Progress.Level + " heroExp=" + d.Progress.Exp + " levelUps=" + d.Progress.LevelUps : string.Empty)
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
                _flow?.Select?.Advance(_shotTimeMs);      // 副本选择界面有自己的时钟(开场动画、横幅轮换):走到 -dnftime 那一刻再画
                _flow?.CharacterSelect?.Advance(_shotTimeMs);   // 选角界面同理(光束与头像的动画)
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
            if (_audio != null)
            {
                UnityObjects.Destroy(_audio.gameObject);
                _audio = null;
            }
            _view?.Dispose();
            _view = null;
            _session = null;
            _flow = null;
            _flowBot = null;
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
            PumpTouch();
            PumpMenu();
            _clockMs = NowMs();
            try
            {
                if (_flow != null)
                {
                    if (!_paused)
                    {
                        StepFlow(Math.Min(Time.unscaledDeltaTime * 1000.0 * _timeScale, 100.0));
                    }
                }
                else if (_session != null && !_paused)
                {
                    // 一帧最多推进 100 毫秒:窗口被拖动、打开数据等卡顿之后主角不会一步冲出去
                    double deltaMs = Math.Min(Time.unscaledDeltaTime * 1000.0 * _timeScale, 100.0);
                    StepSession(deltaMs);
                    // 声音:取走这一步发出的提示、推进调度器、执行命令(不碰画面与游戏状态)
                    _audio?.Tick(deltaMs, _session as ISoundSource);
                    UpdateSettlement();
                }
                _spriteCount = _view.ShowAt((long)_clockMs).Count;
                _frames++;
            }
            catch (Exception e)
            {
                Fail(e);
                return;
            }
            if (_flowBot != null && _flowBot.Done && _quitAfterSeconds >= 0f && !_flowDoneNoted)
            {
                // 无人值守的流程走完了:再留一秒让最后的日志与声音计数落定,然后照常收尾退出(-dnfquitafter 只是上限)
                _flowDoneNoted = true;
                _quitAfterSeconds = Mathf.Min(_quitAfterSeconds, Time.realtimeSinceStartup - _runningSince + 1f);
            }
            if (_quitAfterSeconds >= 0f && Time.realtimeSinceStartup - _runningSince >= _quitAfterSeconds)
            {
                float seconds = Time.realtimeSinceStartup - _runningSince;
                Debug.Log("DNF_TOWN_DEMO ran frames=" + _frames + " seconds=" + seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    + " clockMs=" + (long)_clockMs + " sprites=" + _spriteCount + HeroLog());
                if (_audio != null)
                {
                    Debug.Log("DNF_AUDIO " + _audio.Summary());
                }
                _quitAfterSeconds = -1f;
                Application.Quit(0);
            }
        }

        /// <summary>
        /// 系统菜单(Esc;<see cref="SystemMenu"/>)的宿主部分:菜单开着时游戏暂停(合上或选定后恢复,选定的项在下一次流程轮询里执行),鼠标悬停选行,
        /// 带 -dnfmenuat 时无人值守地走一遍同样的路(阶段开始后第几秒打开、再过一秒选第几项),日志里留 DNF_MENU 行。
        /// </summary>
        private void PumpMenu()
        {
            if (_flow == null)
            {
                return;
            }
            RunMenuProbe();
            RunBagProbe();
            RunShopProbe();
            NoteTownShops();
            bool menuOpen = _flow.Menu.Open;
            bool bagOpen = _flow.Bag.Open;
            bool shopOpen = _flow.Shop.Open;
            if (menuOpen != _menuWasOpen)
            {
                _menuWasOpen = menuOpen;
                Debug.Log("DNF_MENU " + (menuOpen ? "open stage=" + _flow.Stage : "closed") + " paused=" + _paused);
            }
            string bagEvent = null;                      // 背包开关的日志放在暂停与恢复之后打,paused 才是开关之后的状态
            if (bagOpen != _bagWasOpen)
            {
                _bagWasOpen = bagOpen;
                bagEvent = bagOpen ? "open stage=" + _flow.Stage : "closed";
            }
            string shopEvent = null;                     // 商店窗口同理
            if (shopOpen != _shopWasOpen)
            {
                _shopWasOpen = shopOpen;
                shopEvent = shopOpen ? "open shop=" + _flow.Shop.ShopIndex + " items=" + _flow.Shop.ShopItemCount : "closed";
            }
            bool open = menuOpen || bagOpen || shopOpen; // 系统菜单、背包窗口与商店窗口开着都暂停游戏
            if (open != _overlayWasOpen)
            {
                _overlayWasOpen = open;
                if (open)
                {
                    _menuPausedIt = !_paused;        // 已经被空格暂停着就不碰:合上菜单也不替人恢复
                    if (_menuPausedIt)
                    {
                        TogglePause();
                    }
                }
                else if (_menuPausedIt)
                {
                    _menuPausedIt = false;
                    if (_paused)
                    {
                        TogglePause();
                    }
                }
            }
            if (bagEvent != null)
            {
                Debug.Log("DNF_BAG " + bagEvent + " paused=" + _paused
                    + " equipped=" + (_progress == null ? 0 : _progress.EquippedCount) + " bag=" + (_progress == null ? 0 : _progress.BagItems));
            }
            if (shopEvent != null)
            {
                Debug.Log("DNF_SHOP " + shopEvent + " paused=" + _paused + " gold=" + (_progress == null ? 0L : _progress.Gold)
                    + " bag=" + (_progress == null ? 0 : _progress.BagItems));
            }
            if (menuOpen && TryGetCanvasPoint(Input.mousePosition, out int mx, out int my))
            {
                _flow.Menu.SetPointer(mx, my);
            }
        }

        /// <summary>-dnfmenuat 的无人值守探针:副本阶段开始后过了指定秒数就像按 Esc 一样打开菜单,再过一秒按数字键选一项(各只做一次)。</summary>
        private void RunMenuProbe()
        {
            if (_menuProbeAtSeconds < 0.0 || _menuProbeDone || _flow == null)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (_menuProbeOpenedAt < 0f)
            {
                if (_flow.Stage == GameStage.Dungeon && now - _stageRealStart >= _menuProbeAtSeconds && _flow.ToggleMenu())
                {
                    _menuProbeOpenedAt = now;
                    Debug.Log("DNF_MENU probe open items=" + _flow.Menu.Items.Count);
                }
            }
            else if (now - _menuProbeOpenedAt >= 1f)
            {
                _menuProbeDone = true;
                Debug.Log("DNF_MENU probe choose " + _menuProbeChoice);
                _flow.Menu.ChooseNumber(_menuProbeChoice);
            }
        }

        /// <summary>
        /// -dnfbagat 的无人值守探针(背包窗口,证据 E-118):在副本阶段开始后过了指定秒数,先往背包里放一件穿得上的武器(<see cref="HeroLook.GrantWearableWeapon"/>),
        /// 像按 I 键一样打开背包,再过一秒按回车试穿(光标走过装备页每一格),再过一秒关上;日志里留 DNF_BAG probe 行(各只做一次)。
        /// </summary>
        private void RunBagProbe()
        {
            if (_bagProbeAtSeconds < 0.0 || _bagProbeDone || _flow == null)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (_bagProbeOpenedAt < 0f)
            {
                if (_flow.Stage == GameStage.Dungeon && now - _stageRealStart >= _bagProbeAtSeconds && HeroProgressForRun() != null)
                {
                    bool granted = HeroLook.GrantWearableWeapon(_assets, _progress);
                    if (_flow.ToggleBag())
                    {
                        _bagProbeOpenedAt = now;
                        Debug.Log("DNF_BAG probe open granted=" + granted + " items=" + _progress.BagItems + " slots=" + _progress.BagSlots);
                    }
                }
            }
            else if (!_bagProbeEquipped && now - _bagProbeOpenedAt >= 1f)
            {
                _bagProbeEquipped = true;
                bool ok = _flow.Bag.TryEquipAny(_progress, out int tried);
                Debug.Log("DNF_BAG probe equip ok=" + ok + " tried=" + tried + " equipped=" + _progress.EquippedCount + " weapon=" + (_progress.WeaponPath != null)
                    + " msg=\"" + _flow.Bag.Message + "\"" + " changes=" + _flow.Bag.Changes);
            }
            else if (_bagProbeEquipped && _bagProbeClosedAt < 0f && now - _bagProbeOpenedAt >= 2f)
            {
                _bagProbeClosedAt = now;
                _flow.ToggleBag();
                Debug.Log("DNF_BAG probe closed equipped=" + _progress.EquippedCount);
            }
            else if (_bagProbeClosedAt >= 0f && now - _bagProbeClosedAt >= 1f)
            {
                // 关上背包、游戏继续跑了一秒:战斗会话应已按装备栏换了主角的武器外观
                _bagProbeDone = true;
                Debug.Log("DNF_BAG probe look matches=" + HeroLook.Matches(_flow.Session?.Hero, _progress) + " weapon=" + (_progress.WeaponPath != null));
            }
        }

        /// <summary>探针给钱包放的一笔钱(够买得起几件常见商品,只在 -dnfshopat 的无人值守运行里用)。</summary>
        private const long ShopProbeGold = 50000;

        /// <summary>
        /// -dnfshopat 的无人值守探针(NPC 商店,证据 E-119):城镇阶段开始后过了指定秒数,先给钱包放一笔钱,像走到商店 NPC 旁按空格一样打开第一家有商品的店
        /// (<see cref="ShopWindow.FirstStockedShop"/>),再过一秒无人值守地买一件、再卖回去(<see cref="ShopWindow.TryTradeAny"/>),再过一秒按 Esc 的路关上,
        /// 再过一秒收尾;日志里留 DNF_SHOP probe 行(各只做一次)。
        /// </summary>
        private void RunShopProbe()
        {
            if (_shopProbeAtSeconds < 0.0 || _shopProbeDone || _flow == null)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (_shopProbeOpenedAt < 0f)
            {
                if (_flow.Stage == GameStage.Town && now - _stageRealStart >= _shopProbeAtSeconds && HeroProgressForRun() != null)
                {
                    _progress.AddGold(ShopProbeGold);
                    int index = _flow.Shop.FirstStockedShop();
                    if (index >= 0 && _flow.OpenShop(index))
                    {
                        _shopProbeOpenedAt = now;
                        Debug.Log("DNF_SHOP probe open shop=" + index + " items=" + _flow.Shop.ShopItemCount + " tabs=" + _flow.Shop.TabCount + " gold=" + _progress.Gold);
                    }
                    else
                    {
                        _shopProbeDone = true;
                        Debug.Log("DNF_SHOP probe failed shop=" + index);
                    }
                }
            }
            else if (!_shopProbeTraded && now - _shopProbeOpenedAt >= 1f)
            {
                _shopProbeTraded = true;
                long before = _progress.Gold;
                (bool bought, bool sold) = _flow.Shop.TryTradeAny(_progress, out int tried);
                ShopWindow shop = _flow.Shop;
                Debug.Log("DNF_SHOP probe trade bought=" + bought + " sold=" + sold + " tried=" + tried + " purchases=" + shop.Purchases + " sales=" + shop.Sales
                    + " spent=" + shop.Spent + " earned=" + shop.Earned + " net=" + (_progress.Gold - before) + " bag=" + _progress.BagItems + " msg=\"" + shop.Message + "\"");
                _shopTradesLogged = shop.Purchases + shop.Sales;
            }
            else if (_shopProbeTraded && _shopProbeClosedAt < 0f && now - _shopProbeOpenedAt >= 2f)
            {
                _shopProbeClosedAt = now;
                _flow.ToggleMenu();                  // Esc 的路:开着商店时先关商店
                Debug.Log("DNF_SHOP probe closed open=" + _flow.Shop.Open);
            }
            else if (_shopProbeClosedAt >= 0f && now - _shopProbeClosedAt >= 1f)
            {
                _shopProbeDone = true;
                Debug.Log("DNF_SHOP probe done gold=" + _progress.Gold + " bag=" + _progress.BagItems);
            }
        }

        /// <summary>进了一个新的城镇阶段就记一行日志:这座城镇摆着几个 NPC、其中几个开商店(同时把每个 NPC 的商店编号读进缓存,走近时不再卡一下)。</summary>
        private void NoteTownShops()
        {
            if (_flow == null || _flow.Stage != GameStage.Town || _flow.StageSerial == _shopSeenSerial)
            {
                return;
            }
            _shopSeenSerial = _flow.StageSerial;
            int shopNpcs = _flow.CountShopNpcs(out int npcs);
            Debug.Log("DNF_SHOP town serial=" + _flow.StageSerial + " npcs=" + npcs + " shopnpcs=" + shopNpcs);
        }

        /// <summary>当前要叠在画面上的窗口(商店、背包或系统菜单,没开返回 null;城镇里走到商店 NPC 旁时叠一行提示);挂在 <see cref="TownView.Overlay"/> 上。</summary>
        private List<SceneSprite> BuildMenuOverlay()
        {
            if (_flow == null)
            {
                return null;
            }
            if (_flow.Shop.Open)
            {
                return _progress == null ? null : ShopWindowArt.Build(_flow.Shop, _progress, _assets, _menuArt);
            }
            if (_flow.Bag.Open)
            {
                return _progress == null ? null : BagWindowArt.Build(_flow.Bag, _progress, _assets, _menuArt, BagWornStats());
            }
            if (!_flow.Menu.Open)
            {
                if (_progress != null && !_paused && _flow.Stage == GameStage.Town && _flow.FindNearbyShop() >= 0)
                {
                    return ShopWindowArt.BuildNearHint(_menuArt);
                }
                return null;
            }
            return SystemMenuArt.Build(_flow.Menu, _menuArt);
        }

        /// <summary>背包窗口上显示的"穿着装备的攻击加成":按当前主角与装备栏算(装备栏变了或换了主角才重算,不是每帧读脚本)。</summary>
        private EquipmentStats BagWornStats()
        {
            TownHero hero = _flow.Session?.Hero;
            if (_bagWornVersion != _progress.EquipVersion || !ReferenceEquals(_bagWornHero, hero))
            {
                _bagWornVersion = _progress.EquipVersion;
                _bagWornHero = hero;
                _bagWorn = HeroPanel.WornEquipment(_assets, hero, _progress);
            }
            return _bagWorn;
        }

        /// <summary>
        /// 背包里穿脱了装备:城镇里的主角立刻换上装备栏里的武器(外观是会话外的共用状态,城镇会话没有自己的同步);
        /// 副本里的战斗会话下一步更新时自己同步(换外观、换武器常量、重算攻击面板),所以这里只管城镇。
        /// </summary>
        private void SyncHeroLook()
        {
            if (_flow == null || _progress == null || _flow.Stage != GameStage.Town)
            {
                return;
            }
            HeroLook.Apply(_assets, _flow.Session?.Hero, _progress);
        }

        /// <summary>
        /// 把当前的会话推进 <paramref name="deltaMs"/> 毫秒:自动机在打副本就让它按固定心跳决策并推进,否则读键盘(或 -dnfautowalk 的时间表)替主角操作。
        /// </summary>
        private void StepSession(double deltaMs)
        {
            if (_bot != null && _session is DungeonSession botDungeon)
            {
                // 自动机按固定的 1/60 秒"心跳"决策并推进(与命令行 dungeon-check --bot 同一局):帧率抖动、卡顿只会让一帧里多补或少补几个心跳,
                // 不会改变它的路线。它的走位、出手间隔都是按心跳数写的,按可变的帧长喂给它会在怪物旁来回晃(证据 E-072 的实时运行里出现过)。
                if (botDungeon.Finished)
                {
                    // 副本走完(首领清空):主角站着不动,只让时间走,结算画面随之出现;自动机不再决策
                    botDungeon.Update(deltaMs, 0, 0);
                }
                else
                {
                    _bot.Advance(botDungeon, deltaMs);
                }
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

        /// <summary>
        /// 流程模式(<see cref="GameFlow"/>)下推进一帧:流程自动机在就让它推进当前阶段并做决定,否则按人的操作推进当前阶段的会话;
        /// 然后应用阶段切换(换视图、停声音、写日志),最后给声音调度器与结算菜单的悬停更新。
        /// </summary>
        private void StepFlow(double deltaMs)
        {
            if (_flowBot != null)
            {
                _flowBot.Advance(_flow, deltaMs);
            }
            else
            {
                if (_session != null)
                {
                    StepSession(deltaMs);
                }
                _flow.Select?.Advance(deltaMs);      // 副本选择界面自己的时钟(开场动画与横幅轮换)
                _flow.CharacterSelect?.Advance(deltaMs);   // 选角界面自己的时钟(光束与头像的动画)
                _flow.Splash?.Advance(deltaMs);      // 加载画面:停够时间之后流程切到目标阶段
                _flow.Poll();
            }
            if (_flow.StageSerial != _shownSerial)
            {
                SwitchView();
            }
            NoteRevive();
            NoteGold();
            FlushSave("auto");                       // 隔十秒查一次有没有变(捡了东西、升了级),变了才写
            if (_flow.CharacterSelect != null && _flow.CharacterSelect.Outcome == CharacterSelectOutcome.Quit)
            {
                Debug.Log("DNF_FLOW quit from character select");
                Quit();                              // 选角界面上的"结束游戏"(或 Esc):流程不动,宿主退出
                return;
            }
            if (_flow.QuitRequested)
            {
                Debug.Log("DNF_FLOW quit from system menu");
                Quit();                              // 系统菜单里选了"退出游戏"
                return;
            }
            _audio?.Tick(deltaMs, _flow);          // 流程自己是声音源:选角与城镇区域的音乐、结算曲子,加上当前副本会话的提示(E-097)
            UpdateSettlement();
        }

        /// <summary>
        /// 流程换了阶段:用新阶段的内容造视图、销毁旧视图,停掉旧阶段的声音(新副本的第一个房间提示会重新起音乐)。
        /// </summary>
        private void SwitchView()
        {
            TownView previous = _view;
            _session = _flow.Session;
            if (!_noteSaveLook && _loadedSave != null && _progress != null && _flow.Stage == GameStage.Town)
            {
                // 读了存档之后第一次进城镇:主角的外观应已是存档里穿着的武器(日志留一行,无人值守的检查看它)
                _noteSaveLook = true;
                Debug.Log("DNF_SAVE look matches=" + HeroLook.Matches(_flow.Session?.Hero, _progress) + " weapon=" + (_progress.WeaponPath != null));
            }
            _view = new TownView(_assets, _flow.Scene, StageBackground);
            _view.Overlay = BuildMenuOverlay;        // 系统菜单叠在每个阶段的画面之上(没开时不叠)
            _stageRealStart = Time.realtimeSinceStartup;
            previous?.Dispose();
            _shownSerial = _flow.StageSerial;
            FlushSave("stage");                      // 每次阶段切换(副本打完回城、结算、换区域)都存一次
            _loggedChoice = SettlementChoice.None;
            _noteDeaths = 0;
            _noteRevives = 0;
            _noteGoldPicked = -1;
            _noteItemsPicked = -1;
            if (!_flow.LastSwitchStaysInTown)
            {
                _audio?.StopAll();                   // 城镇换区域时不停:相同的音乐标签接着放,只换环境声(E-097)
            }
            LogNewFlowEvents();
        }

        private void LogNewFlowEvents()
        {
            while (_loggedEvents < _flow.History.Count)
            {
                FlowEvent e = _flow.History[_loggedEvents++];
                Debug.Log("DNF_FLOW serial=" + e.Serial + " from=" + e.From + " to=" + e.To + " reason=\"" + e.Reason + "\" runs=" + _flow.Runs
                    + " settlements=" + _flow.Settlements + (e.To == GameStage.Dungeon ? " dungeon=" + _flow.DungeonPath : string.Empty)
                    + " failures=" + _flow.Failures + " giveups=" + _flow.GiveUps);
            }
        }

        /// <summary>副本里主角死亡或复活的次数变了就写一行 DNF_REVIVE(死了几次、复活了几次、复活币还剩几枚),无人值守的检查靠它确认复活真的发生。</summary>
        private void NoteRevive()
        {
            if (!(_flow.Run is DungeonSession dungeon) || dungeon.Revive == null)
            {
                return;
            }
            HeroRevive revive = dungeon.Revive;
            if (revive.Deaths != _noteDeaths || revive.Revives != _noteRevives)
            {
                _noteDeaths = revive.Deaths;
                _noteRevives = revive.Revives;
                Debug.Log("DNF_REVIVE deaths=" + revive.Deaths + " revives=" + revive.Revives + " coins=" + (revive.Coins == null ? 0 : revive.Coins.Count));
            }
        }

        /// <summary>
        /// 副本里捡到金币的堆数变了就写一行 DNF_GOLD(掉了几堆、捡了几堆、这一局捡到的总额与身上的金币),无人值守的检查靠它确认掉落与拾取真的发生
        /// (一局都没掉也没关系,没有变化就不写)。
        /// </summary>
        private void NoteGold()
        {
            if (!(_flow.Run is DungeonSession dungeon))
            {
                return;
            }
            int picked = dungeon.GoldPicked;
            if (_noteGoldPicked < 0)
            {
                _noteGoldPicked = 0;                 // 这一局的起点
            }
            if (picked != _noteGoldPicked)
            {
                _noteGoldPicked = picked;
                Debug.Log("DNF_GOLD dropped=" + dungeon.GoldDropped + " picked=" + picked + " earned=" + dungeon.GoldEarned
                    + " purse=" + (_progress == null ? 0L : _progress.Gold));
            }
            // 物品(E-117):捡起的件数变了写一行 DNF_ITEM(掉了几件、捡了几件、背包放不下留在地上的件数、背包里一共几件占几格)
            int items = dungeon.ItemsPicked;
            if (_noteItemsPicked < 0)
            {
                _noteItemsPicked = 0;
            }
            if (items != _noteItemsPicked)
            {
                _noteItemsPicked = items;
                Debug.Log("DNF_ITEM dropped=" + dungeon.ItemsDropped + " picked=" + items + " full=" + dungeon.ItemsBagFull
                    + " bag=" + (_progress == null ? 0 : _progress.BagItems) + " slots=" + (_progress == null ? 0 : _progress.BagSlots));
            }
        }

        /// <summary>流程模式的初始化:造 <see cref="GameFlow"/>(城镇、副本两种会话的工厂接上这个演示的数据与参数)并进入起始阶段;带自动机参数时再造流程自动机。</summary>
        private void StartFlow()
        {
            OpenSave();
            if (_saveStore != null)
            {
                HeroProgressForRun();                // 城镇里就要按存档里穿的武器造主角,所以进度在流程之前就造好
            }
            string dungeon = string.IsNullOrEmpty(_dungeonPath) ? DefaultDungeon : _dungeonPath;
            // 副本选择界面:读得到世界地图就画原版的世界地图界面(E-094),读不到(数据不全、-dnfworldmap none)退回占位名单
            WorldMapSet world = string.Equals(_worldMapPath, "none", StringComparison.OrdinalIgnoreCase) ? null : WorldMapCatalog.Load(_assets, _worldMapPath);
            bool realSelect = world != null && world.Entries.Count > 0;
            // 城镇:默认走城镇图册(E-095:登录在网关区域、沿出口换区域、地下城入口区域的出口通世界地图、换区域与进图有加载画面);
            // -dnflegacytown 退回只有赛丽亚房间一张图、阶段瞬间切换的老行为;-dnfnosplash 去掉所有加载画面
            ITownWorld townWorld = _legacyTown
                ? new LegacyTownWorld(() => DemoHero.CreateSession(_assets, DefaultMap, DemoHero.SeriaRoom))
                : (ITownWorld)new AtlasTownWorld(_assets, TownAtlas.Open(_assets), 1, withSplashes: !_noSplash, weaponPath: () => _progress?.WeaponPath);
            Func<string, SplashScene> dungeonSplash = _legacyTown || _noSplash ? (Func<string, SplashScene>)null : path => SplashCatalog.ForDungeon(_assets, path);
            // 选角界面(E-096):原版图包与鬼剑士头像;名册是本地存档占位(一个 1 级的默认鬼剑士)
            CharacterSelectScene characterScene = CharacterSelectScene.From(_assets);
            _flow = new GameFlow(
                townWorld,
                (path, run) => CreateDungeon(path, _seed + run),
                realSelect ? (System.Collections.Generic.IEnumerable<DungeonEntry>)world.Entries : new[] { new DungeonEntry(dungeon, null) },
                realSelect ? world.Scene : null,
                dungeonSplash,
                () => new CharacterSelectScreen(CharacterRoster.Local(_progress != null ? _progress.Level : _savedLevel), characterScene));
            _flow.Bag.UseAssets(_assets);            // 背包里穿武器前先问"画面上穿得出来吗"(HeroLook.CanShow)
            _flow.Shop.UseAssets(_assets);           // NPC 商店(E-119):商店目录、物品目录、收购价倍率表都按需从 PVF 读
            _flow.UseShopNpcs(TownShopNpcs.FromAssets(_assets));
            switch (_flowFrom)
            {
                case "char":
                    _flow.EnterCharacterSelect("start");
                    break;
                case "select":
                    _flow.EnterSelect("start");
                    break;
                case "dungeon":
                    _flow.StartDungeon(dungeon, "start");
                    break;
                default:
                    _flow.EnterTown("start");
                    break;
            }
            if (_useBot)
            {
                _flowBot = new FlowBot(_botExplore, ParseSettlementChoices(_flowChoices));
            }
        }

        /// <summary>城镇/区域(例如 1/0)→ 城镇图册里的一个落脚点(登录进入);写错了就报错。</summary>
        private static TownPlace ParseTownArea(string text)
        {
            string[] p = (text ?? string.Empty).Split('/');
            if (p.Length != 2 || !int.TryParse(p[0], out int town) || !int.TryParse(p[1], out int area) || town < 0 || area < 0)
            {
                throw new FormatException("-dnftownarea 需要 城镇/区域(例如 1/0): " + text);
            }
            return new TownPlace(town, area);
        }

        /// <summary>retry、select、town(逗号分隔)→ 结算菜单上的选项;写错了就报错。</summary>
        private static System.Collections.Generic.List<SettlementChoice> ParseSettlementChoices(string text)
        {
            var list = new System.Collections.Generic.List<SettlementChoice>();
            foreach (string item in (text ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (item.Trim().ToLowerInvariant())
                {
                    case "retry":
                        list.Add(SettlementChoice.Retry);
                        break;
                    case "select":
                        list.Add(SettlementChoice.SelectDungeon);
                        break;
                    case "town":
                        list.Add(SettlementChoice.ReturnToTown);
                        break;
                    default:
                        throw new FormatException("-dnfflowchoices 里只能写 retry、select、town: " + item);
                }
            }
            return list;
        }

        /// <summary>流程模式下正处在副本选择界面(没有主角)。</summary>
        private bool InSelect
        {
            get { return _flow != null && _flow.Stage == GameStage.DungeonSelect; }
        }

        /// <summary>流程模式下正处在选角界面(没有主角)。</summary>
        private bool InCharacterSelect
        {
            get { return _flow != null && _flow.Stage == GameStage.CharacterSelect; }
        }

        /// <summary>流程模式下正停在加载画面(没有主角,画面静止,停够时间流程自己往下走)。</summary>
        private bool InLoading
        {
            get { return _flow != null && _flow.Stage == GameStage.Loading; }
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
            bool attack = Input.GetKeyDown(KeyCode.X);
            if (TouchActive)
            {
                // 触屏操作层(M13)与键盘并存:方向以键盘为先,没有键盘方向时用摇杆;按钮是边沿,合并
                TouchFrame f = _touch.Poll();
                if (dirX == 0) { dirX = f.DirX; }
                if (dirY == 0) { dirY = f.DirY; }
                attack |= f.Attack;
                jump |= f.Jump;
                skills |= f.Skills;
            }
            return attack;
        }

        /// <summary>
        /// 触屏操作层有没有在工作:开着(手机、-dnftouch)、在带主角的场景里(城镇或副本,不是选角、副本选择、加载画面、结算菜单)、
        /// 而且没有被键盘时间表(-dnfautowalk)接管。
        /// </summary>
        private bool TouchActive
        {
            get
            {
                return _touch != null && _state == State.Running && _session != null && _heroInput && _script.IsEmpty && _bot == null
                    && !InSelect && !InCharacterSelect && !InLoading
                    && !(_session is DungeonSession settling && settling.SettlementActive);
            }
        }

        private void CreateTouch()
        {
            _touch?.Dispose();
            _touch = null;
            if (!_touchForced && !Application.isMobilePlatform)
            {
                return;
            }
            // 桌面上(没有触摸屏)用鼠标左键模拟一根手指;手机与平板读真实的触摸
            _touch = new TouchOverlay(simulateWithMouse: !Application.isMobilePlatform && !Input.touchSupported, script: TouchScript.Parse(_touchScriptText));
            Debug.Log("DNF_TOUCH on screen=" + Screen.width + "x" + Screen.height + " safe=" + Screen.safeArea + " script=" + (string.IsNullOrEmpty(_touchScriptText) ? "none" : "yes")
                + " mouse=" + !Application.isMobilePlatform + " mobile=" + Application.isMobilePlatform);
        }

        /// <summary>每帧读一次触摸:场景里不该有触屏操作时松开所有手指;暂停键在暂停时也要响应;暂停时丢掉攻击、跳跃、技能键的边沿。</summary>
        private void PumpTouch()
        {
            if (_touch == null)
            {
                return;
            }
            if (!TouchActive)
            {
                _touch.Release();
                return;
            }
            _touch.Pump((Time.realtimeSinceStartup - _runningSince) * 1000.0);
            if (_touch.TakePause())
            {
                TogglePause();
            }
            if (_paused)
            {
                _touch.Release();
            }
            if (!string.IsNullOrEmpty(_touchShotPath) && !_touchShotStarted && Time.realtimeSinceStartup - _runningSince >= _touchShotAtSeconds)
            {
                _touchShotStarted = true;
                StartCoroutine(CaptureTouchShot());
            }
        }

        /// <summary>等这一帧画完,把整个窗口(含 OnGUI 画的控件)存成 PNG,用来检查控件的摆法。</summary>
        private IEnumerator CaptureTouchShot()
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(_touchShotPath));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllBytes(_touchShotPath, tex.EncodeToPNG());
                Debug.Log("DNF_TOUCH shot=" + _touchShotPath + " size=" + tex.width + "x" + tex.height);
            }
            finally
            {
                UnityObjects.Destroy(tex);
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
            _audio?.SetPaused(_paused);
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
            // 桌面:整数倍放大(窗口不到 800×600 时按比例缩小),居中,四周留黑边,像素与原版一一对应。
            // 手机与平板(以及开着触屏操作层时):整数倍会让画面只占屏幕高度的一半多一点,所以改成填满安全区的高度(倍数不是整数,像素会略有不均)。
            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            bool fit = _touch != null || Application.isMobilePlatform;
            Rect region = new Rect(0f, 0f, w, h);
            Rect safe = Screen.safeArea;
            if (fit && safe.width > 0f && safe.height > 0f)
            {
                region = safe;
            }
            float scale = Mathf.Min(region.width / PixelScreen.Width, region.height / PixelScreen.Height);
            if (scale >= 1f && !fit)
            {
                scale = Mathf.Floor(scale);
            }
            float vw = PixelScreen.Width * scale, vh = PixelScreen.Height * scale;
            _view.Stage.Camera.pixelRect = new Rect(Mathf.Floor(region.x + (region.width - vw) * 0.5f), Mathf.Floor(region.y + (region.height - vh) * 0.5f), vw, vh);
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
                    if (TouchActive)
                    {
                        _touch.Draw(_paused);
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

        /// <summary>
        /// 系统菜单开着时的输入(开着就独占:别的键都不响应):上下键选、回车/空格/数字键确定、Esc 关上、鼠标点行确定(悬停选行在 <see cref="PumpMenu"/>)。
        /// </summary>
        private void HandleMenuInput(Event e)
        {
            SystemMenu menu = _flow.Menu;
            if (e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Escape:
                        _flow.ToggleMenu();
                        break;
                    case KeyCode.UpArrow:
                        menu.Move(-1);
                        break;
                    case KeyCode.DownArrow:
                        menu.Move(1);
                        break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                    case KeyCode.Space:
                        menu.Choose();
                        break;
                    case KeyCode.Alpha1:
                    case KeyCode.Keypad1:
                        menu.ChooseNumber(1);
                        break;
                    case KeyCode.Alpha2:
                    case KeyCode.Keypad2:
                        menu.ChooseNumber(2);
                        break;
                    case KeyCode.Alpha3:
                    case KeyCode.Keypad3:
                        menu.ChooseNumber(3);
                        break;
                }
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && TryGetCanvasPoint(Input.mousePosition, out int cx, out int cy))
            {
                menu.Click(cx, cy);
            }
        }

        /// <summary>
        /// 背包窗口开着时的输入(开着就独占):方向键移动光标(格子最左一列再按左键进装备栏),Q / E 或 Tab 换页,回车或 X 穿脱,I 或 Esc 关上,鼠标点格子(再点一下穿脱)。
        /// 键位是本项目自己的占位设计(原版背包界面没有读到)。
        /// </summary>
        private void HandleBagInput(Event e)
        {
            BagWindow bag = _flow.Bag;
            bool changed = false;
            if (e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Escape:
                        _flow.ToggleMenu();          // 菜单的开关键:开着背包时先把背包关上
                        break;
                    case KeyCode.I:
                        _flow.ToggleBag();
                        break;
                    case KeyCode.UpArrow:
                        bag.Move(0, -1);
                        break;
                    case KeyCode.DownArrow:
                        bag.Move(0, 1);
                        break;
                    case KeyCode.LeftArrow:
                        bag.Move(-1, 0);
                        break;
                    case KeyCode.RightArrow:
                        bag.Move(1, 0);
                        break;
                    case KeyCode.Q:
                        bag.NextTab(-1);
                        break;
                    case KeyCode.E:
                    case KeyCode.Tab:
                        bag.NextTab(1);
                        break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                    case KeyCode.X:
                        changed = bag.Activate(_progress);
                        break;
                }
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && _progress != null && TryGetCanvasPoint(Input.mousePosition, out int cx, out int cy))
            {
                int before = bag.Changes;
                bag.Click(cx, cy, _progress);
                changed = bag.Changes != before;
            }
            if (changed)
            {
                SyncHeroLook();
                Debug.Log("DNF_BAG changed equipped=" + _progress.EquippedCount + " weapon=" + (_progress.WeaponPath != null) + " msg=\"" + bag.Message + "\"");
            }
        }

        /// <summary>
        /// 商店窗口开着时的输入(开着就独占):方向键移动光标(数量框开着时上下 ±1、左右 ±10),Q / E 换页,Tab 在买入与出售之间切换,回车或 X 买卖
        /// (可叠加物品先弹数量框,再按回车确认),Esc 关上(数量框开着时 Esc 只取消数量框),鼠标点模式按钮、页签、格子(再点一下已选中的格子等于回车)、数量框的按钮。
        /// 键位是本项目自己的占位设计(原版商店界面没有读到)。
        /// </summary>
        private void HandleShopInput(Event e)
        {
            ShopWindow shop = _flow.Shop;
            if (e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Escape:
                        if (shop.IsPromptOpen)
                        {
                            shop.CancelPrompt();
                        }
                        else
                        {
                            _flow.ToggleMenu();      // 菜单的开关键:开着商店时先把商店关上
                        }
                        break;
                    case KeyCode.UpArrow:
                        shop.Move(0, -1);
                        break;
                    case KeyCode.DownArrow:
                        shop.Move(0, 1);
                        break;
                    case KeyCode.LeftArrow:
                        shop.Move(-1, 0);
                        break;
                    case KeyCode.RightArrow:
                        shop.Move(1, 0);
                        break;
                    case KeyCode.Q:
                        shop.NextTab(-1);
                        break;
                    case KeyCode.E:
                        shop.NextTab(1);
                        break;
                    case KeyCode.Tab:
                        shop.ToggleMode();
                        break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                    case KeyCode.X:
                        if (_progress != null)
                        {
                            shop.Activate(_progress);
                        }
                        break;
                }
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && _progress != null && TryGetCanvasPoint(Input.mousePosition, out int cx, out int cy))
            {
                shop.Click(cx, cy, _progress);
            }
            int trades = shop.Purchases + shop.Sales;
            if (trades != _shopTradesLogged)
            {
                _shopTradesLogged = trades;
                Debug.Log("DNF_SHOP trade purchases=" + shop.Purchases + " sales=" + shop.Sales + " spent=" + shop.Spent + " earned=" + shop.Earned
                    + " gold=" + _progress.Gold + " bag=" + _progress.BagItems + " msg=\"" + shop.Message + "\"");
            }
        }

        private void HandleRunningInput(Event e)
        {
            if (_flow != null && _flow.Menu.Open)
            {
                HandleMenuInput(e);
                return;
            }
            if (_flow != null && _flow.Shop.Open)
            {
                HandleShopInput(e);
                return;
            }
            if (_flow != null && _flow.Bag.Open)
            {
                HandleBagInput(e);
                return;
            }
            if (e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Space:
                        if (InSelect)
                        {
                            _flow.Select.Confirm();     // 副本选择界面底栏写的是"空格 进入地下城"
                        }
                        else if (InCharacterSelect)
                        {
                            _flow.CharacterSelect.Confirm();
                        }
                        else if (!_paused && _flow != null && _flow.Stage == GameStage.Town && HeroProgressForRun() != null && _flow.TryOpenNearbyShop())
                        {
                            // 城镇里走到商店 NPC 身边按空格:打开商店(本项目自己的占位键位;游戏暂停由 PumpMenu 接着处理)
                        }
                        else
                        {
                            TogglePause();
                        }
                        break;
                    case KeyCode.LeftArrow:
                        if (InCharacterSelect)
                        {
                            _flow.CharacterSelect.Move(-1);
                        }
                        else if ((_paused || _session == null) && !InSelect && !InLoading)
                        {
                            Step(-50.0);
                        }
                        break;
                    case KeyCode.RightArrow:
                        if (InCharacterSelect)
                        {
                            _flow.CharacterSelect.Move(1);
                        }
                        else if ((_paused || _session == null) && !InSelect && !InLoading)
                        {
                            Step(50.0);
                        }
                        break;
                    case KeyCode.UpArrow:
                        if (InSelect)
                        {
                            _flow.Select.Move(-1);
                        }
                        else if (InCharacterSelect)
                        {
                            _flow.CharacterSelect.Move(0, -1);
                        }
                        break;
                    case KeyCode.DownArrow:
                        if (InSelect)
                        {
                            _flow.Select.Move(1);
                        }
                        else if (InCharacterSelect)
                        {
                            _flow.CharacterSelect.Move(0, 1);
                        }
                        break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        if (InSelect)
                        {
                            _flow.Select.Confirm();
                        }
                        else if (InCharacterSelect)
                        {
                            _flow.CharacterSelect.Confirm();
                        }
                        else if (_flow != null && _flow.Stage == GameStage.Town && _flow.TownWorld is LegacyTownWorld)
                        {
                            _flow.EnterSelect("key");   // 占位(-dnflegacytown):老式城镇只有赛丽亚房间一张图,没有出口;城镇图册里的城镇走到地下城入口区域的出口就是了
                        }
                        break;
                    case KeyCode.H:
                        _hud = !_hud;
                        break;
                    case KeyCode.I:
                        if (_flow != null && HeroProgressForRun() != null)
                        {
                            _flow.ToggleBag();           // 背包窗口(城镇与副本里;没有等级进度的运行模式没有背包)
                        }
                        break;
                    case KeyCode.Escape:
                        if (_flow != null && _flow.ToggleMenu())
                        {
                            // 城镇与副本里:打开系统菜单(游戏暂停,PumpMenu 接着处理)
                        }
                        else if (InSelect)
                        {
                            _flow.Select.Back();
                        }
                        else if (InCharacterSelect)
                        {
                            _flow.CharacterSelect.Back();   // 结束游戏:宿主在 StepFlow 里看到结果就退出
                        }
                        else
                        {
                            Quit();
                        }
                        break;
                    // 副本结算菜单的热键(原版界面上写的是 F13 再次挑战、F11 选择其它地下城、F12 返回城镇,证据 E-091);
                    // 笔记本键盘没有 F13,所以数字键 1、2、3 也行(这是本演示加的,原版没有)
                    case KeyCode.F13:
                    case KeyCode.Alpha1:
                        ChooseSettlement(SettlementChoice.Retry);
                        break;
                    case KeyCode.F11:
                    case KeyCode.Alpha2:
                        ChooseSettlement(SettlementChoice.SelectDungeon);
                        break;
                    case KeyCode.F12:
                    case KeyCode.Alpha3:
                        ChooseSettlement(SettlementChoice.ReturnToTown);
                        break;
                }
            }
            else if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (_session is DungeonSession settling && settling.SettlementActive)
                {
                    // 结算时点击(触摸)只用来点菜单行;点在别处没有反应,不当成暂停
                    if (TryGetCanvasPoint(Input.mousePosition, out int cx, out int cy))
                    {
                        settling.Click(cx, cy);
                    }
                }
                else if (InSelect)
                {
                    // 副本选择界面:点卡片选中(再点一下进入)、点"返回城镇"按钮返回(触摸也走这条)
                    if (TryGetCanvasPoint(Input.mousePosition, out int sx, out int sy))
                    {
                        _flow.Select.Click(sx, sy);
                    }
                }
                else if (InCharacterSelect)
                {
                    // 选角界面:点角色的格子选中、点"游戏开始"开始、点"结束游戏"退出(触摸也走这条)
                    if (TryGetCanvasPoint(Input.mousePosition, out int cx2, out int cy2))
                    {
                        _flow.CharacterSelect.Click(cx2, cy2);
                    }
                }
                else if (_touch == null)
                {
                    TogglePause(); // 触摸屏上没有空格键(开着触屏操作层时有自己的暂停键,点哪里都不再暂停)
                }
            }
        }

        private void ChooseSettlement(SettlementChoice choice)
        {
            if (_session is DungeonSession dungeon)
            {
                dungeon.Choose(choice);
            }
        }

        /// <summary>
        /// 屏幕(窗口像素,原点在左下)上的一点换算成 800×600 画布上的坐标(原点在左上);不在舞台视口里返回 false。
        /// 舞台是整数倍放大、居中、留黑边的(<see cref="LateUpdate"/>),所以按舞台相机的视口算。
        /// </summary>
        private bool TryGetCanvasPoint(Vector3 screen, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (_view == null)
            {
                return false;
            }
            Rect r = _view.Stage.Camera.pixelRect;
            if (r.width <= 0f || r.height <= 0f)
            {
                return false;
            }
            float fx = (screen.x - r.x) / r.width * PixelScreen.Width;
            float fy = (r.yMax - screen.y) / r.height * PixelScreen.Height;
            if (fx < 0f || fy < 0f || fx >= PixelScreen.Width || fy >= PixelScreen.Height)
            {
                return false;
            }
            x = (int)fx;
            y = (int)fy;
            return true;
        }

        /// <summary>
        /// 结算菜单:鼠标(触摸)停在哪一行就让那一行亮;菜单上选定的那一刻在日志里留一行 DNF_SETTLE(无人值守的检查读它)。
        /// </summary>
        private void UpdateSettlement()
        {
            if (!(_session is DungeonSession dungeon))
            {
                return;
            }
            if (dungeon.SettlementActive && dungeon.Choice == SettlementChoice.None && TryGetCanvasPoint(Input.mousePosition, out int cx, out int cy))
            {
                dungeon.SetPointer(cx, cy);
            }
            else
            {
                dungeon.ClearPointer();
            }
            if (dungeon.Choice != _loggedChoice)
            {
                _loggedChoice = dungeon.Choice;
                Debug.Log("DNF_SETTLE choice=" + dungeon.Choice + " clockMs=" + (long)dungeon.ClockMs);
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
                    + (h.Z > 0 ? "  z " + (int)h.Z : string.Empty)
                    + "  camera x " + _session.Camera.IntX + "\n";
                if (_session is DungeonSession dungeon)
                {
                    hero += "cell (" + dungeon.CellX + ", " + dungeon.CellY + ")   rooms entered " + dungeon.RoomsEntered + "  cleared " + dungeon.RoomsCleared
                        + "/" + (dungeon.Maze.Cols * dungeon.Maze.Rows) + "  moves " + dungeon.Transitions + (dungeon.BossCleared ? "   BOSS ROOM CLEARED" : "")
                        + (dungeon.SettlementActive
                            ? (dungeon.Choice == SettlementChoice.None ? "   SETTLEMENT: F13 or 1 = retry, F11 or 2 = select dungeon, F12 or 3 = town (or click the menu)" : "   CHOICE: " + dungeon.Choice)
                            : string.Empty)
                        + "\n"
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
                if (_audio != null)
                {
                    hero += "audio  cues " + _audio.CueCount + "  playing " + _audio.InFlight + "  music " + (_audio.Scheduler.MusicId ?? "-")
                        + (_audio.Audible ? string.Empty : "  (muted)") + (_audio.FailedCount > 0 ? "  load failures " + _audio.FailedCount : string.Empty) + "\n";
                }
                if (_session is DungeonSession)
                {
                    help = (_bot != null ? "Auto-play bot is driving (bot input only)   " : "Arrow keys: walk (double-tap Left/Right: run)   X: attack (again to chain; while running: dash attack)   C: jump (X in the air: jump attack)   A: Hard Attack   S: Upper Slash   D: Back Step   ")
                        + "Space: pause   H: hide text   " + (_flow != null ? "Esc: menu" : "Esc: quit") + "   (clear a room to open its gates, then walk into a gate; placeholder damage; skills cost MP; monsters hit back unless -dnfnodamage; Z not bound; X on the death screen uses a revive coin)";
                }
                else
                {
                    help = _session is RoomCombatSession
                        ? "Arrow keys: walk (double-tap Left/Right: run)   X: attack (again to chain; while running: dash attack)   C: jump (X in the air: jump attack)   A: Hard Attack   S: Upper Slash   D: Back Step   Space: pause (then Left/Right step 50 ms)   H: hide text   Esc: quit   (placeholder damage; skills cost MP; monsters hit back unless -dnfnodamage)"
                        : "Arrow keys: walk   Space: pause (then Left/Right step 50 ms)   H: hide text   Esc: quit   (no UI, no combat)";
                }
            }
            else
            {
                help = "Space: pause   Left/Right: step 50 ms   H: hide text   Esc: quit   (no player character on this map)";
            }
            if (_flow != null)
            {
                hero = "flow: " + _flow.Stage + "  dungeon runs " + _flow.Runs + "  menu choices " + _flow.Settlements
                    + (_flowBot != null ? "  (autopilot" + (_flowBot.Done ? ", done)" : ")") : string.Empty) + "\n" + hero;
                if (InSelect)
                {
                    help = "Up/Down: choose a dungeon   Enter or Space: go in   Esc: back to town   Mouse: click a card (again to enter) or the town button   H: hide text"
                        + (_flow.Select.World == null ? "   (placeholder screen)" : string.Empty);
                }
                else if (InCharacterSelect)
                {
                    help = "Arrow keys: choose a character   Enter or Space: start   Esc: quit   Mouse: click a character slot, Start or Exit   H: hide text"
                        + (_flow.CharacterSelect.Scene == null ? "   (placeholder screen)" : string.Empty);
                }
                else if (_flow.Stage == GameStage.Loading)
                {
                    help = "Loading screen (the flow moves on by itself)   H: hide text   Esc: quit";
                }
                else if (_flow.Stage == GameStage.Town)
                {
                    help = _flow.TownWorld is LegacyTownWorld
                        ? "Arrow keys: walk   Enter: open the dungeon map (placeholder: this legacy town has no exits)   Space: pause   H: hide text   Esc: menu"
                        : "Arrow keys: walk (push into a map edge exit to change area; the last area's right-hand exit opens the dungeon map)   Space: pause   H: hide text   Esc: menu";
                }
            }
            string shownMap = _session != null ? _session.MapPath : InSelect ? "(dungeon select)" : InCharacterSelect ? "(character select)" : mapPath;
            string text = (_session is DungeonSession ? "DNF dungeon demo (Unity)   room: " : IsRoomSession ? "DNF dungeon room demo (Unity)   map: " : (InSelect || InCharacterSelect) ? "DNF game flow demo (Unity)   screen: " : "DNF town demo (Unity)   map: ") + shownMap + "\n"
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
            _touch?.Dispose();
            _touch = null;
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
