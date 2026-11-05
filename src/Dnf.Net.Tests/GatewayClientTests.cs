using System.Net.Http;
using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>
/// 客户端对内存假网关(<see cref="FakeGateway"/>,独立实现的服务端一侧)的行为:各命令的往返、错误映射、限流、传输失败、取消、
/// 以及"令牌/口令/MD5/地址不进结果文本"。假网关的账号数据是测试自己写的,口令 MD5 用手算常量。
/// </summary>
public sealed class GatewayClientTests
{
    private const string Url = "http://127.0.0.1:8080";
    private const string UserName = "tester01";
    private const string PwOne = "Passw0rd!";
    private const string PwOneMd5 = "47b7bfb65fa83ac9a71dcb0f6296bb6e";
    private const string PwTwo = "NewPass#2";
    private const string PwTwoMd5 = "e12cb76de29932284421ad0127a51bd3";

    private sealed class Rig
    {
        public readonly byte[] SynthKey = GatewayTestSupport.SynthKey(21);
        public readonly ManualClock Clock = new();
        public readonly GatewayRateLimiter Limiter = new();
        public readonly CountingNonceSource Nonces = new();
        public readonly FakeGateway Gateway;
        public readonly GatewayClient Client;

        public Rig(IGatewayTransport transport = null)
        {
            Gateway = new FakeGateway(SynthKey, () => Clock.Now.TotalSeconds);
            GatewayOptions options = new(Url, GatewayTestSupport.HexLower(SynthKey));
            Client = new GatewayClient(options, transport ?? Gateway, Limiter, Clock, Nonces);
        }

        public Rig WithAccount(string user = UserName, string md5 = PwOneMd5, string qq = "")
        {
            Gateway.Accounts[user] = new FakeGateway.Account { Md5 = md5, Qq = qq };
            return this;
        }

        public string LastPlaintext()
        {
            return Gateway.PostPlaintexts[Gateway.PostPlaintexts.Count - 1];
        }
    }

    // ---------- 登录 ----------

    [Fact]
    public async Task LoginSucceedsAndReturnsTheServersToken()
    {
        Rig rig = new Rig().WithAccount();
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);

        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.True(r.IsSuccess);
        Assert.NotNull(rig.Gateway.LastToken);
        Assert.Equal(rig.Gateway.LastToken, r.Token);
        Assert.Equal(200, r.HttpStatus);
        Assert.Equal(GatewayErrorCode.None, r.ErrorCode);
        Assert.Equal(Url + "/api/v1/auth", rig.Gateway.Urls[0]);
        Assert.Equal(1, rig.Gateway.PostCount);
    }

    [Fact]
    public async Task LoginPlaintextHasTheDocumentedShapeAndOnlyTheMd5OfThePassword()
    {
        Rig rig = new Rig().WithAccount();
        await rig.Client.LoginAsync(UserName, PwOne);

        Assert.Equal("login|" + UserName + "|" + PwOneMd5 + "|00:00:00:00:00:00", rig.LastPlaintext());
        Assert.DoesNotContain(PwOne, rig.LastPlaintext());
        // 线上只有密文:请求体里找不到用户名、口令、口令 MD5
        string body = rig.Gateway.PostBodies[0];
        Assert.DoesNotContain(UserName, body);
        Assert.DoesNotContain(PwOneMd5, body);
        Assert.DoesNotContain(PwOne, body);
    }

    [Fact]
    public async Task RequestBodyIsStandardBase64OfNonceCiphertextTagUsingTheInjectedNonce()
    {
        Rig rig = new Rig().WithAccount();
        await rig.Client.LoginAsync(UserName, PwOne);
        await rig.Client.LoginAsync(UserName, PwOne);

        byte[] first = Convert.FromBase64String(rig.Gateway.PostBodies[0]);
        byte[] second = Convert.FromBase64String(rig.Gateway.PostBodies[1]);
        // CountingNonceSource 的第 n 个 nonce 是 12 个值为 n 的字节;线上前 12 字节必须就是它
        Assert.Equal(Enumerable.Repeat((byte)1, 12).ToArray(), first[..12]);
        Assert.Equal(Enumerable.Repeat((byte)2, 12).ToArray(), second[..12]);
        Assert.NotEqual(rig.Gateway.PostBodies[0], rig.Gateway.PostBodies[1]);
        // 长度 = 12 + 明文 + 16
        int plainLength = ("login|" + UserName + "|" + PwOneMd5 + "|00:00:00:00:00:00").Length;
        Assert.Equal(12 + plainLength + 16, first.Length);
    }

    [Fact]
    public async Task LoginWithAWrongPasswordIsAServerErrorNotAnException()
    {
        Rig rig = new Rig().WithAccount();
        GatewayResult r = await rig.Client.LoginAsync(UserName, "wrong-pw");
        Assert.Equal(GatewayOutcome.ServerError, r.Outcome);
        Assert.Equal(GatewayErrorCode.WrongCredentials, r.ErrorCode);
        Assert.Equal("wrong_credentials", r.ErrorKey);
        Assert.Null(r.Token);
        Assert.Equal(200, r.HttpStatus);
    }

    [Fact]
    public async Task LoginOfAnUnknownOrBannedAccount()
    {
        Rig rig = new Rig().WithAccount();
        GatewayResult unknown = await rig.Client.LoginAsync("nobody_here", PwOne);
        Assert.Equal(GatewayErrorCode.WrongCredentials, unknown.ErrorCode);

        rig.Gateway.Banned.Add(UserName);
        GatewayResult banned = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.ServerError, banned.Outcome);
        Assert.Equal(GatewayErrorCode.AccountBanned, banned.ErrorCode);
        Assert.Null(banned.Token);
    }

    [Fact]
    public async Task LoginTokenIsKeptVerbatimIncludingBase64Characters()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.ForceReplyPlaintext = "0|ab+c/d==|e";
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal("ab+c/d==|e", r.Token);
    }

    [Fact]
    public async Task LoginResponseWithoutATokenIsAProtocolError()
    {
        Rig rig = new Rig().WithAccount();
        foreach (string reply in new[] { "success", "0", "0|", "0|  " })
        {
            rig.Gateway.ForceReplyPlaintext = reply;
            rig.Clock.Advance(TimeSpan.FromSeconds(61));
            GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
            Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
            Assert.Null(r.Token);
        }
    }

    [Fact]
    public async Task ARegistrationResponseThatCarriesATokenIsAProtocolError()
    {
        Rig rig = new();
        rig.Gateway.ForceReplyPlaintext = "0|tok_zq9x";
        GatewayResult r = await rig.Client.RegisterAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
        Assert.Null(r.Token);
        Assert.DoesNotContain("tok_zq9x", r.ToString());
    }

    // ---------- 注册 / 改密 / 找回 ----------

    [Fact]
    public async Task RegisterThenLoginWorks()
    {
        Rig rig = new();
        GatewayResult reg = await rig.Client.RegisterAsync(UserName, PwOne, "12345678");
        Assert.Equal(GatewayOutcome.Success, reg.Outcome);
        Assert.Null(reg.Token);
        Assert.Equal("regedit|" + UserName + "|" + PwOneMd5 + "|12345678", rig.LastPlaintext());
        Assert.Equal(PwOneMd5, rig.Gateway.Accounts[UserName].Md5);

        GatewayResult login = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.Success, login.Outcome);
        Assert.NotNull(login.Token);
    }

    [Fact]
    public async Task RegisterWithoutAQqSendsAnEmptyLastField()
    {
        Rig rig = new();
        GatewayResult r = await rig.Client.RegisterAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal("regedit|" + UserName + "|" + PwOneMd5 + "|", rig.LastPlaintext());
    }

    [Fact]
    public async Task RegisterErrorsAreMapped()
    {
        Rig rig = new Rig().WithAccount();
        Assert.Equal(GatewayErrorCode.UserExists, (await rig.Client.RegisterAsync(UserName, PwOne)).ErrorCode);
        Assert.Equal(GatewayErrorCode.InvalidQq, (await rig.Client.RegisterAsync("another_one", PwOne, "12")).ErrorCode);
        rig.Gateway.RegistrationOpen = false;
        GatewayResult closed = await rig.Client.RegisterAsync("third_user", PwOne);
        Assert.Equal(GatewayOutcome.ServerError, closed.Outcome);
        Assert.Equal(GatewayErrorCode.RegistrationClosed, closed.ErrorCode);
    }

    [Fact]
    public async Task ChangePasswordSendsOldThenNewDigestAndTakesEffect()
    {
        Rig rig = new Rig().WithAccount();
        GatewayResult r = await rig.Client.ChangePasswordAsync(UserName, PwOne, PwTwo);
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal("repasswd|" + UserName + "|" + PwOneMd5 + "|" + PwTwoMd5, rig.LastPlaintext());

        Assert.Equal(GatewayErrorCode.WrongCredentials, (await rig.Client.LoginAsync(UserName, PwOne)).ErrorCode);
        Assert.Equal(GatewayOutcome.Success, (await rig.Client.LoginAsync(UserName, PwTwo)).Outcome);
        GatewayResult wrongOld = await rig.Client.ChangePasswordAsync(UserName, "not-the-old-one", "whatever1");
        Assert.Equal(GatewayErrorCode.WrongCredentials, wrongOld.ErrorCode);
    }

    [Fact]
    public async Task ForgotPasswordSendsQqThenTheNewDigest()
    {
        Rig rig = new Rig().WithAccount(qq: "12345678");
        GatewayResult wrong = await rig.Client.ForgotPasswordAsync(UserName, "87654321", PwTwo);
        Assert.Equal(GatewayOutcome.ServerError, wrong.Outcome);
        Assert.Equal(GatewayErrorCode.Fail, wrong.ErrorCode);
        Assert.Equal("forget|" + UserName + "|87654321|" + PwTwoMd5, rig.LastPlaintext());

        GatewayResult ok = await rig.Client.ForgotPasswordAsync(UserName, "12345678", PwTwo);
        Assert.Equal(GatewayOutcome.Success, ok.Outcome);
        Assert.Equal(PwTwoMd5, rig.Gateway.Accounts[UserName].Md5);
    }

    // ---------- 本地校验 ----------

    [Fact]
    public async Task InvalidInputIsReportedWithoutSendingOrSpendingAQuota()
    {
        Rig rig = new Rig().WithAccount();
        GatewayResult[] results =
        {
            await rig.Client.LoginAsync("bad", PwOne),
            await rig.Client.LoginAsync("bad|name", PwOne),
            await rig.Client.LoginAsync(null, PwOne),
            await rig.Client.LoginAsync(UserName, null),
            await rig.Client.RegisterAsync("bad name", PwOne),
            await rig.Client.RegisterAsync(UserName, null),
            await rig.Client.RegisterAsync(UserName, PwOne, "a|b"),
            await rig.Client.ChangePasswordAsync(UserName, null, PwTwo),
            await rig.Client.ChangePasswordAsync(UserName, PwOne, null),
            await rig.Client.ForgotPasswordAsync(UserName, "1|2", PwTwo),
            await rig.Client.ForgotPasswordAsync("x", "12345", PwTwo),
        };
        Assert.All(results, r =>
        {
            Assert.Equal(GatewayOutcome.InvalidInput, r.Outcome);
            Assert.False(r.IsSuccess);
            Assert.Equal(0, r.HttpStatus);
            Assert.Null(r.Token);
        });
        Assert.Equal(0, rig.Gateway.PostCount);
        Assert.Equal(0, rig.Limiter.Used(rig.Clock.Now));
    }

    [Fact]
    public async Task InvalidInputMessagesDoNotEchoTheValues()
    {
        Rig rig = new();
        GatewayResult badUser = await rig.Client.LoginAsync("bad|user_value", PwOne);
        GatewayResult badQq = await rig.Client.RegisterAsync(UserName, PwOne, "q|q_value");
        Assert.DoesNotContain("user_value", badUser.ToString());
        Assert.DoesNotContain("q_value", badQq.ToString());
        Assert.DoesNotContain(PwOne, badUser.ToString());
        Assert.DoesNotContain(PwOneMd5, badQq.ToString());
    }

    [Fact]
    public async Task RequestBodyLimitIsFourThousandNinetySixBytes()
    {
        // 用户名 4 个字符时明文长度 = 46 + qq 长度;密文 = 明文 + 28 字节,base64 每 3 字节 4 个字符
        // qq = 2998 → 明文 3044 → 3072 字节 → 恰好 4096 个字符(放行);2999 → 3073 字节 → 4100 个字符(拦下)
        Rig rig = new();
        GatewayResult atLimit = await rig.Client.RegisterAsync("abcd", PwOne, new string('a', 2998));
        Assert.Equal(1, rig.Gateway.PostCount);
        Assert.Equal(4096, rig.Gateway.PostBodies[0].Length);
        Assert.Equal(GatewayOutcome.ServerError, atLimit.Outcome);
        Assert.Equal(GatewayErrorCode.InvalidQq, atLimit.ErrorCode);

        GatewayResult over = await rig.Client.RegisterAsync("abcd", PwOne, new string('a', 2999));
        Assert.Equal(GatewayOutcome.InvalidInput, over.Outcome);
        Assert.Equal(1, rig.Gateway.PostCount);
        Assert.Contains("4096", over.Message);
        Assert.Equal(1, rig.Limiter.Used(rig.Clock.Now));
    }

    // ---------- 客户端限流 ----------

    [Fact]
    public async Task TheEleventhRequestInAWindowIsHeldBackLocallyAndNeverSent()
    {
        Rig rig = new Rig().WithAccount();
        for (int i = 0; i < 10; i++)
        {
            // 失败的登录同样占名额(服务端成功失败都计数)
            GatewayResult r = await rig.Client.LoginAsync(UserName, "wrong-pw");
            Assert.Equal(GatewayOutcome.ServerError, r.Outcome);
        }
        GatewayResult held = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, held.Outcome);
        Assert.Equal(0, held.HttpStatus);
        Assert.Equal(60, held.RetryAfterSeconds);
        Assert.Null(held.Token);
        Assert.Equal(10, rig.Gateway.PostCount);

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        GatewayResult again = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.Success, again.Outcome);
        Assert.Equal(11, rig.Gateway.PostCount);
        Assert.Equal(0, rig.Gateway.RejectedByRateLimit);
    }

    [Fact]
    public async Task RetryAfterIsTheCeilingOfTheRemainingWait()
    {
        Rig rig = new Rig().WithAccount();
        for (int i = 0; i < 10; i++)
        {
            await rig.Client.LoginAsync(UserName, "wrong-pw");
        }
        rig.Clock.Advance(TimeSpan.FromSeconds(20.5));
        GatewayResult held = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, held.Outcome);
        Assert.Equal(40, held.RetryAfterSeconds);

        rig.Clock.Advance(TimeSpan.FromSeconds(39.25));
        Assert.Equal(1, (await rig.Client.LoginAsync(UserName, PwOne)).RetryAfterSeconds);
    }

    [Fact]
    public async Task QuotaFollowsTheRealTrafficNotTheNumberOfCalls()
    {
        Rig rig = new Rig().WithAccount();
        // 20 次本地校验失败不占名额,之后仍然可以发满 10 次
        for (int i = 0; i < 20; i++)
        {
            await rig.Client.LoginAsync("bad", PwOne);
        }
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(GatewayOutcome.ServerError, (await rig.Client.LoginAsync(UserName, "wrong-pw")).Outcome);
        }
        Assert.Equal(10, rig.Gateway.PostCount);
    }

    [Fact]
    public async Task TransportFailuresStillSpendAQuota()
    {
        Rig rig = new();
        rig.Gateway.ThrowOnRequest = new HttpRequestException("boom");
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.TransportError, r.Outcome);
        Assert.Equal(1, rig.Limiter.Used(rig.Clock.Now));
    }

    [Fact]
    public async Task ReadOnlyQueriesDoNotSpendTheAuthQuota()
    {
        Rig rig = new();
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(GatewayOutcome.Success, (await rig.Client.HealthAsync()).Outcome);
            Assert.Equal(GatewayOutcome.Success, (await rig.Client.GetGameServerIpAsync()).Outcome);
        }
        Assert.Equal(0, rig.Limiter.Used(rig.Clock.Now));
        Assert.Equal(60, rig.Gateway.GetCount);
    }

    [Fact]
    public async Task ConcurrentLoginsNeverExceedTheLimitOnTheWire()
    {
        Rig rig = new Rig().WithAccount();
        Task<GatewayResult>[] calls = Enumerable.Range(0, 40).Select(_ => Task.Run(() => rig.Client.LoginAsync(UserName, PwOne))).ToArray();
        GatewayResult[] results = await Task.WhenAll(calls);
        Assert.Equal(10, rig.Gateway.PostCount);
        Assert.Equal(10, results.Count(r => r.Outcome == GatewayOutcome.Success));
        Assert.Equal(30, results.Count(r => r.Outcome == GatewayOutcome.RateLimited));
        Assert.Equal(0, rig.Gateway.RejectedByRateLimit);
    }

    // ---------- 服务端 429 ----------

    [Fact]
    public async Task ServerRateLimitIsReportedAndMakesTheClientBackOffForAWholeWindow()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.RateLimitMax = 2;
        await rig.Client.LoginAsync(UserName, PwOne);
        await rig.Client.LoginAsync(UserName, PwOne);

        GatewayResult limited = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, limited.Outcome);
        Assert.Equal(429, limited.HttpStatus);
        Assert.Equal(60, limited.RetryAfterSeconds);
        Assert.Equal(3, rig.Gateway.PostCount);
        Assert.Equal(1, rig.Gateway.RejectedByRateLimit);

        // 立刻再试:本地直接拦下,不再打扰服务器
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        GatewayResult held = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, held.Outcome);
        Assert.Equal(0, held.HttpStatus);
        Assert.Equal(50, held.RetryAfterSeconds);
        Assert.Equal(3, rig.Gateway.PostCount);

        rig.Clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(GatewayOutcome.Success, (await rig.Client.LoginAsync(UserName, PwOne)).Outcome);
    }

    [Fact]
    public async Task RetryAfterHeaderIsHonoredAndRoundedUpToWholeSeconds()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.ForceStatus = 429;
        rig.Gateway.RetryAfterHeader = TimeSpan.FromSeconds(7.2);
        GatewayResult limited = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, limited.Outcome);
        Assert.Equal(8, limited.RetryAfterSeconds);

        rig.Gateway.ForceStatus = null;
        rig.Clock.Advance(TimeSpan.FromMilliseconds(7900));
        GatewayResult stillHeld = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, stillHeld.Outcome);
        Assert.Equal(0, stillHeld.HttpStatus);

        rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(GatewayOutcome.Success, (await rig.Client.LoginAsync(UserName, PwOne)).Outcome);
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.2, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.01, 2)]
    [InlineData(3599.0, 3599)]
    [InlineData(3600.0, 3600)]
    [InlineData(100000.0, 3600)]
    public async Task RetryAfterIsClampedBetweenOneSecondAndOneHour(double headerSeconds, int expected)
    {
        Rig rig = new();
        rig.Gateway.ForceStatus = 429;
        rig.Gateway.RetryAfterHeader = TimeSpan.FromSeconds(headerSeconds);
        GatewayResult limited = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(expected, limited.RetryAfterSeconds);
    }

    [Fact]
    public async Task ARetryAfterOfZeroStillBacksOffForOneSecond()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.ForceStatus = 429;
        rig.Gateway.RetryAfterHeader = TimeSpan.Zero;
        GatewayResult limited = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, limited.Outcome);
        Assert.Equal(1, limited.RetryAfterSeconds);

        rig.Gateway.ForceStatus = null;
        GatewayResult held = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.RateLimited, held.Outcome);
        Assert.Equal(0, held.HttpStatus);
        Assert.Equal(1, rig.Gateway.PostCount);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GatewayOutcome.Success, (await rig.Client.LoginAsync(UserName, PwOne)).Outcome);
    }

    [Fact]
    public async Task ServerRateLimitOnReadOnlyRoutesDoesNotLockTheAuthQuota()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.ForceStatus = 429;
        GatewayResult h = await rig.Client.HealthAsync();
        Assert.Equal(GatewayOutcome.RateLimited, h.Outcome);
        Assert.Equal(429, h.HttpStatus);
        rig.Gateway.ForceStatus = null;
        Assert.Equal(GatewayOutcome.Success, (await rig.Client.LoginAsync(UserName, PwOne)).Outcome);
    }

    // ---------- 非 2xx 与响应完整性 ----------

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(199)]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    public async Task OtherStatusCodesAreTransportErrorsAndTheBodyIsNeverDecrypted(int status)
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.ForceStatus = status;
        rig.Gateway.ForceBody = rig.Gateway.Encrypt("0|tok_from_error_page");
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.TransportError, r.Outcome);
        Assert.Equal(status, r.HttpStatus);
        Assert.Null(r.Token);
        Assert.DoesNotContain("tok_from_error_page", r.ToString());
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(299)]
    public async Task EveryTwoHundredClassStatusIsAcceptedAndCarriesItsCode(int status)
    {
        Rig rig = new();
        rig.Gateway.ForceStatus = status;
        rig.Gateway.ForceBody = rig.Gateway.Encrypt("success");
        GatewayResult r = await rig.Client.RegisterAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal(status, r.HttpStatus);
    }

    [Fact]
    public async Task UndecryptableResponsesAreProtocolErrors()
    {
        Rig rig = new Rig().WithAccount();
        FakeGateway other = new(GatewayTestSupport.SynthKey(99));
        string[] bodies =
        {
            string.Empty,
            "not base64 at all",
            "AAAA",
            Convert.ToBase64String(new byte[28]),
            other.Encrypt("0|token_under_other_key"),
        };
        foreach (string body in bodies)
        {
            rig.Gateway.ForceStatus = 200;
            rig.Gateway.ForceBody = body;
            GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
            Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
            Assert.Null(r.Token);
            Assert.Equal(200, r.HttpStatus);
            Assert.DoesNotContain("token_under_other_key", r.ToString());
            rig.Clock.Advance(TimeSpan.FromSeconds(61));
        }
    }

    [Fact]
    public async Task ATamperedResponseIsRejectedEvenWhenTheTokenLooksFine()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.TamperResponse = encoded =>
        {
            byte[] wire = Convert.FromBase64String(encoded);
            wire[14] ^= 0x01;
            return Convert.ToBase64String(wire);
        };
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
        Assert.Null(r.Token);
    }

    [Fact]
    public async Task ResponseFormattingNoiseIsTolerated()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.TamperResponse = encoded => "\r\n" + encoded.TrimEnd('=') + "\n";
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal(rig.Gateway.LastToken, r.Token);
    }

    [Fact]
    public async Task UnknownServerErrorKeysAreKeptAsText()
    {
        Rig rig = new();
        rig.Gateway.ForceReplyPlaintext = "brand_new_error";
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.ServerError, r.Outcome);
        Assert.Equal(GatewayErrorCode.Unknown, r.ErrorCode);
        Assert.Equal("brand_new_error", r.ErrorKey);
    }

    // ---------- 传输失败、超时、取消 ----------

    [Fact]
    public async Task TransportExceptionsBecomeResultsThatNameOnlyTheExceptionType()
    {
        Rig rig = new();
        rig.Gateway.ThrowOnRequest = new HttpRequestException("MARKER-HOST-DETAIL gate.example:8080");
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.TransportError, r.Outcome);
        Assert.Equal(0, r.HttpStatus);
        Assert.IsType<HttpRequestException>(r.TransportException);
        Assert.Contains("HttpRequestException", r.Message);
        Assert.DoesNotContain("MARKER-HOST-DETAIL", r.Message);
        Assert.DoesNotContain("MARKER-HOST-DETAIL", r.ToString());
        Assert.DoesNotContain("gate.example", r.ToString());
        Assert.DoesNotContain("127.0.0.1", r.ToString());
    }

    [Fact]
    public async Task TimeoutsAreNamedAsTimeouts()
    {
        Rig rig = new();
        rig.Gateway.ThrowOnRequest = new TimeoutException("slow");
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.TransportError, r.Outcome);
        Assert.Contains("timed out", r.Message);
        Assert.IsType<TimeoutException>(r.TransportException);
    }

    [Fact]
    public async Task ACancellationNotCausedByTheCallerIsATransportError()
    {
        Rig rig = new();
        rig.Gateway.ThrowOnRequest = new TaskCanceledException("handler gave up");
        GatewayResult r = await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.TransportError, r.Outcome);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAsOperationCanceled()
    {
        Rig rig = new Rig().WithAccount();
        rig.Gateway.BeforeRespond = ct => Task.Delay(Timeout.Infinite, ct);
        using CancellationTokenSource cts = new();
        Task<GatewayResult> pending = rig.Client.LoginAsync(UserName, PwOne, cts.Token);
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatewayTestSupport.Bounded(pending));
    }

    [Fact]
    public async Task AnAlreadyCancelledCallDoesNotSendOrSpendAQuota()
    {
        Rig rig = new Rig().WithAccount();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.LoginAsync(UserName, PwOne, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.RegisterAsync(UserName, PwOne, null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.ChangePasswordAsync(UserName, PwOne, PwTwo, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.ForgotPasswordAsync(UserName, "12345", PwTwo, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.HealthAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.GetGameServerIpAsync(cts.Token));
        Assert.Equal(0, rig.Gateway.PostCount);
        Assert.Equal(0, rig.Gateway.GetCount);
        Assert.Equal(0, rig.Limiter.Used(rig.Clock.Now));
    }

    // ---------- 游戏服 IP ----------

    [Fact]
    public async Task GameServerIpIsReturnedWithoutSurroundingWhitespace()
    {
        Rig rig = new();
        rig.Gateway.GameServerIp = " 127.0.0.1\r\n";
        GatewayResult r = await rig.Client.GetGameServerIpAsync();
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal("127.0.0.1", r.Text);
        Assert.Equal(200, r.HttpStatus);
        Assert.Equal(Url + "/api/v1/game-server-ip", rig.Gateway.Urls[0]);
        Assert.Equal(1, rig.Gateway.GetCount);
    }

    [Fact]
    public async Task Ipv6AddressIsAcceptedToo()
    {
        Rig rig = new();
        rig.Gateway.GameServerIp = "2001:db8::1";
        GatewayResult r = await rig.Client.GetGameServerIpAsync();
        Assert.Equal("2001:db8::1", r.Text);
    }

    [Fact]
    public async Task AMissingGameServerAddressIsNotConfiguredNotAnError()
    {
        Rig rig = new();
        rig.Gateway.GameServerIp = null;
        GatewayResult r = await rig.Client.GetGameServerIpAsync();
        Assert.Equal(GatewayOutcome.NotConfigured, r.Outcome);
        Assert.False(r.IsSuccess);
        Assert.Equal(404, r.HttpStatus);
        Assert.Null(r.Text);
    }

    [Theory]
    [InlineData("gate.example")]
    [InlineData("<html>oops</html>")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("127.0.0.1:8080")]
    public async Task ANonIpBodyForTheGameServerAddressIsAProtocolError(string body)
    {
        Rig rig = new();
        rig.Gateway.GameServerIp = body;
        GatewayResult r = await rig.Client.GetGameServerIpAsync();
        Assert.Equal(GatewayOutcome.ProtocolError, r.Outcome);
        Assert.Null(r.Text);
        Assert.Equal(200, r.HttpStatus);
    }

    [Fact]
    public async Task GameServerIpErrorStatuses()
    {
        Rig rig = new();
        rig.Gateway.ForceStatus = 500;
        GatewayResult serverError = await rig.Client.GetGameServerIpAsync();
        Assert.Equal(GatewayOutcome.TransportError, serverError.Outcome);
        Assert.Equal(500, serverError.HttpStatus);
        rig.Gateway.ForceStatus = 429;
        Assert.Equal(GatewayOutcome.RateLimited, (await rig.Client.GetGameServerIpAsync()).Outcome);
        rig.Gateway.ForceStatus = null;
        rig.Gateway.ThrowOnRequest = new HttpRequestException("down");
        Assert.Equal(GatewayOutcome.TransportError, (await rig.Client.GetGameServerIpAsync()).Outcome);
    }

    // ---------- 健康检查 ----------

    [Fact]
    public async Task HealthReturnsStatusAndTimestamp()
    {
        Rig rig = new();
        GatewayResult r = await rig.Client.HealthAsync();
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal("ok", r.Health.Status);
        Assert.Equal("1730000000", r.Health.Timestamp);
        Assert.Equal(200, r.HttpStatus);
        Assert.Equal(Url + "/health", rig.Gateway.Urls[0]);
    }

    [Fact]
    public async Task HealthProblems()
    {
        Rig rig = new();
        rig.Gateway.HealthJson = "<html>not json</html>";
        GatewayResult bad = await rig.Client.HealthAsync();
        Assert.Equal(GatewayOutcome.ProtocolError, bad.Outcome);
        Assert.Null(bad.Health);
        Assert.Equal(200, bad.HttpStatus);

        rig.Gateway.HealthJson = "{\"status\":\"ok\"}";
        rig.Gateway.ForceStatus = 503;
        GatewayResult down = await rig.Client.HealthAsync();
        Assert.Equal(GatewayOutcome.TransportError, down.Outcome);
        Assert.Equal(503, down.HttpStatus);
        Assert.Null(down.Health);
    }

    // ---------- 构造与释放 ----------

    private sealed class SpyTransport : IGatewayTransport, IDisposable
    {
        public bool Disposed { get; private set; }

        public Task<GatewayHttpResponse> GetTextAsync(string url, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GatewayHttpResponse(200, "{\"status\":\"ok\"}"));
        }

        public Task<GatewayHttpResponse> PostTextAsync(string url, string body, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GatewayHttpResponse(200, string.Empty));
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    [Fact]
    public void ConstructorRejectsMissingArguments()
    {
        GatewayOptions options = new(Url, new string('a', 64));
        Assert.Throws<ArgumentNullException>(() => new GatewayClient(null, new SpyTransport()));
        Assert.Throws<ArgumentNullException>(() => new GatewayClient(options, null));
        Assert.Throws<ArgumentNullException>(() => GatewayClient.Create(null));
    }

    [Fact]
    public async Task ADisposedClientLeavesAnInjectedTransportAlone()
    {
        SpyTransport spy = new();
        GatewayOptions options = new(Url, new string('a', 64));
        GatewayClient client = new(options, spy);
        client.Dispose();
        Assert.False(spy.Disposed);
        GatewayResult r = await client.HealthAsync();
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
    }

    [Fact]
    public void CreateOwnsItsTransportAndDisposeIsIdempotent()
    {
        GatewayOptions options = new("http://127.0.0.1:9", new string('a', 64), TimeSpan.FromSeconds(1));
        GatewayClient client = GatewayClient.Create(options);
        Assert.NotNull(client.RateLimiter);
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void DefaultLimiterMatchesTheServerPolicy()
    {
        GatewayOptions options = new(Url, new string('a', 64));
        GatewayClient client = new(options, new SpyTransport());
        Assert.Equal(10, client.RateLimiter.MaxRequests);
        Assert.Equal(TimeSpan.FromSeconds(60), client.RateLimiter.Window);
    }

    [Fact]
    public async Task ResultTextNeverContainsTheTokenThePasswordOrItsDigest()
    {
        Rig rig = new Rig().WithAccount();
        GatewayResult ok = await rig.Client.LoginAsync(UserName, PwOne);
        GatewayResult bad = await rig.Client.LoginAsync(UserName, "wrong-pw");
        string everything = ok + "|" + ok.Message + "|" + bad + "|" + bad.Message;
        Assert.DoesNotContain(rig.Gateway.LastToken, everything);
        Assert.DoesNotContain(PwOne, everything);
        Assert.DoesNotContain(PwOneMd5, everything);
        Assert.DoesNotContain("wrong-pw", everything);
        Assert.Contains(rig.Gateway.LastToken.Substring(0, 4), ok.ToString());
        Assert.Contains("len " + rig.Gateway.LastToken.Length, ok.ToString());
    }

    [Fact]
    public async Task ARequestEncryptedWithTheWrongKeyGetsAnHttp400ThatIsReportedAsTransportError()
    {
        // 密钥配错的典型表现:服务端解不开,回 400(假网关就是这样做的)
        byte[] serverKey = GatewayTestSupport.SynthKey(40);
        byte[] clientKey = GatewayTestSupport.SynthKey(41);
        FakeGateway gateway = new(serverKey);
        GatewayClient client = new(new GatewayOptions(Url, GatewayTestSupport.HexLower(clientKey)), gateway, new GatewayRateLimiter(), new ManualClock(), new CountingNonceSource());
        GatewayResult r = await client.LoginAsync(UserName, PwOne);
        Assert.Equal(GatewayOutcome.TransportError, r.Outcome);
        Assert.Equal(400, r.HttpStatus);
        Assert.Empty(gateway.PostPlaintexts);
    }

    [Fact]
    public async Task EachSentRequestDrawsExactlyOneFreshNonceAndInvalidInputDrawsNone()
    {
        Rig rig = new Rig().WithAccount();
        await rig.Client.LoginAsync(UserName, PwOne);
        await rig.Client.LoginAsync("bad", PwOne);
        await rig.Client.LoginAsync(UserName, "wrong-pw");
        await rig.Client.RegisterAsync("also_bad name", PwOne);
        await rig.Client.LoginAsync(UserName, PwOne);
        Assert.Equal(3, rig.Nonces.Calls);
        Assert.Equal(3, rig.Gateway.PostBodies.Count);
        string[] nonces = rig.Gateway.PostBodies.Select(b => Convert.ToHexString(Convert.FromBase64String(b)[..12])).ToArray();
        Assert.Equal(3, nonces.Distinct().Count());
    }

    [Fact]
    public async Task DefaultRandomNoncesNeverRepeatAcrossRequests()
    {
        byte[] key = GatewayTestSupport.SynthKey(50);
        FakeGateway gateway = new(key) { RateLimitMax = 1000 };
        gateway.Accounts[UserName] = new FakeGateway.Account { Md5 = PwOneMd5, Qq = string.Empty };
        GatewayClient client = new(new GatewayOptions(Url, GatewayTestSupport.HexLower(key)), gateway, new GatewayRateLimiter(100, TimeSpan.FromSeconds(60)), new ManualClock());
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(GatewayOutcome.Success, (await client.LoginAsync(UserName, PwOne)).Outcome);
        }
        string[] nonces = gateway.PostBodies.Select(b => Convert.ToHexString(Convert.FromBase64String(b)[..12])).ToArray();
        Assert.Equal(40, nonces.Distinct().Count());
    }
}
