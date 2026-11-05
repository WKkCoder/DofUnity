using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 网关客户端:登录、注册、改密、找回口令、查询游戏服 IP、健康检查。所有结局(含业务失败、限流、传输失败)都用 <see cref="GatewayResult"/> 表达,
    /// 不抛异常;唯一例外是调用方自己取消(<see cref="OperationCanceledException"/>)。
    /// 口令只在本地算 MD5 后发送;令牌、口令、MD5 不进任何结果文本与异常消息。每次认证请求先过 <see cref="GatewayRateLimiter"/>,
    /// 本地字段校验失败的请求不发出、不占额度。非 2xx 一律是传输级结果,响应体不解密(429 单列为限流结果,带建议等待秒数)。
    /// </summary>
    public sealed class GatewayClient : IDisposable
    {
        /// <summary>服务端 Retry-After 的采信上限:防止异常服务器把客户端锁上很久。</summary>
        private const int MaxRetryAfterSeconds = 3600;

        private readonly GatewayOptions _options;
        private readonly byte[] _key;
        private readonly IGatewayTransport _transport;
        private readonly GatewayRateLimiter _limiter;
        private readonly IGatewayClock _clock;
        private readonly INonceSource _nonces;
        private readonly bool _ownsTransport;

        /// <summary>用给定的传输创建(传输由调用方负责释放)。限流器、时钟、nonce 源为 null 时用默认实现。</summary>
        public GatewayClient(GatewayOptions options, IGatewayTransport transport, GatewayRateLimiter limiter = null,
            IGatewayClock clock = null, INonceSource nonceSource = null)
            : this(options, transport, limiter, clock, nonceSource, false)
        {
        }

        private GatewayClient(GatewayOptions options, IGatewayTransport transport, GatewayRateLimiter limiter,
            IGatewayClock clock, INonceSource nonceSource, bool ownsTransport)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }
            _options = options;
            _key = options.CopyAesKey();
            _transport = transport;
            _limiter = limiter ?? new GatewayRateLimiter();
            _clock = clock ?? StopwatchGatewayClock.Default;
            _nonces = nonceSource ?? RandomNonceSource.Default;
            _ownsTransport = ownsTransport;
        }

        /// <summary>用默认的 <see cref="HttpGatewayTransport"/> 创建;释放客户端时一并释放它。</summary>
        public static GatewayClient Create(GatewayOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            return new GatewayClient(options, new HttpGatewayTransport(options.Timeout), null, null, null, true);
        }

        /// <summary>客户端使用的限流器(界面可以用它的 <see cref="GatewayRateLimiter.RetryAfter"/> 显示倒计时)。</summary>
        public GatewayRateLimiter RateLimiter
        {
            get { return _limiter; }
        }

        /// <summary>登录。成功时 <see cref="GatewayResult.Token"/> 是要原样交给游戏服的令牌;口令传明文,本方法只发它的 MD5。</summary>
        public Task<GatewayResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            return AuthAsync(() => GatewayCommands.Login(username, PasswordMd5(password)), true, cancellationToken);
        }

        /// <summary>注册。qq 可为空。</summary>
        public Task<GatewayResult> RegisterAsync(string username, string password, string qq = null, CancellationToken cancellationToken = default)
        {
            return AuthAsync(() => GatewayCommands.Register(username, PasswordMd5(password), qq), false, cancellationToken);
        }

        /// <summary>修改口令。</summary>
        public Task<GatewayResult> ChangePasswordAsync(string username, string oldPassword, string newPassword, CancellationToken cancellationToken = default)
        {
            return AuthAsync(() => GatewayCommands.ChangePassword(username, PasswordMd5(oldPassword), PasswordMd5(newPassword)), false, cancellationToken);
        }

        /// <summary>找回口令(用注册时的 qq 校验身份)。</summary>
        public Task<GatewayResult> ForgotPasswordAsync(string username, string qq, string newPassword, CancellationToken cancellationToken = default)
        {
            return AuthAsync(() => GatewayCommands.ForgotPassword(username, qq, PasswordMd5(newPassword)), false, cancellationToken);
        }

        /// <summary>查询游戏服 IP:成功时 <see cref="GatewayResult.Text"/> 是去掉空白的 IP;服务端没配置(404)是 <see cref="GatewayOutcome.NotConfigured"/>,Text 为 null。</summary>
        public Task<GatewayResult> GetGameServerIpAsync(CancellationToken cancellationToken = default)
        {
            return GetAsync(GatewayProtocol.GameServerIpRoute, response =>
            {
                if (response.StatusCode == 404)
                {
                    return GatewayResult.NotConfigured(404);
                }
                if (!response.IsSuccessStatus)
                {
                    return NonSuccess(response, false);
                }
                string ip = (response.Text ?? string.Empty).Trim();
                if (!GatewayProtocol.IsIpText(ip))
                {
                    return GatewayResult.Protocol("game server address response is not an IP address").WithHttpStatus(response.StatusCode);
                }
                return GatewayResult.WithText(ip, response.StatusCode);
            }, cancellationToken);
        }

        /// <summary>健康检查:成功时 <see cref="GatewayResult.Health"/> 给出 status 与时间戳文本。</summary>
        public Task<GatewayResult> HealthAsync(CancellationToken cancellationToken = default)
        {
            return GetAsync(GatewayProtocol.HealthRoute, response =>
            {
                if (!response.IsSuccessStatus)
                {
                    return NonSuccess(response, false);
                }
                GatewayHealth health;
                if (!GatewayHealthParser.TryParse(response.Text, out health))
                {
                    return GatewayResult.Protocol("health response is not the expected JSON object").WithHttpStatus(response.StatusCode);
                }
                return GatewayResult.WithHealth(health, response.StatusCode);
            }, cancellationToken);
        }

        /// <summary>释放自己创建的传输(用调用方给的传输构造时什么都不做)。</summary>
        public void Dispose()
        {
            if (_ownsTransport)
            {
                IDisposable disposable = _transport as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
        }

        private async Task<GatewayResult> AuthAsync(Func<string> buildPlaintext, bool expectToken, CancellationToken cancellationToken)
        {
            // 已经取消的调用不占限流额度、不发请求
            cancellationToken.ThrowIfCancellationRequested();
            string body;
            try
            {
                body = GatewayCodec.Encode(_key, buildPlaintext(), _nonces);
            }
            catch (ArgumentException e)
            {
                // 字段校验的消息只含字段名与规则,不含字段值
                return GatewayResult.Invalid(e.Message);
            }
            if (body.Length > GatewayProtocol.MaxRequestBodyBytes)
            {
                return GatewayResult.Invalid("request is larger than the " + GatewayProtocol.MaxRequestBodyBytes + " bytes the gateway accepts");
            }
            TimeSpan now = _clock.Now;
            if (!_limiter.TryAcquire(now))
            {
                return GatewayResult.RateLimited(SecondsOf(_limiter.RetryAfter(now), MaxRetryAfterSeconds), 0, "client-side rate limit reached, wait before trying again");
            }
            GatewayHttpResponse response;
            try
            {
                response = await _transport.PostTextAsync(_options.UrlFor(GatewayProtocol.AuthRoute), body, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return TransportFailure(e);
            }
            if (!response.IsSuccessStatus)
            {
                return NonSuccess(response, true);
            }
            string plaintext;
            if (!GatewayCodec.TryDecode(_key, response.Text, out plaintext))
            {
                return GatewayResult.Protocol("response could not be decrypted or authenticated").WithHttpStatus(response.StatusCode);
            }
            GatewayResult parsed = GatewayResponseParser.Parse(plaintext);
            if (parsed.IsSuccess)
            {
                if (expectToken && parsed.Token == null)
                {
                    return GatewayResult.Protocol("login succeeded without a token").WithHttpStatus(response.StatusCode);
                }
                if (!expectToken && parsed.Token != null)
                {
                    return GatewayResult.Protocol("unexpected token in the response").WithHttpStatus(response.StatusCode);
                }
            }
            return parsed.WithHttpStatus(response.StatusCode);
        }

        private async Task<GatewayResult> GetAsync(string route, Func<GatewayHttpResponse, GatewayResult> handle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GatewayHttpResponse response;
            try
            {
                response = await _transport.GetTextAsync(_options.UrlFor(route), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return TransportFailure(e);
            }
            return handle(response);
        }

        /// <summary>非 2xx:429 是限流结果(认证请求之后还要让本地限流器冷却同样长的时间),其余一律是传输级结果。响应体不解密。</summary>
        private GatewayResult NonSuccess(GatewayHttpResponse response, bool isAuth)
        {
            if (response.StatusCode == 429)
            {
                int seconds = SecondsOf(response.RetryAfter ?? _limiter.Window, MaxRetryAfterSeconds);
                if (isAuth)
                {
                    _limiter.Penalize(_clock.Now, TimeSpan.FromSeconds(seconds));
                }
                return GatewayResult.RateLimited(seconds, 429, "the gateway reported too many requests");
            }
            return GatewayResult.Transport(response.StatusCode, "the gateway answered HTTP " + response.StatusCode, null);
        }

        private static GatewayResult TransportFailure(Exception e)
        {
            // 只写异常类型名:异常消息里可能带请求地址,而地址是用户的服务器配置
            string message = e is TimeoutException ? "the request timed out" : "transport failure (" + e.GetType().Name + ")";
            return GatewayResult.Transport(0, message, e);
        }

        private static string PasswordMd5(string password)
        {
            if (password == null)
            {
                throw new ArgumentException("password is required");
            }
            return GatewayCommands.Md5Hex(password);
        }

        /// <summary>时间向上取整成秒,夹在 [1, max]。</summary>
        private static int SecondsOf(TimeSpan span, int max)
        {
            double seconds = Math.Ceiling(span.TotalSeconds);
            if (seconds < 1)
            {
                return 1;
            }
            return seconds > max ? max : (int)seconds;
        }
    }
}
