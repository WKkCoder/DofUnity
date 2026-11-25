using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

public sealed class OnlineShellBackendManageTests
{
    private static async Task<BackendRig> LoggedIn(Action<FakeSessionPort> onPort = null, bool legacyOnly = false)
    {
        var rig = new BackendRig(true, onPort, legacyOnly);
        OnlineAuthOutcome login = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(login.Success);
        return rig;
    }

    // ------------------------------------------------------------------ 默认关闭安全边界（Backend / Host / TownDemo）

    [Fact]
    public async Task AllowCharacterWrite_defaults_to_false_and_never_touches_session()
    {
        var rig = new BackendRig();
        Assert.False(rig.Backend.AllowCharacterWrite);
        using var publicDefault = new OnlineShellBackend(rig.Template);
        Assert.False(publicDefault.AllowCharacterWrite);

        // 即使没有会话，默认关闭也直接返回 Disabled 而不抛异常或碰端口
        OnlineCharacterManageOutcome noSessionCreate = await GatewayTestSupport.Bounded(
            rig.Backend.CreateCharacterAsync(0, "Hero900", default));
        OnlineCharacterManageOutcome noSessionDelete = await GatewayTestSupport.Bounded(
            rig.Backend.DeleteCharacterAsync(0, "Hero900", default));
        Assert.Equal(OnlineCharacterManageStatus.Disabled, noSessionCreate.Status);
        Assert.Equal(OnlineCharacterManageStatus.Disabled, noSessionDelete.Status);

        // 登录后同样不碰 FakeSessionPort
        OnlineAuthOutcome login = await GatewayTestSupport.Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(login.Success);
        Assert.Equal(OnlineCharacterManageStatus.Disabled,
            (await GatewayTestSupport.Bounded(rig.Backend.CreateCharacterAsync(0, "Hero900", default))).Status);
        Assert.Equal(OnlineCharacterManageStatus.Disabled,
            (await GatewayTestSupport.Bounded(rig.Backend.DeleteCharacterAsync(0, "Hero900", default))).Status);
        Assert.Equal(0, rig.Port(0).CreateCharacterCalls);
        Assert.Equal(0, rig.Port(0).DeleteCharacterCalls);
    }

    [Fact]
    public void Host_defaults_CharacterManage_to_null_and_TownDemo_forces_false_in_non_dev()
    {
        var rig = new BackendRig();
        var setup = new OnlineShellSetup(rig.Template, OnlineShellConfigSource.ConfigFile, null, 31000, BackendRig.User, null);
        using OnlineShellHost defaultHost = OnlineShellHost.Create(setup, null, _ => { });
        Assert.False(defaultHost.Flow.CanManageCharacters);
        Assert.False(defaultHost.Backend.AllowCharacterWrite);

        using OnlineShellHost enabledHost = OnlineShellHost.Create(setup, null, _ => { }, allowCharacterWrite: true);
        Assert.True(enabledHost.Flow.CanManageCharacters);
        Assert.True(enabledHost.Backend.AllowCharacterWrite);

        // TownDemo 非开发构建强制 false
        Assert.False(OnlineShellSetupLoader.ResolveAllowCharWrite(devBuild: false, flagRequested: false));
        Assert.False(OnlineShellSetupLoader.ResolveAllowCharWrite(devBuild: false, flagRequested: true));
        Assert.False(OnlineShellSetupLoader.ResolveAllowCharWrite(devBuild: true, flagRequested: false));
        Assert.True(OnlineShellSetupLoader.ResolveAllowCharWrite(devBuild: true, flagRequested: true));
    }

    // ------------------------------------------------------------------ 真实后端转发与错误码映射

    [Fact]
    public async Task Enabled_backend_requires_session_and_rejects_legacy_port_safely()
    {
        var noSession = new BackendRig();
        noSession.Backend.AllowCharacterWrite = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => noSession.Backend.CreateCharacterAsync(0, "Hero900", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noSession.Backend.DeleteCharacterAsync(0, "Hero900", default));

        BackendRig legacy = await LoggedIn(legacyOnly: true);
        legacy.Backend.AllowCharacterWrite = true;
        Assert.Equal(OnlineCharacterManageStatus.Disabled,
            (await GatewayTestSupport.Bounded(legacy.Backend.CreateCharacterAsync(0, "Hero900", default))).Status);
        Assert.Equal(OnlineCharacterManageStatus.Disabled,
            (await GatewayTestSupport.Bounded(legacy.Backend.DeleteCharacterAsync(0, "Hero900", default))).Status);
    }

    [Fact]
    public async Task Enabled_backend_validates_inputs_locally_before_touching_port()
    {
        BackendRig rig = await LoggedIn();
        rig.Backend.AllowCharacterWrite = true;

        Assert.Equal(OnlineCharacterManageStatus.InvalidInput,
            (await GatewayTestSupport.Bounded(rig.Backend.CreateCharacterAsync(11, "Hero900", default))).Status);
        Assert.Equal(OnlineCharacterManageStatus.InvalidInput,
            (await GatewayTestSupport.Bounded(rig.Backend.CreateCharacterAsync(0, "abc", default))).Status);
        Assert.Equal(OnlineCharacterManageStatus.InvalidInput,
            (await GatewayTestSupport.Bounded(rig.Backend.CreateCharacterAsync(0, "ab c", default))).Status);
        Assert.Equal(OnlineCharacterManageStatus.InvalidInput,
            (await GatewayTestSupport.Bounded(rig.Backend.DeleteCharacterAsync(0, "", default))).Status);
        Assert.Equal(OnlineCharacterManageStatus.InvalidInput,
            (await GatewayTestSupport.Bounded(rig.Backend.DeleteCharacterAsync(0, new string('x', 30), default))).Status);

        Assert.Equal(0, rig.Port(0).CreateCharacterCalls);
        Assert.Equal(0, rig.Port(0).DeleteCharacterCalls);
    }

    [Fact]
    public async Task Enabled_backend_forwards_create_and_delete_and_maps_refreshed_roster()
    {
        BackendRig rig = await LoggedIn();
        rig.Backend.AllowCharacterWrite = true;

        OnlineCharacterManageOutcome created = await GatewayTestSupport.Bounded(
            rig.Backend.CreateCharacterAsync(5, "剑士甲", default));
        Assert.True(created.Success);
        Assert.Equal(3, created.Characters.Entries.Count);
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.CreateCharacterCalls);
        Assert.Equal((byte)5, port.LastCreateJob);
        Assert.Equal(Encoding.UTF8.GetBytes("剑士甲"), port.LastCreateNameUtf8);
        Assert.Equal((byte)0, port.LastCreateEventFlag);
        Assert.True(port.LastAllowWrite);

        OnlineCharacterManageOutcome deleted = await GatewayTestSupport.Bounded(
            rig.Backend.DeleteCharacterAsync(2, "剑士甲", default));
        Assert.True(deleted.Success);
        Assert.Single(deleted.Characters.Entries);
        Assert.Equal(1, port.DeleteCharacterCalls);
        Assert.Equal((byte)2, port.LastDeleteSlot);
        Assert.Equal(Encoding.UTF8.GetBytes("剑士甲"), port.LastDeleteConfirmUtf8);
        Assert.True(port.LastAllowWrite);
    }

    [Theory]
    [InlineData(CharacterCreateFailure.DatabaseOrUnknownError, OnlineCharacterManageStatus.DatabaseOrUnknownError)]
    [InlineData(CharacterCreateFailure.SlotLimitReached, OnlineCharacterManageStatus.SlotLimitReached)]
    [InlineData(CharacterCreateFailure.ClientRestricted, OnlineCharacterManageStatus.ClientRestricted)]
    [InlineData(CharacterCreateFailure.CreationRestrictedEmptyAccount, OnlineCharacterManageStatus.CreationRestrictedEmptyAccount)]
    [InlineData(CharacterCreateFailure.CreationRestrictedByEvent, OnlineCharacterManageStatus.CreationRestrictedByEvent)]
    [InlineData(CharacterCreateFailure.DuplicateActiveName, OnlineCharacterManageStatus.DuplicateActiveName)]
    [InlineData(CharacterCreateFailure.StateLocked, OnlineCharacterManageStatus.StateLocked)]
    [InlineData(CharacterCreateFailure.EventCharacterConditionFailed, OnlineCharacterManageStatus.EventCharacterConditionFailed)]
    [InlineData(CharacterCreateFailure.ClientSilentIgnored, OnlineCharacterManageStatus.ClientSilentIgnored)]
    [InlineData(CharacterCreateFailure.CreationBlocked, OnlineCharacterManageStatus.CreationBlocked)]
    [InlineData(CharacterCreateFailure.DuplicateDeletedName, OnlineCharacterManageStatus.DuplicateDeletedName)]
    [InlineData(CharacterCreateFailure.DailyCreationLimitReached, OnlineCharacterManageStatus.DailyCreationLimitReached)]
    [InlineData(CharacterCreateFailure.ChannelNotAllowed, OnlineCharacterManageStatus.ChannelNotAllowed)]
    [InlineData(CharacterCreateFailure.InvalidName, OnlineCharacterManageStatus.InvalidName)]
    [InlineData(CharacterCreateFailure.ServiceRestricted, OnlineCharacterManageStatus.ServiceRestricted)]
    [InlineData(CharacterCreateFailure.Unknown, OnlineCharacterManageStatus.UnknownError)]
    public void Mapping_covers_all_create_failure_codes(CharacterCreateFailure netFailure, OnlineCharacterManageStatus expected)
    {
        OnlineCharacterManageOutcome outcome = OnlineShellMapping.Manage(new OnlineCharacterManageReport(
            OnlineCharacterManageKind.Create,
            OnlineCharacterManageOutcomeKind.Rejected,
            netFailure,
            CharacterDeleteFailure.None,
            0x7E,
            null,
            OnlineCharacterListStatus.NotReceived));
        Assert.Equal(expected, outcome.Status);
        Assert.Equal((byte)0x7E, outcome.RawErrorCode);
    }

    [Theory]
    [InlineData(CharacterDeleteFailure.DatabaseOrUnknownError, OnlineCharacterManageStatus.DatabaseOrUnknownError)]
    [InlineData(CharacterDeleteFailure.StateLocked, OnlineCharacterManageStatus.StateLocked)]
    [InlineData(CharacterDeleteFailure.EmptySlot, OnlineCharacterManageStatus.EmptySlot)]
    [InlineData(CharacterDeleteFailure.CooldownOrActiveCharacter, OnlineCharacterManageStatus.CooldownOrActiveCharacter)]
    [InlineData(CharacterDeleteFailure.GuildMasterCannotDelete, OnlineCharacterManageStatus.GuildMasterCannotDelete)]
    [InlineData(CharacterDeleteFailure.ClientDeletionBlocked, OnlineCharacterManageStatus.ClientDeletionBlocked)]
    [InlineData(CharacterDeleteFailure.TradePunished, OnlineCharacterManageStatus.TradePunished)]
    [InlineData(CharacterDeleteFailure.RestingUserRestricted, OnlineCharacterManageStatus.RestingUserRestricted)]
    [InlineData(CharacterDeleteFailure.ServiceRestricted, OnlineCharacterManageStatus.ServiceRestricted)]
    [InlineData(CharacterDeleteFailure.ItemLockActive, OnlineCharacterManageStatus.ItemLockActive)]
    [InlineData(CharacterDeleteFailure.TradePunishRestricted, OnlineCharacterManageStatus.TradePunishRestricted)]
    [InlineData(CharacterDeleteFailure.RestorePunishRestricted, OnlineCharacterManageStatus.RestorePunishRestricted)]
    [InlineData(CharacterDeleteFailure.SecurityServiceRequired, OnlineCharacterManageStatus.SecurityServiceRequired)]
    [InlineData(CharacterDeleteFailure.SharedSecurityRestriction, OnlineCharacterManageStatus.SharedSecurityRestriction)]
    [InlineData(CharacterDeleteFailure.Unknown, OnlineCharacterManageStatus.UnknownError)]
    public void Mapping_covers_all_delete_failure_codes(CharacterDeleteFailure netFailure, OnlineCharacterManageStatus expected)
    {
        OnlineCharacterManageOutcome outcome = OnlineShellMapping.Manage(new OnlineCharacterManageReport(
            OnlineCharacterManageKind.Delete,
            OnlineCharacterManageOutcomeKind.Rejected,
            CharacterCreateFailure.None,
            netFailure,
            0x91,
            null,
            OnlineCharacterListStatus.NotReceived));
        Assert.Equal(expected, outcome.Status);
        Assert.Equal((byte)0x91, outcome.RawErrorCode);
    }

    [Theory]
    [InlineData(OnlineCharacterManageOutcomeKind.NoReply, OnlineCharacterManageStatus.NoReply)]
    [InlineData(OnlineCharacterManageOutcomeKind.RosterMissing, OnlineCharacterManageStatus.RosterMissing)]
    [InlineData(OnlineCharacterManageOutcomeKind.RosterMalformed, OnlineCharacterManageStatus.RosterMalformed)]
    [InlineData(OnlineCharacterManageOutcomeKind.Disconnected, OnlineCharacterManageStatus.Disconnected)]
    [InlineData(OnlineCharacterManageOutcomeKind.FrameError, OnlineCharacterManageStatus.InvalidInput)]
    [InlineData(OnlineCharacterManageOutcomeKind.MalformedReply, OnlineCharacterManageStatus.ProtocolError)]
    public void Mapping_covers_transport_and_protocol_outcomes(OnlineCharacterManageOutcomeKind kind, OnlineCharacterManageStatus expected)
    {
        OnlineCharacterManageOutcome outcome = OnlineShellMapping.Manage(new OnlineCharacterManageReport(
            OnlineCharacterManageKind.Create,
            kind,
            CharacterCreateFailure.None,
            CharacterDeleteFailure.None,
            null,
            null,
            OnlineCharacterListStatus.NotReceived));
        Assert.Equal(expected, outcome.Status);
        Assert.Equal(
            expected == OnlineCharacterManageStatus.RosterMissing
            || expected == OnlineCharacterManageStatus.RosterMalformed,
            outcome.CommandSucceeded);
    }

    [Fact]
    public void Report_default_event_flag_is_false_and_Host_double_gates_CharacterManage()
    {
        UserInfoNotice notice = FakeSessionPort.FakeRoster(2);
        OnlineCharacterManageOutcome defaultFlag = OnlineShellMapping.Manage(new OnlineCharacterManageReport(
            OnlineCharacterManageKind.Delete,
            OnlineCharacterManageOutcomeKind.Succeeded,
            CharacterCreateFailure.None,
            CharacterDeleteFailure.None,
            null,
            notice,
            OnlineCharacterListStatus.Received));
        Assert.False(defaultFlag.EventCharacterFlagCleared);

        var rig = new BackendRig();
        var setup = new OnlineShellSetup(rig.Template, OnlineShellConfigSource.ConfigFile, null, 31000, BackendRig.User, null);

        using var backendTrue = new OnlineShellBackend(setup.CreateSettings(), _ => { }, null, allowCharacterWrite: true);
        using var hostDefaultArg = new OnlineShellHost(setup, backendTrue, null, null, null);
        Assert.False(hostDefaultArg.Flow.CanManageCharacters);

        using var backendFalse = new OnlineShellBackend(setup.CreateSettings(), _ => { }, null, allowCharacterWrite: false);
        using var hostMismatch = new OnlineShellHost(setup, backendFalse, null, null, null, allowCharacterWrite: true);
        Assert.False(hostMismatch.Flow.CanManageCharacters);

        using var hostBothTrue = new OnlineShellHost(setup, backendTrue, null, null, null, allowCharacterWrite: true);
        Assert.True(hostBothTrue.Flow.CanManageCharacters);
    }

    [Fact]
    public void ToManageReport_maps_null_create_and_delete_subresults_without_throwing()
    {
        Assert.True(CharacterManagePayloads.TryParseCreateResult(
            new byte[] { 0, 0x12 }, out CharacterCreateResult createFail));
        Assert.True(CharacterManagePayloads.TryParseDeleteResult(
            new byte[] { 0, 0x24 }, out CharacterDeleteResult deleteFail));

        var createOnly = (OnlineCharacterManageResult)Activator.CreateInstance(
            typeof(OnlineCharacterManageResult),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            new object[]
            {
                OnlineCharacterManageKind.Create,
                OnlineCharacterManageOutcomeKind.Rejected,
                createFail,
                null,
                null,
                OnlineCharacterListStatus.NotReceived,
            },
            null);
        OnlineCharacterManageReport createReport = RealOnlineSessionPort.ToManageReport(createOnly);
        Assert.Equal(CharacterCreateFailure.DuplicateActiveName, createReport.CreateFailure);
        Assert.Equal(CharacterDeleteFailure.None, createReport.DeleteFailure);

        var deleteOnly = (OnlineCharacterManageResult)Activator.CreateInstance(
            typeof(OnlineCharacterManageResult),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            new object[]
            {
                OnlineCharacterManageKind.Delete,
                OnlineCharacterManageOutcomeKind.Rejected,
                null,
                deleteFail,
                null,
                OnlineCharacterListStatus.NotReceived,
            },
            null);
        OnlineCharacterManageReport deleteReport = RealOnlineSessionPort.ToManageReport(deleteOnly);
        Assert.Equal(CharacterCreateFailure.None, deleteReport.CreateFailure);
        Assert.Equal(CharacterDeleteFailure.GuildMasterCannotDelete, deleteReport.DeleteFailure);
        Assert.False(deleteReport.EventCharacterFlagCleared);
    }

    [Fact]
    public async Task RealOnlineSessionPort_defaults_to_blocking_writes_without_mutating_session_flag()
    {
        var rig = new BackendRig();
        using var port = new RealOnlineSessionPort(rig.Template);
        Assert.False(port.Real.AllowCharacterManageWrites);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.CreateCharacterAsync(0, Encoding.UTF8.GetBytes("Hero900"), 0, allowWrite: false, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.DeleteCharacterAsync(0, Encoding.UTF8.GetBytes("Hero900"), allowWrite: false, default));
        Assert.False(port.Real.AllowCharacterManageWrites);

        // allowWrite = true 时才会打开底层会话写开关（在 Created 态仍受状态机拦截）
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.CreateCharacterAsync(0, Encoding.UTF8.GetBytes("Hero900"), 0, allowWrite: true, default));
        Assert.True(port.Real.AllowCharacterManageWrites);
    }
}
