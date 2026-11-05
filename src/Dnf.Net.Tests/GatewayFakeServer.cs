using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>
/// 内存里的假网关(只用于测试):按公开规格独立实现服务端一侧——用 .NET 的 <see cref="AesGcm"/> 解请求、加密响应,
/// 不使用被测库里的任何编解码代码;限流用自己的滑动窗口计数。账号规则里"qq 必须是 5–12 位数字"等细节是假网关自己定的,
/// 与真实网关无关,只为让客户端能走完各条分支。
/// </summary>
internal sealed class FakeGateway : IGatewayTransport
{
    private static readonly Regex UserRule = new("^[A-Za-z0-9_]{4,32}$");
    private static readonly Regex Md5Rule = new("^[0-9a-fA-F]{32}$");
    private static readonly Regex QqRule = new("^[0-9]{5,12}$");

    private readonly byte[] _key;
    private readonly object _lock = new();
    private readonly List<double> _hits = new();
    private readonly Func<double> _nowSeconds;

    public FakeGateway(byte[] key, Func<double> nowSeconds = null)
    {
        _key = key;
        _nowSeconds = nowSeconds ?? (() => 0.0);
    }

    public sealed class Account
    {
        public string Md5;
        public string Qq;
    }

    // ---- 可配置的行为 ----
    public Dictionary<string, Account> Accounts { get; } = new();
    public HashSet<string> Banned { get; } = new();
    public bool RegistrationOpen { get; set; } = true;
    public string GameServerIp { get; set; } = "127.0.0.1";
    public string HealthJson { get; set; } = "{\"status\":\"ok\",\"timestamp\":1730000000}";
    public int RateLimitMax { get; set; } = 10;
    public double RateWindowSeconds { get; set; } = 60;
    public TimeSpan? RetryAfterHeader { get; set; }
    public int? ForceStatus { get; set; }
    public string ForceBody { get; set; }
    public string ForceReplyPlaintext { get; set; }
    public Func<string, string> TamperResponse { get; set; }
    public Exception ThrowOnRequest { get; set; }
    public Func<CancellationToken, Task> BeforeRespond { get; set; }

    // ---- 观测 ----
    public int GetCount { get; private set; }
    public int PostCount { get; private set; }
    public int RejectedByRateLimit { get; private set; }
    public List<string> Urls { get; } = new();
    public List<string> PostBodies { get; } = new();
    public List<string> PostPlaintexts { get; } = new();
    public string LastToken { get; private set; }

    public async Task<GatewayHttpResponse> GetTextAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeRespond != null)
        {
            await BeforeRespond(cancellationToken);
        }
        return Handle("GET", url, null, null);
    }

    public async Task<GatewayHttpResponse> PostTextAsync(string url, string body, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeRespond != null)
        {
            await BeforeRespond(cancellationToken);
        }
        return Handle("POST", url, GatewayProtocol.RequestContentType, body);
    }

    public GatewayHttpResponse Handle(string method, string url, string contentType, string body)
    {
        lock (_lock)
        {
            Urls.Add(url);
            if (ThrowOnRequest != null)
            {
                throw ThrowOnRequest;
            }
            string path = new Uri(url).AbsolutePath;
            if (method == "GET")
            {
                GetCount++;
            }
            else
            {
                PostCount++;
            }
            if (ForceStatus.HasValue)
            {
                return new GatewayHttpResponse(ForceStatus.Value, ForceBody ?? string.Empty, RetryAfterHeader);
            }
            if (method == "GET" && path == "/health")
            {
                return new GatewayHttpResponse(200, HealthJson);
            }
            if (method == "GET" && path == "/api/v1/game-server-ip")
            {
                return GameServerIp == null ? new GatewayHttpResponse(404, string.Empty) : new GatewayHttpResponse(200, GameServerIp);
            }
            if (method == "POST" && path == "/api/v1/auth")
            {
                return HandleAuth(contentType, body);
            }
            return new GatewayHttpResponse(404, string.Empty);
        }
    }

    private GatewayHttpResponse HandleAuth(string contentType, string body)
    {
        // 限流先于一切(成功失败都计数)
        double now = _nowSeconds();
        _hits.RemoveAll(t => now - t >= RateWindowSeconds);
        if (_hits.Count >= RateLimitMax)
        {
            RejectedByRateLimit++;
            return new GatewayHttpResponse(429, string.Empty, RetryAfterHeader);
        }
        _hits.Add(now);
        PostBodies.Add(body);

        if (contentType != GatewayProtocol.RequestContentType)
        {
            return new GatewayHttpResponse(415, string.Empty);
        }
        if (body == null || Encoding.UTF8.GetByteCount(body) > 4096)
        {
            return new GatewayHttpResponse(413, string.Empty);
        }
        Span<byte> wire = new byte[body.Length];
        if (!Convert.TryFromBase64String(body, wire, out int written) || written < 28)
        {
            return new GatewayHttpResponse(400, string.Empty);
        }
        byte[] data = wire.Slice(0, written).ToArray();
        byte[] nonce = data[..12];
        byte[] cipher = data[12..^16];
        byte[] tag = data[^16..];
        byte[] plain = new byte[cipher.Length];
        try
        {
            using AesGcm gcm = new(_key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            return new GatewayHttpResponse(400, string.Empty);
        }
        string text = Encoding.UTF8.GetString(plain);
        PostPlaintexts.Add(text);
        string[] fields = text.Split('|');
        if (fields.Length != 4)
        {
            return new GatewayHttpResponse(400, string.Empty);
        }
        string reply = ForceReplyPlaintext ?? Dispatch(fields);
        if (reply == null)
        {
            return new GatewayHttpResponse(400, string.Empty);
        }
        string encoded = Encrypt(reply);
        if (TamperResponse != null)
        {
            encoded = TamperResponse(encoded);
        }
        return new GatewayHttpResponse(200, encoded);
    }

    private string Dispatch(string[] f)
    {
        string user = f[1];
        switch (f[0])
        {
            case "login":
                if (!UserRule.IsMatch(user)) return "invalid_username";
                if (!Md5Rule.IsMatch(f[2])) return "invalid_password";
                if (!Accounts.TryGetValue(user, out Account acc) || acc.Md5 != f[2]) return "wrong_credentials";
                if (Banned.Contains(user)) return "account_banned";
                LastToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(46));
                return "0|" + LastToken;
            case "regedit":
                if (!RegistrationOpen) return "registration_closed";
                if (!UserRule.IsMatch(user)) return "invalid_username";
                if (!Md5Rule.IsMatch(f[2])) return "invalid_password";
                if (f[3].Length != 0 && !QqRule.IsMatch(f[3])) return "invalid_qq";
                if (Accounts.ContainsKey(user)) return "user_exists";
                Accounts[user] = new Account { Md5 = f[2], Qq = f[3] };
                return "success";
            case "forget":
                if (!Accounts.TryGetValue(user, out Account known) || known.Qq != f[2]) return "fail";
                if (!Md5Rule.IsMatch(f[3])) return "invalid_password";
                known.Md5 = f[3];
                return "success";
            case "repasswd":
                if (!Accounts.TryGetValue(user, out Account owner) || owner.Md5 != f[2]) return "wrong_credentials";
                if (!Md5Rule.IsMatch(f[3])) return "invalid_password";
                owner.Md5 = f[3];
                return "success";
            default:
                return null;
        }
    }

    /// <summary>服务端一侧的加密:随机 nonce ‖ 密文 ‖ 标签,标准 base64。</summary>
    public string Encrypt(string plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[data.Length];
        byte[] tag = new byte[16];
        using AesGcm gcm = new(_key, 16);
        gcm.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(cipher).Concat(tag).ToArray());
    }

    /// <summary>用假网关的视角解开客户端发来的请求体(测试里检查线上内容用);失败返回 null。</summary>
    public string DecryptRequest(string body)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return null;
        }
        if (data.Length < 28)
        {
            return null;
        }
        byte[] plain = new byte[data.Length - 28];
        try
        {
            using AesGcm gcm = new(_key, 16);
            gcm.Decrypt(data[..12], data[12..^16], data[^16..], plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>把 <see cref="FakeGateway"/> 接到 <see cref="HttpMessageHandler"/> 上,用来端到端测试 <see cref="HttpGatewayTransport"/>(不连任何真实网络)。</summary>
internal sealed class FakeGatewayHandler : HttpMessageHandler
{
    private readonly FakeGateway _gateway;

    public FakeGatewayHandler(FakeGateway gateway)
    {
        _gateway = gateway;
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        string body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        string contentType = request.Content?.Headers.ContentType?.ToString();
        GatewayHttpResponse r = _gateway.Handle(request.Method.Method, request.RequestUri.ToString(), contentType, body);
        HttpResponseMessage response = new((HttpStatusCode)r.StatusCode) { Content = new StringContent(r.Text ?? string.Empty) };
        if (r.RetryAfter.HasValue)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(r.RetryAfter.Value);
        }
        return response;
    }
}

/// <summary>用委托写任意行为的处理器。</summary>
internal sealed class DelegateHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _send = send;
    }

    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return _send(request, cancellationToken);
    }
}
