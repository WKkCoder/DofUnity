using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>外壳认的按键(宿主把键盘翻译成它;文字输入走 <see cref="OnlineShellFlow.Type"/>)。</summary>
    public enum OnlineShellKey
    {
        Enter = 0,
        Escape = 1,
        Tab = 2,
        ShiftTab = 3,
        Up = 4,
        Down = 5,
        Left = 6,
        Right = 7,
        Space = 8,
    }

    /// <summary>联网外壳的设置。</summary>
    public sealed class OnlineShellOptions
    {
        /// <summary>
        /// 连接配置是否可用。为 false 时登录界面一开始就显示"未配置连接"(<see cref="ConfigDetail"/> 点名哪一项有问题),
        /// 任何提交都只给提示、<b>不发任何请求</b>。
        /// </summary>
        public bool Configured { get; set; } = true;

        /// <summary>配置有问题时的补充说明(只点名配置项,不含配置的值)。</summary>
        public string ConfigDetail { get; set; }

        /// <summary>默认频道端口(1 到 65535);选服务器与选频道是占位时用它连接。</summary>
        public int DefaultChannelPort { get; set; } = 30011;

        /// <summary>一个网络操作最长等多久(毫秒),超时就放弃。网关登录最多 15 秒 × 2、连频道与进游戏各有十几秒的窗口,所以默认 60 秒。</summary>
        public long TimeoutMs { get; set; } = 60000;

        /// <summary>选服务器与选频道是占位阶段:为 true(默认)时登录成功后自动放行(用默认频道连接);为 false 时停在那里等 <see cref="OnlineShellFlow.ChooseServer"/> 与 <see cref="OnlineShellFlow.ChooseChannel"/>。</summary>
        public bool AutoPassPlaceholders { get; set; } = true;

        /// <summary>阶段日志的去处(每次阶段变化一行:DNF_ONLINE stage=… event=… reason=…);为 null 不记。日志里没有账号、口令、地址、令牌、角色名。</summary>
        public Action<string> Log { get; set; }

        /// <summary>选角界面的素材(图包、头像、光束);也是登录界面与状态画面的背景来源。为 null 时全用纯色与点阵字占位。</summary>
        public CharacterSelectScene Art { get; set; }

        /// <summary>预填的账号(口令从不预填,除非宿主为本机开发特意调 <see cref="LoginScreen.Prefill"/>)。</summary>
        public string InitialUsername { get; set; }

        /// <summary>外壳自己的画面(登录、状态、提示叠层)用的字体集(测试用);为 null 时用全进程共用的(<see cref="OverlayArt.SharedFonts"/>)。</summary>
        public IGlyphFonts Fonts { get; set; }

        /// <summary>
        /// 选服务器、选频道真界面的目录来源(可选注入)。为 null(默认)时这两个阶段还是原来的占位,行为不变;
        /// 不为 null 时这两个阶段显示 <see cref="ServerSelectScreen"/> 与 <see cref="ChannelSelectScreen"/>,
        /// 此时 <see cref="AutoPassPlaceholders"/> 必须是 false(否则构造抛 <see cref="ArgumentException"/>)。
        /// </summary>
        public IChannelDirectoryBackend ChannelDirectory { get; set; }

        /// <summary>真界面的素材(图包、光束、提示条光);为 null 时用 <see cref="Art"/> 的图包与光束,没有 <see cref="Art"/> 就画程序底板。只在 <see cref="ChannelDirectory"/> 不为 null 时有用。</summary>
        public ServerPickerScene PickerScene { get; set; }
    }

    /// <summary>本地的输入检查(没过就不发请求)。账号规则与网关一致:4 到 32 个 ASCII 字母、数字或下划线。</summary>
    public static class OnlineShellValidation
    {
        public const int UsernameMinLength = 4;
        public const int UsernameMaxLength = 32;

        public static bool IsValidUsername(string username)
        {
            if (username == null || username.Length < UsernameMinLength || username.Length > UsernameMaxLength)
            {
                return false;
            }
            foreach (char c in username)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>口令:不能是空的(网关对口令内容与长度没有别的要求,只发它的 MD5)。</summary>
        public static bool IsValidPassword(string password)
        {
            return !string.IsNullOrEmpty(password);
        }
    }

    /// <summary>服务端角色列表 → 外壳自己的占位选角画面的名册(名字与职业用中性占位,真正的选角界面由后续线接手)。</summary>
    public static class OnlineShellRoster
    {
        /// <summary>
        /// 占位名册:每个角色一格,按列表顺序(第 i 个角色就是槽位 i),最多 <see cref="CharacterRoster.Capacity"/> 个;
        /// 名字写 Role加序号、职业写 Job加职业编号、等级取列表里的(不显示服务端给的名字:字库画不出它,日志与截图里也不该有)。
        /// </summary>
        public static CharacterRoster Placeholder(OnlineCharacterList list)
        {
            var slots = new List<CharacterSlot>();
            if (list != null)
            {
                for (int i = 0; i < list.Entries.Count && i < CharacterRoster.Capacity; i++)
                {
                    OnlineCharacterEntry entry = list.Entries[i];
                    slots.Add(new CharacterSlot("Role" + (i + 1), "Job" + entry.Job, entry.Level));
                }
            }
            return new CharacterRoster(slots);
        }
    }

    /// <summary>
    /// 联网外壳阶段机(纯 C#,不碰引擎):<c>Login → ServerSelect → ChannelSelect → CharacterSelect → Loading → InGame</c>。
    /// <list type="bullet">
    /// <item>登录(<see cref="LoginScreen"/>):账号口令换令牌并取游戏服地址。失败留在登录,按类别提示(账号口令错、被封、限流带倒计时、网络或超时、服务器协议错、没有游戏服地址、
    /// 本地输入不合规、连接配置缺失;后两种不发请求)。登录成功后选服务器与选频道是占位,自动放行并用默认频道连接(<see cref="OnlineShellOptions.AutoPassPlaceholders"/>)。</item>
    /// <item>选角:连上频道取回角色列表(<see cref="Characters"/>,原样不截断);占位选角画面复用 <see cref="CharacterSelectScreen"/>。"选择频道"按钮回到选频道;"结束游戏"按钮是退出(<see cref="QuitRequested"/>)。
    /// 确认角色进入加载(选角请求在途),成功进入游戏(<see cref="OnlineShellStage.InGame"/>:外壳把控制权交给宿主的既有 <see cref="GameFlow"/>)。</item>
    /// <item>Esc(返回/取消)在各阶段:登录里取消在途请求(修改密码形态里回到登录形态);选服务器回登录;选频道里取消在途连接,没有在途就回登录(占位阶段不显示,所以一步退回登录);
    /// 选角回登录(放弃会话);加载里取消选角请求并回到选频道重连;进入游戏之后外壳不处理按键。回到登录总会放弃会话,下次登录是全新的会话。</item>
    /// <item>连接断开(<see cref="IOnlineBackend.Disconnected"/> 事件,或某阶段的方法抛 <see cref="IOException"/>)从选服务器起任何阶段(含进入游戏之后)都回到登录并提示;登录阶段本身没有连接,网络错误只提示。</item>
    /// </list>
    /// 所有等待都是"开一个 Task,之后在 <see cref="Poll"/> 里查它完成没有",绝不阻塞渲染线程;阶段只在 <see cref="Poll"/> 里变(输入方法只记意图,断线事件只置标志),
    /// 所以后台线程完成的结果从不在后台线程上被处理。取消与超时放弃在途的任务(它的结果被丢弃),并让后端放弃会话。没有任何自动重试。
    /// </summary>
    public sealed partial class OnlineShellFlow
    {
        private enum OpKind
        {
            Login,
            Register,
            ChangePassword,
            Connect,
            Select,
            Directory,
        }

        private sealed class Operation
        {
            public OpKind Kind;
            public Task Task;
            public CancellationTokenSource Cts;
            public long StartedMs;
        }

        private readonly IOnlineBackend _backend;
        private readonly OnlineShellOptions _options;
        private readonly LoginScreen _login;
        private readonly OnlineShellStatusScreen _status;
        private readonly ChannelTarget _defaultChannel;
        private Operation _op;
        private int _disconnectFlag;
        private long _now;
        private long _retryUntilMs = -1;
        private bool _shutdown;

        // 输入意图(输入方法只记,Poll 里处理)
        private bool _backIntent;
        private bool _retryIntent;
        private bool _channelIntent;
        private bool _serverIntent;
        private bool _swapIntent;
        private ChannelTarget _chosenChannel;

        // 选频道阶段
        private bool _channelHold;
        private bool _channelPending;
        private ChannelTarget _channelTarget;
        private bool _hasLink;

        private CharacterSelectScreen _select;
        private OnlineShellNoticeOverlay _selectScene;
        private ISceneSource _scene;

        public OnlineShellFlow(IOnlineBackend backend, OnlineShellOptions options = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _options = options ?? new OnlineShellOptions();
            _defaultChannel = new ChannelTarget(_options.DefaultChannelPort);
            if (_options.TimeoutMs < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "超时必须是正数");
            }
            ISpriteSource sprites = _options.Art?.Sprites;
            _login = new LoginScreen(sprites, _options.Fonts);
            _status = new OnlineShellStatusScreen(sprites, _options.Fonts);
            PickerInit();
            _backend.Disconnected += OnBackendDisconnected;
            if (!string.IsNullOrEmpty(_options.InitialUsername))
            {
                _login.SetUsername(_options.InitialUsername);
            }
            SelectedSlot = -1;
            EnterStage(OnlineShellStage.Login, "start");
            if (!_options.Configured)
            {
                SetNotice(new OnlineNotice(OnlineNoticeKind.NotConfigured, detail: _options.ConfigDetail));
                Log(OnlineShellStage.Login, "fail", OnlineNotices.Code(OnlineNoticeKind.NotConfigured));
            }
        }

        public OnlineShellStage Stage { get; private set; }

        /// <summary>每次阶段变化(含重新进入同一阶段)加一;宿主靠它判断要不要换画面。</summary>
        public int StageSerial { get; private set; }

        /// <summary>当前阶段要画的东西;<see cref="OnlineShellStage.InGame"/> 时是 null(宿主接手)。</summary>
        public ISceneSource Scene => _scene;

        /// <summary>登录界面(常驻:账号输入在失败与重新登录之间保留)。</summary>
        public LoginScreen Login => _login;

        /// <summary>有网络操作在途。</summary>
        public bool Busy => _op != null;

        /// <summary>玩家点了选角界面上的"结束游戏":宿主据此退出。</summary>
        public bool QuitRequested { get; private set; }

        /// <summary>当前的提示;没有为 null。</summary>
        public OnlineNotice Notice { get; private set; }

        /// <summary>服务端的角色列表(原样、不截断;连上频道之后才有,回到登录时清掉);没有为 null。</summary>
        public OnlineCharacterList Characters { get; private set; }

        /// <summary>选了第几个角色(列表里的位置);没选是 -1。</summary>
        public int SelectedSlot { get; private set; }

        /// <summary>选中的角色条目;没选是 null。</summary>
        public OnlineCharacterEntry SelectedCharacter =>
            Characters != null && SelectedSlot >= 0 && SelectedSlot < Characters.Entries.Count ? Characters.Entries[SelectedSlot] : null;

        // ------------------------------------------------------------------ 输入(只记意图,Poll 里处理)

        /// <summary>键盘文字输入;只有登录阶段用它。</summary>
        public void Type(string text)
        {
            if (!_shutdown && Stage == OnlineShellStage.Login)
            {
                _login.Type(text);
            }
        }

        /// <summary>按键。</summary>
        public void Key(OnlineShellKey key)
        {
            if (_shutdown)
            {
                return;
            }
            switch (Stage)
            {
                case OnlineShellStage.Login:
                    switch (key)
                    {
                        case OnlineShellKey.Enter: _login.Enter(); break;
                        case OnlineShellKey.Tab: _login.Tab(); break;
                        case OnlineShellKey.ShiftTab: _login.Tab(true); break;
                        case OnlineShellKey.Escape: _backIntent = true; break;
                    }
                    break;
                case OnlineShellStage.ServerSelect:
                case OnlineShellStage.ChannelSelect:
                case OnlineShellStage.Loading:
                    if (PickerOwns(Stage) && PickerKey(key))
                    {
                        break;
                    }
                    if (key == OnlineShellKey.Escape)
                    {
                        _backIntent = true;
                    }
                    else if (key == OnlineShellKey.Enter)
                    {
                        _retryIntent = true;
                    }
                    break;
                case OnlineShellStage.CharacterSelect:
                    switch (key)
                    {
                        case OnlineShellKey.Left: _select.Move(-1, 0); break;
                        case OnlineShellKey.Right: _select.Move(1, 0); break;
                        case OnlineShellKey.Up: _select.Move(0, -1); break;
                        case OnlineShellKey.Down: _select.Move(0, 1); break;
                        case OnlineShellKey.Enter:
                        case OnlineShellKey.Space: _select.Confirm(); break;
                        case OnlineShellKey.Escape: _backIntent = true; break;
                    }
                    break;
            }
        }

        /// <summary>鼠标(触摸)点在 800×600 画布坐标 (<paramref name="x"/>, <paramref name="y"/>)。</summary>
        public void Click(int x, int y)
        {
            if (_shutdown)
            {
                return;
            }
            if (Stage == OnlineShellStage.Login)
            {
                _login.Click(x, y);
            }
            else if (PickerOwns(Stage))
            {
                PickerClick(x, y);
            }
            else if (Stage == OnlineShellStage.CharacterSelect)
            {
                if (_options.Art != null && _select.Outcome == CharacterSelectOutcome.None
                    && CharacterSelectArt.HitButton(x, y) == CharacterSelectButton.Channel)
                {
                    _channelIntent = true;
                    return;
                }
                _select.Click(x, y);
            }
        }

        /// <summary>选角界面的动画往前走(宿主每帧传帧间隔毫秒)。</summary>
        public void Advance(double deltaMs)
        {
            if (Stage == OnlineShellStage.CharacterSelect && _select != null)
            {
                _select.Advance(deltaMs);
            }
            PickerAdvance(deltaMs);
        }

        /// <summary>扩展点(选服务器界面):选定服务器,进入选频道。只在选服务器阶段有效。</summary>
        public void ChooseServer()
        {
            _serverIntent = true;
        }

        /// <summary>扩展点(选频道界面):连这个频道(已经连着就是换频道)。只在选频道阶段、没有在途请求时有效。</summary>
        public void ChooseChannel(ChannelTarget target)
        {
            _chosenChannel = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>宿主:进入游戏之后换角色——回到选频道,重连并重新取角色列表。只在进入游戏阶段有效。</summary>
        public void SwapCharacter()
        {
            _swapIntent = true;
        }

        /// <summary>连接掉线的通知(<see cref="IOnlineBackend.Disconnected"/> 订阅的就是它);可以在任何线程上调,只置标志,处理在 <see cref="Poll"/> 里。</summary>
        public void NotifyDisconnected()
        {
            Interlocked.Exchange(ref _disconnectFlag, 1);
        }

        /// <summary>宿主退出:放弃会话、取消在途请求、不再响应。可重复调用。</summary>
        public void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            _backend.Disconnected -= OnBackendDisconnected;
            DropOperation();
            _backend.Disconnect();
        }

        // ------------------------------------------------------------------ 轮询(渲染线程每帧调一次)

        /// <summary>
        /// 推进阶段机一步。<paramref name="nowMs"/> 是单调递增的毫秒(宿主用 Stopwatch;测试给合成的时间)。顺序:先处理断线,再处理这一帧记下的输入意图,
        /// 再查在途任务是否完成(或超时),最后放行占位阶段、刷新倒计时。
        /// </summary>
        public void Poll(long nowMs)
        {
            if (_shutdown)
            {
                return;
            }
            _now = nowMs;
            if (Interlocked.Exchange(ref _disconnectFlag, 0) != 0 && Stage != OnlineShellStage.Login)
            {
                GoToLogin(new OnlineNotice(OnlineNoticeKind.Disconnected), "disconnected");
            }
            ProcessIntents();
            if (_op != null)
            {
                if (_op.Task.IsCompleted)
                {
                    Harvest();
                }
                else if (_now - _op.StartedMs >= _options.TimeoutMs)
                {
                    HandleTimeout();
                }
            }
            AutoPass();
            UpdateCountdown();
            PickerSync();
        }

        private void OnBackendDisconnected()
        {
            NotifyDisconnected();
        }

        // ------------------------------------------------------------------ 意图

        private void ProcessIntents()
        {
            bool back = _backIntent;
            bool retry = _retryIntent;
            bool channel = _channelIntent;
            bool server = _serverIntent;
            bool swap = _swapIntent;
            ChannelTarget chosen = _chosenChannel;
            _backIntent = false;
            _retryIntent = false;
            _channelIntent = false;
            _serverIntent = false;
            _swapIntent = false;
            _chosenChannel = null;
            PickerIntents(ref back, ref server, ref chosen);

            switch (Stage)
            {
                case OnlineShellStage.Login:
                    LoginIntents(back);
                    break;
                case OnlineShellStage.ServerSelect:
                    if (back)
                    {
                        GoToLogin(null, "back");
                    }
                    else if (server)
                    {
                        Log(OnlineShellStage.ServerSelect, "ok", "choose");
                        EnterStage(OnlineShellStage.ChannelSelect, "choose");
                    }
                    break;
                case OnlineShellStage.ChannelSelect:
                    ChannelIntents(back, retry, chosen);
                    break;
                case OnlineShellStage.CharacterSelect:
                    CharacterSelectIntents(back, channel);
                    break;
                case OnlineShellStage.Loading:
                    if (back)
                    {
                        DropOperation();
                        _hasLink = false;
                        Log(OnlineShellStage.Loading, "fail", OnlineNotices.Code(OnlineNoticeKind.Cancelled));
                        EnterStage(OnlineShellStage.ChannelSelect, "cancelled");
                    }
                    break;
                case OnlineShellStage.InGame:
                    if (swap)
                    {
                        Log(OnlineShellStage.InGame, "ok", "swap_character");
                        EnterStage(OnlineShellStage.ChannelSelect, "swap_character");
                    }
                    break;
            }
        }

        private void LoginIntents(bool back)
        {
            if (back)
            {
                if (_op != null)
                {
                    bool wasLogin = _op.Kind == OpKind.Login;
                    DropOperation();
                    _login.SetBusy(false);
                    if (wasLogin)
                    {
                        _backend.Disconnect();       // 登录请求可能已经在后端建了会话:放弃它;注册与改密没有会话可放
                    }
                    SetNotice(new OnlineNotice(OnlineNoticeKind.Cancelled));
                    Log(OnlineShellStage.Login, "fail", OnlineNotices.Code(OnlineNoticeKind.Cancelled));
                }
                else if (_login.Mode == LoginMode.ChangePassword)
                {
                    _login.SetMode(LoginMode.Login);
                }
            }
            LoginAction action = _login.TakeAction();
            if (action != LoginAction.None && _op == null)
            {
                StartAuth(action);
            }
        }

        private void StartAuth(LoginAction action)
        {
            if (!_options.Configured)
            {
                SetNotice(new OnlineNotice(OnlineNoticeKind.NotConfigured, detail: _options.ConfigDetail));
                Log(OnlineShellStage.Login, "fail", OnlineNotices.Code(OnlineNoticeKind.NotConfigured));
                return;
            }
            OnlineNoticeKind invalid = Validate(action);
            if (invalid != OnlineNoticeKind.None)
            {
                SetNotice(new OnlineNotice(invalid));
                Log(OnlineShellStage.Login, "fail", OnlineNotices.Code(invalid));
                return;
            }
            if (_retryUntilMs >= 0 && _now < _retryUntilMs)
            {
                SetNotice(new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: SecondsLeft()));
                Log(OnlineShellStage.Login, "fail", OnlineNotices.Code(OnlineNoticeKind.RateLimited));
                return;
            }
            string user = _login.Username;
            string password = _login.Password;
            string newPassword = _login.NewPassword;
            SetNotice(null);
            _login.SetBusy(true);
            switch (action)
            {
                case LoginAction.Login:
                    _op = Begin(OpKind.Login, ct => _backend.LoginAsync(user, password, ct));
                    break;
                case LoginAction.Register:
                    _op = Begin(OpKind.Register, ct => _backend.RegisterAsync(user, password, ct));
                    break;
                default:
                    _op = Begin(OpKind.ChangePassword, ct => _backend.ChangePasswordAsync(user, password, newPassword, ct));
                    break;
            }
        }

        private OnlineNoticeKind Validate(LoginAction action)
        {
            if (!OnlineShellValidation.IsValidUsername(_login.Username))
            {
                return OnlineNoticeKind.InvalidUsername;
            }
            if (!OnlineShellValidation.IsValidPassword(_login.Password))
            {
                return OnlineNoticeKind.InvalidPassword;
            }
            if (action == LoginAction.ChangePassword && !OnlineShellValidation.IsValidPassword(_login.NewPassword))
            {
                return OnlineNoticeKind.InvalidPassword;
            }
            return OnlineNoticeKind.None;
        }

        private void ChannelIntents(bool back, bool retry, ChannelTarget chosen)
        {
            if (back)
            {
                if (_op != null)
                {
                    DropOperation();
                    _hasLink = false;
                    _channelHold = true;
                    SetNotice(new OnlineNotice(OnlineNoticeKind.Cancelled));
                    UpdateStatus();
                    Log(OnlineShellStage.ChannelSelect, "fail", OnlineNotices.Code(OnlineNoticeKind.Cancelled));
                }
                else if (_options.AutoPassPlaceholders)
                {
                    GoToLogin(null, "back");
                }
                else
                {
                    EnterStage(OnlineShellStage.ServerSelect, "back");
                }
                return;
            }
            if (_op != null)
            {
                return;
            }
            if (chosen != null)
            {
                StartConnect(chosen);
            }
            else if (retry && _channelHold)
            {
                StartConnect(_channelTarget ?? _defaultChannel);
            }
        }

        private void CharacterSelectIntents(bool back, bool channel)
        {
            if (_select.Outcome == CharacterSelectOutcome.Quit)
            {
                QuitRequested = true;
                return;
            }
            if (channel)
            {
                Log(OnlineShellStage.CharacterSelect, "ok", "channel");
                EnterStage(OnlineShellStage.ChannelSelect, "channel");
                return;
            }
            if (back)
            {
                if (_options.AutoPassPlaceholders)
                {
                    GoToLogin(null, "back");
                }
                else
                {
                    EnterStage(OnlineShellStage.ChannelSelect, "back");
                }
                return;
            }
            if (_select.Outcome == CharacterSelectOutcome.Start)
            {
                StartSelect(_select.Index);
            }
        }

        // ------------------------------------------------------------------ 发起操作

        private Operation Begin<T>(OpKind kind, Func<CancellationToken, Task<T>> start)
        {
            var cts = new CancellationTokenSource();
            Task<T> task;
            try
            {
                task = start(cts.Token) ?? Task.FromException<T>(new InvalidOperationException("后端没有返回任务"));
            }
            catch (Exception e)
            {
                task = Task.FromException<T>(e);
            }
            task.ContinueWith(t =>
            {
                _ = t.Exception;
                cts.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new Operation { Kind = kind, Task = task, Cts = cts, StartedMs = _now };
        }

        private void StartConnect(ChannelTarget target)
        {
            _channelPending = false;
            _channelHold = false;
            _channelTarget = target;
            SetNotice(null);
            UpdateStatus();
            _op = _hasLink
                ? Begin(OpKind.Connect, ct => _backend.SwitchChannelAsync(target, ct))
                : Begin(OpKind.Connect, ct => _backend.ConnectChannelAsync(target, ct));
        }

        private void StartSelect(int slot)
        {
            SelectedSlot = slot;
            Log(OnlineShellStage.CharacterSelect, "ok", "confirm");
            EnterStage(OnlineShellStage.Loading, "select");
            _op = Begin(OpKind.Select, ct => _backend.SelectCharacterAsync(slot, ct));
        }

        private void DropOperation()
        {
            Operation op = _op;
            _op = null;
            if (op == null)
            {
                return;
            }
            try
            {
                op.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 任务刚好完成、令牌源已经释放:没有要取消的了
            }
        }

        // ------------------------------------------------------------------ 收结果

        private void Harvest()
        {
            Operation op = _op;
            _op = null;
            Exception error = TaskError(op.Task);
            switch (op.Kind)
            {
                case OpKind.Login:
                case OpKind.Register:
                case OpKind.ChangePassword:
                    FinishAuth(op, error);
                    break;
                case OpKind.Connect:
                    FinishConnect(op, error);
                    break;
                case OpKind.Directory:
                    FinishDirectory(op, error);
                    break;
                default:
                    FinishSelect(op, error);
                    break;
            }
        }

        private static Exception TaskError(Task task)
        {
            if (task.IsFaulted)
            {
                return task.Exception.GetBaseException();
            }
            return task.IsCanceled ? new OperationCanceledException() : null;
        }

        private static bool IsConnectionError(Exception error)
        {
            return error is IOException || error is SocketException;
        }

        /// <summary>异常对应的提示:被取消(后端自己的超时)算网络超时,其它意外算意外错误。连接类异常由调用方先处理。</summary>
        private static OnlineNotice NoticeForError(Exception error)
        {
            if (IsConnectionError(error))
            {
                return new OnlineNotice(OnlineNoticeKind.NetworkError);
            }
            if (error is OperationCanceledException)
            {
                return new OnlineNotice(OnlineNoticeKind.NetworkError, "timeout");
            }
            return new OnlineNotice(OnlineNoticeKind.Unexpected);
        }

        private void FinishAuth(Operation op, Exception error)
        {
            _login.SetBusy(false);
            OnlineAuthOutcome outcome = null;
            OnlineNotice failure = null;
            if (error != null)
            {
                failure = NoticeForError(error);
            }
            else
            {
                outcome = ((Task<OnlineAuthOutcome>)op.Task).Result;
                if (outcome == null)
                {
                    failure = new OnlineNotice(OnlineNoticeKind.Unexpected);
                }
                else if (!outcome.Success)
                {
                    failure = new OnlineNotice(outcome.Failure);
                    if (outcome.Failure == OnlineNoticeKind.RateLimited)
                    {
                        int hold = outcome.RetryAfterSeconds < 1 ? 1 : outcome.RetryAfterSeconds;
                        _retryUntilMs = _now + hold * 1000L;
                        failure = failure.WithSeconds(hold);
                    }
                }
            }
            if (failure != null)
            {
                if (op.Kind == OpKind.Login)
                {
                    _backend.Disconnect();
                }
                SetNotice(failure);
                Log(OnlineShellStage.Login, "fail", failure.Reason);
                return;
            }
            switch (op.Kind)
            {
                case OpKind.Login:
                    _login.ClearSecrets();
                    Log(OnlineShellStage.Login, "ok", "auth");
                    EnterStage(OnlineShellStage.ServerSelect, "login_ok");
                    break;
                case OpKind.Register:
                    SetNotice(new OnlineNotice(OnlineNoticeKind.RegisterOk));
                    Log(OnlineShellStage.Login, "ok", OnlineNotices.Code(OnlineNoticeKind.RegisterOk));
                    break;
                default:
                    _login.ClearSecrets();
                    _login.SetMode(LoginMode.Login);
                    SetNotice(new OnlineNotice(OnlineNoticeKind.PasswordChanged));
                    Log(OnlineShellStage.Login, "ok", OnlineNotices.Code(OnlineNoticeKind.PasswordChanged));
                    break;
            }
        }

        private void FinishConnect(Operation op, Exception error)
        {
            if (error != null && IsConnectionError(error))
            {
                GoToLogin(new OnlineNotice(OnlineNoticeKind.Disconnected), "disconnected");
                return;
            }
            OnlineNotice failure = null;
            OnlineCharacterList list = null;
            if (error != null)
            {
                failure = NoticeForError(error);
            }
            else
            {
                OnlineConnectOutcome outcome = ((Task<OnlineConnectOutcome>)op.Task).Result;
                if (outcome == null)
                {
                    failure = new OnlineNotice(OnlineNoticeKind.Unexpected);
                }
                else if (!outcome.Success)
                {
                    failure = new OnlineNotice(outcome.Failure);
                }
                else
                {
                    list = outcome.Characters;
                }
            }
            if (failure != null && failure.Kind == OnlineNoticeKind.Disconnected)
            {
                GoToLogin(failure, "disconnected");
                return;
            }
            if (failure != null)
            {
                _hasLink = false;
                _channelHold = true;
                SetNotice(failure);
                UpdateStatus();
                Log(OnlineShellStage.ChannelSelect, "fail", failure.Reason);
                return;
            }
            _hasLink = true;
            _channelHold = false;
            Characters = list;
            Log(OnlineShellStage.ChannelSelect, "ok", "connected");
            EnterCharacterSelect(0, list.Entries.Count == 0 ? new OnlineNotice(OnlineNoticeKind.NoCharacters) : null, "connected");
        }

        private void FinishSelect(Operation op, Exception error)
        {
            if (error != null)
            {
                GoToLogin(IsConnectionError(error) ? new OnlineNotice(OnlineNoticeKind.Disconnected) : NoticeForError(error),
                    IsConnectionError(error) ? "disconnected" : "error");
                return;
            }
            OnlineSelectOutcome outcome = ((Task<OnlineSelectOutcome>)op.Task).Result;
            if (outcome == null)
            {
                GoToLogin(new OnlineNotice(OnlineNoticeKind.Unexpected), "error");
                return;
            }
            if (outcome.Entered)
            {
                Log(OnlineShellStage.Loading, "ok", "selected");
                EnterStage(OnlineShellStage.InGame, "handover");
                return;
            }
            if (!outcome.MayTryAnotherSlot || outcome.Failure == OnlineNoticeKind.Disconnected)
            {
                GoToLogin(new OnlineNotice(OnlineNoticeKind.Disconnected), "disconnected");
                return;
            }
            var notice = new OnlineNotice(outcome.Failure);
            Log(OnlineShellStage.Loading, "fail", notice.Reason);
            EnterCharacterSelect(SelectedSlot, notice, "select_failed");
        }

        private void HandleTimeout()
        {
            Operation op = _op;
            DropOperation();
            var notice = new OnlineNotice(OnlineNoticeKind.NetworkError, "timeout");
            switch (op.Kind)
            {
                case OpKind.Login:
                    _login.SetBusy(false);
                    _backend.Disconnect();
                    SetNotice(notice);
                    Log(OnlineShellStage.Login, "fail", notice.Reason);
                    break;
                case OpKind.Register:
                case OpKind.ChangePassword:
                    _login.SetBusy(false);
                    SetNotice(notice);
                    Log(OnlineShellStage.Login, "fail", notice.Reason);
                    break;
                case OpKind.Connect:
                    _hasLink = false;
                    _channelHold = true;
                    SetNotice(notice);
                    UpdateStatus();
                    Log(OnlineShellStage.ChannelSelect, "fail", notice.Reason);
                    break;
                case OpKind.Directory:
                    DirectoryFailed(notice);
                    break;
                default:
                    GoToLogin(notice, "timeout");
                    break;
            }
        }

        // ------------------------------------------------------------------ 阶段切换

        private void AutoPass()
        {
            if (!_options.AutoPassPlaceholders || _op != null)
            {
                return;
            }
            if (Stage == OnlineShellStage.ServerSelect)
            {
                Log(OnlineShellStage.ServerSelect, "ok", "auto");
                EnterStage(OnlineShellStage.ChannelSelect, "auto");
            }
            if (Stage == OnlineShellStage.ChannelSelect && _channelPending && !_channelHold)
            {
                StartConnect(_defaultChannel);
            }
        }

        private void EnterStage(OnlineShellStage stage, string reason)
        {
            Stage = stage;
            StageSerial++;
            SetNotice(null);
            if (stage == OnlineShellStage.ChannelSelect)
            {
                _channelPending = true;
                _channelHold = false;
            }
            switch (stage)
            {
                case OnlineShellStage.Login:
                    _scene = _login;
                    break;
                case OnlineShellStage.CharacterSelect:
                    _scene = _selectScene;
                    break;
                case OnlineShellStage.InGame:
                    _scene = null;
                    break;
                default:
                    _scene = _status;
                    break;
            }
            PickerEnter(stage);
            UpdateStatus();
            Log(stage, "enter", reason);
        }

        private void EnterCharacterSelect(int index, OnlineNotice notice, string reason)
        {
            _select = new CharacterSelectScreen(OnlineShellRoster.Placeholder(Characters), _options.Art, index);
            _selectScene = new OnlineShellNoticeOverlay(_select, _options.Fonts);
            EnterStage(OnlineShellStage.CharacterSelect, reason);
            SetNotice(notice);
        }

        /// <summary>放弃会话回到登录(<paramref name="notice"/> 为 null 是玩家主动返回,不提示)。失败原因记在离开的那个阶段上。</summary>
        private void GoToLogin(OnlineNotice notice, string enterReason)
        {
            OnlineShellStage from = Stage;
            DropOperation();
            _backend.Disconnect();
            _hasLink = false;
            _channelHold = false;
            _channelPending = false;
            Characters = null;
            SelectedSlot = -1;
            _select = null;
            _selectScene = null;
            PickerReset();
            _login.SetBusy(false);
            _login.SetMode(LoginMode.Login);
            _login.ClearSecrets();
            if (notice != null)
            {
                Log(from, "fail", notice.Reason);
            }
            EnterStage(OnlineShellStage.Login, enterReason);
            SetNotice(notice);
        }

        private void SetNotice(OnlineNotice notice)
        {
            Notice = notice;
            _login.SetNotice(notice);
            _status.Notice = notice;
            if (_selectScene != null)
            {
                _selectScene.Notice = notice;
            }
            PickerNotice(notice);
        }

        private void UpdateStatus()
        {
            switch (Stage)
            {
                case OnlineShellStage.ServerSelect:
                    _status.Title = OnlineShellLabel.ServerSelect;
                    _status.Hint = OnlineShellLabel.LoadingHint;
                    break;
                case OnlineShellStage.ChannelSelect:
                    _status.Title = OnlineShellLabel.ChannelConnecting;
                    _status.Hint = _channelHold ? OnlineShellLabel.ChannelHint : OnlineShellLabel.ChannelHintCancel;
                    break;
                case OnlineShellStage.Loading:
                    _status.Title = OnlineShellLabel.Loading;
                    _status.Hint = OnlineShellLabel.LoadingHint;
                    break;
            }
        }

        private int SecondsLeft()
        {
            long left = (_retryUntilMs - _now + 999) / 1000;
            return left < 0 ? 0 : (int)left;
        }

        private void UpdateCountdown()
        {
            if (_retryUntilMs < 0)
            {
                return;
            }
            int left = SecondsLeft();
            if (left <= 0)
            {
                _retryUntilMs = -1;
                if (Notice != null && Notice.Kind == OnlineNoticeKind.RateLimited)
                {
                    SetNotice(null);
                }
            }
            else if (Notice != null && Notice.Kind == OnlineNoticeKind.RateLimited && Notice.Seconds != left)
            {
                SetNotice(Notice.WithSeconds(left));
            }
        }

        private void Log(OnlineShellStage stage, string ev, string reason)
        {
            Action<string> sink = _options.Log;
            if (sink == null)
            {
                return;
            }
            try
            {
                sink("DNF_ONLINE stage=" + OnlineShellStages.Code(stage) + " event=" + ev + " reason=" + reason);
            }
            catch (Exception)
            {
                // 日志去处出错不能拖垮流程
            }
        }
    }
}
