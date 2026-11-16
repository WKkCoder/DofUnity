using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>登录界面上玩家触发的、需要外壳去发请求的动作。</summary>
    public enum LoginAction
    {
        None = 0,
        Login = 1,
        Register = 2,
        ChangePassword = 3,
    }

    /// <summary>登录界面的两种形态:登录(账号、密码)与修改密码(账号、旧密码、新密码)。</summary>
    public enum LoginMode
    {
        Login = 0,
        ChangePassword = 1,
    }

    /// <summary>登录界面的输入框。<see cref="Password"/> 在修改密码形态里是"旧密码"。</summary>
    public enum LoginField
    {
        Username = 0,
        Password = 1,
        NewPassword = 2,
    }

    /// <summary>登录界面上的按钮。</summary>
    public enum LoginButton
    {
        None = 0,

        /// <summary>进入游戏(登录)。</summary>
        Login = 1,

        /// <summary>注册。</summary>
        Register = 2,

        /// <summary>切到修改密码形态(只是界面切换,不发请求)。</summary>
        OpenChangePassword = 3,

        /// <summary>确认修改密码。</summary>
        Confirm = 4,

        /// <summary>回到登录形态。</summary>
        Back = 5,
    }

    /// <summary>
    /// 联网登录界面(<see cref="ISceneSource"/>):背景是选角界面的底图(见 <see cref="OnlineShellArt.AddBackground"/>)加暗角,上方是游戏名的标题字,
    /// 中间一张玻璃卡片:账号框、密码框(只画与字数相同的圆点,从不画密码本身)、提示条、金色的"进入游戏"、"注册"与"修改密码",底部是按键说明。
    /// 版面与配色全是 <see cref="ShellPaint"/> 现算的形状,不读任何素材。
    /// 只管输入与画面:Tab 切换输入框,回车提交,鼠标点输入框与按钮;玩家按下提交只是记下一个待办动作(<see cref="PendingAction"/>),
    /// 由外壳(<see cref="OnlineShellFlow"/>)在轮询里取走(<see cref="TakeAction"/>)并发请求。请求在途时(<see cref="Busy"/>)提交类动作一律忽略
    /// (按钮置灰、重复点击无效);界面上不做任何自动重试。错误与提示由外壳放进来(<see cref="SetNotice"/>)并显示在界面上。
    /// 有 CJK 字体时标签是中文,没有时是英文大写占位。
    /// </summary>
    public sealed class LoginScreen : ISceneSource
    {
        public const int ScreenWidth = OnlineShellArt.ScreenWidth;
        public const int ScreenHeight = OnlineShellArt.ScreenHeight;

        /// <summary>账号输入的字数上限(与网关的账号长度上限一致)。</summary>
        public const int MaxUsernameLength = 32;

        /// <summary>密码输入的字数上限(界面自己的限制;网关对密码长度没有上限,只发它的 MD5)。</summary>
        public const int MaxPasswordLength = 64;

        // ---- 版面(800×600 画布)
        public const int PanelWidth = 400;
        public const int PanelX = (ScreenWidth - PanelWidth) / 2;
        public const int PanelY = 128;
        public const int Padding = 32;
        public const int FieldX = PanelX + Padding;
        public const int FieldWidth = PanelWidth - Padding * 2;
        public const int FieldHeight = 44;

        /// <summary>输入框左端标签格的宽度。</summary>
        public const int LabelCellWidth = 80;

        private const int FieldSpacing = 56;
        private const int FirstFieldY = PanelY + 106;
        private const int FieldRadius = 9;
        private const int PrimaryHeight = 46;
        private const int SecondaryHeight = 34;
        private const int BannerPx = 46;

        /// <summary>游戏名标题字所在那条带的顶端与高度:标题字在带里垂直居中,带的底边是标题下那条金线的下沿。</summary>
        public const int BannerY = 22;
        public const int BannerBand = 74;
        private const int BannerGlowInsetX = 20;
        private const int BannerGlowInsetY = 15;
        private const int BannerGlowSpread = 34;
        private const int BlinkMs = 500;

        /// <summary>密码圆点的直径与相邻两个圆点的间距(像素)。</summary>
        public const int MaskSize = 8;
        public const int MaskStep = 14;

        /// <summary>文字区(账号文字、密码圆点、占位文字)离标签格右边与输入框右边的留白。</summary>
        public const int TextInset = 14;

        /// <summary>文字区的左端 x(三个输入框相同)。</summary>
        public const int TextLeft = FieldX + LabelCellWidth + TextInset;

        /// <summary>一个密码框里最多画几个圆点(再多的字符不再画)。</summary>
        public const int MaskCapacity = (FieldWidth - LabelCellWidth - TextInset * 2) / MaskStep;

        private static readonly ShellBox FieldStyle = new ShellBox(0xFF0B1426u, 0xFF0E1A30u, 0xFF3A5A8Au, 0xFF2B456Eu, 1);
        private static readonly ShellBox FieldFocusStyle = new ShellBox(0xFF0B1426u, 0xFF0E1A30u, 0xFFFFD25Eu, 0xFFE0A93Eu, 1);
        private static readonly ShellBox CellStyle = new ShellBox(0xFF1B2D4Eu, 0xFF15233Fu, 0, 0, 0);
        private static readonly ShellBox CellFocusStyle = new ShellBox(0xFF2D2A22u, 0xFF231F18u, 0, 0, 0);
        private static readonly ShellBox PrimaryStyle = new ShellBox(0xFFFFD36Bu, 0xFFE59A2Bu, 0xFFFFE9A8u, 0xFFB8741Au, 1, 46, 0);
        private static readonly ShellBox PrimaryOffStyle = new ShellBox(0xFF46536Bu, 0xFF363F52u, 0xFF5A6882u, 0xFF2F3748u, 1);
        private static readonly ShellBox SecondaryStyle = new ShellBox(0xE01A2C4Au, 0xE0132039u, 0xFF4B72ABu, 0xFF35568Au, 1, 14, 0);
        private static readonly ShellBox SecondaryOffStyle = new ShellBox(0xB018243Au, 0xB0131C2Eu, 0xFF34425Cu, 0xFF2A3650u, 1);
        private static readonly ShellBox PillStyle = new ShellBox(0xFFFFE08Au, 0xFFE0952Au, 0, 0, 0);

        private readonly OverlayArt _art;
        private readonly ShellPaint _paint;
        private readonly ISpriteSource _sprites;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _newPassword = string.Empty;
        private LoginMode _mode;
        private LoginField _focus;
        private LoginAction _pending;
        private bool _busy;
        private OnlineNotice _notice;

        /// <param name="sprites">取背景底图的图包;为 null 时背景是纯色。</param>
        /// <param name="fonts">这张界面自己的字体集(测试用);为 null 时用全进程共用的字体集(<see cref="OverlayArt.SharedFonts"/>,宿主启动时设),都没有就是点阵字库的英文占位。</param>
        public LoginScreen(ISpriteSource sprites = null, IGlyphFonts fonts = null)
        {
            _sprites = sprites;
            _art = new OverlayArt { Localize = false, Fonts = fonts };
            _paint = new ShellPaint(_art);
        }

        public string Username => _username;

        /// <summary>密码(修改密码形态里是旧密码)。只给外壳取了发请求用,界面从不把它画出来。</summary>
        public string Password => _password;

        public string NewPassword => _newPassword;

        public LoginMode Mode => _mode;

        public LoginField Focus => _focus;

        /// <summary>有一个请求在途:提交类动作被忽略,按钮置灰。</summary>
        public bool Busy => _busy;

        /// <summary>还没被外壳取走的动作。</summary>
        public LoginAction PendingAction => _pending;

        /// <summary>界面上显示的提示;没有为 null。</summary>
        public OnlineNotice Notice => _notice;

        // ------------------------------------------------------------------ 几何(点击测试与测试共用)

        /// <summary>卡片的高度:修改密码形态多一个输入框,比登录形态高一格。</summary>
        public static int PanelHeightOf(LoginMode mode)
        {
            return mode == LoginMode.Login ? 388 : 396;
        }

        /// <summary>提示条(出错、提示、等待)的位置与大小:紧接在最后一个输入框下面,占满输入框的宽度。</summary>
        public static HitRect NoticeRect(LoginMode mode)
        {
            return new HitRect(FieldX, FirstFieldY + FieldSpacing * (mode == LoginMode.Login ? 2 : 3), FieldWidth, OnlineShellArt.NoticeBarHeight);
        }

        /// <summary>输入框的位置与大小(账号、密码/旧密码、新密码从上到下)。</summary>
        public static HitRect FieldRect(LoginField field)
        {
            return new HitRect(FieldX, FirstFieldY + FieldSpacing * (int)field, FieldWidth, FieldHeight);
        }

        /// <summary>按钮的位置与大小。登录形态有登录(整行宽的金色大按钮)、注册、修改密码三个;修改密码形态有确认与返回两个。</summary>
        public static HitRect ButtonRect(LoginButton button)
        {
            switch (button)
            {
                case LoginButton.Login:
                    return new HitRect(FieldX, PanelY + 262, FieldWidth, PrimaryHeight);
                case LoginButton.Register:
                    return new HitRect(FieldX, PanelY + 322, 162, SecondaryHeight);
                case LoginButton.OpenChangePassword:
                    return new HitRect(FieldX + 174, PanelY + 322, 162, SecondaryHeight);
                case LoginButton.Confirm:
                    return new HitRect(FieldX, PanelY + 318, 206, PrimaryHeight);
                case LoginButton.Back:
                    return new HitRect(FieldX + 218, PanelY + 318, 118, PrimaryHeight);
                default:
                    return new HitRect(0, 0, 0, 0);
            }
        }

        /// <summary>画布坐标落在 <paramref name="mode"/> 形态里的哪个按钮上;不在任何按钮上是 <see cref="LoginButton.None"/>。</summary>
        public static LoginButton HitButton(LoginMode mode, int x, int y)
        {
            LoginButton[] buttons = mode == LoginMode.Login
                ? new[] { LoginButton.Login, LoginButton.Register, LoginButton.OpenChangePassword }
                : new[] { LoginButton.Confirm, LoginButton.Back };
            foreach (LoginButton button in buttons)
            {
                if (ButtonRect(button).Contains(x, y))
                {
                    return button;
                }
            }
            return LoginButton.None;
        }

        /// <summary>画布坐标落在 <paramref name="mode"/> 形态里的哪个输入框上。</summary>
        public static bool HitField(LoginMode mode, int x, int y, out LoginField field)
        {
            int count = mode == LoginMode.Login ? 2 : 3;
            for (int i = 0; i < count; i++)
            {
                if (FieldRect((LoginField)i).Contains(x, y))
                {
                    field = (LoginField)i;
                    return true;
                }
            }
            field = LoginField.Username;
            return false;
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>
        /// 键盘文字输入(宿主把这一帧的输入字符串原样交进来):退格 '\b' 删最后一个字符,其它控制字符(回车、制表、转义……)忽略,
        /// 可打印字符追加到当前输入框,超过字数上限的丢掉。请求在途时也能输入(外壳已经取走了发请求用的那份)。
        /// </summary>
        public void Type(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            foreach (char c in text)
            {
                if (c == '\b')
                {
                    Backspace();
                }
                else if (c >= ' ' && c != '\u007f')
                {
                    Append(c);
                }
            }
        }

        /// <summary>删掉当前输入框的最后一个字符。</summary>
        public void Backspace()
        {
            string value = Current();
            if (value.Length > 0)
            {
                SetCurrent(value.Substring(0, value.Length - 1));
            }
        }

        /// <summary>焦点切到下一个(<paramref name="backward"/> 为 true 时上一个)输入框,到头循环。</summary>
        public void Tab(bool backward = false)
        {
            int count = _mode == LoginMode.Login ? 2 : 3;
            int next = ((int)_focus + (backward ? count - 1 : 1)) % count;
            _focus = (LoginField)next;
        }

        /// <summary>回车:提交当前形态的动作(登录形态是登录,修改密码形态是改密)。请求在途或已有待办动作时忽略。</summary>
        public void Enter()
        {
            Submit(_mode == LoginMode.Login ? LoginAction.Login : LoginAction.ChangePassword);
        }

        /// <summary>
        /// 鼠标(触摸)点在画布坐标 (<paramref name="x"/>, <paramref name="y"/>):点输入框设置焦点;点按钮触发它(请求在途或已有待办动作时按钮无效,但点击仍算落在界面上)。
        /// 返回这一下有没有落在输入框或按钮上。
        /// </summary>
        public bool Click(int x, int y)
        {
            if (HitField(_mode, x, y, out LoginField field))
            {
                _focus = field;
                return true;
            }
            LoginButton button = HitButton(_mode, x, y);
            if (button == LoginButton.None)
            {
                return false;
            }
            if (_busy || _pending != LoginAction.None)
            {
                return true;
            }
            switch (button)
            {
                case LoginButton.Login:
                    _pending = LoginAction.Login;
                    break;
                case LoginButton.Register:
                    _pending = LoginAction.Register;
                    break;
                case LoginButton.OpenChangePassword:
                    SetMode(LoginMode.ChangePassword);
                    break;
                case LoginButton.Confirm:
                    _pending = LoginAction.ChangePassword;
                    break;
                case LoginButton.Back:
                    SetMode(LoginMode.Login);
                    break;
            }
            return true;
        }

        /// <summary>取走待办动作(取走后清空);没有时是 <see cref="LoginAction.None"/>。</summary>
        public LoginAction TakeAction()
        {
            LoginAction action = _pending;
            _pending = LoginAction.None;
            return action;
        }

        /// <summary>切换形态。离开修改密码形态时清掉新密码,焦点落回合法的输入框。</summary>
        public void SetMode(LoginMode mode)
        {
            if (_mode == mode)
            {
                return;
            }
            _mode = mode;
            if (mode == LoginMode.Login)
            {
                _newPassword = string.Empty;
                if (_focus == LoginField.NewPassword)
                {
                    _focus = LoginField.Password;
                }
            }
            else
            {
                _focus = LoginField.Password;
            }
        }

        // ------------------------------------------------------------------ 外壳设置的状态

        /// <summary>外壳:有没有请求在途。</summary>
        public void SetBusy(bool busy)
        {
            _busy = busy;
        }

        /// <summary>外壳:设置要显示的提示(null 清掉)。</summary>
        public void SetNotice(OnlineNotice notice)
        {
            _notice = notice;
        }

        /// <summary>外壳:预填账号(超过上限的部分截掉),焦点落到密码框。</summary>
        public void SetUsername(string username)
        {
            _username = Clip(username ?? string.Empty, MaxUsernameLength);
            _focus = LoginField.Password;
        }

        /// <summary>外壳:预填账号与密码(只给本机开发用的凭据,正式路径不调用)。</summary>
        public void Prefill(string username, string password)
        {
            _username = Clip(username ?? string.Empty, MaxUsernameLength);
            _password = Clip(password ?? string.Empty, MaxPasswordLength);
            _focus = LoginField.Password;
        }

        /// <summary>外壳:清掉密码与新密码(登录成功、改密成功之后调用,密码不在界面里久留)。</summary>
        public void ClearSecrets()
        {
            _password = string.Empty;
            _newPassword = string.Empty;
        }

        private string Current()
        {
            switch (_focus)
            {
                case LoginField.Password: return _password;
                case LoginField.NewPassword: return _newPassword;
                default: return _username;
            }
        }

        private void SetCurrent(string value)
        {
            switch (_focus)
            {
                case LoginField.Password: _password = value; break;
                case LoginField.NewPassword: _newPassword = value; break;
                default: _username = value; break;
            }
        }

        private void Append(char c)
        {
            int limit = _focus == LoginField.Username ? MaxUsernameLength : MaxPasswordLength;
            string value = Current();
            if (value.Length < limit)
            {
                SetCurrent(value + c);
            }
        }

        private void Submit(LoginAction action)
        {
            if (_busy || _pending != LoginAction.None)
            {
                return;
            }
            _pending = action;
        }

        private static string Clip(string value, int limit)
        {
            return value.Length > limit ? value.Substring(0, limit) : value;
        }

        // ------------------------------------------------------------------ 画

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            bool cjk = _paint.HasFonts;
            bool changing = _mode == LoginMode.ChangePassword;
            bool caretOn = (timeMs / BlinkMs) % 2 == 0;
            OnlineShellArt.AddBackground(sprites, ref order, _art, _sprites);
            OnlineShellArt.AddVignette(sprites, ref order, _paint);
            AddBanner(sprites, ref order, cjk);
            OnlineShellArt.AddCard(sprites, ref order, _paint, PanelX, PanelY, PanelWidth, PanelHeightOf(_mode), "login-panel");
            AddHeader(sprites, ref order, changing, cjk);

            AddField(sprites, ref order, LoginField.Username, OnlineShellLabel.Username, OnlineShellLabel.UsernamePlaceholder, cjk, caretOn);
            AddField(sprites, ref order, LoginField.Password, changing ? OnlineShellLabel.OldPassword : OnlineShellLabel.Password,
                changing ? OnlineShellLabel.OldPasswordPlaceholder : OnlineShellLabel.PasswordPlaceholder, cjk, caretOn);
            if (changing)
            {
                AddField(sprites, ref order, LoginField.NewPassword, OnlineShellLabel.NewPassword, OnlineShellLabel.NewPasswordPlaceholder, cjk, caretOn);
            }

            HitRect slot = NoticeRect(_mode);
            if (_busy)
            {
                OnlineShellArt.AddNoticeBar(sprites, ref order, _paint, ShellTone.Wait, OnlineShellText.Label(OnlineShellLabel.Waiting, cjk),
                    slot.X, slot.Y, slot.Width, timeMs, "login-busy");
            }
            else if (_notice != null)
            {
                OnlineShellArt.AddNoticeBar(sprites, ref order, _paint, OnlineShellArt.ToneOf(_notice), OnlineShellText.Notice(_notice, cjk),
                    slot.X, slot.Y, slot.Width, timeMs, "login-notice");
            }
            else
            {
                // 没有提示也没有请求在途时,这一格里写账号的规则(省得玩家试错)
                SpriteFrame rule = _paint.Line(OnlineShellText.Label(OnlineShellLabel.AccountRule, cjk), 14, 0x7C93B8u, ShellPaint.Navy, slot.Width - 4);
                OnlineShellArt.Add(sprites, ref order, rule, slot.X + 2, slot.Y + (slot.Height - (rule == null ? 0 : rule.Height)) / 2, "login-rule");
            }

            if (changing)
            {
                AddButton(sprites, ref order, LoginButton.Confirm, OnlineShellLabel.Confirm, "confirm", cjk, true);
                AddButton(sprites, ref order, LoginButton.Back, OnlineShellLabel.Back, "back", cjk, false);
            }
            else
            {
                AddButton(sprites, ref order, LoginButton.Login, OnlineShellLabel.EnterGame, "login", cjk, true);
                AddButton(sprites, ref order, LoginButton.Register, OnlineShellLabel.Register, "register", cjk, false);
                AddButton(sprites, ref order, LoginButton.OpenChangePassword, OnlineShellLabel.ChangePassword, "change-password", cjk, false);
            }

            OnlineShellLabel hint = _busy ? OnlineShellLabel.BusyHint : (changing ? OnlineShellLabel.ChangePasswordHint : OnlineShellLabel.LoginHint);
            OnlineShellArt.AddHints(sprites, ref order, _paint, OnlineShellText.Hints(hint, cjk), ScreenWidth / 2, OnlineShellArt.HintTop, "login-hint");
            return sprites;
        }

        /// <summary>卡片上方的游戏名标题字(在一条固定的带里居中),下面一条两头渐隐的金线和一颗菱形。</summary>
        private void AddBanner(List<SceneSprite> sprites, ref int order, bool cjk)
        {
            SpriteFrame banner = _paint.Banner(OnlineShellText.Label(OnlineShellLabel.GameTitle, cjk), BannerPx, 0xFFF3C6u, ShellPaint.Gold, 0xE58B22u, 0x24130Au);
            if (banner != null)
            {
                int bx = ScreenWidth / 2 - banner.Width / 2;
                int by = BannerY + (BannerBand - banner.Height) / 2;
                // 光晕比标题字四边各缩进一点;字很小的时候(字体给不出像样的字形)缩进跟着变小,光晕自己至少 2 像素,不会画不出来
                int insetX = Math.Max(0, Math.Min(BannerGlowInsetX, (banner.Width - 2) / 2));
                int insetY = Math.Max(0, Math.Min(BannerGlowInsetY, (banner.Height - 2) / 2));
                SpriteFrame glow = _paint.Glow(Math.Max(2, banner.Width - insetX * 2), Math.Max(2, banner.Height - insetY * 2), 20, BannerGlowSpread, 0xE08A1Eu, 62, true);
                OnlineShellArt.Add(sprites, ref order, glow, bx + insetX - BannerGlowSpread, by + insetY - BannerGlowSpread, "login-banner-glow");
                OnlineShellArt.Add(sprites, ref order, banner, bx, by, "login-banner");
            }
            int ruleY = BannerY + BannerBand - 2;
            OnlineShellArt.Add(sprites, ref order, _paint.Rule(300, 2, ShellPaint.Gold, 0, 235, 0), ScreenWidth / 2 - 150, ruleY, "login-banner-rule");
            OnlineShellArt.Add(sprites, ref order, _paint.Diamond(10, ShellPaint.GoldPale), ScreenWidth / 2 - 5, ruleY - 4, "login-banner-gem");
        }

        /// <summary>卡片的标题(左边一条金色竖条)与副标题。</summary>
        private void AddHeader(List<SceneSprite> sprites, ref int order, bool changing, bool cjk)
        {
            OnlineShellArt.Add(sprites, ref order, _paint.Box(4, 26, 2, PillStyle), FieldX - 14, PanelY + 28, "login-title-bar");
            OnlineShellArt.Add(sprites, ref order, _paint.Rule(FieldWidth, 1, 0x7FA6E0u, 150, 60, 0), FieldX, PanelY + 90, "login-divider");
            OnlineShellArt.Add(sprites, ref order,
                _paint.Line(OnlineShellText.Label(changing ? OnlineShellLabel.ChangePasswordTitle : OnlineShellLabel.LoginTitle, cjk), 26, ShellPaint.Ink, ShellPaint.Navy, FieldWidth, true),
                FieldX, PanelY + 24, "login-title");
            OnlineShellArt.Add(sprites, ref order,
                _paint.Line(OnlineShellText.Label(changing ? OnlineShellLabel.ChangePasswordSubtitle : OnlineShellLabel.LoginSubtitle, cjk), 14, ShellPaint.Muted, ShellPaint.Navy, FieldWidth),
                FieldX, PanelY + 62, "login-subtitle");
        }

        private static string FieldName(LoginField field)
        {
            return field == LoginField.Username ? "username" : (field == LoginField.Password ? "password" : "new-password");
        }

        private void AddField(List<SceneSprite> sprites, ref int order, LoginField field, OnlineShellLabel label, OnlineShellLabel placeholder, bool cjk, bool caretOn)
        {
            HitRect box = FieldRect(field);
            string name = FieldName(field);
            bool focused = _focus == field;
            if (focused)
            {
                OnlineShellArt.Add(sprites, ref order, _paint.Glow(box.Width, box.Height, FieldRadius, 8, ShellPaint.Gold, 90, false), box.X - 8, box.Y - 8, "login-focus-" + name);
            }
            OnlineShellArt.Add(sprites, ref order, _paint.Box(box.Width, box.Height, FieldRadius, focused ? FieldFocusStyle : FieldStyle), box.X, box.Y, "login-box-" + name);
            OnlineShellArt.Add(sprites, ref order,
                _paint.Box(LabelCellWidth, box.Height - 2, FieldRadius - 1, focused ? CellFocusStyle : CellStyle, ShellCorners.Left), box.X + 1, box.Y + 1, "login-cell-" + name);
            SpriteFrame text = _paint.Line(OnlineShellText.Label(label, cjk), 16, focused ? ShellPaint.Gold : ShellPaint.Muted, ShellPaint.Navy, LabelCellWidth - 6);
            if (text != null)
            {
                OnlineShellArt.Add(sprites, ref order, text, box.X + (LabelCellWidth - text.Width) / 2, box.Y + (box.Height - text.Height) / 2, "login-label-" + name);
            }

            int textX = TextLeft;
            int textWidth = box.Width - LabelCellWidth - TextInset * 2;
            int endX = textX;
            if (field == LoginField.Username)
            {
                SpriteFrame value = _paint.Data(_username, 17, ShellPaint.Ink, ShellPaint.Navy, textWidth);
                if (value != null)
                {
                    OnlineShellArt.Add(sprites, ref order, value, textX, box.Y + (box.Height - value.Height) / 2, "login-username-text");
                    endX = textX + value.Width;
                }
            }
            else
            {
                string secret = field == LoginField.Password ? _password : _newPassword;
                int visible = Math.Min(secret.Length, MaskCapacity);
                SpriteFrame dot = _paint.Dot(MaskSize, ShellPaint.Ink);
                for (int i = 0; i < visible; i++)
                {
                    OnlineShellArt.Add(sprites, ref order, dot, textX + i * MaskStep, box.Y + (box.Height - MaskSize) / 2, "login-password-mask");
                }
                endX = textX + visible * MaskStep;
                if (secret.Length > 0)
                {
                    endX -= MaskStep - MaskSize;
                }
            }
            bool empty = field == LoginField.Username ? _username.Length == 0 : (field == LoginField.Password ? _password.Length == 0 : _newPassword.Length == 0);
            if (empty)
            {
                SpriteFrame hint = _paint.Line(OnlineShellText.Label(placeholder, cjk), 16, ShellPaint.Faint, ShellPaint.Navy, textWidth);
                if (hint != null)
                {
                    OnlineShellArt.Add(sprites, ref order, hint, textX + 6, box.Y + (box.Height - hint.Height) / 2, "login-placeholder-" + name);
                }
            }
            if (focused && caretOn)
            {
                OnlineShellArt.Add(sprites, ref order, _art.Solid(2, 20, ShellPaint.Ink), endX + (empty ? 0 : 4), box.Y + (box.Height - 20) / 2, "login-caret");
            }
        }

        private void AddButton(List<SceneSprite> sprites, ref int order, LoginButton button, OnlineShellLabel label, string tag, bool cjk, bool primary)
        {
            HitRect rect = ButtonRect(button);
            bool disabled = _busy;
            if (primary && !disabled)
            {
                OnlineShellArt.Add(sprites, ref order, _paint.Glow(rect.Width, rect.Height, 10, 10, 0x000000u, 120, true), rect.X - 10, rect.Y - 5, "login-button-shadow-" + tag);
            }
            ShellBox style = primary ? (disabled ? PrimaryOffStyle : PrimaryStyle) : (disabled ? SecondaryOffStyle : SecondaryStyle);
            OnlineShellArt.Add(sprites, ref order, _paint.Box(rect.Width, rect.Height, primary ? 10 : 8, style), rect.X, rect.Y,
                "login-button-" + tag + (disabled ? "-disabled" : string.Empty));
            uint color = primary ? (disabled ? 0x8A97ADu : ShellPaint.Warm) : (disabled ? ShellPaint.Faint : 0xD6E4FFu);
            uint edge = primary ? (disabled ? 0x3A4558u : 0xF0B640u) : ShellPaint.Navy;
            SpriteFrame text = _paint.Line(OnlineShellText.Label(label, cjk), primary ? 20 : 16, color, edge, rect.Width - 16, primary);
            if (text != null)
            {
                OnlineShellArt.Add(sprites, ref order, text, rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2, "login-button-text-" + tag);
            }
        }
    }
}
