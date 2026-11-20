using System;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>
    /// 一帧里读到的输入(宿主把引擎的键盘鼠标读成它:文字来自 <c>Input.inputString</c>,按键是这一帧刚按下的,点击是画布坐标)。
    /// 全是"刚发生的事",不带状态:外壳自己管焦点、选中格与提交。
    /// </summary>
    public struct OnlineShellFrameInput
    {
        /// <summary>这一帧键盘输入的文字(退格是 '\b',其它控制字符外壳会忽略);没有为 null。</summary>
        public string Text;

        public bool Enter;
        public bool Escape;
        public bool Tab;

        /// <summary>按 Tab 时 Shift 是不是按着(往回切焦点)。</summary>
        public bool Shift;

        public bool Up;
        public bool Down;
        public bool Left;
        public bool Right;
        public bool Space;

        /// <summary>鼠标左键(或触摸)这一帧按下。</summary>
        public bool Click;

        /// <summary>点击位置(800×600 画布坐标)。</summary>
        public int ClickX;

        public int ClickY;

        /// <summary>鼠标指针这一帧在画布上(<see cref="PointerX"/>、<see cref="PointerY"/> 有效);不在画布上为 false。只影响悬停的画法。</summary>
        public bool HasPointer;

        /// <summary>指针位置(800×600 画布坐标)。</summary>
        public int PointerX;

        public int PointerY;
    }

    /// <summary>宿主这一帧要对画面做什么(<see cref="OnlineShellHost.TakeViewChange"/> 的结论;宿主只照做,不自己判断)。</summary>
    public enum OnlineShellViewChange
    {
        /// <summary>什么都不用做。</summary>
        None = 0,

        /// <summary>外壳拿到(或从游戏里拿回)了屏幕:用 <see cref="OnlineShellHost.Flow"/> 的 Scene 造新视图;游戏还开着的话先把它冻住(不再推进)。</summary>
        ShowShell,

        /// <summary>第一次进入游戏:造游戏流程并换成它的画面。</summary>
        StartGame,

        /// <summary>外壳带着新会话再次进入游戏(掉线重登、换角色之后):恢复之前冻住的游戏流程。</summary>
        ResumeGame,
    }

    /// <summary>把一帧输入交给外壳。顺序:先文字(同一帧里先打字再按回车是常见的),再按键,最后点击。</summary>
    public static class OnlineShellInput
    {
        public static void Apply(OnlineShellFlow flow, OnlineShellFrameInput input)
        {
            if (flow == null)
            {
                throw new ArgumentNullException(nameof(flow));
            }
            if (!string.IsNullOrEmpty(input.Text))
            {
                flow.Type(input.Text);
            }
            if (input.Tab)
            {
                flow.Key(input.Shift ? OnlineShellKey.ShiftTab : OnlineShellKey.Tab);
            }
            if (input.Enter)
            {
                flow.Key(OnlineShellKey.Enter);
            }
            if (input.Escape)
            {
                flow.Key(OnlineShellKey.Escape);
            }
            if (input.Up)
            {
                flow.Key(OnlineShellKey.Up);
            }
            if (input.Down)
            {
                flow.Key(OnlineShellKey.Down);
            }
            if (input.Left)
            {
                flow.Key(OnlineShellKey.Left);
            }
            if (input.Right)
            {
                flow.Key(OnlineShellKey.Right);
            }
            if (input.Space)
            {
                flow.Key(OnlineShellKey.Space);
            }
            if (input.HasPointer)
            {
                flow.Hover(input.PointerX, input.PointerY);
            }
            else
            {
                flow.HoverNone();
            }
            if (input.Click)
            {
                flow.Click(input.ClickX, input.ClickY);
            }
        }
    }

    /// <summary>
    /// 联网外壳的宿主一侧(不碰引擎,TownDemo 的 <c>-dnfonline</c> 分支只做"读输入、每帧调 <see cref="Tick"/>、阶段变了换画面"):
    /// 把连接配置(<see cref="OnlineShellSetup"/>)、真后端(<see cref="OnlineShellBackend"/>)与阶段机(<see cref="OnlineShellFlow"/>)接在一起,
    /// 并拥有联网会话——进入游戏之后会话继续活着(<see cref="Session"/>),直到退出、换角色(<see cref="SwapCharacter"/>)、断线或回到登录时被放弃。
    /// </summary>
    public sealed class OnlineShellHost : IDisposable
    {
        private readonly OnlineShellFlow _flow;
        private readonly OnlineShellBackend _backend;
        private bool _disposed;
        private int _shownSerial = -1;          // 宿主已经为它造过视图的外壳阶段序号
        private bool _gameShown;                // 游戏现在占着屏幕
        private bool _gameStarted;              // 游戏流程至少开过一次(再进游戏是恢复,不是重开)
        private bool _rememberPending = true;   // 自从上次停在登录界面以来,还没交出过"登录成功的账号"

        /// <summary>
        /// 造真实的宿主:后端用 <paramref name="setup"/> 的连接配置(没有配置时后端什么网络操作也不做)。
        /// <paramref name="serverUi"/> 为 true 时启用选服务器、选频道的真界面(r18-serverui):外壳不再自动放行这两个阶段(<see cref="OnlineShellOptions.AutoPassPlaceholders"/> 设为 false),
        /// 后端同时充当频道目录的来源;<paramref name="picker"/> 是真界面的素材(为 null 时用 <paramref name="art"/> 的图包与光束)。
        /// 为 false(默认)时一切照旧:两个阶段是占位、自动放行,用默认频道端口连接。
        /// <paramref name="characterSelect"/> 非 null 时选角界面用服务器给的真名册(<see cref="OnlineShellOptions.CharacterSelect"/>),为 null 是外壳原来的占位名册。
        /// </summary>
        public static OnlineShellHost Create(OnlineShellSetup setup, CharacterSelectScene art, Action<string> log, bool serverUi = false, ServerPickerScene picker = null,
            OnlineCharacterSelectOptions characterSelect = null, ChannelDirectoryCatalog directoryCatalog = null, IGlyphFonts fonts = null)
        {
            if (setup == null)
            {
                throw new ArgumentNullException(nameof(setup));
            }
            return new OnlineShellHost(setup, new OnlineShellBackend(setup.CreateSettings(), log, directoryCatalog), art, log, fonts, serverUi, picker, characterSelect);
        }

        internal OnlineShellHost(OnlineShellSetup setup, OnlineShellBackend backend, CharacterSelectScene art, Action<string> log, IGlyphFonts fonts,
            bool serverUi = false, ServerPickerScene picker = null, OnlineCharacterSelectOptions characterSelect = null)
        {
            if (setup == null)
            {
                throw new ArgumentNullException(nameof(setup));
            }
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            var options = new OnlineShellOptions
            {
                Configured = setup.Configured,
                ConfigDetail = setup.ConfigDetail,
                DefaultChannelPort = setup.DefaultChannelPort,
                Art = art,
                Log = log,
                InitialUsername = setup.PrefillUser,
                Fonts = fonts,
                CharacterSelect = characterSelect,
            };
            if (serverUi)
            {
                options.AutoPassPlaceholders = false;
                options.ChannelDirectory = backend;
                options.PickerScene = picker;
            }
            _flow = new OnlineShellFlow(backend, options);
            if (!string.IsNullOrEmpty(setup.PrefillPassword))
            {
                _flow.Login.Prefill(setup.PrefillUser ?? string.Empty, setup.PrefillPassword);
            }
        }

        public OnlineShellFlow Flow => _flow;

        public OnlineShellStage Stage => _flow.Stage;

        /// <summary>外壳已经把控制权交给游戏(<see cref="OnlineShellStage.InGame"/>)。</summary>
        public bool InGame => _flow.Stage == OnlineShellStage.InGame;

        /// <summary>玩家在选角(或选频道)界面点了"结束游戏"。</summary>
        public bool QuitRequested => _flow.QuitRequested;

        /// <summary>
        /// 当前的联网会话(登录之后到放弃之前);进入游戏之后它还活着,给以后的进副本线用(<c>EnterExclusiveReceiveAsync</c>)。没有会话时为 null。
        /// </summary>
        public OnlineClientSession Session => _backend.Session;

        /// <summary>最近一次成功选角进城的结果(<c>Load</c> 含 <c>Village</c> 与 <c>UniqueId</c>);未进城时为 null。</summary>
        public SelectCharacterResult SelectCharacter => _backend.SelectCharacter;

        /// <summary>最近一次成功选角进城的快照诊断;未进城时为 null。</summary>
        public TownEntrySnapshot Snapshot => _backend.Snapshot;

        /// <summary>最近一次成功选角收到的副本权限快照;未进城时为 null。</summary>
        public DungeonPermissionSnapshot DungeonPermissions => _backend.DungeonPermissions;

        /// <summary>每帧调一次(渲染线程):先把这一帧的输入交给外壳,再推动选角界面的动画,最后轮询阶段机。<paramref name="nowMs"/> 要单调递增。</summary>
        public void Tick(OnlineShellFrameInput input, double deltaMs, long nowMs)
        {
            if (_disposed)
            {
                return;
            }
            OnlineShellInput.Apply(_flow, input);
            _flow.Advance(deltaMs);
            _flow.Poll(nowMs);
            if (_flow.Stage == OnlineShellStage.Login)
            {
                _rememberPending = true;     // 停在登录界面:下一次登录成功的账号可以记下来
            }
        }

        /// <summary>
        /// 每帧 <see cref="Tick"/> 之后问一次:屏幕该换了吗。外壳阶段每变一次(序号变了)要换一次视图;进入游戏时第一次是 <see cref="OnlineShellViewChange.StartGame"/>,
        /// 之后(掉线重登、换角色回来)是 <see cref="OnlineShellViewChange.ResumeGame"/>;从游戏里掉回外壳是 <see cref="OnlineShellViewChange.ShowShell"/>
        /// (阶段序号只增不减,进过游戏再回外壳时序号一定不同于上次造外壳视图时的)。取走即清:同一个变化只说一次。
        /// </summary>
        public OnlineShellViewChange TakeViewChange()
        {
            if (_flow.Stage == OnlineShellStage.InGame)
            {
                if (_gameShown)
                {
                    return OnlineShellViewChange.None;
                }
                _gameShown = true;
                if (_gameStarted)
                {
                    return OnlineShellViewChange.ResumeGame;
                }
                _gameStarted = true;
                return OnlineShellViewChange.StartGame;
            }
            _gameShown = false;
            if (_flow.StageSerial == _shownSerial)
            {
                return OnlineShellViewChange.None;
            }
            _shownSerial = _flow.StageSerial;
            return OnlineShellViewChange.ShowShell;
        }

        /// <summary>
        /// 登录成功之后第一次问时交出登录用的账号(之后为 null,直到再回到登录界面又登录成功):宿主据此记下"上次用的账号"(只记账号,口令从不记)。
        /// 还停在登录界面、或这一轮登录的账号已经交出过,返回 null。
        /// </summary>
        public string TakeLoggedInUsername()
        {
            if (!_rememberPending || _flow.Stage == OnlineShellStage.Login)
            {
                return null;
            }
            _rememberPending = false;
            string name = _flow.Login.Username;
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>扩展点:进入游戏之后换角色(回到选频道,重连、重新取角色列表)。</summary>
        public void SwapCharacter()
        {
            _flow.SwapCharacter();
        }

        /// <summary>扩展点:城镇系统菜单选"回选角",重连当前频道并立刻刷新角色列表。</summary>
        public void ReturnToCharacterSelect()
        {
            _flow.ReturnToCharacterSelect();
        }

        /// <summary>
        /// 主动退出游戏时先关闭联网会话到频道服务器的连接(<see cref="OnlineShellBackend.DisconnectGracefullyAsync"/>,只关这条 TCP、网关登录保留),再关闭外壳并释放后端。
        /// <b>不发 <c>1/3</c></b>(联网层也没有 <c>1/3</c> 的构造器),真服会不会把这样的收尾当异常掉线是未验证的。调用之后宿主处于已释放状态(再调、再 <see cref="Tick"/> 都是空操作)。
        /// </summary>
        public async Task DisconnectForQuitAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await _backend.DisconnectGracefullyAsync(cancellationToken).ConfigureAwait(false);
            _flow.Shutdown();
            _backend.Dispose();
        }

        /// <summary>退出:放弃会话(最多等一秒让后台释放做完)。可重复调用。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _flow.Shutdown();
            _backend.Dispose();
        }
    }
}
