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
    /// 联网登录界面(<see cref="ISceneSource"/>):账号框、密码框(只画与字数相同的圆点,从不画密码本身)、"进入游戏"、"注册"与可选的"修改密码",
    /// 背景是选角界面的底图(见 <see cref="OnlineShellArt.AddBackground"/>)。
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
        public const int PanelX = 220;
        public const int PanelY = 140;
        public const int PanelWidth = 360;
        public const int PanelHeight = 310;
        public const int FieldX = PanelX + 24;
        public const int FieldWidth = 312;
        public const int FieldHeight = 32;
        private const int LabelOffset = 20;
        private const int FieldSpacing = 62;
        private const int FirstFieldY = PanelY + 76;

        private const uint PanelBorderColor = 0x3A5A8Au;
        private const uint PanelFillColor = 0x101C30u;
        private const uint FieldFillColor = 0x0A1220u;
        private const uint FieldBorderColor = 0x4A6A9Au;
        private const uint ButtonColor = CharacterSelectArt.FallbackButtonColor;
        private const uint ButtonDisabledColor = 0x2A2F38u;
        private const uint DisabledTextColor = 0x8A8F98u;
        private const int BlinkMs = 500;

        private readonly OverlayArt _art;
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

        /// <summary>输入框的位置与大小(账号、密码/旧密码、新密码从上到下)。</summary>
        public static HitRect FieldRect(LoginField field)
        {
            return new HitRect(FieldX, FirstFieldY + FieldSpacing * (int)field, FieldWidth, FieldHeight);
        }

        /// <summary>按钮的位置与大小。登录形态有登录、注册、修改密码三个;修改密码形态有确认与返回两个。</summary>
        public static HitRect ButtonRect(LoginButton button)
        {
            switch (button)
            {
                case LoginButton.Login:
                    return new HitRect(FieldX, PanelY + 232, 112, 36);
                case LoginButton.Register:
                    return new HitRect(FieldX + 122, PanelY + 232, 80, 36);
                case LoginButton.OpenChangePassword:
                    return new HitRect(FieldX + 212, PanelY + 232, 100, 36);
                case LoginButton.Confirm:
                    return new HitRect(FieldX, PanelY + 262, 150, 36);
                case LoginButton.Back:
                    return new HitRect(FieldX + 162, PanelY + 262, 150, 36);
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
            bool cjk = _art.HasFonts;
            bool caretOn = (timeMs / BlinkMs) % 2 == 0;
            OnlineShellArt.AddBackground(sprites, ref order, _art, _sprites);

            sprites.Add(OverlayArt.Place(_art.Solid(PanelWidth, PanelHeight, PanelBorderColor), PanelX, PanelY, order++, "login-panel-border"));
            sprites.Add(OverlayArt.Place(_art.Solid(PanelWidth - 4, PanelHeight - 4, PanelFillColor), PanelX + 2, PanelY + 2, order++, "login-panel"));

            bool changing = _mode == LoginMode.ChangePassword;
            OnlineShellArt.AddText(sprites, ref order, _art,
                OnlineShellText.Label(changing ? OnlineShellLabel.ChangePasswordTitle : OnlineShellLabel.LoginTitle, cjk),
                FieldX, PanelY + 16, 3, OverlayArt.Yellow, "login-title");

            AddField(sprites, ref order, LoginField.Username, OnlineShellText.Label(OnlineShellLabel.Username, cjk), cjk, caretOn);
            AddField(sprites, ref order, LoginField.Password,
                OnlineShellText.Label(changing ? OnlineShellLabel.OldPassword : OnlineShellLabel.Password, cjk), cjk, caretOn);
            if (changing)
            {
                AddField(sprites, ref order, LoginField.NewPassword, OnlineShellText.Label(OnlineShellLabel.NewPassword, cjk), cjk, caretOn);
            }

            int noticeY = PanelY + (changing ? 240 : 186);
            if (_busy)
            {
                OnlineShellArt.AddText(sprites, ref order, _art, OnlineShellText.Label(OnlineShellLabel.Waiting, cjk), FieldX, noticeY, cjk ? 2 : 1, OverlayArt.Yellow, "login-busy");
            }
            else if (_notice != null)
            {
                OnlineShellArt.AddText(sprites, ref order, _art, OnlineShellText.Notice(_notice, cjk), FieldX, noticeY, cjk ? 2 : 1,
                    OnlineShellArt.NoticeColor(_notice), "login-notice");
            }

            if (changing)
            {
                AddButton(sprites, ref order, LoginButton.Confirm, OnlineShellLabel.Confirm, "confirm", cjk);
                AddButton(sprites, ref order, LoginButton.Back, OnlineShellLabel.Back, "back", cjk);
            }
            else
            {
                AddButton(sprites, ref order, LoginButton.Login, OnlineShellLabel.EnterGame, "login", cjk);
                AddButton(sprites, ref order, LoginButton.Register, OnlineShellLabel.Register, "register", cjk);
                AddButton(sprites, ref order, LoginButton.OpenChangePassword, OnlineShellLabel.ChangePassword, "change-password", cjk);
            }

            OnlineShellArt.AddCentered(sprites, ref order, _art,
                OnlineShellText.Label(changing ? OnlineShellLabel.ChangePasswordHint : OnlineShellLabel.LoginHint, cjk),
                ScreenWidth / 2, ScreenHeight - 40, cjk ? 2 : 1, OverlayArt.White, "login-hint");
            return sprites;
        }

        private void AddField(List<SceneSprite> sprites, ref int order, LoginField field, string label, bool cjk, bool caretOn)
        {
            HitRect box = FieldRect(field);
            string name = field == LoginField.Username ? "username" : field == LoginField.Password ? "password" : "new-password";
            bool focused = _focus == field;
            OnlineShellArt.AddText(sprites, ref order, _art, label, box.X, box.Y - LabelOffset, cjk ? 2 : 1, CharacterSelectArt.CaptionColor, "login-label-" + name);
            sprites.Add(OverlayArt.Place(_art.Solid(box.Width, box.Height, focused ? OverlayArt.Yellow : FieldBorderColor), box.X, box.Y, order++, "login-box-border-" + name));
            sprites.Add(OverlayArt.Place(_art.Solid(box.Width - 2, box.Height - 2, FieldFillColor), box.X + 1, box.Y + 1, order++, "login-box-" + name));

            int textX = box.X + 8;
            int endX = textX;
            if (field == LoginField.Username)
            {
                SpriteFrame text = OnlineShellArt.DataText(_art, _username, cjk ? 2 : 1, OverlayArt.White);
                if (text != null)
                {
                    sprites.Add(OverlayArt.Place(text, textX, box.Y + (box.Height - text.Height) / 2, order++, "login-username-text"));
                    endX = textX + text.Width;
                }
            }
            else
            {
                string secret = field == LoginField.Password ? _password : _newPassword;
                int visible = Math.Min(secret.Length, (box.Width - 16) / MaskStep);
                SpriteFrame dot = _art.Solid(MaskSize, MaskSize, OverlayArt.White);
                for (int i = 0; i < visible; i++)
                {
                    sprites.Add(OverlayArt.Place(dot, textX + i * MaskStep, box.Y + (box.Height - MaskSize) / 2, order++, "login-password-mask"));
                }
                endX = textX + visible * MaskStep;
            }
            if (focused && caretOn)
            {
                sprites.Add(OverlayArt.Place(_art.Solid(2, 18, OverlayArt.White), endX + 2, box.Y + (box.Height - 18) / 2, order++, "login-caret"));
            }
        }

        private const int MaskSize = 8;
        private const int MaskStep = 12;

        private void AddButton(List<SceneSprite> sprites, ref int order, LoginButton button, OnlineShellLabel label, string tag, bool cjk)
        {
            HitRect rect = ButtonRect(button);
            bool disabled = _busy;
            sprites.Add(OverlayArt.Place(_art.Solid(rect.Width, rect.Height, disabled ? ButtonDisabledColor : ButtonColor), rect.X, rect.Y, order++,
                "login-button-" + tag + (disabled ? "-disabled" : string.Empty)));
            SpriteFrame text = _art.Text(OnlineShellText.Label(label, cjk), cjk ? 2 : 1, disabled ? DisabledTextColor : CharacterSelectArt.ButtonTextColor);
            if (text != null)
            {
                sprites.Add(OverlayArt.Place(text, rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2, order++, "login-button-text-" + tag));
            }
        }
    }
}
