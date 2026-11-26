using System.Reflection;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 后端的创建/删除角色在边界上的补充测试(r20fix-createcharui,簇 2):职业上界、名字边界、取消令牌转发、
/// Net 报告到外壳结局的映射(名册缺失时"命令其实成功了")、会话薄皮每次调用自己写写开关。
/// 全部走内存里的假会话与合成数据,不连任何真实服务,也不触发任何真实创建或删除。
/// </summary>
public sealed class OnlineShellBackendManageEdgeTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static async Task<BackendRig> LoggedIn()
    {
        var rig = new BackendRig(true, null, false);
        OnlineAuthOutcome login = await Bounded(rig.Backend.LoginAsync(BackendRig.User, BackendRig.Password, default));
        Assert.True(login.Success);
        rig.Backend.AllowCharacterWrite = true;
        return rig;
    }

    // ------------------------------------------------------------------ 创建职业的上界

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)9)]
    [InlineData((byte)10)]
    public async Task Create_forwards_every_job_up_to_the_highest_creatable_one(byte job)
    {
        BackendRig rig = await LoggedIn();

        OnlineCharacterManageOutcome outcome = await Bounded(rig.Backend.CreateCharacterAsync(job, "Hero905", default));

        Assert.True(outcome.Success);
        Assert.Equal(3, outcome.Characters.Entries.Count);
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.CreateCharacterCalls);
        Assert.Equal(job, port.LastCreateJob);
        Assert.Equal(Encoding.UTF8.GetBytes("Hero905"), port.LastCreateNameUtf8);
        Assert.Equal((byte)0, port.LastCreateEventFlag);
        Assert.True(port.LastAllowWrite);
    }

    [Theory]
    [InlineData((byte)11)]
    [InlineData((byte)12)]
    [InlineData((byte)100)]
    [InlineData((byte)255)]
    public async Task Create_rejects_jobs_above_the_highest_creatable_one_before_touching_the_port(byte job)
    {
        BackendRig rig = await LoggedIn();

        OnlineCharacterManageOutcome outcome = await Bounded(rig.Backend.CreateCharacterAsync(job, "Hero905", default));

        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, outcome.Status);
        Assert.False(outcome.Success);
        Assert.False(outcome.CommandSucceeded);
        Assert.Null(outcome.Characters);
        Assert.Equal(0, rig.Port(0).CreateCharacterCalls);
    }

    // ------------------------------------------------------------------ 名字的边界(后端本地先验一遍,不合规的不碰端口)

    [Theory]
    [InlineData("Hero")]                                        // 4 字节:下限
    [InlineData("Hero900Hero900Hero")]                          // 18 字节:上限
    [InlineData("一二三四五六")]                                 // 6 个汉字 = 18 字节
    [InlineData("一a")]                                         // 3 + 1 = 4 字节:混合内容的下限
    public async Task Create_forwards_names_inside_the_byte_range(string name)
    {
        BackendRig rig = await LoggedIn();

        OnlineCharacterManageOutcome outcome = await Bounded(rig.Backend.CreateCharacterAsync(4, name, default));

        Assert.True(outcome.Success);
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.CreateCharacterCalls);
        Assert.Equal(Encoding.UTF8.GetBytes(name), port.LastCreateNameUtf8);
    }

    [Theory]
    [InlineData("Her")]                                         // 3 字节:下限少一
    [InlineData("Hero900Hero900Hero9")]                         // 19 字节:上限多一
    [InlineData("一二三四五六七")]                               // 7 个汉字 = 21 字节
    [InlineData("Her'o")]                                       // 单引号:禁用字符
    [InlineData("He ro")]                                       // 空格:禁用字符
    [InlineData("\U00020000\U00020001")]                        // 4 字节一个的非基本平面字符:创建名不收
    [InlineData("")]
    [InlineData(null)]
    public async Task Create_rejects_names_outside_the_rules_before_touching_the_port(string name)
    {
        BackendRig rig = await LoggedIn();

        OnlineCharacterManageOutcome outcome = await Bounded(rig.Backend.CreateCharacterAsync(4, name, default));

        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, outcome.Status);
        Assert.Equal(0, rig.Port(0).CreateCharacterCalls);
    }

    [Fact]
    public async Task Create_and_delete_reject_a_name_with_a_lone_surrogate_before_touching_the_port()
    {
        BackendRig rig = await LoggedIn();
        string broken = "Hero" + (char)0xD800;

        OnlineCharacterManageOutcome created = await Bounded(rig.Backend.CreateCharacterAsync(4, broken, default));
        OnlineCharacterManageOutcome deleted = await Bounded(rig.Backend.DeleteCharacterAsync(1, broken, default));

        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, created.Status);
        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, deleted.Status);
        Assert.Equal(0, rig.Port(0).CreateCharacterCalls);
        Assert.Equal(0, rig.Port(0).DeleteCharacterCalls);
    }

    [Theory]
    [InlineData("h")]                                           // 1 字节:下限
    [InlineData("a b")]                                         // 删除确认名允许空格(创建名不允许)
    [InlineData("Hero900Hero900Hero900Hero900H")]               // 29 字节:上限
    [InlineData("一二三四五六七八九")]                           // 9 个汉字 = 27 字节
    public async Task Delete_forwards_confirmation_names_inside_the_byte_range(string name)
    {
        BackendRig rig = await LoggedIn();

        OnlineCharacterManageOutcome outcome = await Bounded(rig.Backend.DeleteCharacterAsync(2, name, default));

        Assert.True(outcome.Success);
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.DeleteCharacterCalls);
        Assert.Equal(Encoding.UTF8.GetBytes(name), port.LastDeleteConfirmUtf8);
    }

    [Theory]
    [InlineData("")]                                            // 0 字节
    [InlineData("Hero900Hero900Hero900Hero900He")]              // 30 字节:上限多一
    [InlineData("一二三四五六七八九十")]                         // 10 个汉字 = 30 字节
    [InlineData("a\0b")]                                        // 含 NUL 字节
    [InlineData(null)]
    public async Task Delete_rejects_confirmation_names_outside_the_rules_before_touching_the_port(string name)
    {
        BackendRig rig = await LoggedIn();

        OnlineCharacterManageOutcome outcome = await Bounded(rig.Backend.DeleteCharacterAsync(2, name, default));

        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, outcome.Status);
        Assert.Equal(0, rig.Port(0).DeleteCharacterCalls);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)35)]
    [InlineData((byte)255)]
    public async Task Delete_forwards_the_slot_index_unchanged(byte slot)
    {
        BackendRig rig = await LoggedIn();

        OnlineCharacterManageOutcome outcome = await Bounded(rig.Backend.DeleteCharacterAsync(slot, "Hero906", default));

        Assert.True(outcome.Success);
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(1, port.DeleteCharacterCalls);
        Assert.Equal(slot, port.LastDeleteSlot);
        Assert.Equal(Encoding.UTF8.GetBytes("Hero906"), port.LastDeleteConfirmUtf8);
    }

    // ------------------------------------------------------------------ 取消令牌:原样交给端口;已取消的不发出

    [Fact]
    public async Task The_cancellation_token_reaches_the_port_for_create_and_delete()
    {
        BackendRig rig = await LoggedIn();
        using var stop = new CancellationTokenSource();

        await Bounded(rig.Backend.CreateCharacterAsync(4, "Hero907", stop.Token));
        FakeSessionPort port = rig.Port(0);
        Assert.Equal(stop.Token, port.LastToken);

        using var other = new CancellationTokenSource();
        await Bounded(rig.Backend.DeleteCharacterAsync(1, "Hero907", other.Token));
        Assert.Equal(other.Token, port.LastToken);
        Assert.NotEqual(stop.Token, port.LastToken);
    }

    [Fact]
    public async Task A_cancelled_token_stops_create_and_delete_before_the_port_is_called()
    {
        BackendRig rig = await LoggedIn();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Backend.CreateCharacterAsync(4, "Hero907", stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Backend.DeleteCharacterAsync(1, "Hero907", stop.Token));

        Assert.Equal(0, rig.Port(0).CreateCharacterCalls);
        Assert.Equal(0, rig.Port(0).DeleteCharacterCalls);
    }

    // ------------------------------------------------------------------ Net 报告 → 外壳结局的映射

    private static OnlineCharacterManageReport Report(
        OnlineCharacterManageKind kind, OnlineCharacterManageOutcomeKind outcome, UserInfoNotice list, OnlineCharacterListStatus status,
        bool flagCleared = false)
    {
        return new OnlineCharacterManageReport(
            kind, outcome, CharacterCreateFailure.None, CharacterDeleteFailure.None, null, list, status, flagCleared);
    }

    /// <summary>
    /// 命令回复成功了,但名册没拿到(状态不是 Received,或名册是空引用):外壳要知道"命令其实成功了、只是名册缺失",
    /// 界面据此不报"创建失败/删除失败"。创建与删除各一遍,四种形状各一遍。
    /// </summary>
    [Theory]
    [InlineData(true, true, OnlineCharacterListStatus.NotReceived)]      // 有名册对象,但状态是没收到
    [InlineData(true, true, OnlineCharacterListStatus.Malformed)]        // 有名册对象,但状态是畸形
    [InlineData(true, false, OnlineCharacterListStatus.Received)]        // 状态说收到了,名册对象却是空
    [InlineData(true, false, OnlineCharacterListStatus.NotReceived)]     // 两样都缺
    [InlineData(false, true, OnlineCharacterListStatus.NotReceived)]
    [InlineData(false, true, OnlineCharacterListStatus.Malformed)]
    [InlineData(false, false, OnlineCharacterListStatus.Received)]
    [InlineData(false, false, OnlineCharacterListStatus.NotReceived)]
    public void A_successful_command_without_a_usable_roster_maps_to_roster_missing_with_the_command_succeeded(
        bool create, bool withRosterObject, OnlineCharacterListStatus listStatus)
    {
        UserInfoNotice list = withRosterObject ? FakeSessionPort.FakeRoster(2) : null;
        OnlineCharacterManageKind kind = create ? OnlineCharacterManageKind.Create : OnlineCharacterManageKind.Delete;

        OnlineCharacterManageOutcome outcome = OnlineShellMapping.Manage(
            Report(kind, OnlineCharacterManageOutcomeKind.Succeeded, list, listStatus, flagCleared: true));

        Assert.Equal(OnlineCharacterManageStatus.RosterMissing, outcome.Status);
        Assert.True(outcome.CommandSucceeded);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Characters);
        Assert.Null(outcome.RawErrorCode);
        Assert.False(outcome.EventCharacterFlagCleared);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void A_successful_command_with_a_received_roster_maps_the_roster_and_the_cleared_flag(bool create, bool flagCleared)
    {
        UserInfoNotice list = FakeSessionPort.FakeRoster(3);
        OnlineCharacterManageKind kind = create ? OnlineCharacterManageKind.Create : OnlineCharacterManageKind.Delete;

        OnlineCharacterManageOutcome outcome = OnlineShellMapping.Manage(
            Report(kind, OnlineCharacterManageOutcomeKind.Succeeded, list, OnlineCharacterListStatus.Received, flagCleared));

        Assert.Equal(OnlineCharacterManageStatus.Succeeded, outcome.Status);
        Assert.True(outcome.Success);
        Assert.True(outcome.CommandSucceeded);
        Assert.Null(outcome.RawErrorCode);
        Assert.Equal(3, outcome.Characters.Entries.Count);
        Assert.Equal(flagCleared, outcome.EventCharacterFlagCleared);
    }

    [Theory]
    [InlineData(OnlineCharacterManageOutcomeKind.Rejected, OnlineCharacterManageStatus.DuplicateActiveName, false)]
    [InlineData(OnlineCharacterManageOutcomeKind.NoReply, OnlineCharacterManageStatus.NoReply, false)]
    [InlineData(OnlineCharacterManageOutcomeKind.Disconnected, OnlineCharacterManageStatus.Disconnected, false)]
    [InlineData(OnlineCharacterManageOutcomeKind.FrameError, OnlineCharacterManageStatus.InvalidInput, false)]
    [InlineData(OnlineCharacterManageOutcomeKind.MalformedReply, OnlineCharacterManageStatus.ProtocolError, false)]
    [InlineData((OnlineCharacterManageOutcomeKind)99, OnlineCharacterManageStatus.ProtocolError, false)]
    [InlineData(OnlineCharacterManageOutcomeKind.RosterMissing, OnlineCharacterManageStatus.RosterMissing, true)]
    [InlineData(OnlineCharacterManageOutcomeKind.RosterMalformed, OnlineCharacterManageStatus.RosterMalformed, true)]
    public void Only_the_roster_outcomes_report_the_command_as_succeeded_and_none_carries_a_roster(
        OnlineCharacterManageOutcomeKind kind, OnlineCharacterManageStatus expectedStatus, bool expectedCommandSucceeded)
    {
        var report = new OnlineCharacterManageReport(
            OnlineCharacterManageKind.Create, kind, CharacterCreateFailure.DuplicateActiveName, CharacterDeleteFailure.None,
            0x12, FakeSessionPort.FakeRoster(2), OnlineCharacterListStatus.Received, true);

        OnlineCharacterManageOutcome outcome = OnlineShellMapping.Manage(report);

        Assert.Equal(expectedStatus, outcome.Status);
        Assert.Equal(expectedCommandSucceeded, outcome.CommandSucceeded);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Characters);
        Assert.False(outcome.EventCharacterFlagCleared);
        Assert.Equal(kind == OnlineCharacterManageOutcomeKind.Rejected ? (byte?)0x12 : null, outcome.RawErrorCode);
    }

    /// <summary>被拒的报告按它自己的操作翻译:创建与删除的失败码同时给出,取哪一个只看 Operation。</summary>
    [Theory]
    [InlineData(OnlineCharacterManageKind.Create, OnlineCharacterManageStatus.SlotLimitReached)]
    [InlineData(OnlineCharacterManageKind.Delete, OnlineCharacterManageStatus.EmptySlot)]
    public void A_rejected_report_is_translated_by_its_own_operation(
        OnlineCharacterManageKind kind, OnlineCharacterManageStatus expected)
    {
        var report = new OnlineCharacterManageReport(
            kind, OnlineCharacterManageOutcomeKind.Rejected, CharacterCreateFailure.SlotLimitReached, CharacterDeleteFailure.EmptySlot,
            0x33, null, OnlineCharacterListStatus.NotReceived);

        OnlineCharacterManageOutcome outcome = OnlineShellMapping.Manage(report);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal((byte?)0x33, outcome.RawErrorCode);
        Assert.False(outcome.CommandSucceeded);
    }

    [Fact]
    public async Task An_enabled_backend_without_a_session_still_rejects_bad_input_instead_of_throwing()
    {
        var rig = new BackendRig();
        rig.Backend.AllowCharacterWrite = true;

        OnlineCharacterManageOutcome badJob = await Bounded(rig.Backend.CreateCharacterAsync(11, "Hero905", default));
        OnlineCharacterManageOutcome badName = await Bounded(rig.Backend.CreateCharacterAsync(4, "Her", default));
        OnlineCharacterManageOutcome badConfirm = await Bounded(rig.Backend.DeleteCharacterAsync(1, "", default));

        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, badJob.Status);
        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, badName.Status);
        Assert.Equal(OnlineCharacterManageStatus.InvalidInput, badConfirm.Status);
    }

    // ------------------------------------------------------------------ 会话结果 → 报告:每个字段各取一个互不相同的值

    private static OnlineCharacterManageResult MakeResult(
        OnlineCharacterManageKind kind, OnlineCharacterManageOutcomeKind outcome, CharacterCreateResult create,
        CharacterDeleteResult delete, UserInfoNotice list, OnlineCharacterListStatus status)
    {
        return (OnlineCharacterManageResult)Activator.CreateInstance(
            typeof(OnlineCharacterManageResult),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { kind, outcome, create, delete, list, status },
            null);
    }

    [Fact]
    public void ToManageReport_carries_every_field_of_a_rejected_delete()
    {
        Assert.True(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 0, 0x24 }, out CharacterDeleteResult rejected));

        OnlineCharacterManageReport report = RealOnlineSessionPort.ToManageReport(MakeResult(
            OnlineCharacterManageKind.Delete, OnlineCharacterManageOutcomeKind.Rejected, null, rejected, null,
            OnlineCharacterListStatus.NotReceived));

        Assert.Equal(OnlineCharacterManageKind.Delete, report.Operation);
        Assert.Equal(OnlineCharacterManageOutcomeKind.Rejected, report.Outcome);
        Assert.Equal(CharacterCreateFailure.None, report.CreateFailure);
        Assert.Equal(CharacterDeleteFailure.GuildMasterCannotDelete, report.DeleteFailure);
        Assert.Equal((byte?)0x24, report.RawErrorCode);
        Assert.Null(report.CharacterList);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, report.CharacterListStatus);
        Assert.False(report.EventCharacterFlagCleared);
    }

    [Fact]
    public void ToManageReport_carries_every_field_of_a_rejected_create()
    {
        Assert.True(CharacterManagePayloads.TryParseCreateResult(new byte[] { 0, 0x12 }, out CharacterCreateResult rejected));

        OnlineCharacterManageReport report = RealOnlineSessionPort.ToManageReport(MakeResult(
            OnlineCharacterManageKind.Create, OnlineCharacterManageOutcomeKind.Rejected, rejected, null, null,
            OnlineCharacterListStatus.NotReceived));

        Assert.Equal(OnlineCharacterManageKind.Create, report.Operation);
        Assert.Equal(OnlineCharacterManageOutcomeKind.Rejected, report.Outcome);
        Assert.Equal(CharacterCreateFailure.DuplicateActiveName, report.CreateFailure);
        Assert.Equal(CharacterDeleteFailure.None, report.DeleteFailure);
        Assert.Equal((byte?)0x12, report.RawErrorCode);
        Assert.Null(report.CharacterList);
        Assert.False(report.EventCharacterFlagCleared);
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, true)]
    [InlineData((byte)2, true)]
    [InlineData((byte)255, true)]
    public void ToManageReport_carries_the_cleared_flag_and_the_roster_of_a_successful_delete(byte clearedByte, bool expectedFlag)
    {
        Assert.True(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 1, clearedByte }, out CharacterDeleteResult accepted));
        UserInfoNotice list = FakeSessionPort.FakeRoster(2);

        OnlineCharacterManageReport report = RealOnlineSessionPort.ToManageReport(MakeResult(
            OnlineCharacterManageKind.Delete, OnlineCharacterManageOutcomeKind.Succeeded, null, accepted, list,
            OnlineCharacterListStatus.Received));

        Assert.Equal(OnlineCharacterManageKind.Delete, report.Operation);
        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, report.Outcome);
        Assert.Same(list, report.CharacterList);
        Assert.Equal(OnlineCharacterListStatus.Received, report.CharacterListStatus);
        Assert.Equal(expectedFlag, report.EventCharacterFlagCleared);
        Assert.Null(report.RawErrorCode);
    }

    [Fact]
    public void ToManageReport_never_reports_the_cleared_flag_for_a_create()
    {
        Assert.True(CharacterManagePayloads.TryParseCreateResult(new byte[] { 1 }, out CharacterCreateResult accepted));
        UserInfoNotice list = FakeSessionPort.FakeRoster(1);

        OnlineCharacterManageReport report = RealOnlineSessionPort.ToManageReport(MakeResult(
            OnlineCharacterManageKind.Create, OnlineCharacterManageOutcomeKind.Succeeded, accepted, null, list,
            OnlineCharacterListStatus.Received));

        Assert.Equal(OnlineCharacterManageKind.Create, report.Operation);
        Assert.Same(list, report.CharacterList);
        Assert.False(report.EventCharacterFlagCleared);
    }

    // ------------------------------------------------------------------ 会话薄皮:每次调用自己写写开关

    /// <summary>
    /// 创建与删除各自带的 allowWrite 都要在那一次调用里写进会话:每次调用<b>之后立刻</b>读开关,
    /// 前一个调用写的值被后一个覆盖回去的情形(例如创建写了 true、紧接着的删除又写回 false)不能蒙混过关。
    /// 调用本身在"已创建"态都会被会话的状态机拦下(抛 InvalidOperationException),这里只看开关。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_port_call_writes_the_session_write_flag_itself(bool createFirst)
    {
        var rig = new BackendRig();
        using var port = new RealOnlineSessionPort(rig.Template);
        byte[] name = Encoding.UTF8.GetBytes("Hero908");
        Assert.False(port.Real.AllowCharacterManageWrites);

        async Task Create(bool allow) =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => port.CreateCharacterAsync(3, name, 0, allowWrite: allow, default));
        async Task Delete(bool allow) =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => port.DeleteCharacterAsync(1, name, allowWrite: allow, default));

        Func<bool, Task> first = createFirst ? Create : Delete;
        Func<bool, Task> second = createFirst ? Delete : Create;

        await first(false);
        Assert.False(port.Real.AllowCharacterManageWrites);
        await second(false);
        Assert.False(port.Real.AllowCharacterManageWrites);

        await first(true);
        Assert.True(port.Real.AllowCharacterManageWrites);
        await second(true);
        Assert.True(port.Real.AllowCharacterManageWrites);

        // 再各自关回去:每一次调用写的都是它自己带的值
        await first(false);
        Assert.False(port.Real.AllowCharacterManageWrites);
        await second(true);
        Assert.True(port.Real.AllowCharacterManageWrites);
        await second(false);
        Assert.False(port.Real.AllowCharacterManageWrites);
        await first(true);
        Assert.True(port.Real.AllowCharacterManageWrites);
    }
}
