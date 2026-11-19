using System;

namespace Dnf.Play
{
    /// <summary>
    /// 联网外壳(<see cref="OnlineShellFlow"/>)的阶段:登录 → 选服务器 → 选频道 → 选角色 → 加载 → 进入游戏。
    /// 选服务器与选频道现在是占位(登录成功后自动放行,用配置里的默认频道连接),真正的界面由后续线接上;
    /// 进入游戏之后外壳把控制权交给既有的 <see cref="GameFlow"/>(由宿主接手)。
    /// </summary>
    public enum OnlineShellStage
    {
        Login = 0,
        ServerSelect = 1,
        ChannelSelect = 2,
        CharacterSelect = 3,

        /// <summary>选角确认之后、服务端回应之前(选角请求在途)。</summary>
        Loading = 4,
        InGame = 5,
    }

    /// <summary>阶段在日志里的写法。</summary>
    public static class OnlineShellStages
    {
        /// <summary>login、server、channel、charselect、loading、ingame。</summary>
        public static string Code(OnlineShellStage stage)
        {
            switch (stage)
            {
                case OnlineShellStage.Login: return "login";
                case OnlineShellStage.ServerSelect: return "server";
                case OnlineShellStage.ChannelSelect: return "channel";
                case OnlineShellStage.CharacterSelect: return "charselect";
                case OnlineShellStage.Loading: return "loading";
                case OnlineShellStage.InGame: return "ingame";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// 要连的游戏频道:端口,加上可空的主机(null = 用登录时网关给的游戏服地址,现在只有这一种;以后选服务器的界面可以给别的主机)。
    /// <see cref="ToString"/> 不输出端口与主机,免得它们进日志。
    /// </summary>
    public sealed class ChannelTarget : IEquatable<ChannelTarget>
    {
        public ChannelTarget(int port, string host = null, ushort serverGroup = 0)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }
            if (host != null && host.Length == 0)
            {
                throw new ArgumentException("主机不能是空串(要默认就传 null)", nameof(host));
            }
            if (serverGroup > 17)
            {
                throw new ArgumentOutOfRangeException(nameof(serverGroup));
            }
            Port = port;
            Host = host;
            ServerGroup = serverGroup;
        }

        public int Port { get; }

        /// <summary>主机;null 表示用网关给的游戏服地址。</summary>
        public string Host { get; }

        /// <summary>所属服务器组编号（1..17，用于跨服/自由练习等复合频道登录；0 表示未指定）。</summary>
        public ushort ServerGroup { get; }

        public bool Equals(ChannelTarget other)
        {
            return other != null && Port == other.Port && string.Equals(Host, other.Host, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ChannelTarget);
        }

        public override int GetHashCode()
        {
            return Port * 397 ^ (Host == null ? 0 : StringComparer.Ordinal.GetHashCode(Host));
        }

        public override string ToString()
        {
            return "ChannelTarget";
        }
    }

    /// <summary>
    /// 给玩家看的提示类别(登录失败的归类也是它):每一类对应一句固定的话(<see cref="OnlineShellText"/>),日志里写它的短码(<see cref="OnlineNotices.Code"/>)。
    /// 提示只有类别、倒计时秒数与"哪一项配置有问题"的说明,从不带账号、口令、地址、令牌、角色名。
    /// </summary>
    public enum OnlineNoticeKind
    {
        None = 0,

        // ---- 登录 / 注册 / 改密(网关那一步)
        /// <summary>账号或口令错误。</summary>
        WrongCredentials,

        /// <summary>账号被封停。</summary>
        AccountBanned,

        /// <summary>被限流(带倒计时秒数)。</summary>
        RateLimited,

        /// <summary>网络错误或超时。</summary>
        NetworkError,

        /// <summary>服务器回的内容不合协议。</summary>
        ProtocolError,

        /// <summary>服务器没有配置游戏服地址。</summary>
        NoGameServer,

        /// <summary>本地检查:账号不合规(没发出请求)。</summary>
        InvalidUsername,

        /// <summary>本地检查:口令是空的(没发出请求)。</summary>
        InvalidPassword,

        /// <summary>没有可用的连接配置(没发出请求)。</summary>
        NotConfigured,

        /// <summary>暂不开放注册。</summary>
        RegistrationClosed,

        /// <summary>账号已存在。</summary>
        UserExists,

        /// <summary>服务器拒绝了请求(其它业务错误)。</summary>
        Rejected,

        /// <summary>注册成功(提示,不是错误)。</summary>
        RegisterOk,

        /// <summary>改密成功(提示,不是错误)。</summary>
        PasswordChanged,

        // ---- 连频道 / 选角
        /// <summary>连不上游戏频道。</summary>
        ConnectFailed,

        /// <summary>游戏服拒绝了登录。</summary>
        GameLoginRejected,

        /// <summary>没有收到(或解不出)角色列表。</summary>
        CharacterListMissing,

        /// <summary>这个角色进不去,可以换一个。</summary>
        SelectFailed,

        /// <summary>账号下没有角色(提示)。</summary>
        NoCharacters,

        // ---- 通用
        /// <summary>连接断开。</summary>
        Disconnected,

        /// <summary>被玩家取消(提示)。</summary>
        Cancelled,

        /// <summary>出了意料之外的错误。</summary>
        Unexpected,
    }

    /// <summary>提示类别的短码与性质。</summary>
    public static class OnlineNotices
    {
        /// <summary>日志里的 reason 短码(全小写加下划线)。</summary>
        public static string Code(OnlineNoticeKind kind)
        {
            switch (kind)
            {
                case OnlineNoticeKind.WrongCredentials: return "wrong_credentials";
                case OnlineNoticeKind.AccountBanned: return "banned";
                case OnlineNoticeKind.RateLimited: return "rate_limited";
                case OnlineNoticeKind.NetworkError: return "network";
                case OnlineNoticeKind.ProtocolError: return "protocol";
                case OnlineNoticeKind.NoGameServer: return "no_game_server";
                case OnlineNoticeKind.InvalidUsername: return "invalid_username";
                case OnlineNoticeKind.InvalidPassword: return "invalid_password";
                case OnlineNoticeKind.NotConfigured: return "not_configured";
                case OnlineNoticeKind.RegistrationClosed: return "registration_closed";
                case OnlineNoticeKind.UserExists: return "user_exists";
                case OnlineNoticeKind.Rejected: return "rejected";
                case OnlineNoticeKind.RegisterOk: return "registered";
                case OnlineNoticeKind.PasswordChanged: return "password_changed";
                case OnlineNoticeKind.ConnectFailed: return "connect_failed";
                case OnlineNoticeKind.GameLoginRejected: return "game_login_rejected";
                case OnlineNoticeKind.CharacterListMissing: return "no_character_list";
                case OnlineNoticeKind.SelectFailed: return "select_failed";
                case OnlineNoticeKind.NoCharacters: return "no_characters";
                case OnlineNoticeKind.Disconnected: return "disconnected";
                case OnlineNoticeKind.Cancelled: return "cancelled";
                case OnlineNoticeKind.Unexpected: return "unexpected";
                default: return "none";
            }
        }

        /// <summary>是不是错误(提示成功、取消、没有角色这类不算错误,界面用另一种颜色)。</summary>
        public static bool IsError(OnlineNoticeKind kind)
        {
            switch (kind)
            {
                case OnlineNoticeKind.None:
                case OnlineNoticeKind.RegisterOk:
                case OnlineNoticeKind.PasswordChanged:
                case OnlineNoticeKind.NoCharacters:
                case OnlineNoticeKind.Cancelled:
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// 一条提示:类别、日志短码、(限流时)还剩几秒、(未配置时)哪一项配置有问题的说明。不可变。
    /// </summary>
    public sealed class OnlineNotice
    {
        public OnlineNotice(OnlineNoticeKind kind, string reason = null, int seconds = 0, string detail = null)
        {
            Kind = kind;
            Reason = reason ?? OnlineNotices.Code(kind);
            Seconds = seconds < 0 ? 0 : seconds;
            Detail = detail;
        }

        public OnlineNoticeKind Kind { get; }

        /// <summary>日志里的 reason 短码(默认是类别的短码;超时这类同一个类别有不同来源时单独指定)。</summary>
        public string Reason { get; }

        /// <summary>限流提示还剩的秒数;其它类别是 0。</summary>
        public int Seconds { get; }

        /// <summary>补充说明(只用于"未配置连接":点名哪一项配置有问题,不含配置的值);没有时为 null。</summary>
        public string Detail { get; }

        public bool IsError => OnlineNotices.IsError(Kind);

        /// <summary>同一条提示换一个倒计时秒数。</summary>
        public OnlineNotice WithSeconds(int seconds)
        {
            return new OnlineNotice(Kind, Reason, seconds, Detail);
        }
    }
}
