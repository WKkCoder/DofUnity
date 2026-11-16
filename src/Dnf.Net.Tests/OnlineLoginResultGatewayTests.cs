using System.Reflection;
using Dnf.Net.Gateway;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// <see cref="OnlineLoginResult"/> 的三个只读属性(<c>GatewayOutcome</c>、<c>GatewayErrorCode</c>、<c>RetryAfterSeconds</c>)与网关结局一一对应:
/// 经 <see cref="OnlineClientSessionHooks.GatewayFactory"/> 把内存里的假网关接到会话上,逐类走过成功、口令错、封禁、限流(服务端 429)、传输异常、协议异常、
/// 本地输入不合规、游戏服地址缺失;再核对"属性里只有枚举与秒数,不含令牌与地址文本"。
/// </summary>
public sealed class OnlineLoginResultGatewayTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static async Task<OnlineLoginResult> Login(OnlineFlowRig rig, string account = OnlineFlowRig.Account,
        string password = OnlineFlowRig.Password)
    {
        OnlineClientSession client = rig.CreateSession();
        return await Bounded(client.LoginAsync(account, password));
    }

    [Fact]
    public async Task Success_reports_success_outcome_without_error_or_retry()
    {
        OnlineLoginResult result = await Login(new OnlineFlowRig());

        Assert.True(result.IsSuccess);
        Assert.Equal(GatewayOutcome.Success, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.None, result.GatewayErrorCode);
        Assert.Equal(0, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Wrong_password_is_a_server_error_wrong_credentials()
    {
        OnlineLoginResult result = await Login(new OnlineFlowRig(), password: "wrong900");

        Assert.Equal(OnlineFailure.GatewayLoginFailed, result.Failure);
        Assert.Equal(OnlineStage.GatewayLogin, result.Stage);
        Assert.Equal(GatewayOutcome.ServerError, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.WrongCredentials, result.GatewayErrorCode);
        Assert.Equal(0, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Unknown_account_is_also_wrong_credentials()
    {
        OnlineLoginResult result = await Login(new OnlineFlowRig(), account: "nobody900");

        Assert.Equal(GatewayOutcome.ServerError, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.WrongCredentials, result.GatewayErrorCode);
    }

    [Fact]
    public async Task Banned_account_is_a_server_error_account_banned()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.Banned.Add(OnlineFlowRig.Account);

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.GatewayLoginFailed, result.Failure);
        Assert.Equal(GatewayOutcome.ServerError, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.AccountBanned, result.GatewayErrorCode);
    }

    [Fact]
    public async Task Server_rate_limit_carries_the_retry_seconds_from_the_header()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.RateLimitMax = 0;
        rig.FakeGateway.RetryAfterHeader = TimeSpan.FromSeconds(7);

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.GatewayLoginFailed, result.Failure);
        Assert.Equal(GatewayOutcome.RateLimited, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.None, result.GatewayErrorCode);
        Assert.Equal(7, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Server_rate_limit_without_a_header_still_gives_a_positive_wait()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.RateLimitMax = 0;

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(GatewayOutcome.RateLimited, result.GatewayOutcome);
        Assert.True(result.RetryAfterSeconds >= 1);
    }

    [Fact]
    public async Task Transport_exception_is_a_transport_error()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ThrowOnRequest = new HttpRequestException("synthetic transport failure");

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.GatewayLoginFailed, result.Failure);
        Assert.Equal(GatewayOutcome.TransportError, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.None, result.GatewayErrorCode);
        Assert.Equal(0, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Non_success_http_status_is_a_transport_error()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ForceStatus = 500;

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(GatewayOutcome.TransportError, result.GatewayOutcome);
    }

    [Fact]
    public async Task Tampered_response_is_a_protocol_error()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.TamperResponse = text => text.Substring(0, text.Length - 8);

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.GatewayLoginFailed, result.Failure);
        Assert.Equal(GatewayOutcome.ProtocolError, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.None, result.GatewayErrorCode);
    }

    [Fact]
    public async Task Local_input_that_breaks_the_field_rules_is_invalid_input_and_sends_nothing()
    {
        var rig = new OnlineFlowRig();

        OnlineLoginResult result = await Login(rig, account: "ab");

        Assert.Equal(OnlineFailure.GatewayLoginFailed, result.Failure);
        Assert.Equal(GatewayOutcome.InvalidInput, result.GatewayOutcome);
        Assert.Equal(0, rig.FakeGateway.PostCount);
        Assert.Equal(0, rig.FakeGateway.GetCount);
    }

    [Fact]
    public async Task Missing_game_server_address_is_not_configured_at_the_address_step()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.GameServerIp = null;

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.NoGameServerAddress, result.Failure);
        Assert.Equal(OnlineStage.GameServerAddress, result.Stage);
        Assert.Equal(GatewayOutcome.NotConfigured, result.GatewayOutcome);
        Assert.Equal(GatewayErrorCode.None, result.GatewayErrorCode);
    }

    [Fact]
    public async Task Garbage_game_server_address_is_a_protocol_error_at_the_address_step()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.GameServerIp = "not-an-address";

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.NoGameServerAddress, result.Failure);
        Assert.Equal(GatewayOutcome.ProtocolError, result.GatewayOutcome);
    }

    [Fact]
    public async Task Transport_failure_at_the_address_step_keeps_the_address_stage_and_the_transport_outcome()
    {
        var rig = new OnlineFlowRig();
        int calls = 0;
        rig.FakeGateway.BeforeRespond = _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                throw new HttpRequestException("synthetic address failure");
            }
            return Task.CompletedTask;
        };

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.NoGameServerAddress, result.Failure);
        Assert.Equal(OnlineStage.GameServerAddress, result.Stage);
        Assert.Equal(GatewayOutcome.TransportError, result.GatewayOutcome);
    }

    [Fact]
    public async Task Rate_limit_at_the_address_step_carries_its_retry_seconds()
    {
        var rig = new OnlineFlowRig();
        int calls = 0;
        rig.FakeGateway.BeforeRespond = _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                rig.FakeGateway.ForceStatus = 429;
                rig.FakeGateway.RetryAfterHeader = TimeSpan.FromSeconds(11);
            }
            return Task.CompletedTask;
        };

        OnlineLoginResult result = await Login(rig);

        Assert.Equal(OnlineFailure.NoGameServerAddress, result.Failure);
        Assert.Equal(GatewayOutcome.RateLimited, result.GatewayOutcome);
        Assert.Equal(11, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Every_gateway_outcome_is_reachable_through_the_session_and_maps_one_to_one()
    {
        var seen = new HashSet<GatewayOutcome>();
        foreach (Action<OnlineFlowRig> arrange in new Action<OnlineFlowRig>[]
        {
            _ => { },
            r => r.FakeGateway.Banned.Add(OnlineFlowRig.Account),
            r => r.FakeGateway.RateLimitMax = 0,
            r => r.FakeGateway.ThrowOnRequest = new HttpRequestException("synthetic"),
            r => r.FakeGateway.TamperResponse = t => t.Substring(0, t.Length - 8),
            r => r.FakeGateway.GameServerIp = null,
        })
        {
            var rig = new OnlineFlowRig();
            arrange(rig);
            seen.Add((await Login(rig)).GatewayOutcome);
        }
        seen.Add((await Login(new OnlineFlowRig(), account: "ab")).GatewayOutcome);

        foreach (GatewayOutcome outcome in Enum.GetValues<GatewayOutcome>())
        {
            Assert.Contains(outcome, seen);
        }
    }

    [Fact]
    public void The_result_exposes_only_enums_numbers_and_flags_never_text()
    {
        // 令牌与游戏服地址是文本;结果对象的公开属性里不许有文本型(含字符串、对象)的成员
        var allowed = new HashSet<Type>
        {
            typeof(OnlineStage), typeof(OnlineFailure), typeof(bool),
            typeof(GatewayOutcome), typeof(GatewayErrorCode), typeof(int),
        };
        foreach (PropertyInfo property in typeof(OnlineLoginResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(allowed.Contains(property.PropertyType), property.Name + " has a type that could carry text");
            Assert.Null(property.GetSetMethod(nonPublic: false));
        }
        Assert.Equal(6, typeof(OnlineLoginResult).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length);
    }

    [Fact]
    public async Task Failure_results_never_contain_the_token_or_the_address_in_their_text_form()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.GameServerIp = null;
        OnlineLoginResult failed = await Login(rig);
        OnlineLoginResult ok = await Login(new OnlineFlowRig());

        var good = new OnlineFlowRig();
        OnlineClientSession session = good.CreateSession();
        await Bounded(session.LoginAsync(OnlineFlowRig.Account, OnlineFlowRig.Password));
        string token = good.FakeGateway.LastToken;

        foreach (OnlineLoginResult result in new[] { failed, ok })
        {
            string text = result.ToString() + result.Stage + result.Failure + result.GatewayOutcome + result.GatewayErrorCode + result.RetryAfterSeconds;
            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain(GatewayTestSupport.Dots(127, 0, 0, 1), text);
        }
    }
}
