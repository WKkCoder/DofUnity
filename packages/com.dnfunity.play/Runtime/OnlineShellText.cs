using System.Text;

namespace Dnf.Play
{
    /// <summary>联网外壳界面上的固定文字(标签、按钮、提示行)。</summary>
    public enum OnlineShellLabel
    {
        LoginTitle,
        ChangePasswordTitle,
        Username,
        Password,
        OldPassword,
        NewPassword,
        EnterGame,
        Register,
        ChangePassword,
        Confirm,
        Back,
        LoginHint,
        ChangePasswordHint,
        Waiting,
        ServerSelect,
        ChannelConnecting,
        ChannelHint,
        ChannelHintCancel,
        Loading,
        LoadingHint,
        CharacterSelectHint,
        GameTitle,
        LoginSubtitle,
        ChangePasswordSubtitle,
        UsernamePlaceholder,
        PasswordPlaceholder,
        OldPasswordPlaceholder,
        NewPasswordPlaceholder,
        BusyHint,
        AccountRule,
    }

    /// <summary>
    /// 联网外壳的文字表。有 CJK 字体时用中文;没有字体时用点阵字库画得出的英文大写占位(只含 A–Z、数字、空格与 ( ) ? - . ! / ~ : +)。
    /// 这里自己带文字表,不去动 <see cref="UiText"/>(那张表是离线流程的占位英文 → 中文翻译,它的行为不能变)。
    /// 提示文字只由类别、倒计时秒数与"哪一项配置有问题"的说明组成,从不含账号、口令、地址、令牌、角色名。
    /// </summary>
    public static class OnlineShellText
    {
        public static string Label(OnlineShellLabel label, bool cjk)
        {
            switch (label)
            {
                case OnlineShellLabel.LoginTitle: return cjk ? "账号登录" : "LOGIN";
                case OnlineShellLabel.ChangePasswordTitle: return cjk ? "修改密码" : "CHANGE PASSWORD";
                case OnlineShellLabel.Username: return cjk ? "账号" : "ACCOUNT";
                case OnlineShellLabel.Password: return cjk ? "密码" : "PASSWORD";
                case OnlineShellLabel.OldPassword: return cjk ? "旧密码" : "OLD PASSWORD";
                case OnlineShellLabel.NewPassword: return cjk ? "新密码" : "NEW PASSWORD";
                case OnlineShellLabel.EnterGame: return cjk ? "进入游戏" : "ENTER GAME";
                case OnlineShellLabel.Register: return cjk ? "注册" : "REGISTER";
                case OnlineShellLabel.ChangePassword: return cjk ? "修改密码" : "CHANGE PASSWORD";
                case OnlineShellLabel.Confirm: return cjk ? "确认修改" : "CONFIRM";
                case OnlineShellLabel.Back: return cjk ? "返回" : "BACK";
                case OnlineShellLabel.Waiting: return cjk ? "请稍候…" : "PLEASE WAIT...";
                case OnlineShellLabel.ServerSelect: return cjk ? "正在选择服务器…" : "SELECTING SERVER...";
                case OnlineShellLabel.ChannelConnecting: return cjk ? "正在连接频道…" : "CONNECTING...";
                case OnlineShellLabel.Loading: return cjk ? "正在进入游戏…" : "ENTERING GAME...";
                case OnlineShellLabel.GameTitle: return cjk ? "地下城与勇士" : "DNF";
                case OnlineShellLabel.LoginSubtitle: return cjk ? "登录后选择服务器与角色" : "SIGN IN TO CONTINUE";
                case OnlineShellLabel.ChangePasswordSubtitle: return cjk ? "输入旧密码与新密码" : "ENTER OLD AND NEW PASSWORD";
                case OnlineShellLabel.UsernamePlaceholder: return cjk ? "请输入账号" : "ENTER ACCOUNT";
                case OnlineShellLabel.PasswordPlaceholder: return cjk ? "请输入密码" : "ENTER PASSWORD";
                case OnlineShellLabel.OldPasswordPlaceholder: return cjk ? "请输入旧密码" : "ENTER OLD PASSWORD";
                case OnlineShellLabel.NewPasswordPlaceholder: return cjk ? "请输入新密码" : "ENTER NEW PASSWORD";
                case OnlineShellLabel.AccountRule: return cjk ? "账号为 4 到 32 位字母、数字或下划线" : "ACCOUNT: 4-32 LETTERS OR DIGITS";
                case OnlineShellLabel.LoginHint:
                case OnlineShellLabel.ChangePasswordHint:
                case OnlineShellLabel.ChannelHint:
                case OnlineShellLabel.ChannelHintCancel:
                case OnlineShellLabel.LoadingHint:
                case OnlineShellLabel.CharacterSelectHint:
                case OnlineShellLabel.BusyHint:
                    return Flat(Hints(label, cjk));
                default: return string.Empty;
            }
        }

        /// <summary>
        /// 按键说明的各项(键帽上的键名 + 这个键做什么):登录、改密、选频道失败、等待、选角的底部提示。
        /// 键名是键盘上印的字样(中文界面里写 Tab、Enter、Esc;点阵占位里是大写)。不是按键说明的标签返回空数组。
        /// </summary>
        public static ShellHint[] Hints(OnlineShellLabel label, bool cjk)
        {
            string tab = cjk ? "Tab" : "TAB";
            string enter = cjk ? "Enter" : "ENTER";
            string esc = cjk ? "Esc" : "ESC";
            switch (label)
            {
                case OnlineShellLabel.LoginHint:
                    return new[] { new ShellHint(tab, cjk ? "切换输入框" : "SWITCH"), new ShellHint(enter, cjk ? "进入游戏" : "LOGIN") };
                case OnlineShellLabel.ChangePasswordHint:
                    return new[] { new ShellHint(tab, cjk ? "切换输入框" : "SWITCH"), new ShellHint(enter, cjk ? "确认修改" : "CONFIRM"), new ShellHint(esc, cjk ? "返回" : "BACK") };
                case OnlineShellLabel.ChannelHint:
                    return new[] { new ShellHint(enter, cjk ? "重试" : "RETRY"), new ShellHint(esc, cjk ? "返回登录" : "BACK") };
                case OnlineShellLabel.ChannelHintCancel:
                case OnlineShellLabel.LoadingHint:
                case OnlineShellLabel.BusyHint:
                    return new[] { new ShellHint(esc, cjk ? "取消" : "CANCEL") };
                case OnlineShellLabel.CharacterSelectHint:
                    return new[] { new ShellHint(cjk ? "方向键" : "ARROWS", cjk ? "选择" : "SELECT"), new ShellHint(enter, cjk ? "开始" : "START"), new ShellHint(esc, cjk ? "返回登录" : "BACK") };
                default:
                    return new ShellHint[0];
            }
        }

        private static string Flat(ShellHint[] hints)
        {
            var sb = new StringBuilder();
            foreach (ShellHint hint in hints)
            {
                if (sb.Length > 0)
                {
                    sb.Append("    ");
                }
                sb.Append(hint.Key).Append(' ').Append(hint.Text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 提示对应的一句话;<see cref="OnlineNoticeKind.None"/> 或 null 返回空串。限流的那句带剩余秒数(至少按 1 秒写);
        /// "未配置连接"在有字体时后面接说明(<see cref="OnlineNotice.Detail"/>,只点名配置项)。
        /// </summary>
        public static string Notice(OnlineNotice notice, bool cjk)
        {
            if (notice == null)
            {
                return string.Empty;
            }
            switch (notice.Kind)
            {
                case OnlineNoticeKind.WrongCredentials: return cjk ? "账号或密码错误" : "WRONG ACCOUNT OR PASSWORD";
                case OnlineNoticeKind.AccountBanned: return cjk ? "该账号已被封停" : "ACCOUNT BANNED";
                case OnlineNoticeKind.RateLimited:
                {
                    int seconds = notice.Seconds < 1 ? 1 : notice.Seconds;
                    return cjk ? "请求过于频繁，请 " + seconds + " 秒后再试" : "TOO MANY REQUESTS   RETRY IN " + seconds + " S";
                }
                case OnlineNoticeKind.NetworkError: return cjk ? "网络错误或连接超时" : "NETWORK ERROR OR TIMEOUT";
                case OnlineNoticeKind.ProtocolError: return cjk ? "服务器响应异常" : "SERVER PROTOCOL ERROR";
                case OnlineNoticeKind.NoGameServer: return cjk ? "服务器没有配置游戏服地址" : "NO GAME SERVER ADDRESS";
                case OnlineNoticeKind.InvalidUsername: return cjk ? "账号需为 4 到 32 位字母、数字或下划线" : "ACCOUNT MUST BE 4-32 LETTERS OR DIGITS";
                case OnlineNoticeKind.InvalidPassword: return cjk ? "密码不能为空" : "PASSWORD REQUIRED";
                case OnlineNoticeKind.NotConfigured:
                    return cjk
                        ? (string.IsNullOrEmpty(notice.Detail) ? "未配置连接" : "未配置连接：" + notice.Detail)
                        : "NOT CONFIGURED";
                case OnlineNoticeKind.RegistrationClosed: return cjk ? "暂不开放注册" : "REGISTRATION CLOSED";
                case OnlineNoticeKind.UserExists: return cjk ? "账号已存在" : "ACCOUNT EXISTS";
                case OnlineNoticeKind.Rejected: return cjk ? "服务器拒绝了请求" : "REQUEST REJECTED";
                case OnlineNoticeKind.RegisterOk: return cjk ? "注册成功，请登录" : "REGISTERED   PLEASE LOGIN";
                case OnlineNoticeKind.PasswordChanged: return cjk ? "密码已修改" : "PASSWORD CHANGED";
                case OnlineNoticeKind.ConnectFailed: return cjk ? "无法连接频道" : "CANNOT CONNECT CHANNEL";
                case OnlineNoticeKind.GameLoginRejected: return cjk ? "游戏服拒绝了登录" : "GAME LOGIN REJECTED";
                case OnlineNoticeKind.CharacterListMissing: return cjk ? "没有收到角色列表" : "NO CHARACTER LIST";
                case OnlineNoticeKind.SelectFailed: return cjk ? "这个角色暂时进不去，请换一个" : "CANNOT ENTER WITH THIS CHARACTER";
                case OnlineNoticeKind.NoCharacters: return cjk ? "账号下没有角色" : "NO CHARACTER";
                case OnlineNoticeKind.Disconnected: return cjk ? "与服务器的连接已断开" : "DISCONNECTED FROM SERVER";
                case OnlineNoticeKind.Cancelled: return cjk ? "已取消" : "CANCELLED";
                case OnlineNoticeKind.Unexpected: return cjk ? "出现意外错误" : "UNEXPECTED ERROR";
                default: return string.Empty;
            }
        }
    }
}
