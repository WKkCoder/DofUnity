using System;
using System.Collections;
using Dnf.Data;
using Dnf.Play;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// TownDemo 的联网外壳一侧(<c>-dnfonline</c>,r18-onlineshell)。所有判断都在不碰引擎的 <see cref="OnlineShellHost"/> 里(有测试);
    /// 这里只做引擎的活:读老 <c>Input</c>(文字用 <c>Input.inputString</c>)、每帧调 <see cref="OnlineShellHost.Tick"/>、按它的结论换画面,
    /// 以及进游戏时把控制权交给既有的城镇流程(<see cref="GameFlow"/>,与 <c>-dnfnet</c> 进城之后同一条路)。
    /// <para>
    /// 屏幕归属:外壳阶段(登录、选服、选频道、选角、加载)画外壳自己的场景,游戏流程不存在或被冻住(<c>_flow</c>、<c>_session</c> 置空,
    /// 于是 TownDemo 里所有按"有没有流程"判断的地方——推进、系统菜单、触屏层——自然什么都不做);进了游戏(<see cref="OnlineShellStage.InGame"/>)
    /// 画游戏流程,外壳仍每帧 Tick 一次(为了发现断线);掉线或换角色时外壳拿回屏幕,游戏流程冻住,回来时恢复。
    /// 联网会话归 <see cref="OnlineShellHost"/>(进游戏之后继续活着),<see cref="DisposeOnlineShell"/> 时放弃。
    /// </para>
    /// </summary>
    public sealed partial class TownDemo
    {
        /// <summary>记住"上次登录成功的账号"的偏好键(只记账号;口令从不记)。</summary>
        private const string OnlineUserPrefsKey = "DnfOnlineUser";

        private bool _onlineMode;            // -dnfonline
        private string _onlineUser;          // -dnfonlineuser <账号>:预填登录界面的账号(口令不走命令行)
        private bool _serverUi;              // -dnfserverui:选服务器、选频道用真界面(不带时这两个阶段是占位、自动放行)
        private OnlineShellHost _shell;      // 联网外壳;进游戏之后它继续拥有联网会话
        private string _shellDir;            // 数据目录(第一次进游戏时开声音要用)
        private GameFlow _frozenFlow;        // 掉线或换角色时外壳拿回屏幕:被冻住的游戏流程
        private FlowBot _frozenBot;          // 与它的自动机
        private bool _frozenPaused;          // 冻住的那一刻游戏是不是暂停着(菜单等窗口开着时是)

        /// <summary>联网外壳现在占着屏幕(登录、选服、选频道、选角、加载);进了游戏之后为 false。</summary>
        private bool ShellOwnsScreen
        {
            get { return _shell != null && !_shell.InGame; }
        }

        /// <summary>
        /// 要不要走联网外壳:带了 -dnfonline,没带 -dnfnet(那条路优先、保持原样),也不是离屏出图(-dnfshot)。
        /// 带了 -dnfonline 却被忽略时留一行提示(不含任何配置值)。
        /// </summary>
        private bool OnlineShellWanted()
        {
            if (!_onlineMode)
            {
                return false;
            }
            if (_netMode)
            {
                Debug.Log("DNF_TOWN_DEMO online ignored reason=net_mode");
                return false;
            }
            if (!string.IsNullOrEmpty(_shotPath))
            {
                Debug.Log("DNF_TOWN_DEMO online ignored reason=shot");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 打开数据、读连接配置、造外壳,画出第一个界面(登录)。配置缺失或不合规时外壳照样起来,登录界面提示"未配置连接"、不发任何请求。
        /// 日志只写配置来源与"有没有配置",不写任何配置的值。
        /// </summary>
        private IEnumerator OpenOnlineShell(string dir)
        {
            _state = State.Loading;
            _message = "Opening the game data…";
            yield return null;
            try
            {
                DisposeView();
                _assets = DnfAssets.Open(dir);
                _flowMode = true;                // 外壳之后接的是城镇流程:字体(汉字标签要用)与声音按流程模式装
                _flowFrom = "town";              // 进游戏落在城镇,不管 -dnfflowfrom 写了什么
                InstallFonts();
                bool dev = Application.isEditor || Debug.isDebugBuild;
                string remembered = PlayerPrefs.HasKey(OnlineUserPrefsKey) ? PlayerPrefs.GetString(OnlineUserPrefsKey) : null;
                OnlineShellSetup setup = OnlineShellSetupLoader.Load(dev, _netRoot, _netPort, Application.persistentDataPath, _onlineUser, remembered);
                CharacterSelectScene shellArt = CharacterSelectScene.ForOnline(_assets);
                _shell = OnlineShellHost.Create(setup, shellArt, line => Debug.Log(line), _serverUi, _serverUi ? ServerPickerScene.From(shellArt, _assets.LoadAni) : null,
                    OnlineCharacterSelectSetup.Create(_assets, _fonts != null));
                _shellDir = dir;
                _shell.TakeViewChange();         // 第一个视图就是下面造的这一个
                _view = new TownView(_assets, _shell.Flow.Scene, StageBackground);
                Debug.Log("DNF_TOWN_DEMO online source=" + setup.Source + " configured=" + setup.Configured + " dev=" + dev + " serverui=" + _serverUi);
            }
            catch (Exception e)
            {
                Fail(e);
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
            Debug.Log("DNF_TOWN_DEMO running online shell");
        }

        /// <summary>
        /// 每帧一次(Update 里,推进游戏之前):读输入交给外壳、推它的阶段机、按它的结论换画面。没带 -dnfonline(没有外壳)时什么也不做、返回 true。
        /// 返回 false 表示这一帧不用再往下走了(出错已经转成失败屏,或选角界面点了"结束游戏"正在退出)。
        /// </summary>
        private bool PumpOnlineShell()
        {
            if (_shell == null)
            {
                return true;
            }
            try
            {
                if (_shell.InGame && _flow != null && _flow.Stage == GameStage.CharacterSelect)
                {
                    // 游戏里系统菜单选了"回选角":联网时回外壳(选频道 → 选角,重新取角色列表),不进离线的占位选角
                    _shell.SwapCharacter();
                }
                // 游戏占着屏幕时键盘鼠标归游戏(外壳只每帧轮询、发现断线),不把输入给外壳
                OnlineShellFrameInput input = _shell.InGame ? default(OnlineShellFrameInput) : ReadShellInput();
                _shell.Tick(input, Math.Min(Time.unscaledDeltaTime * 1000.0, 100.0), (long)(Time.realtimeSinceStartupAsDouble * 1000.0));
                string user = _shell.TakeLoggedInUsername();
                if (user != null && _quitAfterSeconds < 0f)
                {
                    PlayerPrefs.SetString(OnlineUserPrefsKey, user); // 只记账号,口令从不记;无人值守的检查运行不改动用户的偏好
                }
                if (_shell.QuitRequested)
                {
                    Debug.Log("DNF_TOWN_DEMO online quit");
                    Quit();                      // 选角界面(或选频道界面)上的"结束游戏"
                    return false;
                }
                switch (_shell.TakeViewChange())
                {
                    case OnlineShellViewChange.ShowShell:
                        ShowShellView();
                        break;
                    case OnlineShellViewChange.StartGame:
                        StartOnlineGame();
                        break;
                    case OnlineShellViewChange.ResumeGame:
                        ResumeOnlineGame();
                        break;
                }
                return true;
            }
            catch (Exception e)
            {
                Fail(e);
                return false;
            }
        }

        /// <summary>这一帧的键盘与鼠标(老 Input):文字、刚按下的键(Tab 带 Shift)、鼠标左键点在 800×600 画布上的位置。</summary>
        private OnlineShellFrameInput ReadShellInput()
        {
            var input = new OnlineShellFrameInput
            {
                Text = Input.inputString,
                Enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter),
                Escape = Input.GetKeyDown(KeyCode.Escape),
                Tab = Input.GetKeyDown(KeyCode.Tab),
                Shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
                Up = Input.GetKeyDown(KeyCode.UpArrow),
                Down = Input.GetKeyDown(KeyCode.DownArrow),
                Left = Input.GetKeyDown(KeyCode.LeftArrow),
                Right = Input.GetKeyDown(KeyCode.RightArrow),
                Space = Input.GetKeyDown(KeyCode.Space),
            };
            bool onCanvas = TryGetCanvasPoint(Input.mousePosition, out int x, out int y);
            if (onCanvas)
            {
                input.HasPointer = true;         // 悬停的画法(选服务器、选频道界面)用
                input.PointerX = x;
                input.PointerY = y;
            }
            if (Input.GetMouseButtonDown(0) && onCanvas)
            {
                input.Click = true;
                input.ClickX = x;
                input.ClickY = y;
            }
            return input;
        }

        /// <summary>外壳拿到(或拿回)屏幕:游戏还开着(掉线、换角色)先冻住,再用外壳当前阶段的场景造视图、销毁旧视图。</summary>
        private void ShowShellView()
        {
            if (_flow != null)
            {
                FreezeGame();
            }
            TownView previous = _view;
            _view = new TownView(_assets, _shell.Flow.Scene, StageBackground);
            previous?.Dispose();
        }

        /// <summary>
        /// 第一次进游戏:造城镇流程(角色本体先用本地占位,与 -dnfnet 进城之后同一条路),声音与触屏操作层这时才开,
        /// 再换成流程当前阶段的视图(带名牌与底栏的叠加层,盖掉外壳最后一个视图)。
        /// </summary>
        private void StartOnlineGame()
        {
            StartFlow();
            _audio = TryCreateAudio(_shellDir);
            SwitchView();
            CreateTouch();
            Debug.Log("DNF_TOWN_DEMO online ingame" + HeroLog());
        }

        /// <summary>
        /// 外壳带着新会话再次进游戏(掉线重登、换角色之后):恢复被冻住的流程。换角色是从系统菜单的"回选角"来的,流程停在离线占位选角上,
        /// 这里像离线那样"开始游戏"(确认选中的占位角色 → 登录加载画面 → 城镇落脚点);掉线重登流程停在原来的阶段,接着往下走。
        /// </summary>
        private void ResumeOnlineGame()
        {
            if (_frozenFlow == null)
            {
                throw new InvalidOperationException("外壳进了游戏,却没有被冻住的游戏流程可以恢复");
            }
            _flow = _frozenFlow;
            _flowBot = _frozenBot;
            _frozenFlow = null;
            _frozenBot = null;
            if (_flow.Stage == GameStage.CharacterSelect)
            {
                _flow.CharacterSelect?.Confirm();
                _flow.Poll();
            }
            SwitchView();
            if (_frozenPaused && !_paused)
            {
                TogglePause();                   // 冻住时窗口开着、游戏暂停着:回来时窗口还开着,接着暂停
            }
            _frozenPaused = false;
            Debug.Log("DNF_TOWN_DEMO online resume" + HeroLog());
        }

        /// <summary>
        /// 外壳拿回屏幕时冻住游戏:流程、自动机与暂停状态留着,<c>_flow</c> 与 <c>_session</c> 置空(TownDemo 里按"有没有流程"判断的推进、菜单、触屏层都不再动),
        /// 暂停着的话先恢复时钟(外壳的画面照常走),停掉游戏的声音。
        /// </summary>
        private void FreezeGame()
        {
            _frozenFlow = _flow;
            _frozenBot = _flowBot;
            _frozenPaused = _paused;
            if (_paused)
            {
                TogglePause();
            }
            _audio?.StopAll();
            _flow = null;
            _flowBot = null;
            _session = null;
        }

        /// <summary>放弃外壳与它拥有的联网会话(最多等一秒让后台释放做完),冻住的流程一并丢掉。没有外壳时什么也不做。</summary>
        private void DisposeOnlineShell()
        {
            if (_shell != null)
            {
                _shell.Dispose();
                _shell = null;
            }
            _frozenFlow = null;
            _frozenBot = null;
            _frozenPaused = false;
        }
    }
}
