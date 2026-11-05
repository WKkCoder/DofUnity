using System;
using System.Text;

namespace Dnf.Net.Gateway
{
    /// <summary>一次网关调用的结局类别。</summary>
    public enum GatewayOutcome
    {
        /// <summary>成功:登录拿到令牌、注册/改密/找回成功、查询类请求拿到了数据。</summary>
        Success = 0,

        /// <summary>网关解密并处理了请求,但业务上失败(见 <see cref="GatewayResult.ErrorCode"/> 与 <see cref="GatewayResult.ErrorKey"/>)。</summary>
        ServerError = 1,

        /// <summary>被限流:服务端回 429,或客户端自己的限流器没放行(此时 <see cref="GatewayResult.HttpStatus"/> 为 0,没发出请求)。</summary>
        RateLimited = 2,

        /// <summary>传输级错误:网络异常/超时,或服务端回了 2xx 以外的状态码(400 通常表示解密失败或格式错误)。响应体不会被解密。</summary>
        TransportError = 3,

        /// <summary>服务端回了 2xx,但响应解不出来或形状不对(解密失败、空令牌、该有令牌却没有等)。</summary>
        ProtocolError = 4,

        /// <summary>本地字段校验没过,没发出任何请求,也没占用限流额度。</summary>
        InvalidInput = 5,

        /// <summary>服务端没有配置游戏服 IP(<c>game-server-ip</c> 回 404);这是正常状态,不是错误。</summary>
        NotConfigured = 6,
    }

    /// <summary>已知的业务错误键;未知键是 <see cref="Unknown"/>,原文在 <see cref="GatewayResult.ErrorKey"/>。</summary>
    public enum GatewayErrorCode
    {
        None = 0,
        Unknown = 1,
        WrongCredentials = 2,
        AccountBanned = 3,
        RegistrationClosed = 4,
        UserExists = 5,
        InvalidUsername = 6,
        InvalidPassword = 7,
        InvalidQq = 8,
        Fail = 9,
    }

    /// <summary>health 响应里取出的两个字段(都按原样保留成文本;状态词的含义留给调用方判断,规格没有给出取值集合)。</summary>
    public sealed class GatewayHealth
    {
        public GatewayHealth(string status, string timestamp)
        {
            Status = status;
            Timestamp = timestamp;
        }

        /// <summary>status 字段的文本。</summary>
        public string Status { get; }

        /// <summary>时间戳字段的文本(数字按字面、字符串去引号);响应里没有时为 null。</summary>
        public string Timestamp { get; }
    }

    /// <summary>
    /// 网关调用的结果(不可变)。不抛异常表达业务/传输失败,调用方按 <see cref="Outcome"/> 分支。
    /// 令牌是敏感文本:<see cref="ToString"/> 与 <see cref="Message"/> 只给出前 4 个字符加长度,从不输出完整令牌。
    /// </summary>
    public sealed class GatewayResult
    {
        private GatewayResult(GatewayOutcome outcome, int httpStatus, string message)
        {
            Outcome = outcome;
            HttpStatus = httpStatus;
            Message = message ?? string.Empty;
        }

        /// <summary>结局类别。</summary>
        public GatewayOutcome Outcome { get; private set; }

        /// <summary>是否成功(<see cref="GatewayOutcome.Success"/>)。</summary>
        public bool IsSuccess
        {
            get { return Outcome == GatewayOutcome.Success; }
        }

        /// <summary>登录成功时的令牌:不透明文本,原样保存、原样交给游戏服的登录包,不解析、不校验、不截断;其他情况为 null。</summary>
        public string Token { get; private set; }

        /// <summary>查询类请求的文本载荷(游戏服 IP,已去空白);其他情况为 null。</summary>
        public string Text { get; private set; }

        /// <summary>health 成功时的内容;其他情况为 null。</summary>
        public GatewayHealth Health { get; private set; }

        /// <summary><see cref="GatewayOutcome.ServerError"/> 时的错误枚举;其他情况为 <see cref="GatewayErrorCode.None"/>。</summary>
        public GatewayErrorCode ErrorCode { get; private set; }

        /// <summary><see cref="GatewayOutcome.ServerError"/> 时服务端给的错误键原文(未知键也在这里);其他情况为 null。</summary>
        public string ErrorKey { get; private set; }

        /// <summary>HTTP 状态码;0 表示没有拿到响应(没发出请求,或传输异常)。</summary>
        public int HttpStatus { get; private set; }

        /// <summary><see cref="GatewayOutcome.RateLimited"/> 时建议等待的秒数(至少 1);其他情况为 0。</summary>
        public int RetryAfterSeconds { get; private set; }

        /// <summary>传输异常的原始异常(仅供调试;<see cref="ToString"/> 不输出它)。</summary>
        public Exception TransportException { get; private set; }

        /// <summary>给人看的诊断文字:不含令牌、口令、口令 MD5、服务器地址。</summary>
        public string Message { get; private set; }

        /// <summary>令牌的日志安全形式:前 4 个字符加长度;4 个字符及以下只给长度。</summary>
        public static string DescribeToken(string token)
        {
            if (token == null)
            {
                return "(no token)";
            }
            if (token.Length <= 4)
            {
                return "(len " + token.Length + ")";
            }
            return token.Substring(0, 4) + "...(len " + token.Length + ")";
        }

        /// <summary>把可能来自服务端的文本变成适合放进日志的短文本:控制字符换成 ?,最多 64 个字符。</summary>
        public static string Sanitize(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }
            int max = Math.Min(text.Length, 64);
            StringBuilder sb = new StringBuilder(max + 3);
            for (int i = 0; i < max; i++)
            {
                char c = text[i];
                sb.Append(c < ' ' || c == '\u007f' ? '?' : c);
            }
            if (text.Length > max)
            {
                sb.Append("...");
            }
            return sb.ToString();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder("GatewayResult(").Append(Outcome);
            if (Token != null)
            {
                sb.Append(", token ").Append(DescribeToken(Token));
            }
            if (Text != null)
            {
                sb.Append(", text ").Append(Sanitize(Text));
            }
            if (Health != null)
            {
                sb.Append(", health ").Append(Sanitize(Health.Status));
            }
            if (ErrorKey != null)
            {
                sb.Append(", error ").Append(ErrorCode).Append(' ').Append(Sanitize(ErrorKey));
            }
            if (RetryAfterSeconds > 0)
            {
                sb.Append(", retry in ").Append(RetryAfterSeconds).Append("s");
            }
            if (HttpStatus != 0)
            {
                sb.Append(", http ").Append(HttpStatus);
            }
            if (Message.Length > 0)
            {
                sb.Append(": ").Append(Message);
            }
            return sb.Append(')').ToString();
        }

        // ---- 工厂(仅库内使用;结果由 GatewayResponseParser 与 GatewayClient 生成) ----

        internal static GatewayResult WithToken(string token)
        {
            GatewayResult r = new GatewayResult(GatewayOutcome.Success, 0, null);
            r.Token = token;
            return r;
        }

        internal static GatewayResult PlainSuccess()
        {
            return new GatewayResult(GatewayOutcome.Success, 0, null);
        }

        internal static GatewayResult WithText(string text, int httpStatus)
        {
            GatewayResult r = new GatewayResult(GatewayOutcome.Success, httpStatus, null);
            r.Text = text;
            return r;
        }

        internal static GatewayResult WithHealth(GatewayHealth health, int httpStatus)
        {
            GatewayResult r = new GatewayResult(GatewayOutcome.Success, httpStatus, null);
            r.Health = health;
            return r;
        }

        internal static GatewayResult ServerError(string key, GatewayErrorCode code)
        {
            GatewayResult r = new GatewayResult(GatewayOutcome.ServerError, 0, "server reported an error");
            r.ErrorKey = key;
            r.ErrorCode = code;
            return r;
        }

        internal static GatewayResult RateLimited(int retryAfterSeconds, int httpStatus, string message)
        {
            GatewayResult r = new GatewayResult(GatewayOutcome.RateLimited, httpStatus, message);
            // 秒数由 GatewayClient 保证在 [1, 3600];这里不再重复夹取(内部工厂,唯一调用点是客户端)
            r.RetryAfterSeconds = retryAfterSeconds;
            return r;
        }

        internal static GatewayResult Transport(int httpStatus, string message, Exception exception)
        {
            GatewayResult r = new GatewayResult(GatewayOutcome.TransportError, httpStatus, message);
            r.TransportException = exception;
            return r;
        }

        internal static GatewayResult Protocol(string message)
        {
            return new GatewayResult(GatewayOutcome.ProtocolError, 0, message);
        }

        internal static GatewayResult Invalid(string message)
        {
            return new GatewayResult(GatewayOutcome.InvalidInput, 0, message);
        }

        internal static GatewayResult NotConfigured(int httpStatus)
        {
            return new GatewayResult(GatewayOutcome.NotConfigured, httpStatus, "game server address is not configured on the gateway");
        }

        /// <summary>复制并带上 HTTP 状态码(解析器不知道状态码,由客户端补上)。</summary>
        internal GatewayResult WithHttpStatus(int httpStatus)
        {
            GatewayResult r = (GatewayResult)MemberwiseClone();
            r.HttpStatus = httpStatus;
            return r;
        }
    }
}
