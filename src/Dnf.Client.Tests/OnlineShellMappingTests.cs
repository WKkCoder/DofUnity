using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网层结果 → 外壳提示类别的翻译表(登录失败分类表的逐行核对)。全是纯函数:不连网络、不碰引擎。
/// 每个联网层的枚举都用"遍历全部取值"的方式盖一遍:以后联网层新增了取值,这里会立刻红,提醒这张表要补一行。
/// </summary>
public sealed class OnlineShellMappingTests
{
    // ---- 网关业务错误 ----

    [Theory]
    [InlineData(GatewayErrorCode.WrongCredentials, OnlineNoticeKind.WrongCredentials)]
    [InlineData(GatewayErrorCode.AccountBanned, OnlineNoticeKind.AccountBanned)]
    [InlineData(GatewayErrorCode.RegistrationClosed, OnlineNoticeKind.RegistrationClosed)]
    [InlineData(GatewayErrorCode.UserExists, OnlineNoticeKind.UserExists)]
    [InlineData(GatewayErrorCode.InvalidUsername, OnlineNoticeKind.InvalidUsername)]
    [InlineData(GatewayErrorCode.InvalidPassword, OnlineNoticeKind.InvalidPassword)]
    [InlineData(GatewayErrorCode.InvalidQq, OnlineNoticeKind.Rejected)]
    [InlineData(GatewayErrorCode.Fail, OnlineNoticeKind.Rejected)]
    [InlineData(GatewayErrorCode.Unknown, OnlineNoticeKind.Rejected)]
    [InlineData(GatewayErrorCode.None, OnlineNoticeKind.Rejected)]
    public void Server_errors_map_to_their_own_category_and_the_rest_to_Rejected(GatewayErrorCode code, OnlineNoticeKind expected)
    {
        Assert.Equal(expected, OnlineShellMapping.ServerError(code));
    }

    [Fact]
    public void Every_server_error_code_is_covered_by_the_theory_above()
    {
        // 联网层以后新增业务错误键时,这里提醒:要么补一行类别,要么确认它就是"被服务端拒绝"
        Assert.Equal(10, Enum.GetValues<GatewayErrorCode>().Length);
    }

    // ---- 网关一次调用的结局 ----

    [Theory]
    [InlineData(GatewayOutcome.Success, OnlineNoticeKind.None)]
    [InlineData(GatewayOutcome.RateLimited, OnlineNoticeKind.RateLimited)]
    [InlineData(GatewayOutcome.TransportError, OnlineNoticeKind.NetworkError)]
    [InlineData(GatewayOutcome.ProtocolError, OnlineNoticeKind.ProtocolError)]
    [InlineData(GatewayOutcome.InvalidInput, OnlineNoticeKind.InvalidUsername)]
    [InlineData(GatewayOutcome.NotConfigured, OnlineNoticeKind.NoGameServer)]
    public void Gateway_outcomes_map_to_categories(GatewayOutcome outcome, OnlineNoticeKind expected)
    {
        Assert.Equal(expected, OnlineShellMapping.Gateway(outcome, GatewayErrorCode.None));
    }

    [Fact]
    public void A_server_error_outcome_is_decided_by_the_error_code()
    {
        Assert.Equal(OnlineNoticeKind.WrongCredentials, OnlineShellMapping.Gateway(GatewayOutcome.ServerError, GatewayErrorCode.WrongCredentials));
        Assert.Equal(OnlineNoticeKind.AccountBanned, OnlineShellMapping.Gateway(GatewayOutcome.ServerError, GatewayErrorCode.AccountBanned));
        Assert.Equal(OnlineNoticeKind.Rejected, OnlineShellMapping.Gateway(GatewayOutcome.ServerError, GatewayErrorCode.Unknown));
    }

    [Fact]
    public void The_error_code_is_ignored_unless_the_outcome_is_a_server_error()
    {
        // 业务错误码只在"服务端业务失败"时有意义:其它结局带着残留的错误码也不能串味
        foreach (GatewayOutcome outcome in Enum.GetValues<GatewayOutcome>())
        {
            if (outcome == GatewayOutcome.ServerError)
            {
                continue;
            }
            Assert.Equal(
                OnlineShellMapping.Gateway(outcome, GatewayErrorCode.None),
                OnlineShellMapping.Gateway(outcome, GatewayErrorCode.AccountBanned));
        }
    }

    [Fact]
    public void Every_gateway_outcome_has_a_defined_category()
    {
        foreach (GatewayOutcome outcome in Enum.GetValues<GatewayOutcome>())
        {
            Assert.NotEqual(OnlineNoticeKind.Unexpected, OnlineShellMapping.Gateway(outcome, GatewayErrorCode.None));
        }
        Assert.Equal(7, Enum.GetValues<GatewayOutcome>().Length);
    }

    // ---- 登录这一步 ----

    [Fact]
    public void A_login_without_failure_is_a_success()
    {
        OnlineAuthOutcome outcome = OnlineShellMapping.Login(OnlineFailure.None, GatewayOutcome.Success, GatewayErrorCode.None, 0);
        Assert.True(outcome.Success);
        Assert.Equal(OnlineNoticeKind.None, outcome.Failure);
    }

    [Theory]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.WrongCredentials, OnlineNoticeKind.WrongCredentials)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.AccountBanned, OnlineNoticeKind.AccountBanned)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.Unknown, OnlineNoticeKind.Rejected)]
    [InlineData(GatewayOutcome.TransportError, GatewayErrorCode.None, OnlineNoticeKind.NetworkError)]
    [InlineData(GatewayOutcome.ProtocolError, GatewayErrorCode.None, OnlineNoticeKind.ProtocolError)]
    [InlineData(GatewayOutcome.InvalidInput, GatewayErrorCode.None, OnlineNoticeKind.InvalidUsername)]
    // 网关登录"成功"却没有令牌:联网层把它记成 GatewayLoginFailed,这是协议异常,不是任何一种业务拒绝
    [InlineData(GatewayOutcome.Success, GatewayErrorCode.None, OnlineNoticeKind.ProtocolError)]
    public void A_failed_gateway_login_is_classified_by_the_gateway_outcome(GatewayOutcome outcome, GatewayErrorCode code, OnlineNoticeKind expected)
    {
        OnlineAuthOutcome result = OnlineShellMapping.Login(OnlineFailure.GatewayLoginFailed, outcome, code, 0);
        Assert.False(result.Success);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(0, result.RetryAfterSeconds);
    }

    [Fact]
    public void A_rate_limited_login_carries_the_server_countdown()
    {
        OnlineAuthOutcome result = OnlineShellMapping.Login(OnlineFailure.GatewayLoginFailed, GatewayOutcome.RateLimited, GatewayErrorCode.None, 12);
        Assert.Equal(OnlineNoticeKind.RateLimited, result.Failure);
        Assert.Equal(12, result.RetryAfterSeconds);
    }

    [Fact]
    public void A_leftover_countdown_on_any_other_failure_is_dropped()
    {
        // 只有被限流才有倒计时:其它失败带着残留的秒数,界面也不能倒数
        OnlineAuthOutcome network = OnlineShellMapping.Login(OnlineFailure.GatewayLoginFailed, GatewayOutcome.TransportError, GatewayErrorCode.None, 9);
        Assert.Equal(OnlineNoticeKind.NetworkError, network.Failure);
        Assert.Equal(0, network.RetryAfterSeconds);

        OnlineAuthOutcome wrong = OnlineShellMapping.Login(OnlineFailure.GatewayLoginFailed, GatewayOutcome.ServerError, GatewayErrorCode.WrongCredentials, 9);
        Assert.Equal(0, wrong.RetryAfterSeconds);
    }

    [Theory]
    // 取地址调用成功了,但地址不可用(服务端没配置 / 回的地址不是合法主机):没有游戏服地址
    [InlineData(GatewayOutcome.Success, OnlineNoticeKind.NoGameServer)]
    [InlineData(GatewayOutcome.NotConfigured, OnlineNoticeKind.NoGameServer)]
    // 取地址这一步自己的网络与协议问题,照网关结局说
    [InlineData(GatewayOutcome.TransportError, OnlineNoticeKind.NetworkError)]
    [InlineData(GatewayOutcome.ProtocolError, OnlineNoticeKind.ProtocolError)]
    public void A_failed_game_server_lookup_is_classified_by_what_went_wrong(GatewayOutcome outcome, OnlineNoticeKind expected)
    {
        OnlineAuthOutcome result = OnlineShellMapping.Login(OnlineFailure.NoGameServerAddress, outcome, GatewayErrorCode.None, 0);
        Assert.False(result.Success);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public void A_rate_limited_game_server_lookup_also_counts_down()
    {
        OnlineAuthOutcome result = OnlineShellMapping.Login(OnlineFailure.NoGameServerAddress, GatewayOutcome.RateLimited, GatewayErrorCode.None, 4);
        Assert.Equal(OnlineNoticeKind.RateLimited, result.Failure);
        Assert.Equal(4, result.RetryAfterSeconds);
    }

    [Fact]
    public void A_failure_that_the_login_step_cannot_produce_is_an_unexpected_error()
    {
        foreach (OnlineFailure failure in Enum.GetValues<OnlineFailure>())
        {
            if (failure == OnlineFailure.None || failure == OnlineFailure.GatewayLoginFailed || failure == OnlineFailure.NoGameServerAddress)
            {
                continue;
            }
            OnlineAuthOutcome result = OnlineShellMapping.Login(failure, GatewayOutcome.Success, GatewayErrorCode.None, 0);
            Assert.False(result.Success);
            Assert.Equal(OnlineNoticeKind.Unexpected, result.Failure);
        }
    }

    // ---- 注册与改密 ----

    [Fact]
    public void A_successful_register_or_password_change_is_a_success()
    {
        Assert.True(OnlineShellMapping.Auth(GatewayOutcome.Success, GatewayErrorCode.None, 0).Success);
    }

    [Theory]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.UserExists, OnlineNoticeKind.UserExists)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.RegistrationClosed, OnlineNoticeKind.RegistrationClosed)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.InvalidUsername, OnlineNoticeKind.InvalidUsername)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.InvalidPassword, OnlineNoticeKind.InvalidPassword)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.WrongCredentials, OnlineNoticeKind.WrongCredentials)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.AccountBanned, OnlineNoticeKind.AccountBanned)]
    [InlineData(GatewayOutcome.ServerError, GatewayErrorCode.InvalidQq, OnlineNoticeKind.Rejected)]
    [InlineData(GatewayOutcome.TransportError, GatewayErrorCode.None, OnlineNoticeKind.NetworkError)]
    [InlineData(GatewayOutcome.ProtocolError, GatewayErrorCode.None, OnlineNoticeKind.ProtocolError)]
    [InlineData(GatewayOutcome.InvalidInput, GatewayErrorCode.None, OnlineNoticeKind.InvalidUsername)]
    public void Register_and_password_change_failures_are_classified_like_login_failures(GatewayOutcome outcome, GatewayErrorCode code, OnlineNoticeKind expected)
    {
        OnlineAuthOutcome result = OnlineShellMapping.Auth(outcome, code, 0);
        Assert.False(result.Success);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public void A_rate_limited_register_carries_the_countdown_and_nothing_else_does()
    {
        OnlineAuthOutcome limited = OnlineShellMapping.Auth(GatewayOutcome.RateLimited, GatewayErrorCode.None, 30);
        Assert.Equal(OnlineNoticeKind.RateLimited, limited.Failure);
        Assert.Equal(30, limited.RetryAfterSeconds);

        OnlineAuthOutcome exists = OnlineShellMapping.Auth(GatewayOutcome.ServerError, GatewayErrorCode.UserExists, 30);
        Assert.Equal(0, exists.RetryAfterSeconds);
    }

    // ---- 连频道 ----

    [Theory]
    [InlineData(OnlineFailure.ConnectFailed, OnlineNoticeKind.ConnectFailed)]
    [InlineData(OnlineFailure.BootstrapFailed, OnlineNoticeKind.ConnectFailed)]
    [InlineData(OnlineFailure.Rejected, OnlineNoticeKind.Rejected)]
    [InlineData(OnlineFailure.GameLoginRejected, OnlineNoticeKind.GameLoginRejected)]
    [InlineData(OnlineFailure.GameLoginNoReply, OnlineNoticeKind.NetworkError)]
    [InlineData(OnlineFailure.GatewayLoginFailed, OnlineNoticeKind.Unexpected)]
    [InlineData(OnlineFailure.NoGameServerAddress, OnlineNoticeKind.Unexpected)]
    [InlineData(OnlineFailure.NoUsableSlot, OnlineNoticeKind.Unexpected)]
    [InlineData(OnlineFailure.BadLoginRequest, OnlineNoticeKind.Unexpected)]
    public void Connect_failures_map_to_categories(OnlineFailure failure, OnlineNoticeKind expected)
    {
        Assert.Equal(expected, OnlineShellMapping.ConnectFailure(failure));
        OnlineConnectOutcome outcome = OnlineShellMapping.Connect(failure, OnlineCharacterListStatus.Received, FakeSessionPort.FakeRoster(1));
        Assert.False(outcome.Success);
        Assert.Equal(expected, outcome.Failure);
        Assert.Null(outcome.Characters);
    }

    [Fact]
    public void Every_online_failure_is_covered_by_the_connect_table()
    {
        Assert.Equal(10, Enum.GetValues<OnlineFailure>().Length);
    }

    [Fact]
    public void A_connect_with_a_received_roster_succeeds_and_hands_over_every_entry()
    {
        UserInfoNotice notice = FakeSessionPort.FakeRoster(3);
        OnlineConnectOutcome outcome = OnlineShellMapping.Connect(OnlineFailure.None, OnlineCharacterListStatus.Received, notice);
        Assert.True(outcome.Success);
        Assert.Equal(3, outcome.Characters.Entries.Count);
    }

    [Fact]
    public void A_connect_without_a_usable_roster_is_a_failure_not_an_empty_account()
    {
        UserInfoNotice notice = FakeSessionPort.FakeRoster(2);
        foreach (OnlineCharacterListStatus status in new[] { OnlineCharacterListStatus.NotReceived, OnlineCharacterListStatus.Malformed })
        {
            OnlineConnectOutcome missing = OnlineShellMapping.Connect(OnlineFailure.None, status, notice);
            Assert.False(missing.Success);
            Assert.Equal(OnlineNoticeKind.CharacterListMissing, missing.Failure);
        }

        // 状态说收到了,列表对象却没有:同样是失败
        OnlineConnectOutcome nothing = OnlineShellMapping.Connect(OnlineFailure.None, OnlineCharacterListStatus.Received, null);
        Assert.False(nothing.Success);
        Assert.Equal(OnlineNoticeKind.CharacterListMissing, nothing.Failure);
    }

    [Fact]
    public void A_roster_the_parser_rejects_is_a_missing_roster_not_a_partial_one()
    {
        // 真实解析器遇到谎报的名字长度或装备条数会整包判为 Malformed 且不给任何条目;映射不能把它当成"账号下没有角色"
        UserInfoTestData.EntrySpec lyingName = UserInfoTestData.Standard(0);
        lyingName.NameLengthPrefix = 100000;
        UserInfoTestData.EntrySpec lyingEquips = UserInfoTestData.Standard(0);
        lyingEquips.EquipCountPrefix = 200;

        foreach (UserInfoTestData.EntrySpec bad in new[] { lyingName, lyingEquips })
        {
            byte[] payload = UserInfoTestData.Packet(UserInfoTestData.Standard(1), bad);
            Assert.False(UserInfoParser.TryParse(payload, out UserInfoNotice notice, out UserInfoParseStatus status));
            Assert.Equal(UserInfoParseStatus.Malformed, status);
            Assert.Null(notice);

            OnlineConnectOutcome outcome = OnlineShellMapping.Connect(OnlineFailure.None, OnlineCharacterListStatus.Malformed, notice);

            Assert.False(outcome.Success);
            Assert.Equal(OnlineNoticeKind.CharacterListMissing, outcome.Failure);
        }
    }

    [Fact]
    public void An_empty_roster_that_was_received_is_a_success_with_no_entries()
    {
        Assert.True(UserInfoParser.TryParse(UserInfoTestData.Packet(), out UserInfoNotice empty));
        OnlineConnectOutcome outcome = OnlineShellMapping.Connect(OnlineFailure.None, OnlineCharacterListStatus.Received, empty);
        Assert.True(outcome.Success);
        Assert.Empty(outcome.Characters.Entries);
    }

    [Fact]
    public void A_failure_beats_a_roster_that_happens_to_be_present()
    {
        OnlineConnectOutcome outcome = OnlineShellMapping.Connect(OnlineFailure.ConnectFailed, OnlineCharacterListStatus.Received, FakeSessionPort.FakeRoster(2));
        Assert.False(outcome.Success);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, outcome.Failure);
    }

    // ---- 选角 ----

    [Fact]
    public void Selecting_a_character_maps_entered_and_the_retry_hint()
    {
        Assert.True(OnlineShellMapping.Select(true, false).Entered);
        Assert.True(OnlineShellMapping.Select(true, true).Entered);

        OnlineSelectOutcome retry = OnlineShellMapping.Select(false, true);
        Assert.False(retry.Entered);
        Assert.Equal(OnlineNoticeKind.SelectFailed, retry.Failure);
        Assert.True(retry.MayTryAnotherSlot);

        OnlineSelectOutcome dead = OnlineShellMapping.Select(false, false);
        Assert.False(dead.Entered);
        Assert.False(dead.MayTryAnotherSlot);
    }

    // ---- 角色名册:无损 ----

    [Fact]
    public void The_roster_copy_keeps_every_header_field_entry_and_equip_without_loss()
    {
        var random = new UserInfoTestData.Lcg(20081418);
        var specs = new List<UserInfoTestData.EntrySpec>();
        for (int i = 0; i < 9; i++)
        {
            specs.Add(UserInfoTestData.RandomEntry(random));
        }
        byte[] payload = UserInfoTestData.WithTail(UserInfoTestData.Packet(0x21, 0x32, 0x43, 0xA1B2C3D4, specs.ToArray()), 9, 8, 7);
        Assert.True(UserInfoParser.TryParse(payload, out UserInfoNotice notice));

        OnlineCharacterList list = OnlineShellMapping.CharacterList(notice);

        Assert.Equal(0x21, list.A);
        Assert.Equal(0x32, list.B);
        Assert.Equal(0x43, list.C);
        Assert.Equal(0xA1B2C3D4u, list.D);
        Assert.Equal(3, list.TrailingByteCount);
        Assert.Equal(specs.Count, list.Entries.Count);
        for (int i = 0; i < specs.Count; i++)
        {
            AssertSame(notice.Entries[i], list.Entries[i]);
            UserInfoTestData.EntrySpec spec = specs[i];
            Assert.Equal(spec.Uid, list.Entries[i].Uid);
            Assert.Equal(spec.Name, list.Entries[i].NameBytes.ToArray());
            Assert.Equal(spec.Equips.Count, list.Entries[i].Equips.Count);
        }
    }

    [Fact]
    public void The_roster_copy_is_not_truncated_to_the_placeholder_screen_capacity()
    {
        var specs = new UserInfoTestData.EntrySpec[CharacterRoster.Capacity + 9];
        for (int i = 0; i < specs.Length; i++)
        {
            specs[i] = UserInfoTestData.Standard(i, 4, i % 4);
        }
        Assert.True(UserInfoParser.TryParse(UserInfoTestData.Packet(specs), out UserInfoNotice notice));

        OnlineCharacterList list = OnlineShellMapping.CharacterList(notice);

        Assert.Equal(CharacterRoster.Capacity + 9, list.Entries.Count);
        for (int i = 0; i < specs.Length; i++)
        {
            AssertSame(notice.Entries[i], list.Entries[i]);
        }
    }

    [Fact]
    public void An_empty_notice_gives_an_empty_roster_with_its_header()
    {
        Assert.True(UserInfoParser.TryParse(UserInfoTestData.Packet(0x01, 0x02, 0x03, 4), out UserInfoNotice notice));
        OnlineCharacterList list = OnlineShellMapping.CharacterList(notice);
        Assert.Empty(list.Entries);
        Assert.Equal(1, list.A);
        Assert.Equal(2, list.B);
        Assert.Equal(3, list.C);
        Assert.Equal(4u, list.D);
        Assert.Equal(0, list.TrailingByteCount);
    }

    private static void AssertSame(UserInfoEntry expected, OnlineCharacterEntry actual)
    {
        Assert.Equal(expected.Uid, actual.Uid);
        Assert.Equal(expected.NameBytes.ToArray(), actual.NameBytes.ToArray());
        Assert.Equal(expected.X1, actual.X1);
        Assert.Equal(expected.Job, actual.Job);
        Assert.Equal(expected.GrowTypeByte, actual.GrowTypeByte);
        Assert.Equal(expected.Level, actual.Level);
        Assert.Equal(expected.D, actual.D);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.G, actual.G);
        Assert.Equal(expected.H, actual.H);
        Assert.Equal(expected.I, actual.I);
        Assert.Equal(expected.J, actual.J);
        Assert.Equal(expected.Premium, actual.Premium);
        Assert.Equal(expected.K, actual.K);
        Assert.Equal(expected.A2, actual.A2);
        Assert.Equal(expected.B2, actual.B2);
        Assert.Equal(expected.C2, actual.C2);
        Assert.Equal(expected.D2, actual.D2);
        Assert.Equal(expected.Equips.Count, actual.Equips.Count);
        for (int i = 0; i < expected.Equips.Count; i++)
        {
            UserInfoEquip e = expected.Equips[i];
            OnlineCharacterEquip a = actual.Equips[i];
            Assert.Equal(e.Slot, a.Slot);
            Assert.Equal(e.ItemId, a.ItemId);
            Assert.Equal(e.Blob, a.Blob);
            Assert.Equal(e.Packed, a.Packed);
            Assert.Equal(e.ClearAvatar, a.ClearAvatar);
            Assert.Equal(e.Extra, a.Extra);
        }
    }

    // ---- 与联网层、Play 层规则的一致性(同一规则只有一个出处) ----

    [Fact]
    public void The_local_username_rule_matches_the_gateway_rule_for_every_character()
    {
        // 外壳的"本地输入不合规(不发请求)"必须与网关客户端的本地校验同判:否则界面放行的账号会在网关层被拒,或者反过来
        for (int c = 0; c < 0x300; c++)
        {
            string name = "ab" + (char)c + "cd";
            Assert.Equal(GatewayCommands.IsValidUsername(name), OnlineShellValidation.IsValidUsername(name));
        }
        for (int length = 0; length <= 40; length++)
        {
            string name = new string('a', length);
            Assert.Equal(GatewayCommands.IsValidUsername(name), OnlineShellValidation.IsValidUsername(name));
        }
        Assert.False(OnlineShellValidation.IsValidUsername(null));
    }

    [Fact]
    public void The_length_limits_follow_the_gateway_protocol()
    {
        Assert.Equal(GatewayProtocol.UsernameMinLength, OnlineShellValidation.UsernameMinLength);
        Assert.Equal(GatewayProtocol.UsernameMaxLength, OnlineShellValidation.UsernameMaxLength);
        Assert.Equal(GatewayProtocol.UsernameMaxLength, LoginScreen.MaxUsernameLength);
    }

    [Fact]
    public void The_default_channel_port_is_the_same_everywhere()
    {
        Assert.Equal(OnlineConnectionConfig.DefaultChannelPort, new OnlineEntrySettings().ChannelPort);
        Assert.Equal(OnlineConnectionConfig.DefaultChannelPort, new OnlineShellOptions().DefaultChannelPort);
    }
}
