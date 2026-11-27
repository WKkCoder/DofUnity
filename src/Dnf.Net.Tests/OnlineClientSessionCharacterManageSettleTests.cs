using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网会话"创建/删除角色"在收尾处的补充测试(r21-createcharui2):
/// 释放会话要能取消在途的操作、排队中的操作遇到会话已交给游戏要按"已释放"失败、
/// 名册早于回复到达时回复之后仍只等名册窗口(而不是回复窗口)、被拒与无回复两种结局不带名册。
/// 与 <see cref="OnlineClientSessionCharacterManageFaultTests"/> 同风格:全部走内存里的假管道,数据是合成的(名字、槽号、职业都是 900 起的合成值),
/// 不连任何真实服务,也不触发任何真实创建或删除(写开关只对这条内存会话打开)。
/// 每个等待都有 5 秒上限,不靠墙钟睡眠同步。
/// </summary>
public sealed class OnlineClientSessionCharacterManageSettleTests
{
    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static async Task Bounded(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, finished);
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        await Bounded(Task.Run(async () =>
        {
            while (!condition()) await Task.Delay(5);
            return 0;
        }));
    }

    private static Task<OnlineCharacterManageResult> Manage(OnlineClientSession client, bool create)
    {
        return create
            ? client.CreateCharacterAsync(3, Encoding.UTF8.GetBytes("Hero900"))
            : client.DeleteCharacterAsync(1, Encoding.UTF8.GetBytes("Hero901"));
    }

    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script(
        Func<DungeonEntryClientFrame, IEnumerable<byte[]>> others = null)
    {
        return HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            others);
    }

    /// <summary>回复窗口 30 秒、名册窗口 100 毫秒:任何一处把"该等名册窗口"的等待写成回复窗口,调用就会空转到 5 秒上限。</summary>
    private static OnlineTownWindows LongReplyWindow() => new OnlineTownWindows(
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(30));

    private static bool IsManagePacket(DungeonEntryClientFrame frame) => frame.Id == 5 || frame.Id == 6;

    // ------------------------------------------------------------------ 释放会话:取消在途的操作

    /// <summary>
    /// 请求已写出、读端挂着等回复时释放会话:Dispose 要取消这次操作(调用以取消结束),自己也立刻返回;
    /// 取消令牌没把"会话释放"并进来的话,调用和 Dispose 都要空等到 30 秒的回复窗口结束。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_while_waiting_for_the_reply_cancels_the_operation_instead_of_waiting_out_the_window(bool create)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = LongReplyWindow();
        rig.ScriptFactory = _ => Script();
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        OnlineFlowChannel channel = rig.Channels[0];
        int packetId = create ? 5 : 6;

        Task<OnlineCharacterManageResult> call = Manage(client, create);
        await WaitUntil(() => channel.Pipe.ClientFrames.Any(f => f.Id == packetId) && channel.Pipe.HasPendingRead);

        Task dispose = Task.Run(client.Dispose);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(call));
        await Bounded(dispose);
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
    }

    // ------------------------------------------------------------------ 排队中的操作:拿到门闩时会话已被交给游戏

    /// <summary>
    /// 第一个操作停在等回复上(占着门闩),第二个操作排在门闩后面;这时会话被交给游戏(<c>Detach</c>:状态置为已释放,但不取消任何在途或排队的操作)。
    /// 第一个操作取消结束、放出门闩后,第二个拿到门闩要按"已释放"失败,而不是落到"当前状态不能执行"的普通无效操作异常上,也不能往连接上写任何东西。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_queued_operation_fails_as_disposed_when_the_session_was_handed_over_before_it_got_the_gate(bool create)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = LongReplyWindow();
        rig.ScriptFactory = _ => Script();
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        OnlineFlowChannel channel = rig.Channels[0];

        using var stopFirst = new CancellationTokenSource();
        Task<OnlineCharacterManageResult> first = client.CreateCharacterAsync(3, Encoding.UTF8.GetBytes("Hero900"), 0, stopFirst.Token);
        await WaitUntil(() => channel.Pipe.ClientFrames.Any(f => f.Id == 5) && channel.Pipe.HasPendingRead);

        // 第二个操作的同步前缀已经跑完:它停在等门闩上
        Task<OnlineCharacterManageResult> second = Manage(client, create);
        Assert.False(second.IsCompleted);

        using OnlineEntryResult handedOver = client.Detach(OnlineTownEntry.Fail(
            OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, null, -1, 0, null, null, 0));
        Assert.Equal(OnlineClientSessionState.Disposed, client.State);
        Assert.False(second.IsCompleted);

        stopFirst.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(first));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(second));

        Assert.Single(channel.Pipe.ClientFrames, f => f.Id == 5);
        Assert.DoesNotContain(channel.Pipe.ClientFrames, f => f.Id == 6);
    }

    // ------------------------------------------------------------------ 回复之后等名册:只给名册窗口

    /// <summary>
    /// 成功回复之后名册没有(或到过但是畸形的)时,只再等名册窗口(100 毫秒),不是 30 秒的回复窗口。
    /// 畸形名册先于回复到达的那一种,"名册已收到"的判断要看名册是不是<b>读得通</b>,不能把"畸形"也当成收到。
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task After_a_successful_reply_only_the_short_roster_window_is_waited_for_a_roster_that_is_missing_or_malformed(
        bool create, bool malformedRosterFirst)
    {
        var rig = new OnlineFlowRig();
        rig.Hooks.Windows = LongReplyWindow();
        rig.ScriptFactory = _ => Script(frame =>
        {
            if (!IsManagePacket(frame))
            {
                return null;
            }
            byte[] reply = DungeonEntryWire.Reply(frame.Id, create ? new byte[] { 1 } : new byte[] { 1, 0 });
            return malformedRosterFirst
                ? HostTestKit.Frames(HostTestKit.DirectListNotice(new byte[] { 2, 1 }), reply)
                : HostTestKit.Frames(reply);
        });
        (OnlineClientSession client, _) = await rig.Connected(dispatcher: new OnlineInlineDispatcher());
        client.AllowCharacterManageWrites = true;
        UserInfoNotice before = client.CharacterList;

        // 放到线程池上跑:被改坏时它在调用线程里同步空转,不放出去的话上限也计不了时
        OnlineCharacterManageResult res = await Bounded(Task.Run(() => Manage(client, create)));

        Assert.Equal(create ? OnlineCharacterManageKind.Create : OnlineCharacterManageKind.Delete, res.Operation);
        Assert.Equal(
            malformedRosterFirst ? OnlineCharacterManageOutcomeKind.RosterMalformed : OnlineCharacterManageOutcomeKind.RosterMissing,
            res.Outcome);
        Assert.Equal(
            malformedRosterFirst ? OnlineCharacterListStatus.Malformed : OnlineCharacterListStatus.NotReceived,
            res.CharacterListStatus);
        Assert.True(res.CommandSucceeded);
        Assert.False(res.IsSuccess);
        Assert.Null(res.CharacterList);
        Assert.Same(before, client.CharacterList);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    // ------------------------------------------------------------------ 被拒与无回复:名册状态一律是"没收到"

    /// <summary>被服务端拒绝的命令,哪怕有一份读得通的名册先到了,结局里的名册状态仍是"没收到"、名册为空,会话缓存的名册也不动。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rejected_command_reports_no_roster_even_when_a_valid_roster_arrived_first(bool create)
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(frame => IsManagePacket(frame)
            ? HostTestKit.Frames(HostTestKit.DirectListNotice(), DungeonEntryWire.Reply(frame.Id, new byte[] { 0, 0x02 }))
            : null);
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        UserInfoNotice before = client.CharacterList;

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Rejected, res.Outcome);
        Assert.False(res.CommandSucceeded);
        Assert.False(res.IsSuccess);
        Assert.Equal((byte?)0x02, res.RawFailureCode);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);
        Assert.Null(res.CharacterList);
        Assert.Same(before, client.CharacterList);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    /// <summary>一直没有回复的命令,哪怕有一份读得通的名册先到了,结局也是"无回复"、名册状态是"没收到"、没有解析出的回复。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_reply_reports_no_roster_even_when_a_valid_roster_arrived_first(bool create)
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script(frame => IsManagePacket(frame)
            ? HostTestKit.Frames(HostTestKit.DirectListNotice())
            : null);
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        UserInfoNotice before = client.CharacterList;

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.NoReply, res.Outcome);
        Assert.False(res.CommandSucceeded);
        Assert.False(res.IsSuccess);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);
        Assert.Null(res.CreateResult);
        Assert.Null(res.DeleteResult);
        Assert.Null(res.CharacterList);
        Assert.Null(res.RawFailureCode);
        Assert.Same(before, client.CharacterList);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }
}
