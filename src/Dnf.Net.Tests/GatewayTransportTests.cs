using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>
/// <see cref="HttpGatewayTransport"/>:一部分用注入的 HttpMessageHandler(不碰网络),一部分用进程内只监听回环地址的假服务器,
/// 验证真实套接字上的请求头、不跟随重定向、超时与响应体上限。不连接任何外部地址。
/// </summary>
public sealed class GatewayTransportTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private static HttpResponseMessage Ok(string text)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    }

    // ---------- 注入处理器(无网络) ----------

    [Fact]
    public async Task GetReturnsStatusAndText()
    {
        DelegateHandler handler = new((request, _) => Task.FromResult(Ok("hello")));
        using HttpGatewayTransport transport = new(handler, Generous);
        GatewayHttpResponse r = await transport.GetTextAsync("http://127.0.0.1:1/health");
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("hello", r.Text);
        Assert.Null(r.RetryAfter);
        Assert.True(r.IsSuccessStatus);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PostSendsTheBodyAsUtf8BytesWithTheExactContentType()
    {
        HttpMethod seenMethod = null;
        string seenUrl = null;
        byte[] seenBody = null;
        string seenType = null;
        string seenCharset = "unset";
        long? seenLength = null;
        DelegateHandler handler = new(async (request, ct) =>
        {
            seenMethod = request.Method;
            seenUrl = request.RequestUri.ToString();
            seenBody = await request.Content.ReadAsByteArrayAsync(ct);
            seenType = request.Content.Headers.ContentType?.ToString();
            seenCharset = request.Content.Headers.ContentType?.CharSet;
            seenLength = request.Content.Headers.ContentLength;
            return Ok("done");
        });
        using HttpGatewayTransport transport = new(handler, Generous);
        GatewayHttpResponse r = await transport.PostTextAsync("http://127.0.0.1:1/api/v1/auth", "QUJDRA==");
        Assert.Equal(HttpMethod.Post, seenMethod);
        Assert.Equal("http://127.0.0.1:1/api/v1/auth", seenUrl);
        Assert.Equal(Encoding.UTF8.GetBytes("QUJDRA=="), seenBody);
        Assert.Equal("application/octet-stream", seenType);
        Assert.Null(seenCharset);
        Assert.Equal(8L, seenLength);
        Assert.Equal("done", r.Text);
    }

    [Fact]
    public async Task NonSuccessStatusesAreReturnedNotThrown()
    {
        foreach (int code in new[] { 400, 404, 413, 429, 500, 503 })
        {
            DelegateHandler handler = new((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("body " + code) }));
            using HttpGatewayTransport transport = new(handler, Generous);
            GatewayHttpResponse r = await transport.PostTextAsync("http://127.0.0.1:1/x", "AAAA");
            Assert.Equal(code, r.StatusCode);
            Assert.Equal("body " + code, r.Text);
            Assert.False(r.IsSuccessStatus);
        }
    }

    [Fact]
    public async Task RetryAfterAsSecondsIsParsed()
    {
        DelegateHandler handler = new((request, _) =>
        {
            HttpResponseMessage response = new((HttpStatusCode)429) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return Task.FromResult(response);
        });
        using HttpGatewayTransport transport = new(handler, Generous);
        GatewayHttpResponse r = await transport.PostTextAsync("http://127.0.0.1:1/x", "AAAA");
        Assert.Equal(429, r.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), r.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterAsAnHttpDateIsConvertedToAWaitAndPastDatesGiveZero()
    {
        DateTimeOffset future = DateTimeOffset.UtcNow.AddSeconds(100);
        DelegateHandler futureHandler = new((request, _) =>
        {
            HttpResponseMessage response = new((HttpStatusCode)429) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(future);
            return Task.FromResult(response);
        });
        using (HttpGatewayTransport transport = new(futureHandler, Generous))
        {
            GatewayHttpResponse r = await transport.PostTextAsync("http://127.0.0.1:1/x", "AAAA");
            Assert.NotNull(r.RetryAfter);
            Assert.InRange(r.RetryAfter.Value, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(101));
        }

        DelegateHandler pastHandler = new((request, _) =>
        {
            HttpResponseMessage response = new((HttpStatusCode)429) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-100));
            return Task.FromResult(response);
        });
        using (HttpGatewayTransport transport = new(pastHandler, Generous))
        {
            GatewayHttpResponse r = await transport.PostTextAsync("http://127.0.0.1:1/x", "AAAA");
            Assert.Equal(TimeSpan.Zero, r.RetryAfter);
        }
    }

    [Fact]
    public async Task ResponseBodyAtTheLimitIsReadAndOnePastItIsRefused()
    {
        string atLimit = new('a', GatewayProtocol.MaxResponseBytes);
        using (HttpGatewayTransport transport = new(new DelegateHandler((r, _) => Task.FromResult(Ok(atLimit))), Generous))
        {
            GatewayHttpResponse ok = await transport.GetTextAsync("http://127.0.0.1:1/x");
            Assert.Equal(GatewayProtocol.MaxResponseBytes, ok.Text.Length);
        }

        string tooBig = new('a', GatewayProtocol.MaxResponseBytes + 1);
        using (HttpGatewayTransport transport = new(new DelegateHandler((r, _) => Task.FromResult(Ok(tooBig))), Generous))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => transport.GetTextAsync("http://127.0.0.1:1/x"));
        }
    }

    [Fact]
    public async Task ATimeoutBeforeTheHeadersIsATimeoutException()
    {
        DelegateHandler handler = new(async (request, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("never");
        });
        using HttpGatewayTransport transport = new(handler, TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<TimeoutException>(() => GatewayTestSupport.Bounded(transport.GetTextAsync("http://127.0.0.1:1/x")));
        await Assert.ThrowsAsync<TimeoutException>(() => GatewayTestSupport.Bounded(transport.PostTextAsync("http://127.0.0.1:1/x", "AAAA")));
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsATimeout()
    {
        DelegateHandler handler = new(async (request, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("never");
        });
        using HttpGatewayTransport transport = new(handler, Generous);
        using CancellationTokenSource cts = new();
        Task<GatewayHttpResponse> pending = transport.GetTextAsync("http://127.0.0.1:1/x", cts.Token);
        cts.CancelAfter(50);
        Exception e = await Record.ExceptionAsync(() => GatewayTestSupport.Bounded(pending));
        Assert.IsAssignableFrom<OperationCanceledException>(e);
        Assert.IsNotType<TimeoutException>(e);
    }

    [Fact]
    public async Task HandlerExceptionsPropagate()
    {
        DelegateHandler handler = new((request, _) => throw new HttpRequestException("connection refused"));
        using HttpGatewayTransport transport = new(handler, Generous);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetTextAsync("http://127.0.0.1:1/x"));
    }

    [Fact]
    public void ConstructorGuards()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpGatewayTransport(null, Generous));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpGatewayTransport(new DelegateHandler((r, _) => Task.FromResult(Ok("x"))), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpGatewayTransport(new DelegateHandler((r, _) => Task.FromResult(Ok("x"))), TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpGatewayTransport(TimeSpan.Zero));
    }

    [Fact]
    public async Task DisposedTransportRefusesFurtherRequests()
    {
        HttpGatewayTransport transport = new(new DelegateHandler((r, _) => Task.FromResult(Ok("x"))), Generous);
        transport.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.GetTextAsync("http://127.0.0.1:1/x"));
    }

    [Fact]
    public async Task UnicodeAndEmptyBodiesDecodeAsUtf8()
    {
        string text = "中文 ok";
        using (HttpGatewayTransport transport = new(new DelegateHandler((r, _) => Task.FromResult(Ok(text))), Generous))
        {
            Assert.Equal(text, (await transport.GetTextAsync("http://127.0.0.1:1/x")).Text);
        }
        using (HttpGatewayTransport transport = new(new DelegateHandler((r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))), Generous))
        {
            GatewayHttpResponse r = await transport.GetTextAsync("http://127.0.0.1:1/x");
            Assert.Equal(204, r.StatusCode);
            Assert.True(string.IsNullOrEmpty(r.Text));
        }
    }

    // ---------- 客户端 + 传输 + 假网关(处理器适配,无网络) ----------

    [Fact]
    public async Task ClientOverTheHttpTransportCompletesALoginAgainstTheFakeGateway()
    {
        byte[] key = GatewayTestSupport.SynthKey(60);
        FakeGateway gateway = new(key);
        gateway.Accounts["tester01"] = new FakeGateway.Account { Md5 = "47b7bfb65fa83ac9a71dcb0f6296bb6e", Qq = string.Empty };
        FakeGatewayHandler handler = new(gateway);
        using HttpGatewayTransport transport = new(handler, Generous);
        GatewayOptions options = new("http://127.0.0.1:8080", GatewayTestSupport.HexLower(key));
        using GatewayClient client = new(options, transport);

        GatewayResult r = await client.LoginAsync("tester01", "Passw0rd!");
        Assert.Equal(GatewayOutcome.Success, r.Outcome);
        Assert.Equal(gateway.LastToken, r.Token);
        Assert.Equal("login|tester01|47b7bfb65fa83ac9a71dcb0f6296bb6e|00:00:00:00:00:00", gateway.PostPlaintexts[0]);
        Assert.Equal("http://127.0.0.1:8080/api/v1/auth", handler.Requests[0].RequestUri.ToString());
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.DoesNotContain(gateway.PostPlaintexts, p => p.Contains("Passw0rd!"));

        GatewayResult ip = await client.GetGameServerIpAsync();
        Assert.Equal("127.0.0.1", ip.Text);
        GatewayResult health = await client.HealthAsync();
        Assert.Equal("ok", health.Health.Status);
    }

    // ---------- 真实套接字(只连回环上的假服务器) ----------

    [Fact]
    public async Task ARealRequestCarriesTheExactHeadersAndBody()
    {
        using LoopbackServer server = new((request, stream, ct) => LoopbackServer.WriteResponseAsync(stream, 200, Encoding.ASCII.GetBytes("pong"), null, ct));
        using HttpGatewayTransport transport = new(Generous);
        GatewayHttpResponse r = await transport.PostTextAsync(server.BaseUrl + "/api/v1/auth", "QUJDRA==");
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("pong", r.Text);

        Assert.True(server.Requests.TryPeek(out LoopbackRequest seen));
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/api/v1/auth", seen.Target);
        Assert.Equal("application/octet-stream", seen.Headers["Content-Type"]);
        Assert.Equal("8", seen.Headers["Content-Length"]);
        Assert.Equal("QUJDRA==", Encoding.ASCII.GetString(seen.Body));
    }

    [Fact]
    public async Task ARealGetUsesGetAndNoBody()
    {
        using LoopbackServer server = new((request, stream, ct) => LoopbackServer.WriteResponseAsync(stream, 200, Encoding.ASCII.GetBytes("{}"), null, ct));
        using HttpGatewayTransport transport = new(Generous);
        GatewayHttpResponse r = await transport.GetTextAsync(server.BaseUrl + "/health");
        Assert.Equal(200, r.StatusCode);
        Assert.True(server.Requests.TryPeek(out LoopbackRequest seen));
        Assert.Equal("GET", seen.Method);
        Assert.Equal("/health", seen.Target);
        Assert.Empty(seen.Body);
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        // 重定向指向同一个服务器的另一个路径;如果被跟随,服务器会收到更多请求
        using LoopbackServer redirecting = new((request, stream, ct) =>
            LoopbackServer.WriteResponseAsync(stream, 302, Array.Empty<byte>(), new[] { "Location: /elsewhere" }, ct));
        using HttpGatewayTransport transport = new(Generous);
        GatewayHttpResponse post = await transport.PostTextAsync(redirecting.BaseUrl + "/api/v1/auth", "AAAA");
        Assert.Equal(302, post.StatusCode);
        GatewayHttpResponse get = await transport.GetTextAsync(redirecting.BaseUrl + "/health");
        Assert.Equal(302, get.StatusCode);
        Assert.Equal(2, redirecting.Requests.Count);
        Assert.Equal(2, redirecting.Connections);
    }

    [Fact]
    public async Task ARealEndlessBodyWithoutALengthIsCutOffAtTheLimit()
    {
        using LoopbackServer server = new(async (request, stream, ct) =>
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 Test\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, ct);
            byte[] chunk = new byte[8192];
            Array.Fill(chunk, (byte)'z');
            // 没有 Content-Length、写到连接被关为止,但最多写约 5 MiB:被测代码的上限失效时测试只会失败,不会耗尽内存
            for (int i = 0; i < 640 && !ct.IsCancellationRequested; i++)
            {
                await stream.WriteAsync(chunk, ct);
            }
        });
        using HttpGatewayTransport transport = new(Generous);
        await Assert.ThrowsAsync<InvalidDataException>(() => GatewayTestSupport.Bounded(transport.GetTextAsync(server.BaseUrl + "/health")));
    }

    [Fact]
    public async Task ARealServerThatNeverAnswersTimesOut()
    {
        using LoopbackServer server = new((request, stream, ct) => Task.Delay(Timeout.Infinite, ct));
        using HttpGatewayTransport transport = new(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<TimeoutException>(() => GatewayTestSupport.Bounded(transport.PostTextAsync(server.BaseUrl + "/api/v1/auth", "AAAA")));
    }

    [Fact]
    public async Task ARealServerThatStallsMidBodyTimesOut()
    {
        using LoopbackServer server = new(async (request, stream, ct) =>
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 Test\r\nContent-Length: 100\r\nConnection: close\r\n\r\nonly ten b");
            await stream.WriteAsync(head, ct);
            await stream.FlushAsync(ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        using HttpGatewayTransport transport = new(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAsync<TimeoutException>(() => GatewayTestSupport.Bounded(transport.GetTextAsync(server.BaseUrl + "/health")));
    }

    [Fact]
    public async Task ARealOversizedBodyIsRefused()
    {
        byte[] big = new byte[GatewayProtocol.MaxResponseBytes + 4096];
        Array.Fill(big, (byte)'x');
        using LoopbackServer server = new((request, stream, ct) => LoopbackServer.WriteResponseAsync(stream, 200, big, null, ct));
        using HttpGatewayTransport transport = new(Generous);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.GetTextAsync(server.BaseUrl + "/health"));
    }

    [Fact]
    public async Task AClosedPortIsAConnectionFailureNotAHang()
    {
        int port;
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using HttpGatewayTransport transport = new(Generous);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetTextAsync("http://127.0.0.1:" + port + "/health"));
    }

    [Fact]
    public async Task ClientOverARealSocketRunsLoginHealthAndIpEndToEnd()
    {
        byte[] key = GatewayTestSupport.SynthKey(61);
        FakeGateway gateway = new(key);
        gateway.Accounts["tester01"] = new FakeGateway.Account { Md5 = "47b7bfb65fa83ac9a71dcb0f6296bb6e", Qq = string.Empty };
        using LoopbackServer server = new(async (request, stream, ct) =>
        {
            string contentType = request.Headers.TryGetValue("Content-Type", out string ctype) ? ctype : null;
            string body = request.Body.Length == 0 ? null : Encoding.UTF8.GetString(request.Body);
            GatewayHttpResponse r = gateway.Handle(request.Method, "http://127.0.0.1" + request.Target, contentType, body);
            await LoopbackServer.WriteResponseAsync(stream, r.StatusCode, Encoding.UTF8.GetBytes(r.Text ?? string.Empty), null, ct);
        });
        using GatewayClient client = GatewayClient.Create(new GatewayOptions(server.BaseUrl, GatewayTestSupport.HexLower(key), TimeSpan.FromSeconds(15)));

        GatewayResult login = await client.LoginAsync("tester01", "Passw0rd!");
        Assert.Equal(GatewayOutcome.Success, login.Outcome);
        Assert.Equal(gateway.LastToken, login.Token);

        GatewayResult wrong = await client.LoginAsync("tester01", "not-it");
        Assert.Equal(GatewayErrorCode.WrongCredentials, wrong.ErrorCode);

        Assert.Equal("ok", (await client.HealthAsync()).Health.Status);
        Assert.Equal("127.0.0.1", (await client.GetGameServerIpAsync()).Text);
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task ClientOverARealSocketReportsAnUnreachableGatewayWithoutLeakingTheAddress()
    {
        int port;
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using GatewayClient client = GatewayClient.Create(new GatewayOptions("http://127.0.0.1:" + port, new string('a', 64), TimeSpan.FromSeconds(10)));
        GatewayResult r = await client.LoginAsync("tester01", "Passw0rd!");
        Assert.Equal(GatewayOutcome.TransportError, r.Outcome);
        Assert.DoesNotContain("127.0.0.1", r.ToString());
        Assert.DoesNotContain(port.ToString(), r.Message);
    }
}
