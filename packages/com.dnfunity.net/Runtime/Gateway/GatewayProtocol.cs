using System;
using System.Net;
using System.Net.Sockets;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 网关协议里所有"可能与部署版本不同"的约定,集中在这一个文件:路由、请求格式、字段规则、响应关键字、限流口径。
    /// 依据是公开资料的转述,并已在真实网关上核对过健康检查、游戏服地址与登录三条路径(E-295);没有触达的错误路径仍按转述(E-296)。真实服务器与这里不一致时,只改这里(以及对应的测试期望)。
    /// </summary>
    public static class GatewayProtocol
    {
        /// <summary>健康检查路由(GET,JSON)。</summary>
        public const string HealthRoute = "/health";

        /// <summary>游戏服 IP 路由(GET,纯文本;未配置时 404)。</summary>
        public const string GameServerIpRoute = "/api/v1/game-server-ip";

        /// <summary>认证路由(POST)。</summary>
        public const string AuthRoute = "/api/v1/auth";

        /// <summary>认证请求的 Content-Type,发送时必须精确等于这个值(不带 charset)。</summary>
        public const string RequestContentType = "application/octet-stream";

        /// <summary>服务端接受的请求体上限(字节,含 base64 膨胀)。</summary>
        public const int MaxRequestBodyBytes = 4096;

        /// <summary>客户端自设的响应体上限(规格里没有),防止异常服务器灌爆内存;认证响应只有几十到一百多字节。</summary>
        public const int MaxResponseBytes = 64 * 1024;

        /// <summary>明文字段分隔符;任何字段含它都会被服务端判格式错误。</summary>
        public const char FieldSeparator = '|';

        /// <summary>明文命令名:登录、注册、找回、改密。</summary>
        public const string LoginCommand = "login";
        public const string RegisterCommand = "regedit";
        public const string ForgotCommand = "forget";
        public const string ChangePasswordCommand = "repasswd";

        /// <summary>登录命令的 mac 字段:服务端不使用,规格说"随便填一个";用一个形状正常的占位串。</summary>
        public const string DefaultMac = "00:00:00:00:00:00";

        /// <summary>用户名长度范围(服务端校验;客户端提前挡掉,省一次限流额度)。</summary>
        public const int UsernameMinLength = 4;
        public const int UsernameMaxLength = 32;

        /// <summary>口令 MD5 的十六进制长度。</summary>
        public const int Md5HexLength = 32;

        /// <summary>AES 密钥的十六进制长度(32 字节)。</summary>
        public const int AesKeyHexLength = 64;

        /// <summary>登录成功响应的前缀:<c>0|令牌</c>。</summary>
        public const string TokenPrefix = "0|";

        /// <summary>注册/改密/找回成功的响应文本。</summary>
        public const string SuccessText = "success";

        /// <summary>服务端按 IP 限流:每个窗口最多这么多次认证请求(成功失败都计数)。</summary>
        public const int RateLimitMaxRequests = 10;

        /// <summary>限流窗口长度。</summary>
        public static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(60);

        /// <summary>已知错误键与枚举的对应(顺序无意义;未知键映射到 <see cref="GatewayErrorCode.Unknown"/> 并保留原文)。</summary>
        private static readonly string[] KnownErrorKeys =
        {
            "wrong_credentials", "account_banned", "registration_closed", "user_exists",
            "invalid_username", "invalid_password", "invalid_qq", "fail",
        };

        private static readonly GatewayErrorCode[] KnownErrorCodes =
        {
            GatewayErrorCode.WrongCredentials, GatewayErrorCode.AccountBanned, GatewayErrorCode.RegistrationClosed, GatewayErrorCode.UserExists,
            GatewayErrorCode.InvalidUsername, GatewayErrorCode.InvalidPassword, GatewayErrorCode.InvalidQq, GatewayErrorCode.Fail,
        };

        /// <summary>health 响应里"时间戳"字段可能的键名(规格只说"含时间戳",键名未核实,按顺序取第一个出现的)。</summary>
        public static readonly string[] HealthTimestampKeys = { "timestamp", "time", "ts" };

        /// <summary>错误键转枚举;大小写不敏感,未知键返回 <see cref="GatewayErrorCode.Unknown"/>。</summary>
        public static GatewayErrorCode ErrorCodeOf(string key)
        {
            if (key != null)
            {
                for (int i = 0; i < KnownErrorKeys.Length; i++)
                {
                    if (string.Equals(KnownErrorKeys[i], key, StringComparison.OrdinalIgnoreCase))
                    {
                        return KnownErrorCodes[i];
                    }
                }
            }
            return GatewayErrorCode.Unknown;
        }

        /// <summary>
        /// 判断文本是不是 IP 地址(用于 game-server-ip 的响应):IPv4 必须是四段十进制、每段 0-255、不带前导零(前导零在不同解析器里有八进制歧义);
        /// IPv6 只收纯地址(不带作用域、端口、方括号)。不接受主机名与"1"这类缩写(<see cref="IPAddress.TryParse(string, out IPAddress)"/> 会收下它们)。
        /// </summary>
        public static bool IsIpText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.IndexOf(':') < 0)
            {
                return IsIpv4Text(text);
            }
            foreach (char c in text)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex && c != ':' && c != '.')
                {
                    return false;
                }
            }
            IPAddress address;
            return IPAddress.TryParse(text, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private static bool IsIpv4Text(string text)
        {
            string[] parts = text.Split('.');
            if (parts.Length != 4)
            {
                return false;
            }
            foreach (string part in parts)
            {
                if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0'))
                {
                    return false;
                }
                int value = 0;
                foreach (char c in part)
                {
                    if (c < '0' || c > '9')
                    {
                        return false;
                    }
                    value = value * 10 + (c - '0');
                }
                if (value > 255)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
