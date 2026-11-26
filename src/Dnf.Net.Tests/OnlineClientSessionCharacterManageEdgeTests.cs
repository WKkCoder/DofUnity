using System.Diagnostics;
using System.Reflection;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网会话"创建/删除角色"在入参上界、窗口回退与链路缺失上的补充测试(r20fix-createcharui,相邻点变异补测)。
/// 与 <see cref="OnlineClientSessionCharacterManageTests"/>(主流程与各结局)、<see cref="OnlineClientSessionCharacterManageFaultTests"/>
/// (写失败、释放与断线边界)互补。全部走内存里的假管道,数据是合成的,不连任何真实服务,也不触发任何真实创建或删除。
/// </summary>
public sealed class OnlineClientSessionCharacterManageEdgeTests
{
    private const string NoReadyChannelMessage = "当前没有就绪的频道会话";

    private static readonly byte[] RefreshedRoster = UserInfoTestData.Packet(UserInfoTestData.Standard(0), UserInfoTestData.Standard(1));

    private static Task<T> Bounded<T>(Task<T> task) => GatewayTestSupport.Bounded(task, 5);

    private static byte[] Name(string text = "Hero900") => Encoding.UTF8.GetBytes(text);

    /// <summary>创建与删除的回复都是"成功 + 紧跟刷新的名册":回复帧与名册帧同一批给出。</summary>
    private static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script()
    {
        return HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            frame =>
            {
                if (frame.Id == 5)
                {
                    return HostTestKit.Frames(DungeonEntryWire.Reply(5, new byte[] { 1 }), HostTestKit.DirectListNotice(RefreshedRoster));
                }
                if (frame.Id == 6)
                {
                    return HostTestKit.Frames(DungeonEntryWire.Reply(6, new byte[] { 1, 0 }), HostTestKit.DirectListNotice(RefreshedRoster));
                }
                return null;
            });
    }

    private static async Task<(OnlineFlowRig Rig, OnlineClientSession Client)> ConnectedWithWrites()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => Script();
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        return (rig, client);
    }

    private static Task<OnlineCharacterManageResult> Manage(OnlineClientSession client, bool create)
    {
        return create ? client.CreateCharacterAsync(3, Name("Hero902")) : client.DeleteCharacterAsync(1, Name("Hero903"));
    }

    // ------------------------------------------------------------------ 创建:职业与活动标记的边界

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)9)]
    [InlineData((byte)10)]
    public async Task Create_sends_every_job_up_to_the_highest_id_with_a_zero_event_flag_by_default(byte job)
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedWithWrites();

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(job, Name("Hero902")));

        Assert.True(res.IsSuccess);
        DungeonEntryClientFrame sent = Assert.Single(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5);
        Assert.True(CharacterManagePayloads.TryParseCreateCharacterRequest(sent.Plain, out CreateCharacterRequest request));
        Assert.Equal(job, request.Job);
        Assert.Equal((byte)0, request.EventFlag);
        Assert.Equal(Name("Hero902"), request.CopyNameBytes());
    }

    [Theory]
    [InlineData((byte)11)]
    [InlineData((byte)12)]
    [InlineData((byte)100)]
    [InlineData((byte)255)]
    public async Task Create_rejects_a_job_above_the_highest_id_without_writing(byte job)
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedWithWrites();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateCharacterAsync(job, Name("Hero902")));

        Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)126)]
    [InlineData((byte)127)]
    public async Task Create_sends_the_given_event_flag_up_to_the_highest_allowed_value(byte flag)
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedWithWrites();

        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(4, Name("Hero902"), flag));

        Assert.True(res.IsSuccess);
        DungeonEntryClientFrame sent = Assert.Single(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5);
        Assert.True(CharacterManagePayloads.TryParseCreateCharacterRequest(sent.Plain, out CreateCharacterRequest request));
        Assert.Equal((byte)4, request.Job);
        Assert.Equal(flag, request.EventFlag);
    }

    [Theory]
    [InlineData((byte)128)]
    [InlineData((byte)200)]
    [InlineData((byte)255)]
    public async Task Create_rejects_an_event_flag_above_the_highest_allowed_value_without_writing(byte flag)
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedWithWrites();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateCharacterAsync(4, Name("Hero902"), flag));

        Assert.DoesNotContain(rig.Channels[0].Pipe.ClientFrames, f => f.Id == 5);
        Assert.Equal(OnlineClientSessionState.Connected, client.State);
    }

    // ------------------------------------------------------------------ 窗口:零表示"没设",退回默认而不是立刻放弃

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_zero_reply_window_means_unset_and_the_reply_is_still_awaited(bool create)
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedWithWrites();
        rig.Hooks.Windows = new OnlineTownWindows(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.Zero, TimeSpan.FromMilliseconds(30));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.Equal(2, res.CharacterList.Entries.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_zero_roster_window_means_unset_and_the_roster_is_still_awaited(bool create)
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedWithWrites();
        rig.Hooks.Windows = new OnlineTownWindows(
            TimeSpan.FromMilliseconds(100), TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(30));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.Received, res.CharacterListStatus);
        Assert.Equal(2, res.CharacterList.Entries.Count);
    }

    // ------------------------------------------------------------------ 链路缺失或没就绪:拒绝、不写任何字节

    /// <summary>
    /// "已连上"态下链路是空的、链路里没有会话、会话还没收到启动包:这是防御性的闸门(正常流程里"已连上"必有就绪链路),
    /// 只能用反射把内部的链路字段换掉来触发。三种坏形状都要得到同一个"没有就绪的频道会话"提示,且不写任何字节、状态不变;
    /// 换回真链路之后一切照常。
    /// </summary>
    [Fact]
    public async Task A_connected_session_without_a_ready_channel_refuses_both_commands_and_writes_nothing()
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedWithWrites();
        FieldInfo linkField = typeof(OnlineClientSession).GetField("_link", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(linkField);
        object realLink = linkField.GetValue(client);
        Assert.NotNull(realLink);
        int framesBefore = rig.Channels[0].Pipe.ClientFrames.Count;
        var notBootstrapped = new GameClientSession(new MemoryStream(), s => new SliceXorCipher(s));
        Assert.False(notBootstrapped.IsReady);

        try
        {
            foreach (object broken in new object[]
            {
                null,
                new OnlineChannelLink(null, null, 0),
                new OnlineChannelLink(notBootstrapped, null, 0),
            })
            {
                linkField.SetValue(client, broken);

                InvalidOperationException create = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.CreateCharacterAsync(3, Name("Hero902")));
                InvalidOperationException delete = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.DeleteCharacterAsync(1, Name("Hero903")));

                Assert.Equal(NoReadyChannelMessage, create.Message);
                Assert.Equal(NoReadyChannelMessage, delete.Message);
                Assert.Equal(OnlineClientSessionState.Connected, client.State);
            }
        }
        finally
        {
            linkField.SetValue(client, realLink);
        }

        Assert.Equal(framesBefore, rig.Channels[0].Pipe.ClientFrames.Count);
        OnlineCharacterManageResult res = await Bounded(client.CreateCharacterAsync(3, Name("Hero902")));
        Assert.True(res.IsSuccess);
    }

    // ------------------------------------------------------------------ 回复与名册的识别:只认类别、包号都对的帧

    /// <summary>创建的回复是类 1 包号 5、删除的是包号 6;成功载荷各自是 {1} 与 {1, 0}。</summary>
    private static byte[] SuccessReply(bool create)
    {
        return create ? DungeonEntryWire.Reply(5, new byte[] { 1 }) : DungeonEntryWire.Reply(6, new byte[] { 1, 0 });
    }

    /// <summary>
    /// 用自己给定的"收到创建 / 删除命令之后回放哪几帧"起一个已连上、已放开写的会话;
    /// 与 <see cref="Script"/> 的差别是回放的帧由每个用例自己决定(夹杂帧、缺名册等)。
    /// </summary>
    private static async Task<(OnlineFlowRig Rig, OnlineClientSession Client)> ConnectedReplaying(Func<bool, byte[][]> framesAfterCommand)
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            _ => HostTestKit.EnterComplete(),
            frame => frame.Id == 5 ? framesAfterCommand(true) : frame.Id == 6 ? framesAfterCommand(false) : null);
        (OnlineClientSession client, _) = await rig.Connected();
        client.AllowCharacterManageWrites = true;
        return (rig, client);
    }

    /// <summary>另一条命令的"拒绝"回复(类 1)先到:创建时夹一条包号 6 的、删除时夹一条包号 5 的。它不是本次请求的回复。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_command_class_frame_with_another_packet_id_is_not_taken_for_the_reply(bool create)
    {
        (_, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(
            isCreate ? DungeonEntryWire.Reply(6, new byte[] { 0, 0x24 }) : DungeonEntryWire.Reply(5, new byte[] { 0, 0x12 }),
            SuccessReply(isCreate),
            HostTestKit.DirectListNotice(RefreshedRoster)));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.Equal(2, res.CharacterList.Entries.Count);
    }

    /// <summary>同一条命令的回复只认第一次:成功回复之后再来一条同包号的拒绝回复(重复帧)被忽略,结局仍按第一条成功、名册照收。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_second_reply_frame_for_the_same_command_is_ignored(bool create)
    {
        (_, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(
            SuccessReply(isCreate),
            isCreate ? DungeonEntryWire.Reply(5, new byte[] { 0, 0x24 }) : DungeonEntryWire.Reply(6, new byte[] { 0, 0x12 }),
            HostTestKit.DirectListNotice(RefreshedRoster)));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.Received, res.CharacterListStatus);
        Assert.Equal(2, res.CharacterList.Entries.Count);
    }

    /// <summary>包号与本次请求相同、但类别是通知(类 0)的帧,同样不是回复。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_notice_frame_with_the_reply_packet_id_is_not_taken_for_the_reply(bool create)
    {
        (_, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(
            DungeonEntryWire.Notice(isCreate ? (ushort)5 : (ushort)6, new byte[] { 0, 0x12 }),
            SuccessReply(isCreate),
            HostTestKit.DirectListNotice(RefreshedRoster)));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.Equal(2, res.CharacterList.Entries.Count);
    }

    /// <summary>
    /// 包号是用户信息的 2、但类别是命令回包(类 1)的帧不是名册:即使载荷是完整的名册,也不被收下。
    /// 包号 2 是压缩包号,解码器不看类别、一律按 zlib 解包,所以载荷要先压缩;否则帧在解码处就被整帧丢掉,到不了名册判定。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_command_class_frame_with_the_user_info_packet_id_is_not_a_roster(bool create)
    {
        (_, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(
            SuccessReply(isCreate),
            DungeonEntryWire.Reply((ushort)UserInfoParser.PacketId, UserInfoTestData.Zlib(RefreshedRoster))));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.RosterMissing, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);
        Assert.Null(res.CharacterList);
    }

    /// <summary>类别是通知、但包号不是用户信息的帧不是名册:它的载荷哪怕是模式 2 开头的残包,也不能把名册状态记成"残缺"。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_notice_with_another_packet_id_is_not_a_roster_even_when_its_body_looks_like_a_truncated_one(bool create)
    {
        (_, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(
            SuccessReply(isCreate),
            DungeonEntryWire.Notice(77, new byte[] { 2 })));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.RosterMissing, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);
    }

    /// <summary>
    /// 用户信息通知的模式字节不是 2(这里是 1):是"不支持的模式",不是"残缺"——忽略它,名册仍记为没收到。
    /// 载荷经 <c>HostTestKit.DirectListNotice</c> 压缩(包号 2 的帧解码器一律按 zlib 解包,未压缩的会在解码处被丢掉),解码后到名册判定的就是这 4 个明文字节。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_roster_in_an_unsupported_mode_is_ignored_and_not_reported_as_malformed(bool create)
    {
        (_, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(
            SuccessReply(isCreate),
            HostTestKit.DirectListNotice(new byte[] { 1, 0, 0, 0 })));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.RosterMissing, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);
    }

    /// <summary>
    /// 名册先到、一个模式 2 的残包随后到、回复最后到:已经收下的名册不被残包降级,结局照常成功。
    /// 残包同样经 <c>HostTestKit.DirectListNotice</c> 压缩,解码后到判定的是单字节 {2}(模式 2、后面的头读不出来)。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_truncated_roster_after_a_good_one_does_not_downgrade_the_received_roster(bool create)
    {
        (_, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(
            HostTestKit.DirectListNotice(RefreshedRoster),
            HostTestKit.DirectListNotice(new byte[] { 2 }),
            SuccessReply(isCreate)));

        OnlineCharacterManageResult res = await Bounded(Manage(client, create));

        Assert.Equal(OnlineCharacterManageOutcomeKind.Succeeded, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.Received, res.CharacterListStatus);
        Assert.Equal(2, res.CharacterList.Entries.Count);
    }

    // ------------------------------------------------------------------ 成功回复之后等名册:按名册窗口,不再按回复窗口

    /// <summary>
    /// 回复已成功但名册迟迟不来:剩下的等待从回复到达那一刻起按"名册窗口"(这里 100 毫秒)重新计,
    /// 而不是沿用更长的"回复窗口"(这里 4 秒)。上界给 3 秒,比实际(约 0.1 秒)宽松得多。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task After_a_successful_reply_a_missing_roster_is_waited_for_by_the_roster_window_not_the_reply_window(bool create)
    {
        (OnlineFlowRig rig, OnlineClientSession client) = await ConnectedReplaying(isCreate => HostTestKit.Frames(SuccessReply(isCreate)));
        rig.Hooks.Windows = new OnlineTownWindows(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(30));

        var watch = Stopwatch.StartNew();
        OnlineCharacterManageResult res = await Bounded(Manage(client, create));
        watch.Stop();

        Assert.Equal(OnlineCharacterManageOutcomeKind.RosterMissing, res.Outcome);
        Assert.Equal(OnlineCharacterListStatus.NotReceived, res.CharacterListStatus);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "waited " + watch.Elapsed.TotalSeconds.ToString("F1") + " s for a roster that never comes");
    }
}
