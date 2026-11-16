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
                case OnlineShellLabel.LoginTitle: return cjk ? "登录" : "LOGIN";
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
                case OnlineShellLabel.LoginHint: return cjk ? "TAB 切换输入框    回车 进入游戏" : "TAB SWITCH   ENTER LOGIN";
                case OnlineShellLabel.ChangePasswordHint: return cjk ? "TAB 切换输入框    回车 确认修改    ESC 返回" : "TAB SWITCH   ENTER CONFIRM   ESC BACK";
                case OnlineShellLabel.Waiting: return cjk ? "请稍候…(ESC 取消)" : "PLEASE WAIT...   ESC CANCEL";
                case OnlineShellLabel.ServerSelect: return cjk ? "正在选择服务器…" : "SELECTING SERVER...";
                case OnlineShellLabel.ChannelConnecting: return cjk ? "正在连接频道…" : "CONNECTING...";
                case OnlineShellLabel.ChannelHint: return cjk ? "回车 重试    ESC 返回登录" : "ENTER RETRY   ESC BACK";
                case OnlineShellLabel.ChannelHintCancel: return cjk ? "ESC 取消" : "ESC CANCEL";
                case OnlineShellLabel.Loading: return cjk ? "正在进入游戏…" : "ENTERING GAME...";
                case OnlineShellLabel.LoadingHint: return cjk ? "ESC 取消" : "ESC CANCEL";
                case OnlineShellLabel.CharacterSelectHint: return cjk ? "方向键 选择    回车 开始    ESC 返回登录" : "ARROWS SELECT   ENTER START   ESC BACK";
                default: return string.Empty;
            }
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
                    return cjk ? "请求过于频繁,请 " + seconds + " 秒后再试" : "TOO MANY REQUESTS   RETRY IN " + seconds + " S";
                }
                case OnlineNoticeKind.NetworkError: return cjk ? "网络错误或连接超时" : "NETWORK ERROR OR TIMEOUT";
                case OnlineNoticeKind.ProtocolError: return cjk ? "服务器响应异常" : "SERVER PROTOCOL ERROR";
                case OnlineNoticeKind.NoGameServer: return cjk ? "服务器没有配置游戏服地址" : "NO GAME SERVER ADDRESS";
                case OnlineNoticeKind.InvalidUsername: return cjk ? "账号需为 4 到 32 位字母、数字或下划线" : "ACCOUNT MUST BE 4-32 LETTERS OR DIGITS";
                case OnlineNoticeKind.InvalidPassword: return cjk ? "密码不能为空" : "PASSWORD REQUIRED";
                case OnlineNoticeKind.NotConfigured:
                    return cjk
                        ? (string.IsNullOrEmpty(notice.Detail) ? "未配置连接" : "未配置连接:" + notice.Detail)
                        : "NOT CONFIGURED";
                case OnlineNoticeKind.RegistrationClosed: return cjk ? "暂不开放注册" : "REGISTRATION CLOSED";
                case OnlineNoticeKind.UserExists: return cjk ? "账号已存在" : "ACCOUNT EXISTS";
                case OnlineNoticeKind.Rejected: return cjk ? "服务器拒绝了请求" : "REQUEST REJECTED";
                case OnlineNoticeKind.RegisterOk: return cjk ? "注册成功,请登录" : "REGISTERED   PLEASE LOGIN";
                case OnlineNoticeKind.PasswordChanged: return cjk ? "密码已修改" : "PASSWORD CHANGED";
                case OnlineNoticeKind.ConnectFailed: return cjk ? "无法连接频道" : "CANNOT CONNECT CHANNEL";
                case OnlineNoticeKind.GameLoginRejected: return cjk ? "游戏服拒绝了登录" : "GAME LOGIN REJECTED";
                case OnlineNoticeKind.CharacterListMissing: return cjk ? "没有收到角色列表" : "NO CHARACTER LIST";
                case OnlineNoticeKind.SelectFailed: return cjk ? "这个角色暂时进不去,请换一个" : "CANNOT ENTER WITH THIS CHARACTER";
                case OnlineNoticeKind.NoCharacters: return cjk ? "账号下没有角色" : "NO CHARACTER";
                case OnlineNoticeKind.Disconnected: return cjk ? "与服务器的连接已断开" : "DISCONNECTED FROM SERVER";
                case OnlineNoticeKind.Cancelled: return cjk ? "已取消" : "CANCELLED";
                case OnlineNoticeKind.Unexpected: return cjk ? "出现意外错误" : "UNEXPECTED ERROR";
                default: return string.Empty;
            }
        }
    }
}
